'use strict';
/* Agentic console - Chat: rewind (conversation and code back to an earlier message), bookmarks (and a bookmarked-only
   chat list), thinking on/off, and the project's AGENTIC.md (in the desktop app and VS Code, which have a project folder).
   Code is restored by the app that changed the files (window.chatClient.restoreFiles): it saves each file before the
   first change an answer makes to it. */

const BOOKMARK_ICON = '<svg viewBox="0 0 24 24" class="ic"><path d="M6 3h12v18l-6-4-6 4z"/></svg>';
const REWIND_ICON = '<svg viewBox="0 0 24 24" class="ic"><path d="M11 6L5 12l6 6M19 6l-6 6 6 6"/></svg>';
try { CHAT.bookmarksOnly = LS.get('chat.bookmarksOnly') === '1'; } catch { CHAT.bookmarksOnly = false; }

// ------------------------------------------------------------------ bookmarks
function bookmarkBtnHtml(c) {
  return `<button class="btn icon ghost${c.bookmarked ? ' on' : ''}" data-act="chat-bookmark" aria-label="Bookmark" aria-pressed="${!!c.bookmarked}" data-tip="${c.bookmarked ? 'Remove bookmark' : 'Bookmark this chat'}">${BOOKMARK_ICON.replace('class="ic', `class="ic${c.bookmarked ? ' filled' : ''}`)}</button>`;
}
function bookmarkFilterHtml() {
  return `<button class="btn icon ghost bm-filter${CHAT.bookmarksOnly ? ' on' : ''}" data-act="chat-bookmarks-only" aria-pressed="${!!CHAT.bookmarksOnly}" aria-label="Bookmarked only"
    data-tip="${CHAT.bookmarksOnly ? 'Showing bookmarked chats only: show all' : 'Show only bookmarked chats'}">${BOOKMARK_ICON.replace('class="ic', `class="ic${CHAT.bookmarksOnly ? ' filled' : ''}`)}</button>`;
}
ACTIONS['chat-bookmark'] = async () => {
  if (!CHAT.conv) return;
  const on = !CHAT.conv.bookmarked;
  await patch('/api/chat/conversations/' + enc(CHAT.conv.id), { bookmarked: on });
  CHAT.conv.bookmarked = on;
  const item = CHAT.list.find(c => c.id === CHAT.conv.id); if (item) item.bookmarked = on;
  $('#chat-head').innerHTML = headHtml();
  const b = $('#chat-head [data-act="chat-bookmark"]'); if (b) b.classList.add('pop');
  renderChatList();
  toast(on ? 'Bookmarked' : 'Bookmark removed', 'ok');
};
ACTIONS['chat-bookmarks-only'] = el => {
  CHAT.bookmarksOnly = !CHAT.bookmarksOnly;
  try { LS.set('chat.bookmarksOnly', CHAT.bookmarksOnly ? '1' : ''); } catch { /* ignore */ }
  const node = htmlNode(bookmarkFilterHtml()); el.replaceWith(node); bounce(node);
  renderChatList();
};

// ------------------------------------------------------------------ thinking on / off
function thinkingOn() { return !(CHAT.settings && CHAT.settings.thinking === false); }
function setThinking(on) {
  CHAT.settings.thinking = on ? null : false;
  saveSettings();
  const wrap = $('.combo-effort');
  if (wrap) wrap.replaceWith(htmlNode(comboHtml('effort')));
  toast(on ? `Thinking on (${effortOf()[1]})` : 'Thinking off: answers come right away, without reasoning', 'ok');
}
ACTIONS['thinking-toggle'] = () => setThinking(!thinkingOn());

// ------------------------------------------------------------------ rewind
/** The user messages from this one on (the rewind takes them back, with their answers). */
function messagesFrom(messageId) {
  const msgs = CHAT.conv ? CHAT.conv.messages : [];
  const i = msgs.findIndex(m => m.id === messageId);
  return i < 0 ? [] : msgs.slice(i);
}
/** Files the app on this computer can put back to how they were before this message (empty when it cannot). */
async function codeCheckpoints(messageId) {
  const cc = window.chatClient;
  if (!cc || !cc.checkpoints || !CHAT.conv) return [];
  const ids = new Set(messagesFrom(messageId).filter(m => m.role === 'user').map(m => m.id));
  try {
    const list = await cc.checkpoints({ chatId: CHAT.conv.id });
    const files = new Set();
    (list || []).filter(c => ids.has(c.messageId)).forEach(c => (c.files || []).forEach(f => files.add(f)));
    return [...files];
  } catch { return []; }
}
async function rewindFrom(messageId) {
  if (!CHAT.conv) return;
  if (CHAT.streams.has(CHAT.conv.id) || CHAT.live.has(CHAT.conv.id)) { toast('Wait until the answer is finished (or stop it), then rewind.', 'bad'); return; }
  const msgs = messagesFrom(messageId);
  const target = msgs[0];
  if (!target) return;
  const files = await codeCheckpoints(messageId);
  const canCode = files.length > 0;
  const later = msgs.length;
  const fileList = canCode ? `<ul class="rewind-files">${files.slice(0, 12).map(f => `<li><code>${esc(f)}</code></li>`).join('')}${files.length > 12 ? `<li class="muted">…and ${files.length - 12} more</li>` : ''}</ul>` : '';
  const opt = (v, label, desc, on, disabled) => `<label class="radio-card${disabled ? ' disabled' : ''}"><input type="radio" name="rewind" value="${v}"${on ? ' checked' : ''}${disabled ? ' disabled' : ''}><span><b>${label}</b><span class="muted small">${desc}</span></span></label>`;
  openModal('Rewind to here', `<div class="stack">
      <p class="muted small">Go back to before <b>“${esc((target.content || '').slice(0, 90))}${(target.content || '').length > 90 ? '…' : ''}”</b>. ${later} message${later === 1 ? '' : 's'} from there on ${later === 1 ? 'is' : 'are'} removed, and the question goes back into the message box.</p>
      ${opt('both', 'Conversation and code', canCode ? `Also put back ${files.length} file${files.length === 1 ? '' : 's'} changed since then.` : 'No file changes from here on that this app can put back.', canCode, !canCode)}
      ${opt('chat', 'Conversation only', 'Files stay as they are now.', !canCode, false)}
      ${opt('code', 'Code only', 'The conversation stays; only the files go back.', false, !canCode)}
      ${fileList}
      <p class="muted small">Changes made by shell commands (not by the edit tools) cannot be put back.</p></div>`,
    `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="rewind-go" data-id="${esc(messageId)}">${REWIND_ICON} Rewind</button>`, 'narrow');
}
ACTIONS['msg-rewind'] = el => { const m = el.closest('.msg'); if (m && m.dataset.id) rewindFrom(m.dataset.id); };
ACTIONS['rewind-go'] = async el => {
  const id = el.dataset.id, mode = ($('input[name="rewind"]:checked') || {}).value || 'chat';
  const conv = CHAT.conv;
  const userIds = messagesFrom(id).filter(m => m.role === 'user').map(m => m.id);
  let restored = null;
  if (mode !== 'chat' && window.chatClient && window.chatClient.restoreFiles) {
    restored = await window.chatClient.restoreFiles({ chatId: conv.id, messageIds: userIds });
    const failed = (restored && restored.failed) || [];
    if (failed.length) toast(`${failed.length} file${failed.length === 1 ? '' : 's'} could not be put back: ${failed.map(f => f.path).join(', ')}`, 'bad');
  }
  let prompt = null;
  if (mode !== 'code') {
    const r = await post(`/api/chat/conversations/${enc(conv.id)}/rewind`, { messageId: id });
    prompt = r.prompt;
    await resyncConversation();
    refreshList();
  }
  closeModal();
  if (prompt && prompt.content) {
    const input = $('#chat-input');
    if (input) { input.value = prompt.content; autosize(); input.focus(); }
  }
  const n = restored && restored.restored ? restored.restored.length : 0;
  toast(mode === 'code' ? `Put back ${n} file${n === 1 ? '' : 's'}` : mode === 'both' ? `Rewound the conversation and put back ${n} file${n === 1 ? '' : 's'}` : 'Rewound the conversation', 'ok');
};
/** /rewind: pick the message to go back to (newest first). */
function rewindPicker() {
  if (!CHAT.conv) { toast('Start a conversation first.', 'bad'); return; }
  const qs = CHAT.conv.messages.filter(m => m.role === 'user' && m.finishReason !== 'auto_goal').slice(-20).reverse();
  if (!qs.length) { toast('There is nothing to rewind yet.', 'bad'); return; }
  openModal('Rewind to…', `<div class="rewind-pick">${qs.map(m => `<button class="rewind-row" data-act="rewind-pick" data-id="${esc(m.id)}"><span class="t">${esc((m.content || '(files only)').replace(/\s+/g, ' ').slice(0, 110))}</span><span class="muted small">${esc(ago(m.createdUtc))}</span></button>`).join('')}</div>`,
    `<button class="btn" data-act="modal-close">Cancel</button>`, 'narrow');
}
ACTIONS['rewind-pick'] = el => { closeModal(); rewindFrom(el.dataset.id); };

// ------------------------------------------------------------------ AGENTIC.md (the project's instructions file)
async function openAgenticMd() {
  const md = window.chatClient && window.chatClient.agenticMd;
  if (!md) {
    toast('AGENTIC.md lives in your project folder: open this chat in the desktop app, VS Code or the CLI to edit it. Here, use Personalization or a project\'s instructions.', 'warn');
    return;
  }
  let f;
  try { f = await md.read(); } catch (e) { toast(e.message || String(e), 'bad'); return; }
  if (!f || !f.path) { toast('Open a folder first: AGENTIC.md is kept in the project folder.', 'bad'); return; }
  openModal('AGENTIC.md', `<form id="agentic-form" class="stack" data-form="agentic-md" data-path="${esc(f.path)}">
      <p class="muted small"><code>${esc(f.path)}</code>${f.exists ? '' : ' (new file)'} · instructions every message in this project includes. One rule per line works best.</p>
      <textarea name="content" class="agentic-md" rows="16" spellcheck="false" placeholder="# Project instructions&#10;- Use tabs, not spaces&#10;- Run the tests with: dotnet test">${esc(f.content || '')}</textarea>
      <div class="row"><input name="add" placeholder="Add a rule (Enter)" autocomplete="off" data-agentic-add><button type="button" class="btn small" data-act="agentic-add">${icon('plus')} Add</button></div></form>`,
    `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="submit-form" data-form-id="agentic-form">Save</button>`, 'wide');
}
ACTIONS['agentic-add'] = () => {
  const f = $('#agentic-form'); if (!f) return;
  const add = f.querySelector('[name="add"]'), ta = f.querySelector('[name="content"]');
  const line = (add.value || '').trim();
  if (!line) return;
  ta.value = (ta.value.replace(/\s+$/, '') + (ta.value.trim() ? '\n' : '') + (/^[-*#]/.test(line) ? line : '- ' + line)) + '\n';
  add.value = ''; add.focus();
};
document.addEventListener('keydown', ev => {
  if (ev.key === 'Enter' && ev.target.matches && ev.target.matches('[data-agentic-add]')) { ev.preventDefault(); ACTIONS['agentic-add'](); }
});
FORMS['agentic-md'] = async (f, d) => {
  await window.chatClient.agenticMd.write({ path: f.dataset.path, content: d.content || '' });
  closeModal();
  toast('AGENTIC.md saved: the next message uses it', 'ok');
};

// ------------------------------------------------------------------ slash commands
if (typeof CHAT_COMMANDS !== 'undefined') {
  CHAT_COMMANDS.push(
    { name: 'rewind', group: 'Chat', desc: 'Go back to an earlier message: restore the conversation and/or code', run: () => rewindPicker() },
    { name: 'bookmark', group: 'Chat', desc: 'Bookmark or un-bookmark this chat', run: () => needChat() && ACTIONS['chat-bookmark']() },
    { name: 'bookmarks', group: 'Chat', desc: 'Show only bookmarked chats in the list (again: all)', run: () => { const b = $('.bm-filter'); if (b) ACTIONS['chat-bookmarks-only'](b); } },
    { name: 'thinking', args: '[on | off]', group: 'Settings', desc: 'Thinking on or off (off: answers right away, no reasoning)', run: a => setThinking(onOff(a, thinkingOn())) },
    { name: 'agentic', group: 'Settings', desc: 'Edit the project\'s AGENTIC.md (instructions every message includes)', run: () => openAgenticMd() });
}

// ================================================================== Claude Code-style working features
// ------------------------------------------------------------------ special tool cards: task list, subagents, your notes
const TODO_ICONS = { completed: '<span class="td-box done">✓</span>', in_progress: '<span class="td-box doing"></span>', pending: '<span class="td-box"></span>' };
function todosOf(t) { try { return (JSON.parse(t.arguments || '{}').todos || []); } catch { return []; } }
function todoCardHtml(t) {
  const list = todosOf(t), done = list.filter(x => x.status === 'completed').length;
  return `<div class="todo-card" data-call="${esc(t.id)}"><div class="td-head"><b>Tasks</b><span class="muted small">${done} of ${list.length} done</span></div>
    <ul class="td-list">${list.map(x => `<li class="td-${esc(x.status)}">${TODO_ICONS[x.status] || TODO_ICONS.pending}<span>${esc(x.content)}</span></li>`).join('')}</ul></div>`;
}
function noteCardHtml(t) {
  let text = ''; try { text = JSON.parse(t.arguments || '{}').text || ''; } catch { /* ignore */ }
  return `<div class="user-note" data-call="${esc(t.id)}"><span class="un-who">You, while it worked:</span> ${esc(text)}</div>`;
}
const baseToolCardHtml = toolCardHtml;
// eslint-disable-next-line no-global-assign
toolCardHtml = function (t) {
  if (t.name === 'update_todos') return todoCardHtml(t);
  if (t.name === 'user_note') return noteCardHtml(t);
  // A subagent's card is named "subagent" (its description and type come from subagentLabel).
  if (t.name === 'task') return baseToolCardHtml(t).replace(/<code>task<\/code>/, '<code>subagent</code>');
  return baseToolCardHtml(t);
};

// ------------------------------------------------------------------ typing while the model works: steer it, or queue
// Two kinds wait above the message box, each with Edit and ×: "Next step" - sent to the running answer, the model reads it
// before its next step (on the server until then, so any window can take it back) - and "Queued" - kept here and sent as
// the next message when the answer ends (the answer could not take it any more).
CHAT.queuedBy = CHAT.queuedBy || new Map();   // chatId -> [text]
CHAT.steers = CHAT.steers || new Map();       // chatId -> [{id, text}] not read by the model yet
function queuedFor(chatId) { let q = CHAT.queuedBy.get(chatId); if (!q) CHAT.queuedBy.set(chatId, q = []); return q; }
/** A message typed while the answer runs: the model reads it before its next step (or it goes next, if it just ended). */
async function steerOrQueue(text) {
  const conv = CHAT.conv;
  if (!conv || !text.trim()) return;
  const input = $('#chat-input'); if (input) { input.value = ''; autosize(); }
  try {
    const r = await post(`/api/chat/conversations/${enc(conv.id)}/steer`, { content: text });
    if (r && Array.isArray(r.items)) CHAT.steers.set(conv.id, r.items);
  } catch (e) {
    queuedFor(conv.id).push(text);
  }
  renderQueued();
}
function renderQueued() {
  let bar = $('#queued-bar');
  const c = $('#composer');
  if (!c) return;
  const id = CHAT.conv && CHAT.conv.id;
  const steers = id ? CHAT.steers.get(id) || [] : [], queued = id ? CHAT.queuedBy.get(id) || [] : [];
  if (!steers.length && !queued.length) { if (bar) bar.remove(); return; }
  if (!bar) { bar = document.createElement('div'); bar.id = 'queued-bar'; bar.className = 'queued-bar'; c.parentNode.insertBefore(bar, c); }
  const row = (kind, title, text, key) => `<div class="queued-item" title="${esc(title)}"><span class="queued-kind">${kind}</span>` +
    `<span class="queued-text">${esc(text.length > 200 ? text.slice(0, 200) + '…' : text)}</span>` +
    `<button class="btn xs ghost" data-act="queued-edit" ${key} title="Take it back into the message box">Edit</button>` +
    `<button class="btn xs ghost" data-act="queued-drop" ${key} aria-label="Remove" title="Remove">×</button></div>`;
  bar.innerHTML = steers.map(s => row('Next step', 'The model reads this before its next step. Edit or remove it until then.', s.text, `data-id="${esc(s.id)}"`)).join('') +
    queued.map((q, i) => row('Queued', 'Sent as the next message when this answer ends.', q, `data-i="${i}"`)).join('');
}
ACTIONS['queued-drop'] = el => takeBackQueued(el, false);
ACTIONS['queued-edit'] = el => takeBackQueued(el, true);
async function takeBackQueued(el, edit) {
  const conv = CHAT.conv;
  if (!conv) return;
  if (!el.dataset.id) {
    const [text] = queuedFor(conv.id).splice(Number(el.dataset.i), 1);
    renderQueued();
    if (edit && text) backToInput([text]);
    return;
  }
  const item = (CHAT.steers.get(conv.id) || []).find(s => s.id === el.dataset.id);
  try {
    const r = await del(`/api/chat/conversations/${enc(conv.id)}/steers/${enc(el.dataset.id)}`);
    CHAT.steers.set(conv.id, r.items || []);
    renderQueued();
    if (!r.removed) { toast('Too late: the model has already read it.', 'warn'); return; }
    if (edit && item) backToInput([item.text]);
  } catch (e) { toast(e.message, 'bad'); }
}
/** Messages that were not sent go back into the message box (before what is typed there). */
function backToInput(texts) {
  const input = $('#chat-input');
  if (!input || !texts || !texts.length) return;
  input.value = [...texts, input.value.trim()].filter(Boolean).join('\n\n');
  autosize();
  input.focus();
}
/** The answer ended in this window: its unread messages are gone from the server. Stopped: queued ones go back to the box. */
function queueAfterAnswer(chatId, stopped) {
  CHAT.steers.delete(chatId);
  if (stopped && CHAT.conv && CHAT.conv.id === chatId) { const q = CHAT.queuedBy.get(chatId) || []; CHAT.queuedBy.delete(chatId); backToInput(q); }
  renderQueued();
}
/** After an answer: messages queued while it ran go next, together. */
function sendQueued() {
  if (!CHAT.conv || currentStream() || CHAT.live.has(CHAT.conv.id)) return;
  const q = CHAT.queuedBy.get(CHAT.conv.id) || [];
  if (!q.length) return;
  const text = q.join('\n\n');
  CHAT.queuedBy.delete(CHAT.conv.id);
  renderQueued();
  sendMessage(false, text).catch(e => toast(e.message, 'bad'));
}

// ------------------------------------------------------------------ Esc Esc: rewind (chat.rewind in Keyboard shortcuts)
let rewindBlocked = 0;   // a first press while an answer runs or the box has text does not start the pair
document.addEventListener('keydown', ev => {
  if (!ev.target || ev.target.id !== 'chat-input') return;
  if (currentStream() || ev.target.value) { rewindBlocked = Date.now(); return; }
  const firstAt = KEYS.seq.prev ? KEYS.seq.prev.t : 0;
  if (keyIs(ev, 'chat.rewind') && rewindBlocked < firstAt) { ev.preventDefault(); rewindPicker(); }
});

// ------------------------------------------------------------------ @-mentions: files on this computer (desktop app, VS Code)
let mentionTimer = 0;
function mentionAt(input) {
  const upto = input.value.slice(0, input.selectionStart);
  const m = upto.match(/(^|\s)@([^\s@]*)$/);
  return m ? { query: m[2], start: upto.length - m[2].length - 1 } : null;
}
function hideMentions() { const p = $('#mention-pop'); if (p) p.remove(); }
document.addEventListener('input', ev => {
  const input = ev.target;
  if (!input || input.id !== 'chat-input' || !window.chatClient || !window.chatClient.listFiles) return;
  const at = mentionAt(input);
  if (!at) { hideMentions(); return; }
  clearTimeout(mentionTimer);
  mentionTimer = setTimeout(async () => {
    let files = [];
    try { files = await window.chatClient.listFiles(at.query); } catch { files = []; }
    if (!mentionAt(input)) return;
    hideMentions();
    if (!files.length) return;
    const pop = document.createElement('div');
    pop.id = 'mention-pop'; pop.className = 'menu mention-pop';
    pop.innerHTML = files.slice(0, 12).map((f, i) => `<button type="button" class="mention-row${i === 0 ? ' on' : ''}" data-act="mention-pick" data-path="${esc(f)}">${esc(f)}</button>`).join('');
    const c = $('#composer'); c.appendChild(pop);
  }, 120);
});
document.addEventListener('keydown', ev => {
  const pop = $('#mention-pop');
  if (!pop || ev.target.id !== 'chat-input') return;
  const rows = $$('.mention-row', pop), i = rows.findIndex(r => r.classList.contains('on')), down = keyIs(ev, 'list.next');
  if (down || keyIs(ev, 'list.prev')) {
    ev.preventDefault();
    const n = (i + (down ? 1 : -1) + rows.length) % rows.length;
    rows.forEach((r, k) => r.classList.toggle('on', k === n));
  } else if (keyIs(ev, 'list.pick') && i >= 0) {
    ev.preventDefault(); ev.stopImmediatePropagation(); pickMention(rows[i].dataset.path);
  } else if (keyIs(ev, 'list.close')) { ev.stopImmediatePropagation(); hideMentions(); }
}, true);
function pickMention(path) {
  const input = $('#chat-input'); const at = mentionAt(input);
  if (!at) return;
  const quoted = /\s/.test(path) ? `"${path}"` : path;
  input.value = input.value.slice(0, at.start) + '@' + quoted + ' ' + input.value.slice(input.selectionStart);
  hideMentions(); input.focus(); autosize();
}
ACTIONS['mention-pick'] = el => pickMention(el.dataset.path);

// ------------------------------------------------------------------ session cost (this chat)
function sessionTotals() {
  const msgs = (CHAT.conv && CHAT.conv.messages) || [];
  return msgs.reduce((t, m) => ({ cost: t.cost + (m.costMicros || 0), inTok: t.inTok + (m.promptTokens || 0), outTok: t.outTok + (m.completionTokens || 0) }), { cost: 0, inTok: 0, outTok: 0 });
}
function sessionTotalsText() {
  const t = sessionTotals();
  return `This chat: ${num(t.inTok)} tokens in · ${num(t.outTok)} out${t.cost ? ` · $${(t.cost / 1e6).toFixed(4)}` : ''}`;
}

// ------------------------------------------------------------------ permission rules (allow / ask / deny)
async function openPermissionRules() {
  const r = await get('/api/chat/permission-rules');
  const box = (k, label, help) => `<label>${label}<textarea name="${k}" rows="4" spellcheck="false" placeholder="${esc(help)}">${esc((r[k] || []).join('\n'))}</textarea></label>`;
  openModal('Permission rules', `<form id="rules-form" class="stack" data-form="permission-rules">
      <p class="muted small">One pattern per line: a tool name (wildcards <code>*</code>), or <code>tool(argument)</code> matched against the file, command, URL or query.
        Deny blocks the call, Ask always asks first (also in Auto), Allow never asks (also in Manual). Checked in that order; they apply to all your chats.</p>
      ${box('deny', 'Deny', 'run_command(rm *)\n*__delete*')}
      ${box('ask', 'Ask first', 'write_file(*.json)\nrun_command(git push*)')}
      ${box('allow', 'Allow without asking', 'run_command(npm test*)\nread_file')}</form>`,
    `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="submit-form" data-form-id="rules-form">Save rules</button>`, 'wide');
}
FORMS['permission-rules'] = async (f, d) => {
  const lines = k => (d[k] || '').split('\n').map(x => x.trim()).filter(Boolean);
  await put('/api/chat/permission-rules', { allow: lines('allow'), ask: lines('ask'), deny: lines('deny') });
  closeModal(); toast('Permission rules saved', 'ok');
};

// ------------------------------------------------------------------ output style
const STYLES = [['default', 'Default'], ['explanatory', 'Explanatory: insights while it works'], ['learning', 'Learning: teaches, leaves small parts for you'], ['concise', 'Concise: as short as possible']];
function setStyle(arg) {
  const v = (arg || '').trim().toLowerCase();
  const pick = STYLES.find(s => s[0] === v);
  if (!pick) { toast(`Output style: ${(CHAT.settings.outputStyle || 'default')}. Choose ${STYLES.map(s => s[0]).join(', ')}.`, 'ok'); return; }
  CHAT.settings.outputStyle = pick[0] === 'default' ? null : pick[0];
  saveSettings();
  toast('Output style: ' + pick[1], 'ok');
}

// ------------------------------------------------------------------ /doctor
async function runDoctor() {
  const rows = [];
  const add = (ok, what, detail) => rows.push(`<li class="${ok ? 'ok' : 'bad'}">${ok ? icon('check') : icon('alert')} <b>${esc(what)}</b> <span class="muted small">${esc(detail || '')}</span></li>`);
  try { const h = await get('/healthz'); add(h.status === 'ok', 'Server', location.origin); } catch (e) { add(false, 'Server', e.message); }
  try {
    const meta = await get('/api/chat/meta');
    add(!meta.providerError, 'Models', meta.providerError || `${(meta.providerModels || []).length} provider models, ${(meta.customModels || []).length} custom`);
    add(true, 'Balance', meta.account && meta.account.charged ? `$${(meta.account.balanceMicros / 1e6).toFixed(2)}` : 'not charged');
    add(!!meta.webSearch, 'Web search', meta.webSearch ? 'available' : 'not set up on the server');
    const servers = meta.servers || [];
    add(servers.every(s => s.connected), 'Server tools (MCP)', servers.length ? servers.map(s => `${s.name}: ${s.connected ? 'connected' : 'down'}`).join(', ') : 'none');
  } catch (e) { add(false, 'Chat', e.message); }
  const cc = window.chatClient;
  add(!!cc, 'This computer', cc ? 'desktop app or VS Code: tools on this computer are available' : 'browser only: no files or commands on this computer');
  if (cc && cc.doctor) { try { (await cc.doctor()).forEach(r => add(r.ok, r.what, r.detail)); } catch (e) { add(false, 'Local tools', e.message); } }
  if (CHAT.ctx) add(CHAT.ctx.percent < 85, 'Context', `${num(CHAT.ctx.used)} of ${num(CHAT.ctx.max)} tokens`);
  add(true, 'Hub', CHAT.hub ? 'live updates connected' : 'not connected');
  openModal('Doctor', `<ul class="doctor">${rows.join('')}</ul>`, `<button class="btn primary" data-act="modal-close">Close</button>`, 'narrow');
}

// ------------------------------------------------------------------ prompt commands: /init, /review, /commit, /pr
const PROMPTS = {
  init: 'Look through this project (its folders, main files, build and test setup, conventions) and write AGENTIC.md in the project root: a short guide for an AI assistant working here - what the project is, how to build, run and test it, the code style and conventions, important folders and files, and things to watch out for. Keep it concise and factual; if AGENTIC.md already exists, improve it instead of replacing what is still right.',
  review: 'Review my uncommitted changes (run git status and git diff, including staged changes). Report bugs, risks, missing tests and style problems with file and line references, most important first. Do not change any files.',
  commit: 'Look at my uncommitted changes (git status, git diff), then stage them and create one commit with a clear, conventional commit message (a short summary line and, if useful, a body). Do not push. Show me the commit you made.',
  pr: 'Prepare a pull request for the current branch: check git status and the commits since the main branch, push the branch, and open a pull request with the gh CLI with a clear title and a description of what changed and how it was tested. Show me the link.'
};
function needTools(cmd) {
  if (window.chatClient) return true;
  toast(`/${cmd} works on a project folder: use it in the desktop app or VS Code (or the CLI).`, 'bad');
  return false;
}

// ------------------------------------------------------------------ /add-dir
async function addDir(arg) {
  const cc = window.chatClient;
  if (!cc || !cc.addDir) { toast('Adding folders works in the desktop app and VS Code.', 'bad'); return; }
  const path = (arg || '').trim() || (await confirmBox('Add a folder', 'Folder the model may work in', { input: 'Path', confirm: 'Add' }));
  if (typeof path !== 'string' || !path.trim()) return;
  try { const r = await cc.addDir(path.trim()); toast(`Added ${r && r.path || path}`, 'ok'); } catch (e) { toast(e.message || String(e), 'bad'); }
}

if (typeof CHAT_COMMANDS !== 'undefined') {
  CHAT_COMMANDS.push(
    { name: 'init', group: 'Work', desc: 'Look through this project and write AGENTIC.md', run: () => needTools('init') && sendMessage(false, PROMPTS.init) },
    { name: 'review', group: 'Work', desc: 'Review your uncommitted changes (git diff)', run: () => needTools('review') && sendMessage(false, PROMPTS.review) },
    { name: 'commit', group: 'Work', desc: 'Commit your changes with a good message', run: () => needTools('commit') && sendMessage(false, PROMPTS.commit) },
    { name: 'pr', group: 'Work', desc: 'Push the branch and open a pull request (gh)', run: () => needTools('pr') && sendMessage(false, PROMPTS.pr) },
    { name: 'permissions', group: 'Settings', desc: 'Allow / ask / deny rules per tool and argument', run: () => openPermissionRules() },
    { name: 'style', args: 'default | explanatory | learning | concise', group: 'Settings', desc: 'Output style', run: a => setStyle(a) },
    { name: 'doctor', group: 'More', desc: 'Check the server, models, tools and this computer', run: () => runDoctor() },
    { name: 'add-dir', args: '[folder]', group: 'Settings', desc: 'Let the model work in another folder (desktop app, VS Code)', run: a => addDir(a) },
    { name: 'cost', group: 'Chat', desc: 'Tokens and cost of this chat', run: () => toast(sessionTotalsText(), 'ok') });
}

// ------------------------------------------------------------------ Stop hooks (desktop app, VS Code)
// After an answer, the computer's Stop hooks run; a hook that blocks (exit code 2) sends its message back
// so the model keeps working. At most 3 times in a row per chat, so a hook cannot loop forever.
const STOP_HOOK_RUNS = new Map();
async function afterAnswerHook(conv, message) {
  const cc = window.chatClient;
  if (!cc || !cc.afterAnswer || !conv) return;
  let r;
  try { r = await cc.afterAnswer({ chatId: conv.id, text: message && typeof message.content === 'string' ? message.content : '', active: (STOP_HOOK_RUNS.get(conv.id) || 0) > 0 }); } catch { return; }
  if (!r || !r.followUp) { STOP_HOOK_RUNS.delete(conv.id); return; }
  const n = (STOP_HOOK_RUNS.get(conv.id) || 0) + 1;
  if (n > 3) { STOP_HOOK_RUNS.delete(conv.id); toast('A Stop hook kept asking for more work; stopped after 3 rounds.', 'bad'); return; }
  STOP_HOOK_RUNS.set(conv.id, n);
  if (!CHAT.conv || CHAT.conv.id !== conv.id) return;
  queuedFor(conv.id).push('(Stop hook) ' + r.followUp);
  renderQueued();
  setTimeout(sendQueued, 120);
}

// ------------------------------------------------------------------ custom commands (.agentic/commands, .claude/commands)
// Typed as "/name args": the message goes as written and the desktop app / VS Code adds the command's prompt.
let customCommandsLoaded = false;
async function loadCustomCommands() {
  const cc = window.chatClient;
  if (customCommandsLoaded || !cc || !cc.commands || typeof CHAT_COMMANDS === 'undefined') return;
  customCommandsLoaded = true;
  let list = [];
  try { list = await cc.commands(); } catch { return; }
  for (const c of list || []) {
    if (!c || !c.name || CHAT_COMMANDS.some(x => x.name === c.name)) continue;
    CHAT_COMMANDS.push({ name: c.name, args: '[arguments]', group: 'Custom', desc: c.description || 'Custom command', run: a => sendMessage(false, ('/' + c.name + ' ' + (a || '')).trim()) });
  }
}
document.addEventListener('input', ev => { if (ev.target && ev.target.id === 'chat-input' && ev.target.value.startsWith('/')) loadCustomCommands(); });

// ------------------------------------------------------------------ caveman mode (/cavemode)
// Terse answers that keep every fact, code, command, path, number and error exactly (github.com/juliusbrussee/caveman):
// fewer output tokens. full: the caveman voice; ultra: as compressed as it gets; wenyan: Classical Chinese (文言文).
// The server adds the rules to every reply of the chat; new chats keep the mode until it is turned off.
const CAVE_MODES = { full: 'Caveman', ultra: 'Ultracave', wenyan: 'Megacave (文言文)' };
function cavemanChipHtml() {
  const m = CHAT.settings && CHAT.settings.caveman;
  if (!m || !CAVE_MODES[m]) return '';
  return `<button class="chip-toggle on caveman-chip" data-act="caveman-off" data-tip="${esc(CAVE_MODES[m])} mode: terse answers that keep every fact, code, command and error exactly. Click to turn it off (/cavemode off).">🪨 ${esc(CAVE_MODES[m])}</button>`;
}
function setCaveman(arg) {
  const v = (arg || '').trim().toLowerCase();
  const cur = CHAT.settings.caveman || null;
  if (v === 'status' || v === '?') { toast(cur ? `Caveman mode: ${CAVE_MODES[cur]}` : 'Caveman mode is off. /cavemode on, ultra or wenyan turns it on.', 'ok'); return; }
  const map = { '': cur ? null : 'full', on: 'full', full: 'full', caveman: 'full', ultra: 'ultra', ultracave: 'ultra', wenyan: 'wenyan', megacave: 'wenyan', off: null, normal: null, stop: null };
  if (!(v in map)) { toast('Use /cavemode on | ultra | wenyan | off | status', 'bad'); return; }
  CHAT.settings.caveman = map[v];
  saveSettings();
  const bar = $('#composer-bar'); if (bar) bar.innerHTML = composerBarHtml();
  toast(CHAT.settings.caveman ? `${CAVE_MODES[CHAT.settings.caveman]} mode on: terse answers, code and facts kept exactly.` : 'Caveman mode off: normal answers.', 'ok');
}
ACTIONS['caveman-off'] = () => setCaveman('off');
if (typeof CHAT_COMMANDS !== 'undefined') {
  CHAT_COMMANDS.push(
    { name: 'cavemode', args: 'on | ultra | wenyan | off | status', group: 'Settings', desc: 'Caveman mode: terse answers that keep every fact and line of code (fewer tokens)', run: a => setCaveman(a) },
    { name: 'caveman', args: '[off | status]', group: 'Settings', desc: 'Caveman mode on (or off)', run: a => setCaveman(a ? a : 'on') },
    { name: 'ultracave', group: 'Settings', desc: 'Caveman mode, as compressed as it gets', run: () => setCaveman('ultra') },
    { name: 'megacave', group: 'Settings', desc: 'Caveman mode in Classical Chinese (文言文), the tersest', run: () => setCaveman('wenyan') });
}
// "stop caveman" or "normal mode" typed as a message turns it off too (like the original).
document.addEventListener('keydown', ev => {
  if (!ev.target || ev.target.id !== 'chat-input' || !CHAT.settings || !CHAT.settings.caveman || !keyIs(ev, 'chat.send')) return;
  const t = ev.target.value.trim().toLowerCase().replace(/[.!]+$/, '');
  if (t === 'stop caveman' || t === 'normal mode') { ev.preventDefault(); ev.stopImmediatePropagation(); ev.target.value = ''; setCaveman('off'); }
}, true);

// ------------------------------------------------------------------ tool limits (/limits)
// How many tool-call rounds (read/edit/run and the rest) and web searches the model may use per answer in this chat.
// Empty rounds = no fixed limit (the server checks progress instead); a number is a stop; 0 = tools off for the chat.
// Applies to this chat everywhere it is opened.
function limitDefaults() {
  const m = CHAT.meta || {};
  return { rounds: m.defaultMaxToolRounds, total: m.defaultMaxTotalToolRounds || 0, web: m.defaultMaxWebSearches, agents: m.defaultMaxParallelAgents };
}
function openLimits() {
  const s = CHAT.settings || {}, d = limitDefaults();
  const row = (k, label, cur, def, hint, max) => `<label>${label}
      <input name="${k}" type="number" min="${k === 'maxParallelAgents' ? 1 : 0}" max="${max || 10000}" step="1" value="${cur == null ? '' : cur}" placeholder="${esc(def)}">
      <span class="help">${hint}</span></label>`;
  const parallel = s.parallel !== false;
  openModal('Tool limits', `<form id="limits-form" class="stack" data-form="limits">
      <p class="muted small">How much the model may do in this chat per answer. Leave blank for the server default.</p>
      ${row('maxToolRounds', 'Tool-call rounds (read, edit, run commands, browser…)', s.maxToolRounds, d.total ? `default ${d.total}` : 'no limit',
        // Blank: no fixed count - the answer goes on while it gets things done (checked every few rounds), like Claude Code.
        `Blank: it keeps working while it gets things done (progress is checked every ${esc(String(d.rounds || 24))} rounds) until it finishes or you stop it. A number stops it there; 0 turns tools off.`)}
      ${row('maxWebSearches', 'Web searches', s.maxWebSearches, `default ${d.web == null ? '' : d.web}`, 'After this many the model answers from what it found.')}
      <label class="check-row"><input type="checkbox" name="parallel"${parallel ? ' checked' : ''}> <span><b>Parallel agents</b>
        <span class="help">The model splits work into subagents that run at the same time, and reads several files at once. Off: one step at a time.</span></span></label>
      ${row('maxParallelAgents', 'Subagents at once', s.maxParallelAgents, `default ${d.agents == null ? '' : d.agents}`, 'The most subagents one answer runs together (1–16).', 16)}</form>`,
    `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="submit-form" data-form-id="limits-form">Save</button>`);
}
FORMS['limits'] = (f, d) => {
  const num = v => { v = String(v == null ? '' : v).trim(); if (v === '') return null; const n = Math.round(Number(v)); return Number.isFinite(n) && n >= 0 ? n : null; };
  CHAT.settings.maxToolRounds = num(d.maxToolRounds);
  CHAT.settings.maxWebSearches = num(d.maxWebSearches);
  // On is the default, so only "off" is stored.
  CHAT.settings.parallel = f.querySelector('[name="parallel"]').checked ? null : false;
  const agents = num(d.maxParallelAgents);
  CHAT.settings.maxParallelAgents = agents == null ? null : Math.min(16, Math.max(1, agents));
  saveSettings();
  const bar = $('#composer-bar'); if (bar) bar.innerHTML = composerBarHtml();
  closeModal(); toast('Tool limits saved', 'ok');
};
function limitsChipHtml() {
  const s = CHAT.settings || {};
  const parts = [];
  if (s.maxToolRounds != null) parts.push(s.maxToolRounds === 0 ? 'no tools' : s.maxToolRounds + ' rounds');
  if (s.maxWebSearches != null) parts.push(s.maxWebSearches + ' searches');
  if (s.parallel === false) parts.push('one step at a time');
  else if (s.maxParallelAgents != null) parts.push(s.maxParallelAgents + ' agents at once');
  if (!parts.length) return '';
  return `<button class="chip-toggle on" data-act="open-limits" data-tip="Tool limits for this chat. Click to change (/limits).">${icon('bolt')} ${esc(parts.join(' · '))}</button>`;
}
ACTIONS['open-limits'] = () => openLimits();
if (typeof CHAT_COMMANDS !== 'undefined') {
  CHAT_COMMANDS.push({ name: 'limits', group: 'Settings', desc: 'Max tool-call rounds, web searches and parallel agents for this chat', run: () => openLimits() });
  CHAT_COMMANDS.push({ name: 'parallel', group: 'Settings', desc: 'Parallel agents: on, off, or how many subagents at once (e.g. /parallel 8)', run: arg => {
    const a = String(arg || '').trim().toLowerCase();
    if (!a) { openLimits(); return; }
    if (a === 'off' || a === 'no') CHAT.settings.parallel = false;
    else if (a === 'on' || a === 'yes') CHAT.settings.parallel = null;
    else if (/^\d+$/.test(a)) { CHAT.settings.parallel = null; CHAT.settings.maxParallelAgents = Math.min(16, Math.max(1, Number(a))); }
    else { toast('Use /parallel on, off, or a number of subagents', 'bad'); return; }
    saveSettings();
    const bar = $('#composer-bar'); if (bar) bar.innerHTML = composerBarHtml();
    toast(CHAT.settings.parallel === false ? 'Parallel agents off' : `Parallel agents on${CHAT.settings.maxParallelAgents ? ' · up to ' + CHAT.settings.maxParallelAgents + ' at once' : ''}`, 'ok');
  } });
  // Research notes: the server saves what an answer found on the web (research/<chat>.md and a download in the chat).
  CHAT_COMMANDS.push({ name: 'notes', group: 'Settings', desc: 'Research notes: save findings from web research to research/<chat>.md (on or off)', run: arg => {
    const a = String(arg || '').trim().toLowerCase();
    const on = CHAT.settings.researchNotes !== false;
    if (!a) { toast(`Research notes are ${on ? 'on' : 'off'} for this chat${CHAT.settings.researchNotesPath ? ': ' + CHAT.settings.researchNotesPath : ''}. Use /notes on or /notes off.`, 'ok'); return; }
    if (a === 'off' || a === 'no') CHAT.settings.researchNotes = false;
    else if (a === 'on' || a === 'yes') CHAT.settings.researchNotes = null;
    else { toast('Use /notes on or /notes off', 'bad'); return; }
    saveSettings();
    toast(CHAT.settings.researchNotes === false ? 'Research notes off for this chat' : 'Research notes on: findings go to research/<chat>.md', 'ok');
  } });
}
