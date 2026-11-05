Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports System.Threading
Imports Agentic.AI
Imports Agentic.Logging
Imports Agentic.Utilities
Imports Agentic.Work

Namespace Http

    ' Scheduled prompts (/schedule): the server sends a prompt to a chat on a schedule, also while no window is open. The answer
    ' is written like any other: windows that are open follow it live, the others find it in the chat.
    Partial Friend NotInheritable Class AdminApi

        Private _scheduleStore As ChatScheduleStore
        ''' <summary>Schedules running now (a long answer must not start the same schedule twice).</summary>
        Private ReadOnly _schedulesRunning As New Collections.Concurrent.ConcurrentDictionary(Of String, Byte)(StringComparer.Ordinal)

        Friend ReadOnly Property Schedules As ChatScheduleStore
            Get
                If _scheduleStore Is Nothing Then _scheduleStore = New ChatScheduleStore(_h.Database)
                Return _scheduleStore
            End Get
        End Property

        Friend Sub RegisterChatSchedules(r As Router)
            r.Map("GET", "/api/chat/schedules", AuthLevel.Read, AddressOf SchedulesList)
            r.Map("POST", "/api/chat/schedules", AuthLevel.SubmitTask, AddressOf ScheduleCreate)
            r.Map("PATCH", "/api/chat/schedules/{id}", AuthLevel.SubmitTask, AddressOf ScheduleUpdate)
            r.Map("DELETE", "/api/chat/schedules/{id}", AuthLevel.SubmitTask, AddressOf ScheduleDelete)
            r.Map("POST", "/api/chat/schedules/{id}/run", AuthLevel.SubmitTask, AddressOf ScheduleRunNow)
        End Sub

        Private Async Function ScheduleDtoAsync(s As ChatSchedule) As Task(Of Object)
            Dim chat = Await _h.Chat.GetAsync(s.Username, s.ChatId, False).ConfigureAwait(False)
            Return New With {.id = s.Id, .chatId = s.ChatId, .chatTitle = chat?.Title, .prompt = s.Prompt, .when = s.When, .timeZone = s.TimeZone,
                             .nextRunUtc = s.NextRunUtc, .lastRunUtc = s.LastRunUtc, .lastStatus = s.LastStatus, .runs = s.Runs, .enabled = s.Enabled}
        End Function

        ''' <summary>?chatId= for one chat's schedules; else all of the user's.</summary>
        Private Async Function SchedulesList(rc As RequestContext) As Task
            RequireChat()
            Dim list = Await Schedules.ListAsync(rc.User.Name, rc.QueryString("chatId")).ConfigureAwait(False)
            Dim rows As New List(Of Object)
            For Each s In list
                rows.Add(Await ScheduleDtoAsync(s).ConfigureAwait(False))
            Next
            Await rc.OkAsync(rows).ConfigureAwait(False)
        End Function

        ''' <summary>Body {chatId, prompt, when, timeZone}: when is "every 1h", "daily 09:00", "weekdays 08:30", "at 14:00" or "once 2026-11-01 09:00".</summary>
        Private Async Function ScheduleCreate(rc As RequestContext) As Task
            RequireChat()
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim chatId = JsonUtil.GetString(b, "chatId")
            Dim prompt = If(JsonUtil.GetString(b, "prompt"), "").Trim()
            If String.IsNullOrWhiteSpace(chatId) OrElse Await _h.Chat.GetAsync(rc.User.Name, chatId, False).ConfigureAwait(False) Is Nothing Then
                Throw ApiException.NotFound("CHAT_NOT_FOUND", "Conversation does not exist.")
            End If
            If prompt.Length = 0 Then Throw ApiException.BadRequest("What should be sent? Give the prompt after the time: /schedule every 1h <prompt>.")
            Dim problem As String = Nothing
            Dim zone = TextUtil.Truncate(JsonUtil.GetString(b, "timeZone"), 64)
            Dim now = DateTime.UtcNow
            Dim spec = ScheduleSpec.Parse(JsonUtil.GetString(b, "when"), zone, now, problem)
            If spec Is Nothing Then Throw ApiException.BadRequest(problem)
            If Await Schedules.CountAsync(rc.User.Name).ConfigureAwait(False) >= ChatScheduleStore.MaxPerUser Then
                Throw ApiException.BadRequest($"You have {ChatScheduleStore.MaxPerUser} scheduled prompts already: delete one first (/schedule list).")
            End If
            Dim s As New ChatSchedule With {.Id = Ids.NewId("sched"), .Username = rc.User.Name, .ChatId = chatId, .Prompt = TextUtil.Truncate(prompt, 8000),
                                            .When = spec.Text, .TimeZone = If(String.IsNullOrWhiteSpace(zone), Nothing, zone), .NextRunUtc = spec.FirstRun(now),
                                            .Enabled = True, .CreatedUtc = now}
            Await Schedules.AddAsync(s).ConfigureAwait(False)
            Await rc.WriteJsonAsync(201, Await ScheduleDtoAsync(s).ConfigureAwait(False)).ConfigureAwait(False)
        End Function

        ''' <summary>Body {enabled}: pause or resume (resuming sets the next run from now).</summary>
        Private Async Function ScheduleUpdate(rc As RequestContext) As Task
            RequireChat()
            Dim s = Await ScheduleOrThrowAsync(rc).ConfigureAwait(False)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim enabled = JsonUtil.GetBool(b, "enabled", s.Enabled)
            Dim nextRun As DateTime? = Nothing
            If enabled AndAlso Not s.Enabled Then
                Dim problem As String = Nothing
                Dim spec = ScheduleSpec.Parse(s.When, s.TimeZone, DateTime.UtcNow, problem)
                nextRun = spec?.NextAfter(DateTime.UtcNow)
                If nextRun Is Nothing Then Throw ApiException.BadRequest("This one-time prompt has run (or its time has passed): schedule it again.")
            End If
            Await Schedules.SetEnabledAsync(rc.User.Name, s.Id, enabled, nextRun).ConfigureAwait(False)
            Await rc.OkAsync(Await ScheduleDtoAsync(Await Schedules.GetAsync(rc.User.Name, s.Id).ConfigureAwait(False)).ConfigureAwait(False)).ConfigureAwait(False)
        End Function

        Private Async Function ScheduleDelete(rc As RequestContext) As Task
            RequireChat()
            Dim s = Await ScheduleOrThrowAsync(rc).ConfigureAwait(False)
            Await Schedules.DeleteAsync(rc.User.Name, s.Id).ConfigureAwait(False)
            Await rc.OkAsync(New With {.deleted = True}).ConfigureAwait(False)
        End Function

        ''' <summary>Runs the prompt now (the schedule itself stays as it is).</summary>
        Private Async Function ScheduleRunNow(rc As RequestContext) As Task
            RequireChat()
            Dim s = Await ScheduleOrThrowAsync(rc).ConfigureAwait(False)
            If _h.Chat.Live.IsLive(s.ChatId) OrElse _schedulesRunning.ContainsKey(s.Id) Then Throw New ApiException(409, "CHAT_BUSY", "Wait until the answer is finished.")
            Dim unused = Task.Run(Function() RunScheduleAsync(s, CancellationToken.None))
            Await rc.OkAsync(New With {.started = True}).ConfigureAwait(False)
        End Function

        Private Async Function ScheduleOrThrowAsync(rc As RequestContext) As Task(Of ChatSchedule)
            Dim s = Await Schedules.GetAsync(rc.User.Name, rc.Route("id")).ConfigureAwait(False)
            If s Is Nothing Then Throw ApiException.NotFound("SCHEDULE_NOT_FOUND", "That scheduled prompt does not exist.")
            Return s
        End Function

        ''' <summary>
        ''' Starts the prompts that are due (called every 30 seconds). The next run is set before one starts, so a failure never
        ''' repeats it at once; a chat busy with an answer waits a minute.
        ''' </summary>
        Friend Async Function RunDueSchedulesAsync(nowUtc As DateTime, ct As CancellationToken) As Task
            For Each s In Await Schedules.DueAsync(nowUtc).ConfigureAwait(False)
                ct.ThrowIfCancellationRequested()
                If _schedulesRunning.ContainsKey(s.Id) OrElse _h.Chat.Live.IsLive(s.ChatId) Then
                    Await Schedules.SetStatusAsync(s.Id, "waiting: an answer was being written", nowUtc.AddMinutes(1)).ConfigureAwait(False)
                    Continue For
                End If
                Dim problem As String = Nothing
                Dim spec = ScheduleSpec.Parse(s.When, s.TimeZone, nowUtc, problem)
                Await Schedules.MarkRunAsync(s.Id, nowUtc, spec?.NextAfter(nowUtc)).ConfigureAwait(False)
                Dim one = s
                Dim unused = Task.Run(Function() RunScheduleAsync(one, ct))
            Next
        End Function

        ''' <summary>
        ''' A heartbeat (/heartbeat) whose answer is only HEARTBEAT_OK found nothing to do: the check and its answer leave the chat,
        ''' so it holds only the times something needed attention.
        ''' </summary>
        Private Async Function QuietHeartbeatAsync(s As ChatSchedule) As Task(Of String)
            Dim chat = Await _h.Chat.GetAsync(s.Username, s.ChatId).ConfigureAwait(False)
            Dim msgs = chat?.Messages
            If msgs Is Nothing OrElse msgs.Count < 2 Then Return "ok"
            Dim answer = msgs(msgs.Count - 1), asked = msgs(msgs.Count - 2)
            If answer.Role <> "assistant" OrElse asked.Role <> "user" OrElse Not ChatScheduleStore.IsHeartbeatOk(answer.Content) OrElse
               Not If(asked.Content, "").Contains(ChatScheduleStore.HeartbeatPrefix, StringComparison.Ordinal) Then Return "ok"
            Await _h.Chat.RewindAsync(s.Username, s.ChatId, asked.Id).ConfigureAwait(False)
            Return "ok: nothing needed attention"
        End Function

        ''' <summary>One run: as the user who scheduled it, with their access and limits as they are now.</summary>
        Friend Async Function RunScheduleAsync(s As ChatSchedule, ct As CancellationToken) As Task
            If Not _schedulesRunning.TryAdd(s.Id, 0) Then Return
            Try
                Dim account = Await _h.AgentsRepo.GetUserAsync(s.Username).ConfigureAwait(False)
                If account Is Nothing OrElse account.Disabled Then
                    Await Schedules.SetStatusAsync(s.Id, "stopped: the account is disabled", disable:=True).ConfigureAwait(False)
                    Return
                End If
                Dim user = AuthService.BuildSessionUser(account)
                Dim status = Await RunAutomaticAnswerAsync(user, s.ChatId, "⏰ " & s.Prompt, "schedule", ct).ConfigureAwait(False)
                If status = "ok" AndAlso ChatScheduleStore.IsHeartbeat(s.Prompt) Then status = Await QuietHeartbeatAsync(s).ConfigureAwait(False)
                ' A chat that is gone stops its schedule.
                Await Schedules.SetStatusAsync(s.Id, status, disable:=status.StartsWith("stopped: the chat", StringComparison.Ordinal)).ConfigureAwait(False)
            Catch ex As Exception When TypeOf ex IsNot OperationCanceledException
                _h.Log.Warn("Chat", $"Scheduled prompt {s.Id} failed: {ex.Message}")
                Try
                    Dim unused = Schedules.SetStatusAsync(s.Id, "failed: " & ModelErrors.ForUser(ex))
                Catch inner As Exception When TypeOf inner Is InvalidOperationException
                End Try
            Finally
                Dim gone As Byte
                _schedulesRunning.TryRemove(s.Id, gone)
            End Try
        End Function

    End Class

End Namespace
