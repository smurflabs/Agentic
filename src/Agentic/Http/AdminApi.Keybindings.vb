Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports Agentic.Utilities

Namespace Http

    ' Admin API: each account's keyboard shortcuts for the web app (also inside the desktop app and VS Code, which show it).
    ' Only the shortcuts the user changed are stored - {action id: ["Ctrl+Shift+O", …]}, an empty list turns one off - and the
    ' defaults live in the page (core.js), so new shortcuts and changed defaults reach everyone who kept them.
    Partial Friend NotInheritable Class AdminApi

        Friend Const KeybindingsKey As String = "ui.keybindings"
        Private Const MaxKeybindingActions As Integer = 200
        Private Const MaxKeysPerAction As Integer = 4
        Private Const MaxKeybindingsChars As Integer = 4000

        Private Sub RegisterKeybindings(r As Router)
            r.Map("GET", "/api/account/keybindings", AuthLevel.Read, AddressOf KeybindingsGet)
            r.Map("PUT", "/api/account/keybindings", AuthLevel.Read, AddressOf KeybindingsPut)
        End Sub

        Private Async Function KeybindingsGet(rc As RequestContext) As Task
            Dim raw = Await _h.Chat.GetPreferenceAsync(rc.User.Name, KeybindingsKey).ConfigureAwait(False)
            Dim stored As JsonObject = Nothing
            If Not String.IsNullOrWhiteSpace(raw) Then
                Try
                    stored = TryCast(JsonNode.Parse(raw), JsonObject)
                Catch ex As JsonException
                    stored = Nothing
                End Try
            End If
            Await rc.OkAsync(New With {.bindings = If(stored, New JsonObject())}).ConfigureAwait(False)
        End Function

        ''' <summary>Body {bindings: {actionId: ["Ctrl+K", …], …}}: replaces the account's changed shortcuts ({} = every default).</summary>
        Private Async Function KeybindingsPut(rc As RequestContext) As Task
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim el As JsonElement
            If Not b.TryGetProperty("bindings", el) OrElse el.ValueKind <> JsonValueKind.Object Then
                Throw ApiException.BadRequest("bindings must be an object of action ids and their shortcuts.")
            End If
            Dim clean = CleanKeybindings(el)
            Await _h.Chat.SetPreferenceAsync(rc.User.Name, KeybindingsKey, clean.ToJsonString(JsonUtil.Options)).ConfigureAwait(False)
            Await rc.OkAsync(New With {.bindings = clean}).ConfigureAwait(False)
        End Function

        ''' <summary>The bindings as sent, checked: known-looking action ids, at most a few short shortcuts each, small in total.</summary>
        Friend Shared Function CleanKeybindings(el As JsonElement) As JsonObject
            Dim clean As New JsonObject()
            For Each p In el.EnumerateObject()
                If clean.Count >= MaxKeybindingActions Then Throw ApiException.BadRequest($"At most {MaxKeybindingActions} changed shortcuts.")
                If Not ValidKeyActionId(p.Name) Then Throw ApiException.BadRequest($"Invalid action id '{TextUtil.Truncate(p.Name, 60)}'.")
                If p.Value.ValueKind <> JsonValueKind.Array Then Throw ApiException.BadRequest($"{p.Name}: a list of shortcuts is expected.")
                Dim keys As New JsonArray()
                For Each k In p.Value.EnumerateArray()
                    Dim s = If(k.ValueKind = JsonValueKind.String, k.GetString(), Nothing)
                    If String.IsNullOrWhiteSpace(s) OrElse s.Length > 40 OrElse s.Any(Function(c) Char.IsControl(c)) Then
                        Throw ApiException.BadRequest($"{p.Name}: invalid shortcut.")
                    End If
                    If keys.Count >= MaxKeysPerAction Then Throw ApiException.BadRequest($"{p.Name}: at most {MaxKeysPerAction} shortcuts.")
                    keys.Add(s)
                Next
                clean(p.Name) = keys
            Next
            If clean.ToJsonString(JsonUtil.Options).Length > MaxKeybindingsChars Then Throw ApiException.BadRequest("Too many changed shortcuts.")
            Return clean
        End Function

        Private Shared Function ValidKeyActionId(id As String) As Boolean
            Return Not String.IsNullOrEmpty(id) AndAlso id.Length <= 60 AndAlso
                   id.All(Function(c) (c >= "a"c AndAlso c <= "z"c) OrElse (c >= "A"c AndAlso c <= "Z"c) OrElse Char.IsDigit(c) OrElse c = "."c OrElse c = "-"c OrElse c = "_"c)
        End Function

    End Class

End Namespace
