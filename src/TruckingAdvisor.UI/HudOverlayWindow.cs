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
    private readonly StackPanel _panel = new() { Margin = new Thickness(12) };

    /// <summary>Rows the player picked (in order) and the background opacity.</summary>
    public void SetRows(IEnumerable<string> rows, double opacity)
    {
        _panel.Children.Clear();
        var list = rows.ToList();
        bool combined = list.Contains("Speed") && list.Contains("Limit");
        foreach (var row in list)
        {
            UIElement? line = row switch
            {
                // Speed and the limit share one row: big number, US-style sign beside it.
                "Speed" when combined => SpeedWithSign(),
                "Limit" when combined => null,
                "Speed" => SpeedLine(),
                "Limit" => LimitSign(),
                "Fuel" => FuelLine(),
                "Range" => Line("RangeText", 12, "#F59E0B"),
                "Remaining" => Line("RemainingText", 12, "#10B981"),
                "Score" => Line("ScoreText", 12, "#10B981", " trip score"),
                "Location" => Line("LocationText", 12, "#94A3B8"),
                "Grade" => Line("GradeAheadText", 12, "#EF4444"),
                "Radio" => Line("RadioStateText", 12, "#94A3B8"),
                "Map" => MiniMap(),
                _ => null
            };
            if (line is not null) _panel.Children.Add(line);
        }
        if (_frame is not null)
            _frame.Background = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(opacity, 0.2, 1.0) * 255), 0x0B, 0x0F, 0x17));
    }

    private static Brush B(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;

    /// <summary>Big speed; turns red while over the limit.</summary>
    private static UIElement SpeedLine()
    {
        var speed = new TextBlock { FontSize = 40, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Bottom };
        speed.SetBinding(TextBlock.TextProperty, new Binding("SpeedText"));
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.ForegroundProperty, B("#F8FAFC")));
        var trigger = new DataTrigger { Binding = new Binding("IsSpeeding"), Value = true };
        trigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, B("#EF4444")));
        style.Triggers.Add(trigger);
        speed.Style = style;
        var unit = new TextBlock { FontSize = 13, Foreground = B("#94A3B8"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 8) };
        unit.SetBinding(TextBlock.TextProperty, new Binding("SpeedUnitText") { StringFormat = " {0}" });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(speed);
        row.Children.Add(unit);
        return row;
    }

    /// <summary>White US speed-limit sign: SPEED / LIMIT / number.</summary>
    private static UIElement LimitSign()
    {
        var number = new TextBlock { FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center };
        number.SetBinding(TextBlock.TextProperty, new Binding("SpeedLimitNumber"));
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "SPEED", FontSize = 8, FontWeight = FontWeights.Bold, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock { Text = "LIMIT", FontSize = 8, FontWeight = FontWeights.Bold, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(number);
        return new Border
        {
            Background = Brushes.White,
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 3, 6, 2),
            Width = 52,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 2, 0, 4),
            Child = stack
        };
    }

    private static UIElement SpeedWithSign()
    {
        var grid = new DockPanel { LastChildFill = true };
        var sign = LimitSign();
        ((FrameworkElement)sign).VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(sign, Dock.Right);
        grid.Children.Add(sign);
        grid.Children.Add(SpeedLine());
        return grid;
    }

    /// <summary>Mini-map that follows the truck (roads you've driven, cities, destination).</summary>
    private static UIElement MiniMap()
    {
        var map = new TruckingAdvisor.UI.Controls.MapView
        {
            Compact = true,
            Height = 150,
            Margin = new Thickness(0, 6, 0, 2),
            RoadBrush = B("#64748B"),
            AccentBrush = B("#F59E0B"),
            TextBrush = B("#F8FAFC"),
            GoodBrush = B("#10B981"),
            IsHitTestVisible = false
        };
        map.SetBinding(TruckingAdvisor.UI.Controls.MapView.SnapshotProperty, new Binding("Map"));
        return new Border { CornerRadius = new CornerRadius(6), Background = B("#0F172A"), ClipToBounds = true, Child = map, Margin = new Thickness(0, 4, 0, 0) };
    }

    /// <summary>Fuel text with a thin bar underneath (amber; red under 15%).</summary>
    private static UIElement FuelLine()
    {
        var stack = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
        stack.Children.Add(Line("FuelText", 13, "#F59E0B", " fuel"));
        var bar = new ProgressBar { Maximum = 100, Height = 5, Margin = new Thickness(0, 3, 0, 0), Background = B("#1E293B") };
        bar.SetBinding(System.Windows.Controls.Primitives.RangeBase.ValueProperty, new Binding("FuelPercentValue") { Mode = BindingMode.OneWay });
        var style = new Style(typeof(ProgressBar), Application.Current?.TryFindResource(typeof(ProgressBar)) as Style);
        style.Setters.Add(new Setter(Control.ForegroundProperty, B("#F59E0B")));
        var low = new DataTrigger { Binding = new Binding("IsFuelLow"), Value = true };
        low.Setters.Add(new Setter(Control.ForegroundProperty, B("#EF4444")));
        style.Triggers.Add(low);
        bar.Style = style;
        stack.Children.Add(bar);
        return stack;
    }

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

        var panel = _panel;
        SetRows(TruckingAdvisor.AI.AiSettings.DefaultHudRows, 0.85);

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
