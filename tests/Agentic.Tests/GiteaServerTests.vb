Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Threading
Imports Agentic.Mcp
Imports ModelContextProtocol
Imports Xunit

Public NotInheritable Class FakeGiteaHandler
    Inherits HttpMessageHandler

    Public ReadOnly Requests As New List(Of (Method As String, Url As String, Auth As String, Body As String))
    Public Property Respond As Func(Of HttpRequestMessage, HttpResponseMessage) =
        Function(r) New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent("{""ok"":true}", Encoding.UTF8, "application/json")}

    Protected Overrides Async Function SendAsync(request As HttpRequestMessage, cancellationToken As CancellationToken) As Task(Of HttpResponseMessage)
        Dim body = If(request.Content Is Nothing, Nothing, Await request.Content.ReadAsStringAsync(cancellationToken))
        Dim auth As IEnumerable(Of String) = Nothing
        request.Headers.TryGetValues("Authorization", auth)
        Requests.Add((request.Method.Method, request.RequestUri.AbsoluteUri, If(auth?.FirstOrDefault(), ""), body))
        Return Respond(request)
    End Function
End Class

Public Class GiteaServerTests

    Private Const Spec As String = "{""paths"":{" &
        """/repos/{owner}/{repo}"":{""get"":{""summary"":""Get a repository"",""operationId"":""repoGet"",""tags"":[""repository""]}," &
        """delete"":{""summary"":""Delete a repository"",""operationId"":""repoDelete"",""tags"":[""repository""]}}," &
        """/repos/{owner}/{repo}/issues/{index}/stopwatch/delete"":{""delete"":{""summary"":""Delete an issue's existing stopwatch."",""operationId"":""issueDeleteStopWatch""}}," &
        """/repos/{owner}/{repo}/hooks"":{""post"":{""summary"":""Create a hook"",""operationId"":""repoCreateHook"",""parameters"":[" &
        "{""name"":""owner"",""in"":""path"",""required"":true,""type"":""string""},{""name"":""body"",""in"":""body"",""schema"":{""$ref"":""#/definitions/CreateHookOption""}}]}}," &
        """/user/hooks"":{""post"":{""summary"":""Create a hook"",""operationId"":""userCreateHook""}}}," &
        """definitions"":{""CreateHookOption"":{""required"":[""type"",""config""],""properties"":{" &
        """type"":{""type"":""string"",""enum"":[""gitea"",""slack""]},""config"":{""$ref"":""#/definitions/CreateHookOptionConfig""},""active"":{""type"":""boolean""}}}}}"

    Private Shared Function NewServer(handler As FakeGiteaHandler) As GiteaServer
        Return New GiteaServer("https://git.example.com/", "secret-token", handler:=handler)
    End Function

    <Fact>
    Public Async Function RequestsGoToTheInstanceApiWithTheToken() As Task
        Dim h As New FakeGiteaHandler()
        Using s = NewServer(h)
            Dim result = Await s.ApiGetAsync("/repos/me/proj/hooks", "{""page"":2,""limit"":50,""type"":[""a"",""b c""]}")
            Assert.StartsWith("200 OK · GET /api/v1/repos/me/proj/hooks", result)
            Await s.ApiWriteAsync("patch", "api/v1/repos/me/proj", "{""archived"":true}")
            Await s.ApiDeleteAsync("/repos/me/proj/hooks/3")
        End Using
        Assert.Equal("https://git.example.com/api/v1/repos/me/proj/hooks?page=2&limit=50&type=a&type=b%20c", h.Requests(0).Url)
        Assert.All(h.Requests, Sub(r) Assert.Equal("token secret-token", r.Auth))
        Assert.Equal(("PATCH", "https://git.example.com/api/v1/repos/me/proj"), (h.Requests(1).Method, h.Requests(1).Url))
        Assert.Equal("{""archived"":true}", h.Requests(1).Body)
        Assert.Equal("DELETE", h.Requests(2).Method)
    End Function

    <Theory>
    <InlineData("https://evil.example/x")>
    <InlineData("//evil.example/x")>
    <InlineData("/repos/../../admin")>
    <InlineData("/repos/%2e%2e/admin")>
    <InlineData("/repos/a b")>
    <InlineData("")>
    Public Sub PathsCannotLeaveTheApi(path As String)
        Using s = NewServer(New FakeGiteaHandler())
            Assert.Throws(Of McpException)(Function() s.BuildUrl(path, ""))
        End Using
    End Sub

    <Fact>
    Public Async Function ErrorsAndBadInputAreReportedAsToolErrors() As Task
        Dim h As New FakeGiteaHandler With {
            .Respond = Function(r) New HttpResponseMessage(HttpStatusCode.Forbidden) With {.Content = New StringContent("{""message"":""token does not have scope""}", Encoding.UTF8, "application/json")}}
        Using s = NewServer(h)
            Dim ex = Await Assert.ThrowsAsync(Of McpException)(Function() s.ApiGetAsync("/admin/users"))
            Assert.Contains("403", ex.Message)
            Assert.Contains("token does not have scope", ex.Message)
            Await Assert.ThrowsAsync(Of McpException)(Function() s.ApiWriteAsync("GET", "/repos/a/b"))
            Await Assert.ThrowsAsync(Of McpException)(Function() s.ApiWriteAsync("POST", "/repos/a/b/hooks", "{not json"))
        End Using
        Assert.Single(h.Requests)
    End Function

    <Fact>
    Public Sub FinderRanksTheObviousEndpointFirstAndShowsBodyFields()
        Dim ops = GiteaServer.ParseSpec(Spec)
        Assert.Equal(5, ops.Count)
        Assert.Equal("repoDelete", GiteaServer.Search(ops, "delete repository", 3)(0).OperationId)
        Dim hook = GiteaServer.Search(ops, "create webhook", 3)(0)
        Assert.Equal("repoCreateHook", hook.OperationId)
        Assert.Contains("body: CreateHookOption", hook.BodyFields)
        Assert.Contains(hook.BodyFields, Function(f) f.Contains("type (string, required) [gitea|slack]"))
        Assert.Contains(hook.BodyFields, Function(f) f.Contains("config (CreateHookOptionConfig, required)"))
        Assert.Empty(GiteaServer.Search(ops, "the a of", 3))
    End Sub

    <Fact>
    Public Async Function FinderLoadsTheSpecFromTheInstanceOnce() As Task
        Dim h As New FakeGiteaHandler With {.Respond = Function(r) New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent(Spec, Encoding.UTF8, "application/json")}}
        Using s = NewServer(h)
            Dim first = Await s.FindEndpointAsync("create hook")
            Await s.FindEndpointAsync("get repo")
            Assert.Contains("POST /repos/{owner}/{repo}/hooks — Create a hook", first)
        End Using
        Assert.Single(h.Requests)
        Assert.Equal("https://git.example.com/swagger.v1.json", h.Requests(0).Url)
    End Function

    <Fact>
    Public Sub HostMustBeAnHttpUrl()
        Assert.Equal("https://git.example.com", GiteaServer.NormalizeHost("https://git.example.com/api/v1/"))
        Assert.Throws(Of ArgumentException)(Function() GiteaServer.NormalizeHost("git.example.com"))
        Assert.Throws(Of ArgumentException)(Function() GiteaServer.NormalizeHost("file:///c:/x"))
    End Sub

End Class
