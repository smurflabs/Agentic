Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.Configuration
Imports Agentic.Persistence
Imports Agentic.Work
Imports Xunit

''' <summary>The Money tab (crypto prices, exchange rates) and the News tab (RSS/Atom feeds): parsing, storage, the API and the tools.</summary>
Public Class MarketsNewsTests

    ''' <summary>Answers by URL: the first key the URL contains wins.</summary>
    Private NotInheritable Class UrlHandler
        Inherits HttpMessageHandler
        Public ReadOnly Answers As New Dictionary(Of String, String)
        ''' <summary>A status other than 200 for URLs containing the key.</summary>
        Public ReadOnly Statuses As New Dictionary(Of String, HttpStatusCode)
        Public ReadOnly Asked As New List(Of String)
        Protected Overrides Function SendAsync(request As HttpRequestMessage, ct As CancellationToken) As Task(Of HttpResponseMessage)
            Dim url = request.RequestUri.AbsoluteUri
            SyncLock Asked
                Asked.Add(url)
            End SyncLock
            For Each kv In Answers
                If Not url.Contains(kv.Key) Then Continue For
                Dim status = Statuses.Where(Function(s) url.Contains(s.Key)).Select(Function(s) s.Value).DefaultIfEmpty(HttpStatusCode.OK).First()
                Return Task.FromResult(New HttpResponseMessage(status) With {.Content = New StringContent(kv.Value, Encoding.UTF8, "application/xml")})
            Next
            Return Task.FromResult(New HttpResponseMessage(HttpStatusCode.NotFound) With {.Content = New StringContent("no")})
        End Function
    End Class

    Private Shared Async Function NewDbAsync() As Task(Of (Db As Database, Dir As String))
        Dim dir = Path.Combine(Path.GetTempPath(), "mkt-" & Guid.NewGuid().ToString("N").Substring(0, 8))
        Directory.CreateDirectory(dir)
        Dim db As New Database(Path.Combine(dir, "agentic.db"), 2)
        Await New SchemaMigrator(db).MigrateAsync()
        Return (db, dir)
    End Function

    Private Const CmcAnswer As String = "{""data"":[{""id"":1,""name"":""Bitcoin"",""symbol"":""BTC"",""slug"":""bitcoin"",""quotes"":[{""symbol"":""USD"",""price"":76785.27921899772}]}," &
        "{""id"":1027,""name"":""Ethereum"",""symbol"":""ETH"",""slug"":""ethereum"",""quotes"":[{""symbol"":""USD"",""price"":2247.109599760755}]}]," &
        """status"":{""timestamp"":""2026-11-02T20:14:17.912Z"",""error_code"":""0"",""error_message"":"""",""elapsed"":2,""credit_count"":1}}"

    <Fact>
    Public Sub PricesAreReadFromBothSources()
        Dim c = MarketsService.ParseCrypto(CmcAnswer)
        Assert.Null(c.Error)
        Assert.Equal(2, c.Quotes.Count)
        Assert.Equal("BTC", c.Quotes(0).Symbol)
        Assert.Equal("Bitcoin", c.Quotes(0).Name)
        Assert.Equal(76785.27921899772, c.Quotes(0).Price, 6)
        Assert.Equal(New DateTime(2026, 11, 2, 20, 14, 17, 912, DateTimeKind.Utc), c.At.Value)
        Assert.Equal("bad symbol", MarketsService.ParseCrypto("{""data"":[],""status"":{""error_code"":""400"",""error_message"":""bad symbol""}}").Error)
        ' Frankfurter v2 (an array, one row per day and currency) and v1 ({date, rates}).
        Dim fx = MarketsService.ParseFx("[{""date"":""2026-11-02"",""base"":""USD"",""quote"":""EUR"",""rate"":0.89395},{""date"":""2026-11-01"",""base"":""USD"",""quote"":""GBP"",""rate"":0.7467}]")
        Assert.Equal(2, fx.Count)
        Assert.Equal("EUR", fx(0).Quote)
        Assert.Equal(New DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc), fx(0).Day)
        Assert.Equal(0.89395, fx(0).Rate, 6)
        Dim v1 = MarketsService.ParseFx("{""amount"":1,""base"":""USD"",""date"":""2026-11-02"",""rates"":{""EUR"":0.9,""JPY"":150.1}}")
        Assert.Equal(2, v1.Count)
        Assert.Equal(150.1, v1.Single(Function(r) r.Quote = "JPY").Rate, 3)
    End Sub

    <Fact>
    Public Sub LongHistoriesAreThinnedForCharts()
        Dim start = New DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)
        Dim rows = Enumerable.Range(0, 1440).Select(Function(i) (start.AddMinutes(i), CDbl(i))).ToList()
        Dim thin = MarketsService.Downsample(rows, 100)
        Assert.InRange(thin.Count, 90, 101)
        Assert.Equal(0.0, thin(0).Price)
        Assert.Equal(1439.0, thin(thin.Count - 1).Price)
        Assert.True(thin.Zip(thin.Skip(1)).All(Function(p) p.Second.Utc > p.First.Utc))
        Assert.Same(rows, MarketsService.Downsample(rows, 5000))
    End Sub

    <Fact>
    Public Async Function MarketsKeepPricesAndAnswerTheTool() As Task
        Dim t = Await NewDbAsync()
        Try
            Dim h As New UrlHandler()
            h.Answers("simple/price") = CmcAnswer
            h.Answers("from=") = "[{""date"":""2026-10-01"",""base"":""USD"",""quote"":""EUR"",""rate"":0.80},{""date"":""2026-10-26"",""base"":""USD"",""quote"":""EUR"",""rate"":0.85}]"
            h.Answers("quotes=EUR") = "[{""date"":""2026-11-02"",""base"":""USD"",""quote"":""EUR"",""rate"":0.89}]"
            Dim opt As New MarketsOptions With {.CryptoUrl = "https://cmc.test/simple/price", .FxUrl = "https://fx.test/v2/rates"}
            opt.CryptoSymbols.AddRange({"BTC", "ETH", "not valid!"})
            opt.FxQuotes.Add("EUR")
            Dim m As New MarketsService(t.Db, opt, Nothing, h)
            Assert.Equal({"BTC", "ETH"}, m.CryptoWatch.ToArray())
            ' Bitcoin a day earlier, for the 24-hour change.
            Await t.Db.ExecuteAsync("INSERT INTO MarketQuotes(Kind, Symbol, Price, Utc) VALUES ('crypto','BTC',70000,@t)", ("@t", CObj(Db.Ts(New DateTime(2026, 11, 1, 20, 0, 0, DateTimeKind.Utc)))))
            Await m.FetchCryptoAsync(CancellationToken.None)
            Await m.FetchFxAsync(CancellationToken.None)
            Assert.Null(m.CryptoError)
            Assert.Null(m.FxError)
            Assert.Contains(h.Asked, Function(u) u.Contains("symbol=BTC%2CETH") AndAlso u.Contains("convert=USD"))
            Assert.Contains(h.Asked, Function(u) u.Contains("base=USD&quotes=EUR&from="))

            Dim latest = Await m.LatestAsync()
            Dim btc = latest.Single(Function(q) q.Symbol = "BTC")
            Assert.Equal("Bitcoin", btc.Name)
            Assert.Equal(76785.28, btc.Price.Value, 2)
            Assert.Equal((76785.27921899772 - 70000) / 70000 * 100, btc.Change24h.Value, 3)
            Assert.Null(btc.Change7d)
            Dim eur = latest.Single(Function(q) q.Symbol = "EUR")
            Assert.Equal("fx", eur.Kind)
            Assert.Equal("Euro", eur.Name)
            Assert.Equal((0.89 - 0.85) / 0.85 * 100, eur.Change7d.Value, 3)

            Dim hist = Await m.HistoryAsync("fx", "EUR", "all")
            Assert.Equal(3, hist.Count)

            Dim answer = JsonNode.Parse(Await m.ToolAnswerAsync("{""symbols"":[""btc"",""SOL""],""history"":""30d""}"))
            Dim quotes = answer("quotes").AsArray()
            Assert.Single(quotes)
            Assert.Equal("BTC", quotes(0)("symbol").ToString())
            Assert.Equal("USD per BTC", quotes(0)("unit").ToString())
            Assert.Contains("SOL", answer("notWatched").ToString())
            Dim fxAnswer = JsonNode.Parse(Await m.ToolAnswerAsync("{""kind"":""fx""}"))("quotes").AsArray().Single()
            Assert.Equal(1 / 0.89, fxAnswer("usdPer").GetValue(Of Double)(), 3)

            ' An administrator's watch list replaces the configuration's; bad symbols are dropped.
            Dim saved = Await m.SetWatchlistAsync({"sol", "BTC", "bad symbol"}, {"jpy", "USD"})
            Assert.Equal({"SOL", "BTC"}, saved.Crypto.ToArray())
            Assert.Equal({"JPY"}, saved.Fx.ToArray())
            Dim again As New MarketsService(t.Db, opt, Nothing, h)
            Await again.LoadWatchlistAsync()
            Assert.Equal({"SOL", "BTC"}, again.CryptoWatch.ToArray())
            Await Assert.ThrowsAsync(Of ArgumentException)(Function() m.SetWatchlistAsync(Array.Empty(Of String)(), {"EUR"}))
        Finally
            Try : Directory.Delete(t.Dir, True) : Catch : End Try
        End Try
    End Function

    ''' <summary>Each account keeps its own coins and currencies (the defaults until it saves its own); new ones must have a price.</summary>
    <Fact>
    Public Async Function EachAccountKeepsItsOwnCoinsAndCurrencies() As Task
        Dim t = Await NewDbAsync()
        Try
            Dim h As New UrlHandler()
            h.Answers("symbol=SOL&") = "{""data"":[{""name"":""Solana"",""symbol"":""SOL"",""slug"":""solana"",""quotes"":[{""symbol"":""USD"",""price"":180.5}]}],""status"":{""error_code"":""0""}}"
            h.Answers("symbol=DOGEX") = "{""status"":{""error_code"":""400"",""error_message"":""Invalid value for symbol""}}"
            h.Statuses("symbol=DOGEX") = HttpStatusCode.BadRequest
            h.Answers("simple/price") = CmcAnswer
            h.Answers("coinbase.test") = "{""data"":{""currency"":""USD"",""rates"":{""BTC"":""0.000013"",""ETH"":""0.00044""}}}"
            h.Answers("quotes=JPY") = "[{""date"":""2026-11-04"",""base"":""USD"",""quote"":""JPY"",""rate"":150.1}]"
            h.Answers("quotes=XYZ") = "{""status"":422,""message"":""invalid currency: XYZ""}"
            h.Statuses("quotes=XYZ") = CType(422, HttpStatusCode)
            Dim opt As New MarketsOptions With {.CryptoUrl = "https://cmc.test/simple/price", .FxUrl = "https://fx.test/v2/rates",
                                                .CryptoFallbackUrl = "https://coinbase.test/v2/exchange-rates?currency=USD"}
            opt.CryptoSymbols.AddRange({"BTC", "ETH"})
            opt.FxQuotes.Add("EUR")
            Dim m As New MarketsService(t.Db, opt, Nothing, h)

            Dim ann = Await m.WatchForAsync("ann")
            Assert.False(ann.Custom)
            Assert.Equal({"BTC", "ETH"}, ann.Crypto.ToArray())
            Assert.Equal({"EUR"}, ann.Fx.ToArray())

            ' Unknown ones are named and nothing is saved; CoinMarketCap is not paused by an account's typo.
            Dim ex = Await Assert.ThrowsAsync(Of ArgumentException)(Function() m.SetAccountWatchlistAsync("ann", {"BTC", "DOGEX"}, {"XYZ", "JPY"}, CancellationToken.None))
            Assert.Contains("DOGEX", ex.Message)
            Assert.Contains("XYZ", ex.Message)
            Assert.DoesNotContain("JPY", ex.Message)
            Assert.False((Await m.WatchForAsync("ann")).Custom)
            Await Assert.ThrowsAsync(Of ArgumentException)(Function() m.SetAccountWatchlistAsync("ann", {"no good!"}, Array.Empty(Of String)(), CancellationToken.None))
            Await Assert.ThrowsAsync(Of ArgumentException)(Function() m.SetAccountWatchlistAsync("ann", Array.Empty(Of String)(), Array.Empty(Of String)(), CancellationToken.None))
            Await Assert.ThrowsAsync(Of ArgumentException)(Function() m.SetAccountWatchlistAsync("", {"BTC"}, {"EUR"}, CancellationToken.None))

            ' Her own: a new coin and a new currency, read at once; one list may be empty.
            Dim saved = Await m.SetAccountWatchlistAsync("ann", {"btc", "sol"}, {"jpy", "usd"}, CancellationToken.None)
            Assert.Equal({"BTC", "SOL"}, saved.Crypto.ToArray())
            Assert.Equal({"JPY"}, saved.Fx.ToArray())
            Assert.Contains(h.Asked, Function(u) u.Contains("symbol=SOL&"))
            Dim mine = Await m.LatestAsync(Nothing, "ann")
            Assert.Equal({"BTC", "SOL", "JPY"}, mine.Select(Function(q) q.Symbol).ToArray())
            Assert.Equal(180.5, mine.Single(Function(q) q.Symbol = "SOL").Price.Value, 2)
            Assert.Equal("Solana", mine.Single(Function(q) q.Symbol = "SOL").Name)
            Assert.Equal(150.1, mine.Single(Function(q) q.Symbol = "JPY").Price.Value, 2)
            ' The server reads them while she looks; Bob keeps the defaults.
            Assert.Contains("SOL", m.PolledCrypto())
            Assert.Equal({"EUR", "JPY"}, m.PolledFx().ToArray())
            Assert.False((Await m.WatchForAsync("bob")).Custom)
            Assert.Equal({"BTC", "ETH", "EUR"}, (Await m.LatestAsync(Nothing, "bob")).Select(Function(q) q.Symbol).ToArray())
            Assert.Contains("""symbol"":""SOL""", Await m.ToolAnswerAsync("{}", "ann"))
            Assert.DoesNotContain("""symbol"":""SOL""", Await m.ToolAnswerAsync("{}"))
            Assert.Contains("""symbol"":""SOL""", Await m.ToolAnswerAsync("{""symbols"":[""SOL""]}"))
            Dim onlyCoins = Await m.SetAccountWatchlistAsync("ann", {"SOL"}, Array.Empty(Of String)(), CancellationToken.None)
            Assert.Empty(onlyCoins.Fx)
            Assert.Equal({"SOL"}, (Await m.LatestAsync(Nothing, "ann")).Select(Function(q) q.Symbol).ToArray())

            ' Kept with her account.
            Dim again As New MarketsService(t.Db, opt, Nothing, h)
            Assert.True((Await again.WatchForAsync("ann")).Custom)
            Assert.Equal("{""crypto"":[""SOL""],""fx"":[]}", CStr(Await t.Db.ScalarAsync("SELECT Value FROM UserPreferences WHERE Username='ann' AND Key='markets.watchlist'")))
            ' The defaults again: she follows them (no row of her own).
            Await m.SetAccountWatchlistAsync("ann", {"BTC", "ETH"}, {"EUR"}, CancellationToken.None)
            Assert.False((Await m.WatchForAsync("ann")).Custom)
            Assert.Null(Await t.Db.ScalarAsync("SELECT Value FROM UserPreferences WHERE Username='ann' AND Key='markets.watchlist'"))
            Await m.SetAccountWatchlistAsync("ann", {"SOL"}, {"JPY"}, CancellationToken.None)
            Await m.ResetAccountWatchlistAsync("ann")
            Assert.False((Await m.WatchForAsync("ann")).Custom)
            ' An administrator's new defaults reach everyone without their own.
            Await m.SetWatchlistAsync({"ETH"}, {"GBP"})
            Assert.Equal({"ETH"}, (Await m.WatchForAsync("bob")).Crypto.ToArray())
        Finally
            Try : Directory.Delete(t.Dir, True) : Catch : End Try
        End Try
    End Function

    ''' <summary>Production 2026-11-02: CoinMarketCap allows only a few anonymous calls (429, error 1022); prices then come from Coinbase.</summary>
    <Fact>
    Public Async Function WhenCoinMarketCapRefusesPricesComeFromCoinbase() As Task
        Dim t = Await NewDbAsync()
        Try
            Dim h As New UrlHandler()
            h.Answers("cmc.test") = "{""status"":{""error_code"":""1022"",""error_message"":""You've reached the limit for anonymous access.""}}"
            h.Statuses("cmc.test") = CType(429, HttpStatusCode)
            h.Answers("coinbase.test") = "{""data"":{""currency"":""USD"",""rates"":{""BTC"":""0.0000130284400423"",""ETH"":""0.000445581171522"",""USDT"":""1.0016""}}}"
            Dim opt As New MarketsOptions With {.CryptoUrl = "https://cmc.test/simple/price", .CryptoFallbackUrl = "https://coinbase.test/v2/exchange-rates?currency=USD"}
            opt.CryptoSymbols.AddRange({"BTC", "ETH", "USDT"})
            Dim m As New MarketsService(t.Db, opt, Nothing, h)
            Await m.FetchCryptoAsync(CancellationToken.None)
            Assert.Null(m.CryptoError)
            Assert.StartsWith("Coinbase", m.CryptoSource)
            Assert.Contains("rate-limited without an API key", m.CryptoSource)
            Dim latest = Await m.LatestAsync("crypto")
            Assert.Equal(1 / 0.0000130284400423, latest.Single(Function(q) q.Symbol = "BTC").Price.Value, 2)
            Assert.Equal("Bitcoin", latest.Single(Function(q) q.Symbol = "BTC").Name)
            ' Paused: the next read goes straight to Coinbase.
            Dim cmcCalls = h.Asked.Where(Function(u) u.Contains("cmc.test")).Count()
            Await m.FetchCryptoAsync(CancellationToken.None)
            Assert.Equal(cmcCalls, h.Asked.Where(Function(u) u.Contains("cmc.test")).Count())
            Assert.Equal(2, h.Asked.Where(Function(u) u.Contains("coinbase.test")).Count())
        Finally
            Try : Directory.Delete(t.Dir, True) : Catch : End Try
        End Try
    End Function

    Private Const Rss As String = "<?xml version=""1.0"" encoding=""UTF-8""?><rss version=""2.0"" xmlns:media=""http://search.yahoo.com/mrss/"" xmlns:dc=""http://purl.org/dc/elements/1.1/""><channel><title>BBC</title>" &
        "<item><title><![CDATA[Ceasefire talks resume in Cairo]]></title><description><![CDATA[<p>Negotiators &amp; mediators met <b>again</b>.</p>]]></description>" &
        "<link>https://www.bbc.co.uk/news/articles/abc</link><pubDate>Mon, 02 Nov 2026 18:04:10 GMT</pubDate><dc:creator>Jane Doe</dc:creator>" &
        "<media:thumbnail width=""240"" height=""135"" url=""https://ichef.bbci.co.uk/pic.jpg""/></item>" &
        "<item><title>Second story</title><link>https://www.bbc.co.uk/news/articles/def</link><pubDate>Mon, 02 Nov 2026 13:04:10 EST</pubDate></item>" &
        "<item><title>No link</title></item></channel></rss>"
    Private Const Rdf As String = "<?xml version=""1.0""?><rdf:RDF xmlns:rdf=""http://www.w3.org/1999/02/22-rdf-syntax-ns#"" xmlns=""http://purl.org/rss/1.0/"" xmlns:dc=""http://purl.org/dc/elements/1.1/"">" &
        "<channel rdf:about=""https://www.dw.com""><title>DW</title></channel>" &
        "<item rdf:about=""https://www.dw.com/en/story-1""><title>Germany story</title><link>https://www.dw.com/en/story-1</link><description>About it</description><dc:date>2026-11-02T17:30:00Z</dc:date></item></rdf:RDF>"
    Private Const Atom As String = "<?xml version=""1.0"" encoding=""utf-8""?><feed xmlns=""http://www.w3.org/2005/Atom""><title>The Conversation</title>" &
        "<entry><title>Why markets fell</title><link rel=""alternate"" type=""text/html"" href=""https://theconversation.com/why-markets-fell-1""/>" &
        "<published>2026-11-02T10:00:00-05:00</published><updated>2026-11-02T12:00:00Z</updated><summary type=""html"">&lt;p&gt;A look at &lt;img src=""https://images.theconversation.com/x.jpg""&gt; the drop&lt;/p&gt;</summary>" &
        "<author><name>A. Writer</name></author></entry></feed>"
    Private Const GoogleNews As String = "<?xml version=""1.0"" encoding=""UTF-8""?><rss version=""2.0""><channel><item><title>Oil prices jump on supply worries - Reuters</title>" &
        "<link>https://news.google.com/rss/articles/CBMi123?oc=5</link><pubDate>Mon, 02 Nov 2026 19:00:00 GMT</pubDate><description>&lt;a href=""x""&gt;Oil prices jump&lt;/a&gt;</description></item></channel></rss>"

    <Fact>
    Public Sub FeedsOfEveryKindAreRead()
        Dim now = New DateTime(2026, 11, 2, 21, 0, 0, DateTimeKind.Utc)
        Dim rssItems = NewsService.ParseFeed(Rss, "BBC World", "international", now)
        Assert.Equal(2, rssItems.Count)
        Assert.Equal("Ceasefire talks resume in Cairo", rssItems(0).Title)
        Assert.Equal("Negotiators & mediators met again.", rssItems(0).Summary)
        Assert.Equal(New DateTime(2026, 11, 2, 18, 4, 10, DateTimeKind.Utc), rssItems(0).PublishedUtc)
        Assert.Equal("Jane Doe", rssItems(0).Author)
        Assert.Equal("https://ichef.bbci.co.uk/pic.jpg", rssItems(0).ImageUrl)
        Assert.Equal(New DateTime(2026, 11, 2, 18, 4, 10, DateTimeKind.Utc), rssItems(1).PublishedUtc) ' 13:04 EST
        Assert.Equal(NewsService.IdOf("https://www.bbc.co.uk/news/articles/abc"), rssItems(0).Id)

        Dim rdfItem = Assert.Single(NewsService.ParseFeed(Rdf, "Deutsche Welle", "international", now))
        Assert.Equal("Germany story", rdfItem.Title)
        Assert.Equal(New DateTime(2026, 11, 2, 17, 30, 0, DateTimeKind.Utc), rdfItem.PublishedUtc)

        Dim atomItem = Assert.Single(NewsService.ParseFeed(Atom, "The Conversation", "investigative", now))
        Assert.Equal("https://theconversation.com/why-markets-fell-1", atomItem.Link)
        Assert.Equal(New DateTime(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc), atomItem.PublishedUtc)
        Assert.Equal("A. Writer", atomItem.Author)
        Assert.Equal("https://images.theconversation.com/x.jpg", atomItem.ImageUrl)

        ' Google News (Reuters, AP): the outlet's name leaves the title, and the summary (a link to itself) is dropped.
        Dim g = Assert.Single(NewsService.ParseFeed(GoogleNews, "Reuters", "agencies", now))
        Assert.Equal("Oil prices jump on supply worries", g.Title)
        Assert.Null(g.Summary)
        ' NHK through Google News: its own site name goes too; a "|" inside a headline stays.
        Dim nhk = NewsService.ParseFeed(GoogleNews.Replace("Oil prices jump on supply worries - Reuters", "GMO chairman apologizes over cyberattack | NHK WORLD-JAPAN News - NHKニュース"), "NHK World", "international", now)
        Assert.Equal("GMO chairman apologizes over cyberattack", nhk.Single().Title)
        Dim nhkShow = NewsService.ParseFeed(GoogleNews.Replace("Oil prices jump on supply worries - Reuters", "Bee Hummingbird, Cuba - Darwin's Amazing Animals | NHK WORLD-JAPAN - NHK"), "NHK World", "international", now)
        Assert.Equal("Bee Hummingbird, Cuba - Darwin's Amazing Animals", nhkShow.Single().Title)
        Dim bar = NewsService.ParseFeed(GoogleNews.Replace("Oil prices jump on supply worries - Reuters", "Live | Markets fall as oil jumps - Reuters"), "Reuters", "agencies", now)
        Assert.Equal("Live | Markets fall as oil jumps", bar.Single().Title)
        Assert.Contains(NewsService.DefaultFeeds, Function(f) f.Name = "NHK World" AndAlso f.Url.Contains("news.google.com"))
        Assert.Contains(NewsService.DefaultFeeds, Function(f) f.Name = "Washington Post" AndAlso f.Url.Contains("site:washingtonpost.com"))
        ' Every tab has feeds (UK, Europe and Australia among them), every feed a tab, and no outlet twice.
        Assert.Equal({"international", "us", "uk", "europe", "australia", "agencies", "investigative", "business"}, NewsService.Categories)
        Assert.All(NewsService.Categories, Sub(c) Assert.Contains(NewsService.DefaultFeeds, Function(f) f.Category = c))
        Assert.All(NewsService.DefaultFeeds, Sub(f) Assert.Contains(f.Category, NewsService.Categories))
        Assert.Equal(NewsService.DefaultFeeds.Length, NewsService.DefaultFeeds.Select(Function(f) f.Name).Distinct().Count())
        Assert.Equal("Australia", NewsService.CategoryNames("australia"))

        Assert.Equal(New DateTime(2026, 11, 2, 18, 4, 10, DateTimeKind.Utc), NewsService.ParseDate("Mon, 02 Nov 2026 13:04:10 -0500"))
        Assert.Null(NewsService.ParseDate("whenever"))
    End Sub

    <Fact>
    Public Async Function NewsIsCollectedOnceAndSearched() As Task
        Dim t = Await NewDbAsync()
        Try
            Dim h As New UrlHandler()
            h.Answers("bbc.test") = Rss
            h.Answers("dw.test") = Rdf
            Dim opt As New NewsOptions()
            opt.Feeds.Add(New NewsFeedOptions With {.Name = "BBC World", .Url = "https://bbc.test/rss.xml", .Category = "international"})
            opt.Feeds.Add(New NewsFeedOptions With {.Name = "Deutsche Welle", .Url = "https://dw.test/rdf", .Category = "international"})
            opt.Feeds.Add(New NewsFeedOptions With {.Name = "Broken", .Url = "https://broken.test/feed", .Category = "business"})
            Dim n As New NewsService(t.Db, opt, Nothing, h)
            Await n.ReadAllAsync(CancellationToken.None)
            Await n.ReadAllAsync(CancellationToken.None)
            Assert.Equal(3L, Await t.Db.ScalarLongAsync("SELECT COUNT(*) FROM NewsArticles"))
            Dim feeds = n.Feeds
            Assert.Equal(2, feeds.Single(Function(f) f.Name = "BBC World").Items)
            Assert.Equal(0, feeds.Single(Function(f) f.Name = "BBC World").Added) ' the second read found nothing new
            Assert.Contains("404", feeds.Single(Function(f) f.Name = "Broken").Error)

            Dim all = Await n.SearchAsync(Nothing, Nothing, Nothing, 24 * 3650, 50)
            Assert.Equal(3, all.Count)
            Assert.True(all(0).PublishedUtc >= all(1).PublishedUtc)
            Assert.Equal("Ceasefire talks resume in Cairo", Assert.Single(Await n.SearchAsync("cairo talks", Nothing, Nothing, 24 * 3650, 50)).Title)
            Assert.Equal("Germany story", Assert.Single(Await n.SearchAsync(Nothing, "international", "deutsche welle", 24 * 3650, 50)).Title)
            Assert.Empty(Await n.SearchAsync("100%_", Nothing, Nothing, 24 * 3650, 50))
            Dim text = NewsService.ToolText(all, 1)
            Assert.StartsWith("[1] ", text)
            Assert.Contains("https://www.dw.com/en/story-1", text)
        Finally
            Try : Directory.Delete(t.Dir, True) : Catch : End Try
        End Try
    End Function

    ''' <summary>The API, every agent's tools, and a chat with Money and News on: the data reaches the model and articles are cited.</summary>
    <Fact>
    Public Async Function TheTabsAgentsAndChatsShareTheData() As Task
        Using up As New FakeUpstream()
            Dim asked = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    Select Case Interlocked.Increment(asked)
                                        Case 1 : Return ChatFeatureTests.ToolCallStream("news_articles", "{""query"":""cairo""}")
                                        Case 2 : Return ChatFeatureTests.ToolCallStream("market_prices", "{""symbols"":[""BTC""]}")
                                        Case Else : Return ChatFeatureTests.TextStream("Talks resumed [1]; Bitcoin is near $76,785.", "stop")
                                    End Select
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o)
                                                                ' Nothing is read from the internet in the test: the sources answer nothing.
                                                                o.Markets.CryptoUrl = "http://127.0.0.1:9/simple/price"
                                                                o.Markets.FxUrl = "http://127.0.0.1:9/rates"
                                                                o.News.Feeds.Add(New NewsFeedOptions With {.Name = "Nowhere", .Url = "http://127.0.0.1:9/feed", .Category = "international"})
                                                            End Sub)
            Using env.Host
                Dim hostDb = env.Host.H.Database
                Await hostDb.ExecuteAsync("INSERT INTO MarketQuotes(Kind, Symbol, Price, Utc) VALUES ('crypto','BTC',76785.28,@t)", ("@t", CObj(Db.Ts(DateTime.UtcNow.AddMinutes(-1)))))
                Await hostDb.ExecuteAsync("INSERT INTO MarketAssets(Kind, Symbol, Name) VALUES ('crypto','BTC','Bitcoin')")
                For Each a In NewsService.ParseFeed(Rss, "BBC World", "international", DateTime.UtcNow)
                    Await hostDb.ExecuteAsync("INSERT INTO NewsArticles(Id, Link, Source, Category, Title, Summary, PublishedUtc, FetchedUtc) VALUES (@i,@l,@s,@c,@t,@m,@p,@f)",
                                          ("@i", CObj(a.Id)), ("@l", a.Link), ("@s", a.Source), ("@c", a.Category), ("@t", a.Title), ("@m", If(a.Summary, CObj(DBNull.Value))),
                                          ("@p", Db.Ts(DateTime.UtcNow.AddHours(-1))), ("@f", Db.Ts(DateTime.UtcNow)))
                Next
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")

                ' The pages' API.
                Dim markets = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/markets"))
                Dim btc = markets("crypto").AsArray().Single(Function(q) q("symbol").ToString() = "BTC")
                Assert.Equal("Bitcoin", btc("name").ToString())
                Assert.Equal(76785.28, btc("price").GetValue(Of Double)(), 2)
                Assert.Equal(4, markets("fx").AsArray().Count) ' EUR, GBP, AUD, CNY watched (no prices yet)
                Dim hist = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/markets/history?kind=crypto&symbol=BTC&range=24h"))
                Assert.Single(hist("points").AsArray())
                Dim news = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/news?q=ceasefire"))
                Assert.Equal("Ceasefire talks resume in Cairo", news("articles").AsArray().Single()("title").ToString())
                Assert.Equal(2, news("categories")("international").GetValue(Of Integer)())
                Dim sources = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/news/sources"))
                Assert.Equal("Nowhere", sources("feeds").AsArray().Single()("name").ToString())
                Dim bad = Await PlatformApiTests.Send(admin.Client, "PUT", "/api/markets/defaults", New With {.crypto = New String() {}, .fx = {"EUR"}}, admin.Csrf)
                Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode)
                Dim meta = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/chat/meta"))
                Assert.True(meta("markets").GetValue(Of Boolean)())
                Assert.True(meta("news").GetValue(Of Boolean)())

                ' Every agent has both tools.
                Assert.NotEmpty(env.Host.H.Profiles.AllProfiles())
                Assert.All(env.Host.H.Profiles.AllProfiles(), Sub(p)
                                                                Assert.True(p.HasInternalTool("market_prices"))
                                                                Assert.True(p.HasInternalTool("news_articles"))
                                                            End Sub)

                ' A chat with Money and News on: the model reads both, and the article is a source it cites.
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim done = Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "What's happening in Cairo, and the bitcoin price?", .model = "fast/a",
                                                                               .settings = New With {.markets = True, .news = True}})
                Dim requests = up.Requests.Where(Function(r) ChatFeatureTests.Streamed(r)).ToList()
                Dim offered = requests(0)("tools").AsArray().Select(Function(t) t("function")("name").ToString()).ToList()
                Assert.Contains("market_prices", offered)
                Assert.Contains("news_articles", offered)
                Dim newsResult = requests(1)("messages").AsArray().Last(Function(m) m("role").ToString() = "tool")("content").ToString()
                Assert.Contains("[1] Ceasefire talks resume in Cairo — BBC World", newsResult)
                Dim priceResult = requests(2)("messages").AsArray().Last(Function(m) m("role").ToString() = "tool")("content").ToString()
                Assert.Contains("""symbol"":""BTC""", priceResult)
                Dim chat = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/chat/conversations/" & id))
                Dim answer = chat("messages").AsArray().Last()
                Assert.Equal("https://www.bbc.co.uk/news/articles/abc", answer("sources").AsArray()(0)("url").ToString())
            End Using
        End Using
    End Function

End Class
