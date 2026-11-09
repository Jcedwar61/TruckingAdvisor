using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TruckingAdvisor.UI.Overlays;

namespace TruckingAdvisor.UI;

/// <summary>
/// Small always-on-top HUD for use over the game.
/// Locked (normal): click-through and never takes focus, so it can't steal steering/keyboard input.
/// Unlocked: drag it anywhere (any monitor) and resize it from the corner; the text scales with the size.
/// Note: it shows over ATS in borderless/windowed mode; exclusive fullscreen hides normal windows.
/// </summary>
public sealed class HudOverlayWindow : Window
{
    private readonly Border _frame;
    private IntPtr _hwnd;

    public bool IsLocked { get; private set; } = true;

    public HudOverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Width = 320;
        Height = 170;
        MinWidth = 180;
        MinHeight = 90;
        Left = SystemParameters.WorkArea.Right - Width - 20;
        Top = SystemParameters.WorkArea.Top + 20;

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(Line("SpeedText", 30, "#F8FAFC", " MPH"));
        panel.Children.Add(Line("SpeedLimitText", 13, "#F59E0B"));
        panel.Children.Add(Line("FuelText", 13, "#F59E0B", " fuel"));
        panel.Children.Add(Line("RemainingText", 12, "#10B981"));
        panel.Children.Add(Line("RadioStateText", 12, "#94A3B8"));

        _frame = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0B, 0x0F, 0x17)),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(0),
            // Viewbox scales everything to the window size, so resizing makes the text bigger or smaller.
            Child = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both, Child = new Border { Width = 300, Child = panel } }
        };
        Content = _frame;
        MouseLeftButtonDown += (_, e) => { if (!IsLocked && e.ButtonState == MouseButtonState.Pressed) DragMove(); };
    }

    /// <summary>Restores a saved position/size if it is still on a visible screen.</summary>
    public void ApplySavedBounds(double? left, double? top, double? width, double? height)
    {
        if (width is > 0 && height is > 0) { Width = width.Value; Height = height.Value; }
        if (left is { } l && top is { } t && IsOnScreen(l, t, Width, Height)) { Left = l; Top = t; }
    }

    /// <summary>Locked = click-through for gameplay. Unlocked = draggable and resizable.</summary>
    public void SetLocked(bool locked)
    {
        IsLocked = locked;
        ResizeMode = locked ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
        _frame.BorderThickness = new Thickness(locked ? 0 : 2);
        _frame.BorderBrush = Brushes.Orange;
        if (_hwnd != IntPtr.Zero) GameplayOverlayInterop.SetClickThroughAndNoActivate(_hwnd, enableClickThrough: locked);
    }

    private static TextBlock Line(string path, double size, string color, string suffix = "")
    {
        var tb = new TextBlock
        {
            FontSize = size,
            Foreground = (Brush)new BrushConverter().ConvertFromString(color)!,
            TextWrapping = TextWrapping.Wrap
        };
        tb.SetBinding(TextBlock.TextProperty, new Binding(path) { StringFormat = "{0}" + suffix });
        return tb;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        GameplayOverlayInterop.SetClickThroughAndNoActivate(_hwnd, enableClickThrough: IsLocked);
    }

    /// <summary>True if the rectangle overlaps the virtual desktop (all monitors).</summary>
    public static bool IsOnScreen(double left, double top, double width, double height)
    {
        var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var r = new Rect(left, top, Math.Max(width, 1), Math.Max(height, 1));
        r.Intersect(desktop);
        return !r.IsEmpty && r.Width >= 50 && r.Height >= 30;
    }
}
