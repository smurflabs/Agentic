'use strict';
/* Agentic console - page views (overview, operations, agents list, subagents, tasks, communications, inbox, finance,
   models & access, MCP, events, configuration, audit) and shared task/agent widgets. */

// ================================================================== shared widgets
function controlButtons(a, small) {
  const s = small ? ' small' : '';
  const b = (op, label, cls = '') => `<button class="btn${s} ${cls}" data-act="agent-control" data-id="${esc(a.id)}" data-op="${op}">${label}</button>`;
  if (a.isSubagent) return `<span class="btn-group">${a.state === 'Paused' ? b('resume', 'Resume') : b('pause', 'Pause')}${b('stop', 'Cancel subagent', 'danger')}</span>`;
  if (a.state === 'Dead') return `<button class="btn${s} primary" data-act="revive-agent" data-id="${esc(a.id)}">Fund &amp; revive…</button>`;
  const running = !['Stopped', 'Disabled', 'Unhealthy', 'Error'].includes(a.state);
  let h = running ? (a.state === 'Paused' ? b('resume', 'Resume') : b('pause', 'Pause')) + b('stop', 'Stop', 'danger') : b('start', 'Start');
  h += b('restart', 'Restart') + b('run-now', 'Run now');
  return `<span class="btn-group">${h}</span>`;
}
ACTIONS['agent-control'] = async el => {
  const op = el.dataset.op, id = el.dataset.id;
  if (op === 'stop' || op === 'restart') {
    const sub = id.includes('.sub-');
    if (!await confirmBox(sub ? 'Cancel subagent' : (op === 'stop' ? 'Stop agent' : 'Restart agent'),
      sub ? `End subagent <b>${esc(id)}</b>? Its parent receives a cancelled result.` : `${op === 'stop' ? 'Stop' : 'Restart'} <b>${esc(id)}</b>? A running task is interrupted and resumes later from its checkpoint.`,
      { danger: op === 'stop', confirm: sub ? 'Cancel subagent' : (op === 'stop' ? 'Stop' : 'Restart') })) return;
  }
  const r = await post(`/api/agents/${enc(id)}/${op}`);
  toast(`${id}: ${r.message}`, 'ok'); refreshNow();
};
/** A dead agent comes back once its wallet holds money again: credit it here and it restarts. */
ACTIONS['revive-agent'] = el => {
  const id = el.dataset.id;
  openModal('Fund & revive ' + id, `<form id="revive-form" class="stack" data-form="revive-agent" data-id="${esc(id)}">
      <p>${esc(id)} died because its money ran out. Crediting its wallet brings it back to life; its queued work then continues.</p>
      <label>Amount (USD)<input name="amount" type="number" min="0.01" step="0.01" value="5" required></label>
      <label>Note<input name="note" maxlength="300" value="Revived by the administrator"></label></form>`,
    `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="submit-form" data-form-id="revive-form">Credit &amp; revive</button>`, 'narrow');
};
FORMS['revive-agent'] = async (f, d) => {
  const id = f.dataset.id;
  await post(`/api/agents/${enc(id)}/adjust`, { amount: Number(d.amount), description: d.note || 'Revived', idempotencyKey: newKey() });
  try { await post(`/api/agents/${enc(id)}/revive`); } catch { /* the credit already revives it */ }
  toast(`${id} is alive again`, 'ok'); closeModal(); refreshNow();
};

function taskCell(t, opts = {}) {
  const meta = [t.kind, t.handler, t.purpose, t.modelKey ? 'model ' + t.modelKey : null].filter(Boolean).join(' · ');
  return `<a href="#" class="cell-title" data-act="open-task" data-id="${esc(t.agentId)}" data-task="${esc(t.id)}">${esc(t.name)}</a>` +
    (opts.noMeta ? '' : `<div class="cell-sub">${esc(meta)}</div>`);
}
function statusCell(t) {
  const st = taskStatus(t);
  return badge(st) + (t.escalations ? ' ' + tag('escalated ×' + t.escalations, 'warn') : '') +
    (t.status === 'Retrying' && t.nextRunUtc ? `<div class="cell-sub">next ${esc(ago(t.nextRunUtc))}</div>` : '') +
    (t.status === 'Pending' && t.nextRunUtc && new Date(t.nextRunUtc) > new Date() ? `<div class="cell-sub">starts ${esc(ago(t.nextRunUtc))}</div>` : '');
}
function originCell(t) {
  if (t.delegatingAgentId) return 'from ' + agentLink(t.delegatingAgentId);
  return `<span class="muted">${esc(t.createdBy || '—')}</span>`;
}
function taskActions(t, small = true) {
  const s = small ? ' xs' : '';
  const d = `data-id="${esc(t.agentId)}" data-task="${esc(t.id)}"`;
  const parts = [];
  const st = t.status;
  if (st === 'Blocked' && taskStatus(t) === 'Held') parts.push(`<button class="btn${s}" data-act="task-op" data-op="release" ${d}>Release</button>`);
  if (st === 'Pending' || st === 'Retrying') parts.push(`<button class="btn${s}" data-act="task-op" data-op="hold" ${d}>Hold</button>`);
  if (['Failed', 'Cancelled'].includes(st) || (st === 'Blocked' && taskStatus(t) !== 'Held')) parts.push(`<button class="btn${s}" data-act="retry-task" ${d}>Retry</button>`);
  if (['Completed', 'Failed', 'Cancelled'].includes(st)) parts.push(`<button class="btn${s}" data-act="task-op" data-op="clone" ${d}>Run again</button>`);
  if (['Pending', 'Retrying', 'Waiting', 'Blocked', 'Running'].includes(st)) parts.push(`<button class="btn${s} danger" data-act="cancel-task" ${d}>Cancel</button>`);
  return parts.join(' ');
}
function taskTable(tasks, opts = {}) {
  return table([
    { h: 'Task', cls: 'clip', f: t => taskCell(t) },
    ...(opts.agent ? [] : [{ h: 'Agent', f: t => agentLink(t.agentId) }]),
    { h: 'Status', f: statusCell },
    { h: 'Priority', f: t => badge(t.priority, undefined, false) },
    { h: 'Created', f: t => `<span data-tip="${esc(ts(t.createdUtc))}">${esc(ago(t.createdUtc))}</span>` },
    { h: 'Attempts', num: true, f: t => `${t.retryCount + 1}/${t.maxRetries + 1}` },
    { h: 'Cost', num: true, f: t => t.costMicros ? money(t.costMicros) : '<span class="muted">—</span>' },
    { h: 'Origin', f: originCell },
    { h: '', cls: 'act', f: t => taskActions(t) + ' ' + link('/correlations/' + enc(t.correlationId), 'workflow', 'small') }
  ], tasks, { empty: opts.empty || 'No tasks.', sel: opts.sel, rowAttrs: t => `data-state="${esc(t.status)}"`, rowClass: t => t.status === 'Running' ? 'is-running' : '' });
}

ACTIONS['open-task'] = async el => openTask(el.dataset.id, el.dataset.task, el.dataset.transcript === '1');
ACTIONS['cancel-task'] = async el => {
  if (!await confirmBox('Cancel task', 'Cancel this task? A running task stops at the next safe point; its subagents are cancelled too.', { danger: true, confirm: 'Cancel task' })) return;
  const r = await post(`/api/agents/${enc(el.dataset.id)}/tasks/${enc(el.dataset.task)}/cancel`);
  toast(r.message, 'ok'); closeModal(); refreshNow();
};
ACTIONS['retry-task'] = async el => { const r = await post(`/api/agents/${enc(el.dataset.id)}/tasks/${enc(el.dataset.task)}/retry`); toast(r.message, 'ok'); closeModal(); refreshNow(); };
ACTIONS['task-op'] = async el => {
  const r = await post(`/api/agents/${enc(el.dataset.id)}/tasks/${enc(el.dataset.task)}/${el.dataset.op}`);
  toast(el.dataset.op === 'clone' ? 'Queued again as ' + r.taskId : r.message, 'ok'); closeModal(); refreshNow();
};

/** Review, delivery and attachments of a task (task details dialog). */
function deliveryKv(d) {
  const dl = d.delivery;
  if (!dl) return '';
  const parts = [];
  if (dl.reviewOfTaskId || dl.reviewOfGlobalTaskId) parts.push(`<dt>Review of</dt><dd>${dl.reviewOfTaskId ? `<code>${esc(dl.reviewOfTaskId)}</code>` : link('/global-tasks/' + enc(dl.reviewOfGlobalTaskId), 'global task')}</dd>`);
  if (dl.reviewerAgentId) parts.push(`<dt>Checked by</dt><dd>${agentLink(dl.reviewerAgentId)} · up to ${num(dl.maxRevisions)} revision(s)${dl.revision ? ` · this is revision ${num(dl.revision)}` : ''}</dd>`);
  if (d.reviewCriteria) parts.push(`<dt>Expected outcome</dt><dd><span class="badge warn">reviewer only</span><pre>${esc(d.reviewCriteria)}</pre></dd>`);
  if (dl.deliver) parts.push(`<dt>Delivery</dt><dd>${d.resultId ? link('/results/' + enc(d.resultId), 'Open the result', 'btn xs primary') : 'Shown in Results when finished'}</dd>`);
  if (dl.attachments && dl.attachments.length) parts.push(`<dt>Attached files</dt><dd>${dl.attachments.map(a => `<div>${esc(a.name)} <span class="muted small">${esc(a.summary || '')}${a.artifactId ? ' · ' + esc(a.artifactId) : ''}</span></div>`).join('')}</dd>`);
  return parts.join('');
}
/** One step of a task's conversation: who spoke, the tool calls it made (with their arguments) and the text. */
function transcriptStep(m) {
  const calls = (m.toolCalls || []).map(c => typeof c === 'string' ? { name: c } : c);
  const callsHtml = calls.length ? `<div class="small">${calls.map(c => c.arguments
    ? `<details><summary><code>${esc(c.name)}</code></summary><pre class="json">${jsonOr(c.arguments)}</pre></details>` : `<code>${esc(c.name)}</code>`).join('')}</div>` : '';
  const who = m.role === 'tool' && m.name ? `tool · ${m.name}` : m.role;
  return `<div class="section"><b>${esc(who)}</b>${callsHtml}${m.content ? `<pre class="json">${jsonOr(m.content)}</pre>` : ''}</div>`;
}
async function openTask(agentId, taskId, transcript) {
  const d = await get(`/api/agents/${enc(agentId)}/tasks/${enc(taskId)}${transcript ? '?transcript=1' : ''}`);
  const t = d.task;
  const editable = ['Pending', 'Retrying', 'Blocked'].includes(t.status);
  const body = `<div class="row">${statusCell(t)} ${tag(t.kind)} ${badge(t.priority, undefined, false)} ${t.modelKey ? tag('pinned: ' + t.modelKey, 'accent') : ''} ${link('/correlations/' + enc(t.correlationId), 'Workflow tree')}</div>
    <dl class="kv section"><dt>Task</dt><dd><b>${esc(t.name)}</b> <code class="muted">${esc(t.id)}</code></dd><dt>Agent</dt><dd>${agentLink(t.agentId)}</dd>
    <dt>Instructions</dt><dd>${t.description ? `<pre>${esc(t.description)}</pre>` : '—'}</dd>
    <dt>Lineage</dt><dd class="small">parent ${esc(t.parentTaskId || '—')} · from ${esc(t.delegatingAgentId || t.createdBy || '—')} · depth ${t.delegationDepth} · path ${esc((t.ancestry || []).join(' → ') || '—')}</dd>
    <dt>Timing</dt><dd class="small">created ${esc(ts(t.createdUtc))} · started ${esc(ts(t.startedUtc))} · completed ${esc(ts(t.completedUtc))}${t.nextRunUtc ? ' · next run ' + esc(ts(t.nextRunUtc)) : ''}</dd>
    <dt>Model</dt><dd>ladder level ${t.ladderLevel} · ${t.escalations} escalation(s) · cost ${money(t.costMicros)}</dd>
    ${t.waitingOn && t.waitingOn.length ? `<dt>Waiting on</dt><dd>${chips(t.waitingOn)} until ${esc(ts(t.waitDeadlineUtc))}</dd>` : ''}
    ${t.error ? `<dt>Error</dt><dd class="error-text">${esc(t.error)}</dd>` : ''}
    ${runKv(t.deadlineEnforced ? t : Object.assign({}, t, { deadlineUtc: null }))}
    ${deliveryKv(d)}</dl>
    ${t.result ? `<h3 class="section">Result</h3>${json(t.result)}` : ''}
    <h3 class="section">History</h3>${table([{ h: 'When', f: h => esc(ts(h.createdUtc)) }, { h: 'Status', f: h => `${esc(h.from || '')} → ${badge(h.to)}` }, { h: 'Note', f: h => esc(h.note || '') }], d.history)}
    <h3 class="section">AI calls</h3>${aiCallsTable(d.aiCalls)}
    <h3 class="section">Tool calls</h3>${table([{ h: 'Tool', f: c => `<code>${esc(c.toolName)}</code>` }, { h: 'Result', f: c => c.success === null ? badge('incomplete', 'warn') : (c.success ? badge('OK', 'ok') : badge('Failed', 'bad')) },
      { h: 'Duration', num: true, f: c => dur(c.durationMs) }, { h: 'Arguments', cls: 'clip', f: c => `<code data-tip="${esc((c.arguments || '').slice(0, 600))}">${esc(c.arguments || '')}</code>` },
      { h: 'Output', cls: 'clip', f: c => `<span data-tip="${esc((c.result || '').slice(0, 600))}">${esc(c.result || c.errorMessage || '')}</span>` }], d.toolCalls, { empty: 'No tool calls.' })}
    ${d.checkpoint ? `<p class="muted small section">Checkpoint: turn ${d.checkpoint.turn}, ${d.checkpoint.toolCallsUsed} tool calls, ${d.checkpoint.transcriptMessages} transcript messages.
      ${transcript ? '' : `<a href="#" data-act="open-task" data-id="${esc(agentId)}" data-task="${esc(taskId)}" data-transcript="1">Show transcript</a>`}</p>` :
      (!transcript && (d.delivery || t.handler === 'reasoning') && ['Completed', 'Failed', 'Cancelled'].includes(t.status) ? `<p class="muted small section"><a href="#" data-act="open-task" data-id="${esc(agentId)}" data-task="${esc(taskId)}" data-transcript="1">Show the conversation</a></p>` : '')}
    ${d.transcript ? `<h3>Transcript</h3>${d.transcript.length ? d.transcript.map(transcriptStep).join('') : '<p class="muted">This task kept no conversation.</p>'}` : (transcript ? '<p class="muted section">This task kept no conversation (it finished before conversations were kept for every task).</p>' : '')}`;
  const acts = discussButton(`data-agent="${esc(agentId)}" data-task="${esc(taskId)}"`) +
    (editable ? `<button class="btn" data-act="edit-task" data-id="${esc(agentId)}" data-task="${esc(taskId)}">Edit</button>` : '') + taskActions(t, false);
  openModal('Task details', body, acts, 'wide');
}

function aiCallsTable(calls) {
  return table([
    { h: 'When', f: c => `<span data-tip="${esc(ts(c.createdUtc))}">${esc(ago(c.createdUtc))}</span>` }, { h: 'Model', f: c => `<code>${esc(c.modelKey)}</code>` },
    { h: 'Purpose', f: c => esc(c.purpose) }, { h: 'Reason', f: c => badge(c.reason, c.reason === 'Primary' ? 'outline' : (c.reason === 'CustomModel' ? 'accent' : 'warn'), false) },
    { h: 'Result', f: c => c.success ? badge('OK', 'ok') : badge(c.errorKind || 'Failed', 'bad') }, { h: 'Queue', num: true, f: c => dur(c.queueWaitMs) },
    { h: 'Duration', num: true, f: c => dur(c.durationMs) }, { h: 'Tokens in/out', num: true, f: c => `${num(c.promptTokens)} / ${num(c.completionTokens)}${c.usageEstimated ? '*' : ''}` },
    { h: 'Cost', num: true, f: c => c.costMicros ? money(c.costMicros) : '—' }
  ], calls, { empty: 'No AI calls yet.' });
}
function ledgerTable(entries, showAgent) {
  return table([
    { h: 'When', f: e => `<span data-tip="${esc(ts(e.createdUtc))}">${esc(ago(e.createdUtc))}</span>` },
    ...(showAgent ? [{ h: 'Wallet', f: e => agentLink(e.agentId) }] : []),
    { h: 'Type', f: e => badge(e.entryType, e.amountMicros >= 0 ? 'ok' : 'outline', false) }, { h: 'Amount', num: true, f: e => money(e.amountMicros) },
    { h: 'Balance after', num: true, f: e => money(e.balanceAfterMicros) }, { h: 'Model', f: e => e.modelKey ? `<code>${esc(e.modelKey)}</code>` : '' },
    { h: 'Used by', f: e => e.spentByAgentId ? tag('subagent ' + e.spentByAgentId.split('.sub-')[1], 'info') : '' },
    { h: 'Description', cls: 'clip', f: e => `<span data-tip="${esc(e.description || '')}">${esc(e.description || '')}</span>` + (e.reference ? ` <span class="muted">ref ${esc(e.reference)}</span>` : '') },
    { h: 'By', f: e => esc(e.createdBy) }
  ], entries, { empty: 'No ledger entries.' });
}
function hourLabel(r) { const d = new Date(r.bucketUtc); return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }); }
function statsCharts(stats, keyPrefix) {
  const done = stats.reduce((s, r) => s + r.tasksCompleted, 0), failed = stats.reduce((s, r) => s + r.tasksFailed, 0);
  const cost = stats.reduce((s, r) => s + r.costMicros, 0);
  const c1 = chartBox(keyPrefix + '-tasks', {
    rows: stats, y: r => r.tasksCompleted, x: hourLabel, minMax: 4, axis: v => num(Math.round(v)), label: 'Tasks completed per hour, last 24 hours',
    tip: r => `${hourLabel(r)}\n${num(r.tasksCompleted)} completed · ${num(r.tasksFailed)} failed · ${num(r.aiCalls)} AI calls`
  });
  const c2 = chartBox(keyPrefix + '-cost', {
    rows: stats, y: r => r.costMicros / 1e6, x: hourLabel, minMax: 0.1, axis: v => v === 0 ? '$0' : '$' + (v >= 1 ? v.toFixed(2) : String(Number(v.toPrecision(2)))), label: 'Cloud spend per hour, last 24 hours',
    tip: r => `${hourLabel(r)}\n${money(r.costMicros)} cloud spend · ${num(r.tokens)} tokens`
  });
  return `<div class="grid two section">
    ${card('Tasks completed per hour', `<div class="chart-meta"><span class="value">${num(done)}</span><span class="muted small">${num(failed)} failed · last 24 h</span></div>${c1}`, { actions: `<button class="btn xs ghost" data-act="stats-table" data-key="${keyPrefix}">Table</button>` })}
    ${card('Cloud spend per hour', `<div class="chart-meta"><span class="value">${money(cost, 2)}</span><span class="muted small">last 24 h · local model is free</span></div>${c2}`, { actions: `<button class="btn xs ghost" data-act="stats-table" data-key="${keyPrefix}">Table</button>` })}
  </div>`;
}
ACTIONS['stats-table'] = el => {
  const spec = S.charts[el.dataset.key + '-tasks'];
  if (!spec) return;
  openModal('Hourly statistics (last 24 hours)', table([
    { h: 'Hour', f: r => esc(new Date(r.bucketUtc).toLocaleString()) }, { h: 'Completed', num: true, f: r => num(r.tasksCompleted) },
    { h: 'Failed', num: true, f: r => num(r.tasksFailed) }, { h: 'AI calls', num: true, f: r => num(r.aiCalls) }, { h: 'Tool calls', num: true, f: r => num(r.toolCalls) },
    { h: 'Messages', num: true, f: r => num(r.messagesSent) }, { h: 'Tokens', num: true, f: r => num(r.tokens) }, { h: 'Cloud spend', num: true, f: r => money(r.costMicros) }
  ], spec.rows.slice().reverse()), '', 'wide');
};

function agentCardHtml(a) {
  const hasWallet = a.availableMicros !== null && a.availableMicros !== undefined;
  const rw = runwayInfo(a.runwayDays, a.lowRunway);
  return `<div class="card agent-card s-${esc(a.state)}" data-key="${esc(a.id)}" data-state="${esc(a.state)}">
    <div class="head">${agentLink(a.id, a.name)}<span class="spacer"></span><span data-k="state:${esc(a.id)}">${badge(a.state)}</span></div>
    <div class="work">${a.currentTaskName ? `${esc(a.currentTaskName)}<div class="cell-sub">${esc(a.currentOperation || '')}</div>` : `<span class="muted">${esc(a.role || '')}</span>`}</div>
    <div class="stats"><span><b data-k="queued:${esc(a.id)}">${num(a.queuedTasks || 0)}</b> queued</span><span><b>${num(a.activeSubagents || 0)}</b> subagents</span>` +
    `<span data-tip="${esc(!hasWallet ? '' : a.runwayDays === null || a.runwayDays === undefined ? 'No recent cloud spend' : 'Lasts ' + rw.text + ' at the recent spending rate')}"><b class="runway ${rw.cls}">${hasWallet ? money(a.availableMicros, 2) : '—'}</b> balance</span></div>
  </div>`;
}

ACTIONS['global'] = async el => {
  const op = el.dataset.op;
  if (op === 'pause-all' && !await confirmBox('Pause all agents', 'Pause every agent and subagent? Running tasks finish first; nothing new starts until you resume.', { confirm: 'Pause all' })) return;
  const r = await post('/api/operations/' + op);
  toast(r.message, 'ok'); refreshNow();
};

// ================================================================== agents list
VIEWS.agents = {
  live: true,
  async render(p) {
    setCrumbs([['Agents', '/agents']]);
    const q = (p.query.get('q') || '').toLowerCase(), state = p.query.get('state') || '', source = p.query.get('source') || '';
    const all = await get('/api/agents?details=1');
    const list = all.filter(a => (!q || a.id.toLowerCase().includes(q) || (a.name || '').toLowerCase().includes(q) || (a.role || '').toLowerCase().includes(q)) &&
      (!state || a.state === state) && (!source || a.source === source));
    const states = [...new Set(all.map(a => a.state))].sort();
    const opt = (v, cur, label) => `<option value="${esc(v)}"${v === cur ? ' selected' : ''}>${esc(label || v || 'All')}</option>`;
    const filters = `<form class="filters" data-form="agent-filter">
      <label class="grow">Search<input name="q" value="${esc(p.query.get('q') || '')}" placeholder="Name, id or role"></label>
      <label>State<select name="state">${opt('', state)}${states.map(s => opt(s, state)).join('')}</select></label>
      <button class="btn">Apply</button></form>`;
    const bulk = bulkBar('agents', ['start', 'pause', 'resume', 'restart', 'stop'].map(op =>
      `<button class="btn small${op === 'stop' ? ' danger' : ''}" data-act="bulk-agents" data-op="${op}">${op[0].toUpperCase() + op.slice(1)}</button>`).join(''));
    const rows = table([
      { h: 'Agent', cls: 'nw', f: a => `${agentLink(a.id, a.name)} ${a.grantedModels ? ` <a href="/agents/${enc(a.id)}/models" data-nav>${tag('+' + a.grantedModels + ' model' + (a.grantedModels === 1 ? '' : 's'), 'info')}</a>` : ''}<div class="cell-sub">${esc(a.role || '')} · <code>${esc(a.id)}</code></div>` },
      { h: 'State', cls: 'nw', f: a => badge(a.state) + (a.unhealthy ? ' ' + badge('Unhealthy', 'bad') : '') +
          (a.unfunded ? ' ' + `<span data-tip="Its owner ${esc(a.owner || '')} has no money: it continues once money is added">${badge('No money', 'bad', false)}</span>` : '') },
      { h: 'Current work', cls: 'clip', f: a => a.currentTaskName ? `<a href="#" data-act="open-task" data-id="${esc(a.id)}" data-task="${esc(a.currentTaskId)}">${esc(a.currentTaskName)}</a><div class="cell-sub">${esc(a.currentOperation || '')}</div>` : '<span class="muted">—</span>' },
      { h: 'Queue', num: true, f: a => `<a href="/agents/${enc(a.id)}/queue" data-nav>${num(a.queuedTasks)}</a><div class="cell-sub">${num(a.waitingTasks)} waiting · ${num(a.heldTasks)} held</div>` },
      { h: 'Subagents', num: true, f: a => `${num(a.activeSubagents)}<span class="muted"> / ${num(a.maxSubagents)}</span>` },
      { h: 'Owner', cls: 'nw', f: a => a.owner ? esc(a.owner) : '<span class="muted">administrators</span>' },
      { h: 'Balance', num: true, f: a => `${a.availableMicros !== null && a.availableMicros !== undefined ? money(a.availableMicros, 2) : '—'}<div class="cell-sub">${a.spentTodayMicros ? money(a.spentTodayMicros, 2) : '$0'} today` +
        `${a.runwayDays !== null && a.runwayDays !== undefined ? ` · <span data-tip="How long the balance lasts at the recent spending rate">${runway(a.runwayDays, a.lowRunway)}</span>` : ''}</div>` },
      { h: 'Last activity', cls: 'clip', f: a => `${esc(ago(a.lastActivityUtc))}<div class="cell-sub">${esc(a.lastActivity || '')}</div>` },
      { h: '', cls: 'act', f: a => `${link('/agents/' + enc(a.id) + '/queue', 'Queue', 'btn xs')} <button class="btn xs" data-act="add-task" data-id="${esc(a.id)}">Add task</button> ${link('/agents/' + enc(a.id) + '/edit', 'Edit', 'btn xs')}` }
    ], list, { sel: { scope: 'agents', id: a => a.id }, empty: 'No agents match.', rowAttrs: a => `data-state="${esc(a.state)}"`, rowClass: a => a.state === 'Running' ? 'is-running' : '' });
    return pageHead('Agents', {
      subtitle: `${list.length} of ${all.length} agents shown`,
      actions: `<a href="/agents/new" data-nav class="btn primary">New agent</a>`
    }) + card('', filters + '<div class="section"></div>' + bulk + rows, { export: 'agents' });
  }
};
FORMS['agent-filter'] = (f, d) => { const p = new URLSearchParams(); ['q', 'state', 'source'].forEach(k => { if (d[k]) p.set(k, d[k]); }); navigate('/agents' + (p.toString() ? '?' + p : '')); };
ACTIONS['bulk-agents'] = async el => {
  const ids = selected('agents'), op = el.dataset.op;
  if (!ids.length) return;
  if ((op === 'stop' || op === 'restart') && !await confirmBox(op === 'stop' ? 'Stop agents' : 'Restart agents', `${op === 'stop' ? 'Stop' : 'Restart'} ${plural(ids.length, 'agent')}?`, { danger: op === 'stop', confirm: op })) return;
  let ok = 0; const errors = [];
  for (const id of ids) { try { await post(`/api/agents/${enc(id)}/${op}`); ok++; } catch (e) { errors.push(`${id}: ${e.message}`); } }
  toast(`${op}: ${ok} succeeded${errors.length ? ', ' + errors.length + ' failed — ' + errors[0] : ''}`, errors.length ? 'bad' : 'ok');
  clearSel('agents'); refreshNow();
};

// ================================================================== subagents
function subagentTable(list, opts = {}) {
  return table([
    { h: 'Subagent', f: s => `${s.status === 'Running' ? agentLink(s.id, s.name) : `<span class="cell-title">${esc(s.name)}</span>`}<div class="cell-sub"><code>${esc(s.id)}</code></div>` },
    { h: 'Parent', f: s => agentLink(s.parentAgentId) + (s.parentTaskId ? `<div class="cell-sub"><a href="#" data-act="open-task" data-id="${esc(s.parentAgentId)}" data-task="${esc(s.parentTaskId)}">parent task</a></div>` : '') },
    { h: 'Status', f: s => badge(s.status) + (s.state && s.status === 'Running' && s.state !== 'Running' ? ` <span class="muted small">${esc(s.state)}</span>` : '') + (s.currentOperation ? `<div class="cell-sub clip">${esc(s.currentOperation)}</div>` : '') },
    ...(opts.compact ? [] : [{ h: 'Models', f: s => chips(s.models) }]),
    { h: 'Cloud spend', num: true, f: s => `${money(s.spentMicros)}${s.spendCapMicros !== null && s.spendCapMicros !== undefined ? `<div class="cell-sub">cap ${money(s.spendCapMicros, 2)}</div>` : ''}` },
    { h: 'Started', f: s => `<span data-tip="${esc(ts(s.createdUtc))}">${esc(ago(s.createdUtc))}</span>` },
    { h: 'Timing', f: s => s.status === 'Running' ? `<span class="muted small">deadline ${esc(ago(s.deadlineUtc))}</span>` : `<span class="muted small">ended ${esc(ago(s.endedUtc))}</span>` },
    { h: 'Result', cls: 'clip', f: s => s.summary ? `<span data-tip="${esc(s.summary.slice(0, 500))}">${s.success ? '' : '<span class="error-text">✕ </span>'}${esc(s.summary)}</span>` : '<span class="muted">—</span>' },
    { h: '', cls: 'act', f: s => s.status === 'Running' ? `<button class="btn xs danger" data-act="cancel-subagent" data-parent="${esc(s.parentAgentId)}" data-sub="${esc(s.id)}">Cancel</button>` : (s.resultJson ? `<button class="btn xs" data-act="show-subagent" data-sub="${esc(s.id)}">Result</button>` : '') }
  ], list, { empty: 'No subagents.', emptyText: opts.emptyText || 'Agents spawn subagents for parallel subtasks; they appear here while they work.',
    rowAttrs: s => `data-state="${esc(s.status)}"`, rowClass: s => s.status === 'Running' ? 'is-running' : '' });
}
ACTIONS['cancel-subagent'] = async el => {
  if (!await confirmBox('Cancel subagent', `End subagent <b>${esc(el.dataset.sub)}</b>? Its parent receives a cancelled result and continues.`, { danger: true, confirm: 'Cancel subagent' })) return;
  const r = await post(`/api/agents/${enc(el.dataset.parent)}/subagents/${enc(el.dataset.sub)}/cancel`);
  toast(r.message, 'ok'); refreshNow();
};
ACTIONS['show-subagent'] = async el => {
  // By id, however old (the list only holds the most recent subagents).
  let s;
  try { s = await get('/api/subagents/' + enc(el.dataset.sub)); } catch (e) { toast(e.message, 'bad'); return; }
  let result = s.resultJson; try { result = JSON.parse(s.resultJson); } catch { /* raw */ }
  openModal('Subagent ' + s.name, `<dl class="kv"><dt>Id</dt><dd><code>${esc(s.id)}</code></dd><dt>Parent</dt><dd>${agentLink(s.parentAgentId)}</dd><dt>Status</dt><dd>${badge(s.status)}</dd>
    <dt>Models</dt><dd>${chips(s.models)}</dd><dt>Cloud spend</dt><dd>${money(s.spentMicros)} of ${money(s.spendCapMicros, 2)} cap (charged to ${esc(s.parentAgentId)})</dd>
    <dt>Ran</dt><dd>${esc(ts(s.createdUtc))} → ${esc(ts(s.endedUtc))}</dd></dl><h3 class="section">Result handed to the parent</h3>${json(result)}`);
};
VIEWS.subagents = {
  live: true,
  async render(p) {
    setCrumbs([['Subagents', '/subagents']]);
    const status = p.query.get('status') || '';
    const list = await get('/api/subagents?limit=300' + (status ? '&status=' + enc(status) : ''));
    const running = list.filter(s => s.status === 'Running');
    const spend = list.reduce((s, x) => s + (x.spentMicros || 0), 0);
    const opt = v => `<option value="${v}"${v === status ? ' selected' : ''}>${v || 'All'}</option>`;
    return pageHead('Subagents', { subtitle: 'Temporary helpers agents spawn for parallel subtasks. Local-model work is free; cloud use is charged to the parent agent, capped per subagent.' }) +
      `<div class="grid kpis">${kpi('Running', num(running.length))}${kpi('Shown', num(list.length))}${kpi('Cloud spend (shown)', money(spend, 2), 'charged to parent wallets')}
        ${kpi('Succeeded', num(list.filter(s => s.status === 'Completed' && s.success).length))}${kpi('Failed / cancelled', num(list.filter(s => s.status === 'Failed' || s.status === 'Cancelled').length))}</div>` +
      card('', `<form class="filters" data-form="subagent-filter"><label>Status<select name="status">${['', 'Running', 'Completed', 'Failed', 'Cancelled'].map(opt).join('')}</select></label><button class="btn">Apply</button></form>`, { cls: 'section' }) +
      card('', subagentTable(list), { flush: true });
  }
};
FORMS['subagent-filter'] = (f, d) => navigate('/subagents' + (d.status ? '?status=' + enc(d.status) : ''));

// ================================================================== tasks (all agents)
VIEWS.tasks = {
  live: true,
  async render(p) {
    setCrumbs([['Tasks', '/tasks']]);
    const q = p.query;
    const offset = Number(q.get('offset') || 0), limit = 100;
    const params = new URLSearchParams();
    ['agent', 'status', 'q'].forEach(k => { if (q.get(k)) params.set(k, q.get(k)); });
    params.set('limit', limit); params.set('offset', offset);
    const [list, agents] = await Promise.all([get('/api/tasks?' + params), get('/api/agents')]);
    const statuses = ['', 'Pending', 'Running', 'Waiting', 'Retrying', 'Blocked', 'Completed', 'Failed', 'Cancelled'];
    const sel = (name, values, labels) => `<label>${name === 'agent' ? 'Agent' : 'Status'}<select name="${name}">${values.map((v, i) => `<option value="${esc(v)}"${(q.get(name) || '') === v ? ' selected' : ''}>${esc(labels ? labels[i] : (v || 'All'))}</option>`).join('')}</select></label>`;
    const filters = `<form class="filters" data-form="task-filter">
      <label class="grow">Search<input name="q" value="${esc(q.get('q') || '')}" placeholder="Task name, text, task id or workflow id"></label>
      ${sel('agent', ['', ...agents.map(a => a.id)], ['All agents', ...agents.map(a => a.name)])}${sel('status', statuses)}
      <button class="btn">Apply</button></form>`;
    const pager = `<div class="row section"><span class="muted small">Showing ${offset + 1}–${offset + list.length}</span><span class="spacer"></span>
      ${offset > 0 ? `<button class="btn small" data-act="task-page" data-offset="${Math.max(0, offset - limit)}">Previous</button>` : ''}
      ${list.length === limit ? `<button class="btn small" data-act="task-page" data-offset="${offset + limit}">Next</button>` : ''}</div>`;
    const bulk = bulkBar('tasks', `<button class="btn small danger" data-act="bulk-tasks" data-op="cancel">Cancel</button><button class="btn small" data-act="bulk-tasks" data-op="retry">Retry</button>`);
    return pageHead('Tasks', { subtitle: 'Every agent\'s work. Open an agent\'s queue to reorder, edit or hold its tasks.' }) +
      card('', filters + '<div class="section"></div>' + bulk + taskTable(list.map(t => Object.assign(t, { _key: t.agentId + '|' + t.id })), { sel: { scope: 'tasks', id: t => t._key } }) + pager, { export: 'tasks' });
  }
};
FORMS['task-filter'] = (f, d) => { const p = new URLSearchParams(); ['q', 'agent', 'status'].forEach(k => { if (d[k]) p.set(k, d[k]); }); navigate('/tasks' + (p.toString() ? '?' + p : '')); };
ACTIONS['task-page'] = el => { const p = new URLSearchParams(location.search); p.set('offset', el.dataset.offset); navigate('/tasks?' + p); };
ACTIONS['bulk-tasks'] = async el => {
  const keys = selected('tasks'), op = el.dataset.op;
  if (!keys.length) return;
  if (op === 'cancel' && !await confirmBox('Cancel tasks', `Cancel ${plural(keys.length, 'task')}?`, { danger: true, confirm: 'Cancel tasks' })) return;
  const byAgent = {};
  keys.forEach(k => { const [a, t] = k.split('|'); (byAgent[a] = byAgent[a] || []).push(t); });
  let ok = 0, failed = 0;
  for (const [agent, ids] of Object.entries(byAgent)) {
    const r = await post(`/api/agents/${enc(agent)}/tasks/bulk`, { action: op, taskIds: ids });
    ok += r.succeeded; failed += r.failed;
  }
  toast(`${op}: ${ok} succeeded${failed ? `, ${failed} not applicable` : ''}`, failed && !ok ? 'bad' : 'ok');
  clearSel('tasks'); refreshNow();
};

// ================================================================== console (live agent reasoning stream)
VIEWS.console = {
  // The stream fills itself from live events (core.js); refreshing the page would rebuild it and pull you back to the end.
  live: false,
  async render() {
    setCrumbs([['Console', '/console']]);
    if (!S.consoleBuffer) S.consoleBuffer = [];
    return pageHead('Console', { subtitle: 'Live stream of agent AI reasoning, tool calls, and progress reports. Scroll up to read; it follows again at the bottom.' }) +
      card('', `<div id="console-stream" class="console-stream"></div>`, { flush: true, cls: 'section' });
  },
  after() {
    const stream = $('#console-stream');
    if (!stream) return;
    // The buffer is newest first; the stream reads top to bottom like a terminal, newest last.
    stream.innerHTML = (S.consoleBuffer || []).slice(0, 300).reverse().map(e => renderConsoleLine(e)).join('');
    stream.scrollTop = stream.scrollHeight;
  }
};
function renderConsoleLine(e, isNew) {
  const ts = new Date(e.ts).toLocaleTimeString();
  const agent = e.agentId || 'host';
  let icon = '○', cls = '', detail = '';
  if (e.type === 'AiCallStarted' || e.type === 'AiCallCompleted') {
    icon = '◆'; cls = 'ai'; detail = e.summary;
  } else if (e.type === 'ToolStarted' || e.type === 'ToolCompleted') {
    icon = '▸'; cls = 'tool'; detail = e.summary;
  } else if (e.type === 'TaskProgress') {
    icon = '•'; cls = 'progress'; detail = e.summary;
  } else if (e.type === 'TaskStarted') {
    icon = '▶︎'; cls = 'task'; detail = e.summary;
  } else if (e.type === 'TaskCompleted') {
    icon = '✓'; cls = 'ok'; detail = e.summary;
  } else if (e.type === 'TaskFailed') {
    icon = '✗'; cls = 'bad'; detail = e.summary;
  } else {
    return '';
  }
  return `<div class="console-line ${cls}${isNew ? ' new' : ''}"><span class="console-ts">${ts}</span><span class="console-icon">${icon}</span><span class="console-agent">${esc(agent)}</span><span class="console-text">${esc(detail)}</span></div>`;
}

// ================================================================== communications & workflows
VIEWS.communications = {
  live: true,
  async render(p) {
    setCrumbs([['Communications', '/communications']]);
    const q = p.query;
    const params = new URLSearchParams();
    ['agent', 'type', 'status', 'from', 'to', 'correlationId'].forEach(k => { if (q.get(k)) params.set(k, q.get(k)); });
    params.set('limit', '200');
    const [list, topo] = await Promise.all([get('/api/communications?' + params), get('/api/communications/topology?hours=' + (q.get('hours') || 24))]);
    S.lastTopo = topo;
    const sel = (name, values) => `<label>${name[0].toUpperCase() + name.slice(1)}<select name="${name}">${['', ...values].map(v => `<option${q.get(name) === v ? ' selected' : ''} value="${v}">${v || 'All'}</option>`).join('')}</select></label>`;
    return pageHead('Communications', { subtitle: 'Agent-to-agent messages, requests and delegations.' }) +
      // data-morph="keep": live refreshes update the drawing in place (js/topology.js), so its motion keeps running.
      card('Topology', '<div id="topo" class="topo" data-morph="keep"></div>', { hint: `moving dots = messages in the last ${esc(topo.hours)}h (number = count) · faint dashes = may message, unused · red = failures · hover an agent to see its links` }) +
      card('Messages', `<form class="filters" data-form="comm-filter">
        <label>Agent<input name="agent" value="${esc(q.get('agent') || '')}"></label>
        ${sel('type', ['Information', 'Question', 'Request', 'TaskDelegation', 'TaskResult', 'Notification', 'ApprovalRequest', 'StatusRequest', 'Response', 'Error'])}
        ${sel('status', ['Pending', 'Delivered', 'Processing', 'Completed', 'Failed', 'Rejected', 'Expired', 'Cancelled'])}
        <label>From<input name="from" type="datetime-local" value="${esc(q.get('from') || '')}"></label><label>To<input name="to" type="datetime-local" value="${esc(q.get('to') || '')}"></label>
        <label>Workflow id<input name="correlationId" value="${esc(q.get('correlationId') || '')}"></label><button class="btn">Apply</button></form><div class="section"></div><div id="comm-rows">${messageTable(list)}</div>`, { cls: 'section' });
  },
  after(p, root) {
    drawTopology($('#topo', root), S.lastTopo, true);
    // A short "new message" ping on the newest (top) row when the newest message changes.
    const top = $('#comm-rows tbody tr', root);
    const newest = top && top.dataset.cid;
    if (newest && S.commTop !== undefined && newest !== S.commTop && !reducedMotion()) { top.classList.remove('just-spoke'); void top.offsetWidth; top.classList.add('just-spoke'); }
    S.commTop = newest;
  }
};
FORMS['comm-filter'] = (f, data) => { const p = new URLSearchParams(); Object.entries(data).forEach(([k, v]) => { if (v) p.set(k, k === 'from' || k === 'to' ? new Date(v).toISOString() : v); }); navigate('/communications' + (p.toString() ? '?' + p : '')); };
function messageTable(list) {
  return table([
    { h: 'When', f: m => `<span data-tip="${esc(ts(m.createdUtc))}">${esc(ago(m.createdUtc))}</span>` },
    { h: 'From → To', f: m => `${m.sender === 'admin' ? '<b>admin</b>' : agentLink(m.sender)} → ${agentLink(m.recipient)}` },
    { h: 'Type', f: m => tag(m.type, 'accent') }, { h: 'Subject', cls: 'clip', f: m => esc(m.subject || '') + (m.rejectionReason ? `<div class="error-text small">${esc(m.rejectionReason)}</div>` : '') },
    { h: 'Status', f: m => badge(m.status) }, { h: 'Duration', num: true, f: m => dur(m.durationMs) },
    { h: 'Workflow', f: m => link('/correlations/' + enc(m.correlationId), m.correlationId.slice(-8)) },
    { h: '', cls: 'act', f: m => `<button class="btn xs" data-act="show-message" data-msg="${esc(JSON.stringify(m))}">Details</button>` + (['Pending', 'Delivered'].includes(m.status) ? ` <button class="btn xs danger" data-act="cancel-message" data-msg-id="${esc(m.id)}">Cancel</button>` : '') }
  ], list, { empty: 'No messages.', rowAttrs: m => `data-cid="${esc(m.id)}"`, rowClass: m => 'comm-row' + (['Pending', 'Processing', 'Delivered'].includes(m.status) ? ' live' : m.status === 'Failed' ? ' failed' : '') });
}
ACTIONS['show-message'] = el => openModal('Message', json(JSON.parse(el.dataset.msg)), '', 'wide');
ACTIONS['cancel-message'] = async el => { await post(`/api/messages/${enc(el.dataset.msgId)}/cancel`); toast('Message cancelled', 'ok'); refreshNow(); };
// drawTopology: js/topology.js

VIEWS.correlation = {
  live: true,
  async render(p) {
    const id = p.args[0];
    setCrumbs([['Communications', '/communications'], ['Workflow', '']]);
    const c = await get('/api/correlations/' + enc(id));
    const byParent = {}; const ids = new Set(c.tasks.map(t => t.id));
    c.tasks.forEach(t => { const k = t.parentTaskId && ids.has(t.parentTaskId) ? t.parentTaskId : '_root'; (byParent[k] = byParent[k] || []).push(t); });
    const node = t => `<li><span class="tree-node">${t.agentId.includes('.sub-') ? link('/agents/' + enc(t.agentId.split('.sub-')[0]) + '/subagents', t.agentId) + ' ' + tag('subagent', 'info') : agentLink(t.agentId)} <a href="#" data-act="open-task" data-id="${esc(t.agentId)}" data-task="${esc(t.id)}">${esc(t.name)}</a>
      ${badge(taskStatus(t))} <span class="muted small">${esc(t.kind)} · depth ${t.delegationDepth}${t.costMicros ? ' · ' + money(t.costMicros) : ''}</span></span>
      ${(byParent[t.id] || []).length ? `<ul>${byParent[t.id].map(node).join('')}</ul>` : ''}</li>`;
    // Timeline: tasks, tool calls, model calls, subagents and messages in time order.
    const events = c.timeline.slice();
    c.tasks.forEach(t => {
      events.push({ kind: 'task', ts: t.createdUtc, agentId: t.agentId, taskId: t.id, title: `Task created: ${t.name}`, detail: t.createdBy ? 'by ' + t.createdBy : '' });
      if (t.startedUtc) events.push({ kind: 'task', ts: t.startedUtc, agentId: t.agentId, taskId: t.id, title: `Task started: ${t.name}` });
      if (t.completedUtc) events.push({ kind: 'task', ts: t.completedUtc, agentId: t.agentId, taskId: t.id, title: `Task ${taskStatus(t).toLowerCase()}: ${t.name}`, success: t.status === 'Completed', output: (t.result && t.result.summary) || t.error || '' });
    });
    c.messages.forEach(m => events.push({ kind: 'message', ts: m.createdUtc, agentId: m.senderAgentId, title: `${m.messageType} → ${m.recipientAgentId}`, output: m.subject || '', status: m.status }));
    events.sort((a, b) => String(a.ts).localeCompare(String(b.ts)));
    const filter = S.tlFilter || 'all';
    const shown = events.filter(e => filter === 'all' || e.kind === filter);
    const counts = k => events.filter(e => e.kind === k).length;
    const agents = [...new Set(c.tasks.map(t => t.agentId.split('.sub-')[0]))];
    const span = events.length > 1 ? new Date(events[events.length - 1].ts) - new Date(events[0].ts) : 0;
    const kindLabel = { task: 'Task', tool: 'Tool', ai: 'AI call', subagent: 'Subagent', message: 'Message' };
    const kindIconName = { task: 'check', tool: 'bolt', ai: 'layers', subagent: 'agent', message: 'arrow' };
    const item = e => {
      const ok = e.success === false ? ' bad' : '';
      const meta = [e.durationMs ? dur(e.durationMs) : '', e.costMicros ? money(e.costMicros) : '', e.tokens ? num(e.tokens) + ' tokens' : '', e.reason && e.reason !== 'Primary' ? e.reason : '', e.error || ''].filter(Boolean).join(' · ');
      const details = (e.input || e.output) ? `<details><summary>${e.input ? 'Input and result' : 'Result'}</summary>${e.input ? `<pre class="json">${jsonOr(e.input)}</pre>` : ''}${e.output ? `<pre class="json">${jsonOr(e.output)}</pre>` : ''}</details>` : '';
      return `<li class="tl-item tl-${esc(e.kind)}${ok}"><span class="tl-time" data-tip="${esc(ts(e.ts))}">${esc(time(e.ts))}</span><span class="tl-dot">${icon(kindIconName[e.kind] || 'arrow')}</span>
        <div class="tl-body"><div class="tl-head">${tag(kindLabel[e.kind] || e.kind, e.kind === 'ai' ? 'accent' : e.kind === 'tool' ? 'info' : 'outline')} <b>${esc(e.title || '')}</b>
        ${e.agentId ? '<span class="muted small">' + esc(e.agentId) + '</span>' : ''}${e.success === false ? ' ' + badge('failed', 'bad') : ''}${meta ? ` <span class="muted small">${esc(meta)}</span>` : ''}</div>${e.detail ? `<div class="small muted">${esc(e.detail)}</div>` : ''}${details}</div></li>`;
    };
    const chip = (k, label, n) => `<button class="btn xs${filter === k ? ' primary' : ''}" data-act="tl-filter" data-kind="${k}">${label}${n !== undefined ? ` <span class="muted">${n}</span>` : ''}</button>`;
    const noMessages = agents.length <= 1
      ? emptyState('No messages between agents', `${agents[0] || 'The agent'} worked on this on its own and did not need to ask or delegate to another agent. Every step it took is in the timeline below.`)
      : emptyState('No messages between agents', 'The agents in this workflow did not message each other; see the timeline below for what each one did.');
    return pageHead('Workflow', { badges: `<code class="muted">${esc(id)}</code>`, subtitle: `${plural(agents.length, 'agent')} · ${plural(c.tasks.length, 'task')} · total AI cost ${money(c.totalCostMicros)}` }) +
      (c.globalTask ? `<a class="notice info" href="/global-tasks/${enc(c.globalTask.id)}" data-nav>${icon('layers')} Part of the global task <b>${esc(c.globalTask.name)}</b> (${esc(c.globalTask.status)}) — open it for the plan and the final answer.</a><div class="section"></div>` : '') +
      `<div class="grid kpis">${kpi('Agents', num(agents.length), agents.join(', '))}${kpi('Tool calls', num(counts('tool')), `${num(events.filter(e => e.kind === 'tool' && e.success === false).length)} failed`)}
        ${kpi('AI calls', num(counts('ai')), money(c.totalCostMicros) + ' total')}${kpi('Messages', num(counts('message')), 'between agents')}${kpi('Duration', span ? dur(span) : '—', 'first to last event')}</div>` +
      card('Delegation tree', `<ul class="tree">${(byParent._root || []).map(node).join('') || '<li class="muted">No tasks.</li>'}</ul>`, { cls: 'section' }) +
      card('Messages', c.messages.length ? messageTable(c.messages) : noMessages, { flush: true }) +
      card('Timeline', shown.length ? `<ol class="timeline">${shown.map(item).join('')}</ol>` : emptyState('Nothing recorded yet'), {
        cls: 'section', actions: chip('all', 'All', events.length) + chip('task', 'Tasks', counts('task')) + chip('tool', 'Tools', counts('tool')) + chip('ai', 'AI calls', counts('ai')) +
          chip('message', 'Messages', counts('message')) + (counts('subagent') ? chip('subagent', 'Subagents', counts('subagent')) : '')
      });
  }
};
ACTIONS['tl-filter'] = el => { S.tlFilter = el.dataset.kind; rerender(); };

// ================================================================== inbox
/** The tool a tool request is for ("Approve {tool}?"). */
function approvalTool(a) { const m = /^Approve (.+)\?$/.exec(a.title || ''); return m ? m[1] : null; }
/** The command a tool request would run (its "command" argument), or null. */
function approvalCommand(a) {
  if (a.kind !== 'ToolApproval' || !a.context) return null;
  try { const j = JSON.parse(a.context); return typeof j.command === 'string' ? j.command : null; } catch { return null; }
}
const RISK_TONE = { Low: 'ok', Medium: 'warn', High: 'bad' };
const RISK_LABEL = { Low: 'read-only', Medium: 'changes something', High: 'risky' };
function endOfToday() { const d = new Date(); d.setHours(23, 59, 59, 0); return d.toISOString(); }
/** "Approve & stop asking": approve this request and add a standing rule (smart read-only, or everything for a while). */
function approveRuleMenu(a) {
  const tool = approvalTool(a);
  if (!tool) return '';
  const short = tool.replace(/^mcp__/, '').replace(/__/g, ' › ');
  const opt = (label, scope, mode, dur, extra = '') => `<button type="button" class="apr-opt" data-act="approve-rule" data-id="${esc(a.id)}" data-scope="${scope}" data-mode="${mode}" data-dur="${dur}">${label}${extra}</button>`;
  return `<details class="apr-more"><summary class="btn">Approve &amp; stop asking <span class="apr-caret">▾</span></summary><div class="apr-menu">
    ${approvalCommand(a) !== null ? `<div class="apr-group">Smart: approve read-only commands by themselves (others still ask)</div>
      ${opt(`Read-only commands from <b>${esc(a.agentId)}</b>, always`, 'agent', 'safe', 'always', ' <span class="badge ok">recommended</span>')}
      ${opt('Read-only commands from <b>every agent</b>, always', 'all', 'safe', 'always')}` : ''}
    <div class="apr-group">Every ${esc(short)} call from ${esc(a.agentId)}</div>
    <div class="apr-row">${opt('1 hour', 'agent', 'all', '1')}${opt('4 hours', 'agent', 'all', '4')}${opt('8 hours', 'agent', 'all', '8')}${opt('Rest of today', 'agent', 'all', 'today')}${opt('Always', 'agent', 'all', 'always')}</div>
    <div class="apr-group">Every ${esc(short)} call from every agent</div>
    <div class="apr-row">${opt('1 hour', 'all', 'all', '1')}${opt('4 hours', 'all', 'all', '4')}${opt('Rest of today', 'all', 'all', 'today')}${opt('Always', 'all', 'all', 'always')}</div>
    <div class="apr-foot muted small">This request is approved too, and so are waiting ones the rule covers. Revoke rules any time below.</div>
  </div></details>`;
}
function approvalRulesCard(rules) {
  const active = rules.filter(r => r.active);
  const until = r => r.expiresUtc ? `<span data-tip="${esc(ts(r.expiresUtc))}">${esc(ts(r.expiresUtc))}</span>` : '<span class="muted">until revoked</span>';
  return card('Auto-approval rules', active.length ? table([
    { h: 'Agent', f: r => r.agentId === '*' ? tag('every agent', 'info') : agentLink(r.agentId) },
    { h: 'Tool', cls: 'clip', f: r => `<code>${esc(r.tool)}</code>` },
    { h: 'Allows', f: r => r.mode === 'safe' ? tag('read-only commands', 'ok') : tag('every call', 'warn') },
    { h: 'Until', f: until },
    { h: 'Used', f: r => `${num(r.uses)}${r.lastUsedUtc ? ` <span class="muted small">· ${esc(ago(r.lastUsedUtc))}</span>` : ''}` },
    { h: 'Set by', f: r => esc(r.createdBy) },
    { h: '', cls: 'act', f: r => `<button class="btn xs danger" data-act="revoke-approval-rule" data-id="${esc(r.id)}">Revoke</button>` }], active, { empty: '' })
    : '<p class="muted small">No rules. Use “Approve &amp; stop asking” on a request, or add one: matching tool calls are then approved the moment an agent asks.</p>',
    { flush: !!active.length, cls: 'section', actions: `<button class="btn xs" data-act="add-approval-rule">Add rule</button>` });
}
/** A user's /feedback in the Inbox: what they wrote, the app, and the report file (their words, the chat and the app's logs). */
function feedbackItem(a) {
  let c = {}; try { c = JSON.parse(a.context || '{}'); } catch { /* not JSON */ }
  return `<div class="inbox-item needs" data-inbox="${esc(a.id)}">` + card(a.title, `<div class="row muted small">${tag('Feedback', 'accent')} ${esc(c.from || (a.agentId || '').replace(/^user:/, ''))} · ${esc(ago(a.createdUtc))}${c.app ? ' · ' + esc(c.app + (c.version ? ' ' + c.version : '')) : ''}${c.platform ? ' · ' + esc(c.platform) : ''}</div>
    <p class="section fb-text">${esc(a.question)}</p>
    ${c.fileId ? `<div class="section"><a class="btn xs" href="/api/chat/files/${enc(c.fileId)}" download="${esc(c.fileName || 'feedback.md')}">${icon('download')} Report${c.chatTitle ? ' with the chat “' + esc(c.chatTitle) + '”' : ''} and logs</a></div>` : ''}
    <label class="section">Reply (they see it with /feedback)<textarea id="ans-${esc(a.id)}"></textarea></label>
    <div class="row section apr-actions"><span class="spacer"></span><button class="btn" data-act="resolve-approval" data-id="${esc(a.id)}" data-status="Rejected">Dismiss</button><button class="btn primary" data-act="resolve-approval" data-id="${esc(a.id)}" data-status="Answered">Reply</button></div>`) + '</div>';
}
VIEWS.approvals = {
  live: true,
  async render() {
    setCrumbs([['Inbox', '/approvals']]);
    const [pending, recent, rules] = await Promise.all([get('/api/approvals?status=Pending&limit=100'), get('/api/approvals?limit=50'), get('/api/approval-rules').catch(() => [])]);
    S.inboxSeen = { agents: [...new Set(recent.concat(pending).map(a => a.agentId).filter(Boolean))], tools: [...new Set(recent.concat(pending).map(approvalTool).filter(Boolean))] };
    // Requests a supervisor agent is deciding are listed apart: people can still decide them, but they need not.
    const staged = pending.filter(a => a.stage), forYou = pending.filter(a => !a.stage);
    const approveKinds = ['ToolApproval', 'BudgetRequest', 'ProfileChange', 'HireAgent', 'EscalationApproval'];
    const item = a => {
      if (a.kind === 'Feedback') return feedbackItem(a);
      const cmd = approvalCommand(a);
      const risk = a.risk ? ` · ${tag(RISK_LABEL[a.risk] || a.risk, RISK_TONE[a.risk] || '')}${a.riskReason && a.risk !== 'Low' ? ` <span class="small">${esc(a.riskReason)}</span>` : ''}` : '';
      return `<div class="inbox-item${a.stage ? '' : ' needs'}" data-inbox="${esc(a.id)}">` + card(a.title, `<div class="row muted small">${tag(a.kind, 'accent')} ${agentLink(a.agentId)} · ${esc(ago(a.createdUtc))}${a.expiresUtc ? ' · expires ' + esc(ts(a.expiresUtc)) : ''}
        ${a.stage ? ' · ' + tag(a.stage.replace(/^supervisor:/, '') + ' is deciding', 'info') : ''}${risk}</div>
      ${a.reason ? `<div class="muted small section">${esc(a.reason)}</div>` : ''}
      ${cmd !== null ? `<pre class="apr-cmd section">${esc(cmd)}</pre>` : `<p class="section">${esc(a.question)}</p>`}${a.context ? `<details><summary class="muted">${cmd !== null ? 'All arguments' : 'Context'}</summary><pre class="json">${jsonOr(a.context)}</pre></details>` : ''}
      <label class="section">Your answer / note<textarea id="ans-${esc(a.id)}"></textarea></label>
      <div class="row section apr-actions"><span class="spacer"></span>${approveKinds.includes(a.kind)
        ? `<button class="btn danger" data-act="resolve-approval" data-id="${esc(a.id)}" data-status="Rejected">Reject</button>${a.kind === 'ToolApproval' ? approveRuleMenu(a) : ''}<button class="btn primary" data-act="resolve-approval" data-id="${esc(a.id)}" data-status="Approved">Approve</button>`
        : `<button class="btn danger" data-act="resolve-approval" data-id="${esc(a.id)}" data-status="Rejected">Decline</button><button class="btn primary" data-act="resolve-approval" data-id="${esc(a.id)}" data-status="Answered">Send answer</button>`}</div>`) + '</div>';
    };
    const decided = a => {
      const by = a.decidedBy || 'human';
      const who = by === 'human' ? esc(a.resolvedBy || 'a person') : by === 'policy' ? tag('policy', 'ok') : by === 'timeout' ? tag('time-out', 'warn') : by.startsWith('agent:') ? agentLink(by.slice(6)) : esc(by);
      return a.reason ? `<span data-tip="${esc(a.reason)}">${who}</span>` : who;
    };
    return pageHead('Inbox', { subtitle: 'Questions and approval requests from agents. Their tasks wait until someone decides: you, the agent\'s policy, or its supervisor agent.' }) +
      (forYou.map(item).join('') || card('', emptyState('Nothing waiting for you', 'Agents ask here when they need a decision or approval.'))) +
      (staged.length ? `<h2 class="section">Being decided by a supervisor agent</h2>` + staged.map(item).join('') : '') +
      approvalRulesCard(rules) +
      card('Recently decided', table([{ h: 'When', f: a => esc(ago(a.resolvedUtc || a.createdUtc)) }, { h: 'Agent', f: a => agentLink(a.agentId) }, { h: 'Kind', f: a => esc(a.kind) },
        { h: 'Question', cls: 'clip', f: a => esc(a.title) }, { h: 'Status', f: a => badge(a.status) }, { h: 'Response', cls: 'clip', f: a => esc(a.response || '') }, { h: 'Decided by', f: decided }],
        recent.filter(a => a.status !== 'Pending'), { empty: 'Nothing decided yet.', rowClass: () => 'resolved-row' }), { flush: true, cls: 'section' });
  },
  after(p, root) {
    // A new request arrived since the last refresh: ping the newest pending item.
    const items = $$('.inbox-item', root);
    const ids = items.map(i => i.dataset.inbox);
    if (S.inboxIds && !reducedMotion()) {
      const fresh = items.find(i => !S.inboxIds.includes(i.dataset.inbox));
      if (fresh) { fresh.classList.remove('just-arrived'); void fresh.offsetWidth; fresh.classList.add('just-arrived'); playSound && playSound('inputRequired'); }
    }
    S.inboxIds = ids;
  }
};
ACTIONS['resolve-approval'] = async el => {
  const text = ($('#ans-' + CSS.escape(el.dataset.id)) || {}).value || '';
  if (el.dataset.status === 'Answered' && !text.trim()) { toast('Please write an answer.', 'bad'); return; }
  await post(`/api/approvals/${enc(el.dataset.id)}/resolve`, { status: el.dataset.status, response: text });
  toast('Sent. The agent resumes its task.', 'ok'); refreshBadges(); refreshNow();
};
ACTIONS['approve-rule'] = async el => {
  const d = el.dataset;
  const rule = { scope: d.scope, mode: d.mode };
  if (d.dur === 'today') rule.expiresUtc = endOfToday(); else if (d.dur !== 'always') rule.hours = Number(d.dur);
  const text = ($('#ans-' + CSS.escape(d.id)) || {}).value || '';
  const r = await post(`/api/approvals/${enc(d.id)}/resolve`, { status: 'Approved', response: text, rule });
  toast(`Approved. From now on: ${r.ruleDescription || 'covered calls are approved by themselves'}.${r.alsoApproved ? ` ${plural(r.alsoApproved, 'more waiting request')} approved too.` : ''}`, 'ok');
  refreshBadges(); refreshNow();
};
ACTIONS['revoke-approval-rule'] = async el => {
  await del(`/api/approval-rules/${enc(el.dataset.id)}`);
  toast('Rule revoked: those calls ask again.', 'ok'); refreshNow();
};
ACTIONS['add-approval-rule'] = () => {
  const seen = S.inboxSeen || { agents: [], tools: [] };
  openModal('Add an auto-approval rule', `<form id="apr-rule-form" class="stack" data-form="approval-rule">
      <label>Agent<input name="agentId" list="apr-agents" value="*" required><span class="hint">An agent id, or * for every agent.</span></label>
      <datalist id="apr-agents"><option value="*">${seen.agents.map(x => `<option value="${esc(x)}">`).join('')}</datalist>
      <label>Tool<input name="tool" list="apr-tools" placeholder="mcp__shell__run_command" required><span class="hint">A tool name, a prefix ending in * (mcp__shell__*), or * for every tool that needs approval.</span></label>
      <datalist id="apr-tools">${seen.tools.map(x => `<option value="${esc(x)}">`).join('')}<option value="mcp__shell__*"><option value="*"></datalist>
      <label>Allows<select name="mode"><option value="safe">Read-only commands only (others still ask)</option><option value="all">Every call</option></select></label>
      <label>For<select name="dur"><option value="1">1 hour</option><option value="4">4 hours</option><option value="8">8 hours</option><option value="today">Rest of today</option><option value="always" selected>Until revoked</option></select></label>
    </form>`, `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="submit-form" data-form-id="apr-rule-form">Add rule</button>`, 'narrow');
};
FORMS['approval-rule'] = async (f, d) => {
  const body = { agentId: d.agentId.trim() || '*', tool: d.tool.trim(), mode: d.mode };
  if (d.dur === 'today') body.expiresUtc = endOfToday(); else if (d.dur !== 'always') body.hours = Number(d.dur);
  const r = await post('/api/approval-rules', body);
  toast(`Rule added.${r.alsoApproved ? ` ${plural(r.alsoApproved, 'waiting request')} approved.` : ''}`, 'ok');
  closeModal(); refreshBadges(); refreshNow();
};

// ================================================================== finance
// How users spend their balance on models through the Chat tab (messages, images, videos), priced at Model Square prices.
// The amounts are the real account charges, so the graphs are true balance usage, not an estimate.
function chatUsageSection(u) {
  if (!u || !u.byModel || !u.byModel.length) {
    return `<div id="fin-usage">${card('Chat tab spend by model', emptyState('No chat spend yet',
      'When users send messages, create images, make videos or use speech, their balance usage shows here — priced at the Model Square and speech prices.'),
      { cls: 'section', actions: usageDaysControl(u ? u.days : 30) })}</div>`;
  }
  const dollars = v => (Number(v) || 0) / 1e6;
  const money2 = v => money(v * 1e6, 2);
  const top = u.byModel.slice(0, 4).map(m => m.model);
  // Per-day rows with the top models kept apart and the rest folded into "Other", for a stacked chart.
  const days = [...new Set(u.daily.map(r => r.day))].sort();
  const byDay = Object.fromEntries(days.map(d => [d, { day: d, other: 0 }]));
  u.daily.forEach(r => { const row = byDay[r.day]; const k = top.includes(r.model) ? r.model : 'other'; row[k] = (row[k] || 0) + Number(r.micros); });
  const series = top.map((m, i) => ({ label: m, y: r => dollars(r[m]), cls: SERIES[i] }));
  if (u.daily.some(r => !top.includes(r.model))) series.push({ label: 'Other', y: r => dollars(r.other), cls: 'muted-seg' });
  const daily = chartBox('fin-daily', {
    type: 'stack', rows: days.map(d => byDay[d]), series, x: r => r.day.slice(5), axis: moneyAxis, minMax: 0.05,
    label: `Chat spend per day, last ${u.days} days`,
    tip: r => `${r.day}\n` + series.filter(s => s.y(r) > 0).map(s => `${s.label}: ${money(s.y(r) * 1e6, 3)}`).join('\n')
  });
  const modelBars = barList(u.byModel.map(m => ({
    label: m.model, value: dollars(m.micros), cls: 's1', sub: plural(m.calls, 'charge'),
    extra: (m.promptTokens || m.completionTokens) ? num((m.promptTokens || 0) + (m.completionTokens || 0)) + ' tok' : '',
    tip: `${m.model}\n${money(m.micros, 3)} · ${plural(m.calls, 'charge')}` + ((m.promptTokens || m.completionTokens) ? `\n${num(m.promptTokens || 0)} in / ${num(m.completionTokens || 0)} out tokens` : '')
  })), { fmt: money2, limit: 12 });
  const userBars = barList(u.byUser.map(x => ({
    label: x.username, value: dollars(x.micros), cls: 's2', sub: plural(x.calls, 'charge'),
    tip: `${x.username}\n${money(x.micros, 3)} on chat · ${plural(x.calls, 'charge')}`
  })), { fmt: money2, limit: 12, empty: 'No spend yet' });
  return `<div id="fin-usage"><div class="grid two section">
      ${card('Chat spend over time', `<div class="chart-meta"><span class="value">${money(u.totalMicros, 2)}</span><span class="muted small">${plural(u.charges, 'charge')} · last ${u.days} days</span></div>${legend(series)}${daily}`,
        { actions: usageDaysControl(u.days) })}
      ${card('Spend by model', `<div class="chart-meta"><span class="value">${num(u.byModel.length)}</span><span class="muted small">${u.byModel.length === 1 ? 'model' : 'models'} used · Model Square prices</span></div>${modelBars}`,
        { hint: 'Each charge is priced at the Model Square price in force when it happened.' })}
    </div>
    ${card('Top accounts by chat spend', userBars, { cls: 'section' })}</div>`;
}
function usageDaysControl(days) {
  return `<div class="row chip-seg">${[7, 30, 90].map(d => `<button class="btn xs${d === days ? ' primary' : ' ghost'}" data-act="fin-usage-days" data-days="${d}">${d}d</button>`).join('')}</div>`;
}
ACTIONS['fin-usage-days'] = async el => {
  const host = document.getElementById('fin-usage');
  if (!host) return;
  host.classList.add('soft-loading');
  try {
    const u = await get('/api/finance/chat-usage?days=' + Number(el.dataset.days));
    const wrap = document.createElement('div');
    wrap.innerHTML = chatUsageSection(u);
    host.replaceWith(wrap.firstElementChild);
    applyMeters($('#fin-usage'), true);
    drawCharts($('#fin-usage'), true);
  } catch (e) { host.classList.remove('soft-loading'); toast(e.message, 'bad'); }
};

VIEWS.finance = {
  live: true,
  async render() {
    setCrumbs([['Finance', '/finance']]);
    const [f, ledger, pricing, usage, speech, pictures] = await Promise.all([get('/api/finance'), get('/api/finance/ledger?limit=100'), get('/api/pricing'), get('/api/finance/chat-usage?days=30'),
      get('/api/audio/pricing').catch(() => null), get('/api/images/pricing').catch(() => null)]);
    const songs = await get('/api/music/pricing').catch(() => null);
    const t = f.totals;
    const maxSpend = Math.max(1, ...f.spendByModel7d.map(r => r.spendMicros));
    return pageHead('Finance', { subtitle: `AI spend versus profit credited, per agent (${esc(f.currency)}). Subagents spend from their parent's wallet.` }) +
      `<div class="grid kpis">${kpi('Total balance', money(f.account.balanceMicros, 2),
          `${f.account.charged ? 'your account' : 'administrator · never charged'} · agent wallets ${money(t.balanceMicros, 2)}`, { href: '/balance' })}${kpi('Spent today', money(t.spentTodayMicros, 2), f.globalDailyLimit ? `global cap ${usd(f.globalDailyLimit)}` : 'no global cap')}
        ${kpi('Lifetime AI spend', money(t.lifetimeSpendMicros, 2))}${kpi('Profit credited', money(t.lifetimeProfitMicros, 2))}${kpi('Net', money(t.lifetimeProfitMicros - t.lifetimeSpendMicros, 2))}
        ${kpi('Subagent spend (30d)', money(f.subagentSpend30d.reduce((s, r) => s + r.spendMicros, 0), 2), 'charged to parents')}</div>` +
      chatUsageSection(usage) +
      card('Agent wallets', table([
        { h: 'Agent', f: w => link('/agents/' + enc(w.agentId) + '/budget', w.agentId) }, { h: 'Available', num: true, f: w => money(w.availableMicros, 2) },
        { h: 'Spent today', num: true, f: w => money(w.spentTodayMicros, 2) + (w.dailyLimitMicros ? ` <span class="muted">/ ${money(w.dailyLimitMicros, 2)}</span>` : '') },
        { h: 'Lifetime spend', num: true, f: w => money(w.lifetimeSpendMicros, 2) }, { h: 'Profit', num: true, f: w => money(w.lifetimeProfitMicros, 2) },
        { h: 'Net', num: true, f: w => money(w.netMicros, 2) }, { h: 'ROI', num: true, f: w => w.roi === null ? '—' : w.roi.toFixed(2) + '×' },
        { h: 'Runway', num: true, f: w => runway(w.runwayDays, w.lowRunway) + (w.burnRateMicrosPerDay ? `<div class="cell-sub">${money(w.burnRateMicrosPerDay, 2)}/day</div>` : '') },
        { h: 'Subagents (30d)', num: true, f: w => { const s = f.subagentSpend30d.find(x => x.agentId === w.agentId); return s ? money(s.spendMicros, 2) : '<span class="muted">—</span>'; } },
        { h: '', cls: 'act', f: w => `<button class="btn xs primary" data-act="record-profit" data-id="${esc(w.agentId)}">Record profit</button> <button class="btn xs" data-act="adjust-balance" data-id="${esc(w.agentId)}">Adjust</button>` }
      ], f.wallets), { flush: true, cls: 'section', export: 'wallets', hint: `Runway: how long the balance lasts at the average spend of the last ${esc(f.runwayWindowDays)} days; under ${esc(f.lowRunwayDays)} days is flagged.` }) +
      `<div class="grid two section">${card('Spend by model (7 days)', table([
        { h: 'Model', f: r => `<code>${esc(r.modelKey)}</code>` }, { h: 'Charges', num: true, f: r => num(r.charges) },
        { h: 'Spend', num: true, f: r => money(r.spendMicros) }, { h: '', f: r => meter(r.spendMicros / maxSpend, money(r.spendMicros)) }], f.spendByModel7d, { empty: 'No cloud spend yet.' }), { flush: true })}
      ${card('Model pricing (USD per 1M tokens)', table([
        { h: 'Model', f: r => `<code>${esc(r.modelKey)}</code>` + (r.isLocal ? ' ' + badge('local', 'ok', false) : '') },
        { h: 'Input / 1M', f: r => r.isLocal ? 'free' : `<input class="price" data-key="${esc(r.modelKey)}" data-field="inputPerMillion" value="${r.inputPerMillion ?? ''}" size="7" aria-label="Input price">` },
        { h: 'Output / 1M', f: r => r.isLocal ? 'free' : `<input class="price" data-key="${esc(r.modelKey)}" data-field="outputPerMillion" value="${r.outputPerMillion ?? ''}" size="7" aria-label="Output price">` },
        { h: 'Per image', f: r => r.isLocal ? '' : `<input class="price" data-key="${esc(r.modelKey)}" data-field="perImage" value="${r.perImage ?? ''}" size="6" aria-label="Per image price">` },
        { h: 'Source', f: r => `<span class="muted small">${esc(r.source)}</span>` },
        { h: '', f: r => r.isLocal ? '' : `<button class="btn xs" data-act="save-price" data-key="${esc(r.modelKey)}">Save</button>` }], pricing), { flush: true, hint: 'Unpriced cloud models are never used.' })}</div>` +
      (speech && typeof audPricingCardHtml === 'function' ? audPricingCardHtml(speech, can('finance.manage')) : '') +
      (pictures && typeof imgPricingCardHtml === 'function' ? imgPricingCardHtml(pictures, can('finance.manage')) : '') +
      (songs && typeof musPricingCardHtml === 'function' ? musPricingCardHtml(songs, can('finance.manage')) : '') +
      card('Recent ledger entries', ledgerTable(ledger, true), { flush: true, cls: 'section', export: 'ledger' });
  }
};
ACTIONS['save-price'] = async el => {
  const key = el.dataset.key; const body = {};
  $$(`input.price[data-key="${CSS.escape(key)}"]`).forEach(i => { body[i.dataset.field] = i.value === '' ? null : Number(i.value); });
  await put('/api/pricing/' + enc(key), body); toast('Pricing saved for ' + key, 'ok'); refreshNow();
};
ACTIONS['record-profit'] = async el => {
  let tasks = [];
  try { tasks = await get(`/api/agents/${enc(el.dataset.id)}/tasks?status=Completed&limit=50`); } catch { /* optional */ }
  openModal('Record profit for ' + el.dataset.id, `<p class="muted">One-time credit for profit this agent generated. It is added to the agent's balance and counted in its ROI. Submitting twice credits only once.</p>
    <form id="profit-form" class="form-grid" data-form="record-profit" data-id="${esc(el.dataset.id)}" data-key="${newKey()}">
    <label>Amount (USD)<input name="amount" type="number" step="0.01" min="0.01" required></label><label class="span2">Reference (invoice, deal…)<input name="reference" maxlength="200"></label>
    <label class="full">Description<input name="description" maxlength="500" value="Profit generated"></label>
    <label class="full">Task that produced it (optional)<select name="taskId"><option value="">Not linked to a task</option>${tasks.map(t => `<option value="${esc(t.id)}">${esc(t.name)} · ${esc(ago(t.completedUtc))}</option>`).join('')}</select>
      <span class="help">Linking the task lets a success study learn exactly what worked.</span></label></form>`,
    `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="submit-form" data-form-id="profit-form">Credit profit</button>`);
};
ACTIONS['adjust-balance'] = el => {
  openModal('Adjust balance of ' + el.dataset.id, `<form id="adj-form" class="form-grid" data-form="adjust-balance" data-id="${esc(el.dataset.id)}" data-key="${newKey()}">
    <label>Amount (USD, negative to deduct)<input name="amount" type="number" step="0.01" required></label><label class="span2">Description<input name="description" maxlength="500" value="Balance adjustment"></label></form>`,
    `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="submit-form" data-form-id="adj-form">Apply</button>`);
};
FORMS['record-profit'] = async (f, data) => {
  const r = await post(`/api/agents/${enc(f.dataset.id)}/profit`, { amount: Number(data.amount), reference: data.reference, description: data.description, taskId: data.taskId || null, idempotencyKey: f.dataset.key });
  toast(r.duplicate ? 'Already credited (duplicate ignored).' : `Credited. New balance ${money(r.balanceMicros, 2)}`, 'ok'); closeModal(); refreshNow();
};
FORMS['adjust-balance'] = async (f, data) => {
  const r = await post(`/api/agents/${enc(f.dataset.id)}/adjust`, { amount: Number(data.amount), description: data.description, idempotencyKey: f.dataset.key });
  toast(`Balance now ${money(r.balanceMicros, 2)}`, 'ok'); closeModal(); refreshNow();
};

// ================================================================== models & access
VIEWS.models = {
  live: true,
  async render() {
    setCrumbs([['Models & access', '/models']]);
    const [m, ai, grants, agents, paused, limits] = await Promise.all([get('/api/models'), get('/api/ai/status'), get('/api/model-grants'), can('agents.view') ? get('/api/agents') : [],
      get('/api/models/paused-providers').catch(() => ({ items: [] })), get('/api/models/context-limits').catch(() => ({ items: [] }))]);
    const prov = ai.provider;
    const cloud = m.models.filter(x => !x.isLocal && x.enabled);
    const priceText = x => x.isLocal ? badge('local · free', 'ok', false) : (x.pricing && x.pricing.inputPerMillion !== null && x.pricing.outputPerMillion !== null ? `$${x.pricing.inputPerMillion} / $${x.pricing.outputPerMillion}` : badge('unpriced', 'warn', false));
    const modelsTable = table([
      { h: 'Model', f: x => `<code>${esc(x.key)}</code> ${x.isCustom ? tag('custom', 'accent') : ''}<div class="cell-sub">${esc(x.modelId)}</div>` },
      { h: 'Tier', num: true, f: x => x.tier },
      { h: 'Circuit', f: x => badge(x.circuit) + (x.consecutiveFailures ? ` <span class="muted small">${x.consecutiveFailures} fails</span>` : '') },
      { h: 'Slots', cls: 'nw', f: x => `${slots(x.slotsInUse, Math.min(x.slotsCapacity, 10))} <span class="muted small">${x.slotsInUse}/${x.slotsCapacity}${x.chatSlots ? ` (${x.chatSlots} in chat)` : ''} · ${x.queueLength} queued</span>` },
      { h: 'Calls', num: true, f: x => `${num(x.calls)}<div class="cell-sub">${num(x.failures)} failed · ${dur(x.avgLatencyMs)}</div>` },
      { h: '24h cost', num: true, f: x => x.usage24h ? money(x.usage24h.costMicros) : '—' },
      { h: 'Price in/out', f: priceText },
      { h: 'Access', f: x => `${x.isCustom ? '' : `<span data-tip="${esc((x.profileAgents || []).join(', ') || 'none')}">${num((x.profileAgents || []).length)} by profile</span>`}${x.grantedTo && x.grantedTo.length ? `${x.isCustom ? '' : ' · '}<span data-tip="${esc(x.grantedTo.join(', '))}">${num(x.grantedTo.length)} granted</span>` : (x.isCustom ? '<span class="muted">not granted</span>' : '')}` },
      { h: 'Capabilities', f: x => chips(x.capabilities) },
      { h: '', cls: 'act', f: x => (x.isLocal ? '' : `<button class="btn xs" data-act="model-access" data-key="${esc(x.key)}">Access</button> `) +
          (x.isCustom ? `${link('/models/custom/' + enc(x.key), 'Edit', 'btn xs')} <button class="btn xs danger" data-act="delete-model" data-key="${esc(x.key)}">Remove</button> ` : '') +
          (x.circuit !== 'Closed' ? `<button class="btn xs" data-act="reset-circuit" data-key="${esc(x.key)}">Reset circuit</button>` : '') }
    ], m.models);
    const matrix = table([
      { h: 'Agent', f: a => agentLink(a.id, a.name) },
      ...cloud.map(x => ({ h: x.key, f: a => (x.profileAgents || []).includes(a.id) ? badge('profile', 'ok', false) : ((x.grantedTo || []).includes(a.id) ? `<span data-tip="${esc((grants.find(g => g.agentId === a.id && g.modelKey === x.key) || {}).useFor || '')}">${badge('granted', 'accent', false)}</span>` : '<span class="muted">—</span>') }))
    ], agents);
    return pageHead('Models & access', {
      subtitle: `${num(m.spillovers)} spillovers · ${num(m.escalations)} escalations since start. Additional (custom) models are only usable by agents you grant them to.`,
      actions: `<button class="btn" data-act="ai-check">Check provider</button><a href="/models/new" data-nav class="btn primary">Register model</a>`
    }) + card('AI provider', prov ? `<div class="row">${prov.reachable ? badge('Connected', 'ok') : badge('Unreachable', 'bad')} <span class="muted">checked ${esc(ago(prov.checkedUtc))} · ${num((prov.availableModels || []).length)} models offered</span></div>
        ${prov.errorMessage ? `<div class="error-text section">${esc(prov.errorMessage)}</div>` : ''}${prov.missingModels && prov.missingModels.length ? `<div class="section">Missing: ${chips(prov.missingModels, 'warn')}</div>` : ''}` : '—') +
      pausedProvidersCard(paused.items || []) +
      card('Models', modelsTable, { flush: true, cls: 'section' }) +
      contextLimitsCard(limits.items || []) +
      card('Model access by agent', matrix, { flush: true, hint: 'profile = allowed by the agent definition · granted = additional access given by an administrator (hover for its purpose)' }) +
      `<div class="grid two section">${card('Routing ladders', table([{ h: 'Purpose', f: r => esc(r[0]) }, { h: 'Levels', f: r => r[1].map((k, i) => `${i ? ' → ' : ''}<code>${esc(k)}</code>`).join('') }], Object.entries(m.routing.ladders || {})) +
        `<p class="muted small section">Spillover from <code>${esc(m.routing.spillover.fromModel)}</code> to ${chips(m.routing.spillover.candidates)} after ${m.routing.spillover.normalAfterSeconds}s (Normal) — only when the agent can afford it. Custom models are never on ladders; agents use them deliberately (consult_model / switch_model) or when you pin a task to them.</p>`)}
        ${card('Media', `<dl class="kv"><dt>Vision models</dt><dd>${chips(m.vision.models)} expert ${chips(m.vision.expert)}</dd>
          <dt>Image generation</dt><dd>${m.imageGeneration.enabled ? badge('Enabled', 'ok') : badge('Disabled', '')} <code>${esc(m.imageGeneration.model)}</code> · ${m.imageGeneration.pricing.perImage !== null ? '$' + m.imageGeneration.pricing.perImage + '/image' : badge('unpriced', 'warn', false)}</dd></dl>`)}</div>`;
  }
};
/** Providers out of credit: what is paused, since when, and the button that turns each back on after balance was added. */
function pausedProvidersCard(items) {
  if (!items.length) return '';
  return card('Out of credit', `<p class="muted small">These providers' accounts ran out of credit, so their models are paused and agents use other providers. Add balance at the provider, then turn it back on here. Until then each is tried again by itself when its pause ends.</p>` +
    table([
      { h: 'Provider', f: p => `<b>${esc(p.provider)}</b><div class="cell-sub">${esc(p.lastError || '')}</div>` },
      { h: 'Models', f: p => chips(p.models || [], 'warn') },
      { h: 'Since', cls: 'nw', f: p => `${esc(ago(p.sinceUtc))}<div class="cell-sub">found empty ${num(p.times || 1)}×</div>` },
      { h: 'Next try', cls: 'nw', f: p => esc(new Date(p.retryUtc) - Date.now() > 60000 ? ago(p.retryUtc) : 'with the next call') },
      { h: '', cls: 'act', f: p => can('admin') ? `<button class="btn xs primary" data-act="resume-provider" data-provider="${esc(p.provider)}">Turn back on</button>` : '' }
    ], items), { flush: false, cls: 'section warn-card' });
}
/** Real context windows the providers showed (a refusal names it; an answered prompt proves at least that much). */
function contextLimitsCard(items) {
  if (!items.length) return '';
  const tokens = n => n ? num(n) : '—';
  return card('Context windows learned', table([
    { h: 'Model', f: x => `<code>${esc(x.modelId)}</code>${(x.models || []).length ? `<div class="cell-sub">${esc(x.models.join(', '))}</div>` : ''}` },
    { h: 'Configured', num: true, f: x => tokens(x.configured) },
    { h: 'Used now', num: true, f: x => x.effective !== x.configured ? `<b>${tokens(x.effective)}</b>` : tokens(x.effective) },
    { h: 'Largest answered', num: true, f: x => `${tokens(x.maxAccepted)}<div class="cell-sub">${num(x.accepted)} calls</div>` },
    { h: 'Refused', f: x => x.rejected ? `${x.reported ? `provider says ${num(x.reported)}` : `at ~${num(x.minRejected)}`}<div class="cell-sub" title="${esc(x.lastError || '')}">${num(x.rejected)}× · ${esc(x.source || '')}</div>` : '<span class="muted">never</span>' },
    { h: 'Updated', cls: 'nw', f: x => esc(ago(x.updatedUtc)) },
    { h: '', cls: 'act', f: x => can('admin') ? `<button class="btn xs" data-act="forget-context-limit" data-model="${esc(x.modelId)}">Forget</button>` : '' }
  ], items), { flush: true, cls: 'section', hint: 'learned from agents and chat calls: a refusal ("too long") sets the real window, a prompt the provider counted and answered proves it is at least that big' });
}
ACTIONS['forget-context-limit'] = async el => { await del('/api/models/context-limits?model=' + enc(el.dataset.model)); toast('Forgotten: the configured window applies again', 'ok'); refreshNow(); };
ACTIONS['reset-circuit'] = async el => { await post(`/api/models/${enc(el.dataset.key)}/reset-circuit`); toast('Circuit reset', 'ok'); refreshNow(); };
ACTIONS['ai-check'] = async () => { const r = await post('/api/ai/check'); toast(r.reachable ? 'Provider reachable' : 'Provider unreachable: ' + r.errorMessage, r.reachable ? 'ok' : 'bad'); refreshNow(); };
ACTIONS['delete-model'] = async el => {
  if (!await confirmBox('Remove custom model', `Remove <b>${esc(el.dataset.key)}</b>? Every agent's grant for it is revoked immediately.`, { danger: true, confirm: 'Remove' })) return;
  await del('/api/models/custom/' + enc(el.dataset.key)); toast('Model removed', 'ok'); refreshNow();
};
/** Model-centric access manager: who may use this model, and grant it to more agents. */
ACTIONS['model-access'] = async el => {
  const key = el.dataset.key;
  const [m, grants, agents] = await Promise.all([get('/api/models'), get('/api/model-grants?model=' + enc(key)), can('agents.view') ? get('/api/agents') : []]);
  const model = m.models.find(x => x.key === key);
  const byProfile = model.profileAgents || [];
  const candidates = agents.filter(a => !byProfile.includes(a.id) && !grants.some(g => g.agentId === a.id));
  openModal(`Access to ${key}`, `<p class="muted">${esc(model.modelId)}${model.useFor ? ' · intended for: ' + esc(model.useFor) : ''}</p>
    ${byProfile.length ? `<h3 class="section">Allowed by agent profiles</h3>${chips(byProfile)}` : ''}
    <h3 class="section">Granted by administrators</h3>${table([{ h: 'Agent', f: g => agentLink(g.agentId) }, { h: 'Use for', cls: 'clip', f: g => `<span data-tip="${esc(g.useFor)}">${esc(g.useFor)}</span>` },
      { h: 'Subagents', f: g => g.allowSubagents ? badge('allowed', 'ok', false) : '<span class="muted">no</span>' }, { h: 'Granted', f: g => `${esc(g.grantedBy)} · ${esc(ago(g.grantedUtc))}` },
      { h: '', cls: 'act', f: g => `<button class="btn xs danger" data-act="revoke-grant" data-id="${esc(g.agentId)}" data-key="${esc(key)}">Revoke</button>` }], grants, { empty: 'Not granted to anyone.' })}
    ${candidates.length ? `<h3 class="section">Grant to another agent</h3><form id="grant-form" class="form-grid" data-form="grant-model" data-key="${esc(key)}">
      <label>Agent<select name="agent">${candidates.map(a => `<option value="${esc(a.id)}">${esc(a.name)} (${esc(a.id)})</option>`).join('')}</select></label>
      <label class="span2">Use this model for<input name="useFor" required minlength="3" maxlength="1000" value="${esc(model.useFor || '')}" placeholder="e.g. drafting legal contracts"></label>
      <label class="check"><input type="checkbox" name="allowSubagents" value="1"> Its subagents may use it too</label></form>` : ''}`,
    candidates.length ? `<button class="btn" data-act="modal-close">Close</button><button class="btn primary" data-act="submit-form" data-form-id="grant-form">Grant access</button>` : '', 'wide');
};
FORMS['grant-model'] = async (f, d) => {
  await put(`/api/agents/${enc(d.agent)}/model-grants/${enc(f.dataset.key)}`, { useFor: d.useFor, allowSubagents: d.allowSubagents === '1' });
  toast(`${f.dataset.key} granted to ${d.agent}`, 'ok'); closeModal(); refreshNow();
};
ACTIONS['revoke-grant'] = async el => {
  if (!await confirmBox('Revoke model access', `Revoke <b>${esc(el.dataset.key)}</b> from <b>${esc(el.dataset.id)}</b>? It takes effect on the agent's next AI call (subagents included).`, { danger: true, confirm: 'Revoke' })) return;
  await del(`/api/agents/${enc(el.dataset.id)}/model-grants/${enc(el.dataset.key)}`); toast('Access revoked', 'ok'); closeModal(); refreshNow();
};

// ================================================================== MCP, events, config, audit
/// Well-known MCP servers (the same list as the desktop app's Local MCP page and the CLI's "agentic mcp catalog"). Server
// MCP servers are set in appsettings.json (McpServers): each card copies a ready entry to paste there.
const MCP_CATALOG = [
  {"id":"filesystem","name":"Filesystem","category":"Files & code","needs":"Node.js","blurb":"Read, search and edit files in the folders you allow.","command":"npx","args":["-y","@modelcontextprotocol/server-filesystem","{folder}"],"approval":true,"docs":"https://github.com/modelcontextprotocol/servers/tree/main/src/filesystem"},
  {"id":"git","name":"Git","category":"Files & code","needs":"uv (Python)","blurb":"Read a Git repository: status, diffs, log, branches and commits.","command":"uvx","args":["mcp-server-git","--repository","{folder}"],"approval":true,"docs":"https://github.com/modelcontextprotocol/servers/tree/main/src/git"},
  {"id":"github","name":"GitHub","category":"Files & code","needs":"a GitHub token","blurb":"GitHub's official server: repositories, issues, pull requests, code search and Actions.","url":"https://api.githubcopilot.com/mcp/","bearer":"GITHUB_TOKEN","approval":true,"env":[{"key":"GITHUB_TOKEN","hint":"a personal access token (github.com/settings/tokens)"}],"docs":"https://github.com/github/github-mcp-server"},
  {"id":"desktop-commander","name":"Desktop Commander","category":"Files & code","needs":"Node.js","blurb":"Terminal commands, processes and file editing on the computer it runs on.","command":"npx","args":["-y","@wonderwhy-er/desktop-commander"],"approval":true,"docs":"https://github.com/wonderwhy-er/DesktopCommanderMCP"},
  {"id":"playwright","name":"Playwright browser","category":"Browser & web","needs":"Node.js 20+","blurb":"Microsoft's browser automation: open pages, click, type, fill forms and read pages through the accessibility tree.","command":"npx","args":["-y","@playwright/mcp@latest"],"approval":true,"docs":"https://github.com/microsoft/playwright-mcp"},
  {"id":"chrome-devtools","name":"Chrome DevTools","category":"Browser & web","needs":"Node.js 20.19+ and Chrome","blurb":"Google's server for Chrome: drive the browser, read the console and network, record performance traces.","command":"npx","args":["-y","chrome-devtools-mcp@latest"],"approval":true,"docs":"https://github.com/ChromeDevTools/chrome-devtools-mcp"},
  {"id":"fetch","name":"Fetch","category":"Browser & web","needs":"uv (Python)","blurb":"Download a web page and turn it into Markdown for the model.","command":"uvx","args":["mcp-server-fetch"],"docs":"https://github.com/modelcontextprotocol/servers/tree/main/src/fetch"},
  {"id":"firecrawl","name":"Firecrawl","category":"Browser & web","needs":"Node.js 22+ and a Firecrawl API key","blurb":"Scrape, crawl and extract structured data from websites, including JavaScript-heavy ones.","command":"npx","args":["-y","firecrawl-mcp"],"env":[{"key":"FIRECRAWL_API_KEY","hint":"from firecrawl.dev"}],"docs":"https://github.com/mendableai/firecrawl-mcp-server"},
  {"id":"brave-search","name":"Brave Search","category":"Search","needs":"Node.js and a Brave Search API key","blurb":"Web, news, image and local search through Brave's independent index.","command":"npx","args":["-y","@brave/brave-search-mcp-server"],"env":[{"key":"BRAVE_API_KEY","hint":"from brave.com/search/api"}],"docs":"https://github.com/brave/brave-search-mcp-server"},
  {"id":"tavily","name":"Tavily","category":"Search","needs":"Node.js and a Tavily API key","blurb":"Search built for AI agents, with page extraction and site crawling.","command":"npx","args":["-y","tavily-mcp@latest"],"env":[{"key":"TAVILY_API_KEY","hint":"from tavily.com"}],"docs":"https://github.com/tavily-ai/tavily-mcp"},
  {"id":"exa","name":"Exa","category":"Search","needs":"Node.js 20+ and an Exa API key","blurb":"Neural web search: find pages by meaning, research papers, companies and code.","command":"npx","args":["-y","exa-mcp-server"],"env":[{"key":"EXA_API_KEY","hint":"from dashboard.exa.ai"}],"docs":"https://github.com/exa-labs/exa-mcp-server"},
  {"id":"context7","name":"Context7","category":"Documentation","needs":"Node.js 20+","blurb":"Up-to-date documentation and code examples for thousands of libraries, so code uses current APIs.","command":"npx","args":["-y","@upstash/context7-mcp"],"docs":"https://github.com/upstash/context7"},
  {"id":"microsoft-learn","name":"Microsoft Learn","category":"Documentation","needs":"nothing (hosted)","blurb":"Search and read official Microsoft documentation: .NET, Azure, Windows, Microsoft 365.","url":"https://learn.microsoft.com/api/mcp","docs":"https://github.com/MicrosoftDocs/mcp"},
  {"id":"deepwiki","name":"DeepWiki","category":"Documentation","needs":"nothing (hosted)","blurb":"Ask questions about any public GitHub repository and read its generated wiki.","url":"https://mcp.deepwiki.com/mcp","docs":"https://docs.devin.ai/work-with-devin/deepwiki-mcp"},
  {"id":"aws-docs","name":"AWS Documentation","category":"Documentation","needs":"uv (Python)","blurb":"Search and read AWS documentation and get recommendations for related pages.","command":"uvx","args":["awslabs.aws-documentation-mcp-server@latest"],"docs":"https://github.com/awslabs/mcp"},
  {"id":"notion","name":"Notion","category":"Productivity","needs":"Node.js and a Notion integration token","blurb":"Search, read and update pages and databases in your Notion workspace.","command":"npx","args":["-y","@notionhq/notion-mcp-server"],"approval":true,"env":[{"key":"NOTION_TOKEN","hint":"an internal integration token (notion.so/my-integrations)"}],"docs":"https://github.com/makenotion/notion-mcp-server"},
  {"id":"supabase","name":"Supabase","category":"Data","needs":"Node.js and a Supabase access token","blurb":"Query and manage Supabase projects: tables, SQL, migrations, logs (read-only by default here).","command":"npx","args":["-y","@supabase/mcp-server-supabase@latest","--read-only"],"approval":true,"env":[{"key":"SUPABASE_ACCESS_TOKEN","hint":"a personal access token (supabase.com/dashboard/account/tokens)"}],"docs":"https://github.com/supabase-community/supabase-mcp"},
  {"id":"memory","name":"Memory","category":"Thinking","needs":"Node.js","blurb":"A knowledge graph the model can store facts in and recall across sessions.","command":"npx","args":["-y","@modelcontextprotocol/server-memory"],"docs":"https://github.com/modelcontextprotocol/servers/tree/main/src/memory"},
  {"id":"sequential-thinking","name":"Sequential Thinking","category":"Thinking","needs":"Node.js","blurb":"Structured step-by-step reasoning the model can revise and branch for hard problems.","command":"npx","args":["-y","@modelcontextprotocol/server-sequential-thinking"],"docs":"https://github.com/modelcontextprotocol/servers/tree/main/src/sequentialthinking"},
  {"id":"time","name":"Time","category":"Thinking","needs":"uv (Python)","blurb":"The current time in any time zone, and conversions between zones.","command":"uvx","args":["mcp-server-time"],"docs":"https://github.com/modelcontextprotocol/servers/tree/main/src/time"},
  {"id":"rea","name":"REA — reverse engineering","category":"Security","needs":"Node.js 22+; optional Ghidra, Hopper or IDA","blurb":"Reverse-engineer native binaries, .NET assemblies, JS/Electron apps, Android APKs and websites: pseudocode, disassembly, modules and behaviour.","command":"npx","args":["-y","rea-agents@6.3.0","mcp"],"approval":true,"seed":true,"docs":"https://github.com/morluto/rea"}
];
/** A catalog entry as an McpServers item of appsettings.json: secrets are read from environment variables. */
function mcpCatalogEntry(r) {
  const base = { Id: r.id, Name: r.name, Description: r.blurb, Enabled: true };
  if (r.url) {
    return Object.assign(base, { Transport: 'Http', Endpoint: r.url, Auth: r.bearer ? { Type: 'Bearer', SecretEnvVar: r.bearer } : { Type: 'None' } });
  }
  const env = Object.fromEntries((r.env || []).map(e => [e.key, `%${e.key}%`]));
  return Object.assign(base, { Transport: 'Stdio', Command: r.command, Arguments: r.args.map(a => a === '{folder}' ? 'C:\\path\\to\\folder' : a), EnvironmentVariables: env });
}
function mcpCatalogCard(configured) {
  const have = new Set((configured || []).map(s => String(s.id).toLowerCase()));
  const recs = MCP_CATALOG.filter(r => !have.has(r.id.toLowerCase()));
  if (!recs.length) return '';
  const groups = [...new Set(recs.map(r => r.category))];
  const item = r => `<div class="mcp-cat-item"><div class="grow"><div class="cell-title">${esc(r.name)} ${r.url ? tag('hosted', 'outline') : ''}</div>
      <div class="small muted">${esc(r.blurb)}</div>
      <div class="cell-sub"><code>${esc(r.url || (r.command + ' ' + r.args.join(' ')))}</code></div>
      <div class="cell-sub">Needs ${esc(r.needs)}${(r.env || []).length ? ` · set ${r.env.map(e => `<code>${esc(e.key)}</code>`).join(', ')}` : ''} · <a href="${esc(r.docs)}" target="_blank" rel="noopener noreferrer">docs</a></div></div>
    <div class="row"><button class="btn xs primary" data-act="mcp-cat-add" data-id="${esc(r.id)}" data-tip="Add it now: for everyone on this server, or on a computer of yours">Add…</button>
    <button class="btn xs ghost" data-act="mcp-cat-copy" data-id="${esc(r.id)}" data-tip="Copy an McpServers entry for appsettings.json">Copy config</button></div></div>`;
  return card('Add a well-known MCP server', groups.map(g => `<div class="menu-title mcp-cat-title">${esc(g)}</div><div class="mcp-cat-grid">${recs.filter(r => r.category === g).map(item).join('')}</div>`).join(''),
    { cls: 'section', hint: 'Add… adds it at once (no restart): servers run on this host (npx needs Node.js, uvx needs uv). Or copy an entry for appsettings.json.' });
}
/** Add… opens My MCP servers with this server ready to add (here for everyone, or on a computer of yours). */
ACTIONS['mcp-cat-add'] = el => {
  const r = MCP_CATALOG.find(x => x.id === el.dataset.id); if (!r || typeof myMcpEntry !== 'function') return;
  const b64 = btoa(unescape(encodeURIComponent(JSON.stringify({ name: r.id, entry: myMcpEntry(r) })))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  if (typeof MYMCP !== 'undefined') MYMCP.linkShown = false;
  navigate('/my-mcp?add=' + b64);
};
ACTIONS['mcp-cat-copy'] = el => {
  const r = MCP_CATALOG.find(x => x.id === el.dataset.id); if (!r) return;
  copyText(JSON.stringify(mcpCatalogEntry(r), null, 2), `${r.name}: McpServers entry copied`);
};

VIEWS.mcp = {
  live: true,
  async render() {
    setCrumbs([['MCP servers', '/mcp']]);
    const [list, ws] = await Promise.all([get('/api/mcp'), get('/api/websearch')]);
    // The search services in the order they are asked (WebSearch.Order): the next one when a service fails or finds nothing.
    const services = (ws.services || []).map((x, i) => ({ ...x, rank: i + 1 }));
    const serviceState = x => !x.available ? badge('Skipped: ' + (x.missing || 'not set up'), '')
      : x.restingUntil ? badge('Resting until ' + new Date(x.restingUntil).toLocaleTimeString(), 'warn')
      : x.lastError && (!x.lastSuccessUtc || x.lastErrorUtc > x.lastSuccessUtc) ? badge('Failing', 'bad') : badge('Ready', 'ok');
    const wsBody = ws.enabled
      ? `<dl class="kv"><dt>Order</dt><dd>${esc(ws.provider)}</dd>
          <dt>Searches</dt><dd>${num(ws.searches)} <span class="muted small">(${num(ws.failures)} failed)${ws.lastSuccessUtc ? ' · last success ' + esc(ago(ws.lastSuccessUtc)) : ''}</span></dd>
          ${ws.lastError ? `<dt>Last error</dt><dd class="error-text">${esc(ws.lastError)} <span class="muted small">${esc(ago(ws.lastErrorUtc))}</span></dd>` : ''}
          <dt>Agents with <code>search_web</code></dt><dd>${ws.agents.length ? ws.agents.map(a => agentLink(a)).join(', ') : '<span class="muted">none</span>'}</dd></dl>
        ${table([{ h: '#', num: true, f: x => num(x.rank) },
          { h: 'Service', f: x => `<b>${esc(x.label)}</b>${x.endpoint ? `<div class="cell-sub"><code>${esc(x.endpoint)}</code></div>` : ''}` },
          { h: 'State', f: serviceState },
          { h: 'Searches', num: true, f: x => `${num(x.searches)} <span class="muted small">(${num(x.failures)} failed)</span>` },
          { h: 'Last error', cls: 'clip', f: x => x.lastError ? `<span class="error-text" data-tip="${esc(x.lastError)}">${esc(x.lastError)}</span> <span class="muted small">${esc(ago(x.lastErrorUtc))}</span>` : '—' }],
          services, { empty: 'No search services.' })}
        <p class="muted small section">Asked in this order; the next one when a service fails or finds nothing, and one that keeps failing rests for 5 minutes. Change it with <code>WebSearch.Order</code> (brave, bing, searxng) and add <code>WebSearch.BraveApiKey</code> to use Brave.</p>
        <form class="filters section" data-form="websearch-test"><label class="grow">Test a search<input name="query" placeholder="e.g. latest .NET release" required></label><button class="btn">Search</button></form>
        <div id="websearch-results">${S.wsTest || ''}</div>`
      : '<p class="muted">Not configured. Set <code>WebSearch.Enabled</code> in appsettings.json: Bing needs nothing else; add <code>WebSearch.BraveApiKey</code> for Brave or <code>WebSearch.UrlTemplate</code> (a SearXNG URL with <code>{query}</code>) for SearXNG.</p>';
    return pageHead('MCP servers & tools', { subtitle: 'Agents only see tools of connected servers they are permitted to use. Add servers at once on <a href="/my-mcp" data-nav>My MCP servers</a> (or with Add… below), or in appsettings.json (McpServers).' }) +
      card('Web search (built-in search_web tool)', wsBody, { actions: ws.enabled ? badge(ws.lastError && !ws.lastSuccessUtc ? 'Failing' : 'Enabled', ws.lastError && !ws.lastSuccessUtc ? 'bad' : 'ok') : badge('Disabled', '') }) +
      card('MCP servers & tool providers', table([
        { h: 'Server', f: s => `<b>${esc(s.name)}</b> <code class="muted">${esc(s.id)}</code>${s.builtIn ? ' ' + tag('built-in', 'accent') : ''}<div class="cell-sub">${esc(s.description || '')}</div>` }, { h: 'Transport', f: s => esc(s.transport) },
        { h: 'State', f: s => s.enabled ? badge(s.state) : badge('Disabled', '') }, { h: 'Tools', f: s => chips(s.tools) },
        { h: 'Calls', num: true, f: s => `${num(s.calls)} <span class="muted small">(${num(s.failures)} failed)</span>` },
        { h: 'Last connected', f: s => esc(ago(s.lastConnectedUtc)) }, { h: 'Last error', cls: 'clip', f: s => s.lastError ? `<span class="error-text" data-tip="${esc(s.lastError)}">${esc(s.lastError)}</span>` : '—' },
        { h: '', cls: 'act', f: s => s.enabled && !s.builtIn ? `<button class="btn xs" data-act="mcp-reconnect" data-id="${esc(s.id)}">Reconnect</button>` : '' }],
        // Web search is a built-in tool (not an MCP server) but is listed here so every external tool provider is in one place.
        [{ id: 'web-search', name: 'Web search', builtIn: true, description: 'search_web tool for all agents · ' + (ws.provider || 'not configured'),
           transport: 'HTTP (JSON API)', enabled: ws.enabled, state: ws.lastError && !ws.lastSuccessUtc ? 'Faulted' : 'Connected', tools: ['search_web'],
           calls: ws.searches, failures: ws.failures, lastConnectedUtc: ws.lastSuccessUtc, lastError: ws.lastError }, ...list], { empty: 'No MCP servers configured.' }), { flush: true, cls: 'section' }) + mcpCatalogCard(list);
  }
};
FORMS['websearch-test'] = async (f, data) => {
  const show = html => { S.wsTest = html; const box = $('#websearch-results'); if (box) box.innerHTML = html; };
  show('<p class="muted small section">Searching…</p>');
  try {
    const r = await post('/api/websearch/test', { query: data.query, maxResults: 5 });
    show(`<p class="muted small section">"${esc(r.query)}": ${plural(r.results.length, 'result')} from ${esc(r.provider || 'search')} in ${dur(r.durationMs)}${r.unresponsiveEngines.length ? ' · unresponsive engines: ' + esc(r.unresponsiveEngines.join('; ')) : ''}</p>` +
      (r.answers.length ? `<div class="notice info section">${r.answers.map(esc).join('<br>')}</div>` : '') +
      table([{ h: '#', num: true, f: x => num(r.results.indexOf(x) + 1) },
        { h: 'Result', f: x => `<span class="cell-title">${esc(x.title || x.url)}</span><div class="cell-sub">${esc(x.url)}</div>${x.snippet ? `<div class="small">${esc(x.snippet)}</div>` : ''}` },
        { h: 'Engines', f: x => chips(x.engines) }], r.results, { empty: 'No results.' }));
  } catch (e) { show(`<div class="notice bad section">${esc(e.message)}</div>`); }
};
ACTIONS['mcp-reconnect'] = async el => { await post(`/api/mcp/${enc(el.dataset.id)}/reconnect`); toast('Reconnect requested', 'ok'); setTimeout(refreshNow, 1500); };

VIEWS.sandbox = {
  live: true,
  async render() {
    setCrumbs([['Sandbox', '/sandbox']]);
    const all = !!S.sbxAll;
    const [r, appsR] = await Promise.all([get('/api/sandbox/resources' + (all ? '?state=all' : '')), get('/api/sandbox/apps').catch(() => null)]);
    const apps = (appsR && appsR.apps) || [];
    const appsWarn = appsR && appsR.warning ? `<p class="small sbx-apps-warn">${icon('alert')} ${esc(appsR.warning)}</p>` : '';
    const appsCard = appsR ? card('Running apps', appsWarn + (apps.length ? table([
      { h: 'App', f: a => `<span class="cell-title">${esc(a.name)}</span><div class="cell-sub">${esc(a.image || '')}</div>` },
      { h: 'Ports', f: a => esc(a.ports.map(p => p.host + ' → ' + p.container).join(', ')) },
      { h: 'Started', f: a => esc(ago(a.createdUtc)) },
      { h: '', cls: 'act', f: a => `<button class="btn xs primary" data-act="sbx-open-app" data-container="${esc(a.name)}">Open app</button>` }], apps, { empty: '' })
      : '<p class="muted small">No web app is running in a sandbox container (one started here, with a published port).</p>'),
      { flush: !!apps.length && !appsWarn, actions: '<span class="muted small">Opened through this site at /container/… (an isolated page; the link expires).</span>' }) : '';
    const res = r.resources || [];
    const running = res.filter(x => x.state === 'running');
    const reaped = res.filter(x => x.state !== 'running');
    const lifeBits = [];
    if (r.processMaxLifeMinutes) lifeBits.push(`processes: max ${fmtMins(r.processMaxLifeMinutes)}${r.processIdleMinutes ? `, idle ${fmtMins(r.processIdleMinutes)}` : ''}`);
    if (r.manageContainers && r.containerMaxLifeMinutes) lifeBits.push(`containers: max ${fmtMins(r.containerMaxLifeMinutes)}`);
    const head = pageHead('Sandbox resources', {
      badges: (r.reaperEnabled ? badge('Reaper on', 'ok') : badge('Reaper off', '')) + (r.manageContainers ? badge('Containers', 'accent') : badge('Processes only', '')),
      subtitle: 'Long-lived processes and Docker containers the host started in the code sandbox. Only these are ever stopped automatically — anything that pre-dates the host, and Docker Compose stacks, are left alone.'
    });
    const cols = [
      { h: 'Kind', f: x => `<span class="sbx-kind">${esc(x.kind)}</span>` },
      { h: 'Name', f: x => `<span class="cell-title">${esc(x.name || x.ref)}</span>${x.command && x.command !== x.name ? `<div class="cell-sub clip" data-tip="${esc(x.command)}">${esc(x.command.slice(0, 80))}</div>` : ''}` },
      { h: 'Owner', f: x => x.ownerChatId ? `<a href="/chat/${enc(x.ownerChatId)}" data-nav>${esc(x.ownerUser || 'chat')}</a>` : (x.ownerUser ? esc(x.ownerUser) : '<span class="muted">—</span>') },
      { h: 'Started', f: x => esc(ago(x.createdUtc || x.firstSeenUtc)) },
      { h: 'Last active', f: x => esc(ago(x.lastSeenUtc)) },
      { h: 'State', f: x => x.isProtected ? badge('Protected', '') : (x.state === 'running' ? badge('Running', 'ok') : badge(x.state, x.state === 'failed' ? 'bad' : '')) + (x.reason ? `<div class="cell-sub">${esc(x.reason)}</div>` : '') },
      { h: '', cls: 'act', f: x => x.state === 'running' && !x.isProtected ? `<button class="btn xs danger" data-act="sbx-stop" data-id="${esc(x.id)}" data-name="${esc(x.name || x.ref)}">Stop</button>` : '' }
    ];
    return head + appsCard +
      card('Running', table(cols, running, { empty: r.reaperEnabled ? 'Nothing long-lived is running in the sandbox.' : 'The sandbox reaper is turned off (Chat.Sandbox.ReaperEnabled).' }),
        { flush: true, actions: `<span class="muted small">${lifeBits.join(' · ') || 'No lifetime limits set.'}</span> <button class="btn xs ghost" data-act="sbx-toggle-all">${all ? 'Hide stopped' : 'Show stopped'}</button>` }) +
      (all && reaped.length ? card('Stopped / gone', table(cols, reaped, { empty: 'None.' }), { flush: true }) : '');
  }
};
function fmtMins(m) { m = +m || 0; if (m < 60) return m + ' min'; return (m % 60 === 0) ? (m / 60) + 'h' : (Math.floor(m / 60)) + 'h ' + (m % 60) + 'm'; }
ACTIONS['sbx-toggle-all'] = () => { S.sbxAll = !S.sbxAll; refreshNow(); };
ACTIONS['sbx-stop'] = async el => {
  if (!await confirmBox(`Stop “${el.dataset.name}”?`, 'The process or container will be shut down now. Nothing you created is deleted.', { danger: true, confirm: 'Stop' })) return;
  el.disabled = true;
  try { const r = await post(`/api/sandbox/resources/${enc(el.dataset.id)}/stop`, {}); toast(r.ok ? 'Stopped' : 'Could not stop it', r.ok ? 'ok' : 'bad'); }
  catch (e) { toast(e.message || 'Could not stop it', 'bad'); }
  refreshNow();
};

VIEWS.config = {
  live: false,
  async render() {
    setCrumbs([['Configuration', '/config']]);
    const [v, s] = await Promise.all([get('/api/config/validate'), get('/api/config/summary')]);
    return pageHead('Configuration', { badges: v.valid ? badge('Valid', 'ok') : badge('Invalid', 'bad'), subtitle: `<code>${esc(v.configPath)}</code>` }) +
      card('Validation', `${v.errors.map(e => `<div class="error-text">✖︎ ${esc(e)}</div>`).join('')}${v.warnings.map(w => `<div class="muted">⚠︎ ${esc(w)}</div>`).join('') || (v.errors.length ? '' : '<div class="muted">No warnings.</div>')}`) +
      card('Effective configuration (secrets removed)', json(s));
  }
};

VIEWS.audit = {
  live: false,
  async render() {
    setCrumbs([['Audit log', '/audit']]);
    const list = await get('/api/audit?limit=400');
    return pageHead('Audit log', { subtitle: 'Every administrative change, who made it and from where.' }) + card('', table([
      { h: 'When', f: a => esc(ts(a.createdUtc)) }, { h: 'Actor', f: a => esc(a.actor) }, { h: 'Action', f: a => tag(a.action, 'accent') },
      { h: 'Target', f: a => `${esc(a.targetType || '')}${a.targetId ? ` <code>${esc(a.targetId)}</code>` : ''}` },
      { h: 'Details', cls: 'clip', f: a => a.detailsJson ? `<code data-tip="${esc(a.detailsJson.slice(0, 800))}">${esc(a.detailsJson)}</code>` : '' },
      { h: 'From', f: a => esc(a.remoteAddress || '') }], list, { empty: 'No admin actions yet.' }), { flush: true, export: 'audit-log' });
  }
};
