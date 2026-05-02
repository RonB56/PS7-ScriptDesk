using PowerShellStudio.Domain.Models;

namespace PowerShellStudio.Shell.Editor.Sdk
{
    public sealed class SdkEditorFeatureOptions
    {
        public EditorSdkMode EditorSdkMode { get; init; } = EditorSdkMode.Disabled;

        public MetadataEngineMode MetadataEngineMode { get; init; } = MetadataEngineMode.HelperProcessOnly;

        public CompletionEngineMode CompletionEngineMode { get; init; } = CompletionEngineMode.ExistingOnly;

        public bool PowerShellSdkFallbackEnabled { get; init; } = true;

        public static SdkEditorFeatureOptions FromApplicationSettings(ApplicationSettings? settings)
        {
            settings ??= new ApplicationSettings();

            return new SdkEditorFeatureOptions
            {
                EditorSdkMode = settings.EditorSdkMode,
                MetadataEngineMode = settings.MetadataEngineMode,
                CompletionEngineMode = settings.CompletionEngineMode,
                PowerShellSdkFallbackEnabled = settings.PowerShellSdkFallbackEnabled
            };
        }
    }
}
