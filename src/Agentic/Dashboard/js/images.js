'use strict';
/* Agentic console - Images: pictures from the Stable Diffusion 1.5 server with everything it offers. Create: prompt and
   negative prompt (A1111 syntax, embeddings), model, VAE, scheduler, size, steps, guidance, guidance rescale, eta, clip
   skip, seed, up to 16 pictures, FreeU, LoRAs, img2img (strength), inpainting (a mask painted here or uploaded, invert,
   blur, padding), multi-ControlNet (model, image, preprocessor and its params, scale, start, end, guess mode) and the
   hires fix - queued as a job with its progress and live preview. Gallery: every picture kept, with its settings and PNG
   info, to reuse, refine or download. Tools: preprocessors (control maps) and what the server has. Settings
   (administrators only): prices and the server's key. */

const IMG = {
  info: null, tab: 'create', form: null, job: null, jobTimer: null, previewTimer: null, previewUrl: null, recent: [],
  gallery: [], gq: '', starred: false, more: false, init: null, mask: null, controls: [], view: null, pre: { image: null, map: null }
};
const IMG_FORM_KEY = 'images.form';
const IMG_DEFAULTS = {
  prompt: '', negative_prompt: '', model: '', vae: '', scheduler: 'dpmpp_2m', width: 512, height: 512, sizeFromInit: true, steps: 25, guidance_scale: 7.5,
  guidance_rescale: 0, eta: 0, clip_skip: 1, seed: -1, num_images: 1, freeu: false, strength: 0.75, invert_mask: false, mask_blur: 4, paddingOn: false,
  inpaint_padding: 32, guess_mode: false, hiresOn: false, hires: { scale: 2, upscaler: 'lanczos', steps: null, denoise: 0.5 }, loras: [], controlnets: []
};
const IMG_SIZES = [[512, 512], [512, 768], [768, 512], [640, 640], [576, 768], [768, 576], [768, 768]];
const IMG_SCHEDULERS = ['dpmpp_2m', 'dpmpp_2m_karras', 'dpmpp_sde_karras', 'euler_a', 'unipc', 'lcm', 'tcd'];
const IMG_VAES = ['default', 'ft-mse', 'ft-ema'];
const PRE_TO_NET = { canny: 'canny', depth: 'depth', depth_zoe: 'depth', openpose: 'openpose', openpose_full: 'openpose', lineart: 'lineart', lineart_coarse: 'lineart',
  lineart_anime: 'lineart_anime', softedge_hed: 'softedge', softedge_pidi: 'softedge', scribble_hed: 'scribble', scribble_pidi: 'scribble', mlsd: 'mlsd', normalbae: 'normalbae',
  shuffle: 'shuffle', inpaint: 'inpaint' };

function imgForm() {
  if (!IMG.form) {
    let saved = null;
    try { saved = JSON.parse(LS.get(IMG_FORM_KEY) || 'null'); } catch { /* unreadable */ }
    IMG.form = Object.assign(JSON.parse(JSON.stringify(IMG_DEFAULTS)), saved || {});
    IMG.form.hires = Object.assign({}, IMG_DEFAULTS.hires, (saved && saved.hires) || {});
    IMG.controls = IMG.form.controlnets.map(() => null);
  }
  return IMG.form;
}
function imgSaveForm() { LS.set(IMG_FORM_KEY, JSON.stringify(IMG.form)); }

/** Names in /options under any of these keys: a list of names, a list of objects with a name, or an object keyed by name. */
function sdOptions(...keys) {
  const o = (IMG.info && IMG.info.options) || {};
  for (const k of keys) {
    const v = o[k];
    if (Array.isArray(v)) return v.map(x => typeof x === 'string' ? x : x && (x.name || x.id || x.stem || x.file)).filter(Boolean);
    if (v && typeof v === 'object') return Object.keys(v);
  }
  return [];
}
const sdModels = () => [...new Set(['sd15', 'sd15-inpaint', ...sdOptions('models', 'checkpoints', 'model')])];
const sdVaes = () => [...new Set([...IMG_VAES, ...sdOptions('vaes', 'vae')])];
const sdLoras = () => sdOptions('loras', 'lora');
const sdEmbeddings = () => sdOptions('embeddings', 'textual_inversions', 'embedding');
const sdNets = () => [...new Set([...((IMG.info && IMG.info.known && IMG.info.known.controlnets) || []), ...sdOptions('controlnets', 'controlnet')])];
const sdPreprocessors = () => [...new Set([...((IMG.info && IMG.info.known && IMG.info.known.preprocessors) || []), ...sdOptions('preprocessors', 'preprocessor')])];
const sdSchedulers = () => { const s = sdOptions('schedulers', 'samplers', 'scheduler'); return s.length ? s : IMG_SCHEDULERS; };
const datalist = (id, items) => `<datalist id="${id}">${items.map(x => `<option value="${esc(x)}"></option>`).join('')}</datalist>`;

function imgLoad(force) { return get('/api/images' + (force ? '?refresh=1' : '')).then(d => { IMG.info = d; return d; }); }

// ------------------------------------------------------------------ prices
const imgPrices = () => (IMG.info && IMG.info.pricing) || {};
const imgSurcharge = () => 1 + (((IMG.info && IMG.info.account) || {}).surchargePercent || 0) / 100;
function imgPriced() { const p = imgPrices(); return !!(p.perImage || p.perGpuMinute); }
/** About how long the GPU works on a request (as the server estimates it for the balance check). */
function imgEstimateMs(b) {
  const w = b.width || 512, h = b.height || 512, n = b.num_images || 1;
  let steps = b.steps || 25;
  if (b.init_image) steps *= b.strength || 0.75;
  const perStep = 400 * w * h / (512 * 512);
  let total = steps * perStep;
  if (b.hires) total += (b.hires.steps || b.steps || 25) * (b.hires.denoise || 0.5) * perStep * (b.hires.scale || 2) ** 2;
  return total * n;
}
function imgCostMicros(images, gpuMs, surcharge = true) {
  const p = imgPrices();
  return Math.ceil(((p.perImage || 0) * 1e6 * images + gpuMs / 60000 * (p.perGpuMinute || 0) * 1e6) * (surcharge ? imgSurcharge() : 1));
}
function imgCostText() {
  if (!imgPriced()) return '';
  const b = imgRequest(true), ms = imgEstimateMs(b), charged = ((IMG.info && IMG.info.account) || {}).charged;
  return `${charged ? 'About' : 'Users pay about'} ${money(imgCostMicros(b.num_images || 1, ms, charged))} · about ${Math.max(1, Math.round(ms / 1000))} s of GPU`;
}
function imgPricingCardHtml(p, edit) {
  const field = (id, label, value, hint) => `<label>${esc(label)}<input type="number" id="${id}" min="0" step="any" value="${value ?? ''}" placeholder="free"${edit ? '' : ' disabled'}>
    <span class="muted small">${esc(hint)}</span></label>`;
  const source = !p.source || p.source === 'config' ? 'Starting prices from the configuration (StableDiffusion)' : `Set by ${p.source.replace(/^admin:/, '')} ${ago(p.effectiveFromUtc)}`;
  const example = imgCostMicros(4, 60000, false);
  return card('Picture pricing', `<p class="muted small">What Stable Diffusion pictures cost User accounts: each picture made, and each minute the GPU worked on the job. Administrators are never charged; a cancelled or failed job is not charged.</p>
    <div class="form-grid">${field('sdprice-image', 'USD per picture', p.perImage, 'Each picture a job makes')}
      ${field('sdprice-minute', 'USD per GPU minute', p.perGpuMinute, 'The time the server worked on the job')}</div>
    <p class="small">${example ? `4 pictures in a minute of GPU: <b>${money(example)}</b> (before any surcharge)` : 'Pictures are free.'}</p>
    <div class="row img-foot">${edit ? '<button class="btn primary" data-act="img-price-save">Save prices</button>' : ''}<span class="muted small">${esc(source)}</span></div>`, { cls: 'section' });
}
ACTIONS['img-price-save'] = async () => {
  const v = id => { const x = ($('#' + id) || {}).value; return x === '' || x === undefined ? null : Number(x); };
  let p;
  try { p = await put('/api/images/pricing', { perImage: v('sdprice-image'), perGpuMinute: v('sdprice-minute') }); } catch (e) { toast(e.message, 'bad'); return; }
  if (IMG.info) IMG.info.pricing = p;
  toast('Picture prices saved', 'ok');
  if ($('#img-body')) imgRender(); else if (typeof refreshNow === 'function') refreshNow();
};

// ------------------------------------------------------------------ page
const IMG_TABS = [['create', 'Create'], ['gallery', 'Gallery'], ['tools', 'Tools'], ['settings', 'Settings']];
/** The tabs this account sees: Settings is for administrators. */
const imgTabs = info => IMG_TABS.filter(([k]) => k !== 'settings' || info.isAdmin);
function imgStatusHtml(info) {
  if (!info.available) return '<span class="aud-status bad"><i></i>No Stable Diffusion server</span>';
  if (!info.ready) return `<span class="aud-status bad" data-tip="${esc(info.problem || '')}"><i></i>Not answering</span>`;
  const h = info.health || {}, model = h.model || h.active_model || h.loaded_model, q = h.queue ?? h.queue_length ?? h.queued;
  return `<span class="aud-status ok" data-tip="${esc(info.baseUrl || 'The Stable Diffusion server')}"><i></i>Ready${model ? ' · ' + esc(String(model)) : ''}${q !== undefined && q !== null ? ' · queue ' + esc(String(q)) : ''}</span>`;
}
function imgBodyHtml() {
  const info = IMG.info;
  if (!info.ready && IMG.tab !== 'settings') {
    return `<div class="notice warn aud-notice">${esc(info.problem || 'The Stable Diffusion server is not answering.')}${info.isAdmin ? ' <a href="/images/settings" data-nav>Open Settings</a>' : ''}</div>`;
  }
  switch (IMG.tab) {
    case 'gallery': return imgGalleryHtml();
    case 'tools': return imgToolsHtml();
    case 'settings': return imgSettingsHtml();
    default: return imgCreateHtml();
  }
}
function imgRender() {
  const body = $('#img-body'); if (!body) return;
  const head = $('.page-head .actions'); if (head) head.innerHTML = imgStatusHtml(IMG.info);
  body.innerHTML = imgBodyHtml();
  applyPermissions(body);
  imgAfter();
}
VIEWS.images = {
  live: false,
  async render(params) {
    IMG.tab = (params.args && params.args[0]) || 'create';
    setCrumbs([['Images', '/images']]);
    let info;
    try { info = await imgLoad(); } catch (e) { return pageHead('Images') + card('', emptyState('Images are not available', e.message)); }
    if (!info.available) {
      return pageHead('Images', { actions: imgStatusHtml(info) }) + card('', emptyState('No Stable Diffusion server is set up', info.isAdmin
        ? 'Set StableDiffusion.BaseUrl (e.g. http://192.168.1.30:8000) in the configuration and restart; then set its API key here under Settings.'
        : 'An administrator sets up the Stable Diffusion server.'));
    }
    if (!imgTabs(info).some(([k]) => k === IMG.tab)) IMG.tab = 'create';
    if (info.ready) {
      if (IMG.tab === 'create') {
        IMG.recent = await get('/api/images/jobs?limit=12').catch(() => []);
        const live = IMG.recent.find(j => !IMG_FINISHED.has(j.status));
        // The job shown before (if still listed) with its state as it is now, else the one being made.
        IMG.job = live || (IMG.job && IMG.recent.find(j => j.id === IMG.job.id)) || null;
        if (IMG.job) IMG.jobImages = await get('/api/images/gallery?job=' + enc(IMG.job.id)).catch(() => []);
      }
      if (IMG.tab === 'gallery') await imgLoadGallery();
    }
    return pageHead('Images', {
      subtitle: 'Pictures from the Stable Diffusion 1.5 server: text to image, image to image, inpainting, LoRAs, ControlNet and hires fix, kept in a gallery.',
      actions: imgStatusHtml(info)
    }) + `<div class="tabs">${imgTabs(info).map(([k, l]) => `<a href="/images${k === 'create' ? '' : '/' + k}" data-nav class="${IMG.tab === k ? 'active' : ''}">${l}</a>`).join('')}</div>
      <div id="img-body">${imgBodyHtml()}</div>`;
  },
  after() { imgAfter(); }
};
function imgAfter() {
  if (IMG.tab === 'create') imgCreateAfter();
  else if (IMG.tab === 'gallery') imgGalleryAfter();
  else if (IMG.tab === 'tools') imgToolsAfter();
  if (IMG.job && !['done', 'failed', 'cancelled'].includes(IMG.job.status)) imgFollow();
}

// ------------------------------------------------------------------ inputs (init image, mask, control images)
/** A picture for a request: a data URL from a file, or a gallery picture ("gallery:{id}"). */
async function imgFromFile(file) {
  if (!/^image\//.test(file.type)) throw new Error(`${file.name} is not a picture.`);
  const maxMb = (IMG.info && IMG.info.defaults && IMG.info.defaults.maxInputImageMb) || 20;
  if (file.size > maxMb * 1048576) throw new Error(`${file.name} is larger than ${maxMb} MB.`);
  const url = await new Promise((res, rej) => { const r = new FileReader(); r.onload = () => res(r.result); r.onerror = () => rej(r.error); r.readAsDataURL(file); });
  const size = await imgSize(url);
  return { ref: url, url, name: file.name, w: size.w, h: size.h };
}
function imgFromGallery(g) { return { ref: 'gallery:' + g.id, url: `/api/images/gallery/${enc(g.id)}/file`, name: `gallery picture (seed ${g.seed ?? '?'})`, w: g.width, h: g.height }; }
function imgSize(url) { return new Promise(res => { const i = new Image(); i.onload = () => res({ w: i.naturalWidth, h: i.naturalHeight }); i.onerror = () => res({ w: 0, h: 0 }); i.src = url; }); }
function imgPickFile() {
  return new Promise(resolve => {
    const inp = document.createElement('input'); inp.type = 'file'; inp.accept = 'image/*';
    inp.onchange = async () => { const f = inp.files && inp.files[0]; if (!f) return resolve(null); try { resolve(await imgFromFile(f)); } catch (e) { toast(e.message, 'bad'); resolve(null); } };
    inp.click();
  });
}
/** Lets the user choose a gallery picture (resolves null when closed). */
async function imgPickGallery() {
  let list;
  try { list = await get('/api/images/gallery?limit=120'); } catch (e) { toast(e.message, 'bad'); return null; }
  if (!list.length) { toast('The gallery is empty.', 'bad'); return null; }
  return new Promise(resolve => {
    IMG.pickResolve = resolve;
    openModal('Choose a picture', `<div class="img-pick">${list.map(g => `<button type="button" class="img-thumb" data-act="img-pick-one" data-id="${esc(g.id)}" data-tip="${esc(g.prompt.slice(0, 120))}">
      <img src="/api/images/gallery/${enc(g.id)}/file" alt="" loading="lazy"></button>`).join('')}</div>`, '<button class="btn" data-act="modal-close">Cancel</button>', 'wide');
    IMG.pickList = list;
  });
}
ACTIONS['img-pick-one'] = el => {
  const g = (IMG.pickList || []).find(x => x.id === el.dataset.id), r = IMG.pickResolve;
  IMG.pickResolve = null; closeModal();
  if (r) r(g ? imgFromGallery(g) : null);
};
document.addEventListener('mousedown', ev => { if (IMG.pickResolve && ev.target.id === 'modal') { const r = IMG.pickResolve; IMG.pickResolve = null; r(null); } });
function imgSlotHtml(kind, item, label, actions) {
  return `<div class="img-slot${item ? ' has' : ''}" data-slot="${kind}">
    ${item ? `<img src="${esc(item.url)}" alt="">` : `<div class="img-slot-empty">${esc(label)}<span class="muted small">drop a picture here</span></div>`}
    <div class="img-slot-acts"><button type="button" class="btn xs" data-act="img-slot-file" data-slot="${kind}">Upload</button>
      <button type="button" class="btn xs" data-act="img-slot-gallery" data-slot="${kind}">From gallery</button>${actions || ''}
      ${item ? `<button type="button" class="btn xs ghost" data-act="img-slot-clear" data-slot="${kind}">Remove</button>` : ''}</div>
    ${item && item.w ? `<div class="muted small">${esc(item.name)} · ${item.w}×${item.h}</div>` : ''}</div>`;
}
function imgSetSlot(kind, item) {
  if (kind === 'pre') { IMG.pre.image = item; imgRender(); return; }
  if (kind === 'init') { IMG.init = item; if (!item) IMG.mask = null; }
  else if (kind === 'mask') IMG.mask = item;
  else if (kind.startsWith('cn')) IMG.controls[Number(kind.slice(2))] = item;
  imgRedrawCreate();
}
ACTIONS['img-slot-file'] = async el => { const item = await imgPickFile(); if (item) imgSetSlot(el.dataset.slot, item); };
ACTIONS['img-slot-gallery'] = async el => { const item = await imgPickGallery(); if (item) imgSetSlot(el.dataset.slot, item); };
ACTIONS['img-slot-clear'] = el => imgSetSlot(el.dataset.slot, null);

// ------------------------------------------------------------------ Create
function imgCreateHtml() {
  const f = imgForm(), init = IMG.init;
  const num = (k, label, min, max, step, hint, val) => `<label class="img-num">${esc(label)} <code>${k}</code>
    <span class="img-num-row"><input type="range" data-f="${k}" min="${min}" max="${max}" step="${step}" value="${esc(val ?? f[k])}"><input type="number" data-f="${k}" min="${min}" max="${max}" step="${step}" value="${esc(val ?? f[k])}"></span>
    ${hint ? `<span class="muted small">${esc(hint)}</span>` : ''}</label>`;
  const check = (k, label, hint) => `<label class="check"><input type="checkbox" data-f="${k}"${f[k] ? ' checked' : ''}><span><b>${esc(label)}</b> <code>${k}</code>${hint ? `<span class="muted small">${esc(hint)}</span>` : ''}</span></label>`;
  const emb = sdEmbeddings();
  const loras = f.loras.map((l, i) => `<div class="img-row"><input list="sd-loras" data-lora="${i}" data-k="name" value="${esc(l.name || '')}" placeholder="file stem in loras/ or HF repo id" aria-label="LoRA">
      <input type="range" data-lora="${i}" data-k="weight" min="-4" max="4" step="0.05" value="${esc(l.weight ?? 1)}" aria-label="Weight"><input type="number" data-lora="${i}" data-k="weight" min="-4" max="4" step="0.05" value="${esc(l.weight ?? 1)}" class="img-w" aria-label="Weight">
      <button type="button" class="btn xs ghost icon-only" data-act="img-lora-remove" data-i="${i}" aria-label="Remove">${icon('x')}</button></div>`).join('');
  const nets = f.controlnets.map((c, i) => `<div class="img-cn">
      <div class="img-cn-head"><b>ControlNet ${i + 1}</b><span class="spacer"></span><button type="button" class="btn xs ghost icon-only" data-act="img-cn-remove" data-i="${i}" aria-label="Remove">${icon('x')}</button></div>
      <div class="img-cn-grid">
        <div>${imgSlotHtml('cn' + i, IMG.controls[i], init ? 'Control image (empty: the init image)' : 'Control image', `<button type="button" class="btn xs" data-act="img-cn-preview" data-i="${i}" data-tip="Run the preprocessor and see the control map">Preview map</button>`)}</div>
        <div class="form-grid img-cn-fields">
          <label>Model <code>model</code><input list="sd-nets" data-cn="${i}" data-k="model" value="${esc(c.model || '')}" placeholder="canny, depth, a file in controlnets/, or HF id"></label>
          <label>Preprocessor <code>preprocessor</code><select data-cn="${i}" data-k="preprocessor"><option value="">none: the image is a control map</option>
            ${sdPreprocessors().map(p => `<option value="${esc(p)}"${p === c.preprocessor ? ' selected' : ''}>${esc(p)}</option>`).join('')}</select></label>
          <label>Scale <code>scale</code><input type="number" data-cn="${i}" data-k="scale" min="0" max="2" step="0.05" value="${esc(c.scale ?? 1)}"></label>
          <label>Start <code>start</code><input type="number" data-cn="${i}" data-k="start" min="0" max="1" step="0.05" value="${esc(c.start ?? 0)}"></label>
          <label>End <code>end</code><input type="number" data-cn="${i}" data-k="end" min="0" max="1" step="0.05" value="${esc(c.end ?? 1)}"></label>
          <label class="full">Preprocessor params <code>preprocessor_params</code><input data-cn="${i}" data-k="params" value="${esc(c.params || '')}" placeholder='{"low_threshold": 50, "high_threshold": 200}'></label>
        </div></div><div class="img-cn-map" id="img-cn-map-${i}"></div></div>`).join('');
  return `<div class="img-create">
    <div class="img-form">
      ${card('Prompt', `<label>Prompt <span class="muted small">A1111 syntax: (word:1.3), (word), [word], \\(, BREAK; no 77-token limit</span>
          <textarea id="img-prompt" data-f="prompt" rows="4" placeholder="a cozy cabin in a snowy forest, golden hour, (detailed:1.2)">${esc(f.prompt)}</textarea></label>
        <label>Negative prompt <code>negative_prompt</code><textarea data-f="negative_prompt" rows="2" placeholder="blurry, lowres, bad anatomy">${esc(f.negative_prompt)}</textarea></label>
        ${emb.length ? `<div class="row img-emb"><span class="muted small">Embeddings</span>${emb.map(e => `<button type="button" class="btn xs ghost" data-act="img-emb" data-name="${esc(e)}" data-tip="Add it to the negative prompt (Shift: to the prompt)">${esc(e)}</button>`).join('')}</div>` : ''}`)}
      ${card('Model and sampling', `<div class="form-grid">
          <label>Model <code>model</code><input list="sd-models" data-f="model" value="${esc(f.model)}" placeholder="${init && IMG.mask ? 'sd15-inpaint' : 'sd15'}"></label>
          <label>VAE <code>vae</code><input list="sd-vaes" data-f="vae" value="${esc(f.vae)}" placeholder="default"></label>
          <label>Scheduler <code>scheduler</code><select data-f="scheduler">${sdSchedulers().map(s => `<option value="${esc(s)}"${s === f.scheduler ? ' selected' : ''}>${esc(s)}</option>`).join('')}</select></label>
          <label>Seed <code>seed</code><span class="img-num-row"><input type="number" data-f="seed" value="${esc(f.seed)}" step="1" min="-1">
            <button type="button" class="btn xs ghost" data-act="img-seed-random" data-tip="Random (-1)">🎲</button></span><span class="muted small">-1 is random; picture i uses seed + i</span></label>
        </div>
        <div class="img-size"><span class="img-size-label">Size</span>
          ${init ? `<label class="check"><input type="checkbox" data-f="sizeFromInit"${f.sizeFromInit ? ' checked' : ''}><span>From the init image (${init.w}×${init.h}, longest side up to 768)</span></label>` : ''}
          <span class="img-size-fields${init && f.sizeFromInit ? ' off' : ''}"><input type="number" data-f="width" min="64" max="2048" step="8" value="${esc(f.width)}" aria-label="Width"> ×
            <input type="number" data-f="height" min="64" max="2048" step="8" value="${esc(f.height)}" aria-label="Height">
            <button type="button" class="btn xs ghost" data-act="img-size-swap" data-tip="Swap width and height">⇄</button>
            <span class="img-size-presets">${IMG_SIZES.map(([w, h]) => `<button type="button" class="btn xs ghost${f.width === w && f.height === h ? ' on' : ''}" data-act="img-size" data-sw="${w}" data-sh="${h}">${w}×${h}</button>`).join('')}</span></span></div>
        <div class="img-grid2">${num('steps', 'Steps', 1, 150, 1)}${num('guidance_scale', 'Guidance (CFG)', 0, 30, 0.5)}${num('num_images', 'Pictures', 1, 16, 1, 'Made one after another')}
          ${num('clip_skip', 'Clip skip', 1, 12, 1, '2 is common for anime models')}${num('guidance_rescale', 'Guidance rescale', 0, 1, 0.05)}${num('eta', 'Eta', 0, 1, 0.05)}</div>
        <div class="check-grid">${check('freeu', 'FreeU', 'The recommended SD 1.5 values')}</div>`)}
      ${card('LoRAs', `${loras || '<p class="muted small">None. A LoRA is a file stem in loras/ or a Hugging Face repo id (e.g. latent-consistency/lcm-lora-sdv1-5 with the lcm scheduler, 4-8 steps, guidance 1-2).</p>'}
        <button type="button" class="btn xs" data-act="img-lora-add">+ LoRA</button>`, { hint: 'loras' })}
      ${card('Image to image and inpainting', `<div class="img-i2i">
          <div>${imgSlotHtml('init', init, 'Init image (img2img)')}</div>
          <div>${init ? imgSlotHtml('mask', IMG.mask, 'Mask (inpaint): white is repainted', '<button type="button" class="btn xs" data-act="img-mask-draw">Paint mask</button>') : '<p class="muted small">Add an init image for img2img; add a mask too to inpaint.</p>'}</div></div>
        ${init ? `<div class="img-grid2">${num('strength', 'Strength', 0.01, 1, 0.01, 'How much it changes; img2img runs steps × strength')}
          ${IMG.mask ? num('mask_blur', 'Mask blur', 0, 64, 1) : ''}</div>
          ${IMG.mask ? `<div class="check-grid">${check('invert_mask', 'Invert mask', 'Repaint the black part instead')}
            <label class="check"><input type="checkbox" data-f="paddingOn"${f.paddingOn ? ' checked' : ''}><span><b>Only the masked area</b> <code>inpaint_padding</code>
              <span class="muted small">At full resolution, with <input type="number" data-f="inpaint_padding" min="0" max="512" step="1" value="${esc(f.inpaint_padding)}" class="aud-mini"> px around it</span></span></label></div>` : ''}` : ''}`)}
      ${card('ControlNet', `${nets || '<p class="muted small">None. Each ControlNet downloads on first use (about 700 MB). More than one is multi-ControlNet.</p>'}
        <div class="row"><button type="button" class="btn xs" data-act="img-cn-add">+ ControlNet</button>${f.controlnets.length ? check('guess_mode', 'Guess mode') : ''}</div>`, { hint: 'controlnets' })}
      ${card('Hires fix', `<label class="check"><input type="checkbox" data-f="hiresOn"${f.hiresOn ? ' checked' : ''}${init ? ' disabled' : ''}><span><b>Upscale and refine</b> <code>hires</code>
          <span class="muted small">${init ? 'Text to image only (remove the init image)' : 'A second pass at a larger size'}</span></span></label>
        <div class="img-grid2${f.hiresOn && !init ? '' : ' off'}" id="img-hires">
          <label class="img-num">Scale <code>scale</code><input type="number" data-h="scale" min="1" max="4" step="0.05" value="${esc(f.hires.scale)}"></label>
          <label class="img-num">Upscaler <code>upscaler</code><select data-h="upscaler">${['lanczos', 'latent'].map(u => `<option${u === f.hires.upscaler ? ' selected' : ''}>${u}</option>`).join('')}</select></label>
          <label class="img-num">Steps <code>steps</code><input type="number" data-h="steps" min="1" max="150" step="1" value="${esc(f.hires.steps ?? '')}" placeholder="as above"></label>
          <label class="img-num">Denoise <code>denoise</code><input type="number" data-h="denoise" min="0.01" max="1" step="0.01" value="${esc(f.hires.denoise)}"></label></div>`)}
    </div>
    <aside class="img-side">
      <div class="card img-go-card"><div class="card-body">
        <button type="button" class="btn primary img-go" data-act="img-generate" id="img-go">Generate${f.num_images > 1 ? ` ${f.num_images} pictures` : ''}</button>
        <div class="muted small img-cost" id="img-cost">${esc(imgCostText())}</div>
        <div class="muted small">${init ? (IMG.mask ? 'Inpaint' : 'Image to image') : 'Text to image'}${f.controlnets.length ? ` · ${plural(f.controlnets.length, 'ControlNet')}` : ''}${f.loras.length ? ` · ${plural(f.loras.length, 'LoRA')}` : ''} · Ctrl+Enter</div>
      </div></div>
      <div id="img-job">${imgJobHtml()}</div>
      <div id="img-recent">${imgRecentHtml()}</div>
    </aside>
    ${datalist('sd-models', sdModels())}${datalist('sd-vaes', sdVaes())}${datalist('sd-loras', sdLoras())}${datalist('sd-nets', sdNets())}
  </div>`;
}
function imgRedrawCreate() {
  const y = window.scrollY;
  const body = $('#img-body'); if (!body || IMG.tab !== 'create') return;
  body.innerHTML = imgCreateHtml(); applyPermissions(body); imgCreateAfter();
  window.scrollTo(0, y);
}
function imgCreateAfter() {
  const f = imgForm();
  imgApplyStyles($('#img-body'));
  const cost = () => { const c = $('#img-cost'); if (c) c.textContent = imgCostText(); const g = $('#img-go'); if (g) g.textContent = 'Generate' + (f.num_images > 1 ? ` ${f.num_images} pictures` : ''); };
  $$('#img-body [data-f]').forEach(el => {
    const k = el.dataset.f;
    el.addEventListener(el.type === 'checkbox' || el.tagName === 'SELECT' ? 'change' : 'input', () => {
      let v = el.type === 'checkbox' ? el.checked : el.type === 'number' || el.type === 'range' ? (el.value === '' ? null : Number(el.value)) : el.value;
      f[k] = v;
      // A range and its number stay together.
      $$(`#img-body [data-f="${k}"]`).forEach(o => { if (o !== el && o.type !== 'checkbox') o.value = el.value; });
      imgSaveForm(); cost();
      if (k === 'hiresOn') $('#img-hires').classList.toggle('off', !v);
      if (k === 'sizeFromInit') $('.img-size-fields').classList.toggle('off', !!v);
    });
  });
  $$('#img-body [data-h]').forEach(el => el.addEventListener(el.tagName === 'SELECT' ? 'change' : 'input', () => {
    f.hires[el.dataset.h] = el.tagName === 'SELECT' ? el.value : el.value === '' ? null : Number(el.value); imgSaveForm(); cost();
  }));
  $$('#img-body [data-lora]').forEach(el => el.addEventListener('input', () => {
    const l = f.loras[Number(el.dataset.lora)]; if (!l) return;
    l[el.dataset.k] = el.dataset.k === 'weight' ? Number(el.value) : el.value;
    $$(`#img-body [data-lora="${el.dataset.lora}"][data-k="${el.dataset.k}"]`).forEach(o => { if (o !== el) o.value = el.value; });
    imgSaveForm();
  }));
  $$('#img-body [data-cn]').forEach(el => el.addEventListener(el.tagName === 'SELECT' ? 'change' : 'input', () => {
    const c = f.controlnets[Number(el.dataset.cn)]; if (!c) return;
    c[el.dataset.k] = el.type === 'number' ? Number(el.value) : el.value;
    if (el.dataset.k === 'preprocessor' && el.value && !c.model) { c.model = PRE_TO_NET[el.value] || ''; const m = $(`#img-body [data-cn="${el.dataset.cn}"][data-k="model"]`); if (m) m.value = c.model; }
    imgSaveForm();
  }));
  $$('#img-body .img-slot').forEach(slot => {
    slot.addEventListener('dragover', ev => { ev.preventDefault(); slot.classList.add('over'); });
    slot.addEventListener('dragleave', () => slot.classList.remove('over'));
    slot.addEventListener('drop', async ev => {
      ev.preventDefault(); slot.classList.remove('over');
      const file = ev.dataTransfer.files && ev.dataTransfer.files[0]; if (!file) return;
      try { imgSetSlot(slot.dataset.slot, await imgFromFile(file)); } catch (e) { toast(e.message, 'bad'); }
    });
  });
  const prompt = $('#img-prompt');
  if (prompt) prompt.addEventListener('keydown', ev => { if ((ev.ctrlKey || ev.metaKey) && ev.key === 'Enter') { ev.preventDefault(); ACTIONS['img-generate'](); } });
  imgWatchRecent();
}
ACTIONS['img-seed-random'] = () => { imgForm().seed = -1; imgSaveForm(); $$('#img-body [data-f="seed"]').forEach(o => { o.value = -1; }); };
// data-sw / data-sh: the page's data-w and data-h are taken (data-w sets a width in percent; data-h is a hires field here).
ACTIONS['img-size'] = el => { const f = imgForm(); f.width = Number(el.dataset.sw); f.height = Number(el.dataset.sh); f.sizeFromInit = false; imgSaveForm(); imgRedrawCreate(); };
ACTIONS['img-size-swap'] = () => { const f = imgForm(); [f.width, f.height] = [f.height, f.width]; imgSaveForm(); imgRedrawCreate(); };
ACTIONS['img-emb'] = (el, ev) => {
  const f = imgForm(), into = ev && ev.shiftKey ? 'prompt' : 'negative_prompt';
  f[into] = (f[into] ? f[into].replace(/[\s,]*$/, ', ') : '') + el.dataset.name; imgSaveForm(); imgRedrawCreate();
};
ACTIONS['img-lora-add'] = () => { imgForm().loras.push({ name: '', weight: 0.8 }); imgSaveForm(); imgRedrawCreate(); };
ACTIONS['img-lora-remove'] = el => { imgForm().loras.splice(Number(el.dataset.i), 1); imgSaveForm(); imgRedrawCreate(); };
ACTIONS['img-cn-add'] = () => { const f = imgForm(); f.controlnets.push({ model: 'canny', preprocessor: 'canny', params: '', scale: 1, start: 0, end: 1 }); IMG.controls.push(null); imgSaveForm(); imgRedrawCreate(); };
ACTIONS['img-cn-remove'] = el => { const i = Number(el.dataset.i); imgForm().controlnets.splice(i, 1); IMG.controls.splice(i, 1); imgSaveForm(); imgRedrawCreate(); };
function imgParams(text, where) {
  if (!text || !text.trim()) return undefined;
  try { const o = JSON.parse(text); if (o && typeof o === 'object' && !Array.isArray(o)) return o; } catch { /* below */ }
  throw new Error(`${where}: the params are JSON, like {"low_threshold": 50}.`);
}
ACTIONS['img-cn-preview'] = async el => {
  const i = Number(el.dataset.i), c = imgForm().controlnets[i], image = IMG.controls[i] || IMG.init;
  if (!image) { toast('Add a control image (or an init image) first.', 'bad'); return; }
  if (!c.preprocessor) { toast('With no preprocessor the image is the control map already.', 'bad'); return; }
  const box = $('#img-cn-map-' + i); if (box) box.innerHTML = '<div class="muted small">Running the preprocessor… (the first use of one downloads it)</div>';
  try {
    const url = await imgPreprocess(c.preprocessor, image.ref, imgParams(c.params, 'ControlNet ' + (i + 1)));
    if (box) box.innerHTML = `<img src="${esc(url)}" alt="Control map"><div class="row"><button type="button" class="btn xs" data-act="img-cn-usemap" data-i="${i}" data-url="${esc(url)}" data-tip="Use the map as the control image, with no preprocessor">Use this map</button></div>`;
  } catch (e) { if (box) box.innerHTML = ''; toast(e.message, 'bad'); }
};
ACTIONS['img-cn-usemap'] = async el => {
  const i = Number(el.dataset.i), url = await imgBlobToDataUrl(el.dataset.url), size = await imgSize(url);
  IMG.controls[i] = { ref: url, url, name: 'control map', w: size.w, h: size.h };
  imgForm().controlnets[i].preprocessor = ''; imgSaveForm(); imgRedrawCreate();
};
/** The blobs behind the object URLs made here (the page may not fetch blob: URLs: its security policy allows only its own server). */
const IMG_BLOBS = new Map();
async function imgBlobToDataUrl(url) {
  const blob = IMG_BLOBS.get(url) || await (await fetch(url, { credentials: 'same-origin' })).blob();
  return new Promise(res => { const r = new FileReader(); r.onload = () => res(r.result); r.readAsDataURL(blob); });
}
/** Runs a preprocessor: the control map as an object URL. */
async function imgPreprocess(preprocessor, image, params) {
  const res = await fetch('/api/images/preprocess', { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': S.csrf || '' },
    body: JSON.stringify({ preprocessor, image, params }) });
  if (!res.ok) throw await apiErrorOf(res);
  const blob = await res.blob(), url = URL.createObjectURL(blob);
  IMG_BLOBS.set(url, blob);
  return url;
}

/** The request for the form (light: without the pictures, for the cost estimate). */
function imgRequest(light) {
  const f = imgForm(), b = { prompt: f.prompt.trim() };
  if (f.negative_prompt.trim()) b.negative_prompt = f.negative_prompt;
  for (const k of ['model', 'vae']) if (f[k] && f[k].trim()) b[k] = f[k].trim();
  if (f.scheduler) b.scheduler = f.scheduler;
  if (!IMG.init || !f.sizeFromInit) { b.width = f.width; b.height = f.height; } else if (light && IMG.init) { b.width = Math.min(768, IMG.init.w); b.height = Math.min(768, IMG.init.h); }
  for (const k of ['steps', 'guidance_scale', 'guidance_rescale', 'eta', 'clip_skip', 'num_images']) if (f[k] !== null && f[k] !== undefined && f[k] !== '') b[k] = Number(f[k]);
  if (Number(f.seed) >= 0) b.seed = Number(f.seed);
  if (f.freeu) b.freeu = true;
  const loras = f.loras.filter(l => l.name && l.name.trim()).map(l => ({ name: l.name.trim(), weight: Number(l.weight ?? 1) }));
  if (loras.length) b.loras = loras;
  if (IMG.init) {
    b.init_image = light ? true : IMG.init.ref; b.strength = Number(f.strength);
    if (IMG.mask) {
      b.mask_image = light ? true : IMG.mask.ref; b.mask_blur = Number(f.mask_blur);
      if (f.invert_mask) b.invert_mask = true;
      if (f.paddingOn) b.inpaint_padding = Number(f.inpaint_padding);
    }
  }
  if (f.controlnets.length) {
    b.controlnets = f.controlnets.map((c, i) => {
      const o = { model: (c.model || '').trim(), scale: Number(c.scale ?? 1), start: Number(c.start ?? 0), end: Number(c.end ?? 1) };
      if (IMG.controls[i]) o.image = light ? true : IMG.controls[i].ref;
      if (c.preprocessor) o.preprocessor = c.preprocessor;
      if (!light) { const p = imgParams(c.params, 'ControlNet ' + (i + 1)); if (p) o.preprocessor_params = p; }
      return o;
    });
    if (f.guess_mode) b.guess_mode = true;
  }
  if (f.hiresOn && !IMG.init) b.hires = { scale: Number(f.hires.scale), upscaler: f.hires.upscaler, denoise: Number(f.hires.denoise), ...(f.hires.steps ? { steps: Number(f.hires.steps) } : {}) };
  return b;
}
ACTIONS['img-generate'] = async () => {
  const f = imgForm();
  if (!f.prompt.trim()) { toast('Write a prompt.', 'bad'); const p = $('#img-prompt'); if (p) p.focus(); return; }
  let body;
  try { body = imgRequest(false); } catch (e) { toast(e.message, 'bad'); return; }
  const go = $('#img-go'); if (go) go.disabled = true;
  try {
    const res = await fetch('/api/images/jobs', { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': S.csrf || '' }, body: JSON.stringify(body) });
    if (!res.ok) throw await apiErrorOf(res);
    IMG.job = await res.json();
    IMG.jobImages = [];
    if (IMG.previewUrl) { URL.revokeObjectURL(IMG.previewUrl); IMG.previewUrl = null; }
    imgNoteJob(IMG.job);
    imgRenderJob();
    imgRenderRecent();
    imgFollow();
    imgWatchRecent();
  } catch (e) { toast(e.message, 'bad'); }
  finally { const g = $('#img-go'); if (g) g.disabled = false; }
};

// ------------------------------------------------------------------ the job: progress, live preview, results
const IMG_FINISHED = new Set(['done', 'failed', 'cancelled']);
/**
 * How far a job is (0-100), and whether that is an estimate: the step of the picture being made (with the pictures before it),
 * else the server's own progress, else the time it has run against the expected time (up to 95 %). It never goes back for a job.
 */
function imgJobProgress(j) {
  if (j.status === 'done' || j.status === 'saving') return { pct: 100, estimate: false };
  if (j.status !== 'running') return { pct: 0, estimate: false };
  const n = Math.max(1, j.numImages || 1), idx = Math.min(n, Math.max(1, j.imageIndex || 1));
  let pct = null, estimate = false;
  if (j.step && j.totalSteps) pct = ((idx - 1) + Math.min(1, j.step / j.totalSteps)) / n * 100;
  else if (j.progress !== null && j.progress !== undefined && j.progress > 0) pct = j.progress * 100;
  if (pct === null) {
    const start = Date.parse(j.startedUtc || j.submittedUtc || j.createdUtc), expected = Number(j.estimatedMs) || 0;
    pct = start && expected > 0 ? Math.min(95, (Date.now() - start) / expected * 100) : 0;
    estimate = true;
  }
  const before = IMG.pct && IMG.pct.id === j.id ? IMG.pct.value : 0;
  pct = Math.max(before, Math.min(100, Math.max(0, pct)));
  IMG.pct = { id: j.id, value: pct };
  return { pct: Math.round(pct), estimate };
}
function imgJobLabel(j) {
  // The server makes one job at a time: later jobs wait in line here (inLine) until the one before them ends.
  const queued = j.inLine ? (j.queuePosition ? `Waiting in line: ${j.queuePosition === 1 ? 'next' : `number ${j.queuePosition}`}` : 'Waiting in line')
    : j.queuePosition ? `Queued (position ${j.queuePosition})` : 'Queued';
  return { queued, running: 'Making', saving: 'Saving the pictures', done: 'Done', failed: 'Failed', cancelled: 'Cancelled' }[j.status] || j.status;
}
function imgJobDetail(j, p) {
  return [j.status === 'running' ? `${p.estimate ? 'about ' : ''}${p.pct}%` : '', j.step && j.totalSteps ? `step ${j.step} of ${j.totalSteps}` : '',
    j.numImages > 1 && j.imageIndex ? `picture ${j.imageIndex} of ${j.numImages}` : (j.numImages > 1 ? plural(j.numImages, 'picture') : ''),
    j.elapsedMs ? dur(j.elapsedMs) : '', j.costMicros ? 'cost ' + money(j.costMicros) : ''].filter(Boolean).join(' · ');
}
function imgJobHtml() {
  const j = IMG.job;
  if (!j) return '';
  const live = !IMG_FINISHED.has(j.status);
  const p = imgJobProgress(j);
  const images = IMG.jobImages || [];
  return `<div class="card img-job ${esc(j.status)}" data-id="${esc(j.id)}" data-status="${esc(j.status)}" data-inline="${j.inLine ? 1 : 0}" data-images="${images.length}"><div class="card-body">
    <div class="img-job-head"><b>${esc(imgJobLabel(j))}</b><span class="muted small">${esc(imgJobDetail(j, p))}</span><span class="spacer"></span>
      ${live ? '<button type="button" class="btn xs danger" data-act="img-cancel">Cancel</button>' : ''}</div>
    <div class="img-progress${p.estimate ? ' estimate' : ''}"><i data-p="${p.pct}"></i></div>
    ${j.error ? `<div class="notice bad small">${esc(j.error)}</div>` : ''}
    ${j.inLine ? '<p class="muted small">The server makes one job at a time: this one starts when the jobs before it end.</p>' : ''}
    ${live ? `<div class="img-preview" id="img-preview">${IMG.previewUrl ? `<img src="${esc(IMG.previewUrl)}" alt="Live preview">` : '<span class="muted small">The live preview shows here while it is made.</span>'}</div>` : ''}
    ${images.length ? `<div class="img-results">${images.map((g, i) => imgThumbHtml(g, 'job', i)).join('')}</div>
      <div class="row"><button type="button" class="btn xs" data-act="img-reuse" data-id="${esc(images[0].id)}" data-tip="Put this job's settings back in the form">Use these settings</button>
        ${images[0].seed !== null && images[0].seed !== undefined ? `<button type="button" class="btn xs ghost" data-act="img-use-seed" data-seed="${esc(images[0].seed)}">Seed ${esc(images[0].seed)}</button>` : ''}</div>` : ''}
    <div class="muted small img-job-prompt">${esc(j.prompt)}</div>
  </div></div>`;
}
/**
 * Shows the followed job. While its state stays the same only the bar and the words change (the bar then moves smoothly and the
 * live preview does not flicker); a new state draws the card again.
 */
function imgRenderJob() {
  const box = $('#img-job'); if (!box) return;
  const j = IMG.job, el = box.querySelector('.img-job');
  if (j && el && el.dataset.id === j.id && el.dataset.status === j.status && el.dataset.inline === (j.inLine ? '1' : '0') &&
      Number(el.dataset.images) === (IMG.jobImages || []).length) {
    const p = imgJobProgress(j);
    const bar = el.querySelector('.img-progress'); if (bar) { bar.classList.toggle('estimate', p.estimate); bar.firstElementChild.style.width = p.pct + '%'; }
    const label = el.querySelector('.img-job-head b'); if (label) label.textContent = imgJobLabel(j);
    const detail = el.querySelector('.img-job-head .muted'); if (detail) detail.textContent = imgJobDetail(j, p);
    return;
  }
  box.innerHTML = imgJobHtml(); imgApplyStyles(box);
}

// ------------------------------------------------------------------ recent jobs
function imgRecentHtml() {
  if (!IMG.recent.length) return '';
  const shown = IMG.job && IMG.job.id;
  return card('Recent jobs', IMG.recent.map(j => `<button type="button" class="img-recent${j.id === shown ? ' on' : ''}" data-act="img-job-open" data-id="${esc(j.id)}" data-tip="${esc(imgJobLabel(j))}">
      <span class="img-recent-st ${esc(j.status)}"></span><span class="grow">${esc(j.prompt)}</span><span class="muted small">${esc(ago(j.createdUtc))}</span></button>`).join(''), { flush: true });
}
function imgRenderRecent() { const box = $('#img-recent'); if (box) box.innerHTML = imgRecentHtml(); }
/** A job's latest state goes into the recent list (new ones at the top). */
function imgNoteJob(j) {
  const i = IMG.recent.findIndex(x => x.id === j.id);
  if (i >= 0) IMG.recent[i] = j; else IMG.recent = [j, ...IMG.recent].slice(0, 12);
}
/** While a recent job is still queued or being made, the list is read again every 3 s (jobs the page does not follow change too). */
function imgWatchRecent() {
  if (IMG.recentTimer) return;
  IMG.recentTimer = setInterval(async () => {
    const stop = () => { clearInterval(IMG.recentTimer); IMG.recentTimer = null; };
    if (!$('#img-recent') || IMG.tab !== 'create') { stop(); return; }
    if (!IMG.recent.some(j => !IMG_FINISHED.has(j.status))) { stop(); return; }
    const list = await get('/api/images/jobs?limit=12').catch(() => null);
    if (!list || !$('#img-recent')) return;
    const before = IMG.recent.map(j => j.id + j.status).join();
    // The followed job keeps the view it was just given (it is read every second).
    IMG.recent = list.map(j => IMG.job && j.id === IMG.job.id ? IMG.job : j);
    if (IMG.recent.map(j => j.id + j.status).join() !== before) imgRenderRecent();
  }, 3000);
}
/** Sizes set from data attributes (the page's security policy allows no inline style attributes). */
function imgApplyStyles(root) {
  $$('[data-p]', root).forEach(el => { el.style.width = Math.max(0, Math.min(100, Number(el.dataset.p) || 0)) + '%'; });
  $$('[data-ar]', root).forEach(el => { el.style.aspectRatio = el.dataset.ar; });
}
function imgStopFollow() { clearInterval(IMG.jobTimer); clearInterval(IMG.previewTimer); IMG.jobTimer = IMG.previewTimer = null; }
/** Follows the job (every second) and its live preview, until it ends; then shows its pictures. */
function imgFollow() {
  imgStopFollow();
  const id = IMG.job && IMG.job.id; if (!id) return;
  const gone = () => !$('#img-job') || !IMG.job || IMG.job.id !== id;
  IMG.jobTimer = setInterval(async () => {
    if (gone()) { imgStopFollow(); return; }
    let j;
    try { j = await get('/api/images/jobs/' + enc(id)); } catch { return; }
    if (gone()) return;
    const changed = IMG.job.status !== j.status;
    IMG.job = j;
    imgNoteJob(j);
    if (IMG_FINISHED.has(j.status)) {
      imgStopFollow();
      if (IMG.previewUrl) { URL.revokeObjectURL(IMG.previewUrl); IMG.previewUrl = null; }
      IMG.jobImages = j.status === 'done' ? await get('/api/images/gallery?job=' + enc(id)).catch(() => []) : [];
      if (j.status === 'done' && !IN_PANE) playSound('chatComplete');
      if (j.status === 'failed') toast('The pictures could not be made: ' + (j.error || 'the job failed'), 'bad');
    }
    imgRenderJob();
    if (changed) imgRenderRecent();
  }, 1000);
  IMG.previewTimer = setInterval(async () => {
    if (gone()) { imgStopFollow(); return; }
    if (IMG.job.status !== 'running') return;
    try {
      const res = await fetch(`/api/images/jobs/${enc(id)}/preview`, { credentials: 'same-origin' });
      if (res.status !== 200) return;
      const url = URL.createObjectURL(await res.blob());
      if (IMG.previewUrl) URL.revokeObjectURL(IMG.previewUrl);
      IMG.previewUrl = url;
      const box = $('#img-preview'); if (box) box.innerHTML = `<img src="${esc(url)}" alt="Live preview">`;
    } catch { /* the next one */ }
  }, 1500);
}
ACTIONS['img-cancel'] = async () => {
  if (!IMG.job) return;
  try { IMG.job = await del('/api/images/jobs/' + enc(IMG.job.id)); } catch (e) { toast(e.message, 'bad'); return; }
  imgNoteJob(IMG.job);
  toast('Cancelling…', 'ok'); imgRenderJob(); imgRenderRecent();
};
ACTIONS['img-job-open'] = async el => {
  let j;
  try { j = await get('/api/images/jobs/' + enc(el.dataset.id)); } catch (e) { toast(e.message, 'bad'); return; }
  imgStopFollow();
  if (IMG.previewUrl) { URL.revokeObjectURL(IMG.previewUrl); IMG.previewUrl = null; }
  IMG.job = j;
  imgNoteJob(j);
  IMG.jobImages = j.status === 'done' ? await get('/api/images/gallery?job=' + enc(j.id)).catch(() => []) : [];
  imgRenderJob();
  imgRenderRecent();
  if (!IMG_FINISHED.has(j.status)) imgFollow();
};
ACTIONS['img-use-seed'] = el => { imgForm().seed = Number(el.dataset.seed); imgSaveForm(); if (IMG.tab === 'create') imgRedrawCreate(); toast('Seed ' + el.dataset.seed, 'ok'); };

// ------------------------------------------------------------------ Gallery and the picture viewer
function imgThumbHtml(g, list, i) {
  return `<button type="button" class="img-thumb${g.starred ? ' starred' : ''}" data-act="img-view" data-list="${list}" data-i="${i}" data-tip="${esc((g.prompt || '').slice(0, 160))}">
    <img src="/api/images/gallery/${enc(g.id)}/file" alt="" loading="lazy">${g.starred ? '<span class="img-star">★</span>' : ''}</button>`;
}
async function imgLoadGallery(append) {
  const before = append && IMG.gallery.length ? '&before=' + enc(IMG.gallery[IMG.gallery.length - 1].createdUtc) : '';
  const list = await get(`/api/images/gallery?limit=60${IMG.gq ? '&q=' + enc(IMG.gq) : ''}${IMG.starred ? '&starred=1' : ''}${before}`).catch(() => []);
  IMG.gallery = append ? IMG.gallery.concat(list) : list;
  IMG.more = list.length === 60;
}
function imgGalleryHtml() {
  return `<div class="row img-gal-bar"><input id="img-gq" placeholder="Search prompt, model or seed" value="${esc(IMG.gq)}" aria-label="Search the gallery" autocomplete="off">
      <label class="check aud-inline"><input type="checkbox" id="img-starred"${IMG.starred ? ' checked' : ''}><span>Starred only</span></label><span class="spacer"></span>
      <span class="muted small">Starred pictures are kept; the gallery keeps the newest ${num((IMG.info.defaults || {}).maxImages || 500)}.</span>
      ${IMG.gallery.length ? '<button type="button" class="btn xs ghost" data-act="img-gal-clear">Delete unstarred</button>' : ''}</div>
    <div class="img-gallery" id="img-gallery">${IMG.gallery.length ? IMG.gallery.map((g, i) => imgThumbHtml(g, 'gallery', i)).join('') : `<p class="muted">${IMG.gq || IMG.starred ? 'Nothing matches.' : 'Pictures you make show here.'}</p>`}</div>
    ${IMG.more ? '<div class="row img-more"><button type="button" class="btn" data-act="img-gal-more">Load more</button></div>' : ''}`;
}
function imgGalleryAfter() {
  const q = $('#img-gq');
  let t;
  if (q) q.addEventListener('input', () => { clearTimeout(t); t = setTimeout(async () => { IMG.gq = q.value.trim(); await imgLoadGallery(); imgRenderGalleryGrid(); }, 300); });
  const st = $('#img-starred');
  if (st) st.addEventListener('change', async () => { IMG.starred = st.checked; await imgLoadGallery(); imgRenderGalleryGrid(); });
}
function imgRenderGalleryGrid() {
  const g = $('#img-gallery'); if (!g) return;
  g.innerHTML = IMG.gallery.length ? IMG.gallery.map((x, i) => imgThumbHtml(x, 'gallery', i)).join('') : `<p class="muted">${IMG.gq || IMG.starred ? 'Nothing matches.' : 'Pictures you make show here.'}</p>`;
  const more = $('.img-more'); if (more) more.classList.toggle('hidden', !IMG.more);
}
ACTIONS['img-gal-more'] = async () => { await imgLoadGallery(true); imgRender(); };
ACTIONS['img-gal-clear'] = async () => {
  if (!await confirmBox('Delete every unstarred picture?', 'Starred pictures stay.', { confirm: 'Delete', danger: true })) return;
  try { await del('/api/images/gallery'); } catch (e) { toast(e.message, 'bad'); return; }
  await imgLoadGallery(); imgRender();
};
const imgListOf = name => name === 'job' ? (IMG.jobImages || []) : IMG.gallery;
ACTIONS['img-view'] = el => imgOpenViewer(el.dataset.list, Number(el.dataset.i));
function imgOpenViewer(list, i) {
  const items = imgListOf(list), g = items[i];
  if (!g) return;
  IMG.view = { list, i };
  const p = g.params || {}, loras = (p.loras || []).map(l => `${l.name} (${l.weight ?? 1})`).join(', '), nets = (p.controlnets || []).map(c => `${c.model}${c.preprocessor ? ' / ' + c.preprocessor : ''} ×${c.scale ?? 1}`).join(', ');
  const rows = [['Seed', g.seed], ['Size', `${g.width}×${g.height}`], ['Mode', g.mode], ['Model', g.model], ['VAE', p.vae], ['Scheduler', p.scheduler], ['Steps', p.steps], ['Guidance', p.guidance_scale],
    ['Strength', p.init_image ? p.strength : null], ['LoRAs', loras], ['ControlNet', nets], ['Hires', p.hires ? `×${p.hires.scale} ${p.hires.upscaler || ''} denoise ${p.hires.denoise ?? ''}` : null],
    ['Made', new Date(g.createdUtc).toLocaleString()]].filter(r => r[1] !== null && r[1] !== undefined && r[1] !== '');
  openModal('Picture', `<div class="img-viewer">
      <div class="img-viewer-pic">${i > 0 ? '<button type="button" class="img-nav prev" data-act="img-view-step" data-d="-1" aria-label="Previous">‹</button>' : ''}
        <img src="/api/images/gallery/${enc(g.id)}/file" alt="">${i < items.length - 1 ? '<button type="button" class="img-nav next" data-act="img-view-step" data-d="1" aria-label="Next">›</button>' : ''}</div>
      <div class="img-viewer-side">
        <div class="img-viewer-prompt">${esc(g.prompt)}</div>${g.negativePrompt ? `<div class="muted small"><b>Negative:</b> ${esc(g.negativePrompt)}</div>` : ''}
        <dl class="aud-meta">${rows.map(r => `<dt>${esc(r[0])}</dt><dd>${esc(String(r[1]))}</dd>`).join('')}</dl>
        ${g.info ? `<details><summary>PNG info</summary><pre class="img-info">${esc(g.info)}</pre></details>` : ''}
        <div class="img-viewer-acts">
          <button type="button" class="btn xs" data-act="img-star" data-id="${esc(g.id)}">${g.starred ? '★ Starred' : '☆ Star'}</button>
          <a class="btn xs" href="/api/images/gallery/${enc(g.id)}/file?download=1" download>${icon('download')} Download</a>
          <button type="button" class="btn xs" data-act="img-reuse" data-id="${esc(g.id)}">Use these settings</button>
          ${g.seed !== null && g.seed !== undefined ? `<button type="button" class="btn xs" data-act="img-use-seed" data-seed="${esc(g.seed)}">Use the seed</button>` : ''}
          <button type="button" class="btn xs" data-act="img-as-init" data-id="${esc(g.id)}" data-tip="Change it: image to image">Img2img</button>
          <button type="button" class="btn xs" data-act="img-as-inpaint" data-id="${esc(g.id)}" data-tip="Paint the part to redo">Inpaint</button>
          <button type="button" class="btn xs" data-act="img-as-control" data-id="${esc(g.id)}" data-tip="Its shapes guide a new picture">ControlNet</button>
          <button type="button" class="btn xs" data-act="img-copy-prompt" data-id="${esc(g.id)}">Copy prompt</button>
          <button type="button" class="btn xs ghost danger" data-act="img-delete" data-id="${esc(g.id)}">Delete</button></div>
      </div></div>`, '<button class="btn primary" data-act="modal-close">Close</button>', 'full');
}
ACTIONS['img-view-step'] = el => { if (IMG.view) imgOpenViewer(IMG.view.list, IMG.view.i + Number(el.dataset.d)); };
document.addEventListener('keydown', ev => {
  if (!IMG.view || $('#modal').classList.contains('hidden') || !$('.img-viewer')) return;
  if (ev.key === 'ArrowLeft' || ev.key === 'ArrowRight') { ev.preventDefault(); const d = ev.key === 'ArrowLeft' ? -1 : 1, n = IMG.view.i + d; if (imgListOf(IMG.view.list)[n]) imgOpenViewer(IMG.view.list, n); }
});
function imgFind(id) { return IMG.gallery.find(x => x.id === id) || (IMG.jobImages || []).find(x => x.id === id); }
ACTIONS['img-star'] = async el => {
  const g = imgFind(el.dataset.id); if (!g) return;
  let r;
  try { r = await patch('/api/images/gallery/' + enc(g.id), { starred: !g.starred }); } catch (e) { toast(e.message, 'bad'); return; }
  g.starred = r.starred;
  if (IMG.view) imgOpenViewer(IMG.view.list, IMG.view.i);
  if (IMG.tab === 'gallery') imgRenderGalleryGrid();
};
ACTIONS['img-delete'] = async el => {
  const g = imgFind(el.dataset.id); if (!g) return;
  try { await del('/api/images/gallery/' + enc(g.id)); } catch (e) { toast(e.message, 'bad'); return; }
  IMG.gallery = IMG.gallery.filter(x => x.id !== g.id); IMG.jobImages = (IMG.jobImages || []).filter(x => x.id !== g.id);
  closeModal(); toast('Deleted', 'ok');
  if (IMG.tab === 'gallery') imgRenderGalleryGrid(); else imgRenderJob();
};
ACTIONS['img-copy-prompt'] = el => { const g = imgFind(el.dataset.id); if (g) copyText(g.prompt, 'Prompt copied'); };
/** Puts a picture's settings back in the form (its own pictures are not kept with it: init and control images stay as they are). */
ACTIONS['img-reuse'] = el => {
  const g = imgFind(el.dataset.id); if (!g) return;
  const p = g.params || {}, f = imgForm();
  f.prompt = g.prompt || ''; f.negative_prompt = g.negativePrompt || '';
  for (const k of ['model', 'vae', 'scheduler']) f[k] = p[k] || (k === 'scheduler' ? 'dpmpp_2m' : '');
  for (const k of ['steps', 'guidance_scale', 'guidance_rescale', 'eta', 'clip_skip', 'num_images', 'strength', 'mask_blur']) if (p[k] !== undefined) f[k] = p[k];
  if (p.width) { f.width = p.width; f.height = p.height; f.sizeFromInit = false; }
  f.seed = g.seed ?? -1; f.freeu = !!p.freeu; f.invert_mask = !!p.invert_mask; f.guess_mode = !!p.guess_mode;
  f.paddingOn = p.inpaint_padding !== undefined && p.inpaint_padding !== null; if (f.paddingOn) f.inpaint_padding = p.inpaint_padding;
  f.loras = (p.loras || []).map(l => ({ name: l.name, weight: l.weight ?? 1 }));
  f.controlnets = (p.controlnets || []).map(c => ({ model: c.model, preprocessor: c.preprocessor || '', params: c.preprocessor_params ? JSON.stringify(c.preprocessor_params) : '', scale: c.scale ?? 1, start: c.start ?? 0, end: c.end ?? 1 }));
  IMG.controls = f.controlnets.map(() => null);
  f.hiresOn = !!p.hires; if (p.hires) f.hires = Object.assign({}, IMG_DEFAULTS.hires, p.hires);
  imgSaveForm(); closeModal();
  if (IMG.tab !== 'create') navigate('/images'); else imgRedrawCreate();
  toast('Settings back in the form' + (p.init_image || (p.controlnets || []).some(c => c.image) ? ' (add its pictures again)' : ''), 'ok');
};
ACTIONS['img-as-init'] = el => { const g = imgFind(el.dataset.id); if (!g) return; IMG.init = imgFromGallery(g); IMG.mask = null; closeModal(); imgGoCreate(); };
ACTIONS['img-as-inpaint'] = el => { const g = imgFind(el.dataset.id); if (!g) return; IMG.init = imgFromGallery(g); IMG.mask = null; closeModal(); imgGoCreate().then(() => imgMaskEditor()); };
ACTIONS['img-as-control'] = el => {
  const g = imgFind(el.dataset.id); if (!g) return;
  const f = imgForm(); f.controlnets.push({ model: 'canny', preprocessor: 'canny', params: '', scale: 1, start: 0, end: 1 }); IMG.controls.push(imgFromGallery(g));
  imgSaveForm(); closeModal(); imgGoCreate();
};
async function imgGoCreate() {
  if (IMG.tab !== 'create' || !$('#img-body')) { navigate('/images'); await new Promise(r => setTimeout(r, 400)); }
  else imgRedrawCreate();
  window.scrollTo({ top: 0, behavior: reducedMotion() ? 'auto' : 'smooth' });
}

// ------------------------------------------------------------------ the mask painter (inpainting)
/** Paint over the init image: white is repainted, black is kept. The mask is a PNG the size of the picture. */
async function imgMaskEditor() {
  const init = IMG.init;
  if (!init) { toast('Add the init image first.', 'bad'); return; }
  const size = init.w && init.h ? { w: init.w, h: init.h } : await imgSize(init.url);
  IMG.paint = { mode: 'paint', brush: Math.round(Math.max(size.w, size.h) / 16), w: size.w, h: size.h };
  openModal('Paint the mask', `<div class="img-mask">
      <div class="row img-mask-tools"><div class="seg" role="group"><button type="button" class="seg-btn on" data-act="img-mask-mode" data-m="paint">Paint (repaint)</button>
        <button type="button" class="seg-btn" data-act="img-mask-mode" data-m="erase">Erase (keep)</button></div>
        <label class="img-mask-brush">Brush <input type="range" id="img-brush" min="4" max="${Math.round(Math.max(size.w, size.h) / 3)}" value="${IMG.paint.brush}"></label>
        <button type="button" class="btn xs ghost" data-act="img-mask-fill" data-v="1">All</button><button type="button" class="btn xs ghost" data-act="img-mask-fill" data-v="0">Clear</button>
        <button type="button" class="btn xs ghost" data-act="img-mask-invert">Invert</button></div>
      <div class="img-mask-stage" data-ar="${size.w} / ${size.h}"><img src="${esc(init.url)}" alt=""><canvas id="img-mask-canvas" width="${size.w}" height="${size.h}"></canvas></div>
      <p class="muted small">White (shown bright) is repainted. Inpainting uses sd15-inpaint unless you choose another model.</p></div>`,
    '<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="img-mask-done">Use this mask</button>', 'full');
  imgApplyStyles($('#modal-card'));
  const cv = $('#img-mask-canvas'), ctx = cv.getContext('2d');
  ctx.fillStyle = '#000'; ctx.fillRect(0, 0, cv.width, cv.height);
  if (IMG.mask) { const m = new Image(); m.onload = () => ctx.drawImage(m, 0, 0, cv.width, cv.height); m.src = IMG.mask.url; }
  let last = null;
  const at = ev => { const r = cv.getBoundingClientRect(); return { x: (ev.clientX - r.left) * cv.width / r.width, y: (ev.clientY - r.top) * cv.height / r.height }; };
  const stroke = (a, b) => {
    ctx.strokeStyle = IMG.paint.mode === 'paint' ? '#fff' : '#000'; ctx.lineWidth = IMG.paint.brush; ctx.lineCap = 'round'; ctx.lineJoin = 'round';
    ctx.beginPath(); ctx.moveTo(a.x, a.y); ctx.lineTo(b.x, b.y); ctx.stroke();
  };
  cv.addEventListener('pointerdown', ev => { cv.setPointerCapture(ev.pointerId); last = at(ev); stroke(last, last); });
  cv.addEventListener('pointermove', ev => { if (!last) return; const p = at(ev); stroke(last, p); last = p; });
  const up = () => { last = null; };
  cv.addEventListener('pointerup', up); cv.addEventListener('pointercancel', up);
  $('#img-brush').addEventListener('input', ev => { IMG.paint.brush = Number(ev.target.value); });
}
ACTIONS['img-mask-draw'] = () => imgMaskEditor();
ACTIONS['img-mask-mode'] = el => { IMG.paint.mode = el.dataset.m; $$('.img-mask-tools .seg-btn').forEach(b => b.classList.toggle('on', b === el)); };
ACTIONS['img-mask-fill'] = el => { const cv = $('#img-mask-canvas'), ctx = cv.getContext('2d'); ctx.fillStyle = el.dataset.v === '1' ? '#fff' : '#000'; ctx.fillRect(0, 0, cv.width, cv.height); };
ACTIONS['img-mask-invert'] = () => {
  const cv = $('#img-mask-canvas'), ctx = cv.getContext('2d'), d = ctx.getImageData(0, 0, cv.width, cv.height);
  for (let i = 0; i < d.data.length; i += 4) { d.data[i] = 255 - d.data[i]; d.data[i + 1] = 255 - d.data[i + 1]; d.data[i + 2] = 255 - d.data[i + 2]; d.data[i + 3] = 255; }
  ctx.putImageData(d, 0, 0);
};
ACTIONS['img-mask-done'] = () => {
  const cv = $('#img-mask-canvas'); if (!cv) return;
  const url = cv.toDataURL('image/png');
  IMG.mask = { ref: url, url, name: 'painted mask', w: cv.width, h: cv.height };
  closeModal(); imgRedrawCreate();
};

// ------------------------------------------------------------------ Tools: preprocessors, what the server has
function imgToolsHtml() {
  const info = IMG.info, o = info.options || {}, h = info.health || {};
  const lists = [['Models', sdOptions('models', 'checkpoints', 'model'), 'models/'], ['VAEs', sdOptions('vaes', 'vae'), 'vae/'], ['LoRAs', sdLoras(), 'loras/'],
    ['Embeddings', sdEmbeddings(), 'embeddings/'], ['ControlNets', sdOptions('controlnets', 'controlnet'), 'controlnets/'], ['Preprocessors', sdOptions('preprocessors', 'preprocessor'), ''],
    ['Schedulers', sdOptions('schedulers', 'samplers', 'scheduler'), '']];
  const known = new Set(['models', 'checkpoints', 'model', 'vaes', 'vae', 'loras', 'lora', 'embeddings', 'textual_inversions', 'embedding', 'controlnets', 'controlnet', 'preprocessors', 'preprocessor', 'schedulers', 'samplers', 'scheduler']);
  const other = Object.keys(o).filter(k => !known.has(k));
  return `<div class="img-tools">
    ${card('Preprocessor', `<p class="muted small">Turns a picture into a ControlNet control map (edges, depth, pose, lines…). The first use of a preprocessor downloads it.</p>
      <div class="img-i2i"><div>${imgSlotHtml('pre', IMG.pre.image, 'Picture')}</div>
        <div class="stack"><label>Preprocessor <code>preprocessor</code><select id="img-pre">${sdPreprocessors().map(p => `<option${p === IMG.pre.name ? ' selected' : ''}>${esc(p)}</option>`).join('')}</select></label>
          <label>Params <code>params</code><input id="img-pre-params" value="${esc(IMG.pre.params || '')}" placeholder='{"low_threshold": 50}'></label>
          <div class="row"><button type="button" class="btn primary" data-act="img-pre-run">Make the control map</button></div></div></div>
      <div id="img-pre-out">${IMG.pre.map ? imgPreOutHtml() : ''}</div>`)}
    ${card('Server', `<dl class="aud-meta">${Object.entries(h).map(([k, v]) => `<dt>${esc(k)}</dt><dd>${esc(typeof v === 'object' ? JSON.stringify(v) : String(v))}</dd>`).join('') || '<dt>Health</dt><dd>—</dd>'}</dl>
      <div class="row img-foot"><button type="button" class="btn xs" data-act="img-refresh">Refresh</button></div>`, { hint: 'GPU, the model loaded, the queue and VRAM' })}
    ${card('What the server has', lists.map(([label, items, folder]) => `<div class="img-optlist"><b>${esc(label)}</b> <span class="muted small">${num(items.length)}${folder ? ' · drop files into ' + esc(folder) + ' on the server' : ''}</span>
        <div class="img-chips">${items.map(x => `<span class="img-chip">${esc(x)}</span>`).join('') || '<span class="muted small">none</span>'}</div></div>`).join('') +
      (other.length ? `<details><summary>Everything else /options says</summary><pre class="img-info json">${jsonColor(JSON.stringify(Object.fromEntries(other.map(k => [k, o[k]])), null, 2))}</pre></details>` : ''),
      { hint: 'Only SD 1.5-family files work (not SDXL, SD2 or Flux)' })}
  </div>`;
}
function imgPreOutHtml() {
  return `<div class="img-pre-out"><img src="${esc(IMG.pre.map)}" alt="Control map"><div class="row"><button type="button" class="btn xs" data-act="img-pre-use">Use in a ControlNet</button>
    <a class="btn xs" href="${esc(IMG.pre.map)}" download="control-${esc(IMG.pre.name || 'map')}.png">${icon('download')} Download</a></div></div>`;
}
function imgToolsAfter() {
  const sel = $('#img-pre'); if (sel) sel.addEventListener('change', () => { IMG.pre.name = sel.value; });
  const params = $('#img-pre-params'); if (params) params.addEventListener('input', () => { IMG.pre.params = params.value; });
  const slot = $('.img-slot[data-slot="pre"]');
  if (slot) {
    slot.addEventListener('dragover', ev => { ev.preventDefault(); slot.classList.add('over'); });
    slot.addEventListener('dragleave', () => slot.classList.remove('over'));
    slot.addEventListener('drop', async ev => { ev.preventDefault(); slot.classList.remove('over'); const file = ev.dataTransfer.files && ev.dataTransfer.files[0]; if (!file) return; try { IMG.pre.image = await imgFromFile(file); imgRender(); } catch (e) { toast(e.message, 'bad'); } });
  }
}
ACTIONS['img-pre-run'] = async () => {
  if (!IMG.pre.image) { toast('Add a picture first.', 'bad'); return; }
  IMG.pre.name = $('#img-pre').value; IMG.pre.params = $('#img-pre-params').value;
  const out = $('#img-pre-out'); if (out) out.innerHTML = '<div class="muted small">Running the preprocessor…</div>';
  try { IMG.pre.map = await imgPreprocess(IMG.pre.name, IMG.pre.image.ref, imgParams(IMG.pre.params, 'Preprocessor')); if (out) out.innerHTML = imgPreOutHtml(); }
  catch (e) { if (out) out.innerHTML = ''; toast(e.message, 'bad'); }
};
ACTIONS['img-pre-use'] = async () => {
  const url = await imgBlobToDataUrl(IMG.pre.map), size = await imgSize(url), f = imgForm();
  f.controlnets.push({ model: PRE_TO_NET[IMG.pre.name] || IMG.pre.name, preprocessor: '', params: '', scale: 1, start: 0, end: 1 });
  IMG.controls.push({ ref: url, url, name: 'control map (' + IMG.pre.name + ')', w: size.w, h: size.h });
  imgSaveForm(); navigate('/images');
};
ACTIONS['img-refresh'] = async () => { try { await imgLoad(true); } catch (e) { toast(e.message, 'bad'); return; } imgRender(); toast(IMG.info.ready ? 'The server answers' : IMG.info.problem || 'Not answering', IMG.info.ready ? 'ok' : 'bad'); };

// ------------------------------------------------------------------ Settings
function imgSettingsHtml() {
  const info = IMG.info;
  const keyState = info.keyInConfig ? 'set in the configuration file (StableDiffusion.ApiKey), which wins over the vault'
    : info.keySet ? `kept in the vault as <code>${esc(info.keySecret || '')}</code> (no agent may use it)` : '<b class="aud-bad">not set</b>';
  const d = info.defaults || {};
  const server = card('Stable Diffusion server', `<dl class="aud-meta">
      <dt>Server</dt><dd>${info.isAdmin ? `<code>${esc(info.baseUrl || '')}</code> <span class="muted small">(StableDiffusion.BaseUrl)</span>` : 'set up by an administrator'}</dd>
      <dt>Status</dt><dd>${info.ready ? '<span class="aud-status ok"><i></i>Answering</span>' : `<span class="aud-status bad"><i></i>${esc(info.problem || 'Not answering')}</span>`}</dd>
      ${info.isAdmin ? `<dt>API key</dt><dd>${keyState}</dd>` : ''}
      <dt>Limits</dt><dd>${num(d.maxActiveJobs)} jobs at once per person · pictures up to ${num(d.maxInputImageMb)} MB as inputs · a job may take ${dur((d.jobTimeoutSeconds || 0) * 1000)} · the gallery keeps the newest ${num(d.maxImages)} (starred ones always)</dd></dl>
    ${info.isAdmin && !info.keyInConfig ? `<div class="row aud-key"><input type="password" id="sd-key" placeholder="${info.keySet ? 'Replace the API key' : 'The key from api_key.txt on the server'}" autocomplete="new-password" aria-label="API key">
      <button class="btn primary" data-act="sd-key-save">Save key</button>${info.keySet ? '<button class="btn ghost danger" data-act="sd-key-remove">Remove</button>' : ''}</div>` : ''}
    <div class="row"><button class="btn" data-act="img-refresh">Check the connection</button></div>`);
  return `<div class="stack">${imgPricingCardHtml(info.pricing || {}, !!info.canSetPrices)}${server}</div>`;
}
async function imgSetKey(apiKey) {
  let r;
  try { r = await put('/api/images/key', { apiKey }); } catch (e) { toast(e.message, 'bad'); return; }
  if (!apiKey) toast('API key removed', 'ok');
  else if (r.ok) toast('API key saved: the server answers', 'ok');
  else toast('API key saved, but: ' + (r.problem || 'the server does not answer'), 'bad');
  await imgLoad(true); imgRender();
}
ACTIONS['sd-key-save'] = () => { const k = ($('#sd-key').value || '').trim(); if (!k) { toast('Paste the API key first.', 'bad'); return; } imgSetKey(k); };
ACTIONS['sd-key-remove'] = async () => { if (await confirmBox('Remove the API key?', 'Pictures stop working until a key is set again.', { confirm: 'Remove', danger: true })) imgSetKey(''); };
