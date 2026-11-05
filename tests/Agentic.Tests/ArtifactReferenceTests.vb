Option Strict On
Option Explicit On
Option Infer On

Imports Agentic.Artifacts
Imports Agentic.Communication
Imports Xunit

''' <summary>A garbled artifact id in an agent's message still finds the sender's own artifact when only one fits.</summary>
Public Class ArtifactReferenceTests

    Private Shared Function Art(id As String, name As String) As ArtifactInfo
        Return New ArtifactInfo With {.Id = id, .Name = name, .OwnerAgentId = "ProcessingAgent"}
    End Function

    <Fact>
    Public Sub GarbledIdsAndFileNamesResolveToTheSendersArtifact()
        Dim mine = {Art("art_261015201328a6508ff4a7a7", "endpoint-report.md"), Art("art_261015193012c4d27be01f93", "notes.txt"),
                    Art("art_261014081755aa01bb22cc33", "older.json")}
        ' The id from the rejected message: characters dropped by the model.
        Assert.Equal("art_261015201328a6508ff4a7a7", AgentCommunicationService.ResolveOwnArtifact("art_2610151328a68ff4a7a", mine).Id)
        Assert.Equal("art_261015193012c4d27be01f93", AgentCommunicationService.ResolveOwnArtifact("notes.txt", mine).Id)
        Assert.Equal("art_261014081755aa01bb22cc33", AgentCommunicationService.ResolveOwnArtifact("ART_261014081755AA01BB22CC33", mine).Id)
        ' Nothing close, or not an artifact id: no guess.
        Assert.Null(AgentCommunicationService.ResolveOwnArtifact("art_999999999999zzzzzzzzzzzz", mine))
        Assert.Null(AgentCommunicationService.ResolveOwnArtifact("report", mine))
        Assert.Null(AgentCommunicationService.ResolveOwnArtifact("art_2610151328a68ff4a7a", Array.Empty(Of ArtifactInfo)()))
        ' Two artifacts equally close: ambiguous, no guess.
        Dim twins = {Art("art_261015201328a6508ff4a7a7", "a"), Art("art_261015201328a6508ff4a7a8", "b")}
        Assert.Null(AgentCommunicationService.ResolveOwnArtifact("art_261015201328a6508ff4a7a", twins))
    End Sub
End Class
