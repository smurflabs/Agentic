Option Strict On
Option Explicit On
Option Infer On

Imports System.Net.Http
Imports System.Text.RegularExpressions

Namespace AI

    ''' <summary>
    ''' A model call's failure in a few plain words for people (the Chat tab, subagent cards, the API): what kind of problem it
    ''' was, never the provider's own error text (LiteLLM's exception names, upstream URLs, keys, request ids). That text stays in
    ''' the server log for administrators.
    ''' </summary>
    Public NotInheritable Class ModelErrors

        Private Sub New()
        End Sub

        Private Shared Function Has(text As String, pattern As String) As Boolean
            Return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase)
        End Function

        ''' <summary>The HTTP status a failure names ("fast/a: 503 …", "HTTP 429"), or 0.</summary>
        Public Shared Function StatusOf(text As String) As Integer
            Dim m = Regex.Match(If(text, ""), "(?:^|[\s:(])([45]\d\d)\b")
            Return If(m.Success, Integer.Parse(m.Groups(1).Value, Globalization.CultureInfo.InvariantCulture), 0)
        End Function

        ''' <summary>
        ''' The failure for people: "There was an issue with the upstream connection.", "The model is busy right now (rate
        ''' limited).", … Pass the status when it is known apart from the text (0 otherwise).
        ''' </summary>
        Public Shared Function ForUser(errorText As String, Optional status As Integer = 0) As String
            Dim t = If(errorText, "")
            If status = 0 Then status = StatusOf(t)
            If Has(t, "sent nothing for|stopped sending|did not respond in time|no response from the model within|took too long|timed? ?out|deadline") OrElse
               status = 408 OrElse status = 504 OrElse status = 524 Then
                Return "The model took too long to answer."
            End If
            If Has(t, "quota|insufficient[_ ](quota|funds|balance|credit)|\bbilling\b|\bcredits?\b|\bbalance\b|payment required|out of (credit|funds)|额度|余额") OrElse status = 402 Then
                Return "The model's provider account is out of credit or quota."
            End If
            If Has(t, "rate.?limit|too many requests|overloaded|over capacity|server is busy") OrElse status = 429 Then
                Return "The model is busy right now (rate limited)."
            End If
            If Has(t, "no (healthy )?deployments|no available channel|model[^.]{0,60}(not found|does not exist|unknown|not available)|unknown model") OrElse status = 404 Then
                Return "This model is not available right now."
            End If
            If Has(t, "context.{0,20}(length|window|size)|too long|maximum.{0,30}tokens|token limit|prompt is too long") Then
                Return "The conversation is too long for this model."
            End If
            If Has(t, "content.?(filter|policy|management)|safety (system|filter)|moderation|flagged|refused to (answer|comply)|policy violation") Then
                Return "The model's provider refused the request (content policy)."
            End If
            If Has(t, "unauthori[sz]ed|forbidden|invalid.{0,12}(api.?)?key|authentication|permission denied") OrElse status = 401 OrElse status = 403 Then
                Return "The model's provider rejected the request (access)."
            End If
            If Has(t, "connection|connect|unreachable|refused|reset by peer|\beof\b|mid.?stream|stream (failed|ended)|bad gateway|unavailable|network") OrElse
               status = 502 OrElse status = 503 Then
                Return "There was an issue with the upstream connection."
            End If
            If status >= 500 Then Return "The model's provider had an error."
            If status >= 400 Then Return "The model's provider could not handle the request."
            Return "The model returned an error."
        End Function

        ''' <summary>
        ''' A failure for people when it may be Agentic's own: its refusals and limits ("Size must be one of: …", "Insufficient
        ''' budget for …", "… cannot read images, …") keep their words; one carrying a provider's error is said in a few words.
        ''' </summary>
        Public Shared Function ForUser(ex As Exception) As String
            If TypeOf ex Is ModelRefusedException Then Return ex.Message
            Dim ai = TryCast(ex, AiException)
            If ai IsNot Nothing AndAlso IsOwn(ai) Then Return ai.Message
            Return ForUser(ex?.Message, If(ai Is Nothing, 0, ai.StatusCode))
        End Function

        ''' <summary>
        ''' Whether an AI failure is Agentic's own: no provider status or inner failure, and a kind Agentic raises itself.
        ''' "All candidate models failed: …" lists the providers' errors, so it is not.
        ''' </summary>
        Private Shared Function IsOwn(ex As AiException) As Boolean
            If ex.StatusCode <> 0 OrElse ex.InnerException IsNot Nothing OrElse ex.Message.StartsWith("All candidate models failed", StringComparison.Ordinal) Then Return False
            Select Case ex.Kind
                Case AiErrorKind.InvalidRequest, AiErrorKind.CapabilityUnsupported, AiErrorKind.QuotaExceeded, AiErrorKind.BudgetExceeded,
                     AiErrorKind.NoModelAvailable, AiErrorKind.ContextOverflow
                    Return True
                Case Else
                    Return False
            End Select
        End Function

    End Class

    ''' <summary>
    ''' A model cannot be used for a reason Agentic states itself (turned off by an administrator, no model that reads
    ''' images): its message is for people as it is, and asking again gives the same answer.
    ''' </summary>
    Public NotInheritable Class ModelRefusedException
        Inherits HttpRequestException

        Public Sub New(message As String)
            MyBase.New(message)
        End Sub
    End Class

End Namespace
