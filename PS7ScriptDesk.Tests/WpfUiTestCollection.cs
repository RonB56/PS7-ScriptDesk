using Xunit;

namespace PS7ScriptDesk.Tests;

[CollectionDefinition("WpfUi", DisableParallelization = true)]
public sealed class WpfUiTestCollection : ICollectionFixture<WpfUiFixture>
{
}

public sealed class WpfUiFixture : IDisposable
{
    public WpfUiFixture() => WpfTestDispatcher.Start();

    public void Dispose() => WpfTestDispatcher.Stop();
}
