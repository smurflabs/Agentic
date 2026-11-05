Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.Threading
Imports Agentic.Configuration
Imports Agentic.Events
Imports Agentic.Persistence
Imports Agentic.Utilities

Namespace Economy

    Public Class WalletSnapshot
        Public Property AgentId As String
        Public Property BalanceMicros As Long
        Public Property ReservedMicros As Long
        Public Property LifetimeSpendMicros As Long
        Public Property LifetimeProfitMicros As Long
        Public Property LifetimeCreditMicros As Long
        Public Property SpentTodayMicros As Long
        Public Property DailyLimitMicros As Long?
        Public Property UpdatedUtc As DateTime

        Public ReadOnly Property AvailableMicros As Long
            Get
                Return BalanceMicros - ReservedMicros
            End Get
        End Property

        Public ReadOnly Property NetMicros As Long
            Get
                Return LifetimeProfitMicros - LifetimeSpendMicros
            End Get
        End Property

        ''' <summary>Return on AI spend: profit / spend (Nothing when nothing was spent).</summary>
        Public ReadOnly Property Roi As Double?
            Get
                If LifetimeSpendMicros <= 0 Then Return Nothing
                Return CDbl(LifetimeProfitMicros) / CDbl(LifetimeSpendMicros)
            End Get
        End Property

        ''' <summary>Average cloud spend per day over the recent window (Economy.RunwayWindowDays).</summary>
        Public Property BurnRateMicrosPerDay As Long

        ''' <summary>Paid calls in the recent window and their average cost.</summary>
        Public Property WindowCharges As Long
        Public Property WindowSpendMicros As Long

        Public ReadOnly Property AverageChargeMicros As Long
            Get
                Return If(WindowCharges > 0, WindowSpendMicros \ WindowCharges, 0L)
            End Get
        End Property

        ''' <summary>Days the available balance lasts at the current burn rate (Nothing while nothing is being spent).</summary>
        Public ReadOnly Property RunwayDays As Double?
            Get
                If BurnRateMicrosPerDay <= 0 Then Return Nothing
                Return Math.Max(0L, AvailableMicros) / CDbl(BurnRateMicrosPerDay)
            End Get
        End Property
    End Class

    Public Class LedgerEntry
        Public Property Id As Long
        Public Property AgentId As String
        Public Property EntryType As LedgerEntryType
        Public Property AmountMicros As Long
        Public Property BalanceAfterMicros As Long
        Public Property ModelKey As String
        Public Property AiCallId As String
        Public Property TaskId As String
        Public Property CorrelationId As String
        Public Property Description As String
        Public Property Reference As String
        Public Property CreatedBy As String
        Public Property CreatedUtc As DateTime
        ''' <summary>Agent that actually incurred the charge when it differs from the wallet owner (a subagent).</summary>
        Public Property SpentByAgentId As String
    End Class

    Public Class LedgerResult
        Public Property Applied As Boolean
        ''' <summary>Why a transfer was not made (the payer has too little money available).</summary>
        Public Property FailureReason As String
        Public Property Duplicate As Boolean
        Public Property BalanceAfterMicros As Long
        Public Property EntryId As Long
    End Class

    Public Class ChargeDetails
        Public Property EntryType As LedgerEntryType = LedgerEntryType.AiCharge
        Public Property ModelKey As String
        Public Property AiCallId As String
        Public Property TaskId As String
        Public Property CorrelationId As String
        Public Property Description As String
        ''' <summary>Set when a subagent spends from its parent's wallet.</summary>
        Public Property SpentByAgentId As String
    End Class

    ''' <summary>Notified of wallet events (used by organizational learning). Implementations must not block.</summary>
    Public Interface IWalletObserver
        Sub OnCharged(agentId As String, amountMicros As Long, balanceAfterMicros As Long)
        Sub OnReserveRefused(agentId As String, result As ReserveResult)
        Sub OnProfitCredited(agentId As String, amountMicros As Long, balanceAfterMicros As Long, taskId As String)
    End Interface

    ''' <summary>
    ''' Per-agent wallets with an append-only ledger. All balance changes are atomic conditional SQL updates
    ''' (the database is the source of truth; no application locks around I/O).
    ''' Cloud AI calls reserve an estimate first, then settle the actual cost and release the remainder.
    ''' </summary>
    Public NotInheritable Class WalletService
        Private ReadOnly _db As Database
        Private ReadOnly _events As IAgentEventBus
        Private ReadOnly _options As EconomyOptions
        Private ReadOnly _clock As IClock
        Private _globalSpentToday As Long
        Private _globalReserved As Long
        Private _globalDay As String
        Private ReadOnly _globalLock As New Object()

        Public Sub New(db As Database, events As IAgentEventBus, options As EconomyOptions, clock As IClock)
            _db = db
            _events = events
            _options = options
            _clock = clock
        End Sub

        Public ReadOnly Property Enabled As Boolean
            Get
                Return _options.Enabled
            End Get
        End Property

        Public Property Observer As IWalletObserver

        ''' <summary>Called with an agent's balance after every charge, credit and refused reservation (agent life and death). Must not block.</summary>
        Public Property BalanceChanged As Action(Of String, Long)

        Private Sub RaiseBalance(agentId As String, balanceMicros As Long)
            Try
                BalanceChanged?.Invoke(agentId, balanceMicros)
            Catch
            End Try
        End Sub

        Private Sub Notify(action As Action(Of IWalletObserver))
            Dim o = Observer
            If o Is Nothing Then Return
            Try
                action(o)
            Catch
                ' Observers are best-effort and never affect money movements.
            End Try
        End Sub

        Private Function Today() As String
            Return _clock.UtcNow.ToString("yyyy-MM-dd")
        End Function

        ''' <summary>Startup: clears stale reservations and seeds today's global spend.</summary>
        Public Async Function InitializeAsync() As Task
            Await _db.ExecuteAsync("UPDATE AgentWallets SET ReservedMicros=0").ConfigureAwait(False)
            Dim todayStart = _clock.UtcNow.Date
            Dim spent = Await _db.ScalarLongAsync("SELECT COALESCE(SUM(-AmountMicros),0) FROM LedgerEntries WHERE EntryType IN ('AiCharge','ImageCharge') AND CreatedUtc>=@s",
                                                  ("@s", CObj(Db.Ts(todayStart)))).ConfigureAwait(False)
            SyncLock _globalLock
                _globalDay = Today()
                _globalSpentToday = spent
                _globalReserved = 0
            End SyncLock
        End Function

        Public Async Function EnsureWalletAsync(agentId As String, initialMicros As Long, dailyLimitMicros As Long?) As Task
            Dim now = Db.Ts(_clock.UtcNow)
            Await _db.InTransactionAsync(Of Boolean)(
                Async Function(conn, tx)
                    Dim exists = Await Database.TxScalarAsync(conn, tx, "SELECT 1 FROM AgentWallets WHERE AgentId=@a", ("@a", CObj(agentId))).ConfigureAwait(False)
                    If exists Is Nothing Then
                        Await Database.TxExecAsync(conn, tx, "INSERT INTO AgentWallets(AgentId, BalanceMicros, LifetimeCreditMicros, DailyLimitMicros, UpdatedUtc) VALUES (@a, @b, @b, @d, @now)",
                                                   ("@a", CObj(agentId)), ("@b", initialMicros), ("@d", If(dailyLimitMicros.HasValue, CObj(dailyLimitMicros.Value), Nothing)), ("@now", now)).ConfigureAwait(False)
                        If initialMicros <> 0 Then
                            Await Database.TxExecAsync(conn, tx,
                                "INSERT INTO LedgerEntries(AgentId, EntryType, AmountMicros, BalanceAfterMicros, Description, IdempotencyKey, CreatedBy, CreatedUtc) " &
                                "VALUES (@a, 'InitialGrant', @b, @b, 'Initial balance', @k, 'host', @now)",
                                ("@a", CObj(agentId)), ("@b", initialMicros), ("@k", "initial:" & agentId), ("@now", now)).ConfigureAwait(False)
                        End If
                    Else
                        Await Database.TxExecAsync(conn, tx, "UPDATE AgentWallets SET DailyLimitMicros=@d WHERE AgentId=@a",
                                                   ("@d", If(dailyLimitMicros.HasValue, CObj(dailyLimitMicros.Value), Nothing)), ("@a", agentId)).ConfigureAwait(False)
                    End If
                    Return True
                End Function).ConfigureAwait(False)
        End Function

        Private Function MapWallet(r As Microsoft.Data.Sqlite.SqliteDataReader) As WalletSnapshot
            Dim today = Me.Today()
            Dim w As New WalletSnapshot With {
                .AgentId = Db.Str(r, "AgentId"), .BalanceMicros = Db.Lng(r, "BalanceMicros"), .ReservedMicros = Db.Lng(r, "ReservedMicros"),
                .LifetimeSpendMicros = Db.Lng(r, "LifetimeSpendMicros"), .LifetimeProfitMicros = Db.Lng(r, "LifetimeProfitMicros"),
                .LifetimeCreditMicros = Db.Lng(r, "LifetimeCreditMicros"),
                .SpentTodayMicros = If(Db.Str(r, "SpentTodayDate") = today, Db.Lng(r, "SpentTodayMicros"), 0L),
                .DailyLimitMicros = Db.LngN(r, "DailyLimitMicros"), .UpdatedUtc = Db.Dt(r, "UpdatedUtc")}
            ' Burn rate: spend in the window divided by the window length - or by the wallet's age when it is younger
            ' (at least one day), so a new agent's first expensive day is not diluted over a week it did not exist.
            Dim spend = Db.Lng(r, "WindowSpend")
            w.WindowSpendMicros = spend
            w.WindowCharges = Db.Lng(r, "WindowCharges")
            If spend > 0 Then
                Dim now = _clock.UtcNow
                Dim days = CDbl(RunwayWindow)
                Dim first = Db.DtN(r, "FirstEntryUtc")
                If first.HasValue Then days = Math.Min(days, (now - first.Value).TotalDays)
                w.BurnRateMicrosPerDay = CLng(spend / Math.Max(1.0, days))
            End If
            Return w
        End Function

        Private ReadOnly Property RunwayWindow As Integer
            Get
                Return Math.Max(1, _options.RunwayWindowDays)
            End Get
        End Property

        Private Function WalletSelect(where As String) As String
            Return "SELECT w.*, " &
                   "(SELECT COALESCE(SUM(-l.AmountMicros),0) FROM LedgerEntries l WHERE l.AgentId=w.AgentId AND l.EntryType IN ('AiCharge','ImageCharge') AND l.CreatedUtc>=@ws) AS WindowSpend, " &
                   "(SELECT COUNT(*) FROM LedgerEntries l WHERE l.AgentId=w.AgentId AND l.EntryType IN ('AiCharge','ImageCharge') AND l.CreatedUtc>=@ws) AS WindowCharges, " &
                   "(SELECT MIN(l.CreatedUtc) FROM LedgerEntries l WHERE l.AgentId=w.AgentId) AS FirstEntryUtc " &
                   "FROM AgentWallets w " & where
        End Function

        Private Function WindowStart() As String
            Return Db.Ts(_clock.UtcNow.AddDays(-RunwayWindow))
        End Function

        Public Function GetAsync(agentId As String) As Task(Of WalletSnapshot)
            Return _db.QuerySingleAsync(WalletSelect("WHERE w.AgentId=@a"), AddressOf MapWallet, ("@a", CObj(agentId)), ("@ws", WindowStart()))
        End Function

        Public Function ListAsync() As Task(Of List(Of WalletSnapshot))
            Return _db.QueryAsync(WalletSelect("ORDER BY w.AgentId"), AddressOf MapWallet, ("@ws", CObj(WindowStart())))
        End Function

        ''' <summary>True when the agent's balance lasts fewer than Economy.LowRunwayDays at its burn rate.</summary>
        Public Function IsLowRunway(w As WalletSnapshot) As Boolean
            Return w IsNot Nothing AndAlso w.RunwayDays.HasValue AndAlso w.RunwayDays.Value < _options.LowRunwayDays
        End Function

        Public ReadOnly Property LowRunwayDays As Double
            Get
                Return _options.LowRunwayDays
            End Get
        End Property

        Public ReadOnly Property BankruptcyEndsAgent As Boolean
            Get
                Return _options.BankruptcyEndsAgent
            End Get
        End Property

        Public ReadOnly Property BankruptcyThresholdMicros As Long
            Get
                Return TextUtil.DollarsToMicros(_options.BankruptcyThreshold)
            End Get
        End Property

        Public ReadOnly Property GlobalSpentTodayMicros As Long
            Get
                SyncLock _globalLock
                    RollGlobalDay()
                    Return _globalSpentToday
                End SyncLock
            End Get
        End Property

        Private Sub RollGlobalDay()
            Dim t = Today()
            If _globalDay <> t Then
                _globalDay = t
                _globalSpentToday = 0
            End If
        End Sub

        ''' <summary>Reserves funds for a cloud call. Fails if balance, the agent's daily limit or the global daily cap would be exceeded.</summary>
        Public Async Function TryReserveAsync(agentId As String, micros As Long) As Task(Of ReserveResult)
            If micros <= 0 Then Return ReserveResult.Ok
            ' Global daily cap (in-memory, conservative).
            Dim globalLimit = If(_options.GlobalDailyCloudLimit.HasValue, TextUtil.DollarsToMicros(_options.GlobalDailyCloudLimit.Value), -1L)
            SyncLock _globalLock
                RollGlobalDay()
                If globalLimit >= 0 AndAlso _globalSpentToday + _globalReserved + micros > globalLimit Then Return ReserveResult.GlobalLimit
                _globalReserved += micros
            End SyncLock
            Dim ok = False
            Try
                Dim n = Await _db.ExecuteAsync(
                    "UPDATE AgentWallets SET ReservedMicros = ReservedMicros + @x, UpdatedUtc=@now WHERE AgentId=@a AND BalanceMicros - ReservedMicros >= @x " &
                    "AND (DailyLimitMicros IS NULL OR (CASE WHEN SpentTodayDate=@today THEN SpentTodayMicros ELSE 0 END) + ReservedMicros + @x <= DailyLimitMicros)",
                    ("@x", CObj(micros)), ("@now", Db.Ts(_clock.UtcNow)), ("@a", agentId), ("@today", Today())).ConfigureAwait(False)
                ok = n > 0
            Finally
                If Not ok Then
                    SyncLock _globalLock
                        _globalReserved = Math.Max(0, _globalReserved - micros)
                    End SyncLock
                End If
            End Try
            If ok Then Return ReserveResult.Ok
            Dim w = Await GetAsync(agentId).ConfigureAwait(False)
            If w Is Nothing Then Return ReserveResult.NoWallet
            If w.DailyLimitMicros.HasValue AndAlso w.SpentTodayMicros + w.ReservedMicros + micros > w.DailyLimitMicros.Value Then Return ReserveResult.DailyLimit
            Notify(Sub(o) o.OnReserveRefused(agentId, ReserveResult.InsufficientFunds))
            RaiseBalance(agentId, w.BalanceMicros)
            Return ReserveResult.InsufficientFunds
        End Function

        Public Async Function ReleaseAsync(agentId As String, reservedMicros As Long) As Task
            If reservedMicros <= 0 Then Return
            SyncLock _globalLock
                _globalReserved = Math.Max(0, _globalReserved - reservedMicros)
            End SyncLock
            Await _db.ExecuteAsync("UPDATE AgentWallets SET ReservedMicros = MAX(0, ReservedMicros - @x) WHERE AgentId=@a", ("@x", CObj(reservedMicros)), ("@a", agentId)).ConfigureAwait(False)
        End Function

        ''' <summary>Converts a reservation into an actual charge (ledger entry), releasing any unused part.</summary>
        Public Async Function SettleAsync(agentId As String, reservedMicros As Long, actualMicros As Long, details As ChargeDetails) As Task(Of Long)
            actualMicros = Math.Max(0, actualMicros)
            SyncLock _globalLock
                RollGlobalDay()
                _globalReserved = Math.Max(0, _globalReserved - Math.Max(0, reservedMicros))
                _globalSpentToday += actualMicros
            End SyncLock
            Dim now = _clock.UtcNow
            Dim today = Me.Today()
            Dim balanceAfter = Await _db.InTransactionAsync(Of Long)(
                Async Function(conn, tx)
                    Await Database.TxExecAsync(conn, tx,
                        "UPDATE AgentWallets SET BalanceMicros = BalanceMicros - @act, ReservedMicros = MAX(0, ReservedMicros - @res), " &
                        "LifetimeSpendMicros = LifetimeSpendMicros + @act, SpentTodayMicros = CASE WHEN SpentTodayDate=@today THEN SpentTodayMicros + @act ELSE @act END, " &
                        "SpentTodayDate=@today, UpdatedUtc=@now WHERE AgentId=@a",
                        ("@act", CObj(actualMicros)), ("@res", Math.Max(0, reservedMicros)), ("@today", today), ("@now", Db.Ts(now)), ("@a", agentId)).ConfigureAwait(False)
                    Dim bal = Convert.ToInt64(If(Await Database.TxScalarAsync(conn, tx, "SELECT BalanceMicros FROM AgentWallets WHERE AgentId=@a", ("@a", CObj(agentId))).ConfigureAwait(False), 0L))
                    If actualMicros > 0 Then
                        Await Database.TxExecAsync(conn, tx,
                            "INSERT INTO LedgerEntries(AgentId, EntryType, AmountMicros, BalanceAfterMicros, ModelKey, AiCallId, TaskId, CorrelationId, Description, CreatedBy, CreatedUtc, SpentByAgentId) " &
                            "VALUES (@a, @t, @amt, @bal, @m, @call, @task, @corr, @d, 'host', @now, @sb)",
                            ("@a", CObj(agentId)), ("@t", details.EntryType), ("@amt", -actualMicros), ("@bal", bal), ("@m", details.ModelKey),
                            ("@call", details.AiCallId), ("@task", details.TaskId), ("@corr", details.CorrelationId),
                            ("@d", TextUtil.Truncate(details.Description, 500)), ("@now", Db.Ts(now)), ("@sb", details.SpentByAgentId)).ConfigureAwait(False)
                    End If
                    Return bal
                End Function).ConfigureAwait(False)
            If actualMicros > 0 Then
                _events.Emit(AgentEventType.LedgerEntry, $"Charged {TextUtil.FormatMicros(actualMicros)} for {details.ModelKey}" &
                             If(details.SpentByAgentId Is Nothing, "", $" (used by subagent {details.SpentByAgentId})"), agentId, details.TaskId,
                             correlationId:=details.CorrelationId,
                             data:=New Dictionary(Of String, Object) From {{"amountMicros", -actualMicros}, {"balanceMicros", balanceAfter}, {"model", details.ModelKey},
                                                                         {"spentBy", details.SpentByAgentId}})
                Notify(Sub(o) o.OnCharged(agentId, actualMicros, balanceAfter))
                RaiseBalance(agentId, balanceAfter)
            End If
            Return balanceAfter
        End Function

        ''' <summary>
        ''' One-time profit credit recorded by an admin (the agent generated this much profit).
        ''' Idempotent: the same idempotency key never credits twice.
        ''' </summary>
        Public Async Function CreditProfitAsync(agentId As String, micros As Long, description As String, reference As String, idempotencyKey As String, actor As String,
                                                Optional taskId As String = Nothing) As Task(Of LedgerResult)
            If micros <= 0 Then Throw New ArgumentException("Profit amount must be positive.")
            Dim r = Await ApplyCreditAsync(agentId, micros, LedgerEntryType.ProfitCredit, description, reference, idempotencyKey, actor, taskId).ConfigureAwait(False)
            If r.Applied Then Notify(Sub(o) o.OnProfitCredited(agentId, micros, r.BalanceAfterMicros, taskId))
            Return r
        End Function

        ''' <summary>Admin balance adjustment (positive or negative).</summary>
        Public Function AdjustAsync(agentId As String, micros As Long, description As String, idempotencyKey As String, actor As String) As Task(Of LedgerResult)
            If micros = 0 Then Throw New ArgumentException("Adjustment must not be zero.")
            Return ApplyCreditAsync(agentId, micros, LedgerEntryType.AdminAdjustment, description, Nothing, idempotencyKey, actor)
        End Function

        ''' <summary>
        ''' Moves money from one agent's wallet to another's in one transaction, idempotent by key: a hire's starting budget, a
        ''' bounty for accepted work (counted as the receiver's profit). The payer must have the amount available (balance minus
        ''' reservations); otherwise nothing moves and FailureReason says why.
        ''' </summary>
        Public Async Function TransferAsync(fromAgentId As String, toAgentId As String, micros As Long, entryType As LedgerEntryType, description As String,
                                            idempotencyKey As String, actor As String, Optional taskId As String = Nothing) As Task(Of LedgerResult)
            If micros <= 0 Then Throw New ArgumentException("Transfer amount must be positive.")
            If String.Equals(fromAgentId, toAgentId, StringComparison.OrdinalIgnoreCase) Then Throw New ArgumentException("A wallet cannot pay itself.")
            If String.IsNullOrWhiteSpace(idempotencyKey) Then idempotencyKey = Ids.NewId("xfer")
            Dim key = entryType.ToString() & ":" & idempotencyKey
            Dim now = Db.Ts(_clock.UtcNow)
            Dim fromAfter As Long = 0
            Dim result = Await _db.InTransactionAsync(Of LedgerResult)(
                Async Function(conn, tx)
                    Dim existing = Await Database.TxQueryAsync(conn, tx, "SELECT Id, BalanceAfterMicros FROM LedgerEntries WHERE IdempotencyKey=@k",
                                                               Function(r) (Db.Lng(r, "Id"), Db.Lng(r, "BalanceAfterMicros")), ("@k", CObj(key))).ConfigureAwait(False)
                    If existing.Count > 0 Then
                        Return New LedgerResult With {.Applied = False, .Duplicate = True, .EntryId = existing(0).Item1, .BalanceAfterMicros = existing(0).Item2}
                    End If
                    Dim taken = Await Database.TxExecAsync(conn, tx,
                        "UPDATE AgentWallets SET BalanceMicros = BalanceMicros - @x, UpdatedUtc=@now WHERE AgentId=@f AND BalanceMicros - ReservedMicros >= @x",
                        ("@x", CObj(micros)), ("@now", now), ("@f", fromAgentId)).ConfigureAwait(False)
                    If taken = 0 Then
                        Dim has = Await Database.TxScalarAsync(conn, tx, "SELECT BalanceMicros - ReservedMicros FROM AgentWallets WHERE AgentId=@f", ("@f", CObj(fromAgentId))).ConfigureAwait(False)
                        Return New LedgerResult With {.Applied = False,
                            .FailureReason = If(has Is Nothing OrElse TypeOf has Is DBNull, $"{fromAgentId} has no wallet.",
                                                $"{fromAgentId} has only {TextUtil.FormatMicros(Convert.ToInt64(has))} available, not {TextUtil.FormatMicros(micros)}.")}
                    End If
                    Dim profit = If(entryType = LedgerEntryType.Bounty, micros, 0L)
                    Dim given = Await Database.TxExecAsync(conn, tx,
                        "UPDATE AgentWallets SET BalanceMicros = BalanceMicros + @x, LifetimeProfitMicros = LifetimeProfitMicros + @p, " &
                        "LifetimeCreditMicros = LifetimeCreditMicros + @x, UpdatedUtc=@now WHERE AgentId=@t",
                        ("@x", CObj(micros)), ("@p", profit), ("@now", now), ("@t", toAgentId)).ConfigureAwait(False)
                    If given = 0 Then Throw New KeyNotFoundException($"No wallet for agent '{toAgentId}'.")
                    fromAfter = Convert.ToInt64(Await Database.TxScalarAsync(conn, tx, "SELECT BalanceMicros FROM AgentWallets WHERE AgentId=@a", ("@a", CObj(fromAgentId))).ConfigureAwait(False))
                    Dim toAfter = Convert.ToInt64(Await Database.TxScalarAsync(conn, tx, "SELECT BalanceMicros FROM AgentWallets WHERE AgentId=@a", ("@a", CObj(toAgentId))).ConfigureAwait(False))
                    Dim insert = "INSERT INTO LedgerEntries(AgentId, EntryType, AmountMicros, BalanceAfterMicros, Description, Reference, IdempotencyKey, CreatedBy, CreatedUtc, TaskId) " &
                                 "VALUES (@a, @t, @x, @bal, @d, @r, @k, @by, @now, @task)"
                    Await Database.TxExecAsync(conn, tx, insert, ("@a", CObj(fromAgentId)), ("@t", entryType), ("@x", -micros), ("@bal", fromAfter),
                                               ("@d", TextUtil.Truncate(description, 500)), ("@r", "to:" & toAgentId), ("@k", key & ":out"), ("@by", actor), ("@now", now), ("@task", taskId)).ConfigureAwait(False)
                    Await Database.TxExecAsync(conn, tx, insert, ("@a", CObj(toAgentId)), ("@t", entryType), ("@x", micros), ("@bal", toAfter),
                                               ("@d", TextUtil.Truncate(description, 500)), ("@r", "from:" & fromAgentId), ("@k", key), ("@by", actor), ("@now", now), ("@task", taskId)).ConfigureAwait(False)
                    Dim id = Convert.ToInt64(Await Database.TxScalarAsync(conn, tx, "SELECT last_insert_rowid()").ConfigureAwait(False))
                    Return New LedgerResult With {.Applied = True, .BalanceAfterMicros = toAfter, .EntryId = id}
                End Function).ConfigureAwait(False)
            If result.Applied Then
                RaiseBalance(fromAgentId, fromAfter)
                RaiseBalance(toAgentId, result.BalanceAfterMicros)
                _events.Emit(AgentEventType.LedgerEntry, $"{entryType}: {TextUtil.FormatMicros(micros)} from {fromAgentId} to {toAgentId}", toAgentId, taskId,
                             data:=New Dictionary(Of String, Object) From {{"amountMicros", micros}, {"balanceMicros", result.BalanceAfterMicros}, {"type", entryType.ToString()},
                                                                         {"from", fromAgentId}, {"to", toAgentId}})
                If entryType = LedgerEntryType.Bounty Then Notify(Sub(o) o.OnProfitCredited(toAgentId, micros, result.BalanceAfterMicros, taskId))
            End If
            Return result
        End Function

        ''' <summary>Money credited to an agent with an explicit entry type (a bounty a person offered is profit). Idempotent by key.</summary>
        Public Async Function CreditAsync(agentId As String, micros As Long, entryType As LedgerEntryType, description As String, idempotencyKey As String, actor As String,
                                          Optional taskId As String = Nothing) As Task(Of LedgerResult)
            If micros <= 0 Then Throw New ArgumentException("Credit amount must be positive.")
            Dim r = Await ApplyCreditAsync(agentId, micros, entryType, description, Nothing, idempotencyKey, actor, taskId).ConfigureAwait(False)
            If r.Applied AndAlso entryType = LedgerEntryType.Bounty Then Notify(Sub(o) o.OnProfitCredited(agentId, micros, r.BalanceAfterMicros, taskId))
            Return r
        End Function

        Private Async Function ApplyCreditAsync(agentId As String, micros As Long, entryType As LedgerEntryType, description As String, reference As String, idempotencyKey As String, actor As String,
                                                Optional taskId As String = Nothing) As Task(Of LedgerResult)
            If String.IsNullOrWhiteSpace(idempotencyKey) Then idempotencyKey = Ids.NewId("ledger")
            Dim key = entryType.ToString() & ":" & idempotencyKey
            Dim now = Db.Ts(_clock.UtcNow)
            Dim result = Await _db.InTransactionAsync(Of LedgerResult)(
                Async Function(conn, tx)
                    Dim existing = Await Database.TxQueryAsync(conn, tx, "SELECT Id, BalanceAfterMicros FROM LedgerEntries WHERE IdempotencyKey=@k",
                                                               Function(r) (Db.Lng(r, "Id"), Db.Lng(r, "BalanceAfterMicros")), ("@k", CObj(key))).ConfigureAwait(False)
                    If existing.Count > 0 Then
                        Return New LedgerResult With {.Applied = False, .Duplicate = True, .EntryId = existing(0).Item1, .BalanceAfterMicros = existing(0).Item2}
                    End If
                    Dim profitDelta = If(entryType = LedgerEntryType.ProfitCredit OrElse entryType = LedgerEntryType.Bounty, micros, 0L)
                    Dim creditDelta = If(micros > 0, micros, 0L)
                    Dim n = Await Database.TxExecAsync(conn, tx,
                        "UPDATE AgentWallets SET BalanceMicros = BalanceMicros + @x, LifetimeProfitMicros = LifetimeProfitMicros + @p, " &
                        "LifetimeCreditMicros = LifetimeCreditMicros + @c, UpdatedUtc=@now WHERE AgentId=@a",
                        ("@x", CObj(micros)), ("@p", profitDelta), ("@c", creditDelta), ("@now", now), ("@a", agentId)).ConfigureAwait(False)
                    If n = 0 Then Throw New KeyNotFoundException($"No wallet for agent '{agentId}'.")
                    Dim bal = Convert.ToInt64(Await Database.TxScalarAsync(conn, tx, "SELECT BalanceMicros FROM AgentWallets WHERE AgentId=@a", ("@a", CObj(agentId))).ConfigureAwait(False))
                    Await Database.TxExecAsync(conn, tx,
                        "INSERT INTO LedgerEntries(AgentId, EntryType, AmountMicros, BalanceAfterMicros, Description, Reference, IdempotencyKey, CreatedBy, CreatedUtc, TaskId) " &
                        "VALUES (@a, @t, @x, @bal, @d, @r, @k, @by, @now, @task)",
                        ("@a", CObj(agentId)), ("@t", entryType), ("@x", micros), ("@bal", bal), ("@d", TextUtil.Truncate(description, 500)),
                        ("@r", TextUtil.Truncate(reference, 200)), ("@k", key), ("@by", actor), ("@now", now), ("@task", taskId)).ConfigureAwait(False)
                    Dim id = Convert.ToInt64(Await Database.TxScalarAsync(conn, tx, "SELECT last_insert_rowid()").ConfigureAwait(False))
                    Return New LedgerResult With {.Applied = True, .BalanceAfterMicros = bal, .EntryId = id}
                End Function).ConfigureAwait(False)
            If result.Applied Then
                RaiseBalance(agentId, result.BalanceAfterMicros)
                _events.Emit(AgentEventType.LedgerEntry, $"{entryType}: {TextUtil.FormatMicros(micros)} by {actor}", agentId,
                             data:=New Dictionary(Of String, Object) From {{"amountMicros", micros}, {"balanceMicros", result.BalanceAfterMicros}, {"type", entryType.ToString()}})
            End If
            Return result
        End Function

        Public Function SetDailyLimitAsync(agentId As String, micros As Long?) As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE AgentWallets SET DailyLimitMicros=@d WHERE AgentId=@a", ("@d", If(micros.HasValue, CObj(micros.Value), Nothing)), ("@a", agentId))
        End Function

        Public Function ListLedgerAsync(agentId As String, limit As Integer, Optional entryType As String = Nothing) As Task(Of List(Of LedgerEntry))
            Dim where As New List(Of String)
            Dim ps As New List(Of (String, Object))
            If Not String.IsNullOrEmpty(agentId) Then where.Add("AgentId=@a") : ps.Add(("@a", agentId))
            If Not String.IsNullOrEmpty(entryType) Then where.Add("EntryType=@t") : ps.Add(("@t", entryType))
            ps.Add(("@l", Math.Clamp(limit, 1, 1000)))
            Return _db.QueryAsync("SELECT * FROM LedgerEntries" & If(where.Count > 0, " WHERE " & String.Join(" AND ", where), "") & " ORDER BY Id DESC LIMIT @l",
                                  Function(r) New LedgerEntry With {
                                      .Id = Db.Lng(r, "Id"), .AgentId = Db.Str(r, "AgentId"), .EntryType = Db.EnumOf(r, "EntryType", LedgerEntryType.AiCharge),
                                      .AmountMicros = Db.Lng(r, "AmountMicros"), .BalanceAfterMicros = Db.Lng(r, "BalanceAfterMicros"), .ModelKey = Db.Str(r, "ModelKey"),
                                      .AiCallId = Db.Str(r, "AiCallId"), .TaskId = Db.Str(r, "TaskId"), .CorrelationId = Db.Str(r, "CorrelationId"),
                                      .Description = Db.Str(r, "Description"), .Reference = Db.Str(r, "Reference"), .CreatedBy = Db.Str(r, "CreatedBy"),
                                      .CreatedUtc = Db.Dt(r, "CreatedUtc"), .SpentByAgentId = Db.Str(r, "SpentByAgentId")}, ps.ToArray())
        End Function

        ''' <summary>Total cloud spend a subagent charged to its parent's wallet (authoritative, from the ledger).</summary>
        Public Function SpentByAsync(spenderAgentId As String) As Task(Of Long)
            Return _db.ScalarLongAsync("SELECT COALESCE(SUM(-AmountMicros),0) FROM LedgerEntries WHERE SpentByAgentId=@s AND EntryType IN ('AiCharge','ImageCharge')",
                                       ("@s", CObj(spenderAgentId)))
        End Function

        ''' <summary>Per-wallet spend incurred by subagents since a time.</summary>
        Public Function SubagentSpendAsync(sinceUtc As DateTime) As Task(Of List(Of Dictionary(Of String, Object)))
            Return _db.QueryAsync("SELECT AgentId, COUNT(DISTINCT SpentByAgentId) AS N, SUM(-AmountMicros) AS Spend FROM LedgerEntries " &
                                  "WHERE SpentByAgentId IS NOT NULL AND EntryType IN ('AiCharge','ImageCharge') AND CreatedUtc>=@s GROUP BY AgentId",
                                  Function(r) New Dictionary(Of String, Object) From {{"agentId", Db.Str(r, "AgentId")}, {"subagents", Db.Lng(r, "N")}, {"spendMicros", Db.Lng(r, "Spend")}},
                                  ("@s", CObj(Db.Ts(sinceUtc))))
        End Function

        Public Function CostByCorrelationAsync(correlationId As String) As Task(Of Long)
            Return _db.ScalarLongAsync("SELECT COALESCE(SUM(-AmountMicros),0) FROM LedgerEntries WHERE CorrelationId=@c AND EntryType IN ('AiCharge','ImageCharge')", ("@c", CObj(correlationId)))
        End Function

        Public Function SpendByModelAsync(sinceUtc As DateTime, Optional agentId As String = Nothing) As Task(Of List(Of Dictionary(Of String, Object)))
            Dim where = "WHERE EntryType IN ('AiCharge','ImageCharge') AND CreatedUtc>=@s" & If(String.IsNullOrEmpty(agentId), "", " AND AgentId=@a")
            Return _db.QueryAsync($"SELECT ModelKey, COUNT(*) AS N, SUM(-AmountMicros) AS Spend FROM LedgerEntries {where} GROUP BY ModelKey ORDER BY Spend DESC",
                                  Function(r) New Dictionary(Of String, Object) From {{"modelKey", Db.Str(r, "ModelKey")}, {"charges", Db.Lng(r, "N")}, {"spendMicros", Db.Lng(r, "Spend")}},
                                  ("@s", CObj(Db.Ts(sinceUtc))), ("@a", agentId))
        End Function
    End Class

    Public Enum ReserveResult
        Ok
        InsufficientFunds
        DailyLimit
        GlobalLimit
        NoWallet
    End Enum

End Namespace
