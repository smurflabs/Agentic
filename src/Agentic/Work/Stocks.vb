Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.Globalization
Imports System.Net.Http
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Agentic.Configuration
Imports Agentic.Logging
Imports Agentic.Persistence
Imports Agentic.Utilities

Namespace Work

    ''' <summary>A stock or index as it is now (Yahoo Finance's chart meta), with its moves over the day, 5 sessions and a month.</summary>
    Public NotInheritable Class StockQuote
        Public Property Symbol As String
        Public Property Name As String
        Public Property Exchange As String
        ''' <summary>EQUITY, ETF, INDEX …</summary>
        Public Property Type As String
        Public Property Currency As String
        Public Property Price As Double?
        Public Property PreviousClose As Double?
        Public Property Change As Double?
        Public Property ChangePct As Double?
        Public Property Open As Double?
        Public Property DayHigh As Double?
        Public Property DayLow As Double?
        Public Property Volume As Long?
        Public Property YearHigh As Double?
        Public Property YearLow As Double?
        ''' <summary>When the price was set (the last trade).</summary>
        Public Property MarketTimeUtc As DateTime?
        ''' <summary>pre | open | post | closed (from the trading periods Yahoo gives).</summary>
        Public Property MarketState As String
        Public Property Change5d As Double?
        Public Property Change1m As Double?
        ''' <summary>Today's prices (5-minute steps) for the small chart: [unix ms, price].</summary>
        Public Property Intraday As List(Of Double())
        Public Property UpdatedUtc As DateTime
        <Text.Json.Serialization.JsonIgnore>
        Public Property RegularStartUtc As DateTime?
        <Text.Json.Serialization.JsonIgnore>
        Public Property RegularEndUtc As DateTime?
    End Class

    ''' <summary>
    ''' The Stocks tab's data: quotes from Yahoo Finance's chart API (no key) for the watched tickers, every minute while a market
    ''' is open (every 15 minutes otherwise); charts of any range straight from Yahoo (kept a short while); and the ticker list
    ''' from NASDAQ Trader's symbol directory (NASDAQ-listed and other US-listed), read once a day, for names and lookup.
    ''' Agents and chats ask through <see cref="ToolAnswerAsync"/> (the stock_quotes tool), for any ticker.
    ''' </summary>
    Public NotInheritable Class StocksService
        ''' <summary>An account's own tickers (UserPreferences, a JSON list); without one it watches the defaults.</summary>
        Public Const PreferenceKey As String = "stocks.watchlist"
        ''' <summary>An account's own tickers are read every minute for this long after it last opened the tab.</summary>
        Private Shared ReadOnly ActiveFor As TimeSpan = TimeSpan.FromMinutes(30)
        Private Shared ReadOnly TickerPattern As New Regex("^\^?[A-Z0-9][A-Z0-9.\-=]{0,11}$")

        Private ReadOnly _db As Database
        Private ReadOnly _opt As StocksOptions
        Private ReadOnly _log As StructuredLogger
        Private ReadOnly _http As HttpClient
        Private ReadOnly _wake As New SemaphoreSlim(0)
        Private ReadOnly _quotes As New ConcurrentDictionary(Of String, StockQuote)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _charts As New ConcurrentDictionary(Of String, (At As DateTime, Data As JsonObject))(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _daily As New ConcurrentDictionary(Of String, (At As DateTime, Closes As List(Of Double)))(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _defaults As List(Of String)
        ''' <summary>Each account's own list as read from UserPreferences (Nothing: the defaults), and when it last looked.</summary>
        Private ReadOnly _lists As New ConcurrentDictionary(Of String, List(Of String))(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _seen As New ConcurrentDictionary(Of String, DateTime)(StringComparer.OrdinalIgnoreCase)
        Private _directory As New Dictionary(Of String, (Name As String, Exchange As String, Etf As Boolean))(StringComparer.OrdinalIgnoreCase)
        Private _directoryAt As DateTime = DateTime.MinValue
        Private _lastRefresh As DateTime = DateTime.MinValue

        Public Property UpdatedUtc As DateTime?
        Public Property LastError As String
        Public Property DirectoryError As String

        Public Sub New(db As Database, options As StocksOptions, log As StructuredLogger, Optional handler As HttpMessageHandler = Nothing)
            _db = db
            _opt = If(options, New StocksOptions())
            _log = log
            _http = If(handler Is Nothing, New HttpClient(New HttpClientHandler With {.AutomaticDecompression = Net.DecompressionMethods.All}), New HttpClient(handler))
            _http.Timeout = TimeSpan.FromSeconds(Math.Max(5, _opt.TimeoutSeconds))
            ' Yahoo answers browsers; unknown clients are often refused.
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36")
            _defaults = Clean(If(_opt.Symbols.Count > 0, _opt.Symbols, StocksOptions.DefaultSymbols.ToList()))
        End Sub

        Public ReadOnly Property Enabled As Boolean
            Get
                Return _opt.Enabled
            End Get
        End Property

        Public ReadOnly Property Options As StocksOptions
            Get
                Return _opt
            End Get
        End Property

        ''' <summary>The tickers every account watches until it chooses its own (Stocks.Symbols, else the built-in ten).</summary>
        Public ReadOnly Property Defaults As List(Of String)
            Get
                Return _defaults.ToList()
            End Get
        End Property

        Public ReadOnly Property DirectorySize As Integer
            Get
                Return _directory.Count
            End Get
        End Property

        Friend Shared Function Normalize(symbol As String) As String
            Return If(symbol, "").Trim().ToUpperInvariant().Replace(" ", "")
        End Function

        Friend Shared Function IsTicker(symbol As String) As Boolean
            Return TickerPattern.IsMatch(Normalize(symbol))
        End Function

        Private Shared Function Clean(list As IEnumerable(Of String)) As List(Of String)
            Return If(list, Enumerable.Empty(Of String)()).Select(AddressOf Normalize).Where(AddressOf IsTicker).Distinct(StringComparer.OrdinalIgnoreCase).Take(60).ToList()
        End Function

        ' ------------------------------------------------------------------ each account's watch list

        ''' <summary>An account's tickers: its own list (Custom) or the defaults. No account (agents): the defaults.</summary>
        Public Async Function WatchForAsync(username As String) As Task(Of (Symbols As List(Of String), Custom As Boolean))
            If String.IsNullOrWhiteSpace(username) Then Return (_defaults.ToList(), False)
            Dim own As List(Of String) = Nothing
            If Not _lists.TryGetValue(username, own) Then
                Dim json = TryCast(Await _db.ScalarAsync("SELECT Value FROM UserPreferences WHERE Username=@u AND Key=@k",
                                                         ("@u", CObj(username)), ("@k", PreferenceKey)).ConfigureAwait(False), String)
                Try
                    own = If(String.IsNullOrWhiteSpace(json), Nothing, Clean(If(TryCast(JsonNode.Parse(json), JsonArray), New JsonArray()).Select(Function(x) x?.ToString())))
                Catch ex As JsonException
                    own = Nothing
                End Try
                If own IsNot Nothing AndAlso own.Count = 0 Then own = Nothing
                _lists(username) = own
            End If
            Return If(own Is Nothing, (_defaults.ToList(), False), (own.ToList(), True))
        End Function

        ''' <summary>
        ''' Saves an account's own tickers. Each must be a ticker Yahoo knows (checked now); unknown ones are named in the error
        ''' and nothing is saved. The defaults again (the same tickers in the same order) are kept as "the defaults", so the
        ''' account follows them if they change. Returns the list kept, in the order given.
        ''' </summary>
        Public Async Function SetWatchlistAsync(username As String, symbols As IEnumerable(Of String), ct As CancellationToken) As Task(Of List(Of String))
            If String.IsNullOrWhiteSpace(username) Then Throw New ArgumentException("Watch lists belong to an account.")
            Dim wanted = Clean(symbols)
            If wanted.Count = 0 Then Throw New ArgumentException("Watch at least one ticker (AAPL, MSFT, ^GSPC …).")
            Dim unknown As New List(Of String)
            For Each s In wanted.Where(Function(x) Not _quotes.ContainsKey(x))
                Dim q = Await FetchQuoteAsync(s, ct).ConfigureAwait(False)
                If q Is Nothing Then unknown.Add(s) Else _quotes(s) = q
            Next
            If unknown.Count > 0 Then Throw New ArgumentException("Yahoo Finance does not know " & String.Join(", ", unknown) & ".")
            If wanted.SequenceEqual(_defaults, StringComparer.OrdinalIgnoreCase) Then
                Await ResetWatchlistAsync(username).ConfigureAwait(False)
                Return wanted
            End If
            Await _db.ExecuteAsync("INSERT INTO UserPreferences(Username, Key, Value, UpdatedUtc) VALUES (@u, @k, @v, @n) " &
                                   "ON CONFLICT(Username, Key) DO UPDATE SET Value=excluded.Value, UpdatedUtc=excluded.UpdatedUtc",
                                   ("@u", CObj(username)), ("@k", PreferenceKey), ("@v", JsonUtil.ToJson(wanted)), ("@n", Db.Ts(DateTime.UtcNow))).ConfigureAwait(False)
            _lists(username) = wanted
            _seen(username) = DateTime.UtcNow
            Return wanted
        End Function

        ''' <summary>The account watches the defaults again.</summary>
        Public Async Function ResetWatchlistAsync(username As String) As Task
            If String.IsNullOrWhiteSpace(username) Then Return
            Await _db.ExecuteAsync("DELETE FROM UserPreferences WHERE Username=@u AND Key=@k", ("@u", CObj(username)), ("@k", PreferenceKey)).ConfigureAwait(False)
            _lists(username) = Nothing
        End Function

        ''' <summary>What the server reads every round: the defaults, and the own lists of accounts that looked in the last 30 minutes.</summary>
        Friend Function PolledSymbols() As List(Of String)
            Dim all As New List(Of String)(_defaults)
            For Each kv In _seen
                If DateTime.UtcNow - kv.Value > ActiveFor Then
                    _seen.TryRemove(kv.Key, Nothing)
                    Continue For
                End If
                Dim own As List(Of String) = Nothing
                If _lists.TryGetValue(kv.Key, own) AndAlso own IsNot Nothing Then all.AddRange(own)
            Next
            Return all.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        End Function

        ' ------------------------------------------------------------------ reading

        Public Sub Start(ct As CancellationToken)
            If Not _opt.Enabled Then Return
            Dim unused = Task.Run(Function() LoopAsync(ct))
        End Sub

        Private Async Function LoopAsync(ct As CancellationToken) As Task
            While Not ct.IsCancellationRequested
                Try
                    If DateTime.UtcNow - _directoryAt > TimeSpan.FromHours(24) Then Await LoadDirectoryAsync(ct).ConfigureAwait(False)
                    Await RefreshQuotesAsync(ct).ConfigureAwait(False)
                Catch ex As Exception When Not ExceptionUtil.IsCancellation(ex)
                    _log?.Warn("Stocks", "Reading quotes failed: " & ex.Message)
                End Try
                ' Every minute while a market is open (or in its pre- and after-hours), else every 15 minutes.
                Dim busy = _quotes.Values.Any(Function(q) q.MarketState <> "closed")
                Dim wait = TimeSpan.FromSeconds(If(busy, Math.Max(20, _opt.PollSeconds), Math.Max(60, _opt.ClosedPollMinutes * 60)))
                Try
                    Await _wake.WaitAsync(wait, ct).ConfigureAwait(False)
                Catch ex As OperationCanceledException
                    Exit While
                End Try
            End While
        End Function

        ''' <summary>Reads the quotes now (at most every 15 s, whoever asks).</summary>
        Public Async Function RefreshAsync(ct As CancellationToken) As Task
            If DateTime.UtcNow - _lastRefresh < TimeSpan.FromSeconds(15) Then Return
            _lastRefresh = DateTime.UtcNow
            Await RefreshQuotesAsync(ct).ConfigureAwait(False)
        End Function

        Friend Async Function RefreshQuotesAsync(ct As CancellationToken) As Task
            Dim errors As New List(Of String)
            For Each s In PolledSymbols()
                Try
                    Dim q = Await FetchQuoteAsync(s, ct).ConfigureAwait(False)
                    If q Is Nothing Then errors.Add(s & ": unknown to Yahoo") Else _quotes(s) = q
                Catch ex As Exception When Not ExceptionUtil.IsCancellation(ex) OrElse Not ct.IsCancellationRequested
                    errors.Add(s & ": " & TextUtil.Truncate(ex.Message, 100))
                End Try
            Next
            UpdatedUtc = DateTime.UtcNow
            LastError = If(errors.Count = 0, Nothing, "Yahoo Finance: " & String.Join("; ", errors.Take(4)) & If(errors.Count > 4, $" and {errors.Count - 4} more", ""))
        End Function

        ''' <summary>One ticker now: today's chart (5-minute steps) and its meta, plus the month's daily closes (kept an hour) for 5D and 1M.</summary>
        Public Async Function FetchQuoteAsync(symbol As String, ct As CancellationToken) As Task(Of StockQuote)
            Dim s = Normalize(symbol)
            If Not IsTicker(s) Then Return Nothing
            Dim today = Await ChartJsonAsync(s, "1d", "5m", ct).ConfigureAwait(False)
            If today Is Nothing Then Return Nothing
            Dim q = ParseQuote(today, DateTime.UtcNow)
            If q Is Nothing Then Return Nothing
            Dim known As (Name As String, Exchange As String, Etf As Boolean)
            If String.IsNullOrEmpty(q.Name) AndAlso _directory.TryGetValue(s, known) Then q.Name = known.Name
            Dim daily As (At As DateTime, Closes As List(Of Double)) = Nothing
            If Not _daily.TryGetValue(s, daily) OrElse DateTime.UtcNow - daily.At > TimeSpan.FromHours(1) Then
                Try
                    Dim month = Await ChartJsonAsync(s, "1mo", "1d", ct).ConfigureAwait(False)
                    Dim closes = If(month Is Nothing, New List(Of Double), Series(month).Select(Function(p) p(1)).ToList())
                    daily = (DateTime.UtcNow, closes)
                    _daily(s) = daily
                Catch ex As Exception When Not ExceptionUtil.IsCancellation(ex)
                End Try
            End If
            If daily.Closes IsNot Nothing AndAlso daily.Closes.Count > 1 AndAlso q.Price.HasValue Then
                ' The last daily close is today's (or the last session's): 5 sessions back is 6 from the end.
                Dim pct = Function(past As Double) If(past = 0, CType(Nothing, Double?), (q.Price.Value - past) / past * 100)
                If daily.Closes.Count >= 6 Then q.Change5d = pct(daily.Closes(daily.Closes.Count - 6))
                q.Change1m = pct(daily.Closes(0))
            End If
            Return q
        End Function

        ''' <summary>Yahoo's chart answer for a ticker (Nothing when Yahoo does not know it).</summary>
        Private Async Function ChartJsonAsync(symbol As String, range As String, interval As String, ct As CancellationToken) As Task(Of JsonObject)
            Dim url = $"{_opt.ChartUrl.TrimEnd("/"c)}/{Uri.EscapeDataString(symbol)}?range={range}&interval={interval}"
            Using resp = Await _http.GetAsync(url, ct).ConfigureAwait(False)
                Dim body = Await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(False)
                Dim root As JsonNode = Nothing
                Try
                    root = JsonNode.Parse(body)
                Catch ex As JsonException
                End Try
                Dim result = TryCast(TryCast(root?("chart")?("result"), JsonArray)?.FirstOrDefault(), JsonObject)
                If result IsNot Nothing Then Return result
                If resp.StatusCode = Net.HttpStatusCode.NotFound OrElse root?("chart")?("error") IsNot Nothing Then Return Nothing
                Throw New HttpRequestException($"{CInt(resp.StatusCode)} {resp.ReasonPhrase}")
            End Using
        End Function

        ''' <summary>A quote from a chart answer's meta (and today's points from it when its range is a day).</summary>
        Friend Shared Function ParseQuote(chart As JsonObject, now As DateTime) As StockQuote
            Dim meta = TryCast(chart?("meta"), JsonObject)
            If meta Is Nothing Then Return Nothing
            Dim num = Function(name As String) As Double?
                          Dim v As Double
                          Return If(Double.TryParse(meta(name)?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, v), v, CType(Nothing, Double?))
                      End Function
            ' A longer chart's chartPreviousClose is the close before its range began, and its points are not today's.
            Dim range = meta("range")?.ToString()
            Dim isDay = String.IsNullOrEmpty(range) OrElse range = "1d"
            Dim q As New StockQuote With {
                .Symbol = meta("symbol")?.ToString(), .Name = If(meta("longName")?.ToString(), meta("shortName")?.ToString()),
                .Exchange = If(meta("fullExchangeName")?.ToString(), meta("exchangeName")?.ToString()), .Type = meta("instrumentType")?.ToString(),
                .Currency = meta("currency")?.ToString(), .Price = num("regularMarketPrice"), .PreviousClose = If(num("previousClose"), If(isDay, num("chartPreviousClose"), Nothing)),
                .DayHigh = num("regularMarketDayHigh"), .DayLow = num("regularMarketDayLow"), .YearHigh = num("fiftyTwoWeekHigh"), .YearLow = num("fiftyTwoWeekLow"),
                .UpdatedUtc = now}
            Dim vol = num("regularMarketVolume")
            If vol.HasValue Then q.Volume = CLng(vol.Value)
            Dim t = num("regularMarketTime")
            If t.HasValue Then q.MarketTimeUtc = DateTimeOffset.FromUnixTimeSeconds(CLng(t.Value)).UtcDateTime
            If q.Price.HasValue AndAlso q.PreviousClose.HasValue AndAlso q.PreviousClose.Value <> 0 Then
                q.Change = q.Price.Value - q.PreviousClose.Value
                q.ChangePct = q.Change.Value / q.PreviousClose.Value * 100
            End If
            ' The trading periods of the day: before, during and after the regular session.
            Dim period = Function(name As String) As (Start As DateTime?, [End] As DateTime?)
                             Dim p = TryCast(meta("currentTradingPeriod")?(name), JsonObject)
                             Dim s As Long, e As Long
                             If p Is Nothing OrElse Not Long.TryParse(p("start")?.ToString(), s) OrElse Not Long.TryParse(p("end")?.ToString(), e) Then Return (Nothing, Nothing)
                             Return (DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime, DateTimeOffset.FromUnixTimeSeconds(e).UtcDateTime)
                         End Function
            Dim pre = period("pre"), regular = period("regular"), post = period("post")
            q.RegularStartUtc = regular.Start : q.RegularEndUtc = regular.End
            Dim within = Function(p As (Start As DateTime?, [End] As DateTime?)) p.Start.HasValue AndAlso now >= p.Start.Value AndAlso now < p.End.Value
            q.MarketState = If(within(regular), "open", If(within(pre), "pre", If(within(post), "post", "closed")))
            Dim points = If(isDay, Series(chart), New List(Of Double()))
            If points.Count > 0 Then
                q.Intraday = points
                q.Open = points(0)(1)
                If Not q.DayHigh.HasValue Then q.DayHigh = points.Max(Function(p) p(1))
                If Not q.DayLow.HasValue Then q.DayLow = points.Min(Function(p) p(1))
            End If
            Return q
        End Function

        ''' <summary>A chart answer's closes as [unix ms, price], gaps (null closes) left out.</summary>
        Friend Shared Function Series(chart As JsonObject) As List(Of Double())
            Dim list As New List(Of Double())
            Dim times = TryCast(chart?("timestamp"), JsonArray)
            Dim closes = TryCast(chart?("indicators")?("quote")?(0)?("close"), JsonArray)
            If times Is Nothing OrElse closes Is Nothing Then Return list
            For i = 0 To Math.Min(times.Count, closes.Count) - 1
                Dim t As Long, c As Double
                If Long.TryParse(times(i)?.ToString(), t) AndAlso closes(i) IsNot Nothing AndAlso Double.TryParse(closes(i).ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, c) Then
                    list.Add({CDbl(t) * 1000, c})
                End If
            Next
            Return list
        End Function

        ''' <summary>1d | 5d | 1mo | 6mo | ytd | 1y | 5y | max as Yahoo's range and interval.</summary>
        Friend Shared Function RangeOf(range As String) As (Range As String, Interval As String, Keep As TimeSpan)
            Select Case If(range, "").Trim().ToLowerInvariant()
                Case "5d" : Return ("5d", "30m", TimeSpan.FromMinutes(5))
                Case "1mo", "1m" : Return ("1mo", "60m", TimeSpan.FromMinutes(30))
                Case "6mo", "6m" : Return ("6mo", "1d", TimeSpan.FromHours(1))
                Case "ytd" : Return ("ytd", "1d", TimeSpan.FromHours(1))
                Case "1y" : Return ("1y", "1d", TimeSpan.FromHours(1))
                Case "5y" : Return ("5y", "1wk", TimeSpan.FromHours(6))
                Case "max" : Return ("max", "1mo", TimeSpan.FromHours(12))
                Case Else : Return ("1d", "5m", TimeSpan.FromSeconds(55))
            End Select
        End Function

        ''' <summary>A ticker's chart over a range (Yahoo's own history, kept a short while): its points and previous close.</summary>
        Public Async Function ChartAsync(symbol As String, range As String, ct As CancellationToken) As Task(Of (Points As List(Of Double()), PreviousClose As Double?, Quote As StockQuote)?)
            Dim s = Normalize(symbol)
            If Not IsTicker(s) Then Return Nothing
            Dim r = RangeOf(range)
            Dim key = s & "|" & r.Range
            Dim hit As (At As DateTime, Data As JsonObject)
            Dim data As JsonObject
            If _charts.TryGetValue(key, hit) AndAlso DateTime.UtcNow - hit.At < r.Keep Then
                data = hit.Data
            Else
                data = Await ChartJsonAsync(s, r.Range, r.Interval, ct).ConfigureAwait(False)
                If data Is Nothing Then Return Nothing
                _charts(key) = (DateTime.UtcNow, data)
                If _charts.Count > 400 Then
                    For Each old In _charts.OrderBy(Function(kv) kv.Value.At).Take(100).ToList()
                        _charts.TryRemove(old.Key, Nothing)
                    Next
                End If
            End If
            Dim q = ParseQuote(data, DateTime.UtcNow)
            Dim prev = If(r.Range = "1d", q?.PreviousClose, CType(Nothing, Double?))
            Return (Series(data), prev, q)
        End Function

        ''' <summary>A quote recent enough to show: a few rounds old at most (it is read every round while it is watched).</summary>
        Private Function Fresh(q As StockQuote) As Boolean
            Dim most = If(q.MarketState = "closed", TimeSpan.FromMinutes(Math.Max(10, _opt.ClosedPollMinutes * 2)), TimeSpan.FromSeconds(Math.Max(180, _opt.PollSeconds * 3)))
            Return DateTime.UtcNow - q.UpdatedUtc < most
        End Function

        ''' <summary>
        ''' An account's tickers with their latest quotes, in its list's order. The account counts as looking (its own tickers are
        ''' read every round for the next 30 minutes); a ticker not read lately is read now. A ticker that cannot be read has no price.
        ''' </summary>
        Public Async Function LatestForAsync(username As String, ct As CancellationToken) As Task(Of (Quotes As List(Of StockQuote), Watch As List(Of String), Custom As Boolean))
            Dim w = Await WatchForAsync(username).ConfigureAwait(False)
            If Not String.IsNullOrWhiteSpace(username) Then _seen(username) = DateTime.UtcNow
            Dim stale = w.Symbols.Where(Function(s)
                                            Dim q As StockQuote = Nothing
                                            Return Not _quotes.TryGetValue(s, q) OrElse Not Fresh(q)
                                        End Function).ToList()
            If stale.Count > 0 Then
                Using gate As New SemaphoreSlim(4)
                    Await Task.WhenAll(stale.Select(Async Function(s)
                                                        Await gate.WaitAsync(ct).ConfigureAwait(False)
                                                        Try
                                                            Dim q = Await FetchQuoteAsync(s, ct).ConfigureAwait(False)
                                                            If q IsNot Nothing Then _quotes(s) = q
                                                        Catch ex As Exception When Not ExceptionUtil.IsCancellation(ex)
                                                        Finally
                                                            gate.Release()
                                                        End Try
                                                    End Function)).ConfigureAwait(False)
                End Using
            End If
            Dim quotes = w.Symbols.Select(Function(s)
                                              Dim q As StockQuote = Nothing
                                              If _quotes.TryGetValue(s, q) Then Return q
                                              Dim known As (Name As String, Exchange As String, Etf As Boolean)
                                              Return New StockQuote With {.Symbol = s, .Name = If(_directory.TryGetValue(s, known), known.Name, Nothing)}
                                          End Function).ToList()
            Return (quotes, w.Symbols, w.Custom)
        End Function

        ' ------------------------------------------------------------------ the ticker directory

        ''' <summary>NASDAQ Trader's symbol directory: NASDAQ-listed and other US-listed (NYSE, NYSE American, Arca …) tickers with names.</summary>
        Friend Async Function LoadDirectoryAsync(ct As CancellationToken) As Task
            Dim fresh As New Dictionary(Of String, (Name As String, Exchange As String, Etf As Boolean))(StringComparer.OrdinalIgnoreCase)
            Dim problems As New List(Of String)
            For Each url In {_opt.NasdaqListedUrl, _opt.OtherListedUrl}.Where(Function(u) Not String.IsNullOrWhiteSpace(u))
                Try
                    For Each e In ParseDirectory(Await _http.GetStringAsync(url, ct).ConfigureAwait(False))
                        If Not fresh.ContainsKey(e.Symbol) Then fresh(e.Symbol) = (e.Name, e.Exchange, e.Etf)
                    Next
                Catch ex As Exception When Not ExceptionUtil.IsCancellation(ex) OrElse Not ct.IsCancellationRequested
                    problems.Add(New Uri(url).Host & ": " & TextUtil.Truncate(ex.Message, 120))
                End Try
            Next
            If fresh.Count > 0 Then
                _directory = fresh
                _directoryAt = DateTime.UtcNow
            Else
                _directoryAt = DateTime.UtcNow.AddHours(-23) ' try again in an hour
            End If
            DirectoryError = If(problems.Count = 0, Nothing, "Ticker list: " & String.Join("; ", problems))
        End Function

        ''' <summary>
        ''' A symbol directory file: nasdaqlisted.txt (Symbol|Security Name|Market Category|Test Issue|…|ETF|NextShares) or
        ''' otherlisted.txt (ACT Symbol|Security Name|Exchange|CQS Symbol|ETF|Round Lot Size|Test Issue|NASDAQ Symbol); test issues left out.
        ''' </summary>
        Friend Shared Function ParseDirectory(text As String) As List(Of (Symbol As String, Name As String, Exchange As String, Etf As Boolean))
            Dim list As New List(Of (Symbol As String, Name As String, Exchange As String, Etf As Boolean))
            Dim lines = If(text, "").Replace(vbCr, "").Split(ControlChars.Lf)
            If lines.Length < 2 Then Return list
            Dim head = lines(0).Split("|"c).Select(Function(h) h.Trim()).ToList()
            Dim col = Function(name As String) head.FindIndex(Function(h) String.Equals(h, name, StringComparison.OrdinalIgnoreCase))
            Dim iSym = If(col("Symbol") >= 0, col("Symbol"), col("ACT Symbol")), iName = col("Security Name"), iTest = col("Test Issue"), iEtf = col("ETF"), iExch = col("Exchange")
            If iSym < 0 OrElse iName < 0 Then Return list
            Dim exchanges As New Dictionary(Of String, String) From {{"N", "NYSE"}, {"A", "NYSE American"}, {"P", "NYSE Arca"}, {"Z", "Cboe BZX"}, {"V", "IEX"}}
            For Each line In lines.Skip(1)
                If line.StartsWith("File Creation Time", StringComparison.OrdinalIgnoreCase) Then Continue For
                Dim f = line.Split("|"c)
                If f.Length <= Math.Max(iSym, iName) Then Continue For
                If iTest >= 0 AndAlso iTest < f.Length AndAlso f(iTest).Trim() = "Y" Then Continue For
                Dim sym = YahooSymbol(f(iSym).Trim().ToUpperInvariant())
                If sym.Length = 0 Then Continue For
                ' "Apple Inc. - Common Stock" and "Pfizer, Inc. Common Stock" read as "Apple Inc." and "Pfizer, Inc.".
                Dim name = Regex.Replace(f(iName).Trim(), "\s+(-\s+)?(Class [A-Z] )?(New )?(Common Stock|Ordinary Shares|American Depositary Shares)\b.*$", "", RegexOptions.IgnoreCase)
                If name.Length = 0 Then name = f(iName).Trim()
                ' "… Depositary Shares, each representing a 1/400th interest in …" reads as "… Depositary Shares".
                Dim cut = name.IndexOf(", each ", StringComparison.OrdinalIgnoreCase)
                If cut > 0 Then name = name.Substring(0, cut)
                Dim exch = If(iExch >= 0 AndAlso iExch < f.Length, exchanges.GetValueOrDefault(f(iExch).Trim(), f(iExch).Trim()), "NASDAQ")
                list.Add((sym, name, exch, iEtf >= 0 AndAlso iEtf < f.Length AndAlso f(iEtf).Trim() = "Y"))
            Next
            Return list
        End Function

        ''' <summary>
        ''' The directory's symbol as Yahoo writes it: classes with a dash (BRK.B → BRK-B), preferred series with -P (JPM$J → JPM-PJ),
        ''' warrants with -WT (AAC.W → AAC-WT).
        ''' </summary>
        Friend Shared Function YahooSymbol(symbol As String) As String
            Dim s = If(symbol, "")
            Dim pref = s.IndexOf("$"c)
            If pref >= 0 Then Return s.Substring(0, pref) & "-P" & s.Substring(pref + 1).Replace(".", "")
            If s.EndsWith(".W", StringComparison.Ordinal) OrElse s.EndsWith(".WS", StringComparison.Ordinal) Then Return s.Substring(0, s.LastIndexOf("."c)) & "-WT"
            Return s.Replace("."c, "-"c)
        End Function

        ''' <summary>Lower-case letters and digits only, so "JP Morgan" matches "jpmorgan".</summary>
        Private Shared Function Squash(text As String) As String
            Dim sb As New Text.StringBuilder(If(text, "").Length)
            For Each c In If(text, "")
                If Char.IsLetterOrDigit(c) Then sb.Append(Char.ToLowerInvariant(c))
            Next
            Return sb.ToString()
        End Function

        Private Shared ReadOnly MinorIssue As New Regex("\b(warrants?|rights?|units?|preferred|depositary|notes|debentures)\b", RegexOptions.IgnoreCase)

        ''' <summary>
        ''' The ticker itself, then tickers starting with the query, then names containing it (spaces and punctuation ignored), at most
        ''' limit. Shares come first, then ETFs, then preferreds, warrants, rights and units.
        ''' </summary>
        Public Function Search(query As String, limit As Integer) As List(Of (Symbol As String, Name As String, Exchange As String, Etf As Boolean))
            Dim q = If(query, "").Trim()
            Dim qs = Squash(q)
            If q.Length = 0 Then Return New List(Of (Symbol As String, Name As String, Exchange As String, Etf As Boolean))
            Dim all = _directory.Select(Function(kv) (Symbol:=kv.Key, kv.Value.Name, kv.Value.Exchange, kv.Value.Etf))
            Dim exact = all.Where(Function(e) String.Equals(e.Symbol, q, StringComparison.OrdinalIgnoreCase))
            Dim starts = all.Where(Function(e) e.Symbol.StartsWith(q, StringComparison.OrdinalIgnoreCase) AndAlso Not String.Equals(e.Symbol, q, StringComparison.OrdinalIgnoreCase)).
                OrderBy(Function(e) MinorIssue.IsMatch(e.Name)).ThenBy(Function(e) e.Symbol.Length).ThenBy(Function(e) e.Symbol)
            Dim names = If(qs.Length >= 2 AndAlso Not q.StartsWith("^"c),
                           all.Where(Function(e) Not e.Symbol.StartsWith(q, StringComparison.OrdinalIgnoreCase) AndAlso Squash(e.Name).Contains(qs, StringComparison.Ordinal)).
                               OrderBy(Function(e) MinorIssue.IsMatch(e.Name)).ThenBy(Function(e) e.Etf).ThenBy(Function(e) Not Squash(e.Name).StartsWith(qs, StringComparison.Ordinal)).ThenBy(Function(e) e.Name.Length),
                           Enumerable.Empty(Of (Symbol As String, Name As String, Exchange As String, Etf As Boolean))())
            Return exact.Concat(starts).Concat(names).Take(Math.Clamp(limit, 1, 50)).ToList()
        End Function

        ' ------------------------------------------------------------------ the stock_quotes tool

        Public Const ToolName As String = "stock_quotes"
        Public Const ToolDescription As String =
            "Stock and index quotes from Yahoo Finance (delayed about 15 minutes for most US exchanges): price, change and percent since the previous close, " &
            "day and 52-week range, volume, whether the market is open, and the change over 5 sessions and a month. Any ticker (AAPL, MSFT, JPM, ^GSPC for the " &
            "S&P 500, ^IXIC Nasdaq, ^DJI Dow); leave symbols out for the watched ones (in a chat, the user's own tickers). Add history (5d, 1mo, 6mo, 1y, 5y) for a short price series. " &
            "Quote the time of the price you use; this is not investment advice."
        Public Const ToolSchema As String =
            "{""type"":""object"",""properties"":{""symbols"":{""type"":""array"",""items"":{""type"":""string""},""maxItems"":10,""description"":""Tickers; empty: the watched ones""}," &
            """history"":{""type"":""string"",""enum"":[""5d"",""1mo"",""6mo"",""1y"",""5y""],""description"":""Also a price series over this range""}}}"

        ''' <summary>
        ''' The stock_quotes tool's answer (agents and chats): recent quotes from memory, others read now. Without symbols: the chat
        ''' user's own tickers (username), or the defaults (agents).
        ''' </summary>
        Public Async Function ToolAnswerAsync(argsJson As String, ct As CancellationToken, Optional username As String = Nothing) As Task(Of String)
            Dim args As JsonObject = Nothing
            Try
                args = TryCast(JsonNode.Parse(If(String.IsNullOrWhiteSpace(argsJson), "{}", argsJson)), JsonObject)
            Catch ex As JsonException
            End Try
            Dim wanted = Clean(If(TryCast(args?("symbols"), JsonArray), New JsonArray()).Select(Function(x) x?.ToString())).Take(10).ToList()
            Dim history = args?("history")?.ToString()
            Dim quotes As New List(Of StockQuote)
            Dim unknown As New List(Of String)
            For Each s In If(wanted.Count > 0, wanted, (Await WatchForAsync(username).ConfigureAwait(False)).Symbols)
                Dim q As StockQuote = Nothing
                If Not _quotes.TryGetValue(s, q) OrElse DateTime.UtcNow - q.UpdatedUtc > TimeSpan.FromMinutes(5) Then
                    Try
                        q = Await FetchQuoteAsync(s, ct).ConfigureAwait(False)
                    Catch ex As Exception When Not ExceptionUtil.IsCancellation(ex)
                        q = Nothing
                    End Try
                End If
                If q Is Nothing Then unknown.Add(s) Else quotes.Add(q)
            Next
            Dim items As New List(Of Object)
            For Each q In quotes
                Dim series As Object = Nothing
                If Not String.IsNullOrEmpty(history) Then
                    Dim c = Await ChartAsync(q.Symbol, history, ct).ConfigureAwait(False)
                    If c.HasValue Then
                        Dim pts = c.Value.Points
                        Dim stepN = Math.Max(1, CInt(Math.Ceiling(pts.Count / 24.0)))
                        series = pts.Where(Function(p, i) i Mod stepN = 0 OrElse i = pts.Count - 1).
                            Select(Function(p) New With {.utc = DateTimeOffset.FromUnixTimeMilliseconds(CLng(p(0))).UtcDateTime.ToString("yyyy-MM-dd HH:mm"), .price = Math.Round(p(1), 4)}).ToList()
                    End If
                End If
                items.Add(New With {.symbol = q.Symbol, .name = q.Name, .exchange = q.Exchange, .type = q.Type, .currency = q.Currency,
                                    .price = R2(q.Price), .previousClose = R2(q.PreviousClose), .change = R2(q.Change), .changePct = R2(q.ChangePct),
                                    .dayLow = R2(q.DayLow), .dayHigh = R2(q.DayHigh), .yearLow = R2(q.YearLow), .yearHigh = R2(q.YearHigh), .volume = q.Volume,
                                    .change5dPct = R2(q.Change5d), .change1mPct = R2(q.Change1m), .market = q.MarketState,
                                    .asOfUtc = q.MarketTimeUtc?.ToString("yyyy-MM-dd HH:mm") & If(q.MarketTimeUtc.HasValue, " UTC", ""), .history = series})
            Next
            Return JsonUtil.ToJson(New With {.source = "Yahoo Finance (prices may be delayed about 15 minutes)", .quotes = items,
                                             .unknown = If(unknown.Count > 0, "No quote for: " & String.Join(", ", unknown) & " (check the ticker; indexes start with ^, e.g. ^GSPC).", Nothing)})
        End Function

        Private Shared Function R2(v As Double?) As Double?
            Return If(v.HasValue, Math.Round(v.Value, If(Math.Abs(v.Value) < 1, 4, 2)), CType(Nothing, Double?))
        End Function

    End Class

End Namespace
