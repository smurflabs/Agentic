Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.IO
Imports System.Threading
Imports Agentic
Imports Agentic.AI
Imports Agentic.Configuration
Imports Agentic.Logging
Imports Agentic.Security
Imports Agentic.Utilities

''' <summary>Scriptable AI provider. The responder sees every request and decides the reply (or throws AiException).</summary>
Public NotInheritable Class FakeAIProvider
    Implements IAIProvider

    Public ReadOnly Requests As New ConcurrentQueue(Of ChatRequest)
    Public Property Responder As Func(Of ChatRequest, ChatResponse)
    Public Property Delay As Func(Of ChatRequest, TimeSpan)
    Public Property ImageBytes As Byte() = AI.ModelProbe.RedBluePng(8, 8)
    Public Property Models As New List(Of String) From {"local-model", "cloud-a-model", "cloud-b-model", "vision-model"}

    Public ReadOnly Property Name As String = "OpenWebUI" Implements IAIProvider.Name

    Public Async Function ChatAsync(request As ChatRequest, ct As CancellationToken) As Task(Of ChatResponse) Implements IAIProvider.ChatAsync
        Requests.Enqueue(request)
        Dim d = If(Delay Is Nothing, TimeSpan.Zero, Delay(request))
        If d > TimeSpan.Zero Then Await Task.Delay(d, ct)
        Dim r = If(Responder Is Nothing, FakeAIProvider.Text("ok"), Responder(request))
        r.Usage = New ChatUsage With {.PromptTokens = 1000, .CompletionTokens = 100}
        Return r
    End Function

    Public Function ListModelsAsync(ct As CancellationToken) As Task(Of List(Of String)) Implements IAIProvider.ListModelsAsync
        Return Task.FromResult(Models)
    End Function

    ''' <summary>The image engine answers like an upstream outage.</summary>
    Public Property EngineDown As Boolean
    ''' <summary>Image models (asked by id) that make pictures; any other id fails like an unavailable channel.</summary>
    Public Property WorkingImageModels As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
    ''' <summary>Every image request in order: "engine" (GenerateImageAsync) or the model id.</summary>
    Public ReadOnly ImageCalls As New ConcurrentQueue(Of String)

    Public Function GenerateImageAsync(prompt As String, size As String, timeout As TimeSpan, ct As CancellationToken) As Task(Of ImageGenerationResult) Implements IAIProvider.GenerateImageAsync
        ImageCalls.Enqueue("engine")
        If EngineDown Then Throw New AiException(AiErrorKind.ModelUnavailable, "[ERROR: Service Unavailable]")
        Return Task.FromResult(New ImageGenerationResult With {.Data = ImageBytes, .MimeType = "image/png"})
    End Function

    Public Function EditImageWithModelAsync(model As String, prompt As String, size As String, image As Byte(), imageMime As String, imageName As String,
                                            timeout As TimeSpan, ct As CancellationToken) As Task(Of ImageGenerationResult) Implements IAIProvider.EditImageWithModelAsync
        ImageCalls.Enqueue("edit:" & model)
        If Not WorkingImageModels.Contains(model) Then
            Throw New AiException(AiErrorKind.ModelUnavailable, "litellm.ServiceUnavailableError: ServiceUnavailableError: OpenAIException - No available channel for model " & model & " (request id: 1)No fallback model group found")
        End If
        Return Task.FromResult(New ImageGenerationResult With {.Data = ImageBytes, .MimeType = "image/png"})
    End Function

    Public Function GenerateImageWithModelAsync(model As String, viaEngine As Boolean, prompt As String, size As String, timeout As TimeSpan, ct As CancellationToken) As Task(Of ImageGenerationResult) Implements IAIProvider.GenerateImageWithModelAsync
        ImageCalls.Enqueue(model)
        If Not WorkingImageModels.Contains(model) Then
            Throw New AiException(AiErrorKind.ModelUnavailable, "litellm.ServiceUnavailableError: ServiceUnavailableError: OpenAIException - No available channel for model " & model & " (request id: 1)No fallback model group found")
        End If
        Return Task.FromResult(New ImageGenerationResult With {.Data = ImageBytes, .MimeType = "image/png"})
    End Function

    Public Function DownloadAsync(url As String, maxBytes As Integer, ct As CancellationToken) As Task(Of (Data As Byte(), MimeType As String)) Implements IAIProvider.DownloadAsync
        Return Task.FromResult((ImageBytes, "image/png"))
    End Function

    ' ---- response helpers ----
    Public Shared Function Text(content As String) As ChatResponse
        Return New ChatResponse With {.Content = content, .FinishReason = "stop"}
    End Function

    Public Shared Function Tool(name As String, argsJson As String) As ChatResponse
        Return New ChatResponse With {.FinishReason = "tool_calls", .ToolCalls = New List(Of ToolCallRequest) From {
            New ToolCallRequest With {.Id = "call_" & Guid.NewGuid().ToString("N").Substring(0, 8), .Name = name, .ArgumentsJson = argsJson}}}
    End Function

    Public Shared Function Complete(summary As String, Optional success As Boolean = True) As ChatResponse
        Return Tool("complete_task", JsonUtil.ToJson(New With {.success = success, .summary = summary}))
    End Function

    ''' <summary>Agent id extracted from the system prompt ("agent id: X").</summary>
    Public Shared Function AgentOf(r As ChatRequest) As String
        Dim sys = r.Messages.FirstOrDefault(Function(m) m.Role = "system")?.Content
        If sys Is Nothing Then Return Nothing
        Dim i = sys.IndexOf("(agent id: ", StringComparison.Ordinal)
        If i < 0 Then Return Nothing
        Dim j = sys.IndexOf(")", i, StringComparison.Ordinal)
        Return sys.Substring(i + 11, j - i - 11)
    End Function

    Public Shared Function LastToolResult(r As ChatRequest) As String
        Return r.Messages.LastOrDefault(Function(m) m.Role = "tool")?.Content
    End Function

    Public Shared Function ToolResultCount(r As ChatRequest) As Integer
        Return r.Messages.Where(Function(m) m.Role = "tool").Count()
    End Function
End Class

''' <summary>Builds an isolated host (temp data dir, fake AI, no console) for integration-style tests.</summary>
Public NotInheritable Class TestHost
    Implements IDisposable

    Public ReadOnly Property App As HostApplication
    Public ReadOnly Property Provider As FakeAIProvider
    Public ReadOnly Property Options As HostOptions
    Public ReadOnly Property DataDir As String
    Private ReadOnly _log As StructuredLogger

    Public ReadOnly Property H As HostServices
        Get
            Return App.Services
        End Get
    End Property

    Private Sub New(options As HostOptions, provider As FakeAIProvider, dataDir As String, log As StructuredLogger, app As HostApplication)
        Me.Options = options
        Me.Provider = provider
        Me.DataDir = dataDir
        _log = log
        Me.App = app
    End Sub

    Public Shared Function DefaultOptions(dataDir As String) As HostOptions
        Dim o As New HostOptions()
        o.Host.DataDirectory = dataDir
        o.Host.ShutdownTimeoutSeconds = 5
        o.Server.Enabled = False
        o.Logging.Directory = Nothing
        o.Retention.BackupIntervalHours = 0
        o.AIProviders("OpenWebUI") = New AIProviderOptions With {.BaseUrl = "http://fake.local/api", .ApiKey = "test-key-123456"}
        o.Models.Add(New ModelOptions With {.Key = "local", .ModelId = "local-model", .Tier = 1, .IsLocal = True, .MaxConcurrency = 2,
            .Capabilities = New List(Of ModelCapability) From {ModelCapability.ToolCalling}, .MaxTokens = 1000})
        o.Models.Add(New ModelOptions With {.Key = "cloud-a", .ModelId = "cloud-a-model", .Tier = 2, .MaxConcurrency = 4,
            .Capabilities = New List(Of ModelCapability) From {ModelCapability.ToolCalling}, .MaxTokens = 1000,
            .Pricing = New PricingOptions With {.InputPerMillion = 1D, .OutputPerMillion = 2D}})
        o.Models.Add(New ModelOptions With {.Key = "cloud-b", .ModelId = "cloud-b-model", .Tier = 3, .MaxConcurrency = 4,
            .Capabilities = New List(Of ModelCapability) From {ModelCapability.ToolCalling}, .MaxTokens = 1000,
            .Pricing = New PricingOptions With {.InputPerMillion = 5D, .OutputPerMillion = 10D}})
        o.Models.Add(New ModelOptions With {.Key = "vision", .ModelId = "vision-model", .Tier = 2, .MaxConcurrency = 4,
            .Capabilities = New List(Of ModelCapability) From {ModelCapability.ToolCalling, ModelCapability.Vision}, .MaxTokens = 1000,
            .AllowedPurposes = New List(Of AiPurpose) From {AiPurpose.Vision}, .Pricing = New PricingOptions With {.InputPerMillion = 1D, .OutputPerMillion = 1D}})
        o.Routing.Ladders("Default") = New List(Of String) From {"local", "cloud-a", "cloud-b"}
        o.Routing.Spillover.FromModel = "local"
        o.Routing.Spillover.Candidates = New List(Of String) From {"cloud-a"}
        o.Vision.ModelKeys = New List(Of String) From {"vision"}
        o.ImageGeneration.PricePerImage = 0.04D
        o.Economy.DefaultInitialBalance = 5D
        ' Most tests drain wallets on purpose; agent death has its own tests.
        o.Economy.BankruptcyEndsAgent = False
        o.Learning.Enabled = False ' learning tests turn it on explicitly
        o.Learning.ModelKeys = New List(Of String) From {"local"}
        o.Communication.InlineWaitSeconds = 1
        o.Supervision.RestartDelaySeconds = 1
        ' Hosts in tests do not read Yahoo Finance or NASDAQ Trader (StocksTests answers them).
        o.Stocks.ChartUrl = "http://127.0.0.1:9/v8/finance/chart"
        o.Stocks.NasdaqListedUrl = "http://127.0.0.1:9/nasdaqlisted.txt"
        o.Stocks.OtherListedUrl = "http://127.0.0.1:9/otherlisted.txt"
        ' Nor Polymarket (PolymarketTests answers it).
        o.Polymarket.GammaUrl = "http://127.0.0.1:9"
        o.Polymarket.ClobUrl = "http://127.0.0.1:9"
        o.Polymarket.DataUrl = "http://127.0.0.1:9"
        o.Polymarket.WebSocketUrl = "ws://127.0.0.1:9/ws/market"
        o.BaseProfiles.Add(New ProfileOptions With {
            .Id = "default", .AllowedModels = New List(Of String) From {"local", "cloud-a", "cloud-b", "vision"},
            .Idle = New IdleOptions With {.IdleDelaySeconds = 1},
            .Tasks = New TaskPolicyOptions With {.RetryBaseDelaySeconds = 1, .MaxRetries = 2},
            .InternalTools = ProfileResolver.DefaultInternalTools.Concat({"analyze_image", "generate_image", "broadcast_notification"}).ToList()})
        For Each id In {"A", "B", "C"}
            o.Agents.Add(New ProfileOptions With {.Id = id, .BaseProfileId = "default", .Communication = New CommunicationPolicyOptions With {.AllowedRecipients = New List(Of String)}})
        Next
        Return o
    End Function

    Public Shared Async Function StartAsync(Optional configure As Action(Of HostOptions) = Nothing, Optional provider As FakeAIProvider = Nothing,
                                            Optional mcpFactory As Mcp.IMcpClientFactory = Nothing, Optional dataDir As String = Nothing) As Task(Of TestHost)
        ' dataDir: start again on an earlier host's data (what a restart sees).
        Dim dir = If(dataDir, Path.Combine(Path.GetTempPath(), "agentic-tests", Guid.NewGuid().ToString("N")))
        Directory.CreateDirectory(dir)
        Dim o = DefaultOptions(dir)
        configure?.Invoke(o)
        Dim p = If(provider, New FakeAIProvider())
        Dim redactor As New SecretRedactor()
        Dim log As New StructuredLogger(Nothing, LogSeverity.Warn, LogSeverity.Critical, 1, 10, redactor, consoleEnabled:=False)
        Dim app As New HostApplication(o, Path.Combine(dir, "appsettings.json"), log, redactor, Nothing, mcpFactory, p)
        Await app.BuildAsync()
        Await app.StartAsync()
        Return New TestHost(o, p, dir, log, app)
    End Function

    Public Shared Function Agent(o As HostOptions, id As String) As ProfileOptions
        Return o.Agents.First(Function(a) a.Id = id)
    End Function

    Public Shared Async Function WaitUntilAsync(condition As Func(Of Task(Of Boolean)), Optional timeoutSeconds As Integer = 60) As Task(Of Boolean)
        Dim sw = Stopwatch.StartNew()
        While sw.Elapsed.TotalSeconds < timeoutSeconds
            If Await condition() Then Return True
            Await Task.Delay(100)
        End While
        Return False
    End Function

    Public Async Function WaitForTaskStatusAsync(agentId As String, taskId As String, status As AgentTaskStatus, Optional timeoutSeconds As Integer = 60) As Task(Of Work.AgentTask)
        Dim found As Work.AgentTask = Nothing
        Dim ok = Await WaitUntilAsync(Async Function()
                                          found = Await H.Tasks.GetForAgentAsync(agentId, taskId)
                                          Return found IsNot Nothing AndAlso found.Status = status
                                      End Function, timeoutSeconds)
        If Not ok Then Throw New TimeoutException($"Task {taskId} did not reach {status}; last status {found?.Status}, error {found?.ErrorMessage}")
        Return found
    End Function

    Public Async Function SubmitAsync(agentId As String, name As String, Optional description As String = "do it", Optional handler As String = "reasoning") As Task(Of String)
        Dim r = Await H.Agents.SubmitTaskAsync(agentId, New Work.NewTaskRequest With {.Name = name, .Description = description, .Handler = handler})
        If Not r.Success Then Throw New InvalidOperationException(r.Message)
        Return r.TaskId
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        Try
            App.StopAsync().Wait(TimeSpan.FromSeconds(20))
        Catch
        End Try
        _log.Dispose()
        ' Only this host's pool: clearing every pool would break other tests running in parallel.
        Using c = App.Services.Database.CreateConnection()
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(c)
        End Using
        If KeepData Then Return
        Try
            Directory.Delete(DataDir, True)
        Catch
        End Try
    End Sub

    ''' <summary>Leave the data folder on dispose, for a test that starts again on it (StartAsync dataDir).</summary>
    Public Property KeepData As Boolean
End Class

''' <summary>Manually advanced clock for time-dependent unit tests.</summary>
Public NotInheritable Class FakeClock
    Implements IClock

    Public Property Now As DateTime = New DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)

    Public ReadOnly Property UtcNow As DateTime Implements IClock.UtcNow
        Get
            Return Now
        End Get
    End Property

    Public Sub Advance(by As TimeSpan)
        Now = Now.Add(by)
    End Sub
End Class
