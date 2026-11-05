'use strict';
/* Agentic console - the token usage graph (the chart button at the top right): tokens used over the last 24 hours, 7 or
   30 days, one line per user (and agent) or per model, live while it is open. Data from /api/usage/timeline. */

const USAGE = { range: '24h', by: 'user', metric: 'total', hidden: new Set(), data: null, timer: 0, ro: null, drawn: false, busy: false };
const USAGE_COLORS = ['u1', 'u2', 'u3', 'u4', 'u5', 'u6', 'u7', 'u8'];
const USAGE_RANGES = [['24h', '24 hours'], ['7d', '7 days'], ['30d', '30 days']];

ACTIONS['open-usage'] = () => openUsage();
ACTIONS['usage-range'] = el => { USAGE.range = el.dataset.v; USAGE.drawn = false; loadUsage(); };
ACTIONS['usage-by'] = el => { USAGE.by = el.dataset.v; USAGE.hidden.clear(); USAGE.drawn = false; loadUsage(); };
ACTIONS['usage-metric'] = el => { USAGE.metric = el.dataset.v; renderUsage(); };
ACTIONS['usage-toggle'] = el => {
  const k = el.dataset.key;
  if (USAGE.hidden.has(k)) USAGE.hidden.delete(k); else USAGE.hidden.add(k);
  // Never hide every line: the last one stays.
  if (USAGE.data && USAGE.hidden.size >= USAGE.data.series.length) USAGE.hidden.delete(k);
  renderUsage();
};

function openUsage() {
  openModal('Token usage', `<div class="usage">
      <div class="usage-bar">
        <div class="seg-ctl" role="group" aria-label="Period" data-ctl="range"></div>
        <div class="seg-ctl" role="group" aria-label="Lines" data-ctl="by"></div>
        <div class="seg-ctl" role="group" aria-label="Tokens" data-ctl="metric"></div>
        <span class="spacer"></span>
        <span class="usage-live"><i></i><span class="usage-updated">Loading…</span></span>
      </div>
      <div class="usage-kpis"></div>
      <div class="usage-chart-wrap"><div class="usage-chart" id="usage-chart"></div><div class="usage-tip hidden" role="tooltip"></div></div>
      <div class="usage-legend"></div>
      <div class="usage-table"></div>
    </div>`, '', 'wide usage-modal');
  USAGE.drawn = false;
  loadUsage();
  clearInterval(USAGE.timer);
  // Live: new numbers every 15 s while the dialog is open (and the window is in view).
  USAGE.timer = setInterval(() => {
    if (!$('#modal-card .usage')) { clearInterval(USAGE.timer); USAGE.timer = 0; if (USAGE.ro) { USAGE.ro.disconnect(); USAGE.ro = null; } return; }
    if (!document.hidden) loadUsage(true);
  }, 15000);
  if (USAGE.ro) USAGE.ro.disconnect();
  const chart = $('#usage-chart');
  if (chart && window.ResizeObserver) { let w = 0; USAGE.ro = new ResizeObserver(() => { if (chart.clientWidth !== w) { w = chart.clientWidth; drawUsageChart(false); } }); USAGE.ro.observe(chart); }
}

async function loadUsage(quiet) {
  if (USAGE.busy) return;
  USAGE.busy = true;
  const root = $('#modal-card .usage');
  if (root && !quiet) root.classList.add('loading');
  try {
    USAGE.data = await get(`/api/usage/timeline?range=${enc(USAGE.range)}&by=${enc(USAGE.by)}&tz=${new Date().getTimezoneOffset()}`);
    renderUsage();
  } catch (e) {
    const u = $('#modal-card .usage-updated'); if (u) u.textContent = 'Could not load: ' + e.message;
  } finally {
    USAGE.busy = false;
    if (root) root.classList.remove('loading');
  }
}

function tokensShort(v) {
  const a = Math.abs(v);
  if (a >= 1e9) return (v / 1e9).toFixed(a >= 1e11 ? 0 : 1).replace(/\.0$/, '') + 'B';
  if (a >= 1e6) return (v / 1e6).toFixed(a >= 1e8 ? 0 : 1).replace(/\.0$/, '') + 'M';
  if (a >= 1e3) return (v / 1e3).toFixed(a >= 1e5 ? 0 : 1).replace(/\.0$/, '') + 'k';
  return String(Math.round(v));
}
function usageValues(s) {
  if (USAGE.metric === 'input') return s.input;
  if (USAGE.metric === 'output') return s.output;
  return s.input.map((v, i) => v + s.output[i]);
}
function usageColor(s, i) { return s.kind === 'other' ? 'uo' : USAGE_COLORS[i % USAGE_COLORS.length]; }
/** Who used a model (Models), or which models a user or agent used (Users & agents): the biggest first, the rest in "+N". */
function usagePartsHtml(s) {
  const parts = s.parts || [];
  if (!parts.length) return '<span class="muted">—</span>';
  const sum = p => p.input + p.output;
  const chip = p => `<span class="usage-part${p.kind === 'agent' ? ' agent' : ''}" data-tip="${esc(`${p.kind === 'agent' ? 'Agent ' : ''}${p.label}: ${num(sum(p))} tokens (${num(p.input)} in, ${num(p.output)} out) · ${plural(p.calls, 'call')}`)}">` +
    `${p.kind === 'agent' ? '<i class="usage-part-kind">agent</i>' : ''}${esc(p.label)} <b>${tokensShort(sum(p))}</b></span>`;
  const shownParts = parts.slice(0, 6), rest = parts.slice(6);
  const more = rest.length + (s.moreParts || 0);
  return shownParts.map(chip).join('') + (more ? `<span class="usage-part more" data-tip="${esc(rest.map(p => `${p.kind === 'agent' ? 'Agent ' : ''}${p.label} ${tokensShort(sum(p))}`).join(' · ') + (s.moreParts ? ` · and ${s.moreParts} more` : ''))}">+${more}</span>` : '');
}

function renderUsage() {
  const root = $('#modal-card .usage');
  const d = USAGE.data;
  if (!root || !d) return;
  const seg = (ctl, items, cur, act) => { const el = $(`[data-ctl="${ctl}"]`, root); if (el) el.innerHTML = items.map(([v, l]) => `<button type="button" class="${v === cur ? 'on' : ''}" data-act="${act}" data-v="${v}" aria-pressed="${v === cur}">${esc(l)}</button>`).join(''); };
  seg('range', USAGE_RANGES, USAGE.range, 'usage-range');
  seg('by', [['user', d.scope === 'all' ? 'Users & agents' : 'You'], ['model', 'Models']], USAGE.by, 'usage-by');
  seg('metric', [['total', 'Total'], ['input', 'Input'], ['output', 'Output']], USAGE.metric, 'usage-metric');
  $('.usage-updated', root).textContent = `Live · updated ${new Date(d.updatedUtc).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })}`;

  const t = d.totals, total = t.input + t.output;
  const top = d.series.filter(s => s.kind !== 'other').map(s => ({ s, v: s.inputTotal + s.outputTotal })).sort((a, b) => b.v - a.v)[0];
  const kpi = (label, value, sub) => `<div class="usage-kpi"><span>${esc(label)}</span><b>${esc(value)}</b>${sub ? `<small>${esc(sub)}</small>` : ''}</div>`;
  $('.usage-kpis', root).innerHTML =
    kpi('Total tokens', tokensShort(total), `${num(total)} in ${USAGE_RANGES.find(r => r[0] === d.range)[1]}`) +
    kpi('Input', tokensShort(t.input), total ? Math.round(t.input / total * 100) + '% of tokens' : '') +
    kpi('Output', tokensShort(t.output), total ? Math.round(t.output / total * 100) + '% of tokens' : '') +
    kpi('Calls', num(t.calls), t.calls ? tokensShort(total / t.calls) + ' tokens per call' : '') +
    kpi(d.scope === 'all' ? 'Users' : 'Models', d.scope === 'all' ? num(t.users) : num(t.models), d.scope === 'all' ? `${num(t.agents)} agent${t.agents === 1 ? '' : 's'} · ${num(t.models)} models` : '') +
    kpi(USAGE.by === 'model' ? 'Top model' : 'Top user', top ? top.s.label : '—', top && total ? Math.round(top.v / total * 100) + '% of tokens' : '');

  $('.usage-legend', root).innerHTML = d.series.map((s, i) => {
    const off = USAGE.hidden.has(s.key);
    return `<button type="button" class="usage-chip${off ? ' off' : ''}" data-act="usage-toggle" data-key="${esc(s.key)}" aria-pressed="${!off}" title="${off ? 'Show' : 'Hide'} this line">
      <i class="sw ${usageColor(s, i)}"></i><span>${esc(s.label)}</span><b>${tokensShort(usageValues(s).reduce((a, b) => a + b, 0))}</b></button>`;
  }).join('');

  const rows = d.series.map((s, i) => ({ s, i, tot: s.inputTotal + s.outputTotal })).sort((a, b) => (a.s.kind === 'other') - (b.s.kind === 'other') || b.tot - a.tot);
  $('.usage-table', root).innerHTML = d.series.length ? `<table class="table usage-tbl"><thead><tr><th>${USAGE.by === 'model' ? 'Model' : 'User'}</th><th>${USAGE.by === 'model' ? 'Used by' : 'Models'}</th><th class="num">Input</th><th class="num">Output</th><th class="num">Total</th><th class="num">Calls</th><th class="share-col">Share</th></tr></thead><tbody>${
    rows.map(({ s, i, tot }) => `<tr><td><i class="sw ${usageColor(s, i)}"></i>${esc(s.label)}${s.kind === 'agent' ? ' <span class="badge outline">agent</span>' : ''}</td>
      <td class="usage-parts">${usagePartsHtml(s)}</td>
      <td class="num">${num(s.inputTotal)}</td><td class="num">${num(s.outputTotal)}</td><td class="num"><b>${num(tot)}</b></td><td class="num">${num(s.calls)}</td>
      <td class="share-col"><span class="usage-share"><span class="${usageColor(s, i)}" data-share="${total ? (tot / total * 100).toFixed(1) : 0}"></span></span><small>${total ? (tot / total * 100).toFixed(1) : '0'}%</small></td></tr>`).join('')
  }</tbody></table>` : '';
  // Widths from script: the page's security policy ignores style attributes in markup.
  $$('.usage-share [data-share]', root).forEach(b => { b.style.width = b.dataset.share + '%'; });
  drawUsageChart(!USAGE.drawn);
  USAGE.drawn = true;
}

/** Nice round axis maximum and step (1, 2, 2.5 or 5 times a power of ten), about five steps. */
function usageScale(max) {
  if (max <= 0) return { max: 10, step: 2 };
  const raw = max / 5, pow = Math.pow(10, Math.floor(Math.log10(raw)));
  const step = [1, 2, 2.5, 5, 10].map(m => m * pow).find(s => s >= raw);
  return { max: Math.ceil(max / step) * step, step };
}
/** A smooth line through the points that never overshoots (monotone cubic, Fritsch-Carlson). */
function usagePath(pts) {
  const n = pts.length;
  if (n === 0) return '';
  if (n === 1) return `M${pts[0][0]},${pts[0][1]}`;
  const dx = [], dy = [], m = [], t = [];
  for (let i = 0; i < n - 1; i++) { dx[i] = pts[i + 1][0] - pts[i][0]; dy[i] = pts[i + 1][1] - pts[i][1]; m[i] = dx[i] ? dy[i] / dx[i] : 0; }
  t[0] = m[0]; t[n - 1] = m[n - 2];
  for (let i = 1; i < n - 1; i++) t[i] = m[i - 1] * m[i] <= 0 ? 0 : (m[i - 1] + m[i]) / 2;
  for (let i = 0; i < n - 1; i++) {
    if (m[i] === 0) { t[i] = 0; t[i + 1] = 0; continue; }
    const a = t[i] / m[i], b = t[i + 1] / m[i], h = a * a + b * b;
    if (h > 9) { const s = 3 / Math.sqrt(h); t[i] = s * a * m[i]; t[i + 1] = s * b * m[i]; }
  }
  let p = `M${pts[0][0].toFixed(1)},${pts[0][1].toFixed(1)}`;
  for (let i = 0; i < n - 1; i++) {
    const c1x = pts[i][0] + dx[i] / 3, c1y = pts[i][1] + t[i] * dx[i] / 3, c2x = pts[i + 1][0] - dx[i] / 3, c2y = pts[i + 1][1] - t[i + 1] * dx[i] / 3;
    p += ` C${c1x.toFixed(1)},${c1y.toFixed(1)} ${c2x.toFixed(1)},${c2y.toFixed(1)} ${pts[i + 1][0].toFixed(1)},${pts[i + 1][1].toFixed(1)}`;
  }
  return p;
}
function usageBucketLabel(iso, long) {
  const d = new Date(iso), r = USAGE.data.range;
  if (r === '30d') return d.toLocaleDateString([], long ? { weekday: 'short', month: 'short', day: 'numeric' } : { month: 'short', day: 'numeric' });
  if (r === '7d') return long ? d.toLocaleString([], { weekday: 'short', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }) : d.toLocaleDateString([], { weekday: 'short' }) + ' ' + d.toLocaleTimeString([], { hour: '2-digit' });
  return long ? d.toLocaleString([], { weekday: 'short', hour: '2-digit', minute: '2-digit' }) : d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

function drawUsageChart(animate) {
  const el = $('#usage-chart'), d = USAGE.data;
  if (!el || !d) return;
  const visible = d.series.map((s, i) => ({ s, i, vals: usageValues(s) })).filter(x => !USAGE.hidden.has(x.s.key));
  const n = d.buckets.length;
  const W = Math.max(320, el.clientWidth), H = W < 560 ? 240 : 320, L = 54, R = 18, T = 14, B = 34;
  const pw = W - L - R, ph = H - T - B;
  const peak = Math.max(0, ...visible.flatMap(x => x.vals));
  const sc = usageScale(peak);
  const px = i => L + (n <= 1 ? pw / 2 : i / (n - 1) * pw), py = v => T + ph - (v / sc.max) * ph;
  let g = '<defs>' + visible.map(x => `<linearGradient id="ug-${x.i}" x1="0" x2="0" y1="0" y2="1"><stop offset="0%" class="stop-a ${usageColor(x.s, x.i)}"></stop><stop offset="100%" class="stop-b ${usageColor(x.s, x.i)}"></stop></linearGradient>`).join('') + '</defs>';
  for (let v = 0; v <= sc.max + 1e-9; v += sc.step) {
    const y = py(v);
    g += `<line class="u-grid${v === 0 ? ' base' : ''}" x1="${L}" x2="${W - R}" y1="${y.toFixed(1)}" y2="${y.toFixed(1)}"></line><text class="u-axis" x="${L - 8}" y="${(y + 3.5).toFixed(1)}" text-anchor="end">${tokensShort(v)}</text>`;
  }
  const every = Math.max(1, Math.ceil(n / (W < 560 ? 4 : 8)));
  for (let i = 0; i < n; i += every) g += `<text class="u-axis" x="${px(i).toFixed(1)}" y="${H - 10}" text-anchor="middle">${esc(usageBucketLabel(d.buckets[i]))}</text>`;
  // Areas first (soft), then the lines on top, the biggest line underneath the others.
  const order = visible.slice().sort((a, b) => b.vals.reduce((s, v) => s + v, 0) - a.vals.reduce((s, v) => s + v, 0));
  order.forEach(x => {
    const pts = x.vals.map((v, i) => [px(i), py(v)]);
    const line = usagePath(pts);
    if (visible.length <= 4 && n > 1) g += `<path class="u-area" fill="url(#ug-${x.i})" d="${line} L${px(n - 1).toFixed(1)},${T + ph} L${px(0).toFixed(1)},${T + ph} Z"></path>`;
  });
  order.forEach(x => {
    const pts = x.vals.map((v, i) => [px(i), py(v)]);
    g += `<path class="u-line ${usageColor(x.s, x.i)}${animate ? ' draw' : ''}" d="${usagePath(pts)}" pathLength="1"></path>`;
  });
  g += `<line class="u-cross hidden" x1="0" x2="0" y1="${T}" y2="${T + ph}"></line><g class="u-dots"></g>`;
  g += `<rect class="u-hit" x="${L}" y="${T}" width="${pw}" height="${ph}"></rect>`;
  el.innerHTML = peak === 0
    ? `<div class="usage-empty">${icon('chart')}<b>No tokens used in this period</b><span class="muted small">Chat, API and agent calls show here as they happen.</span></div>`
    : `<svg viewBox="0 0 ${W} ${H}" width="${W}" height="${H}" role="img" aria-label="Token usage over the last ${esc(USAGE_RANGES.find(r => r[0] === d.range)[1])}">${g}</svg>`;
  if (peak === 0) return;
  const svg = $('svg', el), tip = $('#modal-card .usage-tip'), cross = $('.u-cross', svg), dots = $('.u-dots', svg);
  const hide = () => { tip.classList.add('hidden'); cross.classList.add('hidden'); dots.innerHTML = ''; };
  $('.u-hit', svg).addEventListener('mouseleave', hide);
  $('.u-hit', svg).addEventListener('mousemove', e => {
    const r = svg.getBoundingClientRect(), x = (e.clientX - r.left) * (W / r.width);
    const i = Math.max(0, Math.min(n - 1, Math.round(n <= 1 ? 0 : (x - L) / pw * (n - 1))));
    const cx = px(i);
    cross.setAttribute('x1', cx); cross.setAttribute('x2', cx); cross.classList.remove('hidden');
    dots.innerHTML = visible.map(v => `<circle class="u-dot ${usageColor(v.s, v.i)}" cx="${cx.toFixed(1)}" cy="${py(v.vals[i]).toFixed(1)}" r="4"></circle>`).join('');
    const items = visible.map(v => ({ v, val: v.vals[i] })).sort((a, b) => b.val - a.val);
    const sum = items.reduce((s, x) => s + x.val, 0);
    tip.innerHTML = `<div class="ut-head">${esc(usageBucketLabel(d.buckets[i], true))}</div>` +
      items.map(({ v, val }) => `<div class="ut-row"><i class="sw ${usageColor(v.s, v.i)}"></i><span>${esc(v.s.label)}</span><b>${num(val)}</b></div>`).join('') +
      (items.length > 1 ? `<div class="ut-row ut-total"><span>Total</span><b>${num(sum)}</b></div>` : '');
    tip.classList.remove('hidden');
    const wrap = el.parentElement.getBoundingClientRect(), tw = tip.offsetWidth, th = tip.offsetHeight;
    let left = (cx / W) * r.width + (r.left - wrap.left) + 14;
    if (left + tw > wrap.width - 4) left = (cx / W) * r.width + (r.left - wrap.left) - tw - 14;
    tip.style.left = Math.max(4, left) + 'px';
    tip.style.top = Math.max(4, Math.min(wrap.height - th - 4, e.clientY - wrap.top - th / 2)) + 'px';
  });
}
