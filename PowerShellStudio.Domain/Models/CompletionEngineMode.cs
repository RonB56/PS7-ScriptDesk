namespace PowerShellStudio.Domain.Models
{
    public enum CompletionEngineMode
    {
        ExistingOnly = 0,
        SdkCompareOnly = 1,
        SdkPreferredWithFallback = 2
    }
}
