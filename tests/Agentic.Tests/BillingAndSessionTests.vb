Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports Agentic
Imports Agentic.Configuration
Imports Agentic.Economy
Imports Agentic.Http
Imports Agentic.Logging
Imports Agentic.AI
Imports Xunit

''' <summary>The 15% surcharge, sign-ins that survive a restart, share links' address, and BTCPay deposits.</summary>
Public Class BillingAndSessionTests

    Private Shared Function Json(res As HttpResponseMessage) As JsonElement
        Return JsonDocument.Parse(res.Content.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement
    End Function

    ''' <summary>Forgets the host's in-memory sign-ins, as a restart of the program does.</summary>
    Private Shared Sub ForgetSessionsInMemory(host As TestHost)
        Dim flags = Reflection.BindingFlags.NonPublic Or Reflection.BindingFlags.Instance
        Dim server = GetType(HostApplication).GetField("_http", flags).GetValue(host.App)
        Dim auth = GetType(HttpAdminServer).GetField("_auth", flags).GetValue(server)
        Dim sessions = GetType(AuthService).GetField("_sessions", flags).GetValue(auth)
        sessions.GetType().GetMethod("Clear").Invoke(sessions, Nothing)
    End Sub

    <Fact>
    Public Async Function AVisionModelThatAnswersForAModelThatCannotSeeIsCharged() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                env.Host.H.Chat.NoVisionModels.Add("fast/a")
                env.Host.H.Chat.VisionModels = New List(Of String) From {"code/b"}
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                ' fast/a is free; the vision model code/b has a price.
                Assert.True((Await PlatformApiTests.Send(admin.Client, "PUT", "/api/model-square", New With {.prices = {New With {.model = "code/b", .inputPerMillion = 1000, .outputPerMillion = 2000}}}, admin.Csrf)).IsSuccessStatusCode)
                Assert.Equal(HttpStatusCode.Created, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/users", New With {.username = "seer", .password = "seer-pass-1234", .role = "User", .permissions = PlatformApiTests.ChatUser}, admin.Csrf)).StatusCode)
                Await PlatformApiTests.Send(admin.Client, "POST", "/api/users/seer/balance", New With {.amount = 1}, admin.Csrf)
                Dim seer = Await PlatformApiTests.LoginAsync(env.Port, "seer", "seer-pass-1234")
                Dim png = Await ImageAndVisionRoutingTests.UploadAsync(seer, "red.png", ModelProbe.RedBluePng(8, 8))
                Dim id = Await ChatFeatureTests.NewChatAsync(seer, New With {.model = "fast/a"})
                Dim sse = Await ChatFeatureTests.AskAsync(seer, id, New With {.content = "what colour is this?", .attachments = {png}})
                Assert.Equal("code/b", ChatFeatureTests.Done(sse)("message")("routedModel").ToString())
                Assert.Contains("""costMicros"":11000", sse)
                Assert.Equal(989000L, (Await env.Host.H.Accounts.GetAccountAsync("seer")).BalanceMicros)
                ' Without a picture the free model answers, free.
                Await ChatFeatureTests.AskAsync(seer, id, New With {.content = "thanks"})
                Assert.Equal(989000L, (Await env.Host.H.Accounts.GetAccountAsync("seer")).BalanceMicros)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function TheSurchargeRaisesEveryFeeAndOnlyForAccountsThatHaveIt() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) o.Server.ExposeErrorDetails = True)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Assert.True((Await PlatformApiTests.Send(admin.Client, "PUT", "/api/model-square", New With {.prices = {New With {.model = "fast/a", .inputPerMillion = 1000, .outputPerMillion = 2000}}}, admin.Csrf)).IsSuccessStatusCode)
                Dim created = Await PlatformApiTests.Send(admin.Client, "POST", "/api/users", New With {.username = "payer", .password = "payer-pass-123", .role = "User",
                                                                                                      .permissions = PlatformApiTests.ChatUser, .surcharge = True}, admin.Csrf)
                Assert.Equal(HttpStatusCode.Created, created.StatusCode)
                Assert.True(Json(created).GetProperty("surcharge").GetBoolean())
                Await PlatformApiTests.Send(admin.Client, "POST", "/api/users/payer/balance", New With {.amount = 1}, admin.Csrf)

                ' The account sees the prices it pays: 15% higher.
                Dim payer = Await PlatformApiTests.LoginAsync(env.Port, "payer", "payer-pass-123")
                Dim metaRes = Await payer.Client.GetAsync("/api/chat/meta")
                Assert.True(metaRes.IsSuccessStatusCode, Await metaRes.Content.ReadAsStringAsync())
                Dim meta = Json(metaRes)
                Assert.Equal(1150D, meta.GetProperty("prices").GetProperty("fast/a").GetProperty("input").GetDecimal())
                Assert.Equal(2300D, meta.GetProperty("prices").GetProperty("fast/a").GetProperty("output").GetDecimal())
                Dim sqRes = Await payer.Client.GetAsync("/api/model-square")
                Assert.True(sqRes.IsSuccessStatusCode, Await sqRes.Content.ReadAsStringAsync())
                Dim square = Json(sqRes)
                Assert.Equal(1150D, square.GetProperty("models").EnumerateArray().First(Function(m) m.GetProperty("id").GetString() = "fast/a").GetProperty("inputPerMillion").GetDecimal())

                ' $0.011 at the Model Square price; $0.01265 with the surcharge.
                Dim chatId = Json(Await PlatformApiTests.Send(payer.Client, "POST", "/api/chat/conversations", New With {.model = "fast/a"}, payer.Csrf)).GetProperty("id").GetString()
                Dim paid = Await (Await PlatformApiTests.Send(payer.Client, "POST", $"/api/chat/conversations/{chatId}/messages", New With {.content = "hello", .model = "fast/a"}, payer.Csrf)).Content.ReadAsStringAsync()
                Assert.Contains("""costMicros"":12650", paid)
                Dim account = JsonDocument.Parse(Await payer.Client.GetStringAsync("/api/account")).RootElement
                Assert.Equal(987350L, account.GetProperty("balanceMicros").GetInt64())
                Assert.Contains("+15% surcharge", account.GetProperty("ledger")(0).GetProperty("description").GetString())
                Assert.True(account.GetProperty("stats").GetProperty("surcharge").GetBoolean())

                ' Every other fee too (an agent's cloud use, images, videos all go through ChargeAsync).
                Await env.Host.H.Accounts.ChargeAsync("payer", 1000, AccountService.EntryAgent, "cloud-a", Nothing, Nothing, "A", "agent call")
                Assert.Equal(987350L - 1150L, (Await env.Host.H.Accounts.GetAccountAsync("payer")).BalanceMicros)

                ' An administrator removes the surcharge: the account pays the plain price again.
                Dim edited = Await PlatformApiTests.Send(admin.Client, "PATCH", "/api/users/payer", New With {.surcharge = False}, admin.Csrf)
                Assert.True(edited.IsSuccessStatusCode, Await edited.Content.ReadAsStringAsync())
                Assert.False(Json(edited).GetProperty("user").GetProperty("surcharge").GetBoolean())
                payer = Await PlatformApiTests.LoginAsync(env.Port, "payer", "payer-pass-123")
                Dim plain = Await (Await PlatformApiTests.Send(payer.Client, "POST", $"/api/chat/conversations/{chatId}/messages", New With {.content = "again", .model = "fast/a"}, payer.Csrf)).Content.ReadAsStringAsync()
                Assert.Contains("""costMicros"":11000", plain)
                Assert.Equal(1000D, JsonDocument.Parse(Await payer.Client.GetStringAsync("/api/chat/meta")).RootElement.GetProperty("prices").GetProperty("fast/a").GetProperty("input").GetDecimal())
            End Using
        End Using
    End Function

    <Fact>
    Public Sub SurchargedAmountsRoundUp()
        Assert.Equal(1150L, AccountService.Surcharged(1000L))
        Assert.Equal(2L, AccountService.Surcharged(1L))
        Dim p = AccountService.Surcharged(New SquarePrice With {.ModelId = "m", .InputPerMillion = 0.15D, .OutputPerMillion = 0.6D})
        Assert.Equal(0.1725D, p.InputPerMillion)
        Assert.Equal(0.69D, p.OutputPerMillion)
    End Sub

    <Fact>
    Public Async Function SignInsSurviveARestartUntilSignedOut() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) o.Server.ExposeErrorDetails = True)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                ForgetSessionsInMemory(env.Host)
                ' The cookie still works, and so does its CSRF token.
                Dim who = JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/auth/me")).RootElement
                Assert.Equal("admin", who.GetProperty("user").GetString())
                Assert.Equal(admin.Csrf, who.GetProperty("csrfToken").GetString())
                Assert.Equal(HttpStatusCode.Created, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/auth/api-key", "{}", admin.Csrf)).StatusCode)

                ' Signing out ends it for good, restart or not.
                Await PlatformApiTests.Send(admin.Client, "POST", "/api/auth/logout", "{}", admin.Csrf)
                ForgetSessionsInMemory(env.Host)
                Assert.Equal(HttpStatusCode.Unauthorized, (Await admin.Client.GetAsync("/api/auth/me")).StatusCode)

                ' Disabling an account ends its stored sign-ins too.
                Dim again = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Assert.Equal(HttpStatusCode.Created, (Await PlatformApiTests.Send(again.Client, "POST", "/api/users", New With {.username = "worker", .password = "worker-pass-123", .role = "User"}, again.Csrf)).StatusCode)
                Dim worker = Await PlatformApiTests.LoginAsync(env.Port, "worker", "worker-pass-123")
                Assert.True(Json(Await PlatformApiTests.Send(again.Client, "PATCH", "/api/users/worker", New With {.disabled = True}, again.Csrf)).GetProperty("sessionsEnded").GetInt32() >= 1)
                ForgetSessionsInMemory(env.Host)
                Assert.Equal(HttpStatusCode.Unauthorized, (Await worker.Client.GetAsync("/api/auth/me")).StatusCode)
                Assert.Equal(HttpStatusCode.OK, (Await again.Client.GetAsync("/api/auth/me")).StatusCode)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function ShareLinksUseTheServersPublicAddress() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) o.Server.PublicUrl = "https://agentic.example.com/")
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Assert.Equal("https://agentic.example.com", JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/auth/me")).RootElement.GetProperty("publicOrigin").GetString())
                Assert.Equal("https://agentic.example.com", JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/chat/meta")).RootElement.GetProperty("publicOrigin").GetString())
                Dim chatId = Json(Await PlatformApiTests.Send(admin.Client, "POST", "/api/chat/conversations", New With {.model = "fast/a"}, admin.Csrf)).GetProperty("id").GetString()
                Await (Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{chatId}/messages", New With {.content = "hello", .model = "fast/a"}, admin.Csrf)).Content.ReadAsStringAsync()
                Dim share = Json(Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{chatId}/share", "{}", admin.Csrf))
                Assert.Equal("https://agentic.example.com/share/" & share.GetProperty("token").GetString(), share.GetProperty("url").GetString())
            End Using
        End Using
        ' Without PublicUrl: the address the request came through (a proxy's forwarded headers first).
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) o.Server.ExposeErrorDetails = True)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Assert.Equal($"http://127.0.0.1:{env.Port}", JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/auth/me")).RootElement.GetProperty("publicOrigin").GetString())
                Dim req As New HttpRequestMessage(HttpMethod.Get, "/api/auth/me")
                req.Headers.Add("X-Forwarded-Proto", "https")
                req.Headers.Add("X-Forwarded-Host", "agentic.smurflabs.xyz")
                Assert.Equal("https://agentic.smurflabs.xyz", Json(Await admin.Client.SendAsync(req)).GetProperty("publicOrigin").GetString())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function ThePaymentServerCreditsEachDepositOnce() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) o.Server.ExposeErrorDetails = True)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Assert.Equal(HttpStatusCode.Created, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/users", New With {.username = "payer", .password = "payer-pass-123", .role = "User"}, admin.Csrf)).StatusCode)
                Using anon As New HttpClient With {.BaseAddress = New Uri($"http://127.0.0.1:{env.Port}")}
                    Dim credit = Function(body As Object, key As String) As Task(Of HttpResponseMessage)
                                     Dim req As New HttpRequestMessage(HttpMethod.Post, "/api/payments/deposit") With {.Content = HttpApiTests.JsonBody(body)}
                                     If key IsNot Nothing Then req.Headers.Add("X-Payment-Key", key)
                                     Return anon.SendAsync(req)
                                 End Function
                    Dim payKey = PaymentOptions.DefaultDepositApiKey
                    Assert.Equal(HttpStatusCode.Unauthorized, (Await credit(New With {.invoiceId = "inv-1", .username = "payer", .amount = 25}, Nothing)).StatusCode)
                    Assert.Equal(HttpStatusCode.Unauthorized, (Await credit(New With {.invoiceId = "inv-1", .username = "payer", .amount = 25}, Guid.NewGuid().ToString())).StatusCode)
                    ' An invoice this server did not make needs the account and the amount.
                    Assert.Equal(HttpStatusCode.NotFound, (Await credit(New With {.invoiceId = "inv-1"}, payKey)).StatusCode)
                    Assert.Equal(HttpStatusCode.NotFound, (Await credit(New With {.invoiceId = "inv-1", .username = "nobody", .amount = 25}, payKey)).StatusCode)

                    Dim first = Json(Await credit(New With {.invoiceId = "inv-1", .username = "payer", .amount = 25}, payKey))
                    Assert.True(first.GetProperty("credited").GetBoolean())
                    Assert.Equal(25_000_000L, first.GetProperty("balanceMicros").GetInt64())
                    ' The webhook may come twice: the money is added once.
                    Dim second = Json(Await credit(New With {.invoiceId = "inv-1", .username = "payer", .amount = 25}, payKey))
                    Assert.False(second.GetProperty("credited").GetBoolean())
                    Assert.True(second.GetProperty("alreadyCredited").GetBoolean())
                    Assert.Equal(25_000_000L, (Await env.Host.H.Accounts.GetAccountAsync("payer")).BalanceMicros)
                    Assert.Equal(HttpStatusCode.Conflict, (Await credit(New With {.invoiceId = "inv-1", .username = "admin"}, payKey)).StatusCode)
                End Using

                ' The account sees the deposit and the money on its Balance tab.
                Dim payer = Await PlatformApiTests.LoginAsync(env.Port, "payer", "payer-pass-123")
                Dim account = JsonDocument.Parse(Await payer.Client.GetStringAsync("/api/account?days=7")).RootElement
                Assert.Equal(25_000_000L, account.GetProperty("balanceMicros").GetInt64())
                Dim recent = account.GetProperty("deposits").GetProperty("recent")(0)
                Assert.Equal("inv-1", recent.GetProperty("invoiceId").GetString())
                Assert.Equal("credited", recent.GetProperty("status").GetString())
                Dim stats = account.GetProperty("stats")
                Assert.Equal(7, stats.GetProperty("days").GetInt32())
                Assert.Contains(stats.GetProperty("daily").EnumerateArray(), Function(r) r.GetProperty("type").GetString() = "Credit" AndAlso r.GetProperty("micros").GetInt64() = 25_000_000L)
                Assert.Equal(25_000_000L, stats.GetProperty("lifetime").GetProperty("addedMicros").GetInt64())
                Assert.Equal("credited", JsonDocument.Parse(Await payer.Client.GetStringAsync("/api/account/deposits/inv-1")).RootElement.GetProperty("deposit").GetProperty("status").GetString())
                Assert.Equal(HttpStatusCode.NotFound, (Await admin.Client.GetAsync("/api/account/deposits/inv-1")).StatusCode)
                ' Without a BTCPay store, accounts cannot start a deposit.
                Assert.False(account.GetProperty("deposits").GetProperty("available").GetBoolean())
                Assert.Equal(HttpStatusCode.ServiceUnavailable, (Await PlatformApiTests.Send(payer.Client, "POST", "/api/account/deposits", New With {.amount = 10}, payer.Csrf)).StatusCode)
            End Using
        End Using
    End Function

    ''' <summary>Answers BTCPay's Greenfield "create invoice" call and remembers what it was sent.</summary>
    Private NotInheritable Class FakeBtcPay
        Inherits HttpMessageHandler
        Public ReadOnly Requests As New List(Of (Url As String, Auth As String, Body As String))
        Protected Overrides Async Function SendAsync(request As HttpRequestMessage, ct As CancellationToken) As Task(Of HttpResponseMessage)
            Dim body = Await request.Content.ReadAsStringAsync(ct)
            Requests.Add((request.RequestUri.ToString(), request.Headers.GetValues("Authorization").First(), body))
            Return New HttpResponseMessage(HttpStatusCode.OK) With {
                .Content = New StringContent("{""id"":""INV123"",""checkoutLink"":""https://pay.example.com/i/INV123"",""status"":""New""}", Encoding.UTF8, "application/json")}
        End Function
    End Class

    <Fact>
    Public Async Function DepositsMakeABtcPayInvoiceForAWholeAmount() As Task
        Using host = Await TestHost.StartAsync()
            Await host.H.AgentsRepo.CreateUserAsync(New Persistence.DashboardUser With {.Username = "payer", .Role = "User"}, Security.PasswordHasher.Hash("payer-pass-123"), "test")
            Dim fake As New FakeBtcPay()
            Dim opts As New PaymentOptions With {.BtcPayUrl = "https://pay.example.com/", .StoreId = "store-1", .ApiKey = "secret-key", .NotificationUrl = "https://hooks.example.com/"}
            Dim deposits As New DepositService(host.H.Database, host.H.Accounts, opts, New NullLog(), fake)
            Assert.True(deposits.Available)
            Assert.Equal("https://pay.example.com", deposits.CheckoutOrigin)
            Await Assert.ThrowsAsync(Of ArgumentException)(Function() deposits.CreateInvoiceAsync("payer", 0, CancellationToken.None))
            Await Assert.ThrowsAsync(Of ArgumentException)(Function() deposits.CreateInvoiceAsync("payer", 501, CancellationToken.None))

            Dim d = Await deposits.CreateInvoiceAsync("payer", 40, CancellationToken.None)
            Assert.Equal("INV123", d.InvoiceId)
            Assert.Equal("https://pay.example.com/i/INV123", d.CheckoutLink)
            Dim sent = fake.Requests.Single()
            Assert.Equal("https://pay.example.com/api/v1/stores/store-1/invoices", sent.Url)
            Assert.Equal("token secret-key", sent.Auth)
            Dim body = JsonDocument.Parse(sent.Body).RootElement
            Assert.Equal("40", body.GetProperty("amount").GetString())
            Assert.Equal("USD", body.GetProperty("currency").GetString())
            Assert.Equal("payer", body.GetProperty("metadata").GetProperty("username").GetString())
            Assert.StartsWith("agentic-deposit-payer-", body.GetProperty("metadata").GetProperty("orderId").GetString())
            Assert.Equal("https://hooks.example.com/", body.GetProperty("checkout").GetProperty("notificationUrl").GetString())

            ' The payment server confirms it with only the invoice id; the invoiced amount is credited, once.
            Dim c = Await deposits.CreditAsync("INV123", Nothing, Nothing, "test")
            Assert.True(c.Credited)
            Assert.Equal(40_000_000L, c.BalanceMicros)
            Assert.False((Await deposits.CreditAsync("INV123", Nothing, Nothing, "test")).Credited)
            Assert.Equal("credited", (Await deposits.GetAsync("INV123")).Status)
        End Using
    End Function
End Class
