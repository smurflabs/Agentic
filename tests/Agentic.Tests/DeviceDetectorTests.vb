Option Strict On
Option Explicit On
Option Infer On

Imports System.Reflection
Imports Agentic.Http
Imports Xunit

Public Class DeviceDetectorTests

    <Theory>
    <InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36")>
    <InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15")>
    <InlineData("Mozilla/5.0 (X11; Linux x86_64; rv:130.0) Gecko/20100101 Firefox/130.0")>
    <InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36 Edg/140.0")>
    <InlineData("")>
    Public Sub DesktopBrowsersGetDesktop(ua As String)
        Assert.Equal(DeviceKind.Desktop, DeviceDetector.Classify(ua))
    End Sub

    <Theory>
    <InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1")>
    <InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Mobile Safari/537.36")>
    <InlineData("Mozilla/5.0 (Linux; Android 14; SM-S928B) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/25.0 Chrome/121.0 Mobile Safari/537.36")>
    <InlineData("Mozilla/5.0 (Android 14; Mobile; rv:130.0) Gecko/130.0 Firefox/130.0")>
    Public Sub PhonesGetPhone(ua As String)
        Assert.Equal(DeviceKind.Phone, DeviceDetector.Classify(ua))
    End Sub

    <Theory>
    <InlineData("Mozilla/5.0 (iPad; CPU OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1")>
    <InlineData("Mozilla/5.0 (Linux; Android 14; SM-X710) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36")>
    <InlineData("Mozilla/5.0 (Linux; Android 13; Lenovo TB-J616F) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36")>
    <InlineData("Mozilla/5.0 (Linux; Android 11; KFTRWI) AppleWebKit/537.36 (KHTML, like Gecko) Silk/120.0 like Chrome/120.0 Safari/537.36")>
    Public Sub TabletsGetTablet(ua As String)
        Assert.Equal(DeviceKind.Tablet, DeviceDetector.Classify(ua))
    End Sub

    <Fact>
    Public Sub MobileClientHintCountsAsPhone()
        Assert.Equal(DeviceKind.Phone, DeviceDetector.Classify("Mozilla/5.0 (Linux) Chrome/140.0", "?1"))
        Assert.Equal(DeviceKind.Desktop, DeviceDetector.Classify("Mozilla/5.0 (Linux) Chrome/140.0", "?0"))
        Assert.Equal(DeviceKind.Desktop, DeviceDetector.Classify(Nothing))
    End Sub

    <Fact>
    Public Sub PhonesGetTheFullAppWithAMobileLayout()
        ' StaticFiles normalises backslashes to forward slashes on load, so use TryGet to probe paths.
        Dim files As New StaticFiles()
        Dim file As (Data As Byte(), ContentType As String, ETag As String) = Nothing
        For Each path In {"css/mobile.css", "js/mobile.js", "index.html"}
            Assert.True(files.TryGet(path, file), $"Expected embedded file: {path}")
        Next
        Assert.False(files.TryGet("mobile.html", file))      ' one shell for every device: phones get every page
        Assert.True(files.TryGet("index.html", file))
        Dim html = Text.Encoding.UTF8.GetString(file.Data)
        Assert.Contains("href=""/assets/css/mobile.css"" media=""(max-width: 900px)""", html)
        Assert.Contains("/assets/js/mobile.js", html)
        Assert.Contains("viewport-fit=cover", html)
        Assert.Contains("class=""tabbar""", html)
        Assert.DoesNotContain("<script>", html)   ' strict CSP: no inline scripts
    End Sub
End Class
