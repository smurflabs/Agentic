Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports Agentic.AI
Imports Agentic.Utilities

Namespace Http

    ' Admin API: Model control. Administrators turn models off (and on again) for everyone: a turned-off model is not offered
    ' in Chat or the API, custom models do not route to it, and agents, image and video generation do not use it.
    Partial Friend NotInheritable Class AdminApi

        Private Sub RegisterModelControl(r As Router)
            r.Map("GET", "/api/model-control", AuthLevel.Admin, AddressOf ModelControlList)
            r.Map("PUT", "/api/model-control", AuthLevel.Admin, AddressOf ModelControlSet)
        End Sub

        Private NotInheritable Class ControlRow
            Public Id As String
            Public Name As String
            Public ReadOnly Kinds As New List(Of String)
            Public ReadOnly Agents As New List(Of String)
            Public ReadOnly RoutedBy As New List(Of String)
            Public OwnedBy As String
        End Class

        ''' <summary>Every model the server knows (provider, custom, agent, image and video models), with whether it is on.</summary>
        Private Async Function ModelControlList(rc As RequestContext) As Task
            Dim rows As New Dictionary(Of String, ControlRow)(StringComparer.OrdinalIgnoreCase)
            Dim add = Function(id As String, name As String, kind As String) As ControlRow
                          If String.IsNullOrWhiteSpace(id) Then Return Nothing
                          id = id.Trim()
                          Dim row As ControlRow = Nothing
                          If Not rows.TryGetValue(id, row) Then
                              row = New ControlRow With {.Id = id, .Name = If(String.IsNullOrWhiteSpace(name), id, name)}
                              rows(id) = row
                          End If
                          If Not row.Kinds.Contains(kind) Then row.Kinds.Add(kind)
                          Return row
                      End Function
            Dim providerError As String = Nothing
            Try
                For Each m In Await _h.ModelHub.ListAllUpstreamAsync(rc.CancellationToken).ConfigureAwait(False)
                    Dim row = add(m.Id, m.Name, If(ImageRouter.IsImageModel(m.Id), "image", "provider"))
                    row.OwnedBy = m.OwnedBy
                Next
            Catch ex As Exception When TypeOf ex Is Net.Http.HttpRequestException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is TaskCanceledException
                providerError = ex.Message
            End Try
            For Each c In Await _h.ModelHub.ListCustomAsync().ConfigureAwait(False)
                add(c.Id, c.Name, "custom")
                Dim targets = {c.Definition.DefaultModel}.Concat(If(c.Definition.DefaultFallbacks, New List(Of String))).
                    Concat(If(c.Definition.Routes, New List(Of RouterRoute)).SelectMany(Function(rt) {rt.Model}.Concat(If(rt.Fallbacks, New List(Of String)))))
                For Each t In targets.Where(Function(x) Not String.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)
                    Dim row As ControlRow = Nothing
                    If rows.TryGetValue(t.Trim(), row) AndAlso Not row.RoutedBy.Contains(c.Id) Then row.RoutedBy.Add(c.Id)
                Next
            Next
            For Each m In _h.ModelRegistry.All().Where(Function(x) x.Options.ByoOwner Is Nothing)
                Dim row = add(m.Options.ModelId, Nothing, If(m.Options.IsLocal, "local", "agent"))
                If row IsNot Nothing AndAlso Not row.Agents.Contains(m.Key) Then row.Agents.Add(m.Key)
            Next
            Dim io = _h.Options.ImageGeneration
            For Each m In {io.Model}.Concat(If(io.FallbackModels, New List(Of String)))
                add(m, Nothing, "image")
            Next
            Dim vo = _h.Options.VideoGeneration
            For Each m In {vo.Model}.Concat(If(vo.FallbackModels, New List(Of String)))
                add(m, Nothing, "video")
            Next
            ' Turned-off models the lists no longer show stay listed, so they can be turned on again.
            Dim off = _h.ModelHub.DisabledModels().ToDictionary(Function(x) x.Id, StringComparer.OrdinalIgnoreCase)
            For Each d In off.Values
                add(d.Id, Nothing, "other")
            Next
            For Each row In rows.Values.Where(Function(x) x.Kinds.Count > 1 AndAlso x.Kinds.Contains("other"))
                row.Kinds.Remove("other")
            Next
            Dim models = rows.Values.Where(Function(x) Not _h.ModelHub.IsConfigHidden(x.Id)).
                OrderBy(Function(x) x.Id, StringComparer.OrdinalIgnoreCase).
                Select(Function(x)
                           Dim d As (Id As String, By As String, At As DateTime) = Nothing
                           Dim isOff = off.TryGetValue(x.Id, d)
                           Return New With {.id = x.Id, .name = x.Name, .kinds = x.Kinds, .ownedBy = x.OwnedBy, .agents = x.Agents, .routedBy = x.RoutedBy,
                                            .enabled = Not isOff, .disabledBy = If(isOff, d.By, Nothing), .disabledUtc = If(isOff, CType(d.At, DateTime?), Nothing)}
                       End Function).ToList()
            Await rc.OkAsync(New With {.models = models, .providerError = providerError, .disabledCount = off.Count}).ConfigureAwait(False)
        End Function

        ''' <summary>Body {models: [ids], enabled: true|false}: turns the models on or off for everyone.</summary>
        Private Async Function ModelControlSet(rc As RequestContext) As Task
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim el As JsonElement
            If Not b.TryGetProperty("enabled", el) OrElse (el.ValueKind <> JsonValueKind.True AndAlso el.ValueKind <> JsonValueKind.False) Then Throw ApiException.BadRequest("Send {models: [...], enabled: true|false}.")
            Dim enabled = el.GetBoolean()
            Dim ids = JsonUtil.GetStringList(b, "models").Where(Function(x) Not String.IsNullOrWhiteSpace(x)).Select(Function(x) x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            Dim one = JsonUtil.GetString(b, "model")
            If Not String.IsNullOrWhiteSpace(one) AndAlso Not ids.Contains(one.Trim(), StringComparer.OrdinalIgnoreCase) Then ids.Add(one.Trim())
            If ids.Count = 0 Then Throw ApiException.BadRequest("Send {models: [...], enabled: true|false}.")
            If ids.Count > 2000 OrElse ids.Any(Function(x) x.Length > 200) Then Throw ApiException.BadRequest("Too many models, or a model id longer than 200 characters.")
            For Each id In ids
                Await _h.ModelHub.SetEnabledAsync(id, enabled, rc.User.Name).ConfigureAwait(False)
            Next
            Await AuditAsync(rc, If(enabled, "model-control.enable", "model-control.disable"), "model", String.Join(",", ids.Take(20)), New With {.models = ids, .enabled = enabled}).ConfigureAwait(False)
            Await rc.OkAsync(New With {.ok = True, .changed = ids.Count, .enabled = enabled, .disabledCount = _h.ModelHub.DisabledModels().Count}).ConfigureAwait(False)
        End Function
    End Class

End Namespace
