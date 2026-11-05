Option Strict On
Option Explicit On
Option Infer On

Imports Microsoft.Data.Sqlite
Imports Agentic.Communication
Imports Agentic.Utilities

Namespace Persistence

    Public Class MessageQuery
        Public Property AgentId As String
        Public Property SenderAgentId As String
        Public Property RecipientAgentId As String
        Public Property MessageType As MessageType?
        Public Property Status As MessageStatus?
        Public Property CorrelationId As String
        Public Property FromUtc As DateTime?
        Public Property ToUtc As DateTime?
        Public Property Limit As Integer = 100
        Public Property Offset As Integer
    End Class

    Public NotInheritable Class MessageRepository
        Private ReadOnly _db As Database

        Public Sub New(db As Database)
            _db = db
        End Sub

        Private Const Columns As String =
            "Id, CorrelationId, ParentMessageId, SenderAgentId, RecipientAgentId, MessageType, Subject, Content, StructuredPayload, Attachments, " &
            "Priority, CreatedUtc, DeliveredUtc, StartedUtc, CompletedUtc, Status, ExpiresUtc, ReplyToMessageId, RequiresResponse, ResponseMessageId, " &
            "SenderTaskId, RelatedTaskId, DelegationDepth, ChainLength, Ancestry, IdempotencyKey, RejectionReason, LeaseExpiresUtc, ContentPurgedUtc"

        Public Shared Function Map(r As SqliteDataReader) As AgentMessage
            Return New AgentMessage With {
                .Id = Db.Str(r, "Id"),
                .CorrelationId = Db.Str(r, "CorrelationId"),
                .ParentMessageId = Db.Str(r, "ParentMessageId"),
                .SenderAgentId = Db.Str(r, "SenderAgentId"),
                .RecipientAgentId = Db.Str(r, "RecipientAgentId"),
                .MessageType = Db.EnumOf(r, "MessageType", MessageType.Information),
                .Subject = Db.Str(r, "Subject"),
                .Content = Db.Str(r, "Content"),
                .StructuredPayload = Db.Str(r, "StructuredPayload"),
                .Attachments = If(JsonUtil.FromJson(Of List(Of String))(Db.Str(r, "Attachments")), New List(Of String)),
                .Priority = CType(Db.Int(r, "Priority"), PriorityLevel),
                .CreatedUtc = Db.Dt(r, "CreatedUtc"),
                .DeliveredUtc = Db.DtN(r, "DeliveredUtc"),
                .StartedUtc = Db.DtN(r, "StartedUtc"),
                .CompletedUtc = Db.DtN(r, "CompletedUtc"),
                .Status = Db.EnumOf(r, "Status", MessageStatus.Pending),
                .ExpiresUtc = Db.DtN(r, "ExpiresUtc"),
                .ReplyToMessageId = Db.Str(r, "ReplyToMessageId"),
                .RequiresResponse = Db.Bool(r, "RequiresResponse"),
                .ResponseMessageId = Db.Str(r, "ResponseMessageId"),
                .SenderTaskId = Db.Str(r, "SenderTaskId"),
                .RelatedTaskId = Db.Str(r, "RelatedTaskId"),
                .DelegationDepth = Db.Int(r, "DelegationDepth"),
                .ChainLength = Db.Int(r, "ChainLength"),
                .Ancestry = If(JsonUtil.FromJson(Of List(Of String))(Db.Str(r, "Ancestry")), New List(Of String)),
                .IdempotencyKey = Db.Str(r, "IdempotencyKey"),
                .RejectionReason = Db.Str(r, "RejectionReason"),
                .LeaseExpiresUtc = Db.DtN(r, "LeaseExpiresUtc"),
                .ContentPurgedUtc = Db.DtN(r, "ContentPurgedUtc")
            }
        End Function

        Private Shared Function Params(m As AgentMessage) As (String, Object)()
            Return {
                ("@Id", CObj(m.Id)), ("@CorrelationId", m.CorrelationId), ("@ParentMessageId", m.ParentMessageId), ("@SenderAgentId", m.SenderAgentId),
                ("@RecipientAgentId", m.RecipientAgentId), ("@MessageType", m.MessageType), ("@Subject", m.Subject), ("@Content", m.Content),
                ("@StructuredPayload", m.StructuredPayload), ("@Attachments", If(m.Attachments Is Nothing OrElse m.Attachments.Count = 0, Nothing, JsonUtil.ToJson(m.Attachments))),
                ("@Priority", CInt(m.Priority)), ("@CreatedUtc", Db.Ts(m.CreatedUtc)), ("@DeliveredUtc", Db.Ts(m.DeliveredUtc)),
                ("@StartedUtc", Db.Ts(m.StartedUtc)), ("@CompletedUtc", Db.Ts(m.CompletedUtc)), ("@Status", m.Status), ("@ExpiresUtc", Db.Ts(m.ExpiresUtc)),
                ("@ReplyToMessageId", m.ReplyToMessageId), ("@RequiresResponse", m.RequiresResponse), ("@ResponseMessageId", m.ResponseMessageId),
                ("@SenderTaskId", m.SenderTaskId), ("@RelatedTaskId", m.RelatedTaskId), ("@DelegationDepth", m.DelegationDepth),
                ("@ChainLength", m.ChainLength), ("@Ancestry", JsonUtil.ToJson(If(m.Ancestry, New List(Of String)))), ("@IdempotencyKey", m.IdempotencyKey),
                ("@RejectionReason", m.RejectionReason), ("@LeaseExpiresUtc", Db.Ts(m.LeaseExpiresUtc)), ("@ContentPurgedUtc", Db.Ts(m.ContentPurgedUtc))
            }
        End Function

        Private Shared ReadOnly InsertSql As String =
            $"INSERT INTO AgentMessages ({Columns}) VALUES (@Id, @CorrelationId, @ParentMessageId, @SenderAgentId, @RecipientAgentId, @MessageType, @Subject, " &
            "@Content, @StructuredPayload, @Attachments, @Priority, @CreatedUtc, @DeliveredUtc, @StartedUtc, @CompletedUtc, @Status, @ExpiresUtc, @ReplyToMessageId, " &
            "@RequiresResponse, @ResponseMessageId, @SenderTaskId, @RelatedTaskId, @DelegationDepth, @ChainLength, @Ancestry, @IdempotencyKey, @RejectionReason, " &
            "@LeaseExpiresUtc, @ContentPurgedUtc) ON CONFLICT DO NOTHING"

        Public Shared Async Function TxInsertAsync(conn As SqliteConnection, tx As SqliteTransaction, m As AgentMessage) As Task(Of Boolean)
            Return (Await Database.TxExecAsync(conn, tx, InsertSql, Params(m)).ConfigureAwait(False)) > 0
        End Function

        Public Async Function InsertAsync(m As AgentMessage) As Task(Of Boolean)
            Return (Await _db.ExecuteAsync(InsertSql, Params(m)).ConfigureAwait(False)) > 0
        End Function

        Public Function GetAsync(id As String) As Task(Of AgentMessage)
            Return _db.QuerySingleAsync($"SELECT {Columns} FROM AgentMessages WHERE Id=@id", AddressOf Map, ("@id", CObj(id)))
        End Function

        Public Function GetByIdempotencyAsync(senderId As String, key As String) As Task(Of AgentMessage)
            Return _db.QuerySingleAsync($"SELECT {Columns} FROM AgentMessages WHERE SenderAgentId=@s AND IdempotencyKey=@k", AddressOf Map, ("@s", CObj(senderId)), ("@k", key))
        End Function

        Public Shared Function TxGetByIdempotencyAsync(conn As SqliteConnection, tx As SqliteTransaction, senderId As String, key As String) As Task(Of List(Of AgentMessage))
            Return Database.TxQueryAsync(conn, tx, $"SELECT {Columns} FROM AgentMessages WHERE SenderAgentId=@s AND IdempotencyKey=@k", AddressOf Map, ("@s", CObj(senderId)), ("@k", key))
        End Function

        ''' <summary>Mailbox fetch: pending/delivered messages for a recipient, highest priority first.</summary>
        Public Function ListInboxAsync(recipientId As String, limit As Integer) As Task(Of List(Of AgentMessage))
            Return _db.QueryAsync($"SELECT {Columns} FROM AgentMessages WHERE RecipientAgentId=@r AND Status IN ('Pending','Delivered') " &
                                  "ORDER BY Priority DESC, CreatedUtc ASC LIMIT @l", AddressOf Map, ("@r", CObj(recipientId)), ("@l", limit))
        End Function

        Public Function MarkStatusAsync(id As String, status As MessageStatus, Optional reason As String = Nothing, Optional responseId As String = Nothing) As Task(Of Integer)
            Dim now = Db.Ts(DateTime.UtcNow)
            Dim sql = "UPDATE AgentMessages SET Status=@s, RejectionReason=COALESCE(@r, RejectionReason), ResponseMessageId=COALESCE(@resp, ResponseMessageId), " &
                      "DeliveredUtc=CASE WHEN @s IN ('Delivered','Processing','Completed') AND DeliveredUtc IS NULL THEN @now ELSE DeliveredUtc END, " &
                      "StartedUtc=CASE WHEN @s='Processing' AND StartedUtc IS NULL THEN @now ELSE StartedUtc END, " &
                      "CompletedUtc=CASE WHEN @s IN ('Completed','Failed','Rejected','Expired','Cancelled') THEN @now ELSE CompletedUtc END " &
                      "WHERE Id=@id"
            Return _db.ExecuteAsync(sql, ("@s", CObj(status.ToString())), ("@r", reason), ("@resp", responseId), ("@now", now), ("@id", id))
        End Function

        Public Function SetRelatedTaskAsync(id As String, taskId As String) As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE AgentMessages SET RelatedTaskId=@t WHERE Id=@id", ("@t", CObj(taskId)), ("@id", id))
        End Function

        Public Function CountInboxAsync(recipientId As String) As Task(Of Long)
            Return _db.ScalarLongAsync("SELECT COUNT(*) FROM AgentMessages WHERE RecipientAgentId=@r AND Status IN ('Pending','Delivered','Processing')", ("@r", CObj(recipientId)))
        End Function

        Public Function CountOutstandingRequestsAsync(senderId As String) As Task(Of Long)
            Return _db.ScalarLongAsync("SELECT COUNT(*) FROM AgentMessages WHERE SenderAgentId=@s AND RequiresResponse=1 AND Status IN ('Pending','Delivered','Processing')", ("@s", CObj(senderId)))
        End Function

        Public Function CountByCorrelationAsync(correlationId As String) As Task(Of Long)
            Return _db.ScalarLongAsync("SELECT COUNT(*) FROM AgentMessages WHERE CorrelationId=@c", ("@c", CObj(correlationId)))
        End Function

        ''' <summary>Marks overdue messages Expired and returns them (so waiting senders can be resumed).</summary>
        Public Async Function ExpireDueAsync(now As DateTime) As Task(Of List(Of AgentMessage))
            Dim due = Await _db.QueryAsync($"SELECT {Columns} FROM AgentMessages WHERE Status IN ('Pending','Delivered','Processing') AND ExpiresUtc IS NOT NULL AND ExpiresUtc <= @now LIMIT 200",
                                           AddressOf Map, ("@now", CObj(Db.Ts(now)))).ConfigureAwait(False)
            For Each m In due
                Await MarkStatusAsync(m.Id, MessageStatus.Expired, "Expired before completion").ConfigureAwait(False)
                m.Status = MessageStatus.Expired
            Next
            Return due
        End Function

        ''' <summary>Startup recovery: Processing messages whose handling task does not exist go back to Delivered.</summary>
        Public Function ResetOrphanedProcessingAsync() As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE AgentMessages SET Status='Delivered' WHERE Status='Processing' AND " &
                                    "NOT EXISTS (SELECT 1 FROM AgentTasks t WHERE t.SourceMessageId = AgentMessages.Id OR t.Id = AgentMessages.RelatedTaskId)")
        End Function

        Public Function QueryAsync(q As MessageQuery) As Task(Of List(Of AgentMessage))
            Dim where As New List(Of String)
            Dim ps As New List(Of (String, Object))
            If Not String.IsNullOrEmpty(q.AgentId) Then where.Add("(SenderAgentId=@agent OR RecipientAgentId=@agent)") : ps.Add(("@agent", q.AgentId))
            If Not String.IsNullOrEmpty(q.SenderAgentId) Then where.Add("SenderAgentId=@sender") : ps.Add(("@sender", q.SenderAgentId))
            If Not String.IsNullOrEmpty(q.RecipientAgentId) Then where.Add("RecipientAgentId=@recipient") : ps.Add(("@recipient", q.RecipientAgentId))
            If q.MessageType.HasValue Then where.Add("MessageType=@type") : ps.Add(("@type", q.MessageType.Value.ToString()))
            If q.Status.HasValue Then where.Add("Status=@status") : ps.Add(("@status", q.Status.Value.ToString()))
            If Not String.IsNullOrEmpty(q.CorrelationId) Then where.Add("CorrelationId=@corr") : ps.Add(("@corr", q.CorrelationId))
            If q.FromUtc.HasValue Then where.Add("CreatedUtc>=@from") : ps.Add(("@from", Db.Ts(q.FromUtc.Value)))
            If q.ToUtc.HasValue Then where.Add("CreatedUtc<=@to") : ps.Add(("@to", Db.Ts(q.ToUtc.Value)))
            ps.Add(("@l", Math.Clamp(q.Limit, 1, 500)))
            ps.Add(("@o", Math.Max(0, q.Offset)))
            Dim sql = $"SELECT {Columns} FROM AgentMessages" & If(where.Count > 0, " WHERE " & String.Join(" AND ", where), String.Empty) &
                      " ORDER BY CreatedUtc DESC LIMIT @l OFFSET @o"
            Return _db.QueryAsync(sql, AddressOf Map, ps.ToArray())
        End Function

        Public Async Function TopologyAsync(sinceUtc As DateTime) As Task(Of List(Of Dictionary(Of String, Object)))
            Return Await _db.QueryAsync("SELECT SenderAgentId, RecipientAgentId, COUNT(*) AS C, SUM(CASE WHEN Status IN ('Failed','Rejected','Expired') THEN 1 ELSE 0 END) AS F " &
                                        "FROM AgentMessages WHERE CreatedUtc>=@s GROUP BY SenderAgentId, RecipientAgentId",
                                        Function(r) New Dictionary(Of String, Object) From {
                                            {"from", Db.Str(r, "SenderAgentId")}, {"to", Db.Str(r, "RecipientAgentId")},
                                            {"count", Db.Lng(r, "C")}, {"failed", Db.Lng(r, "F")}},
                                        ("@s", CObj(Db.Ts(sinceUtc)))).ConfigureAwait(False)
        End Function
    End Class

End Namespace
