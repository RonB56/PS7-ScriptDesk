using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Interfaces
{
    public interface IDocumentRecoveryService
    {
        string RecoveryStorageDirectory { get; }

        TimeSpan RecoveryWriteDelay { get; }

        IReadOnlyList<DocumentRecoveryCandidate> GetRecoverableDocuments();

        bool SaveSnapshot(DocumentRecoverySnapshot snapshot);

        bool DiscardRecovery(string recoveryId);
    }
}
