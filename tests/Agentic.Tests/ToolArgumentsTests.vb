Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json.Nodes
Imports Agentic.Utilities
Imports Agentic.Work
Imports Xunit

''' <summary>
''' Past tool calls replayed to the model must carry valid JSON arguments: a provider such as llama.cpp refuses the whole
''' request otherwise ("Failed to parse tool call arguments as JSON"), and long arguments are stored shortened.
''' </summary>
Public Class ToolArgumentsTests

    Private Shared Function Obj(text As String) As JsonObject
        Dim node = JsonNode.Parse(text)
        Assert.IsType(Of JsonObject)(node)
        Return DirectCast(node, JsonObject)
    End Function

    <Fact>
    Public Sub ValidArgumentsAreUnchanged()
        Dim args = "{""path"":""a.py"",""content"":""print(1)""}"
        Assert.Same(args, ChatService.ValidToolArguments(args))
        Assert.Equal("{}", ChatService.ValidToolArguments(Nothing))
        Assert.Equal("{}", ChatService.ValidToolArguments("  "))
    End Sub

    <Fact>
    Public Sub ShortenedWriteIsClosedAndMarked()
        ' What the chat stores for a long file write: the JSON text cut at 8000 characters plus the marker.
        Dim full = "{""path"":""yt-picker/picker.py"",""content"":""" & String.Concat(Enumerable.Repeat("import json\n\""x\"" = 1\n", 600)) & """}"
        Dim stored = TextUtil.Truncate(full, 8000)
        Dim fixedArgs = Obj(ChatService.ValidToolArguments(stored))
        Assert.Equal("yt-picker/picker.py", fixedArgs("path").ToString())
        Assert.EndsWith("…[truncated]", fixedArgs("content").ToString())
    End Sub

    <Theory>
    <InlineData("{""files"":[""a.txt"",""b.t")>                ' inside a string in an array
    <InlineData("{""content"":""abc\")>                        ' a lone backslash at the cut
    <InlineData("{""content"":""x\u00")>                       ' a \uXXXX escape cut short
    <InlineData("{""a"":""x"",""b""")>                         ' a key without its value
    <InlineData("{""a"":""x"",""b"":")>                        ' a colon without its value
    <InlineData("{""a"":""x"",")>                              ' a trailing comma
    <InlineData("{""n"":{""m"":[1,2")>                         ' nested, cut in a number
    Public Sub CutOffObjectsBecomeValid(cut As String)
        Obj(ChatService.ValidToolArguments(cut))
    End Sub

    <Fact>
    Public Sub NonObjectsAreWrapped()
        Dim wrapped = Obj(ChatService.ValidToolArguments("not json at all"))
        Assert.Equal("not json at all", wrapped("_partial").ToString())
        Obj(ChatService.ValidToolArguments("[1,2,3]"))
    End Sub

End Class
