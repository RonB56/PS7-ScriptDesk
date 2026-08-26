using PS7ScriptDesk.RestApiProofHost.Hosting;

try
{
    var options = RestApiProofHostCommandLine.Parse(args);
    var port = options.Port ?? RestApiProofHostFactory.ResolvePortFromEnvironment(defaultPort: 5087);
    var url = options.Url ?? $"http://127.0.0.1:{port}";

    await using var host = await RestApiProofHostFactory.StartAsync(new RestApiProofHostOptions
    {
        Url = url,
        ContentRootPath = options.ContentRootPath,
        ConfigurationRelativePath = options.ConfigurationRelativePath
    });
    Console.WriteLine($"PS7 ScriptDesk REST API proof host listening on {host.BaseAddress}");
    await host.WaitForShutdownAsync();
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Console.Error.WriteLine($"PS7 ScriptDesk REST API proof host startup failed: {ex.GetType().Name}: {ex.Message}");
    Environment.ExitCode = 1;
}

internal sealed record RestApiProofHostCommandLine(int? Port, string? Url, string? ContentRootPath, string? ConfigurationRelativePath)
{
    public static RestApiProofHostCommandLine Parse(string[] args)
    {
        int? port = null;
        string? url = null;
        string? contentRoot = null;
        string? configuration = null;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            string? ReadValue()
            {
                if (index + 1 >= args.Length)
                {
                    return null;
                }

                index++;
                return args[index];
            }

            switch (argument)
            {
                case "--port" when int.TryParse(ReadValue(), out var parsedPort):
                    port = parsedPort;
                    break;
                case "--url":
                    url = ReadValue();
                    break;
                case "--content-root":
                    contentRoot = ReadValue();
                    break;
                case "--config":
                    configuration = ReadValue();
                    break;
            }
        }

        return new RestApiProofHostCommandLine(port, url, contentRoot, configuration);
    }
}
