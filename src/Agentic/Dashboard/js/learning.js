'use strict';
/* Agentic console - organizational learning: case studies of agents that went broke or became very profitable, and
   the shared lesson library every agent learns from. */

const OUTCOME_CLASS = { Failure: 'bad', Success: 'ok', Review: 'info' };
const STUDY_STATUS_CLASS = { Pending: '', Analyzing: 'info', Published: 'ok', AwaitingReview: 'warn', Failed: 'bad' };
const LESSON_STATUS_CLASS = { Active: 'ok', PendingReview: 'warn', Retracted: 'outline' };
const TRIGGER_TEXT = { Broke: 'went broke', LowBalance: 'low balance', Profit: 'large profit', RoiMilestone: 'high ROI', Manual: 'requested' };

function studyTable(list, opts = {}) {
  return table([
    { h: 'When', f: s => `<span data-tip="${esc(ts(s.createdUtc))}">${esc(ago(s.createdUtc))}</span>` },
    ...(opts.agent ? [] : [{ h: 'Agent', f: s => agentLink(s.agentId) }]),
    { h: 'Outcome', cls: 'nw', f: s => badge(s.outcome, OUTCOME_CLASS[s.outcome]) + `<div class="cell-sub">${esc(TRIGGER_TEXT[s.triggerKind] || s.triggerKind)}</div>` },
    { h: 'Finding', cls: 'clip', f: s => s.headline ? `<a href="#" class="cell-title" data-act="open-study" data-id="${esc(s.id)}">${esc(s.headline)}</a>` : `<span class="muted">${esc(s.triggerDetail || '')}</span>` },
    { h: 'Status', cls: 'nw', f: s => badge(s.status === 'AwaitingReview' ? 'Awaiting review' : s.status, STUDY_STATUS_CLASS[s.status]) },
    { h: 'Lessons', num: true, f: s => num(s.lessonCount) },
    { h: 'Analysis', f: s => s.analysisModel ? tag(s.analysisModel === 'rules' ? 'rule-based' : s.analysisModel) : '<span class="muted">—</span>' },
    { h: '', cls: 'act', f: s => `<button class="btn xs" data-act="open-study" data-id="${esc(s.id)}">Open</button>` +
        (['Published', 'AwaitingReview', 'Failed'].includes(s.status) ? ` <button class="btn xs" data-act="rerun-study" data-id="${esc(s.id)}">Re-analyze</button>` : '') }
  ], list, { empty: opts.empty || 'No case studies yet', emptyText: opts.emptyText || 'A study opens automatically when an agent goes broke or becomes very profitable, or when you start one.' });
}

function lessonTable(list, opts = {}) {
  return table([
    { h: 'Lesson', f: l => `${l.pinned ? `<span class="badge accent" data-tip="Pinned: shown to every agent first">${icon('pin')}</span> ` : ''}${badge(l.kind, l.kind === 'Avoid' ? 'warn' : 'ok', false)} <span class="cell-title">${esc(l.title)}</span>` +
        `${l.confirmations > 1 ? ` <span class="badge info" data-tip="Confirmed independently by ${l.confirmations} case studies${l.lastConfirmedUtc ? '; last ' + esc(ago(l.lastConfirmedUtc)) : ''}. Well-confirmed lessons are preferred in prompts.">${icon('check')} ${l.confirmations}×</span>` : ''}` +
        `<div class="cell-sub">${esc(l.body)}</div>` },
    { h: 'From', cls: 'nw', f: l => agentLink(l.sourceAgentId) + `<div class="cell-sub">${esc((l.outcome || '').toLowerCase())}</div>` },
    { h: 'Tags', f: l => l.tags ? chips(l.tags.split(',').map(x => x.trim()).filter(Boolean)) : '' },
    { h: 'Used', num: true, f: l => `<span data-tip="Times shown to agents in task prompts${l.lastUsedUtc ? '; last ' + esc(ago(l.lastUsedUtc)) : ''}">${num(l.useCount)}</span>` },
    { h: 'Status', cls: 'nw', f: l => badge(l.status === 'PendingReview' ? 'Pending review' : l.status, LESSON_STATUS_CLASS[l.status]) },
    ...(opts.readOnly ? [] : [{
      h: '', cls: 'act', f: l => {
        const d = `data-id="${esc(l.id)}"`;
        let h = '';
        if (l.status === 'PendingReview') h += `<button class="btn xs primary" data-act="lesson-status" data-status="Active" ${d}>Approve</button> `;
        if (l.status === 'Retracted') h += `<button class="btn xs" data-act="lesson-status" data-status="Active" ${d}>Restore</button> `;
        if (l.status !== 'Retracted') h += `<button class="btn xs danger" data-act="lesson-status" data-status="Retracted" ${d}>Retract</button> `;
        if (l.status === 'Active') h += `<button class="btn xs" data-act="lesson-pin" data-pinned="${l.pinned ? 0 : 1}" ${d}>${l.pinned ? 'Unpin' : 'Pin'}</button> `;
        return h + `<button class="btn xs" data-act="edit-lesson" ${d} data-title="${esc(l.title)}" data-body="${esc(l.body)}" data-tags="${esc(l.tags || '')}">Edit</button>`;
      }
    }])
  ], list, { empty: opts.empty || 'No lessons yet' });
}

VIEWS.learning = {
  live: true,
  async render(p) {
    setCrumbs([['Learning', '/learning']]);
    const q = p.query;
    const status = q.get('status') || '', outcome = q.get('outcome') || '', text = q.get('q') || '';
    const lp = new URLSearchParams(); if (status) lp.set('status', status); if (outcome) lp.set('outcome', outcome); if (text) lp.set('q', text);
    const [sum, studies, lessons] = await Promise.all([get('/api/learning/summary'), get('/api/learning/studies?limit=100'), get('/api/learning/lessons?' + lp)]);
    const st = sum.settings;
    const opt = (v, cur, label) => `<option value="${esc(v)}"${v === cur ? ' selected' : ''}>${esc(label || v || 'All')}</option>`;
    const filters = `<form class="filters" data-form="lesson-filter">
      <label class="grow">Search lessons<input name="q" value="${esc(text)}" placeholder="e.g. escalation, subagents, research"></label>
      <label>Status<select name="status">${opt('', status)}${opt('Active', status)}${opt('PendingReview', status, 'Pending review')}${opt('Retracted', status)}</select></label>
      <label>From<select name="outcome">${opt('', outcome)}${opt('Failure', outcome, 'Failures')}${opt('Success', outcome, 'Successes')}${opt('Review', outcome, 'Reviews')}</select></label>
      <button class="btn">Apply</button></form>`;
    return pageHead('Learning', {
      subtitle: 'When an agent goes broke or becomes very profitable, the host studies the decisions that led there and shares the lessons with every agent.',
      actions: `<button class="btn primary" data-act="study-agent">Study an agent</button>`
    }) + (st.enabled ? '' : '<div class="notice warn">Learning is disabled (Learning.Enabled in appsettings.json).</div><div class="section"></div>') +
      `<div class="grid kpis">
        ${kpi('Failure studies', num(sum.failureStudies), 'agents that went broke')}
        ${kpi('Success studies', num(sum.successStudies), 'very profitable agents')}
        ${kpi('Active lessons', num(sum.activeLessons), `${num(sum.pendingLessons)} pending · ${num(sum.confirmations || 0)} confirmed`)}
        ${kpi('Lessons shown to agents', num(sum.lessonUses), 'times, in task prompts')}
        ${kpi('In progress', num(sum.studiesInProgress), sum.lastStudyUtc ? 'last study ' + esc(ago(sum.lastStudyUtc)) : 'no studies yet')}
      </div>
      <div class="notice info section"><b>How it works.</b> A failure study opens when an agent's available balance drops below ${usd(st.brokeThreshold)} or a paid call is refused for lack of funds.
        A success study opens when profit is credited and the agent reaches ${esc(st.minRoi)}× ROI with at least ${usd(st.minProfit)} profit, or on any single credit of ${usd(st.largeProfit)} or more.
        The analysis runs on ${chips(st.modelKeys)} (free) and falls back to rule-based analysis. Lessons ${st.requireApproval ? 'wait for your approval, then ' : ''}appear in every agent's task prompts
        (up to ${esc(st.promptLessons)} relevant ones) and via the <code>search_lessons</code> tool; the studied agent also keeps them in its own memory.
        When a later study reaches a conclusion the library already holds, the existing lesson is <b>confirmed</b> instead of duplicated; well-confirmed lessons are preferred.</div>
      ${card('Case studies', studyTable(studies), { flush: true, cls: 'section' })}
      ${card('Lesson library', filters + '<div class="section"></div>' + lessonTable(lessons), { cls: 'section', export: 'lessons', hint: 'Retract anything wrong or unhelpful; pin the most important lessons so every agent sees them.' })}`;
  }
};
FORMS['lesson-filter'] = (f, d) => { const p = new URLSearchParams(); ['q', 'status', 'outcome'].forEach(k => { if (d[k]) p.set(k, d[k]); }); navigate('/learning' + (p.toString() ? '?' + p : '')); };

ACTIONS['open-study'] = async el => {
  const d = await get('/api/learning/studies/' + enc(el.dataset.id));
  const s = d.study, r = d.report || {};
  const list = items => items && items.length ? `<ul>${items.map(x => `<li>${esc(x)}</li>`).join('')}</ul>` : '<p class="muted">—</p>';
  openModal('Case study: ' + s.agentId, `<div class="row">${badge(s.outcome, OUTCOME_CLASS[s.outcome])} ${tag(TRIGGER_TEXT[s.triggerKind] || s.triggerKind)} ${badge(s.status, STUDY_STATUS_CLASS[s.status])}
      ${s.analysisModel ? tag(s.analysisModel === 'rules' ? 'rule-based analysis' : 'analyzed by ' + s.analysisModel) : ''}</div>
    <dl class="kv section"><dt>Agent</dt><dd>${agentLink(s.agentId)}</dd><dt>Trigger</dt><dd>${esc(s.triggerDetail || '—')}</dd>
      <dt>Period studied</dt><dd>${esc(ts(s.windowStartUtc))} → ${esc(ts(s.windowEndUtc))}</dd>${s.note ? `<dt>Note</dt><dd>${esc(s.note)}</dd>` : ''}
      ${s.error ? `<dt>Error</dt><dd class="error-text">${esc(s.error)}</dd>` : ''}</dl>
    ${r.headline ? `<h3 class="section">Finding</h3><p><b>${esc(r.headline)}</b></p>` : (['Pending', 'Analyzing'].includes(s.status) ? '<div class="notice info section">Analysis in progress…</div>' : '')}
    ${r.keyDecisions ? `<h3 class="section">Decisions that drove the outcome</h3>${list(r.keyDecisions)}` : ''}
    ${r.rootCauses && r.rootCauses.length ? `<h3 class="section">Root causes</h3>${list(r.rootCauses)}` : ''}
    <h3 class="section">New lessons shared with all agents</h3>${lessonTable(d.lessons, { empty: 'No new lessons from this study.' })}
    ${d.confirmedLessons && d.confirmedLessons.length ? `<h3 class="section">Existing lessons this study confirmed</h3>${lessonTable(d.confirmedLessons, { readOnly: true })}` : ''}
    ${r.recommendations && r.recommendations.length ? `<h3 class="section">Recommendations for administrators</h3>${list(r.recommendations)}` : ''}
    ${d.evidence ? `<details class="section"><summary class="muted">Evidence the analysis was based on</summary>${json(d.evidence)}</details>` : ''}`,
    ['Published', 'AwaitingReview', 'Failed'].includes(s.status) ? `<button class="btn" data-act="rerun-study" data-id="${esc(s.id)}">Re-analyze</button>` : '', 'wide');
};
ACTIONS['rerun-study'] = async el => {
  if (!await confirmBox('Re-analyze case study', 'Run the analysis again? Its current lessons are retracted and replaced by the new ones.', { confirm: 'Re-analyze' })) return;
  await post(`/api/learning/studies/${enc(el.dataset.id)}/rerun`); toast('Re-analysis queued', 'ok'); closeModal(); refreshNow();
};
ACTIONS['study-agent'] = async el => {
  const agents = await get('/api/agents');
  openModal('Study an agent', `<p class="muted">The host analyzes the agent's recent decisions (spend by model, escalations, spillover, subagents, costly and profitable tasks) and publishes lessons for every agent.</p>
    <form id="study-form" class="form-grid" data-form="study-agent">
      <label>Agent<select name="agentId">${agents.map(a => `<option value="${esc(a.id)}"${a.id === el.dataset.agent ? ' selected' : ''}>${esc(a.name)} (${esc(a.id)})</option>`).join('')}</select></label>
      <label>Study as<select name="outcome"><option value="Review">General review</option><option value="Failure">Failure (wasted budget)</option><option value="Success">Success (profitable)</option></select></label>
      <label class="full">Note for the analysis (optional)<textarea name="note" rows="3" maxlength="2000" placeholder="e.g. Its research reports led to two new contracts."></textarea></label></form>`,
    `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="submit-form" data-form-id="study-form">Start study</button>`);
};
FORMS['study-agent'] = async (f, d) => {
  const s = await post('/api/learning/studies', { agentId: d.agentId, outcome: d.outcome, note: d.note || null });
  toast(`Case study started for ${s.agentId}`, 'ok'); closeModal(); refreshNow();
};
ACTIONS['lesson-status'] = async el => {
  await patch('/api/learning/lessons/' + enc(el.dataset.id), { status: el.dataset.status });
  toast(el.dataset.status === 'Retracted' ? 'Lesson retracted; agents no longer see it' : 'Lesson active', 'ok'); closeModal(); refreshNow();
};
ACTIONS['lesson-pin'] = async el => { await patch('/api/learning/lessons/' + enc(el.dataset.id), { pinned: el.dataset.pinned === '1' }); toast(el.dataset.pinned === '1' ? 'Pinned' : 'Unpinned', 'ok'); closeModal(); refreshNow(); };
ACTIONS['edit-lesson'] = el => {
  openModal('Edit lesson', `<form id="lesson-form" class="form-grid" data-form="edit-lesson" data-id="${esc(el.dataset.id)}">
    <label class="full">Title<input name="title" required maxlength="120" value="${esc(el.dataset.title)}"></label>
    <label class="full">Lesson<textarea name="body" rows="4" required maxlength="800">${esc(el.dataset.body)}</textarea></label>
    <label class="full">Tags (comma separated)<input name="tags" maxlength="200" value="${esc(el.dataset.tags)}"></label></form>`,
    `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="submit-form" data-form-id="lesson-form">Save</button>`);
};
FORMS['edit-lesson'] = async (f, d) => { await patch('/api/learning/lessons/' + enc(f.dataset.id), { title: d.title, body: d.body, tags: d.tags }); toast('Lesson saved', 'ok'); closeModal(); refreshNow(); };

/** Agent detail tab: studies of this agent and the lessons it contributed. */
async function learningTab(id) {
  const [studies, lessons] = await Promise.all([get('/api/learning/studies?agent=' + enc(id)), get('/api/learning/lessons?agent=' + enc(id))]);
  return card('Case studies of this agent', studyTable(studies, { agent: true, emptyText: 'None yet. Studies open when this agent goes broke or becomes very profitable.' }),
    { flush: true, actions: `<button class="btn small primary" data-act="study-agent" data-agent="${esc(id)}">Study now</button>` }) +
    card('Lessons other agents learned from it', lessonTable(lessons, { empty: 'No lessons from this agent yet.' }), { flush: true, cls: 'section' });
}
