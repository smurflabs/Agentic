Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Security.Cryptography
Imports System.Text

Namespace Security

    ''' <summary>
    ''' Encrypts secrets kept in the database (users' own API keys) with AES-256-GCM. The key is 32 random bytes in
    ''' data\secrets.key, made on first use; a copy of the database alone (a backup, a support bundle) does not reveal the
    ''' secrets. Format: "v1:" + base64(nonce(12) | tag(16) | ciphertext).
    ''' </summary>
    Public NotInheritable Class SecretBox
        Private ReadOnly _key As Byte()

        Public Sub New(keyFile As String)
            _key = LoadOrCreateKey(keyFile)
        End Sub

        ''' <summary>For tests: a box with a given 32-byte key.</summary>
        Friend Sub New(key As Byte())
            If key Is Nothing OrElse key.Length <> 32 Then Throw New ArgumentException("The key must be 32 bytes.")
            _key = key
        End Sub

        Public Function Protect(plain As String) As String
            Dim data = Encoding.UTF8.GetBytes(If(plain, ""))
            Dim nonce = RandomNumberGenerator.GetBytes(12)
            Dim tag(15) As Byte
            Dim cipher(data.Length - 1) As Byte
            Using aes As New AesGcm(_key, 16)
                aes.Encrypt(nonce, data, cipher, tag)
            End Using
            Dim all(11 + 16 + cipher.Length) As Byte
            Buffer.BlockCopy(nonce, 0, all, 0, 12)
            Buffer.BlockCopy(tag, 0, all, 12, 16)
            Buffer.BlockCopy(cipher, 0, all, 28, cipher.Length)
            Return "v1:" & Convert.ToBase64String(all)
        End Function

        ''' <summary>The plain text, or Nothing when it cannot be read (another key, damaged).</summary>
        Public Function Unprotect(boxed As String) As String
            If String.IsNullOrEmpty(boxed) OrElse Not boxed.StartsWith("v1:", StringComparison.Ordinal) Then Return Nothing
            Try
                Dim all = Convert.FromBase64String(boxed.Substring(3))
                If all.Length < 28 Then Return Nothing
                Dim nonce = all.AsSpan(0, 12)
                Dim tag = all.AsSpan(12, 16)
                Dim cipher = all.AsSpan(28)
                Dim plain(cipher.Length - 1) As Byte
                Using aes As New AesGcm(_key, 16)
                    aes.Decrypt(nonce, cipher, tag, plain)
                End Using
                Return Encoding.UTF8.GetString(plain)
            Catch ex As Exception When TypeOf ex Is FormatException OrElse TypeOf ex Is CryptographicException
                Return Nothing
            End Try
        End Function

        Private Shared Function LoadOrCreateKey(file As String) As Byte()
            If IO.File.Exists(file) Then
                Dim existing = Convert.FromBase64String(IO.File.ReadAllText(file).Trim())
                If existing.Length = 32 Then Return existing
                Throw New InvalidOperationException($"{file} is not a valid secrets key.")
            End If
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file)))
            Dim key = RandomNumberGenerator.GetBytes(32)
            IO.File.WriteAllText(file, Convert.ToBase64String(key))
            Try
                If Not OperatingSystem.IsWindows() Then IO.File.SetUnixFileMode(file, UnixFileMode.UserRead Or UnixFileMode.UserWrite)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
            Return key
        End Function
    End Class

End Namespace
