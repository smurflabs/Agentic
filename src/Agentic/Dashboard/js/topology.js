'use strict';
/* Communications → Topology: who may message whom, and who did (in the last hours).
   Layout: the most linked agent in the centre, the others on rings around it - the more links an agent has, the closer to
   the centre - and on each ring next to the agents it is linked with. The rings are ellipses, so the drawing suits a wide
   card, and it grows with the team: 5 agents or 50 keep the same spacing (zoom, drag and full screen for the details).
   It is built once and then updated in place on live refreshes (#topo has data-morph="keep"), so its motion never
   restarts: messages travel along busy links as glowing dots, working agents send out ripples, every agent breathes a
   little, a link that just carried a message flares and its two agents flash, and agents glide to their new places when
   the team changes. Hovering an agent lights up its links and neighbours and dims everything else. */

const TOPO = { sig: '', layout: null, shown: null, tween: 0, view: null, focus: null };
const TOPO_NS = 'http://www.w3.org/2000/svg';

function topoEl(tag, attrs, parent) {
  const e = document.createElementNS(TOPO_NS, tag);
  if (attrs) for (const k in attrs) if (attrs[k] != null) e.setAttribute(k, attrs[k]);
  if (parent) parent.appendChild(e);
  return e;
}
function topoKey(a, b) { return a + '>' + b; }
/** An id for a link's path (the dots that travel along it point at it). */
function topoPathId(k) {
  let h = 0;
  for (let i = 0; i < k.length; i++) h = (h * 31 + k.charCodeAt(i)) | 0;
  return 'tpe-' + k.replace(/[^A-Za-z0-9-]/g, '_').slice(0, 60) + '-' + (h >>> 0).toString(36);
}
function topoStateClass(state) {
  const s = String(state || '').toLowerCase();
  if (/err|fail|crash|dead|broke|bankrupt/.test(s)) return 'bad';
  if (/run|busy|work|think/.test(s)) return 'run';
  if (/wait|block|pend|queue|start/.test(s)) return 'wait';
  if (/stop|pause|disab|off|retir|sleep/.test(s)) return 'off';
  return 'idle';
}
function topoInitials(name) {
  const w = String(name || '?').replace(/[^A-Za-z0-9 ]+/g, ' ').trim().split(/\s+/).filter(Boolean);
  if (!w.length) return '?';
  return (w[0][0] + (w.length > 1 ? w[w.length - 1][0] : (w[0][1] || ''))).toUpperCase();
}
function topoShortName(name) { const s = String(name || ''); return s.length > 16 ? s.slice(0, 15) + '…' : s; }

/** The shape of the graph (agents and links, not message counts): the layout changes only when this does. */
function topoSignature(topo) {
  const ids = topo.nodes.map(n => n.id).sort().join(',');
  const perms = (topo.permissions || []).filter(e => e.to !== '*').map(e => topoKey(e.from, e.to)).sort().join(',');
  const traffic = (topo.traffic || []).filter(t => t.from !== 'admin').map(t => topoKey(t.from, t.to)).sort().join(',');
  return ids + '|' + perms + '|' + traffic;
}

/** Positions: {W, H, r, rings: [{rx, ry}], pos: {id: {x, y}}, ring: {id: n}, centre: id}. */
function topoLayout(topo) {
  const nodes = topo.nodes.slice(), n = nodes.length;
  const ids = new Set(nodes.map(x => x.id));
  const links = new Map(nodes.map(x => [x.id, new Map()]));
  const link = (a, b, w) => {
    if (a === b || !ids.has(a) || !ids.has(b)) return;
    links.get(a).set(b, (links.get(a).get(b) || 0) + w);
    links.get(b).set(a, (links.get(b).get(a) || 0) + w);
  };
  (topo.permissions || []).forEach(e => { if (e.to !== '*') link(e.from, e.to, 1); });
  (topo.traffic || []).forEach(t => link(t.from, t.to, 4 + Math.log2(1 + (t.count || 0))));
  const weight = id => { let s = 0; links.get(id).forEach(w => { s += w; }); return s; };
  nodes.sort((a, b) => weight(b.id) - weight(a.id) || String(a.name).localeCompare(String(b.name)));

  const r = n > 30 ? 19 : n > 14 ? 22 : 26;      // an agent's circle
  const arc = n > 30 ? 92 : 104;                 // room for one agent (and its name) along a ring
  const gap = n > 30 ? 96 : 106;                 // between rings
  // Rings are ellipses, wider than tall for a wide card; a big team gets rounder rings (a taller drawing at a larger scale).
  const ex = n > 30 ? 1.3 : 1.5, ey = n > 30 ? 0.98 : 0.84;
  const perim = rr => { const a = rr * ex, b = rr * ey; return Math.PI * (3 * (a + b) - Math.sqrt((3 * a + b) * (a + 3 * b))); };
  const centre = n > 6 ? nodes.slice(0, 1) : [];
  const rest = nodes.slice(centre.length);
  const rings = [];
  if (rest.length) {
    let rr = centre.length ? gap * 1.15 : Math.max(gap * 0.8, rest.length * arc / (Math.PI * (ex + ey))), cap = 0;
    while (cap < rest.length) { const c = Math.max(1, Math.floor(perim(rr) / arc)); rings.push({ rr, cap: c }); cap += c; rr += gap; }
    // The same spacing on every ring: each takes its share of the agents by its room (the most linked on the inner ones).
    let left = rest.length;
    rings.forEach((g, i) => { g.count = i === rings.length - 1 ? left : Math.min(g.cap, Math.round(rest.length * g.cap / cap)); left -= g.count; });
  }

  const pos = {}, ring = {}, angle = {};
  centre.forEach(c => { pos[c.id] = { x: 0, y: 0 }; ring[c.id] = -1; });
  let at = 0;
  rings.forEach((g, ri) => {
    const members = rest.slice(at, at + g.count);
    at += g.count;
    if (!members.length) return;
    // Each agent near those it is linked with on the inner rings (the circular mean of their angles).
    const pref = members.map(m => {
      let sx = 0, sy = 0;
      links.get(m.id).forEach((w, other) => { const a = angle[other]; if (a !== undefined) { sx += Math.cos(a) * w; sy += Math.sin(a) * w; } });
      return { m, a: sx || sy ? Math.atan2(sy, sx) : null };
    });
    const placed = pref.filter(p => p.a !== null).sort((p, q) => p.a - q.a);
    const free = pref.filter(p => p.a === null);
    // The unlinked ones spread evenly between the linked ones.
    const m = pref.length, step = 2 * Math.PI / m;
    let order;
    if (!placed.length || !free.length) order = placed.concat(free);
    else {
      order = new Array(m).fill(null);
      free.forEach((p, i) => { order[Math.min(m - 1, Math.floor((i + 0.5) * m / free.length))] = p; });
      let pi = 0;
      for (let j = 0; j < m; j++) if (!order[j]) order[j] = placed[pi++];
    }
    let start = -Math.PI / 2 + (ri % 2 ? step / 2 : 0);
    if (placed.length) {
      let sx = 0, sy = 0;
      order.forEach((p, j) => { if (p.a !== null) { sx += Math.cos(p.a - j * step); sy += Math.sin(p.a - j * step); } });
      start = Math.atan2(sy, sx);
    }
    order.forEach((p, j) => {
      const a = start + j * step;
      angle[p.m.id] = a;
      ring[p.m.id] = ri;
      pos[p.m.id] = { x: g.rr * ex * Math.cos(a), y: g.rr * ey * Math.sin(a) };
    });
  });

  const outer = rings.length ? rings[rings.length - 1].rr : 0;
  const W = Math.max(560, 2 * (outer * ex + arc * 0.6 + 24));
  const H = Math.max(340, 2 * (outer * ey + r + 36));
  const cx = W / 2, cy = H / 2 - 6;
  Object.values(pos).forEach(p => { p.x += cx; p.y += cy; });
  return { W, H, cx, cy, r, rings: rings.map(g => ({ rx: g.rr * ex, ry: g.rr * ey })), pos, ring, centre: centre.length ? centre[0].id : null };
}

/** A link's curve from one agent's edge to the other's (two directions bend to opposite sides), and its middle. */
function topoPath(p1, p2, r) {
  const dx = p2.x - p1.x, dy = p2.y - p1.y, len = Math.hypot(dx, dy) || 1;
  const ux = dx / len, uy = dy / len, off = r + 5;
  const x1 = p1.x + ux * off, y1 = p1.y + uy * off, x2 = p2.x - ux * (off + 3), y2 = p2.y - uy * (off + 3);
  const bend = Math.min(48, len * 0.14);
  const mx = (x1 + x2) / 2 - uy * bend, my = (y1 + y2) / 2 + ux * bend;
  return { d: `M${x1.toFixed(1)},${y1.toFixed(1)} Q${mx.toFixed(1)},${my.toFixed(1)} ${x2.toFixed(1)},${y2.toFixed(1)}`,
    lx: (x1 + 2 * mx + x2) / 4, ly: (y1 + 2 * my + y2) / 4, len };
}

function drawTopology(el, topo, animate) {
  if (!el || !topo) return;
  const nodes = topo.nodes || [];
  if (!nodes.length) { el.innerHTML = '<p class="muted">No agents yet.</p>'; TOPO.sig = ''; TOPO.layout = null; return; }
  const motion = !reducedMotion();
  // Links whose message count grew since the last refresh: these agents just spoke.
  const prev = S.topoCounts || {}, counts = {}, bumped = new Set();
  (topo.traffic || []).forEach(t => { const k = topoKey(t.from, t.to); counts[k] = t.count; if (animate && prev[k] !== undefined && t.count > prev[k]) bumped.add(k); });
  S.topoCounts = counts;

  let svg = el.querySelector('svg.topo-svg');
  const first = !svg;
  if (first) svg = topoBuild(el, motion);
  const sig = topoSignature(topo);
  const relayout = first || sig !== TOPO.sig || !TOPO.layout;
  if (relayout) {
    TOPO.layout = topoLayout(topo);
    TOPO.sig = sig;
    TOPO.view = null;
    topoRings(svg, TOPO.layout);
  }
  const L = TOPO.layout;
  topoViewBox(svg);
  svg.style.maxWidth = Math.round(L.W * 1.25) + 'px';
  topoNodes(svg, L, nodes, bumped, first, motion);
  topoEdges(svg, L, topo, bumped, motion);
  // A changed team: agents glide to their new places (new ones appear where they belong).
  const from = TOPO.shown, to = L.pos;
  if (!first && relayout && from && motion) topoTween(svg, L, from, to);
  else { cancelAnimationFrame(TOPO.tween); TOPO.shown = to; topoPlace(svg, L, to); }
  const f = TOPO.focus; TOPO.focus = null; topoFocus(svg, f);
}

function topoBuild(el, motion) {
  el.innerHTML = `<div class="topo-tools">
      <button type="button" class="btn xs ghost" data-act="topo-zoom" data-z="1.3" aria-label="Zoom in" data-tip="Zoom in (or Ctrl + scroll)">+</button>
      <button type="button" class="btn xs ghost" data-act="topo-zoom" data-z="0.77" aria-label="Zoom out" data-tip="Zoom out">−</button>
      <button type="button" class="btn xs ghost" data-act="topo-fit" data-tip="Show everyone">Fit</button>
      <button type="button" class="btn xs ghost" data-act="topo-full" data-tip="Full screen (Esc to leave)">Full screen</button></div>
    <div class="topo-stage"><svg class="topo-svg" role="img" aria-label="Agent communication topology">
      <defs>
        <marker id="tp-arrow" viewBox="0 0 10 10" refX="8" refY="5" markerUnits="userSpaceOnUse" markerWidth="9" markerHeight="9" orient="auto-start-reverse"><path d="M0,1 L10,5 L0,9 z" class="tp-arrow"></path></marker>
        <marker id="tp-arrow-bad" viewBox="0 0 10 10" refX="8" refY="5" markerUnits="userSpaceOnUse" markerWidth="9" markerHeight="9" orient="auto-start-reverse"><path d="M0,1 L10,5 L0,9 z" class="tp-arrow bad"></path></marker>
        <radialGradient id="tp-core" cx="35%" cy="30%" r="80%"><stop offset="0%" class="tp-core-hi"></stop><stop offset="100%" class="tp-core-lo"></stop></radialGradient>
        <radialGradient id="tp-glow"><stop offset="0%" class="tp-glow-in"></stop><stop offset="100%" class="tp-glow-out"></stop></radialGradient>
      </defs>
      <g class="tp-rings"></g><g class="tp-edges"></g><g class="tp-labels"></g><g class="tp-flow"></g><g class="tp-nodes"></g></svg></div>`;
  const svg = el.querySelector('svg');
  TOPO.shown = null; TOPO.view = null; TOPO.focus = null;
  if (motion) { svg.classList.add('tp-enter'); setTimeout(() => svg.classList.remove('tp-enter'), 2200); }
  topoWire(el, svg);
  return svg;
}

/** The rings as faint guides that slowly turn, and a soft glow around the centre. */
function topoRings(svg, L) {
  const layer = svg.querySelector('.tp-rings');
  layer.textContent = '';
  if (L.centre) topoEl('circle', { class: 'tp-glow', cx: L.cx, cy: L.cy, r: L.r * 4.2 }, layer);
  L.rings.forEach(g => topoEl('ellipse', { class: 'tp-ring', cx: L.cx, cy: L.cy, rx: g.rx, ry: g.ry }, layer));
}

function topoNodes(svg, L, nodes, bumped, first, motion) {
  const layer = svg.querySelector('.tp-nodes');
  const have = new Map(Array.from(layer.children).map(g => [g.dataset.id, g]));
  const spoke = new Set();
  bumped.forEach(k => { const [a, b] = k.split('>'); spoke.add(a); spoke.add(b); });
  nodes.forEach((n, i) => {
    if (!L.pos[n.id]) return;
    let g = have.get(n.id);
    have.delete(n.id);
    if (!g || g.classList.contains('tp-gone')) {
      if (g) g.remove();
      g = topoEl('g', { class: 'tp-node', 'data-id': n.id }, layer);
      const pop = topoEl('g', { class: 'tp-pop' }, g);
      const a = topoEl('a', { href: '/agents/' + encodeURIComponent(n.id), 'data-nav': '', class: 'tp-body' }, pop);
      topoEl('circle', { class: 'tp-wave' }, a);
      topoEl('circle', { class: 'tp-halo' }, a);
      topoEl('circle', { class: 'tp-core' }, a);
      topoEl('text', { class: 'tp-init', y: 4 }, a);
      topoEl('circle', { class: 'tp-state' }, a);
      topoEl('text', { class: 'tp-name' }, a);
      // Breathing and ripples out of step with each other; on the first view they pop out ring by ring.
      g.style.setProperty('--tp-delay', `-${((i * 0.61) % 4.5).toFixed(2)}s`);
      g.style.setProperty('--tp-in', first ? `${(L.ring[n.id] + 1) * 120 + (i % 14) * 22}ms` : '0ms');
      if (!first && motion) { g.classList.add('tp-arrive'); setTimeout(() => g.classList.remove('tp-arrive'), 900); }
    }
    const a = g.querySelector('.tp-body');
    a.setAttribute('data-tip', `${n.name}${n.role ? ' · ' + n.role : ''} · ${n.state}`);
    a.setAttribute('aria-label', `${n.name} (${n.state})`);
    ['tp-wave', 'tp-core'].forEach(c => g.querySelector('.' + c).setAttribute('r', L.r));
    g.querySelector('.tp-halo').setAttribute('r', L.r + 5);
    const st = g.querySelector('.tp-state');
    st.setAttribute('cx', (L.r * 0.72).toFixed(1)); st.setAttribute('cy', (-L.r * 0.72).toFixed(1)); st.setAttribute('r', L.r > 20 ? 5 : 4.2);
    const init = g.querySelector('.tp-init');
    if (init.textContent !== topoInitials(n.name)) init.textContent = topoInitials(n.name);
    const name = g.querySelector('.tp-name');
    name.setAttribute('y', L.r + 15);
    if (name.textContent !== topoShortName(n.name)) name.textContent = topoShortName(n.name);
    ['st-run', 'st-idle', 'st-wait', 'st-bad', 'st-off'].forEach(c => g.classList.remove(c));
    g.classList.add('st-' + topoStateClass(n.state));
    g.classList.toggle('tp-centre', n.id === L.centre);
    if (spoke.has(n.id) && motion) { g.classList.remove('spoke'); void g.getBoundingClientRect(); g.classList.add('spoke'); }
  });
  // Agents that left fade out.
  have.forEach(g => { g.classList.add('tp-gone'); setTimeout(() => g.remove(), 450); });
}

function topoEdges(svg, L, topo, bumped, motion) {
  const edges = svg.querySelector('.tp-edges'), labels = svg.querySelector('.tp-labels'), flow = svg.querySelector('.tp-flow');
  const wanted = new Map();
  const traffic = new Map();
  (topo.traffic || []).forEach(t => { if (t.from !== 'admin' && L.pos[t.from] && L.pos[t.to]) traffic.set(topoKey(t.from, t.to), t); });
  (topo.permissions || []).forEach(e => {
    const k = topoKey(e.from, e.to);
    if (e.to !== '*' && !traffic.has(k) && L.pos[e.from] && L.pos[e.to]) wanted.set(k, { a: e.from, b: e.to, perm: true });
  });
  traffic.forEach((t, k) => wanted.set(k, { a: t.from, b: t.to, t }));
  const have = new Map(Array.from(edges.children).map(p => [p.dataset.k, p]));
  wanted.forEach((w, k) => {
    let p = have.get(k);
    have.delete(k);
    const id = topoPathId(k);
    if (!p) p = topoEl('path', { id, 'data-k': k, 'data-a': w.a, 'data-b': w.b }, edges);
    if (w.perm) {
      p.setAttribute('class', 'tp-edge perm');
      p.removeAttribute('marker-end');
      p.removeAttribute('stroke-width');
      p.setAttribute('data-tip', `${w.a} may message ${w.b} (no messages yet)`);
      labels.querySelector(`[data-k="${CSS.escape(k)}"]`)?.remove();
      flow.querySelector(`[data-k="${CSS.escape(k)}"]`)?.remove();
      return;
    }
    const t = w.t, fail = t.failed > 0;
    p.setAttribute('class', 'tp-edge traffic' + (fail ? ' fail' : ''));
    p.setAttribute('marker-end', fail ? 'url(#tp-arrow-bad)' : 'url(#tp-arrow)');
    p.setAttribute('stroke-width', (1.6 + Math.min(3, Math.log2(1 + t.count) * 0.55)).toFixed(2));
    p.setAttribute('data-tip', `${t.from} → ${t.to}: ${num(t.count)} message${t.count === 1 ? '' : 's'}${t.failed ? `, ${num(t.failed)} failed` : ''}`);
    let lab = labels.querySelector(`[data-k="${CSS.escape(k)}"]`);
    if (!lab) lab = topoEl('text', { class: 'tp-elabel', 'data-k': k }, labels);
    lab.classList.toggle('fail', fail);
    if (lab.textContent !== String(t.count)) lab.textContent = String(t.count);
    // Messages in flight: one to three dots travel along the link, more for busier links.
    const want = motion ? (t.count >= 50 ? 3 : t.count >= 10 ? 2 : 1) : 0;
    let dots = flow.querySelector(`.tp-dots[data-k="${CSS.escape(k)}"]`);
    if (dots && (Number(dots.dataset.n) !== want || dots.classList.contains('fail') !== fail)) { dots.remove(); dots = null; }
    if (!dots && want) {
      dots = topoEl('g', { class: 'tp-dots' + (fail ? ' fail' : ''), 'data-k': k, 'data-n': want }, flow);
      const dur = Math.max(1.8, Math.min(4.2, 1.4 + Math.log2(2 + t.count) * 0.35 + Math.random() * 0.6));
      for (let j = 0; j < want; j++) {
        const c = topoEl('circle', { class: 'tp-msg', r: 2.9 }, dots);
        const am = topoEl('animateMotion', { dur: dur.toFixed(2) + 's', repeatCount: 'indefinite', begin: `-${(dur * j / want + Math.random() * 0.4).toFixed(2)}s` }, c);
        topoEl('mpath', { href: '#' + id }, am);
      }
    }
    if (bumped.has(k) && motion) {
      p.classList.remove('bump'); void p.getBoundingClientRect(); p.classList.add('bump');
      // A burst: one bright dot races along the link.
      const c = topoEl('circle', { class: 'tp-burst', r: 4.5 }, flow);
      const am = topoEl('animateMotion', { dur: '1.1s', repeatCount: '1', begin: 'indefinite', fill: 'freeze' }, c);
      topoEl('mpath', { href: '#' + id }, am);
      try { am.beginElement(); } catch { /* no SMIL */ }
      setTimeout(() => c.remove(), 1250);
    }
  });
  have.forEach((p, k) => {
    p.remove();
    labels.querySelector(`[data-k="${CSS.escape(k)}"]`)?.remove();
    flow.querySelector(`.tp-dots[data-k="${CSS.escape(k)}"]`)?.remove();
  });
}

/** Puts agents and links where posMap says. */
function topoPlace(svg, L, posMap) {
  svg.querySelectorAll('.tp-node').forEach(g => {
    const p = posMap[g.dataset.id];
    if (p) g.setAttribute('transform', `translate(${p.x.toFixed(1)},${p.y.toFixed(1)})`);
  });
  svg.querySelectorAll('.tp-edge').forEach(e => {
    const a = posMap[e.dataset.a], b = posMap[e.dataset.b];
    if (!a || !b) return;
    const c = topoPath(a, b, L.r);
    e.setAttribute('d', c.d);
    const lab = e.classList.contains('traffic') && svg.querySelector(`.tp-elabel[data-k="${CSS.escape(e.dataset.k)}"]`);
    if (lab) { lab.setAttribute('x', c.lx.toFixed(1)); lab.setAttribute('y', c.ly.toFixed(1)); }
  });
}

function topoTween(svg, L, from, to) {
  cancelAnimationFrame(TOPO.tween);
  const t0 = performance.now(), ms = 750;
  const ease = x => 1 - Math.pow(1 - x, 3);
  const frame = now => {
    const k = ease(Math.min(1, (now - t0) / ms));
    const cur = {};
    Object.keys(to).forEach(id => {
      const a = from[id] || to[id], b = to[id];
      cur[id] = { x: a.x + (b.x - a.x) * k, y: a.y + (b.y - a.y) * k };
    });
    TOPO.shown = cur;
    topoPlace(svg, L, cur);
    if (k < 1) TOPO.tween = requestAnimationFrame(frame); else TOPO.shown = to;
  };
  TOPO.tween = requestAnimationFrame(frame);
}

/** Hovering an agent: its links and the agents at their other end stay bright, everything else dims. */
function topoFocus(svg, id) {
  if (TOPO.focus === id) return;
  TOPO.focus = id;
  svg.querySelectorAll('.hl, .nb').forEach(e => e.classList.remove('hl', 'nb'));
  svg.classList.toggle('focus', !!id);
  if (!id) return;
  const node = svg.querySelector(`.tp-node[data-id="${CSS.escape(id)}"]`);
  if (!node) { svg.classList.remove('focus'); TOPO.focus = null; return; }
  node.classList.add('hl');
  svg.querySelectorAll(`.tp-edge[data-a="${CSS.escape(id)}"], .tp-edge[data-b="${CSS.escape(id)}"]`).forEach(e => {
    e.classList.add('hl');
    const other = e.dataset.a === id ? e.dataset.b : e.dataset.a;
    svg.querySelector(`.tp-node[data-id="${CSS.escape(other)}"]`)?.classList.add('nb');
    svg.querySelectorAll(`[data-k="${CSS.escape(e.dataset.k)}"]:not(.tp-edge)`).forEach(x => x.classList.add('hl'));
  });
}

function topoViewBox(svg) {
  const L = TOPO.layout;
  if (!L) return;
  const v = TOPO.view || { x: 0, y: 0, w: L.W, h: L.H };
  svg.setAttribute('viewBox', `${v.x.toFixed(1)} ${v.y.toFixed(1)} ${v.w.toFixed(1)} ${v.h.toFixed(1)}`);
  svg.closest('.topo')?.classList.toggle('zoomed', !!TOPO.view);
}
/** Zooms by f (above 1: closer) around a point in drawing units (the middle when none is given). */
function topoZoom(svg, f, px, py) {
  const L = TOPO.layout;
  if (!L) return;
  const v = TOPO.view || { x: 0, y: 0, w: L.W, h: L.H };
  const w = Math.min(L.W, Math.max(L.W / 6, v.w / f)), h = w * L.H / L.W;
  if (w >= L.W - 0.5) { TOPO.view = null; topoViewBox(svg); return; }
  const cx = px === undefined ? v.x + v.w / 2 : px, cy = py === undefined ? v.y + v.h / 2 : py;
  const x = cx - (cx - v.x) * (w / v.w), y = cy - (cy - v.y) * (h / v.h);
  TOPO.view = { x: Math.min(Math.max(0, x), L.W - w), y: Math.min(Math.max(0, y), L.H - h), w, h };
  topoViewBox(svg);
}

/** Hover, Ctrl + scroll to zoom, drag to move around a zoomed drawing (a drag never opens an agent). */
function topoWire(el, svg) {
  svg.addEventListener('pointerover', ev => { const g = ev.target.closest && ev.target.closest('.tp-node'); topoFocus(svg, g ? g.dataset.id : null); });
  svg.addEventListener('pointerleave', () => topoFocus(svg, null));
  svg.addEventListener('wheel', ev => {
    if (!ev.ctrlKey && !ev.metaKey) return;
    ev.preventDefault();
    const m = svg.getScreenCTM();
    if (!m) return;
    const p = new DOMPoint(ev.clientX, ev.clientY).matrixTransform(m.inverse());
    topoZoom(svg, ev.deltaY < 0 ? 1.18 : 1 / 1.18, p.x, p.y);
  }, { passive: false });
  let drag = null;
  svg.addEventListener('pointerdown', ev => {
    if (!TOPO.view || ev.button !== 0) return;
    drag = { x: ev.clientX, y: ev.clientY, v: Object.assign({}, TOPO.view), moved: false, id: ev.pointerId };
  });
  svg.addEventListener('pointermove', ev => {
    if (!drag || ev.pointerId !== drag.id) return;
    const dx = ev.clientX - drag.x, dy = ev.clientY - drag.y;
    if (!drag.moved && Math.hypot(dx, dy) < 4) return;
    if (!drag.moved) { drag.moved = true; svg.setPointerCapture(ev.pointerId); svg.classList.add('dragging'); }
    const L = TOPO.layout, scale = drag.v.w / (svg.clientWidth || 1);
    TOPO.view = { x: Math.min(Math.max(0, drag.v.x - dx * scale), L.W - drag.v.w), y: Math.min(Math.max(0, drag.v.y - dy * scale), L.H - drag.v.h), w: drag.v.w, h: drag.v.h };
    topoViewBox(svg);
  });
  const end = () => { if (drag && drag.moved) { svg.classList.remove('dragging'); TOPO.dragEnded = Date.now(); } drag = null; };
  svg.addEventListener('pointerup', end);
  svg.addEventListener('pointercancel', end);
  // The click that ends a drag does not open the agent under the pointer.
  svg.addEventListener('click', ev => { if (TOPO.dragEnded && Date.now() - TOPO.dragEnded < 250) { ev.preventDefault(); ev.stopPropagation(); } }, true);
}

ACTIONS['topo-zoom'] = el => { const svg = el.closest('.topo')?.querySelector('svg.topo-svg'); if (svg) topoZoom(svg, Number(el.dataset.z) || 1); };
ACTIONS['topo-fit'] = el => { const svg = el.closest('.topo')?.querySelector('svg.topo-svg'); if (svg) { TOPO.view = null; topoViewBox(svg); } };
ACTIONS['topo-full'] = el => {
  const stage = el.closest('.topo')?.querySelector('.topo-stage');
  if (!stage) return;
  if (document.fullscreenElement) document.exitFullscreen().catch(() => { });
  else if (stage.requestFullscreen) stage.requestFullscreen().catch(e => toast('Full screen is not available here: ' + e.message, 'bad'));
};
