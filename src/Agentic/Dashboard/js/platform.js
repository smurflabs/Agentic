'use strict';
/* Agentic console - Platform: custom models (one model name that routes each request to the right model), the Swagger
   API reference, and personal API keys. */

// ================================================================== API key (user menu)
ACTIONS['api-key'] = async () => {
  $('#user-menu').classList.add('hidden');
  const k = await get('/api/auth/api-key');
  openModal('Your API key', apiKeyBody(k, null), apiKeyActions(k), 'narrow');
};
function apiKeyBody(k, fresh) {
  const host = serverOrigin();
  return `<div class="stack">
    <p class="muted">Your key works on every API endpoint with <b>your</b> role and permissions — the same things you can do in this dashboard. Send it as
      <code>Authorization: Bearer agk_…</code> or <code>x-api-key: agk_…</code>. The OpenAI-compatible API is at <code>${esc(host)}/v1</code>.</p>
    ${fresh ? `<div class="notice ok"><b>Copy your key now — it is not shown again.</b><div class="key-box"><code id="new-key">${esc(fresh)}</code>
        <button class="btn xs" data-act="copy-text" data-text="${esc(fresh)}">Copy</button></div></div>` : ''}
    <dl class="kv"><dt>Status</dt><dd>${k.hasKey ? badge('Active', 'ok') + ` <code>${esc(k.prefix)}…</code>` : badge('No key', 'outline')}</dd>
      ${k.hasKey ? `<dt>Created</dt><dd>${esc(ts(k.createdUtc))}</dd><dt>Last used</dt><dd>${k.lastUsedUtc ? esc(ago(k.lastUsedUtc)) : 'never'}</dd>` : ''}</dl>
    <details${fresh ? ' open' : ''}><summary>Examples</summary>
<pre>curl ${esc(host)}/v1/models -H "Authorization: Bearer ${esc(fresh || 'agk_…')}"

curl ${esc(host)}/v1/chat/completions -H "Authorization: Bearer ${esc(fresh || 'agk_…')}" \\
  -H "Content-Type: application/json" \\
  -d '{"model":"lmzh/gpt-4.1-mini","stream":true,"messages":[{"role":"user","content":"Hello"}]}'

# Python (openai package)
from openai import OpenAI
client = OpenAI(base_url="${esc(host)}/v1", api_key="${esc(fresh || 'agk_…')}")</pre></details>
    ${link('/swagger', 'Open the API reference (Swagger)', 'btn small')}</div>`;
}
function apiKeyActions(k) {
  return `<button class="btn" data-act="modal-close">Close</button>${k.hasKey ? '<button class="btn danger" data-act="api-key-revoke">Revoke</button>' : ''}
    <button class="btn primary" data-act="api-key-create">${k.hasKey ? 'Regenerate key' : 'Generate key'}</button>`;
}
ACTIONS['api-key-create'] = async () => {
  const cur = await get('/api/auth/api-key');
  if (cur.hasKey && !await confirmBox('Regenerate API key', 'The current key stops working immediately. Continue?', { confirm: 'Regenerate', danger: true })) return;
  const r = await post('/api/auth/api-key');
  const k = await get('/api/auth/api-key');
  openModal('Your API key', apiKeyBody(k, r.key), apiKeyActions(k), 'narrow');
};
ACTIONS['api-key-revoke'] = async () => {
  if (!await confirmBox('Revoke API key', 'Programs using this key stop working immediately.', { confirm: 'Revoke', danger: true })) return;
  await del('/api/auth/api-key');
  toast('API key revoked', 'ok');
  const k = await get('/api/auth/api-key');
  openModal('Your API key', apiKeyBody(k, null), apiKeyActions(k), 'narrow');
};
ACTIONS['copy-text'] = async el => {
  try { await writeClipboardOrThrow(el.dataset.text); toast('Copied', 'ok'); } catch { toast('Copy is not available here; select and copy the text.', 'bad'); }
};

// ================================================================== Swagger
let SWAGGER_LOADED = null;
function loadScript(src) {
  return new Promise((resolve, reject) => { const s = document.createElement('script'); s.src = src; s.onload = resolve; s.onerror = () => reject(new Error('Could not load ' + src)); document.head.appendChild(s); });
}
function loadStyle(href) { if (!$(`link[href="${href}"]`)) { const l = document.createElement('link'); l.rel = 'stylesheet'; l.href = href; document.head.appendChild(l); } }
VIEWS.swagger = {
  live: false,
  async render() {
    setCrumbs([['Swagger', '/swagger']]);
    const k = await get('/api/auth/api-key').catch(() => ({ hasKey: false }));
    return pageHead('API reference', {
      subtitle: 'Every action in this dashboard is an API endpoint with the same permissions. Try requests right here: they run as you.',
      actions: `<a class="btn" href="/api/openapi.json" target="_blank" rel="noopener">${icon('download')} openapi.json</a><button class="btn primary" data-act="api-key">${k.hasKey ? 'Manage API key' : 'Get an API key'}</button>`
    }) + `<div class="grid three section">
        ${card('Authenticate', `<p class="small">Send your key as <code>Authorization: Bearer agk_…</code> or <code>x-api-key</code>. In this page, requests use your signed-in session.</p>
          <p class="small">${k.hasKey ? `Your key: <code>${esc(k.prefix)}…</code> · last used ${k.lastUsedUtc ? esc(ago(k.lastUsedUtc)) : 'never'}` : 'You have no API key yet.'}</p>`)}
        ${card('OpenAI-compatible', `<p class="small">Base URL <code>${esc(serverOrigin())}/v1</code> — works with OpenAI SDKs and tools (LibreChat, Continue, LangChain …). Every provider model and every custom model, with streaming.</p>`)}
        ${card('Permissions', '<p class="small">Each operation lists the permission it needs. Calls you are not allowed to make return 403, exactly as in the dashboard.</p>')}
      </div><div class="card section swagger-card"><div id="swagger-ui"><div class="empty">Loading the API reference…</div></div></div>`;
  },
  async after() {
    loadStyle('/assets/vendor/swagger/swagger-ui.css');
    try {
      if (!SWAGGER_LOADED) SWAGGER_LOADED = loadScript('/assets/vendor/swagger/swagger-ui-bundle.js');
      await SWAGGER_LOADED;
      window.SwaggerUIBundle({
        url: '/api/openapi.json', dom_id: '#swagger-ui', deepLinking: false, docExpansion: 'list', filter: true, tryItOutEnabled: true, displayRequestDuration: true,
        persistAuthorization: true, defaultModelsExpandDepth: 0,
        requestInterceptor: req => { req.credentials = 'same-origin'; if (S.csrf && !req.headers['X-CSRF-Token']) req.headers['X-CSRF-Token'] = S.csrf; return req; }
      });
    } catch (e) { $('#swagger-ui').innerHTML = `<div class="notice bad">${esc(e.message)}</div>`; }
  }
};

// ================================================================== Apps & updates
const APP_NAMES = { desktop: 'Agentic Desktop', cli: 'Agentic CLI', vscode: 'Agentic for VS Code' };
const PLATFORM_NAMES = { 'win-x64': 'Windows', 'win-arm64': 'Windows (ARM)', 'linux-x64': 'Linux', 'linux-arm64': 'Linux (ARM64)', 'osx-x64': 'macOS (Intel)', 'osx-arm64': 'macOS (Apple Silicon)', any: 'VS Code (any OS)' };
function fileSize(n) { if (!n && n !== 0) return '—'; const u = ['B', 'KB', 'MB', 'GB']; let i = 0; while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; } return `${n.toFixed(i ? 1 : 0)} ${u[i]}`; }
/** How to install one build: extract the zip on Windows; on Linux unpack the .tar.gz (it keeps the binary executable). */
function installHint(a) {
  // The VS Code extension is one .vsix for every platform: installed by VS Code itself.
  if (a.archiveType === 'file') return `In VS Code: Extensions view → <b>⋯</b> → <b>Install from VSIX…</b>, or run <code>code --install-extension ${esc(a.zipName)}</code>. Then run <b>Agentic: Sign In</b> with this server's address and an API key. It updates itself from this server.`;
  if (a.archiveType === 'zip') return `Extract the zip to a folder of its own and run <code>${esc(a.fileName)}</code>${a.files.length ? ` (it keeps its <code>${esc(a.managedFolders.join(', '))}</code> folder next to it)` : ''}.`;
  const folder = (a.zipName || '').replace(/-[\d.]+-[a-z0-9-]+\.tar\.gz$/, '');
  const run = `<code>tar -xzf ${esc(a.zipName)}</code>, then run <code>./${esc(folder)}/${esc(a.fileName)}</code>`;
  // Unsigned macOS binaries are quarantined when downloaded; clear it once so Gatekeeper lets them run.
  const mac = (a.platform || '').startsWith('osx') ? ` On macOS, first clear the download quarantine: <code>xattr -dr com.apple.quarantine ./${esc(folder)}</code>.` : '';
  return (a.baseApp || a.app) === 'desktop'
    ? `${run}. Its window is a Chrome, Chromium, Edge, Brave or Vivaldi app window (else your default browser); <code>--no-window</code> runs only its local server.${mac}`
    : `${run}, or link it onto your PATH (<code>ln -s "$PWD/${esc(folder)}/${esc(a.fileName)}" ~/.local/bin/agentic</code>). Keep the folder yours so it can update itself.${mac}`;
}
// The ready-to-paste Claude Code configs. The model-picker list comes from /api/claude-code/models, so it tracks the
// models this account can use as they are added and removed — nothing is hard-coded.
function claudeCodeSetupCard(models) {
  const base = serverOrigin().replace(/\/+$/, '') + '/';
  const vscode = {
    'agentic.serverUrl': base,
    'claudeCode.preferredLocation': 'panel',
    'claudeCode.environmentVariables': [
      { name: 'ANTHROPIC_BASE_URL', value: base },
      { name: 'ANTHROPIC_AUTH_TOKEN', value: 'agk_YOUR_AGENTIC_KEY' },
      { name: 'CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY', value: '1' },
      { name: 'CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC', value: '1' },
      { name: 'CLAUDE_CODE_ATTRIBUTION_HEADER', value: '0' },
      { name: 'DISABLE_TELEMETRY', value: '1' },
      { name: 'DISABLE_ERROR_REPORTING', value: '1' },
      { name: 'DISABLE_AUTOUPDATER', value: '1' },
      { name: 'DISABLE_BUG_COMMAND', value: '1' },
      { name: 'DISABLE_COST_WARNINGS', value: '1' },
      { name: 'DISABLE_NON_ESSENTIAL_MODEL_CALLS', value: '1' },
      { name: 'CLAUDE_CODE_AUTO_MODE_SERVER', value: '0' }
    ]
  };
  const settings = {
    enabledPlugins: {},
    switchModelsOnFlag: true,
    model: 'opus',
    modelPicker: {
      replaceBuiltInOptions: true,
      options: (models || []).map(m => ({ model: m.model, label: m.label || m.model }))
    }
  };
  const vscodeJson = JSON.stringify(vscode, null, 2);
  const settingsJson = JSON.stringify(settings, null, 2);
  const cliEnv = `# macOS / Linux (bash, zsh)\nexport ANTHROPIC_BASE_URL="${base}"\nexport ANTHROPIC_AUTH_TOKEN="agk_YOUR_AGENTIC_KEY"\n\n# Windows (PowerShell; persists, reopen the terminal after)\nsetx ANTHROPIC_BASE_URL "${base}"\nsetx ANTHROPIC_AUTH_TOKEN "agk_YOUR_AGENTIC_KEY"`;
  const block = text => `<div class="stack cc-block"><div class="row"><span class="spacer"></span><button class="btn xs ghost" data-act="copy-text" data-text="${esc(text)}">Copy</button></div><pre>${jsonOr(text)}</pre></div>`;
  const n = (models || []).length;
  return card('Use Agentic from Claude Code', `
    <p class="small">Point Claude Code — the CLI and the VS Code extension — at this server. Make an API key under <a href="/account" data-nav>Account</a> and put it where it says <code>agk_YOUR_AGENTIC_KEY</code>. The model picker below is built from the ${num(n)} model${n === 1 ? '' : 's'} your account can use right now and updates on its own as models come and go.</p>
    <p><b>VS Code extension</b></p>
    <p class="small">Open <b>Settings (JSON)</b> (<code>Ctrl/Cmd+Shift+P → Preferences: Open User Settings (JSON)</code>) and merge in:</p>
    ${block(vscodeJson)}
    <p><b>Claude Code CLI</b></p>
    <p class="small">Set the base URL and your key, then run <code>claude</code> in any project:</p>
    ${block(cliEnv)}
    <p><b>Model picker — <code>~/.claude/settings.json</code></b> (used by both the CLI and VS Code)</p>
    <p class="small">On Windows this file is <code>%USERPROFILE%\\.claude\\settings.json</code>. It puts every Agentic model in the <code>/model</code> picker:</p>
    ${block(settingsJson)}`, { cls: 'section' });
}
// One tab per app (desktop, CLI, VS Code) plus Claude Code: each with its download, platforms, install steps and
// changelog. The changelog comes from updates\changelog.json (publish scripts add to it; the server records versions
// that arrive without an entry).
const UPD_TABS = ['desktop', 'cli', 'vscode', 'claude'];
const UPD_MARKS = {
  desktop: '<rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8M12 16v4"/>',
  cli: '<rect x="3" y="4" width="18" height="16" rx="2"/><path d="m7 9 3 3-3 3M12 15h5"/>',
  vscode: '<path d="m16 3 5 2.5v13L16 21 6 12z"/><path d="M16 3v18M6 12 3 9.5M6 12l-3 2.5"/>',
  claude: '<path d="M12 3v4M12 17v4M3 12h4M17 12h4M5.6 5.6l2.8 2.8M15.6 15.6l2.8 2.8M5.6 18.4l2.8-2.8M15.6 8.4l2.8-2.8"/>'
};
const UPD_INFO = {
  desktop: {
    name: 'Agentic Desktop', short: 'Desktop', tab: 'Agentic Desktop',
    tagline: 'The whole Agentic workspace as a desktop app: chat with agents and models, with your own files, commands and screen at hand.',
    highlights: [['Chat, agents and global tasks', 'Everything the web app does, in its own window.'], ['Local tools', 'Reads and edits files, runs commands, takes screenshots on your machine.'],
      ['Run, test and preview', 'Starts the project\'s app and shows it in a side pane.'], ['Notifications', 'Tells you when a chat finishes or needs an answer.'], ['Updates itself', 'Downloads new builds from this server and checks them before installing.']]
  },
  cli: {
    name: 'Agentic CLI', short: 'CLI', tab: 'Agentic CLI',
    tagline: 'An agentic coding assistant in your terminal: it reads, edits and runs your project with this server\'s models and agents.',
    highlights: [['Works in any project', 'Run agentic in a folder; it plans, edits and tests there.'], ['Parallel subagents', 'Explores and builds with several agents at once, streamed live.'],
      ['Plans and progress', 'A task list with a progress gauge on the working line.'], ['Git worktrees', '--worktree runs a session on its own branch.'], ['Rebindable keys', '/keys changes any of 43 keyboard actions.'], ['Updates itself', 'Checks this server when a session starts; agentic update on demand.']]
  },
  vscode: {
    name: 'Agentic for VS Code', short: 'VS Code', tab: 'Agentic VS Code',
    tagline: 'Agentic inside VS Code: chat, workspace tools and multitask next to your code, one device per workspace.',
    highlights: [['Chat in the panel', 'The full chat, plans and approvals beside your editor.'], ['Workspace tools', 'Edits files, runs commands and reads diagnostics in the open workspace.'],
      ['Run view', 'Start, test and preview the workspace\'s app.'], ['Right window, every time', 'Each window is its own device, so tool calls reach the project that asked.'], ['Updates itself', 'Installs new .vsix builds from this server.']]
  },
  claude: {
    name: 'Claude Code', short: 'Claude', tab: 'Claude',
    tagline: 'Use Claude Code — the CLI and the VS Code extension — with this server\'s models. One API key; the model picker follows the models your account can use.',
    highlights: [['Every model you can use', 'The picker lists this server\'s models and keeps itself current.'], ['Your key, your limits', 'Calls count against your account\'s usage and limits.'],
      ['CLI and VS Code', 'The same settings work in both.'], ['No telemetry', 'The settings below turn off Claude Code\'s outside traffic.']]
  }
};
const UPD_OS = [['win', 'Windows'], ['linux', 'Linux'], ['osx', 'macOS'], ['any', 'Any OS']];
/** The platform this browser runs on, to offer its build first. */
function updMyPlatform() {
  const p = ((navigator.userAgentData && navigator.userAgentData.platform) || navigator.platform || '').toLowerCase();
  return p.startsWith('win') ? 'win-x64' : p.startsWith('mac') ? 'osx-arm64' : p.includes('linux') ? 'linux-x64' : 'win-x64';
}
function updDate(iso) { const d = new Date(iso); return isNaN(d) ? '' : d.toLocaleDateString([], { year: 'numeric', month: 'short', day: 'numeric' }); }
function updMark(app, cls = '') { return `<span class="upd-mark ${cls}" data-app="${esc(app)}"><svg viewBox="0 0 24 24" aria-hidden="true">${UPD_MARKS[app] || ''}</svg></span>`; }
/** A changelog entry's points: its features and fixes, else its notes split into sentences. */
function updPoints(e) {
  const f = (e.features || []).map(t => ['new', t]), x = (e.fixes || []).map(t => ['fix', t]);
  if (f.length || x.length) return f.concat(x);
  return (e.notes || '').split(/(?<=\.)\s+(?=[A-Z/`-])/).map(t => t.trim()).filter(Boolean).map(t => ['note', t]);
}
function updPointList(points) {
  const label = { new: 'New', fix: 'Fixed', note: 'Change' };
  return `<ul class="upd-points">${points.map(([k, t]) => `<li><span class="upd-kind ${k}">${label[k]}</span><span>${markdown(t).replace(/^<p>|<\/p>\s*$/g, '')}</span></li>`).join('')}</ul>`;
}
function updTimeline(entries, showApp) {
  if (!entries.length) return emptyState('No releases recorded yet', 'Each publish adds its version and what it changed.');
  entries = entries.slice().sort((a, b) => new Date(b.date) - new Date(a.date));
  return `<ol class="upd-timeline">${entries.map((e, i) => `<li class="upd-rel${i === 0 ? ' latest' : ''}">
      <div class="upd-rel-head">${showApp ? updMark(e.app === 'server' ? 'claude' : e.app, 'xs') : ''}<b class="upd-rel-ver">${showApp ? esc(e.app === 'server' ? 'Server' : UPD_INFO[e.app] ? UPD_INFO[e.app].short : e.app) + ' ' : ''}v${esc(e.version)}</b>
        ${i === 0 && !showApp ? badge('Latest', 'ok', false) : ''}${e.build ? `<code class="muted small" data-tip="Dashboard build">${esc(e.build)}</code>` : ''}<span class="spacer"></span><time class="muted small" datetime="${esc(e.date)}" data-tip="${esc(ts(e.date))}">${esc(updDate(e.date))}</time></div>
      ${e.title ? `<div class="upd-rel-title">${esc(e.title)}</div>` : ''}${updPointList(updPoints(e))}</li>`).join('')}</ol>`;
}
function updChangelogCard(all, app, scope) {
  const own = all.filter(e => e.app === (app === 'claude' ? 'server' : app));
  const list = scope === 'all' ? all : own;
  const seg = `<div class="seg" role="tablist"><button class="seg-btn${scope === 'all' ? '' : ' on'}" data-act="upd-log-scope" data-scope="app">${esc(app === 'claude' ? 'Server' : UPD_INFO[app].short)}</button><button class="seg-btn${scope === 'all' ? ' on' : ''}" data-act="upd-log-scope" data-scope="all">All apps</button></div>`;
  return card('Changelog', updTimeline(list, scope === 'all'), { cls: 'section upd-changelog', actions: seg,
    hint: `${plural(list.length, 'release')}${app === 'claude' && scope !== 'all' ? ' of the server Claude Code talks to' : ''}` });
}
function updInstallSteps(app, builds, d) {
  const msi = (d.installers || []).some(i => i.app === 'desktop'), quick = d.publicInstall && builds.length;
  const steps = {
    desktop: msi ? ['Windows: download the installer (.msi) and run it. No administrator rights needed; it adds Start menu and desktop shortcuts.', 'Linux and macOS: extract the .tar.gz to a folder of its own and run AgenticClient.', 'Sign in with this server\'s address.', 'Leave it be: it updates itself from this server.']
      : ['Download the build for your system below.', 'Extract the zip (Windows) or the .tar.gz (Linux, macOS) to a folder of its own.', 'Run AgenticClient and sign in with this server\'s address.', 'Leave it be: it updates itself from this server.'],
    cli: quick ? ['Run the one-line install for your system (above): it downloads and checks the CLI and puts it on your PATH.', 'Open a new terminal and run agentic login with an API key from Account.', 'Run agentic in a project folder.', 'It checks for updates when a session starts; agentic update installs one now.']
      : ['Download the build for your system below.', 'Extract it and put the folder on your PATH (or link agentic into ~/.local/bin).', 'Run agentic in a project folder and sign in with an API key from Account.', 'It checks for updates when a session starts; agentic update installs one now.'],
    vscode: ['Download agentic-vscode.vsix below.', 'In VS Code: Extensions view → ⋯ → Install from VSIX…', 'Run "Agentic: Sign In" with this server\'s address and an API key.', 'New versions install themselves from this server.']
  }[app] || [];
  return card('Install', `<ol class="upd-steps">${steps.map(s => `<li>${esc(s)}</li>`).join('')}</ol>${builds.length ? '' : '<p class="small muted">No build is published yet.</p>'}`, { cls: 'section' });
}
/** A command to paste, with a copy button. */
function updCmd(label, text) {
  return `<div class="upd-cmd"><div class="upd-cmd-head"><span>${esc(label)}</span><button class="btn xs ghost" data-act="copy-text" data-text="${esc(text)}">Copy</button></div><pre><code>${esc(text)}</code></pre></div>`;
}
function updCliQuickInstall(d) {
  const o = (d.origin || serverOrigin()).replace(/\/+$/, '');
  return card('Install with one command', `<p class="small">Downloads the CLI for your system from this server, checks its SHA-256, puts it on your PATH and points it at this server.</p>
    <div class="upd-cmds">${updCmd('Windows · PowerShell', `irm ${o}/install.ps1 | iex`)}${updCmd('Windows · Command Prompt', `powershell -ExecutionPolicy Bypass -c "irm ${o}/install.ps1 | iex"`)}${updCmd('macOS · Linux', `curl -fsSL ${o}/install.sh | bash`)}</div>
    <p class="small muted">Windows: into <code>%LOCALAPPDATA%\\Programs\\Agentic</code>, added to your PATH (the system PATH when run as administrator), so <code>agentic</code> works in every new Command Prompt and PowerShell window. macOS and Linux: <code>~/.local/share/agentic</code>, linked as <code>~/.local/bin/agentic</code>. Then run <code>agentic login</code>.</p>`, { cls: 'section', id: 'upd-quick' });
}
function updClaudeInstall() {
  return card('Install Claude Code', `<p class="small">No Claude Code yet? Install it with Anthropic's installer, then connect it to this server below.</p>
    <div class="upd-cmds">${updCmd('Windows · PowerShell', 'irm https://claude.ai/install.ps1 | iex')}${updCmd('Windows · Command Prompt', 'powershell -ExecutionPolicy Bypass -c "irm https://claude.ai/install.ps1 | iex"')}${updCmd('macOS · Linux · WSL', 'curl -fsSL https://claude.ai/install.sh | bash')}</div>
    <p class="small muted">Then open a new terminal and run <code>claude</code>.</p>`, { cls: 'section', id: 'cc-install' });
}
/** The desktop app's Windows installer, first in its downloads. */
function updMsiRow(m) {
  return `<div class="upd-platform${updMyPlatform().startsWith('win') ? ' mine' : ''}">
      <div class="upd-os" data-os="win">MSI</div>
      <div class="upd-platform-main"><div class="row"><b>Windows installer</b>${tag('Recommended', 'ok')}<span class="muted small">v${esc(m.version)}</span></div>
        <div class="small muted">${esc(m.fileName)} · ${esc(fileSize(m.size))} · built ${esc(ago(m.publishedUtc))} · SHA-256 <code data-tip="${esc(m.sha256)}">${esc(m.sha256.slice(0, 12))}…</code></div>
        <details class="small"><summary>How to install</summary><div>Run the .msi. It installs for your account only (no administrator rights) into <code>%LOCALAPPDATA%\\Programs\\Agentic Desktop</code> with Start menu and desktop shortcuts, and the app keeps itself up to date. If Windows SmartScreen stops it, choose <b>More info → Run anyway</b>. Uninstall it from Settings → Apps.</div></details></div>
      <a class="btn primary" href="${esc(m.url)}" download="${esc(m.fileName)}">${icon('download')} Download</a>
    </div>`;
}
function updHighlights(app) {
  return card('Highlights', `<ul class="upd-highlights">${UPD_INFO[app].highlights.map(([h, t]) => `<li>${icon('check')}<div><b>${esc(h)}</b><span>${esc(t)}</span></div></li>`).join('')}</ul>`, { cls: 'section' });
}
function updBuildRows(builds, hasMsi) {
  const mine = updMyPlatform();
  const order = b => b.platform === mine || b.platform === 'any' ? 0 : (b.platform || '').startsWith('win') ? 1 : (b.platform || '').startsWith('linux') ? 2 : 3;
  return `<div class="upd-platforms">${builds.slice().sort((x, y) => order(x) - order(y) || (x.platform || '').localeCompare(y.platform || '')).map(a => `
    <div class="upd-platform${a.platform === mine ? ' mine' : ''}">
      <div class="upd-os" data-os="${esc((UPD_OS.find(([k]) => (a.platform || '').startsWith(k)) || ['any'])[0])}">${esc((UPD_OS.find(([k]) => (a.platform || '').startsWith(k)) || ['', 'Build'])[1])}</div>
      <div class="upd-platform-main"><div class="row"><b>${esc(PLATFORM_NAMES[a.platform] || a.platform || 'Windows')}</b>${a.platform === mine ? tag('Your system', 'info') : ''}<span class="muted small">v${esc(a.version)}</span></div>
        <div class="small muted">${esc(a.zipName || a.fileName)} · ${esc(fileSize(a.size))} · built ${esc(ago(a.publishedUtc))} · SHA-256 <code data-tip="${esc(a.sha256)}">${esc(a.sha256.slice(0, 12))}…</code>${a.minimumVersion ? ` · required from ${esc(a.minimumVersion)}` : ''}</div>
        <details class="small"><summary>How to install</summary><div>${installHint(a)}</div></details></div>
      <a class="btn${(a.platform === mine && !(hasMsi && mine.startsWith('win'))) || a.platform === 'any' ? ' primary' : ''}" href="${esc(a.zipUrl)}" download="${esc(a.zipName)}">${icon('download')} Download</a>
    </div>`).join('')}</div>`;
}
function updHero(app, latest, builds, entry, extra) {
  const info = UPD_INFO[app], mine = updMyPlatform();
  const best = builds.find(b => b.platform === mine) || builds.find(b => b.platform === 'any') || builds[0];
  const msi = extra && extra.msi;
  const cta = extra && extra.cta ? extra.cta : msi && mine.startsWith('win') ? `<a class="btn primary lg" href="${esc(msi.url)}" download="${esc(msi.fileName)}">${icon('download')} Download installer (.msi)</a>
      <button class="btn ghost" data-act="upd-scroll" data-target="#upd-downloads">${plural(builds.length + 1, 'download')}</button>` : best ? `<a class="btn primary lg" href="${esc(best.zipUrl)}" download="${esc(best.zipName)}">${icon('download')} Download for ${esc(PLATFORM_NAMES[best.platform] || 'your system')}</a>
      ${builds.length > 1 ? `<button class="btn ghost" data-act="upd-scroll" data-target="#upd-downloads">${plural(builds.length, 'platform')}</button>` : ''}` : '<span class="muted">No build published yet.</span>';
  const facts = (extra && extra.facts) || [
    ['Latest version', latest ? 'v' + esc(latest.version) : '—'], ['Released', latest ? esc(updDate(latest.publishedUtc)) + `<span class="muted small"> · ${esc(ago(latest.publishedUtc))}</span>` : '—'],
    ['Platforms', builds.length ? esc(builds.map(b => (PLATFORM_NAMES[b.platform] || b.platform).replace(/ \(.*\)$/, '')).filter((v, i, a) => a.indexOf(v) === i).join(', ')) : '—'],
    ['Download size', best ? esc(fileSize(best.size)) : '—']];
  return `<section class="upd-hero" data-app="${esc(app)}">
    <div class="upd-hero-main">${updMark(app, 'lg')}<div class="stack">
      <div class="upd-hero-title"><h2>${esc(info.name)}</h2>${latest ? `<span class="upd-ver-pill">v${esc(latest.version)}</span>` : ''}${extra && extra.badges ? extra.badges : ''}</div>
      <p class="upd-tagline">${esc(info.tagline)}</p>
      ${entry && entry.title ? `<div class="small muted">Latest: <b>${esc(entry.title)}</b></div>` : ''}
      <div class="row upd-cta">${cta}</div></div></div>
    <dl class="upd-facts">${facts.map(([k, v]) => `<div><dt>${esc(k)}</dt><dd>${v}</dd></div>`).join('')}</dl></section>`;
}
function updInstallStats(app, installs) {
  if (!installs) return '';
  const rows = installs.installs.filter(i => (i.app || '').split('-')[0] === app);
  if (!rows.length) return '';
  const current = rows.filter(r => r.state === 'current').length, outdated = rows.filter(r => r.state === 'outdated').length;
  const versions = {};
  rows.forEach(r => { versions[r.version || '?'] = (versions[r.version || '?'] || 0) + 1; });
  return card('Installs', `<div class="upd-install-kpis"><div><b>${num(rows.length)}</b><span>seen in ${num(installs.days)} days</span></div><div><b class="ok-text">${num(current)}</b><span>up to date</span></div><div><b class="${outdated ? 'warn-text' : ''}">${num(outdated)}</b><span>outdated</span></div></div>
    <div class="chips">${Object.entries(versions).sort((a, b) => b[1] - a[1]).slice(0, 6).map(([v, n]) => tag(`v${v} · ${n}`)).join('')}</div>`, { cls: 'section', hint: 'admins only' });
}
VIEWS.updates = {
  live: false,
  async render(p) {
    let app = p.args[0];
    if (!UPD_TABS.includes(app)) {
      app = UPD_TABS.includes(LS.get('updates.tab')) ? LS.get('updates.tab') : 'desktop';
      history.replaceState({}, '', '/updates/' + app);
      Object.assign(p, { path: '/updates/' + app, args: [app] });   // in place: a new S.params would drop this render as stale
    }
    LS.set('updates.tab', app);
    const info = UPD_INFO[app];
    setCrumbs([['Apps & updates', '/updates'], [info.name, '']]);
    const [d, cc, installs] = await Promise.all([get('/api/updates'), app === 'claude' ? get('/api/claude-code/models').catch(() => ({ models: [] })) : null,
      can('system.view') ? get('/api/updates/installs').catch(() => null) : null]);
    const log = (d.changelog || []).slice().sort((a, b) => new Date(b.date) - new Date(a.date));
    const builds = app === 'claude' ? [] : d.apps.filter(a => (a.baseApp || a.app) === app);
    const latestOf = k => d.apps.filter(a => (a.baseApp || a.app) === k).sort((x, y) => new Date(y.publishedUtc) - new Date(x.publishedUtc))[0];
    const tabs = `<nav class="tabs upd-tabs" aria-label="Apps">${UPD_TABS.map(k => {
      const l = k === 'claude' ? null : latestOf(k);
      return `<a href="/updates/${k}" data-nav class="${k === app ? 'active' : ''}">${updMark(k, 'xs')}${esc(UPD_INFO[k].tab)}${l ? `<span class="count">v${esc(l.version)}</span>` : ''}</a>`;
    }).join('')}</nav>`;
    const scope = LS.get('updates.logScope') === 'all' ? 'all' : 'app';
    const head = pageHead('Apps & updates', {
      subtitle: 'Download the Agentic apps for Windows, Linux and macOS, or connect Claude Code. Installed copies keep themselves up to date.',
      badges: d.server ? `<span class="upd-ver-pill subtle" data-tip="${esc(d.server.build ? 'Build ' + d.server.build : '')}">Server v${esc(d.server.version)}</span>` : ''
    }) + (d.enabled ? '' : '<div class="notice warn">Client updates are turned off (<code>Updates.Enabled</code>).</div><div class="section"></div>') + tabs;

    if (app === 'claude') {
      const models = (cc && cc.models) || [];
      const serverLog = log.filter(e => e.app === 'server');
      const hero = updHero('claude', null, [], serverLog[0], {
        cta: `<button class="btn primary lg" data-act="upd-scroll" data-target="#cc-setup">${icon('arrow')} Set up Claude Code</button><button class="btn" data-act="upd-scroll" data-target="#cc-install">${icon('download')} Install Claude Code</button><a class="btn ghost" href="/account" data-nav>Get an API key</a>`,
        facts: [['Models in the picker', num(models.length)], ['Server version', d.server ? 'v' + esc(d.server.version) : '—'],
          ['Server started', d.server ? esc(ago(d.server.startedUtc)) : '—'], ['Endpoint', `<code>${esc(serverOrigin().replace(/\/+$/, ''))}</code>`]]
      });
      const modelList = card('Models you can pick', models.length ? `<ul class="upd-models">${models.slice(0, 40).map(m => `<li><b>${esc(m.label || m.model)}</b>${m.label && m.label !== m.model ? `<code class="muted small">${esc(m.model)}</code>` : ''}</li>`).join('')}</ul>${models.length > 40 ? `<p class="small muted">and ${num(models.length - 40)} more</p>` : ''}` : emptyState('No models', 'Your account cannot use any model yet.'), { cls: 'section', hint: 'follows your account' });
      return head + hero + `<div class="upd-layout"><div class="upd-main">${updClaudeInstall()}<div id="cc-setup">${claudeCodeSetupCard(models)}</div>${updChangelogCard(log, 'claude', scope)}</div>
        <aside class="upd-side">${updHighlights('claude')}${modelList}</aside></div>`;
    }

    const latest = latestOf(app);
    const entries = log.filter(e => e.app === app);
    const entry = latest ? entries.find(e => e.version === latest.version) : entries[0];
    const whatsNew = entry ? card(`What's new in v${entry.version}`, `${entry.title ? `<div class="upd-rel-title">${esc(entry.title)}</div>` : ''}${updPointList(updPoints(entry))}`, { cls: 'section upd-new', hint: esc(updDate(entry.date)) })
      : latest && latest.notes ? card(`What's new in v${latest.version}`, `<div class="small">${markdown(latest.notes)}</div>`, { cls: 'section upd-new' }) : '';
    const msi = app === 'desktop' ? (d.installers || []).find(i => i.app === 'desktop' && i.platform === 'win-x64') : null;
    const downloads = card('Downloads', builds.length ? (msi ? `<div class="upd-platforms">${updMsiRow(msi)}</div>` : '') + updBuildRows(builds, !!msi) : emptyState('Nothing to download yet', 'No build of this app is published yet.'),
      { cls: 'section', id: 'upd-downloads', hint: builds.length ? `${plural(builds.length + (msi ? 1 : 0), 'download')} · every build checked by SHA-256` : '' });
    const quick = app === 'cli' && d.publicInstall && builds.length ? updCliQuickInstall(d) : '';
    const how = `<p class="small">Installed copies check this server when they start and every ${plural(Math.max(1, Math.round(d.checkIntervalMinutes / 60)), 'hour')}. Each build compares its version and SHA-256 with the one here, downloads what changed and checks it before installing.</p>`;
    const heroExtra = app === 'cli' && quick ? { cta: `<button class="btn primary lg" data-act="upd-scroll" data-target="#upd-quick">${icon('arrow')} Install with one command</button><button class="btn ghost" data-act="upd-scroll" data-target="#upd-downloads">${plural(builds.length, 'download')}</button>` } : { msi };
    return head + updHero(app, latest, builds, entry, heroExtra) +
      `<div class="upd-layout"><div class="upd-main">${quick}${whatsNew}${downloads}${updChangelogCard(log, app, scope)}</div>
        <aside class="upd-side">${updHighlights(app)}${updInstallSteps(app, builds, d)}${updInstallStats(app, installs)}${card('How updates work', how, { cls: 'section' })}</aside></div>`;
  }
};
ACTIONS['upd-scroll'] = el => { const t = $(el.dataset.target); if (t) t.scrollIntoView({ behavior: reducedMotion() ? 'auto' : 'smooth', block: 'start' }); };
ACTIONS['upd-log-scope'] = el => { LS.set('updates.logScope', el.dataset.scope === 'all' ? 'all' : 'app'); rerender(); };

// ================================================================== Custom models
VIEWS.customModels = {
  live: false,
  async render() {
    setCrumbs([['Custom models', '/custom-models']]);
    const [d, usage] = await Promise.all([get('/api/custom-models'), get('/api/custom-models/usage?hours=168').catch(() => ({ rows: [] }))]);
    S.cmMeta = d;
    const catLabel = Object.fromEntries(d.categories.map(c => [c.key, c.label]));
    const cards = d.models.map(m => `<div class="card cm-card${m.enabled ? '' : ' off'}">
        <div class="cm-head"><div><div class="cell-title">${esc(m.name)}</div><code class="muted small">${esc(m.id)}</code></div><span class="spacer"></span>
          ${m.enabled ? badge('Enabled', 'ok') : badge('Disabled', 'outline')} ${tag(m.definition.mode)}</div>
        ${m.description ? `<p class="small muted">${esc(m.description)}</p>` : ''}
        <div class="cm-routes">${(m.definition.routes || []).map(r => `<div class="cm-route"><span class="cat">${esc(catLabel[r.category] || r.category)}</span><span class="arrow">→</span><code>${esc(r.model)}</code>${r.fallbacks && r.fallbacks.length ? ` <span class="muted small">+${r.fallbacks.length} fallback</span>` : ''}</div>`).join('')}
          <div class="cm-route default"><span class="cat">Everything else</span><span class="arrow">→</span><code>${esc(m.definition.defaultModel)}</code></div></div>
        <div class="row">${d.canEdit ? `<a class="btn small" href="/custom-models/edit?id=${enc(m.id)}" data-nav>Edit</a><button class="btn small danger" data-act="cm-delete" data-id="${esc(m.id)}">Delete</button>` : ''}
          <a class="btn small" href="/chat?model=${enc(m.id)}" data-nav>Chat with it</a><button class="btn small ghost" data-act="copy-text" data-text="${esc(m.id)}">Copy id</button></div></div>`).join('');
    const usageRows = table([
      { h: 'Model', f: r => `<code>${esc(r.model)}</code>${r.routedModel && r.routedModel !== r.model ? `<div class="cell-sub">→ ${esc(r.routedModel)}${r.category ? ' · ' + esc(catLabel[r.category] || r.category) : ''}</div>` : ''}` },
      { h: 'User', f: r => esc(r.username) }, { h: 'Via', f: r => tag(r.source === 'chat' ? 'Chat' : 'API') },
      { h: 'Calls', num: true, f: r => num(r.calls) }, { h: 'Failed', num: true, f: r => r.failures ? `<span class="error-text">${num(r.failures)}</span>` : '0' },
      { h: 'Tokens in / out', num: true, f: r => `${num(r.promptTokens)} / ${num(r.completionTokens)}` }, { h: 'Avg time', num: true, f: r => dur(r.avgMs) }
    ], usage.rows, { empty: 'No model calls in the last 7 days.' });
    return pageHead('Custom models', {
      subtitle: 'One model name that sends every request to the model you chose for that kind of work — research, coding, images, tools, JSON, translation and more. Available to every user in Chat and the API.',
      actions: d.canEdit ? `<a class="btn primary" href="/custom-models/new" data-nav>${icon('plus')} New custom model</a>` : ''
    }) + (d.providerError ? `<div class="notice warn">The model provider is unreachable: ${esc(d.providerError)}</div><div class="section"></div>` : '') +
      `<div class="grid kpis">${kpi('Custom models', num(d.models.length), `${num(d.models.filter(m => m.enabled).length)} enabled`)}${kpi('Provider models', num(d.providerModels.length), 'at OpenWebUI')}
        ${kpi('Calls (7 days)', num(usage.rows.reduce((s, r) => s + r.calls, 0)), 'Chat and API')}${kpi('Route categories', num(d.categories.length), 'kinds of work a route can claim')}</div>` +
      (cards ? `<div class="cm-grid section">${cards}</div>` : card('', emptyState('No custom models yet', d.canEdit ? 'Create one: pick a model for research, one for code, one for images…' : 'An administrator has not created any yet.'), { cls: 'section' })) +
      card('How to use a custom model', `<pre>curl ${esc(serverOrigin())}/v1/chat/completions -H "Authorization: Bearer agk_…" -H "Content-Type: application/json" \\
  -d '{"model":"${esc(d.models[0] ? d.models[0].id : 'agentic/assistant')}","stream":true,"messages":[{"role":"user","content":"Write a SQL query for monthly revenue"}]}'</pre>
        <p class="small muted">The response says which model answered in the <code>X-Agentic-Model</code> and <code>X-Agentic-Route</code> headers. Every OpenAI parameter (tools, response_format, streaming …) is passed through.</p>`, { cls: 'section' }) +
      card('Usage (last 7 days)', usageRows, { flush: true, cls: 'section', export: 'model-usage' });
  }
};
ACTIONS['cm-delete'] = async el => {
  if (!await confirmBox('Delete custom model', `Delete <b>${esc(el.dataset.id)}</b>? Programs using this model name will get "model not found".`, { danger: true, confirm: 'Delete' })) return;
  await del('/api/custom-models/' + el.dataset.id.split('/').map(enc).join('/'));
  toast('Custom model deleted', 'ok'); rerender();
};

VIEWS.customModelEdit = {
  live: false,
  async render(p) {
    const editId = p.query.get('id');
    const d = await get('/api/custom-models?refresh=1');
    S.cmMeta = d;
    if (!d.canEdit) return pageHead('Custom model') + '<div class="notice warn">Only administrators can create and edit custom models.</div>';
    const m = editId ? d.models.find(x => x.id === editId) : null;
    if (editId && !m) return pageHead('Custom model') + '<div class="notice bad">This custom model does not exist.</div>';
    setCrumbs([['Custom models', '/custom-models'], [m ? m.name : 'New custom model', '']]);
    const def = m ? m.definition : { mode: 'hybrid', longContextTokens: 24000, routes: [], defaultFallbacks: [] };
    const routes = Object.fromEntries((def.routes || []).map(r => [r.category, r]));
    const groups = [];
    d.categories.forEach(c => { let g = groups.find(x => x.name === c.group); if (!g) groups.push(g = { name: c.group, items: [] }); g.items.push(c); });
    const modelList = `<datalist id="provider-models">${d.providerModels.map(x => `<option value="${esc(x.id)}">${esc(x.name !== x.id ? x.name : '')}</option>`).join('')}</datalist>`;
    const routeRow = c => {
      const r = routes[c.key] || {};
      return `<div class="cm-cat${routes[c.key] ? ' on' : ''}" data-cat="${esc(c.key)}">
        <label class="cm-cat-head"><input type="checkbox" name="on:${esc(c.key)}"${routes[c.key] ? ' checked' : ''} data-change="cm-toggle">
          <span><b>${esc(c.label)}</b>${c.isSignal ? ' <span class="badge info">automatic</span>' : ''}<span class="cell-sub">${esc(c.description)}</span></span></label>
        <div class="cm-cat-body form-grid">
          <label>Model<input name="model:${esc(c.key)}" list="provider-models" value="${esc(r.model || '')}" placeholder="provider model id" class="mono"></label>
          <label>Fallbacks<input name="fallbacks:${esc(c.key)}" list="provider-models" value="${esc((r.fallbacks || []).join(', '))}" placeholder="comma separated, tried in order" class="mono"></label>
          ${c.isSignal ? '' : `<label class="full">Extra trigger words<input name="keywords:${esc(c.key)}" value="${esc((r.keywords || []).join(', '))}" placeholder="comma separated; added to the built-in words below"></label>
          <details class="full small"><summary>Built-in words (${c.keywords.length})</summary><div class="chips">${c.keywords.map(k => tag(k)).join('')}</div></details>`}
          <label>Temperature<input name="temp:${esc(c.key)}" type="number" step="0.05" min="0" max="2" value="${r.temperature ?? ''}" placeholder="request's own"></label>
          <label>Max tokens<input name="max:${esc(c.key)}" type="number" min="1" value="${r.maxTokens ?? ''}" placeholder="request's own"></label>
          <label class="full">Instructions for this route<textarea name="sys:${esc(c.key)}" rows="2" placeholder="Added to the system prompt when this route answers, e.g. 'Always cite sources.'">${esc(r.systemPrompt || '')}</textarea></label>
        </div></div>`;
    };
    return pageHead(m ? 'Edit ' + m.name : 'New custom model', { subtitle: 'Choose a model for each kind of work. Requests the routes do not claim go to the default model.' }) + modelList +
      `<form id="cm-form" data-form="cm-save" data-id="${esc(m ? m.id : '')}" class="stack">
        <div class="grid side">
          ${card('Model', `<div class="form-grid">
            <label>Model id (clients use it as the model name)<input name="id" required maxlength="80" value="${esc(m ? m.id : '')}" ${m ? 'disabled' : ''} placeholder="agentic/assistant" class="mono"></label>
            <label>Display name<input name="name" required maxlength="100" value="${esc(m ? m.name : '')}" placeholder="Agentic Assistant"></label>
            <label class="full">Description<input name="description" maxlength="300" value="${esc(m ? m.description || '' : '')}" placeholder="What it is good at"></label>
            <label class="check full"><input type="checkbox" name="enabled"${!m || m.enabled ? ' checked' : ''}> Enabled (listed in Chat and /v1/models)</label>
            <label>Default model<input name="defaultModel" required list="provider-models" value="${esc(def.defaultModel || '')}" class="mono"><span class="help">Answers anything no route claims</span></label>
            <label>Default fallbacks<input name="defaultFallbacks" list="provider-models" value="${esc((def.defaultFallbacks || []).join(', '))}" class="mono" placeholder="comma separated"></label>
            <label>How requests are sorted<select name="mode" data-change="cm-mode">
              <option value="hybrid"${def.mode === 'hybrid' ? ' selected' : ''}>Hybrid — keywords when clear, otherwise ask the classifier</option>
              <option value="rules"${def.mode === 'rules' ? ' selected' : ''}>Rules — keywords and request type only (instant, free)</option>
              <option value="classifier"${def.mode === 'classifier' ? ' selected' : ''}>Classifier — a model reads every request and picks the category</option></select></label>
            <label data-classifier>Classifier model<input name="classifierModel" list="provider-models" value="${esc(def.classifierModel || '')}" class="mono"><span class="help">A fast, cheap model (not a reasoning model)</span></label>
            <label>Long context above (tokens)<input name="longContextTokens" type="number" min="1000" value="${def.longContextTokens || 24000}"></label>
            <label>Embedding model<input name="embeddingModel" list="provider-models" value="${esc(def.embeddingModel || '')}" class="mono" placeholder="for /v1/embeddings (optional)"></label>
            <label class="full">Instructions for every request<textarea name="systemPrompt" rows="2" placeholder="Prepended to the system prompt of every request">${esc(def.systemPrompt || '')}</textarea></label>
          </div>`)}
          ${card('Try it', `<div class="stack"><label>A prompt<textarea id="cm-test-prompt" rows="4" placeholder="e.g. Fix this NullReferenceException in my C# service"></textarea></label>
            <div class="row"><label class="check"><input type="checkbox" id="cm-test-img"> with an image</label><label class="check"><input type="checkbox" id="cm-test-tools"> with tools</label>
              <label class="check"><input type="checkbox" id="cm-test-json"> wants JSON</label></div>
            <button type="button" class="btn" data-act="cm-test">Which model answers?</button><div id="cm-test-out"></div></div>`, { hint: 'uses the routes as currently edited' })}
        </div>
        ${groups.map(g => card(g.name, `<div class="cm-cats">${g.items.map(routeRow).join('')}</div>`, { hint: g.name === 'Request type' ? 'detected from the request itself, checked first' : 'detected from what the request asks' })).join('')}
        <div data-errors></div>
        <div class="row sticky-actions"><button class="btn primary">${m ? 'Save changes' : 'Create custom model'}</button>${link('/custom-models', 'Cancel', 'btn')}</div>
      </form>`;
  },
  after() { const sel = $('select[name="mode"]'); if (sel) CHANGES['cm-mode'](sel); }
};
CHANGES['cm-toggle'] = el => el.closest('.cm-cat').classList.toggle('on', el.checked);
CHANGES['cm-mode'] = el => $$('[data-classifier]').forEach(x => x.classList.toggle('hidden', el.value === 'rules'));
function readCustomModel(f) {
  const fd = new FormData(f), v = k => (fd.get(k) || '').toString().trim(), list = k => v(k).split(',').map(x => x.trim()).filter(Boolean);
  const routes = S.cmMeta.categories.filter(c => fd.get('on:' + c.key) === 'on').map(c => {
    const r = { category: c.key, model: v('model:' + c.key), fallbacks: list('fallbacks:' + c.key), keywords: list('keywords:' + c.key), systemPrompt: v('sys:' + c.key) || null };
    if (v('temp:' + c.key) !== '') r.temperature = Number(v('temp:' + c.key));
    if (v('max:' + c.key) !== '') r.maxTokens = Number(v('max:' + c.key));
    return r;
  });
  return {
    id: f.dataset.id || v('id'), name: v('name'), description: v('description') || null, enabled: fd.get('enabled') === 'on',
    definition: { defaultModel: v('defaultModel'), defaultFallbacks: list('defaultFallbacks'), mode: v('mode'), classifierModel: v('classifierModel') || null,
      longContextTokens: Number(v('longContextTokens') || 24000), embeddingModel: v('embeddingModel') || null, systemPrompt: v('systemPrompt') || null, routes }
  };
}
FORMS['cm-save'] = async f => {
  const m = readCustomModel(f);
  const missing = m.definition.routes.find(r => !r.model);
  if (missing) throw new Error(`Choose a model for "${(S.cmMeta.categories.find(c => c.key === missing.category) || {}).label}".`);
  if (f.dataset.id) await put('/api/custom-models/' + f.dataset.id.split('/').map(enc).join('/'), m); else await post('/api/custom-models', m);
  toast(`Custom model ${m.id} saved`, 'ok'); navigate('/custom-models');
};
ACTIONS['cm-test'] = async () => {
  const f = $('#cm-form'), out = $('#cm-test-out');
  const m = readCustomModel(f);
  out.innerHTML = '<div class="muted small">Asking the router…</div>';
  try {
    const r = await post('/api/custom-models/test', { definition: m.definition, prompt: $('#cm-test-prompt').value, images: $('#cm-test-img').checked, tools: $('#cm-test-tools').checked, json: $('#cm-test-json').checked });
    out.innerHTML = `<div class="notice info"><b>${esc(r.label || r.category)}</b> → <code>${esc(r.model)}</code><div class="small">${esc(r.reason)}${r.fallbacks.length ? ' · fallbacks ' + esc(r.fallbacks.join(', ')) : ''}</div></div>` +
      (r.scores.length ? `<div class="section">${barList(r.scores.map(s => ({ label: s.label, value: s.score })), { fmt: v => 'score ' + v })}</div>` : '');
    applyDataStyles(out);
  } catch (e) { out.innerHTML = `<div class="notice bad">${esc(e.message)}</div>`; }
};
