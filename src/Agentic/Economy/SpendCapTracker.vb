Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent

Namespace Economy

    ''' <summary>
    ''' Per-spender cloud spend caps (used for subagents, which spend from their parent's wallet). Reservations and
    ''' settlements mirror the wallet's reserve → settle cycle so a subagent can never commit more than its cap, even
    ''' with concurrent calls. Spenders without a registered cap are unlimited here (the wallet still applies).
    ''' </summary>
    Public NotInheritable Class SpendCapTracker
        Private NotInheritable Class Entry
            Public ReadOnly Lock As New Object()
            Public Cap As Long?
            Public Spent As Long
            Public Reserved As Long
        End Class

        Private ReadOnly _entries As New ConcurrentDictionary(Of String, Entry)(StringComparer.OrdinalIgnoreCase)

        Public Sub Register(spenderId As String, capMicros As Long?, alreadySpentMicros As Long)
            _entries(spenderId) = New Entry With {.Cap = capMicros, .Spent = Math.Max(0, alreadySpentMicros)}
        End Sub

        Public Sub Remove(spenderId As String)
            Dim e As Entry = Nothing
            If spenderId IsNot Nothing Then _entries.TryRemove(spenderId, e)
        End Sub

        Public Function IsTracked(spenderId As String) As Boolean
            Return spenderId IsNot Nothing AndAlso _entries.ContainsKey(spenderId)
        End Function

        ''' <summary>False if reserving would exceed the spender's cap.</summary>
        Public Function TryReserve(spenderId As String, micros As Long) As Boolean
            Dim e As Entry = Nothing
            If micros <= 0 OrElse spenderId Is Nothing OrElse Not _entries.TryGetValue(spenderId, e) Then Return True
            SyncLock e.Lock
                If e.Cap.HasValue AndAlso e.Spent + e.Reserved + micros > e.Cap.Value Then Return False
                e.Reserved += micros
            End SyncLock
            Return True
        End Function

        Public Sub Settle(spenderId As String, reservedMicros As Long, actualMicros As Long)
            Dim e As Entry = Nothing
            If spenderId Is Nothing OrElse Not _entries.TryGetValue(spenderId, e) Then Return
            SyncLock e.Lock
                e.Reserved = Math.Max(0, e.Reserved - Math.Max(0, reservedMicros))
                e.Spent += Math.Max(0, actualMicros)
            End SyncLock
        End Sub

        Public Sub Release(spenderId As String, reservedMicros As Long)
            Dim e As Entry = Nothing
            If spenderId Is Nothing OrElse Not _entries.TryGetValue(spenderId, e) Then Return
            SyncLock e.Lock
                e.Reserved = Math.Max(0, e.Reserved - Math.Max(0, reservedMicros))
            End SyncLock
        End Sub

        Public Function GetSpent(spenderId As String) As Long
            Dim e As Entry = Nothing
            If spenderId Is Nothing OrElse Not _entries.TryGetValue(spenderId, e) Then Return 0
            SyncLock e.Lock
                Return e.Spent
            End SyncLock
        End Function

        Public Function GetCap(spenderId As String) As Long?
            Dim e As Entry = Nothing
            If spenderId Is Nothing OrElse Not _entries.TryGetValue(spenderId, e) Then Return Nothing
            Return e.Cap
        End Function
    End Class

End Namespace
