using PS7ScriptDesk.RestApiProofHost.Hosting;

var port = RestApiProofHostFactory.ResolvePortFromEnvironment(defaultPort: 5087);
var url = $"http://127.0.0.1:{port}";

await using var host = await RestApiProofHostFactory.StartAsync(new RestApiProofHostOptions { Url = url });
Console.WriteLine($"PS7 ScriptDesk REST API proof host listening on {host.BaseAddress}");
await host.WaitForShutdownAsync();
