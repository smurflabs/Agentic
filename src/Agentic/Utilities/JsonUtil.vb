Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Encodings.Web
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.Json.Serialization

Namespace Utilities

    Public Module JsonUtil

        ''' <summary>The first of an OpenAI-style response's "choices", or Nothing (also when the list is empty, as in a stream's usage-only chunk).</summary>
        Public Function FirstChoice(json As JsonNode) As JsonNode
            Dim choices = TryCast(json?("choices"), JsonArray)
            Return If(choices Is Nothing OrElse choices.Count = 0, Nothing, choices(0))
        End Function

        ''' <summary>Options used for API responses and persisted JSON blobs (camelCase, enums as strings).</summary>
        Public ReadOnly Options As JsonSerializerOptions = CreateOptions(False)

        ''' <summary>Indented variant for human-facing output.</summary>
        Public ReadOnly IndentedOptions As JsonSerializerOptions = CreateOptions(True)

        Private Function CreateOptions(indented As Boolean) As JsonSerializerOptions
            Dim o As New JsonSerializerOptions(JsonSerializerDefaults.Web) With {
                .WriteIndented = indented,
                .DefaultIgnoreCondition = JsonIgnoreCondition.Never,
                .Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                .ReadCommentHandling = JsonCommentHandling.Skip,
                .AllowTrailingCommas = True
            }
            o.Converters.Add(New JsonStringEnumConverter())
            Return o
        End Function

        Public Function ToJson(value As Object, Optional indented As Boolean = False) As String
            If value Is Nothing Then Return "null"
            Return JsonSerializer.Serialize(value, value.GetType(), If(indented, IndentedOptions, Options))
        End Function

        Public Function FromJson(Of T)(json As String) As T
            If String.IsNullOrWhiteSpace(json) Then Return Nothing
            Return JsonSerializer.Deserialize(Of T)(json, Options)
        End Function

        Public Function TryParseElement(json As String, ByRef element As JsonElement) As Boolean
            If String.IsNullOrWhiteSpace(json) Then Return False
            Try
                Using doc = JsonDocument.Parse(json)
                    element = doc.RootElement.Clone()
                End Using
                Return True
            Catch ex As JsonException
                Return False
            End Try
        End Function

        ''' <summary>
        ''' JSON whose objects repeat a key ({"path": "a", "path": "b"}, which models write now and then) with each key once, the
        ''' last value winning; any other text comes back as it is. JsonObject throws on a repeated key the first time it is
        ''' read ("An item with the same key has already been added. Key: path"), which crashed an agent's task.
        ''' </summary>
        Public Function CollapseDuplicateKeys(json As String) As String
            If String.IsNullOrWhiteSpace(json) Then Return json
            Try
                Using doc = JsonDocument.Parse(json)
                    If Not HasDuplicateKeys(doc.RootElement) Then Return json
                    Return Collapse(doc.RootElement).ToJsonString(CollapseOut)
                End Using
            Catch ex As JsonException
                Return json
            End Try
        End Function

        Private ReadOnly CollapseOut As New JsonSerializerOptions With {.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping}

        Private Function HasDuplicateKeys(el As JsonElement) As Boolean
            Select Case el.ValueKind
                Case JsonValueKind.Object
                    Dim names As New HashSet(Of String)(StringComparer.Ordinal)
                    For Each p In el.EnumerateObject()
                        If Not names.Add(p.Name) OrElse HasDuplicateKeys(p.Value) Then Return True
                    Next
                Case JsonValueKind.Array
                    For Each item In el.EnumerateArray()
                        If HasDuplicateKeys(item) Then Return True
                    Next
            End Select
            Return False
        End Function

        Private Function Collapse(el As JsonElement) As JsonNode
            Select Case el.ValueKind
                Case JsonValueKind.Object
                    Dim obj As New JsonObject()
                    For Each p In el.EnumerateObject()
                        obj(p.Name) = Collapse(p.Value)
                    Next
                    Return obj
                Case JsonValueKind.Array
                    Dim arr As New JsonArray()
                    For Each item In el.EnumerateArray()
                        arr.Add(Collapse(item))
                    Next
                    Return arr
                Case JsonValueKind.Null, JsonValueKind.Undefined
                    Return Nothing
                Case Else
                    Return JsonNode.Parse(el.GetRawText())
            End Select
        End Function

        Public Function ParseElementOrNull(json As String) As JsonElement?
            Dim el As JsonElement
            If TryParseElement(json, el) Then Return el
            Return Nothing
        End Function

        Public Function GetString(el As JsonElement, name As String, Optional defaultValue As String = Nothing) As String
            If el.ValueKind <> JsonValueKind.Object Then Return defaultValue
            Dim p As JsonElement
            If el.TryGetProperty(name, p) Then
                Select Case p.ValueKind
                    Case JsonValueKind.String : Return p.GetString()
                    Case JsonValueKind.Null, JsonValueKind.Undefined : Return defaultValue
                    Case Else : Return p.GetRawText()
                End Select
            End If
            Return defaultValue
        End Function

        Public Function GetInt(el As JsonElement, name As String, defaultValue As Integer) As Integer
            If el.ValueKind <> JsonValueKind.Object Then Return defaultValue
            Dim p As JsonElement
            If el.TryGetProperty(name, p) Then
                Dim v As Integer
                If p.ValueKind = JsonValueKind.Number AndAlso p.TryGetInt32(v) Then Return v
                If p.ValueKind = JsonValueKind.String AndAlso Integer.TryParse(p.GetString(), v) Then Return v
            End If
            Return defaultValue
        End Function

        Public Function GetDecimal(el As JsonElement, name As String, defaultValue As Decimal) As Decimal
            If el.ValueKind <> JsonValueKind.Object Then Return defaultValue
            Dim p As JsonElement
            If el.TryGetProperty(name, p) Then
                Dim v As Decimal
                If p.ValueKind = JsonValueKind.Number AndAlso p.TryGetDecimal(v) Then Return v
                If p.ValueKind = JsonValueKind.String AndAlso Decimal.TryParse(p.GetString(), Globalization.NumberStyles.Number, Globalization.CultureInfo.InvariantCulture, v) Then Return v
            End If
            Return defaultValue
        End Function

        Public Function GetBool(el As JsonElement, name As String, defaultValue As Boolean) As Boolean
            If el.ValueKind <> JsonValueKind.Object Then Return defaultValue
            Dim p As JsonElement
            If el.TryGetProperty(name, p) Then
                If p.ValueKind = JsonValueKind.True Then Return True
                If p.ValueKind = JsonValueKind.False Then Return False
                Dim b As Boolean
                If p.ValueKind = JsonValueKind.String AndAlso Boolean.TryParse(p.GetString(), b) Then Return b
            End If
            Return defaultValue
        End Function

        ''' <summary>Parses a JSON array of strings stored as text (Nothing, empty or invalid text gives an empty list).</summary>
        Public Function ParseStringList(json As String) As List(Of String)
            Dim result As New List(Of String)
            Dim el As JsonElement
            If String.IsNullOrWhiteSpace(json) OrElse Not TryParseElement(json, el) OrElse el.ValueKind <> JsonValueKind.Array Then Return result
            For Each item In el.EnumerateArray()
                If item.ValueKind = JsonValueKind.String AndAlso Not String.IsNullOrWhiteSpace(item.GetString()) Then result.Add(item.GetString())
            Next
            Return result
        End Function

        Public Function GetStringList(el As JsonElement, name As String) As List(Of String)
            Dim result As New List(Of String)
            If el.ValueKind <> JsonValueKind.Object Then Return result
            Dim p As JsonElement
            If el.TryGetProperty(name, p) AndAlso p.ValueKind = JsonValueKind.Array Then
                For Each item In p.EnumerateArray()
                    If item.ValueKind = JsonValueKind.String Then result.Add(item.GetString())
                Next
            End If
            Return result
        End Function

        ''' <summary>Returns the raw JSON of a property if it is an object/array; otherwise Nothing.</summary>
        Public Function GetRaw(el As JsonElement, name As String) As String
            If el.ValueKind <> JsonValueKind.Object Then Return Nothing
            Dim p As JsonElement
            If el.TryGetProperty(name, p) AndAlso (p.ValueKind = JsonValueKind.Object OrElse p.ValueKind = JsonValueKind.Array) Then
                Return p.GetRawText()
            End If
            Return Nothing
        End Function

        Public Function Node(value As String) As JsonNode
            Return JsonValue.Create(value)
        End Function

        ''' <summary>Any object (anonymous types included) as a JSON node; JsonNodes are returned as they are.</summary>
        Public Function ToNode(value As Object) As JsonNode
            If value Is Nothing Then Return Nothing
            If TypeOf value Is JsonNode Then Return DirectCast(value, JsonNode)
            Return JsonNode.Parse(ToJson(value))
        End Function

        Public Function Node(value As Integer) As JsonNode
            Return JsonValue.Create(value)
        End Function

        Public Function Node(value As Long) As JsonNode
            Return JsonValue.Create(value)
        End Function

        Public Function Node(value As Double) As JsonNode
            Return JsonValue.Create(value)
        End Function

        Public Function Node(value As Boolean) As JsonNode
            Return JsonValue.Create(value)
        End Function

    End Module

    ''' <summary>Small fluent builder for JSON-schema tool parameter definitions.</summary>
    Public NotInheritable Class Schema
        Public Shared Function Obj(required As String(), ParamArray props As KeyValuePair(Of String, JsonNode)()) As JsonObject
            Dim properties As New JsonObject()
            For Each p In props
                properties.Add(p.Key, p.Value)
            Next
            Dim o As New JsonObject()
            o.Add("type", JsonValue.Create("object"))
            o.Add("properties", properties)
            Dim req As New JsonArray()
            If required IsNot Nothing Then
                For Each r In required
                    req.Add(JsonValue.Create(r))
                Next
            End If
            o.Add("required", req)
            o.Add("additionalProperties", JsonValue.Create(False))
            Return o
        End Function

        Public Shared Function Prop(name As String, definition As JsonNode) As KeyValuePair(Of String, JsonNode)
            Return New KeyValuePair(Of String, JsonNode)(name, definition)
        End Function

        Private Shared Function Typed(typeName As String, description As String) As JsonObject
            Dim o As New JsonObject()
            o.Add("type", JsonValue.Create(typeName))
            If Not String.IsNullOrEmpty(description) Then o.Add("description", JsonValue.Create(description))
            Return o
        End Function

        Public Shared Function Str(description As String, Optional maxLength As Integer = 0) As JsonObject
            Dim o = Typed("string", description)
            If maxLength > 0 Then o.Add("maxLength", JsonValue.Create(maxLength))
            Return o
        End Function

        Public Shared Function Int(description As String, Optional minimum As Integer? = Nothing, Optional maximum As Integer? = Nothing) As JsonObject
            Dim o = Typed("integer", description)
            If minimum.HasValue Then o.Add("minimum", JsonValue.Create(minimum.Value))
            If maximum.HasValue Then o.Add("maximum", JsonValue.Create(maximum.Value))
            Return o
        End Function

        Public Shared Function Num(description As String) As JsonObject
            Return Typed("number", description)
        End Function

        Public Shared Function Bool(description As String) As JsonObject
            Return Typed("boolean", description)
        End Function

        Public Shared Function AnyObject(description As String) As JsonObject
            Dim o = Typed("object", description)
            o.Add("additionalProperties", JsonValue.Create(True))
            Return o
        End Function

        Public Shared Function StrArray(description As String) As JsonObject
            Dim o = Typed("array", description)
            o.Add("items", Typed("string", Nothing))
            Return o
        End Function

        ''' <summary>An array whose items follow the given object schema.</summary>
        Public Shared Function ObjArray(description As String, item As JsonObject, Optional maxItems As Integer = 0) As JsonObject
            Dim o = Typed("array", description)
            o.Add("items", item)
            If maxItems > 0 Then o.Add("maxItems", JsonValue.Create(maxItems))
            Return o
        End Function

        Public Shared Function OneOfArray(description As String, values As IEnumerable(Of String)) As JsonObject
            Dim o = Typed("array", description)
            o.Add("items", OneOf(Nothing, values))
            Return o
        End Function

        Public Shared Function Num(description As String, minimum As Double, maximum As Double) As JsonObject
            Dim o = Typed("number", description)
            o.Add("minimum", JsonValue.Create(minimum))
            o.Add("maximum", JsonValue.Create(maximum))
            Return o
        End Function

        Public Shared Function OneOf(description As String, values As IEnumerable(Of String)) As JsonObject
            Dim o = Typed("string", description)
            Dim arr As New JsonArray()
            For Each v In values
                arr.Add(JsonValue.Create(v))
            Next
            o.Add("enum", arr)
            Return o
        End Function
    End Class

End Namespace
