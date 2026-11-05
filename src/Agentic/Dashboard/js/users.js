'use strict';
/* Agentic console - dashboard user accounts: administrators manage users, their permissions (grouped: Chat, API & apps,
   Workforce, Money, Administration), the agents, models and MCP servers they may use, and their limits (tokens per day,
   requests per minute); everyone can change their own password. The server enforces all of it. */

const USERS = { catalog: [], minPassword: 10, mcp: [] };
const PERM_GROUPS = ['Chat', 'API & apps', 'Workforce', 'Money', 'Administration'];

function permGroups() {
  const groups = PERM_GROUPS.map(g => ({ g, perms: USERS.catalog.filter(p => (p.group || 'More') === g) }));
  const other = USERS.catalog.filter(p => !PERM_GROUPS.includes(p.group || 'More'));
  if (other.length) groups.push({ g: 'More', perms: other });
  return groups.filter(x => x.perms.length);
}
/** A user's permissions per group: "Chat 9/12" (hover: which ones). */
function permSummary(u) {
  if (u.role === 'Admin') return '<span class="muted">Everything, including users</span>';
  if (!u.permissions.length) return '<span class="muted">None</span>';
  return permGroups().map(({ g, perms }) => {
    const on = perms.filter(p => u.permissions.includes(p.key));
    if (!on.length) return '';
    return `<span class="chip${on.length === perms.length ? ' info' : ''}" data-tip="${esc(on.map(p => p.label).join(', '))}">${esc(g)} ${on.length}/${perms.length}</span>`;
  }).join(' ');
}
function mcpName(id) { const m = USERS.mcp.find(x => x.id === id); return m ? m.name : id; }
function limitsSummary(u) {
  if (u.role === 'Admin') return '<span class="muted">none</span>';
  const parts = [];
  if (u.dailyTokenLimit) parts.push(`${num(u.dailyTokenLimit)} tokens/day`);
  if (u.requestsPerMinute) parts.push(`${num(u.requestsPerMinute)}/min`);
  return parts.length ? parts.map(p => `<span class="chip warn">${esc(p)}</span>`).join(' ') : '<span class="muted">none</span>';
}

VIEWS.users = {
  live: false,
  // Saving or changing a user draws the page again at once (refreshNow), with what the server stored.
  reloadable: true,
  async render() {
    setCrumbs([['Users & access', '/users']]);
    if (!can('admin')) return pageHead('Users & access') + card('', emptyState('Administrators only', 'Ask an administrator to change accounts or permissions.'));
    const d = await get('/api/users');
    USERS.catalog = d.permissions; USERS.minPassword = d.minPasswordLength; USERS.mcp = d.mcpServers || [];
    const rows = table([
      { h: 'User', f: u => `<span class="cell-title">${esc(u.displayName || u.username)}</span>${u.username === S.user ? ' ' + tag('you', 'accent') : ''}<div class="cell-sub"><code>${esc(u.username)}</code> · ${esc(u.source)}</div>` },
      { h: 'Role', cls: 'nw', f: u => u.role === 'Admin' ? badge('Administrator', 'accent', false) : badge('User', 'outline', false) },
      { h: 'Permissions', f: u => permSummary(u) },
      { h: 'Agents', f: u => u.allAgents ? '<span class="muted">All agents</span>' : chips(u.allowedAgents, 'info') },
      { h: 'Models', f: u => u.allModels ? '<span class="muted">All models</span>' : (u.allowedModels.length > 4 ? `<span data-tip="${esc(u.allowedModels.join(', '))}">${plural(u.allowedModels.length, 'model')}</span>` : chips(u.allowedModels)) },
      { h: 'MCP servers', f: u => u.allMcp ? '<span class="muted">All servers</span>' : chips(u.allowedMcpServers.map(mcpName)) },
      { h: 'Limits', cls: 'nw', f: u => limitsSummary(u) },
      { h: 'Balance', cls: 'nw', num: true, f: u => u.charged ? `<span class="${u.balanceMicros <= 0 ? 'amt-neg' : ''}">${money(u.balanceMicros, 2)}</span>` + (u.surcharge ? '<div class="cell-sub">+15% surcharge</div>' : '') : '<span class="muted">never charged</span>' },
      { h: 'Status', cls: 'nw', f: u => u.disabled ? badge('Disabled', 'bad') : badge('Active', 'ok') },
      { h: 'Last sign-in', cls: 'nw', f: u => `<span data-tip="${esc(ts(u.lastLoginUtc))}">${esc(u.lastLoginUtc ? ago(u.lastLoginUtc) : 'never')}</span>` +
          (u.passwordChangedUtc ? `<div class="cell-sub">password changed ${esc(ago(u.passwordChangedUtc))}</div>` : '') },
      { h: 'API key', cls: 'nw', f: u => u.apiKeyPrefix ? `<code>${esc(u.apiKeyPrefix)}…</code><div class="cell-sub">${esc(u.apiKeyLastUsedUtc ? 'used ' + ago(u.apiKeyLastUsedUtc) : 'never used')}</div>` : '<span class="muted">none</span>' },
      { h: '', cls: 'act', f: u => {
          const me = u.username === S.user, data = `data-user="${esc(u.username)}"`;
          return `<button class="btn xs" data-act="user-form" ${data}>Edit</button> ` +
            (u.charged ? `<button class="btn xs" data-act="balance-add" ${data} data-balance="${u.balanceMicros}">${icon('plus')} Add money</button> ` : '') +
            (me ? '' : `<button class="btn xs" data-act="user-password" ${data}>Reset password</button> ` +
              `<button class="btn xs" data-act="user-toggle" ${data} data-disabled="${u.disabled ? 0 : 1}">${u.disabled ? 'Enable' : 'Disable'}</button> ` +
              (u.source === 'Config' ? '' : `<button class="btn xs danger" data-act="user-delete" ${data}>Delete</button>`)) +
            (u.apiKeyPrefix ? ` <button class="btn xs danger" data-act="user-revoke-key" ${data}>Revoke key</button>` : '');
        } }
    ], d.users, { empty: 'No users.' });
    S.usersCache = d.users;
    return pageHead('Users & access', {
      subtitle: 'Who can sign in to this console, what they may do, which agents, models and MCP servers they may use, and their limits. Changes sign the user out so they apply at once.',
      actions: `<button class="btn primary" data-act="user-form">${icon('plus')} New user</button>`
    }) + card('Accounts', rows, { flush: true, export: 'users' }) +
      card('Permissions', permGroups().map(({ g, perms }) => `<div class="menu-title perm-group-title">${esc(g)}</div><div class="check-grid">${perms.map(p => `<div class="notice"><b>${esc(p.label)}</b> <code>${esc(p.key)}</code><div class="small">${esc(p.description)}</div></div>`).join('')}</div>`).join('') +
        `<p class="muted small section">Users see the Workforce (agents, tasks, the inbox …) and its dashboards only with <b>View workforce</b> or a permission that acts on agents, and then only the agents they have access to. Administrators have every permission, no limits, and can manage users.</p>`, { cls: 'section' });
  }
};

ACTIONS['user-form'] = async el => {
  const name = el.dataset.user;
  const editing = name ? (S.usersCache || []).find(u => u.username === name) : null;
  if (!USERS.catalog.length) { const d = await get('/api/users'); USERS.catalog = d.permissions; USERS.minPassword = d.minPasswordLength; USERS.mcp = d.mcpServers || []; }
  const [agents, square] = await Promise.all([get('/api/agents'), get('/api/model-square').catch(() => ({ models: [] }))]);
  const u = editing || { username: '', displayName: '', role: 'User', permissions: [], allowedAgents: [], allAgents: true, allowedModels: [], allModels: true, allowedMcpServers: [], allMcp: true, dailyTokenLimit: 0, requestsPerMinute: 0 };
  const allowedModels = u.allowedModels || [], allowedMcp = u.allowedMcpServers || [];
  const modelBoxes = square.models.map(m => `<label class="check" data-model-s="${esc((m.id + ' ' + m.name).toLowerCase())}"><input type="checkbox" name="model" value="${esc(m.id)}"${allowedModels.includes(m.id) ? ' checked' : ''}>` +
    `<span>${esc(m.name)}<span class="help">${esc(m.id)}${m.custom ? ' · custom' : ''} · ${esc(priceText(m.inputPerMillion, m.outputPerMillion))}</span></span></label>`).join('');
  const isAdmin = u.role === 'Admin';
  const permBox = p => `<label class="check"><input type="checkbox" name="perm" value="${esc(p.key)}" data-group="${esc(p.group || 'More')}"${u.permissions.includes(p.key) || isAdmin ? ' checked' : ''}>` +
    `<span>${esc(p.label)}<span class="help">${esc(p.description)}</span></span></label>`;
  const perms = permGroups().map(({ g, perms: list }) => `<div class="perm-group"><div class="menu-title perm-group-title">${esc(g)}
      <button type="button" class="btn xs ghost" data-act="user-perms-group" data-group="${esc(g)}">All / none</button></div>
      <div class="check-grid">${list.map(permBox).join('')}</div></div>`).join('');
  const agentBoxes = agents.map(a => `<label class="check"><input type="checkbox" name="agent" value="${esc(a.id)}"${u.allowedAgents.includes(a.id) ? ' checked' : ''}>` +
    `<span>${esc(a.name)}<span class="help">${esc(a.id)} · ${esc(a.role || '')}</span></span></label>`).join('');
  const mcpBoxes = USERS.mcp.map(m => `<label class="check"><input type="checkbox" name="mcp" value="${esc(m.id)}"${allowedMcp.includes(m.id) ? ' checked' : ''}>` +
    `<span>${esc(m.name)}${m.enabled === false ? ' <span class="badge outline">off</span>' : ''}<span class="help">${esc(m.id)}${m.description ? ' · ' + esc(m.description) : ''}</span></span></label>`).join('');
  openModal(editing ? `Edit user ${editing.username}` : 'New user', `<form id="user-form" class="stack" data-form="user-form" data-user="${esc(name || '')}">
      <div data-errors></div>
      <div class="form-grid">
        ${editing ? '' : `<label>Username<input name="username" required maxlength="50" pattern="[A-Za-z0-9][A-Za-z0-9._-]{2,49}" autocomplete="off"><span class="help">Letters, digits, dots, dashes; used to sign in.</span></label>`}
        <label>Display name<input name="displayName" maxlength="80" value="${esc(u.displayName || '')}" placeholder="e.g. Jane Smith"></label>
        <label>Role<select name="role" data-change="user-role">
          <option value="User"${isAdmin ? '' : ' selected'}>User — chosen permissions and agents</option>
          <option value="Admin"${isAdmin ? ' selected' : ''}>Administrator — everything, including users</option></select></label>
        ${editing ? '' : `<label>Password<input name="password" type="password" required minlength="${USERS.minPassword}" autocomplete="new-password"><span class="help">At least ${USERS.minPassword} characters.</span></label>
          <label>Repeat password<input name="password2" type="password" required minlength="${USERS.minPassword}" autocomplete="new-password"></label>`}
      </div>
      <div id="user-access" class="${isAdmin ? 'disabled-area' : ''}">
        <h3 class="section">What this user may do</h3>
        <div class="row small"><button type="button" class="btn xs" data-act="user-perms" data-mode="all">Select all</button><button type="button" class="btn xs" data-act="user-perms" data-mode="none">None</button></div>
        <div class="section">${perms}</div>
        <label class="check section" data-tip="Every fee this account pays (model tokens, vision, images, videos and its agents) is 15% higher."><input type="checkbox" name="surcharge" value="1"${u.surcharge ? ' checked' : ''}> Tack on 15% surcharge</label>
        <h3 class="section">Limits</h3>
        <div class="form-grid">
          <label>Tokens per day<input name="dailyTokenLimit" type="number" min="0" step="1" value="${u.dailyTokenLimit || ''}" placeholder="No limit"><span class="help">Chat and API tokens per day (UTC midnight). Empty = no limit.</span></label>
          <label>Requests per minute<input name="requestsPerMinute" type="number" min="0" step="1" value="${u.requestsPerMinute || ''}" placeholder="No limit"><span class="help">Chat messages and API calls. Empty = no limit.</span></label>
        </div>
        <h3 class="section">Which MCP servers this user can use</h3>
        <p class="muted small">In chat (with <b>MCP tools</b>) and for agents the user creates. The code interpreter has its own permission.</p>
        <div class="row">
          <label class="check"><input type="radio" name="mcpScope" value="all" data-change="user-mcp-scope"${u.allMcp !== false ? ' checked' : ''}> All servers (including new ones)</label>
          <label class="check"><input type="radio" name="mcpScope" value="some" data-change="user-mcp-scope"${u.allMcp === false ? ' checked' : ''}> Only these servers</label>
        </div>
        <div id="user-mcp" class="check-grid section${u.allMcp !== false ? ' disabled-area' : ''}">${mcpBoxes || '<span class="muted">No MCP servers are configured.</span>'}</div>
        <h3 class="section">Which agents this user sees</h3>
        <p class="muted small">Only with View workforce or another permission that acts on agents; without them the user sees no agents.</p>
        <div class="row">
          <label class="check"><input type="radio" name="scope" value="all" data-change="user-scope"${u.allAgents ? ' checked' : ''}> All agents (including new ones)</label>
          <label class="check"><input type="radio" name="scope" value="some" data-change="user-scope"${u.allAgents ? '' : ' checked'}> Only these agents</label>
        </div>
        <div id="user-agents" class="check-grid section${u.allAgents ? ' disabled-area' : ''}">${agentBoxes || '<span class="muted">No agents.</span>'}</div>
        <h3 class="section">Which models this user can use</h3>
        <p class="muted small">In Chat, the API and for agents: models the user cannot use are not offered to them, and they cannot give them to an agent.</p>
        <div class="row">
          <label class="check"><input type="radio" name="modelScope" value="all" data-change="user-model-scope"${u.allModels !== false ? ' checked' : ''}> All models (including new ones)</label>
          <label class="check"><input type="radio" name="modelScope" value="some" data-change="user-model-scope"${u.allModels === false ? ' checked' : ''}> Only these models</label>
        </div>
        <div id="user-models" class="section${u.allModels !== false ? ' disabled-area' : ''}">
          <input type="search" placeholder="Filter models" data-user-model-filter aria-label="Filter models" class="ms-search">
          <div class="check-grid section">${modelBoxes || '<span class="muted">No models.</span>'}</div>
        </div>
      </div></form>`,
    `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="submit-form" data-form-id="user-form">${editing ? 'Save changes' : 'Create user'}</button>`, 'wide');
};
CHANGES['user-role'] = el => { $('#user-access').classList.toggle('disabled-area', el.value === 'Admin'); };
CHANGES['user-scope'] = el => { $('#user-agents').classList.toggle('disabled-area', el.value === 'all'); };
CHANGES['user-model-scope'] = el => { $('#user-models').classList.toggle('disabled-area', el.value === 'all'); };
CHANGES['user-mcp-scope'] = el => { $('#user-mcp').classList.toggle('disabled-area', el.value === 'all'); };
document.addEventListener('input', ev => {
  const f = ev.target.closest && ev.target.closest('[data-user-model-filter]');
  if (!f) return;
  const q = f.value.trim().toLowerCase();
  $$('#user-models [data-model-s]').forEach(l => l.classList.toggle('hidden', !!q && !l.dataset.modelS.includes(q)));
});
ACTIONS['user-perms'] = el => { $$('#user-form input[name="perm"]').forEach(cb => { cb.checked = el.dataset.mode === 'all'; }); };
// A group's button: all of the group on, or (when all are on) all off.
ACTIONS['user-perms-group'] = el => {
  const boxes = $$(`#user-form input[name="perm"][data-group="${CSS.escape(el.dataset.group)}"]`);
  const on = !boxes.every(cb => cb.checked);
  boxes.forEach(cb => { cb.checked = on; });
};
/** A limit box: a whole number, 0 when empty. */
function limitOf(v, label) {
  const t = String(v || '').trim();
  if (!t) return 0;
  const n = Number(t);
  if (!Number.isInteger(n) || n < 0) throw new Error(`${label}: a whole number, or empty for no limit.`);
  return n;
}

FORMS['user-form'] = async f => {
  const fd = new FormData(f);
  const editing = f.dataset.user;
  const role = fd.get('role');
  const user = role !== 'Admin';
  const body = {
    displayName: (fd.get('displayName') || '').trim(), role,
    permissions: user ? fd.getAll('perm') : [],
    allowedAgents: !user || fd.get('scope') === 'all' ? [] : fd.getAll('agent'),
    allowedModels: !user || fd.get('modelScope') !== 'some' ? [] : fd.getAll('model'),
    allowedMcpServers: !user || fd.get('mcpScope') !== 'some' ? [] : fd.getAll('mcp'),
    dailyTokenLimit: user ? limitOf(fd.get('dailyTokenLimit'), 'Tokens per day') : 0,
    requestsPerMinute: user ? limitOf(fd.get('requestsPerMinute'), 'Requests per minute') : 0,
    surcharge: user && fd.get('surcharge') === '1'
  };
  if (user && fd.get('scope') === 'some' && body.allowedAgents.length === 0) throw new Error('Select at least one agent, or choose "All agents".');
  if (user && fd.get('modelScope') === 'some' && body.allowedModels.length === 0) throw new Error('Select at least one model, or choose "All models".');
  if (user && fd.get('mcpScope') === 'some' && body.allowedMcpServers.length === 0) throw new Error('Select at least one MCP server, or choose "All servers" (turn off "MCP tools" for none).');
  if (editing) {
    const r = await patch('/api/users/' + enc(editing), body);
    toast(`Saved ${editing}${r.sessionsEnded ? ' — signed out of ' + plural(r.sessionsEnded, 'session') : ''}`, 'ok');
  } else {
    if (fd.get('password') !== fd.get('password2')) throw new Error('The passwords do not match.');
    body.username = (fd.get('username') || '').trim(); body.password = fd.get('password');
    await post('/api/users', body);
    toast(`User ${body.username} created`, 'ok');
  }
  closeModal(); refreshNow();
};

ACTIONS['user-password'] = el => {
  openModal('Reset password of ' + el.dataset.user, `<form id="reset-form" class="form-grid" data-form="user-password" data-user="${esc(el.dataset.user)}">
      <div class="full" data-errors></div>
      <label>New password<input name="password" type="password" required minlength="${USERS.minPassword}" autocomplete="new-password"></label>
      <label>Repeat password<input name="password2" type="password" required minlength="${USERS.minPassword}" autocomplete="new-password"></label>
      <p class="muted small full">The user is signed out everywhere and signs in with the new password.</p></form>`,
    `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="submit-form" data-form-id="reset-form">Reset password</button>`, 'narrow');
};
FORMS['user-password'] = async (f, d) => {
  if (d.password !== d.password2) throw new Error('The passwords do not match.');
  await post(`/api/users/${enc(f.dataset.user)}/password`, { password: d.password });
  toast('Password reset for ' + f.dataset.user, 'ok'); closeModal();
};
ACTIONS['user-toggle'] = async el => {
  const disable = el.dataset.disabled === '1';
  if (disable && !await confirmBox('Disable user', `Disable <b>${esc(el.dataset.user)}</b>? They are signed out and cannot sign in until enabled again.`, { danger: true, confirm: 'Disable' })) return;
  await patch('/api/users/' + enc(el.dataset.user), { disabled: disable });
  toast(`${el.dataset.user} ${disable ? 'disabled' : 'enabled'}`, 'ok'); refreshNow();
};
ACTIONS['user-revoke-key'] = async el => {
  if (!await confirmBox('Revoke API key', `Revoke the API key of <b>${esc(el.dataset.user)}</b>? Programs using it stop working at once; they can generate a new one.`, { danger: true, confirm: 'Revoke key' })) return;
  await del('/api/users/' + enc(el.dataset.user) + '/api-key');
  toast('API key revoked', 'ok'); refreshNow();
};
ACTIONS['user-delete'] = async el => {
  if (!await confirmBox('Delete user', `Delete <b>${esc(el.dataset.user)}</b>? This cannot be undone; their audit history is kept.`, { danger: true, confirm: 'Delete user' })) return;
  await del('/api/users/' + enc(el.dataset.user));
  toast(`${el.dataset.user} deleted`, 'ok'); refreshNow();
};

// Every signed-in user: change their own password.
ACTIONS['change-password'] = () => {
  $('#user-menu').classList.add('hidden');
  openModal('Change your password', `<form id="pw-form" class="stack" data-form="change-password">
      <div data-errors></div>
      <label>Current password<input name="current" type="password" required autocomplete="current-password"></label>
      <label>New password<input name="password" type="password" required minlength="${USERS.minPassword}" autocomplete="new-password"><span class="help">At least ${USERS.minPassword} characters.</span></label>
      <label>Repeat new password<input name="password2" type="password" required minlength="${USERS.minPassword}" autocomplete="new-password"></label>
      <p class="muted small">Your other sessions are signed out; this one stays signed in.</p></form>`,
    `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="submit-form" data-form-id="pw-form">Change password</button>`, 'narrow');
};
FORMS['change-password'] = async (f, d) => {
  if (d.password !== d.password2) throw new Error('The new passwords do not match.');
  const r = await post('/api/auth/password', { currentPassword: d.current, newPassword: d.password });
  toast('Password changed' + (r.otherSessionsEnded ? ` — ${plural(r.otherSessionsEnded, 'other session')} signed out` : ''), 'ok'); closeModal();
};
