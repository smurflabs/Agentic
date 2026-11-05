Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports Agentic.Artifacts
Imports Agentic.Persistence
Imports Agentic.Utilities

Namespace Work

    ''' <summary>Speech the Audio tab made and kept (a WAV file in data\audio\clips).</summary>
    Public NotInheritable Class AudioClip
        Public Property Id As String
        Public Property Username As String
        Public Property Text As String
        ''' <summary>The voice's key ("speaker:Name" or "voice:name").</summary>
        Public Property Voice As String
        ''' <summary>The request's speech settings (language, speed, temperature, …).</summary>
        Public Property Settings As JsonObject
        ''' <summary>stream (played as it was made) or file (/tts).</summary>
        Public Property Mode As String
        Public Property Bytes As Long
        Public Property DurationMs As Long
        ''' <summary>How long the server took to make it.</summary>
        Public Property GenerateMs As Long
        ''' <summary>When the first audio arrived (streamed clips).</summary>
        Public Property FirstAudioMs As Long?
        ''' <summary>What it cost the account (Nothing: free or not charged).</summary>
        Public Property CostMicros As Long?
        Public Property CreatedUtc As DateTime
    End Class

    ''' <summary>
    ''' What speech costs a User account (USD), like tokens in and out: text in (per 1M characters), audio out (per minute), and
    ''' each voice upload (cloning or renewing). Nothing = free.
    ''' </summary>
    Public NotInheritable Class SpeechPrice
        Public Property PerMillionChars As Decimal?
        Public Property PerMinute As Decimal?
        Public Property PerVoiceUpload As Decimal?
        ''' <summary>config, or admin:{name} for a price an administrator set.</summary>
        Public Property Source As String
        Public Property EffectiveFromUtc As DateTime?

        ''' <summary>Text in and audio out: characters × (USD per 1M characters) + minutes × (USD per minute), in micro-USD, rounded up.</summary>
        Public Function SpeechMicros(chars As Long, audioMs As Long) As Long
            Dim cost = CDec(Math.Max(0L, chars)) * PerMillionChars.GetValueOrDefault() + CDec(Math.Max(0L, audioMs)) / 60000D * PerMinute.GetValueOrDefault() * 1000000D
            Return CLng(Math.Ceiling(cost))
        End Function

        Public Function VoiceUploadMicros() As Long
            Return CLng(Math.Ceiling(PerVoiceUpload.GetValueOrDefault() * 1000000D))
        End Function

        Public ReadOnly Property IsFree As Boolean
            Get
                Return PerMillionChars.GetValueOrDefault() = 0 AndAlso PerMinute.GetValueOrDefault() = 0 AndAlso PerVoiceUpload.GetValueOrDefault() = 0
            End Get
        End Property
    End Class

    ''' <summary>
    ''' A voice's personality for one user: how its spoken summaries sound (the prompt Dictation mode gives the model that writes
    ''' them, and that model) and the speech settings it speaks with.
    ''' </summary>
    Public NotInheritable Class VoiceProfile
        Public Property Voice As String
        Public Property SummaryPrompt As String
        ''' <summary>The model that writes this voice's summaries (Nothing = TextToSpeech.SummaryModel, else the answer's model).</summary>
        Public Property SummaryModel As String
        Public Property Settings As JsonObject
        Public Property UpdatedUtc As DateTime
    End Class

    ''' <summary>The clips a cloned voice was made from, kept so it can be uploaded again before the speech server deletes it.</summary>
    Public NotInheritable Class VoiceSource
        Public Property Name As String
        Public Property CreatedBy As String
        Public Property Clips As New List(Of String)
        Public Property Settings As VoiceCloneSettings
        Public Property CreatedUtc As DateTime
        ''' <summary>The last upload to the speech server (the 30 days start again with each one).</summary>
        Public Property UploadedUtc As DateTime

        Public ReadOnly Property ExpiresUtc As DateTime
            Get
                Return UploadedUtc.AddDays(SpeechService.VoiceDays)
            End Get
        End Property
    End Class

    ''' <summary>
    ''' What the speech features keep: each user's chosen voice and Dictation mode settings, their voices' personalities, the
    ''' Audio tab's clips, and the clips cloned voices were made from (data\audio).
    ''' </summary>
    Public NotInheritable Class AudioStore
        Private Shared ReadOnly UnsafeId As New Regex("[^A-Za-z0-9_-]", RegexOptions.Compiled)
        Private ReadOnly _db As Database
        Private ReadOnly _root As String

        Public Sub New(db As Database, rootDirectory As String)
            _db = db
            _root = Path.GetFullPath(rootDirectory)
        End Sub

        Private Function Folder(ParamArray parts As String()) As String
            Dim p = Path.Combine({_root}.Concat(parts).ToArray())
            Directory.CreateDirectory(p)
            Return p
        End Function

        Private Shared Function SafeId(id As String) As String
            Return UnsafeId.Replace(If(id, ""), "")
        End Function

        Private Shared Function ParseObject(json As String) As JsonObject
            If String.IsNullOrWhiteSpace(json) Then Return Nothing
            Try
                Return TryCast(JsonNode.Parse(json), JsonObject)
            Catch ex As Text.Json.JsonException
                Return Nothing
            End Try
        End Function

        ' ------------------------------------------------------------------ preferences

        ''' <summary>The user's chosen voice ("voice") and Dictation mode settings.</summary>
        Public Async Function PrefsAsync(username As String) As Task(Of JsonObject)
            Dim v = Await _db.ScalarAsync("SELECT PrefsJson FROM AudioPrefs WHERE Username=@u", ("@u", CObj(username))).ConfigureAwait(False)
            Return If(ParseObject(If(v Is Nothing OrElse TypeOf v Is DBNull, Nothing, Convert.ToString(v))), New JsonObject())
        End Function

        Public Function SavePrefsAsync(username As String, prefs As JsonObject) As Task(Of Integer)
            Return _db.ExecuteAsync("INSERT INTO AudioPrefs(Username, PrefsJson, UpdatedUtc) VALUES (@u, @p, @n) " &
                                    "ON CONFLICT(Username) DO UPDATE SET PrefsJson=excluded.PrefsJson, UpdatedUtc=excluded.UpdatedUtc",
                                    ("@u", CObj(username)), ("@p", prefs.ToJsonString()), ("@n", Db.Ts(DateTime.UtcNow)))
        End Function

        ' ------------------------------------------------------------------ prices

        ''' <summary>What speech costs now: the latest price an administrator set, else the configured starting prices.</summary>
        Public Async Function PricingAsync(opt As Configuration.TextToSpeechOptions) As Task(Of SpeechPrice)
            Dim latest = Await _db.QuerySingleAsync("SELECT * FROM AudioPricing ORDER BY Id DESC LIMIT 1",
                Function(r) New SpeechPrice With {.PerMillionChars = Db.Dec(r, "PerMillionChars"), .PerMinute = Db.Dec(r, "PerMinute"), .PerVoiceUpload = Db.Dec(r, "PerVoiceUpload"),
                                                  .Source = "admin:" & Db.Str(r, "SetBy"), .EffectiveFromUtc = Db.DtN(r, "EffectiveFromUtc")}).ConfigureAwait(False)
            Return If(latest, New SpeechPrice With {.PerMillionChars = opt?.PricePerMillionChars, .PerMinute = opt?.PricePerMinute, .PerVoiceUpload = opt?.PricePerVoiceUpload, .Source = "config"})
        End Function

        ''' <summary>An administrator sets the prices (Nothing = free); earlier prices stay as history.</summary>
        Public Async Function SetPricingAsync(perMillionChars As Decimal?, perMinute As Decimal?, perVoiceUpload As Decimal?, actor As String) As Task
            For Each p In {perMillionChars, perMinute, perVoiceUpload}
                If p.HasValue AndAlso p.Value < 0 Then Throw New ArgumentException("Prices must be 0 or more.")
            Next
            Await _db.ExecuteAsync("INSERT INTO AudioPricing(PerMillionChars, PerMinute, PerVoiceUpload, EffectiveFromUtc, SetBy) VALUES (@c, @m, @v, @t, @by)",
                                   ("@c", If(perMillionChars.HasValue, CObj(perMillionChars.Value), Nothing)), ("@m", If(perMinute.HasValue, CObj(perMinute.Value), Nothing)),
                                   ("@v", If(perVoiceUpload.HasValue, CObj(perVoiceUpload.Value), Nothing)), ("@t", Db.Ts(DateTime.UtcNow)), ("@by", actor)).ConfigureAwait(False)
        End Function

        ' ------------------------------------------------------------------ personalities

        Private Shared Function MapProfile(r As Microsoft.Data.Sqlite.SqliteDataReader) As VoiceProfile
            Return New VoiceProfile With {.Voice = Db.Str(r, "Voice"), .SummaryPrompt = Db.Str(r, "SummaryPrompt"), .SummaryModel = Db.Str(r, "SummaryModel"),
                                          .Settings = If(ParseObject(Db.Str(r, "SettingsJson")), New JsonObject()), .UpdatedUtc = Db.Dt(r, "UpdatedUtc")}
        End Function

        Public Function ProfilesAsync(username As String) As Task(Of List(Of VoiceProfile))
            Return _db.QueryAsync("SELECT * FROM AudioVoiceProfiles WHERE Username=@u ORDER BY Voice", AddressOf MapProfile, ("@u", CObj(username)))
        End Function

        Public Function ProfileAsync(username As String, voice As String) As Task(Of VoiceProfile)
            Return _db.QuerySingleAsync("SELECT * FROM AudioVoiceProfiles WHERE Username=@u AND Voice=@v", AddressOf MapProfile, ("@u", CObj(username)), ("@v", voice))
        End Function

        Public Function SaveProfileAsync(username As String, p As VoiceProfile) As Task(Of Integer)
            Return _db.ExecuteAsync("INSERT INTO AudioVoiceProfiles(Username, Voice, SummaryPrompt, SummaryModel, SettingsJson, UpdatedUtc) VALUES (@u, @v, @p, @m, @s, @n) " &
                                    "ON CONFLICT(Username, Voice) DO UPDATE SET SummaryPrompt=excluded.SummaryPrompt, SummaryModel=excluded.SummaryModel, " &
                                    "SettingsJson=excluded.SettingsJson, UpdatedUtc=excluded.UpdatedUtc",
                                    ("@u", CObj(username)), ("@v", p.Voice), ("@p", p.SummaryPrompt), ("@m", p.SummaryModel),
                                    ("@s", If(p.Settings Is Nothing, Nothing, p.Settings.ToJsonString())), ("@n", Db.Ts(DateTime.UtcNow)))
        End Function

        Public Async Function DeleteProfileAsync(username As String, voice As String) As Task(Of Boolean)
            Return Await _db.ExecuteAsync("DELETE FROM AudioVoiceProfiles WHERE Username=@u AND Voice=@v", ("@u", CObj(username)), ("@v", voice)).ConfigureAwait(False) > 0
        End Function

        ' ------------------------------------------------------------------ clips

        Private Shared Function MapClip(r As Microsoft.Data.Sqlite.SqliteDataReader) As AudioClip
            Return New AudioClip With {.Id = Db.Str(r, "Id"), .Username = Db.Str(r, "Username"), .Text = Db.Str(r, "Text"), .Voice = Db.Str(r, "Voice"),
                                       .Settings = If(ParseObject(Db.Str(r, "SettingsJson")), New JsonObject()), .Mode = Db.Str(r, "Mode"), .Bytes = Db.Lng(r, "Bytes"),
                                       .DurationMs = Db.Lng(r, "DurationMs"), .GenerateMs = Db.Lng(r, "GenerateMs"), .FirstAudioMs = Db.LngN(r, "FirstAudioMs"),
                                       .CostMicros = Db.LngN(r, "CostMicros"), .CreatedUtc = Db.Dt(r, "CreatedUtc")}
        End Function

        Private Function ClipFile(id As String) As String
            Return Path.Combine(Folder("clips"), SafeId(id) & ".wav")
        End Function

        ''' <summary>Keeps a clip; only the user's newest keep clips stay.</summary>
        Public Async Function AddClipAsync(clip As AudioClip, wav As Byte(), keep As Integer) As Task
            Await File.WriteAllBytesAsync(ClipFile(clip.Id), wav).ConfigureAwait(False)
            Await _db.ExecuteAsync("INSERT INTO AudioClips(Id, Username, Text, Voice, SettingsJson, Mode, Bytes, DurationMs, GenerateMs, FirstAudioMs, CostMicros, CreatedUtc) " &
                                   "VALUES (@id, @u, @t, @v, @s, @m, @b, @d, @g, @f, @cost, @c)",
                                   ("@id", CObj(clip.Id)), ("@u", clip.Username), ("@t", clip.Text), ("@v", clip.Voice), ("@s", clip.Settings?.ToJsonString()),
                                   ("@m", clip.Mode), ("@b", wav.LongLength), ("@d", clip.DurationMs), ("@g", clip.GenerateMs),
                                   ("@f", If(clip.FirstAudioMs.HasValue, CObj(clip.FirstAudioMs.Value), Nothing)),
                                   ("@cost", If(clip.CostMicros.HasValue, CObj(clip.CostMicros.Value), Nothing)), ("@c", Db.Ts(clip.CreatedUtc))).ConfigureAwait(False)
            Dim old = Await _db.QueryAsync("SELECT Id FROM AudioClips WHERE Username=@u ORDER BY CreatedUtc DESC LIMIT -1 OFFSET @k",
                                           Function(r) Db.Str(r, "Id"), ("@u", CObj(clip.Username)), ("@k", Math.Max(1, keep))).ConfigureAwait(False)
            For Each id In old
                Await DeleteClipAsync(clip.Username, id).ConfigureAwait(False)
            Next
        End Function

        Public Function ListClipsAsync(username As String, query As String, limit As Integer) As Task(Of List(Of AudioClip))
            Dim q = If(query, "").Trim()
            Return _db.QueryAsync("SELECT * FROM AudioClips WHERE Username=@u" & If(q.Length > 0, " AND (Text LIKE @q OR Voice LIKE @q)", "") & " ORDER BY CreatedUtc DESC LIMIT @l",
                                  AddressOf MapClip, ("@u", CObj(username)), ("@q", "%" & q & "%"), ("@l", Math.Clamp(limit, 1, 1000)))
        End Function

        Public Function GetClipAsync(username As String, id As String) As Task(Of AudioClip)
            Return _db.QuerySingleAsync("SELECT * FROM AudioClips WHERE Id=@id AND Username=@u", AddressOf MapClip, ("@id", CObj(id)), ("@u", username))
        End Function

        ''' <summary>The clip's WAV file (Nothing when it is gone).</summary>
        Public Async Function ReadClipAsync(id As String) As Task(Of Byte())
            Dim f = ClipFile(id)
            Return If(File.Exists(f), Await File.ReadAllBytesAsync(f).ConfigureAwait(False), Nothing)
        End Function

        Public Async Function DeleteClipAsync(username As String, id As String) As Task(Of Boolean)
            Dim n = Await _db.ExecuteAsync("DELETE FROM AudioClips WHERE Id=@id AND Username=@u", ("@id", CObj(id)), ("@u", username)).ConfigureAwait(False)
            If n > 0 Then
                Try
                    File.Delete(ClipFile(id))
                Catch ex As IOException
                End Try
            End If
            Return n > 0
        End Function

        Public Async Function ClearClipsAsync(username As String) As Task(Of Integer)
            Dim ids = Await _db.QueryAsync("SELECT Id FROM AudioClips WHERE Username=@u", Function(r) Db.Str(r, "Id"), ("@u", CObj(username))).ConfigureAwait(False)
            For Each id In ids
                Await DeleteClipAsync(username, id).ConfigureAwait(False)
            Next
            Return ids.Count
        End Function

        ' ------------------------------------------------------------------ clips for cloning

        ''' <summary>A clip uploaded for a voice that is not cloned yet (kept a day). Returns its id.</summary>
        Public Async Function StageClipAsync(username As String, fileName As String, data As Byte()) As Task(Of String)
            PurgeStaged()
            Dim id = Ids.NewId("vclip")
            Dim dir = Folder("staging", id)
            Await File.WriteAllBytesAsync(Path.Combine(dir, UploadStore.SafeFileName(fileName)), data).ConfigureAwait(False)
            Await File.WriteAllTextAsync(Path.Combine(dir, ".owner"), username).ConfigureAwait(False)
            Return id
        End Function

        ''' <summary>The user's staged clips by id (ArgumentException when one is gone).</summary>
        Public Async Function StagedClipsAsync(username As String, ids As IEnumerable(Of String)) As Task(Of List(Of (FileName As String, Data As Byte())))
            Dim result As New List(Of (FileName As String, Data As Byte()))
            For Each id In ids
                Dim dir = Path.Combine(_root, "staging", SafeId(id))
                Dim owner = Path.Combine(dir, ".owner")
                If Not File.Exists(owner) OrElse (Await File.ReadAllTextAsync(owner).ConfigureAwait(False)) <> username Then Throw New ArgumentException("A clip was not found (they are kept for a day): add it again.")
                Dim f = Directory.GetFiles(dir).FirstOrDefault(Function(x) Path.GetFileName(x) <> ".owner")
                If f Is Nothing Then Throw New ArgumentException("A clip was not found: add it again.")
                result.Add((Path.GetFileName(f), Await File.ReadAllBytesAsync(f).ConfigureAwait(False)))
            Next
            Return result
        End Function

        Public Sub DropStaged(ids As IEnumerable(Of String))
            For Each id In ids
                Try
                    Dim dir = Path.Combine(_root, "staging", SafeId(id))
                    If SafeId(id).Length > 0 AndAlso Directory.Exists(dir) Then Directory.Delete(dir, True)
                Catch ex As IOException
                End Try
            Next
        End Sub

        Private Sub PurgeStaged()
            Dim dir = Path.Combine(_root, "staging")
            If Not Directory.Exists(dir) Then Return
            For Each d In Directory.GetDirectories(dir)
                Try
                    If Directory.GetCreationTimeUtc(d) < DateTime.UtcNow.AddDays(-1) Then Directory.Delete(d, True)
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                End Try
            Next
        End Sub

        ' ------------------------------------------------------------------ cloned voices' clips

        Private Shared Function MapSource(r As Microsoft.Data.Sqlite.SqliteDataReader) As VoiceSource
            Return New VoiceSource With {.Name = Db.Str(r, "Name"), .CreatedBy = Db.Str(r, "CreatedBy"),
                                         .Clips = If(JsonUtil.FromJson(Of List(Of String))(Db.Str(r, "ClipsJson")), New List(Of String)),
                                         .Settings = If(JsonUtil.FromJson(Of VoiceCloneSettings)(Db.Str(r, "SettingsJson")), New VoiceCloneSettings()),
                                         .CreatedUtc = Db.Dt(r, "CreatedUtc"), .UploadedUtc = Db.Dt(r, "UploadedUtc")}
        End Function

        Public Function SourceAsync(name As String) As Task(Of VoiceSource)
            Return _db.QuerySingleAsync("SELECT * FROM AudioVoiceSources WHERE Name=@n", AddressOf MapSource, ("@n", CObj(name)))
        End Function

        Public Function SourcesAsync() As Task(Of List(Of VoiceSource))
            Return _db.QueryAsync("SELECT * FROM AudioVoiceSources ORDER BY Name", AddressOf MapSource)
        End Function

        ''' <summary>Keeps the clips a voice was cloned from (the name must be a valid voice name); a voice made again replaces them.</summary>
        Public Async Function SaveSourceAsync(name As String, createdBy As String, clips As IList(Of (FileName As String, Data As Byte())), settings As VoiceCloneSettings) As Task(Of VoiceSource)
            If Not SpeechService.IsVoiceName(name) Then Throw New ArgumentException("Not a voice name.")
            Dim dir = Path.Combine(_root, "voices", name)
            If Directory.Exists(dir) Then Directory.Delete(dir, True)
            Directory.CreateDirectory(dir)
            Dim names As New List(Of String)
            For i = 0 To clips.Count - 1
                Dim fn = $"{i + 1:00}-{UploadStore.SafeFileName(clips(i).FileName)}"
                Await File.WriteAllBytesAsync(Path.Combine(dir, fn), clips(i).Data).ConfigureAwait(False)
                names.Add(fn)
            Next
            Dim now = Db.Ts(DateTime.UtcNow)
            Await _db.ExecuteAsync("INSERT INTO AudioVoiceSources(Name, CreatedBy, ClipsJson, SettingsJson, CreatedUtc, UploadedUtc) VALUES (@n, @by, @c, @s, @t, @t) " &
                                   "ON CONFLICT(Name) DO UPDATE SET CreatedBy=excluded.CreatedBy, ClipsJson=excluded.ClipsJson, SettingsJson=excluded.SettingsJson, " &
                                   "CreatedUtc=excluded.CreatedUtc, UploadedUtc=excluded.UploadedUtc",
                                   ("@n", CObj(name)), ("@by", createdBy), ("@c", JsonUtil.ToJson(names)), ("@s", JsonUtil.ToJson(settings)), ("@t", now)).ConfigureAwait(False)
            Return Await SourceAsync(name).ConfigureAwait(False)
        End Function

        ''' <summary>The voice was uploaded again: its 30 days start over.</summary>
        Public Function MarkUploadedAsync(name As String) As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE AudioVoiceSources SET UploadedUtc=@t WHERE Name=@n", ("@t", CObj(Db.Ts(DateTime.UtcNow))), ("@n", name))
        End Function

        Public Async Function SourceClipsAsync(src As VoiceSource) As Task(Of List(Of (FileName As String, Data As Byte())))
            Dim result As New List(Of (FileName As String, Data As Byte()))
            Dim dir = Path.Combine(_root, "voices", src.Name)
            For Each fn In src.Clips
                Dim f = Path.Combine(dir, UploadStore.SafeFileName(fn))
                If File.Exists(f) Then result.Add((fn.Substring(Math.Min(fn.Length, 3)), Await File.ReadAllBytesAsync(f).ConfigureAwait(False)))
            Next
            Return result
        End Function

        Public Async Function DeleteSourceAsync(name As String) As Task
            Await _db.ExecuteAsync("DELETE FROM AudioVoiceSources WHERE Name=@n", ("@n", CObj(name))).ConfigureAwait(False)
            If Not SpeechService.IsVoiceName(name) Then Return
            Try
                Dim dir = Path.Combine(_root, "voices", name)
                If Directory.Exists(dir) Then Directory.Delete(dir, True)
            Catch ex As IOException
            End Try
        End Function
    End Class

End Namespace
