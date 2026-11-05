Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports Agentic.Artifacts
Imports Agentic.Configuration
Imports Agentic.Logging
Imports Agentic.Memory

Namespace Persistence

    ''' <summary>
    ''' Applies retention so the database stays bounded during months of operation. Ledger entries are never deleted.
    ''' Message contents are purged before their metadata; AI/tool payloads before their metrics.
    ''' </summary>
    Public NotInheritable Class RetentionService
        Private ReadOnly _db As Database
        Private ReadOnly _opt As RetentionOptions
        Private ReadOnly _memory As IMemoryStore
        Private ReadOnly _artifacts As ArtifactStore
        Private ReadOnly _log As ILog

        Public Sub New(db As Database, options As RetentionOptions, memory As IMemoryStore, artifacts As ArtifactStore, log As ILog)
            _db = db
            _opt = options
            _memory = memory
            _artifacts = artifacts
            _log = log
        End Sub

        Private Shared Function Cutoff(now As DateTime, days As Integer) As String
            Return Db.Ts(now.AddDays(-Math.Max(1, days)))
        End Function

        Public Async Function RunOnceAsync(now As DateTime) As Task(Of Dictionary(Of String, Integer))
            Dim r As New Dictionary(Of String, Integer)
            r("activity") = Await _db.ExecuteAsync("DELETE FROM ActivityEvents WHERE CreatedUtc < @c", ("@c", CObj(Cutoff(now, _opt.ActivityDays)))).ConfigureAwait(False)
            r("aiCallDetails") = Await _db.ExecuteAsync("UPDATE AiCalls SET RequestPreview=NULL, ResponsePreview=NULL, Error=SUBSTR(Error,1,200) WHERE CreatedUtc < @c AND (RequestPreview IS NOT NULL OR ResponsePreview IS NOT NULL)",
                                                       ("@c", CObj(Cutoff(now, _opt.AiCallDetailDays)))).ConfigureAwait(False)
            r("aiCalls") = Await _db.ExecuteAsync("DELETE FROM AiCalls WHERE CreatedUtc < @c", ("@c", CObj(Cutoff(now, _opt.AiCallMetricDays)))).ConfigureAwait(False)
            ' Model speeds (the model picker) only need the last day; a week is kept.
            r("modelSpeed") = Await _db.ExecuteAsync("DELETE FROM ModelSpeed WHERE CreatedUtc < @c", ("@c", CObj(Cutoff(now, 7)))).ConfigureAwait(False)
            r("toolPayloads") = Await _db.ExecuteAsync("UPDATE ToolCalls SET Arguments=NULL, Result=NULL WHERE StartedUtc < @c AND (Arguments IS NOT NULL OR Result IS NOT NULL)",
                                                      ("@c", CObj(Cutoff(now, _opt.ToolCallPayloadDays)))).ConfigureAwait(False)
            r("toolCalls") = Await _db.ExecuteAsync("DELETE FROM ToolCalls WHERE StartedUtc < @c", ("@c", CObj(Cutoff(now, _opt.ToolCallMetricDays)))).ConfigureAwait(False)
            r("messageContent") = Await _db.ExecuteAsync(
                "UPDATE AgentMessages SET Content=NULL, StructuredPayload=NULL, ContentPurgedUtc=@now WHERE CreatedUtc < @c AND ContentPurgedUtc IS NULL " &
                "AND Status IN ('Completed','Failed','Rejected','Expired','Cancelled')",
                ("@now", CObj(Db.Ts(now))), ("@c", Cutoff(now, _opt.MessageContentDays))).ConfigureAwait(False)
            r("messages") = Await _db.ExecuteAsync("DELETE FROM AgentMessages WHERE CreatedUtc < @c AND Status IN ('Completed','Failed','Rejected','Expired','Cancelled')",
                                                  ("@c", CObj(Cutoff(now, _opt.MessageMetadataDays)))).ConfigureAwait(False)
            r("tasks") = Await _db.ExecuteAsync("DELETE FROM AgentTasks WHERE COALESCE(CompletedUtc, UpdatedUtc) < @c AND Status IN ('Completed','Failed','Cancelled')",
                                               ("@c", CObj(Cutoff(now, _opt.CompletedTaskDays)))).ConfigureAwait(False)
            r("pollingTasks") = Await _db.ExecuteAsync("DELETE FROM AgentTasks WHERE Kind='Polling' AND Status='Completed' AND COALESCE(CompletedUtc, UpdatedUtc) < @c",
                                                      ("@c", CObj(Cutoff(now, _opt.PollingTaskDays)))).ConfigureAwait(False)
            r("taskHistory") = Await _db.ExecuteAsync("DELETE FROM TaskHistory WHERE CreatedUtc < @c", ("@c", CObj(Cutoff(now, _opt.TaskHistoryDays)))).ConfigureAwait(False)
            ' The history and kept conversations of tasks that are gone.
            r("orphanHistory") = Await _db.ExecuteAsync("DELETE FROM TaskHistory WHERE NOT EXISTS (SELECT 1 FROM AgentTasks a WHERE a.Id = TaskHistory.TaskId)").ConfigureAwait(False)
            r("transcripts") = Await _db.ExecuteAsync("DELETE FROM TaskTranscripts WHERE NOT EXISTS (SELECT 1 FROM AgentTasks a WHERE a.Id = TaskTranscripts.TaskId)").ConfigureAwait(False)
            r("audit") = Await _db.ExecuteAsync("DELETE FROM AuditLog WHERE CreatedUtc < @c", ("@c", CObj(Cutoff(now, _opt.AuditDays)))).ConfigureAwait(False)
            r("statistics") = Await _db.ExecuteAsync("DELETE FROM Statistics WHERE BucketUtc < @c", ("@c", CObj(Cutoff(now, _opt.StatisticsDays)))).ConfigureAwait(False)
            r("approvals") = Await _db.ExecuteAsync("DELETE FROM Approvals WHERE Status <> 'Pending' AND CreatedUtc < @c", ("@c", CObj(Cutoff(now, _opt.AuditDays)))).ConfigureAwait(False)
            ' Case studies keep their report and lessons forever; only the bulky raw evidence expires.
            r("caseStudyEvidence") = Await _db.ExecuteAsync("UPDATE CaseStudies SET EvidenceJson=NULL WHERE CreatedUtc < @c AND EvidenceJson IS NOT NULL " &
                                                            "AND Status IN ('Published','AwaitingReview','Failed')",
                                                            ("@c", CObj(Cutoff(now, _opt.CaseStudyEvidenceDays)))).ConfigureAwait(False)
            r("memories") = Await _memory.DeleteExpiredAsync(now).ConfigureAwait(False)
            r("artifacts") = Await _artifacts.DeleteExpiredAsync(now).ConfigureAwait(False)
            Await _db.ExecuteAsync("PRAGMA incremental_vacuum(4000);").ConfigureAwait(False)
            Await _db.ExecuteAsync("PRAGMA optimize;").ConfigureAwait(False)
            Dim total = r.Values.Sum()
            If total > 0 Then _log.Info("Retention", "Retention removed/purged: " & String.Join(", ", r.Where(Function(kv) kv.Value > 0).Select(Function(kv) $"{kv.Key}={kv.Value}")))
            Return r
        End Function
    End Class

    ''' <summary>Online SQLite backups (consistent while running) with rotation.</summary>
    Public NotInheritable Class BackupService
        Private ReadOnly _db As Database
        Private ReadOnly _dir As String
        Private ReadOnly _keep As Integer
        Private ReadOnly _log As ILog

        Public Sub New(db As Database, directory As String, keep As Integer, log As ILog)
            _db = db
            _dir = directory
            _keep = Math.Max(1, keep)
            _log = log
        End Sub

        Public Async Function BackupAsync() As Task(Of String)
            Directory.CreateDirectory(_dir)
            Dim file = Path.Combine(_dir, $"agentic-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db")
            Await _db.BackupToAsync(file).ConfigureAwait(False)
            _log.Info("Backup", "Database backup written: " & file)
            ' agenthost-*.db are backups from before the rename; they sort after agentic-* so they age out first.
            Dim di As New DirectoryInfo(_dir)
            For Each old In di.GetFiles("agentic-*.db").Concat(di.GetFiles("agenthost-*.db")).OrderByDescending(Function(f) f.Name).Skip(_keep)
                Try
                    old.Delete()
                Catch
                End Try
            Next
            Return file
        End Function
    End Class

End Namespace
