using TruckingAdvisor.AI;
using TruckingAdvisor.Shared.Enums;
using TruckingAdvisor.Shared.Interfaces;
using TruckingAdvisor.Shared.Models;
using Xunit;

namespace TruckingAdvisor.Core.Tests;

/// <summary>Calling one CB contact, then another while the first is still talking (the reported crash path).</summary>
public class ContactCallTests
{
    private sealed class SlowAudio : IAudioPriorityManager
    {
        public readonly List<AudioItem> Items = new();
        public bool IsRadioChannelBusy { get; set; }
        private CancellationTokenSource _stop = new();
        public void EnqueueAnnouncement(string text, AudioPriority p, string v = "advisor", bool fx = false) => Enqueue(new AudioItem(text, p, v, fx));
        public void Enqueue(AudioItem item) { lock (Items) Items.Add(item); }
        public async Task EnqueueAndWaitAsync(AudioItem item, CancellationToken ct = default)
        {
            Enqueue(item);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
            try { await Task.Delay(300, linked.Token); } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        public void SetChannelVolume(string c, float v) { }
        public void StopAll() { _stop.Cancel(); _stop = new CancellationTokenSource(); }
    }

    private sealed class Llm : ILlmClient
    {
        public bool IsAvailable => true;
        public async Task<LlmResult?> CompleteAsync(string s, string u, int max, CancellationToken ct)
        {
            await Task.Delay(50, ct);
            return new LlmResult("Ten-four, good buddy.", new LlmUsage(1, 1));
        }
    }

    [Fact]
    public async Task CallingADifferentContact_WhileTheFirstIsTalking_DoesNotThrow_AndBothAnswer()
    {
        var audio = new SlowAudio();
        var roster = PersonalityCatalog.CreateDefaultRoster();
        var radio = new CBRadioService(roster, new VirtualCompanyProfile { PlayerHandle = "Ghost" }, audio, () => new AtsTelemetrySnapshot(),
            new AiSettings(), new Llm(), random: new Random(1)) { ReplyPause = (0, 0) };
        var notices = new List<string>();
        radio.Notice += (_, n) => notices.Add(n);
        var errors = new List<Exception>();

        // Something reading the roster and memories constantly, like the contacts list does on the UI thread.
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
                try { foreach (var t in radio.Roster) _ = t.MemoriesSnapshot(); }
                catch (Exception ex) { lock (errors) errors.Add(ex); }
        });

        radio.BeginPushToTalk();
        var first = radio.EndPushToTalkAsync("Breaker one-nine, Big Red, you got a copy?");
        await Task.Delay(120);
        radio.BeginPushToTalk();                                  // interrupt: call someone else
        var second = radio.EndPushToTalkAsync("Breaker one-nine, Highway Hank, you got a copy?");
        await Task.WhenAll(first, second);
        for (int i = 0; i < 20; i++)
        {
            radio.BeginPushToTalk();
            await radio.EndPushToTalkAsync(i % 2 == 0 ? "Diesel Dave, you there?" : "Midnight Mike, come back");
        }
        stop.Cancel();
        await reader;

        Assert.Empty(notices.Where(n => n.StartsWith("Radio error")));
        Assert.Empty(errors);
        Assert.Contains(audio.Items, i => i.Text.Contains("good buddy"));
        Assert.Equal(CBRadioState.RadioIdle, radio.CurrentState);
    }
}
