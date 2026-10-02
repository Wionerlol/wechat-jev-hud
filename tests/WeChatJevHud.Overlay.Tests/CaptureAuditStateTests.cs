using WeChatJevHud.Capture;
using WeChatJevHud.Overlay;
using Xunit;

namespace WeChatJevHud.Overlay.Tests;

public sealed class CaptureAuditStateTests
{
    private static readonly CaptureConfiguration Laptop = new(1, 2, new(10, 20, 1000, 800), "laptop", 144, 144);
    private static readonly CaptureConfiguration External = new(1, 2, new(-1000, 0, 900, 700), "external", 96, 96);
    private static readonly CaptureExclusionEvidence Good = new(true, true, true, true, true, true, true);
    private static void Begin(CaptureAuditState state, CaptureConfiguration config)
    {
        Assert.False(state.Observe(config, true));
        Assert.True(state.Observe(config, true));
        Assert.False(state.CanProcess(config, CaptureMethod.RenderWindow));
    }
    [Fact]
    public void StableConfigurationRequiresAuditAndRejectsDesktopAndOtherConfiguration()
    {
        var state = new CaptureAuditState(); Begin(state, Laptop);
        Assert.Equal(CaptureAuditOutcome.Passed, state.Complete(Laptop, Laptop, true, Good));
        Assert.True(state.CanProcess(Laptop, CaptureMethod.RenderWindow));
        Assert.False(state.CanProcess(Laptop, CaptureMethod.VisibleDesktopFallback));
        Assert.False(state.CanProcess(External, CaptureMethod.RenderWindow));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MovementDuringAuditIsTransientAndNewStableConfigurationCanVerify(bool dpiChange)
    {
        var next = dpiChange ? External : Laptop with { Bounds = new(30, 50, 1000, 800) };
        var state = new CaptureAuditState(); Begin(state, Laptop);
        Assert.Equal(CaptureAuditOutcome.TransientConfigurationChanged, state.Complete(Laptop, next, true, Good));
        Assert.NotEqual(CaptureAuditPhase.HardFailed, state.Phase);
        Begin(state, next);
        Assert.Equal(CaptureAuditOutcome.Passed, state.Complete(next, next, true, Good));
        Assert.True(state.CanProcess(next, CaptureMethod.RenderWindow));
    }
    [Fact]
    public void ForegroundLossAllowsStableRetryWithoutHardFailure()
    {
        var state = new CaptureAuditState(); Begin(state, Laptop);
        Assert.Equal(CaptureAuditOutcome.NotForeground, state.Complete(Laptop, Laptop, false, Good with { ForegroundPreserved = false }));
        Assert.False(state.Observe(Laptop, false));
        Begin(state, Laptop);
        Assert.Equal(CaptureAuditOutcome.Passed, state.Complete(Laptop, Laptop, true, Good));
    }
    [Fact]
    public void ContaminationLatchesOnlyCurrentConfiguration()
    {
        var state = new CaptureAuditState(); Begin(state, Laptop);
        Assert.Equal(CaptureAuditOutcome.HardSafetyFailure, state.Complete(Laptop, Laptop, true, Good with { RenderExcluded = false }));
        for (var i = 0; i < 10; i++) Assert.False(state.Observe(Laptop, true));
        Assert.False(state.CanProcess(Laptop, CaptureMethod.RenderWindow));
        Begin(state, External);
        Assert.Equal(CaptureAuditOutcome.Passed, state.Complete(External, External, true, Good));
    }
    [Fact]
    public void StaleProbeEvidenceDoesNotBecomeHardFailureEvenIfConfigurationReturned()
    {
        var state = new CaptureAuditState(); Begin(state, Laptop);
        Assert.Equal(CaptureAuditOutcome.TransientConfigurationChanged,
            state.Complete(Laptop, Laptop, true, Good with { ConfigurationStable = false, RenderExcluded = false }));
        Begin(state, Laptop);
    }
}
