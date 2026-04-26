using System;
using System.Collections.Generic;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Snippets;

namespace PowerShellStudio.Shell.Editor
{
    /// <summary>
    /// Provides AvalonEdit <see cref="Snippet"/> insertions for common PowerShell structures.
    /// Snippets are triggered from the IntelliSense completion list when the user types a
    /// recognised prefix (e.g. <c>func</c>, <c>if</c>, <c>try</c>).
    /// </summary>
    public sealed class PowerShellSnippetProvider
    {
        private readonly Dictionary<string, SnippetDefinition> _snippets;

        public PowerShellSnippetProvider()
        {
            _snippets = BuildSnippets();
        }

        /// <summary>Returns snippet completion items whose prefix starts with <paramref name="fragment"/>.</summary>
        public IEnumerable<ICompletionData> GetCompletions(string fragment)
        {
            foreach (var kv in _snippets)
            {
                var filter = fragment ?? string.Empty;
                if (kv.Key.StartsWith(filter, StringComparison.OrdinalIgnoreCase) ||
                    kv.Value.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    yield return new SnippetCompletionData(kv.Value);
                }
            }
        }

        private static Dictionary<string, SnippetDefinition> BuildSnippets()
        {
            return new Dictionary<string, SnippetDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                ["func"] = new SnippetDefinition(
                    prefix:      "func",
                    description: "function definition",
                    text:        "function ${FunctionName} {\r\n    ${param()}\r\n    ${cursor}\r\n}"),


                ["advancedfunc"] = new SnippetDefinition(
                    prefix:      "advancedfunc",
                    description: "advanced function with CmdletBinding and param block",
                    text:        "function ${FunctionName} {\r\n    [CmdletBinding()]\r\n    param (\r\n        [Parameter(Mandatory)]\r\n        [string] $${ParameterName}\r\n    )\r\n\r\n    begin {\r\n    }\r\n\r\n    process {\r\n        ${cursor}\r\n    }\r\n\r\n    end {\r\n    }\r\n}"),

                ["pipeline"] = new SnippetDefinition(
                    prefix:      "pipeline",
                    description: "pipeline with Where-Object and ForEach-Object",
                    text:        "${source} | Where-Object { ${condition} } | ForEach-Object {\r\n    ${cursor}\r\n}"),

                ["class"] = new SnippetDefinition(
                    prefix:      "class",
                    description: "PowerShell class",
                    text:        "class ${ClassName} {\r\n    [string] $${PropertyName}\r\n\r\n    ${ClassName}([string] $${PropertyName}) {\r\n        $this.${PropertyName} = $${PropertyName}\r\n    }\r\n\r\n    [void] ${MethodName}() {\r\n        ${cursor}\r\n    }\r\n}"),

                ["ifelse"] = new SnippetDefinition(
                    prefix:      "ifelse",
                    description: "if / else block",
                    text:        "if (${condition}) {\r\n    ${cursor}\r\n} else {\r\n    \r\n}"),

                ["ifelseif"] = new SnippetDefinition(
                    prefix:      "ifelseif",
                    description: "if / elseif / else block",
                    text:        "if (${condition}) {\r\n    ${cursor}\r\n} elseif (${condition2}) {\r\n    \r\n} else {\r\n    \r\n}"),

                ["try"] = new SnippetDefinition(
                    prefix:      "try",
                    description: "try / catch / finally block",
                    text:        "try {\r\n    ${cursor}\r\n} catch {\r\n    Write-Error $_.Exception.Message\r\n} finally {\r\n    \r\n}"),

                ["foreach"] = new SnippetDefinition(
                    prefix:      "foreach",
                    description: "foreach loop",
                    text:        "foreach (${item} in ${collection}) {\r\n    ${cursor}\r\n}"),

                ["for"] = new SnippetDefinition(
                    prefix:      "for",
                    description: "for loop",
                    text:        "for ($i = 0; $i -lt ${count}; $i++) {\r\n    ${cursor}\r\n}"),

                ["while"] = new SnippetDefinition(
                    prefix:      "while",
                    description: "while loop",
                    text:        "while (${condition}) {\r\n    ${cursor}\r\n}"),

                ["dowhile"] = new SnippetDefinition(
                    prefix:      "dowhile",
                    description: "do / while loop",
                    text:        "do {\r\n    ${cursor}\r\n} while (${condition})"),

                ["switch"] = new SnippetDefinition(
                    prefix:      "switch",
                    description: "switch statement",
                    text:        "switch (${expression}) {\r\n    '${value1}' { ${cursor} }\r\n    default    { }\r\n}"),

                ["param"] = new SnippetDefinition(
                    prefix:      "param",
                    description: "param block",
                    text:        "param (\r\n    [Parameter(Mandatory)]\r\n    [string] $${ParameterName}${cursor}\r\n)"),

                ["help"] = new SnippetDefinition(
                    prefix:      "help",
                    description: "comment-based help block",
                    text:        "<#\r\n.SYNOPSIS\r\n    ${Synopsis}\r\n\r\n.DESCRIPTION\r\n    ${Description}\r\n\r\n.PARAMETER ${ParameterName}\r\n    ${ParameterDescription}\r\n\r\n.EXAMPLE\r\n    ${cursor}\r\n#>"),

                ["pscustomobject"] = new SnippetDefinition(
                    prefix:      "pscustomobject",
                    description: "ordered PSCustomObject output object",
                    text:        "[pscustomobject][ordered] @{\r\n    ${PropertyName} = ${Value}\r\n    ${cursor}\r\n}"),

                ["beginprocessend"] = new SnippetDefinition(
                    prefix:      "beginprocessend",
                    description: "advanced pipeline function blocks",
                    text:        "begin {\r\n    ${begin}\r\n}\r\n\r\nprocess {\r\n    ${cursor}\r\n}\r\n\r\nend {\r\n    ${end}\r\n}"),

                ["requires"] = new SnippetDefinition(
                    prefix:      "requires",
                    description: "#requires statement for PowerShell version/module",
                    text:        "#requires -Version ${Version}\r\n#requires -Modules ${ModuleName}\r\n${cursor}"),

                ["usingnamespace"] = new SnippetDefinition(
                    prefix:      "usingnamespace",
                    description: "using namespace statement",
                    text:        "using namespace ${Namespace}\r\n${cursor}"),

                ["usingmodule"] = new SnippetDefinition(
                    prefix:      "usingmodule",
                    description: "using module statement",
                    text:        "using module ${ModuleName}\r\n${cursor}"),

                ["validatedparam"] = new SnippetDefinition(
                    prefix:      "validatedparam",
                    description: "validated mandatory string parameter",
                    text:        "[Parameter(Mandatory)]\r\n[ValidateNotNullOrEmpty()]\r\n[string] $${ParameterName}${cursor}"),

                ["region"] = new SnippetDefinition(
                    prefix:      "region",
                    description: "#region / #endregion block",
                    text:        "#region ${RegionName}\r\n${cursor}\r\n#endregion ${RegionName}"),
            };
        }

        // -------------------------------------------------------------------------
        // Inner types
        // -------------------------------------------------------------------------

        public sealed class SnippetDefinition
        {
            public string Prefix      { get; }
            public string Description { get; }
            public string Text        { get; }

            public SnippetDefinition(string prefix, string description, string text)
            {
                Prefix      = prefix;
                Description = description;
                Text        = text;
            }

            /// <summary>
            /// Builds an AvalonEdit <see cref="Snippet"/> from the definition, converting
            /// <c>${name}</c> placeholders to <see cref="SnippetReplaceableTextElement"/> objects
            /// and <c>${cursor}</c> to a <see cref="SnippetCaretElement"/>.
            /// </summary>
            public Snippet BuildSnippet()
            {
                var snippet = new Snippet();
                var remaining = Text;

                while (remaining.Length > 0)
                {
                    var start = remaining.IndexOf("${", StringComparison.Ordinal);
                    if (start < 0)
                    {
                        snippet.Elements.Add(new SnippetTextElement { Text = remaining });
                        break;
                    }

                    if (start > 0)
                    {
                        snippet.Elements.Add(new SnippetTextElement { Text = remaining[..start] });
                    }

                    var end = remaining.IndexOf('}', start + 2);
                    if (end < 0)
                    {
                        snippet.Elements.Add(new SnippetTextElement { Text = remaining[start..] });
                        break;
                    }

                    var name = remaining[(start + 2)..end];
                    if (string.Equals(name, "cursor", StringComparison.Ordinal))
                    {
                        snippet.Elements.Add(new SnippetCaretElement());
                    }
                    else
                    {
                        snippet.Elements.Add(new SnippetReplaceableTextElement { Text = name });
                    }

                    remaining = remaining[(end + 1)..];
                }

                return snippet;
            }
        }

        private sealed class SnippetCompletionData : ICompletionData
        {
            private readonly SnippetDefinition _definition;

            public SnippetCompletionData(SnippetDefinition definition)
            {
                _definition = definition;
            }

            public System.Windows.Media.ImageSource? Image => null;
            public string Text    => _definition.Prefix;
            public object Content => $"snippet: {_definition.Prefix}";
            public object Description => $"Snippet — {_definition.Description}";
            public double Priority   => 95; // Keep snippets near the top when relevant.

            public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
            {
                // Remove the typed prefix that triggered this completion, then insert snippet.
                textArea.Document.Remove(completionSegment.Offset, completionSegment.Length);
                _definition.BuildSnippet().Insert(textArea);
            }
        }
    }
}
