Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports Microsoft.Extensions.Configuration

Namespace Configuration

    Public NotInheritable Class ConfigLoadResult
        Public Property Options As HostOptions
        Public Property ConfigPath As String
        Public Property BaseDirectory As String
    End Class

    ''' <summary>
    ''' Loads appsettings.json (comments allowed) and applies environment-variable overrides with the
    ''' AGENTIC_ prefix, e.g. AGENTIC_AIProviders__OpenWebUI__ApiKey. Variables with the pre-rename
    ''' AGENTHOST_ prefix are still honoured; AGENTIC_ wins when both are set.
    ''' </summary>
    Public NotInheritable Class ConfigLoader
        Public Const EnvPrefix As String = "AGENTIC_"
        Public Const LegacyEnvPrefix As String = "AGENTHOST_"

        Public Shared Function Load(configPath As String, Optional portOverride As Integer? = Nothing, Optional bindOverride As String = Nothing) As ConfigLoadResult
            Dim full = ResolveConfigPath(configPath)
            If Not File.Exists(full) Then
                Throw New FileNotFoundException($"Configuration file not found: {full}. Copy appsettings.example.json to appsettings.json and edit it.", full)
            End If
            Dim builder As New ConfigurationBuilder()
            builder.SetBasePath(Path.GetDirectoryName(full))
            builder.AddJsonFile(Path.GetFileName(full), optional:=False, reloadOnChange:=False)
            builder.AddEnvironmentVariables(LegacyEnvPrefix)
            builder.AddEnvironmentVariables(EnvPrefix)
            Dim root = builder.Build()

            Dim opts As New HostOptions()
            root.Bind(opts)

            ' Normalise collections that may come back null from binding.
            If opts.Models Is Nothing Then opts.Models = New List(Of ModelOptions)
            If opts.McpServers Is Nothing Then opts.McpServers = New List(Of McpServerOptions)
            If opts.Agents Is Nothing Then opts.Agents = New List(Of ProfileOptions)
            If opts.BaseProfiles Is Nothing Then opts.BaseProfiles = New List(Of ProfileOptions)

            ' Resolve provider keys from explicit env var names, if configured.
            For Each kv In opts.AIProviders
                Dim p = kv.Value
                If String.IsNullOrEmpty(p.ApiKey) AndAlso Not String.IsNullOrEmpty(p.ApiKeyEnvVar) Then
                    p.ApiKey = Environment.GetEnvironmentVariable(p.ApiKeyEnvVar)
                End If
            Next
            If String.IsNullOrEmpty(opts.Alerts.WebhookUrl) AndAlso Not String.IsNullOrEmpty(opts.Alerts.WebhookUrlEnvVar) Then
                opts.Alerts.WebhookUrl = Environment.GetEnvironmentVariable(opts.Alerts.WebhookUrlEnvVar)
            End If

            If portOverride.HasValue Then opts.Server.Port = portOverride.Value
            If Not String.IsNullOrEmpty(bindOverride) Then opts.Server.Host = bindOverride

            ApplyLocalModels(opts)
            ApplyPlatform(opts, OperatingSystem.IsWindows())

            Dim baseDir = Path.GetDirectoryName(full)
            If Not Path.IsPathRooted(opts.Host.DataDirectory) Then opts.Host.DataDirectory = Path.GetFullPath(Path.Combine(baseDir, opts.Host.DataDirectory))
            If String.IsNullOrWhiteSpace(opts.Updates.Directory) Then opts.Updates.Directory = "updates"
            If Not Path.IsPathRooted(opts.Updates.Directory) Then opts.Updates.Directory = Path.GetFullPath(Path.Combine(baseDir, opts.Updates.Directory))
            If Not String.IsNullOrEmpty(opts.Logging.Directory) AndAlso Not Path.IsPathRooted(opts.Logging.Directory) Then
                opts.Logging.Directory = Path.GetFullPath(Path.Combine(baseDir, opts.Logging.Directory))
            End If
            ExpandMcpPlaceholders(opts)

            Return New ConfigLoadResult With {.Options = opts, .ConfigPath = full, .BaseDirectory = baseDir}
        End Function

        ''' <summary>
        ''' LocalModels: the local models' slots (Slots, SlotsByModel over each model's MaxConcurrency), or with Enabled false
        ''' no local models at all. Then every IsLocal model goes, and every reference to it: ladders, spillover, fallbacks,
        ''' AllowedModels, Vision, Learning, global-task planners and schedules. An agent that only had local models gets the
        ''' first model of the Default ladder instead, so the shipped agents keep working on cloud models. Global tasks, which
        ''' plan on local models only, are switched off.
        ''' </summary>
        Public Shared Sub ApplyLocalModels(opts As HostOptions)
            If opts.LocalModels Is Nothing Then opts.LocalModels = New LocalModelsOptions()
            Dim lm = opts.LocalModels
            If lm.SlotsByModel Is Nothing Then lm.SlotsByModel = New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)
            If lm.Enabled Then
                For Each m In opts.Models.Where(Function(x) x.IsLocal)
                    Dim slots As Integer
                    If m.Key IsNot Nothing AndAlso lm.SlotsByModel.TryGetValue(m.Key, slots) AndAlso slots > 0 Then
                        m.MaxConcurrency = slots
                    ElseIf lm.Slots > 0 Then
                        m.MaxConcurrency = lm.Slots
                    End If
                Next
                Return
            End If

            Dim local As New HashSet(Of String)(opts.Models.Where(Function(x) x.IsLocal AndAlso x.Key IsNot Nothing).Select(Function(x) x.Key), StringComparer.OrdinalIgnoreCase)
            If local.Count = 0 Then Return
            opts.Models.RemoveAll(Function(x) x.IsLocal)
            Dim prune = Function(list As List(Of String)) If(list Is Nothing, Nothing, list.Where(Function(k) Not local.Contains(k)).ToList())

            For Each m In opts.Models
                m.Fallbacks = prune(m.Fallbacks)
            Next
            Dim routing = opts.Routing
            For Each name In routing.Ladders.Keys.ToList()
                Dim ladder = prune(routing.Ladders(name))
                If ladder Is Nothing OrElse ladder.Count = 0 Then routing.Ladders.Remove(name) Else routing.Ladders(name) = ladder
            Next
            Dim defaultLadder As List(Of String) = Nothing
            Dim firstCloud = If(routing.Ladders.TryGetValue("Default", defaultLadder) AndAlso defaultLadder.Count > 0, defaultLadder(0),
                                opts.Models.FirstOrDefault(Function(x) x.Enabled AndAlso x.Capabilities.Contains(ModelCapability.ToolCalling))?.Key)
            ' A list that named only local models gets the first cloud model; one that named none stays as it was.
            Dim replace = Function(list As List(Of String))
                              If list Is Nothing OrElse list.Count = 0 Then Return list
                              Dim left = prune(list)
                              If left.Count = 0 AndAlso firstCloud IsNot Nothing Then left.Add(firstCloud)
                              Return left
                          End Function

            Dim sp = routing.Spillover
            If sp.FromModel IsNot Nothing AndAlso local.Contains(sp.FromModel) Then
                sp.FromModel = ""
                sp.Enabled = False
            End If
            sp.Candidates = prune(sp.Candidates)
            sp.LocalCandidates = New List(Of String)

            For Each p In opts.BaseProfiles.Concat(opts.Agents)
                p.AllowedModels = replace(p.AllowedModels)
                If p.Schedules IsNot Nothing Then
                    For Each s In p.Schedules.Where(Function(x) x.ModelKey IsNot Nothing AndAlso local.Contains(x.ModelKey))
                        s.ModelKey = Nothing
                    Next
                End If
            Next
            opts.Vision.ModelKeys = prune(opts.Vision.ModelKeys)
            opts.Vision.ExpertModelKeys = prune(opts.Vision.ExpertModelKeys)
            ' Learning studies and eval grading stay free: without a local model they use rules only.
            opts.Learning.ModelKeys = prune(opts.Learning.ModelKeys)
            ' The global-task coordinator plans on local models only (it has no budget for cloud ones), so without them
            ' global tasks are off.
            opts.GlobalTasks.PlannerModelKeys = prune(opts.GlobalTasks.PlannerModelKeys)
            If opts.GlobalTasks.PlannerModelKeys.Count = 0 Then opts.GlobalTasks.Enabled = False
        End Sub

        ''' <summary>
        ''' Off Windows (Linux, Docker) the shipped configuration works unchanged: the shell and windows tool servers, which
        ''' need Windows, are switched off, and "\" in the {data} paths of stdio servers becomes "/".
        ''' </summary>
        Public Shared Sub ApplyPlatform(opts As HostOptions, isWindows As Boolean)
            If isWindows Then Return
            For Each s In opts.McpServers
                If s.Arguments Is Nothing Then Continue For
                If s.Arguments.Any(Function(a) a = "--mcp-shell" OrElse a = "--mcp-windows") Then s.Enabled = False
                s.Arguments = s.Arguments.Select(Function(a) If(a IsNot Nothing AndAlso a.Contains("{data}"), a.Replace("\"c, "/"c), a)).ToList()
                If s.WorkingDirectory IsNot Nothing AndAlso s.WorkingDirectory.Contains("{data}") Then s.WorkingDirectory = s.WorkingDirectory.Replace("\"c, "/"c)
            Next
        End Sub

        ''' <summary>
        ''' Stdio MCP servers may use {self} (this executable, e.g. for the built-in shared storage server) as Command, and
        ''' {data} (the resolved data directory) in Command, Arguments and WorkingDirectory.
        ''' </summary>
        Public Shared Sub ExpandMcpPlaceholders(opts As HostOptions)
            Dim self = If(Environment.ProcessPath, "")
            Dim data = opts.Host.DataDirectory
            Dim expand = Function(s As String) If(s Is Nothing, Nothing, s.Replace("{self}", self, StringComparison.OrdinalIgnoreCase).Replace("{data}", data, StringComparison.OrdinalIgnoreCase))
            For Each s In opts.McpServers
                s.Command = expand(s.Command)
                s.WorkingDirectory = expand(s.WorkingDirectory)
                s.Arguments = s.Arguments.Select(Function(a) expand(a)).ToList()
            Next
        End Sub

        Public Shared Function ResolveConfigPath(configPath As String) As String
            If Not String.IsNullOrEmpty(configPath) Then Return Path.GetFullPath(configPath)
            ' Prefer the working directory, then the executable's directory.
            Dim cwd = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json")
            If File.Exists(cwd) Then Return cwd
            Return Path.Combine(AppContext.BaseDirectory, "appsettings.json")
        End Function
    End Class

End Namespace
