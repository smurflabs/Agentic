Option Strict On
Option Explicit On
Option Infer On

Imports Agentic.Logging
Imports Agentic.Persistence
Imports Agentic.Utilities
Imports Microsoft.Data.Sqlite

Namespace Economy

    ''' <summary>One movement of an account's money.</summary>
    Public NotInheritable Class AccountEntry
        Public Property Id As Long
        Public Property Username As String
        Public Property EntryType As String
        Public Property AmountMicros As Long
        Public Property BalanceAfterMicros As Long
        Public Property Model As String
        Public Property PromptTokens As Long?
        Public Property CompletionTokens As Long?
        Public Property AgentId As String
        Public Property Description As String
        Public Property CreatedBy As String
        Public Property CreatedUtc As DateTime
    End Class

    ''' <summary>A Model Square price: USD per 1M input and per 1M output tokens.</summary>
    Public NotInheritable Class SquarePrice
        Public Property ModelId As String
        Public Property InputPerMillion As Decimal
        Public Property OutputPerMillion As Decimal
        ''' <summary>USD per 1M cached input tokens (served from the provider's cache); Nothing = <see cref="AccountService.DefaultCachedFraction"/> of the input price.</summary>
        Public Property CachedInputPerMillion As Decimal?
        Public Property UpdatedBy As String
        Public Property UpdatedUtc As DateTime

        ''' <summary>The price of a cached input token: its own price, else a share of the input price.</summary>
        Public ReadOnly Property EffectiveCachedPerMillion As Decimal
            Get
                Return If(CachedInputPerMillion, InputPerMillion * AccountService.DefaultCachedFraction)
            End Get
        End Property
    End Class

    ''' <summary>A dashboard account's money.</summary>
    Public NotInheritable Class AccountBalance
        Public Property Username As String
        Public Property IsAdmin As Boolean
        Public Property BalanceMicros As Long

        ''' <summary>User-role accounts pay for what they use; administrators never do.</summary>
        Public ReadOnly Property Charged As Boolean
            Get
                Return Not IsAdmin
            End Get
        End Property
    End Class

    ''' <summary>
    ''' Account balances and Model Square prices. A User-role account pays from its balance (it starts at 0 and an
    ''' administrator adds money) for its chats, its API calls and its agents' cloud model use; when the balance runs out,
    ''' its agents stop. Administrator accounts are never charged and their agents never stop for money. Every movement
    ''' goes to the AccountLedger; balances change with atomic SQL updates.
    ''' </summary>
    Public NotInheritable Class AccountService
        Public Const EntryCredit As String = "Credit"
        Public Const EntryChat As String = "Chat"
        Public Const EntryApi As String = "Api"
        Public Const EntryAgent As String = "Agent"
        ''' <summary>Speech from the XTTS server: text in, audio out, voice uploads.</summary>
        Public Const EntrySpeech As String = "Speech"
        ''' <summary>Songs from Suno (the Music tab) and lyrics written by it.</summary>
        Public Const EntryMusic As String = "Music"
        ''' <summary>Highest price an administrator may set (USD per 1M tokens): a typo guard.</summary>
        Public Const MaxPricePerMillion As Decimal = 10000D

        ''' <summary>"Tack on 15% surcharge": an account with it set pays this much more for every fee (tokens, vision, images, videos, agents).</summary>
        Public Const SurchargePercent As Integer = 15

        ''' <summary>Accounts' surcharge setting, read once per account (forgotten when an administrator changes the account).</summary>
        Private ReadOnly _surcharge As New System.Collections.Concurrent.ConcurrentDictionary(Of String, Boolean)(StringComparer.OrdinalIgnoreCase)

        Private ReadOnly _db As Database
        Private ReadOnly _log As ILog
        Private _prices As New Dictionary(Of String, SquarePrice)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _priceLock As New Object()

        Public Sub New(db As Database, log As ILog)
            _db = db
            _log = log
        End Sub

        ''' <summary>
        ''' Called after every change of an account's balance: (username, balance, charged). Charged is False for
        ''' administrators. Must not block (the agents of an account stop at 0 and continue once money is added).
        ''' </summary>
        Public Property BalanceChanged As Action(Of String, Long, Boolean)

        Private Sub Raise(username As String, balanceMicros As Long, charged As Boolean)
            Try
                BalanceChanged?.Invoke(username, balanceMicros, charged)
            Catch ex As Exception
                _log.Warn("Accounts", "Balance hook failed: " & ex.Message)
            End Try
        End Sub

        ' ------------------------------------------------------------------ Model Square

        Public Async Function LoadAsync() As Task
            Dim rows = Await _db.QueryAsync("SELECT * FROM ModelSquarePrices", AddressOf MapPrice).ConfigureAwait(False)
            Dim d As New Dictionary(Of String, SquarePrice)(StringComparer.OrdinalIgnoreCase)
            For Each p In rows
                d(p.ModelId) = p
            Next
            SyncLock _priceLock
                _prices = d
            End SyncLock
        End Function

        Private Shared Function MapPrice(r As SqliteDataReader) As SquarePrice
            Return New SquarePrice With {.ModelId = Db.Str(r, "ModelId"), .InputPerMillion = If(Db.Dec(r, "InputPerMillion"), 0D),
                                        .OutputPerMillion = If(Db.Dec(r, "OutputPerMillion"), 0D), .CachedInputPerMillion = Db.Dec(r, "CachedInputPerMillion"), .UpdatedBy = Db.Str(r, "UpdatedBy"), .UpdatedUtc = Db.Dt(r, "UpdatedUtc")}
        End Function

        ''' <summary>Every model with a price (the rest are free).</summary>
        Public Function Prices() As IReadOnlyDictionary(Of String, SquarePrice)
            SyncLock _priceLock
                Return _prices
            End SyncLock
        End Function

        ''' <summary>The model's price, or Nothing when it has none (free).</summary>
        Public Function PriceOf(modelId As String) As SquarePrice
            If String.IsNullOrWhiteSpace(modelId) Then Return Nothing
            Dim p As SquarePrice = Nothing
            Return If(Prices().TryGetValue(modelId, p), p, Nothing)
        End Function

        ''' <summary>
        ''' What a call is charged at: the price of the model that answered (the model a custom model routed to, or the vision
        ''' model that answered for a model that cannot see pictures), else the price of the model asked for. Nothing = free.
        ''' </summary>
        Public Function PriceForUse(model As String, routedModel As String) As SquarePrice
            Return If(PriceOf(routedModel), PriceOf(model))
        End Function

        ''' <summary>Sets a model's price (USD per 1M tokens); 0 and 0 makes it free again.</summary>
        Public Async Function SetPriceAsync(modelId As String, inputPerMillion As Decimal, outputPerMillion As Decimal, actor As String,
                                            Optional cachedInputPerMillion As Decimal? = Nothing, Optional keepCached As Boolean = True) As Task(Of SquarePrice)
            If String.IsNullOrWhiteSpace(modelId) OrElse modelId.Length > 200 Then Throw New ArgumentException("A model id is required.")
            If inputPerMillion < 0 OrElse outputPerMillion < 0 OrElse cachedInputPerMillion.GetValueOrDefault() < 0 Then Throw New ArgumentException("Prices must be 0 or more.")
            If cachedInputPerMillion.GetValueOrDefault() > MaxPricePerMillion Then Throw New ArgumentException($"Prices above ${MaxPricePerMillion:N0} per 1M tokens are not allowed.")
            ' A price change that does not mention the cached price keeps the one already set.
            If Not cachedInputPerMillion.HasValue AndAlso keepCached Then cachedInputPerMillion = PriceOf(modelId?.Trim())?.CachedInputPerMillion
            If inputPerMillion > MaxPricePerMillion OrElse outputPerMillion > MaxPricePerMillion Then Throw New ArgumentException($"Prices above ${MaxPricePerMillion:N0} per 1M tokens are not allowed.")
            modelId = modelId.Trim()
            If inputPerMillion = 0 AndAlso outputPerMillion = 0 Then
                Await ClearPriceAsync(modelId).ConfigureAwait(False)
                Return Nothing
            End If
            Dim now = DateTime.UtcNow
            Await _db.ExecuteAsync("INSERT INTO ModelSquarePrices(ModelId, InputPerMillion, OutputPerMillion, CachedInputPerMillion, UpdatedBy, UpdatedUtc) VALUES (@m, @i, @o, @c, @by, @n) " &
                                   "ON CONFLICT(ModelId) DO UPDATE SET InputPerMillion=@i, OutputPerMillion=@o, CachedInputPerMillion=@c, UpdatedBy=@by, UpdatedUtc=@n",
                                   ("@m", CObj(modelId)), ("@i", inputPerMillion), ("@o", outputPerMillion),
                                   ("@c", If(cachedInputPerMillion.HasValue, CObj(cachedInputPerMillion.Value), Nothing)), ("@by", actor), ("@n", Db.Ts(now))).ConfigureAwait(False)
            Dim p As New SquarePrice With {.ModelId = modelId, .InputPerMillion = inputPerMillion, .OutputPerMillion = outputPerMillion,
                                           .CachedInputPerMillion = cachedInputPerMillion, .UpdatedBy = actor, .UpdatedUtc = now}
            SyncLock _priceLock
                Dim d As New Dictionary(Of String, SquarePrice)(_prices, StringComparer.OrdinalIgnoreCase)
                d(modelId) = p
                _prices = d
            End SyncLock
            Return p
        End Function

        Public Async Function ClearPriceAsync(modelId As String) As Task
            Await _db.ExecuteAsync("DELETE FROM ModelSquarePrices WHERE ModelId=@m", ("@m", CObj(modelId))).ConfigureAwait(False)
            SyncLock _priceLock
                Dim d As New Dictionary(Of String, SquarePrice)(_prices, StringComparer.OrdinalIgnoreCase)
                d.Remove(modelId)
                _prices = d
            End SyncLock
        End Function

        ''' <summary>A cached input token's price when the model has none of its own: providers charge about a tenth.</summary>
        Public Const DefaultCachedFraction As Decimal = 0.1D

        ''' <summary>
        ''' tokens × (USD per 1M tokens) = micro-USD, rounded up. cachedTokens are the part of promptTokens the provider served
        ''' from its cache (a long chat's tool rounds resend the same history); they cost the cached input price.
        ''' </summary>
        Public Shared Function CostMicros(p As SquarePrice, promptTokens As Long, completionTokens As Long, Optional cachedTokens As Long = 0) As Long
            If p Is Nothing Then Return 0
            Dim prompt = Math.Max(0L, promptTokens)
            Dim cached = Math.Min(prompt, Math.Max(0L, cachedTokens))
            Dim cost = CDec(prompt - cached) * p.InputPerMillion + CDec(cached) * p.EffectiveCachedPerMillion + CDec(Math.Max(0L, completionTokens)) * p.OutputPerMillion
            Return CLng(Math.Ceiling(cost))
        End Function

        ' ------------------------------------------------------------------ balances

        ''' <summary>The account's money, or Nothing when there is no such account (API tokens, unknown names).</summary>
        Public Async Function GetAccountAsync(username As String) As Task(Of AccountBalance)
            If String.IsNullOrWhiteSpace(username) Then Return Nothing
            Return Await _db.QuerySingleAsync("SELECT Username, Role, BalanceMicros FROM AdminUsers WHERE Username=@u", AddressOf MapBalance, ("@u", CObj(username))).ConfigureAwait(False)
        End Function

        Public Function ListAccountsAsync() As Task(Of List(Of AccountBalance))
            Return _db.QueryAsync("SELECT Username, Role, BalanceMicros FROM AdminUsers ORDER BY CASE Role WHEN 'Admin' THEN 0 ELSE 1 END, Username", AddressOf MapBalance)
        End Function

        Private Shared Function MapBalance(r As SqliteDataReader) As AccountBalance
            Return New AccountBalance With {.Username = Db.Str(r, "Username"), .IsAdmin = String.Equals(Db.Str(r, "Role"), "Admin", StringComparison.OrdinalIgnoreCase),
                                           .BalanceMicros = Db.Lng(r, "BalanceMicros")}
        End Function

        ''' <summary>
        ''' Why the account may not use this model right now, or Nothing: a User account with an empty balance cannot use a
        ''' model that has a price. Free models, administrators and API tokens are never blocked.
        ''' </summary>
        Public Async Function BlockReasonAsync(username As String, isAdmin As Boolean, model As String, Optional routedModel As String = Nothing) As Task(Of String)
            If isAdmin Then Return Nothing
            Dim price = PriceForUse(model, routedModel)
            If price Is Nothing Then Return Nothing
            Dim acc = Await GetAccountAsync(username).ConfigureAwait(False)
            If acc Is Nothing OrElse Not acc.Charged OrElse acc.BalanceMicros > 0 Then Return Nothing
            Return $"Your balance is empty ({TextUtil.FormatMicros(acc.BalanceMicros)}). {model} costs ${price.InputPerMillion:0.####} in / ${price.OutputPerMillion:0.####} out per 1M tokens: ask an administrator to add money to your account."
        End Function

        ''' <summary>An administrator adds money to an account (or takes it away with a negative amount). Returns the new balance.</summary>
        Public Async Function AdjustAsync(username As String, micros As Long, description As String, actor As String) As Task(Of Long)
            If micros = 0 Then Throw New ArgumentException("The amount must not be zero.")
            Dim after = Await ApplyAsync(username, micros, EntryCredit, Nothing, Nothing, Nothing, Nothing, description, actor, usersOnly:=False).ConfigureAwait(False)
            If Not after.HasValue Then Throw New KeyNotFoundException("User does not exist.")
            Return after.Value
        End Function

        ''' <summary>
        ''' Takes a charge from a User account. Returns the balance after it, or Nothing when nothing was charged
        ''' (a zero amount, an administrator, an API token or an unknown account). The balance may end below zero.
        ''' </summary>
        Public Async Function ChargeAsync(username As String, micros As Long, entryType As String, model As String, promptTokens As Long?, completionTokens As Long?,
                                          agentId As String, description As String) As Task(Of Long?)
            If micros <= 0 OrElse String.IsNullOrWhiteSpace(username) Then Return Nothing
            ' The account's surcharge is taken here, so every fee (tokens, vision, images, videos, agents) carries it.
            Dim total = Await WithSurchargeAsync(username, micros).ConfigureAwait(False)
            If total <> micros Then description = $"{description} (+{SurchargePercent}% surcharge)"
            Return Await ApplyAsync(username, -total, entryType, model, promptTokens, completionTokens, agentId, description, "host", usersOnly:=True).ConfigureAwait(False)
        End Function

        ''' <summary>Whether the account pays the surcharge (only User accounts can).</summary>
        Public Async Function HasSurchargeAsync(username As String) As Task(Of Boolean)
            If String.IsNullOrWhiteSpace(username) Then Return False
            Dim known As Boolean
            If _surcharge.TryGetValue(username, known) Then Return known
            Dim v = Await _db.ScalarAsync("SELECT Surcharge FROM AdminUsers WHERE Username=@u AND Role='User'", ("@u", CObj(username))).ConfigureAwait(False)
            known = v IsNot Nothing AndAlso v IsNot DBNull.Value AndAlso Convert.ToInt64(v) <> 0
            _surcharge(username) = known
            Return known
        End Function

        ''' <summary>A fee as the account pays it: raised by <see cref="SurchargePercent"/> when the account has the surcharge.</summary>
        Public Async Function WithSurchargeAsync(username As String, micros As Long) As Task(Of Long)
            If micros <= 0 OrElse Not Await HasSurchargeAsync(username).ConfigureAwait(False) Then Return micros
            Return Surcharged(micros)
        End Function

        Public Shared Function Surcharged(micros As Long) As Long
            Return CLng(Math.Ceiling(micros * (100D + SurchargePercent) / 100D))
        End Function

        ''' <summary>A Model Square price as an account with the surcharge sees it.</summary>
        Public Shared Function Surcharged(p As SquarePrice) As SquarePrice
            If p Is Nothing Then Return Nothing
            Dim f = (100D + SurchargePercent) / 100D
            Return New SquarePrice With {.ModelId = p.ModelId, .InputPerMillion = Math.Round(p.InputPerMillion * f, 6), .OutputPerMillion = Math.Round(p.OutputPerMillion * f, 6),
                                         .CachedInputPerMillion = If(p.CachedInputPerMillion.HasValue, CType(Math.Round(p.CachedInputPerMillion.Value * f, 6), Decimal?), Nothing),
                                         .UpdatedBy = p.UpdatedBy, .UpdatedUtc = p.UpdatedUtc}
        End Function

        ''' <summary>
        ''' How many of a call's prompt tokens the provider served from its cache, from its usage block: OpenAI
        ''' (prompt_tokens_details.cached_tokens), Responses API (input_tokens_details), Anthropic (cache_read_input_tokens),
        ''' DeepSeek (prompt_cache_hit_tokens). 0 when the provider does not say.
        ''' </summary>
        Public Shared Function CachedTokensOf(usage As System.Text.Json.Nodes.JsonNode) As Long
            If usage Is Nothing Then Return 0
            For Each v In {usage("prompt_tokens_details")?("cached_tokens"), usage("input_tokens_details")?("cached_tokens"),
                           usage("cache_read_input_tokens"), usage("prompt_cache_hit_tokens")}
                Dim n As Long
                If v IsNot Nothing AndAlso Long.TryParse(v.ToString(), n) AndAlso n > 0 Then Return n
            Next
            Return 0
        End Function

        ''' <summary>Forgets the cached surcharge setting of an account (after an administrator changed it).</summary>
        Public Sub ForgetSurcharge(username As String)
            If username IsNot Nothing Then _surcharge.TryRemove(username, Nothing)
        End Sub

        ''' <summary>Charges a Chat or API call at its Model Square price; returns what it cost the account (0 = free or not charged).</summary>
        Public Async Function ChargeUsageAsync(username As String, isAdmin As Boolean, entryType As String, model As String, routedModel As String,
                                               promptTokens As Long, completionTokens As Long, description As String, Optional cachedTokens As Long = 0) As Task(Of Long)
            If isAdmin Then Return 0
            Dim cost = CostMicros(PriceForUse(model, routedModel), promptTokens, completionTokens, cachedTokens)
            If cost <= 0 Then Return 0
            If cachedTokens > 0 Then description = $"{description} ({Math.Min(cachedTokens, promptTokens):N0} of {promptTokens:N0} input tokens cached)"
            Dim after = Await ChargeAsync(username, cost, entryType, If(routedModel, model), promptTokens, completionTokens, Nothing, description).ConfigureAwait(False)
            Return If(after.HasValue, Await WithSurchargeAsync(username, cost).ConfigureAwait(False), 0L)
        End Function

        Private Async Function ApplyAsync(username As String, deltaMicros As Long, entryType As String, model As String, promptTokens As Long?, completionTokens As Long?,
                                          agentId As String, description As String, actor As String, usersOnly As Boolean) As Task(Of Long?)
            Dim now = Db.Ts(DateTime.UtcNow)
            Dim result = Await _db.InTransactionAsync(Of (Balance As Long, IsAdmin As Boolean)?)(
                Async Function(conn, tx)
                    Dim n = Await Database.TxExecAsync(conn, tx, "UPDATE AdminUsers SET BalanceMicros = BalanceMicros + @x WHERE Username=@u" & If(usersOnly, " AND Role='User'", ""),
                                                       ("@x", CObj(deltaMicros)), ("@u", username)).ConfigureAwait(False)
                    If n = 0 Then Return Nothing
                    Dim bal = Convert.ToInt64(If(Await Database.TxScalarAsync(conn, tx, "SELECT BalanceMicros FROM AdminUsers WHERE Username=@u", ("@u", CObj(username))).ConfigureAwait(False), 0L))
                    Dim role = Convert.ToString(Await Database.TxScalarAsync(conn, tx, "SELECT Role FROM AdminUsers WHERE Username=@u", ("@u", CObj(username))).ConfigureAwait(False))
                    Await Database.TxExecAsync(conn, tx,
                        "INSERT INTO AccountLedger(Username, EntryType, AmountMicros, BalanceAfterMicros, Model, PromptTokens, CompletionTokens, AgentId, Description, CreatedBy, CreatedUtc) " &
                        "VALUES (@u, @t, @x, @b, @m, @p, @c, @a, @d, @by, @n)",
                        ("@u", CObj(username)), ("@t", entryType), ("@x", deltaMicros), ("@b", bal), ("@m", model),
                        ("@p", If(promptTokens.HasValue, CObj(promptTokens.Value), Nothing)), ("@c", If(completionTokens.HasValue, CObj(completionTokens.Value), Nothing)),
                        ("@a", agentId), ("@d", TextUtil.Truncate(description, 500)), ("@by", actor), ("@n", now)).ConfigureAwait(False)
                    Return CType((bal, String.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase)), (Balance As Long, IsAdmin As Boolean)?)
                End Function).ConfigureAwait(False)
            If Not result.HasValue Then Return Nothing
            Raise(username, result.Value.Balance, Not result.Value.IsAdmin)
            Return result.Value.Balance
        End Function

        ''' <summary>Re-announces an account's balance (after its role changed, or an agent got a new owner).</summary>
        Public Async Function AnnounceAsync(username As String) As Task
            Dim acc = Await GetAccountAsync(username).ConfigureAwait(False)
            If acc IsNot Nothing Then Raise(acc.Username, acc.BalanceMicros, acc.Charged)
        End Function

        ' ------------------------------------------------------------------ history

        Public Function LedgerAsync(username As String, limit As Integer) As Task(Of List(Of AccountEntry))
            Return _db.QueryAsync("SELECT * FROM AccountLedger WHERE Username=@u ORDER BY Id DESC LIMIT @l", AddressOf MapEntry,
                                  ("@u", CObj(username)), ("@l", Math.Clamp(limit, 1, 1000)))
        End Function

        Private Shared Function MapEntry(r As SqliteDataReader) As AccountEntry
            Return New AccountEntry With {
                .Id = Db.Lng(r, "Id"), .Username = Db.Str(r, "Username"), .EntryType = Db.Str(r, "EntryType"), .AmountMicros = Db.Lng(r, "AmountMicros"),
                .BalanceAfterMicros = Db.Lng(r, "BalanceAfterMicros"), .Model = Db.Str(r, "Model"), .PromptTokens = Db.LngN(r, "PromptTokens"),
                .CompletionTokens = Db.LngN(r, "CompletionTokens"), .AgentId = Db.Str(r, "AgentId"), .Description = Db.Str(r, "Description"),
                .CreatedBy = Db.Str(r, "CreatedBy"), .CreatedUtc = Db.Dt(r, "CreatedUtc")}
        End Function

        ''' <summary>What accounts spent since a time (charges only, not credits), by account.</summary>
        Public Async Function SpentSinceAsync(sinceUtc As DateTime) As Task(Of Dictionary(Of String, Long))
            Dim rows = Await _db.QueryAsync("SELECT Username, COALESCE(SUM(-AmountMicros),0) AS Spent FROM AccountLedger WHERE EntryType<>'Credit' AND CreatedUtc>=@s GROUP BY Username",
                                            Function(r) (Db.Str(r, "Username"), Db.Lng(r, "Spent")), ("@s", CObj(Db.Ts(sinceUtc)))).ConfigureAwait(False)
            Return rows.ToDictionary(Function(x) x.Item1, Function(x) x.Item2, StringComparer.OrdinalIgnoreCase)
        End Function

        ''' <summary>An account's spend per model since a time: {model, calls, micros, promptTokens, completionTokens}.</summary>
        Public Function SpendByModelAsync(username As String, sinceUtc As DateTime) As Task(Of List(Of Dictionary(Of String, Object)))
            Return _db.QueryAsync(
                "SELECT COALESCE(Model,'?') AS Model, COUNT(*) AS Calls, SUM(-AmountMicros) AS Micros, COALESCE(SUM(PromptTokens),0) AS PromptTokens, COALESCE(SUM(CompletionTokens),0) AS CompletionTokens " &
                "FROM AccountLedger WHERE Username=@u AND EntryType<>'Credit' AND CreatedUtc>=@s GROUP BY COALESCE(Model,'?') ORDER BY Micros DESC",
                Function(r) New Dictionary(Of String, Object) From {
                    {"model", Db.Str(r, "Model")}, {"calls", Db.Lng(r, "Calls")}, {"micros", Db.Lng(r, "Micros")},
                    {"promptTokens", Db.Lng(r, "PromptTokens")}, {"completionTokens", Db.Lng(r, "CompletionTokens")}},
                ("@u", CObj(username)), ("@s", Db.Ts(sinceUtc)))
        End Function

        ' ------------------------------------------------------------------ an account's statistics (Balance tab)

        ''' <summary>
        ''' An account's money per UTC day and entry type since a time: {day, type, micros, calls, promptTokens, completionTokens}.
        ''' Charges are positive amounts spent; for "Credit" rows micros is the net money added that day.
        ''' </summary>
        Public Function DailyActivityAsync(username As String, sinceUtc As DateTime) As Task(Of List(Of Dictionary(Of String, Object)))
            ' CreatedUtc is "yyyy-MM-ddTHH:mm:ss.fffZ"; the first 10 characters are the UTC day.
            Return _db.QueryAsync(
                "SELECT substr(CreatedUtc,1,10) AS Day, EntryType, COUNT(*) AS Calls, " &
                "SUM(CASE WHEN EntryType='Credit' THEN AmountMicros ELSE -AmountMicros END) AS Micros, " &
                "COALESCE(SUM(PromptTokens),0) AS P, COALESCE(SUM(CompletionTokens),0) AS C " &
                "FROM AccountLedger WHERE Username=@u AND CreatedUtc>=@s GROUP BY Day, EntryType ORDER BY Day",
                Function(r) New Dictionary(Of String, Object) From {
                    {"day", Db.Str(r, "Day")}, {"type", Db.Str(r, "EntryType")}, {"calls", Db.Lng(r, "Calls")}, {"micros", Db.Lng(r, "Micros")},
                    {"promptTokens", Db.Lng(r, "P")}, {"completionTokens", Db.Lng(r, "C")}},
                ("@u", CObj(username)), ("@s", Db.Ts(sinceUtc)))
        End Function

        ''' <summary>The account's balance at the end of each UTC day that had movements, since a time: {day, balanceMicros}.</summary>
        Public Function ClosingBalancesAsync(username As String, sinceUtc As DateTime) As Task(Of List(Of Dictionary(Of String, Object)))
            Return _db.QueryAsync(
                "SELECT d.Day, (SELECT BalanceAfterMicros FROM AccountLedger WHERE Id=d.LastId) AS Bal FROM " &
                "(SELECT substr(CreatedUtc,1,10) AS Day, MAX(Id) AS LastId FROM AccountLedger WHERE Username=@u AND CreatedUtc>=@s GROUP BY Day) d ORDER BY d.Day",
                Function(r) New Dictionary(Of String, Object) From {{"day", Db.Str(r, "Day")}, {"balanceMicros", Db.Lng(r, "Bal")}},
                ("@u", CObj(username)), ("@s", Db.Ts(sinceUtc)))
        End Function

        ''' <summary>The account's balance just before a time (0 when it had no movements before it).</summary>
        Public Async Function BalanceBeforeAsync(username As String, beforeUtc As DateTime) As Task(Of Long)
            Dim v = Await _db.ScalarAsync("SELECT BalanceAfterMicros FROM AccountLedger WHERE Username=@u AND CreatedUtc<@s ORDER BY Id DESC LIMIT 1",
                                          ("@u", CObj(username)), ("@s", Db.Ts(beforeUtc))).ConfigureAwait(False)
            Return If(v Is Nothing OrElse v Is DBNull.Value, 0L, Convert.ToInt64(v))
        End Function

        ''' <summary>All-time totals of an account: money added, money spent, number of charges, first movement.</summary>
        Public Async Function LifetimeAsync(username As String) As Task(Of (AddedMicros As Long, SpentMicros As Long, Charges As Long, FirstUtc As DateTime?))
            Dim rows = Await _db.QueryAsync(
                "SELECT COALESCE(SUM(CASE WHEN EntryType='Credit' AND AmountMicros>0 THEN AmountMicros ELSE 0 END),0) AS Added, " &
                "COALESCE(SUM(CASE WHEN EntryType<>'Credit' THEN -AmountMicros ELSE 0 END),0) AS Spent, " &
                "COALESCE(SUM(CASE WHEN EntryType<>'Credit' THEN 1 ELSE 0 END),0) AS Charges, MIN(CreatedUtc) AS FirstUtc FROM AccountLedger WHERE Username=@u",
                Function(r) (Db.Lng(r, "Added"), Db.Lng(r, "Spent"), Db.Lng(r, "Charges"), Db.DtN(r, "FirstUtc")), ("@u", CObj(username))).ConfigureAwait(False)
            Return If(rows.Count = 0, (0L, 0L, 0L, CType(Nothing, DateTime?)), rows(0))
        End Function

        ' ------------------------------------------------------------------ chat-tab spend (Finance graphs)

        ''' <summary>
        ''' What User accounts pay through the Chat tab: messages (Chat), images and videos. Each charge was priced at the
        ''' Model Square price in force when it happened, so these are the real balance usage per model.
        ''' </summary>
        Public Shared ReadOnly ChatEntryTypes As String() = {EntryChat, "Image", "Video", EntrySpeech, EntryMusic}

        ''' <summary>Adds parameters for the entry-type list and returns the "IN (@et0, @et1, …)" clause.</summary>
        Private Shared Function EntryTypeIn(types As String(), ps As List(Of (String, Object))) As String
            Dim names As New List(Of String)
            For i = 0 To types.Length - 1
                Dim name = "@et" & i
                names.Add(name)
                ps.Add((name, CObj(types(i))))
            Next
            Return "(" & String.Join(", ", names) & ")"
        End Function

        ''' <summary>Chat-tab spend across all accounts, by model, since a time: {model, calls, micros, promptTokens, completionTokens}.</summary>
        Public Function ChatSpendByModelAsync(sinceUtc As DateTime) As Task(Of List(Of Dictionary(Of String, Object)))
            Dim ps As New List(Of (String, Object)) From {("@s", CObj(Db.Ts(sinceUtc)))}
            Dim inClause = EntryTypeIn(ChatEntryTypes, ps)
            Return _db.QueryAsync(
                "SELECT COALESCE(Model,'?') AS Model, COUNT(*) AS Calls, SUM(-AmountMicros) AS Micros, COALESCE(SUM(PromptTokens),0) AS P, COALESCE(SUM(CompletionTokens),0) AS C " &
                "FROM AccountLedger WHERE EntryType IN " & inClause & " AND CreatedUtc>=@s GROUP BY COALESCE(Model,'?') ORDER BY Micros DESC",
                Function(r) New Dictionary(Of String, Object) From {
                    {"model", Db.Str(r, "Model")}, {"calls", Db.Lng(r, "Calls")}, {"micros", Db.Lng(r, "Micros")},
                    {"promptTokens", Db.Lng(r, "P")}, {"completionTokens", Db.Lng(r, "C")}},
                ps.ToArray())
        End Function

        ''' <summary>Chat-tab spend across all accounts, by account, since a time: {username, calls, micros}.</summary>
        Public Function ChatSpendByUserAsync(sinceUtc As DateTime) As Task(Of List(Of Dictionary(Of String, Object)))
            Dim ps As New List(Of (String, Object)) From {("@s", CObj(Db.Ts(sinceUtc)))}
            Dim inClause = EntryTypeIn(ChatEntryTypes, ps)
            Return _db.QueryAsync(
                "SELECT Username, COUNT(*) AS Calls, SUM(-AmountMicros) AS Micros FROM AccountLedger " &
                "WHERE EntryType IN " & inClause & " AND CreatedUtc>=@s GROUP BY Username ORDER BY Micros DESC",
                Function(r) New Dictionary(Of String, Object) From {
                    {"username", Db.Str(r, "Username")}, {"calls", Db.Lng(r, "Calls")}, {"micros", Db.Lng(r, "Micros")}},
                ps.ToArray())
        End Function

        ''' <summary>Chat-tab spend across all accounts, by day and model (for stacking), since a time: {day, model, micros}.</summary>
        Public Function ChatSpendDailyAsync(sinceUtc As DateTime) As Task(Of List(Of Dictionary(Of String, Object)))
            Dim ps As New List(Of (String, Object)) From {("@s", CObj(Db.Ts(sinceUtc)))}
            Dim inClause = EntryTypeIn(ChatEntryTypes, ps)
            ' CreatedUtc is "yyyy-MM-ddTHH:mm:ss.fffZ"; the first 10 characters are the UTC day.
            Return _db.QueryAsync(
                "SELECT substr(CreatedUtc,1,10) AS Day, COALESCE(Model,'?') AS Model, SUM(-AmountMicros) AS Micros FROM AccountLedger " &
                "WHERE EntryType IN " & inClause & " AND CreatedUtc>=@s GROUP BY Day, COALESCE(Model,'?') ORDER BY Day",
                Function(r) New Dictionary(Of String, Object) From {
                    {"day", Db.Str(r, "Day")}, {"model", Db.Str(r, "Model")}, {"micros", Db.Lng(r, "Micros")}},
                ps.ToArray())
        End Function
    End Class

End Namespace
