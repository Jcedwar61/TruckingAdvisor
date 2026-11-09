using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using TruckingAdvisor.UI.Overlays;

namespace TruckingAdvisor.UI;

/// <summary>
/// Small always-on-top HUD for use over the game. Click-through and never takes focus, so it
/// can't steal steering/keyboard input. Note: it shows over ATS in borderless/windowed mode;
/// exclusive fullscreen hides normal windows.
/// </summary>
public sealed class HudOverlayWindow : Window
{
    public HudOverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Left = SystemParameters.WorkArea.Right - 340;
        Top = SystemParameters.WorkArea.Top + 20;

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(Line("SpeedText", 30, "#F8FAFC", " MPH"));
        panel.Children.Add(Line("SpeedLimitText", 13, "#F59E0B"));
        panel.Children.Add(Line("FuelText", 13, "#F59E0B", " fuel"));
        panel.Children.Add(Line("RemainingText", 12, "#10B981"));
        panel.Children.Add(Line("RadioStateText", 12, "#94A3B8"));
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0B, 0x0F, 0x17)),
            CornerRadius = new CornerRadius(8),
            Child = panel,
            MinWidth = 300
        };
    }

    private static TextBlock Line(string path, double size, string color, string suffix = "")
    {
        var tb = new TextBlock
        {
            FontSize = size,
            Foreground = (Brush)new BrushConverter().ConvertFromString(color)!,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 320
        };
        tb.SetBinding(TextBlock.TextProperty, new Binding(path) { StringFormat = "{0}" + suffix });
        return tb;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        GameplayOverlayInterop.SetClickThroughAndNoActivate(new WindowInteropHelper(this).Handle, enableClickThrough: true);
    }
}
