using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace WeChatJevHud.TypeSafe.Tests;

public class TransportTests
{
    internal static JevSemanticState State => new(new("remote", "你到家了吗？"), [], "zh-CN", false);

    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return send(request, cancellationToken); }
    }

    internal static string ValidResponse()
    {
        var answers = JevQuestionSet.NoulIds.ToDictionary(id => id, _ => (object)new { type = "noul", noul = 0.8 });
        answers["speech_act"] = new
        {
            type = "choice",
            choice = "question",
            confidence = 1.0,
            probabilities = JevQuestionSet.SpeechActs.Keys.ToDictionary(k => k, k => k == "question" ? 1.0 : 0.0)
        };
        object Score(ImmutableArray<string> levels) => new
        {
            type = "score",
            score = 0.5,
            confidence = 0.25,
            legend = levels.Select((v, i) => (v, i)).ToDictionary(x => x.i.ToString(), x => x.v),
            probabilities = new Dictionary<string, double> { ["0"] = 0.5, ["1"] = 0.5, ["2"] = 0, ["3"] = 0 }
        };
        answers["urgency"] = Score(JevQuestionSet.UrgencyLevels);
        answers["emotional_intensity"] = Score(JevQuestionSet.IntensityLevels);
        return JsonSerializer.Serialize(new { model = "jev-1.13.0", answers, usage = new { input_tokens = 600, output_tokens = 100 } });
    }

    [Fact]
    public async Task One_shared_state_request_maps_all_eight_typed_answers_without_conflating_confidence()
    {
        using var handler = new Handler(async (request, token) =>
        {
            Assert.Equal("https://api.typesafe.ai/v1/systemone", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-secret", request.Headers.Authorization.Parameter);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("jev-latest", json.RootElement.GetProperty("model").GetString());
            Assert.Equal(8, json.RootElement.GetProperty("questions").EnumerateObject().Count());
            Assert.Equal("你到家了吗？", json.RootElement.GetProperty("state").GetProperty("current_message").GetProperty("text").GetString());
            return new(HttpStatusCode.OK) { Content = new StringContent(ValidResponse(), Encoding.UTF8, "application/json") };
        });
        using var http = new HttpClient(handler);
        var result = await new TypeSafeHttpClient(http, "test-secret").EvaluateAsync(State, new(), default);
        Assert.Equal(JevStatus.Success, result.Status);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0.8, result.Judgments!.ExpectsResponse.YesProbability);
        Assert.Equal(0.5, result.Judgments.Urgency.Score);
        Assert.Equal(0.25, result.Judgments.Urgency.DistributionConfidence);
        Assert.Equal(1, result.Judgments.SpeechAct.Probabilities["question"]);
        Assert.Equal(600, result.InputTokens);
        Assert.True(result.RequestBytes > 0 && result.ResponseBytes > 0);
    }

    [Theory]
    [InlineData(401, JevStatus.Unauthorized)]
    [InlineData(403, JevStatus.Unauthorized)]
    [InlineData(429, JevStatus.RateLimited)]
    [InlineData(529, JevStatus.ServiceUnavailable)]
    [InlineData(500, JevStatus.ServiceUnavailable)]
    [InlineData(422, JevStatus.MalformedResponse)]
    public async Task Http_failure_is_classified_without_retry_or_sensitive_error_body(int code, JevStatus status)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code)
        { Content = new StringContent("test-secret PRIVATE CHAT") }));
        using var http = new HttpClient(handler);
        var result = await new TypeSafeHttpClient(http, "test-secret").EvaluateAsync(State, new(), default);
        Assert.Equal(status, result.Status);
        Assert.Null(result.Judgments);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain("test-secret", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("PRIVATE CHAT", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("range")]
    [InlineData("distribution")]
    [InlineData("type")]
    [InlineData("legend")]
    [InlineData("truncated")]
    [InlineData("oversized")]
    [InlineData("duplicate")]
    public async Task Invalid_output_is_rejected_without_fabricated_judgments(string defect)
    {
        var json = ValidResponse();
        json = defect switch
        {
            "missing" => json.Replace("expects_response", "unexpected_id"),
            "range" => json.Replace("\"noul\":0.8", "\"noul\":1.2"),
            "distribution" => json.Replace("\"question\":1", "\"question\":0.2"),
            "type" => json.Replace("\"type\":\"score\"", "\"type\":\"noul\""),
            "legend" => json.Replace("No timing pressure is expressed.", "secret text"),
            "truncated" => "{",
            "oversized" => new string('x', 140000),
            "duplicate" => json.Replace("\"model\":", "\"model\":\"duplicate\",\"model\":"),
            _ => throw new InvalidOperationException(),
        };
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(json) }));
        using var http = new HttpClient(handler);
        var result = await new TypeSafeHttpClient(http, "test-secret").EvaluateAsync(State, new(), default);
        Assert.Equal(JevStatus.MalformedResponse, result.Status);
        Assert.Null(result.Judgments);
    }

    [Fact]
    public async Task Missing_key_makes_no_network_call()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("must not call"));
        using var http = new HttpClient(handler);
        var result = await new TypeSafeHttpClient(http, null).EvaluateAsync(State, new(), default);
        Assert.Equal(JevStatus.NotConfigured, result.Status);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Timeout_and_cancellation_are_distinct_and_network_exceptions_are_redacted()
    {
        using var handler = new Handler(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return new(HttpStatusCode.OK); });
        using var http = new HttpClient(handler);
        var client = new TypeSafeHttpClient(http, "test-secret", TimeSpan.FromMilliseconds(30));
        Assert.Equal(JevStatus.Timeout, (await client.EvaluateAsync(State, new(), default)).Status);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Equal(JevStatus.Cancelled, (await client.EvaluateAsync(State, new(), cancellation.Token)).Status);
        using var failure = new Handler((_, _) => throw new HttpRequestException("test-secret"));
        using var failureHttp = new HttpClient(failure);
        var result = await new TypeSafeHttpClient(failureHttp, "test-secret").EvaluateAsync(State, new(), default);
        Assert.Equal(JevStatus.ServiceUnavailable, result.Status);
        Assert.DoesNotContain("test-secret", JsonSerializer.Serialize(result));
    }
}
