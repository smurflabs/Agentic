'use strict';
/* Agentic console - core: state, helpers, API client, router, live events, dialogs, selection, drag & drop, charts.
   No inline scripts or style attributes (strict CSP). Views live in views.js / agent.js / editor.js. */

const S = {
  csrf: null, user: null, es: null, feed: [], view: null, params: null, refreshTimer: null, lastRefresh: 0, loading: false,
  sel: {}, dragging: null, charts: {}, crumbs: [], status: null
};
const VIEWS = {}, ACTIONS = {}, FORMS = {}, CHANGES = {}, DROPS = {};
const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));
/** This page is one pane of Chat's multitask mode (a frame of the Chat page): Chat only, without the app's frame around it. */
const IN_PANE = (() => { try { return !!(window.frameElement && window.frameElement.hasAttribute('data-chat-pane')); } catch { return false; } })();
if (IN_PANE) document.documentElement.classList.add('in-pane');

// ------------------------------------------------------------------ formatting
function esc(v) {
  if (v === null || v === undefined) return '';
  return String(v).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}
function money(micros, digits = 4) {
  if (micros === null || micros === undefined) return '—';
  const d = Number(micros) / 1e6;
  const abs = Math.abs(d);
  let dg = abs >= 100 ? 2 : abs >= 1 ? Math.min(digits, 2) : digits;
  // Never show a nonzero amount as $0.00: small cloud charges get more precision.
  if (abs > 0 && Number(abs.toFixed(dg)) === 0) dg = 4;
  if (abs > 0 && abs < 0.0001) return (d < 0 ? '-' : '') + '<$0.0001';
  return (d < 0 ? '-$' : '$') + abs.toFixed(dg);
}
function usd(v) { return v === null || v === undefined ? '—' : '$' + Number(v).toFixed(2); }
function num(v) { return v === null || v === undefined ? '—' : Number(v).toLocaleString(); }
function ts(iso) { if (!iso) return '—'; const d = new Date(iso); return isNaN(d) ? '—' : d.toLocaleString(); }
function time(iso) { if (!iso) return ''; const d = new Date(iso); return isNaN(d) ? '' : d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' }); }
function ago(iso) {
  if (!iso) return '—';
  const s = (Date.now() - new Date(iso).getTime()) / 1000;
  if (isNaN(s)) return '—';
  if (s < -60) return 'in ' + dur(-s * 1000);
  if (s < 5) return 'just now';
  if (s < 60) return Math.floor(s) + 's ago';
  if (s < 3600) return Math.floor(s / 60) + 'm ago';
  if (s < 86400) return Math.floor(s / 3600) + 'h ago';
  return Math.floor(s / 86400) + 'd ago';
}
function dur(ms) {
  if (ms === null || ms === undefined) return '—';
  if (ms < 1000) return Math.round(ms) + 'ms';
  const s = ms / 1000;
  if (s < 60) return s.toFixed(1) + 's';
  if (s < 3600) return Math.floor(s / 60) + 'm ' + Math.floor(s % 60) + 's';
  if (s < 86400) return Math.floor(s / 3600) + 'h ' + Math.floor((s % 3600) / 60) + 'm';
  return Math.floor(s / 86400) + 'd ' + Math.floor((s % 86400) / 3600) + 'h';
}
function uptime(sec) { return sec ? dur(sec * 1000) : '—'; }
function plural(n, word) { return `${num(n)} ${word}${n === 1 ? '' : 's'}`; }
function enc(v) { return encodeURIComponent(v); }
function newKey() { return crypto.randomUUID ? crypto.randomUUID() : String(Date.now()) + Math.random(); }

const STATE_CLASS = {
  Running: 'info', Idle: 'ok', Waiting: 'accent', Paused: 'warn', Starting: 'info', Stopping: 'warn', Stopped: '', Disabled: '',
  Error: 'bad', Unhealthy: 'bad', Stuck: 'bad', Pending: '', Retrying: 'warn', Completed: 'ok', Failed: 'bad', Cancelled: '',
  Blocked: 'warn', Held: 'warn', Delivered: 'info', Processing: 'info', Rejected: 'bad', Expired: 'warn', Approved: 'ok', Answered: 'ok',
  Connected: 'ok', Connecting: 'info', Faulted: 'bad', Disconnected: 'warn', Closed: 'ok', Open: 'bad', HalfOpen: 'warn',
  Critical: 'bad', High: 'warn', Normal: '', Low: 'outline', Dead: 'bad', Reviewing: 'info',
  NotReviewed: 'outline', New: 'info', Viewed: '', Rated: 'ok', Archived: ''
};
/** States that represent work in progress: their status dot pulses. */
const LIVE_STATES = new Set(['Running', 'Processing', 'Analyzing', 'Starting', 'Connecting', 'Retrying']);
function badge(text, cls, dot) {
  const c = cls !== undefined ? cls : (STATE_CLASS[text] || '');
  const live = dot !== false && LIVE_STATES.has(text) ? ' live' : '';
  return `<span class="badge ${c}${dot === false ? '' : ' dot'}${live}">${esc(text)}</span>`;
}
const ICONS = {
  check: '<path d="M5 12.5l4.5 4.5L19 7"/>', alert: '<path d="M12 8v5M12 16.5v.01"/><circle cx="12" cy="12" r="9"/>',
  info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v5M12 7.5v.01"/>', x: '<path d="M6 6l12 12M18 6L6 18"/>',
  pin: '<path d="M9 4h6l-1 6 3 3H7l3-3zM12 16v5"/>', download: '<path d="M12 4v11m0 0-4-4m4 4 4-4M5 19h14"/>',
  arrow: '<path d="M5 12h14m-5-5 5 5-5 5"/>', page: '<path d="M6 3h8l4 4v14H6zM14 3v5h4"/>', agent: '<circle cx="12" cy="8" r="4"/><path d="M4 21c0-4 4-6 8-6s8 2 8 6"/>',
  bolt: '<path d="M13 2 4 14h7l-1 8 9-12h-7z"/>', plus: '<path d="M12 5v14M5 12h14"/>', moon: '<path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z"/>',
  pause: '<path d="M8 5v14M16 5v14"/>', play: '<path d="M7 5l12 7-12 7z"/>', stop: '<rect x="6" y="6" width="12" height="12" rx="2"/>',
  search: '<circle cx="11" cy="11" r="7"/><path d="m20 20-4.3-4.3"/>', out: '<path d="M15 4h4v16h-4M10 8l-4 4 4 4M6 12h11"/>',
  layers: '<path d="M12 3 2 8l10 5 10-5zM2 13l10 5 10-5"/>', chart: '<path d="M4 4v16h16"/><path d="m7 15 4-5 3 3 5-7"/>', trash: '<path d="M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3"/>',
  folder: '<path d="M3 6.5A1.5 1.5 0 0 1 4.5 5H9l2 2.5h8.5A1.5 1.5 0 0 1 21 9v9.5a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 18.5z"/>',
  globe: '<circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c2.5 2.7 3.8 5.7 3.8 9S14.5 18.3 12 21c-2.5-2.7-3.8-5.7-3.8-9S9.5 5.7 12 3z"/>',
  external: '<path d="M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5"/>',
  upload: '<path d="M12 16V5m0 0-4 4m4-4 4 4M5 19h14"/>', archive: '<path d="M3 5h18v4H3zM5 9v10h14V9M10 13h4"/>'
};
function icon(name, cls = '') { return `<svg class="ic ${cls}" viewBox="0 0 24 24" aria-hidden="true">${ICONS[name] || ''}</svg>`; }
/** Motion preference: the user menu's choice ("on"/"off") wins over the operating system setting ("auto"). */
function reducedMotion() {
  const m = document.documentElement.dataset.motion;
  return m === 'on' ? false : m === 'off' ? true : matchMedia('(prefers-reduced-motion: reduce)').matches;
}
/** Days a balance lasts at the current burn rate: text plus a severity class. */
function runwayInfo(days, low) {
  if (days === null || days === undefined) return { text: 'no recent spend', cls: '' };
  const text = days < 1 ? `${Math.max(1, Math.round(days * 24))} h` : days >= 365 ? '> 1 year' : `${days < 10 ? days.toFixed(1) : Math.round(days)} days`;
  return { text, cls: days < 1 ? 'critical' : low ? 'low' : '' };
}
function runway(days, low) {
  if (days === null || days === undefined) return '<span class="muted" data-tip="No recent cloud spend">—</span>';
  const r = runwayInfo(days, low);
  return `<span class="runway ${r.cls}">${esc(r.text)}</span>`;
}
function tag(text, cls = 'outline') { return `<span class="badge ${cls}">${esc(text)}</span>`; }
/** Task status as shown to admins: Blocked tasks held by an admin read "Held". */
function taskStatus(t) { return t.status === 'Blocked' && /held by administrator/i.test(t.error || '') ? 'Held' : t.status; }
function chips(list, cls = 'outline') { return (list && list.length) ? `<span class="chips">${list.map(x => tag(x, cls)).join('')}</span>` : '<span class="muted">none</span>'; }
function link(href, text, cls = '') { return `<a href="${esc(href)}" data-nav class="${cls}">${esc(text)}</a>`; }
function agentLink(id, label) { return id ? link('/agents/' + enc(id), label || id) : '<span class="muted">—</span>'; }
/** A value as coloured JSON (jsonColor is in markdown.js). */
function json(v) { return `<pre class="json">${jsonColor(JSON.stringify(v, null, 2))}</pre>`; }
/** Small, safe Markdown renderer for agent answers: everything is escaped first; only http(s) links are produced. */
function markdown(src) {
  const lines = String(src || '').replace(/\r\n/g, '\n').split('\n');
  const inline = s => esc(s).replace(/`([^`]+)`/g, '<code>$1</code>').replace(/\*\*([^*]+)\*\*/g, '<b>$1</b>')
    .replace(/(^|[\s(])\*([^*\s][^*]*?)\*(?=[\s).,;:!?]|$)/g, '$1<i>$2</i>')
    .replace(/\[([^\]]+)\]\((https?:\/\/[^)\s]+)\)/g, '<a href="$2" target="_blank" rel="noopener noreferrer">$1</a>');
  const cells = l => l.trim().replace(/^\|/, '').replace(/\|$/, '').split('|').map(c => c.trim());
  const isBlock = l => /^```/.test(l) || /^#{1,6}\s/.test(l) || /^\s*([-*+]|\d+[.)])\s+/.test(l) || /^\s*\|.*\|\s*$/.test(l) || /^\s*(---|\*\*\*)\s*$/.test(l);
  let html = '', i = 0;
  while (i < lines.length) {
    const l = lines[i];
    if (!l.trim()) { i++; continue; }
    if (/^```/.test(l)) {
      const code = []; i++;
      while (i < lines.length && !/^```/.test(lines[i])) code.push(lines[i++]);
      i++; html += `<pre>${esc(code.join('\n'))}</pre>`; continue;
    }
    const h = l.match(/^(#{1,6})\s+(.*)$/);
    if (h) { html += `<h${Math.min(6, h[1].length + 2)} class="md-h">${inline(h[2])}</h${Math.min(6, h[1].length + 2)}>`; i++; continue; }
    if (/^\s*(---|\*\*\*)\s*$/.test(l)) { html += '<hr>'; i++; continue; }
    if (/^\s*\|.*\|\s*$/.test(l) && i + 1 < lines.length && /^\s*\|?\s*:?-{2,}/.test(lines[i + 1])) {
      const head = cells(l); i += 2;
      const rows = [];
      while (i < lines.length && /^\s*\|.*\|\s*$/.test(lines[i])) rows.push(cells(lines[i++]));
      html += `<div class="table-wrap"><table><thead><tr>${head.map(c => `<th>${inline(c)}</th>`).join('')}</tr></thead><tbody>${rows.map(r => `<tr>${r.map(c => `<td>${inline(c)}</td>`).join('')}</tr>`).join('')}</tbody></table></div>`;
      continue;
    }
    const li = l.match(/^\s*([-*+]|\d+[.)])\s+(.*)$/);
    if (li) {
      const ordered = /\d/.test(li[1]), items = [];
      while (i < lines.length) { const m = lines[i].match(/^\s*([-*+]|\d+[.)])\s+(.*)$/); if (!m) break; items.push(m[2]); i++; }
      html += `<${ordered ? 'ol' : 'ul'}>${items.map(x => `<li>${inline(x)}</li>`).join('')}</${ordered ? 'ol' : 'ul'}>`;
      continue;
    }
    const para = [];
    while (i < lines.length && lines[i].trim() && !isBlock(lines[i])) para.push(lines[i++]);
    if (!para.length) para.push(lines[i++]);
    html += `<p>${para.map(inline).join('<br>')}</p>`;
  }
  return html;
}
function meter(fraction, label, cls = '') { const pct = Math.max(0, Math.min(1, fraction || 0)); return `<div class="bar ${cls}" data-tip="${esc(label)}" data-width="${(pct * 100).toFixed(1)}"><span></span></div>`; }
/** Sets meter widths (data-width); on a first render they grow in after the bars have been laid out at zero. */
function applyMeters(root, animate) {
  const bars = $$('.bar[data-width]', root);
  const set = () => bars.forEach(b => { b.firstElementChild.style.width = b.dataset.width + '%'; });
  if (animate && !reducedMotion()) requestAnimationFrame(() => requestAnimationFrame(set)); else set();
}
function slots(used, cap) { let h = `<span class="slots" data-tip="${used} of ${cap} slots in use">`; for (let i = 0; i < cap; i++) h += `<span class="slot ${i < used ? 'used' : ''}"></span>`; return h + '</span>'; }
function kpi(label, value, sub, opts = {}) {
  const attrs = opts.href ? ` data-href="${esc(opts.href)}" data-act="go"` : '';
  return `<div class="card kpi${opts.href ? ' link' : ''}"${attrs}><div class="label">${esc(label)}</div><div class="value" data-k="kpi:${esc(label)}">${value}</div>${sub ? `<div class="sub">${sub}</div>` : ''}${opts.bar || ''}</div>`;
}
/** opts: {actions, hint, flush, cls, id, export: 'file-name' (adds a CSV export of the card's table)} */
function card(title, body, opts = {}) {
  const exp = opts.export ? `<button class="btn xs ghost" data-act="export-csv" data-name="${esc(opts.export)}" data-tip="Download this table as CSV">${icon('download')} CSV</button>` : '';
  const actions = (opts.actions || '') + exp;
  const head = title || actions ? `<div class="card-head"><h2>${esc(title || '')}</h2>${opts.hint ? `<span class="hint">${opts.hint}</span>` : ''}${actions ? `<div class="actions">${actions}</div>` : ''}</div>` : '';
  return `<div class="card${opts.cls ? ' ' + opts.cls : ''}"${opts.id ? ` id="${esc(opts.id)}"` : ''}>${head}<div class="card-body${opts.flush ? ' flush' : ''}">${body}</div></div>`;
}
function pageHead(title, opts = {}) {
  return `<div class="page-head"><div class="title"><div class="title-row"><h1>${esc(title)}</h1>${opts.badges || ''}</div>${opts.subtitle ? `<div class="subtitle">${opts.subtitle}</div>` : ''}</div>${opts.actions ? `<div class="actions">${opts.actions}</div>` : ''}</div>`;
}
function emptyState(title, text) { return `<div class="empty"><div class="big">${esc(title)}</div>${text ? esc(text) : ''}</div>`; }
function btn(label, act, data = {}, cls = '') {
  const attrs = Object.entries(data).map(([k, v]) => ` data-${k}="${esc(v)}"`).join('');
  return `<button class="btn ${cls}" data-act="${esc(act)}"${attrs}>${label}</button>`;
}

/** Table helper. cols: {h, f(row), num, cls}. opts: {empty, sel: {scope, id(row)}, rowAttrs(row), rowClass(row)} */
function table(cols, rows, opts = {}) {
  if (!rows || rows.length === 0) return opts.emptyHtml || emptyState(opts.empty || 'Nothing here yet.', opts.emptyText || '');
  const sel = opts.sel;
  const head = (sel ? `<th class="chk"><input type="checkbox" data-change="sel-all" data-scope="${esc(sel.scope)}" aria-label="Select all"></th>` : '') +
    cols.map(c => `<th class="${c.num ? 'num' : ''}">${esc(c.h)}</th>`).join('');
  const body = rows.map(r => {
    const id = sel ? sel.id(r) : null;
    const checked = sel && selSet(sel.scope).has(id);
    const cls = [opts.rowClass ? opts.rowClass(r) : '', checked ? 'selected' : ''].join(' ').trim();
    // A key lets a live refresh keep each row's identity (rows glide when reordered, new rows slide in).
    const key = opts.key ? opts.key(r) : (sel ? id : (r && r.id !== undefined && r.id !== null ? r.id : null));
    return `<tr${cls ? ` class="${cls}"` : ''}${key !== null && key !== undefined ? ` data-key="${esc(String(key))}"` : ''}${opts.rowAttrs ? ' ' + opts.rowAttrs(r) : ''}>` +
      (sel ? `<td class="chk" data-label=""><input type="checkbox" data-change="sel" data-scope="${esc(sel.scope)}" data-id="${esc(id)}"${checked ? ' checked' : ''} aria-label="Select"></td>` : '') +
      cols.map(c => `<td class="${c.num ? 'num' : ''} ${c.cls || ''}" data-label="${esc(c.h)}">${c.f(r)}</td>`).join('') + '</tr>';
  }).join('');
  // data-table: on phones (css/mobile.css) each row becomes a card, every cell labelled with its column (data-label).
  return `<div class="table-wrap data-table"><table><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table></div>`;
}

/** Notification with icon, dismiss button and a life bar; hovering pauses it. At most five are shown. */
function toast(msg, kind = '') {
  const t = document.createElement('div');
  t.className = 'toast ' + kind;
  t.setAttribute('role', kind === 'bad' ? 'alert' : 'status');
  t.innerHTML = `<span class="toast-icon">${icon(kind === 'bad' ? 'alert' : kind === 'ok' ? 'check' : 'info')}</span><div class="toast-text"></div>` +
    `<button class="toast-x" aria-label="Dismiss">${icon('x')}</button><span class="toast-bar"></span>`;
  $('.toast-text', t).textContent = msg;
  const life = kind === 'bad' ? 9000 : 4500;
  $('.toast-bar', t).style.animationDuration = life + 'ms';
  let left = life, started = Date.now(), timer = setTimeout(dismiss, life);
  function dismiss() {
    clearTimeout(timer);
    if (t.classList.contains('leaving')) return;
    t.classList.add('leaving');
    setTimeout(() => t.remove(), reducedMotion() ? 0 : 230);
  }
  t.addEventListener('mouseenter', () => { clearTimeout(timer); left -= Date.now() - started; });
  t.addEventListener('mouseleave', () => { started = Date.now(); timer = setTimeout(dismiss, Math.max(900, left)); });
  $('.toast-x', t).addEventListener('click', dismiss);
  t._dismiss = dismiss;
  $('#toasts').appendChild(t);
  const open = $$('#toasts .toast:not(.leaving)');
  if (open.length > 5) open[0]._dismiss();
}

// ------------------------------------------------------------------ API
class ApiError extends Error { constructor(status, code, message, body) { super(message); this.status = status; this.code = code; this.body = body; } }
async function api(method, url, body) {
  const opts = { method, credentials: 'same-origin', headers: {} };
  if (body !== undefined) { opts.headers['Content-Type'] = 'application/json'; opts.body = JSON.stringify(body); }
  if (method !== 'GET' && S.csrf) opts.headers['X-CSRF-Token'] = S.csrf;
  // Changes show the top progress bar; background reads (live refresh) stay silent.
  const tracked = method !== 'GET';
  if (tracked) progress(true);
  let res;
  try { res = await fetch(url, opts); } finally { if (tracked) progress(false); }
  noteBuild(res);
  if (res.status === 401 && url !== '/api/auth/login') { showLogin(); throw new ApiError(401, 'UNAUTHORIZED', 'Please sign in.'); }
  const text = await res.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = null; }
  if (!res.ok) {
    const e = data && data.error ? data.error : { code: 'HTTP_' + res.status, message: res.statusText || httpProblem(res.status) };
    throw new ApiError(res.status, e.code, e.message, data);
  }
  return data;
}
/**
 * What an answer that is not the server's own (a proxy's error page, e.g. Cloudflare's) means: over HTTP/2 there is no
 * status text to show.
 */
function httpProblem(status) {
  if (status === 504 || status === 524) return `The server took too long to answer (HTTP ${status}). Try again.`;
  if (status === 502 || status === 503 || (status >= 520 && status <= 523)) return `The server could not be reached (HTTP ${status}): it may be restarting. Try again in a moment.`;
  return `The request failed (HTTP ${status}).`;
}
const get = url => api('GET', url);
const post = (url, body) => api('POST', url, body || {});
/**
 * The dashboard version this page runs (X-Agentic-Build of the first reply). A later reply with another one means the server
 * was updated while the page stayed open: its old scripts can fail against the new server, so offer a reload (once).
 */
function noteBuild(res) {
  const b = res && res.headers && res.headers.get('X-Agentic-Build');
  if (!b) return;
  if (!S.build) { S.build = b; return; }
  if (b !== S.build && !S.updateOffered) { S.updateOffered = true; showUpdateBanner(); }
}
function showUpdateBanner() {
  if (document.getElementById('update-banner')) return;
  const el = document.createElement('div');
  el.id = 'update-banner'; el.className = 'update-banner'; el.setAttribute('role', 'status');
  el.innerHTML = `<span>Agentic was updated. This page still runs the old version — reload to use the new one.</span>
    <button class="btn xs primary" data-act="reload-page">Reload</button><button class="btn xs ghost" data-act="dismiss-update" aria-label="Dismiss">✕</button>`;
  document.body.appendChild(el);
}
ACTIONS['reload-page'] = () => location.reload();
/**
 * An AI provider is out of credit: administrators see it on every page until it works again, with a button that turns it
 * back on at once (after balance was added). Hidden here, it comes back when another provider runs out.
 */
function renderPausedBanner(items) {
  let el = document.getElementById('paused-banner');
  const hidden = (S.pausedHidden || '');
  const show = can('admin') ? items.filter(p => !hidden.split('|').includes(p.provider + '@' + p.sinceUtc)) : [];
  if (!show.length) { if (el) el.remove(); return; }
  if (!el) { el = document.createElement('div'); el.id = 'paused-banner'; el.className = 'update-banner paused-banner'; el.setAttribute('role', 'alert'); document.body.appendChild(el); }
  const p = show[0];
  el.dataset.key = show.map(x => x.provider + '@' + x.sinceUtc).join('|');
  el.innerHTML = `<svg viewBox="0 0 24 24" class="ic"><path d="M12 9v4M12 17h.01M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z"/></svg>` +
    `<span><b>${esc(p.provider)}</b> is out of credit: its models are paused and agents use other providers${show.length > 1 ? ` (and ${show.length - 1} more provider${show.length > 2 ? 's' : ''})` : ''}. Added balance?</span>` +
    `<button class="btn xs primary" data-act="resume-provider" data-provider="${esc(p.provider)}">Turn back on</button>` +
    `<a class="btn xs ghost" href="/models" data-nav>Details</a><button class="btn xs ghost" data-act="hide-paused" aria-label="Hide">✕</button>`;
}
ACTIONS['hide-paused'] = () => { const el = document.getElementById('paused-banner'); if (!el) return; S.pausedHidden = el.dataset.key; el.remove(); };
ACTIONS['resume-provider'] = async el => {
  const provider = el.dataset.provider;
  el.disabled = true;
  try {
    const r = await post(`/api/models/providers/${enc(provider)}/resume`);
    toast(`${provider} is back on: ${(r.models || []).join(', ')} take calls again.`, 'ok');
    renderPausedBanner(r.items || []);
    if (S.view === VIEWS.models) refreshNow();
  } catch (e) { el.disabled = false; toast(e.message, 'bad'); }
};
ACTIONS['dismiss-update'] = () => { const el = document.getElementById('update-banner'); if (el) el.remove(); };
/**
 * POSTs and reads a one-shot server-sent-event reply that ends with `event: done` (data returned) or `event: error`
 * (thrown). Keep-alive comment pings are ignored. Used for long operations (compaction) so a proxy's read timeout can't
 * cut them off, the way the chat message stream avoids it.
 */
async function postEventResult(url, body) {
  const opts = { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json', 'Accept': 'text/event-stream' }, body: JSON.stringify(body || {}) };
  if (S.csrf) opts.headers['X-CSRF-Token'] = S.csrf;
  progress(true);
  let res;
  try { res = await fetch(url, opts); } finally { progress(false); }
  noteBuild(res);
  if (res.status === 401) { showLogin(); throw new ApiError(401, 'UNAUTHORIZED', 'Please sign in.'); }
  if (!res.ok) { let d = null; try { d = JSON.parse(await res.text()); } catch { /* ignore */ } const e = d && d.error ? d.error : { code: 'HTTP_' + res.status, message: res.statusText }; throw new ApiError(res.status, e.code, e.message, d); }
  const reader = res.body.getReader(), dec = new TextDecoder();
  let buf = '', done, errMsg = null;
  for (; ;) {
    const r = await reader.read();
    if (r.done) break;
    buf += dec.decode(r.value, { stream: true });
    let cut;
    while ((cut = buf.indexOf('\n\n')) >= 0) {
      const raw = buf.slice(0, cut); buf = buf.slice(cut + 2);
      const type = (raw.match(/^event: (.*)$/m) || [])[1], dataLine = (raw.match(/^data: (.*)$/m) || [])[1];
      if (!type || !dataLine) continue;
      let d; try { d = JSON.parse(dataLine); } catch { continue; }
      if (type === 'done') done = d; else if (type === 'error') errMsg = d.message || 'The operation failed.';
    }
  }
  if (errMsg) throw new ApiError(500, 'OPERATION_FAILED', errMsg);
  if (done === undefined) throw new ApiError(0, 'NO_RESULT', 'The connection ended before the result arrived.');
  return done;
}
const put = (url, body) => api('PUT', url, body || {});
const patch = (url, body) => api('PATCH', url, body || {});
const del = url => api('DELETE', url);

// ------------------------------------------------------------------ auth & boot
// The address of this server for links people copy (share links, API base URLs). The desktop app and VS Code show
// this page from 127.0.0.1; there the server's own address (from /api/auth/me) is used instead.
function serverOrigin() {
  const loopback = /^(127\.\d+\.\d+\.\d+|localhost|\[::1\]|::1)$/i.test(location.hostname);
  const pub = (S.me && S.me.publicOrigin) || (typeof CHAT !== 'undefined' && CHAT.meta && CHAT.meta.publicOrigin);
  return loopback && pub ? pub : location.origin;
}
function showLogin() {
  $('#shell').classList.add('hidden');
  $('#login').classList.remove('hidden');
  if (S.es) { S.es.close(); S.es = null; }
  // The next sign-in may have other permissions (an administrator changed them) or be another account: the page it
  // returns to is rendered again from fresh data instead of being updated in place.
  S.view = null;
}
async function boot() {
  try {
    const me = await get('/api/auth/me');
    S.user = me.user; S.csrf = me.csrfToken; S.me = me;
    // This account's keyboard shortcuts (keys.js); the browser's copy works until they arrive.
    if (typeof keySync === 'function') { keyHints(); keySync(); }
    $('#whoami').textContent = me.displayName || me.user;
    $('#whoami').setAttribute('data-tip', `${me.user} · ${me.isAdmin ? 'Administrator' : 'User'}${!me.isAdmin && me.allowedAgents && me.allowedAgents.length ? ' · agents: ' + me.allowedAgents.join(', ') : ''}`);
    applyNavPermissions();
    showBalance(me);
    $('#login').classList.add('hidden');
    $('#shell').classList.remove('hidden');
    // A multitask pane leaves live events and badges to the page around it (browsers allow few connections per server).
    if (IN_PANE) { navigate(location.pathname + location.search, false); return; }
    try {
      const recent = await get('/api/events/recent?limit=150');
      S.feed = recent.map(r => ({ seq: r.id !== undefined ? 'h' + r.id : undefined, ts: r.createdUtc, agentId: r.agentId, type: r.eventType, level: r.level, summary: r.summary, correlationId: r.correlationId }));
    } catch { /* feed starts empty */ }
    connectEvents();
    navigate(location.pathname + location.search, false);
    refreshBadges();
    setInterval(refreshBadges, 20000);
  } catch (e) { if (e.status !== 401) toast(e.message, 'bad'); }
}
/** The account balance in the user menu (administrators are never charged). */
function showBalance(me) {
  const b = $('#menu-balance');
  if (b) b.textContent = me && me.charged ? money(me.balanceMicros, 2) : 'not charged';
}
async function refreshBadges() {
  try {
    const st = await get('/api/status');
    S.status = st;
    // The logo already says "Agentic"; only show the instance name when it adds something.
    $('#instance-name').textContent = /^agentic$/i.test((st.name || '').trim()) ? '' : st.name;
    $('#version').textContent = 'v' + st.version + ' · up ' + uptime(st.uptimeSeconds);
    const a = st.pendingApprovals || 0;
    const pill = $('#nav-approvals'); pill.textContent = a; pill.classList.toggle('hidden', a === 0);
    $('#top-approvals-count').textContent = a; $('#top-approvals').classList.toggle('alert', a > 0);
    const nr = st.newResults || 0;
    const rp = $('#nav-results'); if (rp) { rp.textContent = nr; rp.classList.toggle('hidden', nr === 0); }
    const subs = (st.agents && st.agents.activeSubagents) || 0;
    const sp = $('#nav-subagents'); sp.textContent = subs; sp.classList.toggle('hidden', subs === 0);
    // Global tasks under way: shown from the first page, not only once the Global tasks page was opened.
    const gt = st.activeGlobalTasks || 0;
    const gp = $('#nav-global'); if (gp && st.activeGlobalTasks !== undefined) { gp.textContent = gt; gp.classList.toggle('hidden', gt === 0); }
    renderPausedBanner(st.pausedProviders || []);
  } catch { /* ignore */ }
}

// ------------------------------------------------------------------ live events (SSE)
/**
 * The live event stream (the "Live updates" light in the sidebar). The browser retries a dropped connection by itself,
 * but gives up for good after an error response - which is what the desktop app's and VS Code's local proxy answers while
 * the server restarts (502). Then a new stream is opened, sooner and sooner after each wait (2 s … 30 s), and at once
 * when the computer comes back online or the page is shown again. After a reconnect the badges and version are reloaded.
 */
function connectEvents() {
  if (S.es) S.es.close();
  clearTimeout(S.esRetryTimer); S.esRetryTimer = 0;
  const es = new EventSource('/api/events/stream');
  S.es = es;
  const conn = $('#conn');
  es.onopen = () => {
    conn.textContent = 'Live updates'; conn.className = 'conn live';
    if (S.esRetry) { S.esRetry = 0; refreshBadges(); if (S.view && S.view.live) scheduleRefresh(); }
  };
  es.onerror = () => {
    conn.textContent = 'Reconnecting…'; conn.className = 'conn down';
    S.esRetry = (S.esRetry || 0) + 1;
    if (es.readyState !== EventSource.CLOSED || S.es !== es || S.esRetryTimer) return;
    const wait = Math.min(30000, 1000 * Math.pow(2, Math.min(S.esRetry, 5)));
    S.esRetryTimer = setTimeout(() => { S.esRetryTimer = 0; if (S.es === es) connectEvents(); }, wait);
  };
  es.onmessage = msg => {
    let e; try { e = JSON.parse(msg.data); } catch { return; }
    S.feed.unshift(e);
    if (S.feed.length > 500) S.feed.length = 500;
    const feedEl = $('#live-feed');
    if (feedEl && feedMatches(feedEl, e)) { feedEl.insertAdjacentHTML('afterbegin', feedRow(e, true)); while (feedEl.children.length > 300) feedEl.lastElementChild.remove(); }
    if (['AiCallStarted', 'AiCallCompleted', 'ToolStarted', 'ToolCompleted', 'TaskProgress', 'TaskStarted', 'TaskCompleted', 'TaskFailed'].includes(e.type) && !isRoutineEvent(e)) {
      if (!S.consoleBuffer) S.consoleBuffer = [];
      S.consoleBuffer.unshift(e);
      if (S.consoleBuffer.length > 500) S.consoleBuffer.length = 500;
      const consoleEl = $('#console-stream');
      if (consoleEl && S.view && S.view === VIEWS.console) {
        // Follow new lines only while the reader is at the end; someone scrolled up to read stays where they are.
        const atEnd = consoleEl.scrollHeight - consoleEl.scrollTop - consoleEl.clientHeight < 40;
        consoleEl.insertAdjacentHTML('beforeend', renderConsoleLine(e, true));
        while (consoleEl.children.length > 300) consoleEl.firstElementChild.remove();
        if (atEnd) consoleEl.scrollTop = consoleEl.scrollHeight;
      }
    }
    if (['ApprovalRequested', 'ApprovalResolved', 'SubagentSpawned', 'SubagentFinished'].includes(e.type)) refreshBadges();
    // A provider ran out of credit (or works again): the notice for administrators follows at once.
    if (e.type === 'ModelHealthChanged' && e.data && (e.data.outOfCredit || e.data.resumed)) {
      refreshBadges();
      if (e.data.outOfCredit && e.data.firstTime && can('admin')) { toast(`${e.data.provider} is out of credit: its models are paused. Add balance, then turn it back on.`, 'bad'); playSound('agentNeedsYou'); }
    }
    // An agent waits for a person: a tool approval or a question (Inbox), or a global task's plan.
    if ((e.type === 'ApprovalRequested' && can('approvals')) || (e.type === 'GlobalTaskUpdated' && e.data && e.data.status === 'AwaitingApproval' && can('global.tasks'))) playSound('agentNeedsYou');
    if (S.view && S.view.live) scheduleRefresh();
  };
}
function reviveEvents() { if (S.es && S.es.readyState === EventSource.CLOSED) connectEvents(); }
window.addEventListener('online', reviveEvents);
document.addEventListener('visibilitychange', () => { if (!document.hidden) reviveEvents(); });
function feedMatches(el, e) {
  // Routine churn (Idle ↔ Running, a minute-by-minute health check) only shows where the feed asks for everything.
  if (isRoutineEvent(e) && !el.dataset.routine) return false;
  const a = el.dataset.agent; return !a || e.agentId === a || (e.agentId || '').startsWith(a + '.sub-');
}
const ROUTINE_STATES = new Set(['Idle', 'Running', 'Waiting', 'Starting']);
/** The same rule the server uses to keep these out of the activity history (ActivityPersister.IsRoutine). */
function isRoutineEvent(e) {
  if (!e || e.level !== 'Info' || !e.data) return false;
  if (e.type === 'AgentStateChanged') return ROUTINE_STATES.has(e.data.from) && ROUTINE_STATES.has(e.data.to);
  if (e.type === 'TaskCreated' || e.type === 'TaskStarted' || e.type === 'TaskCompleted') return e.data.kind === 'Polling';
  return false;
}
function feedRow(e, isNew) {
  return `<div class="feed-item ${esc(e.level)}${isNew ? ' new' : ''}"${e.seq !== undefined ? ` data-key="${esc(String(e.seq))}"` : ''}><span class="t">${esc(time(e.ts))}</span><span class="a">${e.agentId ? agentLink(e.agentId) : '<span class="muted">host</span>'}</span>` +
    `<span class="s">${tag(e.type)} ${esc(e.summary)}${e.correlationId ? ' ' + link('/correlations/' + enc(e.correlationId), 'workflow') : ''}</span></div>`;
}
function scheduleRefresh() {
  if (S.refreshTimer) return;
  const wait = Math.max(0, 4000 - (Date.now() - S.lastRefresh));
  S.refreshTimer = setTimeout(() => { S.refreshTimer = null; render(true); }, wait);
}
function canAutoRefresh() {
  const modal = $('#modal');
  if (!modal.classList.contains('hidden') && !modal.classList.contains('closing')) return false;
  if (!$('#palette').classList.contains('hidden')) return false;
  if (S.dragging) return false;
  if (document.activeElement && $('#main').contains(document.activeElement) && /^(INPUT|TEXTAREA|SELECT)$/.test(document.activeElement.tagName)) return false;
  if ($$('.menu:not(.hidden)').length) return false;
  return true;
}

// ------------------------------------------------------------------ router
const ROUTES = [
  [/^\/$/, 'overview'], [/^\/operations$/, 'operations'], [/^\/console$/, 'console'], [/^\/agents$/, 'agents'], [/^\/agents\/new$/, 'editor'],
  [/^\/agents\/([^/]+)\/edit$/, 'editor'], [/^\/agents\/([^/]+)(?:\/([a-z-]+))?$/, 'agent'], [/^\/subagents$/, 'subagents'],
  [/^\/tasks$/, 'tasks'], [/^\/learning$/, 'learning'], [/^\/communications$/, 'communications'], [/^\/correlations\/([^/]+)$/, 'correlation'],
  [/^\/approvals$/, 'approvals'], [/^\/finance$/, 'finance'], [/^\/models$/, 'models'], [/^\/models\/new$/, 'modelEditor'],
  [/^\/models\/custom\/([^/]+)$/, 'modelEditor'], [/^\/mcp$/, 'mcp'], [/^\/sandbox$/, 'sandbox'], [/^\/events$/, 'events'], [/^\/config$/, 'config'], [/^\/audit$/, 'audit'], [/^\/users$/, 'users'], [/^\/model-control$/, 'modelControl'],
  [/^\/global-tasks$/, 'globalTasks'], [/^\/global-tasks\/([^/]+)$/, 'globalTask'],
  [/^\/results$/, 'results'], [/^\/results\/([^/]+)(?:\/([a-z-]+))?$/, 'result'],
  [/^\/chat(?:\/([^/]+))?$/, 'chat'], [/^\/custom-models$/, 'customModels'], [/^\/my-models$/, 'myModels'], [/^\/my-mcp$/, 'myMcp'], [/^\/custom-models\/(new|edit)$/, 'customModelEdit'], [/^\/swagger$/, 'swagger'], [/^\/updates(?:\/(desktop|cli|vscode|claude))?$/, 'updates'],
  [/^\/model-square$/, 'modelSquare'], [/^\/balance$/, 'balance'], [/^\/artifacts$/, 'artifacts'], [/^\/secrets$/, 'secrets'],
  [/^\/audio(?:\/(speak|voices|personalities|dictation|settings))?$/, 'audio'], [/^\/images(?:\/(create|gallery|tools|settings))?$/, 'images'],
  [/^\/music(?:\/(create|library|settings))?$/, 'music'],
  [/^\/money(?:\/(crypto|currencies|settings))?$/, 'money'], [/^\/stocks$/, 'stocks'], [/^\/polymarket(?:\/(markets|new|leaderboard|wallet))?$/, 'polymarket'], [/^\/news(?:\/(all|international|us|uk|europe|australia|agencies|investigative|business|sources))?$/, 'news']
];
function navigate(path, push = true) {
  const [p, q] = path.split('?');
  // A multitask pane shows chats only: other pages open in the page around it.
  if (IN_PANE && !/^\/chat(?:\/[^/]+)?$/.test(p)) { try { window.parent.navigate(path); } catch { /* the pane was closed */ } return; }
  // A page this user may not open: home goes to the first page they may open; any other shows "no access".
  const need = pagePerm(p);
  if (S.me && need && !can(need) && p === '/' && homePath() !== '/') {
    history.replaceState({}, '', homePath());
    return navigate(homePath(), false);
  }
  let name = 'overview', params = [];
  for (const [re, n] of ROUTES) { const m = p.match(re); if (m) { name = n; params = m.slice(1).map(x => x === undefined ? undefined : decodeURIComponent(x)); break; } }
  if (S.me && need && !can(need)) name = 'noAccess';
  if (push) history.pushState({}, '', path);
  const prevView = S.view, prevParams = S.params;
  S.view = VIEWS[name];
  S.params = { path: p, args: params, query: new URLSearchParams(q || '') };
  $$('#nav a').forEach(a => { const h = a.getAttribute('href'); a.classList.toggle('active', h === '/' ? p === '/' : p === h || p.startsWith(h + '/')); });
  $('#sidebar').classList.remove('open');
  // The same page with other arguments (a tab, a filter, another conversation) updates in place instead of reloading.
  const samePage = prevView === S.view && prevParams && $('#main').childElementCount > 0;
  if (samePage && S.view.update && S.view.update(S.params, prevParams) === true) return;
  S.sel = {};
  render(false, samePage);
  if (!samePage) { $('#main').focus({ preventScroll: true }); window.scrollTo(0, 0); }
}
window.addEventListener('popstate', () => navigate(location.pathname + location.search, false));
VIEWS.noAccess = {
  live: false,
  async render() {
    setCrumbs([['No access', location.pathname]]);
    return pageHead('No access') + card('', emptyState('You do not have access to this page', 'Ask an administrator for the permission it needs.'));
  }
};

function setCrumbs(list) {
  S.crumbs = list;
  $('#crumbs').innerHTML = list.map((c, i) => i === list.length - 1 ? `<span class="here">${esc(c[0])}</span>` : link(c[1], c[0]) + '<span class="sep">/</span>').join('');
}

/**
 * Renders the current view. A first render shows a skeleton (it fades in only if loading is slow), then plays the
 * entrance animation; a live refresh swaps the content silently, keeps the scroll position and flashes values that changed.
 */
async function render(isRefresh, soft) {
  if (!S.view) return;
  if (S.loading) { if (!isRefresh) S.queued = { soft: !!soft }; return; }
  if (isRefresh && (!S.view.live || !canAutoRefresh())) { if (isRefresh && S.view.live) scheduleRefresh(); return; }
  S.loading = true;
  const main = $('#main');
  const scroll = window.scrollY;
  const view = S.view, params = S.params;
  if (!isRefresh) progress(true);
  try {
    if (!isRefresh && !soft) { main.classList.remove('enter'); main.innerHTML = skeleton(); }
    if (soft) main.classList.add('soft-loading');
    const html = await view.render(params);
    if (isRefresh && !canAutoRefresh()) return;
    if (S.view !== view || S.params !== params) return; // the user moved on while this loaded
    const before = isRefresh ? liveValues(main) : null;
    clearTimeout(S.enterTimer);
    main.classList.remove('enter');
    // Continuous animations (pulsing dots, activity bars) follow a shared clock so a refresh does not restart them.
    main.style.setProperty('--clock', `-${Date.now() % 2000}ms`);
    // Soft updates and live refreshes change only what differs, so nothing on screen blinks or loses its hover state.
    if (soft || isRefresh) morphInto(main, html, soft); else main.innerHTML = html;
    if (!isRefresh && !soft && !reducedMotion()) {
      void main.offsetWidth;
      main.classList.add('enter');
      S.enterTimer = setTimeout(() => main.classList.remove('enter'), 1500);
    }
    applyPermissions(main);
    applyMeters(main, !isRefresh);
    drawCharts(main, !isRefresh);
    if (S.view.after) S.view.after(S.params, main);
    updateBulkBars();
    if (isRefresh || soft) { window.scrollTo(0, scroll); if (isRefresh) flashChanges(main, before); } else countUp(main);
  } catch (e) {
    if (e.status !== 401) main.innerHTML = card('Could not load this page', `<p class="error-text">${esc(e.message)}</p>`);
  } finally {
    S.loading = false; S.lastRefresh = Date.now(); main.classList.remove('soft-loading');
    if (!isRefresh) progress(false);
    if (S.queued) { const q = S.queued; S.queued = null; render(false, q.soft); }
  }
}

/**
 * Updates the DOM under root to match html, touching only nodes that differ (a small morphdom). Elements that are new or
 * replaced get .swap-in so they fade in; with markChanged the changed top-level sections fade in too.
 */
function morphInto(root, html, markChanged) {
  const t = document.createElement('template');
  t.innerHTML = html;
  const fresh = !reducedMotion() && markChanged;
  const oldKids = Array.from(root.childNodes), newKids = Array.from(t.content.childNodes);
  newKids.forEach((n, i) => {
    const o = oldKids[i];
    if (!o) { root.appendChild(n); if (fresh && n.nodeType === 1) swapIn(n); return; }
    const changed = fresh && o.nodeType === 1 && n.nodeType === 1 && !o.isEqualNode(n);
    const result = morphNode(o, n, fresh);
    if (changed && result === o && !o.matches('.tabs, .page-head, .grid.kpis')) swapIn(o);
  });
  for (let i = newKids.length; i < oldKids.length; i++) oldKids[i].remove();
}
function morphNode(o, n, fresh) {
  if (o.nodeType !== n.nodeType || o.nodeName !== n.nodeName || (o.nodeType === 1 && (o.id !== n.id || o.dataset.morph === 'replace'))) {
    o.replaceWith(n); if (fresh && n.nodeType === 1) swapIn(n); return n;
  }
  if (o.nodeType !== 1) { if (o.nodeValue !== n.nodeValue) o.nodeValue = n.nodeValue; return o; }
  if (o.isEqualNode(n)) return o;
  // data-morph="keep": code draws inside it and updates it itself (the communications topology), so a refresh leaves it be.
  if (o.dataset.morph === 'keep' && n.dataset.morph === 'keep') return o;
  // What the user changed is kept: text typed into a field they then left (an Inbox answer, a price), a choice in a list,
  // a details section they opened. Markup never carries style attributes (CSP), so any style here was set by code
  // (bar widths, delays) and stays too - removing it made bars snap to zero and grow again on every refresh.
  const field = o.nodeName === 'INPUT' || o.nodeName === 'TEXTAREA' || o.nodeName === 'SELECT';
  const edited = field && fieldEdited(o);
  if (edited && o.nodeName !== 'INPUT') return o;
  const keepOpen = o.nodeName === 'DETAILS';
  const prevState = o.getAttribute('data-state');
  for (const a of Array.from(o.attributes)) if (!n.hasAttribute(a.name) && a.name !== 'style' && !(keepOpen && a.name === 'open')) o.removeAttribute(a.name);
  for (const a of Array.from(n.attributes)) if (o.getAttribute(a.name) !== a.value && !(keepOpen && a.name === 'open') && !(edited && (a.name === 'value' || a.name === 'checked'))) o.setAttribute(a.name, a.value);
  if (field) {
    if (document.activeElement !== o && !edited) { o.value = n.value; if ('checked' in n) o.checked = n.checked; }
    if (o.nodeName === 'INPUT') return o;
  }
  // A status that changed (an agent going Running → Idle, a step finishing) glows once.
  if (prevState !== null && prevState !== o.getAttribute('data-state')) stateFlip(o);
  const oc = Array.from(o.childNodes), nc = Array.from(n.childNodes);
  if (keyedList(oc) && keyedList(nc)) { morphKeyed(o, oc, nc); return o; }
  nc.forEach((child, i) => { if (oc[i]) morphNode(oc[i], child, false); else o.appendChild(child); });
  for (let i = nc.length; i < oc.length; i++) oc[i].remove();
  return o;
}
/** Whether the user changed a field since it was rendered (value or checked state differs from the markup's). */
function fieldEdited(el) {
  if (el.nodeName === 'SELECT') return Array.from(el.options).some(op => op.selected !== op.defaultSelected);
  if (el.type === 'checkbox' || el.type === 'radio') return el.checked !== el.defaultChecked;
  if (el.type === 'hidden' || el.type === 'button' || el.type === 'submit') return false;
  return el.value !== el.defaultValue;
}
/** Glows once on a status change. Batched into one frame: many rows changing together cost no forced layouts. */
const FLIPS = new Set();
let flipFrame = 0;
function stateFlip(el) {
  if (reducedMotion()) return;
  el.classList.remove('state-flip');
  FLIPS.add(el);
  if (flipFrame) return;
  flipFrame = requestAnimationFrame(() => {
    flipFrame = 0;
    const list = Array.from(FLIPS); FLIPS.clear();
    list.forEach(e => { e.classList.add('state-flip'); clearTimeout(e._flipTimer); e._flipTimer = setTimeout(() => e.classList.remove('state-flip'), 1300); });
  });
}
/** Children that all carry a unique data-key (table rows, agent cards, feed lines) are matched by key, not position. */
function keyedList(list) {
  if (!list.length) return false;
  const seen = new Set();
  for (const c of list) {
    if (c.nodeType !== 1 || !c.hasAttribute('data-key')) return false;
    const k = c.getAttribute('data-key');
    if (seen.has(k)) return false;
    seen.add(k);
  }
  return true;
}
/**
 * Keyed update: rows that stay are updated in place and moved to their new position (gliding there), new rows slide in,
 * rows that are gone are removed. A task moved up the queue, a new message or a finished subagent is easy to follow.
 */
function morphKeyed(parent, oc, nc) {
  const motion = !reducedMotion();
  const byKey = new Map(oc.map(c => [c.getAttribute('data-key'), c]));
  const keep = nc.map(c => c.getAttribute('data-key')).filter(k => byKey.has(k));
  const oldKeep = oc.map(c => c.getAttribute('data-key')).filter(k => keep.includes(k));
  const before = motion && keep.join('\u0001') !== oldKeep.join('\u0001') ? new Map(keep.map(k => [k, byKey.get(k).getBoundingClientRect().top])) : null;
  const placed = new Map(), fresh = [];
  nc.forEach((n, i) => {
    const k = n.getAttribute('data-key');
    let node = byKey.get(k);
    if (node) { byKey.delete(k); node = morphNode(node, n, false); } else { node = n; fresh.push(node); }
    placed.set(k, node);
    const at = parent.children[i];
    if (at !== node) parent.insertBefore(node, at || null);
  });
  byKey.forEach(c => c.remove());
  if (!motion) return;
  if (before) before.forEach((top, k) => {
    const node = placed.get(k), dy = top - node.getBoundingClientRect().top;
    if (Math.abs(dy) > 2 && node.animate) node.animate([{ transform: `translateY(${dy}px)` }, { transform: 'none' }], { duration: 420, easing: 'cubic-bezier(.2,.8,.2,1)' });
  });
  // A whole new list (another filter) appears without a burst of animations; a few new rows slide in.
  if (fresh.length && fresh.length <= 8) fresh.forEach(el => { el.classList.add('row-in'); setTimeout(() => el.classList.remove('row-in'), 950); });
}
function swapIn(el) { el.classList.remove('swap-in'); void el.offsetWidth; el.classList.add('swap-in'); setTimeout(() => el.classList.remove('swap-in'), 450); }
function skeleton() {
  const kpis = '<div class="card sk-card"><div class="sk sk-line short"></div><div class="sk sk-num"></div></div>'.repeat(5);
  return `<div class="skeleton-page" aria-hidden="true"><div class="sk sk-title"></div><div class="sk sk-sub"></div><div class="grid kpis">${kpis}</div>` +
    `<div class="card section sk-block">${'<div class="sk sk-line"></div>'.repeat(6)}</div></div><span class="sr-only" role="status">Loading…</span>`;
}
/** Text of every element with data-k, so a refresh can tell which values changed. */
function liveValues(root) { const m = new Map(); $$('[data-k]', root).forEach(el => m.set(el.dataset.k, el.textContent)); return m; }
function flashChanges(root, before) {
  if (!before || reducedMotion()) return;
  $$('[data-k]', root).forEach(el => {
    const prev = before.get(el.dataset.k);
    if (prev !== undefined && prev !== el.textContent) { el.classList.remove('changed'); void el.offsetWidth; el.classList.add('changed'); }
  });
}
/** Counts KPI numbers up from zero on a page's first render, keeping their format ($, decimals, separators, suffixes). */
function countUp(root) {
  if (reducedMotion()) return;
  $$('.kpi .value, .chart-meta .value', root).forEach(el => {
    const node = el.firstChild;
    if (!node || node.nodeType !== 3) return;
    const m = node.textContent.match(/^(\D*?)(\d[\d,]*(?:\.\d+)?)(.*)$/s);
    if (!m) return;
    const target = Number(m[2].replace(/,/g, ''));
    if (!isFinite(target) || target <= 0) return;
    const decimals = (m[2].split('.')[1] || '').length, grouped = m[2].includes(',');
    const fmt = v => grouped ? v.toLocaleString(undefined, { minimumFractionDigits: decimals, maximumFractionDigits: decimals }) : v.toFixed(decimals);
    const start = performance.now(), duration = 750;
    const step = now => {
      const p = Math.min(1, (now - start) / duration), eased = 1 - Math.pow(1 - p, 3);
      node.textContent = m[1] + fmt(target * eased) + m[3];
      if (p < 1 && node.isConnected) requestAnimationFrame(step); else node.textContent = m[0];
    };
    node.textContent = m[1] + fmt(0) + m[3];
    requestAnimationFrame(step);
  });
}
/** Thin progress bar at the top of the window while pages load or changes are saved (shown after 120 ms). */
let progressDepth = 0, progressTimer = null;
function progress(on) {
  const el = $('#progress');
  if (!el) return;
  progressDepth = Math.max(0, progressDepth + (on ? 1 : -1));
  if (progressDepth > 0) {
    if (!el.classList.contains('active') && !progressTimer) {
      progressTimer = setTimeout(() => { progressTimer = null; if (progressDepth > 0) { el.classList.remove('done'); void el.offsetWidth; el.classList.add('active'); } }, 120);
    }
  } else {
    clearTimeout(progressTimer); progressTimer = null;
    if (el.classList.contains('active')) { el.classList.remove('active'); el.classList.add('done'); setTimeout(() => { if (!progressDepth) el.classList.remove('done'); }, 450); }
  }
}
function rerender() { render(false).then(() => { }); }
/** Shows the latest data now: a live page refreshes; a page that does not refresh by itself but shows what was just changed
 * (reloadable, e.g. Users & access) is drawn again in place. */
function refreshNow() {
  S.lastRefresh = 0;
  if (S.view && !S.view.live && S.view.reloadable) { render(false, true); return; }
  render(true);
}

// ------------------------------------------------------------------ selection (checkbox tables + bulk bars)
function selSet(scope) { return S.sel[scope] || (S.sel[scope] = new Set()); }
function selected(scope) { return Array.from(selSet(scope)); }
function clearSel(scope) { S.sel[scope] = new Set(); }
CHANGES['sel'] = el => {
  const set = selSet(el.dataset.scope);
  if (el.checked) set.add(el.dataset.id); else set.delete(el.dataset.id);
  el.closest('tr').classList.toggle('selected', el.checked);
  updateBulkBars();
};
CHANGES['sel-all'] = el => {
  const table = el.closest('table');
  $$('input[data-change="sel"]', table).forEach(cb => { cb.checked = el.checked; CHANGES.sel(cb); });
};
function updateBulkBars() {
  $$('.bulkbar[data-scope]').forEach(bar => {
    const n = selSet(bar.dataset.scope).size;
    bar.classList.toggle('hidden', n === 0);
    const c = $('.count', bar); if (c) c.textContent = `${n} selected`;
  });
}
function bulkBar(scope, buttons) { return `<div class="bulkbar hidden" data-scope="${esc(scope)}"><span class="count"></span>${buttons}<button class="btn small ghost" data-act="sel-clear" data-scope="${esc(scope)}">Clear</button></div>`; }
ACTIONS['sel-clear'] = el => { clearSel(el.dataset.scope); $$(`input[data-scope="${CSS.escape(el.dataset.scope)}"]`).forEach(cb => { cb.checked = false; const tr = cb.closest('tr'); if (tr) tr.classList.remove('selected'); }); updateBulkBars(); };

// ------------------------------------------------------------------ drag & drop (rows with data-drag-scope / data-drag-id)
document.addEventListener('dragstart', ev => {
  const row = ev.target.closest && ev.target.closest('[data-drag-id]');
  if (!row) return;
  S.dragging = { scope: row.dataset.dragScope, id: row.dataset.dragId };
  row.classList.add('dragging');
  ev.dataTransfer.effectAllowed = 'move';
  try { ev.dataTransfer.setData('text/plain', row.dataset.dragId); } catch { /* ignore */ }
});
document.addEventListener('dragover', ev => {
  if (!S.dragging) return;
  const row = ev.target.closest && ev.target.closest('[data-drag-id]');
  $$('.drop-before, .drop-after').forEach(r => r.classList.remove('drop-before', 'drop-after'));
  if (!row || row.dataset.dragScope !== S.dragging.scope || row.dataset.dragId === S.dragging.id) return;
  ev.preventDefault();
  const r = row.getBoundingClientRect();
  row.classList.add(ev.clientY < r.top + r.height / 2 ? 'drop-before' : 'drop-after');
});
document.addEventListener('drop', ev => {
  if (!S.dragging) return;
  const row = ev.target.closest && ev.target.closest('[data-drag-id]');
  const d = S.dragging;
  const pos = row && row.classList.contains('drop-before') ? 'before' : 'after';
  $$('.drop-before, .drop-after, .dragging').forEach(r => r.classList.remove('drop-before', 'drop-after', 'dragging'));
  S.dragging = null;
  if (!row || row.dataset.dragId === d.id || row.dataset.dragScope !== d.scope) return;
  ev.preventDefault();
  const fn = DROPS[d.scope.split(':')[0]];
  if (fn) Promise.resolve(fn(d, row.dataset.dragId, pos)).catch(e => toast(e.message, 'bad'));
});
document.addEventListener('dragend', () => { $$('.drop-before, .drop-after, .dragging').forEach(r => r.classList.remove('drop-before', 'drop-after', 'dragging')); S.dragging = null; });

// ------------------------------------------------------------------ modal & dialogs
function openModal(title, body, actions = '', size = '') {
  const modal = $('#modal'), card = $('#modal-card');
  clearTimeout(S.modalTimer);
  const wasOpen = !modal.classList.contains('hidden');
  modal.classList.remove('closing');
  card.className = 'modal-card' + (size ? ' ' + size : '');
  card.innerHTML = `<div class="modal-head"><h2>${esc(title)}</h2><button class="btn icon ghost x" data-act="modal-close" aria-label="Close" data-tip="Close${esc(keyTip('app.close'))}">${icon('x')}</button></div>` +
    `<div class="modal-body">${body}</div>${actions ? `<div class="modal-actions">${actions}</div>` : ''}`;
  modal.classList.remove('hidden');
  // Replacing an open dialog's content (e.g. a confirmation after details) replays the entrance.
  if (wasOpen) { card.style.animation = 'none'; void card.offsetWidth; card.style.animation = ''; }
  applyPermissions(card);
  applyMeters(card, true);
  const first = $('#modal-card .modal-body input:not([type=hidden]):not([type=checkbox]), #modal-card .modal-body textarea, #modal-card .modal-body select');
  if (first) first.focus();
}
function closeModal() {
  if (S.confirmResolve) { const r = S.confirmResolve; S.confirmResolve = null; r(null); }
  const modal = $('#modal');
  if (modal.classList.contains('hidden') || modal.classList.contains('closing')) return;
  const finish = () => { modal.classList.add('hidden'); modal.classList.remove('closing'); $('#modal-card').innerHTML = ''; };
  if (reducedMotion()) { finish(); return; }
  modal.classList.add('closing');
  S.modalTimer = setTimeout(finish, 170);
}
$('#modal').addEventListener('mousedown', e => { if (e.target.id === 'modal') closeModal(); });
/** Styled confirmation. Resolves true/false (or the typed text when opts.input is set). */
function confirmBox(title, message, opts = {}) {
  return new Promise(resolve => {
    S.confirmResolve = null;
    openModal(title, `<p>${message}</p>${opts.input ? `<label class="section">${esc(opts.input)}${opts.multiline ? `<textarea id="confirm-input" rows="6">${esc(opts.value || '')}</textarea>` : `<input id="confirm-input" autocomplete="off" value="${esc(opts.value || '')}">`}</label>` : ''}`,
      `<button class="btn" data-act="modal-close">Cancel</button><button class="btn ${opts.danger ? 'danger-solid' : 'primary'}" data-act="confirm-ok">${esc(opts.confirm || 'Confirm')}</button>`, 'narrow');
    S.confirmResolve = resolve;
  });
}
ACTIONS['confirm-ok'] = () => {
  const r = S.confirmResolve; S.confirmResolve = null;
  const input = $('#confirm-input');
  const value = input ? input.value : true;
  closeModal();
  if (r) r(value);
};
ACTIONS['modal-close'] = () => closeModal();

// ------------------------------------------------------------------ clipboard
/**
 * Copies text: through the app around the page when it offers it (VS Code: its webview does not let the page use the
 * clipboard), else the browser's clipboard, else the old copy command. True when it worked.
 */
async function writeClipboard(text) {
  text = String(text ?? '');
  const host = window.chatClient;
  if (host && typeof host.copy === 'function') { try { await host.copy(text); return true; } catch { /* try the others */ } }
  if (navigator.clipboard && navigator.clipboard.writeText) {
    try { await Promise.race([navigator.clipboard.writeText(text), new Promise((_, no) => setTimeout(() => no(new Error('timeout')), 1500))]); return true; } catch { /* try the old way */ }
  }
  try {
    const ta = document.createElement('textarea');
    ta.value = text; ta.setAttribute('readonly', ''); ta.style.cssText = 'position:fixed;top:-1000px;left:0;opacity:0';
    document.body.appendChild(ta); ta.select();
    const ok = document.execCommand('copy');
    ta.remove();
    if (ok) return true;
  } catch { /* nothing left */ }
  return false;
}
/** Like writeClipboard, but throws when copying failed (for callers that show an error). */
async function writeClipboardOrThrow(text) { if (!await writeClipboard(text)) throw new Error('Copy failed'); }
window.writeClipboard = writeClipboard;

// ------------------------------------------------------------------ tooltip (any element with data-tip)
// One tooltip follows the cursor over the element it belongs to. It hides when the pointer leaves that element - and also
// when the element is redrawn or removed under the cursor (no mouseout comes then), on click, scroll, keys, leaving the
// window, and after a moment on touch screens, so it never stays stuck to the cursor.
const tipEl = () => $('#tip');
let tipFor = null, tipCheck = 0, tipTouch = 0;
function hideTip() {
  const tip = tipEl();
  if (tipFor && tipFor.dataset && tipFor.dataset.mark) { const bar = document.getElementById(tipFor.dataset.mark); if (bar) bar.classList.remove('hover'); }
  tipFor = null;
  clearInterval(tipCheck); tipCheck = 0;
  clearTimeout(tipTouch); tipTouch = 0;
  if (tip) tip.classList.add('hidden');
}
/** Still over the element it belongs to (and the element is still there, with a tip)? */
function tipStillValid() {
  return !!(tipFor && tipFor.isConnected && tipFor.dataset.tip && tipFor.matches(':hover'));
}
function placeTip(x0, y0) {
  const tip = tipEl();
  if (!tip) return;
  const w = tip.offsetWidth, h = tip.offsetHeight;
  let x = x0 + 14, y = y0 + 14;
  if (x + w > innerWidth - 8) x = x0 - w - 14;
  if (y + h > innerHeight - 8) y = y0 - h - 14;
  tip.style.left = Math.max(4, x) + 'px'; tip.style.top = Math.max(4, y) + 'px';
}
document.addEventListener('mouseover', e => {
  const t = e.target.closest && e.target.closest('[data-tip]');
  if (!t || !t.dataset.tip) { if (tipFor) hideTip(); return; }
  if (t !== tipFor) hideTip();
  const tip = tipEl();
  if (!tip) return;
  tipFor = t;
  tip.textContent = t.dataset.tip;
  tip.classList.remove('hidden');
  placeTip(e.clientX, e.clientY);
  if (t.dataset.mark) { const bar = document.getElementById(t.dataset.mark); if (bar) bar.classList.add('hover'); }
  // The element may be redrawn or removed while the cursor rests on it: check now and then.
  if (!tipCheck) tipCheck = setInterval(() => { if (!tipStillValid()) hideTip(); }, 300);
});
document.addEventListener('mousemove', e => {
  if (!tipFor) return;
  const t = e.target.closest && e.target.closest('[data-tip]');
  if (t !== tipFor || !tipStillValid()) { hideTip(); return; }
  // The tip text may change while it shows (a counter, a button that toggles).
  const tip = tipEl();
  if (tip.textContent !== tipFor.dataset.tip) tip.textContent = tipFor.dataset.tip;
  placeTip(e.clientX, e.clientY);
});
document.addEventListener('mouseout', e => {
  if (!tipFor) return;
  const to = e.relatedTarget;
  // Moving within the same element (between its children) keeps the tip.
  if (to && tipFor.contains(to)) return;
  hideTip();
});
document.addEventListener('pointerdown', e => {
  // A tap shows the tip briefly; a click hides it (the element often changes or opens something).
  if (e.pointerType === 'touch') { clearTimeout(tipTouch); tipTouch = setTimeout(hideTip, 1800); return; }
  hideTip();
}, true);
['wheel', 'keydown', 'blur', 'resize'].forEach(ev => window.addEventListener(ev, () => { if (tipFor) hideTip(); }, true));
document.addEventListener('visibilitychange', hideTip);
document.documentElement.addEventListener('mouseleave', hideTip);

// ------------------------------------------------------------------ charts (single-series bars, one hue, recessive axes, hover tooltips)
/** Registers a chart placeholder; it is drawn after insertion so it can use the real width. */
function chartBox(key, spec) {
  S.charts[key] = spec;
  return `<div class="chart" data-chart="${esc(key)}"></div>`;
}
function niceMax(v) {
  if (v <= 0) return 1;
  const p = Math.pow(10, Math.floor(Math.log10(v)));
  for (const m of [1, 2, 2.5, 5, 10]) if (v <= m * p) return m * p;
  return 10 * p;
}
/** Draws registered charts at their real width. On a first render (animate) the bars grow from the baseline, left to right. */
/** Chart kinds beyond single-series bars (line, stacked, …) register here (insights.js). */
const CHART_TYPES = {};
function drawCharts(root, animate) {
  const grow = animate && !reducedMotion();
  $$('.chart[data-chart]', root).forEach(el => {
    const spec = S.charts[el.dataset.chart];
    if (!spec) return;
    if (spec.type && CHART_TYPES[spec.type]) { CHART_TYPES[spec.type](el, spec, grow); return; }
    const W = Math.max(240, el.clientWidth), H = 170, L = 46, R = 6, T = 10, B = 22;
    const pw = W - L - R, ph = H - T - B;
    const vals = spec.rows.map(spec.y);
    const max = niceMax(Math.max(spec.minMax || 0, ...vals));
    const band = pw / Math.max(1, spec.rows.length);
    const bw = Math.max(2, band - 2);
    let g = '';
    [0, 0.5, 1].forEach(f => {
      const y = T + ph - f * ph;
      g += `<line class="gridline" x1="${L}" x2="${W - R}" y1="${y.toFixed(1)}" y2="${y.toFixed(1)}"></line>` +
        `<text class="axis-label" x="${L - 6}" y="${(y + 3.5).toFixed(1)}" text-anchor="end">${esc(spec.axis(max * f))}</text>`;
    });
    spec.rows.forEach((r, i) => {
      const x = L + i * band + 1;
      const v = vals[i];
      const h = max ? (v / max) * ph : 0;
      const base = T + ph;
      const id = `m-${el.dataset.chart}-${i}`;
      if (i % (spec.tickEvery || 6) === 0) g += `<text class="axis-label" x="${(x + bw / 2).toFixed(1)}" y="${H - 6}" text-anchor="middle">${esc(spec.x(r))}</text>`;
      g += `<rect class="hit" x="${(L + i * band).toFixed(1)}" y="${T}" width="${band.toFixed(1)}" height="${ph}" data-mark="${id}" data-tip="${esc(spec.tip(r))}"></rect>`;
      if (h > 0.5) {
        const rr = Math.min(4, bw / 2, h);
        const y = base - h;
        g += `<path id="${id}" class="mark${grow ? ' grow' : ''}" d="M${x.toFixed(1)},${base} L${x.toFixed(1)},${(y + rr).toFixed(1)} Q${x.toFixed(1)},${y.toFixed(1)} ${(x + rr).toFixed(1)},${y.toFixed(1)} ` +
          `L${(x + bw - rr).toFixed(1)},${y.toFixed(1)} Q${(x + bw).toFixed(1)},${y.toFixed(1)} ${(x + bw).toFixed(1)},${(y + rr).toFixed(1)} L${(x + bw).toFixed(1)},${base} Z"></path>`;
      }
    });
    el.innerHTML = `<svg viewBox="0 0 ${W} ${H}" width="${W}" height="${H}" role="img" aria-label="${esc(spec.label)}">${g}</svg>`;
    if (grow) $$('.mark.grow', el).forEach((m, i) => { m.style.animationDelay = (120 + i * 14) + 'ms'; });
  });
}
let resizeTimer = null;
window.addEventListener('resize', () => { clearTimeout(resizeTimer); resizeTimer = setTimeout(() => drawCharts($('#main')), 150); });

// ------------------------------------------------------------------ event delegation
document.addEventListener('click', async ev => {
  const nav = ev.target.closest('a[data-nav]');
  if (nav && !ev.ctrlKey && !ev.metaKey && !ev.shiftKey && nav.getAttribute('href').startsWith('/')) { ev.preventDefault(); closeModal(); navigate(nav.getAttribute('href')); return; }
  // Ticking options in a tools menu, or a part of a menu marked data-keep-menu (the model picker's tabs and search), keeps
  // it open; any other click closes the open menus.
  const keepMenu = ev.target.closest('.user-menu') || ev.target.closest('[data-keep-menu]') || (ev.target.closest('.tools-menu') && ev.target.closest('label, input'));
  if (!keepMenu) $$('.menu').forEach(m => m.classList.add('hidden'));
  const el = ev.target.closest('[data-act]');
  if (!el) return;
  const fn = ACTIONS[el.dataset.act];
  const need = ACTION_PERMS[el.dataset.act];
  if (fn && need && !can(need)) { ev.preventDefault(); toast('Your account does not have permission for this. Ask an administrator.', 'bad'); return; }
  if (!fn) return;
  ev.preventDefault();
  // Buttons are disabled while their action runs; a spinner appears if it takes noticeably long.
  let busy = null;
  try {
    if (el.tagName === 'BUTTON') { el.disabled = true; busy = setTimeout(() => el.setAttribute('aria-busy', 'true'), 250); }
    await fn(el, ev);
  }
  catch (e) { if (e.status !== 401) toast(e.message, 'bad'); }
  finally { clearTimeout(busy); if (el.tagName === 'BUTTON') { el.disabled = false; el.removeAttribute('aria-busy'); } }
});
document.addEventListener('submit', async ev => {
  const f = ev.target.closest('form[data-form]');
  if (!f) return;
  ev.preventDefault();
  const fn = FORMS[f.dataset.form];
  if (!fn) return;
  const data = Object.fromEntries(new FormData(f).entries());
  const submitBtns = $$('button[type=submit], .modal-actions .btn.primary', f.closest('.modal-card') || f);
  const busy = setTimeout(() => submitBtns.forEach(b => b.setAttribute('aria-busy', 'true')), 250);
  try { submitBtns.forEach(b => b.disabled = true); await fn(f, data); }
  catch (e) { if (e.status !== 401) { toast(e.message, 'bad'); const errBox = $('[data-errors]', f.closest('.modal-card') || f); if (errBox) errBox.innerHTML = formErrors(e); } }
  finally { clearTimeout(busy); submitBtns.forEach(b => { b.disabled = false; b.removeAttribute('aria-busy'); }); }
});
document.addEventListener('change', ev => {
  const el = ev.target.closest('[data-change]');
  if (el && CHANGES[el.dataset.change]) CHANGES[el.dataset.change](el, ev);
});
// Shortcuts that work on every page (keys.js: each can be changed in Keyboard shortcuts).
document.addEventListener('keydown', e => {
  const typing = keyPlainTyping(e);
  if (!typing && keyIs(e, 'app.palette')) { e.preventDefault(); if (paletteOpen()) closePalette(); else openPalette(); return; }
  if (keyIs(e, 'app.close')) { if (paletteOpen()) { closePalette(); return; } closeModal(); $$('.menu').forEach(m => m.classList.add('hidden')); }
  if (!typing && keyIs(e, 'app.shortcuts')) { e.preventDefault(); if (keysDialogOpen()) closeModal(); else ACTIONS['keys-open'](); return; }
  if (keyIs(e, 'app.search') && !keyTextTarget(document.activeElement)) { e.preventDefault(); $('#global-search').focus(); }
});
function formErrors(e) {
  const errs = e.body && e.body.errors ? e.body.errors : [e.message];
  return `<div class="notice bad"><b>Could not save</b><ul>${errs.map(x => `<li>${esc(x)}</li>`).join('')}</ul></div>`;
}
ACTIONS['submit-form'] = el => { const f = document.getElementById(el.dataset.formId); if (f) f.requestSubmit(); };
ACTIONS['go'] = el => navigate(el.dataset.href);

$('#login-form').addEventListener('submit', async ev => {
  ev.preventDefault();
  const f = new FormData(ev.target);
  $('#login-error').textContent = '';
  try {
    await api('POST', '/api/auth/login', { username: f.get('username'), password: f.get('password') });
    ev.target.reset();
    boot();
  } catch (e) { $('#login-error').textContent = e.message; }
});

// ------------------------------------------------------------------ global chrome actions
ACTIONS['toggle-nav'] = () => {
  if (matchMedia('(max-width: 900px)').matches) { $('#sidebar').classList.toggle('open'); return; }
  const collapsed = document.documentElement.dataset.nav !== 'collapsed';
  if (collapsed) document.documentElement.dataset.nav = 'collapsed'; else delete document.documentElement.dataset.nav;
  try { localStorage.setItem('nav', collapsed ? 'collapsed' : ''); } catch { /* ignore */ }
  // The main column changes width: redraw charts to fit once the sidebar has slid away.
  clearTimeout(S.navResizeTimer); S.navResizeTimer = setTimeout(() => drawCharts($('#main')), 320);
};
ACTIONS['toggle-user-menu'] = () => $('#user-menu').classList.toggle('hidden');
ACTIONS['toggle-theme'] = () => {
  const cur = document.documentElement.dataset.theme || (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light');
  const next = cur === 'dark' ? 'light' : 'dark';
  // Cross-fade the colors instead of flashing to the other theme.
  const html = document.documentElement;
  if (!reducedMotion()) { html.classList.add('theme-transition'); clearTimeout(S.themeTimer); S.themeTimer = setTimeout(() => html.classList.remove('theme-transition'), 400); }
  html.dataset.theme = next;
  try { localStorage.setItem('theme', next); } catch { /* ignore */ }
  $('#user-menu').classList.add('hidden');
  drawCharts($('#main'));
};
ACTIONS['logout'] = async () => { try { await post('/api/auth/logout'); } catch { /* ignore */ } S.csrf = null; showLogin(); };
ACTIONS['emergency-stop'] = async () => {
  const ok = await confirmBox('Emergency stop',
    'This pauses <b>every agent</b>, cancels <b>all running tasks</b> and ends <b>all subagents</b>. Queued work is kept. Type <b>STOP</b> to confirm.',
    { danger: true, confirm: 'Stop everything', input: 'Confirmation' });
  if (typeof ok !== 'string' || ok.trim().toUpperCase() !== 'STOP') { if (ok !== null && ok !== false) toast('Not confirmed.', 'bad'); return; }
  const r = await post('/api/operations/emergency-stop');
  toast(r.message, 'ok'); refreshBadges(); refreshNow();
};
FORMS['global-search'] = async (f, data) => {
  const q = (data.q || '').trim();
  if (!q) return;
  f.reset(); $('#global-search').blur();
  if (!can('agents.view')) return openPalette().then(() => { const i = $('#palette-input'); i.value = q; i.dispatchEvent(new Event('input')); });
  if (q.startsWith('cor_')) return navigate('/correlations/' + enc(q));
  try {
    const agents = await get('/api/agents?includeSubagents=1');
    const hit = agents.find(a => a.id.toLowerCase() === q.toLowerCase()) || agents.find(a => (a.name || '').toLowerCase() === q.toLowerCase());
    if (hit) return navigate('/agents/' + enc(hit.id));
  } catch { /* fall through */ }
  if (q.startsWith('tsk_')) {
    const found = await get('/api/tasks?q=' + enc(q) + '&limit=1');
    if (found.length) return openTask(found[0].agentId, found[0].id);
  }
  navigate('/tasks?q=' + enc(q));
};

// ------------------------------------------------------------------ "Repeat" form section (recurring tasks and global tasks)
const WEEKDAYS = [['mon', 'Mon'], ['tue', 'Tue'], ['wed', 'Wed'], ['thu', 'Thu'], ['fri', 'Fri'], ['sat', 'Sat'], ['sun', 'Sun']];
/** Form fields for how often a task repeats; read them back with readRepeat(form). */
function repeatFields() {
  return `<div class="full repeat-box">
    <div class="form-grid">
      <label>Repeat<select name="repeatMode" data-change="repeat-mode">
        <option value="none">Does not repeat</option><option value="interval">Every few minutes / hours / days</option>
        <option value="daily">Daily</option><option value="weekly">Weekly</option><option value="monthly">Monthly</option><option value="cron">Custom (cron)</option></select>
        <span class="help">Times are the server's local time</span></label>
      <label class="hidden" data-repeat="interval">Every<span class="row"><input name="repeatEvery" type="number" min="1" value="1" class="grow">
        <select name="repeatUnit"><option value="minutes">minutes</option><option value="hours" selected>hours</option><option value="days">days</option><option value="weeks">weeks</option></select></span></label>
      <label class="hidden" data-repeat="daily weekly monthly">At<input name="repeatTime" type="time" value="09:00"></label>
      <label class="hidden" data-repeat="monthly">On day of month<input name="repeatDay" type="number" min="1" max="28" value="1"><span class="help">1-28, so it exists in every month</span></label>
      <label class="hidden" data-repeat="cron">Cron expression<input name="repeatCron" placeholder="*/30 9-17 * * 1-5" class="mono"><span class="help">minute hour day month weekday</span></label>
      <div class="full hidden" data-repeat="weekly"><span class="small strong">On</span><div class="row section">${WEEKDAYS.map(([v, l], i) =>
        `<label class="check"><input type="checkbox" name="repeatDays" value="${v}"${i === 0 ? ' checked' : ''}> ${l}</label>`).join('')}</div></div>
      <label class="hidden" data-repeat="interval daily weekly monthly cron">Ends<select name="repeatEnd" data-change="repeat-end">
        <option value="never">Never</option><option value="on">On a date</option><option value="after">After a number of runs</option></select></label>
      <label class="hidden" data-end="on">End date (local time)<input name="repeatEndAt" type="datetime-local"></label>
      <label class="hidden" data-end="after">Number of runs<input name="repeatMaxRuns" type="number" min="1" value="10"></label>
      <label class="check full hidden" data-repeat="interval daily weekly monthly cron"><input type="checkbox" name="repeatSkip" checked> Skip a run while the previous one is still queued or running</label>
    </div></div>`;
}
CHANGES['repeat-mode'] = el => {
  const box = el.closest('.repeat-box');
  $$('[data-repeat]', box).forEach(x => x.classList.toggle('hidden', !x.dataset.repeat.split(' ').includes(el.value)));
  if (el.value === 'none') $$('[data-end]', box).forEach(x => x.classList.add('hidden')); else CHANGES['repeat-end']($('select[name="repeatEnd"]', box));
};
CHANGES['repeat-end'] = el => { const box = el.closest('.repeat-box'); $$('[data-end]', box).forEach(x => x.classList.toggle('hidden', x.dataset.end !== el.value)); };
/** The repeat spec for the API, or null when the task does not repeat. */
function readRepeat(form) {
  const fd = new FormData(form), mode = fd.get('repeatMode');
  if (!mode || mode === 'none') return null;
  const r = { mode, skipIfRunning: fd.get('repeatSkip') === 'on' };
  if (mode === 'interval') { r.every = Number(fd.get('repeatEvery')); r.unit = fd.get('repeatUnit'); }
  if (['daily', 'weekly', 'monthly'].includes(mode)) r.time = fd.get('repeatTime');
  if (mode === 'weekly') { r.days = fd.getAll('repeatDays'); if (!r.days.length) throw new Error('Choose at least one weekday.'); }
  if (mode === 'monthly') r.day = Number(fd.get('repeatDay'));
  if (mode === 'cron') r.cron = (fd.get('repeatCron') || '').trim();
  if (fd.get('repeatEnd') === 'on') { const v = fd.get('repeatEndAt'); if (!v) throw new Error('Choose the end date.'); r.endUtc = new Date(v).toISOString(); }
  if (fd.get('repeatEnd') === 'after') r.maxRuns = Number(fd.get('repeatMaxRuns'));
  return r;
}
/** "Start" fields shared by task forms: as soon as possible, after a delay, or at a time. */
function startFields(label = 'Start') {
  return `<label>${esc(label)}<select name="startMode" data-change="start-mode"><option value="now">As soon as possible</option><option value="delay">After a delay</option><option value="at">At a specific time</option></select></label>
    <label class="hidden" data-start="delay">Delay (minutes)<input name="delayMinutes" type="number" min="1" value="10"></label>
    <label class="hidden" data-start="at">Start at (local time)<input name="runAt" type="datetime-local"></label>`;
}
function readStart(data, body) {
  if (data.startMode === 'delay') body.delaySeconds = Math.round(Number(data.delayMinutes || 0) * 60);
  if (data.startMode === 'at') { if (!data.runAt) throw new Error('Choose the start time.'); body.runAtUtc = new Date(data.runAt).toISOString(); }
}

// ------------------------------------------------------------------ "Outcome & review" and attachments (agent tasks and global tasks)
/** The agent that normally checks work: the first whose id, name or role says "manager". */
function defaultReviewer(agents, exceptId) {
  const m = agents.find(a => a.id !== exceptId && /manager/i.test(`${a.id} ${a.name} ${a.role}`));
  return m ? m.id : '';
}
/** Form section: deliver the result, who reviews it, how often it may be sent back, the confidential expected outcome, files. */
function deliveryFields(agents, opts = {}) {
  const reviewer = opts.reviewer !== undefined ? opts.reviewer : defaultReviewer(agents, opts.exceptId);
  const options = agents.filter(a => !a.isSubagent).map(a => `<option value="${esc(a.id)}"${a.id === reviewer ? ' selected' : ''}>${esc(a.name || a.id)}${a.role ? ' — ' + esc(a.role) : ''}${a.state === 'Dead' ? ' (dead)' : ''}</option>`).join('');
  return `<h3 class="full section">Outcome &amp; review</h3>
    <div class="full outcome-box"><div class="form-grid">
      <label class="check full"><input type="checkbox" name="deliver" checked> Show me the result in <b>Results</b> — the answer, every file the agents create, images and the conversation; rate it and reward the agents there</label>
      <label>Checked by<select name="reviewerAgentId" data-change="reviewer-mode"><option value="">Nobody (no review)</option>${options}</select>
        <span class="help">This agent checks the work before it counts as done</span></label>
      <label data-review>Revisions allowed<select name="maxRevisions"><option value="0">0 (verdict is final)</option><option value="1" selected>1</option><option value="2">2</option><option value="3">3</option></select>
        <span class="help">How often the reviewer may send it back</span></label>
      <label class="full" data-review>Expected outcome <span class="badge warn">only the reviewer sees this</span>
        <textarea name="reviewCriteria" rows="4" placeholder="What a good result looks like: facts or numbers it must contain, format, quality bar, files to deliver… The agents doing the work never see this; the reviewer uses it to accept or reject."></textarea></label>
      ${can('finance.manage') ? `<label>Bounty (USD)<input name="bountyUsd" type="number" min="0" step="0.01" placeholder="none">
        <span class="help">Paid into the agent's wallet when the work is accepted (finished, and approved if reviewed). A global task's bounty is shared by the agents whose steps succeeded.</span></label>` : ''}
    </div></div>
    <h3 class="full section">Attachments</h3>
    <div class="full" data-attach>
      <label class="dropzone"><input type="file" multiple data-change="attach-files">
        <span class="dz-icon">${icon('download')}</span><span><b>Drop files here or click to choose</b><br><span class="muted small">Word, Excel, PowerPoint, PDF, text, CSV, images (PNG, JPG, ICO…), source code (.cs, .vb, .js, .py…), ZIP. The agents get the files and their text.</span></span></label>
      <div class="attach-list"></div>
    </div>`;
}
/**
 * Form section shared by agent tasks and global tasks: approve tool calls automatically (needs the right to answer the
 * Inbox), and keep working past the deadline. opts.deadline adds the deadline field (agent tasks; global tasks have theirs).
 */
function runFields(opts = {}) {
  const auto = can('approvals') ? `<label class="check full run-opt"><input type="checkbox" name="autoApproveTools"><span><b>Approve tool use automatically</b>
      <span class="help">Tools that normally wait for an approval in the Inbox run right away — for this task and for the work it hands to other agents and subagents. Nobody is asked.</span></span></label>` : '';
  const deadline = opts.deadline ? `<label>Deadline (hours)<input name="deadlineHours" type="number" min="0.1" step="any" placeholder="none">
      <span class="help">Counted from its start; unfinished work is then cancelled, unless it keeps working past it</span></label>` : '';
  return `<h3 class="full section">Run settings</h3>${deadline}${auto}
    <label class="check full run-opt"><input type="checkbox" name="runPastDeadline"><span><b>Keep working past the deadline</b>
      <span class="help">The deadline still counts: when it passes the task is marked late and you are told, but the agents keep working instead of being cancelled.</span></span></label>`;
}
/** Reads the Run settings section into the request body. */
function readRun(form, body, opts = {}) {
  const fd = new FormData(form);
  body.autoApproveTools = fd.get('autoApproveTools') === 'on';
  body.runPastDeadline = fd.get('runPastDeadline') === 'on';
  if (opts.deadline) {
    const hours = Number(fd.get('deadlineHours') || 0);
    if (hours < 0) throw new Error('The deadline cannot be negative.');
    if (hours > 0) body.deadlineMinutes = Math.max(1, Math.round(hours * 60));
    else if (body.runPastDeadline) throw new Error('Set a deadline, or clear "Keep working past the deadline".');
  }
}
/** A task's or global task's run settings, for its details (o: autoApproveTools, deadlineUtc or deadlineText, runPastDeadline, deadlinePassedUtc). */
function runKv(o) {
  const parts = [];
  if (o.autoApproveTools) parts.push(`<dt>Tool approvals</dt><dd>${badge('Automatic', 'accent', false)} <span class="muted small">tools that need approval run without asking</span></dd>`);
  if (o.deadlineUtc || o.deadlineText) {
    const when = o.deadlineText || ts(o.deadlineUtc);
    const rel = o.deadlineUtc ? ` <span class="muted small">(${esc(ago(o.deadlineUtc))})</span>` : '';
    const late = o.deadlinePassedUtc ? ' ' + badge(o.runPastDeadline ? 'Late · still working' : 'Deadline reached', 'warn', false) : '';
    const how = o.runPastDeadline ? 'keeps working past it' : 'unfinished work is cancelled when it passes';
    parts.push(`<dt>Deadline</dt><dd>${esc(when)}${rel}${late} <span class="muted small">${how}</span></dd>`);
  }
  return parts.join('');
}
CHANGES['reviewer-mode'] = el => { const f = el.closest('form'); $$('[data-review]', f).forEach(x => x.classList.toggle('hidden', !el.value)); };
/** Uploads one file to the host (raw body); returns the upload record (id, summary, preview). */
async function uploadFile(file) {
  const res = await fetch('/api/uploads?name=' + enc(file.name), {
    method: 'POST', credentials: 'same-origin', body: file,
    headers: { 'Content-Type': 'application/octet-stream', 'X-CSRF-Token': S.csrf || '' }
  });
  const text = await res.text();
  let data = null; try { data = JSON.parse(text); } catch { /* not JSON */ }
  if (!res.ok) throw new Error(data && data.error ? data.error.message : 'Upload failed (' + res.status + ')');
  return data;
}
function attachmentRow(u) {
  return `<div class="attach-item" data-upload="${esc(u.id)}"><input type="hidden" name="attachments" value="${esc(u.id)}">
    <span class="attach-kind k-${esc(u.kind)}">${esc((u.name.split('.').pop() || '').slice(0, 4).toUpperCase())}</span>
    <span class="grow"><b>${esc(u.name)}</b><span class="cell-sub">${esc(u.summary || '')} · ${esc(fileSize(u.sizeBytes))}</span>
      ${u.preview ? `<details class="small"><summary>Text the agents will see</summary><pre>${esc(u.preview)}</pre></details>` : ''}</span>
    <button type="button" class="btn xs ghost" data-act="remove-attachment" aria-label="Remove">${icon('x')}</button></div>`;
}
function fileSize(b) { return b < 1024 ? b + ' B' : b < 1048576 ? (b / 1024).toFixed(1) + ' KB' : (b / 1048576).toFixed(1) + ' MB'; }
async function addFiles(box, files) {
  const list = $('.attach-list', box);
  for (const file of Array.from(files)) {
    const row = document.createElement('div');
    row.className = 'attach-item pending';
    row.innerHTML = `<span class="spinner"></span><span class="grow">${esc(file.name)} <span class="muted small">uploading ${esc(fileSize(file.size))}…</span></span>`;
    list.appendChild(row);
    box.dataset.pending = String(Number(box.dataset.pending || 0) + 1);
    try { const u = await uploadFile(file); row.outerHTML = attachmentRow(u); }
    catch (e) { row.classList.remove('pending'); row.classList.add('failed'); row.innerHTML = `<span class="grow error-text">${esc(file.name)}: ${esc(e.message)}</span><button type="button" class="btn xs ghost" data-act="remove-attachment">${icon('x')}</button>`; }
    finally { box.dataset.pending = String(Number(box.dataset.pending) - 1); }
  }
}
CHANGES['attach-files'] = el => { const box = el.closest('[data-attach]'); addFiles(box, el.files).then(() => { el.value = ''; }); };
ACTIONS['remove-attachment'] = el => el.closest('.attach-item').remove();
document.addEventListener('dragover', ev => { const dz = ev.target.closest && ev.target.closest('.dropzone'); if (dz) { ev.preventDefault(); dz.classList.add('over'); } });
document.addEventListener('dragleave', ev => { const dz = ev.target.closest && ev.target.closest('.dropzone'); if (dz) dz.classList.remove('over'); });
document.addEventListener('drop', ev => {
  const dz = ev.target.closest && ev.target.closest('.dropzone');
  if (!dz) return;
  ev.preventDefault(); dz.classList.remove('over');
  if (ev.dataTransfer && ev.dataTransfer.files.length) addFiles(dz.closest('[data-attach]'), ev.dataTransfer.files);
});
/** Reads the Outcome & review section into the request body. */
function readDelivery(form, body) {
  const box = $('[data-attach]', form);
  if (box && Number(box.dataset.pending || 0) > 0) throw new Error('Wait until the files have finished uploading.');
  const fd = new FormData(form);
  body.deliver = fd.get('deliver') === 'on';
  body.reviewerAgentId = fd.get('reviewerAgentId') || null;
  body.maxRevisions = Number(fd.get('maxRevisions') || 1);
  const criteria = (fd.get('reviewCriteria') || '').trim();
  if (criteria && !body.reviewerAgentId) throw new Error('The expected outcome is only shown to a reviewer: choose who checks the work, or clear it.');
  body.reviewCriteria = body.reviewerAgentId ? criteria : '';
  body.attachments = fd.getAll('attachments');
  const bounty = Number(fd.get('bountyUsd') || 0);
  if (bounty < 0) throw new Error('A bounty cannot be negative.');
  if (bounty > 0) body.bountyUsd = bounty;
}

// ------------------------------------------------------------------ permissions (UI only: the server enforces every one)
/** Permission each dashboard action needs; anything not listed is viewing. */
const ACTION_PERMS = {
  'agent-control': 'agents.control', 'bulk-agents': 'agents.control', 'cancel-subagent': 'agents.control',
  'add-task': 'tasks.manage', 'edit-task': 'tasks.manage', 'bulk-tasks': 'tasks.manage', 'bulk-queue': 'tasks.manage', 'move-task': 'tasks.manage',
  'task-op': 'tasks.manage', 'retry-task': 'tasks.manage', 'cancel-task': 'tasks.manage',
  'message-agent': 'agents.message', 'cancel-message': 'agents.message', 'delete-memory': 'agents.memory',
  'delete-agent': 'agents.manage', 'edit-grant': 'agents.manage', 'revoke-grant': 'agents.manage',
  'record-profit': 'finance.manage', 'adjust-balance': 'finance.manage', 'daily-limit': 'finance.manage', 'save-price': 'finance.manage', 'aud-price-save': 'finance.manage', 'img-price-save': 'finance.manage', 'mus-price-save': 'finance.manage',
  'delete-model': 'models.manage', 'reset-circuit': 'models.manage', 'ai-check': 'models.manage',
  'resolve-approval': 'approvals',
  'study-agent': 'learning.manage', 'rerun-study': 'learning.manage', 'lesson-status': 'learning.manage', 'lesson-pin': 'learning.manage', 'edit-lesson': 'learning.manage',
  'global': 'operations', 'emergency-stop': 'operations', 'mcp-reconnect': 'operations',
  'user-form': 'admin', 'user-password': 'admin', 'user-toggle': 'admin', 'user-delete': 'admin',
  'schedule-op': 'tasks.manage',
  'new-global-task': 'global.tasks', 'global-op': 'global.tasks', 'global-schedule-op': 'global.tasks', 'global-replan': 'global.tasks',
  'rate-result': 'results', 'archive-result': 'results', 'award-result': 'finance.manage', 'revive-agent': 'agents.control',
  'eval-case': 'agents.manage', 'eval-toggle': 'agents.manage', 'eval-delete': 'agents.manage', 'eval-start': 'agents.manage',
  'mailbox-form': 'admin', 'mailbox-check': 'admin', 'mailbox-delete': 'admin', 'secret-form': 'admin', 'secret-delete': 'admin', 'rollback-proposal': 'admin',
  'goal-add': 'admin', 'goal-op': 'admin', 'goal-delete': 'admin', 'hook-add': 'admin', 'hook-toggle': 'admin', 'hook-delete': 'admin'
};
const FORM_PERMS = { 'global-task': 'global.tasks', 'global-replan': 'global.tasks', 'websearch-test': 'operations', 'submit-task': 'tasks.manage', 'edit-task': 'tasks.manage', 'message-agent': 'agents.message',
  'agent-editor': 'agents.manage', 'grant-model': 'agents.manage', 'agent-grant': 'agents.manage', 'model-editor': 'models.manage',
  'record-profit': 'finance.manage', 'adjust-balance': 'finance.manage', 'daily-limit': 'finance.manage', 'edit-lesson': 'learning.manage', 'study-agent': 'learning.manage',
  'rate-result': 'results', 'award-result': 'finance.manage', 'revive-agent': 'finance.manage', 'eval-case': 'agents.manage', 'mailbox': 'admin', 'secret': 'admin' };
// Pages (and the pages under them) with the permission they need. "View workforce" (agents.view) comes with every
// permission that acts on agents; without it the Workforce and its Monitor dashboards are gone.
const NAV_PERMS = { '/chat': 'models.use', '/artifacts': 'models.use', '/audio': 'chat.voice', '/images': 'images.sd', '/music': 'music.suno','/custom-models': 'models.use', '/my-models': 'models.own', '/my-mcp': 'chat.tools', '/results': 'results', '/finance': 'finance.view', '/config': 'system.view', '/audit': 'system.view', '/mcp': 'system.view', '/sandbox': 'system.view', '/users': 'admin', '/model-control': 'admin', '/secrets': 'admin',
  '/': 'agents.view', '/operations': 'agents.view', '/console': 'agents.view', '/events': 'agents.view', '/agents': 'agents.view', '/subagents': 'agents.view', '/learning': 'agents.view',
  '/global-tasks': 'agents.view', '/tasks': 'agents.view', '/communications': 'agents.view', '/correlations': 'agents.view', '/approvals': 'agents.view' };
/** The permission a page needs: its own entry, else the entry of the page it sits under (/agents/x needs what /agents needs). */
function pagePerm(path) {
  if (NAV_PERMS[path] !== undefined) return NAV_PERMS[path];
  const top = Object.keys(NAV_PERMS).find(k => k !== '/' && path.startsWith(k + '/'));
  return top ? NAV_PERMS[top] : null;
}
/** Where "home" is for this user: the Overview, else the first page they may open. */
function homePath() {
  if (can(NAV_PERMS['/'])) return '/';
  const first = $$('#nav a[data-nav]').find(a => !a.classList.contains('hidden'));
  return first ? first.getAttribute('href') : '/updates';
}
function can(perm) {
  if (!perm) return true;
  const me = S.me;
  if (!me) return false;
  if (me.isAdmin) return true;
  return perm !== 'admin' && (me.permissions || []).includes(perm);
}
/** Hides controls the signed-in user may not use (buttons, forms, editor links). */
function applyPermissions(root) {
  if (!S.me || S.me.isAdmin) return;
  $$('[data-act]', root).forEach(el => { const p = ACTION_PERMS[el.dataset.act]; if (p && !can(p)) el.classList.add('hidden'); });
  $$('form[data-form]', root).forEach(f => { const p = FORM_PERMS[f.dataset.form]; if (p && !can(p)) f.classList.add('hidden'); });
  if (!can('agents.manage')) $$('a[href="/agents/new"], a[href$="/edit"], a[href^="/agents/new?"]', root).forEach(a => a.classList.add('hidden'));
  if (!can('models.manage')) $$('a[href="/models/new"], a[href^="/models/custom/"]', root).forEach(a => a.classList.add('hidden'));
}
function applyNavPermissions() {
  $$('#nav a[data-nav], #tabbar a[data-nav]').forEach(a => { const p = NAV_PERMS[a.getAttribute('href')]; a.classList.toggle('hidden', !!p && !can(p)); });
  $('#top-approvals').classList.toggle('hidden', !can('agents.view'));
  if (!can('agents.view')) $('#global-search').placeholder = 'Jump to a page or run a command…';
  const home = homePath();
  $$('a.top-brand').forEach(a => a.setAttribute('href', home));
  $$('#nav .nav-group').forEach(g => {
    let n = g.nextElementSibling, visible = false;
    while (n && !n.classList.contains('nav-group')) { if (!n.classList.contains('hidden')) visible = true; n = n.nextElementSibling; }
    g.classList.toggle('hidden', !visible);
  });
  $('.top-actions [data-act="emergency-stop"]').classList.toggle('hidden', !can('operations'));
  $('.top-actions [data-act="new-global-task"]').classList.toggle('hidden', !can('global.tasks'));
}

// ------------------------------------------------------------------ command palette (Ctrl+K)
const PAL = { shown: [], index: 0, agents: null, agentsAt: 0, timer: null };
function paletteOpen() { const p = $('#palette'); return !p.classList.contains('hidden') && !p.classList.contains('closing'); }
async function openPalette() {
  const p = $('#palette');
  clearTimeout(PAL.timer);
  p.classList.remove('closing', 'hidden');
  $$('.menu').forEach(m => m.classList.add('hidden'));
  const input = $('#palette-input');
  input.value = ''; PAL.index = 0;
  renderPalette();
  input.focus();
  if (can('agents.view') && (!PAL.agents || Date.now() - PAL.agentsAt > 30000)) {
    try { PAL.agents = await get('/api/agents'); PAL.agentsAt = Date.now(); if (paletteOpen()) renderPalette(); } catch { /* pages and commands still work */ }
  }
}
function closePalette() {
  const p = $('#palette');
  if (p.classList.contains('hidden') || p.classList.contains('closing')) return;
  const finish = () => { p.classList.add('hidden'); p.classList.remove('closing'); };
  if (reducedMotion()) { finish(); return; }
  p.classList.add('closing');
  PAL.timer = setTimeout(finish, 140);
}
/** Everything the palette can do: commands, pages (from the navigation) and agents (with per-agent shortcuts when searching). */
function paletteItems() {
  const cmds = [
    { group: 'Commands', label: 'New global task', hint: 'the system plans it and distributes it across agents', icon: 'layers', perm: 'global.tasks', run: () => ACTIONS['new-global-task']({ dataset: {} }) },
    { group: 'Commands', label: 'New agent', icon: 'plus', perm: 'agents.manage', run: () => navigate('/agents/new') },
    { group: 'Commands', label: 'Study an agent', hint: 'organizational learning', icon: 'bolt', perm: 'learning.manage', run: () => ACTIONS['study-agent']({ dataset: {} }) },
    { group: 'Commands', label: 'Change my password', icon: 'agent', run: () => ACTIONS['change-password']() },
    { group: 'Commands', label: 'New user', icon: 'plus', perm: 'admin', run: () => ACTIONS['user-form']({ dataset: {} }) },
    { group: 'Commands', label: 'Toggle light / dark theme', icon: 'moon', run: () => ACTIONS['toggle-theme']() },
    { group: 'Commands', label: 'Keyboard shortcuts', hint: keyLabel('app.shortcuts') || 'change any shortcut', icon: 'page', keywords: 'keys hotkeys keybindings rebind', run: () => ACTIONS['keys-open']() },
    { group: 'Commands', label: 'Pause all agents', icon: 'pause', perm: 'operations', run: () => ACTIONS['global']({ dataset: { op: 'pause-all' } }) },
    { group: 'Commands', label: 'Resume all agents', icon: 'play', perm: 'operations', run: () => ACTIONS['global']({ dataset: { op: 'resume-all' } }) },
    { group: 'Commands', label: 'Emergency stop', icon: 'stop', danger: true, perm: 'operations', run: () => ACTIONS['emergency-stop']() },
    { group: 'Commands', label: 'Sign out', icon: 'out', run: () => ACTIONS['logout']() }
  ].filter(c => can(c.perm));
  const pages = $$('#nav a[data-nav]:not(.hidden)').map(a => ({ group: 'Pages', label: a.textContent.trim(), hint: a.getAttribute('href'), icon: 'page', run: () => navigate(a.getAttribute('href')) }));
  const agents = (PAL.agents || []).flatMap(a => {
    const kw = `${a.id} ${a.role || ''}`, go = tab => () => navigate('/agents/' + enc(a.id) + (tab ? '/' + tab : ''));
    return [
      { group: 'Agents', label: a.name, hint: `${a.id} · ${a.state}`, icon: 'agent', keywords: kw, run: go('') },
      { group: 'Agents', label: `${a.name}: task queue`, icon: 'layers', keywords: kw, deep: true, run: go('queue') },
      { group: 'Agents', label: `${a.name}: add task`, icon: 'plus', keywords: kw, deep: true, perm: 'tasks.manage', run: () => ACTIONS['add-task']({ dataset: { id: a.id } }) },
      { group: 'Agents', label: `${a.name}: budget`, icon: 'arrow', keywords: kw, deep: true, run: go('budget') }
    ].filter(c => can(c.perm));
  });
  return [...cmds, ...pages, ...agents];
}
function paletteScore(q, it) {
  const label = it.label.toLowerCase(), text = label + ' ' + (it.keywords || '').toLowerCase();
  if (label.startsWith(q)) return 400 - label.length;
  const i = text.indexOf(q);
  if (i >= 0) return 300 - i;
  let at = 0, score = 0;
  for (const ch of q) { if (ch === ' ') continue; const j = text.indexOf(ch, at); if (j < 0) return 0; score += j === at ? 3 : 1; at = j + 1; }
  return score;
}
function paletteMark(label, q) {
  const i = q ? label.toLowerCase().indexOf(q) : -1;
  return i < 0 ? esc(label) : esc(label.slice(0, i)) + '<mark>' + esc(label.slice(i, i + q.length)) + '</mark>' + esc(label.slice(i + q.length));
}
function renderPalette() {
  const raw = $('#palette-input').value.trim(), q = raw.toLowerCase();
  let items = paletteItems().filter(it => q || !it.deep);
  if (q) {
    items = items.map(it => ({ it, s: paletteScore(q, it) })).filter(x => x.s > 0).sort((a, b) => b.s - a.s).slice(0, 40).map(x => x.it);
    if (/^(tsk|cor)_/i.test(raw)) items.unshift({ group: 'Open', label: `Open ${raw}`, icon: 'arrow', run: () => { $('#global-search').value = raw; $('form[data-form="global-search"]').requestSubmit(); } });
    if (raw.length > 1) items.push({ group: 'Search', label: `Search tasks for "${raw}"`, icon: 'search', run: () => navigate('/tasks?q=' + enc(raw)) });
  }
  PAL.shown = items;
  if (PAL.index >= items.length) PAL.index = 0;
  let html = '', group = null;
  items.forEach((it, i) => {
    if (!q && it.group !== group) { group = it.group; html += `<div class="palette-group">${esc(group)}</div>`; }
    const hint = it.hint || (q ? it.group : '');
    html += `<div class="palette-item${i === PAL.index ? ' active' : ''}${it.danger ? ' danger' : ''}" role="option" data-pal="${i}" aria-selected="${i === PAL.index}">` +
      `${icon(it.icon || 'arrow')}<span class="p-label">${paletteMark(it.label, q)}</span>${hint ? `<span class="p-hint">${esc(hint)}</span>` : ''}</div>`;
  });
  $('#palette-list').innerHTML = html || '<div class="palette-empty">No matches</div>';
}
function paletteSelect(i, scroll) {
  PAL.index = i;
  $$('#palette-list .palette-item').forEach(el => {
    const on = Number(el.dataset.pal) === i;
    el.classList.toggle('active', on); el.setAttribute('aria-selected', on);
    if (on && scroll) el.scrollIntoView({ block: 'nearest' });
  });
}
function runPaletteItem(i) {
  const it = PAL.shown[i];
  if (!it) return;
  closePalette();
  Promise.resolve().then(() => it.run()).catch(e => { if (e.status !== 401) toast(e.message, 'bad'); });
}
$('#palette-input').addEventListener('input', () => { PAL.index = 0; renderPalette(); });
$('#palette-input').addEventListener('keydown', e => {
  const n = PAL.shown.length, down = keyIs(e, 'list.next');
  if ((down || keyIs(e, 'list.prev')) && n) { e.preventDefault(); paletteSelect((PAL.index + (down ? 1 : -1) + n) % n, true); }
  else if (keyIs(e, 'list.pick')) { e.preventDefault(); runPaletteItem(PAL.index); }
});
$('#palette-list').addEventListener('mousemove', e => { const it = e.target.closest('.palette-item'); if (it && Number(it.dataset.pal) !== PAL.index) paletteSelect(Number(it.dataset.pal), false); });
$('#palette-list').addEventListener('click', e => { const it = e.target.closest('.palette-item'); if (it) runPaletteItem(Number(it.dataset.pal)); });
$('#palette').addEventListener('mousedown', e => { if (e.target.id === 'palette') closePalette(); });
ACTIONS['open-palette'] = () => { $('#user-menu').classList.add('hidden'); openPalette(); };

// ------------------------------------------------------------------ CSV export of a card's table (what is shown)
function csvCell(v) {
  if (/^[=+@]/.test(v) || /^-[^\d$.]/.test(v)) v = "'" + v; // never let a spreadsheet treat a value as a formula
  return /[",\r\n]/.test(v) ? `"${v.replace(/"/g, '""')}"` : v;
}
ACTIONS['export-csv'] = el => {
  const t = el.closest('.card') && el.closest('.card').querySelector('table');
  if (!t) { toast('Nothing to export.', 'bad'); return; }
  const heads = $$('thead th', t);
  const cols = heads.map((th, i) => (th.classList.contains('chk') || !th.textContent.trim()) ? -1 : i).filter(i => i >= 0);
  const rows = [cols.map(i => heads[i].textContent.trim())];
  $$('tbody tr', t).forEach(tr => rows.push(cols.map(i => tr.children[i] ? tr.children[i].innerText.replace(/\s+/g, ' ').trim() : '')));
  const csv = rows.map(r => r.map(csvCell).join(',')).join('\r\n');
  const a = document.createElement('a');
  a.href = URL.createObjectURL(new Blob(['﻿' + csv], { type: 'text/csv;charset=utf-8' }));
  a.download = `${el.dataset.name || 'export'}-${new Date().toISOString().slice(0, 10)}.csv`;
  document.body.appendChild(a); a.click();
  setTimeout(() => { URL.revokeObjectURL(a.href); a.remove(); }, 1000);
  toast(`Exported ${plural(rows.length - 1, 'row')}`, 'ok');
};


// ------------------------------------------------------------------ sounds (user menu → Sounds; only while this window is in the background)
const SOUNDS = {
  chatComplete: { url: 'https://dl.smurflabs.xyz/chat-complete.mp3', label: 'Chat answer finished' },
  inputRequired: { url: 'https://dl.smurflabs.xyz/user-input-required.mp3', label: 'Chat needs your input' },
  agentNeedsYou: { url: 'https://dl.smurflabs.xyz/agent-requires-user.mp3', label: 'An agent needs you' }
};
const SOUND_AUDIO = {}, SOUND_LAST = {};
function soundEnabled(kind) { try { return localStorage.getItem('sound.' + kind) !== 'off'; } catch { return true; } }
/** Volume of every sound, 0-100 (user menu → Sounds → Volume). */
function soundVolume() {
  try { const v = localStorage.getItem('sound.volume'); return v === null ? 100 : Math.max(0, Math.min(100, Number(v) || 0)); } catch { return 100; }
}
/** True when the user is looking at another window or tab (sounds are for that case only). */
function windowInBackground() { return document.hidden || !document.hasFocus(); }
/** Plays a notification sound if it is switched on and this window is in the background (force: preview from the menu). */
function playSound(kind, force) {
  if (!force) notifyUser(kind);
  const s = SOUNDS[kind];
  if (!s || (!force && (!soundEnabled(kind) || !windowInBackground()))) return;
  const now = Date.now();
  if (!force && now - (SOUND_LAST[kind] || 0) < 2500) return;   // a burst of events makes one sound
  SOUND_LAST[kind] = now;
  try {
    const volume = soundVolume();
    if (volume === 0) return;
    const a = SOUND_AUDIO[kind] || (SOUND_AUDIO[kind] = new Audio(s.url));
    a.volume = volume / 100;
    a.currentTime = 0;
    const p = a.play();
    if (p && p.catch) p.catch(() => { /* the browser blocks sound until the page has been clicked once */ });
  } catch { /* no audio */ }
}
/**
 * The volume applies to everything the page plays: each audio or video element (the Audio tab's speech and clips, Music)
 * takes it when it loads, and every one on the page when the volume changes; speech played through Web Audio (read aloud,
 * Dictation mode) listens for 'sound-volume' (voice.js).
 */
function applySoundVolume(el) { try { el.volume = soundVolume() / 100; } catch { /* not media */ } }
document.addEventListener('loadstart', ev => { if (ev.target instanceof HTMLMediaElement) applySoundVolume(ev.target); }, true);
function soundVolumeChanged() {
  $$('audio, video').forEach(applySoundVolume);
  document.dispatchEvent(new CustomEvent('sound-volume', { detail: soundVolume() }));
}
function renderSoundVolume() {
  const r = $('#sound-volume'), v = soundVolume();
  if (r && Number(r.value) !== v) r.value = v;
  const out = $('.sound-vol-val'); if (out) out.textContent = v + '%';
}
function renderSoundMenu() {
  renderSoundVolume();
  $$('[data-act="toggle-sound"]').forEach(b => {
    const on = soundEnabled(b.dataset.sound);
    b.setAttribute('aria-checked', on ? 'true' : 'false');
    const st = $('.sound-state', b); if (st) st.textContent = on ? 'On' : 'Off';
    b.classList.toggle('off', !on);
  });
}
ACTIONS['toggle-sound'] = el => {
  const kind = el.dataset.sound, on = !soundEnabled(kind);
  try { if (on) localStorage.removeItem('sound.' + kind); else localStorage.setItem('sound.' + kind, 'off'); } catch { /* ignore */ }
  renderSoundMenu();
  if (on) playSound(kind, true);   // a preview, so you know what it sounds like
};
document.addEventListener('DOMContentLoaded', () => {
  renderSoundMenu();
  const r = $('#sound-volume');
  if (!r) return;
  r.addEventListener('input', () => { try { localStorage.setItem('sound.volume', String(r.value)); } catch { /* ignore */ } renderSoundVolume(); soundVolumeChanged(); });
  r.addEventListener('change', () => playSound('chatComplete', true));   // hear the new volume
});
window.playSound = playSound;

// ------------------------------------------------------------------ system notifications (user menu → Sounds; /notify)
// At the same moments as the sounds, while the window is in the background. The desktop app and VS Code show their own
// notification (chatClient.notify, on by default there); a browser shows one once the user has allowed it.
const NOTIFY_TEXT = { chatComplete: 'Answer finished', inputRequired: 'Needs your input', agentNeedsYou: 'An agent needs you' };
const NOTIFY_LAST = {};
function notifyClient() {
  try { if (window.chatClient) return window.chatClient; return window.parent && window.parent !== window ? window.parent.chatClient || null : null; } catch { return null; }
}
function notifyEnabled() {
  try { const v = localStorage.getItem('notify.enabled'); if (v !== null) return v === 'on'; } catch { /* ignore */ }
  const cc = notifyClient();
  return !!(cc && cc.notify);
}
async function setNotify(on) {
  const cc = notifyClient();
  if (on && !(cc && cc.notify)) {
    if (typeof Notification === 'undefined') { toast('This browser cannot show notifications.', 'bad'); return; }
    if (Notification.permission !== 'granted') {
      let p = 'denied';
      try { p = await Notification.requestPermission(); } catch { /* an old browser */ }
      if (p !== 'granted') { toast('The browser blocked notifications: allow them for this site in its settings.', 'bad'); on = false; }
    }
  }
  try { localStorage.setItem('notify.enabled', on ? 'on' : 'off'); } catch { /* ignore */ }
  renderNotifyMenu();
  toast(on ? 'System notifications on' : 'System notifications off', 'ok');
}
function notifyUser(kind) {
  if (!NOTIFY_TEXT[kind] || !notifyEnabled() || !windowInBackground()) return;
  const now = Date.now();
  if (now - (NOTIFY_LAST[kind] || 0) < 2500) return;   // a burst of events makes one notification
  NOTIFY_LAST[kind] = now;
  const chat = typeof CHAT !== 'undefined' && CHAT.conv ? CHAT.conv : null;
  const title = kind !== 'agentNeedsYou' && chat && chat.title ? chat.title : 'Agentic';
  const body = NOTIFY_TEXT[kind];
  const cc = notifyClient();
  try { if (cc && cc.notify) { cc.notify({ kind, title, body, chatId: chat ? chat.id : null }); return; } } catch { /* the browser's own, below */ }
  if (typeof Notification === 'undefined' || Notification.permission !== 'granted') return;
  try {
    const n = new Notification(title, { body, tag: 'agentic-' + kind });
    n.onclick = () => { try { window.focus(); } catch { /* ignore */ } n.close(); };
  } catch { /* not allowed in this context */ }
}
function renderNotifyMenu() {
  const b = $('[data-act="toggle-notify"]');
  if (!b) return;
  const on = notifyEnabled();
  b.setAttribute('aria-checked', on ? 'true' : 'false');
  const st = $('.sound-state', b); if (st) st.textContent = on ? 'On' : 'Off';
  b.classList.toggle('off', !on);
}
ACTIONS['toggle-notify'] = () => setNotify(!notifyEnabled());
document.addEventListener('DOMContentLoaded', renderNotifyMenu);
window.notifyUser = notifyUser;

// Animations: auto (follow the OS) -> on -> off.
const MOTION_LABEL = { '': 'Animations: auto (system)', on: 'Animations: on', off: 'Animations: off' };
function applyMotion(m) {
  const html = document.documentElement;
  if (m) html.dataset.motion = m; else delete html.dataset.motion;
  const label = $('#motion-label'); if (label) label.textContent = MOTION_LABEL[m || ''];
}
ACTIONS['toggle-motion'] = () => {
  const cur = document.documentElement.dataset.motion || '';
  const next = cur === '' ? 'on' : cur === 'on' ? 'off' : '';
  $('#user-menu').classList.add('hidden');
  applyMotion(next);
  try { if (next) localStorage.setItem('motion', next); else localStorage.removeItem('motion'); } catch { /* ignore */ }
  toast(MOTION_LABEL[next] + (next === '' && matchMedia('(prefers-reduced-motion: reduce)').matches ? ' — your system currently asks for reduced motion' : ''), 'ok');
};

(function initTheme() {
  try { const t = localStorage.getItem('theme'); if (t) document.documentElement.dataset.theme = t; } catch { /* ignore */ }
  try { if (localStorage.getItem('nav') === 'collapsed') document.documentElement.dataset.nav = 'collapsed'; } catch { /* ignore */ }
  let m = ''; try { m = localStorage.getItem('motion') || ''; } catch { /* ignore */ }
  applyMotion(m);
})();
document.addEventListener('DOMContentLoaded', boot);
