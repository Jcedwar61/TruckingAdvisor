using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TruckingAdvisor.Presentation;
using TruckingAdvisor.UI.Overlays;

namespace TruckingAdvisor.UI;

/// <summary>
/// Small CB channel picker that sits under the HUD and follows it.
/// Unlike the HUD it takes mouse clicks, but it never takes focus, so the game keeps your keyboard
/// and wheel. Click the bar to open the channel list; [ and ] open it from the keyboard too.
/// </summary>
public sealed class HudChannelBar : Window
{
    private const double DesignWidth = 300;
    private static readonly (int Channel, string Label)[] Presets =
    {
        (19, "Highway traffic"), (9, "Emergency"), (17, "Regional traffic"),
        (21, "Casual conversation"), (25, "Truck stop talk"), (30, "Company dispatch")
    };

    private readonly MainDashboardViewModel _vm;
    private readonly Border _frame;
    private readonly UniformGrid _list = new() { Columns = 3, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _arrow = new() { Text = "▾", Foreground = Amber, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<int, Border> _buttons = new();
    private readonly DispatcherTimer _autoClose = new() { Interval = TimeSpan.FromSeconds(4) };
    private HudOverlayWindow? _hud;
    private int _shownChannel = -1;

    private static readonly Brush Amber = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly Brush Text = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
    private static readonly Brush Chip = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B));
    private static readonly Brush ChipHover = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55));

    public bool IsOpen => _list.Visibility == Visibility.Visible;

    public HudChannelBar(MainDashboardViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 320;

        // Header: "CB 19 · HIGHWAY TRAFFIC ▾" — click to open/close the list.
        var number = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Amber, VerticalAlignment = VerticalAlignment.Center };
        number.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainDashboardViewModel.ChannelNumberText)) { StringFormat = "CB {0}" });
        var name = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainDashboardViewModel.ChannelNameText)));
        var header = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand };
        DockPanel.SetDock(_arrow, Dock.Right);
        header.Children.Add(_arrow);
        header.Children.Add(number);
        header.Children.Add(name);
        header.ToolTip = "Change CB channel ([ and ] work in game too)";
        header.MouseLeftButtonUp += (_, e) => { Toggle(); e.Handled = true; };

        foreach (var (ch, label) in Presets)
        {
            var b = MakeChip(ch, label);
            _buttons[ch] = b;
            _list.Children.Add(b);
        }

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(_list);
        _frame = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 8),
            Background = new SolidColorBrush(Color.FromArgb(0xD9, 0x0B, 0x0F, 0x17)),
            // Same scaling idea as the HUD: designed at 300 px wide, grows and shrinks with the HUD.
            Child = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both, Child = new Border { Width = DesignWidth - 20, Child = stack } }
        };
        Content = _frame;

        _autoClose.Tick += (_, _) => { _autoClose.Stop(); SetOpen(false); };
        _vm.PropertyChanged += OnVmChanged;
        Highlight();
    }

    private Border MakeChip(int channel, string label)
    {
        // One labeled row per channel: "19   Highway traffic".
        var text = new TextBlock();
        text.Inlines.Add(new System.Windows.Documents.Run(channel.ToString().PadLeft(2)) { FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.Bold, FontSize = 15 });
        text.Inlines.Add(new System.Windows.Documents.Run("   " + label) { FontSize = 12 });
        var chip = new Border
        {
            Margin = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(8, 3, 8, 3),
            CornerRadius = new CornerRadius(5),
            Background = Chip,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Child = text,
            ToolTip = MainDashboardViewModel.ChannelName(channel)
        };
        chip.MouseEnter += (_, _) => { if (_vm.ActiveChannel != channel) chip.Background = ChipHover; };
        chip.MouseLeave += (_, _) => Highlight(force: true);
        chip.MouseLeftButtonUp += (_, e) =>
        {
            _vm.SelectChannel(channel);
            SetOpen(false);
            e.Handled = true;
        };
        return chip;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is nameof(MainDashboardViewModel.ChannelNumberText) or nameof(MainDashboardViewModel.ActiveChannel))
            Highlight();
    }

    /// <summary>Current channel: amber chip with dark text. Others: plain.</summary>
    private void Highlight(bool force = false)
    {
        int current = _vm.ActiveChannel;
        if (!force && current == _shownChannel) return;
        _shownChannel = current;
        foreach (var (ch, chip) in _buttons)
        {
            bool on = ch == current;
            chip.Background = on ? Amber : Chip;
            chip.BorderBrush = on ? Amber : Brushes.Transparent;
            ((TextBlock)chip.Child).Foreground = on ? Brushes.Black : Text;
        }
    }

    private void Toggle() => SetOpen(!IsOpen);

    private void SetOpen(bool open)
    {
        _list.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        _arrow.Text = open ? "▴" : "▾";
        if (!open) _autoClose.Stop();
        Dispatcher.BeginInvoke(Follow, DispatcherPriority.Loaded);
    }

    /// <summary>Opens the list briefly (used by the [ and ] hotkeys) so you can see where you landed.</summary>
    public void Flash()
    {
        if (!IsVisible) return;
        SetOpen(true);
        _autoClose.Stop();
        _autoClose.Start();
    }

    /// <summary>Keeps the bar under the HUD (or above it near the bottom of the screen).</summary>
    public void AttachTo(HudOverlayWindow hud)
    {
        _hud = hud;
        hud.LocationChanged += (_, _) => Follow();
        hud.SizeChanged += (_, _) => Follow();
        SizeChanged += (_, _) => Follow();
        Follow();
    }

    private void Follow()
    {
        if (_hud is null) return;
        Width = _hud.ActualWidth > 0 ? _hud.ActualWidth : _hud.Width;
        Left = _hud.Left;
        double height = ActualHeight > 0 ? ActualHeight : 40;
        double below = _hud.Top + (_hud.ActualHeight > 0 ? _hud.ActualHeight : _hud.Height) + 4;
        var screenBottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        Top = below + height <= screenBottom ? below : _hud.Top - height - 4;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        // Clickable, but never activated: the game keeps keyboard focus.
        GameplayOverlayInterop.SetClickThroughAndNoActivate(hwnd, enableClickThrough: false);
        HwndSource.FromHwnd(hwnd)?.AddHook(NoActivateHook);
    }

    private static IntPtr NoActivateHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.PropertyChanged -= OnVmChanged;
        _autoClose.Stop();
        base.OnClosed(e);
    }
}
