'use strict';
/* Agentic console - Stocks: quotes for the watched tickers and indexes from Yahoo Finance (read by the server every minute
   while the market is open), each with its day's line against the previous close; a large chart of any ticker for any range
   (1D to Max, from Yahoo's history); every quote in a table; a lookup of any US ticker (NASDAQ Trader's directory); and
   what you watch (each account its own list; the defaults until it saves one). Agents read the same with stock_quotes;
   chats when Stocks is on (without tickers named: the user's list). Charts and number
   formats are money.js's. */

const STK = { data: null, sel: null, range: '1d', timer: null, extra: null, searchTimer: null };
const STK_RANGES = [['1d', '1D'], ['5d', '5D'], ['1mo', '1M'], ['6mo', '6M'], ['ytd', 'YTD'], ['1y', '1Y'], ['5y', '5Y'], ['max', 'Max']];
const STK_STATES = { open: 'Market open', pre: 'Pre-market', post: 'After hours', closed: 'Market closed' };

function stkKind(q) { return q && (q.type === 'INDEX' || String(q.symbol || '').startsWith('^')) ? 'index' : 'stock'; }
function stkAll() { return STK.data ? STK.data.quotes : []; }
function stkFind(symbol) { return stkAll().find(q => q.symbol === symbol) || (STK.extra && STK.extra.symbol === symbol ? STK.extra : null); }
function stkChange(q) {
  if (!q || q.change === null || q.change === undefined) return '—';
  // Cents, as the price is written (a stock under $1 has 4 decimals).
  const d = Math.abs(q.price || 0) >= 1 ? 2 : 4;
  const s = Math.abs(q.change).toLocaleString('en-US', { minimumFractionDigits: d, maximumFractionDigits: d });
  return `${q.change >= 0 ? '+' : '−'}${stkKind(q) === 'index' ? '' : '$'}${s} (${monPct(q.changePct)})`;
}
function stkVolume(v) {
  if (v === null || v === undefined) return '—';
  return v >= 1e9 ? (v / 1e9).toFixed(2) + 'B' : v >= 1e6 ? (v / 1e6).toFixed(2) + 'M' : v >= 1e3 ? (v / 1e3).toFixed(1) + 'K' : String(v);
}
function stkRange(lo, hi, k) { return lo === null || lo === undefined || hi === null || hi === undefined ? '—' : `${monPrice(lo, k, true)} – ${monPrice(hi, k, true)}`; }
/** The day's small line, green above the previous close and red below it. */
function stkSpark(q) {
  const pts = q.intraday || [];
  if (pts.length < 2) return '<span class="muted small mon-spark-empty">—</span>';
  const svg = monSparkSvg(pts);
  const up = q.previousClose === null || q.previousClose === undefined ? pts[pts.length - 1][1] >= pts[0][1] : pts[pts.length - 1][1] >= q.previousClose;
  return svg.replace(/class="mon-spark (up|down)"/, `class="mon-spark ${up ? 'up' : 'down'}"`);
}
function stkMarketHtml() {
  const q = stkAll().find(x => x.marketState), s = STK.data && STK.data.status;
  const st = (q && q.marketState) || 'closed', bad = s && s.error;
  return `<span class="aud-status ${bad ? 'bad' : st === 'open' ? 'ok' : 'warn'}"${bad ? ` data-tip="${esc(s.error)}"` : ''}><i></i>${esc(STK_STATES[st] || st)}${s && s.updatedUtc ? ' · updated ' + esc(ago(s.updatedUtc)) : ''}</span>`;
}
function stkCardHtml(q) {
  const k = stkKind(q), on = STK.sel === q.symbol;
  return `<button type="button" class="mon-card${on ? ' on' : ''}" data-act="stk-pick" data-symbol="${esc(q.symbol)}">
    <span class="mon-card-head"><b>${esc(q.symbol)}</b><span class="muted small">${esc(q.name || '')}</span></span>
    <span class="mon-card-price" data-stk-price="${esc(q.symbol)}">${esc(monPrice(q.price, k))}</span>
    <span class="mon-card-foot"><span class="mon-chip mon-${monTone(q.changePct)}" data-stk-chip="${esc(q.symbol)}">${esc(monPct(q.changePct))}</span><span class="muted small">today</span></span>
    <span class="mon-card-spark" data-stk-spark="${esc(q.symbol)}">${stkSpark(q)}</span></button>`;
}
function stkTableHtml(list) {
  if (!list.length) return '<p class="muted small">Nothing watched.</p>';
  return `<div class="table-wrap"><table class="table mon-table"><thead><tr><th>Ticker</th><th class="num">Price</th><th class="num">Change</th><th class="num">5D</th><th class="num">1M</th>
    <th class="num">Day range</th><th class="num">52-week range</th><th class="num">Volume</th><th>As of</th></tr></thead><tbody>
    ${list.map(q => { const k = stkKind(q); return `<tr class="clickable" data-act="stk-pick" data-symbol="${esc(q.symbol)}">
      <td><b>${esc(q.symbol)}</b> <span class="muted small">${esc(q.name || '')}</span></td><td class="num">${esc(monPrice(q.price, k))}</td>
      <td class="num mon-${monTone(q.changePct)}">${esc(stkChange(q))}</td><td class="num mon-${monTone(q.change5d)}">${esc(monPct(q.change5d))}</td>
      <td class="num mon-${monTone(q.change1m)}">${esc(monPct(q.change1m))}</td><td class="num">${esc(stkRange(q.dayLow, q.dayHigh, k))}</td>
      <td class="num">${esc(stkRange(q.yearLow, q.yearHigh, k))}</td><td class="num">${k === 'index' ? '—' : esc(stkVolume(q.volume))}</td>
      <td class="muted small">${q.marketTimeUtc ? esc(new Date(q.marketTimeUtc).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })) : '—'}</td></tr>`; }).join('')}</tbody></table></div>`;
}
function stkMainHtml() {
  const q = STK.sel && stkFind(STK.sel);
  if (!q) return '';
  const k = stkKind(q), watched = stkAll().some(x => x.symbol === q.symbol);
  return `<div class="card mon-main"><div class="card-body">
    <div class="mon-main-head">
      <div><div class="mon-main-title">${esc(q.symbol)} <span class="muted">${esc(q.name || '')}</span>${q.exchange ? ` <span class="muted small">· ${esc(q.exchange)}</span>` : ''}
          ${watched ? (STK.data.watchlist.length > 1 ? `<button type="button" class="btn xs ghost stk-unwatch" data-act="stk-watch-drop" data-symbol="${esc(q.symbol)}" data-tip="Take it off your watch list">Stop watching</button>` : '')
                    : `<span class="voice-tag">not watched</span> <button type="button" class="btn xs" data-act="stk-watch-add" data-symbol="${esc(q.symbol)}">Watch it</button>`}</div>
        <div class="mon-main-price"><span id="stk-main-price">${esc(monPrice(q.price, k))}</span><span class="mon-chip mon-${monTone(q.changePct)}" id="stk-main-change">${esc(stkChange(q))}</span>
          <span class="muted small">${esc(STK_STATES[q.marketState] || '')}${q.marketTimeUtc ? ' · ' + esc(new Date(q.marketTimeUtc).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })) : ''}</span></div></div>
      <div class="seg mon-ranges" role="group" aria-label="Range">${STK_RANGES.map(([r, l]) => `<button type="button" class="seg-btn${STK.range === r ? ' on' : ''}" data-act="stk-range" data-range="${r}">${l}</button>`).join('')}</div>
    </div>
    <div class="mon-chart" id="stk-chart"><div class="mon-chart-empty">Loading…</div></div>
    <div class="mon-stats">
      ${[['Open', monPrice(q.open, k)], ['Day range', stkRange(q.dayLow, q.dayHigh, k)], ['Prev close', monPrice(q.previousClose, k)], ['52-week range', stkRange(q.yearLow, q.yearHigh, k)],
         ['5 days', monPct(q.change5d), 'mon-' + monTone(q.change5d)], ['1 month', monPct(q.change1m), 'mon-' + monTone(q.change1m)], ...(k === 'index' ? [] : [['Volume', stkVolume(q.volume)]])]
        .map(([l, v, c]) => `<div class="mon-stat"><span class="muted small">${l}</span><b class="${c || ''}">${esc(v)}</b></div>`).join('')}
    </div>
    <p class="muted small mon-source" id="stk-range-note">Yahoo Finance · ${esc(q.currency || 'USD')} · most US prices are delayed about 15 minutes</p>
  </div></div>`;
}
/** Your watch list (each account keeps its own; the defaults until you save one). */
function stkWatchHtml() {
  const d = STK.data;
  return card('What you watch', `<p class="muted small">${d.custom ? 'Your own tickers, kept with your account.' : `The default tickers (${esc(d.defaults.join(', '))}); save your own and they are kept with your account.`}
      Write them as Yahoo Finance does: AAPL, BRK-B, indexes with ^ (^GSPC S&amp;P 500, ^IXIC Nasdaq, ^DJI Dow, ^RUT Russell 2000). Each is checked when you save.</p>
    <div class="form-grid"><label class="full">Tickers<input id="stk-watch" value="${esc(d.watchlist.join(', '))}" placeholder="^GSPC, AAPL, MSFT"></label></div>
    <div class="row mon-watch-foot"><button type="button" class="btn primary" data-act="stk-watch-save">Save</button>
      ${d.custom ? '<button type="button" class="btn" data-act="stk-watch-reset" data-tip="Watch the default tickers again">Use the defaults</button>' : ''}
      <span class="muted small">Read every ${esc(String(d.status.pollSeconds))} s while a market is open, every ${esc(String(d.status.closedPollMinutes))} min otherwise · ${esc(String(d.status.tickers || 0))} US tickers known for lookup</span></div>`, { cls: 'section' });
}
function stkBodyHtml() {
  const all = stkAll(), idx = all.filter(q => stkKind(q) === 'index'), stocks = all.filter(q => stkKind(q) !== 'index');
  return `<div class="mon" id="stk-root">
    <div class="stk-lookup"><div class="news-search">${icon('search')}<input id="stk-q" type="search" placeholder="Look up any ticker or company (AAPL, Tesla, JPM …)" autocomplete="off" aria-label="Look up a ticker"></div>
      <div class="menu stk-suggest hidden" id="stk-suggest"></div></div>
    ${idx.length ? `<div class="mon-section-title"><h2>Indexes</h2><span class="muted small">points</span></div><div class="mon-cards">${idx.map(stkCardHtml).join('')}</div>` : ''}
    <div class="mon-section-title"><h2>Stocks</h2><span class="muted small">USD · today against the previous close</span></div>
    <div class="mon-cards">${stocks.map(stkCardHtml).join('') || '<p class="muted small">No stocks watched.</p>'}</div>
    <div id="stk-main-box">${stkMainHtml()}</div>
    ${card('Quotes', stkTableHtml(all), { flush: true, cls: 'section' })}
    ${stkWatchHtml()}
  </div>`;
}
VIEWS.stocks = {
  live: false,
  async render() {
    setCrumbs([['Stocks', '/stocks']]);
    try { STK.data = await get('/api/stocks'); }
    catch (e) { return pageHead('Stocks') + card('', emptyState('Stocks are not available', e.message)); }
    if (!STK.sel || !stkFind(STK.sel)) { const first = stkAll().find(q => stkKind(q) !== 'index') || stkAll()[0]; STK.sel = first ? first.symbol : null; }
    return pageHead('Stocks', {
      subtitle: 'Stock and index quotes with their day, charts for any range, and a lookup of any US ticker. Agents can read them, and chats with Stocks turned on.',
      actions: `<span id="stk-status">${stkMarketHtml()}</span><button type="button" class="btn sm ghost" data-act="stk-refresh" data-tip="Read quotes now">↻ Refresh</button>`
    }) + stkBodyHtml();
  },
  after() { stkAfter(); }
};
function stkAfter() {
  stkLoadChart();
  const q = $('#stk-q');
  if (q) {
    q.addEventListener('input', () => { clearTimeout(STK.searchTimer); STK.searchTimer = setTimeout(stkSuggest, 200); });
    q.addEventListener('keydown', ev => {
      if (ev.key === 'Enter') { ev.preventDefault(); const first = $('#stk-suggest [data-act=stk-open]'); stkOpen(first ? first.dataset.symbol : q.value.trim().toUpperCase()); }
      if (ev.key === 'Escape') { $('#stk-suggest').classList.add('hidden'); }
    });
  }
  clearInterval(STK.timer);
  STK.timer = setInterval(stkTick, 20000);
}
async function stkSuggest() {
  const q = $('#stk-q'), box = $('#stk-suggest');
  if (!q || !box) return;
  const text = q.value.trim();
  if (!text) { box.classList.add('hidden'); return; }
  let hits = [];
  try { hits = await get('/api/stocks/search?q=' + enc(text) + '&limit=8'); } catch { /* none */ }
  if (q.value.trim() !== text) return;
  const typed = text.toUpperCase();
  const rows = hits.map(h => `<button type="button" class="stk-hit" data-act="stk-open" data-symbol="${esc(h.symbol)}"><b>${esc(h.symbol)}</b><span class="grow">${esc(h.name)}</span><span class="muted small">${esc(h.exchange)}${h.etf ? ' · ETF' : ''}</span></button>`);
  // What was typed, when it reads as a ticker the directory does not have (an index, a foreign listing, a currency pair).
  // First when no listed symbol starts with it (Enter takes the first row).
  if (!hits.some(h => h.symbol === typed) && /^\^?[A-Z0-9]{1,6}([.\-=][A-Z0-9]{1,3})?$/.test(typed)) rows[hits.some(h => h.symbol.startsWith(typed)) ? 'push' : 'unshift'](`<button type="button" class="stk-hit" data-act="stk-open" data-symbol="${esc(typed)}"><b>${esc(typed)}</b><span class="grow muted">Look it up on Yahoo Finance</span></button>`);
  box.innerHTML = rows.join('') || '<div class="muted small stk-none">No ticker matches.</div>';
  box.classList.remove('hidden');
}
/** Shows any ticker in the chart (a watched one, or one looked up). */
async function stkOpen(symbol) {
  if (!symbol) return;
  const box = $('#stk-suggest'); if (box) box.classList.add('hidden');
  if (!stkAll().some(q => q.symbol === symbol)) {
    try { STK.extra = await get('/api/stocks/quote?symbol=' + enc(symbol)); }
    catch (e) { toast(e.message, 'bad'); return; }
  }
  STK.sel = symbol;
  $$('#stk-root .mon-card').forEach(c => c.classList.toggle('on', c.dataset.symbol === symbol));
  const main = $('#stk-main-box'); if (main) { main.innerHTML = stkMainHtml(); main.scrollIntoView({ behavior: reducedMotion() ? 'auto' : 'smooth', block: 'start' }); }
  stkLoadChart();
}
async function stkLoadChart() {
  const q = STK.sel && stkFind(STK.sel), el = $('#stk-chart');
  if (!q || !el) return;
  const key = q.symbol + ':' + STK.range;
  STK.loading = key;
  let c;
  try { c = await get(`/api/stocks/chart?symbol=${enc(q.symbol)}&range=${enc(STK.range)}`); }
  catch (e) { el.innerHTML = `<div class="mon-chart-empty">${esc(e.message)}</div>`; return; }
  if (STK.loading !== key || !$('#stk-chart')) return;
  const k = stkKind(q), points = c.points || [];
  monDrawChart($('#stk-chart'), points, k, STK.range, { baseline: STK.range === '1d' && typeof c.previousClose === 'number' ? c.previousClose : undefined,
    empty: STK.range === '1d' ? 'No trades yet today: the chart starts when the market opens (prices are delayed about 15 minutes).' : 'No prices in this range.' });
  const note = $('#stk-range-note');
  if (note && points.length > 1 && STK.range !== '1d') {
    const ch = points[0][1] ? (points[points.length - 1][1] - points[0][1]) / points[0][1] * 100 : 0;
    note.innerHTML = `<span class="mon-chip mon-${monTone(ch)}">${esc(monPct(ch))} · ${esc((STK_RANGES.find(r => r[0] === STK.range) || [, ''])[1])}</span> Yahoo Finance · ${esc(q.currency || 'USD')} · most US prices are delayed about 15 minutes`;
  }
}
/** Every 20 s while the page is open: new quotes into the cards and table (a short flash), and today's chart. */
async function stkTick() {
  if (!$('#stk-root')) { clearInterval(STK.timer); STK.timer = null; return; }
  if (document.hidden) return;
  let d;
  try { d = await get('/api/stocks'); } catch { return; }
  if (!$('#stk-root')) return;
  const before = new Map(stkAll().map(q => [q.symbol, q.price]));
  STK.data = d;
  const st = $('#stk-status'); if (st) st.innerHTML = stkMarketHtml();
  for (const q of stkAll()) {
    const el = document.querySelector(`[data-stk-price="${CSS.escape(q.symbol)}"]`), old = before.get(q.symbol);
    if (el && old !== q.price) {
      el.textContent = monPrice(q.price, stkKind(q));
      if (!reducedMotion() && old !== undefined && old !== null) { el.classList.remove('flash-up', 'flash-down'); void el.offsetWidth; el.classList.add(q.price > old ? 'flash-up' : 'flash-down'); }
    }
    const chip = document.querySelector(`[data-stk-chip="${CSS.escape(q.symbol)}"]`); if (chip) { chip.className = 'mon-chip mon-' + monTone(q.changePct); chip.textContent = monPct(q.changePct); }
    const sp = document.querySelector(`[data-stk-spark="${CSS.escape(q.symbol)}"]`); if (sp) sp.innerHTML = stkSpark(q);
  }
  const sel = STK.sel && stkFind(STK.sel);
  if (sel) {
    const p = $('#stk-main-price'); if (p) p.textContent = monPrice(sel.price, stkKind(sel));
    const c = $('#stk-main-change'); if (c) { c.className = 'mon-chip mon-' + monTone(sel.changePct); c.textContent = stkChange(sel); }
  }
  const table = $('#stk-root .mon-table'); if (table) table.closest('.table-wrap').outerHTML = stkTableHtml(stkAll());
  if (STK.range === '1d') stkLoadChart();
}
ACTIONS['stk-pick'] = el => stkOpen(el.dataset.symbol);
ACTIONS['stk-open'] = el => { const q = $('#stk-q'); if (q) q.value = ''; stkOpen(el.dataset.symbol); };
ACTIONS['stk-range'] = el => {
  STK.range = el.dataset.range;
  $$('#stk-root .mon-ranges .seg-btn').forEach(b => b.classList.toggle('on', b.dataset.range === STK.range));
  stkLoadChart();
};
ACTIONS['stk-refresh'] = async () => {
  try { STK.data = await post('/api/stocks/refresh', {}); } catch (e) { toast(e.message, 'bad'); return; }
  toast('Quotes read', 'ok');
  stkTick();
};
async function stkSaveWatch(symbols) {
  try { STK.data = await put('/api/stocks/watchlist', { symbols }); } catch (e) { toast(e.message, 'bad'); return false; }
  toast('Watch list saved', 'ok');
  navigate('/stocks');
  return true;
}
ACTIONS['stk-watch-save'] = () => stkSaveWatch(($('#stk-watch').value || '').split(/[\s,;]+/).map(s => s.trim().toUpperCase()).filter(Boolean));
ACTIONS['stk-watch-add'] = el => stkSaveWatch([...STK.data.watchlist, el.dataset.symbol]);
ACTIONS['stk-watch-drop'] = el => stkSaveWatch(STK.data.watchlist.filter(s => s !== el.dataset.symbol));
ACTIONS['stk-watch-reset'] = async () => {
  try { STK.data = await del('/api/stocks/watchlist'); } catch (e) { toast(e.message, 'bad'); return; }
  toast('Watching the default tickers', 'ok');
  navigate('/stocks');
};
document.addEventListener('click', ev => { const box = $('#stk-suggest'); if (box && !ev.target.closest('.stk-lookup')) box.classList.add('hidden'); });
let stkResize = null;
window.addEventListener('resize', () => { clearTimeout(stkResize); stkResize = setTimeout(() => { if ($('#stk-chart')) stkLoadChart(); }, 200); });
