Option Strict On
Option Explicit On
Option Infer On

Imports System.Diagnostics
Imports System.Runtime.InteropServices

Namespace Mcp

    ''' <summary>
    ''' Windows job objects for the shell server's commands: they all end with the shell server (none outlives a restart of
    ''' Agentic), and each command has a job of its own nested in that one, so stopping it stops everything it started - even
    ''' what outlived its shell, which a process tree cannot reach once a process in between has ended. Elsewhere these do
    ''' nothing (Process.Kill(True) stops a tree there).
    ''' </summary>
    Friend NotInheritable Class ProcessGroups

        Private Shared ReadOnly Root As IntPtr = CreateRoot()

        Private Sub New()
        End Sub

        ''' <summary>
        ''' Puts a started command in the shell server's job and in a job of its own (returned; Zero outside Windows or when
        ''' Windows refuses, e.g. inside a job that does not allow nesting). Only what it starts after this joins the job:
        ''' PowerShell takes long enough to start that this always comes first; a cmd command can start something within
        ''' milliseconds, before it is in its job.
        ''' </summary>
        Public Shared Function ForCommand(proc As Process) As IntPtr
            If Root = IntPtr.Zero OrElse proc Is Nothing Then Return IntPtr.Zero
            Dim own = IntPtr.Zero
            Try
                If Not AssignProcessToJobObject(Root, proc.Handle) Then Return IntPtr.Zero
                own = CreateJobObject(IntPtr.Zero, Nothing)
                If own = IntPtr.Zero Then Return IntPtr.Zero
                ' Already in the root job: this empty one becomes nested in it.
                If AssignProcessToJobObject(own, proc.Handle) Then Return own
            Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is ComponentModel.Win32Exception
                ' It has already ended.
            End Try
            If own <> IntPtr.Zero Then CloseHandle(own)
            Return IntPtr.Zero
        End Function

        ''' <summary>Stops every process of a command's job and lets go of it.</summary>
        Public Shared Sub Terminate(group As IntPtr)
            If group = IntPtr.Zero Then Return
            TerminateJobObject(group, 1UI)
            CloseHandle(group)
        End Sub

        ''' <summary>Lets go of a command's job without stopping anything (what is left still ends with the shell server).</summary>
        Public Shared Sub Release(group As IntPtr)
            If group <> IntPtr.Zero Then CloseHandle(group)
        End Sub

        Private Shared Function CreateRoot() As IntPtr
            If Not OperatingSystem.IsWindows() Then Return IntPtr.Zero
            Try
                Dim handle = CreateJobObject(IntPtr.Zero, Nothing)
                If handle = IntPtr.Zero Then Return IntPtr.Zero
                Dim info As New JOBOBJECT_EXTENDED_LIMIT_INFORMATION()
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                Dim size = Marshal.SizeOf(Of JOBOBJECT_EXTENDED_LIMIT_INFORMATION)()
                Dim buffer = Marshal.AllocHGlobal(size)
                Try
                    Marshal.StructureToPtr(info, buffer, False)
                    If Not SetInformationJobObject(handle, JobObjectExtendedLimitInformation, buffer, CUInt(size)) Then
                        CloseHandle(handle)
                        Return IntPtr.Zero
                    End If
                Finally
                    Marshal.FreeHGlobal(buffer)
                End Try
                ' The handle stays open while the process runs; Windows ends the job's processes when it closes with it.
                Return handle
            Catch ex As Exception When TypeOf ex Is DllNotFoundException OrElse TypeOf ex Is EntryPointNotFoundException
                Return IntPtr.Zero
            End Try
        End Function

        Private Const JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE As UInteger = &H2000UI
        Private Const JobObjectExtendedLimitInformation As Integer = 9

        <StructLayout(LayoutKind.Sequential)>
        Private Structure JOBOBJECT_BASIC_LIMIT_INFORMATION
            Public PerProcessUserTimeLimit As Long
            Public PerJobUserTimeLimit As Long
            Public LimitFlags As UInteger
            Public MinimumWorkingSetSize As UIntPtr
            Public MaximumWorkingSetSize As UIntPtr
            Public ActiveProcessLimit As UInteger
            Public Affinity As UIntPtr
            Public PriorityClass As UInteger
            Public SchedulingClass As UInteger
        End Structure

        <StructLayout(LayoutKind.Sequential)>
        Private Structure IO_COUNTERS
            Public ReadOperationCount As ULong
            Public WriteOperationCount As ULong
            Public OtherOperationCount As ULong
            Public ReadTransferCount As ULong
            Public WriteTransferCount As ULong
            Public OtherTransferCount As ULong
        End Structure

        <StructLayout(LayoutKind.Sequential)>
        Private Structure JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            Public BasicLimitInformation As JOBOBJECT_BASIC_LIMIT_INFORMATION
            Public IoInfo As IO_COUNTERS
            Public ProcessMemoryLimit As UIntPtr
            Public JobMemoryLimit As UIntPtr
            Public PeakProcessMemoryUsed As UIntPtr
            Public PeakJobMemoryUsed As UIntPtr
        End Structure

        <DllImport("kernel32.dll", CharSet:=CharSet.Unicode, SetLastError:=True)>
        Private Shared Function CreateJobObject(attributes As IntPtr, name As String) As IntPtr
        End Function

        <DllImport("kernel32.dll", SetLastError:=True)>
        Private Shared Function SetInformationJobObject(job As IntPtr, infoClass As Integer, info As IntPtr, length As UInteger) As <MarshalAs(UnmanagedType.Bool)> Boolean
        End Function

        <DllImport("kernel32.dll", SetLastError:=True)>
        Private Shared Function AssignProcessToJobObject(job As IntPtr, process As IntPtr) As <MarshalAs(UnmanagedType.Bool)> Boolean
        End Function

        <DllImport("kernel32.dll", SetLastError:=True)>
        Private Shared Function TerminateJobObject(job As IntPtr, exitCode As UInteger) As <MarshalAs(UnmanagedType.Bool)> Boolean
        End Function

        <DllImport("kernel32.dll", SetLastError:=True)>
        Private Shared Function CloseHandle(handle As IntPtr) As <MarshalAs(UnmanagedType.Bool)> Boolean
        End Function

    End Class

End Namespace
