Option Strict On
Option Explicit On
Option Infer On

Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading

Namespace Utilities

    ''' <summary>Abstract clock so time-dependent logic (schedules, rate limits, retention) is testable.</summary>
    Public Interface IClock
        ReadOnly Property UtcNow As DateTime
    End Interface

    Public NotInheritable Class SystemClock
        Implements IClock

        Public Shared ReadOnly Instance As New SystemClock()

        Public ReadOnly Property UtcNow As DateTime Implements IClock.UtcNow
            Get
                Return DateTime.UtcNow
            End Get
        End Property
    End Class

    ''' <summary>Identifier generation. IDs are prefixed, roughly time-sortable and URL-safe.</summary>
    Public Module Ids
        Public Function NewId(prefix As String) As String
            Return prefix & "_" & DateTime.UtcNow.ToString("yyMMddHHmmss") & Guid.NewGuid().ToString("N").Substring(0, 12)
        End Function

        Public Function NewToken(Optional byteCount As Integer = 32) As String
            Dim bytes = RandomNumberGenerator.GetBytes(byteCount)
            Return Convert.ToBase64String(bytes).TrimEnd("="c).Replace("+"c, "-"c).Replace("/"c, "_"c)
        End Function
    End Module

    Public Module TextUtil
        Public Function Truncate(value As String, maxChars As Integer, Optional marker As String = "…[truncated]") As String
            If value Is Nothing Then Return Nothing
            If maxChars <= 0 Then Return String.Empty
            If value.Length <= maxChars Then Return value
            Dim keep = Math.Max(0, maxChars - marker.Length)
            Return value.Substring(0, keep) & marker
        End Function

        Public Function Sha256Hex(value As String) As String
            Dim bytes = SHA256.HashData(Encoding.UTF8.GetBytes(If(value, String.Empty)))
            Return Convert.ToHexString(bytes).ToLowerInvariant()
        End Function

        Public Function Sha256Hex(bytes As Byte()) As String
            Return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
        End Function

        Public Function FixedTimeEquals(a As String, b As String) As Boolean
            If a Is Nothing OrElse b Is Nothing Then Return False
            Return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b))
        End Function

        Public Function Utf8Length(value As String) As Integer
            If value Is Nothing Then Return 0
            Return Encoding.UTF8.GetByteCount(value)
        End Function

        ''' <summary>Formats micro-USD as a dollar string, e.g. 1234567 → "$1.234567".</summary>
        Public Function FormatMicros(micros As Long) As String
            Dim dollars = CDec(micros) / 1000000D
            Return If(dollars < 0, "-$", "$") & Math.Abs(dollars).ToString("0.00####", Globalization.CultureInfo.InvariantCulture)
        End Function

        Public Function DollarsToMicros(dollars As Decimal) As Long
            Return CLng(Math.Round(dollars * 1000000D, MidpointRounding.AwayFromZero))
        End Function
    End Module

    ''' <summary>Exponential backoff with jitter.</summary>
    Public Module Backoff
        Private ReadOnly _rng As New Random()
        Private ReadOnly _rngLock As New Object()

        Public Function Compute(attempt As Integer, baseDelay As TimeSpan, maxDelay As TimeSpan, Optional exponential As Boolean = True) As TimeSpan
            Dim factor = If(exponential, Math.Pow(2, Math.Max(0, Math.Min(attempt, 20))), 1.0)
            Dim ms = Math.Min(baseDelay.TotalMilliseconds * factor, maxDelay.TotalMilliseconds)
            Dim jitter As Double
            SyncLock _rngLock
                jitter = 0.8 + _rng.NextDouble() * 0.4
            End SyncLock
            Return TimeSpan.FromMilliseconds(Math.Max(0, ms * jitter))
        End Function
    End Module

    ''' <summary>
    ''' Auto-reset async signal. Set() wakes one waiter (or the next waiter if none is waiting).
    ''' Used to wake agent loops when new work or messages arrive, without polling tightly.
    ''' </summary>
    Public NotInheritable Class AsyncSignal
        Private ReadOnly _sem As New SemaphoreSlim(0, 1)

        Public Sub [Set]()
            Try
                If _sem.CurrentCount = 0 Then _sem.Release()
            Catch ex As SemaphoreFullException
                ' already signalled
            End Try
        End Sub

        ''' <summary>Waits for the signal or the timeout. Returns True if signalled.</summary>
        Public Function WaitAsync(timeout As TimeSpan, ct As CancellationToken) As Task(Of Boolean)
            Return _sem.WaitAsync(timeout, ct)
        End Function
    End Class

    ''' <summary>Manual-reset async gate used for pause/resume.</summary>
    Public NotInheritable Class AsyncGate
        Private _tcs As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Private ReadOnly _lock As New Object()

        Public Sub New(initiallyOpen As Boolean)
            If initiallyOpen Then _tcs.TrySetResult(True)
        End Sub

        Public ReadOnly Property IsOpen As Boolean
            Get
                SyncLock _lock
                    Return _tcs.Task.IsCompleted
                End SyncLock
            End Get
        End Property

        Public Sub Open()
            SyncLock _lock
                _tcs.TrySetResult(True)
            End SyncLock
        End Sub

        Public Sub Close()
            SyncLock _lock
                If _tcs.Task.IsCompleted Then
                    _tcs = New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
                End If
            End SyncLock
        End Sub

        Public Function WaitAsync(ct As CancellationToken) As Task
            Dim t As Task(Of Boolean)
            SyncLock _lock
                t = _tcs.Task
            End SyncLock
            Return t.WaitAsync(ct)
        End Function
    End Class

    Public Module ExceptionUtil
        ''' <summary>True when the exception represents cancellation (directly or wrapped).</summary>
        Public Function IsCancellation(ex As Exception) As Boolean
            If ex Is Nothing Then Return False
            If TypeOf ex Is OperationCanceledException Then Return True
            Dim agg = TryCast(ex, AggregateException)
            If agg IsNot Nothing Then Return agg.InnerExceptions.All(Function(e) IsCancellation(e))
            Return False
        End Function
    End Module

End Namespace
