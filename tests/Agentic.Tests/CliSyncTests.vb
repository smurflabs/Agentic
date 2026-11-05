Option Strict On
Option Explicit On
Option Infer On

Imports System.Net.Http
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Xunit

''' <summary>The CLI as a device of the account: messages sent from the web app use its tools; approvals are shared.</summary>
Public Class CliSyncTests

    ''' <summary>Reads a live stream (/api/chat/stream) in the background: each "chat" event goes to onEvent(type, data).</summary>
    Private Shared Function Listen(res As HttpResponseMessage, onEvent As Func(Of String, JsonElement, Task)) As Task
        Return Task.Run(Async Function()
                            Try
                                Using stream = Await res.Content.ReadAsStreamAsync(), reader As New IO.StreamReader(stream)
                                    While True
                                        Dim line = Await reader.ReadLineAsync()
                                        If line Is Nothing Then Exit While
                                        If Not line.StartsWith("data: ") Then Continue While
                                        Dim d = JsonDocument.Parse(line.Substring(6)).RootElement
                                        Dim t As JsonElement
                                        If d.TryGetProperty("type", t) AndAlso t.ValueKind = JsonValueKind.String Then Await onEvent(t.GetString(), d.GetProperty("data").Clone())
                                    End While
                                End Using
                            Catch ex As Exception When TypeOf ex Is IO.IOException OrElse TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is OperationCanceledException OrElse TypeOf ex Is HttpRequestException
                            End Try
                        End Function)
    End Function

    Private Const CliTools As String = "[" &
        "{""name"":""Read"",""description"":""Read a file"",""parameters"":{""type"":""object"",""properties"":{""file_path"":{""type"":""string""}}},""x-risk"":""read""}," &
        "{""name"":""Write"",""description"":""Write a file"",""parameters"":{""type"":""object""},""x-risk"":""change""}," &
        "{""name"":""TodoWrite"",""description"":""Task list"",""parameters"":{""type"":""object""},""x-risk"":""read""}," &
        "{""name"":""Task"",""description"":""Subagent"",""parameters"":{""type"":""object""},""x-risk"":""read""}]"

    <Fact>
    Public Async Function AMessageFromTheWebAppRunsOnTheCliAndAnApprovedCallSaysSo() As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    Select Case Interlocked.Increment(stepNo)
                                        Case 1 : Return ChatFeatureTests.TextStream("Hello from the CLI chat.", "stop")
                                        Case 2 : Return ChatFeatureTests.ToolCallStream("Write", "{""file_path"":""a.txt"",""content"":""x""}")
                                        Case Else : Return ChatFeatureTests.TextStream("Written.", "stop")
                                    End Select
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim web = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim cli = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                ' The CLI joins the live stream as a device and says what it offers.
                Dim calls As New Concurrent.ConcurrentQueue(Of JsonElement)
                Dim cliEvents As New Concurrent.ConcurrentQueue(Of String)
                Dim cliStream = Await cli.Client.SendAsync(New HttpRequestMessage(HttpMethod.Get, "/api/chat/stream?device=cli-test&name=CLI%20%C2%B7%20proj"), HttpCompletionOption.ResponseHeadersRead)
                Dim chatId As String = Nothing
                Dim listening = Listen(cliStream, Async Function(type, data)
                                                      cliEvents.Enqueue(type)
                                                      If type = "approval" Then
                                                          ' The user allows it (in the web app; any window may answer).
                                                          Await PlatformApiTests.Send(web.Client, "POST", $"/api/chat/conversations/{chatId}/approvals", New With {.id = data.GetProperty("id").GetString(), .choice = "once"}, web.Csrf)
                                                      ElseIf type = "client_tool" Then
                                                          calls.Enqueue(data)
                                                          Await PlatformApiTests.Send(cli.Client, "POST", $"/api/chat/conversations/{chatId}/tool-results", New With {.id = data.GetProperty("id").GetString(), .ok = True, .result = "wrote a.txt"}, cli.Csrf)
                                                      End If
                                                  End Function)
                Await Task.Delay(200)
                Assert.True((Await PlatformApiTests.Send(cli.Client, "POST", "/api/chat/devices/cli-test/catalog", "{""name"":""CLI · proj"",""clientTools"":" & CliTools & ",""clientInstructions"":""Work in proj.""}", cli.Csrf)).IsSuccessStatusCode)

                ' The CLI's own message: the chat remembers the CLI as its device.
                chatId = Await ChatFeatureTests.NewChatAsync(cli, New With {.model = "fast/a"})
                ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(cli, chatId, New With {.content = "hi", .clientId = "cli-own", .device = "cli-test", .clientTools = JsonNode.Parse(CliTools),
                                                                                             .settings = New With {.permissionMode = "auto"}}))
                ' The CLI brings its own task list and subagents: the server does not add its own (the model would see two).
                Dim names = ChatFeatureTests.ToolNames(ChatFeatureTests.LastStreamedRequest(up))
                Assert.Contains("TodoWrite", names)
                Assert.DoesNotContain("update_todos", names)
                Assert.DoesNotContain("task", names)

                ' The web app sends a message (no tools of its own), asking first: the CLI runs the tool, told it was approved.
                Dim message = ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(web, chatId, New With {.content = "write a.txt", .settings = New With {.permissionMode = "manual"}}))("message")
                Assert.Equal("Written.", message("content").ToString().Trim())
                Assert.Single(calls)
                Dim c = calls.First()
                Assert.Equal("Write", c.GetProperty("name").GetString())
                Assert.Equal("cli-test", c.GetProperty("device").GetString())
                Assert.True(c.GetProperty("approved").GetBoolean())
                ' The chat's permission mode goes with the call (VS Code shows an edit for review only in Manual mode).
                Assert.Equal("manual", c.GetProperty("mode").GetString())
                Assert.Contains("wrote a.txt", message("toolCalls").AsArray().First(Function(t) t("name").ToString() = "Write")("result").ToString())
                ' The CLI saw the answer as it ran (run start, the message, the end) to show it in the terminal.
                Assert.Contains("run", cliEvents)
                Assert.Contains("user", cliEvents)
                Assert.Contains("done", cliEvents)
                cliStream.Dispose()
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function TheCliAsksEveryWindowAndTheFirstAnswerWins() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim web = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim cli = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim chatId = Await ChatFeatureTests.NewChatAsync(cli, New With {.model = "fast/a"})
                Dim seen As New Concurrent.ConcurrentQueue(Of (Type As String, Data As JsonElement))
                Dim webStream = Await web.Client.SendAsync(New HttpRequestMessage(HttpMethod.Get, "/api/chat/stream"), HttpCompletionOption.ResponseHeadersRead)
                Dim listening = Listen(webStream, Async Function(type, data)
                                                      seen.Enqueue((type, data))
                                                      ' The web app shows the CLI's question and the user answers there.
                                                      If type = "approval" AndAlso data.GetProperty("id").GetString() = "cli-a1" Then
                                                          Await PlatformApiTests.Send(web.Client, "POST", $"/api/chat/conversations/{chatId}/approvals", New With {.id = "cli-a1", .choice = "always"}, web.Csrf)
                                                      End If
                                                  End Function)
                Await Task.Delay(200)
                Dim res = Await PlatformApiTests.Send(cli.Client, "POST", $"/api/chat/conversations/{chatId}/approvals/request",
                                                      New With {.id = "cli-a1", .name = "Bash", .arguments = "{""command"":""rm -rf build""}", .reason = "it changes files", .where = "CLI · proj"}, cli.Csrf)
                Dim answer = JsonNode.Parse(Await res.Content.ReadAsStringAsync())
                Assert.Equal("always", answer("choice").ToString())
                Dim asked = seen.First(Function(e) e.Type = "approval").Data
                Assert.Equal("CLI · proj", asked.GetProperty("where").GetString())
                Assert.Equal("client", asked.GetProperty("server").GetString())
                Assert.Contains("rm -rf build", asked.GetProperty("arguments").GetString())

                ' Answered in the terminal instead: the CLI resolves its own question; the other windows hear it.
                Dim pending = PlatformApiTests.Send(cli.Client, "POST", $"/api/chat/conversations/{chatId}/approvals/request",
                                                    New With {.id = "cli-a2", .name = "Write", .arguments = "{}", .reason = "it changes files"}, cli.Csrf)
                Await Task.Delay(300)
                Assert.True((Await PlatformApiTests.Send(cli.Client, "POST", $"/api/chat/conversations/{chatId}/approvals", New With {.id = "cli-a2", .choice = "deny"}, cli.Csrf)).IsSuccessStatusCode)
                Assert.Equal("deny", JsonNode.Parse(Await (Await pending).Content.ReadAsStringAsync())("choice").ToString())
                Await Task.Delay(200)
                Assert.Contains(seen, Function(e) e.Type = "approval_resolved" AndAlso e.Data.GetProperty("id").GetString() = "cli-a2" AndAlso e.Data.GetProperty("choice").GetString() = "deny")
                webStream.Dispose()
            End Using
        End Using
    End Function

End Class
