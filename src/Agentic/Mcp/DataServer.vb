Option Strict On
Option Explicit On
Option Infer On

Imports System.ComponentModel
Imports System.Globalization
Imports System.IO
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports System.Xml.Linq
Imports ModelContextProtocol
Imports ModelContextProtocol.Server

Namespace Mcp

    ''' <summary>
    ''' Built-in "data" MCP server ("Command": "{self}", "Arguments": ["--mcp-data", "--roots", "{data}\shared;{data}\workspace"]):
    ''' structured data - JSON, YAML, TOML, XML and CSV: check, pretty-print, convert, query with JSONPath, check against a JSON
    ''' Schema and compare - plus statistics, finance math, charts (SVG/PNG) and Mermaid diagrams. Inputs are text or a file path
    ''' inside the allowed folders.
    ''' </summary>
    Partial Public NotInheritable Class DataServer
        Private ReadOnly _roots As PathRoots
        Private ReadOnly _node As String
        Private ReadOnly _mermaidCli As String
        Private ReadOnly _chrome As String
        Private Shared ReadOnly Inv As CultureInfo = CultureInfo.InvariantCulture
        Private Const MaxInputBytes As Long = 20L * 1024 * 1024

        Friend Sub New(roots As PathRoots, Optional node As String = Nothing, Optional mermaidCli As String = Nothing, Optional chrome As String = Nothing)
            _roots = roots
            _node = node
            _mermaidCli = mermaidCli
            _chrome = chrome
        End Sub

        Public Shared Function RunStdio(args As String()) As Integer
            Dim roots = ToolKit.SplitList(If(ToolKit.Arg(args, "--roots"), Path.Combine(AppContext.BaseDirectory, "data", "shared")))
            Dim server As New DataServer(New PathRoots(roots), ToolKit.Arg(args, "--node"), ToolKit.Arg(args, "--mermaid-cli"), ToolKit.Arg(args, "--chrome"))
            Return BuiltInServer.Run("agentic-data", server.CreateTools())
        End Function

        Public Function CreateTools() As McpServerPrimitiveCollection(Of McpServerTool)
            Dim tools As New McpServerPrimitiveCollection(Of McpServerTool)()
            Dim src = $" source: the data itself, or the path of a file in {_roots.Describe()}. format: json, yaml, toml, xml or csv (default: from the file name or the content)."
            ToolKit.Add(tools, New Func(Of String, String, String)(AddressOf ValidateData), "validate_data",
                "Check that data parses, and say where it does not (line and column)." & src)
            ToolKit.Add(tools, New Func(Of String, String, Boolean, String)(AddressOf FormatData), "format_data",
                "Pretty-print data in its own format (or minify JSON)." & src)
            ToolKit.Add(tools, New Func(Of String, String, String, String, String)(AddressOf ConvertData), "convert_data",
                "Convert data between JSON, YAML, TOML, XML and CSV (to); optionally save it to output_path." & src, False)
            ToolKit.Add(tools, New Func(Of String, String, String, String)(AddressOf QueryData), "query_data",
                "Query data with JSONPath ($.store.book[*].author, $..price, $.items[?@.qty > 5]) and get each match with its location." & src)
            ToolKit.Add(tools, New Func(Of String, String, String, String)(AddressOf ValidateSchema), "validate_schema",
                "Check data against a JSON Schema (draft 2020-12/2019-09/7/6; the schema as text or a file path): valid, or every error with its location." & src)
            ToolKit.Add(tools, New Func(Of String, String, String, String)(AddressOf DiffData), "diff_data",
                "Compare two data documents (any of the formats, also mixed) by structure: added, removed and changed values with their paths.")
            AddMathTools(tools)
            AddChartTools(tools)
            Return tools
        End Function

        ' ------------------------------------------------------------------------------------------ input

        ''' <summary>Text from a file in the allowed folders (when source names one), else source itself; and the format hint from the file name.</summary>
        Friend Function ReadSource(source As String) As (Text As String, FileName As String)
            Dim s = If(source, "")
            If s.Length > 0 AndAlso s.Length < 400 AndAlso Not s.Contains(ControlChars.Lf) AndAlso Regex.IsMatch(s.Trim(), "\.[A-Za-z0-9]{1,6}$") Then
                Dim full As String = Nothing
                Try
                    full = _roots.Resolve(s.Trim())
                Catch ex As McpException
                End Try
                If full IsNot Nothing AndAlso File.Exists(full) Then
                    If New FileInfo(full).Length > MaxInputBytes Then Throw New McpException($"'{s.Trim()}' is larger than {MaxInputBytes \ (1024 * 1024)} MB.")
                    Return (File.ReadAllText(full), Path.GetFileName(full))
                End If
            End If
            If s.Trim().Length = 0 Then Throw New McpException("source is required (data text or a file path).")
            Return (s, Nothing)
        End Function

        Friend Shared Function DetectFormat(text As String, fileName As String, hint As String) As String
            Dim h = If(hint, "").Trim().ToLowerInvariant().TrimStart("."c)
            If h = "yml" Then h = "yaml"
            If {"json", "yaml", "toml", "xml", "csv"}.Contains(h) Then Return h
            If h.Length > 0 Then Throw New McpException($"Unknown format '{hint}'. Use json, yaml, toml, xml or csv.")
            Select Case Path.GetExtension(If(fileName, "")).ToLowerInvariant()
                Case ".json", ".jsonc", ".geojson" : Return "json"
                Case ".yaml", ".yml" : Return "yaml"
                Case ".toml" : Return "toml"
                Case ".xml", ".csproj", ".vbproj", ".props", ".config", ".svg", ".xaml", ".plist" : Return "xml"
                Case ".csv", ".tsv" : Return "csv"
            End Select
            Dim t = text.TrimStart(ChrW(&HFEFF)).TrimStart()
            If t.StartsWith("{") OrElse t.StartsWith("[") AndAlso Not Regex.IsMatch(t, "^\[[\w.\- ""]+\]\s*\n") Then Return "json"
            If t.StartsWith("<") Then Return "xml"
            If Regex.IsMatch(t, "^\s*\[[\w.\-""]+\]\s*$", RegexOptions.Multiline) AndAlso Regex.IsMatch(t, "^\s*[\w.\-""]+\s*=", RegexOptions.Multiline) Then Return "toml"
            If Regex.IsMatch(t, "^\s*[\w.\-""]+\s*=\s*\S", RegexOptions.Multiline) AndAlso Not Regex.IsMatch(t, "^\s*[\w.\-""]+\s*:\s", RegexOptions.Multiline) Then Return "toml"
            Dim firstLines = t.Split(ControlChars.Lf).Where(Function(l) l.Trim().Length > 0).Take(5).ToList()
            If firstLines.Count >= 2 AndAlso firstLines.All(Function(l) l.Contains(","c)) AndAlso Not t.Contains(": ") Then Return "csv"
            Return "yaml"
        End Function

        ' ------------------------------------------------------------------------------------------ parse to JSON nodes

        Friend Shared Function Parse(text As String, format As String) As JsonNode
            Try
                Select Case format
                    Case "json"
                        Return JsonNode.Parse(text.TrimStart(ChrW(&HFEFF)), Nothing, New JsonDocumentOptions With {.CommentHandling = JsonCommentHandling.Skip, .AllowTrailingCommas = True})
                    Case "yaml" : Return ParseYaml(text)
                    Case "toml" : Return FromToml(Tomlyn.TomlSerializer.Deserialize(Of Tomlyn.Model.TomlTable)(text))
                    Case "xml" : Return FromXml(XDocument.Parse(text).Root, True)
                    Case "csv" : Return FromCsv(text)
                End Select
            Catch ex As JsonException
                Throw New McpException($"Invalid JSON: {ex.Message}")
            Catch ex As YamlDotNet.Core.YamlException
                Throw New McpException($"Invalid YAML at line {ex.Start.Line}, column {ex.Start.Column}: {If(ex.InnerException?.Message, ex.Message)}")
            Catch ex As Tomlyn.TomlException
                Throw New McpException($"Invalid TOML: {ex.Message}")
            Catch ex As Xml.XmlException
                Throw New McpException($"Invalid XML at line {ex.LineNumber}, column {ex.LinePosition}: {ex.Message}")
            Catch ex As InvalidOperationException When format = "yaml"
                ' YamlDotNet's scanner can fail this way on unbalanced brackets: say where it stopped.
                Throw New McpException($"Invalid YAML{YamlStopPosition(text)}: check brackets, quotes and indentation there.")
            End Try
            Throw New McpException($"Unknown format '{format}'.")
        End Function

        ''' <summary>" at line L, column C" where the YAML scanner stops on broken text ("" when it cannot tell).</summary>
        Private Shared Function YamlStopPosition(text As String) As String
            Dim scanner As New YamlDotNet.Core.Scanner(New StringReader(text))
            Try
                While scanner.MoveNext()
                End While
            Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is YamlDotNet.Core.YamlException
                Return $" at line {scanner.CurrentPosition.Line}, column {scanner.CurrentPosition.Column}"
            End Try
            Return ""
        End Function

        Private Shared Function ParseYaml(text As String) As JsonNode
            Dim stream As New YamlDotNet.RepresentationModel.YamlStream()
            stream.Load(New StringReader(text))
            If stream.Documents.Count = 0 Then Return Nothing
            If stream.Documents.Count = 1 Then Return FromYaml(stream.Documents(0).RootNode)
            Return New JsonArray(stream.Documents.Select(Function(d) FromYaml(d.RootNode)).ToArray())
        End Function

        Private Shared Function FromYaml(n As YamlDotNet.RepresentationModel.YamlNode) As JsonNode
            Dim map = TryCast(n, YamlDotNet.RepresentationModel.YamlMappingNode)
            If map IsNot Nothing Then
                Dim o As New JsonObject()
                For Each kv In map.Children
                    o(CStr(DirectCast(kv.Key, YamlDotNet.RepresentationModel.YamlScalarNode).Value)) = FromYaml(kv.Value)
                Next
                Return o
            End If
            Dim seq = TryCast(n, YamlDotNet.RepresentationModel.YamlSequenceNode)
            If seq IsNot Nothing Then Return New JsonArray(seq.Children.Select(Function(c) FromYaml(c)).ToArray())
            Dim sc = TryCast(n, YamlDotNet.RepresentationModel.YamlScalarNode)
            If sc Is Nothing Then Return Nothing
            If sc.Style <> YamlDotNet.Core.ScalarStyle.Plain Then Return JsonValue.Create(sc.Value)
            Return TypedScalar(sc.Value, True)
        End Function

        ''' <summary>A plain value as JSON: null, true/false, a number, or the text.</summary>
        Friend Shared Function TypedScalar(v As String, yaml As Boolean) As JsonNode
            If v Is Nothing Then Return Nothing
            If yaml AndAlso (v = "" OrElse v = "~" OrElse v.Equals("null", StringComparison.OrdinalIgnoreCase)) Then Return Nothing
            If v.Equals("true", StringComparison.OrdinalIgnoreCase) Then Return JsonValue.Create(True)
            If v.Equals("false", StringComparison.OrdinalIgnoreCase) Then Return JsonValue.Create(False)
            If Regex.IsMatch(v, "^-?(0|[1-9]\d{0,17})$") Then Return JsonValue.Create(Long.Parse(v, Inv))
            If Regex.IsMatch(v, "^-?(0|[1-9]\d*)(\.\d+)?([eE][+-]?\d+)?$") Then
                Dim d As Decimal
                If Decimal.TryParse(v, NumberStyles.Float, Inv, d) Then Return JsonValue.Create(d)
                Return JsonValue.Create(Double.Parse(v, NumberStyles.Float, Inv))
            End If
            Return JsonValue.Create(v)
        End Function

        Private Shared Function FromToml(v As Object) As JsonNode
            Select Case True
                Case v Is Nothing : Return Nothing
                Case TypeOf v Is Tomlyn.Model.TomlTable
                    Dim o As New JsonObject()
                    For Each kv In DirectCast(v, Tomlyn.Model.TomlTable)
                        o(kv.Key) = FromToml(kv.Value)
                    Next
                    Return o
                Case TypeOf v Is Tomlyn.Model.TomlTableArray
                    Return New JsonArray(DirectCast(v, Tomlyn.Model.TomlTableArray).Select(Function(t) FromToml(t)).ToArray())
                Case TypeOf v Is Tomlyn.Model.TomlArray
                    Return New JsonArray(DirectCast(v, Tomlyn.Model.TomlArray).Select(Function(x) FromToml(x)).ToArray())
                Case TypeOf v Is String : Return JsonValue.Create(DirectCast(v, String))
                Case TypeOf v Is Boolean : Return JsonValue.Create(DirectCast(v, Boolean))
                Case TypeOf v Is Long : Return JsonValue.Create(DirectCast(v, Long))
                Case TypeOf v Is Integer : Return JsonValue.Create(DirectCast(v, Integer))
                Case TypeOf v Is Double : Return JsonValue.Create(DirectCast(v, Double))
                Case TypeOf v Is DateTime : Return JsonValue.Create(DirectCast(v, DateTime).ToString("o", Inv))
                Case TypeOf v Is DateTimeOffset : Return JsonValue.Create(DirectCast(v, DateTimeOffset).ToString("o", Inv))
            End Select
            Return JsonValue.Create(Convert.ToString(v, Inv))
        End Function

        Private Shared Function FromXml(el As XElement, isRoot As Boolean) As JsonNode
            Dim hasChildren = el.HasElements
            Dim attrs = el.Attributes().Where(Function(a) Not a.IsNamespaceDeclaration).ToList()
            If Not hasChildren AndAlso attrs.Count = 0 Then
                Dim leaf = TypedScalar(el.Value, False)
                Return If(isRoot, New JsonObject From {{el.Name.LocalName, leaf}}, leaf)
            End If
            Dim o As New JsonObject()
            For Each a In attrs
                o("@" & a.Name.LocalName) = TypedScalar(a.Value, False)
            Next
            For Each g In el.Elements().GroupBy(Function(c) c.Name.LocalName)
                Dim items = g.ToList()
                o(g.Key) = If(items.Count = 1, FromXml(items(0), False), New JsonArray(items.Select(Function(c) FromXml(c, False)).ToArray()))
            Next
            Dim text = String.Concat(el.Nodes().OfType(Of XText)().Select(Function(t) t.Value)).Trim()
            If text.Length > 0 Then o("#text") = TypedScalar(text, False)
            Return If(isRoot, New JsonObject From {{el.Name.LocalName, o}}, CType(o, JsonNode))
        End Function

        Private Shared Function FromCsv(text As String) As JsonNode
            Dim rows = TableText.ParseCsv(text, If(text.Split(ControlChars.Lf)(0).Contains(ControlChars.Tab) AndAlso Not text.Split(ControlChars.Lf)(0).Contains(","c), ControlChars.Tab, ","c))
            If rows.Count = 0 Then Return New JsonArray()
            Dim header = rows(0).Select(Function(h, i) If(String.IsNullOrWhiteSpace(h), "column" & (i + 1).ToString(Inv), h.Trim())).ToList()
            Dim arr As New JsonArray()
            For Each r In rows.Skip(1)
                If r.All(Function(c) c.Length = 0) Then Continue For
                Dim o As New JsonObject()
                For i = 0 To header.Count - 1
                    Dim cell = If(i < r.Count, r(i), "")
                    o(header(i)) = If(cell = "", JsonValue.Create(""), TypedScalar(cell, False))
                Next
                arr.Add(o)
            Next
            Return arr
        End Function

        ' ------------------------------------------------------------------------------------------ JSON nodes to text

        Friend Shared Function Serialize(node As JsonNode, format As String, Optional minify As Boolean = False) As String
            Select Case format
                Case "json"
                    Return If(node Is Nothing, "null", node.ToJsonString(New JsonSerializerOptions With {.WriteIndented = Not minify, .Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}))
                Case "yaml"
                    Dim serializer = New YamlDotNet.Serialization.SerializerBuilder().WithQuotingNecessaryStrings().Build()
                    Return serializer.Serialize(ToPlain(node))
                Case "toml"
                    Dim o = TryCast(node, JsonObject)
                    If o Is Nothing Then Throw New McpException("TOML needs an object (key/value table) at the top, not a list or a single value.")
                    Return Tomlyn.TomlSerializer.Serialize(ToToml(o))
                Case "xml"
                    Dim o = TryCast(node, JsonObject)
                    Dim root As XElement
                    If o IsNot Nothing AndAlso o.Count = 1 AndAlso TypeOf o.First().Value Is JsonObject Then
                        root = ToXml(XmlName(o.First().Key), o.First().Value)
                    Else
                        root = ToXml("root", node)
                    End If
                    Return New XDocument(New XDeclaration("1.0", "utf-8", Nothing), root).Declaration.ToString() & vbLf & root.ToString()
                Case "csv" : Return ToCsvText(node)
            End Select
            Throw New McpException($"Unknown format '{format}'.")
        End Function

        ''' <summary>Dictionaries, lists and plain values (for YAML).</summary>
        Private Shared Function ToPlain(n As JsonNode) As Object
            Select Case True
                Case n Is Nothing : Return Nothing
                Case TypeOf n Is JsonObject
                    Dim d As New Dictionary(Of String, Object)
                    For Each kv In DirectCast(n, JsonObject)
                        d(kv.Key) = ToPlain(kv.Value)
                    Next
                    Return d
                Case TypeOf n Is JsonArray : Return DirectCast(n, JsonArray).Select(Function(x) ToPlain(x)).ToList()
            End Select
            Return ScalarValue(n.AsValue())
        End Function

        Private Shared Function ScalarValue(v As JsonValue) As Object
            Select Case v.GetValueKind()
                Case JsonValueKind.String : Return v.GetValue(Of String)()
                Case JsonValueKind.True : Return True
                Case JsonValueKind.False : Return False
                Case JsonValueKind.Number
                    Dim l As Long
                    If v.TryGetValue(l) Then Return l
                    Dim dec As Decimal
                    If v.TryGetValue(dec) Then Return dec
                    Dim raw = v.ToJsonString()
                    If Long.TryParse(raw, NumberStyles.Integer, Inv, l) Then Return l
                    If Decimal.TryParse(raw, NumberStyles.Float, Inv, dec) Then Return dec
                    Return Double.Parse(raw, NumberStyles.Float, Inv)
            End Select
            Return Nothing
        End Function

        Private Shared Function ToToml(o As JsonObject) As Tomlyn.Model.TomlTable
            Dim t As New Tomlyn.Model.TomlTable()
            For Each kv In o
                If kv.Value Is Nothing Then Continue For
                t(kv.Key) = ToTomlValue(kv.Value)
            Next
            Return t
        End Function

        Private Shared Function ToTomlValue(n As JsonNode) As Object
            Dim o = TryCast(n, JsonObject)
            If o IsNot Nothing Then Return ToToml(o)
            Dim a = TryCast(n, JsonArray)
            If a IsNot Nothing Then
                If a.Count > 0 AndAlso a.All(Function(x) TypeOf x Is JsonObject) Then
                    Dim ta As New Tomlyn.Model.TomlTableArray()
                    For Each x In a
                        ta.Add(ToToml(DirectCast(x, JsonObject)))
                    Next
                    Return ta
                End If
                Dim arr As New Tomlyn.Model.TomlArray()
                For Each x In a
                    If x IsNot Nothing Then arr.Add(ToTomlValue(x))
                Next
                Return arr
            End If
            Dim v = ScalarValue(n.AsValue())
            If TypeOf v Is Decimal Then Return CDbl(DirectCast(v, Decimal))
            Return v
        End Function

        Private Shared Function XmlName(s As String) As String
            Dim n = Regex.Replace(If(s, "item"), "[^\w.\-]", "_")
            If n.Length = 0 OrElse Not (Char.IsLetter(n(0)) OrElse n(0) = "_"c) Then n = "_" & n
            Return n
        End Function

        Private Shared Function ToXml(name As String, n As JsonNode) As XElement
            Dim el As New XElement(XmlName(name))
            Dim o = TryCast(n, JsonObject)
            If o IsNot Nothing Then
                For Each kv In o
                    If kv.Key.StartsWith("@") Then
                        el.SetAttributeValue(XmlName(kv.Key.Substring(1)), If(kv.Value Is Nothing, "", Plain(kv.Value)))
                    ElseIf kv.Key = "#text" Then
                        el.Add(New XText(If(kv.Value Is Nothing, "", Plain(kv.Value))))
                    ElseIf TypeOf kv.Value Is JsonArray Then
                        For Each item In DirectCast(kv.Value, JsonArray)
                            el.Add(ToXml(kv.Key, item))
                        Next
                    Else
                        el.Add(ToXml(kv.Key, kv.Value))
                    End If
                Next
                Return el
            End If
            Dim a = TryCast(n, JsonArray)
            If a IsNot Nothing Then
                For Each item In a
                    el.Add(ToXml("item", item))
                Next
                Return el
            End If
            If n IsNot Nothing Then el.Value = Plain(n)
            Return el
        End Function

        ''' <summary>A value as plain text (strings without quotes, the rest as JSON).</summary>
        Friend Shared Function Plain(n As JsonNode) As String
            If n Is Nothing Then Return ""
            If TypeOf n Is JsonValue AndAlso n.GetValueKind() = JsonValueKind.String Then Return n.GetValue(Of String)()
            Return n.ToJsonString()
        End Function

        Private Shared Function ToCsvText(n As JsonNode) As String
            Dim arr = TryCast(n, JsonArray)
            If arr Is Nothing Then
                Dim o = TryCast(n, JsonObject)
                ' One object holding one list (e.g. {"items": [...]}) becomes that list.
                If o IsNot Nothing AndAlso o.Count = 1 AndAlso TypeOf o.First().Value Is JsonArray Then arr = DirectCast(o.First().Value, JsonArray)
            End If
            If arr Is Nothing Then Throw New McpException("CSV needs a list of rows (an array of objects or of arrays).")
            Dim rows As New List(Of List(Of String))
            If arr.All(Function(x) TypeOf x Is JsonObject) Then
                Dim header As New List(Of String)
                For Each x In arr
                    For Each kv In DirectCast(x, JsonObject)
                        If Not header.Contains(kv.Key) Then header.Add(kv.Key)
                    Next
                Next
                rows.Add(header)
                For Each x In arr
                    Dim o = DirectCast(x, JsonObject)
                    rows.Add(header.Select(Function(h) If(o.ContainsKey(h), Plain(o(h)), "")).ToList())
                Next
            Else
                For Each x In arr
                    Dim inner = TryCast(x, JsonArray)
                    rows.Add(If(inner IsNot Nothing, inner.Select(Function(c) Plain(c)).ToList(), New List(Of String) From {Plain(x)}))
                Next
            End If
            Return TableText.ToCsv(rows)
        End Function

        ' ------------------------------------------------------------------------------------------ tools

        Private Function Load(source As String, format As String) As (Node As JsonNode, Format As String, FileName As String)
            Dim src = ReadSource(source)
            Dim f = DetectFormat(src.Text, src.FileName, format)
            Return (Parse(src.Text, f), f, src.FileName)
        End Function

        Private Shared Function Shape(n As JsonNode) As String
            Select Case True
                Case n Is Nothing : Return "null"
                Case TypeOf n Is JsonObject : Return $"an object with {DirectCast(n, JsonObject).Count} key(s): {String.Join(", ", DirectCast(n, JsonObject).Take(12).Select(Function(kv) kv.Key))}{If(DirectCast(n, JsonObject).Count > 12, ", ...", "")}"
                Case TypeOf n Is JsonArray : Return $"a list of {DirectCast(n, JsonArray).Count} item(s)"
            End Select
            Return "a single " & n.GetValueKind().ToString().ToLowerInvariant() & " value"
        End Function

        Friend Function ValidateData(<Description("Data text or file path")> source As String,
                                     <Description("json, yaml, toml, xml or csv (optional)")> Optional format As String = "") As String
            Dim d = Load(source, format)
            Return $"Valid {d.Format.ToUpperInvariant()}{If(d.FileName IsNot Nothing, $" ({d.FileName})", "")}: {Shape(d.Node)}."
        End Function

        Friend Function FormatData(<Description("Data text or file path")> source As String,
                                   <Description("json, yaml, toml, xml or csv (optional)")> Optional format As String = "",
                                   <Description("JSON only: one compact line")> Optional minify As Boolean = False) As String
            Dim d = Load(source, format)
            If d.Format = "xml" Then
                Dim src = ReadSource(source)
                Return ToolKit.Clip(XDocument.Parse(src.Text).ToString(), 200000)
            End If
            Return ToolKit.Clip(Serialize(d.Node, d.Format, minify), 200000)
        End Function

        Friend Function ConvertData(<Description("Data text or file path")> source As String,
                                    <Description("Target format: json, yaml, toml, xml or csv")> [to] As String,
                                    <Description("Source format (optional)")> Optional from As String = "",
                                    <Description("Save the result here (optional; else it is returned)")> Optional output_path As String = "") As String
            Dim d = Load(source, from)
            Dim target = DetectFormat("", Nothing, [to])
            Dim text = Serialize(d.Node, target)
            If String.IsNullOrWhiteSpace(output_path) Then Return ToolKit.Clip(text, 200000)
            Dim full = _roots.Resolve(output_path)
            Directory.CreateDirectory(Path.GetDirectoryName(full))
            File.WriteAllText(full, text, New UTF8Encoding(False))
            Return $"Wrote {_roots.Show(full)} ({d.Format.ToUpperInvariant()} to {target.ToUpperInvariant()}, {text.Length:N0} characters)."
        End Function

        Friend Function QueryData(<Description("Data text or file path")> source As String,
                                  <Description("JSONPath, e.g. $.items[*].name or $..price")> path As String,
                                  <Description("json, yaml, toml, xml or csv (optional)")> Optional format As String = "") As String
            Dim d = Load(source, format)
            Dim jp As Json.Path.JsonPath = Nothing
            If Not Json.Path.JsonPath.TryParse(If(path, "").Trim(), jp) Then Throw New McpException($"'{path}' is not a valid JSONPath (it starts with $, e.g. $.items[0].name).")
            Dim result = jp.Evaluate(d.Node)
            Dim matches = If(result.Matches, Nothing)
            If matches Is Nothing OrElse matches.Count = 0 Then Return "No matches."
            Dim sb As New StringBuilder($"{matches.Count} match(es):" & vbLf)
            For Each m In matches.Take(500)
                sb.AppendLine($"{m.Location}: {If(m.Value Is Nothing, "null", ToolKit.Clip(m.Value.ToJsonString(), 2000))}")
            Next
            Return ToolKit.Clip(sb.ToString().TrimEnd(), 200000)
        End Function

        Friend Function ValidateSchema(<Description("Data text or file path")> source As String,
                                       <Description("The JSON Schema (text or file path; YAML works too)")> schema As String,
                                       <Description("Data format (optional)")> Optional format As String = "") As String
            Dim d = Load(source, format)
            Dim s = ReadSource(schema)
            Dim schemaNode = Parse(s.Text, DetectFormat(s.Text, s.FileName, ""))
            Dim js As Json.Schema.JsonSchema
            Try
                js = Json.Schema.JsonSchema.FromText(schemaNode.ToJsonString())
            Catch ex As Exception When TypeOf ex Is JsonException OrElse TypeOf ex Is ArgumentException OrElse TypeOf ex Is InvalidOperationException
                Throw New McpException("The schema is not valid: " & ex.Message)
            End Try
            Using doc = JsonDocument.Parse(If(d.Node Is Nothing, "null", d.Node.ToJsonString()))
                Dim r = js.Evaluate(doc.RootElement, New Json.Schema.EvaluationOptions With {.OutputFormat = Json.Schema.OutputFormat.List})
                If r.IsValid Then Return "Valid: the data matches the schema."
                Dim errors As New List(Of String)
                Dim walk As Action(Of Json.Schema.EvaluationResults) = Nothing
                walk = Sub(res)
                           If res.Errors IsNot Nothing Then
                               For Each kv In res.Errors
                                   errors.Add($"{res.InstanceLocation} ({kv.Key}): {kv.Value}")
                               Next
                           End If
                           If res.Details IsNot Nothing Then
                               For Each child In res.Details
                                   walk(child)
                               Next
                           End If
                       End Sub
                walk(r)
                Dim distinctErrors = errors.Distinct().ToList()
                Return $"Not valid: {distinctErrors.Count} error(s):" & vbLf & String.Join(vbLf, distinctErrors.Take(100).Select(Function(e) "- " & If(e.StartsWith(" "), "(root)" & e, e)))
            End Using
        End Function

        Friend Function DiffData(<Description("First document (text or file path)")> a As String,
                                 <Description("Second document (text or file path)")> b As String,
                                 <Description("Format of both (optional; detected per document)")> Optional format As String = "") As String
            Dim x = Load(a, format), y = Load(b, format)
            Dim changes As New List(Of String)
            CompareNodes("$", x.Node, y.Node, changes)
            If changes.Count = 0 Then Return "No differences: the documents hold the same data."
            Return $"{changes.Count} difference(s) (+ added in the second, - removed, ~ changed):" & vbLf & String.Join(vbLf, changes.Take(500)) &
                   If(changes.Count > 500, vbLf & "(first 500 shown)", "")
        End Function

        Private Shared Function Brief(n As JsonNode) As String
            Return If(n Is Nothing, "null", ToolKit.Clip(n.ToJsonString(), 200))
        End Function

        Friend Shared Sub CompareNodes(path As String, a As JsonNode, b As JsonNode, changes As List(Of String))
            If changes.Count > 2000 Then Return
            Dim oa = TryCast(a, JsonObject), ob = TryCast(b, JsonObject)
            If oa IsNot Nothing AndAlso ob IsNot Nothing Then
                For Each kv In oa
                    Dim p = path & If(Regex.IsMatch(kv.Key, "^[A-Za-z_]\w*$"), "." & kv.Key, "['" & kv.Key.Replace("'", "\'") & "']")
                    If Not ob.ContainsKey(kv.Key) Then
                        changes.Add($"- {p}: {Brief(kv.Value)}")
                    Else
                        CompareNodes(p, kv.Value, ob(kv.Key), changes)
                    End If
                Next
                For Each kv In ob
                    If Not oa.ContainsKey(kv.Key) Then changes.Add($"+ {path}{If(Regex.IsMatch(kv.Key, "^[A-Za-z_]\w*$"), "." & kv.Key, "['" & kv.Key & "']")}: {Brief(kv.Value)}")
                Next
                Return
            End If
            Dim aa = TryCast(a, JsonArray), ab = TryCast(b, JsonArray)
            If aa IsNot Nothing AndAlso ab IsNot Nothing Then
                For i = 0 To Math.Max(aa.Count, ab.Count) - 1
                    If i >= ab.Count Then
                        changes.Add($"- {path}[{i}]: {Brief(aa(i))}")
                    ElseIf i >= aa.Count Then
                        changes.Add($"+ {path}[{i}]: {Brief(ab(i))}")
                    Else
                        CompareNodes($"{path}[{i}]", aa(i), ab(i), changes)
                    End If
                Next
                Return
            End If
            If Not JsonNode.DeepEquals(a, b) Then
                ' 1 and 1.0 (or "1" from CSV/XML against 1) are the same value written differently: still reported, marked.
                Dim same = a IsNot Nothing AndAlso b IsNot Nothing AndAlso Plain(a) = Plain(b)
                changes.Add($"~ {path}: {Brief(a)} -> {Brief(b)}{If(same, " (same text, different type)", "")}")
            End If
        End Sub
    End Class

End Namespace
