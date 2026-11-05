Option Strict On
Option Explicit On
Option Infer On

Imports System.Text
Imports System.Threading
Imports Agentic.Configuration
Imports Agentic.Economy
Imports Agentic.Work

Namespace AI

    ''' <summary>
    ''' Agent-bound view of the additional models an administrator granted to this agent (or, for a subagent, the grants
    ''' its parent allowed it to inherit). Consults are charged to the paying wallet like any other cloud call.
    ''' </summary>
    Public NotInheritable Class ScopedModelAccess
        Private ReadOnly _gateway As ModelGateway
        Private ReadOnly _access As ModelAccessService
        Private ReadOnly _pricing As PricingService
        Private ReadOnly _agentId As String

        Friend Sub New(gateway As ModelGateway, access As ModelAccessService, pricing As PricingService, agentId As String)
            _gateway = gateway
            _access = access
            _pricing = pricing
            _agentId = agentId
        End Sub

        ''' <summary>Granted models that are currently usable (enabled, priced, still granted).</summary>
        Public Function GrantedModels(profile As EffectiveProfile) As List(Of (Model As ModelRuntime, Grant As ModelGrant))
            If _access Is Nothing Then Return New List(Of (Model As ModelRuntime, Grant As ModelGrant))
            Return _access.GrantedModelsFor(profile).Where(Function(x) _gateway.Selection.IsUsableBy(x.Model, profile, Nothing)).ToList()
        End Function

        ''' <summary>Granted models that can run the agent's tool loop (switch_model needs tool calling).</summary>
        Public Function SwitchableModels(profile As EffectiveProfile) As List(Of (Model As ModelRuntime, Grant As ModelGrant))
            Return GrantedModels(profile).Where(Function(x) x.Model.Has(ModelCapability.ToolCalling)).ToList()
        End Function

        Public Function PriceText(modelKey As String) As String
            Dim p = _pricing.GetPrice(modelKey)
            If Not p.IsPriced Then Return "unpriced"
            Return $"${p.InputPerMillion}/M in, ${p.OutputPerMillion}/M out"
        End Function

        ''' <summary>Prompt section describing the granted models and what each is for.</summary>
        Public Function Describe(profile As EffectiveProfile) As String
            Dim list = GrantedModels(profile)
            If list.Count = 0 Then Return Nothing
            Dim sb As New StringBuilder()
            For Each x In list
                sb.AppendLine($"- {x.Model.Key} ({x.Model.Options.ModelId}; {PriceText(x.Model.Key)}): use for {x.Grant.UseFor.Trim()}")
            Next
            Return sb.ToString().TrimEnd()
        End Function

        ''' <summary>One-off question to a granted model. Throws AiException when not granted, unaffordable or failing.</summary>
        Public Async Function ConsultAsync(profile As EffectiveProfile, t As AgentTask, modelKey As String, prompt As String, instructions As String,
                                           maxTokens As Integer?, ct As CancellationToken) As Task(Of AiCallOutcome)
            Dim entry = GrantedModels(profile).FirstOrDefault(Function(x) String.Equals(x.Model.Key, modelKey, StringComparison.OrdinalIgnoreCase))
            If entry.Model Is Nothing Then Throw New AiException(AiErrorKind.NoModelAvailable, $"Model '{modelKey}' is not granted to you (or is disabled/unpriced).")
            Dim callCtx As New AiCallContext With {
                .AgentId = _agentId, .TaskId = t?.Id, .CorrelationId = t?.CorrelationId, .Profile = profile, .Priority = If(t?.Priority, PriorityLevel.Normal),
                .Purpose = If(t?.Purpose, profile.DefaultPurpose), .AllowSpillover = False, .ExplicitModels = New List(Of String) From {entry.Model.Key},
                .ReasonOverride = AiCallReason.CustomModel, .SkipPurposeCheck = True,
                .MaxTokensOverride = If(maxTokens.HasValue, Math.Clamp(maxTokens.Value, 64, entry.Model.Options.MaxTokens), CType(Nothing, Integer?))}
            Dim sys = $"You are consulted by agent {profile.Id} ({profile.Role}). Your role here: {entry.Grant.UseFor.Trim()}. " &
                      "Answer precisely and completely; say clearly when you are unsure."
            If Not String.IsNullOrWhiteSpace(instructions) Then sys &= vbLf & instructions.Trim()
            Dim messages As New List(Of ChatMessage) From {ChatMessage.ForSystem(sys), ChatMessage.ForUser(prompt)}
            Return Await _gateway.ChatAsync(callCtx, messages, Nothing, ct).ConfigureAwait(False)
        End Function
    End Class

End Namespace
