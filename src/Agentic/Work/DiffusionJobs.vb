Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.Net.Http
Imports System.Text.Json.Nodes
Imports System.Text.Json.Serialization
Imports System.Threading
Imports Agentic.Configuration
Imports Agentic.Logging
Imports Agentic.Utilities

Namespace Work

    ''' <summary>A job of the Images tab as pages see it while it runs (and after).</summary>
    Public NotInheritable Class DiffusionJob
        Public Property Id As String
        Public Property Username As String
        ''' <summary>queued | running | saving | done | failed | cancelled</summary>
        Public Property Status As String
        Public Property Progress As Double?
        Public Property [Step] As Integer?
        Public Property TotalSteps As Integer?
        ''' <summary>Which picture is being made (from 1), when the server says.</summary>
        Public Property ImageIndex As Integer?
        ''' <summary>Its place in line (1 = next), here while InLine, else the server's.</summary>
        Public Property QueuePosition As Integer?
        Public Property NumImages As Integer
        Public Property Prompt As String
        Public Property Mode As String
        Public Property [Error] As String
        Public Property ImageIds As New List(Of String)
        Public Property CostMicros As Long?
        Public Property ElapsedMs As Long?
        Public Property CreatedUtc As DateTime
        ''' <summary>When it was sent to the server (its time limit starts then).</summary>
        Public Property SubmittedUtc As DateTime?
        Public Property StartedUtc As DateTime?
        Public Property FinishedUtc As DateTime?
        ''' <summary>The request without its pictures.</summary>
        Public Property Request As JsonObject

        ''' <summary>About how long the GPU takes for it (ms): the page's progress while the server gives no steps.</summary>
        Public ReadOnly Property EstimatedMs As Long?
            Get
                Return If(Request Is Nothing, CType(Nothing, Long?), DiffusionService.EstimateGpuMs(Request))
            End Get
        End Property

        ''' <summary>The job's id on the server (Nothing while it waits in line here).</summary>
        <JsonIgnore>
        Public Property RemoteId As String
        ' Leaving the line (cancelled, or the host stopping); being sent right now; cancelled while being sent.
        Friend Line As CancellationTokenSource
        Friend Sending As Boolean
        Friend CancelAsked As Boolean

        <JsonIgnore>
        Public ReadOnly Property Finished As Boolean
            Get
                Return Status = "done" OrElse Status = "failed" OrElse Status = "cancelled"
            End Get
        End Property

        ''' <summary>It waits in line here: the server is busy with an earlier job.</summary>
        Public ReadOnly Property InLine As Boolean
            Get
                Return RemoteId Is Nothing AndAlso Not Finished
            End Get
        End Property
    End Class

    ''' <summary>
    ''' Runs the Images tab's jobs. The server makes one job at a time (StableDiffusion.MaxConcurrency), so jobs wait in line
    ''' here, first come first served (their full request kept on disk), and each is sent when the one before it ends. A sent
    ''' job is watched here (not by the page), so its pictures are kept and paid for even when the page was closed; jobs that
    ''' had not ended - sent or still in line - carry on after a restart. When a job is done its pictures go to the gallery and
    ''' the account pays per picture and per GPU minute.
    ''' </summary>
    Public NotInheritable Class DiffusionJobs
        Private Const LineLabel As String = "picture job"
        Private ReadOnly _svc As DiffusionService
        Private ReadOnly _store As DiffusionStore
        Private ReadOnly _accounts As Economy.AccountService
        Private ReadOnly _log As StructuredLogger
        Private ReadOnly _jobs As New ConcurrentDictionary(Of String, DiffusionJob)(StringComparer.Ordinal)
        Private _stop As CancellationToken
        ''' <summary>1 once a running job's raw status was logged (once per run of the host).</summary>
        Private _rawLogged As Integer

        Public Sub New(svc As DiffusionService, store As DiffusionStore, accounts As Economy.AccountService, log As StructuredLogger)
            _svc = svc
            _store = store
            _accounts = accounts
            _log = log
        End Sub

        Private ReadOnly Property Opt As StableDiffusionOptions
            Get
                Return _svc.Options
            End Get
        End Property

        ''' <summary>Starts watching: jobs that had not ended before a restart carry on (those already sent first, then the line in order).</summary>
        Public Sub Start(ct As CancellationToken)
            _stop = ct
            Dim unused = Task.Run(Async Function()
                                      Try
                                          Dim open = (Await _store.OpenJobsAsync().ConfigureAwait(False)).Select(AddressOf FromRecord).ToList()
                                          For Each job In open.OrderBy(Function(j) If(j.RemoteId Is Nothing, 1, 0)).ThenBy(Function(j) j.CreatedUtc)
                                              _jobs(job.Id) = job
                                              Follow(job, Enter(job))
                                          Next
                                      Catch ex As Exception
                                          _log?.Warn("Images", "Could not resume the Stable Diffusion jobs: " & ex.Message)
                                      End Try
                                  End Function)
        End Sub

        Private Shared Function FromRecord(r As SdJobRecord) As DiffusionJob
            Return New DiffusionJob With {.Id = r.Id, .Username = r.Username, .Status = r.Status, .Prompt = r.Prompt, .Request = r.Request, .NumImages = r.NumImages,
                                          .Mode = DiffusionService.ModeOf(r.Request), .ImageIds = r.ImageIds, .Error = r.Error, .ElapsedMs = r.ElapsedMs, .CostMicros = r.CostMicros,
                                          .CreatedUtc = r.CreatedUtc, .FinishedUtc = r.FinishedUtc, .RemoteId = r.RemoteId, .SubmittedUtc = r.SubmittedUtc}
        End Function

        ''' <summary>The user's jobs that have not ended (waiting in line included).</summary>
        Public Function ActiveCount(username As String) As Integer
            Return _jobs.Values.Where(Function(j) j.Username = username AndAlso Not j.Finished).Count()
        End Function

        ''' <summary>Takes the job's place in line (at once, so jobs keep the order they came in).</summary>
        Private Function Enter(job As DiffusionJob) As Task(Of IDisposable)
            job.Line = CancellationTokenSource.CreateLinkedTokenSource(_stop)
            Return _svc.Queue.EnterAsync(job.Username, LineLabel, job.Line.Token, job.Id)
        End Function

        ''' <summary>
        ''' Puts a job in line for the server. When the server is free and nobody waits, it is sent at once (so a request the
        ''' server refuses fails here, and nothing is kept); otherwise it waits its turn and is sent when the job before it ends.
        ''' </summary>
        Public Async Function StartJobAsync(username As String, body As JsonObject, ct As CancellationToken) As Task(Of DiffusionJob)
            Dim n = 1
            Dim nv = TryCast(body("num_images"), JsonValue)
            If nv IsNot Nothing Then nv.TryGetValue(n)
            Dim job As New DiffusionJob With {.Id = Ids.NewId("sdjob"), .Username = username, .Status = "queued", .Prompt = body("prompt").ToString(), .Request = DiffusionService.Summary(body),
                                              .NumImages = Math.Max(1, n), .Mode = DiffusionService.ModeOf(body), .CreatedUtc = DateTime.UtcNow}
            Dim turn = Enter(job)
            Dim record As New SdJobRecord With {.Id = job.Id, .Username = username, .Status = job.Status, .Prompt = job.Prompt, .Request = job.Request, .NumImages = job.NumImages, .CreatedUtc = job.CreatedUtc}
            If turn.IsCompleted Then
                Dim slot = turn.Result
                Dim sent = False
                Try
                    Await SendAsync(job, body, ct).ConfigureAwait(False)
                    record.Status = "running"
                    record.RemoteId = job.RemoteId
                    record.SubmittedUtc = job.SubmittedUtc
                    Await _store.AddJobAsync(record).ConfigureAwait(False)
                    sent = True
                Finally
                    If Not sent Then slot.Dispose()
                End Try
                _jobs(job.Id) = job
                Follow(job, turn)
                Return job
            End If
            Dim kept = False
            Try
                Await _store.SaveWaitingAsync(job.Id, body).ConfigureAwait(False)
                Await _store.AddJobAsync(record).ConfigureAwait(False)
                kept = True
            Finally
                If Not kept Then
                    job.Line.Cancel()
                    _store.DeleteWaiting(job.Id)
                End If
            End Try
            _jobs(job.Id) = job
            Follow(job, turn)
            Return Refresh(job)
        End Function

        ''' <summary>A job waiting in line shows its place (1 = next).</summary>
        Private Function Refresh(job As DiffusionJob) As DiffusionJob
            If job.InLine Then job.QueuePosition = _svc.Queue.PositionOf(job.Id)
            Return job
        End Function

        ''' <summary>A job of this user (Nothing when there is none).</summary>
        Public Async Function GetAsync(username As String, id As String) As Task(Of DiffusionJob)
            Dim job As DiffusionJob = Nothing
            If _jobs.TryGetValue(If(id, ""), job) Then Return If(job.Username = username, Refresh(job), Nothing)
            Dim r = Await _store.JobAsync(id).ConfigureAwait(False)
            Return If(r Is Nothing OrElse r.Username <> username, Nothing, FromRecord(r))
        End Function

        ''' <summary>The user's recent jobs, newest first (running ones as they are now).</summary>
        Public Async Function ListAsync(username As String, limit As Integer) As Task(Of List(Of DiffusionJob))
            Prune()
            Dim list = (Await _store.JobsAsync(username, limit).ConfigureAwait(False)).Select(AddressOf FromRecord).ToList()
            For i = 0 To list.Count - 1
                Dim live As DiffusionJob = Nothing
                If _jobs.TryGetValue(list(i).Id, live) Then list(i) = Refresh(live)
            Next
            Return list
        End Function

        ''' <summary>Jobs that ended more than an hour ago are only kept in the database.</summary>
        Private Sub Prune()
            For Each j In _jobs.Values.Where(Function(x) x.Finished AndAlso x.FinishedUtc.HasValue AndAlso x.FinishedUtc.Value < DateTime.UtcNow.AddHours(-1)).ToList()
                _jobs.TryRemove(j.Id, Nothing)
            Next
        End Sub

        ''' <summary>
        ''' Cancels a job: one still in line leaves it and never reaches the server; a sent one is cancelled on the server and
        ''' the watcher ends it when the server says so.
        ''' </summary>
        Public Async Function CancelAsync(username As String, id As String, ct As CancellationToken) As Task(Of DiffusionJob)
            Dim job = Await GetAsync(username, id).ConfigureAwait(False)
            If job Is Nothing OrElse job.Finished Then Return job
            Dim leaves = False
            SyncLock job
                If job.RemoteId Is Nothing Then
                    If job.Sending Then
                        job.CancelAsked = True
                    Else
                        leaves = True
                        job.Status = "cancelled"
                    End If
                End If
            End SyncLock
            If leaves Then
                job.Line?.Cancel()
                _store.DeleteWaiting(job.Id)
                job.QueuePosition = Nothing
                Await FinishAsync(job, "cancelled", Nothing).ConfigureAwait(False)
                Return job
            End If
            ' Being sent right now: it is cancelled on the server as soon as the server has it.
            If job.RemoteId Is Nothing Then Return job
            Dim gone = False
            Try
                Await _svc.CancelAsync(job.RemoteId, ct).ConfigureAwait(False)
            Catch ex As DiffusionException When ex.Status = 404
                gone = True
            End Try
            ' The server no longer has it: it ends here.
            If gone Then Await FinishAsync(job, "cancelled", Nothing).ConfigureAwait(False)
            Return job
        End Function

        Private Sub Follow(job As DiffusionJob, turn As Task(Of IDisposable))
            Dim unused = Task.Run(Function() RunAsync(job, turn))
        End Sub

        ''' <summary>Waits for the job's turn, sends it if it was waiting, watches it to its end, then frees the server for the next.</summary>
        Private Async Function RunAsync(job As DiffusionJob, turn As Task(Of IDisposable)) As Task
            Dim slot As IDisposable = Nothing
            Try
                slot = Await turn.ConfigureAwait(False)
            Catch ex As OperationCanceledException
                ' It left the line: cancelled (already ended), or the host is stopping (it waits again after the restart).
                Return
            End Try
            Try
                If job.RemoteId Is Nothing AndAlso Not Await SendWaitingAsync(job).ConfigureAwait(False) Then Return
                Await WatchAsync(job).ConfigureAwait(False)
            Catch ex As OperationCanceledException When _stop.IsCancellationRequested
                ' The host is stopping: the job carries on after the restart.
            Catch ex As Exception
                _log?.Warn("Images", $"Stable Diffusion job {job.Id} failed: {ex.Message}")
            Finally
                slot.Dispose()
            End Try
        End Function

        Private Async Function SendAsync(job As DiffusionJob, body As JsonObject, ct As CancellationToken) As Task
            Dim submitted = Await _svc.SubmitAsync(body, ct).ConfigureAwait(False)
            job.RemoteId = submitted.Id
            job.SubmittedUtc = DateTime.UtcNow
            job.QueuePosition = Nothing
            Apply(job, DiffusionService.ReadJob(submitted.Raw))
        End Function

        ''' <summary>A waiting job's turn has come: its request is sent. False when it ended instead (cancelled meanwhile, or refused).</summary>
        Private Async Function SendWaitingAsync(job As DiffusionJob) As Task(Of Boolean)
            SyncLock job
                If job.Finished Then Return False
                job.Sending = True
            End SyncLock
            Dim failure As String = Nothing
            Dim body = Await _store.ReadWaitingAsync(job.Id).ConfigureAwait(False)
            If body Is Nothing Then
                failure = "Its request was lost while it waited in line."
            Else
                Try
                    Await SendAsync(job, body, _stop).ConfigureAwait(False)
                Catch ex As DiffusionException
                    failure = ex.Message
                End Try
            End If
            _store.DeleteWaiting(job.Id)
            If failure IsNot Nothing Then
                Await FinishAsync(job, "failed", failure).ConfigureAwait(False)
                Return False
            End If
            Await _store.SetSentAsync(job.Id, job.RemoteId, job.SubmittedUtc.Value).ConfigureAwait(False)
            Dim cancel As Boolean
            SyncLock job
                job.Sending = False
                cancel = job.CancelAsked
            End SyncLock
            If cancel Then
                Try
                    Await _svc.CancelAsync(job.RemoteId, _stop).ConfigureAwait(False)
                Catch ex As DiffusionException
                End Try
            End If
            Return True
        End Function

        Private Shared Sub Apply(job As DiffusionJob, v As DiffusionJobView)
            If v.Status = "queued" OrElse v.Status = "running" Then job.Status = v.Status
            If v.Status = "running" AndAlso Not job.StartedUtc.HasValue Then job.StartedUtc = DateTime.UtcNow
            If v.Progress.HasValue Then job.Progress = v.Progress
            If v.Step.HasValue Then job.Step = v.Step
            If v.TotalSteps.HasValue Then job.TotalSteps = v.TotalSteps
            If v.ImageIndex.HasValue Then job.ImageIndex = v.ImageIndex
            job.QueuePosition = If(v.Status = "queued", v.QueuePosition, Nothing)
            If v.ElapsedMs.HasValue Then job.ElapsedMs = v.ElapsedMs
        End Sub

        Private Async Function WatchAsync(job As DiffusionJob) As Task
            Dim deadline = If(job.SubmittedUtc, job.CreatedUtc).AddSeconds(Math.Max(60, Opt.JobTimeoutSeconds))
            Dim failures = 0
            Dim view As DiffusionJobView = Nothing
            Dim failure As String = Nothing
            Try
                While True
                    Await Task.Delay(Math.Max(200, Opt.PollMilliseconds), _stop).ConfigureAwait(False)
                    Dim raw As JsonNode = Nothing
                    If DateTime.UtcNow > deadline Then
                        Try
                            Await _svc.CancelAsync(job.RemoteId, _stop).ConfigureAwait(False)
                        Catch ex As DiffusionException
                        End Try
                        Await FinishAsync(job, "failed", $"It took longer than {Opt.JobTimeoutSeconds} s and was cancelled.").ConfigureAwait(False)
                        Return
                    End If
                    Dim lost As String = Nothing
                    Try
                        raw = Await _svc.JobAsync(job.RemoteId, _stop).ConfigureAwait(False)
                        view = DiffusionService.ReadJob(raw)
                        failures = 0
                        ' What the server says while a job runs, once per run of the host (its fields are not documented): read
                        ' from the log when progress does not show.
                        If view.Status = "running" AndAlso Interlocked.Exchange(_rawLogged, 1) = 0 Then
                            _log?.Info("Images", "Stable Diffusion job status while running: " & TextUtil.Truncate(If(raw?.ToJsonString(), "(none)"), 600))
                        End If
                    Catch ex As DiffusionException When ex.Status = 404
                        lost = "The Stable Diffusion server no longer knows this job (it may have restarted)."
                    Catch ex As Exception When TypeOf ex Is DiffusionException OrElse TypeOf ex Is HttpRequestException
                        failures += 1
                        If failures >= 60 Then lost = "The Stable Diffusion server stopped answering: " & ex.Message
                    End Try
                    If lost IsNot Nothing Then
                        Await FinishAsync(job, "failed", lost).ConfigureAwait(False)
                        Return
                    End If
                    If failures > 0 Then Continue While
                    If job.Finished Then Return
                    Apply(job, view)
                    If view.Finished Then Exit While
                End While
                If view.Status = "done" Then
                    Await CollectAsync(job, view).ConfigureAwait(False)
                Else
                    Await FinishAsync(job, view.Status, If(view.Status = "failed", If(view.Error, "The job failed."), Nothing)).ConfigureAwait(False)
                End If
            Catch ex As OperationCanceledException When _stop.IsCancellationRequested
                ' The host is stopping: the job is watched again after the restart.
                Return
            Catch ex As Exception
                _log?.Warn("Images", $"Watching Stable Diffusion job {job.Id} failed: {ex.Message}")
                failure = ex.Message
            End Try
            If failure Is Nothing Then Return
            Try
                Await FinishAsync(job, "failed", "Following the job failed: " & failure).ConfigureAwait(False)
            Catch ex As Exception
                _log?.Warn("Images", $"Could not record the end of Stable Diffusion job {job.Id}: {ex.Message}")
            End Try
        End Function

        Private Async Function FinishAsync(job As DiffusionJob, status As String, [error] As String) As Task
            job.Status = status
            job.Error = [error]
            job.FinishedUtc = DateTime.UtcNow
            Await _store.UpdateJobAsync(New SdJobRecord With {.Id = job.Id, .Status = status, .ImageIds = job.ImageIds, .Error = [error], .ElapsedMs = job.ElapsedMs,
                                                              .CostMicros = job.CostMicros, .FinishedUtc = job.FinishedUtc}).ConfigureAwait(False)
        End Function

        ''' <summary>A done job: its pictures (sent with the job, or fetched one by one) go to the gallery, then it is paid for.</summary>
        Private Async Function CollectAsync(job As DiffusionJob, view As DiffusionJobView) As Task
            job.Status = "saving"
            Dim pictures As New List(Of Byte())
            For Each b64 In view.InlineImages
                Dim png = DiffusionService.DecodeImage(b64)
                If png IsNot Nothing Then pictures.Add(png)
            Next
            If pictures.Count = 0 Then
                Dim count = Math.Max(job.NumImages, view.ImagesReady.GetValueOrDefault())
                ' Pictures are numbered from 0; a server that numbers them from 1 has none at 0.
                Dim first = Await _svc.ImageAsync(job.RemoteId, 0, _stop).ConfigureAwait(False)
                Dim start = 0
                If first.Data Is Nothing Then
                    first = Await _svc.ImageAsync(job.RemoteId, 1, _stop).ConfigureAwait(False)
                    start = 1
                End If
                If first.Data IsNot Nothing Then pictures.Add(first.Data)
                For n = start + 1 To start + count - 1
                    Dim more = Await _svc.ImageAsync(job.RemoteId, n, _stop).ConfigureAwait(False)
                    If more.Data Is Nothing Then Exit For
                    pictures.Add(more.Data)
                Next
            End If
            If pictures.Count = 0 Then
                Await FinishAsync(job, "failed", "The job ended without pictures.").ConfigureAwait(False)
                Return
            End If
            Dim req = job.Request
            Dim baseSeed As Long? = Nothing
            Dim sv = TryCast(req("seed"), JsonValue)
            Dim s As Long
            If sv IsNot Nothing AndAlso sv.TryGetValue(s) Then baseSeed = s
            Dim w = 512, h = 512
            Dim wv = TryCast(req("width"), JsonValue), hv = TryCast(req("height"), JsonValue)
            If wv IsNot Nothing Then wv.TryGetValue(w)
            If hv IsNot Nothing Then hv.TryGetValue(h)
            For i = 0 To pictures.Count - 1
                Dim png = pictures(i)
                Dim info = If(i < view.Infos.Count, view.Infos(i), DiffusionService.PngParameters(png))
                Dim size = DiffusionService.PngSize(png)
                Dim seed = If(i < view.Seeds.Count, CType(view.Seeds(i), Long?), If(DiffusionService.SeedOf(info), If(baseSeed.HasValue, CType(baseSeed.Value + i, Long?), Nothing)))
                Dim img As New SdImage With {.Id = Ids.NewId("sdimg"), .Username = job.Username, .JobId = job.Id, .Idx = i, .Prompt = job.Prompt,
                                             .NegativePrompt = req("negative_prompt")?.ToString(), .Params = req, .Info = info, .Seed = seed,
                                             .Width = If(size.HasValue, size.Value.Width, w), .Height = If(size.HasValue, size.Value.Height, h),
                                             .Model = If(req("model")?.ToString(), If(job.Mode = "inpaint", "sd15-inpaint", "sd15")), .Mode = job.Mode, .CreatedUtc = DateTime.UtcNow}
                Await _store.AddImageAsync(img, png, Opt.MaxImagesPerUser).ConfigureAwait(False)
                job.ImageIds.Add(img.Id)
            Next
            ' Paid per picture and per minute the GPU worked (the server's elapsed time, else the time it ran here).
            Dim gpuMs = If(job.ElapsedMs, CLng((DateTime.UtcNow - If(job.StartedUtc, If(job.SubmittedUtc, job.CreatedUtc))).TotalMilliseconds))
            job.ElapsedMs = gpuMs
            Dim price = Await _store.PricingAsync(Opt).ConfigureAwait(False)
            Dim cost = price.Micros(pictures.Count, gpuMs)
            If cost > 0 AndAlso _accounts IsNot Nothing Then
                Dim model = If(req("model")?.ToString(), "sd15")
                Dim after = Await _accounts.ChargeAsync(job.Username, cost, "Image", model, Nothing, Nothing, Nothing,
                                                        $"Stable Diffusion: {pictures.Count} picture{If(pictures.Count = 1, "", "s")}, {gpuMs / 1000.0:0.0} s of GPU: {TextUtil.Truncate(job.Prompt, 60, "…")}").ConfigureAwait(False)
                If after.HasValue Then job.CostMicros = Await _accounts.WithSurchargeAsync(job.Username, cost).ConfigureAwait(False)
            End If
            job.Progress = 1
            Await FinishAsync(job, "done", Nothing).ConfigureAwait(False)
        End Function
    End Class

End Namespace
