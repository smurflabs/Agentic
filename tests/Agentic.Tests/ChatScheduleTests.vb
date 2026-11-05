Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Text.Json.Nodes
Imports Agentic.Work
Imports Xunit

''' <summary>Scheduled prompts (/schedule): when they run, and the server sending them to a chat while no window is open.</summary>
Public Class ChatScheduleTests

    <Fact>
    Public Sub ScheduleTimesAreReadAndTheNextRunIsFound()
        ' Friday 30 October 2026, 10:00 UTC = 11:00 in Berlin (winter time from 25 October).
        Dim now = New DateTime(2026, 10, 30, 10, 0, 0, DateTimeKind.Utc)
        Dim problem As String = Nothing
        Dim every = ScheduleSpec.Parse("every 2 hours", "UTC", now, problem)
        Assert.Equal("every 2h", every.Text)
        Assert.Equal(now.AddHours(2), every.NextAfter(now))
        Assert.Equal("every 1h", ScheduleSpec.Parse("hourly", Nothing, now, problem).Text)
        Assert.Equal("every 1d", ScheduleSpec.Parse("every 1 day", Nothing, now, problem).Text)
        Assert.Null(ScheduleSpec.Parse("every 3m", Nothing, now, problem))
        Assert.Contains("5 minutes", problem)

        Dim daily = ScheduleSpec.Parse("daily 09:00", "Europe/Berlin", now, problem)
        Assert.Equal("daily 09:00", daily.Text)
        Assert.Equal(New DateTime(2026, 10, 31, 8, 0, 0, DateTimeKind.Utc), daily.NextAfter(now))
        ' Weekdays skip the weekend: from Friday after 08:30 the next is Monday.
        Dim weekdays = ScheduleSpec.Parse("weekdays 08:30", "Europe/Berlin", now, problem)
        Assert.Equal(New DateTime(2026, 11, 2, 7, 30, 0, DateTimeKind.Utc), weekdays.NextAfter(now))

        ' "at 09:00": once, the next time it is 09:00 there; nothing after that.
        Dim at = ScheduleSpec.Parse("at 09:00", "Europe/Berlin", now, problem)
        Assert.Equal("once 2026-10-31 09:00", at.Text)
        Dim first = at.FirstRun(now)
        Assert.Equal(New DateTime(2026, 10, 31, 8, 0, 0, DateTimeKind.Utc), first)
        Assert.Null(at.NextAfter(first))
        ' The normalized text reads back the same.
        Assert.Equal(first, ScheduleSpec.Parse(at.Text, "Europe/Berlin", now, problem).FirstRun(now))

        Assert.Null(ScheduleSpec.Parse("once 2026-10-01 09:00", "UTC", now, problem))
        Assert.Contains("passed", problem)
        Assert.Null(ScheduleSpec.Parse("daily 25:00", "UTC", now, problem))
        Assert.Null(ScheduleSpec.Parse("whenever", "UTC", now, problem))
        Assert.Contains("every 30m", problem)
        ' An unknown time zone counts as UTC.
        Assert.Equal(New DateTime(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc), ScheduleSpec.Parse("daily 09:00", "Mars/Base", now, problem).NextAfter(now))
    End Sub

    <Fact>
    Public Async Function AScheduledPromptIsSentWhileNoWindowIsOpen() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) o.Chat.SchedulePollSeconds = 1)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim created = Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(admin.Client, "POST", "/api/chat/schedules",
                                                                                         New With {.chatId = id, .when = "every 1h", .prompt = "Check the build", .timeZone = "UTC"}, admin.Csrf))
                Assert.Equal("every 1h", created("when").ToString())
                Dim schedId = created("id").ToString()
                ' Due now: the server sends it by itself.
                Await env.Host.H.Database.ExecuteAsync("UPDATE ChatSchedules SET NextRunUtc=@n WHERE Id=@id",
                                                       ("@n", CObj(DateTime.UtcNow.AddMinutes(-1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"))), ("@id", CObj(schedId)))
                Dim chat As JsonNode = Nothing
                For i = 1 To 100
                    chat = Await ChatFeatureTests.GetChatAsync(admin, id)
                    If chat("messages").AsArray().Count >= 2 Then Exit For
                    Await Task.Delay(100)
                Next
                Dim msgs = chat("messages").AsArray()
                Assert.Equal(2, msgs.Count)
                Assert.Equal("⏰ Check the build", msgs(0)("content").ToString())
                Assert.Equal("answer from fast/a", msgs(1)("content").ToString())
                ' Counted, with the next run an hour on.
                Dim list As JsonArray = Nothing
                For i = 1 To 50
                    list = JsonNode.Parse(Await admin.Client.GetStringAsync($"/api/chat/schedules?chatId={id}")).AsArray()
                    If list(0)("lastStatus")?.ToString() = "ok" Then Exit For
                    Await Task.Delay(100)
                Next
                Assert.Equal(1, list(0)("runs").GetValue(Of Integer)())
                Assert.Equal("ok", list(0)("lastStatus")?.ToString())
                Assert.True(list(0)("nextRunUtc").GetValue(Of DateTime)() > DateTime.UtcNow.AddMinutes(50))
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function SchedulesAreCheckedPausedRunAndDeleted() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                ' A time that is not one, and a chat that is not the user's.
                Dim bad = Await PlatformApiTests.Send(admin.Client, "POST", "/api/chat/schedules", New With {.chatId = id, .when = "every 1m", .prompt = "x"}, admin.Csrf)
                Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode)
                Dim missing = Await PlatformApiTests.Send(admin.Client, "POST", "/api/chat/schedules", New With {.chatId = "chat_nope", .when = "every 1h", .prompt = "x"}, admin.Csrf)
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode)
                Dim s = Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(admin.Client, "POST", "/api/chat/schedules",
                                                                                   New With {.chatId = id, .when = "daily 07:15", .prompt = "Morning summary", .timeZone = "UTC"}, admin.Csrf))
                Dim sid = s("id").ToString()
                ' Run now: the prompt goes to the chat at once.
                Dim run = Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/schedules/{sid}/run", New With {.x = 1}, admin.Csrf)
                Assert.True(run.IsSuccessStatusCode, Await run.Content.ReadAsStringAsync())
                Dim chat As JsonNode = Nothing
                For i = 1 To 100
                    chat = Await ChatFeatureTests.GetChatAsync(admin, id)
                    If chat("messages").AsArray().Count >= 2 Then Exit For
                    Await Task.Delay(100)
                Next
                Assert.Equal("⏰ Morning summary", chat("messages")(0)("content").ToString())
                ' Pause, then delete.
                Dim paused = Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(admin.Client, "PATCH", $"/api/chat/schedules/{sid}", New With {.enabled = False}, admin.Csrf))
                Assert.False(paused("enabled").GetValue(Of Boolean)())
                Dim resumed = Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(admin.Client, "PATCH", $"/api/chat/schedules/{sid}", New With {.enabled = True}, admin.Csrf))
                Assert.True(resumed("enabled").GetValue(Of Boolean)())
                Assert.True((Await PlatformApiTests.Send(admin.Client, "DELETE", $"/api/chat/schedules/{sid}", Nothing, admin.Csrf)).IsSuccessStatusCode)
                Assert.Empty(JsonNode.Parse(Await admin.Client.GetStringAsync("/api/chat/schedules")).AsArray())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function MonitorEventsAreAnsweredByTheServer() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim r = Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/monitor",
                                                    New With {.monitor = "bg1", .description = "dev server", .lines = {"compiling", "Listening on http://localhost:3000"}}, admin.Csrf)
                Assert.True(r.IsSuccessStatusCode, Await r.Content.ReadAsStringAsync())
                Dim chat As JsonNode = Nothing
                For i = 1 To 100
                    chat = Await ChatFeatureTests.GetChatAsync(admin, id)
                    If chat("messages").AsArray().Count >= 2 Then Exit For
                    Await Task.Delay(100)
                Next
                Dim msgs = chat("messages").AsArray()
                Assert.Equal(2, msgs.Count)
                ' The events, written by the server (windows fold them away), then the model's answer to them.
                Assert.Equal("auto_monitor", msgs(0)("finishReason")?.ToString())
                Assert.Contains("[monitor bg1 dev server] Listening on http://localhost:3000", msgs(0)("content").ToString())
                Assert.Equal("answer from fast/a", msgs(1)("content").ToString())
            End Using
        End Using
    End Function

End Class
