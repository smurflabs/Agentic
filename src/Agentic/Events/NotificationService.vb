Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Net.Http
Imports System.Net.Mail
Imports System.Text
Imports System.Threading
Imports Agentic.Configuration
Imports Agentic.Logging
Imports Agentic.Utilities

Namespace Events

    ''' <summary>
    ''' Lets an agent reach a human asynchronously instead of only parking for a dashboard answer: posts to a named
    ''' incoming-webhook channel (Slack/Discord/Teams compatible: {"text": ...}) or sends email over SMTP. Credentials come
    ''' only from environment variables, never config.
    ''' </summary>
    Public NotInheritable Class NotificationService
        Private ReadOnly _opt As NotificationOptions
        Private ReadOnly _log As ILog
        Private ReadOnly _http As HttpClient

        Public Sub New(opt As NotificationOptions, log As ILog)
            _opt = If(opt, New NotificationOptions())
            _log = log
            _http = New HttpClient() With {.Timeout = TimeSpan.FromSeconds(20)}
        End Sub

        Public ReadOnly Property Enabled As Boolean
            Get
                Return _opt.Enabled AndAlso (_opt.Email.Enabled OrElse _opt.Channels.Any(Function(c) Not String.IsNullOrWhiteSpace(c.Url)))
            End Get
        End Property

        ''' <summary>Channel names an agent may target (webhook channels first, then "email" if configured).</summary>
        Public Function ChannelNames() As List(Of String)
            Dim names = _opt.Channels.Where(Function(c) Not String.IsNullOrWhiteSpace(c.Name)).Select(Function(c) c.Name).ToList()
            If _opt.Email.Enabled Then names.Add("email")
            Return names
        End Function

        Public Async Function NotifyAsync(channel As String, subject As String, message As String, agentId As String, ct As CancellationToken) As Task(Of (Ok As Boolean, Err As String))
            If Not Enabled Then Return (False, "Outbound notifications are not configured.")
            Dim text = $"[{agentId}] {If(String.IsNullOrWhiteSpace(subject), "", subject & ": ")}{message}"
            ' Pick the channel: by name, else email if asked, else the first configured webhook.
            Dim ch = _opt.Channels.FirstOrDefault(Function(c) String.Equals(c.Name, channel, StringComparison.OrdinalIgnoreCase))
            If ch Is Nothing AndAlso (String.Equals(channel, "email", StringComparison.OrdinalIgnoreCase) OrElse channel Is Nothing) AndAlso _opt.Email.Enabled Then
                Return Await SendEmailAsync(subject, message, ct).ConfigureAwait(False)
            End If
            If ch Is Nothing Then ch = _opt.Channels.FirstOrDefault(Function(c) Not String.IsNullOrWhiteSpace(c.Url))
            If ch Is Nothing Then
                If _opt.Email.Enabled Then Return Await SendEmailAsync(subject, message, ct).ConfigureAwait(False)
                Return (False, "No matching notification channel.")
            End If
            If String.Equals(ch.Type, "email", StringComparison.OrdinalIgnoreCase) Then Return Await SendEmailAsync(subject, message, ct).ConfigureAwait(False)
            Return Await PostWebhookAsync(ch.Url, text, ct).ConfigureAwait(False)
        End Function

        Private Async Function PostWebhookAsync(url As String, text As String, ct As CancellationToken) As Task(Of (Ok As Boolean, Err As String))
            Try
                Dim body = JsonUtil.ToJson(New With {.text = text})
                Using content As New StringContent(body, Encoding.UTF8, "application/json")
                    Using resp = Await _http.PostAsync(url, content, ct).ConfigureAwait(False)
                        If resp.IsSuccessStatusCode Then Return (True, Nothing)
                        Return (False, $"Webhook returned {CInt(resp.StatusCode)}.")
                    End Using
                End Using
            Catch ex As Exception
                _log.Warn("Notify", "Webhook delivery failed: " & ex.Message)
                Return (False, "Webhook delivery failed.")
            End Try
        End Function

        Private Async Function SendEmailAsync(subject As String, message As String, ct As CancellationToken) As Task(Of (Ok As Boolean, Err As String))
            Dim e = _opt.Email
            If Not e.Enabled OrElse String.IsNullOrWhiteSpace(e.SmtpHost) OrElse String.IsNullOrWhiteSpace(e.FromAddress) OrElse String.IsNullOrWhiteSpace(e.ToAddress) Then
                Return (False, "Email is not fully configured.")
            End If
            Try
                Using msg As New MailMessage(e.FromAddress, e.ToAddress) With {.Subject = TextUtil.Truncate(If(subject, "Agent notification"), 200), .Body = message}
                    Using client As New SmtpClient(e.SmtpHost, e.Port) With {.EnableSsl = e.UseStartTls}
                        Dim user = Environment.GetEnvironmentVariable(If(e.UserEnvVar, "AGENTIC_SMTP_USER"))
                        Dim pass = Environment.GetEnvironmentVariable(If(e.PasswordEnvVar, "AGENTIC_SMTP_PASSWORD"))
                        If Not String.IsNullOrEmpty(user) Then client.Credentials = New NetworkCredential(user, pass)
                        Await client.SendMailAsync(msg, ct).ConfigureAwait(False)
                    End Using
                End Using
                Return (True, Nothing)
            Catch ex As Exception
                _log.Warn("Notify", "Email delivery failed: " & ex.Message)
                Return (False, "Email delivery failed.")
            End Try
        End Function
    End Class

End Namespace
