Option Strict On
Option Explicit On
Option Infer On

Imports Agentic.Utilities

Namespace AI

    ''' <summary>
    ''' Shares agent work between the local models (Spillover.FromModel and LocalCandidates: local-main and local-vision),
    ''' all the time rather than only when the main one is full. Each agent task goes to the model with the fewest tasks
    ''' working on it per slot, and stays there while that model has a free slot: a llama.cpp server keeps a task's prompt
    ''' in its cache only while its shared context is not overfilled, so tasks spread over both servers are answered from
    ''' cache instead of being processed from scratch every call (LedgerLite: four agents on local-main took 210-240 s per
    ''' call while local-vision sat idle; two per server took 30-40 s).
    ''' </summary>
    Public NotInheritable Class LocalBalancer

        ''' <summary>One local model as the balancer sees it.</summary>
        Public NotInheritable Class Choice
            Public Property Key As String
            ''' <summary>Slots this work may use (a candidate's slots minus the ones it keeps for its own work).</summary>
            Public Property Capacity As Integer
            Public Property InUse As Integer
            Public Property Waiting As Integer

            Friend ReadOnly Property HasRoom As Boolean
                Get
                    Return InUse < Capacity AndAlso Waiting = 0
                End Get
            End Property
        End Class

        Private ReadOnly _lock As New Object()
        Private ReadOnly _tasks As New Dictionary(Of String, (Model As String, LastUtc As DateTime))(StringComparer.Ordinal)
        Private ReadOnly _clock As IClock

        Public Sub New(clock As IClock)
            _clock = clock
        End Sub

        ''' <summary>
        ''' The model for this call. <paramref name="choices"/>(0) is the main model, which wins ties. A task without a key
        ''' (no task id) is placed by load only and not remembered.
        ''' </summary>
        Public Function Pick(taskKey As String, choices As IList(Of Choice), window As TimeSpan) As String
            If choices Is Nothing OrElse choices.Count = 0 Then Return Nothing
            SyncLock _lock
                Dim now = _clock.UtcNow
                Prune(now, window)
                ' Where the task's prompt is cached, while that model has a free slot.
                Dim current As (Model As String, LastUtc As DateTime) = Nothing
                If taskKey IsNot Nothing AndAlso _tasks.TryGetValue(taskKey, current) Then
                    Dim stay = choices.FirstOrDefault(Function(c) String.Equals(c.Key, current.Model, StringComparison.OrdinalIgnoreCase))
                    If stay IsNot Nothing AndAlso stay.HasRoom Then Return Touch(taskKey, stay.Key, now)
                End If
                ' Else the one with the fewest tasks (or calls under way, or callers waiting) per slot.
                Dim best = choices(0)
                Dim bestScore = Score(best, taskKey)
                For Each c In choices.Skip(1)
                    Dim s = Score(c, taskKey)
                    If (s < bestScore AndAlso (c.HasRoom OrElse Not best.HasRoom)) OrElse (Not best.HasRoom AndAlso c.HasRoom) Then
                        best = c
                        bestScore = s
                    End If
                Next
                Return Touch(taskKey, best.Key, now)
            End SyncLock
        End Function

        ''' <summary>Records where a task's call actually ran (spillover, or back to the main model after a failure).</summary>
        Public Sub Note(taskKey As String, model As String)
            If taskKey Is Nothing OrElse model Is Nothing Then Return
            SyncLock _lock
                Touch(taskKey, model, _clock.UtcNow)
            End SyncLock
        End Sub

        ''' <summary>Tasks counted as working on each model now (for the status page and tests).</summary>
        Public Function TasksPerModel(window As TimeSpan) As Dictionary(Of String, Integer)
            SyncLock _lock
                Prune(_clock.UtcNow, window)
                Return _tasks.Values.GroupBy(Function(v) v.Model, StringComparer.OrdinalIgnoreCase).ToDictionary(Function(g) g.Key, Function(g) g.Count(), StringComparer.OrdinalIgnoreCase)
            End SyncLock
        End Function

        Private Function Score(c As Choice, taskKey As String) As Double
            Dim tasks = _tasks.Where(Function(kv) kv.Key <> taskKey AndAlso String.Equals(kv.Value.Model, c.Key, StringComparison.OrdinalIgnoreCase)).Count()
            Return (Math.Max(tasks, c.InUse) + c.Waiting) / CDbl(Math.Max(1, c.Capacity))
        End Function

        Private Function Touch(taskKey As String, model As String, now As DateTime) As String
            If taskKey IsNot Nothing Then _tasks(taskKey) = (model, now)
            Return model
        End Function

        Private Sub Prune(now As DateTime, window As TimeSpan)
            For Each k In _tasks.Where(Function(kv) now - kv.Value.LastUtc > window).Select(Function(kv) kv.Key).ToList()
                _tasks.Remove(k)
            Next
        End Sub
    End Class

End Namespace
