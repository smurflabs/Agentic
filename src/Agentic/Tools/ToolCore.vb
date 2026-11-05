Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.AI
Imports Agentic.Approvals
Imports Agentic.Artifacts
Imports Agentic.Communication
Imports Agentic.Configuration
Imports Agentic.Economy
Imports Agentic.Memory
Imports Agentic.Work

Namespace Tools

    Public Enum ToolSignal
        None
        ''' <summary>The task must wait (agent reply / human answer) and resume later.</summary>
        Park
        ''' <summary>The task is finished (complete_task).</summary>
        Complete
        ''' <summary>The agent asked for a stronger model.</summary>
        Escalate
        ''' <summary>The agent switched the rest of the task to one of its granted models.</summary>
        SwitchModel
    End Enum

    Public NotInheritable Class ToolResult
        Public Property Success As Boolean
        ''' <summary>Text returned to the model.</summary>
        Public Property Content As String
        Public Property Signal As ToolSignal = ToolSignal.None
        Public Property WaitKey As String
        Public Property WaitDeadlineUtc As DateTime?
        Public Property Completion As TaskResultContract
        Public Property EscalationReason As String
        ''' <summary>When true, the content is wrapped in an untrusted-data block before reaching the model.</summary>
        Public Property Untrusted As Boolean
        Public Property UntrustedSource As String
        ''' <summary>Additional wait keys for a park that waits on several results at once.</summary>
        Public Property ExtraWaitKeys As New List(Of String)
        ''' <summary>Model key for <see cref="ToolSignal.SwitchModel"/>.</summary>
        Public Property ModelKey As String
        ''' <summary>The task's plan after update_plan (kept in the checkpoint and shown with the task).</summary>
        Public Property Plan As List(Of PlanStep)
        ''' <summary>MCP tools load_tools loaded: offered to the model from the next call on (TaskCheckpoint.LoadedTools).</summary>
        Public Property LoadTools As List(Of String)

        ''' <summary>Parks until every key has resolved.</summary>
        Public Shared Function ParkMany(waitKeys As IList(Of String), deadlineUtc As DateTime?, content As String) As ToolResult
            Dim r = Park(waitKeys(0), deadlineUtc, content)
            r.ExtraWaitKeys = waitKeys.Skip(1).ToList()
            Return r
        End Function

        Public Shared Function Ok(content As String) As ToolResult
            Return New ToolResult With {.Success = True, .Content = content}
        End Function

        Public Shared Function OkJson(value As Object) As ToolResult
            Return New ToolResult With {.Success = True, .Content = Utilities.JsonUtil.ToJson(value)}
        End Function

        Public Shared Function Fail(content As String) As ToolResult
            Return New ToolResult With {.Success = False, .Content = "ERROR: " & content}
        End Function

        Public Shared Function Park(waitKey As String, deadlineUtc As DateTime?, content As String) As ToolResult
            Return New ToolResult With {.Success = True, .Signal = ToolSignal.Park, .WaitKey = waitKey, .WaitDeadlineUtc = deadlineUtc, .Content = content}
        End Function
    End Class

    ''' <summary>
    ''' Everything an agent's tools may use, every member bound to that one agent at construction.
    ''' This is the isolation boundary: nothing here can address another agent's state.
    ''' </summary>
    Public NotInheritable Class AgentScope
        Public ReadOnly Property AgentId As String
        Public ReadOnly Property Memory As IAgentMemoryStore
        Public ReadOnly Property Comms As AgentCommunicationClient
        Public ReadOnly Property Artifacts As ScopedArtifactAccess
        Public ReadOnly Property Budget As AgentBudgetView
        Public ReadOnly Property Queue As AgentTaskQueue
        Public ReadOnly Property Approvals As ScopedApprovals
        Public ReadOnly Property Vision As ScopedVision
        Public ReadOnly Property Images As ScopedImageGeneration
        ''' <summary>This agent's own subagents (spawn, list, wait, cancel).</summary>
        Public ReadOnly Property Subagents As Agents.AgentSubagents
        ''' <summary>Administrator-granted additional models (consult / switch).</summary>
        Public ReadOnly Property Models As ScopedModelAccess

        Friend Sub New(agentId As String, memory As IAgentMemoryStore, comms As AgentCommunicationClient, artifacts As ScopedArtifactAccess,
                       budget As AgentBudgetView, queue As AgentTaskQueue, approvals As ScopedApprovals, vision As ScopedVision, images As ScopedImageGeneration,
                       Optional subagents As Agents.AgentSubagents = Nothing, Optional models As ScopedModelAccess = Nothing)
            Me.AgentId = agentId
            Me.Memory = memory
            Me.Comms = comms
            Me.Artifacts = artifacts
            Me.Budget = budget
            Me.Queue = queue
            Me.Approvals = approvals
            Me.Vision = vision
            Me.Images = images
            Me.Subagents = subagents
            Me.Models = models
        End Sub
    End Class

    Public NotInheritable Class ToolInvocationContext
        Public Property Scope As AgentScope
        Public Property Profile As EffectiveProfile
        Public Property CurrentTask As AgentTask
        Public Property ToolCallId As String
        Public Property CancellationToken As CancellationToken
        ''' <summary>Resolution delivered on resume for a parked call (agent reply / human answer), if any.</summary>
        Public Property Resolution As String

        Public ReadOnly Property AgentId As String
            Get
                Return Scope.AgentId
            End Get
        End Property

        Public Function SendContext() As SendContext
            Return Communication.SendContext.FromTask(CurrentTask, ToolCallId)
        End Function
    End Class

    Public Interface IAgentTool
        ReadOnly Property Name As String
        ReadOnly Property Description As String
        ReadOnly Property Parameters As JsonObject
        ReadOnly Property Source As ToolSource
        ''' <summary>Safe to re-run after a crash interrupted it.</summary>
        ReadOnly Property IsIdempotent As Boolean
        ''' <summary>Requires admin approval before each execution.</summary>
        ReadOnly Property RequiresApproval As Boolean
        ReadOnly Property Timeout As TimeSpan
        Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
    End Interface

    ''' <summary>Convenience base for internal tools.</summary>
    Public MustInherit Class InternalTool
        Implements IAgentTool

        Public MustOverride ReadOnly Property Name As String Implements IAgentTool.Name
        Public MustOverride ReadOnly Property Description As String Implements IAgentTool.Description
        Public MustOverride ReadOnly Property Parameters As JsonObject Implements IAgentTool.Parameters

        Public Overridable ReadOnly Property Source As ToolSource Implements IAgentTool.Source
            Get
                Return ToolSource.Internal
            End Get
        End Property

        Public Overridable ReadOnly Property IsIdempotent As Boolean Implements IAgentTool.IsIdempotent
            Get
                Return True
            End Get
        End Property

        Public Overridable ReadOnly Property RequiresApproval As Boolean Implements IAgentTool.RequiresApproval
            Get
                Return False
            End Get
        End Property

        Public Overridable ReadOnly Property Timeout As TimeSpan Implements IAgentTool.Timeout
            Get
                Return TimeSpan.FromMinutes(2)
            End Get
        End Property

        Public MustOverride Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult) Implements IAgentTool.ExecuteAsync
    End Class

    ''' <summary>The set of tools one agent may use for one task. The model only ever sees these definitions.</summary>
    Public NotInheritable Class AgentToolRegistry
        Private ReadOnly _tools As New Dictionary(Of String, IAgentTool)(StringComparer.Ordinal)

        Public Sub Add(tool As IAgentTool)
            If tool Is Nothing OrElse _tools.ContainsKey(tool.Name) Then Return
            _tools(tool.Name) = tool
        End Sub

        Public Function TryGet(name As String) As IAgentTool
            Dim t As IAgentTool = Nothing
            If name IsNot Nothing AndAlso _tools.TryGetValue(name, t) Then Return t
            Return Nothing
        End Function

        Public ReadOnly Property Count As Integer
            Get
                Return _tools.Count
            End Get
        End Property

        Public Function Names() As List(Of String)
            Return _tools.Keys.OrderBy(Function(n) n).ToList()
        End Function

        Public Function All() As List(Of IAgentTool)
            Return _tools.Values.ToList()
        End Function

        Public Function Definitions() As List(Of ToolDefinition)
            Return _tools.Values.Select(Function(t) New ToolDefinition With {.Name = t.Name, .Description = t.Description, .Parameters = t.Parameters}).ToList()
        End Function
    End Class

    ''' <summary>Minimal JSON-schema validation for tool arguments (required fields, primitive types, enums, max lengths).</summary>
    Public Module JsonSchemaValidator
        Public Function Validate(schema As JsonObject, args As JsonElement) As String
            If schema Is Nothing Then Return Nothing
            If args.ValueKind <> JsonValueKind.Object Then Return "Arguments must be a JSON object."
            Dim required = TryCast(schema("required"), JsonArray)
            If required IsNot Nothing Then
                For Each r In required
                    Dim name = If(r IsNot Nothing AndAlso r.GetValueKind() = JsonValueKind.String, r.GetValue(Of String)(), Nothing)
                    Dim v As JsonElement
                    If name IsNot Nothing AndAlso (Not args.TryGetProperty(name, v) OrElse v.ValueKind = JsonValueKind.Null) Then Return $"Missing required argument '{name}'."
                Next
            End If
            Dim props = TryCast(schema("properties"), JsonObject)
            If props Is Nothing Then Return Nothing
            For Each p In args.EnumerateObject()
                Dim def = TryCast(props(p.Name), JsonObject)
                If def Is Nothing Then Continue For
                Dim err = CheckValue(p.Name, def, p.Value)
                If err IsNot Nothing Then Return err
            Next
            Return Nothing
        End Function

        Private Function NodeLong(n As JsonNode) As Long
            Dim l As Long
            If Long.TryParse(n.ToJsonString(), Globalization.NumberStyles.Integer, Globalization.CultureInfo.InvariantCulture, l) Then Return l
            Dim d As Double
            If Double.TryParse(n.ToJsonString(), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, d) Then Return CLng(Math.Floor(d))
            Return 0
        End Function

        Private Function CheckValue(name As String, def As JsonObject, v As JsonElement) As String
            If v.ValueKind = JsonValueKind.Null Then Return Nothing
            Dim typeNode = def("type")
            Dim t = If(typeNode Is Nothing OrElse typeNode.GetValueKind() <> JsonValueKind.String, Nothing, typeNode.GetValue(Of String)())
            Select Case t
                Case "string"
                    If v.ValueKind <> JsonValueKind.String Then Return $"Argument '{name}' must be a string."
                    Dim maxLen = def("maxLength")
                    If maxLen IsNot Nothing AndAlso v.GetString().Length > NodeLong(maxLen) Then Return $"Argument '{name}' exceeds {NodeLong(maxLen)} characters."
                    Dim en = TryCast(def("enum"), JsonArray)
                    If en IsNot Nothing AndAlso Not en.Any(Function(x) x IsNot Nothing AndAlso x.GetValueKind() = JsonValueKind.String AndAlso x.GetValue(Of String)() = v.GetString()) Then
                        Return $"Argument '{name}' must be one of: {String.Join(", ", en.Select(Function(x) x?.ToString()))}."
                    End If
                Case "integer"
                    Dim i As Long
                    If v.ValueKind <> JsonValueKind.Number OrElse Not v.TryGetInt64(i) Then Return $"Argument '{name}' must be an integer."
                    Dim mn = def("minimum")
                    Dim mx = def("maximum")
                    If mn IsNot Nothing AndAlso i < NodeLong(mn) Then Return $"Argument '{name}' must be >= {mn}."
                    If mx IsNot Nothing AndAlso i > NodeLong(mx) Then Return $"Argument '{name}' must be <= {mx}."
                Case "number"
                    If v.ValueKind <> JsonValueKind.Number Then Return $"Argument '{name}' must be a number."
                Case "boolean"
                    If v.ValueKind <> JsonValueKind.True AndAlso v.ValueKind <> JsonValueKind.False Then Return $"Argument '{name}' must be true or false."
                Case "object"
                    If v.ValueKind <> JsonValueKind.Object Then Return $"Argument '{name}' must be an object."
                Case "array"
                    If v.ValueKind <> JsonValueKind.Array Then Return $"Argument '{name}' must be an array."
                    Dim items = TryCast(def("items"), JsonObject)
                    If items IsNot Nothing Then
                        Dim i = 0
                        For Each item In v.EnumerateArray()
                            Dim err = CheckValue($"{name}[{i}]", items, item)
                            If err IsNot Nothing Then Return err
                            i += 1
                        Next
                    End If
            End Select
            Return Nothing
        End Function
    End Module

End Namespace
