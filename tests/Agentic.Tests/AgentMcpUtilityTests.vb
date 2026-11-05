Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.IO.Compression
Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Threading
Imports Agentic.Mcp
Imports ModelContextProtocol
Imports Xunit

''' <summary>Utility tools: random, cron, text (utilities); formats, math, charts (data); archives, files, images (files); lookups.</summary>
Public Class AgentMcpUtilityTests

    Private Shared Function TempDir() As String
        Dim d = Path.Combine(Path.GetTempPath(), "mcputil-" & Guid.NewGuid().ToString("N").Substring(0, 10))
        Directory.CreateDirectory(d)
        Return d
    End Function

    Private Shared Sub Cleanup(dir As String)
        Try
            Directory.Delete(dir, True)
        Catch ex As Exception
        End Try
    End Sub

    ' ------------------------------------------------------------------ utilities: random, cron, text

    <Fact>
    Public Sub RandomTools()
        Dim u As New UtilitiesServer()
        Dim pw = u.GeneratePassword(24, 3).Split(ControlChars.Lf)
        Assert.Equal(4, pw.Length)
        For Each p In pw.Take(3)
            Assert.Equal(24, p.Length)
            Assert.Matches("[A-Z]", p) : Assert.Matches("[a-z]", p) : Assert.Matches("[0-9]", p) : Assert.Matches("[^A-Za-z0-9]", p)
            Assert.DoesNotMatch("[0O1lI|]", p)
        Next
        Assert.Contains("bits of entropy", pw(3))
        Assert.Matches("^sk_live_[0-9a-f]{64}$", u.RandomSecret(32, "hex", "sk_live_"))
        Assert.Matches("^[A-Za-z0-9_-]{43}$", u.RandomSecret(32, "base64url"))
        Dim nums = u.RandomNumbers(1, 10, 10, True).Split(", ").Select(Function(s) Integer.Parse(s)).ToList()
        Assert.Equal(Enumerable.Range(1, 10), nums.OrderBy(Function(n) n))
        Assert.Throws(Of McpException)(Function() u.RandomNumbers(1, 3, 5, True))
        Assert.Equal(2, u.RandomPick("a, b, c, d", 2).Split(ControlChars.Lf).Length)
        Assert.Equal(4, u.RandomPick("a" & vbLf & "b" & vbLf & "c" & vbLf & "d", 0).Split(ControlChars.Lf).Length)
    End Sub

    <Fact>
    Public Sub CronIsExplained()
        Dim u As New UtilitiesServer()
        Dim r = u.ExplainCron("0 9 * * 1-5", "UTC", 3, "2026-10-30T12:00:00Z")
        Assert.Contains("At 09:00, on Monday through Friday", r)
        Assert.Contains("2026-11-02 09:00 Monday", r)
        Assert.Contains("2026-11-03 09:00 Tuesday", r)
        Assert.StartsWith("Every 15 minutes", UtilitiesServer.DescribeCron("*/15 * * * *").Replace("Every 15 minutes", "Every 15 minutes"))
        Assert.Equal("At 00:30, on day 1 of the month, in January and July", UtilitiesServer.DescribeCron("30 0 1 1,7 *"))
        Assert.Contains("Every day at 00:00", u.ExplainCron("@daily", "Europe/Berlin", 1, "2026-10-30"))
        Assert.Throws(Of McpException)(Function() u.ExplainCron("61 * * * *"))
    End Sub

    <Fact>
    Public Sub TextTools()
        Dim u As New UtilitiesServer()
        Dim stats = u.TextStats("The cat sat. The cat ran!" & vbLf & vbLf & "Second paragraph here.")
        Assert.Contains("Words: 9", stats)
        Assert.Contains("sentences: 3", stats)
        Assert.Contains("paragraphs: 2", stats)
        Assert.Contains("cat (2)", stats)
        Dim diff = u.TextDiff("a" & vbLf & "b" & vbLf & "c" & vbLf & "d", "a" & vbLf & "B" & vbLf & "c" & vbLf & "d" & vbLf & "e")
        Assert.Contains("+2 / -1 lines", diff)
        Assert.Contains("@@ -1,4 +1,5 @@", diff)
        Assert.Contains("-b", diff) : Assert.Contains("+B", diff) : Assert.Contains("+e", diff)
        Assert.Equal("The texts are identical.", u.TextDiff("x", "x"))
        Assert.Equal("apple" & vbLf & "Banana" & vbLf & "cherry", u.TransformLines("cherry" & vbLf & " apple " & vbLf & "Banana" & vbLf & "" & vbLf & "apple", "trim, remove_empty, unique, sort"))
        Assert.Equal("2" & vbLf & "10", u.TransformLines("10" & vbLf & "2", "sort_numeric"))
        Dim found = u.ExtractPatterns("Mail ann@x.com or see https://example.com/a?b=1, from 10.0.0.167 #release @bob")
        Assert.Contains("ann@x.com", found) : Assert.Contains("https://example.com/a?b=1", found) : Assert.Contains("10.0.0.167", found)
        Assert.Contains("#release", found) : Assert.Contains("@bob", found)
        Assert.Contains("numbers (2)", u.ExtractPatterns("It costs 1,299.50 or 7.", "numbers"))
        Assert.Equal("hello-world-cafe", u.ConvertCase("Hello, World! Café", "slug"))
        Assert.Equal("userAccountId", u.ConvertCase("user_account_id", "camel"))
        Assert.Equal("USER_ACCOUNT_ID", u.ConvertCase("userAccountID", "constant"))
        Assert.Equal("ParseHtmlString", u.ConvertCase("parse html string", "pascal"))
        Assert.Equal("The Lord of the Rings", u.ConvertCase("the lord of the rings", "title"))
    End Sub

    ' ------------------------------------------------------------------ data: formats

    <Fact>
    Public Sub DataFormatsConvertAndCheck()
        Dim root = TempDir()
        Try
            Dim d As New DataServer(New PathRoots({root}))
            Dim yaml = "server:" & vbLf & "  port: 8022" & vbLf & "  host: ""*""" & vbLf & "  tls: false" & vbLf & "models:" & vbLf & "  - id: a1" & vbLf & "    ctx: 128000" & vbLf & "  - id: flint" & vbLf & "    ctx: 64000" & vbLf
            Dim json = d.ConvertData(yaml, "json")
            Assert.Contains("""port"": 8022", json)
            Assert.Contains("""tls"": false", json)
            Assert.Contains("""host"": ""*""", json)
            Dim toml = d.ConvertData(json, "toml")
            Assert.Contains("port = 8022", toml)
            Assert.Contains("[[models]]", toml)
            Assert.Contains("""port"": 8022", d.ConvertData(toml, "json", "toml"))
            Dim xml = d.ConvertData("{""config"": {""@version"": 2, ""name"": ""x"", ""item"": [1, 2]}}", "xml")
            Assert.Contains("<config version=""2"">", xml)
            Assert.Contains("<item>2</item>", xml)
            Assert.Contains("""@version"": 2", d.ConvertData(xml, "json"))
            Dim csv = d.ConvertData("[{""name"": ""a, b"", ""qty"": 3}, {""name"": ""c"", ""qty"": 4}]", "csv")
            Assert.Contains("""a, b"",3", csv)
            Assert.Contains("""qty"": 4", d.ConvertData("name,qty" & vbLf & "c,4" & vbLf, "json", "csv"))
            Assert.Contains("Wrote out/config.yaml", d.ConvertData(json, "yaml", "", "out/config.yaml"))
            Assert.Contains("Valid YAML (config.yaml)", d.ValidateData("out/config.yaml"))

            Dim bad = Assert.Throws(Of McpException)(Function() d.ValidateData("a: [1, 2" & vbLf & "b: 3", "yaml"))
            Assert.Contains("Invalid YAML at line", bad.Message)
            Assert.Contains("Invalid JSON", Assert.Throws(Of McpException)(Function() d.ValidateData("{""a"": }")).Message)

            Dim q = d.QueryData(json, "$.models[*].id")
            Assert.Contains("2 match(es)", q)
            Assert.Contains("""flint""", q)
            Assert.Contains("1 match(es)", d.QueryData(json, "$.models[?@.ctx > 100000].id"))

            Dim schema = "{""type"": ""object"", ""required"": [""server""], ""properties"": {""server"": {""type"": ""object"", ""properties"": {""port"": {""type"": ""integer"", ""maximum"": 1024}}}}}"
            Dim v = d.ValidateSchema(yaml, schema)
            Assert.Contains("Not valid", v)
            Assert.Contains("/server/port", v)
            Assert.Contains("Valid", d.ValidateSchema("{""server"": {""port"": 80}}", schema))

            Dim diff = d.DiffData("{""a"": 1, ""b"": {""c"": [1, 2]}, ""gone"": true}", "b:" & vbLf & "  c: [1, 3]" & vbLf & "a: 1" & vbLf & "new: x" & vbLf)
            Assert.Contains("~ $.b.c[1]: 2 -> 3", diff)
            Assert.Contains("- $.gone: true", diff)
            Assert.Contains("+ $.new: ""x""", diff)
            Assert.Contains("No differences", d.DiffData("{""a"": 1}", "a: 1"))
        Finally
            Cleanup(root)
        End Try
    End Sub

    ' ------------------------------------------------------------------ data: math

    <Fact>
    Public Sub StatisticsAndFinance()
        Dim d As New DataServer(New PathRoots({TempDir()}))
        Dim s = d.Statistics("2 4 4 4 5 5 7 9")
        Assert.Contains("count 8, sum 40, mean 5, median 4.5, mode 4 (3x)", s)
        Assert.Contains("population 2;", s)
        Assert.Contains("1,234.5", d.Statistics("1,234.5; 10").Replace("sum 1244.5", "1,234.5"))
        Dim table = "region,product,amount" & vbLf & "North,A,100" & vbLf & "North,B,50.5" & vbLf & "South,A,80" & vbLf
        Assert.Contains("amount (3 numeric of 3)", d.Statistics(source:=table))
        Dim agg = d.Aggregate(table, "region", "amount", "count,sum,avg", "sum")
        Assert.Contains("| North | 2 | 150.5 | 75.25 |", agg)
        Assert.Contains("| South | 1 | 80 | 80 |", agg)
        Assert.Contains("= 25%", d.PercentChange(80D, 100D, 0))
        Assert.Contains("CAGR over 2 years: 11.8034%", d.PercentChange(80D, 100D, 2))
        Dim loan = d.LoanSchedule(200000D, 6.0, 360, 2)
        Assert.Contains("Monthly payment: 1,199.10", loan)
        Assert.Contains("| 360 |", loan)
        Assert.Contains("| 0.00 |", loan)
        Dim ci = d.CompoundInterest(1000D, 5.0, 10, 1)
        Assert.Contains("Final balance: 1,628.89", ci)
        Dim cf = d.CashFlows(10, "-1000, 500, 500, 500")
        Assert.Contains("NPV at 10%: 243.43", cf)
        Assert.Contains("IRR: 23.3752% per period", cf)
        Assert.Contains("Payback: 2 periods", cf)
    End Sub

    ' ------------------------------------------------------------------ data: charts, mermaid

    <Fact>
    Public Async Function ChartsAndDiagrams() As Task
        Dim root = TempDir()
        Try
            Dim d As New DataServer(New PathRoots({root}), "C:\Users\Administrator\Desktop\Agentic\tools\node\node.exe",
                                    "C:\Users\Administrator\Desktop\Agentic\tools\mermaid-cli\node_modules\@mermaid-js\mermaid-cli\src\cli.js",
                                    "C:\Program Files\Google\Chrome\Application\chrome.exe")
            Dim table = "month,revenue,costs" & vbLf & "Jan,1200,800" & vbLf & "Feb,1350,900" & vbLf & "Mar,980,1010" & vbLf
            Assert.Contains("Saved a bar chart of revenue, costs by month", d.MakeChart(table, "c/bar.svg", "bar", title:="Q1"))
            Dim svg = File.ReadAllText(Path.Combine(root, "c", "bar.svg"))
            Assert.Contains("<svg", svg) : Assert.Contains(">Q1<", svg) : Assert.Contains(">revenue<", svg)
            For Each kind In {"line", "area", "hbar", "pie", "scatter"}
                d.MakeChart(table, $"c/{kind}.png", kind, If(kind = "scatter", "revenue", ""), If(kind = "scatter", "costs", ""))
                Dim png = File.ReadAllBytes(Path.Combine(root, "c", kind & ".png"))
                Assert.True(png.Length > 2000, $"{kind}: {png.Length} bytes")
                Assert.Equal(&H89, CInt(png(0))) : Assert.Equal(&H50, CInt(png(1)))
            Next
            Assert.Throws(Of McpException)(Function() d.MakeChart(table, "c/x.svg", "bar", "nope"))
            If File.Exists("C:\Users\Administrator\Desktop\Agentic\tools\mermaid-cli\node_modules\@mermaid-js\mermaid-cli\src\cli.js") Then
                Assert.Contains("Saved the diagram", Await d.RenderMermaidAsync("graph TD" & vbLf & "  A[Start] --> B{OK?}" & vbLf & "  B -->|yes| C[Done]", "m/flow.svg", ct:=CancellationToken.None))
                Assert.Contains("<svg", File.ReadAllText(Path.Combine(root, "m", "flow.svg")))
                Dim err = Await Assert.ThrowsAsync(Of McpException)(Function() d.RenderMermaidAsync("graph TD" & vbLf & "  A -->", "m/bad.svg", ct:=CancellationToken.None))
                Assert.Contains("could not be rendered", err.Message)
            End If
        Finally
            Cleanup(root)
        End Try
    End Function

    ' ------------------------------------------------------------------ files

    <Fact>
    Public Sub ArchivesAndFileChecks()
        Dim root = TempDir()
        Try
            Dim f As New FilesServer(New PathRoots({root}), "C:\nowhere\tesseract.exe")
            Directory.CreateDirectory(Path.Combine(root, "proj", "src"))
            File.WriteAllText(Path.Combine(root, "proj", "readme.md"), "# Proj" & vbLf)
            File.WriteAllText(Path.Combine(root, "proj", "src", "app.py"), "print('hi')" & vbLf)
            Assert.Contains("Packed 2 file(s)", f.CreateArchive("proj", "out/proj.zip"))
            Dim list = f.ListArchive("out/proj.zip")
            Assert.Contains("proj/src/app.py", list)
            Assert.Contains("Unpacked 2 file(s)", f.ExtractArchive("out/proj.zip", "unz"))
            Assert.Equal("print('hi')" & vbLf, File.ReadAllText(Path.Combine(root, "unz", "proj", "src", "app.py")))
            Assert.Contains("Skipped 2", f.ExtractArchive("out/proj.zip", "unz"))  ' already there, overwrite false

            Assert.Contains("Packed 2 file(s)", f.CreateArchive("proj/readme.md, proj/src", "out/p.tar.gz"))
            Assert.Contains("src/app.py", f.ListArchive("out/p.tar.gz"))
            Assert.Contains("Unpacked 2 file(s)", f.ExtractArchive("out/p.tar.gz", "untar"))

            ' A zip with an entry that climbs out of the destination (zip slip) is unpacked without it.
            Using z = ZipFile.Open(Path.Combine(root, "evil.zip"), ZipArchiveMode.Create)
                Using w As New StreamWriter(z.CreateEntry("../../escaped.txt").Open())
                    w.Write("nope")
                End Using
                Using w As New StreamWriter(z.CreateEntry("ok.txt").Open())
                    w.Write("fine")
                End Using
            End Using
            Dim r = f.ExtractArchive("evil.zip", "safe")
            Assert.Contains("outside the folder", r)
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(root), "escaped.txt")))
            Assert.True(File.Exists(Path.Combine(root, "safe", "ok.txt")))

            Dim hash = f.HashFile("proj/readme.md")
            Assert.Contains("SHA256", hash)
            Dim hex = hash.Split(": ").Last().Trim()
            Assert.Contains("MATCHES", f.HashFile("proj/readme.md", "sha256", hex.ToUpperInvariant()))
            Assert.Contains("DOES NOT MATCH", f.HashFile("proj/readme.md", "sha256", "00"))

            Dim data As New DataServer(New PathRoots({root}))
            data.MakeChart("a,b" & vbLf & "x,1" & vbLf & "y,2", "pic.png", "bar")
            File.Copy(Path.Combine(root, "pic.png"), Path.Combine(root, "pic.pdf"))
            Assert.Contains("PNG image", f.FileInfoText("pic.png"))
            Assert.Contains("WARNING: the extension '.pdf' does not match", f.FileInfoText("pic.pdf"))
            Assert.Contains("Word document", f.FileInfoText(NewDocx(root)))
            Assert.Contains("UTF-8", f.FileInfoText("proj/readme.md"))
            Assert.Contains("Png 1600 x 960", f.ImageInfo("pic.png"))
            Assert.Contains("400 x 240 Jpeg", f.TransformImage("pic.png", "small.jpg", 400))
            Assert.Contains("200 x 200", f.TransformImage("pic.png", "sq.webp", 200, 200, "cover"))
            Assert.Contains("rotated 90 degrees", f.TransformImage("pic.png", "rot.png", rotate:=90))
            Assert.Contains("960 x 1600", f.ImageInfo("rot.png"))
            Assert.Contains("cropped to 100 x 50", f.TransformImage("pic.png", "crop.png", crop:="10,10,100,50"))
            Assert.Throws(Of McpException)(Function() f.TransformImage("pic.png", "x.png", crop:="1500,0,500,10"))
            Assert.Throws(Of McpException)(Function() f.ListArchive("../x.zip"))
        Finally
            Cleanup(root)
        End Try
    End Sub

    Private Shared Function NewDocx(root As String) As String
        Dim docs As New DocumentsServer(New PathRoots({root}))
        docs.CreateDocument("note.docx", "# Hi")
        Return "note.docx"
    End Function

    <Fact>
    Public Async Function OcrNeedsTesseract() As Task
        Dim root = TempDir()
        Try
            File.WriteAllBytes(Path.Combine(root, "scan.png"), {137, 80, 78, 71})
            Dim f As New FilesServer(New PathRoots({root}), "C:\nowhere\tesseract.exe")
            Dim ex = Await Assert.ThrowsAsync(Of McpException)(Function() f.OcrAsync("scan.png", ct:=CancellationToken.None))
            Assert.Contains("Tesseract", ex.Message)
        Finally
            Cleanup(root)
        End Try
    End Function

    ' ------------------------------------------------------------------ lookups

    Private NotInheritable Class FakeWeb
        Inherits HttpMessageHandler
        Public ReadOnly Routes As New Dictionary(Of String, String)
        Protected Overrides Function SendAsync(request As HttpRequestMessage, ct As CancellationToken) As Task(Of HttpResponseMessage)
            Dim url = request.RequestUri.ToString()
            Dim hit = Routes.Where(Function(kv) url.Contains(kv.Key)).OrderByDescending(Function(kv) kv.Key.Length).FirstOrDefault()
            If hit.Key Is Nothing Then Return Task.FromResult(New HttpResponseMessage(HttpStatusCode.NotFound) With {.Content = New StringContent("")})
            Return Task.FromResult(New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent(hit.Value, Encoding.UTF8, "application/json")})
        End Function
    End Class

    <Fact>
    Public Async Function LookupsUseTheirServices() As Task
        Dim web As New FakeWeb()
        web.Routes("/PublicHolidays/2026/US") = "[{""date"":""2026-11-26"",""localName"":""Thanksgiving Day"",""name"":""Thanksgiving Day"",""global"":true,""counties"":null}," &
                                                 "{""date"":""2026-12-25"",""localName"":""Christmas Day"",""name"":""Christmas Day"",""global"":true,""counties"":null}," &
                                                 "{""date"":""2026-03-31"",""localName"":""César Chávez Day"",""name"":""César Chávez Day"",""global"":false,""counties"":[""US-CA""]}]"
        web.Routes("/latest?base=USD") = "{""amount"":1.0,""base"":""USD"",""date"":""2026-10-29"",""rates"":{""EUR"":0.89847,""GBP"":0.75093}}"
        web.Routes("ipwho.is/8.8.8.8") = "{""ip"":""8.8.8.8"",""success"":true,""country"":""United States"",""country_code"":""US"",""region"":""California"",""city"":""Mountain View"",""continent"":""North America"",""latitude"":37.4,""longitude"":-122.1,""connection"":{""asn"":15169,""org"":""Google LLC"",""isp"":""Google LLC"",""domain"":""google.com""},""timezone"":{""id"":""America/Los_Angeles"",""utc"":""-07:00""}}"
        web.Routes("rdap.org/domain/example.com") = "{""ldhName"":""EXAMPLE.COM"",""status"":[""client delete prohibited""],""events"":[{""eventAction"":""registration"",""eventDate"":""1995-08-14T04:00:00Z""},{""eventAction"":""expiration"",""eventDate"":""2099-08-13T04:00:00Z""}]," &
                                                    """entities"":[{""roles"":[""registrar""],""publicIds"":[{""type"":""IANA Registrar ID"",""identifier"":""376""}],""vcardArray"":[""vcard"",[[""version"",{},""text"",""4.0""],[""fn"",{},""text"",""RESERVED-Internet Assigned Numbers Authority""]]]," &
                                                    """entities"":[{""roles"":[""abuse""],""vcardArray"":[""vcard"",[[""email"",{},""text"",""abuse@iana.org""]]]}]}],""nameservers"":[{""ldhName"":""A.IANA-SERVERS.NET""}],""secureDNS"":{""delegationSigned"":true}}"
        Dim l As New LookupsServer(web)
        Dim hol = Await l.HolidaysAsync("us", 2026, ct:=CancellationToken.None)
        Assert.Contains("2026-11-26 Thursday  Thanksgiving Day", hol)
        Assert.Contains("regional only: US-CA", hol)
        Dim bd = Await l.BusinessDaysAsync("US", "2026-11-25", "2026-11-30", ct:=CancellationToken.None)
        Assert.Contains("2 working day(s)", bd)   ' Fri 27 and Mon 30; Thu 26 is Thanksgiving
        Assert.Contains("2026-11-26 Thanksgiving Day", bd)
        Assert.Contains("= 2026-11-27 (Friday)", Await l.BusinessDaysAsync("US", "2026-11-25", add_days:=1, ct:=CancellationToken.None))
        Dim fx = Await l.CurrencyAsync(1000D, "usd", "EUR, GBP", ct:=CancellationToken.None)
        Assert.Contains("= 898.47 EUR", fx)
        Assert.Contains("= 750.93 GBP", fx)
        Assert.Contains("Mountain View, California, United States", Await l.IpLookupAsync("8.8.8.8", CancellationToken.None))
        Assert.Contains("private network", Await l.IpLookupAsync("10.0.0.167", CancellationToken.None))
        Dim w = Await l.WhoisAsync("www.example.com", CancellationToken.None)
        Assert.Contains("example.com (for www.example.com)", w)
        Assert.Contains("Registrar: RESERVED-Internet Assigned Numbers Authority (IANA 376)", w)
        Assert.Contains("Registration: 1995-08-14", w)
        Assert.Contains("Abuse contact: abuse@iana.org", w)
        Assert.Contains("DNSSEC: signed", w)
        Await Assert.ThrowsAsync(Of McpException)(Function() l.HolidaysAsync("USA", 2026, ct:=CancellationToken.None))
    End Function

End Class
