'use strict';
/* Agentic console - global tasks: describe a goal once; the host plans it, distributes the steps across agents, runs
   them (passing results between dependent steps) and combines the results into one final answer. */

const GT_STATUS = { Scheduled: 'outline', Planning: 'info', AwaitingApproval: 'warn', Running: 'info', Synthesizing: 'info', Reviewing: 'info', Completed: 'ok', Failed: 'bad', Cancelled: '' };
const GT_LABEL = { AwaitingApproval: 'Awaiting approval', Synthesizing: 'Writing final answer', Reviewing: 'Being reviewed' };
function gtBadge(status) { return badge(GT_LABEL[status] || status, GT_STATUS[status] !== undefined ? GT_STATUS[status] : ''); }
function gtProgress(g) { return g.stepsTotal ? meter(g.stepsDone / g.stepsTotal, `${g.stepsDone} of ${g.stepsTotal} steps finished`) : ''; }

VIEWS.globalTasks = {
  live: true,
  async render() {
    setCrumbs([['Global tasks', '/global-tasks']]);
    const d = await get('/api/global-tasks?limit=150');
    const t = d.tasks, active = t.filter(g => ['Planning', 'Running', 'Synthesizing', 'Reviewing'].includes(g.status)).length;
    const awaiting = t.filter(g => g.status === 'AwaitingApproval').length;
    const done = t.filter(g => g.status === 'Completed').length;
    const pill = $('#nav-global'); if (pill) { pill.textContent = active + awaiting; pill.classList.toggle('hidden', active + awaiting === 0); }
    const rows = table([
      { h: 'Global task', cls: 'clip', f: g => `<a href="/global-tasks/${enc(g.id)}" data-nav class="cell-title">${esc(g.name)}</a><div class="cell-sub">${esc((g.understanding || g.description || '').slice(0, 140))}</div>` },
      { h: 'Status', cls: 'nw', f: g => gtBadge(g.status) + (g.scheduleId ? ' ' + tag('recurring', 'accent') : '') },
      { h: 'Progress', f: g => g.stepsTotal ? `${gtProgress(g)}<div class="cell-sub">${g.stepsDone}/${g.stepsTotal} steps</div>` : '<span class="muted">planning…</span>' },
      { h: 'Agents', f: g => g.agents.length ? chips(g.agents, 'info') : '<span class="muted">—</span>' },
      { h: 'Created', cls: 'nw', f: g => `<span data-tip="${esc(ts(g.createdUtc))}">${esc(ago(g.createdUtc))}</span><div class="cell-sub">by ${esc(g.createdBy)}</div>` },
      { h: '', cls: 'act', f: g => `${g.status === 'AwaitingApproval' ? `<button class="btn xs primary" data-act="global-op" data-op="approve" data-id="${esc(g.id)}">Approve</button> ` : ''}` +
          `${['Planning', 'AwaitingApproval', 'Running', 'Synthesizing', 'Reviewing', 'Scheduled'].includes(g.status) ? `<button class="btn xs danger" data-act="global-op" data-op="cancel" data-id="${esc(g.id)}">Cancel</button> ` : ''}` +
          link('/global-tasks/' + enc(g.id), 'Open', 'btn xs') }
    ], t, { empty: 'No global tasks yet', emptyText: 'Use "Global task" to describe a goal; the system plans it and distributes the work across your agents.',
      rowAttrs: g => `data-state="${esc(g.status)}"`, rowClass: g => ['Planning', 'Running', 'Synthesizing', 'Reviewing'].includes(g.status) ? 'is-running' : '' });
    const schedules = table([
      { h: 'Recurring global task', f: s => `<span class="cell-title">${esc(s.name)}</span><div class="cell-sub">${esc(s.summary || '')}</div>` },
      { h: 'Status', f: s => s.enabled ? badge('Active', 'ok') : badge(s.runCount && !s.nextRunUtc ? 'Finished' : 'Paused', 'warn') },
      { h: 'Next run', cls: 'nw', f: s => s.enabled && s.nextRunUtc ? `<span data-tip="${esc(ts(s.nextRunUtc))}">${esc(ago(s.nextRunUtc))}</span>` : '<span class="muted">—</span>' },
      { h: 'Last run', cls: 'nw', f: s => s.lastGlobalTaskId ? `${link('/global-tasks/' + enc(s.lastGlobalTaskId), ago(s.lastRunUtc))}` : '<span class="muted">never</span>' },
      { h: 'Runs', num: true, f: s => num(s.runCount) },
      { h: '', cls: 'act', f: s => `<button class="btn xs" data-act="global-schedule-op" data-op="run" data-id="${esc(s.id)}">Run now</button> ` +
          `<button class="btn xs" data-act="global-schedule-op" data-op="${s.enabled ? 'pause' : 'resume'}" data-id="${esc(s.id)}">${s.enabled ? 'Pause' : 'Resume'}</button> ` +
          `<button class="btn xs danger" data-act="global-schedule-op" data-op="delete" data-id="${esc(s.id)}">Delete</button>` }
    ], d.schedules, { empty: 'No recurring global tasks.' });
    return pageHead('Global tasks', {
      subtitle: 'Describe a goal once: the system works out what is needed, splits it across the right agents, runs the steps and combines the results into one final answer.',
      actions: `<button class="btn primary" data-act="new-global-task">${icon('plus')} New global task</button>`
    }) + (d.enabled ? '' : '<div class="notice warn">Global tasks are disabled (GlobalTasks.Enabled in appsettings.json).</div><div class="section"></div>') +
      `<div class="grid kpis">${kpi('In progress', num(active), 'planning, running or combining')}${kpi('Awaiting approval', num(awaiting), 'plans for you to review')}
        ${kpi('Completed', num(done), 'with a final answer')}${kpi('Recurring', num(d.schedules.length), 'scheduled global tasks')}</div>` +
      card('Global tasks', rows, { flush: true, cls: 'section', export: 'global-tasks' }) +
      card('Recurring global tasks', schedules, { flush: true, cls: 'section', hint: 'Each run creates a new global task that is planned from scratch.' });
  }
};

/** What a step's task did: model turns, tokens and time, tool calls (repeats flagged when they are many), pauses, failed calls. */
function gtStatsLine(st) {
  if (!st || (!st.modelCalls && !st.toolCalls)) return '';
  const tok = n => n >= 1e6 ? (n / 1e6).toFixed(1) + 'M' : n >= 1e3 ? Math.round(n / 1e3) + 'K' : String(n);
  const many = st.toolCalls >= 20 && st.repeatedToolCalls / st.toolCalls >= 0.3;
  return `<div class="gt-stats small muted">${num(st.modelCalls)} model turns · ${tok(st.promptTokens + st.completionTokens)} tokens · ${esc(dur(st.modelSeconds * 1000))} on the model · ` +
    `${num(st.toolCalls)} tool calls${st.repeatedToolCalls ? ` <span class="${many ? 'warn-text' : ''}" data-tip="Calls with the same tool and arguments as an earlier one (re-reads, re-runs)">(${num(st.repeatedToolCalls)} repeated)</span>` : ''}` +
    `${st.resumes ? ` · paused and resumed ${num(st.resumes)}×` : ''}${st.failedModelCalls ? ` · ${num(st.failedModelCalls)} failed model calls` : ''}</div>`;
}
/** The steps of earlier attempts (revisions the reviewer sent back), newest first, folded. */
function gtHistoryHtml(g) {
  if (!g.history || !g.history.length) return '';
  return card('Earlier attempts', g.history.slice().reverse().map(h => {
    const done = h.steps.filter(s => s.status === 'Completed').length;
    return `<details class="gt-history"><summary>Revision ${num(h.revision)}: ${done} of ${h.steps.length} steps completed</summary>
      <div class="gt-steps">${h.steps.map((s, i) => `<div class="gt-step s-${esc(s.status)}"><span class="num">${i + 1}</span><div class="stack">
        <div class="row"><b>${esc(s.title)}</b> ${agentLink(s.agentId)} ${badge(s.status)}${s.startedUtc && s.completedUtc ? ` <span class="muted small">took ${esc(dur(new Date(s.completedUtc) - new Date(s.startedUtc)))}</span>` : ''}</div>
        ${gtStatsLine(s.stats)}
        ${s.result ? `<details><summary class="small">Result</summary><pre>${jsonOr(s.result)}</pre></details>` : ''}
        ${s.error ? `<div class="error-text small">${esc(s.error)}</div>` : ''}</div>
        <div>${s.taskId ? `<button class="btn xs" data-act="open-task" data-id="${esc(s.agentId)}" data-task="${esc(s.taskId)}">Task</button>` : ''}</div></div>`).join('')}</div></details>`;
  }).join(''), { cls: 'section' });
}

VIEWS.globalTask = {
  live: true,
  async render(p) {
    const g = await get('/api/global-tasks/' + enc(p.args[0]));
    setCrumbs([['Global tasks', '/global-tasks'], [g.name, '']]);
    const stats = g.stats || {};
    const byId = Object.fromEntries(g.steps.map((s, i) => [s.stepId, i + 1]));
    const steps = g.steps.map((s, i) => `<div class="gt-step s-${esc(s.status)}" data-key="${esc(s.stepId)}" data-state="${esc(s.status)}">
        <span class="num">${i + 1}</span>
        <div class="stack">
          <div class="row"><b>${esc(s.title)}</b> ${agentLink(s.agentId)} ${badge(s.status === 'Waiting' && s.dependsOn.length ? 'Waiting for ' + s.dependsOn.map(d => '#' + (byId[d] || d)).join(', ') : s.status, s.status === 'Waiting' ? 'outline' : undefined)}
            ${s.startedUtc ? `<span class="muted small">started ${esc(ago(s.startedUtc))}${s.completedUtc ? ' · took ' + esc(dur(new Date(s.completedUtc) - new Date(s.startedUtc))) : ''}</span>` : ''}</div>
          ${gtStatsLine(stats[s.stepId])}
          <details><summary class="small">Instructions</summary><pre>${esc(s.instructions)}</pre></details>
          ${s.result ? `<details${g.steps.length <= 3 ? ' open' : ''}><summary class="small">Result</summary><pre>${jsonOr(s.result)}</pre></details>` : ''}
          ${s.error ? `<div class="error-text small">${esc(s.error)}</div>` : ''}
        </div>
        <div>${s.taskId ? `<button class="btn xs" data-act="open-task" data-id="${esc(s.agentId)}" data-task="${esc(s.taskId)}">Task</button>` : ''}</div>
      </div>`).join('');
    let actions = (g.status === 'AwaitingApproval' ? `<button class="btn primary" data-act="global-op" data-op="approve" data-id="${esc(g.id)}">Approve plan &amp; start</button>` : '') +
      (['AwaitingApproval', 'Failed'].includes(g.status) ? `<button class="btn" data-act="global-replan" data-id="${esc(g.id)}">Re-plan…</button>` : '') +
      (['Planning', 'AwaitingApproval', 'Running', 'Synthesizing', 'Reviewing', 'Scheduled'].includes(g.status) ? `<button class="btn danger" data-act="global-op" data-op="cancel" data-id="${esc(g.id)}">Cancel</button>` : '') +
      link('/correlations/' + enc(g.correlationId), 'Workflow timeline', 'btn') + discussButton(`data-global="${esc(g.id)}"`);
    const o = g.options || {};
    const rv = g.review;
    const reviewCard = (o.reviewerAgentId || rv) ? card('Review', `<dl class="kv">
        <dt>Checked by</dt><dd>${o.reviewerAgentId ? agentLink(o.reviewerAgentId) : '—'}${g.status === 'Reviewing' ? ' ' + badge('Reviewing') : ''}</dd>
        <dt>Revisions</dt><dd>${num(g.revision || 0)} of ${num(o.maxRevisions || 0)} allowed</dd>
        ${rv ? `<dt>Verdict</dt><dd>${badge(rv.verdict)}${rv.score ? ` <b>${rv.score}/10</b>` : ''}</dd>${rv.feedback ? `<dt>Feedback</dt><dd><div class="md">${markdown(rv.feedback)}</div></dd>` : ''}` : ''}
        ${o.reviewCriteria ? `<dt>Expected outcome</dt><dd><span class="badge warn">reviewer only</span><pre class="gt-criteria">${esc(o.reviewCriteria)}</pre></dd>` : ''}
        ${o.attachments && o.attachments.length ? `<dt>Attached files</dt><dd>${o.attachments.map(a => `<div>${esc(a.name)} <span class="muted small">${esc(a.summary || '')}</span></div>`).join('')}</dd>` : ''}
      </dl>`, { cls: 'section' }) : '';
    if (g.resultId) actions = link('/results/' + enc(g.resultId), 'Open result', 'btn primary') + actions;
    return pageHead(g.name, { badges: gtBadge(g.status) + (g.scheduleId ? ' ' + tag('recurring', 'accent') : '') + (g.revision ? ' ' + tag('revision ' + g.revision, 'warn') : ''), subtitle: `Created ${esc(ago(g.createdUtc))} by ${esc(g.createdBy)}${g.completedUtc ? ' · finished ' + esc(ago(g.completedUtc)) : ''}`, actions }) +
      (g.status === 'AwaitingApproval' ? '<div class="notice warn">The plan below waits for your approval. Approve it to start the agents, or re-plan with feedback.</div><div class="section"></div>' : '') +
      (g.error ? `<div class="notice ${g.status === 'Completed' ? 'warn' : 'bad'}">${esc(g.error)}</div><div class="section"></div>` : '') +
      (g.finalResult ? card('Final answer', `<div class="final-answer md">${markdown(g.finalResult)}</div>`, { actions: `<button class="btn xs ghost" data-act="copy-final" data-id="${esc(g.id)}">Copy</button>` }) : '') +
      reviewCard +
      `<div class="grid side section">
        ${card('Plan', (g.understanding ? `<p><b>Understanding.</b> ${esc(g.understanding)}</p>` : (g.status === 'Planning' ? '<div class="notice info">The coordinator is reading the request and choosing agents…</div>' : '')) +
          (steps ? `<div class="gt-steps section">${steps}</div>` : ''), { hint: g.approach ? `${esc(g.approach)} · planned by ${esc(g.plannerModel === 'rules' ? 'rules (no model available)' : g.plannerModel || '')}` : '' })}
        ${card('Request', `<pre class="gt-request">${esc(g.description || g.name)}</pre>
          <dl class="kv gt-request-kv"><dt>Progress</dt><dd>${g.stepsTotal ? `${gtProgress(g)}<div class="small muted">${g.stepsDone}/${g.stepsTotal} steps</div>` : '—'}</dd>
            <dt>Priority</dt><dd>${esc(o.priority || 'Normal')}</dd><dt>Agents</dt><dd>${o.agentIds && o.agentIds.length ? chips(o.agentIds, 'info') : 'Any available agent'}${o.maxAgents ? ` · up to ${o.maxAgents}` : ''}</dd>
            <dt>Final answer by</dt><dd>${o.synthesisAgentId ? agentLink(o.synthesisAgentId) : 'The host (local model)'}</dd>
            <dt>Plan approval</dt><dd>${o.requirePlanApproval ? 'Required' : 'Not required'}</dd>
            ${o.deadlineMinutes ? runKv({ autoApproveTools: o.autoApproveTools, runPastDeadline: o.runPastDeadline, deadlinePassedUtc: g.deadlinePassedUtc,
              deadlineUtc: g.deadlineUtc || (g.startedUtc ? new Date(new Date(g.startedUtc).getTime() + o.deadlineMinutes * 60000).toISOString() : null),
              deadlineText: g.startedUtc ? null : g.revision && g.firstStartedUtc ? 'what is left of the first deadline (at least ' + dur(o.deadlineMinutes * 15000) + ')'
                : dur(o.deadlineMinutes * 60000) + ' after it starts' }) : runKv({ autoApproveTools: o.autoApproveTools })}
            ${g.feedback ? `<dt>Your feedback</dt><dd>${esc(g.feedback)}</dd>` : ''}</dl>`)}
      </div>` + gtHistoryHtml(g);
  }
};

ACTIONS['copy-final'] = async el => {
  const g = await get('/api/global-tasks/' + enc(el.dataset.id));
  try { await writeClipboardOrThrow(g.finalResult || ''); toast('Final answer copied', 'ok'); } catch { toast('Copy is not available in this browser.', 'bad'); }
};
ACTIONS['global-op'] = async el => {
  const op = el.dataset.op;
  if (op === 'cancel' && !await confirmBox('Cancel global task', 'Cancel this global task? Steps that are queued or running are cancelled too.', { danger: true, confirm: 'Cancel task' })) return;
  await post(`/api/global-tasks/${enc(el.dataset.id)}/${op}`);
  toast(op === 'approve' ? 'Plan approved — the agents are starting' : 'Global task cancelled', 'ok'); refreshNow();
};
ACTIONS['global-replan'] = el => {
  openModal('Plan again', `<form id="replan-form" class="stack" data-form="global-replan" data-id="${esc(el.dataset.id)}">
      <label>What should change? (optional)<textarea name="feedback" rows="4" placeholder="e.g. Use the Analyst for the comparison and keep the research to two sources."></textarea></label></form>`,
    `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="submit-form" data-form-id="replan-form">Plan again</button>`, 'narrow');
};
FORMS['global-replan'] = async (f, d) => { await post(`/api/global-tasks/${enc(f.dataset.id)}/replan`, { feedback: d.feedback || null }); toast('Planning again…', 'ok'); closeModal(); refreshNow(); };
ACTIONS['global-schedule-op'] = async el => {
  const base = '/api/global-schedules/' + enc(el.dataset.id), op = el.dataset.op;
  if (op === 'delete') {
    if (!await confirmBox('Delete recurring global task', 'Stop this recurring global task? Runs that already started are not affected.', { danger: true, confirm: 'Delete' })) return;
    await del(base); toast('Recurring global task deleted', 'ok');
  } else if (op === 'run') { await post(base + '/run'); toast('A new run starts in a few seconds', 'ok'); }
  else { await patch(base, { enabled: op === 'resume' }); toast(op === 'resume' ? 'Resumed' : 'Paused', 'ok'); }
  refreshNow();
};

ACTIONS['new-global-task'] = async () => {
  const agents = await get('/api/agents');
  const boxes = agents.map(a => `<label class="check"><input type="checkbox" name="agent" value="${esc(a.id)}"><span>${esc(a.name)}<span class="help">${esc(a.role || '')} · ${esc(a.state)}</span></span></label>`).join('');
  openModal('New global task', `<form id="global-form" class="form-grid" data-form="global-task">
      <div class="full" data-errors></div>
      <p class="full muted">Describe what you want. The coordinator reads the request and your agents' roles, tools and MCP access, decides which agents do which part,
        runs the steps (passing results between them) and writes one final answer.</p>
      <label class="full">Name<input name="name" required maxlength="200" placeholder="e.g. Competitor report on the top three CRM products"></label>
      <label class="full">What should be done?<textarea name="description" rows="7" required placeholder="The goal, all context, constraints and what the final result should look like."></textarea></label>
      <label>Priority<select name="priority"><option>Low</option><option selected>Normal</option><option>High</option><option>Critical</option></select></label>
      <label>Purpose<select name="purpose"><option value="">Each agent's default</option>${PURPOSES.map(x => `<option>${x}</option>`).join('')}</select><span class="help">Model routing for every step</span></label>
      ${startFields('Start')}
      <label>Max retries per step<input name="maxRetries" type="number" min="0" max="10" placeholder="agent default"></label>
      <label>Start at tier<select name="startTier"><option value="0">Default</option><option value="2">2</option><option value="3">3</option></select></label>
      <h3 class="full section">Distribution</h3>
      <div class="full row">
        <label class="check"><input type="radio" name="agentScope" value="any" checked data-change="gt-scope"> Let the system choose from all available agents</label>
        <label class="check"><input type="radio" name="agentScope" value="some" data-change="gt-scope"> Only these agents</label>
      </div>
      <div id="gt-agents" class="full check-grid disabled-area">${boxes}</div>
      <label>Max agents<input name="maxAgents" type="number" min="1" max="20" value="4"><span class="help">Upper limit; the plan uses as few as needed</span></label>
      <label>Final answer written by<select name="synthesisAgentId"><option value="">The host (combines all results)</option>${agents.map(a => `<option value="${esc(a.id)}">${esc(a.name)}</option>`).join('')}</select></label>
      <label>Deadline (hours)<input name="deadlineHours" type="number" min="0.1" step="any" value="4"><span class="help">Unfinished steps are then cancelled, unless they keep working past it (Run settings)</span></label>
      <label class="check full"><input type="checkbox" name="requirePlanApproval"> Let me review and approve the plan before any agent starts</label>
      ${runFields()}
      ${deliveryFields(agents)}
      <h3 class="full section">Recurrence</h3>
      ${repeatFields()}</form>`,
    `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="submit-form" data-form-id="global-form">${icon('layers')} Start global task</button>`, 'wide');
};
CHANGES['gt-scope'] = el => { $('#gt-agents').classList.toggle('disabled-area', el.value !== 'some'); };
FORMS['global-task'] = async (f, data) => {
  const fd = new FormData(f);
  const body = {
    name: data.name, description: data.description, priority: data.priority, purpose: data.purpose || null, startTier: Number(data.startTier || 0),
    maxAgents: Number(data.maxAgents || 4), synthesisAgentId: data.synthesisAgentId || null, requirePlanApproval: fd.get('requirePlanApproval') === 'on',
    deadlineMinutes: Math.round(Number(data.deadlineHours || 4) * 60), agentIds: data.agentScope === 'some' ? fd.getAll('agent') : []
  };
  if (data.agentScope === 'some' && body.agentIds.length === 0) throw new Error('Choose at least one agent, or let the system choose.');
  if (data.maxRetries !== '' && data.maxRetries !== undefined) body.maxRetries = Number(data.maxRetries);
  readStart(data, body);
  readDelivery(f, body);
  readRun(f, body);
  const repeat = readRepeat(f);
  if (repeat) body.repeat = repeat;
  const r = await post('/api/global-tasks', body);
  closeModal();
  if (r.scheduleId) { toast(`Recurring global task created: ${r.summary}`, 'ok'); navigate('/global-tasks'); }
  else { toast('Global task created — planning…', 'ok'); navigate('/global-tasks/' + enc(r.id)); }
};
