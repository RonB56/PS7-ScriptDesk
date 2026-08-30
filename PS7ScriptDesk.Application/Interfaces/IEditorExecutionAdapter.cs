using System;
using System.Threading;
using System.Threading.Tasks;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Interfaces;

public interface IEditorExecutionAdapter
{
    PersistentSessionSnapshot Snapshot { get; }

    event Action<EditorExecutionEvent>? EventPublished;

    Task<EditorExecutionResult> ExecuteAsync(
        EditorExecutionRequest request,
        CancellationToken cancellationToken = default);
}
