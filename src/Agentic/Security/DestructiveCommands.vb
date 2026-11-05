Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports System.Text.RegularExpressions

Namespace Security

    ''' <summary>
    ''' Commands that would break a shared machine for everyone, refused before they reach an MCP server (the code sandbox is
    ''' one container that every user's chats and agents share and that keeps its files): recursive deletes of / (or of a
    ''' system folder, or of the home folder), --no-preserve-root, formatting or overwriting disks, fork bombs, killing every
    ''' process, shutting the machine down, removing the sandbox's own container or the host's volumes through Docker,
    ''' privileged or host-root containers, and the same things hidden behind base64. On Windows hosts: deleting a drive or
    ''' Windows itself, formatting, shutting down. Every string in a call's arguments is checked (a command, a script written
    ''' to a file, input typed into a terminal). It is a guard rail, not a sandbox: the container's own limits come first.
    ''' </summary>
    Public NotInheritable Class DestructiveCommands

        Private Sub New()
        End Sub

        Private Const Opts As RegexOptions = RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant

        ''' <summary>Folders whose recursive removal (or the removal of everything in them) breaks the machine or wipes everyone's work.</summary>
        Private Shared ReadOnly ProtectedPaths As String() = {
            "", "/", "~", "$home", "${home}", "/home", "/home/user", "/root", "/bin", "/boot", "/dev", "/etc", "/lib", "/lib32", "/lib64", "/libx32",
            "/opt", "/proc", "/run", "/sbin", "/srv", "/sys", "/usr", "/usr/bin", "/usr/lib", "/usr/local", "/usr/sbin", "/usr/share", "/var", "/var/lib",
            "/snap", "/mnt", "/media", "/workspace", ".."}

        ''' <summary>The same without the home folder (chmod -R / chown -R of one's own home is a fix, not damage).</summary>
        Private Shared ReadOnly SystemPaths As String() = ProtectedPaths.Where(Function(p) Not {"~", "$home", "${home}", "/home", "/home/user", ".."}.Contains(p)).ToArray()

        ''' <summary>Where a command can start: the beginning, after ; &amp;&amp; || | ( $( or a newline, or after sudo / env / nohup.</summary>
        Private Const Cmd As String = "(?:^|[;&|\n(`]\s*|\$\(\s*)(?:(?:sudo|doas|nohup|env|exec|command|xargs|time)(?:\s+-\S+)*\s+)*"
        ''' <summary>
        ''' The same, and after an opening quote too (bash -c "…", os.system('…')): for commands no one quotes in passing
        ''' (rm, dd, mkfs …), unlike words such as "reboot" in a sentence.
        ''' </summary>
        Private Const QCmd As String = "(?:^|[;&|\n(`""'\[,]\s*|\$\(\s*)(?:(?:sudo|doas|nohup|env|exec|command|xargs|time)(?:\s+-\S+)*\s+)*"

        Private Shared ReadOnly Checks As (Pattern As Regex, Reason As String)() = {
            (New Regex("--no-preserve-root", Opts), "it removes / (--no-preserve-root)"),
            (New Regex(QCmd & "mkfs(\.\w+)?\b", Opts), "it formats a disk (mkfs)"),
            (New Regex(QCmd & "(wipefs|sfdisk|fdisk|parted|blkdiscard)\b", Opts), "it changes the disks' partitions"),
            (New Regex(QCmd & "shred\b[^\n;|&]*\s/dev/", Opts), "it shreds a disk"),
            (New Regex(QCmd & "dd\b[^\n;|&]*\bof=/dev/(?!null\b|zero\b|stdout\b|stderr\b|tty\b)", Opts), "it overwrites a disk (dd of=/dev/…)"),
            (New Regex(">\s*/dev/(sd|nvme|vd|hd|xvd|mmcblk)", Opts), "it overwrites a disk"),
            (New Regex(":\s*\(\s*\)\s*\{[^}]*:\s*\|\s*:", Opts), "it is a fork bomb"),
            (New Regex("\b(\w+)\s*\(\s*\)\s*\{\s*\1\s*\|\s*\1\s*&", Opts), "it is a fork bomb"),
            (New Regex(QCmd & "kill\s+(-\S+\s+)*-1\b", Opts), "it kills every process (kill -1)"),
            (New Regex(QCmd & "(killall5\b|pkill\s+(-\S+\s+)*-u\s+\S+\s*(?=$|[;&|\n]))", Opts), "it kills every process of the user"),
            (New Regex(Cmd & "(shutdown|reboot|halt|poweroff)\b", Opts), "it shuts the machine down"),
            (New Regex(Cmd & "(init|telinit)\s+[06]\b", Opts), "it shuts the machine down"),
            (New Regex(Cmd & "systemctl\s+(poweroff|reboot|halt|kexec)\b", Opts), "it shuts the machine down"),
            (New Regex("\bcd\s+(/|~|\$HOME)\s*(&&|;|\n)\s*(sudo\s+)?rm\s+(-\S*[rR]\S*|--recursive)[^\n;|&]*\s\*", Opts), "it deletes everything in / or the home folder"),
            (New Regex(QCmd & "docker\s+(container\s+)?(rm|stop|kill|restart|pause|update)\b[^\n;|&]*\bopen-terminal\b", Opts), "it stops or removes the sandbox's own container"),
            (New Regex(QCmd & "docker\s+(container\s+)?(exec|cp|commit|export|attach)\b[^\n;|&]*\bopen-terminal\b", Opts), "it reaches into the sandbox's own container as another user (docker exec)"),
            (New Regex(QCmd & "docker\s+(system\s+prune|volume\s+(rm|prune))", Opts), "it removes the host's Docker volumes or containers"),
            (New Regex(QCmd & "docker\s+(container\s+)?(rm|kill|stop)\b[^\n;]*\$\(\s*docker\s+(ps|container\s+ls)\b", Opts), "it stops or removes every container on the host"),
            (New Regex("\bdocker\b[^\n;|&]*\s--privileged\b", Opts), "it starts a privileged container (root on the host)"),
            (New Regex("\bdocker\b[^\n;|&]*\s--pid[= ]host\b", Opts), "it starts a container in the host's processes"),
            (New Regex("\bdocker\b[^\n;|&]*\s(-v|--volume)[= ]?\s*['""]?/['""]?:", Opts), "it mounts the host's whole disk into a container"),
            (New Regex("\bdocker\b[^\n;|&]*--mount[= ][^\n;|&]*\b(source|src)=/[,\s'""]", Opts), "it mounts the host's whole disk into a container"),
            (New Regex("\b(base64\s+(-d|--decode|-D)|xxd\s+-r|openssl\s+(base64|enc)\s+-d)[^\n;]*\|\s*(sudo\s+)?(ba|z|da|k)?sh\b", Opts), "it runs a hidden (encoded) command"),
            (New Regex("\beval\s+[""']?\$\(\s*(echo|printf)\s+[^)]*\|\s*(base64|xxd)", Opts), "it runs a hidden (encoded) command"),
            (New Regex("\brmtree\(\s*(r?[""'](/|~|/home(/user)?|/usr|/etc|/bin|/lib|/var|/opt|/root)/?[""']|os\.path\.expanduser\(\s*[""']~[""']\s*\))", Opts), "it deletes / or a system folder (rmtree)"),
            (New Regex("[""']rm[""']\s*,\s*[""']-\w*[rR]\w*[""']\s*,\s*[""'](/|~|/home(/user)?|/usr|/etc|/bin|/var)/?\*?[""']", Opts), "it deletes / or a system folder"),
            (New Regex("\b(rmSync|rmdirSync|rimraf(\.sync)?|remove(Sync)?)\(\s*[""'](/|/home(/user)?|/usr|/etc|/bin|/var)/?[""']", Opts), "it deletes / or a system folder"),
            (New Regex("\bRemove-Item\b[^\n;|]*-Recurse[^\n;|]*\s['""]?[A-Za-z]:\\?(Windows|Users|Program Files[^'""]*|ProgramData)?\\?\*?['""]?(\s|$)", Opts), "it deletes a drive or Windows itself"),
            (New Regex("\b(rd|rmdir)\s+(/s|/q|\s)+\s*['""]?[A-Za-z]:\\?(Windows|Users)?\\?['""]?(\s|$)", Opts), "it deletes a drive or Windows itself"),
            (New Regex("\bdel\s+(/[a-z]\s+)*['""]?[A-Za-z]:\\\*", Opts), "it deletes a drive"),
            (New Regex("\bformat(\.com)?\s+[A-Za-z]:", Opts), "it formats a drive"),
            (New Regex("\b(Stop-Computer|Restart-Computer|Clear-Disk|Format-Volume|Remove-Partition)\b", Opts), "it shuts Windows down or erases a disk"),
            (New Regex("\bshutdown(\.exe)?\s+/[srph]\b", Opts), "it shuts Windows down")}

        Private Shared ReadOnly RmCall As New Regex(QCmd & "(rm|mv|chmod|chown|chgrp)\s+([^\n;|&]*)", Opts)
        Private Shared ReadOnly FindDelete As New Regex(QCmd & "find\s+(\S+)([^\n;|&]*)", Opts)

        ''' <summary>A path argument as it compares with the protected list: quotes, a trailing /, /* or /. taken off, lower case.</summary>
        Private Shared Function Bare(arg As String) As (Path As String, Everything As Boolean)
            Dim p = arg.Trim().Trim("'"c, """"c, ")"c, "]"c, ","c, "`"c, ";"c).Trim("'"c, """"c).ToLowerInvariant()
            Dim everything = False
            For Each tail In {"/*", "/.*", "/.", "/"}
                If p.Length > 1 AndAlso p.EndsWith(tail, StringComparison.Ordinal) Then
                    everything = everything OrElse tail.Contains("*"c)
                    p = p.Substring(0, p.Length - tail.Length)
                End If
            Next
            If p = "*" OrElse p = "/*" Then Return ("", True)
            p = p.Replace("${home}", "$home")
            Return (p, everything)
        End Function

        Private Shared Function IsProtected(arg As String) As Boolean
            Dim b = Bare(arg)
            If b.Path = "/" Then Return True
            Return ProtectedPaths.Contains(b.Path)
        End Function

        ''' <summary>Why one text (a command, a script) must not run, or Nothing.</summary>
        Public Shared Function CheckText(text As String) As String
            If String.IsNullOrWhiteSpace(text) Then Return Nothing
            Dim t = text.Replace(vbCr, "")
            For Each c In Checks
                If c.Pattern.IsMatch(t) Then Return c.Reason
            Next
            ' rm -r, mv, chmod -R, chown -R of / or a system or home folder.
            For Each m As Match In RmCall.Matches(t)
                Dim verb = m.Groups(1).Value.ToLowerInvariant()
                Dim args = Regex.Split(m.Groups(2).Value.Trim(), "\s+").Where(Function(a) a.Length > 0).ToList()
                Dim flags = args.Where(Function(a) a.StartsWith("-", StringComparison.Ordinal)).ToList()
                Dim targets = args.Where(Function(a) Not a.StartsWith("-", StringComparison.Ordinal)).ToList()
                Dim recursive = flags.Any(Function(f) f = "--recursive" OrElse (Not f.StartsWith("--", StringComparison.Ordinal) AndAlso f.IndexOfAny({"r"c, "R"c}) > 0))
                Select Case verb
                    Case "rm"
                        If recursive AndAlso targets.Any(AddressOf IsProtected) Then Return "it deletes / or a system or home folder (" & m.Value.Trim() & ")"
                    Case "mv"
                        If targets.Count >= 2 AndAlso targets.Take(targets.Count - 1).Any(AddressOf IsProtected) Then Return "it moves / or a system or home folder away"
                    Case Else
                        If recursive AndAlso targets.Skip(1).Any(Function(x) Bare(x).Path = "/" OrElse SystemPaths.Contains(Bare(x).Path)) Then Return $"it changes the permissions of / or a system or home folder ({verb} -R)"
                End Select
            Next
            ' find / -delete (or -exec rm) without a name filter: everything under it.
            For Each m As Match In FindDelete.Matches(t)
                Dim rest = m.Groups(2).Value
                If IsProtected(m.Groups(1).Value) AndAlso Regex.IsMatch(rest, "-delete\b|-exec\s+(sudo\s+)?rm\b", Opts) AndAlso
                   Not Regex.IsMatch(rest, "-(i?name|i?path|i?regex|newer|mtime|mmin|size|empty|user)\b", Opts) Then
                    Return "it deletes everything under / or a system or home folder (find -delete)"
                End If
            Next
            Return Nothing
        End Function

        ''' <summary>Why a tool call must not run (every string in its arguments is checked), or Nothing.</summary>
        Public Shared Function Check(arguments As JsonElement) As String
            Dim found As String = Nothing
            Dim visit As Action(Of JsonElement) = Nothing
            visit = Sub(e)
                        If found IsNot Nothing Then Return
                        Select Case e.ValueKind
                            Case JsonValueKind.String : found = CheckText(e.GetString())
                            Case JsonValueKind.Object
                                For Each p In e.EnumerateObject()
                                    visit(p.Value)
                                Next
                            Case JsonValueKind.Array
                                For Each x In e.EnumerateArray()
                                    visit(x)
                                Next
                        End Select
                    End Sub
            visit(arguments)
            If found Is Nothing AndAlso arguments.ValueKind = JsonValueKind.Object Then
                ' An argument list (["rm", "-rf", "/"]) is checked as one command too.
                Dim parts As New List(Of String)
                For Each p In arguments.EnumerateObject()
                    If p.Value.ValueKind = JsonValueKind.Array Then parts.AddRange(p.Value.EnumerateArray().Where(Function(x) x.ValueKind = JsonValueKind.String).Select(Function(x) x.GetString()))
                Next
                If parts.Count > 1 Then found = CheckText(String.Join(" ", parts))
            End If
            Return found
        End Function

        ''' <summary>The message a blocked call returns (to the model, the agent or the Run button).</summary>
        Public Shared Function BlockedMessage(reason As String) As String
            Return $"Blocked: this was not run because {reason}. The code sandbox is one machine that every user's chats and agents share and that keeps " &
                   "its files, so commands that would destroy it or take it down are refused. Do not try it another way; if the user asked for it, tell them it is not allowed here."
        End Function

    End Class

End Namespace
