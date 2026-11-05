Option Strict On
Option Explicit On
Option Infer On

Imports System.ComponentModel
Imports System.Globalization
Imports System.IO
Imports System.Net.Http
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports System.Threading
Imports System.Xml.Linq
Imports ModelContextProtocol
Imports ModelContextProtocol.Server

Namespace Mcp

    ''' <summary>
    ''' Built-in "deps" MCP server ("Command": "{self}", "Arguments": ["--mcp-deps", "--roots", "{data}\workspace;{data}\shared"]):
    ''' package facts from the registries (npm, PyPI, NuGet, crates.io, Go, RubyGems, Maven Central) - latest version, release dates,
    ''' license, links - and known vulnerabilities from OSV.dev, for one package or for every dependency in a manifest or lock file
    ''' (package.json/package-lock.json, requirements.txt, .csproj/.vbproj/Directory.Packages.props, Cargo.toml/Cargo.lock, go.mod,
    ''' Gemfile.lock, pom.xml).
    ''' </summary>
    Public NotInheritable Class DepsServer
        Private ReadOnly _http As HttpClient
        Private ReadOnly _roots As PathRoots
        Private Shared ReadOnly Inv As CultureInfo = CultureInfo.InvariantCulture

        Friend Sub New(roots As PathRoots, Optional handler As HttpMessageHandler = Nothing)
            _roots = roots
            _http = New HttpClient(If(handler, New SocketsHttpHandler With {.ConnectTimeout = TimeSpan.FromSeconds(10), .AutomaticDecompression = Net.DecompressionMethods.All})) With {.Timeout = TimeSpan.FromSeconds(30)}
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Agentic-deps/1.0 (+https://osv.dev)")
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/json")
        End Sub

        Public Shared Function RunStdio(args As String()) As Integer
            Dim roots = ToolKit.Arg(args, "--roots")
            Dim server As New DepsServer(If(roots Is Nothing, Nothing, New PathRoots(ToolKit.SplitList(roots))))
            Return BuiltInServer.Run("agentic-deps", server.CreateTools())
        End Function

        Public Function CreateTools() As McpServerPrimitiveCollection(Of McpServerTool)
            Dim tools As New McpServerPrimitiveCollection(Of McpServerTool)()
            Const eco As String = " ecosystem: npm, pypi, nuget, crates, go, rubygems or maven (name as group:artifact)."
            ToolKit.Add(tools, New Func(Of String, String, CancellationToken, Task(Of String))(AddressOf PackageInfoAsync), "package_info",
                "A package's latest version (and when it was released), description, license, homepage and repository, from its registry." & eco)
            ToolKit.Add(tools, New Func(Of String, String, Integer, CancellationToken, Task(Of String))(AddressOf VersionsAsync), "package_versions",
                "A package's most recent versions with release dates (pre-releases marked)." & eco)
            ToolKit.Add(tools, New Func(Of String, String, String, CancellationToken, Task(Of String))(AddressOf VulnsAsync), "check_vulnerabilities",
                "Known vulnerabilities (OSV.dev: GitHub advisories, CVEs, PyPA, RustSec, Go) affecting a package version, with severity and the versions that fix them." & eco)
            If _roots IsNot Nothing Then
                ToolKit.Add(tools, New Func(Of String, Boolean, CancellationToken, Task(Of String))(AddressOf AuditAsync), "audit_dependencies",
                    $"Check every dependency in a manifest or lock file (in {_roots.Describe()}) against OSV.dev; check_latest: also list outdated packages. " &
                    "Files: package.json, package-lock.json, requirements.txt, *.csproj/*.vbproj/*.fsproj, Directory.Packages.props, packages.lock.json, Cargo.toml, Cargo.lock, go.mod, Gemfile.lock, pom.xml.")
            End If
            Return tools
        End Function

        ' ------------------------------------------------------------------------------------------ registries

        Friend Shared Function Ecosystem(e As String) As (Key As String, Osv As String)
            Select Case If(e, "").Trim().ToLowerInvariant()
                Case "npm", "node", "javascript", "js" : Return ("npm", "npm")
                Case "pypi", "pip", "python", "py" : Return ("pypi", "PyPI")
                Case "nuget", "dotnet", ".net", "csharp" : Return ("nuget", "NuGet")
                Case "crates", "crates.io", "cargo", "rust" : Return ("crates", "crates.io")
                Case "go", "golang" : Return ("go", "Go")
                Case "rubygems", "gem", "ruby" : Return ("rubygems", "RubyGems")
                Case "maven", "java" : Return ("maven", "Maven")
            End Select
            Throw New McpException($"Unknown ecosystem '{e}'. Use npm, pypi, nuget, crates, go, rubygems or maven.")
        End Function

        Private Async Function GetJsonAsync(url As String, ct As CancellationToken) As Task(Of JsonNode)
            Using resp = Await _http.GetAsync(url, ct).ConfigureAwait(False)
                If resp.StatusCode = Net.HttpStatusCode.NotFound Then Return Nothing
                Dim body = Await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(False)
                If Not resp.IsSuccessStatusCode Then Throw New McpException($"The registry answered {CInt(resp.StatusCode)} for {url}.")
                Return JsonNode.Parse(body)
            End Using
        End Function

        ''' <summary>Go module paths are case-encoded in proxy URLs (Upper -> !upper).</summary>
        Private Shared Function GoEscape(path As String) As String
            Return Regex.Replace(path, "[A-Z]", Function(m) "!" & m.Value.ToLowerInvariant())
        End Function

        Friend NotInheritable Class PackageFacts
            Public Property Name As String
            Public Property Latest As String
            Public Property LatestDate As String
            Public Property Description As String
            Public Property License As String
            Public Property Homepage As String
            Public Property Repository As String
            Public Property Versions As New List(Of (Version As String, [Date] As String))
        End Class

        Private Shared Function S(n As JsonNode) As String
            If n Is Nothing Then Return Nothing
            Try
                Return n.GetValue(Of String)()
            Catch ex As InvalidOperationException
                Return n.ToJsonString()
            End Try
        End Function

        Friend Async Function FactsAsync(ecosystem As String, name As String, ct As CancellationToken) As Task(Of PackageFacts)
            Dim e = DepsServer.Ecosystem(ecosystem)
            Dim n = If(name, "").Trim()
            If n.Length = 0 Then Throw New McpException("name is required.")
            Dim f As New PackageFacts With {.Name = n}
            Select Case e.Key
                Case "npm"
                    Dim j = Await GetJsonAsync("https://registry.npmjs.org/" & n.Replace("/", "%2F"), ct).ConfigureAwait(False)
                    If j Is Nothing Then Return Nothing
                    f.Latest = S(j("dist-tags")?("latest"))
                    f.Description = S(j("description"))
                    f.License = S(j("license"))
                    f.Homepage = S(j("homepage"))
                    f.Repository = S(j("repository")?("url"))
                    Dim times = j("time")?.AsObject()
                    If times IsNot Nothing Then
                        f.LatestDate = S(times(If(f.Latest, "")))
                        f.Versions = times.Where(Function(kv) kv.Key <> "created" AndAlso kv.Key <> "modified").Select(Function(kv) (kv.Key, S(kv.Value))).OrderByDescending(Function(v) v.Item2).ToList()
                    End If
                Case "pypi"
                    Dim j = Await GetJsonAsync($"https://pypi.org/pypi/{Uri.EscapeDataString(n)}/json", ct).ConfigureAwait(False)
                    If j Is Nothing Then Return Nothing
                    Dim info = j("info")
                    f.Latest = S(info?("version"))
                    f.Description = S(info?("summary"))
                    f.License = If(S(info?("license_expression")), S(info?("license")))
                    f.Homepage = If(S(info?("home_page")), S(info?("project_url")))
                    f.Repository = S(info?("project_urls")?("Source"))
                    Dim rels = j("releases")?.AsObject()
                    If rels IsNot Nothing Then
                        f.Versions = rels.Select(Function(kv) (kv.Key, S(kv.Value?.AsArray().FirstOrDefault()?("upload_time_iso_8601")))).Where(Function(v) v.Item2 IsNot Nothing).OrderByDescending(Function(v) v.Item2).ToList()
                        f.LatestDate = f.Versions.FirstOrDefault(Function(v) v.Version = f.Latest).Date
                    End If
                Case "nuget"
                    Dim id = n.ToLowerInvariant()
                    Dim j = Await GetJsonAsync($"https://api.nuget.org/v3/registration5-gz-semver2/{id}/index.json", ct).ConfigureAwait(False)
                    If j Is Nothing Then Return Nothing
                    Dim items As New List(Of JsonNode)
                    For Each page In j("items").AsArray()
                        Dim pageItems = page("items")?.AsArray()
                        If pageItems Is Nothing Then
                            Dim p = Await GetJsonAsync(S(page("@id")), ct).ConfigureAwait(False)
                            pageItems = p?("items")?.AsArray()
                        End If
                        If pageItems IsNot Nothing Then items.AddRange(pageItems)
                    Next
                    Dim entries = items.Select(Function(it) it("catalogEntry")).Where(Function(c) c IsNot Nothing AndAlso Not String.Equals(S(c("listed")), "false", StringComparison.OrdinalIgnoreCase)).ToList()
                    f.Versions = entries.Select(Function(c) (S(c("version")), S(c("published")))).OrderByDescending(Function(v) v.Item2).ToList()
                    Dim stable = entries.LastOrDefault(Function(c) Not S(c("version")).Contains("-"c))
                    Dim last = If(stable, entries.LastOrDefault())
                    If last IsNot Nothing Then
                        f.Latest = S(last("version"))
                        f.LatestDate = S(last("published"))
                        f.Description = S(last("description"))
                        f.License = If(S(last("licenseExpression")), S(last("licenseUrl")))
                        f.Homepage = S(last("projectUrl"))
                    End If
                Case "crates"
                    Dim j = Await GetJsonAsync($"https://crates.io/api/v1/crates/{Uri.EscapeDataString(n)}", ct).ConfigureAwait(False)
                    If j Is Nothing Then Return Nothing
                    Dim c = j("crate")
                    f.Latest = If(S(c?("max_stable_version")), S(c?("newest_version")))
                    f.Description = S(c?("description"))
                    f.Homepage = S(c?("homepage"))
                    f.Repository = S(c?("repository"))
                    Dim vs = j("versions")?.AsArray()
                    If vs IsNot Nothing Then
                        f.Versions = vs.Where(Function(v) Not String.Equals(S(v("yanked")), "true", StringComparison.OrdinalIgnoreCase)).Select(Function(v) (S(v("num")), S(v("created_at")))).ToList()
                        Dim latestNode = vs.FirstOrDefault(Function(v) S(v("num")) = f.Latest)
                        f.License = S(latestNode?("license"))
                        f.LatestDate = S(latestNode?("created_at"))
                    End If
                Case "go"
                    Dim latest = Await GetJsonAsync($"https://proxy.golang.org/{GoEscape(n)}/@latest", ct).ConfigureAwait(False)
                    If latest Is Nothing Then Return Nothing
                    f.Latest = S(latest("Version"))
                    f.LatestDate = S(latest("Time"))
                    f.Homepage = "https://pkg.go.dev/" & n
                    Using resp = Await _http.GetAsync($"https://proxy.golang.org/{GoEscape(n)}/@v/list", ct).ConfigureAwait(False)
                        If resp.IsSuccessStatusCode Then
                            Dim list = (Await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(False)).Split(ControlChars.Lf, StringSplitOptions.RemoveEmptyEntries)
                            f.Versions = list.Reverse().Select(Function(v) (v.Trim(), CStr(Nothing))).ToList()
                        End If
                    End Using
                Case "rubygems"
                    Dim j = Await GetJsonAsync($"https://rubygems.org/api/v1/gems/{Uri.EscapeDataString(n)}.json", ct).ConfigureAwait(False)
                    If j Is Nothing Then Return Nothing
                    f.Latest = S(j("version"))
                    f.LatestDate = S(j("version_created_at"))
                    f.Description = S(j("info"))
                    f.License = String.Join(", ", If(j("licenses")?.AsArray().Select(Function(x) S(x)), Enumerable.Empty(Of String)()))
                    f.Homepage = S(j("homepage_uri"))
                    f.Repository = S(j("source_code_uri"))
                    Dim vs = Await GetJsonAsync($"https://rubygems.org/api/v1/versions/{Uri.EscapeDataString(n)}.json", ct).ConfigureAwait(False)
                    If vs IsNot Nothing Then f.Versions = vs.AsArray().Select(Function(v) (S(v("number")), S(v("created_at")))).ToList()
                Case "maven"
                    Dim parts = n.Split(":"c)
                    If parts.Length <> 2 Then Throw New McpException("Maven packages are named group:artifact, e.g. org.slf4j:slf4j-api.")
                    Dim q = Uri.EscapeDataString($"g:""{parts(0)}"" AND a:""{parts(1)}""")
                    Dim j = Await GetJsonAsync($"https://search.maven.org/solrsearch/select?q={q}&core=gav&rows=30&wt=json", ct).ConfigureAwait(False)
                    Dim docs = j?("response")?("docs")?.AsArray()
                    If docs Is Nothing OrElse docs.Count = 0 Then Return Nothing
                    f.Versions = docs.Select(Function(d) (S(d("v")), DateTimeOffset.FromUnixTimeMilliseconds(CLng(d("timestamp").GetValue(Of Long)())).ToString("yyyy-MM-dd", Inv))).ToList()
                    f.Latest = f.Versions.FirstOrDefault(Function(v) Not Regex.IsMatch(v.Version, "(?i)alpha|beta|rc|snapshot|-M\d")).Version
                    If f.Latest Is Nothing Then f.Latest = f.Versions(0).Version
                    f.LatestDate = f.Versions.First(Function(v) v.Version = f.Latest).Date
                    f.Homepage = $"https://central.sonatype.com/artifact/{parts(0)}/{parts(1)}"
            End Select
            Return f
        End Function

        Private Shared Function Day(s As String) As String
            If String.IsNullOrEmpty(s) Then Return ""
            Dim d As DateTimeOffset
            Return If(DateTimeOffset.TryParse(s, Inv, DateTimeStyles.AssumeUniversal, d), d.ToString("yyyy-MM-dd", Inv), s)
        End Function

        Friend Async Function PackageInfoAsync(<Description("Ecosystem")> ecosystem As String,
                                               <Description("Package name")> name As String,
                                               Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim f = Await FactsAsync(ecosystem, name, ct).ConfigureAwait(False)
            If f Is Nothing Then Return $"No {ecosystem} package named '{name}' (check the spelling; typosquatted names are a common attack)."
            Dim sb As New StringBuilder()
            sb.AppendLine($"{f.Name} ({DepsServer.Ecosystem(ecosystem).Key}) latest: {f.Latest}{If(String.IsNullOrEmpty(f.LatestDate), "", $" released {Day(f.LatestDate)}")}")
            If Not String.IsNullOrWhiteSpace(f.Description) Then sb.AppendLine("Description: " & ToolKit.Clip(f.Description.Trim(), 400))
            If Not String.IsNullOrWhiteSpace(f.License) Then sb.AppendLine("License: " & f.License)
            If Not String.IsNullOrWhiteSpace(f.Homepage) Then sb.AppendLine("Homepage: " & f.Homepage)
            If Not String.IsNullOrWhiteSpace(f.Repository) Then sb.AppendLine("Repository: " & f.Repository)
            If f.Versions.Count > 0 Then sb.Append($"{f.Versions.Count} versions published")
            Return sb.ToString().TrimEnd()
        End Function

        Friend Async Function VersionsAsync(<Description("Ecosystem")> ecosystem As String,
                                            <Description("Package name")> name As String,
                                            <Description("How many (default 20)")> Optional max_results As Integer = 20,
                                            Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim f = Await FactsAsync(ecosystem, name, ct).ConfigureAwait(False)
            If f Is Nothing Then Return $"No {ecosystem} package named '{name}'."
            Dim take = Math.Clamp(If(max_results <= 0, 20, max_results), 1, 200)
            Dim lines = f.Versions.Take(take).Select(Function(v) $"{v.Version}{If(String.IsNullOrEmpty(v.Date), "", "  " & Day(v.Date))}{If(Regex.IsMatch(v.Version, "(?i)[-.](alpha|beta|rc|pre|preview|dev|snapshot)|a\d+$|b\d+$"), "  (pre-release)", "")}{If(v.Version = f.Latest, "  <- latest", "")}")
            Return $"{f.Name}: {f.Versions.Count} versions, newest {Math.Min(take, f.Versions.Count)}:{vbLf}" & String.Join(vbLf, lines)
        End Function

        ' ------------------------------------------------------------------------------------------ OSV

        Private Async Function PostJsonAsync(url As String, body As JsonNode, ct As CancellationToken) As Task(Of JsonNode)
            Using content As New StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
                Using resp = Await _http.PostAsync(url, content, ct).ConfigureAwait(False)
                    Dim text = Await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(False)
                    If Not resp.IsSuccessStatusCode Then Throw New McpException($"OSV.dev answered {CInt(resp.StatusCode)}: {ToolKit.Clip(text, 300)}")
                    Return JsonNode.Parse(text)
                End Using
            End Using
        End Function

        ''' <summary>One vulnerability in a line or two: id, CVE aliases, severity, summary and the fixed versions for this package.</summary>
        Friend Shared Function DescribeVuln(v As JsonNode, packageName As String) As String
            Dim id = S(v("id"))
            Dim aliases = If(v("aliases")?.AsArray().Select(Function(a) S(a)).Where(Function(a) a.StartsWith("CVE-", StringComparison.Ordinal) OrElse a.StartsWith("GHSA-", StringComparison.Ordinal)).ToList(), New List(Of String))
            Dim severity = If(S(v("database_specific")?("severity")), S(v("severity")?.AsArray().FirstOrDefault()?("score")))
            Dim fixes As New List(Of String)
            For Each aff In If(v("affected")?.AsArray(), New JsonArray())
                If packageName IsNot Nothing AndAlso Not String.Equals(S(aff("package")?("name")), packageName, StringComparison.OrdinalIgnoreCase) Then Continue For
                For Each rng In If(aff("ranges")?.AsArray(), New JsonArray())
                    For Each ev In If(rng("events")?.AsArray(), New JsonArray())
                        Dim fx = S(ev("fixed"))
                        If fx IsNot Nothing AndAlso Not fixes.Contains(fx) Then fixes.Add(fx)
                    Next
                Next
            Next
            Return $"{id}{If(aliases.Count > 0, " (" & String.Join(", ", aliases.Where(Function(a) a <> id).Take(3)) & ")", "")}{If(severity IsNot Nothing, " [" & severity & "]", "")}: " &
                   $"{ToolKit.Clip(If(S(v("summary")), ToolKit.Clip(S(v("details")), 200)), 300)}{If(fixes.Count > 0, " - fixed in " & String.Join(", ", fixes.Take(5)), " - no fixed version listed")}"
        End Function

        Friend Async Function VulnsAsync(<Description("Ecosystem")> ecosystem As String,
                                         <Description("Package name")> name As String,
                                         <Description("Version to check (default: the latest)")> Optional version As String = "",
                                         Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim e = DepsServer.Ecosystem(ecosystem)
            Dim ver = If(version, "").Trim().TrimStart("v"c, "^"c, "~"c, "="c)
            If ver.Length = 0 Then
                Dim f = Await FactsAsync(ecosystem, name, ct).ConfigureAwait(False)
                If f Is Nothing Then Return $"No {ecosystem} package named '{name}'."
                ver = f.Latest
            End If
            If e.Key = "go" AndAlso Not ver.StartsWith("v") Then ver = "v" & ver
            Dim q As New JsonObject From {{"package", New JsonObject From {{"name", name.Trim()}, {"ecosystem", e.Osv}}}, {"version", ver}}
            Dim r = Await PostJsonAsync("https://api.osv.dev/v1/query", q, ct).ConfigureAwait(False)
            Dim vulns = r?("vulns")?.AsArray()
            If vulns Is Nothing OrElse vulns.Count = 0 Then Return $"{name} {ver}: no known vulnerabilities in OSV.dev."
            Return $"{name} {ver}: {vulns.Count} known vulnerabilit{If(vulns.Count = 1, "y", "ies")}:{vbLf}" & String.Join(vbLf, vulns.Take(40).Select(Function(v) "- " & DescribeVuln(v, name.Trim())))
        End Function

        ' ------------------------------------------------------------------------------------------ manifests

        ''' <summary>The dependencies a manifest or lock file names: (ecosystem key, name, version or "" when it is a range).</summary>
        Friend Shared Function ParseManifest(fileName As String, text As String) As List(Of (Eco As String, Name As String, Version As String))
            Dim list As New List(Of (Eco As String, Name As String, Version As String))
            Dim lower = fileName.ToLowerInvariant()
            Dim exact = Function(v As String) As String
                            Dim m = Regex.Match(If(v, ""), "^\s*[\^~=v>]*\s*(\d+(?:\.\d+){0,3}(?:[-+][0-9A-Za-z.\-]+)?)\s*$")
                            Return If(m.Success, m.Groups(1).Value, "")
                        End Function
            If lower = "package-lock.json" Then
                Dim j = JsonNode.Parse(text)
                Dim pkgs = j("packages")?.AsObject()
                If pkgs IsNot Nothing Then
                    For Each kv In pkgs
                        If kv.Key = "" Then Continue For
                        Dim nm = kv.Key.Substring(kv.Key.LastIndexOf("node_modules/", StringComparison.Ordinal) + "node_modules/".Length)
                        list.Add(("npm", nm, S(kv.Value?("version"))))
                    Next
                Else
                    For Each kv In If(j("dependencies")?.AsObject(), New JsonObject())
                        list.Add(("npm", kv.Key, S(kv.Value?("version"))))
                    Next
                End If
            ElseIf lower = "package.json" Then
                Dim j = JsonNode.Parse(text)
                For Each section In {"dependencies", "devDependencies", "optionalDependencies", "peerDependencies"}
                    For Each kv In If(j(section)?.AsObject(), New JsonObject())
                        list.Add(("npm", kv.Key, exact(S(kv.Value))))
                    Next
                Next
            ElseIf lower.EndsWith(".txt") AndAlso lower.Contains("requirements") Then
                For Each line In text.Split(ControlChars.Lf)
                    Dim t = line.Split("#"c)(0).Trim()
                    If t.Length = 0 OrElse t.StartsWith("-") Then Continue For
                    Dim m = Regex.Match(t, "^([A-Za-z0-9][A-Za-z0-9._\-]*)(?:\[[^\]]*\])?\s*(?:==\s*([0-9][^\s;,]*))?")
                    If m.Success Then list.Add(("pypi", m.Groups(1).Value, m.Groups(2).Value))
                Next
            ElseIf Regex.IsMatch(lower, "\.(cs|vb|fs)proj$|^directory\.packages\.props$|^packages\.props$") Then
                Dim doc = XDocument.Parse(text)
                For Each el In doc.Descendants().Where(Function(x) x.Name.LocalName = "PackageReference" OrElse x.Name.LocalName = "PackageVersion")
                    Dim nm = If(CStr(el.Attribute("Include")), CStr(el.Attribute("Update")))
                    Dim v = If(CStr(el.Attribute("Version")), el.Elements().FirstOrDefault(Function(x) x.Name.LocalName = "Version")?.Value)
                    If nm IsNot Nothing Then list.Add(("nuget", nm, exact(If(v, "").Trim("["c, "]"c, "("c, ")"c).Split(","c)(0))))
                Next
            ElseIf lower = "packages.lock.json" Then
                Dim j = JsonNode.Parse(text)
                For Each fw In If(j("dependencies")?.AsObject(), New JsonObject())
                    For Each kv In If(fw.Value?.AsObject(), New JsonObject())
                        list.Add(("nuget", kv.Key, S(kv.Value?("resolved"))))
                    Next
                Next
            ElseIf lower = "cargo.lock" Then
                For Each m As Match In Regex.Matches(text, "\[\[package\]\]\s*\nname\s*=\s*""([^""]+)""\s*\nversion\s*=\s*""([^""]+)""")
                    list.Add(("crates", m.Groups(1).Value, m.Groups(2).Value))
                Next
            ElseIf lower = "cargo.toml" Then
                Dim section = ""
                For Each line In text.Split(ControlChars.Lf)
                    Dim t = line.Trim()
                    If t.StartsWith("[") Then section = t.Trim("["c, "]"c).ToLowerInvariant() : Continue For
                    If Not section.EndsWith("dependencies") Then Continue For
                    Dim m = Regex.Match(t, "^([A-Za-z0-9_\-]+)\s*=\s*(?:""([^""]+)""|\{.*?version\s*=\s*""([^""]+)"".*\})")
                    If m.Success Then list.Add(("crates", m.Groups(1).Value, exact(If(m.Groups(2).Success, m.Groups(2).Value, m.Groups(3).Value))))
                Next
            ElseIf lower = "go.mod" Then
                For Each m As Match In Regex.Matches(text, "^\s*(?:require\s+)?([a-zA-Z0-9.\-_/]+\.[a-z]{2,}[^\s]*)\s+(v[0-9][^\s]*)", RegexOptions.Multiline)
                    list.Add(("go", m.Groups(1).Value, m.Groups(2).Value))
                Next
            ElseIf lower = "gemfile.lock" Then
                For Each m As Match In Regex.Matches(text, "^    ([A-Za-z0-9_\-]+) \(([0-9][^)]*)\)", RegexOptions.Multiline)
                    list.Add(("rubygems", m.Groups(1).Value, m.Groups(2).Value))
                Next
            ElseIf lower = "pom.xml" Then
                Dim doc = XDocument.Parse(text)
                For Each dep In doc.Descendants().Where(Function(x) x.Name.LocalName = "dependency")
                    Dim g = dep.Elements().FirstOrDefault(Function(x) x.Name.LocalName = "groupId")?.Value
                    Dim a = dep.Elements().FirstOrDefault(Function(x) x.Name.LocalName = "artifactId")?.Value
                    Dim v = dep.Elements().FirstOrDefault(Function(x) x.Name.LocalName = "version")?.Value
                    If g IsNot Nothing AndAlso a IsNot Nothing Then list.Add(("maven", g & ":" & a, If(v IsNot Nothing AndAlso Not v.Contains("${"), v, "")))
                Next
            Else
                Throw New McpException($"'{fileName}' is not a manifest this tool reads.")
            End If
            Return list.Where(Function(d) Not String.IsNullOrWhiteSpace(d.Name)).
                Select(Function(d) (d.Eco, d.Name.Trim(), If(d.Version, "").Trim())).Distinct().ToList()
        End Function

        Friend Async Function AuditAsync(<Description("Manifest or lock file")> path As String,
                                         <Description("Also look up the latest version of each package (slower)")> Optional check_latest As Boolean = False,
                                         Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim full = _roots.Resolve(path)
            If Not File.Exists(full) Then Throw New McpException($"File '{path}' does not exist.")
            Dim deps As List(Of (Eco As String, Name As String, Version As String))
            Try
                deps = ParseManifest(IO.Path.GetFileName(full), File.ReadAllText(full))
            Catch ex As Exception When TypeOf ex Is JsonException OrElse TypeOf ex Is Xml.XmlException
                Throw New McpException("The file could not be parsed: " & ex.Message)
            End Try
            If deps.Count = 0 Then Return "No dependencies found in " & IO.Path.GetFileName(full) & "."
            Dim pinned = deps.Where(Function(d) d.Version.Length > 0).Take(1000).ToList()
            Dim ranged = deps.Where(Function(d) d.Version.Length = 0).ToList()
            Dim sb As New StringBuilder($"{IO.Path.GetFileName(full)}: {deps.Count} dependenc{If(deps.Count = 1, "y", "ies")} ({pinned.Count} with an exact version)." & vbLf)
            Dim affected As New List(Of (Dep As (Eco As String, Name As String, Version As String), Ids As List(Of String)))
            If pinned.Count > 0 Then
                Dim queries As New JsonArray()
                For Each d In pinned
                    queries.Add(New JsonObject From {{"package", New JsonObject From {{"name", d.Name}, {"ecosystem", Ecosystem(d.Eco).Osv}}}, {"version", d.Version}})
                Next
                Dim r = Await PostJsonAsync("https://api.osv.dev/v1/querybatch", New JsonObject From {{"queries", queries}}, ct).ConfigureAwait(False)
                Dim results = r?("results")?.AsArray()
                For i = 0 To pinned.Count - 1
                    Dim ids = If(results?(i)?("vulns")?.AsArray().Select(Function(v) S(v("id"))).ToList(), New List(Of String))
                    If ids.Count > 0 Then affected.Add((pinned(i), ids))
                Next
            End If
            If affected.Count = 0 Then
                sb.AppendLine("No known vulnerabilities in the pinned versions (OSV.dev).")
            Else
                sb.AppendLine($"{affected.Count} package(s) with known vulnerabilities:")
                Dim details As New Dictionary(Of String, JsonNode)
                Using gate As New SemaphoreSlim(8)
                    Dim ids = affected.SelectMany(Function(a) a.Ids).Distinct().Take(60).ToList()
                    Dim fetches = ids.Select(Async Function(id)
                                                 Await gate.WaitAsync(ct).ConfigureAwait(False)
                                                 Try
                                                     Return (id, Await GetJsonAsync("https://api.osv.dev/v1/vulns/" & Uri.EscapeDataString(id), ct).ConfigureAwait(False))
                                                 Finally
                                                     gate.Release()
                                                 End Try
                                             End Function)
                    For Each pair In Await Task.WhenAll(fetches).ConfigureAwait(False)
                        If pair.Item2 IsNot Nothing Then details(pair.id) = pair.Item2
                    Next
                End Using
                For Each a In affected
                    sb.AppendLine($"* {a.Dep.Name} {a.Dep.Version} ({a.Dep.Eco}):")
                    For Each id In a.Ids
                        Dim v As JsonNode = Nothing
                        sb.AppendLine("  - " & If(details.TryGetValue(id, v), DescribeVuln(v, a.Dep.Name), id))
                    Next
                Next
            End If
            If ranged.Count > 0 Then sb.AppendLine($"Not checked ({ranged.Count}, no exact version - audit the lock file instead): {String.Join(", ", ranged.Take(30).Select(Function(d) d.Name))}{If(ranged.Count > 30, ", ...", "")}")
            If check_latest Then
                Dim outdated As New List(Of String)
                Using gate As New SemaphoreSlim(8)
                    Dim lookups = pinned.Take(80).Select(Async Function(d)
                                                             Await gate.WaitAsync(ct).ConfigureAwait(False)
                                                             Try
                                                                 Dim f = Await FactsAsync(d.Eco, d.Name, ct).ConfigureAwait(False)
                                                                 Return If(f IsNot Nothing AndAlso f.Latest IsNot Nothing AndAlso f.Latest.TrimStart("v"c) <> d.Version.TrimStart("v"c), $"{d.Name} {d.Version} -> {f.Latest}", Nothing)
                                                             Catch ex As Exception When TypeOf ex Is McpException OrElse TypeOf ex Is HttpRequestException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is OperationCanceledException
                                                                 Return Nothing
                                                             Finally
                                                                 gate.Release()
                                                             End Try
                                                         End Function)
                    outdated.AddRange((Await Task.WhenAll(lookups).ConfigureAwait(False)).Where(Function(x) x IsNot Nothing))
                End Using
                sb.AppendLine(If(outdated.Count = 0, "All checked packages are on their latest version.", $"Newer versions available ({outdated.Count}):" & vbLf & String.Join(vbLf, outdated.Select(Function(o) "  " & o))))
            End If
            Return sb.ToString().TrimEnd()
        End Function
    End Class

End Namespace
