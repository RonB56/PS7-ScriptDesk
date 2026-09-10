namespace PS7ScriptDesk.Domain.Models;

public sealed record GitRemote(string Name, string? FetchUrl, string? PushUrl);

public sealed record GitRemoteState(IReadOnlyList<GitRemote> Remotes, string? Error = null);
