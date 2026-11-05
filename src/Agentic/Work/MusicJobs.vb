Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.Text.Json.Nodes
Imports System.Text.Json.Serialization
Imports System.Threading
Imports Agentic.Configuration
Imports Agentic.Logging
Imports Agentic.Utilities

Namespace Work

    ''' <summary>One of a request's songs while Suno makes it.</summary>
    Public NotInheritable Class MusicJobClip
        Public Property Id As String
        ''' <summary>Suno's word: submitted, queued, streaming, complete, error (or missing: not in the library yet).</summary>
        Public Property Status As String
        Public Property Title As String
        Public Property [Error] As String
    End Class

    ''' <summary>A request of the Music tab as pages see it while it runs (and after).</summary>
    Public NotInheritable Class MusicJob
        Public Property Id As String
        Public Property Username As String
        ''' <summary>making | saving | done | failed</summary>
        Public Property Status As String
        Public Property BatchId As String
        Public Property Clips As New List(Of MusicJobClip)
        Public Property SongIds As New List(Of String)
        Public Property [Error] As String
        Public Property CostMicros As Long?
        Public Property CreatedUtc As DateTime
        Public Property FinishedUtc As DateTime?
        ''' <summary>What was asked for (prompt, tags, title, lyrics, instrumental, model).</summary>
        Public Property Request As JsonObject
        <JsonIgnore>
        Public ReadOnly Property Finished As Boolean
            Get
                Return Status = "done" OrElse Status = "failed"
            End Get
        End Property
    End Class

    ''' <summary>
    ''' Runs the Music tab's requests: each is sent to Suno (two songs) and followed here, not by the page, so its songs are
    ''' kept and paid for even when the page was closed; requests that had not ended are followed again after a restart. When
    ''' Suno has finished a song it goes to the library with its cover, and the account pays per song. Its audio is downloaded
    ''' from Suno the first time it is played (EnsureAudioAsync): Suno scrambles its stream, and each official download uses
    ''' one of the Suno account's downloads, so songs nobody plays use none.
    ''' </summary>
    Public NotInheritable Class MusicJobs
        Private ReadOnly _svc As MusicService
        Private ReadOnly _store As MusicStore
        Private ReadOnly _accounts As Economy.AccountService
        Private ReadOnly _log As StructuredLogger
        Private ReadOnly _jobs As New ConcurrentDictionary(Of String, MusicJob)(StringComparer.Ordinal)
        ' One download per song at a time (a player asks for the audio several times at once).
        Private ReadOnly _downloads As New ConcurrentDictionary(Of String, Lazy(Of Task(Of MusicSong)))(StringComparer.Ordinal)
        Private _stop As CancellationToken

        Public Sub New(svc As MusicService, store As MusicStore, accounts As Economy.AccountService, log As StructuredLogger)
            _svc = svc
            _store = store
            _accounts = accounts
            _log = log
        End Sub

        Private ReadOnly Property Opt As MusicOptions
            Get
                Return _svc.Options
            End Get
        End Property

        ''' <summary>Starts watching: requests that had not ended before a restart are followed again.</summary>
        Public Sub Start(ct As CancellationToken)
            _stop = ct
            Dim unused = Task.Run(Async Function()
                                      Try
                                          Dim dropped = Await _store.DropUnplayableAudioAsync().ConfigureAwait(False)
                                          If dropped > 0 Then _log?.Info("Music", $"{dropped} song(s) had audio no player can play (Suno's scrambled stream): it is downloaded again when they are played.")
                                          For Each r In Await _store.OpenJobsAsync().ConfigureAwait(False)
                                              Dim job = FromRecord(r)
                                              job.Status = "making"
                                              _jobs(job.Id) = job
                                              Watch(job)
                                          Next
                                      Catch ex As Exception
                                          _log?.Warn("Music", "Could not resume the music requests: " & ex.Message)
                                      End Try
                                  End Function)
        End Sub

        ''' <summary>
        ''' The song with its audio: downloaded from Suno (its official download, an MP3) the first time it is asked for, then
        ''' kept. Throws MusicException (402: the Suno account has no downloads left).
        ''' </summary>
        Public Async Function EnsureAudioAsync(song As MusicSong, ct As CancellationToken) As Task(Of MusicSong)
            If song.HasAudio Then Return song
            Dim work = _downloads.GetOrAdd(song.Id, Function(id) New Lazy(Of Task(Of MusicSong))(Function() DownloadAudioAsync(song)))
            Try
                Return Await work.Value.WaitAsync(ct).ConfigureAwait(False)
            Finally
                If work.Value.IsCompleted Then _downloads.TryRemove(New KeyValuePair(Of String, Lazy(Of Task(Of MusicSong)))(song.Id, work))
            End Try
        End Function

        Private Async Function DownloadAudioAsync(song As MusicSong) As Task(Of MusicSong)
            Dim audio = Await _svc.DownloadAsync(song.ClipId, _stop).ConfigureAwait(False)
            Await _store.SetAudioAsync(song, audio.Data, audio.ContentType).ConfigureAwait(False)
            _log?.Info("Music", $"Downloaded ""{song.Title}"" from Suno for {song.Username} ({audio.Data.Length:N0} bytes).")
            Return song
        End Function

        Private Shared Function FromRecord(r As MusicJobRecord) As MusicJob
            Return New MusicJob With {.Id = r.Id, .Username = r.Username, .Status = r.Status, .BatchId = r.BatchId, .Request = r.Request,
                                      .Clips = r.ClipIds.Select(Function(c) New MusicJobClip With {.Id = c, .Status = If(r.Status = "done", "complete", "")}).ToList(),
                                      .SongIds = r.SongIds, .Error = r.Error, .CostMicros = r.CostMicros, .CreatedUtc = r.CreatedUtc, .FinishedUtc = r.FinishedUtc}
        End Function

        Public Function ActiveCount(username As String) As Integer
            Return _jobs.Values.Where(Function(j) j.Username = username AndAlso Not j.Finished).Count()
        End Function

        ''' <summary>Sends the request to Suno and starts watching its songs.</summary>
        Public Async Function StartJobAsync(username As String, body As JsonObject, ct As CancellationToken) As Task(Of MusicJob)
            Dim made = Await _svc.GenerateAsync(body, ct).ConfigureAwait(False)
            Dim job As New MusicJob With {.Id = Ids.NewId("music"), .Username = username, .Status = "making", .BatchId = made.BatchId, .Request = body,
                                          .Clips = made.ClipIds.Select(Function(c) New MusicJobClip With {.Id = c, .Status = "submitted"}).ToList(), .CreatedUtc = DateTime.UtcNow}
            _jobs(job.Id) = job
            Await _store.AddJobAsync(New MusicJobRecord With {.Id = job.Id, .Username = username, .Status = job.Status, .BatchId = job.BatchId, .Request = body,
                                                              .ClipIds = made.ClipIds, .CreatedUtc = job.CreatedUtc}).ConfigureAwait(False)
            Watch(job)
            Return job
        End Function

        Public Async Function GetAsync(username As String, id As String) As Task(Of MusicJob)
            Dim job As MusicJob = Nothing
            If _jobs.TryGetValue(If(id, ""), job) Then Return If(job.Username = username, job, Nothing)
            Dim r = Await _store.JobAsync(id).ConfigureAwait(False)
            Return If(r Is Nothing OrElse r.Username <> username, Nothing, FromRecord(r))
        End Function

        Public Async Function ListAsync(username As String, limit As Integer) As Task(Of List(Of MusicJob))
            For Each j In _jobs.Values.Where(Function(x) x.Finished AndAlso x.FinishedUtc.HasValue AndAlso x.FinishedUtc.Value < DateTime.UtcNow.AddHours(-1)).ToList()
                _jobs.TryRemove(j.Id, Nothing)
            Next
            Dim list = (Await _store.JobsAsync(username, limit).ConfigureAwait(False)).Select(AddressOf FromRecord).ToList()
            For i = 0 To list.Count - 1
                Dim live As MusicJob = Nothing
                If _jobs.TryGetValue(list(i).Id, live) Then list(i) = live
            Next
            Return list
        End Function

        Private Sub Watch(job As MusicJob)
            Dim unused = Task.Run(Function() WatchAsync(job))
        End Sub

        Private Async Function WatchAsync(job As MusicJob) As Task
            Dim deadline = job.CreatedUtc.AddSeconds(Math.Max(60, Opt.JobTimeoutSeconds))
            Dim failures = 0
            Dim views As New Dictionary(Of String, JsonObject)(StringComparer.Ordinal)
            Dim failure As String = Nothing
            Try
                While True
                    Await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, Opt.PollSeconds)), _stop).ConfigureAwait(False)
                    Dim timedOut = DateTime.UtcNow > deadline
                    Try
                        For Each v In Await _svc.ClipsAsync(job.Clips.Select(Function(c) c.Id), _stop).ConfigureAwait(False)
                            Dim id = v("id")?.ToString()
                            If id Is Nothing Then Continue For
                            views(id) = v
                            Dim clip = job.Clips.FirstOrDefault(Function(c) c.Id = id)
                            If clip Is Nothing Then Continue For
                            clip.Status = v("status")?.ToString()
                            clip.Title = If(v("title")?.ToString(), clip.Title)
                            clip.Error = v("error")?.ToString()
                        Next
                        failures = 0
                    Catch ex As MusicException When Not timedOut
                        failures += 1
                        If failures >= 60 Then failure = "The Suno wrapper stopped answering: " & ex.Message
                    End Try
                    If failure IsNot Nothing Then Exit While
                    Dim allDone = job.Clips.All(Function(c) MusicService.Stage(c.Status) <> "pending")
                    If allDone OrElse timedOut Then Exit While
                End While
            Catch ex As OperationCanceledException When _stop.IsCancellationRequested
                ' The host is stopping: the request is followed again after the restart.
                Return
            Catch ex As Exception
                _log?.Warn("Music", $"Following music request {job.Id} failed: {ex.Message}")
                failure = "Following the request failed: " & ex.Message
            End Try
            Try
                If failure IsNot Nothing Then
                    Await FinishAsync(job, "failed", failure).ConfigureAwait(False)
                Else
                    Await CollectAsync(job, views).ConfigureAwait(False)
                End If
            Catch ex As Exception
                _log?.Warn("Music", $"Keeping the songs of music request {job.Id} failed: {ex.Message}")
                Try
                    job.Status = "failed"
                    job.Error = "Keeping the songs failed: " & ex.Message
                    job.FinishedUtc = DateTime.UtcNow
                Catch inner As Exception
                End Try
            End Try
        End Function

        Private Async Function FinishAsync(job As MusicJob, status As String, [error] As String) As Task
            job.Status = status
            job.Error = [error]
            job.FinishedUtc = DateTime.UtcNow
            Await _store.UpdateJobAsync(New MusicJobRecord With {.Id = job.Id, .Status = status, .SongIds = job.SongIds, .Error = [error], .CostMicros = job.CostMicros,
                                                                 .FinishedUtc = job.FinishedUtc}).ConfigureAwait(False)
        End Function

        ''' <summary>Suno has finished: each complete song's audio and cover go to the library, then the account pays per song.</summary>
        Private Async Function CollectAsync(job As MusicJob, views As Dictionary(Of String, JsonObject)) As Task
            job.Status = "saving"
            Dim errors As New List(Of String)
            For Each clip In job.Clips
                If MusicService.Stage(clip.Status) <> "done" Then
                    errors.Add(If(clip.Error, If(MusicService.Stage(clip.Status) = "pending", "Suno did not finish a song in time.", "Suno could not make a song.")))
                    Continue For
                End If
                Dim view As JsonObject = Nothing
                views.TryGetValue(clip.Id, view)
                Dim song = Await KeepAsync(job.Username, job.Id, clip.Id, view, job.Request).ConfigureAwait(False)
                If song Is Nothing Then
                    errors.Add("A finished song's audio could not be downloaded.")
                Else
                    job.SongIds.Add(song.Id)
                End If
            Next
            If job.SongIds.Count = 0 Then
                Await FinishAsync(job, "failed", If(errors.FirstOrDefault(), "Suno made no songs.")).ConfigureAwait(False)
                Return
            End If
            Dim price = Await _store.PricingAsync(Opt).ConfigureAwait(False)
            Dim cost = price.SongsMicros(job.SongIds.Count)
            If cost > 0 AndAlso _accounts IsNot Nothing Then
                Dim title = If(job.Clips.FirstOrDefault()?.Title, If(job.Request("title")?.ToString(), job.Request("prompt")?.ToString()))
                Dim after = Await _accounts.ChargeAsync(job.Username, cost, Economy.AccountService.EntryMusic, If(job.Request("model")?.ToString(), "suno"), Nothing, Nothing, Nothing,
                                                        $"Suno: {job.SongIds.Count} song{If(job.SongIds.Count = 1, "", "s")}: {TextUtil.Truncate(title, 60, "…")}").ConfigureAwait(False)
                If after.HasValue Then job.CostMicros = Await _accounts.WithSurchargeAsync(job.Username, cost).ConfigureAwait(False)
            End If
            Await FinishAsync(job, "done", If(errors.Count > 0, String.Join(" ", errors.Distinct()), Nothing)).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' Downloads a finished song (a few tries: the audio can lag behind the status) and its cover, and keeps it for the user.
        ''' Nothing when its audio could not be had.
        ''' </summary>
        Public Async Function KeepAsync(username As String, jobId As String, clipId As String, view As JsonObject, request As JsonObject) As Task(Of MusicSong)
            ' Its audio is downloaded when it is first played (EnsureAudioAsync).
            Dim cover =Await MusicService.CoverAsync(If(view?("image_large_url")?.ToString(), view?("image_url")?.ToString()), _stop).ConfigureAwait(False)
            Dim instrumental = False
            Dim iv = TryCast(If(view?("instrumental"), request?("instrumental")), JsonValue)
            If iv IsNot Nothing Then iv.TryGetValue(instrumental)
            Dim song As New MusicSong With {
                .Id = Ids.NewId("song"), .Username = username, .JobId = jobId, .ClipId = clipId,
                .Title = If(NonEmpty(view?("title")?.ToString()), If(NonEmpty(request?("title")?.ToString()), "Untitled")),
                .Tags = If(NonEmpty(view?("tags")?.ToString()), request?("tags")?.ToString()),
                .Lyrics = If(NonEmpty(view?("lyrics")?.ToString()), request?("lyrics")?.ToString()),
                .Description = If(NonEmpty(request?("prompt")?.ToString()), view?("description")?.ToString()), .Instrumental = instrumental,
                .Model = If(NonEmpty(view?("model")?.ToString()), request?("model")?.ToString()),
                .DurationMs = CLng(MusicService.SecondsOf(view) * 1000),
                .Mime = "audio/mpeg", .CreatedUtc = DateTime.UtcNow}
            Await _store.AddSongAsync(song, Nothing, cover, Opt.MaxSongsPerUser).ConfigureAwait(False)
            song.HasCover = cover IsNot Nothing
            Return song
        End Function

        Private Shared Function NonEmpty(s As String) As String
            Return If(String.IsNullOrWhiteSpace(s), Nothing, s)
        End Function
    End Class

End Namespace
