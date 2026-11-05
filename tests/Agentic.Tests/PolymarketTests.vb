Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Net.Sockets
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.Configuration
Imports Agentic.Http
Imports Agentic.Utilities
Imports Agentic.Work
Imports Xunit

''' <summary>The Polymarket tab: Gamma's events and markets, the CLOB, the Data API, the live channel's messages, the API and the polymarket tool.</summary>
Public Class PolymarketTests

    ''' <summary>Answers by URL: the first key the URL contains wins (in the order added); anything else is 404.</summary>
    Private NotInheritable Class UrlHandler
        Inherits HttpMessageHandler
        Public ReadOnly Answers As New List(Of KeyValuePair(Of String, String))
        Public ReadOnly Asked As New List(Of String)
        Public Sub Add(key As String, body As String)
            Answers.Add(New KeyValuePair(Of String, String)(key, body))
        End Sub
        Protected Overrides Function SendAsync(request As HttpRequestMessage, ct As CancellationToken) As Task(Of HttpResponseMessage)
            Dim url = request.Method.Method & " " & request.RequestUri.AbsoluteUri
            SyncLock Asked
                Asked.Add(url)
            End SyncLock
            For Each kv In Answers
                If url.Contains(kv.Key) Then Return Task.FromResult(New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent(kv.Value, Encoding.UTF8, "application/json")})
            Next
            Return Task.FromResult(New HttpResponseMessage(HttpStatusCode.NotFound) With {.Content = New StringContent("{""error"":""not found""}", Encoding.UTF8, "application/json")})
        End Function
    End Class

    ''' <summary>Polymarket's three APIs for a whole host on one port: answers GETs by a key the request line contains (404 otherwise).</summary>
    Private NotInheritable Class FakePolymarket
        Implements IDisposable
        Private ReadOnly _listener As TcpListener
        Private ReadOnly _cts As New CancellationTokenSource()
        Public ReadOnly Answers As New List(Of KeyValuePair(Of String, String))
        Public ReadOnly Property Port As Integer

        Public Sub New()
            _listener = New TcpListener(IPAddress.Loopback, 0)
            _listener.Start()
            Port = CType(_listener.LocalEndpoint, IPEndPoint).Port
            Dim ignored = Task.Run(AddressOf AcceptLoopAsync)
        End Sub

        Public Sub Add(key As String, body As String)
            SyncLock Answers
                Answers.Add(New KeyValuePair(Of String, String)(key, body))
            End SyncLock
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
                Dim answer As String = Nothing
                SyncLock Answers
                    answer = Answers.Where(Function(kv) If(first, "").Contains(kv.Key)).Select(Function(kv) kv.Value).FirstOrDefault()
                End SyncLock
                Dim body = Encoding.UTF8.GetBytes(If(answer, "{""error"":""not found""}"))
                Dim head = Encoding.ASCII.GetBytes($"HTTP/1.1 {If(answer Is Nothing, "404 Not Found", "200 OK")}{vbCrLf}Content-Type: application/json{vbCrLf}Content-Length: {body.Length}{vbCrLf}Connection: close{vbCrLf}{vbCrLf}")
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

    ''' <summary>JSON written with ' for ": the fixtures stay readable.</summary>
    Private Shared Function J(text As String) As String
        Return text.Replace("'", """")
    End Function

    Private Shared ReadOnly Cond1 As String = "0x" & New String("1"c, 64)
    Private Shared ReadOnly Cond2 As String = "0x" & New String("2"c, 64)
    Private Shared ReadOnly Wallet As String = "0x" & New String("a"c, 40)

    ''' <summary>A Yes/No market as Gamma writes it (outcomes, prices and tokens are JSON strings: \' becomes \").</summary>
    Private Shared Function Market(id As String, name As String, cond As String, yes As Double, tokenYes As String, tokenNo As String,
                                   Optional closed As Boolean = False, Optional resolution As String = Nothing, Optional events As String = Nothing) As String
        Dim inv = Globalization.CultureInfo.InvariantCulture
        Return J($"{{'id':'{id}','question':'Will the Fed cut {name}?','conditionId':'{cond}','slug':'fed-{id}','groupItemTitle':'{name}'," &
                 $"'outcomes':'[\'Yes\', \'No\']','outcomePrices':'[\'{yes.ToString(inv)}\', \'{(1 - yes).ToString(inv)}\']','clobTokenIds':'[\'{tokenYes}\', \'{tokenNo}\']'," &
                 $"'volume':'90000','volume24hr':5000,'liquidity':'8000','bestBid':0.61,'bestAsk':0.63,'lastTradePrice':0.62,'oneDayPriceChange':0.03," &
                 $"'endDate':'2026-12-10T00:00:00Z','createdAt':'2026-09-01T00:00:00Z','active':true,'closed':{closed.ToString().ToLowerInvariant()}" &
                 If(resolution Is Nothing, "", $",'umaResolutionStatus':'{resolution}'") & If(events Is Nothing, "", ",'events':" & events) & "}")
    End Function

    ''' <summary>An event with five markets: two open, one closed, one resolved, one settled at 100%.</summary>
    Private Shared Function FedEvent() As String
        Dim markets = String.Join(",", {
            Market("1", "25 bps", Cond1, 0.62, "111111", "111112"),
            Market("2", "50 bps", Cond2, 0.08, "222221", "222222"),
            Market("3", "Hold", "0x" & New String("3"c, 64), 0.2, "333331", "333332", closed:=True),
            Market("4", "Hike", "0x" & New String("4"c, 64), 0.1, "444441", "444442", resolution:="resolved"),
            Market("5", "75 bps", "0x" & New String("5"c, 64), 1, "555551", "555552")})
        Return J("{'id':'100','slug':'fed-december','title':'Fed decision in December?','description':'Resolves to the Fed decision.','volume':'5000000','volume24hr':120000," &
                 "'liquidity':'300000','endDate':'2026-12-10T00:00:00Z','creationDate':'2026-09-01T00:00:00Z','active':true,'closed':false," &
                 "'image':'https://polymarket-upload.s3.us-east-2.amazonaws.com/fed.png','tags':[{'id':'1','label':'Fed Rates','slug':'fed-rates'},{'id':'120','label':'Finance','slug':'finance'}],'markets':[") &
               markets & "]}"
    End Function

    Private Shared Function CryptoEvent() As String
        Return J("{'id':'200','slug':'btc-100k','title':'Bitcoin above $100k?','volume':'100','volume24hr':10,'active':true,'closed':false,'tags':[{'label':'Crypto','slug':'crypto'}],'markets':[") &
               Market("9", "BTC", "0x" & New String("9"c, 64), 0.4, "999991", "999992") & "]}"
    End Function

    ''' <summary>A page of simple events (ids from start).</summary>
    Private Shared Function EventsPage(start As Integer, count As Integer) As String
        Return "[" & String.Join(",", Enumerable.Range(start, count).Select(Function(i) J($"{{'id':'{i}','slug':'e{i}','title':'Event {i}','active':true,'closed':false,'markets':[") &
                                                                                       Market(CStr(i), "x", Cond1, 0.5, CStr(700000 + i), CStr(800000 + i)) & "]}")) & "]"
    End Function

    <Fact>
    Public Sub EventsAndMarketsAreReadAndClosedOnesLeftOut()
        Dim s As New PolymarketService(New PolymarketOptions(), Nothing)
        Dim ev = s.ParseEvent(JsonNode.Parse(FedEvent()))
        Assert.Equal("100", ev.Id)
        Assert.Equal("Finance", ev.Category) ' the first of the tab's categories among its tags
        Assert.Equal({"Fed Rates", "Finance"}, ev.Tags.ToArray())
        Assert.Equal(5, ev.MarketCount)
        Assert.Equal({"25 bps", "50 bps"}, ev.Markets.Select(Function(m) m.GroupTitle).ToArray()) ' open ones, likeliest first
        Dim m1 = ev.Markets(0)
        Assert.Equal({"Yes", "No"}, m1.Outcomes.ToArray())
        Assert.Equal(0.62, m1.Prices(0), 6)
        Assert.Equal(0.38, m1.Prices(1), 6)
        Assert.Equal({"111111", "111112"}, m1.TokenIds.ToArray())
        Assert.Equal(Cond1, m1.ConditionId)
        Assert.Equal(5000.0, m1.Volume24h.Value)
        Assert.Equal(0.61, m1.BestBid.Value)
        Assert.Equal(New DateTime(2026, 12, 10, 0, 0, 0, DateTimeKind.Utc), m1.EndUtc.Value)
        Assert.Equal("https://polymarket.com/event/fed-december/fed-1", m1.Url)
        Assert.Equal("https://polymarket.com/event/fed-december", ev.Url)
        Assert.True(ev.HasImage)
        ' Closed ones too when asked.
        Assert.Equal(5, s.ParseEvent(JsonNode.Parse(FedEvent()), True).Markets.Count)
        Assert.Null(s.ParseEvent(JsonNode.Parse("{}")))

        Assert.Equal(("volume24hr", False), PolymarketService.OrderOf(Nothing))
        Assert.Equal(("endDate", True), PolymarketService.OrderOf("ending"))
        Assert.Equal(("1m", 180, "30d"), PolymarketService.HistoryRange("1m"))
        Assert.Equal(("1w", 60, "7d"), PolymarketService.HistoryRange("?"))
        Assert.True(PolymarketService.IsToken("111111"))
        Assert.False(PolymarketService.IsToken("12ab"))
        Assert.True(PolymarketService.IsCondition(Cond1))
        Assert.False(PolymarketService.IsCondition("0x12"))
        Assert.True(PolymarketService.IsWallet(Wallet))
        Assert.False(PolymarketService.IsWallet("0xnot"))
        ' The price shown: the midpoint, or the last trade when the spread is wider than 10¢.
        Assert.Equal(0.62, AdminApi.ShownPrice(0.61, 0.63, 0.5, Nothing).Value, 6)
        Assert.Equal(0.5, AdminApi.ShownPrice(0.2, 0.6, 0.5, Nothing).Value, 6)
        Assert.Equal(0.3, AdminApi.ShownPrice(Nothing, Nothing, Nothing, 0.3).Value, 6)
    End Sub

    <Fact>
    Public Async Function TheLiveChannelKeepsBooksPricesAndTrades() As Task
        Dim s As New PolymarketService(New PolymarketOptions(), Nothing)
        s.ApplyMessage(J("{'event_type':'book','asset_id':'111111','market':'" & Cond1 & "','bids':[{'price':'0.60','size':'100'},{'price':'0.61','size':'50'}]," &
                         "'asks':[{'price':'0.65','size':'10'},{'price':'0.63','size':'70'}],'last_trade_price':'0.973'}"))
        Dim l = s.LiveOf("111111")
        Assert.Equal(0.61, l.BestBid.Value)
        Assert.Equal(0.63, l.BestAsk.Value)
        Assert.Equal(0.62, l.Mid.Value, 6)
        Assert.Null(l.LastTrade) ' a book's last_trade_price is not taken
        Dim v1 = l.Version
        ' A level taken away, another added; the message's best bid and ask win.
        s.ApplyMessage(J("{'event_type':'price_change','market':'" & Cond1 & "','price_changes':[{'asset_id':'111111','price':'0.61','size':'0','side':'BUY','best_bid':'0.60','best_ask':'0.63'}," &
                         "{'asset_id':'111111','price':'0.62','size':'5','side':'SELL','best_bid':'0.60','best_ask':'0.62'}]}"))
        Assert.Equal(0.6, l.BestBid.Value)
        Assert.Equal(0.62, l.BestAsk.Value)
        Assert.True(l.Version > v1)
        s.ApplyMessage(J("{'event_type':'last_trade_price','asset_id':'111111','market':'" & Cond1 & "','price':'0.62','side':'BUY','size':'12','timestamp':'1793000000000'}"))
        Assert.Equal(0.62, l.LastTrade.Value)
        Assert.Equal("BUY", l.LastSide)
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1793000000000).UtcDateTime, l.LastTradeUtc.Value)
        s.ApplyMessage(J("[{'event_type':'book','asset_id':'111112','market':'" & Cond1 & "','bids':[{'price':'0.37','size':'9'}],'asks':[{'price':'0.39','size':'9'}]}]"))
        s.ApplyMessage(J("{'event_type':'best_bid_ask','asset_id':'111112','market':'" & Cond1 & "','best_bid':'0.36','best_ask':'0.38'}"))
        Assert.Equal(0.36, s.LiveOf("111112").BestBid.Value)
        s.ApplyMessage("PONG")
        s.ApplyMessage(J("{'event_type':'tick_size_change','asset_id':'111111'}"))
        Assert.ThrowsAny(Of JsonException)(Sub() s.ApplyMessage("not json"))

        ' The page's stream: what changed since a version.
        Dim all = s.LiveSince({"111111", "111112", "999"}, 0)
        Assert.Equal(2, all.Items.Count)
        Assert.Empty(s.LiveSince({"111111", "111112"}, all.Version).Items)
        s.ApplyMessage(J("{'event_type':'best_bid_ask','asset_id':'111112','best_bid':'0.35','best_ask':'0.38'}"))
        Assert.Equal("111112", s.LiveSince({"111111", "111112"}, all.Version).Items.Single().TokenId)

        ' New and resolved markets.
        s.ApplyMessage(J("{'event_type':'new_market','id':'7','question':'Dogecoin up or down?','market':'0x7','slug':'doge-up','outcomes':['Up','Down']}"))
        Dim fresh = JsonNode.Parse(JsonUtil.ToJson(s.NewMarkets()))
        Assert.Equal("Dogecoin up or down?", fresh(0)("question").ToString())
        Assert.Equal("https://polymarket.com/event/doge-up", fresh(0)("url").ToString())
        s.ApplyMessage(J("{'event_type':'market_resolved','market':'" & Cond1 & "','assets_ids':['111111','111112'],'winning_asset_id':'111111'}"))
        Assert.True(s.LiveOf("111111").Won.Value)
        Assert.False(s.LiveOf("111112").Won.Value)

        ' The order book from the live state: bids highest first, asks lowest first; asking follows it.
        Dim book = Await s.BookAsync("111111", 5, CancellationToken.None)
        Assert.True(book.Value.Live)
        Assert.Equal({0.6}, book.Value.Bids.Select(Function(x) x(0)).ToArray())
        Assert.Equal({0.62, 0.63, 0.65}, book.Value.Asks.Select(Function(x) x(0)).ToArray())
        Assert.Contains("111111", s.DesiredTokens())
        Assert.False((Await s.BookAsync("bad", 5, CancellationToken.None)).HasValue)
    End Function

    <Fact>
    Public Async Function PagesSearchPricesActivityAndTheTool() As Task
        Dim h As New UrlHandler()
        h.Add("/events?limit=101&offset=0&", EventsPage(1000, 101))
        h.Add("/events?limit=51&offset=100&", EventsPage(1100, 51))
        h.Add("/events?limit=11&offset=0&order=volume24hr&ascending=false&active=true&closed=false&archived=false&tag_slug=finance", "[" & FedEvent() & "]")
        h.Add("/public-search?q=fed", J("{'events':[") & FedEvent() & "," & CryptoEvent() & J("],'pagination':{'hasMore':false,'totalResults':2}}"))
        h.Add("/events/slug/fed-december", FedEvent())
        h.Add("/events/100", FedEvent())
        h.Add("/markets?slug=fed-1", "[" & Market("1", "25 bps", Cond1, 0.62, "111111", "111112", events:="[{'id':'100','slug':'fed-december','title':'Fed decision in December?','tags':[{'label':'Finance','slug':'finance'}]}]") & "]")
        h.Add("/prices-history?market=111111&interval=1w&fidelity=60", J("{'history':[{'t':1793000000,'p':0.5},{'t':1793003600,'p':0.55},{'t':1793007200,'p':0.62}]}"))
        h.Add("POST https://clob.test/prices", J("{'111111':{'BUY':'0.61','SELL':'0.63'},'111112':{'BUY':'0.37','SELL':'0.39'}}"))
        h.Add("POST https://clob.test/midpoints", J("{'111111':'0.62','111112':'0.38'}"))
        h.Add("POST https://clob.test/last-trades-prices", J("[{'token_id':'111111','price':'0.62','side':'BUY'}]"))
        h.Add("/book?token_id=111111", J("{'bids':[{'price':'0.55','size':'10'},{'price':'0.61','size':'20'}],'asks':[{'price':'0.70','size':'5'},{'price':'0.63','size':'8'}]}"))
        h.Add("/v2/trades?condition=" & Cond1, J("{'data':[{'side':'BUY','outcome':'Yes','size':10,'price':0.62,'timestamp':1793000000,'name':'alice','proxy_wallet':'" & Wallet & "','transaction_hash':'0xdead'}]}"))
        h.Add("/v2/holders?condition=" & Cond1, J("{'data':[{'token_id':'111111','holders':[{'name':'bob','amount':500,'outcome_index':0,'proxy_wallet':'" & Wallet & "','bio':'secret'}]}]}"))
        h.Add("/v2/leaderboard?time_period=month&sort_by=VOLUME", J("{'data':[{'rank':1,'user_id':'" & Wallet & "','user_name':'carol','pnl':1000,'volume':5000,'profile_image':'x'}]}"))
        h.Add("/v2/positions?user=" & Wallet, J("{'data':[{'title':'Fed decision','outcome':'Yes','current_size':100,'avg_price':0.5,'current_price':0.62,'current_value':62,'total_pnl':12,'status':'OPEN','entry_fees_usdc':1}]}"))
        h.Add("/v2/activity?user=" & Wallet, J("{'data':[{'timestamp':1793000000,'type':'TRADE','title':'Fed decision','side':'BUY','size':100,'price':0.5,'usdc_size':50}]}"))
        Dim s As New PolymarketService(New PolymarketOptions With {.GammaUrl = "https://gamma.test", .ClobUrl = "https://clob.test", .DataUrl = "https://data.test"}, Nothing, h)

        ' More than one of Gamma's pages, joined.
        Dim many = Await s.EventsAsync(Nothing, Nothing, Nothing, Nothing, 0, 150, CancellationToken.None)
        Assert.Equal(150, many.Events.Count)
        Assert.True(many.More)
        Assert.Equal("1000", many.Events(0).Id)
        Assert.Equal("1149", many.Events.Last().Id)
        ' A category, and search (narrowed by category).
        Dim fin = Await s.EventsAsync(Nothing, "finance", "open", Nothing, 0, 10, CancellationToken.None)
        Assert.Equal("Fed decision in December?", fin.Events.Single().Title)
        Assert.False(fin.More)
        Dim found = Await s.EventsAsync("fed", Nothing, Nothing, Nothing, 0, 10, CancellationToken.None)
        Assert.Equal({"100", "200"}, found.Events.Select(Function(e) e.Id).ToArray())
        Assert.Contains(h.Asked, Function(u) u.Contains("events_status=active"))
        Assert.Equal("100", (Await s.EventsAsync("fed", "finance", Nothing, Nothing, 0, 10, CancellationToken.None)).Events.Single().Id)
        Assert.NotNull(s.KnownEvent("100"))

        ' Markets by slug, link; prices from the CLOB; history; the REST book; trades, holders, leaderboard, wallet.
        Dim bySlug = Await s.MarketAsync("fed-1", CancellationToken.None)
        Assert.Equal("Fed decision in December?", bySlug.Value.Ev.Title)
        Dim byLink = Await s.MarketAsync("https://polymarket.com/event/fed-december/fed-2", CancellationToken.None)
        Assert.Equal("50 bps", byLink.Value.Market.GroupTitle)
        Assert.False((Await s.MarketAsync("https://polymarket.com/event/nothing-here", CancellationToken.None)).HasValue)
        Dim q = Await s.QuotesAsync({"111111", "111112"}, CancellationToken.None)
        Assert.Equal(0.61, q("111111").Bid.Value)
        Assert.Equal(0.63, q("111111").Ask.Value)
        Assert.Equal(0.38, q("111112").Mid.Value)
        Assert.Equal(0.62, q("111111").Last.Value)
        Assert.Null(q("111112").Last)
        Dim hist = Await s.HistoryAsync("111111", "1w", CancellationToken.None)
        Assert.Equal(3, hist.Count)
        Assert.Equal(1793000000000.0, hist(0)(0))
        Dim book = Await s.BookAsync("111111", 5, CancellationToken.None)
        Assert.False(book.Value.Live)
        Assert.Equal({0.61, 0.55}, book.Value.Bids.Select(Function(x) x(0)).ToArray())
        Assert.Equal({0.63, 0.7}, book.Value.Asks.Select(Function(x) x(0)).ToArray())
        Dim trades = Await s.TradesAsync(Cond1, 10, CancellationToken.None)
        Assert.Equal("alice", trades(0)("name").ToString())
        Assert.Null(trades(0)("transaction_hash"))
        Assert.Null((Await s.HoldersAsync(Cond1, 10, CancellationToken.None))(0)("holders")(0)("bio"))
        Assert.Empty(Await s.TradesAsync("0xbad", 10, CancellationToken.None))
        Assert.Equal("carol", (Await s.LeaderboardAsync("month", "volume", 10, CancellationToken.None))(0)("user_name").ToString())
        Dim w = Await s.WalletAsync(Wallet.ToUpperInvariant().Replace("0X", "0x"), CancellationToken.None)
        Assert.Equal(62, w.Positions(0)("current_value").GetValue(Of Integer)())
        Assert.Null(w.Positions(0)("entry_fees_usdc"))
        Assert.Equal("TRADE", w.Activity(0)("type").ToString())
        Await Assert.ThrowsAsync(Of ArgumentException)(Function() s.WalletAsync("0xnope", CancellationToken.None))

        ' The tool: search, a market with live prices, book and history, trades, holders, leaderboard, wallet.
        Dim search = JsonNode.Parse(Await s.ToolAnswerAsync("{""action"":""search"",""query"":""fed""}", CancellationToken.None))
        Dim fed = search("events")(0)
        Assert.Equal("Fed decision in December?", fed("title").ToString())
        Assert.Equal("62%", fed("markets")(0)("outcomes")(0)("chance").ToString())
        Assert.Equal(2, fed("markets").AsArray().Count)
        Dim one = JsonNode.Parse(Await s.ToolAnswerAsync("{""action"":""market"",""market"":""fed-1"",""history"":""1w""}", CancellationToken.None))
        Assert.Equal("Will the Fed cut 25 bps?", one("market")("question").ToString())
        Assert.Equal(0.61, one("live")(0)("bestBid").GetValue(Of Double)(), 4)
        Assert.Equal(3, one("history").AsArray().Count)
        Assert.NotNull(one("book")("bids"))
        Assert.Contains("alice", Await s.ToolAnswerAsync("{""action"":""trades"",""market"":""fed-1""}", CancellationToken.None))
        Assert.Contains("bob", Await s.ToolAnswerAsync("{""action"":""holders"",""market"":""fed-1""}", CancellationToken.None))
        Assert.Contains("carol", Await s.ToolAnswerAsync("{""action"":""leaderboard"",""period"":""month"",""sort"":""volume""}", CancellationToken.None))
        Assert.Contains("TRADE", Await s.ToolAnswerAsync("{""action"":""wallet"",""address"":""" & Wallet & """}", CancellationToken.None))
        Assert.Contains("error", Await s.ToolAnswerAsync("{""action"":""wallet"",""address"":""nope""}", CancellationToken.None))
        Assert.Contains("No market found", Await s.ToolAnswerAsync("{""action"":""market"",""market"":""no-such""}", CancellationToken.None))
    End Function

    ''' <summary>The API (every account), the page's stream, every agent's tool, and a chat with Polymarket on.</summary>
    <Fact>
    Public Async Function TheTabAgentsAndChatsShareTheMarkets() As Task
        Assert.Equal(Permissions.Self, Permissions.ForRoute("GET", "/api/polymarket/events"))
        Assert.Equal(Permissions.Self, Permissions.ForRoute("GET", "/api/polymarket/stream"))
        Using fake As New FakePolymarket()
            fake.Add("/events?limit=", "[" & FedEvent() & "," & CryptoEvent() & "]")
            fake.Add("/events/100", FedEvent())
            fake.Add("/public-search?q=fed", J("{'events':[") & FedEvent() & J("],'pagination':{'hasMore':false}}"))
            fake.Add("/prices-history?market=111111", J("{'history':[{'t':1793000000,'p':0.5},{'t':1793003600,'p':0.62}]}"))
            fake.Add("/book?token_id=111111", J("{'bids':[{'price':'0.61','size':'20'}],'asks':[{'price':'0.63','size':'8'}]}"))
            fake.Add("/v2/trades?condition=" & Cond1, J("{'data':[{'side':'BUY','outcome':'Yes','size':10,'price':0.62,'timestamp':1793000000,'name':'alice'}]}"))
            fake.Add("/v2/leaderboard", J("{'data':[{'rank':1,'user_id':'" & Wallet & "','user_name':'carol','pnl':1000,'volume':5000}]}"))
            fake.Add("/markets?", "[]")
            Using up As New FakeUpstream()
                Dim asked = 0
                up.StreamOverride = Function(req)
                                        If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                        Select Case Interlocked.Increment(asked)
                                            Case 1 : Return ChatFeatureTests.ToolCallStream("polymarket", "{""action"":""search"",""query"":""fed""}")
                                            Case Else : Return ChatFeatureTests.TextStream("Traders give a 25 bps cut a 62% chance.", "stop")
                                        End Select
                                    End Function
                Dim base = $"http://127.0.0.1:{fake.Port}"
                Dim env = Await PlatformApiTests.StartAsync(up, Sub(o)
                                                                    o.Polymarket.GammaUrl = base
                                                                    o.Polymarket.ClobUrl = base
                                                                    o.Polymarket.DataUrl = base
                                                                End Sub)
                Using env.Host
                    Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                    Dim info = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/polymarket"))
                    Assert.Equal("politics", info("categories")(0)("slug").ToString())
                    Assert.False(info("status")("connected").GetValue(Of Boolean)())

                    Dim list = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/polymarket/events?limit=10"))
                    Dim fed = list("events").AsArray().First(Function(e) e("id").ToString() = "100")
                    Assert.Equal("Finance", fed("category").ToString())
                    Assert.Equal(2, fed("openMarkets").GetValue(Of Integer)())
                    Assert.Equal(0.62, fed("markets")(0)("prices")(0).GetValue(Of Double)(), 6)
                    Assert.False(fed("markets")(0)("live").GetValue(Of Boolean)())
                    Dim one = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/polymarket/events/100"))
                    Assert.Equal(2, one("event")("markets").AsArray().Count)
                    Assert.Equal("Resolves to the Fed decision.", one("description").ToString())
                    Assert.Equal(5, (Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/polymarket/events/100?closed=1")))("event")("markets").AsArray().Count)
                    Assert.Equal(HttpStatusCode.NotFound, (Await admin.Client.GetAsync("/api/polymarket/events/999")).StatusCode)
                    Dim hist = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/polymarket/history?token=111111&range=1w"))
                    Assert.Equal(2, hist("points").AsArray().Count)
                    Assert.Equal("7d", hist("chartRange").ToString())
                    Assert.Equal(HttpStatusCode.BadRequest, (Await admin.Client.GetAsync("/api/polymarket/history?token=abc")).StatusCode)
                    Dim book = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/polymarket/book?token=111111"))
                    Assert.Equal(0.61, book("bids")(0)(0).GetValue(Of Double)())
                    Assert.False(book("live").GetValue(Of Boolean)())
                    Dim trades = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/polymarket/trades?market=" & Cond1))
                    Assert.Equal("alice", trades("trades")(0)("name").ToString())
                    Assert.Equal(HttpStatusCode.BadRequest, (Await admin.Client.GetAsync("/api/polymarket/trades?market=0x1")).StatusCode)
                    Assert.Equal("carol", (Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/polymarket/leaderboard")))("traders")(0)("user_name").ToString())
                    Assert.Equal(HttpStatusCode.BadRequest, (Await admin.Client.GetAsync("/api/polymarket/wallet?address=0xnope")).StatusCode)
                    Dim searched = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/polymarket/events?q=fed"))
                    Assert.Equal("100", searched("events")(0)("id").ToString())

                    ' The page's stream starts with the channel's status.
                    Using req As New HttpRequestMessage(HttpMethod.Get, "/api/polymarket/stream?events=100")
                        Using resp = Await admin.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
                            Assert.Equal("text/event-stream", resp.Content.Headers.ContentType.MediaType)
                            Using sr As New StreamReader(Await resp.Content.ReadAsStreamAsync())
                                Dim line = Await sr.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10))
                                Assert.StartsWith("data: ", line)
                                Dim first = JsonNode.Parse(line.Substring(6))
                                Assert.Equal("status", first("type").ToString())
                                Assert.Equal(4, first("following").GetValue(Of Integer)()) ' the event's two open markets, two outcomes each
                            End Using
                        End Using
                    End Using

                    Dim meta = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/chat/meta"))
                    Assert.True(meta("polymarket").GetValue(Of Boolean)())
                    Assert.All(env.Host.H.Profiles.AllProfiles(), Sub(p) Assert.True(p.HasInternalTool("polymarket")))

                    ' A chat with Polymarket on: the model is offered the tool and reads the odds.
                    Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                    Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "What are the odds of a Fed cut?", .model = "fast/a", .settings = New With {.polymarket = True}})
                    Dim requests = up.Requests.Where(Function(r) ChatFeatureTests.Streamed(r)).ToList()
                    Assert.Contains("polymarket", requests(0)("tools").AsArray().Select(Function(x) x("function")("name").ToString()))
                    Dim result = requests(1)("messages").AsArray().Last(Function(m) m("role").ToString() = "tool")("content").ToString()
                    Assert.Contains("Fed decision in December?", result)
                    Assert.Contains("""chance"":""62%""", result)
                End Using
            End Using
        End Using
    End Function

End Class
