'use strict';
/* Agentic console - News: articles the server collects from major outlets' RSS feeds every few minutes (international, US,
   UK, Europe, Australia, news agencies through Google News, investigative, business), newest first, by category, outlet, words and time; the
   feeds' health on Sources. Agents search the same articles with news_articles; chats when News is on. */

const NEWS = { cat: 'all', q: '', source: '', hours: 24, articles: [], more: false, counts: {}, sources: {}, feeds: null, timer: null, newest: null, fresh: 0, lastRound: null };
const NEWS_TABS = [['all', 'All'], ['international', 'International'], ['us', 'US'], ['uk', 'UK'], ['europe', 'Europe'], ['australia', 'Australia'],
  ['agencies', 'News agencies'], ['investigative', 'Investigative'], ['business', 'Business & markets'], ['sources', 'Sources']];
const NEWS_HOURS = [[6, 'Last 6 hours'], [24, 'Last 24 hours'], [72, 'Last 3 days'], [168, 'Last 7 days'], [720, 'Last 30 days']];

function newsQuery(extra) {
  const p = new URLSearchParams({ hours: String(NEWS.hours), limit: '60' });
  if (NEWS.cat !== 'all') p.set('category', NEWS.cat);
  if (NEWS.q) p.set('q', NEWS.q);
  if (NEWS.source) p.set('source', NEWS.source);
  for (const [k, v] of Object.entries(extra || {})) p.set(k, v);
  return '/api/news?' + p.toString();
}
async function newsLoad() {
  const d = await get(newsQuery());
  NEWS.articles = d.articles; NEWS.more = d.more; NEWS.counts = d.categories || {}; NEWS.sources = d.sources || {}; NEWS.lastRound = d.lastRoundUtc;
  NEWS.newest = d.articles.length ? d.articles[0].publishedUtc : null; NEWS.fresh = 0;
  return d;
}
function newsDay(iso) {
  const d = new Date(iso), today = new Date(); today.setHours(0, 0, 0, 0);
  const day = new Date(d); day.setHours(0, 0, 0, 0);
  const diff = Math.round((today - day) / 86400000);
  return diff === 0 ? 'Today' : diff === 1 ? 'Yesterday' : d.toLocaleDateString([], { weekday: 'long', month: 'long', day: 'numeric' });
}
function newsArticleHtml(a) {
  const time = new Date(a.publishedUtc).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  return `<article class="news-item">
    <div class="news-text">
      <div class="news-meta"><span class="news-src cat-${esc(a.category)}" data-act="news-source" data-source="${esc(a.source)}" data-tip="Only ${esc(a.source)}">${esc(a.source)}</span>
        <span class="muted small" data-tip="${esc(new Date(a.publishedUtc).toLocaleString())}">${esc(ago(a.publishedUtc))} · ${esc(time)}</span>${a.author ? `<span class="muted small">· ${esc(a.author)}</span>` : ''}</div>
      <a class="news-title" href="${esc(a.link)}" target="_blank" rel="noopener noreferrer">${esc(a.title)}</a>
      ${a.summary ? `<p class="news-summary">${esc(a.summary)}</p>` : ''}
    </div>
    ${a.imageUrl ? `<a class="news-pic" href="${esc(a.link)}" target="_blank" rel="noopener noreferrer" tabindex="-1"><img src="/api/news/${enc(a.id)}/image" alt="" loading="lazy" data-pic></a>` : ''}
  </article>`;
}
function newsListHtml() {
  if (!NEWS.articles.length) {
    return card('', emptyState(NEWS.q || NEWS.source ? 'Nothing matches' : 'No articles yet',
      NEWS.q || NEWS.source ? 'Try fewer words, another outlet, or a longer time.' : 'The feeds are read every few minutes; the first articles arrive shortly after the server starts.'));
  }
  let html = '', day = null;
  for (const a of NEWS.articles) {
    const d = newsDay(a.publishedUtc);
    if (d !== day) { if (day !== null) html += '</div>'; html += `<h3 class="news-day">${esc(d)}</h3><div class="news-group">`; day = d; }
    html += newsArticleHtml(a);
  }
  return `<div class="news-list">${html}</div></div>${NEWS.more ? '<div class="row news-more"><button type="button" class="btn" data-act="news-more">Older articles</button></div>' : ''}`;
}
function newsToolbarHtml() {
  const outlets = Object.entries(NEWS.sources).sort((a, b) => a[0].localeCompare(b[0]));
  return `<div class="news-toolbar">
    <div class="news-search">${icon('search')}<input id="news-q" type="search" placeholder="Search headlines and summaries" value="${esc(NEWS.q)}" aria-label="Search"></div>
    <select id="news-source" aria-label="Outlet"><option value="">Every outlet</option>${outlets.map(([s, n]) => `<option value="${esc(s)}"${s === NEWS.source ? ' selected' : ''}>${esc(s)} (${n})</option>`).join('')}</select>
    <select id="news-hours" aria-label="Time">${NEWS_HOURS.map(([h, l]) => `<option value="${h}"${h === NEWS.hours ? ' selected' : ''}>${l}</option>`).join('')}</select>
    <span class="spacer"></span><button type="button" class="news-fresh hidden" id="news-fresh" data-act="news-show-fresh"></button>
  </div>`;
}
function newsSourcesHtml() {
  const f = NEWS.feeds;
  if (!f) return card('', emptyState('Loading…', ''));
  const names = f.categories || {};
  const rows = f.feeds.map(x => `<tr><td><b>${esc(x.name)}</b><div class="muted small news-url">${esc(x.url)}</div></td><td>${esc(names[x.category] || x.category)}</td>
    <td>${x.lastReadUtc ? esc(ago(x.lastReadUtc)) : '—'}</td><td class="num">${x.items}</td><td class="num">${x.added}</td>
    <td>${x.error ? `<span class="aud-status bad" data-tip="${esc(x.error)}"><i></i>${esc(x.error.length > 48 ? x.error.slice(0, 47) + '…' : x.error)}</span>` : x.lastOkUtc ? '<span class="aud-status ok"><i></i>Working</span>' : '<span class="muted small">not read yet</span>'}</td></tr>`).join('');
  return card(`${f.feeds.length} feeds`, `<div class="table-wrap"><table class="table"><thead><tr><th>Outlet</th><th>Category</th><th>Read</th><th class="num">Items</th><th class="num">New</th><th>Status</th></tr></thead><tbody>${rows}</tbody></table></div>
    <p class="muted small news-note">Read every ${esc(String(f.pollMinutes))} minutes, kept ${esc(String(f.keepDays))} days. Reuters and the Associated Press (no public feeds of their own), NHK World and the Washington Post (their feeds stopped working) come through Google News.</p>`,
    { flush: true, actions: '<button type="button" class="btn xs" data-act="news-refresh">Read the feeds now</button>' });
}
function newsBodyHtml() {
  return NEWS.cat === 'sources' ? newsSourcesHtml() : newsToolbarHtml() + `<div id="news-results">${newsListHtml()}</div>`;
}
function newsTabsHtml() {
  const total = Object.values(NEWS.counts).reduce((a, b) => a + b, 0);
  return `<div class="tabs">${NEWS_TABS.map(([k, l]) => {
    const n = k === 'all' ? total : NEWS.counts[k];
    return `<a href="/news${k === 'all' ? '' : '/' + k}" data-nav class="${NEWS.cat === k ? 'active' : ''}">${l}${n && k !== 'sources' ? ` <span class="tab-count">${n}</span>` : ''}</a>`;
  }).join('')}</div>`;
}
VIEWS.news = {
  live: false,
  async render(params) {
    NEWS.cat = (params.args && params.args[0]) || 'all';
    setCrumbs([['News', '/news']]);
    try {
      if (NEWS.cat === 'sources') { NEWS.feeds = await get('/api/news/sources'); if (!Object.keys(NEWS.counts).length) await newsLoad().catch(() => null); }
      else await newsLoad();
    } catch (e) { return pageHead('News') + card('', emptyState('News is not available', e.message)); }
    const outlets = Object.keys(NEWS.sources).length;
    return pageHead('News', {
      subtitle: `Headlines from ${outlets || 'major'} outlets' feeds, read every few minutes. Agents can search them, and chats with News turned on.`,
      actions: `<span class="aud-status ok" id="news-status"><i></i>${NEWS.lastRound ? 'Updated ' + esc(ago(NEWS.lastRound)) : 'Reading the feeds…'}</span>`
    }) + newsTabsHtml() + `<div id="news-body">${newsBodyHtml()}</div>`;
  },
  after() { newsAfter(); }
};
function newsRedraw() {
  const body = $('#news-body'); if (!body) return;
  const tabs = $('.tabs'); if (tabs) tabs.outerHTML = newsTabsHtml();
  body.innerHTML = newsBodyHtml();
  newsAfter();
}
function newsAfter() {
  // Pictures that cannot be shown leave no empty box.
  $$('#news-body img[data-pic]').forEach(img => img.addEventListener('error', () => { const a = img.closest('.news-pic'); if (a) a.remove(); }, { once: true }));
  const q = $('#news-q');
  if (q) {
    let t = null;
    q.addEventListener('input', () => { clearTimeout(t); t = setTimeout(async () => { NEWS.q = q.value.trim(); await newsReload(); }, 300); });
  }
  const src = $('#news-source'); if (src) src.addEventListener('change', async () => { NEWS.source = src.value; await newsReload(); });
  const hrs = $('#news-hours'); if (hrs) hrs.addEventListener('change', async () => { NEWS.hours = Number(hrs.value); await newsReload(); });
  clearInterval(NEWS.timer);
  NEWS.timer = setInterval(newsTick, 60000);
}
/** The list again for the current filters (the search box keeps its focus). */
async function newsReload() {
  try { await newsLoad(); } catch (e) { toast(e.message, 'bad'); return; }
  const res = $('#news-results'); if (res) res.innerHTML = newsListHtml();
  const tabs = $('.tabs'); if (tabs) tabs.outerHTML = newsTabsHtml();
  $$('#news-results img[data-pic]').forEach(img => img.addEventListener('error', () => { const a = img.closest('.news-pic'); if (a) a.remove(); }, { once: true }));
}
/** Every minute: how many new articles there are (shown as a button, the list does not jump), and when the feeds were read. */
async function newsTick() {
  if (!$('#news-body')) { clearInterval(NEWS.timer); NEWS.timer = null; return; }
  if (document.hidden || NEWS.cat === 'sources') return;
  let d;
  try { d = await get(newsQuery({ limit: '60' })); } catch { return; }
  const st = $('#news-status'); if (st && d.lastRoundUtc) st.innerHTML = `<i></i>Updated ${esc(ago(d.lastRoundUtc))}`;
  const known = new Set(NEWS.articles.map(a => a.id));
  NEWS.fresh = d.articles.filter(a => !known.has(a.id) && (!NEWS.newest || a.publishedUtc >= NEWS.newest)).length;
  const btn = $('#news-fresh');
  if (btn) { btn.classList.toggle('hidden', !NEWS.fresh); btn.textContent = `${NEWS.fresh} new article${NEWS.fresh === 1 ? '' : 's'} ↑`; }
}
ACTIONS['news-show-fresh'] = async () => { await newsReload(); window.scrollTo({ top: 0, behavior: reducedMotion() ? 'auto' : 'smooth' }); };
ACTIONS['news-more'] = async el => {
  const last = NEWS.articles[NEWS.articles.length - 1]; if (!last) return;
  let d;
  try { d = await get(newsQuery({ before: last.publishedUtc })); } catch (e) { toast(e.message, 'bad'); return; }
  NEWS.articles = NEWS.articles.concat(d.articles.filter(a => !NEWS.articles.some(x => x.id === a.id)));
  NEWS.more = d.more;
  const res = $('#news-results'); if (res) res.innerHTML = newsListHtml();
};
ACTIONS['news-source'] = el => {
  NEWS.source = NEWS.source === el.dataset.source ? '' : el.dataset.source;
  if (NEWS.cat === 'sources') { navigate('/news'); return; }
  newsReload().then(() => { const s = $('#news-source'); if (s) s.value = NEWS.source; });
};
ACTIONS['news-refresh'] = async () => {
  let r;
  try { r = await post('/api/news/refresh', {}); } catch (e) { toast(e.message, 'bad'); return; }
  toast(r.started ? 'Reading every feed now: new articles arrive within a minute.' : 'The feeds were read less than a minute ago.', r.started ? 'ok' : 'bad');
  if (NEWS.cat === 'sources') setTimeout(async () => { try { NEWS.feeds = await get('/api/news/sources'); if ($('#news-body')) newsRedraw(); } catch { /* next time */ } }, 15000);
};
