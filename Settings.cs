using System.Collections.Generic;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using Color = SharpDX.Color;

namespace MapLens;

public class Settings : ISettings
{
    [Menu("Enable MapLens")]
    public ToggleNode Enable { get; set; } = new(true);

    public ListNode DisplayMode { get; set; } = new()
    {
        Value = "Hideout Summary Only",
        Values = new List<string>
        {
            "Hideout Summary Only",
            "Compact HUD Only",
            "Compact HUD + Hideout Summary"
        }
    };

    public ToggleNode ShowKills { get; set; } = new(true);
    public ToggleNode ShowDps { get; set; } = new(true);
    public ToggleNode ShowTotalDamage { get; set; } = new(true);
    public ToggleNode ShowDamageTaken { get; set; } = new(true);
    public ToggleNode ShowExperience { get; set; } = new(true);
    public ToggleNode ShowPortals { get; set; } = new(true);
    public ToggleNode ShowBoss { get; set; } = new(true);
    public ToggleNode ShowGold { get; set; } = new(true);
    public ToggleNode EditPanelPositions { get; set; } = new(false);

    public RangeNode<int> PanelX { get; set; } = new(28, 0, 4000);
    public RangeNode<int> PanelY { get; set; } = new(190, 0, 2200);
    public RangeNode<int> PanelWidth { get; set; } = new(580, 520, 760);
    public RangeNode<int> UiScale { get; set; } = new(100, 90, 130);
    public RangeNode<int> MaximumPortals { get; set; } = new(6, 1, 12);
    public RangeNode<int> UpdateIntervalMs { get; set; } = new(200, 100, 1000);
    public RangeNode<int> CombatWarmupMs { get; set; } = new(1500, 500, 5000);
    public RangeNode<int> SummaryDurationSeconds { get; set; } = new(30, 5, 120);
    public RangeNode<int> SummaryX { get; set; } = new(20, 0, 4000);
    public RangeNode<int> SummaryY { get; set; } = new(100, 0, 2200);
    public RangeNode<int> SummaryWidth { get; set; } = new(340, 300, 520);
    public RangeNode<int> SummaryUiScale { get; set; } = new(100, 90, 130);
    public RangeNode<int> LayoutVersion { get; set; } = new(0, 0, 4);

    public ColorNode BackgroundColor { get; set; } = new(new Color(5, 6, 8, 218));
    public ColorNode BorderColor { get; set; } = new(new Color(74, 67, 54, 175));
    public ColorNode AccentColor { get; set; } = new(new Color(232, 174, 69, 255));
}
