'use strict';
/* Agentic console - Music: songs from Suno through the server's Suno wrapper. Create: describe a song, or write it
   (title, style, lyrics - Suno can write the lyrics), instrumental, model; each request makes two songs, followed here
   until they are in the library. Library: every song with its cover, style and lyrics, a player bar, star, rename,
   download, reuse; administrators can take songs from the Suno account's own library. Settings (administrators only):
   prices and the wrapper's key. */

const MUS = { info: null, tab: 'create', form: null, job: null, jobSongs: [], jobTimer: null, recent: [], songs: [], q: '', starred: false, more: false,
  playing: null, list: [], suno: null };
const MUS_FORM_KEY = 'music.form';
const MUS_DEFAULTS = { mode: 'simple', prompt: '', title: '', tags: '', lyrics: '', instrumental: false, model: '', idea: '' };
const MUS_STYLES = ['pop', 'rock', 'hip hop', 'lo-fi', 'edm', 'house', 'synthwave', 'jazz', 'blues', 'r&b', 'soul', 'funk', 'country', 'folk', 'acoustic', 'metal',
  'punk', 'indie', 'orchestral', 'cinematic', 'ambient', 'reggae', 'latin', 'k-pop', 'trap', 'drum and bass'];
const MUS_MOODS = ['upbeat', 'melancholic', 'dreamy', 'energetic', 'chill', 'dark', 'romantic', 'epic', 'happy', 'sad', 'aggressive', 'nostalgic'];
const MUS_VOICES = ['male vocals', 'female vocals', 'duet', 'choir', 'whispered vocals', 'rap vocals'];
const MUS_SECTIONS = ['[Intro]', '[Verse]', '[Pre-Chorus]', '[Chorus]', '[Bridge]', '[Outro]', '[Instrumental]'];
const MUS_CLIP_LABELS = { submitted: 'Sent to Suno', queued: 'Waiting at Suno', streaming: 'Being made', complete: 'Done', error: 'Failed', failed: 'Failed', missing: 'Waiting at Suno', '': 'Waiting' };

function musForm() {
  if (!MUS.form) {
    let saved = null;
    try { saved = JSON.parse(LS.get(MUS_FORM_KEY) || 'null'); } catch { /* unreadable */ }
    MUS.form = Object.assign({}, MUS_DEFAULTS, saved || {});
  }
  return MUS.form;
}
function musSaveForm() { LS.set(MUS_FORM_KEY, JSON.stringify(MUS.form)); }
function musLoad() { return get('/api/music').then(d => { MUS.info = d; return d; }); }
const musDur = ms => { const s = Math.round((ms || 0) / 1000); return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`; };

// ------------------------------------------------------------------ prices
const musPrices = () => (MUS.info && MUS.info.pricing) || {};
const musSurcharge = () => 1 + (((MUS.info && MUS.info.account) || {}).surchargePercent || 0) / 100;
const musCharged = () => !!((MUS.info && MUS.info.account) || {}).charged;
function musSongsMicros(n) { return Math.ceil((musPrices().perSong || 0) * 1e6 * n * (musCharged() ? musSurcharge() : 1)); }
function musLyricsMicros() { return Math.ceil((musPrices().perLyrics || 0) * 1e6 * (musCharged() ? musSurcharge() : 1)); }
function musPricingCardHtml(p, edit) {
  const field = (id, label, value, hint) => `<label>${esc(label)}<input type="number" id="${id}" min="0" step="any" value="${value ?? ''}" placeholder="free"${edit ? '' : ' disabled'}>
    <span class="muted small">${esc(hint)}</span></label>`;
  const source = !p.source || p.source === 'config' ? 'Starting prices from the configuration (Music)' : `Set by ${p.source.replace(/^admin:/, '')} ${ago(p.effectiveFromUtc)}`;
  return card('Music pricing', `<p class="muted small">What Suno songs cost User accounts: each song made (a request makes two), and each set of lyrics Suno writes. Administrators are never charged; songs Suno could not make are not charged.</p>
    <div class="form-grid">${field('musprice-song', 'USD per song', p.perSong, 'Each finished song')}${field('musprice-lyrics', 'USD per AI lyrics', p.perLyrics, '"Write lyrics with AI"')}</div>
    <div class="row img-foot">${edit ? '<button class="btn primary" data-act="mus-price-save">Save prices</button>' : ''}<span class="muted small">${esc(source)}</span></div>`, { cls: 'section' });
}
ACTIONS['mus-price-save'] = async () => {
  const v = id => { const x = ($('#' + id) || {}).value; return x === '' || x === undefined ? null : Number(x); };
  let p;
  try { p = await put('/api/music/pricing', { perSong: v('musprice-song'), perLyrics: v('musprice-lyrics') }); } catch (e) { toast(e.message, 'bad'); return; }
  if (MUS.info) MUS.info.pricing = p;
  toast('Music prices saved', 'ok');
  if ($('#mus-body')) musRender(); else if (typeof refreshNow === 'function') refreshNow();
};

// ------------------------------------------------------------------ page
const MUS_TABS = [['create', 'Create'], ['library', 'Library'], ['settings', 'Settings']];
/** The tabs this account sees: Settings is for administrators. */
const musTabs = info => MUS_TABS.filter(([k]) => k !== 'settings' || info.isAdmin);
function musStatusHtml(info) {
  if (!info.available) return '<span class="aud-status bad"><i></i>No Suno wrapper</span>';
  if (!info.ready) return `<span class="aud-status bad" data-tip="${esc(info.problem || '')}"><i></i>Not working</span>`;
  const c = info.credits && info.credits.available_credits;
  return `<span class="aud-status ok"><i></i>Ready${c !== undefined && c !== null ? ` · ${num(c)} Suno credits` : ''}</span>`;
}
function musBodyHtml() {
  const info = MUS.info;
  if (!info.ready && MUS.tab !== 'settings') {
    return `<div class="notice warn aud-notice">${esc(info.problem || 'Suno is not working.')}${info.isAdmin ? ' <a href="/music/settings" data-nav>Open Settings</a>' : ''}</div>` +
      (MUS.tab === 'library' ? musLibraryHtml() : '');
  }
  return MUS.tab === 'library' ? musLibraryHtml() : MUS.tab === 'settings' ? musSettingsHtml() : musCreateHtml();
}
function musRender() {
  const body = $('#mus-body'); if (!body) return;
  const head = $('.page-head .actions'); if (head) head.innerHTML = musStatusHtml(MUS.info);
  body.innerHTML = musBodyHtml(); applyPermissions(body); musAfter();
}
VIEWS.music = {
  live: false,
  async render(params) {
    MUS.tab = (params.args && params.args[0]) || 'create';
    setCrumbs([['Music', '/music']]);
    let info;
    try { info = await musLoad(); } catch (e) { return pageHead('Music') + card('', emptyState('Music is not available', e.message)); }
    if (!info.available) {
      return pageHead('Music', { actions: musStatusHtml(info) }) + card('', emptyState('No Suno wrapper is set up', info.isAdmin
        ? 'Set Music.BaseUrl (e.g. http://127.0.0.1:8000, the SunoAPI task on the server) in the configuration and restart.'
        : 'An administrator sets up music.'));
    }
    if (!musTabs(info).some(([k]) => k === MUS.tab)) MUS.tab = 'create';
    if (MUS.tab === 'create') {
      MUS.recent = await get('/api/music/jobs?limit=10').catch(() => []);
      const live = MUS.recent.find(j => j.status === 'making' || j.status === 'saving');
      if (live) MUS.job = live; else if (MUS.job && !MUS.recent.some(j => j.id === MUS.job.id)) MUS.job = null;
      if (MUS.job) MUS.jobSongs = await get('/api/music/songs?job=' + enc(MUS.job.id)).catch(() => []);
    }
    if (MUS.tab === 'library') await musLoadSongs();
    return pageHead('Music', {
      subtitle: 'Songs from Suno: describe one, or write its lyrics, style and title. Each request makes two songs, kept in your library.',
      actions: musStatusHtml(info)
    }) + `<div class="tabs">${musTabs(info).map(([k, l]) => `<a href="/music${k === 'create' ? '' : '/' + k}" data-nav class="${MUS.tab === k ? 'active' : ''}">${l}</a>`).join('')}</div>
      <div id="mus-body">${musBodyHtml()}</div>${musPlayerHtml()}`;
  },
  after() { musAfter(); }
};
function musAfter() {
  if (MUS.tab === 'create') musCreateAfter();
  if (MUS.tab === 'library') musLibraryAfter();
  musPlayerAfter();
  if (MUS.job && (MUS.job.status === 'making' || MUS.job.status === 'saving')) musFollow();
}

// ------------------------------------------------------------------ Create
function musCreateHtml() {
  const f = musForm(), info = MUS.info, custom = f.mode === 'custom';
  const models = info.models || [];
  const chips = (list, cls) => list.map(t => `<button type="button" class="mus-chip ${cls}" data-act="mus-tag" data-tag="${esc(t)}">${esc(t)}</button>`).join('');
  const lyricsCost = musLyricsMicros();
  return `<div class="img-create mus-create">
    <div class="img-form">
      ${card('Song', `<div class="seg mus-mode" role="group" aria-label="How to make it">
          <button type="button" class="seg-btn${custom ? '' : ' on'}" data-act="mus-mode" data-m="simple" data-tip="Say what the song should be; Suno writes the lyrics and picks the style">Describe it</button>
          <button type="button" class="seg-btn${custom ? ' on' : ''}" data-act="mus-mode" data-m="custom" data-tip="Your own lyrics, style and title">Write it</button></div>
        ${custom ? `
          <label>Title<input data-mf="title" maxlength="200" value="${esc(f.title)}" placeholder="Neon Ghosts"></label>
          <label>Style <span class="muted small">genres, moods, instruments, voice: Suno's "style of music"</span>
            <textarea data-mf="tags" rows="2" maxlength="1000" placeholder="synthwave, dreamy, female vocals, 90 bpm">${esc(f.tags)}</textarea></label>
          <div class="mus-chips"><span class="muted small">Genre</span>${chips(MUS_STYLES, 'g')}</div>
          <div class="mus-chips"><span class="muted small">Mood</span>${chips(MUS_MOODS, 'm')}</div>
          <div class="mus-chips"><span class="muted small">Voice</span>${chips(MUS_VOICES, 'v')}</div>
          <label class="check mus-inst"><input type="checkbox" data-mf="instrumental"${f.instrumental ? ' checked' : ''}><span><b>Instrumental</b><span class="muted small">No vocals: the lyrics are left out</span></span></label>
          ${f.instrumental ? '' : `<label>Lyrics <span class="muted small" id="mus-lyrics-count">${num(f.lyrics.length)} / 6,000</span>
            <textarea id="mus-lyrics" data-mf="lyrics" rows="14" maxlength="6000" placeholder="[Verse]&#10;Streetlights bleeding gold on the wet&#10;…&#10;&#10;[Chorus]&#10;…">${esc(f.lyrics)}</textarea></label>
          <div class="mus-chips"><span class="muted small">Insert</span>${MUS_SECTIONS.map(s => `<button type="button" class="mus-chip" data-act="mus-section" data-s="${esc(s)}">${esc(s)}</button>`).join('')}</div>
          <div class="mus-ai"><label>Write lyrics with AI <span class="muted small">Suno writes them from a description${lyricsCost && musCharged() ? ` · ${money(lyricsCost)}` : ''}</span>
            <span class="img-num-row"><input id="mus-idea" data-mf="idea" value="${esc(f.idea)}" placeholder="a dreamy lo-fi song about city rain at midnight, warm piano"><button type="button" class="btn" data-act="mus-write-lyrics" id="mus-write">Write</button></span></label></div>`}`
        : `<label>Describe the song <span class="muted small">genre, mood, what it is about: Suno writes the lyrics</span>
            <textarea id="mus-prompt" data-mf="prompt" rows="5" maxlength="3000" placeholder="A dreamy lo-fi track about city rain at midnight, warm piano, soft drums">${esc(f.prompt)}</textarea></label>
          <label class="check mus-inst"><input type="checkbox" data-mf="instrumental"${f.instrumental ? ' checked' : ''}><span><b>Instrumental</b><span class="muted small">No vocals</span></span></label>`}
        <label class="mus-model">Model <code>model</code><input list="mus-models" data-mf="model" value="${esc(f.model)}" placeholder="${esc(info.defaultModel || 'chirp-goose')}">
          <span class="muted small">${esc(models.map(m => `${m.id}: ${m.name}`).join(' · '))}</span></label>
        <datalist id="mus-models">${models.map(m => `<option value="${esc(m.id)}">${esc(m.name)}</option>`).join('')}</datalist>`)}
    </div>
    <aside class="img-side">
      <div class="card img-go-card"><div class="card-body">
        <button type="button" class="btn primary img-go" data-act="mus-generate" id="mus-go">Create 2 songs</button>
        <div class="muted small">${musSongsMicros(2) ? `${musCharged() ? 'Costs' : 'Users pay'} ${money(musSongsMicros(2))} (two songs) · ` : ''}Suno usually needs one to three minutes · Ctrl+Enter</div>
      </div></div>
      <div id="mus-job">${musJobHtml()}</div>
      ${MUS.recent.length ? card('Recent requests', MUS.recent.map(j => `<button type="button" class="img-recent" data-act="mus-job-open" data-id="${esc(j.id)}">
          <span class="img-recent-st ${j.status === 'making' || j.status === 'saving' ? 'running' : esc(j.status)}"></span><span class="grow">${esc(musJobTitle(j))}</span><span class="muted small">${esc(ago(j.createdUtc))}</span></button>`).join(''), { flush: true }) : ''}
    </aside>
  </div>`;
}
function musJobTitle(j) { const r = j.request || {}; return r.title || (j.clips || []).map(c => c.title).find(Boolean) || r.prompt || r.tags || 'Song'; }
function musRedrawCreate() { const body = $('#mus-body'); if (!body || MUS.tab !== 'create') return; const y = window.scrollY; body.innerHTML = musCreateHtml(); applyPermissions(body); musCreateAfter(); window.scrollTo(0, y); }
function musCreateAfter() {
  const f = musForm();
  $$('#mus-body [data-mf]').forEach(el => el.addEventListener(el.type === 'checkbox' ? 'change' : 'input', () => {
    f[el.dataset.mf] = el.type === 'checkbox' ? el.checked : el.value;
    musSaveForm();
    if (el.dataset.mf === 'instrumental') musRedrawCreate();
    if (el.dataset.mf === 'lyrics') { const c = $('#mus-lyrics-count'); if (c) c.textContent = `${num(el.value.length)} / 6,000`; }
  }));
  $$('#mus-body textarea, #mus-body input:not([type=checkbox])').forEach(el => el.addEventListener('keydown', ev => {
    if ((ev.ctrlKey || ev.metaKey) && ev.key === 'Enter') { ev.preventDefault(); ACTIONS['mus-generate'](); }
  }));
}
ACTIONS['mus-mode'] = el => { musForm().mode = el.dataset.m; musSaveForm(); musRedrawCreate(); };
ACTIONS['mus-tag'] = el => {
  const f = musForm(), t = el.dataset.tag, parts = f.tags.split(',').map(x => x.trim()).filter(Boolean);
  const i = parts.findIndex(p => p.toLowerCase() === t.toLowerCase());
  if (i >= 0) parts.splice(i, 1); else parts.push(t);
  f.tags = parts.join(', '); musSaveForm();
  const box = $('#mus-body [data-mf=tags]'); if (box) box.value = f.tags;
};
ACTIONS['mus-section'] = el => {
  const box = $('#mus-lyrics'); if (!box) return;
  const at = box.selectionStart ?? box.value.length, before = box.value.slice(0, at), after = box.value.slice(at);
  const ins = (before && !before.endsWith('\n\n') ? (before.endsWith('\n') ? '\n' : '\n\n') : '') + el.dataset.s + '\n';
  box.value = before + ins + after; box.focus(); box.setSelectionRange(at + ins.length, at + ins.length);
  box.dispatchEvent(new Event('input'));
};
ACTIONS['mus-write-lyrics'] = async () => {
  const f = musForm(), idea = (f.idea || '').trim();
  if (idea.length < 3) { toast('Say what the song is about.', 'bad'); $('#mus-idea').focus(); return; }
  if (f.lyrics.trim() && !await confirmBox('Replace the lyrics?', 'Suno writes new lyrics in place of the ones there.', { confirm: 'Write new lyrics' })) return;
  const btn = $('#mus-write'); if (btn) { btn.disabled = true; btn.textContent = 'Writing…'; }
  try {
    const r = await post('/api/music/lyrics', { instruction: idea });
    f.lyrics = r.lyrics; if (!f.title) f.title = ''; musSaveForm(); musRedrawCreate();
    toast('Lyrics written' + (r.costMicros ? ' · ' + money(r.costMicros) : ''), 'ok');
  } catch (e) { toast(e.message, 'bad'); }
  finally { const b = $('#mus-write'); if (b) { b.disabled = false; b.textContent = 'Write'; } }
};
ACTIONS['mus-generate'] = async () => {
  const f = musForm(), custom = f.mode === 'custom';
  const body = custom ? { title: f.title, tags: f.tags, lyrics: f.instrumental ? '' : f.lyrics, instrumental: f.instrumental, model: f.model || undefined, prompt: '' }
    : { prompt: f.prompt, instrumental: f.instrumental, model: f.model || undefined };
  if (custom && !body.lyrics.trim() && !body.tags.trim()) { toast(f.instrumental ? 'Give the instrumental a style.' : 'Write the lyrics (or have Suno write them), or describe a style.', 'bad'); return; }
  if (!custom && !body.prompt.trim()) { toast('Describe the song.', 'bad'); const p = $('#mus-prompt'); if (p) p.focus(); return; }
  const go = $('#mus-go'); if (go) go.disabled = true;
  try {
    MUS.job = await post('/api/music/jobs', body);
    MUS.jobSongs = [];
    MUS.recent = [MUS.job, ...MUS.recent.filter(j => j.id !== MUS.job.id)].slice(0, 10);
    musRenderJob(); musFollow();
  } catch (e) { toast(e.message, 'bad'); }
  finally { const g = $('#mus-go'); if (g) g.disabled = false; }
};

// ------------------------------------------------------------------ the request: Suno's progress, then the songs
function musJobHtml() {
  const j = MUS.job;
  if (!j) return '';
  const live = j.status === 'making' || j.status === 'saving';
  const label = { making: 'Suno is making the songs', saving: 'Saving the songs', done: 'Done', failed: 'Failed' }[j.status] || j.status;
  const since = Math.round((Date.now() - new Date(j.createdUtc)) / 1000);
  return `<div class="card img-job mus-job ${live ? 'running' : esc(j.status)}"><div class="card-body">
    <div class="img-job-head"><b>${esc(label)}</b><span class="muted small">${live ? `${Math.floor(since / 60)}:${String(since % 60).padStart(2, '0')}` : ''}${j.costMicros ? ' · cost ' + money(j.costMicros) : ''}</span></div>
    <div class="muted small img-job-prompt">${esc(musJobTitle(j))}</div>
    ${live ? '<div class="img-progress"><i class="mus-indeterminate"></i></div>' : ''}
    ${MUS.jobSongs.length ? '' : (j.clips || []).map((c, i) => `<div class="mus-clip ${esc(c.status || '')}"><span class="img-recent-st ${c.status === 'complete' ? 'done' : c.status === 'error' || c.status === 'failed' ? 'failed' : live ? 'running' : ''}"></span>
      <span class="grow">${esc(c.title || 'Song ' + (i + 1))}</span><span class="muted small">${esc(MUS_CLIP_LABELS[c.status || ''] || c.status)}</span></div>`).join('')}
    ${j.error ? `<div class="notice ${j.status === 'done' ? 'warn' : 'bad'} small">${esc(j.error)}</div>` : ''}
    ${MUS.jobSongs.length ? `<div class="mus-list">${MUS.jobSongs.map(s => musSongHtml(s, 'job')).join('')}</div>` : ''}</div></div>`;
}
function musRenderJob() { const box = $('#mus-job'); if (box) box.innerHTML = musJobHtml(); }
function musStopFollow() { clearInterval(MUS.jobTimer); MUS.jobTimer = null; }
function musFollow() {
  musStopFollow();
  const id = MUS.job && MUS.job.id; if (!id) return;
  MUS.jobTimer = setInterval(async () => {
    if (!$('#mus-job') || !MUS.job || MUS.job.id !== id) { musStopFollow(); return; }
    let j;
    try { j = await get('/api/music/jobs/' + enc(id)); } catch { return; }
    MUS.job = j;
    if (j.status === 'done' || j.status === 'failed') {
      musStopFollow();
      MUS.jobSongs = j.status === 'done' ? await get('/api/music/songs?job=' + enc(id)).catch(() => []) : [];
      const r = MUS.recent.findIndex(x => x.id === id); if (r >= 0) MUS.recent[r] = j;
      if (j.status === 'done' && !IN_PANE) playSound('chatComplete');
      if (j.status === 'failed') toast('The songs could not be made: ' + (j.error || 'Suno failed'), 'bad');
    }
    musRenderJob();
  }, 3000);
}
ACTIONS['mus-job-open'] = async el => {
  try { MUS.job = await get('/api/music/jobs/' + enc(el.dataset.id)); } catch (e) { toast(e.message, 'bad'); return; }
  MUS.jobSongs = MUS.job.status === 'done' ? await get('/api/music/songs?job=' + enc(MUS.job.id)).catch(() => []) : [];
  musRenderJob();
  if (MUS.job.status === 'making' || MUS.job.status === 'saving') musFollow();
};

// ------------------------------------------------------------------ songs and the player bar
function musCoverHtml(s, cls = '') {
  return s.hasCover ? `<img class="mus-cover ${cls}" src="/api/music/songs/${enc(s.id)}/cover" alt="" loading="lazy">`
    : `<span class="mus-cover none ${cls}" aria-hidden="true">${esc((s.title || '♪').trim().charAt(0).toUpperCase())}</span>`;
}
function musSongHtml(s, list) {
  const on = MUS.playing === s.id;
  return `<div class="mus-song${on ? ' playing' : ''}" data-id="${esc(s.id)}">
    <button type="button" class="mus-play" data-act="mus-play" data-id="${esc(s.id)}" data-list="${list}" aria-label="${on ? 'Pause' : 'Play'} ${esc(s.title || '')}">${musCoverHtml(s)}<span class="mus-play-ic">${on ? '❚❚' : '▶'}</span></button>
    <div class="mus-song-main"><div class="mus-song-title">${esc(s.title || 'Untitled')}${s.starred ? ' <span class="mus-star">★</span>' : ''}</div>
      <div class="muted small">${esc(musDur(s.durationMs))}${s.tags ? ' · ' + esc(s.tags) : ''}${s.instrumental ? ' · instrumental' : ''}</div>
      ${list === 'library' ? `<div class="muted small">${esc(ago(s.createdUtc))}${s.model ? ' · ' + esc(s.model) : ''}</div>` : ''}</div>
    <div class="mus-song-acts">
      <button type="button" class="btn xs ghost${s.starred ? ' on' : ''}" data-act="mus-star" data-id="${esc(s.id)}" aria-label="${s.starred ? 'Unstar' : 'Star'}" data-tip="${s.starred ? 'Unstar' : 'Star: always kept'}">${s.starred ? '★' : '☆'}</button>
      <a class="btn xs ghost" href="${esc(musAudioUrl(s))}&download=1" download data-act="mus-download" data-id="${esc(s.id)}" data-tip="Download (mp3)${s.hasAudio ? '' : ': its first download uses one of the Suno account\'s downloads'}">${icon('download')}</a>
      <button type="button" class="btn xs ghost" data-act="mus-details" data-id="${esc(s.id)}" data-tip="Lyrics and details">⋯</button></div></div>`;
}
function musPlayerHtml() {
  return `<div class="mus-player hidden" id="mus-player"><span id="mus-now"></span><audio id="mus-audio" controls preload="auto"></audio>
    <button type="button" class="btn xs ghost" data-act="mus-next" data-d="-1" aria-label="Previous">⏮</button><button type="button" class="btn xs ghost" data-act="mus-next" data-d="1" aria-label="Next">⏭</button>
    <button type="button" class="goal-x" data-act="mus-close" aria-label="Close the player">×</button></div>`;
}
function musPlayerAfter() {
  const a = $('#mus-audio'); if (!a || a.dataset.bound) return;
  a.dataset.bound = '1';
  a.addEventListener('ended', () => ACTIONS['mus-next']({ dataset: { d: '1' } }));
  a.addEventListener('play', () => musMarkPlaying());
  a.addEventListener('pause', () => musMarkPlaying());
}
function musFind(id) { return MUS.songs.find(s => s.id === id) || MUS.jobSongs.find(s => s.id === id); }
function musMarkPlaying() {
  const a = $('#mus-audio'), paused = !a || a.paused;
  $$('.mus-song').forEach(n => {
    const on = n.dataset.id === MUS.playing;
    n.classList.toggle('playing', on && !paused);
    const ic = $('.mus-play-ic', n); if (ic) ic.textContent = on && !paused ? '❚❚' : '▶';
  });
}
/** The song's audio address: its size names the version, so a browser never plays an older copy it kept. */
function musAudioUrl(s) { return `/api/music/songs/${enc(s.id)}/audio?v=${s.bytes || 0}`; }
/**
 * A song's audio comes from Suno the first time it is played (Suno scrambles its stream; an official download uses one of
 * the Suno account's downloads); after that it is kept here. False when Suno refused it.
 */
async function musEnsureAudio(s) {
  if (s.hasAudio) return true;
  toast('Getting the song from Suno…', 'ok');
  let r;
  try { r = await post('/api/music/songs/' + enc(s.id) + '/fetch', {}); } catch (e) { toast(e.message, 'bad'); return false; }
  for (const list of [MUS.songs, MUS.jobSongs]) { const x = list.find(y => y.id === s.id); if (x) Object.assign(x, r); }
  Object.assign(s, r);
  musRedrawLists();
  return true;
}
async function musPlay(s, list) {
  const bar = $('#mus-player'), a = $('#mus-audio');
  if (!bar || !a) return;
  MUS.playing = s.id; MUS.list = list;
  $('#mus-now').innerHTML = `${musCoverHtml(s, 'small')}<span><b>${esc(s.title || 'Untitled')}</b><span class="muted small">${esc(s.tags || '')}</span></span>`;
  bar.classList.remove('hidden');
  if (!await musEnsureAudio(s)) { if (MUS.playing === s.id) ACTIONS['mus-close'](); return; }
  if (MUS.playing !== s.id) return;
  a.src = musAudioUrl(s);
  a.play().catch(() => { });
}
ACTIONS['mus-download'] = async el => {
  const s = musFind(el.dataset.id); if (!s) return;
  // Not here yet: it comes from Suno first, then the download starts.
  if (!await musEnsureAudio(s)) return;
  const link = document.createElement('a');
  link.href = musAudioUrl(s) + '&download=1'; link.download = ''; document.body.appendChild(link); link.click(); link.remove();
};
ACTIONS['mus-play'] = el => {
  const s = musFind(el.dataset.id), a = $('#mus-audio'); if (!s) return;
  if (MUS.playing === s.id && a && a.src) { if (a.paused) a.play().catch(() => { }); else a.pause(); return; }
  musPlay(s, el.dataset.list === 'job' ? MUS.jobSongs : MUS.songs);
};
ACTIONS['mus-next'] = el => {
  const list = MUS.list || [], i = list.findIndex(s => s.id === MUS.playing), n = list[i + Number(el.dataset.d)];
  if (n) musPlay(n, list); else musMarkPlaying();
};
ACTIONS['mus-close'] = () => { const a = $('#mus-audio'); if (a) { a.pause(); a.removeAttribute('src'); a.load(); } MUS.playing = null; $('#mus-player').classList.add('hidden'); musMarkPlaying(); };
ACTIONS['mus-star'] = async el => {
  const s = musFind(el.dataset.id); if (!s) return;
  let r;
  try { r = await patch('/api/music/songs/' + enc(s.id), { starred: !s.starred }); } catch (e) { toast(e.message, 'bad'); return; }
  s.starred = r.starred; musRedrawLists();
};
function musRedrawLists() {
  const lib = $('#mus-songs'); if (lib) lib.innerHTML = musSongsListHtml();
  musRenderJob(); musMarkPlaying();
}
ACTIONS['mus-details'] = el => {
  const s = musFind(el.dataset.id); if (!s) return;
  openModal(s.title || 'Song', `<div class="mus-details">
      <div class="mus-details-head">${musCoverHtml(s, 'big')}<div class="stack">
        <label>Title<input id="mus-rename" value="${esc(s.title || '')}" maxlength="200"></label>
        <dl class="aud-meta"><dt>Length</dt><dd>${esc(musDur(s.durationMs))}</dd>${s.tags ? `<dt>Style</dt><dd>${esc(s.tags)}</dd>` : ''}${s.description ? `<dt>Asked for</dt><dd>${esc(s.description)}</dd>` : ''}
          <dt>Model</dt><dd>${esc(s.model || '—')}</dd><dt>Made</dt><dd>${esc(new Date(s.createdUtc).toLocaleString())}</dd><dt>Suno id</dt><dd><code>${esc(s.clipId)}</code></dd></dl></div></div>
      ${s.lyrics && !s.instrumental ? `<div><div class="menu-title">Lyrics</div><pre class="mus-lyrics">${esc(s.lyrics)}</pre></div>` : ''}</div>`,
    `<button class="btn ghost danger" data-act="mus-delete" data-id="${esc(s.id)}">Delete</button><button class="btn" data-act="mus-reuse" data-id="${esc(s.id)}" data-tip="Its title, style and lyrics into the form">Use again</button>
     ${s.lyrics ? `<button class="btn" data-act="mus-copy-lyrics" data-id="${esc(s.id)}">Copy lyrics</button>` : ''}<button class="btn primary" data-act="mus-rename-save" data-id="${esc(s.id)}">Save</button>`, 'wide');
};
ACTIONS['mus-rename-save'] = async el => {
  const s = musFind(el.dataset.id), title = ($('#mus-rename').value || '').trim(); if (!s) return;
  if (title && title !== s.title) {
    try { const r = await patch('/api/music/songs/' + enc(s.id), { title }); s.title = r.title; } catch (e) { toast(e.message, 'bad'); return; }
  }
  closeModal(); musRedrawLists();
};
ACTIONS['mus-copy-lyrics'] = el => { const s = musFind(el.dataset.id); if (s) copyText(s.lyrics, 'Lyrics copied'); };
ACTIONS['mus-reuse'] = el => {
  const s = musFind(el.dataset.id); if (!s) return;
  const f = musForm();
  Object.assign(f, { mode: 'custom', title: s.title || '', tags: s.tags || '', lyrics: s.instrumental ? '' : (s.lyrics || ''), instrumental: !!s.instrumental });
  musSaveForm(); closeModal();
  if (MUS.tab !== 'create') navigate('/music'); else musRedrawCreate();
};
ACTIONS['mus-delete'] = async el => {
  const s = musFind(el.dataset.id); if (!s) return;
  if (!await confirmBox(`Delete “${s.title || 'this song'}”?`, 'It is deleted from your library (Suno keeps its own copy).', { confirm: 'Delete', danger: true })) return;
  try { await del('/api/music/songs/' + enc(s.id)); } catch (e) { toast(e.message, 'bad'); return; }
  if (MUS.playing === s.id) ACTIONS['mus-close']();
  MUS.songs = MUS.songs.filter(x => x.id !== s.id); MUS.jobSongs = MUS.jobSongs.filter(x => x.id !== s.id);
  musRedrawLists(); toast('Deleted', 'ok');
};

// ------------------------------------------------------------------ Library
async function musLoadSongs(append) {
  const before = append && MUS.songs.length ? '&before=' + enc(MUS.songs[MUS.songs.length - 1].createdUtc) : '';
  const list = await get(`/api/music/songs?limit=60${MUS.q ? '&q=' + enc(MUS.q) : ''}${MUS.starred ? '&starred=1' : ''}${before}`).catch(() => []);
  MUS.songs = append ? MUS.songs.concat(list) : list;
  MUS.more = list.length === 60;
}
function musSongsListHtml() {
  return MUS.songs.length ? MUS.songs.map(s => musSongHtml(s, 'library')).join('') : `<p class="muted">${MUS.q || MUS.starred ? 'Nothing matches.' : 'Songs you make show here.'}</p>`;
}
function musLibraryHtml() {
  const info = MUS.info;
  return `<div class="row img-gal-bar"><input id="mus-q" placeholder="Search title, style or lyrics" value="${esc(MUS.q)}" aria-label="Search songs" autocomplete="off">
      <label class="check aud-inline"><input type="checkbox" id="mus-starred"${MUS.starred ? ' checked' : ''}><span>Starred only</span></label><span class="spacer"></span>
      <span class="muted small">Starred songs are kept; the library keeps the newest ${num((info.defaults || {}).maxSongs || 300)}.</span>
      ${MUS.songs.length ? '<button type="button" class="btn xs ghost" data-act="mus-clear">Delete unstarred</button>' : ''}</div>
    <div class="mus-list" id="mus-songs">${musSongsListHtml()}</div>
    ${MUS.more ? '<div class="row img-more"><button type="button" class="btn" data-act="mus-more">Load more</button></div>' : ''}
    ${info.isAdmin && info.ready ? card('From the Suno account', MUS.suno ? musSunoHtml() : '<button type="button" class="btn" data-act="mus-suno">Show the Suno library</button>',
      { cls: 'section', hint: 'Songs made on suno.com (or before): take them into your library' }) : ''}`;
}
function musLibraryAfter() {
  const q = $('#mus-q'); let t;
  if (q) q.addEventListener('input', () => { clearTimeout(t); t = setTimeout(async () => { MUS.q = q.value.trim(); await musLoadSongs(); musRedrawLists(); }, 300); });
  const st = $('#mus-starred'); if (st) st.addEventListener('change', async () => { MUS.starred = st.checked; await musLoadSongs(); musRedrawLists(); });
}
ACTIONS['mus-more'] = async () => { await musLoadSongs(true); musRender(); };
ACTIONS['mus-clear'] = async () => {
  if (!await confirmBox('Delete every unstarred song?', 'Starred songs stay. Suno keeps its own copies.', { confirm: 'Delete', danger: true })) return;
  try { await del('/api/music/songs'); } catch (e) { toast(e.message, 'bad'); return; }
  await musLoadSongs(); musRender();
};
function musSunoHtml() {
  const list = MUS.suno || [];
  return list.length ? `<div class="mus-suno">${list.map(c => `<div class="mus-suno-row"><span class="grow"><b>${esc(c.title || 'Untitled')}</b>
      <span class="muted small">${esc(c.status || '')}${c.duration ? ' · ' + esc(musDur(c.duration * 1000)) : ''}${c.tags ? ' · ' + esc(c.tags) : ''}</span></span>
      ${c.inLibrary ? '<span class="voice-tag">in your library</span>' : c.status === 'complete' ? `<button type="button" class="btn xs" data-act="mus-import" data-id="${esc(c.id)}">Add</button>` : ''}</div>`).join('')}</div>
    <div class="row img-foot"><button type="button" class="btn xs ghost" data-act="mus-suno">Refresh</button></div>` : '<p class="muted small">The Suno library is empty.</p>';
}
ACTIONS['mus-suno'] = async () => {
  try { MUS.suno = await get('/api/music/suno?limit=40'); } catch (e) { toast(e.message, 'bad'); return; }
  musRender();
};
ACTIONS['mus-import'] = async el => {
  let r;
  try { r = await post('/api/music/import', { clipIds: [el.dataset.id] }); } catch (e) { toast(e.message, 'bad'); return; }
  if (r.added.length) { toast(`Added: ${r.added[0].title}`, 'ok'); const c = (MUS.suno || []).find(x => x.id === el.dataset.id); if (c) c.inLibrary = true; await musLoadSongs(); musRender(); }
  else toast('It could not be added (not finished, or its audio is not available).', 'bad');
};

// ------------------------------------------------------------------ Settings
function musSettingsHtml() {
  const info = MUS.info, c = info.credits || {}, d = info.defaults || {};
  const keyState = info.keyInConfig ? 'set in the configuration file (Music.ApiKey), which wins over the vault'
    : info.keySet ? `kept in the vault as <code>${esc(info.keySecret || '')}</code> (no agent may use it)` : '<b class="aud-warn">not set</b> (needed when the wrapper has an api_key.txt)';
  const server = card('Suno', `<dl class="aud-meta">
      <dt>Wrapper</dt><dd>${info.isAdmin ? `<code>${esc(info.baseUrl || '')}</code> <span class="muted small">(Music.BaseUrl; the SunoAPI scheduled task runs it at boot)</span>` : 'set up by an administrator'}</dd>
      <dt>Status</dt><dd>${info.ready ? '<span class="aud-status ok"><i></i>Working</span>' : `<span class="aud-status bad"><i></i>${esc(info.problem || 'Not working')}</span>`}</dd>
      ${info.isAdmin && info.credits ? `<dt>Suno credits</dt><dd>${num(c.available_credits)} available${c.monthly_limit ? ` · ${num(c.monthly_usage)} of ${num(c.monthly_limit)} used this month` : ''}</dd>` : ''}
      ${info.isAdmin && info.credits && c.downloads_left !== undefined && c.downloads_left !== null ? `<dt>Suno downloads</dt><dd>${num(c.downloads_left)} left <span class="muted small">· a song is downloaded from Suno the first time it is played (Suno scrambles its stream), then kept here; more downloads come with a paid Suno plan or download packs on suno.com</span></dd>` : ''}
      ${info.isAdmin ? `<dt>API key</dt><dd>${keyState}</dd>` : ''}
      <dt>Limits</dt><dd>${num(d.maxActiveJobs)} requests at once per person · a request may take ${dur((d.jobTimeoutSeconds || 0) * 1000)} · the library keeps the newest ${num(d.maxSongs)} (starred ones always)</dd></dl>
    ${info.isAdmin && !info.keyInConfig ? `<div class="row aud-key"><input type="password" id="mus-key" placeholder="${info.keySet ? 'Replace the API key' : 'The key from api_key.txt next to server.py'}" autocomplete="new-password" aria-label="API key">
      <button class="btn primary" data-act="mus-key-save">Save key</button>${info.keySet ? '<button class="btn ghost danger" data-act="mus-key-remove">Remove</button>' : ''}</div>` : ''}
    ${info.isAdmin ? '<p class="muted small">If Suno\'s sign-in stops working, run <code>python login.py</code> in the suno folder on the server and sign in again.</p>' : ''}
    <div class="row"><button class="btn" data-act="mus-refresh">Check the connection</button></div>`);
  return `<div class="stack">${musPricingCardHtml(info.pricing || {}, !!info.canSetPrices)}${server}</div>`;
}
async function musSetKey(apiKey) {
  let r;
  try { r = await put('/api/music/key', { apiKey }); } catch (e) { toast(e.message, 'bad'); return; }
  if (!apiKey) toast('API key removed', 'ok');
  else if (r.ok) toast('API key saved: Suno answers', 'ok');
  else toast('API key saved, but: ' + (r.problem || 'the wrapper does not answer'), 'bad');
  await musLoad(); musRender();
}
ACTIONS['mus-key-save'] = () => { const k = ($('#mus-key').value || '').trim(); if (!k) { toast('Paste the API key first.', 'bad'); return; } musSetKey(k); };
ACTIONS['mus-key-remove'] = async () => { if (await confirmBox('Remove the API key?', 'The wrapper refuses requests without it.', { confirm: 'Remove', danger: true })) musSetKey(''); };
ACTIONS['mus-refresh'] = async () => {
  try { await musLoad(); } catch (e) { toast(e.message, 'bad'); return; }
  toast(MUS.info.ready ? 'Suno works' : MUS.info.problem || 'Not working', MUS.info.ready ? 'ok' : 'bad'); musRender();
};
