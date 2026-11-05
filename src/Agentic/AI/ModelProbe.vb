Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.IO.Compression
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.Configuration
Imports Agentic.Utilities

Namespace AI

    ''' <summary>
    ''' --probe-models: verifies every configured model against the live provider (listed?, chat, native tool calls,
    ''' vision on a generated red/blue test image) and optionally one image generation. Prints a table.
    ''' </summary>
    Public Module ModelProbe

        Public Async Function RunAsync(o As HostOptions, includeImage As Boolean) As Task(Of Integer)
            Dim providers As New Dictionary(Of String, OpenAICompatibleProvider)(StringComparer.OrdinalIgnoreCase)
            For Each kv In o.AIProviders
                providers(kv.Key) = New OpenAICompatibleProvider(kv.Key, kv.Value)
            Next
            Dim listed As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each kv In providers
                Try
                    For Each id In Await kv.Value.ListModelsAsync(CancellationToken.None)
                        listed.Add(id)
                    Next
                    Console.WriteLine($"Provider {kv.Key}: reachable, {listed.Count} models listed.")
                Catch ex As Exception
                    Console.WriteLine($"Provider {kv.Key}: NOT reachable - {ex.Message}")
                End Try
            Next
            Console.WriteLine()
            Console.WriteLine($"{"Model key",-16} {"Model id",-30} {"Listed",-7} {"Chat",-12} {"Tools",-8} {"Vision",-14}")
            Console.WriteLine(New String("-"c, 92))
            Dim image = "data:image/png;base64," & Convert.ToBase64String(RedBluePng(64, 64))
            Dim failures = 0
            Dim tasks = o.Models.Where(Function(m) m.Enabled).Select(Function(m) ProbeOneAsync(m, providers, listed, image)).ToList()
            Dim rows = Await Task.WhenAll(tasks)
            For Each row In rows
                Console.WriteLine(row.Line)
                If Not row.Ok Then failures += 1
            Next

            ' Pictures come only from the images API in ImageGeneration (never from the chat provider), like at runtime.
            If includeImage AndAlso o.ImageGeneration.Enabled Then
                Console.WriteLine()
                Using api = ImageRouter.CreateApi(o.ImageGeneration)
                    Dim router = ImageRouter.ForApi(o.ImageGeneration, api)
                    If Not router.Enabled Then
                        failures += 1
                        Console.WriteLine("Image generation: NOT SET UP - ImageGeneration.BaseUrl and ApiKey (or AGENTIC_ImageGeneration__ApiKey) are needed.")
                    Else
                        Try
                            Dim sw = Stopwatch.StartNew()
                            Dim made = Await router.GenerateAsync("A small red circle on a white background, flat illustration", o.ImageGeneration.DefaultSize, CancellationToken.None)
                            Dim outFile = Path.Combine(Path.GetTempPath(), $"agentic-probe-{DateTime.UtcNow:yyyyMMddHHmmss}.png")
                            IO.File.WriteAllBytes(outFile, made.Result.Data)
                            Console.WriteLine($"Image generation: OK with {made.Model} ({made.Result.Data.Length \ 1024} KB, {made.Result.MimeType}) in {sw.ElapsedMilliseconds} ms -> {outFile}")
                            For Each f In made.Failures
                                Console.WriteLine("  failed first: " & f)
                            Next
                        Catch ex As AiException
                            failures += 1
                            Console.WriteLine("Image generation: FAILED - " & TextUtil.Truncate(ex.Message, 300))
                        End Try
                    End If
                End Using
            End If
            For Each p In providers.Values
                p.Dispose()
            Next
            Console.WriteLine()
            Console.WriteLine(If(failures = 0, "All probes passed.", $"{failures} model(s) had problems. Fallbacks/circuit breakers will route around unavailable models at runtime."))
            Return If(failures = 0, 0, 1)
        End Function

        Private Async Function ProbeOneAsync(m As ModelOptions, providers As Dictionary(Of String, OpenAICompatibleProvider), listed As HashSet(Of String), imageUri As String) As Task(Of (Line As String, Ok As Boolean))
            Dim provider As OpenAICompatibleProvider = Nothing
            If Not providers.TryGetValue(m.Provider, provider) Then Return ($"{m.Key,-16} provider '{m.Provider}' missing", False)
            Dim isListed = listed.Contains(m.ModelId)
            Dim chat = "-"
            Dim tools = "-"
            Dim vision = "n/a"
            Dim ok = True
            Dim timeout = TimeSpan.FromSeconds(Math.Min(120, Math.Max(30, m.TimeoutSeconds)))
            Try
                Dim sw = Stopwatch.StartNew()
                Dim r = Await provider.ChatAsync(New ChatRequest With {.ModelId = m.ModelId, .MaxTokens = 400, .Timeout = timeout,
                                                                      .Messages = New List(Of ChatMessage) From {ChatMessage.ForUser("Reply with exactly: OK")}}, CancellationToken.None)
                chat = If(String.IsNullOrWhiteSpace(r.Content), "EMPTY", $"OK {sw.ElapsedMilliseconds}ms")
                If String.IsNullOrWhiteSpace(r.Content) Then ok = False
            Catch ex As AiException
                chat = ex.Kind.ToString()
                ok = False
            End Try
            If ok AndAlso m.Capabilities.Contains(ModelCapability.ToolCalling) Then
                Try
                    Dim def As New ToolDefinition With {.Name = "get_ticket_status", .Description = "Get the status of a ticket",
                        .Parameters = Schema.Obj({"ticket_id"}, Schema.Prop("ticket_id", Schema.Str("Ticket id")))}
                    Dim r = Await provider.ChatAsync(New ChatRequest With {.ModelId = m.ModelId, .MaxTokens = 400, .Timeout = timeout,
                                                                          .Tools = New List(Of ToolDefinition) From {def},
                                                                          .Messages = New List(Of ChatMessage) From {ChatMessage.ForUser("Look up the status of ticket 4711 using the tool.")}}, CancellationToken.None)
                    tools = If(r.HasToolCalls AndAlso r.ToolCalls(0).Name = "get_ticket_status", "OK", "NO CALL")
                    If tools <> "OK" Then ok = False
                Catch ex As AiException
                    tools = ex.Kind.ToString()
                    ok = False
                End Try
            End If
            If m.Capabilities.Contains(ModelCapability.Vision) Then
                Try
                    Dim msg As New ChatMessage With {.Role = "user", .Content = "The left half and right half of this image are each a solid color. Name the two colors, left then right, in at most 4 words.",
                                                     .ImageUrls = New List(Of String) From {imageUri}}
                    Dim r = Await provider.ChatAsync(New ChatRequest With {.ModelId = m.ModelId, .MaxTokens = 600, .Timeout = timeout, .Messages = New List(Of ChatMessage) From {msg}}, CancellationToken.None)
                    Dim text = If(r.Content, "").ToLowerInvariant()
                    Dim redIdx = text.IndexOf("red", StringComparison.Ordinal)
                    Dim blueIdx = text.IndexOf("blue", StringComparison.Ordinal)
                    vision = If(redIdx >= 0 AndAlso blueIdx > redIdx, "OK", "WRONG: " & TextUtil.Truncate(r.Content, 20))
                    If vision <> "OK" Then ok = False
                Catch ex As AiException
                    vision = ex.Kind.ToString()
                    ok = False
                End Try
            End If
            Dim line = $"{m.Key,-16} {TextUtil.Truncate(m.ModelId, 30),-30} {If(isListed, "yes", "NO"),-7} {chat,-12} {tools,-8} {vision,-14}"
            Return (line, ok)
        End Function

        ''' <summary>Encodes a minimal RGB PNG: left half red, right half blue.</summary>
        Public Function RedBluePng(width As Integer, height As Integer) As Byte()
            Dim raw As New MemoryStream()
            For y = 0 To height - 1
                raw.WriteByte(0)
                For x = 0 To width - 1
                    If x < width \ 2 Then
                        raw.WriteByte(255) : raw.WriteByte(0) : raw.WriteByte(0)
                    Else
                        raw.WriteByte(0) : raw.WriteByte(0) : raw.WriteByte(255)
                    End If
                Next
            Next
            Dim compressed As New MemoryStream()
            Using z As New ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen:=True)
                raw.Position = 0
                raw.CopyTo(z)
            End Using
            Dim png As New MemoryStream()
            png.Write({137, 80, 78, 71, 13, 10, 26, 10}, 0, 8)
            Dim ihdr As New MemoryStream()
            WriteBE(ihdr, width) : WriteBE(ihdr, height)
            ihdr.Write({8, 2, 0, 0, 0}, 0, 5)
            WriteChunk(png, "IHDR", ihdr.ToArray())
            WriteChunk(png, "IDAT", compressed.ToArray())
            WriteChunk(png, "IEND", Array.Empty(Of Byte)())
            Return png.ToArray()
        End Function

        Private Sub WriteBE(s As Stream, v As Integer)
            s.WriteByte(CByte((v >> 24) And &HFF)) : s.WriteByte(CByte((v >> 16) And &HFF)) : s.WriteByte(CByte((v >> 8) And &HFF)) : s.WriteByte(CByte(v And &HFF))
        End Sub

        Private Sub WriteBEU(s As Stream, v As UInteger)
            s.WriteByte(CByte((v >> 24) And &HFFUI)) : s.WriteByte(CByte((v >> 16) And &HFFUI)) : s.WriteByte(CByte((v >> 8) And &HFFUI)) : s.WriteByte(CByte(v And &HFFUI))
        End Sub

        Private Sub WriteChunk(s As Stream, type As String, data As Byte())
            WriteBE(s, data.Length)
            Dim typeBytes = Text.Encoding.ASCII.GetBytes(type)
            s.Write(typeBytes, 0, 4)
            s.Write(data, 0, data.Length)
            Dim crcInput = typeBytes.Concat(data).ToArray()
            WriteBEU(s, Crc32(crcInput))
        End Sub

        Private Function Crc32(data As Byte()) As UInteger
            Dim crc As UInteger = &HFFFFFFFFUI
            For Each b In data
                crc = crc Xor b
                For k = 0 To 7
                    If (crc And 1UI) <> 0 Then
                        crc = (crc >> 1) Xor &HEDB88320UI
                    Else
                        crc >>= 1
                    End If
                Next
            Next
            Return Not crc
        End Function
    End Module

End Namespace
