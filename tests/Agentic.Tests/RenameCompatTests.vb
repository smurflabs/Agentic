Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports Agentic
Imports Agentic.Configuration
Imports Xunit

''' <summary>Installs from before the AgentHost → Agentic rename keep their data and environment settings.</summary>
Public Class RenameCompatTests

    <Fact>
    Public Sub LegacyDatabaseIsRenamedWithItsSideFiles()
        Dim dir = Path.Combine(Path.GetTempPath(), "agentic-rename-" & Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(dir)
        Try
            File.WriteAllText(Path.Combine(dir, "agenthost.db"), "main")
            File.WriteAllText(Path.Combine(dir, "agenthost.db-wal"), "wal")

            Dim resolved = HostApplication.ResolveDatabasePath(dir, Nothing)

            Assert.Equal(Path.Combine(dir, "agentic.db"), resolved)
            Assert.Equal("main", File.ReadAllText(resolved))
            Assert.Equal("wal", File.ReadAllText(resolved & "-wal"))
            Assert.False(File.Exists(Path.Combine(dir, "agenthost.db")))
            Assert.False(File.Exists(Path.Combine(dir, "agenthost.db-wal")))

            ' Once agentic.db exists, a stray legacy file is left alone.
            File.WriteAllText(Path.Combine(dir, "agenthost.db"), "stale")
            Assert.Equal(resolved, HostApplication.ResolveDatabasePath(dir, Nothing))
            Assert.Equal("main", File.ReadAllText(resolved))
        Finally
            Directory.Delete(dir, recursive:=True)
        End Try
    End Sub

    <Fact>
    Public Sub FreshDataFolderUsesAgenticDb()
        Dim dir = Path.Combine(Path.GetTempPath(), "agentic-rename-" & Guid.NewGuid().ToString("N"))
        Assert.Equal(Path.Combine(dir, "agentic.db"), HostApplication.ResolveDatabasePath(dir, Nothing))
    End Sub

    <Fact>
    Public Sub LegacyEnvironmentPrefixStillAppliesAndNewPrefixWins()
        Dim cfg = Path.Combine(AppContext.BaseDirectory, "appsettings.json")
        Const LegacyOnly = "AGENTHOST_Alerts__WebhookUrlEnvVar"
        Const BothOld = "AGENTHOST_Retention__BackupCount"
        Const BothNew = "AGENTIC_Retention__BackupCount"
        Try
            Environment.SetEnvironmentVariable(LegacyOnly, "RENAME_TEST_HOOK")
            Environment.SetEnvironmentVariable(BothOld, "3")
            Environment.SetEnvironmentVariable(BothNew, "9")
            Dim o = ConfigLoader.Load(cfg).Options
            Assert.Equal("RENAME_TEST_HOOK", o.Alerts.WebhookUrlEnvVar)
            Assert.Equal(9, o.Retention.BackupCount)
        Finally
            Environment.SetEnvironmentVariable(LegacyOnly, Nothing)
            Environment.SetEnvironmentVariable(BothOld, Nothing)
            Environment.SetEnvironmentVariable(BothNew, Nothing)
        End Try
    End Sub

End Class
