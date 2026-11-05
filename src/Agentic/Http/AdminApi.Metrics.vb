Option Strict On
Option Explicit On
Option Infer On

Imports System.Globalization
Imports System.Text

Namespace Http

    ' Admin API: Prometheus metrics (text exposition format 0.0.4) for external monitoring and alerting.
    ' Scrape with an API token that has the read scope; per-agent series respect the token's agent allowlist.
    Partial Friend NotInheritable Class AdminApi

        Friend Sub RegisterMetrics(r As Router)
            r.Map("GET", "/api/metrics", AuthLevel.Read, AddressOf Metrics)
        End Sub

        Private NotInheritable Class MetricsWriter
            Private ReadOnly _sb As New StringBuilder()
            Private ReadOnly _declared As New HashSet(Of String)(StringComparer.Ordinal)

            ''' <summary>Writes one sample; HELP/TYPE lines are emitted the first time a metric name appears.</summary>
            Public Sub Add(name As String, type As String, help As String, value As Double, Optional labels As IEnumerable(Of (Key As String, Value As String)) = Nothing)
                ' The exposition format requires LF line endings (AppendLine would write CRLF on Windows).
                If _declared.Add(name) Then
                    _sb.Append("# HELP ").Append(name).Append(" "c).Append(help).Append(vbLf)
                    _sb.Append("# TYPE ").Append(name).Append(" "c).Append(type).Append(vbLf)
                End If
                _sb.Append(name)
                If labels IsNot Nothing AndAlso labels.Any() Then
                    _sb.Append("{"c).Append(String.Join(",", labels.Select(Function(l) $"{l.Key}=""{Escape(l.Value)}"""))).Append("}"c)
                End If
                _sb.Append(" "c).Append(If(Double.IsNaN(value), "NaN", value.ToString("R", CultureInfo.InvariantCulture))).Append(vbLf)
            End Sub

            Private Shared Function Escape(v As String) As String
                Return If(v, "").Replace("\", "\\").Replace("""", "\""").Replace(vbLf, "\n")
            End Function

            Public Overrides Function ToString() As String
                Return _sb.ToString()
            End Function
        End Class

        Private Shared Function Lbl(ParamArray pairs As (String, String)()) As IEnumerable(Of (Key As String, Value As String))
            Return pairs.Select(Function(p) (Key:=p.Item1, Value:=p.Item2))
        End Function

        Private Shared Function Usd(micros As Long) As Double
            Return micros / 1000000.0
        End Function

        Private Async Function Metrics(rc As RequestContext) As Task
            Dim m As New MetricsWriter()
            m.Add("agentic_up", "gauge", "1 while the host is running.", 1)
            m.Add("agentic_uptime_seconds", "gauge", "Seconds since the host started.", Math.Round((DateTime.UtcNow - _h.StartedUtc).TotalSeconds))

            For Each kv In _h.Agents.CountByState()
                m.Add("agentic_agents", "gauge", "Agents by state.", kv.Value, Lbl(("state", kv.Key)))
            Next
            m.Add("agentic_subagents_active", "gauge", "Running subagents.", _h.Agents.ActiveSubagentCount)

            Dim open = Await _h.Tasks.OpenCountsAsync().ConfigureAwait(False)
            Dim byStatus As New Dictionary(Of String, Long)(StringComparer.Ordinal)
            For Each agentCounts In open.Where(Function(x) rc.User.CanAccessAgent(x.Key))
                For Each kv In agentCounts.Value
                    byStatus(kv.Key) = If(byStatus.ContainsKey(kv.Key), byStatus(kv.Key), 0L) + kv.Value
                Next
            Next
            For Each kv In byStatus
                m.Add("agentic_tasks_open", "gauge", "Open tasks by status (Pending, Retrying, Running, Waiting, Blocked).", kv.Value, Lbl(("status", kv.Key)))
            Next
            m.Add("agentic_approvals_pending", "gauge", "Questions and approvals waiting for an administrator.", Await _h.Approvals.CountPendingAsync().ConfigureAwait(False))

            For Each model In _h.Gateway.Registry.All()
                Dim l = Lbl(("model", model.Key), ("local", If(model.Options.IsLocal, "true", "false")))
                m.Add("agentic_model_slots_in_use", "gauge", "Concurrent calls running on the model.", model.Gate.InUse, l)
                m.Add("agentic_model_slots_capacity", "gauge", "Maximum concurrent calls for the model.", model.Gate.Capacity, l)
                m.Add("agentic_model_queue_length", "gauge", "Calls waiting for a model slot.", model.Gate.QueueLength, l)
                m.Add("agentic_model_circuit_open", "gauge", "1 when the model's circuit breaker is open (model skipped).", If(model.Breaker.State.ToString() = "Open", 1, 0), l)
                m.Add("agentic_model_calls_total", "counter", "Model calls since start.", model.Calls, l)
                m.Add("agentic_model_failures_total", "counter", "Failed model calls since start.", model.Failures, l)
                m.Add("agentic_model_latency_avg_ms", "gauge", "Average model call latency.", Math.Round(model.AverageLatencyMs), l)
            Next
            m.Add("agentic_spillovers_total", "counter", "Calls moved from the busy local model to a paid model since start.", _h.Gateway.SpilloverCount)
            m.Add("agentic_escalations_total", "counter", "Escalations to stronger models since start.", _h.Gateway.EscalationCount)

            m.Add("agentic_cloud_spend_today_usd", "gauge", "Cloud AI spend today (UTC) across all agents.", Usd(_h.Wallets.GlobalSpentTodayMicros))
            If _h.Options.Economy.GlobalDailyCloudLimit.HasValue Then
                m.Add("agentic_cloud_daily_limit_usd", "gauge", "Global daily cloud spend cap.", CDbl(_h.Options.Economy.GlobalDailyCloudLimit.Value))
            End If
            For Each w In (Await _h.Wallets.ListAsync().ConfigureAwait(False)).Where(Function(x) rc.User.CanAccessAgent(x.AgentId))
                Dim l = Lbl(("agent", w.AgentId))
                m.Add("agentic_wallet_available_usd", "gauge", "Agent balance available for spending.", Usd(w.AvailableMicros), l)
                m.Add("agentic_wallet_spent_today_usd", "gauge", "Agent cloud spend today (UTC).", Usd(w.SpentTodayMicros), l)
                m.Add("agentic_wallet_burn_rate_usd_per_day", "gauge", "Agent's average daily cloud spend over the runway window.", Usd(w.BurnRateMicrosPerDay), l)
                If w.RunwayDays.HasValue Then m.Add("agentic_wallet_runway_days", "gauge", "Days the agent's balance lasts at its burn rate.", Math.Round(w.RunwayDays.Value, 2), l)
                m.Add("agentic_wallet_lifetime_spend_usd", "counter", "Agent lifetime cloud spend.", Usd(w.LifetimeSpendMicros), l)
                m.Add("agentic_wallet_lifetime_profit_usd", "counter", "Profit credited to the agent.", Usd(w.LifetimeProfitMicros), l)
            Next

            Dim servers = _h.Mcp.GetStatus()
            m.Add("agentic_mcp_servers", "gauge", "Enabled MCP servers.", servers.Where(Function(x) x.Enabled).Count())
            m.Add("agentic_mcp_servers_connected", "gauge", "Connected MCP servers.", servers.Where(Function(x) x.State = Global.Agentic.Mcp.McpConnectionState.Connected).Count())

            Dim learning = Await _h.Lessons.SummaryAsync().ConfigureAwait(False)
            m.Add("agentic_lessons_active", "gauge", "Lessons shown to agents.", Convert.ToDouble(learning("activeLessons"), CultureInfo.InvariantCulture))
            m.Add("agentic_lessons_pending_review", "gauge", "Lessons waiting for administrator approval.", Convert.ToDouble(learning("pendingLessons"), CultureInfo.InvariantCulture))
            For Each outcome In {"failure", "success", "review"}
                m.Add("agentic_case_studies", "gauge", "Case studies by outcome.", Convert.ToDouble(learning(outcome & "Studies"), CultureInfo.InvariantCulture), Lbl(("outcome", outcome)))
            Next
            m.Add("agentic_events_dropped_total", "counter", "Events dropped because the event queue was full.", _h.Events.DroppedInbound)

            rc.Http.Response.Headers("Cache-Control") = "no-store"
            Await rc.WriteBytesAsync(200, "text/plain; version=0.0.4; charset=utf-8", Encoding.UTF8.GetBytes(m.ToString())).ConfigureAwait(False)
        End Function
    End Class

End Namespace
