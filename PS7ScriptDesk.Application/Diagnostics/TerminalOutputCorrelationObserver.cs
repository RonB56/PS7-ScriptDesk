using PS7ScriptDesk.Application.Interfaces;

namespace PS7ScriptDesk.Application.Diagnostics;

public sealed class TerminalOutputCorrelationObserver : ITerminalObserver
{
    public void Observe(TerminalOutputObservation observation)
    {
        TerminalOutputCorrelationTrace.Record(
            "TerminalDataPlaneObserver",
            observation.SessionGeneration,
            observation.Data,
            envelopeSequence: observation.Sequence);
    }
}
