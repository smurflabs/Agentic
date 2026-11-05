Option Strict On
Option Explicit On
Option Infer On

Imports System.ComponentModel
Imports System.Data
Imports System.Data.Common
Imports System.Globalization
Imports System.IO
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading
Imports ModelContextProtocol
Imports ModelContextProtocol.Server

Namespace Mcp

    ''' <summary>
    ''' Built-in "database" MCP server ("Command": "{self}", "Arguments": ["--mcp-database", "--roots", "{data}\shared;{data}\workspace"]):
    ''' SQL for agents on SQLite, PostgreSQL, MySQL/MariaDB and SQL Server. Databases are named in environment variables
    ''' DB_&lt;NAME&gt; = "&lt;provider&gt;:&lt;connection string&gt;" (provider: sqlite, postgres, mysql, sqlserver); DB_&lt;NAME&gt;_WRITABLE=true lets the
    ''' execute tool change that one. Queries are read-only twice over: only SELECT-like single statements pass, and they run in a
    ''' read-only (or rolled-back) transaction. SQLite files inside the allowed folders can be queried by path (read-only).
    ''' </summary>
    Public NotInheritable Class DatabaseServer
        Friend NotInheritable Class Connection
            Public Property Name As String
            Public Property Provider As String
            Public Property ConnectionString As String
            Public Property Writable As Boolean
        End Class

        Private ReadOnly _dbs As New Dictionary(Of String, Connection)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _roots As PathRoots
        Private ReadOnly _timeoutSeconds As Integer
        Private Shared ReadOnly Inv As CultureInfo = CultureInfo.InvariantCulture

        Friend Sub New(connections As IEnumerable(Of Connection), roots As PathRoots, Optional timeoutSeconds As Integer = 30)
            For Each c In connections
                _dbs(c.Name) = c
            Next
            _roots = roots
            _timeoutSeconds = Math.Clamp(timeoutSeconds, 5, 600)
        End Sub

        Public Shared Function RunStdio(args As String()) As Integer
            Dim roots = ToolKit.Arg(args, "--roots")
            Dim server As New DatabaseServer(FromEnvironment(), If(roots Is Nothing, Nothing, New PathRoots(ToolKit.SplitList(roots))), ToolKit.IntArg(args, "--timeout", 30))
            Return BuiltInServer.Run("agentic-database", server.CreateTools())
        End Function

        ''' <summary>DB_NAME=provider:connection string (+ DB_NAME_WRITABLE=true) from the environment.</summary>
        Friend Shared Function FromEnvironment() As List(Of Connection)
            Dim list As New List(Of Connection)
            For Each entry As DictionaryEntry In Environment.GetEnvironmentVariables()
                Dim key = CStr(entry.Key)
                If Not key.StartsWith("DB_", StringComparison.OrdinalIgnoreCase) OrElse key.EndsWith("_WRITABLE", StringComparison.OrdinalIgnoreCase) Then Continue For
                Dim c = Parse(key.Substring(3), CStr(entry.Value))
                If c Is Nothing Then Continue For
                c.Writable = String.Equals(Environment.GetEnvironmentVariable(key & "_WRITABLE"), "true", StringComparison.OrdinalIgnoreCase)
                list.Add(c)
            Next
            Return list
        End Function

        Friend Shared Function Parse(name As String, spec As String) As Connection
            Dim s = If(spec, "").Trim()
            Dim colon = s.IndexOf(":"c)
            If colon <= 0 OrElse String.IsNullOrWhiteSpace(name) Then Return Nothing
            Dim provider = NormalizeProvider(s.Substring(0, colon))
            If provider Is Nothing Then Return Nothing
            Return New Connection With {.Name = name.Trim().ToLowerInvariant(), .Provider = provider, .ConnectionString = s.Substring(colon + 1).Trim()}
        End Function

        Private Shared Function NormalizeProvider(p As String) As String
            Select Case p.Trim().ToLowerInvariant()
                Case "sqlite" : Return "sqlite"
                Case "postgres", "postgresql", "pg", "npgsql" : Return "postgres"
                Case "mysql", "mariadb" : Return "mysql"
                Case "sqlserver", "mssql", "sql server" : Return "sqlserver"
            End Select
            Return Nothing
        End Function

        Public Function CreateTools() As McpServerPrimitiveCollection(Of McpServerTool)
            Dim tools As New McpServerPrimitiveCollection(Of McpServerTool)()
            Dim dbArg = " database: a name from list_databases" & If(_roots IsNot Nothing, ", or the path of a SQLite file (.db/.sqlite) in " & _roots.Describe(), "") & "."
            ToolKit.Add(tools, New Func(Of String)(AddressOf ListDatabases), "list_databases", "The databases you can query: name, engine, server/database and whether execute may change it.")
            ToolKit.Add(tools, New Func(Of String, String, CancellationToken, Task(Of String))(AddressOf ListTablesAsync), "list_tables",
                "Tables and views of a database (optionally one schema), with row counts where cheap." & dbArg)
            ToolKit.Add(tools, New Func(Of String, String, CancellationToken, Task(Of String))(AddressOf DescribeTableAsync), "describe_table",
                "A table's columns (type, nullable, default, primary key), indexes and foreign keys. table may be schema.table." & dbArg)
            ToolKit.Add(tools, New Func(Of String, String, Integer, CancellationToken, Task(Of String))(AddressOf QueryAsync), "query",
                "Run one read-only SQL statement (SELECT, WITH, EXPLAIN, SHOW, DESCRIBE, VALUES, read-only PRAGMA) and get the rows as a table (max_rows, default 200). " &
                "It runs in a read-only transaction: it can never change data." & dbArg)
            ToolKit.Add(tools, New Func(Of String, String, CancellationToken, Task(Of String))(AddressOf ExplainAsync), "explain_query",
                "The query plan for a SELECT (EXPLAIN / EXPLAIN QUERY PLAN), to find missing indexes and slow joins." & dbArg)
            ToolKit.Add(tools, New Func(Of String, String, CancellationToken, Task(Of String))(AddressOf ExecuteAsync), "execute",
                "Run one statement that changes data or schema (INSERT, UPDATE, DELETE, CREATE, ALTER, ...) on a database marked writable, in a transaction; returns the rows affected." & dbArg,
                False, True)
            If _roots IsNot Nothing Then
                ToolKit.Add(tools, New Func(Of String, String, String, CancellationToken, Task(Of String))(AddressOf ExportAsync), "export_query",
                    $"Run a read-only query and save all its rows (up to 100 000) as .csv or .xlsx in {_roots.Describe()}." & dbArg, False)
            End If
            Return tools
        End Function

        ' ------------------------------------------------------------------------------------------ connections

        Private Function Find(database As String) As Connection
            Dim name = If(database, "").Trim()
            If name.Length = 0 Then
                If _dbs.Count = 1 Then Return _dbs.Values.First()
                Throw New McpException("database is required. " & ListDatabases())
            End If
            Dim c As Connection = Nothing
            If _dbs.TryGetValue(name, c) Then Return c
            If _roots IsNot Nothing AndAlso Regex.IsMatch(name, "\.(db|sqlite|sqlite3|db3)$", RegexOptions.IgnoreCase) Then
                Dim full = _roots.Resolve(name)
                If Not File.Exists(full) Then Throw New McpException($"SQLite file '{name}' does not exist.")
                Return New Connection With {.Name = name, .Provider = "sqlite", .ConnectionString = $"Data Source={full}", .Writable = False}
            End If
            Throw New McpException($"Unknown database '{name}'. " & ListDatabases())
        End Function

        Private Shared Function Open(c As Connection, readOnlyMode As Boolean) As DbConnection
            Select Case c.Provider
                Case "sqlite"
                    Dim b As New Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(c.ConnectionString)
                    If readOnlyMode OrElse Not c.Writable Then b.Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly
                    Return New Microsoft.Data.Sqlite.SqliteConnection(b.ToString())
                Case "postgres" : Return New Npgsql.NpgsqlConnection(c.ConnectionString)
                Case "mysql" : Return New MySqlConnector.MySqlConnection(c.ConnectionString)
                Case "sqlserver" : Return New Microsoft.Data.SqlClient.SqlConnection(c.ConnectionString)
            End Select
            Throw New McpException("Unknown database engine " & c.Provider)
        End Function

        ''' <summary>The connection string without passwords, for people to see.</summary>
        Friend Shared Function Describe(c As Connection) As String
            Dim parts = c.ConnectionString.Split(";"c).Select(Function(p) p.Trim()).Where(Function(p) p.Length > 0).
                Where(Function(p) Not Regex.IsMatch(p, "^(password|pwd|user id|uid|username|user|token|access token)\s*=", RegexOptions.IgnoreCase))
            Return String.Join("; ", parts)
        End Function

        Friend Function ListDatabases() As String
            If _dbs.Count = 0 Then
                Return "No databases are configured (an administrator adds DB_<NAME>=provider:connection string to this tool's environment)." &
                       If(_roots IsNot Nothing, $" SQLite files in {_roots.Describe()} can be queried by path.", "")
            End If
            Dim sb As New StringBuilder("Databases:" & vbLf)
            For Each c In _dbs.Values.OrderBy(Function(x) x.Name)
                sb.AppendLine($"- {c.Name} ({c.Provider}, {If(c.Writable, "writable with execute", "read-only")}): {Describe(c)}")
            Next
            If _roots IsNot Nothing Then sb.AppendLine($"SQLite files in {_roots.Describe()} can also be queried by path (read-only).")
            Return sb.ToString().TrimEnd()
        End Function

        ' ------------------------------------------------------------------------------------------ SQL checks

        ''' <summary>The SQL without comments and string literals (to classify it safely).</summary>
        Friend Shared Function StripLiterals(sql As String) As String
            Dim sb As New StringBuilder()
            Dim i = 0
            While i < sql.Length
                Dim c = sql(i)
                If c = "-"c AndAlso i + 1 < sql.Length AndAlso sql(i + 1) = "-"c Then
                    While i < sql.Length AndAlso sql(i) <> ControlChars.Lf
                        i += 1
                    End While
                    sb.Append(" "c)
                ElseIf c = "/"c AndAlso i + 1 < sql.Length AndAlso sql(i + 1) = "*"c Then
                    Dim e = sql.IndexOf("*/", i + 2, StringComparison.Ordinal)
                    i = If(e < 0, sql.Length, e + 2)
                    sb.Append(" "c)
                    Continue While
                ElseIf c = "'"c OrElse c = """"c OrElse c = "`"c OrElse c = "["c Then
                    Dim close = If(c = "["c, "]"c, c)
                    i += 1
                    While i < sql.Length
                        If sql(i) = close Then
                            If i + 1 < sql.Length AndAlso sql(i + 1) = close AndAlso close <> "]"c Then
                                i += 2
                                Continue While
                            End If
                            Exit While
                        End If
                        i += 1
                    End While
                    sb.Append(If(c = "'"c, "''", "x"))
                Else
                    sb.Append(c)
                End If
                i += 1
            End While
            Return sb.ToString()
        End Function

        ''' <summary>One statement (a trailing semicolon is fine); throws for two or more.</summary>
        Friend Shared Function SingleStatement(sql As String) As String
            Dim s = If(sql, "").Trim()
            If s.Length = 0 Then Throw New McpException("sql is required.")
            Dim bare = StripLiterals(s).Trim().TrimEnd(";"c).Trim()
            If bare.Contains(";"c) Then Throw New McpException("Run one statement at a time.")
            Return s.TrimEnd().TrimEnd(";"c)
        End Function

        Private Shared ReadOnly ReadVerbs As String() = {"SELECT", "WITH", "EXPLAIN", "SHOW", "DESCRIBE", "DESC", "VALUES", "PRAGMA", "TABLE"}
        Private Shared ReadOnly WriteWords As New Regex("\b(INSERT|UPDATE|DELETE|MERGE|UPSERT|CREATE|ALTER|DROP|GRANT|REVOKE|ATTACH|DETACH|VACUUM|REINDEX|COPY|CALL|EXEC|EXECUTE|INTO|LOCK|SET|LOAD|HANDLER|RENAME)\b",
                                                         RegexOptions.IgnoreCase Or RegexOptions.Compiled)

        ''' <summary>Whether a statement only reads: a reading verb first and no writing keyword anywhere (outside strings and comments).</summary>
        Friend Shared Function IsReadOnly(sql As String) As Boolean
            Dim bare = StripLiterals(sql).Trim()
            Dim first = Regex.Match(bare, "^\(*\s*([A-Za-z]+)").Groups(1).Value.ToUpperInvariant()
            If Not ReadVerbs.Contains(first) Then Return False
            If first = "PRAGMA" Then Return Not bare.Contains("="c)
            Return Not WriteWords.IsMatch(bare)
        End Function

        ' ------------------------------------------------------------------------------------------ running

        Private Shared Function Cell(v As Object) As String
            If v Is Nothing OrElse TypeOf v Is DBNull Then Return "NULL"
            If TypeOf v Is Byte() Then Return $"<{DirectCast(v, Byte()).Length:N0} bytes>"
            If TypeOf v Is DateTime Then
                Dim d = DirectCast(v, DateTime)
                Return If(d.TimeOfDay = TimeSpan.Zero, d.ToString("yyyy-MM-dd", Inv), d.ToString("yyyy-MM-dd HH:mm:ss.FFF", Inv))
            End If
            If TypeOf v Is DateTimeOffset Then Return DirectCast(v, DateTimeOffset).ToString("yyyy-MM-dd HH:mm:ss.FFF zzz", Inv)
            If TypeOf v Is Boolean Then Return If(DirectCast(v, Boolean), "true", "false")
            Dim f = TryCast(v, IFormattable)
            If f IsNot Nothing Then Return f.ToString(Nothing, Inv)
            Return v.ToString()
        End Function

        ''' <summary>Runs a reading statement in a read-only transaction (rolled back afterwards); the rows (header first) and whether more were left.</summary>
        Private Async Function ReadRowsAsync(c As Connection, sql As String, maxRows As Integer, ct As CancellationToken) As Task(Of (Rows As List(Of List(Of String)), More As Boolean))
            Using conn = Open(c, True)
                Await conn.OpenAsync(ct).ConfigureAwait(False)
                ' Read-only at the database too: SQLite opens read-only, PostgreSQL and MySQL get a read-only transaction, and every
                ' transaction (SQL Server's included) is rolled back.
                If c.Provider = "mysql" Then Await RunAsync(conn, Nothing, "SET SESSION TRANSACTION READ ONLY", ct).ConfigureAwait(False)
                Using tx = Await conn.BeginTransactionAsync(ct).ConfigureAwait(False)
                    If c.Provider = "postgres" Then Await RunAsync(conn, tx, "SET TRANSACTION READ ONLY", ct).ConfigureAwait(False)
                    Try
                        Using cmd = conn.CreateCommand()
                            cmd.Transaction = tx
                            cmd.CommandText = sql
                            cmd.CommandTimeout = _timeoutSeconds
                            Using reader = Await cmd.ExecuteReaderAsync(ct).ConfigureAwait(False)
                                Dim rows As New List(Of List(Of String))
                                Dim more = False
                                If reader.FieldCount > 0 Then
                                    rows.Add(Enumerable.Range(0, reader.FieldCount).Select(Function(i) reader.GetName(i)).ToList())
                                    While Await reader.ReadAsync(ct).ConfigureAwait(False)
                                        If rows.Count > maxRows Then
                                            more = True
                                            Exit While
                                        End If
                                        rows.Add(Enumerable.Range(0, reader.FieldCount).Select(Function(i) Cell(reader.GetValue(i))).ToList())
                                    End While
                                End If
                                Return (rows, more)
                            End Using
                        End Using
                    Finally
                        Try
                            tx.Rollback()
                        Catch ex As Exception When TypeOf ex Is DbException OrElse TypeOf ex Is InvalidOperationException
                        End Try
                    End Try
                End Using
            End Using
        End Function

        Private Async Function RunAsync(conn As DbConnection, tx As DbTransaction, sql As String, ct As CancellationToken) As Task(Of Integer)
            Using cmd = conn.CreateCommand()
                cmd.Transaction = tx
                cmd.CommandText = sql
                cmd.CommandTimeout = _timeoutSeconds
                Return Await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(False)
            End Using
        End Function

        Private Async Function GuardAsync(Of T)(work As Func(Of Task(Of T))) As Task(Of T)
            Try
                Return Await work().ConfigureAwait(False)
            Catch ex As DbException
                Throw New McpException("The database said: " & ex.Message)
            Catch ex As InvalidOperationException When Not TypeOf ex Is ObjectDisposedException
                Throw New McpException("The database could not be used: " & ex.Message)
            Catch ex As ArgumentException
                Throw New McpException("The connection settings are invalid: " & ex.Message)
            Catch ex As TimeoutException
                Throw New McpException($"The query took longer than {_timeoutSeconds} s.")
            End Try
        End Function

        Private Async Function TableAsync(c As Connection, sql As String, ct As CancellationToken, Optional maxRows As Integer = 1000) As Task(Of List(Of List(Of String)))
            Return (Await ReadRowsAsync(c, sql, maxRows, ct).ConfigureAwait(False)).Rows
        End Function

        Private Shared Function Lit(s As String) As String
            Return "'" & If(s, "").Replace("'", "''") & "'"
        End Function

        Private Shared Function SplitName(table As String) As (Schema As String, Name As String)
            Dim t = If(table, "").Trim().Replace("""", "").Replace("[", "").Replace("]", "").Replace("`", "")
            Dim dot = t.LastIndexOf("."c)
            Return If(dot > 0, (t.Substring(0, dot), t.Substring(dot + 1)), (CStr(Nothing), t))
        End Function

        ' ------------------------------------------------------------------------------------------ tools

        Friend Function ListTablesAsync(<Description("Database name or SQLite file path")> database As String,
                                        <Description("Only this schema (optional)")> Optional schema As String = "",
                                        Optional ct As CancellationToken = Nothing) As Task(Of String)
            Return GuardAsync(Async Function()
                                  Dim c = Find(database)
                                  Dim sql As String
                                  Dim sch = If(String.IsNullOrWhiteSpace(schema), Nothing, schema.Trim())
                                  Select Case c.Provider
                                      Case "sqlite" : sql = "SELECT name AS table_name, type AS kind FROM sqlite_master WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%' ORDER BY name"
                                      Case "postgres" : sql = "SELECT t.table_schema, t.table_name, t.table_type AS kind, c.reltuples::bigint AS approx_rows FROM information_schema.tables t " &
                                                              "LEFT JOIN pg_class c ON c.relname = t.table_name AND c.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = t.table_schema) " &
                                                              "WHERE t.table_schema NOT IN ('pg_catalog','information_schema')" & If(sch Is Nothing, "", " AND t.table_schema = " & Lit(sch)) & " ORDER BY 1, 2"
                                      Case "mysql" : sql = "SELECT table_schema, table_name, table_type AS kind, table_rows AS approx_rows FROM information_schema.tables WHERE table_schema = " &
                                                           If(sch Is Nothing, "DATABASE()", Lit(sch)) & " ORDER BY table_name"
                                      Case Else : sql = "SELECT t.TABLE_SCHEMA, t.TABLE_NAME, t.TABLE_TYPE AS kind, (SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = OBJECT_ID(QUOTENAME(t.TABLE_SCHEMA) + '.' + QUOTENAME(t.TABLE_NAME)) AND p.index_id IN (0,1)) AS approx_rows " &
                                                        "FROM INFORMATION_SCHEMA.TABLES t" & If(sch Is Nothing, "", " WHERE t.TABLE_SCHEMA = " & Lit(sch)) & " ORDER BY 1, 2"
                                  End Select
                                  Dim rows = Await TableAsync(c, sql, ct).ConfigureAwait(False)
                                  Return If(rows.Count <= 1, "No tables.", $"{rows.Count - 1} table(s)/view(s) in {c.Name}:{vbLf}" & TableText.ToMarkdown(rows, 1000))
                              End Function)
        End Function

        Friend Function DescribeTableAsync(<Description("Database name or SQLite file path")> database As String,
                                           <Description("Table (or schema.table)")> table As String,
                                           Optional ct As CancellationToken = Nothing) As Task(Of String)
            Return GuardAsync(Async Function()
                                  Dim c = Find(database)
                                  Dim t = SplitName(table)
                                  If String.IsNullOrWhiteSpace(t.Name) Then Throw New McpException("table is required.")
                                  Dim sb As New StringBuilder()
                                  Select Case c.Provider
                                      Case "sqlite"
                                          Dim q = """" & t.Name.Replace("""", """""") & """"
                                          Dim cols = Await TableAsync(c, $"SELECT name, type, CASE WHEN ""notnull"" = 1 THEN 'NO' ELSE 'YES' END AS nullable, dflt_value AS ""default"", pk AS primary_key FROM pragma_table_info({Lit(t.Name)})", ct).ConfigureAwait(False)
                                          If cols.Count <= 1 Then Throw New McpException($"No table '{table}'.")
                                          sb.AppendLine("Columns:").AppendLine(TableText.ToMarkdown(cols, 1000))
                                          Dim idx = Await TableAsync(c, $"SELECT il.name AS index_name, il.""unique"", group_concat(ii.name, ', ') AS columns FROM pragma_index_list({Lit(t.Name)}) il, pragma_index_info(il.name) ii GROUP BY il.name", ct).ConfigureAwait(False)
                                          If idx.Count > 1 Then sb.AppendLine().AppendLine("Indexes:").AppendLine(TableText.ToMarkdown(idx, 200))
                                          Dim fks = Await TableAsync(c, $"SELECT ""from"" AS column_name, ""table"" AS references_table, ""to"" AS references_column, on_delete FROM pragma_foreign_key_list({Lit(t.Name)})", ct).ConfigureAwait(False)
                                          If fks.Count > 1 Then sb.AppendLine().AppendLine("Foreign keys:").AppendLine(TableText.ToMarkdown(fks, 200))
                                          Dim n = Await TableAsync(c, $"SELECT COUNT(*) AS rows FROM {q}", ct).ConfigureAwait(False)
                                          sb.AppendLine().Append($"Rows: {n(1)(0)}")
                                      Case "postgres"
                                          Dim schemaSql = If(t.Schema Is Nothing, "current_schema()", Lit(t.Schema))
                                          Dim cols = Await TableAsync(c, "SELECT c.column_name, c.data_type, c.is_nullable AS nullable, c.column_default AS ""default"", " &
                                              "CASE WHEN EXISTS (SELECT 1 FROM information_schema.table_constraints tc JOIN information_schema.key_column_usage k ON k.constraint_name = tc.constraint_name AND k.table_schema = tc.table_schema " &
                                              "WHERE tc.constraint_type = 'PRIMARY KEY' AND tc.table_schema = c.table_schema AND tc.table_name = c.table_name AND k.column_name = c.column_name) THEN 'yes' ELSE '' END AS primary_key " &
                                              $"FROM information_schema.columns c WHERE c.table_schema = {schemaSql} AND c.table_name = {Lit(t.Name)} ORDER BY c.ordinal_position", ct).ConfigureAwait(False)
                                          If cols.Count <= 1 Then Throw New McpException($"No table '{table}'.")
                                          sb.AppendLine("Columns:").AppendLine(TableText.ToMarkdown(cols, 1000))
                                          Dim idx = Await TableAsync(c, $"SELECT indexname, indexdef FROM pg_indexes WHERE schemaname = {schemaSql} AND tablename = {Lit(t.Name)}", ct).ConfigureAwait(False)
                                          If idx.Count > 1 Then sb.AppendLine().AppendLine("Indexes:").AppendLine(TableText.ToMarkdown(idx, 200))
                                          Dim fks = Await TableAsync(c, "SELECT kcu.column_name, ccu.table_name AS references_table, ccu.column_name AS references_column FROM information_schema.table_constraints tc " &
                                              "JOIN information_schema.key_column_usage kcu ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema " &
                                              "JOIN information_schema.constraint_column_usage ccu ON ccu.constraint_name = tc.constraint_name AND ccu.table_schema = tc.table_schema " &
                                              $"WHERE tc.constraint_type = 'FOREIGN KEY' AND tc.table_schema = {schemaSql} AND tc.table_name = {Lit(t.Name)}", ct).ConfigureAwait(False)
                                          If fks.Count > 1 Then sb.AppendLine().AppendLine("Foreign keys:").AppendLine(TableText.ToMarkdown(fks, 200))
                                      Case "mysql"
                                          Dim schemaSql = If(t.Schema Is Nothing, "DATABASE()", Lit(t.Schema))
                                          Dim cols = Await TableAsync(c, "SELECT column_name, column_type, is_nullable AS nullable, column_default AS `default`, column_key AS `key`, extra FROM information_schema.columns " &
                                              $"WHERE table_schema = {schemaSql} AND table_name = {Lit(t.Name)} ORDER BY ordinal_position", ct).ConfigureAwait(False)
                                          If cols.Count <= 1 Then Throw New McpException($"No table '{table}'.")
                                          sb.AppendLine("Columns:").AppendLine(TableText.ToMarkdown(cols, 1000))
                                          Dim idx = Await TableAsync(c, "SELECT index_name, non_unique, GROUP_CONCAT(column_name ORDER BY seq_in_index) AS columns FROM information_schema.statistics " &
                                              $"WHERE table_schema = {schemaSql} AND table_name = {Lit(t.Name)} GROUP BY index_name, non_unique", ct).ConfigureAwait(False)
                                          If idx.Count > 1 Then sb.AppendLine().AppendLine("Indexes:").AppendLine(TableText.ToMarkdown(idx, 200))
                                          Dim fks = Await TableAsync(c, "SELECT column_name, referenced_table_name, referenced_column_name FROM information_schema.key_column_usage " &
                                              $"WHERE table_schema = {schemaSql} AND table_name = {Lit(t.Name)} AND referenced_table_name IS NOT NULL", ct).ConfigureAwait(False)
                                          If fks.Count > 1 Then sb.AppendLine().AppendLine("Foreign keys:").AppendLine(TableText.ToMarkdown(fks, 200))
                                      Case Else
                                          Dim schemaSql = If(t.Schema Is Nothing, "SCHEMA_NAME()", Lit(t.Schema))
                                          Dim cols = Await TableAsync(c, "SELECT c.COLUMN_NAME, c.DATA_TYPE + COALESCE('(' + CAST(c.CHARACTER_MAXIMUM_LENGTH AS varchar(10)) + ')', '') AS data_type, c.IS_NULLABLE AS nullable, c.COLUMN_DEFAULT AS [default], " &
                                              "CASE WHEN k.COLUMN_NAME IS NOT NULL THEN 'yes' ELSE '' END AS primary_key FROM INFORMATION_SCHEMA.COLUMNS c LEFT JOIN (SELECT ku.TABLE_SCHEMA, ku.TABLE_NAME, ku.COLUMN_NAME FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc " &
                                              "JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku ON tc.CONSTRAINT_NAME = ku.CONSTRAINT_NAME WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY') k ON k.TABLE_SCHEMA = c.TABLE_SCHEMA AND k.TABLE_NAME = c.TABLE_NAME AND k.COLUMN_NAME = c.COLUMN_NAME " &
                                              $"WHERE c.TABLE_SCHEMA = {schemaSql} AND c.TABLE_NAME = {Lit(t.Name)} ORDER BY c.ORDINAL_POSITION", ct).ConfigureAwait(False)
                                          If cols.Count <= 1 Then Throw New McpException($"No table '{table}'.")
                                          sb.AppendLine("Columns:").AppendLine(TableText.ToMarkdown(cols, 1000))
                                          Dim idx = Await TableAsync(c, "SELECT i.name AS index_name, i.is_unique, STRING_AGG(col.name, ', ') AS columns FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id " &
                                              $"JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id WHERE i.object_id = OBJECT_ID(QUOTENAME({schemaSql}) + '.' + QUOTENAME({Lit(t.Name)})) GROUP BY i.name, i.is_unique", ct).ConfigureAwait(False)
                                          If idx.Count > 1 Then sb.AppendLine().AppendLine("Indexes:").AppendLine(TableText.ToMarkdown(idx, 200))
                                  End Select
                                  Return sb.ToString().TrimEnd()
                              End Function)
        End Function

        Friend Function QueryAsync(<Description("Database name or SQLite file path")> database As String,
                                   <Description("One read-only SQL statement")> sql As String,
                                   <Description("Most rows to return (default 200, at most 5000)")> Optional max_rows As Integer = 200,
                                   Optional ct As CancellationToken = Nothing) As Task(Of String)
            Return GuardAsync(Async Function()
                                  Dim c = Find(database)
                                  Dim stmt = SingleStatement(sql)
                                  If Not IsReadOnly(stmt) Then Throw New McpException("query only runs read-only statements (SELECT, WITH, EXPLAIN, SHOW, ...). Use execute to change data" & If(c.Writable, ".", $" (and {c.Name} is read-only)."))
                                  Dim limit = Math.Clamp(If(max_rows <= 0, 200, max_rows), 1, 5000)
                                  Dim sw = Diagnostics.Stopwatch.StartNew()
                                  Dim r = Await ReadRowsAsync(c, stmt, limit, ct).ConfigureAwait(False)
                                  If r.Rows.Count = 0 Then Return $"Done in {sw.ElapsedMilliseconds} ms (no result set)."
                                  Dim n = r.Rows.Count - 1
                                  Dim text = TableText.ToMarkdown(r.Rows.Select(Function(row) row.Select(Function(v) If(v.Length > 300, v.Substring(0, 300) & "...", v)).ToList()).ToList(), limit)
                                  Return $"{n:N0} row(s){If(r.More, $" (stopped at {limit}; add LIMIT/WHERE or raise max_rows)", "")} in {sw.ElapsedMilliseconds} ms:{vbLf}{ToolKit.Clip(text, 120000)}"
                              End Function)
        End Function

        Friend Function ExplainAsync(<Description("Database name or SQLite file path")> database As String,
                                     <Description("The SELECT to explain")> sql As String,
                                     Optional ct As CancellationToken = Nothing) As Task(Of String)
            Return GuardAsync(Async Function()
                                  Dim c = Find(database)
                                  Dim stmt = SingleStatement(sql)
                                  If Not IsReadOnly(stmt) Then Throw New McpException("Only read-only statements can be explained here.")
                                  Dim prefix As String
                                  Select Case c.Provider
                                      Case "sqlite" : prefix = "EXPLAIN QUERY PLAN "
                                      Case "postgres" : prefix = "EXPLAIN "
                                      Case "mysql" : prefix = "EXPLAIN "
                                      Case Else : Throw New McpException("For SQL Server, use the estimated plan in SSMS; plans are not available here.")
                                  End Select
                                  Dim r = Await ReadRowsAsync(c, prefix & stmt, 500, ct).ConfigureAwait(False)
                                  Return TableText.ToMarkdown(r.Rows, 500)
                              End Function)
        End Function

        Friend Function ExecuteAsync(<Description("Database name (must be writable)")> database As String,
                                     <Description("One statement that changes data or schema")> sql As String,
                                     Optional ct As CancellationToken = Nothing) As Task(Of String)
            Return GuardAsync(Async Function()
                                  Dim c = Find(database)
                                  If Not c.Writable Then Throw New McpException($"{c.Name} is read-only: an administrator sets DB_{c.Name.ToUpperInvariant()}_WRITABLE=true to allow changes.")
                                  Dim stmt = SingleStatement(sql)
                                  Using conn = Open(c, False)
                                      Await conn.OpenAsync(ct).ConfigureAwait(False)
                                      Using tx = Await conn.BeginTransactionAsync(ct).ConfigureAwait(False)
                                          Dim n = Await RunAsync(conn, tx, stmt, ct).ConfigureAwait(False)
                                          Await tx.CommitAsync(ct).ConfigureAwait(False)
                                          Return $"Done: {If(n < 0, "no row count reported", $"{n:N0} row(s) affected")} in {c.Name}."
                                      End Using
                                  End Using
                              End Function)
        End Function

        Friend Function ExportAsync(<Description("Database name or SQLite file path")> database As String,
                                    <Description("One read-only SQL statement")> sql As String,
                                    <Description("Output file (.csv or .xlsx)")> path As String,
                                    Optional ct As CancellationToken = Nothing) As Task(Of String)
            Return GuardAsync(Async Function()
                                  Dim c = Find(database)
                                  Dim stmt = SingleStatement(sql)
                                  If Not IsReadOnly(stmt) Then Throw New McpException("Only read-only statements can be exported.")
                                  Dim full = _roots.Resolve(path)
                                  Dim ext = IO.Path.GetExtension(full).ToLowerInvariant()
                                  If ext <> ".csv" AndAlso ext <> ".xlsx" Then Throw New McpException("path must end in .csv or .xlsx.")
                                  Dim r = Await ReadRowsAsync(c, stmt, 100000, ct).ConfigureAwait(False)
                                  Directory.CreateDirectory(IO.Path.GetDirectoryName(full))
                                  Dim rows = r.Rows.Select(Function(row) row.Select(Function(v) If(v = "NULL", "", v)).ToList()).ToList()
                                  If ext = ".xlsx" Then
                                      OfficeWriter.WriteXlsx(full, New List(Of (Name As String, Rows As List(Of List(Of String)))) From {("Query", rows)})
                                  Else
                                      File.WriteAllText(full, TableText.ToCsv(rows), New UTF8Encoding(True))
                                  End If
                                  Return $"Saved {Math.Max(0, rows.Count - 1):N0} row(s){If(r.More, " (stopped at 100 000)", "")} to {_roots.Show(full)}."
                              End Function)
        End Function
    End Class

End Namespace
