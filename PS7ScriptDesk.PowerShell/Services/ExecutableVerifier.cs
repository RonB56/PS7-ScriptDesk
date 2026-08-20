using System;
using System.IO;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.PowerShell.Services;

internal sealed class ExecutableVerifier
{
    public ExeExportValidationResult Verify(string executablePath, ExeTargetArchitecture architecture)
    {
        var result = new ExeExportValidationResult();
        if (!File.Exists(executablePath))
        {
            result.Errors.Add("The expected executable file was not produced.");
            return result;
        }

        var fileInfo = new FileInfo(executablePath);
        if (fileInfo.Length == 0)
        {
            result.Errors.Add("The exported executable is zero bytes.");
            return result;
        }

        try
        {
            using var stream = File.OpenRead(executablePath);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt16() != 0x5A4D)
            {
                result.Errors.Add("The exported file does not contain a Windows PE executable header.");
                return result;
            }

            stream.Position = 0x3C;
            var peOffset = reader.ReadInt32();
            if (peOffset < 0 || peOffset > stream.Length - 6)
            {
                result.Errors.Add("The exported executable has an invalid PE header offset.");
                return result;
            }

            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550)
            {
                result.Errors.Add("The exported file does not contain a valid PE signature.");
                return result;
            }

            var machine = reader.ReadUInt16();
            var expected = architecture == ExeTargetArchitecture.Arm64 ? (ushort)0xAA64 : (ushort)0x8664;
            if (machine != expected)
                result.Errors.Add($"The exported executable architecture ({FormatMachine(machine)}) does not match the requested target ({FormatMachine(expected)}).");
        }
        catch (Exception ex)
        {
            result.Errors.Add($"The exported executable could not be verified: {ex.Message}");
        }

        return result;
    }

    private static string FormatMachine(ushort machine) => machine switch
    {
        0x8664 => "Windows x64",
        0xAA64 => "Windows ARM64",
        _ => $"0x{machine:X4}"
    };
}
