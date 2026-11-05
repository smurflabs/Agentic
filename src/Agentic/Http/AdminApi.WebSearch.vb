Option Strict On
Option Explicit On
Option Infer On

Imports Agentic.Utilities

Namespace Http

    ' Admin API: built-in web search (search_web through SearXNG) - status and a test search.
    Partial Friend NotInheritable Class AdminApi

        Friend Sub RegisterWebSearch(r As Router)
            r.Map("GET", "/api/websearch", AuthLevel.Read, AddressOf WebSearchStatus)
            r.Map("POST", "/api/websearch/test", AuthLevel.Admin, AddressOf WebSearchTest)
        End Sub

        Private Function WebSearchStatus(rc As RequestContext) As Task
            Dim s = _h.WebSearch.Status()
            ' Agents whose tool set includes search_web (subagents inherit it from their parent's tools).
            s("agents") = _h.Agents.GetSnapshots(False).
                Where(Function(a) rc.User.CanAccessAgent(a.Id) AndAlso If(_h.Agents.GetEffectiveProfile(a.Id)?.HasInternalTool("search_web"), False)).
                Select(Function(a) a.Id).ToList()
            Return rc.OkAsync(s)
        End Function

        ''' <summary>Body: {"query": "...", "maxResults": 5}. Runs one search exactly as an agent would.</summary>
        Private Async Function WebSearchTest(rc As RequestContext) As Task
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim q = JsonUtil.GetString(b, "query")
            If String.IsNullOrWhiteSpace(q) Then Throw ApiException.BadRequest("query is required.")
            Dim res As Tools.WebSearchResponse = Nothing
            Dim failure As String = Nothing
            Try
                res = Await _h.WebSearch.SearchAsync(q, JsonUtil.GetInt(b, "maxResults", 5), ct:=rc.CancellationToken).ConfigureAwait(False)
            Catch ex As InvalidOperationException
                failure = ex.Message
            End Try
            If failure IsNot Nothing Then Throw New ApiException(502, "WEB_SEARCH_FAILED", failure)
            Await rc.OkAsync(res).ConfigureAwait(False)
        End Function
    End Class

End Namespace
