namespace PS7ScriptDesk.Domain.Models;

public static class GitBranchName
{
    public static string Normalize(string name, bool isRemote)
    {
        var prefix = isRemote ? "refs/remotes/" : "refs/heads/";
        var candidate = name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name;
        return candidate.TrimStart('/');
    }
}
