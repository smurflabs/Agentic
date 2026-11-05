Option Strict On
Option Explicit On
Option Infer On

Imports System.Text
Imports System.Text.Json.Nodes
Imports System.Text.Json.Serialization
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Agentic.Utilities

Namespace Work

    ''' <summary>
    ''' What a subagent did, in order: its writing, its thinking and its tool calls. Shown live in the subagent's card (Chat tab,
    ''' desktop app, VS Code), kept in the live answer for windows that open the chat mid-answer, and saved with the card's tool
    ''' call afterwards. Long subagents keep their beginning; the live view still shows everything as it happens.
    ''' </summary>
    Public Class SubagentTranscript
        Public Const MaxTextChars As Integer = 16000
        Public Const MaxItems As Integer = 240

        Public Property Label As String
        ''' <summary>explore | general (or the CLI's subagent type)</summary>
        <JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingNull)>
        Public Property Kind As String
        Public Property Done As Boolean
        Public Property Ok As Boolean
        ''' <summary>Text or items were left out (over MaxTextChars / MaxItems).</summary>
        Public Property Clipped As Boolean
        Public Property Items As New List(Of SubagentItem)

        Private _textChars As Integer

        ''' <summary>Applies one subagent event (see SubagentStream) to the transcript. Callers lock it.</summary>
        Public Sub Apply(e As JsonNode)
            If e Is Nothing Then Return
            Select Case e("kind")?.ToString()
                Case "start"
                    Label = If(e("label")?.ToString(), Label)
                    Kind = If(e("type")?.ToString(), Kind)
                Case "text", "reasoning"
                    Dim t = e("text")?.ToString()
                    If String.IsNullOrEmpty(t) Then Return
                    Dim k = e("kind").ToString()
                    If _textChars + t.Length > MaxTextChars Then
                        Clipped = True
                        Dim room = Math.Max(0, MaxTextChars - _textChars)
                        If room = 0 Then Return
                        t = t.Substring(0, room)
                    End If
                    _textChars += t.Length
                    Dim last = Items.LastOrDefault()
                    If last IsNot Nothing AndAlso last.Kind = k Then
                        last.Text &= t
                    ElseIf Items.Count < MaxItems Then
                        Items.Add(New SubagentItem With {.Kind = k, .Text = t})
                    Else
                        Clipped = True
                    End If
                Case "tool"
                    Dim c = e("call")
                    Dim id = c?("id")?.ToString()
                    Dim existing = If(id Is Nothing, Nothing, Items.LastOrDefault(Function(i) i.Kind = "tool" AndAlso i.Id = id))
                    If existing Is Nothing Then
                        If Items.Count >= MaxItems Then Clipped = True : Return
                        existing = New SubagentItem With {.Kind = "tool", .Id = id, .Name = c?("name")?.ToString(), .Target = c?("target")?.ToString()}
                        Items.Add(existing)
                    End If
                    If e("phase")?.ToString() = "end" Then
                        existing.Ok = String.Equals(c?("ok")?.ToString(), "true", StringComparison.OrdinalIgnoreCase)
                        existing.Result = c?("result")?.ToString()
                        existing.Meta = c?("meta")?.ToString()
                    End If
                Case "note"
                    If Items.Count < MaxItems Then Items.Add(New SubagentItem With {.Kind = "note", .Text = e("text")?.ToString()})
                Case "end"
                    Done = True
                    Ok = String.Equals(e("ok")?.ToString(), "true", StringComparison.OrdinalIgnoreCase)
            End Select
        End Sub

        Public Function Copy() As SubagentTranscript
            Return New SubagentTranscript With {.Label = Label, .Kind = Kind, .Done = Done, .Ok = Ok, .Clipped = Clipped,
                                                .Items = Items.Select(Function(i) i.Copy()).ToList()}
        End Function
    End Class

    Public Class SubagentItem
        ''' <summary>text | reasoning | tool | note</summary>
        Public Property Kind As String
        <JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingNull)>
        Public Property Text As String
        <JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingNull)>
        Public Property Id As String
        <JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingNull)>
        Public Property Name As String
        ''' <summary>A tool call's file, command, query or address, shortly.</summary>
        <JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingNull)>
        Public Property Target As String
        ''' <summary>A tool call's outcome; Nothing while it runs.</summary>
        <JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingNull)>
        Public Property Ok As Boolean?
        ''' <summary>The start of a tool call's result.</summary>
        <JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingNull)>
        Public Property Result As String
        ''' <summary>What came of it in a few words ("17 lines", "exit 0"), from its whole result.</summary>
        <JsonIgnore(Condition:=JsonIgnoreCondition.WhenWritingNull)>
        Public Property Meta As String

        Public Function Copy() As SubagentItem
            Return DirectCast(MemberwiseClone(), SubagentItem)
        End Function
    End Class

    ''' <summary>
    ''' A running subagent's stream, sent as "subagent" events for its card (call id): {id, kind: start|text|reasoning|tool|note|end, ...}.
    ''' Text and thinking arrive token by token; they are sent in small batches (every ~100 ms) so several subagents at once stay light.
    ''' </summary>
    Public NotInheritable Class SubagentStream
        Private Const BatchMs As Integer = 100
        Private Const BatchChars As Integer = 600
        ''' <summary>How much of each tool result the card keeps: its first line on the step's row, the rest when the row is opened.</summary>
        Public Const ResultChars As Integer = 800

        Private ReadOnly _id As String
        Private ReadOnly _emit As Func(Of String, Object, Task)
        Private ReadOnly _gate As New SemaphoreSlim(1, 1)
        Private ReadOnly _pending As New StringBuilder()
        Private _pendingKind As String
        Private _lastSent As DateTime = DateTime.MinValue
        Private _ended As Boolean

        ''' <summary>Everything so far (for the saved tool call).</summary>
        Public ReadOnly Property Transcript As New SubagentTranscript()

        ''' <param name="id">The call id of the subagent's card (the task call).</param>
        ''' <param name="emit">Where the events go (the answer's stream, or the live hub for a subagent of another chat).</param>
        Public Sub New(id As String, emit As Func(Of String, Object, Task))
            _id = id
            _emit = emit
        End Sub

        Public ReadOnly Property Id As String
            Get
                Return _id
            End Get
        End Property

        Public Function StartAsync(label As String, type As String) As Task
            Return SendAsync(New JsonObject From {{"kind", "start"}, {"label", label}, {"type", type}})
        End Function

        ''' <summary>Text or thinking (kind "text" | "reasoning"), batched.</summary>
        Public Async Function WriteAsync(kind As String, text As String) As Task
            If String.IsNullOrEmpty(text) OrElse _ended Then Return
            Dim flush As JsonObject = Nothing
            Await _gate.WaitAsync().ConfigureAwait(False)
            Try
                If _pendingKind IsNot Nothing AndAlso _pendingKind <> kind AndAlso _pending.Length > 0 Then flush = TakePendingLocked()
                _pendingKind = kind
                _pending.Append(text)
            Finally
                _gate.Release()
            End Try
            If flush IsNot Nothing Then Await SendAsync(flush).ConfigureAwait(False)
            If _pending.Length >= BatchChars OrElse (DateTime.UtcNow - _lastSent).TotalMilliseconds >= BatchMs Then Await FlushAsync().ConfigureAwait(False)
        End Function

        Public Async Function FlushAsync() As Task
            Dim e As JsonObject = Nothing
            Await _gate.WaitAsync().ConfigureAwait(False)
            Try
                If _pending.Length > 0 Then e = TakePendingLocked()
            Finally
                _gate.Release()
            End Try
            If e IsNot Nothing Then Await SendAsync(e).ConfigureAwait(False)
        End Function

        Private Function TakePendingLocked() As JsonObject
            Dim e As New JsonObject From {{"kind", _pendingKind}, {"text", _pending.ToString()}}
            _pending.Clear()
            Return e
        End Function

        Public Async Function ToolStartAsync(callId As String, name As String, target As String) As Task
            Await FlushAsync().ConfigureAwait(False)
            Await SendAsync(New JsonObject From {{"kind", "tool"}, {"phase", "start"},
                                                 {"call", New JsonObject From {{"id", callId}, {"name", name}, {"target", TextUtil.Truncate(target, 160)}}}}).ConfigureAwait(False)
        End Function

        Public Async Function ToolEndAsync(callId As String, name As String, target As String, ok As Boolean, result As String) As Task
            Dim c As New JsonObject From {{"id", callId}, {"name", name}, {"target", TextUtil.Truncate(target, 160)}, {"ok", ok},
                                          {"result", TextUtil.Truncate(If(result, "").Trim(), ResultChars)}}
            Dim meta = StepMeta(name, ok, result)
            If meta IsNot Nothing Then c("meta") = meta
            Await SendAsync(New JsonObject From {{"kind", "tool"}, {"phase", "end"}, {"call", c}}).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' What came of a step, in a few words, from its whole result (the card keeps only its start): "17 lines" for a read,
        ''' "3 results" for a search or listing, "exit 0" for a command; Nothing when there is nothing short to say.
        ''' </summary>
        Public Shared Function StepMeta(name As String, ok As Boolean, result As String) As String
            If Not ok OrElse String.IsNullOrWhiteSpace(result) Then Return Nothing
            Dim text = result.Trim()
            Dim lines = text.Split(ControlChars.Lf).Length
            Select Case name
                Case "read_file", "Read", "read_page", "WebFetch", "fetch_url"
                    Return $"{lines} line{If(lines = 1, "", "s")}"
                Case "grep", "Grep", "glob_files", "Glob", "list_dir", "LS"
                    If Regex.IsMatch(text, "^(no matches|no files|nothing found|no results|\(empty)", RegexOptions.IgnoreCase) Then Return "nothing found"
                    Return $"{lines} result{If(lines = 1, "", "s")}"
                Case "run_command", "Bash", "PowerShell", "command_output", "BashOutput"
                    Dim m = Regex.Match(text, "exit code[:\s]+(-?\d+)", RegexOptions.IgnoreCase)
                    Return If(m.Success, "exit " & m.Groups(1).Value, Nothing)
            End Select
            Return Nothing
        End Function

        Public Async Function NoteAsync(text As String) As Task
            Await FlushAsync().ConfigureAwait(False)
            Await SendAsync(New JsonObject From {{"kind", "note"}, {"text", text}}).ConfigureAwait(False)
        End Function

        ''' <summary>The subagent is done (once).</summary>
        Public Async Function EndAsync(ok As Boolean) As Task
            If _ended Then Return
            Await FlushAsync().ConfigureAwait(False)
            Await SendAsync(New JsonObject From {{"kind", "end"}, {"ok", ok}}).ConfigureAwait(False)
            _ended = True
        End Function

        Private Async Function SendAsync(e As JsonObject) As Task
            e("id") = _id
            SyncLock Transcript
                Transcript.Apply(e)
            End SyncLock
            _lastSent = DateTime.UtcNow
            Try
                Await _emit("subagent", e).ConfigureAwait(False)
            Catch ex As Exception When TypeOf ex Is IO.IOException OrElse TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is OperationCanceledException OrElse
                                       TypeOf ex Is InvalidOperationException
                ' The window went away: the subagent keeps working, its transcript is still kept.
            End Try
        End Function

    End Class

    ''' <summary>
    ''' Splits a model's streamed text into writing and thinking when it thinks inside &lt;think&gt; tags (some models do, instead
    ''' of reasoning_content). Tags can arrive split across chunks.
    ''' </summary>
    Public NotInheritable Class ThinkTagSplitter
        Private ReadOnly _hold As New StringBuilder()
        Private _inThink As Boolean

        ''' <summary>The parts of a chunk, in order: (isThinking, text).</summary>
        Public Function Feed(chunk As String) As List(Of (Think As Boolean, Text As String))
            Dim parts As New List(Of (Think As Boolean, Text As String))
            _hold.Append(chunk)
            Dim s = _hold.ToString()
            _hold.Clear()
            Dim pos = 0
            While pos < s.Length
                Dim tag = If(_inThink, "</think>", "<think>")
                Dim at = s.IndexOf(tag, pos, StringComparison.OrdinalIgnoreCase)
                If at >= 0 Then
                    If at > pos Then parts.Add((_inThink, s.Substring(pos, at - pos)))
                    _inThink = Not _inThink
                    pos = at + tag.Length
                    Continue While
                End If
                ' No whole tag: keep a possible start of one for the next chunk.
                Dim keep = 0
                For n = Math.Min(tag.Length - 1, s.Length - pos) To 1 Step -1
                    If tag.StartsWith(s.Substring(s.Length - n), StringComparison.OrdinalIgnoreCase) Then keep = n : Exit For
                Next
                If s.Length - keep > pos Then parts.Add((_inThink, s.Substring(pos, s.Length - keep - pos)))
                If keep > 0 Then _hold.Append(s.Substring(s.Length - keep))
                Exit While
            End While
            Return parts
        End Function

        ''' <summary>What was held back at the end (a lone "&lt;" that started no tag).</summary>
        Public Function Finish() As (Think As Boolean, Text As String)
            Dim rest = _hold.ToString()
            _hold.Clear()
            Return (_inThink, rest)
        End Function
    End Class

End Namespace
