using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Interfaces;

/// <summary>
/// Optional single-owner awaitable source boundary implemented by the authoritative PSES service.
/// The registered presentation owner receives visible output only after parser, echo, and backend
/// prompt handling. The source awaits that owner before reading/publishing the next decoded chunk.
/// </summary>
public interface IAwaitableTerminalOutputSource
{
    event Func<int, ExecutionOutputStreamKind, string, ValueTask>? VisibleOutputPublishingAsync;
}
