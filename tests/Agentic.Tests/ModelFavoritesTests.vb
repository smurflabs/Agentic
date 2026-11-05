Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Text.Json.Nodes
Imports Xunit

''' <summary>The model picker's favorites: saved for the account (the same in every app), in the order they were starred.</summary>
Public Class ModelFavoritesTests

    <Fact>
    Public Async Function FavoritesAreSavedForTheAccount() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Assert.Empty(JsonNode.Parse(Await admin.Client.GetStringAsync("/api/chat/meta"))("favoriteModels").AsArray())
                Dim put = Await PlatformApiTests.Send(admin.Client, "PUT", "/api/chat/preferences", New With {.favorites = {"code/b", "fast/a", "code/b", "", "   "}}, admin.Csrf)
                Assert.Equal(HttpStatusCode.OK, put.StatusCode)
                Dim saved = JsonNode.Parse(Await put.Content.ReadAsStringAsync())("favorites").AsArray().Select(Function(x) x.ToString()).ToArray()
                Assert.Equal({"code/b", "fast/a"}, saved)   ' in order, once each, no blanks
                ' Every app gets them with the chat's settings (and the last model is untouched).
                Dim meta = JsonNode.Parse(Await admin.Client.GetStringAsync("/api/chat/meta"))
                Assert.Equal({"code/b", "fast/a"}, meta("favoriteModels").AsArray().Select(Function(x) x.ToString()).ToArray())
                Dim prefs = JsonNode.Parse(Await admin.Client.GetStringAsync("/api/chat/preferences"))
                Assert.Equal(2, prefs("favorites").AsArray().Count)
                ' Unstarring all: an empty list.
                Assert.Equal(HttpStatusCode.OK, (Await PlatformApiTests.Send(admin.Client, "PUT", "/api/chat/preferences", New With {.favorites = Array.Empty(Of String)()}, admin.Csrf)).StatusCode)
                Assert.Empty(JsonNode.Parse(Await admin.Client.GetStringAsync("/api/chat/preferences"))("favorites").AsArray())
                ' Too many is refused.
                Dim many = Enumerable.Range(0, 201).Select(Function(i) "m/" & i).ToArray()
                Assert.Equal(HttpStatusCode.BadRequest, (Await PlatformApiTests.Send(admin.Client, "PUT", "/api/chat/preferences", New With {.favorites = many}, admin.Csrf)).StatusCode)
            End Using
        End Using
    End Function

End Class
