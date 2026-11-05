Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text
Imports System.Text.Encodings.Web
Imports System.Text.Json
Imports System.Text.Json.Nodes

Namespace Work

    ''' <summary>
    ''' Some models (qwen among them) now and then write source code HTML-escaped in a tool call, "If a &amp;lt; b" instead of
    ''' "If a &lt; b". The file then does not compile, and every rewrite brings the escapes back, so the model loops without
    ''' progress. Before a file tool runs, &amp;lt; &amp;gt; and &amp;amp; in a code file's text are turned back into &lt; &gt; and
    ''' &amp;, but only outside string literals and comments: code that escapes HTML on purpose (s.Replace("&lt;", "&amp;lt;"))
    ''' keeps its strings.
    ''' </summary>
    Public NotInheritable Class CodeEntities

        Private Sub New()
        End Sub

        ''' <summary>Source files only: markup (html, xml, jsx, php, razor, svg, md) uses entities on purpose.</summary>
        Private Shared ReadOnly CodeExtensions As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
            ".vb", ".cs", ".fs", ".fsx", ".py", ".pyw", ".js", ".mjs", ".cjs", ".ts", ".mts", ".cts", ".java", ".kt", ".kts", ".go", ".rs",
            ".c", ".h", ".cpp", ".hpp", ".cc", ".cxx", ".hh", ".swift", ".rb", ".lua", ".ps1", ".psm1", ".psd1", ".sh", ".bash", ".zsh",
            ".sql", ".r", ".dart", ".scala", ".pl", ".pm", ".groovy"}
        Private Shared ReadOnly HashComments As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
            ".py", ".pyw", ".rb", ".ps1", ".psm1", ".psd1", ".sh", ".bash", ".zsh", ".pl", ".pm", ".r"}
        Private Shared ReadOnly PathKeys As String() = {"path", "file_path", "filePath", "filename", "file", "target_file", "targetFile"}
        Private Shared ReadOnly TextKeys As New HashSet(Of String)(StringComparer.Ordinal) From {
            "content", "new_string", "old_string", "new_str", "old_str", "new_text", "old_text", "text", "code", "replacement", "contents"}
        Private Shared ReadOnly Relaxed As New JsonSerializerOptions With {.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping}

        ''' <summary>The tool call's arguments with escaped code repaired, or Nothing when there was nothing to repair.</summary>
        Public Shared Function FixToolArguments(argsJson As String) As String
            If String.IsNullOrEmpty(argsJson) OrElse argsJson.IndexOf("&"c) < 0 Then Return Nothing
            Dim node As JsonNode
            Try
                node = JsonNode.Parse(argsJson)
            Catch ex As JsonException
                Return Nothing
            End Try
            Dim changed = False
            Visit(node, Nothing, changed)
            Return If(changed, node.ToJsonString(Relaxed), Nothing)
        End Function

        Private Shared Sub Visit(node As JsonNode, inheritedPath As String, ByRef changed As Boolean)
            Dim obj = TryCast(node, JsonObject)
            If obj IsNot Nothing Then
                Dim path = inheritedPath
                For Each k In PathKeys
                    Dim v = TryCast(obj(k), JsonValue)
                    Dim s As String = Nothing
                    If v IsNot Nothing AndAlso v.TryGetValue(s) AndAlso Not String.IsNullOrWhiteSpace(s) Then path = s : Exit For
                Next
                Dim ext = ExtensionOf(path)
                For Each kv In obj.ToList()
                    Dim text As String = Nothing
                    If ext IsNot Nothing AndAlso TextKeys.Contains(kv.Key) AndAlso TypeOf kv.Value Is JsonValue AndAlso DirectCast(kv.Value, JsonValue).TryGetValue(text) Then
                        Dim repaired = Decode(text, ext)
                        If repaired <> text Then obj(kv.Key) = repaired : changed = True
                    ElseIf TypeOf kv.Value Is JsonObject OrElse TypeOf kv.Value Is JsonArray Then
                        Visit(kv.Value, path, changed)
                    End If
                Next
                Return
            End If
            Dim arr = TryCast(node, JsonArray)
            If arr Is Nothing Then Return
            For Each item In arr
                If item IsNot Nothing Then Visit(item, inheritedPath, changed)
            Next
        End Sub

        Private Shared Function ExtensionOf(path As String) As String
            If String.IsNullOrWhiteSpace(path) Then Return Nothing
            Dim ext = IO.Path.GetExtension(path.Trim())
            Return If(CodeExtensions.Contains(ext), ext.ToLowerInvariant(), Nothing)
        End Function

        ''' <summary>Turns &amp;lt; &amp;gt; &amp;amp; back into characters outside the language's strings and comments.</summary>
        Public Shared Function Decode(code As String, ext As String) As String
            If String.IsNullOrEmpty(code) OrElse code.IndexOf("&"c) < 0 Then Return code
            Dim vb = ext = ".vb"
            Dim hash = HashComments.Contains(ext)
            Dim sql = ext = ".sql"
            Dim slash = Not vb AndAlso Not hash AndAlso Not sql
            Dim template = ext = ".js" OrElse ext = ".mjs" OrElse ext = ".cjs" OrElse ext = ".ts" OrElse ext = ".mts" OrElse ext = ".cts" OrElse ext = ".go"
            Dim sb As New StringBuilder(code.Length)
            Dim quote As Char = Nothing         ' inside a string that started with this character
            Dim blockComment = False             ' /* ... */
            Dim lineComment = False
            Dim i = 0
            While i < code.Length
                Dim c = code(i)
                Dim nxt = If(i + 1 < code.Length, code(i + 1), ChrW(0))
                If c = vbLf(0) Then
                    lineComment = False
                    If quote <> "`"c Then quote = Nothing      ' plain strings end with the line; template strings do not
                    sb.Append(c) : i += 1 : Continue While
                End If
                If lineComment Then sb.Append(c) : i += 1 : Continue While
                If blockComment Then
                    sb.Append(c)
                    If c = "*"c AndAlso nxt = "/"c Then sb.Append(nxt) : i += 2 : blockComment = False : Continue While
                    i += 1 : Continue While
                End If
                If quote <> Nothing Then
                    sb.Append(c)
                    If c = "\"c AndAlso Not vb AndAlso nxt <> ChrW(0) Then sb.Append(nxt) : i += 2 : Continue While
                    If c = quote Then quote = Nothing
                    i += 1 : Continue While
                End If
                ' Code: comments and strings start here; entities are repaired here.
                If (vb AndAlso c = "'"c) OrElse (hash AndAlso c = "#"c) OrElse ((slash OrElse template) AndAlso c = "/"c AndAlso nxt = "/"c) OrElse (sql AndAlso c = "-"c AndAlso nxt = "-"c) Then
                    lineComment = True : sb.Append(c) : i += 1 : Continue While
                End If
                If slash AndAlso c = "/"c AndAlso nxt = "*"c Then blockComment = True : sb.Append(c).Append(nxt) : i += 2 : Continue While
                If c = """"c OrElse (c = "'"c AndAlso Not vb) OrElse (c = "`"c AndAlso template) Then quote = c : sb.Append(c) : i += 1 : Continue While
                If c = "&"c Then
                    If String.CompareOrdinal(code, i, "&lt;", 0, 4) = 0 Then sb.Append("<"c) : i += 4 : Continue While
                    If String.CompareOrdinal(code, i, "&gt;", 0, 4) = 0 Then sb.Append(">"c) : i += 4 : Continue While
                    If String.CompareOrdinal(code, i, "&amp;", 0, 5) = 0 Then sb.Append("&"c) : i += 5 : Continue While
                End If
                sb.Append(c)
                i += 1
            End While
            Return sb.ToString()
        End Function

    End Class

End Namespace
