Option Strict On
Option Explicit On
Option Infer On

Namespace Work

    ''' <summary>
    ''' Caveman mode (/cavemode, like github.com/juliusbrussee/caveman): the model answers in a terse style that keeps every
    ''' fact, code, command, path, number and error exactly, and drops the filler - fewer output tokens to read and pay for.
    ''' full: the caveman voice; ultra: as compressed as it gets; wenyan: Classical Chinese (文言文), the tersest of all.
    ''' Security warnings, irreversible steps, step order and a confused user get plain full sentences.
    ''' </summary>
    Partial Public NotInheritable Class ChatService

        Friend Shared Function NormalizeCaveman(mode As String) As String
            Select Case If(mode, "").Trim().ToLowerInvariant()
                Case "full", "on", "caveman", "true", "1" : Return "full"
                Case "ultra", "ultracave", "grunt" : Return "ultra"
                Case "wenyan", "megacave", "文言文" : Return "wenyan"
                Case Else : Return Nothing
            End Select
        End Function

        Private Const CavemanRules As String =
            "Answer first: [thing] [action] [reason]. [next step]. No greeting, no ""Sure!"", no ""Let me"", no recap, no closing offer of more help, no hedging " &
            "(just, really, basically, actually, simply). " &
            "Short common words; standard abbreviations only (API, DB, HTTP) - never invented ones. Articles and filler may go when the sentence still reads in one pass. " &
            "One idea per sentence, at most 20 words, active voice, imperative for instructions. Fragments are fine. " &
            "Meaning never drops: keep every not, never, no, only and except; numbers and units exact. If two readings are possible, write the full sentence. " &
            "Payload verbatim: code blocks, commands, file paths, API names, numbers and error messages exactly as they are (quote only the decisive line of an error). " &
            "Between tool calls say nothing unless it clarifies, warns or disambiguates; at most one short line per phase and one line with the result. " &
            "Never perform the style: no ""me think"", no ""Caveman:"" prefix, no normal answer followed by a short copy; if the caveman phrasing is not shorter, use plain words. " &
            "Answer in the user's language. " &
            "Break the style - plain, complete sentences - for security warnings, irreversible or destructive actions (confirm them in full first), step-by-step " &
            "instructions where a fragment could scramble the order, and when the user is confused or repeats a question; then go back to it. " &
            "Anything written to a file or sent elsewhere (code, comments, commit messages, docs, PR text, emails, tickets) is written normally, not in caveman style."

        ''' <summary>The instructions for the chat's caveman mode, or Nothing when it is off.</summary>
        Friend Shared Function CavemanInstructions(mode As String) As String
            Select Case NormalizeCaveman(mode)
                Case "full"
                    Return "CAVEMAN MODE (the user turned it on; it stays on for every reply until they turn it off). Respond terse like a smart caveman: " &
                           "all technical substance stays, only fluff dies. " & CavemanRules
                Case "ultra"
                    Return "ULTRA CAVEMAN MODE (the user turned it on; it stays on for every reply until they turn it off). Compress as hard as meaning allows: " &
                           "fragments over sentences, lists and symbols over prose (key: value, A → B, ✓ / ✗), drop every article, connective and pronoun that is " &
                           "not needed, one line per point, no explanations unless asked. " & CavemanRules
                Case "wenyan"
                    Return "WENYAN CAVEMAN MODE (the user turned it on; it stays on for every reply until they turn it off). Write your prose in terse Classical " &
                           "Chinese (文言文): the fewest characters that keep every fact. Code, commands, paths, API names, numbers, units and error messages stay " &
                           "exactly as they are (do not translate them). " & CavemanRules.Replace("Answer in the user's language. ", "")
                Case Else : Return Nothing
            End Select
        End Function

    End Class

End Namespace
