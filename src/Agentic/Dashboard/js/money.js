'use strict';
/* Agentic console - Money: crypto prices (CoinMarketCap, about every minute) and exchange rates (the ECB's daily reference
   rates through Frankfurter), read by the server on its own and kept for charts. Ticker cards with their last day, a large
   chart of the chosen asset (ranges, crosshair, open/high/low/last), every price in a table, and what you watch (each account
   its own coins and currencies; the defaults, which administrators choose, until it saves its own). Agents read the same data
   with market_prices; chats when Money is on. */

const MON = { data: null, sel: null, range: '24h', timer: null, chartPoints: [], uid: 0 };
const MON_RANGES = [['1h', '1H'], ['6h', '6H'], ['24h', '24H'], ['7d', '7D'], ['30d', '30D'], ['90d', '90D'], ['1y', '1Y'], ['all', 'All']];
const MON_FX_RANGES = new Set(['7d', '30d', '90d', '1y', 'all']);

// ------------------------------------------------------------------ numbers
/** A price with as many decimals as it needs: $76,785.28, $1.2487, 0.89395. */
function monPrice(v, kind, bare) {
  if (v === null || v === undefined || isNaN(v)) return '—';
  // A prediction market's price (0-1) is its chance: 0.623 is 62.3%.
  if (kind === 'odds') { const p = v * 100; return (p > 0 && p < 1 ? p.toFixed(2) : p.toFixed(1)) + '%'; }
  const a = Math.abs(v);
  const d = kind === 'fx' ? (a >= 100 ? 2 : 4) : kind === 'stock' || kind === 'index' ? (a >= 1 ? 2 : 4) : a >= 1000 ? 2 : a >= 1 ? 2 : a >= 0.01 ? 4 : 6;
  const s = Number(v).toLocaleString('en-US', { minimumFractionDigits: d, maximumFractionDigits: d });
  // Indexes are points, not dollars.
  return (kind === 'crypto' || kind === 'stock') && !bare ? '$' + s : s;
}
function monPct(v) { return v === null || v === undefined || isNaN(v) ? '—' : `${v > 0 ? '+' : v < 0 ? '−' : ''}${Math.abs(v).toFixed(2)}%`; }
function monTone(v) { return v === null || v === undefined || Math.abs(v) < 0.005 ? 'flat' : v > 0 ? 'up' : 'down'; }
/** Round steps for an axis: 1, 2, 2.5, 5 × 10^n. */
function monTicks(min, max, count) {
  const span = max - min || Math.abs(max) || 1;
  const raw = span / Math.max(1, count), p = Math.pow(10, Math.floor(Math.log10(raw)));
  const step = [1, 2, 2.5, 5, 10].map(m => m * p).find(s => s >= raw) || 10 * p;
  const ticks = [];
  for (let t = Math.ceil(min / step) * step; t <= max + step * 1e-9; t += step) ticks.push(Number(t.toPrecision(12)));
  return ticks;
}
function monTime(ms, range, withDate) {
  const d = new Date(ms);
  const hm = d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  if (range === '1h' || range === '6h' || range === '24h' || range === '1d') return withDate ? `${d.toLocaleDateString([], { month: 'short', day: 'numeric' })} ${hm}` : hm;
  if (range === '7d' || range === '5d') return d.toLocaleDateString([], { weekday: 'short', day: 'numeric' }) + (withDate ? ' ' + hm : '');
  if (range === '6mo' || range === 'ytd') return d.toLocaleDateString([], { month: 'short', day: 'numeric', ...(withDate ? { year: 'numeric' } : {}) });
  if (range === '1y' || range === 'all' || range === '5y' || range === 'max') return d.toLocaleDateString([], { month: 'short', year: '2-digit', ...(withDate ? { day: 'numeric' } : {}) });
  return d.toLocaleDateString([], { month: 'short', day: 'numeric' }) + (withDate && range !== '90d' ? ' ' + hm : '');
}

// ------------------------------------------------------------------ charts
/** A small line of the last day (crypto) or month (currencies), green when it ended higher. */
function monSparkSvg(points) {
  if (!points || points.length < 2) return '<span class="muted small mon-spark-empty">collecting…</span>';
  const W = 120, H = 34, vals = points.map(p => p[1]);
  const min = Math.min(...vals), max = Math.max(...vals), span = max - min || 1;
  const x0 = points[0][0], x1 = points[points.length - 1][0], dx = x1 - x0 || 1;
  const xy = points.map(p => [((p[0] - x0) / dx) * W, H - 3 - ((p[1] - min) / span) * (H - 6)]);
  const line = xy.map((q, i) => `${i ? 'L' : 'M'}${q[0].toFixed(1)},${q[1].toFixed(1)}`).join('');
  const tone = vals[vals.length - 1] >= vals[0] ? 'up' : 'down';
  return `<svg class="mon-spark ${tone}" viewBox="0 0 ${W} ${H}" preserveAspectRatio="none" aria-hidden="true"><path class="mon-spark-area" d="${line}L${W},${H}L0,${H}Z"></path><path class="mon-spark-line" d="${line}"></path></svg>`;
}

/**
 * The large chart: a price line over an area, round price steps on the right, times below, the last price marked, and a
 * crosshair with the price and time under the pointer.
 */
/**
 * opts.baseline: a price the move is measured against (a stock's previous close on its 1-day chart), drawn as a dotted line;
 * the colour then says above or below it. opts.empty: the text while there are no prices.
 */
function monDrawChart(el, points, kind, range, opts = {}) {
  MON.chartPoints = points;
  if (!el) return;
  if (!points || points.length < 2) {
    el.innerHTML = `<div class="mon-chart-empty">${esc(opts.empty || `${points && points.length === 1 ? 'One price so far.' : 'No prices in this range yet.'} The chart fills in as prices arrive${kind === 'crypto' ? ' (about every minute)' : ' (the ECB publishes once each working day)'}.`)}</div>`;
    return;
  }
  const W = Math.max(320, el.clientWidth), H = el.clientWidth < 600 ? 240 : 340, L = 10, R = 76, T = 16, B = 30;
  const pw = W - L - R, ph = H - T - B;
  const vals = points.map(p => p[1]);
  const base = typeof opts.baseline === 'number' ? opts.baseline : null;
  let min = Math.min(...vals, ...(base !== null ? [base] : [])), max = Math.max(...vals, ...(base !== null ? [base] : []));
  const pad = (max - min) * 0.08 || Math.abs(max) * 0.002 || 1;
  min -= pad; max += pad;
  const x0 = points[0][0], x1 = points[points.length - 1][0], dx = x1 - x0 || 1;
  const X = t => L + ((t - x0) / dx) * pw, Y = v => T + ph - ((v - min) / (max - min)) * ph;
  const tone = vals[vals.length - 1] >= (base !== null ? base : vals[0]) ? 'up' : 'down';
  const id = 'mon-grad-' + (++MON.uid);
  let g = '';
  monTicks(min, max, 5).forEach(t => {
    const y = Y(t);
    if (y < T - 1 || y > T + ph + 1) return;
    g += `<line class="mon-grid" x1="${L}" x2="${L + pw}" y1="${y.toFixed(1)}" y2="${y.toFixed(1)}"></line><text class="mon-axis" x="${W - R + 8}" y="${(y + 4).toFixed(1)}">${esc(monPrice(t, kind, true))}</text>`;
  });
  const xt = Math.max(2, Math.min(7, Math.floor(pw / 110)));
  for (let i = 0; i <= xt; i++) {
    const t = x0 + (dx * i) / xt, x = X(t);
    g += `<text class="mon-axis" x="${x.toFixed(1)}" y="${H - 9}" text-anchor="${i === 0 ? 'start' : i === xt ? 'end' : 'middle'}">${esc(monTime(t, range))}</text>`;
  }
  const line = points.map((p, i) => `${i ? 'L' : 'M'}${X(p[0]).toFixed(1)},${Y(p[1]).toFixed(1)}`).join('');
  const last = points[points.length - 1], ly = Y(last[1]);
  g += `<defs><linearGradient id="${id}" x1="0" x2="0" y1="0" y2="1"><stop offset="0" class="mon-stop-top"></stop><stop offset="1" class="mon-stop-bottom"></stop></linearGradient></defs>
    <path class="mon-area" fill="url(#${id})" d="${line}L${X(last[0]).toFixed(1)},${T + ph}L${L},${T + ph}Z"></path>
    <path class="mon-line" d="${line}"></path>
    <line class="mon-last" x1="${L}" x2="${L + pw}" y1="${ly.toFixed(1)}" y2="${ly.toFixed(1)}"></line>
    <rect class="mon-last-tag" x="${W - R + 2}" y="${(ly - 10).toFixed(1)}" width="${R - 4}" height="20" rx="4"></rect>
    <text class="mon-last-text" x="${W - R + 8}" y="${(ly + 4).toFixed(1)}">${esc(monPrice(last[1], kind, true))}</text>
    <circle class="mon-last-dot" cx="${X(last[0]).toFixed(1)}" cy="${ly.toFixed(1)}" r="3.5"></circle>
    ${base !== null ? `<line class="mon-base" x1="${L}" x2="${L + pw}" y1="${Y(base).toFixed(1)}" y2="${Y(base).toFixed(1)}"></line>
      <text class="mon-base-text" x="${L + 4}" y="${(Y(base) - 5).toFixed(1)}">Prev close ${esc(monPrice(base, kind, true))}</text>` : ''}
    <g class="mon-hover hidden"><line class="mon-cross" y1="${T}" y2="${T + ph}"></line><line class="mon-cross h" x1="${L}" x2="${L + pw}"></line><circle class="mon-hover-dot" r="4.5"></circle></g>
    <rect class="mon-hit" x="${L}" y="${T}" width="${pw}" height="${ph}"></rect>`;
  el.innerHTML = `<svg class="mon-svg ${tone}" viewBox="0 0 ${W} ${H}" width="${W}" height="${H}" role="img" aria-label="Price chart">${g}</svg><div class="mon-tip hidden"></div>`;
  const svg = el.firstElementChild, hover = svg.querySelector('.mon-hover'), tip = el.querySelector('.mon-tip');
  const show = ev => {
    const r = svg.getBoundingClientRect(), px = (ev.clientX - r.left) * (W / r.width);
    const t = x0 + ((px - L) / pw) * dx;
    let lo = 0, hi = points.length - 1;
    while (hi - lo > 1) { const mid = (lo + hi) >> 1; if (points[mid][0] < t) lo = mid; else hi = mid; }
    const p = Math.abs(points[lo][0] - t) < Math.abs(points[hi][0] - t) ? points[lo] : points[hi];
    const x = X(p[0]), y = Y(p[1]);
    hover.classList.remove('hidden');
    hover.querySelector('.mon-cross').setAttribute('x1', x); hover.querySelector('.mon-cross').setAttribute('x2', x);
    const h = hover.querySelector('.mon-cross.h'); h.setAttribute('y1', y); h.setAttribute('y2', y);
    const dot = hover.querySelector('.mon-hover-dot'); dot.setAttribute('cx', x); dot.setAttribute('cy', y);
    const first = base !== null ? base : points[0][1], ch = first ? (p[1] - first) / first * 100 : 0;
    // Odds move in percentage points (40% to 45% is +5.0 pts), everything else in percent.
    const move = kind === 'odds' ? (p[1] - points[0][1]) * 100 : ch;
    const moveText = kind === 'odds' ? `${move > 0 ? '+' : move < 0 ? '−' : ''}${Math.abs(move).toFixed(1)} pts` : monPct(ch);
    tip.innerHTML = `<b>${esc(monPrice(p[1], kind))}</b><span class="mon-${monTone(move)}">${esc(moveText)}</span><span class="muted">${esc(monTime(p[0], range, true))}</span>`;
    tip.classList.remove('hidden');
    const sx = x * (r.width / W), sy = y * (r.height / H);
    tip.style.left = Math.min(Math.max(0, sx - tip.offsetWidth / 2), r.width - tip.offsetWidth) + 'px';
    tip.style.top = Math.max(0, sy - tip.offsetHeight - 14) + 'px';
  };
  const hide = () => { hover.classList.add('hidden'); tip.classList.add('hidden'); };
  const hit = svg.querySelector('.mon-hit');
  hit.addEventListener('pointermove', show);
  hit.addEventListener('pointerdown', show);
  hit.addEventListener('pointerleave', hide);
}

// ------------------------------------------------------------------ page
function monAll() { return MON.data ? [...MON.data.crypto, ...MON.data.fx] : []; }
function monFind(kind, symbol) { return monAll().find(q => q.kind === kind && q.symbol === symbol); }
function monStatusHtml(d) {
  const s = d.status || {}, bad = s.cryptoError || s.fxError;
  const at = s.cryptoUpdatedUtc || s.fxUpdatedUtc;
  return `<span class="aud-status ${bad ? 'bad' : 'ok'}"${bad ? ` data-tip="${esc([s.cryptoError, s.fxError].filter(Boolean).join(' · '))}"` : ''}><i></i>${bad ? 'Problem reading prices' : 'Live'}${at ? ' · updated ' + esc(ago(at)) : ''}</span>`;
}
function monCardHtml(q) {
  const on = MON.sel && MON.sel.kind === q.kind && MON.sel.symbol === q.symbol;
  const ch = q.kind === 'crypto' ? q.change24h : q.change24h ?? q.change7d;
  const chLabel = q.kind === 'crypto' || q.change24h !== null ? '24h' : '7d';
  const base = (MON.data && MON.data.base) || 'USD';
  return `<button type="button" class="mon-card${on ? ' on' : ''}" data-act="mon-pick" data-kind="${esc(q.kind)}" data-symbol="${esc(q.symbol)}">
    <span class="mon-card-head"><b>${esc(q.kind === 'fx' ? `${base}/${q.symbol}` : q.symbol)}</b><span class="muted small">${esc(q.name || '')}</span></span>
    <span class="mon-card-price" data-price="${esc(q.kind + ':' + q.symbol)}">${esc(monPrice(q.price, q.kind))}</span>
    <span class="mon-card-foot"><span class="mon-chip mon-${monTone(ch)}">${esc(monPct(ch))}</span><span class="muted small">${chLabel}</span></span>
    <span class="mon-card-spark" data-spark="${esc(q.kind + ':' + q.symbol)}"></span></button>`;
}
function monTableHtml(list, kind) {
  if (!list.length) return '<p class="muted small">Nothing watched.</p>';
  const base = (MON.data && MON.data.base) || 'USD';
  return `<div class="table-wrap"><table class="table mon-table"><thead><tr><th>${kind === 'fx' ? 'Pair' : 'Coin'}</th><th class="num">${kind === 'fx' ? `Rate (per 1 ${esc(base)})` : 'Price (USD)'}</th>
    ${kind === 'fx' ? `<th class="num">${esc(base)} per unit</th>` : '<th class="num">1h</th>'}<th class="num">24h</th><th class="num">7d</th><th class="num">30d</th><th>Updated</th></tr></thead><tbody>
    ${list.map(q => `<tr class="clickable" data-act="mon-pick" data-kind="${esc(q.kind)}" data-symbol="${esc(q.symbol)}">
      <td><b>${esc(kind === 'fx' ? `${base}/${q.symbol}` : q.symbol)}</b> <span class="muted small">${esc(q.name || '')}</span></td>
      <td class="num">${esc(monPrice(q.price, q.kind))}</td>
      ${kind === 'fx' ? `<td class="num">${q.price ? esc(monPrice(1 / q.price, 'fx')) : '—'}</td>` : `<td class="num mon-${monTone(q.change1h)}">${esc(monPct(q.change1h))}</td>`}
      <td class="num mon-${monTone(q.change24h)}">${esc(monPct(q.change24h))}</td><td class="num mon-${monTone(q.change7d)}">${esc(monPct(q.change7d))}</td>
      <td class="num mon-${monTone(q.change30d)}">${esc(monPct(q.change30d))}</td><td class="muted small">${q.utc ? esc(ago(q.utc)) : '—'}</td></tr>`).join('')}</tbody></table></div>`;
}
function monChartCardHtml() {
  const q = MON.sel && monFind(MON.sel.kind, MON.sel.symbol);
  if (!q) return '';
  const base = (MON.data && MON.data.base) || 'USD';
  const ranges = MON_RANGES.filter(([k]) => q.kind === 'crypto' || MON_FX_RANGES.has(k));
  return `<div class="card mon-main"><div class="card-body">
    <div class="mon-main-head">
      <div><div class="mon-main-title">${esc(q.kind === 'fx' ? `${base}/${q.symbol}` : q.symbol)} <span class="muted">${esc(q.name || '')}</span>
          ${monAll().length > 1 ? `<button type="button" class="btn xs ghost stk-unwatch" data-act="mon-watch-drop" data-kind="${esc(q.kind)}" data-symbol="${esc(q.symbol)}" data-tip="Take it off your watch list">Stop watching</button>` : ''}</div>
        <div class="mon-main-price"><span id="mon-main-price">${esc(monPrice(q.price, q.kind))}</span><span class="mon-chip mon-flat" id="mon-range-change">—</span>
          ${q.kind === 'fx' && q.price ? `<span class="muted small">1 ${esc(q.symbol)} = ${esc(monPrice(1 / q.price, 'fx'))} ${esc(base)}</span>` : ''}</div></div>
      <div class="seg mon-ranges" role="group" aria-label="Range">${ranges.map(([k, l]) => `<button type="button" class="seg-btn${MON.range === k ? ' on' : ''}" data-act="mon-range" data-range="${k}">${l}</button>`).join('')}</div>
    </div>
    <div class="mon-chart" id="mon-chart"><div class="mon-chart-empty">Loading…</div></div>
    <div class="mon-stats" id="mon-stats"></div>
    <p class="muted small mon-source">${q.kind === 'crypto' ? `${esc((MON.data.status && MON.data.status.cryptoSource) || 'CoinMarketCap')} · USD · read about every minute` : `European Central Bank reference rate via Frankfurter · units of ${esc(q.symbol)} per 1 ${esc(base)} · once each working day`}</p>
  </div></div>`;
}
function monStatsHtml(points, kind) {
  if (!points || points.length < 2) return '';
  const vals = points.map(p => p[1]), open = vals[0], last = vals[vals.length - 1];
  const hi = Math.max(...vals), lo = Math.min(...vals), ch = open ? (last - open) / open * 100 : 0;
  const cell = (k, v, cls) => `<div class="mon-stat"><span class="muted small">${k}</span><b class="${cls || ''}">${esc(v)}</b></div>`;
  return cell('Open', monPrice(open, kind)) + cell('High', monPrice(hi, kind)) + cell('Low', monPrice(lo, kind)) + cell('Last', monPrice(last, kind)) +
    cell('Change', `${last - open >= 0 ? '+' : '−'}${monPrice(Math.abs(last - open), kind)} (${monPct(ch)})`, 'mon-' + monTone(ch));
}
/** Your coins and currencies (each account keeps its own; the defaults until you save them), and the defaults (administrators). */
function monSettingsHtml() {
  const d = MON.data, def = d.defaults || d.watchlist;
  const mine = card('What you watch', `<p class="muted small">${d.custom ? 'Your own coins and currencies, kept with your account.' : `The defaults (${esc(def.crypto.join(', ') || 'no coins')}; ${esc(def.fx.join(', ') || 'no currencies')}); save your own and they are kept with your account.`}
      Coins by their symbol (BTC, SOL, DOGE), currencies by their ISO code (against ${esc(d.base)}). Each new one is checked when you save; a new currency gets its last 90 days, a new coin starts collecting now.</p>
    <div class="form-grid"><label>Coins<input id="mon-w-crypto" value="${esc(d.watchlist.crypto.join(', '))}" placeholder="BTC, ETH, SOL"></label>
      <label>Currencies<input id="mon-w-fx" value="${esc(d.watchlist.fx.join(', '))}" placeholder="EUR, GBP, JPY"></label></div>
    <div class="row mon-watch-foot"><button type="button" class="btn primary" data-act="mon-watch-save">Save</button>
      ${d.custom ? '<button type="button" class="btn" data-act="mon-watch-reset" data-tip="Watch the default coins and currencies again">Use the defaults</button>' : ''}
      <span class="muted small">Prices: every ${esc(String(d.status.cryptoPollSeconds))} s (crypto), every ${esc(String(d.status.fxPollMinutes))} min (exchange rates).</span></div>`, { cls: 'section' });
  if (!d.isAdmin) return mine;
  return mine + card('Defaults for everyone', `<p class="muted small">What every account watches until it saves its own (administrators). Accounts with their own list keep it.</p>
    <div class="form-grid"><label>Coins<input id="mon-d-crypto" value="${esc(def.crypto.join(', '))}" placeholder="BTC, ETH, SOL"></label>
      <label>Currencies<input id="mon-d-fx" value="${esc(def.fx.join(', '))}" placeholder="EUR, GBP, JPY"></label></div>
    <div class="row mon-watch-foot"><button type="button" class="btn" data-act="mon-defaults-save">Save defaults</button></div>`, { cls: 'section' });
}
function monBodyHtml() {
  const d = MON.data;
  return `<div class="mon" id="mon-root">
    <div class="mon-section-title"><h2>Crypto</h2><span class="muted small">USD · ${esc((d.status && d.status.cryptoSource) || 'CoinMarketCap')}</span></div>
    <div class="mon-cards">${d.crypto.map(monCardHtml).join('') || '<p class="muted small">No coins watched.</p>'}</div>
    <div class="mon-section-title"><h2>Currencies</h2><span class="muted small">per 1 ${esc(d.base)} · European Central Bank</span></div>
    <div class="mon-cards">${d.fx.map(monCardHtml).join('') || '<p class="muted small">No currencies watched.</p>'}</div>
    <div id="mon-main-box">${monChartCardHtml()}</div>
    ${card('Crypto prices', monTableHtml(d.crypto, 'crypto'), { flush: true, cls: 'section' })}
    ${card('Exchange rates', monTableHtml(d.fx, 'fx'), { flush: true, cls: 'section' })}
    ${monSettingsHtml()}
  </div>`;
}
VIEWS.money = {
  live: false,
  async render() {
    setCrumbs([['Money', '/money']]);
    try { MON.data = await get('/api/markets'); }
    catch (e) { return pageHead('Money') + card('', emptyState('Money is not available', e.message)); }
    if (!MON.sel || !monFind(MON.sel.kind, MON.sel.symbol)) {
      const first = MON.data.crypto[0] || MON.data.fx[0];
      MON.sel = first ? { kind: first.kind, symbol: first.symbol } : null;
      if (MON.sel && MON.sel.kind === 'fx' && !MON_FX_RANGES.has(MON.range)) MON.range = '30d';
    }
    return pageHead('Money', {
      subtitle: 'Live crypto prices and exchange rates, kept for charts. Agents can read them, and chats with Money turned on.',
      actions: `<span id="mon-status">${monStatusHtml(MON.data)}</span><button type="button" class="btn sm ghost" data-act="mon-refresh" data-tip="Read prices now">↻ Refresh</button>`
    }) + monBodyHtml();
  },
  after() { monAfter(); }
};
function monAfter() {
  monLoadSparks();
  monLoadChart();
  clearInterval(MON.timer);
  MON.timer = setInterval(monTick, 15000);
}
async function monLoadSparks() {
  for (const q of monAll()) {
    const box = document.querySelector(`[data-spark="${CSS.escape(q.kind + ':' + q.symbol)}"]`);
    if (!box) continue;
    get(`/api/markets/history?kind=${enc(q.kind)}&symbol=${enc(q.symbol)}&range=${q.kind === 'crypto' ? '24h' : '30d'}&points=60`)
      .then(h => { box.innerHTML = monSparkSvg(h.points); }).catch(() => { box.innerHTML = ''; });
  }
}
async function monLoadChart() {
  const q = MON.sel && monFind(MON.sel.kind, MON.sel.symbol), el = $('#mon-chart');
  if (!q || !el) return;
  const key = `${q.kind}:${q.symbol}:${MON.range}`;
  MON.loading = key;
  let h;
  try { h = await get(`/api/markets/history?kind=${enc(q.kind)}&symbol=${enc(q.symbol)}&range=${enc(MON.range)}&points=${Math.min(600, Math.max(120, Math.round(el.clientWidth / 2)))}`); }
  catch (e) { el.innerHTML = `<div class="mon-chart-empty">${esc(e.message)}</div>`; return; }
  if (MON.loading !== key || !$('#mon-chart')) return;
  const points = h.points || [];
  // The latest price ends the line (the chart reaches "now").
  if (q.price !== null && q.utc && points.length && Date.parse(q.utc) > points[points.length - 1][0]) points.push([Date.parse(q.utc), q.price]);
  monDrawChart($('#mon-chart'), points, q.kind, MON.range);
  const stats = $('#mon-stats'); if (stats) stats.innerHTML = monStatsHtml(points, q.kind);
  const chip = $('#mon-range-change');
  if (chip && points.length > 1) {
    const ch = points[0][1] ? (points[points.length - 1][1] - points[0][1]) / points[0][1] * 100 : 0;
    chip.className = 'mon-chip mon-' + monTone(ch);
    chip.textContent = `${monPct(ch)} · ${(MON_RANGES.find(r => r[0] === MON.range) || [, ''])[1]}`;
  }
}
/** Every 15 s while the page is open: new prices into the cards and table (a short flash), the chart when its range is short. */
async function monTick() {
  if (!$('#mon-root')) { clearInterval(MON.timer); MON.timer = null; return; }
  if (document.hidden) return;
  let d;
  try { d = await get('/api/markets'); } catch { return; }
  if (!$('#mon-root')) return;
  const before = new Map(monAll().map(q => [q.kind + ':' + q.symbol, q.price]));
  MON.data = d;
  const st = $('#mon-status'); if (st) st.innerHTML = monStatusHtml(d);
  for (const q of monAll()) {
    const k = q.kind + ':' + q.symbol, old = before.get(k);
    const el = document.querySelector(`[data-price="${CSS.escape(k)}"]`);
    if (!el || old === q.price) continue;
    el.textContent = monPrice(q.price, q.kind);
    const cls = q.price > old ? 'flash-up' : 'flash-down';
    if (!reducedMotion()) { el.classList.remove('flash-up', 'flash-down'); void el.offsetWidth; el.classList.add(cls); }
    const card = el.closest('.mon-card');
    if (card) { const chip = card.querySelector('.mon-chip'), ch = q.kind === 'crypto' ? q.change24h : q.change24h ?? q.change7d; chip.className = 'mon-chip mon-' + monTone(ch); chip.textContent = monPct(ch); }
  }
  const sel = MON.sel && monFind(MON.sel.kind, MON.sel.symbol);
  const mp = $('#mon-main-price'); if (mp && sel) mp.textContent = monPrice(sel.price, sel.kind);
  const tables = $$('#mon-root .mon-table');
  if (tables[0]) tables[0].closest('.table-wrap').outerHTML = monTableHtml(d.crypto, 'crypto');
  if (tables[1]) tables[1].closest('.table-wrap').outerHTML = monTableHtml(d.fx, 'fx');
  if (['1h', '6h', '24h'].includes(MON.range)) monLoadChart();
}
ACTIONS['mon-pick'] = el => {
  MON.sel = { kind: el.dataset.kind, symbol: el.dataset.symbol };
  if (MON.sel.kind === 'fx' && !MON_FX_RANGES.has(MON.range)) MON.range = '30d';
  $$('#mon-root .mon-card').forEach(c => c.classList.toggle('on', c.dataset.kind === MON.sel.kind && c.dataset.symbol === MON.sel.symbol));
  const box = $('#mon-main-box'); if (box) box.innerHTML = monChartCardHtml();
  monLoadChart();
  if (el.tagName === 'TR' && box) box.scrollIntoView({ behavior: reducedMotion() ? 'auto' : 'smooth', block: 'start' });
};
ACTIONS['mon-range'] = el => {
  MON.range = el.dataset.range;
  $$('#mon-root .mon-ranges .seg-btn').forEach(b => b.classList.toggle('on', b.dataset.range === MON.range));
  monLoadChart();
};
ACTIONS['mon-refresh'] = async () => {
  try { MON.data = await post('/api/markets/refresh', {}); } catch (e) { toast(e.message, 'bad'); return; }
  const st = $('#mon-status'); if (st) st.innerHTML = monStatusHtml(MON.data);
  toast('Prices read', 'ok');
  monTick();
};
const monSplit = id => (($('#' + id) || {}).value || '').split(/[\s,;]+/).map(s => s.trim().toUpperCase()).filter(Boolean);
async function monSaveWatch(crypto, fx) {
  const btn = $('[data-act="mon-watch-save"]'); if (btn) btn.disabled = true;
  try { MON.data = await put('/api/markets/watchlist', { crypto, fx }); }
  catch (e) { toast(e.message, 'bad'); if (btn) btn.disabled = false; return; }
  toast('Watch list saved', 'ok');
  navigate('/money');
}
ACTIONS['mon-watch-save'] = () => monSaveWatch(monSplit('mon-w-crypto'), monSplit('mon-w-fx'));
ACTIONS['mon-watch-drop'] = el => {
  const w = MON.data.watchlist, k = el.dataset.kind, s = el.dataset.symbol;
  monSaveWatch(k === 'crypto' ? w.crypto.filter(x => x !== s) : w.crypto, k === 'fx' ? w.fx.filter(x => x !== s) : w.fx);
};
ACTIONS['mon-watch-reset'] = async () => {
  try { MON.data = await del('/api/markets/watchlist'); } catch (e) { toast(e.message, 'bad'); return; }
  toast('Watching the defaults', 'ok');
  navigate('/money');
};
ACTIONS['mon-defaults-save'] = async () => {
  try { await put('/api/markets/defaults', { crypto: monSplit('mon-d-crypto'), fx: monSplit('mon-d-fx') }); }
  catch (e) { toast(e.message, 'bad'); return; }
  toast('Defaults saved', 'ok');
  navigate('/money');
};
// The chart follows the page's width.
let monResize = null;
window.addEventListener('resize', () => { clearTimeout(monResize); monResize = setTimeout(() => { if ($('#mon-chart') && MON.chartPoints.length > 1 && MON.sel) monDrawChart($('#mon-chart'), MON.chartPoints, MON.sel.kind, MON.range); }, 150); });
