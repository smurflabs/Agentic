Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports Agentic.Persistence
Imports Agentic.Utilities

Namespace Work

    ''' <summary>A picture the Images tab made and kept (a PNG in data\images).</summary>
    Public NotInheritable Class SdImage
        Public Property Id As String
        Public Property Username As String
        Public Property JobId As String
        ''' <summary>Its place in the job (from 0).</summary>
        Public Property Idx As Integer
        Public Property Prompt As String
        Public Property NegativePrompt As String
        ''' <summary>The job's request without its pictures (init_image etc. are true when there was one).</summary>
        Public Property Params As JsonObject
        ''' <summary>The PNG's A1111 "parameters" text.</summary>
        Public Property Info As String
        Public Property Seed As Long?
        Public Property Width As Integer
        Public Property Height As Integer
        Public Property Model As String
        ''' <summary>txt2img | img2img | inpaint</summary>
        Public Property Mode As String
        Public Property Bytes As Long
        Public Property Starred As Boolean
        Public Property CreatedUtc As DateTime
    End Class

    ''' <summary>A job of the Images tab: what it asked for and how it ended.</summary>
    Public NotInheritable Class SdJobRecord
        Public Property Id As String
        Public Property Username As String
        Public Property Status As String
        Public Property Prompt As String
        Public Property Request As JsonObject
        Public Property NumImages As Integer
        Public Property ImageIds As New List(Of String)
        Public Property [Error] As String
        Public Property ElapsedMs As Long?
        Public Property CostMicros As Long?
        Public Property CreatedUtc As DateTime
        Public Property FinishedUtc As DateTime?
        ''' <summary>The job's id on the server (Nothing while it waits in line here).</summary>
        Public Property RemoteId As String
        Public Property SubmittedUtc As DateTime?
    End Class

    ''' <summary>What pictures cost a User account (USD): per picture, and per minute the GPU worked. Nothing = free.</summary>
    Public NotInheritable Class SdPrice
        Public Property PerImage As Decimal?
        Public Property PerGpuMinute As Decimal?
        Public Property Source As String
        Public Property EffectiveFromUtc As DateTime?

        ''' <summary>pictures × (USD per picture) + minutes × (USD per GPU minute), in micro-USD, rounded up.</summary>
        Public Function Micros(images As Integer, gpuMs As Long) As Long
            Dim cost = CDec(Math.Max(0, images)) * PerImage.GetValueOrDefault() * 1000000D + CDec(Math.Max(0L, gpuMs)) / 60000D * PerGpuMinute.GetValueOrDefault() * 1000000D
            Return CLng(Math.Ceiling(cost))
        End Function

        Public ReadOnly Property IsFree As Boolean
            Get
                Return PerImage.GetValueOrDefault() = 0 AndAlso PerGpuMinute.GetValueOrDefault() = 0
            End Get
        End Property
    End Class

    ''' <summary>The Images tab's jobs, pictures (data\images) and prices.</summary>
    Public NotInheritable Class DiffusionStore
        Private Shared ReadOnly UnsafeId As New Regex("[^A-Za-z0-9_-]", RegexOptions.Compiled)
        Private ReadOnly _db As Database
        Private ReadOnly _root As String

        Public Sub New(db As Database, rootDirectory As String)
            _db = db
            _root = Path.GetFullPath(rootDirectory)
        End Sub

        Private Function FileOf(id As String) As String
            Directory.CreateDirectory(_root)
            Return Path.Combine(_root, UnsafeId.Replace(If(id, ""), "") & ".png")
        End Function

        Private Shared Function ParseObject(json As String) As JsonObject
            If String.IsNullOrWhiteSpace(json) Then Return Nothing
            Try
                Return TryCast(JsonNode.Parse(json), JsonObject)
            Catch ex As Text.Json.JsonException
                Return Nothing
            End Try
        End Function

        ' ------------------------------------------------------------------ jobs

        Private Shared Function MapJob(r As Microsoft.Data.Sqlite.SqliteDataReader) As SdJobRecord
            Return New SdJobRecord With {.Id = Db.Str(r, "Id"), .Username = Db.Str(r, "Username"), .Status = Db.Str(r, "Status"), .Prompt = Db.Str(r, "Prompt"),
                                         .Request = If(ParseObject(Db.Str(r, "RequestJson")), New JsonObject()), .NumImages = CInt(Db.Lng(r, "NumImages")),
                                         .ImageIds = If(JsonUtil.FromJson(Of List(Of String))(Db.Str(r, "ImageIdsJson")), New List(Of String)), .Error = Db.Str(r, "Error"),
                                         .ElapsedMs = Db.LngN(r, "ElapsedMs"), .CostMicros = Db.LngN(r, "CostMicros"), .CreatedUtc = Db.Dt(r, "CreatedUtc"), .FinishedUtc = Db.DtN(r, "FinishedUtc"),
                                         .RemoteId = Db.Str(r, "RemoteId"), .SubmittedUtc = Db.DtN(r, "SubmittedUtc")}
        End Function

        ''' <summary>The job was sent to the server, which knows it as remoteId.</summary>
        Public Function SetSentAsync(id As String, remoteId As String, sentUtc As DateTime) As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE SdJobs SET RemoteId=@r, SubmittedUtc=@t, Status='running' WHERE Id=@id", ("@r", CObj(remoteId)), ("@t", Db.Ts(sentUtc)), ("@id", id))
        End Function

        ' A job waiting in line keeps its full request (pictures included) here until it is sent.
        Private Function WaitingFileOf(id As String) As String
            Dim dir = Path.Combine(_root, "queue")
            Directory.CreateDirectory(dir)
            Return Path.Combine(dir, UnsafeId.Replace(If(id, ""), "") & ".json")
        End Function

        Public Function SaveWaitingAsync(id As String, body As JsonObject) As Task
            Return File.WriteAllTextAsync(WaitingFileOf(id), body.ToJsonString())
        End Function

        ''' <summary>The full request of a job waiting in line (Nothing when it is gone).</summary>
        Public Async Function ReadWaitingAsync(id As String) As Task(Of JsonObject)
            Dim f = WaitingFileOf(id)
            If Not File.Exists(f) Then Return Nothing
            Return ParseObject(Await File.ReadAllTextAsync(f).ConfigureAwait(False))
        End Function

        Public Sub DeleteWaiting(id As String)
            Try
                File.Delete(WaitingFileOf(id))
            Catch ex As IOException
            End Try
        End Sub

        Public Function AddJobAsync(j As SdJobRecord) As Task(Of Integer)
            Return _db.ExecuteAsync("INSERT INTO SdJobs(Id, Username, Status, Prompt, RequestJson, NumImages, CreatedUtc, RemoteId, SubmittedUtc) VALUES (@id, @u, @s, @p, @r, @n, @c, @rid, @sent)",
                                    ("@id", CObj(j.Id)), ("@u", j.Username), ("@s", j.Status), ("@p", j.Prompt), ("@r", j.Request.ToJsonString()), ("@n", j.NumImages), ("@c", Db.Ts(j.CreatedUtc)),
                                    ("@rid", j.RemoteId), ("@sent", If(j.SubmittedUtc.HasValue, CObj(Db.Ts(j.SubmittedUtc.Value)), Nothing)))
        End Function

        Public Function UpdateJobAsync(j As SdJobRecord) As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE SdJobs SET Status=@s, ImageIdsJson=@i, Error=@e, ElapsedMs=@el, CostMicros=@cost, FinishedUtc=@f WHERE Id=@id",
                                    ("@s", CObj(j.Status)), ("@i", JsonUtil.ToJson(j.ImageIds)), ("@e", j.Error), ("@el", If(j.ElapsedMs.HasValue, CObj(j.ElapsedMs.Value), Nothing)),
                                    ("@cost", If(j.CostMicros.HasValue, CObj(j.CostMicros.Value), Nothing)), ("@f", If(j.FinishedUtc.HasValue, CObj(Db.Ts(j.FinishedUtc.Value)), Nothing)), ("@id", j.Id))
        End Function

        Public Function JobAsync(id As String) As Task(Of SdJobRecord)
            Return _db.QuerySingleAsync("SELECT * FROM SdJobs WHERE Id=@id", AddressOf MapJob, ("@id", CObj(id)))
        End Function

        Public Function JobsAsync(username As String, limit As Integer) As Task(Of List(Of SdJobRecord))
            Return _db.QueryAsync("SELECT * FROM SdJobs WHERE Username=@u ORDER BY CreatedUtc DESC LIMIT @l", AddressOf MapJob, ("@u", CObj(username)), ("@l", Math.Clamp(limit, 1, 200)))
        End Function

        ''' <summary>Jobs that had not ended (watched again after a restart).</summary>
        Public Function OpenJobsAsync() As Task(Of List(Of SdJobRecord))
            Return _db.QueryAsync("SELECT * FROM SdJobs WHERE Status IN ('queued','running') ORDER BY CreatedUtc", AddressOf MapJob)
        End Function

        ' ------------------------------------------------------------------ pictures

        Private Shared Function MapImage(r As Microsoft.Data.Sqlite.SqliteDataReader) As SdImage
            Return New SdImage With {.Id = Db.Str(r, "Id"), .Username = Db.Str(r, "Username"), .JobId = Db.Str(r, "JobId"), .Idx = CInt(Db.Lng(r, "Idx")), .Prompt = Db.Str(r, "Prompt"),
                                     .NegativePrompt = Db.Str(r, "NegativePrompt"), .Params = If(ParseObject(Db.Str(r, "ParamsJson")), New JsonObject()), .Info = Db.Str(r, "Info"),
                                     .Seed = Db.LngN(r, "Seed"), .Width = CInt(Db.Lng(r, "Width")), .Height = CInt(Db.Lng(r, "Height")), .Model = Db.Str(r, "Model"), .Mode = Db.Str(r, "Mode"),
                                     .Bytes = Db.Lng(r, "Bytes"), .Starred = Db.Bool(r, "Starred"), .CreatedUtc = Db.Dt(r, "CreatedUtc")}
        End Function

        ''' <summary>Keeps a picture; only the user's newest keep pictures stay (starred ones are never dropped).</summary>
        Public Async Function AddImageAsync(img As SdImage, png As Byte(), keep As Integer) As Task
            Await File.WriteAllBytesAsync(FileOf(img.Id), png).ConfigureAwait(False)
            Await _db.ExecuteAsync("INSERT INTO SdImages(Id, Username, JobId, Idx, Prompt, NegativePrompt, ParamsJson, Info, Seed, Width, Height, Model, Mode, Bytes, Starred, CreatedUtc) " &
                                   "VALUES (@id, @u, @j, @i, @p, @n, @pa, @in, @s, @w, @h, @m, @mo, @b, 0, @c)",
                                   ("@id", CObj(img.Id)), ("@u", img.Username), ("@j", img.JobId), ("@i", img.Idx), ("@p", img.Prompt), ("@n", img.NegativePrompt),
                                   ("@pa", img.Params.ToJsonString()), ("@in", img.Info), ("@s", If(img.Seed.HasValue, CObj(img.Seed.Value), Nothing)), ("@w", img.Width), ("@h", img.Height),
                                   ("@m", img.Model), ("@mo", img.Mode), ("@b", png.LongLength), ("@c", Db.Ts(img.CreatedUtc))).ConfigureAwait(False)
            Dim old = Await _db.QueryAsync("SELECT Id FROM SdImages WHERE Username=@u AND Starred=0 ORDER BY CreatedUtc DESC LIMIT -1 OFFSET @k",
                                           Function(r) Db.Str(r, "Id"), ("@u", CObj(img.Username)), ("@k", Math.Max(1, keep))).ConfigureAwait(False)
            For Each id In old
                Await DeleteImageAsync(img.Username, id).ConfigureAwait(False)
            Next
        End Function

        Public Function ImagesAsync(username As String, query As String, starredOnly As Boolean, jobId As String, limit As Integer, beforeUtc As DateTime?) As Task(Of List(Of SdImage))
            Dim q = If(query, "").Trim()
            Return _db.QueryAsync("SELECT * FROM SdImages WHERE Username=@u" &
                                  If(q.Length > 0, " AND (Prompt LIKE @q OR NegativePrompt LIKE @q OR Model LIKE @q OR CAST(Seed AS TEXT)=@qs)", "") &
                                  If(starredOnly, " AND Starred=1", "") & If(String.IsNullOrEmpty(jobId), "", " AND JobId=@j") &
                                  If(beforeUtc.HasValue, " AND CreatedUtc<@b", "") & " ORDER BY CreatedUtc DESC, Idx LIMIT @l",
                                  AddressOf MapImage, ("@u", CObj(username)), ("@q", "%" & q & "%"), ("@qs", q), ("@j", jobId),
                                  ("@b", If(beforeUtc.HasValue, Db.Ts(beforeUtc.Value), "")), ("@l", Math.Clamp(limit, 1, 500)))
        End Function

        Public Function ImageAsync(username As String, id As String) As Task(Of SdImage)
            Return _db.QuerySingleAsync("SELECT * FROM SdImages WHERE Id=@id AND Username=@u", AddressOf MapImage, ("@id", CObj(id)), ("@u", username))
        End Function

        Public Async Function ReadImageAsync(id As String) As Task(Of Byte())
            Dim f = FileOf(id)
            Return If(File.Exists(f), Await File.ReadAllBytesAsync(f).ConfigureAwait(False), Nothing)
        End Function

        Public Async Function StarAsync(username As String, id As String, starred As Boolean) As Task(Of Boolean)
            Return Await _db.ExecuteAsync("UPDATE SdImages SET Starred=@s WHERE Id=@id AND Username=@u", ("@s", CObj(If(starred, 1, 0))), ("@id", id), ("@u", username)).ConfigureAwait(False) > 0
        End Function

        Public Async Function DeleteImageAsync(username As String, id As String) As Task(Of Boolean)
            Dim n = Await _db.ExecuteAsync("DELETE FROM SdImages WHERE Id=@id AND Username=@u", ("@id", CObj(id)), ("@u", username)).ConfigureAwait(False)
            If n > 0 Then
                Try
                    File.Delete(FileOf(id))
                Catch ex As IOException
                End Try
            End If
            Return n > 0
        End Function

        ''' <summary>Deletes the user's unstarred pictures.</summary>
        Public Async Function ClearAsync(username As String) As Task(Of Integer)
            Dim ids = Await _db.QueryAsync("SELECT Id FROM SdImages WHERE Username=@u AND Starred=0", Function(r) Db.Str(r, "Id"), ("@u", CObj(username))).ConfigureAwait(False)
            For Each id In ids
                Await DeleteImageAsync(username, id).ConfigureAwait(False)
            Next
            Return ids.Count
        End Function

        ' ------------------------------------------------------------------ prices

        Public Async Function PricingAsync(opt As Configuration.StableDiffusionOptions) As Task(Of SdPrice)
            Dim latest = Await _db.QuerySingleAsync("SELECT * FROM SdPricing ORDER BY Id DESC LIMIT 1",
                Function(r) New SdPrice With {.PerImage = Db.Dec(r, "PerImage"), .PerGpuMinute = Db.Dec(r, "PerGpuMinute"), .Source = "admin:" & Db.Str(r, "SetBy"),
                                              .EffectiveFromUtc = Db.DtN(r, "EffectiveFromUtc")}).ConfigureAwait(False)
            Return If(latest, New SdPrice With {.PerImage = opt?.PricePerImage, .PerGpuMinute = opt?.PricePerGpuMinute, .Source = "config"})
        End Function

        Public Async Function SetPricingAsync(perImage As Decimal?, perGpuMinute As Decimal?, actor As String) As Task
            For Each p In {perImage, perGpuMinute}
                If p.HasValue AndAlso p.Value < 0 Then Throw New ArgumentException("Prices must be 0 or more.")
            Next
            Await _db.ExecuteAsync("INSERT INTO SdPricing(PerImage, PerGpuMinute, EffectiveFromUtc, SetBy) VALUES (@i, @m, @t, @by)",
                                   ("@i", If(perImage.HasValue, CObj(perImage.Value), Nothing)), ("@m", If(perGpuMinute.HasValue, CObj(perGpuMinute.Value), Nothing)),
                                   ("@t", Db.Ts(DateTime.UtcNow)), ("@by", actor)).ConfigureAwait(False)
        End Function
    End Class

End Namespace
