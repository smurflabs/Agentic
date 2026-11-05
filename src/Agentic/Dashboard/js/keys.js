'use strict';
/* Agentic console - keyboard shortcuts. Every shortcut of the app is listed here and can be changed in Keyboard shortcuts
   (user menu, command palette, Ctrl+/ by default). A shortcut is a chord like "Ctrl+Shift+O" (Ctrl also means ⌘ on a Mac)
   or two chords pressed one right after the other ("Esc Esc"). Letters, digits and punctuation are named by their place on
   the keyboard, so Shift does not change them ("Ctrl+Shift+;"); a shortcut also matches the character a key types, so "/"
   works on keyboards where it is Shift+7.
   The account keeps only the shortcuts the user changed (/api/account/keybindings): the same ones in every browser, the
   desktop app and VS Code. This browser keeps a copy, so they work before the server answers (and with older servers). */

const KEY_ACTIONS = [
  { id: 'app.palette', group: 'Everywhere', ctx: 'app', label: 'Command palette', keys: ['Ctrl+K'] },
  { id: 'app.search', group: 'Everywhere', ctx: 'app', label: 'Search (outside text boxes)', keys: ['/'] },
  { id: 'app.shortcuts', group: 'Everywhere', ctx: 'app', label: 'Keyboard shortcuts', keys: ['Ctrl+/'] },
  { id: 'app.close', group: 'Everywhere', ctx: 'app', label: 'Close a dialog or menu', keys: ['Esc'] },
  { id: 'chat.send', group: 'Message box', ctx: 'composer', label: 'Send', keys: ['Enter', 'Ctrl+Enter'] },
  { id: 'chat.newline', group: 'Message box', ctx: 'composer', label: 'New line', keys: ['Shift+Enter'] },
  { id: 'chat.stop', group: 'Message box', ctx: 'composer', label: 'Stop the answer', keys: ['Esc'] },
  { id: 'chat.mode', group: 'Message box', ctx: 'composer', label: 'Next permission mode (Manual, Accept edits, Auto)', keys: ['Shift+Tab'] },
  { id: 'chat.editLast', group: 'Message box', ctx: 'composer', label: 'Edit your last message (in an empty box)', keys: ['Up'] },
  { id: 'chat.rewind', group: 'Message box', ctx: 'composer', label: 'Go back to an earlier message (in an empty box)', keys: ['Esc Esc'] },
  { id: 'chat.cancelEdit', group: 'Message box', ctx: 'edit', label: 'Cancel editing a message', keys: ['Esc'] },
  { id: 'chat.new', group: 'Chat', ctx: 'chat', label: 'New chat', keys: ['Ctrl+Shift+O'] },
  { id: 'chat.focus', group: 'Chat', ctx: 'chat', label: 'Focus the message box', keys: ['Shift+Esc'] },
  { id: 'chat.copyAnswer', group: 'Chat', ctx: 'chat', label: 'Copy the last answer', keys: ['Ctrl+Shift+C'] },
  { id: 'chat.copyCode', group: 'Chat', ctx: 'chat', label: 'Copy the last code block', keys: ['Ctrl+Shift+;'] },
  { id: 'chat.toggleList', group: 'Chat', ctx: 'chat', label: 'Show or hide the chat list', keys: ['Ctrl+Shift+S'] },
  { id: 'chat.personalize', group: 'Chat', ctx: 'chat', label: 'Personalization', keys: ['Ctrl+Shift+I'] },
  { id: 'chat.delete', group: 'Chat', ctx: 'chat', label: 'Delete this chat', keys: ['Ctrl+Shift+Backspace'] },
  { id: 'chat.multitask', group: 'Chat', ctx: 'chat', label: 'Multitask on or off', keys: ['Alt+Shift+M'] },
  ...[1, 2, 3, 4].map(n => ({ id: 'chat.pane' + n, group: 'Chat', ctx: 'chat', label: `Go to pane ${n} (multitask)`, keys: ['Alt+' + n] })),
  ...[1, 2, 3, 4, 5].map(n => ({ id: 'chat.answer' + n, group: 'When the model asks you', ctx: 'ask', label: `Pick answer ${n}`, keys: [String(n)] })),
  { id: 'list.next', group: 'Suggestions and pickers', ctx: 'list', label: 'Next', keys: ['Down'] },
  { id: 'list.prev', group: 'Suggestions and pickers', ctx: 'list', label: 'Previous', keys: ['Up'] },
  { id: 'list.pick', group: 'Suggestions and pickers', ctx: 'list', label: 'Pick', keys: ['Enter', 'Tab'] },
  { id: 'list.close', group: 'Suggestions and pickers', ctx: 'list', label: 'Close', keys: ['Esc'] }
];
const KEYS = { overrides: {}, map: {}, seq: { prev: null, last: null }, capture: null, synced: false };
const KEY_MAX = 4;
const KEY_SEQ_MS = 800;
const KEY_MODS = new Set(['Control', 'Shift', 'Alt', 'Meta', 'AltGraph', 'CapsLock', 'OS', 'Fn', 'Hyper', 'Super']);
const KEY_CODE_NAMES = {
  Semicolon: ';', Equal: '=', Comma: ',', Minus: '-', Period: '.', Slash: '/', Backquote: '`', BracketLeft: '[', Backslash: '\\',
  BracketRight: ']', Quote: "'", IntlBackslash: '\\', Space: 'Space', NumpadAdd: 'NumPlus', NumpadSubtract: 'NumMinus',
  NumpadMultiply: 'NumMultiply', NumpadDivide: 'NumDivide', NumpadDecimal: 'NumDecimal', NumpadEnter: 'Enter'
};
const KEY_NAMES = { Escape: 'Esc', ArrowUp: 'Up', ArrowDown: 'Down', ArrowLeft: 'Left', ArrowRight: 'Right', ' ': 'Space', Del: 'Delete', '+': 'Plus' };

/** Registers a shortcut of a layer (the desktop app, VS Code) so it is listed and can be changed too. */
function registerKeyAction(a) {
  if (!a || !a.id || KEY_ACTIONS.some(x => x.id === a.id)) return;
  KEY_ACTIONS.push(Object.assign({ group: 'More', ctx: 'app', keys: [] }, a));
  keyRebuild();
}
function keyAction(id) { return KEY_ACTIONS.find(a => a.id === id); }
function keyRebuild() {
  KEYS.map = {};
  for (const a of KEY_ACTIONS) KEYS.map[a.id] = (Object.prototype.hasOwnProperty.call(KEYS.overrides, a.id) ? KEYS.overrides[a.id] : a.keys).slice(0, KEY_MAX);
}
/** The shortcuts of an action (what the user chose, else the default). */
function keysOf(id) { return KEYS.map[id] || (keyAction(id) || { keys: [] }).keys; }
/** Its first shortcut for labels ("Ctrl+Shift+I"), '' when it has none. */
function keyLabel(id) { return keysOf(id)[0] || ''; }
/** " (Ctrl+Shift+I)" for tooltips, '' when the action has no shortcut. */
function keyTip(id) { const k = keyLabel(id); return k ? ` (${k})` : ''; }

// ------------------------------------------------------------------ what was pressed
/** The key by its place on the keyboard: "O", "1", ";", "Enter", "Esc", "Up", "F2", "Num1". */
function keyName(ev) {
  const c = ev.code || '';
  let m;
  if ((m = /^Key([A-Z])$/.exec(c))) return m[1];
  if ((m = /^Digit(\d)$/.exec(c))) return m[1];
  if ((m = /^Numpad(\d)$/.exec(c))) return 'Num' + m[1];
  if (KEY_CODE_NAMES[c]) return KEY_CODE_NAMES[c];
  const k = ev.key || '';
  if (KEY_NAMES[k]) return KEY_NAMES[k];
  return k.length === 1 ? k.toUpperCase() : k;
}
function keyMods(ev, shift) { return (ev.ctrlKey || ev.metaKey ? 'Ctrl+' : '') + (ev.altKey ? 'Alt+' : '') + (shift && ev.shiftKey ? 'Shift+' : ''); }
/** The chord pressed, by key position: "Ctrl+Shift+O". '' for a modifier on its own. */
function keyChord(ev) { return !ev || KEY_MODS.has(ev.key) ? '' : keyMods(ev, true) + keyName(ev); }
/** The same press by the character it types ("/" from Shift+7 on a German keyboard); '' when that is the same key. */
function keyChordTyped(ev) {
  const k = ev && ev.key || '';
  if (k.length !== 1 || k === ' ' || /^Key[A-Z]$/.test(ev.code || '')) return '';
  const name = KEY_NAMES[k] || k.toUpperCase();
  return name === keyName(ev) ? '' : keyMods(ev, false) + name;
}
// Remembers the last two chords, for two-chord shortcuts like "Esc Esc" (before any other handler sees the key).
window.addEventListener('keydown', ev => {
  if (KEY_MODS.has(ev.key) || KEYS.capture) return;
  KEYS.seq.prev = KEYS.seq.last;
  KEYS.seq.last = { a: keyChord(ev), b: keyChordTyped(ev), t: Date.now() };
}, true);
function keyMatches(ev, combo) {
  const parts = String(combo).trim().split(/\s+/);
  const a = keyChord(ev), b = keyChordTyped(ev), hit = c => c === a || (!!b && c === b);
  if (parts.length === 1) return hit(parts[0]);
  if (parts.length !== 2 || ev.repeat || !hit(parts[1])) return false;
  const p = KEYS.seq.prev, l = KEYS.seq.last;
  return !!(p && l && (p.a === parts[0] || p.b === parts[0]) && l.t - p.t < KEY_SEQ_MS);
}
/** Whether this key press is one of the action's shortcuts. */
function keyIs(ev, id) {
  if (!ev || KEYS.capture) return false;
  for (const k of keysOf(id)) {
    if (!keyMatches(ev, k)) continue;
    if (/\s/.test(k.trim())) KEYS.seq.last = null;   // a pair is used up: a third press starts a new one
    return true;
  }
  return false;
}
function keyTextTarget(t) { return !!(t && (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName))); }
/** A plain character typed into a text box: page shortcuts never take it (only shortcuts with Ctrl or Alt, or special keys). */
function keyPlainTyping(ev) {
  return !!ev && !ev.ctrlKey && !ev.metaKey && !ev.altKey && (ev.key || '').length === 1 && keyTextTarget(ev.target);
}
/** Plain Enter and Shift+Enter add a line in a text box by themselves; other new-line shortcuts add it here. */
function keyNewline(ev) {
  if (ev.key === 'Enter' && !ev.ctrlKey && !ev.metaKey && !ev.altKey) return;
  ev.preventDefault();
  const ta = ev.target;
  if (!ta || typeof ta.setRangeText !== 'function') return;
  ta.setRangeText('\n', ta.selectionStart, ta.selectionEnd, 'end');
  ta.dispatchEvent(new Event('input', { bubbles: true }));
}

// ------------------------------------------------------------------ saved shortcuts
function keyLoadLocal() {
  try { const o = JSON.parse(localStorage.getItem('keybindings') || '{}'); KEYS.overrides = o && typeof o === 'object' && !Array.isArray(o) ? o : {}; }
  catch { KEYS.overrides = {}; }
  keyRebuild();
}
function keyStoreLocal() { try { localStorage.setItem('keybindings', JSON.stringify(KEYS.overrides)); } catch { /* private mode: this page only */ } }
/** Takes the account's shortcuts from the server (another browser or app may have changed them). */
async function keySync() {
  if (typeof IN_PANE !== 'undefined' && IN_PANE) return;   // panes follow the page around them (storage event)
  try {
    const r = await get('/api/account/keybindings');
    const server = r && r.bindings && typeof r.bindings === 'object' ? r.bindings : {};
    KEYS.synced = true;
    if (JSON.stringify(server) === JSON.stringify(KEYS.overrides)) return;
    KEYS.overrides = server;
    keyStoreLocal(); keyRebuild(); keyHints();
  } catch { /* an older server or offline: this browser's copy stays */ }
}
async function keySave() {
  keyStoreLocal(); keyRebuild(); keyHints();
  try { await put('/api/account/keybindings', { bindings: KEYS.overrides }); return true; }
  catch (e) {
    if (e.status === 404) toast('Saved in this browser only: this server does not keep shortcuts yet.', 'warn');
    else if (e.status !== 401) toast(e.message, 'bad');
    return false;
  }
}
function keySet(id, list) {
  const a = keyAction(id); if (!a) return;
  list = [...new Set(list.filter(Boolean))].slice(0, KEY_MAX);
  if (list.join('\n') === a.keys.join('\n')) delete KEYS.overrides[id]; else KEYS.overrides[id] = list;
  keyRebuild();
}
// Another tab, window or multitask pane saved shortcuts: use them here too.
window.addEventListener('storage', ev => { if (ev.key === 'keybindings') { keyLoadLocal(); keyHints(); if (keysDialogOpen()) keysRedraw(); } });

/** Shortcut hints shown in the page (the top bar's palette button, the user menu). */
function keyHints() {
  const kbds = combo => combo ? combo.split(/\s+/).map(ch => ch.split('+').map(p => `<kbd>${esc(p)}</kbd>`).join('')).join(' ') : '';
  $$('.kbd-hint[data-act="open-palette"]').forEach(b => { const k = keyLabel('app.palette'); b.innerHTML = kbds(k); b.classList.toggle('hidden', !k); b.title = 'Command palette' + keyTip('app.palette'); });
  $$('[data-key-hint]').forEach(el => { const k = keyLabel(el.dataset.keyHint); el.innerHTML = kbds(k); el.classList.toggle('hidden', !k); });
}

// ------------------------------------------------------------------ the Keyboard shortcuts dialog
function keysDialogOpen() { return !!$('#modal-card .keys-dialog'); }
function keyKbd(combo) {
  return combo.split(/\s+/).map(ch => ch.split('+').map(p => `<kbd>${esc(p)}</kbd>`).join('<i>+</i>')).join('<i class="key-then">then</i>');
}
function keyRowHtml(a) {
  const keys = keysOf(a.id), changed = Object.prototype.hasOwnProperty.call(KEYS.overrides, a.id), cap = KEYS.capture && KEYS.capture.id === a.id;
  const shown = keys.length ? keys.map(k => `<span class="key-combo">${keyKbd(k)}</span>`).join('<i class="key-or">or</i>') : '<span class="muted small">none</span>';
  const body = cap
    ? `<span class="key-capture">${KEYS.capture.first ? keyKbd(KEYS.capture.first) + ' …' : 'Press the new shortcut…'}</span>
       <button class="btn small ghost" data-act="key-none" data-id="${esc(a.id)}">None</button><button class="btn small ghost" data-act="key-cancel">Cancel</button>`
    : `<button type="button" class="key-btn" data-act="key-change" data-id="${esc(a.id)}" data-tip="Change this shortcut">${shown}</button>
       <button type="button" class="btn icon ghost small" data-act="key-add" data-id="${esc(a.id)}" aria-label="Add a shortcut" data-tip="Add another shortcut">${icon('plus')}</button>
       ${changed ? `<button type="button" class="btn small ghost" data-act="key-reset" data-id="${esc(a.id)}" data-tip="Back to ${esc(a.keys.join(' or ') || 'none')}">Reset</button>` : ''}`;
  return `<div class="kbd-row key-row${changed ? ' changed' : ''}${cap ? ' capturing' : ''}" data-key-id="${esc(a.id)}"><span>${esc(a.label)}</span><span class="keys">${body}</span></div>`;
}
function keysBodyHtml() {
  const groups = [...new Set(KEY_ACTIONS.map(a => a.group))];
  return `<div class="keys-dialog"><p class="muted small">Click a shortcut to change it, or <b>+</b> to add another. Two keys pressed one after the other
    (like Esc Esc) also work. Saved to your account: the same in every browser, the desktop app and VS Code. Ctrl is ⌘ on a Mac.</p>` +
    groups.map(g => `<div class="menu-title key-group">${esc(g)}</div><div class="kbd-list">${KEY_ACTIONS.filter(a => a.group === g).map(keyRowHtml).join('')}</div>`).join('') + '</div>';
}
function openKeys() {
  KEYS.capture = null;
  openModal('Keyboard shortcuts', keysBodyHtml(), `<button class="btn" data-act="keys-reset-all">Reset all</button><span class="spacer"></span><button class="btn primary" data-act="modal-close">Done</button>`, 'keys-modal');
}
function keysRedraw() {
  const body = $('#modal-card .modal-body'); if (!body || !keysDialogOpen()) return;
  const top = body.scrollTop;
  body.innerHTML = keysBodyHtml();
  body.scrollTop = top;
}
ACTIONS['keys-open'] = () => { $$('.menu').forEach(m => m.classList.add('hidden')); openKeys(); };
ACTIONS['chat-shortcuts'] = ACTIONS['keys-open'];
ACTIONS['key-change'] = el => keyCaptureStart(el.dataset.id, false);
ACTIONS['key-add'] = el => keyCaptureStart(el.dataset.id, true);
ACTIONS['key-cancel'] = () => { keyCaptureStop(); keysRedraw(); };
ACTIONS['key-none'] = async el => { keyCaptureStop(); keySet(el.dataset.id, []); await keySave(); keysRedraw(); };
ACTIONS['key-reset'] = async el => { const a = keyAction(el.dataset.id); if (!a) return; delete KEYS.overrides[a.id]; keyRebuild(); await keySave(); keysRedraw(); };
ACTIONS['keys-reset-all'] = async () => {
  const ok = await confirmBox('Reset all shortcuts', 'Every shortcut goes back to its default.', { confirm: 'Reset all', danger: true });
  if (ok) { KEYS.overrides = {}; keyRebuild(); await keySave(); }
  openKeys();
};
function keyCaptureStart(id, add) {
  keyCaptureStop();
  KEYS.capture = { id, add, first: null, at: 0, timer: 0 };
  keysRedraw();
}
function keyCaptureStop() { if (KEYS.capture) clearTimeout(KEYS.capture.timer); KEYS.capture = null; }
// While a shortcut is being recorded, every key goes to the recorder (nothing else reacts to it).
window.addEventListener('keydown', ev => {
  const c = KEYS.capture;
  if (!c || KEY_MODS.has(ev.key)) return;
  ev.preventDefault(); ev.stopImmediatePropagation();
  if (ev.repeat) return;
  const chord = keyChord(ev);
  if (!chord) return;
  if (c.first && Date.now() - c.at < KEY_SEQ_MS) { clearTimeout(c.timer); keyCaptured(c.first + ' ' + chord); return; }
  c.first = chord; c.at = Date.now();
  keysRedraw();
  c.timer = setTimeout(() => keyCaptured(c.first), KEY_SEQ_MS);
}, true);
// Key-ups of a recorded shortcut must not reach the page either (e.g. Shift's key-up shows the selection menu in a chat).
window.addEventListener('keyup', ev => { if (KEYS.capture) ev.stopImmediatePropagation(); }, true);
async function keyCaptured(combo) {
  const c = KEYS.capture; if (!c) return;
  keyCaptureStop();
  const a = keyAction(c.id); if (!a) return;
  // In text boxes a plain character must stay a character (answers 1-5 are picked outside text boxes).
  const first = combo.split(' ')[0];
  if (['composer', 'edit', 'list'].includes(a.ctx) && /^(Shift\+)?(.|Space)$/.test(first)) {
    toast('Choose a key with Ctrl or Alt, or a key like Enter, Tab or F2: a plain character would be typed into the box.', 'warn');
    keysRedraw(); return;
  }
  const clash = KEY_ACTIONS.filter(o => o.id !== a.id && o.ctx === a.ctx && keysOf(o.id).includes(combo));
  if (clash.length) {
    const top = $('#modal-card .modal-body')?.scrollTop || 0;
    const ok = await confirmBox('Shortcut already used', `<b>${esc(combo)}</b> is used for ${clash.map(o => `“${esc(o.label)}”`).join(' and ')}. Use it for “${esc(a.label)}” instead?`, { confirm: 'Use it here' });
    openKeys();
    const body = $('#modal-card .modal-body'); if (body) body.scrollTop = top;
    if (!ok) return;
    for (const o of clash) keySet(o.id, keysOf(o.id).filter(k => k !== combo));
  }
  keySet(a.id, c.add ? [...keysOf(a.id).filter(k => k !== combo), combo] : [combo]);
  await keySave();
  keysRedraw();
}

keyLoadLocal();
if (typeof S !== 'undefined' && S.me) keySync();
