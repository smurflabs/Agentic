Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Channels
Imports Agentic.Security
Imports Agentic.Utilities

Namespace Logging

    Public Enum LogSeverity
        Trace = 0
        Debug = 1
        Info = 2
        Warn = 3
        [Error] = 4
        Critical = 5
    End Enum

    Public NotInheritable Class LogEntry
        Public Property TimestampUtc As DateTime
        Public Property Level As LogSeverity
        Public Property Component As String
        Public Property Message As String
        Public Property AgentId As String
        Public Property TaskId As String
        Public Property MessageId As String
        Public Property CorrelationId As String
        Public Property Exception As String
    End Class

    ''' <summary>Structured logging facade used throughout the host.</summary>
    Public Interface ILog
        Sub Write(level As LogSeverity, component As String, message As String,
                  Optional agentId As String = Nothing, Optional taskId As String = Nothing,
                  Optional messageId As String = Nothing, Optional correlationId As String = Nothing,
                  Optional ex As Exception = Nothing)
        Function IsEnabled(level As LogSeverity) As Boolean
    End Interface

    Public Module LogExtensions
        <System.Runtime.CompilerServices.Extension>
        Public Sub Info(log As ILog, component As String, message As String, Optional agentId As String = Nothing, Optional taskId As String = Nothing)
            log.Write(LogSeverity.Info, component, message, agentId, taskId)
        End Sub

        <System.Runtime.CompilerServices.Extension>
        Public Sub Warn(log As ILog, component As String, message As String, Optional agentId As String = Nothing, Optional taskId As String = Nothing, Optional ex As Exception = Nothing)
            log.Write(LogSeverity.Warn, component, message, agentId, taskId, ex:=ex)
        End Sub

        <System.Runtime.CompilerServices.Extension>
        Public Sub Err(log As ILog, component As String, message As String, Optional ex As Exception = Nothing, Optional agentId As String = Nothing, Optional taskId As String = Nothing)
            log.Write(LogSeverity.Error, component, message, agentId, taskId, ex:=ex)
        End Sub

        <System.Runtime.CompilerServices.Extension>
        Public Sub Dbg(log As ILog, component As String, message As String, Optional agentId As String = Nothing, Optional taskId As String = Nothing)
            If log.IsEnabled(LogSeverity.Debug) Then log.Write(LogSeverity.Debug, component, message, agentId, taskId)
        End Sub
    End Module

    ''' <summary>
    ''' Asynchronous structured logger. Entries go through a bounded channel (drop-oldest when the writer
    ''' falls behind, so logging can never block or exhaust memory) to a JSON-lines rolling file and the console.
    ''' Secrets are redacted before anything is written.
    ''' </summary>
    Public NotInheritable Class StructuredLogger
        Implements ILog, IDisposable

        Private ReadOnly _channel As Channel(Of LogEntry)
        Private ReadOnly _minLevel As LogSeverity
        Private ReadOnly _consoleLevel As LogSeverity
        Private ReadOnly _directory As String
        Private ReadOnly _retainDays As Integer
        Private ReadOnly _maxFileBytes As Long
        Private ReadOnly _redactor As SecretRedactor
        Private ReadOnly _writerTask As Task
        Private _currentDate As String
        Private _currentWriter As StreamWriter
        Private _currentSize As Long
        Private _fileIndex As Integer
        Private _dropped As Long
        Private ReadOnly _consoleEnabled As Boolean

        Public Sub New(directory As String, minLevel As LogSeverity, consoleLevel As LogSeverity, retainDays As Integer,
                       maxFileMb As Integer, redactor As SecretRedactor, Optional consoleEnabled As Boolean = True)
            _directory = directory
            _minLevel = minLevel
            _consoleLevel = consoleLevel
            _retainDays = Math.Max(1, retainDays)
            _maxFileBytes = Math.Max(1, maxFileMb) * 1024L * 1024L
            _redactor = redactor
            _consoleEnabled = consoleEnabled
            If Not String.IsNullOrEmpty(_directory) Then IO.Directory.CreateDirectory(_directory)
            _channel = Channel.CreateBounded(Of LogEntry)(New BoundedChannelOptions(20000) With {
                .FullMode = BoundedChannelFullMode.DropOldest,
                .SingleReader = True,
                .SingleWriter = False
            })
            _writerTask = WriterLoopAsync()
        End Sub

        Public ReadOnly Property DroppedCount As Long
            Get
                Return Interlocked.Read(_dropped)
            End Get
        End Property

        Public Function IsEnabled(level As LogSeverity) As Boolean Implements ILog.IsEnabled
            Return level >= _minLevel
        End Function

        Public Sub Write(level As LogSeverity, component As String, message As String,
                         Optional agentId As String = Nothing, Optional taskId As String = Nothing,
                         Optional messageId As String = Nothing, Optional correlationId As String = Nothing,
                         Optional ex As Exception = Nothing) Implements ILog.Write
            If level < _minLevel Then Return
            Dim entry As New LogEntry With {
                .TimestampUtc = DateTime.UtcNow,
                .Level = level,
                .Component = component,
                .Message = _redactor.Redact(message),
                .AgentId = agentId,
                .TaskId = taskId,
                .MessageId = messageId,
                .CorrelationId = correlationId,
                .Exception = If(ex Is Nothing, Nothing, _redactor.Redact(ex.GetType().Name & ": " & ex.Message & If(level >= LogSeverity.Error, Environment.NewLine & ex.StackTrace, String.Empty)))
            }
            If Not _channel.Writer.TryWrite(entry) Then Interlocked.Increment(_dropped)
        End Sub

        Private Async Function WriterLoopAsync() As Task
            Dim reader = _channel.Reader
            Try
                While Await reader.WaitToReadAsync().ConfigureAwait(False)
                    Dim entry As LogEntry = Nothing
                    While reader.TryRead(entry)
                        WriteEntry(entry)
                    End While
                    _currentWriter?.Flush()
                End While
            Catch ex As Exception
                Try
                    Console.Error.WriteLine("Logger writer failed: " & ex.Message)
                Catch
                End Try
            End Try
            _currentWriter?.Flush()
            _currentWriter?.Dispose()
        End Function

        Private Sub WriteEntry(entry As LogEntry)
            If _consoleEnabled AndAlso entry.Level >= _consoleLevel Then
                Try
                    Dim sb As New StringBuilder()
                    sb.Append(entry.TimestampUtc.ToString("HH:mm:ss")).Append(" ").Append(LevelTag(entry.Level)).Append(" [").Append(entry.Component).Append("] ")
                    If entry.AgentId IsNot Nothing Then sb.Append("(").Append(entry.AgentId).Append(") ")
                    sb.Append(entry.Message)
                    If entry.Exception IsNot Nothing AndAlso entry.Level >= LogSeverity.Error Then sb.Append(" | ").Append(entry.Exception.Split(ControlChars.Lf)(0))
                    If entry.Level >= LogSeverity.Error Then
                        Console.Error.WriteLine(sb.ToString())
                    Else
                        Console.WriteLine(sb.ToString())
                    End If
                Catch
                    ' Console might be unavailable when running as a service.
                End Try
            End If

            If String.IsNullOrEmpty(_directory) Then Return
            Try
                EnsureWriter(entry.TimestampUtc)
                Dim line = JsonSerializer.Serialize(entry, JsonUtil.Options)
                _currentWriter.WriteLine(line)
                _currentSize += line.Length + 2
            Catch ex As Exception
                ' Never let logging failures propagate.
            End Try
        End Sub

        Private Shared Function LevelTag(level As LogSeverity) As String
            Select Case level
                Case LogSeverity.Trace : Return "TRC"
                Case LogSeverity.Debug : Return "DBG"
                Case LogSeverity.Info : Return "INF"
                Case LogSeverity.Warn : Return "WRN"
                Case LogSeverity.Error : Return "ERR"
                Case Else : Return "CRT"
            End Select
        End Function

        Private Sub EnsureWriter(ts As DateTime)
            Dim d = ts.ToString("yyyyMMdd")
            If _currentWriter IsNot Nothing AndAlso d = _currentDate AndAlso _currentSize < _maxFileBytes Then Return
            If _currentWriter IsNot Nothing Then
                _currentWriter.Flush()
                _currentWriter.Dispose()
                _currentWriter = Nothing
            End If
            If d <> _currentDate Then
                _fileIndex = 0
                _currentDate = d
                CleanupOldFiles()
            Else
                _fileIndex += 1
            End If
            Dim fileName = $"agentic-{d}" & If(_fileIndex > 0, "-" & _fileIndex.ToString(), String.Empty) & ".log"
            Dim full = IO.Path.Combine(_directory, fileName)
            Dim fs = New FileStream(full, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)
            _currentWriter = New StreamWriter(fs, New UTF8Encoding(False))
            _currentSize = fs.Length
        End Sub

        Private Sub CleanupOldFiles()
            Try
                Dim cutoff = DateTime.UtcNow.AddDays(-_retainDays)
                For Each f In IO.Directory.GetFiles(_directory, "agentic-*.log").Concat(IO.Directory.GetFiles(_directory, "agenthost-*.log"))
                    If File.GetLastWriteTimeUtc(f) < cutoff Then File.Delete(f)
                Next
            Catch
            End Try
        End Sub

        ''' <summary>Flushes pending entries (bounded wait) and stops the writer.</summary>
        Public Sub Flush(timeout As TimeSpan)
            _channel.Writer.TryComplete()
            Try
                _writerTask.Wait(timeout)
            Catch
            End Try
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            Flush(TimeSpan.FromSeconds(5))
        End Sub
    End Class

    ''' <summary>Logger that discards everything (tests, CLI commands).</summary>
    Public NotInheritable Class NullLog
        Implements ILog

        Public Shared ReadOnly Instance As New NullLog()

        Public Sub Write(level As LogSeverity, component As String, message As String, Optional agentId As String = Nothing, Optional taskId As String = Nothing, Optional messageId As String = Nothing, Optional correlationId As String = Nothing, Optional ex As Exception = Nothing) Implements ILog.Write
        End Sub

        Public Function IsEnabled(level As LogSeverity) As Boolean Implements ILog.IsEnabled
            Return False
        End Function
    End Class

End Namespace
