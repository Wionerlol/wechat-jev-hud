using Xunit;

namespace WeChatJevHud.Ocr.Tests;

public sealed class V0OcrSemanticGateTests
{
    private static readonly OcrInputEvidence Safe = new(true, true, true);

    private static OcrResult Result(int count = 1, double score = 0) => new("好", null, OcrTextStatus.LowConfidence, "好",
        new OcrDiagnostics(OcrRoute.PaddleUnified, OcrTrustBasis.None, [], TimeSpan.Zero,
            new UnifiedExtraction(count, Enumerable.Range(0, count).Select(i => new[] { 1, i * 10, 20, i * 10 + 8 }).ToArray(),
                Enumerable.Range(0, Math.Max(1, count)).Select(_ => new PaddleLine("好", score)).ToArray(),
                TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero)));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(1, 0.9999)]
    public void Valid_success_uses_hard_facts_not_score_or_line_count_threshold(int count, double score) =>
        Assert.True(V0OcrSemanticGate.Allows(Result(count, score), Safe));

    [Fact]
    public void Every_input_gate_is_required()
    {
        foreach (var input in new OcrInputEvidence?[] { null, new(false, true, true), new(true, false, true), new(true, true, false) })
            Assert.False(V0OcrSemanticGate.Allows(Result(), input));
    }

    [Fact]
    public void Fallback_empty_missing_structure_and_invalid_lines_never_pass()
    {
        var r = Result();
        foreach (var bad in new[]
        {
            r with { RawText = " " }, r with { Diagnostics = null }, r with { Status = OcrTextStatus.Unsupported },
            r with { Diagnostics = r.Diagnostics! with { RuntimeFallback = true } },
            r with { Diagnostics = r.Diagnostics! with { Route = OcrRoute.Adaptive } },
            r with { Diagnostics = r.Diagnostics! with { Extraction = null } },
            r with { Diagnostics = r.Diagnostics! with { Extraction = r.Diagnostics.Extraction! with { Lines = [] } } },
            r with { Diagnostics = r.Diagnostics! with { Extraction = r.Diagnostics.Extraction! with { Lines = [new("", 1)] } } },
            r with { Diagnostics = r.Diagnostics! with { Extraction = r.Diagnostics.Extraction! with { LineBoxes = [new[] { 10, 0, 1, 8 }] } } },
        }) Assert.False(V0OcrSemanticGate.Allows(bad, Safe));
    }
}
