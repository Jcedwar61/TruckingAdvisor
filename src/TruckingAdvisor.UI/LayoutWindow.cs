using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TruckingAdvisor.AI;

namespace TruckingAdvisor.UI;

/// <summary>Theme, dashboard widget order/visibility, and which rows the HUD shows.</summary>
public sealed class LayoutWindow : Window
{
    private sealed class Item
    {
        public string Id { get; init; } = "";
        public bool Visible { get; set; }
        public override string ToString() => Id;
    }

    private readonly AiSettings _s;
    private readonly ComboBox _theme = new() { ItemsSource = ThemeManager.Themes };
    private readonly ComboBox _accent = new() { ItemsSource = ThemeManager.Accents };
    private readonly ObservableCollection<Item> _widgets = new();
    private readonly ObservableCollection<Item> _gauges = new();
    private readonly ObservableCollection<Item> _hud = new();
    private readonly Slider _hudOpacity = new() { Minimum = 0.3, Maximum = 1.0, SmallChange = 0.05 };
    private readonly ListBox _widgetList = new() { Height = 150 };
    private readonly ListBox _hudList = new() { Height = 210 };

    /// <summary>Raised on Apply so the main window can update live.</summary>
    public event EventHandler? Applied;

    public LayoutWindow(AiSettings settings)
    {
        _s = settings;
        Title = "Layout & Theme";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "CardBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");

        _theme.SelectedItem = ThemeManager.Themes.Contains(_s.Theme) ? _s.Theme : ThemeManager.Themes[0];
        _accent.SelectedItem = ThemeManager.Accents.Contains(_s.Accent) ? _s.Accent : ThemeManager.Accents[0];
        foreach (var id in Ordered(_s.WidgetOrder, AiSettings.DefaultWidgetOrder))
            _widgets.Add(new Item { Id = id, Visible = !_s.HiddenWidgets.Contains(id) });
        foreach (var id in AiSettings.GaugeCards)
            _gauges.Add(new Item { Id = id, Visible = !_s.HiddenWidgets.Contains("Gauge:" + id) });
        foreach (var id in Ordered(_s.HudRows, AiSettings.AllHudRows))
            _hud.Add(new Item { Id = id, Visible = _s.HudRows.Contains(id) });
        _hudOpacity.Value = _s.HudOpacity;

        var form = new StackPanel { Margin = new Thickness(16) };
        form.Children.Add(Header("Theme"));
        form.Children.Add(Row("Colors", _theme));
        form.Children.Add(Row("Accent", _accent));

        form.Children.Add(Header("Dashboard widgets (top to bottom)"));
        _widgetList.ItemsSource = _widgets;
        _widgetList.ItemTemplate = CheckTemplate();
        form.Children.Add(WithMoveButtons(_widgetList, _widgets));
        var gaugePanel = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        gaugePanel.Children.Add(new TextBlock { Text = "Gauge cards: ", VerticalAlignment = VerticalAlignment.Center });
        foreach (var g in _gauges)
        {
            var cb = new CheckBox { Content = g.Id, IsChecked = g.Visible, Margin = new Thickness(0, 0, 10, 0) };
            cb.Checked += (_, _) => g.Visible = true;
            cb.Unchecked += (_, _) => g.Visible = false;
            gaugePanel.Children.Add(cb);
        }
        form.Children.Add(gaugePanel);

        form.Children.Add(Header("HUD overlay rows (top to bottom)"));
        _hudList.ItemsSource = _hud;
        _hudList.ItemTemplate = CheckTemplate();
        form.Children.Add(WithMoveButtons(_hudList, _hud));
        form.Children.Add(Row("HUD opacity", _hudOpacity));
        form.Children.Add(new TextBlock
        {
            Text = "Use \"Move/Resize HUD\" on the main window to drag and resize the overlay.",
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 4, 0, 0)
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(Button("Reset", (_, _) => Reset()));
        buttons.Children.Add(Button("Apply", (_, _) => Save()));
        buttons.Children.Add(Button("OK", (_, _) => { Save(); DialogResult = true; }));
        form.Children.Add(buttons);
        Content = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 860 };
    }

    private static IEnumerable<string> Ordered(IEnumerable<string> saved, IEnumerable<string> all) =>
        saved.Where(all.Contains).Concat(all.Where(a => !saved.Contains(a))).Distinct();

    private void Save()
    {
        _s.Theme = _theme.SelectedItem as string ?? "Midnight";
        _s.Accent = _accent.SelectedItem as string ?? "Amber";
        _s.WidgetOrder = _widgets.Select(w => w.Id).ToList();
        _s.HiddenWidgets = _widgets.Where(w => !w.Visible).Select(w => w.Id)
            .Concat(_gauges.Where(g => !g.Visible).Select(g => "Gauge:" + g.Id)).ToList();
        _s.HudRows = _hud.Where(h => h.Visible).Select(h => h.Id).ToList();
        _s.HudOpacity = _hudOpacity.Value;
        try { _s.Save(); } catch { }
        Applied?.Invoke(this, EventArgs.Empty);
    }

    private void Reset()
    {
        _s.Theme = "Midnight"; _s.Accent = "Amber";
        _s.WidgetOrder = new(AiSettings.DefaultWidgetOrder);
        _s.HiddenWidgets = new();
        _s.HudRows = new(AiSettings.DefaultHudRows);
        _s.HudOpacity = 0.85;
        try { _s.Save(); } catch { }
        Applied?.Invoke(this, EventArgs.Empty);
        DialogResult = true;
    }

    private static DataTemplate CheckTemplate()
    {
        var cb = new FrameworkElementFactory(typeof(CheckBox));
        cb.SetBinding(ContentControl.ContentProperty, new System.Windows.Data.Binding("Id"));
        cb.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding("Visible") { Mode = System.Windows.Data.BindingMode.TwoWay });
        return new DataTemplate { VisualTree = cb };
    }

    private static FrameworkElement WithMoveButtons(ListBox list, ObservableCollection<Item> items)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var side = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        side.Children.Add(Button("Up", (_, _) => Move(list, items, -1)));
        side.Children.Add(Button("Down", (_, _) => Move(list, items, +1)));
        Grid.SetColumn(side, 1);
        grid.Children.Add(list);
        grid.Children.Add(side);
        return grid;
    }

    private static void Move(ListBox list, ObservableCollection<Item> items, int delta)
    {
        int i = list.SelectedIndex;
        int j = i + delta;
        if (i < 0 || j < 0 || j >= items.Count) return;
        items.Move(i, j);
        list.SelectedIndex = j;
    }

    private static TextBlock Header(string text)
    {
        var t = new TextBlock { Text = text, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 12, 0, 6) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        return t;
    }

    private static FrameworkElement Row(string label, Control input)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(input, 1);
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        grid.Children.Add(input);
        return grid;
    }

    private static Button Button(string text, RoutedEventHandler onClick)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 4) };
        b.Click += onClick;
        return b;
    }
}
