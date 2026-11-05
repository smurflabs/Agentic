Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.RegularExpressions

Namespace Mcp

    ''' <summary>
    ''' One-line hints added to tool errors that models keep making (from the tool-call audit of 2026-11-02): what the error means
    ''' and the call that works, so the next try is right instead of a variation of the same mistake.
    ''' </summary>
    Public Module McpErrorHints

        Private ReadOnly Rules As (Tool As Regex, Errors As Regex, Hint As String)() = {
            (New Regex("^get_file_contents$", RegexOptions.IgnoreCase), New Regex("object does not exist|not found", RegexOptions.IgnoreCase),
             "That path is not a file on that branch (it may be a folder, or misspelled): list the folder with get_dir_contents (path ""."" is the repository's top)."),
            (New Regex("^get_dir_contents$", RegexOptions.IgnoreCase), New Regex("path is required", RegexOptions.IgnoreCase),
             "Give path: ""."" for the repository's top, or a folder such as ""src""."),
            (New Regex("^get_dir_contents$", RegexOptions.IgnoreCase), New Regex("object does not exist", RegexOptions.IgnoreCase),
             "No such folder on that branch: list its parent folder (path ""."" is the top) to see what exists."),
            (New Regex("^create_or_update_file", RegexOptions.IgnoreCase), New Regex("already exists", RegexOptions.IgnoreCase),
             "The file exists, so this is an update: get its sha with get_file_contents and send it with the file (""sha"") to replace it."),
            (New Regex("^(read_file|list_files|display_file)$", RegexOptions.IgnoreCase), New Regex("404|not found", RegexOptions.IgnoreCase),
             "No such file or folder there: find it with glob_search or list_files (the sandbox home is /home/user; relative paths start there)."),
            (New Regex("^run_command$", RegexOptions.IgnoreCase), New Regex("\[Errno 2\] No such file or directory", RegexOptions.IgnoreCase),
             "The working folder (cwd) does not exist: leave cwd out (commands start in /home/user) or create the folder first with mkdir -p."),
            (New Regex("^(kill_process|get_process_status|send_process_input)$", RegexOptions.IgnoreCase), New Regex("404|not found", RegexOptions.IgnoreCase),
             "No process with that id is running any more (it finished, or the id is wrong): list_processes shows the ids. Nothing to stop."),
            (New Regex("^run_command$", RegexOptions.IgnoreCase), New Regex("Errno 24|Too many open files", RegexOptions.IgnoreCase),
             "The sandbox has run out of open files (too many stuck commands): it needs a restart by an administrator. Do not retry; tell the user.")
        }

        ''' <summary>The hint for this tool's error text, or Nothing.</summary>
        Public Function HintFor(toolName As String, errorText As String) As String
            If String.IsNullOrEmpty(toolName) OrElse String.IsNullOrEmpty(errorText) Then Return Nothing
            For Each r In Rules
                If r.Tool.IsMatch(toolName) AndAlso r.Errors.IsMatch(errorText) Then Return r.Hint
            Next
            Return Nothing
        End Function

    End Module

End Namespace
