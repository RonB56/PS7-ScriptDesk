using Microsoft.Win32.SafeHandles;
using System;
using System.Runtime.InteropServices;

namespace PS7ScriptDesk.Application.Utilities;

/// <summary>
/// Resolves the elevation state of the application's primary process token.
/// </summary>
public static class CurrentProcessElevation
{
    private const uint TokenQueryAccess = 0x0008;

    /// <summary>
    /// Returns whether the current process token is elevated, or <see langword="null"/>
    /// when the token cannot be queried on the current platform.
    /// </summary>
    public static bool? TryGetIsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQueryAccess, out var token) || token.IsInvalid)
            {
                token?.Dispose();
                return null;
            }

            using (token)
            {
                return GetTokenInformation(
                    token,
                    TokenInformationClass.TokenElevation,
                    out var elevation,
                    Marshal.SizeOf<TokenElevation>(),
                    out _)
                    ? IsElevatedTokenValue(elevation.TokenIsElevated)
                    : null;
            }
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Converts the native TOKEN_ELEVATION flag into the managed elevation state.
    /// </summary>
    public static bool IsElevatedTokenValue(int tokenIsElevated)
    {
        return tokenIsElevated != 0;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        TokenInformationClass tokenInformationClass,
        out TokenElevation tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    private enum TokenInformationClass
    {
        TokenElevation = 20
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevation
    {
        public int TokenIsElevated;
    }
}
