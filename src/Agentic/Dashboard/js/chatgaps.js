'use strict';
/* Agentic console - Chat: what the CLI, the desktop app and VS Code had that the Chat tab did not.
   Mermaid diagrams drawn as pictures; Stable Diffusion and Suno songs in chat (SD and Music chips); archived chats and chats
   imported from a file; /heartbeat (a checklist looked at on a schedule, quiet when nothing needs attention); /context,
   /diff, /remember, /forget, /read, /status, /mcp, /hooks, /skills, /release-notes; and, with the desktop app or VS Code,
   /bashes and /monitors (commands running in the background), /agents (your own subagent types) and /worktree. */

// ------------------------------------------------------------------ Mermaid diagrams
// A finished ```mermaid block is drawn by the server (mermaid-cli) and shown as a picture; "Code" shows the source again.
const MERMAID_URLS = new Map();
function darkTheme() {
  const t = document.documentElement.dataset.theme;
  return t === 'dark' || (t !== 'light' && window.matchMedia && matchMedia('(prefers-color-scheme: dark)').matches);
}
async function mermaidUrl(code, theme) {
  const key = theme + '\n' + code;
  if (MERMAID_URLS.has(key)) return MERMAID_URLS.get(key);
  const job = (async () => {
    const res = await fetch('/api/chat/mermaid', { method: 'POST', credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': S.csrf || '' }, body: JSON.stringify({ code, theme }) });
    if (!res.ok) { let msg = res.statusText; try { msg = (await res.json()).error.message; } catch { /* not JSON */ } throw new Error(msg); }
    return URL.createObjectURL(await res.blob());
  })();
  MERMAID_URLS.set(key, job);
  job.catch(() => MERMAID_URLS.delete(key));
  return job;
}
function drawMermaid(root) {
  if (typeof CHAT === 'undefined' || !CHAT.meta || !CHAT.meta.mermaid) return;
  $$('.code-block[data-mermaid]:not([data-drawn])', root).forEach(async block => {
    block.dataset.drawn = '1';
    const view = $('.mermaid-view', block), src = $('.code-src', block);
    if (!view || !src) return;
    view.innerHTML = '<div class="muted small mermaid-wait">Drawing the diagram…</div>';
    try {
      const url = await mermaidUrl(src.value, darkTheme() ? 'dark' : 'default');
      view.innerHTML = `<img class="mermaid-img" alt="Diagram" src="${esc(url)}">`;
      block.classList.add('diagram');
      const t = $('[data-act="mermaid-toggle"]', block); if (t) { t.hidden = false; t.textContent = 'Code'; }
    } catch (e) {
      view.innerHTML = `<div class="muted small mermaid-err">The diagram could not be drawn: ${esc(e.message)}</div>`;
    }
  });
}
ACTIONS['mermaid-toggle'] = el => {
  const block = document.getElementById(el.dataset.block);
  if (!block) return;
  const code = block.classList.toggle('diagram');
  el.textContent = code ? 'Code' : 'Diagram';
};

// ------------------------------------------------------------------ SD and Music chips, songs (make_song)
const SD_ICON = '<svg viewBox="0 0 24 24" class="ic"><path d="M4 20l6-6M14 4l6 6-8 8-6-6z"/><path d="M15 3l1 2M19 7l2 1M17 3.5l.5 1.5"/></svg>';
const MUSIC_ICON = '<svg viewBox="0 0 24 24" class="ic"><path d="M9 18V5l11-2v13"/><circle cx="6" cy="18" r="3"/><circle cx="17" cy="16" r="3"/></svg>';
function mediaChipsHtml(meta, s) {
  return (meta.diffusion ? `<button class="chip-toggle${s.diffusion ? ' on' : ''}" data-act="chat-toggle" data-key="diffusion" data-tip="Stable Diffusion: the model makes pictures on the Images tab's server (they also go to your gallery)">${SD_ICON} SD</button>` : '') +
    (meta.music ? `<button class="chip-toggle${s.music ? ' on' : ''}" data-act="chat-toggle" data-key="music" data-tip="Music: the model makes songs with Suno when you ask for one (uses Suno credits; songs also go to your Music library)">${MUSIC_ICON} Music</button>` : '');
}
function songCardHtml(a) {
  const id = enc(a.uploadId);
  return `<div class="song-card"><img class="song-cover" src="/api/music/songs/${id}/cover" alt="" loading="lazy">
    <div class="song-body"><div class="song-title">${esc(a.name || 'Song')}</div>${a.summary ? `<div class="muted small">${esc(a.summary)}</div>` : ''}
      <audio controls preload="none" src="/api/music/songs/${id}/audio"></audio></div>
    <a class="btn xs ghost" href="/api/music/songs/${id}/audio?download=1" download data-tip="Download">${icon('download')}</a></div>`;
}
document.addEventListener('error', ev => {
  const img = ev.target;
  if (img && img.tagName === 'IMG' && img.classList.contains('song-cover')) img.classList.add('none');
}, true);

// ------------------------------------------------------------------ archived chats
ACTIONS['chat-archive'] = async () => {
  if (!CHAT.conv) return;
  const on = !CHAT.conv.archived;
  await patch('/api/chat/conversations/' + enc(CHAT.conv.id), { archived: on });
  CHAT.conv.archived = on;
  if (on) CHAT.list = CHAT.list.filter(c => c.id !== CHAT.conv.id); else await refreshList();
  renderChatList();
  toast(on ? 'Archived: it left the list (/archived shows archived chats)' : 'Back in the list', 'ok');
};
async function showArchived() {
  const list = await get('/api/chat/conversations?archived=1&limit=300');
  openModal('Archived chats', list.length ? `<div class="arch-list">${list.map(c => `<div class="arch-row" data-id="${esc(c.id)}">
      <div class="grow"><b>${esc(c.title)}</b><div class="muted small">${esc(ago(c.updatedUtc))}${c.preview ? ' · ' + esc(c.preview.slice(0, 90)) : ''}</div></div>
      <button class="btn xs" data-act="arch-open" data-id="${esc(c.id)}">Open</button><button class="btn xs ghost" data-act="arch-restore" data-id="${esc(c.id)}">Unarchive</button></div>`).join('')}</div>`
    : emptyState('No archived chats', 'Archive a chat from its ⋯ menu (or /archive) to take it out of the list without deleting it.'), '', 'wide');
}
ACTIONS['arch-open'] = el => { closeModal(); navigate('/chat/' + enc(el.dataset.id)); };
ACTIONS['arch-restore'] = async el => {
  await patch('/api/chat/conversations/' + enc(el.dataset.id), { archived: false });
  el.closest('.arch-row').remove();
  if (CHAT.conv && CHAT.conv.id === el.dataset.id) CHAT.conv.archived = false;
  await refreshList(); renderChatList();
  toast('Back in the list', 'ok');
};

// ------------------------------------------------------------------ import
function importChats() {
  const input = document.createElement('input');
  input.type = 'file'; input.accept = '.json,application/json';
  input.addEventListener('change', async () => {
    const file = input.files && input.files[0];
    if (!file) return;
    try {
      const res = await fetch('/api/chat/import?model=' + enc(CHAT.model || ''), { method: 'POST', credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': S.csrf || '' }, body: await file.text() });
      const data = await res.json().catch(() => null);
      if (!res.ok) throw new Error(data && data.error ? data.error.message : res.statusText);
      await refreshList(); renderChatList();
      const chats = data.chats || [];
      toast(chats.length === 1 ? `Imported "${chats[0].title}" (${num(chats[0].messages)} messages)` : `Imported ${num(chats.length)} chats`, 'ok');
      if (chats.length) navigate('/chat/' + enc(chats[0].id));
    } catch (e) { toast('Import failed: ' + e.message, 'bad'); }
  });
  input.click();
}
ACTIONS['chat-import'] = () => importChats();

// ------------------------------------------------------------------ /heartbeat
// Like the CLI's: a checklist the model goes through on a schedule (the server sends it, also while no window is open).
// When nothing needs attention it answers HEARTBEAT_OK and the server takes the check out of the chat again.
const HEARTBEAT_PREFIX = 'Heartbeat check.';
const HEARTBEAT_TEMPLATE = '- Anything failing, stuck or waiting on me?\n- ';
function heartbeatPrompt(checklist) {
  const local = !!(window.chatClient && window.chatClient.runTool);
  return `${HEARTBEAT_PREFIX} Go through the checklist below and act on anything that needs attention now, using your tools where useful. Keep it short. ` +
    `If nothing needs attention, reply with exactly HEARTBEAT_OK and nothing else.${local ? ' If HEARTBEAT.md exists in the project folder on the user\'s computer, go through it too.' : ''}` +
    `\n\n<heartbeat>\n${checklist.trim()}\n</heartbeat>`;
}
function checklistOf(prompt) { const m = /<heartbeat>\n?([\s\S]*?)\n?<\/heartbeat>/.exec(prompt || ''); return m ? m[1] : ''; }
async function heartbeatSchedule() {
  if (!CHAT.conv) return null;
  const list = await get('/api/chat/schedules?chatId=' + enc(CHAT.conv.id));
  return list.find(s => (s.prompt || '').startsWith(HEARTBEAT_PREFIX)) || null;
}
async function cmdHeartbeat(a) {
  const [verb, ...rest] = (a || '').trim().split(/\s+/);
  const v = (verb || '').toLowerCase();
  if (!CHAT.conv) { toast('Start a conversation first: the heartbeat runs in a chat.', 'bad'); return; }
  const hb = await heartbeatSchedule();
  if (!v) {
    toast(hb ? `Heartbeat on: ${hb.when}${hb.lastStatus ? ' · last: ' + hb.lastStatus : ''}. /heartbeat edit · now · off` : 'Heartbeat off. /heartbeat on [30m] starts it.', 'ok');
    return;
  }
  if (v === 'off') { if (!hb) { toast('The heartbeat is not on.', 'bad'); return; } await del('/api/chat/schedules/' + enc(hb.id)); toast('Heartbeat off', 'ok'); return; }
  if (v === 'now') { if (!hb) { toast('The heartbeat is not on: /heartbeat on first.', 'bad'); return; } await post('/api/chat/schedules/' + enc(hb.id) + '/run'); toast('Heartbeat check started', 'ok'); return; }
  if (v === 'on' || v === 'edit') {
    let every = (rest[0] || '').toLowerCase();
    if (!/^\d+[mh]$/.test(every)) every = hb ? (hb.when || '').replace(/^every\s+/, '') : '30m';
    if (v === 'on' && hb && !rest[0]) { toast(`The heartbeat is already on (${hb.when}). /heartbeat edit changes its checklist.`, 'ok'); return; }
    openModal('Heartbeat', `<form id="hb-form" class="stack" data-form="heartbeat">
        <p class="muted small">What to look at every time. The model acts on what needs attention; when nothing does, it answers HEARTBEAT_OK and the check leaves the chat again.</p>
        <label>Every<input name="every" value="${esc(every)}" placeholder="30m" pattern="\\d+[mh]"></label>
        <label>Checklist<textarea name="checklist" rows="8" class="mono">${esc(hb ? checklistOf(hb.prompt) : HEARTBEAT_TEMPLATE)}</textarea></label>
        <input type="hidden" name="old" value="${esc(hb ? hb.id : '')}"></form>`,
      `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="submit-form" data-form-id="hb-form">${hb ? 'Save' : 'Start'}</button>`);
    return;
  }
  toast('/heartbeat [on [30m] | edit | now | off]', 'bad');
}
FORMS['heartbeat'] = async (f, d) => {
  const every = /^\d+[mh]$/i.test(d.every || '') ? d.every.toLowerCase() : '30m';
  if (!(d.checklist || '').trim()) throw new Error('Write what the heartbeat should check.');
  if (d.old) await del('/api/chat/schedules/' + enc(d.old));
  await post('/api/chat/schedules', { chatId: CHAT.conv.id, prompt: heartbeatPrompt(d.checklist), when: 'every ' + every, timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone });
  closeModal();
  toast(`Heartbeat ${d.old ? 'saved' : 'on'}: every ${every}`, 'ok');
};
/** A heartbeat's check in the conversation: one small line, its checklist folded. */
function heartbeatMsgHtml(m, live) {
  return `<div class="msg auto-note${live ? ' new' : ''}" data-id="${esc(m.id || '')}"><details class="auto-events"><summary class="auto-chip">♥ Heartbeat check</summary>
    <pre class="auto-lines">${esc(checklistOf(m.content))}</pre></details></div>`;
}

// ------------------------------------------------------------------ /context, /status
async function showContext() {
  if (!CHAT.conv) { toast('Start a conversation first.', 'bad'); return; }
  const c = await get(`/api/chat/conversations/${enc(CHAT.conv.id)}/context?model=${enc(CHAT.model || '')}`);
  const pct = Math.min(100, c.percent || 0);
  openModal('Context', `<div class="stack">
      <div class="ctx-full-bar"><i></i></div>
      <dl class="kv"><dt>Model</dt><dd>${esc(modelName(c.model || CHAT.model))}</dd>
        <dt>Used</dt><dd>${num(c.used)} of ${num(c.max)} tokens (${(c.percent || 0).toFixed(1)}%)</dd>
        <dt>Messages</dt><dd>${num(c.messages)}${c.messagesInFull !== c.messages ? ` · the last ${num(c.messagesInFull)} in full` : ''}</dd>
        <dt>Summary</dt><dd>${c.compacted ? `earlier messages summarized (${num(c.summaryTokens || 0)} tokens${c.compactedUtc ? ', ' + esc(ago(c.compactedUtc)) : ''})` : 'none'}</dd>
        <dt>Compacts</dt><dd>${c.autoCompactPercent ? `automatically at ${c.autoCompactPercent}%` : 'only when you ask (/compact)'}</dd></dl></div>`,
    `<button class="btn" data-act="modal-close">Close</button><button class="btn primary" data-act="ctx-compact">Compact now</button>`);
  const bar = $('.modal .ctx-full-bar i'); if (bar) bar.style.width = pct + '%';
}
ACTIONS['ctx-compact'] = () => { closeModal(); ACTIONS['chat-compact'](); };
function showStatus() {
  const m = CHAT.meta || {}, s = CHAT.settings || {}, st = S.status || {};
  const on = [s.webSearch && 'web search', s.codeInterpreter && 'code interpreter', s.imageGeneration && 'image', s.videoGeneration && 'video', s.diffusion && 'Stable Diffusion',
    s.music && 'Music', s.markets && 'Money', s.stocks && 'Stocks', s.polymarket && 'Polymarket', s.news && 'News'].filter(Boolean);
  const client = window.chatClient ? (window.chatClient.lapi ? 'VS Code' : 'the desktop app') : 'this browser';
  openModal('Status', `<dl class="kv">
      <dt>Server</dt><dd>${esc(st.name || 'Agentic')} v${esc(st.version || '?')}</dd>
      <dt>Signed in</dt><dd>${esc((S.me && S.me.displayName) || S.user || '')}${m.isAdmin ? ' · administrator' : ''}</dd>
      <dt>Running in</dt><dd>${esc(client)}</dd>
      <dt>Model</dt><dd>${esc(modelName(CHAT.model))}</dd>
      <dt>Permission mode</dt><dd>${esc(s.permissionMode || m.defaultPermissionMode || 'auto')}</dd>
      <dt>Effort</dt><dd>${esc(s.reasoningEffort || 'default')}</dd>
      <dt>Switched on</dt><dd>${on.length ? esc(on.join(', ')) : 'nothing'}</dd>
      <dt>MCP servers</dt><dd>${(s.mcpServers || []).length ? esc(s.mcpServers.join(', ')) : 'none'}</dd>
      ${m.account && m.account.charged ? `<dt>Balance</dt><dd>${esc(money(m.account.balanceMicros))}</dd>` : ''}
      ${CHAT.conv ? `<dt>Chat</dt><dd>${esc(CHAT.conv.title)} · ${num(CHAT.conv.messages.length)} messages</dd>` : ''}</dl>`, '', 'wide');
}

// ------------------------------------------------------------------ /remember, /forget, /read
async function cmdRemember(a) {
  const text = (a || '').trim();
  if (!text) { ACTIONS['chat-personalize'](); return; }
  await post('/api/chat/memories', { content: text });
  toast('Remembered: ' + text, 'ok');
}
async function cmdForget(a) {
  const q = (a || '').trim().toLowerCase();
  if (!q) { ACTIONS['chat-personalize'](); return; }
  const p = await get('/api/chat/personalization');
  const hits = (p.memories || []).filter(m => m.id === a.trim() || (m.content || '').toLowerCase().includes(q));
  if (!hits.length) { toast('No memory matches that.', 'bad'); return; }
  for (const m of hits) await del('/api/chat/memories/' + enc(m.id));
  toast(hits.length === 1 ? 'Forgot: ' + hits[0].content : `Forgot ${hits.length} memories`, 'ok');
}
function cmdRead() {
  const btn = $$('#chat-thread .msg.assistant [data-act="msg-speak"]').pop();
  if (btn) btn.click(); else toast('There is no answer to read yet.', 'bad');
}

// ------------------------------------------------------------------ the desktop app and VS Code: jobs, hooks, skills, agents, worktree, diff
function needClient(what) {
  if (window.chatClient && window.chatClient[what]) return true;
  toast('This works in the desktop app and in VS Code, where the tools run on your computer.', 'bad');
  return false;
}
async function showJobs(kind) {
  if (!needClient('jobs')) return;
  const all = await window.chatClient.jobs();
  const list = (all || []).filter(j => !kind || j.kind === kind);
  const title = kind === 'monitor' ? 'Monitors' : 'Background commands';
  openModal(title, list.length ? `<div class="job-list">${list.map(j => `<div class="job-row${j.running ? ' on' : ''}">
      <div class="grow"><code>${esc(j.command || j.description || j.id)}</code>
        <div class="muted small">${esc(j.kind === 'monitor' ? 'monitor' : 'command')} · ${j.running ? 'running' : 'ended' + (j.exitCode != null ? ' (exit ' + j.exitCode + ')' : '')}${j.startedUtc ? ' · started ' + esc(ago(j.startedUtc)) : ''}${j.description && j.command ? ' · ' + esc(j.description) : ''}</div>
        ${j.output ? `<pre class="job-out">${esc(j.output)}</pre>` : ''}</div>
      ${j.running ? `<button class="btn xs danger" data-act="job-stop" data-id="${esc(j.id)}">Stop</button>` : ''}</div>`).join('')}</div>`
    : emptyState(kind === 'monitor' ? 'No monitors' : 'Nothing running', kind === 'monitor' ? 'The model watches a command with the monitor tool; its lines arrive as messages.' : 'Commands the model starts in the background show here.'), '', 'wide');
}
ACTIONS['job-stop'] = async el => {
  const r = await window.chatClient.stopJob(el.dataset.id);
  toast(r && r.message ? r.message : 'Stopped', r && r.stopped === false ? 'bad' : 'ok');
  const row = el.closest('.job-row'); if (row) { row.classList.remove('on'); el.remove(); }
};
async function showHooks() {
  if (!needClient('hooks')) return;
  const h = await window.chatClient.hooks();
  const rows = (h && h.hooks) || [];
  openModal('Hooks', `<p class="muted small">Commands this computer runs around the chat (Claude Code's format, in ${esc((h && h.files || []).join(', ') || '.agentic/settings.json')}):
      PreToolUse / PostToolUse around tools, UserPromptSubmit before a message is sent (exit 2 stops it; what it prints goes with the message),
      SessionStart when a chat starts here, Stop after an answer.</p>
    ${rows.length ? `<table class="table"><thead><tr><th>Event</th><th>Matcher</th><th>Command</th><th></th></tr></thead><tbody>${rows.map((r, i) => `<tr><td>${esc(r.event)}</td><td>${esc(r.matcher || '*')}</td><td><code>${esc(r.command)}</code><div class="cell-sub">${esc(r.file || '')}</div></td>
        <td class="act">${window.chatClient.testHook ? `<button class="btn xs ghost" data-act="hook-test" data-i="${i}">Try</button>` : ''}${window.chatClient.removeHook ? `<button class="btn xs ghost danger" data-act="hook-remove" data-i="${i}">Remove</button>` : ''}</td></tr>`).join('')}</tbody></table>`
    : emptyState('No hooks', 'Add one below, or in .agentic/settings.json (or .claude/settings.json) in the project or your home folder.')}
    <div id="hook-try-out"></div>`,
    `<button class="btn" data-act="modal-close">Close</button>${window.chatClient.saveHook ? `<button class="btn primary" data-act="hook-new">${icon('plus')} Add hook</button>` : ''}`, 'wide');
  HOOKS_SHOWN = rows;
}
let HOOKS_SHOWN = [];
const HOOK_EVENT_LIST = ['PreToolUse', 'PostToolUse', 'PostToolUseFailure', 'PermissionRequest', 'UserPromptSubmit', 'SessionStart', 'Stop', 'SubagentStart', 'SubagentStop', 'Notification', 'PreCompact', 'PostCompact'];
ACTIONS['hook-new'] = () => {
  openModal('Add a hook', `<form class="stack" data-form="hook-save">
    <div class="form-grid"><label>When<select name="event">${HOOK_EVENT_LIST.map(e => `<option>${e}</option>`).join('')}</select></label>
      <label>Matcher <span class="muted small">(tools: Edit|Write, Bash, mcp__.*; empty: all)</span><input name="matcher" placeholder="e.g. Edit|Write"></label></div>
    <label>Command <span class="muted small">(gets the event as JSON on stdin; exit 2 blocks; JSON on stdout can allow, deny or add context)</span>
      <textarea name="command" rows="3" class="mono" placeholder="npx prettier --write \"$(jq -r .tool_input.file_path)\""></textarea></label>
    <div class="form-grid"><label>Timeout (seconds)<input name="timeout" type="number" min="1" max="600" placeholder="60"></label>
      <label>Where<select name="scope"><option value="project">This project (.agentic/settings.json)</option><option value="user">Me, every project (~/.agentic/settings.json)</option></select></label></div>
    <div data-errors></div><div id="hook-new-out"></div></form>`,
    `<button class="btn" data-act="hook-try-new">Try it</button><button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="hook-save-go">Add hook</button>`, 'wide');
};
ACTIONS['hook-save-go'] = () => $('#modal-card form').requestSubmit();
FORMS['hook-save'] = async (f, d) => {
  try { await window.chatClient.saveHook({ event: d.event, matcher: d.matcher || '', command: d.command || '', timeout: Number(d.timeout) || 0, scope: d.scope }); toast('Hook added', 'ok'); showHooks(); }
  catch (e) { const box = $('[data-errors]', f); if (box) box.innerHTML = `<div class="notice bad small">${esc(e.message)}</div>`; }
};
function hookTryHtml(r) {
  return `<div class="section"><div>${r.code === 0 ? badge('exit 0', 'ok') : r.code === 2 ? badge('exit 2: blocks', 'warn') : badge('exit ' + r.code, 'bad')}</div>
    ${r.stdout ? `<div class="small">Printed:</div><pre class="code-block">${esc(r.stdout)}</pre>` : ''}${r.stderr ? `<div class="small">Errors:</div><pre class="code-block">${esc(r.stderr)}</pre>` : ''}
    <details><summary class="small muted">Sample input it got</summary><pre class="code-block">${esc(r.input || '')}</pre></details></div>`;
}
ACTIONS['hook-try-new'] = async () => {
  const f = $('#modal-card form'); const out = $('#hook-new-out');
  out.innerHTML = '<div class="muted small">Running…</div>';
  try { out.innerHTML = hookTryHtml(await window.chatClient.testHook({ event: f.elements.event.value, command: f.elements.command.value })); } catch (e) { out.innerHTML = `<div class="notice bad small">${esc(e.message)}</div>`; }
};
ACTIONS['hook-test'] = async el => {
  const r = HOOKS_SHOWN[Number(el.dataset.i)]; const out = $('#hook-try-out'); if (!r || !out) return;
  out.innerHTML = '<div class="muted small">Running…</div>';
  try { out.innerHTML = hookTryHtml(await window.chatClient.testHook({ event: r.event, command: r.command })); } catch (e) { out.innerHTML = `<div class="notice bad small">${esc(e.message)}</div>`; }
};
ACTIONS['hook-remove'] = async el => {
  const r = HOOKS_SHOWN[Number(el.dataset.i)]; if (!r) return;
  if (!await confirmBox('Remove hook', `Remove the ${esc(r.event)} hook <code>${esc(r.command)}</code> from ${esc(r.file)}?`, { danger: true, confirm: 'Remove' })) return;
  try { await window.chatClient.removeHook({ file: r.file, event: r.event, matcher: r.matcher || '', command: r.command }); toast('Removed', 'ok'); } catch (e) { toast(e.message, 'bad'); }
  showHooks();
};
async function showSkills() {
  const fromClient = window.chatClient && window.chatClient.skills ? await window.chatClient.skills() : null;
  const prompts = await get('/api/chat/prompts').catch(() => []);
  const list = [...(fromClient || []), ...(prompts || []).map(p => ({ name: p.command || p.title, description: p.title, kind: 'saved prompt' }))];
  openModal('Skills and commands', list.length ? `<div class="cmd-help">${list.map(s => `<div class="cmd-row"><code>/${esc(s.name)}</code><span>${esc(s.description || '')}${s.kind ? ` <span class="muted small">· ${esc(s.kind)}</span>` : ''}</span></div>`).join('')}</div>`
    : emptyState('None yet', window.chatClient ? 'Add skills (SKILL.md) or commands to .claude/skills, .claude/commands or .agentic in the project or your home folder.' : 'Save prompts with /prompts; in the desktop app and VS Code your skills and commands show here too.'), '', 'wide');
}
async function showAgents() {
  if (!needClient('agents')) return;
  const list = await window.chatClient.agents();
  openModal('Your agents', `<p class="muted small">Subagent types from Markdown files in .claude/agents or .agentic/agents (the project's or your home folder's). The model hands them work with the task tool; each works by its file's instructions with only the tools it lists.</p>
    ${(list || []).length ? `<div class="cmd-help">${list.map(a => `<div class="cmd-row"><code>${esc(a.name)}</code><span>${esc(a.description || '')}<span class="muted small"> · ${a.tools && a.tools.length ? esc(a.tools.join(', ')) : 'every tool'} · ${esc(a.file || '')}</span></span></div>`).join('')}</div>`
      : emptyState('No agents yet', 'Create one below, or put Claude Code agent files in .claude/agents.')}`,
    `<button class="btn" data-act="modal-close">Close</button><button class="btn primary" data-act="agent-new">${icon('plus')} New agent</button>`, 'wide');
}
ACTIONS['agent-new'] = () => {
  openModal('New agent', `<form id="agent-def-form" class="stack" data-form="agent-def">
      <label>Name<input name="name" required pattern="[A-Za-z0-9_.-]+" placeholder="code-reviewer"></label>
      <label>When to use it<input name="description" placeholder="Reviews changes for bugs and style; use after edits"></label>
      <label>Tools <span class="muted small">(comma-separated, e.g. Read, Grep, Glob; empty = every tool)</span><input name="tools" placeholder="Read, Grep, Glob"></label>
      <label>Instructions<textarea name="prompt" rows="8" required placeholder="You review code changes…"></textarea></label>
      <label class="check-row"><input type="checkbox" name="user"> <span>For all my projects (in my home folder) instead of this project</span></label></form>`,
    `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="submit-form" data-form-id="agent-def-form">Create</button>`);
};
FORMS['agent-def'] = async (f, d) => {
  const r = await window.chatClient.saveAgent({ name: d.name, description: d.description, prompt: d.prompt, scope: d.user ? 'user' : 'project',
    tools: (d.tools || '').split(',').map(x => x.trim()).filter(Boolean) });
  closeModal();
  toast(`Agent ${d.name} saved${r && r.file ? ' to ' + r.file : ''}`, 'ok');
};
async function cmdWorktree(a) {
  if (!needClient('worktree')) return;
  const r = await window.chatClient.worktree((a || '').trim());
  if (!r) return;
  if (r.list !== undefined) { openModal('Worktrees', `<pre class="job-out">${esc(r.list || 'none')}</pre>`, '', 'wide'); return; }
  toast(r.message || (r.ok ? 'Done' : 'Failed'), r.ok === false ? 'bad' : 'ok');
}
async function showDiff() {
  if (!needClient('diff')) return;
  const d = await window.chatClient.diff();
  if (!d || (!d.diff && !d.status)) { toast(d && d.error ? d.error : 'No changes.', d && d.error ? 'bad' : 'ok'); return; }
  openModal('Changes', `${d.status ? `<pre class="job-out">${esc(d.status)}</pre>` : ''}${d.diff ? `<pre class="embed-code diff-view"><code>${highlight(d.diff, 'diff')}</code></pre>` : ''}`, '', 'wide');
}

// ------------------------------------------------------------------ commands
if (typeof CHAT_COMMANDS !== 'undefined') {
  CHAT_COMMANDS.push(
    { name: 'sd', args: '[on | off]', group: 'Settings', desc: 'Stable Diffusion: the model makes pictures on the Images tab\'s server', when: () => !!(CHAT.meta && CHAT.meta.diffusion), run: a => setToggle('diffusion', 'Stable Diffusion', a) },
    { name: 'music', args: '[on | off]', group: 'Settings', desc: 'Music: the model makes songs with Suno (uses Suno credits)', when: () => !!(CHAT.meta && CHAT.meta.music), run: a => setToggle('music', 'Music', a) },
    { name: 'archive', group: 'Chat', desc: 'Archive this chat (out of the list, not deleted); again: back in the list', run: () => needChat() && ACTIONS['chat-archive']() },
    { name: 'archived', group: 'Chat', desc: 'Archived chats: open or bring back', run: () => showArchived() },
    { name: 'import', group: 'Chat', desc: 'Import chats from a file: a chat exported as JSON, or ChatGPT\'s or Claude.ai\'s conversations.json', run: () => importChats() },
    { name: 'heartbeat', args: '[on [30m] | edit | now | off]', group: 'Work', desc: 'A checklist the model looks at on a schedule; quiet when nothing needs attention', run: cmdHeartbeat },
    { name: 'context', group: 'Chat', desc: 'How full the context window is', run: () => showContext() },
    { name: 'status', group: 'More', desc: 'Server, model, mode and what is switched on', run: () => showStatus() },
    { name: 'remember', args: '<fact>', group: 'More', desc: 'Add something the assistant should know about you', run: cmdRemember },
    { name: 'forget', args: '<words | id>', group: 'More', desc: 'Remove memories that contain these words', run: cmdForget },
    { name: 'read', group: 'Voice', desc: 'Read the last answer aloud', run: () => cmdRead() },
    // With the desktop app or VS Code: their MCP servers page (add, allow, sign in); else the server's MCP servers in the Tools menu.
    { name: 'mcp', group: 'Settings', desc: 'MCP servers: on this computer (desktop app, VS Code), or the server\'s for this chat', when: () => !!((window.chatClient && window.chatClient.mcpPage) || (CHAT.meta && CHAT.meta.canTools)), run: () => { if (window.chatClient && window.chatClient.mcpPage) { window.chatClient.mcpPage(); return; } const b = $('#tools-btn'); if (b) b.click(); else toast('No MCP servers are available.', 'bad'); } },
    { name: 'skills', group: 'More', desc: 'Your skills, commands and saved prompts', run: () => showSkills() },
    { name: 'release-notes', group: 'More', desc: 'What changed in the newest versions', run: () => navigate('/updates') },
    { name: 'hooks', group: 'More', desc: 'Hooks this computer runs around the chat (desktop app, VS Code)', when: () => !!(window.chatClient && window.chatClient.hooks), run: () => showHooks() },
    { name: 'bashes', group: 'Work', desc: 'Commands running in the background: see and stop them (alias /tasks)', when: () => !!(window.chatClient && window.chatClient.jobs), run: () => showJobs('command') },
    { name: 'tasks', group: 'Work', desc: 'Commands running in the background (like /bashes)', when: () => !!(window.chatClient && window.chatClient.jobs), run: () => showJobs('command') },
    { name: 'monitors', group: 'Work', desc: 'Commands the monitor tool watches: see and stop them', when: () => !!(window.chatClient && window.chatClient.jobs), run: () => showJobs('monitor') },
    { name: 'agents', group: 'Work', desc: 'Your own subagent types (.claude/agents): list and create', when: () => !!(window.chatClient && window.chatClient.agents), run: () => showAgents() },
    { name: 'worktree', args: '[name | list | done | remove <name>]', group: 'Work', desc: 'Work in a git worktree: its own folder and branch', when: () => !!(window.chatClient && window.chatClient.worktree), run: cmdWorktree },
    { name: 'diff', group: 'Work', desc: 'The changes in the working tree (git)', when: () => !!(window.chatClient && window.chatClient.diff), run: () => showDiff() });
}
