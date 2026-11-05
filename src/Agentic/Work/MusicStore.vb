Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports Agentic.Persistence
Imports Agentic.Utilities

Namespace Work

    ''' <summary>A song the Music tab made (or imported) and kept: its audio and cover in data\music.</summary>
    Public NotInheritable Class MusicSong
        Public Property Id As String
        Public Property Username As String
        ''' <summary>The request it came from ("import" for songs taken from the Suno library).</summary>
        Public Property JobId As String
        ''' <summary>Suno's id of the song.</summary>
        Public Property ClipId As String
        Public Property Title As String
        ''' <summary>The style.</summary>
        Public Property Tags As String
        Public Property Lyrics As String
        ''' <summary>What was asked for (a simple song's description).</summary>
        Public Property Description As String
        Public Property Instrumental As Boolean
        Public Property Model As String
        Public Property DurationMs As Long
        Public Property Mime As String
        ''' <summary>The audio kept here (0: not downloaded from Suno yet; that happens the first time it is played).</summary>
        Public Property Bytes As Long
        Public Property HasCover As Boolean
        Public Property Starred As Boolean
        Public Property CreatedUtc As DateTime

        Public ReadOnly Property HasAudio As Boolean
            Get
                Return Bytes > 0
            End Get
        End Property
    End Class

    ''' <summary>A request of the Music tab: what it asked for, Suno's songs for it and how it ended.</summary>
    Public NotInheritable Class MusicJobRecord
        Public Property Id As String
        Public Property Username As String
        Public Property Status As String
        Public Property BatchId As String
        Public Property Request As JsonObject
        Public Property ClipIds As New List(Of String)
        Public Property SongIds As New List(Of String)
        Public Property [Error] As String
        Public Property CostMicros As Long?
        Public Property CreatedUtc As DateTime
        Public Property FinishedUtc As DateTime?
    End Class

    ''' <summary>What music costs a User account (USD): per song made, per lyrics written by AI. Nothing = free.</summary>
    Public NotInheritable Class MusicPrice
        Public Property PerSong As Decimal?
        Public Property PerLyrics As Decimal?
        Public Property Source As String
        Public Property EffectiveFromUtc As DateTime?

        Public Function SongsMicros(songs As Integer) As Long
            Return CLng(Math.Ceiling(CDec(Math.Max(0, songs)) * PerSong.GetValueOrDefault() * 1000000D))
        End Function

        Public Function LyricsMicros() As Long
            Return CLng(Math.Ceiling(PerLyrics.GetValueOrDefault() * 1000000D))
        End Function
    End Class

    ''' <summary>The Music tab's requests, songs (data\music) and prices.</summary>
    Public NotInheritable Class MusicStore
        Private Shared ReadOnly UnsafeId As New Regex("[^A-Za-z0-9_-]", RegexOptions.Compiled)
        Private ReadOnly _db As Database
        Private ReadOnly _root As String

        Public Sub New(db As Database, rootDirectory As String)
            _db = db
            _root = Path.GetFullPath(rootDirectory)
        End Sub

        Private Function FileOf(id As String, ext As String) As String
            Directory.CreateDirectory(_root)
            Return Path.Combine(_root, UnsafeId.Replace(If(id, ""), "") & ext)
        End Function

        Private Shared Function ParseObject(json As String) As JsonObject
            If String.IsNullOrWhiteSpace(json) Then Return Nothing
            Try
                Return TryCast(JsonNode.Parse(json), JsonObject)
            Catch ex As Text.Json.JsonException
                Return Nothing
            End Try
        End Function

        ' ------------------------------------------------------------------ requests

        Private Shared Function MapJob(r As Microsoft.Data.Sqlite.SqliteDataReader) As MusicJobRecord
            Return New MusicJobRecord With {.Id = Db.Str(r, "Id"), .Username = Db.Str(r, "Username"), .Status = Db.Str(r, "Status"), .BatchId = Db.Str(r, "BatchId"),
                                            .Request = If(ParseObject(Db.Str(r, "RequestJson")), New JsonObject()),
                                            .ClipIds = If(JsonUtil.FromJson(Of List(Of String))(Db.Str(r, "ClipIdsJson")), New List(Of String)),
                                            .SongIds = If(JsonUtil.FromJson(Of List(Of String))(Db.Str(r, "SongIdsJson")), New List(Of String)),
                                            .Error = Db.Str(r, "Error"), .CostMicros = Db.LngN(r, "CostMicros"), .CreatedUtc = Db.Dt(r, "CreatedUtc"), .FinishedUtc = Db.DtN(r, "FinishedUtc")}
        End Function

        Public Function AddJobAsync(j As MusicJobRecord) As Task(Of Integer)
            Return _db.ExecuteAsync("INSERT INTO MusicJobs(Id, Username, Status, BatchId, RequestJson, ClipIdsJson, CreatedUtc) VALUES (@id, @u, @s, @b, @r, @c, @t)",
                                    ("@id", CObj(j.Id)), ("@u", j.Username), ("@s", j.Status), ("@b", j.BatchId), ("@r", j.Request.ToJsonString()),
                                    ("@c", JsonUtil.ToJson(j.ClipIds)), ("@t", Db.Ts(j.CreatedUtc)))
        End Function

        Public Function UpdateJobAsync(j As MusicJobRecord) As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE MusicJobs SET Status=@s, SongIdsJson=@i, Error=@e, CostMicros=@cost, FinishedUtc=@f WHERE Id=@id",
                                    ("@s", CObj(j.Status)), ("@i", JsonUtil.ToJson(j.SongIds)), ("@e", j.Error), ("@cost", If(j.CostMicros.HasValue, CObj(j.CostMicros.Value), Nothing)),
                                    ("@f", If(j.FinishedUtc.HasValue, CObj(Db.Ts(j.FinishedUtc.Value)), Nothing)), ("@id", j.Id))
        End Function

        Public Function JobAsync(id As String) As Task(Of MusicJobRecord)
            Return _db.QuerySingleAsync("SELECT * FROM MusicJobs WHERE Id=@id", AddressOf MapJob, ("@id", CObj(id)))
        End Function

        Public Function JobsAsync(username As String, limit As Integer) As Task(Of List(Of MusicJobRecord))
            Return _db.QueryAsync("SELECT * FROM MusicJobs WHERE Username=@u ORDER BY CreatedUtc DESC LIMIT @l", AddressOf MapJob, ("@u", CObj(username)), ("@l", Math.Clamp(limit, 1, 200)))
        End Function

        Public Function OpenJobsAsync() As Task(Of List(Of MusicJobRecord))
            Return _db.QueryAsync("SELECT * FROM MusicJobs WHERE Status IN ('making','saving') ORDER BY CreatedUtc", AddressOf MapJob)
        End Function

        ' ------------------------------------------------------------------ songs

        Private Shared Function MapSong(r As Microsoft.Data.Sqlite.SqliteDataReader) As MusicSong
            Return New MusicSong With {.Id = Db.Str(r, "Id"), .Username = Db.Str(r, "Username"), .JobId = Db.Str(r, "JobId"), .ClipId = Db.Str(r, "ClipId"), .Title = Db.Str(r, "Title"),
                                       .Tags = Db.Str(r, "Tags"), .Lyrics = Db.Str(r, "Lyrics"), .Description = Db.Str(r, "Description"), .Instrumental = Db.Bool(r, "Instrumental"),
                                       .Model = Db.Str(r, "Model"), .DurationMs = Db.Lng(r, "DurationMs"), .Mime = Db.Str(r, "Mime"), .Bytes = Db.Lng(r, "Bytes"),
                                       .HasCover = Db.Bool(r, "HasCover"), .Starred = Db.Bool(r, "Starred"), .CreatedUtc = Db.Dt(r, "CreatedUtc")}
        End Function

        ''' <summary>Keeps a song (and its cover); only the user's newest keep songs stay (starred ones are never dropped).</summary>
        ''' <summary>Keeps a song: its audio (Nothing: downloaded from Suno the first time it is played), its cover.</summary>
        Public Async Function AddSongAsync(s As MusicSong, audio As Byte(), cover As Byte(), keep As Integer) As Task
            If audio IsNot Nothing Then Await File.WriteAllBytesAsync(FileOf(s.Id, ".audio"), audio).ConfigureAwait(False)
            If cover IsNot Nothing Then Await File.WriteAllBytesAsync(FileOf(s.Id, ".cover"), cover).ConfigureAwait(False)
            s.Bytes = If(audio Is Nothing, 0L, audio.LongLength)
            Await _db.ExecuteAsync("INSERT INTO MusicSongs(Id, Username, JobId, ClipId, Title, Tags, Lyrics, Description, Instrumental, Model, DurationMs, Mime, Bytes, HasCover, Starred, CreatedUtc) " &
                                   "VALUES (@id, @u, @j, @c, @t, @g, @l, @d, @i, @m, @du, @mi, @b, @hc, 0, @cr)",
                                   ("@id", CObj(s.Id)), ("@u", s.Username), ("@j", s.JobId), ("@c", s.ClipId), ("@t", s.Title), ("@g", s.Tags), ("@l", s.Lyrics),
                                   ("@d", s.Description), ("@i", If(s.Instrumental, 1, 0)), ("@m", s.Model), ("@du", s.DurationMs), ("@mi", s.Mime), ("@b", s.Bytes),
                                   ("@hc", If(cover IsNot Nothing, 1, 0)), ("@cr", Db.Ts(s.CreatedUtc))).ConfigureAwait(False)
            Dim old = Await _db.QueryAsync("SELECT Id FROM MusicSongs WHERE Username=@u AND Starred=0 ORDER BY CreatedUtc DESC LIMIT -1 OFFSET @k",
                                           Function(r) Db.Str(r, "Id"), ("@u", CObj(s.Username)), ("@k", Math.Max(1, keep))).ConfigureAwait(False)
            For Each id In old
                Await DeleteSongAsync(s.Username, id).ConfigureAwait(False)
            Next
        End Function

        Public Function SongsAsync(username As String, query As String, starredOnly As Boolean, jobId As String, limit As Integer, beforeUtc As DateTime?) As Task(Of List(Of MusicSong))
            Dim q = If(query, "").Trim()
            Return _db.QueryAsync("SELECT * FROM MusicSongs WHERE Username=@u" &
                                  If(q.Length > 0, " AND (Title LIKE @q OR Tags LIKE @q OR Lyrics LIKE @q OR Description LIKE @q)", "") &
                                  If(starredOnly, " AND Starred=1", "") & If(String.IsNullOrEmpty(jobId), "", " AND JobId=@j") &
                                  If(beforeUtc.HasValue, " AND CreatedUtc<@b", "") & " ORDER BY CreatedUtc DESC LIMIT @l",
                                  AddressOf MapSong, ("@u", CObj(username)), ("@q", "%" & q & "%"), ("@j", jobId),
                                  ("@b", If(beforeUtc.HasValue, Db.Ts(beforeUtc.Value), "")), ("@l", Math.Clamp(limit, 1, 500)))
        End Function

        Public Function SongAsync(username As String, id As String) As Task(Of MusicSong)
            Return _db.QuerySingleAsync("SELECT * FROM MusicSongs WHERE Id=@id AND Username=@u", AddressOf MapSong, ("@id", CObj(id)), ("@u", username))
        End Function

        ''' <summary>The Suno songs this user already has (so the Suno library offers only new ones).</summary>
        Public Function ClipIdsAsync(username As String) As Task(Of List(Of String))
            Return _db.QueryAsync("SELECT ClipId FROM MusicSongs WHERE Username=@u", Function(r) Db.Str(r, "ClipId"), ("@u", CObj(username)))
        End Function

        Public Async Function ReadAudioAsync(id As String) As Task(Of Byte())
            Dim f = FileOf(id, ".audio")
            Return If(File.Exists(f), Await File.ReadAllBytesAsync(f).ConfigureAwait(False), Nothing)
        End Function

        ''' <summary>A song's audio, downloaded from Suno after it was made (its first play).</summary>
        Public Async Function SetAudioAsync(s As MusicSong, audio As Byte(), mime As String) As Task
            Await File.WriteAllBytesAsync(FileOf(s.Id, ".audio"), audio).ConfigureAwait(False)
            Await _db.ExecuteAsync("UPDATE MusicSongs SET Mime=@m, Bytes=@b WHERE Id=@id", ("@m", CObj(mime)), ("@b", audio.LongLength), ("@id", s.Id)).ConfigureAwait(False)
            s.Mime = mime
            s.Bytes = audio.LongLength
        End Function

        ''' <summary>
        ''' Songs kept with audio no player can play (Suno's scrambled stream, kept before 2.0.8): their audio goes, so their
        ''' next play downloads it properly. Returns how many.
        ''' </summary>
        Public Async Function DropUnplayableAudioAsync() As Task(Of Integer)
            Dim kept = Await _db.QueryAsync("SELECT Id FROM MusicSongs WHERE Bytes>0", Function(r) Db.Str(r, "Id")).ConfigureAwait(False)
            Dim dropped = 0
            For Each id In kept
                Dim f = FileOf(id, ".audio")
                Dim head(15) As Byte
                Dim n = 0
                If File.Exists(f) Then
                    Using s = File.OpenRead(f)
                        n = Await s.ReadAsync(head.AsMemory()).ConfigureAwait(False)
                    End Using
                End If
                If n > 0 AndAlso MusicService.Playable(head, n) Then Continue For
                Try
                    File.Delete(f)
                Catch ex As IOException
                End Try
                Await _db.ExecuteAsync("UPDATE MusicSongs SET Bytes=0 WHERE Id=@id", ("@id", CObj(id))).ConfigureAwait(False)
                dropped += 1
            Next
            Return dropped
        End Function

        Public Async Function ReadCoverAsync(id As String) As Task(Of Byte())
            Dim f = FileOf(id, ".cover")
            Return If(File.Exists(f), Await File.ReadAllBytesAsync(f).ConfigureAwait(False), Nothing)
        End Function

        ''' <summary>Changes a song's title or star (Nothing leaves it as it is).</summary>
        Public Async Function UpdateSongAsync(username As String, id As String, title As String, starred As Boolean?) As Task(Of Boolean)
            Return Await _db.ExecuteAsync("UPDATE MusicSongs SET Title=COALESCE(@t, Title), Starred=COALESCE(@s, Starred) WHERE Id=@id AND Username=@u",
                                          ("@t", CObj(title)), ("@s", If(starred.HasValue, CObj(If(starred.Value, 1, 0)), Nothing)), ("@id", id), ("@u", username)).ConfigureAwait(False) > 0
        End Function

        Public Async Function DeleteSongAsync(username As String, id As String) As Task(Of Boolean)
            Dim n = Await _db.ExecuteAsync("DELETE FROM MusicSongs WHERE Id=@id AND Username=@u", ("@id", CObj(id)), ("@u", username)).ConfigureAwait(False)
            If n > 0 Then
                For Each ext In {".audio", ".cover"}
                    Try
                        File.Delete(FileOf(id, ext))
                    Catch ex As IOException
                    End Try
                Next
            End If
            Return n > 0
        End Function

        Public Async Function ClearAsync(username As String) As Task(Of Integer)
            Dim ids = Await _db.QueryAsync("SELECT Id FROM MusicSongs WHERE Username=@u AND Starred=0", Function(r) Db.Str(r, "Id"), ("@u", CObj(username))).ConfigureAwait(False)
            For Each id In ids
                Await DeleteSongAsync(username, id).ConfigureAwait(False)
            Next
            Return ids.Count
        End Function

        ' ------------------------------------------------------------------ prices

        Public Async Function PricingAsync(opt As Configuration.MusicOptions) As Task(Of MusicPrice)
            Dim latest = Await _db.QuerySingleAsync("SELECT * FROM MusicPricing ORDER BY Id DESC LIMIT 1",
                Function(r) New MusicPrice With {.PerSong = Db.Dec(r, "PerSong"), .PerLyrics = Db.Dec(r, "PerLyrics"), .Source = "admin:" & Db.Str(r, "SetBy"),
                                                 .EffectiveFromUtc = Db.DtN(r, "EffectiveFromUtc")}).ConfigureAwait(False)
            Return If(latest, New MusicPrice With {.PerSong = opt?.PricePerSong, .PerLyrics = opt?.PricePerLyrics, .Source = "config"})
        End Function

        Public Async Function SetPricingAsync(perSong As Decimal?, perLyrics As Decimal?, actor As String) As Task
            For Each p In {perSong, perLyrics}
                If p.HasValue AndAlso p.Value < 0 Then Throw New ArgumentException("Prices must be 0 or more.")
            Next
            Await _db.ExecuteAsync("INSERT INTO MusicPricing(PerSong, PerLyrics, EffectiveFromUtc, SetBy) VALUES (@s, @l, @t, @by)",
                                   ("@s", If(perSong.HasValue, CObj(perSong.Value), Nothing)), ("@l", If(perLyrics.HasValue, CObj(perLyrics.Value), Nothing)),
                                   ("@t", Db.Ts(DateTime.UtcNow)), ("@by", actor)).ConfigureAwait(False)
        End Function
    End Class

End Namespace
