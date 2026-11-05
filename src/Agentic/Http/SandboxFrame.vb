Option Strict On
Option Explicit On
Option Infer On

Imports System.Text

Namespace Http

    ''' <summary>
    ''' /sandbox/frame: the page the Chat tab runs HTML and JavaScript in. It is served with a CSP "sandbox" directive, so
    ''' it has an opaque origin: code inside cannot read the dashboard, its cookies or its API. Inline scripts and public
    ''' CDNs are allowed there (unlike the dashboard). The chat posts it a document to render; console output and errors are
    ''' posted back.
    ''' </summary>
    Friend NotInheritable Class SandboxFrame
        Private Const Page As String =
            "<!doctype html><html><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1"">" &
            "<style>html,body{margin:0;background:#fff;color:#111;font:14px system-ui,sans-serif}</style></head><body>" &
            "<script>window.addEventListener('message',function(e){var d=e.data||{};if(d.type!=='run'||typeof d.doc!=='string')return;" &
            "document.open();document.write(d.doc);document.close();});parent.postMessage({type:'sandbox-ready'},'*');</script></body></html>"

        Public Shared Async Function ServeAsync(rc As RequestContext) As Task
            Dim resp = rc.Http.Response
            resp.Headers.Remove("X-Frame-Options")
            resp.Headers("X-Frame-Options") = "SAMEORIGIN"
            resp.Headers("Content-Security-Policy") =
                "default-src 'none'; script-src 'unsafe-inline' 'unsafe-eval' https://cdn.jsdelivr.net https://cdnjs.cloudflare.com https://unpkg.com https://esm.sh; " &
                "style-src 'unsafe-inline' https:; img-src data: blob: https:; font-src data: https:; media-src data: blob: https:; connect-src https:; " &
                "worker-src blob:; frame-ancestors 'self'; sandbox allow-scripts allow-modals allow-forms allow-popups"
            resp.Headers("Cache-Control") = "no-store"
            Await rc.WriteBytesAsync(200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Page)).ConfigureAwait(False)
        End Function
    End Class

End Namespace
