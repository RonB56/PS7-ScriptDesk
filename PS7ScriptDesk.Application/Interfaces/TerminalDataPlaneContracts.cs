using System.Collections.Generic;

namespace PS7ScriptDesk.Application.Interfaces;

/// <summary>Immutable observation copy emitted beside, never instead of, terminal delivery.</summary>
public readonly record struct TerminalOutputObservation(
    int SessionGeneration,
    string Data,
    long? Sequence = null,
    string Source = "ConPTY")
{
    public int CharacterCount => Data.Length;
}

public interface ITerminalObserver
{
    void Observe(TerminalOutputObservation observation);
}

public interface ITerminalDataPlane
{
    bool Publish(TerminalOutputObservation observation);
    IDisposable Subscribe(ITerminalObserver observer);
}

