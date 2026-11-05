'use strict';
/* Agentic console - Chat: run and test the project (desktop app, VS Code), like Claude Desktop's preview. The app on the
   computer finds how to run each project of its workspace - .agentic/launch.json, Claude Code's .claude/launch.json, or the
   project's files (package.json scripts, a static site, Python, .NET, Go, Rust, a Makefile) - and runs it there; a web app
   opens in the preview next to the chat. The app provides window.chatClient.run:
     configs() -> [{id, name, command, kind: web|static|app|test, url?, project?, projectName?}]   start(id)   stop(id)
     status() -> [{id, name, kind, running, url, exitCode}]   log(id, from) -> {text, end, running, exitCode, url}
     preview(url, title)   edit()   (newer apps) projects() -> [{id, name, path, share, key}]   share(projectId) -> {link, updated} */

const RUN = { configs: [], status: [], poll: 0, autoOpen: new Set(), opened: new Set(), announced: new Set() };
const RUNLOG = { id: null, from: 0, timer: 0 };
/** Called after the run state is read again (the Artifacts page redraws its projects). */
const RUN_LISTENERS = new Set();
const RUN_ICON = '<svg viewBox="0 0 24 24" class="ic"><path d="M8 5v14l11-7z"/></svg>';
const RUN_KIND = { web: '🌐', static: '🌐', app: '▶', test: '🧪' };
const RUN_SETUP_PROMPT = 'Work out how to run and test this project (its README, package or project files and scripts), then write ' +
  '.agentic/launch.json in its root folder: {"configurations": [{"name": "Dev server", "runtimeExecutable": "npm", "runtimeArgs": ["run", "dev"], ' +
  '"cwd": ".", "port": 5173, "kind": "web"}]} - one entry for each way to start it (a dev server or the app: kind "web" when it serves a page, ' +
  '"app" otherwise) and one for its tests (kind "test"). Check that each command works before you save the file, then tell me what you wrote.';

function runClient() { return window.chatClient && window.chatClient.run ? window.chatClient.run : null; }

/** The Run chip in the composer bar (only where the app can run things on this computer). */
function runChipHtml() {
  if (!runClient()) return '';
  return `<div class="tools-menu-wrap run-wrap"><button class="chip-toggle run-chip" data-act="run-menu" id="run-btn">${runChipInner()}</button>
    <div class="menu tools-menu run-menu hidden" id="run-menu" data-keep-menu></div></div>`;
}
function runChipInner() {
  const live = RUN.status.filter(s => s.running);
  return `${RUN_ICON} ${live.length ? esc(live[0].name) : 'Run'}${live.length > 1 ? ` <b>${live.length}</b>` : ''}`;
}
function refreshRunChip() {
  const b = $('#run-btn');
  if (!b) return;
  const live = RUN.status.filter(s => s.running);
  b.innerHTML = runChipInner();
  b.classList.toggle('on', live.length > 0);
  b.classList.toggle('live', live.length > 0);
  b.dataset.tip = live.length ? `Running: ${live.map(s => s.name).join(', ')}` : 'Run and test this project: a dev server, the app or its tests, with a preview next to the chat';
}

async function refreshRun(withConfigs) {
  const rc = runClient();
  if (!rc) return;
  if (withConfigs) RUN.configs = (await rc.configs()) || [];
  RUN.status = (await rc.status()) || [];
  // A web app started here opens in the preview once it has an address; a finished run says how it went.
  for (const s of RUN.status) {
    if (s.running && s.url && RUN.autoOpen.has(s.id) && !RUN.opened.has(s.id)) {
      RUN.opened.add(s.id);
      try { rc.preview(s.url, s.name); } catch { /* no preview pane */ }
      toast(`${s.name} is running at ${s.url}`, 'ok');
    }
    if (!s.running && s.exitCode != null && RUN.autoOpen.has(s.id) && !RUN.announced.has(s.id)) {
      RUN.announced.add(s.id);
      RUN.autoOpen.delete(s.id);
      if (s.exitCode === 0) toast(`${s.name}: done`, 'ok');
      else toast(`${s.name} failed (exit code ${s.exitCode}). Open its output with ≡ in the Run menu, or ask Agentic to fix it there.`, 'bad');
    }
  }
  refreshRunChip();
  for (const f of [...RUN_LISTENERS]) { try { f(); } catch { /* a page that went away */ } }
}

/** Keeps the chip current: often while something runs, now and then otherwise (the model may start things too). */
function pollRun() {
  clearTimeout(RUN.poll);
  if (!runClient()) return;
  const busy = RUN.status.some(s => s.running) || RUN.autoOpen.size > 0;
  RUN.poll = setTimeout(async () => {
    if (!document.hidden) { try { await refreshRun(false); renderRunMenu(); } catch { /* the app is busy */ } }
    pollRun();
  }, busy ? 1500 : 5000);
}

async function openRunMenu() {
  const menu = $('#run-menu');
  if (!menu) return;
  if (!menu.classList.contains('hidden')) { menu.classList.add('hidden'); return; }
  menu.classList.remove('hidden');
  menu.innerHTML = '<div class="menu-title">Run this project</div><p class="muted small run-empty">Looking at the project…</p>';
  try { await refreshRun(true); } catch (e) { menu.innerHTML = `<div class="notice bad">${esc(e.message || String(e))}</div>`; return; }
  renderRunMenu();
}

/** One way to run the project: start it (or show it when it runs), with its preview, output and stop buttons. */
function runRowHtml(c) {
  const s = RUN.status.find(x => x.id === c.id), running = s && s.running;
  const acts = running
    ? `${s.url ? `<button class="btn xs" data-act="run-preview" data-url="${esc(s.url)}" data-name="${esc(c.name)}" data-tip="Show it in the preview">↗</button>` : ''}` +
      `<button class="btn xs" data-act="run-log" data-id="${esc(c.id)}" data-tip="Its output">≡</button><button class="btn xs danger" data-act="run-stop" data-id="${esc(c.id)}" data-tip="Stop">■</button>`
    : s && s.exitCode != null
      ? `<span class="run-exit ${s.exitCode === 0 ? 'ok' : 'bad'}">${s.exitCode === 0 ? '✓' : '✗ ' + s.exitCode}</span><button class="btn xs" data-act="run-log" data-id="${esc(c.id)}" data-tip="Its output">≡</button>`
      : '';
  return `<div class="run-row${running ? ' running' : ''}"><button class="run-main" data-act="run-start" data-id="${esc(c.id)}"
      data-tip="${esc(running ? (s.url ? 'Running: show it' : 'Running: show its output') : (c.command || 'The built-in server serves this folder'))}">
      <span class="run-kind">${RUN_KIND[c.kind] || '▶'}</span><span class="run-name">${esc(c.name)}</span><code class="run-cmd">${esc(c.command || 'built-in server')}</code></button>
      ${acts ? `<span class="run-acts">${acts}</span>` : ''}</div>`;
}

/** The configurations grouped by project (a heading for each when the workspace has several). */
function runGroupsHtml(configs) {
  const groups = new Map();
  for (const c of configs) { const k = c.projectName || ''; if (!groups.has(k)) groups.set(k, []); groups.get(k).push(c); }
  if (groups.size < 2) return configs.map(runRowHtml).join('');
  return [...groups.entries()].map(([name, list]) => `<div class="run-group">${esc(name || 'Project')}</div>${list.map(runRowHtml).join('')}`).join('');
}

function renderRunMenu() {
  const menu = $('#run-menu');
  if (!menu || menu.classList.contains('hidden')) return;
  const rc = runClient();
  menu.innerHTML = `<div class="menu-title">Run this project</div>
    ${RUN.configs.length ? runGroupsHtml(RUN.configs) : '<p class="muted small run-empty">No way to run it was found in this folder.</p>'}
    <div class="menu-sep"></div>
    <button class="run-extra" data-act="run-setup">⚙ ${RUN.configs.length ? 'Fix how it runs' : 'Set up how to run it'} <span class="muted small">(the model writes .agentic/launch.json)</span></button>
    ${rc && rc.edit ? '<button class="run-extra" data-act="run-edit">✎ Edit launch.json</button>' : ''}
    <button class="run-extra" data-act="go-artifacts">▦ All projects and artifacts <span class="muted small">(run, share)</span></button>`;
}

/** Starts a configuration (or shows it when it runs already). */
async function startRun(id) {
  const rc = runClient();
  const c = RUN.configs.find(x => x.id === id) || { name: id, kind: 'app' };
  const s = RUN.status.find(x => x.id === id && x.running);
  // Running already: show it.
  if (s) { if (s.url) rc.preview(s.url, c.name); else showRunLog(id); return; }
  try { await rc.start(id); } catch (e) { toast(e.message || String(e), 'bad'); return; }
  RUN.autoOpen.add(id);
  RUN.opened.delete(id);
  RUN.announced.delete(id);
  toast(`Starting ${c.name}…`, 'ok');
  if (c.kind === 'test' || c.kind === 'app') showRunLog(id);
  await refreshRun(false);
  renderRunMenu();
  pollRun();
}

ACTIONS['run-menu'] = () => openRunMenu();
ACTIONS['run-start'] = el => startRun(el.dataset.id);
ACTIONS['run-stop'] = async el => {
  try { await runClient().stop(el.dataset.id); } catch (e) { toast(e.message || String(e), 'bad'); }
  RUN.autoOpen.delete(el.dataset.id);
  await refreshRun(false);
  renderRunMenu();
};
ACTIONS['run-preview'] = el => { try { runClient().preview(el.dataset.url, el.dataset.name || el.dataset.url); } catch (e) { toast(e.message || String(e), 'bad'); } };
ACTIONS['run-log'] = el => showRunLog(el.dataset.id);
ACTIONS['run-setup'] = () => { $('#run-menu')?.classList.add('hidden'); if (typeof sendMessage === 'function' && $('#chat-input')) sendMessage(false, RUN_SETUP_PROMPT); else { navigate('/chat'); setTimeout(() => sendMessage(false, RUN_SETUP_PROMPT), 600); } };
ACTIONS['run-edit'] = async () => { $('#run-menu')?.classList.add('hidden'); try { await runClient().edit(); } catch (e) { toast(e.message || String(e), 'bad'); } };
ACTIONS['go-artifacts'] = () => { $('#run-menu')?.classList.add('hidden'); navigate('/artifacts'); };

/** A run's output as it comes, with Stop, Restart and "Ask Agentic to fix it". */
async function showRunLog(id) {
  const rc = runClient();
  const c = RUN.configs.find(x => x.id === id) || { name: id, command: '' };
  RUNLOG.id = id;
  RUNLOG.from = 0;
  openModal(c.name, `<div class="run-log-head" id="run-log-head"></div><pre class="run-log" id="run-log"></pre>`,
    `<button class="btn" data-act="run-fix" data-id="${esc(id)}">Ask Agentic to fix it</button><button class="btn" data-act="run-restart" data-id="${esc(id)}">Restart</button>` +
    `<button class="btn danger" data-act="run-stop" data-id="${esc(id)}">Stop</button><button class="btn primary" data-act="modal-close">Close</button>`, 'wide');
  clearInterval(RUNLOG.timer);
  const tick = async () => {
    const box = $('#run-log');
    if (!box || RUNLOG.id !== id) { clearInterval(RUNLOG.timer); return; }
    let r;
    try { r = await rc.log(id, RUNLOG.from); } catch { return; }
    if (r && r.text) {
      const stick = box.scrollHeight - box.scrollTop - box.clientHeight < 40;
      box.textContent += r.text;
      if (box.textContent.length > 300000) box.textContent = box.textContent.slice(-200000);
      if (stick) box.scrollTop = box.scrollHeight;
    }
    if (r) RUNLOG.from = r.end;
    const head = $('#run-log-head');
    if (head && r) head.innerHTML = r.running
      ? `<span class="badge ok">Running</span>${r.url ? ` <button class="btn xs" data-act="run-preview" data-url="${esc(r.url)}" data-name="${esc(c.name)}">↗ ${esc(r.url)}</button>` : ''} <code class="muted small">${esc(c.command || '')}</code>`
      : r.exitCode == null ? '<span class="badge">Not running</span>' : `<span class="badge ${r.exitCode === 0 ? 'ok' : 'bad'}">Exited with code ${r.exitCode}</span> <code class="muted small">${esc(c.command || '')}</code>`;
  };
  await tick();
  RUNLOG.timer = setInterval(tick, 1000);
}
ACTIONS['run-restart'] = async el => {
  const id = el.dataset.id;
  try { await runClient().start(id); } catch (e) { toast(e.message || String(e), 'bad'); return; }
  RUNLOG.from = 0;
  const box = $('#run-log'); if (box) box.textContent = '';
  RUN.autoOpen.add(id); RUN.opened.delete(id); RUN.announced.delete(id);
  pollRun();
};
ACTIONS['run-fix'] = el => {
  const id = el.dataset.id;
  const c = RUN.configs.find(x => x.id === id) || { name: id, command: '' };
  const out = ($('#run-log')?.textContent || '').split('\n').slice(-80).join('\n').trim();
  closeModal();
  const prompt = `Running "${c.name}" (\`${c.command || 'the built-in server'}\`) did not work as it should. Its last output:\n\n\`\`\`\n${out || '(no output)'}\n\`\`\`\n\nFind the cause and fix it, then run it again to check.`;
  if ($('#chat-input')) sendMessage(false, prompt);
  else { navigate('/chat'); setTimeout(() => sendMessage(false, prompt), 600); }
};

/** Shares a project's website (its static files) as a public link; the same project shared again keeps its link. */
async function shareProject(projectId) {
  const rc = runClient();
  if (!rc || !rc.share) { toast('Sharing projects needs a newer desktop app or VS Code extension.', 'bad'); return null; }
  toast('Uploading the website…');
  try {
    const r = await rc.share(projectId);
    const link = r && (r.link || r);
    if (link && link.url) {
      const copied = await writeClipboard(link.url);
      toast(`${r.updated ? 'Link updated' : 'Shared'}: ${link.url}${copied ? ' (copied)' : ''}`, 'ok');
    }
    return r;
  } catch (e) { toast(e.message || String(e), 'bad'); return null; }
}

/** /run: the Run menu; /run <name> starts that way to run it; /run stop [name]; /run share [project]. */
async function cmdRun(arg) {
  const rc = runClient();
  if (!rc) { toast('Running projects works in the desktop app, VS Code and the CLI.', 'bad'); return; }
  const a = (arg || '').trim();
  if (!a) { const b = $('#run-btn'); if (b) b.click(); else navigate('/artifacts'); return; }
  await refreshRun(true);
  const [verb, ...rest] = a.split(/\s+/);
  const find = q => { const s = q.toLowerCase(); return RUN.configs.find(c => c.id === s || c.name.toLowerCase() === s) || RUN.configs.find(c => c.name.toLowerCase().includes(s) || (c.command || '').toLowerCase().includes(s)); };
  if (verb.toLowerCase() === 'stop') {
    const live = RUN.status.filter(s => s.running), name = rest.join(' ');
    const targets = name ? live.filter(s => { const c = find(name); return c && c.id === s.id; }) : live;
    if (!targets.length) { toast(name ? `${name} is not running.` : 'Nothing is running.', 'bad'); return; }
    for (const s of targets) { try { await rc.stop(s.id); } catch (e) { toast(e.message || String(e), 'bad'); } RUN.autoOpen.delete(s.id); }
    toast(`Stopped ${targets.map(s => s.name).join(', ')}`, 'ok');
    await refreshRun(false);
    return;
  }
  if (verb.toLowerCase() === 'share') {
    const projects = rc.projects ? (await rc.projects()) || [] : [];
    const q = rest.join(' ').toLowerCase();
    const p = q ? projects.find(x => x.name.toLowerCase().includes(q)) : projects.find(x => x.share) || projects[0];
    if (!p) { toast(q ? `No project "${q}".` : 'No project to share.', 'bad'); return; }
    await shareProject(p.id);
    return;
  }
  const c = find(a);
  if (!c) { toast(`No way to run "${a}". Known: ${RUN.configs.map(x => x.name).join(', ') || 'none'}.`, 'bad'); return; }
  await startRun(c.id);
}

// The chip shows what runs after a reload too.
document.addEventListener('DOMContentLoaded', () => setTimeout(async () => {
  if (!runClient()) return;
  try { await refreshRun(false); } catch { /* the app starts up */ }
  pollRun();
}, 1500));

if (typeof CHAT_COMMANDS !== 'undefined') {
  CHAT_COMMANDS.push({ name: 'run', args: '[name] | stop [name] | share [project]', group: 'Work',
    desc: 'Run or test this project (a dev server with a preview, the app, its tests), stop it, or share its website', when: () => !!runClient(), run: cmdRun });
  CHAT_COMMANDS.push({ name: 'artifacts', group: 'Work', desc: 'Your artifacts: pages from your chats and projects on this computer, to run or share',
    run: () => navigate('/artifacts' + (CHAT.conv ? '?chat=' + encodeURIComponent(CHAT.conv.id) : '')) });
}
