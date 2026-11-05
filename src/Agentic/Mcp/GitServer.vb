Option Strict On
Option Explicit On
Option Infer On

Imports System.ComponentModel
Imports System.IO
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading
Imports ModelContextProtocol
Imports ModelContextProtocol.Server

Namespace Mcp

    ''' <summary>
    ''' Built-in "git" MCP server ("Command": "{self}", "Arguments": ["--mcp-git", "--roots", "{data}\workspace;{data}\shared"]):
    ''' structured git for local repositories under the allowed folders - status, diff, log, show, blame, grep, branches, commits,
    ''' clone, pull and push - without a shell. Runs git.exe directly with prompts off, so a command never waits for input.
    ''' Pushing to the Gitea host uses GITEA_HOST + GITEA_ACCESS_TOKEN when they are set (the token never appears in output).
    ''' </summary>
    Public NotInheritable Class GitServer
        Private ReadOnly _roots As PathRoots
        Private ReadOnly _git As String
        Private ReadOnly _maxChars As Integer
        Private ReadOnly _tokenHost As String
        Private ReadOnly _token As String
        Private ReadOnly _userName As String
        Private ReadOnly _userEmail As String
        Private Shared ReadOnly Timeout As TimeSpan = TimeSpan.FromSeconds(120)
        Private Shared ReadOnly LongTimeout As TimeSpan = TimeSpan.FromMinutes(10)

        Friend Sub New(roots As PathRoots, Optional git As String = Nothing, Optional maxOutputKb As Integer = 64,
                       Optional giteaHost As String = Nothing, Optional giteaToken As String = Nothing,
                       Optional userName As String = Nothing, Optional userEmail As String = Nothing)
            _roots = roots
            _git = If(git, FindGit())
            _maxChars = Math.Max(4, maxOutputKb) * 1024
            If Not String.IsNullOrWhiteSpace(giteaHost) AndAlso Not String.IsNullOrWhiteSpace(giteaToken) Then
                Dim u As Uri = Nothing
                If Uri.TryCreate(If(giteaHost.Contains("://"), giteaHost, "https://" & giteaHost), UriKind.Absolute, u) Then _tokenHost = u.Host
                _token = giteaToken.Trim()
            End If
            _userName = If(String.IsNullOrWhiteSpace(userName), "Agentic", userName.Trim())
            _userEmail = If(String.IsNullOrWhiteSpace(userEmail), "agent@agentic.local", userEmail.Trim())
        End Sub

        Public Shared Function RunStdio(args As String()) As Integer
            Dim roots = ToolKit.SplitList(If(ToolKit.Arg(args, "--roots"), Path.Combine(AppContext.BaseDirectory, "data", "workspace")))
            Dim server As New GitServer(New PathRoots(roots), ToolKit.Env("GIT_PATH"), ToolKit.IntArg(args, "--max-output-kb", 64),
                                        ToolKit.Env("GITEA_HOST"), ToolKit.Env("GITEA_ACCESS_TOKEN"), ToolKit.Env("GIT_USER_NAME"), ToolKit.Env("GIT_USER_EMAIL"))
            Return BuiltInServer.Run("agentic-git", server.CreateTools())
        End Function

        ''' <summary>git.exe on PATH (not a .bat/.cmd wrapper), else the usual install folders.</summary>
        Friend Shared Function FindGit() As String
            For Each folder As String In If(Environment.GetEnvironmentVariable("PATH"), "").Split(Path.PathSeparator)
                Try
                    Dim exe = Path.Combine(folder.Trim().Trim(""""c), If(OperatingSystem.IsWindows(), "git.exe", "git"))
                    If File.Exists(exe) Then Return exe
                Catch ex As ArgumentException
                End Try
            Next
            For Each c In {"C:\Program Files\Git\cmd\git.exe", "C:\Program Files\Git\bin\git.exe", "/usr/bin/git", "/usr/local/bin/git"}
                If File.Exists(c) Then Return c
            Next
            Return "git"
        End Function

        Public Function CreateTools() As McpServerPrimitiveCollection(Of McpServerTool)
            Dim tools As New McpServerPrimitiveCollection(Of McpServerTool)()
            Dim where = $" repo: the repository folder (relative to {_roots.Roots(0)}, or an absolute path inside {_roots.Describe()})."
            ToolKit.Add(tools, New Func(Of String, CancellationToken, Task(Of String))(AddressOf StatusAsync), "git_status",
                "Branch, upstream (ahead/behind) and changed files of a repository." & where)
            ToolKit.Add(tools, New Func(Of String, String, Boolean, String, CancellationToken, Task(Of String))(AddressOf DiffAsync), "git_diff",
                "Changes as a unified diff with a summary: working tree (default), staged, or against a commit/branch (ref; 'main...HEAD' for a branch's changes)." & where)
            ToolKit.Add(tools, New Func(Of String, Integer, String, String, String, String, CancellationToken, Task(Of String))(AddressOf LogAsync), "git_log",
                "Commit history: hash, date, author, subject; filter by file, author, text in the message or date." & where)
            ToolKit.Add(tools, New Func(Of String, String, String, CancellationToken, Task(Of String))(AddressOf ShowAsync), "git_show",
                "A commit's message and changes; with path, that file as it was in the commit (ref:path)." & where)
            ToolKit.Add(tools, New Func(Of String, String, Integer, Integer, CancellationToken, Task(Of String))(AddressOf BlameAsync), "git_blame",
                "Who last changed each line of a file (and in which commit), optionally only lines start_line..end_line." & where)
            ToolKit.Add(tools, New Func(Of String, String, String, Boolean, CancellationToken, Task(Of String))(AddressOf GrepAsync), "git_grep",
                "Search the tracked files for a regular expression (file:line: text)." & where)
            ToolKit.Add(tools, New Func(Of String, CancellationToken, Task(Of String))(AddressOf BranchesAsync), "git_branches",
                "Local and remote branches with their last commit and upstream; and the remotes (credentials hidden)." & where)
            ToolKit.Add(tools, New Func(Of String, String, Boolean, String, CancellationToken, Task(Of String))(AddressOf SwitchAsync), "git_switch",
                "Switch to a branch, or create it (create: true, from start_point or the current commit)." & where, False)
            ToolKit.Add(tools, New Func(Of String, String, CancellationToken, Task(Of String))(AddressOf AddAsync), "git_add",
                "Stage files for the next commit (paths separated by commas; '.' for everything)." & where, False)
            ToolKit.Add(tools, New Func(Of String, String, Boolean, CancellationToken, Task(Of String))(AddressOf CommitAsync), "git_commit",
                "Commit the staged changes (all: also stage every change to tracked files first). Returns the new commit." & where, False)
            ToolKit.Add(tools, New Func(Of String, String, Boolean, CancellationToken, Task(Of String))(AddressOf RestoreAsync), "git_restore",
                "Discard uncommitted changes to files (paths separated by commas), or only unstage them (staged_only: true)." & where, False, True)
            ToolKit.Add(tools, New Func(Of String, String, String, CancellationToken, Task(Of String))(AddressOf StashAsync), "git_stash",
                "Stash work in progress: action push (default, with a message), pop, list or drop." & where, False)
            ToolKit.Add(tools, New Func(Of String, String, Boolean, Boolean, CancellationToken, Task(Of String))(AddressOf MergeAsync), "git_merge",
                "Merge a branch into the current one (no_ff: always make a merge commit). abort: true stops a merge with conflicts. Conflicted files are listed." & where, False)
            ToolKit.Add(tools, New Func(Of String, String, String, String, CancellationToken, Task(Of String))(AddressOf TagAsync), "git_tag",
                "List tags, or create one (name, with a message for an annotated tag, at ref or the current commit)." & where, False)
            ToolKit.Add(tools, New Func(Of String, String, String, Integer, CancellationToken, Task(Of String))(AddressOf CloneAsync), "git_clone",
                $"Clone a repository (https or ssh URL) into a new folder under {_roots.Roots(0)} (folder: its name; default from the URL). depth: shallow clone.", False)
            ToolKit.Add(tools, New Func(Of String, Boolean, CancellationToken, Task(Of String))(AddressOf PullAsync), "git_pull",
                "Fetch and integrate the upstream branch (rebase: true to rebase instead of merge)." & where, False)
            ToolKit.Add(tools, New Func(Of String, CancellationToken, Task(Of String))(AddressOf FetchAsync), "git_fetch",
                "Fetch all remotes (prunes deleted branches) without changing the working tree." & where)
            ToolKit.Add(tools, New Func(Of String, String, String, Boolean, CancellationToken, Task(Of String))(AddressOf PushAsync), "git_push",
                "Push a branch (default: the current one) to a remote (default origin); set_upstream for a new branch. Never forces." & where, False)
            ToolKit.Add(tools, New Func(Of String, CancellationToken, Task(Of String))(AddressOf InitAsync), "git_init",
                $"Create a new repository in a folder (relative to {_roots.Roots(0)}).", False)
            Return tools
        End Function

        ' ------------------------------------------------------------------------------------------ running git

        Private Function RepoDir(p As String) As String
            Dim full = _roots.Resolve(p)
            If Not Directory.Exists(full) Then Throw New McpException($"Folder '{p}' does not exist.")
            Return full
        End Function

        ''' <summary>A value that must not be read as an option (a branch called "--upload-pack=..." would be one).</summary>
        Private Shared Function Plain(value As String, what As String) As String
            Dim v = If(value, "").Trim()
            If v.StartsWith("-", StringComparison.Ordinal) Then Throw New McpException($"{what} must not start with '-'.")
            If v.Contains(ControlChars.NullChar) OrElse v.Contains(ControlChars.Lf) Then Throw New McpException($"{what} contains invalid characters.")
            Return v
        End Function

        Private Shared Function PathList(paths As String) As List(Of String)
            Return ToolKit.SplitList(paths).Select(Function(p) Plain(p, "A path")).ToList()
        End Function

        Private Function Redact(s As String) As String
            If String.IsNullOrEmpty(s) Then Return ""
            If _token IsNot Nothing Then s = s.Replace(_token, "***")
            Return Regex.Replace(s, "(https?://)[^/@\s:]+(:[^/@\s]*)?@", "$1***@")
        End Function

        ''' <summary>Runs git in a folder; throws an McpException with git's own message when it fails (unless allowFailure).</summary>
        Friend Async Function GitAsync(dir As String, args As IEnumerable(Of String), ct As CancellationToken, Optional allowFailure As Boolean = False,
                                       Optional timeout As TimeSpan? = Nothing, Optional remoteUrl As String = Nothing) As Task(Of (Code As Integer, Out As String))
            Dim all As New List(Of String) From {"-c", "core.quotepath=false", "-c", "color.ui=false", "-c", "protocol.ext.allow=never", "-c", "core.pager=cat"}
            If _token IsNot Nothing AndAlso NeedsToken(dir, remoteUrl) Then all.AddRange({"-c", $"http.https://{_tokenHost}/.extraheader=Authorization: token {_token}"})
            all.AddRange(args)
            Dim env As New Dictionary(Of String, String) From {{"GIT_TERMINAL_PROMPT", "0"}, {"GCM_INTERACTIVE", "never"}, {"GIT_ASKPASS", ""}, {"SSH_ASKPASS", ""},
                {"GIT_SSH_COMMAND", "ssh -o BatchMode=yes -o StrictHostKeyChecking=accept-new"}, {"LC_ALL", "C"}}
            Dim r = Await ToolKit.RunProcessAsync(_git, all, dir, If(timeout, GitServer.Timeout), ct, env).ConfigureAwait(False)
            Dim text = Redact((r.Output & If(String.IsNullOrWhiteSpace(r.Error), "", If(r.Output.Length > 0, vbLf, "") & r.Error)).TrimEnd())
            If r.TimedOut Then Throw New McpException($"git {String.Join(" ", args.Take(2))} did not finish within {If(timeout, GitServer.Timeout).TotalSeconds:0} s and was stopped.")
            If r.ExitCode <> 0 AndAlso Not allowFailure Then Throw New McpException($"git {args.First()} failed (exit {r.ExitCode}): {ToolKit.Clip(text, 4000)}")
            Return (r.ExitCode, ToolKit.Clip(text, _maxChars))
        End Function

        Private Function NeedsToken(dir As String, remoteUrl As String) As Boolean
            If _tokenHost Is Nothing Then Return False
            If remoteUrl IsNot Nothing Then Return remoteUrl.Contains("://" & _tokenHost, StringComparison.OrdinalIgnoreCase)
            Try
                Dim cfg = Path.Combine(dir, ".git", "config")
                Return File.Exists(cfg) AndAlso File.ReadAllText(cfg).Contains(_tokenHost, StringComparison.OrdinalIgnoreCase)
            Catch ex As IOException
                Return False
            End Try
        End Function

        ''' <summary>Commits need a name and email: the repository's own when set, otherwise Agentic's.</summary>
        Private Async Function IdentityAsync(dir As String, ct As CancellationToken) As Task(Of List(Of String))
            Dim name = Await GitAsync(dir, {"config", "user.name"}, ct, allowFailure:=True).ConfigureAwait(False)
            Dim email = Await GitAsync(dir, {"config", "user.email"}, ct, allowFailure:=True).ConfigureAwait(False)
            Dim extra As New List(Of String)
            If name.Code <> 0 OrElse String.IsNullOrWhiteSpace(name.Out) Then extra.AddRange({"-c", "user.name=" & _userName})
            If email.Code <> 0 OrElse String.IsNullOrWhiteSpace(email.Out) Then extra.AddRange({"-c", "user.email=" & _userEmail})
            Return extra
        End Function

        ' ------------------------------------------------------------------------------------------ tools

        Friend Async Function StatusAsync(<Description("Repository folder")> repo As String, ct As CancellationToken) As Task(Of String)
            Dim dir = RepoDir(repo)
            Dim r = Await GitAsync(dir, {"status", "--short", "--branch", "--untracked-files=all"}, ct).ConfigureAwait(False)
            Dim lines = r.Out.Split(ControlChars.Lf)
            If lines.Length <= 1 Then Return r.Out & vbLf & "Working tree clean."
            Return r.Out & vbLf & "(XY codes: M modified, A added, D deleted, R renamed, ?? untracked; X = staged, Y = not staged)"
        End Function

        Friend Async Function DiffAsync(<Description("Repository folder")> repo As String,
                                        <Description("Only this file or folder (optional)")> Optional path As String = "",
                                        <Description("Staged changes instead of the working tree")> Optional staged As Boolean = False,
                                        <Description("Compare with this commit/branch, or a range like 'main...HEAD' (optional)")> Optional ref As String = "",
                                        Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim dir = RepoDir(repo)
            Dim baseArgs As New List(Of String) From {"diff"}
            If staged Then baseArgs.Add("--staged")
            If Not String.IsNullOrWhiteSpace(ref) Then baseArgs.Add(Plain(ref, "ref"))
            Dim tail As New List(Of String)
            If Not String.IsNullOrWhiteSpace(path) Then tail.AddRange({"--", Plain(path, "path")})
            Dim stat = Await GitAsync(dir, baseArgs.Concat({"--stat=120"}).Concat(tail), ct).ConfigureAwait(False)
            If String.IsNullOrWhiteSpace(stat.Out) Then Return "No changes."
            Dim patch = Await GitAsync(dir, baseArgs.Concat({"--patch", "--no-ext-diff"}).Concat(tail), ct).ConfigureAwait(False)
            Return stat.Out & vbLf & vbLf & patch.Out
        End Function

        Friend Async Function LogAsync(<Description("Repository folder")> repo As String,
                                       <Description("How many commits (default 20, at most 200)")> Optional max_count As Integer = 20,
                                       <Description("Only commits touching this file or folder")> Optional path As String = "",
                                       <Description("Only commits by this author (name or email)")> Optional author As String = "",
                                       <Description("Only commits whose message contains this")> Optional grep As String = "",
                                       <Description("Only commits since, e.g. '2026-10-01' or '2 weeks ago'")> Optional since As String = "",
                                       Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim dir = RepoDir(repo)
            Dim a As New List(Of String) From {"log", "-n", Math.Clamp(If(max_count <= 0, 20, max_count), 1, 200).ToString(Globalization.CultureInfo.InvariantCulture),
                                               "--date=iso-strict", "--pretty=format:%h  %ad  %an  %s%d"}
            If Not String.IsNullOrWhiteSpace(author) Then a.Add("--author=" & author.Trim())
            If Not String.IsNullOrWhiteSpace(grep) Then a.AddRange({"-i", "--grep=" & grep.Trim()})
            If Not String.IsNullOrWhiteSpace(since) Then a.Add("--since=" & since.Trim())
            If Not String.IsNullOrWhiteSpace(path) Then a.AddRange({"--", Plain(path, "path")})
            Dim r = Await GitAsync(dir, a, ct, allowFailure:=True).ConfigureAwait(False)
            If r.Code <> 0 AndAlso r.Out.Contains("does not have any commits", StringComparison.OrdinalIgnoreCase) Then Return "No commits yet."
            If r.Code <> 0 Then Throw New McpException("git log failed: " & r.Out)
            Return If(String.IsNullOrWhiteSpace(r.Out), "No matching commits.", r.Out)
        End Function

        Friend Async Function ShowAsync(<Description("Repository folder")> repo As String,
                                        <Description("Commit, branch or tag (default HEAD)")> Optional ref As String = "HEAD",
                                        <Description("A file: show its content in that commit instead")> Optional path As String = "",
                                        Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim dir = RepoDir(repo)
            Dim r = If(String.IsNullOrWhiteSpace(ref), "HEAD", Plain(ref, "ref"))
            If Not String.IsNullOrWhiteSpace(path) Then
                Return (Await GitAsync(dir, {"show", r & ":" & Plain(path, "path").Replace("\"c, "/"c)}, ct).ConfigureAwait(False)).Out
            End If
            Return (Await GitAsync(dir, {"show", "--stat=120", "--patch", "--no-ext-diff", "--date=iso-strict", r}, ct).ConfigureAwait(False)).Out
        End Function

        Friend Async Function BlameAsync(<Description("Repository folder")> repo As String,
                                         <Description("File to blame")> path As String,
                                         <Description("First line (optional)")> Optional start_line As Integer = 0,
                                         <Description("Last line (optional)")> Optional end_line As Integer = 0,
                                         Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim dir = RepoDir(repo)
            Dim a As New List(Of String) From {"blame", "--date=short", "-w"}
            If start_line > 0 Then a.AddRange({"-L", $"{start_line},{If(end_line >= start_line, end_line.ToString(Globalization.CultureInfo.InvariantCulture), "")}"})
            a.AddRange({"--", Plain(path, "path")})
            Return (Await GitAsync(dir, a, ct).ConfigureAwait(False)).Out
        End Function

        Friend Async Function GrepAsync(<Description("Repository folder")> repo As String,
                                        <Description("Regular expression to look for")> pattern As String,
                                        <Description("Only below this folder or these files (optional)")> Optional path As String = "",
                                        <Description("Ignore case")> Optional ignore_case As Boolean = False,
                                        Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim dir = RepoDir(repo)
            If String.IsNullOrEmpty(pattern) Then Throw New McpException("pattern is required.")
            Dim a As New List(Of String) From {"grep", "-n", "-I", "-E", "--max-count=50"}
            If ignore_case Then a.Add("-i")
            a.AddRange({"-e", pattern})
            If Not String.IsNullOrWhiteSpace(path) Then a.AddRange({"--", Plain(path, "path")})
            Dim r = Await GitAsync(dir, a, ct, allowFailure:=True).ConfigureAwait(False)
            If r.Code = 1 Then Return "No matches."
            If r.Code <> 0 Then Throw New McpException("git grep failed: " & r.Out)
            Return r.Out
        End Function

        Friend Async Function BranchesAsync(<Description("Repository folder")> repo As String, ct As CancellationToken) As Task(Of String)
            Dim dir = RepoDir(repo)
            Dim b = Await GitAsync(dir, {"branch", "--all", "-vv", "--no-abbrev"}, ct).ConfigureAwait(False)
            Dim remotes = Await GitAsync(dir, {"remote", "-v"}, ct).ConfigureAwait(False)
            Return If(String.IsNullOrWhiteSpace(b.Out), "No branches yet (no commits).", b.Out) & vbLf & vbLf & "Remotes:" & vbLf & If(String.IsNullOrWhiteSpace(remotes.Out), "(none)", remotes.Out)
        End Function

        Friend Async Function SwitchAsync(<Description("Repository folder")> repo As String,
                                          <Description("Branch name")> branch As String,
                                          <Description("Create the branch")> Optional create As Boolean = False,
                                          <Description("Where a new branch starts (optional)")> Optional start_point As String = "",
                                          Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim dir = RepoDir(repo)
            Dim a As New List(Of String) From {"switch"}
            If create Then a.Add("-c")
            a.Add(Plain(branch, "branch"))
            If create AndAlso Not String.IsNullOrWhiteSpace(start_point) Then a.Add(Plain(start_point, "start_point"))
            Dim r = Await GitAsync(dir, a, ct).ConfigureAwait(False)
            Return If(String.IsNullOrWhiteSpace(r.Out), $"On branch {branch.Trim()}.", r.Out)
        End Function

        Friend Async Function AddAsync(<Description("Repository folder")> repo As String,
                                       <Description("Files or folders to stage, separated by commas ('.' = everything)")> paths As String,
                                       ct As CancellationToken) As Task(Of String)
            Dim dir = RepoDir(repo)
            Dim list = PathList(paths)
            If list.Count = 0 Then Throw New McpException("paths is required ('.' for everything).")
            Await GitAsync(dir, New List(Of String) From {"add", "--"}.Concat(list), ct).ConfigureAwait(False)
            Return Await StatusAsync(repo, ct).ConfigureAwait(False)
        End Function

        Friend Async Function CommitAsync(<Description("Repository folder")> repo As String,
                                          <Description("Commit message (first line: a short summary)")> message As String,
                                          <Description("Stage all changes to tracked files first")> Optional all As Boolean = False,
                                          Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim dir = RepoDir(repo)
            If String.IsNullOrWhiteSpace(message) Then Throw New McpException("message is required.")
            Dim a = Await IdentityAsync(dir, ct).ConfigureAwait(False)
            a.AddRange({"commit", "-F", "-"})
            If all Then a.Add("-a")
            Dim r = Await CommitWithMessageAsync(dir, a, message, ct).ConfigureAwait(False)
            Dim head = Await GitAsync(dir, {"log", "-1", "--stat=120", "--pretty=format:%H%n%an <%ae>  %ad%n%n%s"}, ct).ConfigureAwait(False)
            Return r & vbLf & vbLf & head.Out
        End Function

        Private Async Function CommitWithMessageAsync(dir As String, args As List(Of String), message As String, ct As CancellationToken) As Task(Of String)
            Dim all As New List(Of String) From {"-c", "core.quotepath=false", "-c", "color.ui=false"}
            all.AddRange(args)
            Dim env As New Dictionary(Of String, String) From {{"GIT_TERMINAL_PROMPT", "0"}, {"LC_ALL", "C"}}
            Dim r = Await ToolKit.RunProcessAsync(_git, all, dir, GitServer.Timeout, ct, env, message).ConfigureAwait(False)
            Dim text = Redact((r.Output & vbLf & r.Error).Trim())
            If r.ExitCode <> 0 Then
                If text.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase) OrElse text.Contains("no changes added", StringComparison.OrdinalIgnoreCase) Then
                    Throw New McpException("Nothing to commit: stage files with git_add first (or use all: true for tracked files).")
                End If
                Throw New McpException($"git commit failed (exit {r.ExitCode}): {ToolKit.Clip(text, 4000)}")
            End If
            Return ToolKit.Clip(text, 4000)
        End Function

        Friend Async Function RestoreAsync(<Description("Repository folder")> repo As String,
                                           <Description("Files to restore, separated by commas ('.' = everything)")> paths As String,
                                           <Description("Only unstage (keep the changes in the working tree)")> Optional staged_only As Boolean = False,
                                           Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim dir = RepoDir(repo)
            Dim list = PathList(paths)
            If list.Count = 0 Then Throw New McpException("paths is required.")
            Dim a As New List(Of String) From {"restore"}
            a.AddRange(If(staged_only, {"--staged"}, {"--staged", "--worktree"}))
            a.Add("--")
            a.AddRange(list)
            Await GitAsync(dir, a, ct).ConfigureAwait(False)
            Return Await StatusAsync(repo, ct).ConfigureAwait(False)
        End Function

        Friend Async Function StashAsync(<Description("Repository folder")> repo As String,
                                         <Description("push (default), pop, list or drop")> Optional action As String = "push",
                                         <Description("Message for push")> Optional message As String = "",
                                         Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim dir = RepoDir(repo)
            Dim a As New List(Of String)
            Select Case If(action, "push").Trim().ToLowerInvariant()
                Case "", "push", "save"
                    a.AddRange(Await IdentityAsync(dir, ct).ConfigureAwait(False))
                    a.AddRange({"stash", "push", "--include-untracked"})
                    If Not String.IsNullOrWhiteSpace(message) Then a.AddRange({"-m", message.Trim()})
                Case "pop" : a.AddRange({"stash", "pop"})
                Case "list" : a.AddRange({"stash", "list"})
                Case "drop" : a.AddRange({"stash", "drop"})
                Case Else : Throw New McpException("action must be push, pop, list or drop.")
            End Select
            Dim r = Await GitAsync(dir, a, ct).ConfigureAwait(False)
            Return If(String.IsNullOrWhiteSpace(r.Out), "Done (no stashes).", r.Out)
        End Function

        Friend Async Function MergeAsync(<Description("Repository folder")> repo As String,
                                         <Description("Branch to merge into the current branch")> Optional branch As String = "",
                                         <Description("Always create a merge commit")> Optional no_ff As Boolean = False,
                                         <Description("Abort a merge that stopped with conflicts")> Optional abort As Boolean = False,
                                         Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim dir = RepoDir(repo)
            If abort Then
                Await GitAsync(dir, {"merge", "--abort"}, ct).ConfigureAwait(False)
                Return "Merge aborted." & vbLf & Await StatusAsync(repo, ct).ConfigureAwait(False)
            End If
            If String.IsNullOrWhiteSpace(branch) Then Throw New McpException("branch is required.")
            Dim a = Await IdentityAsync(dir, ct).ConfigureAwait(False)
            a.AddRange({"merge", "--no-edit"})
            If no_ff Then a.Add("--no-ff")
            a.Add(Plain(branch, "branch"))
            Dim r = Await GitAsync(dir, a, ct, allowFailure:=True).ConfigureAwait(False)
            If r.Code = 0 Then Return r.Out
            Dim conflicts = Await GitAsync(dir, {"diff", "--name-only", "--diff-filter=U"}, ct, allowFailure:=True).ConfigureAwait(False)
            If Not String.IsNullOrWhiteSpace(conflicts.Out) Then
                Return $"Merge stopped with conflicts in:{vbLf}{conflicts.Out}{vbLf}Fix the files (remove the <<<<<<< ======= >>>>>>> markers), git_add them and git_commit; or git_merge with abort: true."
            End If
            Throw New McpException("git merge failed: " & r.Out)
        End Function

        Friend Async Function TagAsync(<Description("Repository folder")> repo As String,
                                       <Description("New tag name (empty = list tags)")> Optional name As String = "",
                                       <Description("Message: makes an annotated tag")> Optional message As String = "",
                                       <Description("Commit to tag (default HEAD)")> Optional ref As String = "",
                                       Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim dir = RepoDir(repo)
            If String.IsNullOrWhiteSpace(name) Then
                Dim l = Await GitAsync(dir, {"tag", "--list", "--sort=-creatordate", "--format=%(refname:short)  %(creatordate:short)  %(subject)"}, ct).ConfigureAwait(False)
                Return If(String.IsNullOrWhiteSpace(l.Out), "No tags.", l.Out)
            End If
            Dim a = Await IdentityAsync(dir, ct).ConfigureAwait(False)
            a.Add("tag")
            If Not String.IsNullOrWhiteSpace(message) Then a.AddRange({"-a", "-m", message.Trim()})
            a.Add(Plain(name, "name"))
            If Not String.IsNullOrWhiteSpace(ref) Then a.Add(Plain(ref, "ref"))
            Await GitAsync(dir, a, ct).ConfigureAwait(False)
            Return $"Tagged {name.Trim()}. Push it with git_push (branch: the tag name)."
        End Function

        Friend Async Function CloneAsync(<Description("Repository URL (https:// or ssh)")> url As String,
                                         <Description("Folder name to clone into (default: from the URL)")> Optional folder As String = "",
                                         <Description("Branch to check out (optional)")> Optional branch As String = "",
                                         <Description("Shallow clone with this many commits (0 = full history)")> Optional depth As Integer = 0,
                                         Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim u = Plain(url, "url")
            If Not (u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) OrElse u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) OrElse
                    u.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) OrElse Regex.IsMatch(u, "^[\w.-]+@[\w.-]+:")) Then
                Throw New McpException("Only https://, http://, ssh:// and user@host:path URLs can be cloned.")
            End If
            Dim name = If(String.IsNullOrWhiteSpace(folder), Regex.Replace(u.TrimEnd("/"c).Split("/"c, ":"c).Last(), "\.git$", "", RegexOptions.IgnoreCase), folder.Trim())
            Dim target = _roots.Resolve(name)
            If Directory.Exists(target) AndAlso Directory.EnumerateFileSystemEntries(target).Any() Then Throw New McpException($"'{_roots.Show(target)}' already exists and is not empty.")
            Dim a As New List(Of String) From {"clone", "--no-hardlinks"}
            If depth > 0 Then a.AddRange({"--depth", depth.ToString(Globalization.CultureInfo.InvariantCulture)})
            If Not String.IsNullOrWhiteSpace(branch) Then a.AddRange({"--branch", Plain(branch, "branch")})
            a.AddRange({"--", u, target})
            Await GitAsync(_roots.Roots(0), a, ct, timeout:=LongTimeout, remoteUrl:=u).ConfigureAwait(False)
            Dim head = Await GitAsync(target, {"log", "-1", "--pretty=format:%h %s (%an, %ad)", "--date=short"}, ct, allowFailure:=True).ConfigureAwait(False)
            Return $"Cloned into {_roots.Show(target)}. Last commit: {head.Out}"
        End Function

        Friend Async Function PullAsync(<Description("Repository folder")> repo As String,
                                        <Description("Rebase instead of merge")> Optional rebase As Boolean = False,
                                        Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim dir = RepoDir(repo)
            Dim a = Await IdentityAsync(dir, ct).ConfigureAwait(False)
            a.AddRange({"pull", If(rebase, "--rebase", "--no-rebase"), "--no-edit"})
            Return (Await GitAsync(dir, a, ct, timeout:=LongTimeout).ConfigureAwait(False)).Out
        End Function

        Friend Async Function FetchAsync(<Description("Repository folder")> repo As String, ct As CancellationToken) As Task(Of String)
            Dim dir = RepoDir(repo)
            Dim r = Await GitAsync(dir, {"fetch", "--all", "--prune"}, ct, timeout:=LongTimeout).ConfigureAwait(False)
            Return If(String.IsNullOrWhiteSpace(r.Out), "Up to date.", r.Out) & vbLf & (Await StatusAsync(repo, ct).ConfigureAwait(False)).Split(ControlChars.Lf)(0)
        End Function

        Friend Async Function PushAsync(<Description("Repository folder")> repo As String,
                                        <Description("Remote (default origin)")> Optional remote As String = "origin",
                                        <Description("Branch or tag to push (default: the current branch)")> Optional branch As String = "",
                                        <Description("Set the upstream (for a new branch)")> Optional set_upstream As Boolean = False,
                                        Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim dir = RepoDir(repo)
            Dim a As New List(Of String) From {"push"}
            If set_upstream Then a.Add("--set-upstream")
            a.Add(Plain(If(String.IsNullOrWhiteSpace(remote), "origin", remote), "remote"))
            If String.IsNullOrWhiteSpace(branch) Then
                a.Add("HEAD")
            Else
                Dim b = Plain(branch, "branch")
                If b.StartsWith("+", StringComparison.Ordinal) Then Throw New McpException("Force pushes are not allowed.")
                a.Add(b)
            End If
            Dim r = Await GitAsync(dir, a, ct, timeout:=LongTimeout).ConfigureAwait(False)
            Return If(String.IsNullOrWhiteSpace(r.Out), "Pushed.", r.Out)
        End Function

        Friend Async Function InitAsync(<Description("Folder for the new repository")> folder As String, ct As CancellationToken) As Task(Of String)
            Dim dir = _roots.Resolve(folder)
            Directory.CreateDirectory(dir)
            Dim r = Await GitAsync(dir, {"init", "-b", "main"}, ct).ConfigureAwait(False)
            Return r.Out
        End Function
    End Class

End Namespace
