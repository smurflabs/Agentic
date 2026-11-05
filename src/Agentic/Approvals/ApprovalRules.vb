Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports System.Text.RegularExpressions
Imports Agentic.Persistence
Imports Agentic.Utilities

Namespace Approvals

    ''' <summary>A standing approval: an agent (or every agent) may use a tool without asking, for a while or until revoked.</summary>
    Public NotInheritable Class ApprovalRule
        Public Property Id As String
        ''' <summary>An agent id, or "*" for every agent.</summary>
        Public Property AgentId As String
        ''' <summary>A tool name, a prefix ending in "*" (mcp__shell__*), or "*".</summary>
        Public Property Tool As String
        ''' <summary>all = every call; safe = only calls whose command reads and changes nothing (others still ask).</summary>
        Public Property Mode As String = "all"
        Public Property ExpiresUtc As DateTime?
        Public Property Note As String
        Public Property CreatedBy As String
        Public Property CreatedUtc As DateTime
        Public Property RevokedUtc As DateTime?
        Public Property RevokedBy As String
        Public Property Uses As Long
        Public Property LastUsedUtc As DateTime?

        Public Function IsActive(now As DateTime) As Boolean
            Return Not RevokedUtc.HasValue AndAlso (Not ExpiresUtc.HasValue OrElse ExpiresUtc.Value > now)
        End Function

        Public Function CoversTool(name As String) As Boolean
            Dim t = If(Tool, "").Trim()
            If t = "*" Then Return True
            If t.EndsWith("*") Then Return If(name, "").StartsWith(t.TrimEnd("*"c), StringComparison.OrdinalIgnoreCase)
            Return String.Equals(t, name, StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>Whether the rule is for this agent (or a subagent of it). The parameter is not named agentId: VB would read
        ''' the property AgentId as the parameter.</summary>
        Public Function CoversAgent(caller As String) As Boolean
            Dim mine = Me.AgentId
            Dim who = If(caller, "")
            Return mine = "*" OrElse String.Equals(mine, who, StringComparison.OrdinalIgnoreCase) OrElse
                   String.Equals(mine, who.Split(".sub-")(0), StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>What the rule allows, in words ("BackendDevAgent: safe mcp__shell__run_command calls until 18:00 UTC").</summary>
        Public Function Describe() As String
            Return $"{If(AgentId = "*", "every agent", AgentId)}: {If(Mode = "safe", "read-only ", "")}{If(Tool = "*", "every tool", Tool)} " &
                   If(ExpiresUtc.HasValue, $"until {ExpiresUtc.Value:yyyy-MM-dd HH:mm} UTC", "until revoked")
        End Function
    End Class

    ''' <summary>The standing approval rules (table ApprovalRules).</summary>
    Public NotInheritable Class ApprovalRuleStore
        Private ReadOnly _db As Database

        Public Sub New(db As Database)
            _db = db
        End Sub

        Private Shared Function Map(r As Microsoft.Data.Sqlite.SqliteDataReader) As ApprovalRule
            Return New ApprovalRule With {
                .Id = Db.Str(r, "Id"), .AgentId = Db.Str(r, "AgentId"), .Tool = Db.Str(r, "Tool"), .Mode = If(Db.Str(r, "Mode"), "all"),
                .ExpiresUtc = Db.DtN(r, "ExpiresUtc"), .Note = Db.Str(r, "Note"), .CreatedBy = Db.Str(r, "CreatedBy"), .CreatedUtc = Db.Dt(r, "CreatedUtc"),
                .RevokedUtc = Db.DtN(r, "RevokedUtc"), .RevokedBy = Db.Str(r, "RevokedBy"), .Uses = Db.Lng(r, "Uses"), .LastUsedUtc = Db.DtN(r, "LastUsedUtc")}
        End Function

        ''' <summary>Rules in force now, newest first.</summary>
        Public Function ListActiveAsync(now As DateTime) As Task(Of List(Of ApprovalRule))
            Return _db.QueryAsync("SELECT * FROM ApprovalRules WHERE RevokedUtc IS NULL AND (ExpiresUtc IS NULL OR ExpiresUtc > @n) ORDER BY CreatedUtc DESC LIMIT 500",
                                  AddressOf Map, ("@n", CObj(Db.Ts(now))))
        End Function

        ''' <summary>Every rule, newest first (ended ones too).</summary>
        Public Function ListAsync(Optional limit As Integer = 100) As Task(Of List(Of ApprovalRule))
            Return _db.QueryAsync("SELECT * FROM ApprovalRules ORDER BY CreatedUtc DESC LIMIT @l", AddressOf Map, ("@l", CObj(Math.Clamp(limit, 1, 500))))
        End Function

        Public Function GetAsync(id As String) As Task(Of ApprovalRule)
            Return _db.QuerySingleAsync("SELECT * FROM ApprovalRules WHERE Id=@id", AddressOf Map, ("@id", CObj(id)))
        End Function

        Public Async Function AddAsync(agentId As String, tool As String, mode As String, expiresUtc As DateTime?, note As String, actor As String) As Task(Of ApprovalRule)
            Dim a = If(agentId, "").Trim()
            Dim t = If(tool, "").Trim()
            If a.Length = 0 Then Throw New ArgumentException("agentId is required ('*' for every agent).")
            If t.Length = 0 Then Throw New ArgumentException("tool is required ('*' for every tool).")
            Dim m = If(mode, "all").Trim().ToLowerInvariant()
            If m <> "all" AndAlso m <> "safe" Then Throw New ArgumentException("mode must be all or safe.")
            If expiresUtc.HasValue AndAlso expiresUtc.Value <= DateTime.UtcNow Then Throw New ArgumentException("The rule would already have ended.")
            Dim rule As New ApprovalRule With {.Id = Ids.NewId("arl"), .AgentId = a, .Tool = t, .Mode = m, .ExpiresUtc = expiresUtc,
                                               .Note = TextUtil.Truncate(note, 500), .CreatedBy = actor, .CreatedUtc = DateTime.UtcNow}
            Await _db.ExecuteAsync("INSERT INTO ApprovalRules(Id, AgentId, Tool, Mode, ExpiresUtc, Note, CreatedBy, CreatedUtc) VALUES (@id, @a, @t, @m, @e, @n, @by, @c)",
                                   ("@id", CObj(rule.Id)), ("@a", a), ("@t", t), ("@m", m), ("@e", Db.Ts(expiresUtc)), ("@n", CObj(rule.Note)),
                                   ("@by", actor), ("@c", Db.Ts(rule.CreatedUtc))).ConfigureAwait(False)
            Return rule
        End Function

        Public Async Function RevokeAsync(id As String, actor As String) As Task(Of Boolean)
            Return (Await _db.ExecuteAsync("UPDATE ApprovalRules SET RevokedUtc=@n, RevokedBy=@by WHERE Id=@id AND RevokedUtc IS NULL",
                                           ("@n", CObj(Db.Ts(DateTime.UtcNow))), ("@by", actor), ("@id", id)).ConfigureAwait(False)) > 0
        End Function

        ''' <summary>The first rule in force that covers this call, and why it applies; Nothing when none does.</summary>
        Public Async Function MatchAsync(agentId As String, tool As String, argsJson As String, now As DateTime) As Task(Of (Rule As ApprovalRule, Reason As String))
            Dim rules = (Await ListActiveAsync(now).ConfigureAwait(False)).Where(Function(r) r.CoversAgent(agentId) AndAlso r.CoversTool(tool)).ToList()
            If rules.Count = 0 Then Return (Nothing, Nothing)
            Dim risk As (Level As CommandRiskLevel, Why As String)? = Nothing
            ' Specific rules first (one agent and one tool), then the broader ones.
            For Each r In rules.OrderBy(Function(x) If(x.AgentId = "*", 1, 0) + If(x.Tool.Contains("*"c), 1, 0))
                If r.Mode = "safe" Then
                    If Not risk.HasValue Then
                        Dim cmd = CommandRisk.CommandOf(tool, argsJson)
                        risk = If(cmd Is Nothing, (CommandRiskLevel.Medium, "it has no command to check"), CommandRisk.Classify(cmd))
                    End If
                    If risk.Value.Level <> CommandRiskLevel.Low Then Continue For
                End If
                Await _db.ExecuteAsync("UPDATE ApprovalRules SET Uses=Uses+1, LastUsedUtc=@n WHERE Id=@id", ("@n", CObj(Db.Ts(now))), ("@id", r.Id)).ConfigureAwait(False)
                Return (r, $"Allowed by a standing rule set by {r.CreatedBy} ({r.Describe()}){If(r.Mode = "safe", ": " & risk.Value.Why, "")}.")
            Next
            Return (Nothing, Nothing)
        End Function
    End Class

    Public Enum CommandRiskLevel
        ''' <summary>Only reads (lists, shows, searches, checks).</summary>
        Low
        ''' <summary>Changes something, runs code, or cannot be judged.</summary>
        Medium
        ''' <summary>Deletes, stops, changes the system or runs downloaded code.</summary>
        High
    End Enum

    ''' <summary>
    ''' How risky a shell command is, judged from its words (PowerShell, cmd and POSIX shells): Low when every part only reads,
    ''' High when a part deletes, stops, changes the system or runs downloaded code, Medium otherwise. It errs towards Medium:
    ''' anything it does not recognise is not Low.
    ''' </summary>
    Public NotInheritable Class CommandRisk
        Private Sub New()
        End Sub

        ''' <summary>The command a tool call runs (run_command, run_command_background, ssh_run, exec_in_container...), or Nothing.</summary>
        Public Shared Function CommandOf(tool As String, argsJson As String) As String
            Dim t = If(tool, "").ToLowerInvariant()
            If Not (t.EndsWith("run_command") OrElse t.EndsWith("run_command_background") OrElse t.EndsWith("ssh_run") OrElse
                    t.EndsWith("exec_in_container") OrElse t.EndsWith("__shell") OrElse t.EndsWith("execute_command")) Then Return Nothing
            Dim el As JsonElement
            If Not JsonUtil.TryParseElement(argsJson, el) OrElse el.ValueKind <> JsonValueKind.Object Then Return Nothing
            Dim c As JsonElement
            If el.TryGetProperty("command", c) AndAlso c.ValueKind = JsonValueKind.String Then Return c.GetString()
            Return Nothing
        End Function

        Private Shared ReadOnly ReadOnlyCommands As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
            "select-object", "select", "select-string", "sls", "where-object", "?", "sort-object", "measure-object", "measure", "format-table", "ft",
            "format-list", "fl", "format-wide", "fw", "out-string", "out-host", "out-null", "convertto-json", "convertfrom-json", "convertto-csv",
            "convertfrom-csv", "test-path", "test-connection", "test-netconnection", "resolve-dnsname", "write-output", "write-host", "echo", "compare-object",
            "group-object", "join-path", "split-path", "resolve-path", "set-location", "sl", "cd", "chdir", "push-location", "pushd", "pop-location", "popd",
            "hostname", "whoami", "ipconfig", "netstat", "systeminfo", "tasklist", "which", "dir", "ls", "gci", "cat", "gc", "type", "more", "head", "tail",
            "findstr", "grep", "egrep", "fgrep", "rg", "wc", "uniq", "sort", "pwd", "ver", "uname", "df", "du", "free", "uptime", "ps", "id", "nproc", "file",
            "stat", "tree", "realpath", "basename", "dirname", "true", "nslookup", "dig", "host", "ping", "tracert", "traceroute", "pathping", "arp", "route",
            "getmac", "query", "qwinsta", "quser", "lsblk", "lscpu", "lsusb", "lspci", "ss", "ip", "jq", "column", "cut", "tr", "less", "md5sum", "sha256sum",
            "sha1sum", "certutil", "get-filehash", "date", "cal", "printf", "lsof", "w", "who", "last", "dmesg", "journalctl", "systemctl", "service", "nvidia-smi",
            "foreach-object", "%", "if", "exit", "where.exe", "Get-Command", "help", "man"}

        Private Shared ReadOnly DangerousCommands As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
            {"rm", "deletes files"}, {"del", "deletes files"}, {"erase", "deletes files"}, {"rmdir", "deletes folders"}, {"rd", "deletes folders"},
            {"remove-item", "deletes files"}, {"ri", "deletes files"}, {"format", "formats a disk"}, {"format-volume", "formats a disk"}, {"diskpart", "changes disks"},
            {"mkfs", "formats a disk"}, {"dd", "writes raw disk data"}, {"shutdown", "shuts the machine down"}, {"restart-computer", "restarts the machine"},
            {"stop-computer", "shuts the machine down"}, {"reboot", "restarts the machine"}, {"poweroff", "shuts the machine down"}, {"stop-process", "ends processes"},
            {"kill", "ends processes"}, {"taskkill", "ends processes"}, {"pkill", "ends processes"}, {"killall", "ends processes"}, {"spps", "ends processes"},
            {"reg", "changes the registry"}, {"regedit", "changes the registry"}, {"icacls", "changes permissions"}, {"cacls", "changes permissions"},
            {"takeown", "takes ownership of files"}, {"chmod", "changes permissions"}, {"chown", "changes owners"}, {"sudo", "runs as administrator/root"},
            {"su", "switches user"}, {"runas", "runs as another user"}, {"invoke-expression", "runs text as code"}, {"iex", "runs text as code"},
            {"set-executionpolicy", "changes script security"}, {"stop-service", "stops a service"}, {"set-service", "changes a service"}, {"new-service", "creates a service"},
            {"remove-service", "deletes a service"}, {"bcdedit", "changes boot settings"}, {"vssadmin", "changes shadow copies"}, {"wevtutil", "can clear event logs"},
            {"cipher", "can wipe free space"}, {"sdelete", "wipes files"}, {"net", "changes users, shares or services"}, {"netsh", "changes network or firewall settings"},
            {"sc", "changes services"}, {"schtasks", "changes scheduled tasks"}, {"crontab", "changes scheduled jobs"}, {"useradd", "creates users"},
            {"userdel", "deletes users"}, {"passwd", "changes passwords"}, {"iptables", "changes the firewall"}, {"ufw", "changes the firewall"}, {"mount", "mounts disks"},
            {"umount", "unmounts disks"}, {"truncate", "empties files"}, {"shred", "wipes files"}}

        Private Shared ReadOnly ChangingVerbs As String() = {"set-", "new-", "add-", "remove-", "clear-", "copy-", "move-", "rename-", "start-", "stop-", "restart-",
            "install-", "uninstall-", "update-", "enable-", "disable-", "import-", "export-", "out-file", "invoke-", "register-", "unregister-", "suspend-", "resume-",
            "reset-", "grant-", "revoke-", "block-", "unblock-", "write-", "send-", "publish-", "save-", "mount-", "dismount-", "initialize-", "format-volume"}

        ''' <summary>Splits on | ; && || &amp; and line breaks outside quotes; also reports redirections (> file) and sub-commands.</summary>
        Friend Shared Function Segments(command As String) As (Parts As List(Of String), Redirects As List(Of String), Substitution As Boolean)
            Dim parts As New List(Of String)
            Dim redirects As New List(Of String)
            Dim sb As New Text.StringBuilder()
            Dim quote As Char = ChrW(0)
            Dim substitution = False
            Dim s = If(command, "")
            Dim i = 0
            Dim flush = Sub()
                            If sb.ToString().Trim().Length > 0 Then parts.Add(sb.ToString().Trim())
                            sb.Clear()
                        End Sub
            While i < s.Length
                Dim c = s(i)
                If quote <> ChrW(0) Then
                    If c = quote Then quote = ChrW(0)
                    If quote = """"c AndAlso c = "$"c AndAlso i + 1 < s.Length AndAlso s(i + 1) = "("c Then substitution = True
                    sb.Append(c)
                ElseIf c = """"c OrElse c = "'"c Then
                    quote = c : sb.Append(c)
                ElseIf c = "$"c AndAlso i + 1 < s.Length AndAlso s(i + 1) = "("c Then
                    substitution = True : sb.Append(c)
                ElseIf c = "`"c AndAlso s.Count(Function(x) x = "`"c) >= 2 Then
                    ' A pair of backticks is a POSIX command substitution (a single one is PowerShell's escape character).
                    substitution = True : sb.Append(c)
                ElseIf c = "|"c OrElse c = ";"c OrElse c = ControlChars.Lf OrElse c = ControlChars.Cr OrElse c = "&"c Then
                    If c = "&"c AndAlso sb.ToString().TrimEnd().EndsWith(">") Then
                        sb.Append(c)   ' 2>&1
                    Else
                        flush()
                        If i + 1 < s.Length AndAlso (s(i + 1) = c) Then i += 1
                    End If
                ElseIf c = ">"c Then
                    ' Redirection target: the next word.
                    Dim j = i + 1
                    If j < s.Length AndAlso s(j) = ">"c Then j += 1
                    While j < s.Length AndAlso s(j) = " "c
                        j += 1
                    End While
                    Dim k = j
                    While k < s.Length AndAlso Not Char.IsWhiteSpace(s(k)) AndAlso s(k) <> "|"c AndAlso s(k) <> ";"c
                        k += 1
                    End While
                    Dim target = s.Substring(j, k - j).Trim(""""c, "'"c)
                    If Not Regex.IsMatch(target, "^(\$null|nul|null|/dev/null|&\d)$", RegexOptions.IgnoreCase) Then redirects.Add(target)
                    i = k - 1
                Else
                    sb.Append(c)
                End If
                i += 1
            End While
            flush()
            Return (parts, redirects, substitution)
        End Function

        Private Shared Function Words(segment As String) As List(Of String)
            Return Regex.Matches(segment, "(""[^""]*""|'[^']*'|\S+)").Select(Function(m) m.Value).ToList()
        End Function

        ''' <summary>The risk of one command line and why.</summary>
        Public Shared Function Classify(command As String) As (Level As CommandRiskLevel, Why As String)
            Dim text = If(command, "").Trim()
            If text.Length = 0 Then Return (CommandRiskLevel.Medium, "empty command")
            If Regex.IsMatch(text, "\b(powershell|pwsh)(\.exe)?\b.*\s-e(nc(odedcommand)?)?\s+[A-Za-z0-9+/=]{16,}", RegexOptions.IgnoreCase) Then Return (CommandRiskLevel.High, "runs an encoded (hidden) command")
            If Regex.IsMatch(text, "(curl|wget|iwr|irm|invoke-webrequest|invoke-restmethod)\b[^|]*\|\s*(sh|bash|zsh|python\d?|iex|invoke-expression|powershell|pwsh)\b", RegexOptions.IgnoreCase) Then
                Return (CommandRiskLevel.High, "runs code downloaded from the internet")
            End If
            Dim seg = Segments(text)
            If seg.Redirects.Count > 0 Then
                Return (CommandRiskLevel.Medium, $"writes to {String.Join(", ", seg.Redirects.Take(3))}")
            End If
            Dim worst = CommandRiskLevel.Low
            Dim reasons As New List(Of String)
            For Each part In seg.Parts
                Dim r = ClassifyPart(part)
                If r.Level > worst Then worst = r.Level : reasons.Clear()
                If r.Level = worst AndAlso r.Level <> CommandRiskLevel.Low Then reasons.Add(r.Why)
            Next
            If worst = CommandRiskLevel.Low AndAlso seg.Substitution Then Return (CommandRiskLevel.Medium, "runs a sub-command that cannot be checked")
            If worst = CommandRiskLevel.Low Then Return (CommandRiskLevel.Low, "only reads (lists, shows, searches or checks)")
            Return (worst, String.Join("; ", reasons.Distinct().Take(3)))
        End Function

        Private Shared Function ClassifyPart(part As String) As (Level As CommandRiskLevel, Why As String)
            Dim w = Words(part)
            If w.Count = 0 Then Return (CommandRiskLevel.Low, "")
            Dim first = w(0).Trim(""""c, "'"c).TrimStart("&"c, "."c, "\"c, "/"c).Trim()
            If first.Length = 0 AndAlso w.Count > 1 Then w.RemoveAt(0) : first = w(0).Trim(""""c, "'"c)
            ' A path like C:\Windows\System32\whoami.exe or /usr/bin/ls counts as its file name.
            first = Regex.Replace(IO.Path.GetFileName(first.Replace("/"c, "\"c).Replace("\"c, IO.Path.DirectorySeparatorChar)), "\.(exe|cmd|bat|com)$", "", RegexOptions.IgnoreCase)
            Dim args = w.Skip(1).Select(Function(a) a.Trim(""""c, "'"c)).ToList()
            Dim argText = String.Join(" ", args).ToLowerInvariant()
            Dim name = first.ToLowerInvariant()
            Dim danger As String = Nothing
            If DangerousCommands.TryGetValue(name, danger) Then
                If name = "net" AndAlso args.Count > 0 AndAlso {"view", "session", "statistics", "config", "time", "accounts"}.Contains(args(0).ToLowerInvariant()) Then Return (CommandRiskLevel.Low, "")
                If name = "sc" AndAlso args.Count > 0 AndAlso {"query", "qc", "queryex", "qdescription"}.Contains(args(0).ToLowerInvariant()) Then Return (CommandRiskLevel.Low, "")
                If name = "schtasks" AndAlso args.Any(Function(a) a.Equals("/query", StringComparison.OrdinalIgnoreCase)) AndAlso Not args.Any(Function(a) Regex.IsMatch(a, "^/(create|delete|change|run|end)$", RegexOptions.IgnoreCase)) Then Return (CommandRiskLevel.Low, "")
                If name = "netsh" AndAlso args.Any(Function(a) a.Equals("show", StringComparison.OrdinalIgnoreCase)) AndAlso Not args.Any(Function(a) Regex.IsMatch(a, "^(set|add|delete|reset)$", RegexOptions.IgnoreCase)) Then Return (CommandRiskLevel.Low, "")
                If name = "wevtutil" AndAlso args.Count > 0 AndAlso {"qe", "query-events", "el", "enum-logs", "gl", "get-log", "gli"}.Contains(args(0).ToLowerInvariant()) Then Return (CommandRiskLevel.Low, "")
                If name = "reg" AndAlso args.Count > 0 AndAlso args(0).Equals("query", StringComparison.OrdinalIgnoreCase) Then Return (CommandRiskLevel.Low, "")
                If name = "kill" AndAlso args.Count > 0 AndAlso args(0) = "-l" Then Return (CommandRiskLevel.Low, "")
                Return (CommandRiskLevel.High, $"{first} {danger}")
            End If
            If name.StartsWith("get-") OrElse name.StartsWith("test-") OrElse name.StartsWith("show-") OrElse name.StartsWith("find-") OrElse name.StartsWith("measure-") OrElse
               name.StartsWith("search-") OrElse name.StartsWith("read-") AndAlso name <> "read-host" Then
                If name = "get-credential" Then Return (CommandRiskLevel.Medium, "asks for a password")
                If name = "get-childitem" OrElse name = "gci" OrElse name = "dir" OrElse name = "ls" Then
                    If args.Any(Function(a) a.StartsWith("env:", StringComparison.OrdinalIgnoreCase)) Then Return (CommandRiskLevel.Medium, "shows environment variables")
                End If
                Return (CommandRiskLevel.Low, "")
            End If
            For Each v In ChangingVerbs
                If name.StartsWith(v) Then
                    If name = "write-output" OrElse name = "write-host" Then Exit For
                    Return (CommandRiskLevel.Medium, $"{first} changes something")
                End If
            Next
            Select Case name
                Case "git" : Return GitRisk(args)
                Case "find"
                    If args.Any(Function(a) Regex.IsMatch(a, "^-(delete|exec|execdir|ok|okdir|fprint\w*)$")) Then Return (CommandRiskLevel.High, "find with -delete/-exec changes or runs things")
                    Return (CommandRiskLevel.Low, "")
                Case "curl", "wget", "invoke-webrequest", "iwr", "invoke-restmethod", "irm"
                    If Regex.IsMatch(argText, "(^|\s)(-o|-O|--output|--remote-name|-outfile|-x\s*(post|put|delete|patch)|--request\s*(post|put|delete|patch)|-d|--data\S*|-f|--form|-t|--upload-file|-method\s+(post|put|delete|patch)|-body|-infile)(\s|=|$)") OrElse
                       (name = "wget" AndAlso Not argText.Contains("-o -") AndAlso Not argText.Contains("--spider") AndAlso Not argText.Contains("-qo-")) Then
                        Return (CommandRiskLevel.Medium, $"{first} sends data or saves a file")
                    End If
                    Return (CommandRiskLevel.Low, "")
                Case "dotnet"
                    If args.Count = 0 OrElse Regex.IsMatch(argText, "^(--info|--version|--list-sdks|--list-runtimes|list\b|nuget locals\s+\S+\s+--list|sdk check)") Then Return (CommandRiskLevel.Low, "")
                    Return (CommandRiskLevel.Medium, "dotnet builds or runs code")
                Case "npm", "pnpm", "yarn"
                    If Regex.IsMatch(argText, "^(ls|list|view|info|outdated|-v|--version|config get|audit(\s|$)(?!fix)|why|explain|search)") Then Return (CommandRiskLevel.Low, "")
                    Return (CommandRiskLevel.Medium, $"{first} installs, builds or runs code")
                Case "pip", "pip3"
                    If Regex.IsMatch(argText, "^(list|show|freeze|--version|-v|check|index versions)") Then Return (CommandRiskLevel.Low, "")
                    Return (CommandRiskLevel.Medium, "pip installs or removes packages")
                Case "python", "python3", "py", "node", "ruby", "perl", "php", "java", "go", "cargo", "rustc", "gcc", "pwsh", "powershell", "cmd", "bash", "sh", "zsh", "wsl"
                    If args.Count = 1 AndAlso Regex.IsMatch(args(0), "^(--version|-v|-V|version)$") Then Return (CommandRiskLevel.Low, "")
                    Return (CommandRiskLevel.Medium, $"{first} runs code")
                Case "docker", "podman"
                    If args.Count > 0 AndAlso {"ps", "images", "logs", "inspect", "version", "info", "top", "port", "history", "search"}.Contains(args(0).ToLowerInvariant()) Then Return (CommandRiskLevel.Low, "")
                    If args.Count > 1 AndAlso args(0).Equals("stats", StringComparison.OrdinalIgnoreCase) AndAlso argText.Contains("--no-stream") Then Return (CommandRiskLevel.Low, "")
                    If args.Count > 1 AndAlso {"compose", "container", "image", "volume", "network"}.Contains(args(0).ToLowerInvariant()) AndAlso
                       {"ls", "ps", "list", "inspect", "logs", "config", "images"}.Contains(args(1).ToLowerInvariant()) Then Return (CommandRiskLevel.Low, "")
                    Return (CommandRiskLevel.Medium, $"{first} {If(args.Count > 0, args(0), "")} changes containers")
                Case "kubectl"
                    If args.Count > 0 AndAlso {"get", "describe", "logs", "version", "top", "explain", "api-resources", "cluster-info"}.Contains(args(0).ToLowerInvariant()) Then Return (CommandRiskLevel.Low, "")
                    Return (CommandRiskLevel.Medium, "kubectl changes the cluster")
                Case "env", "printenv", "set"
                    If args.Count = 0 OrElse name <> "set" Then Return (CommandRiskLevel.Medium, "shows environment variables")
                    Return (CommandRiskLevel.Medium, "sets variables")
                Case "time"
                    Return If(args.Count = 0 OrElse args(0) = "/t", (CommandRiskLevel.Low, ""), (CommandRiskLevel.Medium, "time with a value sets the clock"))
                Case "systemctl", "service"
                    If args.Count > 0 AndAlso {"status", "list-units", "list-unit-files", "is-active", "is-enabled", "show", "--version"}.Contains(args(0).ToLowerInvariant()) Then Return (CommandRiskLevel.Low, "")
                    If args.Count > 1 AndAlso args(1).Equals("status", StringComparison.OrdinalIgnoreCase) Then Return (CommandRiskLevel.Low, "")
                    Return (CommandRiskLevel.High, $"{first} starts, stops or changes services")
                Case "certutil"
                    If Regex.IsMatch(argText, "(-hashfile|-dump|-verify|-store)") AndAlso Not Regex.IsMatch(argText, "(-urlcache|-decode|-encode|-addstore|-delstore)") Then Return (CommandRiskLevel.Low, "")
                    Return (CommandRiskLevel.High, "certutil can download or change certificates")
                Case "foreach-object", "%"
                    Return (CommandRiskLevel.Medium, "runs a script block for each item")
                Case "if", "exit"
                    Return (CommandRiskLevel.Medium, "contains script logic that cannot be checked")
            End Select
            If ReadOnlyCommands.Contains(name) Then Return (CommandRiskLevel.Low, "")
            Return (CommandRiskLevel.Medium, $"'{first}' is not a known read-only command")
        End Function

        Private Shared Function GitRisk(args As List(Of String)) As (Level As CommandRiskLevel, Why As String)
            ' Skip global options (-C path, -c k=v, --no-pager) to the subcommand.
            Dim i = 0
            While i < args.Count AndAlso args(i).StartsWith("-")
                If args(i) = "-C" OrElse args(i) = "-c" Then i += 1
                i += 1
            End While
            If i >= args.Count Then Return (CommandRiskLevel.Low, "")
            Dim [sub] = args(i).ToLowerInvariant()
            Dim rest = String.Join(" ", args.Skip(i + 1)).ToLowerInvariant()
            Select Case [sub]
                Case "status", "log", "diff", "show", "blame", "grep", "ls-files", "ls-tree", "rev-parse", "describe", "shortlog", "cat-file", "rev-list", "whatchanged", "version", "help", "count-objects", "check-ignore", "name-rev", "merge-base"
                    Return (CommandRiskLevel.Low, "")
                Case "branch"
                    If Regex.IsMatch(rest, "(^|\s)(-d|-D|--delete|-m|-M|--move|-c|-C|--copy|-f|--force|--set-upstream-to|-u)(\s|$)") Then Return (CommandRiskLevel.Medium, "git branch changes branches")
                    If rest.Length = 0 OrElse Regex.IsMatch(rest, "^(-a|-r|-v|-vv|--all|--list|--show-current|--contains|--merged|--no-merged|\s)+") Then Return (CommandRiskLevel.Low, "")
                    Return (CommandRiskLevel.Medium, "git branch creates a branch")
                Case "tag"
                    If rest.Length = 0 OrElse rest.StartsWith("-l") OrElse rest.StartsWith("--list") Then Return (CommandRiskLevel.Low, "")
                    Return (CommandRiskLevel.Medium, "git tag creates or deletes tags")
                Case "remote"
                    If rest.Length = 0 OrElse rest = "-v" OrElse rest.StartsWith("show") OrElse rest.StartsWith("get-url") Then Return (CommandRiskLevel.Low, "")
                    Return (CommandRiskLevel.Medium, "git remote changes remotes")
                Case "config"
                    If Regex.IsMatch(rest, "^(--get|--list|-l|--get-all|--show-origin)") Then Return (CommandRiskLevel.Low, "")
                    Return (CommandRiskLevel.Medium, "git config changes settings")
                Case "stash"
                    If rest.StartsWith("list") OrElse rest.StartsWith("show") Then Return (CommandRiskLevel.Low, "")
                    Return (CommandRiskLevel.Medium, "git stash changes the working tree")
                Case "push"
                    If Regex.IsMatch(rest, "(^|\s)(-f|--force|--force-with-lease|--delete|-d|--mirror)(\s|$)|(^|\s)\+") Then Return (CommandRiskLevel.High, "git push --force/--delete rewrites or deletes remote history")
                    Return (CommandRiskLevel.Medium, "git push sends changes to the remote")
                Case "reset"
                    Return If(rest.Contains("--hard"), (CommandRiskLevel.High, "git reset --hard throws away work"), (CommandRiskLevel.Medium, "git reset moves the branch"))
                Case "clean"
                    Return (CommandRiskLevel.High, "git clean deletes untracked files")
                Case "fetch"
                    Return (CommandRiskLevel.Medium, "git fetch downloads from the remote")
            End Select
            Return (CommandRiskLevel.Medium, $"git {[sub]} changes the repository")
        End Function
    End Class

End Namespace
