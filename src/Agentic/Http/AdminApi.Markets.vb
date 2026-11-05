Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports Agentic.Utilities
Imports Agentic.Work

Namespace Http

    ' Admin API: the Money tab (crypto prices from CoinMarketCap, exchange rates from Frankfurter) and the News tab (articles
    ' from RSS feeds). Both are read on the server on their own; every account may look. Each account keeps its own coins and
    ' currencies (the defaults until it saves its own); administrators choose the defaults.
    Partial Friend NotInheritable Class AdminApi

        Private Sub RegisterMarketsAndNews(r As Router)
            r.Map("GET", "/api/markets", AuthLevel.Read, AddressOf MarketsInfo)
            r.Map("GET", "/api/markets/history", AuthLevel.Read, AddressOf MarketsHistory)
            r.Map("POST", "/api/markets/refresh", AuthLevel.Read, AddressOf MarketsRefresh)
            r.Map("PUT", "/api/markets/watchlist", AuthLevel.SubmitTask, AddressOf MarketsWatchlist)
            r.Map("DELETE", "/api/markets/watchlist", AuthLevel.SubmitTask, AddressOf MarketsWatchlistReset)
            r.Map("PUT", "/api/markets/defaults", AuthLevel.Admin, AddressOf MarketsDefaults)
            r.Map("GET", "/api/news", AuthLevel.Read, AddressOf NewsList)
            r.Map("GET", "/api/news/sources", AuthLevel.Read, AddressOf NewsSources)
            r.Map("POST", "/api/news/refresh", AuthLevel.Read, AddressOf NewsRefresh)
            r.Map("GET", "/api/news/{articleId}/image", AuthLevel.Read, AddressOf NewsImage)
        End Sub

        ''' <summary>An article's picture, fetched through here (the pages may only load this server's pictures).</summary>
        Private Async Function NewsImage(rc As RequestContext) As Task
            RequireNews()
            Dim pic As (Data As Byte(), Type As String)? = Nothing
            Try
                pic = Await _h.News.PictureAsync(rc.Route("articleId"), rc.CancellationToken).ConfigureAwait(False)
            Catch ex As Exception When Not ExceptionUtil.IsCancellation(ex)
            End Try
            If Not pic.HasValue Then Throw ApiException.NotFound("NO_PICTURE", "This article has no picture that can be shown.")
            Await rc.WriteBytesAsync(200, pic.Value.Type, pic.Value.Data, New Dictionary(Of String, String) From {{"Cache-Control", "private, max-age=86400"}}).ConfigureAwait(False)
        End Function

        Private Sub RequireMarkets()
            If _h.Markets Is Nothing OrElse Not _h.Markets.Enabled Then Throw New ApiException(404, "MARKETS_OFF", "The Money tab is switched off (Markets.Enabled).")
        End Sub

        Private Sub RequireNews()
            If _h.News Is Nothing OrElse Not _h.News.Enabled Then Throw New ApiException(404, "NEWS_OFF", "The News tab is switched off (News.Enabled).")
        End Sub

        Private Shared Function QuoteDto(q As MarketQuote) As Object
            Return New With {.kind = q.Kind, .symbol = q.Symbol, .name = q.Name, .price = q.Price, .utc = q.Utc,
                             .change1h = q.Change1h, .change24h = q.Change24h, .change7d = q.Change7d, .change30d = q.Change30d}
        End Function

        ''' <summary>
        ''' The account's coins and currencies: price, changes, when each was read; its watch lists (custom: its own, else the
        ''' defaults), the defaults, and how reading goes.
        ''' </summary>
        Private Async Function MarketsInfo(rc As RequestContext) As Task
            RequireMarkets()
            Dim m = _h.Markets
            Dim w = Await m.WatchForAsync(rc.User.Name).ConfigureAwait(False)
            Dim quotes = Await m.LatestAsync(Nothing, rc.User.Name).ConfigureAwait(False)
            Await rc.OkAsync(New With {
                .crypto = quotes.Where(Function(q) q.Kind = MarketsService.Crypto).Select(AddressOf QuoteDto).ToList(),
                .fx = quotes.Where(Function(q) q.Kind = MarketsService.Fx).Select(AddressOf QuoteDto).ToList(),
                .base = m.FxBase,
                .watchlist = New With {.crypto = w.Crypto, .fx = w.Fx},
                .custom = w.Custom,
                .defaults = New With {.crypto = m.CryptoWatch, .fx = m.FxWatch},
                .status = New With {.cryptoUpdatedUtc = m.CryptoUpdatedUtc, .fxUpdatedUtc = m.FxUpdatedUtc, .cryptoError = m.CryptoError, .fxError = m.FxError,
                                    .cryptoSource = If(m.CryptoSource, "CoinMarketCap"),
                                    .cryptoPollSeconds = Math.Max(15, m.Options.CryptoPollSeconds), .fxPollMinutes = Math.Max(5, m.Options.FxPollMinutes)},
                .sources = New With {.crypto = If(m.CryptoSource, "CoinMarketCap"), .fx = "Frankfurter (European Central Bank reference rates)"},
                .isAdmin = rc.User.IsAdmin}).ConfigureAwait(False)
        End Function

        ''' <summary>Query kind (crypto|fx), symbol, range (1h, 6h, 24h, 7d, 30d, 90d, 1y, all), points: the price over time.</summary>
        Private Async Function MarketsHistory(rc As RequestContext) As Task
            RequireMarkets()
            Dim kind = If(rc.QueryString("kind"), MarketsService.Crypto).ToLowerInvariant()
            If kind <> MarketsService.Crypto AndAlso kind <> MarketsService.Fx Then Throw ApiException.BadRequest("kind is crypto or fx.")
            Dim symbol = If(rc.QueryString("symbol"), "").Trim().ToUpperInvariant()
            If symbol.Length = 0 Then Throw ApiException.BadRequest("symbol is required (BTC, EUR …).")
            Dim range = If(rc.QueryString("range"), "24h")
            Dim points = Await _h.Markets.HistoryAsync(kind, symbol, range, rc.QueryInt("points", 360, 10, 2000)).ConfigureAwait(False)
            Await rc.OkAsync(New With {.kind = kind, .symbol = symbol, .range = range,
                                       .points = points.Select(Function(p) New Object() {New DateTimeOffset(p.Utc).ToUnixTimeMilliseconds(), p.Price}).ToList()}).ConfigureAwait(False)
        End Function

        ''' <summary>Reads prices now (at most every 15 s for everyone).</summary>
        Private Async Function MarketsRefresh(rc As RequestContext) As Task
            RequireMarkets()
            Await _h.Markets.RefreshAsync(rc.CancellationToken).ConfigureAwait(False)
            Await MarketsInfo(rc).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' Body {crypto: ["BTC", …], fx: ["EUR", …]}: the account's own coins and currencies (one list may be empty). A coin or
        ''' currency nobody watches yet must have a price now (400 names the ones that do not); answers like GET /api/markets.
        ''' </summary>
        Private Async Function MarketsWatchlist(rc As RequestContext) As Task
            RequireMarkets()
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Try
                Await _h.Markets.SetAccountWatchlistAsync(rc.User.Name, JsonUtil.GetStringList(b, "crypto"), JsonUtil.GetStringList(b, "fx"), rc.CancellationToken).ConfigureAwait(False)
            Catch ex As ArgumentException
                Throw ApiException.BadRequest(ex.Message)
            Catch ex As InvalidOperationException
                Throw New ApiException(503, "PRICES_UNAVAILABLE", ex.Message)
            End Try
            Await MarketsInfo(rc).ConfigureAwait(False)
        End Function

        ''' <summary>The account watches the defaults again; answers like GET /api/markets.</summary>
        Private Async Function MarketsWatchlistReset(rc As RequestContext) As Task
            RequireMarkets()
            Await _h.Markets.ResetAccountWatchlistAsync(rc.User.Name).ConfigureAwait(False)
            Await MarketsInfo(rc).ConfigureAwait(False)
        End Function

        ''' <summary>Body {crypto: ["BTC", …], fx: ["EUR", …]}: the defaults every account watches until it saves its own (administrators).</summary>
        Private Async Function MarketsDefaults(rc As RequestContext) As Task
            RequireMarkets()
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim lists As (Crypto As List(Of String), Fx As List(Of String))
            Try
                lists = Await _h.Markets.SetWatchlistAsync(JsonUtil.GetStringList(b, "crypto"), JsonUtil.GetStringList(b, "fx")).ConfigureAwait(False)
            Catch ex As ArgumentException
                Throw ApiException.BadRequest(ex.Message)
            End Try
            Await AuditAsync(rc, "markets-watchlist", "markets", "defaults", New With {.crypto = lists.Crypto, .fx = lists.Fx}).ConfigureAwait(False)
            ' Prices for new symbols come at once.
            Try
                Await _h.Markets.FetchCryptoAsync(rc.CancellationToken).ConfigureAwait(False)
                Await _h.Markets.FetchFxAsync(rc.CancellationToken).ConfigureAwait(False)
            Catch ex As Exception When Not ExceptionUtil.IsCancellation(ex)
            End Try
            Await MarketsInfo(rc).ConfigureAwait(False)
        End Function

        Private Shared Function ArticleDto(a As NewsArticle) As Object
            Return New With {.id = a.Id, .title = a.Title, .link = a.Link, .source = a.Source, .category = a.Category, .summary = a.Summary,
                             .author = a.Author, .imageUrl = a.ImageUrl, .publishedUtc = a.PublishedUtc}
        End Function

        ''' <summary>Query q, category, source, hours (default 72), limit (default 60), before (paging): the articles, newest first.</summary>
        Private Async Function NewsList(rc As RequestContext) As Task
            RequireNews()
            Dim hours = rc.QueryInt("hours", 72, 1, 24 * 365)
            Dim limit = rc.QueryInt("limit", 60, 1, 200)
            Dim list = Await _h.News.SearchAsync(rc.QueryString("q"), rc.QueryString("category"), rc.QueryString("source"), hours, limit, rc.QueryDate("before")).ConfigureAwait(False)
            Dim counts = Await _h.News.CountsAsync(hours).ConfigureAwait(False)
            Await rc.OkAsync(New With {.articles = list.Select(AddressOf ArticleDto).ToList(), .more = list.Count >= limit,
                                       .categories = counts.Categories, .sources = counts.Sources, .lastRoundUtc = _h.News.LastRoundUtc}).ConfigureAwait(False)
        End Function

        ''' <summary>The feeds: name, category, link, when each was last read and how (items, new ones, error).</summary>
        Private Async Function NewsSources(rc As RequestContext) As Task
            RequireNews()
            Await rc.OkAsync(New With {
                .feeds = _h.News.Feeds.Select(Function(f) New With {.name = f.Name, .url = f.Url, .category = f.Category, .lastReadUtc = f.LastReadUtc,
                                                                      .lastOkUtc = f.LastOkUtc, .items = f.Items, .added = f.Added, .error = f.Error}).ToList(),
                .categories = NewsService.CategoryNames, .pollMinutes = Math.Max(2, _h.News.Options.PollMinutes), .keepDays = _h.News.Options.KeepDays,
                .lastRoundUtc = _h.News.LastRoundUtc}).ConfigureAwait(False)
        End Function

        ''' <summary>Reads every feed again now (at most once a minute for everyone).</summary>
        Private Async Function NewsRefresh(rc As RequestContext) As Task
            RequireNews()
            Await rc.OkAsync(New With {.started = _h.News.RefreshNow()}).ConfigureAwait(False)
        End Function

    End Class

End Namespace
