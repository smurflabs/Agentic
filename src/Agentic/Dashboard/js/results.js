'use strict';
/* Agentic console - Results: work the agents finished and the reviewer accepted (or rejected), delivered to the
   administrator with the answer, every file, the agents' conversation and the cost. Rate results and reward agents. */

const VERDICT_LABEL = { Approved: 'Approved', Rejected: 'Rejected', NotReviewed: 'Not reviewed' };
function verdictBadge(v) { return badge(VERDICT_LABEL[v] || v || '—', v === 'Approved' ? 'ok' : v === 'Rejected' ? 'bad' : 'outline'); }
function stars(n, size = '') { let h = `<span class="stars ${size}" aria-label="${n || 0} of 5">`; for (let i = 1; i <= 5; i++) h += `<span class="${i <= (n || 0) ? 'on' : ''}">★</span>`; return h + '</span>'; }
function resultFileUrl(id, path, download) { return `/api/results/${enc(id)}/file?path=${enc(path)}${download ? '&download=1' : ''}`; }
const FILE_ICON = { image: 'IMG', text: 'TXT', pdf: 'PDF', docx: 'DOC', doc: 'DOC', xlsx: 'XLS', xls: 'XLS', csv: 'CSV', pptx: 'PPT', zip: 'ZIP', json: 'JSN', md: 'MD' };
function fileBadge(f) { const ext = (f.name.split('.').pop() || '').toLowerCase(); return `<span class="attach-kind k-${esc(f.kind)}">${esc(FILE_ICON[ext] || FILE_ICON[f.kind] || ext.slice(0, 3).toUpperCase() || 'FILE')}</span>`; }

VIEWS.results = {
  live: true,
  async render(p) {
    setCrumbs([['Results', '/results']]);
    const status = p.query.get('status') || '';
    const d = await get('/api/results?limit=300' + (status ? '&status=' + enc(status) : ''));
    const c = d.counts || {}, n = k => c[k] || 0;
    const all = d.results;
    const rated = all.filter(r => r.rating);
    const avg = rated.length ? rated.reduce((s, r) => s + r.rating, 0) / rated.length : null;
    const reviewed = all.filter(r => r.reviewVerdict !== 'NotReviewed');
    const approved = reviewed.filter(r => r.reviewVerdict === 'Approved').length;
    const tabs = [['', 'Inbox', n('New') + n('Viewed') + n('Rated')], ['New', 'New', n('New')], ['Viewed', 'To rate', n('Viewed')], ['Rated', 'Rated', n('Rated')], ['Archived', 'Archived', n('Archived')], ['all', 'All']];
    const tabBar = `<div class="tabs">${tabs.map(([k, label, count]) => `<a href="/results${k ? '?status=' + k : ''}" data-nav class="${status === k ? 'active' : ''}">${esc(label)}${count ? ` <span class="pill ${k === 'New' ? 'ok' : ''}">${num(count)}</span>` : ''}</a>`).join('')}</div>`;
    const cards = all.map(r => `<a class="card result-card ${r.status === 'New' ? 'is-new' : ''}" href="/results/${enc(r.id)}" data-nav data-key="${esc(r.id)}">
        <div class="head"><span class="cell-title">${esc(r.title)}</span><span class="spacer"></span>${r.status === 'New' ? badge('New') : ''}</div>
        <div class="row small">${r.success ? '' : badge('Failed', 'bad')} ${verdictBadge(r.reviewVerdict)}${r.reviewScore ? ` <b>${r.reviewScore}/10</b>` : ''}${r.revisions ? ' ' + tag(r.revisions + ' revision' + (r.revisions === 1 ? '' : 's'), 'warn') : ''} ${tag(r.sourceType === 'GlobalTask' ? 'global task' : 'task')}</div>
        <p class="excerpt">${esc(r.summary || '')}</p>
        <div class="foot"><span>${chips(r.participants, 'info')}</span><span class="spacer"></span>
          ${r.files ? `<span class="muted small" data-tip="${r.images} image(s)">${icon('page')} ${num(r.files)}</span>` : ''}
          <span class="muted small">${money(r.costMicros, 3)}</span>${r.awardedMicros ? ` <span class="badge ok">+${money(r.awardedMicros, 2)}</span>` : ''}
          ${r.rating ? stars(r.rating, 'sm') : ''}<span class="muted small" data-tip="${esc(ts(r.createdUtc))}">${esc(ago(r.createdUtc))}</span></div>
      </a>`).join('');
    return pageHead('Results', { subtitle: 'Finished work delivered to you: the answer, every file the agents made, their conversation and the reviewer\'s verdict. Rate results and reward the agents that earned it.' }) +
      `<div class="grid kpis">
        ${kpi('New', num(n('New')), 'waiting for you')}
        ${kpi('To rate', num(n('Viewed')), 'opened, not rated')}
        ${kpi('Average rating', avg ? avg.toFixed(1) + ' / 5' : '—', rated.length ? stars(Math.round(avg)) : 'no ratings yet')}
        ${kpi('Approved by reviewer', reviewed.length ? Math.round(approved / reviewed.length * 100) + '%' : '—', `${num(approved)} of ${num(reviewed.length)} reviewed`)}
        ${kpi('Awarded', money(all.reduce((s, r) => s + r.awardedMicros, 0), 2), 'paid to agents for results')}
        ${kpi('Cost of these results', money(all.reduce((s, r) => s + r.costMicros, 0), 2), 'cloud spend of their workflows')}
      </div>` + tabBar +
      (cards ? `<div class="result-grid section">${cards}</div>` : card('', emptyState('No results here', 'When you add a task or a global task with "Show me the result in Results", the finished work appears here.')));
  }
};

VIEWS.result = {
  live: false,
  async render(p) {
    const id = p.args[0], tab = p.args[1] || 'answer';
    const d = await get('/api/results/' + enc(id));
    const r = d.result;
    setCrumbs([['Results', '/results'], [r.title, '']]);
    refreshBadges();
    const base = '/results/' + enc(id);
    const files = r.files || [];
    const conv = d.conversation || { tasks: [], messages: [] };
    const tabs = [['answer', 'Answer'], ['files', `Files (${files.length})`], ['conversation', `Conversation (${(conv.messages || []).length + (conv.tasks || []).length})`],
      ['review', 'Review'], ['agents', 'Agents & rewards']];
    let body = '';
    if (tab === 'answer') body = resultAnswer(r, d);
    else if (tab === 'files') body = resultFiles(r);
    else if (tab === 'conversation') body = resultConversation(conv);
    else if (tab === 'review') body = resultReview(r);
    else body = resultAgents(r, d);
    const src = r.sourceType === 'GlobalTask' ? link('/global-tasks/' + enc(r.sourceId), 'Global task', 'btn') : '';
    return pageHead(r.title, {
      badges: `${r.success ? badge('Succeeded', 'ok') : badge('Failed', 'bad')} ${verdictBadge(r.reviewVerdict)} ${badge(r.status)}`,
      subtitle: `Delivered ${esc(ago(r.createdUtc))} · ${chips(r.participants, 'info')}${r.createdBy ? ' · requested by ' + esc(r.createdBy) : ''}`,
      actions: `<a class="btn primary" href="/api/results/${enc(id)}/zip">${icon('download')} Download all</a>${src}` +
        (r.correlationId ? link('/correlations/' + enc(r.correlationId), 'Workflow', 'btn') : '') + discussButton(`data-result="${esc(id)}"`) +
        `<button class="btn" data-act="archive-result" data-id="${esc(id)}" data-archived="${r.status === 'Archived' ? '0' : '1'}">${r.status === 'Archived' ? 'Restore' : 'Archive'}</button>`
    }) + `<div class="grid kpis">
        ${kpi('Review', r.reviewScore ? r.reviewScore + '/10' : VERDICT_LABEL[r.reviewVerdict] || '—', r.reviewerAgentId ? 'by ' + esc(r.reviewerAgentId) : 'no reviewer', { bar: r.reviewScore ? meter(r.reviewScore / 10, 'reviewer score') : '' })}
        ${kpi('Revisions', num(r.revisions), r.revisions ? 'sent back before acceptance' : 'accepted first time')}
        ${kpi('Files', num(files.length), `${files.filter(f => f.kind === 'image').length} image(s)`)}
        ${kpi('Cost', money(r.costMicros, 3), 'cloud spend of the workflow')}
        ${kpi('Awarded', money(r.awardedMicros, 2), (d.awards || []).length + ' award(s)')}
        ${kpi('Your rating', r.rating ? stars(r.rating) : '—', r.rating ? (r.ratingComment ? esc(r.ratingComment.slice(0, 60)) : 'rated') : 'not rated yet')}
      </div>
      <div class="tabs">${tabs.map(([k, label]) => `<a href="${base}/${k}" data-nav class="${tab === k ? 'active' : ''}">${esc(label)}</a>`).join('')}</div>` + body;
  }
};

function resultAnswer(r, d) {
  const imgs = (r.files || []).filter(f => f.kind === 'image').slice(0, 8);
  return `<div class="grid side section">
      ${card('Answer', `<div class="final-answer md">${markdown(r.summary || '')}</div>` +
        (d.data ? `<h3 class="section">Structured data</h3>${json(d.data)}` : ''), { actions: `<button class="btn xs ghost" data-act="copy-result" data-id="${esc(r.id)}">Copy</button>` })}
      <div class="stack">
        ${imgs.length ? card('Images', `<div class="thumbs">${imgs.map(f => `<a href="/results/${enc(r.id)}/files?file=${enc(f.path)}" data-nav><img src="${resultFileUrl(r.id, f.path)}" alt="${esc(f.name)}" loading="lazy"></a>`).join('')}</div>`) : ''}
        ${card('Request', `<pre class="clip-pre">${esc(r.instructions || '')}</pre>`)}
        ${card('Rate this result', rateForm(r))}
      </div>
    </div>`;
}
function rateForm(r) {
  return `<form class="stack" data-form="rate-result" data-id="${esc(r.id)}">
      <div class="star-input" role="radiogroup" aria-label="Rating">${[1, 2, 3, 4, 5].map(i => `<button type="button" class="star ${i <= (r.rating || 0) ? 'on' : ''}" data-act="pick-star" data-n="${i}" aria-label="${i} star${i > 1 ? 's' : ''}">★</button>`).join('')}</div>
      <input type="hidden" name="rating" value="${r.rating || ''}">
      <label>Comment for the agents<textarea name="comment" rows="3" placeholder="What was good, what should be different next time? Every agent that worked on it remembers this.">${esc(r.ratingComment || '')}</textarea></label>
      <div class="row"><button class="btn primary">Save rating</button>${r.ratedBy ? `<span class="muted small">rated by ${esc(r.ratedBy)} ${esc(ago(r.ratedUtc))}</span>` : ''}</div>
    </form>`;
}
ACTIONS['pick-star'] = el => {
  const f = el.closest('form'), n = Number(el.dataset.n);
  $('input[name="rating"]', f).value = n;
  $$('.star', f).forEach((s, i) => s.classList.toggle('on', i < n));
};
FORMS['rate-result'] = async (f, data) => {
  if (!data.rating) throw new Error('Choose 1 to 5 stars.');
  await post(`/api/results/${enc(f.dataset.id)}/rate`, { rating: Number(data.rating), comment: data.comment || null });
  toast('Rating saved — the agents have been told', 'ok'); rerender();
};
ACTIONS['copy-result'] = async el => {
  const d = await get('/api/results/' + enc(el.dataset.id));
  try { await writeClipboardOrThrow(d.result.summary || ''); toast('Answer copied', 'ok'); } catch { toast('Copy is not available in this browser.', 'bad'); }
};
ACTIONS['archive-result'] = async el => {
  await post(`/api/results/${enc(el.dataset.id)}/archive`, { archived: el.dataset.archived === '1' });
  toast(el.dataset.archived === '1' ? 'Archived' : 'Restored', 'ok'); rerender();
};

// ------------------------------------------------------------------ files
function resultFiles(r) {
  const files = r.files || [];
  if (!files.length) return card('', emptyState('No files', 'The agents did not save any files for this result (artifacts or the shared results folder).'));
  const sel = S.params.query.get('file') || files[0].path;
  const groups = {};
  files.forEach(f => { const top = f.path.split('/')[0]; (groups[top] = groups[top] || []).push(f); });
  const label = { files: 'Shared folder', artifacts: 'Artifacts' };
  const tree = Object.entries(groups).map(([g, list]) => `<div class="tree-group"><div class="tree-head">${icon('layers')} ${esc(label[g] || g)} <span class="muted small">${list.length}</span></div>` +
    list.map(f => `<a href="#" class="tree-item ${f.path === sel ? 'active' : ''}" data-act="result-file" data-id="${esc(r.id)}" data-path="${esc(f.path)}">${fileBadge(f)}
      <span class="grow"><span class="name">${esc(f.path.split('/').slice(1).join('/'))}</span><span class="cell-sub">${esc(fileSize(f.sizeBytes))}${f.agentId ? ' · ' + esc(f.agentId) : ''}</span></span></a>`).join('') + '</div>').join('');
  setTimeout(() => showResultFile(r.id, sel, files), 0);
  return `<div class="file-browser section"><div class="card tree">${tree}</div><div class="card viewer" id="file-viewer"><div class="empty">Loading…</div></div></div>`;
}
ACTIONS['result-file'] = async el => {
  $$('.tree-item').forEach(x => x.classList.toggle('active', x === el));
  const d = S.resultCache && S.resultCache.id === el.dataset.id ? S.resultCache : null;
  history.replaceState({}, '', `/results/${enc(el.dataset.id)}/files?file=${enc(el.dataset.path)}`);
  await showResultFile(el.dataset.id, el.dataset.path, d ? d.files : null);
};
async function showResultFile(id, path, files) {
  const box = $('#file-viewer');
  if (!box) return;
  if (!files) files = (await get('/api/results/' + enc(id))).result.files;
  S.resultCache = { id, files };
  const f = files.find(x => x.path === path);
  if (!f) { box.innerHTML = emptyState('File not found', ''); return; }
  const head = `<div class="card-head"><h2>${esc(f.name)}</h2><span class="hint">${esc(f.mimeType)} · ${esc(fileSize(f.sizeBytes))}</span><div class="actions">
      ${f.kind === 'image' || f.mimeType === 'application/pdf' ? `<a class="btn xs" href="${resultFileUrl(id, path)}" target="_blank" rel="noopener">Open</a>` : ''}
      <a class="btn xs primary" href="${resultFileUrl(id, path, true)}">${icon('download')} Download</a></div></div>`;
  if (f.kind === 'image' && !/\.svg$/i.test(f.name)) { box.innerHTML = head + `<div class="card-body image-view"><img src="${resultFileUrl(id, path)}" alt="${esc(f.name)}"></div>`; return; }
  box.innerHTML = head + '<div class="card-body"><div class="empty">Reading the file…</div></div>';
  try {
    const pv = await get(`/api/results/${enc(id)}/preview?path=${enc(path)}`);
    const text = pv.text || '';
    box.innerHTML = head + `<div class="card-body">${pv.summary ? `<p class="muted small">${esc(pv.summary)}</p>` : ''}` +
      (text ? (/\.md$/i.test(f.name) ? `<div class="md final-answer">${markdown(text.slice(0, 200000))}</div>` : `<pre class="file-text">${/\.json$/i.test(f.name) ? jsonOr(text.slice(0, 200000)) : esc(text.slice(0, 200000))}</pre>`) : emptyState('No preview', 'Download the file to open it.')) + '</div>';
  } catch (e) { box.innerHTML = head + `<div class="card-body"><div class="notice bad">${esc(e.message)}</div></div>`; }
}

// ------------------------------------------------------------------ conversation
/** One line for a collapsed prompt: its first plain line. */
function promptTitle(text) {
  const line = (text || '').split('\n').map(l => l.trim()).find(l => l && !l.startsWith('#') && !l.startsWith('Task id:'));
  return (line ? line.slice(0, 110) : 'Prompt') + ' …';
}
function resultConversation(conv) {
  const msgs = conv.messages || [], tasks = conv.tasks || [];
  const ROLE = { work: 'Work', revision: 'Revision', review: 'Review', step: 'Step' };
  const thread = msgs.length ? `<div class="chat">${msgs.map(m => `<div class="bubble ${m.from === 'admin' ? 'me' : ''}">
      <div class="meta">${m.from ? agentLink(m.from) : 'host'} → ${m.to ? agentLink(m.to) : '—'} ${tag(m.type)} <span class="muted small">${esc(ts(m.createdUtc))}</span></div>
      ${m.subject ? `<b>${esc(m.subject)}</b>` : ''}<div class="md">${markdown(m.content || '')}</div></div>`).join('')}</div>` : emptyState('No messages', 'The agents did not message each other in this workflow.');
  const work = tasks.map((t, i) => {
    const turns = (t.transcript || []).filter(m => m.role !== 'system');
    return `<details class="task-thread"${i === tasks.length - 1 ? ' open' : ''}><summary><b>${esc(t.name)}</b> ${agentLink(t.agentId)} ${tag(ROLE[t.role] || t.role, t.role === 'review' ? 'accent' : t.role === 'revision' ? 'warn' : 'outline')} ${badge(t.status)}
        <span class="muted small">${t.completedUtc ? esc(ago(t.completedUtc)) : ''}${t.costMicros ? ' · ' + money(t.costMicros, 4) : ''}</span></summary>
      ${t.summary ? `<div class="notice info small"><b>Result.</b> ${esc(t.summary.slice(0, 1200))}</div>` : ''}
      <div class="chat">${turns.map(m => m.role === 'tool'
        ? `<details class="bubble tool"><summary><code>${esc(m.name || 'tool')}</code> result</summary><pre class="json">${jsonOr((m.content || '').slice(0, 6000))}</pre></details>`
        : m.role === 'user' ? `<details class="bubble user"><summary>${esc(promptTitle(m.content))}</summary><div class="md">${markdown((m.content || '').slice(0, 12000))}</div></details>`
        : `<div class="bubble ${m.role === 'assistant' ? 'agent' : 'user'}"><div class="meta">${m.role === 'assistant' ? esc(t.agentId) : 'task'}</div>${m.content ? `<div class="md">${markdown(m.content.slice(0, 12000))}</div>` : ''}${m.toolCalls ? `<div class="calls">${m.toolCalls.map(c => `<span class="badge outline" data-tip="${esc((c.arguments || '').slice(0, 500))}">${esc(c.name)}</span>`).join(' ')}</div>` : ''}</div>`).join('') ||
        '<div class="muted small">No conversation was kept for this task.</div>'}</div></details>`;
  }).join('');
  return `<div class="grid two section">${card('Between agents', thread, { hint: msgs.length + ' message(s)' })}${card('Each agent\'s work', work || emptyState('No tasks', ''), { hint: tasks.length + ' task(s)' })}</div>`;
}

// ------------------------------------------------------------------ review, agents & rewards
function resultReview(r) {
  return `<div class="grid side section">${card('Reviewer\'s verdict', `<div class="row">${verdictBadge(r.reviewVerdict)}${r.reviewScore ? ` <span class="big-score">${r.reviewScore}<span class="muted">/10</span></span>` : ''}</div>
      ${r.reviewScore ? meter(r.reviewScore / 10, 'score ' + r.reviewScore + '/10', r.reviewScore < 5 ? 'bad' : r.reviewScore < 7 ? 'warn' : '') : ''}
      ${r.reviewFeedback ? `<div class="md section">${markdown(r.reviewFeedback)}</div>` : '<p class="muted section">No feedback.</p>'}`)}
    ${card('How it was checked', `<dl class="kv"><dt>Reviewer</dt><dd>${r.reviewerAgentId ? agentLink(r.reviewerAgentId) : 'nobody'}</dd>
      <dt>Revisions</dt><dd>${num(r.revisions)}</dd><dt>Outcome</dt><dd>${r.success ? 'The work finished successfully.' : 'The work did not finish successfully.'}</dd></dl>
      <p class="muted small">The reviewer judged the work against your expected outcome, which the agents doing the work never saw.</p>`)}</div>`;
}
function resultAgents(r, d) {
  const people = d.participants || [];
  const rows = table([
    { h: 'Agent', f: a => `${agentLink(a.agentId, a.name)}<div class="cell-sub">${esc(a.role || '')}</div>` }, { h: 'State', f: a => a.state ? badge(a.state) : '—' },
    { h: 'Balance', num: true, f: a => a.balanceMicros === null || a.balanceMicros === undefined ? '—' : money(a.balanceMicros, 2) },
    { h: 'Awarded here', num: true, f: a => a.awardedMicros ? `<span class="ok-text">+${money(a.awardedMicros, 2)}</span>` : '—' },
    { h: 'Award now (USD)', f: a => `<input class="award-input" type="number" min="0" step="0.01" name="award:${esc(a.agentId)}" placeholder="0.00"${a.state === 'Dead' ? ' data-tip="Funding a dead agent revives it"' : ''}>` }
  ], people, { empty: 'No agents took part.' });
  const history = table([{ h: 'When', f: a => esc(ago(a.createdUtc)) }, { h: 'Agent', f: a => agentLink(a.agentId) }, { h: 'Amount', num: true, f: a => money(a.amountMicros, 2) },
    { h: 'Note', f: a => esc(a.note || '') }, { h: 'By', f: a => esc(a.createdBy) }], d.awards || [], { empty: 'No awards yet.' });
  return `<form class="section" data-form="award-result" data-id="${esc(r.id)}" data-key="${esc(newKey())}">${card('Reward the agents', rows +
      `<div class="form-grid section"><label class="full">Note (the agents remember it)<input name="note" maxlength="300" placeholder="e.g. Excellent research; the sources checked out."></label></div>
       <div class="row"><button class="btn primary">Pay awards</button><span class="muted small">Awards are profit credits: they raise the agents' balance, ROI and runway, and can revive an agent that died.</span></div>`, { flush: false })}</form>
    ${card('Awards paid for this result', history, { flush: true, cls: 'section' })}`;
}
FORMS['award-result'] = async (f, data) => {
  const awards = Object.entries(data).filter(([k, v]) => k.startsWith('award:') && Number(v) > 0).map(([k, v]) => ({ agentId: k.slice(6), amount: Number(v) }));
  if (!awards.length) throw new Error('Enter an amount for at least one agent.');
  const total = awards.reduce((s, a) => s + a.amount, 0);
  if (!await confirmBox('Pay awards', `Pay <b>$${total.toFixed(2)}</b> to ${awards.map(a => esc(a.agentId)).join(', ')}?`, { confirm: 'Pay' })) return;
  const r = await post(`/api/results/${enc(f.dataset.id)}/award`, { awards, note: data.note || null, idempotencyKey: f.dataset.key });
  toast(r.message, 'ok'); rerender();
};
