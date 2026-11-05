'use strict';
/* Agentic console - Chat: more slash commands, after Claude Code's.
   /btw <question>      a side question about this chat, answered aside (no tools) and not added to the conversation
   /recap               one line: what this chat is about and where it stands
   /research <question> deep research: several searches, sources read and cross-checked, a cited report (web search on)
   /fork [title]        a copy of this chat to try another direction; the original stays as it is
   /loop [5m] <prompt>  repeats a prompt (or a /command) in this chat while the page is open; /loop lists, /loop stop [#|all]
   /notify [on|off]     system notifications when an answer finishes or a chat needs you while the window is in the background
   /schedule <when> <prompt> | list   a prompt the server sends to this chat on a schedule, also while no window is open
   /security-review, /simplify, /verify work on the project folder (desktop app, VS Code). */

const RECAP_QUESTION = 'Recap this session in one line of at most 30 words: what it is about and where it stands now (done, in progress, or blocked, and on what).';
const ASIDE = { question: '', answer: '' };

// ------------------------------------------------------------------ /btw and /recap
async function askAside(question, recap) {
  if (!needChat()) return;
  const q = (question || '').trim();
  if (!q) { toast('/btw <question>', 'bad'); return; }
  const conv = CHAT.conv;
  ASIDE.question = recap ? '' : q;
  ASIDE.answer = '';
  openModal(recap ? 'Recap' : 'By the way', `<div class="aside">${recap ? '' : `<div class="aside-q">${esc(q)}</div>`}
      <div class="md aside-a" id="aside-answer"><span class="muted">Thinking…</span></div>
      <p class="muted small aside-note">Answered from this chat without tools. Not added to the conversation.</p></div>`,
    `${recap ? '' : '<button class="btn" data-act="aside-to-chat" disabled>Ask in the chat</button>'}<button class="btn" data-act="aside-copy" disabled>Copy</button><button class="btn primary" data-act="modal-close">Close</button>`);
  try {
    const r = await post(`/api/chat/conversations/${enc(conv.id)}/aside`, { question: q, model: CHAT.model });
    ASIDE.answer = r.answer || '';
    const box = $('#aside-answer');
    if (!box) return;   // the dialog was closed meanwhile
    box.innerHTML = chatMarkdown(ASIDE.answer);
    applyRich(box);
    $$('[data-act="aside-copy"], [data-act="aside-to-chat"]').forEach(b => { b.disabled = false; });
  } catch (e) {
    const box = $('#aside-answer');
    if (box) box.innerHTML = `<div class="notice bad">${esc(e.message)}</div>`;
  }
}
ACTIONS['aside-copy'] = () => { if (ASIDE.answer) copyText(ASIDE.answer, 'Answer copied'); };
// The side question, asked properly: it goes into the message box, so the model can use its tools and the answer stays.
ACTIONS['aside-to-chat'] = () => {
  closeModal();
  const input = $('#chat-input');
  if (!input || !ASIDE.question) return;
  input.value = ASIDE.question;
  autosize();
  input.focus();
};

// ------------------------------------------------------------------ /research
function cmdResearch(a) {
  if (!CHAT.settings.webSearch) setToggle('webSearch', 'Web search', 'on');
  if (!CHAT.settings.webSearch) return;   // web search is not available here (setToggle said why)
  return sendMessage(false, a, { research: true });
}

// ------------------------------------------------------------------ /fork
async function cmdFork(a) {
  if (!needChat()) return;
  if (currentStream() || CHAT.live.has(CHAT.conv.id)) { toast('Wait until the answer is finished.', 'bad'); return; }
  const r = await post(`/api/chat/conversations/${enc(CHAT.conv.id)}/fork`, { title: a ? a.slice(0, 120) : undefined });
  refreshList();
  navigate('/chat/' + r.id);
  toast(`Forked into "${r.title}". The original chat is unchanged.`, 'ok');
}

// ------------------------------------------------------------------ /loop
const LOOPS = [];
let loopSeq = 0;
/** "30s", "5m", "2h" in milliseconds; 0 when it is not an interval. */
function loopInterval(s) {
  const m = /^(\d+)\s*(s|sec|m|min|h|hr)$/i.exec(s || '');
  if (!m) return 0;
  const n = Number(m[1]), unit = m[2][0].toLowerCase();
  return n * (unit === 's' ? 1000 : unit === 'm' ? 60000 : 3600000);
}
function loopEvery(ms) { return ms % 3600000 === 0 ? `${ms / 3600000}h` : ms % 60000 === 0 ? `${ms / 60000}m` : `${Math.round(ms / 1000)}s`; }
function cmdLoop(a) {
  const arg = (a || '').trim();
  if (!arg) { showLoops(); return; }
  const stop = /^stop\b\s*(.*)$/i.exec(arg);
  if (stop) {
    const which = stop[1].replace('#', '').trim();
    const gone = LOOPS.filter(l => !which || /^all$/i.test(which) || String(l.id) === which);
    if (!gone.length) { toast(which ? `There is no loop #${which}.` : 'No loops are running.', 'bad'); return; }
    gone.forEach(l => { clearInterval(l.timer); LOOPS.splice(LOOPS.indexOf(l), 1); });
    toast(gone.length === 1 ? `Loop #${gone[0].id} stopped` : `${gone.length} loops stopped`, 'ok');
    return;
  }
  if (!needChat()) return;
  const first = arg.split(/\s+/)[0];
  let ms = loopInterval(first);
  const prompt = (ms ? arg.slice(first.length) : arg).trim();
  if (!ms) ms = 10 * 60000;
  if (!prompt) { toast('/loop [30s | 5m | 2h] <prompt or /command>', 'bad'); return; }
  ms = Math.max(30000, Math.min(24 * 3600000, ms));
  const loop = { id: ++loopSeq, chatId: CHAT.conv.id, title: CHAT.conv.title || 'this chat', ms, prompt, next: Date.now() + ms, runs: 0 };
  loop.timer = setInterval(() => tickLoop(loop), 5000);
  LOOPS.push(loop);
  toast(`Loop #${loop.id}: every ${loopEvery(ms)} in this chat. /loop stop ${loop.id} ends it.`, 'ok');
  runLoop(loop);
}
/** Runs a loop when it is due, its chat is open, nothing is being written and the message box is empty (typing pauses it). */
function tickLoop(loop) {
  if (Date.now() < loop.next || !CHAT.conv || CHAT.conv.id !== loop.chatId) return;
  if (currentStream() || CHAT.live.has(loop.chatId)) return;
  const input = $('#chat-input');
  if (input && input.value.trim()) return;
  runLoop(loop);
}
async function runLoop(loop) {
  loop.next = Date.now() + loop.ms;
  loop.runs++;
  try {
    if (loop.prompt.startsWith('/') && typeof runChatCommand === 'function' && await runChatCommand(loop.prompt)) return;
    await sendMessage(false, loop.prompt);
  } catch (e) { toast(`Loop #${loop.id}: ${e.message}`, 'bad'); }
}
function showLoops() {
  if (!LOOPS.length) { toast('No loops are running. /loop [5m] <prompt> starts one.', 'ok'); return; }
  openModal('Loops', `<div class="cmd-help">${LOOPS.map(l => `<div class="cmd-row"><code>#${l.id} every ${esc(loopEvery(l.ms))}</code>
      <span>${esc(l.prompt)}<span class="muted small"> · ${esc(l.title)} · ran ${l.runs}× · next in ${Math.max(0, Math.round((l.next - Date.now()) / 1000))} s</span></span></div>`).join('')}
      <p class="muted small">Loops run while this page is open, when their chat is open and the message box is empty. <code>/loop stop [#|all]</code> ends them.</p></div>`,
    '<button class="btn primary" data-act="modal-close">Close</button>');
}

// ------------------------------------------------------------------ /schedule
// The server sends the prompt to this chat on a schedule, also while no window is open; the answer is in the chat afterwards.
const SCHEDULE_WHEN = /^(every\s+\d+\s*(?:m|min|mins|minutes?|h|hr|hrs|hours?|d|days?)|hourly|daily\s+\d{1,2}:\d{2}|weekdays\s+\d{1,2}:\d{2}|at\s+\d{1,2}:\d{2}|once\s+\d{4}-\d{2}-\d{2}\s+\d{1,2}:\d{2})\s+([\s\S]+)$/i;
const SCHEDULE_HELP = '/schedule every 30m | hourly | daily 09:00 | weekdays 08:30 | at 14:00 | once 2026-11-01 09:00 <prompt>';
function userTimeZone() { try { return Intl.DateTimeFormat().resolvedOptions().timeZone || ''; } catch { return ''; } }
async function cmdSchedule(a) {
  const arg = (a || '').trim();
  if (!arg || /^list$/i.test(arg)) { await showSchedules(/^list$/i.test(arg) || !CHAT.conv); return; }
  const m = SCHEDULE_WHEN.exec(arg);
  if (!m) { toast(SCHEDULE_HELP, 'bad'); return; }
  if (!needChat()) return;
  const r = await post('/api/chat/schedules', { chatId: CHAT.conv.id, when: m[1], prompt: m[2].trim(), timeZone: userTimeZone() });
  toast(`Scheduled (${r.when}): next ${new Date(r.nextRunUtc).toLocaleString()}. It runs even when this page is closed.`, 'ok');
}
let SCHEDULES_ALL = false;
async function showSchedules(all) {
  SCHEDULES_ALL = !!all;
  const list = await get('/api/chat/schedules' + (all || !CHAT.conv ? '' : '?chatId=' + enc(CHAT.conv.id)));
  const row = s => `<div class="cmd-row sched-row${s.enabled ? '' : ' off'}"><code>${esc(s.when)}</code>
      <span><b>${esc(String(s.prompt).slice(0, 160))}</b>
      <span class="muted small"><br>${all ? esc(s.chatTitle || s.chatId) + ' · ' : ''}${s.enabled ? 'next ' + esc(new Date(s.nextRunUtc).toLocaleString()) : 'paused'}${s.lastRunUtc ? ' · last ' + esc(ago(s.lastRunUtc)) + (s.lastStatus ? ' (' + esc(s.lastStatus) + ')' : '') : ''} · ran ${s.runs}×</span>
      <span class="sched-acts"><button class="btn xs" data-act="schedule-run" data-id="${esc(s.id)}">Run now</button>
      <button class="btn xs" data-act="schedule-toggle" data-id="${esc(s.id)}" data-on="${s.enabled ? '1' : ''}">${s.enabled ? 'Pause' : 'Resume'}</button>
      <button class="btn xs danger" data-act="schedule-delete" data-id="${esc(s.id)}">Delete</button></span></span></div>`;
  openModal(all ? 'Scheduled prompts' : 'Scheduled prompts in this chat',
    `<div class="cmd-help">${list.length ? list.map(row).join('') : '<p class="muted">None yet.</p>'}
      <p class="muted small">${esc(SCHEDULE_HELP)}. Times are in your time zone (${esc(userTimeZone() || 'UTC')}). They run on the server, also while no window is open.</p></div>`,
    `${all ? '' : '<button class="btn" data-act="schedule-all">All chats</button>'}<button class="btn primary" data-act="modal-close">Close</button>`);
}
ACTIONS['schedule-all'] = () => showSchedules(true);
ACTIONS['schedule-run'] = async el => { await post(`/api/chat/schedules/${enc(el.dataset.id)}/run`); toast('Started: the answer appears in its chat.', 'ok'); };
ACTIONS['schedule-toggle'] = async el => { await patch(`/api/chat/schedules/${enc(el.dataset.id)}`, { enabled: !el.dataset.on }); showSchedules(SCHEDULES_ALL); };
ACTIONS['schedule-delete'] = async el => { await api('DELETE', `/api/chat/schedules/${enc(el.dataset.id)}`); toast('Scheduled prompt deleted', 'ok'); showSchedules(SCHEDULES_ALL); };

// ------------------------------------------------------------------ prompt commands on the project folder
const PROMPTS_MORE = {
  security: 'Review my uncommitted changes for security problems (run git status and git diff, including staged changes): injection, path traversal, ' +
    'unsafe deserialization, secrets or keys in code, missing authentication or permission checks, unsafe handling of user input, weak cryptography ' +
    'and risky dependencies. For each problem give the file and line, why it matters and how to fix it, most serious first. Do not change any files.',
  simplify: 'Look at my uncommitted changes (git status, git diff) and simplify them without changing what they do: remove duplication and dead code, ' +
    'use helpers that already exist in the project, choose clearer names, flatten needless nesting and match the style of the code around them. ' +
    'Make the edits, build or run the tests if the project has them, then list briefly what you changed and why.',
  verify: 'Check that my latest changes actually work. Find out how this project is built, run and tested (README, package and build files, scripts), ' +
    'build it, run the tests that cover the change, and if it is an app, start it and try the changed feature. Report exactly what you ran, what ' +
    'passed and what failed with the exact errors. Do not fix anything unless I ask; when something fails, say what you think causes it.'
};

if (typeof CHAT_COMMANDS !== 'undefined') {
  CHAT_COMMANDS.push(
    { name: 'btw', args: '<question>', group: 'Chat', desc: 'A side question about this chat: answered aside, not added to the conversation', required: true, run: a => askAside(a) },
    { name: 'recap', group: 'Chat', desc: 'One line: what this chat is about and where it stands', run: () => askAside(RECAP_QUESTION, true) },
    { name: 'fork', args: '[title]', group: 'Chat', desc: 'Copy this chat to try another direction; the original stays as it is', run: cmdFork },
    { name: 'research', args: '<question>', group: 'Work', desc: 'Deep research: several searches, sources read and cross-checked, a cited report', required: true, run: cmdResearch },
    { name: 'loop', args: '[5m] <prompt> | stop [#|all]', group: 'Work', desc: 'Repeat a prompt or /command in this chat every few minutes while the page is open', run: cmdLoop },
    { name: 'security-review', group: 'Work', desc: 'Review your uncommitted changes for security problems', run: () => needTools('security-review') && sendMessage(false, PROMPTS_MORE.security) },
    { name: 'simplify', group: 'Work', desc: 'Simplify your uncommitted changes without changing what they do', run: () => needTools('simplify') && sendMessage(false, PROMPTS_MORE.simplify) },
    { name: 'verify', group: 'Work', desc: 'Build, test and run the project to check that the latest changes work', run: () => needTools('verify') && sendMessage(false, PROMPTS_MORE.verify) },
    { name: 'schedule', args: '<when> <prompt> | list', group: 'Work', desc: 'Send a prompt to this chat on a schedule (every 30m, daily 09:00 …), also while no window is open', run: cmdSchedule },
    { name: 'notify', args: '[on | off]', group: 'Settings', desc: 'System notifications when an answer finishes or a chat needs you (window in the background)', run: a => setNotify(onOff(a, notifyEnabled())) });
}
