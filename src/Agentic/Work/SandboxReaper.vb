Option Strict On
Option Explicit On
Option Infer On

Imports System.Threading

Imports Agentic.Configuration
Imports Agentic.Logging
Imports Agentic.Persistence
Imports Agentic.Utilities

Namespace Work

    ''' <summary>
    ''' Stops long-lived things the host left in the code sandbox so they do not run forever: background processes (via
    ''' Open Terminal's kill_process) and Docker containers/compose projects it started (via `sudo docker`). It only ever
    ''' touches resources recorded in <see cref="SandboxResourceStore"/>, and never a protected one — every container that
    ''' already existed when the host started is recorded protected, and Compose stacks are left alone unless explicitly
    ''' listed as managed. The owner chat is told when one of its resources is stopped.
    ''' </summary>
    Public NotInheritable Class SandboxReaper
        Private ReadOnly _gateway As SandboxGateway
        Private ReadOnly _store As SandboxResourceStore
        Private ReadOnly _opt As SandboxOptions
        Private ReadOnly _serverId As String
        Private ReadOnly _chat As ChatService
        Private ReadOnly _activity As SandboxActivity
        Private ReadOnly _getState As Func(Of String, Task(Of String))
        Private ReadOnly _setState As Func(Of String, String, Task)
        Private ReadOnly _log As ILog

        ' process id -> last output offset we have seen, to tell whether it is still producing output (not idle).
        Private ReadOnly _procOffset As New Dictionary(Of String, Integer)(StringComparer.Ordinal)
        Private _dockerChecked As Boolean
        Private _dockerOk As Boolean

        Private Const SnapshotKey As String = "SandboxContainerSnapshotDone"
        Private Shared ReadOnly AttributionWindow As TimeSpan = TimeSpan.FromMinutes(15)

        Public Sub New(gateway As SandboxGateway, store As SandboxResourceStore, options As SandboxOptions, serverId As String, chat As ChatService,
                       activity As SandboxActivity, getState As Func(Of String, Task(Of String)), setState As Func(Of String, String, Task), log As ILog)
            _gateway = gateway
            _store = store
            _opt = If(options, New SandboxOptions())
            _serverId = serverId
            _chat = chat
            _activity = activity
            _getState = getState
            _setState = setState
            _log = log
        End Sub

        ''' <summary>Whether the reaper has anything to do (enabled and a reachable code server with the tools it needs).</summary>
        Public Function Active() As Boolean
            Return _opt.ReaperEnabled AndAlso Not String.IsNullOrWhiteSpace(_serverId) AndAlso _gateway.Available(_serverId)
        End Function

        ' ----------------------------------------------------------------- pure decisions (tested directly)

        ''' <summary>Why a tracked resource is due to be stopped now, or Nothing if it is not. Protected resources are never due.</summary>
        Friend Shared Function DueReason(res As SandboxResourceInfo, opt As SandboxOptions, now As DateTime) As String
            If res Is Nothing OrElse res.IsProtected OrElse Not String.Equals(res.State, "running", StringComparison.OrdinalIgnoreCase) Then Return Nothing
            Dim maxLife = res.MaxLifeMinutes.GetValueOrDefault(If(res.Kind = "process", opt.ProcessMaxLifeMinutes, opt.ContainerMaxLifeMinutes))
            Dim started = If(res.CreatedUtc.HasValue, res.CreatedUtc.Value, res.FirstSeenUtc)
            If maxLife > 0 AndAlso (now - started).TotalMinutes >= maxLife Then
                Return $"reached its maximum lifetime of {FormatMinutes(maxLife)}"
            End If
            If res.Kind = "process" Then
                Dim idle = res.IdleMinutes.GetValueOrDefault(opt.ProcessIdleMinutes)
                If idle > 0 AndAlso (now - res.LastSeenUtc).TotalMinutes >= idle Then
                    Return $"was idle for {FormatMinutes(idle)}"
                End If
            End If
            Return Nothing
        End Function

        ''' <summary>Whether a container seen on the host is one the reaper may manage (not protected, not infrastructure/keep-listed).</summary>
        Friend Shared Function IsManageable(c As SandboxContainer, opt As SandboxOptions, protectedRefs As HashSet(Of String)) As Boolean
            If c Is Nothing OrElse String.IsNullOrEmpty(c.Id) Then Return False
            If protectedRefs IsNot Nothing AndAlso (protectedRefs.Contains(c.Id) OrElse protectedRefs.Contains(c.Name)) Then Return False
            Dim never = opt.NeverTouch
            If never IsNot Nothing AndAlso (never.Contains(c.Name, StringComparer.OrdinalIgnoreCase) OrElse
               (Not String.IsNullOrEmpty(c.ComposeProject) AndAlso never.Contains(c.ComposeProject, StringComparer.OrdinalIgnoreCase))) Then Return False
            ' Compose stacks are infrastructure unless the admin listed the project as managed. Ad-hoc `docker run` (no project) is managed.
            If Not String.IsNullOrEmpty(c.ComposeProject) Then
                Return opt.ManagedComposeProjects IsNot Nothing AndAlso opt.ManagedComposeProjects.Contains(c.ComposeProject, StringComparer.OrdinalIgnoreCase)
            End If
            Return True
        End Function

        Friend Shared Function FormatMinutes(minutes As Integer) As String
            If minutes < 60 Then Return minutes & " min"
            If minutes Mod 60 = 0 Then Return (minutes \ 60) & "h"
            Return (minutes \ 60) & "h " & (minutes Mod 60) & "m"
        End Function

        ' ----------------------------------------------------------------- the pass

        Public Async Function RunOnceAsync(now As DateTime, ct As CancellationToken) As Task
            If Not Active() Then Return
            ' What could not be listed this pass is not stopped this pass either: the records may be stale. Right after the
            ' sandbox was restarted the list failed once, and 497 processes that no longer existed were "stopped" (404 each).
            Dim known As New HashSet(Of String)(StringComparer.Ordinal)
            Try
                Await TrackProcessesAsync(now, ct).ConfigureAwait(False)
                known.Add("process")
            Catch ex As Global.Agentic.Mcp.McpUnavailableException
                ' The sandbox is down (the watchdog says so): said once per outage, and nothing else is tried this pass.
                If Not _downNoted Then _log.Warn("Sandbox", "Sandbox tracking paused: " & ex.Message)
                _downNoted = True
                Return
            Catch ex As Exception When Not ExceptionUtil.IsCancellation(ex)
                _log.Warn("Sandbox", "Tracking sandbox processes failed: " & ex.Message)
            End Try
            If _downNoted Then
                _downNoted = False
                _log.Info("Sandbox", "Sandbox tracking resumed.")
            End If
            If _opt.ManageContainers Then
                Try
                    Await TrackContainersAsync(now, ct).ConfigureAwait(False)
                    known.Add("container")
                    known.Add("compose")
                Catch ex As Exception When Not ExceptionUtil.IsCancellation(ex)
                    _log.Warn("Sandbox", "Tracking sandbox containers failed: " & ex.Message)
                End Try
            End If
            Await ReapDueAsync(now, ct, known).ConfigureAwait(False)
        End Function

        ''' <summary>A stop that failed because the process or container no longer exists (it ended, or the sandbox was restarted).</summary>
        Friend Shared Function AlreadyGone(message As String) As Boolean
            Dim m = If(message, "")
            Return Text.RegularExpressions.Regex.IsMatch(m, "\b404\b|not found|no such (process|container)|does not exist|unknown process", Text.RegularExpressions.RegexOptions.IgnoreCase)
        End Function

        ''' <summary>The sandbox was down at the last pass (said once, not every pass).</summary>
        Private _downNoted As Boolean
        ''' <summary>Resources the reaper could not stop: how often, and when to try again (doubling from 15 min up to 4 h).</summary>
        Private ReadOnly _stopFailures As New Dictionary(Of String, (Count As Integer, NextTryUtc As DateTime))(StringComparer.Ordinal)
        ''' <summary>
        ''' The reaper reads every tracked record, not the store's default page of 500: with about 1000 tracked after the sandbox
        ''' was restarted, only 500 were marked gone, and the others were "stopped" (497 times "Process not found").
        ''' </summary>
        Private Const AllRows As Integer = 100000

        ''' <summary>The code server's watchdog command (HealthCheck.Arguments "command", set by the host), e.g. "echo ok".</summary>
        Public Property HealthCheckCommand As String

        Friend Function IsHealthCheck(command As String) As Boolean
            Dim c = If(command, "").Trim()
            Return c.Length > 0 AndAlso (String.Equals(c, If(HealthCheckCommand, "").Trim(), StringComparison.Ordinal) OrElse c = "echo ok")
        End Function

        Private Async Function TrackProcessesAsync(now As DateTime, ct As CancellationToken) As Task
            Dim procs = Await _gateway.ListProcessesAsync(_serverId, ct).ConfigureAwait(False)
            Dim seen As New HashSet(Of String)(StringComparer.Ordinal)
            For Each p In procs
                If String.IsNullOrEmpty(p.Id) Then Continue For
                ' The watchdog's own checks ("echo ok") are not anyone's work: never tracked or stopped (with the sandbox
                ' failing, each check stayed "running", the reaper could not stop it, and 2997 warnings followed in 3 days).
                If IsHealthCheck(p.Command) Then Continue For
                seen.Add(p.Id)
                Dim running = String.Equals(p.Status, "running", StringComparison.OrdinalIgnoreCase)
                Dim existing = Await _store.GetByRefAsync(_serverId, "process", p.Id).ConfigureAwait(False)
                If Not running Then
                    ' A process that has finished on its own is no longer our concern.
                    If existing IsNot Nothing AndAlso (String.Equals(existing.State, "running", StringComparison.OrdinalIgnoreCase) OrElse
                                                       String.Equals(existing.State, "failed", StringComparison.OrdinalIgnoreCase)) Then _
                        Await _store.MarkStateAsync(existing.Id, "gone").ConfigureAwait(False)
                    _procOffset.Remove(p.Id)
                    Continue For
                End If
                ' Still producing output means still active: refresh LastSeen (idle is measured from it).
                Dim active = existing Is Nothing
                Try
                    Dim prev = 0
                    _procOffset.TryGetValue(p.Id, prev)
                    Dim since = Await _gateway.ProcessOutputSinceAsync(_serverId, p.Id, prev, ct).ConfigureAwait(False)
                    If since.Length > 0 Then active = True
                    _procOffset(p.Id) = since.NextOffset
                Catch ex As Exception When Not ExceptionUtil.IsCancellation(ex)
                End Try
                Dim res As New SandboxResourceInfo With {.ServerId = _serverId, .Kind = "process", .Ref = p.Id,
                    .Name = TextUtil.Truncate(p.Command, 200), .Command = TextUtil.Truncate(p.Command, 2000), .State = "running"}
                If existing Is Nothing Then
                    Attribute(res, now)
                    res.CreatedUtc = ProcessStartFromId(p.Id)
                End If
                Dim stored = Await _store.UpsertAsync(res, If(active OrElse existing Is Nothing, now, If(existing?.LastSeenUtc, now))).ConfigureAwait(False)
                ' A stop that failed did not end it: still running, so it is tried again (less and less often, see _stopFailures).
                If String.Equals(existing?.State, "failed", StringComparison.OrdinalIgnoreCase) Then Await _store.MarkStateAsync(existing.Id, "running").ConfigureAwait(False)
            Next
            ' Processes we tracked (or failed to stop) but the sandbox no longer lists are gone.
            For Each tracked In Await _store.ListAsync({"running", "failed"}, AllRows).ConfigureAwait(False)
                If tracked.Kind = "process" AndAlso tracked.ServerId = _serverId AndAlso Not seen.Contains(tracked.Ref) Then
                    Await _store.MarkStateAsync(tracked.Id, "gone").ConfigureAwait(False)
                    _procOffset.Remove(tracked.Ref)
                End If
            Next
        End Function

        ' ----------------------------------------------------------------- app previews

        ''' <summary>Preview links (set by the host); their links end when the reaper stops the container.</summary>
        Public Property Previews As SandboxPreviewStore
        ''' <summary>The sandbox's Docker host as seen from this host (where published ports are reached).</summary>
        Public Property PreviewHost As String

        ''' <summary>
        ''' Running containers that may be previewed: ones the host started (the same rule as stopping them: never anything that
        ''' predates the host, a Compose stack or the keep list) that publish at least one TCP port.
        ''' </summary>
        Public Async Function PreviewableAsync(ct As CancellationToken, Optional fresh As Boolean = False) As Task(Of List(Of SandboxContainer))
            If Not _opt.Previews OrElse Not _opt.ManageContainers OrElse Not Active() Then Return New List(Of SandboxContainer)
            If Not Await DockerReadyAsync(ct).ConfigureAwait(False) Then Return New List(Of SandboxContainer)
            Dim containers = Await ContainersAsync(ct, If(fresh, TimeSpan.Zero, TimeSpan.FromSeconds(2))).ConfigureAwait(False)
            Await SnapshotIfFirstAsync(containers, DateTime.UtcNow).ConfigureAwait(False)
            Dim protectedRefs = Await ProtectedRefsAsync().ConfigureAwait(False)
            Return containers.Where(Function(c) c.Ports.Count > 0 AndAlso IsManageable(c, _opt, protectedRefs)).ToList()
        End Function

        ''' <summary>"20000-20499" (or one port) as a range; Nothing when empty or not a sensible range (at most 5000 ports).</summary>
        Friend Shared Function ParsePortRange(s As String) As (Low As Integer, High As Integer)?
            Dim m = Global.System.Text.RegularExpressions.Regex.Match(If(s, "").Trim(), "^(\d{1,5})(?:\s*-\s*(\d{1,5}))?$")
            If Not m.Success Then Return Nothing
            Dim low = CInt(m.Groups(1).Value)
            Dim high = If(m.Groups(2).Success, CInt(m.Groups(2).Value), low)
            If low < 1 OrElse high > 65535 OrElse low > high OrElse high - low >= 5000 Then Return Nothing
            Return (low, high)
        End Function

        ''' <summary>The host ports forwarded for previews (Chat.Sandbox.PreviewPorts), or Nothing.</summary>
        Public ReadOnly Property PreviewRange As (Low As Integer, High As Integer)?
            Get
                Return ParsePortRange(_opt.PreviewPorts)
            End Get
        End Property

        ''' <summary>The first <paramref name="count"/> ports of the range that no container publishes.</summary>
        Friend Shared Function FreePorts(range As (Low As Integer, High As Integer)?, used As IEnumerable(Of Integer), count As Integer) As List(Of Integer)
            If Not range.HasValue Then Return New List(Of Integer)
            Dim taken = New HashSet(Of Integer)(If(used, Enumerable.Empty(Of Integer)()))
            Return Enumerable.Range(range.Value.Low, range.Value.High - range.Value.Low + 1).Where(Function(p) Not taken.Contains(p)).Take(Math.Max(0, count)).ToList()
        End Function

        ''' <summary>Preview ports free right now (no running container publishes them), for the model to use.</summary>
        Public Async Function FreePreviewPortsAsync(count As Integer, ct As CancellationToken) As Task(Of List(Of Integer))
            Dim range = PreviewRange
            If Not range.HasValue OrElse Not Active() OrElse Not _opt.ManageContainers Then Return New List(Of Integer)
            If Not Await DockerReadyAsync(ct).ConfigureAwait(False) Then Return New List(Of Integer)
            Dim containers = Await ContainersAsync(ct, TimeSpan.FromSeconds(15)).ConfigureAwait(False)
            Return FreePorts(range, containers.SelectMany(Function(c) c.Ports.Select(Function(p) p.HostPort)), count)
        End Function

        ''' <summary>The port to open when none is named: one that looks like a web app's, else the first.</summary>
        Friend Shared Function PickPort(ports As List(Of SandboxPort), wanted As Integer?) As SandboxPort
            If ports Is Nothing OrElse ports.Count = 0 Then Return Nothing
            If wanted.HasValue Then Return ports.FirstOrDefault(Function(p) p.HostPort = wanted.Value OrElse p.ContainerPort = wanted.Value)
            If ports.Count = 1 Then Return ports(0)
            Dim web = {80, 8080, 3000, 5000, 8000, 8501, 7860, 4200, 5173, 8888, 443}
            Return If(ports.FirstOrDefault(Function(p) web.Contains(p.ContainerPort)), ports(0))
        End Function

        ''' <summary>
        ''' A preview link to a container's published port for this user (and chat). Errors say what to do instead, for the
        ''' model: no such container, one that is infrastructure, or one that publishes no port.
        ''' </summary>
        Public Async Function CreatePreviewAsync(owner As String, chatId As String, container As String, port As Integer?, ct As CancellationToken) As Task(Of (Preview As SandboxPreview, ErrorText As String, Container As SandboxContainer))
            If Previews Is Nothing OrElse Not _opt.Previews Then Return (Nothing, "App previews are turned off (Chat.Sandbox.Previews).", Nothing)
            If Not _opt.ManageContainers Then Return (Nothing, "Container management is off (Chat.Sandbox.ManageContainers), so apps cannot be previewed.", Nothing)
            Dim name = If(container, "").Trim()
            If name.Length = 0 Then Return (Nothing, "Name the container to preview (its name or id).", Nothing)
            If Not Active() Then Return (Nothing, "The code sandbox is not connected, so there is no app to open.", Nothing)
            Try
                Return Await CreatePreviewCoreAsync(owner, chatId, name, port, ct).ConfigureAwait(False)
            Catch ex As Exception When Not ExceptionUtil.IsCancellation(ex)
                Return (Nothing, "The sandbox could not be asked about its containers: " & ex.Message, Nothing)
            End Try
        End Function

        Private Async Function CreatePreviewCoreAsync(owner As String, chatId As String, name As String, port As Integer?, ct As CancellationToken) As Task(Of (Preview As SandboxPreview, ErrorText As String, Container As SandboxContainer))
            Dim apps = Await PreviewableAsync(ct, fresh:=True).ConfigureAwait(False)
            Dim match = Function(c As SandboxContainer) String.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) OrElse
                                                     (name.Length >= 6 AndAlso c.Id.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            Dim found = apps.FirstOrDefault(match)
            If found Is Nothing Then
                Dim all = Await ContainersAsync(ct, TimeSpan.FromSeconds(30)).ConfigureAwait(False)
                Dim any = all.FirstOrDefault(match)
                Dim running = String.Join(", ", apps.Select(Function(a) a.Name))
                If any Is Nothing Then Return (Nothing, $"No running container is called '{name}'." & If(running.Length > 0, $" Containers you can preview: {running}.", ""), Nothing)
                If any.Ports.Count = 0 Then
                    Return (Nothing, $"The container '{any.Name}' publishes no port, so it cannot be opened from outside. Start it again with a published port, " &
                                     "e.g. sudo docker run -d --name " & any.Name & " -p 8080:<the port the app listens on> <image>, then preview it.", any)
                End If
                Return (Nothing, $"The container '{any.Name}' is not one started here (it is infrastructure or kept), so it cannot be previewed.", any)
            End If
            Dim chosen = PickPort(found.Ports, port)
            If chosen Is Nothing Then
                Return (Nothing, $"The container '{found.Name}' publishes " & String.Join(", ", found.Ports.Select(Function(p) $"{p.HostPort}→{p.ContainerPort}")) &
                                 $"; port {port} is not one of them.", found)
            End If
            Dim now = DateTime.UtcNow
            ' Straight to the port when this host reaches it; else through the sandbox connection.
            Dim mode = If(_opt.PreviewTransport, "auto").Trim().ToLowerInvariant()
            Dim transport = If(mode = "direct" OrElse mode = "mcp", mode, If(Await CanReachAsync(PreviewHost, chosen.HostPort).ConfigureAwait(False), "direct", "mcp"))
            Dim preview = Await Previews.CreateAsync(owner, chatId, found.Id, found.Name, PreviewHost, chosen.HostPort, chosen.ContainerPort, _opt.PreviewHours, now, transport).ConfigureAwait(False)
            If preview.Transport <> transport Then
                Await Previews.SetTransportAsync(preview.Id, transport).ConfigureAwait(False)
                preview.Transport = transport
            End If
            ' The reaper knows whose app this is (to tell that chat when it stops it).
            Await _store.UpsertAsync(New SandboxResourceInfo With {.ServerId = _serverId, .Kind = "container", .Ref = found.Id, .Name = found.Name,
                .CreatedUtc = found.CreatedUtc, .OwnerUser = owner, .OwnerChatId = chatId, .State = "running"}, now).ConfigureAwait(False)
            Return (preview, Nothing, found)
        End Function

        ''' <summary>Whether this host can open a TCP connection to the port (in 1.5 s).</summary>
        Friend Shared Async Function CanReachAsync(host As String, port As Integer) As Task(Of Boolean)
            If String.IsNullOrWhiteSpace(host) OrElse port <= 0 Then Return False
            Using client As New Net.Sockets.TcpClient()
                Using cts As New CancellationTokenSource(TimeSpan.FromMilliseconds(1500))
                    Try
                        Await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(False)
                        Return client.Connected
                    Catch ex As Exception When TypeOf ex Is Net.Sockets.SocketException OrElse TypeOf ex Is OperationCanceledException
                        Return False
                    End Try
                End Using
            End Using
        End Function

        ''' <summary>For each preview request: the container still runs and still publishes the port (Nothing = fine, else why not).</summary>
        Public Async Function ValidatePreviewAsync(p As SandboxPreview, ct As CancellationToken) As Task(Of String)
            Try
                Dim list = Await ContainersAsync(ct, TimeSpan.FromSeconds(15)).ConfigureAwait(False)
                Dim c = list.FirstOrDefault(Function(x) x.Id = p.ContainerId)
                If c Is Nothing Then Return "This app is not running any more (its container stopped). Start it again and open a new preview."
                If Not c.Ports.Any(Function(x) x.HostPort = p.TargetPort) Then Return $"The app's container no longer publishes port {p.TargetPort}."
                Return Nothing
            Catch ex As Exception When Not ExceptionUtil.IsCancellation(ex)
                Return "The sandbox cannot be reached right now: " & ex.Message
            End Try
        End Function

        ''' <summary>How long one look at the sandbox (docker check, docker ps) may take before it counts as failed.</summary>
        Public Property SandboxCallTimeout As TimeSpan = TimeSpan.FromSeconds(60)
        ''' <summary>After a failed look, how long callers get the failure (or the last list) before the sandbox is asked again.</summary>
        Public Property SandboxRetryPause As TimeSpan = TimeSpan.FromSeconds(30)

        ' One docker check at a time for everyone; a failure (the sandbox hung or answered with an error) is retried after a pause.
        Private ReadOnly _dockerGate As New Object()
        Private _dockerTask As Task(Of Boolean)
        Private _dockerFailedAt As DateTime = DateTime.MinValue

        Private Function DockerReadyAsync(ct As CancellationToken) As Task(Of Boolean)
            If _dockerChecked Then Return Task.FromResult(_dockerOk)
            SyncLock _dockerGate
                If _dockerTask Is Nothing OrElse (_dockerTask.IsFaulted AndAlso DateTime.UtcNow - _dockerFailedAt >= SandboxRetryPause) OrElse _dockerTask.IsCanceled Then
                    _dockerTask = Task.Run(Function() CheckDockerAsync())
                End If
                Return _dockerTask.WaitAsync(ct)
            End SyncLock
        End Function

        Private Async Function CheckDockerAsync() As Task(Of Boolean)
            Try
                Using cts As New CancellationTokenSource(SandboxCallTimeout)
                    _dockerOk = Await _gateway.DockerAvailableAsync(_serverId, cts.Token).ConfigureAwait(False)
                End Using
            Catch ex As Exception
                _dockerFailedAt = DateTime.UtcNow
                Throw SandboxFailure(ex)
            End Try
            _dockerChecked = True
            If Not _dockerOk Then _log.Info("Sandbox", "Container management is on but `sudo docker` is not available in the sandbox; managing processes only.")
            Return _dockerOk
        End Function

        ''' <summary>A failed sandbox call as a TimeoutException with a plain message (a hung sandbox shows up as a cancellation).</summary>
        Private Function SandboxFailure(ex As Exception) As Exception
            If TypeOf ex Is OperationCanceledException Then Return New TimeoutException($"The sandbox did not answer within {SandboxCallTimeout.TotalSeconds:0} s.")
            Return ex
        End Function

        ' The containers running on the sandbox host, shared by the reaper, the preview list and every preview request: one docker ps
        ' at a time for everyone, never longer than SandboxCallTimeout, and callers may stop waiting without stopping it.
        Private ReadOnly _listLock As New Object()
        Private _listTask As Task(Of List(Of SandboxContainer))
        Private _listFailedAt As DateTime = DateTime.MinValue
        Private _listed As List(Of SandboxContainer)
        Private _listedAt As DateTime

        ''' <summary>The running containers, at most <paramref name="maxAge"/> old (one docker ps for everyone asking at once).</summary>
        Public Function ContainersAsync(ct As CancellationToken, Optional maxAge As TimeSpan = Nothing) As Task(Of List(Of SandboxContainer))
            Dim cached = _listed
            If cached IsNot Nothing AndAlso DateTime.UtcNow - _listedAt <= maxAge Then Return Task.FromResult(cached)
            SyncLock _listLock
                If _listTask Is Nothing OrElse _listTask.IsCompletedSuccessfully OrElse _listTask.IsCanceled OrElse
                   (_listTask.IsFaulted AndAlso DateTime.UtcNow - _listFailedAt >= SandboxRetryPause) Then
                    _listTask = Task.Run(Function() LoadContainersAsync())
                End If
                Return _listTask.WaitAsync(ct)
            End SyncLock
        End Function

        ''' <summary>Tests: the container list counts as old.</summary>
        Friend Sub ExpireContainerListForTests()
            _listedAt = DateTime.MinValue
        End Sub

        Private Async Function LoadContainersAsync() As Task(Of List(Of SandboxContainer))
            Try
                Using cts As New CancellationTokenSource(SandboxCallTimeout)
                    Dim list = Await _gateway.ListContainersAsync(_serverId, cts.Token).ConfigureAwait(False)
                    _listed = list
                    _listedAt = DateTime.UtcNow
                    Return list
                End Using
            Catch ex As Exception
                _listFailedAt = DateTime.UtcNow
                Throw SandboxFailure(ex)
            End Try
        End Function

        ''' <summary>
        ''' The previewable apps for a page, within <paramref name="budget"/>: when the sandbox is slow or failing, the apps from the
        ''' last good look (or none) come back at once with a warning, and the look goes on in the background for the next page.
        ''' </summary>
        Public Async Function PreviewableForPageAsync(ct As CancellationToken, budget As TimeSpan) As Task(Of (Apps As List(Of SandboxContainer), Warning As String))
            If Not _opt.Previews OrElse Not _opt.ManageContainers OrElse Not Active() Then Return (New List(Of SandboxContainer), Nothing)
            Dim work = PreviewableAsync(CancellationToken.None)
            Dim first = Await Task.WhenAny(work, Task.Delay(budget, ct)).ConfigureAwait(False)
            ct.ThrowIfCancellationRequested()
            Dim why As String
            If first Is work Then
                If work.IsCompletedSuccessfully Then Return (work.Result, Nothing)
                why = "The sandbox could not list its apps: " & If(work.Exception?.GetBaseException().Message, "it stopped answering") & "."
            Else
                why = "The sandbox is slow to answer."
            End If
            Dim known = _listed
            If known Is Nothing Then Return (New List(Of SandboxContainer), why & " No app list is known yet.")
            Dim protectedRefs = Await ProtectedRefsAsync().ConfigureAwait(False)
            Dim age = DateTime.UtcNow - _listedAt
            Return (known.Where(Function(c) c.Ports.Count > 0 AndAlso IsManageable(c, _opt, protectedRefs)).ToList(),
                    why & $" Showing the apps from {If(age.TotalMinutes < 1, "under a minute", CInt(age.TotalMinutes).ToString(Globalization.CultureInfo.InvariantCulture) & " min")} ago.")
        End Function

        ''' <summary>The very first look: everything already running predates the host, so it is recorded protected (never stopped or previewed).</summary>
        Private Async Function SnapshotIfFirstAsync(containers As List(Of SandboxContainer), now As DateTime) As Task(Of Boolean)
            If Not String.IsNullOrEmpty(Await _getState(SnapshotKey).ConfigureAwait(False)) Then Return False
            For Each c In containers
                Await _store.UpsertAsync(New SandboxResourceInfo With {.ServerId = _serverId, .Kind = "container", .Ref = c.Id,
                    .Name = c.Name, .WorkingDir = c.ComposeProject, .CreatedUtc = c.CreatedUtc, .IsProtected = True, .State = "running"}, now).ConfigureAwait(False)
            Next
            Await _setState(SnapshotKey, Db.Ts(now)).ConfigureAwait(False)
            _log.Info("Sandbox", $"Recorded {containers.Count} pre-existing container(s) as protected; they will never be stopped.")
            Return True
        End Function

        ''' <summary>Containers recorded protected (ids and names).</summary>
        Private Async Function ProtectedRefsAsync() As Task(Of HashSet(Of String))
            Dim refs As New HashSet(Of String)(StringComparer.Ordinal)
            For Each pr In Await _store.ListAsync({"running"}, AllRows).ConfigureAwait(False)
                If pr.Kind = "container" AndAlso pr.IsProtected Then
                    refs.Add(pr.Ref)
                    If Not String.IsNullOrEmpty(pr.Name) Then refs.Add(pr.Name)
                End If
            Next
            Return refs
        End Function

        Private Async Function TrackContainersAsync(now As DateTime, ct As CancellationToken) As Task
            If Not Await DockerReadyAsync(ct).ConfigureAwait(False) Then Return
            Dim containers = Await ContainersAsync(ct).ConfigureAwait(False)
            If Await SnapshotIfFirstAsync(containers, now).ConfigureAwait(False) Then Return
            ' Later passes: protect anything already marked protected, plus the never-touch list.
            Dim protectedRefs = Await ProtectedRefsAsync().ConfigureAwait(False)

            Dim present As New HashSet(Of String)(StringComparer.Ordinal)
            For Each c In containers
                present.Add(c.Id)
                If IsManageable(c, _opt, protectedRefs) Then
                    Dim res As New SandboxResourceInfo With {.ServerId = _serverId, .Kind = "container", .Ref = c.Id,
                        .Name = If(String.IsNullOrEmpty(c.Name), c.Id.Substring(0, Math.Min(12, c.Id.Length)), c.Name),
                        .WorkingDir = c.ComposeProject, .CreatedUtc = c.CreatedUtc, .State = "running"}
                    Dim existing = Await _store.GetByRefAsync(_serverId, "container", c.Id).ConfigureAwait(False)
                    If existing Is Nothing Then Attribute(res, now)
                    Await _store.UpsertAsync(res, now).ConfigureAwait(False)
                Else
                    ' Keep a protected record so it shows in the UI as "left alone", but never reap it.
                    Dim existing = Await _store.GetByRefAsync(_serverId, "container", c.Id).ConfigureAwait(False)
                    If existing Is Nothing Then
                        Await _store.UpsertAsync(New SandboxResourceInfo With {.ServerId = _serverId, .Kind = "container", .Ref = c.Id,
                            .Name = c.Name, .WorkingDir = c.ComposeProject, .CreatedUtc = c.CreatedUtc, .IsProtected = True, .State = "running"}, now).ConfigureAwait(False)
                    End If
                End If
            Next
            ' Containers we tracked but that are gone from the host.
            For Each tracked In Await _store.ListAsync({"running"}, AllRows).ConfigureAwait(False)
                If tracked.Kind = "container" AndAlso tracked.ServerId = _serverId AndAlso Not present.Contains(tracked.Ref) Then
                    Await _store.MarkStateAsync(tracked.Id, "gone").ConfigureAwait(False)
                End If
            Next
        End Function

        Private Async Function ReapDueAsync(now As DateTime, ct As CancellationToken, known As HashSet(Of String)) As Task
            For Each res In Await _store.ListAsync({"running"}, AllRows).ConfigureAwait(False)
                If res.ServerId <> _serverId OrElse Not known.Contains(res.Kind) Then Continue For
                Dim reason = DueReason(res, _opt, now)
                If reason Is Nothing Then Continue For
                Dim failed As (Count As Integer, NextTryUtc As DateTime)
                SyncLock _stopFailures
                    If _stopFailures.TryGetValue(res.Kind & ":" & res.Ref, failed) AndAlso failed.NextTryUtc > now Then Continue For
                End SyncLock
                Await StopResourceAsync(res, reason, "max lifetime/idle", now, ct).ConfigureAwait(False)
            Next
        End Function

        ''' <summary>Stops one tracked resource and records it. <paramref name="trigger"/> is "manual" or the reaper's reason category.</summary>
        Public Async Function StopResourceAsync(res As SandboxResourceInfo, reason As String, trigger As String, now As DateTime, ct As CancellationToken) As Task(Of Boolean)
            If res Is Nothing Then Return False
            If res.IsProtected AndAlso trigger <> "manual" Then Return False
            Dim ok = False, how = ""
            Select Case res.Kind
                Case "process"
                    Dim r = Await _gateway.KillProcessAsync(_serverId, res.Ref, True, ct).ConfigureAwait(False)
                    ok = r.Ok : how = r.Message
                Case "container"
                    Dim r = Await _gateway.StopContainerAsync(_serverId, res.Ref, _opt.RemoveStoppedContainers, ct).ConfigureAwait(False)
                    ok = r.Ok : how = r.Message
                Case "compose"
                    Dim r = Await _gateway.ComposeDownAsync(_serverId, res.WorkingDir, ct).ConfigureAwait(False)
                    ok = r.Ok : how = r.Message
            End Select
            If Not ok AndAlso res.Kind <> "compose" AndAlso AlreadyGone(how) Then
                ' Nothing left to stop: it ended by itself or went with a sandbox restart.
                Await _store.MarkStateAsync(res.Id, "gone").ConfigureAwait(False)
                SyncLock _stopFailures
                    _stopFailures.Remove(res.Kind & ":" & res.Ref)
                End SyncLock
                _procOffset.Remove(res.Ref)
                _log.Dbg("Sandbox", $"Sandbox {res.Kind} {If(res.Name, res.Ref)} had already ended.")
                Return True
            End If
            Await _store.MarkStoppedAsync(res.Id, If(ok, "stopped", "failed"), reason, now).ConfigureAwait(False)
            ' Its preview links end with it (a later container on the same port is not the same app).
            If ok AndAlso res.Kind = "container" AndAlso Previews IsNot Nothing Then Await Previews.RevokeForContainerAsync(res.Ref, now).ConfigureAwait(False)
            Dim key = res.Kind & ":" & res.Ref
            If ok Then
                SyncLock _stopFailures
                    _stopFailures.Remove(key)
                End SyncLock
                _log.Info("Sandbox", $"Stopped sandbox {res.Kind} {If(res.Name, res.Ref)} ({how}): {reason}.")
                Await NotifyOwnerAsync(res, reason).ConfigureAwait(False)
            Else
                ' Tried again later, less and less often; said the first time and then at 2, 4, 8… tries, not on every pass.
                Dim count As Integer
                SyncLock _stopFailures
                    Dim before As (Count As Integer, NextTryUtc As DateTime)
                    _stopFailures.TryGetValue(key, before)
                    count = before.Count + 1
                    _stopFailures(key) = (count, now.AddMinutes(Math.Min(240, 15 * (1 << Math.Min(count - 1, 5)))))
                End SyncLock
                If count = 1 Then
                    _log.Warn("Sandbox", $"Could not stop sandbox {res.Kind} {If(res.Name, res.Ref)}{If(String.IsNullOrEmpty(how), "", ": " & how)}.")
                ElseIf (count And (count - 1)) = 0 Then
                    _log.Info("Sandbox", $"Still could not stop sandbox {res.Kind} {If(res.Name, res.Ref)} ({count} tries){If(String.IsNullOrEmpty(how), "", ": " & how)}.")
                End If
            End If
            Return ok
        End Function

        Private Async Function NotifyOwnerAsync(res As SandboxResourceInfo, reason As String) As Task
            If Not _opt.NotifyOwner OrElse _chat Is Nothing Then Return
            If String.IsNullOrEmpty(res.OwnerUser) OrElse String.IsNullOrEmpty(res.OwnerChatId) OrElse res.Notified Then Return
            Dim what = Select_(res.Kind, "process", "A background process", "container", "A Docker container", "compose", "A Docker compose project", "A sandbox resource")
            Dim text = $"🧹 {what} from this chat was stopped automatically because it {reason}: **{TextUtil.Truncate(If(res.Name, res.Ref), 120)}**." & vbLf &
                       "Nothing you created was deleted; the running process/container was just shut down to free the sandbox. Start it again if you still need it."
            Try
                Await _chat.PostAgentReportAsync(res.OwnerUser, res.OwnerChatId, "sandbox", text).ConfigureAwait(False)
                Await _store.SetNotifiedAsync(res.Id).ConfigureAwait(False)
            Catch ex As Exception When Not ExceptionUtil.IsCancellation(ex)
                _log.Warn("Sandbox", "Could not notify the owner chat: " & ex.Message)
            End Try
        End Function

        Private Sub Attribute(res As SandboxResourceInfo, now As DateTime)
            If _activity Is Nothing Then Return
            Dim owner = _activity.Attribute(now, AttributionWindow)
            If owner.HasValue Then
                res.OwnerUser = owner.Value.User
                res.OwnerChatId = owner.Value.ChatId
                res.OwnerAgentId = owner.Value.AgentId
            End If
        End Sub

        ''' <summary>Open Terminal process ids start with a timestamp "yyyyMMdd-HHmmss-xxxxxx"; use it as the start time when it parses.</summary>
        Friend Shared Function ProcessStartFromId(id As String) As DateTime?
            If String.IsNullOrEmpty(id) OrElse id.Length < 15 Then Return Nothing
            Dim dt As DateTime
            If DateTime.TryParseExact(id.Substring(0, 15), "yyyyMMdd-HHmmss", Globalization.CultureInfo.InvariantCulture,
                                      Globalization.DateTimeStyles.AssumeUniversal Or Globalization.DateTimeStyles.AdjustToUniversal, dt) Then Return dt
            Return Nothing
        End Function

        Private Shared Function Select_(key As String, ParamArray pairs As String()) As String
            Dim i = 0
            While i + 1 < pairs.Length
                If String.Equals(key, pairs(i), StringComparison.Ordinal) Then Return pairs(i + 1)
                i += 2
            End While
            Return If(pairs.Length Mod 2 = 1, pairs(pairs.Length - 1), key)
        End Function
    End Class

End Namespace
