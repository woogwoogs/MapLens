using System;

namespace MapLens;

internal sealed class RunSnapshot
{
    public uint AreaHash;
    public string AreaName = "Unknown Map";
    public int AreaLevel;
    public int MapTier;
    public DateTime StartedAt;
    public DateTime? FinishedAt;

    public double ActiveSeconds;
    public long StartExperience;
    public int StartLevel;
    public int StartKills;
    public int Entries;
    public int Deaths;

    public long ExperienceGained;
    public int Kills;
    public double TotalDamage;
    public double CurrentDps;
    public double PeakDps;
    public double MaxMonsterBurst;
    public double DamageTaken;
    public double MaxDamageTaken;
    public double LowestLifePercent = 100d;
    public double CombatSeconds;
    public long GoldGained;
    public bool HasMapStats;
    public int MapQuantity;
    public int MapRarity;
    public int MapPackSize;
    public int BossesSeen;
    public int BossesDefeated;
    public double BossEncounterStartedAt = -1d;
    public double BossKillSeconds;

    public RunSnapshot Copy()
    {
        return (RunSnapshot)MemberwiseClone();
    }
}
