namespace WeChatJevHud.Ocr;

public sealed record SemanticRegionEvidence(bool Verified, string Reason);

/// <summary>
/// Conservative V0 single-background-region check, not a quote parser or OCR probe.
/// Pixel-identical embedded regions cannot be distinguished; that remains residual risk.
/// </summary>
public static class SemanticRegionInspector
{
    public static SemanticRegionEvidence Inspect(ImageCrop crop)
    {
        var frame = ImageCropExtractor.Extract(crop);
        var inset = Math.Max(2, (int)Math.Round(4d * frame.DpiY / 96));
        var width = frame.Width - inset * 2;
        var height = frame.Height - inset * 2;
        if (width < 8 || height < 8) return new(false, "InsufficientInterior");
        int Color(int x, int y)
        {
            var i = (y + inset) * frame.Stride + (x + inset) * 4;
            return (frame.Bgra32Pixels[i] >> 3) | ((frame.Bgra32Pixels[i + 1] >> 3) << 5) |
                ((frame.Bgra32Pixels[i + 2] >> 3) << 10);
        }
        var border = new Dictionary<int, int>();
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                if (x == 0 || y == 0 || x == width - 1 || y == height - 1)
                {
                    var color = Color(x, y);
                    border[color] = border.GetValueOrDefault(color) + 1;
                }
        var dominant = border.MaxBy(p => p.Value);
        if (dominant.Value < border.Values.Sum() * .65) return new(false, "AmbiguousBackground");
        var mask = new bool[width * height];
        var backgroundCount = 0;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var c = Color(x, y);
                var matches = Math.Abs((c & 31) - (dominant.Key & 31)) <= 1 &&
                    Math.Abs(((c >> 5) & 31) - ((dominant.Key >> 5) & 31)) <= 1 &&
                    Math.Abs((c >> 10) - (dominant.Key >> 10)) <= 1;
                mask[y * width + x] = !matches;
                if (matches) backgroundCount++;
            }
        if (backgroundCount < mask.Length * .55) return new(false, "MultipleOrAmbiguousRegions");
        var queue = new int[mask.Length];
        for (var index = 0; index < mask.Length; index++)
        {
            if (!mask[index]) continue;
            var head = 0; var tail = 0;
            queue[tail++] = index; mask[index] = false;
            var left = index % width; var right = left;
            var top = index / width; var bottom = top;
            while (head < tail)
            {
                var p = queue[head++]; var x = p % width; var y = p / width;
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                if (x > 0) Add(p - 1);
                if (x + 1 < width) Add(p + 1);
                if (y > 0) Add(p - width);
                if (y + 1 < height) Add(p + width);
            }
            var componentWidth = right - left + 1;
            var componentHeight = bottom - top + 1;
            if (componentWidth >= width * .5 && componentHeight >= Math.Max(4, 6d * frame.DpiY / 96) &&
                tail >= componentWidth * componentHeight * .65)
                return new(false, "EmbeddedPanelOrAmbiguousRegion");
            void Add(int p)
            {
                if (!mask[p]) return;
                mask[p] = false; queue[tail++] = p;
            }
        }
        return new(true, "SingleBackgroundRegion");
    }
}
