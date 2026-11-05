'use strict';
/* Agentic console - Audio: speech from the XTTS server with everything it offers. Speak: any text in any voice and
   language with every setting (speed, temperature, top_k, top_p, repetition and length penalty, sentence splitting,
   stream chunk size), streamed (PCM or WAV) or as a whole file, kept as clips to play again, download (WAV or raw PCM) or
   reuse. Voices: the built-in speakers and cloned voices - clone from files or a recording with every cloning setting,
   see a voice's clips and expiry, renew it before the server deletes it (30 days), delete it. Personalities: how each
   voice sounds when it speaks the TL;DR of an answer in Dictation mode (its prompt, the model that writes it, its speech
   settings), tried out on a sample. Dictation: your Dictation mode settings. Settings (administrators only): prices, the
   server and its API key. */

const AUD = { tab: 'speak', clips: [], q: '', sel: null, form: null, result: null, blobUrl: null, ctrl: null, staged: [], rec: null, models: null, pvoice: null, pq: '', bq: '' };
const AUD_FORM_KEY = 'audio.form';
const AUD_DEFAULTS = { speed: 1, temperature: 0.75, top_k: 50, top_p: 0.85, repetition_penalty: 5, length_penalty: 1, enable_text_splitting: true, stream_chunk_size: 20 };
const AUD_FIELDS = [
  { k: 'speed', label: 'Speed', min: 0.1, max: 3, step: 0.05, hint: '0.8 is slower, 1.2 faster' },
  { k: 'temperature', label: 'Temperature', min: 0.05, max: 2, step: 0.05, hint: '0.5–0.65 steadier and more consistent · 0.85–1.0 more expressive, more likely to glitch' },
  { k: 'top_k', label: 'Top K', min: 1, max: 1000, step: 1, hint: 'How many likely sounds it picks from' },
  { k: 'top_p', label: 'Top P', min: 0.05, max: 1, step: 0.01, hint: 'Picks from the likeliest sounds that add up to this share' },
  { k: 'repetition_penalty', label: 'Repetition penalty', min: 1, max: 50, step: 0.5, hint: 'Raise to 7–10 if it stutters or repeats sounds' },
  { k: 'length_penalty', label: 'Length penalty', min: -10, max: 10, step: 0.1, hint: 'Above 1 favours longer, below 1 shorter output' },
  { k: 'stream_chunk_size', label: 'Stream chunk size', min: 5, max: 200, step: 1, stream: true, hint: 'Streaming only: lower means the first sound comes sooner, in smaller pieces' }
];
const AUD_PRESETS = [
  ['Default', AUD_DEFAULTS, 'The server\'s own defaults'],
  ['Steady', { temperature: 0.6, repetition_penalty: 7 }, 'Less glitchy, more consistent (temperature 0.6, repetition penalty 7)'],
  ['Expressive', { temperature: 0.88, repetition_penalty: 5 }, 'More emotion and variation (temperature 0.88)'],
  ['Slower', { speed: 0.85 }, 'Speed 0.85'],
  ['Faster', { speed: 1.2 }, 'Speed 1.2']
];
const LANGUAGE_NAMES = { en: 'English', es: 'Spanish', fr: 'French', de: 'German', it: 'Italian', pt: 'Portuguese', pl: 'Polish', tr: 'Turkish', ru: 'Russian', nl: 'Dutch',
  cs: 'Czech', ar: 'Arabic', 'zh-cn': 'Chinese', hu: 'Hungarian', ko: 'Korean', ja: 'Japanese', hi: 'Hindi' };
const PERSONA_IDEAS = [
  ['Friendly', 'You are a warm, upbeat friend who is genuinely glad to help. Casual, encouraging, a little playful.'],
  ['Butler', 'You are an impeccably formal English butler. Polite, precise, dry understatement, always "sir" or "madam".'],
  ['Sarcastic', 'You are dry and sarcastic, permanently unimpressed. Deadpan delivery, eye-rolling asides, but you still get the facts across.'],
  ['Drill sergeant', 'You are a loud, impatient drill sergeant. Short barked sentences, orders instead of suggestions, zero patience for excuses.'],
  ['News anchor', 'You are a calm evening-news anchor: measured, neutral, authoritative, every sentence a headline.'],
  ['Pirate', 'You are a gruff old pirate captain. Salty slang, nautical metaphors, a growl in every line.']
];
const PERSONA_SAMPLE = 'I fixed the login bug: the session cookie was set without the Secure flag, so browsers dropped it on HTTPS. I changed auth.js and added two tests, and all 48 tests pass. You still need to deploy it - do you want me to do that now?';

function audForm() {
  if (!AUD.form) {
    let saved = null;
    try { saved = JSON.parse(LS.get(AUD_FORM_KEY) || 'null'); } catch { /* unreadable */ }
    AUD.form = Object.assign({ text: '', voice: '', language: '', mode: 'stream', format: 'pcm', save: true, useProfile: false }, saved || {});
    AUD.form.settings = Object.assign({}, AUD_DEFAULTS, (saved && saved.settings) || {});
  }
  return AUD.form;
}
function audSaveForm() { LS.set(AUD_FORM_KEY, JSON.stringify(AUD.form)); }
const langName = c => LANGUAGE_NAMES[c] || c;
function langOptionsHtml(sel, blank) {
  const langs = (VOICE.info && VOICE.info.languages) || Object.keys(LANGUAGE_NAMES);
  return (blank ? `<option value="">${esc(blank)}</option>` : '') + langs.map(c => `<option value="${esc(c)}"${c === sel ? ' selected' : ''}>${esc(langName(c))} (${esc(c)})</option>`).join('');
}
function voiceOptionsHtml(sel) {
  const keys = allVoices();
  if (sel && !keys.includes(sel)) keys.unshift(sel);
  const opt = k => `<option value="${esc(k)}"${k === sel ? ' selected' : ''}>${esc(voiceKeyName(k))}${voiceProfile(k) && voiceProfile(k).summaryPrompt ? ' · personality' : ''}</option>`;
  const cloned = keys.filter(voiceIsCloned), builtin = keys.filter(k => !voiceIsCloned(k));
  return (cloned.length ? `<optgroup label="Cloned voices">${cloned.map(opt).join('')}</optgroup>` : '') + `<optgroup label="Built-in voices">${builtin.map(opt).join('')}</optgroup>`;
}
function audSettingsText(s) {
  if (!s) return '';
  const parts = [s.language, s.speed !== undefined ? 'speed ' + s.speed : '', s.temperature !== undefined ? 'temperature ' + s.temperature : '',
    s.top_k !== undefined ? 'top_k ' + s.top_k : '', s.top_p !== undefined ? 'top_p ' + s.top_p : '', s.repetition_penalty !== undefined ? 'repetition ' + s.repetition_penalty : '',
    s.length_penalty !== undefined ? 'length ' + s.length_penalty : '', s.enable_text_splitting === false ? 'no splitting' : '', s.stream_chunk_size !== undefined ? 'chunk ' + s.stream_chunk_size : ''];
  return parts.filter(Boolean).join(' · ');
}
// ------------------------------------------------------------------ prices
// Speech is paid like tokens in and out: the text sent (per 1M characters), the audio made (per minute), and each voice
// upload. The estimates here use the account's own prices (its surcharge included).
const audPrices = () => (VOICE.info && VOICE.info.pricing) || {};
const audSurchargeFactor = () => 1 + (((VOICE.info && VOICE.info.account) || {}).surchargePercent || 0) / 100;
function audPriced() { const p = audPrices(); return !!(p.perMillionChars || p.perMinute || p.perVoiceUpload); }
/** About how long speech of this many characters plays (about 14 characters a second at speed 1), as the server estimates it. */
const audEstimateMs = (chars, speed) => chars * 1000 / 14 / Math.max(0.1, Number(speed) || 1);
function audSpeechMicros(chars, audioMs, surcharge = true) {
  const p = audPrices();
  return Math.ceil((chars * (p.perMillionChars || 0) + audioMs / 60000 * (p.perMinute || 0) * 1e6) * (surcharge ? audSurchargeFactor() : 1));
}
function audUploadMicros() { return Math.ceil((audPrices().perVoiceUpload || 0) * 1e6 * audSurchargeFactor()); }
function audCostText(text, speed) {
  if (!audPriced() || !text) return '';
  const ms = audEstimateMs(text.length, speed);
  const charged = ((VOICE.info && VOICE.info.account) || {}).charged;
  return `${charged ? 'Costs about' : 'Users pay about'} ${money(audSpeechMicros(text.length, ms, charged))} (${num(text.length)} characters, about ${Math.max(1, Math.round(ms / 1000))} s of audio)`;
}
/** The prices form (Audio → Settings for administrators, and Finance): text in, audio out, voice uploads. */
function audPricingCardHtml(p, edit) {
  const field = (id, label, value, hint) => `<label>${esc(label)}<input type="number" id="${id}" min="0" step="any" value="${value ?? ''}" placeholder="free"${edit ? '' : ' disabled'}>
    <span class="muted small">${esc(hint)}</span></label>`;
  const example = (1000 * (p.perMillionChars || 0) + (p.perMinute || 0) * 1e6) || 0;
  const source = !p.source || p.source === 'config' ? 'Starting prices from the configuration (TextToSpeech)' : `Set by ${p.source.replace(/^admin:/, '')} ${ago(p.effectiveFromUtc)}`;
  return card('Speech pricing', `<p class="muted small">What speech costs User accounts, like tokens in and out: the text sent (in), the audio made (out), and each voice upload.
      Administrators are never charged. The spoken summary in Dictation mode is a model call, charged at that model's token prices (Model Square).</p>
    <div class="form-grid">${field('price-chars', 'Text in: USD per 1M characters', p.perMillionChars, 'For example 15 is $0.015 per 1,000 characters')}
      ${field('price-minute', 'Audio out: USD per minute', p.perMinute, 'Per minute of speech made')}
      ${field('price-upload', 'Voice upload: USD each', p.perVoiceUpload, 'Cloning a voice, or renewing one')}</div>
    <p class="small aud-price-example">${example ? `1,000 characters with a minute of audio: <b>${money(Math.ceil(example))}</b> (before any surcharge)` : 'Speech is free.'}</p>
    <div class="row aud-settings-foot">${edit ? '<button class="btn primary" data-act="aud-price-save">Save prices</button>' : ''}<span class="muted small">${esc(source)}</span></div>`, { cls: 'section' });
}
ACTIONS['aud-price-save'] = async () => {
  const v = id => { const x = ($('#' + id) || {}).value; return x === '' || x === undefined ? null : Number(x); };
  let p;
  try { p = await put('/api/audio/pricing', { perMillionChars: v('price-chars'), perMinute: v('price-minute'), perVoiceUpload: v('price-upload') }); }
  catch (e) { toast(e.message, 'bad'); return; }
  if (VOICE.info) VOICE.info.pricing = p;
  toast('Speech prices saved', 'ok');
  if ($('#aud-body')) audRender(); else if (typeof refreshNow === 'function') refreshNow();
};

function expiryText(iso) {
  const days = Math.ceil((new Date(iso) - Date.now()) / 86400000);
  if (days <= 0) return '<b class="aud-bad">deleted by the server (expired)</b>';
  return days <= 5 ? `<b class="aud-warn">deleted in ${plural(days, 'day')}</b>` : `kept for ${plural(days, 'more day')}`;
}

// ------------------------------------------------------------------ page
const AUD_TABS = [['speak', 'Speak'], ['voices', 'Voices'], ['personalities', 'Personalities'], ['dictation', 'Dictation'], ['settings', 'Settings']];
/** The tabs this account sees: Settings is for administrators. */
const audTabs = info => AUD_TABS.filter(([k]) => k !== 'settings' || info.isAdmin);
function audStatusHtml(info) {
  if (!info.available) return '<span class="aud-status bad"><i></i>No speech server</span>';
  if (!info.ready) return `<span class="aud-status bad" data-tip="${esc(info.problem || '')}"><i></i>Not answering</span>`;
  const sp = info.speakers || {};
  return `<span class="aud-status ok" data-tip="${esc(info.baseUrl || 'The XTTS speech server')}"><i></i>Ready · ${plural((sp.builtin || []).length, 'voice')} · ${num((sp.voices || []).length)} cloned</span>`;
}
function audBodyHtml() {
  const info = VOICE.info;
  const notReady = !info.ready && AUD.tab !== 'settings' && AUD.tab !== 'dictation'
    ? `<div class="notice warn aud-notice">${esc(info.problem || 'The speech server is not answering.')}${info.isAdmin ? ' <a href="/audio/settings" data-nav>Open Settings</a>' : ''}</div>` : '';
  if (notReady) return notReady;
  switch (AUD.tab) {
    case 'voices': return audVoicesHtml();
    case 'personalities': return audPersonaPageHtml();
    case 'dictation': return audDictationHtml();
    case 'settings': return audSettingsPageHtml();
    default: return audSpeakHtml();
  }
}
function audRender() {
  const body = $('#aud-body');
  if (!body) return;
  const head = $('.page-head .actions');
  if (head) head.innerHTML = audStatusHtml(VOICE.info);
  body.innerHTML = audBodyHtml();
  applyPermissions(body);
  audAfter();
}
async function audModels() {
  try {
    const meta = CHAT.meta && CHAT.meta.providerModels ? CHAT.meta : await get('/api/chat/meta');
    AUD.defaultModel = meta.defaultModel || null;
    return [...(meta.customModels || []), ...(meta.providerModels || [])].map(m => ({ id: m.id, name: m.name || m.id }));
  } catch { return []; }
}
VIEWS.audio = {
  live: false,
  async render(params) {
    AUD.tab = (params.args && params.args[0]) || 'speak';
    setCrumbs([['Audio', '/audio']]);
    let info;
    try { info = await loadVoice(true); }
    catch (e) { return pageHead('Audio') + card('', emptyState('Audio is not available', e.message)); }
    if (!info.available) {
      return pageHead('Audio', { actions: audStatusHtml(info) }) + card('', emptyState('No speech server is set up', info.isAdmin
        ? 'Set TextToSpeech.BaseUrl (the XTTS server, e.g. http://192.168.1.20:8020) in the configuration and restart; then set its API key here under Settings.'
        : 'An administrator sets up the speech server.'));
    }
    if (!audTabs(info).some(([k]) => k === AUD.tab)) AUD.tab = 'speak';
    if (info.ready && AUD.tab === 'speak') AUD.clips = await get('/api/audio/clips').catch(() => []);
    if (AUD.tab === 'personalities' && !AUD.models) AUD.models = await audModels();
    return pageHead('Audio', {
      subtitle: 'Speech from the XTTS server: make it with every setting, clone voices, and give each voice a personality for Dictation mode in Chat.',
      actions: audStatusHtml(info)
    }) + `<div class="tabs">${audTabs(info).map(([k, l]) => `<a href="/audio${k === 'speak' ? '' : '/' + k}" data-nav class="${AUD.tab === k ? 'active' : ''}">${l}</a>`).join('')}</div>
      <div id="aud-body">${audBodyHtml()}</div>`;
  },
  after() { audAfter(); }
};
function audAfter() {
  if (AUD.tab === 'speak') audSpeakAfter();
  else if (AUD.tab === 'voices') audVoicesAfter();
  else if (AUD.tab === 'personalities') audPersonaAfter();
}

// ------------------------------------------------------------------ Speak
function audSpeakHtml() {
  const f = audForm(), info = VOICE.info, max = info.defaults.maxTextChars;
  const voice = f.voice || currentVoice();
  const prof = voiceProfile(voice);
  return `<div class="aud-speak">
    <div class="aud-main">
      ${card('Text to speak', `<textarea id="aud-text" class="aud-text" rows="7" maxlength="${max}" placeholder="Type or paste what to say. Punctuation sets the pauses; spell out symbols and numbers where it matters (3.5 GB: three point five gigabytes).">${esc(f.text)}</textarea>
        <div class="aud-text-foot"><span class="muted small" id="aud-count">${num(f.text.length)} / ${num(max)}</span>
          <span class="muted small aud-cost" id="aud-cost">${esc(audCostText(f.text, f.useProfile ? 1 : f.settings.speed))}</span><span class="spacer"></span>
          <button class="btn xs ghost" data-act="aud-clean" data-tip="Remove Markdown, links, code blocks and emoji so it reads well">Clean up for speech</button>
          <button class="btn xs ghost" data-act="aud-clear-text">Clear</button></div>
        <div class="aud-controls">
          <label>Voice<select id="aud-voice">${voiceOptionsHtml(voice)}</select></label>
          <label>Language<select id="aud-lang">${langOptionsHtml(f.language || (prof && prof.settings && prof.settings.language) || info.defaults.language)}</select></label>
          <div class="aud-mode"><span>Mode</span><div class="seg" role="group" aria-label="Mode">
            <button type="button" class="seg-btn${f.mode === 'stream' ? ' on' : ''}" data-act="aud-mode" data-mode="stream" data-tip="Plays while it is made: the first sound comes after about 1.5 s (/tts/stream)">Stream</button>
            <button type="button" class="seg-btn${f.mode === 'file' ? ' on' : ''}" data-act="aud-mode" data-mode="file" data-tip="Made whole, then played (/tts)">Whole file</button></div></div>
          <label class="aud-format${f.mode === 'stream' ? '' : ' hidden'}">Stream format<select id="aud-format">
            <option value="pcm"${f.format === 'pcm' ? ' selected' : ''}>PCM: raw 16-bit, 24 kHz</option><option value="wav"${f.format === 'wav' ? ' selected' : ''}>WAV: header, then PCM</option></select></label>
          <label class="check aud-inline"><input type="checkbox" id="aud-save"${f.save ? ' checked' : ''}><span>Keep the clip</span></label>
        </div>
        <div class="aud-go"><button class="btn primary" data-act="aud-generate" id="aud-go">${SPEAK_ICON} Speak</button>
          <button class="btn hidden" data-act="aud-stop" id="aud-stop">Stop</button>
          <span class="muted small" id="aud-live"></span></div>
        <div id="aud-result">${audResultHtml()}</div>`)}
      ${card('Speech settings', audSettingsHtml(f), { actions: AUD_PRESETS.map(([n, , tip], i) => `<button class="btn xs ghost" data-act="aud-preset" data-i="${i}" data-tip="${esc(tip)}">${esc(n)}</button>`).join('') })}
    </div>
    <aside class="aud-side">${card('Clips', audClipsHtml(), {
      hint: AUD.clips.length ? `${num(AUD.clips.length)} of ${num(info.defaults.maxClips)}` : '',
      actions: AUD.clips.length ? '<button class="btn xs ghost" data-act="aud-clips-clear">Clear all</button>' : '', flush: true })}</aside>
  </div>`;
}
function audSettingsHtml(f) {
  const s = f.settings;
  return `<label class="check aud-useprofile"><input type="checkbox" id="aud-useprofile"${f.useProfile ? ' checked' : ''}><span><b>Use the voice's own settings</b>
      <span class="muted small">The settings saved with the voice's personality (the ones Chat uses) instead of these</span></span></label>
    <div class="aud-sliders${f.useProfile ? ' off' : ''}" id="aud-sliders">${AUD_FIELDS.map(x => `<div class="aud-field${x.stream && f.mode !== 'stream' ? ' dim' : ''}">
        <div class="aud-field-head"><span>${esc(x.label)}</span><code>${x.k}</code><span class="spacer"></span>
          <input type="number" class="aud-num" data-k="${x.k}" min="${x.min}" max="${x.max}" step="${x.step}" value="${esc(s[x.k])}" aria-label="${esc(x.label)}"></div>
        <input type="range" class="aud-range" data-k="${x.k}" min="${x.min}" max="${x.max}" step="${x.step}" value="${esc(s[x.k])}" aria-label="${esc(x.label)}">
        <div class="muted small">${esc(x.hint)}</div></div>`).join('')}
      <label class="check aud-field"><input type="checkbox" id="aud-split"${s.enable_text_splitting ? ' checked' : ''}><span><b>Split into sentences</b> <code>enable_text_splitting</code>
        <span class="muted small">Keep it on for anything longer than one sentence</span></span></label></div>
    <div class="row aud-settings-foot">
      <button class="btn xs" data-act="aud-save-profile" data-tip="These settings become the voice's own: Read aloud and Dictation mode in Chat use them">Save as the voice's settings</button>
      <button class="btn xs ghost" data-act="aud-load-profile" data-tip="Fill in the settings saved with the voice">Load the voice's settings</button></div>`;
}
function audResultHtml() {
  const r = AUD.result;
  if (!r) return '';
  const stats = [r.firstMs != null ? `first sound after ${dur(r.firstMs)}` : '', r.generateMs ? `made in ${dur(r.generateMs)}` : '',
    r.seconds ? `${r.seconds.toFixed(1)} s of audio` : '', r.generateMs && r.seconds ? `${(r.generateMs / 1000 / r.seconds).toFixed(2)} s per second of audio` : '',
    r.costMicros ? `cost ${money(r.costMicros)}` : ''].filter(Boolean).join(' · ');
  const src = r.clipId ? `/api/audio/clips/${enc(r.clipId)}/audio` : r.url;
  return `<div class="aud-result">${stats ? `<div class="muted small">${esc(stats)}</div>` : ''}${src ? `<audio controls preload="auto" src="${esc(src)}"></audio>` : ''}
    <div class="row">${r.clipId ? `<a class="btn xs" href="/api/audio/clips/${enc(r.clipId)}/audio?download=1" download>${icon('download')} WAV</a>
      <a class="btn xs" href="/api/audio/clips/${enc(r.clipId)}/audio?format=pcm&download=1" download>${icon('download')} PCM</a>`
      : r.url ? `<a class="btn xs" href="${esc(r.url)}" download="speech.wav">${icon('download')} WAV</a>` : ''}</div></div>`;
}
function audClipsHtml() {
  return `<div class="aud-clip-search"><input id="aud-clip-q" placeholder="Filter clips" value="${esc(AUD.q)}" aria-label="Filter clips" autocomplete="off"></div>${audClipListHtml()}`;
}
function audClipListHtml() {
  const q = AUD.q.toLowerCase();
  const list = AUD.clips.filter(c => !q || (c.text + ' ' + c.voice).toLowerCase().includes(q));
  return `<div class="aud-clips" id="aud-clips">${list.length ? list.map(audClipHtml).join('') : `<p class="muted small aud-none">${AUD.clips.length ? 'Nothing matches.' : 'Speech you keep shows here: play it again, download it, or use its text and settings again.'}</p>`}</div>`;
}
function audRedrawClipList() { const list = $('#aud-clips'); if (list) list.outerHTML = audClipListHtml(); }
function audClipHtml(c) {
  const on = AUD.sel === c.id, id = enc(c.id);
  return `<div class="aud-clip${on ? ' on' : ''}" data-id="${esc(c.id)}">
    <button type="button" class="aud-clip-main" data-act="aud-clip-open" data-id="${esc(c.id)}"><span class="aud-clip-text">${esc(c.text)}</span>
      <span class="muted small">${esc(voiceKeyName(c.voice))} · ${(c.durationMs / 1000).toFixed(1)} s · ${c.mode === 'stream' ? 'streamed' : 'whole file'} · ${esc(ago(c.createdUtc))}</span></button>
    ${on ? `<div class="aud-clip-more"><audio controls preload="auto" src="/api/audio/clips/${id}/audio"></audio>
      <div class="muted small">${esc(audSettingsText(c.settings))}${c.firstAudioMs != null ? ` · first sound after ${dur(c.firstAudioMs)}` : ''} · made in ${dur(c.generateMs)} · ${fileSize(c.bytes)}${c.costMicros ? ` · cost ${money(c.costMicros)}` : ''}</div>
      <div class="row"><a class="btn xs" href="/api/audio/clips/${id}/audio?download=1" download>${icon('download')} WAV</a>
        <a class="btn xs" href="/api/audio/clips/${id}/audio?format=pcm&download=1" download>${icon('download')} PCM</a>
        <button type="button" class="btn xs" data-act="aud-clip-reuse" data-id="${esc(c.id)}" data-tip="Put its text, voice and settings back in the form">Use again</button>
        <button type="button" class="btn xs ghost" data-act="aud-clip-copy" data-id="${esc(c.id)}">Copy text</button>
        <span class="spacer"></span><button type="button" class="btn xs ghost danger" data-act="aud-clip-delete" data-id="${esc(c.id)}">Delete</button></div></div>` : ''}
  </div>`;
}
async function audReloadClips(select) {
  AUD.clips = await get('/api/audio/clips').catch(() => AUD.clips);
  if (select) AUD.sel = select;
  const side = $('.aud-side');
  if (side) side.innerHTML = card('Clips', audClipsHtml(), { hint: AUD.clips.length ? `${num(AUD.clips.length)} of ${num(VOICE.info.defaults.maxClips)}` : '',
    actions: AUD.clips.length ? '<button class="btn xs ghost" data-act="aud-clips-clear">Clear all</button>' : '', flush: true });
  audClipSearchAfter();
}
function audClipSearchAfter() {
  const q = $('#aud-clip-q');
  if (q) q.addEventListener('input', () => { AUD.q = q.value; audRedrawClipList(); });
}
/** Reads the form on the page into AUD.form (and keeps it for next time). */
function audReadForm() {
  const f = audForm();
  const val = id => { const el = $('#' + id); return el ? el.value : undefined; };
  if ($('#aud-text')) f.text = val('aud-text');
  if ($('#aud-voice')) f.voice = val('aud-voice');
  if ($('#aud-lang')) f.language = val('aud-lang');
  if ($('#aud-format')) f.format = val('aud-format');
  if ($('#aud-save')) f.save = $('#aud-save').checked;
  if ($('#aud-useprofile')) f.useProfile = $('#aud-useprofile').checked;
  if ($('#aud-split')) f.settings.enable_text_splitting = $('#aud-split').checked;
  $$('.aud-num').forEach(n => { const v = Number(n.value); if (n.value !== '' && Number.isFinite(v)) f.settings[n.dataset.k] = v; });
  audSaveForm();
  return f;
}
/** The request for the form: text, voice, language, and its settings (unless the voice's own are used). */
function audRequest(f) {
  const body = { text: f.text, voiceKey: f.voice || currentVoice(), language: f.language || undefined, save: !!f.save, useProfile: !!f.useProfile };
  if (!f.useProfile) {
    for (const x of AUD_FIELDS) if (!x.stream || f.mode === 'stream') body[x.k] = f.settings[x.k];
    body.enable_text_splitting = !!f.settings.enable_text_splitting;
  }
  return body;
}
function audBusy(on) {
  const go = $('#aud-go'), stop = $('#aud-stop');
  if (go) go.disabled = on;
  if (stop) stop.classList.toggle('hidden', !on);
  if (!on) { const l = $('#aud-live'); if (l) l.textContent = ''; }
}
function audSpeakAfter() {
  const text = $('#aud-text');
  if (!text) return;
  const max = VOICE.info.defaults.maxTextChars;
  const showCost = () => { const c = $('#aud-cost'); if (c) c.textContent = audCostText(text.value, AUD.form.useProfile ? 1 : AUD.form.settings.speed); };
  text.addEventListener('input', () => { $('#aud-count').textContent = `${num(text.value.length)} / ${num(max)}`; AUD.form.text = text.value; audSaveForm(); showCost(); });
  // The estimate follows the speed (and the voice's own settings, which speak at speed 1 here).
  $$('.aud-num[data-k=speed], .aud-range[data-k=speed]').forEach(el => el.addEventListener('input', () => setTimeout(showCost)));
  $('#aud-useprofile').addEventListener('change', () => setTimeout(showCost));
  text.addEventListener('keydown', ev => { if ((ev.ctrlKey || ev.metaKey) && ev.key === 'Enter') { ev.preventDefault(); ACTIONS['aud-generate'](); } });
  ['aud-lang', 'aud-format', 'aud-save'].forEach(id => { const el = $('#' + id); if (el) el.addEventListener('change', () => audReadForm()); });
  const voice = $('#aud-voice');
  voice.addEventListener('change', () => {
    audReadForm();
    // The voice's own language, when its personality has one.
    const p = voiceProfile(voice.value), lang = p && p.settings && p.settings.language;
    if (lang && $('#aud-lang')) { $('#aud-lang').value = lang; audReadForm(); }
  });
  const useProfile = $('#aud-useprofile');
  useProfile.addEventListener('change', () => { $('#aud-sliders').classList.toggle('off', useProfile.checked); audReadForm(); });
  $('#aud-split').addEventListener('change', () => audReadForm());
  $$('.aud-range').forEach(r => r.addEventListener('input', () => { const n = $(`.aud-num[data-k="${r.dataset.k}"]`); if (n) n.value = r.value; audReadForm(); }));
  $$('.aud-num').forEach(n => n.addEventListener('input', () => { const r = $(`.aud-range[data-k="${n.dataset.k}"]`); if (r && n.value !== '') r.value = n.value; audReadForm(); }));
  audClipSearchAfter();
}
ACTIONS['aud-mode'] = el => {
  const f = audReadForm();
  f.mode = el.dataset.mode; audSaveForm();
  $$('.aud-mode .seg-btn').forEach(b => b.classList.toggle('on', b.dataset.mode === f.mode));
  $('.aud-format').classList.toggle('hidden', f.mode !== 'stream');
  $$('.aud-field').forEach((x, i) => { if (AUD_FIELDS[i] && AUD_FIELDS[i].stream) x.classList.toggle('dim', f.mode !== 'stream'); });
};
ACTIONS['aud-preset'] = el => {
  const f = audReadForm(), preset = AUD_PRESETS[Number(el.dataset.i)];
  if (!preset) return;
  Object.assign(f.settings, preset[1]);
  if ($('#aud-useprofile')) $('#aud-useprofile').checked = false;
  f.useProfile = false; audSaveForm();
  audRender();
  toast(`Settings: ${preset[0]}`, 'ok');
};
ACTIONS['aud-clean'] = () => {
  const t = $('#aud-text'); if (!t) return;
  t.value = speakableText(t.value).replace(/\(code\)|\(formula\)/g, '').replace(/https?:\/\/\S+/g, '').replace(/[\u{1F000}-\u{1FAFF}\u{2600}-\u{27BF}\u{FE0F}]/gu, '').replace(/\s+([.,!?])/g, '$1').replace(/\s{2,}/g, ' ').trim();
  t.dispatchEvent(new Event('input'));
};
ACTIONS['aud-clear-text'] = () => { const t = $('#aud-text'); if (t) { t.value = ''; t.dispatchEvent(new Event('input')); t.focus(); } };
ACTIONS['aud-generate'] = async () => {
  const f = audReadForm();
  if (!f.text.trim()) { toast('Type something to say.', 'bad'); const t = $('#aud-text'); if (t) t.focus(); return; }
  const body = audRequest(f);
  audBusy(true);
  AUD.result = null;
  const out = $('#aud-result'); if (out) out.innerHTML = '';
  const live = t => { const l = $('#aud-live'); if (l) l.textContent = t; };
  const t0 = performance.now();
  try {
    if (f.mode === 'stream') {
      live('Waiting for the first sound…');
      let playing = false;
      const r = await streamSpeech(body, { format: f.format, onqueue: t => live(t || 'Waiting for the first sound…'),
        onbuffer: b => { playing = !b; if (b) { live(`Buffering ${b.seconds.toFixed(1)} of ${isFinite(b.needed) ? b.needed.toFixed(1) : '…'} s, so it plays without gaps (the server makes speech slower than it plays)`); } },
        onstart: ms => { playing = true; live(`First sound after ${dur(ms)}`); },
        onprogress: b => { if (playing) live(`Playing · ${(b / 2 / 24000).toFixed(1)} s arrived`); } });
      AUD.result = { clipId: r.stopped ? null : r.clipId, firstMs: r.firstMs, seconds: r.seconds };
      if (r.stopped) toast('Stopped', 'ok');
    } else {
      live('Making the whole file…');
      AUD.ctrl = new AbortController();
      const stopLine = speechLineWatch(t => live(t || 'Making the whole file…'));
      const res = await fetch('/api/audio/tts', { method: 'POST', credentials: 'same-origin', signal: AUD.ctrl.signal,
        headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': S.csrf || '' }, body: JSON.stringify(body) }).finally(stopLine);
      if (!res.ok) throw await apiErrorOf(res);
      const blob = await res.blob();
      const clipId = res.headers.get('X-Clip-Id');
      if (AUD.blobUrl) URL.revokeObjectURL(AUD.blobUrl);
      AUD.blobUrl = clipId ? null : URL.createObjectURL(blob);
      AUD.result = { clipId, url: AUD.blobUrl, generateMs: Number(res.headers.get('X-Generate-Ms')) || Math.round(performance.now() - t0), seconds: Number(res.headers.get('X-Duration-Ms')) / 1000,
        costMicros: Number(res.headers.get('X-Cost-Micros')) || 0, play: true };
    }
    if (AUD.result.clipId) {
      await audReloadClips(AUD.result.clipId);
      const c = AUD.clips.find(x => x.id === AUD.result.clipId);
      if (c) { AUD.result.generateMs = c.generateMs; AUD.result.costMicros = c.costMicros || AUD.result.costMicros; }
    }
    const box = $('#aud-result');
    if (box) box.innerHTML = audResultHtml();
    if (AUD.result.play) { const a = $('#aud-result audio'); if (a) a.play().catch(() => { }); }
  } catch (e) { if (e.name !== 'AbortError') toast(e.message, 'bad'); }
  finally { AUD.ctrl = null; audBusy(false); }
};
ACTIONS['aud-stop'] = () => { stopAudio(); if (AUD.ctrl) AUD.ctrl.abort(); $$('audio').forEach(a => a.pause()); };
ACTIONS['aud-clip-open'] = el => {
  AUD.sel = AUD.sel === el.dataset.id ? null : el.dataset.id;
  audRedrawClipList();
};
ACTIONS['aud-clip-reuse'] = el => {
  const c = AUD.clips.find(x => x.id === el.dataset.id); if (!c) return;
  const f = audForm(), s = c.settings || {};
  f.text = c.text; f.voice = c.voice; f.language = s.language || f.language; f.useProfile = false;
  for (const x of AUD_FIELDS) if (s[x.k] !== undefined) f.settings[x.k] = s[x.k];
  if (s.enable_text_splitting !== undefined) f.settings.enable_text_splitting = s.enable_text_splitting;
  f.mode = c.mode === 'file' ? 'file' : 'stream';
  audSaveForm(); audRender();
  window.scrollTo({ top: 0, behavior: reducedMotion() ? 'auto' : 'smooth' });
};
ACTIONS['aud-clip-copy'] = el => { const c = AUD.clips.find(x => x.id === el.dataset.id); if (c) copyText(c.text, 'Text copied'); };
ACTIONS['aud-clip-delete'] = async el => {
  try { await del('/api/audio/clips/' + enc(el.dataset.id)); } catch (e) { toast(e.message, 'bad'); return; }
  if (AUD.result && AUD.result.clipId === el.dataset.id) { AUD.result = null; const r = $('#aud-result'); if (r) r.innerHTML = ''; }
  await audReloadClips();
};
ACTIONS['aud-clips-clear'] = async () => {
  if (!await confirmBox('Delete every clip?', `All ${num(AUD.clips.length)} kept clips are deleted.`, { confirm: 'Delete all', danger: true })) return;
  try { await del('/api/audio/clips'); } catch (e) { toast(e.message, 'bad'); return; }
  AUD.result = null; await audReloadClips();
};
ACTIONS['aud-save-profile'] = async () => {
  const f = audReadForm(), key = f.voice || currentVoice(), p = voiceProfile(key) || {};
  const settings = Object.assign({ language: f.language || undefined }, f.settings);
  try { await audSaveProfile(key, p.summaryPrompt || null, p.summaryModel || null, settings); } catch (e) { toast(e.message, 'bad'); return; }
  toast(`${voiceKeyName(key)} speaks with these settings now`, 'ok');
};
ACTIONS['aud-load-profile'] = () => {
  const f = audReadForm(), key = f.voice || currentVoice(), p = voiceProfile(key);
  if (!p || !p.settings || !Object.keys(p.settings).length) { toast(`${voiceKeyName(key)} has no settings of its own yet.`, 'bad'); return; }
  for (const x of AUD_FIELDS) if (p.settings[x.k] !== undefined) f.settings[x.k] = p.settings[x.k];
  if (p.settings.enable_text_splitting !== undefined) f.settings.enable_text_splitting = p.settings.enable_text_splitting;
  if (p.settings.language) f.language = p.settings.language;
  f.useProfile = false; audSaveForm(); audRender();
};
async function audSaveProfile(voice, summaryPrompt, summaryModel, settings) {
  const saved = await put('/api/audio/profiles', { voice, summaryPrompt, summaryModel, settings });
  const list = VOICE.info.profiles || (VOICE.info.profiles = []);
  const i = list.findIndex(x => x.voice === voice);
  if (i >= 0) list[i] = saved; else list.push(saved);
  return saved;
}

// ------------------------------------------------------------------ Voices
function audVoicesHtml() {
  const info = VOICE.info, sp = info.speakers || {}, cur = currentVoice();
  const onServer = new Set(sp.voices || []);
  const sources = new Map((info.sources || []).map(s => [s.name, s]));
  const names = [...new Set([...(sp.voices || []), ...sources.keys()])].sort((a, b) => a.localeCompare(b));
  const cloned = names.length ? names.map(n => {
    const key = 'voice:' + n, src = sources.get(n), here = onServer.has(n);
    const mayDelete = src ? src.mine : info.isAdmin;
    return `<div class="aud-voice-row"><div class="grow"><b>${esc(n)}</b>${key === cur ? ' <span class="voice-tag">your voice</span>' : ''}${voiceProfile(key) && voiceProfile(key).summaryPrompt ? ' <span class="voice-tag">personality</span>' : ''}
        <div class="muted small">${src ? `cloned by ${esc(src.createdBy)} · ${plural(src.clips.length, 'clip')} · ${expiryText(src.expiresUtc)}` : 'cloned elsewhere: its clips are not kept here, so it cannot be renewed'}${here ? '' : ' · <b class="aud-bad">not on the speech server</b>'}</div></div>
      <div class="row">${here ? `<button class="btn xs" data-act="voice-preview" data-key="${esc(key)}">${SPEAK_ICON} Hear</button>` : ''}
        ${here && key !== cur ? `<button class="btn xs" data-act="aud-use-voice" data-key="${esc(key)}">Use in Chat</button>` : ''}
        <button class="btn xs ghost" data-act="aud-voice-details" data-name="${esc(n)}">Details</button>
        ${src && src.mine ? `<button class="btn xs ghost" data-act="aud-voice-renew" data-name="${esc(n)}" data-tip="Upload its clips again: the server keeps it ${info.defaults.voiceDays} more days${audUploadMicros() && info.account && info.account.charged ? ' (' + money(audUploadMicros()) + ')' : ''}">Renew</button>` : ''}
        ${mayDelete ? `<button class="btn xs ghost danger" data-act="aud-voice-delete" data-name="${esc(n)}">Delete</button>` : ''}</div></div>`;
  }).join('') : '<p class="muted small">No cloned voices yet: clone one below.</p>';
  return `<div class="stack">
    ${card('Cloned voices', cloned, { hint: `The speech server deletes a cloned voice ${info.defaults.voiceDays} days after it was uploaded; renew it to keep it.` })}
    ${card('Clone a voice', audCloneHtml())}
    ${card('Built-in voices', `<input id="aud-bq" placeholder="Search ${num((sp.builtin || []).length)} voices" value="${esc(AUD.bq)}" autocomplete="off" aria-label="Search built-in voices">
      <div class="aud-builtin" id="aud-builtin">${audBuiltinHtml()}</div>`, { hint: 'Every voice speaks every language.' })}
  </div>`;
}
function audBuiltinHtml() {
  const q = AUD.bq.trim().toLowerCase(), cur = currentVoice();
  const list = ((VOICE.info.speakers || {}).builtin || []).filter(n => !q || n.toLowerCase().includes(q));
  return list.length ? list.map(n => { const key = 'speaker:' + n; return `<div class="aud-chip${key === cur ? ' on' : ''}">
      <button type="button" class="aud-chip-play" data-act="voice-preview" data-key="${esc(key)}" aria-label="Hear ${esc(n)}">${SPEAK_ICON}</button><span>${esc(n)}</span>
      ${key === cur ? '<span class="voice-tag">yours</span>' : `<button type="button" class="btn xs ghost" data-act="aud-use-voice" data-key="${esc(key)}">Use</button>`}</div>`; }).join('')
    : '<p class="muted small">No voice matches.</p>';
}
function audCloneListHtml() {
  const clips = AUD.staged.map((c, i) => {
    const warn = c.seconds && (c.seconds < 6 || c.seconds > 30) ? `<span class="aud-warn small">${c.seconds < 6 ? 'shorter than 6 s' : 'longer than 30 s: only the first part is used'}</span>` : '';
    return `<div class="clone-clip"><span class="grow"><b>${esc(c.name)}</b> <span class="muted small">${c.seconds ? c.seconds.toFixed(1) + ' s · ' : ''}${fileSize(c.blob.size)}</span> ${warn}</span>
      <audio controls preload="metadata" src="${esc(c.url)}"></audio><button class="btn xs ghost icon-only" data-act="clone-remove" data-i="${i}" aria-label="Remove">${icon('x')}</button></div>`;
  }).join('');
  return `<div id="clone-list" class="clone-list">${clips}</div>`;
}
function audCloneHtml() {
  const mb = VOICE.info.defaults.maxCloneClipMb;
  return `<div class="stack">
    <label class="clone-name">Name<input id="clone-name" maxlength="64" placeholder="my-voice" autocomplete="off" value="${esc(AUD.cloneName || '')}">
      <span class="muted small">Letters, digits, _ or -. Cloning under a name in use replaces that voice.</span></label>
    <div class="clone-drop" id="clone-drop" tabindex="0" role="button" aria-label="Add clips"><span><b>Drop clips here</b> or <u>choose files</u></span>
      <span class="muted small">wav, mp3, flac or ogg, up to ${mb} MB each · 6–30 s of one speaker, natural speech, no music or background noise · 2–4 varied clips clone better than one</span>
      <input type="file" id="clone-files" accept="audio/*,.wav,.mp3,.flac,.ogg,.m4a" multiple hidden></div>
    <div class="row"><button class="btn xs${AUD.rec ? ' danger-solid' : ''}" data-act="clone-record" id="clone-rec">${AUD.rec ? 'Stop recording' : 'Record a clip'}</button>
      <span class="muted small" id="clone-rec-time">${AUD.rec ? 'Recording…' : 'Read something aloud for 10–20 seconds, in your normal voice.'}</span></div>
    ${audCloneListHtml()}
    <details class="clone-adv"><summary>Cloning settings</summary><div class="form-grid">
      <label class="check"><input type="checkbox" id="clone-trim" checked><span>Trim silence below <input type="number" id="clone-trim-db" min="10" max="80" step="1" value="30" class="aud-mini"> dB <code>trim_silence_db</code></span></label>
      <label class="check"><input type="checkbox" id="clone-normalize"><span>Even out loudness <code>normalize</code><span class="muted small">When the clips differ in volume</span></span></label>
      <label>Style audio, seconds <code>gpt_cond_len</code><input type="number" id="clone-cond" min="1" max="60" step="1" value="30"></label>
      <label>Style chunk, seconds <code>gpt_cond_chunk_len</code><input type="number" id="clone-chunk" min="1" max="60" step="1" value="4"></label>
      <label>Most seconds used per clip <code>max_ref_length</code><input type="number" id="clone-maxref" min="1" max="120" step="1" value="30"></label>
    </div></details>
    <label class="check clone-consent"><input type="checkbox" id="clone-consent"><span><b>This is my own voice, or its speaker agreed to it being cloned.</b>
      <span class="muted small">Never clone someone's voice to impersonate or deceive them or others. The speech model is licensed for non-commercial use only.</span></span></label>
    <div class="row"><button class="btn primary" data-act="clone-go" id="clone-go">Clone voice${audUploadMicros() && (VOICE.info.account || {}).charged ? ' · ' + money(audUploadMicros()) : ''}</button><span class="muted small" id="clone-status"></span></div>
  </div>`;
}
function audRenderClone() {
  const name = $('#clone-name'); if (name) AUD.cloneName = name.value;
  const list = $('#clone-list');
  if (list) list.outerHTML = audCloneListHtml();
  const rec = $('#clone-rec');
  if (rec) { rec.textContent = AUD.rec ? 'Stop recording' : 'Record a clip'; rec.classList.toggle('danger-solid', !!AUD.rec); }
}
/** A clip for cloning: its length, from decoding it. */
async function audAddClip(blob, name) {
  let seconds = null;
  try { const ab = await audioCtx().decodeAudioData(await blob.arrayBuffer()); seconds = ab.duration; } catch { /* the server decodes it */ }
  AUD.staged.push({ name, blob, seconds, url: URL.createObjectURL(blob) });
  audRenderClone();
}
/** An AudioBuffer as a 16-bit mono WAV file (recordings are sent as WAV, which every speech server reads). */
function audEncodeWav(buffer) {
  const rate = buffer.sampleRate, n = buffer.length, ch = buffer.numberOfChannels;
  const mono = new Float32Array(n);
  for (let c = 0; c < ch; c++) { const d = buffer.getChannelData(c); for (let i = 0; i < n; i++) mono[i] += d[i] / ch; }
  const out = new DataView(new ArrayBuffer(44 + n * 2)), w = (o, s) => { for (let i = 0; i < s.length; i++) out.setUint8(o + i, s.charCodeAt(i)); };
  w(0, 'RIFF'); out.setUint32(4, 36 + n * 2, true); w(8, 'WAVEfmt '); out.setUint32(16, 16, true); out.setUint16(20, 1, true); out.setUint16(22, 1, true);
  out.setUint32(24, rate, true); out.setUint32(28, rate * 2, true); out.setUint16(32, 2, true); out.setUint16(34, 16, true); w(36, 'data'); out.setUint32(40, n * 2, true);
  for (let i = 0; i < n; i++) { const s = Math.max(-1, Math.min(1, mono[i])); out.setInt16(44 + i * 2, s < 0 ? s * 32768 : s * 32767, true); }
  return new Blob([out.buffer], { type: 'audio/wav' });
}
function audVoicesAfter() {
  const bq = $('#aud-bq');
  if (bq) bq.addEventListener('input', () => { AUD.bq = bq.value; $('#aud-builtin').innerHTML = audBuiltinHtml(); });
  const drop = $('#clone-drop'), files = $('#clone-files');
  if (!drop) return;
  const add = list => { for (const f of list) { if (!/^audio\//.test(f.type) && !/\.(wav|mp3|flac|ogg|oga|opus|m4a)$/i.test(f.name)) { toast(`${f.name} is not an audio file.`, 'bad'); continue; } audAddClip(f, f.name); } };
  drop.addEventListener('click', () => files.click());
  drop.addEventListener('keydown', ev => { if (ev.key === 'Enter' || ev.key === ' ') { ev.preventDefault(); files.click(); } });
  files.addEventListener('change', () => { add([...files.files]); files.value = ''; });
  drop.addEventListener('dragover', ev => { ev.preventDefault(); drop.classList.add('over'); });
  drop.addEventListener('dragleave', () => drop.classList.remove('over'));
  drop.addEventListener('drop', ev => { ev.preventDefault(); drop.classList.remove('over'); add([...ev.dataTransfer.files]); });
  const name = $('#clone-name');
  if (name) name.addEventListener('input', () => { AUD.cloneName = name.value; });
}
ACTIONS['clone-remove'] = el => {
  const c = AUD.staged.splice(Number(el.dataset.i), 1)[0];
  if (c) URL.revokeObjectURL(c.url);
  audRenderClone();
};
ACTIONS['clone-record'] = async () => {
  if (AUD.rec) { AUD.rec.stop(); return; }
  if (!navigator.mediaDevices || !window.MediaRecorder) { toast('Recording needs a microphone and a browser that can record audio.', 'bad'); return; }
  let stream;
  try { stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: false, noiseSuppression: true, autoGainControl: true } }); }
  catch (e) { toast('No access to the microphone: ' + e.message, 'bad'); return; }
  const chunks = [], rec = new MediaRecorder(stream), t0 = Date.now();
  const timer = setInterval(() => { const t = $('#clone-rec-time'); if (t) t.textContent = `Recording… ${((Date.now() - t0) / 1000).toFixed(0)} s (aim for 10–20 s)`; }, 250);
  rec.ondataavailable = e => { if (e.data && e.data.size) chunks.push(e.data); };
  rec.onstop = async () => {
    clearInterval(timer); stream.getTracks().forEach(t => t.stop()); AUD.rec = null;
    const raw = new Blob(chunks, { type: (rec.mimeType || 'audio/webm').split(';')[0] });
    try {
      const wav = audEncodeWav(await audioCtx().decodeAudioData(await raw.arrayBuffer()));
      await audAddClip(wav, `recording-${AUD.staged.length + 1}.wav`);
    } catch (e) { toast('The recording could not be read: ' + e.message, 'bad'); audRenderClone(); }
    const t = $('#clone-rec-time'); if (t) t.textContent = '';
  };
  AUD.rec = rec;
  rec.start(250);
  audRenderClone();
};
ACTIONS['clone-go'] = async () => {
  const name = ($('#clone-name').value || '').trim(), status = t => { const s = $('#clone-status'); if (s) s.textContent = t; };
  if (!/^[A-Za-z0-9_-]{1,64}$/.test(name)) { toast('A voice name has 1 to 64 letters, digits, _ or -.', 'bad'); $('#clone-name').focus(); return; }
  if (!AUD.staged.length) { toast('Add at least one clip of the voice.', 'bad'); return; }
  if (!$('#clone-consent').checked) { toast('Confirm that this is your own voice, or that its speaker agreed.', 'bad'); return; }
  const cond = Number($('#clone-cond').value), chunk = Number($('#clone-chunk').value);
  if (chunk > cond) { toast('gpt_cond_chunk_len must not be larger than gpt_cond_len.', 'bad'); return; }
  const existing = allVoices().includes('voice:' + name);
  if (existing && !await confirmBox(`Replace “${name}”?`, 'A voice with this name exists: cloning replaces it.', { confirm: 'Replace', danger: true })) return;
  const go = $('#clone-go'); go.disabled = true;
  try {
    const ids = [];
    for (let i = 0; i < AUD.staged.length; i++) {
      const c = AUD.staged[i];
      status(`Uploading clip ${i + 1} of ${AUD.staged.length}…`);
      const res = await fetch('/api/audio/voice-clips?name=' + enc(c.name), { method: 'POST', credentials: 'same-origin', body: c.blob,
        headers: { 'Content-Type': c.blob.type || 'application/octet-stream', 'X-CSRF-Token': S.csrf || '' } });
      if (!res.ok) throw await apiErrorOf(res);
      ids.push((await res.json()).id);
    }
    status('Cloning…');
    const r = await post('/api/audio/voices', {
      name, clips: ids, consent: true, normalize: $('#clone-normalize').checked,
      trimSilenceDb: $('#clone-trim').checked ? Number($('#clone-trim-db').value) : null,
      gptCondLen: cond, gptCondChunkLen: chunk, maxRefLength: Number($('#clone-maxref').value)
    });
    AUD.staged.forEach(c => URL.revokeObjectURL(c.url));
    AUD.staged = []; AUD.cloneName = '';
    await loadVoice(true);
    audRender();
    toast(`Cloned: ${name}${r.costMicros ? ' · ' + money(r.costMicros) : ''}`, 'ok');
    if (await confirmBox(`Use ${name} in Chat?`, 'It becomes the voice that reads answers aloud and talks in Dictation mode.', { confirm: 'Use it' })) { await setVoice(r.key); audRender(); }
  } catch (e) { toast(e.message, 'bad'); status(''); }
  finally { const b = $('#clone-go'); if (b) b.disabled = false; }
};
ACTIONS['aud-use-voice'] = async el => {
  try { await setVoice(el.dataset.key); } catch (e) { toast(e.message, 'bad'); return; }
  audRender();
};
function audMetaHtml(v) {
  if (v === null || v === undefined) return '<span class="muted">—</span>';
  if (Array.isArray(v)) return v.length ? `<ul class="aud-meta-list">${v.map(x => `<li>${audMetaHtml(x)}</li>`).join('')}</ul>` : '<span class="muted">none</span>';
  if (typeof v === 'object') return `<dl class="aud-meta">${Object.entries(v).map(([k, x]) => `<dt>${esc(k)}</dt><dd>${audMetaHtml(x)}</dd>`).join('')}</dl>`;
  return esc(String(v));
}
ACTIONS['aud-voice-details'] = async el => {
  let d;
  try { d = await get('/api/audio/voices/' + enc(el.dataset.name)); } catch (e) { toast(e.message, 'bad'); return; }
  const s = d.source;
  openModal(`Voice: ${d.name}`, `<div class="stack">
    ${d.problem ? `<div class="notice warn">${esc(d.problem)}</div>` : ''}
    ${d.server ? `<div><div class="menu-title">On the speech server</div>${audMetaHtml(d.server)}</div>` : ''}
    ${s ? `<div><div class="menu-title">Kept here (for renewing)</div><dl class="aud-meta">
      <dt>Cloned by</dt><dd>${esc(s.createdBy)}</dd><dt>Clips</dt><dd>${esc(s.clips.join(', '))}</dd>
      <dt>Settings</dt><dd>trim_silence_db ${s.settings.trimSilenceDb ?? 'off'} · normalize ${s.settings.normalize ? 'on' : 'off'} · gpt_cond_len ${s.settings.gptCondLen} · gpt_cond_chunk_len ${s.settings.gptCondChunkLen} · max_ref_length ${s.settings.maxRefLength}</dd>
      <dt>Uploaded</dt><dd>${esc(new Date(s.uploadedUtc).toLocaleString())}</dd><dt>Deleted by the server</dt><dd>${esc(new Date(s.expiresUtc).toLocaleString())} (${expiryText(s.expiresUtc)})</dd></dl></div>`
      : '<p class="muted small">Its clips are not kept here (it was cloned elsewhere), so it cannot be renewed from this page.</p>'}</div>`,
    `${s && s.mine ? `<button class="btn" data-act="aud-voice-renew" data-name="${esc(d.name)}">Renew</button>` : ''}<button class="btn primary" data-act="modal-close">Close</button>`);
};
ACTIONS['aud-voice-renew'] = async el => {
  const b = el; b.disabled = true;
  try {
    const r = await post(`/api/audio/voices/${enc(el.dataset.name)}/renew`, {});
    toast(`Renewed: the server keeps ${el.dataset.name} until ${new Date(r.source.expiresUtc).toLocaleDateString()}`, 'ok');
    closeModal();
    await loadVoice(true);
    if (AUD.tab === 'voices') audRender();
  } catch (e) { toast(e.message, 'bad'); b.disabled = false; }
};
ACTIONS['aud-voice-delete'] = async el => {
  const name = el.dataset.name;
  if (!await confirmBox(`Delete the voice “${name}”?`, 'It is deleted from the speech server, with the clips kept here. Its personality stays until you remove it.', { confirm: 'Delete', danger: true })) return;
  try { await del('/api/audio/voices/' + enc(name)); } catch (e) { toast(e.message, 'bad'); return; }
  const wasMine = currentVoice() === 'voice:' + name;
  await loadVoice(true);
  if (wasMine) await setVoice('speaker:' + VOICE.info.defaults.speaker).catch(() => { });
  audRender();
  toast(`Deleted: ${name}`, 'ok');
};

// ------------------------------------------------------------------ Personalities
function audPersonaPageHtml() {
  const key = AUD.pvoice || currentVoice();
  AUD.pvoice = key;
  return `<div class="aud-persona">
    <aside class="aud-persona-list">${card('Voices', `<input id="aud-pq" placeholder="Search voices" value="${esc(AUD.pq)}" autocomplete="off" aria-label="Search voices">
      <div class="aud-plist" id="aud-plist">${audPersonaListHtml()}</div>`, { flush: true })}</aside>
    <div class="aud-persona-main">${card('', `<div id="aud-persona">${audPersonaHtml(key)}</div>`)}</div>
  </div>`;
}
function audPersonaListHtml() {
  const q = AUD.pq.trim().toLowerCase(), cur = currentVoice();
  const keys = allVoices().filter(k => !q || voiceKeyName(k).toLowerCase().includes(q));
  // Voices with a personality first.
  keys.sort((a, b) => (voiceProfile(b) ? 1 : 0) - (voiceProfile(a) ? 1 : 0));
  return keys.map(k => `<button type="button" class="aud-pitem${k === AUD.pvoice ? ' on' : ''}" data-act="persona-open" data-key="${esc(k)}">
      <span class="grow">${esc(voiceKeyName(k))}</span>${voiceIsCloned(k) ? '<span class="voice-tag">cloned</span>' : ''}${voiceProfile(k) ? '<span class="voice-tag">personality</span>' : ''}${k === cur ? '<span class="voice-tag">yours</span>' : ''}</button>`).join('')
    || '<p class="muted small aud-none">No voice matches.</p>';
}
function audPersonaHtml(key) {
  const p = voiceProfile(key) || {}, s = p.settings || {}, d = VOICE.info.defaults;
  const models = AUD.models || [];
  const modelOpts = models.map(m => `<option value="${esc(m.id)}"${m.id === p.summaryModel ? ' selected' : ''}>${esc(m.name)}</option>`).join('') +
    (p.summaryModel && !models.some(m => m.id === p.summaryModel) ? `<option value="${esc(p.summaryModel)}" selected>${esc(p.summaryModel)}</option>` : '');
  const field = x => `<label>${esc(x.label)} <code>${x.k}</code><input type="number" class="persona-num" data-k="${x.k}" min="${x.min}" max="${x.max}" step="${x.step}" value="${s[x.k] !== undefined ? esc(s[x.k]) : ''}" placeholder="default"></label>`;
  return `<div class="persona-head"><h3>${esc(voiceKeyName(key))}</h3>${voiceIsCloned(key) ? '<span class="voice-tag">cloned</span>' : ''}<span class="spacer"></span>
      <button class="btn xs" data-act="voice-preview" data-key="${esc(key)}">${SPEAK_ICON} Hear</button>
      ${key === currentVoice() ? '<span class="voice-tag">your voice in Chat</span>' : `<button class="btn xs" data-act="persona-use" data-key="${esc(key)}">Use in Chat</button>`}</div>
    <label class="persona-prompt">Personality
      <span class="muted small">How this voice talks when it tells you the TL;DR of an answer in Dictation mode. Write it like directions for an actor: who it is, its attitude, how it speaks. Empty: ${esc(d.summaryPrompt)}</span>
      <textarea id="persona-prompt" rows="7" maxlength="8000" placeholder="${esc(d.summaryPrompt)}">${esc(p.summaryPrompt || '')}</textarea></label>
    <div class="row persona-ideas"><span class="muted small">Start from</span>${PERSONA_IDEAS.map((x, i) => `<button type="button" class="btn xs ghost" data-act="persona-idea" data-i="${i}">${esc(x[0])}</button>`).join('')}</div>
    <div class="form-grid">
      <label>Model that writes it<select id="persona-model"><option value="">${esc(d.summaryModel ? 'Default: ' + d.summaryModel : 'The answer\'s own model')}</option>${modelOpts}</select>
        <span class="muted small">Some models tone down or refuse a rough personality: pick one that plays along.</span></label>
      <label>Language<select id="persona-lang">${langOptionsHtml(s.language || '', 'Default (' + langName(d.language) + ')')}</select>
        <span class="muted small">It speaks, and writes its summaries, in this language.</span></label>
    </div>
    <details class="persona-speech"${Object.keys(s).some(k => k !== 'language') ? ' open' : ''}><summary>Speech settings for this voice <span class="muted small">(empty: the server's default)</span></summary>
      <div class="form-grid">${AUD_FIELDS.map(field).join('')}
        <label>Split into sentences <code>enable_text_splitting</code><select id="persona-split"><option value="">default (on)</option>
          <option value="1"${s.enable_text_splitting === true ? ' selected' : ''}>on</option><option value="0"${s.enable_text_splitting === false ? ' selected' : ''}>off</option></select></label></div></details>
    <div class="persona-try"><label>Try it on an answer<textarea id="persona-sample" rows="4">${esc(AUD.sample || PERSONA_SAMPLE)}</textarea></label>
      <div class="row"><button type="button" class="btn" data-act="persona-try" id="persona-try">${TLDR_ICON} Write and speak the TL;DR</button><span class="muted small" id="persona-status"></span></div>
      <div id="persona-out"></div></div>
    <div class="row persona-save"><button type="button" class="btn primary" data-act="persona-save">Save</button>
      ${voiceProfile(key) ? '<button type="button" class="btn ghost danger" data-act="persona-delete">Remove the personality</button>' : ''}</div>`;
}
/** What the editor holds now (saved or not). */
function audPersonaRead() {
  const settings = {};
  $$('.persona-num').forEach(n => { if (n.value !== '' && Number.isFinite(Number(n.value))) settings[n.dataset.k] = Number(n.value); });
  const lang = $('#persona-lang').value, split = $('#persona-split').value;
  if (lang) settings.language = lang;
  if (split) settings.enable_text_splitting = split === '1';
  return { voice: AUD.pvoice, summaryPrompt: $('#persona-prompt').value.trim() || null, summaryModel: $('#persona-model').value || null, settings };
}
function audPersonaAfter() {
  const q = $('#aud-pq');
  if (q) q.addEventListener('input', () => { AUD.pq = q.value; $('#aud-plist').innerHTML = audPersonaListHtml(); });
  const sample = $('#persona-sample');
  if (sample) sample.addEventListener('input', () => { AUD.sample = sample.value; });
}
ACTIONS['persona-open'] = el => {
  AUD.pvoice = el.dataset.key;
  $('#aud-persona').innerHTML = audPersonaHtml(AUD.pvoice);
  $('#aud-plist').innerHTML = audPersonaListHtml();
  audPersonaAfter();
};
ACTIONS['persona-idea'] = el => {
  const idea = PERSONA_IDEAS[Number(el.dataset.i)], box = $('#persona-prompt');
  if (!idea || !box) return;
  box.value = idea[1]; box.focus();
};
ACTIONS['persona-use'] = async el => {
  try { await setVoice(el.dataset.key); } catch (e) { toast(e.message, 'bad'); return; }
  $('#aud-persona').innerHTML = audPersonaHtml(AUD.pvoice);
  $('#aud-plist').innerHTML = audPersonaListHtml();
  audPersonaAfter();
};
ACTIONS['persona-try'] = async () => {
  const p = audPersonaRead(), text = ($('#persona-sample').value || '').trim(), status = t => { const s = $('#persona-status'); if (s) s.textContent = t; };
  if (!text) { toast('Write an answer to try it on.', 'bad'); return; }
  const btn = $('#persona-try'); btn.disabled = true;
  status('Writing the TL;DR…');
  try {
    // Without a model of its own, the model you chat with writes it (there is no answer here whose model could).
    const r = await post('/api/audio/summary', { text, voice: p.voice, summaryPrompt: p.summaryPrompt || undefined, summaryModel: p.summaryModel || undefined,
      language: p.settings.language || undefined, fallbackModel: LS.get('chat.model') || AUD.defaultModel || undefined });
    const out = $('#persona-out');
    if (out) out.innerHTML = `<div class="tldr"><div class="tldr-head">${TLDR_ICON}<b>TL;DR</b><span class="tldr-voice">${esc(voiceKeyName(p.voice))} · written by ${esc(r.model)}</span></div><div class="tldr-text">${esc(r.text)}</div></div>`;
    status('Speaking…');
    await streamSpeech(Object.assign({ text: r.text, voiceKey: p.voice, useProfile: false }, p.settings));
    status('');
  } catch (e) { toast(e.message, 'bad'); status(''); }
  finally { btn.disabled = false; }
};
ACTIONS['persona-save'] = async () => {
  const p = audPersonaRead();
  try { await audSaveProfile(p.voice, p.summaryPrompt, p.summaryModel, p.settings); } catch (e) { toast(e.message, 'bad'); return; }
  toast(`Saved: ${voiceKeyName(p.voice)}`, 'ok');
  $('#aud-persona').innerHTML = audPersonaHtml(AUD.pvoice);
  $('#aud-plist').innerHTML = audPersonaListHtml();
  audPersonaAfter();
};
ACTIONS['persona-delete'] = async () => {
  const key = AUD.pvoice;
  if (!await confirmBox(`Remove the personality of ${voiceKeyName(key)}?`, 'Its prompt, model and speech settings go; the voice itself stays.', { confirm: 'Remove', danger: true })) return;
  try { await del('/api/audio/profiles?voice=' + enc(key)); } catch (e) { toast(e.message, 'bad'); return; }
  VOICE.info.profiles = (VOICE.info.profiles || []).filter(x => x.voice !== key);
  $('#aud-persona').innerHTML = audPersonaHtml(key);
  $('#aud-plist').innerHTML = audPersonaListHtml();
  audPersonaAfter();
};

// ------------------------------------------------------------------ Dictation
function audDictationHtml() {
  const p = vmPrefs();
  const n = (id, label, value, min, max, step, hint) => `<label>${esc(label)}<input type="number" id="${id}" min="${min}" max="${max}" step="${step}" value="${value}"><span class="muted small">${esc(hint)}</span></label>`;
  return `<div class="stack">${card('Dictation mode', `<p class="muted small">In Chat, the headset button (or <code>/dictation</code>) starts a spoken conversation: it listens, sends what you said when you pause,
      and when the answer is done speaks a short summary of it in your voice's personality. Saying only "stop", "that's enough for now", "thanks, that's all" or "goodbye" ends it (nothing is sent). When the assistant asks a question, it reads it out with its options: say the answer or its number.</p>
    <div class="form-grid">
      ${n('pref-silence', 'Pause that ends your turn (ms)', p.silenceMs, 400, 6000, 100, 'How long you stay quiet before what you said is sent')}
      ${n('pref-words', 'Spoken summary length (words)', p.summaryWords, 10, 300, 5, 'About how long the TL;DR is')}
      ${n('pref-max', 'Longest turn (seconds)', p.maxTurnSeconds, 10, 600, 5, 'A turn is sent after this long even without a pause')}
      ${n('pref-nospeech', 'Stop listening after (seconds)', p.noSpeechSeconds, 5, 300, 5, 'With nothing said for this long, it pauses')}
      <label>Microphone sensitivity<input type="range" id="pref-sens" min="1" max="10" step="1" value="${p.sensitivity}"><span class="muted small">Higher hears quieter speech; lower ignores more background noise (recorded speech: desktop app, Firefox)</span></label>
    </div>
    <div class="check-grid">
      <label class="check"><input type="checkbox" id="pref-listen"${p.autoListen ? ' checked' : ''}><span><b>Listen again after speaking</b><span class="muted small">Off: press Talk for each turn</span></span></label>
      <label class="check"><input type="checkbox" id="pref-errors"${p.speakErrors ? ' checked' : ''}><span><b>Say when an answer fails</b><span class="muted small">Instead of staying silent</span></span></label>
    </div>
    <div class="row aud-settings-foot"><button class="btn primary" data-act="pref-save">Save</button></div>`)}</div>`;
}

// ------------------------------------------------------------------ Settings (administrators)
function audSettingsPageHtml() {
  const info = VOICE.info, sp = info.speakers || {};
  const keyState = info.keyInConfig ? 'set in the configuration file (TextToSpeech.ApiKey), which wins over the vault'
    : info.keySet ? `kept in the vault as <code>${esc(info.keySecret || '')}</code> (no agent may use it)` : '<b class="aud-bad">not set</b>';
  const server = card('Speech server', `<dl class="aud-meta">
      <dt>Server</dt><dd>${info.isAdmin ? `<code>${esc(info.baseUrl || '')}</code> <span class="muted small">(TextToSpeech.BaseUrl)</span>` : 'set up by an administrator'}</dd>
      <dt>Status</dt><dd>${info.ready ? `<span class="aud-status ok"><i></i>Answering</span> ${plural((sp.builtin || []).length, 'built-in voice')}, ${num((sp.voices || []).length)} cloned` : `<span class="aud-status bad"><i></i>${esc(info.problem || 'Not answering')}</span>`}</dd>
      ${info.isAdmin ? `<dt>API key</dt><dd>${keyState}</dd>` : ''}
      <dt>Languages</dt><dd>${(info.languages || []).map(c => esc(langName(c))).join(', ')}</dd>
      <dt>Kept clips</dt><dd>the newest ${num(info.defaults.maxClips)} per person; cloned voices are deleted by the server ${info.defaults.voiceDays} days after upload (renew them on Voices)</dd></dl>
    ${info.isAdmin && !info.keyInConfig ? `<div class="row aud-key"><input type="password" id="tts-key" placeholder="${info.keySet ? 'Replace the API key' : 'The X-API-Key of the speech server'}" autocomplete="new-password" aria-label="API key">
      <button class="btn primary" data-act="tts-key-save">Save key</button>${info.keySet && !info.keyInConfig ? '<button class="btn ghost danger" data-act="tts-key-remove">Remove</button>' : ''}</div>` : ''}
    <div class="row"><button class="btn" data-act="tts-check">Check the connection</button></div>`);
  return `<div class="stack">${audPricingCardHtml(info.pricing || {}, !!info.canSetPrices)}${server}</div>`;
}
ACTIONS['pref-save'] = async () => {
  const v = id => Number($('#' + id).value);
  try {
    VOICE.info.prefs = await put('/api/audio/prefs', {
      silenceMs: v('pref-silence'), summaryWords: v('pref-words'), maxTurnSeconds: v('pref-max'), noSpeechSeconds: v('pref-nospeech'), sensitivity: v('pref-sens'),
      autoListen: $('#pref-listen').checked, speakErrors: $('#pref-errors').checked
    });
  } catch (e) { toast(e.message, 'bad'); return; }
  toast('Dictation mode settings saved', 'ok');
};
async function audSetKey(apiKey) {
  let r;
  try { r = await put('/api/audio/key', { apiKey }); } catch (e) { toast(e.message, 'bad'); return; }
  if (!apiKey) toast('API key removed', 'ok');
  else if (r.ok) toast('API key saved: the speech server answers', 'ok');
  else toast('API key saved, but: ' + (r.problem || 'the speech server does not answer'), 'bad');
  await loadVoice(true);
  audRender();
}
ACTIONS['tts-key-save'] = () => {
  const k = ($('#tts-key').value || '').trim();
  if (!k) { toast('Paste the API key first.', 'bad'); return; }
  audSetKey(k);
};
ACTIONS['tts-key-remove'] = async () => {
  if (!await confirmBox('Remove the API key?', 'Speech stops working until a key is set again.', { confirm: 'Remove', danger: true })) return;
  audSetKey('');
};
ACTIONS['tts-check'] = async () => {
  try { await loadVoice(true); } catch (e) { toast(e.message, 'bad'); return; }
  toast(VOICE.info.ready ? 'The speech server answers' : VOICE.info.problem || 'The speech server does not answer', VOICE.info.ready ? 'ok' : 'bad');
  audRender();
};
