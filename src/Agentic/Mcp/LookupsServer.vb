Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.ComponentModel
Imports System.Globalization
Imports System.Net
Imports System.Net.Http
Imports System.Net.Sockets
Imports System.Text
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports System.Threading
Imports ModelContextProtocol
Imports ModelContextProtocol.Server

Namespace Mcp

    ''' <summary>
    ''' Built-in "lookups" MCP server ("Command": "{self}", "Arguments": ["--mcp-lookups"]): facts from public services that need no
    ''' key - public holidays and holiday-aware business days (date.nager.at), currency rates (ECB, via frankfurter.dev), IP location
    ''' (ipwho.is) and WHOIS for domains and IP networks (RDAP). Base URLs can be changed with LOOKUPS_HOLIDAYS_URL,
    ''' LOOKUPS_RATES_URL, LOOKUPS_IP_URL and LOOKUPS_RDAP_URL.
    ''' </summary>
    Public NotInheritable Class LookupsServer
        Private ReadOnly _http As HttpClient
        Private ReadOnly _holidaysUrl As String
        Private ReadOnly _ratesUrl As String
        Private ReadOnly _ipUrl As String
        Private ReadOnly _rdapUrl As String
        Private ReadOnly _holidays As New ConcurrentDictionary(Of String, List(Of (Day As Date, Name As String, Local As String, IsGlobal As Boolean, Regions As String)))
        Private Shared ReadOnly Inv As CultureInfo = CultureInfo.InvariantCulture

        Friend Sub New(Optional handler As HttpMessageHandler = Nothing)
            _http = New HttpClient(If(handler, New SocketsHttpHandler With {.ConnectTimeout = TimeSpan.FromSeconds(10), .AutomaticDecompression = DecompressionMethods.All})) With {.Timeout = TimeSpan.FromSeconds(30)}
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Agentic-lookups/1.0")
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/json, application/rdap+json")
            _holidaysUrl = If(ToolKit.Env("LOOKUPS_HOLIDAYS_URL"), "https://date.nager.at/api/v3").TrimEnd("/"c)
            _ratesUrl = If(ToolKit.Env("LOOKUPS_RATES_URL"), "https://api.frankfurter.dev/v1").TrimEnd("/"c)
            _ipUrl = If(ToolKit.Env("LOOKUPS_IP_URL"), "https://ipwho.is").TrimEnd("/"c)
            _rdapUrl = If(ToolKit.Env("LOOKUPS_RDAP_URL"), "https://rdap.org").TrimEnd("/"c)
        End Sub

        Public Shared Function RunStdio(args As String()) As Integer
            Return BuiltInServer.Run("agentic-lookups", New LookupsServer().CreateTools())
        End Function

        Public Function CreateTools() As McpServerPrimitiveCollection(Of McpServerTool)
            Dim tools As New McpServerPrimitiveCollection(Of McpServerTool)()
            ToolKit.Add(tools, New Func(Of String, Integer, String, CancellationToken, Task(Of String))(AddressOf HolidaysAsync), "public_holidays",
                "A country's public holidays for a year (country: 2-letter code like US, GB, DE; region: e.g. US-CA for regional ones).")
            ToolKit.Add(tools, New Func(Of String, String, String, Integer, String, CancellationToken, Task(Of String))(AddressOf BusinessDaysAsync), "business_days",
                "Working days that skip weekends AND the country's public holidays: count them between two dates, or add a number of them to a date.")
            ToolKit.Add(tools, New Func(Of Decimal, String, String, String, CancellationToken, Task(Of String))(AddressOf CurrencyAsync), "currency_convert",
                "Convert money between currencies at European Central Bank reference rates (daily; date: a past day, default latest). to: one or more codes, comma-separated.")
            ToolKit.Add(tools, New Func(Of String, CancellationToken, Task(Of String))(AddressOf IpLookupAsync), "ip_lookup",
                "Where an IP address (or host name) is: country, region, city, time zone, network owner (ISP/ASN). Private and local addresses are recognised without a lookup.")
            ToolKit.Add(tools, New Func(Of String, CancellationToken, Task(Of String))(AddressOf WhoisAsync), "whois",
                "WHOIS (RDAP) for a domain - registrar, created/expires dates, status, name servers - or for an IP address - network name, range, owner, country, abuse contact.")
            Return tools
        End Function

        Private Async Function GetJsonAsync(url As String, ct As CancellationToken, Optional notFoundIsNull As Boolean = True) As Task(Of JsonNode)
            Try
                Using resp = Await _http.GetAsync(url, ct).ConfigureAwait(False)
                    If resp.StatusCode = HttpStatusCode.NotFound AndAlso notFoundIsNull Then Return Nothing
                    Dim body = Await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(False)
                    If Not resp.IsSuccessStatusCode Then Throw New McpException($"The service answered {CInt(resp.StatusCode)}: {ToolKit.Clip(body, 300)}")
                    Return If(String.IsNullOrWhiteSpace(body), Nothing, JsonNode.Parse(body))
                End Using
            Catch ex As HttpRequestException
                Throw New McpException("The service could not be reached: " & ex.Message)
            Catch ex As TaskCanceledException When Not ct.IsCancellationRequested
                Throw New McpException("The service did not answer within 30 s.")
            Catch ex As Text.Json.JsonException
                Throw New McpException("The service sent something that is not JSON.")
            End Try
        End Function

        Private Shared Function S(n As JsonNode) As String
            If n Is Nothing Then Return ""
            Try
                Return n.GetValue(Of String)()
            Catch ex As InvalidOperationException
                Return n.ToJsonString()
            End Try
        End Function

        ' ------------------------------------------------------------------------------------------ holidays

        Private Shared Function Country(code As String) As String
            Dim c = If(code, "").Trim().ToUpperInvariant()
            If Not Regex.IsMatch(c, "^[A-Z]{2}$") Then Throw New McpException($"'{code}' is not a 2-letter country code (US, GB, DE, FR, ...).")
            Return c
        End Function

        Friend Async Function HolidayListAsync(countryCode As String, year As Integer, ct As CancellationToken) As Task(Of List(Of (Day As Date, Name As String, Local As String, IsGlobal As Boolean, Regions As String)))
            Dim key = $"{countryCode}-{year}"
            Dim cached As List(Of (Day As Date, Name As String, Local As String, IsGlobal As Boolean, Regions As String)) = Nothing
            If _holidays.TryGetValue(key, cached) Then Return cached
            Dim j = Await GetJsonAsync($"{_holidaysUrl}/PublicHolidays/{year}/{countryCode}", ct).ConfigureAwait(False)
            Dim arr = TryCast(j, JsonArray)
            If arr Is Nothing Then Throw New McpException($"No public holiday data for {countryCode} (see date.nager.at for the countries covered).")
            Dim list = arr.Select(Function(h) (Day:=Date.ParseExact(S(h("date")), "yyyy-MM-dd", Inv), Name:=S(h("name")), Local:=S(h("localName")),
                                               IsGlobal:=S(h("global")) <> "false", Regions:=If(h("counties") Is Nothing, "", String.Join(", ", h("counties").AsArray().Select(Function(c) S(c)))))).ToList()
            _holidays(key) = list
            Return list
        End Function

        Private Shared Function Applies(h As (Day As Date, Name As String, Local As String, IsGlobal As Boolean, Regions As String), region As String) As Boolean
            If h.IsGlobal Then Return True
            Return Not String.IsNullOrWhiteSpace(region) AndAlso h.Regions.Split(","c).Any(Function(r) r.Trim().Equals(region.Trim(), StringComparison.OrdinalIgnoreCase))
        End Function

        Friend Async Function HolidaysAsync(<Description("Country code, e.g. US, GB, DE")> country As String,
                                            <Description("Year (default this year)")> Optional year As Integer = 0,
                                            <Description("Region code for regional holidays, e.g. US-CA, DE-BY (optional)")> Optional region As String = "",
                                            Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim c = LookupsServer.Country(country)
            Dim y = If(year <= 0, DateTime.UtcNow.Year, year)
            Dim list = Await HolidayListAsync(c, y, ct).ConfigureAwait(False)
            Dim sb As New StringBuilder($"Public holidays in {c} in {y}{If(String.IsNullOrWhiteSpace(region), "", $" (with {region.Trim().ToUpperInvariant()})")}:" & vbLf)
            For Each h In list
                Dim scope = If(h.IsGlobal, "", If(Applies(h, region), $" (regional: {region.Trim().ToUpperInvariant()})", $" (regional only: {ToolKit.Clip(h.Regions, 80)})"))
                sb.AppendLine($"{h.Day:yyyy-MM-dd} {h.Day.DayOfWeek,-9} {h.Name}{If(h.Local <> h.Name AndAlso h.Local <> "", $" ({h.Local})", "")}{scope}")
            Next
            Return sb.ToString().TrimEnd()
        End Function

        Friend Async Function BusinessDaysAsync(<Description("Country code, e.g. US")> country As String,
                                                <Description("Start date (yyyy-mm-dd)")> start As String,
                                                <Description("End date: count the working days after start up to and including it (optional)")> Optional [end] As String = "",
                                                <Description("Or: working days to add to start (negative goes back)")> Optional add_days As Integer = 0,
                                                <Description("Region code, e.g. US-CA (optional)")> Optional region As String = "",
                                                Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim c = LookupsServer.Country(country)
            Dim a As Date
            If Not Date.TryParse(start, Inv, DateTimeStyles.None, a) Then Throw New McpException($"start '{start}' is not a date (yyyy-mm-dd).")
            Dim holidaySet As New Dictionary(Of Date, String)
            Dim load = Async Function(year As Integer) As Task
                           For Each h In Await HolidayListAsync(c, year, ct).ConfigureAwait(False)
                               If Applies(h, region) Then holidaySet(h.Day) = h.Name
                           Next
                       End Function
            Dim isWorkday = Function(d As Date) d.DayOfWeek <> DayOfWeek.Saturday AndAlso d.DayOfWeek <> DayOfWeek.Sunday AndAlso Not holidaySet.ContainsKey(d)
            If Not String.IsNullOrWhiteSpace([end]) Then
                Dim b As Date
                If Not Date.TryParse([end], Inv, DateTimeStyles.None, b) Then Throw New McpException($"end '{[end]}' is not a date (yyyy-mm-dd).")
                Dim lo = If(b < a, b, a), hi = If(b < a, a, b)
                If (hi - lo).TotalDays > 3660 Then Throw New McpException("At most 10 years apart.")
                For y = lo.Year To hi.Year
                    Await load(y).ConfigureAwait(False)
                Next
                Dim count = 0
                Dim skipped As New List(Of String)
                Dim d = lo.AddDays(1)
                While d <= hi
                    If isWorkday(d) Then
                        count += 1
                    ElseIf holidaySet.ContainsKey(d) AndAlso d.DayOfWeek <> DayOfWeek.Saturday AndAlso d.DayOfWeek <> DayOfWeek.Sunday Then
                        skipped.Add($"{d:yyyy-MM-dd} {holidaySet(d)}")
                    End If
                    d = d.AddDays(1)
                End While
                Return $"{If(b < a, -count, count)} working day(s) in {c}{If(String.IsNullOrWhiteSpace(region), "", " " & region.Trim().ToUpperInvariant())} from {a:yyyy-MM-dd} to {b:yyyy-MM-dd} (start not counted, end counted)." &
                       If(skipped.Count > 0, vbLf & $"Holidays skipped ({skipped.Count}): " & String.Join("; ", skipped), vbLf & "No weekday holidays in between.")
            End If
            If add_days = 0 Then Throw New McpException("Give end (to count) or add_days (to add).")
            If Math.Abs(add_days) > 2500 Then Throw New McpException("add_days must be at most 2500.")
            Dim stepDir = Math.Sign(add_days)
            Dim left = Math.Abs(add_days)
            Dim cur = a
            Await load(cur.Year).ConfigureAwait(False)
            Dim passed As New List(Of String)
            While left > 0
                cur = cur.AddDays(stepDir)
                If Not holidaySet.Keys.Any(Function(k) k.Year = cur.Year) Then Await load(cur.Year).ConfigureAwait(False)
                If isWorkday(cur) Then
                    left -= 1
                ElseIf holidaySet.ContainsKey(cur) AndAlso cur.DayOfWeek <> DayOfWeek.Saturday AndAlso cur.DayOfWeek <> DayOfWeek.Sunday Then
                    passed.Add($"{cur:yyyy-MM-dd} {holidaySet(cur)}")
                End If
            End While
            Return $"{a:yyyy-MM-dd} {If(add_days > 0, "+", "-")} {Math.Abs(add_days)} working day(s) in {c}{If(String.IsNullOrWhiteSpace(region), "", " " & region.Trim().ToUpperInvariant())} = {cur:yyyy-MM-dd} ({cur.DayOfWeek})." &
                   If(passed.Count > 0, vbLf & "Holidays skipped: " & String.Join("; ", passed), "")
        End Function

        ' ------------------------------------------------------------------------------------------ currency

        Friend Async Function CurrencyAsync(<Description("Amount")> amount As Decimal,
                                            <Description("From currency, e.g. USD")> from As String,
                                            <Description("To currency or currencies, e.g. EUR or EUR,GBP,JPY")> [to] As String,
                                            <Description("Rate date yyyy-mm-dd (default: latest)")> Optional [date] As String = "",
                                            Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim f = If(from, "").Trim().ToUpperInvariant()
            Dim targets = ToolKit.SplitList([to]).Select(Function(t) t.ToUpperInvariant()).Where(Function(t) t <> f).Distinct().ToList()
            If Not Regex.IsMatch(f, "^[A-Z]{3}$") OrElse targets.Count = 0 OrElse targets.Any(Function(t) Not Regex.IsMatch(t, "^[A-Z]{3}$")) Then
                Throw New McpException("Use 3-letter currency codes, e.g. from USD to EUR,GBP.")
            End If
            Dim day = "latest"
            If Not String.IsNullOrWhiteSpace([date]) Then
                Dim d As Date
                If Not Date.TryParse([date], Inv, DateTimeStyles.None, d) Then Throw New McpException($"date '{[date]}' is not a date (yyyy-mm-dd).")
                day = d.ToString("yyyy-MM-dd", Inv)
            End If
            Dim j = Await GetJsonAsync($"{_ratesUrl}/{day}?base={f}&symbols={String.Join(",", targets)}", ct, notFoundIsNull:=True).ConfigureAwait(False)
            Dim rates = TryCast(j?("rates"), JsonObject)
            If rates Is Nothing OrElse rates.Count = 0 Then Throw New McpException($"No ECB rates for {f} to {String.Join(", ", targets)} (the ECB covers about 30 major currencies; crypto is not included).")
            Dim sb As New StringBuilder($"{amount.ToString("#,##0.##", Inv)} {f} at the ECB reference rate of {S(j("date"))}:" & vbLf)
            For Each t In targets
                If Not rates.ContainsKey(t) Then sb.AppendLine($"  {t}: no rate") : Continue For
                Dim rate = Decimal.Parse(rates(t).ToJsonString(), NumberStyles.Float, Inv)
                sb.AppendLine($"  = {Math.Round(amount * rate, 2, MidpointRounding.AwayFromZero).ToString("#,##0.00", Inv)} {t} (1 {f} = {rate.ToString("0.######", Inv)} {t}; 1 {t} = {Math.Round(1D / rate, 6).ToString("0.######", Inv)} {f})")
            Next
            sb.Append("Reference rates are for information; banks and card networks add their own margins.")
            Return sb.ToString()
        End Function

        ' ------------------------------------------------------------------------------------------ IP, WHOIS

        Friend Shared Function PrivateRange(ip As IPAddress) As String
            If IPAddress.IsLoopback(ip) Then Return "loopback (this machine)"
            If ip.AddressFamily = AddressFamily.InterNetworkV6 Then
                If ip.IsIPv6LinkLocal Then Return "IPv6 link-local"
                If ip.IsIPv6UniqueLocal Then Return "IPv6 unique local (private)"
                Return Nothing
            End If
            Dim b = ip.GetAddressBytes()
            If b(0) = 10 OrElse (b(0) = 172 AndAlso b(1) >= 16 AndAlso b(1) <= 31) OrElse (b(0) = 192 AndAlso b(1) = 168) Then Return "private network (RFC 1918)"
            If b(0) = 169 AndAlso b(1) = 254 Then Return "link-local (no DHCP address)"
            If b(0) = 100 AndAlso b(1) >= 64 AndAlso b(1) <= 127 Then Return "carrier-grade NAT (RFC 6598)"
            If b(0) = 0 OrElse b(0) >= 224 Then Return "reserved or multicast"
            Return Nothing
        End Function

        Private Shared Async Function ResolveAsync(query As String, ct As CancellationToken) As Task(Of IPAddress)
            Dim q = If(query, "").Trim()
            Dim ip As IPAddress = Nothing
            If IPAddress.TryParse(q, ip) Then Return ip
            Dim u As Uri = Nothing
            If q.Contains("://") AndAlso Uri.TryCreate(q, UriKind.Absolute, u) Then q = u.Host
            If Not Regex.IsMatch(q, "^[A-Za-z0-9.\-]+$") Then Throw New McpException($"'{query}' is not an IP address or host name.")
            Try
                Dim addrs = Await Dns.GetHostAddressesAsync(q, ct).ConfigureAwait(False)
                Dim v4 = addrs.FirstOrDefault(Function(a) a.AddressFamily = AddressFamily.InterNetwork)
                Return If(v4, addrs.FirstOrDefault())
            Catch ex As SocketException
                Throw New McpException($"{q} does not resolve: {ex.SocketErrorCode}")
            End Try
        End Function

        Friend Async Function IpLookupAsync(<Description("IP address or host name")> ip As String, Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim addr = Await ResolveAsync(ip, ct).ConfigureAwait(False)
            If addr Is Nothing Then Throw New McpException($"{ip} has no address.")
            Dim local = PrivateRange(addr)
            If local IsNot Nothing Then Return $"{addr}: {local}. It is not on the public internet, so it has no location."
            Dim j = Await GetJsonAsync($"{_ipUrl}/{addr}", ct).ConfigureAwait(False)
            If j Is Nothing OrElse S(j("success")) = "false" Then Return $"{addr}: no location data ({S(j?("message"))})."
            Dim conn = j("connection")
            Dim tz = j("timezone")
            Return $"{addr}{If(addr.ToString() <> ip.Trim(), $" ({ip.Trim()})", "")}: {S(j("city"))}, {S(j("region"))}, {S(j("country"))} ({S(j("country_code"))}), {S(j("continent"))}" & vbLf &
                   $"Approximate location: {S(j("latitude"))}, {S(j("longitude"))}; time zone {S(tz?("id"))} (UTC{S(tz?("utc"))})" & vbLf &
                   $"Network: {S(conn?("isp"))}{If(S(conn?("org")) <> "" AndAlso S(conn?("org")) <> S(conn?("isp")), " / " & S(conn?("org")), "")}, AS{S(conn?("asn"))}{If(S(conn?("domain")) <> "", ", " & S(conn?("domain")), "")}" & vbLf &
                   "IP locations are approximate (city level at best)."
        End Function

        Friend Async Function WhoisAsync(<Description("Domain (example.com) or IP address")> query As String, Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim q = If(query, "").Trim().ToLowerInvariant()
            Dim u As Uri = Nothing
            If q.Contains("://") AndAlso Uri.TryCreate(q, UriKind.Absolute, u) Then q = u.Host
            q = q.TrimEnd("."c)
            Dim ip As IPAddress = Nothing
            If IPAddress.TryParse(q, ip) Then
                Dim local = PrivateRange(ip)
                If local IsNot Nothing Then Return $"{ip}: {local}; WHOIS covers public addresses only."
                Dim j = Await GetJsonAsync($"{_rdapUrl}/ip/{ip}", ct).ConfigureAwait(False)
                If j Is Nothing Then Return $"No RDAP record for {ip}."
                Dim sb As New StringBuilder($"{ip}: network {S(j("name"))} ({S(j("handle"))}), {S(j("startAddress"))} - {S(j("endAddress"))}{If(S(j("country")) <> "", ", country " & S(j("country")), "")}" & vbLf)
                AppendEntities(sb, j)
                AppendEvents(sb, j)
                Return sb.ToString().TrimEnd()
            End If
            If Not Regex.IsMatch(q, "^([a-z0-9]([a-z0-9\-]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9\-]{1,62}$") Then Throw New McpException($"'{query}' is not a domain name or IP address.")
            ' The registrable domain: WHOIS knows example.co.uk / example.com, not www.example.com.
            Dim labels = q.Split("."c)
            Dim candidates = Enumerable.Range(0, Math.Max(1, labels.Length - 1)).Select(Function(i) String.Join(".", labels.Skip(i))).ToList()
            For Each cand In candidates
                Dim j = Await GetJsonAsync($"{_rdapUrl}/domain/{cand}", ct).ConfigureAwait(False)
                If j Is Nothing Then Continue For
                Dim sb As New StringBuilder($"{S(j("ldhName")).ToLowerInvariant()}{If(cand <> q, $" (for {q})", "")}" & vbLf)
                Dim status = j("status")?.AsArray().Select(Function(x) S(x)).ToList()
                If status IsNot Nothing AndAlso status.Count > 0 Then sb.AppendLine("Status: " & String.Join(", ", status))
                AppendEvents(sb, j)
                AppendEntities(sb, j)
                Dim ns = j("nameservers")?.AsArray().Select(Function(x) S(x("ldhName")).ToLowerInvariant()).ToList()
                If ns IsNot Nothing AndAlso ns.Count > 0 Then sb.AppendLine("Name servers: " & String.Join(", ", ns))
                Dim dnssec = j("secureDNS")?("delegationSigned")
                If dnssec IsNot Nothing Then sb.AppendLine("DNSSEC: " & If(S(dnssec) = "true", "signed", "not signed"))
                Return sb.ToString().TrimEnd()
            Next
            Return $"No RDAP record for {q} (unregistered, or its registry has no RDAP service)."
        End Function

        Private Shared Sub AppendEvents(sb As StringBuilder, j As JsonNode)
            Dim events = j("events")?.AsArray()
            If events Is Nothing Then Return
            For Each ev In events
                Dim action = S(ev("eventAction"))
                If action = "last update of RDAP database" Then Continue For
                Dim d As DateTimeOffset
                Dim parsed = DateTimeOffset.TryParse(S(ev("eventDate")), Inv, DateTimeStyles.AssumeUniversal, d)
                Dim whenText = If(parsed, d.ToString("yyyy-MM-dd", Inv), S(ev("eventDate")))
                Dim note = ""
                If action = "expiration" AndAlso parsed Then
                    Dim days = (d - DateTimeOffset.UtcNow).TotalDays
                    note = If(days < 0, " (EXPIRED)", $" ({days:0} days from now)")
                End If
                sb.AppendLine($"{Char.ToUpperInvariant(action(0))}{action.Substring(1)}: {whenText}{note}")
            Next
        End Sub

        Private Shared Sub AppendEntities(sb As StringBuilder, j As JsonNode)
            Dim entities = j("entities")?.AsArray()
            If entities Is Nothing Then Return
            For Each e In entities
                Dim roles = If(e("roles")?.AsArray().Select(Function(r) S(r)).ToList(), New List(Of String))
                Dim name = VcardValue(e, "fn")
                Dim email = VcardValue(e, "email")
                If roles.Contains("registrar") Then
                    sb.AppendLine($"Registrar: {If(name, S(e("handle")))}{If(e("publicIds") IsNot Nothing, $" (IANA {S(e("publicIds").AsArray().FirstOrDefault()?("identifier"))})", "")}")
                ElseIf roles.Contains("registrant") OrElse roles.Contains("abuse") OrElse roles.Contains("administrative") Then
                    If name IsNot Nothing OrElse email IsNot Nothing Then sb.AppendLine($"{String.Join("/", roles)}: {name}{If(email IsNot Nothing, " <" & email & ">", "")}")
                End If
                ' Abuse contacts often sit one level down (the registrar's or network owner's).
                For Each inner In If(e("entities")?.AsArray(), New JsonArray())
                    Dim innerRoles = If(inner("roles")?.AsArray().Select(Function(r) S(r)).ToList(), New List(Of String))
                    If innerRoles.Contains("abuse") Then
                        Dim ie = VcardValue(inner, "email"), ip = VcardValue(inner, "tel")
                        If ie IsNot Nothing OrElse ip IsNot Nothing Then sb.AppendLine($"Abuse contact: {ie}{If(ip IsNot Nothing, " " & ip, "")}")
                    End If
                Next
            Next
        End Sub

        Private Shared Function VcardValue(entity As JsonNode, field As String) As String
            Dim card = entity("vcardArray")?.AsArray()
            If card Is Nothing OrElse card.Count < 2 Then Return Nothing
            For Each prop In If(card(1)?.AsArray(), New JsonArray())
                Dim p = prop?.AsArray()
                If p IsNot Nothing AndAlso p.Count >= 4 AndAlso S(p(0)) = field Then
                    Dim v = S(p(3)).Replace("mailto:", "").Replace("tel:", "")
                    Return If(String.IsNullOrWhiteSpace(v), Nothing, v)
                End If
            Next
            Return Nothing
        End Function
    End Class

End Namespace
