Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.IO.Compression
Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Text.Json.Nodes
Imports Agentic.Work
Imports Xunit

''' <summary>The Artifacts page: HTML and SVG from chat answers, websites from projects, and their public links (/a/{token}).</summary>
Public Class ChatArtifactTests

    Private Const Answer As String = "Here it is:" & vbLf & "```js" & vbLf & "console.log(1)" & vbLf & "```" & vbLf &
                                     "```html" & vbLf & "<!doctype html><html><head><title>Snake &amp; Ladders</title></head><body><canvas id=c></canvas><script>c.width=99</script></body></html>" & vbLf & "```" & vbLf &
                                     "And a drawing:" & vbLf & "```svg" & vbLf & "<svg viewBox=""0 0 10 10""><circle cx=""5"" cy=""5"" r=""4""/></svg>" & vbLf & "```"

    <Fact>
    Public Sub ArtifactsAreTheChatTabsCodeBlocks()
        Dim blocks = ChatArtifacts.Blocks(Answer)
        Assert.Equal({"js", "html", "svg"}, blocks.Select(Function(b) b.Lang).ToArray())
        Assert.Equal({0, 1, 2}, blocks.Select(Function(b) b.Index).ToArray())
        Assert.False(ChatArtifacts.IsArtifact("js", blocks(0).Code))
        Assert.True(ChatArtifacts.IsArtifact("html", blocks(1).Code))
        Assert.True(ChatArtifacts.IsArtifact("svg", blocks(2).Code))
        Assert.False(ChatArtifacts.IsArtifact("html", "<b>x</b>"))
        Assert.Equal("Snake & Ladders", ChatArtifacts.TitleOf("html", blocks(1).Code, "Chat"))
        Assert.Equal("My chat", ChatArtifacts.TitleOf("svg", blocks(2).Code, "My chat"))
        Assert.Equal("Big title", ChatArtifacts.TitleOf("html", "<div><h1 class=x>Big <i>title</i></h1></div>", "Chat"))
        ' A whole page stays as it is; a fragment or a drawing gets a page around it.
        Assert.Equal(blocks(1).Code, ChatArtifacts.DocumentOf("html", blocks(1).Code))
        Assert.StartsWith("<!doctype html>", ChatArtifacts.DocumentOf("html", "<p>hello there, world</p>"))
        Dim svgDoc = ChatArtifacts.DocumentOf("svg", blocks(2).Code)
        Assert.Contains("<body>" & blocks(2).Code & "</body>", svgDoc)
        ' An answer that ends inside a block still has it.
        Dim open = ChatArtifacts.Blocks("```html" & vbLf & "<html><body>unfinished page here</body>")
        Assert.Single(open)
        Assert.Contains("unfinished", open(0).Code)
    End Sub

    Private Shared Function Zip(ParamArray files As (Name As String, Text As String)()) As Byte()
        Using ms As New MemoryStream()
            Using z As New ZipArchive(ms, ZipArchiveMode.Create, leaveOpen:=True)
                For Each f In files
                    Using w As New StreamWriter(z.CreateEntry(f.Name).Open())
                        w.Write(f.Text)
                    End Using
                Next
            End Using
            Return ms.ToArray()
        End Using
    End Function

    <Fact>
    Public Async Function AWebsiteIsUnpackedSafelyAndSharingItAgainKeepsItsLink() As Task
        Using host = Await TestHost.StartAsync()
            Dim root = Path.Combine(host.DataDir, "shared-test")
            Dim store As New SharedArtifactStore(host.H.Database, root)
            ' Zipping a folder puts everything in it: that folder's index.html is the site's.
            Dim first = Await store.SaveSiteAsync("alice", "Game", "game", "pc:p1", Zip(("game/index.html", "<title>v1</title>"), ("game/js/app.js", "export {}"), ("game/old.txt", "old")), 1024 * 1024)
            Assert.False(first.Updated)
            Dim token = first.Artifact.Token
            Assert.True(SharedArtifactStore.IsToken(token))
            Assert.Equal(3, first.Artifact.Files)
            Assert.EndsWith("index.html", store.SiteFile(token, ""))
            Assert.EndsWith("app.js", store.SiteFile(token, "js/app.js"))
            Assert.Null(store.SiteFile(token, "../../secrets.txt"))
            Assert.Null(store.SiteFile(token, "js/../../x"))
            Assert.Null(store.SiteFile("not a token!", ""))
            ' The same project again: the same link with the new files (the old ones are gone).
            Dim second = Await store.SaveSiteAsync("alice", "Game", "game", "pc:p1", Zip(("index.html", "<title>v2</title>")), 1024 * 1024)
            Assert.True(second.Updated)
            Assert.Equal(token, second.Artifact.Token)
            Assert.Contains("v2", File.ReadAllText(store.SiteFile(token, "")))
            Assert.Null(store.SiteFile(token, "old.txt"))
            ' Refused: a name that leaves the folder, no index.html, too large. Nothing is kept.
            Await Assert.ThrowsAsync(Of InvalidDataException)(Function() store.SaveSiteAsync("alice", "x", "x", Nothing, Zip(("index.html", "x"), ("../evil.txt", "x")), 1024 * 1024))
            Await Assert.ThrowsAsync(Of InvalidDataException)(Function() store.SaveSiteAsync("alice", "x", "x", Nothing, Zip(("readme.md", "x")), 1024 * 1024))
            Await Assert.ThrowsAsync(Of InvalidDataException)(Function() store.SaveSiteAsync("alice", "x", "x", Nothing, Zip(("index.html", New String("a"c, 5000))), 1000))
            Assert.Equal(1, Directory.GetDirectories(root).Length)
            Assert.Equal(1L, Await store.CountAsync("alice"))
            ' Only its owner removes it (or an administrator: no user name).
            Assert.False(Await store.DeleteAsync(token, "bob"))
            Assert.True(Await store.DeleteAsync(token, "alice"))
            Assert.False(Directory.Exists(Path.Combine(root, token)))
        End Using
    End Function

    <Fact>
    Public Async Function ChatArtifactsAreListedSharedAndOpenedWithoutSigningIn() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim chatId = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Await env.Host.H.Database.ExecuteAsync("INSERT INTO ChatMessages(Id, ChatId, Role, Content, CreatedUtc) VALUES('m1', @c, 'assistant', @t, @n)",
                                                       ("@c", CObj(chatId)), ("@t", CObj(Answer)), ("@n", CObj(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"))))
                Dim list = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/chat/artifacts"))
                Dim items = list("items").AsArray()
                Assert.Equal(2, items.Count)
                Assert.Equal("Snake & Ladders", items(0)("title").ToString())
                Assert.Equal(1, items(0)("block").GetValue(Of Integer)())
                Assert.Equal("svg", items(1)("lang").ToString())
                Assert.True(list("canShare").GetValue(Of Boolean)())
                ' Only this chat's (the chat page's "Artifacts in this chat").
                Assert.Equal(2, (Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/chat/artifacts?chat=" & chatId)))("items").AsArray().Count)
                Assert.Empty((Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/chat/artifacts?chat=nope")))("items").AsArray())

                Dim code = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync($"/api/chat/artifacts/code?chat={chatId}&message=m1&block=1"))
                Assert.Contains("<canvas", code("code").ToString())
                Assert.Equal(HttpStatusCode.NotFound, (Await admin.Client.GetAsync($"/api/chat/artifacts/code?chat={chatId}&message=m1&block=0")).StatusCode)

                Dim link = Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(admin.Client, "POST", "/api/chat/artifacts/share", New With {.chatId = chatId, .messageId = "m1", .block = 1}, admin.Csrf))
                Dim token = link("token").ToString()
                Assert.EndsWith("/a/" & token, link("url").ToString())
                ' Shared again: the same link.
                Dim again = Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(admin.Client, "POST", "/api/chat/artifacts/share", New With {.chatId = chatId, .messageId = "m1", .block = 1}, admin.Csrf))
                Assert.Equal(token, again("token").ToString())
                list = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/chat/artifacts"))
                Assert.Equal(token, list("items")(0)("sharedToken").ToString())
                Assert.Single(list("shared").AsArray())

                ' Anyone opens it: its page, what it is, and the artifact itself as an isolated origin.
                Using anon As New HttpClient With {.BaseAddress = New Uri($"http://127.0.0.1:{env.Port}")}
                    Dim page = Await anon.GetAsync("/a/" & token)
                    Assert.Equal(HttpStatusCode.OK, page.StatusCode)
                    Assert.Contains("artifact.js", Await page.Content.ReadAsStringAsync())
                    Dim info = Await ChatFeatureTests.JsonOf(Await anon.GetAsync("/api/public/artifacts/" & token))
                    Assert.Equal("Snake & Ladders", info("title").ToString())
                    Assert.Equal($"/a/{token}/run", info("run").ToString())
                    Dim run = Await anon.GetAsync($"/a/{token}/run")
                    Assert.Equal(HttpStatusCode.OK, run.StatusCode)
                    Assert.Equal("text/html", run.Content.Headers.ContentType.MediaType)
                    Dim csp = String.Join(" ", run.Headers.GetValues("Content-Security-Policy"))
                    Assert.Contains("sandbox allow-scripts", csp)
                    Assert.DoesNotContain("allow-same-origin", csp)
                    Assert.Contains("frame-ancestors 'self'", csp)
                    Assert.Contains("<canvas id=c>", Await run.Content.ReadAsStringAsync())
                    Assert.Equal(HttpStatusCode.NotFound, (Await anon.GetAsync($"/a/{token}/site/")).StatusCode)
                    Assert.Equal(HttpStatusCode.NotFound, (Await anon.GetAsync("/api/public/artifacts/nope-nope-nope")).StatusCode)
                    ' A view is counted each time its page asks what it is.
                    Assert.Equal(2, (Await ChatFeatureTests.JsonOf(Await anon.GetAsync("/api/public/artifacts/" & token)))("views").GetValue(Of Integer)())

                    ' Sharing turned off: no new links, and existing ones stop opening.
                    env.Host.Options.Chat.ArtifactSharing = False
                    Assert.Equal(HttpStatusCode.NotFound, (Await anon.GetAsync($"/a/{token}/run")).StatusCode)
                    Assert.Equal(CType(409, HttpStatusCode), (Await PlatformApiTests.Send(admin.Client, "POST", "/api/chat/artifacts/share", New With {.chatId = chatId, .messageId = "m1", .block = 2}, admin.Csrf)).StatusCode)
                    env.Host.Options.Chat.ArtifactSharing = True

                    ' Stop sharing: the link no longer opens.
                    Assert.Equal(HttpStatusCode.OK, (Await PlatformApiTests.Send(admin.Client, "DELETE", "/api/chat/artifacts/shared/" & token, Nothing, admin.Csrf)).StatusCode)
                    Assert.Equal(HttpStatusCode.NotFound, (Await anon.GetAsync($"/a/{token}/run")).StatusCode)
                    Assert.Equal(HttpStatusCode.NotFound, (Await anon.GetAsync("/api/public/artifacts/" & token)).StatusCode)
                End Using
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function AWebsiteFromAnAppIsSharedWithItsKey() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim key = Await PlatformApiTests.NewKeyAsync(admin.Client, admin.Csrf)
                Using app As New HttpClient With {.BaseAddress = New Uri($"http://127.0.0.1:{env.Port}")}
                    app.DefaultRequestHeaders.Authorization = New Headers.AuthenticationHeaderValue("Bearer", key)
                    Dim upload = Function(z As Byte()) app.PostAsync("/api/chat/artifacts/share-site?title=My%20game&source=game&key=pc1:pabc", New ByteArrayContent(z))
                    Dim res = Await upload(Zip(("dist/index.html", "<script type=module src=js/app.js></script>"), ("dist/js/app.js", "export const v = 1;")))
                    Assert.Equal(HttpStatusCode.Created, res.StatusCode)
                    Dim body = JsonNode.Parse(Await res.Content.ReadAsStringAsync())
                    Dim token = body("link")("token").ToString()
                    Assert.False(body("updated").GetValue(Of Boolean)())
                    Assert.Equal("pc1:pabc", body("link")("key").ToString())
                    Using anon As New HttpClient With {.BaseAddress = New Uri($"http://127.0.0.1:{env.Port}")}
                        Dim index = Await anon.GetAsync($"/a/{token}/site/")
                        Assert.Equal(HttpStatusCode.OK, index.StatusCode)
                        Assert.Contains("type=module", Await index.Content.ReadAsStringAsync())
                        Assert.Contains("sandbox", String.Join(" ", index.Headers.GetValues("Content-Security-Policy")))
                        ' Its own requests come from an opaque origin: modules and fetches need CORS.
                        Dim js = Await anon.GetAsync($"/a/{token}/site/js/app.js")
                        Assert.Equal("text/javascript", js.Content.Headers.ContentType.MediaType)
                        Assert.Equal("*", js.Headers.GetValues("Access-Control-Allow-Origin").First())
                        Assert.Equal(HttpStatusCode.NotFound, (Await anon.GetAsync($"/a/{token}/site/..%2F..%2Fagentic.db")).StatusCode)
                        Assert.Equal(HttpStatusCode.NotFound, (Await anon.GetAsync($"/a/{token}/run")).StatusCode)
                        Assert.Equal($"/a/{token}/site/", (Await ChatFeatureTests.JsonOf(Await anon.GetAsync("/api/public/artifacts/" & token)))("run").ToString())
                    End Using
                    ' The same project again: the same link, updated.
                    res = Await upload(Zip(("index.html", "<p>v2 of the game</p>")))
                    Assert.Equal(HttpStatusCode.OK, res.StatusCode)
                    body = JsonNode.Parse(Await res.Content.ReadAsStringAsync())
                    Assert.True(body("updated").GetValue(Of Boolean)())
                    Assert.Equal(token, body("link")("token").ToString())
                    ' Not a website.
                    res = Await app.PostAsync("/api/chat/artifacts/share-site?title=x", New ByteArrayContent(Zip(("notes.txt", "x"))))
                    Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode)
                    Assert.Contains("index.html", Await res.Content.ReadAsStringAsync())
                End Using
            End Using
        End Using
    End Function

End Class
