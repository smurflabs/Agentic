'use strict';
/* Agentic - the public, read-only page of a shared chat (/share/{token}). It needs no sign-in: the conversation comes
   from /api/share/{token} without reasoning, tool output or costs. markdown.js draws the answers like the Chat tab. */

const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));
function esc(v) {
  if (v === null || v === undefined) return '';
  return String(v).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}
function enc(v) { return encodeURIComponent(v); }
function num(v) { return v === null || v === undefined ? '—' : Number(v).toLocaleString(); }
const SHARE_ICONS = { play: '<path d="M7 5l12 7-12 7z"/>', bolt: '<path d="M13 2 4 14h7l-1 8 9-12h-7z"/>' };
function icon(name) { return `<svg class="ic" viewBox="0 0 24 24" aria-hidden="true">${SHARE_ICONS[name] || ''}</svg>`; }

(function theme() { try { const t = localStorage.getItem('theme'); if (t) document.documentElement.dataset.theme = t; } catch { /* ignore */ } })();

const token = location.pathname.split('/').filter(Boolean)[1] || '';
function fileUrl(uploadId) { return `/api/share/${enc(token)}/files/${enc(uploadId)}`; }
function hostName(url) { try { return new URL(url).hostname.replace(/^www\./, ''); } catch { return ''; } }

function sharedMessageHtml(m) {
  const images = (m.attachments || []).filter(a => a.kind === 'image' && a.uploadId);
  const videos = (m.attachments || []).filter(a => a.kind === 'video' && a.uploadId);
  const imagesHtml = images.map(a => `<a class="msg-img" href="${fileUrl(a.uploadId)}" target="_blank" rel="noopener"><img src="${fileUrl(a.uploadId)}" alt="${esc(a.name)}" loading="lazy"></a>`).join('');
  if (m.role === 'user') {
    const files = (m.attachments || []).filter(a => !(a.kind === 'image' && a.uploadId)).map(a => `<span class="msg-file">📄 ${esc(a.name)}</span>`).join('');
    return `<div class="msg user">${imagesHtml ? `<div class="msg-imgs">${imagesHtml}</div>` : ''}<div class="bubble-u">${files ? `<div class="msg-files">${files}</div>` : ''}<div class="md">${chatMarkdown(m.content || '')}</div></div></div>`;
  }
  const sources = (m.sources || []).length ? `<div class="sources">${m.sources.map(s => `<a class="source" href="${esc(s.url)}" target="_blank" rel="noopener noreferrer">
      <span class="n">${s.index}</span><span class="st">${esc(s.title || hostName(s.url))}</span><span class="sh">${esc(hostName(s.url))}</span></a>`).join('')}</div>` : '';
  const tools = (m.tools || []).filter(t => !['ask_user', 'remember', 'forget', 'generate_image', 'generate_video'].includes(t));
  return `<div class="msg assistant"><div class="avatar"><img src="/assets/img/favicon.ico" alt=""></div><div class="msg-body">
      <div class="msg-head"><b>${esc(m.model || 'Assistant')}</b>${tools.length ? ` <span class="muted small">used ${esc(tools.join(', '))}</span>` : ''}</div>
      ${images.length || videos.length ? `<div class="gen-images">${images.map(a => `<figure class="gen-image"><a href="${fileUrl(a.uploadId)}" target="_blank" rel="noopener"><img src="${fileUrl(a.uploadId)}" alt="${esc(a.name)}" loading="lazy"></a></figure>`).join('')}${videos.map(a => `<figure class="gen-video"><video controls playsinline preload="metadata" src="${fileUrl(a.uploadId)}"></video></figure>`).join('')}</div>` : ''}
      <div class="md answer">${chatMarkdown(m.content || '', m.sources)}</div>${sources}</div></div>`;
}

document.addEventListener('click', async ev => {
  const b = ev.target.closest('[data-act="code-copy"]');
  if (!b) return;
  const src = $('#' + b.dataset.block + ' .code-src');
  try { await navigator.clipboard.writeText(src ? src.value : ''); b.textContent = 'Copied ✓'; setTimeout(() => { b.textContent = 'Copy'; }, 1400); } catch { /* not available */ }
});

document.addEventListener('DOMContentLoaded', async () => {
  const head = $('#share-head'), thread = $('#share-thread');
  let chat;
  try {
    const res = await fetch('/api/share/' + enc(token), { credentials: 'omit', cache: 'no-store' });
    if (!res.ok) throw new Error(res.status === 404 ? 'This link does not exist or is no longer shared.' : `The conversation could not be loaded (${res.status}).`);
    chat = await res.json();
  } catch (e) {
    head.innerHTML = `<h1>Conversation not available</h1><p class="muted">${esc(e.message)}</p>`;
    return;
  }
  document.title = `${chat.title} · Shared chat`;
  const when = new Date(chat.createdUtc);
  head.innerHTML = `<h1>${esc(chat.title)}</h1><div class="muted small">${esc(chat.model || '')}${isNaN(when) ? '' : ' · ' + esc(when.toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' }))} · ${num(chat.messages.length)} messages</div>`;
  thread.innerHTML = chat.messages.map(m => {
    try { return sharedMessageHtml(m); } catch { return `<div class="msg"><pre class="wrap">${esc(m.content || '')}</pre></div>`; }
  }).join('');
  $$('.md-color[data-color]', thread).forEach(e => { e.style.color = e.dataset.color; });
});
