namespace PS7ScriptDesk.Application.Diagnostics;

/// <summary>Classifies only fixed terminal control sequences; never returns payload text.</summary>
public static class TerminalInputClassifier
{
    public static string Classify(string data)
    {
        if (string.IsNullOrEmpty(data)) return "Unknown";
        return data switch
        {
            "\r" or "\n" or "\r\n" => "Enter",
            "\b" or "\u007f" => "Backspace",
            "\u001b" => "Escape",
            "\u0003" => "CtrlC",
            "\u001b[A" or "\u001bOA" => "ArrowUp",
            "\u001b[B" or "\u001bOB" => "ArrowDown",
            "\u001b[C" or "\u001bOC" => "ArrowRight",
            "\u001b[D" or "\u001bOD" => "ArrowLeft",
            "\u001b[H" or "\u001b[1~" => "Home",
            "\u001b[F" or "\u001b[4~" => "End",
            "\u001b[I" => "FocusIn",
            "\u001b[O" => "FocusOut",
            "\u001b[200~" => "Paste/BracketedPasteStart",
            "\u001b[201~" => "Paste/BracketedPasteEnd",
            _ when IsMouseProtocol(data) => "MouseProtocol",
            _ when data.Contains('\u001b') || data.Any(char.IsControl) => "OtherControlSequence",
            _ when data.All(c => !char.IsControl(c)) => "PrintableText",
            _ => "Unknown"
        };
    }

    public static bool EstablishesUserEditOwnership(string inputClass) =>
        !string.Equals(inputClass, "FocusIn", StringComparison.Ordinal) &&
        !string.Equals(inputClass, "FocusOut", StringComparison.Ordinal);

    private static bool IsMouseProtocol(string data) =>
        (data.StartsWith("\u001b[M", StringComparison.Ordinal) && data.Length >= 6) ||
        (data.StartsWith("\u001b[<", StringComparison.Ordinal) && (data.EndsWith('M') || data.EndsWith('m')));
}
