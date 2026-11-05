Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports Agentic.AI
Imports Agentic.Economy
Imports Agentic.Utilities

Namespace Http

    ' Admin API: account balances and Model Square. Every account sees its own balance and what it spent; administrators add
    ' money to accounts and set the price of every model (provider models and custom models). User accounts pay those prices
    ' for their chats and API calls, and their agents' cloud use; administrators are never charged.
    Partial Friend NotInheritable Class AdminApi

        Private Sub RegisterAccounts(r As Router)
            r.Map("GET", "/api/account", AuthLevel.Read, AddressOf MyAccount)
            r.Map("POST", "/api/account/deposits", AuthLevel.Read, AddressOf CreateDeposit)
            r.Map("GET", "/api/account/deposits/{invoiceId}", AuthLevel.Read, AddressOf DepositStatus)
            ' The payment server (BTCPay webhook handler) confirms a paid deposit; it signs in with the X-Payment-Key header.
            r.Map("POST", "/api/payments/deposit", AuthLevel.Anonymous, AddressOf PaymentDeposit)
            r.Map("GET", "/api/model-square", AuthLevel.Read, AddressOf ModelSquare)
            r.Map("PUT", "/api/model-square", AuthLevel.Admin, AddressOf SetModelSquare)
            r.Map("POST", "/api/users/{username}/balance", AuthLevel.Admin, AddressOf AddBalance)
        End Sub

        ' ------------------------------------------------------------------ model access and money checks

        ''' <summary>
        ''' Why this account may not use the model now, or Nothing: the model is not among the account's models, or a User
        ''' account with an empty balance asks for a model that has a price.
        ''' </summary>
        Private Async Function ModelUseProblemAsync(rc As RequestContext, model As String, Optional routedModel As String = Nothing) As Task(Of String)
            If String.IsNullOrWhiteSpace(model) Then Return Nothing
            If Not rc.User.CanUseModel(model) Then Return $"You do not have access to the model '{model}'. Ask an administrator to add it to your account."
            If Not (rc.User.ViaSession OrElse rc.User.ViaApiKey) Then Return Nothing   ' API tokens are not accounts
            Return Await _h.Accounts.BlockReasonAsync(rc.User.Name, rc.User.IsAdmin, model, routedModel).ConfigureAwait(False)
        End Function

        ''' <summary>A User account may only give agents the models it may use itself (by the model id behind the key).</summary>
        Private Sub RequireModelKeyAccess(rc As RequestContext, modelKey As String)
            If Not rc.User.IsRestrictedUser OrElse String.IsNullOrWhiteSpace(modelKey) Then Return
            Dim m = _h.ModelRegistry.TryGet(modelKey)
            If m Is Nothing Then Return
            If Not rc.User.CanUseModel(m.Options.ModelId) Then
                Throw New ApiException(403, "MODEL_NOT_ALLOWED", $"You may not use the model '{m.Options.ModelId}' ({modelKey}), so you cannot give it to an agent.")
            End If
        End Sub

        ''' <summary>A Model Square price change also prices the agents' model keys that use that model (cloud keys; local stays free for agents).</summary>
        Private Async Function SyncAgentPricesAsync(modelId As String, inputPerMillion As Decimal, outputPerMillion As Decimal, actor As String) As Task(Of List(Of String))
            Dim keys As New List(Of String)
            For Each m In _h.ModelRegistry.All().Where(Function(x) Not x.Options.IsLocal AndAlso String.Equals(x.Options.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
                Dim existing = _h.Pricing.GetPrice(m.Key)
                If existing.Source = "none" Then Continue For
                If existing.InputPerMillion.GetValueOrDefault() = inputPerMillion AndAlso existing.OutputPerMillion.GetValueOrDefault() = outputPerMillion AndAlso existing.IsPriced Then Continue For
                Await _h.Pricing.SetPriceAsync(m.Key, inputPerMillion, outputPerMillion, existing.PerImage, actor).ConfigureAwait(False)
                keys.Add(m.Key)
            Next
            Return keys
        End Function

        ' ------------------------------------------------------------------ my account

        ''' <summary>The signed-in account's balance, what it spent (today, 30 days, by model) and its recent ledger; administrators also get every account.</summary>
        Private Async Function MyAccount(rc As RequestContext) As Task
            Dim name = rc.User.Name
            Dim acc = If(rc.User.ViaSession OrElse rc.User.ViaApiKey, Await _h.Accounts.GetAccountAsync(name).ConfigureAwait(False), Nothing)
            Dim since30 = DateTime.UtcNow.AddDays(-30)
            Dim days = rc.QueryInt("days", 30, 7, 365)
            Dim since = DateTime.UtcNow.Date.AddDays(1 - days)
            Dim spent30 = Await _h.Accounts.SpentSinceAsync(since30).ConfigureAwait(False)
            Dim spentToday = Await _h.Accounts.SpentSinceAsync(DateTime.UtcNow.Date).ConfigureAwait(False)
            Dim mine = Function(d As Dictionary(Of String, Long)) As Long
                           Dim v As Long
                           Return If(d.TryGetValue(name, v), v, 0L)
                       End Function
            Dim ownedAgents = _h.Agents.GetSnapshots().Where(Function(s) String.Equals(s.Owner, name, StringComparison.OrdinalIgnoreCase)).
                Select(Function(s) New With {.id = s.Id, .name = s.Name, .state = s.State, .unfunded = s.Unfunded}).ToList()
            Dim accounts As Object = Nothing
            If rc.User.IsAdmin Then
                accounts = (Await _h.Accounts.ListAccountsAsync().ConfigureAwait(False)).Select(Function(a) New With {
                    .username = a.Username, .isAdmin = a.IsAdmin, .balanceMicros = a.BalanceMicros, .spent30dMicros = SpentOf(spent30, a.Username),
                    .agents = _h.Agents.GetSnapshots().Where(Function(s) String.Equals(s.Owner, a.Username, StringComparison.OrdinalIgnoreCase)).Count()}).ToList()
            End If
            Await rc.OkAsync(New With {
                .username = name, .isAdmin = rc.User.IsAdmin, .charged = acc IsNot Nothing AndAlso acc.Charged,
                .balanceMicros = If(acc?.BalanceMicros, 0L), .spentTodayMicros = mine(spentToday), .spent30dMicros = mine(spent30),
                .ledger = If(acc Is Nothing, New List(Of AccountEntry), Await _h.Accounts.LedgerAsync(name, rc.QueryInt("limit", 200, 1, 1000)).ConfigureAwait(False)),
                .byModel30d = If(acc Is Nothing, New List(Of Dictionary(Of String, Object)), Await _h.Accounts.SpendByModelAsync(name, since30).ConfigureAwait(False)),
                .agents = ownedAgents, .accounts = accounts,
                .stats = Await AccountStatsAsync(name, acc IsNot Nothing, days, since).ConfigureAwait(False),
                .deposits = Await DepositsInfoAsync(rc, acc IsNot Nothing).ConfigureAwait(False)}).ConfigureAwait(False)
        End Function

        ''' <summary>The Balance tab's statistics: money per day and type, closing balances, spend over 7 days, all-time totals.</summary>
        Private Async Function AccountStatsAsync(name As String, hasAccount As Boolean, days As Integer, since As DateTime) As Task(Of Object)
            If Not hasAccount Then Return Nothing
            Dim life = Await _h.Accounts.LifetimeAsync(name).ConfigureAwait(False)
            Dim week = Await _h.Accounts.SpentSinceAsync(DateTime.UtcNow.Date.AddDays(-6)).ConfigureAwait(False)
            Return New With {
                .days = days, .sinceUtc = since,
                .daily = Await _h.Accounts.DailyActivityAsync(name, since).ConfigureAwait(False),
                .closingBalances = Await _h.Accounts.ClosingBalancesAsync(name, since).ConfigureAwait(False),
                .openingBalanceMicros = Await _h.Accounts.BalanceBeforeAsync(name, since).ConfigureAwait(False),
                .spent7dMicros = SpentOf(week, name),
                .lifetime = New With {.addedMicros = life.AddedMicros, .spentMicros = life.SpentMicros, .charges = life.Charges, .firstUtc = life.FirstUtc},
                .surcharge = Await _h.Accounts.HasSurchargeAsync(name).ConfigureAwait(False), .surchargePercent = AccountService.SurchargePercent}
        End Function

        ''' <summary>Whether this account can deposit, the limits, and its recent deposits.</summary>
        Private Async Function DepositsInfoAsync(rc As RequestContext, hasAccount As Boolean) As Task(Of Object)
            Dim d = _h.Deposits
            Dim o = If(d?.Options, New Global.Agentic.Configuration.PaymentOptions())
            Dim list = If(hasAccount AndAlso d IsNot Nothing, Await d.ListAsync(rc.User.Name, 20).ConfigureAwait(False), New List(Of Deposit))
            Return New With {
                .available = hasAccount AndAlso rc.User.ViaSession AndAlso d IsNot Nothing AndAlso d.Available,
                .min = o.MinDeposit, .max = o.MaxDeposit, .currency = o.Currency, .checkoutOrigin = d?.CheckoutOrigin,
                .recent = list.Select(AddressOf DepositDto).ToList()}
        End Function

        Private Shared Function DepositDto(x As Deposit) As Object
            Return New With {.invoiceId = x.InvoiceId, .amountMicros = x.AmountMicros, .currency = x.Currency, .status = x.Status,
                             .createdUtc = x.CreatedUtc, .creditedUtc = x.CreditedUtc}
        End Function

        ''' <summary>Body {amount}: a whole number from Payments.MinDeposit to MaxDeposit. Makes a BTCPay invoice for the signed-in account.</summary>
        Private Async Function CreateDeposit(rc As RequestContext) As Task
            If _h.Deposits Is Nothing OrElse Not _h.Deposits.Available Then Throw New ApiException(503, "DEPOSITS_OFF", "Deposits are not set up on this server.")
            If Await _h.Accounts.GetAccountAsync(rc.User.Name).ConfigureAwait(False) Is Nothing Then Throw ApiException.BadRequest("Only dashboard accounts can deposit.")
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim el As JsonElement
            Dim amount As Decimal
            If Not b.TryGetProperty("amount", el) OrElse Not TryWhole(el, amount) Then Throw ApiException.BadRequest("Enter a whole number.")
            Dim o = _h.Deposits.Options
            If amount < o.MinDeposit OrElse amount > o.MaxDeposit Then Throw ApiException.BadRequest($"Enter a whole amount from {o.MinDeposit} to {o.MaxDeposit}.")
            Dim d As Deposit
            Try
                d = Await _h.Deposits.CreateInvoiceAsync(rc.User.Name, CInt(amount), rc.CancellationToken).ConfigureAwait(False)
            Catch ex As InvalidOperationException
                Throw New ApiException(502, "PAYMENT_SERVER", ex.Message)
            Catch ex As Net.Http.HttpRequestException
                Throw New ApiException(502, "PAYMENT_SERVER", "The payment server cannot be reached. Try again in a moment.")
            End Try
            Await AuditAsync(rc, "deposit.invoice", "deposit", d.InvoiceId, New With {.amount = amount, .currency = d.Currency}).ConfigureAwait(False)
            Await rc.WriteJsonAsync(201, New With {.invoiceId = d.InvoiceId, .checkoutLink = d.CheckoutLink, .checkoutOrigin = _h.Deposits.CheckoutOrigin,
                                                   .amountMicros = d.AmountMicros, .currency = d.Currency}).ConfigureAwait(False)
        End Function

        Private Shared Function TryWhole(el As JsonElement, ByRef value As Decimal) As Boolean
            If el.ValueKind = JsonValueKind.Number Then
                If Not el.TryGetDecimal(value) Then Return False
            ElseIf el.ValueKind = JsonValueKind.String Then
                If Not Decimal.TryParse(el.GetString(), Globalization.NumberStyles.Integer, Globalization.CultureInfo.InvariantCulture, value) Then Return False
            Else
                Return False
            End If
            Return value = Decimal.Truncate(value)
        End Function

        ''' <summary>A deposit of the signed-in account (to see whether it was credited after the checkout closed).</summary>
        Private Async Function DepositStatus(rc As RequestContext) As Task
            Dim d = If(_h.Deposits Is Nothing, Nothing, Await _h.Deposits.GetAsync(rc.Route("invoiceId")).ConfigureAwait(False))
            If d Is Nothing OrElse Not String.Equals(d.Username, rc.User.Name, StringComparison.OrdinalIgnoreCase) Then Throw ApiException.NotFound("DEPOSIT_NOT_FOUND", "No such deposit.")
            Dim acc = Await _h.Accounts.GetAccountAsync(rc.User.Name).ConfigureAwait(False)
            Await rc.OkAsync(New With {.deposit = DepositDto(d), .balanceMicros = If(acc?.BalanceMicros, 0L)}).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' The payment server confirms a paid deposit. Header X-Payment-Key: Payments.DepositApiKey. Body
        ''' {invoiceId, username?, amount?}: an invoice made here needs only invoiceId; amount (USD, may have cents) overrides
        ''' the invoiced amount; an invoice made elsewhere needs username and amount. Each invoice is credited once:
        ''' repeating the call answers credited=false, alreadyCredited=true.
        ''' </summary>
        Private Async Function PaymentDeposit(rc As RequestContext) As Task
            Dim expected = If(_h.Deposits?.Options.DepositApiKey, "")
            Dim sent = If(rc.Http.Request.Headers("X-Payment-Key"), "").Trim()
            If expected.Length < 16 OrElse Not TextUtil.FixedTimeEquals(sent, expected) Then Throw New ApiException(401, "UNAUTHORIZED", "Missing or invalid X-Payment-Key.")
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim amount = ParseMoney(b, "amount", False)
            If amount.HasValue AndAlso amount.Value <= 0D Then Throw ApiException.BadRequest("amount must be more than 0.")
            Dim credit As DepositCredit
            Try
                credit = Await _h.Deposits.CreditAsync(JsonUtil.GetString(b, "invoiceId"), JsonUtil.GetString(b, "username"),
                                                       If(amount.HasValue, CLng(Math.Round(amount.Value * 1_000_000D)), CType(Nothing, Long?)), "btcpay").ConfigureAwait(False)
            Catch ex As KeyNotFoundException
                Throw ApiException.NotFound("DEPOSIT_NOT_FOUND", ex.Message)
            Catch ex As ArgumentException
                Throw ApiException.BadRequest(ex.Message)
            Catch ex As InvalidOperationException
                Throw New ApiException(409, "DEPOSIT_CONFLICT", ex.Message)
            End Try
            If credit.Credited Then
                rc.User = New AuthUser With {.Name = "payment-server"}
                Await AuditAsync(rc, "deposit.credit", "user", credit.Username, New With {credit.InvoiceId, credit.AmountMicros}).ConfigureAwait(False)
            End If
            Await rc.OkAsync(New With {.ok = True, .credited = credit.Credited, .alreadyCredited = Not credit.Credited, .invoiceId = credit.InvoiceId,
                                       .username = credit.Username, .amountMicros = credit.AmountMicros, .balanceMicros = credit.BalanceMicros}).ConfigureAwait(False)
        End Function

        ''' <summary>Model Square prices as the signed-in account pays them (raised by the surcharge when the account has it).</summary>
        Private Async Function PricesForAsync(rc As RequestContext) As Task(Of IReadOnlyDictionary(Of String, SquarePrice))
            Dim prices = _h.Accounts.Prices()
            If rc.User.IsAdmin OrElse Not Await _h.Accounts.HasSurchargeAsync(rc.User.Name).ConfigureAwait(False) Then Return prices
            Return prices.ToDictionary(Function(kv) kv.Key, Function(kv) AccountService.Surcharged(kv.Value), StringComparer.OrdinalIgnoreCase)
        End Function

        ''' <summary>
        ''' The address people reach this server at, without a trailing slash: Server.PublicUrl, else the request's
        ''' forwarded or Host header. Share links use it, so links made in the desktop app or VS Code do not point at 127.0.0.1.
        ''' </summary>
        Private Function PublicOrigin(rc As RequestContext) As String
            Dim configured = If(_h.Options.Server.PublicUrl, "").Trim().TrimEnd("/"c)
            If configured.Length > 0 Then Return configured
            Dim req = rc.Http.Request
            Dim proto = If(req.Headers("X-Forwarded-Proto"), "").Split(","c)(0).Trim().ToLowerInvariant()
            If proto <> "http" AndAlso proto <> "https" Then proto = req.Url.Scheme
            Dim host = If(req.Headers("X-Forwarded-Host"), "").Split(","c)(0).Trim()
            If host.Length = 0 Then host = If(req.Headers("Host"), req.Url.Authority)
            ' Only a host name (and port): anything else in the header is not used.
            If host.Length > 255 OrElse host.IndexOfAny({"/"c, "\"c, "@"c, " "c, "?"c, "#"c, """"c, "<"c, ">"c}) >= 0 Then host = req.Url.Authority
            Return proto & "://" & host
        End Function

        Private Shared Function SpentOf(d As Dictionary(Of String, Long), user As String) As Long
            Dim v As Long
            Return If(d.TryGetValue(user, v), v, 0L)
        End Function

        ''' <summary>Body {amount (USD; negative takes money away), note}: an administrator adds money to an account.</summary>
        Private Async Function AddBalance(rc As RequestContext) As Task
            Dim username = rc.Route("username")
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim amount = ParseMoney(b, "amount").Value
            If amount = 0 Then Throw ApiException.BadRequest("Enter an amount other than 0.")
            If Math.Abs(amount) > 1000000D Then Throw ApiException.BadRequest("That amount is too large.")
            Dim note = TextUtil.Truncate(JsonUtil.GetString(b, "note"), 300)
            Dim balance As Long
            Try
                balance = Await _h.Accounts.AdjustAsync(username, TextUtil.DollarsToMicros(amount),
                                                        If(String.IsNullOrWhiteSpace(note), If(amount > 0, "Money added by ", "Money removed by ") & rc.User.Name, note), rc.User.Name).ConfigureAwait(False)
            Catch ex As KeyNotFoundException
                Throw ApiException.NotFound("USER_NOT_FOUND", "User does not exist.")
            End Try
            Await AuditAsync(rc, "add-balance", "user", username, New With {.amount = amount, .note = note, .balanceMicros = balance}).ConfigureAwait(False)
            Await rc.OkAsync(New With {.username = username, .balanceMicros = balance}).ConfigureAwait(False)
        End Function

        ' ------------------------------------------------------------------ Model Square

        ''' <summary>
        ''' Every model with its price per 1M input and output tokens: custom models first, then the provider's models (only the
        ''' models this account may use), with the agents' model keys that use each one.
        ''' </summary>
        Private Async Function ModelSquare(rc As RequestContext) As Task
            Dim prices = Await PricesForAsync(rc).ConfigureAwait(False)
            Dim keysByModel = _h.ModelRegistry.All().Where(Function(m) m.Options.ByoOwner Is Nothing).GroupBy(Function(m) m.Options.ModelId, StringComparer.OrdinalIgnoreCase).
                ToDictionary(Function(g) g.Key, Function(g) g.Select(Function(m) New With {.key = m.Key, .isLocal = m.Options.IsLocal,
                                                                                         .slots = If(m.Options.IsLocal, m.Gate.Capacity, 0)}).ToList(), StringComparer.OrdinalIgnoreCase)
            ' How each model has been doing (calls in the last day, speed): the cards' status and the speed sorts.
            Dim status = Await ModelStatusFor(rc).ConfigureAwait(False)
            Dim rows As New List(Of Object)
            Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Dim add = Sub(id As String, name As String, ownedBy As String, isCustom As Boolean, note As String, description As String, window As Integer, createdUtc As DateTime?)
                          If Not seen.Add(id) OrElse Not rc.User.CanUseModel(id) Then Return
                          Dim p As SquarePrice = Nothing
                          prices.TryGetValue(id, p)
                          Dim keys As Object = Nothing
                          If keysByModel.ContainsKey(id) Then keys = keysByModel(id)
                          Dim health As Object = Nothing
                          status.TryGetValue(id, health)
                          rows.Add(New With {.id = id, .name = name, .ownedBy = ownedBy, .custom = isCustom, .note = note, .description = description,
                                             .group = If(isCustom, "custom", _h.ModelHub.ProviderGroup(id)), .contextWindow = If(window > 0, window, If(_h.Chat Is Nothing, 0, _h.Chat.WindowFor(id))),
                                             .createdUtc = createdUtc, .status = health,
                                             .inputPerMillion = If(p Is Nothing, 0D, p.InputPerMillion), .outputPerMillion = If(p Is Nothing, 0D, p.OutputPerMillion),
                                             .cachedInputPerMillion = p?.CachedInputPerMillion, .cachedEffective = If(p Is Nothing, 0D, p.EffectiveCachedPerMillion),
                                             .priced = p IsNot Nothing, .updatedBy = p?.UpdatedBy, .updatedUtc = p?.UpdatedUtc, .agentKeys = keys})
                      End Sub
            For Each c In Await _h.ModelHub.ListCustomAsync().ConfigureAwait(False)
                add(c.Id, c.Name, "custom", True, If(c.Enabled, "routes to " & c.Definition.DefaultModel, "disabled"), c.Description, If(_h.Chat Is Nothing, 0, _h.Chat.CustomWindow(c)), c.CreatedUtc)
            Next
            Dim providerError As String = Nothing
            Try
                For Each m In Await _h.ModelHub.ListUpstreamAsync(rc.CancellationToken).ConfigureAwait(False)
                    add(m.Id, m.Name, m.OwnedBy, False, Nothing, Nothing, 0, If(m.Created > 0, DateTimeOffset.FromUnixTimeSeconds(m.Created).UtcDateTime, CType(Nothing, DateTime?)))
                Next
            Catch ex As Exception When TypeOf ex Is Net.Http.HttpRequestException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is TaskCanceledException
                providerError = ex.Message
            End Try
            ' Models the provider no longer offers (an agent's model that went away, a price left from an old model) are left
            ' out, and with them a provider none of whose models is offered. Only when the provider's list could not be read
            ' are the agents' models and the priced ones shown from what is known.
            If providerError IsNot Nothing Then
                For Each m In _h.ModelRegistry.All().Where(Function(x) x.Options.ByoOwner Is Nothing)
                    add(m.Options.ModelId, m.Options.ModelId, "agents", False, Nothing, m.Options.Description, m.ContextWindow, Nothing)
                Next
                For Each id In prices.Keys.ToList()
                    add(id, id, "?", False, "provider list unavailable", Nothing, 0, Nothing)
                Next
            End If
            Await rc.OkAsync(New With {.models = rows, .canEdit = rc.User.IsAdmin, .providerError = providerError, .currency = _h.Options.Economy.Currency}).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' Body {prices: [{model, inputPerMillion, outputPerMillion, cachedInputPerMillion?}]} (USD per 1M tokens; 0 and 0 = free).
        ''' cachedInputPerMillion: the cached input price; null = 10% of the input price; left out = unchanged.
        ''' </summary>
        Private Async Function SetModelSquare(rc As RequestContext) As Task
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim el As JsonElement
            If Not b.TryGetProperty("prices", el) OrElse el.ValueKind <> JsonValueKind.Array Then Throw ApiException.BadRequest("Send {prices: [{model, inputPerMillion, outputPerMillion}]}.")
            Dim changes As New List(Of (Model As String, InP As Decimal, OutP As Decimal, Cached As Decimal?, HasCached As Boolean))
            For Each p In el.EnumerateArray()
                Dim model = JsonUtil.GetString(p, "model")
                If String.IsNullOrWhiteSpace(model) Then Throw ApiException.BadRequest("Every price needs its 'model'.")
                Dim cachedEl As JsonElement
                Dim hasCached = p.TryGetProperty("cachedInputPerMillion", cachedEl)
                changes.Add((model.Trim(), If(ParseMoney(p, "inputPerMillion", False), 0D), If(ParseMoney(p, "outputPerMillion", False), 0D),
                             If(hasCached, ParseMoney(p, "cachedInputPerMillion", False), Nothing), hasCached))
            Next
            If changes.Count = 0 Then Throw ApiException.BadRequest("No prices to change.")
            If changes.Count > 2000 Then Throw ApiException.BadRequest("Too many prices at once.")
            Dim agentKeys As New List(Of String)
            For Each c In changes
                Try
                    Await _h.Accounts.SetPriceAsync(c.Model, c.InP, c.OutP, rc.User.Name, c.Cached, keepCached:=Not c.HasCached).ConfigureAwait(False)
                Catch ex As ArgumentException
                    Throw ApiException.BadRequest($"{c.Model}: {ex.Message}")
                End Try
                If c.InP > 0 OrElse c.OutP > 0 Then agentKeys.AddRange(Await SyncAgentPricesAsync(c.Model, c.InP, c.OutP, rc.User.Name).ConfigureAwait(False))
            Next
            Await AuditAsync(rc, "set-model-prices", "model-square", Nothing, New With {.prices = changes.Select(Function(c) New With {.model = c.Model, .inputPerMillion = c.InP, .outputPerMillion = c.OutP, .cachedInputPerMillion = c.Cached}), .agentKeys = agentKeys}).ConfigureAwait(False)
            Await rc.OkAsync(New With {.changed = changes.Count, .agentKeys = agentKeys}).ConfigureAwait(False)
        End Function
    End Class

End Namespace
