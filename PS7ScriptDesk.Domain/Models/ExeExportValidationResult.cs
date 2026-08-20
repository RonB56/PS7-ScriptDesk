using System.Collections.Generic;

namespace PS7ScriptDesk.Domain.Models;

public sealed class ExeExportValidationResult
{
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool IsValid => Errors.Count == 0;
}
