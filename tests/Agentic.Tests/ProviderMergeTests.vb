Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json.Nodes
Imports Agentic.AI
Imports Xunit

''' <summary>
''' Two connections to the same provider are one (Chat.ProviderMerges {"dddai2": "dddai"}): each model is offered once, under
''' the main provider, and a call that fails at one connection is answered by the other's copy of the model.
''' </summary>
Public Class ProviderMergeTests

    <Fact>
    Public Sub MergedProvidersListEachModelOnceAndKnowTheOtherCopies()
        Dim hub As New ModelHub(Nothing, Nothing, Nothing)
        hub.SetProviderMerges(New Dictionary(Of String, String) From {{"dddai2", "dddai"}})
        Assert.Equal("dddai", hub.ProviderGroup("dddai2/claude-opus-4-8"))
        Assert.Equal("dddai", hub.ProviderGroup("dddai/claude-opus-4-6"))
        Assert.Equal("sev", hub.ProviderGroup("sev/haiku"))
        Assert.Equal("", hub.ProviderGroup("FLINT"))
        Dim listed = {"dddai/a", "dddai2/a", "dddai2/b", "sev/a", "FLINT"}
        Assert.Equal({"dddai/a", "dddai2/b", "sev/a", "FLINT"}, hub.WithoutMergedDuplicates(listed, Function(x) x).ToArray())
        Assert.Equal({"dddai2/a"}, hub.MergedCopies("dddai/a", listed).ToArray())
        Assert.Equal({"dddai/a"}, hub.MergedCopies("dddai2/a", listed).ToArray())
        Assert.Empty(hub.MergedCopies("dddai2/b", listed))   ' only one connection has it
        Assert.Empty(hub.MergedCopies("sev/a", listed))
        ' What another connection may not have: its account's credit or limits, or its copy of the model; not a bad request.
        Assert.True(ModelHub.CopyMayHelp(429, ""))
        Assert.True(ModelHub.CopyMayHelp(503, ""))
        Assert.True(ModelHub.CopyMayHelp(400, "{""error"":{""message"":""insufficient_quota""}}"))
        Assert.False(ModelHub.CopyMayHelp(400, "{""error"":{""message"":""messages: field required""}}"))
    End Sub

    <Fact>
    Public Async Function TheChatOffersEachModelOnceAndFallsBackToTheOtherConnection() As Task
        Using up As New FakeUpstream()
            up.ExtraModels.AddRange({"dddai/opus", "dddai2/opus", "dddai2/sonnet", "sev/haiku"})
            ' dddai is out of credit: its copy fails, dddai2's answers.
            up.Down.Add("dddai/opus")
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) o.Chat.ProviderMerges("dddai2") = "dddai")
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim meta = JsonNode.Parse(Await admin.Client.GetStringAsync("/api/chat/meta"))
                Dim models = meta("providerModels").AsArray().ToDictionary(Function(m) m("id").ToString(), Function(m) m)
                Assert.True(models.ContainsKey("dddai/opus"))
                Assert.False(models.ContainsKey("dddai2/opus"))           ' the duplicate is not offered
                Assert.True(models.ContainsKey("dddai2/sonnet"))          ' only the second connection has it: offered, as dddai
                Assert.Equal("dddai", models("dddai2/sonnet")("group").ToString())
                Assert.Equal("dddai", models("dddai/opus")("group").ToString())
                Assert.Equal("sev", models("sev/haiku")("group").ToString())
                Assert.Equal("fast", models("fast/a")("group").ToString())   ' a provider of its own
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "dddai/opus"})
                Dim done = ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "hello"}))
                Assert.Contains("answer from dddai2/opus", done("message")("content").ToString())
            End Using
        End Using
    End Function

End Class
