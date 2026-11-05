'use strict';
/* Agentic - the public page of a shared artifact (/a/{token}): no sign-in. It shows the artifact's name and runs it in a
   sandboxed frame; the artifact itself (/a/{token}/run or /a/{token}/site/) is served as an isolated origin, so it cannot
   use the viewer's Agentic session. */

(function theme() { try { const t = localStorage.getItem('theme'); if (t) document.documentElement.dataset.theme = t; } catch { /* ignore */ } })();

function esc(v) {
  if (v === null || v === undefined) return '';
  return String(v).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

document.addEventListener('DOMContentLoaded', async () => {
  const token = location.pathname.split('/').filter(Boolean)[1] || '';
  const title = document.getElementById('artifact-title'), main = document.getElementById('artifact-main'), open = document.getElementById('artifact-open');
  let a;
  try {
    const res = await fetch('/api/public/artifacts/' + encodeURIComponent(token), { credentials: 'omit', cache: 'no-store' });
    if (!res.ok) throw new Error(res.status === 404 ? 'This link does not exist or is no longer shared.' : `It could not be loaded (${res.status}).`);
    a = await res.json();
  } catch (e) {
    title.innerHTML = '<b>Not available</b>';
    main.innerHTML = `<div class="artifact-missing"><h1>Nothing to show</h1><p class="muted">${esc(e.message)}</p></div>`;
    return;
  }
  document.title = `${a.title} · Agentic`;
  const when = new Date(a.updatedUtc || a.createdUtc);
  title.innerHTML = `<b>${esc(a.title)}</b><span class="muted small">${a.kind === 'site' ? 'Website' : (a.lang === 'svg' ? 'Drawing' : 'Page')} shared from Agentic${isNaN(when) ? '' : ' · ' + esc(when.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' }))}</span>`;
  open.href = a.run;
  open.hidden = false;
  const frame = document.createElement('iframe');
  frame.className = 'artifact-frame';
  frame.title = a.title;
  // The frame is sandboxed as well as the page it loads: scripts run, but as an origin of their own.
  frame.setAttribute('sandbox', 'allow-scripts allow-forms allow-modals allow-popups allow-downloads allow-pointer-lock allow-presentation');
  frame.setAttribute('allow', 'fullscreen; clipboard-write; autoplay');
  frame.src = a.run;
  main.appendChild(frame);
});
