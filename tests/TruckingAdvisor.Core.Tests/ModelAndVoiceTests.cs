using System.Net;
using System.Text;
using System.Text.Json;
using TruckingAdvisor.AI;
using Xunit;

namespace TruckingAdvisor.Core.Tests;

public class ModelAndVoiceTests
{
    /// <summary>A fake OpenAI: some models "don't exist", the rest answer.</summary>
    private sealed class FakeOpenAi : HttpMessageHandler
    {
        public HashSet<string> Missing = new();
        public HashSet<string> EmptyReply = new();
        public HashSet<string>? Listed;
        public HttpStatusCode? ForceStatus;
        public readonly List<string> ChatModelsTried = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string url = request.RequestUri!.ToString();
            if (url.EndsWith("/models"))
                return Json(HttpStatusCode.OK, new { data = (Listed ?? new HashSet<string>()).Select(id => new { id }) });
            if (ForceStatus is { } s) return Json(s, new { error = new { message = "nope", code = "x" } });
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            string model = JsonDocument.Parse(body).RootElement.GetProperty("model").GetString()!;
            ChatModelsTried.Add(model);
            if (Missing.Contains(model))
                return Json(HttpStatusCode.NotFound, new { error = new { message = $"The model `{model}` does not exist or you do not have access to it.", code = "model_not_found" } });
            string content = EmptyReply.Contains(model) ? "" : $"Ten-four from {model}.";
            return Json(HttpStatusCode.OK, new { choices = new[] { new { message = new { content } } }, usage = new { prompt_tokens = 10, completion_tokens = 5 } });
        }

        private static HttpResponseMessage Json(HttpStatusCode code, object o) =>
            new(code) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };
    }

    private static (OpenAiClientService Client, FakeOpenAi Fake, AiSettings Settings) Make(AiQuality q = AiQuality.Balanced)
    {
        var fake = new FakeOpenAi();
        var settings = new AiSettings { GlobalAiEnabled = true, Quality = q };
        return (new OpenAiClientService(settings, new AiUsageTracker(), () => "sk-test", fake), fake, settings);
    }

    [Fact]
    public async Task RejectedModel_IsSkipped_AndRemembered()
    {
        var (client, fake, settings) = Make();
        fake.Missing.Add("gpt-5-mini");
        var r1 = await client.CompleteAsync("sys", "hi", 80, CancellationToken.None);
        Assert.Equal("Ten-four from gpt-4.1-mini.", r1!.Text);
        Assert.Equal("gpt-4.1-mini", settings.ChatModel);
        fake.ChatModelsTried.Clear();
        await client.CompleteAsync("sys", "hi again", 80, CancellationToken.None);
        Assert.Equal(new[] { "gpt-4.1-mini" }, fake.ChatModelsTried);   // didn't retry the missing one
    }

    [Fact]
    public async Task ReasoningModel_ThatRepliesEmpty_FallsThroughToTheNext()
    {
        var (client, fake, _) = Make();
        fake.EmptyReply.Add("gpt-5-mini");
        var r = await client.CompleteAsync("sys", "hi", 80, CancellationToken.None);
        Assert.Equal("Ten-four from gpt-4.1-mini.", r!.Text);
    }

    [Fact]
    public async Task BadKey_DoesNotBurnThroughTheModelList()
    {
        var (client, fake, _) = Make();
        fake.ForceStatus = HttpStatusCode.Unauthorized;
        Assert.Null(await client.CompleteAsync("sys", "hi", 80, CancellationToken.None));
        Assert.Contains("401", client.LastError);
        fake.ForceStatus = null;
        Assert.NotNull(await client.CompleteAsync("sys", "hi", 80, CancellationToken.None)); // first model still usable
    }

    [Fact]
    public async Task ModelList_FromTheAccount_PicksTheBestAvailable()
    {
        var (client, fake, settings) = Make(AiQuality.Best);
        fake.Listed = new HashSet<string> { "gpt-4o", "gpt-4o-mini", "whisper-1", "tts-1", "gpt-4o-mini-realtime-preview" };
        await client.RefreshModelsAsync();
        Assert.Equal("gpt-4o", settings.ChatModel);
        Assert.Equal("whisper-1", settings.SpeechToTextModel);
        Assert.Equal("tts-1", settings.TextToSpeechModel);
        Assert.Equal("gpt-4o-mini-realtime-preview", settings.RealtimeModel);
    }

    [Theory]
    [InlineData(404, "{}", true)]
    [InlineData(400, "{\"error\":{\"code\":\"model_not_found\"}}", true)]
    [InlineData(400, "{\"error\":{\"message\":\"Unsupported parameter: 'temperature' is not supported with this model.\",\"code\":\"unsupported_parameter\"}}", true)]
    [InlineData(403, "{\"error\":{\"message\":\"Project does not have access to model gpt-5\"}}", true)]
    [InlineData(401, "{}", false)]
    [InlineData(429, "{}", false)]
    [InlineData(500, "{}", false)]
    [InlineData(400, "{\"error\":{\"message\":\"Invalid 'messages[0].content'\"}}", false)]
    public void ModelProblems_AreToldApartFromOtherErrors(int status, string body, bool expected) =>
        Assert.Equal(expected, OpenAiClientService.IsModelProblem(status, body));

    [Fact]
    public void EveryQuality_HasABuiltInModelList_AndNothingToType()
    {
        foreach (var q in Enum.GetValues<AiQuality>()) Assert.True(AiModelCatalog.Chat(q).Count >= 3);
        Assert.Contains("whisper-1", AiModelCatalog.Transcription);
        Assert.Equal(AiQuality.Balanced, new AiSettings().Quality);
    }

    [Fact]
    public void EachRegular_HasTheirOwnNeuralVoice_AndNewcomersGetOneToo()
    {
        var roster = PersonalityCatalog.CreateDefaultRoster();
        var voices = roster.Select(PersonalityCatalog.NeuralVoiceFor).ToList();
        Assert.All(voices, v => Assert.EndsWith("Neural", v.Split('|')[0]));
        Assert.Equal(voices.Count, voices.Select(v => v.Split('|')[0]).Distinct().Count());
        var newcomer = PersonalityCatalog.GenerateDynamicTrucker(3);
        Assert.NotEmpty(PersonalityCatalog.NeuralVoiceFor(newcomer));
        var oldSave = new TruckingAdvisor.Shared.Models.TruckerPersonality { Handle = "Old Timer" }; // saved before voices existed
        Assert.Equal(PersonalityCatalog.NeuralVoiceFor(oldSave), PersonalityCatalog.NeuralVoiceFor(oldSave));
        Assert.Equal(TruckingAdvisor.Shared.Enums.VoiceEngine.FreeNeural, new AiSettings().VoiceEngine);
    }
}
