Option Strict On
Option Explicit On
Option Infer On

Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.RegularExpressions

Namespace Security

    ''' <summary>
    ''' Removes secrets from text before it reaches logs, events or the dashboard.
    ''' Known secret values (API keys, tokens) are registered at startup; common token shapes are masked by pattern.
    ''' </summary>
    Public NotInheritable Class SecretRedactor
        Private ReadOnly _secrets As New List(Of String)
        Private ReadOnly _lock As New Object()
        Private Shared ReadOnly Patterns As Regex() = {
            New Regex("sk-[A-Za-z0-9_\-]{16,}", RegexOptions.Compiled),
            New Regex("(?i)(bearer\s+)[A-Za-z0-9_\-\.=]{12,}", RegexOptions.Compiled),
            New Regex("(?i)(""?(api[_-]?key|apikey|token|password|secret)""?\s*[:=]\s*""?)[^""\s,}]{6,}", RegexOptions.Compiled)
        }

        Public Sub AddSecret(secret As String)
            If String.IsNullOrWhiteSpace(secret) OrElse secret.Length < 6 Then Return
            SyncLock _lock
                If Not _secrets.Contains(secret) Then _secrets.Add(secret)
            End SyncLock
        End Sub

        Public Function Redact(text As String) As String
            If String.IsNullOrEmpty(text) Then Return text
            Dim result = text
            SyncLock _lock
                For Each s In _secrets
                    If result.Contains(s, StringComparison.Ordinal) Then result = result.Replace(s, "***")
                Next
            End SyncLock
            result = Patterns(0).Replace(result, "sk-***")
            result = Patterns(1).Replace(result, "$1***")
            result = Patterns(2).Replace(result, "$1***")
            Return result
        End Function

        Private Shared ReadOnly TokenShape As New Regex("(?<![A-Za-z0-9])(?=[A-Za-z0-9_\-]*\d)(?=[A-Za-z0-9_\-]*[A-Za-z])[A-Za-z0-9_\-]{28,}(?![A-Za-z0-9])", RegexOptions.Compiled)

        ''' <summary>True when the text holds what looks like a key, token or password (so it is not stored as a memory).</summary>
        Public Shared Function LooksLikeSecret(text As String) As Boolean
            If String.IsNullOrEmpty(text) Then Return False
            Return Patterns.Any(Function(p) p.IsMatch(text)) OrElse TokenShape.IsMatch(text)
        End Function
    End Class

    ''' <summary>PBKDF2-SHA256 password hashing. Format: pbkdf2$iterations$saltB64$hashB64.</summary>
    Public Module PasswordHasher
        Private Const Iterations As Integer = 210000
        Private Const SaltBytes As Integer = 16
        Private Const HashBytes As Integer = 32

        Public Function Hash(password As String) As String
            If String.IsNullOrEmpty(password) Then Throw New ArgumentException("Password must not be empty.")
            Dim salt = RandomNumberGenerator.GetBytes(SaltBytes)
            Dim derived = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes)
            Return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(derived)}"
        End Function

        Public Function Verify(password As String, stored As String) As Boolean
            If String.IsNullOrEmpty(password) OrElse String.IsNullOrEmpty(stored) Then Return False
            Dim parts = stored.Split("$"c)
            If parts.Length <> 4 OrElse parts(0) <> "pbkdf2" Then Return False
            Dim iters As Integer
            If Not Integer.TryParse(parts(1), iters) OrElse iters < 10000 Then Return False
            Try
                Dim salt = Convert.FromBase64String(parts(2))
                Dim expected = Convert.FromBase64String(parts(3))
                Dim actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iters, HashAlgorithmName.SHA256, expected.Length)
                Return CryptographicOperations.FixedTimeEquals(actual, expected)
            Catch ex As FormatException
                Return False
            End Try
        End Function

        Public Function IsHash(value As String) As Boolean
            Return value IsNot Nothing AndAlso value.StartsWith("pbkdf2$", StringComparison.Ordinal)
        End Function
    End Module

    Public Module InputValidation
        Private ReadOnly IdPattern As New Regex("^[A-Za-z0-9][A-Za-z0-9_\-\.]{0,63}$", RegexOptions.Compiled)

        ''' <summary>Agent / server / model identifiers: 1-64 chars, alphanumerics plus _ - .</summary>
        Public Function IsValidId(id As String) As Boolean
            Return id IsNot Nothing AndAlso IdPattern.IsMatch(id)
        End Function

        Public Function SanitizeToolName(name As String) As String
            If String.IsNullOrEmpty(name) Then Return "tool"
            Dim sb As New StringBuilder()
            For Each c In name
                If Char.IsLetterOrDigit(c) OrElse c = "_"c OrElse c = "-"c Then
                    sb.Append(c)
                Else
                    sb.Append("_"c)
                End If
            Next
            Dim s = sb.ToString()
            If s.Length > 64 Then s = s.Substring(0, 64)
            Return s
        End Function
    End Module

End Namespace
