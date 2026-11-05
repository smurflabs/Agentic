'use strict';
/* Agentic console - agent editor (create / edit / clone, including additional model grants and schedules) and the
   custom model editor. Empty fields inherit from the base profile. */

const MESSAGE_TYPES_SEND = ['Information', 'Question', 'Request', 'TaskDelegation', 'TaskResult', 'Notification', 'ApprovalRequest', 'StatusRequest', 'Response', 'Error'];
const CAPABILITIES = ['ToolCalling', 'Vision', 'Reasoning', 'JsonMode', 'Streaming'];

function fval(v) { return v === null || v === undefined ? '' : esc(v); }
function numInput(name, label, value, help, attrs = '') {
  return `<label>${esc(label)}<input name="${name}" type="number" ${attrs} value="${fval(value)}" placeholder="inherit">${help ? `<span class="help">${help}</span>` : ''}</label>`;
}
function triSelect(name, label, value, help) {
  const v = value === true ? 'true' : value === false ? 'false' : '';
  return `<label>${esc(label)}<select name="${name}"><option value=""${v === '' ? ' selected' : ''}>Inherit</option><option value="true"${v === 'true' ? ' selected' : ''}>Yes</option><option value="false"${v === 'false' ? ' selected' : ''}>No</option></select>${help ? `<span class="help">${help}</span>` : ''}</label>`;
}
function inheritBlock(key, label, isInherited, inner) {
  return `<label class="check inherit-toggle"><input type="checkbox" data-change="inherit" data-target="${key}"${isInherited ? ' checked' : ''}> ${esc(label)}</label>
    <div data-inherit-area="${key}" class="${isInherited ? 'disabled-area' : ''}">${inner}</div>`;
}
CHANGES['inherit'] = el => { const area = $(`[data-inherit-area="${CSS.escape(el.dataset.target)}"]`); if (area) area.classList.toggle('disabled-area', el.checked); };
function checkGrid(group, items, selected) {
  const set = new Set(selected || []);
  return `<div class="check-grid">${items.map(it => `<label class="check"><input type="checkbox" data-group="${group}" value="${esc(it.value)}"${set.has(it.value) ? ' checked' : ''}><span>${esc(it.label || it.value)}${it.help ? `<span class="help">${esc(it.help)}</span>` : ''}</span></label>`).join('')}</div>`;
}
function grantRow(meta, g = {}) {
  return `<div class="repeat-row grant" data-row="grant">
    <label>Model<select data-k="modelKey">${meta.grantableModels.map(m => `<option value="${esc(m.key)}"${m.key === g.modelKey ? ' selected' : ''} data-use="${esc(m.useFor || '')}">${esc(m.key)}${m.isCustom ? ' (custom)' : ''} · ${m.priced ? `$${m.inputPerMillion}/$${m.outputPerMillion}` : 'unpriced'}</option>`).join('')}</select></label>
    <label>Use it for<input data-k="useFor" maxlength="1000" value="${fval(g.useFor)}" placeholder="What this agent should use the model for"></label>
    <label class="check"><input type="checkbox" data-k="allowSubagents"${g.allowSubagents ? ' checked' : ''}> Subagents too</label>
    <button class="btn small danger" data-act="remove-row" type="button">Remove</button></div>`;
}
function scheduleRow(meta, s = {}) {
  const params = Object.entries(s.parameters || {}).map(([k, v]) => `${k}=${v}`).join('\n');
  const opt = (vals, cur) => vals.map(v => `<option${v === cur ? ' selected' : ''}>${v}</option>`).join('');
  return `<div class="repeat-row schedule" data-row="schedule">
    <label>Id<input data-k="id" value="${fval(s.id)}" required></label>
    <label>Name<input data-k="name" value="${fval(s.name)}"></label>
    <label>Handler<select data-k="handler">${opt(meta.handlers, s.handler || 'reasoning')}</select></label>
    <label>Every (seconds)<input data-k="intervalSeconds" type="number" min="5" value="${fval(s.intervalSeconds)}"></label>
    <label>or Cron (UTC)<input data-k="cron" value="${fval(s.cron)}" placeholder="0 8 * * *"></label>
    <label>Priority<select data-k="priority">${opt(meta.priorities, s.priority || 'Normal')}</select></label>
    <label>Kind<select data-k="kind">${opt(['Recurring', 'Scheduled', 'Polling', 'LongRunning'], s.kind || 'Recurring')}</select></label>
    <label class="check"><input type="checkbox" data-k="enabled"${s.enabled === false ? '' : ' checked'}> Enabled</label>
    <label class="check"><input type="checkbox" data-k="runOnStart"${s.runOnStart ? ' checked' : ''}> Run on start</label>
    <label class="full">Instructions<textarea data-k="description" rows="2">${fval(s.description)}</textarea></label>
    <label class="full">Parameters (Key=Value per line)<textarea data-k="parameters" class="code" rows="2">${esc(params)}</textarea></label>
    <div><button class="btn small danger" data-act="remove-row" type="button">Remove schedule</button></div></div>`;
}
ACTIONS['remove-row'] = el => el.closest('[data-row]').remove();
ACTIONS['add-row'] = el => {
  const meta = S.editorMeta;
  const container = $(`[data-rows="${CSS.escape(el.dataset.kind)}"]`);
  container.insertAdjacentHTML('beforeend', el.dataset.kind === 'grant' ? grantRow(meta) : scheduleRow(meta));
  const sel = $('[data-row]:last-child select[data-k="modelKey"]', container);
  if (sel) { const use = sel.selectedOptions[0] && sel.selectedOptions[0].dataset.use; const inp = $('[data-k="useFor"]', sel.closest('[data-row]')); if (use && inp) inp.value = use; }
};

VIEWS.editor = {
  live: false,
  async render(p) {
    const editId = p.args[0];
    const cloneId = !editId ? p.query.get('clone') : null;
    const meta = await get('/api/meta/agent-form');
    S.editorMeta = meta;
    let def = {}, grants = [], title = 'New agent', notice = '', owner = '';
    if (editId || cloneId) {
      const d = await get(`/api/agents/${enc(editId || cloneId)}/definition`);
      if (editId && !d.editable) return pageHead('Edit agent') + card('', `<div class="notice warn">${esc(editId)} is defined in appsettings.json and cannot be edited here.</div><div class="row section">${link('/agents/new?clone=' + enc(editId), 'Clone it into a dashboard-managed agent', 'btn primary')}</div>`);
      def = JSON.parse(JSON.stringify(d.profile || {}));
      grants = d.grants || [];
      if (editId) owner = d.owner || '';
      if (cloneId) {
        title = 'New agent (copy of ' + cloneId + ')';
        notice = `<div class="notice info">Copied from <b>${esc(cloneId)}</b>${d.source === 'Config' ? ' (defined in appsettings.json)' : ''}. Choose a new id, review and save.</div>`;
        def.id = ''; def.name = 'Copy of ' + (def.name || cloneId);
      } else title = 'Edit ' + (def.name || editId);
    } else {
      def = { baseProfileId: meta.baseProfiles.includes('default') ? 'default' : meta.baseProfiles[0], allowedModels: meta.models.filter(m => m.isLocal).map(m => m.key) };
    }
    setCrumbs([['Agents', '/agents'], [editId ? (def.name || editId) : 'New agent', editId ? '/agents/' + enc(editId) : ''], ...(editId ? [['Edit', '']] : [])]);
    const c = def.communication || {}, t = def.tasks || {}, ai = def.ai || {}, mem = def.memory || {}, eco = def.economy || {}, sub = def.subagents || {}, idle = def.idle || {};
    const agents = meta.agents.filter(a => a.id !== editId);
    const sections = [['identity', 'Identity'], ['behaviour', 'Behaviour'], ['models', 'Models & access'], ['tools', 'Tools & MCP'], ['comms', 'Communication'],
      ['limits', 'Limits'], ['economy', 'Budget & subagents'], ['autonomy', 'Autonomy'], ['schedules', 'Schedules']];
    const nav = `<nav class="editor-nav">${sections.map(([k, l]) => `<a href="#sec-${k}" data-act="scroll-to" data-target="sec-${k}">${l}</a>`).join('')}</nav>`;
    const au = def.autonomy || {};
    const toolsMode = au.autoApproveTools == null ? '' : au.autoApproveTools.includes('*') ? 'all' : au.autoApproveTools.length ? 'list' : 'none';
    const sel = (cur, val) => cur === val ? ' selected' : '';
    const body = `
      <div class="form-section" id="sec-identity"><h2>Identity</h2><span class="help">Who the agent is. The id cannot be changed later.</span>
        <div class="form-grid">
          <label>Agent id<input name="id" value="${fval(def.id)}" ${editId ? 'readonly' : 'required'} pattern="[A-Za-z0-9][A-Za-z0-9_.\\-]{0,63}" placeholder="e.g. SalesAgent"><span class="help">Letters, digits, _ - .</span></label>
          <label>Display name<input name="name" value="${fval(def.name)}" maxlength="100"></label>
          <label>Role<input name="role" value="${fval(def.role)}" maxlength="200" placeholder="e.g. Sales analyst"></label>
          <label>Base profile<select name="baseProfileId">${meta.baseProfiles.map(b => `<option${b === def.baseProfileId ? ' selected' : ''}>${esc(b)}</option>`).join('')}</select><span class="help">Defaults for everything left empty</span></label>
          <label class="full">Description<textarea name="description" rows="2" maxlength="2000">${fval(def.description)}</textarea></label>
          <label class="span2">Personality<input name="personality" value="${fval(def.personality)}" placeholder="inherit"></label>
          <label class="check"><input type="checkbox" name="enabled"${def.enabled === false ? '' : ' checked'}> Enabled</label>
        </div></div>
      <div class="form-section" id="sec-behaviour"><h2>Behaviour</h2><span class="help">Instructions that shape every task.</span>
        <div class="form-grid">
          <label class="full">System prompt (replaces the base prompt)<textarea name="systemPrompt" rows="5" placeholder="Leave empty to use the base profile's prompt">${fval(def.systemPrompt)}</textarea></label>
          <label class="full">Additional instructions (appended)<textarea name="appendSystemPrompt" rows="3">${fval(def.appendSystemPrompt)}</textarea></label>
          <label class="full">Standing goals (one per line)<textarea name="goals" rows="3">${esc((def.goals || []).join('\n'))}</textarea></label>
          <label class="full">Safety rules (one per line; empty = inherit)<textarea name="safetyRules" rows="3">${esc((def.safetyRules || []).join('\n'))}</textarea></label>
          <label>Default purpose<select name="defaultPurpose"><option value="">Inherit</option>${meta.purposes.map(p => `<option${p === def.defaultPurpose ? ' selected' : ''}>${p}</option>`).join('')}</select></label>
        </div></div>
      <div class="form-section" id="sec-models"><h2>Models & access</h2><span class="help">Standard models come from the routing ladders. Additional models are granted explicitly with a stated purpose — only agents granted a model can use it.</span>
        ${inheritBlock('allowedModels', "Use the base profile's models", def.allowedModels == null,
          checkGrid('allowedModels', meta.models.map(m => ({ value: m.key, label: m.byo ? (m.name || m.key) : m.key, help: m.byo ? 'your own API key · free' : `tier ${m.tier} · ${m.isLocal ? 'local, free' : (m.priced ? `${m.inputPerMillion}/${m.outputPerMillion} per 1M` : 'unpriced')}` })), def.allowedModels))}
        <div class="form-grid section"><label>Max tier<select name="maxTier"><option value="">Inherit</option>${[1, 2, 3, 4, 5, 6, 7, 8, 9].map(n => `<option${n === def.maxTier ? ' selected' : ''}>${n}</option>`).join('')}</select><span class="help">Highest escalation tier allowed</span></label></div>
        <h3 class="section">Additional model access</h3>
        <div data-rows="grant">${grants.map(g => grantRow(meta, g)).join('')}</div>
        ${meta.grantableModels.length ? `<button class="btn small" type="button" data-act="add-row" data-kind="grant">Add model access</button>` : '<p class="muted">No cloud models available to grant. Register one under Models &amp; access.</p>'}
      </div>
      <div class="form-section" id="sec-tools"><h2>Tools & MCP</h2>
        ${inheritBlock('internalTools', "Use the base profile's tools", def.internalTools == null,
          checkGrid('internalTools', meta.internalTools.map(x => ({ value: x.name, label: x.name, help: x.description })), def.internalTools || meta.internalTools.filter(x => x.isDefault).map(x => x.name)))}
        <h3 class="section">MCP servers</h3>${meta.mcpServers.length ? checkGrid('allowedMcpServers', meta.mcpServers.map(s => ({ value: s.id, label: s.name || s.id, help: s.enabled ? '' : 'disabled' })), def.allowedMcpServers) : '<p class="muted">No MCP servers configured.</p>'}
        <div class="form-grid section">
          <label class="span2">Allowed MCP tools<input name="allowedMcpTools" value="${esc((def.allowedMcpTools || []).join(', '))}" placeholder="server:* or server:tool, comma separated"></label>
          <label class="span2">Denied MCP tools<input name="deniedMcpTools" value="${esc((def.deniedMcpTools || []).join(', '))}" placeholder="server:tool, comma separated"></label>
        </div></div>
      <div class="form-section" id="sec-comms"><h2>Communication</h2><span class="help">Which agents this agent may contact. Others must list this agent to contact it.</span>
        <h3>May send to</h3>${agents.length ? checkGrid('allowedRecipients', agents.map(a => ({ value: a.id, label: a.name, help: a.role })), c.allowedRecipients) : '<p class="muted">No other agents.</p>'}
        <div class="section">${inheritBlock('sendTypes', 'Use default message types', c.allowedSendMessageTypes == null, checkGrid('sendTypes', MESSAGE_TYPES_SEND.map(v => ({ value: v })), c.allowedSendMessageTypes))}</div>
        <div class="form-grid section">
          <label>Max send priority<select name="maxSendPriority"><option value="">Inherit</option>${meta.priorities.map(p => `<option${p === c.maxSendPriority ? ' selected' : ''}>${p}</option>`).join('')}</select></label>
          ${triSelect('canBroadcast', 'Can broadcast to groups', c.canBroadcast)}
          ${numInput('maxMessagesPerMinute', 'Messages / minute', c.maxMessagesPerMinute, '', 'min="1"')}
          ${numInput('maxRequestsPerMinute', 'Requests / minute', c.maxRequestsPerMinute, '', 'min="1"')}
          ${numInput('maxDelegationsPerHour', 'Delegations / hour', c.maxDelegationsPerHour, '', 'min="0"')}
        </div>
        ${meta.groups.length ? `<h3 class="section">Groups</h3>${checkGrid('groups', meta.groups.map(g => ({ value: g })), c.groups)}` : ''}</div>
      <div class="form-section" id="sec-limits"><h2>Limits</h2><span class="help">Empty = inherit from the base profile.</span>
        <div class="form-grid">
          ${numInput('maxReasoningTurns', 'Turns between progress checks', t.maxReasoningTurns, 'A task still making progress then goes on; a stuck one stops with a report.', 'min="1" max="200"')}
          ${numInput('maxToolCalls', 'Tool calls between progress checks', t.maxToolCalls, '', 'min="1"')}
          ${numInput('maxTotalTurns', 'Total turns / task', t.maxTotalTurns, '0 or empty = no fixed total: budget and AI limits decide.', 'min="0"')}
          ${numInput('maxTaskDurationSeconds', 'Time slice (s)', t.maxTaskDurationSeconds, 'A task still making progress pauses after this and goes on from its checkpoint.', 'min="10"')}
          ${numInput('maxRetries', 'Max retries', t.maxRetries, '', 'min="0" max="10"')}
          ${numInput('retryBaseDelaySeconds', 'Retry delay (s)', t.retryBaseDelaySeconds, '', 'min="1"')}
          ${triSelect('escalateOnRetry', 'Escalate model on retry', t.escalateOnRetry)}
          ${numInput('maxAiCallsPerHour', 'AI calls / hour', ai.maxAiCallsPerHour, '', 'min="1"')}
          ${numInput('maxAiCallsPerDay', 'AI calls / day', ai.maxAiCallsPerDay, '', 'min="1"')}
          ${numInput('maxTokensPerDay', 'Tokens / day', ai.maxTokensPerDay, '', 'min="1"')}
          ${numInput('maxConcurrentAiCalls', 'Concurrent AI calls', ai.maxConcurrentAiCalls, '', 'min="1" max="16"')}
          ${numInput('maxMemories', 'Max memories', mem.maxMemories, '', 'min="0"')}
          ${numInput('recallCount', 'Memories recalled per task', mem.recallCount, '', 'min="0" max="50"')}
          ${triSelect('autoRecordTaskResults', 'Remember task results', mem.autoRecordTaskResults)}
          ${numInput('idleDelaySeconds', 'Idle poll (s)', idle.idleDelaySeconds, '', 'min="1"')}
        </div></div>
      <div class="form-section" id="sec-economy"><h2>Budget & subagents</h2><span class="help">Cloud model use is paid from the agent's wallet (its spending limit) and charged to the account that owns it; the local model is free. Subagents spend from this wallet too.</span>
        <div class="form-grid">
          ${meta.isAdmin && meta.owners ? `<label class="span2">Owner (pays for it)<select name="owner"><option value="">Administrators (never stops for money)</option>${meta.owners.map(o =>
            `<option value="${esc(o.username)}"${o.username === owner ? ' selected' : ''}>${esc(o.username)}${o.isAdmin ? ' (administrator, never stops)' : ' · ' + money(o.balanceMicros, 2)}</option>`).join('')}</select>
            <span class="help">Its cloud use is charged to this account. When a user's balance is empty, all of that user's agents stop until money is added.</span></label>` : ''}
          <label>Initial balance (USD)<input name="initialBalance" type="number" step="0.01" min="0" value="${fval(eco.initialBalance)}" placeholder="${esc(String(meta.economy.defaultInitialBalance))}"><span class="help">Applied when the wallet is first created</span></label>
          <label>Daily spend limit (USD)<input name="dailySpendLimit" type="number" step="0.01" min="0" value="${fval(eco.dailySpendLimit)}" placeholder="inherit"></label>
          ${numInput('subMaxConcurrent', 'Max concurrent subagents', sub.maxConcurrent, `0 disables; host default ${meta.subagents.maxPerAgent}`, 'min="0" max="20"')}
          <label>Default subagent cap (USD)<input name="subDefaultSpendCap" type="number" step="0.01" min="0" value="${fval(sub.defaultSpendCap)}" placeholder="${esc(String(meta.subagents.defaultSpendCap))}"><span class="help">Per subagent, cloud only</span></label>
        </div></div>
      <div class="form-section" id="sec-autonomy"><h2>Autonomy</h2><span class="help">How independently the agent works. Empty fields inherit from the base profile, then from the level's defaults.</span>
        <div class="form-grid">
          <label class="span2">Level<select name="auLevel"><option value="">Inherit (supervised)</option>${['supervised', 'assisted', 'autonomous'].map(l => `<option value="${l}"${sel(au.level, l)}>${l}</option>`).join('')}</select>
            <span class="help">supervised: people decide everything · assisted: takes initiative and directs its own work, small decisions are automatic · autonomous: its policy or supervisor agent decides, people are told</span></label>
          <label class="span2">Supervisor agent<select name="auSupervisor"><option value="">None (a person decides)</option>${agents.map(a => `<option value="${esc(a.id)}"${sel(au.supervisorAgentId, a.id)}>${esc(a.name)}</option>`).join('')}</select>
            <span class="help">Decides its requests the policy does not decide, instead of a person</span></label>
          <label>Budget approved at once (USD)<input name="auBudget" type="number" step="0.01" min="0" value="${fval(au.autoApproveBudgetUsd)}" placeholder="inherit"><span class="help">Per request</span></label>
          <label>… at most per day (USD)<input name="auBudgetDay" type="number" step="0.01" min="0" value="${fval(au.autoApproveBudgetPerDayUsd)}" placeholder="inherit"></label>
          <label>Tool approvals<select name="auToolsMode"><option value=""${sel(toolsMode, '')}>Inherit (autonomous: all, or its supervisor decides)</option><option value="all"${sel(toolsMode, 'all')}>Approve all automatically</option>
            <option value="none"${sel(toolsMode, 'none')}>None (supervisor or a person)</option><option value="list"${sel(toolsMode, 'list')}>Only the tools listed</option></select></label>
          <label>Tools approved automatically<input name="auTools" value="${esc(toolsMode === 'list' ? au.autoApproveTools.join(', ') : '')}" placeholder="tool names, comma separated"></label>
          ${triSelect('auApplyChanges', 'Apply its own behaviour changes', au.autoApplyBehaviorChanges, 'Its evaluation keeps or rolls back each one')}
          ${triSelect('auCanHire', 'May hire helper agents', au.canHire)}
          ${triSelect('auApproveHires', 'Approve its own hires', au.autoApproveHires)}
          ${numInput('auMaxHires', 'Max hires', au.maxHires, '', 'min="0" max="20"')}
          ${numInput('auAskTimeout', 'Unanswered question after (min)', au.askHumanTimeoutMinutes, 'Then its supervisor answers, or it decides itself (0 = wait)', 'min="0"')}
          ${numInput('auInitiative', 'Initiative after idle (min)', au.initiativeMinutes, '0 = never', 'min="0"')}
          ${numInput('auVerify', 'Self-check passes', au.verifyPasses, 'Before a successful result counts', 'min="0" max="5"')}
          ${triSelect('auRunCode', 'Run code in the sandbox', au.runCode)}
          ${numInput('auMaxGoals', 'Goals it may set', au.maxSelfGoals, '', 'min="0"')}
          ${numInput('auMaxSchedules', 'Schedules it may set', au.maxSelfSchedules, '', 'min="0"')}
          <label class="span2">Web hosts it may call<input name="auHosts" value="${esc((au.allowedHttpHosts || []).join(', '))}" placeholder="api.example.com, *.example.org, * (any public host)">
            <span class="help">http_request and vault secrets go only to these hosts; a host named exactly may be on your network</span></label>
          <label class="span2">Knowledge bases<input name="auKbs" value="${esc((au.knowledgeBases || []).join(', '))}" placeholder="ids or names; * = all of its owner's"></label>
          <label class="full">Tools it must not have<input name="auDenied" value="${esc((au.deniedTools || []).join(', '))}" placeholder="e.g. run_code, create_webhook"></label>
        </div>
        <h3 class="section">Manages</h3><span class="help">It sees their work and can give them tasks (assign_task); its hires are always included.</span>
        <label class="check"><input type="checkbox" data-group="auManages" value="*"${(au.manages || []).includes('*') ? ' checked' : ''}> Every agent of the same owner</label>
        ${agents.length ? checkGrid('auManages', agents.map(a => ({ value: a.id, label: a.name, help: a.role })), au.manages) : ''}</div>
      <div class="form-section" id="sec-schedules"><h2>Schedules</h2><span class="help">Recurring work this agent creates for itself.</span>
        <div data-rows="schedule">${(def.schedules || []).map(s => scheduleRow(meta, s)).join('')}</div>
        <button class="btn small" type="button" data-act="add-row" data-kind="schedule">Add schedule</button></div>
      <div class="editor-foot"><div data-errors class="grow"></div><button class="btn" type="button" data-act="preview-agent">Preview JSON</button>
        <a class="btn" href="${editId ? '/agents/' + enc(editId) : '/agents'}" data-nav>Cancel</a><button class="btn primary" type="submit">${editId ? 'Save changes' : 'Create agent'}</button></div>`;
    return pageHead(title, { subtitle: editId ? 'Changes apply to the running agent immediately (its current task keeps the old settings).' : `${meta.agentCount} of ${meta.maxAgents} agents in use.` }) +
      notice + `<div class="editor section">${nav}<form class="card" id="agent-editor" data-form="agent-editor" data-edit="${esc(editId || '')}" novalidate>${body}</form></div>`;
  }
};
ACTIONS['scroll-to'] = el => { const t = document.getElementById(el.dataset.target); if (t) t.scrollIntoView({ behavior: 'smooth', block: 'start' }); };

function collectAgentForm(form) {
  const v = n => { const el = form.elements[n]; return el ? el.value.trim() : ''; };
  const numOrNull = n => { const x = v(n); return x === '' ? null : Number(x); };
  const triOrNull = n => { const x = v(n); return x === '' ? null : x === 'true'; };
  const group = g => $$(`input[data-group="${g}"]:checked`, form).map(i => i.value);
  const inherited = k => { const cb = $(`input[data-change="inherit"][data-target="${k}"]`, form); return cb && cb.checked; };
  const lines = n => v(n).split('\n').map(x => x.trim()).filter(Boolean);
  const csv = n => v(n).split(',').map(x => x.trim()).filter(Boolean);
  const drop = o => { Object.keys(o).forEach(k => { if (o[k] === null || o[k] === '' || (Array.isArray(o[k]) && o[k].length === 0 && k !== 'allowedRecipients')) delete o[k]; }); return Object.keys(o).length ? o : null; };
  const profile = {
    id: v('id'), name: v('name') || null, role: v('role') || null, description: v('description') || null, personality: v('personality') || null,
    baseProfileId: v('baseProfileId') || null, enabled: form.elements.enabled.checked,
    systemPrompt: v('systemPrompt') || null, appendSystemPrompt: v('appendSystemPrompt') || null,
    goals: lines('goals').length ? lines('goals') : null, safetyRules: lines('safetyRules').length ? lines('safetyRules') : null,
    defaultPurpose: v('defaultPurpose') || null,
    allowedModels: inherited('allowedModels') ? null : group('allowedModels'), maxTier: numOrNull('maxTier'),
    internalTools: inherited('internalTools') ? null : group('internalTools'),
    allowedMcpServers: group('allowedMcpServers'), allowedMcpTools: csv('allowedMcpTools'), deniedMcpTools: csv('deniedMcpTools'),
    communication: drop({
      allowedRecipients: group('allowedRecipients'), allowedSendMessageTypes: inherited('sendTypes') ? null : group('sendTypes'),
      maxSendPriority: v('maxSendPriority') || null, groups: group('groups'), canBroadcast: triOrNull('canBroadcast'),
      maxMessagesPerMinute: numOrNull('maxMessagesPerMinute'), maxRequestsPerMinute: numOrNull('maxRequestsPerMinute'), maxDelegationsPerHour: numOrNull('maxDelegationsPerHour')
    }),
    tasks: drop({
      maxReasoningTurns: numOrNull('maxReasoningTurns'), maxToolCalls: numOrNull('maxToolCalls'), maxTotalTurns: numOrNull('maxTotalTurns'), maxTaskDurationSeconds: numOrNull('maxTaskDurationSeconds'),
      maxRetries: numOrNull('maxRetries'), retryBaseDelaySeconds: numOrNull('retryBaseDelaySeconds'), escalateOnRetry: triOrNull('escalateOnRetry')
    }),
    ai: drop({ maxAiCallsPerHour: numOrNull('maxAiCallsPerHour'), maxAiCallsPerDay: numOrNull('maxAiCallsPerDay'), maxTokensPerDay: numOrNull('maxTokensPerDay'), maxConcurrentAiCalls: numOrNull('maxConcurrentAiCalls') }),
    memory: drop({ maxMemories: numOrNull('maxMemories'), recallCount: numOrNull('recallCount'), autoRecordTaskResults: triOrNull('autoRecordTaskResults') }),
    economy: drop({ initialBalance: numOrNull('initialBalance'), dailySpendLimit: numOrNull('dailySpendLimit') }),
    subagents: drop({ maxConcurrent: numOrNull('subMaxConcurrent'), defaultSpendCap: numOrNull('subDefaultSpendCap') }),
    idle: drop({ idleDelaySeconds: numOrNull('idleDelaySeconds') }),
    autonomy: (() => {
      const mode = v('auToolsMode');
      let a = drop({
        level: v('auLevel') || null, supervisorAgentId: v('auSupervisor') || null,
        autoApproveBudgetUsd: numOrNull('auBudget'), autoApproveBudgetPerDayUsd: numOrNull('auBudgetDay'),
        autoApproveTools: mode === 'all' ? ['*'] : mode === 'list' ? csv('auTools') : null,
        autoApplyBehaviorChanges: triOrNull('auApplyChanges'), canHire: triOrNull('auCanHire'), autoApproveHires: triOrNull('auApproveHires'), maxHires: numOrNull('auMaxHires'),
        askHumanTimeoutMinutes: numOrNull('auAskTimeout'), initiativeMinutes: numOrNull('auInitiative'), verifyPasses: numOrNull('auVerify'), runCode: triOrNull('auRunCode'),
        maxSelfGoals: numOrNull('auMaxGoals'), maxSelfSchedules: numOrNull('auMaxSchedules'),
        allowedHttpHosts: csv('auHosts'), knowledgeBases: csv('auKbs'), deniedTools: csv('auDenied'), manages: group('auManages')
      });
      // "None" is an explicit empty list (an autonomous agent would otherwise approve every tool by itself).
      if (mode === 'none') { a = a || {}; a.autoApproveTools = []; }
      return a;
    })(),
    schedules: $$('[data-row="schedule"]', form).map(row => {
      const k = name => $(`[data-k="${name}"]`, row);
      const params = {};
      k('parameters').value.split('\n').map(x => x.trim()).filter(Boolean).forEach(l => { const i = l.indexOf('='); if (i > 0) params[l.slice(0, i).trim()] = l.slice(i + 1).trim(); });
      const s = {
        id: k('id').value.trim(), name: k('name').value.trim() || null, handler: k('handler').value, priority: k('priority').value, kind: k('kind').value,
        intervalSeconds: k('intervalSeconds').value ? Number(k('intervalSeconds').value) : null, cron: k('cron').value.trim() || null,
        enabled: k('enabled').checked, runOnStart: k('runOnStart').checked, description: k('description').value.trim() || null, parameters: params
      };
      return s;
    })
  };
  ['allowedMcpServers', 'allowedMcpTools', 'deniedMcpTools'].forEach(kk => { if (!profile[kk].length) profile[kk] = null; });
  if (!profile.schedules.length) profile.schedules = null;
  const grants = $$('[data-row="grant"]', form).map(row => ({
    modelKey: $('[data-k="modelKey"]', row).value, useFor: $('[data-k="useFor"]', row).value.trim(), allowSubagents: $('[data-k="allowSubagents"]', row).checked
  }));
  const ownerEl = form.elements.owner;
  return ownerEl ? { profile, grants, owner: ownerEl.value || null } : { profile, grants };
}
ACTIONS['preview-agent'] = () => openModal('Agent definition (JSON)', json(collectAgentForm($('#agent-editor'))), '', 'wide');
FORMS['agent-editor'] = async f => {
  const body = collectAgentForm(f);
  const errs = [];
  if (!/^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$/.test(body.profile.id || '')) errs.push('Agent id: 1-64 characters, letters, digits, _ - . (must start with a letter or digit).');
  body.grants.forEach(g => { if ((g.useFor || '').length < 3) errs.push(`Model access ${g.modelKey}: describe what the agent should use it for.`); });
  if (new Set(body.grants.map(g => g.modelKey)).size !== body.grants.length) errs.push('Each additional model can be granted only once.');
  (body.profile.schedules || []).forEach(s => { if (!s.id) errs.push('Every schedule needs an id.'); if (!s.intervalSeconds && !s.cron) errs.push(`Schedule ${s.id || '?'}: set an interval or a cron expression.`); });
  const box = $('[data-errors]', f);
  if (errs.length) { box.innerHTML = `<div class="notice bad"><b>Please fix:</b><ul>${errs.map(e => `<li>${esc(e)}</li>`).join('')}</ul></div>`; return; }
  box.innerHTML = '';
  const r = await post('/api/agents', body);
  toast(`${body.profile.id} saved${r.warnings && r.warnings.length ? ' — ' + r.warnings.length + ' warning(s)' : ''}`, 'ok');
  if (r.warnings && r.warnings.length) setTimeout(() => toast('Warnings: ' + r.warnings.join(' | '), ''), 300);
  navigate('/agents/' + enc(body.profile.id));
};

// ================================================================== custom model editor
VIEWS.modelEditor = {
  live: false,
  async render(p) {
    const key = p.args[0];
    setCrumbs([['Models & access', '/models'], [key ? 'Edit ' + key : 'Register model', '']]);
    const [available, agents, existing] = await Promise.all([get('/api/ai/available-models').catch(() => ({ models: [] })), get('/api/agents'),
      key ? get('/api/models/custom').then(list => list.find(x => x.definition.key === key)) : Promise.resolve(null)]);
    if (key && !existing) return card('', emptyState('Model not found', 'It may have been removed.'));
    const m = existing ? existing.definition : { tier: 3, maxTokens: 4096, contextWindowTokens: 128000, maxConcurrency: 4, temperature: 0.2, timeoutSeconds: 300, enabled: true, capabilities: ['ToolCalling'], pricing: {} };
    const pr = existing ? existing.pricing : (m.pricing || {});
    const grantedIds = new Set((existing ? existing.grants : []).map(g => g.agentId));
    const unconfigured = available.models.filter(x => !x.configuredAs);
    return pageHead(key ? 'Edit custom model' : 'Register a custom model', { subtitle: 'An additional cloud model from your provider. It is never used automatically — only agents you grant it to can use it, for the purpose you state.' }) +
      `<form class="card" id="model-editor" data-form="model-editor" data-key="${esc(key || '')}">
        <div class="form-section"><h2>Model</h2><div class="form-grid">
          <label>Key<input name="key" value="${fval(m.key)}" ${key ? 'readonly' : 'required'} pattern="[A-Za-z0-9][A-Za-z0-9_.\\-]{0,63}" placeholder="e.g. legal-expert"><span class="help">Short name agents and admins use</span></label>
          <label class="span2">Provider model id<input name="modelId" value="${fval(m.modelId)}" required list="provider-models" placeholder="e.g. dddai2/claude-sonnet-5"><span class="help">${num(unconfigured.length)} models offered by the provider are not configured yet</span></label>
          <datalist id="provider-models">${unconfigured.map(x => `<option value="${esc(x.modelId)}"></option>`).join('')}</datalist>
          <label class="full">Description<input name="description" value="${fval(m.description)}" maxlength="500"></label>
          <label class="full">Intended use<textarea name="useFor" rows="2" maxlength="1000" placeholder="Default purpose shown when granting, e.g. drafting and reviewing legal contracts">${fval(m.useFor)}</textarea></label>
        </div></div>
        <div class="form-section"><h2>Pricing</h2><span class="help">Required — cloud models without prices are never used. Costs are charged to the agent that uses the model.</span><div class="form-grid">
          <label>Input (USD per 1M tokens)<input name="inputPerMillion" type="number" step="0.0001" min="0" required value="${fval(pr.inputPerMillion)}"></label>
          <label>Output (USD per 1M tokens)<input name="outputPerMillion" type="number" step="0.0001" min="0" required value="${fval(pr.outputPerMillion)}"></label>
        </div></div>
        <div class="form-section"><h2>Capabilities & limits</h2>
          ${checkGrid('capabilities', CAPABILITIES.map(c => ({ value: c, help: c === 'ToolCalling' ? 'needed for switch_model' : '' })), m.capabilities)}
          <div class="form-grid section">
            <label>Tier<input name="tier" type="number" min="1" max="9" value="${fval(m.tier)}"></label>
            <label>Max output tokens<input name="maxTokens" type="number" min="16" value="${fval(m.maxTokens)}"></label>
            <label>Context window (tokens)<input name="contextWindowTokens" type="number" min="1024" value="${fval(m.contextWindowTokens)}"></label>
            <label>Max concurrent calls<input name="maxConcurrency" type="number" min="1" max="256" value="${fval(m.maxConcurrency)}"></label>
            <label>Temperature<input name="temperature" type="number" step="0.05" min="0" max="2" value="${fval(m.temperature)}"></label>
            <label>Timeout (s)<input name="timeoutSeconds" type="number" min="10" value="${fval(m.timeoutSeconds)}"></label>
            <label class="check"><input type="checkbox" name="enabled"${m.enabled === false ? '' : ' checked'}> Enabled</label>
          </div></div>
        ${key ? '' : `<div class="form-section"><h2>Grant access now (optional)</h2><span class="help">You can also grant it later from the model list or an agent's Model access tab.</span>
          ${agents.map(a => `<div class="repeat-row grant" data-row="new-grant" data-agent="${esc(a.id)}"><label class="check"><input type="checkbox" data-k="on"${grantedIds.has(a.id) ? ' checked' : ''}> ${esc(a.name)} <span class="muted small">${esc(a.id)}</span></label>
            <label>Use it for<input data-k="useFor" placeholder="same as intended use"></label><label class="check"><input type="checkbox" data-k="allowSubagents"> Subagents too</label><span></span></div>`).join('')}</div>`}
        <div class="editor-foot"><div data-errors class="grow"></div><a class="btn" href="/models" data-nav>Cancel</a><button class="btn primary" type="submit">${key ? 'Save model' : 'Register model'}</button></div>
      </form>`;
  }
};
FORMS['model-editor'] = async (f, d) => {
  const key = f.dataset.key;
  const body = {
    key: d.key, modelId: d.modelId, description: d.description || null, useFor: d.useFor || null, tier: Number(d.tier || 1), maxTokens: Number(d.maxTokens || 4096),
    contextWindowTokens: Number(d.contextWindowTokens || 128000), maxConcurrency: Number(d.maxConcurrency || 4), temperature: d.temperature === '' ? null : Number(d.temperature),
    timeoutSeconds: Number(d.timeoutSeconds || 300), enabled: !!f.elements.enabled.checked,
    capabilities: $$('input[data-group="capabilities"]:checked', f).map(i => i.value),
    pricing: { inputPerMillion: d.inputPerMillion === '' ? null : Number(d.inputPerMillion), outputPerMillion: d.outputPerMillion === '' ? null : Number(d.outputPerMillion) }
  };
  if (key) await put('/api/models/custom/' + enc(key), body); else await post('/api/models/custom', body);
  let granted = 0;
  for (const row of $$('[data-row="new-grant"]', f)) {
    if (!$('[data-k="on"]', row).checked) continue;
    await put(`/api/agents/${enc(row.dataset.agent)}/model-grants/${enc(body.key)}`, { useFor: $('[data-k="useFor"]', row).value.trim() || body.useFor || body.description || body.key, allowSubagents: $('[data-k="allowSubagents"]', row).checked });
    granted++;
  }
  toast(`${body.key} ${key ? 'saved' : 'registered'}${granted ? ` and granted to ${plural(granted, 'agent')}` : ''}`, 'ok');
  navigate('/models');
};
