Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.Utilities

Namespace Work

    ''' <summary>
    ''' The Images and Music tabs in chat: stable_diffusion makes pictures with the Stable Diffusion server (waiting its turn in
    ''' the same line as the Images tab; the pictures also go to the gallery) and make_song makes two songs with Suno (they also
    ''' go to the Music library). Each is turned on per chat ("SD" and "Music" in the composer), like Money or News, and each
    ''' account pays as it does in the tab.
    ''' </summary>
    Partial Public NotInheritable Class ChatService

        Public Const DiffusionTool As String = "stable_diffusion"
        Public Const SongTool As String = "make_song"
        ''' <summary>How often a running picture or song job is looked at.</summary>
        Friend Shared MediaPollInterval As TimeSpan = TimeSpan.FromSeconds(1.5)

        ''' <summary>The Images tab's server, line and gallery (set by the host).</summary>
        Public Property Diffusion As DiffusionService
        Public Property DiffusionJobs As DiffusionJobs
        Public Property DiffusionStore As DiffusionStore
        ''' <summary>The Music tab's Suno wrapper, jobs and library (set by the host).</summary>
        Public Property Music As MusicService
        Public Property MusicJobs As MusicJobs
        Public Property MusicStore As MusicStore

        Public ReadOnly Property DiffusionAvailable As Boolean
            Get
                Return Diffusion IsNot Nothing AndAlso Diffusion.Configured AndAlso DiffusionJobs IsNot Nothing AndAlso DiffusionStore IsNot Nothing
            End Get
        End Property

        Public ReadOnly Property MusicAvailable As Boolean
            Get
                Return Music IsNot Nothing AndAlso Music.Configured AndAlso MusicJobs IsNot Nothing AndAlso MusicStore IsNot Nothing
            End Get
        End Property

        Private Function DiffusionBinding() As ToolBinding
            Return New ToolBinding With {.Name = DiffusionTool, .Server = "sd", .Tool = DiffusionTool, .Risk = "read", .Definition = FunctionDef(DiffusionTool,
                "Make pictures with the local Stable Diffusion 1.5 server and show them to the user (they also go to the user's Images gallery). " &
                "Write the prompt the way SD 1.5 likes it: comma-separated subject, details, style, lighting and quality words (e.g. 'a red fox in snow, " &
                "detailed fur, golden hour, photorealistic, 85mm'); put what to avoid in negative_prompt. 512x512 is its native size (up to 768 on a side works). " &
                "To change a picture from this chat, give reference_image and how strongly to change it (strength). Pictures wait their turn when the server is busy. " &
                "Call it once per request; use num_images for variations.",
                "{""type"":""object"",""properties"":{""prompt"":{""type"":""string"",""description"":""What to draw: comma-separated subject, details, style, lighting""}," &
                """negative_prompt"":{""type"":""string"",""description"":""What to keep out (e.g. 'blurry, low quality, extra fingers, text')""}," &
                """width"":{""type"":""integer"",""minimum"":256,""maximum"":1024,""description"":""Default 512""}," &
                """height"":{""type"":""integer"",""minimum"":256,""maximum"":1024,""description"":""Default 512""}," &
                """num_images"":{""type"":""integer"",""minimum"":1,""maximum"":4,""description"":""How many pictures (default 1)""}," &
                """steps"":{""type"":""integer"",""minimum"":10,""maximum"":60,""description"":""Sampling steps (default: the server's)""}," &
                """guidance_scale"":{""type"":""number"",""minimum"":1,""maximum"":20,""description"":""How closely to follow the prompt (default about 7)""}," &
                """seed"":{""type"":""integer"",""description"":""A seed to repeat a picture (default random)""}," &
                """reference_image"":{""type"":""string"",""description"":""Optional: a picture from this chat to change (its file name, or \""latest\"")""}," &
                """strength"":{""type"":""number"",""minimum"":0.05,""maximum"":1,""description"":""With reference_image: how much to change it (0.3 a little, 0.75 a lot; default 0.6)""}},""required"":[""prompt""]}")}
        End Function

        Private Function SongBinding() As ToolBinding
            Return New ToolBinding With {.Name = SongTool, .Server = "music", .Tool = SongTool, .Risk = "change", .Definition = FunctionDef(SongTool,
                "Make a song with Suno and show it to the user (Suno makes two versions; both also go to the user's Music library). It uses the server's paid " &
                "Suno credits: only call it when the user asked for a song in this conversation, once per song they want. Either describe the song (prompt) and " &
                "let Suno write the words, or give your own lyrics with a style (tags) and a title. Making it takes one to three minutes.",
                "{""type"":""object"",""properties"":{""prompt"":{""type"":""string"",""description"":""A description of the song (genre, mood, subject); Suno writes the lyrics""}," &
                """lyrics"":{""type"":""string"",""description"":""Your own lyrics, with [Verse] / [Chorus] tags (instead of prompt)""}," &
                """tags"":{""type"":""string"",""description"":""The style, e.g. 'upbeat synth-pop, female vocals, 120 bpm'""}," &
                """title"":{""type"":""string""},""instrumental"":{""type"":""boolean"",""description"":""No vocals""}}}")}
        End Function

        ''' <summary>A picture of this chat as base64 for img2img (Nothing with the reason when it cannot be used).</summary>
        Private Async Function ChatPictureBase64Async(chat As ChatConversation, answer As ChatMessageRecord, wanted As String) As Task(Of (Base64 As String, Name As String, Problem As String))
            Dim pictures = ChatPictures(chat, answer)
            If pictures.Count = 0 Then Return (Nothing, Nothing, "There is no picture in this chat to change. Leave reference_image out to draw a new picture.")
            Dim w = If(wanted, "").Trim()
            Dim found = If(LatestPictureWords.Contains(w), pictures.Last(),
                           pictures.LastOrDefault(Function(p) String.Equals(p.Ref.Name, w, StringComparison.OrdinalIgnoreCase) OrElse String.Equals(p.Ref.UploadId, w, StringComparison.Ordinal)))
            If found.Ref Is Nothing Then Return (Nothing, Nothing, $"There is no picture called '{w}' in this chat (or use ""latest"").")
            Dim file = Await _uploads.ReadAsync(found.Ref.UploadId).ConfigureAwait(False)
            If file.Info Is Nothing OrElse file.Data Is Nothing Then Return (Nothing, Nothing, $"The picture '{found.Ref.Name}' is no longer available.")
            Return (Convert.ToBase64String(file.Data), found.Ref.Name, Nothing)
        End Function

        ''' <summary>Whether a charged account can pay about this much (surcharge included); the reason when not.</summary>
        Private Async Function BalanceProblemAsync(username As String, isAdmin As Boolean, micros As Long, what As String) As Task(Of String)
            If micros <= 0 OrElse isAdmin OrElse Accounts Is Nothing Then Return Nothing
            Dim acc = Await Accounts.GetAccountAsync(username).ConfigureAwait(False)
            If acc Is Nothing OrElse Not acc.Charged Then Return Nothing
            Dim needed = Await Accounts.WithSurchargeAsync(username, micros).ConfigureAwait(False)
            If acc.BalanceMicros >= needed Then Return Nothing
            Return $"The user's balance ({TextUtil.FormatMicros(acc.BalanceMicros)}) does not cover {what} (about {TextUtil.FormatMicros(needed)}). Tell them to ask an administrator to add money."
        End Function

        ''' <summary>stable_diffusion: queues the job like the Images tab, follows it, and shows its pictures with the answer.</summary>
        Private Async Function RunDiffusionToolAsync(username As String, isAdmin As Boolean, chat As ChatConversation, argsJson As String, answer As ChatMessageRecord,
                                                     emit As Func(Of String, Object, Task), ct As CancellationToken) As Task(Of (Ok As Boolean, Text As String))
            If Not DiffusionAvailable Then Return (False, "Stable Diffusion is not set up on this server.")
            Dim args As JsonElement
            If Not JsonUtil.TryParseElement(If(String.IsNullOrWhiteSpace(argsJson), "{}", argsJson), args) Then Return (False, "The arguments are not valid JSON.")
            Dim opt = Diffusion.Options
            If DiffusionJobs.ActiveCount(username) >= Math.Max(1, opt.MaxActiveJobsPerUser) Then
                Return (False, $"The user already has {opt.MaxActiveJobsPerUser} picture jobs queued or running in the Images tab: wait for one to end.")
            End If
            Dim input As New JsonObject From {{"prompt", JsonUtil.GetString(args, "prompt")}, {"negative_prompt", JsonUtil.GetString(args, "negative_prompt")},
                                              {"width", JsonUtil.GetInt(args, "width", 512)}, {"height", JsonUtil.GetInt(args, "height", 512)},
                                              {"num_images", Math.Clamp(JsonUtil.GetInt(args, "num_images", 1), 1, 4)}}
            Dim steps = JsonUtil.GetInt(args, "steps", 0)
            If steps > 0 Then input("steps") = Math.Clamp(steps, 10, 60)
            Dim el As JsonElement
            If args.TryGetProperty("guidance_scale", el) AndAlso el.ValueKind = JsonValueKind.Number Then input("guidance_scale") = Math.Clamp(el.GetDouble(), 1, 20)
            If args.TryGetProperty("seed", el) AndAlso el.ValueKind = JsonValueKind.Number Then input("seed") = el.GetInt64()
            Dim fromName As String = Nothing
            Dim reference = JsonUtil.GetString(args, "reference_image")
            If Not String.IsNullOrWhiteSpace(reference) Then
                Dim pic = Await ChatPictureBase64Async(chat, answer, reference).ConfigureAwait(False)
                If pic.Problem IsNot Nothing Then Return (False, pic.Problem)
                input("init_image") = pic.Base64
                input("strength") = If(args.TryGetProperty("strength", el) AndAlso el.ValueKind = JsonValueKind.Number, Math.Clamp(el.GetDouble(), 0.05, 1), 0.6)
                fromName = pic.Name
            End If
            Dim body As JsonObject
            Try
                body = DiffusionService.BuildRequest(JsonDocument.Parse(input.ToJsonString()).RootElement, Function(v) v)
            Catch ex As DiffusionException
                Return (False, "Stable Diffusion did not take these settings: " & ex.Message)
            End Try
            Dim count = CInt(body("num_images")?.GetValue(Of Integer)())
            Dim price = Await DiffusionStore.PricingAsync(opt).ConfigureAwait(False)
            Dim problem = Await BalanceProblemAsync(username, isAdmin, price.Micros(count, DiffusionService.EstimateGpuMs(body)), If(count = 1, "this picture", $"these {count} pictures")).ConfigureAwait(False)
            If problem IsNot Nothing Then Return (False, problem)
            Dim job As DiffusionJob
            Try
                job = Await DiffusionJobs.StartJobAsync(username, body, ct).ConfigureAwait(False)
            Catch ex As DiffusionException
                Return (False, "Stable Diffusion refused the job: " & ex.Message)
            End Try
            Dim limit = DateTime.UtcNow.AddSeconds(Math.Max(60, opt.JobTimeoutSeconds))
            Dim said = ""
            While Not job.Finished
                Dim note = If(job.InLine, $"Stable Diffusion: waiting in line{If(job.QueuePosition.HasValue, $" (#{job.QueuePosition.Value})", "")}…",
                              $"Stable Diffusion: drawing{If(job.Progress.HasValue, $" {Math.Round(job.Progress.Value * 100)}%", "")}…")
                If note <> said Then
                    said = note
                    Await emit("status", New With {.text = note}).ConfigureAwait(False)
                End If
                If DateTime.UtcNow > limit Then Return (False, "The picture job is still not done; it carries on in the Images tab, where the user will find the pictures.")
                Try
                    Await Task.Delay(MediaPollInterval, ct).ConfigureAwait(False)
                Catch ex As OperationCanceledException
                    ' The answer was stopped: the job leaves the line (or is cancelled on the server).
                    Dim unused = DiffusionJobs.CancelAsync(username, job.Id, CancellationToken.None)
                    Throw
                End Try
                job = If(Await DiffusionJobs.GetAsync(username, job.Id).ConfigureAwait(False), job)
            End While
            If job.Status <> "done" OrElse job.ImageIds.Count = 0 Then Return (False, $"Stable Diffusion did not make the picture ({job.Status}): {If(job.Error, "no picture came back")}.")
            Dim shown = 0
            For Each id In job.ImageIds
                Dim png = Await DiffusionStore.ReadImageAsync(id).ConfigureAwait(False)
                If png Is Nothing Then Continue For
                shown += 1
                Dim u = Await _uploads.SaveAsync($"sd-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{shown}.png", png, username).ConfigureAwait(False)
                Dim att As New ChatAttachmentRef With {.UploadId = u.Id, .Name = u.Name, .Kind = "image", .SizeBytes = u.SizeBytes,
                                                       .Summary = TextUtil.Truncate(job.Prompt, 240, "…") & " · Stable Diffusion" & If(fromName Is Nothing, "", " · from " & fromName)}
                answer.Attachments.Add(att)
                Await emit("image", New With {.messageId = answer.Id, .attachment = att}).ConfigureAwait(False)
            Next
            If job.CostMicros.GetValueOrDefault() > 0 AndAlso Not isAdmin Then answer.CostMicros = If(answer.CostMicros, 0L) + job.CostMicros.Value
            Return (True, $"Stable Diffusion made {shown} picture{If(shown = 1, "", "s")}{If(fromName Is Nothing, "", " from " & fromName)}; they are shown to the user with your answer " &
                          "and are in their Images gallery. Do not add links or Markdown images for them; describe them briefly if useful.")
        End Function

        ''' <summary>make_song: asks Suno for the song like the Music tab, follows it, and shows both versions with the answer.</summary>
        Private Async Function RunSongToolAsync(username As String, isAdmin As Boolean, argsJson As String, answer As ChatMessageRecord,
                                                emit As Func(Of String, Object, Task), ct As CancellationToken) As Task(Of (Ok As Boolean, Text As String))
            If Not MusicAvailable Then Return (False, "Suno is not set up on this server.")
            Dim args As JsonElement
            If Not JsonUtil.TryParseElement(If(String.IsNullOrWhiteSpace(argsJson), "{}", argsJson), args) Then Return (False, "The arguments are not valid JSON.")
            Dim opt = Music.Options
            If MusicJobs.ActiveCount(username) >= Math.Max(1, opt.MaxActiveJobsPerUser) Then
                Return (False, $"The user already has {opt.MaxActiveJobsPerUser} songs being made: wait for one to finish.")
            End If
            Dim body As JsonObject
            Try
                body = MusicService.BuildRequest(args, opt.DefaultModel)
            Catch ex As MusicException
                Return (False, ex.Message)
            End Try
            Dim price = Await MusicStore.PricingAsync(opt).ConfigureAwait(False)
            Dim problem = Await BalanceProblemAsync(username, isAdmin, price.SongsMicros(2), "two songs").ConfigureAwait(False)
            If problem IsNot Nothing Then Return (False, problem)
            Dim job As MusicJob
            Try
                job = Await MusicJobs.StartJobAsync(username, body, ct).ConfigureAwait(False)
            Catch ex As MusicException
                Return (False, "Suno refused the song: " & ex.Message)
            End Try
            Await emit("status", New With {.text = "Suno is making the song…"}).ConfigureAwait(False)
            Dim limit = DateTime.UtcNow.AddSeconds(Math.Max(60, opt.JobTimeoutSeconds))
            While Not job.Finished
                ' A stopped answer leaves the song to finish: it still lands in the Music library.
                Await Task.Delay(MediaPollInterval, ct).ConfigureAwait(False)
                If DateTime.UtcNow > limit Then Return (False, "The song is still not done; it carries on in the Music tab, where the user will find it.")
                job = If(Await MusicJobs.GetAsync(username, job.Id).ConfigureAwait(False), job)
            End While
            Dim songs = Await MusicStore.SongsAsync(username, Nothing, False, job.Id, 10, Nothing).ConfigureAwait(False)
            If songs.Count = 0 Then Return (False, $"Suno did not make the song ({job.Status}): {If(job.Error, "no song came back")}.")
            For Each s In songs.OrderBy(Function(x) x.CreatedUtc)
                Dim att As New ChatAttachmentRef With {.UploadId = s.Id, .Name = If(String.IsNullOrWhiteSpace(s.Title), "Song", s.Title), .Kind = "song", .SizeBytes = s.Bytes,
                                                       .Summary = TextUtil.Truncate(String.Join(" · ", {s.Tags, If(s.DurationMs > 0, $"{s.DurationMs \ 60000}:{(s.DurationMs \ 1000) Mod 60:00}", Nothing)}.Where(Function(x) Not String.IsNullOrWhiteSpace(x))), 240, "…")}
                answer.Attachments.Add(att)
                Await emit("song", New With {.messageId = answer.Id, .attachment = att}).ConfigureAwait(False)
            Next
            If job.CostMicros.GetValueOrDefault() > 0 AndAlso Not isAdmin Then answer.CostMicros = If(answer.CostMicros, 0L) + job.CostMicros.Value
            Return (True, $"Suno made {songs.Count} version{If(songs.Count = 1, "", "s")} of the song (""{songs(0).Title}""); they are shown to the user with players and are in their " &
                          "Music library. Do not add links; say briefly what you asked Suno for.")
        End Function
    End Class

End Namespace
