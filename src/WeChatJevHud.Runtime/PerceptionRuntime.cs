using WeChatJevHud.Ocr;
using WeChatJevHud.Observer;
using WeChatJevHud.Vision;

namespace WeChatJevHud.Runtime;

/// <summary>Shared production construction only; accepted perception algorithms and options are unchanged.</summary>
public sealed class PerceptionRuntime : IAsyncDisposable
{
    private readonly TesseractOcrEngine _raw, _upscaled;
    public PaddleWorkerClient Worker { get; }
    public ProductionOcrCounters OcrCounters { get; } = new();
    public IMessageObserver Observer { get; }
    public PerceptionRuntime(string repository, string? python = null, string device = "gpu:0", string? tessdata = null,
        Action<string>? workerLog = null, Action<AppendAttemptTrace>? appendLog = null)
    {
        tessdata ??= Path.Combine(repository, ".ocr-cache", "tessdata");
        python ??= Environment.GetEnvironmentVariable("WECHAT_JEV_PADDLE_PYTHON") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeChatJevHud", "paddle-ocr", ".venv", "Scripts", "python.exe");
        _raw = new(tessdata, lowConfidenceThreshold: .90);
        _upscaled = new(tessdata, lowConfidenceThreshold: .90, preparation: OcrImagePreparation.Upscaled);
        var adaptive = new AdaptiveOcrEngine([_raw, _upscaled],
            new WindowsMediaOcrEngine("zh-Hans-CN", OcrImagePreparation.Upscaled), confidenceThreshold: .90);
        Worker = new(PaddleWorkerOptions.Create(python, [Path.Combine(repository,"scripts","paddle_ocr_worker.py"),
            "--model","PP-OCRv6_small_rec","--device",device,"--warmup-count","1"]), OcrCounters, workerLog);
        var ocr = new UnifiedPaddleOcrEngine(new PaddleRecognitionOcrEngine(Worker), adaptive, OcrCounters);
        Observer = new MessageObserver(new DarkThemeChatRegionLocator(), new DarkThemeBubbleDetector(), ocr,
            new ChatRoiChangeDetector(), new VisualConversationIdentityProvider(), appendDiagnosticSink: appendLog);
    }
    public async ValueTask DisposeAsync()
    {
        await Worker.DisposeAsync().ConfigureAwait(false);
        _raw.Dispose(); _upscaled.Dispose();
    }
}
