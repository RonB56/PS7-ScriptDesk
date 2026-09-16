namespace PS7ScriptDesk.Application.Services;

public static class GitCloneValidation
{
    public static string ValidateSource(string? source)
    {
        var value = source?.Trim();
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri is not null &&
               uri.Scheme is "http" or "https" && !string.IsNullOrWhiteSpace(uri.Host)
            ? string.Empty
            : "Enter a complete repository URL, such as https://github.com/org/repo.git.";
    }

    public static string ValidateDestinationParent(string? destinationParent)
        => string.IsNullOrWhiteSpace(destinationParent) ? "Choose a destination parent folder before cloning." : string.Empty;

    public static string ValidateFolderName(string? folderName)
    {
        var value = folderName?.Trim();
        return string.IsNullOrWhiteSpace(value) || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            ? "Enter a valid repository folder name."
            : string.Empty;
    }

    public static string Validate(string? source, string? destinationParent, string? folderName)
        => ValidateSource(source) is { Length: > 0 } sourceError ? sourceError
         : ValidateDestinationParent(destinationParent) is { Length: > 0 } parentError ? parentError
         : ValidateFolderName(folderName);
}
