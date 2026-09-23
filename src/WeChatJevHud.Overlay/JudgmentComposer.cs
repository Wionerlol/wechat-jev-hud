using System.Collections.Immutable;
using System.Globalization;
using WeChatJevHud.TypeSafe;

namespace WeChatJevHud.Overlay;

public sealed class JudgmentComposer
{
    public static readonly ImmutableDictionary<string, string> SpeechActLabels = new Dictionary<string, string>
    {
        ["question"] = "询问",
        ["request"] = "请求",
        ["answer"] = "回答",
        ["acknowledgement"] = "回应/确认",
        ["clarification"] = "澄清/纠正",
        ["complaint_or_concern"] = "担忧/抱怨",
        ["planning"] = "计划",
        ["joke_or_banter"] = "玩笑/闲聊",
        ["information"] = "信息",
        ["other"] = "其他",
    }.ToImmutableDictionary();

    public HudPresentationModel? Compose(JevAnalysisResult result)
    {
        var j = result.Evaluation.Judgments;
        if (result.Status != JevStatus.Success || j is null || j.SpeechAct is null || j.Urgency is null ||
            j.ExpectsResponse is null || j.ReferencesPriorContext is null ||
            !SpeechActLabels.TryGetValue(j.SpeechAct.Choice, out var label) ||
            !j.SpeechAct.Probabilities.TryGetValue(j.SpeechAct.Choice, out var selected) ||
            !Probability(selected) || !Probability(j.ExpectsResponse.YesProbability) ||
            !Probability(j.ReferencesPriorContext.YesProbability) || !double.IsFinite(j.Urgency.Score) || j.Urgency.Score is < 0 or > 3)
            return null;
        return new("Jev", [new(label, Percent(selected)), new("期待回应", Percent(j.ExpectsResponse.YesProbability)),
            new("依赖前文", Percent(j.ReferencesPriorContext.YesProbability)), new("紧迫度", j.Urgency.Score.ToString("0.0", CultureInfo.InvariantCulture) + "/3")], result);
    }
    private static bool Probability(double p) => double.IsFinite(p) && p is >= 0 and <= 1;
    private static string Percent(double p) => (p * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
}
