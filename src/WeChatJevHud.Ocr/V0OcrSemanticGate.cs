namespace WeChatJevHud.Ocr;

/// <summary>Accepted V0 risk policy. Never reads model scores, side, words or verifier output.</summary>
public static class V0OcrSemanticGate
{
    public static bool Allows(OcrResult result, OcrInputEvidence? input)
    {
        if (input is not { HasCompleteText: true, RegionSeparationVerified: true, OutsideSemanticEdgeGuard: true } ||
            string.IsNullOrWhiteSpace(result.RawText) ||
            result.Status is not (OcrTextStatus.Recognized or OcrTextStatus.LowConfidence) ||
            result.Diagnostics is not { Route: OcrRoute.PaddleUnified, RuntimeFallback: false, Extraction: { } extraction })
            return false;

        return extraction.DetectedLineCount >= 0 &&
               extraction.LineBoxes.Count == extraction.DetectedLineCount &&
               extraction.LineBoxes.All(box => box.Length == 4 && box[0] >= 0 && box[1] >= 0 && box[2] > box[0] && box[3] > box[1]) &&
               extraction.Lines.Count == Math.Max(1, extraction.DetectedLineCount) &&
               extraction.Lines.All(line => !string.IsNullOrWhiteSpace(line.RawText));
    }
}
