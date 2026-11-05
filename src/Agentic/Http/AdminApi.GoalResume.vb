Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.Text.Json
Imports System.Threading
Imports Agentic.Logging
Imports Agentic.Utilities
Imports Agentic.Work

Namespace Http

    Partial Friend NotInheritable Class AdminApi

        ''' <summary>What one answer did, as far as the goal is concerned (from the events it sent).</summary>
        Private NotInheritable Class GoalWatch
            Public Paused As Boolean
            Public Asked As Boolean
            Public ErrorText As String
            Public ToolUses As Integer
            Public Sub Note(type As String, payload As Object)
                Select Case type
                    Case "goal"
                        Dim st = JsonUtil.ToNode(payload)?("status")?.ToString()
                        If st = "paused" Then Paused = True
                        If st = "met" OrElse st = "failed" OrElse st = "stopped" Then Paused = False
                    Case "question" : Asked = True
                    Case "error" : ErrorText = JsonUtil.ToNode(payload)?("message")?.ToString()
                    Case "client_tool" : ToolUses += 1
                    Case "tool"
                        If JsonUtil.ToNode(payload)?("phase")?.ToString() = "start" Then ToolUses += 1
                End Select
            End Sub
            ''' <summary>The model stopped while the goal is open, and nobody needs to answer anything: go on by itself.</summary>
            Public ReadOnly Property WantsResume As Boolean
                Get
                    Return Paused AndAlso Not Asked
                End Get
            End Property
        End Class

        ''' <summary>Bumped by every message the user sends: an automatic continuation waiting to start then gives way.</summary>
        Private Shared ReadOnly GoalResumeGen As New ConcurrentDictionary(Of String, Integer)(StringComparer.Ordinal)

        Private Shared Function NextGoalGeneration(chatId As String) As Integer
            Return GoalResumeGen.AddOrUpdate(chatId, 1, Function(k, v) v + 1)
        End Function

        ''' <summary>
        ''' A goal is worked on until it is met: when an answer ends with the goal still open - out of tool rounds, tools switched
        ''' off, the model stopping to summarize, or failures - the next answer starts by itself a few seconds later, as a
        ''' message "Keep working on the goal" from the app. It stops when the goal is met or reported impossible, the user
        ''' presses Stop, the model asks the user something, the user writes, two automatic answers in a row make no progress
        ''' (no tool call), or after Chat.GoalMaxAutoResumes of them.
        ''' </summary>
        Private Sub ScheduleGoalResume(user As AuthUser, chatId As String, attempt As Integer, noProgress As Integer, errorStreak As Integer, delay As TimeSpan)
            If Not _h.Chat.Options.GoalAutoResume OrElse attempt > Math.Max(0, _h.Chat.Options.GoalMaxAutoResumes) Then Return
            Dim gen = GoalResumeGen.GetOrAdd(chatId, 0)
            _h.Chat.Live.Publish(user.Name, chatId, "goal", New With {.status = "resuming", .attempt = attempt, .max = _h.Chat.Options.GoalMaxAutoResumes, .inSeconds = CInt(delay.TotalSeconds)})
            Dim unused = Task.Run(Function() GoalResumeAsync(user, chatId, attempt, noProgress, errorStreak, delay, gen))
        End Sub

        Private Async Function GoalResumeAsync(user As AuthUser, chatId As String, attempt As Integer, noProgress As Integer, errorStreak As Integer, delay As TimeSpan, gen As Integer) As Task
            Try
                Await Task.Delay(delay).ConfigureAwait(False)
                ' The user wrote (or another window started an answer) in the meantime: theirs goes first.
                If GoalResumeGen.GetOrAdd(chatId, 0) <> gen OrElse _h.Chat.Live.IsLive(chatId) Then Return
                Dim c = Await _h.Chat.GetAsync(user.Name, chatId, False).ConfigureAwait(False)
                If c Is Nothing OrElse c.Settings Is Nothing OrElse String.IsNullOrWhiteSpace(c.Settings.Goal) Then Return
                Dim model = c.Model
                ' Still allowed this model, with money for it.
                If Not user.CanUseModel(model) Then Return
                If user.ViaSession OrElse user.ViaApiKey Then
                    Dim blocked = Await _h.Accounts.BlockReasonAsync(user.Name, user.IsAdmin, model, Nothing).ConfigureAwait(False)
                    If blocked IsNot Nothing Then
                        _h.Chat.Live.Publish(user.Name, chatId, "goal", New With {.status = "paused", .reason = blocked})
                        Return
                    End If
                End If
                Using cts As New CancellationTokenSource()
                    _h.Chat.Live.SetStopper(chatId, cts)
                    ' No window asked for this answer: its events are kept for windows that open the chat (GET …/attach), and
                    ' every open window follows it live.
                    Dim sink As New ChatSink(chatId, user.Name, Nothing)
                    _h.Chat.Live.Sinks.Add(sink)
                    Dim watch As New GoalWatch()
                    Dim emit As Func(Of String, Object, Task) =
                        Function(type, payload)
                            If cts.IsCancellationRequested Then Return Task.CompletedTask
                            watch.Note(type, payload)
                            Return sink.WriteAsync($"event: {type}" & vbLf & "data: " & JsonUtil.ToJson(payload) & vbLf & vbLf)
                        End Function
                    Dim content = $"↻ Keep working on the goal until it is met (automatic continuation {attempt} of {_h.Chat.Options.GoalMaxAutoResumes}: " &
                                  "you stopped before reporting it met). Pick up exactly where you left off."
                    ' The account's tokens for today (Users & access) also bound automatic continuations.
                    Dim limitProblem As String = Nothing
                    If user.IsRestrictedUser Then limitProblem = Await LimitProblemAsync(user.Name, 0, user.DailyTokenLimit, False).ConfigureAwait(False)
                    Try
                        If limitProblem IsNot Nothing Then
                            watch.ErrorText = limitProblem
                        Else
                            Await _h.Chat.SendAsync(user.Name, user.IsAdmin, user.HasPermission("chat.tools"), chatId, content, model, New List(Of String), c.Settings, False, emit, cts.Token,
                                                    New ChatClientContext With {.ClientId = "goal-resume", .Automatic = "goal"}, Nothing, False, Function(m) user.CanUseModel(m),
                                                    ChatAccessFor(user)).ConfigureAwait(False)
                        End If
                    Catch ex As Exception When Not cts.IsCancellationRequested
                        watch.ErrorText = ex.Message
                    End Try
                    If watch.ErrorText IsNot Nothing Then Await emit("error", New With {.message = "The answer failed: " & watch.ErrorText}).ConfigureAwait(False)
                    _h.Chat.Live.ClearStopper(chatId, cts)
                    sink.Complete()
                    Dim stopped = cts.IsCancellationRequested
                    If Not stopped Then AfterGoalAnswer(user, chatId, watch, attempt + 1, noProgress, errorStreak, c.Settings.Goal)
                End Using
            Catch ex As Exception
                _h.Log.Warn("Chat", $"Automatic goal continuation failed in chat {chatId}: {ex.Message}")
            End Try
        End Function

        ''' <summary>After an answer in a chat with a goal: continue by itself when the goal is still open (see ScheduleGoalResume).</summary>
        Private Sub AfterGoalAnswer(user As AuthUser, chatId As String, watch As GoalWatch, attempt As Integer, noProgress As Integer, errorStreak As Integer,
                                    Optional goalAtStart As String = Nothing)
            If watch.Asked Then Return
            ' The user set another goal while this answer ran (it was told, but worked on the old one): the new goal is worked on next,
            ' whatever became of the old one.
            If watch.ErrorText Is Nothing AndAlso Not watch.WantsResume Then
                Dim unused = Task.Run(Async Function()
                                          Dim c = Await _h.Chat.GetAsync(user.Name, chatId, False).ConfigureAwait(False)
                                          Dim now = c?.Settings?.Goal
                                          If Not String.IsNullOrWhiteSpace(now) AndAlso Not String.Equals(now, goalAtStart, StringComparison.Ordinal) Then
                                              ScheduleGoalResume(user, chatId, attempt, 0, 0, TimeSpan.FromSeconds(Math.Max(1, _h.Chat.Options.GoalResumeDelaySeconds)))
                                          End If
                                      End Function)
                Return
            End If
            If watch.ErrorText IsNot Nothing Then
                ' A failed answer (the model was down for longer than its retries): try again later, a few times - in a chat with
                ' a goal only (a chat without one waits for the user's Try again, and is never told a goal continues).
                If errorStreak >= 2 OrElse Not ChatService.IsTransientText(watch.ErrorText) Then Return
                Dim later = Task.Run(Async Function()
                                         Dim c = Await _h.Chat.GetAsync(user.Name, chatId, False).ConfigureAwait(False)
                                         If String.IsNullOrWhiteSpace(c?.Settings?.Goal) Then Return
                                         ScheduleGoalResume(user, chatId, attempt, noProgress, errorStreak + 1, TimeSpan.FromSeconds(Math.Max(30, _h.Chat.Options.GoalRetrySeconds)))
                                     End Function)
                Return
            End If
            If Not watch.WantsResume Then Return
            Dim stalled = If(watch.ToolUses = 0, noProgress + 1, 0)
            If stalled >= 2 Then
                _h.Chat.Live.Publish(user.Name, chatId, "goal", New With {.status = "paused", .reason = "The model made no progress in two automatic continuations: tell it what to do next."})
                Return
            End If
            ScheduleGoalResume(user, chatId, attempt, stalled, 0, TimeSpan.FromSeconds(Math.Max(1, _h.Chat.Options.GoalResumeDelaySeconds)))
        End Sub

    End Class

End Namespace
