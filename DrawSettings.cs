using System;
using System.Numerics;
using ExileCore.Shared.Nodes;
using ImGuiNET;
using Color = SharpDX.Color;

namespace MapLens;

public partial class MapLens
{
    public override void DrawSettings()
    {
        ImGui.TextColored(new Vector4(0.91f, 0.68f, 0.27f, 1f), "MAPLENS");
        ImGui.TextDisabled("A clean live dashboard and summary for every map run.");
        ImGui.Separator();

        if (!ImGui.BeginTabBar("##maplens_tabs"))
            return;

        if (ImGui.BeginTabItem("HUD"))
        {
            DrawHudSettings();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Run History"))
        {
            DrawHistorySettings();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("MapLens V5.4 • Session history resets when ExileAPI closes.");
        ImGui.TextDisabled("UP is the percentage of map time spent in active combat.");
        ImGui.TextDisabled("Boss fight time freezes on the first confirmed boss death.");
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.91f, 0.68f, 0.27f, 1f),
            "Made by Woogo. Pls message on Discord for problems or suggestions <3");
    }

    private void DrawHudSettings()
    {
        ImGui.Spacing();
        Toggle("Enable MapLens", Settings.Enable);

        ImGui.Spacing();
        ImGui.TextDisabled("DISPLAY MODE");
        ImGui.Separator();
        ListSelector("When MapLens is visible", Settings.DisplayMode);
        ImGui.TextDisabled("Hideout summaries appear after a direct map-to-hideout return.");

        ImGui.Spacing();
        ImGui.TextDisabled("COMPACT IN-MAP HUD");
        ImGui.Separator();
        Toggle("Kills and kills per minute", Settings.ShowKills);
        Toggle("Current and peak DPS", Settings.ShowDps);
        Toggle("Total damage and combat uptime", Settings.ShowTotalDamage);
        Toggle("Damage taken, largest hit, and lowest life", Settings.ShowDamageTaken);
        Toggle("XP gained and XP per hour", Settings.ShowExperience);
        Toggle("Portals remaining and deaths", Settings.ShowPortals);
        Toggle("Map boss status", Settings.ShowBoss);
        Toggle("Gold gained (optional)", Settings.ShowGold);

        ImGui.Spacing();
        ImGui.TextDisabled("COMPACT HUD POSITION & SIZE");
        ImGui.Separator();
        Toggle("Edit panel positions with the mouse", Settings.EditPanelPositions);
        ImGui.TextDisabled("Enable this, then drag a panel's top strip. Disable it when finished.");
        Slider("Horizontal position", Settings.PanelX);
        Slider("Vertical position", Settings.PanelY);
        Slider("Panel width", Settings.PanelWidth);
        Slider("UI scale", Settings.UiScale);

        ImGui.Spacing();
        ImGui.TextDisabled("VERTICAL HIDEOUT SUMMARY");
        ImGui.Separator();
        Slider("Auto-close delay (seconds)", Settings.SummaryDurationSeconds);
        Slider("Summary horizontal position", Settings.SummaryX);
        Slider("Summary vertical position", Settings.SummaryY);
        Slider("Summary width", Settings.SummaryWidth);
        Slider("Summary UI scale", Settings.SummaryUiScale);

        ImGui.Spacing();
        ImGui.TextDisabled("MAP TRACKING");
        ImGui.Separator();
        Slider("Maximum map portals", Settings.MaximumPortals);
        ImGui.TextDisabled("The first map entry consumes one portal. The normal value is 6.");

        if (ImGui.CollapsingHeader("ADVANCED"))
        {
            Slider("Combat update interval (ms)", Settings.UpdateIntervalMs);
            ImGui.TextDisabled("Higher values use less CPU but make DPS react more slowly.");
            Slider("Area-entry damage warmup (ms)", Settings.CombatWarmupMs);
            ImGui.TextDisabled("Prevents loading and entity initialization from appearing as damage.");
        }

        ImGui.Spacing();
        ImGui.TextDisabled("APPEARANCE");
        ImGui.Separator();
        ColorNodeEditor("Background", Settings.BackgroundColor);
        ColorNodeEditor("Border", Settings.BorderColor);
        ColorNodeEditor("Accent", Settings.AccentColor);

        if (ImGui.Button("Reset Appearance"))
        {
            Settings.PanelX.Value = 28;
            Settings.PanelY.Value = 190;
            Settings.PanelWidth.Value = 580;
            Settings.UiScale.Value = 100;
            Settings.SummaryX.Value = 20;
            Settings.SummaryY.Value = 100;
            Settings.SummaryWidth.Value = 340;
            Settings.SummaryUiScale.Value = 100;
            Settings.BackgroundColor.Value = new Color(5, 6, 8, 218);
            Settings.BorderColor.Value = new Color(74, 67, 54, 175);
            Settings.AccentColor.Value = new Color(232, 174, 69, 255);
        }

        if (_activeRun != null && _currentAreaIsActiveMap)
        {
            ImGui.SameLine();
            if (ImGui.Button("Reset Current Run"))
                ResetCurrentRun();
        }
    }

    private void DrawHistorySettings()
    {
        ImGui.Spacing();
        ImGui.TextDisabled("The ten most recent completed maps from this ExileAPI session.");
        ImGui.Separator();

        if (_history.Count == 0)
        {
            ImGui.TextDisabled("No completed map runs yet.");
            return;
        }

        if (ImGui.BeginTable("##maplens_history", 9,
                ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg |
                ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Map");
            ImGui.TableSetupColumn("Time");
            ImGui.TableSetupColumn("Combat");
            ImGui.TableSetupColumn("Kills");
            ImGui.TableSetupColumn("XP");
            ImGui.TableSetupColumn("Peak DPS");
            ImGui.TableSetupColumn("Dealt");
            ImGui.TableSetupColumn("Taken");
            ImGui.TableSetupColumn("Deaths");
            ImGui.TableHeadersRow();

            foreach (var run in _history)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{run.AreaName} (T{run.MapTier})");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatDuration(run.ActiveSeconds));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatDuration(run.CombatSeconds));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(run.Kills.ToString("N0"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatNumber(run.ExperienceGained));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatNumber(run.PeakDps));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatNumber(run.TotalDamage));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatNumber(run.DamageTaken));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(run.Deaths.ToString());
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
        if (ImGui.Button("Clear Session History"))
            _history.Clear();
    }

    private static void Toggle(string label, ToggleNode node)
    {
        var value = node.Value;
        if (ImGui.Checkbox(label, ref value))
            node.Value = value;
    }

    private static void ListSelector(string label, ListNode node)
    {
        ImGui.SetNextItemWidth(Math.Max(220f, ImGui.GetContentRegionAvail().X * 0.55f));
        if (!ImGui.BeginCombo(label, node.Value))
            return;

        foreach (var option in node.Values)
        {
            var selected = string.Equals(option, node.Value, StringComparison.Ordinal);
            if (ImGui.Selectable(option, selected))
                node.Value = option;
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private static void Slider(string label, RangeNode<int> node)
    {
        var value = node.Value;
        ImGui.SetNextItemWidth(Math.Max(190f, ImGui.GetContentRegionAvail().X * 0.55f));
        if (ImGui.SliderInt(label, ref value, node.Min, node.Max))
            node.Value = value;
    }

    private static void ColorNodeEditor(string label, ColorNode node)
    {
        var color = node.Value;
        var value = new Vector4(
            color.R / 255f,
            color.G / 255f,
            color.B / 255f,
            color.A / 255f);

        if (ImGui.ColorEdit4(label, ref value, ImGuiColorEditFlags.AlphaBar))
        {
            node.Value = new Color(
                (byte)Math.Clamp((int)Math.Round(value.X * 255f), 0, 255),
                (byte)Math.Clamp((int)Math.Round(value.Y * 255f), 0, 255),
                (byte)Math.Clamp((int)Math.Round(value.Z * 255f), 0, 255),
                (byte)Math.Clamp((int)Math.Round(value.W * 255f), 0, 255));
        }
    }
}
