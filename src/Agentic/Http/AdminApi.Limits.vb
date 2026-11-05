Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent

Namespace Http

    ' Admin API: the limits an administrator sets per User account (Users & access): chat messages and API calls per minute,
    ' and chat and API tokens per day (UTC). A call over a limit is refused (429) before any model is asked.
    Partial Friend NotInheritable Class AdminApi

        Private ReadOnly _recentRequests As New ConcurrentDictionary(Of String, Queue(Of DateTime))(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _tokensToday As New ConcurrentDictionary(Of String, (Day As String, Tokens As Long, At As DateTime))(StringComparer.OrdinalIgnoreCase)

        ''' <summary>Why this call is over the account's limits (Nothing when it is not); a call within them is counted.</summary>
        Private Async Function LimitProblemAsync(rc As RequestContext) As Task(Of String)
            Dim u = rc.User
            If Not u.IsRestrictedUser Then Return Nothing
            Return Await LimitProblemAsync(u.Name, u.RequestsPerMinute, u.DailyTokenLimit, countRequest:=True).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' Requests per minute (counted when countRequest) and tokens per day for an account; Nothing when within them. Automatic
        ''' goal continuations check the daily tokens only.
        ''' </summary>
        Friend Async Function LimitProblemAsync(username As String, requestsPerMinute As Integer, dailyTokenLimit As Long, countRequest As Boolean) As Task(Of String)
            If dailyTokenLimit > 0 Then
                Dim used = Await TokensUsedTodayAsync(username).ConfigureAwait(False)
                If used >= dailyTokenLimit Then Return $"This account has used its {dailyTokenLimit:N0} tokens for today; the limit resets at midnight UTC."
            End If
            If requestsPerMinute > 0 AndAlso countRequest Then
                Dim q = _recentRequests.GetOrAdd(username, Function(k) New Queue(Of DateTime)())
                SyncLock q
                    Dim cutoff = DateTime.UtcNow.AddMinutes(-1)
                    While q.Count > 0 AndAlso q.Peek() < cutoff
                        q.Dequeue()
                    End While
                    If q.Count >= requestsPerMinute Then Return $"Too many requests: this account may send {requestsPerMinute} per minute. Try again in a moment."
                    q.Enqueue(DateTime.UtcNow)
                End SyncLock
            End If
            Return Nothing
        End Function

        ''' <summary>Chat and API tokens the account used today (UTC), from the usage log; kept for 2 s (a burst of calls asks once).</summary>
        Private Async Function TokensUsedTodayAsync(username As String) As Task(Of Long)
            Dim day = DateTime.UtcNow.ToString("yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture)
            Dim cached As (Day As String, Tokens As Long, At As DateTime) = Nothing
            If _tokensToday.TryGetValue(username, cached) AndAlso cached.Day = day AndAlso (DateTime.UtcNow - cached.At).TotalSeconds < 2 Then Return cached.Tokens
            Dim raw = Await _h.Database.ScalarAsync("SELECT COALESCE(SUM(PromptTokens + CompletionTokens), 0) FROM ModelApiUsage WHERE Username=@u AND CreatedUtc>=@d",
                                                    ("@u", CObj(username)), ("@d", day & "T00:00:00.000Z")).ConfigureAwait(False)
            Dim total = If(raw Is Nothing OrElse TypeOf raw Is DBNull, 0L, Convert.ToInt64(raw, Globalization.CultureInfo.InvariantCulture))
            _tokensToday(username) = (day, total, DateTime.UtcNow)
            Return total
        End Function

    End Class

End Namespace
