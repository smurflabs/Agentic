Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json.Nodes
Imports System.Text.Json.Serialization

Namespace AI

    ''' <summary>One message in an OpenAI-style chat transcript.</summary>
    Public Class ChatMessage
        ''' <summary>system | user | assistant | tool</summary>
        Public Property Role As String
        Public Property Content As String
        ''' <summary>Image parts for vision requests (data: URIs or https URLs). Not persisted in checkpoints.</summary>
        <JsonIgnore>
        Public Property ImageUrls As List(Of String)
        Public Property ToolCalls As List(Of ToolCallRequest)
        Public Property ToolCallId As String
        Public Property Name As String

        Public Shared Function ForSystem(content As String) As ChatMessage
            Return New ChatMessage With {.Role = "system", .Content = content}
        End Function

        Public Shared Function ForUser(content As String) As ChatMessage
            Return New ChatMessage With {.Role = "user", .Content = content}
        End Function

        Public Shared Function ForAssistant(content As String, Optional toolCalls As List(Of ToolCallRequest) = Nothing) As ChatMessage
            Return New ChatMessage With {.Role = "assistant", .Content = content, .ToolCalls = toolCalls}
        End Function

        Public Shared Function ForTool(toolCallId As String, name As String, content As String) As ChatMessage
            Return New ChatMessage With {.Role = "tool", .ToolCallId = toolCallId, .Name = name, .Content = content}
        End Function

        Public Function EstimatedChars() As Integer
            Dim n = If(Content, "").Length + 20
            If ToolCalls IsNot Nothing Then
                For Each tc In ToolCalls
                    n += If(tc.Name, "").Length + If(tc.ArgumentsJson, "").Length + 30
                Next
            End If
            If ImageUrls IsNot Nothing Then n += ImageUrls.Count * 3000
            Return n
        End Function
    End Class

    Public Class ToolCallRequest
        Public Property Id As String
        Public Property Name As String
        Public Property ArgumentsJson As String
    End Class

    Public Class ToolDefinition
        Public Property Name As String
        Public Property Description As String
        Public Property Parameters As JsonObject
    End Class

    Public Class ChatRequest
        Public Property ModelId As String
        Public Property Messages As New List(Of ChatMessage)
        Public Property Tools As List(Of ToolDefinition)
        Public Property MaxTokens As Integer = 2048
        Public Property Temperature As Double?
        Public Property Stream As Boolean = True
        Public Property Timeout As TimeSpan = TimeSpan.FromMinutes(5)
    End Class

    Public Class ChatUsage
        Public Property PromptTokens As Long?
        Public Property CompletionTokens As Long?
        Public Property ReasoningTokens As Long?
        ''' <summary>The part of PromptTokens the provider served from its cache (charged at a tenth of the input price).</summary>
        Public Property CachedTokens As Long?
    End Class

    Public Class ChatResponse
        Public Property Content As String
        Public Property ReasoningContent As String
        Public Property ToolCalls As New List(Of ToolCallRequest)
        Public Property FinishReason As String
        Public Property Usage As New ChatUsage()
        Public Property Model As String
        Public Property DurationMs As Long
        ''' <summary>Request sent to the first streamed output (text, thinking or a tool call); Nothing when not streamed.</summary>
        Public Property FirstTokenMs As Long?

        Public ReadOnly Property HasToolCalls As Boolean
            Get
                Return ToolCalls IsNot Nothing AndAlso ToolCalls.Count > 0
            End Get
        End Property

        Public ReadOnly Property IsTruncatedEmpty As Boolean
            Get
                Return Not HasToolCalls AndAlso String.IsNullOrWhiteSpace(Content) AndAlso
                       String.Equals(FinishReason, "length", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property
    End Class

    Public Class ImageGenerationResult
        Public Property Data As Byte()
        Public Property MimeType As String
        Public Property RevisedPrompt As String
    End Class

    Public Enum AiErrorKind
        Transient
        RateLimited
        ModelUnavailable
        ContextOverflow
        CapabilityUnsupported
        Auth
        InvalidRequest
        InvalidResponse
        Timeout
        Cancelled
        BudgetExceeded
        QuotaExceeded
        NoModelAvailable
    End Enum

    ''' <summary>Classified AI failure. Retryable kinds are retried with backoff; others fail fast or trigger fallback/escalation.</summary>
    Public Class AiException
        Inherits Exception

        Public ReadOnly Property Kind As AiErrorKind
        Public ReadOnly Property StatusCode As Integer
        ''' <summary>
        ''' The model's upstream account has no credit left (insufficient balance / quota). Retrying soon will not help: the
        ''' gateway pauses that whole provider for a while and other providers' models take over.
        ''' </summary>
        Public Property OutOfCredit As Boolean

        Public Sub New(kind As AiErrorKind, message As String, Optional statusCode As Integer = 0, Optional inner As Exception = Nothing)
            MyBase.New(message, inner)
            Me.Kind = kind
            Me.StatusCode = statusCode
        End Sub

        Public ReadOnly Property IsRetryable As Boolean
            Get
                Return Kind = AiErrorKind.Transient OrElse Kind = AiErrorKind.RateLimited OrElse Kind = AiErrorKind.Timeout OrElse Kind = AiErrorKind.InvalidResponse
            End Get
        End Property

        ''' <summary>Errors that mean "try a different model" rather than "retry the same one".</summary>
        Public ReadOnly Property ShouldFallback As Boolean
            Get
                Return Kind = AiErrorKind.ModelUnavailable OrElse Kind = AiErrorKind.CapabilityUnsupported OrElse IsRetryable
            End Get
        End Property
    End Class

End Namespace
