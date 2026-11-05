'use strict';
/* Agentic console - account balances and Model Square. Every account sees its balance and what it spent; administrators
   add money to accounts and set the price of every model. User accounts pay those prices for their chats and API calls
   and for their agents' cloud use; administrators are never charged. */

const SQUARE = { rows: [], canEdit: false, q: '', show: 'all', dirty: new Map() };

/** "$0.15 / $0.60" per 1M tokens, or "free". */
function priceText(inP, outP) {
  if (!inP && !outP) return 'free';
  const f = v => '$' + Number(v || 0).toFixed(Number(v) >= 1 ? 2 : 4).replace(/(\.\d\d\d*?)0+$/, '$1');
  return `${f(inP)} / ${f(outP)}`;
}
const ENTRY_LABEL = { Credit: 'Added', Chat: 'Chat', Api: 'API', Agent: 'Agent', Speech: 'Speech', Music: 'Music' };

// ================================================================== Balance
/** Spend types in the charts: images, videos and speech together as "Media" (four series, four palette colors). */
const SPEND_SERIES = [
  { key: 'Chat', label: 'Chat', types: ['Chat'] },
  { key: 'Api', label: 'API', types: ['Api'] },
  { key: 'Agent', label: 'Agents', types: ['Agent'] },
  { key: 'Media', label: 'Images, videos, speech & music', types: ['Image', 'Video', 'Speech', 'Music'] }
];
const BALANCE = { days: 30 };
try { BALANCE.days = Number(localStorage.getItem('balanceDays')) || 30; } catch { /* ignore */ }

/** One row per UTC day of the range (days without movements are zeros), from the server's day × type rows. */
function balanceDays(stats) {
  const byDay = new Map();
  const start = new Date(stats.sinceUtc);
  for (let i = 0; i < stats.days; i++) {
    const d = new Date(Date.UTC(start.getUTCFullYear(), start.getUTCMonth(), start.getUTCDate() + i)).toISOString().slice(0, 10);
    byDay.set(d, { day: d, spent: 0, added: 0, calls: 0, inTok: 0, outTok: 0, Chat: 0, Api: 0, Agent: 0, Media: 0, balance: null });
  }
  for (const r of stats.daily) {
    const row = byDay.get(r.day);
    if (!row) continue;
    if (r.type === 'Credit') { row.added += r.micros; continue; }
    row.spent += r.micros; row.calls += r.calls; row.inTok += r.promptTokens; row.outTok += r.completionTokens;
    const s = SPEND_SERIES.find(x => x.types.includes(r.type));
    row[s ? s.key : 'Chat'] += r.micros;
  }
  for (const c of stats.closingBalances) { const row = byDay.get(c.day); if (row) row.balance = c.balanceMicros; }
  // Days without movements keep the balance of the day before.
  let bal = stats.openingBalanceMicros || 0;
  const rows = [...byDay.values()];
  rows.forEach(r => { if (r.balance === null) r.balance = bal; else bal = r.balance; });
  return rows;
}
function dayLabel(d) { return new Date(d + 'T00:00:00Z').toLocaleDateString([], { month: 'short', day: 'numeric', timeZone: 'UTC' }); }
function moneyM(micros) { return money(micros, micros && Math.abs(micros) < 10000 ? 4 : 2); }

VIEWS.balance = {
  live: true,
  async render() {
    setCrumbs([['Balance', '/balance']]);
    const a = await get('/api/account?days=' + BALANCE.days);
    const st = a.stats, dep = a.deposits || { available: false, recent: [] };
    const empty = a.charged && a.balanceMicros <= 0;
    const stopped = a.agents.filter(x => x.unfunded).length;
    const depositBtn = dep.available ? `<button class="btn primary" data-act="deposit">${icon('plus')} Deposit</button>` : '';
    const ranges = [7, 30, 90].map(n => `<button type="button" class="chip-toggle${BALANCE.days === n ? ' on' : ''}" data-act="balance-range" data-days="${n}">${n} days</button>`).join('');
    const head = pageHead('Balance', {
      subtitle: a.charged
        ? 'Your account pays for your chats and API calls at the <a href="/model-square" data-nav>Model Square</a> prices, and for your agents\' cloud model use. When it reaches $0.00, priced models stop answering and your agents stop until money is added.' +
          (st && st.surcharge ? ` Prices on this account include a ${st.surchargePercent}% surcharge.` : '')
        : 'Administrators are never charged, and their agents never stop for money. Add money to accounts below or under Users &amp; access.',
      actions: `<div class="chips">${ranges}</div>${depositBtn}`
    });
    const warn = empty ? `<div class="notice bad section">Your balance is empty. Models with a price do not answer you, and your agents are stopped. ${dep.available ? 'Deposit money to continue.' : 'Ask an administrator to add money.'}</div>` : '';
    if (!st) {
      return head + warn + `<div class="grid kpis section">${kpi('Balance', money(a.balanceMicros, 2), 'administrator · never charged')}${kpi('Your agents', num(a.agents.length))}</div>` + balanceAccounts(a);
    }
    const rows = balanceDays(st);
    const spentRange = rows.reduce((s, r) => s + r.spent, 0), addedRange = rows.reduce((s, r) => s + r.added, 0);
    const callsRange = rows.reduce((s, r) => s + r.calls, 0);
    const activeDays = rows.filter(r => r.spent > 0).length;
    const perDay = spentRange / Math.max(1, rows.length);
    const runway = a.charged && perDay > 0 ? a.balanceMicros / perDay : null;
    const spark = (k) => sparkline(rows.map(r => r[k]));
    const kpis = `<div class="grid kpis section">` +
      kpi('Balance', money(a.balanceMicros, 2), a.charged ? (empty ? '<span class="amt-neg">empty</span>' : (runway !== null ? `about ${runway >= 365 ? 'a year or more' : plural(Math.floor(runway), 'day')} at your ${st.days}-day pace` : 'your account')) : 'administrator · never charged', { bar: sparkline(rows.map(r => Math.max(0, r.balance)), 's2') }) +
      kpi('Spent today', money(a.spentTodayMicros, 2), `${money(st.spent7dMicros, 2)} in the last 7 days`) +
      kpi(`Spent (${st.days} days)`, money(spentRange, 2), `${num(callsRange)} charges · ${plural(activeDays, 'active day')}`, { bar: spark('spent') }) +
      kpi('Average per day', moneyM(perDay), callsRange ? `${moneyM(spentRange / callsRange)} per charge` : 'no charges yet') +
      kpi(`Added (${st.days} days)`, money(addedRange, 2), dep.recent.filter(d => d.status === 'credited').length ? plural(dep.recent.filter(d => d.status === 'credited').length, 'deposit') + ' recently' : 'deposits and credits', { bar: spark('added') }) +
      kpi('Tokens', num(rows.reduce((s, r) => s + r.inTok + r.outTok, 0)), `${num(rows.reduce((s, r) => s + r.inTok, 0))} in · ${num(rows.reduce((s, r) => s + r.outTok, 0))} out`) +
      kpi('Lifetime added', money(st.lifetime.addedMicros, 2), st.lifetime.firstUtc ? 'since ' + esc(new Date(st.lifetime.firstUtc).toLocaleDateString()) : 'nothing yet') +
      kpi('Your agents', num(a.agents.length), stopped ? `<span class="amt-neg">${plural(stopped, 'agent')} stopped: no money</span>` : (a.agents.length ? 'paid from this balance' : 'none')) + '</div>';

    const tick = Math.max(1, Math.ceil(rows.length / 8));
    const spendChart = card(`Daily spend by type`, chartBox('bal-spend', {
      type: 'stack', rows, x: r => dayLabel(r.day), label: 'Money spent per day, by type', axis: moneyAxis, minMax: 0.01, tickEvery: tick,
      series: SPEND_SERIES.map((s, i) => ({ label: s.label, y: r => r[s.key] / 1e6, cls: SERIES[i] })),
      tip: r => SPEND_SERIES.map(s => `${s.label}: ${moneyM(r[s.key])}`).join('\n') + `\nTotal: ${moneyM(r.spent)}`
    }) + legend(SPEND_SERIES.map(s => ({ label: s.label }))), { hint: `last ${st.days} days (UTC)` });
    const balChart = card('Balance over time', chartBox('bal-line', {
      type: 'line', rows, x: r => dayLabel(r.day), label: 'Balance at the end of each day', axis: moneyAxis, area: true, minMax: 0.01, tickEvery: tick,
      series: [{ label: 'Balance', y: r => Math.max(0, r.balance) / 1e6, cls: 's2' }],
      tip: r => `Balance: ${money(r.balance, 2)}` + (r.added ? `\nAdded: ${money(r.added, 2)}` : '') + (r.spent ? `\nSpent: ${moneyM(r.spent)}` : '')
    }), { hint: 'end of each day' });
    const tokChart = card('Tokens per day', chartBox('bal-tok', {
      type: 'line', rows, x: r => dayLabel(r.day), label: 'Input and output tokens per day', axis: numAxis, tickEvery: tick,
      series: [{ label: 'Input', y: r => r.inTok, cls: 's1' }, { label: 'Output', y: r => r.outTok, cls: 's3' }]
    }) + legend([{ label: 'Input', cls: 's1' }, { label: 'Output', cls: 's3' }]));
    const typeTotals = SPEND_SERIES.map((s, i) => ({ key: s.label, count: rows.reduce((t, r) => t + r[s.key], 0), cls: 'mx' + i }));
    const mix = card('Where your money goes', moneyMix(typeTotals, 'Nothing spent in this period.') +
      `<h3 class="section small muted">Top models (30 days)</h3>` +
      barList(a.byModel30d.map(r => ({ label: r.model, value: r.micros / 1e6, sub: `${num(r.calls)} charges · ${num(r.promptTokens)} in · ${num(r.completionTokens)} out` })),
        { fmt: v => money(v * 1e6, v < 0.01 ? 4 : 2), limit: 8, empty: 'No model spend in the last 30 days' }));

    const byModel = card('Spend by model (30 days)', table([
      { h: 'Model', f: r => `<code>${esc(r.model)}</code>` }, { h: 'Charges', num: true, f: r => num(r.calls) },
      { h: 'Tokens in', num: true, f: r => num(r.promptTokens) }, { h: 'Tokens out', num: true, f: r => num(r.completionTokens) },
      { h: 'Avg / charge', num: true, f: r => moneyM(r.calls ? r.micros / r.calls : 0) },
      { h: 'Spent', num: true, f: r => money(r.micros) }
    ], a.byModel30d, { empty: 'Nothing spent in the last 30 days.' }), { flush: true, export: 'spend-by-model' });
    const deposits = dep.available || dep.recent.length ? card('Deposits', table([
      { h: 'When', cls: 'nw', f: d => `<span data-tip="${esc(ts(d.createdUtc))}">${esc(ago(d.createdUtc))}</span>` },
      { h: 'Invoice', f: d => `<code>${esc(d.invoiceId)}</code>` },
      { h: 'Status', cls: 'nw', f: d => d.status === 'credited' ? badge('Paid', 'ok') : badge('Awaiting payment', 'warn') },
      { h: 'Amount', num: true, f: d => money(d.amountMicros, 2) }
    ], dep.recent, { empty: 'No deposits yet.' }), { flush: true, actions: depositBtn.replace('btn primary', 'btn xs') }) : '';
    const ledger = card('History', table([
      { h: 'When', cls: 'nw', f: e => `<span data-tip="${esc(ts(e.createdUtc))}">${esc(ago(e.createdUtc))}</span>` },
      { h: 'What', cls: 'nw', f: e => tag(ENTRY_LABEL[e.entryType] || e.entryType, e.entryType === 'Credit' ? (e.amountMicros >= 0 ? 'ok' : 'warn') : 'outline') },
      { h: 'Details', f: e => `${e.model ? `<code>${esc(e.model)}</code> ` : ''}${e.agentId ? agentLink(e.agentId) + ' ' : ''}<span class="muted small">${esc(e.description || '')}</span>` +
          (e.promptTokens || e.completionTokens ? `<div class="cell-sub">${num(e.promptTokens || 0)} in · ${num(e.completionTokens || 0)} out</div>` : '') },
      { h: 'Amount', num: true, f: e => `<span class="${e.amountMicros > 0 ? 'amt-pos' : ''}">${e.amountMicros > 0 ? '+' : ''}${money(e.amountMicros)}</span>` },
      { h: 'Balance', num: true, f: e => money(e.balanceAfterMicros, 2) }
    ], a.ledger, { empty: a.charged ? 'No money has moved yet.' : 'Administrators are never charged.' }), { flush: true, export: 'balance-history' });
    return head + warn + kpis +
      `<div class="grid two section">${spendChart}${balChart}</div>` +
      `<div class="grid two section">${mix}${tokChart}</div>` +
      balanceAccounts(a) +
      `<div class="grid two section">${byModel}${deposits || ledger}</div>` +
      (deposits ? `<div class="section">${ledger}</div>` : '');
  }
};
/** A 100% bar of money by type, with a legend (like mixBar, in dollars). */
function moneyMix(items, emptyText) {
  const parts = items.filter(i => i.count > 0);
  const total = parts.reduce((s, i) => s + i.count, 0);
  if (!total) return emptyState(emptyText, '');
  const pctOf = i => Math.round(i.count / total * 100) + '%';
  return `<div class="mixbar">${parts.map(i => `<span class="${i.cls}" data-flex="${i.count}" data-tip="${esc(i.key)}: ${moneyM(i.count)} (${pctOf(i)})"></span>`).join('')}</div>
    <div class="legend">${parts.map(i => `<span><i class="sw ${i.cls}"></i>${esc(i.key)} <b>${moneyM(i.count)}</b> <span class="muted">${pctOf(i)}</span></span>`).join('')}</div>`;
}
function balanceAccounts(a) {
  if (!a.accounts) return '';
  return card('Accounts', table([
    { h: 'Account', f: x => `<span class="cell-title">${esc(x.username)}</span>${x.username === S.user ? ' ' + tag('you', 'accent') : ''}` },
    { h: 'Role', cls: 'nw', f: x => x.isAdmin ? badge('Administrator', 'accent', false) : badge('User', 'outline', false) },
    { h: 'Balance', num: true, f: x => x.isAdmin ? '<span class="muted">never charged</span>' : `<span class="${x.balanceMicros <= 0 ? 'amt-neg' : ''}">${money(x.balanceMicros, 2)}</span>` },
    { h: 'Spent (30 days)', num: true, f: x => money(x.spent30dMicros, 2) },
    { h: 'Agents', num: true, f: x => num(x.agents) },
    { h: '', cls: 'act', f: x => x.isAdmin ? '' : `<button class="btn xs" data-act="balance-add" data-user="${esc(x.username)}" data-balance="${x.balanceMicros}">${icon('plus')} Add money</button>` }
  ], a.accounts, { empty: 'No accounts.' }), { flush: true, cls: 'section', export: 'account-balances' });
}
ACTIONS['balance-range'] = el => {
  BALANCE.days = Number(el.dataset.days) || 30;
  try { localStorage.setItem('balanceDays', String(BALANCE.days)); } catch { /* ignore */ }
  navigate(location.pathname + location.search, false);
};

// ------------------------------------------------------------------ deposits (BTCPay)
/** Balance → Deposit: ask for a whole amount, make the invoice, then show BTCPay's checkout over the page. */
ACTIONS['deposit'] = async () => {
  const a = await get('/api/account');
  const dep = a.deposits;
  if (!dep || !dep.available) { toast('Deposits are not available.', 'bad'); return; }
  const quick = [10, 25, 50, 100].filter(v => v >= dep.min && v <= dep.max);
  openModal('Deposit', `<form id="deposit-form" class="stack" data-form="deposit" data-min="${dep.min}" data-max="${dep.max}">
      <div data-errors></div>
      <p class="muted small">Current balance: <b>${money(a.balanceMicros, 2)}</b>. Pay with Bitcoin or Lightning through BTCPay; the money is added to your balance as soon as the payment is confirmed.</p>
      <label>How much would you like to deposit? (${esc(dep.currency)})<input name="amount" type="number" inputmode="numeric" min="${dep.min}" max="${dep.max}" step="1" required autofocus placeholder="${dep.min}–${dep.max}">
        <span class="help">A whole number from ${dep.min} to ${dep.max}.</span></label>
      ${quick.length ? `<div class="chips">${quick.map(v => `<button type="button" class="chip-toggle" data-act="deposit-quick" data-amount="${v}">$${v}</button>`).join('')}</div>` : ''}</form>`,
    `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="submit-form" data-form-id="deposit-form">Continue to payment</button>`, 'narrow');
};
ACTIONS['deposit-quick'] = el => { const i = $('#deposit-form input[name="amount"]'); if (i) { i.value = el.dataset.amount; i.focus(); } };
FORMS['deposit'] = async (f, d) => {
  const min = Number(f.dataset.min), max = Number(f.dataset.max), raw = String(d.amount || '').trim();
  if (!/^\d+$/.test(raw)) throw new Error(`Enter a whole number from ${min} to ${max}.`);
  const amount = Number(raw);
  if (amount < min || amount > max) throw new Error(`Enter a whole number from ${min} to ${max}.`);
  const inv = await post('/api/account/deposits', { amount });
  closeModal();
  showCheckout(inv);
};
/** BTCPay's checkout in modal view, in a frame over the page (what btcpay.js does). It posts "close" when the payer closes it. */
function showCheckout(inv) {
  closeCheckout();
  const base = inv.checkoutLink || `${inv.checkoutOrigin}/i/${encodeURIComponent(inv.invoiceId)}`;
  const frame = document.createElement('iframe');
  frame.className = 'btcpay-frame';
  frame.name = 'btcpay';
  frame.title = 'BTCPay checkout';
  frame.allow = 'clipboard-write';
  frame.src = base + (base.includes('?') ? '&' : '?') + 'view=modal';
  const onMessage = ev => {
    if (ev.origin !== inv.checkoutOrigin) return;
    const m = ev.data;
    if (m === 'close') { closeCheckout(); watchDeposit(inv.invoiceId); return; }
    const status = m && typeof m === 'object' ? String(m.status || '').toLowerCase() : '';
    if (status === 'settled' || status === 'complete' || status === 'confirmed' || status === 'paid') watchDeposit(inv.invoiceId);
  };
  S.checkout = { frame, onMessage, invoiceId: inv.invoiceId };
  window.addEventListener('message', onMessage);
  document.body.appendChild(frame);
}
function closeCheckout() {
  const c = S.checkout;
  if (!c) return;
  window.removeEventListener('message', c.onMessage);
  c.frame.remove();
  S.checkout = null;
}
/** After the checkout: waits (up to 3 minutes) for the payment server to confirm, then tells the user and refreshes. */
function watchDeposit(invoiceId) {
  S.depositWatch = S.depositWatch || new Set();
  if (S.depositWatch.has(invoiceId)) return;
  S.depositWatch.add(invoiceId);
  let tries = 0;
  const tick = async () => {
    tries++;
    try {
      const r = await get('/api/account/deposits/' + enc(invoiceId));
      if (r.deposit.status === 'credited') {
        S.depositWatch.delete(invoiceId);
        toast(`Deposit received: ${money(r.deposit.amountMicros, 2)} added. Balance ${money(r.balanceMicros, 2)}.`, 'ok');
        showBalance({ balanceMicros: r.balanceMicros, charged: true });
        if (location.pathname === '/balance') navigate('/balance', false);
        return;
      }
    } catch { /* keep waiting */ }
    if (tries < 60) setTimeout(tick, 3000); else S.depositWatch.delete(invoiceId);
  };
  if (location.pathname === '/balance') navigate('/balance', false);
  setTimeout(tick, 1500);
}

/** Administrators: add money to an account (a negative amount takes money away). */
ACTIONS['balance-add'] = el => {
  const user = el.dataset.user;
  openModal('Add money to ' + user, `<form id="balance-form" class="stack" data-form="balance-add" data-user="${esc(user)}">
      <div data-errors></div>
      <p class="muted small">Current balance: <b>${money(Number(el.dataset.balance || 0), 2)}</b>. The account pays for its chats, API calls and agents from it.</p>
      <label>Amount (USD)<input name="amount" type="number" step="0.01" required autofocus placeholder="e.g. 10.00"><span class="help">A negative amount takes money away.</span></label>
      <label>Note<input name="note" maxlength="300" placeholder="e.g. Invoice 2026-041"></label></form>`,
    `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="submit-form" data-form-id="balance-form">Add money</button>`, 'narrow');
};
FORMS['balance-add'] = async (f, d) => {
  const amount = Number(d.amount);
  if (!amount) throw new Error('Enter an amount other than 0.');
  const r = await post(`/api/users/${enc(f.dataset.user)}/balance`, { amount, note: d.note || null });
  toast(`${f.dataset.user}: balance ${money(r.balanceMicros, 2)}`, 'ok');
  closeModal(); navigate(location.pathname + location.search, false);
};

// ================================================================== Model Square
// Every model this account may use, as cards (or a table), with filters on the left (provider, type, pricing, health,
// context, agents) and sorts by name, price, speed, latency, context, use and age. Administrators edit prices in the
// table view.
const SQUARE_FILTERS = { group: 'all', type: 'all', price: 'all', status: 'all', ctx: 'all', agents: 'all' };
const SQUARE_SORTS = [['name', 'Name (A–Z)'], ['name-desc', 'Name (Z–A)'], ['in', 'Input price: low to high'], ['in-desc', 'Input price: high to low'],
  ['out', 'Output price: low to high'], ['out-desc', 'Output price: high to low'], ['tps', 'Fastest (tokens/s)'], ['latency', 'Lowest latency'],
  ['ctx', 'Largest context'], ['calls', 'Most used (24 h)'], ['reliable', 'Most reliable (24 h)'], ['newest', 'Newest'], ['priced', 'Recently priced']];
function squareLs(k, fallback, allowed) { const v = LS.get('square.' + k); return allowed.includes(v) ? v : fallback; }
function squareGroupLabel(g) { return g === 'custom' ? 'Custom models' : g === '' ? 'Local (A1 / FLINT)' : g; }
function squareType(r) { return r.custom ? 'custom' : r.group === '' ? 'local' : 'provider'; }
function squarePrice(r, which) {
  const v = SQUARE.dirty.get(r.id);
  if (which === 'cached') { const own = v ? v.cached : r.cachedInputPerMillion; return own != null ? Number(own) : Number(v ? v.in : r.inputPerMillion || 0) * 0.1; }
  return Number(v ? v[which] : which === 'in' ? r.inputPerMillion : r.outputPerMillion) || 0;
}
function squarePriced(r) { const v = SQUARE.dirty.get(r.id); return v ? (v.in > 0 || v.out > 0) : r.priced; }
/** "$0.02" per 1M, or per 1K with the unit switch; trailing zeros dropped. */
function squareMoney(perMillion) {
  const v = SQUARE.unit === 'K' ? perMillion / 1000 : perMillion;
  if (!v) return '$0';
  const digits = v >= 100 ? 0 : v >= 1 ? 2 : v >= 0.01 ? 4 : 7;
  return '$' + Number(v.toFixed(digits)).toString();
}
function squareCtx(n) { return !n ? '—' : n >= 1e6 ? `${+(n / 1048576).toFixed(n % 1048576 ? 1 : 0)}M` : `${Math.round(n / 1000)}K`; }
function squareStatus(r) { return r.status ? r.status.state : 'gray'; }
function squareMatches(r, f, skip) {
  const q = SQUARE.q.trim().toLowerCase();
  if (q && !q.split(/\s+/).every(w => (r.id + ' ' + r.name + ' ' + (r.ownedBy || '') + ' ' + squareGroupLabel(r.group) + ' ' + (r.description || '') + ' ' + (r.note || '') +
    ' ' + (r.agentKeys || []).map(k => k.key).join(' ')).toLowerCase().includes(w))) return false;
  if (skip !== 'group' && f.group !== 'all' && r.group !== f.group) return false;
  if (skip !== 'type' && f.type !== 'all' && squareType(r) !== f.type) return false;
  if (skip !== 'price' && f.price !== 'all' && (f.price === 'priced') !== squarePriced(r)) return false;
  if (skip !== 'status' && f.status !== 'all' && squareStatus(r) !== f.status) return false;
  if (skip !== 'agents' && f.agents === 'yes' && !(r.agentKeys && r.agentKeys.length)) return false;
  if (skip !== 'ctx' && f.ctx !== 'all') {
    const c = r.contextWindow || 0;
    if (f.ctx === '1m' ? c < 1000000 : f.ctx === '200k' ? c < 200000 : f.ctx === '128k' ? c < 128000 : c >= 128000) return false;
  }
  return true;
}
function squareRows() {
  const rows = SQUARE.rows.filter(r => squareMatches(r, SQUARE.f));
  const st = r => r.status || {}, inf = Number.POSITIVE_INFINITY;
  const by = {
    'name': (a, b) => a.name.localeCompare(b.name), 'name-desc': (a, b) => b.name.localeCompare(a.name),
    'in': (a, b) => squarePrice(a, 'in') - squarePrice(b, 'in'), 'in-desc': (a, b) => squarePrice(b, 'in') - squarePrice(a, 'in'),
    'out': (a, b) => squarePrice(a, 'out') - squarePrice(b, 'out'), 'out-desc': (a, b) => squarePrice(b, 'out') - squarePrice(a, 'out'),
    'tps': (a, b) => (st(b).tps || 0) - (st(a).tps || 0),
    'latency': (a, b) => (st(a).firstTokenMs ?? st(a).responseMs ?? inf) - (st(b).firstTokenMs ?? st(b).responseMs ?? inf),
    'ctx': (a, b) => (b.contextWindow || 0) - (a.contextWindow || 0), 'calls': (a, b) => (st(b).calls || 0) - (st(a).calls || 0),
    'reliable': (a, b) => { const ok = s => s.calls ? (s.calls - s.failures) / s.calls : -1; return ok(st(b)) - ok(st(a)) || (st(b).calls || 0) - (st(a).calls || 0); },
    'newest': (a, b) => new Date(b.createdUtc || 0) - new Date(a.createdUtc || 0), 'priced': (a, b) => new Date(b.updatedUtc || 0) - new Date(a.updatedUtc || 0)
  }[SQUARE.sort] || ((a, b) => a.name.localeCompare(b.name));
  return rows.sort((a, b) => by(a, b) || a.name.localeCompare(b.name));
}
/** A filter group: its options with how many models each would show (given the other filters and the search). */
function squareFacet(key, title, options) {
  const base = SQUARE.rows.filter(r => squareMatches(r, SQUARE.f, key));
  const count = test => base.filter(test).length;
  const items = options.map(([v, label, test, extra]) => {
    const n = v === 'all' ? base.length : count(test);
    if (v !== 'all' && !n && SQUARE.f[key] !== v) return '';
    return `<button type="button" class="ms-opt${SQUARE.f[key] === v ? ' on' : ''}" data-act="ms-filter" data-key="${key}" data-value="${esc(v)}">${extra || ''}<span>${esc(label)}</span><span class="ms-n">${num(n)}</span></button>`;
  }).join('');
  return `<details class="ms-facet" open><summary>${esc(title)}</summary><div class="ms-opts">${items}</div></details>`;
}
function squareFilters() {
  const groups = [...new Set(SQUARE.rows.map(r => r.group))].sort((a, b) => (a === 'custom' ? -2 : a === '' ? -1 : 0) - (b === 'custom' ? -2 : b === '' ? -1 : 0) || a.localeCompare(b));
  const dot = s => `<i class="mdot s-${s}" aria-hidden="true"></i>`;
  const active = Object.keys(SQUARE_FILTERS).some(k => SQUARE.f[k] !== SQUARE_FILTERS[k]) || SQUARE.q;
  return `<div class="ms-filter-head"><div><b>Filter</b><div class="small muted">By provider, type, pricing, health and context.</div></div>
      <button type="button" class="btn xs ghost" data-act="ms-reset"${active ? '' : ' disabled'}>Reset</button></div>` +
    squareFacet('group', 'Providers', [['all', 'All providers'], ...groups.map(g => [g, squareGroupLabel(g), r => r.group === g, `<span class="ms-ava xs" data-hue="${squareHue(g)}">${esc(squareInitial(g))}</span>`])]) +
    squareFacet('type', 'Type', [['all', 'All types'], ['custom', 'Custom models', r => r.custom], ['provider', 'Provider models', r => squareType(r) === 'provider'], ['local', 'Local models', r => squareType(r) === 'local']]) +
    squareFacet('price', 'Pricing', [['all', 'All models'], ['priced', 'Priced', r => squarePriced(r)], ['free', 'Free', r => !squarePriced(r)]]) +
    squareFacet('status', 'Health (24 h)', [['all', 'Any'], ['green', 'Working well', r => squareStatus(r) === 'green', dot('green')], ['yellow', 'Some failures', r => squareStatus(r) === 'yellow', dot('yellow')],
      ['red', 'Failing', r => squareStatus(r) === 'red', dot('red')], ['gray', 'No recent calls', r => squareStatus(r) === 'gray', dot('gray')]]) +
    squareFacet('ctx', 'Context window', [['all', 'Any size'], ['1m', '1M and more', r => r.contextWindow >= 1000000], ['200k', '200K and more', r => r.contextWindow >= 200000],
      ['128k', '128K and more', r => r.contextWindow >= 128000], ['small', 'Under 128K', r => (r.contextWindow || 0) < 128000]]) +
    squareFacet('agents', 'Agents', [['all', 'All models'], ['yes', 'Used by agents', r => !!(r.agentKeys && r.agentKeys.length)]]);
}
function squareInitial(s) { const t = (s || '').replace(/^custom$/, 'Custom').replace(/[^A-Za-z0-9]/g, ''); return (t[0] || '★').toUpperCase(); }
function squareHue(s) { let h = 0; for (const c of String(s || 'local')) h = (h * 31 + c.charCodeAt(0)) % 360; return Math.round(h / 30) * 30 % 360; }
function squareCard(r) {
  const s = r.status, st = squareStatus(r), priced = squarePriced(r), g = r.group;
  const cached = squarePrice(r, 'cached');
  const tags = [squareCtx(r.contextWindow) !== '—' ? squareCtx(r.contextWindow) + ' context' : '', r.custom ? 'Custom' : '', squareType(r) === 'local' ? 'Local' : '',
    r.agentKeys && r.agentKeys.length ? `Agents: ${r.agentKeys.map(k => k.key).join(', ')}` : ''].filter(Boolean);
  return `<article class="ms-card${SQUARE.dirty.has(r.id) ? ' dirty' : ''}" data-id="${esc(r.id)}">
    <div class="ms-card-head"><span class="ms-ava" data-hue="${squareHue(r.custom ? 'custom' : g)}">${esc(squareInitial(r.custom ? 'custom' : g || r.name))}</span>
      <div class="ms-card-title"><b data-tip="${esc(r.id)}">${esc(r.name)}</b><code class="muted">${esc(r.id)}</code></div>
      <button type="button" class="btn xs ghost icon-only" data-act="copy-text" data-text="${esc(r.id)}" data-tip="Copy model id" aria-label="Copy model id">⧉</button></div>
    <dl class="ms-prices">${priced
      ? `<div><dt>Input</dt><dd>${esc(squareMoney(squarePrice(r, 'in')))}</dd></div><div><dt>Output</dt><dd>${esc(squareMoney(squarePrice(r, 'out')))}</dd></div><div><dt>Cached</dt><dd>${esc(squareMoney(cached))}</dd></div>`
      : '<div class="free"><dt>Price</dt><dd>Free</dd></div>'}</dl>
    <p class="ms-desc${r.description || r.note ? '' : ' muted'}">${esc(r.description || r.note || 'No description.')}</p>
    <div class="ms-card-foot">
      <div class="ms-prov"><span>${esc(r.custom ? 'Custom model' : squareGroupLabel(g))}</span>${r.ownedBy && !r.custom && r.ownedBy !== g ? `<span class="muted small">${esc(r.ownedBy)}</span>` : ''}</div>
      <div class="ms-stats" data-tip="${esc(s ? `${s.calls - s.failures} of ${plural(s.calls, 'call')} succeeded (last 24 h)${s.lastErrorKind && s.failures ? ' · last failure: ' + s.lastErrorKind : ''}` : 'No calls in the last 24 hours')}">
        <div><span>Lat.</span><b>${s && (s.firstTokenMs ?? s.responseMs) != null ? esc(fmtMs(s.firstTokenMs ?? s.responseMs)) : '—'}</b></div>
        <div><span>TPS</span><b${s && s.tps > TPS_FIRE ? ' class="hot"' : ''}>${s && s.tps ? esc(fmtTps(s.tps)) : '—'}</b></div>
        <div><span>Status</span><b><i class="mdot s-${esc(st)}" aria-hidden="true"></i>${esc(st === 'gray' ? 'idle' : s ? `${s.calls - s.failures}/${s.calls}` : '')}</b></div></div>
    </div>
    ${tags.length ? `<div class="ms-tags">${tags.map(t => `<span>${esc(t)}</span>`).join('')}</div>` : ''}
  </article>`;
}
function squareResults() {
  const rows = squareRows();
  if (!rows.length) return card('', emptyState('No models match', 'Change the search or reset the filters.'));
  if (SQUARE.view === 'table') return `<div class="card"><div class="card-body flush">${squareTable(rows)}</div></div>`;
  return `<div class="ms-grid">${rows.map(squareCard).join('')}</div>`;
}
function squareToolbar() {
  const seg = (act, cur, opts) => `<div class="seg" role="group">${opts.map(([v, l, tip]) => `<button type="button" class="seg-btn${cur === v ? ' on' : ''}" data-act="${act}" data-value="${v}"${tip ? ` data-tip="${esc(tip)}"` : ''}>${l}</button>`).join('')}</div>`;
  return `<div class="ms-toolbar card"><div class="ms-count"><b id="ms-count">${num(squareRows().length)}</b> <span class="muted">of ${plural(SQUARE.rows.length, 'model')}</span></div><span class="spacer"></span>
    ${seg('ms-unit', SQUARE.unit, [['M', '/1M', 'Prices per 1M tokens'], ['K', '/1K', 'Prices per 1K tokens']])}
    <label class="ms-sort">${icon('chart')}<select data-change="ms-sort" aria-label="Sort models">${SQUARE_SORTS.map(([v, l]) => `<option value="${v}"${SQUARE.sort === v ? ' selected' : ''}>${esc(l)}</option>`).join('')}</select></label>
    ${seg('ms-view', SQUARE.view, [['cards', '▦', 'Cards'], ['table', '☰', SQUARE.canEdit ? 'Table (edit prices)' : 'Table']])}</div>`;
}
VIEWS.modelSquare = {
  live: false,
  async render() {
    setCrumbs([['Model Square', '/model-square']]);
    const d = await get('/api/model-square');
    SQUARE.rows = d.models; SQUARE.canEdit = d.canEdit; SQUARE.dirty = new Map();
    SQUARE.f = Object.assign({}, SQUARE_FILTERS, SQUARE.f || {});
    SQUARE.sort = squareLs('sort', 'name', SQUARE_SORTS.map(x => x[0]));
    SQUARE.unit = squareLs('unit', 'M', ['M', 'K']);
    SQUARE.view = squareLs('view', 'cards', ['cards', 'table']);
    const groups = new Set(d.models.map(m => m.group)).size;
    const info = d.canEdit
      ? 'Prices are in USD. User accounts pay them from their balance for Chat, the API and their agents\' cloud use; administrators are never charged. A model without a price is free. A custom model is charged at the price of the model that answered, or at its own price when that model is free. Accounts with the surcharge pay 15% more. Edit prices in the table view.'
      : 'Prices are in USD per 1M tokens (switch to /1K below). A model without a price is free for your account.';
    return `<section class="ms-hero"><h1>Model Square</h1><p class="ms-lead">This server offers <b>${plural(d.models.length, 'model')}</b> from ${plural(groups, 'provider')} to your account.</p>
        <p class="ms-sub">Compare prices, speed and context, and pick the right model for the job.</p>
        <label class="ms-search-box">${icon('search')}<input type="search" class="ms-search" placeholder="Search model name, id, provider or description…" value="${esc(SQUARE.q)}" data-ms="q" aria-label="Search models"></label>
        ${d.canEdit ? `<div class="ms-hero-actions"><button class="btn primary" data-act="ms-save" id="ms-save" disabled>Save prices</button></div>` : ''}</section>` +
      (d.providerError ? `<div class="notice warn">The provider's model list is unavailable (${esc(d.providerError)}); only known models are shown.</div><div class="section"></div>` : '') +
      `<div class="ms-layout"><aside class="ms-filters card" id="ms-filters">${squareFilters()}</aside>
        <div class="ms-results">${squareToolbar()}<div id="ms-results">${squareResults()}</div>
        <p class="small muted ms-info">${esc(info)}</p></div></div>`;
  }
};
function squareTable(rows) {
  const priceCell = (r, which) => {
    const v = SQUARE.dirty.get(r.id), value = v ? v[which] : (which === 'in' ? r.inputPerMillion : r.outputPerMillion);
    if (!SQUARE.canEdit) return r.priced ? esc(squareMoney(Number(value))) : '<span class="muted">free</span>';
    return `<input type="number" class="ms-price" min="0" step="0.0001" inputmode="decimal" data-ms="${which}" data-id="${esc(r.id)}" value="${value ? esc(String(value)) : ''}" placeholder="0" aria-label="${which === 'in' ? 'Input' : 'Output'} price per 1M tokens for ${esc(r.id)}">`;
  };
  // Cached input (prompt tokens the provider served from its cache): blank = 10% of the input price.
  const cachedCell = r => {
    const v = SQUARE.dirty.get(r.id), own = v ? v.cached : r.cachedInputPerMillion;
    const inP = v ? v.in : r.inputPerMillion, fallback = Number(inP || 0) * 0.1;
    if (!SQUARE.canEdit) return r.priced ? esc(squareMoney(Number(own ?? fallback))) + (own == null ? ' <span class="muted small">(10%)</span>' : '') : '<span class="muted">free</span>';
    return `<input type="number" class="ms-price" min="0" step="0.0001" inputmode="decimal" data-ms="cached" data-id="${esc(r.id)}" value="${own != null ? esc(String(own)) : ''}" ` +
      `placeholder="${esc(fallback ? String(+fallback.toFixed(6)) : '10% of input')}" aria-label="Cached input price per 1M tokens for ${esc(r.id)}" data-tip="Blank = 10% of the input price">`;
  };
  const unit = SQUARE.canEdit ? '1M' : SQUARE.unit === 'K' ? '1K' : '1M';
  return table([
    { h: 'Model', f: r => `<span class="cell-title">${esc(r.name)}</span>${r.custom ? ' ' + tag('custom', 'accent') : ''}` +
        `<div class="cell-sub"><code>${esc(r.id)}</code>${r.ownedBy && !r.custom ? ' · ' + esc(r.ownedBy) : ''}${r.note ? ' · ' + esc(r.note) : ''}</div>` +
        (r.agentKeys && r.agentKeys.length ? `<div class="cell-sub">agents: ${r.agentKeys.map(k => esc(k.key) + (k.isLocal ? ` (local, ${k.slots} slots; free for agents)` : '')).join(', ')}</div>` : '') },
    { h: 'Provider', cls: 'nw', f: r => esc(r.custom ? 'Custom' : squareGroupLabel(r.group)) },
    { h: `Input $/${unit}`, cls: 'nw', f: r => priceCell(r, 'in') },
    { h: `Output $/${unit}`, cls: 'nw', f: r => priceCell(r, 'out') },
    { h: `Cached $/${unit}`, cls: 'nw', f: cachedCell },
    { h: 'Context', cls: 'nw', num: true, f: r => esc(squareCtx(r.contextWindow)) },
    { h: 'Speed', cls: 'nw', f: r => r.status && r.status.tps ? `<i class="mdot s-${esc(squareStatus(r))}" aria-hidden="true"></i> ${esc(fmtTps(r.status.tps))} tok/s<div class="cell-sub">${esc(fmtMs(r.status.firstTokenMs ?? r.status.responseMs))}</div>` : `<i class="mdot s-${esc(squareStatus(r))}" aria-hidden="true"></i> <span class="muted">—</span>` },
    { h: 'Updated', cls: 'nw', f: r => r.updatedUtc ? `<span data-tip="${esc(ts(r.updatedUtc))}">${esc(ago(r.updatedUtc))}</span><div class="cell-sub">${esc(r.updatedBy || '')}</div>` : '<span class="muted">—</span>' }
  ], rows, { empty: 'No models match.', rowClass: r => SQUARE.dirty.has(r.id) ? 'selected' : '' });
}
/** Redraws the results and the filter counts; the search box keeps its focus. */
function squareRefresh() {
  const res = $('#ms-results'); if (res) { res.innerHTML = squareResults(); if (typeof applyDataStyles === 'function') applyDataStyles(res); }
  const f = $('#ms-filters'); if (f) f.innerHTML = squareFilters();
  const c = $('#ms-count'); if (c) c.textContent = num(squareRows().length);
}
document.addEventListener('input', ev => {
  const el = ev.target.closest && ev.target.closest('[data-ms]');
  if (!el) return;
  if (el.dataset.ms === 'q') { SQUARE.q = el.value; squareRefresh(); return; }
  const row = SQUARE.rows.find(r => r.id === el.dataset.id);
  if (!row) return;
  const cur = SQUARE.dirty.get(row.id) || { in: row.inputPerMillion, out: row.outputPerMillion, cached: row.cachedInputPerMillion ?? null };
  if (el.dataset.ms === 'cached') cur.cached = el.value.trim() === '' ? null : Math.max(0, Number(el.value) || 0);
  else cur[el.dataset.ms] = Math.max(0, Number(el.value) || 0);
  if (cur.in === row.inputPerMillion && cur.out === row.outputPerMillion && cur.cached === (row.cachedInputPerMillion ?? null)) SQUARE.dirty.delete(row.id); else SQUARE.dirty.set(row.id, cur);
  el.closest('tr').classList.toggle('selected', SQUARE.dirty.has(row.id));
  const b = $('#ms-save');
  if (b) { b.disabled = SQUARE.dirty.size === 0; b.textContent = SQUARE.dirty.size ? `Save ${plural(SQUARE.dirty.size, 'price')}` : 'Save prices'; }
});
ACTIONS['ms-filter'] = el => { SQUARE.f[el.dataset.key] = el.dataset.value; squareRefresh(); };
ACTIONS['ms-reset'] = () => { SQUARE.f = Object.assign({}, SQUARE_FILTERS); SQUARE.q = ''; const s = $('.ms-search'); if (s) s.value = ''; squareRefresh(); };
ACTIONS['ms-unit'] = el => { SQUARE.unit = el.dataset.value; LS.set('square.unit', SQUARE.unit); $$('[data-act="ms-unit"]').forEach(b => b.classList.toggle('on', b === el)); squareRefresh(); };
ACTIONS['ms-view'] = el => { SQUARE.view = el.dataset.value; LS.set('square.view', SQUARE.view); $$('[data-act="ms-view"]').forEach(b => b.classList.toggle('on', b === el)); squareRefresh(); };
CHANGES['ms-sort'] = el => { SQUARE.sort = el.value; LS.set('square.sort', SQUARE.sort); squareRefresh(); };
ACTIONS['ms-save'] = async () => {
  const prices = [...SQUARE.dirty].map(([model, v]) => ({ model, inputPerMillion: v.in, outputPerMillion: v.out, cachedInputPerMillion: v.cached }));
  if (!prices.length) return;
  const r = await put('/api/model-square', { prices });
  toast(`Saved ${plural(r.changed, 'price')}` + (r.agentKeys && r.agentKeys.length ? ` (also for agents: ${r.agentKeys.join(', ')})` : ''), 'ok');
  SQUARE.dirty = new Map();
  navigate(location.pathname + location.search, false);
};
// Escape closes the checkout too (the payment may still arrive: keep watching for it).
document.addEventListener('keydown', e => {
  if (!S.checkout || !keyIs(e, 'app.close')) return;
  const id = S.checkout.invoiceId;
  closeCheckout(); watchDeposit(id);
});
