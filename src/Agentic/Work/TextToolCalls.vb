Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions

Namespace Work

    ''' <summary>
    ''' Tool calls a model wrote as text instead of making them (some open models do, now and then): Qwen's XML form
    ''' (&lt;tool_call&gt;&lt;function=name&gt;&lt;parameter=p&gt;value&lt;/parameter&gt;&lt;/function&gt;&lt;/tool_call&gt;) and the Hermes JSON form
    ''' (&lt;tool_call&gt;{"name": …, "arguments": {…}}&lt;/tool_call&gt;). Only tools that were offered count; the text before the
    ''' first call is what the model said.
    ''' </summary>
    Public NotInheritable Class TextToolCalls

        Private Sub New()
        End Sub

        Private Shared ReadOnly XmlCall As New Regex("<function\s*=\s*([\w.\-]+)\s*>(.*?)(?:</function\s*>|$)", RegexOptions.Singleline Or RegexOptions.IgnoreCase)
        Private Shared ReadOnly XmlParam As New Regex("<parameter\s*=\s*([\w.\-]+)\s*>(.*?)(?:</parameter\s*>|(?=<parameter\s*=)|$)", RegexOptions.Singleline Or RegexOptions.IgnoreCase)
        Private Shared ReadOnly JsonCall As New Regex("<tool_call>\s*(\{.*?\})\s*(?:</tool_call>|$)", RegexOptions.Singleline Or RegexOptions.IgnoreCase)
        Private Shared ReadOnly Scalar As New Regex("^(true|false|null|-?\d+(\.\d+)?)$", RegexOptions.IgnoreCase)

        Public Shared Function Extract(text As String, offered As IEnumerable(Of String)) As (Before As String, Calls As List(Of (Name As String, Args As String)))
            Dim found As New List(Of (Name As String, Args As String))
            text = If(text, "")
            Dim names = offered.Where(Function(n) Not String.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(Function(n) n, Function(n) n, StringComparer.OrdinalIgnoreCase)
            If names.Count = 0 OrElse (text.IndexOf("<function", StringComparison.OrdinalIgnoreCase) < 0 AndAlso text.IndexOf("<tool_call>", StringComparison.OrdinalIgnoreCase) < 0) Then Return (text, found)
            Dim first = Integer.MaxValue
            For Each m As Match In XmlCall.Matches(text)
                Dim name As String = Nothing
                If Not names.TryGetValue(m.Groups(1).Value, name) Then Continue For
                Dim args As New JsonObject()
                For Each p As Match In XmlParam.Matches(m.Groups(2).Value)
                    args(p.Groups(1).Value) = ParamValue(p.Groups(2).Value)
                Next
                found.Add((name, args.ToJsonString()))
                first = Math.Min(first, CallStart(text, m.Index))
            Next
            If found.Count = 0 Then
                For Each m As Match In JsonCall.Matches(text)
                    Dim node As JsonNode = Nothing
                    Try
                        node = JsonNode.Parse(m.Groups(1).Value)
                    Catch ex As JsonException
                        Continue For
                    End Try
                    Dim name As String = Nothing
                    If Not names.TryGetValue(If(node?("name")?.ToString(), ""), name) Then Continue For
                    Dim a = If(node("arguments"), node("parameters"))
                    Dim argsText = If(a Is Nothing, "{}", If(TypeOf a Is JsonValue AndAlso a.GetValueKind() = JsonValueKind.String, a.ToString(), a.ToJsonString()))
                    found.Add((name, argsText))
                    first = Math.Min(first, m.Index)
                Next
            End If
            If found.Count = 0 Then Return (text, found)
            Return (text.Substring(0, Math.Min(first, text.Length)).TrimEnd(), found)
        End Function

        ''' <summary>Where the call starts: at its &lt;tool_call&gt; wrapper when it has one.</summary>
        Private Shared Function CallStart(text As String, functionAt As Integer) As Integer
            Dim wrap = text.LastIndexOf("<tool_call>", functionAt, StringComparison.OrdinalIgnoreCase)
            If wrap >= 0 AndAlso text.Substring(wrap + 11, functionAt - wrap - 11).Trim().Length = 0 Then Return wrap
            Return functionAt
        End Function

        ''' <summary>A parameter's value: JSON when it is an object, a list, a number or true/false; otherwise the text as written.</summary>
        Private Shared Function ParamValue(raw As String) As JsonNode
            Dim v = raw.Trim(ChrW(13), ChrW(10))
            Dim t = v.Trim()
            If t.StartsWith("{") OrElse t.StartsWith("[") OrElse Scalar.IsMatch(t) Then
                Try
                    Dim n = JsonNode.Parse(t)
                    If n IsNot Nothing Then Return n
                Catch ex As JsonException
                End Try
            End If
            Return JsonValue.Create(v)
        End Function

    End Class

End Namespace
