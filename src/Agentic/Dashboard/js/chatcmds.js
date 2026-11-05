'use strict';
/* Agentic console - Chat slash commands: type / in the message box. Built-in commands control the chat (goal, plan,
   model, effort, tools, …) and run here instead of being sent. The picker lists them first, then saved prompts (and, in
   the desktop app, its skills and commands). Also the goal bar above the message box.
   /goal <text>  sets a goal: the model keeps working until it reports the goal met (goal_complete), and failed model
                 calls are retried (5 times, 120 s apart by default; Chat.GoalRetries / GoalRetrySeconds on the server).
   /plan <task>  plans this message only: read-only tools, then the model asks you to approve its plan. */

const GOAL_ICON = '<svg viewBox="0 0 24 24" class="ic"><circle cx="12" cy="12" r="8"/><circle cx="12" cy="12" r="4"/><circle cx="12" cy="12" r=".6"/></svg>';
const onOff = (arg, current) => /^(on|yes|true|1)$/i.test(arg || '') ? true : /^(off|no|false|0)$/i.test(arg || '') ? false : !current;
const needChat = () => { if (CHAT.conv) return true; toast('Start a conversation first.', 'bad'); return false; };
function redrawComposerBar() { const bar = $('#composer-bar'); if (bar) bar.innerHTML = composerBarHtml(); }
function setToggle(key, label, arg) {
  const btn = $(`#composer-bar [data-key="${key}"]`);
  if (!btn || btn.disabled) { toast(`${label} is not available here.`, 'bad'); return; }
  const want = onOff(arg, CHAT.settings[key]);
  if (!!CHAT.settings[key] !== want) ACTIONS['chat-toggle'](btn);
  toast(`${label} ${want ? 'on' : 'off'}`, 'ok');
}
function findModel(q) {
  const all = [...CHAT.meta.customModels.map(m => ({ id: m.id, name: m.name || m.id })), ...(CHAT.meta.providerModels || []).map(m => ({ id: m.id, name: m.name || m.id }))];
  const s = q.toLowerCase();
  return all.find(m => m.id.toLowerCase() === s || m.name.toLowerCase() === s) || all.find(m => m.id.toLowerCase().startsWith(s) || m.name.toLowerCase().startsWith(s)) ||
    all.find(m => m.id.toLowerCase().includes(s) || m.name.toLowerCase().includes(s));
}

const CHAT_COMMANDS = [
  { name: 'goal', args: '<what to achieve> | clear', group: 'Work', desc: 'Set a goal: the model keeps working, and retries when the model fails, until the goal is met', run: cmdGoal },
  { name: 'plan', args: '<task>', group: 'Work', desc: 'Plan first: the model looks around read-only, writes a plan and asks you to approve it', run: a => sendMessage(false, a || 'Make a plan for what we discussed.', { plan: true }) },
  { name: 'search', args: '<question>', group: 'Work', desc: 'Ask with web search switched on', required: true, run: a => { if (!CHAT.settings.webSearch) setToggle('webSearch', 'Web search', 'on'); return sendMessage(false, a); } },
  { name: 'retry', group: 'Work', desc: 'Answer the last message again (a failed answer picks up where it stopped)', run: () => ACTIONS['msg-retry']() },
  { name: 'continue', group: 'Work', desc: 'Continue the last answer where it stopped', run: () => ACTIONS['msg-continue']() },
  { name: 'stop', group: 'Work', desc: 'Stop the answer being written', run: () => { if (currentStream()) stopStreaming(); else if (CHAT.conv && CHAT.live.has(CHAT.conv.id)) stopOnServer(CHAT.conv.id); else toast('Nothing is being written.', 'bad'); } },
  { name: 'model', args: '[name]', group: 'Settings', desc: 'Switch the model (no name: open the model list)', run: cmdModel },
  { name: 'effort', args: 'none | low | medium | high | ultracode', group: 'Settings', desc: 'Reasoning effort', run: a => cmdCombo('effort', a) },
  { name: 'mode', args: 'auto | acceptEdits | manual', group: 'Settings', desc: 'Permission mode: ask before changes, only before commands, or never', run: a => cmdCombo('mode', a) },
  { name: 'web', args: '[on | off]', group: 'Settings', desc: 'Web search', run: a => setToggle('webSearch', 'Web search', a) },
  { name: 'code', args: '[on | off]', group: 'Settings', desc: 'Code interpreter', when: () => !!(CHAT.meta && CHAT.meta.canTools), run: a => setToggle('codeInterpreter', 'Code interpreter', a) },
  { name: 'image', args: '[on | off]', group: 'Settings', desc: 'Image generation', run: a => setToggle('imageGeneration', 'Image', a) },
  { name: 'video', args: '[on | off]', group: 'Settings', desc: 'Video generation', run: a => setToggle('videoGeneration', 'Video', a) },
  { name: 'money', args: '[on | off]', group: 'Settings', desc: 'Money: live crypto prices and exchange rates', when: () => !!(CHAT.meta && CHAT.meta.markets), run: a => setToggle('markets', 'Money', a) },
  { name: 'stocks', args: '[on | off]', group: 'Settings', desc: 'Stocks: stock and index quotes for any ticker', when: () => !!(CHAT.meta && CHAT.meta.stocks), run: a => setToggle('stocks', 'Stocks', a) },
  { name: 'polymarket', args: '[on | off]', group: 'Settings', desc: 'Polymarket: prediction market odds, live prices, trades', when: () => !!(CHAT.meta && CHAT.meta.polymarket), run: a => setToggle('polymarket', 'Polymarket', a) },
  { name: 'news', args: '[on | off]', group: 'Settings', desc: 'News: search the latest articles from the news feeds', when: () => !!(CHAT.meta && CHAT.meta.news), run: a => setToggle('news', 'News', a) },
  { name: 'instructions', args: '[text | clear]', group: 'Settings', desc: 'Instructions for this chat (no text: open the editor)', run: cmdInstructions },
  { name: 'temperature', args: '<0-2> | default', group: 'Settings', desc: 'Sampling temperature for this chat', required: true, run: cmdTemperature },
  { name: 'new', group: 'Chat', desc: 'Start a new chat', run: () => navigate('/chat') },
  { name: 'temp', group: 'Chat', desc: 'Start a temporary chat: not saved in history, no memory', run: () => { if (CHAT.conv) navigate('/chat'); setTimeout(() => { if (!CHAT.temporary) ACTIONS['chat-temp'](); }, CHAT.conv ? 400 : 0); } },
  { name: 'rename', args: '[title]', group: 'Chat', desc: 'Rename this chat', run: cmdRename },
  { name: 'pin', group: 'Chat', desc: 'Pin or unpin this chat', run: () => needChat() && ACTIONS['chat-pin']() },
  { name: 'compact', group: 'Chat', desc: 'Summarize older messages to free context', run: () => ACTIONS['chat-compact']() },
  { name: 'copy', group: 'Chat', desc: 'Copy the last answer', run: () => { const m = lastAnswer(); if (m) copyText(m.content, 'Last answer copied'); else toast('There is no answer yet.', 'bad'); } },
  { name: 'export', args: '[md | json]', group: 'Chat', desc: 'Download this chat', run: a => needChat() && ACTIONS['chat-export']({ dataset: { format: /json/i.test(a) ? 'json' : 'md' } }) },
  { name: 'share', group: 'Chat', desc: 'Share a read-only link', run: () => { if (needChat()) $('#chat-head [data-act="chat-share"]')?.click(); } },
  { name: 'delete', group: 'Chat', desc: 'Delete this chat', run: () => needChat() && ACTIONS['chat-delete']({ dataset: { id: CHAT.conv.id } }) },
  { name: 'memory', group: 'More', desc: 'What the assistant knows about you', run: () => ACTIONS['chat-personalize']() },
  { name: 'prompts', group: 'More', desc: 'Manage saved prompts', run: () => ACTIONS['prompts-manage']() },
  { name: 'multitask', group: 'More', desc: 'Up to 4 chats side by side' + keyTip('chat.multitask'), run: () => { if (IN_PANE) { try { window.parent.mtKey('toggle'); } catch { /* closed */ } } else mtEnter(); } },
  { name: 'shortcuts', group: 'More', desc: 'See and change every keyboard shortcut' + keyTip('app.shortcuts'), run: () => ACTIONS['keys-open']() },
  { name: 'help', group: 'More', desc: 'All commands', run: () => commandsHelp() }
];

/**
 * In a Claude Code chat only these run here (they act on the page); every other /command - /compact, /review, /init, your
 * own commands and skills - goes to Claude Code as typed.
 */
const EXTERNAL_COMMANDS = new Set(['plan', 'model', 'stop', 'new', 'temp', 'rename', 'pin', 'copy', 'export', 'share', 'delete', 'prompts', 'multitask', 'help']);
const externalChat = () => typeof isExternal === 'function' && isExternal(CHAT.model);
/** Built-in commands matching what follows the / (for the pickers). */
function chatCommandHits(q) {
  const s = (q || '').toLowerCase();
  // A command may hide itself (e.g. /delegate when the user has no agent permission).
  const base = CHAT_COMMANDS.filter(c => !c.when || c.when());
  const list = externalChat() ? base.filter(c => EXTERNAL_COMMANDS.has(c.name)) : base;
  return list.filter(c => c.name.startsWith(s)).concat(s ? list.filter(c => !c.name.startsWith(s) && c.desc.toLowerCase().includes(s)) : []);
}
/** Runs "/name args" when name is a built-in command; false otherwise (it is then sent as written). */
async function runChatCommand(text) {
  const m = /^\/([\w-]+)(?:\s+([\s\S]*))?$/.exec(text.trim());
  const cmd = m && CHAT_COMMANDS.find(c => c.name === m[1].toLowerCase());
  if (!cmd) return false;
  if (externalChat() && !EXTERNAL_COMMANDS.has(cmd.name)) return false;
  const arg = (m[2] || '').trim();
  if (cmd.required && !arg) { toast(`/${cmd.name} ${cmd.args}`, 'bad'); return true; }
  const input = $('#chat-input');
  if (input && input.value.trim() === text.trim()) { input.value = ''; autosize(); renderContext(); }
  try { await cmd.run(arg); } catch (e) { toast(e.message, 'bad'); }
  return true;
}
window.chatCommandHits = chatCommandHits;
window.runChatCommand = runChatCommand;

function cmdModel(a) {
  // In a Claude Code chat, /model sets Claude's model (opus, sonnet, haiku, fable or a full model name).
  if (a && externalChat() && window.chatClient.setExternalOption) { window.chatClient.setExternalOption(CHAT, 'model', a); toast(`Claude Code model: ${a}`, 'ok'); return; }
  if (!a) { $('#model-picker .model-btn')?.click(); return; }
  const m = findModel(a);
  if (!m) { toast(`No model matches "${a}".`, 'bad'); return; }
  return ACTIONS['model-pick']({ dataset: { id: m.id } }).then(() => toast(`Model: ${m.name}`, 'ok'));
}
function cmdCombo(kind, a) {
  const list = kind === 'effort' ? EFFORTS : MODES, v = (a || '').toLowerCase();
  const hit = list.find(x => x[0].toLowerCase() === v || x[1].toLowerCase() === v) || (v && list.find(x => x[0].toLowerCase().startsWith(v.replace(/[\s_-]/g, ''))));
  if (!hit) { toast(`/${kind} ${list.map(x => x[0]).join(' | ')}`, 'bad'); return; }
  if (kind === 'effort') CHAT.settings.reasoningEffort = hit[0]; else CHAT.settings.permissionMode = hit[0];
  saveSettings(); redrawComposerBar();
  toast(kind === 'effort' ? `Reasoning effort: ${hit[1]}` : `${hit[1]} — ${hit[2]}`, 'ok');
}
function cmdInstructions(a) {
  if (!a) { ACTIONS['chat-system'](); return; }
  CHAT.settings.systemPrompt = /^(clear|off|none)$/i.test(a) ? null : a.slice(0, 4000);
  saveSettings(); redrawComposerBar();
  toast(CHAT.settings.systemPrompt ? 'Instructions set for this chat' : 'Instructions cleared', 'ok');
}
function cmdTemperature(a) {
  if (/^(default|auto|clear)$/i.test(a)) CHAT.settings.temperature = null;
  else { const t = Number(a); if (!(t >= 0 && t <= 2)) { toast('/temperature takes a number from 0 to 2, or default.', 'bad'); return; } CHAT.settings.temperature = t; }
  saveSettings();
  toast(CHAT.settings.temperature === null ? 'Temperature: the model\'s default' : `Temperature: ${CHAT.settings.temperature}`, 'ok');
}
async function cmdRename(a) {
  if (!needChat()) return;
  if (!a) { ACTIONS['chat-rename'](); return; }
  await patch('/api/chat/conversations/' + enc(CHAT.conv.id), { title: a.slice(0, 120) });
  setTitleAnimated(CHAT.conv.id, a.slice(0, 120)); refreshList();
}
function commandsHelp() {
  const groups = [...new Set(CHAT_COMMANDS.map(c => c.group))];
  openModal('Slash commands', `<div class="cmd-help"><p class="muted small">Type them at the start of the message box. Saved prompts${window.chatClient ? ', skills and commands from this computer' : ''} are listed with them.</p>
    ${groups.map(g => `<div class="menu-title">${esc(g)}</div>${CHAT_COMMANDS.filter(c => c.group === g).map(c => `<div class="cmd-row"><code>/${esc(c.name)}${c.args ? ` <i>${esc(c.args)}</i>` : ''}</code><span>${esc(c.desc)}</span></div>`).join('')}`).join('')}</div>`,
    '<button class="btn primary" data-act="modal-close">Close</button>');
}

// ------------------------------------------------------------------ goals
async function cmdGoal(a) {
  if (!a) {
    if (CHAT.settings.goal) toast(`Goal: ${CHAT.settings.goal}`, 'ok'); else toast('No goal yet. Type /goal and what the model should achieve.', 'bad');
    return;
  }
  if (/^(clear|off|stop|none|done)$/i.test(a)) { clearGoal(); return; }
  CHAT.settings.goal = a.slice(0, 4000);
  CHAT.goal = { chatId: CHAT.conv && CHAT.conv.id, status: 'set' };
  saveSettings();
  renderGoalBar(true);
  // Working starts at once. While an answer is being written, that answer is told (before its next step) and the new goal
  // is worked on by itself when it ends.
  if (currentStream() || (CHAT.conv && CHAT.live.has(CHAT.conv.id))) {
    try {
      const r = await post(`/api/chat/conversations/${enc(CHAT.conv.id)}/steer`, { content: `(The goal changed. New goal: ${CHAT.settings.goal})` });
      if (r && Array.isArray(r.items) && CHAT.steers) { CHAT.steers.set(CHAT.conv.id, r.items); if (typeof renderQueued === 'function') renderQueued(); }
      toast('Goal updated: the model reads it before its next step.', 'ok');
    } catch { toast('Goal updated: it is worked on when this answer ends.', 'ok'); }
    return;
  }
  await sendMessage(false, `My goal: ${CHAT.settings.goal}\n\nWork on it until it is done.`);
}
function clearGoal() {
  if (!CHAT.settings.goal) { toast('There is no goal to clear.', 'bad'); return; }
  CHAT.settings.goal = null;
  if (CHAT.conv && CHAT.conv.settings) CHAT.conv.settings.goal = null;
  saveSettings();
  renderGoalBar();
  toast('Goal cleared', 'ok');
}
ACTIONS['goal-clear'] = () => clearGoal();
ACTIONS['goal-show'] = () => openModal('Goal', `<div class="goal-modal"><p>${esc(CHAT.settings.goal || '')}</p>
  <p class="muted small">The model keeps working until it reports this goal met, and failed model calls are retried. Change it with <code>/goal …</code>.</p></div>`,
  '<button class="btn" data-act="goal-clear">Clear goal</button><button class="btn primary" data-act="modal-close">Close</button>');
/** A goal event from an answer (this window's or another's): met, not possible, or paused (the answer ended first). */
function onGoalEvent(chatId, d) {
  const here = CHAT.conv && CHAT.conv.id === chatId;
  if (!here) return;
  CHAT.goal = { chatId, status: d.status };
  if (d.status === 'met') {
    CHAT.settings.goal = null;
    if (CHAT.conv.settings) CHAT.conv.settings.goal = null;
    renderGoalBar();
    toast(`Goal met${d.summary ? ': ' + d.summary.slice(0, 140) : ''}`, 'ok');
    if (!IN_PANE) playSound('chatComplete');
  } else if (d.status === 'stopped') {
    // The server cleared the goal because the user asked it to stop; drop it here too so it does not re-trigger.
    CHAT.settings.goal = null;
    if (CHAT.conv.settings) CHAT.conv.settings.goal = null;
    renderGoalBar();
    toast('Goal cleared — you asked to stop.', 'ok');
  } else {
    renderGoalBar();
    if (d.status === 'resuming') toast(`Goal not met yet: continuing by itself (${d.attempt} of ${d.max})…`, 'ok');
    if (d.status === 'paused' && d.reason) toast('Goal paused: ' + d.reason, 'warn');
    if (d.status === 'failed') toast(`The goal could not be met${d.summary ? ': ' + d.summary.slice(0, 140) : ''}`, 'warn');
  }
}
/** The bar above the message box while the chat has a goal: what it is, and whether the model is working on it. */
function renderGoalBar(arrive) {
  const bar = $('#goal-bar'); if (!bar) return;
  const goal = CHAT.settings && CHAT.settings.goal;
  if (!goal) {
    if (!bar.classList.contains('hidden') && !bar.classList.contains('leaving')) {
      bar.classList.add('leaving');
      // Emptied when hidden: forget what it showed, or the same goal shown again would find an empty bar.
      setTimeout(() => { if (!bar.classList.contains('leaving')) return; bar.classList.add('hidden'); bar.classList.remove('leaving'); bar.innerHTML = ''; delete bar.dataset.key; }, reducedMotion() ? 0 : 220);
    }
    return;
  }
  const busy = !!currentStream() || !!(CHAT.conv && CHAT.live.has(CHAT.conv.id));
  // Reopened later: a goal still set after the model answered means it stopped before meeting it (paused).
  const lastMsg = CHAT.conv && CHAT.conv.messages[CHAT.conv.messages.length - 1];
  const st = CHAT.goal && CHAT.conv && CHAT.goal.chatId === CHAT.conv.id ? CHAT.goal.status : lastMsg && lastMsg.role === 'assistant' ? 'paused' : null;
  const [cls, label] = busy ? ['working', 'Working'] : st === 'resuming' ? ['working', 'Resuming…'] : st === 'failed' ? ['failed', 'Not possible'] : st === 'paused' ? ['paused', 'Paused'] : ['set', 'Set'];
  const html = `${GOAL_ICON}<b class="goal-label">Goal</b><button type="button" class="goal-text" data-act="goal-show" data-tip="${esc(goal.slice(0, 300))}">${esc(goal)}</button>
    <span class="goal-state ${cls}"><i></i>${label}</span><button type="button" class="goal-x" data-act="goal-clear" aria-label="Clear goal" data-tip="Clear goal">×</button>`;
  const key = goal + '|' + cls;
  if (bar.dataset.key !== key || !bar.firstElementChild) { bar.innerHTML = html; bar.dataset.key = key; }
  bar.classList.remove('leaving');
  if (bar.classList.contains('hidden') || arrive) { bar.classList.remove('hidden'); if (!reducedMotion()) { bar.classList.remove('arrive'); void bar.offsetWidth; bar.classList.add('arrive'); } }
}

// ------------------------------------------------------------------ the web app's / picker: commands, then saved prompts
document.addEventListener('input', ev => { if (ev.target && ev.target.id === 'chat-input') commandPicker(ev.target); });
async function commandPicker(input) {
  const v = input.value, m = v.match(/^\/([\w-]*)$/);
  // The desktop app shows commands in its own / list (desktop.js).
  if (!m || (window.chatClient && window.chatClient.slashPrompts)) { hideCmdPop(); return; }
  const q = m[1].toLowerCase();
  const saved = (await loadPrompts().catch(() => []) || []).filter(p => p.command.startsWith(q) || (q && (p.title || '').toLowerCase().includes(q)))
    .map(p => ({ name: p.command, desc: p.title || p.content.slice(0, 90), prompt: p }));
  if (input.value !== v || !$('.composer-wrap')) return;
  const hits = chatCommandHits(q).slice(0, 8).concat(saved.slice(0, 6));
  let pop = $('#cmd-pop');
  if (!pop) { pop = htmlNode('<div class="cmd-pop" id="cmd-pop" role="listbox" aria-label="Commands and saved prompts"></div>'); $('.composer-wrap').prepend(pop); }
  CHAT.cmdHits = hits; CHAT.cmdSel = 0;
  const firstSaved = hits.findIndex(h => h.prompt);
  pop.innerHTML = `<div class="cmd-pop-head">Commands <span class="muted">↑↓ · Enter · Esc</span></div>` + (hits.length ? hits.map((c, i) =>
    `${i === firstSaved ? '<div class="cmd-pop-head sub">Saved prompts</div>' : ''}<button type="button" class="cmd-opt${i === 0 ? ' on' : ''}${c.prompt ? ' saved' : ''}" role="option" data-act="cmd-pick" data-i="${i}">
    <b>/${esc(c.name)}</b>${c.args ? `<span class="cmd-args">${esc(c.args)}</span>` : ''}<span class="cmd-desc">${esc(c.desc)}</span></button>`).join('')
    : '<div class="muted small prompt-none">Nothing matches.</div>') +
    `<button type="button" class="prompt-manage" data-act="prompts-manage">${icon('plus')} Saved prompts</button>`;
}
function hideCmdPop() { $('#cmd-pop')?.remove(); CHAT.cmdHits = null; }
/** Picking a command: one that needs words after it waits for them; the others run at once. A saved prompt fills the box. */
function pickCommand(i) {
  const c = CHAT.cmdHits && CHAT.cmdHits[i], input = $('#chat-input');
  if (!c || !input) return;
  hideCmdPop();
  if (c.prompt) { input.value = c.prompt.content; autosize(); renderContext(); input.focus(); input.setSelectionRange(input.value.length, input.value.length); return; }
  if (c.args && (c.required || c.args.startsWith('<'))) { input.value = `/${c.name} `; autosize(); input.focus(); input.setSelectionRange(input.value.length, input.value.length); return; }
  input.value = '';
  autosize();
  runChatCommand('/' + c.name);
}
window.pickChatCommand = c => { CHAT.cmdHits = [c]; pickCommand(0); };
ACTIONS['cmd-pick'] = el => pickCommand(Number(el.dataset.i));
document.addEventListener('keydown', ev => {
  if (!CHAT.cmdHits || !ev.target || ev.target.id !== 'chat-input' || !$('#cmd-pop')) return;
  const n = CHAT.cmdHits.length;
  if (!n) { if (keyIs(ev, 'list.pick') || keyIs(ev, 'chat.send') || keyIs(ev, 'list.close')) hideCmdPop(); return; }
  const down = keyIs(ev, 'list.next');
  if (down || keyIs(ev, 'list.prev')) {
    ev.preventDefault(); ev.stopImmediatePropagation();
    CHAT.cmdSel = (CHAT.cmdSel + (down ? 1 : n - 1)) % n;
    $$('#cmd-pop .cmd-opt').forEach((b, i) => b.classList.toggle('on', i === CHAT.cmdSel));
  } else if (keyIs(ev, 'list.pick')) { ev.preventDefault(); ev.stopImmediatePropagation(); pickCommand(CHAT.cmdSel); }
  else if (keyIs(ev, 'list.close')) { ev.stopImmediatePropagation(); hideCmdPop(); }
}, true);
document.addEventListener('mousedown', ev => { if (CHAT.cmdHits && !ev.target.closest('#cmd-pop, #chat-input')) hideCmdPop(); });
document.addEventListener('chat:mounted', hideCmdPop);
