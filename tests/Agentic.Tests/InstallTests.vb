Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Net.Http
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports Agentic.Configuration
Imports Agentic.Security
Imports Xunit

''' <summary>One-line installs (/install.ps1, /install.sh, /install/bin, /install/desktop.msi) and the Model Square leaving out models no longer offered.</summary>
Public Class InstallTests

    Private Shared Function Sha(text As String) As String
        Return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()
    End Function

    Private Shared Function MakeUpdates() As String
        Dim dir = IO.Path.Combine(IO.Path.GetTempPath(), "agentic-install-" & Guid.NewGuid().ToString("N"))
        IO.Directory.CreateDirectory(IO.Path.Combine(dir, "cli"))
        IO.Directory.CreateDirectory(IO.Path.Combine(dir, "cli-linux-x64"))
        IO.Directory.CreateDirectory(IO.Path.Combine(dir, "desktop"))
        IO.Directory.CreateDirectory(IO.Path.Combine(dir, "installers"))
        IO.File.WriteAllText(IO.Path.Combine(dir, "cli", "agentic.exe"), "windows cli")
        IO.File.WriteAllText(IO.Path.Combine(dir, "cli", "release.json"), "{ ""version"": ""2.3.18"" }")
        IO.File.WriteAllText(IO.Path.Combine(dir, "cli-linux-x64", "agentic"), "linux cli")
        IO.File.WriteAllText(IO.Path.Combine(dir, "cli-linux-x64", "release.json"), "{ ""version"": ""2.3.18"" }")
        IO.File.WriteAllText(IO.Path.Combine(dir, "desktop", "AgenticClient.exe"), "desktop")
        IO.File.WriteAllText(IO.Path.Combine(dir, "installers", "AgenticDesktop-0.4.12-x64.msi"), "old msi")
        IO.File.WriteAllText(IO.Path.Combine(dir, "installers", "AgenticDesktop-0.4.13-x64.msi"), "new msi")
        Return dir
    End Function

    Private Shared Async Function StartAsync(up As FakeUpstream, dir As String, publicInstall As Boolean) As Task(Of (Host As TestHost, Port As Integer))
        Return Await PlatformApiTests.StartAsync(up, Sub(o)
                                                        o.Updates.Directory = dir
                                                        o.Updates.PublicInstall = publicInstall
                                                    End Sub)
    End Function

    <Fact>
    Public Async Function InstallScriptsAndDownloadsWorkWithoutSigningIn() As Task
        Dim dir = MakeUpdates()
        Try
            Using up As New FakeUpstream()
                Dim env = Await StartAsync(up, dir, True)
                Using env.Host
                    Using anon As New HttpClient With {.BaseAddress = New Uri($"http://127.0.0.1:{env.Port}")}
                        Dim ps1Res = Await anon.GetAsync("/install.ps1")
                        Assert.Equal(HttpStatusCode.OK, ps1Res.StatusCode)
                        Dim ps1 = Await ps1Res.Content.ReadAsStringAsync()
                        Assert.Contains($"$server = 'http://127.0.0.1:{env.Port}'", ps1)
                        Assert.Contains($"'x64' = @{{ version = '2.3.18'; url = '/install/bin/cli'; sha256 = '{Sha("windows cli")}' }}", ps1)
                        Assert.DoesNotContain("__BUILDS__", ps1)
                        Assert.Contains("HKCU:\Environment", ps1)

                        Dim sh = Await anon.GetStringAsync("/install.sh")
                        Assert.Contains($"  cli-linux-x64) version='2.3.18'; sha='{Sha("linux cli")}' ;;", sh)
                        Assert.DoesNotContain(vbCr, sh)
                        Assert.StartsWith("#!/usr/bin/env bash", sh)

                        Assert.Equal("windows cli", Await anon.GetStringAsync("/install/bin/cli"))
                        Assert.Equal("linux cli", Await anon.GetStringAsync("/install/bin/cli-linux-x64"))
                        ' Only the CLI installs this way; other apps and odd names are not found.
                        Assert.Equal(HttpStatusCode.NotFound, (Await anon.GetAsync("/install/bin/desktop")).StatusCode)
                        Assert.Equal(HttpStatusCode.NotFound, (Await anon.GetAsync("/install/bin/..%2Fdesktop")).StatusCode)
                        ' The newest MSI.
                        Dim msi = Await anon.GetAsync("/install/desktop.msi")
                        Assert.Equal(HttpStatusCode.OK, msi.StatusCode)
                        Assert.Equal("new msi", Await msi.Content.ReadAsStringAsync())
                        Assert.Contains("AgenticDesktop-0.4.13-x64.msi", msi.Content.Headers.ContentDisposition.ToString())
                        ' The updates API itself still needs a sign-in.
                        Assert.Equal(HttpStatusCode.Unauthorized, (Await anon.GetAsync("/api/updates")).StatusCode)
                    End Using

                    Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                    Dim list = JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/updates")).RootElement
                    Assert.True(list.GetProperty("publicInstall").GetBoolean())
                    Dim installers = list.GetProperty("installers").EnumerateArray().ToList()
                    Assert.Single(installers)
                    Assert.Equal("0.4.13", installers(0).GetProperty("version").GetString())
                    Assert.Equal("/install/desktop.msi", installers(0).GetProperty("url").GetString())
                    ' The installers folder is not an update channel.
                    Assert.DoesNotContain(list.GetProperty("apps").EnumerateArray(), Function(a) a.GetProperty("app").GetString() = "installers")
                End Using
            End Using
        Finally
            Try : IO.Directory.Delete(dir, True) : Catch ex As IO.IOException : End Try
        End Try
    End Function

    <Fact>
    Public Async Function PublicInstallOffNeedsASignIn() As Task
        Dim dir = MakeUpdates()
        Try
            Using up As New FakeUpstream()
                Dim env = Await StartAsync(up, dir, False)
                Using env.Host
                    Using anon As New HttpClient With {.BaseAddress = New Uri($"http://127.0.0.1:{env.Port}")}
                        Assert.Equal(HttpStatusCode.NotFound, (Await anon.GetAsync("/install.ps1")).StatusCode)
                        Assert.Equal(HttpStatusCode.NotFound, (Await anon.GetAsync("/install.sh")).StatusCode)
                        Assert.Equal(HttpStatusCode.NotFound, (Await anon.GetAsync("/install/bin/cli")).StatusCode)
                        Assert.Equal(HttpStatusCode.NotFound, (Await anon.GetAsync("/install/desktop.msi")).StatusCode)
                        Assert.Equal(HttpStatusCode.Unauthorized, (Await anon.GetAsync("/api/updates/installers/desktop")).StatusCode)
                    End Using
                    Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                    Assert.Equal("new msi", Await admin.Client.GetStringAsync("/api/updates/installers/desktop"))
                    Dim list = JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/updates")).RootElement
                    Assert.False(list.GetProperty("publicInstall").GetBoolean())
                    Assert.Equal("/api/updates/installers/desktop", list.GetProperty("installers")(0).GetProperty("url").GetString())
                End Using
            End Using
        Finally
            Try : IO.Directory.Delete(dir, True) : Catch ex As IO.IOException : End Try
        End Try
    End Function

    <Fact>
    Public Async Function ModelSquareLeavesOutModelsNoLongerOffered() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                ' A price for a model the provider does not list (any more).
                Assert.True((Await PlatformApiTests.Send(admin.Client, "PUT", "/api/model-square", New With {.prices = {
                    New With {.model = "gone/old-model", .inputPerMillion = 1, .outputPerMillion = 2},
                    New With {.model = "fast/a", .inputPerMillion = 1, .outputPerMillion = 2}}}, admin.Csrf)).IsSuccessStatusCode)
                Dim square = JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/model-square")).RootElement
                Dim models = square.GetProperty("models").EnumerateArray().ToList()
                Assert.Contains(models, Function(m) m.GetProperty("id").GetString() = "fast/a")
                Assert.DoesNotContain(models, Function(m) m.GetProperty("id").GetString() = "gone/old-model")
                Assert.DoesNotContain(models, Function(m) m.GetProperty("group").GetString() = "gone")
                Assert.DoesNotContain(models, Function(m) m.GetProperty("note").ValueKind = JsonValueKind.String AndAlso m.GetProperty("note").GetString() = "no longer offered")
            End Using
        End Using
    End Function

End Class
