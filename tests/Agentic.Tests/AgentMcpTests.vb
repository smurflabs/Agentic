Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Threading
Imports Agentic.Mcp
Imports ModelContextProtocol
Imports Xunit

''' <summary>The built-in agent tool servers: utilities, git, documents (more in AgentMcpOpsTests).</summary>
Public Class AgentMcpTests

    Private Shared Function TempDir() As String
        Dim d = Path.Combine(Path.GetTempPath(), "mcp-" & Guid.NewGuid().ToString("N").Substring(0, 10))
        Directory.CreateDirectory(d)
        Return d
    End Function

    Private Shared Sub Cleanup(dir As String)
        Try
            For Each f In Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                File.SetAttributes(f, FileAttributes.Normal)
            Next
            Directory.Delete(dir, True)
        Catch ex As Exception
        End Try
    End Sub

    ' ------------------------------------------------------------------ utilities

    <Fact>
    Public Sub CalculateIsExact()
        Dim u As New UtilitiesServer()
        Assert.EndsWith("= 3314.9745", u.Calculate("(1299.99 * 3) * (1 - 15%)"))
        Assert.EndsWith("= 0.3", u.Calculate("0.1 + 0.2"))
        Assert.EndsWith("= 1024", u.Calculate("2^10"))
        Assert.EndsWith("= 120", u.Calculate("5!"))
        Assert.EndsWith("= 1.414214", u.Calculate("round(sqrt(2), 6)"))
        Assert.EndsWith("= 1", u.Calculate("10 mod 3"))
        Assert.EndsWith("= -8", u.Calculate("-2^3"))
        Assert.EndsWith("= 15", u.Calculate("sum(1, 2, 3, 4, 5)"))
        Assert.Contains("(1,234,567.5)", u.Calculate("1234567.5"))
        Assert.Throws(Of McpException)(Function() u.Calculate("1 / 0"))
        Assert.Throws(Of McpException)(Function() u.Calculate("2 +"))
        Assert.Throws(Of McpException)(Function() u.Calculate("foo(1)"))
    End Sub

    <Fact>
    Public Sub TimeZonesAndDates()
        Dim u As New UtilitiesServer()
        Dim ny = u.ConvertTime("2026-07-01 12:00", "UTC", "America/New_York, Asia/Kolkata, +05:30, PST")
        Assert.Contains("America/New_York: 2026-07-01 08:00:00", ny)
        Assert.Contains("daylight saving", ny)
        Assert.Contains("Asia/Kolkata: 2026-07-01 17:30:00", ny)
        Assert.Contains("+05:30: 2026-07-01 17:30:00", ny)
        Assert.Contains("PST: 2026-07-01 05:00:00", ny)
        Assert.Contains("Europe/Berlin: 2026-01-15 09:00:00", u.ConvertTime("2026-01-15T08:00:00Z", "", "Europe/Berlin"))
        Assert.Throws(Of McpException)(Function() u.ConvertTime("now", "Mars/Olympus", "UTC"))
        Assert.StartsWith("2026-11-02 (Monday", u.DateAdd("2026-10-30", business_days:=1))
        Assert.StartsWith("2027-02-28", u.DateAdd("2026-11-30", months:=3))
        Dim diff = u.DateDiff("2026-10-26", "2026-11-02")
        Assert.Contains("7 days", diff)
        Assert.Contains("5 business days", diff)
        Assert.Contains("UTC:", u.CurrentTime("Europe/London"))
    End Sub

    <Fact>
    Public Sub UnitsHashesEncodings()
        Dim u As New UtilitiesServer()
        Assert.Equal("100 °F = 37.7777777778 °C", u.ConvertUnits(100, "F", "C"))
        Assert.Equal("1 GiB = 1073.741824 MB", u.ConvertUnits(1, "GiB", "MB"))
        Assert.Equal("5 mi = 8.04672 km", u.ConvertUnits(5, "mi", "km"))
        Assert.Throws(Of McpException)(Function() u.ConvertUnits(1, "kg", "m"))
        Assert.Contains("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", u.HashText("abc"))
        Assert.Equal("aGVsbG8gd29ybGQ=", u.EncodeText("hello world", "base64"))
        Assert.Equal("hello world", u.EncodeText("aGVsbG8gd29ybGQ=", "base64", True))
        Assert.Equal("a%20b%26c", u.EncodeText("a b&c", "url"))
        ' {"alg":"none"} . {"sub":"42","exp":1}
        Dim jwt = u.EncodeText("eyJhbGciOiJub25lIn0.eyJzdWIiOiI0MiIsImV4cCI6MX0.", "jwt", True)
        Assert.Contains("""sub"": ""42""", jwt)
        Assert.Contains("(expired)", jwt)
        Assert.Equal(3, u.NewUuids(3).Split(ControlChars.Lf).Length)
        Assert.Contains("2 match(es)", u.RegexTest("(?<n>\d+)", "a1 b22"))
        Assert.Equal("a# b#", u.RegexTest("\d+", "a1 b22", "#"))
    End Sub

    ' ------------------------------------------------------------------ git

    <Fact>
    Public Async Function GitWorksOnLocalRepositories() As Task
        Dim root = TempDir()
        Try
            Dim git As New GitServer(New PathRoots({root}))
            Await git.InitAsync("demo", CancellationToken.None)
            File.WriteAllText(Path.Combine(root, "demo", "readme.md"), "# Demo" & vbLf & "first line" & vbLf)
            Dim status = Await git.StatusAsync("demo", CancellationToken.None)
            Assert.Contains("?? readme.md", status)
            Await git.AddAsync("demo", ".", CancellationToken.None)
            Dim commit = Await git.CommitAsync("demo", "Add readme" & vbLf & vbLf & "With a body.", ct:=CancellationToken.None)
            Assert.Contains("Add readme", commit)
            Assert.Matches("(?m)^[0-9a-f]{40}$", commit)

            File.AppendAllText(Path.Combine(root, "demo", "readme.md"), "second line" & vbLf)
            Dim diff = Await git.DiffAsync("demo", ct:=CancellationToken.None)
            Assert.Contains("+second line", diff)
            Assert.Contains("1 file changed", diff)

            Await git.SwitchAsync("demo", "feature/x", create:=True, ct:=CancellationToken.None)
            Await git.CommitAsync("demo", "Second line", all:=True, ct:=CancellationToken.None)
            Dim log = Await git.LogAsync("demo", ct:=CancellationToken.None)
            Assert.Contains("Second line", log)
            Assert.Contains("Add readme", log)
            Assert.Contains("feature/x", Await git.BranchesAsync("demo", CancellationToken.None))
            Assert.Contains("readme.md:3:second line", Await git.GrepAsync("demo", "second", ct:=CancellationToken.None))
            Assert.Contains("first line", Await git.ShowAsync("demo", "HEAD~1", "readme.md", CancellationToken.None))
            Assert.Contains(") second line", Await git.BlameAsync("demo", "readme.md", 3, 3, CancellationToken.None))

            Dim mainBranch = Await git.SwitchAsync("demo", "main", ct:=CancellationToken.None)
            Dim merge = Await git.MergeAsync("demo", "feature/x", ct:=CancellationToken.None)
            Assert.Contains("Fast-forward", merge)
            Await git.TagAsync("demo", "v1.0.0", "First release", ct:=CancellationToken.None)
            Assert.Contains("v1.0.0", Await git.TagAsync("demo", ct:=CancellationToken.None))

            ' Option injection and paths outside the allowed folders are refused.
            Await Assert.ThrowsAsync(Of McpException)(Function() git.SwitchAsync("demo", "--orphan=x", ct:=CancellationToken.None))
            Await Assert.ThrowsAsync(Of McpException)(Function() git.StatusAsync(Path.GetTempPath(), CancellationToken.None))
            Await Assert.ThrowsAsync(Of McpException)(Function() git.StatusAsync("..\..", CancellationToken.None))
            Await Assert.ThrowsAsync(Of McpException)(Function() git.CloneAsync("file:///c:/windows", ct:=CancellationToken.None))
            Await Assert.ThrowsAsync(Of McpException)(Function() git.PushAsync("demo", "origin", "+main", ct:=CancellationToken.None))
        Finally
            Cleanup(root)
        End Try
    End Function

    ' ------------------------------------------------------------------ documents

    Private Const Report As String =
        "# Quarterly report" & vbLf & vbLf &
        "Revenue grew **12%** over the quarter, driven by *new customers* and `api` usage." & vbLf & vbLf &
        "## Highlights" & vbLf & vbLf &
        "- Faster onboarding" & vbLf & "- Lower churn" & vbLf & "  - nested point" & vbLf & vbLf &
        "1. Plan" & vbLf & "2. Build" & vbLf & vbLf &
        "| Region | Revenue | Growth |" & vbLf & "| --- | ---: | --- |" & vbLf & "| North | 1200 | 5% |" & vbLf & "| South | 980 | 9% |" & vbLf & vbLf &
        "```" & vbLf & "SELECT * FROM sales;" & vbLf & "```" & vbLf & vbLf &
        "---" & vbLf & vbLf &
        "## Appendix" & vbLf & vbLf & "Café prices — naïve résumé ✓ check."

    <Fact>
    Public Sub WordDocumentsRoundTrip()
        Dim root = TempDir()
        Try
            Dim docs As New DocumentsServer(New PathRoots({root}))
            Assert.Contains("Wrote out/report.docx", docs.CreateDocument("out/report.docx", Report, "Q3 2026", "Analyst"))
            Dim text = docs.ReadDocument("out/report.docx")
            Assert.Contains("# Q3 2026", text)
            Assert.Contains("# Quarterly report", text)
            Assert.Contains("## Highlights", text)
            Assert.Contains("- Faster onboarding", text)
            Assert.Contains("  - nested point", text)
            Assert.Contains("| North | 1200 | 5% |", text)
            Assert.Contains("Revenue grew 12% over the quarter", text)
            Assert.Contains("Café prices", text)
            Dim info = docs.DocumentInfo("out/report.docx")
            Assert.Contains("title: Q3 2026", info)
            Assert.Contains("creator: Analyst", info)
            Assert.Contains("[Document]", docs.SearchDocument("out/report.docx", "lower churn"))
        Finally
            Cleanup(root)
        End Try
    End Sub

    <Fact>
    Public Sub PdfReportsCanBeReadBack()
        Dim root = TempDir()
        Try
            Dim docs As New DocumentsServer(New PathRoots({root}))
            docs.CreateDocument("report.pdf", Report & vbLf & vbLf & String.Join(vbLf & vbLf, Enumerable.Range(1, 60).Select(Function(i) $"Paragraph {i} with enough words to wrap across the line at least once, so the layout has work to do.")), "Q3 2026", "")
            Dim info = docs.DocumentInfo("report.pdf")
            Assert.Contains("Title: Q3 2026", info)
            Assert.Matches("[3-9] page\(s\)", info)
            Dim page1 = docs.ReadDocument("report.pdf", "1")
            Assert.Contains("Quarterly report", page1)
            Assert.Contains("Revenue grew 12%", page1)
            Assert.Contains("North", page1)
            Assert.Contains("SELECT * FROM sales;", page1)
            Assert.DoesNotContain("Appendix", page1)  ' after the page break
            Assert.Contains("Café", docs.ReadDocument("report.pdf", "2"))
            Assert.Contains("[Page 2]", docs.SearchDocument("report.pdf", "Appendix"))
            Assert.Contains("Page 1 of", page1)
        Finally
            Cleanup(root)
        End Try
    End Sub

    <Fact>
    Public Sub SpreadsheetsRoundTrip()
        Dim root = TempDir()
        Try
            Dim docs As New DocumentsServer(New PathRoots({root}))
            docs.CreateSpreadsheet("data/sales.xlsx", "{""Sales"": [{""region"": ""North"", ""amount"": 1200.5, ""date"": ""2026-10-01"", ""zip"": ""00501""}, {""region"": ""South"", ""amount"": 980, ""date"": ""2026-10-02"", ""zip"": ""30301""}], ""Notes"": [[""a"",""b""],[""1"",""TRUE""]]}")
            Dim text = docs.ReadDocument("data/sales.xlsx")
            Assert.Contains("=== Sheet Sales ===", text)
            Assert.Contains("| region | amount | date | zip |", text)
            Assert.Contains("| North | 1200.5 | 2026-10-01 | 00501 |", text)
            Assert.Contains("| 1 | TRUE |", text)
            Assert.Contains("Sheets (2): Sales, Notes", docs.DocumentInfo("data/sales.xlsx"))
            Assert.DoesNotContain("Notes", docs.ReadDocument("data/sales.xlsx", sheet:="Sales").Replace("Sheet Sales", ""))

            docs.CreateSpreadsheet("data/list.csv", "name,qty" & vbLf & """Widget, large"",3" & vbLf)
            Assert.Contains("| Widget, large | 3 |", docs.ReadDocument("data/list.csv"))
            docs.ConvertDocument("data/list.csv", "data/list.xlsx")
            Assert.Contains("| Widget, large | 3 |", docs.ReadDocument("data/list.xlsx"))
            docs.ConvertDocument("data/sales.xlsx", "data/sales.html")
            Assert.Contains("<td>North</td>", File.ReadAllText(Path.Combine(root, "data", "sales.html")))

            Assert.Throws(Of McpException)(Function() docs.ReadDocument("..\outside.txt"))
            Assert.Throws(Of McpException)(Function() docs.ReadDocument("missing.pdf"))
        Finally
            Cleanup(root)
        End Try
    End Sub

End Class
