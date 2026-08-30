using PS7ScriptDesk.Shell.Controls;

namespace PS7ScriptDesk.Tests;

public sealed class TerminalWebView2LifecyclePolicyTests
{
    [Fact]
    public void DisposedRenderer_RejectsLayoutAndRendererCallbacks()
    {
        var policy = new TerminalWebView2LifecyclePolicy();

        Assert.True(policy.TryBeginInitialization());
        Assert.True(policy.MarkReady());
        Assert.True(policy.CanUseRenderer);
        Assert.True(policy.CanAcceptRendererCallback);

        Assert.True(policy.MarkDisposed());

        Assert.False(policy.CanUseRenderer);
        Assert.False(policy.CanAcceptRendererCallback);
        Assert.False(policy.MarkFaulted());
        Assert.False(policy.MarkDisposed());
        Assert.Equal(TerminalWebView2LifecycleState.Disposed, policy.State);
    }

    [Fact]
    public void FaultedRenderer_IsTerminalAndCannotBeReinitialized()
    {
        var policy = new TerminalWebView2LifecyclePolicy();

        Assert.True(policy.TryBeginInitialization());
        Assert.True(policy.MarkFaulted());

        Assert.False(policy.CanUseRenderer);
        Assert.False(policy.CanAcceptRendererCallback);
        Assert.False(policy.TryBeginInitialization());
        Assert.False(policy.MarkReady());
        Assert.Equal(TerminalWebView2LifecycleState.Faulted, policy.State);
    }

    [Fact]
    public void FaultedDisposingAndDisposedRenderers_RejectCoreAccess()
    {
        var faulted = CreateReadyPolicy();
        Assert.True(faulted.MarkFaulted());
        Assert.False(faulted.CanUseRenderer);
        Assert.False(faulted.CanAcceptRendererCallback);

        var disposing = CreateReadyPolicy();
        Assert.True(disposing.TryBeginDisposal());
        Assert.Equal(TerminalWebView2LifecycleState.Disposing, disposing.State);
        Assert.False(disposing.CanUseRenderer);
        Assert.False(disposing.CanAcceptRendererCallback);

        var disposed = CreateReadyPolicy();
        Assert.True(disposed.MarkDisposed());
        Assert.False(disposed.CanUseRenderer);
        Assert.False(disposed.CanAcceptRendererCallback);
    }

    [Fact]
    public void RendererReplacement_UsesFreshLifecycleAndRetiresOldInstance()
    {
        var oldRenderer = CreateReadyPolicy();
        Assert.True(oldRenderer.MarkFaulted());

        var replacementRenderer = CreateReadyPolicy();

        Assert.NotSame(oldRenderer, replacementRenderer);
        Assert.False(oldRenderer.CanUseRenderer);
        Assert.True(replacementRenderer.CanUseRenderer);
    }

    [Fact]
    public void FallbackPolicy_DistinguishesMissingRuntimeFromRendererFault()
    {
        Assert.Equal(
            "WebView2 Runtime is required for the integrated terminal.",
            TerminalWebView2FallbackPolicy.GetMessage(TerminalWebView2FallbackState.RuntimeUnavailable));
        Assert.True(TerminalWebView2FallbackPolicy.ShowsRuntimeInstallDetails(TerminalWebView2FallbackState.RuntimeUnavailable));

        Assert.Equal(
            "The integrated terminal renderer encountered an error and was stopped. Use Reset Console to retry.",
            TerminalWebView2FallbackPolicy.GetMessage(TerminalWebView2FallbackState.Faulted));
        Assert.False(TerminalWebView2FallbackPolicy.ShowsRuntimeInstallDetails(TerminalWebView2FallbackState.Faulted));
    }

    private static TerminalWebView2LifecyclePolicy CreateReadyPolicy()
    {
        var policy = new TerminalWebView2LifecyclePolicy();
        Assert.True(policy.TryBeginInitialization());
        Assert.True(policy.MarkReady());
        return policy;
    }
}
