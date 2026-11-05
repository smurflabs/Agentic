Option Strict On
Option Explicit On
Option Infer On

Imports System.Net.Http
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.Configuration
Imports Agentic.Logging
Imports Agentic.Persistence
Imports Agentic.Utilities
Imports Microsoft.Data.Sqlite

Namespace Economy

    ''' <summary>A deposit: a BTCPay invoice made for an account and, once paid, the credit to its balance.</summary>
    Public NotInheritable Class Deposit
        Public Property InvoiceId As String
        Public Property Username As String
        Public Property AmountMicros As Long
        Public Property Currency As String
        ''' <summary>pending | credited</summary>
        Public Property Status As String
        Public Property CheckoutLink As String
        Public Property CreatedUtc As DateTime
        Public Property CreditedUtc As DateTime?
        Public Property CreditedBy As String
    End Class

    ''' <summary>What crediting a deposit did.</summary>
    Public NotInheritable Class DepositCredit
        Public Property InvoiceId As String
        Public Property Username As String
        Public Property AmountMicros As Long
        ''' <summary>False when the deposit had been credited before (nothing was added this time).</summary>
        Public Property Credited As Boolean
        Public Property BalanceMicros As Long
    End Class

    ''' <summary>
    ''' Deposits through BTCPay Server. An account asks for a whole amount (Payments.MinDeposit..MaxDeposit); an invoice is
    ''' made with the Greenfield API (metadata: username, orderId "agentic-deposit-…", server "agentic") and shown in the
    ''' BTCPay checkout. When it is paid, BTCPay's webhook reaches the payment server, which calls
    ''' POST /api/payments/deposit with the Payments.DepositApiKey; the amount is then added to the balance, once per invoice.
    ''' </summary>
    Public NotInheritable Class DepositService
        Public Const CreditedByPrefix As String = "payment:"

        Private ReadOnly _db As Database
        Private ReadOnly _accounts As AccountService
        Private ReadOnly _options As PaymentOptions
        Private ReadOnly _log As ILog
        Private ReadOnly _http As HttpClient

        Public Sub New(db As Database, accounts As AccountService, options As PaymentOptions, log As ILog, Optional handler As HttpMessageHandler = Nothing)
            _db = db
            _accounts = accounts
            _options = If(options, New PaymentOptions())
            _log = log
            _http = If(handler Is Nothing, New HttpClient(), New HttpClient(handler))
            _http.Timeout = TimeSpan.FromSeconds(30)
        End Sub

        Public ReadOnly Property Options As PaymentOptions
            Get
                Return _options
            End Get
        End Property

        ''' <summary>Whether accounts can make deposits (BTCPay is configured).</summary>
        Public ReadOnly Property Available As Boolean
            Get
                Return _options.Configured
            End Get
        End Property

        ''' <summary>The BTCPay Server's origin (scheme://host[:port]), for the checkout frame; Nothing when not configured.</summary>
        Public ReadOnly Property CheckoutOrigin As String
            Get
                Dim u As Uri = Nothing
                If Not Available OrElse Not Uri.TryCreate(_options.BtcPayUrl, UriKind.Absolute, u) Then Return Nothing
                Return u.GetLeftPart(UriPartial.Authority)
            End Get
        End Property

        ''' <summary>Makes a BTCPay invoice for a whole amount and records it as a pending deposit of the account.</summary>
        Public Async Function CreateInvoiceAsync(username As String, amount As Integer, ct As CancellationToken) As Task(Of Deposit)
            If Not Available Then Throw New InvalidOperationException("Deposits are not set up on this server.")
            If amount < _options.MinDeposit OrElse amount > _options.MaxDeposit Then
                Throw New ArgumentException($"Enter a whole amount from {_options.MinDeposit} to {_options.MaxDeposit}.")
            End If
            Dim orderId = $"agentic-deposit-{username}-{DateTime.UtcNow:yyyyMMddHHmmss}"
            Dim checkout As New JsonObject From {{"redirectAutomatically", False}}
            If Not String.IsNullOrWhiteSpace(_options.NotificationUrl) Then checkout("notificationUrl") = _options.NotificationUrl
            If Not String.IsNullOrWhiteSpace(_options.RedirectUrl) Then
                checkout("redirectURL") = _options.RedirectUrl
                checkout("redirectAutomatically") = True
            End If
            Dim payload As New JsonObject From {
                {"amount", amount.ToString(Globalization.CultureInfo.InvariantCulture)},
                {"currency", _options.Currency},
                {"metadata", New JsonObject From {
                    {"orderId", orderId}, {"username", username}, {"buyerName", username}, {"server", "agentic"},
                    {"itemDesc", $"Agentic balance deposit ({amount} {_options.Currency})"}}},
                {"checkout", checkout}}
            Dim url = $"{_options.BtcPayUrl.TrimEnd("/"c)}/api/v1/stores/{Uri.EscapeDataString(_options.StoreId)}/invoices"
            Using req As New HttpRequestMessage(HttpMethod.Post, url)
                req.Headers.TryAddWithoutValidation("Authorization", "token " & _options.ApiKey)
                req.Content = New StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
                Using resp = Await _http.SendAsync(req, ct).ConfigureAwait(False)
                    Dim body = Await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(False)
                    If Not resp.IsSuccessStatusCode Then
                        _log.Warn("Payments", $"BTCPay refused the invoice for {username} ({amount} {_options.Currency}): HTTP {CInt(resp.StatusCode)} {TextUtil.Truncate(body, 300)}")
                        Throw New InvalidOperationException("The payment server did not make an invoice. Try again in a moment.")
                    End If
                    Dim json = JsonNode.Parse(body)
                    Dim id = json?("id")?.ToString()
                    If String.IsNullOrWhiteSpace(id) Then Throw New InvalidOperationException("The payment server's answer had no invoice id.")
                    Dim d As New Deposit With {
                        .InvoiceId = id, .Username = username, .AmountMicros = amount * 1_000_000L, .Currency = _options.Currency, .Status = "pending",
                        .CheckoutLink = json?("checkoutLink")?.ToString(), .CreatedUtc = DateTime.UtcNow}
                    Await _db.ExecuteAsync("INSERT OR IGNORE INTO Deposits(InvoiceId, Username, AmountMicros, Currency, Status, CheckoutLink, CreatedUtc) VALUES (@i, @u, @a, @c, 'pending', @l, @n)",
                                           ("@i", CObj(d.InvoiceId)), ("@u", username), ("@a", d.AmountMicros), ("@c", d.Currency), ("@l", d.CheckoutLink), ("@n", Db.Ts(d.CreatedUtc))).ConfigureAwait(False)
                    _log.Info("Payments", $"Invoice {id} made for {username}: {amount} {_options.Currency}.")
                    Return d
                End Using
            End Using
        End Function

        ''' <summary>
        ''' Adds a paid deposit to its account, once per invoice. An invoice made here needs no username or amount (they were
        ''' recorded); one made elsewhere needs both. A second call for the same invoice changes nothing (Credited = False).
        ''' </summary>
        Public Async Function CreditAsync(invoiceId As String, username As String, amountMicros As Long?, actor As String) As Task(Of DepositCredit)
            If String.IsNullOrWhiteSpace(invoiceId) OrElse invoiceId.Length > 200 Then Throw New ArgumentException("invoiceId is required.")
            invoiceId = invoiceId.Trim()
            Dim known = Await GetAsync(invoiceId).ConfigureAwait(False)
            If known Is Nothing Then
                If String.IsNullOrWhiteSpace(username) OrElse Not amountMicros.HasValue Then
                    Throw New KeyNotFoundException("Unknown invoice: send username and amount to credit an invoice made elsewhere.")
                End If
                If amountMicros.Value <= 0 Then Throw New ArgumentException("amount must be more than 0.")
                Dim acc = Await _accounts.GetAccountAsync(username.Trim()).ConfigureAwait(False)
                If acc Is Nothing Then Throw New KeyNotFoundException($"User '{username}' does not exist.")
                Await _db.ExecuteAsync("INSERT OR IGNORE INTO Deposits(InvoiceId, Username, AmountMicros, Currency, Status, CreatedUtc) VALUES (@i, @u, @a, @c, 'pending', @n)",
                                       ("@i", CObj(invoiceId)), ("@u", acc.Username), ("@a", amountMicros.Value), ("@c", _options.Currency), ("@n", Db.Ts(DateTime.UtcNow))).ConfigureAwait(False)
                known = Await GetAsync(invoiceId).ConfigureAwait(False)
            ElseIf Not String.IsNullOrWhiteSpace(username) AndAlso Not String.Equals(username.Trim(), known.Username, StringComparison.OrdinalIgnoreCase) Then
                Throw New InvalidOperationException($"Invoice {invoiceId} belongs to another account.")
            End If
            ' The payment server may report what was really paid; otherwise the invoiced amount is credited.
            Dim amount = If(amountMicros.HasValue AndAlso amountMicros.Value > 0, amountMicros.Value, known.AmountMicros)
            ' Claim the invoice first (one UPDATE), so two calls at once cannot both credit it.
            Dim claimed = Await _db.ExecuteAsync("UPDATE Deposits SET Status='credited', AmountMicros=@a, CreditedUtc=@n, CreditedBy=@by WHERE InvoiceId=@i AND Status<>'credited'",
                                                 ("@a", CObj(amount)), ("@n", Db.Ts(DateTime.UtcNow)), ("@by", CreditedByPrefix & actor), ("@i", invoiceId)).ConfigureAwait(False)
            If claimed = 0 Then
                Dim acc = Await _accounts.GetAccountAsync(known.Username).ConfigureAwait(False)
                Return New DepositCredit With {.InvoiceId = invoiceId, .Username = known.Username, .AmountMicros = known.AmountMicros, .Credited = False,
                                               .BalanceMicros = If(acc?.BalanceMicros, 0L)}
            End If
            Dim balance As Long
            Try
                balance = Await _accounts.AdjustAsync(known.Username, amount, $"Deposit: BTCPay invoice {invoiceId}", CreditedByPrefix & actor).ConfigureAwait(False)
            Catch ex As Exception
                ' Not credited after all: the invoice can be credited by a later call.
                Dim unused = _db.ExecuteAsync("UPDATE Deposits SET Status='pending', CreditedUtc=NULL, CreditedBy=NULL WHERE InvoiceId=@i", ("@i", CObj(invoiceId)))
                Throw
            End Try
            _log.Info("Payments", $"Deposit {invoiceId} credited to {known.Username}: {TextUtil.FormatMicros(amount)} (by {actor}).")
            Return New DepositCredit With {.InvoiceId = invoiceId, .Username = known.Username, .AmountMicros = amount, .Credited = True, .BalanceMicros = balance}
        End Function

        Public Function GetAsync(invoiceId As String) As Task(Of Deposit)
            Return _db.QuerySingleAsync("SELECT * FROM Deposits WHERE InvoiceId=@i", AddressOf Map, ("@i", CObj(invoiceId)))
        End Function

        Public Function ListAsync(username As String, limit As Integer) As Task(Of List(Of Deposit))
            Return _db.QueryAsync("SELECT * FROM Deposits WHERE Username=@u ORDER BY CreatedUtc DESC LIMIT @l", AddressOf Map,
                                  ("@u", CObj(username)), ("@l", Math.Clamp(limit, 1, 500)))
        End Function

        Private Shared Function Map(r As SqliteDataReader) As Deposit
            Return New Deposit With {
                .InvoiceId = Db.Str(r, "InvoiceId"), .Username = Db.Str(r, "Username"), .AmountMicros = Db.Lng(r, "AmountMicros"),
                .Currency = Db.Str(r, "Currency"), .Status = Db.Str(r, "Status"), .CheckoutLink = Db.Str(r, "CheckoutLink"),
                .CreatedUtc = Db.Dt(r, "CreatedUtc"), .CreditedUtc = Db.DtN(r, "CreditedUtc"), .CreditedBy = Db.Str(r, "CreditedBy")}
        End Function
    End Class

End Namespace
