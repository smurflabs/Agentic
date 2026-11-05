Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports Agentic.Learning
Imports Agentic.Utilities

Namespace Tools

    ''' <summary>Searches the shared library of lessons the host derived from other agents' failures and successes.</summary>
    Public NotInheritable Class SearchLessonsTool
        Inherits InternalTool

        Private ReadOnly _store As LessonStore

        Public Sub New(store As LessonStore)
            _store = store
        End Sub

        Public Overrides ReadOnly Property Name As String = "search_lessons"
        Public Overrides ReadOnly Property Description As String =
            "Search lessons learned from case studies of agents that went broke or became very profitable (what to do and what to avoid). " &
            "Use it before costly or unfamiliar work."
        Public Overrides ReadOnly Property Parameters As JsonObject = Schema.Obj({"query"},
            Schema.Prop("query", Schema.Str("Topic, e.g. 'escalation', 'subagents budget', 'code review'", 300)),
            Schema.Prop("limit", Schema.Int("Max results (default 5)", 1, 15)))

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Dim list = Await _store.SearchAsync(JsonUtil.GetString(args, "query"), JsonUtil.GetInt(args, "limit", 5)).ConfigureAwait(False)
            If list.Count = 0 Then Return ToolResult.Ok("No lessons match.")
            Return New ToolResult With {
                .Success = True, .Untrusted = True, .UntrustedSource = "lessons",
                .Content = JsonUtil.ToJson(list.Select(Function(l) New With {
                    .kind = l.Kind, .title = l.Title, .lesson = l.Body, .outcome = l.Outcome, .source = l.SourceAgentId, .tags = l.Tags}).ToList())}
        End Function
    End Class

End Namespace
