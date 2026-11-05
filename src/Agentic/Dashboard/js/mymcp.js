'use strict';
/* Agentic console - My MCP servers: add MCP servers without editing any file.
   - Hosted (HTTP) servers for your own chats, with a token header or a browser sign-in (OAuth). Only you see them.
   - Administrators also add servers for everyone, and programs that run on this server.
   - Servers that run on your own computer go to the desktop app or VS Code: "Install on my computer" sends the server
     there (the app shows its add form filled in).
   - A link to /my-mcp?add=<base64url JSON {name, entry}> opens this page with that server ready to add (for READMEs). */

const MYMCP = { servers: [], isAdmin: false, devices: [] };

VIEWS.myMcp = {
  live: true,
  async render() {
    setCrumbs([['My MCP servers', '/my-mcp']]);
    const [d, devices] = await Promise.all([get('/api/chat/mcp-servers'), get('/api/chat/devices').catch(() => [])]);
    MYMCP.servers = d.servers || []; MYMCP.isAdmin = !!d.isAdmin; MYMCP.devices = (devices || []).filter(x => x.online);
    const state = s => !s.enabled ? badge('Off', 'outline') : s.needsSignIn ? badge('Sign in needed', 'warn')
      : s.state === 'Connected' ? badge('Connected', 'ok') : s.state === 'Faulted' ? badge('Failed', 'bad') : badge(s.state || 'Starting', 'info');
    const actions = s => {
      if (!s.mine) return '<span class="muted small">added by an administrator</span>';
      const b = (label, act, cls = 'ghost', tip = '') => `<button class="btn xs ${cls}" data-act="${act}" data-id="${esc(s.id)}"${tip ? ` data-tip="${esc(tip)}"` : ''}>${label}</button>`;
      return [s.transport !== 'stdio' && (s.needsSignIn || !s.signedIn) ? b('Sign in', 'mymcp-signin', s.needsSignIn ? 'primary' : 'ghost', 'Sign in to this server in the browser (OAuth)') : '',
        s.signedIn ? b('Sign out', 'mymcp-signout') : '', b(s.enabled ? 'Turn off' : 'Turn on', 'mymcp-toggle'),
        b('Edit', 'mymcp-edit'), b('Remove', 'mymcp-remove', 'ghost danger')].join('');
    };
    const rows = table([
      { h: 'Server', f: s => `<b>${esc(s.name)}</b> <code class="muted">${esc(s.id)}</code><div class="cell-sub">${s.shared ? 'For everyone' : 'Only you'}${s.askFirst ? ' · asks before each call' : ''}${s.description ? ' · ' + esc(s.description) : ''}</div>` },
      { h: 'Connection', cls: 'clip', f: s => `${tag(s.transport)} <code>${esc(s.transport === 'stdio' ? `${s.command} ${(s.args || []).join(' ')}` : s.url)}</code>` },
      { h: 'State', f: s => state(s) + (s.error && s.state !== 'Connected' ? `<div class="cell-sub error-text" data-tip="${esc(s.error)}">${esc(String(s.error).slice(0, 140))}</div>` : '') },
      { h: 'Tools', f: s => s.tools && s.tools.length ? chips(s.tools.slice(0, 6)) + (s.tools.length > 6 ? ` <span class="muted small">+${s.tools.length - 6}</span>` : '') : '<span class="muted">—</span>' },
      { h: '', cls: 'act', f: actions }], MYMCP.servers, { empty: 'No MCP servers added here yet.', key: s => s.id });
    const html = pageHead('My MCP servers', {
      subtitle: 'Hosted MCP servers for your chats: turn them on in the chat\'s <b>Tools</b> menu, and the model uses their tools. ' +
        'Servers that sign in with OAuth show a <b>Sign in</b> button. For servers that run on your own computer, use the desktop app or VS Code (<b>Install on my computer</b> sends one there)' +
        (MYMCP.isAdmin ? '. As an administrator you can also add servers for everyone, and programs that run on this server.' : '.'),
      actions: `<button class="btn primary" data-act="mymcp-add">${icon('plus')} Add server</button>`
    }) + card('', rows, { flush: true, cls: 'section' }) + myMcpCatalog();
    // A link with ?add= opens the add dialog for that server once.
    const add = new URLSearchParams(location.search).get('add');
    if (add && !MYMCP.linkShown) {
      MYMCP.linkShown = true;
      try { const j = JSON.parse(atob(add.replace(/-/g, '+').replace(/_/g, '/'))); setTimeout(() => offerServer(j.name || 'server', j.entry || j), 0); }
      catch { toast('The link to add an MCP server is not valid.', 'bad'); }
    }
    return html;
  }
};

/** Well-known servers (MCP_CATALOG from the MCP page): hosted ones are added here, programs go to your computer. */
function myMcpCatalog() {
  const list = (typeof MCP_CATALOG !== 'undefined' ? MCP_CATALOG : []);
  if (!list.length) return '';
  const have = new Set(MYMCP.servers.map(s => s.name.toLowerCase()));
  const item = r => `<div class="mcp-cat-item"><div class="grow"><div class="cell-title">${esc(r.name)} ${r.url ? tag('hosted', 'outline') : tag('runs on a computer', 'outline')}</div>
      <div class="small muted">${esc(r.blurb)}</div><div class="cell-sub">Needs ${esc(r.needs)} · <a href="${esc(r.docs)}" target="_blank" rel="noopener noreferrer">docs</a></div></div>
    <button class="btn xs${r.url ? ' primary' : ''}" data-act="mymcp-catalog" data-id="${esc(r.id)}"${have.has(r.name.toLowerCase()) ? ' disabled' : ''}>${r.url ? 'Add' : 'Install…'}</button></div>`;
  const groups = [...new Set(list.map(r => r.category))];
  return card('Well-known servers', groups.map(g => `<div class="menu-title mcp-cat-title">${esc(g)}</div><div class="mcp-cat-grid">${list.filter(r => r.category === g).map(item).join('')}</div>`).join(''),
    { cls: 'section', hint: 'Hosted ones are added here; the others run on your computer (the desktop app or VS Code adds them) or, for administrators, on this server.' });
}

/** A catalog entry in Claude Code's format: keys become ${KEY} (read from the environment where it runs). */
function myMcpEntry(r) {
  const ref = k => '${' + k + '}';
  if (r.url) return { type: 'http', url: r.url, headers: r.bearer ? { Authorization: 'Bearer ' + ref(r.bearer) } : {} };
  const e = { command: r.command, args: r.args.slice() };
  if (r.env && r.env.length) e.env = Object.fromEntries(r.env.map(x => [x.key, ref(x.key)]));
  if (r.approval) e.requireApproval = true;
  return e;
}

/** Where to add a server: here (hosted, or for administrators any), or a connected computer. */
function offerServer(name, entry) {
  const hosted = !!(entry.url || entry.endpoint);
  const devices = MYMCP.devices;
  const target = entry.url || entry.endpoint || `${entry.command || ''} ${(entry.args || []).join(' ')}`;
  openModal('Add ' + name, `<p><code>${esc(target)}</code></p>
    <div class="stack">${hosted ? `<button class="btn primary" data-act="mymcp-offer-here">Add for my chats</button>` : ''}
      ${!hosted && MYMCP.isAdmin ? `<button class="btn" data-act="mymcp-offer-here">Run it on this server (everyone)</button>` : ''}
      ${devices.length ? devices.map(dv => `<button class="btn${hosted ? '' : ' primary'}" data-act="mymcp-offer-device" data-device="${esc(dv.id)}">Install on ${esc(dv.name || dv.id)}</button>`).join('')
        : (!hosted ? '<div class="notice small">Open the Agentic desktop app or VS Code on your computer, then try again: it adds the server there. <a href="/updates/desktop" data-nav>Get the apps</a></div>' : '')}</div>`, '', 'narrow');
  MYMCP.offer = { name, entry };
}
ACTIONS['mymcp-offer-here'] = async () => {
  const { name, entry } = MYMCP.offer || {};
  closeModal();
  if (entry.url || entry.endpoint) return myMcpForm({ name, transport: entry.type === 'sse' ? 'sse' : 'http', url: entry.url || entry.endpoint, headers: entry.headers || {} });
  myMcpForm({ name, transport: 'stdio', command: entry.command, args: entry.args || [], env: entry.env || {}, shared: true });
};
ACTIONS['mymcp-offer-device'] = async el => {
  const { name, entry } = MYMCP.offer || {};
  try { await post('/api/chat/mcp-servers/install', { device: el.dataset.device, name, entry }); closeModal(); toast('Sent: confirm it in the app on your computer', 'ok'); }
  catch (e) { toast(e.message, 'bad'); }
};
ACTIONS['mymcp-catalog'] = el => {
  const r = (typeof MCP_CATALOG !== 'undefined' ? MCP_CATALOG : []).find(x => x.id === el.dataset.id); if (!r) return;
  offerServer(r.id, myMcpEntry(r));
};

function myMcpForm(s) {
  const v = s || {};
  const kv = (o, sep) => Object.entries(o || {}).map(([k, x]) => k + sep + (String(x).startsWith('${') ? '' : x)).join('\n');
  const t = v.transport || 'http';
  openModal(v.id ? 'Edit ' + v.name : 'Add an MCP server', `<form class="stack" data-form="mymcp-save" data-id="${esc(v.id || '')}">
    <div class="form-grid"><label>Name<input name="name" required value="${esc(v.name || '')}" placeholder="e.g. linear"${v.id ? ' readonly' : ''}></label>
      <label>Connection<select name="transport" data-change="mymcp-transport">
        <option value="http"${t === 'http' ? ' selected' : ''}>HTTP (Streamable HTTP)</option><option value="sse"${t === 'sse' ? ' selected' : ''}>HTTP + SSE (older servers)</option>
        ${MYMCP.isAdmin ? `<option value="stdio"${t === 'stdio' ? ' selected' : ''}>Program on this server (stdio)</option>` : ''}</select></label></div>
    <div class="stack" data-show="http sse"><label>URL<input name="url" value="${esc(v.url || '')}" placeholder="https://mcp.example.com/mcp"></label>
      <label>Headers <span class="muted small">(optional: a token; leave empty to sign in with OAuth instead)</span><textarea name="headers" rows="2" class="mono" placeholder="Authorization: Bearer …">${esc(v.id ? (v.headers || []).map(h => h + ': •••').join('\n') : kv(v.headers, ': '))}</textarea></label></div>
    ${MYMCP.isAdmin ? `<div class="stack" data-show="stdio"><label>Command<input name="command" value="${esc(v.command || '')}" placeholder="npx, uvx, node, python …"></label>
      <label>Arguments<textarea name="args" rows="3" class="mono" placeholder="one per line">${esc((v.args || []).join('\n'))}</textarea></label>
      <label>Environment variables<textarea name="env" rows="2" class="mono" placeholder="KEY=value (one per line; %VAR% reads this server's environment)">${esc(kv(v.env, '='))}</textarea></label></div>` : ''}
    <label>Description <span class="muted small">(optional)</span><input name="description" value="${esc(v.description || '')}"></label>
    <label class="check"><input type="checkbox" name="askFirst"${v.askFirst ? ' checked' : ''}> Ask before each tool call (in every permission mode)</label>
    ${MYMCP.isAdmin ? `<label class="check"><input type="checkbox" name="shared"${v.shared ? ' checked' : ''}${v.id ? ' disabled' : ''}> For everyone (else only for my chats)</label>` : ''}
    <div data-errors></div></form>`,
    `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="mymcp-submit">${v.id ? 'Save' : 'Add and connect'}</button>`, 'wide');
  CHANGES['mymcp-transport']($('#modal-card select[name="transport"]'));
}
CHANGES['mymcp-transport'] = el => $$('#modal-card [data-show]').forEach(x => x.classList.toggle('hidden', !x.dataset.show.split(' ').includes(el.value)));
ACTIONS['mymcp-add'] = () => myMcpForm(null);
ACTIONS['mymcp-submit'] = () => $('#modal-card form').requestSubmit();
ACTIONS['mymcp-edit'] = el => { const s = MYMCP.servers.find(x => x.id === el.dataset.id); if (s) myMcpForm(Object.assign({}, s, { env: Object.fromEntries((s.env || []).map(k => [k, '']))})); };
FORMS['mymcp-save'] = async f => {
  const val = n => (f.elements[n] ? f.elements[n].value : '').trim();
  const lines = t => String(t || '').split('\n').map(x => x.trim()).filter(Boolean);
  const pairs = (t, sep) => Object.fromEntries(lines(t).filter(l => l.includes(sep)).map(l => [l.slice(0, l.indexOf(sep)).trim(), l.slice(l.indexOf(sep) + sep.length).trim()]));
  const body = { id: f.dataset.id || undefined, name: val('name'), transport: val('transport'), url: val('url'), command: val('command'), args: lines(val('args')),
    env: pairs(val('env'), '='), headers: pairs(val('headers'), ':'), description: val('description'),
    askFirst: !!(f.elements.askFirst && f.elements.askFirst.checked), shared: !!(f.elements.shared && f.elements.shared.checked) };
  try {
    const r = await post('/api/chat/mcp-servers', body);
    closeModal(); toast('Saved — connecting…', 'ok');
    // A hosted server without a token: offer the sign-in right away when it asks for one.
    setTimeout(async () => { await refreshNow(); const s = MYMCP.servers.find(x => x.id === r.id); if (s && s.needsSignIn) toast(`${s.name} needs you to sign in: click Sign in`, 'warn'); }, 2500);
    refreshNow();
  } catch (e) { const box = $('[data-errors]', f); if (box) box.innerHTML = `<div class="notice bad small">${esc(e.message)}</div>`; else toast(e.message, 'bad'); }
};
ACTIONS['mymcp-signin'] = async el => {
  // The window opens now (a popup opened later, after the request, would be blocked) and goes to the sign-in page.
  const w = window.open('about:blank', '_blank');
  try { const r = await post(`/api/chat/mcp-servers/${enc(el.dataset.id)}/signin`); if (w) w.location = r.url; else location.href = r.url; toast('Sign in in the new tab; this page updates when it is done', 'ok'); }
  catch (e) { if (w) w.close(); toast(e.message, 'bad'); }
};
ACTIONS['mymcp-signout'] = async el => { await post(`/api/chat/mcp-servers/${enc(el.dataset.id)}/signout`); toast('Signed out', 'ok'); refreshNow(); };
ACTIONS['mymcp-toggle'] = async el => {
  const s = MYMCP.servers.find(x => x.id === el.dataset.id); if (!s) return;
  await post(`/api/chat/mcp-servers/${enc(s.id)}/enabled`, { on: !s.enabled }); toast(s.enabled ? 'Turned off' : 'Turned on', 'ok'); refreshNow();
};
ACTIONS['mymcp-remove'] = async el => {
  if (!await confirmBox('Remove server', `Remove <b>${esc(el.dataset.id)}</b>?`, { danger: true, confirm: 'Remove' })) return;
  await del(`/api/chat/mcp-servers/${enc(el.dataset.id)}`); toast('Removed', 'ok'); refreshNow();
};
