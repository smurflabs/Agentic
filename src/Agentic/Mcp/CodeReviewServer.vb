Option Strict On
Option Explicit On
Option Infer On

Imports System.ComponentModel
Imports System.IO
Imports System.Text
Imports System.Text.RegularExpressions
Imports ModelContextProtocol
Imports ModelContextProtocol.Server

Namespace Mcp

    Public NotInheritable Class ReviewFinding
        Public Property Rule As String
        Public Property Severity As String
        Public Property Line As Integer
        Public Property Message As String
        Public Property Snippet As String
    End Class

    ''' <summary>
    ''' Built-in "code review" MCP server: fast, deterministic checks an agent runs before or alongside its own reading of the
    ''' code - hard-coded secrets, injection risks, unsafe deserialization, weak crypto, disabled TLS checks, swallowed errors,
    ''' leftover debug code, oversized or deeply nested code and duplication - plus a diff that reports only newly introduced
    ''' problems, and per-language review checklists. Files can be reviewed straight from the shared storage folder.
    ''' </summary>
    Public NotInheritable Class CodeReviewServer
        Private ReadOnly _storage As SharedStorageServer

        Private NotInheritable Class Rule
            Public Id As String, Severity As String, Message As String, Pattern As Regex, Languages As String()
        End Class

        Private Shared Function R(id As String, severity As String, pattern As String, message As String, Optional languages As String() = Nothing) As Rule
            Return New Rule With {.Id = id, .Severity = severity, .Message = message, .Languages = languages,
                                  .Pattern = New Regex(pattern, RegexOptions.Compiled Or RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200))}
        End Function

        Private Shared ReadOnly Rules As Rule() = {
            R("SEC001", "high", "(?i)\b(password|passwd|pwd|secret|api[_-]?key|access[_-]?token|auth[_-]?token|client[_-]?secret)\b\s*[:=]\s*[""'][^""'\s]{6,}[""']",
              "Hard-coded credential. Load secrets from configuration or a secret store."),
            R("SEC002", "high", "-----BEGIN (RSA |EC |OPENSSH |DSA |ENCRYPTED )?PRIVATE KEY-----", "Private key embedded in source."),
            R("SEC003", "high", "\b(AKIA[0-9A-Z]{16}|ghp_[A-Za-z0-9]{36}|github_pat_[A-Za-z0-9_]{30,}|sk-[A-Za-z0-9]{20,}|xox[baprs]-[A-Za-z0-9-]{10,})\b",
              "Looks like a real access key or token."),
            R("SEC010", "high", "(?i)[""'`]\s*(select\b.+?\bfrom|insert\s+into|update\s+\w+\s+set|delete\s+from)\b.*?[""'`]\s*(\+|&|\.\s*concat)",
              "SQL built by string concatenation: use parameters."),
            R("SEC011", "high", "(?i)(\bf|\$)[""'](select\b.+\bfrom|insert\s+into|update\s+\w+\s+set|delete\s+from)\b[^""']*\{", "SQL built with string interpolation: use parameters."),
            R("SEC020", "high", "(?<![\w.])(eval|exec)\s*\(|new\s+Function\s*\(", "Dynamic code execution (eval/exec): avoid, or never pass untrusted input."),
            R("SEC021", "high", "os\.system\s*\(|subprocess\.\w+\([^)]*shell\s*=\s*True|child_process\.exec(Sync)?\s*\(|Runtime\.getRuntime\(\)\.exec|Process\.Start\(\s*""(cmd|powershell)",
              "Shell command execution: injection risk if any part comes from input; pass arguments separately."),
            R("SEC030", "medium", "pickle\.loads?\(|yaml\.load\((?![^)]*SafeLoader)|BinaryFormatter|ObjectInputStream|\bunserialize\s*\(|Marshal\.load",
              "Unsafe deserialization of potentially untrusted data."),
            R("SEC040", "medium", "hashlib\.(md5|sha1)\b|\bMD5\.Create|\bSHA1\.Create|DESCryptoServiceProvider|TripleDES|Cipher\.getInstance\(""(DES|RC4)|createHash\([""'](md5|sha1)",
              "Weak cryptographic algorithm (MD5/SHA-1/DES/RC4) for anything security-related."),
            R("SEC050", "high", "verify\s*=\s*False|rejectUnauthorized\s*:\s*false|ServerCertificateValidationCallback\s*\+?=.*(=>|Function).*\b[Tt]rue\b|InsecureSkipVerify\s*:\s*true|CURLOPT_SSL_VERIFYPEER\s*,\s*(0|false)",
              "TLS certificate validation disabled."),
            R("SEC060", "medium", "\.innerHTML\s*=|dangerouslySetInnerHTML|document\.write\s*\(|\.html\(\s*[^)'""\s]", "Possible XSS: HTML built from data. Escape or use text APIs."),
            R("SEC070", "low", "(?i)[""']http://(?!localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1\])", "Plain-HTTP URL: prefer HTTPS outside trusted networks."),
            R("REL001", "medium", "catch\s*(\([^)]*\))?\s*\{\s*\}", "Empty catch block swallows errors."),
            R("REL002", "medium", "^\s*except\s*:\s*(pass)?\s*$", "Bare 'except:' catches everything, including KeyboardInterrupt.", {"python"}),
            R("REL003", "medium", "^\s*except\s+\w+(\s+as\s+\w+)?\s*:\s*pass\s*$", "Exception silently ignored.", {"python"}),
            R("REL010", "low", "(?i)\b(TODO|FIXME|HACK|XXX)\b", "Unfinished work marker."),
            R("REL020", "low", "console\.log\(|\bdebugger\s*;|System\.out\.println|Debug\.Print|\bvar_dump\(|\bdd\(", "Debug output left in code."),
            R("REL030", "low", "Thread\.Sleep\(|time\.sleep\(|Start-Sleep", "Fixed sleep: prefer waiting on the actual condition or a timer.")}

        Private Shared ReadOnly Extensions As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
            {".py", "python"}, {".js", "javascript"}, {".mjs", "javascript"}, {".ts", "typescript"}, {".tsx", "typescript"}, {".jsx", "javascript"},
            {".cs", "csharp"}, {".vb", "vbnet"}, {".java", "java"}, {".go", "go"}, {".rb", "ruby"}, {".php", "php"}, {".rs", "rust"},
            {".ps1", "powershell"}, {".sh", "shell"}, {".sql", "sql"}, {".html", "html"}, {".c", "c"}, {".cpp", "cpp"}, {".h", "c"}, {".kt", "kotlin"}}

        Public Sub New(Optional storageRoot As String = Nothing)
            If Not String.IsNullOrWhiteSpace(storageRoot) Then _storage = New SharedStorageServer(storageRoot)
        End Sub

        Public Shared Function RunStdio(args As String()) As Integer
            Dim root As String = Nothing
            For i = 0 To args.Length - 2
                If String.Equals(args(i), "--mcp-code-review", StringComparison.OrdinalIgnoreCase) AndAlso Not args(i + 1).StartsWith("--", StringComparison.Ordinal) Then root = args(i + 1)
            Next
            Dim server As New CodeReviewServer(root)
            Dim tools As New McpServerPrimitiveCollection(Of McpServerTool)()
            Dim add = Sub(d As [Delegate], name As String, description As String)
                          tools.Add(McpServerTool.Create(d, New McpServerToolCreateOptions With {.Name = name, .Description = description, .ReadOnly = True, .Idempotent = True}))
                      End Sub
            add(New Func(Of String, String, String, String)(AddressOf server.ReviewCode), "review_code",
                "Run automated review checks on source code (security, reliability, maintainability). Returns findings with line numbers and metrics.")
            If server._storage IsNot Nothing Then
                add(New Func(Of String, String)(AddressOf server.ReviewFile), "review_file", "Run the automated review checks on a file in the shared storage (path relative to it).")
            End If
            add(New Func(Of String, String, String, String)(AddressOf server.DiffCode), "diff_code",
                "Compare two versions of code: a line diff plus the review findings that the new version introduces.")
            add(New Func(Of String, String)(AddressOf server.ReviewChecklist), "review_checklist", "A code review checklist for a language (things automated checks cannot judge).")
            Return BuiltInServer.Run("agentic-code-review", tools)
        End Function

        Friend Shared Function DetectLanguage(filename As String, language As String) As String
            If Not String.IsNullOrWhiteSpace(language) Then Return language.Trim().ToLowerInvariant()
            Dim lang As String = Nothing
            If Not String.IsNullOrWhiteSpace(filename) AndAlso Extensions.TryGetValue(Path.GetExtension(filename), lang) Then Return lang
            Return "unknown"
        End Function

        ''' <summary>All findings for a piece of code (deterministic; used by the tools and the tests).</summary>
        Friend Shared Function Analyze(code As String, language As String) As (Findings As List(Of ReviewFinding), Metrics As String)
            Dim lines = If(code, "").Replace(vbCrLf, vbLf).Split(ControlChars.Lf)
            Dim findings As New List(Of ReviewFinding)
            Dim add = Sub(rule As String, severity As String, line As Integer, message As String, snippet As String)
                          findings.Add(New ReviewFinding With {.Rule = rule, .Severity = severity, .Line = line, .Message = message, .Snippet = TextUtilTrim(snippet)})
                      End Sub
            Dim commentLines = 0, codeLines = 0, functions = 0, maxDepth = 0, maxDepthLine = 0, longLines As New List(Of Integer), tabs = False, spaces = False
            Dim funcPattern As New Regex("^\s*(def |async def |function\b|(public|private|protected|internal|friend|static|async|override|shared|\s)*(sub|function)\s+\w+|(public|private|protected|internal|static|async|override|virtual|\s)+[\w<>\[\],]+\s+\w+\s*\([^;]*\)\s*(\{|=>|$)|func\s+\w+|fn\s+\w+)", RegexOptions.IgnoreCase)
            For i = 0 To lines.Length - 1
                Dim line = lines(i), n = i + 1, trimmed = line.Trim()
                If trimmed.Length = 0 Then Continue For
                Dim isComment = trimmed.StartsWith("//") OrElse trimmed.StartsWith("#") OrElse trimmed.StartsWith("'") OrElse trimmed.StartsWith("--") OrElse trimmed.StartsWith("/*") OrElse trimmed.StartsWith("*") OrElse trimmed.StartsWith("REM ", StringComparison.OrdinalIgnoreCase)
                If isComment Then commentLines += 1 Else codeLines += 1
                If funcPattern.IsMatch(line) Then functions += 1
                If line.Length > 140 Then longLines.Add(n)
                Dim indent = line.Length - line.TrimStart().Length
                If line.StartsWith(vbTab) Then tabs = True
                If line.StartsWith("  ") Then spaces = True
                Dim depth = If(line.StartsWith(vbTab), line.TakeWhile(Function(c) c = vbTab(0)).Count(), indent \ 4)
                If depth > maxDepth AndAlso Not isComment Then maxDepth = depth : maxDepthLine = n
                For Each rule In Rules
                    If rule.Languages IsNot Nothing AndAlso Not rule.Languages.Contains(language) Then Continue For
                    ' Secret and marker rules also apply to comments; code rules do not.
                    If isComment AndAlso Not (rule.Id.StartsWith("SEC00") OrElse rule.Id = "REL010") Then Continue For
                    Try
                        If rule.Pattern.IsMatch(line) Then add(rule.Id, rule.Severity, n, rule.Message, trimmed)
                    Catch ex As RegexMatchTimeoutException
                    End Try
                Next
                ' VB/PowerShell-style empty Catch: "Catch" line directly followed by "End Try" / "}".
                If Regex.IsMatch(trimmed, "^Catch(\s+\w+\s+As\s+\w+)?$", RegexOptions.IgnoreCase) AndAlso i + 1 < lines.Length AndAlso
                   Regex.IsMatch(lines(i + 1).Trim(), "^(End Try)$", RegexOptions.IgnoreCase) Then add("REL001", "medium", n, "Empty Catch block swallows errors.", trimmed)
            Next
            If longLines.Count > 0 Then add("MNT001", "low", longLines(0), $"{longLines.Count} line(s) longer than 140 characters (first at line {longLines(0)}).", "")
            If maxDepth >= 6 Then add("MNT002", "low", maxDepthLine, $"Deep nesting ({maxDepth} levels): extract functions or return early.", lines(maxDepthLine - 1).Trim())
            If lines.Length > 600 Then add("MNT003", "info", 1, $"Large file ({lines.Length} lines): consider splitting it.", "")
            If tabs AndAlso spaces Then add("MNT004", "low", 1, "Mixed tab and space indentation.", "")
            Dim dupes = lines.Select(Function(l, idx) (Text:=l.Trim(), Line:=idx + 1)).Where(Function(x) x.Text.Length >= 30).GroupBy(Function(x) x.Text).Where(Function(g) g.Count() >= 3).ToList()
            For Each g In dupes.Take(3)
                add("MNT005", "info", g.First().Line, $"Same line repeated {g.Count()} times: consider a shared function or constant.", g.Key)
            Next
            Dim order = New Dictionary(Of String, Integer) From {{"high", 0}, {"medium", 1}, {"low", 2}, {"info", 3}}
            findings = findings.OrderBy(Function(f) order(f.Severity)).ThenBy(Function(f) f.Line).ToList()
            Dim metrics = $"{lines.Length} lines ({codeLines} code, {commentLines} comment), ~{functions} functions, max nesting {maxDepth}"
            Return (findings, metrics)
        End Function

        Private Shared Function TextUtilTrim(s As String) As String
            If String.IsNullOrEmpty(s) Then Return s
            Return If(s.Length > 160, s.Substring(0, 160) & "…", s)
        End Function

        Private Shared Function Format(title As String, language As String, result As (Findings As List(Of ReviewFinding), Metrics As String)) As String
            Dim f = result.Findings
            Dim sb As New StringBuilder()
            sb.Append($"Automated review of {title} ({language}): {result.Metrics}.").Append(vbLf)
            sb.Append($"Findings: {f.Where(Function(x) x.Severity = "high").Count()} high, {f.Where(Function(x) x.Severity = "medium").Count()} medium, " &
                      $"{f.Where(Function(x) x.Severity = "low").Count()} low, {f.Where(Function(x) x.Severity = "info").Count()} info.").Append(vbLf)
            For Each x In f.Take(80)
                sb.Append($"[{x.Severity.ToUpperInvariant()}] {x.Rule} line {x.Line}: {x.Message}")
                If Not String.IsNullOrEmpty(x.Snippet) Then sb.Append("  → ").Append(x.Snippet)
                sb.Append(vbLf)
            Next
            If f.Count > 80 Then sb.Append($"... {f.Count - 80} more findings.").Append(vbLf)
            sb.Append("These are pattern checks only: still read the code for correctness, edge cases, error handling, naming, tests and design (see review_checklist).")
            Return sb.ToString()
        End Function

        Friend Function ReviewCode(<Description("The source code to review")> code As String,
                                   <Description("File name, used to detect the language (optional)")> Optional filename As String = "",
                                   <Description("Language, e.g. python, csharp, javascript (optional)")> Optional language As String = "") As String
            If String.IsNullOrWhiteSpace(code) Then Throw New McpException("code is required.")
            If code.Length > 400000 Then Throw New McpException("The code is too large (max 400,000 characters); review it in parts.")
            Dim lang = DetectLanguage(filename, language)
            Return Format(If(String.IsNullOrWhiteSpace(filename), "the code", filename), lang, Analyze(code, lang))
        End Function

        Friend Function ReviewFile(<Description("File path in the shared storage, e.g. 'src/app.py'")> path As String) As String
            Dim code = _storage.ReadFile(path)
            Return Format(path, DetectLanguage(path, Nothing), Analyze(code, DetectLanguage(path, Nothing)))
        End Function

        Friend Function DiffCode(<Description("The original code")> original As String, <Description("The changed code")> revised As String,
                                 <Description("File name (optional)")> Optional filename As String = "") As String
            Dim a = If(original, "").Replace(vbCrLf, vbLf).Split(ControlChars.Lf)
            Dim b = If(revised, "").Replace(vbCrLf, vbLf).Split(ControlChars.Lf)
            If a.Length > 3000 OrElse b.Length > 3000 Then Throw New McpException("Each version may have at most 3,000 lines.")
            ' Longest common subsequence diff.
            Dim lcs(a.Length, b.Length) As Integer
            For i = a.Length - 1 To 0 Step -1
                For j = b.Length - 1 To 0 Step -1
                    lcs(i, j) = If(a(i) = b(j), lcs(i + 1, j + 1) + 1, Math.Max(lcs(i + 1, j), lcs(i, j + 1)))
                Next
            Next
            Dim ops As New List(Of (Kind As Char, Text As String, NewLine As Integer))
            Dim x = 0, y = 0
            While x < a.Length OrElse y < b.Length
                If x < a.Length AndAlso y < b.Length AndAlso a(x) = b(y) Then
                    ops.Add((" "c, a(x), y + 1)) : x += 1 : y += 1
                ElseIf y < b.Length AndAlso (x >= a.Length OrElse lcs(x, y + 1) >= lcs(x + 1, y)) Then
                    ops.Add(("+"c, b(y), y + 1)) : y += 1
                Else
                    ops.Add(("-"c, a(x), 0)) : x += 1
                End If
            End While
            Dim added = ops.Where(Function(o) o.Kind = "+"c).Count(), removed = ops.Where(Function(o) o.Kind = "-"c).Count()
            Dim sb As New StringBuilder()
            sb.Append($"Diff of {If(String.IsNullOrWhiteSpace(filename), "the code", filename)}: +{added} / -{removed} lines.").Append(vbLf)
            Dim shown = 0
            For k = 0 To ops.Count - 1
                Dim near = Enumerable.Range(Math.Max(0, k - 2), Math.Min(ops.Count, k + 3) - Math.Max(0, k - 2)).Any(Function(z) ops(z).Kind <> " "c)
                If Not near Then Continue For
                If shown >= 400 Then sb.Append("... (diff truncated)").Append(vbLf) : Exit For
                sb.Append(ops(k).Kind).Append(" "c).Append(ops(k).Text).Append(vbLf)
                shown += 1
            Next
            ' Findings in the new version on changed lines only.
            Dim lang = DetectLanguage(filename, Nothing)
            Dim changedLines = New HashSet(Of Integer)(ops.Where(Function(o) o.Kind = "+"c).Select(Function(o) o.NewLine))
            Dim introduced = Analyze(revised, lang).Findings.Where(Function(f) changedLines.Contains(f.Line) AndAlso Not f.Rule.StartsWith("MNT")).ToList()
            sb.Append(vbLf).Append(If(introduced.Count = 0, "The change introduces no findings.", $"Findings introduced by the change ({introduced.Count}):")).Append(vbLf)
            For Each f In introduced.Take(50)
                sb.Append($"[{f.Severity.ToUpperInvariant()}] {f.Rule} line {f.Line}: {f.Message}  → {f.Snippet}").Append(vbLf)
            Next
            Return sb.ToString().TrimEnd()
        End Function

        Friend Function ReviewChecklist(<Description("Language, e.g. python, csharp, javascript (optional)")> Optional language As String = "") As String
            Dim lang = If(language, "").Trim().ToLowerInvariant()
            Dim sb As New StringBuilder("Code review checklist" & If(lang.Length > 0, $" ({lang})", "") & ":" & vbLf)
            For Each item In {
                "Correctness: does it do what the task asks? Check edge cases (empty, null, very large, unicode, concurrent use).",
                "Errors: are failures handled or reported, not swallowed? Are resources (files, connections) closed on every path?",
                "Inputs: is every external input validated? Are queries parameterized and shell arguments passed separately?",
                "Secrets: no credentials in code, logs or error messages.",
                "Tests: are the new paths covered, including failure cases?",
                "Readability: clear names, small functions, comments explain why rather than what.",
                "Performance: no needless work in loops, N+1 queries or unbounded memory growth.",
                "Compatibility: public interfaces, data formats and configuration stay backward compatible or are migrated."}
                sb.Append("- ").Append(item).Append(vbLf)
            Next
            Select Case lang
                Case "python" : sb.Append("- Python: type hints on public functions; no mutable default arguments; context managers for files; specific exceptions.")
                Case "csharp", "vbnet" : sb.Append("- .NET: async all the way (no .Result/.Wait), CancellationToken passed through, IDisposable in using blocks, nullable handling.")
                Case "javascript", "typescript" : sb.Append("- JS/TS: awaited promises and handled rejections, strict equality, no implicit any, user data escaped before HTML.")
                Case "java" : sb.Append("- Java: try-with-resources, no raw types, equals/hashCode together, immutable value objects.")
                Case "go" : sb.Append("- Go: every error checked, contexts passed down, goroutines can exit, no data races (run with -race).")
                Case "sql" : sb.Append("- SQL: indexes for new filters, transactions around multi-step changes, no SELECT * in production queries.")
            End Select
            Return sb.ToString().TrimEnd()
        End Function
    End Class

End Namespace
