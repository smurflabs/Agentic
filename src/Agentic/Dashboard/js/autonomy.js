'use strict';
/* Agentic console - fully autonomous agents: an agent's Autonomy tab (its level and policy, decisions made without a
   person, team and hires, initiative, bounties, evaluation suite with rollback, mailboxes) and the Secrets page (the vault
   that tools use without the model ever seeing a value). */

const LEVEL_TEXT = {
  supervised: 'People decide everything it asks for.',
  assisted: 'Takes initiative and directs its own work; small decisions are automatic, people decide the rest.',
  autonomous: 'No person in the loop: its policy or its supervisor agent decides, and people are told.'
};
function levelBadge(level) {
  const l = level || 'supervised';
  return badge(l, l === 'autonomous' ? 'accent' : l === 'assisted' ? 'info' : 'outline');
}
function decidedByText(by) {
  if (!by || by === 'human') return 'a person';
  if (by === 'policy') return 'its policy';
  if (by === 'timeout') return 'time-out';
  if (by === 'host') return 'the host';
  return by.startsWith('agent:') ? by.slice(6) : by;
}
function verdictBadge(v) {
  if (!v) return '<span class="muted">—</span>';
  return badge(v, v === 'kept' || v === 'baseline' ? 'ok' : v === 'rolled-back' || v === 'worse' ? 'bad' : 'warn');
}

// ------------------------------------------------------------------ the agent's Autonomy tab
async function autonomyTab(id) {
  const [o, evals] = await Promise.all([get(`/api/agents/${enc(id)}/autonomy`), get(`/api/agents/${enc(id)}/evals`).catch(() => ({ cases: [], runs: [] }))]);
  const p = o.policy || {}, opts = o.options || {};
  const admin = can('admin'), manage = can('agents.manage');
  const usdMicros = v => v ? money(v, 2) : '<span class="muted">none</span>';
  const decided = Object.entries(o.decidedToday || {});
  const kpis = `<div class="grid kpis">
    ${kpi('Level', levelBadge(o.level), esc(LEVEL_TEXT[o.level] || ''))}
    ${kpi('Supervisor', o.supervisor ? agentLink(o.supervisor) : '<span class="muted">a person</span>', 'decides what its policy does not')}
    ${kpi('Decided today', num(decided.reduce((n, [, c]) => n + Number(c || 0), 0)), decided.map(([by, c]) => `${esc(decidedByText(by))}: ${num(c)}`).join(' · ') || 'nothing yet')}
    ${kpi('Waiting', num((o.waiting || []).length), 'requests not decided yet', { href: '/approvals' })}
    ${kpi('Team', num((o.manages || []).length), `${num((o.hires || []).filter(h => !h.retiredUtc).length)} hired`)}
  </div>`;
  const policy = card('Policy', `<dl class="kv">
      <dt>Budget approved at once</dt><dd>${usdMicros(p.autoApproveBudgetMicros)} a request, at most ${usdMicros(p.autoApproveBudgetPerDayMicros)} a day</dd>
      <dt>Tool approvals</dt><dd>${(p.autoApproveTools || []).includes('*') ? 'all approved by its policy' : (p.autoApproveTools || []).length ? chips(p.autoApproveTools) : '<span class="muted">wait for ' + (o.supervisor ? esc(o.supervisor) : 'a person') + '</span>'}</dd>
      <dt>Own behaviour changes</dt><dd>${p.autoApplyBehaviorChanges ? 'applied at once, then checked by its evaluation (rolled back if worse)' : 'need approval'}</dd>
      <dt>Hiring</dt><dd>${p.canHire ? `up to ${num(p.maxHires)} helper(s)${p.autoApproveHires ? ', approved at once' : ', each needs approval'}` : '<span class="muted">not allowed</span>'}</dd>
      <dt>Unanswered questions</dt><dd>${p.askHumanTimeoutMinutes ? `after ${num(p.askHumanTimeoutMinutes)} min: ${o.supervisor ? esc(o.supervisor) + ' answers, else ' : ''}it decides itself` : 'wait for a person'}</dd>
      <dt>Initiative</dt><dd>${p.initiativeMinutes && opts.initiative ? `after ${num(p.initiativeMinutes)} idle minutes it looks for the most useful work` : '<span class="muted">off</span>'}</dd>
      <dt>Self-check</dt><dd>${p.verifyPasses ? plural(p.verifyPasses, 'verification pass') + ' before a result counts' : '<span class="muted">none</span>'}</dd>
      <dt>Web APIs</dt><dd>${(p.allowedHttpHosts || []).length ? chips(p.allowedHttpHosts) : '<span class="muted">none</span>'}</dd>
      <dt>Knowledge bases</dt><dd>${(p.knowledgeBases || []).length ? chips(p.knowledgeBases) : '<span class="muted">none</span>'}</dd>
    </dl>${opts.policies === false ? '<div class="notice warn section">Policies are switched off on this server (Autonomy.PoliciesEnabled): every request waits for a person.</div>' : ''}`,
    { actions: manage ? link(`/agents/${enc(id)}/edit`, 'Change', 'btn small') : '' });
  const tools = card('Tools from its level', (o.tools || []).length ? chips(o.tools) : '<span class="muted">none</span>',
    { hint: 'On top of the tools in its profile' });
  const waiting = card('Waiting for a decision', table([
    { h: 'Request', f: w => `<span class="cell-title">${esc(w.title)}</span><div class="cell-sub">${esc(w.kind)} · ${esc(ago(w.createdUtc))}</div>` },
    { h: 'With', cls: 'nw', f: w => w.stage ? tag(w.stage.replace(/^supervisor:/, ''), 'info') : tag('a person', 'warn') },
    { h: 'Why', cls: 'clip', f: w => esc(w.reason || '') }
  ], o.waiting || [], { empty: 'Nothing is waiting.' }), { flush: true, actions: link('/approvals', 'Inbox', 'btn small') });
  const team = card('Team', `${(o.manages || []).length ? `<div class="row">${(o.manages || []).map(m => agentLink(m)).join(' ')}</div>` : '<p class="muted">It manages no agents.</p>'}
    ${(o.hires || []).length ? table([
      { h: 'Hired agent', f: h => h.retiredUtc ? `<span class="muted">${esc(h.agentId)}</span>` : agentLink(h.agentId) },
      { h: 'Why', cls: 'clip', f: h => esc(h.reason || '') },
      { h: 'Hired', cls: 'nw', f: h => esc(ago(h.createdUtc)) },
      { h: 'Status', cls: 'nw', f: h => h.retiredUtc ? `${badge('retired', 'outline')} <span class="muted small">${esc(h.retiredReason || '')}</span>` : badge('active', 'ok') }
    ], o.hires) : ''}`, { flush: !!(o.hires || []).length });
  const initiative = card('Initiative', table([
    { h: 'When', cls: 'nw', f: t => `<a href="#" data-act="open-task" data-id="${esc(id)}" data-task="${esc(t.taskId)}">${esc(ago(t.createdUtc))}</a>` },
    { h: 'Status', cls: 'nw', f: t => badge(t.status) },
    { h: 'What it decided', cls: 'clip', f: t => esc(t.summary || '') }
  ], o.initiatives || [], { empty: 'No initiative tasks yet.', emptyText: p.initiativeMinutes ? 'They come when it has been idle for a while.' : 'Initiative is off for this agent.' }), { flush: true });
  const bounties = card('Bounties earned', table([
    { h: 'When', cls: 'nw', f: b => esc(ago(b.createdUtc)) },
    { h: 'Amount', num: true, f: b => `<span class="${b.amountMicros < 0 ? 'error-text' : ''}">${money(b.amountMicros, 2)}</span>` },
    { h: 'For', cls: 'clip', f: b => esc(b.description || '') },
    { h: 'From / to', cls: 'nw', f: b => esc((b.reference || '').replace(/^from:/, 'from ').replace(/^to:/, 'to ')) }
  ], o.bounties || [], { empty: 'No bounties yet.', emptyText: opts.bounties ? 'Tasks can carry a bounty paid when the work is accepted.' : 'Bounties are off on this server.' }), { flush: true });
  return kpis + `<div class="grid two section">${policy}${waiting}</div>` + `<div class="section">${tools}</div>` +
    `<div class="grid two section">${team}${initiative}</div>` + `<div class="section">${bounties}</div>` +
    (admin ? await selfDirectionSections(id) : '') +
    `<div class="section">${evalSection(id, evals, manage, opts)}</div>` + `<div class="section">${mailboxSection(id, o.mailboxes || [], admin, opts)}</div>`;
}

/** What the agent set up for itself and what it learned to do: goals, behaviour changes, webhooks, skills (administrators). */
async function selfDirectionSections(id) {
  const [goals, props, hooks, skills] = await Promise.all([get('/api/goals?agent=' + enc(id)).catch(() => []), get('/api/proposals?agent=' + enc(id)).catch(() => []),
    get('/api/hooks?agent=' + enc(id)).catch(() => []), get('/api/skills?agent=' + enc(id)).catch(() => [])]);
  const setBy = by => !by ? '' : by.startsWith('agent:') ? tag('set by itself', 'info') : `<span class="muted small">by ${esc(by)}</span>`;
  const goalCard = card('Goals', table([
    { h: 'Goal', f: g => `<span class="cell-title">${esc(g.title)}</span> ${setBy(g.createdBy)}<div class="cell-sub clip">${esc(g.progress || g.detail || '')}</div>` },
    { h: 'Progress', cls: 'nw', f: g => `${meter((g.progressPct || 0) / 100, (g.progressPct || 0) + '%')} <span class="muted small">${num(g.progressPct || 0)}%</span>` },
    { h: 'Status', cls: 'nw', f: g => badge(g.status, g.status === 'active' ? 'ok' : g.status === 'done' ? 'info' : 'outline') },
    { h: 'Review', cls: 'nw', f: g => g.status === 'active' && g.nextReviewUtc ? `${esc(ago(g.nextReviewUtc))}<div class="cell-sub">every ${dur(g.cadenceMinutes * 60000)}</div>` : '<span class="muted">—</span>' },
    { h: '', cls: 'act', f: g => `<button class="btn xs" data-act="goal-op" data-goal="${esc(g.id)}" data-status="${g.status === 'active' ? 'paused' : 'active'}">${g.status === 'active' ? 'Pause' : 'Resume'}</button> ` +
        `<button class="btn xs danger" data-act="goal-delete" data-goal="${esc(g.id)}">Delete</button>` }
  ], goals, { empty: 'No goals', emptyText: 'Standing goals give the agent long-running objectives; it reviews each on its cadence and queues the next steps.' }),
    { flush: true, actions: `<button class="btn small" data-act="goal-add" data-id="${esc(id)}">Add goal</button>` });
  const propCard = card('Behaviour changes', table([
    { h: 'Change', cls: 'clip', f: p => `<span class="cell-title">${esc(p.proposed)}</span><div class="cell-sub">${esc(p.rationale || '')}</div>` },
    { h: 'Status', cls: 'nw', f: p => badge(p.status, p.status === 'approved' ? 'ok' : p.status === 'pending' ? 'warn' : 'outline') +
        (p.rollbackReason ? `<div class="cell-sub" data-tip="${esc(p.rollbackReason)}">${esc(ago(p.rolledBackUtc))}</div>` : '') },
    { h: 'Decided by', cls: 'nw', f: p => p.decidedBy ? esc(decidedByText(p.decidedBy === 'policy' || p.decidedBy.startsWith('agent:') ? p.decidedBy : 'human')) + `<div class="cell-sub">${esc(ago(p.decidedUtc))}</div>` : '<span class="muted">—</span>' },
    { h: '', cls: 'act', f: p => p.status === 'approved' ? `<button class="btn xs danger" data-act="rollback-proposal" data-proposal="${esc(p.id)}">Roll back</button>` : '' }
  ], props, { empty: 'No behaviour changes', emptyText: 'The agent can propose rules for itself (propose_profile_change).' }), { flush: true });
  const hookCard = card('Webhooks', table([
    { h: 'Webhook', f: h => `<span class="cell-title">${esc(h.name)}</span> ${setBy(h.createdBy)}<div class="cell-sub"><code>${esc(h.url)}</code></div>` },
    { h: 'Calls', num: true, f: h => num(h.fireCount) },
    { h: 'Last', cls: 'nw', f: h => h.lastFiredUtc ? esc(ago(h.lastFiredUtc)) : '<span class="muted">never</span>' },
    { h: '', cls: 'act', f: h => `<button class="btn xs" data-act="copy-text" data-text="${esc(location.origin + h.url)}">Copy URL</button> ` +
        `<button class="btn xs" data-act="hook-toggle" data-hook="${esc(h.id)}" data-on="${h.enabled ? '0' : '1'}">${h.enabled ? 'Turn off' : 'Turn on'}</button> ` +
        `<button class="btn xs danger" data-act="hook-delete" data-hook="${esc(h.id)}">Delete</button>` }
  ], hooks, { empty: 'No webhooks', emptyText: 'A webhook is a secret URL another service calls to give this agent a task.' }),
    { flush: true, actions: `<button class="btn small" data-act="hook-add" data-id="${esc(id)}">Add webhook</button>` });
  const skillCard = card('Skills it wrote', table([
    { h: 'Skill', f: s => `<span class="cell-title">${esc(s.name)}</span><div class="cell-sub">${esc(s.description || '')}</div>` },
    { h: 'Used', num: true, f: s => num(s.uses) }, { h: 'Updated', cls: 'nw', f: s => esc(ago(s.updatedUtc)) }
  ], skills, { empty: 'No skills yet', emptyText: 'Procedures it worked out and saved for next time (author_skill).' }), { flush: true });
  return `<div class="grid two section">${goalCard}${propCard}</div><div class="grid two section">${hookCard}${skillCard}</div>`;
}
ACTIONS['goal-add'] = el => openModal('Add a goal', `<form id="goal-form" class="form-grid" data-form="goal" data-id="${esc(el.dataset.id)}">
    <label class="full">Goal<input name="title" required maxlength="300" placeholder="e.g. Grow newsletter sign-ups by 10% a month"></label>
    <label class="full">Detail<textarea name="detail" rows="3"></textarea></label>
    <label class="span2">How progress is judged<input name="metric" maxlength="500"></label>
    <label>Review every (minutes)<input name="cadenceMinutes" type="number" min="5" value="1440"></label>
  </form><div data-errors></div>`, `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="submit-form" data-form-id="goal-form">Add goal</button>`, 'wide');
FORMS['goal'] = async (f, d) => {
  await post('/api/goals', { agent: f.dataset.id, title: d.title, detail: d.detail || null, metric: d.metric || null, cadenceMinutes: Number(d.cadenceMinutes || 1440) });
  toast('Goal added: the first review comes soon', 'ok'); closeModal(); refreshNow();
};
ACTIONS['goal-op'] = async el => { await patch('/api/goals/' + enc(el.dataset.goal), { status: el.dataset.status }); refreshNow(); };
ACTIONS['goal-delete'] = async el => {
  if (!await confirmBox('Delete goal', 'Delete this goal? Tasks it already queued stay.', { danger: true, confirm: 'Delete' })) return;
  await del('/api/goals/' + enc(el.dataset.goal)); toast('Goal deleted', 'ok'); refreshNow();
};
ACTIONS['rollback-proposal'] = async el => {
  const reason = await confirmBox('Roll back behaviour change', 'Take this rule out of the agent\'s standing instructions?', { input: 'Why (optional)', confirm: 'Roll back', danger: true });
  if (reason === null || reason === false) return;
  await post(`/api/proposals/${enc(el.dataset.proposal)}/rollback`, { reason: typeof reason === 'string' ? reason : '' });
  toast('Rolled back', 'ok'); refreshNow();
};
ACTIONS['hook-add'] = el => openModal('Add a webhook', `<form id="hook-form" class="form-grid" data-form="hook" data-id="${esc(el.dataset.id)}">
    <label class="span2">Name<input name="name" required maxlength="200" placeholder="e.g. Stripe payments"></label>
    <label class="span2">Task name<input name="taskName" maxlength="200" placeholder="Name of the tasks it creates"></label>
    <label class="full">Task text (optional)<textarea name="template" rows="3" placeholder="{{payload}} is replaced by the request body (as untrusted data). Empty: a standard text with the body."></textarea></label>
    <label>Ignore calls closer than (s)<input name="minIntervalSeconds" type="number" min="0" value="0"></label>
  </form><div data-errors></div>`, `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="submit-form" data-form-id="hook-form">Create</button>`, 'wide');
FORMS['hook'] = async (f, d) => {
  const h = await post('/api/hooks', { agent: f.dataset.id, name: d.name, taskName: d.taskName || null, template: d.template || null, minIntervalSeconds: Number(d.minIntervalSeconds || 0) });
  closeModal(); refreshNow();
  await writeClipboard(location.origin + h.url);
  toast('Webhook created; its URL is copied', 'ok');
};
ACTIONS['hook-toggle'] = async el => { await patch('/api/hooks/' + enc(el.dataset.hook), { enabled: el.dataset.on === '1' }); refreshNow(); };
ACTIONS['hook-delete'] = async el => {
  if (!await confirmBox('Delete webhook', 'Delete this webhook? Its URL stops working.', { danger: true, confirm: 'Delete' })) return;
  await del('/api/hooks/' + enc(el.dataset.hook)); toast('Webhook deleted', 'ok'); refreshNow();
};

// ------------------------------------------------------------------ evaluation suite
function evalSection(id, evals, manage, opts) {
  const cases = evals.cases || [], runs = evals.runs || [];
  const caseTable = table([
    { h: 'Test case', f: c => `<span class="cell-title">${esc(c.name)}</span><div class="cell-sub clip">${esc(c.instructions)}</div>` },
    { h: 'Expected', cls: 'clip', f: c => c.criteria ? esc(c.criteria) : '<span class="muted">judged against the task</span>' },
    { h: 'On', cls: 'nw', f: c => c.enabled ? badge('on', 'ok') : badge('off', 'outline') },
    { h: '', cls: 'act', f: c => manage ? `<button class="btn xs" data-act="eval-case" data-id="${esc(id)}" data-case="${esc(c.id)}">Edit</button> ` +
        `<button class="btn xs" data-act="eval-toggle" data-id="${esc(id)}" data-case="${esc(c.id)}" data-on="${c.enabled ? '0' : '1'}">${c.enabled ? 'Turn off' : 'Turn on'}</button> ` +
        `<button class="btn xs danger" data-act="eval-delete" data-id="${esc(id)}" data-case="${esc(c.id)}">Delete</button>` : '' }
  ], cases, { empty: 'No test cases yet', emptyText: 'Add fixed tasks with what a good result has. After every behaviour change the agent runs them; a change that makes it worse is rolled back.' });
  const runTable = runs.length ? table([
    { h: 'When', cls: 'nw', f: r => `<a href="#" data-act="eval-run" data-id="${esc(id)}" data-run="${esc(r.id)}">${esc(ago(r.startedUtc))}</a>` },
    { h: 'Why', cls: 'nw', f: r => esc(r.triggerKind === 'change' ? 'behaviour change' : r.triggerKind) + (r.createdBy ? `<div class="cell-sub">${esc(decidedByText(r.createdBy))}</div>` : '') },
    { h: 'Passed', num: true, f: r => r.status === 'running' ? '<span class="spinner"></span>' : `${num(r.passed)}/${num(r.total)}` },
    { h: 'Average', num: true, f: r => r.avgScore === null || r.avgScore === undefined ? '—' : Number(r.avgScore).toFixed(1) },
    { h: 'Verdict', cls: 'nw', f: r => r.status === 'running' ? badge('running', 'info') : verdictBadge(r.verdict) },
    { h: 'Note', cls: 'clip', f: r => esc(r.note || '') }
  ], runs) : '';
  const actions = manage ? `<button class="btn small" data-act="eval-case" data-id="${esc(id)}">Add test case</button>` +
    (cases.length ? ` <button class="btn small primary" data-act="eval-start" data-id="${esc(id)}">Run now</button>` : '') : '';
  return card('Evaluation', `${opts.evals === false ? '<div class="notice warn">Evaluations are off on this server (Autonomy.EvalsEnabled).</div>' : ''}
    <p class="muted small">Test cases run as ordinary tasks of this agent — write ones without side effects. A judge model grades each result 1-10 against its expected outcome (pass: 7 or more). The first run is the baseline; each later run is compared with the one before.</p>
    ${caseTable}${runTable ? `<h3 class="section">Runs</h3>${runTable}` : ''}`, { actions });
}

ACTIONS['eval-case'] = async el => {
  const id = el.dataset.id, caseId = el.dataset.case;
  const c = caseId ? (await get(`/api/agents/${enc(id)}/evals`)).cases.find(x => x.id === caseId) || {} : {};
  openModal(caseId ? 'Edit test case' : 'Add a test case', `<form id="eval-form" class="form-grid" data-form="eval-case" data-id="${esc(id)}" data-case="${esc(caseId || '')}">
    <label class="full">Name<input name="name" required maxlength="200" value="${esc(c.name || '')}" placeholder="e.g. Summarise a contract"></label>
    <label class="full">The task the agent gets<textarea name="instructions" rows="5" required placeholder="Exactly what the agent is asked to do. Include any input it needs.">${esc(c.instructions || '')}</textarea></label>
    <label class="full">Expected outcome <span class="badge warn">only the judge sees this</span><textarea name="criteria" rows="4" placeholder="What a good result contains: facts, numbers, format, quality bar.">${esc(c.criteria || '')}</textarea></label>
  </form><div data-errors></div>`, `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="submit-form" data-form-id="eval-form">${caseId ? 'Save' : 'Add'}</button>`, 'wide');
};
FORMS['eval-case'] = async (f, d) => {
  const id = f.dataset.id, caseId = f.dataset.case;
  const body = { name: d.name, instructions: d.instructions, criteria: d.criteria || '' };
  if (caseId) await patch(`/api/agents/${enc(id)}/evals/${enc(caseId)}`, body); else await post(`/api/agents/${enc(id)}/evals`, body);
  toast(caseId ? 'Test case saved' : 'Test case added', 'ok'); closeModal(); refreshNow();
};
ACTIONS['eval-toggle'] = async el => {
  await patch(`/api/agents/${enc(el.dataset.id)}/evals/${enc(el.dataset.case)}`, { enabled: el.dataset.on === '1' });
  refreshNow();
};
ACTIONS['eval-delete'] = async el => {
  if (!await confirmBox('Delete test case', 'Delete this test case? Earlier runs keep their results.', { danger: true, confirm: 'Delete' })) return;
  await del(`/api/agents/${enc(el.dataset.id)}/evals/${enc(el.dataset.case)}`); toast('Test case deleted', 'ok'); refreshNow();
};
ACTIONS['eval-start'] = async el => {
  const r = await post(`/api/agents/${enc(el.dataset.id)}/evals/run`);
  toast(`Evaluation started: ${plural(r.total, 'case')}`, 'ok'); refreshNow();
};
ACTIONS['eval-run'] = async el => {
  const r = await get(`/api/agents/${enc(el.dataset.id)}/evals/runs/${enc(el.dataset.run)}`);
  openModal('Evaluation run', `<dl class="kv"><dt>Started</dt><dd>${esc(ts(r.startedUtc))}</dd><dt>Why</dt><dd>${esc(r.triggerKind)}${r.changeRef ? ' · change ' + esc(r.changeRef) : ''}</dd>
      <dt>Result</dt><dd>${num(r.passed)}/${num(r.total)} passed${r.avgScore !== null && r.avgScore !== undefined ? ', average ' + Number(r.avgScore).toFixed(1) : ''} · ${verdictBadge(r.verdict)}</dd>
      ${r.note ? `<dt>Note</dt><dd>${esc(r.note)}</dd>` : ''}</dl>` +
    table([
      { h: 'Case', f: x => esc(x.caseName || x.caseId) },
      { h: 'Result', cls: 'nw', f: x => badge(x.status, x.status === 'passed' ? 'ok' : x.status === 'pending' ? 'info' : 'bad') },
      { h: 'Score', num: true, f: x => x.score === null || x.score === undefined ? '—' : num(x.score) },
      { h: 'Judge', cls: 'clip', f: x => esc(x.feedback || '') },
      { h: '', cls: 'act', f: x => x.taskId ? `<a href="#" data-act="open-task" data-id="${esc(r.agentId)}" data-task="${esc(x.taskId)}">task</a>` : '' }
    ], r.results || []), '', 'wide');
};

// ------------------------------------------------------------------ mailboxes
function mailboxSection(id, boxes, admin, opts) {
  return card('Mailboxes', `${opts.email === false ? '<div class="notice warn">Email input is off on this server (Autonomy.EmailEnabled).</div>' : ''}
    <p class="muted small">New mail in these IMAP mailboxes becomes a task for this agent (the first check only notes where the mailbox stands). The password is a vault secret (Secrets). Sender addresses can be forged: the agent treats every email as untrusted data.</p>` +
    table([
      { h: 'Mailbox', f: m => `<span class="cell-title">${esc(m.username)}</span><div class="cell-sub">${esc(m.host)}:${esc(m.port)}${m.useSsl ? ' · TLS' : ''} · ${esc(m.folder)}</div>` },
      { h: 'Accepts mail from', cls: 'clip', f: m => m.allowedSenders ? esc(m.allowedSenders) : '<span class="muted">anyone</span>' },
      { h: 'Checked', cls: 'nw', f: m => (m.enabled ? '' : badge('off', 'outline') + ' ') + (m.lastPollUtc ? esc(ago(m.lastPollUtc)) : '<span class="muted">never</span>') +
          (m.lastError ? `<div class="cell-sub error-text">${esc(m.lastError)}</div>` : '') },
      { h: 'Tasks', num: true, f: m => num(m.received) },
      { h: '', cls: 'act', f: m => admin ? `<button class="btn xs" data-act="mailbox-check" data-mailbox="${esc(m.id)}">Check now</button> ` +
          `<button class="btn xs" data-act="mailbox-form" data-id="${esc(id)}" data-mailbox="${esc(m.id)}">Edit</button> ` +
          `<button class="btn xs danger" data-act="mailbox-delete" data-mailbox="${esc(m.id)}">Delete</button>` : '' }
    ], boxes, { empty: 'No mailboxes', emptyText: 'An administrator can connect a mailbox so this agent works on new mail.' }),
    { flush: false, actions: admin ? `<button class="btn small" data-act="mailbox-form" data-id="${esc(id)}">Add mailbox</button>` : '' });
}
ACTIONS['mailbox-form'] = async el => {
  const id = el.dataset.id, mid = el.dataset.mailbox;
  const [boxes, secrets] = await Promise.all([mid ? get('/api/mailboxes?agent=' + enc(id)) : Promise.resolve([]), get('/api/secrets').catch(() => [])]);
  const m = boxes.find(x => x.id === mid) || { port: 993, useSsl: true, folder: 'INBOX', pollSeconds: 120, priority: 1, enabled: true };
  const pr = ['Low', 'Normal', 'High', 'Critical'];
  openModal(mid ? 'Edit mailbox' : 'Connect a mailbox', `<form id="mailbox-form" class="form-grid" data-form="mailbox" data-id="${esc(id)}" data-mailbox="${esc(mid || '')}">
    <label class="span2">IMAP server<input name="host" required value="${esc(m.host || '')}" placeholder="imap.example.com"></label>
    <label>Port<input name="port" type="number" min="1" max="65535" value="${esc(m.port)}"></label>
    <label class="check"><input type="checkbox" name="useSsl"${m.useSsl ? ' checked' : ''}> TLS (port 993)</label>
    <label class="span2">User name<input name="username" required value="${esc(m.username || '')}" placeholder="agent@example.com"></label>
    <label class="span2">Password (vault secret)<select name="passwordSecret" required>${secrets.map(s => `<option${s.name === m.passwordSecret ? ' selected' : ''}>${esc(s.name)}</option>`).join('')}</select>
      <span class="help">${secrets.length ? 'Store it under Secrets first' : 'No secrets yet: add the password under Secrets first'}</span></label>
    <label>Folder<input name="folder" value="${esc(m.folder || 'INBOX')}"></label>
    <label>Check every (s)<input name="pollSeconds" type="number" min="30" value="${esc(m.pollSeconds)}"></label>
    <label>Priority<select name="priority">${pr.map((x, i) => `<option value="${i}"${i === m.priority ? ' selected' : ''}>${x}</option>`).join('')}</select></label>
    <label class="check"><input type="checkbox" name="enabled"${m.enabled ? ' checked' : ''}> Enabled</label>
    <label class="full">Accept mail from<input name="allowedSenders" value="${esc(m.allowedSenders || '')}" placeholder="@example.com, boss@corp.com — empty: anyone"></label>
    <label class="full">Task name<input name="taskName" value="${esc(m.taskName || '')}" placeholder="Email: <subject>"></label>
    <label class="full">Task text (optional)<textarea name="template" rows="4" placeholder="Default: the email as untrusted data. Placeholders: {{from}} {{name}} {{subject}} {{date}} {{body}} {{attachments}} {{email}}">${esc(m.template || '')}</textarea></label>
  </form><div data-errors></div>`, `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="submit-form" data-form-id="mailbox-form">Save</button>`, 'wide');
};
FORMS['mailbox'] = async (f, d) => {
  const body = { agentId: f.dataset.id, host: d.host, port: Number(d.port || 993), useSsl: !!f.elements.useSsl.checked, username: d.username, passwordSecret: d.passwordSecret,
    folder: d.folder || 'INBOX', pollSeconds: Number(d.pollSeconds || 120), priority: Number(d.priority || 1), enabled: !!f.elements.enabled.checked,
    allowedSenders: d.allowedSenders || null, taskName: d.taskName || null, template: d.template || null };
  if (f.dataset.mailbox) await put('/api/mailboxes/' + enc(f.dataset.mailbox), body); else await post('/api/mailboxes', body);
  toast('Mailbox saved', 'ok'); closeModal(); refreshNow();
};
ACTIONS['mailbox-check'] = async el => {
  const r = await post(`/api/mailboxes/${enc(el.dataset.mailbox)}/check`);
  toast(r.ok ? `Checked: ${plural(r.created, 'new task')}` : 'Could not read the mailbox: ' + r.error, r.ok ? 'ok' : 'bad'); refreshNow();
};
ACTIONS['mailbox-delete'] = async el => {
  if (!await confirmBox('Delete mailbox', 'Stop reading this mailbox? Tasks it created stay.', { danger: true, confirm: 'Delete' })) return;
  await del('/api/mailboxes/' + enc(el.dataset.mailbox)); toast('Mailbox deleted', 'ok'); refreshNow();
};

// ------------------------------------------------------------------ secrets (administrators)
VIEWS.secrets = {
  live: false, reloadable: true,
  async render() {
    setCrumbs([['Secrets', '/secrets']]);
    const list = await get('/api/secrets');
    return pageHead('Secrets', {
      subtitle: 'Values tools use without the model ever seeing them: API keys in http_request ({{secret:NAME}}), mailbox passwords. Encrypted on the server, never shown again, kept out of logs and tool output.',
      actions: `<button class="btn primary" data-act="secret-form">Add secret</button>`
    }) + `<div class="notice warn">Bind each secret to the hosts that need it (Only sent to): then even an agent tricked by a web page cannot send it anywhere else. A secret without hosts goes to any host its agents may call.</div>` +
      card('', table([
        { h: 'Name', f: s => `<code>${esc(s.name)}</code><div class="cell-sub">${esc(s.description || '')}</div>` },
        { h: 'Agents', f: s => (s.agentIds || []).includes('*') ? tag('every agent', 'warn') : chips(s.agentIds) },
        { h: 'Only sent to', f: s => (s.hosts || []).length ? chips(s.hosts) : tag('any allowed host', 'warn') },
        { h: 'Used', cls: 'nw', f: s => `${num(s.uses)}×` + (s.lastUsedUtc ? `<div class="cell-sub">${esc(ago(s.lastUsedUtc))}</div>` : '') },
        { h: 'Changed', cls: 'nw', f: s => esc(ago(s.updatedUtc)) + (s.createdBy ? `<div class="cell-sub">by ${esc(s.createdBy)}</div>` : '') },
        { h: '', cls: 'act', f: s => `<button class="btn xs" data-act="secret-form" data-name="${esc(s.name)}">Edit</button> <button class="btn xs danger" data-act="secret-delete" data-name="${esc(s.name)}">Delete</button>` }
      ], list, { empty: 'No secrets yet', emptyText: 'Add an API key or a password; then give agents the web hosts they may call (agent editor → Autonomy).' }), { flush: true, cls: 'section' });
  }
};
ACTIONS['secret-form'] = async el => {
  const name = el.dataset.name;
  const [list, agents] = await Promise.all([name ? get('/api/secrets') : Promise.resolve([]), get('/api/agents')]);
  const s = list.find(x => x.name === name) || { agentIds: [] };
  const ids = new Set(s.agentIds || []);
  openModal(name ? 'Edit secret ' + name : 'Add a secret', `<form id="secret-form" class="form-grid" data-form="secret" data-name="${esc(name || '')}">
    <label class="span2">Name<input name="name" required ${name ? 'readonly' : ''} pattern="[A-Za-z][A-Za-z0-9_.\\-]{0,63}" value="${esc(name || '')}" placeholder="e.g. STRIPE_KEY"><span class="help">Agents write {{secret:NAME}}</span></label>
    <label class="span2">Value<input name="value" type="password" autocomplete="new-password" ${name ? '' : 'required'} placeholder="${name ? 'leave empty to keep the stored value' : ''}"></label>
    <label class="full">What it is for<input name="description" value="${esc(s.description || '')}" maxlength="500"></label>
    <label class="full">Only sent to<input name="hosts" value="${esc((s.hosts || []).join(', '))}" placeholder="api.stripe.com, *.example.com — empty: any host its agents may call">
      <span class="help">http_request fills it in only for requests to these hosts</span></label>
    <div class="full"><b>Agents that may use it</b>
      <label class="check"><input type="checkbox" data-group="secret-agents" value="*"${ids.has('*') ? ' checked' : ''}> Every agent</label>
      <div class="check-grid">${agents.filter(a => !a.isSubagent).map(a => `<label class="check"><input type="checkbox" data-group="secret-agents" value="${esc(a.id)}"${ids.has(a.id) ? ' checked' : ''}> ${esc(a.name || a.id)}</label>`).join('')}</div></div>
  </form><div data-errors></div>`, `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="submit-form" data-form-id="secret-form">Save</button>`, 'wide');
};
FORMS['secret'] = async (f, d) => {
  const name = f.dataset.name || d.name;
  const body = { description: d.description || null, agents: $$('input[data-group="secret-agents"]:checked', f).map(i => i.value),
    hosts: (d.hosts || '').split(',').map(x => x.trim()).filter(Boolean) };
  if (d.value) body.value = d.value;
  await put('/api/secrets/' + enc(name), body);
  toast('Secret saved', 'ok'); closeModal(); refreshNow();
};
ACTIONS['secret-delete'] = async el => {
  if (!await confirmBox('Delete secret', `Delete ${esc(el.dataset.name)}? Tools and mailboxes that use it stop working.`, { danger: true, confirm: 'Delete' })) return;
  await del('/api/secrets/' + enc(el.dataset.name)); toast('Secret deleted', 'ok'); refreshNow();
};
