Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports Agentic.Http
Imports Xunit

''' <summary>
''' Keyboard shortcuts saved per account (/api/account/keybindings): only checked, small lists of short shortcuts are kept.
''' </summary>
Public Class KeybindingsTests

    Private Shared Function Parse(json As String) As JsonElement
        Return JsonDocument.Parse(json).RootElement.Clone()
    End Function

    <Fact>
    Public Sub ChangedShortcutsAreKeptAsSent()
        Dim clean = AdminApi.CleanKeybindings(Parse("{""chat.new"":[""Ctrl+Alt+N""],""chat.rewind"":[""Esc Esc""],""chat.copyCode"":[]}"))
        Assert.Equal("{""chat.new"":[""Ctrl+Alt+N""],""chat.rewind"":[""Esc Esc""],""chat.copyCode"":[]}", clean.ToJsonString(Global.Agentic.Utilities.JsonUtil.Options))
    End Sub

    <Fact>
    Public Sub EmptyObjectMeansEveryDefault()
        Assert.Equal("{}", AdminApi.CleanKeybindings(Parse("{}")).ToJsonString(Global.Agentic.Utilities.JsonUtil.Options))
    End Sub

    <Theory>
    <InlineData("{""bad id!"":[""Ctrl+K""]}")>
    <InlineData("{""chat.new"":""Ctrl+K""}")>
    <InlineData("{""chat.new"":[42]}")>
    <InlineData("{""chat.new"":[""""]}")>
    <InlineData("{""chat.new"":[""a"",""b"",""c"",""d"",""e""]}")>
    <InlineData("{""chat.new"":[""Ctrl+AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA""]}")>
    <InlineData("{""chat.new"":[""Ctrl+\u0001""]}")>
    Public Sub InvalidShortcutsAreRefused(json As String)
        Assert.Throws(Of ApiException)(Function() AdminApi.CleanKeybindings(Parse(json)))
    End Sub

    <Fact>
    Public Sub TooManyChangedShortcutsAreRefused()
        Dim many = "{" & String.Join(",", Enumerable.Range(1, 201).Select(Function(i) $"""a{i}"":[""F{i Mod 12 + 1}""]")) & "}"
        Assert.Throws(Of ApiException)(Function() AdminApi.CleanKeybindings(Parse(many)))
    End Sub

End Class
