namespace PS7ScriptDesk.Shell;

internal static class GitFeatureAvailability
{
    // Git remains implemented in the repository but is intentionally unavailable to production users until the integration is release-ready.
    internal static bool IsEnabled => false;
}
