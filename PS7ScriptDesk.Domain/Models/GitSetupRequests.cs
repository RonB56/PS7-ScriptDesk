namespace PS7ScriptDesk.Domain.Models;

public sealed record GitCloneRequest(string Source, string DestinationParent, string FolderName);
