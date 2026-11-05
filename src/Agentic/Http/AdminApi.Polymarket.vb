Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Text
Imports System.Threading
Imports Agentic.Utilities
Imports Agentic.Work

Namespace Http

    ' Admin API: the Polymarket tab (prediction markets from Polymarket's public APIs). Every account may look. Live prices reach
    ' the page through its own stream (/api/polymarket/stream), fed by the server's one connection to Polymarket's market channel.
    Partial Friend NotInheritable Class AdminApi

        Private ReadOnly _pmStreams As New Dictionary(Of String, List(Of CancellationTokenSource))(StringComparer.OrdinalIgnoreCase)
        Private Const PmStreamsPerAccount As Integer = 3

        Private Sub RegisterPolymarket(r As Router)
            r.Map("GET", "/api/polymarket", AuthLevel.Read, AddressOf PmInfo)
            r.Map("GET", "/api/polymarket/events", AuthLevel.Read, AddressOf PmEvents)
            r.Map("GET", "/api/polymarket/events/{eventId}", AuthLevel.Read, AddressOf PmEventOne)
            r.Map("GET", "/api/polymarket/events/{eventId}/image", AuthLevel.Read, AddressOf PmImage)
            r.Map("GET", "/api/polymarket/history", AuthLevel.Read, AddressOf PmHistory)
            r.Map("GET", "/api/polymarket/book", AuthLevel.Read, AddressOf PmBook)
            r.Map("GET", "/api/polymarket/trades", AuthLevel.Read, AddressOf PmTrades)
            r.Map("GET", "/api/polymarket/holders", AuthLevel.Read, AddressOf PmHolders)
            r.Map("GET", "/api/polymarket/leaderboard", AuthLevel.Read, AddressOf PmLeaderboard)
            r.Map("GET", "/api/polymarket/wallet", AuthLevel.Read, AddressOf PmWallet)
            r.Map("GET", "/api/polymarket/stream", AuthLevel.Read, AddressOf PmStream)
        End Sub

        Private Sub RequirePolymarket()
            If _h.Polymarket Is Nothing OrElse Not _h.Polymarket.Enabled Then Throw New ApiException(404, "POLYMARKET_OFF", "The Polymarket tab is switched off (Polymarket.Enabled).")
        End Sub

        ''' <summary>Polymarket's answer failed: 502 with its reason.</summary>
        Private Shared Function Upstream(ex As Exception) As ApiException
            Return New ApiException(502, "POLYMARKET_UNAVAILABLE", "Polymarket did not answer: " & TextUtil.Truncate(ex.Message, 200))
        End Function

        ''' <summary>The price shown for an outcome: the midpoint, or the last trade when the spread is wider than 10¢ (as Polymarket shows it).</summary>
        Friend Shared Function ShownPrice(bid As Double?, ask As Double?, last As Double?, fallback As Double?) As Double?
            If bid.HasValue AndAlso ask.HasValue AndAlso ask.Value - bid.Value <= 0.1 Then Return Math.Round((bid.Value + ask.Value) / 2, 4)
            Return If(last, fallback)
        End Function

        ''' <summary>A market for the page, with the live channel's prices where it follows the outcome.</summary>
        Private Function PmMarketDto(m As PmMarket) As Object
            Dim live = m.TokenIds.Select(Function(t) _h.Polymarket.LiveOf(t)).ToList()
            Dim first = live.FirstOrDefault()
            Dim prices = m.Outcomes.Select(Function(o, i)
                                               Dim l = If(i < live.Count, live(i), Nothing)
                                               Dim gamma = If(i < m.Prices.Count, m.Prices(i), CType(Nothing, Double?))
                                               Return If(l Is Nothing, gamma, ShownPrice(l.BestBid, l.BestAsk, l.LastTrade, gamma))
                                           End Function).ToList()
            Return New With {.id = m.Id, .conditionId = m.ConditionId, .slug = m.Slug, .question = m.Question, .name = m.GroupTitle, .outcomes = m.Outcomes,
                             .prices = prices, .tokens = m.TokenIds, .volume = m.Volume, .volume24h = m.Volume24h, .liquidity = m.Liquidity,
                             .bestBid = If(first?.BestBid, m.BestBid), .bestAsk = If(first?.BestAsk, m.BestAsk), .lastTrade = If(first?.LastTrade, m.LastTrade),
                             .lastSide = first?.LastSide, .dayChange = m.DayChange, .endUtc = m.EndUtc, .createdUtc = m.CreatedUtc,
                             .open = PolymarketService.IsOpen(m), .resolution = m.Resolution, .won = live.Select(Function(l) l?.Won).ToList(),
                             .live = first IsNot Nothing, .url = m.Url}
        End Function

        Private Function PmEventDto(e As PmEvent, markets As Integer) As Object
            Return New With {.id = e.Id, .slug = e.Slug, .title = e.Title, .category = e.Category, .tags = e.Tags, .volume = e.Volume, .volume24h = e.Volume24h,
                             .liquidity = e.Liquidity, .endUtc = e.EndUtc, .createdUtc = e.CreatedUtc, .closed = e.Closed, .hasImage = e.HasImage, .url = e.Url,
                             .marketCount = e.MarketCount, .openMarkets = e.Markets.Count,
                             .markets = e.Markets.Take(markets).Select(AddressOf PmMarketDto).ToList()}
        End Function

        ''' <summary>The live channel's state, the categories and the markets that opened while the server listened.</summary>
        Private Async Function PmInfo(rc As RequestContext) As Task
            RequirePolymarket()
            Await rc.OkAsync(New With {.status = _h.Polymarket.Status(), .categories = PolymarketService.Categories.Select(Function(c) New With {.slug = c.Slug, .name = c.Name}).ToList(),
                                       .newMarkets = _h.Polymarket.NewMarkets()}).ConfigureAwait(False)
        End Function

        ''' <summary>Query q, category, status (open | closed), sort (volume24h | volume | liquidity | ending | newest), offset, limit (1-100).</summary>
        Private Async Function PmEvents(rc As RequestContext) As Task
            RequirePolymarket()
            Dim limit = rc.QueryInt("limit", 24, 1, 100), offset = rc.QueryInt("offset", 0, 0, 100000)
            Dim found As (Events As List(Of PmEvent), More As Boolean)
            Try
                found = Await _h.Polymarket.EventsAsync(rc.QueryString("q"), rc.QueryString("category"), rc.QueryString("status"), rc.QueryString("sort"),
                                                       offset, limit, rc.CancellationToken).ConfigureAwait(False)
            Catch ex As Exception When TypeOf ex Is Net.Http.HttpRequestException OrElse TypeOf ex Is Text.Json.JsonException OrElse TypeOf ex Is TaskCanceledException AndAlso Not rc.CancellationToken.IsCancellationRequested
                Throw Upstream(ex)
            End Try
            Await rc.OkAsync(New With {.events = found.Events.Select(Function(e) PmEventDto(e, 4)).ToList(), .more = found.More, .offset = offset,
                                       .next = offset + found.Events.Count, .status = _h.Polymarket.Status()}).ConfigureAwait(False)
        End Function

        ''' <summary>One event with all its markets (query closed=1: closed and resolved ones too).</summary>
        Private Async Function PmEventOne(rc As RequestContext) As Task
            RequirePolymarket()
            Dim ev As PmEvent
            Try
                ev = Await _h.Polymarket.EventAsync(rc.Route("eventId"), rc.QueryString("closed") = "1", rc.CancellationToken).ConfigureAwait(False)
            Catch ex As Exception When TypeOf ex Is Net.Http.HttpRequestException OrElse TypeOf ex Is Text.Json.JsonException OrElse TypeOf ex Is TaskCanceledException AndAlso Not rc.CancellationToken.IsCancellationRequested
                Throw Upstream(ex)
            End Try
            If ev Is Nothing Then Throw ApiException.NotFound("NO_EVENT", "Polymarket has no such event.")
            ' Its markets are followed live while someone looks at it.
            _h.Polymarket.Want(ev.Markets.Take(60).SelectMany(Function(m) m.TokenIds))
            Await rc.OkAsync(New With {.event = PmEventDto(ev, 200), .description = ev.Description}).ConfigureAwait(False)
        End Function

        Private Async Function PmImage(rc As RequestContext) As Task
            RequirePolymarket()
            Dim pic As (Data As Byte(), Type As String)? = Nothing
            Try
                pic = Await _h.Polymarket.PictureAsync(rc.Route("eventId"), rc.CancellationToken).ConfigureAwait(False)
            Catch ex As Exception When Not ExceptionUtil.IsCancellation(ex)
            End Try
            If Not pic.HasValue Then Throw ApiException.NotFound("NO_PICTURE", "This event has no picture that can be shown.")
            Await rc.WriteBytesAsync(200, pic.Value.Type, pic.Value.Data, New Dictionary(Of String, String) From {{"Cache-Control", "private, max-age=86400"}}).ConfigureAwait(False)
        End Function

        ''' <summary>Query token, range (1d | 1w | 1m | max): the outcome's price over time, [unix ms, price 0-1].</summary>
        Private Async Function PmHistory(rc As RequestContext) As Task
            RequirePolymarket()
            Dim token = rc.QueryString("token")
            If Not PolymarketService.IsToken(token) Then Throw ApiException.BadRequest("token is an outcome's token id (digits).")
            Dim range = If(rc.QueryString("range"), "1w")
            Dim points As List(Of Double())
            Try
                points = Await _h.Polymarket.HistoryAsync(token, range, rc.CancellationToken).ConfigureAwait(False)
            Catch ex As Exception When TypeOf ex Is Net.Http.HttpRequestException OrElse TypeOf ex Is Text.Json.JsonException
                Throw Upstream(ex)
            End Try
            Await rc.OkAsync(New With {.token = token, .range = range, .chartRange = PolymarketService.HistoryRange(range).Chart, .points = points}).ConfigureAwait(False)
        End Function

        ''' <summary>Query token, depth (default 12): the outcome's order book (live when the server follows it).</summary>
        Private Async Function PmBook(rc As RequestContext) As Task
            RequirePolymarket()
            Dim token = rc.QueryString("token")
            If Not PolymarketService.IsToken(token) Then Throw ApiException.BadRequest("token is an outcome's token id (digits).")
            Dim book As (Bids As List(Of Double()), Asks As List(Of Double()), Live As Boolean)?
            Try
                book = Await _h.Polymarket.BookAsync(token, rc.QueryInt("depth", 12, 1, 50), rc.CancellationToken).ConfigureAwait(False)
            Catch ex As Exception When TypeOf ex Is Net.Http.HttpRequestException OrElse TypeOf ex Is Text.Json.JsonException
                Throw Upstream(ex)
            End Try
            If Not book.HasValue Then Throw ApiException.NotFound("NO_BOOK", "This outcome has no order book.")
            Dim l = _h.Polymarket.LiveOf(token)
            Await rc.OkAsync(New With {.token = token, .bids = book.Value.Bids, .asks = book.Value.Asks, .live = book.Value.Live,
                                       .lastTrade = l?.LastTrade, .lastSide = l?.LastSide, .lastTradeUtc = l?.LastTradeUtc}).ConfigureAwait(False)
        End Function

        Private Async Function PmTrades(rc As RequestContext) As Task
            RequirePolymarket()
            Dim market = rc.QueryString("market")
            If Not PolymarketService.IsCondition(market) Then Throw ApiException.BadRequest("market is a condition id (0x and 64 hex digits).")
            Try
                Await rc.OkAsync(New With {.trades = Await _h.Polymarket.TradesAsync(market, rc.QueryInt("limit", 25, 1, 100), rc.CancellationToken).ConfigureAwait(False)}).ConfigureAwait(False)
            Catch ex As Exception When TypeOf ex Is Net.Http.HttpRequestException OrElse TypeOf ex Is Text.Json.JsonException
                Throw Upstream(ex)
            End Try
        End Function

        Private Async Function PmHolders(rc As RequestContext) As Task
            RequirePolymarket()
            Dim market = rc.QueryString("market")
            If Not PolymarketService.IsCondition(market) Then Throw ApiException.BadRequest("market is a condition id (0x and 64 hex digits).")
            Try
                Await rc.OkAsync(New With {.holders = Await _h.Polymarket.HoldersAsync(market, rc.QueryInt("limit", 10, 1, 50), rc.CancellationToken).ConfigureAwait(False)}).ConfigureAwait(False)
            Catch ex As Exception When TypeOf ex Is Net.Http.HttpRequestException OrElse TypeOf ex Is Text.Json.JsonException
                Throw Upstream(ex)
            End Try
        End Function

        ''' <summary>Query period (day | week | month | all), sort (pnl | volume), limit.</summary>
        Private Async Function PmLeaderboard(rc As RequestContext) As Task
            RequirePolymarket()
            Try
                Await rc.OkAsync(New With {.traders = Await _h.Polymarket.LeaderboardAsync(rc.QueryString("period"), rc.QueryString("sort"), rc.QueryInt("limit", 50, 1, 100),
                                                                                         rc.CancellationToken).ConfigureAwait(False)}).ConfigureAwait(False)
            Catch ex As Exception When TypeOf ex Is Net.Http.HttpRequestException OrElse TypeOf ex Is Text.Json.JsonException
                Throw Upstream(ex)
            End Try
        End Function

        ''' <summary>Query address: a wallet's positions and activity.</summary>
        Private Async Function PmWallet(rc As RequestContext) As Task
            RequirePolymarket()
            Dim w As (Positions As Text.Json.Nodes.JsonArray, Activity As Text.Json.Nodes.JsonArray)
            Try
                w = Await _h.Polymarket.WalletAsync(rc.QueryString("address"), rc.CancellationToken).ConfigureAwait(False)
            Catch ex As ArgumentException
                Throw ApiException.BadRequest(ex.Message)
            Catch ex As Exception When TypeOf ex Is Net.Http.HttpRequestException OrElse TypeOf ex Is Text.Json.JsonException
                Throw Upstream(ex)
            End Try
            Await rc.OkAsync(New With {.address = rc.QueryString("address").Trim().ToLowerInvariant(), .positions = w.Positions, .activity = w.Activity}).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' Query events (ids, up to 120), book (a token): the page's live stream. Every second, the outcomes of those events whose
        ''' live state changed ("prices"), and the book of the one open ("book") when it changed; the channel's status every 15 s.
        ''' Each account has at most 3 (one more closes its oldest).
        ''' </summary>
        Private Async Function PmStream(rc As RequestContext) As Task
            RequirePolymarket()
            Dim pm = _h.Polymarket
            Dim ids = If(rc.QueryString("events"), "").Split(","c, StringSplitOptions.RemoveEmptyEntries Or StringSplitOptions.TrimEntries).Distinct().Take(120).ToList()
            Dim bookToken = rc.QueryString("book")
            If Not PolymarketService.IsToken(bookToken) Then bookToken = Nothing
            Dim tokens = ids.Select(Function(id) pm.KnownEvent(id)).Where(Function(e) e IsNot Nothing).
                SelectMany(Function(e) e.Markets.Take(60).SelectMany(Function(m) m.TokenIds)).Concat(If(bookToken Is Nothing, Array.Empty(Of String)(), {bookToken})).
                Distinct().Take(800).ToList()
            Dim owner = If(rc.User.Name, "")
            Dim mine As New CancellationTokenSource()
            Dim evicted As CancellationTokenSource = Nothing
            SyncLock _pmStreams
                Dim list As List(Of CancellationTokenSource) = Nothing
                If Not _pmStreams.TryGetValue(owner, list) Then
                    list = New List(Of CancellationTokenSource)()
                    _pmStreams(owner) = list
                End If
                If list.Count >= PmStreamsPerAccount Then
                    evicted = list(0)
                    list.RemoveAt(0)
                End If
                list.Add(mine)
            End SyncLock
            Try
                evicted?.Cancel()
            Catch ex As ObjectDisposedException
            End Try
            Try
                pm.Want(tokens)
                Dim resp = rc.Http.Response
                resp.StatusCode = 200
                resp.ContentType = "text/event-stream; charset=utf-8"
                resp.SendChunked = True
                resp.Headers("Cache-Control") = "no-cache"
                resp.Headers("X-Accel-Buffering") = "no"
                rc.MarkResponded()
                Dim output = resp.OutputStream
                Using linked = CancellationTokenSource.CreateLinkedTokenSource(rc.CancellationToken, mine.Token)
                    Dim ct = linked.Token
                    Dim since = 0L, bookVersion = -1L, ticks = 0
                    Await WriteSseAsync(output, "data: " & JsonUtil.ToJson(New With {.type = "status", .status = pm.Status(), .following = tokens.Count}) & vbLf & vbLf, ct).ConfigureAwait(False)
                    While Not ct.IsCancellationRequested
                        Dim changed = pm.LiveSince(tokens, since)
                        since = changed.Version
                        Dim sb As New StringBuilder()
                        If changed.Items.Count > 0 Then
                            sb.Append("data: ").Append(JsonUtil.ToJson(New With {.type = "prices", .items = changed.Items.Select(Function(l) New With {
                                .t = l.TokenId, .b = l.BestBid, .a = l.BestAsk, .l = l.LastTrade, .s = l.LastSide, .p = ShownPrice(l.BestBid, l.BestAsk, l.LastTrade, Nothing), .w = l.Won}).ToList()})).Append(vbLf).Append(vbLf)
                        End If
                        Dim bl = If(bookToken Is Nothing, Nothing, pm.LiveOf(bookToken))
                        If bl IsNot Nothing AndAlso bl.Version <> bookVersion AndAlso (bl.Bids.Count > 0 OrElse bl.Asks.Count > 0) Then
                            bookVersion = bl.Version
                            Dim book = Await pm.BookAsync(bookToken, 12, ct).ConfigureAwait(False)
                            If book.HasValue Then sb.Append("data: ").Append(JsonUtil.ToJson(New With {.type = "book", .token = bookToken, .bids = book.Value.Bids, .asks = book.Value.Asks,
                                                                                                         .lastTrade = bl.LastTrade, .lastSide = bl.LastSide, .lastTradeUtc = bl.LastTradeUtc})).Append(vbLf).Append(vbLf)
                        End If
                        ticks += 1
                        If ticks Mod 15 = 0 Then
                            sb.Append("data: ").Append(JsonUtil.ToJson(New With {.type = "status", .status = pm.Status(), .following = tokens.Count})).Append(vbLf).Append(vbLf)
                            pm.Want(tokens)
                        End If
                        If sb.Length > 0 Then Await WriteSseAsync(output, sb.ToString(), ct).ConfigureAwait(False)
                        Await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(False)
                    End While
                End Using
            Catch ex As HttpListenerException
                ' the page went away
            Catch ex As IO.IOException
            Catch ex As OperationCanceledException
                ' the page went away, or a newer one of the same account took this stream's place
            Catch ex As ObjectDisposedException
            Finally
                SyncLock _pmStreams
                    Dim list As List(Of CancellationTokenSource) = Nothing
                    If _pmStreams.TryGetValue(owner, list) Then
                        list.Remove(mine)
                        If list.Count = 0 Then _pmStreams.Remove(owner)
                    End If
                End SyncLock
                mine.Dispose()
            End Try
        End Function

    End Class

End Namespace
