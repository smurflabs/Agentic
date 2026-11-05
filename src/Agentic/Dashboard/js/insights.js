'use strict';
/* Agentic console - charts beyond single bars (lines, stacked bars, bar lists, heat map, part-to-whole bar) and the
   data-dense Overview, Operations and Live events pages built on /api/insights. Series colors come from a validated
   categorical palette (--s1..--s4); every multi-series chart has a legend, every mark a tooltip, and tables back them. */

const SERIES = ['s1', 's2', 's3', 's4'];
function legend(series) {
  return `<div class="legend">${series.map((s, i) => `<span><i class="sw ${esc(s.cls || SERIES[i])}"></i>${esc(s.label)}</span>`).join('')}</div>`;
}
function hourLabelOf(iso) { return new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }); }
function moneyAxis(v) { return v === 0 ? '$0' : '$' + (v >= 1 ? v.toFixed(2) : String(Number(v.toPrecision(2)))); }
function numAxis(v) { return v >= 1000 ? (v / 1000).toFixed(v >= 10000 ? 0 : 1) + 'k' : String(Math.round(v)); }
function msAxis(v) { return v >= 1000 ? (v / 1000).toFixed(v >= 10000 ? 0 : 1) + 's' : Math.round(v) + 'ms'; }

/** Shared plot frame: gridlines with axis labels, returns geometry. */
function frame(el, spec, max) {
  const W = Math.max(240, el.clientWidth), H = spec.height || 170, L = 46, R = 8, T = 10, B = 22;
  const pw = W - L - R, ph = H - T - B;
  let g = '';
  [0, 0.5, 1].forEach(f => {
    const y = T + ph - f * ph;
    g += `<line class="gridline" x1="${L}" x2="${W - R}" y1="${y.toFixed(1)}" y2="${y.toFixed(1)}"></line>` +
      `<text class="axis-label" x="${L - 6}" y="${(y + 3.5).toFixed(1)}" text-anchor="end">${esc((spec.axis || numAxis)(max * f))}</text>`;
  });
  return { W, H, L, R, T, B, pw, ph, g };
}
function xLabels(spec, f, band) {
  let g = '';
  const every = spec.tickEvery || Math.max(1, Math.ceil(spec.rows.length / 8));
  spec.rows.forEach((r, i) => { if (i % every === 0) g += `<text class="axis-label" x="${(f.L + i * band + band / 2).toFixed(1)}" y="${f.H - 6}" text-anchor="middle">${esc(spec.x(r))}</text>`; });
  return g;
}

/** Multi-series line chart (first series optionally filled). spec: {rows, series:[{label, y, cls}], x, axis, tip, area} */
CHART_TYPES.line = (el, spec, grow) => {
  const vals = spec.series.map(s => spec.rows.map(s.y));
  const max = niceMax(Math.max(spec.minMax || 0, ...vals.flat()));
  const f = frame(el, spec, max);
  const n = spec.rows.length, band = f.pw / Math.max(1, n);
  const px = i => f.L + i * band + band / 2, py = v => f.T + f.ph - (max ? v / max * f.ph : 0);
  let g = f.g + xLabels(spec, f, band);
  spec.series.forEach((s, si) => {
    const pts = vals[si].map((v, i) => `${px(i).toFixed(1)},${py(v).toFixed(1)}`);
    if (si === 0 && spec.area && n > 1) g += `<path class="area ${s.cls || SERIES[si]}" d="M${px(0).toFixed(1)},${f.T + f.ph} L${pts.join(' L')} L${px(n - 1).toFixed(1)},${f.T + f.ph} Z"></path>`;
    g += `<polyline class="line ${s.cls || SERIES[si]}${grow ? ' draw' : ''}" points="${pts.join(' ')}"></polyline>`;
    if (n <= 40) vals[si].forEach((v, i) => { if (v > 0) g += `<circle class="dot ${s.cls || SERIES[si]}" cx="${px(i).toFixed(1)}" cy="${py(v).toFixed(1)}" r="2.6"></circle>`; });
  });
  spec.rows.forEach((r, i) => {
    const tip = spec.tip ? spec.tip(r) : spec.series.map((s, si) => `${s.label}: ${(spec.fmt || num)(vals[si][i])}`).join('\n');
    g += `<rect class="hit" x="${(f.L + i * band).toFixed(1)}" y="${f.T}" width="${band.toFixed(1)}" height="${f.ph}" data-tip="${esc((spec.x(r) + '\n' + tip).trim())}"></rect>`;
  });
  el.innerHTML = `<svg viewBox="0 0 ${f.W} ${f.H}" width="${f.W}" height="${f.H}" role="img" aria-label="${esc(spec.label)}">${g}</svg>`;
};

/** Stacked bars. spec: {rows, series:[{label, y, cls}], x, axis, tip} */
CHART_TYPES.stack = (el, spec, grow) => {
  const totals = spec.rows.map(r => spec.series.reduce((s, x) => s + (x.y(r) || 0), 0));
  const max = niceMax(Math.max(spec.minMax || 0, ...totals));
  const f = frame(el, spec, max);
  const band = f.pw / Math.max(1, spec.rows.length), bw = Math.max(2, band - 2);
  let g = f.g + xLabels(spec, f, band);
  spec.rows.forEach((r, i) => {
    const x = f.L + i * band + 1;
    let base = f.T + f.ph;
    spec.series.forEach((s, si) => {
      const v = s.y(r) || 0;
      if (v <= 0) return;
      const h = max ? v / max * f.ph : 0;
      // A 2px surface gap separates stacked segments.
      const gap = base < f.T + f.ph ? 2 : 0;
      if (h - gap > 0.4) g += `<rect class="seg ${s.cls || SERIES[si]}${grow ? ' grow' : ''}" x="${x.toFixed(1)}" y="${(base - h).toFixed(1)}" width="${bw.toFixed(1)}" height="${(h - gap).toFixed(1)}" rx="${Math.min(3, bw / 3).toFixed(1)}" data-delay="${100 + i * 12}"></rect>`;
      base -= h;
    });
    const tip = spec.tip ? spec.tip(r) : spec.series.map(s => `${s.label}: ${(spec.fmt || num)(s.y(r) || 0)}`).join('\n');
    g += `<rect class="hit" x="${(f.L + i * band).toFixed(1)}" y="${f.T}" width="${band.toFixed(1)}" height="${f.ph}" data-tip="${esc((spec.x(r) + '\n' + tip).trim())}"></rect>`;
  });
  el.innerHTML = `<svg viewBox="0 0 ${f.W} ${f.H}" width="${f.W}" height="${f.H}" role="img" aria-label="${esc(spec.label)}">${g}</svg>`;
  if (grow) $$(".seg.grow", el).forEach(s => { s.style.animationDelay = s.dataset.delay + "ms"; });
};

/** Ranked horizontal bars (HTML): items [{label, value, sub, href, cls}] */
function barList(items, opts = {}) {
  if (!items.length) return emptyState(opts.empty || 'No data yet', '');
  const max = Math.max(...items.map(i => i.value), 1e-9);
  const fmt = opts.fmt || num;
  return `<div class="barlist">${items.slice(0, opts.limit || 12).map(i => `<div class="bl-row"${i.tip ? ` data-tip="${esc(i.tip)}"` : ''}>
      <span class="bl-label">${i.href ? `<a href="${esc(i.href)}" data-nav>${esc(i.label)}</a>` : esc(i.label)}${i.sub ? `<span class="cell-sub">${i.sub}</span>` : ''}</span>
      <span class="bl-bar"><span class="${esc(i.cls || '')}" data-w="${(i.value / max * 100).toFixed(1)}"></span>${i.part ? `<span class="part bad" data-w="${(i.part / max * 100).toFixed(1)}"></span>` : ''}</span>
      <span class="bl-value">${fmt(i.value)}${i.extra ? ` <span class="muted small">${i.extra}</span>` : ''}</span></div>`).join('')}</div>`;
}
/** One 100% bar showing parts of a whole, with a legend (up to 5 parts; the rest is "Other"). */
function mixBar(items, opts = {}) {
  const sorted = items.filter(i => i.count > 0).sort((a, b) => b.count - a.count);
  if (!sorted.length) return emptyState(opts.empty || 'Nothing yet', '');
  const top = sorted.slice(0, 5), rest = sorted.slice(5).reduce((s, i) => s + i.count, 0);
  if (rest) top.push({ key: 'Other', count: rest, cls: 'muted-seg' });
  const total = top.reduce((s, i) => s + i.count, 0);
  const cls = (i, k) => i.cls || (opts.status && i.key in STATE_CLASS ? 'st-' + STATE_CLASS[i.key] : 'mx' + k);
  return `<div class="mixbar">${top.map((i, k) => `<span class="${cls(i, k)}" data-flex="${i.count}" data-tip="${esc(i.key)}: ${num(i.count)} (${Math.round(i.count / total * 100)}%)"></span>`).join('')}</div>
    <div class="legend">${top.map((i, k) => `<span><i class="sw ${cls(i, k)}"></i>${esc(i.key)} <b>${num(i.count)}</b> <span class="muted">${Math.round(i.count / total * 100)}%</span></span>`).join('')}</div>`;
}
/** Agents × hours activity grid, one hue, darker = busier. */
function heatmap(agents, hours) {
  const max = Math.max(1, ...agents.flatMap(a => a.activity));
  const head = `<div class="hm-row hm-head"><span class="hm-label"></span>${hours.map((h, i) => `<span class="hm-cell">${i % 3 === 0 ? esc(hourLabelOf(h.bucketUtc).slice(0, 2)) : ''}</span>`).join('')}</div>`;
  const rows = agents.map(a => `<div class="hm-row"><span class="hm-label">${hostName(a.agentId) ? `<span class="muted">${esc(hostName(a.agentId))}</span>` : agentLink(a.agentId, a.name)}</span>${a.activity.map((v, i) =>
    `<span class="hm-cell v" data-a="${v ? (0.18 + 0.82 * v / max).toFixed(2) : 0}" data-tip="${esc(a.name)} · ${esc(hourLabelOf(hours[i].bucketUtc))}\n${num(v)} actions (AI calls, tool calls, tasks)"></span>`).join('')}</div>`).join('');
  return `<div class="heatmap">${head}${rows}</div><div class="legend small"><span>Less</span><span class="hm-scale"></span><span>More</span></div>`;
}
/** Tiny trend line for KPI cards (no axes). */
function sparkline(values, cls = 's1') {
  if (!values || values.length < 2) return '';
  const max = Math.max(...values, 1e-9), w = 100, h = 24;
  const pts = values.map((v, i) => `${(i / (values.length - 1) * w).toFixed(1)},${(h - 2 - v / max * (h - 4)).toFixed(1)}`).join(' ');
  return `<svg class="spark" viewBox="0 0 ${w} ${h}" preserveAspectRatio="none" aria-hidden="true"><polyline class="line ${cls}" points="${pts}"></polyline></svg>`;
}
/** The page's Content-Security-Policy forbids style attributes in markup, so sizes travel as data-* and are set here. */
function applyDataStyles(root) {
  $$('[data-w]', root).forEach(e => { e.style.width = e.dataset.w + '%'; });
  $$('[data-flex]', root).forEach(e => { e.style.flex = e.dataset.flex; });
  $$('[data-a]', root).forEach(e => { e.style.setProperty('--a', e.dataset.a); });
}
const baseApplyMeters = applyMeters;
applyMeters = function (root, animate) { // eslint-disable-line no-func-assign
  baseApplyMeters(root, animate);
  applyDataStyles(root);
};
/** Host-internal actors (coordinator, learning) are not agents. */
function hostName(id) { return /^host\./.test(id || '') ? id.slice(5) + ' (host)' : null; }
function pct(a, b) { return b ? Math.round(a / b * 100) + '%' : '—'; }
function sum(rows, k) { return rows.reduce((s, r) => s + (r[k] || 0), 0); }

// ================================================================== Overview
VIEWS.overview = {
  live: true,
  async render() {
    setCrumbs([['Overview', '/']]);
    const [st, agents, ops, ins] = await Promise.all([get('/api/status'), get('/api/agents?details=1'), get('/api/operations'), get('/api/insights?hours=24')]);
    const by = st.agents.byState || {}, n = k => by[k] || 0;
    const active = n('Running') + n('Idle') + n('Waiting') + n('Starting');
    const spent = st.economy.globalSpentTodayMicros, limit = st.economy.globalDailyLimit;
    const t = ops.totals, h = ins.hourly;
    const done = sum(h, 'tasksCompleted'), failed = sum(h, 'tasksFailed'), ai = sum(h, 'aiCalls'), aiFail = sum(h, 'aiFailures');
    const local = (st.ai.localQueue || [])[0];
    const dead = st.deadAgents || [];
    const attention = [];
    dead.forEach(id => attention.push(['bad', `${id} is dead — out of money`, 'Credit its wallet (Finance or the agent\'s Budget tab) to revive it.', '/agents/' + enc(id) + '/budget']));
    agents.filter(a => ['Error', 'Unhealthy', 'Stuck'].includes(a.state) || a.unhealthy).forEach(a => attention.push(['bad', `${a.name} is ${a.state}`, a.lastError || '', '/agents/' + enc(a.id)]));
    if (st.ai.provider && !st.ai.provider.reachable) attention.push(['bad', 'AI provider unreachable', st.ai.provider.errorMessage || '', '/models']);
    if (st.ai.provider && st.ai.provider.missingModels && st.ai.provider.missingModels.length) attention.push(['warn', 'Models missing at the provider', st.ai.provider.missingModels.join(', '), '/models']);
    ops.models.filter(m => m.circuit !== 'Closed').forEach(m => attention.push(['warn', `Model ${m.key}: circuit ${m.circuit}`, m.lastError || '', '/models']));
    if (st.newResults) attention.push(['info', plural(st.newResults, 'new result') + ' to look at', 'Rate them and reward the agents.', '/results']);
    if (st.pendingApprovals) attention.push(['warn', plural(st.pendingApprovals, 'question') + ' waiting for you', 'Agents are paused on these until you answer.', '/approvals']);
    if (t.held) attention.push(['warn', plural(t.held, 'held task'), 'Held tasks do not run until released.', '/operations']);
    agents.filter(a => a.lowRunway && a.state !== 'Dead').forEach(a => attention.push([a.runwayDays < 1 ? 'bad' : 'warn', `${a.name}: money runs out in ~${runwayInfo(a.runwayDays, true).text}`,
      `${money(a.availableMicros, 2)} left. It dies when the balance runs out; top it up or lower its spending.`, '/agents/' + enc(a.id) + '/budget']));
    ops.recentFailures.slice(0, 4).forEach(f => attention.push(['bad', `Failed: ${f.name}`, `${f.agentId} · ${f.error || ''}`, '/agents/' + enc(f.agentId) + '/history']));

    const throughput = chartBox('ov-thru', { type: 'stack', rows: h, x: r => hourLabelOf(r.bucketUtc), label: 'Tasks finished per hour',
      series: [{ label: 'Completed', y: r => r.tasksCompleted, cls: 's1' }, { label: 'Failed', y: r => r.tasksFailed, cls: 'st-bad' }], minMax: 4 });
    const activity = chartBox('ov-act', { type: 'line', rows: h, x: r => hourLabelOf(r.bucketUtc), label: 'AI calls, tool calls and messages per hour', area: true,
      series: [{ label: 'AI calls', y: r => r.aiCalls }, { label: 'Tool calls', y: r => r.toolCalls }, { label: 'Messages', y: r => r.messagesSent }], minMax: 4 });
    const spend = chartBox('ov-cost', { rows: h, y: r => r.costMicros / 1e6, x: r => hourLabelOf(r.bucketUtc), minMax: 0.05, axis: moneyAxis, label: 'Cloud spend per hour',
      tip: r => `${hourLabelOf(r.bucketUtc)}\n${money(r.costMicros)} · ${num(r.tokens)} tokens` });
    const daily = chartBox('ov-daily', { type: 'stack', rows: ins.daily, x: r => r.day.slice(5), label: 'Tasks per day, last 14 days', tickEvery: 2,
      series: [{ label: 'Completed', y: r => r.tasksCompleted, cls: 's1' }, { label: 'Failed', y: r => r.tasksFailed, cls: 'st-bad' }],
      tip: r => `${num(r.tasksCompleted)} completed · ${num(r.tasksFailed)} failed\n${num(r.aiCalls)} AI calls · ${money(r.costMicros, 2)}` });
    const wallets = barList(ins.wallets.slice().sort((a, b) => b.availableMicros - a.availableMicros).map(w => ({
      label: w.agentId, href: '/agents/' + enc(w.agentId) + '/budget', value: Math.max(0, w.availableMicros) / 1e6,
      cls: w.dead ? 'st-bad' : w.lowRunway ? 'st-warn' : 's2',
      sub: w.dead ? '<span class="error-text">dead</span>' : w.runwayDays !== null && w.runwayDays !== undefined ? 'lasts ' + esc(runwayInfo(w.runwayDays, w.lowRunway).text) : 'no recent spend',
      tip: `${w.agentId}: ${money(w.availableMicros, 2)} · spends ${money(w.burnRateMicrosPerDay, 3)}/day · lifetime ${money(w.lifetimeSpendMicros, 2)} spent, ${money(w.lifetimeProfitMicros, 2)} earned`
    })), { fmt: v => '$' + v.toFixed(2), limit: 20, empty: 'No wallets' });
    const verdicts = (ins.verdicts || []).map(v => ({ key: VERDICT_LABEL[v.key] || v.key, count: v.count, cls: v.key === 'Approved' ? 'st-ok' : v.key === 'Rejected' ? 'st-bad' : 'muted-seg' }));

    return pageHead('Overview', {
      subtitle: `${esc(st.name)} · ${plural(st.agents.total, 'agent')} · up ${uptime(st.uptimeSeconds)} · last ${ins.hours} hours`,
      actions: `${link('/operations', 'Operations center', 'btn')}${link('/results', 'Results', 'btn')}<a href="/agents/new" data-nav class="btn primary">New agent</a>`
    }) + `<div class="grid kpis">
      ${kpi('Agents alive', `${active}<span class="muted small"> / ${st.agents.total}</span>`, `${n('Paused')} paused · ${n('Stopped') + n('Disabled')} stopped${dead.length ? ` · <span class="error-text">${dead.length} dead</span>` : ''}`, { href: '/agents' })}
      ${kpi('Running now', num(t.running), `${num(t.subagents)} subagents`, { href: '/operations' })}
      ${kpi('Queued', num(t.queued), `${num(t.waiting)} waiting · ${num(t.held)} held`, { href: '/operations' })}
      ${kpi('Tasks done (24 h)', num(done), `${pct(done, done + failed)} succeeded`, { bar: sparkline(h.map(r => r.tasksCompleted)) })}
      ${kpi('AI calls (24 h)', num(ai), `${pct(aiFail, ai)} failed · ${num(sum(h, 'tokens'))} tokens`, { bar: sparkline(h.map(r => r.aiCalls), 's2') })}
      ${kpi('Local model', local ? `${local.slotsInUse}/${local.slotsCapacity}` : '—', local ? `${local.queueLength} queued · ${esc(local.key)}` : '', { bar: local ? meter(local.slotsInUse / local.slotsCapacity, 'slots in use') : '' })}
      ${kpi('Cloud spend today', money(spent, 2), limit ? `of ${usd(limit)} daily cap` : 'no global cap', { href: '/finance', bar: limit ? meter(spent / 1e6 / limit, 'share of the global daily cap', spent / 1e6 > limit * 0.8 ? 'warn' : '') : sparkline(h.map(r => r.costMicros), 's3') })}
      ${kpi('Results & inbox', `${num(st.newResults || 0)}<span class="muted small"> new</span>`, `${num(st.pendingApprovals)} question(s) waiting`, { href: '/results' })}
    </div>
    <div class="grid two section">
      ${card('Tasks finished per hour', `<div class="chart-meta"><span class="value">${num(done)}</span><span class="muted small">${num(failed)} failed · ${num(sum(h, 'tasksRetried'))} retried</span></div>${legend([{ label: 'Completed', cls: 's1' }, { label: 'Failed', cls: 'st-bad' }])}${throughput}`, { actions: `<button class="btn xs ghost" data-act="insight-table" data-what="hourly">Table</button>` })}
      ${card('Activity per hour', `<div class="chart-meta"><span class="value">${num(ai)}</span><span class="muted small">AI calls · ${num(sum(h, 'toolCalls'))} tool calls · ${num(sum(h, 'messagesSent'))} messages</span></div>${legend([{ label: 'AI calls' }, { label: 'Tool calls' }, { label: 'Messages' }])}${activity}`)}
    </div>
    <div class="grid two section">
      ${card('Cloud spend per hour', `<div class="chart-meta"><span class="value">${money(sum(h, 'costMicros'), 2)}</span><span class="muted small">last ${ins.hours} h · the local model is free</span></div>${spend}`)}
      ${card('Last 14 days', `<div class="chart-meta"><span class="value">${num(sum(ins.daily, 'tasksCompleted'))}</span><span class="muted small">tasks · ${money(sum(ins.daily, 'costMicros'), 2)} spent</span></div>${legend([{ label: 'Completed', cls: 's1' }, { label: 'Failed', cls: 'st-bad' }])}${daily}`)}
    </div>
    <div class="grid side section">
      ${card('Agents', `<div class="agent-grid">${agents.map(agentCardHtml).join('') || emptyState('No agents', 'Create one to get started.')}</div>`, { actions: link('/agents', 'Manage agents →') })}
      <div class="stack">
        ${card('Needs attention', attention.length ? `<div class="stack">${attention.slice(0, 12).map(([cls, title, text, href]) =>
          `<a class="notice ${cls}" href="${esc(href)}" data-nav><b>${esc(title)}</b>${text ? `<div class="small">${esc(text.slice(0, 160))}</div>` : ''}</a>`).join('')}</div>` : emptyState('All clear', 'Nothing needs your attention.'))}
        ${card('Money & survival', wallets, { hint: 'Balance; agents die when it runs out', actions: link('/finance', 'Finance →') })}
      </div>
    </div>
    ${card('Who is working', heatmap(ins.agents, h), { cls: 'section', hint: `actions per agent per hour, last ${ins.hours} h` })}
    <div class="grid three section">
      ${card('Model usage', barList(ins.models.map(m => ({ label: m.model, value: m.calls, part: m.failures, extra: m.costMicros ? money(m.costMicros, 2) : 'free', tip: `${m.model}: ${num(m.calls)} calls, ${num(m.failures)} failed · p50 ${dur(m.p50Ms)} · p95 ${dur(m.p95Ms)} · ${num(m.tokens)} tokens` }))), { hint: 'calls · failures in red' })}
      ${card('Tasks by status', mixBar(ins.taskMix, { status: true }), { hint: 'created in the window or still open' })}
      ${card('Results', mixBar(verdicts, { empty: 'No results yet' }) + `<dl class="kv section small">${(ins.results || []).map(r => `<dt>${esc(r.status)}</dt><dd>${num(r.count)}${r.avgRating ? ' · ' + r.avgRating.toFixed(1) + '★' : ''}${r.awardedMicros ? ' · ' + money(r.awardedMicros, 2) + ' awarded' : ''}</dd>`).join('')}</dl>`, { actions: link('/results', 'Open →') })}
    </div>
    ${card('Live activity', `<div id="live-feed" class="feed">${S.feed.filter(e => !isRoutineEvent(e)).slice(0, 120).map(e => feedRow(e)).join('')}</div>`, { flush: true, cls: 'section', actions: link('/events', 'All events →') })}`;
  }
};
ACTIONS['insight-table'] = async () => {
  const ins = await get('/api/insights?hours=24');
  openModal('Hourly statistics (last 24 hours)', table([
    { h: 'Hour', f: r => esc(new Date(r.bucketUtc).toLocaleString()) }, { h: 'Completed', num: true, f: r => num(r.tasksCompleted) }, { h: 'Failed', num: true, f: r => num(r.tasksFailed) },
    { h: 'Retried', num: true, f: r => num(r.tasksRetried) }, { h: 'AI calls', num: true, f: r => num(r.aiCalls) }, { h: 'AI failed', num: true, f: r => num(r.aiFailures) },
    { h: 'Avg AI', num: true, f: r => dur(r.avgAiMs) }, { h: 'Tool calls', num: true, f: r => num(r.toolCalls) }, { h: 'Messages', num: true, f: r => num(r.messagesSent) },
    { h: 'Avg task', num: true, f: r => dur(r.avgTaskMs) }, { h: 'Tokens', num: true, f: r => num(r.tokens) }, { h: 'Cloud spend', num: true, f: r => money(r.costMicros) }
  ], ins.hourly.slice().reverse()), '', 'wide');
};

// ================================================================== Operations
VIEWS.operations = {
  live: true,
  async render() {
    setCrumbs([['Operations', '/operations']]);
    const [ops, ins] = await Promise.all([get('/api/operations'), get('/api/insights?hours=24')]);
    const t = ops.totals, h = ins.hourly;
    const running = table([
      { h: 'Agent', f: a => agentLink(a.id, a.name) + (a.isSubagent ? ` ${tag('subagent', 'info')}` : '') + `<div class="cell-sub">${esc(a.isSubagent ? 'parent ' + a.parentAgentId : a.role || '')}</div>` },
      { h: 'Task', cls: 'clip', f: a => `<a href="#" class="cell-title" data-act="open-task" data-id="${esc(a.id)}" data-task="${esc(a.currentTaskId)}">${esc(a.currentTaskName)}</a>` },
      { h: 'Current operation', cls: 'clip', f: a => esc(a.currentOperation || '—') },
      { h: 'For', num: true, f: a => esc(a.currentOperationStartedUtc ? dur(Date.now() - new Date(a.currentOperationStartedUtc).getTime()) : '—') },
      { h: '', cls: 'act', f: a => `<button class="btn xs danger" data-act="cancel-task" data-id="${esc(a.id)}" data-task="${esc(a.currentTaskId)}">Cancel</button> ${a.state === 'Paused' ? '' : `<button class="btn xs" data-act="agent-control" data-id="${esc(a.id)}" data-op="pause">Pause agent</button>`}` }
    ], ops.running, { empty: 'Nothing is running', emptyText: 'Agents are idle or waiting.', key: a => a.id + '|' + a.currentTaskId, rowClass: () => 'is-running' });
    const waiting = table([
      { h: 'Task', cls: 'clip', f: x => taskCell(x, { noMeta: true }) }, { h: 'Agent', f: x => agentLink(x.agentId) },
      { h: 'Waiting on', cls: 'clip', f: x => (x.waitingOn || []).map(k => tag(k.split(':')[0])).join(' ') }, { h: 'Deadline', f: x => esc(ago(x.waitDeadlineUtc)) },
      { h: '', cls: 'act', f: x => `<button class="btn xs danger" data-act="cancel-task" data-id="${esc(x.agentId)}" data-task="${esc(x.id)}">Cancel</button>` }
    ], ops.waiting, { empty: 'Nothing is waiting.' });
    const held = table([
      { h: 'Task', cls: 'clip', f: x => taskCell(x, { noMeta: true }) }, { h: 'Agent', f: x => agentLink(x.agentId) }, { h: 'Status', f: statusCell },
      { h: '', cls: 'act', f: x => taskActions(x) }
    ], ops.held, { empty: 'No held or blocked tasks.' });
    const maxQ = Math.max(1, ...ops.queues.map(q => q.running + q.queued + q.waiting + q.held));
    const queues = table([
      { h: 'Agent', f: q => agentLink(q.agentId, q.name) }, { h: 'State', f: q => badge(q.state) },
      { h: 'Load', f: q => `<span class="qbar">${[['running', 's1'], ['queued', 's2'], ['waiting', 's3'], ['held', 'st-warn']].map(([k, c]) => q[k] ? `<span class="${c}" data-w="${(q[k] / maxQ * 100).toFixed(1)}" data-tip="${q[k]} ${k}"></span>` : '').join('')}</span>` },
      { h: 'Running', num: true, f: q => num(q.running) }, { h: 'Queued', num: true, f: q => num(q.queued) }, { h: 'Waiting', num: true, f: q => num(q.waiting) }, { h: 'Held', num: true, f: q => num(q.held) },
      { h: '', cls: 'act', f: q => link('/agents/' + enc(q.agentId) + '/queue', 'Open queue', 'btn xs') }
    ], ops.queues, { key: q => q.agentId, rowAttrs: q => `data-state="${esc(q.state)}"` });
    const statByModel = Object.fromEntries(ins.models.map(m => [m.model, m]));
    const maxP95 = Math.max(1, ...ins.models.map(m => m.p95Ms));
    const models = table([
      { h: 'Model', f: m => `<code>${esc(m.key)}</code>${m.isCustom ? ' ' + tag('custom', 'accent') : ''}` }, { h: 'Circuit', f: m => badge(m.circuit) },
      { h: 'Slots', cls: 'nw', f: m => `${slots(m.slotsInUse, Math.min(m.slotsCapacity, 10))} <span class="muted small">${m.slotsInUse}/${m.slotsCapacity}</span>` },
      { h: 'Queued', num: true, f: m => num(m.queueLength) },
      { h: 'Calls 24 h', num: true, f: m => statByModel[m.key] ? `${num(statByModel[m.key].calls)}${statByModel[m.key].failures ? ` <span class="error-text small">${num(statByModel[m.key].failures)} failed</span>` : ''}` : '—' },
      { h: 'Latency p50 / p95', f: m => { const s = statByModel[m.key]; return s ? `<span class="lat"><span class="p95" data-w="${(s.p95Ms / maxP95 * 100).toFixed(1)}"></span><span class="p50" data-w="${(s.p50Ms / maxP95 * 100).toFixed(1)}"></span></span><span class="small">${dur(s.p50Ms)} / ${dur(s.p95Ms)}</span>` : '—'; } },
      { h: 'Queue wait', num: true, f: m => statByModel[m.key] ? `${dur(statByModel[m.key].avgWaitMs)} <span class="muted small">p95 ${dur(statByModel[m.key].p95WaitMs)}</span>` : '—' }
    ], ops.models.filter(m => m.enabled), { key: m => m.key, rowAttrs: m => `data-state="${esc(m.circuit)}"` });
    const tools = table([
      { h: 'Tool', f: x => `<code>${esc(x.tool)}</code> ${tag(x.source)}` }, { h: 'Calls', num: true, f: x => num(x.calls) },
      { h: 'Failure rate', f: x => `${meter(x.calls ? x.failures / x.calls : 0, `${x.failures} of ${x.calls} failed`, x.failures / Math.max(1, x.calls) > 0.2 ? 'bad' : x.failures ? 'warn' : '')}<span class="small">${pct(x.failures, x.calls)}</span>` },
      { h: 'Avg time', num: true, f: x => dur(x.avgMs) }
    ], ins.tools, { empty: 'No tool calls in the last 24 hours.' });
    const latency = chartBox('op-lat', { type: 'line', rows: h, x: r => hourLabelOf(r.bucketUtc), label: 'Average AI call and task duration per hour', axis: msAxis, fmt: dur,
      series: [{ label: 'Avg AI call', y: r => r.avgAiMs }, { label: 'Avg task', y: r => r.avgTaskMs }] });
    const reliability = chartBox('op-rel', { type: 'stack', rows: h, x: r => hourLabelOf(r.bucketUtc), label: 'AI call outcomes per hour', minMax: 4,
      series: [{ label: 'AI calls OK', y: r => r.aiCalls - r.aiFailures, cls: 's1' }, { label: 'AI calls failed', y: r => r.aiFailures, cls: 'st-bad' }] });
    const perAgent = barList(ins.agents.filter(a => a.tasksCompleted + a.tasksFailed + a.aiCalls > 0).map(a => ({
      label: a.name, href: '/agents/' + enc(a.agentId), value: a.tasksCompleted + a.tasksFailed, part: a.tasksFailed,
      extra: `${num(a.aiCalls)} AI · ${money(a.costMicros, 2)}`, tip: `${a.name}: ${num(a.tasksCompleted)} completed, ${num(a.tasksFailed)} failed, ${num(a.aiCalls)} AI calls, ${num(a.toolCalls)} tool calls, ${num(a.tokens)} tokens`
    })), { empty: 'No work in the last 24 hours.' });
    return pageHead('Operations center', {
      subtitle: 'Everything running, waiting or held right now, how fast it moves and where it fails — with full control.',
      actions: `<button class="btn" data-act="global" data-op="pause-all">Pause all</button><button class="btn" data-act="global" data-op="resume-all">Resume all</button>` +
        `<button class="btn danger-solid" data-act="emergency-stop">Emergency stop</button>`
    }) + `<div class="grid kpis">
        ${kpi('Running', num(t.running), 'tasks in progress')}${kpi('Queued', num(t.queued), 'ready to run')}${kpi('Waiting', num(t.waiting), 'on agents, subagents or people')}
        ${kpi('Held', num(t.held), 'held or blocked')}${kpi('Subagents', num(t.subagents), 'running')}${kpi('Paused agents', num(ops.paused.length), ops.paused.slice(0, 3).join(', '))}
        ${kpi('AI failure rate', pct(sum(h, 'aiFailures'), sum(h, 'aiCalls')), `${num(sum(h, 'aiFailures'))} of ${num(sum(h, 'aiCalls'))} calls (24 h)`)}
        ${kpi('Tool failure rate', pct(sum(h, 'toolFailures'), sum(h, 'toolCalls')), `${num(sum(h, 'toolFailures'))} of ${num(sum(h, 'toolCalls'))} calls (24 h)`)}
        ${kpi('Inbox', num(ops.pendingApprovals), 'awaiting your answer', { href: '/approvals' })}
      </div>
      ${card('Running now', running, { flush: true, cls: 'section' })}
      <div class="grid two section">
        ${card('Speed', `${legend([{ label: 'Avg AI call' }, { label: 'Avg task' }])}${latency}`, { hint: 'per hour, last 24 h' })}
        ${card('AI reliability', `${legend([{ label: 'OK', cls: 's1' }, { label: 'Failed', cls: 'st-bad' }])}${reliability}`, { hint: 'calls per hour' })}
      </div>
      ${card('Active subagents', subagentTable(ops.subagents, { compact: true }), { flush: true, cls: 'section', actions: link('/subagents', 'All subagents →') })}
      <div class="grid two section">${card('Waiting', waiting, { flush: true })}${card('Held & blocked', held, { flush: true })}</div>
      ${card('Queues by agent', queues, { flush: true, cls: 'section', hint: `${legend([{ label: 'running', cls: 's1' }, { label: 'queued', cls: 's2' }, { label: 'waiting', cls: 's3' }, { label: 'held', cls: 'st-warn' }])}` })}
      ${card('Model capacity & latency', models, { flush: true, cls: 'section', actions: link('/models', 'Models →') })}
      <div class="grid three section">
        ${card('Work per agent (24 h)', perAgent, { hint: 'tasks · failures in red' })}
        ${card('Why each model was used', mixBar(ins.reasons.map(r => ({ key: r.key, count: r.count }))) + '<div class="section"></div>' + barList(ins.purposes.map(p => ({ label: p.key, value: p.count }))), { hint: 'routing reason and purpose' })}
        ${card('Messages between agents', mixBar(ins.messageMix) + `<dl class="kv section small">${ins.messages.filter(m => m.status === 'Rejected' || m.status === 'Expired').map(m => `<dt>${esc(m.type)}</dt><dd>${num(m.count)} ${esc(m.status.toLowerCase())}</dd>`).join('') || '<dt>Problems</dt><dd>none</dd>'}</dl>`, { actions: link('/communications', 'Open →') })}
      </div>
      ${card('Tool reliability (24 h)', tools, { flush: true, cls: 'section' })}
      ${card('Failures in the last 24 hours', taskTable(ops.recentFailures, { empty: 'No failures in the last 24 hours.' }), { flush: true, cls: 'section' })}`;
  }
};

// ================================================================== Live events
const EVT = { paused: false, type: '', level: '', q: '' };
VIEWS.events = {
  live: false,
  async render(p) {
    setCrumbs([['Live events', '/events']]);
    const agent = p.query.get('agent') || '';
    const [recent, ins] = await Promise.all([get('/api/events/recent?limit=300' + (agent ? '&agent=' + enc(agent) : '')), get('/api/insights?hours=24')]);
    const m = ins.eventsPerMinute;
    const lastHour = m.reduce((s, x) => s + x.info + x.warning + x.error, 0);
    const warns = m.reduce((s, x) => s + x.warning, 0), errs = m.reduce((s, x) => s + x.error, 0);
    const now5 = m.slice(-5).reduce((s, x) => s + x.info + x.warning + x.error, 0) / 5;
    const rate = chartBox('ev-rate', { type: 'stack', rows: m, x: r => new Date(r.minuteUtc).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }), tickEvery: 10, label: 'Events per minute, last hour', minMax: 4,
      series: [{ label: 'Info', y: r => r.info, cls: 's1' }, { label: 'Warning', y: r => r.warning, cls: 'st-warn' }, { label: 'Error', y: r => r.error, cls: 'st-bad' }] });
    const types = [...new Set(S.feed.map(e => e.type).concat(ins.eventTypes.map(t => t.key)))].sort();
    const levelCounts = Object.fromEntries((ins.eventLevels || []).map(l => [l.key, l.count]));
    return pageHead('Live events', {
      subtitle: 'Everything the host and the agents do, as it happens.',
      actions: `<button class="btn" data-act="events-pause">${EVT.paused ? icon('play') + ' Resume stream' : icon('pause') + ' Pause stream'}</button>`
    }) + `<div class="grid kpis">
        ${kpi('Events (last hour)', num(lastHour), `${now5.toFixed(1)} per minute now`, { bar: sparkline(m.map(x => x.info + x.warning + x.error)) })}
        ${kpi('Warnings (last hour)', num(warns), `${num(levelCounts.Warning || 0)} in 24 h`, { bar: sparkline(m.map(x => x.warning), 'st-warn') })}
        ${kpi('Errors (last hour)', num(errs), `${num(levelCounts.Error || 0)} in 24 h`, { bar: sparkline(m.map(x => x.error), 'st-bad') })}
        ${kpi('Event types (24 h)', num(ins.eventTypes.length), ins.eventTypes[0] ? `most: ${esc(ins.eventTypes[0].key)}` : '')}
        ${kpi('Most active agent', ins.eventAgents[0] ? `<span class="kpi-text">${esc(hostName(ins.eventAgents[0].agentId) || ins.eventAgents[0].agentId)}</span>` : '—', ins.eventAgents[0] ? num(ins.eventAgents[0].count) + ' events (24 h)' : '')}
      </div>
      ${card('Events per minute', `${legend([{ label: 'Info', cls: 's1' }, { label: 'Warning', cls: 'st-warn' }, { label: 'Error', cls: 'st-bad' }])}${rate}`, { cls: 'section', hint: 'last 60 minutes' })}
      <div class="grid two section">
        ${card('By type (24 h)', barList(ins.eventTypes.map(t => ({ label: t.key, value: t.count, part: t.warnings, tip: `${t.key}: ${num(t.count)} events, ${num(t.warnings)} warnings or errors` })), { limit: 14 }), { hint: 'warnings and errors in red' })}
        ${card('By agent (24 h)', barList(ins.eventAgents.map(a => ({ label: hostName(a.agentId) || a.agentId, href: hostName(a.agentId) ? null : '/events?agent=' + enc(a.agentId), value: a.count })), { limit: 14 }), { hint: 'click to filter' })}
      </div>
      ${card('Live', `<form class="filters" data-form="events-filter">
          <label>Agent<input name="agent" value="${esc(agent)}" placeholder="any"></label>
          <label>Type<select name="type" data-change="events-live-filter"><option value="">any</option>${types.map(x => `<option${x === EVT.type ? ' selected' : ''}>${esc(x)}</option>`).join('')}</select></label>
          <label>Level<select name="level" data-change="events-live-filter"><option value="">any</option>${['Info', 'Warning', 'Error'].map(x => `<option${x === EVT.level ? ' selected' : ''}>${x}</option>`).join('')}</select></label>
          <label>Contains<input name="q" value="${esc(EVT.q)}" data-change="events-live-filter" placeholder="text"></label>
          <button class="btn">Apply</button></form>
        <div id="live-feed" class="feed tall" data-routine="1" data-agent="${esc(agent)}" data-type="${esc(EVT.type)}" data-level="${esc(EVT.level)}" data-q="${esc(EVT.q)}">${S.feed.filter(e => eventVisible(e, agent)).slice(0, 200).map(e => feedRow(e)).join('')}</div>`,
        { flush: true, cls: 'section', hint: EVT.paused ? '<span class="badge warn">paused</span>' : '<span class="badge ok dot live">streaming</span>' })}
      ${card('Persisted history', table([{ h: 'When', f: e => esc(ts(e.createdUtc)) }, { h: 'Agent', f: e => e.agentId ? agentLink(e.agentId) : 'host' },
        { h: 'Event', f: e => tag(e.eventType) }, { h: 'Level', f: e => badge(e.level, e.level === 'Info' ? 'outline' : (e.level === 'Error' ? 'bad' : 'warn'), false) }, { h: 'Summary', f: e => esc(e.summary) },
        { h: '', f: e => e.correlationId ? link('/correlations/' + enc(e.correlationId), 'workflow') : '' }], recent), { flush: true, export: 'events' })}`;
  }
};
function eventVisible(e, agent) {
  if (agent && e.agentId !== agent && !(e.agentId || '').startsWith(agent + '.sub-')) return false;
  if (EVT.type && e.type !== EVT.type) return false;
  if (EVT.level && e.level !== EVT.level) return false;
  if (EVT.q && !`${e.summary} ${e.agentId} ${e.type}`.toLowerCase().includes(EVT.q.toLowerCase())) return false;
  return true;
}
// The live stream (core.js) asks feedMatches(); honour the page's extra filters and the pause switch.
const baseFeedMatches = feedMatches;
feedMatches = function (el, e) { // eslint-disable-line no-func-assign
  if (!baseFeedMatches(el, e)) return false;
  if (el.closest('#main') && S.view === VIEWS.events) return !EVT.paused && eventVisible(e, el.dataset.agent || '');
  return true;
};
CHANGES['events-live-filter'] = el => {
  const f = el.closest('form');
  EVT.type = $('select[name="type"]', f).value; EVT.level = $('select[name="level"]', f).value; EVT.q = $('input[name="q"]', f).value.trim();
  const feed = $('#live-feed');
  if (feed) feed.innerHTML = S.feed.filter(e => eventVisible(e, feed.dataset.agent || '')).slice(0, 200).map(e => feedRow(e)).join('');
};
ACTIONS['events-pause'] = () => { EVT.paused = !EVT.paused; toast(EVT.paused ? 'Stream paused — new events are still collected' : 'Stream resumed', 'ok'); rerender(); };
FORMS['events-filter'] = (f, data) => { EVT.type = data.type || ''; EVT.level = data.level || ''; EVT.q = (data.q || '').trim(); navigate('/events' + (data.agent ? '?agent=' + enc(data.agent) : '')); };
