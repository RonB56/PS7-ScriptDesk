using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace PowerShellStudio.Shell.Editor
{
    /// <summary>
    /// PowerShell syntax colorizer for AvalonEdit.
    ///
    /// Primary mode: colours spans returned by PowerShell's own parser token stream.
    /// Fallback mode: uses lightweight regex rules until the first parser response arrives
    /// or while live typing invalidates the previous token snapshot.
    /// </summary>
    public sealed class PowerShellSyntaxColorizer : DocumentColorizingTransformer
    {
        private static readonly Regex TokenRegex = new(
            @"(?<Comment>#.*$)" +
            @"|(?<BlockCommentStart><#)" +
            @"|(?<HereStringDQ>@"")" +
            @"|(?<HereStringSQ>@')" +
            @"|(?<String>""(?:\\.|[^""\\])*""|'(?:''|[^'])*')" +
            @"|(?<Variable>\$[\w:\?]+)" +
            @"|(?<Parameter>-[A-Za-z][\w-]*)" +
            @"|(?<Cmdlet>\b[A-Za-z][\w]*-[A-Za-z][\w-]*\b)" +
            @"|(?<Keyword>\b(?:begin|break|catch|class|continue|data|define|do|dynamicparam|else|elseif|end|enum|exit|filter|finally|for|foreach|from|function|hidden|if|in|param|process|return|switch|throw|trap|try|until|using|var|while|workflow|parallel|sequence|default)\b)" +
            @"|(?<Type>\[(?:[\w.\[\],]+)\])" +
            @"|(?<Number>\b\d+(?:\.\d+)?\b)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

        private static readonly HashSet<string> KeywordKinds = new(StringComparer.OrdinalIgnoreCase)
        {
            "Begin", "Break", "Catch", "Class", "Continue", "Data", "Do", "DynamicParam",
            "Else", "ElseIf", "End", "Enum", "Exit", "Filter", "Finally", "For", "ForEach",
            "From", "Function", "If", "In", "Param", "Process", "Return", "Switch", "Throw",
            "Trap", "Try", "Until", "Using", "Var", "While", "Workflow", "Parallel", "Sequence",
            "Default", "Hidden", "Configuration", "Define"
        };

        private static readonly HashSet<string> KeywordTexts = new(StringComparer.OrdinalIgnoreCase)
        {
            "begin", "break", "catch", "class", "continue", "data", "define", "do", "dynamicparam",
            "else", "elseif", "end", "enum", "exit", "filter", "finally", "for", "foreach", "from",
            "function", "hidden", "if", "in", "param", "process", "return", "switch", "throw", "trap",
            "try", "until", "using", "var", "while", "workflow", "parallel", "sequence", "default",
            "configuration"
        };

        private static readonly WpfBrush FallbackComment   = FreezeBrush(Colors.ForestGreen);
        private static readonly WpfBrush FallbackString    = FreezeBrush(Colors.SaddleBrown);
        private static readonly WpfBrush FallbackVariable  = FreezeBrush(Colors.DarkCyan);
        private static readonly WpfBrush FallbackParameter = FreezeBrush(Colors.MediumVioletRed);
        private static readonly WpfBrush FallbackCmdlet    = FreezeBrush(Colors.DarkBlue);
        private static readonly WpfBrush FallbackKeyword   = FreezeBrush(Colors.MediumBlue);
        private static readonly WpfBrush FallbackType      = FreezeBrush(Colors.DarkMagenta);
        private static readonly WpfBrush FallbackNumber    = FreezeBrush(Colors.DarkOrange);

        private IReadOnlyList<SyntaxTokenInfo> _parserTokens = Array.Empty<SyntaxTokenInfo>();
        private ITextSourceVersion? _cachedDocumentVersion;
        private List<ColoredRegion>? _cachedRegions;

        public void SetParserTokens(IReadOnlyList<SyntaxTokenInfo>? parserTokens)
        {
            _parserTokens = parserTokens is null || parserTokens.Count == 0
                ? Array.Empty<SyntaxTokenInfo>()
                : parserTokens
                    .Where(token => token.Length > 0)
                    .OrderBy(token => token.StartOffset)
                    .ToList();
        }

        public void ClearParserTokens()
        {
            _parserTokens = Array.Empty<SyntaxTokenInfo>();
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            var doc = CurrentContext.Document;
            var lineText = doc.GetText(line);
            if (string.IsNullOrEmpty(lineText)) return;

            var lineOffset = line.Offset;
            var lineEnd = line.EndOffset;
            var occupied = new bool[lineText.Length];

            var hasParserTokens = _parserTokens.Count > 0;
            if (hasParserTokens)
            {
                ApplyParserTokens(lineOffset, lineEnd, occupied);
            }
            else
            {
                EnsureRegionsUpToDate(doc);
                ApplyMultilineFallbackRegions(lineOffset, lineEnd, occupied);
            }

            ApplyRegexFallbackTokens(lineText, lineOffset, occupied);
        }

        private void ApplyParserTokens(int lineOffset, int lineEnd, bool[] occupied)
        {
            foreach (var token in _parserTokens)
            {
                if (token.EndOffset <= lineOffset)
                {
                    continue;
                }

                if (token.StartOffset >= lineEnd)
                {
                    break;
                }

                var groupName = GetParserGroupName(token);
                if (groupName is null)
                {
                    continue;
                }

                var tokenStart = Math.Max(token.StartOffset, lineOffset);
                var tokenEnd = Math.Min(token.EndOffset, lineEnd);
                if (tokenEnd <= tokenStart)
                {
                    continue;
                }

                var brush = GetBrush(groupName) ?? FallbackComment;
                var isBold = groupName is "Keyword" or "Cmdlet";
                ChangeLinePart(tokenStart, tokenEnd, element =>
                {
                    element.TextRunProperties.SetForegroundBrush(brush);
                    if (isBold)
                    {
                        element.TextRunProperties.SetTypeface(new Typeface(
                            element.TextRunProperties.Typeface.FontFamily,
                            element.TextRunProperties.Typeface.Style,
                            FontWeights.SemiBold,
                            element.TextRunProperties.Typeface.Stretch));
                    }
                });

                MarkOccupied(occupied, tokenStart - lineOffset, tokenEnd - tokenStart);
            }
        }

        private void ApplyMultilineFallbackRegions(int lineOffset, int lineEnd, bool[] occupied)
        {
            if (_cachedRegions is null)
            {
                return;
            }

            foreach (var region in _cachedRegions)
            {
                if (region.End <= lineOffset || region.Start >= lineEnd) continue;

                var regionStart = Math.Max(region.Start, lineOffset);
                var regionEnd = Math.Min(region.End, lineEnd);
                var brush = region.Kind == RegionKind.BlockComment ? GetBrush("Comment") ?? FallbackComment : GetBrush("String") ?? FallbackString;

                ChangeLinePart(regionStart, regionEnd, el => el.TextRunProperties.SetForegroundBrush(brush));
                MarkOccupied(occupied, regionStart - lineOffset, regionEnd - regionStart);
            }
        }

        private void ApplyRegexFallbackTokens(string lineText, int lineOffset, bool[] occupied)
        {
            foreach (Match match in TokenRegex.Matches(lineText))
            {
                if (!match.Success || match.Length == 0) continue;

                var groupName = GetWinningGroupName(match);
                if (groupName is "BlockCommentStart" or "HereStringDQ" or "HereStringSQ") continue;

                var brush = GetBrush(groupName);
                if (brush is null) continue;
                if (Overlaps(occupied, match.Index, match.Length)) continue;

                MarkOccupied(occupied, match.Index, match.Length);
                var startOffset = lineOffset + match.Index;
                var endOffset = startOffset + match.Length;

                var isBold = groupName is "Keyword" or "Cmdlet";
                ChangeLinePart(startOffset, endOffset, element =>
                {
                    element.TextRunProperties.SetForegroundBrush(brush);
                    if (isBold)
                    {
                        element.TextRunProperties.SetTypeface(new Typeface(
                            element.TextRunProperties.Typeface.FontFamily,
                            element.TextRunProperties.Typeface.Style,
                            FontWeights.SemiBold,
                            element.TextRunProperties.Typeface.Stretch));
                    }
                });
            }
        }

        private static string? GetParserGroupName(SyntaxTokenInfo token)
        {
            var kind = token.Kind?.Trim() ?? string.Empty;
            var text = token.Text?.Trim() ?? string.Empty;

            if (kind.Contains("Comment", StringComparison.OrdinalIgnoreCase))
            {
                return "Comment";
            }

            if (kind.Contains("String", StringComparison.OrdinalIgnoreCase) ||
                kind.Contains("HereString", StringComparison.OrdinalIgnoreCase))
            {
                return "String";
            }

            if (string.Equals(kind, "Variable", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kind, "SplattedVariable", StringComparison.OrdinalIgnoreCase))
            {
                return "Variable";
            }

            if (string.Equals(kind, "Parameter", StringComparison.OrdinalIgnoreCase))
            {
                return "Parameter";
            }

            if (kind.Contains("Number", StringComparison.OrdinalIgnoreCase) ||
                kind.Contains("Integer", StringComparison.OrdinalIgnoreCase) ||
                kind.Contains("Decimal", StringComparison.OrdinalIgnoreCase))
            {
                return "Number";
            }

            if (kind.Contains("Type", StringComparison.OrdinalIgnoreCase))
            {
                return "Type";
            }

            if (KeywordKinds.Contains(kind) || KeywordTexts.Contains(text))
            {
                return "Keyword";
            }

            if (string.Equals(kind, "Command", StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(kind, "Identifier", StringComparison.OrdinalIgnoreCase) && text.Contains("-", StringComparison.Ordinal)))
            {
                return "Cmdlet";
            }

            return null;
        }

        private void EnsureRegionsUpToDate(TextDocument document)
        {
            var version = document.Version;
            if (ReferenceEquals(_cachedDocumentVersion, version)) return;

            _cachedDocumentVersion = version;
            _cachedRegions = BuildRegions(document.Text);
        }

        private static List<ColoredRegion> BuildRegions(string text)
        {
            var regions = new List<ColoredRegion>();
            var i = 0;
            var len = text.Length;

            while (i < len)
            {
                if (i + 1 < len && text[i] == '<' && text[i + 1] == '#')
                {
                    var start = i;
                    i += 2;
                    while (i + 1 < len && !(text[i] == '#' && text[i + 1] == '>')) i++;
                    var end = i + 2 < len ? i + 2 : len;
                    regions.Add(new ColoredRegion(start, end, RegionKind.BlockComment));
                    i = end;
                    continue;
                }

                if (i + 1 < len && text[i] == '@' && text[i + 1] == '"')
                {
                    var start = i;
                    i += 2;
                    while (i < len)
                    {
                        if (text[i] == '"' && i + 1 < len && text[i + 1] == '@')
                        {
                            i += 2; break;
                        }
                        i++;
                    }
                    regions.Add(new ColoredRegion(start, i, RegionKind.HereString));
                    continue;
                }

                if (i + 1 < len && text[i] == '@' && text[i + 1] == '\'')
                {
                    var start = i;
                    i += 2;
                    while (i < len)
                    {
                        if (text[i] == '\'' && i + 1 < len && text[i + 1] == '@')
                        {
                            i += 2; break;
                        }
                        i++;
                    }
                    regions.Add(new ColoredRegion(start, i, RegionKind.HereString));
                    continue;
                }

                if (text[i] == '"')
                {
                    i++;
                    while (i < len && text[i] != '"') { if (text[i] == '`') i++; i++; }
                    if (i < len) i++;
                    continue;
                }

                if (text[i] == '\'')
                {
                    i++;
                    while (i < len && !(text[i] == '\'' && (i + 1 >= len || text[i + 1] != '\''))) i++;
                    if (i < len) i++;
                    continue;
                }

                if (text[i] == '#')
                {
                    while (i < len && text[i] != '\n') i++;
                    continue;
                }

                i++;
            }

            return regions;
        }

        private static string? GetWinningGroupName(Match match)
        {
            foreach (var name in new[] { "Comment", "BlockCommentStart", "HereStringDQ", "HereStringSQ",
                                         "String", "Variable", "Parameter", "Cmdlet", "Keyword", "Type", "Number" })
            {
                if (match.Groups[name].Success) return name;
            }
            return null;
        }

        private static WpfBrush? GetBrush(string? groupName) => groupName switch
        {
            "Comment"   => ResolveBrush("Theme.Syntax.Comment",   FallbackComment),
            "String"    => ResolveBrush("Theme.Syntax.String",     FallbackString),
            "Variable"  => ResolveBrush("Theme.Syntax.Variable",   FallbackVariable),
            "Parameter" => ResolveBrush("Theme.Syntax.Parameter",  FallbackParameter),
            "Cmdlet"    => ResolveBrush("Theme.Syntax.Cmdlet",     FallbackCmdlet),
            "Keyword"   => ResolveBrush("Theme.Syntax.Keyword",    FallbackKeyword),
            "Type"      => ResolveBrush("Theme.Syntax.Type",       FallbackType),
            "Number"    => ResolveBrush("Theme.Syntax.Number",     FallbackNumber),
            _           => null,
        };

        private static WpfBrush ResolveBrush(string resourceKey, WpfBrush fallback)
        {
            if (System.Windows.Application.Current?.Resources[resourceKey] is WpfBrush b) return b;
            return fallback;
        }

        private static WpfBrush FreezeBrush(WpfColor color)
        {
            var b = new WpfSolidColorBrush(color);
            b.Freeze();
            return b;
        }

        private static bool Overlaps(IReadOnlyList<bool> occupied, int start, int length)
        {
            var end = Math.Min(start + length, occupied.Count);
            for (var i = start; i < end; i++) if (occupied[i]) return true;
            return false;
        }

        private static void MarkOccupied(IList<bool> occupied, int start, int length)
        {
            var normalizedStart = Math.Max(0, start);
            var end = Math.Min(normalizedStart + Math.Max(0, length), occupied.Count);
            for (var i = normalizedStart; i < end; i++) occupied[i] = true;
        }

        private enum RegionKind { BlockComment, HereString }

        private readonly struct ColoredRegion
        {
            public ColoredRegion(int start, int end, RegionKind kind)
            {
                Start = start; End = end; Kind = kind;
            }
            public int Start { get; }
            public int End { get; }
            public RegionKind Kind { get; }
        }
    }
}
