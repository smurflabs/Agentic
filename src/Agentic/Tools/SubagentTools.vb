Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports Agentic.Agents
Imports Agentic.Utilities

Namespace Tools

    ' =============================================================================================
    ' Subagents: temporary helpers for focused subtasks that can run in parallel. They use the parent's
    ' permitted models; local-model work is free, cloud usage is charged to the parent's wallet (capped).
    ' =============================================================================================

    Public NotInheritable Class SpawnSubagentTool
        Inherits InternalTool

        Private ReadOnly _models As List(Of String)
        Private ReadOnly _defaultTimeout As Integer
        Private ReadOnly _maxTimeout As Integer
        Private ReadOnly _defaultCap As Decimal
        Private ReadOnly _maxConcurrent As Integer

        Public Sub New(models As List(Of String), defaultTimeoutMinutes As Integer, maxTimeoutMinutes As Integer, defaultCap As Decimal, maxConcurrent As Integer)
            _models = models
            _defaultTimeout = defaultTimeoutMinutes
            _maxTimeout = Math.Max(5, maxTimeoutMinutes)
            _defaultCap = defaultCap
            _maxConcurrent = maxConcurrent
        End Sub

        Public Overrides ReadOnly Property Name As String = "spawn_subagent"

        Public Overrides ReadOnly Property Description As String
            Get
                Return $"Create a temporary subagent that works on one focused, self-contained subtask in parallel with you (max {_maxConcurrent} at a time). " &
                       "Give it complete instructions - it cannot ask you questions or see your memory. It uses the free local model by default; any cloud model " &
                       $"usage is charged to YOUR balance, capped by budget_limit_usd (default ${_defaultCap:0.00}). Spawn several in the same turn to run them in parallel. " &
                       "Subagents are cancelled when your current task ends, so collect their results first (wait_for_result or wait_for_subagents). " &
                       "Only spawn when splitting the work genuinely helps."
            End Get
        End Property

        Public Overrides ReadOnly Property Parameters As JsonObject
            Get
                Return Schema.Obj({"name", "instructions"},
                    Schema.Prop("name", Schema.Str("Short name of the subtask", 100)),
                    Schema.Prop("instructions", Schema.Str("Complete, self-contained instructions including all context and the expected output", 20000)),
                    Schema.Prop("models", Schema.OneOfArray("Models it may use (default: all of yours that subagents may use; prefer the local model)", _models)),
                    Schema.Prop("purpose", Schema.OneOf("Kind of work (selects the routing ladder)", [Enum].GetNames(Of AiPurpose)().Where(Function(p) p <> "Vision" AndAlso p <> "ImageGeneration"))),
                    Schema.Prop("budget_limit_usd", Schema.Num("Maximum cloud spend for this subagent, from your balance", 0, 1000)),
                    Schema.Prop("timeout_minutes", Schema.Int($"Maximum run time (default {_defaultTimeout})", 5, _maxTimeout)),
                    Schema.Prop("wait_for_result", Schema.Bool("Pause your task until this subagent finishes (default true). Use false to start several and then call wait_for_subagents.")),
                    Schema.Prop("payload", Schema.AnyObject("Optional structured input data")),
                    Schema.Prop("attachments", Schema.StrArray("Artifact ids you own that the subagent may read")),
                    Schema.Prop("priority", Schema.OneOf("Priority on the shared local model (default: your task's)", {"Low", "Normal", "High"})))
            End Get
        End Property

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            If ctx.Scope.Subagents Is Nothing Then Return ToolResult.Fail("Subagents are not available.")
            Dim purposeText = JsonUtil.GetString(args, "purpose")
            Dim purpose As AiPurpose
            Dim prText = JsonUtil.GetString(args, "priority")
            Dim pr As PriorityLevel
            Dim capEl As JsonElement
            Dim cap As Decimal? = Nothing
            If args.TryGetProperty("budget_limit_usd", capEl) AndAlso capEl.ValueKind = JsonValueKind.Number Then cap = capEl.GetDecimal()
            Dim req As New SpawnRequest With {
                .Name = JsonUtil.GetString(args, "name"), .Instructions = JsonUtil.GetString(args, "instructions"), .PayloadJson = JsonUtil.GetRaw(args, "payload"),
                .Models = JsonUtil.GetStringList(args, "models"), .Purpose = If(purposeText IsNot Nothing AndAlso [Enum].TryParse(purposeText, True, purpose), purpose, CType(Nothing, AiPurpose?)),
                .SpendCapUsd = cap, .TimeoutMinutes = JsonUtil.GetInt(args, "timeout_minutes", 0), .Attachments = JsonUtil.GetStringList(args, "attachments"),
                .Priority = If(prText IsNot Nothing AndAlso [Enum].TryParse(prText, True, pr), pr, CType(Nothing, PriorityLevel?)),
                .IdempotencyKey = If(ctx.CurrentTask Is Nothing, Nothing, $"{ctx.CurrentTask.Id}:{ctx.ToolCallId}")}
            Dim r = Await ctx.Scope.Subagents.SpawnAsync(ctx.CurrentTask, req).ConfigureAwait(False)
            If Not r.Success Then Return ToolResult.Fail($"{r.Code}: {r.Message}")
            If JsonUtil.GetBool(args, "wait_for_result", True) Then
                Return ToolResult.Park("sub:" & r.SubagentId, r.DeadlineUtc.AddMinutes(2), $"Subagent {r.SubagentId} is working on '{req.Name}'; waiting for its result.")
            End If
            Return ToolResult.Ok($"Subagent {r.SubagentId} started on '{req.Name}' (cloud cap {TextUtil.FormatMicros(r.SpendCapMicros)}, deadline {r.DeadlineUtc:u}). " &
                                 "Call wait_for_subagents to collect its result before you complete your task.")
        End Function
    End Class

    Public NotInheritable Class WaitForSubagentsTool
        Inherits InternalTool

        Public Overrides ReadOnly Property Name As String = "wait_for_subagents"
        Public Overrides ReadOnly Property Description As String = "Wait until your subagents finish and get their results (default: every subagent this task started). Returns immediately if they are already done."
        Public Overrides ReadOnly Property Parameters As JsonObject = Schema.Obj(Array.Empty(Of String)(),
            Schema.Prop("subagent_ids", Schema.StrArray("Specific subagent ids (default: all started by this task)")))

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            If ctx.Scope.Subagents Is Nothing Then Return ToolResult.Fail("Subagents are not available.")
            Dim ids = JsonUtil.GetStringList(args, "subagent_ids")
            Dim list As List(Of SubagentInfo)
            If ids.Count > 0 Then
                list = New List(Of SubagentInfo)
                For Each id In ids.Distinct(StringComparer.OrdinalIgnoreCase)
                    Dim s = Await ctx.Scope.Subagents.GetAsync(id).ConfigureAwait(False)
                    If s Is Nothing Then Return ToolResult.Fail($"'{id}' is not one of your subagents.")
                    list.Add(s)
                Next
            Else
                list = Await ctx.Scope.Subagents.ListAsync(ctx.CurrentTask?.Id).ConfigureAwait(False)
            End If
            If list.Count = 0 Then Return ToolResult.Ok("You have no subagents for this task.")
            If list.All(Function(s) s.Status <> SubagentStatus.Running.ToString()) Then
                Return New ToolResult With {.Success = True, .Content = "[" & String.Join(",", list.Select(Function(s) If(s.ResultJson, "null"))) & "]",
                                            .Untrusted = True, .UntrustedSource = "subagents"}
            End If
            ' Park on all of them; already-finished ones resolve immediately when the task parks.
            Dim deadline = list.Where(Function(s) s.DeadlineUtc.HasValue).Select(Function(s) s.DeadlineUtc.Value).DefaultIfEmpty(DateTime.UtcNow.AddHours(1)).Max().AddMinutes(2)
            Dim running = list.Where(Function(s) s.Status = SubagentStatus.Running.ToString()).Count()
            Return ToolResult.ParkMany(list.Select(Function(s) "sub:" & s.Id).ToList(), deadline, $"Waiting for {running} running subagent(s).")
        End Function
    End Class

    Public NotInheritable Class ListSubagentsTool
        Inherits InternalTool

        Public Overrides ReadOnly Property Name As String = "list_subagents"
        Public Overrides ReadOnly Property Description As String = "List your recent subagents with their status, cloud spend and result summary."
        Public Overrides ReadOnly Property Parameters As JsonObject = Schema.Obj(Array.Empty(Of String)(),
            Schema.Prop("this_task_only", Schema.Bool("Only subagents started by the current task (default false)")))

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            If ctx.Scope.Subagents Is Nothing Then Return ToolResult.Fail("Subagents are not available.")
            Dim list = Await ctx.Scope.Subagents.ListAsync(If(JsonUtil.GetBool(args, "this_task_only", False), ctx.CurrentTask?.Id, Nothing)).ConfigureAwait(False)
            If list.Count = 0 Then Return ToolResult.Ok("You have no subagents.")
            Return New ToolResult With {
                .Success = True, .Untrusted = True, .UntrustedSource = "subagents",
                .Content = JsonUtil.ToJson(list.Select(Function(s) New With {
                    .id = s.Id, .name = s.Name, .status = s.Status, .cloudCost = TextUtil.FormatMicros(s.SpentMicros),
                    .cap = If(s.SpendCapMicros.HasValue, TextUtil.FormatMicros(s.SpendCapMicros.Value), "none"), .createdUtc = s.CreatedUtc,
                    .summary = TextUtil.Truncate(s.Summary, 300)}).ToList())}
        End Function
    End Class

    Public NotInheritable Class CancelSubagentTool
        Inherits InternalTool

        Public Overrides ReadOnly Property Name As String = "cancel_subagent"
        Public Overrides ReadOnly Property Description As String = "Stop one of your running subagents (for example when its work is no longer needed)."
        Public Overrides ReadOnly Property Parameters As JsonObject = Schema.Obj({"subagent_id"},
            Schema.Prop("subagent_id", Schema.Str("Subagent id", 100)),
            Schema.Prop("reason", Schema.Str("Why", 300)))

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            If ctx.Scope.Subagents Is Nothing Then Return ToolResult.Fail("Subagents are not available.")
            Dim r = Await ctx.Scope.Subagents.CancelAsync(JsonUtil.GetString(args, "subagent_id"), JsonUtil.GetString(args, "reason", "No longer needed")).ConfigureAwait(False)
            Return If(r.Success, ToolResult.Ok(r.Message), ToolResult.Fail(r.Message))
        End Function
    End Class

    ' =============================================================================================
    ' Administrator-granted additional models
    ' =============================================================================================

    Public NotInheritable Class ConsultModelTool
        Inherits InternalTool

        Private ReadOnly _models As List(Of (Key As String, UseFor As String, Price As String))

        Public Sub New(models As List(Of (Key As String, UseFor As String, Price As String)))
            _models = models
        End Sub

        Public Overrides ReadOnly Property Name As String = "consult_model"

        Public Overrides ReadOnly Property Description As String
            Get
                Return "Ask one of the additional models an administrator granted you, for the purpose it was granted for. You get its answer back; " &
                       "cost is charged to your balance. Granted models: " & String.Join("; ", _models.Select(Function(m) $"{m.Key} - use for {m.UseFor} ({m.Price})"))
            End Get
        End Property

        Public Overrides ReadOnly Property Parameters As JsonObject
            Get
                Return Schema.Obj({"model", "prompt"},
                    Schema.Prop("model", Schema.OneOf("Granted model", _models.Select(Function(m) m.Key))),
                    Schema.Prop("prompt", Schema.Str("Complete request including all needed context", 60000)),
                    Schema.Prop("instructions", Schema.Str("Optional extra instructions (format, constraints)", 4000)),
                    Schema.Prop("max_tokens", Schema.Int("Maximum answer length in tokens", 64, 200000)))
            End Get
        End Property

        Public Overrides ReadOnly Property Source As ToolSource
            Get
                Return ToolSource.AiCapability
            End Get
        End Property

        Public Overrides ReadOnly Property IsIdempotent As Boolean
            Get
                Return False
            End Get
        End Property

        Public Overrides ReadOnly Property Timeout As TimeSpan
            Get
                Return TimeSpan.FromMinutes(10)
            End Get
        End Property

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            If ctx.Scope.Models Is Nothing Then Return ToolResult.Fail("No additional models are available.")
            Dim maxTokens = JsonUtil.GetInt(args, "max_tokens", 0)
            Try
                Dim outcome = Await ctx.Scope.Models.ConsultAsync(ctx.Profile, ctx.CurrentTask, JsonUtil.GetString(args, "model"), JsonUtil.GetString(args, "prompt"),
                                                                  JsonUtil.GetString(args, "instructions"), If(maxTokens > 0, maxTokens, CType(Nothing, Integer?)),
                                                                  ctx.CancellationToken).ConfigureAwait(False)
                If ctx.CurrentTask IsNot Nothing Then ctx.CurrentTask.CostMicros += outcome.CostMicros
                Dim answer = If(String.IsNullOrWhiteSpace(outcome.Response.Content), "(empty answer)", outcome.Response.Content.Trim())
                Return New ToolResult With {.Success = True, .Untrusted = True, .UntrustedSource = "model:" & outcome.ModelKey,
                                            .Content = answer & $" [model {outcome.ModelKey}, cost {TextUtil.FormatMicros(outcome.CostMicros)}]"}
            Catch ex As AI.AiException
                Return ToolResult.Fail($"Consultation failed ({ex.Kind}): {ex.Message}")
            End Try
        End Function
    End Class

    Public NotInheritable Class SwitchModelTool
        Inherits InternalTool

        Private ReadOnly _models As List(Of (Key As String, UseFor As String, Price As String))

        Public Sub New(models As List(Of (Key As String, UseFor As String, Price As String)))
            _models = models
        End Sub

        Public Overrides ReadOnly Property Name As String = "switch_model"

        Public Overrides ReadOnly Property Description As String
            Get
                Return "Continue the rest of this task on one of your granted models (with all your tools), when the work matches what it was granted for. " &
                       "The host verifies access and budget; if the model fails or is unaffordable you return to your normal models. Options: " &
                       String.Join("; ", _models.Select(Function(m) $"{m.Key} - use for {m.UseFor} ({m.Price})"))
            End Get
        End Property

        Public Overrides ReadOnly Property Parameters As JsonObject
            Get
                Return Schema.Obj({"model", "reason"},
                    Schema.Prop("model", Schema.OneOf("Granted model", _models.Select(Function(m) m.Key))),
                    Schema.Prop("reason", Schema.Str("Why this task needs it", 500)))
            End Get
        End Property

        Public Overrides Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Dim key = JsonUtil.GetString(args, "model")
            Return Task.FromResult(New ToolResult With {.Success = True, .Signal = ToolSignal.SwitchModel, .ModelKey = key,
                                                        .Content = $"Switching the rest of this task to {key}."})
        End Function
    End Class

End Namespace
