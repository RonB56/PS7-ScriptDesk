using System;
using System.Collections.Generic;
using System.Linq;

namespace PowerShellStudio.Shell.Help
{
    public static class HelpTopicCatalog
    {
        private static readonly IReadOnlyDictionary<string, HelpTopic> Topics = BuildTopics();

        public static HelpTopic Get(string? key)
        {
            if (!string.IsNullOrWhiteSpace(key) && Topics.TryGetValue(key, out var topic))
            {
                return topic;
            }

            return Topics["App.Overview"];
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

        private static IReadOnlyDictionary<string, HelpTopic> BuildTopics()
        {
            var topics = new Dictionary<string, HelpTopic>(StringComparer.OrdinalIgnoreCase)
            {
                ["App.Overview"] = Topic(
                    "App.Overview",
                    "PowerShellStudio help overview",
                    "This app is a Windows-only WPF PowerShell editor with a shared terminal, file tabs, workspace explorer, and debugger tools.",
                    "Use this overview when you want to understand how the main parts fit together before you start editing or debugging.",
                    "The app is already useful, but some advanced behaviors still depend on the current implementation of the PowerShell host and debugger rather than a fully mature commercial IDE engine.",
                    Section("Suggested first workflow", true,
                        "Pick or confirm the PowerShell 7 runtime in the Explorer pane.",
                        "Open a folder if you want a workspace tree, or open a single script file if you just want to edit one file.",
                        "Use the Editor to write or change a script.",
                        "Use Run to send the whole script into the shared terminal session, or Run Selection to send only highlighted text.",
                        "Use Interrupt to stop the current command while keeping the terminal alive.",
                        "Use Debug when you want breakpoint-based execution instead of a plain run."),
                    Section("Deep help discovery", false,
                        "Hover for quick help almost anywhere in the shell.",
                        "Press F1 while a control has focus to open detailed help for that control or area.",
                        "Right-click many controls and choose 'What does this do?' for context help.",
                        "Use Help > Context Help or the toolbar Help button if you prefer a visible command."),
                    Section("Main areas", false,
                        "Explorer: runtime picker, workspace filter, workspace tree, and the open-tabs list.",
                        "Editor: tab strip, code editor, syntax messages, and document footer.",
                        "Console: shared live terminal output plus a command entry box.",
                        "Debug: variables, call stack, and breakpoint management when the debug panel is shown."),
                    Section("Important behavior notes", false,
                        "Run and Run Selection use the same shared terminal session, so state can carry forward between commands.",
                        "Interrupt stops the current command; Reset Console creates a fresh terminal session.",
                        "Debug can use a temporary snapshot when the current tab has unsaved changes or no file path yet.",
                        "Syntax help reflects the current parser and editor pipeline, so it is meant to describe the app as it behaves today, not how a future version might behave."),
                    related: new[] { "Explorer.Area", "Editor.Area", "Console.Area", "Debug.Area" }),

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
                    "Opens an existing script or text file into a tab.",
                    "Use it when you want to work on a file from disk without opening an entire workspace folder.",
                    "The app opens the file in a tab; it does not automatically add sibling files unless you also open a workspace folder.",
                    Section("Shortcut", false, "Ctrl+O"),
                    Section("Related behavior", false,
                        "You can also drag supported files onto the editor to open them.",
                        "The workspace explorer can open files by double-clicking them in the tree."),
                    related: new[] { "Editor.Surface", "Explorer.WorkspaceTree", "Editor.DragDrop" }),

                ["Command.OpenFolder"] = Topic(
                    "Command.OpenFolder",
                    "Open Folder / Workspace",
                    "Loads a folder into the workspace explorer so you can browse files and folders inside the app.",
                    "Use it when you want project-style navigation instead of opening one file at a time.",
                    "The workspace tree is designed to load quickly at the top level and then expand deeper as you open folders.",
                    Section("Shortcut", false, "Ctrl+Shift+O"),
                    Section("What to expect", false,
                        "Top-level entries appear first.",
                        "Subfolders load as you expand them.",
                        "The workspace filter helps narrow what you are looking at."),
                    related: new[] { "Explorer.Area", "Explorer.WorkspaceTree", "Explorer.WorkspaceFilter" }),

                ["Command.Save"] = Topic(
                    "Command.Save",
                    "Save",
                    "Writes the active tab back to disk.",
                    "Use it after editing a file you want to keep.",
                    "If the tab has never been saved before, Save will behave like Save As because there is no existing file path yet.",
                    Section("Shortcut", false, "Ctrl+S"),
                    Section("Related topics", false,
                        "Use Save As when you want a new file name or location.",
                        "Debugging unsaved changes may use a temporary snapshot if you start debugging before saving."),
                    related: new[] { "Command.SaveAs", "Command.StartDebug" }),

                ["Command.SaveAs"] = Topic(
                    "Command.SaveAs",
                    "Save As",
                    "Saves the active document under a new path.",
                    "Use it for new tabs, copies, renamed files, or alternate versions.",
                    "This does not automatically save your old file under the new name too; the tab is moved to the chosen path.",
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
                    "Clears the visible console/output area.",
                    "Use it when the console becomes cluttered and you want a cleaner view before the next run.",
                    "Clearing output does not erase your script tabs or delete files. It only clears visible terminal/output text.",
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
                    "Runs the active document in the shared terminal session.",
                    "Use it when you want to send the whole script into the live PowerShell session.",
                    "Run uses the shared terminal. Variables, location changes, and imports can carry into later commands. If enabled breakpoints exist, the shell may start a debug session instead of a plain run.",
                    Section("Shortcut", false, "Ctrl+F5"),
                    Section("Expected result", false,
                        "Script output appears in the console/output pane.",
                        "The terminal session stays alive after the run unless the script itself exits it."),
                    related: new[] { "Command.RunSelection", "Console.Area", "Command.StartDebug" }),

                ["Command.RunSelection"] = Topic(
                    "Command.RunSelection",
                    "Run Selection",
                    "Runs only the currently highlighted editor text in the shared terminal session.",
                    "Use it for quick experiments, one-off commands, or executing only part of a script.",
                    "Because it uses the shared terminal, selected code can depend on variables or functions already defined earlier in the same session.",
                    Section("Shortcut", false, "F8"),
                    Section("Workflow", true,
                        "Highlight the code you want to run.",
                        "Choose Run Selection.",
                        "Watch the console for output and errors.",
                        "Clear or reset the console if the shared state becomes confusing."),
                    related: new[] { "Command.RunScript", "Console.Area", "Editor.Surface" }),

                ["Command.Interrupt"] = Topic(
                    "Command.Interrupt",
                    "Interrupt",
                    "Stops the current command or script without throwing away the terminal session.",
                    "Use it when a run hangs, takes too long, or you started the wrong command.",
                    "Interrupt is different from Reset Console. Interrupt tries to keep the current session alive so you can continue using it.",
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
                    "Opens deep help for the control or area that currently has focus.",
                    "Use it when quick hover help is not enough.",
                    "If the current focus does not map cleanly to a help topic, the app falls back to the overall help overview.",
                    Section("Shortcut", false, "F1"),
                    related: new[] { "App.Overview" }),

                ["Help.About"] = Topic(
                    "Help.About",
                    "About",
                    "The current About command writes a brief version/identity note into the shell status and output areas.",
                    "Use it when you want a lightweight confirmation of the running app version without opening full help.",
                    "About is intentionally brief today. Use the full help overview for actual workflow guidance.",
                    related: new[] { "App.Overview" }),

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
                    "Use it when you want to confirm or change which PowerShell the app uses.",
                    "The primary target is the most current installed PowerShell 7.x. Windows PowerShell 5.1 is secondary only.",
                    Section("Recommended use", true,
                        "Refresh runtimes if the list looks stale.",
                        "Prefer the newest PowerShell 7.x entry for normal work.",
                        "Check the executable path if you are not sure which host you selected."),
                    related: new[] { "Runtime.Refresh", "Runtime.Path", "Runtime.List" }),

                ["Runtime.Refresh"] = Topic(
                    "Runtime.Refresh",
                    "Refresh runtimes",
                    "Rescans the machine for installed PowerShell runtimes.",
                    "Use it after installing or removing a PowerShell version, or if the runtime list looks wrong.",
                    "Refreshing the list does not automatically rewrite your script tabs or their content.",
                    related: new[] { "Runtime.Area", "Runtime.List" }),

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
                    "Use it to choose the runtime you want the shell to prefer.",
                    "Primary recommendation: choose the newest PowerShell 7.x entry unless you specifically need a legacy compatibility test.",
                    related: new[] { "Runtime.Area", "Runtime.Path" }),

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
                    "This opens Windows Explorer; it does not create a new tab in PowerShellStudio.",
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
                    "This is the main code-writing surface, including the tab strip, document header, editor, syntax panel, and footer.",
                    "Use it for writing, reading, selecting, and breakpointing script code.",
                    "The editor works together with the shared terminal and debugger, so actions here can affect console state and debug behavior.",
                    Section("Key parts", false,
                        "Tab strip for multiple files",
                        "Document summary header",
                        "AvalonEdit code surface with line numbers",
                        "Syntax message panel",
                        "Footer with caret, line, selection, breakpoint, and syntax summary text"),
                    related: new[] { "Editor.TabStrip", "Editor.Surface", "Editor.SyntaxPanel", "Editor.Footer" }),

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
                    "This is where you type code, select text, use find/replace, and set breakpoints.",
                    "Use it for everyday script editing.",
                    "The left gutter includes line numbers and the breakpoint click zone. Running selected text uses the shared terminal session, not an isolated sandbox.",
                    Section("Helpful gestures", false,
                        "Click in the left gutter to toggle a breakpoint on that line.",
                        "Press F9 to toggle a breakpoint on the current line.",
                        "Press Ctrl+Space for IntelliSense suggestions when available.",
                        "Use Ctrl+mouse wheel to zoom the editor."),
                    Section("Context menu", false,
                        "Run Selection",
                        "Toggle Breakpoint",
                        "Find",
                        "Replace",
                        "What does this do?"),
                    related: new[] { "Command.RunSelection", "Command.ToggleBreakpoint", "Command.Find", "Command.Replace", "View.Zoom" }),

                ["Editor.SyntaxPanel"] = Topic(
                    "Editor.SyntaxPanel",
                    "Syntax message panel",
                    "Shows parser-reported syntax errors for the current tab.",
                    "Use it when the editor tells you something is structurally wrong with the script.",
                    "This panel reflects the app's current syntax-diagnostics pipeline. It is there to help you find real issues quickly, but it is not the same thing as executing the script.",
                    Section("How to read it", false,
                        "The summary line tells you whether syntax looks okay or how many errors were found.",
                        "Each listed message gives line/column context when available.",
                        "Fix the syntax in the editor and then watch the panel update."),
                    related: new[] { "Editor.Footer", "Editor.Surface" }),

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
                    "The console hosts a real PowerShell terminal session through Windows ConPTY.",
                    "Use it when you want live command execution, script output, or a persistent PowerShell session shared with Run and Run Selection.",
                    "This is a real session, so variables, location changes, modules, and profiles can affect later commands.",
                    Section("Main parts", false,
                        "Output display",
                        "Reset Console button",
                        "Prompt label",
                        "Command entry box",
                        "Execute button"),
                    related: new[] { "Console.Output", "Console.Input", "Console.Execute", "Console.Reset", "Command.RunScript" }),

                ["Console.Output"] = Topic(
                    "Console.Output",
                    "Console output display",
                    "Shows script output, terminal text, and debugger-related output routed into the console view.",
                    "Use it to read what your commands actually produced.",
                    "This is a read-only display area. Type commands in the command input box below instead.",
                    related: new[] { "Console.Input", "Command.ClearOutput" }),

                ["Console.Reset"] = Topic(
                    "Console.Reset",
                    "Reset Console",
                    "Rebuilds the live terminal session.",
                    "Use it when the session state becomes confusing or you want a fresh PowerShell host.",
                    "Reset Console is stronger than Interrupt. Reset throws away the old session state and starts again.",
                    related: new[] { "Command.Interrupt", "Console.Area" }),

                ["Console.Input"] = Topic(
                    "Console.Input",
                    "Console command box",
                    "This is where you type ad-hoc PowerShell commands for the shared terminal session.",
                    "Use it for quick one-line commands, testing, or follow-up commands after a script run.",
                    "Because the console is shared, the result can depend on what you ran earlier.",
                    Section("Tips", false,
                        "Press Enter to execute.",
                        "Use the Execute button if you prefer the mouse.",
                        "Use Reset Console if old session state becomes confusing."),
                    related: new[] { "Console.Execute", "Console.Area" }),

                ["Console.Execute"] = Topic(
                    "Console.Execute",
                    "Execute command",
                    "Sends the current console input to the live terminal session.",
                    "Use it instead of pressing Enter if you prefer a visible button.",
                    "It runs whatever is currently in the console command box, not the editor tab.",
                    related: new[] { "Console.Input", "Command.RunScript" }),

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
