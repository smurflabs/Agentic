Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json.Nodes

Namespace Http

    ''' <summary>A hand-written description of a route for the OpenAPI document: what it does, its body, query and answer.</summary>
    Friend NotInheritable Class RouteDoc
        Public Summary As String
        Public Notes As String
        ''' <summary>The JSON body's schema (Nothing: no JSON body).</summary>
        Public Body As JsonObject
        ''' <summary>An example body (JSON text).</summary>
        Public Example As String
        ''' <summary>The body is a file (raw bytes), not JSON.</summary>
        Public BinaryBody As Boolean
        Public ReadOnly Query As New List(Of JsonObject)
        ''' <summary>The answer's media type when it is not JSON (audio/wav, image/png …).</summary>
        Public Answer As String
        ''' <summary>The JSON answer's schema.</summary>
        Public AnswerSchema As JsonObject
        Public AnswerNotes As String
        ''' <summary>202 Accepted (the work goes on in the background).</summary>
        Public Accepted As Boolean
        Public NoContent As Boolean
    End Class

    ''' <summary>
    ''' The Audio, Images and Music APIs in the OpenAPI document (/swagger): each route's body with every field and its range,
    ''' its query, what it answers, and how the features are used from a program with a personal API key.
    ''' </summary>
    Partial Friend NotInheritable Class OpenApi

        Friend Const AudioTag As String = "Audio (speech)"
        Friend Const ImagesTag As String = "Images (Stable Diffusion)"
        Friend Const MusicTag As String = "Music (Suno)"

        Friend Shared ReadOnly TagDescriptions As New Dictionary(Of String, String) From {
            {AudioTag, "Speech from the XTTS-v2 server: the Audio tab, Read aloud and Dictation mode (permission **Voice**, `chat.voice`). " &
                       "With an API key: `POST /api/audio/tts` with `{""text"": ""Hello"", ""voiceKey"": ""speaker:Ana Florence""}` answers a WAV file; " &
                       "`POST /api/audio/stream` streams the audio as it is made. `GET /api/audio` lists the voices (`speaker:Name` built in, `voice:name` cloned) and languages. " &
                       "**One line for everyone:** the server makes one request at a time; every other request (from the tabs, the chat or the API) waits here, first come first served, " &
                       "until the one being made ends: `GET /api/audio/queue` shows where yours are. User accounts pay per 1M characters spoken and per minute of audio (`GET /api/audio/pricing`)."},
            {MarketsTag, MarketsTagText}, {NewsTag, NewsTagText}, {StocksTag, StocksTagText}, {PolymarketTag, PolymarketTagText},
            {ImagesTag, "Pictures from the Stable Diffusion 1.5 server: the Images tab (permission **Images (Stable Diffusion)**, `images.sd`). " &
                        "With an API key: `POST /api/images/jobs` answers 202 with the job; follow `GET /api/images/jobs/{jobId}` until `status` is `done` (or `failed`, `cancelled`), " &
                        "then each of its `imageIds` is `GET /api/images/gallery/{imageId}/file` (a PNG). Pictures are given as base64 (a data: URL is fine) or `gallery:{imageId}`. " &
                        "**One job at a time:** the server makes one job at a time, so jobs (and preprocessor runs) wait in line here, first come first served, and are sent when " &
                        "the one before them ends; a waiting job has `inLine: true` and `queuePosition` (1 = next). `GET /api/images/queue` shows the line. " &
                        "User accounts pay per picture and per GPU minute (`GET /api/images/pricing`)."},
            {MusicTag, "Songs from Suno through the Suno API wrapper: the Music tab (permission **Music (Suno)**, `music.suno`). " &
                       "With an API key: `POST /api/music/jobs` answers 202 with the request; follow `GET /api/music/jobs/{jobId}` until `status` is `done` (Suno needs one to three minutes), " &
                       "then each of its `songIds` is `GET /api/music/songs/{songId}/audio` (an MP3; and `/cover`). Every request makes two songs and spends the server's Suno credits. " &
                       "**Suno downloads:** a song's audio comes from Suno's official download the first time it is asked for (Suno scrambles its stream), which uses one of " &
                       "the Suno account's downloads (402 `SUNO_NO_DOWNLOADS` when none are left); after that it is kept here. " &
                       "User accounts pay per song and per lyrics written (`GET /api/music/pricing`)."}}

        ' ------------------------------------------------------------------ schema helpers

        Private Shared Function Prop(type As String, description As String, Optional min As Double? = Nothing, Optional max As Double? = Nothing, Optional example As JsonNode = Nothing) As JsonObject
            Dim o As New JsonObject From {{"type", type}, {"description", description}}
            If min.HasValue Then o("minimum") = min.Value
            If max.HasValue Then o("maximum") = max.Value
            If example IsNot Nothing Then o("example") = example
            Return o
        End Function

        Private Shared Function Str(description As String, Optional example As String = Nothing) As JsonObject
            Return Prop("string", description, example:=If(example Is Nothing, Nothing, JsonValue.Create(example)))
        End Function

        Private Shared Function Num(description As String, Optional min As Double? = Nothing, Optional max As Double? = Nothing) As JsonObject
            Return Prop("number", description, min, max)
        End Function

        Private Shared Function Int(description As String, Optional min As Double? = Nothing, Optional max As Double? = Nothing) As JsonObject
            Return Prop("integer", description, min, max)
        End Function

        Private Shared Function Bool(description As String) As JsonObject
            Return Prop("boolean", description)
        End Function

        Private Shared Function Arr(items As JsonObject, description As String) As JsonObject
            Return New JsonObject From {{"type", "array"}, {"items", items}, {"description", description}}
        End Function

        Private Shared Function Obj(ParamArray props As (Name As String, Schema As JsonObject)()) As JsonObject
            Dim p As New JsonObject()
            For Each x In props
                p(x.Name) = x.Schema
            Next
            Return New JsonObject From {{"type", "object"}, {"properties", p}}
        End Function

        Private Shared Function Required(schema As JsonObject, ParamArray names As String()) As JsonObject
            schema("required") = New JsonArray(names.Select(Function(n) CType(JsonValue.Create(n), JsonNode)).ToArray())
            Return schema
        End Function

        Private Shared Function QueryParam(name As String, type As String, description As String) As JsonObject
            Return New JsonObject From {{"name", name}, {"in", "query"}, {"required", False}, {"description", description}, {"schema", New JsonObject From {{"type", type}}}}
        End Function

        Private Shared Function Doc(summary As String, Optional notes As String = Nothing, Optional body As JsonObject = Nothing, Optional example As String = Nothing,
                                    Optional answer As String = Nothing, Optional answerSchema As JsonObject = Nothing, Optional answerNotes As String = Nothing,
                                    Optional accepted As Boolean = False, Optional query As JsonObject() = Nothing) As RouteDoc
            Dim d As New RouteDoc With {.Summary = summary, .Notes = notes, .Body = body, .Example = example, .Answer = answer, .AnswerSchema = answerSchema,
                                        .AnswerNotes = answerNotes, .Accepted = accepted}
            If query IsNot Nothing Then d.Query.AddRange(query)
            Return d
        End Function

        ' ------------------------------------------------------------------ shared shapes

        ''' <summary>The speech settings of /tts (each optional; the voice's personality, then the server, fill in the rest).</summary>
        Private Shared Function SpeechSettings(streaming As Boolean) As (Name As String, Schema As JsonObject)()
            Dim list As New List(Of (Name As String, Schema As JsonObject)) From {
                ("language", Str("Language code (GET /api/audio lists them), e.g. en, es, fr, de, zh-cn", "en")),
                ("speed", Num("Speaking speed (above 0, at most 3; 1 is normal)", 0, 3)),
                ("temperature", Num("Sampling temperature (above 0, at most 2)", 0, 2)),
                ("top_k", Int("Top-k sampling (1 to 1000)", 1, 1000)),
                ("top_p", Num("Top-p sampling (above 0, at most 1)", 0, 1)),
                ("repetition_penalty", Num("Repetition penalty (1 to 50)", 1, 50)),
                ("length_penalty", Num("Length penalty (-100 to 100)", -100, 100)),
                ("enable_text_splitting", Bool("Split long text into sentences"))}
            If streaming Then list.Add(("stream_chunk_size", Int("Tokens per streamed chunk (5 to 200): smaller starts sooner", 5, 200)))
            Return list.ToArray()
        End Function

        Private Shared Function SpeechBody(streaming As Boolean) As JsonObject
            Dim props As New List(Of (Name As String, Schema As JsonObject)) From {
                ("text", Str("What to say (at most TextToSpeech.MaxTextChars, 20,000 by default)", "Hello from Agentic.")),
                ("voiceKey", Str("The voice: speaker:Name (built in) or voice:name (cloned); your chosen voice when left out", "speaker:Ana Florence")),
                ("speaker", Str("A built-in speaker's name (instead of voiceKey)")),
                ("voice", Str("A cloned voice's name (instead of voiceKey)")),
                ("useProfile", Bool("Fill in the settings left out from the voice's personality (default true)")),
                ("save", Bool("Keep the speech on the Audio tab (its id is in the X-Clip-Id header)"))}
            props.AddRange(SpeechSettings(streaming))
            If streaming Then props.Add(("format", Str("pcm (raw 16-bit mono 24 kHz, the default) or wav; ?format= works too", "pcm")))
            Return Required(Obj(props.ToArray()), "text")
        End Function

        Private Shared Function QueueSchema() As JsonObject
            Return Obj(("slots", Int("Requests the server works on at once")), ("running", Int("Requests it is working on now")), ("waiting", Int("Requests waiting in line")),
                       ("mineRunning", Int("Your requests it is working on")),
                       ("mine", Arr(Obj(("id", Str("The waiting request")), ("label", Str("What it is")), ("position", Int("Its place in line (1 = next)")),
                                        ("waitingSeconds", Int("How long it has waited"))), "Your requests waiting, first in line first")))
        End Function

        Private Shared Function SdImageInput(description As String) As JsonObject
            Return Str(description & ": base64 (a data: URL is fine) or gallery:{imageId}")
        End Function

        Private Shared Function SdJobBody() As JsonObject
            Dim lora = Obj(("name", Str("A LoRA the server has (GET /api/images/options)")), ("weight", Num("Its weight (-4 to 4)", -4, 4)))
            Dim net = Obj(("model", Str("A ControlNet: canny, depth, openpose, lineart, lineart_anime, softedge, scribble, mlsd, normalbae, seg, inpaint, tile, shuffle, ip2p")),
                          ("image", SdImageInput("Its control image (empty: the init image)")),
                          ("preprocessor", Str("Run on the image first (canny, depth, openpose …; none: the image is already a map)")),
                          ("preprocessor_params", New JsonObject From {{"type", "object"}, {"description", "The preprocessor's settings, e.g. {""low_threshold"": 50}"}}),
                          ("scale", Num("Its strength (0 to 2)", 0, 2)), ("start", Num("When it starts (0 to 1 of the steps)", 0, 1)), ("end", Num("When it ends (0 to 1)", 0, 1)))
            Dim hires = Obj(("scale", Num("Upscale factor (1 to 4)", 1, 4)), ("upscaler", Str("lanczos or latent")), ("steps", Int("Steps of the second pass")),
                            ("denoise", Num("Denoising of the second pass (above 0, at most 1)", 0, 1)))
            Return Required(Obj(
                ("prompt", Str("What to draw (A1111 syntax: (word:1.2), embeddings by name)", "a lighthouse at dusk, oil painting")),
                ("negative_prompt", Str("What to avoid", "blurry, low quality")),
                ("model", Str("A checkpoint the server has (GET /api/images/options), a file stem or a Hugging Face repo id")),
                ("vae", Str("A VAE (default: the model's)")), ("scheduler", Str("A scheduler, e.g. dpmpp_2m, euler_a")),
                ("width", Int("Width in pixels (64 to 2048, rounded down to a multiple of 8)", 64, 2048)),
                ("height", Int("Height in pixels (64 to 2048, rounded down to a multiple of 8)", 64, 2048)),
                ("steps", Int("Sampling steps (1 to 150)", 1, 150)), ("guidance_scale", Num("CFG scale (0 to 30)", 0, 30)),
                ("guidance_rescale", Num("Guidance rescale (0 to 1)", 0, 1)), ("eta", Num("Eta (0 to 1)", 0, 1)), ("clip_skip", Int("CLIP skip (1 to 12)", 1, 12)),
                ("seed", Int("Seed (-1 is random; picture i uses seed + i)")), ("num_images", Int("Pictures to make (1 to 16)", 1, 16)),
                ("freeu", Bool("FreeU")), ("loras", Arr(lora, "LoRAs to apply")),
                ("init_image", SdImageInput("img2img: the picture to start from")), ("strength", Num("img2img: how much it changes (0 to 1)", 0, 1)),
                ("mask_image", SdImageInput("Inpainting: white = repaint")), ("invert_mask", Bool("Inpainting: repaint the black part instead")),
                ("mask_blur", Int("Inpainting: mask blur in pixels")), ("inpaint_padding", Int("Inpainting: repaint only the masked area with this padding (0 to 512)", 0, 512)),
                ("controlnets", Arr(net, "ControlNets (up to 8)")), ("guess_mode", Bool("ControlNet guess mode")),
                ("hires", hires.WithDescription("Hires fix: a second, larger pass (text to image only)"))), "prompt")
        End Function

        Private Shared Function SdJobSchema() As JsonObject
            Return Obj(("id", Str("The job")), ("status", Str("queued | running | saving | done | failed | cancelled")),
                       ("inLine", Bool("It waits in line here for the server (busy with an earlier job)")),
                       ("queuePosition", Int("Its place in line (1 = next)")), ("progress", Num("0 to 1")), ("step", Int("Current step")), ("totalSteps", Int("Steps in all")),
                       ("imageIndex", Int("Which picture is being made (from 1)")), ("numImages", Int("Pictures asked for")), ("prompt", Str("The prompt")),
                       ("mode", Str("txt2img | img2img | inpaint")), ("error", Str("Why it failed")), ("imageIds", Arr(Str("A gallery picture"), "The pictures made (when done)")),
                       ("costMicros", Int("What it cost, in micro-USD (User accounts)")), ("elapsedMs", Int("GPU time")),
                       ("createdUtc", Str("When it was asked for")), ("submittedUtc", Str("When it was sent to the server")), ("finishedUtc", Str("When it ended")),
                       ("request", New JsonObject From {{"type", "object"}, {"description", "The request without its pictures"}}))
        End Function

        Private Shared Function MusicJobSchema() As JsonObject
            Return Obj(("id", Str("The request")), ("status", Str("making | saving | done | failed")), ("batchId", Str("Suno's batch")),
                       ("clips", Arr(Obj(("id", Str("Suno's clip id")), ("status", Str("submitted | queued | streaming | complete | error")), ("title", Str("Its title")), ("error", Str("Why it failed"))), "Suno's two songs as it makes them")),
                       ("songIds", Arr(Str("A song in the library"), "The songs kept (when done)")), ("error", Str("Why it failed, or which song failed")),
                       ("costMicros", Int("What it cost, in micro-USD (User accounts)")), ("createdUtc", Str("When it was asked for")), ("finishedUtc", Str("When it ended")),
                       ("request", New JsonObject From {{"type", "object"}, {"description", "What was sent to Suno"}}))
        End Function

        Private Shared Function SongSchema() As JsonObject
            Return Obj(("id", Str("The song")), ("jobId", Str("The request that made it")), ("clipId", Str("Suno's clip id")), ("title", Str("Title")), ("tags", Str("Style")),
                       ("lyrics", Str("Lyrics")), ("description", Str("What it was asked for")), ("instrumental", Bool("No vocals")), ("model", Str("Suno model")),
                       ("durationMs", Int("Length")), ("mime", Str("The audio's type")), ("bytes", Int("The audio's size (0 before its first play)")),
                       ("hasAudio", Bool("Its audio is kept here (false: the first play downloads it from Suno)")), ("hasCover", Bool("It has cover art")),
                       ("starred", Bool("Starred songs are always kept")), ("createdUtc", Str("When it was kept")))
        End Function

        ' ------------------------------------------------------------------ the routes

        Friend Shared ReadOnly MediaDocs As Dictionary(Of String, RouteDoc) = BuildMediaDocs()

        Private Shared Function BuildMediaDocs() As Dictionary(Of String, RouteDoc)
            Dim d As New Dictionary(Of String, RouteDoc)(StringComparer.OrdinalIgnoreCase)
            Dim limit = QueryParam("limit", "integer", "How many")
            Dim search = QueryParam("q", "string", "Search")
            Dim pricingNote = "Prices are in USD (null = free) and apply to User accounts (with their surcharge); administrators never pay."

            ' ---- Audio
            d("GET /api/audio") = Doc("Speech: status, voices, languages, defaults, prices, your choices and the line",
                "Whether the speech server works (ready, problem), its built-in speakers and cloned voices (speakers), languages, defaults, prices, your account, " &
                "your Dictation settings (prefs), your voices' personalities (profiles) and the line (queue).", query:={QueryParam("refresh", "string", "1: ask the server for its voices again")})
            d("GET /api/audio/queue") = Doc("Speech: the line for the server", "The server makes one request at a time; the rest wait here, first come first served. " &
                "Your own waiting requests are listed with their place in line.", answerSchema:=QueueSchema())
            d("POST /api/audio/tts") = Doc("Make speech: the whole WAV file",
                "Waits in line for the server (one request at a time), then answers the WAV (24 kHz mono 16-bit). X-Clip-Id: the kept clip (save: true). " &
                "402 when a User account's balance does not cover the estimate; 503 when the line did not move for TextToSpeech.TimeoutSeconds.",
                SpeechBody(False), "{""text"":""Hello from Agentic."",""voiceKey"":""speaker:Ana Florence"",""language"":""en"",""speed"":1.0}", answer:="audio/wav")
            d("POST /api/audio/stream") = Doc("Make speech as it is made (streamed)",
                "Like /tts but the audio arrives as it is made (first audio after about 1.5 s): ?format=pcm (raw 16-bit mono 24 kHz, the default) or wav. " &
                "Headers X-Voice and X-Clip-Id. What reached you is charged when it ends.", SpeechBody(True),
                "{""text"":""Streaming speech."",""voiceKey"":""speaker:Damien Black"",""stream_chunk_size"":20}", answer:="application/octet-stream",
                query:={QueryParam("format", "string", "pcm (default) or wav")})
            d("POST /api/audio/summary") = Doc("Write a spoken summary (TL;DR) in a voice's personality",
                "Body {chatId, messageId} (kept with that chat answer) or {text}. The summary is written by a chat model and charged like a chat answer; speak it with /tts.",
                Obj(("text", Str("Text to summarize (instead of a chat answer)")), ("chatId", Str("A chat conversation")), ("messageId", Str("An answer in it")),
                    ("voiceKey", Str("The voice whose personality writes it")), ("words", Int("About how many words (10 to 300)", 10, 300)),
                    ("summaryPrompt", Str("A personality to try (instead of the voice's)")), ("summaryModel", Str("A model to write it")),
                    ("language", Str("Its language")), ("fallbackModel", Str("The model to use when no other is set"))),
                "{""text"":""A long answer …"",""voiceKey"":""speaker:Ana Florence"",""words"":40}")
            d("PUT /api/audio/prefs") = Doc("Your speech and Dictation settings",
                "Only the settings sent change (null removes one).",
                Obj(("voice", Str("Your voice: speaker:Name or voice:name")), ("silenceMs", Int("Dictation: silence that ends your turn (400 to 6000 ms)", 400, 6000)),
                    ("maxTurnSeconds", Int("Dictation: longest turn (10 to 600 s)", 10, 600)), ("noSpeechSeconds", Int("Dictation: stop listening after this long without speech (5 to 300 s)", 5, 300)),
                    ("summaryWords", Int("Spoken summary length (10 to 300 words)", 10, 300)), ("sensitivity", Int("Microphone sensitivity (1 to 10)", 1, 10)),
                    ("autoListen", Bool("Listen again after the answer is spoken")), ("speakErrors", Bool("Speak errors")), ("showTranscript", Bool("Show what was heard"))),
                "{""voice"":""speaker:Damien Black"",""silenceMs"":1200}")
            d("PUT /api/audio/profiles") = Doc("A voice's personality (for you)",
                "How the voice speaks: its spoken-summary personality and model, and default speech settings.",
                Required(Obj(("voice", Str("The voice: speaker:Name or voice:name")), ("summaryPrompt", Str("Its personality (system prompt of its spoken summaries)")),
                             ("summaryModel", Str("The model that writes its summaries")), ("settings", Obj(SpeechSettings(True)).WithDescription("Its default speech settings"))), "voice"),
                "{""voice"":""speaker:Damien Black"",""summaryPrompt"":""You are blunt."",""settings"":{""temperature"":0.6,""speed"":1.1}}")
            d("DELETE /api/audio/profiles") = Doc("Remove a voice's personality", query:={QueryParam("voice", "string", "The voice key")})
            d("GET /api/audio/clips") = Doc("Your kept speech clips", query:={search, limit})
            d("DELETE /api/audio/clips") = Doc("Delete all your kept clips")
            d("GET /api/audio/clips/{clipId}/audio") = Doc("A kept clip's audio", "WAV, or ?format=pcm. Byte ranges work.", answer:="audio/wav",
                query:={QueryParam("format", "string", "wav (default) or pcm"), QueryParam("download", "string", "1: as a file")})
            d("DELETE /api/audio/clips/{clipId}") = Doc("Delete a kept clip")
            d("POST /api/audio/voice-clips") = Doc("Upload a clip of a voice to clone",
                "Raw audio body (wav, mp3, flac, ogg, webm, m4a; at most TextToSpeech.MaxCloneClipMb), ?name=clip.wav. Answers its id for POST /api/audio/voices; kept for a day.",
                query:={QueryParam("name", "string", "The file name")})
            d("POST /api/audio/voice-clips").BinaryBody = True
            d("POST /api/audio/voices") = Doc("Clone a voice",
                "From uploaded clips of one speaker (POST /api/audio/voice-clips). consent must be true: the voice is yours, or its speaker agreed. Waits in line like speech. " &
                "The server keeps a voice 30 days (renew it).",
                Required(Obj(("name", Str("1 to 64 letters, digits, _ or -", "my_voice")), ("clips", Arr(Str("An uploaded clip's id"), "1 to 12 clips")),
                             ("consent", Bool("It is your voice, or its speaker agreed")), ("trimSilenceDb", Num("Trim silence below this level (10 to 80 dB)", 10, 80)),
                             ("normalize", Bool("Normalize loudness")), ("gptCondLen", Int("Seconds of audio to condition on (1 to 60)", 1, 60)),
                             ("gptCondChunkLen", Int("Chunk length (1 to 60, at most gptCondLen)", 1, 60)), ("maxRefLength", Int("Longest reference (1 to 120 s)", 1, 120))),
                         "name", "clips", "consent"),
                "{""name"":""my_voice"",""clips"":[""vclip_…""],""consent"":true}")
            d("GET /api/audio/voices/{name}") = Doc("A cloned voice: its clips, settings and expiry")
            d("POST /api/audio/voices/{name}/renew") = Doc("Renew a cloned voice (its 30 days start over)")
            d("DELETE /api/audio/voices/{name}") = Doc("Delete a cloned voice")
            d("PUT /api/audio/key") = Doc("Set the speech server's API key (administrators)", "Kept in the vault; empty removes it.",
                Obj(("apiKey", Str("The server's key"))))
            d("GET /api/audio/pricing") = Doc("Speech prices", pricingNote)
            d("PUT /api/audio/pricing") = Doc("Set speech prices", pricingNote,
                Obj(("perMillionChars", Num("USD per 1M characters spoken")), ("perMinute", Num("USD per minute of audio")), ("perVoiceUpload", Num("USD per voice cloned or renewed"))),
                "{""perMillionChars"":15,""perMinute"":0.01,""perVoiceUpload"":0.05}")

            ' ---- Images
            d("GET /api/images") = Doc("Pictures: status, the server's options and health, prices and the line",
                "Whether the server works (ready, problem), what it has (options: models, VAEs, LoRAs, embeddings, ControlNets, preprocessors, schedulers), " &
                "its health, limits (defaults), prices, your account and the line (queue).", query:={QueryParam("refresh", "string", "1: ask the server for its options again")})
            d("GET /api/images/queue") = Doc("Pictures: the line for the server", "The server makes one job at a time; the rest wait here, first come first served.",
                answerSchema:=QueueSchema())
            d("GET /api/images/options") = Doc("The server's models, VAEs, LoRAs, embeddings, ControlNets, preprocessors and schedulers")
            d("GET /api/images/health") = Doc("The server's health (GPU, model, queue, VRAM)")
            d("POST /api/images/jobs") = Doc("Make pictures (a job)",
                "Every setting is checked against its range before anything is sent. Answers 202 with the job: sent at once when the server is free, else waiting in line " &
                "(inLine, queuePosition) until the job before it ends. Follow it with GET /api/images/jobs/{jobId}. 429 when you already have " &
                "StableDiffusion.MaxActiveJobsPerUser jobs waiting or running; 402 when a User account's balance does not cover the estimate.",
                SdJobBody(), "{""prompt"":""a lighthouse at dusk, oil painting"",""negative_prompt"":""blurry"",""width"":512,""height"":512,""steps"":25,""guidance_scale"":7,""seed"":-1,""num_images"":2}",
                answerSchema:=SdJobSchema(), accepted:=True)
            d("GET /api/images/jobs") = Doc("Your recent jobs (newest first)", answerSchema:=Arr(SdJobSchema(), "Jobs"), query:={limit})
            d("GET /api/images/jobs/{jobId}") = Doc("A job: its progress, place in line, pictures and cost", answerSchema:=SdJobSchema())
            d("GET /api/images/jobs/{jobId}/preview") = Doc("A running job's live preview", "204 while there is none (in line, or not started).", answer:="image/png")
            d("DELETE /api/images/jobs/{jobId}") = Doc("Cancel a job", "One waiting in line leaves it and never reaches the server; a running one is cancelled there. Cancelled jobs are not charged.",
                answerSchema:=SdJobSchema())
            d("POST /api/images/preprocess") = Doc("Run a ControlNet preprocessor on a picture",
                "Answers the control map (PNG). Waits in line behind running jobs (the server does one thing at a time).",
                Required(Obj(("preprocessor", Str("canny, depth, depth_zoe, openpose, openpose_full, lineart, lineart_coarse, lineart_anime, softedge_hed, softedge_pidi, scribble_hed, scribble_pidi, mlsd, normalbae, shuffle, inpaint")),
                             ("image", SdImageInput("The picture")), ("params", New JsonObject From {{"type", "object"}, {"description", "Its settings, e.g. {""low_threshold"": 50}"}})),
                         "preprocessor", "image"),
                "{""preprocessor"":""canny"",""image"":""gallery:sdimg_…"",""params"":{""low_threshold"":50,""high_threshold"":150}}", answer:="image/png")
            d("GET /api/images/gallery") = Doc("Your gallery (newest first)",
                query:={QueryParam("q", "string", "Search prompt, model or seed"), QueryParam("starred", "string", "1: starred only"), QueryParam("job", "string", "Pictures of one job"), limit,
                        QueryParam("before", "string", "Older than this time (paging)")})
            d("DELETE /api/images/gallery") = Doc("Delete your unstarred pictures")
            d("GET /api/images/gallery/{imageId}") = Doc("A picture's settings, seed and PNG info")
            d("GET /api/images/gallery/{imageId}/file") = Doc("A picture (PNG)", answer:="image/png", query:={QueryParam("download", "string", "1: as a file")})
            d("PATCH /api/images/gallery/{imageId}") = Doc("Star or unstar a picture (starred pictures are always kept)", body:=Obj(("starred", Bool("Starred"))), example:="{""starred"":true}")
            d("DELETE /api/images/gallery/{imageId}") = Doc("Delete a picture")
            d("PUT /api/images/key") = Doc("Set the Stable Diffusion server's API key (administrators)", "Kept in the vault; empty removes it.", Obj(("apiKey", Str("The server's key"))))
            d("GET /api/images/pricing") = Doc("Picture prices", pricingNote)
            d("PUT /api/images/pricing") = Doc("Set picture prices", pricingNote,
                Obj(("perImage", Num("USD per picture")), ("perGpuMinute", Num("USD per minute the GPU worked"))), "{""perImage"":0.002,""perGpuMinute"":0.01}")

            ' ---- Music
            d("GET /api/music") = Doc("Music: status, models, prices and your account", "Whether the Suno wrapper works (ready, problem), the models, limits, prices and your account " &
                "(administrators also see Suno's credits).")
            d("POST /api/music/jobs") = Doc("Make songs (a request: two songs)",
                "Describe it (prompt: Suno writes the lyrics) or write it (title, tags, lyrics). instrumental leaves the vocals out. Answers 202 with the request; " &
                "follow it with GET /api/music/jobs/{jobId}. 429 when you already have Music.MaxActiveJobsPerUser requests being made; 402 when a User account's balance " &
                "does not cover two songs.",
                Obj(("prompt", Str("Describe it: genre, mood, what it is about (at most 3,000 characters)", "a dreamy lo-fi song about city rain at midnight")),
                    ("title", Str("Write it: the title (at most 200)")), ("tags", Str("The style: genre, mood, voice (at most 1,000)", "lo-fi, dreamy, female vocals")),
                    ("lyrics", Str("Write it: the lyrics with [Verse], [Chorus] … (at most 6,000)")), ("instrumental", Bool("No vocals")),
                    ("model", Str("A Suno model id (default Music.DefaultModel, chirp-goose = v6 mini)", "chirp-goose"))),
                "{""title"":""Midnight Rain"",""tags"":""lo-fi, dreamy, female vocals"",""lyrics"":""[Verse]\nStreetlights bleeding gold …\n\n[Chorus]\nOh midnight rain"",""instrumental"":false}",
                answerSchema:=MusicJobSchema(), accepted:=True)
            d("GET /api/music/jobs") = Doc("Your recent requests (newest first)", answerSchema:=Arr(MusicJobSchema(), "Requests"), query:={limit})
            d("GET /api/music/jobs/{jobId}") = Doc("A request: each song's state, the songs kept and the cost", answerSchema:=MusicJobSchema())
            d("POST /api/music/lyrics") = Doc("Write lyrics with Suno", "Answers {title, lyrics}. Charged per lyrics (User accounts).",
                Required(Obj(("instruction", Str("What the song is about (Suno refuses very short ones)", "a dreamy lo-fi song about city rain at midnight"))), "instruction"),
                "{""instruction"":""a dreamy lo-fi song about city rain at midnight""}")
            d("GET /api/music/songs") = Doc("Your library (newest first)", answerSchema:=Arr(SongSchema(), "Songs"),
                query:={QueryParam("q", "string", "Search title, style or lyrics"), QueryParam("starred", "string", "1: starred only"), QueryParam("job", "string", "Songs of one request"), limit,
                        QueryParam("before", "string", "Older than this time (paging)")})
            d("DELETE /api/music/songs") = Doc("Delete your unstarred songs")
            d("GET /api/music/songs/{songId}") = Doc("A song", answerSchema:=SongSchema())
            d("GET /api/music/songs/{songId}/audio") = Doc("A song's audio (MP3)",
                "The first time, it is downloaded from Suno (its official download: one of the Suno account's downloads; 402 SUNO_NO_DOWNLOADS when none are left), " &
                "then kept here, so it plays after Suno's links expire. Byte ranges work.", answer:="audio/mpeg",
                query:={QueryParam("download", "string", "1: as a file")})
            d("POST /api/music/songs/{songId}/fetch") = Doc("Get a song's audio from Suno now",
                "What the first play does, on its own (one of the Suno account's downloads; 402 SUNO_NO_DOWNLOADS when none are left). Answers the song (hasAudio true).",
                answerSchema:=SongSchema())
            d("GET /api/music/songs/{songId}/cover") = Doc("A song's cover art", answer:="image/jpeg")
            d("PATCH /api/music/songs/{songId}") = Doc("Rename, star or unstar a song", body:=Obj(("title", Str("1 to 200 characters")), ("starred", Bool("Starred songs are always kept"))),
                example:="{""title"":""Midnight Rain (take 1)"",""starred"":true}", answerSchema:=SongSchema())
            d("DELETE /api/music/songs/{songId}") = Doc("Delete a song")
            d("GET /api/music/suno") = Doc("The Suno account's own songs (administrators)", "Marked inLibrary when already in yours.")
            d("POST /api/music/import") = Doc("Take songs of the Suno account into your library (administrators)", body:=Required(Obj(("clipIds", Arr(Str("Suno's clip id"), "The songs"))), "clipIds"))
            d("PUT /api/music/key") = Doc("Set the Suno wrapper's API key (administrators)", "Its api_key.txt; kept in the vault, empty removes it.", Obj(("apiKey", Str("The wrapper's key"))))
            d("GET /api/music/pricing") = Doc("Music prices", pricingNote)
            d("PUT /api/music/pricing") = Doc("Set music prices", pricingNote,
                Obj(("perSong", Num("USD per song made (a request makes two)")), ("perLyrics", Num("USD per lyrics written by AI"))), "{""perSong"":0.05,""perLyrics"":0.01}")
            AddDataDocs(d)
            Return d
        End Function

        ''' <summary>Applies a route's hand-written description to its operation.</summary>
        Friend Shared Sub ApplyDoc(op As JsonObject, doc As RouteDoc, pathParams As JsonArray)
            op("summary") = doc.Summary
            If Not String.IsNullOrEmpty(doc.Notes) Then op("description") = doc.Notes & vbLf & vbLf & op("description").ToString()
            Dim prms As New JsonArray()
            For Each p In pathParams
                prms.Add(p.DeepClone())
            Next
            For Each p In doc.Query
                prms.Add(p.DeepClone())
            Next
            If prms.Count > 0 Then op("parameters") = prms Else op.Remove("parameters")
            If doc.Body IsNot Nothing Then
                Dim media As New JsonObject From {{"schema", doc.Body.DeepClone()}}
                If doc.Example IsNot Nothing Then media("example") = JsonNode.Parse(doc.Example)
                op("requestBody") = New JsonObject From {{"required", True}, {"content", New JsonObject From {{"application/json", media}}}}
            ElseIf doc.BinaryBody Then
                op("requestBody") = New JsonObject From {{"required", True}, {"content", New JsonObject From {
                    {"application/octet-stream", New JsonObject From {{"schema", New JsonObject From {{"type", "string"}, {"format", "binary"}}}}}}}}
            Else
                op.Remove("requestBody")
            End If
            Dim ok As New JsonObject From {{"description", If(doc.AnswerNotes, If(doc.Accepted, "Accepted: the work goes on; follow it", "OK"))}}
            If doc.Answer IsNot Nothing Then
                ok("content") = New JsonObject From {{doc.Answer, New JsonObject From {{"schema", New JsonObject From {{"type", "string"}, {"format", "binary"}}}}}}
            ElseIf doc.AnswerSchema IsNot Nothing Then
                ok("content") = New JsonObject From {{"application/json", New JsonObject From {{"schema", doc.AnswerSchema.DeepClone()}}}}
            End If
            Dim responses = op("responses").AsObject()
            If doc.Accepted Then
                responses.Remove("200")
                responses("202") = ok
                responses("429") = New JsonObject From {{"description", "Too many of your requests at once"}}
            Else
                responses("200") = ok
            End If
            If doc.Body IsNot Nothing OrElse doc.Accepted Then responses("402") = New JsonObject From {{"description", "The balance does not cover it (User accounts)"}}
        End Sub
    End Class

    Friend Module JsonSchemaExtensions
        ''' <summary>The schema with a description (for nested objects).</summary>
        <System.Runtime.CompilerServices.Extension>
        Friend Function WithDescription(schema As JsonObject, description As String) As JsonObject
            schema("description") = description
            Return schema
        End Function
    End Module

End Namespace
