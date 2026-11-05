Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.Globalization
Imports System.Net
Imports System.Net.Http
Imports System.Net.WebSockets
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Agentic.Configuration
Imports Agentic.Logging
Imports Agentic.Utilities

Namespace Work

    ''' <summary>One market of an event: a question with its outcomes (Yes/No, or named ones), their prices (the chance, 0-1) and tokens.</summary>
    Public NotInheritable Class PmMarket
        Public Property Id As String
        Public Property ConditionId As String
        Public Property Slug As String
        Public Property Question As String
        ''' <summary>The market's name within its event ("Trump", "Above $100k"), when the event has several.</summary>
        Public Property GroupTitle As String
        Public Property Outcomes As New List(Of String)
        ''' <summary>Each outcome's price (0-1): the market's chance of it.</summary>
        Public Property Prices As New List(Of Double)
        ''' <summary>Each outcome's CLOB token id (prices, books, history and the live channel are by token).</summary>
        Public Property TokenIds As New List(Of String)
        Public Property Volume As Double?
        Public Property Volume24h As Double?
        Public Property Liquidity As Double?
        ''' <summary>Best bid and ask, last trade and spread of the first outcome (Yes).</summary>
        Public Property BestBid As Double?
        Public Property BestAsk As Double?
        Public Property LastTrade As Double?
        Public Property Spread As Double?
        ''' <summary>The first outcome's price change over a day (0-1 points).</summary>
        Public Property DayChange As Double?
        Public Property EndUtc As DateTime?
        Public Property CreatedUtc As DateTime?
        Public Property Active As Boolean
        Public Property Closed As Boolean
        ''' <summary>The resolution's state (proposed, disputed, resolved …), when there is one.</summary>
        Public Property Resolution As String
        Public Property Url As String
    End Class

    ''' <summary>An event: one question (one market) or a group of them ("Who will win …?"), with its category and totals.</summary>
    Public NotInheritable Class PmEvent
        Public Property Id As String
        Public Property Slug As String
        Public Property Title As String
        Public Property Description As String
        Public Property Category As String
        Public Property Tags As New List(Of String)
        Public Property Volume As Double?
        Public Property Volume24h As Double?
        Public Property Liquidity As Double?
        Public Property EndUtc As DateTime?
        Public Property CreatedUtc As DateTime?
        Public Property Active As Boolean
        Public Property Closed As Boolean
        Public Property HasImage As Boolean
        Public Property Url As String
        ''' <summary>Its markets (open ones only unless closed ones were asked for), busiest first.</summary>
        Public Property Markets As New List(Of PmMarket)
        ''' <summary>All of its markets, open or not.</summary>
        Public Property MarketCount As Integer
        <Text.Json.Serialization.JsonIgnore>
        Public Property ImageUrl As String
    End Class

    ''' <summary>A token's live state from the market channel: best bid and ask, last trade, and its order book.</summary>
    Public NotInheritable Class PmLive
        Public Property TokenId As String
        Public Property Market As String
        Public Property BestBid As Double?
        Public Property BestAsk As Double?
        Public Property LastTrade As Double?
        Public Property LastSide As String
        Public Property LastSize As Double?
        Public Property LastTradeUtc As DateTime?
        Public Property UpdatedUtc As DateTime
        Public Property Version As Long
        ''' <summary>Set when the market resolved: this token won (True) or lost (False).</summary>
        Public Property Won As Boolean?
        Friend ReadOnly Bids As New SortedDictionary(Of Decimal, Decimal)()
        Friend ReadOnly Asks As New SortedDictionary(Of Decimal, Decimal)()

        Public ReadOnly Property Mid As Double?
            Get
                If BestBid.HasValue AndAlso BestAsk.HasValue Then Return Math.Round((BestBid.Value + BestAsk.Value) / 2, 4)
                Return If(LastTrade, BestBid)
            End Get
        End Property
    End Class

    ''' <summary>
    ''' The Polymarket tab's data, from Polymarket's public APIs (no key):
    ''' Gamma lists and searches events and their markets (pages of results joined for more than one page; closed, inactive and
    ''' resolved markets left out unless asked for); the CLOB has prices, books and price history; the Data API has trades,
    ''' holders, wallets and the leaderboard. One WebSocket on the market channel follows the busiest markets and every market
    ''' open on someone's page (or asked about by a tool) live: books, price changes, best bid and ask, trades and new markets.
    ''' It answers PING with PONG every 10 s, reconnects on its own (1 s, doubling to 60 s), and subscribes and unsubscribes as
    ''' pages come and go. Pages get the live state through <see cref="LiveSince"/> (the tab's stream), never by asking Polymarket.
    ''' </summary>
    Public NotInheritable Class PolymarketService
        ''' <summary>Gamma's tags for the tab's categories, in the order the tab shows them.</summary>
        Public Shared ReadOnly Categories As (Slug As String, Name As String)() = {
            ("politics", "Politics"), ("sports", "Sports"), ("crypto", "Crypto"), ("finance", "Finance"), ("geopolitics", "Geopolitics"),
            ("tech", "Tech"), ("pop-culture", "Culture"), ("world", "World"), ("economy", "Economy"), ("elections", "Elections")}
        Private Shared ReadOnly WalletPattern As New Regex("^0x[0-9a-fA-F]{40}$")
        Private Shared ReadOnly ConditionPattern As New Regex("^0x[0-9a-fA-F]{64}$")
        Private Shared ReadOnly TokenPattern As New Regex("^[0-9]{6,90}$")

        Private ReadOnly _opt As PolymarketOptions
        Private ReadOnly _log As StructuredLogger
        Private ReadOnly _http As HttpClient
        Private ReadOnly _cache As New ConcurrentDictionary(Of String, (At As DateTime, Body As String))(StringComparer.Ordinal)
        Private ReadOnly _events As New ConcurrentDictionary(Of String, (At As DateTime, Ev As PmEvent))(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _pictures As New ConcurrentDictionary(Of String, (Data As Byte(), Type As String, At As DateTime))(StringComparer.Ordinal)

        ' The live channel.
        Private ReadOnly _live As New ConcurrentDictionary(Of String, PmLive)(StringComparer.Ordinal)
        Private ReadOnly _wanted As New ConcurrentDictionary(Of String, DateTime)(StringComparer.Ordinal)
        Private _pinned As New HashSet(Of String)(StringComparer.Ordinal)
        Private ReadOnly _subscribed As New HashSet(Of String)(StringComparer.Ordinal)
        Private ReadOnly _newMarkets As New ConcurrentQueue(Of Object)()
        Private ReadOnly _sendLock As New SemaphoreSlim(1, 1)
        Private _socket As ClientWebSocket
        Private _version As Long
        Private _messages As Long

        Public Property Connected As Boolean
        Public Property ConnectedSinceUtc As DateTime?
        Public Property LastMessageUtc As DateTime?
        Public Property Reconnects As Integer
        Public Property LastError As String

        Public Sub New(options As PolymarketOptions, log As StructuredLogger, Optional handler As HttpMessageHandler = Nothing)
            _opt = If(options, New PolymarketOptions())
            _log = log
            _http = If(handler Is Nothing, New HttpClient(New HttpClientHandler With {.AutomaticDecompression = DecompressionMethods.All}), New HttpClient(handler))
            _http.Timeout = TimeSpan.FromSeconds(Math.Max(5, _opt.TimeoutSeconds))
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36")
        End Sub

        Public ReadOnly Property Enabled As Boolean
            Get
                Return _opt.Enabled
            End Get
        End Property

        Public ReadOnly Property Options As PolymarketOptions
            Get
                Return _opt
            End Get
        End Property

        Friend Shared Function IsWallet(text As String) As Boolean
            Return WalletPattern.IsMatch(If(text, "").Trim())
        End Function

        Friend Shared Function IsCondition(text As String) As Boolean
            Return ConditionPattern.IsMatch(If(text, "").Trim())
        End Function

        Friend Shared Function IsToken(text As String) As Boolean
            Return TokenPattern.IsMatch(If(text, "").Trim())
        End Function

        ' ------------------------------------------------------------------ reading Polymarket's JSON

        Private Shared Function Str(o As JsonNode, name As String) As String
            Dim v = TryCast(o, JsonObject)?(name)
            If v Is Nothing Then Return Nothing
            Dim s = If(v.GetValueKind() = JsonValueKind.String, v.GetValue(Of String)(), v.ToJsonString())
            Return If(String.IsNullOrWhiteSpace(s) OrElse s = "null", Nothing, s)
        End Function

        Private Shared Function Num(o As JsonNode, name As String) As Double?
            Dim s = Str(o, name)
            Dim d As Double
            Return If(s IsNot Nothing AndAlso Double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, d), d, CType(Nothing, Double?))
        End Function

        Private Shared Function Bool(o As JsonNode, name As String) As Boolean
            Return String.Equals(Str(o, name), "true", StringComparison.OrdinalIgnoreCase)
        End Function

        Private Shared Function DateOf(o As JsonNode, name As String) As DateTime?
            Dim s = Str(o, name)
            Dim d As DateTimeOffset
            Return If(s IsNot Nothing AndAlso DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, d), d.UtcDateTime, CType(Nothing, DateTime?))
        End Function

        ''' <summary>A list Gamma writes as a JSON string ("[\"Yes\", \"No\"]") or as an array.</summary>
        Private Shared Function Strings(o As JsonNode, name As String) As List(Of String)
            Dim v = TryCast(o, JsonObject)?(name)
            If v Is Nothing Then Return New List(Of String)
            Dim arr = TryCast(v, JsonArray)
            If arr Is Nothing AndAlso v.GetValueKind() = JsonValueKind.String Then
                Try
                    arr = TryCast(JsonNode.Parse(v.GetValue(Of String)()), JsonArray)
                Catch ex As JsonException
                End Try
            End If
            Return If(arr, New JsonArray()).Select(Function(x) If(x Is Nothing, "", If(x.GetValueKind() = JsonValueKind.String, x.GetValue(Of String)(), x.ToJsonString()))).ToList()
        End Function

        ''' <summary>A market as Gamma writes it (outcomes, prices and tokens are JSON strings).</summary>
        Friend Function ParseMarket(m As JsonNode, Optional eventSlug As String = Nothing) As PmMarket
            If m Is Nothing Then Return Nothing
            Dim pm As New PmMarket With {
                .Id = Str(m, "id"), .ConditionId = Str(m, "conditionId"), .Slug = Str(m, "slug"), .Question = Str(m, "question"),
                .GroupTitle = Str(m, "groupItemTitle"), .Outcomes = Strings(m, "outcomes"), .TokenIds = Strings(m, "clobTokenIds"),
                .Volume = If(Num(m, "volumeNum"), Num(m, "volume")), .Volume24h = Num(m, "volume24hr"), .Liquidity = If(Num(m, "liquidityNum"), Num(m, "liquidity")),
                .BestBid = Num(m, "bestBid"), .BestAsk = Num(m, "bestAsk"), .LastTrade = Num(m, "lastTradePrice"), .Spread = Num(m, "spread"),
                .DayChange = Num(m, "oneDayPriceChange"), .EndUtc = DateOf(m, "endDate"), .CreatedUtc = If(DateOf(m, "createdAt"), DateOf(m, "startDate")),
                .Active = Bool(m, "active"), .Closed = Bool(m, "closed"), .Resolution = Str(m, "umaResolutionStatus")}
            pm.Prices = Strings(m, "outcomePrices").Select(Function(p)
                                                            Dim d As Double
                                                            Return If(Double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, d), d, 0)
                                                        End Function).ToList()
            If String.IsNullOrEmpty(eventSlug) Then eventSlug = Str(TryCast(m("events"), JsonArray)?.FirstOrDefault(), "slug")
            pm.Url = SiteLink(eventSlug, pm.Slug)
            Return pm
        End Function

        ''' <summary>
        ''' An event as Gamma writes it, with its markets. Markets that are closed, not active or resolved are left out unless
        ''' withClosed; the rest are ordered by their chance (several) or kept in Gamma's order.
        ''' </summary>
        Friend Function ParseEvent(e As JsonNode, Optional withClosed As Boolean = False) As PmEvent
            If e Is Nothing OrElse Str(e, "id") Is Nothing Then Return Nothing
            Dim tags = If(TryCast(e("tags"), JsonArray), New JsonArray()).Select(Function(t) (Slug:=Str(t, "slug"), Label:=Str(t, "label"))).Where(Function(t) t.Label IsNot Nothing).ToList()
            Dim slug = Str(e, "slug")
            Dim category = Categories.Where(Function(c) tags.Any(Function(t) String.Equals(t.Slug, c.Slug, StringComparison.OrdinalIgnoreCase))).Select(Function(c) c.Name).FirstOrDefault()
            Dim ev As New PmEvent With {
                .Id = Str(e, "id"), .Slug = slug, .Title = Str(e, "title"), .Description = TextUtil.Truncate(Str(e, "description"), 1500),
                .Category = If(category, tags.Select(Function(t) t.Label).FirstOrDefault()), .Tags = tags.Select(Function(t) t.Label).Take(8).ToList(),
                .Volume = Num(e, "volume"), .Volume24h = Num(e, "volume24hr"), .Liquidity = Num(e, "liquidity"), .EndUtc = DateOf(e, "endDate"),
                .CreatedUtc = If(DateOf(e, "creationDate"), DateOf(e, "createdAt")), .Active = Bool(e, "active"), .Closed = Bool(e, "closed"),
                .ImageUrl = If(Str(e, "image"), Str(e, "icon")), .Url = SiteLink(slug, Nothing)}
            ev.HasImage = Not String.IsNullOrEmpty(ev.ImageUrl)
            Dim all = If(TryCast(e("markets"), JsonArray), New JsonArray()).Select(Function(m) ParseMarket(m, slug)).Where(Function(m) m IsNot Nothing).ToList()
            ev.MarketCount = all.Count
            Dim kept = If(withClosed, all, all.Where(AddressOf IsOpen).ToList())
            ' Several markets (who will win …): the likeliest first.
            If kept.Count > 1 Then kept = kept.OrderByDescending(Function(m) If(m.Prices.Count > 0, m.Prices(0), 0)).ThenByDescending(Function(m) If(m.Volume, 0)).ToList()
            ev.Markets = kept
            Return ev
        End Function

        ''' <summary>Open: active, not closed, not resolved, and still trading at a price between 0 and 1.</summary>
        Friend Shared Function IsOpen(m As PmMarket) As Boolean
            If m Is Nothing OrElse Not m.Active OrElse m.Closed Then Return False
            If String.Equals(m.Resolution, "resolved", StringComparison.OrdinalIgnoreCase) Then Return False
            Return Not (m.Prices.Count > 0 AndAlso m.Prices.Any(Function(p) p >= 0.9995))
        End Function

        Private Function SiteLink(eventSlug As String, marketSlug As String) As String
            If String.IsNullOrEmpty(eventSlug) Then Return Nothing
            Return _opt.SiteUrl.TrimEnd("/"c) & "/event/" & Uri.EscapeDataString(eventSlug) & If(String.IsNullOrEmpty(marketSlug) OrElse marketSlug = eventSlug, "", "/" & Uri.EscapeDataString(marketSlug))
        End Function

        ' ------------------------------------------------------------------ HTTP

        ''' <summary>GET a JSON answer, kept for keep (the same question within it is answered from memory).</summary>
        Private Async Function GetJsonAsync(url As String, keep As TimeSpan, ct As CancellationToken) As Task(Of JsonNode)
            Dim hit As (At As DateTime, Body As String)
            If keep > TimeSpan.Zero AndAlso _cache.TryGetValue(url, hit) AndAlso DateTime.UtcNow - hit.At < keep Then Return JsonNode.Parse(hit.Body)
            Using resp = Await _http.GetAsync(url, ct).ConfigureAwait(False)
                Dim body = Await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(False)
                If resp.StatusCode = HttpStatusCode.NotFound Then Return Nothing
                If Not resp.IsSuccessStatusCode Then
                    Dim why = Str(TryCastParse(body), "error")
                    Throw New HttpRequestException($"Polymarket answered {CInt(resp.StatusCode)}{If(why Is Nothing, "", ": " & TextUtil.Truncate(why, 160))}")
                End If
                If keep > TimeSpan.Zero Then
                    _cache(url) = (DateTime.UtcNow, body)
                    If _cache.Count > 600 Then
                        For Each old In _cache.OrderBy(Function(kv) kv.Value.At).Take(200).ToList()
                            _cache.TryRemove(old.Key, Nothing)
                        Next
                    End If
                End If
                Return JsonNode.Parse(body)
            End Using
        End Function

        Private Shared Function TryCastParse(body As String) As JsonNode
            Try
                Return JsonNode.Parse(body)
            Catch ex As JsonException
                Return Nothing
            End Try
        End Function

        Private Async Function PostJsonAsync(url As String, payload As Object, ct As CancellationToken) As Task(Of JsonNode)
            Using content As New StringContent(JsonUtil.ToJson(payload), Encoding.UTF8, "application/json")
                Using resp = Await _http.PostAsync(url, content, ct).ConfigureAwait(False)
                    Dim body = Await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(False)
                    If Not resp.IsSuccessStatusCode Then Throw New HttpRequestException($"Polymarket answered {CInt(resp.StatusCode)}")
                    Return JsonNode.Parse(body)
                End Using
            End Using
        End Function

        Private Function Gamma(path As String) As String
            Return _opt.GammaUrl.TrimEnd("/"c) & path
        End Function

        ' ------------------------------------------------------------------ events (Gamma)

        ''' <summary>Gamma's order for each of the tab's sorts.</summary>
        Friend Shared Function OrderOf(sort As String) As (Field As String, Ascending As Boolean)
            Select Case If(sort, "").ToLowerInvariant()
                Case "volume" : Return ("volume", False)
                Case "liquidity" : Return ("liquidity", False)
                Case "ending" : Return ("endDate", True)
                Case "newest" : Return ("startDate", False)
                Case Else : Return ("volume24hr", False)
            End Select
        End Function

        ''' <summary>
        ''' Events, a page at a time (offset, limit up to 200: more than one of Gamma's pages are joined). With words: Polymarket's
        ''' search (open events, or closed ones). Category: one of <see cref="Categories"/>' slugs. status: open (default) or
        ''' closed. More: there are more after these.
        ''' </summary>
        Public Async Function EventsAsync(query As String, category As String, status As String, sort As String, offset As Integer, limit As Integer,
                                          ct As CancellationToken) As Task(Of (Events As List(Of PmEvent), More As Boolean))
            limit = Math.Clamp(limit, 1, 200)
            offset = Math.Max(0, offset)
            Dim closed = String.Equals(status, "closed", StringComparison.OrdinalIgnoreCase)
            Dim cat = Categories.FirstOrDefault(Function(c) String.Equals(c.Slug, category, StringComparison.OrdinalIgnoreCase)).Slug
            Dim found As New List(Of PmEvent)
            Dim more = False
            If Not String.IsNullOrWhiteSpace(query) Then
                ' Search answers pages of a fixed size; the pages that cover offset..offset+limit are joined.
                Const pageSize As Integer = 20
                Dim page = offset \ pageSize + 1, skip = offset Mod pageSize
                Do
                    Dim url = Gamma($"/public-search?q={Uri.EscapeDataString(query.Trim())}&limit_per_type={pageSize}&page={page}&events_status={If(closed, "closed", "active")}&search_tags=false&search_profiles=false")
                    Dim j = Await GetJsonAsync(url, TimeSpan.FromSeconds(60), ct).ConfigureAwait(False)
                    Dim list = If(TryCast(j?("events"), JsonArray), New JsonArray())
                    For Each e In list.Skip(skip)
                        Dim ev = ParseEvent(e, closed)
                        If ev Is Nothing OrElse (Not closed AndAlso ev.Markets.Count = 0) Then Continue For
                        If cat IsNot Nothing AndAlso Not EventHasTag(e, cat) Then Continue For
                        found.Add(ev)
                    Next
                    skip = 0
                    more = String.Equals(Str(j?("pagination"), "hasMore"), "true", StringComparison.OrdinalIgnoreCase)
                    page += 1
                Loop While more AndAlso found.Count < limit AndAlso page <= offset \ pageSize + 10
            Else
                Dim order = OrderOf(sort)
                If closed AndAlso order.Field = "endDate" Then order = ("endDate", False)
                Const pageSize As Integer = 100
                Dim at = offset
                Do
                    Dim n = Math.Min(pageSize, limit - found.Count)
                    Dim url = Gamma($"/events?limit={n + 1}&offset={at}&order={order.Field}&ascending={order.Ascending.ToString().ToLowerInvariant()}" &
                                    If(closed, "&closed=true", "&active=true&closed=false&archived=false") & If(cat Is Nothing, "", "&tag_slug=" & cat) &
                                    If(order.Field = "endDate" AndAlso Not closed, "&end_date_min=" & DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), ""))
                    Dim list = If(TryCast(Await GetJsonAsync(url, TimeSpan.FromSeconds(30), ct).ConfigureAwait(False), JsonArray), New JsonArray())
                    For Each e In list.Take(n)
                        Dim ev = ParseEvent(e, closed)
                        If ev IsNot Nothing AndAlso (closed OrElse ev.Markets.Count > 0) Then found.Add(ev)
                    Next
                    more = list.Count > n
                    at += n
                Loop While more AndAlso found.Count < limit
            End If
            For Each ev In found
                _events(ev.Id) = (DateTime.UtcNow, ev)
            Next
            TrimEvents()
            Return (found.Take(limit).ToList(), more OrElse found.Count > limit)
        End Function

        Private Shared Function EventHasTag(e As JsonNode, slug As String) As Boolean
            Return If(TryCast(e("tags"), JsonArray), New JsonArray()).Any(Function(t) String.Equals(Str(t, "slug"), slug, StringComparison.OrdinalIgnoreCase))
        End Function

        Private Sub TrimEvents()
            If _events.Count <= 2000 Then Return
            For Each old In _events.OrderBy(Function(kv) kv.Value.At).Take(500).ToList()
                _events.TryRemove(old.Key, Nothing)
            Next
        End Sub

        ''' <summary>One event with all its markets (closed ones too when withClosed), by id or slug; Nothing when there is none.</summary>
        Public Async Function EventAsync(idOrSlug As String, withClosed As Boolean, ct As CancellationToken) As Task(Of PmEvent)
            Dim key = If(idOrSlug, "").Trim()
            If key.Length = 0 OrElse key.Length > 200 Then Return Nothing
            Dim url = If(key.All(AddressOf Char.IsDigit), Gamma("/events/" & key), Gamma("/events/slug/" & Uri.EscapeDataString(key)))
            Dim ev = ParseEvent(Await GetJsonAsync(url, TimeSpan.FromSeconds(20), ct).ConfigureAwait(False), withClosed)
            ' Kept for the page's stream (which tokens to follow): the open markets only.
            If ev IsNot Nothing AndAlso Not withClosed Then _events(ev.Id) = (DateTime.UtcNow, ev)
            Return ev
        End Function

        ''' <summary>The events last served by id (for the tab's stream: which tokens to follow).</summary>
        Friend Function KnownEvent(id As String) As PmEvent
            Dim hit As (At As DateTime, Ev As PmEvent)
            Return If(_events.TryGetValue(If(id, ""), hit), hit.Ev, Nothing)
        End Function

        ''' <summary>A market by its id, slug, condition id or Polymarket link (Nothing when there is none).</summary>
        Public Async Function MarketAsync(key As String, ct As CancellationToken) As Task(Of (Market As PmMarket, Ev As PmEvent)?)
            Dim k = If(key, "").Trim()
            If k.Length = 0 Then Return Nothing
            ' A link: polymarket.com/event/<event>[/<market>].
            Dim link = Regex.Match(k, "/event/([^/?#]+)(?:/([^/?#]+))?")
            If link.Success Then
                Dim ev = Await EventAsync(Uri.UnescapeDataString(link.Groups(1).Value), True, ct).ConfigureAwait(False)
                If ev Is Nothing Then Return Nothing
                Dim wanted = If(link.Groups(2).Success, Uri.UnescapeDataString(link.Groups(2).Value), Nothing)
                Dim m = If(wanted Is Nothing, ev.Markets.FirstOrDefault(AddressOf IsOpen), ev.Markets.FirstOrDefault(Function(x) x.Slug = wanted))
                If m Is Nothing Then m = ev.Markets.FirstOrDefault()
                If m Is Nothing Then Return Nothing
                Return (m, ev)
            End If
            Dim url = If(IsCondition(k), Gamma("/markets?condition_ids=" & k), If(k.All(AddressOf Char.IsDigit), Gamma("/markets/" & k), Gamma("/markets?slug=" & Uri.EscapeDataString(k))))
            Dim j = Await GetJsonAsync(url, TimeSpan.FromSeconds(20), ct).ConfigureAwait(False)
            Dim node = If(TryCast(j, JsonArray)?.FirstOrDefault(), If(TypeOf j Is JsonObject, j, Nothing))
            Dim market = ParseMarket(node)
            If market Is Nothing OrElse market.Id Is Nothing Then Return Nothing
            Dim evNode = TryCast(node("events"), JsonArray)?.FirstOrDefault()
            Return (market, If(evNode Is Nothing, Nothing, ParseEvent(evNode, True)))
        End Function

        ' ------------------------------------------------------------------ prices (CLOB)

        ''' <summary>The tab's chart ranges as the CLOB's interval and point spacing (minutes), and the chart's own range name.</summary>
        Friend Shared Function HistoryRange(range As String) As (Interval As String, Fidelity As Integer, Chart As String)
            Select Case If(range, "").ToLowerInvariant()
                Case "1d" : Return ("1d", 5, "1d")
                Case "1m" : Return ("1m", 180, "30d")
                Case "max", "all" : Return ("max", 1440, "max")
                Case Else : Return ("1w", 60, "7d")
            End Select
        End Function

        ''' <summary>A token's price over a range: [unix ms, price 0-1], oldest first.</summary>
        Public Async Function HistoryAsync(token As String, range As String, ct As CancellationToken) As Task(Of List(Of Double()))
            If Not IsToken(token) Then Return New List(Of Double())
            Dim r = HistoryRange(range)
            Dim j = Await GetJsonAsync($"{_opt.ClobUrl.TrimEnd("/"c)}/prices-history?market={token}&interval={r.Interval}&fidelity={r.Fidelity}", TimeSpan.FromSeconds(60), ct).ConfigureAwait(False)
            Return If(TryCast(j?("history"), JsonArray), New JsonArray()).Select(Function(p) New Double() {If(Num(p, "t"), 0) * 1000, If(Num(p, "p"), 0)}).
                Where(Function(p) p(0) > 0).ToList()
        End Function

        ''' <summary>
        ''' Bid, ask, midpoint and last trade for tokens: the live channel's where it follows them, the CLOB's (one request each
        ''' for all of them) for the rest.
        ''' </summary>
        Public Async Function QuotesAsync(tokens As IEnumerable(Of String), ct As CancellationToken) As Task(Of Dictionary(Of String, (Bid As Double?, Ask As Double?, Mid As Double?, Last As Double?)))
            Dim result As New Dictionary(Of String, (Bid As Double?, Ask As Double?, Mid As Double?, Last As Double?))(StringComparer.Ordinal)
            Dim missing As New List(Of String)
            For Each t In tokens.Where(AddressOf IsToken).Distinct().Take(100)
                Dim l As PmLive = Nothing
                If _live.TryGetValue(t, l) AndAlso (l.BestBid.HasValue OrElse l.BestAsk.HasValue) Then
                    result(t) = (l.BestBid, l.BestAsk, l.Mid, l.LastTrade)
                Else
                    missing.Add(t)
                End If
            Next
            If missing.Count = 0 Then Return result
            Dim clob = _opt.ClobUrl.TrimEnd("/"c)
            Dim prices = Await PostJsonAsync(clob & "/prices", missing.SelectMany(Function(t) {New With {.token_id = t, .side = "BUY"}, New With {.token_id = t, .side = "SELL"}}).ToList(), ct).ConfigureAwait(False)
            Dim mids = Await PostJsonAsync(clob & "/midpoints", missing.Select(Function(t) New With {.token_id = t}).ToList(), ct).ConfigureAwait(False)
            Dim lasts = TryCast(Await PostJsonAsync(clob & "/last-trades-prices", missing.Select(Function(t) New With {.token_id = t}).ToList(), ct).ConfigureAwait(False), JsonArray)
            For Each t In missing
                Dim last = lasts?.FirstOrDefault(Function(x) Str(x, "token_id") = t)
                ' A BUY price is what buyers bid (the best bid); a SELL price what sellers ask.
                result(t) = (Num(prices?(t), "BUY"), Num(prices?(t), "SELL"), Num(mids, t), Num(last, "price"))
            Next
            Return result
        End Function

        ''' <summary>A token's order book, the best levels each side (bids highest first, asks lowest first): live, else the CLOB's.</summary>
        Public Async Function BookAsync(token As String, depth As Integer, ct As CancellationToken) As Task(Of (Bids As List(Of Double()), Asks As List(Of Double()), Live As Boolean)?)
            If Not IsToken(token) Then Return Nothing
            Want({token})
            Dim l As PmLive = Nothing
            If _live.TryGetValue(token, l) AndAlso (l.Bids.Count > 0 OrElse l.Asks.Count > 0) Then
                SyncLock l
                    Return (l.Bids.Reverse().Take(depth).Select(Function(kv) New Double() {CDbl(kv.Key), CDbl(kv.Value)}).ToList(),
                            l.Asks.Take(depth).Select(Function(kv) New Double() {CDbl(kv.Key), CDbl(kv.Value)}).ToList(), True)
                End SyncLock
            End If
            Dim j = Await GetJsonAsync($"{_opt.ClobUrl.TrimEnd("/"c)}/book?token_id={token}", TimeSpan.FromSeconds(5), ct).ConfigureAwait(False)
            If j Is Nothing Then Return Nothing
            Dim side = Function(name As String) If(TryCast(j(name), JsonArray), New JsonArray()).Select(Function(x) New Double() {If(Num(x, "price"), 0), If(Num(x, "size"), 0)})
            Return (side("bids").OrderByDescending(Function(x) x(0)).Take(depth).ToList(), side("asks").OrderBy(Function(x) x(0)).Take(depth).ToList(), False)
        End Function

        ' ------------------------------------------------------------------ trading activity (Data API)

        Private Function DataUrl(path As String) As String
            Return _opt.DataUrl.TrimEnd("/"c) & path
        End Function

        ''' <summary>A market's latest trades (newest first).</summary>
        Public Async Function TradesAsync(conditionId As String, limit As Integer, ct As CancellationToken) As Task(Of JsonArray)
            If Not IsCondition(conditionId) Then Return New JsonArray()
            Dim j = Await GetJsonAsync(DataUrl($"/v2/trades?condition={conditionId}&limit={Math.Clamp(limit, 1, 100)}"), TimeSpan.FromSeconds(10), ct).ConfigureAwait(False)
            Return Pick(j?("data"), "side", "outcome", "outcome_index", "size", "price", "timestamp", "name", "pseudonym", "proxy_wallet")
        End Function

        ''' <summary>A market's biggest holders of each outcome.</summary>
        Public Async Function HoldersAsync(conditionId As String, limit As Integer, ct As CancellationToken) As Task(Of JsonArray)
            If Not IsCondition(conditionId) Then Return New JsonArray()
            Dim j = Await GetJsonAsync(DataUrl($"/v2/holders?condition={conditionId}&limit={Math.Clamp(limit, 1, 50)}"), TimeSpan.FromSeconds(60), ct).ConfigureAwait(False)
            Dim result As New JsonArray()
            For Each tok In If(TryCast(j?("data"), JsonArray), New JsonArray())
                result.Add(New JsonObject From {{"token_id", Str(tok, "token_id")},
                                                {"holders", Pick(tok("holders"), "name", "pseudonym", "amount", "outcome_index", "proxy_wallet", "verified")}})
            Next
            Return result
        End Function

        ''' <summary>The traders' leaderboard: period day, week, month or all; by profit (PNL) or volume (VOLUME).</summary>
        Public Async Function LeaderboardAsync(period As String, sort As String, limit As Integer, ct As CancellationToken) As Task(Of JsonArray)
            Dim p = If({"day", "week", "month", "all"}.Contains(If(period, "").ToLowerInvariant()), period.ToLowerInvariant(), "week")
            Dim s = If(String.Equals(sort, "volume", StringComparison.OrdinalIgnoreCase), "VOLUME", "PNL")
            Dim j = Await GetJsonAsync(DataUrl($"/v2/leaderboard?time_period={p}&sort_by={s}&limit={Math.Clamp(limit, 1, 100)}"), TimeSpan.FromMinutes(2), ct).ConfigureAwait(False)
            Return Pick(j?("data"), "rank", "user_id", "user_name", "pnl", "volume", "verified")
        End Function

        ''' <summary>A wallet's open positions and latest activity (trades, redemptions …).</summary>
        Public Async Function WalletAsync(address As String, ct As CancellationToken) As Task(Of (Positions As JsonArray, Activity As JsonArray))
            If Not IsWallet(address) Then Throw New ArgumentException("A wallet is an address like 0x56687bf447db6ffa42ffe2204a05edaa20f55839.")
            Dim a = address.Trim().ToLowerInvariant()
            Dim pos = Await GetJsonAsync(DataUrl($"/v2/positions?user={a}&limit=50"), TimeSpan.FromSeconds(30), ct).ConfigureAwait(False)
            Dim act = Await GetJsonAsync(DataUrl($"/v2/activity?user={a}&limit=50"), TimeSpan.FromSeconds(30), ct).ConfigureAwait(False)
            Return (Pick(pos?("data"), "title", "slug", "event_slug", "outcome", "current_size", "avg_price", "current_price", "current_value", "total_pnl", "percent_pnl", "status", "redeemable"),
                    Pick(act?("data"), "timestamp", "type", "title", "slug", "event_slug", "outcome", "side", "size", "price", "usdc_size"))
        End Function

        ''' <summary>The named fields of each object in a list (what the tab and tools use, without everything else).</summary>
        Private Shared Function Pick(list As JsonNode, ParamArray names As String()) As JsonArray
            Dim result As New JsonArray()
            For Each o In If(TryCast(list, JsonArray), New JsonArray()).OfType(Of JsonObject)()
                Dim row As New JsonObject()
                For Each n In names
                    Dim v = o(n)
                    If v IsNot Nothing Then row(n) = v.DeepClone()
                Next
                result.Add(row)
            Next
            Return result
        End Function

        ' ------------------------------------------------------------------ pictures

        ''' <summary>An event's picture (from Polymarket's own storage only), kept for the next page.</summary>
        Public Async Function PictureAsync(eventId As String, ct As CancellationToken) As Task(Of (Data As Byte(), Type As String)?)
            Dim hit As (Data As Byte(), Type As String, At As DateTime)
            If _pictures.TryGetValue(If(eventId, ""), hit) Then Return (hit.Data, hit.Type)
            Dim ev = If(KnownEvent(eventId), Await EventAsync(eventId, True, ct).ConfigureAwait(False))
            Dim u As Uri = Nothing
            If ev Is Nothing OrElse Not Uri.TryCreate(ev.ImageUrl, UriKind.Absolute, u) OrElse u.Scheme <> Uri.UriSchemeHttps Then Return Nothing
            If Not (u.Host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase) OrElse u.Host.EndsWith("polymarket.com", StringComparison.OrdinalIgnoreCase)) Then Return Nothing
            Using resp = Await _http.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(False)
                Dim type = If(resp.Content.Headers.ContentType?.MediaType, "")
                If Not resp.IsSuccessStatusCode OrElse Not type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) OrElse type.Contains("svg") Then Return Nothing
                Dim data = Await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(False)
                If data.Length > 2_000_000 Then Return Nothing
                If _pictures.Count >= 400 Then
                    For Each old In _pictures.OrderBy(Function(kv) kv.Value.At).Take(100).ToList()
                        _pictures.TryRemove(old.Key, Nothing)
                    Next
                End If
                _pictures(eventId) = (data, type, DateTime.UtcNow)
                Return (data, type)
            End Using
        End Function

        ' ------------------------------------------------------------------ the live channel (WebSocket)

        ''' <summary>Follows these tokens live for the next 10 minutes (a page showing them, a tool asking about them).</summary>
        Public Sub Want(tokens As IEnumerable(Of String))
            Dim now = DateTime.UtcNow
            For Each t In tokens.Where(AddressOf IsToken)
                _wanted(t) = now
            Next
        End Sub

        ''' <summary>The tokens to follow: the busiest markets', then the most recently wanted, at most MaxLiveTokens.</summary>
        Friend Function DesiredTokens() As List(Of String)
            Dim cutoff = DateTime.UtcNow.AddMinutes(-10)
            For Each kv In _wanted.Where(Function(x) x.Value < cutoff).ToList()
                _wanted.TryRemove(kv.Key, Nothing)
            Next
            Dim pinned = _pinned
            Return pinned.Concat(_wanted.OrderByDescending(Function(kv) kv.Value).Select(Function(kv) kv.Key)).Distinct(StringComparer.Ordinal).
                Take(Math.Max(10, _opt.MaxLiveTokens)).ToList()
        End Function

        Public Sub Start(ct As CancellationToken)
            If Not _opt.Enabled Then Return
            Dim unused = Task.Run(Function() LiveLoopAsync(ct))
            Dim unused2 = Task.Run(Function() PinLoopAsync(ct))
        End Sub

        ''' <summary>Every 5 minutes: the busiest open markets' tokens are followed live.</summary>
        Private Async Function PinLoopAsync(ct As CancellationToken) As Task
            While Not ct.IsCancellationRequested
                Try
                    Dim url = Gamma($"/markets?active=true&closed=false&archived=false&order=volume24hr&ascending=false&limit={Math.Clamp(_opt.LiveTopMarkets, 1, 500)}")
                    Dim list = If(TryCast(Await GetJsonAsync(url, TimeSpan.FromMinutes(4), ct).ConfigureAwait(False), JsonArray), New JsonArray())
                    _pinned = New HashSet(Of String)(list.Select(Function(m) ParseMarket(m)).Where(AddressOf IsOpen).SelectMany(Function(m) m.TokenIds), StringComparer.Ordinal)
                Catch ex As Exception When Not ExceptionUtil.IsCancellation(ex)
                    _log?.Warn("Polymarket", "Reading the busiest markets failed: " & ex.Message)
                End Try
                Try
                    Await Task.Delay(TimeSpan.FromMinutes(5), ct).ConfigureAwait(False)
                Catch ex As OperationCanceledException
                    Exit While
                End Try
            End While
        End Function

        ''' <summary>Connects, subscribes, reads; reconnects after a failure (1 s, doubling to 60 s).</summary>
        Private Async Function LiveLoopAsync(ct As CancellationToken) As Task
            Dim wait = 1
            While Not ct.IsCancellationRequested
                Dim started = DateTime.UtcNow
                Try
                    Await RunSocketAsync(ct).ConfigureAwait(False)
                Catch ex As Exception When Not ExceptionUtil.IsCancellation(ex) OrElse Not ct.IsCancellationRequested
                    LastError = TextUtil.Truncate(ex.Message, 200)
                End Try
                Connected = False
                If ct.IsCancellationRequested Then Exit While
                Reconnects += 1
                ' A connection that lasted a while starts the waits over.
                If DateTime.UtcNow - started > TimeSpan.FromMinutes(2) Then wait = 1
                Try
                    Await Task.Delay(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(False)
                Catch ex As OperationCanceledException
                    Exit While
                End Try
                wait = Math.Min(60, wait * 2)
            End While
        End Function

        Private Async Function RunSocketAsync(ct As CancellationToken) As Task
            ' Nothing to follow yet (the busiest markets not read): wait for some.
            While DesiredTokens().Count = 0
                Await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(False)
            End While
            Using ws As New ClientWebSocket(), linked = CancellationTokenSource.CreateLinkedTokenSource(ct)
                ws.Options.SetRequestHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36")
                ws.Options.KeepAliveInterval = TimeSpan.Zero
                Using connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                    connectCts.CancelAfter(TimeSpan.FromSeconds(20))
                    Await ws.ConnectAsync(New Uri(_opt.WebSocketUrl), connectCts.Token).ConfigureAwait(False)
                End Using
                _socket = ws
                SyncLock _subscribed
                    _subscribed.Clear()
                End SyncLock
                Connected = True
                ConnectedSinceUtc = DateTime.UtcNow
                LastMessageUtc = DateTime.UtcNow
                LastError = Nothing
                Await SyncSubscriptionsAsync(ws, True, linked.Token).ConfigureAwait(False)
                Dim keeper = KeepAsync(ws, linked)
                Dim failure As Exception = Nothing
                Try
                    Await ReceiveAsync(ws, linked.Token).ConfigureAwait(False)
                Catch ex As Exception
                    failure = ex
                End Try
                linked.Cancel()
                Try
                    Await keeper.ConfigureAwait(False)
                Catch ex As Exception
                End Try
                _socket = Nothing
                If failure IsNot Nothing Then Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw()
            End Using
        End Function

        ''' <summary>PING every 10 s; subscriptions follow what is wanted every 5 s; 40 s without a word ends the connection.</summary>
        Private Async Function KeepAsync(ws As ClientWebSocket, linked As CancellationTokenSource) As Task
            Dim ct = linked.Token
            Dim tick = 0
            Try
                While Not ct.IsCancellationRequested AndAlso ws.State = WebSocketState.Open
                    Await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(False)
                    tick += 1
                    If tick Mod 2 = 0 Then Await SendAsync(ws, "PING", ct).ConfigureAwait(False)
                    Await SyncSubscriptionsAsync(ws, False, ct).ConfigureAwait(False)
                    If LastMessageUtc.HasValue AndAlso DateTime.UtcNow - LastMessageUtc.Value > TimeSpan.FromSeconds(40) Then
                        LastError = "No message for 40 s: connecting again."
                        ws.Abort()
                        Exit While
                    End If
                End While
            Catch ex As Exception When Not ct.IsCancellationRequested
                ' A send that failed: the connection is gone; reading ends and the loop connects again.
                LastError = TextUtil.Truncate(ex.Message, 200)
                ws.Abort()
            End Try
        End Function

        Private Async Function SendAsync(ws As ClientWebSocket, text As String, ct As CancellationToken) As Task
            Await _sendLock.WaitAsync(ct).ConfigureAwait(False)
            Try
                Await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, True, ct).ConfigureAwait(False)
            Finally
                _sendLock.Release()
            End Try
        End Function

        ''' <summary>Subscribes to the tokens now wanted and unsubscribes from the rest (in pieces of 200; the first message on a new connection names the channel).</summary>
        Private Async Function SyncSubscriptionsAsync(ws As ClientWebSocket, first As Boolean, ct As CancellationToken) As Task
            Dim desired = DesiredTokens()
            Dim add As List(Of String), drop As List(Of String)
            SyncLock _subscribed
                add = desired.Where(Function(t) Not _subscribed.Contains(t)).ToList()
                Dim keep As New HashSet(Of String)(desired, StringComparer.Ordinal)
                drop = _subscribed.Where(Function(t) Not keep.Contains(t)).ToList()
            End SyncLock
            Dim isFirst = first
            For Each part In add.Chunk(200)
                Dim msg = If(isFirst, JsonUtil.ToJson(New With {.assets_ids = part, .type = "market", .custom_feature_enabled = True}),
                                      JsonUtil.ToJson(New With {.operation = "subscribe", .assets_ids = part, .custom_feature_enabled = True}))
                Await SendAsync(ws, msg, ct).ConfigureAwait(False)
                isFirst = False
                SyncLock _subscribed
                    For Each t In part
                        _subscribed.Add(t)
                    Next
                End SyncLock
            Next
            For Each part In drop.Chunk(200)
                Await SendAsync(ws, JsonUtil.ToJson(New With {.operation = "unsubscribe", .assets_ids = part}), ct).ConfigureAwait(False)
                SyncLock _subscribed
                    For Each t In part
                        _subscribed.Remove(t)
                        _live.TryRemove(t, Nothing)
                    Next
                End SyncLock
            Next
        End Function

        Private Async Function ReceiveAsync(ws As ClientWebSocket, ct As CancellationToken) As Task
            Dim buffer(65535) As Byte
            Dim message As New IO.MemoryStream()
            While ws.State = WebSocketState.Open AndAlso Not ct.IsCancellationRequested
                Dim r = Await ws.ReceiveAsync(New ArraySegment(Of Byte)(buffer), ct).ConfigureAwait(False)
                If r.MessageType = WebSocketMessageType.Close Then
                    LastError = $"Polymarket closed the connection ({r.CloseStatus}{If(String.IsNullOrEmpty(r.CloseStatusDescription), "", ": " & r.CloseStatusDescription)})."
                    Exit While
                End If
                message.Write(buffer, 0, r.Count)
                If message.Length > 8_000_000 Then
                    message.SetLength(0)
                    Continue While
                End If
                If Not r.EndOfMessage Then Continue While
                Dim text = Encoding.UTF8.GetString(message.GetBuffer(), 0, CInt(message.Length))
                message.SetLength(0)
                LastMessageUtc = DateTime.UtcNow
                Interlocked.Increment(_messages)
                Try
                    ApplyMessage(text)
                Catch ex As Exception When TypeOf ex Is JsonException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is FormatException
                    ' One message that cannot be read does not end the connection.
                End Try
            End While
        End Function

        ''' <summary>One message from the market channel: a book, price changes, a trade, a best bid and ask, a new or resolved market.</summary>
        Friend Sub ApplyMessage(text As String)
            If String.IsNullOrWhiteSpace(text) OrElse text = "PONG" Then Return
            Dim root = JsonNode.Parse(text)
            Dim items = If(TryCast(root, JsonArray)?.ToList(), New List(Of JsonNode) From {root})
            For Each m In items
                Select Case Str(m, "event_type")
                    Case "book"
                        Dim l = LiveFor(Str(m, "asset_id"), Str(m, "market"))
                        If l Is Nothing Then Continue For
                        SyncLock l
                            l.Bids.Clear() : l.Asks.Clear()
                            For Each lv In Levels(m("bids"))
                                If lv.Size > 0 Then l.Bids(lv.Price) = lv.Size
                            Next
                            For Each lv In Levels(m("asks"))
                                If lv.Size > 0 Then l.Asks(lv.Price) = lv.Size
                            Next
                            l.BestBid = If(l.Bids.Count > 0, CDbl(l.Bids.Keys.Last()), CType(Nothing, Double?))
                            l.BestAsk = If(l.Asks.Count > 0, CDbl(l.Asks.Keys.First()), CType(Nothing, Double?))
                            ' A book's last_trade_price is not always this token's last trade (2026-11-02: 97.3¢ on a 3% outcome):
                            ' the last trade comes only from last_trade_price messages.
                        End SyncLock
                        Touch(l)
                    Case "price_change"
                        For Each c In If(TryCast(m("price_changes"), JsonArray), New JsonArray())
                            Dim l = LiveFor(Str(c, "asset_id"), Str(m, "market"))
                            If l Is Nothing Then Continue For
                            SyncLock l
                                Dim price As Decimal, size As Decimal
                                If Decimal.TryParse(Str(c, "price"), NumberStyles.Float, CultureInfo.InvariantCulture, price) AndAlso
                                   Decimal.TryParse(Str(c, "size"), NumberStyles.Float, CultureInfo.InvariantCulture, size) Then
                                    Dim book = If(String.Equals(Str(c, "side"), "BUY", StringComparison.OrdinalIgnoreCase), l.Bids, l.Asks)
                                    If size <= 0 Then book.Remove(price) Else book(price) = size
                                End If
                                l.BestBid = If(Num(c, "best_bid"), If(l.Bids.Count > 0, CDbl(l.Bids.Keys.Last()), CType(Nothing, Double?)))
                                l.BestAsk = If(Num(c, "best_ask"), If(l.Asks.Count > 0, CDbl(l.Asks.Keys.First()), CType(Nothing, Double?)))
                            End SyncLock
                            Touch(l)
                        Next
                    Case "last_trade_price"
                        Dim l = LiveFor(Str(m, "asset_id"), Str(m, "market"))
                        If l Is Nothing Then Continue For
                        l.LastTrade = Num(m, "price")
                        l.LastSide = Str(m, "side")
                        l.LastSize = Num(m, "size")
                        Dim ts = Num(m, "timestamp")
                        l.LastTradeUtc = If(ts.HasValue, DateTimeOffset.FromUnixTimeMilliseconds(CLng(ts.Value)).UtcDateTime, DateTime.UtcNow)
                        Touch(l)
                    Case "best_bid_ask"
                        Dim l = LiveFor(Str(m, "asset_id"), Str(m, "market"))
                        If l Is Nothing Then Continue For
                        l.BestBid = If(Num(m, "best_bid"), l.BestBid)
                        l.BestAsk = If(Num(m, "best_ask"), l.BestAsk)
                        Touch(l)
                    Case "new_market"
                        _newMarkets.Enqueue(New With {.id = Str(m, "id"), .question = Str(m, "question"), .slug = Str(m, "slug"), .market = Str(m, "market"),
                                                      .outcomes = Strings(m, "outcomes"), .url = SiteLink(If(Str(m, "event_slug"), Str(m, "slug")), Nothing), .seenUtc = DateTime.UtcNow})
                        Dim dropped As Object = Nothing
                        While _newMarkets.Count > 60 AndAlso _newMarkets.TryDequeue(dropped)
                        End While
                    Case "market_resolved"
                        Dim winner = Str(m, "winning_asset_id")
                        For Each t In Strings(m, "assets_ids")
                            Dim l As PmLive = Nothing
                            If _live.TryGetValue(t, l) Then
                                l.Won = String.Equals(t, winner, StringComparison.Ordinal)
                                Touch(l)
                            End If
                        Next
                End Select
            Next
        End Sub

        Private Shared Function Levels(list As JsonNode) As IEnumerable(Of (Price As Decimal, Size As Decimal))
            Return If(TryCast(list, JsonArray), New JsonArray()).Select(Function(x)
                                                                             Dim p As Decimal, s As Decimal
                                                                             Decimal.TryParse(Str(x, "price"), NumberStyles.Float, CultureInfo.InvariantCulture, p)
                                                                             Decimal.TryParse(Str(x, "size"), NumberStyles.Float, CultureInfo.InvariantCulture, s)
                                                                             Return (p, s)
                                                                         End Function)
        End Function

        ''' <summary>The live state of a token followed (a token not asked for is not kept: messages after unsubscribing are dropped).</summary>
        Private Function LiveFor(token As String, market As String) As PmLive
            If String.IsNullOrEmpty(token) Then Return Nothing
            SyncLock _subscribed
                If _subscribed.Count > 0 AndAlso Not _subscribed.Contains(token) AndAlso Not _wanted.ContainsKey(token) Then Return Nothing
            End SyncLock
            Return _live.GetOrAdd(token, Function(t) New PmLive With {.TokenId = t, .Market = market})
        End Function

        Private Sub Touch(l As PmLive)
            l.UpdatedUtc = DateTime.UtcNow
            l.Version = Interlocked.Increment(_version)
        End Sub

        ''' <summary>The live state of these tokens changed after version since (all of them when since is 0), and the version now.</summary>
        Public Function LiveSince(tokens As IEnumerable(Of String), since As Long) As (Items As List(Of PmLive), Version As Long)
            Dim now = Interlocked.Read(_version)
            Dim items As New List(Of PmLive)
            For Each t In tokens
                Dim l As PmLive = Nothing
                If _live.TryGetValue(t, l) AndAlso l.Version > since Then items.Add(l)
            Next
            Return (items, now)
        End Function

        Public Function LiveOf(token As String) As PmLive
            Dim l As PmLive = Nothing
            Return If(_live.TryGetValue(If(token, ""), l), l, Nothing)
        End Function

        ''' <summary>Markets that opened while the server listened, newest first.</summary>
        Public Function NewMarkets() As List(Of Object)
            Return _newMarkets.Reverse().ToList()
        End Function

        Public Function Status() As Object
            Dim subscribed As Integer
            SyncLock _subscribed
                subscribed = _subscribed.Count
            End SyncLock
            Return New With {.connected = Connected, .connectedSinceUtc = ConnectedSinceUtc, .lastMessageUtc = LastMessageUtc, .reconnects = Reconnects,
                             .error = LastError, .following = subscribed, .messages = Interlocked.Read(_messages)}
        End Function

        ' ------------------------------------------------------------------ the polymarket tool

        Public Const ToolName As String = "polymarket"
        Public Const ToolDescription As String =
            "Polymarket prediction markets (public data, read only). action search: open markets by words and/or category (politics, sports, crypto, " &
            "finance, geopolitics, tech, pop-culture, world, economy, elections), busiest first, with each outcome's price, which is the market's " &
            "probability of it (0.62 = 62%). action market: one market by id, slug, condition id or polymarket.com link, with live best bid, ask, " &
            "midpoint and last trade, its order book's top levels, and optional price history (1d, 1w, 1m, max). action trades / holders: a market's " &
            "latest trades or biggest holders. action leaderboard: top traders (period day, week, month, all; sort pnl or volume). action wallet: " &
            "an address's positions and activity. Odds are crowd prices, not facts; say when they were read."
        Public Const ToolSchema As String =
            "{""type"":""object"",""properties"":{""action"":{""type"":""string"",""enum"":[""search"",""market"",""trades"",""holders"",""leaderboard"",""wallet""]}," &
            """query"":{""type"":""string"",""description"":""search: words (empty: the busiest)""}," &
            """category"":{""type"":""string"",""enum"":[""politics"",""sports"",""crypto"",""finance"",""geopolitics"",""tech"",""pop-culture"",""world"",""economy"",""elections""]}," &
            """status"":{""type"":""string"",""enum"":[""open"",""closed""],""description"":""search: open (default) or closed/resolved""}," &
            """limit"":{""type"":""integer"",""minimum"":1,""maximum"":100,""description"":""search: how many events (default 10)""}," &
            """market"":{""type"":""string"",""description"":""market, trades, holders: id, slug, condition id (0x…) or polymarket.com link""}," &
            """history"":{""type"":""string"",""enum"":[""1d"",""1w"",""1m"",""max""]}," &
            """period"":{""type"":""string"",""enum"":[""day"",""week"",""month"",""all""]},""sort"":{""type"":""string"",""enum"":[""pnl"",""volume""]}," &
            """address"":{""type"":""string"",""description"":""wallet: 0x… address""}},""required"":[""action""]}"

        Private Shared Function P(v As Double?) As Double?
            Return If(v.HasValue, Math.Round(v.Value, 4), CType(Nothing, Double?))
        End Function

        Private Shared Function Money(v As Double?) As Double?
            Return If(v.HasValue, Math.Round(v.Value, 0), CType(Nothing, Double?))
        End Function

        Private Function MarketSummary(m As PmMarket) As Object
            Return New With {.question = m.Question, .name = m.GroupTitle,
                             .outcomes = m.Outcomes.Select(Function(o, i) New With {.outcome = o, .price = P(If(i < m.Prices.Count, m.Prices(i), CType(Nothing, Double?))),
                                                                                    .chance = If(i < m.Prices.Count, Math.Round(m.Prices(i) * 100, 1).ToString(CultureInfo.InvariantCulture) & "%", Nothing)}).ToList(),
                             .volume24h = Money(m.Volume24h), .volume = Money(m.Volume), .liquidity = Money(m.Liquidity), .ends = m.EndUtc?.ToString("yyyy-MM-dd"),
                             .closed = m.Closed OrElse Not IsOpen(m), .slug = m.Slug, .url = m.Url}
        End Function

        ''' <summary>The polymarket tool's answer (agents and chats).</summary>
        Public Async Function ToolAnswerAsync(argsJson As String, ct As CancellationToken) As Task(Of String)
            Dim a As JsonNode = Nothing
            Try
                a = JsonNode.Parse(If(String.IsNullOrWhiteSpace(argsJson), "{}", argsJson))
            Catch ex As JsonException
            End Try
            Dim action = If(Str(a, "action"), "search").ToLowerInvariant()
            Dim read = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm") & " UTC"
            Try
                Select Case action
                    Case "market", "trades", "holders"
                        Dim found = Await MarketAsync(Str(a, "market"), ct).ConfigureAwait(False)
                        If Not found.HasValue Then Return JsonUtil.ToJson(New With {.error = "No market found for that id, slug, condition id or link. Search first (action search)."})
                        Dim m = found.Value.Market
                        If action = "trades" Then Return JsonUtil.ToJson(New With {.market = m.Question, .read = read, .trades = (Await TradesAsync(m.ConditionId, 25, ct).ConfigureAwait(False))})
                        If action = "holders" Then Return JsonUtil.ToJson(New With {.market = m.Question, .outcomes = m.Outcomes, .holders = (Await HoldersAsync(m.ConditionId, 10, ct).ConfigureAwait(False))})
                        Want(m.TokenIds)
                        Dim quotes = Await QuotesAsync(m.TokenIds, ct).ConfigureAwait(False)
                        Dim book = If(m.TokenIds.Count > 0, Await BookAsync(m.TokenIds(0), 5, ct).ConfigureAwait(False), Nothing)
                        Dim hist As Object = Nothing
                        If Str(a, "history") IsNot Nothing AndAlso m.TokenIds.Count > 0 Then
                            Dim pts = Await HistoryAsync(m.TokenIds(0), Str(a, "history"), ct).ConfigureAwait(False)
                            Dim stepN = Math.Max(1, CInt(Math.Ceiling(pts.Count / 24.0)))
                            hist = pts.Where(Function(x, i) i Mod stepN = 0 OrElse i = pts.Count - 1).
                                Select(Function(x) New With {.utc = DateTimeOffset.FromUnixTimeMilliseconds(CLng(x(0))).UtcDateTime.ToString("yyyy-MM-dd HH:mm"), .price = P(x(1))}).ToList()
                        End If
                        Return JsonUtil.ToJson(New With {
                            .event = found.Value.Ev?.Title, .category = found.Value.Ev?.Category, .market = MarketSummary(m), .conditionId = m.ConditionId, .read = read,
                            .live = m.Outcomes.Select(Function(o, i)
                                                          Dim q As (Bid As Double?, Ask As Double?, Mid As Double?, Last As Double?) = Nothing
                                                          Dim has = i < m.TokenIds.Count AndAlso quotes.TryGetValue(m.TokenIds(i), q)
                                                          Return New With {.outcome = o, .bestBid = If(has, P(q.Bid), Nothing), .bestAsk = If(has, P(q.Ask), Nothing),
                                                                           .midpoint = If(has, P(q.Mid), Nothing), .lastTrade = If(has, P(q.Last), Nothing)}
                                                      End Function).ToList(),
                            .book = If(book.HasValue, New With {.outcome = m.Outcomes.FirstOrDefault(), .bids = book.Value.Bids, .asks = book.Value.Asks}, Nothing),
                            .history = hist, .description = TextUtil.Truncate(found.Value.Ev?.Description, 600)})
                    Case "leaderboard"
                        Return JsonUtil.ToJson(New With {.period = If(Str(a, "period"), "week"), .read = read,
                                                         .traders = Await LeaderboardAsync(Str(a, "period"), Str(a, "sort"), 20, ct).ConfigureAwait(False)})
                    Case "wallet"
                        Dim w = Await WalletAsync(Str(a, "address"), ct).ConfigureAwait(False)
                        Return JsonUtil.ToJson(New With {.address = Str(a, "address"), .read = read, .positions = w.Positions, .activity = w.Activity})
                    Case Else
                        Dim limit = CInt(Math.Clamp(If(Num(a, "limit"), 10), 1, 100))
                        Dim r = Await EventsAsync(Str(a, "query"), Str(a, "category"), Str(a, "status"), Nothing, 0, limit, ct).ConfigureAwait(False)
                        Return JsonUtil.ToJson(New With {
                            .read = read, .more = r.More,
                            .events = r.Events.Select(Function(e) New With {.title = e.Title, .category = e.Category, .volume24h = Money(e.Volume24h), .volume = Money(e.Volume),
                                                                             .ends = e.EndUtc?.ToString("yyyy-MM-dd"), .url = e.Url, .markets = e.Markets.Take(6).Select(AddressOf MarketSummary).ToList(),
                                                                             .moreMarkets = Math.Max(0, e.Markets.Count - 6)}).ToList()})
                End Select
            Catch ex As ArgumentException
                Return JsonUtil.ToJson(New With {.error = ex.Message})
            Catch ex As HttpRequestException
                Return JsonUtil.ToJson(New With {.error = ex.Message})
            End Try
        End Function

    End Class

End Namespace
