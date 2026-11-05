Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent

Namespace Work

    ''' <summary>
    ''' A short memory of which chat/user last used the code sandbox, so a long-lived process or container that appears there
    ''' can be attributed to an owner (for notifying the right chat when it is stopped). Attribution is deliberately
    ''' conservative: it names an owner only when a single chat used the sandbox in the window, so one user is never told about
    ''' another user's resource.
    ''' </summary>
    Public NotInheritable Class SandboxActivity
        Public Structure Owner
            Public User As String
            Public ChatId As String
            Public AgentId As String
            Public At As DateTime
        End Structure

        Private ReadOnly _recent As New ConcurrentQueue(Of Owner)
        Private Const Keep As Integer = 200

        ''' <summary>Records that a chat (or agent) just used the sandbox.</summary>
        Public Sub Note(user As String, chatId As String, Optional agentId As String = Nothing, Optional now As DateTime = Nothing)
            If String.IsNullOrEmpty(user) AndAlso String.IsNullOrEmpty(agentId) Then Return
            _recent.Enqueue(New Owner With {.User = user, .ChatId = chatId, .AgentId = agentId, .At = If(now = Nothing, DateTime.UtcNow, now)})
            Dim dummy As Owner = New Owner()
            While _recent.Count > Keep AndAlso _recent.TryDequeue(dummy)
            End While
        End Sub

        ''' <summary>
        ''' The owner to attribute a resource first seen at <paramref name="asOf"/> to: the single distinct chat/user that used
        ''' the sandbox within <paramref name="window"/> before it. Nothing if none, or if more than one did (ambiguous).
        ''' </summary>
        Public Function Attribute(asOf As DateTime, window As TimeSpan) As Owner?
            Dim candidates = _recent.ToArray().Where(Function(o) o.At <= asOf.AddSeconds(5) AndAlso asOf - o.At <= window).ToList()
            If candidates.Count = 0 Then Return Nothing
            Dim distinct = candidates.Select(Function(o) (If(o.User, ""), If(o.ChatId, ""), If(o.AgentId, ""))).Distinct().ToList()
            If distinct.Count <> 1 Then Return Nothing
            Return candidates.OrderByDescending(Function(o) o.At).First()
        End Function
    End Class

End Namespace
