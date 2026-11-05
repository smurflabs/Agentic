Option Strict On
Option Explicit On
Option Infer On

Namespace Http

    ''' <summary>The Money (markets) and News APIs in the OpenAPI document (/swagger).</summary>
    Partial Friend NotInheritable Class OpenApi

        Friend Const MarketsTag As String = "Money (markets)"
        Friend Const NewsTag As String = "News"
        Friend Const StocksTag As String = "Stocks"
        Friend Const PolymarketTag As String = "Polymarket"

        Private Const PolymarketTagText As String =
            "Prediction markets from Polymarket's public APIs (no key): the Polymarket tab (every account). Events and their markets come from Gamma " &
            "(open ones unless `status=closed`; closed, inactive and resolved markets are left out); prices, books and history from the CLOB; trades, " &
            "holders, wallets and the leaderboard from the Data API. The server keeps one connection to Polymarket's market channel for the busiest " &
            "markets and every market open on a page; pages get its prices from `GET /api/polymarket/stream` (server-sent events). A price is the " &
            "market's chance of that outcome (0.62 = 62%). Agents have the same data as the `polymarket` tool, and chats when they turn **Polymarket** on."

        Private Const StocksTagText As String =
            "Stock and index quotes from Yahoo Finance's chart API (no key; most US prices are delayed about 15 minutes): the Stocks tab (every account). " &
            "Each account watches its own tickers (the ten defaults until it saves its own); they are read every minute while a market is open, every 15 " &
            "minutes otherwise. Charts of any range come from Yahoo. Tickers " &
            "(for lookup and names) come from NASDAQ Trader's symbol directory, read once a day. With an API key: `GET /api/stocks`, " &
            "`GET /api/stocks/chart?symbol=AAPL&range=1y`, `GET /api/stocks/quote?symbol=JPM` (any ticker). Agents have the same data as the " &
            "`stock_quotes` tool, and chats when they turn **Stocks** on."

        Private Const MarketsTagText As String =
            "Crypto prices and exchange rates, read by the server on its own and kept for charts: the Money tab (every account). Crypto: CoinMarketCap's public " &
            "simple-price API about every minute (USD per coin). Exchange rates: Frankfurter (the European Central Bank's daily reference rates), units of the " &
            "currency per 1 USD. With an API key: `GET /api/markets` for every asset you watch with its changes, `GET /api/markets/history?kind=crypto&symbol=BTC&range=7d` " &
            "for a chart. Each account keeps its own coins and currencies (`PUT /api/markets/watchlist`); until it does it watches the defaults, which administrators " &
            "choose (`PUT /api/markets/defaults`). Agents have the same data as the `market_prices` tool, and chats when they turn **Money** on."

        Private Const NewsTagText As String =
            "News articles collected from RSS and Atom feeds (international, US, UK, Europe, Australia, news agencies through Google News, investigative and business outlets) every few " &
            "minutes, each kept once by its link: the News tab (every account). With an API key: `GET /api/news?q=election&category=international&hours=24`. " &
            "Agents have the same articles as the `news_articles` tool, and chats when they turn **News** on (articles are then cited as [n])."

        Private Shared Sub AddDataDocs(d As Dictionary(Of String, RouteDoc))
            ' A schema node has one parent: each use gets its own.
            Dim quote = Function() Obj(("kind", Str("crypto or fx")), ("symbol", Str("BTC, EUR …")), ("name", Str("Bitcoin, Euro …")),
                            ("price", Num("Crypto: USD per coin. fx: units of the currency per 1 USD")), ("utc", Str("When this price was read (ISO 8601)")),
                            ("change1h", Num("Percent change over 1 hour (crypto; null until there is a price that old)")),
                            ("change24h", Num("Percent change over 24 hours")), ("change7d", Num("Percent change over 7 days")), ("change30d", Num("Percent change over 30 days")))
            d("GET /api/markets") = Doc("Money: every coin and currency you watch now",
                "Latest prices with their changes, your watch lists (custom: your own, else the defaults), the defaults, when each source was last read and any problem reading it.",
                answerSchema:=Obj(("crypto", Arr(quote(), "Watched coins")), ("fx", Arr(quote(), "Watched currencies")), ("base", Str("The currencies' base (USD)")),
                                  ("watchlist", Obj(("crypto", Arr(Str("Symbol"), "Coins")), ("fx", Arr(Str("ISO code"), "Currencies")))),
                                  ("custom", Bool("Your own lists (false: the defaults)")),
                                  ("defaults", Obj(("crypto", Arr(Str("Symbol"), "Coins")), ("fx", Arr(Str("ISO code"), "Currencies")))),
                                  ("status", Obj(("cryptoUpdatedUtc", Str("Last read")), ("fxUpdatedUtc", Str("Last read")), ("cryptoError", Str("Problem, if any")), ("fxError", Str("Problem, if any"))))))
            d("GET /api/markets/history") = Doc("Money: an asset's price over time (for a chart)",
                "points are [unix milliseconds, price], oldest first, at most `points` of them (the last price of each equal slice of time). Every price is kept " &
                "Markets.KeepAllDays (14), then one an hour.",
                query:={QueryParam("kind", "string", "crypto (default) or fx"), QueryParam("symbol", "string", "BTC, ETH, EUR …"),
                        QueryParam("range", "string", "1h, 6h, 24h (default), 7d, 30d, 90d, 1y or all"), QueryParam("points", "integer", "Most points (10-2000, default 360)")},
                answerSchema:=Obj(("kind", Str("crypto or fx")), ("symbol", Str("The asset")), ("range", Str("The range")),
                                  ("points", Arr(Arr(Num("[unix ms, price]"), "One point"), "Oldest first"))))
            d("POST /api/markets/refresh") = Doc("Money: read prices now", "At most every 15 seconds for everyone; answers like GET /api/markets.")
            d("PUT /api/markets/watchlist") = Doc("Money: your watch list",
                "Kept with your account. Coins by symbol, currencies by ISO code; one list may be empty. A coin or currency nobody watches yet must have a price now " &
                "(400 names the ones that do not); a currency's history is fetched when it is first watched. Answers like GET /api/markets.",
                Required(Obj(("crypto", Arr(Str("Coin symbol"), "Coins, e.g. BTC, ETH, SOL")), ("fx", Arr(Str("ISO 4217 code"), "Currencies, e.g. EUR, GBP, JPY")))),
                "{""crypto"":[""BTC"",""ETH"",""SOL""],""fx"":[""EUR"",""GBP"",""JPY""]}")
            d("DELETE /api/markets/watchlist") = Doc("Money: back to the default watch list", "Answers like GET /api/markets.")
            d("PUT /api/markets/defaults") = Doc("Money: the defaults (administrators)",
                "What every account watches until it saves its own. Coins by symbol (CoinMarketCap), currencies by ISO code (Frankfurter); both lists need one.",
                Required(Obj(("crypto", Arr(Str("Coin symbol"), "Coins, e.g. BTC, ETH, SOL")), ("fx", Arr(Str("ISO 4217 code"), "Currencies, e.g. EUR, GBP, JPY"))), "crypto", "fx"),
                "{""crypto"":[""BTC"",""ETH"",""SOL""],""fx"":[""EUR"",""GBP"",""JPY""]}")
            Dim article = Obj(("id", Str("news_…")), ("title", Str("Headline")), ("link", Str("The article")), ("source", Str("BBC World, Reuters …")),
                              ("category", Str("international, us, uk, europe, australia, agencies, investigative or business")), ("summary", Str("The feed's summary (may be null)")),
                              ("author", Str("May be null")), ("imageUrl", Str("A picture (https, may be null)")), ("publishedUtc", Str("ISO 8601")))
            d("GET /api/news") = Doc("News: articles, newest first",
                "Every word of q must be in the title or the summary. Page with before = the last article's publishedUtc. Also the counts per category and source over the same hours.",
                query:={QueryParam("q", "string", "Words"), QueryParam("category", "string", "international, us, uk, europe, australia, agencies, investigative, business (or all)"),
                        QueryParam("source", "string", "One outlet (its name as in /api/news/sources)"), QueryParam("hours", "integer", "How far back (default 72)"),
                        QueryParam("limit", "integer", "How many (1-200, default 60)"), QueryParam("before", "string", "Only older than this (ISO 8601)")},
                answerSchema:=Obj(("articles", Arr(article, "Newest first")), ("more", Bool("There may be older ones")),
                                  ("categories", Obj()), ("sources", Obj()), ("lastRoundUtc", Str("When the feeds were last read"))))
            d("GET /api/news/sources") = Doc("News: the feeds", "Each feed with its category, when it was last read, how many items it had, how many were new, and its error.")
            Dim stock = Function() Obj(("symbol", Str("AAPL, ^GSPC …")), ("name", Str("Apple Inc.")), ("exchange", Str("NasdaqGS, NYSE …")), ("type", Str("EQUITY, ETF, INDEX")),
                                       ("currency", Str("USD")), ("price", Num("Last price")), ("previousClose", Num("The previous session's close")),
                                       ("change", Num("Price minus the previous close")), ("changePct", Num("Change in percent")), ("open", Num("Today's first price")),
                                       ("dayHigh", Num("Today's high")), ("dayLow", Num("Today's low")), ("volume", Int("Shares traded today")),
                                       ("yearHigh", Num("52-week high")), ("yearLow", Num("52-week low")), ("marketTimeUtc", Str("When the price was set")),
                                       ("marketState", Str("pre, open, post or closed")), ("change5d", Num("Percent over 5 sessions")), ("change1m", Num("Percent over a month")),
                                       ("intraday", Arr(Arr(Num("[unix ms, price]"), "One point"), "Today's prices (5-minute steps)")))
            d("GET /api/stocks") = Doc("Stocks: your watched tickers now", "Your own list (custom) or the defaults, in the list's order; a ticker that cannot be read has no price.",
                answerSchema:=Obj(("quotes", Arr(stock(), "Watched tickers")), ("watchlist", Arr(Str("Ticker"), "What you watch")), ("custom", Bool("Your own list (false: the defaults)")),
                                  ("defaults", Arr(Str("Ticker"), "What every account watches until it saves its own")),
                                  ("status", Obj(("updatedUtc", Str("Last read")), ("error", Str("Problem, if any")), ("tickers", Int("Tickers in the directory"))))))
            d("GET /api/stocks/chart") = Doc("Stocks: a ticker's price over a range (any ticker)",
                "points are [unix ms, price], oldest first, from Yahoo (1d in 5-minute steps, 5d 30 minutes, 1mo an hour, 6mo/ytd/1y a day, 5y a week, max a month). " &
                "previousClose is set for 1d (the line the day is measured against).",
                query:={QueryParam("symbol", "string", "AAPL, BRK-B, ^GSPC …"), QueryParam("range", "string", "1d (default), 5d, 1mo, 6mo, ytd, 1y, 5y or max")},
                answerSchema:=Obj(("symbol", Str("The ticker")), ("range", Str("The range")), ("points", Arr(Arr(Num("[unix ms, price]"), "One point"), "Oldest first")),
                                  ("previousClose", Num("1d only")), ("quote", stock())))
            d("GET /api/stocks/quote") = Doc("Stocks: any ticker's quote now", "404 UNKNOWN_TICKER when Yahoo does not know it.",
                query:={QueryParam("symbol", "string", "AAPL, JPM, ^DJI …")}, answerSchema:=stock())
            d("GET /api/stocks/search") = Doc("Stocks: find a ticker", "From NASDAQ Trader's directory (NASDAQ and other US exchanges): symbols starting with q, then names containing it.",
                query:={QueryParam("q", "string", "Symbol or name"), QueryParam("limit", "integer", "How many (1-50, default 12)")})
            d("POST /api/stocks/refresh") = Doc("Stocks: read the quotes now", "At most every 15 seconds for everyone; answers like GET /api/stocks.")
            d("PUT /api/stocks/watchlist") = Doc("Stocks: your watch list", "Kept with your account. Each ticker must be one Yahoo knows (400 names the ones it does not); answers like GET /api/stocks.",
                Required(Obj(("symbols", Arr(Str("Ticker"), "Tickers, indexes with ^"))), "symbols"), "{""symbols"":[""^GSPC"",""AAPL"",""MSFT"",""JPM""]}")
            d("DELETE /api/stocks/watchlist") = Doc("Stocks: back to the default watch list", "Answers like GET /api/stocks.")
            d("GET /api/news/{articleId}/image") = Doc("News: an article's picture", "Fetched through the server from the feed's picture link (https and public hosts only, at most 3 MB).", answer:="image/jpeg")
            d("POST /api/news/refresh") = Doc("News: read every feed now", "At most once a minute for everyone; the feeds are read in the background (started: false when it was too soon).")
            Dim pmMk = Function() Obj(("id", Str("Gamma's market id")), ("conditionId", Str("0x… (trades, holders)")), ("question", Str("The question")),
                                          ("name", Str("Its name within the event (several markets)")), ("outcomes", Arr(Str("Yes, No, or names"), "Outcomes")),
                                          ("prices", Arr(Num("0-1"), "Each outcome's price: its chance")), ("tokens", Arr(Str("Token id"), "Each outcome's token")),
                                          ("volume", Num("USD traded")), ("volume24h", Num("USD traded in 24 hours")), ("liquidity", Num("USD")),
                                          ("bestBid", Num("First outcome")), ("bestAsk", Num("First outcome")), ("lastTrade", Num("First outcome")),
                                          ("endUtc", Str("When it ends")), ("open", Bool("Still trading")), ("live", Bool("Its prices come from the live channel")), ("url", Str("On polymarket.com")))
            Dim pmEv = Function() Obj(("id", Str("Event id")), ("title", Str("The question")), ("category", Str("Politics, Sports, Crypto …")),
                                         ("volume", Num("USD")), ("volume24h", Num("USD")), ("liquidity", Num("USD")), ("endUtc", Str("ISO 8601")),
                                         ("marketCount", Int("Markets in all")), ("openMarkets", Int("Open markets")), ("markets", Arr(pmMk(), "Open markets, likeliest first")), ("url", Str("On polymarket.com")))
            d("GET /api/polymarket") = Doc("Polymarket: the live channel's state", "Connected, last message, reconnects, tokens followed; the categories; markets that opened while the server listened.")
            d("GET /api/polymarket/events") = Doc("Polymarket: events and their markets",
                "Busiest first (or sort), a page at a time; with q, Polymarket's search. Open events only unless status=closed.",
                query:={QueryParam("q", "string", "Words"), QueryParam("category", "string", "politics, sports, crypto, finance, geopolitics, tech, pop-culture, world, economy, elections"),
                        QueryParam("status", "string", "open (default) or closed"), QueryParam("sort", "string", "volume24h (default), volume, liquidity, ending, newest"),
                        QueryParam("offset", "integer", "Skip this many"), QueryParam("limit", "integer", "How many (1-100, default 24)")},
                answerSchema:=Obj(("events", Arr(pmEv(), "Events")), ("more", Bool("There are more")), ("next", Int("offset of the next page"))))
            d("GET /api/polymarket/events/{eventId}") = Doc("Polymarket: one event with all its markets", "By id or slug; closed=1 includes closed and resolved markets. Its markets are followed live while it is open on a page.",
                query:={QueryParam("closed", "string", "1: closed markets too")}, answerSchema:=Obj(("event", pmEv()), ("description", Str("The rules"))))
            d("GET /api/polymarket/events/{eventId}/image") = Doc("Polymarket: an event's picture", "From Polymarket's own storage, through the server.", answer:="image/jpeg")
            d("GET /api/polymarket/history") = Doc("Polymarket: an outcome's price over time", "points are [unix ms, price 0-1], from the CLOB.",
                query:={QueryParam("token", "string", "The outcome's token id"), QueryParam("range", "string", "1d, 1w (default), 1m or max")},
                answerSchema:=Obj(("points", Arr(Arr(Num("[unix ms, price]"), "One point"), "Oldest first"))))
            d("GET /api/polymarket/book") = Doc("Polymarket: an outcome's order book", "bids highest first, asks lowest first, [price, size]; live when the server follows it, else the CLOB's.",
                query:={QueryParam("token", "string", "The outcome's token id"), QueryParam("depth", "integer", "Levels each side (1-50, default 12)")})
            d("GET /api/polymarket/trades") = Doc("Polymarket: a market's latest trades", "From the Data API.", query:={QueryParam("market", "string", "Condition id (0x…)"), QueryParam("limit", "integer", "1-100")})
            d("GET /api/polymarket/holders") = Doc("Polymarket: a market's biggest holders", "Each outcome's top holders, from the Data API.", query:={QueryParam("market", "string", "Condition id (0x…)"), QueryParam("limit", "integer", "1-50")})
            d("GET /api/polymarket/leaderboard") = Doc("Polymarket: the traders' leaderboard", "From the Data API.",
                query:={QueryParam("period", "string", "day, week (default), month, all"), QueryParam("sort", "string", "pnl (default) or volume"), QueryParam("limit", "integer", "1-100")})
            d("GET /api/polymarket/wallet") = Doc("Polymarket: a wallet's positions and activity", "From the Data API.", query:={QueryParam("address", "string", "0x… (40 hex digits)")})
            d("GET /api/polymarket/stream") = Doc("Polymarket: live prices for a page (server-sent events)",
                "Every second, the outcomes of these events whose prices changed (type prices: t token, b bid, a ask, l last trade, p price shown); the open book (type book) when it changed; the channel's status every 15 s. At most 3 per account.",
                query:={QueryParam("events", "string", "Event ids, comma-separated (up to 120)"), QueryParam("book", "string", "A token whose book to send")}, answer:="text/event-stream")
        End Sub

    End Class

End Namespace
