using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using ICSharpCode.AvalonEdit.Document;
using PS7ScriptDesk.Application.Diagnostics;

namespace PS7ScriptDesk.Shell.Editor;

public static class EditorTransformCommands
{
    public static EditorCommandResult SortLinesAscending(TextDocument document, int start, int length) => ApplyLines(document, start, length, lines => lines.OrderBy(line => line, StringComparer.Ordinal).ToArray());
    public static EditorCommandResult SortLinesDescending(TextDocument document, int start, int length) => ApplyLines(document, start, length, lines => lines.OrderByDescending(line => line, StringComparer.Ordinal).ToArray());
    public static EditorCommandResult ReverseLines(TextDocument document, int start, int length) => ApplyLines(document, start, length, lines => lines.Reverse().ToArray());
    public static EditorCommandResult RemoveDuplicateLines(TextDocument document, int start, int length) => ApplyLines(document, start, length, lines => lines.Distinct(StringComparer.Ordinal).ToArray());
    public static EditorCommandResult TrimLines(TextDocument document, int start, int length) => ApplyLines(document, start, length, lines => lines.Select(line => line.Trim()).ToArray());
    public static EditorCommandResult TrimTrailingWhitespace(TextDocument document, int start, int length) => ApplyLines(document, start, length, lines => lines.Select(line => line.TrimEnd(' ', '\t')).ToArray());
    public static EditorCommandResult RemoveBlankLines(TextDocument document, int start, int length) => ApplyLines(document, start, length, lines => lines.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray());
    public static EditorCommandResult UppercaseSelection(TextDocument document, int start, int length) => ApplySelection(document, start, length, text => text.ToUpperInvariant());
    public static EditorCommandResult LowercaseSelection(TextDocument document, int start, int length) => ApplySelection(document, start, length, text => text.ToLowerInvariant());
    public static EditorCommandResult TitleCaseSelection(TextDocument document, int start, int length) => ApplySelection(document, start, length, text => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text));
    public static EditorCommandResult PrefixLines(TextDocument document, int start, int length, string prefix) => ApplyLines(document, start, length, lines => lines.Select(line => prefix + line).ToArray());
    public static EditorCommandResult SuffixLines(TextDocument document, int start, int length, string suffix) => ApplyLines(document, start, length, lines => lines.Select(line => line + suffix).ToArray());
    public static EditorCommandResult QuoteLines(TextDocument document, int start, int length, char quote) => ApplyLines(document, start, length, lines => lines.Select(line => quote + line + quote).ToArray());
    public static EditorCommandResult AddTrailingComma(TextDocument document, int start, int length) => ApplyLines(document, start, length, lines => lines.Select(line => line.EndsWith(",", StringComparison.Ordinal) ? line : line + ",").ToArray());
    public static EditorCommandResult RemoveTrailingComma(TextDocument document, int start, int length) => ApplyLines(document, start, length, lines => lines.Select(line => line.EndsWith(",", StringComparison.Ordinal) ? line[..^1] : line).ToArray());

    public static EditorCommandResult ConvertListToPowerShellArray(TextDocument document, int start, int length, int indentationSize = 4) =>
        TryApplySelection(document, start, length, text => ConvertListToPowerShellArrayText(document.Text, text, indentationSize), nameof(ConvertListToPowerShellArray), typeof(FormatException));

    public static EditorCommandResult ConvertPowerShellArrayToList(TextDocument document, int start, int length) =>
        TryApplySelection(document, start, length, text => ConvertPowerShellArrayToListText(document.Text, text), nameof(ConvertPowerShellArrayToList), typeof(FormatException));

    public static EditorCommandResult TrimDocumentTrailingWhitespace(TextDocument document)
    {
        var lines = Enumerable.Range(1, document.LineCount).Select(number => document.GetLineByNumber(number)).ToArray();
        var replacements = lines
            .Select(line => (Line: line, Text: document.GetText(line.Offset, line.Length)))
            .Where(item => item.Text.Length > 0 && item.Text.TrimEnd(' ', '\t').Length != item.Text.Length)
            .ToArray();
        using (BeginUndoGroup(document))
        {
            foreach (var replacement in replacements.Reverse())
            {
                var trimmed = replacement.Text.TrimEnd(' ', '\t');
                document.Replace(replacement.Line.Offset, replacement.Line.Length, trimmed);
            }
        }

        return new EditorCommandResult(0, document.TextLength);
    }

    public static EditorCommandResult SortLinesIgnoreCaseAscending(TextDocument document, int start, int length) =>
        ApplyLines(document, start, length, lines => lines.OrderBy(line => line, StringComparer.OrdinalIgnoreCase).ToArray());

    public static EditorCommandResult SortLinesIgnoreCaseDescending(TextDocument document, int start, int length) =>
        ApplyLines(document, start, length, lines => lines.OrderByDescending(line => line, StringComparer.OrdinalIgnoreCase).ToArray());

    public static EditorCommandResult SortLinesByLength(TextDocument document, int start, int length) =>
        ApplyLines(document, start, length, lines => lines.OrderBy(line => line.Length).ToArray());

    public static EditorCommandResult UniqueSortLines(TextDocument document, int start, int length) =>
        ApplyLines(document, start, length, lines => lines.Distinct(StringComparer.Ordinal).OrderBy(line => line, StringComparer.Ordinal).ToArray());

    public static EditorCommandResult CollapseConsecutiveBlankLines(TextDocument document, int start, int length) =>
        ApplyLines(document, start, length, CollapseBlankLines);

    public static EditorCommandResult AddLineNumbers(TextDocument document, int start, int length)
    {
        var range = GetLineRange(document, start, length);
        var sourceLines = GetLineTexts(document, range);
        if (sourceLines.Count > 0 && IsGeneratedNumbering(sourceLines))
        {
            return new EditorCommandResult(document.GetLineByNumber(range.FirstLine).Offset, GetRangeLength(document, range));
        }

        var width = sourceLines.Count.ToString(CultureInfo.InvariantCulture).Length;
        return ApplyLines(document, start, length, lines => lines
            .Select((line, index) => $"{(index + 1).ToString(CultureInfo.InvariantCulture).PadLeft(width)}. {line}")
            .ToArray());
    }

    public static EditorCommandResult RemoveLineNumbers(TextDocument document, int start, int length) =>
        ApplyLines(document, start, length, lines => lines.Select(RemoveGeneratedNumberPrefix).ToArray());

    public static EditorCommandResult ConvertLineEndingsToCrlf(TextDocument document) => ConvertLineEndings(document, "\r\n");

    public static EditorCommandResult ConvertLineEndingsToLf(TextDocument document) => ConvertLineEndings(document, "\n");

    public static EditorCommandResult UrlEncode(TextDocument document, int start, int length) =>
        ApplySelection(document, start, length, WebUtility.UrlEncode);

    public static EditorCommandResult UrlDecode(TextDocument document, int start, int length) =>
        ApplySelection(document, start, length, WebUtility.UrlDecode);

    public static EditorCommandResult Base64Encode(TextDocument document, int start, int length) =>
        ApplySelection(document, start, length, text => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)));

    public static EditorCommandResult Base64Decode(TextDocument document, int start, int length)
    {
        return TryApplySelection(document, start, length, text =>
        {
            var bytes = Convert.FromBase64String(text);
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }, nameof(Base64Decode), typeof(FormatException), typeof(DecoderFallbackException));
    }

    public static EditorCommandResult JsonPrettyPrint(TextDocument document, int start, int length) =>
        TryFormatJson(document, start, length, writeIndented: true, nameof(JsonPrettyPrint));

    public static EditorCommandResult JsonMinify(TextDocument document, int start, int length) =>
        TryFormatJson(document, start, length, writeIndented: false, nameof(JsonMinify));

    public static EditorCommandResult JoinLines(TextDocument document, int start, int length)
    {
        var range = GetLineRange(document, start, length);
        var lines = Enumerable.Range(range.FirstLine, range.LastLine - range.FirstLine + 1)
            .Select(number => document.GetLineByNumber(number))
            .Select(line => new
            {
                Line = line,
                Text = document.GetText(line.Offset, line.Length),
                Delimiter = document.GetText(line.Offset + line.Length, line.TotalLength - line.Length)
            })
            .ToArray();
        var joined = string.Join(" ", lines.Select(item => item.Text.Trim()).Where(text => text.Length > 0));
        var replacement = joined + lines[^1].Delimiter;
        var sourceStart = lines[0].Line.Offset;
        var sourceLength = lines.Sum(item => item.Line.TotalLength);
        using (BeginUndoGroup(document)) document.Replace(sourceStart, sourceLength, replacement);
        return new EditorCommandResult(sourceStart, joined.Length);
    }

    private static IReadOnlyList<string> CollapseBlankLines(IReadOnlyList<string> lines)
    {
        var result = new List<string>(lines.Count);
        var inBlankRun = false;
        foreach (var line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                result.Add(line);
                inBlankRun = false;
            }
            else if (!inBlankRun)
            {
                result.Add(string.Empty);
                inBlankRun = true;
            }
        }

        return result;
    }

    private static EditorCommandResult ConvertLineEndings(TextDocument document, string delimiter)
    {
        var lines = document.Lines.ToArray();
        var replacement = string.Concat(lines.Select(line =>
            document.GetText(line.Offset, line.Length) +
            (line.DelimiterLength == 0 ? string.Empty : delimiter)));
        if (!string.Equals(replacement, document.Text, StringComparison.Ordinal))
        {
            using (BeginUndoGroup(document)) document.Replace(0, document.TextLength, replacement);
        }

        return new EditorCommandResult(0, document.TextLength);
    }

    private static IReadOnlyList<string> GetLineTexts(TextDocument document, (int FirstLine, int LastLine) range) =>
        Enumerable.Range(range.FirstLine, range.LastLine - range.FirstLine + 1)
            .Select(number => document.GetLineByNumber(number))
            .Select(line => document.GetText(line.Offset, line.Length))
            .ToArray();

    private static int GetRangeLength(TextDocument document, (int FirstLine, int LastLine) range)
    {
        var first = document.GetLineByNumber(range.FirstLine);
        var last = document.GetLineByNumber(range.LastLine);
        return Math.Min(document.TextLength - first.Offset, last.Offset + last.Length - first.Offset);
    }

    private static bool IsGeneratedNumbering(IReadOnlyList<string> lines)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            var match = GeneratedNumberPrefix.Match(lines[index]);
            if (!match.Success || !int.TryParse(match.Groups["number"].Value, out var number) || number != index + 1)
            {
                return false;
            }
        }

        return true;
    }

    private static string RemoveGeneratedNumberPrefix(string line)
    {
        var match = GeneratedNumberPrefix.Match(line);
        return match.Success ? line[match.Length..] : line;
    }

    private static readonly Regex GeneratedNumberPrefix = new(
        "^[ ]*(?<number>[1-9][0-9]*)\\. ",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static EditorCommandResult ApplySelection(TextDocument document, int start, int length, Func<string, string> transform)
    {
        if (length <= 0) return new EditorCommandResult(start, 0);
        var original = document.GetText(start, length);
        var transformed = transform(original);
        if (!string.Equals(original, transformed, StringComparison.Ordinal))
        {
            using (BeginUndoGroup(document)) document.Replace(start, length, transformed);
        }

        return new EditorCommandResult(start, length);
    }

    private static EditorCommandResult TryApplySelection(
        TextDocument document,
        int start,
        int length,
        Func<string, string> transform,
        string commandName,
        params Type[] expectedExceptions)
    {
        if (length <= 0) return new EditorCommandResult(start, 0);

        var original = document.GetText(start, length);
        try
        {
            var transformed = transform(original);
            if (!string.Equals(original, transformed, StringComparison.Ordinal))
            {
                using (BeginUndoGroup(document)) document.Replace(start, length, transformed);
            }

            return new EditorCommandResult(start, transformed.Length);
        }
        catch (Exception exception) when (expectedExceptions.Any(type => type.IsInstanceOfType(exception)))
        {
            DeveloperDiagnostics.LogDecision(
                "Editor",
                commandName,
                $"{commandName} left the selection unchanged because the input was invalid.",
                "InvalidInput");
            return new EditorCommandResult(start, length);
        }
    }

    private static EditorCommandResult TryFormatJson(TextDocument document, int start, int length, bool writeIndented, string commandName)
    {
        return TryApplySelection(document, start, length, text =>
        {
            using var parsed = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(parsed.RootElement, new JsonSerializerOptions { WriteIndented = writeIndented });
        }, commandName, typeof(JsonException));
    }

    private static string ConvertListToPowerShellArrayText(string documentText, string selectedText, int indentationSize)
    {
        var lines = SplitLines(selectedText);
        var values = lines.Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        if (values.Length == 0)
        {
            throw new FormatException("The selection contains no nonblank lines.");
        }

        var firstValue = lines.First(line => line.Trim().Length > 0);
        var baseIndent = firstValue[..(firstValue.Length - firstValue.TrimStart(' ', '\t').Length)];
        var memberIndent = baseIndent + (baseIndent.Contains('\t', StringComparison.Ordinal) ? "\t" : new string(' ', Math.Max(1, indentationSize)));
        var lineEnding = DetectLineEnding(documentText, selectedText);
        var renderedValues = values.Select(RenderArrayValue);
        var result = string.Join(lineEnding, new[] { baseIndent + "@(" }.Concat(renderedValues.Select(value => memberIndent + value)).Append(baseIndent + ")"));
        return EndsWithLineBreak(selectedText) ? result + lineEnding : result;
    }

    private static string ConvertPowerShellArrayToListText(string documentText, string selectedText)
    {
        var lines = SplitLines(selectedText);
        var first = Array.FindIndex(lines, line => line.Trim().Length > 0);
        var last = Array.FindLastIndex(lines, line => line.Trim().Length > 0);
        if (first < 0 || last <= first || lines[first].Trim() != "@(" || lines[last].Trim() != ")")
        {
            throw new FormatException("The selection is not a supported PowerShell array.");
        }

        var baseIndent = lines[first][..(lines[first].Length - lines[first].TrimStart(' ', '\t').Length)];
        var values = new List<string>();
        for (var index = first + 1; index < last; index++)
        {
            var value = lines[index].Trim();
            if (value.Length == 0) continue;
            if (value.EndsWith(",", StringComparison.Ordinal)) value = value[..^1].TrimEnd();
            values.Add(DecodeArrayValue(value));
        }

        var lineEnding = DetectLineEnding(documentText, selectedText);
        var result = string.Join(lineEnding, values.Select(value => baseIndent + value));
        return EndsWithLineBreak(selectedText) ? result + lineEnding : result;
    }

    private static string RenderArrayValue(string value)
    {
        if (IsSimplePowerShellValue(value) || SingleQuotedLiteral.IsMatch(value) || DoubleQuotedLiteral.IsMatch(value)) return value;
        return $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    private static string DecodeArrayValue(string value)
    {
        if (SingleQuotedLiteral.IsMatch(value)) return value[1..^1].Replace("''", "'", StringComparison.Ordinal);
        if (DoubleQuotedLiteral.IsMatch(value)) return value[1..^1];
        if (IsSimplePowerShellValue(value)) return value;
        throw new FormatException("The array contains an unsupported expression.");
    }

    private static bool IsSimplePowerShellValue(string value) =>
        SimpleNumberLiteral.IsMatch(value) || SimplePowerShellLiteral.IsMatch(value);

    private static string[] SplitLines(string text) => text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);

    private static string DetectLineEnding(string documentText, string selectedText)
    {
        if (selectedText.Contains("\r\n", StringComparison.Ordinal)) return "\r\n";
        if (selectedText.Contains('\n')) return "\n";
        if (documentText.Contains("\r\n", StringComparison.Ordinal)) return "\r\n";
        return "\n";
    }

    private static bool EndsWithLineBreak(string text) => text.EndsWith("\r\n", StringComparison.Ordinal) || text.EndsWith('\n') || text.EndsWith('\r');

    private static readonly Regex SimpleNumberLiteral = new(
        @"^[+-]?(?:\d+(?:\.\d+)?|\.\d+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SimplePowerShellLiteral = new(
        @"^\$(?:true|false|null)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SingleQuotedLiteral = new(
        @"^'(?:''|[^'])*'$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DoubleQuotedLiteral = new(
        "^\"[^\"]*\"$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static EditorCommandResult ApplyLines(TextDocument document, int start, int length, Func<IReadOnlyList<string>, IReadOnlyList<string>> transform)
    {
        var range = GetLineRange(document, start, length);
        var lines = Enumerable.Range(range.FirstLine, range.LastLine - range.FirstLine + 1).Select(number => document.GetLineByNumber(number)).ToArray();
        var sourceTexts = lines.Select(line => document.GetText(line.Offset, line.Length)).ToArray();
        var delimiters = lines.Select(line => document.GetText(line.Offset + line.Length, line.TotalLength - line.Length)).ToArray();
        var transformed = transform(sourceTexts);
        var transformedDelimiters = delimiters.Take(transformed.Count).ToArray();
        if (transformed.Count > 0 && delimiters[^1].Length == 0)
        {
            transformedDelimiters = transformedDelimiters
                .Take(transformed.Count - 1)
                .Append(string.Empty)
                .ToArray();
        }

        var replacement = string.Concat(transformed.Select((text, index) => text + transformedDelimiters[index]));
        var sourceStart = lines[0].Offset;
        var sourceLength = lines.Sum(line => line.TotalLength);
        using (BeginUndoGroup(document)) document.Replace(sourceStart, sourceLength, replacement);
        var resultLength = transformed.Count == 0 ? 0 : transformed.Sum(text => text.Length) + delimiters.Take(transformed.Count).Sum(delimiter => delimiter.Length);
        return new EditorCommandResult(sourceStart, Math.Min(resultLength, document.TextLength - sourceStart));
    }

    private static (int FirstLine, int LastLine) GetLineRange(TextDocument document, int start, int length)
    {
        var safeStart = Math.Clamp(start, 0, document.TextLength);
        var safeEnd = Math.Clamp(start + Math.Max(0, length), 0, document.TextLength);
        var first = document.GetLineByOffset(safeStart).LineNumber;
        var last = document.GetLineByOffset(safeEnd).LineNumber;
        if (length > 0 && last > first && safeEnd == document.GetLineByNumber(last).Offset) last--;
        return (first, Math.Max(first, last));
    }

    private static IDisposable BeginUndoGroup(TextDocument document)
    {
        document.UndoStack.StartUndoGroup();
        return new UndoGroup(document);
    }

    private sealed class UndoGroup(TextDocument document) : IDisposable
    {
        public void Dispose() => document.UndoStack.EndUndoGroup();
    }
}
