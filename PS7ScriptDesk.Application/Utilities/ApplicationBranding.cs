using System;
using System.IO;

namespace PS7ScriptDesk.Application.Utilities
{
    /// <summary>
    /// Centralized public branding and storage identifiers for the application.
    ///
    /// Keep the public product name distinct from Microsoft/SAPIEN product names while still
    /// describing compatibility with PowerShell 7.x in user-facing text.
    /// </summary>
    public static class ApplicationBranding
    {
        public const string PublicName = "PS7 ScriptDesk";
        public const string InternalName = "PS7ScriptDesk";
        public const string Tagline = "An ISE-style scripting tool for PowerShell 7.x";
        public const string LogFileName = "ps7scriptdesk.log";

        public static string LocalApplicationDataRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            InternalName);
    }
}
