Option Strict On
Option Explicit On
Option Infer On

Imports Agentic.Utilities
Imports Agentic.Work

Namespace Http

    ' Admin API: the Stocks tab (quotes and charts from Yahoo Finance, tickers from NASDAQ Trader's symbol directory). Every
    ' account may look and look up any ticker, and keeps its own watch list (the defaults until it saves one).
    Partial Friend NotInheritable Class AdminApi

        Private Sub RegisterStocks(r As Router)
            r.Map("GET", "/api/stocks", AuthLevel.Read, AddressOf StocksInfo)
            r.Map("GET", "/api/stocks/chart", AuthLevel.Read, AddressOf StocksChart)
            r.Map("GET", "/api/stocks/quote", AuthLevel.Read, AddressOf StocksQuote)
            r.Map("GET", "/api/stocks/search", AuthLevel.Read, AddressOf StocksSearch)
            r.Map("POST", "/api/stocks/refresh", AuthLevel.Read, AddressOf StocksRefresh)
            r.Map("PUT", "/api/stocks/watchlist", AuthLevel.SubmitTask, AddressOf StocksWatchlist)
            r.Map("DELETE", "/api/stocks/watchlist", AuthLevel.SubmitTask, AddressOf StocksWatchlistReset)
        End Sub

        Private Sub RequireStocks()
            If _h.Stocks Is Nothing OrElse Not _h.Stocks.Enabled Then Throw New ApiException(404, "STOCKS_OFF", "The Stocks tab is switched off (Stocks.Enabled).")
        End Sub

        ''' <summary>The account's tickers with their quotes, its watch list (custom: its own, else the defaults), and how reading goes.</summary>
        Private Async Function StocksInfo(rc As RequestContext) As Task
            RequireStocks()
            Dim s = _h.Stocks
            Dim mine = Await s.LatestForAsync(rc.User.Name, rc.CancellationToken).ConfigureAwait(False)
            Await rc.OkAsync(New With {.quotes = mine.Quotes, .watchlist = mine.Watch, .custom = mine.Custom, .defaults = s.Defaults,
                                       .status = New With {.updatedUtc = s.UpdatedUtc, .error = s.LastError, .directoryError = s.DirectoryError, .tickers = s.DirectorySize,
                                                           .pollSeconds = Math.Max(20, s.Options.PollSeconds), .closedPollMinutes = Math.Max(1, s.Options.ClosedPollMinutes)},
                                       .source = "Yahoo Finance"}).ConfigureAwait(False)
        End Function

        ''' <summary>Query symbol, range (1d, 5d, 1mo, 6mo, ytd, 1y, 5y, max): the price over time, the previous close (1d) and the quote.</summary>
        Private Async Function StocksChart(rc As RequestContext) As Task
            RequireStocks()
            Dim symbol = StocksService.Normalize(rc.QueryString("symbol"))
            If Not StocksService.IsTicker(symbol) Then Throw ApiException.BadRequest("symbol is a ticker: AAPL, BRK-B, ^GSPC …")
            Dim range = If(rc.QueryString("range"), "1d")
            Dim c = Await _h.Stocks.ChartAsync(symbol, range, rc.CancellationToken).ConfigureAwait(False)
            If Not c.HasValue Then Throw ApiException.NotFound("UNKNOWN_TICKER", $"Yahoo Finance does not know {symbol}.")
            Await rc.OkAsync(New With {.symbol = symbol, .range = range, .points = c.Value.Points, .previousClose = c.Value.PreviousClose, .quote = c.Value.Quote}).ConfigureAwait(False)
        End Function

        ''' <summary>Query symbol: any ticker's quote now (it need not be watched).</summary>
        Private Async Function StocksQuote(rc As RequestContext) As Task
            RequireStocks()
            Dim symbol = StocksService.Normalize(rc.QueryString("symbol"))
            If Not StocksService.IsTicker(symbol) Then Throw ApiException.BadRequest("symbol is a ticker: AAPL, BRK-B, ^GSPC …")
            Dim q = Await _h.Stocks.FetchQuoteAsync(symbol, rc.CancellationToken).ConfigureAwait(False)
            If q Is Nothing Then Throw ApiException.NotFound("UNKNOWN_TICKER", $"Yahoo Finance does not know {symbol}.")
            Await rc.OkAsync(q).ConfigureAwait(False)
        End Function

        ''' <summary>Query q, limit: tickers from the US symbol directory, by symbol then by name.</summary>
        Private Async Function StocksSearch(rc As RequestContext) As Task
            RequireStocks()
            Dim hits = _h.Stocks.Search(rc.QueryString("q"), rc.QueryInt("limit", 12, 1, 50))
            Await rc.OkAsync(hits.Select(Function(h) New With {.symbol = h.Symbol, .name = h.Name, .exchange = h.Exchange, .etf = h.Etf}).ToList()).ConfigureAwait(False)
        End Function

        ''' <summary>Reads the quotes now (at most every 15 s for everyone); answers like GET /api/stocks.</summary>
        Private Async Function StocksRefresh(rc As RequestContext) As Task
            RequireStocks()
            Await _h.Stocks.RefreshAsync(rc.CancellationToken).ConfigureAwait(False)
            Await StocksInfo(rc).ConfigureAwait(False)
        End Function

        ''' <summary>Body {symbols: ["AAPL", "^GSPC", …]}: the account's own watch list (each must be a ticker Yahoo knows).</summary>
        Private Async Function StocksWatchlist(rc As RequestContext) As Task
            RequireStocks()
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Try
                Await _h.Stocks.SetWatchlistAsync(rc.User.Name, JsonUtil.GetStringList(b, "symbols"), rc.CancellationToken).ConfigureAwait(False)
            Catch ex As ArgumentException
                Throw ApiException.BadRequest(ex.Message)
            End Try
            Await StocksInfo(rc).ConfigureAwait(False)
        End Function

        ''' <summary>The account watches the defaults again; answers like GET /api/stocks.</summary>
        Private Async Function StocksWatchlistReset(rc As RequestContext) As Task
            RequireStocks()
            Await _h.Stocks.ResetWatchlistAsync(rc.User.Name).ConfigureAwait(False)
            Await StocksInfo(rc).ConfigureAwait(False)
        End Function

    End Class

End Namespace
