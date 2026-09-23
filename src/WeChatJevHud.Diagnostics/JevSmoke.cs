using System.Text.Json;
using WeChatJevHud.Core.Messages;
using WeChatJevHud.Observer;
using WeChatJevHud.Ocr;
using WeChatJevHud.TypeSafe;

namespace WeChatJevHud.Diagnostics;

internal static class JevSmoke
{
    // Synthetic, non-sensitive conversations. Not an OCR or live-WeChat acceptance claim.
    public static async Task<int> RunAsync()
    {
        using var http = TypeSafeHttpClient.CreateHttpClient();
        var client = new TypeSafeHttpClient(http, Environment.GetEnvironmentVariable("TYPESAFE_API_KEY"));
        await using var coordinator = new JevAnalysisCoordinator(new ConversationJudgmentService(client));
        coordinator.SetEpoch(1);
        (string? Prior, string Current)[] examples =
        [
            ("你到了吗", "到了"), (null, "你到家了吗？"), (null, "帮我把文件发一下"),
            ("我们周五见", "不是，我说的是周六"), (null, "今晚八点见"), (null, "哈哈哈哈哈"),
        ];
        for (var i = 0; i < examples.Length; i++)
        {
            var target = Message($"smoke-{i + 1}", examples[i].Current, MessageSide.Remote);
            ObservedMessage[] timeline = examples[i].Prior is { } prior
                ? [Message($"prior-{i + 1}", prior, MessageSide.Self), target] : [target];
            Console.WriteLine($"[JEV] fixture={i + 1} scheduling_status={coordinator.TrySchedule(timeline, target)}");
        }
        await coordinator.CompleteAsync();
        var results = coordinator.DrainResults();
        foreach (var result in results) Console.WriteLine(JevDiagnosticFormatter.Format(result));
        Console.WriteLine("[JEV] counters=" + JsonSerializer.Serialize(coordinator.Counters));
        return results.Count == examples.Length && results.All(r => r.Status == JevStatus.Success) ? 0 : 2;
    }

    private static ObservedMessage Message(string id, string text, MessageSide side) =>
        new(id, 1, side, text, text, OcrTextStatus.Recognized, null, default,
            DateTimeOffset.UtcNow, MessageObservationKind.LiveNew, "synthetic-semantic-input", true,
            SemanticRegionVerified: true, OutsideSemanticEdgeGuard: true);
}
