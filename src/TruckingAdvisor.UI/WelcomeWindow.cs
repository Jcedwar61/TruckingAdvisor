using System.Windows;
using System.Windows.Controls;
using TruckingAdvisor.AI;
using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Models;

namespace TruckingAdvisor.UI;

/// <summary>First-run setup: who you are on the radio, your company, and how your co-driver talks.</summary>
public sealed class WelcomeWindow : Window
{
    private readonly TextBox _handle = new(), _company = new();
    private readonly ComboBox _coDriver = new() { ItemsSource = Enum.GetValues<CoDriverPersonality>() };
    private readonly AiSettings _settings;
    private readonly VirtualCompanyProfile _profile;

    /// <summary>True when the player asked to go straight to AI setup.</summary>
    public bool OpenAiSettings { get; private set; }

    public WelcomeWindow(AiSettings settings, VirtualCompanyProfile profile)
    {
        _settings = settings;
        _profile = profile;
        Title = "Welcome to TruckingAdvisor";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "CardBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");

        _handle.Text = profile.PlayerHandle;
        _company.Text = profile.CompanyName;
        _coDriver.SelectedItem = settings.CoDriver;

        var form = new StackPanel { Margin = new Thickness(24) };
        var title = new TextBlock { Text = "Welcome, driver.", FontSize = 26, FontWeight = FontWeights.Bold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        form.Children.Add(title);
        form.Children.Add(Para("TruckingAdvisor rides along in American Truck Simulator: live gauges, a driving coach, delivery scores, a career, and a CB radio full of truckers who remember you."));

        form.Children.Add(Label("Your CB handle (what truckers call you)"));
        form.Children.Add(_handle);
        form.Children.Add(Label("Your trucking company's name"));
        form.Children.Add(_company);
        form.Children.Add(Label("Co-driver voice style"));
        form.Children.Add(_coDriver);

        form.Children.Add(Para("Good to know:\n•  Hold LEFT ALT to talk on the CB (or type in the radio box).\n•  F8 shows the in-game HUD.  ]  changes channel.  F10 calls dispatch.\n•  Everything works offline. Add an OpenAI key later for real AI conversations.\n•  Say \"TruckingAdvisor, what's my fuel range?\" for voice commands."));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ai = new Button { Content = "Set up AI voices now", Margin = new Thickness(0, 0, 8, 0) };
        ai.Click += (_, _) => { OpenAiSettings = true; Save(); };
        var go = new Button { Content = "Let's roll", IsDefault = true };
        go.SetResourceReference(BackgroundProperty, "AccentBrush");
        go.SetResourceReference(ForegroundProperty, "BgBrush");
        go.Click += (_, _) => Save();
        buttons.Children.Add(ai);
        buttons.Children.Add(go);
        form.Children.Add(buttons);
        Content = form;
    }

    private void Save()
    {
        _profile.PlayerHandle = _handle.Text.Trim();
        if (!string.IsNullOrWhiteSpace(_company.Text)) _profile.CompanyName = _company.Text.Trim();
        if (_coDriver.SelectedItem is CoDriverPersonality p) _settings.CoDriver = p;
        DialogResult = true;
    }

    private static TextBlock Para(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8), LineHeight = 20 };

    private static TextBlock Label(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(0, 10, 0, 4) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return t;
    }
}
