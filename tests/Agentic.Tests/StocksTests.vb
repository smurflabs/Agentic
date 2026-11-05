Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Net.Sockets
Imports System.Text
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.Configuration
Imports Agentic.Http
Imports Agentic.Persistence
Imports Agentic.Work
Imports Xunit

''' <summary>The Stocks tab: Yahoo Finance chart answers, NASDAQ Trader's ticker directory, the watch list, the API and the stock_quotes tool.</summary>
Public Class StocksTests

    ''' <summary>Answers by URL: the first key the URL contains wins; anything else is 404.</summary>
    Private NotInheritable Class UrlHandler
        Inherits HttpMessageHandler
        Public ReadOnly Answers As New Dictionary(Of String, String)
        Public ReadOnly Asked As New List(Of String)
        Protected Overrides Function SendAsync(request As HttpRequestMessage, ct As CancellationToken) As Task(Of HttpResponseMessage)
            Dim url = request.RequestUri.AbsoluteUri
            SyncLock Asked
                Asked.Add(url)
            End SyncLock
            For Each kv In Answers
                If url.Contains(kv.Key) Then Return Task.FromResult(New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent(kv.Value, Encoding.UTF8, "application/json")})
            Next
            Return Task.FromResult(New HttpResponseMessage(HttpStatusCode.NotFound) With {.Content = New StringContent(UnknownTicker, Encoding.UTF8, "application/json")})
        End Function
    End Class

    ''' <summary>Yahoo and NASDAQ Trader for a whole host: answers GETs by a key the request line contains (404 otherwise).</summary>
    Private NotInheritable Class FakeSources
        Implements IDisposable
        Private ReadOnly _listener As TcpListener
        Private ReadOnly _cts As New CancellationTokenSource()
        Public ReadOnly Answers As New Dictionary(Of String, String)
        Public ReadOnly Property Port As Integer

        Public Sub New()
            _listener = New TcpListener(IPAddress.Loopback, 0)
            _listener.Start()
            Port = CType(_listener.LocalEndpoint, IPEndPoint).Port
            Dim ignored = Task.Run(AddressOf AcceptLoopAsync)
        End Sub

        Private Async Function AcceptLoopAsync() As Task
            While Not _cts.IsCancellationRequested
                Dim client As TcpClient
                Try
                    client = Await _listener.AcceptTcpClientAsync(_cts.Token)
                Catch
                    Return
                End Try
                Dim ignored = Task.Run(Function() ServeAsync(client))
            End While
        End Function

        Private Async Function ServeAsync(client As TcpClient) As Task
            Using client
                Dim stream = client.GetStream()
                Dim reader As New StreamReader(stream, Encoding.ASCII, False, 1024, leaveOpen:=True)
                Dim first = Await reader.ReadLineAsync()
                While Not String.IsNullOrEmpty(Await reader.ReadLineAsync())
                End While
                Dim answer = Answers.Where(Function(kv) If(first, "").Contains(kv.Key)).Select(Function(kv) kv.Value).FirstOrDefault()
                Dim body = Encoding.UTF8.GetBytes(If(answer, UnknownTicker))
                Dim status = If(answer Is Nothing, "404 Not Found", "200 OK")
                Dim head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}{vbCrLf}Content-Type: application/json{vbCrLf}Content-Length: {body.Length}{vbCrLf}Connection: close{vbCrLf}{vbCrLf}")
                Await stream.WriteAsync(head)
                Await stream.WriteAsync(body)
                Await stream.FlushAsync()
            End Using
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            _cts.Cancel()
            _listener.Stop()
        End Sub
    End Class

    Private Const UnknownTicker As String = "{""chart"":{""result"":null,""error"":{""code"":""Not Found"",""description"":""No data found, symbol may be delisted""}}}"

    Private Const NasdaqListed As String =
        "Symbol|Security Name|Market Category|Test Issue|Financial Status|Round Lot Size|ETF|NextShares" & vbCrLf &
        "AAPL|Apple Inc. - Common Stock|Q|N|N|100|N|N" & vbCrLf &
        "AAPB|GraniteShares 2x Long AAPL Daily ETF|G|N|N|100|Y|N" & vbCrLf &
        "ZXZZT|NASDAQ TEST STOCK|G|Y|N|100|N|N" & vbCrLf &
        "QQQ|Invesco QQQ Trust, Series 1|G|N|N|100|Y|N" & vbCrLf &
        "File Creation Time: 1102202617:01|||||||"

    Private Const OtherListed As String =
        "ACT Symbol|Security Name|Exchange|CQS Symbol|ETF|Round Lot Size|Test Issue|NASDAQ Symbol" & vbLf &
        "JPM|JP Morgan Chase & Co. Common Stock|N|JPM|N|40|N|JPM" & vbLf &
        "JPM$J|J P Morgan Chase & Co Depositary Shares, each representing a 1/400th interest in a share of JPMorgan Chase & Co. 4.75% Non-Cumulative Preferred Stock, Series GG|N|JPM$J|N|100|N|JPM-J" & vbLf &
        "BRK.B|Berkshire Hathaway Inc. New Common Stock|N|BRK.B|N|40|N|BRK.B" & vbLf &
        "AAC.W|Ares Acquisition Corporation III Redeemable warrants|N|AAC.W|N|100|N|AACW" & vbLf &
        "SPY|SPDR S&P 500 ETF Trust|P|SPY|Y|100|N|SPY" & vbLf &
        "AAPLX|Pineapple Holdings Class A Common Stock|A|AAPLX|N|100|N|AAPLX" & vbLf &
        "JPIE|JPMorgan Income ETF|P|JPIE|Y|100|N|JPIE" & vbLf &
        "File Creation Time: 1102202617:01|||||||"

    Private Shared Function Unix(t As DateTime) As Long
        Return New DateTimeOffset(t).ToUnixTimeSeconds()
    End Function

    ''' <summary>A Yahoo chart answer: meta (as JSON members) plus timestamps and closes (Nothing for a gap).</summary>
    Private Shared Function Chart(meta As String, times As IEnumerable(Of Long), closes As IEnumerable(Of Double?)) As String
        Dim c = String.Join(",", closes.Select(Function(v) If(v.HasValue, v.Value.ToString(Globalization.CultureInfo.InvariantCulture), "null")))
        Return "{""chart"":{""result"":[{""meta"":{" & meta & "},""timestamp"":[" & String.Join(",", times) & "],""indicators"":{""quote"":[{""close"":[" & c & "]}]}}],""error"":null}}"
    End Function

    ''' <summary>A day for AAPL around now: the regular session began an hour ago; three 5-minute prices, one missing.</summary>
    Private Shared Function AaplDay(now As DateTime) As String
        Dim open = now.AddHours(-1)
        Dim meta = $"""currency"":""USD"",""symbol"":""AAPL"",""exchangeName"":""NMS"",""fullExchangeName"":""NasdaqGS"",""instrumentType"":""EQUITY"",""regularMarketTime"":{Unix(now.AddMinutes(-2))}," &
                   """regularMarketPrice"":270.5,""fiftyTwoWeekHigh"":288.6,""fiftyTwoWeekLow"":169.2,""regularMarketDayHigh"":271.9,""regularMarketDayLow"":266.1,""regularMarketVolume"":41234567," &
                   """longName"":""Apple Inc."",""shortName"":""Apple Inc."",""chartPreviousClose"":268.0,""previousClose"":268.0,""range"":""1d""," &
                   $"""currentTradingPeriod"":{{""pre"":{{""start"":{Unix(open.AddHours(-5.5))},""end"":{Unix(open)}}},""regular"":{{""start"":{Unix(open)},""end"":{Unix(open.AddHours(6.5))}}},""post"":{{""start"":{Unix(open.AddHours(6.5))},""end"":{Unix(open.AddHours(10.5))}}}}}"
        Return Chart(meta, {Unix(open), Unix(open.AddMinutes(5)), Unix(open.AddMinutes(10))}, {CType(267.1, Double?), Nothing, 270.5})
    End Function

    ''' <summary>A month of AAPL's daily closes: 250 a month ago, 259 five sessions back, 270.5 today.</summary>
    Private Shared Function AaplMonth(now As DateTime) As String
        Dim closes = {250.0, 255, 256, 257, 258, 259, 260, 262, 263, 265, 270.5}
        Dim meta = """currency"":""USD"",""symbol"":""AAPL"",""regularMarketPrice"":270.5,""chartPreviousClose"":248.0,""range"":""1mo"""
        Return Chart(meta, closes.Select(Function(c, i) Unix(now.Date.AddDays(i - closes.Length + 1))), closes.Select(Function(c) CType(c, Double?)))
    End Function

    ''' <summary>A year of AAPL by weeks (60 points) for history.</summary>
    Private Shared Function AaplYear(now As DateTime) As String
        Dim meta = """currency"":""USD"",""symbol"":""AAPL"",""regularMarketPrice"":270.5,""chartPreviousClose"":180.0,""range"":""1y"""
        Return Chart(meta, Enumerable.Range(0, 60).Select(Function(i) Unix(now.Date.AddDays(i * 6 - 360))), Enumerable.Range(0, 60).Select(Function(i) CType(200.0 + i, Double?)))
    End Function

    Private Shared Function IndexDay(now As DateTime) As String
        Dim meta = $"""currency"":""USD"",""symbol"":""^GSPC"",""fullExchangeName"":""SNP"",""instrumentType"":""INDEX"",""regularMarketPrice"":6840.2,""previousClose"":6800.0,""range"":""1d"",""regularMarketTime"":{Unix(now)}," &
                   """shortName"":""S&P 500"""
        Return Chart(meta, {Unix(now.AddMinutes(-5)), Unix(now)}, {CType(6820.0, Double?), 6840.2})
    End Function

    Private Shared Async Function NewDbAsync() As Task(Of (Db As Database, Dir As String))
        Dim dir = Path.Combine(Path.GetTempPath(), "stk-" & Guid.NewGuid().ToString("N").Substring(0, 8))
        Directory.CreateDirectory(dir)
        Dim db As New Database(Path.Combine(dir, "agentic.db"), 2)
        Await New SchemaMigrator(db).MigrateAsync()
        Return (db, dir)
    End Function

    <Fact>
    Public Sub QuotesAreReadFromYahooCharts()
        Dim now = New DateTime(2026, 11, 2, 16, 0, 0, DateTimeKind.Utc)
        Dim q = StocksService.ParseQuote(CType(JsonNode.Parse(AaplDay(now))("chart")("result")(0), JsonObject), now)
        Assert.Equal("AAPL", q.Symbol)
        Assert.Equal("Apple Inc.", q.Name)
        Assert.Equal("NasdaqGS", q.Exchange)
        Assert.Equal("EQUITY", q.Type)
        Assert.Equal(270.5, q.Price.Value)
        Assert.Equal(268.0, q.PreviousClose.Value)
        Assert.Equal(2.5, q.Change.Value, 6)
        Assert.Equal(2.5 / 268 * 100, q.ChangePct.Value, 6)
        Assert.Equal(41234567L, q.Volume.Value)
        Assert.Equal(now.AddMinutes(-2), q.MarketTimeUtc.Value)
        Assert.Equal("open", q.MarketState)
        ' The gap (a null close) is left out; the day opened at the first price.
        Assert.Equal(2, q.Intraday.Count)
        Assert.Equal(267.1, q.Open.Value)
        Assert.Equal(Unix(now.AddHours(-1)) * 1000.0, q.Intraday(0)(0))
        ' Before the session, after it, and on a weekend.
        Assert.Equal("pre", StocksService.ParseQuote(CType(JsonNode.Parse(AaplDay(now))("chart")("result")(0), JsonObject), now.AddHours(-2)).MarketState)
        Assert.Equal("post", StocksService.ParseQuote(CType(JsonNode.Parse(AaplDay(now))("chart")("result")(0), JsonObject), now.AddHours(6)).MarketState)
        Assert.Equal("closed", StocksService.ParseQuote(CType(JsonNode.Parse(AaplDay(now))("chart")("result")(0), JsonObject), now.AddDays(1)).MarketState)
        ' A year's chart: its chartPreviousClose is a year old and its points are not today's.
        Dim y = StocksService.ParseQuote(CType(JsonNode.Parse(AaplYear(now))("chart")("result")(0), JsonObject), now)
        Assert.Null(y.PreviousClose)
        Assert.Null(y.Change)
        Assert.Null(y.Intraday)
        Assert.Equal(60, StocksService.Series(CType(JsonNode.Parse(AaplYear(now))("chart")("result")(0), JsonObject)).Count)
        Assert.Null(StocksService.ParseQuote(New JsonObject(), now))

        Assert.Equal(("1d", "5m"), (StocksService.RangeOf(Nothing).Range, StocksService.RangeOf(Nothing).Interval))
        Assert.Equal(("1mo", "60m"), (StocksService.RangeOf("1M").Range, StocksService.RangeOf("1M").Interval))
        Assert.Equal(("5y", "1wk"), (StocksService.RangeOf("5y").Range, StocksService.RangeOf("5y").Interval))
        Assert.Equal(("max", "1mo"), (StocksService.RangeOf("max").Range, StocksService.RangeOf("max").Interval))
        Assert.True(StocksService.IsTicker("aapl"))
        Assert.True(StocksService.IsTicker("BRK-B"))
        Assert.True(StocksService.IsTicker("^GSPC"))
        Assert.True(StocksService.IsTicker("EURUSD=X"))
        Assert.False(StocksService.IsTicker("not valid!"))
        Assert.False(StocksService.IsTicker(""))
        Assert.False(StocksService.IsTicker("../etc"))
    End Sub

    <Fact>
    Public Async Function TheTickerDirectoryIsReadAndSearched() As Task
        Dim nasdaq = StocksService.ParseDirectory(NasdaqListed)
        Assert.Equal({"AAPL", "AAPB", "QQQ"}, nasdaq.Select(Function(e) e.Symbol).ToArray()) ' the test issue and the footer are left out
        Assert.Equal("Apple Inc.", nasdaq(0).Name)
        Assert.Equal("NASDAQ", nasdaq(0).Exchange)
        Assert.True(nasdaq(1).Etf)
        Dim other = StocksService.ParseDirectory(OtherListed)
        ' Symbols as Yahoo writes them; names without the kind of share.
        Assert.Equal({"JPM", "JPM-PJ", "BRK-B", "AAC-WT", "SPY", "AAPLX", "JPIE"}, other.Select(Function(e) e.Symbol).ToArray())
        Assert.Equal("JP Morgan Chase & Co.", other(0).Name)
        Assert.Equal("J P Morgan Chase & Co Depositary Shares", other(1).Name)
        Assert.Equal("Berkshire Hathaway Inc.", other(2).Name)
        Assert.Equal("Pineapple Holdings", other(5).Name)
        Assert.Equal("NYSE", other(0).Exchange)
        Assert.Equal("NYSE Arca", other(4).Exchange)
        Assert.True(other(4).Etf)
        Assert.Equal("XYZ-WT", StocksService.YahooSymbol("XYZ.WS"))
        Assert.Equal("AAPL", StocksService.YahooSymbol("AAPL"))
        Assert.Empty(StocksService.ParseDirectory(""))
        Assert.Empty(StocksService.ParseDirectory("Nothing|Here" & vbLf & "a|b"))

        Dim t = Await NewDbAsync()
        Try
            Dim h As New UrlHandler()
            h.Answers("nasdaqlisted") = NasdaqListed
            h.Answers("otherlisted") = OtherListed
            Dim s As New StocksService(t.Db, New StocksOptions With {.ChartUrl = "https://yahoo.test/v8/finance/chart", .NasdaqListedUrl = "https://nasdaq.test/nasdaqlisted.txt",
                                                                     .OtherListedUrl = "https://nasdaq.test/otherlisted.txt"}, Nothing, h)
            Await s.LoadDirectoryAsync(CancellationToken.None)
            Assert.Null(s.DirectoryError)
            Assert.Equal(10, s.DirectorySize)
            ' The exact symbol, then symbols starting with it (shortest first), then names containing it (companies before ETFs).
            Assert.Equal({"AAPB", "AAPL", "AAPLX"}, s.Search("aap", 10).Select(Function(e) e.Symbol).ToArray())
            Assert.Equal({"AAPL", "AAPLX", "AAPB"}, s.Search("AAPL", 10).Select(Function(e) e.Symbol).ToArray())
            Assert.Equal({"AAPL", "AAPLX"}, s.Search("apple", 10).Select(Function(e) e.Symbol).ToArray()) ' the name that starts with it first
            ' Spaces and punctuation do not count ("JP Morgan" is "jpmorgan"); shares, then ETFs, then preferreds.
            Assert.Equal({"JPM", "JPIE", "JPM-PJ"}, s.Search("jpmorgan", 10).Select(Function(e) e.Symbol).ToArray())
            Assert.Equal({"JPM", "JPM-PJ"}, s.Search("Chase", 10).Select(Function(e) e.Symbol).ToArray())
            Assert.Equal({"JPM", "JPM-PJ", "JPIE"}, s.Search("jpm", 10).Select(Function(e) e.Symbol).ToArray())
            Assert.Equal("BRK-B", s.Search("berkshire", 10).Single().Symbol)
            Assert.Empty(s.Search("^RUT", 10)) ' an index: no names
            Assert.Equal({"QQQ"}, s.Search("q", 10).Select(Function(e) e.Symbol).ToArray()) ' one letter: symbols only
            Assert.Single(s.Search("a", 1))
            Assert.Empty(s.Search("  ", 10))
            ' A directory that cannot be read keeps the one there was, and says so.
            h.Answers.Clear()
            Await s.LoadDirectoryAsync(CancellationToken.None)
            Assert.Equal(10, s.DirectorySize)
            Assert.Contains("nasdaq.test", s.DirectoryError)
        Finally
            Try : Directory.Delete(t.Dir, True) : Catch : End Try
        End Try
    End Function

    <Fact>
    Public Async Function QuotesTheWatchListAndTheTool() As Task
        Dim now = DateTime.UtcNow
        Dim t = Await NewDbAsync()
        Try
            Dim h As New UrlHandler()
            h.Answers("/AAPL?range=1d") = AaplDay(now)
            h.Answers("/AAPL?range=1mo") = AaplMonth(now)
            h.Answers("/AAPL?range=1y") = AaplYear(now)
            h.Answers("/%5EGSPC?range=1d") = IndexDay(now)
            Dim opt As New StocksOptions With {.ChartUrl = "https://yahoo.test/v8/finance/chart/"}
            opt.Symbols.AddRange({"^gspc", "aapl", "not valid!", "AAPL"})
            Dim s As New StocksService(t.Db, opt, Nothing, h)
            Assert.Equal({"^GSPC", "AAPL"}, s.Defaults.ToArray())
            Assert.Equal({"^GSPC", "AAPL"}, s.PolledSymbols().ToArray())

            Await s.RefreshQuotesAsync(CancellationToken.None)
            Assert.Null(s.LastError)
            Assert.NotNull(s.UpdatedUtc)
            Assert.Contains(h.Asked, Function(u) u = "https://yahoo.test/v8/finance/chart/AAPL?range=1d&interval=5m")
            Dim latest = (Await s.LatestForAsync(Nothing, CancellationToken.None)).Quotes
            Dim idx = latest(0)
            Assert.Equal("^GSPC", idx.Symbol)
            Assert.Equal("INDEX", idx.Type)
            Assert.Equal(40.2, idx.Change.Value, 6)
            Assert.Null(idx.Change5d) ' its month could not be read
            Dim aapl = latest(1)
            Assert.Equal((270.5 - 259) / 259 * 100, aapl.Change5d.Value, 6)
            Assert.Equal((270.5 - 250) / 250 * 100, aapl.Change1m.Value, 6)
            ' The month is kept an hour: reading again asks only for the day.
            Dim monthAsks = h.Asked.Where(Function(u) u.Contains("AAPL?range=1mo")).Count()
            Await s.RefreshQuotesAsync(CancellationToken.None)
            Assert.Equal(monthAsks, h.Asked.Where(Function(u) u.Contains("AAPL?range=1mo")).Count())

            ' Charts: today against the previous close; longer ranges have none.
            Dim day = Await s.ChartAsync("aapl", "1d", CancellationToken.None)
            Assert.Equal(268.0, day.Value.PreviousClose.Value)
            Assert.Equal(2, day.Value.Points.Count)
            Dim year = Await s.ChartAsync("AAPL", "1y", CancellationToken.None)
            Assert.Null(year.Value.PreviousClose)
            Assert.Equal(60, year.Value.Points.Count)
            Dim asked = h.Asked.Count
            Await s.ChartAsync("AAPL", "1y", CancellationToken.None) ' kept a while
            Assert.Equal(asked, h.Asked.Count)
            Assert.False((Await s.ChartAsync("ZZZZ", "1d", CancellationToken.None)).HasValue)
            Assert.False((Await s.ChartAsync("bad ticker!", "1d", CancellationToken.None)).HasValue)

            ' Each account's own list: every ticker is checked first; unknown ones are named and nothing changes.
            Dim ex = Await Assert.ThrowsAsync(Of ArgumentException)(Function() s.SetWatchlistAsync("ann", {"aapl", "zzzz"}, CancellationToken.None))
            Assert.Contains("ZZZZ", ex.Message)
            Assert.False((Await s.WatchForAsync("ann")).Custom)
            Await Assert.ThrowsAsync(Of ArgumentException)(Function() s.SetWatchlistAsync("ann", {"!!"}, CancellationToken.None))
            Await Assert.ThrowsAsync(Of ArgumentException)(Function() s.SetWatchlistAsync("", {"AAPL"}, CancellationToken.None))
            Assert.Equal({"AAPL"}, (Await s.SetWatchlistAsync("ann", {"aapl"}, CancellationToken.None)).ToArray())
            Dim annLatest = Await s.LatestForAsync("ann", CancellationToken.None)
            Assert.True(annLatest.Custom)
            Assert.Equal({"AAPL"}, annLatest.Quotes.Select(Function(q) q.Symbol).ToArray())
            ' Others keep the defaults; the quotes they need stay.
            Dim bob = Await s.LatestForAsync("bob", CancellationToken.None)
            Assert.False(bob.Custom)
            Assert.Equal({"^GSPC", "AAPL"}, bob.Quotes.Select(Function(q) q.Symbol).ToArray())
            Assert.Equal(6840.2, bob.Quotes(0).Price.Value)
            ' Kept with the account (a new server reads it back).
            Dim again As New StocksService(t.Db, opt, Nothing, h)
            Assert.Equal({"AAPL"}, (Await again.WatchForAsync("ann")).Symbols.ToArray())
            Assert.Equal("[""AAPL""]", CStr(Await t.Db.ScalarAsync("SELECT Value FROM UserPreferences WHERE Username='ann' AND Key='stocks.watchlist'")))
            ' Saving exactly the defaults, or Use the defaults, removes the account's own list.
            Await s.SetWatchlistAsync("bob", {"^GSPC", "AAPL"}, CancellationToken.None)
            Assert.Null(Await t.Db.ScalarAsync("SELECT Value FROM UserPreferences WHERE Username='bob' AND Key='stocks.watchlist'"))
            Await s.ResetWatchlistAsync("ann")
            Assert.False((Await s.WatchForAsync("ann")).Custom)
            Await s.SetWatchlistAsync("ann", {"AAPL"}, CancellationToken.None)

            ' What the server reads: the defaults, and own tickers of accounts that looked lately.
            h.Answers("/MSFT?range=1d") = AaplDay(now).Replace("""AAPL""", """MSFT""")
            Await s.SetWatchlistAsync("cy", {"msft"}, CancellationToken.None)
            Assert.Equal({"^GSPC", "AAPL", "MSFT"}, s.PolledSymbols().ToArray())

            ' The tool: any ticker (read now when it is not watched), unknown ones named, and a short history.
            Dim answer = JsonNode.Parse(Await s.ToolAnswerAsync("{""symbols"":[""^gspc"",""zzzz""],""history"":""1y""}", CancellationToken.None))
            Dim quotes = answer("quotes").AsArray()
            Assert.Single(quotes)
            Assert.Equal("^GSPC", quotes(0)("symbol").ToString())
            Assert.Equal(6840.2, quotes(0)("price").GetValue(Of Double)(), 2)
            Assert.Contains("ZZZZ", answer("unknown").ToString())
            ' Without tickers: the chat user's own list, or the defaults (agents).
            Assert.Equal({"^GSPC", "AAPL"}, JsonNode.Parse(Await s.ToolAnswerAsync("{}", CancellationToken.None))("quotes").AsArray().Select(Function(q) q("symbol").ToString()).ToArray())
            Assert.Equal({"MSFT"}, JsonNode.Parse(Await s.ToolAnswerAsync("{}", CancellationToken.None, "cy"))("quotes").AsArray().Select(Function(q) q("symbol").ToString()).ToArray())
            Dim watched = JsonNode.Parse(Await s.ToolAnswerAsync("{""history"":""1y""}", CancellationToken.None, "ann"))("quotes").AsArray().Single()
            Assert.Equal("AAPL", watched("symbol").ToString())
            Assert.Equal("open", watched("market").ToString())
            Assert.Equal(4.44, watched("change5dPct").GetValue(Of Double)(), 2)
            Dim series = watched("history").AsArray()
            Assert.InRange(series.Count, 20, 25)
            Assert.Equal(259.0, series.Last()("price").GetValue(Of Double)())
            Assert.Contains("Yahoo", JsonNode.Parse(Await s.ToolAnswerAsync("not json", CancellationToken.None))("source").ToString())
        Finally
            Try : Directory.Delete(t.Dir, True) : Catch : End Try
        End Try
    End Function

    ''' <summary>The API (every account looks and keeps its own list), every agent's tool, and a chat with Stocks on.</summary>
    <Fact>
    Public Async Function TheTabAgentsAndChatsShareTheQuotes() As Task
        Assert.Equal(Permissions.Self, Permissions.ForRoute("PUT", "/api/stocks/watchlist"))
        Assert.Equal(Permissions.Self, Permissions.ForRoute("DELETE", "/api/stocks/watchlist"))
        Assert.Equal(Permissions.AdminOnly, Permissions.ForRoute("PUT", "/api/markets/defaults"))
        Assert.Equal(Permissions.Self, Permissions.ForRoute("PUT", "/api/markets/watchlist"))
        Assert.Equal(Permissions.Self, Permissions.ForRoute("DELETE", "/api/markets/watchlist"))
        Assert.Equal(Permissions.Self, Permissions.ForRoute("GET", "/api/stocks"))
        Assert.Equal(Permissions.Self, Permissions.ForRoute("GET", "/api/stocks/chart"))
        Assert.Equal(Permissions.Self, Permissions.ForRoute("POST", "/api/stocks/refresh"))
        Dim now = DateTime.UtcNow
        Using sources As New FakeSources()
            sources.Answers("/AAPL?range=1d") = AaplDay(now)
            sources.Answers("/AAPL?range=1mo") = AaplMonth(now)
            sources.Answers("/AAPL?range=1y") = AaplYear(now)
            sources.Answers("/MSFT?range=1d") = AaplDay(now).Replace("""AAPL""", """MSFT""").Replace("Apple Inc.", "Microsoft Corporation")
            sources.Answers("nasdaqlisted") = NasdaqListed
            sources.Answers("otherlisted") = OtherListed
            Using up As New FakeUpstream()
                Dim asked = 0
                up.StreamOverride = Function(req)
                                        If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                        Select Case Interlocked.Increment(asked)
                                            Case 1 : Return ChatFeatureTests.ToolCallStream("stock_quotes", "{}")
                                            Case Else : Return ChatFeatureTests.TextStream("Apple is at $270.50, up 0.93% today.", "stop")
                                        End Select
                                    End Function
                Dim base = $"http://127.0.0.1:{sources.Port}"
                Dim env = Await PlatformApiTests.StartAsync(up, Sub(o)
                                                                    o.Stocks.ChartUrl = base & "/v8/finance/chart"
                                                                    o.Stocks.NasdaqListedUrl = base & "/nasdaqlisted.txt"
                                                                    o.Stocks.OtherListedUrl = base & "/otherlisted.txt"
                                                                    o.Stocks.Symbols.Add("AAPL")
                                                                End Sub)
                Using env.Host
                    Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                    Dim info As JsonNode = Nothing
                    Assert.True(Await TestHost.WaitUntilAsync(Async Function()
                                                                  info = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/stocks"))
                                                                  Return info("quotes")(0)("price") IsNot Nothing AndAlso info("status")("tickers").GetValue(Of Integer)() > 0
                                                              End Function, 30))
                    Dim aapl = info("quotes").AsArray().Single()
                    Assert.Equal("AAPL", aapl("symbol").ToString())
                    Assert.Equal(270.5, aapl("price").GetValue(Of Double)())
                    Assert.Equal("open", aapl("marketState").ToString())
                    Assert.Equal(2, aapl("intraday").AsArray().Count)
                    Assert.Null(aapl("regularStartUtc"))
                    Assert.False(info("custom").GetValue(Of Boolean)())
                    Assert.Equal({"AAPL"}, info("defaults").AsArray().Select(Function(x) x.ToString()).ToArray())
                    Assert.Equal(10, info("status")("tickers").GetValue(Of Integer)())

                    Dim day = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/stocks/chart?symbol=aapl&range=1d"))
                    Assert.Equal(268.0, day("previousClose").GetValue(Of Double)())
                    Assert.Equal(2, day("points").AsArray().Count)
                    Dim year = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/stocks/chart?symbol=AAPL&range=1y"))
                    Assert.Null(year("previousClose"))
                    Assert.Equal(60, year("points").AsArray().Count)
                    Assert.Equal(HttpStatusCode.NotFound, (Await admin.Client.GetAsync("/api/stocks/chart?symbol=ZZZZ")).StatusCode)
                    Assert.Equal(HttpStatusCode.BadRequest, (Await admin.Client.GetAsync("/api/stocks/quote?symbol=..%2Fx")).StatusCode)
                    Dim msft = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/stocks/quote?symbol=msft"))
                    Assert.Equal("Microsoft Corporation", msft("name").ToString())
                    Dim found = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/stocks/search?q=apple"))
                    Assert.Equal("AAPL", found(0)("symbol").ToString())
                    Assert.Equal("NASDAQ", found(0)("exchange").ToString())

                    Dim bad = Await PlatformApiTests.Send(admin.Client, "PUT", "/api/stocks/watchlist", New With {.symbols = {"AAPL", "ZZZZ"}}, admin.Csrf)
                    Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode)
                    Assert.Contains("ZZZZ", Await bad.Content.ReadAsStringAsync())
                    Dim saved = Await PlatformApiTests.Send(admin.Client, "PUT", "/api/stocks/watchlist", New With {.symbols = {"msft", "AAPL"}}, admin.Csrf)
                    Assert.Equal(HttpStatusCode.OK, saved.StatusCode)
                    Dim savedInfo = Await ChatFeatureTests.JsonOf(saved)
                    Assert.Equal({"MSFT", "AAPL"}, savedInfo("watchlist").AsArray().Select(Function(x) x.ToString()).ToArray())
                    Assert.True(savedInfo("custom").GetValue(Of Boolean)())
                    Assert.Equal("Microsoft Corporation", savedInfo("quotes")(0)("name").ToString())

                    ' Another account (not an administrator) still has the defaults, and can keep its own list.
                    Assert.Equal(HttpStatusCode.Created, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/users", New With {.username = "trader", .password = "trader-pass-123", .role = "User"}, admin.Csrf)).StatusCode)
                    Dim trader = Await PlatformApiTests.LoginAsync(env.Port, "trader", "trader-pass-123")
                    Dim theirs = Await ChatFeatureTests.JsonOf(Await trader.Client.GetAsync("/api/stocks"))
                    Assert.Equal({"AAPL"}, theirs("watchlist").AsArray().Select(Function(x) x.ToString()).ToArray())
                    Dim own = Await PlatformApiTests.Send(trader.Client, "PUT", "/api/stocks/watchlist", New With {.symbols = {"MSFT"}}, trader.Csrf)
                    Assert.Equal(HttpStatusCode.OK, own.StatusCode)
                    Assert.Equal({"MSFT"}, (Await ChatFeatureTests.JsonOf(own))("watchlist").AsArray().Select(Function(x) x.ToString()).ToArray())
                    Dim back = Await PlatformApiTests.Send(trader.Client, "DELETE", "/api/stocks/watchlist", Nothing, trader.Csrf)
                    Assert.Equal(HttpStatusCode.OK, back.StatusCode)
                    Assert.False((Await ChatFeatureTests.JsonOf(back))("custom").GetValue(Of Boolean)())
                    Assert.Equal({"MSFT", "AAPL"}, (Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/stocks")))("watchlist").AsArray().Select(Function(x) x.ToString()).ToArray())

                    Dim meta = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/chat/meta"))
                    Assert.True(meta("stocks").GetValue(Of Boolean)())
                    Assert.All(env.Host.H.Profiles.AllProfiles(), Sub(p) Assert.True(p.HasInternalTool("stock_quotes")))

                    ' A chat with Stocks on: the model is offered the tool and, naming no tickers, reads the user's own.
                    Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                    Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "How is Apple doing today?", .model = "fast/a", .settings = New With {.stocks = True}})
                    Dim requests = up.Requests.Where(Function(r) ChatFeatureTests.Streamed(r)).ToList()
                    Assert.Contains("stock_quotes", requests(0)("tools").AsArray().Select(Function(x) x("function")("name").ToString()))
                    Dim result = requests(1)("messages").AsArray().Last(Function(m) m("role").ToString() = "tool")("content").ToString()
                    Assert.Contains("""symbol"":""MSFT""", result)
                    Assert.Contains("""symbol"":""AAPL""", result)
                    Assert.Contains("""price"":270.5", result)
                End Using
            End Using
        End Using
    End Function

End Class
