using System;
using System.Collections.Generic;
using System.Linq;
using PowerShellStudio.Application.Diagnostics;
using PowerShellStudio.Application.Utilities;

namespace PowerShellStudio.Shell.Help
{
    public static class HelpTopicCatalog
    {
        public const string OverviewKey = "App.Overview";

        private static readonly IReadOnlyDictionary<string, HelpTopic> Topics = BuildTopics();
        private static readonly HashSet<string> LoggedMissingKeys = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> LoggedBrokenRelationships = new(StringComparer.OrdinalIgnoreCase);

        static HelpTopicCatalog()
        {
            ValidateTopicRelationships();
        }

        public static bool TryGet(string? key, out HelpTopic topic)
        {
            if (!string.IsNullOrWhiteSpace(key) && Topics.TryGetValue(key, out var resolvedTopic))
            {
                topic = resolvedTopic;
                return true;
            }

            topic = Topics[OverviewKey];
            return false;
        }

        public static HelpTopic Get(string? key, string? source = null)
        {
            if (TryGet(key, out var topic))
            {
                return topic;
            }

            LogMissingKey(key, source);
            return CreateMissingTopic(key);
        }

        public static IReadOnlyList<HelpTopic> GetRelatedTopics(HelpTopic topic)
        {
            if (topic.RelatedTopicKeys.Count == 0)
            {
                return Array.Empty<HelpTopic>();
            }

            var relatedTopics = new List<HelpTopic>();
            foreach (var key in topic.RelatedTopicKeys)
            {
                if (Topics.TryGetValue(key, out var relatedTopic))
                {
                    relatedTopics.Add(relatedTopic);
                }
            }

            return relatedTopics;
        }

        public static IReadOnlyList<HelpTopic> GetAllTopics()
        {
            return Topics.Values
                .OrderBy(static topic => topic.Title, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public static IReadOnlyList<string> ValidateKeys(IEnumerable<string>? keys, string source)
        {
            if (keys is null)
            {
                return Array.Empty<string>();
            }

            var missingKeys = keys
                .Where(static key => !string.IsNullOrWhiteSpace(key))
                .Select(static key => key.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(static key => !Topics.ContainsKey(key))
                .ToArray();

            foreach (var key in missingKeys)
            {
                LogMissingKey(key, source);
            }

            return missingKeys;
        }

        private static IReadOnlyDictionary<string, HelpTopic> BuildTopics()
        {
            var topics = new Dictionary<string, HelpTopic>(StringComparer.OrdinalIgnoreCase)
            {
                ["App.Overview"] = Topic(
                    "App.Overview",
                    $"{ApplicationBranding.PublicName} help overview",
                    $"{ApplicationBranding.PublicName} is a Windows-only WPF PowerShell editor and live ConPTY terminal host with workspace browsing, diagnostics, runtime discovery, metadata-backed IntelliSense, and breakpoint-driven debugging.",
                    "Use this overview when you want the current app-level workflow before drilling into a specific panel or command.",
                    "The app keeps one shared interactive PowerShell session for terminal work, Run, and Run Selection. Session state carries forward until you interrupt or reset that console.",
                    Section("Suggested first workflow", true,
                        "Wait for runtime discovery to finish, then confirm the selected PowerShell runtime in the Explorer pane.",
                        "Open a folder if you want workspace navigation, or open one file if you only need a single script tab.",
                        "Edit in the AvalonEdit editor and watch the diagnostics panel and footer update as you type.",
                        "Use Ctrl+Space for manual completion and watch the metadata status badge if command metadata is still warming up.",
                        "Use Run to send the whole visible editor buffer to the shared live console, or Run Selection to send only highlighted text in the current scope.",
                        "Use Interrupt for Ctrl+C behavior or Reset Console when you need a fresh interactive session.",
                        "Use Debug when you want breakpoint pauses, stepping, variables, and call stack inspection."),
                    Section("Deep help discovery", false,
                        "Hover many controls to see quick help.",
                        "Press F1 for focused help. In the editor, F1 first tries command quick info at the caret and falls back to editor help when no quick-info topic is available.",
                        "Right-click many controls and choose 'What does this do?' for context help.",
                        "Use Help > Context Help or the toolbar Help button if you prefer an explicit command."),
                    Section("Main areas", false,
                        "Explorer: runtime picker, workspace filter, workspace tree, and the open-tabs list.",
                        "Editor: tab strip, document summary, code editor, diagnostics panel, and document footer.",
                        "Console: embedded xterm.js surface backed by a real PowerShell process through Windows ConPTY.",
                        "Debug: variables, call stack, and breakpoint management when the debug panel is shown."),
                    Section("Important behavior notes", false,
                        "Run and Run Selection do not require clean diagnostics. Diagnostics are advisory unless a different command explicitly blocks execution.",
                        "If a saved tab no longer matches the file on disk, Run and Debug use the visible editor content instead of silently executing stale disk content.",
                        "PowerShell command metadata loads in the background per runtime and can be refreshed or deleted from the Run menu.",
                        "There is no dedicated Settings dialog today. Layout, theme, zoom, selected runtime, reopened tabs, workspace path, and context-help enablement are persisted automatically."),
                    related: new[] { "Explorer.Area", "Editor.Area", "Console.Area", "Debug.Area", "Editor.Metadata", "App.Settings", "Help.Troubleshooting", "Help.Packaging" }),

                ["Menu.File"] = Topic(
                    "Menu.File",
                    "File menu",
                    "The File menu is where you create, open, save, close, and exit.",
                    "Use it when you are managing script tabs and files on disk.",
                    "Saving affects the active tab only. Closing a tab is different from deleting a file from disk.",
                    Section("Typical uses", false,
                        "Create a new blank script tab.",
                        "Open a script or text file from disk.",
                        "Open a folder into the workspace explorer.",
                        "Save the active tab or save it under a different name.",
                        "Export the active saved script as a Windows EXE wrapper."),
                    related: new[] { "Command.NewScript", "Command.OpenFile", "Command.Save", "Command.ExportAsExe", "Explorer.WorkspaceTree" }),

                ["Command.NewScript"] = Topic(
                    "Command.NewScript",
                    "New Script",
                    "Creates a new blank editor tab.",
                    "Use it when you want to start from scratch without leaving the current session.",
                    "A new tab starts as an unsaved document until you save it to disk.",
                    Section("What happens", false,
                        "A new tab is added to the tab strip.",
                        "The editor focuses that new tab.",
                        "The tab stays in memory until you close it or exit the app."),
                    Section("Shortcut", false, "Ctrl+N"),
                    related: new[] { "Editor.TabStrip", "Editor.PlusTab", "Command.Save" }),

                ["Command.CloseTab"] = Topic(
                    "Command.CloseTab",
                    "Close Tab",
                    "Closes the current document tab.",
                    "Use it when you are done with a tab and want to reduce clutter.",
                    "Closing a tab is not the same as saving it. Unsaved content can be lost if you close carelessly.",
                    Section("What to watch", false,
                        "Make sure important edits are saved first.",
                        "The active tab changes after the current one closes."),
                    Section("Shortcut", false, "Ctrl+W"),
                    related: new[] { "Editor.TabStrip", "Command.Save" }),

                ["Command.OpenFile"] = Topic(
                    "Command.OpenFile",
                    "Open File",
                    "Opens an existing file into an editor tab.",
                    "Use it when you want to work on one file from disk without loading a workspace folder.",
                    "The Open dialog is filtered to `.ps1`, `.txt`, and all files. Drag-and-drop can also open other readable text-like files, but opening one file does not automatically load sibling files into the workspace tree.",
                    Section("Shortcut", false, "Ctrl+O"),
                    Section("Related behavior", false,
                        "If the file is already open, the app switches to that existing tab instead of opening a duplicate.",
                        "You can also drag supported files onto the editor surface to open them.",
                        "The workspace explorer can open files by double-clicking them in the tree."),
                    related: new[] { "Editor.Surface", "Explorer.WorkspaceTree", "Editor.DragDrop" }),

                ["Command.OpenFolder"] = Topic(
                    "Command.OpenFolder",
                    "Open Folder / Workspace",
                    "Loads a folder into the workspace explorer so you can browse it inside the app.",
                    "Use it when you want project-style navigation, a workspace tree, and a stable startup directory for the shared console.",
                    "Workspace loading is file-system browsing, not project parsing. The tree loads top-level items first and expands child folders on demand.",
                    Section("Shortcut", false, "Ctrl+Shift+O"),
                    Section("What to expect", false,
                        "Top-level entries appear first.",
                        "Subfolders load as you expand them.",
                        "The workspace filter narrows the already loaded workspace view instead of rescanning the whole drive on each keystroke.",
                        "The workspace path becomes the preferred startup directory for the shared console when possible."),
                    related: new[] { "Explorer.Area", "Explorer.WorkspaceTree", "Explorer.WorkspaceFilter" }),

                ["Command.Save"] = Topic(
                    "Command.Save",
                    "Save",
                    "Writes the active tab back to disk.",
                    "Use it after editing a file you want to keep.",
                    "If the tab has never been saved before, Save routes to Save As because there is no existing file path yet.",
                    Section("Shortcut", false, "Ctrl+S"),
                    Section("Related topics", false,
                        "Use Save As when you want a new file name or location.",
                        "If a clean tab's disk file changed behind the editor, the tab is marked stale so Run and Debug use the visible editor content instead of stale disk text.",
                        "Save failures are surfaced in the status/output areas and logged."),
                    related: new[] { "Command.SaveAs", "Command.StartDebug" }),

                ["Command.SaveAs"] = Topic(
                    "Command.SaveAs",
                    "Save As",
                    "Saves the active document under a new path.",
                    "Use it for new tabs, copies, renamed files, or alternate versions.",
                    "If you omit a file extension, the app adds `.ps1`. After a successful Save As, the active tab is rebound to the new path.",
                    Section("Shortcut", false, "Ctrl+Shift+S"),
                    related: new[] { "Command.Save", "Editor.ActiveDocument" }),

                ["Command.ExportAsExe"] = Topic(
                    "Command.ExportAsExe",
                    "Export as EXE",
                    "Builds a Windows executable wrapper for the active saved PowerShell script.",
                    "Use it when you want one clickable EXE that launches the current script through PowerShell 7.",
                    "This V1 export is not native PowerShell compilation. It creates a local .NET wrapper executable that still depends on PowerShell 7 being available when the EXE runs, and it also depends on the local .NET SDK at export time.",
                    Section("What the command does", true,
                        "Verifies that the active tab has script content.",
                        "Requires the active script to be saved first.",
                        "Prompts for the destination .exe path.",
                        "Uses the currently selected PowerShell 7 runtime as the preferred runtime path embedded into the wrapper.",
                        "Runs a local dotnet publish step to generate the launcher executable."),
                    Section("Important limitations", false,
                        "The exported file is a launcher EXE, not a native compile of the script language itself.",
                        "PowerShell 7 must still be installed when the EXE runs.",
                        "The export step requires the local .NET SDK because the IDE builds a small wrapper executable on demand.",
                        "Scripts that depend on $PSScriptRoot pointing at a real on-disk project folder may need extra care because the embedded script is extracted to a temporary file at runtime."),
                    related: new[] { "Menu.File", "Command.Save", "Runtime.List" }),

                ["Command.Find"] = Topic(
                    "Command.Find",
                    "Find",
                    "Searches inside the active editor tab.",
                    "Use it to jump to the next occurrence of text in the current document.",
                    "Find only works on the active editor tab, not across all open files or the whole workspace.",
                    Section("Shortcut", false, "Ctrl+F"),
                    Section("Workflow", true,
                        "Open Find.",
                        "Enter the text you want.",
                        "Choose match-case if needed.",
                        "Run Find again to move to the next match."),
                    related: new[] { "Command.Replace", "Command.GoToLine", "Editor.Surface" }),

                ["Command.Replace"] = Topic(
                    "Command.Replace",
                    "Replace",
                    "Replaces text in the active editor tab.",
                    "Use it when you want to change one match at a time or replace all matches in the active document.",
                    "Replace does not work across every tab at once.",
                    Section("Shortcut", false, "Ctrl+H"),
                    related: new[] { "Command.Find", "Editor.Surface" }),

                ["FindReplace.Dialog"] = Topic(
                    "FindReplace.Dialog",
                    "Find / Replace window",
                    "This dialog focuses search and replace work on the active editor tab.",
                    "Use it when you want a more deliberate text-editing workflow than keyboard shortcuts alone.",
                    "This window works on the active tab only. It is not a multi-file search tool.",
                    related: new[] { "Command.Find", "Command.Replace" }),

                ["FindReplace.FindText"] = Topic(
                    "FindReplace.FindText",
                    "Find text box",
                    "Type the text you want to search for in the active document.",
                    "Use it before Find Next, Replace, or Replace All.",
                    "Empty search text cannot produce useful results.",
                    related: new[] { "FindReplace.Dialog", "FindReplace.FindNext" }),

                ["FindReplace.ReplaceText"] = Topic(
                    "FindReplace.ReplaceText",
                    "Replace text box",
                    "Type the text that should replace the search text.",
                    "Use it for Replace and Replace All.",
                    "This value is ignored if you only use Find Next.",
                    related: new[] { "FindReplace.Dialog", "FindReplace.ReplaceOne", "FindReplace.ReplaceAll" }),

                ["FindReplace.MatchCase"] = Topic(
                    "FindReplace.MatchCase",
                    "Match case",
                    "Makes search and replace care about upper-case and lower-case differences.",
                    "Use it when capitalization matters.",
                    "Leaving it off gives a broader search.",
                    related: new[] { "FindReplace.FindText" }),

                ["FindReplace.WholeWord"] = Topic(
                    "FindReplace.WholeWord",
                    "Whole word",
                    "Limits matches to complete words instead of partial text fragments.",
                    "Use it when you want `Get-Item` to match `Get-Item` but not `Get-ItemProperty`.",
                    "Whole-word matching narrows the result set and can skip text that only contains the search term as part of a larger token.",
                    related: new[] { "FindReplace.FindText" }),

                ["FindReplace.Regex"] = Topic(
                    "FindReplace.Regex",
                    "Use regex",
                    "Treats the Find text as a .NET regular expression.",
                    "Use it when you need pattern-based searching instead of plain text searching.",
                    "Regex search is more powerful, but invalid or overly broad patterns can produce confusing matches. Turn it off when you only need literal text matching.",
                    related: new[] { "FindReplace.FindText" }),

                ["FindReplace.FindNext"] = Topic(
                    "FindReplace.FindNext",
                    "Find Next",
                    "Moves to the next matching occurrence in the active tab.",
                    "Use it to review matches one at a time.",
                    "The search wraps when needed, but only inside the current document.",
                    related: new[] { "FindReplace.FindText", "Command.Find" }),

                ["FindReplace.ReplaceOne"] = Topic(
                    "FindReplace.ReplaceOne",
                    "Replace current match",
                    "Replaces the current match and then continues the search workflow.",
                    "Use it when you want careful one-by-one replacement.",
                    "This is safer than Replace All when you are not sure every occurrence should change.",
                    related: new[] { "FindReplace.ReplaceAll", "Command.Replace" }),

                ["FindReplace.ReplaceAll"] = Topic(
                    "FindReplace.ReplaceAll",
                    "Replace All",
                    "Replaces every match in the active document.",
                    "Use it when you are confident the replacement should happen everywhere in the current tab.",
                    "Replace All can make many changes very quickly, so save thoughtfully afterward.",
                    related: new[] { "FindReplace.ReplaceOne", "Command.Replace" }),

                ["Command.GoToLine"] = Topic(
                    "Command.GoToLine",
                    "Go To Line",
                    "Moves the caret directly to a line number in the active document.",
                    "Use it when a message, error, or teammate refers to a specific line.",
                    "The line number must exist in the current document.",
                    Section("Shortcut", false, "Ctrl+G"),
                    related: new[] { "Editor.Surface", "Status.Caret" }),

                ["GoToLine.Dialog"] = Topic(
                    "GoToLine.Dialog",
                    "Go To Line dialog",
                    "This dialog moves the caret directly to a chosen line in the active editor tab.",
                    "Use it when you want precise navigation to a known line number.",
                    "You must enter a valid line that exists in the current document.",
                    related: new[] { "Command.GoToLine", "GoToLine.LineNumber", "GoToLine.Go" }),

                ["GoToLine.LineNumber"] = Topic(
                    "GoToLine.LineNumber",
                    "Line number box",
                    "Type the target line number here.",
                    "Use it when you know exactly where you want the caret to move.",
                    "The number must fall inside the shown valid range.",
                    related: new[] { "GoToLine.Dialog", "GoToLine.Go" }),

                ["GoToLine.Go"] = Topic(
                    "GoToLine.Go",
                    "Go button",
                    "Moves the editor caret to the valid line number you entered.",
                    "Use it after entering a valid number.",
                    "The button stays disabled until the dialog sees a valid line.",
                    related: new[] { "GoToLine.LineNumber", "Command.GoToLine" }),

                ["Command.ClearOutput"] = Topic(
                    "Command.ClearOutput",
                    "Clear Output",
                    "Clears the visible console area.",
                    "Use it when the console becomes cluttered and you want a cleaner view before the next run.",
                    "If a live PowerShell session is running, the app sends `cls` through that session so PowerShell and PSReadLine redraw their own prompt cleanly. If no session exists yet, the app falls back to a display-only clear.",
                    related: new[] { "Console.Output", "Console.Area" }),

                ["View.Menu"] = Topic(
                    "View.Menu",
                    "View menu",
                    "The View menu controls layout choices such as the explorer, debug panels, zoom, and theme.",
                    "Use it when you want to change how the shell looks or how much information is shown.",
                    "View settings affect the shell layout and readability, but do not change your script logic.",
                    related: new[] { "View.Explorer", "View.DebugPanels", "View.ThemeMenu", "View.Zoom" }),

                ["View.Explorer"] = Topic(
                    "View.Explorer",
                    "Show Explorer",
                    "Shows or hides the entire Explorer pane on the left.",
                    "Use it when you need more space for the editor and console or when you need runtime and workspace navigation.",
                    "Hiding the Explorer does not unload your tabs or workspace. It only changes visibility.",
                    related: new[] { "Explorer.Area", "Explorer.WorkspaceTree" }),

                ["View.DebugPanels"] = Topic(
                    "View.DebugPanels",
                    "Show Debug Panels",
                    "Shows or hides the right-side debug panel area.",
                    "Use it when you want to inspect variables, call stack entries, or breakpoint rows during debugging.",
                    "The panel can be visible even when you are not currently paused in the debugger.",
                    related: new[] { "Debug.Area", "Debug.Variables", "Debug.CallStack", "Debug.Breakpoints" }),

                ["View.Zoom"] = Topic(
                    "View.Zoom",
                    "Zoom controls",
                    "Zoom changes the editor text size for readability.",
                    "Use it when code looks too small or too large.",
                    "Zoom changes the editor font size, not the console font and not the saved file contents.",
                    Section("Shortcuts", false,
                        "Zoom In: Ctrl+=",
                        "Zoom Out: Ctrl+-",
                        "Reset Zoom: Ctrl+0",
                        "You can also hold Ctrl and use the mouse wheel over the editor."),
                    related: new[] { "Status.Zoom", "Editor.Surface" }),

                ["View.ThemeMenu"] = Topic(
                    "View.ThemeMenu",
                    "Theme menu",
                    "Theme changes the shell appearance.",
                    "Use it when you want a different visual style or contrast level.",
                    "Theme changes the app chrome and editor presentation, but not your code or terminal state.",
                    Section("Available choices", false,
                        "Dark",
                        "Light",
                        "ISE Blue"),
                    related: new[] { "Theme.Dark", "Theme.Light", "Theme.IseBlue" }),

                ["Theme.Dark"] = SimpleThemeTopic("Theme.Dark", "Dark theme", "Dark"),
                ["Theme.Light"] = SimpleThemeTopic("Theme.Light", "Light theme", "Light"),
                ["Theme.IseBlue"] = SimpleThemeTopic("Theme.IseBlue", "ISE Blue theme", "ISE Blue"),

                ["Command.RunScript"] = Topic(
                    "Command.RunScript",
                    "Run Script",
                    "Sends the active editor buffer to the shared live PowerShell console.",
                    "Use it when you want the whole visible script to execute inside the app's current interactive PowerShell session.",
                    "Run shares state with the terminal. Variables, location changes, imports, and functions can carry into later commands. While execution is active, Run stays disabled so the same session is not dispatched twice at once.",
                    Section("Shortcut", false, "Ctrl+F5"),
                    Section("Expected result", false,
                        "PowerShell output appears in the embedded console pane.",
                        "If the current saved `.ps1` exactly matches the visible editor text, the run can preserve that file identity. Otherwise the app runs the visible buffer from a temporary snapshot so it never executes stale disk content.",
                        "The shared terminal session stays alive after the run unless the script itself exits it."),
                    related: new[] { "Command.RunSelection", "Console.Area", "Command.StartDebug", "Help.Troubleshooting" }),

                ["Command.RunSelection"] = Topic(
                    "Command.RunSelection",
                    "Run Selection",
                    "Runs only the currently highlighted editor text in the shared terminal session.",
                    "Use it for quick experiments, one-off commands, or executing only part of a script.",
                    "Run Selection executes in the current session scope. The selected code can depend on variables, functions, modules, and location changes already present in that shared session.",
                    Section("Shortcut", false, "F8"),
                    Section("Workflow", true,
                        "Highlight the code you want to run.",
                        "Choose Run Selection.",
                        "Watch the console for output and errors.",
                        "Clear or reset the console if the shared state becomes confusing.",
                        "If nothing is selected, the command does not run and the status bar tells you to select script text first."),
                    related: new[] { "Command.RunScript", "Console.Area", "Editor.Surface", "Help.Troubleshooting" }),

                ["Command.Interrupt"] = Topic(
                    "Command.Interrupt",
                    "Interrupt",
                    "Stops the current command or script without throwing away the terminal session.",
                    "Use it when a run hangs, takes too long, or you started the wrong command.",
                    "Interrupt sends Ctrl+C into the live terminal session. Unlike Reset Console, it tries to preserve the session so you can keep using the same runtime, variables, and working directory afterward.",
                    Section("Best use", false,
                        "Long loops",
                        "Accidental full-script runs",
                        "Commands waiting for input or taking too long"),
                    related: new[] { "Console.Reset", "Command.RunScript" }),

                ["Command.ToggleBreakpoint"] = Topic(
                    "Command.ToggleBreakpoint",
                    "Toggle Breakpoint",
                    "Adds or removes a breakpoint on the current line.",
                    "Use it when you want the debugger to pause at a specific line.",
                    "Breakpoints matter to debugging, not to plain non-debug execution. The shell can also choose to start debugging from Run if enabled breakpoints are present.",
                    Section("Shortcuts and gestures", false,
                        "F9 toggles the breakpoint on the current line.",
                        "Click the editor gutter near a line number to toggle a breakpoint with the mouse."),
                    related: new[] { "Editor.Surface", "Debug.Breakpoints", "Command.StartDebug" }),

                ["Command.StartDebug"] = Topic(
                    "Command.StartDebug",
                    "Start Debug",
                    "Starts a debugging session for the active tab.",
                    "Use it when you want breakpoint pauses, stepping, variable inspection, and call-stack visibility.",
                    "If the current tab is unsaved or has unsaved changes, the shell may create a temporary script snapshot so the debugger has a real launch target.",
                    Section("Shortcut", false, "F5"),
                    Section("Typical flow", true,
                        "Set one or more breakpoints.",
                        "Start Debug.",
                        "When execution pauses, inspect Variables, Call Stack, and Breakpoints.",
                        "Use Continue or a step command to move forward.",
                        "Stop Debug when you are done."),
                    Section("Current-behavior note", false,
                        "Debugging follows the current app debug pipeline and PowerShell debug prompt handling. That means it is powerful, but still more implementation-sensitive than simple Run."),
                    related: new[] { "Command.ToggleBreakpoint", "Debug.Area", "Debug.Variables", "Debug.CallStack", "Debug.Breakpoints" }),

                ["Command.StepInto"] = Topic(
                    "Command.StepInto",
                    "Step Into",
                    "Moves to the next executable statement and enters called code when possible.",
                    "Use it when you want the most detailed debugging path.",
                    "Step commands only make sense while the debugger is currently paused.",
                    Section("Shortcut", false, "F11"),
                    related: new[] { "Command.StartDebug", "Command.StepOver", "Command.StepOut", "Command.ContinueDebug" }),

                ["Command.StepOver"] = Topic(
                    "Command.StepOver",
                    "Step Over",
                    "Moves to the next statement without stepping into every nested call.",
                    "Use it when you want progress through a script without entering each helper command in detail.",
                    "Step commands work only while paused in the debugger.",
                    Section("Shortcut", false, "F10"),
                    related: new[] { "Command.StepInto", "Command.StepOut", "Command.ContinueDebug" }),

                ["Command.StepOut"] = Topic(
                    "Command.StepOut",
                    "Step Out",
                    "Continues until the current scope returns to its caller.",
                    "Use it when you stepped in too far and want to back out to the calling level.",
                    "Step Out only works while paused inside a valid debug scope.",
                    Section("Shortcut", false, "Shift+F11"),
                    related: new[] { "Command.StepInto", "Command.StepOver", "Debug.CallStack" }),

                ["Command.ContinueDebug"] = Topic(
                    "Command.ContinueDebug",
                    "Continue",
                    "Resumes execution until the next breakpoint or the end of the session.",
                    "Use it after inspecting the current paused state.",
                    "Continue only applies while paused.",
                    Section("Shortcut", false, "F5 while already paused"),
                    related: new[] { "Command.StartDebug", "Command.StopDebug" }),

                ["Command.StopDebug"] = Topic(
                    "Command.StopDebug",
                    "Stop Debug",
                    "Ends the current debug session.",
                    "Use it when you no longer need the debugger attached.",
                    "Stopping debug is not the same as Interrupt for the shared console run path.",
                    Section("Shortcut", false, "Shift+F5"),
                    related: new[] { "Command.Interrupt", "Command.StartDebug" }),

                ["Help.Menu"] = Topic(
                    "Help.Menu",
                    "Help menu",
                    "The Help menu is the central place for overview help, focused help, and About information.",
                    "Use it when you want guidance without guessing where to click.",
                    "Context Help works best when the control you care about currently has focus.",
                    related: new[] { "Help.Context", "App.Overview", "Help.About" }),

                ["Help.Context"] = Topic(
                    "Help.Context",
                    "Context Help",
                    "Opens detailed help for the focused control or area.",
                    "Use it when quick hover help is not enough or you want to verify how the current control maps to the app's real behavior.",
                    "If a focused control does not have a matching help topic, the app now shows a visible fallback topic that includes the missing help key and logs the problem instead of failing silently.",
                    Section("Shortcut and editor behavior", false,
                        "F1 opens context help for most controls.",
                        "When the editor has focus, F1 first tries PowerShell quick info at the caret.",
                        "If the editor caret does not resolve to quick info, F1 falls back to the editor help topic instead of doing nothing."),
                    related: new[] { "App.Overview" }),

                ["Help.About"] = Topic(
                    "Help.About",
                    "About",
                    "The current About command writes a brief version and identity note into the shell status and output areas.",
                    "Use it when you want to confirm the running build without opening a separate About dialog.",
                    "There is no dedicated About window or in-app package/update screen today. Packaging details live in the repo's Windows packaging project, not in a runtime UI.",
                    related: new[] { "App.Overview", "Help.Packaging", "App.Settings" }),

                ["Explorer.Area"] = Topic(
                    "Explorer.Area",
                    "Explorer pane",
                    "The Explorer pane combines runtime selection, workspace browsing, and the open-tabs list.",
                    "Use it when you need navigation and environment control instead of just raw editing space.",
                    "You can hide the Explorer to make more room, but that does not unload the data it already holds.",
                    Section("What lives here", false,
                        "Runtime controls",
                        "Workspace summary and filter",
                        "Workspace tree",
                        "Open tabs list"),
                    related: new[] { "Runtime.Area", "Explorer.WorkspaceTree", "Explorer.WorkspaceFilter", "Explorer.OpenTabs" }),

                ["Runtime.Area"] = Topic(
                    "Runtime.Area",
                    "PowerShell Runtime section",
                    "This section shows the preferred runtime, the selected runtime, the executable path, and the discovered runtime list.",
                    "Use it when you want to confirm or change which PowerShell the editor, terminal, Run, and debugging features should use.",
                    "The app prefers the highest-priority PowerShell 7.x runtime it can validate. Windows PowerShell 5.1 is discovered as a secondary fallback for compatibility scenarios.",
                    Section("Recommended use", true,
                        "Refresh runtimes if the list looks stale.",
                        "Prefer the newest PowerShell 7.x entry for normal work.",
                        "Check the executable path if you are not sure which host you selected.",
                        "If no runtime is detected, Run, Reset Console, and command execution stay unavailable until discovery succeeds."),
                    Section("How discovery works today", false,
                        "The app probes known `Program Files\\PowerShell` locations first.",
                        "On Windows it also checks PowerShell registry entries, `PATH`, and `where.exe` results when needed.",
                        "Windows PowerShell system paths are added as fallback candidates.",
                        "Candidate metadata is validated before a runtime is accepted."),
                    related: new[] { "Runtime.Refresh", "Runtime.Path", "Runtime.List", "Editor.Metadata", "Help.Troubleshooting" }),

                ["Runtime.Refresh"] = Topic(
                    "Runtime.Refresh",
                    "Refresh runtimes",
                    "Rescans the machine for installed PowerShell runtimes.",
                    "Use it after installing or removing a PowerShell version, or if the runtime list looks wrong.",
                    "Refresh temporarily disables runtime-changing actions while discovery is in progress. It does not modify script tabs, console history, or file contents.",
                    related: new[] { "Runtime.Area", "Runtime.List", "Help.Troubleshooting" }),

                ["Runtime.Path"] = Topic(
                    "Runtime.Path",
                    "Selected runtime path",
                    "Shows the executable path of the currently selected runtime.",
                    "Use it when you want to verify exactly which pwsh or powershell executable the shell is targeting.",
                    "A readable display name can still be misleading if two runtimes have similar names, so the path is the real source of truth.",
                    related: new[] { "Runtime.Area", "Runtime.List" }),

                ["Runtime.List"] = Topic(
                    "Runtime.List",
                    "Detected runtimes list",
                    "Lists the PowerShell runtimes the app found on the machine.",
                    "Use it to choose the runtime you want the editor and live console to use next.",
                    "Changing the selection affects future console startup and editor metadata work for that runtime. If the current console is restarted, it comes back under the newly selected runtime.",
                    related: new[] { "Runtime.Area", "Runtime.Path", "Editor.Metadata" }),

                ["Explorer.WorkspaceSummary"] = Topic(
                    "Explorer.WorkspaceSummary",
                    "Workspace summary text",
                    "These summary lines show tab count, workspace counts, current workspace path, and the selected workspace item path.",
                    "Use them for quick orientation without drilling into the tree.",
                    "Counts and paths describe what the app currently knows about the loaded workspace view; they do not replace the actual tree."),

                ["Explorer.WorkspaceFilter"] = Topic(
                    "Explorer.WorkspaceFilter",
                    "Workspace Filter",
                    "Narrows the visible workspace view so you can find files and folders faster.",
                    "Use it when the workspace tree is large and you want a quicker view of matching items.",
                    "Filtering is based on the workspace data the app currently loaded. It is not a full-text code search engine.",
                    Section("Practical tip", false,
                        "Use short file or folder name fragments first.",
                        "Refresh the workspace if you changed files on disk and the tree needs a fresh view."),
                    related: new[] { "Explorer.WorkspaceTree", "Command.OpenFolder" }),

                ["Explorer.WorkspaceRefresh"] = Topic(
                    "Explorer.WorkspaceRefresh",
                    "Refresh Workspace",
                    "Reloads the current workspace view.",
                    "Use it when files changed on disk, the filter feels stale, or you want a fresh explorer view.",
                    "Refresh affects the workspace pane, not the live terminal state.",
                    related: new[] { "Explorer.WorkspaceTree", "Explorer.WorkspaceFilter" }),

                ["Explorer.ShowInExplorer"] = Topic(
                    "Explorer.ShowInExplorer",
                    "Show in Explorer",
                    "Opens the current workspace or selected file in Windows Explorer.",
                    "Use it when you want the Windows shell view for copying, moving, or inspecting files outside the app.",
                    "This opens Windows Explorer; it does not create a new tab in PS7 ScriptDesk.",
                    related: new[] { "Explorer.WorkspaceTree", "Command.OpenFolder" }),

                ["Explorer.WorkspaceTree"] = Topic(
                    "Explorer.WorkspaceTree",
                    "Workspace tree",
                    "The workspace tree shows folders and files from the loaded workspace.",
                    "Use it to browse, expand folders, and open files into tabs.",
                    "The tree is designed to load top-level items first and expand children on demand, so what you see grows as you open folders.",
                    Section("How to use it", true,
                        "Open a folder into the workspace.",
                        "Expand folders as needed.",
                        "Double-click a file to open it in a tab.",
                        "Use the filter box if the tree is large."),
                    related: new[] { "Explorer.WorkspaceFilter", "Command.OpenFile", "Explorer.ShowInExplorer" }),

                ["Explorer.OpenTabs"] = Topic(
                    "Explorer.OpenTabs",
                    "Open Tabs list",
                    "Shows all currently open document tabs in one list.",
                    "Use it when many tabs are open and the tab strip becomes crowded.",
                    "The list is a navigation aid. It does not replace the main editor tab strip.",
                    related: new[] { "Editor.TabStrip", "Editor.CloseTabButton" }),

                ["Editor.Area"] = Topic(
                    "Editor.Area",
                    "Editor area",
                    "This is the main code-writing surface, including the tab strip, document header, editor, diagnostics panel, and footer.",
                    "Use it for writing, reading, selecting, navigating, and breakpointing PowerShell scripts.",
                    "The editor is separate from the terminal. Editor IntelliSense and quick info stay in the editor and are deliberately suppressed when terminal input becomes active.",
                    Section("Key parts", false,
                        "Tab strip for multiple files",
                        "Document summary header",
                        "AvalonEdit code surface with line numbers",
                        "Diagnostics panel",
                        "Footer with caret, line, selection, breakpoint, and syntax summary text"),
                    related: new[] { "Editor.TabStrip", "Editor.Surface", "Editor.SyntaxPanel", "Editor.Footer", "Editor.Metadata", "Help.Troubleshooting" }),

                ["Editor.ActiveDocument"] = Topic(
                    "Editor.ActiveDocument",
                    "Active document summary",
                    "Shows which file or unsaved tab is active, where it lives, the current selection summary, and breakpoint summary.",
                    "Use it when you want quick context before running or debugging.",
                    "An unsaved tab can still run or debug, but debugging may use a temporary snapshot path under the hood.",
                    related: new[] { "Command.StartDebug", "Editor.TabStrip" }),

                ["Editor.TabStrip"] = Topic(
                    "Editor.TabStrip",
                    "Tab strip",
                    "Holds the open document tabs across the top of the editor.",
                    "Use it to switch between open files quickly.",
                    "A crowded tab strip is normal when many files are open; the Open Tabs list in the Explorer can help when the strip feels tight.",
                    related: new[] { "Explorer.OpenTabs", "Command.NewScript", "Editor.CloseTabButton", "Editor.PlusTab" }),

                ["Editor.CloseTabButton"] = Topic(
                    "Editor.CloseTabButton",
                    "Tab close button",
                    "Closes the tab next to the title in the tab strip.",
                    "Use it when you want to close one specific tab without changing menus.",
                    "Closing a tab affects only that tab. Save first if you care about the changes.",
                    related: new[] { "Command.CloseTab", "Editor.TabStrip" }),

                ["Editor.Surface"] = Topic(
                    "Editor.Surface",
                    "Editor surface, line numbers, and breakpoint gutter",
                    "This is where you type code, select text, use find/replace, inspect diagnostics, and set breakpoints.",
                    "Use it for everyday script editing and PowerShell authoring.",
                    "The left gutter includes line numbers and the breakpoint click zone. The editor supports syntax coloring, command quick info, metadata-backed completion, and drag-and-drop opening for supported files.",
                    Section("Helpful gestures", false,
                        "Click in the left gutter to toggle a breakpoint on that line.",
                        "Press F9 to toggle a breakpoint on the current line.",
                        "Press Ctrl+Space for IntelliSense suggestions when available.",
                        "Command and parameter completion come from cached and live PowerShell metadata. If metadata is still loading or failed, completion can be reduced until the background metadata work finishes.",
                        "Press F1 on a command or parameter for quick info at the caret, with fallback to editor help when no quick info is available.",
                        "Use Ctrl+mouse wheel to zoom the editor."),
                    Section("Context menu", false,
                        "Run Selection",
                        "Toggle Breakpoint",
                        "Find",
                        "Replace",
                        "What does this do?"),
                    related: new[] { "Command.RunSelection", "Command.ToggleBreakpoint", "Command.Find", "Command.Replace", "View.Zoom", "Editor.Metadata", "Editor.DragDrop" }),

                ["Editor.SyntaxPanel"] = Topic(
                    "Editor.SyntaxPanel",
                    "Diagnostics panel",
                    "Shows parser and authoring diagnostics for the current tab.",
                    "Use it when the editor highlights syntax or PowerShell-authoring issues such as parse errors, suspicious commands, or invalid parameters.",
                    "Diagnostics are advisory. Incomplete syntax such as `if (` will surface here, but the presence of diagnostics does not automatically disable Run or Run Selection.",
                    Section("How to read it", false,
                        "The summary line tells you whether diagnostics look okay or how many issues were found.",
                        "Each listed message gives line/column context when available.",
                        "Click a diagnostic row to navigate to that location in the active editor.",
                        "Fix the script and watch the panel update after the background parse completes."),
                    related: new[] { "Editor.Footer", "Editor.Surface", "Help.Troubleshooting" }),

                ["Editor.Footer"] = Topic(
                    "Editor.Footer",
                    "Editor footer and status strip",
                    "Shows caret position, line counts, selection size, breakpoint count, and syntax summary for the active tab.",
                    "Use it when you want quick document-state information without leaving the editor.",
                    "This footer describes the current tab only, not every open tab at once.",
                    related: new[] { "Status.Caret", "Status.Selection", "Status.Breakpoints" }),

                ["Editor.PlusTab"] = Topic(
                    "Editor.PlusTab",
                    "New tab plus button",
                    "Opens a new blank tab from the tab area.",
                    "Use it when you want a quick mouse-driven way to create a new script.",
                    "It creates a new unsaved tab, just like New Script.",
                    related: new[] { "Command.NewScript", "Editor.TabStrip" }),

                ["Editor.DragDrop"] = Topic(
                    "Editor.DragDrop",
                    "Drag and drop file open",
                    "You can drag one or more files from Windows Explorer and drop them onto the editor area to open them as tabs.",
                    "Use it when you already have File Explorer open and want a fast way to bring files into the app.",
                    "Only real file paths can be opened this way. Dropping folders does not turn them into a workspace by itself.",
                    related: new[] { "Command.OpenFile", "Command.OpenFolder" }),

                ["Console.Area"] = Topic(
                    "Console.Area",
                    "Console area",
                    "The console hosts a real interactive PowerShell terminal session through Windows ConPTY and xterm.js.",
                    "Use it when you want live command execution, script output, prompt-driven interaction, or the shared session used by Run and Run Selection.",
                    "This is not an editor-owned command box. You type directly into the embedded terminal, and the prompt is produced by PowerShell/PSReadLine. Editor IntelliSense does not attach to terminal typing.",
                    Section("Main parts", false,
                        "Embedded terminal surface for both output and direct typing",
                        "Session status text",
                        "Reset Console button",
                        "Shared session state used by Run, Run Selection, and manual commands"),
                    related: new[] { "Console.Output", "Console.Reset", "Command.RunScript", "Command.RunSelection", "Help.Troubleshooting" }),

                ["Console.Output"] = Topic(
                    "Console.Output",
                    "Interactive terminal surface",
                    "Shows script output, prompt text, manual commands, and debugger-related terminal output inside the embedded console.",
                    "Use it to read results and to type directly into the live PowerShell session.",
                    "The terminal surface is interactive. If the prompt or cursor looks confusing after a command, Interrupt or Reset Console can recover the session more reliably than editing the visible text.",
                    related: new[] { "Command.ClearOutput", "Console.Reset", "Help.Troubleshooting" }),

                ["Console.Reset"] = Topic(
                    "Console.Reset",
                    "Reset Console",
                    "Rebuilds the live terminal session.",
                    "Use it when the session state becomes confusing or you want a fresh PowerShell host.",
                    "Reset Console is stronger than Interrupt. It discards the old interactive session state and starts a new PowerShell process under the currently selected runtime.",
                    related: new[] { "Command.Interrupt", "Console.Area" }),

                ["Editor.Metadata"] = Topic(
                    "Editor.Metadata",
                    "PowerShell editor metadata and IntelliSense cache",
                    "PS7 ScriptDesk loads command, parameter, syntax, and help metadata in the background for the selected runtime and saves reusable caches for later launches.",
                    "Use this help when completion looks incomplete, the metadata badge is visible, or you are using the Run-menu metadata refresh and cache-delete commands.",
                    "Metadata problems reduce IntelliSense quality, but they do not stop the visible editor text from running. Missing or failed metadata states are logged so they can be diagnosed later.",
                    Section("Metadata states you can see", false,
                        "Loading or scheduled: the app is still preparing metadata in the background.",
                        "Ready: a healthy full cache is available for the current runtime.",
                        "Warning or failed refresh: the app kept using an older healthy cache while a rebuild failed.",
                        "Failed: no healthy cache could be prepared for the current runtime, so completions may be limited."),
                    Section("What the Run menu metadata commands do", false,
                        "Refresh PowerShell Editor Metadata starts a background rebuild for the selected runtime.",
                        "Delete Current Runtime Metadata Cache removes the saved cache for the selected runtime, then rebuilds it in the background.",
                        "Delete All PowerShell Metadata Caches removes every saved metadata cache so each runtime will rebuild when used again."),
                    Section("Troubleshooting hints", true,
                        "If completion is sparse right after switching runtimes, wait for the metadata badge to clear or finish rebuilding.",
                        "If metadata failed, use the cache refresh commands and check the app log for `EditorMetadata` and `EditorCompletion` entries.",
                        "If a runtime never reaches a healthy cache, verify that the selected PowerShell executable starts correctly outside the app."),
                    related: new[] { "Editor.Surface", "Runtime.Area", "Runtime.List", "Help.Troubleshooting" }),

                ["Debug.Area"] = Topic(
                    "Debug.Area",
                    "Debug panel area",
                    "The Debug panel holds Variables, Call Stack, and Breakpoints tabs.",
                    "Use it while debugging to inspect the current paused state.",
                    "These views are most meaningful when a debug session is active and paused.",
                    related: new[] { "Debug.Variables", "Debug.CallStack", "Debug.Breakpoints", "Command.StartDebug" }),

                ["Debug.Variables"] = Topic(
                    "Debug.Variables",
                    "Variables panel",
                    "Shows variable names, types, and values captured from the current debug pause.",
                    "Use it to inspect state without sprinkling Write-Host lines through the script.",
                    "Variable visibility depends on what the debugger can retrieve from the current paused scope.",
                    related: new[] { "Debug.CallStack", "Command.StartDebug" }),

                ["Debug.CallStack"] = Topic(
                    "Debug.CallStack",
                    "Call Stack panel",
                    "Shows the debug call stack when execution is paused.",
                    "Use it to see how execution reached the current point.",
                    "The call stack is only meaningful during a valid debug pause.",
                    related: new[] { "Debug.Variables", "Command.StepOut" }),

                ["Debug.Breakpoints"] = Topic(
                    "Debug.Breakpoints",
                    "Breakpoints panel",
                    "Lists known breakpoints and lets you enable, disable, or remove them.",
                    "Use it when you want a central list instead of manually inspecting each tab.",
                    "A disabled breakpoint stays listed but should not be handed to the debugger as an active stop point.",
                    related: new[] { "Command.ToggleBreakpoint", "Debug.RemoveBreakpoint" }),

                ["Debug.RemoveBreakpoint"] = Topic(
                    "Debug.RemoveBreakpoint",
                    "Remove Selected breakpoint",
                    "Removes the currently selected breakpoint row from the list.",
                    "Use it when you want to clean up old breakpoints quickly.",
                    "This removes the breakpoint from the tracked list; it does not save or reload files.",
                    related: new[] { "Debug.Breakpoints", "Command.ToggleBreakpoint" }),

                ["App.Settings"] = Topic(
                    "App.Settings",
                    "Persisted settings and layout state",
                    "PS7 ScriptDesk does not currently expose a dedicated Settings window. Instead, it persists core shell state automatically between sessions.",
                    "Use this topic when you want to know what the app remembers on exit and restores on the next launch.",
                    "Because there is no settings dialog today, changing these values happens through normal UI actions such as resizing panes, changing theme or zoom, choosing a runtime, and toggling context help.",
                    Section("What is persisted today", false,
                        "Window position, size, and maximized state",
                        "Explorer visibility and splitter sizes",
                        "Console and explorer section heights",
                        "Last workspace folder",
                        "Selected runtime path",
                        "Reopened saved-file tabs and selected tab",
                        "Recent file paths",
                        "Theme, editor zoom level, and context-help enabled state"),
                    related: new[] { "View.Menu", "Help.About", "App.Overview" }),

                ["Help.Troubleshooting"] = Topic(
                    "Help.Troubleshooting",
                    "Troubleshooting",
                    "Use these checks when help, completion, diagnostics, runtime discovery, file operations, or script execution do not behave as expected.",
                    "Open this topic when a feature seems idle, unavailable, or inconsistent with the current UI state.",
                    "PS7 ScriptDesk writes detailed diagnostics to the app log. Most help and metadata failures should remain visible and logged instead of failing silently.",
                    Section("Metadata failed or completion is weak", true,
                        "Watch the metadata status badge or Run-menu metadata commands for the current runtime state.",
                        "If completion is sparse right after startup or a runtime switch, wait for the background metadata build to finish.",
                        "If metadata failed, refresh or delete the cache and check the app log for `EditorMetadata` and `EditorCompletion` entries."),
                    Section("Script appears not to run or Run is disabled", true,
                        "Confirm a runtime is selected and runtime discovery is not still refreshing.",
                        "Run and Run Selection stay disabled while another execution is active, while runtime discovery is active, or while a debug session is active.",
                        "If nothing is selected, Run Selection does not dispatch and reports that status instead."),
                    Section("Console prompt or cursor looks wrong", true,
                        "Remember that the terminal is interactive and owns its own prompt.",
                        "Use Interrupt if PowerShell is waiting for more input.",
                        "Use Reset Console if the session state is confused or the prompt never recovers cleanly."),
                    Section("Help topic missing, diagnostics absent, runtime missing, or file open/save failed", true,
                        "A missing help topic now shows a visible fallback message with the missing key and logs the problem.",
                        "If diagnostics do not appear, verify that a PowerShell runtime is available for syntax checking.",
                        "If no runtime is found, refresh discovery and verify that `pwsh.exe` or Windows PowerShell exists on the machine.",
                        "If open/save fails, read the status/output message first, then check file permissions, controlled-folder access, and whether the target path still exists."),
                    Section("App log", false, AppLogger.CurrentLogPath),
                    related: new[] { "Editor.Metadata", "Console.Area", "Runtime.Area", "Command.RunScript", "Command.RunSelection", "Help.Context" }),

                ["Help.Packaging"] = Topic(
                    "Help.Packaging",
                    "Version and packaging",
                    "The running shell version comes from the app assembly information, while Windows packaging details live in the repo's packaging project and manifest.",
                    "Use this topic when you want to understand what the app exposes at runtime versus what only exists in the packaging/build configuration.",
                    "The app does not currently expose an in-app update screen, Store workflow, or AppInstaller UI. Do not assume package-update behavior unless you verify it in the packaging project.",
                    Section("Current runtime-facing behavior", false,
                        "The About command writes a brief version note to the shell status/output areas.",
                        "The status bar also shows the running version string.",
                        "Packaging and signing are build-time concerns, not a live in-app settings page."),
                    related: new[] { "Help.About", "Status.Version", "App.Overview" }),

                ["Status.Version"] = StatusTopic(
                    "Status.Version",
                    "Version status",
                    "Shows the current app version."),
                ["Status.General"] = StatusTopic(
                    "Status.General",
                    "General status text",
                    "Shows the latest high-level status message from the shell."),
                ["Status.Caret"] = StatusTopic(
                    "Status.Caret",
                    "Caret position",
                    "Shows the current line and column in the active editor tab."),
                ["Status.Lines"] = StatusTopic(
                    "Status.Lines",
                    "Editor metrics",
                    "Shows document metrics such as line counts for the active tab."),
                ["Status.Selection"] = StatusTopic(
                    "Status.Selection",
                    "Selection summary",
                    "Shows whether text is selected and how large the current selection is."),
                ["Status.Breakpoints"] = StatusTopic(
                    "Status.Breakpoints",
                    "Breakpoint summary",
                    "Shows how many breakpoints the active tab currently has."),
                ["Status.Runtime"] = StatusTopic(
                    "Status.Runtime",
                    "Runtime summary",
                    "Shows the runtime summary in the status bar."),
                ["Status.Workspace"] = StatusTopic(
                    "Status.Workspace",
                    "Workspace summary",
                    "Shows the current workspace summary in the status bar."),
                ["Status.Zoom"] = StatusTopic(
                    "Status.Zoom",
                    "Zoom summary",
                    "Shows the current editor zoom level."),
                ["Status.Execution"] = StatusTopic(
                    "Status.Execution",
                    "Execution progress",
                    "Shows execution-progress text while commands or scripts are running.")
            };

            return topics;
        }

        private static HelpTopic SimpleThemeTopic(string key, string title, string choiceName)
        {
            return Topic(
                key,
                title,
                $"Switches the shell to the {choiceName} theme.",
                "Use it when this theme gives you the readability or visual style you want.",
                "Changing theme affects the app appearance only.",
                Section("Expected result", false, $"The app updates to the {choiceName} theme immediately."),
                related: new[] { "View.ThemeMenu" });
        }

        private static HelpTopic StatusTopic(string key, string title, string summary)
        {
            return Topic(
                key,
                title,
                summary,
                "Use it when you want a compact status readout without opening another panel.",
                "Status-bar values are lightweight summaries, not full diagnostic reports.",
                related: new[] { "Editor.Footer", "Console.Area" });
        }

        private static void ValidateTopicRelationships()
        {
            foreach (var topic in Topics.Values)
            {
                foreach (var relatedKey in topic.RelatedTopicKeys)
                {
                    if (Topics.ContainsKey(relatedKey))
                    {
                        continue;
                    }

                    var relationshipKey = $"{topic.Key}->{relatedKey}";
                    if (LoggedBrokenRelationships.Add(relationshipKey))
                    {
                        AppLogger.Warning("Help", $"Help topic '{topic.Key}' references missing related topic '{relatedKey}'.");
                    }
                }
            }
        }

        private static void LogMissingKey(string? key, string? source)
        {
            var normalizedKey = string.IsNullOrWhiteSpace(key) ? "(null or empty key)" : key.Trim();
            if (LoggedMissingKeys.Add(normalizedKey))
            {
                AppLogger.Warning(
                    "Help",
                    $"Help topic key '{normalizedKey}' was requested but no topic exists. Source={source ?? "unknown"}. LogPath={AppLogger.CurrentLogPath}");
            }
        }

        private static HelpTopic CreateMissingTopic(string? key)
        {
            var normalizedKey = string.IsNullOrWhiteSpace(key) ? "(null or empty key)" : key.Trim();
            return Topic(
                "Help.MissingTopic",
                "Help Topic Not Found",
                $"{ApplicationBranding.PublicName} could not find help content for '{normalizedKey}'.",
                "Use this message to confirm that the help command is wired, but the requested topic key is missing, stale, or not yet cataloged.",
                "The app keeps running. The missing key was written to the app log so the help mapping can be corrected.",
                Section("Requested topic key", false, normalizedKey),
                Section("What to do next", true,
                    "Open the main overview topic if you still need general workflow guidance.",
                    "If this happens repeatedly for the same control, the help key or catalog entry needs to be fixed.",
                    "Check the app log for the recorded missing-key warning."),
                Section("App log", false, AppLogger.CurrentLogPath),
                related: new[] { OverviewKey, "Help.Troubleshooting" });
        }

        private static HelpTopic Topic(
            string key,
            string title,
            string quickSummary,
            string whenToUse,
            string limitationOrGotcha,
            HelpSection? section1 = null,
            HelpSection? section2 = null,
            HelpSection? section3 = null,
            HelpSection? section4 = null,
            HelpSection? section5 = null,
            HelpSection? section6 = null,
            HelpSection? section7 = null,
            HelpSection? section8 = null,
            IEnumerable<string>? related = null)
        {
            var sections = new[] { section1, section2, section3, section4, section5, section6, section7, section8 }
                .Where(static section => section is not null)
                .Cast<HelpSection>()
                .ToArray();

            return new HelpTopic(key, title, quickSummary, whenToUse, limitationOrGotcha, sections, related);
        }

        private static HelpSection Section(string heading, bool numbered, params string[] items)
        {
            return new HelpSection(heading, items, numbered);
        }
    }
}
