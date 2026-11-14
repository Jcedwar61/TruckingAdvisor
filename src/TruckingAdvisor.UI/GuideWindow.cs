using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using TruckingAdvisor.Presentation;
using TruckingAdvisor.Shared.Input;

namespace TruckingAdvisor.UI;

/// <summary>
/// Built-in user guide: topics on the left, the page on the right, search on top.
/// Opened with F1, the ? button or More > User Guide; it's a separate window, so it takes no room on the dashboard.
/// </summary>
public sealed class GuideWindow : Window
{
    private readonly IReadOnlyList<GuideTopic> _topics;
    private readonly ListBox _list = new() { Width = 230, Margin = new Thickness(0, 0, 12, 0) };
    private readonly StackPanel _page = new() { Margin = new Thickness(4, 0, 12, 12) };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _search = new() { Margin = new Thickness(0, 0, 0, 10) };
    private string _query = "";

    private static GuideWindow? _open;

    /// <summary>Shows the guide (or brings the open one to the front), optionally on a topic.</summary>
    public static void ShowGuide(Window owner, InputBindingSet bindings, string? topic = null)
    {
        if (_open is { IsLoaded: true })
        {
            _open.Activate();
            if (topic is not null) _open.Select(topic);
            return;
        }
        _open = new GuideWindow(Guide.Build(bindings)) { Owner = owner };
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        if (topic is not null) _open.Select(topic);
    }

    private GuideWindow(IReadOnlyList<GuideTopic> topics)
    {
        _topics = topics;
        Title = "TruckingAdvisor Guide";
        Width = 960;
        Height = 700;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "CardBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");

        var root = new DockPanel { Margin = new Thickness(16) };

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var title = new TextBlock { Text = "GUIDE", FontSize = 20, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 10) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        DockPanel.SetDock(title, Dock.Left);
        top.Children.Add(title);
        _search.ToolTip = "Search the guide (e.g. \"dispatch\", \"fuel\", \"wheel\")";
        _search.TextChanged += (_, _) => { _query = _search.Text.Trim(); Refresh(); };
        top.Children.Add(_search);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var hint = new TextBlock { Text = "F1 opens this guide any time.  Esc closes it.", FontSize = 11, Margin = new Thickness(0, 8, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        DockPanel.SetDock(hint, Dock.Bottom);
        root.Children.Add(hint);

        _list.DisplayMemberPath = nameof(GuideTopic.Title);
        _list.SelectionChanged += (_, _) => ShowTopic(_list.SelectedItem as GuideTopic);
        DockPanel.SetDock(_list, Dock.Left);
        root.Children.Add(_list);

        _scroll.Content = _page;
        root.Children.Add(_scroll);
        Content = root;

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += (_, _) => _search.Focus();
        Refresh();
    }

    private void Select(string topic)
    {
        var t = _topics.FirstOrDefault(x => x.Title.Equals(topic, StringComparison.OrdinalIgnoreCase));
        if (t is null) return;
        _search.Text = "";
        _list.SelectedItem = t;
    }

    private void Refresh()
    {
        var current = _list.SelectedItem as GuideTopic;
        var shown = _topics.Where(t => t.Matches(_query)).ToList();
        _list.ItemsSource = shown;
        _list.SelectedItem = shown.Contains(current!) ? current : shown.FirstOrDefault();
        if (shown.Count == 0) ShowTopic(null);
    }

    private void ShowTopic(GuideTopic? topic)
    {
        _page.Children.Clear();
        _scroll.ScrollToTop();
        if (topic is null)
        {
            _page.Children.Add(Muted($"Nothing in the guide matches \"{_query}\"."));
            return;
        }
        var h = new TextBlock { Text = topic.Title, FontSize = 22, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 2) };
        _page.Children.Add(h);
        var sub = Muted(topic.Summary);
        sub.Margin = new Thickness(0, 0, 0, 12);
        _page.Children.Add(sub);

        foreach (var line in topic.Lines)
        {
            if (line.Length == 0) { _page.Children.Add(new Border { Height = 8 }); continue; }
            if (line.StartsWith("## "))
            {
                var t = new TextBlock { Text = line[3..].ToUpperInvariant(), FontWeight = FontWeights.Bold, FontSize = 12, Margin = new Thickness(0, 12, 0, 4) };
                t.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                _page.Children.Add(t);
            }
            else if (line.StartsWith("› "))
            {
                // Something to say or type: a quoted chip.
                var t = new TextBlock { Text = $"“{line[2..]}”", FontStyle = FontStyles.Italic, TextWrapping = TextWrapping.Wrap };
                var chip = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(12, 2, 0, 2), HorizontalAlignment = HorizontalAlignment.Left, Child = t };
                chip.SetResourceReference(Border.BackgroundProperty, "ButtonBrush");
                _page.Children.Add(chip);
            }
            else if (line.StartsWith("• "))
            {
                var g = new Grid { Margin = new Thickness(4, 2, 0, 2) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                var dot = new TextBlock { Text = "•" };
                dot.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                var body = Body(line[2..]);
                Grid.SetColumn(body, 1);
                g.Children.Add(dot);
                g.Children.Add(body);
                _page.Children.Add(g);
            }
            else if (line.Contains(" | "))
            {
                int bar = line.IndexOf(" | ", StringComparison.Ordinal);
                var g = new Grid { Margin = new Thickness(4, 2, 0, 2) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                var left = new TextBlock { Text = line[..bar], TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
                var right = Body(line[(bar + 3)..]);
                Grid.SetColumn(right, 1);
                g.Children.Add(left);
                g.Children.Add(right);
                _page.Children.Add(g);
            }
            else _page.Children.Add(Body(line));
        }
    }

    /// <summary>Paragraph text; the search term is highlighted.</summary>
    private TextBlock Body(string text)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        if (_query.Length < 2) { tb.Text = text; return tb; }
        int i = 0;
        while (i < text.Length)
        {
            int hit = text.IndexOf(_query, i, StringComparison.OrdinalIgnoreCase);
            if (hit < 0) { tb.Inlines.Add(new Run(text[i..])); break; }
            if (hit > i) tb.Inlines.Add(new Run(text[i..hit]));
            var mark = new Run(text.Substring(hit, _query.Length)) { FontWeight = FontWeights.Bold };
            mark.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
            tb.Inlines.Add(mark);
            i = hit + _query.Length;
        }
        return tb;
    }

    private static TextBlock Muted(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return t;
    }
}
