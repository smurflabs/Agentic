Option Strict On
Option Explicit On
Option Infer On

Imports System.ComponentModel
Imports System.Globalization
Imports System.IO
Imports System.IO.Pipes
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
    ''' Built-in "docker" MCP server ("Command": "{self}", "Arguments": ["--mcp-docker"]): containers, images, logs, stats and Compose
    ''' projects through the Docker Engine API (no docker CLI needed) on the hosts in DOCKER_HOSTS - "name=tcp://host:2375",
    ''' "name=npipe:////./pipe/docker_engine" or "name=unix:///var/run/docker.sock", separated by commas. Changing tools (start, stop,
    ''' restart, remove, pull, exec) are marked destructive so the host can ask for approval.
    ''' </summary>
    Public NotInheritable Class DockerServer
        Private ReadOnly _hosts As New Dictionary(Of String, (Endpoint As String, Http As HttpClient))(StringComparer.OrdinalIgnoreCase)
        Private Shared ReadOnly Inv As CultureInfo = CultureInfo.InvariantCulture

        Friend Sub New(hosts As IEnumerable(Of (Name As String, Endpoint As String)), Optional handlerFor As Func(Of String, HttpMessageHandler) = Nothing)
            For Each h In hosts
                Dim handler = If(handlerFor IsNot Nothing, handlerFor(h.Endpoint), CreateHandler(h.Endpoint))
                Dim baseUri = If(h.Endpoint.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase), "http://" & h.Endpoint.Substring(6),
                              If(h.Endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase), h.Endpoint, "http://docker"))
                _hosts(h.Name) = (h.Endpoint, New HttpClient(handler) With {.BaseAddress = New Uri(baseUri.TrimEnd("/"c) & "/"), .Timeout = TimeSpan.FromMinutes(10)})
            Next
        End Sub

        Public Shared Function RunStdio(args As String()) As Integer
            Dim server As New DockerServer(ParseHosts(ToolKit.Env("DOCKER_HOSTS")))
            Return BuiltInServer.Run("agentic-docker", server.CreateTools())
        End Function

        Friend Shared Function ParseHosts(spec As String) As List(Of (Name As String, Endpoint As String))
            Dim list As New List(Of (Name As String, Endpoint As String))
            For Each entry In ToolKit.SplitList(spec)
                Dim eq = entry.IndexOf("="c)
                Dim name = If(eq > 0, entry.Substring(0, eq).Trim(), "default")
                Dim ep = If(eq > 0, entry.Substring(eq + 1).Trim(), entry.Trim())
                If Regex.IsMatch(ep, "^(tcp|https?|npipe|unix)://", RegexOptions.IgnoreCase) Then list.Add((name, ep))
            Next
            Return list
        End Function

        ''' <summary>A handler that reaches the engine over TCP, a Windows named pipe or a Unix socket.</summary>
        Private Shared Function CreateHandler(endpoint As String) As HttpMessageHandler
            Dim h As New SocketsHttpHandler With {.ConnectTimeout = TimeSpan.FromSeconds(10)}
            If endpoint.StartsWith("npipe://", StringComparison.OrdinalIgnoreCase) Then
                Dim pipe = endpoint.Substring("npipe://".Length).Replace("/"c, "\"c).TrimStart("\"c)
                Dim name = pipe.Substring(pipe.IndexOf("pipe\", StringComparison.OrdinalIgnoreCase) + 5)
                h.ConnectCallback = Function(ctx, ct) New ValueTask(Of Stream)(ConnectPipeAsync(name, ct))
            ElseIf endpoint.StartsWith("unix://", StringComparison.OrdinalIgnoreCase) Then
                Dim socketPath = endpoint.Substring("unix://".Length)
                h.ConnectCallback = Function(ctx, ct) New ValueTask(Of Stream)(ConnectUnixAsync(socketPath, ct))
            End If
            Return h
        End Function

        Private Shared Async Function ConnectPipeAsync(name As String, ct As CancellationToken) As Task(Of Stream)
            Dim pipe As New NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous)
            Await pipe.ConnectAsync(10000, ct).ConfigureAwait(False)
            Return pipe
        End Function

        Private Shared Async Function ConnectUnixAsync(socketPath As String, ct As CancellationToken) As Task(Of Stream)
            Dim sock As New Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
            Await sock.ConnectAsync(New UnixDomainSocketEndPoint(socketPath), ct).ConfigureAwait(False)
            Return New NetworkStream(sock, ownsSocket:=True)
        End Function

        Public Function CreateTools() As McpServerPrimitiveCollection(Of McpServerTool)
            Dim tools As New McpServerPrimitiveCollection(Of McpServerTool)()
            Dim hostArg = If(_hosts.Count = 0, " (no Docker hosts are configured: an administrator sets DOCKER_HOSTS)", $" host: {String.Join(", ", _hosts.Keys)}{If(_hosts.Count = 1, " (default)", "")}.")
            ToolKit.Add(tools, New Func(Of String, CancellationToken, Task(Of String))(AddressOf InfoAsync), "docker_info", "Docker engine version, OS, CPUs, memory and container/image counts." & hostArg)
            ToolKit.Add(tools, New Func(Of String, Boolean, String, CancellationToken, Task(Of String))(AddressOf ContainersAsync), "list_containers",
                "Containers: id, name, image, state, status, ports and Compose project (all: include stopped; name_filter: part of the name)." & hostArg)
            ToolKit.Add(tools, New Func(Of String, String, CancellationToken, Task(Of String))(AddressOf InspectAsync), "inspect_container",
                "A container's details: image, command, state, health, restarts, exit code, ports, mounts, networks, labels and environment variable names (values hidden)." & hostArg)
            ToolKit.Add(tools, New Func(Of String, String, Integer, String, CancellationToken, Task(Of String))(AddressOf LogsAsync), "container_logs",
                "A container's recent log lines (tail, default 200; since: e.g. 10m, 2h or an ISO time), stdout and stderr with timestamps." & hostArg)
            ToolKit.Add(tools, New Func(Of String, String, CancellationToken, Task(Of String))(AddressOf StatsAsync), "container_stats", "A running container's CPU %, memory, network and disk I/O right now." & hostArg)
            ToolKit.Add(tools, New Func(Of String, CancellationToken, Task(Of String))(AddressOf ComposeAsync), "list_compose_projects", "Compose projects on the host with their services and states." & hostArg)
            ToolKit.Add(tools, New Func(Of String, CancellationToken, Task(Of String))(AddressOf ImagesAsync), "list_images", "Images: tags, size and age." & hostArg)
            ToolKit.Add(tools, New Func(Of String, String, String, Integer, CancellationToken, Task(Of String))(AddressOf ControlAsync), "control_container",
                "start, stop or restart a container (timeout_seconds before a stop is forced, default 10)." & hostArg, False, True)
            ToolKit.Add(tools, New Func(Of String, String, Boolean, CancellationToken, Task(Of String))(AddressOf RemoveAsync), "remove_container",
                "Remove a stopped container (force: also a running one). Its anonymous volumes are kept." & hostArg, False, True)
            ToolKit.Add(tools, New Func(Of String, String, CancellationToken, Task(Of String))(AddressOf PullAsync), "pull_image", "Pull an image (name:tag) from its registry." & hostArg, False, True)
            ToolKit.Add(tools, New Func(Of String, String, String, Integer, CancellationToken, Task(Of String))(AddressOf ExecAsync), "exec_in_container",
                "Run a command inside a running container (sh -c) and get its exit code and output; stopped after timeout_seconds (default 60)." & hostArg, False, True)
            Return tools
        End Function

        ' ------------------------------------------------------------------------------------------ engine API

        Private Function Engine(host As String) As HttpClient
            If _hosts.Count = 0 Then Throw New McpException("No Docker hosts are configured (DOCKER_HOSTS).")
            If String.IsNullOrWhiteSpace(host) Then
                If _hosts.Count = 1 Then Return _hosts.Values.First().Http
                Throw New McpException($"host is required: {String.Join(", ", _hosts.Keys)}.")
            End If
            Dim h As (Endpoint As String, Http As HttpClient) = Nothing
            If Not _hosts.TryGetValue(host.Trim(), h) Then Throw New McpException($"Unknown Docker host '{host}'. Hosts: {String.Join(", ", _hosts.Keys)}.")
            Return h.Http
        End Function

        Private Shared Function Id(container As String) As String
            Dim c = If(container, "").Trim().TrimStart("/"c)
            If c.Length = 0 OrElse Not Regex.IsMatch(c, "^[A-Za-z0-9][A-Za-z0-9_.\-]*$") Then Throw New McpException($"'{container}' is not a container id or name.")
            Return Uri.EscapeDataString(c)
        End Function

        Private Shared Async Function SendAsync(http As HttpClient, method As HttpMethod, path As String, ct As CancellationToken, Optional body As JsonNode = Nothing,
                                               Optional timeout As TimeSpan? = Nothing) As Task(Of (Status As Integer, Body As Byte()))
            Using req As New HttpRequestMessage(method, path)
                If body IsNot Nothing Then req.Content = New StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
                Using cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                    cts.CancelAfter(If(timeout, TimeSpan.FromSeconds(60)))
                    Try
                        Using resp = Await http.SendAsync(req, cts.Token).ConfigureAwait(False)
                            Dim bytes = Await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(False)
                            Return (CInt(resp.StatusCode), bytes)
                        End Using
                    Catch ex As HttpRequestException
                        Throw New McpException("Cannot reach the Docker engine: " & If(ex.InnerException?.Message, ex.Message))
                    Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is TimeoutException
                        Throw New McpException("Cannot reach the Docker engine: " & ex.Message)
                    Catch ex As OperationCanceledException When Not ct.IsCancellationRequested
                        Throw New McpException("The Docker engine did not answer in time.")
                    End Try
                End Using
            End Using
        End Function

        Private Shared Async Function JsonAsync(http As HttpClient, path As String, ct As CancellationToken) As Task(Of JsonNode)
            Dim r = Await SendAsync(http, HttpMethod.Get, path, ct).ConfigureAwait(False)
            Dim text = Encoding.UTF8.GetString(r.Body)
            If r.Status = 404 Then Throw New McpException("Not found: " & EngineMessage(text))
            If r.Status >= 400 Then Throw New McpException($"Docker engine error {r.Status}: {EngineMessage(text)}")
            Return JsonNode.Parse(text)
        End Function

        Private Shared Function EngineMessage(text As String) As String
            Try
                Return If(JsonNode.Parse(text)?("message")?.GetValue(Of String)(), text)
            Catch ex As Exception When TypeOf ex Is Text.Json.JsonException OrElse TypeOf ex Is InvalidOperationException
                Return ToolKit.Clip(text, 300)
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

        Private Shared Function L(n As JsonNode) As Long
            If n Is Nothing Then Return 0
            Try
                Return n.GetValue(Of Long)()
            Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is FormatException
                Return CLng(n.GetValue(Of Double)())
            End Try
        End Function

        Friend Shared Function Size(bytes As Long) As String
            Dim units = {"B", "KB", "MB", "GB", "TB"}
            Dim v = CDbl(bytes)
            Dim i = 0
            While v >= 1024 AndAlso i < units.Length - 1
                v /= 1024 : i += 1
            End While
            Return v.ToString(If(i = 0, "0", "0.#"), Inv) & " " & units(i)
        End Function

        ''' <summary>Docker's multiplexed stdout/stderr stream (8-byte frame headers) as text; plain text (TTY) passes through.</summary>
        Friend Shared Function Demux(data As Byte()) As String
            If data.Length < 8 OrElse data(0) > 2 OrElse data(1) <> 0 OrElse data(2) <> 0 OrElse data(3) <> 0 Then Return Encoding.UTF8.GetString(data)
            Dim sb As New StringBuilder()
            Dim i = 0
            While i + 8 <= data.Length
                Dim stream = data(i)
                Dim len = (CInt(data(i + 4)) << 24) Or (CInt(data(i + 5)) << 16) Or (CInt(data(i + 6)) << 8) Or data(i + 7)
                i += 8
                If len < 0 OrElse i + len > data.Length Then len = data.Length - i
                Dim text = Encoding.UTF8.GetString(data, i, len)
                If stream = 2 Then text = String.Join(vbLf, text.TrimEnd(ControlChars.Lf).Split(ControlChars.Lf).Select(Function(l) "[stderr] " & l)) & vbLf
                sb.Append(text)
                i += len
            End While
            Return sb.ToString()
        End Function

        ' ------------------------------------------------------------------------------------------ tools

        Friend Async Function InfoAsync(<Description("Docker host (optional when there is one)")> Optional host As String = "", Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim http = Engine(host)
            Dim info = Await JsonAsync(http, "info", ct).ConfigureAwait(False)
            Dim ver = Await JsonAsync(http, "version", ct).ConfigureAwait(False)
            Return $"Docker {S(ver("Version"))} (API {S(ver("ApiVersion"))}) on {S(info("OperatingSystem"))} {S(info("Architecture"))}, {S(info("NCPU"))} CPUs, {Size(L(info("MemTotal")))} memory" & vbLf &
                   $"Containers: {S(info("ContainersRunning"))} running, {S(info("ContainersPaused"))} paused, {S(info("ContainersStopped"))} stopped; images: {S(info("Images"))}" & vbLf &
                   $"Storage driver: {S(info("Driver"))}; root: {S(info("DockerRootDir"))}; name: {S(info("Name"))}"
        End Function

        Friend Async Function ContainersAsync(<Description("Docker host")> Optional host As String = "",
                                              <Description("Include stopped containers (default true)")> Optional all As Boolean = True,
                                              <Description("Only names containing this (optional)")> Optional name_filter As String = "",
                                              Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim list = (Await JsonAsync(Engine(host), "containers/json?all=" & If(all, "1", "0"), ct).ConfigureAwait(False)).AsArray()
            Dim rows As New List(Of List(Of String)) From {New List(Of String) From {"id", "name", "image", "state", "status", "ports", "compose"}}
            For Each c In list
                Dim name = S(c("Names")?.AsArray().FirstOrDefault()).TrimStart("/"c)
                If Not String.IsNullOrWhiteSpace(name_filter) AndAlso Not name.Contains(name_filter.Trim(), StringComparison.OrdinalIgnoreCase) Then Continue For
                Dim ports = If(c("Ports")?.AsArray().Where(Function(p) p("PublicPort") IsNot Nothing).Select(Function(p) $"{S(p("PublicPort"))}->{S(p("PrivatePort"))}/{S(p("Type"))}").Distinct().ToList(), New List(Of String))
                Dim labels = c("Labels")
                Dim compose = If(labels?("com.docker.compose.project") IsNot Nothing, $"{S(labels("com.docker.compose.project"))}/{S(labels("com.docker.compose.service"))}", "")
                rows.Add(New List(Of String) From {S(c("Id")).Substring(0, Math.Min(12, S(c("Id")).Length)), name, S(c("Image")), S(c("State")), S(c("Status")), String.Join(" ", ports), compose})
            Next
            If rows.Count = 1 Then Return "No containers" & If(String.IsNullOrWhiteSpace(name_filter), ".", $" named like '{name_filter}'.")
            Return $"{rows.Count - 1} container(s):{vbLf}" & TableText.ToMarkdown(rows, 500)
        End Function

        Friend Async Function InspectAsync(<Description("Docker host")> host As String,
                                           <Description("Container id or name")> container As String,
                                           Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim c = Await JsonAsync(Engine(host), $"containers/{Id(container)}/json", ct).ConfigureAwait(False)
            Dim st = c("State")
            Dim cfg = c("Config")
            Dim hc = c("HostConfig")
            Dim sb As New StringBuilder()
            sb.AppendLine($"{S(c("Name")).TrimStart("/"c)} ({S(c("Id")).Substring(0, 12)}) image {S(cfg?("Image"))}, created {S(c("Created"))}")
            sb.AppendLine($"State: {S(st?("Status"))}{If(S(st?("Health")?("Status")) <> "", ", health " & S(st("Health")("Status")), "")}, started {S(st?("StartedAt"))}, exit code {S(st?("ExitCode"))}{If(S(st?("Error")) <> "", ", error: " & S(st("Error")), "")}{If(S(st?("OOMKilled")) = "true", ", OOM killed", "")}")
            sb.AppendLine($"Restarts: {S(c("RestartCount"))} (policy {S(hc?("RestartPolicy")?("Name"))})")
            sb.AppendLine($"Command: {String.Join(" ", If(cfg?("Entrypoint")?.AsArray().Select(Function(x) S(x)), Enumerable.Empty(Of String)()).Concat(If(cfg?("Cmd")?.AsArray().Select(Function(x) S(x)), Enumerable.Empty(Of String)())))}")
            Dim ports = c("NetworkSettings")?("Ports")?.AsObject()
            If ports IsNot Nothing Then sb.AppendLine("Ports: " & String.Join(", ", ports.Select(Function(kv) kv.Key & If(kv.Value Is Nothing, "", " <- " & String.Join(" ", kv.Value.AsArray().Select(Function(b) $"{S(b("HostIp"))}:{S(b("HostPort"))}"))))))
            Dim mounts = c("Mounts")?.AsArray()
            If mounts IsNot Nothing AndAlso mounts.Count > 0 Then sb.AppendLine("Mounts: " & String.Join("; ", mounts.Select(Function(m) $"{S(m("Type"))} {If(S(m("Name")) <> "", S(m("Name")), S(m("Source")))} -> {S(m("Destination"))}{If(S(m("RW")) = "false", " (ro)", "")}")))
            Dim nets = c("NetworkSettings")?("Networks")?.AsObject()
            If nets IsNot Nothing Then sb.AppendLine("Networks: " & String.Join(", ", nets.Select(Function(kv) $"{kv.Key} {S(kv.Value?("IPAddress"))}")))
            Dim env = cfg?("Env")?.AsArray()
            If env IsNot Nothing AndAlso env.Count > 0 Then sb.AppendLine("Environment (names only): " & String.Join(", ", env.Select(Function(e) S(e).Split("="c)(0))))
            Dim labels = cfg?("Labels")?.AsObject()
            If labels IsNot Nothing AndAlso labels.Count > 0 Then sb.AppendLine("Labels: " & ToolKit.Clip(String.Join("; ", labels.Select(Function(kv) $"{kv.Key}={S(kv.Value)}")), 1500))
            If hc IsNot Nothing Then sb.Append($"Limits: memory {If(L(hc("Memory")) > 0, Size(L(hc("Memory"))), "none")}, CPUs {If(L(hc("NanoCpus")) > 0, (L(hc("NanoCpus")) / 1.0E+9).ToString("0.##", Inv), "no limit")}")
            Return sb.ToString().TrimEnd()
        End Function

        Friend Async Function LogsAsync(<Description("Docker host")> host As String,
                                        <Description("Container id or name")> container As String,
                                        <Description("Last lines (default 200, at most 5000)")> Optional tail As Integer = 200,
                                        <Description("Only since, e.g. 10m, 2h or an ISO time (optional)")> Optional since As String = "",
                                        Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim q = $"containers/{Id(container)}/logs?stdout=1&stderr=1&timestamps=1&tail={Math.Clamp(If(tail <= 0, 200, tail), 1, 5000)}"
            If Not String.IsNullOrWhiteSpace(since) Then
                Dim m = Regex.Match(since.Trim(), "^(\d+)\s*([smhd])$", RegexOptions.IgnoreCase)
                Dim at As DateTimeOffset
                If m.Success Then
                    Dim n = Integer.Parse(m.Groups(1).Value, Inv)
                    Dim span As TimeSpan
                    Select Case m.Groups(2).Value.ToLowerInvariant()
                        Case "s" : span = TimeSpan.FromSeconds(n)
                        Case "m" : span = TimeSpan.FromMinutes(n)
                        Case "h" : span = TimeSpan.FromHours(n)
                        Case Else : span = TimeSpan.FromDays(n)
                    End Select
                    at = DateTimeOffset.UtcNow - span
                ElseIf Not DateTimeOffset.TryParse(since.Trim(), Inv, DateTimeStyles.AssumeUniversal, at) Then
                    Throw New McpException("since must look like 10m, 2h, 1d or an ISO time.")
                End If
                q &= "&since=" & at.ToUnixTimeSeconds().ToString(Inv)
            End If
            Dim r = Await SendAsync(Engine(host), HttpMethod.Get, q, ct).ConfigureAwait(False)
            If r.Status >= 400 Then Throw New McpException($"Docker engine error {r.Status}: {EngineMessage(Encoding.UTF8.GetString(r.Body))}")
            Dim text = Demux(r.Body)
            If text.Trim().Length = 0 Then Return "No log lines."
            Return ToolKit.Clip(text.TrimEnd(), 100000)
        End Function

        Friend Async Function StatsAsync(<Description("Docker host")> host As String,
                                         <Description("Container id or name")> container As String,
                                         Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim stats = Await JsonAsync(Engine(host), $"containers/{Id(container)}/stats?stream=false&one-shot=false", ct).ConfigureAwait(False)
            Dim cpuDelta = L(stats("cpu_stats")?("cpu_usage")?("total_usage")) - L(stats("precpu_stats")?("cpu_usage")?("total_usage"))
            Dim sysDelta = L(stats("cpu_stats")?("system_cpu_usage")) - L(stats("precpu_stats")?("system_cpu_usage"))
            Dim cpus = Math.Max(1L, L(stats("cpu_stats")?("online_cpus")))
            Dim cpu = If(sysDelta > 0 AndAlso cpuDelta >= 0, cpuDelta / CDbl(sysDelta) * cpus * 100.0, 0.0)
            Dim memUsed = L(stats("memory_stats")?("usage")) - L(stats("memory_stats")?("stats")?("inactive_file"))
            Dim memLimit = L(stats("memory_stats")?("limit"))
            Dim rx = 0L, tx = 0L
            For Each kv In If(stats("networks")?.AsObject(), New JsonObject())
                rx += L(kv.Value?("rx_bytes")) : tx += L(kv.Value?("tx_bytes"))
            Next
            Dim readB = 0L, writeB = 0L
            For Each e In If(stats("blkio_stats")?("io_service_bytes_recursive")?.AsArray(), New JsonArray())
                If S(e("op")).Equals("read", StringComparison.OrdinalIgnoreCase) Then readB += L(e("value"))
                If S(e("op")).Equals("write", StringComparison.OrdinalIgnoreCase) Then writeB += L(e("value"))
            Next
            Return $"{S(stats("name")).TrimStart("/"c)}: CPU {cpu.ToString("0.0", Inv)}% of {cpus} CPU(s); memory {Size(memUsed)} of {Size(memLimit)} ({If(memLimit > 0, (100.0 * memUsed / memLimit).ToString("0.0", Inv), "?")}%); " &
                   $"network in {Size(rx)} / out {Size(tx)}; disk read {Size(readB)} / write {Size(writeB)}; processes {S(stats("pids_stats")?("current"))}"
        End Function

        Friend Async Function ComposeAsync(<Description("Docker host")> Optional host As String = "", Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim list = (Await JsonAsync(Engine(host), "containers/json?all=1", ct).ConfigureAwait(False)).AsArray()
            Dim projects = list.Where(Function(c) c("Labels")?("com.docker.compose.project") IsNot Nothing).
                GroupBy(Function(c) S(c("Labels")("com.docker.compose.project"))).OrderBy(Function(g) g.Key).ToList()
            If projects.Count = 0 Then Return "No Compose projects."
            Dim sb As New StringBuilder()
            For Each g In projects
                Dim dir = S(g.First()("Labels")?("com.docker.compose.project.working_dir"))
                sb.AppendLine($"{g.Key}{If(dir <> "", $" ({dir})", "")}: {g.Count(Function(c) S(c("State")) = "running")}/{g.Count()} running")
                For Each c In g.OrderBy(Function(x) S(x("Labels")("com.docker.compose.service")))
                    sb.AppendLine($"  - {S(c("Labels")("com.docker.compose.service"))}: {S(c("State"))} ({S(c("Status"))}) {S(c("Image"))}")
                Next
            Next
            Return sb.ToString().TrimEnd()
        End Function

        Friend Async Function ImagesAsync(<Description("Docker host")> Optional host As String = "", Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim list = (Await JsonAsync(Engine(host), "images/json", ct).ConfigureAwait(False)).AsArray()
            Dim rows As New List(Of List(Of String)) From {New List(Of String) From {"tags", "id", "size", "created"}}
            For Each img In list.OrderByDescending(Function(i) L(i("Created")))
                Dim tags = If(img("RepoTags")?.AsArray().Select(Function(t) S(t)).Where(Function(t) t <> "<none>:<none>").ToList(), New List(Of String))
                rows.Add(New List(Of String) From {If(tags.Count = 0, "<untagged>", String.Join(" ", tags)), S(img("Id")).Replace("sha256:", "").Substring(0, 12), Size(L(img("Size"))),
                                                   DateTimeOffset.FromUnixTimeSeconds(L(img("Created"))).ToString("yyyy-MM-dd", Inv)})
            Next
            Return $"{rows.Count - 1} image(s):{vbLf}" & TableText.ToMarkdown(rows, 500)
        End Function

        Friend Async Function ControlAsync(<Description("Docker host")> host As String,
                                           <Description("Container id or name")> container As String,
                                           <Description("start, stop or restart")> action As String,
                                           <Description("Seconds before a stop is forced (default 10)")> Optional timeout_seconds As Integer = 10,
                                           Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim act = If(action, "").Trim().ToLowerInvariant()
            If act <> "start" AndAlso act <> "stop" AndAlso act <> "restart" Then Throw New McpException("action must be start, stop or restart.")
            Dim t = Math.Clamp(If(timeout_seconds <= 0, 10, timeout_seconds), 1, 300)
            Dim r = Await SendAsync(Engine(host), HttpMethod.Post, $"containers/{Id(container)}/{act}{If(act = "start", "", "?t=" & t.ToString(Inv))}", ct, timeout:=TimeSpan.FromSeconds(t + 60)).ConfigureAwait(False)
            If r.Status = 304 Then Return $"{container} was already {If(act = "start", "running", "stopped")}."
            If r.Status >= 400 Then Throw New McpException($"Docker engine error {r.Status}: {EngineMessage(Encoding.UTF8.GetString(r.Body))}")
            Return $"{container}: {act} done."
        End Function

        Friend Async Function RemoveAsync(<Description("Docker host")> host As String,
                                          <Description("Container id or name")> container As String,
                                          <Description("Also remove it when running")> Optional force As Boolean = False,
                                          Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim r = Await SendAsync(Engine(host), HttpMethod.Delete, $"containers/{Id(container)}?force={If(force, "1", "0")}", ct).ConfigureAwait(False)
            If r.Status >= 400 Then Throw New McpException($"Docker engine error {r.Status}: {EngineMessage(Encoding.UTF8.GetString(r.Body))}")
            Return $"Removed {container}."
        End Function

        Friend Async Function PullAsync(<Description("Docker host")> host As String,
                                        <Description("Image, e.g. nginx:1.27 or ghcr.io/org/app:tag")> image As String,
                                        Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim img = If(image, "").Trim()
            If Not Regex.IsMatch(img, "^[a-z0-9][a-z0-9._\-/:@]*$") Then Throw New McpException($"'{image}' is not an image name.")
            Dim tagAt = img.LastIndexOf(":"c)
            Dim name = img, tag = "latest"
            If img.Contains("@") Then
                tag = ""
            ElseIf tagAt > img.LastIndexOf("/"c) Then
                name = img.Substring(0, tagAt) : tag = img.Substring(tagAt + 1)
            End If
            Dim r = Await SendAsync(Engine(host), HttpMethod.Post, $"images/create?fromImage={Uri.EscapeDataString(name)}{If(tag = "", "", "&tag=" & Uri.EscapeDataString(tag))}", ct, timeout:=TimeSpan.FromMinutes(15)).ConfigureAwait(False)
            Dim text = Encoding.UTF8.GetString(r.Body)
            If r.Status >= 400 Then Throw New McpException($"Pull failed ({r.Status}): {EngineMessage(text)}")
            Dim lines = text.Split(ControlChars.Lf, StringSplitOptions.RemoveEmptyEntries)
            Dim errorLine = lines.FirstOrDefault(Function(l) l.Contains("""error""", StringComparison.Ordinal))
            If errorLine IsNot Nothing Then Throw New McpException("Pull failed: " & ToolKit.Clip(errorLine, 400))
            Dim status = lines.Select(Function(l)
                                          Try
                                              Return S(JsonNode.Parse(l)?("status"))
                                          Catch ex As Text.Json.JsonException
                                              Return ""
                                          End Try
                                      End Function).Where(Function(x) x.StartsWith("Status:") OrElse x.StartsWith("Digest:")).ToList()
            Return $"Pulled {img}. " & String.Join(" ", status)
        End Function

        Friend Async Function ExecAsync(<Description("Docker host")> host As String,
                                        <Description("Container id or name")> container As String,
                                        <Description("Shell command (run with sh -c)")> command As String,
                                        <Description("Stop waiting after this many seconds (default 60, at most 600)")> Optional timeout_seconds As Integer = 60,
                                        Optional ct As CancellationToken = Nothing) As Task(Of String)
            If String.IsNullOrWhiteSpace(command) Then Throw New McpException("command is required.")
            Dim http = Engine(host)
            Dim create As New JsonObject From {{"AttachStdout", True}, {"AttachStderr", True}, {"Tty", False}, {"Cmd", New JsonArray("sh", "-c", command)}}
            Dim c = Await SendAsync(http, HttpMethod.Post, $"containers/{Id(container)}/exec", ct, create).ConfigureAwait(False)
            If c.Status >= 400 Then Throw New McpException($"Docker engine error {c.Status}: {EngineMessage(Encoding.UTF8.GetString(c.Body))}")
            Dim execId = S(JsonNode.Parse(Encoding.UTF8.GetString(c.Body))("Id"))
            Dim t = TimeSpan.FromSeconds(Math.Clamp(If(timeout_seconds <= 0, 60, timeout_seconds), 1, 600))
            Dim run = Await SendAsync(http, HttpMethod.Post, $"exec/{execId}/start", ct, New JsonObject From {{"Detach", False}, {"Tty", False}}, t).ConfigureAwait(False)
            Dim info = Await JsonAsync(http, $"exec/{execId}/json", ct).ConfigureAwait(False)
            Dim output = Demux(run.Body).TrimEnd()
            Return $"Exit code {S(info("ExitCode"))}{If(output.Length > 0, vbLf & ToolKit.Clip(output, 64000), " (no output)")}"
        End Function
    End Class

End Namespace
