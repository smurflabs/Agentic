Option Strict On
Option Explicit On
Option Infer On

Imports System.Globalization
Imports System.IO
Imports System.Reflection
Imports System.Threading
Imports Microsoft.Data.Sqlite

Namespace Persistence

    ''' <summary>
    ''' SQLite access. One short-lived pooled connection per operation (WAL mode, busy timeout, FKs on).
    ''' A global semaphore bounds concurrent DB operations (Limits.MaxDatabaseOperations).
    ''' No application lock is ever held across a database call.
    ''' </summary>
    Public NotInheritable Class Database
        Implements IDisposable

        Private ReadOnly _connectionString As String
        Private ReadOnly _limiter As SemaphoreSlim
        Private _disposed As Boolean

        Public ReadOnly Property FilePath As String

        Public Sub New(filePath As String, maxConcurrentOperations As Integer)
            Me.FilePath = filePath
            Dim dir = IO.Path.GetDirectoryName(filePath)
            If Not String.IsNullOrEmpty(dir) Then Directory.CreateDirectory(dir)
            Dim csb As New SqliteConnectionStringBuilder With {
                .DataSource = filePath,
                .Mode = SqliteOpenMode.ReadWriteCreate,
                .Cache = SqliteCacheMode.Private,
                .Pooling = True,
                .DefaultTimeout = 30
            }
            _connectionString = csb.ToString()
            _limiter = New SemaphoreSlim(Math.Max(1, maxConcurrentOperations))
        End Sub

        ''' <summary>
        ''' DEPRECATED: Creates a raw connection without pragmas. Use OpenAsync() instead to ensure
        ''' foreign_keys=ON, busy_timeout, and synchronous mode are applied. Only use this when
        ''' you will manually apply the same pragmas.
        ''' </summary>
        <Obsolete("Use OpenAsync() to ensure per-connection pragmas are applied correctly.")>
        Public Function CreateConnection() As SqliteConnection
            Return New SqliteConnection(_connectionString)
        End Function

        ''' <summary>Opens a connection with per-connection pragmas applied.</summary>
        Public Async Function OpenAsync() As Task(Of SqliteConnection)
            Dim conn = New SqliteConnection(_connectionString)
            Await conn.OpenAsync().ConfigureAwait(False)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "PRAGMA busy_timeout=10000; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL;"
                Await cmd.ExecuteNonQueryAsync().ConfigureAwait(False)
            End Using
            Return conn
        End Function

        Private Async Function AcquireAsync() As Task
            If _disposed Then Throw New ObjectDisposedException(NameOf(Database))
            Await _limiter.WaitAsync().ConfigureAwait(False)
        End Function

        Public Shared Sub AddParams(cmd As SqliteCommand, ps As (String, Object)())
            If ps Is Nothing Then Return
            For Each p In ps
                cmd.Parameters.AddWithValue(p.Item1, Db.Val(p.Item2))
            Next
        End Sub

        Public Async Function ExecuteAsync(sql As String, ParamArray ps As (String, Object)()) As Task(Of Integer)
            Await AcquireAsync().ConfigureAwait(False)
            Try
                Return Await WithBusyRetryAsync(
                    Async Function()
                        Using conn = Await OpenAsync().ConfigureAwait(False)
                            Using cmd = conn.CreateCommand()
                                cmd.CommandText = sql
                                AddParams(cmd, ps)
                                Return Await cmd.ExecuteNonQueryAsync().ConfigureAwait(False)
                            End Using
                        End Using
                    End Function).ConfigureAwait(False)
            Finally
                _limiter.Release()
            End Try
        End Function

        Public Async Function ScalarAsync(sql As String, ParamArray ps As (String, Object)()) As Task(Of Object)
            Await AcquireAsync().ConfigureAwait(False)
            Try
                Return Await WithBusyRetryAsync(
                    Async Function()
                        Using conn = Await OpenAsync().ConfigureAwait(False)
                            Using cmd = conn.CreateCommand()
                                cmd.CommandText = sql
                                AddParams(cmd, ps)
                                Dim v = Await cmd.ExecuteScalarAsync().ConfigureAwait(False)
                                Return If(v Is DBNull.Value, Nothing, v)
                            End Using
                        End Using
                    End Function).ConfigureAwait(False)
            Finally
                _limiter.Release()
            End Try
        End Function

        Public Async Function ScalarLongAsync(sql As String, ParamArray ps As (String, Object)()) As Task(Of Long)
            Dim v = Await ScalarAsync(sql, ps).ConfigureAwait(False)
            If v Is Nothing Then Return 0
            Return Convert.ToInt64(v, CultureInfo.InvariantCulture)
        End Function

        Public Async Function QueryAsync(Of T)(sql As String, map As Func(Of SqliteDataReader, T), ParamArray ps As (String, Object)()) As Task(Of List(Of T))
            Await AcquireAsync().ConfigureAwait(False)
            Try
                Return Await WithBusyRetryAsync(
                    Async Function()
                        Dim result As New List(Of T)
                        Using conn = Await OpenAsync().ConfigureAwait(False)
                            Using cmd = conn.CreateCommand()
                                cmd.CommandText = sql
                                AddParams(cmd, ps)
                                Using rdr = Await cmd.ExecuteReaderAsync().ConfigureAwait(False)
                                    While Await rdr.ReadAsync().ConfigureAwait(False)
                                        result.Add(map(rdr))
                                    End While
                                End Using
                            End Using
                        End Using
                        Return result
                    End Function).ConfigureAwait(False)
            Finally
                _limiter.Release()
            End Try
        End Function

        Public Async Function QuerySingleAsync(Of T As Class)(sql As String, map As Func(Of SqliteDataReader, T), ParamArray ps As (String, Object)()) As Task(Of T)
            Dim list = Await QueryAsync(sql, map, ps).ConfigureAwait(False)
            Return list.FirstOrDefault()
        End Function

        ''' <summary>Runs work inside an IMMEDIATE transaction; commits on success, rolls back on exception.</summary>
        Public Async Function InTransactionAsync(Of T)(work As Func(Of SqliteConnection, SqliteTransaction, Task(Of T))) As Task(Of T)
            Await AcquireAsync().ConfigureAwait(False)
            Try
                Return Await WithBusyRetryAsync(
                    Async Function()
                        Using conn = Await OpenAsync().ConfigureAwait(False)
                            Using tx = conn.BeginTransaction(deferred:=False)
                                Dim failure As Exception = Nothing
                                Dim value As T = Nothing
                                Try
                                    value = Await work(conn, tx).ConfigureAwait(False)
                                    tx.Commit()
                                Catch ex As Exception
                                    failure = ex
                                End Try
                                If failure IsNot Nothing Then
                                    Try
                                        tx.Rollback()
                                    Catch
                                    End Try
                                    Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw()
                                End If
                                Return value
                            End Using
                        End Using
                    End Function).ConfigureAwait(False)
            Finally
                _limiter.Release()
            End Try
        End Function

        Private Shared Async Function WithBusyRetryAsync(Of T)(op As Func(Of Task(Of T))) As Task(Of T)
            Dim attempt = 0
            While True
                Dim busy As SqliteException = Nothing
                Try
                    Return Await op().ConfigureAwait(False)
                Catch ex As SqliteException When (ex.SqliteErrorCode = 5 OrElse ex.SqliteErrorCode = 6) AndAlso attempt < 4
                    busy = ex
                End Try
                attempt += 1
                Await Task.Delay(50 * attempt * attempt).ConfigureAwait(False)
            End While
            Throw New InvalidOperationException("unreachable")
        End Function

        ' ---------------- Helpers for use inside transactions ----------------

        Public Shared Async Function TxExecAsync(conn As SqliteConnection, tx As SqliteTransaction, sql As String, ParamArray ps As (String, Object)()) As Task(Of Integer)
            Using cmd = conn.CreateCommand()
                cmd.Transaction = tx
                cmd.CommandText = sql
                AddParams(cmd, ps)
                Return Await cmd.ExecuteNonQueryAsync().ConfigureAwait(False)
            End Using
        End Function

        Public Shared Async Function TxScalarAsync(conn As SqliteConnection, tx As SqliteTransaction, sql As String, ParamArray ps As (String, Object)()) As Task(Of Object)
            Using cmd = conn.CreateCommand()
                cmd.Transaction = tx
                cmd.CommandText = sql
                AddParams(cmd, ps)
                Dim v = Await cmd.ExecuteScalarAsync().ConfigureAwait(False)
                Return If(v Is DBNull.Value, Nothing, v)
            End Using
        End Function

        Public Shared Async Function TxQueryAsync(Of T)(conn As SqliteConnection, tx As SqliteTransaction, sql As String, map As Func(Of SqliteDataReader, T), ParamArray ps As (String, Object)()) As Task(Of List(Of T))
            Dim result As New List(Of T)
            Using cmd = conn.CreateCommand()
                cmd.Transaction = tx
                cmd.CommandText = sql
                AddParams(cmd, ps)
                Using rdr = Await cmd.ExecuteReaderAsync().ConfigureAwait(False)
                    While Await rdr.ReadAsync().ConfigureAwait(False)
                        result.Add(map(rdr))
                    End While
                End Using
            End Using
            Return result
        End Function

        ''' <summary>Online backup to a file (consistent snapshot while the host keeps running).</summary>
        Public Async Function BackupToAsync(destinationFile As String) As Task
            Await AcquireAsync().ConfigureAwait(False)
            Try
                Using src = Await OpenAsync().ConfigureAwait(False)
                    Using dst = New SqliteConnection(New SqliteConnectionStringBuilder With {.DataSource = destinationFile, .Pooling = False}.ToString())
                        dst.Open()
                        src.BackupDatabase(dst)
                    End Using
                End Using
            Finally
                _limiter.Release()
            End Try
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then Return
            _disposed = True
            ' Release only this database's pooled connections (other databases in the process keep theirs).
            Using c = CreateConnection()
                SqliteConnection.ClearPool(c)
            End Using
        End Sub
    End Class

    ''' <summary>Value conversion helpers between .NET values and SQLite storage.</summary>
    Public Module Db
        Public Const TimestampFormat As String = "yyyy-MM-ddTHH:mm:ss.fffZ"

        Public Function Ts(value As DateTime) As String
            Return value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture)
        End Function

        Public Function Ts(value As DateTime?) As Object
            If Not value.HasValue Then Return DBNull.Value
            Return Ts(value.Value)
        End Function

        Public Function Val(value As Object) As Object
            If value Is Nothing Then Return DBNull.Value
            If TypeOf value Is DateTime Then Return Ts(DirectCast(value, DateTime))
            If TypeOf value Is Boolean Then Return If(DirectCast(value, Boolean), 1L, 0L)
            If TypeOf value Is Decimal Then Return DirectCast(value, Decimal).ToString(CultureInfo.InvariantCulture)
            If TypeOf value Is PriorityLevel Then Return CLng(DirectCast(value, PriorityLevel))
            If value.GetType().IsEnum Then Return value.ToString()
            Return value
        End Function

        Public Function Str(r As SqliteDataReader, col As String) As String
            Dim i = r.GetOrdinal(col)
            If r.IsDBNull(i) Then Return Nothing
            Return r.GetString(i)
        End Function

        Public Function Int(r As SqliteDataReader, col As String) As Integer
            Dim i = r.GetOrdinal(col)
            If r.IsDBNull(i) Then Return 0
            Return CInt(r.GetInt64(i))
        End Function

        Public Function IntN(r As SqliteDataReader, col As String) As Integer?
            Dim i = r.GetOrdinal(col)
            If r.IsDBNull(i) Then Return Nothing
            Return CInt(r.GetInt64(i))
        End Function

        Public Function Lng(r As SqliteDataReader, col As String) As Long
            Dim i = r.GetOrdinal(col)
            If r.IsDBNull(i) Then Return 0
            Return r.GetInt64(i)
        End Function

        Public Function LngN(r As SqliteDataReader, col As String) As Long?
            Dim i = r.GetOrdinal(col)
            If r.IsDBNull(i) Then Return Nothing
            Return r.GetInt64(i)
        End Function

        Public Function Bool(r As SqliteDataReader, col As String) As Boolean
            Return Lng(r, col) <> 0
        End Function

        Public Function BoolN(r As SqliteDataReader, col As String) As Boolean?
            Dim v = LngN(r, col)
            If Not v.HasValue Then Return Nothing
            Return v.Value <> 0
        End Function

        Public Function Dt(r As SqliteDataReader, col As String) As DateTime
            Dim v = DtN(r, col)
            Return If(v.HasValue, v.Value, DateTime.MinValue)
        End Function

        Public Function DtN(r As SqliteDataReader, col As String) As DateTime?
            Dim s = Str(r, col)
            If String.IsNullOrEmpty(s) Then Return Nothing
            Return ParseTs(s)
        End Function

        Public Function ParseTs(s As String) As DateTime
            Return DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal Or DateTimeStyles.AssumeUniversal)
        End Function

        Public Function Dec(r As SqliteDataReader, col As String) As Decimal?
            Dim s = Str(r, col)
            If String.IsNullOrEmpty(s) Then Return Nothing
            Return Decimal.Parse(s, NumberStyles.Number, CultureInfo.InvariantCulture)
        End Function

        Public Function DblN(r As SqliteDataReader, col As String) As Double?
            Dim i = r.GetOrdinal(col)
            If r.IsDBNull(i) Then Return Nothing
            Return r.GetDouble(i)
        End Function

        Public Function EnumOf(Of T As Structure)(r As SqliteDataReader, col As String, defaultValue As T) As T
            Dim s = Str(r, col)
            Dim v As T = defaultValue
            If s IsNot Nothing AndAlso [Enum].TryParse(s, True, v) Then Return v
            Return defaultValue
        End Function

        Public Function HasColumn(r As SqliteDataReader, col As String) As Boolean
            For i = 0 To r.FieldCount - 1
                If String.Equals(r.GetName(i), col, StringComparison.OrdinalIgnoreCase) Then Return True
            Next
            Return False
        End Function
    End Module

    ''' <summary>Applies embedded SQL migrations in order, backing up the database file first.</summary>
    Public NotInheritable Class SchemaMigrator
        Private ReadOnly _db As Database

        Public Sub New(db As Database)
            _db = db
        End Sub

        Public Async Function MigrateAsync() As Task(Of Integer)
            Dim isNew = Not File.Exists(_db.FilePath) OrElse New FileInfo(_db.FilePath).Length = 0
            Using conn = Await _db.OpenAsync().ConfigureAwait(False)
                Using cmd = conn.CreateCommand()
                    If isNew Then
                        cmd.CommandText = "PRAGMA auto_vacuum=INCREMENTAL;"
                        Await cmd.ExecuteNonQueryAsync().ConfigureAwait(False)
                    End If
                    cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS SchemaVersion (Version INTEGER PRIMARY KEY, Name TEXT NOT NULL, AppliedUtc TEXT NOT NULL);"
                    Await cmd.ExecuteNonQueryAsync().ConfigureAwait(False)
                End Using
            End Using

            Dim applied = New HashSet(Of Integer)(Await _db.QueryAsync("SELECT Version FROM SchemaVersion", Function(r) CInt(r.GetInt64(0))).ConfigureAwait(False))
            Dim asm = GetType(SchemaMigrator).Assembly
            Dim scripts = asm.GetManifestResourceNames().
                Where(Function(n) n.StartsWith("Migrations/", StringComparison.Ordinal) AndAlso n.EndsWith(".sql", StringComparison.Ordinal)).
                OrderBy(Function(n) n, StringComparer.Ordinal).ToList()

            Dim count = 0
            Dim backedUp = False
            For Each name In scripts
                Dim fileName = name.Substring("Migrations/".Length)
                Dim version = Integer.Parse(fileName.Split("_"c)(0), CultureInfo.InvariantCulture)
                If applied.Contains(version) Then Continue For
                If Not isNew AndAlso Not backedUp Then
                    Dim backupDir = Path.Combine(Path.GetDirectoryName(_db.FilePath), "backups")
                    Directory.CreateDirectory(backupDir)
                    Await _db.BackupToAsync(Path.Combine(backupDir, $"pre-migration-{version:0000}-{DateTime.UtcNow:yyyyMMddHHmmss}.db")).ConfigureAwait(False)
                    backedUp = True
                End If
                Dim sql As String
                Using s = asm.GetManifestResourceStream(name)
                    Using rdr As New StreamReader(s)
                        sql = rdr.ReadToEnd()
                    End Using
                End Using
                Await _db.InTransactionAsync(Of Boolean)(
                    Async Function(conn, tx)
                        Await Database.TxExecAsync(conn, tx, sql).ConfigureAwait(False)
                        Await Database.TxExecAsync(conn, tx, "INSERT INTO SchemaVersion(Version, Name, AppliedUtc) VALUES (@v, @n, @t)",
                                                   ("@v", version), ("@n", fileName), ("@t", Db.Ts(DateTime.UtcNow))).ConfigureAwait(False)
                        Return True
                    End Function).ConfigureAwait(False)
                count += 1
            Next
            Return count
        End Function
    End Class

End Namespace
