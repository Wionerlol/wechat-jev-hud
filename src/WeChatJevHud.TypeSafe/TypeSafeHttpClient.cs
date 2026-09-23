using System.Collections.Immutable;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace WeChatJevHud.TypeSafe;

/// <summary>HTTP v1 contract consulted 2026-09-24. Never exposes error bodies or exceptions.</summary>
public sealed class TypeSafeHttpClient : ITypeSafeClient
{
    private readonly HttpClient _http;
    private readonly string? _key;
    private readonly TimeSpan _timeout;
    private const int MaxResponseBytes = 128 * 1024;

    public TypeSafeHttpClient(HttpClient http, string? apiKey, TimeSpan? timeout = null)
    {
        _http = http;
        _key = apiKey;
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    // No redirects carrying credentials to another origin. The caller owns lifetime.
    public static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    public async Task<TypeSafeEvaluation> EvaluateAsync(JevSemanticState state, JevQuestionSet questions,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_key)) return new(JevStatus.NotConfigured);
        var stateJson = JevContextBuilder.SerializeState(state);
        if (Encoding.UTF8.GetByteCount(stateJson) > 16000) return new(JevStatus.InputTooLarge);
        using var stateDocument = JsonDocument.Parse(stateJson);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            state = stateDocument.RootElement,
            model = "jev-latest",
            questions = questions.ToProtocolQuestions(),
        }, JevContextBuilder.JsonOptions);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        var stopwatch = Stopwatch.StartNew();
        var responseBytes = 0;
        var sent = 0;
        TypeSafeEvaluation Failure(JevStatus status) => new(status, RequestBytes: payload.Length,
            ResponseBytes: responseBytes, RoundtripMs: stopwatch.Elapsed.TotalMilliseconds, RequestCount: sent);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.typesafe.ai/v1/systemone");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new("application/json");
            sent = 1;
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Failure((int)response.StatusCode switch
                {
                    401 or 403 => JevStatus.Unauthorized,
                    429 => JevStatus.RateLimited,
                    422 or 400 => JevStatus.MalformedResponse,
                    _ => JevStatus.ServiceUnavailable,
                }); // No automatic resubmission; never read/print potentially sensitive error bodies.

            using var body = new MemoryStream();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var buffer = new byte[4096];
            int count;
            while ((count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
            {
                responseBytes += count;
                if (responseBytes > MaxResponseBytes) return Failure(JevStatus.MalformedResponse);
                body.Write(buffer, 0, count);
            }
            var roundtrip = stopwatch.Elapsed.TotalMilliseconds;
            var mapping = Stopwatch.StartNew();
            using var document = JsonDocument.Parse(body.ToArray());
            var result = Map(document.RootElement);
            return result with
            {
                RequestBytes = payload.Length,
                ResponseBytes = responseBytes,
                RoundtripMs = roundtrip,
                MappingMs = mapping.Elapsed.TotalMilliseconds,
                RequestCount = sent
            };
        }
        catch (OperationCanceledException)
        { return Failure(cancellationToken.IsCancellationRequested ? JevStatus.Cancelled : JevStatus.Timeout); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException)
        { return Failure(JevStatus.MalformedResponse); }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        { return Failure(JevStatus.ServiceUnavailable); }
    }

    private static TypeSafeEvaluation Map(JsonElement root)
    {
        RejectDuplicateKeys(root);
        var model = root.GetProperty("model").GetString();
        if (string.IsNullOrWhiteSpace(model) || model.Length > 80 || model.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_'))
            throw new JsonException();
        var answers = root.GetProperty("answers");
        var expected = JevQuestionSet.NoulIds.Concat(["speech_act", "urgency", "emotional_intensity"]).ToHashSet(StringComparer.Ordinal);
        if (!expected.SetEquals(answers.EnumerateObject().Select(p => p.Name))) throw new JsonException();
        NoulJudgment Noul(string id)
        {
            var answer = answers.GetProperty(id);
            Type(answer, "noul");
            return new(Probability(answer.GetProperty("noul")));
        }
        var choice = answers.GetProperty("speech_act");
        Type(choice, "choice");
        var selected = choice.GetProperty("choice").GetString()!;
        var distribution = Distribution(choice.GetProperty("probabilities"), JevQuestionSet.SpeechActs.Keys);
        if (!distribution.TryGetValue(selected, out var selectedProbability) || distribution.Values.Max() > selectedProbability + 0.000001)
            throw new JsonException();

        ScoreJudgment Score(string id, ImmutableArray<string> levels)
        {
            var answer = answers.GetProperty(id);
            Type(answer, "score");
            var keys = Enumerable.Range(0, levels.Length).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var probabilities = Distribution(answer.GetProperty("probabilities"), keys);
            var legend = answer.GetProperty("legend");
            if (!keys.ToHashSet().SetEquals(legend.EnumerateObject().Select(p => p.Name))) throw new JsonException();
            for (var i = 0; i < levels.Length; i++)
                if (legend.GetProperty(keys[i]).GetString() != levels[i]) throw new JsonException();
            var score = answer.GetProperty("score").GetDouble();
            var mean = probabilities.Sum(p => int.Parse(p.Key, System.Globalization.CultureInfo.InvariantCulture) * p.Value);
            if (!double.IsFinite(score) || score < 0 || score > levels.Length - 1 || Math.Abs(score - mean) > 0.03)
                throw new JsonException();
            return new(score, probabilities.ToImmutableDictionary(p => int.Parse(p.Key, System.Globalization.CultureInfo.InvariantCulture), p => p.Value),
                levels.Select((text, i) => (text, i)).ToImmutableDictionary(p => p.i, p => p.text), Probability(answer.GetProperty("confidence")));
        }

        var judgments = new JevJudgments(Noul(expectedId(0)), Noul(expectedId(1)), Noul(expectedId(2)), Noul(expectedId(3)), Noul(expectedId(4)),
            new(selected, distribution, Probability(choice.GetProperty("confidence"))),
            Score("urgency", JevQuestionSet.UrgencyLevels), Score("emotional_intensity", JevQuestionSet.IntensityLevels));
        var usage = root.GetProperty("usage");
        var input = usage.TryGetProperty("input_tokens", out var inputTokens) ? inputTokens.GetInt32() : (int?)null;
        var output = usage.TryGetProperty("output_tokens", out var outputTokens) ? outputTokens.GetInt32() : (int?)null;
        if (usage.ValueKind != JsonValueKind.Object || input < 0 || output < 0) throw new JsonException();
        return new(JevStatus.Success, judgments, model, InputTokens: input, OutputTokens: output);
        static string expectedId(int index) => JevQuestionSet.NoulIds[index];
    }

    private static void Type(JsonElement answer, string type)
    { if (answer.GetProperty("type").GetString() != type) throw new JsonException(); }
    private static double Probability(JsonElement element)
    {
        var value = element.GetDouble();
        if (!double.IsFinite(value) || value < 0 || value > 1) throw new JsonException();
        return value;
    }
    private static ImmutableDictionary<string, double> Distribution(JsonElement value, IEnumerable<string> keys)
    {
        var result = value.EnumerateObject().ToImmutableDictionary(p => p.Name, p => Probability(p.Value));
        if (!keys.ToHashSet(StringComparer.Ordinal).SetEquals(result.Keys) || Math.Abs(result.Values.Sum() - 1) > 0.03)
            throw new JsonException(); // Small serialization rounding allowance, not a display threshold.
        return result;
    }
    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException();
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateKeys(item);
    }
}
