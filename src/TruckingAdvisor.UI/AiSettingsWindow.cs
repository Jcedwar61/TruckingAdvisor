using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TruckingAdvisor.AI;
using TruckingAdvisor.Shared.Models;

namespace TruckingAdvisor.UI;

/// <summary>
/// AI &amp; radio settings. The API key goes straight to DPAPI storage and is never shown again.
/// </summary>
public sealed class AiSettingsWindow : Window
{
    private readonly AiSettings _s;
    private readonly OpenAiClientService _client;
    private readonly VirtualCompanyProfile _company;

    private readonly CheckBox _enabled = new() { Content = "Enable AI (CB truckers, dispatcher, voice)" };
    private readonly PasswordBox _key = new();
    private readonly TextBlock _keyStatus = new();
    private readonly TextBox _chat = new(), _stt = new(), _tts = new(), _maxTokens = new(), _requestLimit = new(), _spendLimit = new();
    private readonly TextBox _priceIn = new(), _priceOut = new(), _priceStt = new(), _priceTts = new();
    private readonly TextBox _handle = new(), _companyName = new();
    private readonly CheckBox _mic = new() { Content = "Use my microphone for push-to-talk (recorded only while the PTT key is held)" };
    private readonly CheckBox _openAiVoices = new() { Content = "Use OpenAI voices (costs more; off = free Windows voices)" };
    private readonly ComboBox _activity = new();
    private readonly TextBlock _usage = new() { TextWrapping = TextWrapping.Wrap };

    public AiSettingsWindow(AiSettings settings, OpenAiClientService client, VirtualCompanyProfile company)
    {
        _s = settings;
        _client = client;
        _company = company;
        Title = "AI & Radio Settings";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x15, 0x1C, 0x28));
        Foreground = Brushes.White;

        var form = new StackPanel { Margin = new Thickness(16) };
        form.Children.Add(Header("Driver"));
        form.Children.Add(Row("Your CB handle", _handle));
        form.Children.Add(Row("Company name", _companyName));

        form.Children.Add(Header("OpenAI"));
        form.Children.Add(_enabled);
        form.Children.Add(Row("API key", _key));
        var keyButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(150, 4, 0, 0) };
        keyButtons.Children.Add(Button("Save key (encrypted)", SaveKey));
        keyButtons.Children.Add(Button("Delete saved key", DeleteKey));
        form.Children.Add(keyButtons);
        form.Children.Add(Indent(_keyStatus));
        form.Children.Add(Row("Chat model", _chat));
        form.Children.Add(Row("Speech-to-text model", _stt));
        form.Children.Add(Row("Text-to-speech model", _tts));
        form.Children.Add(Row("Max reply tokens", _maxTokens));
        form.Children.Add(_mic);
        form.Children.Add(_openAiVoices);
        _activity.ItemsSource = Enum.GetValues<RadioActivitySetting>();
        form.Children.Add(Row("Background radio activity", _activity));

        form.Children.Add(Header("Spending (estimates; your OpenAI dashboard is the source of truth)"));
        form.Children.Add(Row("Daily request limit", _requestLimit));
        form.Children.Add(Row("Daily spend limit (USD)", _spendLimit));
        form.Children.Add(Indent(new TextBlock
        {
            Text = "Enter prices from openai.com/api/pricing to see cost estimates. With no prices, only requests and tokens are counted.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray, FontSize = 11
        }));
        form.Children.Add(Row("Chat input $ / 1M tokens", _priceIn));
        form.Children.Add(Row("Chat output $ / 1M tokens", _priceOut));
        form.Children.Add(Row("Transcription $ / minute", _priceStt));
        form.Children.Add(Row("Voice $ / 1M characters", _priceTts));
        form.Children.Add(Indent(_usage));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(Button("Save", (_, _) => SaveAndClose()));
        buttons.Children.Add(Button("Cancel", (_, _) => { DialogResult = false; }));
        form.Children.Add(buttons);

        Content = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 820 };
        LoadValues();
    }

    private void LoadValues()
    {
        var inv = CultureInfo.InvariantCulture;
        _handle.Text = _company.PlayerHandle;
        _companyName.Text = _company.CompanyName;
        _enabled.IsChecked = _s.GlobalAiEnabled;
        _chat.Text = _s.ChatModel;
        _stt.Text = _s.SpeechToTextModel;
        _tts.Text = _s.TextToSpeechModel;
        _maxTokens.Text = _s.MaxResponseTokens.ToString(inv);
        _mic.IsChecked = _s.MicrophoneEnabled;
        _openAiVoices.IsChecked = _s.UseOpenAiVoices;
        _activity.SelectedItem = _s.RadioActivity;
        _requestLimit.Text = _s.DailyRequestLimit.ToString(inv);
        _spendLimit.Text = _s.DailySpendLimitUsd.ToString(inv);
        _priceIn.Text = _s.ChatInputPricePerMillionTokens.ToString(inv);
        _priceOut.Text = _s.ChatOutputPricePerMillionTokens.ToString(inv);
        _priceStt.Text = _s.SpeechToTextPricePerMinute.ToString(inv);
        _priceTts.Text = _s.TextToSpeechPricePerMillionChars.ToString(inv);
        _keyStatus.Text = ApiKeyStore.Exists(_s.Provider) ? "A key is saved (encrypted for your Windows account)." : "No key saved.";
        var u = _client.Usage.Data;
        _usage.Text = $"Today: {u.Today.Requests} requests, {u.Today.InputTokens + u.Today.OutputTokens:N0} tokens, ~${u.Today.EstimatedCostUsd:F4}\n" +
                      $"All time: {u.Lifetime.Requests} requests, {u.Lifetime.InputTokens + u.Lifetime.OutputTokens:N0} tokens, ~${u.Lifetime.EstimatedCostUsd:F4}" +
                      (_client.LastError is { } err ? $"\nLast error: {err}" : string.Empty);
    }

    private void SaveKey(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_key.Password)) { _keyStatus.Text = "Paste a key first."; return; }
        ApiKeyStore.Save(_key.Password, _s.Provider);
        _key.Clear();
        _keyStatus.Text = "Key saved (encrypted for your Windows account).";
    }

    private void DeleteKey(object sender, RoutedEventArgs e)
    {
        ApiKeyStore.Delete(_s.Provider);
        _keyStatus.Text = "Saved key deleted.";
    }

    private void SaveAndClose()
    {
        var inv = CultureInfo.InvariantCulture;
        _company.PlayerHandle = _handle.Text.Trim();
        if (!string.IsNullOrWhiteSpace(_companyName.Text)) _company.CompanyName = _companyName.Text.Trim();
        _s.GlobalAiEnabled = _enabled.IsChecked == true;
        _s.ChatModel = NonEmpty(_chat.Text, _s.ChatModel);
        _s.SpeechToTextModel = NonEmpty(_stt.Text, _s.SpeechToTextModel);
        _s.TextToSpeechModel = NonEmpty(_tts.Text, _s.TextToSpeechModel);
        _s.MicrophoneEnabled = _mic.IsChecked == true;
        _s.UseOpenAiVoices = _openAiVoices.IsChecked == true;
        if (_activity.SelectedItem is RadioActivitySetting a) _s.RadioActivity = a;
        if (int.TryParse(_maxTokens.Text, NumberStyles.Integer, inv, out var mt)) _s.MaxResponseTokens = Math.Clamp(mt, 32, 1000);
        if (int.TryParse(_requestLimit.Text, NumberStyles.Integer, inv, out var rl)) _s.DailyRequestLimit = Math.Max(0, rl);
        _s.DailySpendLimitUsd = Dec(_spendLimit.Text, _s.DailySpendLimitUsd);
        _s.ChatInputPricePerMillionTokens = Dec(_priceIn.Text, _s.ChatInputPricePerMillionTokens);
        _s.ChatOutputPricePerMillionTokens = Dec(_priceOut.Text, _s.ChatOutputPricePerMillionTokens);
        _s.SpeechToTextPricePerMinute = Dec(_priceStt.Text, _s.SpeechToTextPricePerMinute);
        _s.TextToSpeechPricePerMillionChars = Dec(_priceTts.Text, _s.TextToSpeechPricePerMillionChars);
        _s.Save();
        DialogResult = true;
    }

    private static string NonEmpty(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static decimal Dec(string text, decimal fallback) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) && d >= 0 ? d : fallback;

    private static TextBlock Header(string text) => new()
    {
        Text = text, FontWeight = FontWeights.Bold, Foreground = Brushes.Orange, Margin = new Thickness(0, 12, 0, 6)
    };

    private static FrameworkElement Indent(FrameworkElement e)
    {
        e.Margin = new Thickness(150, 2, 0, 2);
        return e;
    }

    private static FrameworkElement Row(string label, Control input)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var tb = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(input, 1);
        grid.Children.Add(tb);
        grid.Children.Add(input);
        return grid;
    }

    private static Button Button(string text, RoutedEventHandler onClick)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0) };
        b.Click += onClick;
        return b;
    }
}
