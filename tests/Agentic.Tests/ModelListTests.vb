Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Threading
Imports Agentic.AI
Imports Agentic.Configuration
Imports Agentic.Logging
Imports Agentic.Persistence
Imports Xunit

''' <summary>
''' The provider's model list (what /api/chat/meta, the model pickers and RAG wait for): a slow or hung provider never holds a
''' page once a list is known, the first load gives up after ModelListTimeout, and the last good list survives a restart.
''' </summary>
<Collection(TimingSensitive.Name)>
Public Class ModelListTests

    ''' <summary>Answers /models with two models, or hangs until the request is cancelled (Hang = True).</summary>
    Private NotInheritable Class FakeProvider
        Inherits HttpMessageHandler
        Public Hang As Boolean
        Public Status As HttpStatusCode = HttpStatusCode.OK
        Private _requests As Integer
        Public ReadOnly Property Requests As Integer
            Get
                Return Volatile.Read(_requests)
            End Get
        End Property
        Protected Overrides Async Function SendAsync(request As HttpRequestMessage, ct As CancellationToken) As Task(Of HttpResponseMessage)
            Interlocked.Increment(_requests)
            If Hang Then Await Task.Delay(Timeout.Infinite, ct)
            Return New HttpResponseMessage(Status) With {
                .Content = New StringContent("{""data"":[{""id"":""sev/glm-5.3"",""name"":""GLM 5.3""},{""id"":""arena-model""},{""id"":""dddai/qwen"",""name"":""Qwen""}]}", Encoding.UTF8, "application/json")}
        End Function
    End Class

    ''' <summary>A provider request may take 1.5 s here: a page that waited for one would take at least that long.</summary>
    Private Shared Function NewHub(fake As FakeProvider, Optional db As Database = Nothing) As ModelHub
        Return New ModelHub(New AIProviderOptions With {.BaseUrl = "http://provider.test/api", .ApiKey = "k"}, db, New NullLog(), fake) With {
            .ModelListTimeout = TimeSpan.FromMilliseconds(1500), .ModelListRetryPause = TimeSpan.FromMinutes(5)}
    End Function

    <Fact>
    Public Async Function AKnownListComesBackAtOnceWhileTheProviderHangs() As Task
        Dim fake As New FakeProvider()
        Dim hub = NewHub(fake)
        Dim first = Await hub.ListAllUpstreamAsync(CancellationToken.None)
        Assert.Equal({"dddai/qwen", "sev/glm-5.3"}, first.Select(Function(m) m.Id).ToArray())  ' sorted, arena-model hidden
        Assert.Equal(1, fake.Requests)

        ' A minute later the provider hangs: every page still gets the list at once, and only one request goes out for them all.
        fake.Hang = True
        hub.ExpireModelListForTests()
        Dim sw = Stopwatch.StartNew()
        Dim pages = Await Task.WhenAll(Enumerable.Range(0, 8).Select(Function(i) hub.ListAllUpstreamAsync(CancellationToken.None)))
        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms")
        Assert.All(pages, Sub(p) Assert.Equal(2, p.Count))
        Await Task.Delay(200)
        Assert.Equal(2, fake.Requests)

        ' The background load gives up after ModelListTimeout and keeps the list; the provider is not asked again for a while.
        Await Task.Delay(2000)
        Dim after = Await hub.ListAllUpstreamAsync(CancellationToken.None)
        Assert.Equal(2, after.Count)
        Assert.Equal(2, fake.Requests)

        ' Asking for a fresh list on purpose (Refresh) waits at most ModelListTimeout and still answers with the last list.
        sw.Restart()
        Dim forced = Await hub.ListAllUpstreamAsync(CancellationToken.None, force:=True)
        Assert.Equal(2, forced.Count)
        Assert.True(sw.ElapsedMilliseconds < 5000, $"took {sw.ElapsedMilliseconds} ms")
    End Function

    <Fact>
    Public Async Function TheFirstLoadGivesUpInsteadOfHanging() As Task
        Dim fake As New FakeProvider With {.Hang = True}
        Dim hub = NewHub(fake)
        Dim sw = Stopwatch.StartNew()
        Dim ex = Await Assert.ThrowsAsync(Of HttpRequestException)(Function() hub.ListUpstreamAsync(CancellationToken.None))
        Assert.True(sw.ElapsedMilliseconds < 5000, $"took {sw.ElapsedMilliseconds} ms")
        Assert.Contains("not available", ex.Message)

        ' Right after, the next page gets the same answer at once instead of another wait (the provider is not asked again yet).
        sw.Restart()
        Await Assert.ThrowsAsync(Of HttpRequestException)(Function() hub.ListUpstreamAsync(CancellationToken.None))
        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms")
        Assert.Equal(1, fake.Requests)

        ' Once the provider answers again, the list loads (here on purpose; otherwise after ModelListRetryPause).
        fake.Hang = False
        Assert.Equal(2, (Await hub.ListUpstreamAsync(CancellationToken.None, force:=True)).Count)
        Assert.Equal(2, (Await hub.ListUpstreamAsync(CancellationToken.None)).Count)
    End Function

    <Fact>
    Public Async Function AFailingProviderKeepsTheLastList() As Task
        Dim fake As New FakeProvider()
        Dim hub = NewHub(fake)
        Assert.Equal(2, (Await hub.ListAllUpstreamAsync(CancellationToken.None)).Count)
        fake.Status = HttpStatusCode.BadGateway
        Dim again = Await hub.ListAllUpstreamAsync(CancellationToken.None, force:=True)
        Assert.Equal(2, again.Count)
    End Function

    ''' <summary>A provider whose model list times out the way HttpClient does (TaskCanceledException, though nobody cancelled).</summary>
    Private NotInheritable Class TimingOutProvider
        Implements IAIProvider
        Public ReadOnly Property Name As String = "openwebui" Implements IAIProvider.Name
        Public Function ListModelsAsync(ct As CancellationToken) As Task(Of List(Of String)) Implements IAIProvider.ListModelsAsync
            ct.ThrowIfCancellationRequested()
            Return Task.FromException(Of List(Of String))(New TaskCanceledException("The operation was canceled."))
        End Function
        Public Function ChatAsync(request As ChatRequest, ct As CancellationToken) As Task(Of ChatResponse) Implements IAIProvider.ChatAsync
            Throw New NotSupportedException()
        End Function
        Public Function GenerateImageAsync(prompt As String, size As String, timeout As TimeSpan, ct As CancellationToken) As Task(Of ImageGenerationResult) Implements IAIProvider.GenerateImageAsync
            Throw New NotSupportedException()
        End Function
        Public Function GenerateImageWithModelAsync(model As String, viaEngine As Boolean, prompt As String, size As String, timeout As TimeSpan, ct As CancellationToken) As Task(Of ImageGenerationResult) Implements IAIProvider.GenerateImageWithModelAsync
            Throw New NotSupportedException()
        End Function
        Public Function EditImageWithModelAsync(model As String, prompt As String, size As String, image As Byte(), imageMime As String, imageName As String,
                                                timeout As TimeSpan, ct As CancellationToken) As Task(Of ImageGenerationResult) Implements IAIProvider.EditImageWithModelAsync
            Throw New NotSupportedException()
        End Function
        Public Function DownloadAsync(url As String, maxBytes As Integer, ct As CancellationToken) As Task(Of (Data As Byte(), MimeType As String)) Implements IAIProvider.DownloadAsync
            Throw New NotSupportedException()
        End Function
    End Class

    ''' <summary>Production 2026-10-30: a hung provider made the start-up check throw, and the server shut down at every start.</summary>
    <Fact>
    Public Async Function AProviderThatTimesOutDoesNotStopTheStart() As Task
        Dim h As New Global.Agentic.HostServices With {.Options = New HostOptions(), .Log = New NullLog(),
            .Providers = New Dictionary(Of String, IAIProvider)(StringComparer.OrdinalIgnoreCase) From {{"OpenWebUI", New TimingOutProvider()}}}
        Dim check = Await Global.Agentic.HostApplication.CheckProviderAsync(h, CancellationToken.None)
        Assert.False(check.Reachable)
        Assert.Contains("did not answer", check.ErrorMessage)

        ' A real shutdown still stops it.
        Using cts As New CancellationTokenSource()
            cts.Cancel()
            Await Assert.ThrowsAnyAsync(Of OperationCanceledException)(Function() Global.Agentic.HostApplication.CheckProviderAsync(h, cts.Token))
        End Using
    End Function

    <Fact>
    Public Async Function TheLastListSurvivesARestart() As Task
        Dim dir = Path.Combine(Path.GetTempPath(), "models-" & Guid.NewGuid().ToString("N").Substring(0, 8))
        Directory.CreateDirectory(dir)
        Try
            Dim db As New Database(Path.Combine(dir, "agentic.db"), 2)
            Await New SchemaMigrator(db).MigrateAsync()
            Dim before = NewHub(New FakeProvider(), db)
            Await before.LoadDisabledAsync()
            Assert.Equal(2, (Await before.ListAllUpstreamAsync(CancellationToken.None)).Count)

            ' After a restart the provider hangs: the first page gets the saved list at once (and a fresh one loads behind it).
            Dim fake As New FakeProvider With {.Hang = True}
            Dim after = NewHub(fake, db)
            Await after.LoadDisabledAsync()
            Dim sw = Stopwatch.StartNew()
            Dim list = Await after.ListAllUpstreamAsync(CancellationToken.None)
            Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms")
            Assert.Equal({"dddai/qwen", "sev/glm-5.3"}, list.Select(Function(m) m.Id).ToArray())
            Assert.Equal("GLM 5.3", list(1).Name)
            Await Task.Delay(200)
            Assert.Equal(1, fake.Requests)
        Finally
            Try : Directory.Delete(dir, True) : Catch : End Try
        End Try
    End Function

End Class
