using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Interfaces;

public interface IGitSetupPromptService
{
    GitCloneRequest? ShowCloneDialog();
    string? ShowInitializeDialog(string folderPath);
}
