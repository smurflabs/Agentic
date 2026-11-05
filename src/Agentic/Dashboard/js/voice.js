'use strict';
/* Agentic console - Voice: speech from the server's XTTS voices (/api/audio). Read aloud speaks an answer in the chosen
   voice (the browser's own voices when the server has none); /voice picks the voice; Dictation mode is a spoken
   conversation: it listens for your turn, sends what you said, and when the answer is done a model writes a short spoken
   summary (TL;DR) in the voice's personality - shown on the answer and spoken - then it listens again. The Audio tab
   (audio.js) has every speech setting, cloned voices and the personalities. */

const VOICE = { info: null, loading: null };
const HEADSET_ICON = svgIcon('<path d="M4 14v-2a8 8 0 0 1 16 0v2"/><rect x="3" y="14" width="4" height="6" rx="1.5"/><rect x="17" y="14" width="4" height="6" rx="1.5"/>');
const TLDR_ICON = svgIcon('<path d="M4 6h16M4 11h10M4 16h7"/><path d="M17 14v6M14 17h6"/>');

/** /api/audio: whether speech works, the voices, languages, defaults, the user's voice and personalities. */
function loadVoice(force) {
  if (VOICE.info && !force) return Promise.resolve(VOICE.info);
  if (!VOICE.loading || force) {
    VOICE.loading = get('/api/audio' + (force ? '?refresh=1' : '')).then(d => { VOICE.info = d; return d; }, e => { VOICE.loading = null; throw e; });
  }
  return VOICE.loading;
}
/** The server's voices can speak (set up, with its key, and answering). */
function voiceUsable() { return !!(VOICE.info && VOICE.info.ready); }
const voiceKeyName = k => String(k || '').replace(/^(speaker|voice):/, '');
const voiceIsCloned = k => /^voice:/.test(k || '');
function currentVoice() {
  const i = VOICE.info;
  return (i && i.prefs && i.prefs.voice) || 'speaker:' + ((i && i.defaults && i.defaults.speaker) || 'Ana Florence');
}
/** Every voice's key: cloned voices first, then the built-in speakers. */
function allVoices() {
  const sp = (VOICE.info && VOICE.info.speakers) || {};
  return [...(sp.voices || []).map(n => 'voice:' + n), ...(sp.builtin || []).map(n => 'speaker:' + n)];
}
function voiceProfile(key) { return ((VOICE.info && VOICE.info.profiles) || []).find(p => p.voice === key) || null; }
/** The voice whose name matches q best (exact, then the start of a name, then anywhere in it). */
function findVoice(q) {
  const s = String(q || '').trim().toLowerCase().replace(/^(speaker|voice):/, '');
  const all = allVoices(), name = k => voiceKeyName(k).toLowerCase();
  return all.find(k => name(k) === s) || all.find(k => name(k).startsWith(s)) || all.find(k => name(k).includes(s)) || null;
}
async function apiErrorOf(res) {
  let m = res.statusText ? `${res.status} ${res.statusText}` : httpProblem(res.status);
  try { const j = await res.json(); if (j && j.error) m = j.error.message; } catch { /* not JSON */ }
  const e = new Error(m); e.status = res.status; return e;
}

// ------------------------------------------------------------------ playing speech as it arrives
/**
 * Speech plays through Web Audio while it streams in: raw 16-bit mono PCM at 24 kHz (or a WAV stream, whose header is
 * skipped), scheduled piece after piece. Only one speech plays at a time; stopAudio ends it.
 */
const SPEAKER = { ctx: null, out: null, ctrl: null, sources: new Set(), at: 0, gen: 0 };
const SPEECH_RATE = 24000;
function audioCtx() {
  if (!SPEAKER.ctx) {
    const AC = window.AudioContext || window.webkitAudioContext;
    if (!AC) throw new Error('This browser cannot play audio.');
    SPEAKER.ctx = new AC();
    // Every speech plays through one gain: the volume of every sound (user menu → Sounds → Volume), changed while it plays too.
    SPEAKER.out = SPEAKER.ctx.createGain();
    SPEAKER.out.gain.value = soundVolume() / 100;
    SPEAKER.out.connect(SPEAKER.ctx.destination);
  }
  if (SPEAKER.ctx.state === 'suspended') SPEAKER.ctx.resume().catch(() => { });
  return SPEAKER.ctx;
}
document.addEventListener('sound-volume', ev => { if (SPEAKER.out) SPEAKER.out.gain.value = ev.detail / 100; });
function stopAudio() {
  SPEAKER.gen++;
  if (SPEAKER.ctrl) { try { SPEAKER.ctrl.abort(); } catch { /* done */ } SPEAKER.ctrl = null; }
  for (const s of SPEAKER.sources) { try { s.stop(); } catch { /* ended */ } }
  SPEAKER.sources.clear();
  SPEAKER.at = 0;
}
function speechPlaying() { return !!SPEAKER.ctrl || (SPEAKER.ctx && SPEAKER.at > SPEAKER.ctx.currentTime + 0.05); }
/** Bytes still to skip at the start of a WAV stream: everything up to and including the data chunk's header. */
function wavHeaderLength(bytes) {
  if (bytes.length < 12) return -1;
  if (String.fromCharCode(...bytes.subarray(0, 4)) !== 'RIFF') return 0;
  let at = 12;
  while (at + 8 <= bytes.length) {
    const id = String.fromCharCode(...bytes.subarray(at, at + 4));
    const size = bytes[at + 4] | bytes[at + 5] << 8 | bytes[at + 6] << 16 | bytes[at + 7] << 24;
    if (id === 'data') return at + 8;
    at += 8 + (size >>> 0) + ((size >>> 0) % 2);
  }
  return -1;
}
/** Plays 16-bit PCM bytes (an even count) after what is already scheduled. */
function schedulePcm(ctx, pcm, node) {
  const n = pcm.length >> 1;
  if (!n) return null;
  const dv = new DataView(pcm.buffer, pcm.byteOffset, n * 2);
  const ab = ctx.createBuffer(1, n, SPEECH_RATE), ch = ab.getChannelData(0);
  for (let i = 0; i < n; i++) ch[i] = dv.getInt16(i * 2, true) / 32768;
  const src = ctx.createBufferSource();
  src.buffer = ab;
  src.connect(node || (ctx === SPEAKER.ctx && SPEAKER.out) || ctx.destination);
  // Fell behind (the network was slower than the speech): start right away instead of in the past.
  if (SPEAKER.at < ctx.currentTime + 0.02) SPEAKER.at = ctx.currentTime + 0.06;
  src.start(SPEAKER.at);
  SPEAKER.at += ab.duration;
  SPEAKER.sources.add(src);
  src.onended = () => SPEAKER.sources.delete(src);
  return src;
}
/**
 * While a speech request waits for the server (it makes one at a time; everyone else's requests wait in line here, first come
 * first served), tells onText where the user's first waiting request is ('' when none waits). Returns stop().
 */
function speechLineWatch(onText) {
  let stopped = false, timer = null;
  const tick = async () => {
    const q = await get('/api/audio/queue').catch(() => null);
    if (stopped) return;
    const mine = q && q.mine && q.mine[0];
    onText(mine ? `Waiting for the speech server: ${mine.position === 1 ? 'next in line' : `number ${mine.position} in line`}` : '');
    timer = setTimeout(tick, 1500);
  };
  timer = setTimeout(tick, 700);
  return () => { stopped = true; clearTimeout(timer); onText(''); };
}
/** The page-wide notice for a speech request waiting in line ('' hides it). */
function speechLineNotice(text) {
  let el = document.getElementById('speech-line');
  if (!text) { if (el) el.remove(); return; }
  if (!el) { el = document.createElement('div'); el.id = 'speech-line'; el.className = 'speech-line'; el.setAttribute('role', 'status'); document.body.appendChild(el); }
  el.textContent = text;
}
/** Speech always buffered before it plays (seconds), and the margin on the computed wait. */
const SPEECH_MIN_LEAD = 1, SPEECH_LEAD_MARGIN = 1.35;
/**
 * How the speech server did last time: seconds of speech made per second (rate) and characters per second of speech at
 * speed 1 (cps). Until it is known: a cautious 0.75 and 14 (a GTX 1660 Ti makes about 0.8 s a second, and speaks about
 * 16 characters a second).
 */
function speechPace() {
  try { const p = JSON.parse(localStorage.getItem('speech.pace') || 'null'); if (p && p.rate > 0 && p.cps > 0) return p; } catch { /* none */ }
  return { rate: 0.75, cps: 14 };
}
function rememberSpeechPace(rate, cps) {
  if (!(rate > 0 && cps > 0)) return;
  try { localStorage.setItem('speech.pace', JSON.stringify({ rate: Math.round(rate * 1000) / 1000, cps: Math.round(cps * 100) / 100 })); } catch { /* not kept */ }
}
/** About how long text takes to say (seconds). */
function speechEstimate(text, speed, cps = 14) { return String(text || '').length / cps / (speed > 0 ? speed : 1); }
/**
 * How much speech must be buffered before it plays (or plays again after running dry) so playback never catches up with
 * the server: when the GPU makes speech slower than it plays (rate < 1: seconds made per second), the rest (remaining
 * seconds) arrives remaining / rate seconds from now but plays in remaining seconds, so the buffer must cover the
 * difference, remaining × (1 / rate − 1), plus a margin. Infinity while the rate is not known yet.
 */
function speechLead(rate, remaining) {
  if (!(rate > 0)) return Infinity;
  if (rate >= 1.15) return SPEECH_MIN_LEAD;
  return SPEECH_MIN_LEAD + Math.max(0, remaining) * (1 / rate - 1) * SPEECH_LEAD_MARGIN;
}
/**
 * Speaks a /api/audio/stream request while it is made. Resolves when the last sound has played or it was stopped:
 * { stopped, bytes, firstMs, clipId, seconds, rebuffers }. The server's GPU may make speech slower than it plays, so the
 * first sound waits until enough is buffered for the rest to arrive in time (speechLead, from the rate measured as it
 * arrives and the text's length); should it run dry anyway, it buffers again the same way. opts: format (pcm | wav),
 * onstart(ms), onprogress(bytes), onbuffer({seconds, needed}) (buffering before the first sound or after running dry; a
 * page-wide notice by default), onqueue(text) (its place in line while the server is busy; a page-wide notice by default),
 * node (an AudioNode to play through, e.g. an analyser).
 */
async function streamSpeech(body, opts = {}) {
  stopAudio();
  const ctx = audioCtx(), gen = SPEAKER.gen, ctrl = new AbortController();
  SPEAKER.ctrl = ctrl;
  const t0 = performance.now(), fmt = opts.format === 'wav' ? 'wav' : 'pcm';
  const stopLine = speechLineWatch(opts.onqueue || speechLineNotice);
  let res;
  try {
    res = await fetch('/api/audio/stream?format=' + fmt, {
      method: 'POST', credentials: 'same-origin', signal: ctrl.signal,
      headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': S.csrf || '' }, body: JSON.stringify(body)
    });
  } catch (e) { if (gen !== SPEAKER.gen || e.name === 'AbortError') return { stopped: true, bytes: 0 }; throw e; }
  finally { stopLine(); }
  if (!res.ok) { if (SPEAKER.ctrl === ctrl) SPEAKER.ctrl = null; throw await apiErrorOf(res); }
  const clipId = res.headers.get('X-Clip-Id');
  const reader = res.body.getReader();
  let carry = new Uint8Array(0), skip = fmt === 'wav' ? -1 : 0, bytes = 0, firstMs = null;
  // Buffered until it can play without catching up with the server (speechLead).
  const onbuffer = opts.onbuffer || (b => speechLineNotice(b ? `Getting the speech ready: ${b.seconds.toFixed(1)} of ${isFinite(b.needed) ? b.needed.toFixed(1) : '…'} s` : ''));
  const pace = speechPace(), speed = Number(body && body.speed) || 1, chars = String((body && body.text) || '').length;
  const estimate = speechEstimate(body && body.text, speed, pace.cps);
  let pending = [], pendingSec = 0, playing = false, firstAt = 0, firstSec = 0, rebuffers = 0, lastAt = 0;
  const arrivals = [];   // [time, seconds received]: the rate of the last few seconds
  /**
   * The rate to plan with: the slowest of last time's, the average since the first sound and the last 3 s. The first
   * second runs fast (the first sentence comes in a burst), so the measured rates count only after 2 s.
   */
  const planRate = now => {
    let rate = pace.rate;
    const elapsed = (now - firstAt) / 1000, received = bytes / 2 / SPEECH_RATE;
    if (elapsed >= 2) rate = Math.min(rate, (received - firstSec) / elapsed);
    const old = arrivals.find(a => now - a[0] <= 3000);
    if (elapsed >= 2 && old && now - old[0] >= 1500) rate = Math.min(rate, (received - old[1]) / ((now - old[0]) / 1000));
    return rate;
  };
  const start = () => {
    SPEAKER.at = ctx.currentTime + 0.05;
    for (const p of pending) schedulePcm(ctx, p, opts.node);
    pending = []; pendingSec = 0; playing = true;
    onbuffer(null);
    if (firstMs === null) { firstMs = Math.round(performance.now() - t0); if (opts.onstart) opts.onstart(firstMs); }
  };
  try {
    for (; ;) {
      const { value, done } = await reader.read();
      if (gen !== SPEAKER.gen) { onbuffer(null); return { stopped: true, bytes, firstMs, clipId, rebuffers }; }
      if (done) break;
      let chunk = value;
      if (carry.length) { const joined = new Uint8Array(carry.length + chunk.length); joined.set(carry); joined.set(chunk, carry.length); chunk = joined; carry = new Uint8Array(0); }
      if (skip < 0) {
        const h = wavHeaderLength(chunk);
        if (h < 0) { carry = chunk; continue; }
        chunk = chunk.subarray(h); skip = 0;
      }
      const even = chunk.length - (chunk.length % 2);
      if (even < chunk.length) carry = chunk.slice(even);
      if (!even) continue;
      const pcm = chunk.slice(0, even), sec = even / 2 / SPEECH_RATE, now = performance.now();
      bytes += even;
      const received = bytes / 2 / SPEECH_RATE;
      if (!firstAt) { firstAt = now; firstSec = received; }
      lastAt = now;
      arrivals.push([now, received]);
      while (arrivals.length && now - arrivals[0][0] > 4000) arrivals.shift();
      // It caught up with the server after all: it buffers again before going on.
      if (playing && SPEAKER.at < ctx.currentTime + 0.05) { playing = false; rebuffers++; }
      if (playing) schedulePcm(ctx, pcm, opts.node);
      else {
        pending.push(pcm); pendingSec += sec;
        const remaining = received < estimate ? estimate - received : Math.max(1, received * 0.1);
        const needed = speechLead(planRate(now), remaining);
        if (pendingSec >= needed) start(); else onbuffer({ seconds: pendingSec, needed });
      }
      if (opts.onprogress) opts.onprogress(bytes);
    }
  } catch (e) {
    onbuffer(null);
    if (gen !== SPEAKER.gen || e.name === 'AbortError') return { stopped: true, bytes, firstMs, clipId, rebuffers };
    throw e;
  }
  if (SPEAKER.ctrl === ctrl) SPEAKER.ctrl = null;
  // Everything has arrived: what is still buffered plays now.
  if (!playing && pending.length) start();
  onbuffer(null);
  if (!bytes) throw new Error('No speech came back from the speech server.');
  // How the server did, for the next time (a few seconds of speech at least tell).
  const made = bytes / 2 / SPEECH_RATE, took = (lastAt - firstAt) / 1000;
  if (made >= 3 && took >= 2 && chars > 0) rememberSpeechPace((made - firstSec) / took, chars / (made * speed));
  // Done arriving: wait until the last piece has played.
  await new Promise(resolve => {
    const wait = () => {
      if (gen !== SPEAKER.gen) return resolve();
      const left = SPEAKER.at - ctx.currentTime;
      if (left <= 0.03) resolve(); else setTimeout(wait, Math.min(250, left * 1000));
    };
    wait();
  });
  return { stopped: gen !== SPEAKER.gen, bytes, firstMs, clipId, seconds: bytes / 2 / SPEECH_RATE, rebuffers };
}
/** Speaks text in a voice (the chosen one by default) with that voice's personality settings. */
function voiceSay(text, opts = {}) {
  return streamSpeech(Object.assign({ text, voiceKey: opts.voice || currentVoice() }, opts.settings || {}), opts);
}

// ------------------------------------------------------------------ the voice: /voice and its picker
async function setVoice(key) {
  const prefs = await put('/api/audio/prefs', { voice: key });
  if (VOICE.info) VOICE.info.prefs = prefs;
  toast('Voice: ' + voiceKeyName(key), 'ok');
  renderVoiceBar();
  document.dispatchEvent(new CustomEvent('voice:changed', { detail: { voice: key } }));
}
function voiceListHtml(q, cur) {
  const s = String(q || '').trim().toLowerCase();
  const keys = allVoices().filter(k => !s || voiceKeyName(k).toLowerCase().includes(s));
  const row = k => `<div class="voice-row${k === cur ? ' on' : ''}">
      <button type="button" class="voice-name" data-act="voice-choose" data-key="${esc(k)}">${k === cur ? icon('check') : '<i class="voice-dot"></i>'}<span>${esc(voiceKeyName(k))}</span>
        ${voiceProfile(k) && voiceProfile(k).summaryPrompt ? '<span class="voice-tag" data-tip="Has a personality: its spoken summaries sound its own way">personality</span>' : ''}</button>
      <button type="button" class="btn xs ghost icon-only" data-act="voice-preview" data-key="${esc(k)}" aria-label="Hear ${esc(voiceKeyName(k))}" data-tip="Hear it">${SPEAK_ICON}</button></div>`;
  const cloned = keys.filter(voiceIsCloned), builtin = keys.filter(k => !voiceIsCloned(k));
  return (cloned.length ? `<div class="menu-title">Cloned voices</div>${cloned.map(row).join('')}` : '') +
    (builtin.length ? `<div class="menu-title">Built-in voices</div>${builtin.map(row).join('')}` : '') || '<p class="muted small">No voice matches.</p>';
}
async function openVoicePicker(q) {
  try { await loadVoice(); } catch (e) { toast(e.message, 'bad'); return; }
  if (!voiceUsable()) { toast(VOICE.info.problem || 'The speech server is not answering.', 'bad'); return; }
  openModal('Voice', `<div class="voice-pick"><input id="voice-q" placeholder="Search ${num(allVoices().length)} voices" value="${esc(q || '')}" autocomplete="off" aria-label="Search voices">
    <div class="voice-list" id="voice-list">${voiceListHtml(q, currentVoice())}</div>
    <p class="muted small">It reads answers aloud and talks in Dictation mode. How each voice's spoken summaries sound (its personality) and its speech settings are on the <a href="/audio/personalities" data-nav>Audio tab</a>.</p></div>`,
    '<button class="btn primary" data-act="modal-close">Done</button>');
  const box = $('#voice-q');
  if (box) box.addEventListener('input', () => { const l = $('#voice-list'); if (l) l.innerHTML = voiceListHtml(box.value, currentVoice()); });
}
ACTIONS['voice-pick'] = () => openVoicePicker();
ACTIONS['voice-choose'] = async el => {
  try { await setVoice(el.dataset.key); } catch (e) { toast(e.message, 'bad'); return; }
  const l = $('#voice-list'), box = $('#voice-q');
  if (l) l.innerHTML = voiceListHtml(box ? box.value : '', currentVoice());
};
ACTIONS['voice-preview'] = el => {
  const key = el.dataset.key;
  voiceSay(`Hi, I'm ${voiceKeyName(key)}. This is how I sound.`, { voice: key }).catch(e => toast(e.message, 'bad'));
};
async function cmdVoice(a) {
  if (!a) { openVoicePicker(); return; }
  try { await loadVoice(); } catch (e) { toast(e.message, 'bad'); return; }
  if (!voiceUsable()) { toast(VOICE.info.problem || 'The speech server is not answering.', 'bad'); return; }
  const k = findVoice(a);
  if (!k) { openVoicePicker(a); return; }
  await setVoice(k);
}

// ------------------------------------------------------------------ the spoken summary (TL;DR) on an answer
function spokenHtml(m) {
  if (!m || !m.spoken) return '';
  return `<div class="tldr"><div class="tldr-head">${TLDR_ICON}<b>TL;DR</b>${m.spokenVoice ? `<span class="tldr-voice">${esc(voiceKeyName(m.spokenVoice))}</span>` : ''}<span class="spacer"></span>
    <button type="button" class="btn xs ghost icon-only" data-act="tldr-speak" aria-label="Speak the summary" data-tip="Speak it again">${SPEAK_ICON}</button></div>
    <div class="tldr-text">${esc(m.spoken)}</div></div>`;
}
function tldrSlot(messageId) {
  const node = $(`#chat-thread .msg.assistant[data-id="${CSS.escape(messageId)}"]`);
  return node ? $('.msg-body > .msg-actions', node) : null;
}
function showTldrPending(messageId) {
  const at = tldrSlot(messageId); if (!at) return;
  const node = at.closest('.msg');
  $$('.tldr', node).forEach(x => x.remove());
  at.before(htmlNode(`<div class="tldr pending"><div class="tldr-head">${TLDR_ICON}<b>TL;DR</b><span class="tldr-voice">writing the spoken summary…</span></div></div>`));
}
function hideTldrPending(messageId) { const at = tldrSlot(messageId); if (at) $$('.tldr.pending', at.closest('.msg')).forEach(x => x.remove()); }
/** Shows a new spoken summary on its answer (and keeps it with the message). */
function setSpoken(conv, messageId, text, voice) {
  const m = conv && conv.messages.find(x => x.id === messageId);
  if (m) { m.spoken = text; m.spokenVoice = voice; }
  const at = tldrSlot(messageId); if (!at) return;
  $$('.tldr', at.closest('.msg')).forEach(x => x.remove());
  const node = htmlNode(spokenHtml({ spoken: text, spokenVoice: voice }));
  at.before(node);
  if (!reducedMotion()) node.classList.add('arrive');
}
ACTIONS['tldr-speak'] = el => {
  const node = el.closest('.msg'), m = CHAT.conv && node && CHAT.conv.messages.find(x => x.id === node.dataset.id);
  if (!m || !m.spoken) return;
  if (el.classList.contains('on')) { stopAudio(); return; }
  el.classList.add('on');
  voiceSay(m.spoken, { voice: m.spokenVoice || currentVoice() }).catch(e => toast(e.message, 'bad')).finally(() => el.classList.remove('on'));
};
/** /tldr: the spoken summary of the last answer, written now and spoken. */
async function cmdTldr() {
  const m = lastAnswer();
  if (!m || !m.id || !CHAT.conv) { toast('There is no answer yet.', 'bad'); return; }
  try { await loadVoice(); } catch (e) { toast(e.message, 'bad'); return; }
  showTldrPending(m.id);
  let r;
  try { r = await post('/api/audio/summary', { chatId: CHAT.conv.id, messageId: m.id, voice: currentVoice() }); }
  catch (e) { hideTldrPending(m.id); toast('The spoken summary failed: ' + e.message, 'bad'); return; }
  setSpoken(CHAT.conv, m.id, r.text, r.voice);
  if (voiceUsable()) voiceSay(r.text, { voice: r.voice }).catch(e => toast(e.message, 'bad'));
}

// ------------------------------------------------------------------ Dictation mode
/**
 * A spoken conversation in the open chat. Each turn: listen (the browser's speech recognition where it works, else the
 * microphone is recorded until you pause and the server turns it into text), send it, wait for the answer, have the
 * spoken summary written, speak it, and listen again. VM.turn changes whenever the user steps in (Skip, Pause, End, a
 * new chat), so steps of an old turn stop where they are.
 */
const VM = { on: false, state: 'off', turn: 0, transcript: '', spoken: '', note: '', waiting: null, lastAnswered: null, endNow: null, rec: null, stream: null, source: null, analyser: null, noBrowser: false, level: 0, mine: false };
function vmPrefs() {
  const p = (VOICE.info && VOICE.info.prefs) || {}, d = (VOICE.info && VOICE.info.defaults) || {};
  return {
    silenceMs: p.silenceMs || 1400, maxTurnSeconds: p.maxTurnSeconds || 120, noSpeechSeconds: p.noSpeechSeconds || 30,
    summaryWords: p.summaryWords || d.summaryWords || 60, sensitivity: p.sensitivity || 5,
    autoListen: p.autoListen !== false, speakErrors: p.speakErrors !== false, showTranscript: p.showTranscript !== false
  };
}
function talkBtnHtml() {
  if (!CHAT.meta || !CHAT.meta.voice) return '';
  return `<button class="btn icon ghost talk-btn${VM.on ? ' on' : ''}" id="talk-btn" data-act="voice-mode" aria-label="Dictation mode" aria-pressed="${VM.on}"
    data-tip="${VM.on ? 'Dictation mode is on: click to end it' : 'Dictation mode: talk with the chat. It listens, sends what you said, and speaks a short summary of each answer'}">${HEADSET_ICON}</button>`;
}
function redrawTalkBtn() { const b = $('#talk-btn'); if (b) b.outerHTML = talkBtnHtml(); }
const VM_LABELS = { listening: 'Listening…', transcribing: 'Turning speech into text…', thinking: 'Answering…', summarizing: 'Writing the spoken summary…', speaking: 'Speaking…', paused: 'Paused' };
function renderVoiceBar(arrive) {
  const bar = $('#voice-bar'); if (!bar) return;
  if (!VM.on) { bar.classList.add('hidden'); bar.innerHTML = ''; return; }
  const st = VM.state, text = st === 'paused' ? VM.note : st === 'speaking' ? VM.spoken : VM.transcript;
  bar.innerHTML = `${HEADSET_ICON}<b class="goal-label">Dictation</b>
    <span class="goal-state vb-state ${esc(st)}"><i></i>${esc(VM_LABELS[st] || st)}</span>
    ${st === 'listening' ? '<span class="vb-level" aria-hidden="true"><i id="vb-level"></i></span>' : ''}
    <span class="vb-text" id="vb-text" title="${esc(text || '')}">${esc(text || '')}</span>
    <button type="button" class="vb-voice" data-act="voice-pick" data-tip="The voice: click to change it">${SPEAK_ICON}${esc(voiceKeyName(currentVoice()))}</button>
    ${st === 'listening' ? '<button type="button" class="btn xs" data-act="vm-send" data-tip="Send what you said now, without waiting for the pause">Send</button><button type="button" class="btn xs ghost" data-act="vm-pause" data-tip="Stop listening for now">Pause</button>' : ''}
    ${st === 'speaking' || st === 'summarizing' ? '<button type="button" class="btn xs" data-act="vm-skip" data-tip="Stop speaking and listen (Esc)">Skip</button>' : ''}
    ${st === 'paused' ? '<button type="button" class="btn xs primary" data-act="vm-talk" data-tip="Listen for what you say next">Talk</button>' : ''}
    <button type="button" class="goal-x" data-act="voice-mode" aria-label="End Dictation mode" data-tip="End Dictation mode">×</button>`;
  bar.classList.remove('hidden');
  if (arrive && !reducedMotion()) { bar.classList.remove('arrive'); void bar.offsetWidth; bar.classList.add('arrive'); }
}
function vmSetState(st, extra) { VM.state = st; if (extra) Object.assign(VM, extra); renderVoiceBar(); }
function vmShowTranscript() { const t = $('#vb-text'); if (t) { t.textContent = VM.transcript; t.title = VM.transcript; } }
function vmShowLevel(x) { const l = $('#vb-level'); if (l) l.style.transform = `scaleX(${Math.max(0.04, Math.min(1, x))})`; }
/** The chat page is gone (another page opened): Dictation mode ends. */
function vmGone() { return !document.getElementById('voice-bar'); }

ACTIONS['voice-mode'] = () => { if (VM.on) vmEnd(); else vmStart(); };
ACTIONS['vm-send'] = () => { if (VM.endNow) VM.endNow(); };
ACTIONS['vm-pause'] = () => vmPause('Paused: press Talk to go on.');
ACTIONS['vm-talk'] = () => vmListen();
ACTIONS['vm-skip'] = () => { stopAudio(); if (VM.state === 'summarizing') { VM.turn++; vmNext(VM.turn); } };

async function vmStart() {
  if (!CHAT.meta || !CHAT.meta.voice) { toast('Dictation mode needs the speech server: an administrator sets it up on the Audio tab.', 'bad'); return; }
  try { audioCtx(); } catch (e) { toast(e.message, 'bad'); return; }   // this click lets the page play sound
  try { await loadVoice(); } catch (e) { toast(e.message, 'bad'); return; }
  if (!voiceUsable()) { toast(VOICE.info.problem || 'The speech server is not answering.', 'bad'); return; }
  if (typeof DICTATION !== 'undefined' && DICTATION.active) stopDictation();
  if (typeof stopSpeaking === 'function') stopSpeaking();
  VM.on = true; VM.turn++; VM.transcript = ''; VM.spoken = ''; VM.lastAnswered = null;
  redrawTalkBtn();
  renderVoiceBar(true);
  // An answer is being written: its summary is spoken when it ends, then it listens.
  if (busyHere()) { VM.waiting = CHAT.conv.id; vmSetState('thinking'); return; }
  // A question from the model is waiting for the user: it is said first.
  if (vmQuestion()) { vmAsk(); return; }
  vmListen();
}
function vmEnd() {
  VM.on = false; VM.turn++;
  if (VM.endNow) { try { VM.endNow(true); } catch { /* ended */ } }
  if (VM.rec) { try { VM.rec.abort(); } catch { /* ended */ } VM.rec = null; }
  stopAudio();
  vmReleaseMic();
  VM.state = 'off'; VM.waiting = null;
  renderVoiceBar();
  redrawTalkBtn();
}
function vmPause(note) {
  if (!VM.on) return;
  VM.turn++;
  if (VM.endNow) { try { VM.endNow(true); } catch { /* ended */ } }
  if (VM.rec) { try { VM.rec.abort(); } catch { /* ended */ } VM.rec = null; }
  stopAudio();
  vmReleaseMic();
  vmSetState('paused', { note: note || '' });
}
function vmNext(turn) {
  if (turn !== VM.turn || !VM.on) return;
  if (vmGone()) { vmEnd(); return; }
  if (vmPrefs().autoListen) vmListen(); else vmPause('Press Talk when you are ready.');
}

async function vmListen() {
  if (!VM.on) return;
  if (vmGone()) { vmEnd(); return; }
  const turn = ++VM.turn;
  stopAudio();
  vmSetState('listening', { transcript: '' });
  let text = '';
  try { text = await captureTurn(turn); }
  catch (e) { if (turn === VM.turn) { toast(e.message, 'bad'); vmPause(e.message); } return; }
  if (turn !== VM.turn || !VM.on) return;
  text = String(text || '').trim();
  if (!text) { vmPause('Nothing was heard: press Talk to go on.'); return; }
  // "Stop", "that's enough for now", "thanks, that's all", "goodbye": the user is done talking. Nothing is sent; it says so and ends.
  if (vmIsGoodbye(text)) {
    vmEnd();
    toast('Dictation mode off', 'ok');
    voiceSay('Okay. Dictation is off.').catch(() => { /* the toast says it */ });
    return;
  }
  // The model asked a question (its card is open): what was said answers it.
  const asked = vmQuestion();
  if (asked) { vmAnswerQuestion(asked, text, turn); return; }
  if (busyHere()) { vmPause('An answer is still being written: wait for it, then press Talk.'); return; }
  VM.waiting = CHAT.conv ? CHAT.conv.id : '(new)';
  vmSetState('thinking', { transcript: text });
  // Sent by Dictation mode itself: its own chat:sending is not a send from elsewhere.
  VM.mine = true;
  let sending;
  try { sending = sendMessage(false, text); } finally { VM.mine = false; }
  try { await sending; } catch (e) { toast(e.message, 'bad'); }
  // Nothing was sent (the chat refused it, or the answer could not start): the turn waits for the user.
  if (turn === VM.turn && VM.on && VM.state === 'thinking' && !busyHere()) { VM.waiting = null; vmPause('The message was not sent: press Talk to try again.'); }
}

// ------------------------------------------------------------------ ending Dictation mode by voice
// What ends it when said on its own: a stop, or a goodbye. Polite words around it ("okay thanks, that's all for now") do not
// count; anything more ("stop the server", "that's all the tests?") is a message and is sent.
const VM_END = new Set([
  'stop', 'stop listening', 'stop dictation', 'stop dictation mode', 'stop voice mode', 'stop talking', 'stop here', 'lets stop', 'lets stop here',
  'we can stop', 'you can stop', 'end', 'end dictation', 'end dictation mode', 'end voice mode', 'end listening', 'end the conversation',
  'end conversation', 'end the chat', 'end chat', 'exit', 'exit dictation', 'quit', 'quit dictation', 'done', 'im done', 'i am done', 'were done',
  'we are done', 'all done', 'finished', 'im finished', 'i am finished', 'thats enough', 'that is enough', 'enough', 'thats all', 'that is all',
  'thats it', 'that is it', 'thatll be all', 'that will be all', 'thats everything', 'nothing else', 'nothing more', 'no more', 'goodbye',
  'good bye', 'bye', 'bye bye', 'see you', 'see you later', 'see ya', 'talk later', 'talk to you later', 'talk soon', 'good night', 'goodnight',
  'signing off', 'over and out', 'lets leave it there', 'leave it there', 'lets call it a day', 'call it a day', 'lets wrap up', 'lets end here'
]);
const VM_END_LEAD = ['thank you very much', 'thank you so much', 'thanks a lot', 'thank you', 'thanks', 'all right', 'alright', 'okay', 'ok', 'right', 'well',
  'so', 'great', 'good', 'perfect', 'cool', 'nice', 'awesome', 'cheers', 'nope', 'nah', 'no', 'yeah', 'yes', 'please', 'hey', 'um', 'uh', 'hmm', 'and'];
const VM_END_TAIL = ['thank you very much', 'thank you so much', 'thanks a lot', 'for the day', 'for tonight', 'for today', 'for now', 'for me', 'thank you',
  'thanks', 'goodbye', 'bye', 'please', 'then', 'now', 'here'];
function vmIsGoodbye(said) {
  let t = ' ' + String(said || '').toLowerCase().replace(/['’]/g, '').replace(/[^a-z0-9]+/g, ' ').trim() + ' ';
  for (let changed = true; changed;) {
    if (VM_END.has(t.trim())) return true;
    changed = false;
    for (const w of VM_END_LEAD) if (t.startsWith(' ' + w + ' ') && t.trim() !== w) { t = t.slice(w.length + 1); changed = true; break; }
    if (changed) continue;
    for (const w of VM_END_TAIL) if (t.endsWith(' ' + w + ' ') && t.trim() !== w) { t = t.slice(0, t.length - w.length - 1); changed = true; break; }
  }
  return false;
}

// ------------------------------------------------------------------ questions from the model in Dictation mode
/** The question card open in this chat (the model asked with ask_user), or null. */
function vmQuestion() { return typeof CHAT !== 'undefined' && CHAT.ask && CHAT.conv && CHAT.ask.convId === CHAT.conv.id ? CHAT.ask : null; }
/** What is said for the card's current question: the question, its options by number, and whether an own answer is fine. */
function vmQuestionSpeech(a) {
  const q = a.questions[a.step] || {}, opts = q.options || [];
  const head = a.questions.length > 1 ? `Question ${a.step + 1} of ${a.questions.length}. ` : '';
  const list = opts.length ? ' ' + opts.map((o, i) => `Option ${i + 1}: ${o}.`).join(' ') + (q.allowOther !== false ? ' Or say your own answer.' : '') : '';
  return head + String(q.question || '').trim() + list;
}
const VM_NUMBERS = { one: 1, first: 1, two: 2, second: 2, three: 3, third: 3, four: 4, fourth: 4, five: 5, fifth: 5 };
/** The option a spoken answer picks ("two", "option 2", "the second one", or an option said in full), else what was said. */
function vmPickOption(q, said) {
  const opts = (q && q.options) || [];
  const norm = s => String(s).toLowerCase().replace(/[^a-z0-9]+/g, ' ').trim();
  const t = norm(said);
  const exact = opts.find(o => norm(o) === t);
  if (exact) return exact;
  const m = /^(?:(?:go with|pick|choose|select)\s+)?(?:option|number|answer|the)?\s*(one|two|three|four|five|first|second|third|fourth|fifth|[1-5])(?:\s+(?:one|option|please))?$/.exec(t);
  const n = m ? (VM_NUMBERS[m[1]] || Number(m[1])) : 0;
  if (n && opts[n - 1]) return opts[n - 1];
  const named = opts.filter(o => norm(o).length > 2 && (' ' + t + ' ').includes(' ' + norm(o) + ' '));
  return named.length === 1 ? named[0] : String(said).trim();
}
/** Says the open question with its options, then listens for the answer. */
async function vmAsk() {
  const a = vmQuestion();
  if (!a || !VM.on) return false;
  const turn = ++VM.turn;
  stopAudio();
  const text = vmQuestionSpeech(a);
  vmSetState('speaking', { spoken: text });
  try { await voiceSay(text, { voice: currentVoice() }); } catch (e) { if (turn === VM.turn) toast('Speaking failed: ' + e.message, 'bad'); }
  vmNext(turn);
  return true;
}
/** A spoken answer to the open question card: it goes in the card like a click (the last one sends the answers). */
function vmAnswerQuestion(a, said, turn) {
  const answer = vmPickOption(a.questions[a.step], said);
  const last = a.step + 1 >= a.questions.length;
  if (last) { VM.waiting = CHAT.conv ? CHAT.conv.id : '(new)'; vmSetState('thinking', { transcript: answer }); }
  VM.mine = true;
  try { answerQuestion(answer); } finally { VM.mine = false; }
  if (!last) { vmAsk(); return; }
  // Nothing was sent after a while (the chat refused it): the turn waits for the user.
  setTimeout(() => { if (turn === VM.turn && VM.on && VM.state === 'thinking' && !busyHere()) { VM.waiting = null; vmPause('The answer was not sent: press Talk to try again.'); } }, 5000);
}
/** Stops what the current turn is doing (listening is thrown away, speech stops) without leaving Dictation mode. */
function vmInterrupt() {
  VM.turn++;
  if (VM.endNow) { try { VM.endNow(true); } catch { /* ended */ } }
  if (VM.rec) { try { VM.rec.abort(); } catch { /* ended */ } VM.rec = null; }
  stopAudio();
}
// A message sent some other way while Dictation mode listens or speaks (typed, an answer clicked in the question card):
// it stops listening, waits for that answer, and speaks its summary as usual.
document.addEventListener('chat:sending', ev => {
  if (!VM.on || VM.mine) return;
  vmInterrupt();
  VM.waiting = (ev.detail && ev.detail.convId) || (CHAT.conv ? CHAT.conv.id : '(new)');
  vmSetState('thinking', { transcript: '' });
});
// The question card moved on by a click (next question, back) or was skipped: say the new question, or listen as usual.
document.addEventListener('chat:question', ev => {
  if (!VM.on || VM.mine || busyHere()) return;
  vmInterrupt();
  if ((ev.detail || {}).kind === 'skip' || !vmQuestion()) vmListen(); else vmAsk();
});

/** The end of an answer in this chat (chat.js): its spoken summary is written and spoken. */
document.addEventListener('chat:answered', ev => {
  const d = ev.detail || {};
  if (!VM.on || !d.conv || !CHAT.conv || d.conv.id !== CHAT.conv.id) return;
  if (VM.waiting && VM.waiting !== '(new)' && VM.waiting !== d.conv.id) return;
  const id = d.message && d.message.id;
  if (id && VM.lastAnswered === id) return;
  VM.lastAnswered = id || null; VM.waiting = null;
  vmAfterAnswer(d);
});
async function vmAfterAnswer(d) {
  const turn = ++VM.turn, p = vmPrefs();
  if (d.aborted) { vmPause('Stopped: press Talk to go on.'); return; }
  // The answer ends with a question for the user: the question and its options are said (not a summary), then it listens
  // for the answer, which goes in the question card.
  if (vmQuestion()) { vmAsk(); return; }
  const m = d.message;
  let text = null, voice = currentVoice();
  if (m && m.id) {
    vmSetState('summarizing');
    showTldrPending(m.id);
    try {
      const r = await post('/api/audio/summary', { chatId: d.conv.id, messageId: m.id, voice, words: p.summaryWords });
      if (turn !== VM.turn) { hideTldrPending(m.id); return; }
      text = r.text; voice = r.voice || voice;
      setSpoken(d.conv, m.id, r.text, voice);
    } catch (e) {
      hideTldrPending(m.id);
      if (turn !== VM.turn) return;
      toast('The spoken summary failed: ' + e.message, 'bad');
      if (p.speakErrors) text = m.error ? 'The answer failed. ' + String(m.error).slice(0, 160) : 'The summary could not be written. The answer is on the screen.';
    }
  } else if (d.failure && p.speakErrors) text = 'The answer failed. ' + String(d.failure).slice(0, 160);
  if (turn !== VM.turn || !VM.on) return;
  if (text) {
    vmSetState('speaking', { spoken: text });
    try { await voiceSay(text, { voice }); } catch (e) { if (turn === VM.turn) toast('Speaking failed: ' + e.message, 'bad'); }
  }
  vmNext(turn);
}

// Listening for one turn: what the user said, '' when nothing was heard.
function captureTurn(turn) {
  // An app whose page cannot use the microphone (VS Code's webview) records it itself.
  if (window.chatClient && typeof window.chatClient.listen === 'function') return captureClient(turn);
  const SR = window.SpeechRecognition || window.webkitSpeechRecognition;
  if (SR && !window.chatClient && !VM.noBrowser) {
    return captureBrowser(SR, turn).catch(e => { if (e && e.fallback) { VM.noBrowser = true; return captureRecorded(turn); } throw e; });
  }
  return captureRecorded(turn);
}
/** The browser's speech recognition: the turn ends after a pause of silenceMs once something was said. */
function captureBrowser(SR, turn) {
  return new Promise((resolve, reject) => {
    const p = vmPrefs(), rec = new SR();
    rec.continuous = true; rec.interimResults = true; rec.lang = navigator.language || 'en-US';
    let finalText = '', interim = '', heard = false, done = false, silence = null;
    const finish = (err, discard) => {
      if (done) return; done = true;
      clearTimeout(silence); clearTimeout(noSpeech); clearTimeout(maxTimer); clearInterval(watch);
      if (VM.rec === rec) VM.rec = null;
      VM.endNow = null;
      try { rec.stop(); } catch { /* ended */ }
      if (err) reject(err); else resolve(discard ? '' : (finalText + ' ' + interim).trim());
    };
    const noSpeech = setTimeout(() => { if (!heard) finish(); }, p.noSpeechSeconds * 1000);
    const maxTimer = setTimeout(() => finish(), p.maxTurnSeconds * 1000);
    const watch = setInterval(() => { if (turn !== VM.turn || vmGone()) finish(null, true); }, 250);
    rec.onresult = ev => {
      heard = true; interim = '';
      for (let i = ev.resultIndex; i < ev.results.length; i++) { const r = ev.results[i]; if (r.isFinal) finalText += r[0].transcript + ' '; else interim += r[0].transcript; }
      VM.transcript = (finalText + interim).trim(); vmShowTranscript();
      clearTimeout(silence); silence = setTimeout(() => finish(), p.silenceMs);
    };
    rec.onerror = ev => {
      if (ev.error === 'network' || ev.error === 'service-not-allowed' || ev.error === 'language-not-supported') { const e = new Error(ev.error); e.fallback = true; finish(e); }
      else if (ev.error === 'not-allowed') finish(new Error('The browser did not allow the microphone.'));
      else if (ev.error !== 'no-speech' && ev.error !== 'aborted') finish(new Error('Speech recognition stopped: ' + ev.error));
    };
    // Chrome ends recognition on its own after a while: it starts again until the turn is over.
    rec.onend = () => { if (done) return; if (turn !== VM.turn) { finish(null, true); return; } try { rec.start(); } catch { finish(); } };
    VM.rec = rec;
    VM.endNow = discard => finish(null, discard === true);
    try { rec.start(); } catch (e) { finish(e); }
  });
}
async function vmMic() {
  if (VM.stream && VM.stream.active && VM.analyser) return VM.stream;
  if (!navigator.mediaDevices || !window.MediaRecorder) throw new Error('Dictation mode needs a microphone and a browser that can record audio.');
  try { VM.stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true } }); }
  catch (e) { throw new Error('No access to the microphone: ' + e.message); }
  const ctx = audioCtx();
  VM.source = ctx.createMediaStreamSource(VM.stream);
  VM.analyser = ctx.createAnalyser();
  VM.analyser.fftSize = 1024;
  VM.source.connect(VM.analyser);
  return VM.stream;
}
/**
 * The app records the turn and has the server turn it into text (chatClient.listen), with the same pause, limits and
 * sensitivity as here; Send ends it now, Pause / End / Skip throw it away.
 */
async function captureClient(turn) {
  const p = vmPrefs(), c = window.chatClient;
  const stop = discard => { try { c.stopListening(!!discard); } catch { /* ended */ } };
  VM.endNow = discard => stop(discard === true);
  const watch = setInterval(() => { if (turn !== VM.turn || vmGone()) stop(true); }, 250);
  try {
    const r = await c.listen({
      silenceMs: p.silenceMs, maxTurnSeconds: p.maxTurnSeconds, noSpeechSeconds: p.noSpeechSeconds, sensitivity: p.sensitivity,
      language: (navigator.language || 'en').slice(0, 2),
      onLevel: x => vmShowLevel(x),
      onTranscribing: () => { if (turn === VM.turn) vmSetState('transcribing'); }
    });
    if (turn !== VM.turn) return '';
    const text = String((r && r.text) || '').trim();
    if (text) { VM.transcript = text; vmShowTranscript(); }
    return text;
  } finally { clearInterval(watch); VM.endNow = null; }
}
function vmReleaseMic() {
  if (window.chatClient && typeof window.chatClient.releaseMic === 'function') { try { window.chatClient.releaseMic(); } catch { /* gone */ } }
  if (VM.stream) VM.stream.getTracks().forEach(t => t.stop());
  try { if (VM.source) VM.source.disconnect(); } catch { /* gone */ }
  VM.stream = null; VM.source = null; VM.analyser = null;
}
/**
 * The microphone, recorded until the user pauses: loudness is measured every 50 ms against the room's noise (learned while
 * nobody speaks; Settings > sensitivity), and silenceMs of quiet after speech ends the turn. The server turns it into text.
 */
async function captureRecorded(turn) {
  const p = vmPrefs();
  const stream = await vmMic();
  if (turn !== VM.turn) return '';
  const type = ['audio/webm;codecs=opus', 'audio/webm', 'audio/ogg;codecs=opus', 'audio/mp4'].find(t => MediaRecorder.isTypeSupported(t)) || '';
  const media = new MediaRecorder(stream, type ? { mimeType: type } : undefined);
  const chunks = [];
  media.ondataavailable = e => { if (e.data && e.data.size) chunks.push(e.data); };
  const blob = await new Promise(resolve => {
    const buf = new Float32Array(VM.analyser.fftSize), started = performance.now();
    const factor = 1.5 + (10 - p.sensitivity) * 0.35, floorMin = 0.004 + (10 - p.sensitivity) * 0.002;
    let floor = 0.005, heard = false, lastVoice = 0, ended = false;
    const stop = discard => {
      if (ended) return; ended = true;
      clearInterval(tick); VM.endNow = null;
      media.onstop = () => resolve(!discard && heard ? new Blob(chunks, { type: (media.mimeType || 'audio/webm').split(';')[0] }) : null);
      try { media.stop(); } catch { resolve(null); }
    };
    const tick = setInterval(() => {
      if (turn !== VM.turn || vmGone() || !VM.analyser) { stop(true); return; }
      VM.analyser.getFloatTimeDomainData(buf);
      let sum = 0; for (let i = 0; i < buf.length; i++) sum += buf[i] * buf[i];
      const rms = Math.sqrt(sum / buf.length), now = performance.now(), threshold = Math.max(floorMin, floor * factor);
      vmShowLevel(rms / (threshold * 2));
      if (rms > threshold) { heard = true; lastVoice = now; }
      else if (!heard) floor = Math.min(0.05, floor * 0.95 + rms * 0.05);   // the room's noise, while nobody speaks
      if (heard && now - lastVoice > p.silenceMs) stop();
      else if (!heard && now - started > p.noSpeechSeconds * 1000) stop();
      else if (now - started > p.maxTurnSeconds * 1000) stop();
    }, 50);
    VM.endNow = discard => { if (discard !== true) heard = heard || chunks.length > 0; stop(discard === true); };
    media.start(250);
  });
  if (!blob || blob.size < 2000 || turn !== VM.turn) return '';
  vmSetState('transcribing');
  const ext = /ogg/.test(blob.type) ? 'ogg' : /mp4/.test(blob.type) ? 'm4a' : 'webm';
  const res = await fetch(`/api/chat/transcribe?name=speech.${ext}&language=${enc((navigator.language || 'en').slice(0, 2))}`, {
    method: 'POST', credentials: 'same-origin', body: blob, headers: { 'Content-Type': blob.type || 'application/octet-stream', 'X-CSRF-Token': S.csrf || '' }
  });
  if (!res.ok) throw await apiErrorOf(res);
  const data = await res.json().catch(() => null);
  if (data && data.text) { VM.transcript = data.text; vmShowTranscript(); }
  return (data && data.text) || '';
}

// Esc while speaking: skip to listening (unless a dialog or a menu is open).
document.addEventListener('keydown', ev => {
  if (!VM.on || ev.key !== 'Escape' || !$('#modal').classList.contains('hidden')) return;
  if (VM.state === 'speaking' || VM.state === 'summarizing') { ev.preventDefault(); ACTIONS['vm-skip'](); }
});
// Another chat opens: Dictation mode ends (it talks with one chat), and the chat's voice settings are read again.
document.addEventListener('chat:mounted', () => {
  if (VM.on) vmEnd();
  if (CHAT.meta && CHAT.meta.voice && !VOICE.info) loadVoice().catch(() => { });
});

// ------------------------------------------------------------------ commands
CHAT_COMMANDS.push(
  { name: 'voice', args: '[name]', group: 'Voice', desc: 'The voice that reads answers aloud and talks in Dictation mode (no name: pick one)', when: () => !!(CHAT.meta && CHAT.meta.voice), run: cmdVoice },
  { name: 'dictation', args: '[on | off]', group: 'Voice', desc: 'Dictation mode: talk with the chat, and hear a short spoken summary of each answer', when: () => !!(CHAT.meta && CHAT.meta.voice), run: a => { if (onOff(a, VM.on) !== VM.on) ACTIONS['voice-mode'](); } },
  { name: 'tldr', group: 'Voice', desc: 'Write and speak the spoken summary of the last answer', when: () => !!(CHAT.meta && CHAT.meta.voice), run: cmdTldr });
['voice', 'dictation', 'tldr'].forEach(n => EXTERNAL_COMMANDS.add(n));
