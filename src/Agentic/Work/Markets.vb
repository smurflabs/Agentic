Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.Globalization
Imports System.Net.Http
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Agentic.Configuration
Imports Agentic.Logging
Imports Agentic.Persistence
Imports Agentic.Utilities

Namespace Work

    ''' <summary>One watched asset as it is now: its latest price and how it moved.</summary>
    Public NotInheritable Class MarketQuote
        ''' <summary>crypto | fx</summary>
        Public Property Kind As String
        Public Property Symbol As String
        Public Property Name As String
        ''' <summary>Crypto: USD per coin. Exchange rates: units of the currency for one of the base (USD).</summary>
        Public Property Price As Double?
        Public Property Utc As DateTime?
        ''' <summary>Changes in percent against the price that long ago (Nothing while there is no price that old).</summary>
        Public Property Change1h As Double?
        Public Property Change24h As Double?
        Public Property Change7d As Double?
        Public Property Change30d As Double?
    End Class

    ''' <summary>The coins and currencies one account watches.</summary>
    Public NotInheritable Class MarketWatch
        Public Property Crypto As New List(Of String)
        Public Property Fx As New List(Of String)
        ''' <summary>The account's own lists (else the defaults).</summary>
        Public Property Custom As Boolean
    End Class

    ''' <summary>
    ''' The Money tab's data: crypto prices from CoinMarketCap's public simple-price API about every minute, and exchange rates
    ''' from Frankfurter (the ECB's daily reference rates) every hour, each kept in MarketQuotes for the charts. Administrators
    ''' choose the defaults every account watches; each account may keep its own coins and currencies instead. Agents and chats
    ''' ask through <see cref="ToolAnswerAsync"/> (the market_prices tool).
    ''' </summary>
    Public NotInheritable Class MarketsService
        Public Const Crypto As String = "crypto"
        Public Const Fx As String = "fx"
        Private Const WatchlistKey As String = "MarketsWatchlist"
        ''' <summary>An account's own coins and currencies (UserPreferences, JSON {crypto, fx}); without them it watches the defaults.</summary>
        Public Const PreferenceKey As String = "markets.watchlist"
        ''' <summary>An account's own coins and currencies are read every round for this long after it last looked.</summary>
        Private Shared ReadOnly ActiveFor As TimeSpan = TimeSpan.FromMinutes(30)
        Private Shared ReadOnly CryptoSymbol As New Regex("^[A-Z0-9]{1,15}$")
        Private Shared ReadOnly CurrencyCode As New Regex("^[A-Z]{3}$")

        Private ReadOnly _db As Database
        Private ReadOnly _opt As MarketsOptions
        Private ReadOnly _log As StructuredLogger
        Private ReadOnly _http As HttpClient
        Private ReadOnly _wake As New SemaphoreSlim(0)
        Private _crypto As List(Of String)
        Private _fx As List(Of String)
        Private ReadOnly _backfilled As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        ''' <summary>Each account's own lists as read from UserPreferences (Nothing: the defaults), and when it last looked.</summary>
        Private ReadOnly _lists As New ConcurrentDictionary(Of String, MarketWatch)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _seen As New ConcurrentDictionary(Of String, DateTime)(StringComparer.OrdinalIgnoreCase)
        Private _lastRefresh As DateTime = DateTime.MinValue

        Public Property CryptoUpdatedUtc As DateTime?
        Public Property FxUpdatedUtc As DateTime?
        Public Property CryptoError As String
        Public Property FxError As String

        Public Sub New(db As Database, options As MarketsOptions, log As StructuredLogger, Optional handler As HttpMessageHandler = Nothing)
            _db = db
            _opt = If(options, New MarketsOptions())
            _log = log
            _http = If(handler Is Nothing, New HttpClient(), New HttpClient(handler))
            _http.Timeout = TimeSpan.FromSeconds(Math.Max(5, _opt.TimeoutSeconds))
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Agentic/2 (+markets)")
            _crypto = Clean(If(_opt.CryptoSymbols.Count > 0, _opt.CryptoSymbols, MarketsOptions.DefaultCrypto.ToList()), CryptoSymbol)
            _fx = Clean(If(_opt.FxQuotes.Count > 0, _opt.FxQuotes, MarketsOptions.DefaultFx.ToList()), CurrencyCode)
        End Sub

        Public ReadOnly Property Options As MarketsOptions
            Get
                Return _opt
            End Get
        End Property

        Public ReadOnly Property Enabled As Boolean
            Get
                Return _opt.Enabled
            End Get
        End Property

        Public ReadOnly Property FxBase As String
            Get
                Return If(String.IsNullOrWhiteSpace(_opt.FxBase), "USD", _opt.FxBase.Trim().ToUpperInvariant())
            End Get
        End Property

        Public ReadOnly Property CryptoWatch As List(Of String)
            Get
                Return _crypto.ToList()
            End Get
        End Property

        Public ReadOnly Property FxWatch As List(Of String)
            Get
                Return _fx.ToList()
            End Get
        End Property

        Private Shared Function Clean(list As IEnumerable(Of String), pattern As Regex) As List(Of String)
            Return If(list, Enumerable.Empty(Of String)()).Select(Function(s) If(s, "").Trim().ToUpperInvariant()).
                Where(Function(s) pattern.IsMatch(s)).Distinct(StringComparer.OrdinalIgnoreCase).Take(40).ToList()
        End Function

        ' ------------------------------------------------------------------ the watch lists

        ''' <summary>The watch lists an administrator saved (they replace the configuration's).</summary>
        Public Async Function LoadWatchlistAsync() As Task
            Dim json = TryCast(Await _db.ScalarAsync("SELECT Value FROM HostState WHERE Key=@k", ("@k", CObj(WatchlistKey))).ConfigureAwait(False), String)
            If String.IsNullOrWhiteSpace(json) Then Return
            Try
                Dim node = JsonNode.Parse(json)
                Dim c = Clean(If(TryCast(node?("crypto"), JsonArray), New JsonArray()).Select(Function(x) x?.ToString()), CryptoSymbol)
                Dim f = Clean(If(TryCast(node?("fx"), JsonArray), New JsonArray()).Select(Function(x) x?.ToString()), CurrencyCode)
                If c.Count > 0 Then _crypto = c
                If f.Count > 0 Then _fx = f
            Catch ex As JsonException
            End Try
        End Function

        ''' <summary>Saves new watch lists (each symbol checked) and reads prices for them at once. Returns the lists kept.</summary>
        Public Async Function SetWatchlistAsync(cryptoSymbols As IEnumerable(Of String), fxCodes As IEnumerable(Of String)) As Task(Of (Crypto As List(Of String), Fx As List(Of String)))
            Dim c = Clean(cryptoSymbols, CryptoSymbol)
            Dim f = Clean(fxCodes, CurrencyCode).Where(Function(x) x <> FxBase).ToList()
            If c.Count = 0 Then Throw New ArgumentException("Watch at least one coin (symbols such as BTC, ETH).")
            If f.Count = 0 Then Throw New ArgumentException("Watch at least one currency (ISO codes such as EUR, GBP).")
            _crypto = c
            _fx = f
            Await _db.ExecuteAsync("INSERT INTO HostState(Key, Value) VALUES (@k, @v) ON CONFLICT(Key) DO UPDATE SET Value=@v",
                                   ("@k", CObj(WatchlistKey)), ("@v", JsonUtil.ToJson(New With {.crypto = c, .fx = f}))).ConfigureAwait(False)
            _wake.Release()
            Return (c, f)
        End Function

        ''' <summary>An account's coins and currencies: its own (Custom) or the defaults. No account (agents): the defaults.</summary>
        Public Async Function WatchForAsync(username As String) As Task(Of MarketWatch)
            Dim defaults = New MarketWatch With {.Crypto = _crypto.ToList(), .Fx = _fx.ToList()}
            If String.IsNullOrWhiteSpace(username) Then Return defaults
            Dim own As MarketWatch = Nothing
            If Not _lists.TryGetValue(username, own) Then
                Dim json = TryCast(Await _db.ScalarAsync("SELECT Value FROM UserPreferences WHERE Username=@u AND Key=@k",
                                                         ("@u", CObj(username)), ("@k", PreferenceKey)).ConfigureAwait(False), String)
                Try
                    Dim node = If(String.IsNullOrWhiteSpace(json), Nothing, JsonNode.Parse(json))
                    If node IsNot Nothing Then
                        own = New MarketWatch With {
                            .Crypto = Clean(If(TryCast(node("crypto"), JsonArray), New JsonArray()).Select(Function(x) x?.ToString()), CryptoSymbol),
                            .Fx = Clean(If(TryCast(node("fx"), JsonArray), New JsonArray()).Select(Function(x) x?.ToString()), CurrencyCode).Where(Function(x) x <> FxBase).ToList(),
                            .Custom = True}
                        If own.Crypto.Count = 0 AndAlso own.Fx.Count = 0 Then own = Nothing
                    End If
                Catch ex As JsonException
                    own = Nothing
                End Try
                _lists(username) = own
            End If
            Return If(own Is Nothing, defaults, New MarketWatch With {.Crypto = own.Crypto.ToList(), .Fx = own.Fx.ToList(), .Custom = True})
        End Function

        ''' <summary>
        ''' Saves an account's own coins and currencies. A coin or currency nobody watches yet must have a price now (CoinMarketCap
        ''' or Coinbase; Frankfurter): unknown ones are named in the error and nothing is saved. The defaults again are kept as
        ''' "the defaults", so the account follows them when an administrator changes them. Returns the lists kept.
        ''' </summary>
        Public Async Function SetAccountWatchlistAsync(username As String, cryptoSymbols As IEnumerable(Of String), fxCodes As IEnumerable(Of String), ct As CancellationToken) As Task(Of MarketWatch)
            If String.IsNullOrWhiteSpace(username) Then Throw New ArgumentException("Watch lists belong to an account.")
            Dim badCoins = If(cryptoSymbols, Enumerable.Empty(Of String)()).Select(Function(s) If(s, "").Trim().ToUpperInvariant()).Where(Function(s) s.Length > 0 AndAlso Not CryptoSymbol.IsMatch(s)).ToList()
            Dim badCodes = If(fxCodes, Enumerable.Empty(Of String)()).Select(Function(s) If(s, "").Trim().ToUpperInvariant()).Where(Function(s) s.Length > 0 AndAlso Not CurrencyCode.IsMatch(s)).ToList()
            If badCoins.Count > 0 Then Throw New ArgumentException("Not a coin symbol: " & String.Join(", ", badCoins) & " (use symbols such as BTC, ETH).")
            If badCodes.Count > 0 Then Throw New ArgumentException("Not a currency code: " & String.Join(", ", badCodes) & " (use ISO codes such as EUR, GBP).")
            Dim c = Clean(cryptoSymbols, CryptoSymbol)
            Dim f = Clean(fxCodes, CurrencyCode).Where(Function(x) x <> FxBase).ToList()
            If c.Count = 0 AndAlso f.Count = 0 Then Throw New ArgumentException("Watch at least one coin or currency.")
            Dim polledCoins = New HashSet(Of String)(PolledCrypto(), StringComparer.OrdinalIgnoreCase)
            Dim polledCodes = New HashSet(Of String)(PolledFx(), StringComparer.OrdinalIgnoreCase)
            Dim unknown As New List(Of String)
            Dim newCoins = c.Where(Function(s) Not polledCoins.Contains(s)).ToList()
            If newCoins.Count > 0 Then
                Dim r = Await ReadCryptoAsync(newCoins, ct, probe:=True).ConfigureAwait(False)
                If r.Found Is Nothing Then Throw New InvalidOperationException("Could not check the coins now: " & r.Error)
                unknown.AddRange(newCoins.Where(Function(s) Not r.Found.Contains(s, StringComparer.OrdinalIgnoreCase)))
            End If
            Dim newCodes = f.Where(Function(s) Not polledCodes.Contains(s)).ToList()
            If newCodes.Count > 0 Then unknown.AddRange(Await UnknownCurrenciesAsync(newCodes, ct).ConfigureAwait(False))
            If unknown.Count > 0 Then Throw New ArgumentException("No prices for " & String.Join(", ", unknown) & ".")
            _seen(username) = DateTime.UtcNow
            If c.SequenceEqual(_crypto, StringComparer.OrdinalIgnoreCase) AndAlso f.SequenceEqual(_fx, StringComparer.OrdinalIgnoreCase) Then
                Await ResetAccountWatchlistAsync(username).ConfigureAwait(False)
                Return New MarketWatch With {.Crypto = c, .Fx = f}
            End If
            Await _db.ExecuteAsync("INSERT INTO UserPreferences(Username, Key, Value, UpdatedUtc) VALUES (@u, @k, @v, @n) " &
                                   "ON CONFLICT(Username, Key) DO UPDATE SET Value=excluded.Value, UpdatedUtc=excluded.UpdatedUtc",
                                   ("@u", CObj(username)), ("@k", PreferenceKey), ("@v", JsonUtil.ToJson(New With {.crypto = c, .fx = f})), ("@n", Db.Ts(DateTime.UtcNow))).ConfigureAwait(False)
            _lists(username) = New MarketWatch With {.Crypto = c, .Fx = f, .Custom = True}
            ' A new currency's history comes with the next round.
            If newCodes.Count > 0 Then _wake.Release()
            Return New MarketWatch With {.Crypto = c.ToList(), .Fx = f.ToList(), .Custom = True}
        End Function

        ''' <summary>The account watches the defaults again.</summary>
        Public Async Function ResetAccountWatchlistAsync(username As String) As Task
            If String.IsNullOrWhiteSpace(username) Then Return
            Await _db.ExecuteAsync("DELETE FROM UserPreferences WHERE Username=@u AND Key=@k", ("@u", CObj(username)), ("@k", PreferenceKey)).ConfigureAwait(False)
            _lists(username) = Nothing
        End Function

        ''' <summary>The account is looking: its own coins and currencies are read every round for the next 30 minutes.</summary>
        Public Sub MarkSeen(username As String)
            If Not String.IsNullOrWhiteSpace(username) Then _seen(username) = DateTime.UtcNow
        End Sub

        ''' <summary>What the server reads every round: the defaults, and the own lists of accounts that looked in the last 30 minutes.</summary>
        Friend Function PolledCrypto() As List(Of String)
            Return Polled(Function(w) w.Crypto, _crypto)
        End Function

        Friend Function PolledFx() As List(Of String)
            Return Polled(Function(w) w.Fx, _fx)
        End Function

        Private Function Polled(pick As Func(Of MarketWatch, List(Of String)), defaults As List(Of String)) As List(Of String)
            Dim all As New List(Of String)(defaults)
            For Each kv In _seen
                If DateTime.UtcNow - kv.Value > ActiveFor Then
                    _seen.TryRemove(kv.Key, Nothing)
                    Continue For
                End If
                Dim own As MarketWatch = Nothing
                If _lists.TryGetValue(kv.Key, own) AndAlso own IsNot Nothing Then all.AddRange(pick(own))
            Next
            Return all.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        End Function

        ''' <summary>The currencies Frankfurter has no rate for (it refuses a whole request naming one unknown code, so each is then asked alone).</summary>
        Private Async Function UnknownCurrenciesAsync(codes As List(Of String), ct As CancellationToken) As Task(Of List(Of String))
            Dim ask = Async Function(list As List(Of String)) As Task(Of HashSet(Of String))
                          Using resp = Await _http.GetAsync($"{_opt.FxUrl}?base={FxBase}&quotes={String.Join(",", list)}", ct).ConfigureAwait(False)
                              If CInt(resp.StatusCode) = 404 OrElse CInt(resp.StatusCode) = 422 Then Return New HashSet(Of String)
                              resp.EnsureSuccessStatusCode()
                              Dim got As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
                              For Each r In ParseFx(Await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(False))
                                  Await AddAsync(Fx, r.Quote, r.Rate, r.Day).ConfigureAwait(False)
                                  got.Add(r.Quote)
                              Next
                              Return got
                          End Using
                      End Function
            Try
                Dim found = Await ask(codes).ConfigureAwait(False)
                If found.Count = 0 AndAlso codes.Count > 1 Then
                    For Each code In codes
                        If (Await ask(New List(Of String) From {code}).ConfigureAwait(False)).Count > 0 Then found.Add(code)
                    Next
                End If
                Return codes.Where(Function(x) Not found.Contains(x)).ToList()
            Catch ex As Exception When Not ExceptionUtil.IsCancellation(ex) OrElse Not ct.IsCancellationRequested
                Throw New InvalidOperationException("Could not check the currencies now: Frankfurter: " & TextUtil.Truncate(ex.Message, 160))
            End Try
        End Function

        ' ------------------------------------------------------------------ fetching

        Public Sub Start(ct As CancellationToken)
            If Not _opt.Enabled Then Return
            Dim unused = Task.Run(Function() LoopAsync(ct))
        End Sub

        Private Async Function LoopAsync(ct As CancellationToken) As Task
            Try
                Await LoadWatchlistAsync().ConfigureAwait(False)
            Catch ex As Exception When Not ExceptionUtil.IsCancellation(ex)
            End Try
            Dim nextFx = DateTime.MinValue, nextThin = DateTime.UtcNow.AddMinutes(5)
            While Not ct.IsCancellationRequested
                Try
                    Await FetchCryptoAsync(ct).ConfigureAwait(False)
                    If DateTime.UtcNow >= nextFx OrElse PolledFx().Any(Function(s) Not _backfilled.Contains(s)) Then
                        Await FetchFxAsync(ct).ConfigureAwait(False)
                        nextFx = DateTime.UtcNow.AddMinutes(Math.Max(5, _opt.FxPollMinutes))
                    End If
                    If DateTime.UtcNow >= nextThin Then
                        Await ThinAsync(DateTime.UtcNow).ConfigureAwait(False)
                        nextThin = DateTime.UtcNow.AddHours(6)
                    End If
                Catch ex As Exception When Not ExceptionUtil.IsCancellation(ex)
                    _log?.Warn("Markets", "Reading prices failed: " & ex.Message)
                End Try
                Try
                    Await _wake.WaitAsync(TimeSpan.FromSeconds(Math.Max(15, _opt.CryptoPollSeconds)), ct).ConfigureAwait(False)
                Catch ex As OperationCanceledException
                    Exit While
                End Try
            End While
        End Function

        ''' <summary>Reads prices now (at most every 15 s, whoever asks).</summary>
        Public Async Function RefreshAsync(ct As CancellationToken) As Task
            If DateTime.UtcNow - _lastRefresh < TimeSpan.FromSeconds(15) Then Return
            _lastRefresh = DateTime.UtcNow
            Await FetchCryptoAsync(ct).ConfigureAwait(False)
            Await FetchFxAsync(ct).ConfigureAwait(False)
        End Function

        ''' <summary>Where crypto prices came from last: CoinMarketCap, or the fallback while CoinMarketCap refuses.</summary>
        Public Property CryptoSource As String
        Private _cmcPausedUntil As DateTime = DateTime.MinValue
        Private _cmcPause As TimeSpan = TimeSpan.Zero

        ''' <summary>Reads and keeps the price of every coin watched (the defaults and the accounts looking now).</summary>
        Friend Async Function FetchCryptoAsync(ct As CancellationToken) As Task
            Dim symbols = PolledCrypto()
            If symbols.Count = 0 Then Return
            Dim r = Await ReadCryptoAsync(symbols, ct).ConfigureAwait(False)
            If r.Found IsNot Nothing Then
                CryptoUpdatedUtc = DateTime.UtcNow
                CryptoSource = r.Source
            End If
            CryptoError = r.Error
        End Function

        ''' <summary>
        ''' Crypto prices for these coins, kept: from CoinMarketCap; while it refuses (its anonymous limit, an error) from the
        ''' fallback (Coinbase), and CoinMarketCap is asked again after a pause that doubles from 30 minutes up to 6 hours. A probe
        ''' (checking coins an account wants) does not pause CoinMarketCap: one unknown symbol can make it refuse the request.
        ''' Returns the coins that got a price (Nothing: no source answered), where from, and any problem.
        ''' </summary>
        Private Async Function ReadCryptoAsync(symbols As List(Of String), ct As CancellationToken, Optional probe As Boolean = False) As Task(Of (Found As List(Of String), Source As String, [Error] As String))
            Dim cmcProblem As String = Nothing
            If DateTime.UtcNow >= _cmcPausedUntil Then
                Try
                    Dim parsed = Await ReadCoinMarketCapAsync(symbols, ct).ConfigureAwait(False)
                    If parsed.Error IsNot Nothing Then
                        cmcProblem = parsed.Error
                    ElseIf parsed.Quotes.Count = 0 Then
                        cmcProblem = "no prices for " & String.Join(", ", symbols)
                    Else
                        Dim at = If(parsed.At, DateTime.UtcNow)
                        For Each q In parsed.Quotes
                            Await AddAsync(Crypto, q.Symbol, q.Price, at).ConfigureAwait(False)
                            Await NameAsync(Crypto, q.Symbol, q.Name, q.Slug).ConfigureAwait(False)
                        Next
                        _cmcPause = TimeSpan.Zero
                        Dim got = parsed.Quotes.Select(Function(q) q.Symbol).ToList()
                        Dim missing = symbols.Where(Function(s) Not got.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList()
                        Return (got, "CoinMarketCap", If(missing.Count > 0, "CoinMarketCap has no price for " & String.Join(", ", missing) & ".", Nothing))
                    End If
                Catch ex As Exception When Not ExceptionUtil.IsCancellation(ex) OrElse Not ct.IsCancellationRequested
                    cmcProblem = TextUtil.Truncate(ex.Message, 200)
                End Try
                If Not probe Then
                    _cmcPause = If(_cmcPause = TimeSpan.Zero, TimeSpan.FromMinutes(30), TimeSpan.FromTicks(Math.Min(TimeSpan.FromHours(6).Ticks, _cmcPause.Ticks * 2)))
                    _cmcPausedUntil = DateTime.UtcNow + _cmcPause
                    _log?.Info("Markets", $"CoinMarketCap refused ({cmcProblem}); prices from the fallback until {_cmcPausedUntil:HH:mm} UTC.")
                End If
            End If
            If String.IsNullOrWhiteSpace(_opt.CryptoFallbackUrl) Then Return (Nothing, Nothing, "CoinMarketCap: " & If(cmcProblem, "paused after refusing") & ".")
            Try
                Dim rates = ParseCoinbase(Await _http.GetStringAsync(_opt.CryptoFallbackUrl, ct).ConfigureAwait(False))
                Dim now = DateTime.UtcNow
                Dim found As New List(Of String)
                For Each s In symbols
                    Dim rate As Double
                    If Not rates.TryGetValue(s, rate) OrElse rate <= 0 Then Continue For
                    Await AddAsync(Crypto, s, 1 / rate, now).ConfigureAwait(False)
                    Await _db.ExecuteAsync("INSERT OR IGNORE INTO MarketAssets(Kind, Symbol, Name) VALUES (@k, @s, @n)",
                                           ("@k", CObj(Crypto)), ("@s", s), ("@n", CoinName(s))).ConfigureAwait(False)
                    found.Add(s)
                Next
                Dim source = "Coinbase (CoinMarketCap " & If(_opt.CryptoApiKey Is Nothing OrElse _opt.CryptoApiKey.Length = 0, "rate-limited without an API key", "refused") & $"; asked again after {_cmcPausedUntil:HH:mm} UTC)"
                Return (found, source, If(found.Count = 0, "Neither CoinMarketCap nor Coinbase gave prices for " & String.Join(", ", symbols) & ".", Nothing))
            Catch ex As Exception When Not ExceptionUtil.IsCancellation(ex) OrElse Not ct.IsCancellationRequested
                Return (Nothing, Nothing, $"CoinMarketCap: {If(cmcProblem, "paused")}; Coinbase: {TextUtil.Truncate(ex.Message, 160)}")
            End Try
        End Function

        ''' <summary>CoinMarketCap's answer (its JSON error too: a refusal still answers JSON with status.error_code).</summary>
        Private Async Function ReadCoinMarketCapAsync(symbols As List(Of String), ct As CancellationToken) As Task(Of (Quotes As List(Of (Symbol As String, Name As String, Slug As String, Price As Double)), At As DateTime?, [Error] As String))
            Dim url = $"{_opt.CryptoUrl}?symbol={Uri.EscapeDataString(String.Join(",", symbols))}&convert=USD"
            Using req As New HttpRequestMessage(HttpMethod.Get, url)
                If Not String.IsNullOrWhiteSpace(_opt.CryptoApiKey) Then req.Headers.Add("X-CMC_PRO_API_KEY", _opt.CryptoApiKey.Trim())
                Using resp = Await _http.SendAsync(req, ct).ConfigureAwait(False)
                    Dim body = Await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(False)
                    Try
                        Dim parsed = ParseCrypto(body)
                        If parsed.Error IsNot Nothing OrElse resp.IsSuccessStatusCode Then Return parsed
                    Catch ex As JsonException
                    End Try
                    Return (New List(Of (Symbol As String, Name As String, Slug As String, Price As Double)), Nothing, $"{CInt(resp.StatusCode)} {resp.ReasonPhrase}")
                End Using
            End Using
        End Function

        ''' <summary>Coinbase's exchange rates for USD: units of each coin per 1 USD (a coin's price is 1 / rate).</summary>
        Friend Shared Function ParseCoinbase(json As String) As Dictionary(Of String, Double)
            Dim result As New Dictionary(Of String, Double)(StringComparer.OrdinalIgnoreCase)
            Dim rates = TryCast(JsonNode.Parse(json)?("data")?("rates"), JsonObject)
            If rates Is Nothing Then Return result
            For Each kv In rates
                Dim v As Double
                If Double.TryParse(kv.Value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, v) Then result(kv.Key) = v
            Next
            Return result
        End Function

        Private Shared ReadOnly Coins As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
            {"BTC", "Bitcoin"}, {"ETH", "Ethereum"}, {"LTC", "Litecoin"}, {"XRP", "XRP"}, {"USDT", "Tether USDt"}, {"USDC", "USDC"}, {"SOL", "Solana"},
            {"DOGE", "Dogecoin"}, {"ADA", "Cardano"}, {"BNB", "BNB"}, {"DOT", "Polkadot"}, {"TRX", "TRON"}, {"AVAX", "Avalanche"}, {"LINK", "Chainlink"},
            {"BCH", "Bitcoin Cash"}, {"XLM", "Stellar"}, {"SHIB", "Shiba Inu"}, {"TON", "Toncoin"}, {"POL", "POL (ex-MATIC)"}, {"ATOM", "Cosmos"}, {"UNI", "Uniswap"}}

        Private Shared Function CoinName(symbol As String) As String
            Dim n As String = Nothing
            Return If(Coins.TryGetValue(If(symbol, ""), n), n, symbol)
        End Function

        ''' <summary>Reads and keeps the rate of every currency watched (the defaults and the accounts looking now).</summary>
        Friend Async Function FetchFxAsync(ct As CancellationToken) As Task
            Dim codes = PolledFx()
            If codes.Count = 0 Then Return
            Try
                ' A currency watched for the first time gets its history (the ECB publishes once each working day).
                Dim fresh = codes.Where(Function(s) Not _backfilled.Contains(s)).ToList()
                If fresh.Count > 0 AndAlso _opt.FxHistoryDays > 0 Then
                    Dim since = Await OldestAsync(Fx, fresh).ConfigureAwait(False)
                    Dim fromDay = DateTime.UtcNow.Date.AddDays(-Math.Min(3650, _opt.FxHistoryDays))
                    If Not since.HasValue OrElse since.Value > fromDay.AddDays(3) Then
                        Dim histUrl = $"{_opt.FxUrl}?base={FxBase}&quotes={String.Join(",", fresh)}&from={fromDay:yyyy-MM-dd}&to={DateTime.UtcNow:yyyy-MM-dd}"
                        For Each r In ParseFx(Await _http.GetStringAsync(histUrl, ct).ConfigureAwait(False))
                            Await AddAsync(Fx, r.Quote, r.Rate, r.Day).ConfigureAwait(False)
                        Next
                    End If
                    For Each s In fresh
                        _backfilled.Add(s)
                    Next
                End If
                Dim url = $"{_opt.FxUrl}?base={FxBase}&quotes={String.Join(",", codes)}"
                For Each r In ParseFx(Await _http.GetStringAsync(url, ct).ConfigureAwait(False))
                    Await AddAsync(Fx, r.Quote, r.Rate, r.Day).ConfigureAwait(False)
                Next
                FxUpdatedUtc = DateTime.UtcNow
                FxError = Nothing
            Catch ex As Exception When Not ExceptionUtil.IsCancellation(ex) OrElse Not ct.IsCancellationRequested
                FxError = "Frankfurter: " & TextUtil.Truncate(ex.Message, 200)
            End Try
        End Function

        ''' <summary>CoinMarketCap's simple-price answer: each coin's USD price, name and slug, and when it was read.</summary>
        Friend Shared Function ParseCrypto(json As String) As (Quotes As List(Of (Symbol As String, Name As String, Slug As String, Price As Double)), At As DateTime?, [Error] As String)
            Dim list As New List(Of (Symbol As String, Name As String, Slug As String, Price As Double))
            Dim root = TryCast(JsonNode.Parse(json), JsonObject)
            Dim status = TryCast(root?("status"), JsonObject)
            Dim code = status?("error_code")?.ToString()
            If Not String.IsNullOrEmpty(code) AndAlso code <> "0" Then Return (list, Nothing, If(status?("error_message")?.ToString(), "error " & code))
            Dim at As DateTime? = Nothing
            Dim stamp As DateTime
            If DateTime.TryParse(status?("timestamp")?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal Or DateTimeStyles.AssumeUniversal, stamp) Then at = stamp
            Dim data = root?("data")
            Dim items As IEnumerable(Of JsonNode) = If(TypeOf data Is JsonArray, DirectCast(data, JsonArray).AsEnumerable(),
                                                      If(TypeOf data Is JsonObject, DirectCast(data, JsonObject).Select(Function(kv) kv.Value), Enumerable.Empty(Of JsonNode)()))
            For Each item In items
                Dim o = TryCast(item, JsonObject)
                If o Is Nothing Then Continue For
                Dim symbol = o("symbol")?.ToString()
                Dim quotes = o("quotes")
                Dim usd As JsonNode = Nothing
                If TypeOf quotes Is JsonArray Then
                    usd = DirectCast(quotes, JsonArray).FirstOrDefault(Function(q) String.Equals(q?("symbol")?.ToString(), "USD", StringComparison.OrdinalIgnoreCase))
                ElseIf TypeOf o("quote") Is JsonObject Then
                    usd = o("quote")("USD")
                End If
                Dim price As Double
                If String.IsNullOrEmpty(symbol) OrElse usd Is Nothing OrElse Not Double.TryParse(usd("price")?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, price) Then Continue For
                list.Add((symbol.ToUpperInvariant(), o("name")?.ToString(), o("slug")?.ToString(), price))
            Next
            Return (list, at, Nothing)
        End Function

        ''' <summary>Frankfurter's rates: [{date, base, quote, rate}] (v2), or {date, rates: {EUR: …}} (v1).</summary>
        Friend Shared Function ParseFx(json As String) As List(Of (Day As DateTime, Quote As String, Rate As Double))
            Dim list As New List(Of (Day As DateTime, Quote As String, Rate As Double))
            Dim root = JsonNode.Parse(json)
            Dim add = Sub(day As String, quote As String, rate As JsonNode)
                          Dim d As DateTime, r As Double
                          If DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal Or DateTimeStyles.AssumeUniversal, d) AndAlso
                             Not String.IsNullOrEmpty(quote) AndAlso Double.TryParse(rate?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, r) Then
                              list.Add((DateTime.SpecifyKind(d.Date, DateTimeKind.Utc), quote.ToUpperInvariant(), r))
                          End If
                      End Sub
            If TypeOf root Is JsonArray Then
                For Each x In DirectCast(root, JsonArray)
                    add(x?("date")?.ToString(), x?("quote")?.ToString(), x?("rate"))
                Next
            ElseIf TypeOf root Is JsonObject AndAlso TypeOf root("rates") Is JsonObject Then
                Dim rates = DirectCast(root("rates"), JsonObject)
                For Each kv In rates
                    If TypeOf kv.Value Is JsonObject Then
                        For Each q In DirectCast(kv.Value, JsonObject)
                            add(kv.Key, q.Key, q.Value)
                        Next
                    Else
                        add(root("date")?.ToString(), kv.Key, kv.Value)
                    End If
                Next
            End If
            Return list
        End Function

        ' ------------------------------------------------------------------ the stored prices

        Private Function AddAsync(kind As String, symbol As String, price As Double, at As DateTime) As Task(Of Integer)
            Return _db.ExecuteAsync("INSERT OR IGNORE INTO MarketQuotes(Kind, Symbol, Price, Utc) VALUES (@k, @s, @p, @t)",
                                    ("@k", CObj(kind)), ("@s", symbol), ("@p", price), ("@t", Db.Ts(at)))
        End Function

        Private Function NameAsync(kind As String, symbol As String, name As String, slug As String) As Task(Of Integer)
            Return _db.ExecuteAsync("INSERT INTO MarketAssets(Kind, Symbol, Name, Slug) VALUES (@k, @s, @n, @g) ON CONFLICT(Kind, Symbol) DO UPDATE SET Name=@n, Slug=@g",
                                    ("@k", CObj(kind)), ("@s", symbol), ("@n", If(name, CObj(DBNull.Value))), ("@g", If(slug, CObj(DBNull.Value))))
        End Function

        Private Async Function OldestAsync(kind As String, symbols As List(Of String)) As Task(Of DateTime?)
            Dim oldest As DateTime? = Nothing
            For Each s In symbols
                Dim v = TryCast(Await _db.ScalarAsync("SELECT MIN(Utc) FROM MarketQuotes WHERE Kind=@k AND Symbol=@s", ("@k", CObj(kind)), ("@s", s)).ConfigureAwait(False), String)
                If String.IsNullOrEmpty(v) Then Return Nothing
                Dim t = Db.ParseTs(v)
                If Not oldest.HasValue OrElse t > oldest.Value Then oldest = t
            Next
            Return oldest
        End Function

        ''' <summary>Old prices: every one is kept KeepAllDays, then the first of each hour, and nothing after KeepDays.</summary>
        Friend Async Function ThinAsync(now As DateTime) As Task
            Dim cut = Db.Ts(now.AddDays(-Math.Max(1, _opt.KeepAllDays)))
            Await _db.ExecuteAsync("DELETE FROM MarketQuotes WHERE Utc < @c AND rowid NOT IN (SELECT MIN(rowid) FROM MarketQuotes WHERE Utc < @c GROUP BY Kind, Symbol, substr(Utc, 1, 13))",
                                   ("@c", CObj(cut))).ConfigureAwait(False)
            If _opt.KeepDays > 0 Then Await _db.ExecuteAsync("DELETE FROM MarketQuotes WHERE Utc < @c", ("@c", CObj(Db.Ts(now.AddDays(-_opt.KeepDays))))).ConfigureAwait(False)
        End Function

        Private Async Function PriceAtAsync(kind As String, symbol As String, at As DateTime) As Task(Of (Price As Double, Utc As DateTime)?)
            Dim rows = Await _db.QueryAsync("SELECT Price, Utc FROM MarketQuotes WHERE Kind=@k AND Symbol=@s AND Utc<=@t ORDER BY Utc DESC LIMIT 1",
                                            Function(r) (r.GetDouble(0), Db.ParseTs(r.GetString(1))), ("@k", CObj(kind)), ("@s", symbol), ("@t", Db.Ts(at))).ConfigureAwait(False)
            If rows.Count = 0 Then Return Nothing
            Return rows(0)
        End Function

        ''' <summary>
        ''' Every asset the account watches (no account: the defaults) now: latest price and its change over 1 hour, 24 hours, 7 and
        ''' 30 days. The account counts as looking (its own coins and currencies are read every round for the next 30 minutes).
        ''' </summary>
        Public Async Function LatestAsync(Optional kind As String = Nothing, Optional username As String = Nothing) As Task(Of List(Of MarketQuote))
            Dim w = Await WatchForAsync(username).ConfigureAwait(False)
            MarkSeen(username)
            Return Await QuotesAsync(kind, w.Crypto, w.Fx).ConfigureAwait(False)
        End Function

        Private Async Function QuotesAsync(kind As String, coins As IEnumerable(Of String), codes As IEnumerable(Of String)) As Task(Of List(Of MarketQuote))
            Dim names = (Await _db.QueryAsync("SELECT Kind, Symbol, Name FROM MarketAssets", Function(r) (Kind:=r.GetString(0), Symbol:=r.GetString(1), Name:=If(r.IsDBNull(2), Nothing, r.GetString(2)))).ConfigureAwait(False)).
                ToDictionary(Function(x) x.Kind & ":" & x.Symbol, Function(x) x.Name, StringComparer.OrdinalIgnoreCase)
            Dim list As New List(Of MarketQuote)
            Dim watched = If(kind <> Fx, coins.Select(Function(s) (Crypto, s)), Enumerable.Empty(Of (String, String))()).
                          Concat(If(kind <> Crypto, codes.Select(Function(s) (Fx, s)), Enumerable.Empty(Of (String, String))()))
            For Each w In watched
                Dim q As New MarketQuote With {.Kind = w.Item1, .Symbol = w.Item2}
                Dim n As String = Nothing
                q.Name = If(names.TryGetValue(w.Item1 & ":" & w.Item2, n) AndAlso n IsNot Nothing, n, If(w.Item1 = Fx, CurrencyName(w.Item2), w.Item2))
                Dim latest = Await PriceAtAsync(w.Item1, w.Item2, DateTime.UtcNow.AddMinutes(1)).ConfigureAwait(False)
                If latest.HasValue Then
                    q.Price = latest.Value.Price
                    q.Utc = latest.Value.Utc
                    Dim change = Async Function(span As TimeSpan) As Task(Of Double?)
                                     Dim past = Await PriceAtAsync(w.Item1, w.Item2, latest.Value.Utc - span).ConfigureAwait(False)
                                     ' Only a price from around then counts (not one much older when there is a gap).
                                     If Not past.HasValue OrElse past.Value.Price = 0 OrElse latest.Value.Utc - past.Value.Utc > span + TimeSpan.FromTicks(span.Ticks \ 2) + TimeSpan.FromDays(If(w.Item1 = Fx, 4, 0)) Then Return Nothing
                                     Return (latest.Value.Price - past.Value.Price) / past.Value.Price * 100
                                 End Function
                    If w.Item1 = Crypto Then q.Change1h = Await change(TimeSpan.FromHours(1)).ConfigureAwait(False)
                    q.Change24h = Await change(TimeSpan.FromHours(24)).ConfigureAwait(False)
                    q.Change7d = Await change(TimeSpan.FromDays(7)).ConfigureAwait(False)
                    q.Change30d = Await change(TimeSpan.FromDays(30)).ConfigureAwait(False)
                End If
                list.Add(q)
            Next
            Return list
        End Function

        ''' <summary>1h | 24h | 7d | 30d | 90d | 1y | all as a time span (Nothing: all).</summary>
        Public Shared Function RangeSpan(range As String) As TimeSpan?
            Select Case If(range, "").Trim().ToLowerInvariant()
                Case "1h" : Return TimeSpan.FromHours(1)
                Case "6h" : Return TimeSpan.FromHours(6)
                Case "", "24h", "1d" : Return TimeSpan.FromHours(24)
                Case "7d", "1w" : Return TimeSpan.FromDays(7)
                Case "30d", "1m" : Return TimeSpan.FromDays(30)
                Case "90d", "3m" : Return TimeSpan.FromDays(90)
                Case "1y", "365d" : Return TimeSpan.FromDays(365)
                Case Else : Return Nothing
            End Select
        End Function

        ''' <summary>An asset's prices over a range, at most maxPoints of them (the last price of each equal slice of time).</summary>
        Public Async Function HistoryAsync(kind As String, symbol As String, range As String, Optional maxPoints As Integer = 360) As Task(Of List(Of (Utc As DateTime, Price As Double)))
            Dim span = RangeSpan(range)
            Dim since = If(span.HasValue, DateTime.UtcNow - span.Value, DateTime.MinValue.ToUniversalTime())
            Dim rows = Await _db.QueryAsync("SELECT Utc, Price FROM MarketQuotes WHERE Kind=@k AND Symbol=@s AND Utc>=@t ORDER BY Utc",
                                            Function(r) (Db.ParseTs(r.GetString(0)), r.GetDouble(1)), ("@k", CObj(kind)), ("@s", If(symbol, "").ToUpperInvariant()),
                                            ("@t", Db.Ts(since))).ConfigureAwait(False)
            Return Downsample(rows, Math.Clamp(maxPoints, 10, 2000))
        End Function

        Friend Shared Function Downsample(rows As List(Of (Utc As DateTime, Price As Double)), maxPoints As Integer) As List(Of (Utc As DateTime, Price As Double))
            If rows.Count <= maxPoints Then Return rows
            Dim first = rows(0).Utc.Ticks, last = rows(rows.Count - 1).Utc.Ticks
            ' maxPoints - 1 slices after the first price, so there are at most maxPoints.
            Dim slice = Math.Max(1L, (last - first) \ Math.Max(1, maxPoints - 2) + 1)
            ' The first price stays (a chart opens where the range starts); then the last price of each slice.
            Dim result As New List(Of (Utc As DateTime, Price As Double)) From {rows(0)}
            Dim bucket = -1L
            For i = 1 To rows.Count - 1
                Dim r = rows(i)
                Dim b = (r.Utc.Ticks - first) \ slice
                If b <> bucket OrElse result.Count = 1 Then
                    result.Add(r)
                    bucket = b
                Else
                    result(result.Count - 1) = r
                End If
            Next
            Return result
        End Function

        Private Shared ReadOnly Currencies As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
            {"USD", "US Dollar"}, {"EUR", "Euro"}, {"GBP", "British Pound"}, {"AUD", "Australian Dollar"}, {"CNY", "Chinese Yuan"}, {"JPY", "Japanese Yen"},
            {"CAD", "Canadian Dollar"}, {"CHF", "Swiss Franc"}, {"INR", "Indian Rupee"}, {"MXN", "Mexican Peso"}, {"BRL", "Brazilian Real"}, {"KRW", "South Korean Won"},
            {"SEK", "Swedish Krona"}, {"NOK", "Norwegian Krone"}, {"DKK", "Danish Krone"}, {"PLN", "Polish Zloty"}, {"NZD", "New Zealand Dollar"}, {"SGD", "Singapore Dollar"},
            {"HKD", "Hong Kong Dollar"}, {"ZAR", "South African Rand"}, {"TRY", "Turkish Lira"}, {"CZK", "Czech Koruna"}, {"HUF", "Hungarian Forint"}, {"ILS", "Israeli Shekel"}}

        Public Shared Function CurrencyName(code As String) As String
            Dim n As String = Nothing
            Return If(Currencies.TryGetValue(If(code, ""), n), n, code)
        End Function

        ' ------------------------------------------------------------------ the market_prices tool

        Public Const ToolName As String = "market_prices"
        Public Const ToolDescription As String =
            "Live market data kept by this server: crypto prices in USD (CoinMarketCap, about every minute) and exchange rates against USD (the ECB's daily " &
            "reference rates via Frankfurter), with the change over 1 hour (crypto), 24 hours, 7 and 30 days. Leave symbols out for every watched asset; " &
            "add history (1h, 24h, 7d, 30d, 90d) for a short price series. Quote the time of the price you use."
        Public Const ToolSchema As String =
            "{""type"":""object"",""properties"":{""symbols"":{""type"":""array"",""items"":{""type"":""string""},""description"":""Coins (BTC, ETH) or currencies (EUR, GBP); empty: all watched""}," &
            """kind"":{""type"":""string"",""enum"":[""all"",""crypto"",""fx""]},""history"":{""type"":""string"",""enum"":[""1h"",""24h"",""7d"",""30d"",""90d""],""description"":""Also a price series over this range""}}}"

        ''' <summary>
        ''' The market_prices tool's answer (agents and chats): prices, changes and optionally a short series per asset. Without
        ''' symbols: what the account watches (agents: the defaults); named symbols may be any asset the server reads.
        ''' </summary>
        Public Async Function ToolAnswerAsync(argsJson As String, Optional username As String = Nothing) As Task(Of String)
            Dim args As JsonObject = Nothing
            Try
                args = TryCast(JsonNode.Parse(If(String.IsNullOrWhiteSpace(argsJson), "{}", argsJson)), JsonObject)
            Catch ex As JsonException
            End Try
            Dim kind = If(args?("kind")?.ToString(), "all").ToLowerInvariant()
            Dim wanted = New HashSet(Of String)(If(TryCast(args?("symbols"), JsonArray), New JsonArray()).Select(Function(x) If(x?.ToString(), "").Trim().ToUpperInvariant()).
                                                Where(Function(s) s.Length > 0), StringComparer.OrdinalIgnoreCase)
            Dim history = args?("history")?.ToString()
            Dim onlyKind = If(kind = Crypto OrElse kind = Fx, kind, Nothing)
            Dim quotes As List(Of MarketQuote)
            If wanted.Count = 0 Then
                quotes = Await LatestAsync(onlyKind, username).ConfigureAwait(False)
            Else
                Dim w = Await WatchForAsync(username).ConfigureAwait(False)
                quotes = Await QuotesAsync(onlyKind, w.Crypto.Concat(PolledCrypto()).Distinct(StringComparer.OrdinalIgnoreCase).Where(Function(s) wanted.Contains(s)),
                                           w.Fx.Concat(PolledFx()).Distinct(StringComparer.OrdinalIgnoreCase).Where(Function(s) wanted.Contains(s))).ConfigureAwait(False)
            End If
            Dim missing = wanted.Where(Function(s) Not quotes.Any(Function(q) String.Equals(q.Symbol, s, StringComparison.OrdinalIgnoreCase))).ToList()
            Dim items As New List(Of Object)
            For Each q In quotes
                Dim series As Object = Nothing
                If Not String.IsNullOrEmpty(history) Then
                    series = (Await HistoryAsync(q.Kind, q.Symbol, history, 24).ConfigureAwait(False)).Select(Function(p) New With {.utc = p.Utc.ToString("yyyy-MM-dd HH:mm"), .price = Round(p.Price)}).ToList()
                End If
                items.Add(New With {.kind = q.Kind, .symbol = q.Symbol, .name = q.Name,
                                    .price = If(q.Price.HasValue, Round(q.Price.Value), CType(Nothing, Double?)),
                                    .unit = If(q.Kind = Crypto, "USD per " & q.Symbol, $"{q.Symbol} per 1 {FxBase}"),
                                    .usdPer = If(q.Kind = Fx AndAlso q.Price.HasValue AndAlso q.Price.Value <> 0, Round(1 / q.Price.Value), CType(Nothing, Double?)),
                                    .asOfUtc = q.Utc?.ToString("yyyy-MM-dd HH:mm") & If(q.Utc.HasValue, " UTC", ""),
                                    .change1hPct = Pct(q.Change1h), .change24hPct = Pct(q.Change24h), .change7dPct = Pct(q.Change7d), .change30dPct = Pct(q.Change30d), .history = series})
            Next
            Return JsonUtil.ToJson(New With {.source = "CoinMarketCap (crypto), Frankfurter / ECB (exchange rates)", .quotes = items,
                                             .notWatched = If(missing.Count > 0, $"Not watched here: {String.Join(", ", missing)} (add coins and currencies to your watch list on the Money tab).", Nothing),
                                             .problems = If(CryptoError Is Nothing AndAlso FxError Is Nothing, Nothing, String.Join(" ", {CryptoError, FxError}.Where(Function(e) e IsNot Nothing)))})
        End Function

        Private Shared Function Round(v As Double) As Double
            If v = 0 Then Return 0
            Dim digits = Math.Max(2, Math.Min(10, 6 - CInt(Math.Floor(Math.Log10(Math.Abs(v))))))
            Return Math.Round(v, digits)
        End Function

        Private Shared Function Pct(v As Double?) As Double?
            Return If(v.HasValue, Math.Round(v.Value, 2), CType(Nothing, Double?))
        End Function

    End Class

End Namespace
