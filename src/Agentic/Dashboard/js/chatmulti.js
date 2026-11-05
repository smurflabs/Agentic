'use strict';
/* Agentic console - Chat multitask mode: up to four conversations side by side, each in its own pane, all live at once.
   Each pane is a frame of the Chat page itself in "pane" mode (core.js: IN_PANE), so every Chat feature works in it
   (streaming, tools, approvals, questions, the desktop app's tools). The panes share this page's chat event stream
   (chat.js: joinParentHub). This page keeps the chat list on the left: a chat chosen there (or dragged onto a pane)
   opens in the current pane. Layout, split sizes and the open chats are remembered in this browser.
   Shortcuts (Keyboard shortcuts can change them): Alt+Shift+M multitask on/off, Alt+1…4 go to a pane. */

const MT_MAX = 4;
const MT = { on: false, panes: [], focus: 0, col: 0.5, row: 0.5, zoom: null, timer: 0, seq: 0 };
const MT_NARROW = matchMedia('(max-width: 900px)');
const MT_ICON = '<svg viewBox="0 0 24 24" class="ic"><path d="M4 5h7v6H4zM13 5h7v6h-7zM4 13h7v6H4zM13 13h7v6h-7z"/></svg>';
const MT_LAYOUT_PATHS = {
  1: 'M4 5h16v14H4z', 2: 'M4 5h16v14H4zM12 5v14', 3: 'M4 5h16v14H4zM11 5v14M11 12h9', 4: 'M4 5h16v14H4zM12 5v14M4 12h16'
};
const MT_SVG = {
  zoom: '<path d="M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5"/>',
  unzoom: '<path d="M9 4v5H4M15 4v5h5M9 20v-5H4M15 20v-5h5"/>',
  solo: '<path d="M14 4h6v6M20 4l-8 8M18 14v6H4V6h6"/>',
  close: '<path d="M6 6l12 12M18 6L6 18"/>'
};
const mtSvg = name => `<svg viewBox="0 0 24 24" class="ic">${MT_SVG[name]}</svg>`;

(function mtLoad() {
  if (IN_PANE) return;
  try {
    const s = JSON.parse(localStorage.getItem('chat.multi') || 'null');
    if (!s) return;
    const ratio = v => typeof v === 'number' && v >= 0.2 && v <= 0.8 ? v : 0.5;
    MT.on = !!s.on;
    MT.panes = (Array.isArray(s.panes) ? s.panes : []).slice(0, MT_MAX).map(id => ({ key: mtNewKey(), id: typeof id === 'string' && id ? id : null }));
    MT.focus = Math.min(Math.max(0, s.focus | 0), Math.max(0, MT.panes.length - 1));
    MT.col = ratio(s.col); MT.row = ratio(s.row);
  } catch { /* start fresh */ }
})();
function mtNewKey() { return 'p' + (++MT.seq) + Math.random().toString(36).slice(2, 7); }
function mtSave() {
  try { localStorage.setItem('chat.multi', JSON.stringify({ on: MT.on, panes: MT.panes.map(p => p.id), focus: MT.focus, col: MT.col, row: MT.row })); } catch { /* ignore */ }
}
/** Multitask shows on wide screens only; phones keep one chat (the setting is kept for a wider window). */
function mtActive() { return !IN_PANE && MT.on && !MT_NARROW.matches; }
function mtHasChat(id) { return !!id && !!$('.mt-app') && MT.panes.some(p => p.id === id); }
/** The chat that was open when multitask starts (or a link to one) gets a pane: a new one while there is room. */
function mtSeed(id) {
  if (!MT.panes.length) { MT.panes.push({ key: mtNewKey(), id: id || null }); MT.focus = 0; }
  else if (id && !MT.panes.some(p => p.id === id)) {
    if (MT.panes.length < MT_MAX) { MT.panes.push({ key: mtNewKey(), id }); MT.focus = MT.panes.length - 1; }
    else MT.panes[MT.focus].id = id;
  } else if (id) MT.focus = MT.panes.findIndex(p => p.id === id);
  MT.zoom = null;
  mtSave();
}

// ------------------------------------------------------------------ the page
function mtMainHtml() {
  const n = MT.panes.length;
  return `<section class="mt-main" id="mt-main">
    <header class="chat-top mt-bar">
      <button class="btn icon ghost" data-act="chat-side" aria-label="Toggle chats" data-tip="Show or hide chats"><svg viewBox="0 0 24 24" class="ic"><path d="M4 5h16v14H4zM9 5v14"/></svg></button>
      <div class="mt-brand">${MT_ICON}<b>Multitask</b><span class="mt-count" id="mt-count">${n} / ${MT_MAX}</span></div>
      <span class="mt-activity" id="mt-activity" aria-live="polite"></span>
      <span class="spacer"></span>
      <div class="mt-layouts" id="mt-layouts" role="group" aria-label="Number of chats">${mtLayoutsHtml()}</div>
      <button class="btn small mt-add" data-act="mt-add" id="mt-add"${n >= MT_MAX ? ' disabled' : ''} data-tip="Open another chat beside these">${icon('plus')} Add chat</button>
      <button class="btn small ghost mt-exit" data-act="mt-exit" data-tip="Back to one chat at a time${esc(keyTip('chat.multitask'))}">Exit multitask</button>
    </header>
    <div class="mt-grid" id="mt-grid" data-n="${n}">
      <div class="mt-split v" data-split="col" role="separator" aria-orientation="vertical" aria-label="Resize columns" data-tip="Drag to resize · double-click to even out"></div>
      <div class="mt-split h" data-split="row" role="separator" aria-orientation="horizontal" aria-label="Resize rows" data-tip="Drag to resize · double-click to even out"></div>
    </div>
  </section>`;
}
function mtLayoutsHtml() {
  const n = MT.panes.length;
  return [1, 2, 3, 4].map(k => `<button class="mt-layout${k === n ? ' on' : ''}" data-act="mt-count" data-n="${k}" aria-pressed="${k === n}"
    aria-label="${k} chat${k === 1 ? '' : 's'}" data-tip="${k} chat${k === 1 ? '' : 's'}"><svg viewBox="0 0 24 24" class="ic"><path d="${MT_LAYOUT_PATHS[k]}"/></svg></button>`).join('');
}
function mtPaneSrc(id) { return id ? '/chat/' + enc(id) : '/chat'; }
function mtPaneEl(p) {
  const el = htmlNode(`<div class="mt-pane" data-key="${p.key}">
    <div class="mt-head">
      <span class="mt-num"></span><span class="mt-dot" aria-hidden="true"></span>
      <span class="mt-title">Loading…</span><span class="mt-model"></span>
      <span class="spacer"></span>
      <button class="btn icon ghost mt-btn" data-act="mt-pane-new" aria-label="New chat in this pane" data-tip="New chat in this pane">${icon('plus')}</button>
      <button class="btn icon ghost mt-btn mt-zoom-btn" data-act="mt-pane-zoom" aria-label="Maximize" data-tip="Maximize this pane">${mtSvg('zoom')}</button>
      <button class="btn icon ghost mt-btn" data-act="mt-pane-solo" aria-label="Open on its own" data-tip="Open on its own (leaves multitask)">${mtSvg('solo')}</button>
      <button class="btn icon ghost mt-btn mt-close" data-act="mt-pane-close" aria-label="Close pane" data-tip="Close this pane">${mtSvg('close')}</button>
    </div>
    <div class="mt-body">
      <iframe class="mt-frame" data-chat-pane src="${esc(mtPaneSrc(p.id))}" title="Chat"></iframe>
      <div class="mt-veil" aria-hidden="true"><div class="mt-skel"><i class="w60"></i><i class="w90"></i><i class="w75"></i><i class="w40 right"></i><i class="w85"></i></div><span class="mt-spinner"></span></div>
      <div class="mt-drop" aria-hidden="true"><span>Open here</span></div>
    </div>
  </div>`);
  const frame = $('.mt-frame', el);
  // The pane shows once its chat has rendered (it says so: mtPaneReady), not at 'load': until then a frame is an empty
  // white page and the app's loading skeleton. If it never says so (an error page), it shows after a while anyway.
  frame.addEventListener('load', () => { mtSyncTheme(frame); mtPoll(); clearTimeout(el._readyTimer); el._readyTimer = setTimeout(() => el.classList.add('ready'), 5000); });
  return el;
}
/** Called by a pane when its chat is on screen. */
function mtPaneReady(frame) {
  const el = frame && frame.closest && frame.closest('.mt-pane'); if (!el) return;
  clearTimeout(el._readyTimer);
  requestAnimationFrame(() => el.classList.add('ready'));
}
function mtGrid() { return $('#mt-grid'); }
function mtPaneNode(p) { const g = mtGrid(); return g && $(`.mt-pane[data-key="${p.key}"]`, g); }
function mtFrameWin(p) { const el = mtPaneNode(p); try { return el ? $('.mt-frame', el).contentWindow : null; } catch { return null; } }

/** Builds the panes after the page renders, and keeps them in step with MT.panes from then on (frames are never reloaded). */
function mtMount() {
  history.replaceState({}, '', '/chat');
  mtLayout();
  const grid = mtGrid();
  if (window.ResizeObserver) new ResizeObserver(mtPlaceSplits).observe(grid);
  $$('.mt-split', grid).forEach(mtSplitDrag);
  new MutationObserver(mtDecorateList).observe($('#chat-list'), { childList: true });
  mtDecorateList();
  clearInterval(MT.timer);
  MT.timer = setInterval(() => { if (!$('.mt-app')) { clearInterval(MT.timer); return; } mtPoll(); }, 600);
  setTimeout(() => mtFocus(MT.focus, true), 400);
}
function mtLayout() {
  const grid = mtGrid(); if (!grid) return;
  // FLIP: where each pane was, so it can glide to its new place instead of jumping.
  const before = new Map($$('.mt-pane', grid).filter(el => el.offsetParent).map(el => [el.dataset.key, el.getBoundingClientRect()]));
  const keep = new Set(MT.panes.map(p => p.key));
  $$('.mt-pane', grid).forEach(el => { if (!keep.has(el.dataset.key)) el.remove(); });
  const first = $('.mt-split', grid);
  let fresh = 0;
  MT.panes.forEach((p, i) => {
    let el = mtPaneNode(p);
    // Only new panes are inserted (at the end, as new panes are always added last): moving a frame in the page reloads
    // it, which would lose its chat's state and stop an answer it is writing.
    if (!el) {
      el = mtPaneEl(p);
      if (!reducedMotion()) { el.classList.add('arrive'); el.style.setProperty('--i', String(fresh++)); setTimeout(() => el.classList.remove('arrive'), 900); }
      grid.insertBefore(el, first);
    }
    $('.mt-num', el).textContent = i + 1;
    $('.mt-frame', el).title = `Chat ${i + 1}`;
    const zoomed = MT.zoom === p.key;
    el.classList.toggle('zoom', zoomed);
    const zb = $('.mt-zoom-btn', el);
    zb.innerHTML = mtSvg(zoomed ? 'unzoom' : 'zoom'); zb.dataset.tip = zoomed ? 'Show all panes' : 'Maximize this pane'; zb.setAttribute('aria-label', zoomed ? 'Show all panes' : 'Maximize');
  });
  if (MT.focus >= MT.panes.length) MT.focus = MT.panes.length - 1;
  grid.dataset.n = MT.panes.length;
  grid.classList.toggle('zoomed', !!MT.zoom && MT.panes.some(p => p.key === MT.zoom));
  $('#mt-count').textContent = `${MT.panes.length} / ${MT_MAX}`;
  $('#mt-layouts').innerHTML = mtLayoutsHtml();
  $('#mt-add').disabled = MT.panes.length >= MT_MAX;
  mtApplyRatios();
  mtMarkFocus();
  mtSave();
  mtGlide(grid, before);
  requestAnimationFrame(mtPlaceSplits);
}
/** Moves panes that changed place or size from where they were to where they are now. */
function mtGlide(grid, before) {
  if (reducedMotion() || !before.size || !grid.animate) return;
  $$('.mt-pane', grid).forEach(el => {
    const a = before.get(el.dataset.key); if (!a || !el.offsetParent) return;
    const b = el.getBoundingClientRect();
    if (Math.abs(a.left - b.left) < 1 && Math.abs(a.top - b.top) < 1 && Math.abs(a.width - b.width) < 1 && Math.abs(a.height - b.height) < 1) return;
    el.animate([{ transformOrigin: 'top left', transform: `translate(${a.left - b.left}px, ${a.top - b.top}px) scale(${a.width / b.width}, ${a.height / b.height})` },
      { transformOrigin: 'top left', transform: 'none' }], { duration: 380, easing: 'cubic-bezier(.16, 1, .3, 1)' });
  });
}
function mtMarkFocus() {
  MT.panes.forEach((p, i) => { const el = mtPaneNode(p); if (el) { el.classList.toggle('focused', i === MT.focus); if (i === MT.focus) el.classList.remove('done'); } });
  mtDecorateList();
}
/** Makes pane i the current one; with moveInput the cursor goes to its message box. */
function mtFocus(i, moveInput) {
  if (i < 0 || i >= MT.panes.length) return;
  const changed = MT.focus !== i;
  MT.focus = i;
  mtMarkFocus();
  if (changed) mtSave();
  if (moveInput) {
    const w = mtFrameWin(MT.panes[i]);
    try { if (w) { w.focus(); if (w.focusComposer) w.focusComposer(); } } catch { /* still loading */ }
  }
}
/** Called by a pane when it is clicked or typed in. */
function mtFocusFrame(frame) {
  const el = frame && frame.closest && frame.closest('.mt-pane'); if (!el) return;
  const i = MT.panes.findIndex(p => p.key === el.dataset.key);
  if (i >= 0 && i !== MT.focus) mtFocus(i, false);
}
function mtFocusedWindow() { return $('.mt-app') && MT.panes[MT.focus] ? mtFrameWin(MT.panes[MT.focus]) : null; }

/** Opens a chat (or a new chat for null) in pane i: in place when the pane is ready, else by loading it. */
function mtOpenIn(i, id) {
  const p = MT.panes[i]; if (!p) return;
  const other = id ? MT.panes.findIndex(x => x.id === id) : -1;
  if (other >= 0 && other !== i) { mtFocus(other, true); mtFlash(MT.panes[other]); return; }
  p.id = id || null;
  const w = mtFrameWin(p);
  let done = false;
  try { if (w && w.navigate && w.CHAT_READY) { w.navigate(mtPaneSrc(id)); done = true; } } catch { /* not ready */ }
  if (!done) { const el = mtPaneNode(p); if (el) { el.classList.remove('ready'); $('.mt-frame', el).src = mtPaneSrc(id); } }
  mtFocus(i, true);
  mtSave();
  mtDecorateList();
}
function mtOpenChat(id) { if (!MT.panes.length) return; mtOpenIn(MT.focus, id); }
function mtFlash(p) { const el = mtPaneNode(p); if (!el || reducedMotion()) return; el.classList.remove('flash'); void el.offsetWidth; el.classList.add('flash'); }

// ------------------------------------------------------------------ what each pane is doing
function mtPoll() {
  if (!$('.mt-app')) return;
  let busy = 0, needs = 0, changed = false;
  MT.panes.forEach((p, i) => {
    const el = mtPaneNode(p); if (!el) return;
    let st = null;
    try { const w = mtFrameWin(p); st = w && w.chatPaneState ? w.chatPaneState() : null; } catch { st = null; }
    if (!st) return;
    if (st.id !== p.id) { p.id = st.id; changed = true; }
    const title = $('.mt-title', el);
    if (title.textContent !== st.title) { title.textContent = st.title; title.dataset.tip = st.title; }
    $('.mt-model', el).textContent = st.model || '';
    const wasBusy = el.classList.contains('busy');
    el.classList.toggle('busy', st.busy);
    el.classList.toggle('needs', st.needs);
    // An answer finished in a pane you are not looking at: its header says so until you go there.
    if (wasBusy && !st.busy && i !== MT.focus) el.classList.add('done');
    if (st.busy) busy++;
    if (st.needs) needs++;
  });
  const parts = [];
  if (busy) parts.push(`<span class="mt-act busy"><i></i>${busy} answering</span>`);
  if (needs) parts.push(`<span class="mt-act needs"><i></i>${needs} need${needs === 1 ? 's' : ''} you</span>`);
  const act = $('#mt-activity');
  const html = parts.join('');
  if (act && act.innerHTML !== html) act.innerHTML = html;
  if (changed) { mtSave(); mtDecorateList(); }
}
/** Chats open in a pane show the pane's number in the chat list; the current pane's chat is highlighted. */
function mtDecorateList() {
  const list = $('#chat-list'); if (!list || !$('.mt-app')) return;
  $$('.chat-item[data-id]', list).forEach(a => {
    const i = MT.panes.findIndex(p => p.id === a.dataset.id);
    a.classList.toggle('active', i >= 0 && i === MT.focus);
    a.classList.toggle('in-mt', i >= 0);
    let tag = $('.mt-tag', a);
    if (i < 0) { if (tag) tag.remove(); return; }
    if (!tag) { tag = document.createElement('span'); tag.className = 'mt-tag'; a.appendChild(tag); }
    tag.textContent = i + 1;
    tag.dataset.tip = `Open in pane ${i + 1}`;
  });
}
function mtSyncTheme(frame) {
  try {
    const src = document.documentElement, dst = frame.contentDocument && frame.contentDocument.documentElement;
    if (!dst) return;
    ['theme', 'motion'].forEach(k => { if (src.dataset[k]) dst.dataset[k] = src.dataset[k]; else delete dst.dataset[k]; });
  } catch { /* still loading */ }
}

// ------------------------------------------------------------------ split sizes
function mtApplyRatios() {
  const g = mtGrid(); if (!g) return;
  g.style.setProperty('--mt-c1', MT.col + 'fr'); g.style.setProperty('--mt-c2', (1 - MT.col) + 'fr');
  g.style.setProperty('--mt-r1', MT.row + 'fr'); g.style.setProperty('--mt-r2', (1 - MT.row) + 'fr');
}
function mtPlaceSplits() {
  const g = mtGrid(); if (!g) return;
  const panes = $$('.mt-pane', g), n = panes.length;
  const v = $('.mt-split.v', g), h = $('.mt-split.h', g);
  const zoomed = g.classList.contains('zoomed');
  v.classList.toggle('hidden', n < 2 || zoomed);
  h.classList.toggle('hidden', n < 3 || zoomed);
  if (n < 2 || zoomed) return;
  // Layout boxes (offsets), not on-screen boxes: panes may be gliding (transformed) right now.
  const a = panes[0], b = panes[1];
  v.style.left = ((a.offsetLeft + a.offsetWidth + b.offsetLeft) / 2) + 'px';
  if (n >= 3) {
    const top = panes[n === 3 ? 1 : 0], below = panes[2];
    h.style.top = ((top.offsetTop + top.offsetHeight + below.offsetTop) / 2) + 'px';
    h.style.left = (n === 3 ? b.offsetLeft : 0) + 'px';
  }
}
function mtSplitDrag(handle) {
  const kind = handle.dataset.split;
  handle.addEventListener('dblclick', () => { MT[kind] = 0.5; mtApplyRatios(); mtPlaceSplits(); mtSave(); });
  handle.addEventListener('pointerdown', ev => {
    if (ev.button !== 0) return;
    ev.preventDefault();
    const g = mtGrid();
    handle.setPointerCapture(ev.pointerId);
    g.classList.add('resizing', kind === 'col' ? 'resizing-col' : 'resizing-row');
    const move = e => {
      const r = g.getBoundingClientRect();
      const ratio = kind === 'col' ? (e.clientX - r.left) / r.width : (e.clientY - r.top) / r.height;
      MT[kind] = Math.min(0.8, Math.max(0.2, ratio));
      mtApplyRatios(); mtPlaceSplits();
    };
    const up = () => {
      handle.removeEventListener('pointermove', move); handle.removeEventListener('pointerup', up); handle.removeEventListener('pointercancel', up);
      g.classList.remove('resizing', 'resizing-col', 'resizing-row'); mtSave();
    };
    handle.addEventListener('pointermove', move); handle.addEventListener('pointerup', up); handle.addEventListener('pointercancel', up);
  });
  handle.addEventListener('keydown', ev => {
    const step = { ArrowLeft: -0.05, ArrowUp: -0.05, ArrowRight: 0.05, ArrowDown: 0.05 }[ev.key];
    if (!step) return;
    ev.preventDefault(); MT[kind] = Math.min(0.8, Math.max(0.2, MT[kind] + step)); mtApplyRatios(); mtPlaceSplits(); mtSave();
  });
  handle.tabIndex = 0;
}

// ------------------------------------------------------------------ actions
function mtRerender(path, kind) {
  history.pushState({}, '', path);
  S.params = { path: path.split('?')[0], args: path.startsWith('/chat/') ? [decodeURIComponent(path.slice(6))] : [], query: new URLSearchParams() };
  // Going in or out of multitask: the page morphs (a view transition where the browser has them), panes rise in one by one.
  const root = document.documentElement;
  if (!kind || reducedMotion() || !document.startViewTransition) { render(false); return; }
  root.classList.add('mt-vt', 'mt-vt-' + kind);
  const vt = document.startViewTransition(async () => { await render(false); $('#main').classList.remove('enter'); });
  vt.finished.finally(() => root.classList.remove('mt-vt', 'mt-vt-' + kind));
}
function mtEnter() {
  if (MT_NARROW.matches) { toast('Multitask needs a wider window.', 'bad'); return; }
  MT.on = true;
  mtSeed(CHAT.conv && CHAT.conv.id);
  mtRerender('/chat', 'in');
}
async function mtExit() {
  const busy = MT.panes.filter((p, i) => i !== MT.focus && mtPaneNode(p)?.classList.contains('busy')).length;
  if (busy && !await confirmBox('Exit multitask', `${busy === 1 ? 'A chat in another pane is' : busy + ' chats in other panes are'} still answering. Leaving multitask closes those panes, which may stop the answer${busy === 1 ? '' : 's'}.`, { confirm: 'Exit multitask' })) return;
  const p = MT.panes[MT.focus];
  MT.on = false; MT.zoom = null; mtSave();
  clearInterval(MT.timer);
  mtRerender(p && p.id ? '/chat/' + enc(p.id) : '/chat', 'out');
}
async function mtClose(i) {
  const p = MT.panes[i]; if (!p) return;
  if (mtPaneNode(p)?.classList.contains('busy') && !await confirmBox('Close pane', 'This chat is still answering. Closing its pane may stop the answer.', { confirm: 'Close pane', danger: true })) return;
  if (MT.panes.length === 1) { MT.panes = [{ key: mtNewKey(), id: null }]; MT.focus = 0; mtLayout(); return; }
  const el = mtPaneNode(p);
  if (el && !reducedMotion()) { el.classList.add('leaving'); await new Promise(r => setTimeout(r, 180)); }
  MT.panes.splice(i, 1);
  if (MT.zoom === p.key) MT.zoom = null;
  if (MT.focus >= i && MT.focus > 0) MT.focus--;
  mtLayout();
  mtFocus(MT.focus, true);
}
function mtAdd(id) {
  if (MT.panes.length >= MT_MAX) { toast(`Multitask shows up to ${MT_MAX} chats.`, 'bad'); return; }
  MT.panes.push({ key: mtNewKey(), id: id || null });
  MT.focus = MT.panes.length - 1; MT.zoom = null;
  mtLayout();
  setTimeout(() => mtFocus(MT.focus, true), 500);
}
const mtPaneIndex = el => { const pane = el.closest('.mt-pane'); return pane ? MT.panes.findIndex(p => p.key === pane.dataset.key) : -1; };
ACTIONS['mt-enter'] = () => mtEnter();
ACTIONS['mt-exit'] = () => mtExit();
ACTIONS['mt-add'] = () => mtAdd(null);
ACTIONS['mt-count'] = async el => {
  const n = Number(el.dataset.n);
  while (MT.panes.length < n) MT.panes.push({ key: mtNewKey(), id: null });
  if (MT.panes.length > n) {
    const extra = MT.panes.slice(n);
    const busy = extra.filter(p => mtPaneNode(p)?.classList.contains('busy')).length;
    if (busy && !await confirmBox('Fewer panes', `${busy === 1 ? 'A chat that would close is' : busy + ' chats that would close are'} still answering. Closing ${busy === 1 ? 'its pane' : 'their panes'} may stop the answer${busy === 1 ? '' : 's'}.`, { confirm: 'Close panes' })) return;
    MT.panes = MT.panes.slice(0, n);
  }
  MT.zoom = null;
  mtLayout();
};
ACTIONS['mt-pane-new'] = el => { const i = mtPaneIndex(el); if (i >= 0) mtOpenIn(i, null); };
ACTIONS['mt-pane-close'] = el => { const i = mtPaneIndex(el); if (i >= 0) mtClose(i); };
ACTIONS['mt-pane-zoom'] = el => {
  const i = mtPaneIndex(el); if (i < 0) return;
  const key = MT.panes[i].key;
  MT.zoom = MT.zoom === key ? null : key;
  mtLayout();
  mtFocus(i, true);
};
ACTIONS['mt-pane-solo'] = el => { const i = mtPaneIndex(el); if (i < 0) return; MT.focus = i; MT.on = false; MT.zoom = null; mtSave(); mtExitTo(MT.panes[i]); };
function mtExitTo(p) { clearInterval(MT.timer); mtRerender(p && p.id ? '/chat/' + enc(p.id) : '/chat', 'out'); }
// Clicking a pane's header makes it the current pane.
document.addEventListener('pointerdown', ev => {
  const head = ev.target instanceof Element && ev.target.closest('.mt-pane');
  if (head) { const i = mtPaneIndex(head); if (i >= 0 && i !== MT.focus) mtFocus(i, !ev.target.closest('button')); }
});

// ------------------------------------------------------------------ drag a chat from the list onto a pane
const MT_DRAG_TYPE = 'application/x-agentic-chat';
document.addEventListener('dragstart', ev => {
  const item = ev.target instanceof Element && ev.target.closest('#chat-list .chat-item[data-id]');
  if (!item || !$('.mt-app')) return;
  ev.dataTransfer.setData(MT_DRAG_TYPE, item.dataset.id);
  ev.dataTransfer.effectAllowed = 'copyMove';
  mtGrid().classList.add('dnd');
});
document.addEventListener('dragend', () => { const g = mtGrid(); if (g) { g.classList.remove('dnd'); $$('.mt-pane.over', g).forEach(x => x.classList.remove('over')); } });
document.addEventListener('dragover', ev => {
  const pane = ev.target instanceof Element && ev.target.closest('.mt-grid.dnd .mt-pane');
  if (!pane || !ev.dataTransfer.types.includes(MT_DRAG_TYPE)) return;
  ev.preventDefault();
  ev.dataTransfer.dropEffect = 'copy';
  $$('.mt-pane.over').forEach(x => { if (x !== pane) x.classList.remove('over'); });
  pane.classList.add('over');
});
document.addEventListener('drop', ev => {
  const pane = ev.target instanceof Element && ev.target.closest('.mt-grid.dnd .mt-pane');
  const id = ev.dataTransfer && ev.dataTransfer.getData(MT_DRAG_TYPE);
  const g = mtGrid(); if (g) { g.classList.remove('dnd'); $$('.mt-pane.over', g).forEach(x => x.classList.remove('over')); }
  if (!pane || !id) return;
  ev.preventDefault();
  mtOpenIn(mtPaneIndex(pane), id);
});

// ------------------------------------------------------------------ shortcuts (here and inside the panes)
function mtKeyOf(ev) {
  if (keyPlainTyping(ev)) return null;
  if (keyIs(ev, 'chat.multitask')) return 'toggle';
  for (let n = 1; n <= 4; n++) if (keyIs(ev, 'chat.pane' + n)) return 'pane:' + n;
  return null;
}
function mtKey_(k) {
  if (k === 'toggle') { if ($('.mt-app')) mtExit(); else if ($('.chat-app')) mtEnter(); return; }
  if (k.startsWith('pane:') && $('.mt-app')) mtFocus(Number(k.slice(5)) - 1, true);
}
window.mtKey = mtKey_;
if (!IN_PANE) {
  document.addEventListener('keydown', ev => {
    const k = mtKeyOf(ev);
    if (!k || !$('.chat-app')) return;
    ev.preventDefault();
    mtKey_(k);
  });
  // Panes follow this page's theme and animation settings.
  new MutationObserver(() => $$('.mt-frame').forEach(mtSyncTheme)).observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme', 'data-motion'] });
  // Too narrow for panes (or wide enough again): show the right layout.
  MT_NARROW.addEventListener('change', () => { if (MT.on && $('.chat-app') && !!$('.mt-app') !== mtActive()) mtRerender('/chat'); });
}

// ------------------------------------------------------------------ inside a pane
/** What the page around this pane shows in its header (null until Chat has loaded). */
function chatPaneState() {
  if (!CHAT.meta || !$('.chat-app')) return null;
  const c = CHAT.conv;
  return {
    id: c ? c.id : null,
    title: c ? c.title || 'Chat' : CHAT.temporary ? 'Temporary chat' : 'New chat',
    model: CHAT.model ? modelName(CHAT.model) : '',
    busy: !!currentStream() || !!(c && CHAT.live.has(c.id)),
    needs: !!(c && ((CHAT.ask && CHAT.ask.convId === c.id) || [...CHAT.approvals.values()].some(a => a.chatId === c.id)))
  };
}
if (IN_PANE) {
  const ping = () => { try { if (window.parent.mtFocusFrame) window.parent.mtFocusFrame(window.frameElement); } catch { /* closed */ } };
  document.addEventListener('pointerdown', ping, true);
  document.addEventListener('focusin', ping, true);
  document.addEventListener('keydown', ev => {
    const k = mtKeyOf(ev); if (!k) return;
    ev.preventDefault();
    try { window.parent.mtKey(k); } catch { /* closed */ }
  }, true);
  document.addEventListener('chat:mounted', () => {
    window.CHAT_READY = true;
    try { if (window.parent.mtPaneReady) window.parent.mtPaneReady(window.frameElement); if (window.parent.mtPoll) window.parent.mtPoll(); } catch { /* closed */ }
  });
}
