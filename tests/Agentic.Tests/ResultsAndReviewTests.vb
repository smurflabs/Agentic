Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Agentic
Imports Agentic.AI
Imports Agentic.Configuration
Imports Agentic.Utilities
Imports Agentic.Work
Imports Xunit

Public Class ResultsAndReviewTests

    Private Shared Function CompleteWith(summary As String, data As Object) As ChatResponse
        Return FakeAIProvider.Tool("complete_task", JsonUtil.ToJson(New With {.success = True, .summary = summary, .data = data}))
    End Function

    Private Shared Function UserText(r As ChatRequest) As String
        Return If(r.Messages.FirstOrDefault(Function(m) m.Role = "user")?.Content, "")
    End Function

    Private Shared Function SystemText(r As ChatRequest) As String
        Return If(r.Messages.FirstOrDefault(Function(m) m.Role = "system")?.Content, "")
    End Function

    <Fact>
    Public Async Function ReviewerSendsWorkBackThenApprovesAndTheResultIsDelivered() As Task
        Dim reviews = 0
        Dim dataDir As String = Nothing
        Dim p As New FakeAIProvider()
        p.Responder = Function(r)
                          Dim agent = FakeAIProvider.AgentOf(r)
                          Dim user = UserText(r)
                          If agent = "B" AndAlso user.Contains("Expected outcome — CONFIDENTIAL") Then
                              If Interlocked.Increment(reviews) = 1 Then Return CompleteWith("Missing the count.", New With {.verdict = "revise", .score = 4, .feedback = "Give the exact number of apples."})
                              Return CompleteWith("Now correct.", New With {.verdict = "approve", .score = 9, .feedback = "Exact and complete."})
                          End If
                          If agent = "A" Then
                              ' The worker saves a deliverable into the workflow's results folder (as the storage MCP server would).
                              Dim m = Regex.Match(SystemText(r), "results/(cor_[A-Za-z0-9]+)/")
                              If m.Success AndAlso dataDir IsNot Nothing Then
                                  Dim folder = Path.Combine(dataDir, "shared", "results", m.Groups(1).Value)
                                  Directory.CreateDirectory(folder)
                                  File.WriteAllText(Path.Combine(folder, "report.txt"), "apples: 7")
                              End If
                              Return FakeAIProvider.Complete(If(user.Contains("Revision 1"), "There are 7 apples.", "Some apples."))
                          End If
                          Return FakeAIProvider.Complete("ok")
                      End Function
        Using host = Await TestHost.StartAsync(Nothing, p)
            dataDir = host.H.Options.Host.DataDirectory
            Dim r = Await host.H.Agents.SubmitTaskAsync("A", New NewTaskRequest With {
                .Name = "Count apples", .Description = "Count the apples in the basket.",
                .Delivery = New TaskDelivery With {.Deliver = True, .ReviewerAgentId = "B", .MaxRevisions = 1},
                .ReviewCriteria = "SECRET-CRITERIA: the answer gives the exact apple count (7)."})
            Assert.True(r.Success, r.Message)
            Dim result As ResultRecord = Nothing
            Assert.True(Await TestHost.WaitUntilAsync(Async Function()
                                                          result = Await host.H.Results.FindBySourceAsync("Task", r.TaskId)
                                                          Return result IsNot Nothing
                                                      End Function, 60), "no result was delivered")
            Assert.Equal(2, reviews)
            Assert.Equal("Approved", result.ReviewVerdict)
            Assert.Equal(9, result.ReviewScore)
            Assert.Equal(1, result.Revisions)
            Assert.Contains("7 apples", result.Summary)
            Assert.Contains("A", result.Participants)
            Assert.Contains("B", result.Participants)
            Assert.Contains(result.Files, Function(f) f.Path = "files/report.txt")
            Assert.Contains("Give the exact number of apples", Await ConversationTextAsync(host, result))
            ' Only the reviewer ever saw the expected outcome.
            Assert.DoesNotContain(p.Requests, Function(x) FakeAIProvider.AgentOf(x) = "A" AndAlso x.Messages.Any(Function(m) If(m.Content, "").Contains("SECRET-CRITERIA")))
            Assert.Contains(p.Requests, Function(x) FakeAIProvider.AgentOf(x) = "B" AndAlso UserText(x).Contains("SECRET-CRITERIA"))
            ' The revision told A what to fix.
            Assert.Contains(p.Requests, Function(x) FakeAIProvider.AgentOf(x) = "A" AndAlso UserText(x).Contains("Give the exact number of apples"))

            ' Rating and awards: the agents learn about them; awards are profit and are paid once per key.
            Await host.H.Results.RateAsync(result.Id, 5, "Exactly what I needed", "admin")
            Dim before = Await host.H.Wallets.GetAsync("A")
            Dim paid = Await host.H.Results.AwardAsync(result.Id, {("A", 1000000L)}, "nice", "admin", "k1")
            Dim again = Await host.H.Results.AwardAsync(result.Id, {("A", 1000000L)}, "nice", "admin", "k1")
            Assert.Single(paid)
            Assert.Empty(again)
            Dim after = Await host.H.Wallets.GetAsync("A")
            Assert.Equal(before.BalanceMicros + 1000000L, after.BalanceMicros)
            Assert.Equal(before.LifetimeProfitMicros + 1000000L, after.LifetimeProfitMicros)
            Dim updated = Await host.H.Results.GetAsync(result.Id)
            Assert.Equal(5, updated.Rating)
            Assert.Equal(1000000L, updated.AwardedMicros)
            Dim memories = Await host.H.MemoryStore.RecentAsync("A", 20)
            Assert.Contains(memories, Function(m) m.Content.Contains("rated the result") AndAlso m.Content.Contains("5/5"))
            Assert.Contains(memories, Function(m) m.Content.Contains("awarded"))

            ' The whole result downloads as one ZIP.
            Using zip As New ZipArchive(New MemoryStream(host.H.Results.ZipBytes(updated)))
                Assert.NotNull(zip.GetEntry("RESULT.md"))
                Assert.NotNull(zip.GetEntry("files/report.txt"))
            End Using
        End Using
    End Function

    Private Shared Async Function ConversationTextAsync(host As TestHost, r As ResultRecord) As Task(Of String)
        Dim full = Await host.H.Results.GetAsync(r.Id)
        Return If(full.ConversationJson, "")
    End Function

    <Fact>
    Public Async Function UnreviewedTaskIsDeliveredAsIs() As Task
        Using host = Await TestHost.StartAsync()
            Dim r = Await host.H.Agents.SubmitTaskAsync("A", New NewTaskRequest With {.Name = "Hello", .Description = "Say hello",
                .Delivery = New TaskDelivery With {.Deliver = True}})
            Dim result As ResultRecord = Nothing
            Assert.True(Await TestHost.WaitUntilAsync(Async Function()
                                                          result = Await host.H.Results.FindBySourceAsync("Task", r.TaskId)
                                                          Return result IsNot Nothing
                                                      End Function, 60))
            Assert.Equal("NotReviewed", result.ReviewVerdict)
            Assert.True(result.Success)
            Assert.Equal(1L, Await host.H.Results.CountNewAsync())
        End Using
    End Function

    <Fact>
    Public Async Function AgentDiesWhenItsMoneyRunsOutAndFundingRevivesIt() As Task
        Using host = Await TestHost.StartAsync(Sub(o) o.Economy.BankruptcyEndsAgent = True)
            ' Every agent is told what it has and that running out means death.
            Dim first = Await host.SubmitAsync("A", "Warm-up")
            Await host.WaitForTaskStatusAsync("A", first, AgentTaskStatus.Completed)
            Assert.Contains(host.Provider.Requests, Function(x) FakeAIProvider.AgentOf(x) = "A" AndAlso SystemText(x).Contains("you DIE"))

            Dim w = Await host.H.Wallets.GetAsync("A")
            Await host.H.Wallets.AdjustAsync("A", -(w.BalanceMicros - 5000), "drain", Nothing, "test") ' $0.005 left
            Assert.True(Await TestHost.WaitUntilAsync(Function() Task.FromResult(host.H.Agents.GetSnapshot("A").State = "Dead"), 30), "A did not die")
            Assert.True(host.H.Agents.IsDead("A"))
            Dim t = Await host.SubmitAsync("A", "After death")
            Await Task.Delay(1500)
            Assert.Equal(AgentTaskStatus.Pending, (Await host.H.Tasks.GetAsync(t)).Status)
            Dim start = Await host.H.Agents.StartAgentAsync("A")
            Assert.False(start.Success)
            Assert.Equal("OUT_OF_MONEY", start.Code)

            ' A credit brings it back, and the queued work runs.
            Await host.H.Wallets.AdjustAsync("A", 1000000, "rescue", Nothing, "test")
            Assert.True(Await TestHost.WaitUntilAsync(Function() Task.FromResult(host.H.Agents.GetSnapshot("A").State <> "Dead" AndAlso Not host.H.Agents.IsDead("A")), 30), "A was not revived")
            Await host.WaitForTaskStatusAsync("A", t, AgentTaskStatus.Completed)
        End Using
    End Function

    <Fact>
    Public Async Function AttachedDocumentReachesThePromptAndTheAgent() As Task
        Using host = Await TestHost.StartAsync()
            Dim upload = Await host.H.Uploads.SaveAsync("report.docx", Docx("Quarterly revenue was 42 million."), "admin")
            Assert.Equal("document", upload.Kind)
            Dim atts = Await host.H.Attachments.MaterializeAsync(Await host.H.Attachments.DescribeUploadsAsync({upload.Id}), "A", "f1")
            Dim a = Assert.Single(atts)
            Assert.NotNull(a.ArtifactId)
            Assert.True(File.Exists(Path.Combine(host.H.Options.Host.DataDirectory, "shared", a.SharedPath.Replace("/"c, Path.DirectorySeparatorChar))))
            Dim r = Await host.H.Agents.SubmitTaskAsync("A", New NewTaskRequest With {.Name = "Summarize", .Description = "Summarize the report.",
                .Delivery = New TaskDelivery With {.Attachments = atts}})
            Await host.WaitForTaskStatusAsync("A", r.TaskId, AgentTaskStatus.Completed)
            Assert.Contains(host.Provider.Requests, Function(x) UserText(x).Contains("## Attached files") AndAlso UserText(x).Contains("42 million"))
        End Using
    End Function

    <Fact>
    Public Async Function ConfigAgentsAreSeededOnceAndThenEditable() As Task
        Using host = Await TestHost.StartAsync()
            Dim def = Await host.H.Agents.GetDefinitionAsync("A")
            Assert.Equal("Admin", def.Source)
            def.Profile.Role = "Edited in the dashboard"
            def.Profile.SystemPrompt = "You are a careful analyst."
            Dim report = Await host.H.Agents.SaveAdminAgentAsync(def.Profile, {}, {"local", "cloud-a", "cloud-b", "vision"})
            Assert.True(report.IsValid, String.Join("; ", report.Errors))
            Assert.Equal("Edited in the dashboard", host.H.Agents.GetSnapshot("A").Role)
        End Using
    End Function

    ' ------------------------------------------------------------------ file extraction

    Friend Shared Function Docx(text As String) As Byte()
        Return ZipOf(("word/document.xml", "<w:document xmlns:w=""http://schemas.openxmlformats.org/wordprocessingml/2006/main""><w:body>" &
                                           $"<w:p><w:r><w:t>{text}</w:t></w:r></w:p><w:p><w:r><w:t>Second</w:t><w:tab/><w:t>para</w:t></w:r></w:p></w:body></w:document>"))
    End Function

    Private Shared Function ZipOf(ParamArray entries As (Name As String, Content As String)()) As Byte()
        Using ms As New MemoryStream()
            Using z As New ZipArchive(ms, ZipArchiveMode.Create, True)
                For Each e In entries
                    Using w As New StreamWriter(z.CreateEntry(e.Name).Open(), New UTF8Encoding(False))
                        w.Write(e.Content)
                    End Using
                Next
            End Using
            Return ms.ToArray()
        End Using
    End Function

    <Fact>
    Public Sub ExtractsWordExcelPdfZipCodeAndImages()
        Dim d = FileExtractor.Extract("r.docx", Docx("Hello Word"))
        Assert.Equal("document", d.Kind)
        Assert.Contains("Hello Word", d.Text)
        Assert.Contains("Second" & vbTab & "para", d.Text)

        Dim x = FileExtractor.Extract("book.xlsx", ZipOf(
            ("xl/workbook.xml", "<workbook xmlns:r=""r""><sheets><sheet name=""Sales"" sheetId=""1"" r:id=""rId1""/></sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", "<Relationships><Relationship Id=""rId1"" Target=""worksheets/sheet1.xml""/></Relationships>"),
            ("xl/sharedStrings.xml", "<sst><si><t>Region</t></si><si><t>North</t></si></sst>"),
            ("xl/worksheets/sheet1.xml", "<worksheet><sheetData><row r=""1""><c r=""A1"" t=""s""><v>0</v></c><c r=""B1""><v>12.5</v></c></row>" &
                                        "<row r=""2""><c r=""A2"" t=""s""><v>1</v></c><c r=""C2""><v>3</v></c></row></sheetData></worksheet>")))
        Assert.Equal("spreadsheet", x.Kind)
        Assert.Contains("Sales 2 rows", x.Summary)
        Assert.Contains("Region" & vbTab & "12.5", x.Text)
        Assert.Contains("North" & vbTab & vbTab & "3", x.Text)

        Dim pdfText = "%PDF-1.4" & vbLf & "1 0 obj << /Type /Page >> endobj" & vbLf & "2 0 obj << /Length 60 >>" & vbLf & "stream" & vbLf &
                      "BT /F1 12 Tf 72 712 Td (Invoice total: 1234 EUR) Tj 0 -14 Td (Due in 30 days) Tj ET" & vbLf & "endstream" & vbLf & "endobj"
        Dim pdf = FileExtractor.Extract("inv.pdf", Encoding.Latin1.GetBytes(pdfText))
        Assert.Equal("pdf", pdf.Kind)
        Assert.Contains("Invoice total: 1234 EUR", pdf.Text)
        Assert.Contains("Due in 30 days", pdf.Text)
        Assert.Contains("1 page", pdf.Summary)

        Dim z = FileExtractor.Extract("src.zip", ZipOf(("src/Program.cs", "class P { static void Main() {} }"), ("readme.md", "# Tool")))
        Assert.Equal("archive", z.Kind)
        Assert.Contains("src/Program.cs", z.Text)
        Assert.Contains("static void Main", z.Text)
        Assert.Contains("1 source files", z.Summary)

        Dim vb = FileExtractor.Extract("Module1.vb", Encoding.UTF8.GetBytes("Module M" & vbLf & "End Module"))
        Assert.Equal("code", vb.Kind)
        Assert.Contains("Visual Basic source, 2 lines", vb.Summary)

        Dim png = FileExtractor.Extract("pic.png", ModelProbe.RedBluePng(8, 6))
        Assert.Equal("image", png.Kind)
        Assert.Contains("8×6", png.Summary)
        Assert.Null(png.Text)
    End Sub
End Class
