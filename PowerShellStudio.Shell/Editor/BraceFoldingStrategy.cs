using System;
using System.Collections.Generic;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;

namespace PowerShellStudio.Shell.Editor
{
    /// <summary>
    /// Produces fold regions for:
    /// <list type="bullet">
    ///   <item><c>{ ... }</c> brace blocks (functions, if, foreach, try/catch, …)</item>
    ///   <item><c>#region … #endregion</c> named regions</item>
    /// </list>
    /// Only multi-line blocks become foldable so single-line hash-tables and inline
    /// closures are not cluttered with fold arrows.
    /// </summary>
    public sealed class BraceFoldingStrategy
    {
        /// <summary>
        /// Recomputes fold sections for <paramref name="document"/> and pushes them to
        /// <paramref name="manager"/>.  Existing folded sections remain folded where possible.
        /// </summary>
        public void UpdateFoldings(FoldingManager manager, TextDocument document)
        {
            manager.UpdateFoldings(CreateFoldings(document), -1);
        }

        private static IEnumerable<NewFolding> CreateFoldings(TextDocument document)
        {
            var foldings = new List<NewFolding>();
            var openBraces = new Stack<int>();
            var openRegions = new Stack<(int Offset, string Name)>();
            var text = document.Text;

            // Single pass: detect { } pairs and #region / #endregion pairs.
            // We scan line-by-line for region directives and character-by-character for braces.
            // Characters inside strings / comments are not excluded here because the folding
            // strategy is purely structural (brace counting) — minor false positives are
            // acceptable.

            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];

                if (ch == '{')
                {
                    openBraces.Push(i);
                    continue;
                }

                if (ch == '}')
                {
                    if (openBraces.Count > 0)
                    {
                        var startOffset = openBraces.Pop();
                        var endOffset = i + 1;
                        if (ContainsNewline(text, startOffset, endOffset))
                            foldings.Add(new NewFolding(startOffset, endOffset));
                    }
                    continue;
                }

                if (ch == '#')
                {
                    // Check for #region or #endregion at the start of the logical line
                    var lineStart = GetLineStart(text, i);
                    var lineEnd = GetLineEnd(text, i);
                    var lineContent = text.Substring(lineStart, lineEnd - lineStart).TrimStart();

                    if (lineContent.StartsWith("#region", StringComparison.OrdinalIgnoreCase))
                    {
                        var regionName = lineContent.Length > 7 ? lineContent[7..].Trim() : string.Empty;
                        openRegions.Push((lineStart, regionName));
                        // Skip to end of line
                        i = lineEnd - 1;
                        continue;
                    }

                    if (lineContent.StartsWith("#endregion", StringComparison.OrdinalIgnoreCase))
                    {
                        if (openRegions.Count > 0)
                        {
                            var (regionStart, regionName) = openRegions.Pop();
                            var regionEnd = lineEnd;
                            if (ContainsNewline(text, regionStart, regionEnd))
                            {
                                var fold = new NewFolding(regionStart, regionEnd);
                                if (!string.IsNullOrWhiteSpace(regionName))
                                    fold.Name = $"#region {regionName}";
                                foldings.Add(fold);
                            }
                        }
                        i = lineEnd - 1;
                        continue;
                    }
                }
            }

            foldings.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
            return foldings;
        }

        private static bool ContainsNewline(string text, int start, int end)
        {
            for (var i = start; i < end && i < text.Length; i++)
                if (text[i] == '\n') return true;
            return false;
        }

        private static int GetLineStart(string text, int offset)
        {
            var i = offset;
            while (i > 0 && text[i - 1] != '\n') i--;
            return i;
        }

        private static int GetLineEnd(string text, int offset)
        {
            var i = offset;
            while (i < text.Length && text[i] != '\r' && text[i] != '\n') i++;
            return i;
        }
    }
}
