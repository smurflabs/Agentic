Option Strict On
Option Explicit On
Option Infer On

Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes

Namespace Mcp

    ''' <summary>
    ''' File contents an MCP server returns base64-encoded (Gitea's and GitHub's get_file_contents: {"content": "…",
    ''' "encoding": "base64"}) are decoded to text before a model sees them. Models cannot read base64, and copying it into a
    ''' decode tool corrupts it (a LedgerLite review judged a pyproject.toml that came out garbled that way); the text is
    ''' also a quarter smaller. Binary files (not valid UTF-8, or with NUL bytes) stay encoded.
    ''' </summary>
    Public Module McpFileContent

        Private ReadOnly JsonOut As New JsonSerializerOptions With {.Encoder = Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}

        Public Sub DecodeInPlace(result As McpCallResult)
            If result Is Nothing OrElse result.IsError Then Return
            result.Text = DecodeText(result.Text)
            result.StructuredJson = DecodeText(result.StructuredJson)
        End Sub

        ''' <summary>The JSON text with every base64 file content in it decoded; anything else comes back unchanged.</summary>
        Public Function DecodeText(text As String) As String
            If String.IsNullOrEmpty(text) OrElse text.IndexOf("base64", StringComparison.OrdinalIgnoreCase) < 0 Then Return text
            Dim t = text.TrimStart()
            If Not (t.StartsWith("{"c) OrElse t.StartsWith("["c)) Then Return text
            Dim node As JsonNode
            Try
                node = JsonNode.Parse(text)
            Catch ex As JsonException
                Return text
            End Try
            If node Is Nothing OrElse Not Decode(node) Then Return text
            Return node.ToJsonString(JsonOut)
        End Function

        Private Function Decode(node As JsonNode) As Boolean
            Dim changed = False
            Dim obj = TryCast(node, JsonObject)
            If obj IsNot Nothing Then
                Dim enc As String = Nothing, content As String = Nothing
                If TryString(obj("encoding"), enc) AndAlso TryString(obj("content"), content) AndAlso String.Equals(enc, "base64", StringComparison.OrdinalIgnoreCase) Then
                    Dim decoded = TryDecode(content)
                    If decoded IsNot Nothing Then
                        obj("content") = JsonValue.Create(decoded)
                        obj("encoding") = JsonValue.Create("utf-8 (decoded from base64 by the host)")
                        changed = True
                    End If
                End If
                For Each kv In obj.ToList()
                    If kv.Value IsNot Nothing AndAlso kv.Key <> "content" AndAlso Decode(kv.Value) Then changed = True
                Next
            End If
            Dim arr = TryCast(node, JsonArray)
            If arr IsNot Nothing Then
                For Each item In arr
                    If item IsNot Nothing AndAlso Decode(item) Then changed = True
                Next
            End If
            Return changed
        End Function

        Private Function TryString(node As JsonNode, ByRef value As String) As Boolean
            Dim v = TryCast(node, JsonValue)
            Return v IsNot Nothing AndAlso v.TryGetValue(Of String)(value) AndAlso value IsNot Nothing
        End Function

        Private Function TryDecode(b64 As String) As String
            Dim bytes As Byte()
            Try
                bytes = Convert.FromBase64String(b64.Replace(vbLf, "").Replace(vbCr, ""))
            Catch ex As FormatException
                Return Nothing
            End Try
            If Array.IndexOf(bytes, CByte(0)) >= 0 Then Return Nothing
            Try
                Return New UTF8Encoding(False, True).GetString(bytes)
            Catch ex As DecoderFallbackException
                Return Nothing
            End Try
        End Function
    End Module

End Namespace
