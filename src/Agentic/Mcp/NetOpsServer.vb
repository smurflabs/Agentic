Option Strict On
Option Explicit On
Option Infer On

Imports System.ComponentModel
Imports System.Diagnostics
Imports System.Globalization
Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Net.NetworkInformation
Imports System.Net.Security
Imports System.Net.Sockets
Imports System.Security.Cryptography.X509Certificates
Imports System.Text
Imports System.Text.Json
Imports System.Text.RegularExpressions
Imports System.Threading
Imports ModelContextProtocol
Imports ModelContextProtocol.Server

Namespace Mcp

    ''' <summary>
    ''' Built-in "netops" MCP server ("Command": "{self}", "Arguments": ["--mcp-netops", "--known-hosts", "{data}\ssh_known_hosts"]): network
    ''' diagnostics - ping, DNS (any record type), TCP port checks, TLS certificates, HTTP checks, traceroute - and commands over SSH on
    ''' the hosts an administrator lists in SSH_HOSTS ("name=user@host:port, ..."), with SSH_KEY_&lt;NAME&gt; / SSH_PASSWORD_&lt;NAME&gt; (or
    ''' SSH_KEY / SSH_PASSWORD for all) as credentials. A host's key is remembered the first time and a changed key is refused.
    ''' </summary>
    Public NotInheritable Class NetOpsServer
        Friend NotInheritable Class SshHost
            Public Property Name As String
            Public Property User As String
            Public Property Host As String
            Public Property Port As Integer = 22
        End Class

        Private ReadOnly _hosts As New Dictionary(Of String, SshHost)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _knownHosts As String
        Private ReadOnly _http As HttpClient
        Private ReadOnly _dohUrl As String
        Private ReadOnly _maxChars As Integer
        Private Shared ReadOnly Inv As CultureInfo = CultureInfo.InvariantCulture

        Friend Sub New(hosts As IEnumerable(Of SshHost), knownHostsFile As String, Optional dohUrl As String = Nothing, Optional handler As HttpMessageHandler = Nothing, Optional maxOutputKb As Integer = 64)
            For Each h In If(hosts, Enumerable.Empty(Of SshHost)())
                _hosts(h.Name) = h
            Next
            _knownHosts = knownHostsFile
            _dohUrl = If(String.IsNullOrWhiteSpace(dohUrl), "https://cloudflare-dns.com/dns-query", dohUrl.Trim())
            _http = New HttpClient(If(handler, New SocketsHttpHandler With {.AllowAutoRedirect = False, .ConnectTimeout = TimeSpan.FromSeconds(10)})) With {.Timeout = TimeSpan.FromSeconds(30)}
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Agentic-netops/1.0")
            _maxChars = Math.Max(4, maxOutputKb) * 1024
        End Sub

        Public Shared Function RunStdio(args As String()) As Integer
            Dim known = If(ToolKit.Arg(args, "--known-hosts"), Path.Combine(AppContext.BaseDirectory, "data", "ssh_known_hosts"))
            Dim server As New NetOpsServer(ParseHosts(ToolKit.Env("SSH_HOSTS")), known, ToolKit.Env("NETOPS_DOH_URL"), Nothing, ToolKit.IntArg(args, "--max-output-kb", 64))
            Return BuiltInServer.Run("agentic-netops", server.CreateTools())
        End Function

        ''' <summary>"name=user@host:port" entries (port optional; name defaults to the host).</summary>
        Friend Shared Function ParseHosts(spec As String) As List(Of SshHost)
            Dim list As New List(Of SshHost)
            For Each entry In ToolKit.SplitList(spec)
                Dim m = Regex.Match(entry, "^(?:([\w.-]+)\s*=\s*)?([^@\s]+)@([^:\s]+)(?::(\d+))?$")
                If Not m.Success Then Continue For
                list.Add(New SshHost With {.Name = If(m.Groups(1).Success, m.Groups(1).Value, m.Groups(3).Value), .User = m.Groups(2).Value, .Host = m.Groups(3).Value,
                                           .Port = If(m.Groups(4).Success, Integer.Parse(m.Groups(4).Value, Inv), 22)})
            Next
            Return list
        End Function

        Public Function CreateTools() As McpServerPrimitiveCollection(Of McpServerTool)
            Dim tools As New McpServerPrimitiveCollection(Of McpServerTool)()
            ToolKit.Add(tools, New Func(Of String, Integer, CancellationToken, Task(Of String))(AddressOf PingAsync), "ping", "Ping a host: replies, round-trip times (min/avg/max) and packet loss.")
            ToolKit.Add(tools, New Func(Of String, String, CancellationToken, Task(Of String))(AddressOf DnsAsync), "dns_lookup",
                "Look up DNS records: A, AAAA (this server's resolver, so internal names work), MX, TXT, NS, CNAME, SOA, CAA, SRV (public DNS over HTTPS), PTR (reverse, for an IP).")
            ToolKit.Add(tools, New Func(Of String, String, Integer, CancellationToken, Task(Of String))(AddressOf PortsAsync), "check_ports",
                "Check whether TCP ports on a host accept connections: ports like '22,80,443' or '8000-8010' (at most 200).")
            ToolKit.Add(tools, New Func(Of String, Integer, CancellationToken, Task(Of String))(AddressOf TlsAsync), "check_tls",
                "A server's TLS certificate: subject, issuer, names it covers, validity and days left, chain problems, protocol.")
            ToolKit.Add(tools, New Func(Of String, String, String, Integer, CancellationToken, Task(Of String))(AddressOf HttpCheckAsync), "http_check",
                "Request a URL and report status, redirects, timing, size, key headers and the start of the body; optionally check that the body contains a text.")
            ToolKit.Add(tools, New Func(Of String, Integer, CancellationToken, Task(Of String))(AddressOf TraceAsync), "traceroute", "The network path to a host, hop by hop, with round-trip times.")
            If _hosts.Count > 0 Then
                ToolKit.Add(tools, New Func(Of String)(AddressOf SshHosts), "ssh_hosts", "The hosts ssh_run can reach.")
                ToolKit.Add(tools, New Func(Of String, String, Integer, CancellationToken, Task(Of String))(AddressOf SshRunAsync), "ssh_run",
                    $"Run a shell command on a host over SSH ({String.Join(", ", _hosts.Keys)}) and get its exit code and output. No interactive input; it is stopped after timeout_seconds.", False, True)
            End If
            Return tools
        End Function

        Private Shared Function CleanHost(host As String) As String
            Dim h = If(host, "").Trim()
            Dim u As Uri = Nothing
            If h.Contains("://") AndAlso Uri.TryCreate(h, UriKind.Absolute, u) Then h = u.Host
            If h.Length = 0 OrElse h.Length > 253 OrElse Not Regex.IsMatch(h, "^[A-Za-z0-9.:\-\[\]_]+$") Then Throw New McpException($"'{host}' is not a host name or IP address.")
            Return h.Trim("["c, "]"c)
        End Function

        ' ------------------------------------------------------------------------------------------ ping, traceroute

        Friend Async Function PingAsync(<Description("Host name or IP")> host As String,
                                        <Description("How many pings (default 4, at most 20)")> Optional count As Integer = 4,
                                        Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim h = CleanHost(host)
            Dim n = Math.Clamp(If(count <= 0, 4, count), 1, 20)
            Dim times As New List(Of Long)
            Dim sb As New StringBuilder()
            Using p As New Ping()
                For i = 1 To n
                    ct.ThrowIfCancellationRequested()
                    Try
                        Dim r = Await p.SendPingAsync(h, 2000).ConfigureAwait(False)
                        If r.Status = IPStatus.Success Then
                            times.Add(r.RoundtripTime)
                            sb.AppendLine($"reply from {r.Address}: {r.RoundtripTime} ms ttl={If(r.Options?.Ttl, 0)}")
                        Else
                            sb.AppendLine($"{r.Status}")
                        End If
                    Catch ex As PingException
                        Throw New McpException($"Cannot ping {h}: {If(ex.InnerException?.Message, ex.Message)}")
                    End Try
                    If i < n Then Await Task.Delay(300, ct).ConfigureAwait(False)
                Next
            End Using
            Dim loss = 100.0 * (n - times.Count) / n
            sb.Append($"{n} sent, {times.Count} received, {loss:0}% loss")
            If times.Count > 0 Then sb.Append($"; min/avg/max {times.Min()}/{times.Average():0.#}/{times.Max()} ms")
            Return sb.ToString()
        End Function

        Friend Async Function TraceAsync(<Description("Host name or IP")> host As String,
                                         <Description("Most hops (default 20, at most 30)")> Optional max_hops As Integer = 20,
                                         Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim h = CleanHost(host)
            Dim target As IPAddress = Nothing
            If Not IPAddress.TryParse(h, target) Then
                Dim addrs = Await Dns.GetHostAddressesAsync(h, ct).ConfigureAwait(False)
                target = addrs.FirstOrDefault(Function(a) a.AddressFamily = AddressFamily.InterNetwork)
                If target Is Nothing Then target = addrs.FirstOrDefault()
                If target Is Nothing Then Throw New McpException($"{h} has no address.")
            End If
            Dim sb As New StringBuilder($"Route to {h} ({target}):" & vbLf)
            Using p As New Ping()
                For ttl = 1 To Math.Clamp(If(max_hops <= 0, 20, max_hops), 1, 30)
                    ct.ThrowIfCancellationRequested()
                    Dim sw = Stopwatch.StartNew()
                    Dim r = Await p.SendPingAsync(target, 1500, New Byte(31) {}, New PingOptions(ttl, True)).ConfigureAwait(False)
                    sw.Stop()
                    Select Case r.Status
                        Case IPStatus.TtlExpired, IPStatus.TimeExceeded
                            sb.AppendLine($"{ttl,2}  {r.Address}  {sw.ElapsedMilliseconds} ms")
                        Case IPStatus.Success
                            sb.AppendLine($"{ttl,2}  {r.Address}  {r.RoundtripTime} ms  (reached)")
                            Return sb.ToString().TrimEnd()
                        Case Else
                            sb.AppendLine($"{ttl,2}  *  ({r.Status})")
                    End Select
                Next
            End Using
            Return sb.Append("(the target was not reached within the hop limit)").ToString()
        End Function

        ' ------------------------------------------------------------------------------------------ DNS

        Friend Async Function DnsAsync(<Description("Name (or IP for PTR)")> name As String,
                                       <Description("A (default), AAAA, MX, TXT, NS, CNAME, SOA, CAA, SRV or PTR")> Optional type As String = "A",
                                       Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim n = CleanHost(name)
            Dim t = If(String.IsNullOrWhiteSpace(type), "A", type.Trim().ToUpperInvariant())
            Try
                Select Case t
                    Case "A", "AAAA"
                        Dim fam = If(t = "A", AddressFamily.InterNetwork, AddressFamily.InterNetworkV6)
                        Dim addrs = (Await Dns.GetHostAddressesAsync(n, fam, ct).ConfigureAwait(False)).ToList()
                        Return If(addrs.Count = 0, $"No {t} records for {n}.", $"{n} {t}:{vbLf}" & String.Join(vbLf, addrs.Select(Function(a) a.ToString())))
                    Case "PTR"
                        Dim ip As IPAddress = Nothing
                        If Not IPAddress.TryParse(n, ip) Then Throw New McpException("PTR needs an IP address.")
                        Dim entry = Await Dns.GetHostEntryAsync(ip).ConfigureAwait(False)
                        Return $"{n} PTR {entry.HostName}"
                    Case "MX", "TXT", "NS", "CNAME", "SOA", "CAA", "SRV"
                        Return Await DohAsync(n, t, ct).ConfigureAwait(False)
                    Case Else
                        Throw New McpException($"Unknown record type '{type}'.")
                End Select
            Catch ex As SocketException
                Return $"{n}: {ex.SocketErrorCode} ({ex.Message})"
            End Try
        End Function

        Private Async Function DohAsync(name As String, type As String, ct As CancellationToken) As Task(Of String)
            Using req As New HttpRequestMessage(HttpMethod.Get, $"{_dohUrl}?name={Uri.EscapeDataString(name)}&type={type}")
                req.Headers.Accept.ParseAdd("application/dns-json")
                Using resp = Await _http.SendAsync(req, ct).ConfigureAwait(False)
                    Dim body = Await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(False)
                    If Not resp.IsSuccessStatusCode Then Throw New McpException($"DNS over HTTPS failed: {CInt(resp.StatusCode)}")
                    Using doc = JsonDocument.Parse(body)
                        Dim status = doc.RootElement.GetProperty("Status").GetInt32()
                        If status = 3 Then Return $"{name} does not exist (NXDOMAIN)."
                        Dim answers As JsonElement
                        If Not doc.RootElement.TryGetProperty("Answer", answers) OrElse answers.GetArrayLength() = 0 Then Return $"No {type} records for {name}."
                        Dim lines = answers.EnumerateArray().Select(Function(a) $"{a.GetProperty("name").GetString()} {a.GetProperty("TTL").GetInt32()}s {RecordType(a.GetProperty("type").GetInt32())} {a.GetProperty("data").GetString()}")
                        Return String.Join(vbLf, lines)
                    End Using
                End Using
            End Using
        End Function

        Private Shared Function RecordType(code As Integer) As String
            Select Case code
                Case 1 : Return "A"
                Case 2 : Return "NS"
                Case 5 : Return "CNAME"
                Case 6 : Return "SOA"
                Case 15 : Return "MX"
                Case 16 : Return "TXT"
                Case 28 : Return "AAAA"
                Case 33 : Return "SRV"
                Case 257 : Return "CAA"
            End Select
            Return "TYPE" & code.ToString(Inv)
        End Function

        ' ------------------------------------------------------------------------------------------ ports, TLS, HTTP

        Friend Shared Function ParsePorts(spec As String) As List(Of Integer)
            Dim ports As New SortedSet(Of Integer)
            For Each part In ToolKit.SplitList(spec)
                Dim m = Regex.Match(part, "^(\d+)(?:\s*-\s*(\d+))?$")
                If Not m.Success Then Throw New McpException($"'{part}' is not a port or range.")
                Dim a = Integer.Parse(m.Groups(1).Value, Inv), b = If(m.Groups(2).Success, Integer.Parse(m.Groups(2).Value, Inv), a)
                If a < 1 OrElse b > 65535 OrElse b < a Then Throw New McpException($"'{part}' is not a valid port range.")
                If b - a + ports.Count > 200 Then Throw New McpException("At most 200 ports per check.")
                For p = a To b
                    ports.Add(p)
                Next
            Next
            If ports.Count = 0 Then Throw New McpException("ports is required, e.g. '22,80,443'.")
            Return ports.ToList()
        End Function

        Friend Async Function PortsAsync(<Description("Host name or IP")> host As String,
                                         <Description("Ports, e.g. '22,80,443' or '8000-8010'")> ports As String,
                                         <Description("Timeout per port in ms (default 3000)")> Optional timeout_ms As Integer = 3000,
                                         Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim h = CleanHost(host)
            Dim list = ParsePorts(ports)
            Dim timeout = Math.Clamp(If(timeout_ms <= 0, 3000, timeout_ms), 200, 15000)
            Using gate As New SemaphoreSlim(32)
                Dim checks = list.Select(Async Function(port)
                                             Await gate.WaitAsync(ct).ConfigureAwait(False)
                                             Try
                                                 Dim sw = Stopwatch.StartNew()
                                                 Using tcp As New TcpClient()
                                                     Using cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                                                         cts.CancelAfter(timeout)
                                                         Try
                                                             Await tcp.ConnectAsync(h, port, cts.Token).ConfigureAwait(False)
                                                             Return (port, $"open ({sw.ElapsedMilliseconds} ms)", True)
                                                         Catch ex As OperationCanceledException When Not ct.IsCancellationRequested
                                                             Return (port, $"no answer (filtered?) after {timeout} ms", False)
                                                         Catch ex As SocketException
                                                             Return (port, If(ex.SocketErrorCode = SocketError.ConnectionRefused, "closed (refused)", ex.SocketErrorCode.ToString()), False)
                                                         End Try
                                                     End Using
                                                 End Using
                                             Finally
                                                 gate.Release()
                                             End Try
                                         End Function).ToList()
                Dim results = Await Task.WhenAll(checks).ConfigureAwait(False)
                Dim open = results.Where(Function(r) r.Item3).Select(Function(r) r.Item1).ToList()
                Dim sb As New StringBuilder($"{h}: {open.Count} of {results.Length} port(s) open{If(open.Count > 0, " (" & String.Join(", ", open) & ")", "")}" & vbLf)
                For Each r In results.Where(Function(x) x.Item3 OrElse results.Length <= 20)
                    sb.AppendLine($"{r.Item1}: {r.Item2}")
                Next
                Return sb.ToString().TrimEnd()
            End Using
        End Function

        Friend Async Function TlsAsync(<Description("Host name (or https URL)")> host As String,
                                       <Description("Port (default 443)")> Optional port As Integer = 443,
                                       Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim h = CleanHost(host)
            Dim p = If(port <= 0, 443, port)
            Dim errors As SslPolicyErrors = SslPolicyErrors.None
            Dim chainNotes As New List(Of String)
            Using tcp As New TcpClient()
                Using cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                    cts.CancelAfter(TimeSpan.FromSeconds(15))
                    Try
                        Await tcp.ConnectAsync(h, p, cts.Token).ConfigureAwait(False)
                    Catch ex As Exception When TypeOf ex Is SocketException OrElse TypeOf ex Is OperationCanceledException
                        Throw New McpException($"Cannot connect to {h}:{p}: {If(TypeOf ex Is OperationCanceledException, "no answer", ex.Message)}")
                    End Try
                    Using ssl As New SslStream(tcp.GetStream(), False, Function(sender, cert, chain, errs)
                                                                         errors = errs
                                                                         If chain IsNot Nothing Then
                                                                             For Each st In chain.ChainStatus
                                                                                 chainNotes.Add(st.StatusInformation.Trim())
                                                                             Next
                                                                         End If
                                                                         Return True
                                                                     End Function)
                        Try
                            Await ssl.AuthenticateAsClientAsync(New SslClientAuthenticationOptions With {.TargetHost = h}, cts.Token).ConfigureAwait(False)
                        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is System.Security.Authentication.AuthenticationException
                            Throw New McpException($"TLS handshake with {h}:{p} failed: {ex.Message}")
                        End Try
                        Using cert = New X509Certificate2(ssl.RemoteCertificate)
                            Dim sans = cert.Extensions.OfType(Of X509SubjectAlternativeNameExtension)().SelectMany(Function(e) e.EnumerateDnsNames()).ToList()
                            Dim days = (cert.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalDays
                            Dim sb As New StringBuilder()
                            sb.AppendLine($"{h}:{p} {ssl.SslProtocol}, {ssl.NegotiatedCipherSuite}")
                            sb.AppendLine($"Subject: {cert.Subject}")
                            sb.AppendLine($"Issuer: {cert.Issuer}")
                            If sans.Count > 0 Then sb.AppendLine($"Names: {String.Join(", ", sans.Take(30))}{If(sans.Count > 30, $" (+{sans.Count - 30})", "")}")
                            sb.AppendLine($"Valid: {cert.NotBefore.ToUniversalTime():yyyy-MM-dd} to {cert.NotAfter.ToUniversalTime():yyyy-MM-dd} UTC ({If(days < 0, $"EXPIRED {-days:0} days ago", $"{days:0} days left")})")
                            sb.AppendLine($"Serial: {cert.SerialNumber}, SHA-256: {cert.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256)}")
                            sb.Append(If(errors = SslPolicyErrors.None, "Trusted: yes", $"Trusted: NO ({errors}{If(chainNotes.Count > 0, ": " & String.Join("; ", chainNotes.Distinct()), "")})"))
                            Return sb.ToString()
                        End Using
                    End Using
                End Using
            End Using
        End Function

        Friend Async Function HttpCheckAsync(<Description("URL (http or https)")> url As String,
                                             <Description("GET (default) or HEAD")> Optional method As String = "GET",
                                             <Description("Text the body should contain (optional)")> Optional contains As String = "",
                                             <Description("Timeout in seconds (default 20)")> Optional timeout_seconds As Integer = 20,
                                             Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim u As Uri = Nothing
            If Not Uri.TryCreate(If(url, "").Trim(), UriKind.Absolute, u) OrElse (u.Scheme <> Uri.UriSchemeHttp AndAlso u.Scheme <> Uri.UriSchemeHttps) Then
                Throw New McpException("url must be an http:// or https:// address.")
            End If
            Dim verb = If(String.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase), HttpMethod.Head, HttpMethod.Get)
            Dim sb As New StringBuilder()
            Dim total = Stopwatch.StartNew()
            Using cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(If(timeout_seconds <= 0, 20, timeout_seconds), 1, 120)))
                Try
                    For hop = 0 To 5
                        Dim sw = Stopwatch.StartNew()
                        Using req As New HttpRequestMessage(verb, u)
                            Using resp = Await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(False)
                                Dim headersMs = sw.ElapsedMilliseconds
                                Dim code = CInt(resp.StatusCode)
                                If code >= 300 AndAlso code < 400 AndAlso resp.Headers.Location IsNot Nothing AndAlso hop < 5 Then
                                    Dim nextUri = If(resp.Headers.Location.IsAbsoluteUri, resp.Headers.Location, New Uri(u, resp.Headers.Location))
                                    sb.AppendLine($"{code} {u} -> {nextUri} ({headersMs} ms)")
                                    u = nextUri
                                    Continue For
                                End If
                                Dim body = If(verb = HttpMethod.Head, "", Await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(False))
                                sb.AppendLine($"{code} {resp.ReasonPhrase} from {u}")
                                sb.AppendLine($"Time: {headersMs} ms to headers, {total.ElapsedMilliseconds} ms in all; body {Encoding.UTF8.GetByteCount(body):N0} bytes; HTTP/{resp.Version}")
                                For Each name In {"Server", "Content-Type", "Content-Length", "Cache-Control", "Strict-Transport-Security", "Content-Security-Policy", "X-Frame-Options", "Last-Modified", "ETag"}
                                    Dim values As IEnumerable(Of String) = Nothing
                                    If resp.Headers.TryGetValues(name, values) OrElse resp.Content.Headers.TryGetValues(name, values) Then sb.AppendLine($"{name}: {ToolKit.Clip(String.Join(", ", values), 200)}")
                                Next
                                If Not String.IsNullOrEmpty(contains) Then
                                    sb.AppendLine(If(body.Contains(contains, StringComparison.OrdinalIgnoreCase), $"Body contains '{contains}': yes", $"Body contains '{contains}': NO"))
                                End If
                                If body.Length > 0 Then sb.AppendLine("Body starts:").Append(ToolKit.Clip(Regex.Replace(body, "\s+", " ").Trim(), 600))
                                Return sb.ToString().TrimEnd()
                            End Using
                        End Using
                    Next
                    Return sb.Append("Too many redirects.").ToString()
                Catch ex As OperationCanceledException When Not ct.IsCancellationRequested
                    Return sb.Append($"No answer within the timeout ({total.ElapsedMilliseconds} ms).").ToString()
                Catch ex As HttpRequestException
                    Return sb.Append($"Request failed: {ex.Message}{If(ex.InnerException IsNot Nothing, " (" & ex.InnerException.Message & ")", "")}").ToString()
                End Try
            End Using
        End Function

        ' ------------------------------------------------------------------------------------------ SSH

        Friend Function SshHosts() As String
            Return String.Join(vbLf, _hosts.Values.Select(Function(h) $"{h.Name}: {h.User}@{h.Host}:{h.Port}"))
        End Function

        Private Shared Function Credential(name As String, suffix As String) As String
            Return If(ToolKit.Env($"SSH_{suffix}_{Regex.Replace(name, "[^A-Za-z0-9]", "_").ToUpperInvariant()}"), ToolKit.Env("SSH_" & suffix))
        End Function

        ''' <summary>Trust on first use: the key is recorded the first time; a different key later is refused.</summary>
        Friend Function CheckHostKey(host As SshHost, fingerprint As String) As Boolean
            Dim id = $"[{host.Host}]:{host.Port}"
            Dim lines = If(File.Exists(_knownHosts), File.ReadAllLines(_knownHosts).ToList(), New List(Of String))
            Dim known = lines.Select(Function(l) l.Split(" "c)).FirstOrDefault(Function(p) p.Length >= 2 AndAlso p(0) = id)
            If known IsNot Nothing Then Return known(1) = fingerprint
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_knownHosts)))
            File.AppendAllLines(_knownHosts, {$"{id} {fingerprint}"})
            Return True
        End Function

        Friend Async Function SshRunAsync(<Description("Host name from ssh_hosts")> host As String,
                                          <Description("Shell command to run")> command As String,
                                          <Description("Stop it after this many seconds (default 60, at most 900)")> Optional timeout_seconds As Integer = 60,
                                          Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim h As SshHost = Nothing
            If Not _hosts.TryGetValue(If(host, "").Trim(), h) Then Throw New McpException($"Unknown host '{host}'. Hosts: {String.Join(", ", _hosts.Keys)}")
            If String.IsNullOrWhiteSpace(command) Then Throw New McpException("command is required.")
            Dim auth As New List(Of Renci.SshNet.AuthenticationMethod)
            Dim key = Credential(h.Name, "KEY")
            If key IsNot Nothing Then
                Dim keyFile As Renci.SshNet.PrivateKeyFile
                If key.Contains("PRIVATE KEY") Then
                    keyFile = New Renci.SshNet.PrivateKeyFile(New MemoryStream(Encoding.ASCII.GetBytes(key.Replace("\n", vbLf))), Credential(h.Name, "KEY_PASSPHRASE"))
                Else
                    keyFile = New Renci.SshNet.PrivateKeyFile(key, Credential(h.Name, "KEY_PASSPHRASE"))
                End If
                auth.Add(New Renci.SshNet.PrivateKeyAuthenticationMethod(h.User, keyFile))
            End If
            Dim password = Credential(h.Name, "PASSWORD")
            If password IsNot Nothing Then auth.Add(New Renci.SshNet.PasswordAuthenticationMethod(h.User, password))
            If auth.Count = 0 Then Throw New McpException($"No credentials for {h.Name} (SSH_KEY_{h.Name.ToUpperInvariant()} or SSH_PASSWORD_{h.Name.ToUpperInvariant()}).")
            Dim info As New Renci.SshNet.ConnectionInfo(h.Host, h.Port, h.User, auth.ToArray()) With {.Timeout = TimeSpan.FromSeconds(20)}
            Dim keyRejected = False
            Using client As New Renci.SshNet.SshClient(info)
                AddHandler client.HostKeyReceived, Sub(sender, e)
                                                       e.CanTrust = CheckHostKey(h, e.FingerPrintSHA256)
                                                       keyRejected = Not e.CanTrust
                                                   End Sub
                Try
                    Await client.ConnectAsync(ct).ConfigureAwait(False)
                Catch ex As Exception When TypeOf ex Is Renci.SshNet.Common.SshException OrElse TypeOf ex Is SocketException OrElse TypeOf ex Is OperationCanceledException
                    If keyRejected Then Throw New McpException($"{h.Name}'s host key changed since it was first seen; refusing to connect. An administrator must check the server and remove its line from {_knownHosts}.")
                    Throw New McpException($"Cannot connect to {h.Name} ({h.Host}:{h.Port}): {ex.Message}")
                End Try
                Dim timeout = TimeSpan.FromSeconds(Math.Clamp(If(timeout_seconds <= 0, 60, timeout_seconds), 1, 900))
                Using cmd = client.CreateCommand(command)
                    cmd.CommandTimeout = timeout
                    Dim sw = Stopwatch.StartNew()
                    Dim timedOut = False
                    Try
                        Await cmd.ExecuteAsync(ct).ConfigureAwait(False)
                    Catch ex As Renci.SshNet.Common.SshOperationTimeoutException
                        timedOut = True
                    End Try
                    client.Disconnect()
                    Dim out = If(cmd.Result, "")
                    Dim err = If(cmd.Error, "")
                    Dim sb As New StringBuilder()
                    sb.AppendLine(If(timedOut, $"Stopped after {timeout.TotalSeconds:0} s (timeout).", $"Exit code {If(cmd.ExitStatus, -1)} on {h.Name} after {sw.ElapsedMilliseconds} ms."))
                    If out.Length > 0 Then sb.AppendLine(ToolKit.Clip(out.TrimEnd(), _maxChars))
                    If err.Length > 0 Then sb.AppendLine("stderr:").AppendLine(ToolKit.Clip(err.TrimEnd(), _maxChars \ 2))
                    Return sb.ToString().TrimEnd()
                End Using
            End Using
        End Function
    End Class

End Namespace
