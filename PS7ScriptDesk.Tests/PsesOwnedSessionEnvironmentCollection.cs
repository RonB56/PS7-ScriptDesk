using Xunit;

namespace PS7ScriptDesk.Tests;

/// <summary>
/// Serializes only tests that mutate the process-wide PSES-owned-session feature-gate variable.
/// Other test collections remain eligible for normal xUnit parallel execution.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PsesOwnedSessionEnvironmentCollection
{
    public const string Name = "PSES-owned-session environment";
}
