using System.Windows;
using System.Windows.Media;

namespace TruckingAdvisor.UI;

/// <summary>Swaps the app's color resources at runtime (every main-window color uses DynamicResource).</summary>
public static class ThemeManager
{
    public static readonly string[] Themes = { "Midnight", "Charcoal", "Slate Light" };
    public static readonly string[] Accents = { "Amber", "Blue", "Green", "Red", "Purple" };

    public static void Apply(string? theme, string? accent)
    {
        var (bg, card, button, text, muted) = theme switch
        {
            "Charcoal" => ("#121212", "#1E1E1E", "#2A2A2A", "#F5F5F5", "#A3A3A3"),
            "Slate Light" => ("#E2E8F0", "#F8FAFC", "#CBD5E1", "#0F172A", "#475569"),
            _ => ("#0B0F17", "#151C28", "#1E293B", "#F8FAFC", "#94A3B8")
        };
        string accentHex = accent switch
        {
            "Blue" => "#3B82F6",
            "Green" => "#22C55E",
            "Red" => "#F43F5E",
            "Purple" => "#A855F7",
            _ => "#F59E0B"
        };
        Set("BgBrush", bg);
        Set("CardBrush", card);
        Set("ButtonBrush", button);
        Set("TextBrush", text);
        Set("MutedBrush", muted);
        Set("AccentBrush", accentHex);
        Set("GoodBrush", theme == "Slate Light" ? "#047857" : "#10B981");
        Set("BadBrush", theme == "Slate Light" ? "#B91C1C" : "#EF4444");
    }

    private static void Set(string key, string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        Application.Current.Resources[key] = brush;
    }
}
