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
