namespace PS7ScriptDesk.Tests;

internal static class TestRepositoryPaths
{
    public static string GetFilePath(params string[] relativeSegments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PS7ScriptDesk.slnx")) &&
                Directory.Exists(Path.Combine(current.FullName, "PS7ScriptDesk.Tests")))
            {
                return Path.Combine(new[] { current.FullName }.Concat(relativeSegments).ToArray());
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the PS7 ScriptDesk repository root from the test assembly location.");
    }

    public static string ReadFile(params string[] relativeSegments)
        => File.ReadAllText(GetFilePath(relativeSegments));
}
