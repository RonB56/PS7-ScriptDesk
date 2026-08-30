namespace PS7ScriptDesk.Shell.Controls;

internal enum TerminalWebView2FallbackState
{
    None,
    RuntimeUnavailable,
    InitializationFailed,
    Faulted
}

internal static class TerminalWebView2FallbackPolicy
{
    public static string GetMessage(TerminalWebView2FallbackState state)
    {
        return state switch
        {
            TerminalWebView2FallbackState.RuntimeUnavailable => "WebView2 Runtime is required for the integrated terminal.",
            TerminalWebView2FallbackState.InitializationFailed => "The integrated terminal renderer could not be initialized. Use Reset Console to retry.",
            TerminalWebView2FallbackState.Faulted => "The integrated terminal renderer encountered an error and was stopped. Use Reset Console to retry.",
            _ => "The integrated terminal renderer is unavailable. Use Reset Console to retry."
        };
    }

    public static bool ShowsRuntimeInstallDetails(TerminalWebView2FallbackState state)
        => state == TerminalWebView2FallbackState.RuntimeUnavailable;
}
