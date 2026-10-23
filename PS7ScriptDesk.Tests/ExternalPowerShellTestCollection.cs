using Xunit;

namespace PS7ScriptDesk.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExternalPowerShellTestCollection
{
    public const string Name = "ExternalPowerShell";
}
