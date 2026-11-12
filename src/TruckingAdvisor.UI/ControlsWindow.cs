using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TruckingAdvisor.Shared.Input;

namespace TruckingAdvisor.UI;

/// <summary>
/// Controls & Key Bindings: every function can have any number of keyboard keys and
/// wheel / button-box / joystick buttons. Changes apply immediately and are saved.
/// </summary>
public sealed class ControlsWindow : Window
{
    private readonly InputBindingSet _bindings;
    private readonly InputRouter _input;
    private readonly Func<IReadOnlyList<string>> _devices;
    private readonly Action _save;
    private readonly Dictionary<string, TextBlock> _boundText = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), MinHeight = 18 };
    private readonly TextBlock _deviceText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly System.Windows.Threading.DispatcherTimer _deviceTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private Button? _waiting;

    public ControlsWindow(InputBindingSet bindings, InputRouter input, Func<IReadOnlyList<string>> devices, Action save)
    {
        _bindings = bindings;
        _input = input;
        _devices = devices;
        _save = save;
        Title = "Controls & Key Bindings";
        Width = 720;
        Height = 760;
        MinWidth = 560;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "CardBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");

        var root = new DockPanel { Margin = new Thickness(16) };

        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Text = "Click Add, then press a key or a button on your wheel, button box, joystick or gamepad. " +
                   "A function can have several bindings; a key or button can only do one thing (binding it again moves it). " +
                   "Inputs still reach the game too, so pick buttons the game isn't using. Esc cancels."
        };
        DockPanel.SetDock(intro, Dock.Top);
        root.Children.Add(intro);

        // Bottom: detected controllers, status, buttons.
        var bottom = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        _deviceText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        bottom.Children.Add(_deviceText);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        bottom.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(MakeButton("Reset all to defaults", (_, _) => ResetAll()));
        buttons.Children.Add(MakeButton("Close", (_, _) => Close()));
        bottom.Children.Add(buttons);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        // Middle: one row per function, grouped.
        var list = new StackPanel();
        foreach (var group in InputActions.All.GroupBy(a => a.Category))
        {
            var header = new TextBlock { Text = group.Key.ToUpperInvariant(), FontWeight = FontWeights.Bold, FontSize = 12, Margin = new Thickness(0, 12, 0, 4) };
            header.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            list.Children.Add(header);
            foreach (var action in group) list.Children.Add(Row(action));
        }
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        RefreshDevices();
        _deviceTimer.Tick += (_, _) => RefreshDevices();
        _deviceTimer.Start();
        Closed += (_, _) => { _deviceTimer.Stop(); _input.CancelCapture(); };
    }

    private FrameworkElement Row(InputAction action)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock { Text = action.Label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var bound = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 8, 0) };
        _boundText[action.Id] = bound;
        UpdateRow(action.Id);

        var add = MakeButton("Add", null);
        add.Click += (_, _) => StartCapture(action, add);
        var clear = MakeButton("Clear", (_, _) =>
        {
            _bindings.Clear(action.Id);
            UpdateRow(action.Id);
            _save();
            _status.Text = $"Cleared: {action.Label}.";
        });
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(add);
        actions.Children.Add(clear);

        Grid.SetColumn(label, 0);
        Grid.SetColumn(bound, 1);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(label);
        grid.Children.Add(bound);
        grid.Children.Add(actions);
        return grid;
    }

    private void StartCapture(InputAction action, Button button)
    {
        if (_waiting is not null) _waiting.Content = "Add";
        _waiting = button;
        button.Content = "Press...";
        _status.Text = $"Press a key or a wheel / button-box button for \"{action.Label}\" (Esc cancels).";
        _input.CaptureNext(input => Dispatcher.BeginInvoke(() => FinishCapture(action, button, input)));
    }

    private void FinishCapture(InputAction action, Button button, InputBinding input)
    {
        button.Content = "Add";
        _waiting = null;
        if (input.Source == InputSource.Keyboard && input.Code == 0x1B) // Esc
        {
            _status.Text = "Cancelled.";
            return;
        }
        string? movedFrom = _bindings.Bind(action.Id, input);
        foreach (var id in _boundText.Keys) UpdateRow(id);
        _save();
        _status.Text = movedFrom is not null && InputActions.Find(movedFrom) is { } old
            ? $"{input.Display} now does \"{action.Label}\" (it was \"{old.Label}\")."
            : $"{input.Display} now does \"{action.Label}\".";
    }

    private void UpdateRow(string actionId)
    {
        var list = _bindings.For(actionId);
        var tb = _boundText[actionId];
        tb.Text = list.Count == 0 ? "Not bound" : string.Join("   ·   ", list.Select(b => b.Display));
        if (list.Count == 0) tb.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        else tb.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
    }

    private void ResetAll()
    {
        if (MessageBox.Show(this, "Put every binding back to the default keys? Wheel and button-box bindings will be removed.",
                "Reset controls", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _bindings.Bindings = InputBindingSet.Defaults().Bindings;
        foreach (var id in _boundText.Keys) UpdateRow(id);
        _save();
        _status.Text = "Controls reset to defaults.";
    }

    private void RefreshDevices()
    {
        var names = _devices();
        _deviceText.Text = names.Count == 0
            ? "No wheel, button box or joystick detected. Plug one in and it appears here within a few seconds."
            : "Detected controllers: " + string.Join(", ", names);
    }

    private static Button MakeButton(string text, RoutedEventHandler? onClick)
    {
        var b = new Button { Content = text, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
        if (onClick is not null) b.Click += onClick;
        return b;
    }
}
