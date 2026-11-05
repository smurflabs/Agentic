Option Strict On
Option Explicit On
Option Infer On

Imports System.Globalization
Imports System.Net
Imports System.Net.Http
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports System.Threading
Imports System.Xml
Imports System.Xml.Linq
Imports Agentic.Configuration
Imports Agentic.Logging
Imports Agentic.Persistence
Imports Agentic.Utilities

Namespace Work

    Public NotInheritable Class NewsArticle
        Public Property Id As String
        Public Property Link As String
        Public Property Source As String
        Public Property Category As String
        Public Property Title As String
        Public Property Summary As String
        Public Property Author As String
        Public Property ImageUrl As String
        Public Property PublishedUtc As DateTime
        Public Property FetchedUtc As DateTime
    End Class

    ''' <summary>A feed as the News tab shows it: what it is and how its last read went.</summary>
    Public NotInheritable Class NewsFeedState
        Public Property Name As String
        Public Property Url As String
        Public Property Category As String
        Public Property LastReadUtc As DateTime?
        Public Property LastOkUtc As DateTime?
        ''' <summary>Items in the feed at the last read, and how many of them were new.</summary>
        Public Property Items As Integer
        Public Property Added As Integer
        Public Property [Error] As String
    End Class

    ''' <summary>
    ''' The News tab's articles: every feed (RSS 2.0, RSS 1.0/RDF or Atom) read every PollMinutes, each article kept once (by its
    ''' link) for KeepDays with its source, category, summary and picture. Agents and chats search them through
    ''' <see cref="ToolAnswerAsync"/> (the news_articles tool).
    ''' </summary>
    Public NotInheritable Class NewsService
        Public Shared ReadOnly Categories As String() = {"international", "us", "uk", "europe", "australia", "agencies", "investigative", "business"}
        Public Shared ReadOnly CategoryNames As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
            {"international", "International"}, {"us", "US"}, {"uk", "UK"}, {"europe", "Europe"}, {"australia", "Australia"}, {"agencies", "News agencies"},
            {"investigative", "Investigative"}, {"business", "Business & markets"}}

        ''' <summary>
        ''' The feeds used when News.Feeds is empty. Reuters and AP have no public feeds, so Google News carries them; Politico
        ''' Europe too (its own feed answered 429 after a few reads on 2026-11-02); NHK World
        ''' and the Washington Post too: on 2026-11-02 NHK's feed address answered 404 (its English JSON list had stopped
        ''' updating in September) and feeds.washingtonpost.com answered 502 for every feed (the site refuses automated readers).
        ''' </summary>
        Public Shared ReadOnly DefaultFeeds As (Name As String, Url As String, Category As String)() = {
            ("BBC World", "https://feeds.bbci.co.uk/news/world/rss.xml", "international"),
            ("Al Jazeera", "https://www.aljazeera.com/xml/rss/all.xml", "international"),
            ("Deutsche Welle", "https://rss.dw.com/rdf/rss-en-world", "international"),
            ("France 24", "https://www.france24.com/en/rss", "international"),
            ("NHK World", "https://news.google.com/rss/search?q=when:24h+site:www3.nhk.or.jp/nhkworld&hl=en-US&gl=US&ceid=US:en", "international"),
            ("CBC World", "https://www.cbc.ca/webfeed/rss/rss-world", "international"),
            ("Euronews", "https://www.euronews.com/rss", "international"),
            ("South China Morning Post", "https://www.scmp.com/rss/91/feed/", "international"),
            ("NPR", "https://feeds.npr.org/1001/rss.xml", "us"),
            ("PBS NewsHour", "https://www.pbs.org/newshour/feeds/rss/headlines", "us"),
            ("New York Times", "https://rss.nytimes.com/services/xml/rss/nyt/HomePage.xml", "us"),
            ("Washington Post", "https://news.google.com/rss/search?q=when:24h+site:washingtonpost.com&hl=en-US&gl=US&ceid=US:en", "us"),
            ("Fox News", "https://moxie.foxnews.com/google-publisher/latest.xml", "us"),
            ("CBS News", "https://www.cbsnews.com/latest/rss/main", "us"),
            ("NBC News", "https://feeds.nbcnews.com/nbcnews/public/news", "us"),
            ("The Guardian US", "https://www.theguardian.com/us-news/rss", "us"),
            ("BBC UK", "https://feeds.bbci.co.uk/news/uk/rss.xml", "uk"),
            ("Sky News", "https://feeds.skynews.com/feeds/rss/uk.xml", "uk"),
            ("The Guardian UK", "https://www.theguardian.com/uk-news/rss", "uk"),
            ("The Independent", "https://www.independent.co.uk/news/uk/rss", "uk"),
            ("BBC Europe", "https://feeds.bbci.co.uk/news/world/europe/rss.xml", "europe"),
            ("The Guardian Europe", "https://www.theguardian.com/world/europe-news/rss", "europe"),
            ("Politico Europe", "https://news.google.com/rss/search?q=when:24h+site:politico.eu&hl=en-US&gl=US&ceid=US:en", "europe"),
            ("DW Europe", "https://rss.dw.com/rdf/rss-en-eu", "europe"),
            ("RTÉ News", "https://www.rte.ie/feeds/rss/?index=/news/", "europe"),
            ("Kyiv Independent", "https://kyivindependent.com/news-archive/rss/", "europe"),
            ("The Local", "https://www.thelocal.com/feeds/rss.php", "europe"),
            ("ABC News Australia", "https://www.abc.net.au/news/feed/51120/rss.xml", "australia"),
            ("The Guardian Australia", "https://www.theguardian.com/australia-news/rss", "australia"),
            ("Sydney Morning Herald", "https://www.smh.com.au/rss/feed.xml", "australia"),
            ("The Age", "https://www.theage.com.au/rss/feed.xml", "australia"),
            ("Reuters", "https://news.google.com/rss/search?q=when:24h+site:reuters.com&hl=en-US&gl=US&ceid=US:en", "agencies"),
            ("Associated Press", "https://news.google.com/rss/search?q=when:24h+site:apnews.com&hl=en-US&gl=US&ceid=US:en", "agencies"),
            ("ProPublica", "https://www.propublica.org/feeds/propublica/main", "investigative"),
            ("The Intercept", "https://theintercept.com/feed/", "investigative"),
            ("The Conversation", "https://theconversation.com/articles.atom", "investigative"),
            ("Bellingcat", "https://www.bellingcat.com/feed/", "investigative"),
            ("CNBC", "https://www.cnbc.com/id/100003114/device/rss/rss.html", "business"),
            ("Bloomberg", "https://feeds.bloomberg.com/markets/news.rss", "business"),
            ("WSJ Markets", "https://feeds.content.dowjones.io/public/rss/RSSMarketsMain", "business"),
            ("Financial Times", "https://www.ft.com/rss/home", "business"),
            ("MarketWatch", "https://feeds.marketwatch.com/marketwatch/topstories/", "business")}

        Private ReadOnly _db As Database
        Private ReadOnly _opt As NewsOptions
        Private ReadOnly _log As StructuredLogger
        Private ReadOnly _http As HttpClient
        Private ReadOnly _feeds As List(Of NewsFeedState)
        Private ReadOnly _wake As New SemaphoreSlim(0)
        Private _reading As Integer
        Private _lastRefresh As DateTime = DateTime.MinValue

        Public Property LastRoundUtc As DateTime?

        Public Sub New(db As Database, options As NewsOptions, log As StructuredLogger, Optional handler As HttpMessageHandler = Nothing)
            _db = db
            _opt = If(options, New NewsOptions())
            _log = log
            _http = If(handler Is Nothing, New HttpClient(New HttpClientHandler With {.AutomaticDecompression = DecompressionMethods.All}), New HttpClient(handler))
            _http.Timeout = TimeSpan.FromSeconds(Math.Max(5, _opt.TimeoutSeconds))
            ' Some sites (CBC) answer only browsers: anything else gets a page with no articles, or no answer at all.
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36")
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/rss+xml, application/atom+xml, application/rdf+xml, application/xml;q=0.9, text/xml;q=0.9, */*;q=0.5")
            Dim configured = _opt.Feeds.Where(Function(f) f IsNot Nothing AndAlso f.Enabled AndAlso Not String.IsNullOrWhiteSpace(f.Url)).
                Select(Function(f) New NewsFeedState With {.Name = If(String.IsNullOrWhiteSpace(f.Name), New Uri(f.Url).Host, f.Name.Trim()), .Url = f.Url.Trim(),
                                                           .Category = If(String.IsNullOrWhiteSpace(f.Category), "general", f.Category.Trim().ToLowerInvariant())}).ToList()
            _feeds = If(configured.Count > 0, configured,
                        DefaultFeeds.Select(Function(f) New NewsFeedState With {.Name = f.Name, .Url = f.Url, .Category = f.Category}).ToList())
        End Sub

        Public ReadOnly Property Enabled As Boolean
            Get
                Return _opt.Enabled
            End Get
        End Property

        Public ReadOnly Property Feeds As List(Of NewsFeedState)
            Get
                SyncLock _feeds
                    Return _feeds.ToList()
                End SyncLock
            End Get
        End Property

        Public ReadOnly Property Options As NewsOptions
            Get
                Return _opt
            End Get
        End Property

        ' ------------------------------------------------------------------ reading the feeds

        Public Sub Start(ct As CancellationToken)
            If Not _opt.Enabled Then Return
            Dim unused = Task.Run(Function() LoopAsync(ct))
        End Sub

        Private Async Function LoopAsync(ct As CancellationToken) As Task
            Dim nextPrune = DateTime.UtcNow.AddMinutes(2)
            While Not ct.IsCancellationRequested
                Try
                    Await ReadAllAsync(ct).ConfigureAwait(False)
                    If DateTime.UtcNow >= nextPrune Then
                        Await PruneAsync(DateTime.UtcNow).ConfigureAwait(False)
                        nextPrune = DateTime.UtcNow.AddHours(6)
                    End If
                Catch ex As Exception When Not ExceptionUtil.IsCancellation(ex)
                    _log?.Warn("News", "Reading the feeds failed: " & ex.Message)
                End Try
                Try
                    Await _wake.WaitAsync(TimeSpan.FromMinutes(Math.Max(2, _opt.PollMinutes)), ct).ConfigureAwait(False)
                Catch ex As OperationCanceledException
                    Exit While
                End Try
            End While
        End Function

        ''' <summary>Reads every feed again now (at most once a minute, whoever asks).</summary>
        Public Function RefreshNow() As Boolean
            If DateTime.UtcNow - _lastRefresh < TimeSpan.FromMinutes(1) Then Return False
            _lastRefresh = DateTime.UtcNow
            _wake.Release()
            Return True
        End Function

        ''' <summary>Reads every feed (a few at once); one round at a time.</summary>
        Public Async Function ReadAllAsync(ct As CancellationToken) As Task
            If Interlocked.Exchange(_reading, 1) = 1 Then Return
            Try
                Using gate As New SemaphoreSlim(Math.Max(1, _opt.Concurrency))
                    Dim work = Feeds.Select(Async Function(f)
                                                Await gate.WaitAsync(ct).ConfigureAwait(False)
                                                Try
                                                    Await ReadFeedAsync(f, ct).ConfigureAwait(False)
                                                Finally
                                                    gate.Release()
                                                End Try
                                            End Function).ToList()
                    Await Task.WhenAll(work).ConfigureAwait(False)
                End Using
                LastRoundUtc = DateTime.UtcNow
            Finally
                Interlocked.Exchange(_reading, 0)
            End Try
        End Function

        Private Async Function ReadFeedAsync(feed As NewsFeedState, ct As CancellationToken) As Task
            Dim now = DateTime.UtcNow
            Try
                Using resp = Await _http.GetAsync(feed.Url, ct).ConfigureAwait(False)
                    If Not resp.IsSuccessStatusCode Then Throw New HttpRequestException($"{CInt(resp.StatusCode)} {resp.ReasonPhrase}")
                    Dim text = Await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(False)
                    Dim items = ParseFeed(text, feed.Name, feed.Category, now)
                    Dim added = 0
                    For Each a In items
                        added += Await _db.ExecuteAsync(
                            "INSERT OR IGNORE INTO NewsArticles(Id, Link, Source, Category, Title, Summary, Author, ImageUrl, PublishedUtc, FetchedUtc) " &
                            "VALUES (@id, @l, @s, @c, @t, @m, @a, @i, @p, @f)",
                            ("@id", CObj(a.Id)), ("@l", a.Link), ("@s", a.Source), ("@c", a.Category), ("@t", a.Title), ("@m", If(a.Summary, CObj(DBNull.Value))),
                            ("@a", If(a.Author, CObj(DBNull.Value))), ("@i", If(a.ImageUrl, CObj(DBNull.Value))), ("@p", Db.Ts(a.PublishedUtc)), ("@f", Db.Ts(now))).ConfigureAwait(False)
                    Next
                    SyncLock _feeds
                        feed.LastReadUtc = now : feed.LastOkUtc = now : feed.Items = items.Count : feed.Added = added : feed.Error = Nothing
                    End SyncLock
                End Using
            Catch ex As Exception When Not ct.IsCancellationRequested
                SyncLock _feeds
                    Dim why = If(TypeOf ex Is TaskCanceledException, "No answer in time.",
                              If(TypeOf ex Is XmlException AndAlso Not ex.Message.StartsWith("Not an RSS", StringComparison.Ordinal), "Not a feed (the site answered something else): " & ex.Message, ex.Message))
                    feed.LastReadUtc = now : feed.Error = TextUtil.Truncate(why, 200)
                End SyncLock
            End Try
        End Function

        Friend Async Function PruneAsync(now As DateTime) As Task
            If _opt.KeepDays > 0 Then Await _db.ExecuteAsync("DELETE FROM NewsArticles WHERE PublishedUtc < @c", ("@c", CObj(Db.Ts(now.AddDays(-_opt.KeepDays))))).ConfigureAwait(False)
        End Function

        ' ------------------------------------------------------------------ parsing

        ''' <summary>The articles of an RSS 2.0, RSS 1.0 (RDF) or Atom feed.</summary>
        Friend Shared Function ParseFeed(xml As String, source As String, category As String, now As DateTime) As List(Of NewsArticle)
            Dim settings As New XmlReaderSettings With {.DtdProcessing = DtdProcessing.Ignore, .XmlResolver = Nothing, .MaxCharactersInDocument = 20_000_000}
            Dim doc As XDocument
            Using sr As New IO.StringReader(If(xml, "").TrimStart(ChrW(&HFEFF))), xr = XmlReader.Create(sr, settings)
                doc = XDocument.Load(xr)
            End Using
            Dim list As New List(Of NewsArticle)
            Dim rootName = If(doc.Root?.Name.LocalName, "")
            If Not {"rss", "RDF", "feed"}.Contains(rootName, StringComparer.OrdinalIgnoreCase) Then
                Throw New XmlException($"Not an RSS or Atom feed (the site answered {If(rootName.Length = 0, "nothing", "a <" & rootName & "> document")}).")
            End If
            Dim atom = doc.Root.Name.LocalName = "feed"
            Dim nodes = doc.Descendants().Where(Function(e) e.Name.LocalName = If(atom, "entry", "item"))
            Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each item In nodes.Take(200)
                Dim child = Function(name As String) item.Elements().FirstOrDefault(Function(e) e.Name.LocalName = name)
                Dim text = Function(name As String) child(name)?.Value
                Dim link As String
                If atom Then
                    Dim links = item.Elements().Where(Function(e) e.Name.LocalName = "link").ToList()
                    Dim alt = links.FirstOrDefault(Function(e) If(e.Attribute("rel")?.Value, "alternate") = "alternate")
                    link = If(alt, links.FirstOrDefault())?.Attribute("href")?.Value
                Else
                    link = text("link")
                    If String.IsNullOrWhiteSpace(link) Then link = item.Attribute(XName.Get("about", "http://www.w3.org/1999/02/22-rdf-syntax-ns#"))?.Value
                    If String.IsNullOrWhiteSpace(link) Then
                        Dim guid = child("guid")
                        If guid IsNot Nothing AndAlso If(guid.Attribute("isPermaLink")?.Value, "true") <> "false" AndAlso guid.Value.StartsWith("http", StringComparison.OrdinalIgnoreCase) Then link = guid.Value
                    End If
                End If
                link = If(link, "").Trim()
                If Not link.StartsWith("http", StringComparison.OrdinalIgnoreCase) OrElse Not seen.Add(link) Then Continue For
                Dim title = CleanText(text("title"), 300)
                ' Google News titles end with " - Reuters" (and some with the site's own name too: " | NHK WORLD-JAPAN News"): the
                ' source shows on its own.
                If link.Contains("news.google.com") Then
                    title = Regex.Replace(title, "\s+-\s+[^-]{2,60}$", "")
                    title = Regex.Replace(title, "\s+\|\s+[^|]{2,40}\bNews\b[^|]{0,20}$", "", RegexOptions.IgnoreCase)
                    Dim outlet = If(source, "").Split(" "c)(0)
                    If outlet.Length > 1 Then title = Regex.Replace(title, "\s+\|\s+[^|]{0,20}\b" & Regex.Escape(outlet) & "\b[^|]{0,30}$", "", RegexOptions.IgnoreCase)
                End If
                If title.Length = 0 Then Continue For
                Dim body = If(text("description"), If(text("summary"), If(text("encoded"), text("content"))))
                Dim summary = CleanText(body, 600)
                If String.Equals(summary, title, StringComparison.OrdinalIgnoreCase) OrElse link.Contains("news.google.com") Then summary = Nothing
                Dim stamp = If(text("pubDate"), If(text("date"), If(text("published"), If(text("updated"), text("issued")))))
                Dim published = If(ParseDate(stamp), now)
                If published > now.AddMinutes(10) Then published = now
                Dim author = CleanText(If(text("creator"), If(child("author")?.Elements().FirstOrDefault(Function(e) e.Name.LocalName = "name")?.Value, text("author"))), 120)
                list.Add(New NewsArticle With {.Id = IdOf(link), .Link = link, .Source = source, .Category = category, .Title = title,
                                               .Summary = If(String.IsNullOrWhiteSpace(summary), Nothing, summary), .Author = If(author.Length = 0, Nothing, author),
                                               .ImageUrl = ImageOf(item, body), .PublishedUtc = published, .FetchedUtc = now})
            Next
            Return list
        End Function

        Friend Shared Function IdOf(link As String) As String
            Return "news_" & Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(link))).Substring(0, 20).ToLowerInvariant()
        End Function

        ''' <summary>The article's picture: media:content or media:thumbnail, an image enclosure, or the first img in its HTML.</summary>
        Private Shared Function ImageOf(item As XElement, html As String) As String
            Dim pick As String = Nothing
            For Each e In item.Descendants()
                Dim n = e.Name.LocalName
                Dim url = e.Attribute("url")?.Value
                If String.IsNullOrEmpty(url) Then Continue For
                If n = "thumbnail" OrElse (n = "content" AndAlso (If(e.Attribute("medium")?.Value, "") = "image" OrElse If(e.Attribute("type")?.Value, "").StartsWith("image/") OrElse
                                                                  Regex.IsMatch(url, "\.(jpe?g|png|webp|gif)(\?|$)", RegexOptions.IgnoreCase))) OrElse
                   (n = "enclosure" AndAlso If(e.Attribute("type")?.Value, "").StartsWith("image/")) Then
                    pick = url
                    If n = "content" Then Exit For
                End If
            Next
            If pick Is Nothing AndAlso Not String.IsNullOrEmpty(html) Then
                Dim m = Regex.Match(html, "<img[^>]+src=[""']([^""']+)[""']", RegexOptions.IgnoreCase)
                If m.Success Then pick = WebUtility.HtmlDecode(m.Groups(1).Value)
            End If
            Return If(pick IsNot Nothing AndAlso pick.StartsWith("https://", StringComparison.OrdinalIgnoreCase), TextUtil.Truncate(pick, 1000), Nothing)
        End Function

        ''' <summary>Text without HTML, entities decoded, spaces collapsed, at most max characters.</summary>
        Friend Shared Function CleanText(s As String, max As Integer) As String
            If String.IsNullOrWhiteSpace(s) Then Return ""
            Dim t = Regex.Replace(s, "<(script|style)[^>]*>[\s\S]*?</\1>", " ", RegexOptions.IgnoreCase)
            ' Blocks and line breaks part words; inline tags (b, a, span) do not.
            t = Regex.Replace(t, "<(br|p|/p|div|/div|li|/li|h\d|/h\d|tr|/tr|td|/td)\b[^>]*>", " ", RegexOptions.IgnoreCase)
            t = Regex.Replace(t, "<[^>]+>", "")
            t = WebUtility.HtmlDecode(WebUtility.HtmlDecode(t))
            t = Regex.Replace(t, "\s+", " ").Trim()
            Return If(t.Length > max, t.Substring(0, max - 1).TrimEnd() & "…", t)
        End Function

        Private Shared ReadOnly Zones As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
            {"GMT", "+0000"}, {"UT", "+0000"}, {"UTC", "+0000"}, {"Z", "+0000"}, {"EST", "-0500"}, {"EDT", "-0400"}, {"CST", "-0600"}, {"CDT", "-0500"},
            {"MST", "-0700"}, {"MDT", "-0600"}, {"PST", "-0800"}, {"PDT", "-0700"}, {"BST", "+0100"}, {"CET", "+0100"}, {"CEST", "+0200"}, {"JST", "+0900"}}

        ''' <summary>RFC 822 (RSS: "Mon, 02 Nov 2026 20:14:19 GMT", "+0000", "EST") or ISO 8601 (Atom, Dublin Core), as UTC.</summary>
        Friend Shared Function ParseDate(s As String) As DateTime?
            If String.IsNullOrWhiteSpace(s) Then Return Nothing
            Dim t = s.Trim()
            Dim m = Regex.Match(t, "\s([A-Za-z]{1,4})$")
            Dim z As String = Nothing
            If m.Success AndAlso Zones.TryGetValue(m.Groups(1).Value, z) Then t = t.Substring(0, m.Index) & " " & z
            ' "+0000" reads as an offset only as "+00:00".
            t = Regex.Replace(t, "([+-])(\d{2})(\d{2})$", "$1$2:$3")
            Dim d As DateTimeOffset
            If DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, d) Then Return d.UtcDateTime
            ' Without the weekday ("Mon, " can be wrong in some feeds).
            t = Regex.Replace(t, "^[A-Za-z]{3},\s*", "")
            If DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, d) Then Return d.UtcDateTime
            Return Nothing
        End Function

        ' ------------------------------------------------------------------ reading the articles

        Private Shared Function Map(r As Microsoft.Data.Sqlite.SqliteDataReader) As NewsArticle
            Return New NewsArticle With {.Id = Db.Str(r, "Id"), .Link = Db.Str(r, "Link"), .Source = Db.Str(r, "Source"), .Category = Db.Str(r, "Category"),
                                         .Title = Db.Str(r, "Title"), .Summary = Db.Str(r, "Summary"), .Author = Db.Str(r, "Author"), .ImageUrl = Db.Str(r, "ImageUrl"),
                                         .PublishedUtc = Db.Dt(r, "PublishedUtc"), .FetchedUtc = Db.Dt(r, "FetchedUtc")}
        End Function

        ''' <summary>
        ''' Articles, newest first: words of the query in the title or summary (all of them), a category and source, within the
        ''' last hours, published before a time (the next page).
        ''' </summary>
        Public Function SearchAsync(query As String, category As String, source As String, hours As Integer, limit As Integer,
                                    Optional before As DateTime? = Nothing) As Task(Of List(Of NewsArticle))
            Dim sql As New StringBuilder("SELECT * FROM NewsArticles WHERE PublishedUtc >= @since")
            Dim ps As New List(Of (String, Object)) From {("@since", Db.Ts(DateTime.UtcNow.AddHours(-Math.Clamp(If(hours <= 0, 24 * 30, hours), 1, 24 * 365))))}
            If before.HasValue Then sql.Append(" AND PublishedUtc < @before") : ps.Add(("@before", Db.Ts(before.Value)))
            If Not String.IsNullOrWhiteSpace(category) AndAlso category <> "all" Then sql.Append(" AND Category = @cat") : ps.Add(("@cat", category.Trim().ToLowerInvariant()))
            If Not String.IsNullOrWhiteSpace(source) Then sql.Append(" AND Source = @src COLLATE NOCASE") : ps.Add(("@src", source.Trim()))
            Dim words = Regex.Split(If(query, ""), "\s+").Select(Function(w) w.Trim(""""c, "'"c, ","c, "."c)).Where(Function(w) w.Length > 1).Take(8).ToList()
            For i = 0 To words.Count - 1
                sql.Append($" AND (Title LIKE @w{i} ESCAPE '\' OR COALESCE(Summary,'') LIKE @w{i} ESCAPE '\')")
                ps.Add(($"@w{i}", "%" & words(i).Replace("\", "\\").Replace("%", "\%").Replace("_", "\_") & "%"))
            Next
            sql.Append(" ORDER BY PublishedUtc DESC LIMIT @lim")
            ps.Add(("@lim", Math.Clamp(limit, 1, 200)))
            Return _db.QueryAsync(sql.ToString(), AddressOf Map, ps.ToArray())
        End Function

        ''' <summary>Articles per category and per source over the last hours (the tabs' counts).</summary>
        Public Async Function CountsAsync(hours As Integer) As Task(Of (Categories As Dictionary(Of String, Integer), Sources As Dictionary(Of String, Integer)))
            Dim since = Db.Ts(DateTime.UtcNow.AddHours(-Math.Clamp(hours, 1, 24 * 365)))
            Dim cats = (Await _db.QueryAsync("SELECT Category, COUNT(*) FROM NewsArticles WHERE PublishedUtc >= @s GROUP BY Category", Function(r) (r.GetString(0), r.GetInt32(1)), ("@s", CObj(since))).ConfigureAwait(False)).
                ToDictionary(Function(x) x.Item1, Function(x) x.Item2, StringComparer.OrdinalIgnoreCase)
            Dim srcs = (Await _db.QueryAsync("SELECT Source, COUNT(*) FROM NewsArticles WHERE PublishedUtc >= @s GROUP BY Source", Function(r) (r.GetString(0), r.GetInt32(1)), ("@s", CObj(since))).ConfigureAwait(False)).
                ToDictionary(Function(x) x.Item1, Function(x) x.Item2, StringComparer.OrdinalIgnoreCase)
            Return (cats, srcs)
        End Function

        ' ------------------------------------------------------------------ pictures

        Private ReadOnly _pictures As New Concurrent.ConcurrentDictionary(Of String, (Data As Byte(), Type As String, At As DateTime))(StringComparer.Ordinal)
        Private _pictureHttp As HttpClient
        Private Const MaxPictureBytes As Integer = 3 * 1024 * 1024

        ''' <summary>
        ''' An article's picture through this server (the page may only load its own pictures): https only, public addresses only
        ''' (redirects checked one by one), at most 3 MB, an image; the last 300 are kept in memory. Nothing when there is none.
        ''' </summary>
        Public Async Function PictureAsync(articleId As String, ct As CancellationToken) As Task(Of (Data As Byte(), Type As String)?)
            Dim hit As (Data As Byte(), Type As String, At As DateTime)
            If _pictures.TryGetValue(articleId, hit) Then Return (hit.Data, hit.Type)
            Dim url = TryCast(Await _db.ScalarAsync("SELECT ImageUrl FROM NewsArticles WHERE Id=@id", ("@id", CObj(articleId))).ConfigureAwait(False), String)
            If String.IsNullOrEmpty(url) Then Return Nothing
            If _pictureHttp Is Nothing Then
                _pictureHttp = New HttpClient(New HttpClientHandler With {.AllowAutoRedirect = False, .AutomaticDecompression = DecompressionMethods.All}) With {.Timeout = TimeSpan.FromSeconds(15)}
                _pictureHttp.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; AgenticNews/2.0)")
            End If
            Dim target As New Uri(url)
            For hop = 0 To 3
                If target.Scheme <> Uri.UriSchemeHttps Then Return Nothing
                Dim addresses = Await Dns.GetHostAddressesAsync(target.DnsSafeHost, ct).ConfigureAwait(False)
                If addresses.Length = 0 OrElse addresses.Any(Function(a) Not Global.Agentic.AI.OpenAICompatibleProvider.IsPublicAddress(a)) Then Return Nothing
                Using resp = Await _pictureHttp.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(False)
                    If CInt(resp.StatusCode) >= 300 AndAlso CInt(resp.StatusCode) < 400 AndAlso resp.Headers.Location IsNot Nothing Then
                        target = If(resp.Headers.Location.IsAbsoluteUri, resp.Headers.Location, New Uri(target, resp.Headers.Location))
                        Continue For
                    End If
                    If Not resp.IsSuccessStatusCode Then Return Nothing
                    Dim type = If(resp.Content.Headers.ContentType?.MediaType, "")
                    If Not type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) OrElse type.Contains("svg") Then Return Nothing
                    If resp.Content.Headers.ContentLength.HasValue AndAlso resp.Content.Headers.ContentLength.Value > MaxPictureBytes Then Return Nothing
                    Using s = Await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(False), ms As New IO.MemoryStream()
                        Dim buf(81919) As Byte
                        Do
                            Dim n = Await s.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(False)
                            If n = 0 Then Exit Do
                            ms.Write(buf, 0, n)
                            If ms.Length > MaxPictureBytes Then Return Nothing
                        Loop
                        Dim data = ms.ToArray()
                        If _pictures.Count >= 300 Then
                            For Each old In _pictures.OrderBy(Function(kv) kv.Value.At).Take(50).ToList()
                                _pictures.TryRemove(old.Key, Nothing)
                            Next
                        End If
                        _pictures(articleId) = (data, type, DateTime.UtcNow)
                        Return (data, type)
                    End Using
                End Using
            Next
            Return Nothing
        End Function

        ' ------------------------------------------------------------------ the news_articles tool

        Public Const ToolName As String = "news_articles"
        Public Const ToolDescription As String =
            "Recent news articles collected by this server from major outlets' RSS feeds (BBC, Al Jazeera, DW, NPR, NYT, Washington Post, Fox News, Reuters, AP, " &
            "Sky News, the Guardian, Politico Europe, ABC Australia, ProPublica, CNBC, Bloomberg, WSJ, FT and more), newest first: title, source, time, link and summary. Search words in the title or summary, " &
            "and narrow by category (international, us, uk, europe, australia, agencies, investigative, business), source or hours back. Cite articles as [n] when the results number them."
        Public Const ToolSchema As String =
            "{""type"":""object"",""properties"":{""query"":{""type"":""string"",""description"":""Words that must all appear (empty: the latest)""}," &
            """category"":{""type"":""string"",""enum"":[""all"",""international"",""us"",""uk"",""europe"",""australia"",""agencies"",""investigative"",""business""]}," &
            """source"":{""type"":""string"",""description"":""One outlet, e.g. BBC World, Reuters, Bloomberg""}," &
            """hours"":{""type"":""integer"",""minimum"":1,""maximum"":720,""description"":""How far back (default 24)""}," &
            """limit"":{""type"":""integer"",""minimum"":1,""maximum"":40,""description"":""How many (default 12)""}}}"

        ''' <summary>The articles a news_articles call asks for (agents and chats).</summary>
        Public Function ToolSearchAsync(argsJson As String) As Task(Of List(Of NewsArticle))
            Dim args As JsonObject = Nothing
            Try
                args = TryCast(JsonNode.Parse(If(String.IsNullOrWhiteSpace(argsJson), "{}", argsJson)), JsonObject)
            Catch ex As Text.Json.JsonException
            End Try
            Dim int = Function(name As String, fallback As Integer) As Integer
                          Dim v As Integer
                          Return If(Integer.TryParse(args?(name)?.ToString(), v), v, fallback)
                      End Function
            Return SearchAsync(args?("query")?.ToString(), args?("category")?.ToString(), args?("source")?.ToString(),
                               Math.Clamp(int("hours", 24), 1, 720), Math.Clamp(int("limit", 12), 1, 40))
        End Function

        ''' <summary>Articles as the tools give them: numbered from <paramref name="first"/>, with time, source, link and summary.</summary>
        Public Shared Function ToolText(list As List(Of NewsArticle), first As Integer, Optional indexOf As Func(Of NewsArticle, Integer) = Nothing) As String
            If list.Count = 0 Then Return "No articles match. Try fewer words, another category, or more hours back."
            Dim sb As New StringBuilder()
            Dim n = first
            For Each a In list
                Dim i = If(indexOf IsNot Nothing, indexOf(a), n)
                sb.Append($"[{i}] {a.Title} — {a.Source}, {a.PublishedUtc:yyyy-MM-dd HH:mm} UTC").Append(vbLf).Append(a.Link).Append(vbLf)
                If Not String.IsNullOrEmpty(a.Summary) Then sb.Append(TextUtil.Truncate(a.Summary, 320)).Append(vbLf)
                sb.Append(vbLf)
                n += 1
            Next
            Return sb.ToString().TrimEnd()
        End Function

    End Class

End Namespace
