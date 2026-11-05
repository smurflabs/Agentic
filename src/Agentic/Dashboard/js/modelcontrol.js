'use strict';
/* Agentic console - Model control (Administration): administrators turn models off and on for everyone. A turned-off
   model is not offered in Chat or the API, custom models do not route to it, and agents, images and videos do not use it. */

const MCTL = { rows: [], q: '', show: 'all', selected: new Set() };
const MCTL_KIND = { provider: 'Provider', custom: 'Custom', agent: 'Agent model', local: 'Local', image: 'Image', video: 'Video', other: 'Not listed' };

VIEWS.modelControl = {
  live: false,
  async render() {
    setCrumbs([['Model control', '/model-control']]);
    if (!can('admin')) return pageHead('Model control') + card('', emptyState('Administrators only', 'Ask an administrator to turn models on or off.'));
    const d = await get('/api/model-control');
    MCTL.rows = d.models; MCTL.selected = new Set();
    const shows = [['all', 'All'], ['on', 'On'], ['off', 'Off'], ['provider', 'Provider'], ['custom', 'Custom'], ['agents', 'Used by agents'], ['media', 'Images & videos']];
    return pageHead('Model control', {
      subtitle: 'Turn models off for everyone. A model that is off is not shown in the Chat tab\'s model list, the API refuses it, custom models skip it when they route, and agents, image and video generation do not use it. Turn it on again at any time.'
    }) + (d.providerError ? `<div class="notice warn">The provider's model list is unavailable (${esc(d.providerError)}); only known models are shown.</div>` : '') +
      `<div class="grid kpis section">${kpi('Models', num(d.models.length))}${kpi('On', num(d.models.filter(m => m.enabled).length))}` +
      `${kpi('Off', num(d.disabledCount), d.disabledCount ? 'used by nobody' : 'every model is on')}${kpi('Used by agents', num(d.models.filter(m => m.agents.length).length))}</div>` +
      `<div class="card section"><div class="card-head ms-tools"><input type="search" class="ms-search" placeholder="Search models" value="${esc(MCTL.q)}" data-mctl="q" aria-label="Search models">` +
      `<div class="chips">${shows.map(([k, l]) => `<button type="button" class="chip-toggle${MCTL.show === k ? ' on' : ''}" data-act="mctl-show" data-show="${k}">${esc(l)}</button>`).join('')}</div>` +
      `<span class="hint" id="mctl-count"></span>` +
      `<div class="actions"><button class="btn xs ghost" data-act="mctl-pick-shown">Select shown</button><button class="btn xs" data-act="mctl-bulk" data-enabled="1" id="mctl-on" disabled>Turn on</button><button class="btn xs danger" data-act="mctl-bulk" data-enabled="0" id="mctl-off" disabled>Turn off</button></div></div>` +
      `<div class="card-body flush" id="mctl-table">${mctlTable()}</div></div>`;
  },
  after() { mctlCount(); }
};

function mctlRows() {
  const q = MCTL.q.trim().toLowerCase();
  return MCTL.rows.filter(r => {
    if (q && !(r.id + ' ' + r.name + ' ' + (r.ownedBy || '') + ' ' + r.agents.join(' ') + ' ' + r.routedBy.join(' ')).toLowerCase().includes(q)) return false;
    switch (MCTL.show) {
      case 'on': return r.enabled;
      case 'off': return !r.enabled;
      case 'provider': return r.kinds.includes('provider');
      case 'custom': return r.kinds.includes('custom');
      case 'agents': return r.agents.length > 0;
      case 'media': return r.kinds.includes('image') || r.kinds.includes('video');
      default: return true;
    }
  });
}
function mctlCount() {
  const c = $('#mctl-count');
  if (c) c.textContent = `${mctlRows().length} of ${plural(MCTL.rows.length, 'model')}` + (MCTL.selected.size ? ` · ${MCTL.selected.size} selected` : '');
  const on = $('#mctl-on'), off = $('#mctl-off');
  if (on) on.disabled = !MCTL.selected.size;
  if (off) off.disabled = !MCTL.selected.size;
}
function mctlTable() {
  const rows = mctlRows();
  return table([
    { h: '', cls: 'nw', f: r => `<input type="checkbox" data-mctl="pick" data-id="${esc(r.id)}" aria-label="Select ${esc(r.id)}"${MCTL.selected.has(r.id) ? ' checked' : ''}>` },
    { h: 'Model', f: r => `<span class="cell-title">${esc(r.name)}</span>` +
        `<div class="cell-sub"><code>${esc(r.id)}</code>${r.ownedBy ? ' · ' + esc(r.ownedBy) : ''}</div>` +
        (r.agents.length ? `<div class="cell-sub">agents: ${esc(r.agents.join(', '))}</div>` : '') +
        (r.routedBy.length ? `<div class="cell-sub">custom models routing to it: ${esc(r.routedBy.join(', '))}</div>` : '') },
    { h: 'Kind', f: r => chips(r.kinds.map(k => MCTL_KIND[k] || k)) },
    { h: 'Status', cls: 'nw', f: r => r.enabled ? badge('On', 'ok') : badge('Off', 'bad') +
        (r.disabledBy || r.disabledUtc ? `<div class="cell-sub">by ${esc(r.disabledBy || '?')}${r.disabledUtc ? ' · ' + `<span data-tip="${esc(ts(r.disabledUtc))}">${esc(ago(r.disabledUtc))}</span>` : ''}</div>` : '') },
    { h: '', cls: 'act', f: r => `<label class="switch" data-tip="${r.enabled ? 'Turn off for everyone' : 'Turn on again'}"><input type="checkbox" data-mctl="toggle" data-id="${esc(r.id)}"${r.enabled ? ' checked' : ''} aria-label="${esc(r.id)} on"><span></span></label>` }
  ], rows, { empty: 'No models match.', rowClass: r => r.enabled ? '' : 'row-off' });
}
function mctlRefresh() { const t = $('#mctl-table'); if (t) t.innerHTML = mctlTable(); mctlCount(); }

async function mctlSet(ids, enabled) {
  if (!ids.length) return;
  if (!enabled) {
    const used = MCTL.rows.filter(r => ids.includes(r.id) && (r.agents.length || r.routedBy.length));
    const note = used.length ? ` ${used.length === 1 ? 'One of them is' : used.length + ' of them are'} used by agents or custom models, which will skip ${used.length === 1 ? 'it' : 'them'}.` : '';
    if (!confirm(`Turn off ${ids.length === 1 ? ids[0] : plural(ids.length, 'model')} for everyone?${note}`)) { mctlRefresh(); return; }
  }
  await put('/api/model-control', { models: ids, enabled });
  toast(`${ids.length === 1 ? ids[0] : plural(ids.length, 'model')} turned ${enabled ? 'on' : 'off'}`, 'ok');
  MCTL.selected = new Set();
  navigate('/model-control', false);
}

document.addEventListener('input', ev => {
  const el = ev.target.closest && ev.target.closest('[data-mctl="q"]');
  if (!el) return;
  MCTL.q = el.value; mctlRefresh();
});
document.addEventListener('change', async ev => {
  const el = ev.target.closest && ev.target.closest('[data-mctl]');
  if (!el) return;
  const kind = el.dataset.mctl;
  if (kind === 'pick') { if (el.checked) MCTL.selected.add(el.dataset.id); else MCTL.selected.delete(el.dataset.id); mctlCount(); return; }
  if (kind === 'all') { mctlRows().forEach(r => { if (el.checked) MCTL.selected.add(r.id); else MCTL.selected.delete(r.id); }); mctlRefresh(); return; }
  if (kind === 'toggle') {
    el.disabled = true;
    try { await mctlSet([el.dataset.id], el.checked); }
    catch (e) { toast(e.message, 'bad'); el.checked = !el.checked; }
    finally { el.disabled = false; }
  }
});
ACTIONS['mctl-show'] = el => { MCTL.show = el.dataset.show; $$('[data-act="mctl-show"]').forEach(b => b.classList.toggle('on', b === el)); mctlRefresh(); };
ACTIONS['mctl-bulk'] = el => mctlSet([...MCTL.selected], el.dataset.enabled === '1');
ACTIONS['mctl-pick-shown'] = () => {
  const rows = mctlRows();
  const all = rows.length > 0 && rows.every(r => MCTL.selected.has(r.id));
  rows.forEach(r => { if (all) MCTL.selected.delete(r.id); else MCTL.selected.add(r.id); });
  mctlRefresh();
};
