'use strict';
/* Agentic console - agent detail: overview, queue manager (drag & drop ordering, edit, hold, bulk), history, subagents,
   model access (grants), memory, messages, communications, tools, AI usage, budget, activity, artifacts, settings. */

const AGENT_TABS = [['', 'Overview'], ['queue', 'Queue'], ['history', 'History'], ['autonomy', 'Autonomy'], ['subagents', 'Subagents'], ['models', 'Model access'],
  ['memory', 'Memory'], ['messages', 'Messages'], ['communications', 'Communications'], ['tools', 'Tools'], ['ai', 'AI usage'],
  ['budget', 'Budget'], ['learning', 'Learning'], ['activity', 'Activity'], ['artifacts', 'Artifacts'], ['settings', 'Settings']];
const SUBAGENT_TABS = [['', 'Overview'], ['activity', 'Activity'], ['ai', 'AI usage'], ['tools', 'Tools'], ['artifacts', 'Artifacts']];

VIEWS.agent = {
  live: true,
  // /agents/X?task=Y (a link in an agent's report in the chat, or after delegating) opens that task's details once.
  after(p) {
    const task = p.query.get('task');
    if (task && S.taskLinkOpened !== p) { S.taskLinkOpened = p; openTask(p.args[0], task, false).catch(e => toast(e.message, 'bad')); }
  },
  async render(p) {
    const id = p.args[0]; const tab = p.args[1] || '';
    const d = await get('/api/agents/' + enc(id));
    const a = d.snapshot, prof = d.profile || {};
    const isSub = !!a.isSubagent;
    setCrumbs(isSub ? [['Agents', '/agents'], [a.parentAgentId, '/agents/' + enc(a.parentAgentId)], ['Subagent ' + a.name, '']] : [['Agents', '/agents'], [a.name, '']]);
    const counts = d.taskCounts || {};
    const tabsDef = isSub ? SUBAGENT_TABS : AGENT_TABS;
    const tabCount = { queue: (counts.Pending || 0) + (counts.Retrying || 0) + (counts.Blocked || 0) + (counts.Waiting || 0) + (counts.Running || 0), models: (d.grants || []).length };
    const tabPerms = { memory: 'agents.memory', budget: 'finance.view', settings: 'agents.manage' };
    const tabs = tabsDef.filter(([k]) => can(tabPerms[k])).map(([k, label]) => `<a href="/agents/${enc(id)}${k ? '/' + k : ''}" data-nav class="${k === tab ? 'active' : ''}">${label}${tabCount[k] ? `<span class="count">${tabCount[k]}</span>` : ''}</a>`).join('');
    let body;
    switch (tab) {
      case 'queue': body = await queueTab(id, a); break;
      case 'history': body = await historyTab(id, p.query.get('status') || ''); break;
      case 'autonomy': body = await autonomyTab(id); break;
      case 'subagents': body = await subagentsTab(id, prof); break;
      case 'models': body = await modelAccessTab(id); break;
      case 'memory': body = await memoryTab(id, p.query.get('q') || ''); break;
      case 'messages': body = await messagesTab(id); break;
      case 'communications': body = await commsTab(id, prof); break;
      case 'tools': body = await toolsTab(id); break;
      case 'ai': body = await aiTab(id); break;
      case 'budget': body = await budgetTab(id, isSub, a); break;
      case 'learning': body = await learningTab(id); break;
      case 'activity': body = await activityTab(id); break;
      case 'artifacts': body = await artifactsTab(id); break;
      case 'settings': body = await settingsTab(id); break;
      default: body = overviewTab(d);
    }
    const editable = a.source === 'Admin';
    const actions = isSub ? controlButtons(a, false) :
      `${controlButtons(a, false)}<button class="btn primary" data-act="add-task" data-id="${esc(id)}">Add task</button>
       <button class="btn" data-act="message-agent" data-id="${esc(id)}">Message</button>
       ${editable ? link('/agents/' + enc(id) + '/edit', 'Edit', 'btn') : link('/agents/new?clone=' + enc(id), 'Clone', 'btn')}`;
    return pageHead(a.name, {
      badges: `<span class="state-chip" data-state="${esc(a.state)}">${badge(a.state)}</span> <code class="muted">${esc(a.id)}</code> ${isSub ? tag('subagent of ' + a.parentAgentId, 'info') : ''}` +
        (prof.autonomy && prof.autonomy.level && prof.autonomy.level !== 'supervised' ? ' ' + levelBadge(prof.autonomy.level) : ''),
      subtitle: esc(prof.role || '') + (prof.description ? ' · ' + esc(prof.description) : ''), actions
    }) + (a.state === 'Dead' ? `<div class="notice bad dead-banner"><b>${esc(a.name)} is dead.</b> Its money ran out, so the host shut it down and abandoned its running work.
        It stays dead until its wallet is funded again — then it comes back and its queued tasks continue.
        <button class="btn small primary" data-act="revive-agent" data-id="${esc(a.id)}">Fund &amp; revive…</button></div><div class="section"></div>` : '') +
      `<div class="tabs">${tabs}</div>${body}`;
  }
};

// ------------------------------------------------------------------ overview
function overviewTab(d) {
  const a = d.snapshot, p = d.profile || {}, w = d.wallet;
  const counts = d.taskCounts || {};
  const sub = d.subagent;
  return `<div class="grid kpis">
    ${kpi('Tasks completed', num(a.tasksCompleted), `${num(a.tasksFailed)} failed · ${num(a.tasksRetried)} retried`)}
    ${kpi('Queue', num((counts.Pending || 0) + (counts.Retrying || 0)), `${num(counts.Waiting || 0)} waiting · ${num(counts.Blocked || 0)} held/blocked`, { href: `/agents/${enc(a.id)}/queue` })}
    ${kpi('AI calls', num(a.aiCalls), `${num(a.aiFailures)} failed · avg ${dur(a.avgAiLatencyMs)}`)}
    ${kpi('Tool calls', num(a.toolCalls), `${num(a.toolFailures)} failed · avg ${dur(a.avgToolLatencyMs)}`)}
    ${kpi('Messages', `${num(a.messagesSent)} / ${num(a.messagesReceived)}`, 'sent / received')}
    ${a.isSubagent ? kpi('Cloud spend', money(sub ? sub.spentMicros : 0), sub && sub.spendCapMicros !== null ? `cap ${money(sub.spendCapMicros, 2)} · from ${esc(a.parentAgentId)}` : '')
      : kpi('Balance', w ? money(w.availableMicros, 2) : '—', w ? `spent today ${money(w.spentTodayMicros, 2)}` : '', { href: `/agents/${enc(a.id)}/budget` })}
    ${a.isSubagent ? '' : kpi('Subagents', `${num(a.activeSubagents ?? 0)}<span class="muted small"> max ${num(p.maxSubagents)}</span>`, 'running in parallel', { href: `/agents/${enc(a.id)}/subagents` })}
  </div>
  ${sub ? card('Subagent', `<dl class="kv"><dt>Parent</dt><dd>${agentLink(sub.parentAgentId)} ${sub.parentTaskId ? `· <a href="#" data-act="open-task" data-id="${esc(sub.parentAgentId)}" data-task="${esc(sub.parentTaskId)}">parent task</a>` : ''}</dd>
    <dt>Models</dt><dd>${chips(sub.models)}</dd><dt>Spend</dt><dd>${money(sub.spentMicros)} of ${money(sub.spendCapMicros, 2)} cap, charged to ${esc(sub.parentAgentId)}'s wallet (local model is free)</dd>
    <dt>Deadline</dt><dd>${esc(ts(sub.deadlineUtc))} (${esc(ago(sub.deadlineUtc))})</dd></dl>`, { cls: 'section' }) : ''}
  <div class="grid two section">
    ${card('Runtime', `<dl class="kv">
      <dt>Status</dt><dd>${badge(a.state)} since ${esc(ago(a.stateSinceUtc))}</dd>
      <dt>Current task</dt><dd>${a.currentTaskId ? `<a href="#" data-act="open-task" data-id="${esc(a.id)}" data-task="${esc(a.currentTaskId)}">${esc(a.currentTaskName)}</a> <button class="btn xs danger" data-act="cancel-task" data-id="${esc(a.id)}" data-task="${esc(a.currentTaskId)}">Cancel</button>` : '—'}</dd>
      <dt>Operation</dt><dd>${esc(a.currentOperation || '—')} ${a.currentOperationStartedUtc ? '<span class="muted">(' + esc(ago(a.currentOperationStartedUtc)) + ')</span>' : ''}</dd>
      <dt>Heartbeat</dt><dd>${esc(ago(a.lastHeartbeatUtc))}</dd><dt>Last progress</dt><dd>${esc(ago(a.lastProgressUtc))}</dd>
      <dt>Uptime</dt><dd>${uptime(a.uptimeSeconds)}</dd><dt>Last success</dt><dd>${esc(ago(a.lastSuccessfulTaskUtc))}</dd>
      <dt>Avg task</dt><dd>${dur(a.avgTaskMs)}</dd><dt>Restarts (window)</dt><dd>${num(a.recentRestarts)}</dd>
      <dt>Last error</dt><dd>${a.lastError ? `<span class="error-text">${esc(a.lastError)}</span> <span class="muted">${esc(ago(a.lastErrorUtc))}</span>` : '—'}</dd></dl>`)}
    ${card('Profile & permissions', `<dl class="kv">
      <dt>Models</dt><dd>${chips(p.allowedModels)} <span class="muted small">max tier ${esc(p.maxTier)} · default purpose ${esc(p.defaultPurpose)}</span></dd>
      <dt>Extra models</dt><dd class="kv-grants">${(d.grants || []).length ? (d.grants || []).map(g => `<span data-tip="${esc(g.useFor)}">${tag(g.modelKey, 'accent')}</span>`).join('') : '<span class="muted">none</span>'}${a.isSubagent ? '' : link(`/agents/${enc(a.id)}/models`, 'manage')}</dd>
      <dt>MCP servers</dt><dd>${chips(p.allowedMcpServers)}</dd>
      <dt>Can message</dt><dd>${chips(p.communication && p.communication.allowedRecipients)}</dd>
      <dt>Subagents</dt><dd>${a.isSubagent ? 'cannot spawn' : `up to ${num(p.maxSubagents)} at a time${p.subagentDefaultSpendCap !== null && p.subagentDefaultSpendCap !== undefined ? ', default cap ' + usd(p.subagentDefaultSpendCap) : ''}`}</dd>
      <dt>Limits</dt><dd class="small">${esc(p.tasks ? `progress check every ${p.tasks.maxReasoningTurns} turns or ${p.tasks.maxToolCalls} tool calls, ${p.tasks.maxTotalTurns ? p.tasks.maxTotalTurns + ' turns in all' : 'no fixed turn total'}, ${dur(p.tasks.maxTaskDurationSeconds * 1000)} time slices, ${p.tasks.maxRetries} retries` : '')}
        ${esc(p.ai ? ` · AI ${p.ai.maxAiCallsPerHour ?? '∞'}/h, ${p.ai.maxAiCallsPerDay ?? '∞'}/day` : '')}</dd>
      <dt>Schedules</dt><dd>${(p.schedules || []).map(s => tag(`${s.id} · ${s.intervalSeconds ? s.intervalSeconds + 's' : s.cron} · ${s.handler}`)).join(' ') || '—'}</dd></dl>
      <details class="section"><summary class="muted">System prompt & safety rules</summary><pre>${esc(p.systemPrompt)}\n\n${esc((p.safetyRules || []).join('\n'))}</pre></details>`)}
  </div>
  ${card('Live activity', `<div id="live-feed" class="feed" data-agent="${esc(a.id)}">${S.feed.filter(e => (e.agentId === a.id || (e.agentId || '').startsWith(a.id + '.sub-')) && !isRoutineEvent(e)).slice(0, 80).map(e => feedRow(e)).join('')}</div>`, { flush: true, cls: 'section' })}`;
}

// ------------------------------------------------------------------ queue manager
/** Recurring tasks of an agent (from its configuration or created in the dashboard). */
function scheduleTable(id, list) {
  return table([
    { h: 'Recurring task', f: s => `<span class="cell-title">${esc(s.name || s.scheduleId)}</span> ${s.source === 'Admin' ? tag('dashboard', 'accent') : tag('config')}` +
        `<div class="cell-sub">${esc(s.summary || '')}${s.createdBy ? ' · by ' + esc(s.createdBy) : ''}</div>` },
    { h: 'Status', cls: 'nw', f: s => s.enabled ? badge('Active', 'ok') : (s.nextRunUtc ? badge('Paused', 'warn') : badge(s.runCount ? 'Finished' : 'Paused', s.runCount ? 'outline' : 'warn')) },
    { h: 'Next run', cls: 'nw', f: s => s.enabled && s.nextRunUtc ? `<span data-tip="${esc(ts(s.nextRunUtc))}">${esc(ago(s.nextRunUtc))}</span>` : '<span class="muted">—</span>' },
    { h: 'Last run', cls: 'nw', f: s => s.lastRunUtc ? `<span data-tip="${esc(ts(s.lastRunUtc))}">${esc(ago(s.lastRunUtc))}</span>` + (s.lastTaskId ? ` · <a href="#" data-act="open-task" data-id="${esc(id)}" data-task="${esc(s.lastTaskId)}">task</a>` : '') : '<span class="muted">never</span>' },
    { h: 'Runs', num: true, f: s => num(s.runCount || 0) },
    { h: '', cls: 'act', f: s => {
        const d = `data-id="${esc(id)}" data-schedule="${esc(s.scheduleId)}"`;
        return `<button class="btn xs" data-act="schedule-op" data-op="run" ${d}>Run now</button> ` +
          `<button class="btn xs" data-act="schedule-op" data-op="${s.enabled ? 'pause' : 'resume'}" ${d}>${s.enabled ? 'Pause' : 'Resume'}</button>` +
          (s.source === 'Admin' ? ` <button class="btn xs danger" data-act="schedule-op" data-op="delete" ${d}>Delete</button>` : '');
      } }
  ], list, { empty: 'No recurring tasks', emptyText: 'Add a task and choose how often it repeats.' });
}
ACTIONS['schedule-op'] = async el => {
  const base = `/api/agents/${enc(el.dataset.id)}/schedules/${enc(el.dataset.schedule)}`, op = el.dataset.op;
  if (op === 'delete') {
    if (!await confirmBox('Delete recurring task', 'Stop and delete this recurring task? Tasks it already queued stay in the queue.', { danger: true, confirm: 'Delete' })) return;
    await del(base); toast('Recurring task deleted', 'ok');
  } else if (op === 'run') { const r = await post(base + '/run'); toast(r.message, 'ok'); }
  else { await patch(base, { enabled: op === 'resume' }); toast(op === 'resume' ? 'Recurring task resumed' : 'Recurring task paused', 'ok'); }
  refreshNow();
};

async function queueTab(id, a) {
  const [all, schedules] = await Promise.all([get(`/api/agents/${enc(id)}/tasks?status=queue&limit=500`), get(`/api/agents/${enc(id)}/schedules`).catch(() => [])]);
  const running = all.filter(t => t.status === 'Running');
  const queued = all.filter(t => ['Pending', 'Retrying', 'Blocked'].includes(t.status));
  const waiting = all.filter(t => t.status === 'Waiting');
  const scope = 'queue:' + id;
  const rows = queued.map((t, i) => Object.assign(t, { _pos: i + 1, _prev: queued[i - 1] && queued[i - 1].id, _next: queued[i + 1] && queued[i + 1].id }));
  const mv = (t, label, data, tip) => `<button class="btn xs" data-act="move-task" data-id="${esc(id)}" data-task="${esc(t.id)}" ${data} data-tip="${esc(tip)}">${label}</button>`;
  const upNext = table([
    { h: '', f: () => '<span class="drag-handle" data-tip="Drag to reorder">⋮⋮</span>' },
    { h: '#', f: t => `<span class="pos">${t._pos}</span>` },
    { h: 'Task', cls: 'clip', f: t => taskCell(t) },
    { h: 'Status', f: statusCell },
    { h: 'Priority', f: t => badge(t.priority, undefined, false) },
    { h: 'Starts', f: t => t.nextRunUtc && new Date(t.nextRunUtc) > new Date() ? `<span data-tip="${esc(ts(t.nextRunUtc))}">${esc(ago(t.nextRunUtc))}</span>` : '<span class="muted">when ready</span>' },
    { h: 'Origin', f: originCell },
    {
      h: '', cls: 'act', f: t => (t._prev ? mv(t, '↑', `data-before="${esc(t._prev)}"`, 'Move up') : '') + ' ' + (t._next ? mv(t, '↓', `data-after="${esc(t._next)}"`, 'Move down') : '') + ' ' +
        (t._pos > 1 ? mv(t, 'Top', 'data-position="top"', 'Run next') : '') + ` <button class="btn xs" data-act="edit-task" data-id="${esc(id)}" data-task="${esc(t.id)}">Edit</button> ` + taskActions(t)
    }
  ], rows, {
    sel: { scope, id: t => t.id }, rowAttrs: t => `draggable="true" data-drag-scope="${esc(scope)}" data-drag-id="${esc(t.id)}" data-state="${esc(t.status)}"`,
    empty: 'The queue is empty', emptyText: 'Add a task, or let schedules and other agents fill it.'
  });
  const bulk = bulkBar(scope, ['top:Move to top', 'bottom:Move to bottom', 'hold:Hold', 'release:Release', 'cancel:Cancel'].map(x => {
    const [op, label] = x.split(':');
    return `<button class="btn small${op === 'cancel' ? ' danger' : ''}" data-act="bulk-queue" data-id="${esc(id)}" data-op="${op}">${label}</button>`;
  }).join(''));
  const runningHtml = running.length ? table([
    { h: 'Task', cls: 'clip', f: t => taskCell(t) }, { h: 'Operation', cls: 'clip', f: () => esc(a.currentOperation || '—') },
    { h: 'Started', f: t => esc(ago(t.startedUtc)) }, { h: 'Cost so far', num: true, f: t => money(t.costMicros) },
    { h: '', cls: 'act', f: t => `<button class="btn xs danger" data-act="cancel-task" data-id="${esc(id)}" data-task="${esc(t.id)}">Cancel</button>` }
  ], running, { rowClass: () => 'is-running' }) : emptyState('Not working on anything right now', a.state === 'Paused' ? 'The agent is paused; resume it to continue the queue.' : '');
  return `${a.state === 'Paused' ? `<div class="notice warn">This agent is paused. Queued tasks will not start until you resume it. ${btn('Resume', 'agent-control', { id, op: 'resume' }, 'small')}</div><div class="section"></div>` : ''}
    ${card('Running now', runningHtml, { flush: true })}
    ${card('Up next', bulk + upNext, { flush: false, hint: 'Drag rows (or use ↑ ↓ Top) to reorder. Tasks run top to bottom; priority only sets where new tasks are inserted.', actions: `<button class="btn small primary" data-act="add-task" data-id="${esc(id)}">Add task</button>` })}
    ${card('Recurring tasks', scheduleTable(id, schedules), { flush: true, cls: 'section', hint: 'Each run adds a task to the queue above.' })}
    ${card('Waiting', table([
      { h: 'Task', cls: 'clip', f: t => taskCell(t) }, { h: 'Waiting on', f: t => (t.waitingOn || []).map(k => k.startsWith('sub:') ? tag('subagent ' + k.slice(4).split('.sub-')[1], 'info') : tag(k.split(':')[0])).join(' ') },
      { h: 'Deadline', f: t => esc(ago(t.waitDeadlineUtc)) }, { h: '', cls: 'act', f: t => taskActions(t) }
    ], waiting, { empty: 'Nothing is waiting.' }), { flush: true })}`;
}
DROPS['queue'] = async (d, targetId, pos) => {
  const agentId = d.scope.slice('queue:'.length);
  await post(`/api/agents/${enc(agentId)}/tasks/${enc(d.id)}/move`, pos === 'before' ? { before: targetId } : { after: targetId });
  toast('Queue order updated', 'ok'); refreshNow();
};
ACTIONS['move-task'] = async el => {
  const body = el.dataset.position ? { position: el.dataset.position } : (el.dataset.before ? { before: el.dataset.before } : { after: el.dataset.after });
  await post(`/api/agents/${enc(el.dataset.id)}/tasks/${enc(el.dataset.task)}/move`, body);
  refreshNow();
};
ACTIONS['bulk-queue'] = async el => {
  const scope = 'queue:' + el.dataset.id, ids = selected(scope), op = el.dataset.op;
  if (!ids.length) return;
  if (op === 'cancel' && !await confirmBox('Cancel tasks', `Cancel ${plural(ids.length, 'task')}?`, { danger: true, confirm: 'Cancel tasks' })) return;
  // Keep the on-screen order for moves.
  const order = $$(`tr[data-drag-scope="${CSS.escape(scope)}"]`).map(r => r.dataset.dragId).filter(x => ids.includes(x));
  const r = await post(`/api/agents/${enc(el.dataset.id)}/tasks/bulk`, { action: op, taskIds: op === 'bottom' ? order : order });
  toast(`${r.succeeded} updated${r.failed ? `, ${r.failed} not applicable` : ''}`, r.failed && !r.succeeded ? 'bad' : 'ok');
  clearSel(scope); refreshNow();
};

/** Models this agent can be pinned to (profile + granted, usable). */
async function pinnableModels(id) {
  try {
    const acc = await get(`/api/agents/${enc(id)}/model-access`);
    return acc.profileModels.filter(x => x.usable).map(x => x.model).concat(acc.grants.filter(g => g.usable && g.model).map(g => Object.assign({}, g.model, { granted: true })));
  } catch { return []; }
}
function modelOptions(models, current) {
  return `<option value="">Automatic (routing ladder)</option>` + models.map(m => `<option value="${esc(m.key)}"${m.key === current ? ' selected' : ''}>${esc(m.key)} · ${m.isLocal ? 'local, free' : (m.priced ? `$${m.inputPerMillion}/$${m.outputPerMillion} per 1M` : 'unpriced')}${m.granted ? ' · granted' : ''}</option>`).join('');
}
const PURPOSES = ['GeneralReasoning', 'Planning', 'Classification', 'Extraction', 'Summarization', 'ToolSelection', 'CodeGeneration', 'AdvancedCoding', 'ReverseEngineering', 'ComplexAnalysis', 'ComplexLanguage', 'Research'];
ACTIONS['add-task'] = async el => {
  const id = el.dataset.id;
  const [models, agents] = await Promise.all([pinnableModels(id), get('/api/agents')]);
  openModal('Add task for ' + id, `<form id="task-form" class="form-grid" data-form="submit-task" data-id="${esc(id)}">
    <label class="full">Name<input name="name" required maxlength="200" placeholder="Short, descriptive task name"></label>
    <label class="full">Instructions<textarea name="description" rows="6" placeholder="What should the agent do? Include all context and the expected result."></textarea></label>
    <label>Priority<select name="priority"><option>Low</option><option selected>Normal</option><option>High</option><option>Critical</option></select><span class="help">Sets the default queue position</span></label>
    <label>Queue position<select name="position"><option value="">By priority</option><option value="front">Front of the queue</option></select></label>
    <label>Purpose<select name="purpose"><option value="">Agent default</option>${PURPOSES.map(p => `<option>${p}</option>`).join('')}</select><span class="help">Selects the model routing ladder</span></label>
    <label>Model<select name="modelKey">${modelOptions(models, '')}</select><span class="help">Pin to one model, or let routing decide</span></label>
    ${startFields('Start / first run')}
    <label>Handler<select name="handler"><option>reasoning</option><option>http-check</option><option>mcp-tool</option><option>noop</option></select></label>
    <label>Max retries<input name="maxRetries" type="number" min="0" max="10" placeholder="profile default"></label>
    <label>Start at tier<select name="startTier"><option value="0">Default</option><option value="2">2</option><option value="3">3</option></select></label>
    <label class="full">Payload (optional JSON object)<textarea name="payload" class="code" rows="3" placeholder='{"key": "value"}'></textarea></label>
    ${runFields({ deadline: true })}
    ${deliveryFields(agents, { exceptId: id })}
    <h3 class="full section">Recurrence</h3>
    ${repeatFields()}</form><div data-errors></div>`,
    `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="submit-form" data-form-id="task-form">Add task</button>`, 'wide');
};
CHANGES['start-mode'] = el => { const form = el.closest('form'); $$('[data-start]', form).forEach(x => x.classList.toggle('hidden', x.dataset.start !== el.value)); };
FORMS['submit-task'] = async (f, data) => {
  const body = { name: data.name, description: data.description, handler: data.handler, priority: data.priority, startTier: Number(data.startTier || 0) };
  if (data.purpose) body.purpose = data.purpose;
  if (data.modelKey) body.modelKey = data.modelKey;
  if (data.position) body.position = data.position;
  if (data.maxRetries !== '' && data.maxRetries !== undefined) body.maxRetries = Number(data.maxRetries);
  readStart(data, body);
  if (data.payload && data.payload.trim()) { try { body.payload = JSON.parse(data.payload); } catch { throw new Error('Payload must be valid JSON.'); } }
  readDelivery(f, body);
  readRun(f, body, { deadline: true });
  const repeat = readRepeat(f);
  if (repeat) {
    body.repeat = repeat;
    const r = await post(`/api/agents/${enc(f.dataset.id)}/schedules`, body);
    toast(`Recurring task created: ${r.summary}${r.nextRunUtc ? ' · first run ' + ts(r.nextRunUtc) : ''}`, 'ok');
  } else {
    const r = await post(`/api/agents/${enc(f.dataset.id)}/task`, body);
    toast('Task queued: ' + r.taskId, 'ok');
  }
  closeModal(); refreshNow();
};
ACTIONS['edit-task'] = async el => {
  const id = el.dataset.id;
  const [d, models] = await Promise.all([get(`/api/agents/${enc(id)}/tasks/${enc(el.dataset.task)}`), pinnableModels(id)]);
  const t = d.task;
  const local = t.nextRunUtc ? new Date(new Date(t.nextRunUtc).getTime() - new Date().getTimezoneOffset() * 60000).toISOString().slice(0, 16) : '';
  openModal('Edit task', `<form id="edit-task-form" class="form-grid" data-form="edit-task" data-id="${esc(id)}" data-task="${esc(t.id)}">
    <label class="full">Name<input name="name" required maxlength="200" value="${esc(t.name)}"></label>
    <label class="full">Instructions<textarea name="description" rows="7">${esc(t.description || '')}</textarea></label>
    <label>Priority<select name="priority">${['Low', 'Normal', 'High', 'Critical'].map(p => `<option${p === t.priority ? ' selected' : ''}>${p}</option>`).join('')}</select><span class="help">Changing it re-inserts the task by priority</span></label>
    <label>Purpose<select name="purpose"><option value="">Agent default</option>${PURPOSES.map(p => `<option${p === t.purpose ? ' selected' : ''}>${p}</option>`).join('')}</select></label>
    <label>Model<select name="modelKey">${modelOptions(models, t.modelKey)}</select></label>
    <label>Start at (local time)<input name="runAt" type="datetime-local" value="${esc(local)}"><span class="help">Empty = as soon as possible</span></label>
    <label>Max retries<input name="maxRetries" type="number" min="0" max="10" value="${t.maxRetries}"></label></form><div data-errors></div>`,
    `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="submit-form" data-form-id="edit-task-form">Save changes</button>`, 'wide');
};
FORMS['edit-task'] = async (f, data) => {
  const body = { name: data.name, description: data.description, priority: data.priority, purpose: data.purpose || null, modelKey: data.modelKey || null };
  // An emptied field keeps the task's setting (it used to become 0 retries).
  if (data.maxRetries !== '' && data.maxRetries !== undefined) body.maxRetries = Number(data.maxRetries);
  if (data.runAt) body.runAtUtc = new Date(data.runAt).toISOString(); else body.runNow = true;
  await patch(`/api/agents/${enc(f.dataset.id)}/tasks/${enc(f.dataset.task)}`, body);
  toast('Task updated', 'ok'); closeModal(); refreshNow();
};

// ------------------------------------------------------------------ history
async function historyTab(id, status) {
  const st = status || 'Completed,Failed,Cancelled';
  const [list, schedules] = await Promise.all([get(`/api/agents/${enc(id)}/tasks?status=${enc(st)}&limit=150`), get(`/api/agents/${enc(id)}/schedules`).catch(() => [])]);
  const opt = (v, label) => `<option value="${v}"${v === status ? ' selected' : ''}>${label}</option>`;
  return card('Finished tasks', `<form class="filters" data-form="history-filter" data-id="${esc(id)}"><label>Status<select name="status">${opt('', 'All finished')}${opt('Completed', 'Completed')}${opt('Failed', 'Failed')}${opt('Cancelled', 'Cancelled')}</select></label><button class="btn">Apply</button></form>
    <div class="section"></div>${taskTable(list, { agent: true, empty: 'No finished tasks yet.' })}`) +
    card('Schedules', table([
      { h: 'Schedule', f: s => esc(s.scheduleId) }, { h: 'Enabled', f: s => s.enabled ? badge('yes', 'ok') : badge('no', '') },
      { h: 'Last run', f: s => esc(ago(s.lastRunUtc)) }, { h: 'Next run', f: s => esc(ts(s.nextRunUtc)) },
      { h: 'Definition', cls: 'clip', f: s => `<code data-tip="${esc(JSON.stringify(s.definition))}">${esc(JSON.stringify(s.definition))}</code>` }], schedules, { empty: 'No schedules.' }), { flush: true });
}
FORMS['history-filter'] = (f, d) => navigate(`/agents/${enc(f.dataset.id)}/history${d.status ? '?status=' + enc(d.status) : ''}`);

// ------------------------------------------------------------------ subagents
async function subagentsTab(id, prof) {
  const list = await get(`/api/agents/${enc(id)}/subagents?limit=200`);
  const running = list.filter(s => s.status === 'Running');
  const spend = list.reduce((s, x) => s + (x.spentMicros || 0), 0);
  return `<div class="grid kpis">${kpi('Running', `${num(running.length)}<span class="muted small"> / ${num(prof.maxSubagents)}</span>`, 'concurrent limit')}
      ${kpi('Total (recent)', num(list.length))}${kpi('Cloud spend', money(spend, 2), 'charged to this agent')}
      ${kpi('Default cap', prof.subagentDefaultSpendCap !== null && prof.subagentDefaultSpendCap !== undefined ? usd(prof.subagentDefaultSpendCap) : 'host default', 'per subagent')}</div>
    <div class="notice info section">The agent spawns subagents itself when splitting work helps (tool <code>spawn_subagent</code>). Subagents use this agent's models; local-model work is free and any cloud use is charged to this agent's wallet, capped per subagent. They end when the task that spawned them ends.</div>
    ${card('Subagents', subagentTable(list), { flush: true, cls: 'section' })}`;
}

// ------------------------------------------------------------------ model access (grants)
async function modelAccessTab(id) {
  const acc = await get(`/api/agents/${enc(id)}/model-access`);
  const profileTable = table([
    { h: 'Model', f: x => `<code>${esc(x.model.key)}</code><div class="cell-sub">${esc(x.model.modelId)}</div>` }, { h: 'Tier', num: true, f: x => x.model.tier },
    { h: 'Price in/out (per 1M)', f: x => x.model.isLocal ? badge('local · free', 'ok', false) : (x.model.priced ? `$${x.model.inputPerMillion} / $${x.model.outputPerMillion}` : badge('unpriced', 'warn', false)) },
    { h: 'Usable', f: x => x.usable ? badge('yes', 'ok') : badge(x.aboveMaxTier ? 'above max tier' : 'no', 'warn') }
  ], acc.profileModels, { empty: 'No models in the profile.' });
  const grantsTable = table([
    { h: 'Model', f: g => `<code>${esc(g.grant.modelKey)}</code> ${g.model && g.model.isCustom ? tag('custom', 'accent') : ''}<div class="cell-sub">${esc(g.model ? g.model.modelId : 'removed')}</div>` },
    { h: 'Use for', cls: 'clip', f: g => `<span data-tip="${esc(g.grant.useFor)}">${esc(g.grant.useFor)}</span>` },
    { h: 'Subagents', f: g => g.grant.allowSubagents ? badge('allowed', 'ok', false) : '<span class="muted">no</span>' },
    { h: 'Price in/out', f: g => g.model && g.model.priced ? `$${g.model.inputPerMillion} / $${g.model.outputPerMillion}` : badge('unpriced', 'warn', false) },
    { h: 'Status', f: g => g.usable ? badge('active', 'ok') : badge('unavailable', 'warn') },
    { h: 'Granted', f: g => `${esc(g.grant.grantedBy)}<div class="cell-sub">${esc(ago(g.grant.grantedUtc))}</div>` },
    { h: '', cls: 'act', f: g => `<button class="btn xs" data-act="edit-grant" data-id="${esc(id)}" data-key="${esc(g.grant.modelKey)}" data-use="${esc(g.grant.useFor)}" data-sub="${g.grant.allowSubagents ? 1 : 0}">Edit</button> <button class="btn xs danger" data-act="revoke-grant" data-id="${esc(id)}" data-key="${esc(g.grant.modelKey)}">Revoke</button>` }
  ], acc.grants, { empty: 'No additional models granted', emptyText: 'Grant a model below so this agent can use it for a specific purpose.' });
  const grantForm = acc.grantable.length ? `<form id="agent-grant-form" class="form-grid" data-form="agent-grant" data-id="${esc(id)}">
      <label>Model<select name="modelKey" data-change="grant-model-default">${acc.grantable.map(m => `<option value="${esc(m.key)}" data-use="${esc(m.useFor || '')}">${esc(m.key)}${m.isCustom ? ' (custom)' : ''} · ${m.priced ? `$${m.inputPerMillion}/$${m.outputPerMillion}` : 'unpriced'}</option>`).join('')}</select></label>
      <label class="span2">Use this model for<input name="useFor" required minlength="3" maxlength="1000" value="${esc(acc.grantable[0].useFor || '')}" placeholder="e.g. reviewing legal contracts"><span class="help">Shown to the agent so it knows when to use the model</span></label>
      <label class="check"><input type="checkbox" name="allowSubagents" value="1"> Subagents may use it (charged to this agent)</label>
      <div><button class="btn primary">Grant access</button></div></form>` : '<p class="muted">Every enabled cloud model is already available to this agent.</p>';
  return `${card('Additional models (granted by administrators)', grantsTable, { flush: true, hint: 'Only agents with a grant can use a custom model. Revoking takes effect on the next AI call.' })}
    ${acc.isSubagent ? '' : card('Grant another model', grantForm, { cls: 'section' })}
    ${card('Models from the agent profile', profileTable, { flush: true, hint: `max tier ${acc.maxTier}` })}
    ${card('Grant history', table([{ h: 'Model', f: g => `<code>${esc(g.modelKey)}</code>` }, { h: 'Use for', cls: 'clip', f: g => esc(g.useFor) },
      { h: 'Granted', f: g => `${esc(g.grantedBy)} · ${esc(ts(g.grantedUtc))}` }, { h: 'Revoked', f: g => g.revokedUtc ? `${esc(g.revokedBy)} · ${esc(ts(g.revokedUtc))}` : badge('active', 'ok') }], acc.history, { empty: 'No grants yet.' }), { flush: true })}`;
}
CHANGES['grant-model-default'] = el => { const use = el.selectedOptions[0] && el.selectedOptions[0].dataset.use; const input = $('input[name=useFor]', el.closest('form')); if (use && input && !input.value) input.value = use; };
FORMS['agent-grant'] = async (f, d) => {
  await put(`/api/agents/${enc(f.dataset.id)}/model-grants/${enc(d.modelKey)}`, { useFor: d.useFor, allowSubagents: d.allowSubagents === '1' });
  toast(`${d.modelKey} granted`, 'ok'); refreshNow();
};
ACTIONS['edit-grant'] = el => {
  openModal(`Grant: ${el.dataset.key} → ${el.dataset.id}`, `<form id="edit-grant-form" class="form-grid" data-form="agent-grant" data-id="${esc(el.dataset.id)}">
    <input type="hidden" name="modelKey" value="${esc(el.dataset.key)}">
    <label class="full">Use this model for<input name="useFor" required minlength="3" maxlength="1000" value="${esc(el.dataset.use)}"></label>
    <label class="check full"><input type="checkbox" name="allowSubagents" value="1"${el.dataset.sub === '1' ? ' checked' : ''}> Subagents may use it (charged to this agent)</label></form>`,
    `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="submit-form" data-form-id="edit-grant-form">Save</button>`);
};

// ------------------------------------------------------------------ memory, messages, comms, tools, AI, budget, activity, artifacts
async function memoryTab(id, q) {
  const m = await get(`/api/agents/${enc(id)}/memory?limit=200${q ? '&q=' + enc(q) : ''}`);
  return card('Private memory', `<form class="filters" data-form="memory-search" data-id="${esc(id)}"><label class="grow">Search<input name="q" value="${esc(q)}" placeholder="keywords"></label><button class="btn">Search</button></form>
    <p class="muted small section">${num(m.total)} memories. Private to this agent — other agents can never read them; administrators can view and delete them here.</p>
    ${table([
      { h: 'Category', f: x => tag(x.category) }, { h: 'Content', f: x => esc(x.content) + (x.tags ? `<div class="cell-sub">${esc(x.tags)}</div>` : '') },
      { h: 'Imp.', num: true, f: x => x.importance }, { h: 'Created', f: x => esc(ago(x.createdUtc)) },
      { h: '', cls: 'act', f: x => `<button class="btn xs danger" data-act="delete-memory" data-id="${esc(id)}" data-mem="${esc(x.id)}">Delete</button>` }], m.items, { empty: 'No memories.' })}`);
}
FORMS['memory-search'] = (f, data) => navigate(`/agents/${enc(f.dataset.id)}/memory${data.q ? '?q=' + enc(data.q) : ''}`);
ACTIONS['delete-memory'] = async el => {
  if (!await confirmBox('Delete memory', 'Delete this memory permanently?', { danger: true, confirm: 'Delete' })) return;
  await del(`/api/agents/${enc(el.dataset.id)}/memory/${enc(el.dataset.mem)}`); toast('Memory deleted', 'ok'); refreshNow();
};
async function messagesTab(id) {
  const [inbox, outbox] = await Promise.all([get(`/api/agents/${enc(id)}/messages?direction=in&limit=100`), get(`/api/agents/${enc(id)}/messages?direction=out&limit=100`)]);
  return card('Incoming', messageTable(inbox), { flush: true }) + card('Outgoing', messageTable(outbox), { flush: true });
}
async function commsTab(id, profile) {
  const c = await get(`/api/agents/${enc(id)}/communications`);
  const pc = profile.communication || {};
  return `<div class="grid two">${card('Permissions', `<dl class="kv">
      <dt>May send to</dt><dd>${chips(c.allowedRecipients)}</dd><dt>May receive from</dt><dd>${chips(c.canReceiveFrom)}</dd>
      <dt>Send types</dt><dd>${chips(pc.sendTypes)}</dd><dt>Receive types</dt><dd>${chips(pc.receiveTypes)}</dd>
      <dt>Max priority</dt><dd>${esc(pc.maxSendPriority)}</dd><dt>Broadcast</dt><dd>${pc.canBroadcast ? 'groups: ' + chips(pc.groups) : 'not allowed'}</dd>
      <dt>Rate limits</dt><dd class="small">${pc.maxMessagesPerMinute} msg/min · ${pc.maxRequestsPerMinute} req/min · ${pc.maxDelegationsPerHour} delegations/h · ${pc.maxOutstandingRequests} outstanding</dd>
    </dl><p class="muted small section">Permissions come from the agent definition and only administrators can change them.</p>`)}
    ${card('Traffic (7 days)', table([{ h: 'From', f: e => esc(e.from) }, { h: 'To', f: e => esc(e.to) }, { h: 'Messages', num: true, f: e => num(e.count) }, { h: 'Failed', num: true, f: e => num(e.failed) }], c.traffic7d, { empty: 'No traffic.' }), { flush: true })}</div>
    ${card('Contacts as the agent sees them', table([{ h: 'Agent', f: x => agentLink(x.id) }, { h: 'Role', f: x => esc(x.role) }, { h: 'Status', f: x => tag(x.status) }], c.contacts, { empty: 'This agent cannot contact anyone.' }), { flush: true, cls: 'section' })}`;
}
async function toolsTab(id) {
  const tools = await get(`/api/agents/${enc(id)}/tools`);
  return card('Tools the model sees', table([
    { h: 'Tool', f: t => `<code>${esc(t.name)}</code>` }, { h: 'Source', f: t => tag(t.source) }, { h: 'Description', f: t => esc(t.description) },
    { h: 'Flags', f: t => (t.requiresApproval ? badge('approval', 'warn', false) + ' ' : '') + (t.idempotent ? tag('idempotent') : '') },
    { h: '', f: t => `<details><summary class="muted small">schema</summary>${json(t.parameters)}</details>` }], tools), { flush: true, hint: 'Built from this agent\'s permissions; disconnected MCP servers are omitted.' });
}
async function aiTab(id) {
  const u = await get(`/api/agents/${enc(id)}/ai-usage?limit=100`);
  const usage = rows => table([{ h: 'Model', f: r => `<code>${esc(r.modelKey)}</code>` }, { h: 'Calls', num: true, f: r => num(r.calls) }, { h: 'Failures', num: true, f: r => num(r.failures) },
    { h: 'Tokens', num: true, f: r => num(r.promptTokens + r.completionTokens) }, { h: 'Cost', num: true, f: r => money(r.costMicros) }, { h: 'Avg latency', num: true, f: r => dur(Math.round(r.avgDurationMs)) },
    { h: 'Spill / Esc / Fallback', num: true, f: r => `${r.spillovers} / ${r.escalations} / ${r.fallbacks}` }], rows, { empty: 'No usage.' });
  return `<div class="grid two">${card('Last 24 hours', usage(u.last24h), { flush: true })}${card('Last 7 days', usage(u.last7d), { flush: true })}</div>
    ${card('Recent AI calls', aiCallsTable(u.recent), { flush: true, cls: 'section', hint: '* token usage estimated (provider reported none or implausible usage)' })}`;
}
async function budgetTab(id, isSub, a) {
  if (isSub) return card('', `<p>Subagents have no wallet of their own; their cloud use is charged to ${agentLink(a.parentAgentId)}.</p>`);
  const l = await get(`/api/agents/${enc(id)}/ledger?limit=200`);
  const w = l.wallet || {};
  const limit = w.dailyLimitMicros;
  return `<div class="grid kpis">${kpi('Available', money(w.availableMicros, 2), `reserved ${money(w.reservedMicros)}`)}
    ${kpi('Spent today', money(w.spentTodayMicros, 2), limit ? `of ${money(limit, 2)} daily limit` : 'no daily limit', { bar: limit ? meter(w.spentTodayMicros / limit, 'spent today / daily limit', w.spentTodayMicros > limit * 0.8 ? 'warn' : '') : '' })}
    ${kpi('Lifetime spend', money(w.lifetimeSpendMicros, 2))}${kpi('Profit credited', money(w.lifetimeProfitMicros, 2))}
    ${kpi('Net', money(w.netMicros, 2), 'profit − AI spend')}${kpi('ROI', w.roi === null || w.roi === undefined ? '—' : w.roi.toFixed(2) + '×', 'profit / spend')}
    ${kpi('Runway', w.runwayDays === null || w.runwayDays === undefined ? '—' : runway(w.runwayDays, w.lowRunway),
      w.burnRateMicrosPerDay ? `at ${money(w.burnRateMicrosPerDay, 2)}/day recent spend` : 'no recent cloud spend')}</div>
    ${w.lowRunway ? `<div class="notice warn section"><b>Budget running out.</b> At its recent spending rate this agent's balance lasts about ${esc(runwayInfo(w.runwayDays, true).text)}. Its prompts now tell it to prefer the free local model and avoid paid escalation.</div>` : ''}
    ${card('Ledger', ledgerTable(l.entries), {
      flush: true, cls: 'section', export: 'ledger-' + id, actions: `<button class="btn small primary" data-act="record-profit" data-id="${esc(id)}">Record profit</button>
      <button class="btn small" data-act="adjust-balance" data-id="${esc(id)}">Adjust balance</button>
      <button class="btn small" data-act="daily-limit" data-id="${esc(id)}" data-current="${limit ? limit / 1e6 : ''}">Daily limit</button>`
    })}`;
}
ACTIONS['daily-limit'] = el => {
  openModal('Daily spend limit for ' + el.dataset.id, `<form id="limit-form" class="form-grid" data-form="daily-limit" data-id="${esc(el.dataset.id)}">
    <label>Limit (USD per day, empty = none)<input name="amount" type="number" step="0.01" min="0" value="${esc(el.dataset.current || '')}"></label></form>
    <p class="muted small section">Applies to this agent's own cloud use and its subagents' cloud use.</p>`,
    `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="submit-form" data-form-id="limit-form">Save</button>`, 'narrow');
};
FORMS['daily-limit'] = async (f, data) => { await post(`/api/agents/${enc(f.dataset.id)}/daily-limit`, { amount: data.amount === '' ? null : Number(data.amount) }); toast('Limit saved', 'ok'); closeModal(); refreshNow(); };
async function activityTab(id) {
  const list = await get(`/api/agents/${enc(id)}/activity?limit=300`);
  return card('Activity (persisted)', table([
    { h: 'When', f: e => `<span data-tip="${esc(ts(e.createdUtc))}">${esc(time(e.createdUtc))}</span>` }, { h: 'Event', f: e => tag(e.eventType) },
    { h: 'Summary', f: e => esc(e.summary) }, { h: 'Task', f: e => e.taskId ? `<a href="#" data-act="open-task" data-id="${esc(id)}" data-task="${esc(e.taskId)}">open</a>` : '' },
    { h: 'Workflow', f: e => e.correlationId ? link('/correlations/' + enc(e.correlationId), 'tree') : '' }], list, { empty: 'No activity yet.' }), { flush: true });
}
async function artifactsTab(id) {
  const list = await get(`/api/agents/${enc(id)}/artifacts`);
  return card('Artifacts', table([
    { h: 'Name', f: x => `<a href="/api/artifacts/${esc(x.id)}/content" target="_blank" rel="noopener">${esc(x.name)}</a>` }, { h: 'Type', f: x => esc(x.mimeType) },
    { h: 'Size', num: true, f: x => num(Math.max(1, Math.round(x.sizeBytes / 1024))) + ' KB' }, { h: 'Description', cls: 'clip', f: x => esc(x.description || '') },
    { h: 'Created', f: x => esc(ago(x.createdUtc)) }], list, { empty: 'No artifacts.' }), { flush: true, hint: 'Files a finished subagent saved are handed to its parent.' });
}

// ------------------------------------------------------------------ settings
async function settingsTab(id) {
  const d = await get(`/api/agents/${enc(id)}/definition`);
  if (!d.editable) {
    return card('Definition', `<div class="notice">This agent is defined in <code>appsettings.json</code>. Edit it there and restart the host, or clone it into a dashboard-managed agent you can edit here. Runtime controls, the queue, budget and model grants can be managed from the dashboard.</div>
      <div class="row section">${link('/agents/new?clone=' + enc(id), 'Clone into a new agent', 'btn primary')}</div><h3 class="section">Overrides (as configured)</h3>${json(d.profile)}`);
  }
  return card('Definition', `<p>This agent is managed from the dashboard.</p><div class="row section">${link('/agents/' + enc(id) + '/edit', 'Edit agent', 'btn primary')}
      ${link('/agents/new?clone=' + enc(id), 'Clone', 'btn')}<span class="spacer"></span><button class="btn danger" data-act="delete-agent" data-id="${esc(id)}">Delete agent</button></div>
      <h3 class="section">Overrides (stored)</h3>${json(d.profile)}`);
}
ACTIONS['delete-agent'] = async el => {
  const id = el.dataset.id;
  const typed = await confirmBox('Delete agent', `Delete <b>${esc(id)}</b>? Its runtime stops, running subagents are cancelled and its model grants are revoked. History, ledger and memories are kept. Type the agent id to confirm.`,
    { danger: true, confirm: 'Delete agent', input: 'Agent id' });
  if (typeof typed !== 'string' || typed.trim() !== id) { if (typed !== null && typed !== false) toast('Agent id did not match.', 'bad'); return; }
  await del('/api/agents/' + enc(id)); toast('Agent deleted', 'ok'); navigate('/agents');
};

// ------------------------------------------------------------------ message an agent
ACTIONS['message-agent'] = el => {
  openModal('Message ' + el.dataset.id, `<form id="msg-form" class="form-grid" data-form="message-agent" data-id="${esc(el.dataset.id)}">
    <label>Type<select name="type"><option value="Information">Information (note, stored in memory)</option><option value="Request">Request (agent works on it)</option><option value="StatusRequest">Status request</option></select></label>
    <label class="span2">Subject<input name="subject" maxlength="300"></label><label class="full">Content<textarea name="content" rows="5"></textarea></label></form>`,
    `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="submit-form" data-form-id="msg-form">Send</button>`);
};
FORMS['message-agent'] = async (f, data) => { await post(`/api/agents/${enc(f.dataset.id)}/message`, data); toast('Message sent', 'ok'); closeModal(); };
