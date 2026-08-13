using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ExileCore.Shared.Enums;
using Color = SharpDX.Color;
using RectangleF = SharpDX.RectangleF;
using Vector2 = System.Numerics.Vector2;

namespace MapLens;

public partial class MapLens : BaseSettingsPlugin<Settings>
{
    private const double DpsWindowSeconds = 4.0;
    private const int MaxHistoryEntries = 10;
    private const string SummaryOnlyMode = "Hideout Summary Only";
    private const string CompactOnlyMode = "Compact HUD Only";

    private static readonly long[] ExperienceToNextLevel =
    {
        0L, 525L, 1235L, 2021L, 3403L, 5002L, 7138L, 10053L, 13804L, 18512L,
        24297L, 31516L, 39878L, 50352L, 62261L, 76465L, 92806L, 112027L,
        133876L, 158538L, 187025L, 218895L, 255366L, 295852L, 341805L,
        392470L, 449555L, 512121L, 583857L, 662181L, 747411L, 844146L,
        949053L, 1064952L, 1192712L, 1333241L, 1487491L, 1656447L,
        1841143L, 2046202L, 2265837L, 2508528L, 2776124L, 3061734L,
        3379914L, 3723676L, 4099570L, 4504444L, 4951099L, 5430907L,
        5957868L, 6528910L, 7153414L, 7827968L, 8555414L, 9353933L,
        10212541L, 11142646L, 12157041L, 13252160L, 14441758L, 15731508L,
        17127265L, 18635053L, 20271765L, 22044909L, 23950783L, 26019833L,
        28261412L, 30672515L, 33287878L, 36118904L, 39163425L, 42460810L,
        46024718L, 49853964L, 54008554L, 58473753L, 63314495L, 68516464L,
        74132190L, 80182477L, 86725730L, 93748717L, 101352108L, 109524907L,
        118335069L, 127813148L, 138033822L, 149032822L, 160890604L,
        173648795L, 187372170L, 202153736L, 218041909L, 235163399L,
        253547862L, 273358532L, 294631836L, 317515914L, 0L
    };

    private readonly Dictionary<long, long> _monsterLife = new();
    private readonly Queue<DamageSample> _damageWindow = new();
    private readonly HashSet<long> _bossIds = new();
    private readonly HashSet<long> _deadBossIds = new();
    private readonly HashSet<uint> _activeRunAreaHashes = new();
    private readonly List<RunSnapshot> _history = new();

    private RunSnapshot _activeRun;
    private DateTime _lastTickUtc;
    private DateTime _nextCombatUpdateUtc;
    private DateTime _damageWarmupUntilUtc;
    private DateTime _combatActiveUntilUtc;
    private DateTime _nextMapStatsUpdateUtc;
    private DateTime _summaryVisibleUntilUtc;
    private double _rollingDamage;
    private long _previousPlayerLife;
    private bool _hasPlayerLifeBaseline;
    private long _previousGold;
    private bool _hasGoldBaseline;
    private bool _playerWasAlive;
    private bool _currentAreaIsActiveMap;
    private uint _lastObservedAreaHash;
    private RunSnapshot _summaryRun;

    private readonly struct DamageSample
    {
        public DamageSample(DateTime time, double damage)
        {
            Time = time;
            Damage = damage;
        }

        public DateTime Time { get; }
        public double Damage { get; }
    }

    private sealed class HudMetric
    {
        public string Label;
        public string Value;
        public string Detail;
        public Color ValueColor;
    }

    public MapLens()
    {
        Name = "MapLens";
        Description = "A clean live HUD and summary for Path of Exile map runs.";
        Order = -15;
    }

    public override bool Initialise()
    {
        ApplySettingsMigrations();
        _lastTickUtc = DateTime.UtcNow;
        _nextCombatUpdateUtc = DateTime.MinValue;
        TrySynchronizeArea(GameController?.Area?.CurrentArea);
        return true;
    }

    private void ApplySettingsMigrations()
    {
        if (Settings.LayoutVersion.Value < 2)
        {
            // V1 stored a tall card-layout width, scale, and opaque colors.
            Settings.PanelWidth.Value = 580;
            Settings.UiScale.Value = 100;
            Settings.BackgroundColor.Value = new Color(5, 6, 8, 218);
            Settings.BorderColor.Value = new Color(74, 67, 54, 175);
            Settings.LayoutVersion.Value = 2;
        }

        if (Settings.LayoutVersion.Value < 3)
        {
            Settings.DisplayMode.Value = SummaryOnlyMode;
            Settings.ShowGold.Value = true;
            Settings.SummaryX.Value = 36;
            Settings.SummaryY.Value = 80;
            Settings.SummaryWidth.Value = 760;
            Settings.SummaryUiScale.Value = 100;
            Settings.SummaryDurationSeconds.Value = 30;
            Settings.LayoutVersion.Value = 3;
        }

        if (Settings.LayoutVersion.Value < 4)
        {
            // V4 makes the hideout summary a narrow left-side vertical panel.
            Settings.DisplayMode.Value = SummaryOnlyMode;
            Settings.SummaryX.Value = 20;
            Settings.SummaryY.Value = 100;
            Settings.SummaryWidth.Value = 340;
            Settings.SummaryUiScale.Value = 100;
            Settings.LayoutVersion.Value = 4;
        }
    }

    public override void AreaChange(AreaInstance area)
    {
        if (!Settings.Enable.Value)
            return;

        TrySynchronizeArea(area);
    }

    public override void EntityRemoved(Entity entity)
    {
        if (_activeRun == null || !_currentAreaIsActiveMap || entity == null ||
            !_bossIds.Contains(entity.Id))
            return;

        try
        {
            var life = entity.GetComponent<Life>();
            if (life?.CurHP <= 0 || !entity.IsAlive)
                MarkBossDefeated(entity.Id);
        }
        catch
        {
            // A removed entity can become unreadable immediately. Normal
            // combat sampling remains the primary boss-death path.
        }
    }

    public override Job Tick()
    {
        if (!Initialized || !Settings.Enable.Value)
            return null;

        try
        {
            var area = GameController?.Area?.CurrentArea;
            if (area == null)
                return null;

            if (_lastObservedAreaHash != area.Hash)
            {
                TrySynchronizeArea(area);
                area = GameController?.Area?.CurrentArea;
                if (area == null)
                    return null;
            }

            if (IsMapArea(area) &&
                (_activeRun == null || !_activeRunAreaHashes.Contains(area.Hash)))
                TrySynchronizeArea(area);

            var now = DateTime.UtcNow;
            var delta = (now - _lastTickUtc).TotalSeconds;
            _lastTickUtc = now;

            // Do not count time spent while the game or overlay was suspended.
            if (delta < 0 || delta > 5)
                delta = 0;

            _currentAreaIsActiveMap = IsCurrentActiveMap(area);
            if (_activeRun == null)
                return null;

            if (_currentAreaIsActiveMap)
            {
                _activeRun.ActiveSeconds += delta;
                UpdatePlayerMetrics();
                DetectDeath();

                if (now >= _nextCombatUpdateUtc)
                {
                    _nextCombatUpdateUtc = now.AddMilliseconds(Settings.UpdateIntervalMs.Value);
                    UpdateCombatMetrics(now);
                }

                if (now <= _combatActiveUntilUtc)
                    _activeRun.CombatSeconds += delta;
            }
            else
            {
                _activeRun.CurrentDps = 0;
                TrimDamageWindow(now);
            }
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[MapLens] Tick error: {ex.Message}");
        }

        return null;
    }

    public override void Render()
    {
        if (!Settings.Enable.Value)
            return;

        try
        {
            var mode = Settings.DisplayMode.Value;
            var area = GameController?.Area?.CurrentArea;

            if (mode != CompactOnlyMode && area?.IsHideout == true &&
                _summaryRun != null && DateTime.UtcNow < _summaryVisibleUntilUtc)
            {
                DrawSummaryPanel(_summaryRun, DateTime.UtcNow);
            }

            if (mode != SummaryOnlyMode && _currentAreaIsActiveMap && _activeRun != null)
                DrawRunPanel(_activeRun, true);
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[MapLens] Render error: {ex.Message}");
        }
    }

    private void TrySynchronizeArea(AreaInstance area)
    {
        _lastTickUtc = DateTime.UtcNow;
        _nextCombatUpdateUtc = DateTime.MinValue;

        if (area == null)
            return;

        var cameFromTrackedMap = _activeRun != null &&
                                 (_currentAreaIsActiveMap ||
                                  _activeRunAreaHashes.Contains(_lastObservedAreaHash));
        var wasActiveMap = cameFromTrackedMap;
        if (wasActiveMap && area.IsHideout)
        {
            UpdatePlayerMetrics();
            ShowHideoutSummary();
        }

        var isMap = IsMapArea(area);
        _lastObservedAreaHash = area.Hash;
        _currentAreaIsActiveMap = isMap && _activeRun != null &&
                                  _activeRunAreaHashes.Contains(area.Hash);

        if (!isMap)
        {
            ClearDamageCache();
            if (_activeRun != null)
                _activeRun.CurrentDps = 0;
            return;
        }

        _summaryVisibleUntilUtc = DateTime.MinValue;

        if (_activeRun != null && _activeRunAreaHashes.Contains(area.Hash))
        {
            // Moving between tracked map rooms is internal travel. Only count
            // another portal when returning from an outside area such as hideout.
            if (!cameFromTrackedMap)
                _activeRun.Entries++;

            _currentAreaIsActiveMap = true;
            ClearDamageCache();
            _playerWasAlive = IsPlayerAlive();
            return;
        }

        if (_activeRun != null && cameFromTrackedMap)
        {
            // Some map-boss arenas use their own area hash. Keep that arena in
            // the same run rather than archiving the map and starting over.
            _activeRunAreaHashes.Add(area.Hash);
            _currentAreaIsActiveMap = true;
            ClearDamageCache();
            _playerWasAlive = IsPlayerAlive();
            return;
        }

        var player = GameController?.Player?.GetComponent<Player>();
        if (player == null)
            return;

        if (_activeRun != null)
            ArchiveActiveRun();

        _activeRun = new RunSnapshot
        {
            AreaHash = area.Hash,
            AreaName = string.IsNullOrWhiteSpace(area.DisplayName) ? area.Name : area.DisplayName,
            AreaLevel = area.RealLevel,
            MapTier = Math.Max(1, area.RealLevel - 67),
            StartedAt = DateTime.Now,
            StartExperience = player.XP,
            StartLevel = player.Level,
            StartKills = GetCharacterKillCount(),
            Entries = 1
        };

        _activeRunAreaHashes.Clear();
        _activeRunAreaHashes.Add(area.Hash);
        _currentAreaIsActiveMap = true;
        _playerWasAlive = IsPlayerAlive();
        ClearRunCombatCache();
        UpdatePlayerMetrics();
    }

    private static bool IsMapArea(AreaInstance area)
    {
        // ExileAPI does not expose a stable IsMap flag across all supported
        // builds. Endgame maps are non-peaceful, non-town areas at level 68+.
        return area != null && !area.IsTown && !area.IsHideout &&
               !area.IsPeaceful && area.RealLevel >= 68;
    }

    private bool IsCurrentActiveMap(AreaInstance area)
    {
        return _activeRun != null && IsMapArea(area) &&
               _activeRunAreaHashes.Contains(area.Hash);
    }

    private void ShowHideoutSummary()
    {
        if (_activeRun == null)
            return;

        if (!_activeRun.HasMapStats)
            TryCaptureMapStats();
        _summaryRun = _activeRun.Copy();
        _summaryRun.CurrentDps = 0;
        _summaryVisibleUntilUtc = DateTime.UtcNow.AddSeconds(
            Settings.SummaryDurationSeconds.Value);
    }

    private void ArchiveActiveRun()
    {
        if (_activeRun == null)
            return;

        UpdatePlayerMetrics();
        var archived = _activeRun.Copy();
        archived.FinishedAt = DateTime.Now;
        archived.CurrentDps = 0;
        _history.Insert(0, archived);

        if (_history.Count > MaxHistoryEntries)
            _history.RemoveRange(MaxHistoryEntries, _history.Count - MaxHistoryEntries);
    }

    private void ResetCurrentRun()
    {
        var area = GameController?.Area?.CurrentArea;
        if (!IsMapArea(area))
            return;

        var player = GameController?.Player?.GetComponent<Player>();
        if (player == null)
            return;

        _activeRun = new RunSnapshot
        {
            AreaHash = area.Hash,
            AreaName = string.IsNullOrWhiteSpace(area.DisplayName) ? area.Name : area.DisplayName,
            AreaLevel = area.RealLevel,
            MapTier = Math.Max(1, area.RealLevel - 67),
            StartedAt = DateTime.Now,
            StartExperience = player.XP,
            StartLevel = player.Level,
            StartKills = GetCharacterKillCount(),
            Entries = 1
        };

        _activeRunAreaHashes.Clear();
        _activeRunAreaHashes.Add(area.Hash);
        _currentAreaIsActiveMap = true;
        _lastTickUtc = DateTime.UtcNow;
        _playerWasAlive = IsPlayerAlive();
        ClearRunCombatCache();
    }

    private void UpdatePlayerMetrics()
    {
        if (_activeRun == null)
            return;

        var player = GameController?.Player?.GetComponent<Player>();
        if (player == null)
            return;

        _activeRun.ExperienceGained = Math.Max(0, player.XP - _activeRun.StartExperience);
        _activeRun.Kills = Math.Max(0, GetCharacterKillCount() - _activeRun.StartKills);

        if (_currentAreaIsActiveMap)
        {
            UpdateGoldGained();
            if (!_activeRun.HasMapStats && DateTime.UtcNow >= _nextMapStatsUpdateUtc)
            {
                _nextMapStatsUpdateUtc = DateTime.UtcNow.AddSeconds(1);
                TryCaptureMapStats();
            }
        }
    }

    private void TryCaptureMapStats()
    {
        if (_activeRun == null)
            return;

        try
        {
            var data = GameController?.IngameState?.Data;
            if (data == null)
                return;

            var dataType = data.GetType();
            object rawStats = dataType.GetProperty("MapStats",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(data);

            if (rawStats == null)
            {
                var method = dataType.GetMethod("GetMapStats",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                rawStats = method?.Invoke(data, null);
            }

            if (rawStats is not IEnumerable entries)
                return;

            var readAnyEntry = false;
            foreach (var entry in entries)
            {
                object key;
                object value;

                if (entry is DictionaryEntry dictionaryEntry)
                {
                    key = dictionaryEntry.Key;
                    value = dictionaryEntry.Value;
                }
                else
                {
                    var entryType = entry?.GetType();
                    key = entryType?.GetProperty("Key")?.GetValue(entry);
                    value = entryType?.GetProperty("Value")?.GetValue(entry);
                }

                if (key == null || value == null)
                    continue;

                var statName = key.ToString();
                var statValue = Convert.ToInt32(value);

                switch (statName)
                {
                    case "MonsterDroppedItemQuantityPct":
                    case "MapItemQuantityPct":
                    case "MapItemDropQuantityPct":
                        _activeRun.MapQuantity = statValue;
                        readAnyEntry = true;
                        break;
                    case "MonsterDroppedItemRarityPct":
                    case "MapItemRarityPct":
                        _activeRun.MapRarity = statValue;
                        readAnyEntry = true;
                        break;
                    case "MapPackSizePct":
                        _activeRun.MapPackSize = statValue;
                        readAnyEntry = true;
                        break;
                }
            }

            _activeRun.HasMapStats = readAnyEntry;
        }
        catch
        {
            // Map-stat memory differs between ExileAPI builds. The summary
            // remains fully usable and simply omits this optional footer.
        }
    }

    private void UpdateGoldGained()
    {
        if (_activeRun == null)
            return;

        long currentGold;
        try
        {
            currentGold = GameController.IngameState.ServerData.Gold;
        }
        catch
        {
            _hasGoldBaseline = false;
            return;
        }

        if (_hasGoldBaseline && currentGold > _previousGold)
            _activeRun.GoldGained += currentGold - _previousGold;

        _previousGold = currentGold;
        _hasGoldBaseline = true;
    }

    private int GetCharacterKillCount()
    {
        return GameController?.Player?.Stats?.GetValueOrDefault(GameStat.CharacterKillCount, 0) ?? 0;
    }

    private bool IsPlayerAlive()
    {
        var life = GameController?.Player?.GetComponent<Life>();
        return life != null && life.CurHP > 0;
    }

    private void DetectDeath()
    {
        if (_activeRun == null)
            return;

        var alive = IsPlayerAlive();
        if (_playerWasAlive && !alive)
            _activeRun.Deaths++;

        _playerWasAlive = alive;
    }

    private void UpdateCombatMetrics(DateTime now)
    {
        if (_activeRun == null)
            return;

        var acceptDamage = now >= _damageWarmupUntilUtc;
        var incomingDamage = UpdatePlayerDamageMetrics(acceptDamage);
        double sampleDamage = 0;

        foreach (var entity in GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Monster])
        {
            if (entity == null || !entity.IsHostile)
                continue;

            var life = entity.GetComponent<Life>();
            if (life == null)
                continue;

            var currentLife = Math.Max(0L, (long)life.CurHP + life.CurES);
            var entityId = entity.Id;

            double observedDamage = 0;
            if (_monsterLife.TryGetValue(entityId, out var previousLife) && currentLife < previousLife)
            {
                var damage = previousLife - currentLife;
                var maximumLife = Math.Max(0L, (long)life.MaxHP + life.MaxES);
                if (maximumLife > 0)
                    damage = Math.Min(damage, maximumLife);

                if (acceptDamage && damage > 0)
                {
                    observedDamage = damage;
                    sampleDamage += damage;
                    _activeRun.TotalDamage += damage;
                    // Keep burst isolated to one monster. Total damage and DPS
                    // still combine all observed monster damage in this update.
                    _activeRun.MaxMonsterBurst = Math.Max(
                        _activeRun.MaxMonsterBurst, damage);
                }
            }

            _monsterLife[entityId] = currentLife;
            UpdateBossState(entity, observedDamage > 0, currentLife <= 0);
        }

        if (sampleDamage > 0)
        {
            _damageWindow.Enqueue(new DamageSample(now, sampleDamage));
            _rollingDamage += sampleDamage;
        }

        if (sampleDamage > 0 || incomingDamage > 0)
            _combatActiveUntilUtc = now.AddSeconds(DpsWindowSeconds);

        TrimDamageWindow(now);

        if (_damageWindow.Count == 0)
        {
            _activeRun.CurrentDps = 0;
            return;
        }

        var oldest = _damageWindow.Peek().Time;
        var measuredSeconds = Math.Clamp((now - oldest).TotalSeconds, 1.0, DpsWindowSeconds);
        _activeRun.CurrentDps = _rollingDamage / measuredSeconds;
        _activeRun.PeakDps = Math.Max(_activeRun.PeakDps, _activeRun.CurrentDps);
    }

    private double UpdatePlayerDamageMetrics(bool acceptDamage)
    {
        var life = GameController?.Player?.GetComponent<Life>();
        if (life == null || _activeRun == null)
        {
            _hasPlayerLifeBaseline = false;
            return 0;
        }

        var currentLife = Math.Max(0L, (long)life.CurHP + life.CurES);
        var maximumLife = Math.Max(1L, (long)life.MaxHP + life.MaxES);

        if (acceptDamage)
        {
            var currentPercent = Math.Clamp(currentLife * 100d / maximumLife, 0d, 100d);
            _activeRun.LowestLifePercent = Math.Min(_activeRun.LowestLifePercent, currentPercent);
        }

        double damageTaken = 0;
        if (acceptDamage && _hasPlayerLifeBaseline && currentLife < _previousPlayerLife)
        {
            damageTaken = Math.Min(_previousPlayerLife - currentLife, maximumLife);
            _activeRun.DamageTaken += damageTaken;
            _activeRun.MaxDamageTaken = Math.Max(_activeRun.MaxDamageTaken, damageTaken);
        }

        _previousPlayerLife = currentLife;
        _hasPlayerLifeBaseline = true;
        return damageTaken;
    }

    private void TrimDamageWindow(DateTime now)
    {
        while (_damageWindow.Count > 0 &&
               (now - _damageWindow.Peek().Time).TotalSeconds > DpsWindowSeconds)
        {
            _rollingDamage -= _damageWindow.Dequeue().Damage;
        }

        if (_rollingDamage < 0)
            _rollingDamage = 0;

        if (_activeRun != null && _damageWindow.Count == 0)
            _activeRun.CurrentDps = 0;
    }

    private void UpdateBossState(Entity entity, bool tookDamage, bool isDead)
    {
        if (_activeRun == null || !IsMapBossEntity(entity))
            return;

        _bossIds.Add(entity.Id);
        if ((tookDamage || isDead) && _activeRun.BossEncounterStartedAt < 0)
            _activeRun.BossEncounterStartedAt = _activeRun.ActiveSeconds;

        if (isDead)
            MarkBossDefeated(entity.Id);

        _activeRun.BossesSeen = Math.Max(_activeRun.BossesSeen, _bossIds.Count);
        _activeRun.BossesDefeated = Math.Max(_activeRun.BossesDefeated, _deadBossIds.Count);
    }

    private static bool IsMapBossEntity(Entity entity)
    {
        if (entity == null || entity.Rarity != MonsterRarity.Unique || !entity.IsHostile)
            return false;

        var stats = entity.Stats;
        if (stats != null)
        {
            if (stats.ContainsKey(GameStat.IsMapBossUnderlingMonster))
                return false;

            // Boolean monster stats are sometimes present with a zero value,
            // so key presence is more reliable than checking for > 0.
            if (stats.ContainsKey(GameStat.MonsterUsesMapBossDifficultyScaling) ||
                stats.ContainsKey(GameStat.MapBossMaximumLifePct) ||
                stats.ContainsKey(GameStat.MapBossDamagePct) ||
                stats.ContainsKey(GameStat.MapBossAttackAndCastSpeedPct))
                return true;
        }

        var modifiers = entity.GetComponent<ObjectMagicProperties>()?.Mods;
        if (modifiers != null)
        {
            foreach (var modifier in modifiers)
            {
                if (!string.IsNullOrEmpty(modifier) &&
                    modifier.IndexOf("MapBoss", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    modifier.IndexOf("Underling", StringComparison.OrdinalIgnoreCase) < 0)
                    return true;
            }
        }

        var path = entity.Path ?? entity.Metadata ?? string.Empty;
        return path.IndexOf("MapBoss", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void MarkBossDefeated(long entityId)
    {
        if (_activeRun == null)
            return;

        _deadBossIds.Add(entityId);
        if (_activeRun.BossEncounterStartedAt < 0)
            _activeRun.BossEncounterStartedAt = _activeRun.ActiveSeconds;

        _activeRun.BossKillSeconds = Math.Max(_activeRun.BossKillSeconds,
            _activeRun.ActiveSeconds - _activeRun.BossEncounterStartedAt);
        _activeRun.BossesSeen = Math.Max(_activeRun.BossesSeen, _bossIds.Count);
        _activeRun.BossesDefeated = Math.Max(_activeRun.BossesDefeated, _deadBossIds.Count);
    }

    private void ClearDamageCache()
    {
        _monsterLife.Clear();
        _damageWindow.Clear();
        _rollingDamage = 0;
        _damageWarmupUntilUtc = DateTime.UtcNow.AddMilliseconds(Settings.CombatWarmupMs.Value);
        _combatActiveUntilUtc = DateTime.MinValue;
        _hasPlayerLifeBaseline = false;
        _hasGoldBaseline = false;

        if (_activeRun != null)
            _activeRun.CurrentDps = 0;
    }

    private void ClearRunCombatCache()
    {
        ClearDamageCache();
        _bossIds.Clear();
        _deadBossIds.Clear();
        _nextMapStatsUpdateUtc = DateTime.MinValue;
    }

    private void DrawSummaryPanel(RunSnapshot run, DateTime now)
    {
        var scale = Settings.SummaryUiScale.Value / 100f;
        var panelWidth = Math.Max(300, Settings.SummaryWidth.Value) * scale;
        var padding = 14f * scale;
        var headerHeight = 88f * scale;
        var metricHeight = 55f * scale;
        var footerHeight = 62f * scale;
        var metrics = BuildSummaryMetrics(run);
        var panelHeight = headerHeight + metrics.Count * metricHeight + footerHeight;
        var x = Settings.SummaryX.Value;
        var y = Settings.SummaryY.Value;
        var accent = Settings.AccentColor.Value;
        var background = Settings.BackgroundColor.Value;
        var border = Settings.BorderColor.Value;
        var normal = new Color(239, 241, 246, 255);
        var muted = new Color(145, 151, 164, 255);

        Graphics.DrawBox(new RectangleF(x, y, panelWidth, panelHeight), border, 8f * scale);
        Graphics.DrawBox(new RectangleF(x + 1f, y + 1f, panelWidth - 2f, panelHeight - 2f),
            background, 7f * scale);
        Graphics.DrawBox(new RectangleF(x + 1f, y + 1f, 4f * scale, panelHeight - 2f),
            accent, 6f * scale);

        var contentX = x + padding;
        var contentWidth = panelWidth - padding * 2f;
        var labelY = y + 10f * scale;
        Graphics.DrawText("MAP SUMMARY", new Vector2(contentX, labelY), accent);

        var mapY = y + 31f * scale;
        var mapName = TrimToWidth(run.AreaName, contentWidth - 58f * scale);
        Graphics.DrawText(mapName, new Vector2(contentX, mapY), normal);

        var tierText = $"T{run.MapTier}";
        var tierSize = Graphics.MeasureText(tierText);
        var tierX = x + panelWidth - padding - tierSize.X - 12f * scale;
        Graphics.DrawBox(new RectangleF(tierX, mapY - 1f * scale,
            tierSize.X + 12f * scale, 22f * scale), new Color(54, 46, 31, 245), 4f * scale);
        Graphics.DrawText(tierText, new Vector2(tierX + 6f * scale, mapY), accent);

        var timer = FormatDuration(run.ActiveSeconds);
        var timerSize = Graphics.MeasureText(timer);
        Graphics.DrawText(timer,
            new Vector2(x + panelWidth - padding - timerSize.X, y + 57f * scale), normal);

        var statusY = y + 57f * scale;
        Graphics.DrawText("RETURNED TO HIDEOUT", new Vector2(contentX, statusY), muted);

        var metricsTop = y + headerHeight;
        Graphics.DrawBox(new RectangleF(contentX, metricsTop, contentWidth, 1f),
            new Color(62, 64, 70, 145));

        for (var i = 0; i < metrics.Count; i++)
        {
            var metricY = metricsTop + i * metricHeight;
            if (i > 0)
                Graphics.DrawBox(new RectangleF(contentX, metricY, contentWidth, 1f),
                    new Color(55, 58, 64, 120));

            DrawSummaryMetric(metrics[i],
                new RectangleF(contentX, metricY, contentWidth, metricHeight), scale);
        }

        var footerY = metricsTop + metricHeight * metrics.Count;
        Graphics.DrawBox(new RectangleF(contentX, footerY, contentWidth, 1f),
            new Color(62, 64, 70, 145));

        var combatUptime = run.ActiveSeconds > 0
            ? Math.Clamp(run.CombatSeconds * 100d / run.ActiveSeconds, 0d, 100d)
            : 0;
        var hasUsefulMapStats = run.HasMapStats &&
                                (run.MapQuantity != 0 || run.MapRarity != 0 || run.MapPackSize != 0);
        var contextText = hasUsefulMapStats
            ? $"QUANTITY {run.MapQuantity}%  ·  RARITY {run.MapRarity}%"
            : $"COMBAT UPTIME {combatUptime:0}%";
        Graphics.DrawText(TrimToWidth(contextText, contentWidth),
            new Vector2(contentX, footerY + 10f * scale), hasUsefulMapStats ? accent : muted);

        var footerSecondLine = hasUsefulMapStats
            ? $"PACK SIZE {run.MapPackSize}%"
            : "RUN SUMMARY";
        Graphics.DrawText(footerSecondLine,
            new Vector2(contentX, footerY + 33f * scale), muted);

        if (Settings.ShowGold.Value)
        {
            var goldText = $"GOLD +{FormatNumber(run.GoldGained)}";
            var goldSize = Graphics.MeasureText(goldText);
            Graphics.DrawText(goldText,
                new Vector2(x + panelWidth - padding - goldSize.X, footerY + 33f * scale), accent);
        }

        var duration = Math.Max(1, Settings.SummaryDurationSeconds.Value);
        var progress = Math.Clamp((_summaryVisibleUntilUtc - now).TotalSeconds / duration, 0d, 1d);
        Graphics.DrawBox(new RectangleF(x + 2f, y + panelHeight - 3f * scale,
            (panelWidth - 4f) * (float)progress, 2f * scale), accent);
    }

    private List<HudMetric> BuildSummaryMetrics(RunSnapshot run)
    {
        var metrics = new List<HudMetric>(8);
        var accent = Settings.AccentColor.Value;
        var normal = new Color(239, 241, 246, 255);
        var good = new Color(91, 204, 104, 255);
        var warning = new Color(245, 180, 65, 255);
        var killsPerMinute = run.ActiveSeconds > 0
            ? run.Kills / (run.ActiveSeconds / 60d)
            : 0;
        var xpPerHour = run.ActiveSeconds > 0
            ? run.ExperienceGained / (run.ActiveSeconds / 3600d)
            : 0;
        var xpPercent = GetExperienceGainPercent(run.StartLevel, run.ExperienceGained);
        var combatUptime = run.ActiveSeconds > 0
            ? Math.Clamp(run.CombatSeconds * 100d / run.ActiveSeconds, 0d, 100d)
            : 0;
        var averageDps = run.CombatSeconds > 0 ? run.TotalDamage / run.CombatSeconds : 0;
        var portalsRemaining = Math.Max(0, Settings.MaximumPortals.Value - run.Entries);

        metrics.Add(new HudMetric
        {
            Label = "KILLS",
            Value = run.Kills.ToString("N0"),
            Detail = $"{killsPerMinute:0.0} / MIN",
            ValueColor = normal
        });
        metrics.Add(new HudMetric
        {
            Label = "XP GAINED",
            Value = $"+{FormatNumber(run.ExperienceGained)}  ·  +{xpPercent:0.00}%",
            Detail = $"{FormatNumber(xpPerHour)} / HR",
            ValueColor = good
        });
        metrics.Add(new HudMetric
        {
            Label = "MAP TIME",
            Value = FormatDuration(run.ActiveSeconds),
            Detail = $"COMBAT {FormatDuration(run.CombatSeconds)}  ·  {combatUptime:0}%",
            ValueColor = normal
        });
        metrics.Add(new HudMetric
        {
            Label = "PORTALS",
            Value = $"{portalsRemaining} LEFT",
            Detail = $"{run.Entries} USED  ·  {run.Deaths} DEATHS",
            ValueColor = portalsRemaining <= 1 ? warning : normal
        });
        metrics.Add(new HudMetric
        {
            Label = "DAMAGE DEALT",
            Value = FormatNumber(run.TotalDamage),
            Detail = $"AVG {FormatNumber(averageDps)}  ·  PEAK {FormatNumber(run.PeakDps)}",
            ValueColor = normal
        });
        metrics.Add(new HudMetric
        {
            Label = "DAMAGE TAKEN",
            Value = FormatNumber(run.DamageTaken),
            Detail = $"MAX HIT {FormatNumber(run.MaxDamageTaken)}",
            ValueColor = run.LowestLifePercent <= 35 ? warning : normal
        });
        metrics.Add(new HudMetric
        {
            Label = "LOWEST LIFE",
            Value = $"{run.LowestLifePercent:0}%",
            Detail = run.LowestLifePercent <= 35 ? "CLOSE CALL" : "LIFE + ENERGY SHIELD",
            ValueColor = run.LowestLifePercent <= 35 ? warning : normal
        });

        string bossValue;
        string bossDetail;
        Color bossColor;
        if (run.BossesSeen == 0)
        {
            bossValue = "NOT SEEN";
            bossDetail = "MAP BOSS";
            bossColor = warning;
        }
        else if (run.BossesDefeated >= run.BossesSeen)
        {
            bossValue = "DEFEATED";
            bossDetail = run.BossKillSeconds > 0
                ? $"{FormatDuration(run.BossKillSeconds)} FIGHT"
                : "MAP BOSS";
            bossColor = good;
        }
        else
        {
            bossValue = "ENGAGED";
            bossDetail = $"{run.BossesDefeated}/{run.BossesSeen} DOWN";
            bossColor = accent;
        }

        metrics.Add(new HudMetric
        {
            Label = "BOSS",
            Value = bossValue,
            Detail = bossDetail,
            ValueColor = bossColor
        });

        return metrics;
    }

    private void DrawSummaryMetric(HudMetric metric, RectangleF rect, float scale)
    {
        var muted = new Color(145, 151, 164, 255);
        var textX = rect.X + 2f * scale;
        var rightX = rect.X + rect.Width - 2f * scale;
        var availableWidth = rect.Width - 4f * scale;

        Graphics.DrawText(TrimToWidth(metric.Label, availableWidth),
            new Vector2(textX, rect.Y + 7f * scale), muted);
        var value = TrimToWidth(metric.Value, availableWidth * 0.68f);
        var valueSize = Graphics.MeasureText(value);
        Graphics.DrawText(value,
            new Vector2(rightX - valueSize.X, rect.Y + 7f * scale), metric.ValueColor);
        Graphics.DrawText(TrimToWidth(metric.Detail, availableWidth),
            new Vector2(textX, rect.Y + 31f * scale), muted);
    }

    private static double GetExperienceGainPercent(int level, long experienceGained)
    {
        if (level <= 0 || level >= 100 || level >= ExperienceToNextLevel.Length ||
            ExperienceToNextLevel[level] <= 0)
            return 0;

        return experienceGained * 100d / ExperienceToNextLevel[level];
    }

    private void DrawRunPanel(RunSnapshot run, bool active)
    {
        var scale = Settings.UiScale.Value / 100f;
        // The minimum also migrates older V1 configurations whose stored width
        // predates the new horizontal layout.
        var panelWidth = Math.Max(560, Settings.PanelWidth.Value) * scale;
        var padding = 10f * scale;
        var headerHeight = 46f * scale;
        var metricHeight = 48f * scale;
        var rowGap = 1f * scale;

        var metrics = BuildMetrics(run);
        var rows = metrics.Count == 0 ? 0 : (int)Math.Ceiling(metrics.Count / 4f);
        var panelHeight = padding + headerHeight + rows * metricHeight +
                          Math.Max(0, rows - 1) * rowGap + padding;

        var x = Settings.PanelX.Value;
        var y = Settings.PanelY.Value;
        var bounds = new RectangleF(x, y, panelWidth, panelHeight);
        var border = Settings.BorderColor.Value;
        var background = Settings.BackgroundColor.Value;
        var accent = Settings.AccentColor.Value;

        Graphics.DrawBox(bounds, border, 7f * scale);
        Graphics.DrawBox(new RectangleF(x + 1f, y + 1f, panelWidth - 2f, panelHeight - 2f),
            background, 6f * scale);
        Graphics.DrawBox(new RectangleF(x + 1f, y + 1f, 3f * scale, panelHeight - 2f),
            accent, 5f * scale);

        var headerX = x + padding;
        var headerY = y + padding - 2f * scale;
        var titleColor = new Color(239, 241, 246, 255);
        var muted = new Color(145, 151, 164, 255);
        var statusColor = active
            ? new Color(74, 207, 138, 255)
            : new Color(245, 180, 65, 255);

        var mapName = TrimToWidth(run.AreaName, panelWidth - 170f * scale);
        Graphics.DrawText(mapName, new Vector2(headerX, headerY), titleColor);

        var tierText = $"T{run.MapTier}";
        var tierSize = Graphics.MeasureText(tierText);
        var tierWidth = tierSize.X + 14f * scale;
        var tierX = x + panelWidth - padding - tierWidth;
        Graphics.DrawBox(new RectangleF(tierX, headerY - 2f * scale, tierWidth,
            23f * scale), new Color(54, 46, 31, 245), 5f * scale);
        Graphics.DrawText(tierText,
            new Vector2(tierX + (tierWidth - tierSize.X) / 2f, headerY), accent);

        var secondY = headerY + 23f * scale;
        Graphics.DrawBox(new RectangleF(headerX, secondY + 6f * scale,
            5f * scale, 5f * scale), statusColor, 3f * scale);
        Graphics.DrawText(active ? "IN MAP" : "PAUSED",
            new Vector2(headerX + 10f * scale, secondY), statusColor);

        var timer = FormatDuration(run.ActiveSeconds);
        var timerSize = Graphics.MeasureText(timer);
        Graphics.DrawText(timer,
            new Vector2(x + panelWidth - padding - timerSize.X, secondY), muted);

        var metricsTop = y + padding + headerHeight;
        Graphics.DrawBox(new RectangleF(x + padding, metricsTop - 5f * scale,
            panelWidth - padding * 2f, 1f), new Color(64, 66, 72, 145));

        var metricIndex = 0;
        for (var row = 0; row < rows; row++)
        {
            var remaining = metrics.Count - metricIndex;
            var columns = Math.Min(4, remaining);
            var metricWidth = columns > 0
                ? (panelWidth - padding * 2f) / columns
                : panelWidth - padding * 2f;
            var metricY = metricsTop + row * (metricHeight + rowGap);

            if (row > 0)
                Graphics.DrawBox(new RectangleF(x + padding, metricY - 2f * scale,
                    panelWidth - padding * 2f, 1f), new Color(55, 58, 64, 120));

            for (var col = 0; col < columns; col++, metricIndex++)
            {
                var metricX = x + padding + col * metricWidth;
                if (col > 0)
                    Graphics.DrawBox(new RectangleF(metricX, metricY + 5f * scale,
                        1f, metricHeight - 13f * scale), new Color(55, 58, 64, 115));

                DrawMetric(metrics[metricIndex],
                    new RectangleF(metricX, metricY, metricWidth, metricHeight), scale);
            }
        }
    }

    private List<HudMetric> BuildMetrics(RunSnapshot run)
    {
        var metrics = new List<HudMetric>(8);
        var accent = Settings.AccentColor.Value;
        var normal = new Color(239, 241, 246, 255);
        var good = new Color(74, 207, 138, 255);
        var warning = new Color(245, 180, 65, 255);

        if (Settings.ShowKills.Value)
        {
            var killsPerMinute = run.ActiveSeconds > 0
                ? run.Kills / (run.ActiveSeconds / 60d)
                : 0;
            metrics.Add(new HudMetric
            {
                Label = "KILLS",
                Value = run.Kills.ToString("N0"),
                Detail = $"{killsPerMinute:0.0} / MIN",
                ValueColor = normal
            });
        }

        if (Settings.ShowDps.Value)
        {
            metrics.Add(new HudMetric
            {
                Label = "DPS",
                Value = FormatNumber(run.CurrentDps),
                Detail = $"PEAK {FormatNumber(run.PeakDps)}",
                ValueColor = accent
            });
        }

        if (Settings.ShowTotalDamage.Value)
        {
            metrics.Add(new HudMetric
            {
                Label = "DEALT",
                Value = FormatNumber(run.TotalDamage),
                Detail = $"BURST {FormatNumber(run.MaxMonsterBurst)}",
                ValueColor = normal
            });
        }

        if (Settings.ShowDamageTaken.Value)
        {
            metrics.Add(new HudMetric
            {
                Label = "TAKEN",
                Value = FormatNumber(run.DamageTaken),
                Detail = $"MAX {FormatNumber(run.MaxDamageTaken)}  ·  L{run.LowestLifePercent:0}%",
                ValueColor = run.LowestLifePercent <= 35 ? warning : normal
            });
        }

        if (Settings.ShowExperience.Value)
        {
            var xpPerHour = run.ActiveSeconds > 0
                ? run.ExperienceGained / (run.ActiveSeconds / 3600d)
                : 0;
            metrics.Add(new HudMetric
            {
                Label = "XP GAINED",
                Value = $"+{FormatNumber(run.ExperienceGained)}",
                Detail = $"{FormatNumber(xpPerHour)} / HR",
                ValueColor = good
            });
        }

        if (Settings.ShowPortals.Value)
        {
            var remaining = Math.Max(0, Settings.MaximumPortals.Value - run.Entries);
            metrics.Add(new HudMetric
            {
                Label = "PORTALS",
                Value = $"{remaining} LEFT",
                Detail = $"USED {run.Entries}  ·  DIED {run.Deaths}",
                ValueColor = remaining <= 1 ? warning : normal
            });
        }

        if (Settings.ShowBoss.Value)
        {
            string value;
            Color color;
            string detail;

            if (run.BossesSeen == 0)
            {
                value = "NOT SEEN";
                detail = "MAP BOSS";
                color = warning;
            }
            else if (run.BossesDefeated >= run.BossesSeen)
            {
                value = "DEFEATED";
                detail = run.BossKillSeconds > 0
                    ? $"{FormatDuration(run.BossKillSeconds)} FIGHT"
                    : "MAP BOSS";
                color = good;
            }
            else
            {
                value = "ENGAGED";
                detail = run.BossEncounterStartedAt >= 0
                    ? $"{FormatDuration(run.ActiveSeconds - run.BossEncounterStartedAt)} FIGHT"
                    : $"{run.BossesDefeated}/{run.BossesSeen} DOWN";
                color = accent;
            }

            metrics.Add(new HudMetric
            {
                Label = "BOSS",
                Value = value,
                Detail = detail,
                ValueColor = color
            });
        }

        if (Settings.ShowGold.Value)
        {
            metrics.Add(new HudMetric
            {
                Label = "GOLD",
                Value = $"+{FormatNumber(run.GoldGained)}",
                Detail = "THIS MAP",
                ValueColor = accent
            });
        }

        return metrics;
    }

    private void DrawMetric(HudMetric metric, RectangleF rect, float scale)
    {
        var muted = new Color(137, 143, 156, 255);

        var textX = rect.X + 9f * scale;
        var availableWidth = rect.Width - 16f * scale;
        var label = TrimToWidth(metric.Label, availableWidth);
        var value = TrimToWidth(metric.Value, availableWidth);
        var detail = TrimToWidth(metric.Detail, availableWidth);

        Graphics.DrawText(label,
            new Vector2(textX, rect.Y + 1f * scale), muted);
        Graphics.DrawText(value,
            new Vector2(textX, rect.Y + 17f * scale), metric.ValueColor);
        Graphics.DrawText(detail,
            new Vector2(textX, rect.Y + 33f * scale), muted);
    }

    private string TrimToWidth(string value, float maxWidth)
    {
        if (string.IsNullOrWhiteSpace(value) || Graphics.MeasureText(value).X <= maxWidth)
            return value ?? string.Empty;

        var trimmed = value;
        while (trimmed.Length > 3 && Graphics.MeasureText(trimmed + "...").X > maxWidth)
            trimmed = trimmed[..^1];

        // The in-game font does not reliably include the Unicode ellipsis and
        // renders it as '?', so always use the ASCII fallback.
        return trimmed + "...";
    }

    private static string FormatDuration(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}";
    }

    private static string FormatNumber(double value)
    {
        var abs = Math.Abs(value);
        if (abs >= 1_000_000_000_000d) return $"{value / 1_000_000_000_000d:0.##}T";
        if (abs >= 1_000_000_000d) return $"{value / 1_000_000_000d:0.##}B";
        if (abs >= 1_000_000d) return $"{value / 1_000_000d:0.##}M";
        if (abs >= 1_000d) return $"{value / 1_000d:0.##}K";
        return value.ToString("0");
    }
}
