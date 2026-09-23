using System.Text.Json;

namespace WeChatJevHud.TypeSafe;

public static class JevDiagnosticFormatter
{
    public static string Format(JevAnalysisResult result) => "[JEV] " + JsonSerializer.Serialize(new
    {
        epoch = result.ConversationEpochId,
        message = result.MessageId,
        status = result.Status.ToString(),
        judgment_set = result.JudgmentSetVersion,
        context_messages = result.ContextMessages,
        state_characters = result.StateCharacters,
        context_has_gaps = result.ContextHasGaps,
        questions_per_request = JevQuestionSet.Count,
        request_count = result.Evaluation.RequestCount,
        request_bytes = result.Evaluation.RequestBytes,
        response_bytes = result.Evaluation.ResponseBytes,
        input_tokens = result.Evaluation.InputTokens,
        output_tokens = result.Evaluation.OutputTokens,
        model = result.Evaluation.Model,
        context_build_ms = result.ContextBuildMs,
        queue_wait_ms = result.QueueWaitMs,
        typesafe_roundtrip_ms = result.Evaluation.RoundtripMs,
        result_mapping_ms = result.Evaluation.MappingMs,
        total_semantic_ms = result.TotalSemanticMs,
        judgments = result.Evaluation.Judgments,
    }, JevContextBuilder.JsonOptions);
}
