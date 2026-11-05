Option Strict On
Option Explicit On
Option Infer On

Namespace Http

    Public Enum DeviceKind
        Desktop
        Tablet
        Phone
    End Enum

    ''' <summary>
    ''' Classifies a browser from its request headers. Every device gets the same app (index.html, laid out for narrow
    ''' screens by css/mobile.css); a phone or tablet that asked for the desktop site (?desktop=1) gets it at desktop width.
    ''' </summary>
    Public NotInheritable Class DeviceDetector
        ''' <summary>Cookie that pins a layout chosen with ?desktop=1 or ?mobile=1.</summary>
        Public Const CookieName As String = "agentic_layout"

        Private Shared ReadOnly TabletTokens As String() = {"iPad", "Tablet", "PlayBook", "Silk/", "Kindle", "Nexus 7", "Nexus 9", "Nexus 10", "SM-T", "SM-X", "Tab "}
        Private Shared ReadOnly PhoneTokens As String() = {"iPhone", "iPod", "Windows Phone", "IEMobile", "BlackBerry", "BB10", "Opera Mini", "Mobile Safari", "webOS"}

        Private Sub New()
        End Sub

        ''' <param name="userAgent">The User-Agent header (may be Nothing).</param>
        ''' <param name="chUaMobile">The Sec-CH-UA-Mobile client hint ("?1" on mobile Chromium), may be Nothing.</param>
        Public Shared Function Classify(userAgent As String, Optional chUaMobile As String = Nothing) As DeviceKind
            Dim ua = If(userAgent, "")
            If ua.Length = 0 Then Return If(chUaMobile = "?1", DeviceKind.Phone, DeviceKind.Desktop)
            If Has(ua, TabletTokens) Then Return DeviceKind.Tablet
            ' Android without "Mobile" is a tablet (Google's own convention).
            If Contains(ua, "Android") Then Return If(Contains(ua, "Mobile"), DeviceKind.Phone, DeviceKind.Tablet)
            If Has(ua, PhoneTokens) OrElse Contains(ua, "Mobi") Then Return DeviceKind.Phone
            If chUaMobile = "?1" Then Return DeviceKind.Phone
            ' iPadOS 13+ asks for the desktop site by default and reports itself as a Mac; the mobile app's own
            ' check (touch points) cannot run before the page loads, so these iPads get the desktop console.
            Return DeviceKind.Desktop
        End Function

        Private Shared Function Contains(ua As String, token As String) As Boolean
            Return ua.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0
        End Function

        Private Shared Function Has(ua As String, tokens As String()) As Boolean
            For Each t In tokens
                If Contains(ua, t) Then Return True
            Next
            Return False
        End Function
    End Class

End Namespace
