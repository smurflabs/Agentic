Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.IO.Compression
Imports System.Net
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.RegularExpressions
Imports Agentic.Persistence
Imports Agentic.Utilities

Namespace Work

    ''' <summary>An artifact in a chat answer: an HTML or SVG code block, which the Chat tab runs in its sandboxed preview.</summary>
    Public NotInheritable Class ChatArtifact
        Public Property ChatId As String
        Public Property ChatTitle As String
        Public Property MessageId As String
        ''' <summary>The block's number in its message (every fenced code block counts, as in the Chat tab).</summary>
        Public Property Block As Integer
        Public Property Lang As String
        Public Property Title As String
        Public Property Lines As Integer
        Public Property Bytes As Integer
        Public Property CreatedUtc As DateTime
    End Class

    ''' <summary>A public link to an artifact: a chat block (code) or an uploaded website (site).</summary>
    Public NotInheritable Class SharedArtifact
        Public Property Token As String
        Public Property Username As String
        Public Property Kind As String
        Public Property Title As String
        Public Property Lang As String
        ''' <summary>code: the block as it was shared (only read when it is opened).</summary>
        Public Property Content As String
        Public Property ChatId As String
        Public Property MessageId As String
        Public Property BlockIndex As Integer?
        Public Property Source As String
        Public Property SourceKey As String
        Public Property Files As Integer
        Public Property Bytes As Long
        Public Property Views As Integer
        Public Property CreatedUtc As DateTime
        Public Property UpdatedUtc As DateTime
    End Class

    ''' <summary>Finding artifacts in chat answers: their code blocks, numbered and titled like the Chat tab shows them.</summary>
    Public Module ChatArtifacts

        ''' <summary>Languages an artifact may be in: pages and drawings (the browser runs them; JavaScript snippets are not apps).</summary>
        Public ReadOnly RunnableLangs As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {"html", "htm", "svg"}
        ' The same fences markdown.js finds: an unclosed one runs to the end of the answer.
        Private ReadOnly Fence As New Regex("```([\w+#.-]*)[^\n]*\n([\s\S]*?)(```|$)", RegexOptions.Compiled)
        Private ReadOnly TitleTag As New Regex("<title[^>]*>([\s\S]*?)</title>", RegexOptions.IgnoreCase Or RegexOptions.Compiled)
        Private ReadOnly HeadingTag As New Regex("<h[12][^>]*>([\s\S]*?)</h[12]>", RegexOptions.IgnoreCase Or RegexOptions.Compiled)
        Private ReadOnly AnyTag As New Regex("<[^>]+>", RegexOptions.Compiled)

        ''' <summary>The fenced code blocks of an answer, in order: (number, language, code).</summary>
        Public Function Blocks(content As String) As List(Of (Index As Integer, Lang As String, Code As String))
            Dim list As New List(Of (Index As Integer, Lang As String, Code As String))
            If String.IsNullOrEmpty(content) Then Return list
            Dim i = 0
            For Each m As Match In Fence.Matches(content)
                Dim code = m.Groups(2).Value
                If code.EndsWith(vbLf, StringComparison.Ordinal) Then code = code.Substring(0, code.Length - 1)
                list.Add((i, m.Groups(1).Value.ToLowerInvariant(), code))
                i += 1
            Next
            Return list
        End Function

        ''' <summary>A block that is a page or a drawing (not a one-line fragment).</summary>
        Public Function IsArtifact(lang As String, code As String) As Boolean
            Return RunnableLangs.Contains(If(lang, "")) AndAlso code IsNot Nothing AndAlso code.Trim().Length >= 20 AndAlso code.Contains("<"c)
        End Function

        ''' <summary>The artifact's name: its &lt;title&gt;, else its first heading, else the fallback.</summary>
        Public Function TitleOf(lang As String, code As String, fallback As String) As String
            For Each re In {TitleTag, HeadingTag}
                Dim m = re.Match(If(code, ""))
                If Not m.Success Then Continue For
                Dim text = WebUtility.HtmlDecode(AnyTag.Replace(m.Groups(1).Value, "")).Trim()
                text = Regex.Replace(text, "\s+", " ")
                If text.Length > 0 Then Return If(text.Length > 80, text.Substring(0, 80).TrimEnd() & "…", text)
            Next
            Dim name = If(String.IsNullOrWhiteSpace(fallback), If(String.Equals(lang, "svg", StringComparison.OrdinalIgnoreCase), "Drawing", "Page"), fallback.Trim())
            Return If(name.Length > 80, name.Substring(0, 80).TrimEnd() & "…", name)
        End Function

        ''' <summary>A shared block as a document of its own (a fragment gets a page around it).</summary>
        Public Function DocumentOf(lang As String, code As String) As String
            Dim c = If(code, "")
            If String.Equals(lang, "svg", StringComparison.OrdinalIgnoreCase) AndAlso Not Regex.IsMatch(c, "<html[\s>]", RegexOptions.IgnoreCase) Then
                Return "<!doctype html><html><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1"">" &
                       "<style>html,body{margin:0;height:100%}body{display:flex;align-items:center;justify-content:center;background:#fff}svg{max-width:100%;max-height:100vh}</style>" &
                       "</head><body>" & c & "</body></html>"
            End If
            If Regex.IsMatch(c, "<!doctype|<html[\s>]|<head[\s>]", RegexOptions.IgnoreCase) Then Return c
            Return "<!doctype html><html><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1""></head><body>" & c & "</body></html>"
        End Function

        ''' <summary>
        ''' The artifacts in the user's chats, newest first (temporary chats are left out): every HTML or SVG block of the last
        ''' answers that have code. A block an answer repeats unchanged in the same chat is listed once (the newest).
        ''' </summary>
        Public Async Function ScanAsync(store As Database, username As String, chatId As String, maxAnswers As Integer, maxItems As Integer) As Task(Of List(Of ChatArtifact))
            Dim rows = Await store.QueryAsync(
                "SELECT m.Id AS MessageId, m.ChatId AS ChatId, m.Content AS Content, m.CreatedUtc AS CreatedUtc, c.Title AS Title " &
                "FROM ChatMessages m JOIN Chats c ON c.Id = m.ChatId " &
                "WHERE c.Username=@u AND m.Role='assistant' AND COALESCE(c.Temporary, 0)=0 AND instr(m.Content, '```') > 0 AND (@c IS NULL OR m.ChatId=@c) " &
                "ORDER BY m.CreatedUtc DESC LIMIT @n",
                Function(r) (MessageId:=Db.Str(r, "MessageId"), ChatId:=Db.Str(r, "ChatId"), Content:=Db.Str(r, "Content"), CreatedUtc:=Db.Dt(r, "CreatedUtc"), Title:=Db.Str(r, "Title")),
                ("@u", CObj(username)), ("@c", Db.Val(chatId)), ("@n", CObj(Math.Max(1, maxAnswers)))).ConfigureAwait(False)
            Dim list As New List(Of ChatArtifact)
            Dim seen As New HashSet(Of String)(StringComparer.Ordinal)
            For Each row In rows
                ' Blocks of one answer in their order (the answers are newest first).
                For Each b In Blocks(row.Content)
                    If Not IsArtifact(b.Lang, b.Code) Then Continue For
                    If Not seen.Add(row.ChatId & ":" & Hash(b.Code)) Then Continue For
                    list.Add(New ChatArtifact With {
                        .ChatId = row.ChatId, .ChatTitle = row.Title, .MessageId = row.MessageId, .Block = b.Index, .Lang = b.Lang,
                        .Title = TitleOf(b.Lang, b.Code, row.Title), .Lines = b.Code.Split(ChrW(10)).Length, .Bytes = Encoding.UTF8.GetByteCount(b.Code),
                        .CreatedUtc = row.CreatedUtc})
                    If list.Count >= maxItems Then Return list
                Next
            Next
            Return list
        End Function

        Private Function Hash(text As String) As String
            Return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).Substring(0, 24)
        End Function
    End Module

    ''' <summary>
    ''' Public links to artifacts. A chat block is kept as it was shared (later edits of the chat do not change the link); a
    ''' website is unpacked into its own folder, and sharing the same project again replaces its files under the same link.
    ''' </summary>
    Public NotInheritable Class SharedArtifactStore
        ''' <summary>Files one shared website may have.</summary>
        Public Const MaxFiles As Integer = 3000
        Private Const Columns As String = "Token, Username, Kind, Title, Lang, ChatId, MessageId, BlockIndex, Source, SourceKey, Files, Bytes, Views, CreatedUtc, UpdatedUtc"
        Private Shared ReadOnly TokenPattern As New Regex("^[A-Za-z0-9_-]{8,64}$", RegexOptions.Compiled)
        Private ReadOnly _db As Database
        Private ReadOnly _root As String

        Public Sub New(db As Database, root As String)
            _db = db
            _root = Path.GetFullPath(root)
        End Sub

        Public ReadOnly Property Root As String
            Get
                Return _root
            End Get
        End Property

        Public Shared Function IsToken(token As String) As Boolean
            Return token IsNot Nothing AndAlso TokenPattern.IsMatch(token)
        End Function

        Private Shared Function Map(r As Microsoft.Data.Sqlite.SqliteDataReader) As SharedArtifact
            Return New SharedArtifact With {
                .Token = Db.Str(r, "Token"), .Username = Db.Str(r, "Username"), .Kind = Db.Str(r, "Kind"), .Title = Db.Str(r, "Title"), .Lang = Db.Str(r, "Lang"),
                .ChatId = Db.Str(r, "ChatId"), .MessageId = Db.Str(r, "MessageId"), .BlockIndex = Db.IntN(r, "BlockIndex"), .Source = Db.Str(r, "Source"),
                .SourceKey = Db.Str(r, "SourceKey"), .Files = Db.Int(r, "Files"), .Bytes = Db.Lng(r, "Bytes"), .Views = Db.Int(r, "Views"),
                .CreatedUtc = Db.Dt(r, "CreatedUtc"), .UpdatedUtc = Db.Dt(r, "UpdatedUtc")}
        End Function

        Public Function ListAsync(username As String) As Task(Of List(Of SharedArtifact))
            Return _db.QueryAsync($"SELECT {Columns} FROM SharedArtifacts WHERE Username=@u ORDER BY CreatedUtc DESC", AddressOf Map, ("@u", CObj(username)))
        End Function

        ''' <summary>A link with its content (a chat block's document).</summary>
        Public Async Function GetAsync(token As String) As Task(Of SharedArtifact)
            If Not IsToken(token) Then Return Nothing
            Return Await _db.QuerySingleAsync($"SELECT {Columns}, Content FROM SharedArtifacts WHERE Token=@t",
                                              Function(r)
                                                  Dim a = Map(r)
                                                  a.Content = Db.Str(r, "Content")
                                                  Return a
                                              End Function, ("@t", CObj(token))).ConfigureAwait(False)
        End Function

        Public Function FindCodeAsync(username As String, chatId As String, messageId As String, block As Integer) As Task(Of SharedArtifact)
            Return _db.QuerySingleAsync($"SELECT {Columns} FROM SharedArtifacts WHERE Username=@u AND Kind='code' AND ChatId=@c AND MessageId=@m AND BlockIndex=@b",
                                        AddressOf Map, ("@u", CObj(username)), ("@c", CObj(chatId)), ("@m", CObj(messageId)), ("@b", CObj(block)))
        End Function

        Public Function FindByKeyAsync(username As String, key As String) As Task(Of SharedArtifact)
            Return _db.QuerySingleAsync($"SELECT {Columns} FROM SharedArtifacts WHERE Username=@u AND Kind='site' AND SourceKey=@k",
                                        AddressOf Map, ("@u", CObj(username)), ("@k", CObj(key)))
        End Function

        Public Function CountAsync(username As String) As Task(Of Long)
            Return _db.ScalarLongAsync("SELECT COUNT(*) FROM SharedArtifacts WHERE Username=@u", ("@u", CObj(username)))
        End Function

        Private Shared Function NewToken() As String
            Return Ids.NewToken(18)
        End Function

        Public Async Function AddCodeAsync(username As String, title As String, lang As String, code As String, chatId As String, messageId As String, block As Integer) As Task(Of SharedArtifact)
            Dim now = DateTime.UtcNow
            Dim a As New SharedArtifact With {
                .Token = NewToken(), .Username = username, .Kind = "code", .Title = title, .Lang = lang, .Content = code, .ChatId = chatId, .MessageId = messageId,
                .BlockIndex = block, .Files = 1, .Bytes = Encoding.UTF8.GetByteCount(code), .CreatedUtc = now, .UpdatedUtc = now}
            Await _db.ExecuteAsync("INSERT INTO SharedArtifacts(Token, Username, Kind, Title, Lang, Content, ChatId, MessageId, BlockIndex, Files, Bytes, Views, CreatedUtc, UpdatedUtc) " &
                                   "VALUES(@t, @u, 'code', @ti, @l, @co, @c, @m, @b, 1, @by, 0, @cr, @cr)",
                                   ("@t", CObj(a.Token)), ("@u", username), ("@ti", title), ("@l", lang), ("@co", code), ("@c", chatId), ("@m", messageId), ("@b", block),
                                   ("@by", a.Bytes), ("@cr", Db.Ts(now))).ConfigureAwait(False)
            Return a
        End Function

        ''' <summary>
        ''' A website from a zip (index.html at its top, or inside the one folder everything is in): a new link, or the new files of
        ''' the link the same project already has (key). Nothing is kept when the upload is refused.
        ''' </summary>
        Public Async Function SaveSiteAsync(username As String, title As String, source As String, key As String, zip As Byte(), maxBytes As Long) As Task(Of (Artifact As SharedArtifact, Updated As Boolean))
            Dim existing = If(String.IsNullOrWhiteSpace(key), Nothing, Await FindByKeyAsync(username, key).ConfigureAwait(False))
            Dim token = If(existing?.Token, NewToken())
            Directory.CreateDirectory(_root)
            Dim staging = Path.Combine(_root, ".new-" & token & "-" & Guid.NewGuid().ToString("N").Substring(0, 8))
            Dim unpacked As (Files As Integer, Bytes As Long)
            Try
                unpacked = Unpack(zip, staging, maxBytes)
                Dim target = Path.Combine(_root, token)
                If Directory.Exists(target) Then Directory.Delete(target, True)
                Directory.Move(staging, target)
            Catch
                TryDelete(staging)
                Throw
            End Try
            Dim now = DateTime.UtcNow
            If existing IsNot Nothing Then
                Await _db.ExecuteAsync("UPDATE SharedArtifacts SET Title=@ti, Source=@s, Files=@f, Bytes=@by, UpdatedUtc=@n WHERE Token=@t",
                                       ("@ti", CObj(title)), ("@s", Db.Val(source)), ("@f", unpacked.Files), ("@by", unpacked.Bytes), ("@n", Db.Ts(now)), ("@t", token)).ConfigureAwait(False)
                existing.Title = title
                existing.Source = source
                existing.Files = unpacked.Files
                existing.Bytes = unpacked.Bytes
                existing.UpdatedUtc = now
                Return (existing, True)
            End If
            Dim a As New SharedArtifact With {.Token = token, .Username = username, .Kind = "site", .Title = title, .Source = source, .SourceKey = key,
                                              .Files = unpacked.Files, .Bytes = unpacked.Bytes, .CreatedUtc = now, .UpdatedUtc = now}
            Await _db.ExecuteAsync("INSERT INTO SharedArtifacts(Token, Username, Kind, Title, Source, SourceKey, Files, Bytes, Views, CreatedUtc, UpdatedUtc) " &
                                   "VALUES(@t, @u, 'site', @ti, @s, @k, @f, @by, 0, @cr, @cr)",
                                   ("@t", CObj(token)), ("@u", username), ("@ti", title), ("@s", Db.Val(source)), ("@k", Db.Val(If(String.IsNullOrWhiteSpace(key), Nothing, key))),
                                   ("@f", unpacked.Files), ("@by", unpacked.Bytes), ("@cr", Db.Ts(now))).ConfigureAwait(False)
            Return (a, False)
        End Function

        ''' <summary>Unpacks the zip into the folder: at most maxBytes in all and MaxFiles files, nothing outside the folder.</summary>
        Private Shared Function Unpack(zip As Byte(), folder As String, maxBytes As Long) As (Files As Integer, Bytes As Long)
            Using ms As New MemoryStream(zip, writable:=False), archive As New ZipArchive(ms, ZipArchiveMode.Read)
                Dim entries = archive.Entries.Where(Function(e) e.Name.Length > 0 AndAlso Not e.FullName.StartsWith("__MACOSX/", StringComparison.Ordinal)).ToList()
                If entries.Count = 0 Then Throw New InvalidDataException("The upload has no files.")
                If entries.Count > MaxFiles Then Throw New InvalidDataException($"A shared website may have at most {MaxFiles} files; this one has {entries.Count}.")
                Dim names = entries.Select(Function(e) e.FullName.Replace("\"c, "/"c).TrimStart("/"c)).ToList()
                ' Zipping a folder puts everything in it: its index.html is the site's.
                Dim prefix = ""
                If Not names.Any(Function(n) n.Equals("index.html", StringComparison.OrdinalIgnoreCase)) Then
                    Dim tops = names.Select(Function(n) If(n.Contains("/"c), n.Substring(0, n.IndexOf("/"c) + 1), "")).Distinct().ToList()
                    If tops.Count = 1 AndAlso tops(0).Length > 0 AndAlso names.Any(Function(n) n.Equals(tops(0) & "index.html", StringComparison.OrdinalIgnoreCase)) Then
                        prefix = tops(0)
                    Else
                        Throw New InvalidDataException("A shared website needs an index.html at its top.")
                    End If
                End If
                If entries.Sum(Function(e) e.Length) > maxBytes Then Throw New InvalidDataException($"The website is larger than {maxBytes \ (1024 * 1024)} MB unpacked.")
                Directory.CreateDirectory(folder)
                Dim baseDir = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar
                Dim total As Long = 0
                Dim count = 0
                Dim buffer(81919) As Byte
                For i = 0 To entries.Count - 1
                    If Not names(i).StartsWith(prefix, StringComparison.Ordinal) Then Continue For
                    Dim rel = names(i).Substring(prefix.Length)
                    Dim parts = rel.Split("/"c)
                    If rel.Length = 0 OrElse parts.Any(Function(p) p.Length = 0 OrElse p = "." OrElse p = ".." OrElse p.IndexOf(":"c) >= 0 OrElse p.Any(Function(ch) AscW(ch) < 32)) Then
                        Throw New InvalidDataException("The upload has a file name that is not allowed: " & rel)
                    End If
                    Dim full = Path.GetFullPath(Path.Combine(folder, rel.Replace("/"c, Path.DirectorySeparatorChar)))
                    If Not full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase) Then Throw New InvalidDataException("The upload has a file outside its folder: " & rel)
                    Directory.CreateDirectory(Path.GetDirectoryName(full))
                    Using src = entries(i).Open(), dst = File.Create(full)
                        ' Counted while it is written: a zip that understates its sizes still stops at the limit.
                        Do
                            Dim n = src.Read(buffer, 0, buffer.Length)
                            If n <= 0 Then Exit Do
                            total += n
                            If total > maxBytes Then Throw New InvalidDataException($"The website is larger than {maxBytes \ (1024 * 1024)} MB unpacked.")
                            dst.Write(buffer, 0, n)
                        Loop
                    End Using
                    count += 1
                Next
                Return (count, total)
            End Using
        End Function

        ''' <summary>Removes a link (only the user's own, unless username is Nothing) and its files.</summary>
        Public Async Function DeleteAsync(token As String, username As String) As Task(Of Boolean)
            If Not IsToken(token) Then Return False
            Dim n = Await _db.ExecuteAsync("DELETE FROM SharedArtifacts WHERE Token=@t AND (@u IS NULL OR Username=@u)", ("@t", CObj(token)), ("@u", Db.Val(username))).ConfigureAwait(False)
            If n > 0 Then TryDelete(Path.Combine(_root, token))
            Return n > 0
        End Function

        Public Function AddViewAsync(token As String) As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE SharedArtifacts SET Views=Views+1 WHERE Token=@t", ("@t", CObj(token)))
        End Function

        ''' <summary>A file of a shared website (a folder serves its index.html); Nothing when there is none or the path leaves the site.</summary>
        Public Function SiteFile(token As String, relativePath As String) As String
            If Not IsToken(token) Then Return Nothing
            Dim folder = Path.GetFullPath(Path.Combine(_root, token))
            If Not Directory.Exists(folder) Then Return Nothing
            Dim clean = If(relativePath, "").Replace("\"c, "/"c).TrimStart("/"c)
            If clean.IndexOf(":"c) >= 0 OrElse clean.IndexOf(ChrW(0)) >= 0 Then Return Nothing
            Dim full = Path.GetFullPath(Path.Combine(folder, clean.Replace("/"c, Path.DirectorySeparatorChar)))
            If Not full.Equals(folder, StringComparison.OrdinalIgnoreCase) AndAlso
               Not full.StartsWith(folder & Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) Then Return Nothing
            If Directory.Exists(full) Then full = Path.Combine(full, "index.html")
            Return If(File.Exists(full), full, Nothing)
        End Function

        Private Shared Sub TryDelete(folder As String)
            Try
                If Directory.Exists(folder) Then Directory.Delete(folder, True)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
        End Sub
    End Class

End Namespace
