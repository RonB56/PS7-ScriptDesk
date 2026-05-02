using System;

namespace PowerShellStudio.Shell.Editor.Sdk
{
    public sealed class SdkRunspacePoolOptions
    {
        public int MinRunspaces { get; set; } = 1;

        public int MaxRunspaces { get; set; } = 4;

        public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(30);

        public string? RuntimeName { get; set; } = "EditorSdkRuntime";

        public bool LoadDefaultProfile { get; set; }

        public bool UseNoProfileBehavior { get; set; } = true;

        public void Validate()
        {
            if (MinRunspaces <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MinRunspaces), MinRunspaces, "The minimum runspace count must be greater than zero.");
            }

            if (MaxRunspaces <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxRunspaces), MaxRunspaces, "The maximum runspace count must be greater than zero.");
            }

            if (MaxRunspaces < MinRunspaces)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxRunspaces), MaxRunspaces, "The maximum runspace count must be greater than or equal to the minimum runspace count.");
            }

            if (DefaultTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(DefaultTimeout), DefaultTimeout, "The default timeout must be greater than zero.");
            }
        }
    }
}
