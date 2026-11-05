'use strict';
/* Agentic console - Polymarket: prediction markets from Polymarket's public APIs. Markets: open events busiest first (or by
   category, search, closed ones), each with its outcomes' chances; one opened shows each market's odds, best bid and ask,
   last trade, a chart of its chance, the live order book, recent trades and top holders. New: markets that just opened.
   Leaderboard: top traders. Wallet: any address's positions and activity. Live prices come from the server's own stream
   (it follows Polymarket's market channel); nothing is polled. Agents read the same with the polymarket tool; chats when
   Polymarket is on. Charts are money.js's. */

const PM = { tab: 'markets', q: '', cat: '', status: 'open', sort: 'volume24h', events: [], more: false, next: 0, info: null, sel: null, detail: null,
  market: 0, outcome: 0, range: '1w', es: null, esKey: '', esTimer: null, searchTimer: null, lb: { period: 'week', sort: 'pnl' }, wallet: '', loading: '' };
const PM_RANGES = [['1d', '1D'], ['1w', '1W'], ['1m', '1M'], ['max', 'Max']];
const PM_SORTS = [['volume24h', '24h volume'], ['volume', 'Total volume'], ['liquidity', 'Liquidity'], ['ending', 'Ending soon'], ['newest', 'Newest']];
const PM_TABS = [['markets', 'Markets'], ['new', 'New'], ['leaderboard', 'Leaderboard'], ['wallet', 'Wallet']];

// ------------------------------------------------------------------ numbers
/** A chance (0-1) as a percent: 62%, <1%, >99%. */
function pmPct(p) {
  if (p === null || p === undefined || isNaN(p)) return '—';
  const v = p * 100;
  return v > 0 && v < 1 ? '<1%' : v > 99 && v < 100 ? '>99%' : Math.round(v) + '%';
}
/** A price (0-1 USD a share) in cents: 62¢, 0.4¢. */
function pmCents(p) {
  if (p === null || p === undefined || isNaN(p)) return '—';
  const c = p * 100;
  return (c < 1 && c > 0 ? c.toFixed(1) : c % 1 ? c.toFixed(1) : c.toFixed(0)) + '¢';
}
function pmMoney(v) {
  if (v === null || v === undefined || isNaN(v)) return '—';
  const a = Math.abs(v);
  return (v < 0 ? '−$' : '$') + (a >= 1e9 ? (a / 1e9).toFixed(1) + 'B' : a >= 1e6 ? (a / 1e6).toFixed(1) + 'M' : a >= 1e3 ? (a / 1e3).toFixed(1) + 'K' : Math.round(a).toLocaleString('en-US'));
}
function pmPts(d) { return d === null || d === undefined || isNaN(d) ? '' : `${d > 0 ? '+' : d < 0 ? '−' : ''}${Math.abs(d * 100).toFixed(1)} pts`; }
function pmSize(v) { return v === null || v === undefined ? '—' : Number(v) >= 1000 ? Math.round(Number(v)).toLocaleString('en-US') : Number(v).toFixed(2).replace(/\.00$/, ''); }
function pmDate(iso) { return iso ? new Date(iso).toLocaleDateString([], { month: 'short', day: 'numeric', year: new Date(iso).getFullYear() !== new Date().getFullYear() ? 'numeric' : undefined }) : '—'; }
function pmWhen(sec) { return sec ? new Date(sec * 1000).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }) : '—'; }
function pmShort(addr) { return addr ? addr.slice(0, 6) + '…' + addr.slice(-4) : ''; }
function pmIsBinary(m) { return m.outcomes.length === 2 && /^yes$/i.test(m.outcomes[0]) && /^no$/i.test(m.outcomes[1]); }
/** Sets the width of every bar (data-pw, percent) under root: the page may not use inline styles. */
function pmBars(root) { $$('[data-pw]', root || document).forEach(e => { e.style.width = Math.max(0, Math.min(100, Number(e.dataset.pw))) + '%'; }); }

// ------------------------------------------------------------------ cards
function pmStatusHtml() {
  const s = PM.info && PM.info.status;
  if (!s) return '';
  const tip = s.connected ? `Following ${s.following} outcomes live${s.lastMessageUtc ? ' · last message ' + ago(s.lastMessageUtc) : ''}` : (s.error || 'Connecting to Polymarket…');
  return `<span class="aud-status ${s.connected ? 'ok' : 'warn'}" data-tip="${esc(tip)}"><i></i>${s.connected ? 'Live' : 'Reconnecting'}</span>`;
}
function pmPic(ev, cls) {
  return ev.hasImage ? `<img class="${cls}" src="/api/polymarket/events/${enc(ev.id)}/image" alt="" loading="lazy" width="44" height="44">` : `<span class="${cls} pm-pic-none">${esc((ev.title || '?').slice(0, 1))}</span>`;
}
/** One market's chance on a card: the big number for a Yes/No event, a row with a bar for each of several. */
function pmCardBody(ev) {
  const ms = ev.markets;
  if (!ms.length) return '<p class="muted small">No open market.</p>';
  if (ms.length === 1 && pmIsBinary(ms[0])) {
    const m = ms[0], p = m.prices[0];
    return `<div class="pm-big"><b data-pm-token="${esc(m.tokens[0] || '')}" data-pm-show="pct">${esc(pmPct(p))}</b><span class="muted small">chance</span>
        ${m.dayChange ? `<span class="mon-chip mon-${monTone(m.dayChange)}">${esc(pmPts(m.dayChange))}</span>` : ''}</div>
      <div class="pm-yesno"><span class="pm-yes">Yes <b data-pm-token="${esc(m.tokens[0] || '')}" data-pm-show="cents">${esc(pmCents(p))}</b></span>
        <span class="pm-no">No <b data-pm-token="${esc(m.tokens[1] || '')}" data-pm-show="cents">${esc(pmCents(m.prices[1]))}</b></span></div>`;
  }
  const rows = ms.slice(0, 3).map(m => {
    const p = m.prices[0];
    return `<div class="pm-row"><span class="pm-row-name">${esc(m.name || m.question)}</span><span class="pm-bar"><span data-pw="${((p || 0) * 100).toFixed(1)}" data-pm-token="${esc(m.tokens[0] || '')}" data-pm-show="bar"></span></span>
      <b data-pm-token="${esc(m.tokens[0] || '')}" data-pm-show="pct">${esc(pmPct(p))}</b></div>`;
  }).join('');
  return rows + (ev.openMarkets > 3 ? `<div class="muted small">+${ev.openMarkets - 3} more</div>` : '');
}
function pmCardHtml(ev) {
  const live = ev.markets.some(m => m.live);
  return `<button type="button" class="pm-card${PM.sel === ev.id ? ' on' : ''}" data-act="pm-open" data-id="${esc(ev.id)}">
    <span class="pm-card-head">${pmPic(ev, 'pm-pic')}<span class="pm-title">${esc(ev.title)}</span></span>
    <span class="pm-card-body">${pmCardBody(ev)}</span>
    <span class="pm-card-foot"><span>${esc(pmMoney(ev.volume24h))} 24h · ${esc(pmMoney(ev.volume))} vol</span>
      <span>${ev.category ? `<span class="voice-tag">${esc(ev.category)}</span> ` : ''}${ev.closed ? 'Closed' : 'Ends ' + esc(pmDate(ev.endUtc))}${live ? ' <i class="pm-live" data-tip="Live prices"></i>' : ''}</span></span>
  </button>`;
}

// ------------------------------------------------------------------ an event opened
function pmSelMarket() { return PM.detail && PM.detail.markets[PM.market]; }
function pmDetailHtml() {
  const ev = PM.detail;
  if (!ev) return PM.sel ? '<div class="card"><div class="card-body"><div class="mon-chart-empty">Loading…</div></div></div>' : '';
  const m = pmSelMarket();
  const multi = ev.markets.length > 1;
  return `<div class="card pm-detail"><div class="card-body">
    <div class="pm-detail-head">${pmPic(ev, 'pm-pic lg')}<div class="grow"><div class="pm-detail-title">${esc(ev.title)}</div>
      <div class="muted small">${ev.category ? esc(ev.category) + ' · ' : ''}${ev.closed ? 'Closed' : 'Ends ' + esc(pmDate(ev.endUtc))} · ${esc(pmMoney(ev.volume))} volume · ${esc(pmMoney(ev.volume24h))} in 24h · ${esc(pmMoney(ev.liquidity))} liquidity</div></div>
      ${ev.url ? `<a class="btn sm" href="${esc(ev.url)}" target="_blank" rel="noopener noreferrer">Polymarket ↗</a>` : ''}
      <button type="button" class="btn sm ghost icon-only" data-act="pm-close" aria-label="Close">${icon('x')}</button></div>
    ${multi ? `<div class="table-wrap pm-markets"><table class="table"><thead><tr><th>Outcome</th><th class="num">Chance</th><th class="num">Bid</th><th class="num">Ask</th><th class="num">Last</th><th class="num">24h vol</th></tr></thead><tbody>
      ${ev.markets.map((x, i) => `<tr class="clickable${i === PM.market ? ' on' : ''}" data-act="pm-market" data-i="${i}"><td>${esc(x.name || x.question)}${x.open ? '' : ' <span class="voice-tag">closed</span>'}</td>
        <td class="num"><b data-pm-token="${esc(x.tokens[0] || '')}" data-pm-show="pct">${esc(pmPct(x.prices[0]))}</b></td>
        <td class="num" data-pm-token="${esc(x.tokens[0] || '')}" data-pm-show="bid">${esc(pmCents(x.bestBid))}</td><td class="num" data-pm-token="${esc(x.tokens[0] || '')}" data-pm-show="ask">${esc(pmCents(x.bestAsk))}</td>
        <td class="num" data-pm-token="${esc(x.tokens[0] || '')}" data-pm-show="last">${esc(pmCents(x.lastTrade))}</td><td class="num">${esc(pmMoney(x.volume24h))}</td></tr>`).join('')}</tbody></table></div>` : ''}
    ${m ? pmMarketHtml(m, multi) : ''}
    ${PM.description ? `<details class="pm-rules"><summary>Rules</summary><p class="small">${esc(PM.description)}</p></details>` : ''}
  </div></div>`;
}
function pmMarketHtml(m, multi) {
  const tok = m.tokens[PM.outcome] || m.tokens[0];
  // Gamma gives the first outcome's bid and ask; the second of two is its mirror (a No bid is 1 − the Yes ask).
  const flip = PM.outcome === 1 && m.outcomes.length === 2, inv = v => v === null || v === undefined ? null : 1 - v;
  const bid = PM.outcome === 0 ? m.bestBid : flip ? inv(m.bestAsk) : null, ask = PM.outcome === 0 ? m.bestAsk : flip ? inv(m.bestBid) : null;
  return `<div class="pm-market">
    ${multi ? `<div class="pm-market-q">${esc(m.question)}</div>` : ''}
    <div class="pm-outcomes">${m.outcomes.map((o, i) => `<button type="button" class="pm-outcome${i === PM.outcome ? ' on' : ''}${m.won && m.won[i] ? ' won' : ''}" data-act="pm-outcome" data-i="${i}">
      <span>${esc(o)}</span><b data-pm-token="${esc(m.tokens[i] || '')}" data-pm-show="pct">${esc(pmPct(m.prices[i]))}</b><span class="muted small" data-pm-token="${esc(m.tokens[i] || '')}" data-pm-show="cents">${esc(pmCents(m.prices[i]))}</span></button>`).join('')}</div>
    <div class="mon-stats">
      ${[['Best bid', 'bid', pmCents(bid)], ['Best ask', 'ask', pmCents(ask)], ['Spread', 'spread', bid !== null && ask !== null ? pmCents(ask - bid) : '—'],
         ['Last trade', 'last', pmCents(PM.outcome === 0 ? m.lastTrade : null)], ['24h volume', '', pmMoney(m.volume24h)], ['Volume', '', pmMoney(m.volume)], ['Liquidity', '', pmMoney(m.liquidity)], ['Ends', '', pmDate(m.endUtc)]]
        .map(([l, k, v]) => `<div class="mon-stat"><span class="muted small">${l}</span><b${k ? ` data-pm-token="${esc(tok)}" data-pm-show="${k}"` : ''}>${esc(v)}</b></div>`).join('')}
    </div>
    <div class="mon-main-head"><div class="muted small">Chance of <b>${esc(m.outcomes[PM.outcome] || '')}</b></div>
      <div class="seg mon-ranges" role="group" aria-label="Range">${PM_RANGES.map(([r, l]) => `<button type="button" class="seg-btn${PM.range === r ? ' on' : ''}" data-act="pm-range" data-range="${r}">${l}</button>`).join('')}</div></div>
    <div class="mon-chart" id="pm-chart"><div class="mon-chart-empty">Loading…</div></div>
    <div class="pm-grid2">
      <div><h3 class="pm-h">Order book <span class="muted small" id="pm-book-live"></span></h3><div id="pm-book"><p class="muted small">Loading…</p></div></div>
      <div><h3 class="pm-h">Recent trades</h3><div id="pm-trades"><p class="muted small">Loading…</p></div></div>
    </div>
    <h3 class="pm-h">Top holders</h3><div id="pm-holders"><p class="muted small">Loading…</p></div>
    ${m.url ? `<p class="muted small mon-source">Polymarket · <a href="${esc(m.url)}" target="_blank" rel="noopener noreferrer">${esc(m.url.replace(/^https?:\/\//, ''))}</a> · prices are what traders pay for a share that pays $1 if it happens</p>` : ''}
  </div>`;
}
function pmBookHtml(b) {
  if (!b || (!b.bids.length && !b.asks.length)) return '<p class="muted small">No orders.</p>';
  const max = Math.max(1, ...b.bids.map(x => x[1]), ...b.asks.map(x => x[1]));
  const side = (list, cls) => list.map(([p, s]) => `<div class="pm-level ${cls}"><span class="pm-depth"><span data-pw="${(s / max * 100).toFixed(1)}"></span></span><b>${esc(pmCents(p))}</b><span>${esc(pmSize(s))}</span></div>`).join('');
  return `<div class="pm-book"><div><div class="pm-level head"><span>Bid</span><span>Shares</span></div>${side(b.bids, 'bid') || '<p class="muted small">None</p>'}</div>
    <div><div class="pm-level head"><span>Ask</span><span>Shares</span></div>${side(b.asks, 'ask') || '<p class="muted small">None</p>'}</div></div>
    ${b.lastTrade !== null && b.lastTrade !== undefined ? `<p class="muted small">Last trade ${esc(pmCents(b.lastTrade))}${b.lastSide ? ' · ' + esc(b.lastSide.toLowerCase()) : ''}${b.lastTradeUtc ? ' · ' + esc(ago(b.lastTradeUtc)) : ''}</p>` : ''}`;
}
function pmTradesHtml(list) {
  if (!list.length) return '<p class="muted small">No trades yet.</p>';
  return `<div class="table-wrap"><table class="table pm-small"><thead><tr><th>When</th><th>Trader</th><th>Side</th><th>Outcome</th><th class="num">Price</th><th class="num">Shares</th></tr></thead><tbody>
    ${list.slice(0, 15).map(t => `<tr><td class="muted small">${esc(pmWhen(t.timestamp))}</td><td>${t.proxy_wallet ? `<button type="button" class="pm-link" data-act="pm-wallet" data-address="${esc(t.proxy_wallet)}">${esc(t.name || t.pseudonym || pmShort(t.proxy_wallet))}</button>` : esc(t.name || '—')}</td>
      <td class="${t.side === 'BUY' ? 'mon-up' : 'mon-down'}">${esc((t.side || '').toLowerCase())}</td><td>${esc(t.outcome || '')}</td><td class="num">${esc(pmCents(t.price))}</td><td class="num">${esc(pmSize(t.size))}</td></tr>`).join('')}</tbody></table></div>`;
}
function pmHoldersHtml(list, m) {
  const cols = (list || []).map(tk => ({ outcome: m.outcomes[m.tokens.indexOf(tk.token_id)] || '', holders: tk.holders || [] })).filter(c => c.holders.length);
  if (!cols.length) return '<p class="muted small">No holders listed.</p>';
  return `<div class="pm-grid2">${cols.map(c => `<div><div class="muted small">${esc(c.outcome)}</div><div class="table-wrap"><table class="table pm-small"><tbody>
    ${c.holders.slice(0, 10).map(h => `<tr><td>${h.proxy_wallet ? `<button type="button" class="pm-link" data-act="pm-wallet" data-address="${esc(h.proxy_wallet)}">${esc(h.name || h.pseudonym || pmShort(h.proxy_wallet))}</button>` : esc(h.name || '—')}${h.verified ? ' ✓' : ''}</td><td class="num">${esc(pmSize(h.amount))} shares</td></tr>`).join('')}</tbody></table></div></div>`).join('')}</div>`;
}

// ------------------------------------------------------------------ the page
function pmTabsHtml() {
  return `<div class="tabs">${PM_TABS.map(([k, l]) => `<a href="/polymarket${k === 'markets' ? '' : '/' + k}" data-nav class="${PM.tab === k ? 'active' : ''}">${l}</a>`).join('')}</div>`;
}
function pmToolbarHtml() {
  const cats = (PM.info && PM.info.categories) || [];
  return `<div class="pm-toolbar"><div class="news-search">${icon('search')}<input id="pm-q" type="search" placeholder="Search markets (election, bitcoin, Fed, Super Bowl …)" value="${esc(PM.q)}" autocomplete="off" aria-label="Search markets"></div>
      <select id="pm-status" aria-label="Status"><option value="open"${PM.status === 'open' ? ' selected' : ''}>Open</option><option value="closed"${PM.status === 'closed' ? ' selected' : ''}>Closed &amp; resolved</option></select>
      <select id="pm-sort" aria-label="Sort"${PM.q ? ' disabled' : ''}>${PM_SORTS.map(([k, l]) => `<option value="${k}"${PM.sort === k ? ' selected' : ''}>${l}</option>`).join('')}</select></div>
    <div class="pm-cats">${[['', 'All'], ...cats.map(c => [c.slug, c.name])].map(([k, l]) => `<button type="button" class="chip-toggle${PM.cat === k ? ' on' : ''}" data-act="pm-cat" data-cat="${k}">${esc(l)}</button>`).join('')}</div>`;
}
function pmGridHtml() {
  if (PM.loading === 'list' && !PM.events.length) return '<div class="mon-chart-empty">Loading markets…</div>';
  if (!PM.events.length) return card('', emptyState(PM.q ? 'No market matches' : 'No markets', PM.q ? 'Try other words, another category, or closed markets.' : ''));
  return `<div class="pm-cards">${PM.events.map(pmCardHtml).join('')}</div>${PM.more ? `<div class="pm-more"><button type="button" class="btn" data-act="pm-more">Load more</button></div>` : ''}`;
}
function pmMarketsHtml() {
  return `${pmToolbarHtml()}<div id="pm-detail-box">${pmDetailHtml()}</div><div id="pm-grid">${pmGridHtml()}</div>`;
}
function pmNewHtml() {
  const fresh = (PM.info && PM.info.newMarkets) || [];
  return `${card('Opened while the server listened', fresh.length ? `<div class="table-wrap"><table class="table"><thead><tr><th>Market</th><th>Outcomes</th><th>Seen</th></tr></thead><tbody>
      ${fresh.map(n => `<tr><td>${n.url ? `<a href="${esc(n.url)}" target="_blank" rel="noopener noreferrer">${esc(n.question || n.slug)}</a>` : esc(n.question || n.slug)}</td><td>${esc((n.outcomes || []).join(' / '))}</td><td class="muted small">${esc(ago(n.seenUtc))}</td></tr>`).join('')}</tbody></table></div>`
      : '<p class="muted small">None yet: markets appear here the moment Polymarket opens them.</p>', { flush: !!fresh.length, cls: 'section' })}
    <div class="mon-section-title"><h2>Newest events</h2></div><div id="pm-grid">${pmGridHtml()}</div>`;
}
function pmLeaderboardHtml() {
  return `<div class="pm-toolbar"><div class="seg" role="group" aria-label="Period">${[['day', 'Day'], ['week', 'Week'], ['month', 'Month'], ['all', 'All time']].map(([k, l]) => `<button type="button" class="seg-btn${PM.lb.period === k ? ' on' : ''}" data-act="pm-lb" data-period="${k}">${l}</button>`).join('')}</div>
    <div class="seg" role="group" aria-label="Sort">${[['pnl', 'Profit'], ['volume', 'Volume']].map(([k, l]) => `<button type="button" class="seg-btn${PM.lb.sort === k ? ' on' : ''}" data-act="pm-lb" data-sort="${k}">${l}</button>`).join('')}</div></div>
    <div id="pm-lb">${card('', '<p class="muted small">Loading…</p>')}</div>`;
}
function pmWalletHtml() {
  return `<div class="pm-toolbar"><div class="news-search">${icon('search')}<input id="pm-address" type="search" placeholder="Wallet address (0x…)" value="${esc(PM.wallet)}" autocomplete="off" spellcheck="false" aria-label="Wallet address"></div>
    <button type="button" class="btn primary" data-act="pm-wallet-go">Look up</button></div><div id="pm-wallet">${PM.wallet ? card('', '<p class="muted small">Loading…</p>') : card('', emptyState('Any trader\'s positions', 'Paste a wallet address, or pick a trader on the Leaderboard or in a market\'s trades.'))}</div>`;
}
VIEWS.polymarket = {
  live: false,
  async render(params) {
    PM.tab = (params && params.args && params.args[0]) || 'markets';
    setCrumbs([['Polymarket', '/polymarket']]);
    try { PM.info = await get('/api/polymarket'); }
    catch (e) { return pageHead('Polymarket') + card('', emptyState('Polymarket is not available', e.message)); }
    if (PM.tab === 'markets' || PM.tab === 'new') PM.loading = 'list';
    const body = PM.tab === 'new' ? pmNewHtml() : PM.tab === 'leaderboard' ? pmLeaderboardHtml() : PM.tab === 'wallet' ? pmWalletHtml() : pmMarketsHtml();
    return pageHead('Polymarket', {
      subtitle: 'Prediction markets: what traders think will happen, as odds. Agents can read them, and chats with Polymarket turned on.',
      actions: `<span id="pm-status-chip">${pmStatusHtml()}</span>`
    }) + `<div class="mon" id="pm-root">${pmTabsHtml()}${body}</div>`;
  },
  after() { pmAfter(); }
};
function pmAfter() {
  if (PM.tab === 'markets' || PM.tab === 'new') pmLoad(false);
  if (PM.tab === 'leaderboard') pmLoadLeaderboard();
  if (PM.tab === 'wallet' && PM.wallet) pmLoadWallet();
  const q = $('#pm-q');
  if (q) q.addEventListener('input', () => { clearTimeout(PM.searchTimer); PM.searchTimer = setTimeout(() => { PM.q = q.value.trim(); PM.sel = null; PM.detail = null; pmLoad(false); }, 350); });
  const st = $('#pm-status'); if (st) st.addEventListener('change', () => { PM.status = st.value; pmLoad(false); });
  const so = $('#pm-sort'); if (so) so.addEventListener('change', () => { PM.sort = so.value; pmLoad(false); });
  const ad = $('#pm-address'); if (ad) ad.addEventListener('keydown', ev => { if (ev.key === 'Enter') ACTIONS['pm-wallet-go'](); });
  if (PM.tab === 'markets' && PM.sel) pmOpen(PM.sel, false);
}
/** Events for the grid (more: the next page after the ones shown). */
async function pmLoad(more) {
  const sort = PM.tab === 'new' ? 'newest' : PM.sort;
  const key = [PM.q, PM.cat, PM.status, sort, more ? PM.next : 0].join('|');
  PM.loading = 'list';
  if (!more) { const g = $('#pm-grid'); if (g && !PM.events.length) g.innerHTML = pmGridHtml(); }
  let d;
  try { d = await get(`/api/polymarket/events?q=${enc(PM.tab === 'new' ? '' : PM.q)}&category=${enc(PM.tab === 'new' ? '' : PM.cat)}&status=${enc(PM.tab === 'new' ? 'open' : PM.status)}&sort=${enc(sort)}&offset=${more ? PM.next : 0}&limit=24`); }
  catch (e) { PM.loading = ''; const g = $('#pm-grid'); if (g) g.innerHTML = card('', emptyState('Polymarket did not answer', e.message)); return; }
  if (key !== [PM.q, PM.cat, PM.status, PM.tab === 'new' ? 'newest' : PM.sort, more ? PM.next : 0].join('|')) return;
  PM.loading = '';
  PM.events = more ? PM.events.concat(d.events.filter(e => !PM.events.some(x => x.id === e.id))) : d.events;
  PM.more = d.more; PM.next = d.next;
  if (d.status && PM.info) { PM.info.status = d.status; const c = $('#pm-status-chip'); if (c) c.innerHTML = pmStatusHtml(); }
  const g = $('#pm-grid'); if (g) { g.innerHTML = pmGridHtml(); pmBars(g); }
  pmStream();
}
/** Opens an event above the grid: its markets, then the chosen one's chart, book, trades and holders. */
async function pmOpen(id, scroll = true) {
  if (PM.sel !== id) { PM.market = 0; PM.outcome = 0; }
  PM.sel = id;
  $$('#pm-root .pm-card').forEach(c => c.classList.toggle('on', c.dataset.id === id));
  const box = $('#pm-detail-box');
  if (!box) return;
  if (!PM.detail || PM.detail.id !== id) { PM.detail = null; box.innerHTML = pmDetailHtml(); }
  if (scroll) box.scrollIntoView({ behavior: reducedMotion() ? 'auto' : 'smooth', block: 'start' });
  let d;
  try { d = await get(`/api/polymarket/events/${enc(id)}${PM.status === 'closed' ? '?closed=1' : ''}`); }
  catch (e) { box.innerHTML = card('', emptyState('This event did not load', e.message)); return; }
  if (PM.sel !== id) return;
  PM.detail = d.event; PM.description = d.description;
  if (PM.market >= PM.detail.markets.length) PM.market = 0;
  pmRenderDetail();
}
function pmRenderDetail() {
  const box = $('#pm-detail-box');
  if (!box) return;
  box.innerHTML = pmDetailHtml();
  pmBars(box);
  pmLoadMarket();
  pmStream();
}
async function pmLoadMarket() {
  const m = pmSelMarket();
  if (!m) return;
  const tok = m.tokens[PM.outcome] || m.tokens[0];
  pmLoadChart();
  get(`/api/polymarket/book?token=${enc(tok)}`).then(b => { if (pmSelToken() === tok) pmShowBook(b); }).catch(e => { const el = $('#pm-book'); if (el) el.innerHTML = `<p class="muted small">${esc(e.message)}</p>`; });
  if (m.conditionId) {
    get(`/api/polymarket/trades?market=${enc(m.conditionId)}&limit=20`).then(t => { const el = $('#pm-trades'); if (el && pmSelMarket() === m) el.innerHTML = pmTradesHtml(t.trades || []); })
      .catch(e => { const el = $('#pm-trades'); if (el) el.innerHTML = `<p class="muted small">${esc(e.message)}</p>`; });
    get(`/api/polymarket/holders?market=${enc(m.conditionId)}&limit=10`).then(h => { const el = $('#pm-holders'); if (el && pmSelMarket() === m) el.innerHTML = pmHoldersHtml(h.holders, m); })
      .catch(e => { const el = $('#pm-holders'); if (el) el.innerHTML = `<p class="muted small">${esc(e.message)}</p>`; });
  }
}
function pmSelToken() { const m = pmSelMarket(); return m ? (m.tokens[PM.outcome] || m.tokens[0]) : null; }
function pmShowBook(b) {
  const el = $('#pm-book');
  if (!el) return;
  el.innerHTML = pmBookHtml(b);
  pmBars(el);
  const live = $('#pm-book-live'); if (live) live.textContent = b.live ? '· live' : '';
}
async function pmLoadChart() {
  const tok = pmSelToken(), el = $('#pm-chart');
  if (!tok || !el) return;
  const key = tok + ':' + PM.range;
  PM.chartKey = key;
  let h;
  try { h = await get(`/api/polymarket/history?token=${enc(tok)}&range=${enc(PM.range)}`); }
  catch (e) { el.innerHTML = `<div class="mon-chart-empty">${esc(e.message)}</div>`; return; }
  if (PM.chartKey !== key || !$('#pm-chart')) return;
  monDrawChart($('#pm-chart'), h.points || [], 'odds', h.chartRange || '7d', { empty: 'No trades in this range yet.' });
}

// ------------------------------------------------------------------ live prices (the server's stream)
/** (Re)opens the page's stream for the events shown and the open book; at most once per change. */
function pmStream() {
  clearTimeout(PM.esTimer);
  PM.esTimer = setTimeout(() => {
    if (!$('#pm-root')) { pmStopStream(); return; }
    const ids = [...new Set([...(PM.events || []).map(e => e.id), ...(PM.sel ? [PM.sel] : [])])].slice(0, 120);
    const book = PM.tab === 'markets' ? pmSelToken() : null;
    const key = ids.join(',') + '|' + (book || '');
    if (key === PM.esKey && PM.es && PM.es.readyState !== EventSource.CLOSED) return;
    pmStopStream();
    if (!ids.length) return;
    PM.esKey = key;
    const es = new EventSource(`/api/polymarket/stream?events=${enc(ids.join(','))}${book ? '&book=' + enc(book) : ''}`);
    PM.es = es;
    es.onmessage = msg => {
      if (!$('#pm-root')) { pmStopStream(); return; }
      let d; try { d = JSON.parse(msg.data); } catch { return; }
      if (d.type === 'prices') pmApplyPrices(d.items);
      else if (d.type === 'book' && d.token === pmSelToken()) pmShowBook({ bids: d.bids, asks: d.asks, live: true, lastTrade: d.lastTrade, lastSide: d.lastSide, lastTradeUtc: d.lastTradeUtc });
      else if (d.type === 'status' && PM.info) { PM.info.status = d.status; const c = $('#pm-status-chip'); if (c) c.innerHTML = pmStatusHtml(); }
    };
  }, 300);
}
function pmStopStream() { if (PM.es) { PM.es.close(); PM.es = null; } PM.esKey = ''; }
/** New prices into every place that shows them (a short flash where a chance moved). */
function pmApplyPrices(items) {
  for (const it of items) {
    const els = document.querySelectorAll(`[data-pm-token="${CSS.escape(it.t)}"]`);
    if (!els.length) continue;
    for (const el of els) {
      const show = el.dataset.pmShow;
      if (show === 'pct' || show === 'cents' || show === 'bar') {
        if (it.p === null || it.p === undefined) continue;
        if (show === 'bar') { el.dataset.pw = (it.p * 100).toFixed(1); el.style.width = Math.max(0, Math.min(100, it.p * 100)) + '%'; continue; }
        const text = show === 'pct' ? pmPct(it.p) : pmCents(it.p);
        if (el.textContent !== text) {
          const up = parseFloat(el.textContent) < parseFloat(text);
          el.textContent = text;
          if (!reducedMotion()) { el.classList.remove('flash-up', 'flash-down'); void el.offsetWidth; el.classList.add(up ? 'flash-up' : 'flash-down'); }
        }
      } else {
        // Only what the channel knows replaces what is shown (a token with no trade yet keeps Polymarket's last trade).
        const has = v => v !== null && v !== undefined;
        if (show === 'bid' && has(it.b)) el.textContent = pmCents(it.b);
        else if (show === 'ask' && has(it.a)) el.textContent = pmCents(it.a);
        else if (show === 'last' && has(it.l)) el.textContent = pmCents(it.l);
        else if (show === 'spread' && has(it.a) && has(it.b)) el.textContent = pmCents(it.a - it.b);
      }
    }
  }
}

// ------------------------------------------------------------------ leaderboard and wallets
async function pmLoadLeaderboard() {
  const el = $('#pm-lb');
  let d;
  try { d = await get(`/api/polymarket/leaderboard?period=${enc(PM.lb.period)}&sort=${enc(PM.lb.sort)}&limit=50`); }
  catch (e) { if (el) el.innerHTML = card('', emptyState('The leaderboard did not load', e.message)); return; }
  if (!$('#pm-lb')) return;
  const rows = d.traders || [];
  $('#pm-lb').innerHTML = card('', rows.length ? `<div class="table-wrap"><table class="table"><thead><tr><th class="num">#</th><th>Trader</th><th class="num">Profit</th><th class="num">Volume</th></tr></thead><tbody>
    ${rows.map(t => `<tr><td class="num">${esc(String(t.rank))}</td><td><button type="button" class="pm-link" data-act="pm-wallet" data-address="${esc(t.user_id)}">${esc(t.user_name || pmShort(t.user_id))}</button>${t.verified ? ' ✓' : ''}</td>
      <td class="num mon-${monTone(t.pnl)}">${esc(pmMoney(t.pnl))}</td><td class="num">${esc(pmMoney(t.volume))}</td></tr>`).join('')}</tbody></table></div>` : '<p class="muted small">No traders.</p>', { flush: rows.length > 0 });
}
async function pmLoadWallet() {
  const el = $('#pm-wallet');
  if (!el) return;
  el.innerHTML = card('', '<p class="muted small">Loading…</p>');
  let d;
  try { d = await get('/api/polymarket/wallet?address=' + enc(PM.wallet)); }
  catch (e) { el.innerHTML = card('', emptyState('That wallet did not load', e.message)); return; }
  const link = r => r.event_slug ? `<a href="https://polymarket.com/event/${enc(r.event_slug)}" target="_blank" rel="noopener noreferrer">${esc(r.title || r.slug)}</a>` : esc(r.title || r.slug || '—');
  const table = list => `<div class="table-wrap"><table class="table"><thead><tr><th>Market</th><th>Outcome</th><th class="num">Shares</th><th class="num">Avg</th><th class="num">Now</th><th class="num">Value</th><th class="num">P&amp;L</th></tr></thead><tbody>
      ${list.map(p => `<tr><td>${link(p)}</td><td>${esc(p.outcome || '')}</td><td class="num">${esc(pmSize(p.current_size))}</td><td class="num">${esc(pmCents(p.avg_price))}</td><td class="num">${esc(pmCents(p.current_price))}</td>
        <td class="num">${esc(pmMoney(p.current_value))}</td><td class="num mon-${monTone(p.total_pnl)}">${esc(pmMoney(p.total_pnl))}${p.percent_pnl !== undefined && p.percent_pnl !== null ? ` <span class="muted small">${esc(monPct(p.percent_pnl))}</span>` : ''}</td></tr>`).join('')}</tbody></table></div>`;
  // Open positions first; settled ones (resolved, worth nothing now) folded away.
  const open = d.positions.filter(p => String(p.status || 'OPEN').toUpperCase() === 'OPEN' && Number(p.current_value) > 0), settled = d.positions.filter(p => !open.includes(p));
  el.innerHTML = card(`Open positions · ${esc(pmShort(d.address))}`, (open.length ? table(open) : '<p class="muted small">No open positions.</p>') +
      (settled.length ? `<details class="pm-rules pm-settled"><summary>${settled.length} settled</summary>${table(settled)}</details>` : ''), { flush: open.length > 0, cls: 'section' }) +
    card('Activity', d.activity.length ? `<div class="table-wrap"><table class="table"><thead><tr><th>When</th><th>Type</th><th>Market</th><th>Outcome</th><th class="num">Price</th><th class="num">Shares</th><th class="num">USDC</th></tr></thead><tbody>
      ${d.activity.map(a => `<tr><td class="muted small">${esc(pmWhen(a.timestamp))}</td><td>${esc(((a.side ? a.side + ' ' : '') + (a.type || '')).toLowerCase())}</td><td>${link(a)}</td><td>${esc(a.outcome || '')}</td>
        <td class="num">${esc(pmCents(a.price))}</td><td class="num">${esc(pmSize(a.size))}</td><td class="num">${esc(pmMoney(a.usdc_size))}</td></tr>`).join('')}</tbody></table></div>` : '<p class="muted small">No activity.</p>', { flush: d.activity.length > 0, cls: 'section' });
}

// ------------------------------------------------------------------ actions
ACTIONS['pm-open'] = el => pmOpen(el.dataset.id);
ACTIONS['pm-close'] = () => { PM.sel = null; PM.detail = null; const b = $('#pm-detail-box'); if (b) b.innerHTML = ''; $$('#pm-root .pm-card').forEach(c => c.classList.remove('on')); pmStream(); };
ACTIONS['pm-market'] = el => { PM.market = Number(el.dataset.i) || 0; PM.outcome = 0; pmRenderDetail(); };
ACTIONS['pm-outcome'] = el => { PM.outcome = Number(el.dataset.i) || 0; pmRenderDetail(); };
ACTIONS['pm-range'] = el => { PM.range = el.dataset.range; $$('#pm-root .mon-ranges .seg-btn').forEach(b => b.classList.toggle('on', b.dataset.range === PM.range)); pmLoadChart(); };
ACTIONS['pm-cat'] = el => { PM.cat = el.dataset.cat; PM.sel = null; PM.detail = null; $$('#pm-root .pm-cats .chip-toggle').forEach(b => b.classList.toggle('on', b.dataset.cat === PM.cat)); const b = $('#pm-detail-box'); if (b) b.innerHTML = ''; pmLoad(false); };
ACTIONS['pm-more'] = el => { el.disabled = true; el.textContent = 'Loading…'; pmLoad(true); };
ACTIONS['pm-lb'] = el => { if (el.dataset.period) PM.lb.period = el.dataset.period; if (el.dataset.sort) PM.lb.sort = el.dataset.sort; navigate('/polymarket/leaderboard'); };
ACTIONS['pm-wallet'] = el => { PM.wallet = el.dataset.address; navigate('/polymarket/wallet'); };
ACTIONS['pm-wallet-go'] = () => {
  const v = ($('#pm-address') ? $('#pm-address').value : '').trim();
  if (!/^0x[0-9a-fA-F]{40}$/.test(v)) { toast('A wallet address is 0x and 40 letters or digits.', 'bad'); return; }
  PM.wallet = v; pmLoadWallet();
};
setInterval(() => { if (PM.es && !$('#pm-root')) pmStopStream(); }, 5000);
let pmResize = null;
window.addEventListener('resize', () => { clearTimeout(pmResize); pmResize = setTimeout(() => { if ($('#pm-chart') && PM.detail) pmLoadChart(); }, 200); });
