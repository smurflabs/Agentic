'use strict';
/* Agentic console - My models: bring your own API key. Each account adds OpenAI (or OpenAI-compatible), Anthropic and
   Amazon Bedrock models with its own keys. Only that account sees them — in Chat, the API, the desktop app, the CLI and
   VS Code — and agents it owns can use them when they are in the agent's allowed models. They are free here: the
   provider bills the key's owner. Keys are write-only: only their last characters come back. */

const BYO_DEFAULTS = {
  openai: { modelName: 'gpt-4o', contextWindow: 128000, maxTokens: 16384, vision: true, help: 'An OpenAI key (sk-…). For another OpenAI-compatible API (OpenRouter, Groq, a local LM Studio or Ollama …) set its address too.' },
  anthropic: { modelName: 'claude-sonnet-4-5', contextWindow: 200000, maxTokens: 16384, vision: true, help: 'An Anthropic API key (sk-ant-…).' },
  bedrock: { modelName: 'anthropic.claude-3-5-sonnet-20240620-v1:0', contextWindow: 200000, maxTokens: 8192, vision: true, help: 'An Amazon Bedrock API key, or an IAM access key id and secret access key, and the region the model runs in. The model must be enabled for your account in the Bedrock console.' }
};

VIEWS.myModels = {
  live: false,
  async render() {
    setCrumbs([['My models', '/my-models']]);
    const d = await get('/api/chat/my-models');
    S.byo = d;
    const cards = d.models.map(m => `<div class="card cm-card byo-card${m.enabled ? '' : ' off'}">
        <div class="cm-head"><div><div class="cell-title">${esc(m.name)}</div><code class="muted small">${esc(m.modelName)}</code></div><span class="spacer"></span>
          ${m.enabled ? badge('On', 'ok') : badge('Off', 'outline')} ${tag(m.providerLabel)}</div>
        <div class="small muted">${m.baseUrl ? `${esc(m.baseUrl)} · ` : ''}${m.region ? `${esc(m.region)} · ` : ''}${m.auth === 'iam' ? 'IAM access key' : m.auth === 'key' ? 'API key' : 'no key'} ${m.keyHint ? `<code>${esc(m.keyHint)}</code>` : ''}
          · ${num(m.contextWindow)} tokens context · up to ${num(m.maxTokens)} out${m.vision ? ' · sees pictures' : ''}</div>
        <div class="row byo-actions">
          <a class="btn small primary" href="/chat?model=${enc(m.id)}" data-nav>Chat with it</a>
          <button class="btn small" data-act="byo-test" data-id="${esc(m.id)}">Test</button>
          <button class="btn small" data-act="byo-edit" data-id="${esc(m.id)}">Edit</button>
          <button class="btn small" data-act="byo-toggle" data-id="${esc(m.id)}">${m.enabled ? 'Turn off' : 'Turn on'}</button>
          <button class="btn small danger" data-act="byo-delete" data-id="${esc(m.id)}">Delete</button>
          <button class="btn small ghost" data-act="copy-text" data-text="${esc(m.id)}" data-tip="The id to use in the API and in agents' allowed models">Copy id</button></div>
        <div class="small byo-test-result" id="byo-test-${esc(m.id)}"></div></div>`).join('');
    return pageHead('My models', {
      subtitle: 'Bring your own API key: add OpenAI, Anthropic or Amazon Bedrock models. Only you see them — in Chat, the API, the desktop app, the CLI and VS Code. They are free here: your provider bills you directly.',
      actions: `<button class="btn primary" data-act="byo-new">${icon('plus')} Add a model</button>`
    }) +
      (cards ? `<div class="cm-grid section">${cards}</div>` : card('', emptyState('No models of your own yet', 'Add an OpenAI, Anthropic or Amazon Bedrock model with your API key; it appears in your Chat model list right away.'), { cls: 'section' })) +
      card('Using them', `<p class="small">In <b>Chat</b> (web, desktop, VS Code) they are listed under <b>Your models</b>; in the CLI by name. With the API, use the id:</p>
        <pre>curl ${esc(serverOrigin())}/v1/chat/completions -H "Authorization: Bearer agk_…" -H "Content-Type: application/json" \\
  -d '{"model":"${esc(d.models[0] ? d.models[0].id : 'byo-…')}","messages":[{"role":"user","content":"Hello"}]}'</pre>
        <p class="small muted"><b>Agents you own</b> can use a model of yours when you add it to the agent's allowed models (Agents → edit → Models). Nobody else — administrators included — sees or uses your models, and your keys never leave this server: they are stored encrypted and only their last characters are shown.</p>`, { cls: 'section' });
  }
};

function byoForm(m) {
  const p = (m && m.provider) || 'openai';
  const def = BYO_DEFAULTS[p];
  const keyLabel = m ? 'API key <span class="muted small">(leave empty to keep the current one)</span>' : 'API key';
  return `<form id="byo-form" class="form-grid byo-form" autocomplete="off">
    <label>Provider<select name="provider" ${m ? 'disabled' : ''}>${S.byo.providers.map(x => `<option value="${esc(x.id)}"${x.id === p ? ' selected' : ''}>${esc(x.label)}</option>`).join('')}</select>
      <span class="help" id="byo-help">${esc(def.help)}</span></label>
    <label>Name<input name="name" maxlength="80" value="${esc(m ? m.name : '')}" placeholder="How it is shown in Chat"></label>
    <label>Model id<input name="modelName" required maxlength="200" value="${esc(m ? m.modelName : def.modelName)}"><span class="help">The provider's own model id.</span></label>
    <label class="byo-f byo-openai byo-anthropic">API address <span class="muted small">(optional)</span><input name="baseUrl" value="${esc(m && m.baseUrl || '')}" placeholder="${p === 'anthropic' ? 'https://api.anthropic.com' : 'https://api.openai.com/v1'}">
      <span class="help">Only for another endpoint (an OpenAI-compatible API, a proxy).</span></label>
    <label class="byo-f byo-bedrock">Region<input name="region" value="${esc(m && m.region || 'us-east-1')}" placeholder="us-east-1"></label>
    <label class="byo-f byo-openai byo-anthropic byo-bedrock">${keyLabel}<input name="apiKey" type="password" autocomplete="new-password" placeholder="${m && m.keyHint ? esc(m.keyHint) : ''}"></label>
    <details class="byo-f byo-bedrock byo-iam"${m && m.auth === 'iam' ? ' open' : ''}><summary>Use an IAM access key instead</summary>
      <label>Access key id<input name="accessKeyId" autocomplete="off"></label>
      <label>Secret access key<input name="secretAccessKey" type="password" autocomplete="new-password"></label>
      <label>Session token <span class="muted small">(temporary credentials only)</span><input name="sessionToken" type="password" autocomplete="new-password"></label></details>
    <label>Context window (tokens)<input name="contextWindow" type="number" min="1024" step="1" value="${esc(m ? m.contextWindow : def.contextWindow)}"></label>
    <label>Max output tokens<input name="maxTokens" type="number" min="16" value="${esc(m ? m.maxTokens : def.maxTokens)}"></label>
    <label class="check"><input type="checkbox" name="vision"${(m ? m.vision : def.vision) ? ' checked' : ''}> It can see pictures</label>
  </form>`;
}

function byoShowFields() {
  const f = $('#byo-form'); if (!f) return;
  const p = f.provider.value;
  $$('.byo-f', f).forEach(el => el.classList.toggle('hidden', !el.classList.contains('byo-' + p)));
  const help = $('#byo-help'); if (help) help.textContent = BYO_DEFAULTS[p].help;
}

function byoOpen(m) {
  openModal(m ? `Edit ${m.name}` : 'Add a model', byoForm(m),
    `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="byo-save" data-id="${esc(m ? m.id : '')}">${m ? 'Save' : 'Add model'}</button>`, 'wide');
  const f = $('#byo-form');
  f.provider.addEventListener('change', () => {
    const def = BYO_DEFAULTS[f.provider.value];
    f.modelName.value = def.modelName; f.contextWindow.value = def.contextWindow; f.maxTokens.value = def.maxTokens; f.vision.checked = def.vision;
    byoShowFields();
  });
  byoShowFields();
}

ACTIONS['byo-new'] = () => byoOpen(null);
ACTIONS['byo-edit'] = el => byoOpen(S.byo.models.find(m => m.id === el.dataset.id));
ACTIONS['byo-save'] = async el => {
  const f = $('#byo-form');
  const id = el.dataset.id;
  const body = {
    provider: f.provider.value, name: f.name.value.trim(), modelName: f.modelName.value.trim(),
    baseUrl: f.baseUrl.value.trim(), region: f.region.value.trim(), apiKey: f.apiKey.value.trim() || undefined,
    accessKeyId: f.accessKeyId.value.trim() || undefined, secretAccessKey: f.secretAccessKey.value.trim() || undefined, sessionToken: f.sessionToken.value.trim() || undefined,
    contextWindow: Number(f.contextWindow.value) || undefined, maxTokens: Number(f.maxTokens.value) || undefined, vision: f.vision.checked
  };
  if (body.provider !== 'bedrock') { body.region = ''; body.accessKeyId = body.secretAccessKey = body.sessionToken = undefined; }
  else body.baseUrl = '';
  el.disabled = true;
  try {
    const m = id ? await put(`/api/chat/my-models/${enc(id)}`, body) : await post('/api/chat/my-models', body);
    closeModal();
    toast(id ? 'Saved' : `${m.name} added — testing it…`, 'ok');
    await render();
    if (!id) byoTest(m.id);
  } catch (e) { toast(e.message, 'bad'); el.disabled = false; }
};
async function byoTest(id) {
  const out = document.getElementById('byo-test-' + id);
  if (out) { out.className = 'small byo-test-result muted'; out.textContent = 'Testing…'; }
  try {
    const r = await post(`/api/chat/my-models/${enc(id)}/test`);
    if (out) { out.className = 'small byo-test-result ' + (r.ok ? 'ok-text' : 'error-text'); out.textContent = (r.ok ? '✓ Answered: ' : '✗ ') + r.message; }
  } catch (e) { if (out) { out.className = 'small byo-test-result error-text'; out.textContent = '✗ ' + e.message; } }
}
ACTIONS['byo-test'] = el => byoTest(el.dataset.id);
ACTIONS['byo-toggle'] = async el => {
  const m = S.byo.models.find(x => x.id === el.dataset.id);
  try { await put(`/api/chat/my-models/${enc(m.id)}`, { enabled: !m.enabled }); toast(m.enabled ? 'Turned off' : 'Turned on', 'ok'); render(); }
  catch (e) { toast(e.message, 'bad'); }
};
ACTIONS['byo-delete'] = async el => {
  const m = S.byo.models.find(x => x.id === el.dataset.id);
  if (!await confirmBox('Delete model', `Delete ${m.name}? Its key is removed from this server; chats that used it stay, and agents can no longer use it.`, { danger: true, confirm: 'Delete' })) return;
  try { await del(`/api/chat/my-models/${enc(m.id)}`); toast('Deleted', 'ok'); render(); } catch (e) { toast(e.message, 'bad'); }
};
