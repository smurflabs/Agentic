'use strict';
/* Agentic console - Chat: finding your way around a conversation, and the message box's shortcuts. The web app, the desktop
   app and VS Code (full and lite) all show this chat, so all of them get it.
   - Find in this chat (Ctrl+F): every message, also those not drawn yet and the insides of folded tool cards and thinking.
   - Outline: your messages in order, click one to go there. Pinned messages, with a bar that jumps between them.
   - When each message was sent (hover the actions row) and a divider where the day changes.
   - Expand or collapse every card at once; transcript mode (Ctrl+O) keeps them open.
   - Regenerate the last answer with another model; compare two versions of an answer side by side.
   - ! runs a shell command on this computer (or the code sandbox): its output goes into the chat for the next message.
   - # saves a memory (your account's, or the project's AGENTIC.md). Ctrl+R searches your earlier messages in every chat.
   - A long paste becomes a chip ("[Pasted text #1 +120 lines]") so the message box stays usable.
   - An edit waiting for approval can be accepted in part (each change has a checkbox).
   - Desktop app and VS Code: apply a code block to a file (with a diff first), insert it at the cursor, save it as a new file.
   - /usage, /feedback, /pr-comments and /plugin. */

const NAV = { pastes: new Map(), pasteSeq: 0, find: null, outline: false, transcript: false, pinAt: 0, hunks: new Map(), picks: new Map(), hist: null };
const PIN_ICON = '<svg viewBox="0 0 24 24" class="ic"><path d="M9 4h6l-1 6 3 3H7l3-3zM12 16v5"/></svg>';
const OUTLINE_ICON = '<svg viewBox="0 0 24 24" class="ic"><path d="M8 6h12M8 12h12M8 18h12M4 6h.01M4 12h.01M4 18h.01"/></svg>';
const TRANSCRIPT_ICON = '<svg viewBox="0 0 24 24" class="ic"><path d="M4 4h16v16H4zM8 9l4 3-4 3M13 15h3"/></svg>';
const SHELL_ICON = '<svg viewBox="0 0 24 24" class="ic"><path d="M4 17l6-5-6-5M12 19h8"/></svg>';
const FIND_ICON = '<svg viewBox="0 0 24 24" class="ic"><circle cx="11" cy="11" r="7"/><path d="M20 20l-4-4"/></svg>';

if (typeof registerKeyAction === 'function') {
  registerKeyAction({ id: 'chat.find', group: 'Chat', ctx: 'chat', label: 'Find in this chat', keys: ['Ctrl+F'] });
  registerKeyAction({ id: 'chat.outline', group: 'Chat', ctx: 'chat', label: 'Outline: your messages in this chat', keys: ['Alt+O'] });
  registerKeyAction({ id: 'chat.transcript', group: 'Chat', ctx: 'chat', label: 'Transcript mode: every tool card and thinking open', keys: ['Ctrl+O'] });
  registerKeyAction({ id: 'chat.history', group: 'Message box', ctx: 'composer', label: 'Search your earlier messages (every chat)', keys: ['Ctrl+R'] });
}

function navMsgs() { return (CHAT.conv && CHAT.conv.messages) || []; }
function navIndexOf(id) { return navMsgs().findIndex(m => m.id === id); }
function navNode(m) { return m && m.id ? $(`#chat-thread > .msg[data-id="${CSS.escape(m.id)}"]`) : null; }
/** A message on the page, drawing the chat from it on when it is older than what is drawn (an answer being written stays). */
function navDrawTo(index) {
  const msgs = navMsgs(), m = msgs[index];
  if (!m) return null;
  let node = navNode(m);
  if (node) return node;
  const thread = $('#chat-thread');
  if (!thread) return null;
  const live = CHAT.conv && CHAT.streams && CHAT.streams.get(CHAT.conv.id);
  thread.innerHTML = threadHtml(index);
  if (live && live.el) thread.appendChild(live.el);
  applyRich(thread);
  if (typeof watchEarlierBar === 'function') watchEarlierBar();
  if (typeof markCompacted === 'function') markCompacted();
  return navNode(m);
}
function navGo(node, block) {
  if (!node) return;
  CHAT.stick = false;
  node.scrollIntoView({ block: block || 'center', behavior: reducedMotion() ? 'auto' : 'smooth' });
  node.classList.remove('nav-flash'); void node.offsetWidth; node.classList.add('nav-flash');
  setTimeout(() => node.classList.remove('nav-flash'), 1600);
}
/** One line saying what a message is (outline, pins, find results). */
function navPreview(m, max = 120) {
  if (!m) return '';
  if (m.finishReason === 'shell') { const s = shellParts(m.content); return '$ ' + s.command; }
  const t = String(m.content || '').replace(/```[\s\S]*?(```|$)/g, ' (code) ').replace(/[#*_>`]+/g, '').replace(/\s+/g, ' ').trim();
  if (!t) return m.role === 'user' ? '(files only)' : (m.toolCalls || []).length ? `(${m.toolCalls.length} tool call${m.toolCalls.length === 1 ? '' : 's'})` : '(no text)';
  return t.length > max ? t.slice(0, max - 1) + '…' : t;
}

// ------------------------------------------------------------------ times and day dividers
function navDate(m) { const d = m && m.createdUtc ? new Date(m.createdUtc) : null; return d && !isNaN(d) ? d : null; }
function sameDay(a, b) { return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate(); }
function navDayLabel(d) {
  const now = new Date(), y = new Date(now); y.setDate(now.getDate() - 1);
  if (sameDay(d, now)) return 'Today';
  if (sameDay(d, y)) return 'Yesterday';
  if (now - d < 6 * 864e5) return d.toLocaleDateString([], { weekday: 'long' });
  return d.toLocaleDateString([], { weekday: 'short', month: 'short', day: 'numeric', year: d.getFullYear() !== now.getFullYear() ? 'numeric' : undefined });
}
function shortTime(d) {
  const t = d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }), now = new Date();
  if (sameDay(d, now)) return t;
  const y = new Date(now); y.setDate(now.getDate() - 1);
  if (sameDay(d, y)) return 'Yesterday ' + t;
  return d.toLocaleDateString([], { month: 'short', day: 'numeric', year: d.getFullYear() !== now.getFullYear() ? 'numeric' : undefined }) + ' ' + t;
}
/** When a message was sent, in its actions row (the full date and time on hover). */
function msgTimeHtml(m) {
  const d = navDate(m);
  return d ? `<time class="msg-time muted small" datetime="${esc(d.toISOString())}" data-tip="${esc(d.toLocaleString())}">${esc(shortTime(d))}</time>` : '';
}
/** A divider above the first drawn message of each day ("Today", "Yesterday", "Mon, Nov 2"). Kept right as messages come and go. */
function markDays() {
  const thread = $('#chat-thread');
  if (!thread || !CHAT.conv) return;
  const byId = new Map(navMsgs().map(m => [m.id, m]));
  let prev = null;
  for (const node of [...thread.children]) {
    if (!node.matches('.msg[data-id]')) continue;
    const d = navDate(byId.get(node.dataset.id));
    if (!d) continue;
    const key = d.toDateString(), before = node.previousElementSibling;
    const has = before && before.classList.contains('day-div') ? before : null;
    if (key !== prev) {
      const label = navDayLabel(d);
      if (!has) node.before(htmlNode(`<div class="day-div" data-day="${esc(key)}"><span>${esc(label)}</span></div>`));
      else if (has.dataset.day !== key || has.textContent !== label) { has.dataset.day = key; has.firstElementChild.textContent = label; }
      prev = key;
    } else if (has) has.remove();
  }
  for (const d of $$('#chat-thread > .day-div')) { const n = d.nextElementSibling; if (!n || !n.matches('.msg[data-id]')) d.remove(); }
}
let navObserved = null, navTick = 0;
/** The thread's messages changed (drawn, answered, trimmed): dividers, transcript mode and the outline follow. */
function watchThread() {
  const thread = $('#chat-thread');
  if (!thread || navObserved === thread) return;
  navObserved = thread;
  const added = new Set();
  new MutationObserver(muts => {
    muts.forEach(mu => mu.addedNodes.forEach(n => { if (n.nodeType === 1) added.add(n); }));
    cancelAnimationFrame(navTick);
    navTick = requestAnimationFrame(() => {
      markDays();
      if (NAV.transcript) added.forEach(n => { if (n.isConnected) setCards(n, true); });
      added.clear();
      pruneRegenWith();
      if (NAV.outline) renderOutline();
      renderPinBar();
    });
  }).observe(thread, { childList: true });
}
/** "Regenerate with another model" belongs to the last answer only (an older one keeps it until the chat is drawn again). */
function pruneRegenWith() {
  const msgs = navMsgs(), last = msgs[msgs.length - 1];
  $$('#chat-thread .regen-with').forEach(b => { const id = b.closest('.msg')?.dataset.id; if (!last || id !== last.id || last.role !== 'assistant') b.remove(); });
}

// ------------------------------------------------------------------ pinned messages
function pinnedIds() { return (CHAT.settings && CHAT.settings.pinnedMessages) || []; }
function pinBtnHtml(m) {
  if (!m || !m.id || !CHAT.conv) return '';
  const on = pinnedIds().includes(m.id);
  return `<button class="btn xs ghost icon-only msg-pin${on ? ' on' : ''}" data-act="msg-pin" aria-pressed="${on}" aria-label="${on ? 'Unpin' : 'Pin'}" data-tip="${on ? 'Unpin this message' : 'Pin this message (the bar at the top jumps to it)'}">${PIN_ICON}</button>`;
}
ACTIONS['msg-pin'] = el => {
  const id = el.closest('.msg')?.dataset.id;
  if (!id || !CHAT.conv) return;
  const on = !pinnedIds().includes(id);
  const list = on ? [...pinnedIds(), id] : pinnedIds().filter(x => x !== id);
  CHAT.settings.pinnedMessages = list.length ? list : null;
  saveSettings();
  $$(`#chat-thread .msg[data-id="${CSS.escape(id)}"] [data-act="msg-pin"]`).forEach(b => { b.outerHTML = pinBtnHtml(navMsgs().find(m => m.id === id)); });
  NAV.pinAt = on ? list.length - 1 : 0;
  renderPinBar();
  if (NAV.outline) renderOutline();
  toast(on ? 'Pinned: the bar at the top jumps to it' : 'Unpinned', 'ok');
};
function pinnedMessages() { const msgs = navMsgs(); return pinnedIds().map(id => msgs.find(m => m.id === id)).filter(Boolean); }
function renderPinBar() {
  const bar = $('#pin-bar');
  if (!bar) return;
  const pins = pinnedMessages();
  if (!pins.length) { bar.classList.add('hidden'); bar.innerHTML = ''; return; }
  NAV.pinAt = Math.max(0, Math.min(NAV.pinAt, pins.length - 1));
  const m = pins[NAV.pinAt];
  const html = `<button type="button" class="pin-main" data-act="pin-go" data-tip="Go to the pinned message">${PIN_ICON}<span class="pin-count">${pins.length > 1 ? `${NAV.pinAt + 1} / ${pins.length}` : 'Pinned'}</span><span class="pin-text">${esc(navPreview(m, 160))}</span></button>
    ${pins.length > 1 ? `<button class="btn xs ghost icon-only" data-act="pin-step" data-dir="-1" aria-label="Previous pinned message">‹</button><button class="btn xs ghost icon-only" data-act="pin-step" data-dir="1" aria-label="Next pinned message">›</button>` : ''}
    <button class="btn xs ghost icon-only" data-act="pin-list" aria-label="All pinned messages" data-tip="All pinned messages">${OUTLINE_ICON}</button>`;
  if (bar.dataset.html !== html) { bar.innerHTML = html; bar.dataset.html = html; }
  bar.classList.remove('hidden');
}
ACTIONS['pin-go'] = () => { const m = pinnedMessages()[NAV.pinAt]; if (m) navGo(navDrawTo(navIndexOf(m.id))); };
ACTIONS['pin-step'] = el => { const n = pinnedMessages().length; if (!n) return; NAV.pinAt = (NAV.pinAt + Number(el.dataset.dir) + n) % n; renderPinBar(); ACTIONS['pin-go'](); };
ACTIONS['pin-list'] = () => {
  const pins = pinnedMessages();
  openModal('Pinned messages', pins.length ? `<div class="outline-list">${pins.map(m => `<button class="outline-row" data-act="outline-go" data-id="${esc(m.id)}"><span class="outline-who">${m.role === 'user' ? 'You' : esc(modelName(m.model))}</span><span class="outline-text">${esc(navPreview(m, 200))}</span><span class="muted small">${esc(navDate(m) ? shortTime(navDate(m)) : '')}</span></button>`).join('')}</div>`
    : emptyState('Nothing pinned', 'Pin a message with the pin in its actions row.'), '', 'wide');
};

// ------------------------------------------------------------------ outline
function navButtonsHtml() {
  return `<button class="btn icon ghost nav-btn" data-act="find-open" aria-label="Find in this chat" data-tip="Find in this chat${esc(keyTip('chat.find'))}">${FIND_ICON}</button>
    <button class="btn icon ghost nav-btn${NAV.outline ? ' on' : ''}" data-act="outline-toggle" aria-label="Outline" aria-pressed="${NAV.outline}" data-tip="Outline: your messages in this chat${esc(keyTip('chat.outline'))}">${OUTLINE_ICON}</button>
    <button class="btn icon ghost nav-btn${NAV.transcript ? ' on' : ''}" data-act="transcript-toggle" aria-label="Transcript mode" aria-pressed="${NAV.transcript}" data-tip="${NAV.transcript ? 'Transcript mode is on: cards open. Click to fold them' : 'Transcript mode: open every tool card and thinking'}${esc(keyTip('chat.transcript'))}">${TRANSCRIPT_ICON}</button>`;
}
function renderNavButtons() { const s = $('#nav-slot'); if (s) s.innerHTML = navButtonsHtml(); }
function outlineItems() {
  const msgs = navMsgs(), pins = new Set(pinnedIds());
  return msgs.map((m, i) => ({ m, i })).filter(x => (x.m.role === 'user' && x.m.finishReason !== 'auto_goal' && x.m.finishReason !== 'auto_monitor') || pins.has(x.m.id));
}
function renderOutline() {
  const main = $('.chat-main');
  if (!main) return;
  let box = $('#chat-outline');
  if (!NAV.outline) { box?.remove(); main.classList.remove('with-outline'); return; }
  if (!box) { box = htmlNode('<aside class="chat-outline" id="chat-outline" aria-label="Outline"></aside>'); main.appendChild(box); }
  main.classList.add('with-outline');
  const items = outlineItems(), pins = new Set(pinnedIds());
  const html = `<div class="outline-head"><b>Outline</b><span class="muted small">${num(items.length)} message${items.length === 1 ? '' : 's'}</span><span class="spacer"></span><button class="btn xs ghost icon-only" data-act="outline-toggle" aria-label="Close the outline">×</button></div>
    <div class="outline-list">${items.length ? items.map(({ m }, n) => `<button class="outline-row${m.role !== 'user' ? ' answer' : ''}" data-act="outline-go" data-id="${esc(m.id)}">
      <span class="outline-n">${m.finishReason === 'shell' ? '$' : n + 1}</span><span class="outline-text">${pins.has(m.id) ? PIN_ICON : ''}${esc(navPreview(m, 140))}</span>
      <span class="muted small outline-time">${esc(navDate(m) ? shortTime(navDate(m)) : '')}</span></button>`).join('') : '<div class="muted small outline-empty">Your messages show here.</div>'}</div>`;
  if (box.dataset.html !== html) { box.innerHTML = html; box.dataset.html = html; }
}
ACTIONS['outline-toggle'] = () => { NAV.outline = !NAV.outline; LS.set('chat.outline', NAV.outline ? '1' : ''); renderOutline(); renderNavButtons(); };
ACTIONS['outline-go'] = el => {
  if (el.closest('#modal')) closeModal();
  const i = navIndexOf(el.dataset.id);
  if (i < 0) return;
  navGo(navDrawTo(i), 'start');
  if (matchMedia('(max-width: 900px)').matches && NAV.outline) { NAV.outline = false; renderOutline(); renderNavButtons(); }
};

// ------------------------------------------------------------------ transcript mode and expand / collapse all
function setCards(root, open) {
  $$('details.tool-card, details.thinking, details.tool-group, details.sub-io', root).forEach(d => {
    if (d.open === open) return;
    d.open = open;
    if (open && typeof fillLazy === 'function') fillLazy(d);
  });
}
function setAllCards(open) { const t = $('#chat-thread'); if (t) setCards(t, open); }
ACTIONS['transcript-toggle'] = () => {
  NAV.transcript = !NAV.transcript;
  setAllCards(NAV.transcript);
  $('#chat-thread')?.classList.toggle('transcript', NAV.transcript);
  renderNavButtons();
  toast(NAV.transcript ? 'Transcript mode: every tool card and thinking is open' : 'Cards folded', 'ok');
};

// ------------------------------------------------------------------ find in this chat
/** What can be found in a message: its text, its thinking and each tool call (name, arguments, result). */
function findParts(m) {
  const parts = [{ kind: 'text', text: m.finishReason === 'shell' ? m.content.replace(/<\/?bash-[\w-]+>/g, '') : String(m.content || '') }];
  if (m.reasoning) parts.push({ kind: 'thinking', text: String(m.reasoning) });
  (m.toolCalls || []).forEach(t => parts.push({ kind: 'tool', id: t.id, text: [t.name, t.arguments, t.result].filter(Boolean).join('\n') }));
  return parts;
}
function findHits(q, matchCase) {
  const hits = [], msgs = navMsgs(), needle = matchCase ? q : q.toLowerCase();
  if (!needle) return hits;
  for (let i = 0; i < msgs.length && hits.length < 5000; i++) {
    for (const p of findParts(msgs[i])) {
      const hay = matchCase ? p.text : p.text.toLowerCase();
      let at = hay.indexOf(needle), k = 0;
      while (at >= 0 && hits.length < 5000) { hits.push({ i, kind: p.kind, id: p.id, k: k++ }); at = hay.indexOf(needle, at + needle.length); }
    }
  }
  return hits;
}
function openFind(initial) {
  if (!$('.chat-main') || !CHAT.conv) { toast('Open a chat first.', 'bad'); return; }
  let bar = $('#find-bar');
  if (!bar) return;
  if (bar.classList.contains('hidden')) {
    bar.innerHTML = `${FIND_ICON}<input id="find-q" type="search" placeholder="Find in this chat" autocomplete="off" spellcheck="false" aria-label="Find in this chat">
      <span class="find-count muted small" id="find-count"></span>
      <button class="btn xs ghost icon-only" data-act="find-step" data-dir="-1" aria-label="Previous" data-tip="Previous (Shift+Enter)">↑</button>
      <button class="btn xs ghost icon-only" data-act="find-step" data-dir="1" aria-label="Next" data-tip="Next (Enter)">↓</button>
      <button class="btn xs ghost find-case" data-act="find-case" aria-pressed="false" data-tip="Match case">Aa</button>
      <button class="btn xs ghost icon-only" data-act="find-close" aria-label="Close" data-tip="Close (Esc)">×</button>`;
    bar.classList.remove('hidden');
    NAV.find = { q: '', hits: [], at: -1, matchCase: false };
    const q = $('#find-q');
    q.addEventListener('input', () => { clearTimeout(NAV.findTimer); NAV.findTimer = setTimeout(() => runFind(q.value, true), 120); });
    q.addEventListener('keydown', ev => {
      if (ev.key === 'Enter') { ev.preventDefault(); stepFind(ev.shiftKey ? -1 : 1); }
      else if (ev.key === 'Escape') { ev.preventDefault(); ev.stopPropagation(); closeFind(); }
    });
  }
  const q = $('#find-q');
  const sel = initial !== undefined ? initial : String(window.getSelection ? window.getSelection() : '').trim();
  if (sel && sel.length < 200 && !sel.includes('\n')) { q.value = sel; runFind(sel, true); }
  q.focus(); q.select();
}
function closeFind() {
  const bar = $('#find-bar');
  if (bar) { bar.classList.add('hidden'); bar.innerHTML = ''; }
  NAV.find = null;
  if (window.CSS && CSS.highlights) { CSS.highlights.delete('chat-find'); CSS.highlights.delete('chat-find-now'); }
}
function runFind(q, jump) {
  const f = NAV.find;
  if (!f) return;
  f.q = q;
  f.hits = q.trim() ? findHits(q, f.matchCase) : [];
  // Start at the first hit at or after what is on screen now, going down; else the last one.
  f.at = -1;
  if (jump && f.hits.length) {
    const drawnFrom = CHAT.renderedFrom || 0;
    const firstVisible = $$('#chat-thread > .msg[data-id]').find(n => n.getBoundingClientRect().bottom > ($('#chat-scroll')?.getBoundingClientRect().top || 0));
    const visIdx = firstVisible ? navIndexOf(firstVisible.dataset.id) : drawnFrom;
    const idx = f.hits.findIndex(h => h.i >= visIdx);
    f.at = idx >= 0 ? idx : f.hits.length - 1;
    showHit();
  } else if (window.CSS && CSS.highlights) { CSS.highlights.delete('chat-find'); CSS.highlights.delete('chat-find-now'); }
  findCount();
}
function findCount() {
  const c = $('#find-count'), f = NAV.find;
  if (!c || !f) return;
  c.textContent = !f.q.trim() ? '' : f.hits.length ? `${f.at + 1} of ${num(f.hits.length)}${f.hits.length >= 5000 ? '+' : ''}` : 'No results';
  c.classList.toggle('none', !!f.q.trim() && !f.hits.length);
}
function stepFind(dir) {
  const f = NAV.find;
  if (!f || !f.hits.length) return;
  f.at = (f.at + dir + f.hits.length) % f.hits.length;
  showHit();
  findCount();
}
/** Text ranges of q inside an element (for the highlight). */
function textRanges(root, q, matchCase) {
  const out = [], needle = matchCase ? q : q.toLowerCase();
  if (!root || !needle) return out;
  const walk = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, { acceptNode: n => n.parentElement && n.parentElement.closest('textarea, script, style, .code-src') ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT });
  for (let n = walk.nextNode(); n && out.length < 2000; n = walk.nextNode()) {
    const hay = matchCase ? n.data : n.data.toLowerCase();
    let at = hay.indexOf(needle);
    while (at >= 0) { const r = document.createRange(); r.setStart(n, at); r.setEnd(n, at + needle.length); out.push(r); at = hay.indexOf(needle, at + needle.length); }
  }
  return out;
}
function showHit() {
  const f = NAV.find, h = f && f.hits[f.at];
  if (!h) return;
  const node = navDrawTo(h.i);
  if (!node) return;
  let scope = node;
  if (h.kind === 'tool') {
    const card = $(`.tool-card[data-call="${CSS.escape(String(h.id || ''))}"]`, node);
    if (card) {
      for (let d = card; d && d !== node; d = d.parentElement) if (d.tagName === 'DETAILS' && !d.open) { d.open = true; if (typeof fillLazy === 'function') fillLazy(d); }
      $$('details', card).forEach(d => { if (!d.open && !d.classList.contains('sub-io')) { d.open = true; if (typeof fillLazy === 'function') fillLazy(d); } });
      scope = card;
    }
  } else if (h.kind === 'thinking') {
    $$('details.thinking', node).forEach(d => { d.open = true; });
    scope = $('details.thinking', node) || node;
  } else {
    const more = $('.bubble-more[aria-expanded="false"]', node);
    if (more) more.click();
  }
  // Every match in the scope is marked; the current one stands out and comes into view.
  requestAnimationFrame(() => {
    const ranges = textRanges(scope, f.q, f.matchCase);
    const others = textRanges(node, f.q, f.matchCase);
    const pick = ranges[Math.min(h.k, ranges.length - 1)] || null;
    if (window.CSS && CSS.highlights && typeof Highlight === 'function') {
      CSS.highlights.set('chat-find', new Highlight(...others));
      if (pick) CSS.highlights.set('chat-find-now', new Highlight(pick)); else CSS.highlights.delete('chat-find-now');
    }
    const sc = $('#chat-scroll');
    CHAT.stick = false;
    if (pick && sc) {
      const r = pick.getBoundingClientRect(), box = sc.getBoundingClientRect();
      if (r.height) { sc.scrollTop += r.top - box.top - box.height / 3; return; }
    }
    node.scrollIntoView({ block: 'center' });
  });
}
ACTIONS['find-open'] = () => openFind();
ACTIONS['find-close'] = () => closeFind();
ACTIONS['find-step'] = el => stepFind(Number(el.dataset.dir));
ACTIONS['find-case'] = el => { if (!NAV.find) return; NAV.find.matchCase = !NAV.find.matchCase; el.classList.toggle('on', NAV.find.matchCase); el.setAttribute('aria-pressed', String(NAV.find.matchCase)); runFind($('#find-q').value, true); };

document.addEventListener('keydown', ev => {
  if (!$('.chat-app') || $('.mt-app') || ev.isComposing) return;
  if (keyIs(ev, 'chat.find')) { ev.preventDefault(); openFind(); return; }
  if (keyPlainTyping(ev)) return;
  if (keyIs(ev, 'chat.outline')) { ev.preventDefault(); ACTIONS['outline-toggle'](); }
  else if (keyIs(ev, 'chat.transcript')) { ev.preventDefault(); ACTIONS['transcript-toggle'](); }
});

// ------------------------------------------------------------------ regenerate with another model, compare versions
function navModels() {
  const meta = CHAT.meta || {};
  const list = [...(meta.customModels || []), ...(meta.providerModels || [])].filter(m => m && m.id && !m.hidden && m.enabled !== false);
  const seen = new Set();
  return list.filter(m => !seen.has(m.id) && seen.add(m.id));
}
/** On the last answer: answer the question again with a model picked for this once. */
function regenWithHtml(m) {
  const msgs = navMsgs();
  if (!m || msgs[msgs.length - 1] !== m || m.role !== 'assistant') return '';
  return `<button class="btn xs ghost regen-with" data-act="msg-regen-with" aria-label="Regenerate with another model" data-tip="Regenerate with another model (this answer is kept as a version)"><svg viewBox="0 0 24 24" class="ic"><path d="M6 9l6 6 6-6"/></svg></button>`;
}
ACTIONS['msg-regen-with'] = () => {
  if (typeof busyHere === 'function' && busyHere()) { toast('Wait until the answer is finished.', 'bad'); return; }
  const models = navModels();
  openModal('Regenerate with…', `<input class="input" id="rw-q" placeholder="Search models" autocomplete="off"><div class="rw-list">${models.map(m => `<button class="rw-row${m.id === CHAT.model ? ' on' : ''}" data-act="rw-pick" data-id="${esc(m.id)}" data-name="${esc((modelName(m.id) + ' ' + m.id).toLowerCase())}"><b>${esc(modelName(m.id))}</b><span class="muted small">${esc(m.id)}</span></button>`).join('')}</div>`, '', 'narrow');
  const q = $('#rw-q');
  if (q) {
    q.focus();
    q.addEventListener('input', () => { const v = q.value.toLowerCase().trim(); $$('.rw-row').forEach(r => r.classList.toggle('hidden', !!v && !r.dataset.name.includes(v))); });
    q.addEventListener('keydown', ev => { if (ev.key === 'Enter') { const first = $('.rw-row:not(.hidden)'); if (first) { ev.preventDefault(); first.click(); } } });
  }
};
ACTIONS['rw-pick'] = async el => {
  closeModal();
  const id = el.dataset.id, prev = CHAT.model, prevLast = CHAT.meta.lastModel, prevSaved = LS.get('chat.model');
  CHAT.model = id;
  $('#model-picker').innerHTML = modelButton();
  try { await sendMessage(true); }
  catch (e) { toast(e.message, 'bad'); }
  finally {
    // The pick was for this answer: the chat goes on with the model it had.
    CHAT.model = prev; CHAT.meta.lastModel = prevLast;
    if (prevSaved) LS.set('chat.model', prevSaved);
    const mp = $('#model-picker'); if (mp) mp.innerHTML = modelButton();
  }
};
function compareBtnHtml(anchor) {
  return `<button class="btn xs ghost ver-compare" data-act="ver-compare" data-anchor="${anchor}" data-tip="Compare this version with another, side by side">Compare</button>`;
}
function answerAt(msgs, i) {
  const m = msgs[i];
  if (!m) return { q: null, a: null };
  if (m.role === 'assistant') return { q: null, a: m };
  return { q: m, a: msgs.slice(i + 1).find(x => x.role === 'assistant') || null };
}
function compareSideHtml(label, side) {
  const a = side.a;
  const meta = a ? [modelName(a.model), a.durationMs ? dur(a.durationMs) : '', a.completionTokens ? num(a.completionTokens) + ' tokens' : '', navDate(a) ? shortTime(navDate(a)) : ''].filter(Boolean).join(' · ') : '';
  return `<div class="cmp-side"><div class="cmp-head"><b>${esc(label)}</b><span class="muted small">${esc(meta)}</span></div>
    ${side.q ? `<div class="cmp-q"><span class="muted small">Question</span><div class="md">${chatMarkdown(String(side.q.content || ''))}</div></div>` : ''}
    <div class="md cmp-a">${a ? chatMarkdown(String(a.content || '')) + (a.error ? `<div class="notice bad small">${esc(a.error)}</div>` : '') : '<span class="muted">No answer in this version.</span>'}</div></div>`;
}
ACTIONS['ver-compare'] = async el => {
  const anchor = Number(el.dataset.anchor), p = (CHAT.conv.versions || []).find(v => v.anchorIndex === anchor);
  if (!p) return;
  const others = p.versions.map((v, i) => ({ v, i })).filter(x => x.v.branchId);
  if (!others.length) return;
  const current = answerAt(navMsgs(), anchor);
  const pickHtml = `<select class="input cmp-pick" id="cmp-pick">${others.map(x => `<option value="${esc(x.v.branchId)}">Version ${x.i + 1} of ${p.total}${x.v.preview ? ' · ' + esc(x.v.preview.slice(0, 50)) : ''}</option>`).join('')}</select>`;
  openModal('Compare versions', `<div class="cmp-bar">${pickHtml}<span class="spacer"></span><button class="btn xs" data-act="cmp-use" data-anchor="${anchor}">Use the other version</button></div>
    <div class="cmp-grid">${compareSideHtml(`Version ${p.position} (shown now)`, current)}<div class="cmp-side" id="cmp-other"><div class="muted">Loading…</div></div></div>`, '', 'wide cmp-modal');
  const load = async () => {
    const id = $('#cmp-pick').value, box = $('#cmp-other');
    try {
      const msgs = await get(`/api/chat/conversations/${enc(CHAT.conv.id)}/versions/${enc(id)}`);
      const i = others.find(x => x.v.branchId === id).i;
      box.outerHTML = compareSideHtml(`Version ${i + 1}`, answerAt(msgs, 0)).replace('<div class="cmp-side">', '<div class="cmp-side" id="cmp-other">');
      applyRich($('.cmp-grid'));
    } catch (e) { box.innerHTML = `<div class="notice bad small">${esc(e.message)}</div>`; }
  };
  $('#cmp-pick').addEventListener('change', load);
  applyRich($('.cmp-grid'));
  load();
};
ACTIONS['cmp-use'] = el => {
  const anchor = Number(el.dataset.anchor), p = (CHAT.conv.versions || []).find(v => v.anchorIndex === anchor);
  const id = $('#cmp-pick') && $('#cmp-pick').value;
  const i = p ? p.versions.findIndex(v => v.branchId === id) : -1;
  closeModal();
  if (i < 0) return;
  // The version switch of the conversation (‹ ›), to that version.
  ACTIONS['msg-version']({ closest: () => ({ dataset: { anchor: String(anchor) } }), dataset: { dir: String(i - (p.position - 1)) } });
};

// ------------------------------------------------------------------ ! runs a shell command; # saves a memory
function shellParts(content) {
  const s = String(content || ''), get1 = tag => { const m = new RegExp(`<${tag}>([\\s\\S]*?)</${tag}>`).exec(s); return m ? m[1] : ''; };
  const code = get1('bash-exit-code');
  return { command: get1('bash-input'), output: get1('bash-stdout'), exitCode: code === '' ? 0 : Number(code), where: get1('bash-where') };
}
/** A command the user ran with ! and what it printed (kept in the chat: the model sees it with the next message). */
function shellCardHtml(s, m, live) {
  const out = String(s.output || ''), lines = out ? out.split('\n').length : 0, long = lines > 24;
  return `<div class="msg user shell-msg${live ? ' new' : ''}${s.running ? ' running' : ''}" data-id="${esc((m && m.id) || '')}">
    <div class="shell-card${s.exitCode ? ' failed' : ''}"><div class="shell-head">${SHELL_ICON}<code class="shell-cmd">${esc(s.command)}</code><span class="spacer"></span>
      ${s.running ? '<span class="spinner"></span>' : `<span class="muted small">${s.exitCode ? `exit ${esc(String(s.exitCode))}` : ''}${s.where ? `${s.exitCode ? ' · ' : ''}${esc(s.where)}` : ''}</span>`}</div>
      ${s.running ? '' : `<pre class="shell-out${long ? ' long' : ''}">${esc(out) || '<span class="muted">(no output)</span>'}</pre>${long ? `<button type="button" class="bubble-more shell-more" data-act="shell-more" aria-expanded="false">Show all <span class="muted">· ${num(lines)} lines</span></button>` : ''}`}</div>
    ${m && !s.running ? `<div class="msg-actions user-actions">${msgTimeHtml(m)}<button class="btn xs ghost icon-only" data-act="shell-copy" aria-label="Copy the output" data-tip="Copy the output">${typeof COPY_ICON !== 'undefined' ? COPY_ICON : 'Copy'}</button>${pinBtnHtml(m)}</div>` : ''}</div>`;
}
function shellMsgHtml(m, live) { return shellCardHtml(shellParts(m.content), m, live); }
ACTIONS['shell-more'] = el => { const pre = el.parentElement.querySelector('.shell-out'); const open = pre.classList.toggle('open'); el.setAttribute('aria-expanded', String(open)); el.firstChild.textContent = open ? 'Show less ' : 'Show all '; };
ACTIONS['shell-copy'] = el => { const m = navMsgs().find(x => x.id === el.closest('.msg')?.dataset.id); if (m) copyText(shellParts(m.content).output, 'Output copied'); };
/** Where a ! command runs: this computer (desktop app, VS Code), else the server's code sandbox. */
function shellRunner() {
  const cc = window.chatClient;
  if (cc && cc.runTool && (!cc.shellEnabled || cc.shellEnabled())) {
    return {
      where: cc.machineName || (cc.appName ? 'your computer (' + cc.appName + ')' : 'your computer'),
      async run(command) {
        const r = await cc.runTool({ id: 'shell-' + Date.now().toString(36), name: 'run_command', arguments: JSON.stringify({ command }), approved: true, mode: 'auto' }, CHAT.conv);
        const text = String((r && r.result) ?? '');
        const code = typeof toolExitCode === 'function' ? toolExitCode(text) : null;
        return { output: text.replace(/^\s*exit code:?\s*-?\d+\.?[^\S\n]*(\r?\n|$)/i, '').replace(/\r?\n\(exit code -?\d+\)\s*$/i, ''), exitCode: code !== null ? code : (r && r.ok ? 0 : 1) };
      }
    };
  }
  if (CHAT.meta && CHAT.meta.codeInterpreter) {
    return { where: 'the code sandbox', async run(command) { const r = await post('/api/chat/run', { language: 'bash', code: command }); return { output: String(r.output || ''), exitCode: r.exitCode ?? 0 }; } };
  }
  return null;
}
async function runShellLine(command) {
  if (!command) return;
  if (typeof answering === 'function' && answering()) { toast('Wait until the answer is finished (or stop it), then run the command.', 'bad'); return; }
  const runner = shellRunner();
  if (!runner) { toast('There is nowhere to run commands here: open this chat in the desktop app, VS Code or the CLI, or switch on the code interpreter.', 'bad'); return; }
  const input = $('#chat-input');
  const conv = await ensureConv();
  if (!conv) return;
  if (input) { input.value = ''; autosize(); }
  clearDraft(draftKey());
  composerMode();
  $('.chat-welcome')?.remove();
  const node = htmlNode(shellCardHtml({ command, where: runner.where, running: true }, null, true));
  $('#chat-thread').appendChild(node);
  scrollThread(true);
  let res;
  try { res = await runner.run(command); } catch (e) { res = { output: e.message || String(e), exitCode: 1 }; }
  let saved = null;
  try { saved = await post(`/api/chat/conversations/${enc(conv.id)}/shell`, { command, output: res.output, exitCode: res.exitCode, where: runner.where }); }
  catch (e) { toast('The output could not be kept in the chat: ' + e.message, 'bad'); }
  if (saved && !conv.messages.some(x => x.id === saved.id)) conv.messages.push(saved);
  const fresh = saved && CHAT.conv === conv ? htmlNode(messageHtml(saved, true)) : htmlNode(shellCardHtml({ command, where: runner.where, output: res.output, exitCode: res.exitCode }, null, true));
  if (node.isConnected) node.replaceWith(fresh); else if (CHAT.conv === conv) $('#chat-thread')?.appendChild(fresh);
  scrollThread(true);
  if (typeof renderContext === 'function') renderContext();
}
/** # in the message box: a memory for your account (every chat) or, with a project folder, the project's AGENTIC.md. */
function pickMemoryPlace() {
  return new Promise(resolve => {
    const done = v => { delete ACTIONS['mem-place']; closeModal(); resolve(v); };
    ACTIONS['mem-place'] = el => done(el.dataset.where || null);
    openModal('Save this memory', `<p class="muted small">Where should it go?</p>`,
      `<button class="btn" data-act="mem-place" data-where="">Cancel</button><button class="btn" data-act="mem-place" data-where="project">This project (AGENTIC.md)</button><button class="btn primary" data-act="mem-place" data-where="account">Your memory (every chat)</button>`, 'narrow');
  });
}
async function quickMemory(text) {
  if (!text) return;
  const input = $('#chat-input');
  const md = window.chatClient && window.chatClient.agenticMd;
  const where = md ? await pickMemoryPlace() : 'account';
  if (!where) return;
  if (input) { input.value = ''; autosize(); }
  clearDraft(draftKey());
  composerMode();
  try {
    if (where === 'account') { await cmdRemember(text); return; }
    const f = await md.read();
    if (!f || !f.path) { toast('Open a folder first: AGENTIC.md is kept in the project folder.', 'bad'); return; }
    const body = String(f.content || '').replace(/\s+$/, '');
    await md.write({ content: (body ? body + '\n' : '') + '- ' + text + '\n' });
    toast('Added to ' + f.path, 'ok');
  } catch (e) { toast(e.message || String(e), 'bad'); if (input && !input.value) { input.value = '#' + text; autosize(); } }
}
/** The message box says what Enter will do when the message starts with ! or #. */
function composerMode() {
  const input = $('#chat-input'), comp = $('#composer'), hint = $('#composer-hint');
  if (!input || !comp) return;
  const v = input.value, shell = /^!/.test(v), mem = /^#(?!#)/.test(v);
  comp.classList.toggle('mode-shell', shell);
  comp.classList.toggle('mode-memory', mem);
  if (!hint) return;
  if (shell) {
    const r = shellRunner();
    hint.textContent = r ? `Shell: Enter runs it on ${r.where}; the output goes into the chat for your next message` : 'Shell: there is nowhere to run commands in this window';
    hint.dataset.nav = '1';
  } else if (mem) {
    hint.textContent = window.chatClient && window.chatClient.agenticMd ? 'Memory: Enter saves it to your memory or this project\'s AGENTIC.md' : 'Memory: Enter saves it to your memory (every chat)';
    hint.dataset.nav = '1';
  } else if (hint.dataset.nav) { delete hint.dataset.nav; hint.textContent = routeHint(); }
}
document.addEventListener('input', ev => { if (ev.target && ev.target.id === 'chat-input') { composerMode(); renderPasteChips(); } });

// ------------------------------------------------------------------ a long paste becomes a chip
const PASTE_CHIP_CHARS = 2500, PASTE_CHIP_LINES = 30;
const PASTE_TOKEN = /\[Pasted text #\d+ \+\d+ lines?\]/g;
try { const saved = JSON.parse(sessionStorage.getItem('chat.pastes') || '[]'); saved.forEach(([k, v]) => NAV.pastes.set(k, v)); NAV.pasteSeq = saved.length ? Math.max(...saved.map(([k]) => Number((/#(\d+)/.exec(k) || [0, 0])[1]))) : 0; } catch { /* none kept */ }
function keepPastes() { try { sessionStorage.setItem('chat.pastes', JSON.stringify([...NAV.pastes].slice(-20))); } catch { /* too big or blocked: this visit only */ } }
document.addEventListener('paste', ev => {
  const t = ev.target;
  if (!t || t.id !== 'chat-input' || !ev.clipboardData) return;
  if (ev.clipboardData.files && ev.clipboardData.files.length) return;
  const text = ev.clipboardData.getData('text/plain');
  if (!text) return;
  const lines = text.split('\n').length;
  if (text.length < PASTE_CHIP_CHARS && lines < PASTE_CHIP_LINES) return;
  ev.preventDefault();
  const token = `[Pasted text #${++NAV.pasteSeq} +${lines} line${lines === 1 ? '' : 's'}]`;
  NAV.pastes.set(token, text);
  keepPastes();
  t.setRangeText(token, t.selectionStart, t.selectionEnd, 'end');
  t.dispatchEvent(new Event('input', { bubbles: true }));
}, true);
/** The message with each paste chip's text put back. */
function expandPastes(s) { return !s || !NAV.pastes.size ? s : String(s).replace(PASTE_TOKEN, tok => NAV.pastes.has(tok) ? NAV.pastes.get(tok) : tok); }
function renderPasteChips() {
  const input = $('#chat-input'), files = $('#composer-files');
  if (!input || !files) return;
  let box = $('#paste-chips');
  const toks = [...new Set(input.value.match(PASTE_TOKEN) || [])].filter(t => NAV.pastes.has(t));
  if (!toks.length) { box?.remove(); return; }
  if (!box) { box = htmlNode('<div class="paste-chips" id="paste-chips"></div>'); files.after(box); }
  box.innerHTML = toks.map(t => `<span class="paste-chip"><button type="button" class="paste-view" data-act="paste-view" data-tok="${esc(t)}" data-tip="See the pasted text">📋 ${esc(t.slice(1, -1))}</button><button type="button" class="paste-x" data-act="paste-drop" data-tok="${esc(t)}" aria-label="Remove">×</button></span>`).join('');
}
ACTIONS['paste-view'] = el => { const text = NAV.pastes.get(el.dataset.tok) || ''; openModal(el.dataset.tok.slice(1, -1), `<pre class="job-out paste-full">${esc(text)}</pre>`, `<button class="btn" data-act="modal-close">Close</button>`, 'wide'); };
ACTIONS['paste-drop'] = el => { const input = $('#chat-input'); if (!input) return; input.value = input.value.split(el.dataset.tok).join('').replace(/[ \t]{2,}/g, ' '); input.dispatchEvent(new Event('input', { bubbles: true })); input.focus(); };

// ------------------------------------------------------------------ Ctrl+R: your earlier messages, in every chat
document.addEventListener('keydown', ev => {
  if (!ev.target || ev.target.id !== 'chat-input' || ev.isComposing || !keyIs(ev, 'chat.history')) return;
  ev.preventDefault(); ev.stopPropagation();
  openHistory();
}, true);
function openHistory() {
  const wrap = $('.composer-wrap'), input = $('#chat-input');
  if (!wrap || !input) return;
  if (typeof hidePromptPop === 'function') hidePromptPop();
  let pop = $('#hist-pop');
  if (!pop) {
    pop = htmlNode(`<div class="prompt-pop hist-pop" id="hist-pop" role="dialog" aria-label="Your earlier messages">
      <div class="hist-search">${FIND_ICON}<input id="hist-q" placeholder="Search your earlier messages (every chat)" autocomplete="off" spellcheck="false"><span class="muted small">↑↓ · Enter · Esc</span></div>
      <div class="hist-list" id="hist-list" role="listbox"></div></div>`);
    wrap.prepend(pop);
    const q = $('#hist-q', pop);
    q.addEventListener('input', () => { clearTimeout(NAV.histTimer); NAV.histTimer = setTimeout(() => loadHistory(q.value), 130); });
    q.addEventListener('keydown', ev => {
      const n = (NAV.hist || []).length;
      if (ev.key === 'ArrowDown' || ev.key === 'ArrowUp' || keyIs(ev, 'chat.history')) {
        ev.preventDefault(); ev.stopPropagation();
        if (!n) return;
        NAV.histSel = (NAV.histSel + (ev.key === 'ArrowUp' ? n - 1 : 1)) % n;
        paintHistory();
      } else if (ev.key === 'Enter') { ev.preventDefault(); pickHistory(NAV.histSel); }
      else if (ev.key === 'Escape') { ev.preventDefault(); ev.stopPropagation(); closeHistory(true); }
    });
    q.value = input.value.trim().slice(0, 200);
  }
  $('#hist-q').focus();
  loadHistory($('#hist-q').value);
}
async function loadHistory(q) {
  let list = [];
  try { list = await get('/api/chat/prompt-history?limit=40&q=' + enc(q || '')); } catch (e) { list = []; }
  if (!$('#hist-pop') || $('#hist-q').value !== q) return;
  NAV.hist = list; NAV.histSel = 0;
  paintHistory();
}
function paintHistory() {
  const box = $('#hist-list');
  if (!box) return;
  const list = NAV.hist || [];
  box.innerHTML = list.length ? list.map((p, i) => `<button type="button" class="prompt-opt hist-opt${i === NAV.histSel ? ' on' : ''}" role="option" data-act="hist-pick" data-i="${i}">
      <span class="hist-text">${esc(p.text.replace(/\s+/g, ' ').slice(0, 220))}</span><span class="muted small hist-where">${esc(p.chatTitle || '')} · ${esc(ago(p.createdUtc))}</span></button>`).join('')
    : `<div class="muted small prompt-none">${$('#hist-q') && $('#hist-q').value ? 'Nothing you sent matches.' : 'Nothing sent yet.'}</div>`;
  $('.hist-opt.on', box)?.scrollIntoView({ block: 'nearest' });
}
function pickHistory(i) {
  const p = (NAV.hist || [])[i], input = $('#chat-input');
  if (!p || !input) { closeHistory(true); return; }
  input.value = p.text;
  closeHistory(false);
  autosize(); if (typeof renderContext === 'function') renderContext(); composerMode();
  input.focus(); input.setSelectionRange(input.value.length, input.value.length);
}
function closeHistory(focus) { $('#hist-pop')?.remove(); NAV.hist = null; if (focus) $('#chat-input')?.focus(); }
ACTIONS['hist-pick'] = el => pickHistory(Number(el.dataset.i));
document.addEventListener('mousedown', ev => { if ($('#hist-pop') && !ev.target.closest('#hist-pop')) closeHistory(false); });

// ------------------------------------------------------------------ accept only some changes of an edit
/** Line operations turning a into b (longest common subsequence; very large files fall back to one block of change). */
function lineOps(oldText, newText) {
  const a = String(oldText ?? '').split('\n'), b = String(newText ?? '').split('\n');
  let s = 0; while (s < a.length && s < b.length && a[s] === b[s]) s++;
  let e = 0; while (e < a.length - s && e < b.length - s && a[a.length - 1 - e] === b[b.length - 1 - e]) e++;
  const ops = [];
  for (let i = 0; i < s; i++) ops.push({ t: '=', text: a[i] });
  const A = a.slice(s, a.length - e), B = b.slice(s, b.length - e);
  if (A.length * B.length > 2_500_000) { A.forEach(x => ops.push({ t: '-', text: x })); B.forEach(x => ops.push({ t: '+', text: x })); }
  else {
    const n = A.length, m = B.length, w = m + 1, L = new Uint32Array((n + 1) * (m + 1));
    for (let i = n - 1; i >= 0; i--) for (let j = m - 1; j >= 0; j--) L[i * w + j] = A[i] === B[j] ? L[(i + 1) * w + j + 1] + 1 : Math.max(L[(i + 1) * w + j], L[i * w + j + 1]);
    let i = 0, j = 0;
    while (i < n && j < m) {
      if (A[i] === B[j]) { ops.push({ t: '=', text: A[i] }); i++; j++; }
      else if (L[(i + 1) * w + j] >= L[i * w + j + 1]) ops.push({ t: '-', text: A[i++] });
      else ops.push({ t: '+', text: B[j++] });
    }
    while (i < n) ops.push({ t: '-', text: A[i++] });
    while (j < m) ops.push({ t: '+', text: B[j++] });
  }
  for (let i = a.length - e; i < a.length; i++) ops.push({ t: '=', text: a[i] });
  return ops;
}
/** Changes far enough apart (more than 2 × context unchanged lines) are separate hunks; each op gets its hunk number. */
function hunksOf(ops, ctx = 3) {
  let h = -1, lastChange = -1e9;
  ops.forEach((o, i) => {
    if (o.t === '=') return;
    if (i - lastChange > ctx * 2 + 1 || h < 0) h++;
    o.h = h; lastChange = i;
  });
  return h + 1;
}
/** A diff whose hunks can each be left out (a checkbox per change when there are two or more). */
function hunkDiffHtml(oldText, newText, key) {
  const ops = lineOps(oldText, newText), count = hunksOf(ops);
  NAV.hunks.set(key, { ops, count });
  if (count < 2) return typeof diffHtml === 'function' ? diffHtml(oldText, newText) : '';
  const picks = NAV.picks.get(key) || new Set([...Array(count).keys()]);
  NAV.picks.set(key, picks);
  const row = (cls, sign, l) => `<div class="dl ${cls}"><span class="ds">${sign}</span><span class="dt">${esc(l) || ' '}</span></div>`;
  const ctx = 3, near = new Set();
  ops.forEach((o, i) => { if (o.t !== '=') for (let k = i - ctx; k <= i + ctx; k++) near.add(k); });
  let html = '', open = -1, skipped = false;
  ops.forEach((o, i) => {
    if (o.t !== '=' && o.h !== open) {
      if (open >= 0) html += '</div>';
      open = o.h;
      html += `<div class="hunk${picks.has(o.h) ? '' : ' left-out'}" data-h="${o.h}"><label class="hunk-pick"><input type="checkbox" data-hunk="${o.h}" data-key="${esc(key)}"${picks.has(o.h) ? ' checked' : ''}> Change ${o.h + 1} of ${count}</label>`;
    }
    if (o.t === '=') {
      if (!near.has(i)) { if (!skipped) { if (open >= 0) { html += '</div>'; open = -1; } html += '<div class="dl gap"><span class="ds">⋯</span><span class="dt"></span></div>'; skipped = true; } return; }
      skipped = false;
      const nextChange = ops.slice(i + 1, i + 1 + ctx).find(x => x.t !== '=');
      if (open >= 0 && (!nextChange || nextChange.h !== open)) { html += row('ctx', ' ', o.text); return; }
      html += row('ctx', ' ', o.text);
    } else { skipped = false; html += row(o.t === '-' ? 'del' : 'add', o.t === '-' ? '−' : '+', o.text); }
  });
  if (open >= 0) html += '</div>';
  return `<div class="tc-diff hunk-diff"><div class="hunk-note muted small">${count} changes: untick the ones you do not want, then allow.</div>${html}</div>`;
}
document.addEventListener('change', ev => {
  const box = ev.target;
  if (!box || !box.matches || !box.matches('input[data-hunk]')) return;
  const picks = NAV.picks.get(box.dataset.key) || new Set();
  if (box.checked) picks.add(Number(box.dataset.hunk)); else picks.delete(Number(box.dataset.hunk));
  NAV.picks.set(box.dataset.key, picks);
  box.closest('.hunk')?.classList.toggle('left-out', !box.checked);
});
/** The new text with only the accepted hunks applied (left-out hunks keep the old lines). */
function applyHunks(key, accepted) {
  const h = NAV.hunks.get(key);
  if (!h) return null;
  const out = [];
  for (const o of h.ops) {
    if (o.t === '=') out.push(o.text);
    else if (o.t === '+' && accepted.has(o.h)) out.push(o.text);
    else if (o.t === '-' && !accepted.has(o.h)) out.push(o.text);
  }
  return out.join('\n');
}
/** An approval answered with some changes left out: the arguments to run instead ({none} when every change was left out). */
function editedApprovalArgs(a) {
  const h = NAV.hunks.get(a.id), picks = NAV.picks.get(a.id);
  if (!h || h.count < 2 || !picks || picks.size === h.count) return null;
  if (!picks.size) return { none: true };
  const args = argsOf(a.arguments), merged = applyHunks(a.id, picks);
  if (merged === null) return null;
  if (a.name === 'edit_file' || a.name === 'Edit') { if ('new_str' in args && !('new_string' in args)) args.new_str = merged; else args.new_string = merged; }
  else if (a.name === 'write_file' || a.name === 'Write') args.content = merged;
  else return null;
  return { args };
}
/** The approval card's diff for an edit or an overwrite, with a checkbox per change. */
function approvalEditDiff(a, oldText, newText) { return hunkDiffHtml(oldText, newText, a.id); }

// ------------------------------------------------------------------ apply a code block (desktop app, VS Code)
function codeApplyHtml(id) {
  const cc = window.chatClient;
  if (!cc || !cc.runTool || !cc.fileText) return '';
  return `<span class="cb-apply-wrap"><button class="btn xs" data-act="code-apply-menu" data-block="${id}" aria-haspopup="menu" data-tip="Apply this code to a file, insert it at the cursor, or save it as a new file">Apply ▾</button></span>`;
}
ACTIONS['code-apply-menu'] = el => {
  const wrap = el.closest('.cb-apply-wrap');
  let menu = $('.cb-apply-menu', wrap);
  if (menu) { menu.remove(); return; }
  $$('.cb-apply-menu').forEach(m => m.remove());
  const cc = window.chatClient, id = el.dataset.block;
  menu = htmlNode(`<div class="menu cb-apply-menu" role="menu">
    <button role="menuitem" data-act="code-apply" data-block="${id}">Apply to a file… <span class="muted small">diff first</span></button>
    ${cc.insertCode ? `<button role="menuitem" data-act="code-insert" data-block="${id}">Insert at the cursor</button>` : ''}
    <button role="menuitem" data-act="code-newfile" data-block="${id}">Save as a new file…</button></div>`);
  wrap.appendChild(menu);
};
document.addEventListener('mousedown', ev => { if (!ev.target.closest('.cb-apply-wrap')) $$('.cb-apply-menu').forEach(m => m.remove()); });
function blockCode(id) { const b = document.getElementById(id); return b ? $('.code-src', b).value : ''; }
/** The file a code block is for: a path in its first line's comment, else the last path named just above it. */
function guessCodePath(id) {
  const b = document.getElementById(id);
  if (!b) return '';
  const pathRe = /((?:[A-Za-z]:)?(?:[\w.@-]+[\\/])*[\w@-][\w.@-]*\.[A-Za-z0-9]{1,10})(?![\w/\\])/g;
  const first = (blockCode(id).split('\n')[0] || '').trim();
  const fm = /^(?:\/\/|#|--|\/\*|<!--|;)\s*(?:file(?:name)?:?\s*)?(\S+\.\w{1,10})\b/i.exec(first);
  if (fm) return fm[1];
  let el = b.previousElementSibling, hops = 0;
  while (el && hops++ < 3) {
    const hits = [...(el.textContent || '').matchAll(pathRe)].map(m => m[1]).filter(p => !/^\d+(\.\d+)+$/.test(p) && !/^(e\.g|i\.e)\./i.test(p));
    if (hits.length) return hits[hits.length - 1];
    el = el.previousElementSibling;
  }
  return '';
}
/**
 * Where a snippet goes in a file: the whole file when the block looks like all of it, else the region between the file's
 * lines that match the block's first and last lines. null when that is unclear.
 */
function applySnippet(before, code) {
  const text = String(code).replace(/\n$/, '');
  if (!String(before).trim()) return text + '\n';
  const a = String(before).split('\n'), s = text.split('\n'), keepEol = /\n$/.test(before);
  const filled = s.filter(l => l.trim());
  if (!filled.length) return null;
  if (s.length >= a.filter(l => l.trim()).length * 0.7) return text + (keepEol ? '\n' : '');
  const first = filled[0].trim(), last = filled[filled.length - 1].trim();
  for (let i = 0; i < a.length; i++) {
    if (a[i].trim() !== first) continue;
    if (first === last && filled.length === 1) return [...a.slice(0, i), ...s, ...a.slice(i + 1)].join('\n');
    for (let j = i + 1; j < a.length && j <= i + s.length * 3 + 20; j++) if (a[j].trim() === last) return [...a.slice(0, i), ...s, ...a.slice(j + 1)].join('\n');
  }
  return null;
}
function askPath(title, value, note) {
  return new Promise(resolve => {
    const done = v => { delete ACTIONS['ap-ok']; delete ACTIONS['ap-cancel']; closeModal(); resolve(v); };
    ACTIONS['ap-ok'] = () => done(($('#ap-path') && $('#ap-path').value.trim()) || null);
    ACTIONS['ap-cancel'] = () => done(null);
    openModal(title, `<label>File (in the workspace, or a full path)<input class="input" id="ap-path" value="${esc(value || '')}" spellcheck="false" autocomplete="off"></label>${note ? `<p class="muted small">${esc(note)}</p>` : ''}`,
      `<button class="btn" data-act="ap-cancel">Cancel</button><button class="btn primary" data-act="ap-ok">Continue</button>`, 'narrow');
    const inp = $('#ap-path');
    if (inp) { inp.focus(); inp.select(); inp.addEventListener('keydown', ev => { if (ev.key === 'Enter') { ev.preventDefault(); ACTIONS['ap-ok'](); } }); }
  });
}
async function writeLocal(path, content) {
  const cc = window.chatClient;
  // Read first: the app's tools only overwrite a file read in this chat.
  await cc.runTool({ id: 'apply-r-' + Date.now().toString(36), name: 'read_file', arguments: JSON.stringify({ path }), approved: true, mode: 'auto' }, CHAT.conv).catch(() => null);
  const r = await cc.runTool({ id: 'apply-w-' + Date.now().toString(36), name: 'write_file', arguments: JSON.stringify({ path, content }), approved: true, mode: 'auto' }, CHAT.conv);
  if (!r || r.ok === false) throw new Error((r && r.result) || 'The file could not be written.');
  if (cc.openFile) cc.openFile({ path }).catch(() => { });
}
ACTIONS['code-apply'] = async el => {
  $$('.cb-apply-menu').forEach(m => m.remove());
  const cc = window.chatClient, id = el.dataset.block, code = blockCode(id);
  const path = await askPath('Apply to a file', guessCodePath(id), 'You see the change as a diff before anything is written.');
  if (!path) return;
  let before = null;
  try { const r = await cc.fileText(path); before = r && typeof r.text === 'string' ? r.text : null; } catch { before = null; }
  if (before === null) {
    if (!await confirmBox('New file', `${path} does not exist yet. Create it with this code?`, { confirm: 'Create' })) return;
    try { await writeLocal(path, code.replace(/\n?$/, '\n')); toast('Created ' + path, 'ok'); } catch (e) { toast(e.message, 'bad'); }
    return;
  }
  let proposed = applySnippet(before, code);
  if (proposed === null) {
    if (!await confirmBox('Where does it go?', `The code's first and last lines are not in ${path}, so where it belongs is unclear. Replace the whole file with it, or cancel and use Insert at the cursor (VS Code) or ask the model to apply it.`, { confirm: 'Replace the whole file' })) return;
    proposed = code.replace(/\n?$/, '\n');
  }
  if (proposed === before) { toast('The file already has this code.', 'ok'); return; }
  // VS Code shows its own diff (accept, reject, or revert single changes there); elsewhere the diff below with a checkbox per change.
  if (cc.reviewApply) {
    try { const r = await cc.reviewApply({ path, content: proposed }); toast(r && r.applied ? 'Applied to ' + path : 'Not applied', r && r.applied ? 'ok' : 'warn'); }
    catch (e) { toast(e.message, 'bad'); }
    return;
  }
  const key = 'apply-' + id;
  NAV.picks.delete(key);
  openModal('Apply to ' + path, `<div class="apply-diff">${hunkDiffHtml(before, proposed, key)}</div>`,
    `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="apply-ok" data-key="${esc(key)}" data-path="${esc(path)}">Apply</button>`, 'wide');
};
ACTIONS['apply-ok'] = async el => {
  const key = el.dataset.key, h = NAV.hunks.get(key), picks = NAV.picks.get(key);
  const content = h && h.count >= 2 ? applyHunks(key, picks || new Set()) : applyHunks(key, new Set([0]));
  closeModal();
  try { await writeLocal(el.dataset.path, content); toast('Applied to ' + el.dataset.path, 'ok'); } catch (e) { toast(e.message, 'bad'); }
};
ACTIONS['code-insert'] = async el => {
  $$('.cb-apply-menu').forEach(m => m.remove());
  try { const r = await window.chatClient.insertCode({ code: blockCode(el.dataset.block) }); toast(r && r.ok === false ? (r.result || 'Open a file in the editor first.') : 'Inserted', r && r.ok === false ? 'bad' : 'ok'); }
  catch (e) { toast(e.message, 'bad'); }
};
ACTIONS['code-newfile'] = async el => {
  $$('.cb-apply-menu').forEach(m => m.remove());
  const cc = window.chatClient, id = el.dataset.block;
  const path = await askPath('Save as a new file', guessCodePath(id));
  if (!path) return;
  let exists = false;
  try { const r = await cc.fileText(path); exists = !!(r && typeof r.text === 'string'); } catch { exists = false; }
  if (exists && !await confirmBox('Replace the file?', `${path} exists. Replace its content with this code?`, { confirm: 'Replace', danger: true })) return;
  try { await writeLocal(path, blockCode(id).replace(/\n?$/, '\n')); toast('Saved ' + path, 'ok'); } catch (e) { toast(e.message, 'bad'); }
};

// ------------------------------------------------------------------ /usage
function tokensText(o) { return `${num((o.input || 0) + (o.output || 0))} tokens`; }
async function showUsage() {
  let u;
  try { u = await get('/api/chat/usage'); } catch (e) { toast(e.message, 'bad'); return; }
  const tile = (label, o) => `<div class="use-tile"><div class="muted small">${label}</div><div class="use-big">${tokensText(o)}</div>
    <div class="muted small">${num(o.input)} in · ${num(o.output)} out · ${num(o.calls)} call${o.calls === 1 ? '' : 's'}${u.hasAccount ? ' · ' + money(o.costMicros) : ''}</div></div>`;
  const lim = u.limits || {};
  const left = lim.resetsUtc ? Math.max(0, new Date(lim.resetsUtc) - Date.now()) : 0;
  const limitHtml = lim.dailyTokens > 0 ? (() => {
    const pct = Math.min(100, Math.round(lim.usedToday / lim.dailyTokens * 100));
    return `<div class="use-limit"><div class="row"><b>Daily tokens</b><span class="spacer"></span><span class="muted small">${num(lim.usedToday)} of ${num(lim.dailyTokens)} · resets in ${dur(left)}</span></div>
      <div class="use-bar${pct >= 90 ? ' hot' : ''}"><span data-w="${pct}"></span></div></div>`;
  })() : '';
  const max = Math.max(1, ...(u.daily || []).map(d => d.input + d.output));
  // Sizes are set after drawing (the console's CSP allows no style attributes in HTML).
  const days = (u.daily || []).map(d => `<span class="use-day" data-h="${Math.max(2, Math.round((d.input + d.output) / max * 100))}" data-tip="${esc(d.day)} · ${num(d.input + d.output)} tokens · ${num(d.calls)} calls"></span>`).join('');
  openModal('Usage', `<div class="use-tiles">${tile('Today', u.today)}${tile('This month', u.month)}${tile('Last 30 days', u.days30)}</div>
    ${u.hasAccount ? `<div class="row use-balance"><b>Balance</b><span class="spacer"></span><span>${money(u.balanceMicros)}</span></div>` : ''}
    ${limitHtml}${lim.requestsPerMinute > 0 ? `<div class="muted small">Up to ${num(lim.requestsPerMinute)} requests a minute.</div>` : ''}
    ${days ? `<div class="use-days" aria-label="Tokens per day, last 30 days">${days}</div><div class="muted small use-days-note">Tokens per day, last 30 days (UTC)</div>` : ''}
    ${(u.byModel || []).length ? `<table class="table use-models"><thead><tr><th>Model (30 days)</th><th>Today</th><th>In</th><th>Out</th><th>Calls</th>${u.hasAccount ? '<th>Cost</th>' : ''}</tr></thead><tbody>
      ${u.byModel.map(r => `<tr><td>${esc(modelName(r.model))}<div class="cell-sub">${esc(r.model)}</div></td><td>${num(r.todayTokens)}</td><td>${num(r.input)}</td><td>${num(r.output)}</td><td>${num(r.calls)}</td>${u.hasAccount ? `<td>${money(r.costMicros)}</td>` : ''}</tr>`).join('')}</tbody></table>`
      : emptyState('No use yet', 'Messages in Chat, the CLI, the desktop app, VS Code and API calls count here.')}
    <p class="muted small">This account's use in every app and the API. /cost shows this chat only.</p>`, '', 'wide');
  $$('#modal-card .use-day[data-h]').forEach(d => { d.style.height = d.dataset.h + '%'; });
  $$('#modal-card .use-bar span[data-w]').forEach(s => { s.style.width = s.dataset.w + '%'; });
}

// ------------------------------------------------------------------ /feedback
async function showFeedback(text) {
  const cc = window.chatClient;
  const canLogs = !!(cc && cc.logs);
  openModal('Send feedback', `<form id="fb-form" class="stack" data-form="chat-feedback">
      <label>What happened? What did you expect?<textarea name="text" rows="6" required placeholder="The answer stopped after… / The Run button…">${esc(text || '')}</textarea></label>
      ${CHAT.conv ? `<label class="check"><input type="checkbox" name="includeChat" checked> Include this chat (“${esc(CHAT.conv.title || 'chat')}”)</label>` : ''}
      ${canLogs ? `<label class="check"><input type="checkbox" name="includeLogs" checked> Include this app's logs</label>` : ''}
      <p class="muted small">It goes to the administrators' Inbox. Their reply shows here next time.</p></form>
      <div id="fb-earlier"></div>`,
    `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="submit-form" data-form-id="fb-form">Send</button>`, 'wide');
  try {
    const mine = await get('/api/chat/feedback');
    const box = $('#fb-earlier');
    if (box && mine.length) box.innerHTML = `<h4 class="section">Your earlier reports</h4><div class="fb-list">${mine.slice(0, 10).map(f => `<div class="fb-row"><div class="row"><span class="badge ${f.status === 'Answered' ? 'ok' : f.status === 'Pending' ? 'outline' : ''}">${esc(f.status === 'Answered' ? 'Replied' : f.status === 'Pending' ? 'Waiting' : f.status)}</span><span class="muted small">${esc(ago(f.createdUtc))}</span></div>
      <div class="fb-text">${esc(String(f.text || '').slice(0, 300))}</div>${f.reply ? `<div class="fb-reply"><b>${esc(f.by || 'Admin')}:</b> ${esc(f.reply)}</div>` : ''}</div>`).join('')}</div>`;
  } catch { /* an older server */ }
}
FORMS['chat-feedback'] = async (f, d) => {
  const cc = window.chatClient;
  let logs = '';
  if (d.includeLogs && cc && cc.logs) { try { logs = String(await cc.logs() || ''); } catch { logs = ''; } }
  const r = await post('/api/chat/feedback', {
    text: d.text, chatId: CHAT.conv && d.includeChat ? CHAT.conv.id : undefined, includeChat: !!d.includeChat, logs,
    app: (cc && cc.appName) || 'web', version: (cc && cc.appVersion) || '', platform: navigator.platform || ''
  });
  closeModal();
  toast('Sent to the administrators' + (r && r.fileName ? ' (' + r.fileName + ')' : ''), 'ok');
};

// ------------------------------------------------------------------ /pr-comments, /plugin
function prCommentsPrompt(pr) {
  const which = pr ? `pull request ${pr}` : 'the pull request of the current branch';
  return `Work through the review comments on ${which}.\n` +
    `1. Find it: \`gh pr view ${pr || ''} --json number,url,title,headRefName,baseRefName\` (GitHub). For Gitea or another host, use its CLI (tea) or its API with the git remote's address.\n` +
    `2. Read every comment, the inline ones with their file and line too: \`gh api repos/{owner}/{repo}/pulls/{number}/comments\`, \`gh api repos/{owner}/{repo}/pulls/{number}/reviews\` and \`gh pr view ${pr || ''} --comments\`.\n` +
    `3. List them briefly, then fix each one in the code (or say why it should not change). Check your fixes (build, tests).\n` +
    `4. End with a table: comment (file:line) → what you changed. Do not push or reply on the pull request unless I ask.`;
}
async function showPlugins() {
  const cc = window.chatClient;
  let d;
  try { d = await cc.plugins({ action: 'list' }); } catch (e) { toast(e.message, 'bad'); return; }
  renderPlugins(d);
}
function pluginCounts(c) { return c ? Object.entries(c).filter(([, v]) => v).map(([k, v]) => `${v} ${k}`).join(' · ') : ''; }
function renderPlugins(d) {
  const plugins = d.plugins || [], markets = d.marketplaces || [];
  const installed = new Set(plugins.map(p => p.id));
  const body = `<p class="muted small">Claude Code plugins: their commands, agents, skills, hooks and MCP servers work here. Switching one off here does not change Claude Code.</p>
    ${plugins.length ? `<div class="plug-list">${plugins.map(p => `<div class="plug-row${p.enabled ? '' : ' off'}"><div class="grow"><b>${esc(p.name)}</b> <span class="muted small">${esc(p.marketplace ? '@' + p.marketplace : '')}${p.version ? ' · ' + esc(p.version) : ''} · ${esc(p.source || '')}</span>
        ${p.description ? `<div class="muted small">${esc(p.description)}</div>` : ''}<div class="muted small">${esc(pluginCounts(p.counts) || 'nothing to load')}${p.enabled && p.counts && p.counts.hooks && p.hooksOn === false ? ' · its hooks are written for Claude Code: they run here once you turn it on here' : ''}</div></div>
        ${p.enabled && p.counts && p.counts.hooks && p.hooksOn === false ? `<button class="btn xs" data-act="plug-do" data-op="enable" data-id="${esc(p.id)}" data-tip="Its hooks run in Agentic too">Turn on here</button>` : ''}
        <button class="btn xs${p.enabled ? '' : ' primary'}" data-act="plug-do" data-op="${p.enabled ? 'disable' : 'enable'}" data-id="${esc(p.id)}">${p.enabled ? 'Turn off' : 'Turn on'}</button>
        ${p.removable ? `<button class="btn xs ghost danger" data-act="plug-do" data-op="uninstall" data-id="${esc(p.id)}">Uninstall</button>` : ''}</div>`).join('')}</div>`
      : emptyState('No plugins', 'Add a marketplace below and install a plugin, or install them with Claude Code.')}
    <h4 class="section">Marketplaces</h4>
    ${markets.map(m => `<details class="plug-market"><summary><b>${esc(m.name)}</b> <span class="muted small">${esc(m.source || '')} · ${num((m.plugins || []).length)} plugins</span>${m.removable ? `<span class="spacer"></span><button class="btn xs ghost" data-act="plug-do" data-op="marketplace-update" data-name="${esc(m.name)}">Update</button><button class="btn xs ghost danger" data-act="plug-do" data-op="marketplace-remove" data-name="${esc(m.name)}">Remove</button>` : ''}</summary>
      ${(m.plugins || []).map(p => `<div class="plug-row"><div class="grow"><b>${esc(p.name)}</b>${p.version ? ` <span class="muted small">${esc(p.version)}</span>` : ''}${p.description ? `<div class="muted small">${esc(p.description)}</div>` : ''}</div>
        ${installed.has(p.name + '@' + m.name) ? '<span class="badge ok">installed</span>' : `<button class="btn xs" data-act="plug-do" data-op="install" data-name="${esc(p.name)}" data-market="${esc(m.name)}">Install</button>`}</div>`).join('') || '<div class="muted small">No plugins listed.</div>'}</details>`).join('')}
    <form id="plug-add" class="row plug-add" data-form="plugin-market"><input class="input grow" name="source" placeholder="Add a marketplace: owner/repo, a git URL, or a folder" required><button class="btn" type="submit">Add</button></form>`;
  if ($('#plug-body')) $('#plug-body').innerHTML = body;
  else openModal('Plugins', `<div id="plug-body">${body}</div>`, '', 'wide');
}
ACTIONS['plug-do'] = async el => {
  const op = el.dataset.op;
  if (op === 'uninstall' && !await confirmBox('Uninstall the plugin?', `${el.dataset.id} is removed from Agentic's plugins.`, { confirm: 'Uninstall', danger: true })) return;
  el.disabled = true;
  try {
    const d = await window.chatClient.plugins({ action: op, id: el.dataset.id, name: el.dataset.name, marketplace: el.dataset.market });
    renderPlugins(d);
    toast(op === 'install' ? 'Installed: its commands, skills and agents work from the next message' : 'Done', 'ok');
  } catch (e) { toast(e.message, 'bad'); el.disabled = false; }
};
FORMS['plugin-market'] = async (f, d) => {
  const r = await window.chatClient.plugins({ action: 'marketplace-add', source: d.source });
  renderPlugins(r);
  toast('Marketplace added', 'ok');
};

// ------------------------------------------------------------------ commands and wiring
if (typeof CHAT_COMMANDS !== 'undefined') {
  CHAT_COMMANDS.push(
    { name: 'find', args: '[text]', group: 'Chat', desc: 'Find in this chat (Ctrl+F): every message, tool output and thinking', run: a => openFind(a || undefined) },
    { name: 'outline', group: 'Chat', desc: 'Your messages in this chat: click one to go there', run: () => { if (!NAV.outline) ACTIONS['outline-toggle'](); } },
    { name: 'pins', group: 'Chat', desc: 'Pinned messages in this chat', run: () => ACTIONS['pin-list']() },
    { name: 'expand', group: 'Chat', desc: 'Open every tool card and thinking on the page', run: () => setAllCards(true) },
    { name: 'collapse', group: 'Chat', desc: 'Fold every tool card and thinking on the page', run: () => setAllCards(false) },
    { name: 'transcript', group: 'Chat', desc: 'Transcript mode on or off (Ctrl+O): cards stay open', run: () => ACTIONS['transcript-toggle']() },
    { name: 'usage', group: 'More', desc: 'Your use: tokens today, this month and by model; balance and limits', run: () => showUsage() },
    { name: 'feedback', args: '[what happened]', group: 'More', desc: 'Send a report to the administrators (with this chat and the app\'s logs)', run: a => showFeedback(a) },
    { name: 'bug', args: '[what happened]', group: 'More', desc: 'Report a problem (like /feedback)', run: a => showFeedback(a) },
    { name: 'pr-comments', args: '[number | url]', group: 'Work', desc: 'Work through a pull request\'s review comments', when: () => !!(window.chatClient && window.chatClient.runTool), run: a => sendMessage(false, prCommentsPrompt((a || '').trim())) },
    { name: 'plugin', group: 'Settings', desc: 'Claude Code plugins: list, turn on or off, install from a marketplace', when: () => !!(window.chatClient && window.chatClient.plugins), run: () => showPlugins() },
    { name: 'plugins', group: 'Settings', desc: 'Claude Code plugins (like /plugin)', when: () => !!(window.chatClient && window.chatClient.plugins), run: () => showPlugins() });
}
document.addEventListener('chat:mounted', () => {
  closeFind();
  closeHistory(false);
  NAV.pinAt = 0;
  if (NAV.outline === false && LS.get('chat.outline') === '1' && !matchMedia('(max-width: 900px)').matches) NAV.outline = true;
  watchThread();
  markDays();
  renderPinBar();
  renderOutline();
  renderNavButtons();
  renderPasteChips();
  composerMode();
  if (NAV.transcript) { setAllCards(true); $('#chat-thread')?.classList.add('transcript'); }
});
document.addEventListener('chat:answered', () => { if (NAV.outline) renderOutline(); });
