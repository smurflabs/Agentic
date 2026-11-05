'use strict';
/* Agentic console - Chat: conversations with any provider model or custom model, streamed token by token, with files,
   web search, MCP tools, a code interpreter and multiple-choice questions from the model. Code blocks run in place:
   HTML/JS in a sandboxed frame, Python/Node/shell in the code sandbox. Switching conversations updates only the parts
   of the page that change. */

const CHAT = {
  meta: null, list: [], conv: null, model: null, settings: null, streams: new Map(), attachments: [], q: '', seq: 0, ask: null,
  // This window's id (to tell its own live events apart), answers running in other windows, desktop apps, approvals, context.
  clientId: 'w' + Math.random().toString(36).slice(2, 12), live: new Map(), devices: new Map(), approvals: new Map(), watch: null, ctx: null, hub: null, stick: true,
  // Managing the chat list (checked chats), desktop apps' tool lists (for the Tools menu) and its open groups.
  managing: false, checked: new Set(), catalogs: new Map(), devOpen: new Set(),
  // Answers this window started whose stream broke: followed through the event stream like another window's.
  adopted: new Set()
};
const LS = { get: k => { try { return localStorage.getItem(k); } catch { return null; } }, set: (k, v) => { try { localStorage.setItem(k, v); } catch { /* ignore */ } } };
/* Extension point: a host app (the Agentic desktop client) may set window.chatClient = {
     prepare({content, conv, model}) -> {tools, instructions}   tools the model may call on the user's computer, extra instructions
     runTool(call, conv) -> {ok, result}                         runs one such tool call
     composerHtml(CHAT) -> html                                  extra controls in the composer bar
     externalModels() -> [{id, name, description}]               models answered on this computer (Claude Code), not by the server
     externalComposerHtml(CHAT) -> html                          the composer controls while such a model is chosen
     runExternal({conv, content, attachments, settings, model, plan, signal, emit, approve, ask, history}) -> {answer, settings}
                                                                 answers one message there; chat.js saves it to the conversation }
   In a normal browser it is undefined and Chat works without it. */
// The Markdown renderer (chatMarkdown, highlight, code blocks, math) is in markdown.js, shared with the share page.

// ------------------------------------------------------------------ running code
ACTIONS['code-copy'] = async el => {
  const src = $('#' + el.dataset.block + ' .code-src').value;
  try { await writeClipboardOrThrow(src); el.textContent = 'Copied ✓'; el.classList.add('done'); setTimeout(() => { el.textContent = 'Copy'; el.classList.remove('done'); }, 1400); } catch { toast('Copy is not available here.', 'bad'); }
};
const SANDBOX_BRIDGE = '<script>(function(){function s(l,a){try{parent.postMessage({type:"sandbox-console",level:l,text:Array.prototype.map.call(a,function(x){try{return typeof x==="object"?JSON.stringify(x,null,1):String(x)}catch(e){return String(x)}}).join(" ")},"*")}catch(e){}}' +
  '["log","info","warn","error","debug"].forEach(function(l){var o=console[l];console[l]=function(){s(l,arguments);o&&o.apply(console,arguments)}});' +
  'window.addEventListener("error",function(e){s("error",[e.message+(e.lineno?" (line "+e.lineno+")":"")])});window.addEventListener("unhandledrejection",function(e){s("error",["Unhandled promise rejection: "+(e.reason&&e.reason.message||e.reason)])});})();<\/script>';
function sandboxDoc(lang, code) {
  if (lang === 'html' || lang === 'htm' || lang === 'svg') {
    if (/<head[^>]*>/i.test(code)) return code.replace(/<head[^>]*>/i, m => m + SANDBOX_BRIDGE);
    return '<!doctype html><html><head><meta charset="utf-8">' + SANDBOX_BRIDGE + '</head><body>' + code + '</body></html>';
  }
  return '<!doctype html><html><head><meta charset="utf-8">' + SANDBOX_BRIDGE + '<style>body{font:14px system-ui;margin:12px}</style></head><body><div id="app"></div><script>' +
    code.replace(/<\/script/gi, '<\\/script') + '<\/script></body></html>';
}
/** Runs a document in a sandboxed frame inside host; console output goes to the console element. Returns a cleanup function. */
function mountSandbox(host, lang, code) {
  const frame = $('iframe', host), consoleEl = $('.sandbox-console', host);
  const onMsg = ev => {
    if (ev.source !== frame.contentWindow) return;
    const d = ev.data || {};
    if (d.type === 'sandbox-ready') frame.contentWindow.postMessage({ type: 'run', doc: sandboxDoc(lang, code) }, '*');
    if (d.type === 'sandbox-console') { consoleEl.hidden = false; consoleEl.insertAdjacentHTML('beforeend', `<div class="cl ${esc(d.level)}">${esc(d.text)}</div>`); consoleEl.scrollTop = consoleEl.scrollHeight; }
  };
  window.addEventListener('message', onMsg);
  return () => window.removeEventListener('message', onMsg);
}
const sandboxFrameHtml = cls => `<iframe class="sandbox-frame ${cls || ''}" sandbox="allow-scripts allow-modals allow-forms allow-popups" src="/sandbox/frame" title="Preview"></iframe><div class="sandbox-console" hidden></div>`;
ACTIONS['code-preview'] = el => {
  const block = $('#' + el.dataset.block), lang = $('code', block).dataset.lang, code = $('.code-src', block).value;
  const out = $('.code-out', block);
  if (out._cleanup) out._cleanup();
  out.innerHTML = `<div class="sandbox"><div class="sandbox-bar"><span class="live-dot"></span><span>Sandboxed preview</span><span class="spacer"></span>
      <button class="btn xs ghost" data-act="code-preview" data-block="${el.dataset.block}">Re-run</button>
      <button class="btn xs ghost icon-only" data-act="embed-expand" data-kind="preview" data-block="${el.dataset.block}" aria-label="Open full screen" data-tip="Open full screen">${EXPAND_ICON}</button>
      <button class="btn xs ghost" data-act="sandbox-close">Close</button></div>${sandboxFrameHtml()}</div>`;
  out._cleanup = mountSandbox(out, lang, code);
};
ACTIONS['sandbox-close'] = el => { const out = el.closest('.code-out'); if (out._cleanup) out._cleanup(); out.classList.add('closing'); setTimeout(() => { out.innerHTML = ''; out.classList.remove('closing'); }, 180); };
ACTIONS['code-run'] = async el => {
  const block = $('#' + el.dataset.block), code = $('.code-src', block).value, out = $('.code-out', block);
  out.innerHTML = `<div class="run-out running"><div class="run-head"><span class="spinner"></span> Running ${esc(el.dataset.lang)} in the sandbox…</div><div class="run-progress"></div></div>`;
  const bar = (r, body) => `<div class="run-head">${r}<span class="spacer"></span>
    <button class="btn xs ghost icon-only" data-act="embed-expand" data-kind="output" aria-label="Open full screen" data-tip="Open full screen">${EXPAND_ICON}</button>
    <button class="btn xs ghost" data-act="sandbox-close">Close</button></div><pre class="run-text">${esc(body)}</pre>`;
  try {
    const r = await post('/api/chat/run', { language: el.dataset.lang, code });
    out.innerHTML = `<div class="run-out ${r.exitCode === 0 ? 'ok' : 'bad'}">${bar(`${r.exitCode === 0 ? badge('exit 0', 'ok') : badge('exit ' + (r.exitCode ?? '?'), 'bad')}${r.timedOut ? ' ' + badge('timed out', 'warn') : ''}
      <span class="muted small">${esc(r.runner)} · ${esc(dur(r.durationMs))}</span>`, r.output || '(no output)')}</div>`;
  } catch (e) { out.innerHTML = `<div class="run-out bad">${bar(badge('error', 'bad'), e.message)}</div>`; }
};

// ------------------------------------------------------------------ embeds in a full-screen modal
ACTIONS['embed-expand'] = el => {
  const kind = el.dataset.kind;
  if (kind === 'code' || kind === 'preview') {
    const block = $('#' + el.dataset.block), lang = $('code', block).dataset.lang || 'text', code = $('.code-src', block).value;
    const browser = RUN_BROWSER.has(lang);
    openModal(`${lang} · ${num(code.split('\n').length)} lines`, `<div class="embed-modal">
        ${browser ? `<div class="seg" role="tablist"><button class="seg-btn${kind === 'code' ? ' on' : ''}" data-act="embed-tab" data-tab="code">Code</button><button class="seg-btn${kind === 'preview' ? ' on' : ''}" data-act="embed-tab" data-tab="preview">Preview</button></div>` : ''}
        <div class="embed-pane" data-pane="code"${kind === 'preview' ? ' hidden' : ''}><pre class="embed-code"><code>${highlight(code, lang)}</code></pre></div>
        ${browser ? `<div class="embed-pane sandbox" data-pane="preview"${kind === 'preview' ? '' : ' hidden'}></div>` : ''}
        <textarea class="code-src" hidden>${esc(code)}</textarea></div>`,
      `<button class="btn" data-act="embed-copy">Copy</button><button class="btn" data-act="embed-download" data-lang="${esc(lang)}">Download</button><button class="btn primary" data-act="modal-close">Close</button>`, 'full');
    const pane = $('#modal-card [data-pane="preview"]');
    if (pane) { pane.dataset.lang = lang; if (kind === 'preview') startModalPreview(pane); }
    return;
  }
  let body = '';
  if (kind === 'table') body = `<div class="table-wrap md-table embed-table">${el.closest('.md-table-box').querySelector('table').outerHTML}</div>`;
  if (kind === 'output') body = `<pre class="embed-code">${esc(el.closest('.run-out').querySelector('.run-text').textContent)}</pre>`;
  if (kind === 'tool') { const card = el.closest('.tool-card'); fillLazy(card, true); const io = card.querySelector('.tool-io'); body = `<div class="embed-tool">${io ? io.innerHTML : ''}</div>`; }
  openModal(kind === 'table' ? 'Table' : kind === 'tool' ? 'Tool call' : 'Output', `<div class="embed-modal">${body}</div>`,
    `<button class="btn" data-act="embed-copy">Copy</button><button class="btn primary" data-act="modal-close">Close</button>`, 'full');
  $$('#modal-card .embed-tool [data-act="embed-expand"]').forEach(b => b.remove());
};
function startModalPreview(pane) {
  if (pane._cleanup) return;
  pane.innerHTML = sandboxFrameHtml('tall');
  pane._cleanup = mountSandbox(pane, pane.dataset.lang, $('#modal-card .code-src').value);
  const obs = new MutationObserver(() => { if (!pane.isConnected) { pane._cleanup(); obs.disconnect(); } });
  obs.observe(document.body, { childList: true, subtree: true });
}
ACTIONS['embed-tab'] = el => {
  $$('#modal-card .seg-btn').forEach(b => b.classList.toggle('on', b === el));
  $$('#modal-card .embed-pane').forEach(p => { p.hidden = p.dataset.pane !== el.dataset.tab; });
  if (el.dataset.tab === 'preview') startModalPreview($('#modal-card [data-pane="preview"]'));
};
ACTIONS['embed-copy'] = async el => {
  const src = $('#modal-card .code-src'), text = src ? src.value : $('#modal-card .embed-modal').innerText;
  try { await writeClipboardOrThrow(text); el.textContent = 'Copied ✓'; setTimeout(() => { el.textContent = 'Copy'; }, 1400); } catch { toast('Copy is not available here.', 'bad'); }
};
ACTIONS['embed-download'] = el => {
  const ext = { python: 'py', py: 'py', javascript: 'js', js: 'js', node: 'js', typescript: 'ts', ts: 'ts', html: 'html', htm: 'html', css: 'css', json: 'json', bash: 'sh', sh: 'sh', shell: 'sh', sql: 'sql', csharp: 'cs', cs: 'cs', vb: 'vb', svg: 'svg', xml: 'xml', yaml: 'yaml', yml: 'yml', go: 'go', rust: 'rs', java: 'java', md: 'md', markdown: 'md' }[el.dataset.lang] || 'txt';
  const url = URL.createObjectURL(new Blob([$('#modal-card .code-src').value], { type: 'text/plain' }));
  const a = document.createElement('a'); a.href = url; a.download = 'snippet.' + ext; document.body.appendChild(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
};

// ------------------------------------------------------------------ page
VIEWS.chat = {
  live: false,
  async render(p) {
    setCrumbs([['Chat', '/chat']]);
    CHAT.welcomeIdeas = null;   // a new chat screen gets new ideas
    const [meta, list, projects] = await Promise.all([get('/api/chat/meta'), get('/api/chat/conversations'), get('/api/chat/projects').catch(() => [])]);
    CHAT.meta = meta; CHAT.list = list; CHAT.projects = projects || [];
    CHAT.modelStatus = meta.modelStatus || {}; CHAT.modelStatusAt = Date.now();
    CHAT.favorites = new Set(meta.favoriteModels || []);
    if (CHAT.project && !CHAT.projects.some(x => x.id === CHAT.project)) CHAT.project = null;
    // Coming back to Chat (from another page, or the app opening again) shows the chat you were in, not a new one; New chat
    // forgets it, so a new chat stays new.
    let id = p.args[0];
    const resume = !id && !p.query.get('model') && !p.query.get('new') && !mtActive() && LS.get('chat.lastConv');
    if (resume) id = resume;
    CHAT.conv = id ? await get('/api/chat/conversations/' + enc(id)).catch(() => null) : null;
    if (resume) {
      // Changed in place: a new S.params object would make render() drop this page as out of date.
      if (CHAT.conv) { history.replaceState({}, '', '/chat/' + enc(CHAT.conv.id)); Object.assign(p, { path: '/chat/' + CHAT.conv.id, args: [CHAT.conv.id] }); }
      else LS.set('chat.lastConv', '');
    }
    // A live view left from before (another chat) no longer belongs on this page.
    if (CHAT.watch && (!CHAT.conv || CHAT.watch.chatId !== CHAT.conv.id)) CHAT.watch = null;
    chooseModelAndSettings(p.query.get('model'));
    CHAT.attachments = [];
    // Multitask: up to four chats side by side, each in its own pane (chatmulti.js); the chat list stays on the left.
    const multi = mtActive();
    if (multi) { mtSeed(CHAT.conv && CHAT.conv.id); CHAT.conv = null; }
    return `<div class="chat-app${multi ? ' mt-app' : ''}${LS.get('chat.side') === 'closed' ? ' side-closed' : ''}">
      <aside class="chat-side">
        <div class="chat-side-top"><button class="btn primary new-chat-btn" data-act="chat-new">${icon('plus')} New chat</button>
          <button class="btn icon manage-btn${CHAT.managing ? ' on' : ''}" data-act="chat-manage" aria-pressed="${CHAT.managing}" aria-label="Manage chats" data-tip="Manage chats: select several and delete them">${MANAGE_ICON}</button></div>
        <div class="manage-bar${CHAT.managing ? '' : ' hidden'}" id="manage-bar">${manageBarHtml()}</div>
        <div class="chat-search">${icon('search')}<input id="chat-q" placeholder="Search chats" value="${esc(CHAT.q)}" autocomplete="off">${bookmarkFilterHtml()}</div>
        <div class="chat-projects" id="chat-projects">${projectsHtml()}</div>
        <div class="chat-list" id="chat-list">${chatListHtml()}</div>
        <div class="chat-side-foot"><button class="btn ghost small side-foot-btn" data-act="chat-personalize" data-tip="What the assistant knows about you and how it answers${esc(keyTip('chat.personalize'))}">${PERSON_ICON} Personalization</button>
          <button class="btn ghost small icon side-foot-btn" data-act="prompts-manage" aria-label="Saved prompts" data-tip="Saved prompts: type / in the message box">${PROMPT_ICON}</button>
          <button class="btn ghost small icon side-foot-btn" data-act="chat-shortcuts" aria-label="Keyboard shortcuts" data-tip="Keyboard shortcuts: change any of them${esc(keyTip('app.shortcuts'))}">${KEYS_ICON}</button></div>
      </aside>
      ${multi ? mtMainHtml() + '</div>' : `<section class="chat-main">
        <header class="chat-top">
          <button class="btn icon ghost" data-act="chat-side" aria-label="Toggle chats" data-tip="Show or hide chats"><svg viewBox="0 0 24 24" class="ic"><path d="M4 5h16v14H4zM9 5v14"/></svg></button>
          <div class="model-picker" id="model-picker">${modelButton()}</div>
          <span id="tps-slot" class="tps-slot"></span>
          <span id="device-chip"></span>
          <span class="spacer"></span>
          <span id="nav-slot" class="nav-slot">${typeof navButtonsHtml === 'function' ? navButtonsHtml() : ''}</span>
          <button class="btn icon ghost mt-toggle" data-act="mt-enter" aria-label="Multitask" data-tip="Multitask: up to 4 chats side by side${esc(keyTip('chat.multitask'))}">${MT_ICON}</button>
          <span id="temp-slot">${tempToggleHtml()}</span>
          <div class="chat-head" id="chat-head">${headHtml()}</div>
        </header>
        <div class="find-bar hidden" id="find-bar" role="search"></div>
        <div class="pin-bar hidden" id="pin-bar"></div>
        <div class="chat-scroll" id="chat-scroll"><div class="chat-thread" id="chat-thread">${threadHtml()}</div></div>
        <button class="to-bottom hidden" data-act="chat-bottom" aria-label="Scroll to the newest message"><svg viewBox="0 0 24 24" class="ic"><path d="M12 5v14M6 13l6 6 6-6"/></svg></button>
        <div class="composer-wrap">
          <div class="ask-pop hidden" id="ask-pop" role="dialog" aria-label="Question from the assistant"></div>
          <div class="appr-pop hidden" id="approve-pop" role="dialog" aria-label="Approve a tool"></div>
          <div class="goal-bar hidden" id="goal-bar" aria-live="polite"></div>
          <div class="goal-bar voice-bar hidden" id="voice-bar" aria-live="polite"></div>
          <div class="composer" id="composer">
            <div class="composer-files" id="composer-files"></div>
            <textarea id="chat-input" rows="1" placeholder="${esc(placeholder())}"></textarea>
            <div class="composer-bar" id="composer-bar">${composerBarHtml()}</div>
          </div>
          <div class="composer-hint" id="composer-hint">${esc(routeHint())}</div></div>
      </section></div>`}`;
  },
  after() {
    if ($('.mt-app')) {
      applyRich($('.chat-side'));
      $('#chat-q').addEventListener('input', ev => chatSearch(ev.target));
      ensureChatHub();
      mtMount();
      return;
    }
    const input = $('#chat-input');
    applyRich($('.chat-app'));
    scrollThread(true);
    renderTodoDock();
    input.addEventListener('input', autosize);
    input.addEventListener('keydown', ev => {
      if (ev.isComposing) return;
      // On a phone Enter adds a line (the send button sends).
      if (keyIs(ev, 'chat.send') && !TOUCH_ONLY.matches) {
        ev.preventDefault();
        if (answering() && ev.target.value.trim()) sendWhileAnswering(ev.target.value);
        else sendMessage().catch(e => toast(e.message, 'bad'));
        return;
      }
      if (keyIs(ev, 'chat.newline')) { keyNewline(ev); return; }
      if (keyIs(ev, 'chat.mode')) { ev.preventDefault(); cycleMode(); return; }
      if (keyIs(ev, 'chat.stop') && currentStream()) stopStreaming();
    });
    input.addEventListener('paste', ev => { const files = Array.from(ev.clipboardData && ev.clipboardData.files || []); if (files.length) { ev.preventDefault(); attachFiles(files); } });
    const comp = $('#composer');
    comp.addEventListener('dragover', ev => { ev.preventDefault(); comp.classList.add('drop'); });
    comp.addEventListener('dragleave', () => comp.classList.remove('drop'));
    comp.addEventListener('drop', ev => { ev.preventDefault(); comp.classList.remove('drop'); if (ev.dataTransfer.files.length) attachFiles(ev.dataTransfer.files); });
    $('#chat-q').addEventListener('input', ev => chatSearch(ev.target));
    const sc = $('#chat-scroll');
    // Scrolling up (wheel, scroll bar, touch, keys) stops following the answer at once: a smooth wheel scroll starts with a few
    // pixels, and the next streamed line would otherwise pull the view back down. Back at the bottom, it follows again.
    sc.addEventListener('wheel', ev => { if (ev.deltaY < 0) unstickThread(); }, { passive: true });
    sc.addEventListener('pointerdown', ev => { if (ev.target === sc && ev.offsetX >= sc.clientWidth) unstickThread(); });
    // Boxes inside answers scroll on their own (code, tool output, thinking, sources). The browser gives a wheel turn to the
    // box under the pointer and keeps the rest of that scroll on it, so in a long chat the conversation stalled on each code
    // block, could not reach the bottom, and jumped when the wheel let go. A vertical wheel now scrolls the conversation
    // unless you clicked into the box first; its scroll bar and sideways scrolling still work. While the wheel turns, the
    // boxes let it through (.wheeling), so the rest of the scroll is the browser's own smooth one.
    let engagedBox = null, wheelingTimer = 0;
    const endWheeling = () => sc.classList.remove('wheeling');
    sc.addEventListener('pointerdown', ev => { engagedBox = innerScroller(ev.target, sc); }, true);
    sc.addEventListener('wheel', ev => {
      if (sc.classList.contains('wheeling')) { clearTimeout(wheelingTimer); wheelingTimer = setTimeout(endWheeling, 400); }
      if (ev.ctrlKey || ev.shiftKey || Math.abs(ev.deltaX) > Math.abs(ev.deltaY)) return;
      const box = innerScroller(ev.target, sc);
      if (!box || box === engagedBox) return;
      ev.preventDefault();
      sc.scrollTop += ev.deltaY * (ev.deltaMode === 1 ? 40 : ev.deltaMode === 2 ? sc.clientHeight : 1);
      sc.classList.add('wheeling');
      clearTimeout(wheelingTimer);
      wheelingTimer = setTimeout(endWheeling, 400);
    }, { passive: false });
    let touchY = null;
    sc.addEventListener('touchstart', ev => { touchY = ev.touches[0].clientY; }, { passive: true });
    sc.addEventListener('touchmove', ev => { if (touchY !== null && ev.touches[0].clientY > touchY + 4) unstickThread(); }, { passive: true });
    sc.addEventListener('scroll', () => {
      const gap = sc.scrollHeight - sc.scrollTop - sc.clientHeight;
      if (gap >= 80) CHAT.stick = false;
      else if (gap < 24 && Date.now() - (CHAT.unstuckAt || 0) > 400) CHAT.stick = true;
      syncToBottom();
    });
    // Anything that makes the thread taller (the answer, tool cards, thinking, images) or the view shorter (approval and
    // question cards above the message box) keeps the newest content in view while you follow it.
    if (window.ResizeObserver) {
      const follow = new ResizeObserver(() => { if (CHAT.stick) sc.scrollTop = sc.scrollHeight; syncToBottom(); });
      follow.observe($('#chat-thread'));
      follow.observe(sc);
    }
    input.addEventListener('input', renderContext);
    input.addEventListener('input', () => { clearTimeout(CHAT.draftTimer); CHAT.draftTimer = setTimeout(stashDraft, 400); });
    restoreDraft();
    watchComposerFit();
    ensureChatHub();
    syncBusy();
    showPendingQuestion();
    markCompacted();
    watchEarlierBar();
    loadContext();
    renderApproval();
    renderDeviceChip();
    renderGoalBar();
    reattachLive();
    rememberOpenChat();
    attachToLiveRun(CHAT.conv);
    focusComposer();
    document.dispatchEvent(new CustomEvent('chat:mounted'));
  },
  /** Another conversation (or a new chat) opens in place: only the thread, the header and the composer state change. */
  update(p) {
    if (!$('.chat-app') || !CHAT.meta) return false;
    // Multitask: a chat chosen in the list opens in the current pane; switching the mode renders the page again.
    if (mtActive() !== !!$('.mt-app')) return false;
    if ($('.mt-app')) { if (p.args[0]) mtOpenChat(p.args[0]); else if (p.query.get('model')) mtOpenChat(null); history.replaceState({}, '', '/chat'); return true; }
    openConversation(p.args[0], p.query.get('model'));
    return true;
  }
};
/** Models answered on this computer by the desktop app (Claude Code), not by the server. */
function extModels() { try { return (window.chatClient && window.chatClient.externalModels && window.chatClient.externalModels()) || []; } catch { return []; } }
function isExternal(id) { return !!id && extModels().some(m => m.id === id); }
function chooseModelAndSettings(wanted) {
  const meta = CHAT.meta;
  const allIds = [...meta.customModels.map(m => m.id), ...(meta.providerModels || []).map(m => m.id), ...extModels().map(m => m.id)];
  // A chat keeps its model; a new one starts with the model this account last used (remembered by the server for the
  // web app, the desktop app and the CLI alike), then this browser's last pick, then the server's default.
  const saved = LS.get('chat.model');
  const last = meta.lastModel;
  // A chat whose model was removed or turned off (Model control) continues with the usual model instead of failing on
  // every message (unless the model list could not be loaded: then nothing is known to be gone).
  const convModel = CHAT.conv && CHAT.conv.model;
  const convOk = convModel && (allIds.includes(convModel) || !!meta.providerError);
  CHAT.model = (wanted && allIds.includes(wanted) && wanted) || (convOk && convModel) || (last && allIds.includes(last) && last) ||
    (saved && allIds.includes(saved) && saved) || meta.defaultModel || allIds[0];
  if (convModel && !convOk && CHAT.model && CHAT.model !== convModel) toast(`This chat's model (${convModel}) is no longer available: continuing with ${modelName(CHAT.model)}.`, 'warn');
  let defaults = null; try { defaults = newChatDefaults(JSON.parse(LS.get('chat.settings') || 'null')); } catch { /* ignore */ }
  CHAT.settings = CHAT.conv ? Object.assign({ mcpServers: [] }, CHAT.conv.settings) : Object.assign({ webSearch: false, codeInterpreter: false, mcpServers: [], knowledge: [] }, defaults, { device: null });
  if (!CHAT.settings.reasoningEffort) CHAT.settings.reasoningEffort = 'medium';
  if (!CHAT.settings.permissionMode) CHAT.settings.permissionMode = meta.defaultPermissionMode || 'auto';
  if (!CHAT.conv) CHAT.settings.systemPrompt = LS.get('chat.instructions') || null;
  // What this account may not use stays off (Users & access; the server enforces it too).
  if (!meta.canTools) CHAT.settings.mcpServers = [];
  if (!meta.canCode) CHAT.settings.codeInterpreter = false;
  if (meta.canDevices === false) CHAT.settings.device = null;
  if (!CHAT.settings.knowledge) CHAT.settings.knowledge = [];
  if (!meta.knowledge) CHAT.settings.knowledge = [];
  // Image and Video are on one at a time (a chat saved with both keeps Image).
  if (CHAT.settings.imageGeneration && CHAT.settings.videoGeneration) CHAT.settings.videoGeneration = false;
}
async function openConversation(id, wantedModel) {
  const seq = ++CHAT.seq;
  const thread = $('#chat-thread');
  if (CHAT.conv && CHAT.conv.id === id && !wantedModel) { focusComposer(); return; }
  // What the user leaves behind - an unsent message, attached files, where they had scrolled - waits for their return.
  const leaving = CHAT.conv;
  stashDraft();
  rememberScroll();
  thread.classList.add('leaving');
  progress(true);
  let conv = null, failed = null;
  try {
    // A setting changed a moment ago may still be on its way: the chat must come back with it.
    if (CHAT.savingSettings) await CHAT.savingSettings.catch(() => { });
    conv = id ? await get('/api/chat/conversations/' + enc(id)) : null;
  }
  catch (e) { failed = e; }
  finally { progress(false); }
  if (seq !== CHAT.seq || !$('#chat-thread')) return;
  // Could not open it: stay where we were (never turn into an empty new chat).
  if (failed) {
    toast(failed.status === 404 ? 'That chat no longer exists.' : 'Could not open the chat: ' + failed.message, 'bad');
    thread.classList.remove('leaving');
    history.replaceState({}, '', leaving ? '/chat/' + enc(leaving.id) : '/chat');
    S.params = Object.assign({}, S.params, { path: leaving ? '/chat/' + leaving.id : '/chat', args: leaving ? [leaving.id] : [] });
    restoreDraft();
    if (failed.status === 404) refreshList();
    return;
  }
  CHAT.conv = conv;
  rememberOpenChat();
  CHAT.watch = null;
  CHAT.ctx = null;
  chooseModelAndSettings(wantedModel);
  hideQuestion(true);
  $('#model-picker').innerHTML = modelButton();
  $('#chat-head').innerHTML = headHtml();
  $('#temp-slot').innerHTML = tempToggleHtml();
  $('#composer-bar').innerHTML = composerBarHtml();
  hideSelPop();
  renderTps();
  $('#composer-hint').textContent = routeHint();
  const input = $('#chat-input');
  input.placeholder = placeholder();
  thread.innerHTML = threadHtml();
  const live = conv && CHAT.streams.get(conv.id);
  if (live && live.el) { $('.chat-welcome', thread)?.remove(); thread.appendChild(live.el); }
  applyRich(thread);
  watchEarlierBar();
  thread.classList.remove('leaving');
  if (!reducedMotion()) { thread.classList.remove('arriving'); void thread.offsetWidth; thread.classList.add('arriving'); setTimeout(() => { thread.classList.remove('arriving'); syncToBottom(); }, 500); }
  $$('#chat-list .chat-item').forEach(a => a.classList.toggle('active', !!conv && a.dataset.id === conv.id));
  markCompacted();
  restoreDraft();
  restoreScroll();
  syncBusy();
  showPendingQuestion();
  loadContext();
  renderApproval();
  renderDeviceChip();
  renderGoalBar();
  if (typeof renderQueued === 'function') renderQueued();
  attachToLiveRun(conv);
  focusComposer();
  document.dispatchEvent(new CustomEvent('chat:mounted'));
}
/** Unsent messages per chat ("new" for a chat not started yet): the text is kept across reloads, attached files for this visit. */
function draftKey() { return CHAT.conv ? CHAT.conv.id : (CHAT.temporary ? 'temp' : 'new'); }
function savedDrafts() { try { return JSON.parse(LS.get('chat.drafts') || '{}') || {}; } catch { return {}; } }
function stashDraft() {
  const input = $('#chat-input'), files = $('#composer-files');
  if (!input) return;
  CHAT.drafts = CHAT.drafts || new Map();
  const key = draftKey(), text = input.value;
  const nodes = files ? [...files.children] : [];
  if (text.trim() || CHAT.attachments.length || nodes.length) CHAT.drafts.set(key, { text, attachments: CHAT.attachments, nodes });
  else CHAT.drafts.delete(key);
  const all = savedDrafts();
  if (text.trim()) all[key] = { text: text.slice(0, 20000), at: Date.now() }; else delete all[key];
  // The 50 newest only.
  const keep = Object.entries(all).sort((a, b) => b[1].at - a[1].at).slice(0, 50);
  LS.set('chat.drafts', JSON.stringify(Object.fromEntries(keep)));
}
function restoreDraft() {
  const input = $('#chat-input'), files = $('#composer-files');
  if (!input) return;
  const key = draftKey(), d = CHAT.drafts && CHAT.drafts.get(key), saved = savedDrafts()[key];
  input.value = d ? d.text : saved ? saved.text : '';
  CHAT.attachments = d && d.attachments ? d.attachments : [];
  if (files) { files.innerHTML = ''; (d && d.nodes || []).forEach(n => files.appendChild(n)); }
  autosize();
  renderContext();
}
/** Forgets a chat's draft (its message was sent). */
function clearDraft(key) {
  if (CHAT.drafts) CHAT.drafts.delete(key);
  const all = savedDrafts(); if (all[key]) { delete all[key]; LS.set('chat.drafts', JSON.stringify(all)); }
}
/** Where the user was reading in each chat: reopened, it is where they left it (or the newest message when they followed it). */
function rememberScroll() {
  const sc = $('#chat-scroll');
  if (!sc || !CHAT.conv) return;
  CHAT.scrolls = CHAT.scrolls || new Map();
  const gap = sc.scrollHeight - sc.scrollTop - sc.clientHeight;
  CHAT.scrolls.set(CHAT.conv.id, { top: sc.scrollTop, bottom: gap < 80 || CHAT.stick });
}
function restoreScroll() {
  const sc = $('#chat-scroll'), m = CHAT.conv && CHAT.scrolls && CHAT.scrolls.get(CHAT.conv.id);
  if (!sc || !m || m.bottom) { scrollThread(true); return; }
  CHAT.stick = false;
  sc.scrollTop = m.top;
  requestAnimationFrame(() => { sc.scrollTop = m.top; syncToBottom(); });
}
/**
 * Long chats draw only their latest messages: every message (Markdown, highlighted code, tool cards, diffs) costs layout and
 * memory, and a chat of hundreds of messages made the chat tab, the desktop app and VS Code very slow. Older messages come in
 * batches when the user scrolls up to the top (or clicks the bar), and a long session trims them off again at the bottom.
 */
const THREAD_WINDOW = 40;
/**
 * ... and at most this much weight (messageWeight): an agent's answer with dozens of tool calls costs as much as many plain
 * messages, so a busy coding chat draws fewer of them. At least THREAD_MIN messages are drawn.
 */
const THREAD_WEIGHT = 60, THREAD_MIN = 6;
function messageWeight(m) {
  return 1 + ((m && m.toolCalls) || []).length / 6 + String((m && m.content) || '').length / 4000;
}
/** Where a batch that ends before message end starts: back until the window's count or weight is used up. */
function windowStart(msgs, end) {
  let i = end, w = 0;
  while (i > 0 && end - i < THREAD_WINDOW) {
    const next = w + messageWeight(msgs[i - 1]);
    if (next > THREAD_WEIGHT && end - i >= THREAD_MIN) break;
    w = next; i--;
  }
  return i;
}
/** The first message the model still sees in full: those before it are only in the conversation's summary. */
function firstInContext(msgs) {
  const through = CHAT.conv && CHAT.conv.summaryThroughUtc && new Date(CHAT.conv.summaryThroughUtc).getTime();
  if (!through) return 0;
  const i = msgs.findIndex(m => new Date(m.createdUtc).getTime() > through);
  return i < 0 ? msgs.length : i;
}
/** keepFrom: draw at least from this message on (a re-draw keeps what was on the page, or the message the user is at). */
function threadHtml(keepFrom) {
  if (!CHAT.conv || !CHAT.conv.messages.length) { CHAT.renderedFrom = 0; return welcomeHtml(); }
  const msgs = CHAT.conv.messages;
  const at = Number(keepFrom);
  let from = windowStart(msgs, msgs.length);
  // Messages already summarized out of the model's context are not drawn at first (scrolling up brings them).
  const inContext = firstInContext(msgs);
  if (inContext > from && msgs.length - inContext >= THREAD_MIN) from = inContext;
  if (Number.isFinite(at)) from = Math.max(0, Math.min(from, at));
  CHAT.renderedFrom = from;
  return earlierBarHtml(from) + msgs.slice(from).map(safeMessageHtml).join('');
}
/** The bar above the drawn messages while older ones are not drawn. */
function earlierBarHtml(hidden) {
  if (!(hidden > 0)) return '';
  return `<div class="earlier-bar" id="earlier-bar"><button class="btn sm ghost" data-act="chat-earlier">Show earlier messages <span class="muted">(${hidden} more)</span></button></div>`;
}
/** Draws the next batch of older messages above the others, keeping what is on screen where it is. */
function showEarlier() {
  const thread = $('#chat-thread'), sc = $('#chat-scroll'), bar = $('#earlier-bar');
  const msgs = CHAT.conv && CHAT.conv.messages;
  if (!thread || !bar || !msgs || !(CHAT.renderedFrom > 0)) return;
  const from = windowStart(msgs, CHAT.renderedFrom);
  const t = document.createElement('template');
  t.innerHTML = msgs.slice(from, CHAT.renderedFrom).map(safeMessageHtml).join('');
  const nodes = [...t.content.children];
  const before = sc ? sc.scrollHeight : 0;
  bar.after(...nodes);
  nodes.forEach(n => applyRich(n));
  CHAT.renderedFrom = from;
  if (from > 0) bar.outerHTML = earlierBarHtml(from); else bar.remove();
  if (sc) sc.scrollTop += sc.scrollHeight - before;
  markCompacted();
  watchEarlierBar();
}
ACTIONS['chat-earlier'] = () => showEarlier();
/** Scrolling up to the bar draws the earlier messages by itself. */
function watchEarlierBar() {
  if (CHAT.earlierObserver) { CHAT.earlierObserver.disconnect(); CHAT.earlierObserver = null; }
  const bar = $('#earlier-bar'), sc = $('#chat-scroll');
  if (!bar || !sc || typeof IntersectionObserver === 'undefined') return;
  CHAT.earlierObserver = new IntersectionObserver(entries => {
    if (entries.some(e => e.isIntersecting) && !CHAT.stick) showEarlier();
  }, { root: sc, rootMargin: '300px 0px 0px 0px' });
  CHAT.earlierObserver.observe(bar);
}
/**
 * While the user is at the bottom, messages far above are taken off the page again once there are twice as many as the
 * window (an answer after answer session keeps adding them); scrolling up brings them back.
 */
function trimThread() {
  if (!CHAT.conv || !CHAT.stick || currentStream()) return;
  const thread = $('#chat-thread');
  const msgs = CHAT.conv.messages;
  if (!thread || !msgs.length) return;
  // Only once well past the window (twice its count or weight), so a session does not redraw after every answer.
  const drawnFrom = Math.max(0, CHAT.renderedFrom || 0);
  const weight = msgs.slice(drawnFrom).reduce((w, m) => w + messageWeight(m), 0);
  if (msgs.length - drawnFrom <= THREAD_WINDOW * 2 && weight <= THREAD_WEIGHT * 2) return;
  const idx = windowStart(msgs, msgs.length);
  if (idx <= drawnFrom) return;
  const keep = $(`#chat-thread > .msg[data-id="${CSS.escape(msgs[idx].id || '')}"]`);
  if (!keep) return;
  // Everything above the first kept message goes (messages, dividers, the old bar); the bar is drawn again.
  while (thread.firstElementChild && thread.firstElementChild !== keep) thread.firstElementChild.remove();
  CHAT.renderedFrom = idx;
  keep.insertAdjacentHTML('beforebegin', earlierBarHtml(idx));
  markCompacted();
  watchEarlierBar();
  scrollThread(true);
}
/** One message that cannot be drawn shows as plain text instead of taking the whole conversation down with it. */
function safeMessageHtml(m) {
  try { return messageHtml(m); }
  catch (e) {
    console.error('Could not draw message', m && m.id, e);
    return `<div class="msg ${m && m.role === 'user' ? 'user' : 'assistant'}"><div class="bubble-u plain-fallback"><pre class="wrap">${esc((m && m.content) || '')}</pre></div></div>`;
  }
}
function headHtml() {
  if (!CHAT.conv) return '';
  const c = CHAT.conv, project = c.projectId && (CHAT.projects || []).find(p => p.id === c.projectId);
  return `${project ? `<button class="proj-crumb" data-act="proj-open" data-id="${esc(project.id)}" data-tip="Project: show its chats">${FOLDER_ICON}<span>${esc(project.name)}</span></button><span class="crumb-sep">/</span>` : ''}
    <span class="chat-title" data-act="chat-rename" data-tip="Rename">${esc(c.title)}</span>${c.temporary ? `<span class="badge outline temp-badge" data-tip="Not in your history, no memory; deleted 24 hours after the last message">${TEMP_ICON} Temporary</span>` : ''}
    ${c.temporary || (CHAT.meta.canShare === false && !c.shareToken) ? '' : `<button class="btn icon ghost${c.shareToken ? ' shared' : ''}" data-act="chat-share" aria-label="Share" data-tip="${c.shareToken ? 'Shared with a link: manage it' : 'Share a read-only link'}">${SHARE_ICON}</button>`}
    <button class="btn icon ghost" data-act="chat-pin" aria-label="Pin" data-tip="${c.pinned ? 'Unpin' : 'Pin'}"><svg viewBox="0 0 24 24" class="ic${c.pinned ? ' filled' : ''}"><path d="M9 4h6l-1 6 3 3H7l3-3zM12 16v5"/></svg></button>
    ${c.temporary ? '' : bookmarkBtnHtml(c)}
    <div class="chat-more-wrap"><button class="btn icon ghost" data-act="chat-more" aria-label="More" data-tip="More: export, move to a project, delete">${MORE_ICON}</button><div class="menu chat-more-menu hidden" id="chat-more-menu"></div></div>`;
}
function composerBarHtml() {
  const meta = CHAT.meta, s = CHAT.settings, servers = (s.mcpServers || []).length + (!window.chatClient && s.device ? 1 : 0);
  // A model on this computer (Claude Code) brings its own tools and settings: the server's switches do not apply.
  if (isExternal(CHAT.model) && window.chatClient.externalComposerHtml) {
    return `${attachControlHtml()}
      ${window.chatClient.externalComposerHtml(CHAT)}<span class="spacer"></span>
      <button class="btn icon ghost mic-btn${DICTATION.active ? ' on' : ''}" id="mic-btn" data-act="chat-dictate" aria-label="Dictate" data-tip="Dictate: speak your message">${MIC_ICON}</button>
      ${typeof talkBtnHtml === 'function' ? talkBtnHtml() : ''}
      <button class="send-btn" id="send-btn" data-act="chat-send" aria-label="Send"><svg viewBox="0 0 24 24" class="ic send"><path d="M5 12h13M13 6l6 6-6 6"/></svg><svg viewBox="0 0 24 24" class="ic stop"><rect x="7" y="7" width="10" height="10" rx="2"/></svg></button>`;
  }
  return `${attachControlHtml()}
    <button class="chip-toggle${s.webSearch ? ' on' : ''}" data-act="chat-toggle" data-key="webSearch"${meta.webSearch ? ' data-tip="Web search: look things up and cite sources"' : ' disabled data-tip="Web search is not configured"'}>${icon('search')} Web search</button>
    ${meta.imageGeneration ? `<button class="chip-toggle${s.imageGeneration ? ' on' : ''}" data-act="chat-toggle" data-key="imageGeneration" data-tip="Create images: the model draws pictures, or changes a picture you attach (switches Video off)">${IMAGE_ICON} Image</button>` : ''}
    ${meta.videoGeneration ? `<button class="chip-toggle${s.videoGeneration ? ' on' : ''}" data-act="chat-toggle" data-key="videoGeneration" data-tip="Create videos: the model can make short video clips, a few minutes each (switches Image off)">${VIDEO_ICON} Video</button>` : ''}
    ${typeof mediaChipsHtml === 'function' ? mediaChipsHtml(meta, s) : ''}
    ${meta.markets ? `<button class="chip-toggle${s.markets ? ' on' : ''}" data-act="chat-toggle" data-key="markets" data-tip="Money: the model can read live crypto prices and exchange rates, with their changes and history (the Money tab)">${MONEY_ICON} Money</button>` : ''}
    ${meta.stocks ? `<button class="chip-toggle${s.stocks ? ' on' : ''}" data-act="chat-toggle" data-key="stocks" data-tip="Stocks: the model can read stock and index quotes for any ticker, with their changes and history (the Stocks tab)">${STOCKS_ICON} Stocks</button>` : ''}
    ${meta.polymarket ? `<button class="chip-toggle${s.polymarket ? ' on' : ''}" data-act="chat-toggle" data-key="polymarket" data-tip="Polymarket: the model can read prediction markets: odds, live prices, trades and holders (the Polymarket tab)">${POLYMARKET_ICON} Polymarket</button>` : ''}
    ${meta.news ? `<button class="chip-toggle${s.news ? ' on' : ''}" data-act="chat-toggle" data-key="news" data-tip="News: the model can search the latest articles from the news feeds and cite them (the News tab)">${NEWS_ICON} News</button>` : ''}
    ${meta.canCode ? `<button class="chip-toggle${s.codeInterpreter ? ' on' : ''}" data-act="chat-toggle" data-key="codeInterpreter"${meta.codeInterpreter ? ' data-tip="Code interpreter: run Python and Node in the sandbox"' : ' disabled data-tip="No code sandbox is connected"'}>${icon('bolt')} Code interpreter</button>` : ''}
    ${meta.canTools || (meta.canDevices && deviceToolsHtml()) ? `<div class="tools-menu-wrap"><button class="chip-toggle${servers ? ' on' : ''}" data-act="chat-tools" id="tools-btn" data-tip="Tools: MCP servers and your computers for this chat">${toolsBtnHtml()}</button>
        <div class="menu tools-menu hidden" id="tools-menu">${toolsMenuHtml()}</div></div>` : ''}
    ${meta.knowledge ? `<div class="tools-menu-wrap"><button class="chip-toggle${(s.knowledge || []).length ? ' on' : ''}" data-act="chat-knowledge" id="kb-btn" data-tip="Knowledge (RAG): answer from your knowledge bases, with citations">${knowledgeBtnHtml()}</button>
        <div class="menu tools-menu kb-menu hidden" id="kb-menu">${knowledgeMenuHtml()}</div></div>` : ''}
    ${window.chatClient && window.chatClient.composerHtml ? window.chatClient.composerHtml(CHAT) : ''}
    ${typeof runChipHtml === 'function' ? runChipHtml() : ''}
    ${typeof cavemanChipHtml === 'function' ? cavemanChipHtml() : ''}
    ${typeof limitsChipHtml === 'function' ? limitsChipHtml() : ''}
    <button class="chip-toggle${s.systemPrompt ? ' on' : ''}" data-act="chat-system" id="ins-btn" data-tip="${s.systemPrompt ? esc(s.systemPrompt.slice(0, 160)) : 'Instructions for this chat'}">${icon('page')} Instructions</button>
    ${meta.sandboxDownloads ? `<button class="chip-toggle" data-act="chat-files" id="files-btn" data-tip="Sandbox files: browse and download files from the code sandbox">${icon('folder')} Files</button>` : ''}
    <span class="spacer"></span>
    <div class="composer-controls">${comboHtml('effort')}${comboHtml('mode')}${contextHtml()}</div>
    <button class="btn icon ghost mic-btn${DICTATION.active ? ' on' : ''}" id="mic-btn" data-act="chat-dictate" aria-label="Dictate" data-tip="Dictate: speak your message">${MIC_ICON}</button>
    ${typeof talkBtnHtml === 'function' ? talkBtnHtml() : ''}
    <button class="send-btn" id="send-btn" data-act="chat-send" aria-label="Send"><svg viewBox="0 0 24 24" class="ic send"><path d="M5 12h13M13 6l6 6-6 6"/></svg><svg viewBox="0 0 24 24" class="ic stop"><rect x="7" y="7" width="10" height="10" rx="2"/></svg></button>`;
}
/**
 * Keeps every composer control on one line with the send button at the bottom right. When the box is too narrow, gaps
 * tighten first, then the chips with the longest labels show only their icon (their tooltip names them), one at a time,
 * then the other controls get smaller step by step.
 */
const FIT_STEPS = ['fit-2', 'fit-3', 'fit-4', 'fit-5', 'fit-6', 'fit-7'];
function fitComposerBar() {
  const bar = $('#composer-bar'), send = $('#send-btn');
  if (!bar || !send || !bar.clientWidth) return;
  const fits = () => send.getBoundingClientRect().right <= bar.getBoundingClientRect().right + 0.5;
  // Sizes must change at once while measuring: the chips' own transitions would otherwise delay them.
  bar.classList.add('fitting');
  bar.classList.remove('fit-1', ...FIT_STEPS);
  const chips = $$('.chip-toggle', bar);
  chips.forEach(ch => ch.classList.remove('chip-icon'));
  let ok = fits();
  if (!ok) { bar.classList.add('fit-1'); ok = fits(); }
  if (!ok) for (const ch of chips.sort((a, b) => b.offsetWidth - a.offsetWidth)) { ch.classList.add('chip-icon'); if ((ok = fits())) break; }
  if (!ok) for (const step of FIT_STEPS) { bar.classList.add(step); if (fits()) break; }
  void bar.offsetWidth;
  bar.classList.remove('fitting');
}
let fitFontsHooked = false;
function watchComposerFit() {
  const bar = $('#composer-bar');
  if (!bar) return;
  // The bar's width and each control's width (labels, badges, the font arriving) all decide what fits.
  const ro = window.ResizeObserver ? new ResizeObserver(fitComposerBar) : null;
  const track = () => { if (!ro) return; ro.disconnect(); ro.observe(bar); for (const el of bar.children) ro.observe(el); };
  track();
  fitComposerBar();
  new MutationObserver(() => { track(); fitComposerBar(); }).observe(bar, { childList: true, subtree: true, characterData: true });
  if (document.fonts && !fitFontsHooked) { fitFontsHooked = true; document.fonts.addEventListener('loadingdone', fitComposerBar); }
}
/** Touch screens (phones, tablets): Enter starts a new line and the send button sends, like other mobile chat apps. */
const TOUCH_ONLY = matchMedia('(hover: none) and (pointer: coarse)');
/** A model's Model Square price per 1M tokens (input / output) for the model menu; nothing for free models. */
function modelPriceHtml(id) {
  const p = CHAT.meta && CHAT.meta.prices && CHAT.meta.prices[id];
  return p ? `<span class="mm-price" data-tip="USD per 1M input / output tokens">${esc(priceText(p.input, p.output))}</span>` : '';
}
/** An answer the account paid for: its balance in the user menu goes down by that much. */
function noteCharge(message) {
  if (!message || !message.costMicros || !S.me || !S.me.charged) return;
  S.me.balanceMicros -= message.costMicros;
  if (CHAT.meta && CHAT.meta.account) CHAT.meta.account.balanceMicros = S.me.balanceMicros;
  showBalance(S.me);
}
/** A chat that opens is ready to type in, except on touch screens, where focus pops the keyboard up over the chat. */
function focusComposer() { if (!TOUCH_ONLY.matches) $('#chat-input')?.focus(); }
function placeholder() { return `Message ${modelName(CHAT.model)}…`; }
function autosize() { const t = $('#chat-input'); if (!t) return; t.style.height = 'auto'; t.style.height = Math.min(260, t.scrollHeight) + 'px'; }
/** A model's name for people: its display name, else its id made readable ("sev/claude-opus-5-5" → "Claude Opus 5.5"). */
function modelName(id) {
  const m = CHAT.meta && (CHAT.meta.customModels.find(x => x.id === id) || (CHAT.meta.providerModels || []).find(x => x.id === id) || extModels().find(x => x.id === id));
  if (!m && id && id.startsWith('claude-code')) return 'Claude Code';
  if (m && modelDisplayName(m)) return modelDisplayName(m);
  return id ? prettyModelId(id) : 'a model';
}
// ------------------------------------------------------------------ model health: the dot beside each model
// Green, yellow or red from the model's recent calls in Chat, the API and agents (/api/chat/model-status); gray when it had
// none in the last day. Beside it in the menu, the model's speed: average response time and tokens per second. Updated every
// 30 s while a chat is open, in place.
const MDOT_LABEL = { green: 'Working well', yellow: 'Some failures lately', red: 'Failing lately', gray: 'No recent calls' };
/** Tokens per second above which a model is on fire (the speed chip burns, its speed in the menu glows). */
const TPS_FIRE = 200;
function modelStatusOf(id) { return (CHAT.modelStatus || {})[id] || null; }
function fmtMs(ms) { return ms < 1000 ? `${Math.round(ms)} ms` : `${(ms / 1000).toFixed(ms < 10000 ? 1 : 0)} s`; }
function fmtTps(v) { return v >= 100 ? String(Math.round(v)) : v.toFixed(1); }
function modelDotTip(id) {
  const s = modelStatusOf(id);
  if (!s) return 'No calls in the last 24 hours (Chat, API or agents)';
  const from = [s.chat ? `chat ${s.chat}` : '', s.api ? `API ${s.api}` : '', s.agents ? `agents ${s.agents}` : ''].filter(Boolean).join(' · ');
  let t = `${MDOT_LABEL[s.state] || s.state}: ${s.calls - s.failures} of ${plural(s.calls, 'call')} succeeded ${s.window === '1h' ? 'in the last hour' : '(its latest calls)'}`;
  if (from) t += ` (${from})`;
  if (s.tps) t += ` · ${fmtMs(s.responseMs)} average response, ${fmtTps(s.tps)} tokens/s`;
  if (s.failures && s.lastFailureUtc) t += ` · last failure ${ago(s.lastFailureUtc)}: ${s.lastErrorKind || 'error'}${s.lastError ? ` (${s.lastError})` : ''}`;
  return t + ` · last call ${ago(s.lastUtc)}`;
}
function modelDotHtml(id) {
  if (!id || isExternal(id)) return '';
  const s = modelStatusOf(id), st = s ? s.state : 'gray';
  return `<i class="mdot s-${esc(st)}" data-mdot="${esc(id)}" data-tip="${esc(modelDotTip(id))}" role="img" aria-label="${esc(MDOT_LABEL[st] || st)}"></i>`;
}
/** A model's speed in the menu: "2.3 s · 85 tok/s" (empty without a finished call in the last day). */
function modelSpeedText(s) { return s && s.tps ? `${fmtMs(s.responseMs)} · ${fmtTps(s.tps)} tok/s` : ''; }
function modelSpeedTip(s) {
  if (!s || !s.tps) return 'No finished calls in the last 24 hours';
  return `Average response time ${fmtMs(s.responseMs)}${s.firstTokenMs != null ? ` (first token after ${fmtMs(s.firstTokenMs)})` : ''} · ` +
    `writes ${fmtTps(s.tps)} tokens per second${s.tps > TPS_FIRE ? ' — on fire' : ''} · from its last ${plural(s.speedCalls, 'call')} (Chat, API and agents)`;
}
function modelSpeedHtml(id) {
  if (!id || isExternal(id)) return '';
  const s = modelStatusOf(id);
  return `<span class="mm-speed${s && s.tps > TPS_FIRE ? ' hot' : ''}" data-mspeed="${esc(id)}" data-tip="${esc(modelSpeedTip(s))}">${esc(modelSpeedText(s))}</span>`;
}
function syncModelDots() {
  $$('[data-mdot]').forEach(el => {
    const s = modelStatusOf(el.dataset.mdot), st = s ? s.state : 'gray';
    el.className = 'mdot s-' + st; el.dataset.tip = modelDotTip(el.dataset.mdot); el.setAttribute('aria-label', MDOT_LABEL[st] || st);
  });
  $$('[data-mspeed]').forEach(el => {
    const s = modelStatusOf(el.dataset.mspeed);
    el.textContent = modelSpeedText(s); el.dataset.tip = modelSpeedTip(s); el.classList.toggle('hot', !!(s && s.tps > TPS_FIRE));
  });
}
async function refreshModelStatus() {
  try { const r = await get('/api/chat/model-status'); CHAT.modelStatus = (r && r.models) || {}; CHAT.modelStatusAt = Date.now(); syncModelDots(); }
  catch { /* the last known dots stay */ }
}
setInterval(() => { if (CHAT.meta && $('.chat-app') && !document.hidden) refreshModelStatus(); }, 30000);

function modelButton() {
  const custom = CHAT.meta.customModels.find(x => x.id === CHAT.model);
  // The readable name only; the model's id is in the tooltip (and in the model list).
  return `<button class="model-btn" data-act="model-menu" data-tip="${esc(CHAT.model || '')}">${modelDotHtml(CHAT.model)}${custom ? '<span class="badge accent">custom</span> ' : ''}<b>${esc(modelName(CHAT.model))}</b>
    <svg viewBox="0 0 24 24" class="ic"><path d="M6 9l6 6 6-6"/></svg></button><div class="menu model-menu hidden" id="model-menu"></div>`;
}
function routeHint() {
  const c = CHAT.meta.customModels.find(x => x.id === CHAT.model);
  if (c) return `${c.name} routes each message: ${c.routes.slice(0, 6).join(', ')}${c.routes.length > 6 ? '…' : ''} — anything else goes to its default model.`;
  return '';
}
function welcomeHtml() {
  const project = CHAT.project && (CHAT.projects || []).find(p => p.id === CHAT.project);
  if (CHAT.temporary) {
    return `<div class="chat-welcome temp"><div class="welcome-logo temp-logo">${TEMP_ICON}</div><h2>Temporary chat</h2>
      <p class="muted">It will not appear in your history, the assistant will not use or update its memory of you, and it is deleted 24 hours after the last message.</p></div>`;
  }
  return `<div class="chat-welcome"><div class="welcome-logo"><img src="/assets/img/logo.png" alt="Agentic"></div>
    <h2>${project ? esc(project.name) : 'How can I help?'}</h2><p class="muted">${project ? `New chat in this project: its instructions${project.files && project.files.length ? ` and ${project.files.length} file${project.files.length === 1 ? '' : 's'}` : ''} go with every message.` : (isExternal(CHAT.model) ? `<b>${esc(modelName(CHAT.model))}</b> on this computer, with its own tools, MCP servers, skills and settings. Choose its model, permissions and folder with the Claude button below; type / for its commands.` : `Using <b>${esc(modelName(CHAT.model))}</b>. ${CHAT.meta.canTools ? 'Attach files, turn on web search or tools, and run the code you get.' : 'Attach files or turn on web search.'}`)}</p>
    ${project ? `<div class="proj-welcome-actions"><button class="btn small" data-act="proj-edit" data-id="${esc(project.id)}">${FOLDER_ICON} Project settings</button></div>` : ''}
    ${isExternal(CHAT.model) ? '' : `<div class="welcome-suggest">${welcomeSuggestions().map(([label, text]) => `<button type="button" class="suggest-chip" data-act="welcome-suggest" data-text="${esc(text)}" title="${esc(label)}">${esc(label)}</button>`).join('')}</div>`}</div>`;
}
/** Ways to start: coding work where the app has tools on this computer (desktop, VS Code), everyday work elsewhere. */
function welcomeSuggestions() {
  if (window.chatClient) return [['Explain this project', 'Explain how this project is structured: the main parts, how they fit together, and where to start reading.'],
    ['Find and fix a bug', 'Find the cause of this bug and fix it: '], ['Write tests', 'Write tests for '], ['Review my changes', 'Review my uncommitted changes: bugs, risks and anything that should be simpler.']];
  // Six of a hundred ideas (WELCOME_IDEAS), new ones each time a new chat opens; the same while it stays open.
  if (!CHAT.welcomeIdeas) CHAT.welcomeIdeas = randomWelcomeIdeas(6);
  return CHAT.welcomeIdeas;
}
ACTIONS['welcome-suggest'] = el => {
  const t = $('#chat-input');
  if (!t) return;
  t.value = el.dataset.text || '';
  t.dispatchEvent(new Event('input', { bubbles: true }));
  autosize();
  t.focus();
  t.setSelectionRange(t.value.length, t.value.length);
};
ACTIONS['chat-bottom'] = () => { const sc = $('#chat-scroll'); CHAT.stick = true; $('.to-bottom')?.classList.add('hidden'); sc.scrollTo({ top: sc.scrollHeight, behavior: reducedMotion() ? 'auto' : 'smooth' }); };

// ------------------------------------------------------------------ sidebar
function chatListHtml(fresh) {
  const q = CHAT.q.toLowerCase();
  const list = CHAT.list.filter(c => (!CHAT.project || c.projectId === CHAT.project) && (!CHAT.bookmarksOnly || c.bookmarked) &&
    (!q || (c.title || '').toLowerCase().includes(q) || (c.preview || '').toLowerCase().includes(q)));
  if (!list.length) return `<div class="muted small chat-empty">${q ? 'No chats match.' : CHAT.bookmarksOnly ? 'No bookmarked chats yet: bookmark a chat with the bookmark button at the top of it.' : CHAT.project ? 'No chats in this project yet.' : 'No chats yet.'}</div>`;
  const now = Date.now(), day = 864e5;
  const bucket = c => c.pinned ? 'Pinned' : (now - new Date(c.updatedUtc)) < day ? 'Today' : (now - new Date(c.updatedUtc)) < 7 * day ? 'Previous 7 days' : (now - new Date(c.updatedUtc)) < 30 * day ? 'Previous 30 days' : 'Older';
  let html = '', last = null;
  list.forEach((c, i) => {
    const b = bucket(c);
    if (b !== last) { html += `<div class="chat-group">${esc(b)}</div>`; last = b; }
    const isNew = fresh && fresh.has(c.id);
    const needs = c.needsYou || [...CHAT.approvals.values()].some(a => a.chatId === c.id);
    if (CHAT.managing) {
      const busy = CHAT.streams.has(c.id) || CHAT.live.has(c.id) || c.live, on = CHAT.checked.has(c.id);
      html += `<label class="chat-item managing${on ? ' checked' : ''}${busy ? ' busy' : ''}" data-id="${esc(c.id)}" data-i="${Math.min(i, 20)}"${busy ? ' data-tip="Being answered: it can be deleted when the answer is finished"' : ''}>
        <input type="checkbox" class="chat-check" data-change="chat-check" value="${esc(c.id)}"${on ? ' checked' : ''}${busy ? ' disabled' : ''}>
        <span class="chat-item-text"><span class="t">${esc(c.title)}</span><span class="p">${esc(previewText(c.preview).slice(0, 70))}</span></span></label>`;
      return;
    }
    html += `<a class="chat-item${CHAT.conv && CHAT.conv.id === c.id ? ' active' : ''}${isNew ? ' appear' : ''}${fresh ? ' still' : ''}${needs ? ' needs-you' : ''}" href="/chat/${enc(c.id)}" data-nav data-id="${esc(c.id)}" data-i="${Math.min(i, 20)}">
      <span class="t">${c.pinned ? '<svg viewBox="0 0 24 24" class="ic pin"><path d="M9 4h6l-1 6 3 3H7l3-3zM12 16v5"/></svg>' : ''}${c.projectId && !CHAT.project ? FOLDER_ICON.replace('class="ic', 'class="ic in-proj') : ''}${esc(c.title)}${c.bookmarked ? BOOKMARK_ICON.replace('class="ic', 'class="ic bm-mark') : ''}${c.isShared ? SHARE_ICON.replace('class="ic', 'class="ic shared-mark') : ''}</span><span class="p">${esc(previewText(c.preview).slice(0, 70))}</span>
      ${CHAT.streams.has(c.id) || CHAT.live.has(c.id) || c.live ? '<span class="item-live" data-tip="Answering…"></span>' : ''}</a>`;
  });
  return html;
}
/** Reloads the list; items that were not there before slide in, the rest stay put. */
async function refreshList() {
  const before = new Set(CHAT.list.map(c => c.id));
  CHAT.list = await get('/api/chat/conversations').catch(() => CHAT.list);
  const el = $('#chat-list');
  if (!el) return;
  const fresh = new Set(CHAT.list.filter(c => !before.has(c.id)).map(c => c.id));
  el.innerHTML = chatListHtml(fresh);
  applyRich(el);
  if (CHAT.managing) { CHAT.checked.forEach(id => { if (!CHAT.list.some(x => x.id === id)) CHAT.checked.delete(id); }); renderManageBar(); }
}

// ------------------------------------------------------------------ managing chats: check several, delete them at once
const MANAGE_ICON = '<svg viewBox="0 0 24 24" class="ic"><path d="M4 6l1.5 1.5L8 5M4 12l1.5 1.5L8 11M4 18l1.5 1.5L8 17M11 6h9M11 12h9M11 18h9"/></svg>';
function manageBarHtml() {
  const n = CHAT.checked.size;
  return `<div class="manage-row"><span class="manage-count" id="manage-count">${n ? `<b>${n}</b> selected` : 'Select chats'}</span><span class="spacer"></span>
      <button class="btn xs ghost" data-act="chat-check-all">${icon('check')} Check all</button><button class="btn xs ghost" data-act="chat-uncheck-all">Uncheck all</button></div>
    <button class="btn danger small manage-delete" data-act="chat-delete-checked"${n ? '' : ' disabled'}>${icon('trash')} ${n ? `Delete ${n} chat${n === 1 ? '' : 's'}` : 'Delete checked'}</button>`;
}
function renderManageBar() { const bar = $('#manage-bar'); if (bar) bar.innerHTML = manageBarHtml(); }
function renderChatList() { const l = $('#chat-list'); if (l) { l.innerHTML = chatListHtml(); applyRich(l); } }
ACTIONS['chat-manage'] = el => {
  CHAT.managing = !CHAT.managing;
  CHAT.checked.clear();
  el.classList.toggle('on', CHAT.managing);
  el.setAttribute('aria-pressed', String(CHAT.managing));
  const bar = $('#manage-bar');
  bar.innerHTML = manageBarHtml();
  bar.classList.toggle('hidden', !CHAT.managing);
  $('.chat-side').classList.toggle('managing', CHAT.managing);
  renderChatList();
};
CHANGES['chat-check'] = el => {
  if (el.checked) CHAT.checked.add(el.value); else CHAT.checked.delete(el.value);
  el.closest('.chat-item').classList.toggle('checked', el.checked);
  renderManageBar();
};
/** Checks every chat in the list (the ones the search shows), except chats being answered. */
ACTIONS['chat-check-all'] = () => {
  $$('#chat-list .chat-check:not(:disabled)').forEach(b => { b.checked = true; CHAT.checked.add(b.value); b.closest('.chat-item').classList.add('checked'); });
  renderManageBar();
};
ACTIONS['chat-uncheck-all'] = () => {
  CHAT.checked.clear();
  $$('#chat-list .chat-check').forEach(b => { b.checked = false; b.closest('.chat-item').classList.remove('checked'); });
  renderManageBar();
};
ACTIONS['chat-delete-checked'] = async () => {
  const ids = [...CHAT.checked];
  if (!ids.length) return;
  if (!await confirmBox(ids.length === 1 ? 'Delete chat' : `Delete ${ids.length} chats`, `Delete ${ids.length === 1 ? 'this conversation' : 'these ' + ids.length + ' conversations'} for good?`, { danger: true, confirm: 'Delete' })) return;
  let r;
  try { r = await post('/api/chat/conversations/delete', { ids }); } catch (e) { toast(e.message, 'bad'); return; }
  await removeChatsFromList(r.deleted || []);
  toast(`${r.deleted.length} chat${r.deleted.length === 1 ? '' : 's'} deleted${r.skipped && r.skipped.length ? ` · ${r.skipped.length} still being answered` : ''}`, 'ok');
};
/** Takes deleted chats out of the list (they slide away); the open chat closes if it was one of them. */
async function removeChatsFromList(ids) {
  const gone = new Set(ids);
  if (!gone.size) return;
  const items = $$('#chat-list .chat-item').filter(i => gone.has(i.dataset.id));
  if (items.length && !reducedMotion()) { items.forEach(i => i.classList.add('removing')); await new Promise(res => setTimeout(res, 260)); }
  CHAT.list = CHAT.list.filter(x => !gone.has(x.id));
  gone.forEach(id => CHAT.checked.delete(id));
  if (CHAT.managing && !CHAT.list.length) { CHAT.managing = false; $('#manage-bar')?.classList.add('hidden'); $('.manage-btn')?.classList.remove('on'); $('.chat-side')?.classList.remove('managing'); }
  renderChatList();
  renderManageBar();
  if (CHAT.conv && gone.has(CHAT.conv.id)) navigate('/chat');
}
const chatSearch = el => { CHAT.q = el.value.trim(); const l = $('#chat-list'); l.innerHTML = chatListHtml(); applyRich(l); };
ACTIONS['chat-new'] = el => {
  el.classList.remove('pulse'); void el.offsetWidth; el.classList.add('pulse');
  if ($('.mt-app')) { mtOpenChat(null); return; }
  LS.set('chat.lastConv', '');
  if (!CHAT.conv) { const t = $('#chat-input'); t.focus(); return; }
  navigate('/chat');
};
/** The chat open now, to come back to after visiting another page (or reopening the app). */
function rememberOpenChat() { if (!IN_PANE) LS.set('chat.lastConv', CHAT.conv ? CHAT.conv.id : ''); }
/**
 * Back on the chat page while this window is still writing (or watching) the chat's answer: that answer kept going while you
 * were away, in an element no longer on the page. It goes back into the thread, with the task list above the message box,
 * instead of the chat looking finished or out of step until the answer ends.
 */
function reattachLive() {
  const thread = $('#chat-thread');
  if (!thread || !CHAT.conv) return;
  const own = CHAT.streams.get(CHAT.conv.id);
  const watched = CHAT.watch && !CHAT.watch.closed && CHAT.watch.chatId === CHAT.conv.id ? CHAT.watch : null;
  for (const holder of [own, watched]) {
    const el = holder && holder.el;
    if (!el || el.isConnected) continue;
    $('.chat-welcome', thread)?.remove();
    // The saved copy of the same answer (written so far) gives way to the live one.
    const id = (holder.view && holder.view.live && holder.view.live.id) || (holder.live && holder.live.id);
    if (id) $(`.msg[data-id="${CSS.escape(id)}"]`, thread)?.remove();
    thread.appendChild(el);
  }
  renderTodoDock();
  syncBusy();
}
ACTIONS['chat-side'] = () => { const a = $('.chat-app'); a.classList.toggle('side-closed'); LS.set('chat.side', a.classList.contains('side-closed') ? 'closed' : 'open'); };
ACTIONS['chat-delete'] = async el => {
  if (!await confirmBox('Delete chat', 'Delete this conversation for good?', { danger: true, confirm: 'Delete' })) return;
  const id = el.dataset.id;
  await del('/api/chat/conversations/' + enc(id));
  const item = $(`#chat-list .chat-item[data-id="${CSS.escape(id)}"]`);
  if (item && !reducedMotion()) { item.classList.add('removing'); await new Promise(r => setTimeout(r, 260)); }
  CHAT.list = CHAT.list.filter(c => c.id !== id);
  toast('Chat deleted', 'ok');
  // Deleting the open chat starts a new one; deleting another one leaves the open chat as it is.
  if (!CHAT.conv || CHAT.conv.id === id) { LS.set('chat.lastConv', ''); navigate('/chat'); }
  const l = $('#chat-list'); if (l) { l.innerHTML = chatListHtml(); applyRich(l); }
};
ACTIONS['chat-pin'] = async () => {
  await patch('/api/chat/conversations/' + enc(CHAT.conv.id), { pinned: !CHAT.conv.pinned });
  CHAT.conv.pinned = !CHAT.conv.pinned;
  $('#chat-head').innerHTML = headHtml();
  const pin = $('#chat-head [data-act="chat-pin"]'); pin.classList.add('pop');
  refreshList();
};
ACTIONS['chat-rename'] = async () => {
  const name = await confirmBox('Rename chat', 'New title', { input: 'Title', value: CHAT.conv.title, confirm: 'Rename' });
  if (typeof name !== 'string' || !name.trim()) return;
  await patch('/api/chat/conversations/' + enc(CHAT.conv.id), { title: name.trim() });
  setTitleAnimated(CHAT.conv.id, name.trim()); refreshList();
};
function setTitleAnimated(convId, title) {
  if (CHAT.conv && CHAT.conv.id === convId) CHAT.conv.title = title;
  const t = $('.chat-title');
  if (!t || !CHAT.conv || CHAT.conv.id !== convId) return;
  const token = (t._retitle || 0) + 1; t._retitle = token;
  if (reducedMotion()) { t.textContent = title; return; }
  t.classList.add('retitle');
  setTimeout(() => {
    if (t._retitle !== token) return;
    t.classList.remove('retitle'); t.textContent = ''; let i = 0;
    const step = () => { if (t._retitle !== token) return; t.textContent = title.slice(0, ++i); if (i < title.length) setTimeout(step, 18); };
    step();
  }, 160);
}

// ------------------------------------------------------------------ model picker, tools, instructions
// ------------------------------------------------------------------ the model picker: tabs
// Favorites (the account's starred models, the same in every app); Agentic: the custom models and the models without a
// provider prefix (the local servers: A1, FLINT…); then one tab per provider (providers merged on the server are one tab:
// Chat.ProviderMerges); then the account's own models and the models on this computer. Searching looks through every tab.
function modelGroupOf(m) { return m.group != null ? m.group : (m.id.includes('/') ? m.id.split('/')[0] : ''); }
/** The star on a model in the picker: it adds the model to Favorites (or takes it out) without choosing it. */
function favStarHtml(id) {
  const on = !!(CHAT.favorites && CHAT.favorites.has(id)), tip = on ? 'Remove from favorites' : 'Add to favorites';
  return `<span class="mm-fav${on ? ' on' : ''}" role="button" tabindex="-1" data-act="model-fav" data-id="${esc(id)}" data-keep-menu aria-label="${tip}" data-tip="${tip}">${on ? '★' : '☆'}</span>`;
}
function customItemHtml(m) {
  return `<button class="mm-item custom${m.id === CHAT.model ? ' active' : ''}" data-act="model-pick" data-id="${esc(m.id)}" data-s="${esc((m.id + ' ' + m.name + ' ' + m.routes.join(' ') + ' agentic custom').toLowerCase())}">
        <span><span class="mm-name">${modelDotHtml(m.id)}<b>${esc(m.name)}</b> <span class="badge accent">custom</span>${m.routes && m.routes.length ? ` <span class="mm-routes">${num(m.routes.length)} routes</span>` : ''}</span>${m.description ? `<span class="cell-sub">${esc(m.description)}</span>` : ''}</span><span class="mm-side">${modelPriceHtml(m.id)}${modelSpeedHtml(m.id)}</span>${favStarHtml(m.id)}</button>`;
}
function extItemHtml(m) {
  return `<button class="mm-item${m.id === CHAT.model ? ' active' : ''}" data-act="model-pick" data-id="${esc(m.id)}" data-s="${esc((m.id + ' ' + m.name + ' ' + (m.description || '')).toLowerCase())}">
        <span><b>${esc(m.name)}</b> <span class="badge ok">this PC</span><span class="cell-sub">${esc(m.description || '')}</span></span>${favStarHtml(m.id)}</button>`;
}
function providerItemHtml(m) {
  // Without a display name, the id people can read ("Claude Opus 5.5"), as in the header; the id itself is under it.
  return `<button class="mm-item${m.id === CHAT.model ? ' active' : ''}" data-act="model-pick" data-id="${esc(m.id)}" data-s="${esc((m.id + ' ' + m.name + ' ' + prettyModelId(m.id) + ' ' + (modelGroupOf(m) || 'agentic')).toLowerCase())}"><span><span class="mm-name">${modelDotHtml(m.id)}${esc(modelDisplayName(m) || prettyModelId(m.id))}${m.byo ? ' <span class="badge ok">your key</span>' : ''}</span><span class="cell-sub">${esc(m.byo ? `${m.provider} · free` : m.id)}</span></span><span class="mm-side">${m.byo ? '' : modelPriceHtml(m.id)}${modelSpeedHtml(m.id)}</span>${favStarHtml(m.id)}</button>`;
}
function modelTabs() {
  const tabs = [];
  const tab = (key, label) => { let t = tabs.find(x => x.key === key); if (!t) { t = { key, label, html: [], ids: [] }; tabs.push(t); } return t; };
  const put = (t, m, html) => { t.html.push(html); t.ids.push(m.id); };
  const prov = CHAT.meta.providerModels || [];
  // Favorites first, in the order they were starred (models no longer offered are left out).
  if (CHAT.favorites && CHAT.favorites.size) {
    const fav = tab('fav', '★ Favorites');
    CHAT.favorites.forEach(id => {
      const c = CHAT.meta.customModels.find(m => m.id === id), p = prov.find(m => m.id === id), x = extModels().find(m => m.id === id);
      if (c) put(fav, c, customItemHtml(c)); else if (p) put(fav, p, providerItemHtml(p)); else if (x) put(fav, x, extItemHtml(x));
    });
  }
  const agentic = tab('agentic', 'Agentic');
  CHAT.meta.customModels.forEach(m => put(agentic, m, customItemHtml(m)));
  // The server's own models (A1, FLINT: no provider prefix) under a line after the custom models.
  const local = prov.filter(m => !m.byo && !modelGroupOf(m));
  if (agentic.html.length && local.length) agentic.html.push('<div class="mm-sep" role="separator"><span>Local models</span></div>');
  local.forEach(m => put(agentic, m, providerItemHtml(m)));
  [...new Set(prov.filter(m => !m.byo).map(modelGroupOf).filter(Boolean))].sort((a, b) => a.localeCompare(b)).forEach(g => {
    const t = tab('p:' + g, g);
    prov.filter(m => !m.byo && modelGroupOf(m) === g).forEach(m => put(t, m, providerItemHtml(m)));
  });
  prov.filter(m => m.byo).forEach(m => put(tab('you', 'Your models'), m, providerItemHtml(m)));
  extModels().forEach(m => put(tab('pc', 'This PC'), m, extItemHtml(m)));
  return tabs.filter(t => t.html.length);
}
function showModelTab(menu, key) {
  $$('.mm-tab', menu).forEach(b => { const on = b.dataset.mmTab === key; b.classList.toggle('active', on); b.setAttribute('aria-selected', on); });
  $$('.mm-pane', menu).forEach(p => p.classList.toggle('hidden', p.dataset.mmPane !== key));
  menu.dataset.tab = key;
  LS.set('chat.modelTab', key);
}
/** Draws the open model picker with a tab open (the first when that tab is gone) and a search (kept when drawn again). */
function renderModelMenu(menu, tabKey, query) {
  const tabs = modelTabs();
  const current = (tabs.find(t => t.key === tabKey) || tabs[0] || {}).key;
  const total = new Set(tabs.flatMap(t => t.ids)).size;   // a favorite counts once
  menu.innerHTML = `<div class="mm-search" data-keep-menu>${icon('search')}<input id="mm-q" placeholder="Search ${num(total)} models" autocomplete="off"></div>` +
    (tabs.length > 1 ? `<div class="mm-tabs" role="tablist" data-keep-menu>${tabs.map(t => `<button type="button" class="mm-tab${t.key === current ? ' active' : ''}${t.key === 'fav' ? ' mm-tab-fav' : ''}" role="tab" aria-selected="${t.key === current}" data-mm-tab="${esc(t.key)}">${esc(t.label)}<span class="mm-tab-n">${num(t.ids.length)}</span></button>`).join('')}</div>` : '') +
    `<div class="mm-list" id="mm-list">` +
    tabs.map(t => `<div class="mm-pane${t.key === current ? '' : ' hidden'}" data-mm-pane="${esc(t.key)}" role="tabpanel"><div class="mm-group mm-pane-title">${esc(t.label)} <span class="muted">${num(t.ids.length)}</span></div>${t.html.join('')}</div>`).join('') +
    (CHAT.meta.providerError ? `<div class="notice warn small">${esc(CHAT.meta.providerError)}</div>` : '') + '</div>';
  menu.dataset.tab = current || '';
  $$('.mm-tab', menu).forEach(b => b.addEventListener('click', () => { const q = $('#mm-q'); if (q.value) { q.value = ''; q.dispatchEvent(new Event('input')); } showModelTab(menu, b.dataset.mmTab); q.focus(); }));
  const q = $('#mm-q', menu);
  q.addEventListener('input', () => {
    const v = q.value.trim().toLowerCase();
    const list = $('#mm-list', menu);
    list.classList.toggle('searching', !!v);
    $('.mm-tabs', menu)?.classList.toggle('searching', !!v);
    $$('.mm-item', menu).forEach(b => b.classList.toggle('hidden', !!v && !b.dataset.s.includes(v)));
    // Searching: every tab's matches, each under its tab's name (a favorite under its provider only); otherwise the open tab.
    $$('.mm-pane', menu).forEach(p => p.classList.toggle('hidden', v ? (p.dataset.mmPane === 'fav' || !$('.mm-item:not(.hidden)', p)) : p.dataset.mmPane !== menu.dataset.tab));
  });
  q.addEventListener('keydown', ev => { if (keyIs(ev, 'list.pick')) { const first = $('.mm-pane:not(.hidden) .mm-item:not(.hidden)', menu); if (first) { ev.preventDefault(); first.click(); } } });
  if (query) { q.value = query; q.dispatchEvent(new Event('input')); }
}
ACTIONS['model-menu'] = () => {
  const menu = $('#model-menu');
  if (!menu.classList.contains('hidden')) { menu.classList.add('hidden'); return; }
  // Fresh health dots when the last ones are older than 10 s (they update in place when they arrive).
  if (!CHAT.modelStatusAt || Date.now() - CHAT.modelStatusAt > 10000) refreshModelStatus();
  // The tab of the model in use (Favorites when it is one), else the one opened last, else the first.
  const tabs = modelTabs();
  renderModelMenu(menu, (tabs.find(t => t.ids.includes(CHAT.model)) || tabs.find(t => t.key === LS.get('chat.modelTab')) || tabs[0] || {}).key);
  menu.classList.remove('hidden');
  $('#mm-q', menu).focus();
  const active = $('.mm-pane:not(.hidden) .mm-item.active', menu); if (active) active.scrollIntoView({ block: 'center' });
  setTimeout(() => document.addEventListener('click', function close(ev) { if (!ev.target.closest('#model-picker')) { menu.classList.add('hidden'); document.removeEventListener('click', close); } }), 0);
};
/** The star: a model into Favorites or out of it (saved for the account), the picker staying where it was. */
ACTIONS['model-fav'] = el => {
  const id = el.dataset.id;
  if (!id) return;
  CHAT.favorites = CHAT.favorites || new Set();
  if (CHAT.favorites.has(id)) CHAT.favorites.delete(id); else CHAT.favorites.add(id);
  put('/api/chat/preferences', { favorites: [...CHAT.favorites] }).catch(e => toast(e.message || 'Could not save your favorites', 'bad'));
  const menu = $('#model-menu');
  if (!menu || menu.classList.contains('hidden')) return;
  // Drawn again once this click is over: its star must still be in the picker while the click goes on (an outside click closes it).
  setTimeout(() => {
    if (menu.classList.contains('hidden')) return;
    const list = $('#mm-list', menu), top = list ? list.scrollTop : 0, query = ($('#mm-q', menu) || {}).value || '';
    renderModelMenu(menu, menu.dataset.tab, query);
    const again = $('#mm-list', menu); if (again) again.scrollTop = top;
    $('#mm-q', menu).focus();
  }, 0);
};
ACTIONS['model-pick'] = async el => {
  CHAT.model = el.dataset.id; LS.set('chat.model', CHAT.model);
  $('#model-picker').innerHTML = modelButton();
  // Claude Code and server models have different controls.
  const bar = $('#composer-bar'); if (bar) bar.innerHTML = composerBarHtml();
  const btn = $('#model-picker .model-btn'); btn.classList.add('changed');
  $('#chat-input').placeholder = placeholder();
  $('#composer-hint').textContent = routeHint();
  const w = $('.chat-welcome p b'); if (w) w.textContent = modelName(CHAT.model);
  if (CHAT.conv) loadContext(); else renderContext();
  CHAT.meta.lastModel = CHAT.model;
  if (CHAT.conv) await patch('/api/chat/conversations/' + enc(CHAT.conv.id), { model: CHAT.model });
  else put('/api/chat/preferences', { lastModel: CHAT.model }).catch(() => { });
};
function toolsMenuHtml() {
  const servers = CHAT.meta.servers || [];
  if (!CHAT.meta.canTools) return deviceToolsHtml();
  return deviceToolsHtml() + `<div class="menu-title">MCP servers the model may use</div>` + (servers.length ? servers.map(s => `<label class="tool-opt${s.connected ? '' : ' off'}"><input type="checkbox" data-change="chat-server" value="${esc(s.id)}"${(CHAT.settings.mcpServers || []).includes(s.id) ? ' checked' : ''}${s.connected ? '' : ' disabled'}>
      <span><b>${esc(s.name)}</b> <span class="muted small">${s.connected ? num(s.tools) + ' tools' : 'not connected'}</span><span class="cell-sub">${esc((s.description || '').slice(0, 110))}</span></span></label>`).join('') : '<div class="muted small">No MCP servers are configured.</div>') +
    `<a class="menu-link small" href="/my-mcp" data-nav>Add your own MCP server…</a>` +
    (CHAT.meta.isAdmin ? `<hr><label class="tool-opt"><input type="checkbox" data-change="chat-approval"${CHAT.settings.allowApprovalTools ? ' checked' : ''}><span><b>Allow tools that need approval</b><span class="cell-sub">e.g. shell run_command — they run without asking</span></span></label>` : '');
}
/** In the web app: your desktop apps, and the tools each offers (files, commands, skills, its MCP servers) for this chat. */
function deviceToolsHtml() {
  if (window.chatClient) return ''; // the desktop app has its own This PC and Skills menus
  if (CHAT.meta && CHAT.meta.canDevices === false) return '';   // Users & access: no tools on own computers
  const cur = CHAT.settings.device;
  const devs = [...CHAT.devices.values()].filter(d => d.online || d.id === cur);
  if (!devs.length) return '';
  const off = new Set(CHAT.settings.deviceToolsOff || []);
  return `<div class="menu-title">Tools on your computers</div>` + devs.map(d => {
    const on = cur === d.id, cat = CHAT.catalogs.get(d.id), tools = cat ? cat.tools : [];
    const groups = {};
    tools.forEach(t => (groups[t.group] = groups[t.group] || []).push(t));
    const used = tools.filter(t => !off.has(t.name)).length;
    return `<div class="dev-tools${on ? ' on' : ''}">
      <label class="tool-opt${d.online ? '' : ' off'}"><input type="checkbox" data-change="chat-device" value="${esc(d.id)}"${on ? ' checked' : ''}${d.online || on ? '' : ' disabled'}>
        <span><b>${DEVICE_ICON}${esc(d.name || 'Desktop app')}</b> <span class="muted small">${!d.online ? 'not connected' : cat ? (on ? `${used} of ${tools.length} tools` : `${tools.length} tools`) : (d.tools || 0) + ' tools'}</span>
        <span class="cell-sub">${on ? 'The desktop app runs these tools on that computer.' : 'Use files, commands, skills and MCP servers on that computer in this chat.'}</span></span></label>
      ${on && !cat && d.online ? '<div class="muted small dev-loading"><span class="spinner"></span> Loading its tools…</div>' : ''}
      ${on && cat ? Object.entries(groups).map(([g, list]) => {
        const n = list.filter(t => !off.has(t.name)).length, open = CHAT.devOpen.has(g);
        return `<div class="dev-group${open ? ' open' : ''}"><div class="dev-group-head">
            <label class="dev-group-check"><input type="checkbox" data-change="chat-device-group" data-group="${esc(g)}"${n ? ' checked' : ''} data-mixed="${n > 0 && n < list.length}"><span>${esc(g)}</span></label>
            <button type="button" class="dev-group-toggle" data-act="dev-group" data-keep-menu data-group="${esc(g)}" aria-expanded="${open}">${n}/${list.length}<svg viewBox="0 0 24 24" class="ic chev"><path d="M6 9l6 6 6-6"/></svg></button></div>
          <div class="dev-group-list">${list.map(t => `<label class="tool-opt sub"><input type="checkbox" data-change="chat-device-tool" value="${esc(t.name)}"${off.has(t.name) ? '' : ' checked'}>
            <span><span class="dev-tool-line"><code>${esc(t.name)}</code>${t.risk && t.risk !== 'read' ? `<span class="badge ${t.risk === 'change' ? 'warn' : 'outline'}">${esc(t.risk)}</span>` : ''}</span><span class="cell-sub">${esc(t.description || '')}</span></span></label>`).join('')}</div></div>`;
      }).join('') : ''}</div>`;
  }).join('') + '<hr>';
}
const DEVICE_ICON = '<svg viewBox="0 0 24 24" class="ic dev-ic"><path d="M3 5h18v11H3zM8 20h8M12 16v4"/></svg>';
function knowledgeBtnHtml() {
  const n = (CHAT.settings.knowledge || []).length;
  return `${KB_ICON} Knowledge${n ? ` <b>${n}</b>` : ''}`;
}
function knowledgeMenuHtml() {
  const bases = CHAT.meta.knowledgeBases || [], sel = CHAT.settings.knowledge || [];
  const list = bases.length ? bases.map(b => `<label class="tool-opt${b.chunks ? '' : ' off'}"><input type="checkbox" data-change="chat-kb" value="${esc(b.id)}"${sel.includes(b.id) ? ' checked' : ''}${b.chunks ? '' : ' disabled'}>
      <span><b>${esc(b.name)}</b> <span class="muted small">${b.chunks ? plural(b.docs, 'file') + ' · ' + plural(b.chunks, 'chunk') : 'no indexed files yet'}</span></span></label>`).join('')
    : '<div class="muted small">No knowledge bases yet. Create one to let the model answer from your documents.</div>';
  return `<div class="menu-title">Answer from these knowledge bases</div>${list}<hr><button class="btn small" data-act="kb-manage">${icon('plus')} Manage knowledge bases</button>`;
}
function renderKnowledgeMenu() {
  const m = $('#kb-menu'); if (m) m.innerHTML = knowledgeMenuHtml();
  const b = $('#kb-btn'); if (b) { b.classList.toggle('on', (CHAT.settings.knowledge || []).length > 0); b.innerHTML = knowledgeBtnHtml(); }
}
function toolsBtnHtml() {
  const n = (CHAT.settings.mcpServers || []).length + (!window.chatClient && CHAT.settings.device ? 1 : 0);
  return `${icon('layers')} Tools${n ? ` <b>${n}</b>` : ''}`;
}
function renderToolsMenu() {
  const m = $('#tools-menu'), b = $('#tools-btn');
  if (b) { const n = (CHAT.settings.mcpServers || []).length + (!window.chatClient && CHAT.settings.device ? 1 : 0); b.classList.toggle('on', n > 0); b.innerHTML = toolsBtnHtml(); }
  if (!m) return;
  const scroll = m.scrollTop;
  m.innerHTML = toolsMenuHtml();
  m.scrollTop = scroll;
  $$('input[data-mixed="true"]', m).forEach(i => { i.indeterminate = true; });
}
/** Loads what the desktop apps offer (when the Tools menu opens in the web app). */
async function loadCatalogs() {
  if (window.chatClient) return;
  const ids = [...CHAT.devices.values()].filter(d => d.online || d.id === CHAT.settings.device).map(d => d.id);
  await Promise.all(ids.map(async id => { try { CHAT.catalogs.set(id, await get(`/api/chat/devices/${enc(id)}/catalog`)); } catch { CHAT.catalogs.delete(id); } }));
  if ($('#tools-menu') && !$('#tools-menu').classList.contains('hidden')) renderToolsMenu();
}
CHANGES['chat-device'] = el => {
  CHAT.settings.device = el.checked ? el.value : null;
  saveSettings();
  renderDeviceChip();
  renderToolsMenu();
  bounce($('#tools-btn'));
  if (el.checked && !CHAT.catalogs.has(el.value)) loadCatalogs();
};
CHANGES['chat-device-tool'] = el => {
  const off = new Set(CHAT.settings.deviceToolsOff || []);
  if (el.checked) off.delete(el.value); else off.add(el.value);
  CHAT.settings.deviceToolsOff = [...off];
  saveSettings();
  renderToolsMenu();
};
CHANGES['chat-device-group'] = el => {
  const cat = CHAT.catalogs.get(CHAT.settings.device);
  if (!cat) return;
  const off = new Set(CHAT.settings.deviceToolsOff || []);
  cat.tools.filter(t => t.group === el.dataset.group).forEach(t => { if (el.checked) off.delete(t.name); else off.add(t.name); });
  CHAT.settings.deviceToolsOff = [...off];
  saveSettings();
  renderToolsMenu();
};
ACTIONS['dev-group'] = el => {
  const g = el.dataset.group;
  if (CHAT.devOpen.has(g)) CHAT.devOpen.delete(g); else CHAT.devOpen.add(g);
  const box = el.closest('.dev-group');
  box.classList.toggle('open', CHAT.devOpen.has(g));
  el.setAttribute('aria-expanded', String(CHAT.devOpen.has(g)));
};
ACTIONS['chat-tools'] = () => {
  const m = $('#tools-menu'); m.classList.toggle('hidden');
  if (!m.classList.contains('hidden')) { renderToolsMenu(); loadCatalogs(); }
  if (!m.classList.contains('hidden')) setTimeout(() => document.addEventListener('click', function close(ev) { if (!ev.target.closest('.tools-menu-wrap')) { m.classList.add('hidden'); document.removeEventListener('click', close); } }), 0);
};
CHANGES['chat-server'] = el => {
  const set = new Set(CHAT.settings.mcpServers || []);
  if (el.checked) set.add(el.value); else set.delete(el.value);
  CHAT.settings.mcpServers = [...set];
  renderToolsMenu();
  bounce($('#tools-btn'));
  saveSettings();
};
CHANGES['chat-approval'] = el => { CHAT.settings.allowApprovalTools = el.checked; saveSettings(); };
ACTIONS['chat-knowledge'] = () => {
  const m = $('#kb-menu'); m.classList.toggle('hidden');
  if (!m.classList.contains('hidden')) {
    renderKnowledgeMenu();
    setTimeout(() => document.addEventListener('click', function close(ev) { if (!ev.target.closest('.tools-menu-wrap')) { m.classList.add('hidden'); document.removeEventListener('click', close); } }), 0);
  }
};
CHANGES['chat-kb'] = el => {
  const set = new Set(CHAT.settings.knowledge || []);
  if (el.checked) set.add(el.value); else set.delete(el.value);
  CHAT.settings.knowledge = [...set];
  renderKnowledgeMenu();
  bounce($('#kb-btn'));
  saveSettings();
};

// ------------------------------------------------------------------ knowledge bases (RAG) manager
const KB = { openId: null };
ACTIONS['kb-manage'] = () => kbManager();
async function kbManager() {
  openModal('Knowledge bases', '<div id="kb-modal" class="kb-modal"><div class="muted small"><span class="spinner"></span> Loading…</div></div>',
    '<button class="btn" data-act="modal-close">Close</button>', 'wide');
  KB.openId = null;
  await kbRenderList();
}
async function kbRenderList() {
  const host = $('#kb-modal'); if (!host) return;
  let d; try { d = await get('/api/chat/knowledge'); } catch (e) { host.innerHTML = `<div class="notice bad">${esc(e.message)}</div>`; return; }
  CHAT.meta.knowledgeBases = d.bases.map(b => ({ id: b.id, name: b.name, docs: b.docs, chunks: b.chunks }));
  renderKnowledgeMenu();
  const note = !d.available ? `<div class="notice warn small">Knowledge needs an embedding model on the provider${d.enabled ? '' : ', and RAG is turned off (Rag.Enabled)'}. ${d.embeddingModel ? '' : 'No embedding model was found — set <code>Rag.EmbeddingModel</code>.'}</div>` : '';
  const list = d.bases.length ? d.bases.map(b => `<div class="kb-row">
      <div class="grow"><b>${esc(b.name)}</b>${b.description ? ` <span class="muted small">${esc(b.description)}</span>` : ''}
        <div class="cell-sub">${plural(b.docs, 'file')} · ${plural(b.chunks, 'chunk')}${b.embeddingModel ? ' · ' + esc(b.embeddingModel) : ''}</div></div>
      <button class="btn xs" data-act="kb-open" data-id="${esc(b.id)}">Files</button>
      <button class="btn xs ghost danger" data-act="kb-del" data-id="${esc(b.id)}" data-name="${esc(b.name)}">Delete</button></div>`).join('')
    : '<div class="muted small">No knowledge bases yet.</div>';
  host.innerHTML = note + `<div class="kb-new"><input id="kb-new-name" placeholder="New knowledge base name" maxlength="120">
      <button class="btn primary" data-act="kb-create">${icon('plus')} Create</button></div><div class="kb-list">${list}</div>`;
}
ACTIONS['kb-create'] = async () => {
  const name = ($('#kb-new-name').value || '').trim();
  if (!name) { toast('Enter a name.', 'bad'); return; }
  try { await post('/api/chat/knowledge', { name }); toast('Knowledge base created', 'ok'); await kbRenderList(); }
  catch (e) { toast(e.message, 'bad'); }
};
ACTIONS['kb-del'] = async el => {
  if (!await confirmBox('Delete knowledge base', `Delete <b>${esc(el.dataset.name)}</b> and everything indexed in it?`, { danger: true, confirm: 'Delete' })) return;
  await del('/api/chat/knowledge/' + enc(el.dataset.id));
  CHAT.settings.knowledge = (CHAT.settings.knowledge || []).filter(x => x !== el.dataset.id);
  saveSettings();
  toast('Deleted', 'ok'); await kbRenderList();
};
ACTIONS['kb-open'] = async el => { KB.openId = el.dataset.id; await kbRenderBase(); };
ACTIONS['kb-back'] = async () => { KB.openId = null; await kbRenderList(); };
async function kbRenderBase() {
  const host = $('#kb-modal'); if (!host || !KB.openId) return;
  host.innerHTML = '<div class="muted small"><span class="spinner"></span> Loading…</div>';
  let d; try { d = await get('/api/chat/knowledge/' + enc(KB.openId)); } catch (e) { host.innerHTML = `<div class="notice bad">${esc(e.message)}</div>`; return; }
  host.innerHTML = kbBaseHtml(d);
}
function kbBaseHtml(d) {
  const docs = d.docs.length ? d.docs.map(doc => `<div class="kb-doc${doc.status === 'failed' ? ' failed' : ''}">
      <span class="attach-kind k-${esc((doc.name.split('.').pop() || '').toLowerCase())}">${esc((doc.name.split('.').pop() || '').slice(0, 4).toUpperCase())}</span>
      <span class="grow"><b>${esc(doc.name)}</b><span class="cell-sub">${doc.status === 'failed' ? '<span class="error-text">' + esc(doc.error || 'failed') + '</span>' : plural(doc.chunks, 'chunk') + ' · ' + num(doc.chars) + ' chars'}</span></span>
      <button class="btn xs ghost danger" data-act="kb-doc-del" data-id="${esc(doc.id)}">Remove</button></div>`).join('')
    : '<div class="muted small">No files indexed yet. Add files to make this base searchable.</div>';
  return `<div class="kb-base-head"><button class="btn xs ghost" data-act="kb-back">← Back</button><b>${esc(d.base.name)}</b>
      <span class="muted small">${plural(d.base.docs, 'file')} · ${plural(d.base.chunks, 'chunk')}${d.base.embeddingModel ? ' · ' + esc(d.base.embeddingModel) : ''}</span>
      <span class="spacer"></span><label class="btn small">${icon('plus')} Add files<input type="file" multiple data-change="kb-files" hidden></label></div>
    <div class="kb-docs" id="kb-docs">${docs}</div>`;
}
CHANGES['kb-files'] = async el => {
  const files = Array.from(el.files); el.value = '';
  if (!files.length || !KB.openId) return;
  const box = $('#kb-docs');
  const ids = [];
  for (const file of files) {
    if (box) box.insertAdjacentHTML('beforeend', `<div class="kb-doc pending"><span class="spinner"></span><span class="grow">${esc(file.name)} — uploading…</span></div>`);
    try { const u = await uploadFile(file); ids.push(u.id); } catch (e) { toast(`${file.name}: ${e.message}`, 'bad'); }
  }
  if (!ids.length) { await kbRenderBase(); return; }
  if (box) box.insertAdjacentHTML('beforeend', `<div class="kb-doc pending"><span class="spinner"></span><span class="grow">Indexing ${plural(ids.length, 'file')} — embedding the text…</span></div>`);
  try { const r = await post('/api/chat/knowledge/' + enc(KB.openId) + '/docs', { uploadIds: ids }); if (r.error) toast(r.error, 'bad'); }
  catch (e) { toast(e.message, 'bad'); }
  await kbRenderBase();
};
/** Toggles that cannot be on together: switching one on switches the other off. */
const EXCLUSIVE_TOGGLES = { imageGeneration: 'videoGeneration', videoGeneration: 'imageGeneration' };
const TOGGLE_NAMES = { imageGeneration: 'Image', videoGeneration: 'Video', diffusion: 'Stable Diffusion', music: 'Music', markets: 'Money', stocks: 'Stocks', polymarket: 'Polymarket', news: 'News' };
ACTIONS['chat-toggle'] = el => {
  const k = el.dataset.key; CHAT.settings[k] = !CHAT.settings[k]; el.classList.toggle('on', CHAT.settings[k]); bounce(el);
  const other = EXCLUSIVE_TOGGLES[k];
  if (other && CHAT.settings[k] && CHAT.settings[other]) {
    CHAT.settings[other] = false;
    $(`#composer-bar [data-key="${other}"]`)?.classList.remove('on');
    // One note at a time: switching back and forth replaces it.
    $$('#toasts .toast.media-switch').forEach(t => t._dismiss && t._dismiss());
    toast(`${TOGGLE_NAMES[k]} on, ${TOGGLE_NAMES[other]} off: a chat makes images or videos, one at a time.`);
    $('#toasts .toast:last-child')?.classList.add('media-switch');
  }
  saveSettings();
};
function bounce(el) { el.classList.remove('bounce'); void el.offsetWidth; el.classList.add('bounce'); }

const INSTRUCTION_PRESETS = [
  ['Concise', 'Answer briefly and directly. Use short paragraphs or bullet points, and skip preambles and summaries.'],
  ['Senior engineer', 'You are a senior software engineer. Show working code first, then explain the important decisions, trade-offs and edge cases.'],
  ['Teacher', 'Explain step by step for a beginner, with simple examples and analogies. Check understanding with a short question at the end.'],
  ['Formal writer', 'Write in a clear, formal, professional tone suitable for business documents. Avoid slang and emoji.'],
  ['Critical reviewer', 'Challenge assumptions, point out risks and weaknesses, and suggest concrete improvements. Be direct.'],
  ['Translator', 'Translate the user\'s text faithfully into the language they ask for, keeping tone and formatting. Explain idioms only when asked.']
];
ACTIONS['chat-system'] = () => {
  const current = CHAT.settings.systemPrompt || '';
  openModal('Chat instructions', `<div class="ins-modal">
      <p class="ins-lead">Instructions are added to the system prompt of every message in ${CHAT.conv ? 'this conversation' : 'the new conversation'}. Use them to set a role, a tone, a format or rules the model must follow.</p>
      <div class="ins-presets"><span class="ins-label">Start from a preset</span>
        <div class="ins-chips">${INSTRUCTION_PRESETS.map(([name], i) => `<button type="button" class="ins-chip" data-act="ins-preset" data-i="${i}">${esc(name)}</button>`).join('')}</div></div>
      <div class="ins-editor"><textarea id="ins-text" rows="9" maxlength="4000" placeholder="e.g. You are a senior .NET engineer. Answer briefly, show code first and explain after.">${esc(current)}</textarea>
        <div class="ins-foot"><span id="ins-count" class="muted small"></span><span class="spacer"></span>
          <label class="check small"><input type="checkbox" id="ins-default"${LS.get('chat.instructions') ? ' checked' : ''}> Also use for new chats</label></div></div>
    </div>`,
    `<button class="btn ghost danger" data-act="ins-clear">Clear</button><span class="spacer"></span><button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="ins-save">Save instructions</button>`, 'ins');
  const ta = $('#ins-text'), count = $('#ins-count');
  const upd = () => { count.textContent = `${num(ta.value.length)} / 4,000 characters`; count.classList.toggle('warn-text', ta.value.length > 3600); };
  ta.addEventListener('input', upd); upd();
  setTimeout(() => { ta.focus(); ta.setSelectionRange(ta.value.length, ta.value.length); }, 50);
};
ACTIONS['ins-preset'] = el => {
  const ta = $('#ins-text'), text = INSTRUCTION_PRESETS[Number(el.dataset.i)][1];
  ta.value = ta.value.trim() ? ta.value.trim() + '\n\n' + text : text;
  ta.dispatchEvent(new Event('input')); ta.focus();
  bounce(el);
};
ACTIONS['ins-clear'] = () => { const ta = $('#ins-text'); ta.value = ''; ta.dispatchEvent(new Event('input')); ta.focus(); };
ACTIONS['ins-save'] = () => {
  const v = $('#ins-text').value.trim();
  if ($('#ins-default').checked) LS.set('chat.instructions', v); else LS.set('chat.instructions', '');
  CHAT.settings.systemPrompt = v || null;
  closeModal();
  saveSettings();
  const b = $('#ins-btn');
  if (b) { b.classList.toggle('on', !!v); b.dataset.tip = v ? v.slice(0, 160) : 'Instructions for this chat'; bounce(b); }
  toast(v ? 'Instructions saved' : 'Instructions removed', 'ok');
};
/**
 * What a new chat takes over from the last one: preferences only (tool switches, reasoning effort, permission mode, MCP
 * servers). What belongs to one chat - its goal, instructions, temperature, knowledge bases, desktop app - starts fresh.
 */
const CARRIED_SETTINGS = ['webSearch', 'codeInterpreter', 'imageGeneration', 'videoGeneration', 'diffusion', 'music', 'markets', 'stocks', 'polymarket', 'news', 'reasoningEffort', 'permissionMode', 'mcpServers', 'caveman'];
function newChatDefaults(s) {
  if (!s || typeof s !== 'object') return null;
  const out = {};
  CARRIED_SETTINGS.forEach(k => { if (s[k] !== undefined) out[k] = s[k]; });
  return out;
}
function saveSettings() {
  LS.set('chat.settings', JSON.stringify(newChatDefaults(CHAT.settings)));
  if (!CHAT.conv) return;
  const id = CHAT.conv.id, settings = JSON.parse(JSON.stringify(CHAT.settings));
  // Saved one after another, so an older save can never land after a newer one.
  const run = (CHAT.savingSettings || Promise.resolve()).catch(() => { }).then(() => patch('/api/chat/conversations/' + enc(id), { settings }));
  CHAT.savingSettings = run;
  run.catch(e => { if (CHAT.savingSettings === run) toast('This chat\'s settings could not be saved: ' + e.message, 'bad'); })
    .finally(() => { if (CHAT.savingSettings === run) CHAT.savingSettings = null; });
}

// ------------------------------------------------------------------ attachments
CHANGES['chat-files'] = el => { attachFiles(el.files); el.value = ''; };
async function attachFiles(files) {
  if (CHAT.meta && CHAT.meta.canUpload === false) { toast('This account may not attach files (Users & access).', 'bad'); return; }
  const box = $('#composer-files');
  // The chat may change while files upload: they belong to the draft they were attached to.
  const target = CHAT.attachments;
  for (const file of Array.from(files)) {
    const chip = document.createElement('div');
    chip.className = 'file-chip pending';
    chip.innerHTML = `<span class="spinner"></span><span>${esc(file.name)}</span>`;
    box.appendChild(chip);
    try {
      const u = await uploadFile(file);
      target.push(u);
      fillAttachmentChip(chip, u);
      if (u.kind === 'image') { const img = $('img', chip); if (img) img.src = URL.createObjectURL(file); }
    } catch (e) { chip.className = 'file-chip failed'; chip.innerHTML = `<span class="error-text">${esc(file.name)}: ${esc(e.message)}</span><button class="btn xs ghost" data-act="chat-unattach">${icon('x')}</button>`; }
  }
}
/** The attach button and its menu (Upload Files / Attach Webpage / Attach Files / Attach Knowledge / Reference Chats), with the hidden file inputs. */
function attachControlHtml() {
  return `<div class="tools-menu-wrap attach-wrap">
      <button type="button" class="btn icon ghost attach-btn" data-act="attach-menu" aria-label="Attach" data-tip="Attach files, a webpage, knowledge or chats">${icon('plus')}</button>
      <div class="menu attach-menu hidden" id="attach-menu"></div>
      <input type="file" multiple id="chat-file" data-change="chat-files" hidden>
      <input type="file" multiple id="chat-file-docs" data-change="chat-files" accept=".pdf,.txt,.md,.markdown,.csv,.tsv,.json,.log,.docx,.doc,.rtf,.xml,.yml,.yaml,.html,.htm,.css,.js,.ts,.py,.vb,.cs,.java,.go,.rs,.rb,.php,.sql,.sh,.ps1,.c,.cpp,.h" hidden></div>`;
}
const WEB_ICON = '<svg viewBox="0 0 24 24" class="ic"><circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c3 3 3 15 0 18M12 3c-3 3-3 15 0 18"/></svg>';
const CHATS_ICON = '<svg viewBox="0 0 24 24" class="ic"><path d="M4 5h12v8H8l-4 3zM10 15h10v-6"/></svg>';
ACTIONS['attach-menu'] = () => {
  const m = $('#attach-menu'); if (!m) return;
  if (!m.classList.contains('hidden')) { m.classList.add('hidden'); return; }
  const kb = !!(CHAT.meta && CHAT.meta.knowledge);
  const item = (opt, ic, title, sub, off) => `<button type="button" class="attach-opt${off ? ' off' : ''}" data-act="attach-opt" data-opt="${opt}"${off ? ' disabled' : ''}>${ic}<span><b>${title}</b><span class="cell-sub">${esc(sub)}</span></span></button>`;
  // Only what the account may use (Users & access).
  const up = CHAT.meta.canUpload !== false, web = CHAT.meta.canWebpage !== false;
  m.innerHTML = (up ? item('upload', icon('download'), 'Upload Files', 'Images, PDFs and documents from this device') : '') +
    (web ? item('webpage', WEB_ICON, 'Attach Webpage', 'Fetch a page and add its text as context') : '') +
    (up ? item('files', icon('page'), 'Attach Files', 'Pick documents from this device') : '') +
    item('knowledge', icon('layers'), 'Attach Knowledge', kb ? 'Answer from your knowledge bases (RAG)' : 'No knowledge bases are configured', !kb) +
    item('chats', CHATS_ICON, 'Reference Chats', 'Add other conversations as context');
  m.classList.remove('hidden');
  setTimeout(() => document.addEventListener('click', function close(ev) {
    if (ev.target.closest('#attach-menu button') || !ev.target.closest('.attach-wrap')) { m.classList.add('hidden'); document.removeEventListener('click', close); }
  }), 0);
};
ACTIONS['attach-opt'] = el => {
  $('#attach-menu')?.classList.add('hidden');
  switch (el.dataset.opt) {
    case 'upload': $('#chat-file')?.click(); break;
    case 'files': $('#chat-file-docs')?.click(); break;
    case 'webpage': attachWebpage(); break;
    case 'knowledge': if (CHAT.meta && CHAT.meta.knowledge) ACTIONS['chat-knowledge'](); else toast('No knowledge bases are configured.', 'bad'); break;
    case 'chats': attachReferenceChats(); break;
  }
};
/** A finished attachment chip for an already-uploaded file (shared by every attach source). */
function fillAttachmentChip(chip, u) {
  chip.className = 'file-chip';
  chip.dataset.id = u.id;
  const thumb = u.kind === 'image' ? `<img alt="" data-local="1">` : `<span class="attach-kind k-${esc(u.kind)}">${esc((u.name.split('.').pop() || '').slice(0, 4).toUpperCase())}</span>`;
  chip.innerHTML = `${thumb}<span class="fc-text"><b>${esc(u.name)}</b><span class="cell-sub">${esc(u.summary || '')}</span></span><button class="btn xs ghost" data-act="chat-unattach" data-id="${esc(u.id)}" aria-label="Remove">${icon('x')}</button>`;
  if (u.kind === 'image') { const img = $('img', chip); if (img) img.src = fileUrl(u.id); }
}
function pendingChip(label) {
  const chip = document.createElement('div');
  chip.className = 'file-chip pending';
  chip.innerHTML = `<span class="spinner"></span><span>${esc(label)}</span>`;
  $('#composer-files').appendChild(chip);
  return chip;
}
/** Attach a webpage: the server fetches its text and saves it as an upload. */
async function attachWebpage() {
  const url = await confirmBox('Attach a webpage', 'Address of the page', { input: 'https://…', confirm: 'Attach' });
  if (typeof url !== 'string' || !url.trim()) return;
  const chip = pendingChip('Reading ' + url.trim().replace(/^https?:\/\//, '').slice(0, 40) + '…');
  try {
    const u = await post('/api/chat/fetch-page', { url: url.trim() });
    CHAT.attachments.push(u);
    fillAttachmentChip(chip, u);
  } catch (e) { chip.className = 'file-chip failed'; chip.innerHTML = `<span class="error-text">${esc(e.message)}</span><button class="btn xs ghost" data-act="chat-unattach">${icon('x')}</button>`; }
}
/** Reference other chats: the server saves each as an upload attached as context. */
async function attachReferenceChats() {
  const others = (CHAT.list || []).filter(c => !CHAT.conv || c.id !== CHAT.conv.id).slice(0, 100);
  if (!others.length) { toast('You have no other conversations to reference.', 'bad'); return; }
  openModal('Reference chats', `<form id="ref-chats-form" data-form="ref-chats"><p class="muted small">Pick conversations to attach as context. Each is added as a file the model reads.</p>
      <div class="chat-search" style="margin:6px 0 10px">${icon('search')}<input id="ref-q" placeholder="Search chats" autocomplete="off"></div>
      <div class="ref-list" id="ref-list">${others.map(c => `<label class="tool-opt ref-item" data-s="${esc((c.title || '').toLowerCase())}"><input type="checkbox" name="chat" value="${esc(c.id)}">
        <span><b>${esc(c.title || 'Untitled')}</b><span class="cell-sub">${esc(previewText(c.preview).slice(0, 80))}</span></span></label>`).join('')}</div></form><div data-errors></div>`,
    `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="submit-form" data-form-id="ref-chats-form">Attach</button>`, 'wide');
  const q = $('#ref-q'); if (q) q.addEventListener('input', () => { const v = q.value.trim().toLowerCase(); $$('#ref-list .ref-item').forEach(x => x.classList.toggle('hidden', v && !x.dataset.s.includes(v))); });
};
FORMS['ref-chats'] = async (f, data) => {
  const ids = Array.from(new FormData(f).getAll('chat'));
  if (!ids.length) throw new Error('Choose at least one conversation.');
  closeModal();
  const chips = ids.map(() => pendingChip('Attaching a chat…'));
  try {
    const r = await post('/api/chat/reference-chats', { chatIds: ids });
    (r.uploads || []).forEach((u, i) => { CHAT.attachments.push(u); if (chips[i]) fillAttachmentChip(chips[i], u); });
    chips.slice((r.uploads || []).length).forEach(c => c.remove());
  } catch (e) { chips.forEach(c => c.remove()); toast(e.message, 'bad'); }
};
ACTIONS['chat-unattach'] = el => {
  CHAT.attachments = CHAT.attachments.filter(a => a.id !== el.dataset.id);
  const chip = el.closest('.file-chip'); chip.classList.add('leaving'); setTimeout(() => chip.remove(), reducedMotion() ? 0 : 180);
};

// ------------------------------------------------------------------ messages
/** Who posted an agent report: an agent's name, or the agents together for a global task. */
function reportAuthor(m) {
  const a = String(m.model || '').replace(/^agent:/, '');
  return a === 'global' || a === 'host' || !a ? 'Your agents' : a;
}
/** A message an agent posted into this chat: a delegated task's result, a global task's answer, or work opened to discuss. */
function agentReportHtml(m, live) {
  return `<div class="msg assistant agent-report${live ? ' new' : ''}" data-id="${esc(m.id || '')}">
    <div class="avatar">${icon('agent')}</div>
    <div class="msg-body">
      <div class="msg-head"><b>${esc(reportAuthor(m))}</b> <span class="report-chip">${icon('layers')} agent report</span>
        <span class="muted small" data-tip="${esc(ts(m.createdUtc))}">${esc(ago(m.createdUtc))}</span></div>
      <div class="flow"><div class="md answer">${chatMarkdown(m.content || '')}</div></div>
      <div class="msg-actions">
        <button class="btn xs ghost icon-only" data-act="msg-copy" aria-label="Copy" data-tip="Copy">${COPY_ICON}</button>
        <button class="btn xs ghost icon-only" data-act="msg-speak" aria-label="Read aloud" data-tip="Read aloud">${SPEAK_ICON}</button></div>
    </div></div>`;
}
/** The goal's automatic continuation: written by the server, not typed in a window, so not a message bubble. */
function autoGoalHtml(m, live) {
  const n = /automatic continuation (\d+) of (\d+)/i.exec(m.content || '');
  return `<div class="msg auto-note${live ? ' new' : ''}" data-id="${esc(m.id || '')}"><span class="auto-chip" data-tip="${esc(m.content || '')}">↻ Goal not met yet — continuing by itself${n ? ` (${n[1]} of ${n[2]})` : ''}</span></div>`;
}
/** Monitor events (the monitor tool): output lines of a watched command, written by the server, folded away. */
function autoMonitorHtml(m, live) {
  const lines = String(m.content || '').split('\n').filter(l => l.startsWith('[monitor '));
  return `<div class="msg auto-note${live ? ' new' : ''}" data-id="${esc(m.id || '')}"><details class="auto-events"><summary class="auto-chip">👁 Monitor events · ${lines.length} line${lines.length === 1 ? '' : 's'}</summary>
    <pre class="auto-lines">${esc(lines.join('\n'))}</pre></details></div>`;
}
// ------------------------------------------------------------------ parts drawn when opened
/**
 * A closed tool card's details (a diff, a command's output, a result, a subagent's transcript and report) are drawn only when
 * it is opened: in a long chat they were most of the page (tens of thousands of elements) and made writing and scrolling
 * lag. Answers older than the last few draw their cards closed (the newest keep their diffs open, like Claude Code).
 */
const LAZY_PARTS = new Map();
const CARDS_OPEN_RECENT = 4;
let lazySeq = 0, CARDS_FOLDED = false;
function lazyHtml(make) {
  // Parts of cards long gone from the page are forgotten first (a Map keeps the order they were added in).
  if (LAZY_PARTS.size > 40000) { let n = 10000; for (const k of LAZY_PARTS.keys()) { LAZY_PARTS.delete(k); if (--n <= 0) break; } }
  const k = String(++lazySeq);
  LAZY_PARTS.set(k, make);
  return `<div class="lazy-part" data-lazy="${k}"></div>`;
}
/** Draws root's parts that wait (its own; deep: also those of cards inside it, for "open full screen"). */
function fillLazy(root, deep) {
  if (!root || !root.querySelectorAll) return;
  $$(deep ? '[data-lazy]' : ':scope > [data-lazy]', root).forEach(el => {
    const make = LAZY_PARTS.get(el.dataset.lazy);
    LAZY_PARTS.delete(el.dataset.lazy);
    const node = make ? htmlNode(make()) : null;
    if (!node) { el.remove(); return; }
    el.replaceWith(node);
    applyRich(node);
    if (deep) fillLazy(node, true);
  });
}
document.addEventListener('toggle', ev => { const d = ev.target; if (d && d.tagName === 'DETAILS' && d.open) fillLazy(d); }, true);
/** Whether a drawn message's cards start closed: all but the last few messages of the chat. */
function cardsFoldedFor(m) {
  const msgs = CHAT.conv && CHAT.conv.messages;
  if (!msgs || !m) return false;
  const i = msgs.indexOf(m);
  return i >= 0 && i < msgs.length - CARDS_OPEN_RECENT;
}
function messageHtml(m, live) {
  const folded = CARDS_FOLDED;
  CARDS_FOLDED = !live && cardsFoldedFor(m);
  try { return messageHtmlCore(m, live); } finally { CARDS_FOLDED = folded; }
}
function messageHtmlCore(m, live) {
  if (m.role === 'assistant' && m.finishReason === 'agent_report') return agentReportHtml(m, live);
  if (m.role === 'user' && m.finishReason === 'auto_goal') return autoGoalHtml(m, live);
  if (m.role === 'user' && m.finishReason === 'auto_monitor') return autoMonitorHtml(m, live);
  if (m.role === 'user' && typeof heartbeatMsgHtml === 'function' && /^⏰ Heartbeat check\./.test(m.content || '')) return heartbeatMsgHtml(m, live);
  if (m.role === 'user' && m.finishReason === 'shell' && typeof shellMsgHtml === 'function') return shellMsgHtml(m, live);
  const when = typeof msgTimeHtml === 'function' ? msgTimeHtml(m) : '', pin = typeof pinBtnHtml === 'function' ? pinBtnHtml(m) : '';
  if (m.role === 'user') {
    const images = (m.attachments || []).filter(a => a.kind === 'image' && a.uploadId);
    const files = (m.attachments || []).filter(a => !(a.kind === 'image' && a.uploadId)).map(a => `<span class="msg-file">${a.kind === 'image' ? '🖼' : '📄'} ${esc(a.name)}</span>`).join('');
    // A long message (a pasted log or file) shows its start; "Show more" opens it, so the conversation stays readable.
    const text = String(m.content || ''), long = text.length > 1500 || text.split('\n').length > 16;
    return `<div class="msg user${live ? ' new' : ''}" data-id="${esc(m.id || '')}">${images.length ? `<div class="msg-imgs">${images.map(imageThumbHtml).join('')}</div>` : ''}
      <div class="bubble-u${long ? ' long' : ''}">${files ? `<div class="msg-files">${files}</div>` : ''}<div class="md">${chatMarkdown(text)}</div>${long ? `<button type="button" class="bubble-more" data-act="bubble-more" aria-expanded="false">Show more <span class="muted">· ${num(text.split('\n').length)} lines</span></button>` : ''}</div>
      <div class="msg-actions user-actions">${when}<span class="ver-slot">${versionNavHtml(m)}</span>${pin}
        <button class="btn xs ghost icon-only" data-act="msg-rewind" aria-label="Rewind to here" data-tip="Rewind to here: take back this message and everything after it (and the code changes, in the desktop app and VS Code)">${REWIND_ICON}</button>
        <button class="btn xs ghost icon-only" data-act="msg-edit" aria-label="Edit" data-tip="Edit and send again (the current answer is kept as a version)">${PENCIL_ICON}</button>
        <button class="btn xs ghost icon-only" data-act="msg-copy" aria-label="Copy" data-tip="Copy">${COPY_ICON}</button></div></div>`;
  }
  const route = m.routedModel ? `<span class="route-badge" data-tip="${esc([m.routedModel, m.routeInfo].filter(Boolean).join(' · '))}">→ ${esc(modelName(m.routedModel))}</span>` : '';
  const meta = [m.durationMs ? dur(m.durationMs) : '', m.completionTokens ? num(m.completionTokens) + ' tokens' : '',
    m.costMicros ? '$' + (m.costMicros / 1e6).toFixed(m.costMicros < 100 ? 6 : 4) : ''].filter(Boolean).join(' · ');
  const calls = m.toolCalls || [];
  const asked = calls.filter(t => t.name === 'ask_user');
  const images = (m.attachments || []).filter(a => a.kind === 'image' && a.uploadId);
  const videos = (m.attachments || []).filter(a => a.kind === 'video' && a.uploadId);
  const sharedFiles = (m.attachments || []).filter(a => a.uploadId && a.kind !== 'image' && a.kind !== 'video' && a.kind !== 'song');
  const songs = (m.attachments || []).filter(a => a.kind === 'song' && a.uploadId);
  const shown = cardCalls(calls);
  return `<div class="msg assistant${live ? ' new' : ''}" data-id="${esc(m.id || '')}">
    <div class="avatar"><img src="/assets/img/favicon.ico" alt=""></div>
    <div class="msg-body">
      <div class="msg-head"><b>${esc(modelName(m.model))}</b> ${route}</div>
      ${m.reasoning && reasoningPartsOf(m) === null ? thinkingHtml(m.reasoning) : ''}
      ${calls.filter(t => MEMORY_TOOLS.has(t.name)).map(memoryChipHtml).join('')}
      ${images.length || videos.length ? `<div class="gen-images">${images.map(generatedImageHtml).join('')}${videos.map(generatedVideoHtml).join('')}</div>` : ''}
      ${songs.length && typeof songCardHtml === 'function' ? `<div class="gen-songs">${songs.map(songCardHtml).join('')}</div>` : ''}
      <div class="files-slot">${sharedFiles.map(generatedFileHtml).join('')}</div>
      <div class="flow">${flowHtml(m, shown)}</div>
      ${asked.map(t => askedCardHtml(t)).join('')}
      ${m.error ? `<div class="notice bad small msg-error"><span class="grow">${esc(m.error)}</span>${live ? '' : `<span class="err-actions">${failedWithWork(m)
        ? `<button class="btn xs" data-act="msg-retry" data-tip="Picks up where it stopped: what it wrote and the tools it ran are kept">${REGEN_ICON} Try again</button><button class="btn xs ghost" data-act="msg-regen" data-tip="Answer the question again from the start (this answer is kept as a version)">Start over</button>`
        : `<button class="btn xs" data-act="msg-regen">${REGEN_ICON} Try again</button>`}<button class="btn xs ghost" data-act="model-menu">Switch model</button></span>`}</div>` : ''}
      ${sourcesHtml(m.sources)}
      ${live ? '' : changesSummaryHtml(m)}
      ${!live && typeof spokenHtml === 'function' ? spokenHtml(m) : ''}
      <div class="msg-actions"><span class="ver-slot">${versionNavHtml(m)}</span>
        <button class="btn xs ghost icon-only" data-act="msg-copy" aria-label="Copy" data-tip="Copy">${COPY_ICON}</button>
        <button class="btn xs ghost icon-only" data-act="msg-regen" aria-label="Regenerate" data-tip="Regenerate (the current answer is kept as a version)">${REGEN_ICON}</button>${!live && typeof regenWithHtml === 'function' ? regenWithHtml(m) : ''}
        ${pin}<button class="btn xs ghost icon-only" data-act="msg-speak" aria-label="Read aloud" data-tip="Read aloud">${SPEAK_ICON}</button>
        <button class="btn xs ghost icon-only rate${m.feedback === 1 ? ' on' : ''}" data-act="msg-rate" data-v="1" aria-label="Good answer" aria-pressed="${m.feedback === 1}" data-tip="Good answer">${THUMB_UP}</button>
        <button class="btn xs ghost icon-only rate down${m.feedback === -1 ? ' on' : ''}" data-act="msg-rate" data-v="-1" aria-label="Bad answer" aria-pressed="${m.feedback === -1}" data-tip="Bad answer">${THUMB_DOWN}</button>
        ${canContinue(m) ? `<button class="btn xs continue-btn" data-act="msg-continue" data-tip="${m.finishReason === 'length' ? 'The answer was cut off: continue it' : 'Continue writing this answer'}">${icon('play')} Continue</button>` : ''}
        ${meta ? `<span class="muted small">${esc(meta)}</span>` : ''}${live ? '' : when}</div>
    </div></div>`;
}
/**
 * An answer's text with its tool calls where they happened (like Claude Code): text, the tools it then used, the text
 * after them, and so on. Calls without a position (answers saved before positions were kept) come first, as before.
 * The last part is the ".answer" (the one still being written while streaming).
 */
function flowHtml(m, calls) {
  const content = m.content || '';
  // Only the latest task list is shown (each update replaces the one before).
  const lastTodos = [...calls].reverse().find(t => t.name === 'update_todos');
  calls = calls.filter(t => t.name !== 'update_todos' || t === lastTodos);
  // Everything in the order it happened: each thinking phase goes before the tool call that followed it.
  const shown = new Set(calls), all = m.toolCalls || [], parts = reasoningPartsOf(m) || [];
  const items = [];
  let pi = 0;
  for (let i = 0; i <= all.length; i++) {
    while (pi < parts.length && parts[pi].tools <= i) items.push({ think: parts[pi++] });
    if (i < all.length && shown.has(all[i])) items.push({ call: all[i] });
  }
  while (pi < parts.length) items.push({ think: parts[pi++] });
  calls.filter(c => !all.includes(c)).forEach(c => items.push({ call: c }));
  // Calls without a position (answers saved before positions were kept) come first, as before.
  let html = items.filter(x => x.call && !Number.isInteger(x.call.at)).map(x => toolCardHtml(x.call)).join('');
  let pos = 0, reads = [];
  // Reads in a row (three or more, nothing written between them) fold into one line: "Read 3 files, searched for 2 patterns".
  const flush = () => { html += reads.length >= TOOL_GROUP_MIN ? toolGroupHtml(reads) : reads.map(toolCardHtml).join(''); reads = []; };
  for (const x of items) {
    if (x.call && !Number.isInteger(x.call.at)) continue;
    const at = Math.min(Math.max(x.call ? x.call.at : x.think.at, pos), content.length);
    const part = content.slice(pos, at);
    if (part.trim()) { flush(); html += `<div class="md answer-part">${chatMarkdown(part, m.sources)}</div>`; }
    if (x.call && toolGroupable(x.call)) reads.push(x.call);
    else { flush(); html += x.call ? toolCardHtml(x.call) : thinkingHtml(x.think.text); }
    pos = at;
  }
  flush();
  return html + `<div class="md answer">${chatMarkdown(content.slice(pos), m.sources)}</div>`;
}
/**
 * An answer's thinking phases ({text, at, tools}: where in the text and after how many tool calls each one started), or
 * null for answers saved before phases were kept (their thinking is one block on top).
 */
function reasoningPartsOf(m) {
  const r = m.reasoning || '';
  const parts = Array.isArray(m.reasoningParts) && m.reasoningParts.length ? m.reasoningParts : null;
  if (!r.trim()) return [];
  if (!parts) return null;
  return parts.map((p, i) => ({ at: p.at || 0, tools: p.tools || 0, text: r.slice(p.start || 0, i + 1 < parts.length ? parts[i + 1].start : r.length) })).filter(p => p.text.trim());
}
/** A thinking block (closed; the user opens it to read what the model thought before it acted). */
function thinkingHtml(text, label) {
  const words = (String(text).trim().match(/\S+/g) || []).length;
  return `<details class="thinking"><summary>${esc(label || `Thought · ${num(words)} word${words === 1 ? '' : 's'}`)}</summary><div class="thinking-text">${esc(text)}</div></details>`;
}
/** Tool calls that get a card (memory changes show as chips, made pictures as themselves, questions as their card). */
function cardCalls(calls) {
  return (calls || []).filter(t => t.name !== 'ask_user' && !MEMORY_TOOLS.has(t.name) && !((t.name === 'generate_image' || t.name === 'generate_video') && t.ok));
}
/** Whether a continuation starts a new line (like ChatFeatures.JoinContinuation on the server, from its first characters). */
/** Sets the thinking text and, if the user was already at the bottom, keeps it scrolled down as it streams. */
function setThinkingText(tt, text) {
  if (!tt) return;
  const stick = tt.scrollHeight - tt.scrollTop - tt.clientHeight < 40;
  tt.textContent = text;
  if (stick) tt.scrollTop = tt.scrollHeight;
}
function continuesOnNewLine(before, after) {
  if (!before || !after || /\s$/.test(before) || /^\s/.test(after)) return false;
  const last = before.slice(before.lastIndexOf('\n') + 1);
  if (/^\s*(\d+[.)]?\s|[-*+]\s|\||#{1,6}\s|>)/.test(last) && /^(\d|[-*+|#>])/.test(after)) return true;
  return (before.match(/^\s*```/gm) || []).length % 2 === 1 && /[;{}()[\],:]\s*$/.test(last);
}
/** Answers that stopped early (too long, out of tool rounds, stopped or failed mid-way) can be continued - the last one only. */
function canContinue(m) {
  if (!m || m.role !== 'assistant' || !['length', 'max_rounds', 'stopped', 'error'].includes(m.finishReason)) return false;
  if (!(m.content || '').trim() && m.finishReason !== 'max_rounds') return false;
  const msgs = CHAT.conv ? CHAT.conv.messages : [];
  return msgs.length > 0 && msgs[msgs.length - 1].id === m.id;
}
/** ‹ 2 / 3 › on a question that was edited or an answer that was regenerated: switches between the versions. */
function versionNavHtml(m) {
  const conv = CHAT.conv;
  if (!conv || !conv.versions || !conv.versions.length || !m.id) return '';
  const idx = conv.messages.findIndex(x => x.id === m.id);
  const p = conv.versions.find(v => v.anchorIndex === idx);
  if (!p) return '';
  return `<span class="ver-nav" data-anchor="${idx}"><button class="btn xs ghost icon-only" data-act="msg-version" data-dir="-1"${p.position <= 1 ? ' disabled' : ''} aria-label="Previous version">‹</button><span class="ver-pos">${p.position} / ${p.total}</span><button class="btn xs ghost icon-only" data-act="msg-version" data-dir="1"${p.position >= p.total ? ' disabled' : ''} aria-label="Next version">›</button>${typeof compareBtnHtml === 'function' ? compareBtnHtml(idx) : ''}</span>`;
}
function renderVersionNavs() {
  const thread = $('#chat-thread');
  if (!thread || !CHAT.conv) return;
  $$('.msg[data-id]', thread).forEach(node => {
    const m = CHAT.conv.messages.find(x => x.id === node.dataset.id), slot = $('.ver-slot', node);
    if (m && slot) slot.innerHTML = versionNavHtml(m);
  });
}
const MEMORY_TOOLS = new Set(['remember', 'forget']);
function memoryChipHtml(t) {
  let fact = ''; try { const a = JSON.parse(t.arguments || '{}'); fact = a.fact || a.content || a.contains || ''; } catch { /* ignore */ }
  const forgot = t.name === 'forget';
  return `<div class="mem-chip${t.ok === false ? ' failed' : ''}" data-tip="${esc(t.result || '')}">${MEMORY_ICON}<span><b>${t.ok === false ? 'Memory not changed' : forgot ? 'Memory: forgot' : 'Memory updated'}</b>${fact ? ' · ' + esc(fact.slice(0, 140)) : ''}</span>
    <button class="btn xs ghost" data-act="chat-personalize" data-tab="memory">Manage</button></div>`;
}
function fileUrl(uploadId) { return '/api/chat/files/' + enc(uploadId); }
function imageThumbHtml(a) { return `<a class="msg-img" href="${fileUrl(a.uploadId)}" target="_blank" rel="noopener" data-tip="${esc(a.name)}"><img src="${fileUrl(a.uploadId)}" alt="${esc(a.name)}" loading="lazy"></a>`; }
/** A generated video: the player (inline on phones), what it shows and a download link. */
function generatedVideoHtml(a) {
  return `<figure class="gen-video"><video controls playsinline preload="metadata" src="${fileUrl(a.uploadId)}"></video>
    <figcaption><span class="muted small">${esc((a.summary || '').slice(0, 200))}</span><a class="btn xs ghost" href="${fileUrl(a.uploadId)}" download="${esc(a.name)}">${icon('download')} Download</a></figcaption></figure>`;
}
function generatedImageHtml(a) {
  return `<figure class="gen-image"><a href="${fileUrl(a.uploadId)}" target="_blank" rel="noopener"><img src="${fileUrl(a.uploadId)}" alt="${esc(a.summary || a.name)}" loading="lazy"></a>
    <figcaption><span class="muted small">${esc((a.summary || '').slice(0, 160))}</span><a class="btn xs ghost" href="${fileUrl(a.uploadId)}" download="${esc(a.name)}">${icon('download')} Download</a></figcaption></figure>`;
}
/** A file the assistant shared out of the sandbox: an icon, the name and size, and a Download button. */
function generatedFileHtml(a) {
  return `<div class="shared-file"><span class="sf-icon">${fileKindIcon(a.kind, a.name)}</span>
    <span class="sf-meta"><b class="sf-name">${esc(a.name)}</b>${a.sizeBytes ? `<span class="muted small">${fmtBytes(a.sizeBytes)}</span>` : ''}</span>
    <a class="btn xs" href="${fileUrl(a.uploadId)}" download="${esc(a.name)}">${icon('download')} Download</a></div>`;
}
function fmtBytes(n) {
  n = +n || 0;
  if (n < 1024) return n + ' B';
  if (n < 1048576) return (n / 1024).toFixed(1) + ' KB';
  if (n < 1073741824) return (n / 1048576).toFixed(1) + ' MB';
  return (n / 1073741824).toFixed(2) + ' GB';
}
function fileKindIcon(kind, name) {
  const ext = (name || '').split('.').pop().toLowerCase();
  if (kind === 'archive' || ['zip', 'tar', 'gz', 'tgz', '7z', 'rar'].includes(ext)) return '🗜';
  if (kind === 'pdf' || ext === 'pdf') return '📕';
  if (kind === 'spreadsheet' || ['csv', 'xlsx', 'xls'].includes(ext)) return '📊';
  if (kind === 'code') return '📜';
  if (kind === 'document') return '📄';
  return '📎';
}

// ------------------------------------------------------------------ sandbox file browser
ACTIONS['chat-files'] = () => {
  const apps = CHAT.meta && CHAT.meta.sandboxPreviews;
  openModal('Sandbox files', `${apps ? '<div id="sbx-apps" class="sbx-apps"></div>' : ''}<div id="sbx-browser" class="sbx-browser"><div class="muted small pad">Loading…</div></div>`,
    `<span class="muted small">Files live in the code sandbox. Download them here, or ask the assistant to share one.${apps ? ' Web apps running there in a container open with Open app.' : ''}</span>`, 'wide');
  loadSbx('/home/user');
  if (apps) loadSbxApps();
};
ACTIONS['sbx-nav'] = el => loadSbx(el.dataset.path);
/** Web apps running in sandbox containers (published ports): each opens through this site at /container/…. */
async function loadSbxApps() {
  const host = document.getElementById('sbx-apps');
  if (!host) return;
  let r;
  try { r = await get('/api/sandbox/apps'); } catch { host.remove(); return; }
  const apps = (r && r.apps) || [];
  const warn = r && r.warning ? `<div class="small sbx-apps-warn">${icon('alert')} ${esc(r.warning)}</div>` : '';
  if (!apps.length) { host.innerHTML = warn + `<div class="muted small sbx-apps-empty">No web app is running in a sandbox container. Ask the assistant to run one (in Docker, with a published port) and it can give you a link.</div>`; return; }
  host.innerHTML = warn + `<div class="sbx-apps-title">Running apps</div>` + apps.map(a => `<div class="sbx-row app-row">
      <span class="sbx-name">${icon('globe')} ${esc(a.name)} <span class="muted small">${esc((a.image || '').slice(0, 40))}</span></span>
      <span class="sbx-size muted small">${a.ports.map(p => p.host + '→' + p.container).join(', ')}</span>
      <button class="btn xs primary" data-act="sbx-open-app" data-container="${esc(a.name)}">${icon('external')} Open app</button></div>`).join('');
}
ACTIONS['sbx-open-app'] = async el => {
  // Open the window in the click (so it is not blocked as a popup), then point it at the link.
  const win = window.open('about:blank', '_blank');
  el.disabled = true;
  try {
    const p = await post('/api/sandbox/apps/preview', { container: el.dataset.container, chatId: CHAT.conv ? CHAT.conv.id : undefined });
    if (p.error) throw new Error(p.error.message || p.error);
    const url = p.url || previewUrl(p.path);
    if (win) win.location = url; else window.open(url, '_blank', 'noopener');
  } catch (e) { if (win) win.close(); toast(e.message || 'Could not open the app', 'bad'); }
  el.disabled = false;
};
async function loadSbx(path) {
  const host = document.getElementById('sbx-browser');
  if (!host) return;
  host.dataset.path = path;
  let r;
  try { r = await get('/api/chat/sandbox/files?path=' + enc(path)); }
  catch (e) { host.innerHTML = `<div class="notice bad small">${esc((e && e.message) || 'Could not list the folder.')}</div>`; return; }
  if (!r || r.error) { host.innerHTML = `<div class="notice bad small">${esc((r && r.error) || 'Could not list the folder.')}</div>`; return; }
  host.innerHTML = sbxBrowserHtml(r.dir || path, r.entries || []);
}
function sbxCrumbs(dir) {
  let acc = '';
  const crumbs = [`<a data-act="sbx-nav" data-path="/">/</a>`];
  (dir || '').split('/').filter(Boolean).forEach(p => { acc += '/' + p; crumbs.push(`<a data-act="sbx-nav" data-path="${esc(acc)}">${esc(p)}</a>`); });
  return crumbs.join('<span class="sep">/</span>');
}
function sbxBrowserHtml(dir, entries) {
  const base = dir.replace(/\/+$/, '');
  const folderZip = '/api/chat/sandbox/download?zip=1&path=' + enc(dir);
  const rows = entries.map(e => {
    const full = base + '/' + e.name;
    if (e.type === 'dir') return `<div class="sbx-row"><button class="sbx-name dir" data-act="sbx-nav" data-path="${esc(full)}">📁 ${esc(e.name)}</button><span class="sbx-size muted small"></span>
      <a class="btn xs ghost" href="/api/chat/sandbox/download?zip=1&path=${enc(full)}" download="${esc(e.name)}.zip">${icon('download')} .zip</a></div>`;
    return `<div class="sbx-row"><span class="sbx-name">${fileKindIcon(e.kind, e.name)} ${esc(e.name)}</span><span class="sbx-size muted small">${e.size ? fmtBytes(e.size) : ''}</span>
      <a class="btn xs" href="/api/chat/sandbox/download?path=${enc(full)}" download="${esc(e.name)}">${icon('download')} Download</a></div>`;
  }).join('');
  return `<div class="sbx-bar"><div class="sbx-crumbs">${sbxCrumbs(dir)}</div>
      <a class="btn xs ghost" href="${folderZip}" download="${esc(base.split('/').pop() || 'sandbox')}.zip" data-tip="Download this whole folder as a .zip">${icon('download')} Folder .zip</a></div>
    <div class="sbx-list">${rows || '<div class="muted small pad">This folder is empty.</div>'}</div>`;
}
function questionsOf(call) { try { return (JSON.parse(call.arguments || '{}').questions || []).filter(q => q && q.question); } catch { return []; } }
function askedCardHtml(call) {
  const qs = questionsOf(call);
  if (!qs.length) return '';
  return `<div class="asked-card"><div class="asked-title">${icon('info')} ${qs.length === 1 ? 'Asked you a question' : `Asked you ${qs.length} questions`}</div>
    ${qs.map(q => `<div class="asked-q"><b>${esc(q.question)}</b>${(q.options || []).length ? `<div class="asked-opts">${q.options.map(o => `<span>${esc(o)}</span>`).join('')}</div>` : ''}</div>`).join('')}</div>`;
}
/** A subagent's call (task here, the CLI's Task): its short name and type, shown on its card. */
const SUBAGENT_TOOLS = new Set(['task', 'Task', 'Agent', 'agent']);
function subagentLabel(t) {
  if (!SUBAGENT_TOOLS.has(t.name)) return '';
  let a = {}; try { a = JSON.parse(t.arguments || '{}') || {}; } catch { /* partial */ }
  const kind = a.subagent_type && a.subagent_type !== 'general' && a.subagent_type !== 'general-purpose' ? `<span class="sub-kind">${esc(a.subagent_type)}</span>` : '';
  return a.description ? `<b class="sub-desc">${esc(String(a.description).slice(0, 70))}</b>${kind}` : kind;
}
// ------------------------------------------------------------------ subagent streams (in their cards)
// "subagent" events {id, kind: start|text|reasoning|tool|note|end} show, in the card of that call, what the subagent writes,
// thinks and does as it happens; the saved call keeps the same transcript (call.subagent), and a window that opens the chat
// mid-answer gets it from the live snapshot.
/**
 * One step of a subagent's stream. Its thinking is a box that follows the newest thought while it thinks, then folds to
 * "Thought · 40 words" (a click opens it); each tool call is a row like the answer's own ("Read src/app.js") with the first
 * line of what it got, and a click shows more of it; what it writes is Markdown. live: the newest thinking stays open.
 */
function thoughtPreview(s) { return String(s || '').trim().replace(/\s+/g, ' ').slice(0, 220); }
function subItemHtml(it, live) {
  switch (it.kind) {
    case 'text': return `<div class="ss-text md">${chatMarkdown(it.text || '', [])}</div>`;
    case 'reasoning':
      // Folded, a thought still says what it was about: its opening words ("Thought  I need the list of controllers first…").
      return `<details class="ss-think${live ? ' thinking' : ''}"${live ? ' open' : ''}><summary><span class="ss-think-ic"></span><span class="ss-think-label">${live ? 'Thinking…' : 'Thought'}</span>` +
        `<span class="ss-think-preview">${esc(thoughtPreview(it.text))}</span></summary><div class="ss-think-text">${esc(it.text || '')}</div></details>`;
    case 'tool': {
      const state = it.ok == null ? 'run' : it.ok ? 'ok' : 'bad', result = String(it.result || '').trim();
      const statusIc = state === 'run' ? '<span class="spinner"></span>' : icon(state === 'ok' ? 'check' : 'alert');
      // What came of it: "17 lines", "3 results", "exit 0" (worked out from its whole result), else its first line.
      const meta = it.meta ? `<span class="ss-meta m">${esc(it.meta)}</span>` : result ? `<span class="ss-meta">${esc(toolFirstLine(result).slice(0, 160))}</span>` : '';
      return `<details class="ss-step ${state}" data-sid="${esc(it.id || '')}"${result ? '' : ' data-empty'}><summary>${statusIc}` +
        `<span class="ss-verb" data-tip="${esc(it.name || 'tool')}">${esc(toolVerb(it.name || 'tool'))}</span><span class="ss-target">${esc(it.target || '')}</span>` +
        `${meta}</summary>${result ? `<pre class="ss-result">${jsonOr(result)}</pre>` : ''}</details>`;
    }
    case 'note': return `<div class="ss-note">${esc(it.text || '')}</div>`;
  }
  return '';
}
function subStreamHtml(tr, live) {
  const items = (tr && tr.items) || [];
  // Only a live stream's last step can still be thinking.
  const body = items.map((it, i) => subItemHtml(it, live && i === items.length - 1)).join('') || (live ? '<div class="ss-wait">Starting…</div>' : '');
  return `<div class="sub-stream${live ? ' live' : ''}" data-sub-stream>${body}${tr && tr.clipped ? '<div class="ss-note">Only the beginning of this long run is kept.</div>' : ''}</div>`;
}
/**
 * The live state of a subagent card's stream (its items and their nodes). stick: the stream follows its newest step - on until
 * you scroll up in it, on again when you scroll back to the end (or click "Latest").
 */
function subState(card) {
  fillLazy(card);
  let box = $(':scope > [data-sub-stream]', card);
  if (!box) { box = htmlNode(subStreamHtml(null, true)); $(':scope > summary', card).after(box); }
  if (!card._sub) {
    const st = card._sub = { box, items: [], dirty: new Set(), raf: 0, stick: true, auto: 0 };
    // Following stops only when you scroll up: a scroll event from the box's own auto-scroll (st.auto) arrives a frame later,
    // when newer steps may already have made the box taller, and must not count as you scrolling.
    box.addEventListener('scroll', () => {
      const atEnd = box.scrollHeight - box.scrollTop - box.clientHeight < 40;
      if (atEnd) st.stick = true;
      else if (box.scrollTop + 1 < st.auto) st.stick = false;
      subJump(card);
    }, { passive: true });
  }
  return card._sub;
}
/** Keeps the newest step in view while following; otherwise offers "↓ Latest". */
function subFollow(card, st) {
  if (st.stick) { st.box.scrollTop = st.box.scrollHeight; st.auto = st.box.scrollTop; }
  subJump(card);
}
function subJump(card) {
  const st = card._sub;
  if (!st) return;
  let b = $(':scope > .ss-jump', st.box);
  const show = !st.stick && st.box.scrollHeight > st.box.clientHeight + 40;
  if (show && !b) { b = htmlNode('<button type="button" class="ss-jump" data-act="sub-follow">↓ Latest</button>'); st.box.appendChild(b); }
  if (b) b.classList.toggle('hidden', !show);
}
ACTIONS['sub-follow'] = el => {
  const card = el.closest('.subagent-card, .sub-nested'), st = card && card._sub;
  if (!st) return;
  st.stick = true;
  subFollow(card, st);
};
/**
 * A thought's place in the stream: "thinking" (being written, open, following its newest words), "recent" (the newest finished
 * thought: still open, so you can read what it decided while its next steps run), or "folded" (one line with its opening words,
 * once a newer thought starts or the subagent is done). A thought you opened yourself is left alone.
 */
function subThought(it, state) {
  const n = it && it.node;
  if (!n) return;
  n.classList.toggle('thinking', state === 'thinking');
  n.classList.toggle('recent', state === 'recent');
  n.open = state !== 'folded';
  const l = $('.ss-think-label', n);
  if (l) l.textContent = state === 'thinking' ? 'Thinking…' : 'Thought';
  const p = $('.ss-think-preview', n);
  if (p) p.textContent = thoughtPreview(it.text);
}
function subFoldThoughts(st) {
  st.items.forEach(x => { if (x.kind === 'reasoning' && x.node && (x.node.classList.contains('thinking') || x.node.classList.contains('recent'))) subThought(x, 'folded'); });
}
function subAppend(st, it) {
  $('.ss-wait', st.box)?.remove();
  const prev = st.items[st.items.length - 1];
  if (it.kind === 'reasoning') subFoldThoughts(st);
  else if (prev && prev.kind === 'reasoning' && prev.node && prev.node.classList.contains('thinking')) subThought(prev, 'recent');
  it.node = htmlNode(subItemHtml(it, true));
  st.items.push(it);
  // New steps go before the "Latest" button, which stays at the bottom of the box.
  st.box.insertBefore(it.node, $(':scope > .ss-jump', st.box));
}
/** What the subagent is doing now, on its card's line: "Reading src/app.js…", "Thinking…", "Writing…". */
function subActivity(card, text) {
  const p = $(':scope > summary .tool-progress', card);
  if (p) p.textContent = text;
}
/** "4 tool uses · 12s" on a working subagent's line (and the time ticks while any works). */
let subTicker = 0;
function subStats(card) {
  const el = $(':scope > summary .sub-stats', card);
  if (!el) return;
  const uses = card._sub ? card._sub.items.filter(x => x.kind === 'tool').length : 0;
  const started = Number(card.dataset.started) || 0, secs = started ? Math.floor((Date.now() - started) / 1000) : 0;
  el.textContent = [uses ? `${uses} tool use${uses === 1 ? '' : 's'}` : '', secs >= 1 ? dur(secs * 1000) : ''].filter(Boolean).join(' · ');
}
function subTick() {
  const cards = $$('.flow .subagent-card.running');
  if (!cards.length) { clearInterval(subTicker); subTicker = 0; return; }
  cards.forEach(subStats);
}
// Opening a working subagent's card shows its newest step and follows from there (a finished one opens at its start).
document.addEventListener('toggle', ev => {
  const card = ev.target;
  if (!card || !card.classList || !card.open || !(card.classList.contains('subagent-card') || card.classList.contains('sub-nested'))) return;
  if (!(card.classList.contains('running') || card.classList.contains('sub-live'))) return;
  const box = $(':scope > [data-sub-stream]', card);
  if (!box) return;
  if (card._sub) card._sub.stick = true;
  requestAnimationFrame(() => {
    box.scrollTop = box.scrollHeight;
    if (card._sub) card._sub.auto = box.scrollTop;
    subJump(card);
    if (card.classList.contains('subagent-card')) card.scrollIntoView({ block: 'nearest', behavior: reducedMotion() ? 'auto' : 'smooth' });
  });
}, true);
/** One "subagent" event in its card. */
function subagentEvent(root, d) {
  const card = $(`.flow [data-call="${CSS.escape(String(d.id || ''))}"]`, root);
  if (!card) return;
  const st = subState(card), box = st.box;
  const last = st.items[st.items.length - 1];
  if (!subTicker && card.classList.contains('subagent-card')) subTicker = setInterval(subTick, 1000);
  switch (d.kind) {
    case 'start':
      card.classList.add('sub-live'); box.classList.add('live');
      if (card.classList.contains('sub-nested') && d.label) { const sm = $(':scope > summary', card); if (sm) sm.textContent = d.label; }
      break;
    case 'text': case 'reasoning':
      if (last && last.kind === d.kind) last.text += d.text || '';
      else subAppend(st, { kind: d.kind, text: d.text || '' });
      st.dirty.add(st.items[st.items.length - 1]);
      subActivity(card, d.kind === 'reasoning' ? 'Thinking…' : 'Writing…');
      if (!st.raf) st.raf = requestAnimationFrame(() => {
        st.raf = 0;
        st.dirty.forEach(it => {
          if (it.kind === 'text') it.node.innerHTML = chatMarkdown(it.text, [], true);
          else {
            // The thought box shows its newest lines while it thinks.
            const t = $('.ss-think-text', it.node);
            if (t) { t.textContent = it.text; t.scrollTop = t.scrollHeight; }
          }
        });
        st.dirty.clear();
        subFollow(card, st);
      });
      return;
    case 'tool': {
      const c = d.call || {};
      const it = { kind: 'tool', id: c.id, name: c.name, target: c.target, ok: d.phase === 'end' ? !!c.ok : null, result: c.result, meta: c.meta };
      const was = c.id && st.items.find(x => x.kind === 'tool' && x.id === c.id);
      if (was) {
        // Its result came: the row keeps its place (and stays open if you opened it).
        const opened = was.node && was.node.open;
        Object.assign(was, it);
        const node = htmlNode(subItemHtml(was));
        if (opened) node.open = true;
        was.node.replaceWith(node); was.node = node;
      } else {
        subAppend(st, it);
        const verb = toolVerb(c.name || 'tool');
        subActivity(card, `${TOOL_GERUNDS[verb] || 'Using ' + verb}${c.target ? ' ' + toolShortPath(c.target, 60) : ''}…`);
        // A subagent of this subagent: its own stream goes in a nested card right under its step.
        if (SUBAGENT_TOOLS.has(c.name) && c.id) {
          const nest = htmlNode(`<details class="sub-nested" data-call="${esc(c.id)}" open><summary>${esc(c.target || 'subagent')}</summary></details>`);
          it.node.after(nest);
          st.items.push({ kind: 'nest', node: nest });
        }
      }
      subStats(card);
      break;
    }
    case 'note': subAppend(st, { kind: 'note', text: d.text }); break;
    case 'end':
      card.classList.remove('sub-live'); box.classList.remove('live'); $('.ss-wait', box)?.remove();
      subFoldThoughts(st);
      subActivity(card, 'finishing…');
      break;
  }
  subFollow(card, st);
}
/** A subagent's transcript so far (a window opening the chat mid-answer), then its live events continue it. */
function subagentRestore(root, id, tr) {
  const card = $(`.flow [data-call="${CSS.escape(String(id || ''))}"]`, root);
  if (!card || !tr) return;
  const st = subState(card);
  st.box.innerHTML = '';
  st.items = [];
  (tr.items || []).forEach(it => subAppend(st, Object.assign({}, it)));
  if (tr.done) subFoldThoughts(st);
  card.classList.toggle('sub-live', !tr.done);
  st.box.classList.toggle('live', !tr.done);
  if (!tr.done) card.open = true;
  st.stick = true;
  subFollow(card, st);
  subStats(card);
  if (!tr.done && !subTicker && card.classList.contains('subagent-card')) subTicker = setInterval(subTick, 1000);
}
/** An app preview link: absolute on this server, so it also opens from the desktop app and VS Code. */
function previewUrl(path) { return ((CHAT.meta && CHAT.meta.publicOrigin) || '') + path; }
/** preview_app worked: an "Open app" card instead of the tool's raw output. */
function appCardHtml(t) {
  const m = /(\/container\/[a-z0-9-]+\/)/.exec(t.result || '');
  if (!m) return '';
  const name = (/container ([^,]+), host port (\d+)/.exec(t.result || '') || []);
  return `<div class="app-card" data-call="${esc(t.id)}"><span class="app-ic">${icon('globe')}</span>
    <span class="app-meta"><b>${esc(name[1] || 'Your app')}</b><span class="muted small">${name[2] ? 'port ' + esc(name[2]) + ' · ' : ''}running in the sandbox</span></span>
    <a class="btn xs primary" href="${esc(previewUrl(m[1]))}" target="_blank" rel="noopener">${icon('external')} Open app</a></div>`;
}
function toolCardHtml(t) {
  const running = t.ok === undefined || t.result === undefined;
  if (!running && t.ok && t.name === 'preview_app') { const card = appCardHtml(t); if (card) return card; }
  const isSub = SUBAGENT_TOOLS.has(t.name);
  const state = running ? 'running' : t.ok ? 'ok' : 'bad';
  const statusIcon = running ? '<span class="spinner"></span>' : t.ok ? icon('check') : icon('alert');
  const expand = running ? '' : `<button class="btn xs ghost icon-only tc-expand" data-act="embed-expand" data-kind="tool" aria-label="Open full screen" data-tip="Open full screen">${EXPAND_ICON}</button>`;
  if (isSub) {
    // A subagent's card is open while it works: what it writes, thinks and does streams into it; its prompt and report fold away.
    const ioHtml = () => `<div class="tool-io"><div class="small strong">Prompt</div><pre>${toolArgs(t).prompt ? esc(toolArgs(t).prompt) : jsonOr(t.arguments)}</pre>${running ? '' : `<div class="small strong">Report</div><pre>${jsonOr(t.result)}</pre>`}</div>`;
    // Done: its transcript, prompt and report are drawn when the card (and its "Prompt and report") is opened.
    const io = running ? ioHtml() : lazyHtml(ioHtml);
    const stream = running ? subStreamHtml(t.subagent, true) : lazyHtml(() => subStreamHtml(t.subagent, false));
    // Done: "3 tool uses · 1.9s" (like Claude Code's "Done (3 tool uses · 1.9s)").
    const uses = ((t.subagent && t.subagent.items) || []).filter(x => x.kind === 'tool').length;
    const doneMeta = [t.ok ? '' : 'failed', uses ? `${uses} tool use${uses === 1 ? '' : 's'}` : '', t.durationMs ? dur(t.durationMs) : ''].filter(Boolean).join(' · ');
    // Working: what it does now ("Reading src/app.js…"), and on the right "4 tool uses · 12s".
    return `<details class="tool-card ${state} subagent-card"${running ? ` open data-started="${Date.now()}"` : ''} data-call="${esc(t.id)}"><summary>${statusIcon}
        <span class="tc-verb">Subagent</span>${subagentLabel(t)}<span class="muted small${running ? ' tool-progress' : ''}">${running ? 'starting…' : doneMeta}</span>
        <span class="spacer"></span>${running ? '<span class="muted small sub-stats"></span>' : ''}${expand}</summary>${stream + `<details class="sub-io"><summary>${running ? 'Prompt' : 'Prompt and report'}</summary>${io}</details>`}</details>`;
  }
  // What it did and to what ("Read src/app.js", "Run dotnet build"), what came of it ("+3 −1", "exit 0"), and the details
  // when opened: an edit's diff, a command with its output, the result. A closed card's details are drawn when it opens.
  const v = toolView(t, false);
  const open = v.open && !CARDS_FOLDED;
  const ioHtml = () => `<div class="tool-io">${toolView(t).body}</div>`;
  const waiting = running && CHAT.approvals && CHAT.approvals.has(t.id);
  return `<details class="tool-card ${state}${waiting ? ' needs-approval' : ''}"${open ? ' open' : ''} data-call="${esc(t.id)}" data-tool="${esc(t.name)}"><summary>${statusIcon}
      <span class="tc-verb">${esc(v.verb)}</span>${v.server ? `<span class="tc-server">${esc(v.server)}</span>` : ''}${v.target ? `<span class="tc-target${v.file ? ' tc-link' : ''}" data-tip="${esc(v.file ? `${localFileTip()} ${v.target}` : v.target)}"${localFileAttrs(v.file)}>${esc(toolShortPath(v.target))}</span>` : ''}
      ${v.meta ? `<span class="tc-meta ${v.metaClass}">${v.meta}</span>` : ''}<span class="spacer"></span>
      <span class="muted small${running ? ' tool-progress' : ' tc-time'}">${running ? (waiting ? 'waiting for your approval' : '') : (t.durationMs ? dur(t.durationMs) : '')}</span>${expand}</summary>
    ${running || open ? ioHtml() : lazyHtml(ioHtml)}</details>`;
}
/** A tool card while its call waits for the user's approval says so (and stops saying so once answered). */
function markApprovalOnCard(id, waiting) {
  $$(`.flow .tool-card[data-call="${CSS.escape(String(id || ''))}"]`).forEach(card => {
    card.classList.toggle('needs-approval', waiting);
    const p = $('.tool-progress', card);
    if (p && !card.classList.contains('subagent-card')) p.textContent = waiting ? 'waiting for your approval' : '';
  });
}
/** Up to six sources show as chips; more collapse into one compact row of site icons that opens a list. */
function sourcesHtml(sources) {
  if (!sources || !sources.length) return '';
  if (sources.length <= 6) {
    return `<div class="sources">${sources.map((s, i) => `<a class="source" href="${esc(s.url)}" target="_blank" rel="noopener noreferrer" data-tip="${esc(s.snippet || s.title || '')}" data-i="${i}">
      ${favicon(s.url)}<span class="st">${esc(s.title || hostOf(s.url))}</span><span class="sh">${esc(hostOf(s.url))}</span><span class="sn">${s.index}</span></a>`).join('')}</div>`;
  }
  const hosts = [...new Set(sources.map(s => hostOf(s.url)).filter(Boolean))];
  return `<details class="sources-compact"><summary><span class="fav-stack">${sources.slice(0, 5).map((s, i) => `<span class="fs" data-i="${i}">${favicon(s.url)}</span>`).join('')}</span>
      <b>${num(sources.length)} sources</b><span class="muted small sc-hosts">${esc(hosts.slice(0, 4).join(', '))}${hosts.length > 4 ? ` +${hosts.length - 4}` : ''}</span>
      <svg viewBox="0 0 24 24" class="ic chev"><path d="M6 9l6 6 6-6"/></svg></summary>
    <div class="source-list">${sources.map((s, i) => `<a class="source-row" href="${esc(s.url)}" target="_blank" rel="noopener noreferrer" data-i="${Math.min(i, 20)}">
      <span class="sn">${s.index}</span>${favicon(s.url)}<span class="sr-text"><span class="st">${esc(s.title || hostOf(s.url))}</span><span class="sh">${esc(hostOf(s.url))}${s.snippet ? ' — ' + esc(s.snippet.slice(0, 140)) : ''}</span></span></a>`).join('')}</div></details>`;
}
/**
 * Follows the conversation while you are at the bottom (CHAT.stick); scrolling up stops it, and sending, opening a chat
 * or the "newest message" button starts it again.
 */
function unstickThread() { CHAT.stick = false; CHAT.unstuckAt = Date.now(); }
/** The box inside the thread (a code block, tool output, thinking, sources) that would take a vertical wheel turn here, or null. */
function innerScroller(el, sc) {
  for (let n = el; n && n !== sc; n = n.parentElement) {
    if (n.scrollHeight > n.clientHeight + 1) {
      const oy = getComputedStyle(n).overflowY;
      if (oy === 'auto' || oy === 'scroll') return n;
    }
  }
  return null;
}
document.addEventListener('keydown', ev => {
  if (['PageUp', 'ArrowUp', 'Home'].includes(ev.key) && $('#chat-scroll') && !elOf(ev)?.closest('input, textarea, select, [contenteditable="true"]')) unstickThread();
});
/** Where the reader is inside an answer (which block is at the top of the view, and how far down), to keep it there. */
function readingAnchor(sc, root) {
  const top = sc.getBoundingClientRect().top, blocks = $$('.answer > *', root);
  for (let i = 0; i < blocks.length; i++) { const r = blocks[i].getBoundingClientRect(); if (r.bottom > top) return { i, offset: r.top - top }; }
  return { i: -1, offset: root.getBoundingClientRect().top - top };
}
function restoreReading(sc, root, a) {
  const target = a.i >= 0 ? $$('.answer > *', root)[a.i] : root;
  if (target) sc.scrollTop += target.getBoundingClientRect().top - sc.getBoundingClientRect().top - a.offset;
}
function scrollThread(force) {
  const sc = $('#chat-scroll');
  if (!sc) return;
  if (force) CHAT.stick = true;
  if (CHAT.stick) sc.scrollTop = sc.scrollHeight;
  syncToBottom();
}
/**
 * The "to the newest message" button shows only when the newest message is well out of view. It is checked whenever the
 * view could have changed - scrolling, the thread or the view changing size, another chat opening - not only on scroll:
 * a new chat's thread is shorter and fires no scroll event, which left the button showing at the bottom of an empty chat.
 */
let toBottomFrame = 0;
function syncToBottom() {
  if (toBottomFrame) return;
  toBottomFrame = requestAnimationFrame(() => {
    toBottomFrame = 0;
    const sc = $('#chat-scroll'), btn = $('.to-bottom');
    if (!sc || !btn) return;
    const gap = sc.scrollHeight - sc.scrollTop - sc.clientHeight;
    const show = sc.scrollHeight > sc.clientHeight + 4 && gap >= 240 && !CHAT.stick;
    btn.classList.toggle('hidden', !show);
    // Just above the message box however tall it grows (a long draft): kept at the bottom of the thread's visible area.
    const main = sc.closest('.chat-main');
    if (main) btn.style.bottom = Math.max(14, Math.round(main.getBoundingClientRect().bottom - sc.getBoundingClientRect().bottom + 14)) + 'px';
  });
}
ACTIONS['msg-copy'] = async el => {
  const id = el.closest('.msg').dataset.id;
  const m = CHAT.conv && CHAT.conv.messages.find(x => x.id === id);
  try {
    await writeClipboardOrThrow(m ? m.content : (el.closest('.msg').querySelector('.answer, .md') || {}).innerText || '');
    const before = el.innerHTML; el.innerHTML = icon('check'); el.classList.add('done');
    setTimeout(() => { el.innerHTML = before; el.classList.remove('done'); }, 1400);
  } catch { toast('Copy is not available here.', 'bad'); }
};
ACTIONS['msg-regen'] = () => { if (!currentStream()) sendMessage(true).catch(e => toast(e.message, 'bad')); };
/** An answer that failed part-way (the model's connection gave out after its retries) after writing or running something. */
function failedWithWork(m) {
  return !!(m && m.role === 'assistant' && m.finishReason === 'error' && ((m.content || '').trim() || (m.toolCalls || []).length));
}
/** Try again: a failed answer that had done work picks up where it stopped (its text, tools and results stay); anything else is answered again. */
ACTIONS['msg-retry'] = () => {
  if (currentStream()) return;
  const msgs = CHAT.conv ? CHAT.conv.messages : [];
  const resume = failedWithWork(msgs[msgs.length - 1]);
  sendMessage(!resume, undefined, resume ? { continue: true } : {}).catch(e => toast(e.message, 'bad'));
};
/** "keep going", "continue", "try again" … typed after a failed answer: carry on with it (see sendMessage). */
const KEEP_GOING = /^(please\s+)?(keep\s+going|keep\s+trying|continue|carry\s+on|go\s+on|try\s+again|retry|resume)[\s.!]*(please)?[\s.!]*$/i;
/** How long a failed answer waits before it is tried again by itself: one wait per attempt, then it is left to the user. */
const AUTO_CONTINUE_WAITS = [60, 180, 600];
/** Per chat: automatic tries so far for the failed answer, and the timer waiting to make the next. */
const AUTO_CONTINUE = new Map();
/** A failure worth trying again later: the model's connection or provider, not a refusal or a bad request. */
function transientFailure(m) {
  return !!(m && /gave up after|did not respond in time|took too long|upstream|connection|temporarily|unavailable|overloaded|rate.?limit|timed out/i.test(m.error || ''));
}
function cancelAutoContinue(convId, reset) {
  const a = AUTO_CONTINUE.get(convId);
  if (!a) return;
  if (a.timer) { clearTimeout(a.timer); a.timer = null; }
  if (reset) AUTO_CONTINUE.delete(convId);
}
/**
 * An answer with work in it failed after its retries (the model's provider down for a while): it is picked up again by itself
 * after 1, 3 and 10 minutes - from this window, so the tools on this computer are there - unless the user acts first.
 * Giving up at once threw away a plan in progress over a short outage.
 */
function scheduleAutoContinue(conv, m) {
  const a = AUTO_CONTINUE.get(conv.id) || { count: 0, timer: null };
  if (a.timer) return;
  if (a.count >= AUTO_CONTINUE_WAITS.length) {
    AUTO_CONTINUE.delete(conv.id);
    toast('The answer failed again. Press Try again, or type "keep going", to carry on.', 'bad');
    return;
  }
  const wait = AUTO_CONTINUE_WAITS[a.count];
  a.count++;
  AUTO_CONTINUE.set(conv.id, a);
  toast(`The model's connection failed. Trying again by itself in ${wait >= 60 ? Math.round(wait / 60) + ' min' : wait + ' s'} (${a.count} of ${AUTO_CONTINUE_WAITS.length}); sending a message cancels it.`, 'warn');
  a.timer = setTimeout(() => {
    a.timer = null;
    if (!CHAT.conv || CHAT.conv.id !== conv.id || currentStream() || CHAT.live.has(conv.id)) return;
    const msgs = CHAT.conv.messages;
    const last = msgs[msgs.length - 1];
    if (!last || last.id !== m.id || !failedWithWork(last)) return;
    sendMessage(false, undefined, { continue: true, auto: true }).catch(e => toast(e.message, 'bad'));
  }, wait * 1000);
}

// ------------------------------------------------------------------ questions from the model (ask_user)
function showPendingQuestion() {
  const msgs = CHAT.conv ? CHAT.conv.messages : [];
  const last = msgs[msgs.length - 1];
  if (!last || last.role !== 'assistant' || currentStream()) return;
  const call = (last.toolCalls || []).filter(t => t.name === 'ask_user').pop();
  if (call && !(CHAT.ask && CHAT.ask.convId === CHAT.conv.id)) showQuestions(questionsOf(call));
}
function showQuestions(questions) {
  if (!questions.length) return;
  CHAT.ask = { questions, answers: [], step: 0, convId: CHAT.conv && CHAT.conv.id };
  renderQuestion();
}
function renderQuestion() {
  const pop = $('#ask-pop'), a = CHAT.ask;
  if (!pop || !a) return;
  const q = a.questions[a.step], total = a.questions.length;
  const keys = [1, 2, 3, 4, 5].map(n => esc(keyLabel('chat.answer' + n) || '·'));   // the keys that pick each answer (Keyboard shortcuts)
  pop.innerHTML = `<div class="ask-card" data-step="${a.step}">
    <div class="ask-head"><span class="ask-badge">${icon('info')}</span><span class="ask-kicker">${total > 1 ? `Question ${a.step + 1} of ${total}` : 'The assistant needs your input'}</span>
      ${total > 1 ? `<span class="ask-dots">${a.questions.map((_, i) => `<i class="${i < a.step ? 'done' : i === a.step ? 'on' : ''}"></i>`).join('')}</span>` : ''}
      <span class="spacer"></span><button class="btn xs ghost" data-act="ask-skip" data-tip="Answer in the message box instead">Skip</button></div>
    <div class="ask-q">${esc(q.question)}</div>
    <div class="ask-opts">${(q.options || []).map((o, i) => `<button class="ask-opt" data-act="ask-pick" data-i="${i}" data-i2="${i}"><kbd>${keys[i]}</kbd><span class="ask-opt-text">${esc(o)}</span><svg viewBox="0 0 24 24" class="ic ask-go"><path d="M5 12h13M13 6l6 6-6 6"/></svg></button>`).join('')}
      ${q.allowOther !== false || !(q.options || []).length ? `<button class="ask-opt other" data-act="ask-other"><kbd>${(q.options || []).length ? keys[(q.options || []).length] : '✎'}</kbd><span class="ask-opt-text">Other…<span class="ask-opt-hint">type your own answer</span></span><svg viewBox="0 0 24 24" class="ic ask-go"><path d="M5 12h13M13 6l6 6-6 6"/></svg></button>` : ''}</div>
    <div class="ask-other-box${(q.options || []).length ? ' hidden' : ''}"><textarea id="ask-text" rows="2" placeholder="Type your answer…"></textarea>
      <button class="btn primary" data-act="ask-send-other">${a.step + 1 < total ? 'Next' : 'Send'}</button></div>
    ${a.step > 0 ? '<button class="btn xs ghost ask-back" data-act="ask-back">← Back</button>' : ''}</div>`;
  $$('.ask-opt', pop).forEach((b, i) => b.style.setProperty('--i', i));
  pop.classList.remove('hidden');
  if (!(q.options || []).length) setTimeout(() => $('#ask-text') && $('#ask-text').focus(), 60);
  const ta = $('#ask-text');
  ta.addEventListener('keydown', ev => {
    if (ev.isComposing) return;
    if (keyIs(ev, 'chat.send')) { ev.preventDefault(); ACTIONS['ask-send-other'](); }
    else if (keyIs(ev, 'chat.newline')) keyNewline(ev);
  });
}
function hideQuestion(instant) {
  const pop = $('#ask-pop');
  CHAT.ask = null;
  if (!pop || pop.classList.contains('hidden')) return;
  if (instant || reducedMotion()) { pop.classList.add('hidden'); pop.innerHTML = ''; return; }
  pop.classList.add('closing');
  setTimeout(() => { pop.classList.add('hidden'); pop.classList.remove('closing'); pop.innerHTML = ''; }, 200);
}
/** The question card changed without a message being sent (next question, back, skipped): Dictation mode follows it. */
function questionChanged(kind) { document.dispatchEvent(new CustomEvent('chat:question', { detail: { kind } })); }
function answerQuestion(text) {
  const a = CHAT.ask;
  if (!a || !text.trim()) return;
  a.answers[a.step] = text.trim();
  if (a.step + 1 < a.questions.length) { a.step++; renderQuestion(); questionChanged('next'); return; }
  if (a.onDone) { const done = a.onDone, answers = a.answers.slice(); hideQuestion(); done(answers); return; }
  const content = a.questions.length === 1 ? a.answers[0] : a.questions.map((q, i) => `**${q.question}**\n${a.answers[i]}`).join('\n\n');
  hideQuestion();
  sendMessage(false, content).catch(e => toast(e.message, 'bad'));
}
ACTIONS['ask-pick'] = el => { el.classList.add('chosen'); const q = CHAT.ask.questions[CHAT.ask.step]; setTimeout(() => answerQuestion(q.options[Number(el.dataset.i)]), reducedMotion() ? 0 : 160); };
ACTIONS['ask-other'] = el => { $$('#ask-pop .ask-opt').forEach(b => b.classList.toggle('chosen', b === el)); $('#ask-pop .ask-other-box').classList.remove('hidden'); $('#ask-text').focus(); };
ACTIONS['ask-send-other'] = () => { const t = $('#ask-text'); if (t && t.value.trim()) answerQuestion(t.value); else if (t) t.focus(); };
ACTIONS['ask-back'] = () => { if (CHAT.ask && CHAT.ask.step > 0) { CHAT.ask.step--; renderQuestion(); questionChanged('back'); } };
ACTIONS['ask-skip'] = () => { const done = CHAT.ask && CHAT.ask.onDone; hideQuestion(); if (done) done(null); $('#chat-input').focus(); questionChanged('skip'); };
// Back to this window after a while: the question may have been answered elsewhere in the meantime.
document.addEventListener('visibilitychange', async () => {
  if (document.visibilityState !== 'visible' || !CHAT.ask || !CHAT.conv || currentStream() || CHAT.watch) return;
  const id = CHAT.conv.id, fresh = await get('/api/chat/conversations/' + enc(id)).catch(() => null);
  if (fresh && CHAT.conv && CHAT.conv.id === id && (fresh.messages || []).length !== (CHAT.conv.messages || []).length) resyncConversation();
});
document.addEventListener('keydown', ev => {
  if (!CHAT.ask || !$('#ask-pop') || $('#ask-pop').classList.contains('hidden')) return;
  if (document.activeElement && /^(TEXTAREA|INPUT)$/.test(document.activeElement.tagName)) return;
  for (let n = 1; n <= 5; n++) {
    if (!keyIs(ev, 'chat.answer' + n)) continue;
    const btn = $$('#ask-pop .ask-opt')[n - 1];
    if (btn) { ev.preventDefault(); btn.click(); }
    return;
  }
});

// ------------------------------------------------------------------ sending and streaming
function currentStream() { return CHAT.conv ? CHAT.streams.get(CHAT.conv.id) : null; }
/** Busy while this window streams the answer, or another window / the desktop app is answering in this chat. */
function syncBusy() {
  const c = $('#composer'); if (!c) return;
  if (typeof renderGoalBar === 'function') renderGoalBar();
  const elsewhere = !currentStream() && CHAT.conv && CHAT.live.has(CHAT.conv.id);
  c.classList.toggle('busy', !!currentStream());
  c.classList.toggle('remote', !!elsewhere);
  const hint = $('#composer-hint');
  if (hint) hint.textContent = !elsewhere ? routeHint() : liveAuto(CHAT.conv.id) === 'goal'
    ? 'Working on the goal by itself (automatic continuation) — follow it here, answer its questions, or stop it with ■.'
    : liveAuto(CHAT.conv.id) === 'schedule'
      ? 'Answering a scheduled prompt (/schedule) — follow it here, answer its questions, or stop it with ■.'
      : `Answering in ${liveWhere(CHAT.conv.id)} — follow it here, answer its questions, or stop it with ■.`;
}
/** Why an answer runs that this window did not start: 'goal' = the server continues the chat's goal by itself. */
function liveAuto(chatId) { const r = CHAT.live.get(chatId); return (r && r.auto) || ''; }
function liveWhere(chatId) {
  const r = CHAT.live.get(chatId);
  // No window sent the goal's automatic continuation: it is not 'another window'.
  if (r && r.auto === 'goal') return 'the goal’s automatic continuation';
  if (r && r.auto === 'schedule') return 'a scheduled prompt';
  if (r && r.auto === 'monitor') return 'monitor events';
  const dev = r && r.device && CHAT.devices.get(r.device);
  return dev ? dev.name || 'the desktop app' : 'another window';
}
/** The badge on an answer this window follows. */
function liveBadge(chatId) { const a = liveAuto(chatId); return a === 'goal' ? '↻ Goal · automatic' : a === 'schedule' ? '⏰ Scheduled' : a === 'monitor' ? '👁 Monitor events' : liveWhere(chatId); }

// ------------------------------------------------------------------ a streaming answer (this window's or another window's)
/**
 * The assistant's bubble while an answer is written. Used for this window's own answers and for answers written in
 * another window or the desktop app (followed live through the chat event stream).
 */
// None of these are emoji (U+2733 ✳ is, and phones drew it as one): text symbols only.
const SPIN_GLYPHS = ['·', '✢', '✶', '✷', '✻', '✽'];
const WORKING_WORDS = ['Thinking', 'Pondering', 'Musing', 'Cogitating', 'Ruminating', 'Mulling', 'Deliberating', 'Brewing', 'Percolating', 'Noodling',
  'Conjuring', 'Synthesizing', 'Contemplating', 'Crafting', 'Computing', 'Wrangling', 'Untangling', 'Assembling', 'Sketching', 'Tinkering',
  'Distilling', 'Weaving', 'Hatching', 'Simmering', 'Marinating', 'Churning', 'Puzzling', 'Reasoning', 'Considering', 'Figuring', 'Envisioning', 'Forging'];
let lastWord = '';
function workingWord() {
  let w;
  do { w = WORKING_WORDS[Math.floor(Math.random() * WORKING_WORDS.length)]; } while (w === lastWord);
  lastWord = w;
  return w;
}
/** The dots after the working word: ... .. . .. ... */
const STATUS_DOTS = ['...', '..', '.', '..'];
function createLiveView(conv, model, opts = {}) {
  const live = { id: null, model, content: '', reasoning: '', toolCalls: [], sources: [], routedModel: null, routeInfo: null };
  const el = htmlNode(`<div class="msg assistant new streaming${opts.remote ? ' remote' : ''}"><div class="avatar"><img src="/assets/img/favicon.ico" alt=""></div><div class="msg-body">
      <div class="msg-head"><b>${esc(modelName(model))}</b> <span class="route-slot"></span>${opts.remote ? `<span class="badge outline remote-badge">${esc(opts.remote)}</span>` : ''}</div>
      <div class="images-slot gen-images"></div><div class="flow"><div class="md answer"></div></div>
      <div class="live-tail"><span class="status-line"><span class="spin-glyph" aria-hidden="true">✻</span><span class="st-text">${esc(workingWord())}</span><span class="st-dots" aria-hidden="true">...</span><span class="st-meta"></span></span></div>
      <div class="question-slot"></div><div class="sources-slot"></div></div></div>`);
  let raf = 0, paintTimer = 0, lastPaint = 0;
  /**
   * The text being written is drawn again at most once a frame - and less often as it grows: its Markdown is parsed whole
   * each time, so a long part drawn every frame kept the window busy and laggy (most in the desktop app and VS Code).
   */
  const schedulePaint = () => {
    if (raf || paintTimer) return;
    const len = live.content.length - segStart;
    const gap = len > 24000 ? 450 : len > 10000 ? 250 : len > 3000 ? 120 : 0;
    const wait = lastPaint + gap - performance.now();
    if (wait > 0) paintTimer = setTimeout(() => { paintTimer = 0; raf = requestAnimationFrame(paint); }, wait);
    else raf = requestAnimationFrame(paint);
  };
  const cancelPaint = () => { if (raf) cancelAnimationFrame(raf); if (paintTimer) clearTimeout(paintTimer); raf = 0; paintTimer = 0; };
  // Thinking: each phase gets its own block in the flow, open while the model thinks and closed once it writes or uses
  // a tool (the user can open it again to read it). live.reasoningParts keeps where each one started.
  let think = null;
  const thinkEls = [];
  live.reasoningParts = [];
  const thinkDone = () => {
    if (!think) return;
    think.el.removeAttribute('open');
    const s = Math.max(1, Math.round((Date.now() - think.began) / 1000));
    $('summary', think.el).textContent = `Thought for ${s}s`;
    think = null;
  };
  // The text written since the last tool call goes into the last part of the flow (".answer"); earlier parts are done.
  let segStart = 0;
  const seg = () => $('.flow > .answer', el);
  /** A tool call: the text before it is finished, the card goes after it, and later text starts a new part below. */
  const placeTool = node => {
    const cur = seg();
    if (live.content.slice(segStart).trim()) {
      cancelPaint();
      cur.innerHTML = chatMarkdown(live.content.slice(segStart), live.sources);
      cur.classList.replace('answer', 'answer-part');
      cur.after(node);
      node.insertAdjacentHTML('afterend', '<div class="md answer"></div>');
      segStart = live.content.length;
      applyRich(cur);
    } else cur.before(node);
  };
  const paint = () => {
    raf = 0;
    lastPaint = performance.now();
    // Keep the scroll position of code blocks being written so the newest lines stay visible.
    segStart = Math.min(segStart, live.content.length);
    const part = seg();
    if (!part) return;
    part.innerHTML = chatMarkdown(live.content.slice(segStart), live.sources, true);
    // Only the part being written changed: the rest of the answer (earlier parts, tool cards) is not looked at again.
    applyRich(part);
    $$('.code-block.open pre', part).forEach(p => { p.scrollTop = p.scrollHeight; });
    if (el.isConnected) scrollThread();
  };
  // Generic "thinking" shows changing words; specific statuses (searching, using a tool…) are shown as they are.
  let generic = true;
  const setText = t => {
    t = String(t || '').replace(/(…|\.\.\.)\s*$/, '');   // the dots are animated separately
    const s = $('.st-text', el);
    if (!s || s.textContent === t) return;
    s.textContent = t;
    if (!reducedMotion()) { s.classList.remove('swap'); void s.offsetWidth; s.classList.add('swap'); }
  };
  const status = t => {
    const s = $('.status-line', el);
    if (!s) return;
    const wasGeneric = generic, wasHidden = s.classList.contains('hidden');
    generic = !t || /^(thinking|writing)…?$/i.test(t);
    // Repeated 'thinking' updates keep the current word; the timer changes it every few seconds.
    if (!generic) setText(t); else if (!wasGeneric || wasHidden) setText(workingWord());
    s.classList.remove('hidden');
    if (el.isConnected) scrollThread();
  };
  const started = Date.now();
  let gi = 0, dir = 1, tick = 0;
  const ticker = setInterval(() => {
    const g = $('.spin-glyph', el), m = $('.st-meta', el), dots = $('.st-dots', el);
    if (g) { gi += dir; if (gi <= 0 || gi >= SPIN_GLYPHS.length - 1) dir = -dir; g.textContent = SPIN_GLYPHS[gi]; }
    if (dots && !reducedMotion() && ++tick % 3 === 0) dots.textContent = STATUS_DOTS[(tick / 3) % STATUS_DOTS.length];
    if (m) { const s = Math.floor((Date.now() - started) / 1000); m.textContent = (s < 60 ? s + 's' : Math.floor(s / 60) + 'm ' + String(s % 60).padStart(2, '0') + 's') + (opts.remote || TOUCH_ONLY.matches ? '' : ' · esc to stop'); }
  }, 130);
  const words = setInterval(() => { if (generic) setText(workingWord()); }, 2600);
  /** Continuing an answer: what it says so far moves into this bubble, and the new text is written after it. */
  const adoptContinued = id => {
    const target = CHAT.conv && CHAT.conv.id === conv.id ? CHAT.conv : conv;
    const prev = target.messages.find(m => m.id === id);
    const node = $(`#chat-thread .msg[data-id="${CSS.escape(id)}"]`);
    if (node && node !== el) node.remove();
    if (!prev) return;
    live.content = prev.content || '';
    live.sources = prev.sources || [];
    live.toolCalls = (prev.toolCalls || []).slice();
    // What it says so far keeps its tools in place; the continuation is written after it.
    $('.flow', el).innerHTML = flowHtml(prev, cardCalls(prev.toolCalls));
    const last = $('.flow > .answer', el);
    if (last) last.classList.replace('answer', 'answer-part');
    $('.flow', el).insertAdjacentHTML('beforeend', '<div class="md answer"></div>');
    segStart = live.content.length;
    if (live.sources.length) $('.sources-slot', el).innerHTML = sourcesHtml(live.sources);
    (prev.attachments || []).filter(a => a.uploadId && (a.kind === 'image' || a.kind === 'video')).forEach(a => $('.images-slot', el).insertAdjacentHTML('beforeend', a.kind === 'video' ? generatedVideoHtml(a) : generatedImageHtml(a)));
    view.continuing = true;
    view.resumed = prev.finishReason === 'error' && !!(prev.content || '').trim();
    paint();
  };
  const view = {
    el, live, chatId: conv && conv.id, questions: null, closed: false,
    apply(type, d) {
      switch (type) {
        case 'start': live.id = d.messageId; if (d.continues) adoptContinued(d.messageId); break;
        case 'status': status(String(d.text || '').replace(/^(Subagent '[^']*': )(.*)$/, (x, who, step) => who + friendlyStep(step))); el.classList.toggle('retrying', !!d.retry); break;
        // A failed model call is retried: what it had written so far is taken back.
        case 'rollback': {
          live.content = live.content.slice(0, Math.max(0, live.content.length - (d.drop || 0)));
          live.reasoning = live.reasoning.slice(0, Math.max(0, live.reasoning.length - (d.dropReasoning || 0)));
          // Thinking blocks of the round taken back go; the one before it shows what is left.
          while (live.reasoningParts.length && live.reasoningParts[live.reasoningParts.length - 1].start >= live.reasoning.length && (d.dropReasoning || 0) > 0) {
            live.reasoningParts.pop(); const gone = thinkEls.pop(); if (think && think.el === gone) think = null; if (gone) gone.remove();
          }
          const lastPart = live.reasoningParts[live.reasoningParts.length - 1];
          if (lastPart && thinkEls.length) setThinkingText($('.thinking-text', thinkEls[thinkEls.length - 1]), live.reasoning.slice(lastPart.start));
          schedulePaint(); break;
        }
        case 'goal': onGoalEvent(conv.id, d); break;
        case 'image': $('.images-slot', el).insertAdjacentHTML('beforeend', generatedImageHtml(d.attachment)); (live.attachments = live.attachments || []).push(d.attachment); status('Writing…'); break;
        case 'video': $('.images-slot', el).insertAdjacentHTML('beforeend', generatedVideoHtml(d.attachment)); (live.attachments = live.attachments || []).push(d.attachment); status('Writing…'); break;
        case 'song': $('.images-slot', el).insertAdjacentHTML('beforeend', songCardHtml(d.attachment)); (live.attachments = live.attachments || []).push(d.attachment); status('Writing…'); break;
        case 'file': { const fs = $('.files-slot', el) || $('.images-slot', el); if (fs) fs.insertAdjacentHTML('beforeend', generatedFileHtml(d.attachment)); (live.attachments = live.attachments || []).push(d.attachment); status('Sharing a file…'); break; }
        case 'memory': status(d.action === 'forgot' ? 'Updated memory…' : 'Saved to memory…'); break;
        case 'route': live.routedModel = d.model; live.routeInfo = `${d.label}: ${d.reason}`;
          $('.route-slot', el).innerHTML = `<span class="route-badge pop" data-tip="${esc(d.reason)}">${esc(d.label)} → ${esc(d.model)}</span>`; break;
        case 'sources': live.sources = d; $('.sources-slot', el).innerHTML = sourcesHtml(d); applyRich(el); status('Reading sources…'); break;
        case 'reasoning': {
          if (!view.restoring) tpsFeed(conv.id, d.text);
          if (!think) {
            // The model starts thinking (again): a new block, open while it thinks, where it is in the answer.
            const node = htmlNode('<details class="thinking live-think" open><summary>Thinking…</summary><div class="thinking-text"></div></details>');
            placeTool(node);
            think = { el: node, start: live.reasoning.length, began: Date.now() };
            thinkEls.push(node);
            live.reasoningParts.push({ start: live.reasoning.length, at: live.content.length, tools: live.toolCalls.length });
          }
          live.reasoning += d.text;
          setThinkingText($('.thinking-text', think.el), live.reasoning.slice(think.start));
          status('Thinking…');
          if (el.isConnected) scrollThread();
          break;
        }
        case 'delta':
          if (!view.restoring) tpsFeed(conv.id, d.text);
          // It writes: its thinking block closes.
          if (think && String(d.text || '').trim()) thinkDone();
          // The first words of a continued answer: a new list item or line of code goes on its own line (as the server joins them).
          // A failed answer picked up again starts a new paragraph after the tools it ran.
          if (view.continuing) { view.continuing = false; if (view.resumed) live.content += '\n\n'; else if (continuesOnNewLine(live.content, d.text)) live.content += '\n'; }
          live.content += d.text; schedulePaint(); break;
        case 'tool': {
          // It acts: its thinking block closes.
          thinkDone();
          const call = d.call, ex = $(`.flow [data-call="${CSS.escape(call.id)}"]`, el);
          if (d.phase === 'start') { if (!ex) placeTool(htmlNode(toolCardHtml({ id: call.id, name: call.name, arguments: call.arguments }))); status(toolStatusText(call)); }
          else {
            // Memory changes show as a small chip; a created image shows itself (its card only when it failed).
            const html = MEMORY_TOOLS.has(call.name) ? memoryChipHtml(call) : (call.name === 'generate_image' || call.name === 'generate_video') && call.ok ? '' : toolCardHtml(call);
            // A subagent's card that was open (its live stream) stays open with its whole transcript.
            if (html) { const node = htmlNode(html); if (ex) { if (ex.open && node.tagName === 'DETAILS') node.open = true; ex.replaceWith(node); } else placeTool(node); } else if (ex) ex.remove();
            if (call.name === 'update_todos') { $$('.flow .todo-card', el).forEach(n => { if (n.dataset.call !== call.id) n.remove(); }); setLiveTodos(conv.id, call); }
            live.toolCalls.push(call); status('Writing…');
          }
          if (el.isConnected) scrollThread(); break;
        }
        case 'subagent':
          subagentEvent(el, d);
          if (d.kind === 'start' || d.kind === 'end') clientHook(conv.id, d.kind === 'start' ? 'SubagentStart' : 'SubagentStop', { agent_type: d.type || d.label || 'subagent', agent_id: d.id });
          break;
        case 'tool_progress': {
          // A subagent (several may run at once) reports its step on its own card.
          const card = $(`.flow [data-call="${CSS.escape(String(d.id || ''))}"]`, el), p = card && $(':scope > summary .tool-progress', card);
          const said = String(d.text || '');
          // Its stream already says what it does ("Reading src/app.js…"): only what the stream cannot say goes here
          // (retries, writing its report, a progress check).
          const streamSays = card && card._sub && (/^step \d+/.test(said) || said !== friendlyStep(said));
          if (p && !streamSays) p.textContent = friendlyStep(said).slice(0, 120);
          break;
        }
        case 'question': view.questions = d.questions || []; live.toolCalls.push(d.call); $('.question-slot', el).innerHTML = askedCardHtml(d.call);
          clientHook(conv.id, 'Notification', { message: 'Agentic has a question for you', notification_type: 'question' }); break;
        case 'compacted':
          clientHook(conv.id, 'PostCompact', { trigger: 'auto' });
          status('Conversation compacted — continuing…');
          if (CHAT.conv && CHAT.conv.id === conv.id) { CHAT.conv.summaryThroughUtc = d.summaryThroughUtc; CHAT.conv.summary = null; updateContext(d); markCompacted(true, d.stats); }
          break;
      }
    },
    /** Restores an answer that was already partly written (opening a chat mid-answer). */
    restore(snap) {
      view.restoring = true;
      if (snap.route) view.apply('route', snap.route);
      if (snap.sources) view.apply('sources', snap.sources);
      const r = snap.reasoning || '', parts = (snap.reasoningParts || []).filter(Boolean);
      if (r && !parts.length) view.apply('reasoning', { text: r });
      const text = snap.content || '';
      let pos = 0;
      const tools = (snap.tools || []).filter(Boolean);
      const items = [];
      let pi = 0;
      for (let i = 0; i <= tools.length; i++) {
        while (pi < parts.length && (parts[pi].tools || 0) <= i) { const p = parts[pi]; items.push({ p, end: pi + 1 < parts.length ? parts[pi + 1].start : r.length }); pi++; }
        if (i < tools.length) items.push({ t: tools[i] });
      }
      for (const x of items) {
        const want = x.t ? (Number.isInteger(x.t.at) ? x.t.at : pos) : (x.p.at || 0);
        const at = Math.min(Math.max(want, pos), text.length);
        if (at > pos) { view.apply('delta', { text: text.slice(pos, at) }); pos = at; }
        if (x.p) { thinkDone(); view.apply('reasoning', { text: r.slice(x.p.start || 0, x.end) }); }
        else view.apply('tool', { phase: x.t.result === undefined && x.t.ok === undefined ? 'start' : 'end', call: x.t });
      }
      if (pos < text.length) view.apply('delta', { text: text.slice(pos) });
      // Subagents at work: what each wrote, thought and did so far, then live.
      Object.entries(snap.subagents || {}).forEach(([id, tr]) => subagentRestore(el, id, tr));
      if (snap.status && !snap.content) status(snap.status);
      live.id = snap.messageId;
      view.restoring = false;
    },
    finish(message, failure, aborted) {
      if (view.closed) return;
      view.closed = true;
      tpsDone(conv.id, message);
      setLiveTodos(conv.id, null);
      cancelPaint();
      clearInterval(ticker); clearInterval(words);
      const msg = message || Object.assign(live, { error: failure || (aborted ? 'Stopped.' : null) });
      if (failure && message) msg.error = msg.error || failure;
      // Stopped (or failed) after it started: the server saves what was written, but this window gets no "done" with it.
      // The text on screen goes into the conversation for now - a redraw must not drop it - and the saved message replaces
      // it when the server says it is saved (the "changed" event with stopped).
      const keep = message || (live.id && (aborted || failure) ? Object.assign(live, { role: 'assistant', provisional: true, finishReason: aborted ? 'stopped' : 'error', createdUtc: live.createdUtc || new Date().toISOString() }) : null);
      if (keep) {
        // A continued answer replaces the earlier text of the same message.
        const put = list => { const i = list.findIndex(m => m.id === keep.id); if (i >= 0) { if (!(keep.provisional && !list[i].provisional && (list[i].content || '').length > (keep.content || '').length)) list[i] = keep; } else list.push(keep); };
        const target = CHAT.conv && CHAT.conv.id === conv.id ? CHAT.conv : conv;
        put(target.messages);
        if (target !== conv) put(conv.messages);
      }
      if (el.isConnected) {
        const sc = $('#chat-scroll'), anchor = sc && !CHAT.stick ? readingAnchor(sc, el) : null;
        const opened = thinkEls.map(t => t !== (think && think.el) && t.open);
        const node = htmlNode(messageHtml(msg));
        node.classList.add('settled');
        $$('.flow > .thinking', node).forEach((t, i) => { if (opened[i]) t.open = true; });
        el.replaceWith(node);
        applyRich(node);
        if (anchor) restoreReading(sc, node, anchor);
        scrollThread();
        if (view.questions && view.questions.length && CHAT.conv && CHAT.conv.id === conv.id) showQuestions(view.questions);
        // The answer before it may no longer be the last one (its Continue button goes), and versions may have changed.
        if (CHAT.conv && CHAT.conv.id === conv.id) { $$('#chat-thread .continue-btn').forEach(b => { if (!b.closest('.msg') || b.closest('.msg') !== node) b.remove(); }); renderVersionNavs(); }
      }
    }
  };
  return view;
}

// ------------------------------------------------------------------ generation speed: tokens per second next to the model
/**
 * Estimated from the streamed text (about 4 characters a token) over the last two seconds; the final average uses the
 * model's own token count when it reports one.
 */
const TPS = new Map();
let tpsTimer = 0;
function tpsFeed(chatId, text) {
  if (!text) return;
  let m = TPS.get(chatId);
  const now = performance.now();
  if (!m || m.done) { m = { events: [], chars: 0, first: now, last: now, done: false, shown: 0, hist: [], final: 0, doneAt: 0 }; TPS.set(chatId, m); }
  m.events.push([now, text.length]); m.chars += text.length; m.last = now;
  if (!tpsTimer) tpsTimer = setInterval(tpsTick, 200);
}
function tpsDone(chatId, message) {
  const m = TPS.get(chatId);
  if (!m || m.done) return;
  m.done = true; m.doneAt = performance.now();
  const secs = Math.max(0.25, (m.last - m.first) / 1000);
  const tokens = message && message.completionTokens ? message.completionTokens : m.chars / 4;
  m.final = tokens / secs;
}
function tpsRate(m, now) {
  const span = 2000;
  m.events = m.events.filter(e => now - e[0] <= span);
  if (!m.events.length) return 0;
  const chars = m.events.reduce((n, e) => n + e[1], 0);
  return chars / 4 / (Math.max(400, Math.min(span, now - m.first)) / 1000);
}
function tpsTick() {
  const now = performance.now();
  TPS.forEach((m, id) => { if (m.done && now - m.doneAt > 7000) TPS.delete(id); });
  if (!TPS.size) { clearInterval(tpsTimer); tpsTimer = 0; }
  renderTps(now);
}
function renderTps(now = performance.now()) {
  const slot = $('#tps-slot');
  if (!slot) return;
  const m = CHAT.conv && TPS.get(CHAT.conv.id);
  if (!m) { slot.classList.remove('on-fire'); const old = $('.tps-chip', slot); if (old && !old.classList.contains('leaving')) { old.classList.remove('fire'); old.classList.add('leaving'); setTimeout(() => { if (old.classList.contains('leaving')) old.remove(); }, 260); } return; }
  let chip = $('.tps-chip', slot);
  if (!chip || chip.classList.contains('leaving')) {
    // The aura (the glow around the chip) and the heat (its red) show only while the model is on fire (over TPS_FIRE tokens per second).
    slot.innerHTML = `<span class="tps-chip" data-tip="Generation speed (estimated while streaming)"><span class="tps-aura" aria-hidden="true"></span><span class="tps-heat" aria-hidden="true"></span>
      <svg viewBox="0 0 24 24" class="ic tps-bolt"><path d="M13 2 4 14h7l-1 8 9-12h-7z"/></svg>
      <b class="tps-num">0</b><span class="tps-unit">tok/s</span><span class="tps-spark">${'<i></i>'.repeat(14)}</span></span>`;
    chip = $('.tps-chip', slot);
  }
  const idle = !m.done && now - m.last > 1500;
  const rate = m.done || idle ? 0 : tpsRate(m, now);
  if (rate > 0) m.held = rate;
  // While a tool runs (no new text) the number keeps the last speed, dimmed, instead of falling to "0.0 tok/s".
  const target = m.done ? m.final : idle ? (m.held || 0) : rate;
  m.shown += (target - m.shown) * (m.done ? 0.5 : 0.35);
  if (!m.done) { m.hist.push(rate); if (m.hist.length > 14) m.hist.shift(); }
  const v = m.shown;
  $('.tps-num', chip).textContent = v >= 100 ? Math.round(v) : v.toFixed(1);
  $('.tps-unit', chip).textContent = m.done ? 'tok/s avg' : 'tok/s';
  const peak = Math.max(20, ...m.hist);
  $$('.tps-spark i', chip).forEach((bar, i) => { const h = m.hist[i - (14 - m.hist.length)]; bar.style.height = (h === undefined ? 8 : Math.max(8, Math.round(100 * h / peak))) + '%'; });
  chip.classList.toggle('idle', idle);
  chip.classList.toggle('final', m.done);
  chip.classList.toggle('slow', !m.done && !idle && v < 15);
  chip.classList.toggle('fast', !idle && v >= 60);
  const fire = !idle && v > TPS_FIRE;
  chip.classList.toggle('fire', fire);
  slot.classList.toggle('on-fire', fire);
  chip.dataset.tip = fire ? `On fire: over ${TPS_FIRE} tokens per second${m.done ? ' (the last answer on average)' : ''}` :
    m.done ? 'Average speed of the last answer' : idle ? 'Waiting (a tool, a search or the model is thinking without output)' : 'Generation speed (estimated while streaming)';
}

// ------------------------------------------------------------------ ask about or explain selected text
function quoteText(text) { return text.replace(/\r/g, '').slice(0, 6000).split('\n').map(l => '> ' + l).join('\n'); }
function threadSelection() {
  const sel = window.getSelection();
  if (!sel || sel.isCollapsed || !sel.rangeCount) return null;
  const range = sel.getRangeAt(0), thread = $('#chat-thread');
  if (!thread || !thread.contains(range.commonAncestorContainer)) return null;
  const node = range.commonAncestorContainer.nodeType === 1 ? range.commonAncestorContainer : range.commonAncestorContainer.parentElement;
  if (!node || node.closest('textarea, input, .sel-pop, .chat-welcome')) return null;
  const text = sel.toString().trim();
  if (text.length < 2) return null;
  const rects = [...range.getClientRects()].filter(r => r.width > 0);
  return { text, first: rects[0] || range.getBoundingClientRect(), last: rects[rects.length - 1] || range.getBoundingClientRect() };
}
function hideSelPop() { const p = $('#sel-pop'); if (p) { p.remove(); } }
function showSelPop() {
  const s = threadSelection();
  if (!s) return;
  hideSelPop();
  const pop = htmlNode(`<div class="sel-pop" id="sel-pop" role="dialog" aria-label="Ask about the selected text">
    <div class="sel-actions"><button type="button" class="sel-btn" data-act="sel-ask"><svg viewBox="0 0 24 24" class="ic"><path d="M4 5h16v11H9l-5 4z"/><path d="M10 9.5a2 2 0 1 1 2.8 1.8c-.5.3-.8.7-.8 1.2M12 14.5v.01"/></svg>Ask</button>
      <span class="sel-sep"></span><button type="button" class="sel-btn" data-act="sel-explain"><svg viewBox="0 0 24 24" class="ic"><path d="M9 18h6M10 21h4M12 3a6 6 0 0 0-3.5 10.9c.6.4 1 1.1 1 1.8V16h5v-.3c0-.7.4-1.4 1-1.8A6 6 0 0 0 12 3z"/></svg>Explain</button></div>
    <div class="sel-ask hidden"><div class="sel-quote"></div><div class="sel-ask-row"><input id="sel-q" placeholder="Ask about this…" autocomplete="off">
      <button type="button" class="sel-send" data-act="sel-send" aria-label="Ask"><svg viewBox="0 0 24 24" class="ic"><path d="M5 12h13M13 6l6 6-6 6"/></svg></button></div></div></div>`);
  pop._text = s.text;
  $('.sel-quote', pop).textContent = s.text.length > 220 ? s.text.slice(0, 220) + '…' : s.text;
  document.body.appendChild(pop);
  placeSelPop(pop, s);
  pop.addEventListener('mousedown', ev => { if (!ev.target.closest('input')) ev.preventDefault(); });
  $('#sel-q', pop).addEventListener('keydown', ev => {
    if (keyIs(ev, 'chat.send') && !ev.isComposing) { ev.preventDefault(); ACTIONS['sel-send'](); }
    if (keyIs(ev, 'app.close')) hideSelPop();
  });
}
function placeSelPop(pop, s) {
  const w = pop.offsetWidth, h = pop.offsetHeight, vw = document.documentElement.clientWidth;
  const comp = $('.composer-wrap'), limit = comp ? comp.getBoundingClientRect().top : window.innerHeight;
  let top = s.last.bottom + 10, below = true;
  if (top + h > limit - 8) { top = s.first.top - h - 10; below = false; }
  const pane = $('.chat-main') ? $('.chat-main').getBoundingClientRect() : { left: 0, right: vw };
  const x = Math.min(Math.max(pane.left + 8, (below ? s.last.left + s.last.width / 2 : s.first.left + s.first.width / 2) - w / 2), Math.min(vw, pane.right) - w - 8);
  pop.style.left = Math.round(x) + 'px';
  pop.style.top = Math.round(Math.max(8, top)) + 'px';
  pop.classList.toggle('above', !below);
}
ACTIONS['sel-explain'] = () => {
  const p = $('#sel-pop'); if (!p) return;
  const text = p._text;
  hideSelPop();
  askAboutSelection(`Explain this:\n\n${quoteText(text)}`);
};
ACTIONS['sel-ask'] = () => {
  const p = $('#sel-pop'); if (!p) return;
  p.classList.add('asking');
  $('.sel-actions', p).classList.add('hidden');
  $('.sel-ask', p).classList.remove('hidden');
  const s = threadSelection();
  if (s) placeSelPop(p, s);
  $('#sel-q', p).focus();
};
ACTIONS['sel-send'] = () => {
  const p = $('#sel-pop'), q = $('#sel-q');
  if (!p || !q) return;
  if (!q.value.trim()) { q.focus(); p.classList.remove('shake'); void p.offsetWidth; p.classList.add('shake'); return; }
  const text = p._text, question = q.value.trim();
  hideSelPop();
  askAboutSelection(`${quoteText(text)}\n\n${question}`);
};
function askAboutSelection(content) {
  window.getSelection()?.removeAllRanges();
  if (currentStream()) { toast('Wait until the answer is finished.', 'bad'); return; }
  sendMessage(false, content).catch(e => toast(e.message, 'bad'));
}
const elOf = ev => ev.target instanceof Element ? ev.target : null;
document.addEventListener('mouseup', ev => {
  if (!$('#chat-thread') || elOf(ev)?.closest('.sel-pop')) return;
  setTimeout(showSelPop, 0);
});
document.addEventListener('keyup', ev => { if (ev.shiftKey && $('#chat-thread') && !elOf(ev)?.closest('input, textarea')) showSelPop(); });
document.addEventListener('mousedown', ev => { if (!elOf(ev)?.closest('.sel-pop')) hideSelPop(); });
document.addEventListener('keydown', ev => { if (keyIs(ev, 'app.close')) hideSelPop(); });
document.addEventListener('scroll', ev => {
  const p = $('#sel-pop');
  if (!p || !elOf(ev)?.closest('#chat-scroll')) return;
  const s = threadSelection();
  if (s) placeSelPop(p, s); else if (!p.classList.contains('asking')) hideSelPop();
}, true);

// ------------------------------------------------------------------ sending
/**
 * Sends the message in the box (or presetContent). regenerate answers the last question again; opts.editMessageId sends
 * an edited version of that question; opts.continue continues the last answer. What they replace is kept as a version.
 */
/** The open chat; a new chat is created on the server first (the first message, or a ! command in a new chat). */
async function ensureConv() {
  if (CHAT.conv) return CHAT.conv;
  const c = await post('/api/chat/conversations', { model: CHAT.model, settings: CHAT.settings, temporary: !!CHAT.temporary, projectId: CHAT.temporary ? undefined : CHAT.project || undefined });
  CHAT.conv = Object.assign(c, { messages: [] });
  rememberOpenChat();
  history.replaceState({}, '', '/chat/' + enc(c.id));
  S.params = Object.assign({}, S.params, { path: '/chat/' + c.id, args: [c.id] });
  $('#chat-head').innerHTML = headHtml();
  $('#chat-head').classList.add('arrive');
  $('#temp-slot').innerHTML = tempToggleHtml();
  if (!c.temporary) refreshList();
  const project = c.projectId && (CHAT.projects || []).find(p => p.id === c.projectId);
  if (project) { project.chats = (project.chats || 0) + 1; renderProjects(); }
  return CHAT.conv;
}
async function sendMessage(regenerate, presetContent, opts = {}) {
  if (currentStream()) return;
  if (CHAT.conv && CHAT.live.has(CHAT.conv.id)) { toast(`An answer is still being written in ${liveWhere(CHAT.conv.id)}. Stop it with ■ or wait for it.`, 'bad'); return; }
  const input = $('#chat-input');
  // Built-in slash commands (/goal, /plan, /model …) run here instead of being sent (chatcmds.js).
  if (!regenerate && !opts.continue && !opts.editMessageId && presetContent === undefined && typeof runChatCommand === 'function') {
    const typed = input.value.trim();
    if (typed.startsWith('/') && await runChatCommand(typed)) return;
  }
  // ! runs a shell command (its output goes into the chat); # saves a memory (chatnav.js).
  if (!regenerate && !opts.continue && !opts.editMessageId && presetContent === undefined && !CHAT.attachments.length) {
    const typed = input.value.trim();
    if (typed.length > 1 && typed.startsWith('!') && typeof runShellLine === 'function') { await runShellLine(typed.slice(1).trim()); return; }
    if (/^#(?!#)\s*\S/.test(typed) && typeof quickMemory === 'function') { await quickMemory(typed.slice(1).trim()); return; }
  }
  // The user acts: an automatic try-again that was waiting gives way (and the count starts over).
  if (CHAT.conv && !opts.auto) cancelAutoContinue(CHAT.conv.id, true);
  // "keep going" (or "continue", "try again" …) right after an answer failed part-way picks that answer up where it stopped,
  // like Try again, instead of a new turn that could start the plan over.
  if (!regenerate && !opts.continue && !opts.editMessageId && presetContent === undefined && CHAT.conv && !CHAT.attachments.length) {
    const msgs = CHAT.conv.messages;
    if (failedWithWork(msgs[msgs.length - 1]) && KEEP_GOING.test(input.value.trim())) {
      input.value = ''; autosize(); clearDraft(draftKey());
      return sendMessage(false, undefined, { continue: true });
    }
  }
  if (isExternal(CHAT.model) && window.chatClient && window.chatClient.runExternal) return sendExternal(regenerate, presetContent, opts);
  const draftAt = draftKey();
  const special = regenerate || opts.continue;
  // A long paste is a chip in the box ("[Pasted text #1 +120 lines]"): its text goes with the message.
  const typedText = typeof expandPastes === 'function' ? expandPastes(input.value.trim()) : input.value.trim();
  const content = special ? '' : (presetContent !== undefined ? presetContent : typedText);
  if (!special && !content && !CHAT.attachments.length && !opts.editMessageId) { const c = $('#composer'); c.classList.remove('shake'); void c.offsetWidth; c.classList.add('shake'); return; }
  if (!opts.editMessageId && $('#composer-files .pending')) { toast('Wait until the files have finished uploading.', 'bad'); return; }
  // UserPromptSubmit hooks on this computer (desktop app, VS Code) may stop a message (exit code 2) or add to it.
  if (!special && content && window.chatClient && window.chatClient.checkPrompt) {
    let check = null;
    try { check = await window.chatClient.checkPrompt({ content, chatId: CHAT.conv && CHAT.conv.id }); } catch { /* hooks never block chat by failing */ }
    if (check && check.blocked) { toast('Not sent: a UserPromptSubmit hook stopped it. ' + check.blocked, 'bad'); return; }
  }
  hideQuestion();
  // Dictation mode (voice.js) stops listening when the user sends some other way (typed, or an answer clicked).
  document.dispatchEvent(new CustomEvent('chat:sending', { detail: { convId: CHAT.conv && CHAT.conv.id } }));
  await ensureConv();
  const conv = CHAT.conv;
  const thread = $('#chat-thread');
  const welcome = $('.chat-welcome', thread);
  if (welcome) { welcome.classList.add('leaving'); setTimeout(() => welcome.remove(), reducedMotion() ? 0 : 220); }
  // Answering again replaces the last answer - unless there is none (it was lost): then nothing on screen goes.
  const lastSaved = conv.messages[conv.messages.length - 1];
  if (regenerate && lastSaved && lastSaved.role === 'assistant') { const last = $$('.msg.assistant', thread).pop(); if (last) { last.classList.add('leaving'); setTimeout(() => last.remove(), 200); } }
  // An edited question keeps its files; new attachments go with new messages only.
  const attachments = opts.editMessageId ? undefined : CHAT.attachments.map(a => a.id);
  if (presetContent === undefined && !special) { input.value = ''; autosize(); }
  if (!opts.editMessageId) { $('#composer-files').innerHTML = ''; CHAT.attachments = []; }
  if (presetContent === undefined && !special) { clearDraft(draftAt); clearDraft(draftKey()); }
  renderContext();
  const sendBtn = $('#send-btn'); if (sendBtn) bounce(sendBtn);
  CHAT.meta.lastModel = CHAT.model; LS.set('chat.model', CHAT.model);
  const view = createLiveView(conv, CHAT.model);
  const el = view.el;
  thread.appendChild(el);
  scrollThread(true);
  const ctrl = new AbortController();
  CHAT.streams.set(conv.id, { ctrl, el, view });
  CHAT.adopted.delete(conv.id);
  syncBusy();
  refreshListItemLive(conv.id);
  let finished = null, failure = null, asked = false, answered = false, serverError = false, stalled = false;
  // The server writes at least every 15 s (a ping when there is nothing else); a silent connection is a dead one.
  let lastByte = Date.now(), reader = null;
  const watchdog = setInterval(() => { if (reader && Date.now() - lastByte > 75000) { stalled = true; reader.cancel().catch(() => { }); } }, 5000);
  // Reads one response's events (the message's own stream, or a reconnect to it) until it ends.
  async function pump(res, onEnded) {
    reader = res.body.getReader();
    lastByte = Date.now();
    const dec = new TextDecoder();
    let buf = '';
    for (; ;) {
      const { value, done } = await reader.read();
      if (done) break;
      lastByte = Date.now();
      buf += dec.decode(value, { stream: true });
      let cut;
      while ((cut = buf.indexOf('\n\n')) >= 0) {
        const raw = buf.slice(0, cut); buf = buf.slice(cut + 2);
        const type = (raw.match(/^event: (.*)$/m) || [])[1], dataLine = (raw.match(/^data: (.*)$/m) || [])[1];
        if (!type || !dataLine) continue;
        let d; try { d = JSON.parse(dataLine); } catch { continue; }
        if ((type === 'delta' || type === 'reasoning') && d.text) growContext(conv.id, d.text.length);
        switch (type) {
          case 'context': liveContext(conv.id, d); break;
          case 'user': if (window.chatClient && window.chatClient.beginTurn) { try { window.chatClient.beginTurn({ chatId: conv.id, messageId: d.id }); } catch { /* the app's checkpoints are optional */ } }
            if (!regenerate && !conv.messages.some(m => m.id === d.id)) { conv.messages.push(d); const node = htmlNode(messageHtml(d, true)); el.before(node); applyRich(node); scrollThread(true); } break;
          case 'truncate': truncateFrom(conv, d.fromMessageId); break;
          case 'client_tool': runClientTool(conv, d); break;
          case 'approval': showApproval(conv.id, d); playSound('inputRequired'); break;
          case 'approval_resolved': hideApproval(d.id); break;
          case 'error': failure = d.message; serverError = true; break;
          case 'question': view.apply(type, d); asked = true; playSound('inputRequired'); break;
          case 'done': finished = d; if (d.versions) conv.versions = d.versions; finish(); if (!asked) playSound('chatComplete'); noteCharge(d.message); break;
          // A model-written title can follow the answer; the composer is already free by then.
          case 'title': setTitleAnimated(conv.id, d.title); refreshList(); break;
          // A reconnect found no answer to follow (it ended long ago, or the server restarted).
          case 'ended': if (onEnded) onEnded(d); break;
          case 'steers_left': if (typeof queuedFor === 'function') { queuedFor(conv.id).push(...(d.texts || [])); renderQueued(); } break;
          default: view.apply(type, d);
        }
      }
    }
  }
  try {
    let client = null;
    if (window.chatClient && window.chatClient.prepare) {
      // A desktop app that does not answer must not hold the message forever: send it without its tools after 20 s.
      const slow = new Promise(r => setTimeout(() => r('timeout'), 20000));
      try {
        const got = await Promise.race([window.chatClient.prepare({ content, conv, model: CHAT.model, regenerate: !!regenerate }), slow]);
        if (got === 'timeout') toast('The tools on this computer did not respond: sending without them.', 'warn'); else client = got;
      } catch (e) { console.warn('chat client', e); }
    }
    // Answering again or continuing: the code checkpoints go with the question already there.
    if ((regenerate || opts.continue) && window.chatClient && window.chatClient.beginTurn) {
      const lu = [...conv.messages].reverse().find(m => m.role === 'user');
      if (lu) { try { window.chatClient.beginTurn({ chatId: conv.id, messageId: lu.id }); } catch { /* optional */ } }
    }
    const res = await fetch(`/api/chat/conversations/${enc(conv.id)}/messages`, {
      method: 'POST', credentials: 'same-origin', signal: ctrl.signal,
      headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': S.csrf || '' },
      body: JSON.stringify({ content, model: CHAT.model, attachments, settings: CHAT.settings, regenerate: !!regenerate, clientId: CHAT.clientId,
        editMessageId: opts.editMessageId || undefined, continue: opts.continue ? true : undefined, plan: opts.plan ? true : undefined, research: opts.research ? true : undefined,
        device: client && client.device || undefined,
        clientTools: client && client.tools && client.tools.length ? client.tools : undefined, clientInstructions: client && client.instructions || undefined,
        // Permission rules from the computer's settings files (Claude Code's and Agentic's): merged with the account's for this message.
        clientRules: client && client.rules || undefined,
        clientAgents: client && client.agents && client.agents.length ? client.agents : undefined })
    });
    if (res.status === 409 && !regenerate && !opts.continue && !opts.editMessageId) {
      let code = ''; try { code = (await res.clone().json()).error.code; } catch { /* not JSON */ }
      if (code === 'ANSWERING') { joinRunningAnswer(conv, view, content); clearInterval(watchdog); return; }
    }
    if (!res.ok) { serverError = true; let msg = res.statusText; try { msg = (await res.json()).error.message; } catch { /* ignore */ } throw new Error(msg); }
    answered = true;
    await pump(res);
  } catch (e) { if (e.name !== 'AbortError') failure = e.message; }
  // The connection ended without the answer's end (network drop, proxy reconnect, a stalled stream): the answer keeps
  // running on the server, so reconnect to it (GET …/attach: what was missed, then the rest live). Only after every
  // attempt fails does the user see an error.
  let ended = false, attempt = 0;
  while (!finished && !serverError && !ctrl.signal.aborted && (answered || stalled) && !ended && attempt < RECONNECT_ATTEMPTS) {
    attempt++;
    const wait = Math.min(30000, 1000 * 2 ** attempt);
    view.apply('status', { text: `The connection dropped: reconnecting (attempt ${attempt} of ${RECONNECT_ATTEMPTS})…` });
    await new Promise(r => setTimeout(r, wait));
    if (ctrl.signal.aborted) break;
    try {
      const r = await fetch(`/api/chat/conversations/${enc(conv.id)}/attach`, { credentials: 'same-origin', signal: ctrl.signal });
      if (!r.ok) continue;
      stalled = false; failure = null;
      await pump(r, () => { ended = true; });
      if (!finished && !ended) attempt = Math.max(0, attempt - 1);   // it streamed again before dropping: a fresh drop, not a failed attempt
    } catch (e) { if (e.name === 'AbortError') break; }
  }
  clearInterval(watchdog);
  // Still no end: the answer is gone from the server (it restarted) or every reconnect failed - reload what was saved.
  const lost = !finished && !serverError && !ctrl.signal.aborted && (answered || stalled);
  if (lost) { recoverAnswer(conv, view, stalled); return; }
  finish();

  function finish() {
    if (view.closed) return;
    CHAT.streams.delete(conv.id);
    syncBusy();
    clearApprovals(conv.id);
    if (finished) AUTO_RETRIES.delete(conv.id);
    view.finish(finished && finished.message, failure, ctrl.signal.aborted);
    if (finished && finished.context && CHAT.conv && CHAT.conv.id === conv.id) updateContext(finished.context);
    if (finished && finished.title) setTitleAnimated(conv.id, finished.title);
    refreshList();
    if (typeof queueAfterAnswer === 'function') queueAfterAnswer(conv.id, ctrl.signal.aborted);
    if (typeof sendQueued === 'function' && !ctrl.signal.aborted) setTimeout(sendQueued, 80);
    if (finished && !failure && !ctrl.signal.aborted && typeof afterAnswerHook === 'function') afterAnswerHook(conv, finished.message);
    // Dictation mode (voice.js) speaks the answer's summary.
    document.dispatchEvent(new CustomEvent('chat:answered', { detail: { conv, message: finished && finished.message, failure, aborted: ctrl.signal.aborted } }));
    // A long session keeps only the latest messages on the page (scrolling up brings the others back).
    if (CHAT.conv && CHAT.conv.id === conv.id) setTimeout(trimThread, 400);
    // A failed answer with work in it is tried again by itself later; one that worked ends the automatic tries.
    if (finished && finished.message && !ctrl.signal.aborted) {
      if (failedWithWork(finished.message) && transientFailure(finished.message)) scheduleAutoContinue(conv, finished.message);
      else if (!finished.message.error) AUTO_CONTINUE.delete(conv.id);
    }
    // The desktop app and VS Code log the answer's tokens in Claude Code's format (for npx ccusage).
    if (finished && finished.message && window.chatClient && window.chatClient.recordUsage) {
      try { window.chatClient.recordUsage({ chatId: conv.id, message: finished.message, cachedTokens: finished.cachedTokens || 0 }); } catch { /* never in the way */ }
    }
  }
}
/**
 * A message to a model on this computer (Claude Code, through the desktop app). It answers there - with its own session,
 * tools and settings - and the exchange is then saved to the conversation, so history, the chat list, sharing and other
 * windows work as with any chat. Permission prompts and Claude's questions use this page's approval and question cards.
 */
async function sendExternal(regenerate, presetContent, opts = {}) {
  if (regenerate || opts.continue || opts.editMessageId) { toast(`${modelName(CHAT.model)} keeps its own session: send a new message instead of answering again, editing or continuing.`, 'bad'); return; }
  const input = $('#chat-input');
  const content = presetContent !== undefined ? presetContent : input.value.trim();
  if (!content && !CHAT.attachments.length) { const c = $('#composer'); c.classList.remove('shake'); void c.offsetWidth; c.classList.add('shake'); return; }
  if ($('#composer-files .pending')) { toast('Wait until the files have finished uploading.', 'bad'); return; }
  hideQuestion();
  const draftAt = draftKey();
  if (!CHAT.conv) await createConversationForSend();
  const conv = CHAT.conv, thread = $('#chat-thread');
  const welcome = $('.chat-welcome', thread);
  if (welcome) { welcome.classList.add('leaving'); setTimeout(() => welcome.remove(), reducedMotion() ? 0 : 220); }
  const atts = CHAT.attachments.slice();
  if (presetContent === undefined) { input.value = ''; autosize(); }
  $('#composer-files').innerHTML = ''; CHAT.attachments = [];
  if (presetContent === undefined) { clearDraft(draftAt); clearDraft(draftKey()); }
  const sendBtn = $('#send-btn'); if (sendBtn) bounce(sendBtn);
  $$('#composer .menu:not(.hidden)').forEach(m => m.classList.add('hidden'));   // an options menu would cover the approval card
  const history = conv.messages.slice(-40);
  const userMsg = { id: 'pending-' + Date.now(), role: 'user', content, attachments: atts.map(a => ({ uploadId: a.id, name: a.name, kind: a.kind })), createdUtc: new Date().toISOString() };
  conv.messages.push(userMsg);
  const userNode = htmlNode(messageHtml(userMsg, true)); thread.appendChild(userNode); applyRich(userNode);
  const view = createLiveView(conv, CHAT.model);
  thread.appendChild(view.el);
  scrollThread(true);
  const ctrl = new AbortController();
  CHAT.streams.set(conv.id, { ctrl, el: view.el, external: true, view });
  syncBusy(); refreshListItemLive(conv.id);
  let result = null, failure = null;
  try {
    result = await window.chatClient.runExternal({
      conv, content, attachments: atts, settings: CHAT.settings, model: CHAT.model, plan: !!opts.plan, signal: ctrl.signal, history,
      emit: (type, d) => view.apply(type, d),
      approve: req => askLocalApproval(conv.id, req),
      ask: questions => askLocalQuestions(conv.id, questions)
    });
  } catch (e) { failure = e && e.message || String(e); }
  const answer = result && result.answer ? result.answer : { content: view.live.content, reasoning: view.live.reasoning || null, toolCalls: view.live.toolCalls, finishReason: 'error' };
  if (failure && !answer.error) answer.error = failure;
  if (ctrl.signal.aborted && !answer.error) { answer.error = 'Stopped.'; answer.finishReason = 'stopped'; }
  if (result && result.settings) Object.assign(CHAT.settings, result.settings);
  let saved = null;
  if ((answer.content || '').trim() || (answer.toolCalls || []).length || answer.error) {
    try { saved = await post(`/api/chat/conversations/${enc(conv.id)}/external`, { model: CHAT.model, content, attachments: atts.map(a => a.id), settings: CHAT.settings, answer }); }
    catch (e) { toast('The answer could not be saved to this chat: ' + e.message, 'bad'); }
  }
  CHAT.streams.delete(conv.id);
  clearApprovals(conv.id);
  if (CHAT.ask && CHAT.ask.local) hideQuestion();
  syncBusy();
  if (saved) {
    const i = conv.messages.indexOf(userMsg); if (i >= 0) conv.messages[i] = saved.user;
    userNode.dataset.id = saved.user.id;
    view.finish(saved.message, null, false);
    if (saved.title) setTitleAnimated(conv.id, saved.title);
    if (!saved.message.error) playSound('chatComplete');
  } else view.finish(null, answer.error, ctrl.signal.aborted);
  clearListItemLive(conv.id);
  refreshList();
  document.dispatchEvent(new CustomEvent('chat:answered', { detail: { conv, message: saved && saved.message, failure: answer.error, aborted: ctrl.signal.aborted } }));
}
/** Creates the conversation for a first message (shared by server and Claude Code answers). */
async function createConversationForSend() {
  const c = await post('/api/chat/conversations', { model: CHAT.model, settings: CHAT.settings, temporary: !!CHAT.temporary, projectId: CHAT.temporary ? undefined : CHAT.project || undefined });
  CHAT.conv = Object.assign(c, { messages: [] });
  rememberOpenChat();
  history.replaceState({}, '', '/chat/' + enc(c.id));
  S.params = Object.assign({}, S.params, { path: '/chat/' + c.id, args: [c.id] });
  $('#chat-head').innerHTML = headHtml();
  $('#chat-head').classList.add('arrive');
  $('#temp-slot').innerHTML = tempToggleHtml();
  if (!c.temporary) refreshList();
  const project = c.projectId && (CHAT.projects || []).find(p => p.id === c.projectId);
  if (project) { project.chats = (project.chats || 0) + 1; renderProjects(); }
}
/** A permission prompt from Claude Code: the approval card; resolves to once | always | deny. */
function askLocalApproval(chatId, req) {
  return new Promise(resolve => {
    const id = 'local-' + req.requestId;
    CHAT.approvals.set(id, { id, chatId, name: req.toolName, arguments: JSON.stringify(req.input || {}), reason: req.reason || 'Claude Code asks first', where: 'Claude Code on this computer', resolve });
    renderApproval();
    refreshListItemLive(chatId, true);
    playSound('inputRequired');
    document.dispatchEvent(new CustomEvent('chat:approval', { detail: { id } }));
  });
}
/** Claude Code's questions (AskUserQuestion): the question card; resolves to the answers, or null when skipped. */
function askLocalQuestions(chatId, questions) {
  return new Promise(resolve => {
    if (!CHAT.conv || CHAT.conv.id !== chatId) { resolve(null); return; }
    CHAT.ask = { questions, answers: [], step: 0, convId: chatId, local: true, onDone: resolve };
    renderQuestion();
    playSound('inputRequired');
  });
}

/**
 * This window's answer stream broke before the answer ended. Follow the answer again if the server is still writing it
 * (a chat with a goal keeps running), otherwise show what the server saved, so the chat never looks busy or finished
 * when it is neither.
 */
/** How often a dropped answer stream is reconnected (waits 2, 4, 8, 16 then 30 s) before the user is told. */
const RECONNECT_ATTEMPTS = 8;
/** Answers asked again by themselves after being lost, per chat (reset when an answer arrives). */
const AUTO_RETRIES = new Map();
/**
 * The message could not start an answer: one is already being written in this chat (another window, the CLI, the goal or a
 * monitor started it, and this window had not heard yet). The message goes to that answer, and this window follows it.
 */
function joinRunningAnswer(conv, view, content) {
  CHAT.streams.delete(conv.id);
  view.closed = true;
  view.el.remove();
  CHAT.live.set(conv.id, CHAT.live.get(conv.id) || { chatId: conv.id });
  syncBusy();
  refreshListItemLive(conv.id);
  if (content && typeof steerOrQueue === 'function') steerOrQueue(content);
  if (CHAT.conv && CHAT.conv.id === conv.id) attachToLiveRun(conv, true);
}
async function recoverAnswer(conv, view, stalled) {
  CHAT.streams.delete(conv.id);
  view.closed = true;
  view.el.remove();
  syncBusy();
  // The server may be restarting: keep asking for a while before giving up.
  let snap = null;
  for (let i = 0; i < 6 && !snap; i++) {
    try { snap = await get(`/api/chat/conversations/${enc(conv.id)}/live`); }
    catch { await new Promise(r => setTimeout(r, Math.min(30000, 2000 * 2 ** i))); }
  }
  if (snap && snap.active) {
    CHAT.adopted.add(conv.id);
    CHAT.live.set(conv.id, { chatId: conv.id, clientId: snap.clientId, auto: snap.auto });
    refreshListItemLive(conv.id);
    if (CHAT.conv && CHAT.conv.id === conv.id) { await resyncConversation(); attachToLiveRun(conv, true); }
    toast(stalled ? 'The answer stopped arriving: following it again.' : 'The connection dropped: following the answer again.', 'warn');
    return;
  }
  clearListItemLive(conv.id);
  if (CHAT.conv && CHAT.conv.id === conv.id) await resyncConversation();
  const last = CHAT.conv && CHAT.conv.id === conv.id ? CHAT.conv.messages[CHAT.conv.messages.length - 1] : null;
  if (!snap) toast('Lost the connection to the server. Reload the chat when it is back.', 'bad');
  else if (!last || last.role !== 'assistant') {
    // The answer was lost (the server restarted mid-answer): ask again by itself, a few times, before leaving it to the user.
    const n = (AUTO_RETRIES.get(conv.id) || 0) + 1;
    if (n <= 3 && CHAT.conv && CHAT.conv.id === conv.id) {
      AUTO_RETRIES.set(conv.id, n);
      toast(`The answer was lost before it was saved: answering again (attempt ${n} of 3)…`, 'warn');
      refreshList();
      sendMessage(true).catch(e => toast(e.message, 'bad'));
      return;
    }
    toast('The answer was lost several times. Type /retry to answer it again.', 'bad');
  }
  refreshList();
}
/** A tool the model asked to run on the user's computer: the host app runs it and the answer continues with its result. */
async function runClientTool(conv, call) {
  let r;
  try { r = window.chatClient && window.chatClient.runTool ? await window.chatClient.runTool(call, conv) : { ok: false, result: 'This app cannot run tools on the user\'s computer.' }; }
  catch (e) { r = { ok: false, result: e.message || String(e) }; }
  // The answer waits for this result: a lost request would leave it waiting until it times out, so try a few times.
  for (let attempt = 1; attempt <= 4; attempt++) {
    try { await post(`/api/chat/conversations/${enc(conv.id)}/tool-results`, { id: call.id, ok: !!(r && r.ok), result: String((r && r.result) ?? ''), image: r && r.image || undefined, images: r && r.images || undefined }); return; }
    catch (e) { if (e.status === 404 || attempt === 4) { console.warn('tool result not delivered', e); return; } await new Promise(res => setTimeout(res, 1500 * attempt)); }
  }
}
function refreshListItemLive(convId, needsYou) {
  const item = $(`#chat-list .chat-item[data-id="${CSS.escape(convId)}"]`);
  if (!item) return;
  if (!$('.item-live', item)) item.insertAdjacentHTML('beforeend', '<span class="item-live" data-tip="Answering…"></span>');
  item.classList.toggle('needs-you', !!needsYou);
}
function clearListItemLive(convId) {
  const item = $(`#chat-list .chat-item[data-id="${CSS.escape(convId)}"]`);
  if (!item) return;
  $('.item-live', item)?.remove();
  item.classList.remove('needs-you');
}
function htmlNode(html) { const t = document.createElement('template'); t.innerHTML = html.trim(); return t.content.firstElementChild; }
/** An edited question or a regenerated answer: that message and everything after it leave the thread (the server kept them as a version). */
function truncateFrom(conv, id) {
  const i = conv.messages.findIndex(m => m.id === id);
  if (i >= 0) conv.messages.splice(i);
  if (!CHAT.conv || CHAT.conv.id !== conv.id) return;
  let node = $(`#chat-thread .msg[data-id="${CSS.escape(id)}"]`);
  while (node && !node.classList.contains('streaming')) { const next = node.nextElementSibling; node.remove(); node = next; }
}
/** Typed while the chat is answered: a /command (/goal, /model …) runs here; other text goes to the answer, or waits for its end. */
async function sendWhileAnswering(text) {
  if (text.trim().startsWith('/') && typeof runChatCommand === 'function' && await runChatCommand(text)) return;
  if (typeof steerOrQueue === 'function') steerOrQueue(typeof expandPastes === 'function' ? expandPastes(text) : text);
}
/** Ends the answer on the server too (a proxy in between may not pass on that the request was cancelled). */
function stopOnServer(chatId) { CHAT.stoppedHere = chatId; return post('/api/chat/conversations/' + enc(chatId) + '/stop').catch(() => { }); }
function stopStreaming() { const s = currentStream(); if (s) { s.ctrl.abort(); stopOnServer(CHAT.conv.id); } }
ACTIONS['chat-send'] = () => {
  const typed = (($('#chat-input') || {}).value || '').trim();
  if (answering() && typed) sendWhileAnswering(typed);
  else if (currentStream()) stopStreaming();
  else if (CHAT.conv && CHAT.live.has(CHAT.conv.id)) { toast(`Stopping the answer in ${liveWhere(CHAT.conv.id)}…`, 'ok'); stopOnServer(CHAT.conv.id); }
  else sendMessage().catch(e => toast(e.message, 'bad'));
};

// ------------------------------------------------------------------ live: every window of this user (web app, desktop app)
/**
 * One event stream per window: answers written in other windows or the desktop app, approvals waiting for an answer,
 * and desktop apps coming and going. Other scripts (the desktop layer) get every event as a "chathub" DOM event.
 */
function ensureChatHub() {
  if (CHAT.hub) return CHAT.hub;
  if (IN_PANE) { try { if (window.parent.chatHubSnapshot) return joinParentHub(window.parent); } catch { /* not the Chat page around it */ } }
  const q = window.chatClient && window.chatClient.hubQuery ? window.chatClient.hubQuery() : '';
  const es = new EventSource('/api/chat/stream' + q);
  CHAT.hub = es;
  // The browser reconnects by itself after a network error, but gives up for good after an error response (a proxy's 502
  // while the server restarts): then this window would never hear of answers ending. Open a new stream instead.
  es.addEventListener('open', () => { CHAT.hubRetry = 0; });
  es.addEventListener('error', () => {
    if (es.readyState !== EventSource.CLOSED || CHAT.hub !== es) return;
    CHAT.hub = null;
    const wait = Math.min(30000, 2000 * Math.pow(2, CHAT.hubRetry = (CHAT.hubRetry || 0) + 1));
    setTimeout(() => { if (!CHAT.hub) ensureChatHub(); }, wait);
  });
  es.addEventListener('hello', ev => {
    let d; try { d = JSON.parse(ev.data); } catch { return; }
    CHAT.hello = d;
    onHubHello(d);
    document.dispatchEvent(new CustomEvent('chathub-hello', { detail: d }));
  });
  es.addEventListener('chat', ev => {
    let m; try { m = JSON.parse(ev.data); } catch { return; }
    document.dispatchEvent(new CustomEvent('chathub', { detail: m }));
    onHubEvent(m);
  });
  return es;
}
window.ensureChatHub = ensureChatHub;
// Back online or shown again after the stream gave up: reconnect now instead of after the wait.
window.addEventListener('online', () => { if (!CHAT.hub && $('.chat-app')) ensureChatHub(); });
document.addEventListener('visibilitychange', () => { if (!document.hidden && !CHAT.hub && $('.chat-app')) ensureChatHub(); });
function onHubHello(d) {
  CHAT.live.clear();
  (d.runs || []).forEach(r => { if (r.clientId !== CHAT.clientId || CHAT.adopted.has(r.chatId)) CHAT.live.set(r.chatId, r); });
  CHAT.adopted.forEach(id => { if (!CHAT.live.has(id)) CHAT.adopted.delete(id); });
  // An answer followed here that ended while the stream was down: its end was missed, so close it and show what was saved.
  if (CHAT.watch && !CHAT.watch.closed && CHAT.conv && !CHAT.live.has(CHAT.conv.id)) { CHAT.watch.finish(null, null, false); CHAT.watch = null; resyncConversation(); }
  (d.devices || []).forEach(x => CHAT.devices.set(x.id, x));
  // Events missed while disconnected: questions answered and approvals settled elsewhere.
  if (CHAT.ask && CHAT.live.has(CHAT.ask.convId)) hideQuestion();
  CHAT.approvals.forEach((a, id) => { if (!(d.runs || []).some(r => r.chatId === a.chatId && r.approvals > 0)) CHAT.approvals.delete(id); });
  renderApproval();
  syncBusy();
  renderDeviceChip();
  // The open chat may be answered elsewhere right now (the page asked before the stream said so).
  if (CHAT.conv && CHAT.live.has(CHAT.conv.id) && !CHAT.watch && $('.chat-app')) attachToLiveRun(CHAT.conv);
}
/** What a multitask pane needs to know when it joins this page's event stream: the answers running now and the desktop apps. */
window.chatHubSnapshot = () => ({
  runs: [...CHAT.live.values(), ...[...CHAT.streams.keys()].map(id => ({ chatId: id, clientId: CHAT.clientId }))],
  devices: [...CHAT.devices.values()]
});
/**
 * A multitask pane shares the Chat page's event stream instead of opening its own: four panes with a stream each, plus
 * their answers, would use up the connections a browser allows per server (six over HTTP/1.1, as in the desktop app).
 * Tool calls for this computer are left to the page around it, so they run once.
 */
function joinParentHub(host) {
  host.ensureChatHub();
  const onChat = ev => { const m = ev.detail; if (!m) return; if (m.type !== 'client_tool') document.dispatchEvent(new CustomEvent('chathub', { detail: m })); onHubEvent(m); };
  const onHello = ev => { if (ev.detail) onHubHello(ev.detail); };
  host.document.addEventListener('chathub', onChat);
  host.document.addEventListener('chathub-hello', onHello);
  window.addEventListener('pagehide', () => { try { host.document.removeEventListener('chathub', onChat); host.document.removeEventListener('chathub-hello', onHello); } catch { /* gone */ } });
  CHAT.hub = { shared: true, close() { } };
  onHubHello(host.chatHubSnapshot());
  return CHAT.hub;
}

/** The open chat is being answered: in this window, or elsewhere (the CLI, the desktop app, another tab). */
function answering() { return !!(currentStream() || (CHAT.conv && CHAT.live.has(CHAT.conv.id))); }
function onHubEvent(m) {
  const { chatId, type, data } = m;
  if (type === 'device') {
    CHAT.devices.set(data.id, Object.assign(CHAT.devices.get(data.id) || {}, data));
    renderDeviceChip();
    if ($('#tools-menu') && !$('#tools-menu').classList.contains('hidden')) { renderToolsMenu(); loadCatalogs(); }
    return;
  }
  if (type === 'deleted') { removeChatsFromList((data.ids || []).filter(id => !CHAT.streams.has(id))); return; }
  // A client's subagent (the CLI's Task) streams into its card only through here, also in the window that asked.
  if (type === 'subagent' && data && data.relayed) { const own = CHAT.streams.get(chatId); if (own && own.view) { own.view.apply('subagent', data); return; } }
  else if (m.clientId && m.clientId === CHAT.clientId && !CHAT.adopted.has(chatId)) return; // this window's own answer: it has its own stream
  if (type === 'run' && data.phase !== 'start') CHAT.adopted.delete(chatId);
  const open = CHAT.conv && CHAT.conv.id === chatId && $('.chat-app');
  // A new message in this chat from another window answers the question shown here.
  if (((type === 'run' && data.phase === 'start') || type === 'user') && CHAT.ask && CHAT.ask.convId === chatId) hideQuestion();
  if (type === 'run') {
    if (data.phase === 'start') {
      CHAT.live.set(chatId, { chatId, device: data.device, auto: data.auto }); refreshListItemLive(chatId);
      // An app (the CLI, the desktop app) works in the open chat with its tools: the chat now uses it, so messages sent from
      // here run its tools too (the server remembers the same).
      if (open && data.device && CHAT.settings && CHAT.settings.device !== data.device) { CHAT.settings.device = data.device; if (CHAT.conv) CHAT.conv.settings = Object.assign({}, CHAT.conv.settings, { device: data.device }); renderDeviceChip(); }
    }
    else {
      CHAT.live.delete(chatId); clearListItemLive(chatId); clearApprovals(chatId);
      if (CHAT.steers && CHAT.steers.delete(chatId) && open && typeof renderQueued === 'function') renderQueued();
      if (open && CHAT.watch && !CHAT.watch.closed) { CHAT.watch.finish(null, null, false); CHAT.watch = null; resyncConversation(); }
      refreshList();
      if (open && typeof sendQueued === 'function') setTimeout(sendQueued, 400);
    }
    if (open) syncBusy();
    return;
  }
  if (type === 'approval') {
    refreshListItemLive(chatId, true);
    // In multitask mode the page around the panes plays the sound and names chats that are not in a pane.
    if (!IN_PANE) playSound('inputRequired');
    if (open) showApproval(chatId, data);
    else if (!IN_PANE && !(typeof mtHasChat === 'function' && mtHasChat(chatId))) toast(`A chat needs your approval: ${(CHAT.list.find(c => c.id === chatId) || {}).title || 'open Chat'}`, 'warn');
    document.dispatchEvent(new CustomEvent('chat:approval', { detail: data }));
    return;
  }
  if (type === 'approval_resolved') { hideApproval(data.id); refreshListItemLive(chatId, false); return; }
  // The goal's state (met, paused, resuming by itself): the goal bar, not a live answer.
  if (type === 'goal') { if (typeof onGoalEvent === 'function') onGoalEvent(chatId, data); return; }
  if (type === 'title') { setTitleAnimated(chatId, data.title); return; }
  // The context meter follows an answer written elsewhere too.
  if (type === 'context') { liveContext(chatId, data); return; }
  if ((type === 'delta' || type === 'reasoning') && data && data.text) growContext(chatId, data.text.length);
  if (type === 'compacted' && open && !CHAT.watch) { resyncConversation(); return; }
  // A stopped answer was saved: what it wrote replaces the text kept on screen (or comes back if a reload missed it).
  // Messages typed for it that it never read go back into the box of the window that stopped it.
  if (type === 'changed' && data && data.stopped && data.message) {
    if (open) settleStopped(data.message);
    if (open && (data.steers || []).length && CHAT.stoppedHere === chatId && typeof backToInput === 'function') backToInput(data.steers);
    if (CHAT.stoppedHere === chatId) CHAT.stoppedHere = null;
    return;
  }
  // Messages waiting for the model's next step changed (added, read, or taken back in any window).
  if (type === 'steers') { if (CHAT.steers) CHAT.steers.set(chatId, (data && data.items) || []); if (open && typeof renderQueued === 'function') renderQueued(); return; }
  // Another window switched to another version of a message - or an agent posted its work into the chat.
  if (type === 'changed') {
    const report = data && data.agentReport;
    if (open && !CHAT.watch && !CHAT.streams.has(chatId)) {
      const done = resyncConversation();
      if (report) Promise.resolve(done).then(markReportArrived);
    } else if (report && !IN_PANE) {
      toast(`${data.agentId && data.agentId !== 'global' ? data.agentId : 'Your agents'} posted in “${data.title || 'a chat'}”`, 'ok');
    }
    if (report) refreshList();
    return;
  }
  if (type === 'truncate') { if (open) truncateFrom(CHAT.conv, data.fromMessageId); return; }
  // The answer is complete at 'done' (the run itself may still be naming the chat).
  if (type === 'done') { CHAT.live.delete(chatId); clearListItemLive(chatId); }
  if (!open) return;
  // Follow another window's answer in this chat - never one this window writes itself (it shows it already).
  if (CHAT.streams.has(chatId)) return;
  if (!CHAT.watch || CHAT.watch.closed) {
    if (type === 'done' || type === 'error') { resyncConversation(); return; }
    startWatch(chatId);
  }
  const w = CHAT.watch;
  if (!w) return;
  if (type === 'user') {
    if (!CHAT.conv.messages.some(x => x.id === data.id)) { CHAT.conv.messages.push(data); const node = htmlNode(messageHtml(data, true)); w.el.before(node); applyRich(node); scrollThread(); }
    return;
  }
  if (type === 'done') {
    if (data.versions) CHAT.conv.versions = data.versions;
    w.finish(data.message, null, false); CHAT.watch = null; if (data.context) updateContext(data.context); syncBusy();
    document.dispatchEvent(new CustomEvent('chat:answered', { detail: { conv: CHAT.conv, message: data.message } }));
    return;
  }
  if (type === 'error') { w.live.error = data.message; return; }
  if (type !== 'client_tool') w.apply(type, data);
}
function startWatch(chatId, snapshot) {
  const thread = $('#chat-thread');
  if (!thread) return null;
  $('.chat-welcome', thread)?.remove();
  const r = CHAT.live.get(chatId) || {};
  const w = createLiveView(CHAT.conv, (snapshot && snapshot.model) || CHAT.model, { remote: liveBadge(chatId) });
  thread.appendChild(w.el);
  CHAT.watch = w;
  if (snapshot) w.restore(snapshot);
  scrollThread(true);
  syncBusy();
  return w;
}
/** Opening a chat while it is being answered elsewhere: show what is written so far, then follow it live. */
async function attachToLiveRun(conv, force) {
  if (!conv || !CHAT.live.has(conv.id) || CHAT.streams.has(conv.id)) return;
  try {
    const snap = await get(`/api/chat/conversations/${enc(conv.id)}/live`);
    if (!CHAT.conv || CHAT.conv.id !== conv.id) return;
    // Over already (its end may have been missed): show what was saved instead of a bubble that waits forever.
    if (!snap.active) { CHAT.live.delete(conv.id); CHAT.adopted.delete(conv.id); clearListItemLive(conv.id); syncBusy(); resyncConversation(); return; }
    // Messages already waiting for its next step (typed in another window, or before a reload).
    get(`/api/chat/conversations/${enc(conv.id)}/steers`).then(r => { if (CHAT.steers) CHAT.steers.set(conv.id, r.items || []); if (CHAT.conv && CHAT.conv.id === conv.id && typeof renderQueued === 'function') renderQueued(); }).catch(() => { });
    if (!force && snap.clientId && snap.clientId === CHAT.clientId && !CHAT.adopted.has(conv.id)) return;
    if (CHAT.watch && !CHAT.watch.closed) return;
    startWatch(conv.id, snap);
    (snap.approvals || []).forEach(a => showApproval(conv.id, a));
  } catch { /* the run may have just ended */ }
}
/** An agent's report just arrived in the open chat: it rises in and its avatar rings, and the chat follows it if at the end. */
function markReportArrived() {
  const reports = $$('#chat-thread .msg.agent-report');
  const last = reports[reports.length - 1];
  if (!last || reducedMotion()) return;
  last.classList.add('arrived');
  setTimeout(() => last.classList.remove('arrived'), 3200);
  if (CHAT.stick) scrollThread(true);
}
/**
 * The server saved an answer that was stopped. It takes the place of the text kept on screen for it; when that is gone
 * (the chat was reloaded before the save), it goes back in its place by time - or the chat reloads when nothing is running.
 */
function settleStopped(message) {
  const conv = CHAT.conv;
  if (!conv || !message || !message.id) return;
  const list = conv.messages || (conv.messages = []);
  const i = list.findIndex(m => m.id === message.id);
  // Followed live from here: its end reloads the chat, which then has it.
  if (i < 0 && CHAT.watch && !CHAT.watch.closed) return;
  if (i < 0 && !CHAT.streams.has(conv.id)) { resyncConversation(); return; }
  if (!CHAT.streams.has(conv.id)) loadContext();   // its count is saved with it (no "done" brought it here)
  if (i >= 0) list[i] = message;
  else {
    const at = list.findIndex(m => m.createdUtc && message.createdUtc && m.createdUtc > message.createdUtc);
    if (at < 0) list.push(message); else list.splice(at, 0, message);
  }
  const old = $(`#chat-thread .msg[data-id="${CSS.escape(message.id)}"]:not(.streaming)`);
  const node = htmlNode(messageHtml(message));
  node.classList.add('settled');
  if (old) { old.replaceWith(node); applyRich(node); return; }
  const next = list[list.indexOf(message) + 1];
  const before = next && $(`#chat-thread .msg[data-id="${CSS.escape(next.id)}"]`) || $('#chat-thread .msg.streaming');
  if (before) { before.before(node); applyRich(node); }
}
/** Reloads the open chat's messages (after another window's answer ended or the chat was compacted). */
async function resyncConversation() {
  if (!CHAT.conv) return;
  const id = CHAT.conv.id;
  const fresh = await get('/api/chat/conversations/' + enc(id)).catch(() => null);
  if (!fresh || !CHAT.conv || CHAT.conv.id !== id || CHAT.streams.has(id) || CHAT.watch) return;
  if (!Array.isArray(fresh.messages)) return;
  if (fresh.messages.length !== (CHAT.conv.messages || []).length) hideQuestion(true);
  const drawnFrom = CHAT.renderedFrom;
  CHAT.conv = Object.assign(fresh, { settings: CHAT.conv.settings });
  const thread = $('#chat-thread');
  thread.innerHTML = threadHtml(drawnFrom);
  applyRich(thread);
  watchEarlierBar();
  markCompacted();
  scrollThread(true);
  showPendingQuestion();
  loadContext();
  syncBusy();
}

// ------------------------------------------------------------------ tool approvals (any window can answer)
function argsOf(s) { try { return JSON.parse(s || '{}'); } catch { return {}; } }
function approvalDetail(a) {
  const args = argsOf(a.arguments), name = a.name || '';
  // An edit or an overwrite with several changes: each one has a checkbox, so some can be left out (chatnav.js).
  const diffHtml = (o, n) => typeof approvalEditDiff === 'function' && name !== 'MultiEdit' ? approvalEditDiff(a, o, n) : window.diffHtml(o, n);
  if (name === 'run_command') return `<pre class="appr-code">${esc(args.command || '')}</pre>${args.cwd ? `<div class="muted small">in ${esc(args.cwd)}</div>` : ''}`;
  // Overwriting a file: the change as a diff when this window's app could read the file as it is now (approvalBefore).
  if ((name === 'write_file' || name === 'Write') && typeof a.before === 'string') return `<div class="appr-path">${esc(args.path || args.file_path || '')}</div>${diffHtml(a.before, String(args.content || ''))}`;
  if (name === 'write_file') return `<div class="appr-path">${esc(args.path || '')}</div><pre class="appr-code">${esc(String(args.content || '').split('\n').slice(0, 60).join('\n'))}</pre>`;
  if (name === 'edit_file') return `<div class="appr-path">${esc(args.path || '')}</div>${diffHtml(args.old_string || '', args.new_string || '')}`;
  if (name === 'agentic_api') return `<pre class="appr-code">${esc(String(args.method || '').toUpperCase())} ${esc(args.path || '')}${args.body ? '\n\n' + jsonColor(JSON.stringify(args.body, null, 2)) : ''}</pre>`;
  if (name === 'run_code') return `<pre class="appr-code">${esc(args.code || '')}</pre>`;
  if (name === 'take_screenshot') return `<div class="appr-shot"><svg viewBox="0 0 24 24" class="ic"><path d="M3 5h18v12H3zM8 21h8M12 17v4"/></svg><div><b>The model wants to see ${args.area === 'all' ? 'all your monitors' : 'your screen'}.</b>${args.reason ? `<div>${esc(args.reason)}</div>` : ''}<div class="muted small">Agentic steps aside while the picture is taken. Close anything private first; the picture is shown with the answer.</div></div></div>`;
  // Claude Code's tools
  if (name === 'Bash' || name === 'PowerShell') return `<pre class="appr-code">${esc(args.command || '')}</pre>${args.description ? `<div class="muted small">${esc(args.description)}</div>` : ''}`;
  if (name === 'Write') return `<div class="appr-path">${esc(args.file_path || '')}</div><pre class="appr-code">${esc(String(args.content || '').split('\n').slice(0, 60).join('\n'))}</pre>`;
  if (name === 'Edit') return `<div class="appr-path">${esc(args.file_path || '')}</div>${diffHtml(args.old_string || '', args.new_string || '')}`;
  if (name === 'MultiEdit') return `<div class="appr-path">${esc(args.file_path || '')}</div>${(args.edits || []).slice(0, 6).map(e => diffHtml(e.old_string || '', e.new_string || '')).join('')}`;
  if (name === 'WebFetch') return `<div class="appr-path">${esc(args.url || '')}</div>${args.prompt ? `<div class="muted small">${esc(args.prompt)}</div>` : ''}`;
  return toolArgsHtml(args) || '<div class="muted small">No details.</div>';
}
function showApproval(chatId, a) {
  const entry = Object.assign({ chatId }, a);
  CHAT.approvals.set(a.id, entry);
  markApprovalOnCard(a.id, true);
  renderApproval();
  // The tools run on this window's computer (desktop app, VS Code) when this window sent the message: its hooks and files.
  if (a.server === 'client' && CHAT.streams.has(chatId) && window.chatClient) {
    approvalHooks(entry);
    approvalBefore(entry);
  }
}
/**
 * PermissionRequest hooks on this computer answer the question first (Claude Code's hookSpecificOutput.decision.behavior
 * allow | deny); Notification hooks hear that the chat waits for the user.
 */
async function approvalHooks(a) {
  const cc = window.chatClient;
  clientHook(a.chatId, 'Notification', { message: `Agentic needs your permission to use ${a.name}`, notification_type: 'permission_prompt' });
  if (!cc.permissionRequest) return;
  let r = null;
  try { r = await cc.permissionRequest({ chatId: a.chatId, id: a.id, name: a.name, arguments: a.arguments, reason: a.reason }); } catch { return; }
  if (!r || (r.decision !== 'allow' && r.decision !== 'deny') || !CHAT.approvals.has(a.id)) return;
  try { await post(`/api/chat/conversations/${enc(a.chatId)}/approvals`, { id: a.id, choice: r.decision === 'allow' ? 'once' : 'deny' }); } catch { return; }
  hideApproval(a.id);
  toast(`${r.decision === 'allow' ? 'Allowed' : 'Denied'} by a PermissionRequest hook${r.message ? ': ' + r.message : ''}`, r.decision === 'allow' ? 'ok' : 'warn');
}
/** Hooks on this computer (desktop app, VS Code) for what happens in an answer this window runs (Claude Code's hook events). */
function clientHook(chatId, event, payload) {
  const cc = window.chatClient;
  if (!cc || !cc.hookEvent || !CHAT.streams.has(chatId)) return;
  Promise.resolve().then(() => cc.hookEvent({ event, chatId, payload: payload || {} })).catch(() => { });
}
/** Overwriting a file on this computer: its text as it is now, so the approval card shows the change as a diff. */
async function approvalBefore(a) {
  const cc = window.chatClient, args = argsOf(a.arguments), path = a.name === 'write_file' ? args.path : a.name === 'Write' ? args.file_path : null;
  if (!path || !cc.fileText) return;
  let r = null;
  try { r = await cc.fileText(path); } catch { return; }
  if (!r || typeof r.text !== 'string' || !CHAT.approvals.has(a.id)) return;
  a.before = r.text;
  renderApproval();
}
function hideApproval(id) { markApprovalOnCard(id, false); if (CHAT.approvals.delete(id)) renderApproval(); }
function clearApprovals(chatId) { let changed = false; CHAT.approvals.forEach((a, id) => { if (a.chatId === chatId) { CHAT.approvals.delete(id); changed = true; } }); if (changed) renderApproval(); }
function renderApproval() {
  const pop = $('#approve-pop');
  if (!pop) return;
  const a = [...CHAT.approvals.values()].find(x => CHAT.conv && x.chatId === CHAT.conv.id);
  if (!a) { if (!pop.classList.contains('hidden')) { pop.classList.add('closing'); setTimeout(() => { pop.classList.add('hidden'); pop.classList.remove('closing'); pop.innerHTML = ''; }, reducedMotion() ? 0 : 180); } return; }
  const where = a.where || (a.server === 'client' ? (CHAT.live.get(a.chatId) && liveWhere(a.chatId)) || 'your computer' : 'the Agentic server');
  const reason = String(a.reason || 'it needs your approval').replace(/^it is the first time this chat uses \S+$/, 'it is the first time this chat uses this tool');
  pop.innerHTML = `<div class="appr-card" data-id="${esc(a.id)}"><div class="appr-head"><span class="appr-icon"><svg viewBox="0 0 24 24" class="ic"><path d="M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z"/></svg></span>
      <div class="grow"><div class="appr-title">${approvalTitleHtml(a)}</div><div class="muted small">Asked because ${esc(reason)} · runs on ${esc(where)} · <code>${esc(a.name)}</code></div></div>
      ${CHAT.approvals.size > 1 ? `<span class="badge warn">${CHAT.approvals.size} waiting</span>` : ''}</div>
    <div class="appr-body">${approvalDetail(a)}</div>
    <div class="appr-actions"><button class="btn danger" data-act="chat-approve" data-choice="deny" data-tip="Key 3 or Esc">Deny <kbd>3</kbd></button><span class="spacer"></span>
      <button class="btn" data-act="chat-approve" data-choice="always" data-tip="${a.alwaysScope ? esc(`Key 2 · later ${a.alwaysScope} commands in this chat run without asking; other commands still ask`) : 'Key 2'}">${a.alwaysScope ? `Always allow <code>${esc(a.alwaysScope.length > 40 ? a.alwaysScope.slice(0, 40) + '…' : a.alwaysScope)}</code> here` : 'Always allow in this chat'} <kbd>2</kbd></button><button class="btn primary" data-act="chat-approve" data-choice="once" data-tip="Key 1">Allow once <kbd>1</kbd></button></div></div>`;
  pop.classList.remove('hidden');
}
// Keys for the approval card (like Claude Code): 1 allows once, 2 always in this chat, 3 or Esc denies - unless you are typing.
document.addEventListener('keydown', ev => {
  const pop = $('#approve-pop');
  if (!pop || pop.classList.contains('hidden') || ev.ctrlKey || ev.metaKey || ev.altKey) return;
  const field = ev.target && ev.target.closest && ev.target.closest('input, textarea, [contenteditable="true"]');
  if (field && String(field.value || field.textContent || '').trim()) return;
  const choice = { 1: 'once', 2: 'always', 3: 'deny', Escape: 'deny' }[ev.key];
  if (!choice) return;
  const btn = $(`.appr-card:not(.answered) [data-act="chat-approve"][data-choice="${choice}"]`, pop);
  if (!btn) return;
  ev.preventDefault();
  ev.stopPropagation();
  btn.click();
}, true);
ACTIONS['chat-approve'] = async el => {
  const card = el.closest('.appr-card'), id = card.dataset.id, a = CHAT.approvals.get(id);
  if (!a) return;
  // Some changes of an edit left out (their checkboxes cleared): the edit runs with the rest; none left: it is denied.
  let choice = el.dataset.choice, edited = null;
  if (choice !== 'deny' && typeof editedApprovalArgs === 'function') {
    const e = editedApprovalArgs(a);
    if (e && e.none) choice = 'deny'; else if (e && e.args) edited = e.args;
  }
  card.classList.add('answered');
  if (a.resolve) { a.resolve(choice, edited); hideApproval(id); refreshListItemLive(a.chatId, false); return; }
  try { await post(`/api/chat/conversations/${enc(a.chatId)}/approvals`, { id, choice, arguments: edited ? JSON.stringify(edited) : undefined }); }
  catch (e) { toast(e.message, 'bad'); }
  if (edited) toast('Allowed with some changes left out', 'ok');
  hideApproval(id);
};

// ------------------------------------------------------------------ reasoning effort and permission mode
const EFFORTS = [['none', 'None', 'Answer right away'], ['low', 'Low', 'Quick thinking'], ['medium', 'Medium', 'Balanced — the default'],
  ['high', 'High', 'Think longer about hard problems'], ['ultracode', 'Ultracode', 'Deepest reasoning; complete, careful code']];
const MODES = [['manual', 'Manual', 'Agentic asks before commands and other changes, and before opening new sites.'],
  ['acceptEdits', 'Accept edits', 'File edits go ahead without asking; commands and other changes still ask.'],
  ['auto', 'Auto', 'Agentic runs on its own and never stops to ask.']];
const MODE_ICONS = { manual: '✋', acceptEdits: '✎', auto: '⚡' };
const BRAIN = '<svg viewBox="0 0 24 24" class="ic"><path d="M9 4a3 3 0 0 0-3 3v.5A3 3 0 0 0 4 10.3 3 3 0 0 0 5 15a3 3 0 0 0 3 4h1zM15 4a3 3 0 0 1 3 3v.5a3 3 0 0 1 2 2.8 3 3 0 0 1-1 4.7 3 3 0 0 1-3 4h-1zM9 4h6M9 20h6"/></svg>';
const SHIELD = '<svg viewBox="0 0 24 24" class="ic"><path d="M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z"/></svg>';
function effortOf() { const v = (CHAT.settings && CHAT.settings.reasoningEffort) || 'medium'; return EFFORTS.find(e => e[0] === v) || EFFORTS[2]; }
function modeOf() { const v = (CHAT.settings && CHAT.settings.permissionMode) || (CHAT.meta && CHAT.meta.defaultPermissionMode) || 'auto'; return MODES.find(m => m[0] === v) || MODES[MODES.length - 1]; }
/** Shift+Tab in the message box (like Claude Code): the next permission mode. */
function cycleMode() {
  const next = MODES[(MODES.indexOf(modeOf()) + 1) % MODES.length];
  CHAT.settings.permissionMode = next[0];
  saveSettings();
  if (typeof redrawComposerBar === 'function') redrawComposerBar();
  toast(`${MODE_ICONS[next[0]] || ''} ${next[1]} — ${next[2]}`, 'ok');
}
function comboHtml(kind) {
  const isEffort = kind === 'effort', cur = isEffort ? effortOf() : modeOf(), list = isEffort ? EFFORTS : MODES;
  const thinkOff = isEffort && typeof thinkingOn === 'function' && !thinkingOn();
  return `<div class="combo combo-${kind}${!isEffort && cur[0] !== 'auto' ? ' strict' : ''}${isEffort ? ' lvl-' + (thinkOff ? 'off' : cur[0]) : ''}" data-combo="${kind}">
    <button type="button" class="combo-btn" data-act="combo-open" data-combo="${kind}" data-tip="${isEffort ? 'Reasoning effort' : 'Permission mode'}">${isEffort ? BRAIN : SHIELD}<span>${esc(thinkOff ? 'Thinking off' : cur[1])}</span><svg viewBox="0 0 24 24" class="ic chev"><path d="M6 15l6-6 6 6"/></svg></button>
    <div class="menu combo-menu hidden">${isEffort ? `<button type="button" class="combo-opt think-row" data-act="thinking-toggle"><span class="combo-text"><b>Thinking</b><span>${thinkOff ? 'Off: answers come right away, without reasoning' : 'On: the model reasons before it answers'}</span></span><span class="switch-mini${thinkOff ? '' : ' on'}" aria-hidden="true"></span></button>` : ''}<div class="menu-title">${isEffort ? 'Reasoning effort' : 'Permission mode'}</div>${list.map(([v, label, desc], i) => `<button type="button" class="combo-opt${v === cur[0] ? ' on' : ''}" data-act="combo-pick" data-combo="${kind}" data-value="${v}" data-i="${i}">
      ${isEffort ? `<span class="lvl-dots">${[0, 1, 2, 3].map(n => `<i class="${n < i ? 'f' : ''}"></i>`).join('')}</span>` : `<span class="mode-ico">${MODE_ICONS[v] || '⚡'}</span>`}
      <span class="combo-text"><b>${esc(label)}</b><span>${esc(desc)}</span></span>${v === cur[0] ? '<svg viewBox="0 0 24 24" class="ic tick"><path d="M5 12.5l4.5 4.5L19 7"/></svg>' : ''}</button>`).join('')}</div></div>`;
}
ACTIONS['combo-open'] = el => {
  const wrap = el.closest('.combo'), menu = $('.combo-menu', wrap);
  const open = menu.classList.contains('hidden');
  $$('.combo-menu').forEach(m => m.classList.add('hidden'));
  if (!open) return;
  menu.classList.remove('hidden');
  applyRich(menu);
  setTimeout(() => document.addEventListener('click', function close(ev) { if (!ev.target.closest('.combo')) { menu.classList.add('hidden'); document.removeEventListener('click', close); } }), 0);
};
ACTIONS['combo-pick'] = el => {
  const kind = el.dataset.combo;
  if (kind === 'effort') CHAT.settings.reasoningEffort = el.dataset.value; else CHAT.settings.permissionMode = el.dataset.value;
  saveSettings();
  const wrap = el.closest('.combo');
  const node = htmlNode(comboHtml(kind));
  wrap.replaceWith(node);
  bounce($('.combo-btn', node));
  toast(kind === 'effort' ? `Reasoning effort: ${effortOf()[1]}` : `${modeOf()[1]} — ${modeOf()[2]}`, 'ok');
};

// ------------------------------------------------------------------ context window
function fmtTokens(n) { return n >= 1e6 ? (n / 1e6).toFixed(n >= 1e7 ? 0 : 1).replace(/\.0$/, '') + 'M' : n >= 1000 ? (n / 1000).toFixed(n >= 1e5 ? 0 : 1).replace(/\.0$/, '') + 'k' : String(n); }
function maxContext() { return (CHAT.meta && CHAT.meta.contextWindows && CHAT.meta.contextWindows[CHAT.model]) || 128000; }
function contextHtml() {
  return `<div class="ctx-meter" id="ctx-meter"><button type="button" class="ctx-main" data-act="ctx-info" data-tip="Context: what the model receives">
      <span class="ctx-bar"><i class="ctx-fill" id="ctx-fill"></i><i class="ctx-pending" id="ctx-pending"></i></span><span class="ctx-label" id="ctx-label">0 / ${fmtTokens(maxContext())}</span><span class="ctx-pct" id="ctx-pct">0%</span></button>
    <button type="button" class="ctx-compact" data-act="chat-compact" data-tip="Compact: summarize older messages to free context"><svg viewBox="0 0 24 24" class="ic"><path d="M4 9h16M4 15h16M9 4l3 3 3-3M9 20l3-3 3 3"/></svg></button></div>`;
}
function updateContext(stats) {
  if (!stats || !stats.max) return;
  CHAT.ctx = stats;
  CHAT.ctxLive = null;
  renderContext();
}
/**
 * While an answer is written the meter moves with it: each request the model gets (the server's estimate, then the
 * provider's count), plus what it has written since (about 4 characters a token).
 */
function liveContext(chatId, d) {
  if (!d || !d.used || !CHAT.conv || CHAT.conv.id !== chatId) return;
  CHAT.ctx = Object.assign({}, CHAT.ctx || {}, { used: d.used, max: d.max || (CHAT.ctx && CHAT.ctx.max) || maxContext() });
  CHAT.ctxLive = { chatId, chars: 0 };
  renderContext();
}
let ctxTimer = 0;
function growContext(chatId, chars) {
  const l = CHAT.ctxLive;
  if (!l || l.chatId !== chatId) return;
  l.chars += chars;
  if (!ctxTimer) ctxTimer = setTimeout(() => { ctxTimer = 0; renderContext(); }, 250);
}
function renderContext() {
  const fill = $('#ctx-fill'), label = $('#ctx-label'), pend = $('#ctx-pending'), meter = $('#ctx-meter');
  if (!fill || !label || !pend || !meter) return;
  const max = (CHAT.ctx && CHAT.conv && CHAT.ctx.max) || maxContext();
  const growing = CHAT.ctxLive && CHAT.conv && CHAT.ctxLive.chatId === CHAT.conv.id ? Math.round(CHAT.ctxLive.chars / 4) : 0;
  const used = (CHAT.conv && CHAT.ctx ? CHAT.ctx.used : 0) + growing;
  const input = $('#chat-input');
  const typing = Math.ceil(((input && input.value.length) || 0) / 4) + CHAT.attachments.reduce((n, a) => n + (a.kind === 'image' ? 1000 : Math.min((a.sizeBytes || 0) / 4, 30000)), 0);
  const p = Math.min(100, 100 * used / max), q = Math.min(100 - p, 100 * typing / max);
  fill.style.width = p + '%';
  pend.style.width = q + '%';
  // Nothing used yet: the window's size, quietly, instead of "0 / 200k".
  const empty = used + typing === 0;
  label.textContent = empty ? `${fmtTokens(max)} context` : `${fmtTokens(used + typing)} / ${fmtTokens(max)}`;
  meter.classList.toggle('ctx-empty', empty);
  const total = p + q;
  const pct = $('#ctx-pct');
  if (pct) pct.textContent = total > 0 && total < 1 ? '<1%' : Math.round(total) + '%';
  meter.classList.toggle('warn', total >= 60 && total < 85);
  meter.classList.toggle('bad', total >= 85);
  const auto = (CHAT.meta && CHAT.meta.autoCompactPercent) || 0;
  meter.dataset.tip = (typeof sessionTotalsText === 'function' && CHAT.conv ? sessionTotalsText() + '\n' : '') + `${num(used)} of ${num(max)} tokens (${total.toFixed(1)}%)` + (CHAT.ctx && CHAT.ctx.compacted ? ' · earlier messages are summarized' : '') + (auto ? ` · compacts automatically at ${auto}%` : '');
}
async function loadContext() {
  if (!CHAT.conv) { CHAT.ctx = null; renderContext(); return; }
  try { updateContext(await get(`/api/chat/conversations/${enc(CHAT.conv.id)}/context?model=${enc(CHAT.model)}`)); } catch { renderContext(); }
}
ACTIONS['ctx-info'] = () => {
  const c = CHAT.ctx;
  openModal('Context window', `<div class="ctx-detail">
      <div class="ctx-big"><span class="ctx-bar"><i class="ctx-fill" id="ctx-fill-big"></i></span><div><b>${num(c ? c.used : 0)}</b> of ${num(c ? c.max : maxContext())} tokens</div></div>
      <p class="muted">What <b>${esc(modelName(CHAT.model))}</b> receives with each message: instructions, ${c && c.compacted ? 'a summary of earlier messages, ' : ''}the last ${num(c ? c.messagesInFull : 0)} message${c && c.messagesInFull === 1 ? '' : 's'} in full and your files.
      When it passes ${num((CHAT.meta && CHAT.meta.autoCompactPercent) || 80)}% (or the chat gets long), older messages are summarized automatically.</p>
      ${typeof sessionTotalsText === 'function' && CHAT.conv ? `<p class="small"><b>${esc(sessionTotalsText())}</b></p>` : ''}
      ${c && c.compacted ? `<div class="notice info small">Compacted ${esc(ago(c.compactedUtc))} · summary about ${num(c.summaryTokens)} tokens. <button class="btn xs" data-act="chat-summary">View summary</button></div>` : ''}</div>`,
    `<button class="btn" data-act="modal-close">Close</button><button class="btn primary" data-act="chat-compact">Compact now</button>`, 'ins');
  const big = $('#ctx-fill-big'); if (big && c) big.style.width = Math.min(100, 100 * c.used / c.max) + '%';
};
ACTIONS['chat-compact'] = async el => {
  if (!CHAT.conv) { toast('Start a conversation first.', 'bad'); return; }
  if (CHAT.streams.has(CHAT.conv.id) || CHAT.live.has(CHAT.conv.id)) { toast('Wait until the answer is finished.', 'bad'); return; }
  closeModal();
  // PreCompact / PostCompact hooks on this computer (desktop app, VS Code); exit code 2 of PreCompact stops the compaction.
  const cc = window.chatClient, chatId = CHAT.conv.id;
  const hook = async event => { if (!cc || !cc.hookEvent) return null; try { return await cc.hookEvent({ event, chatId, payload: { trigger: 'manual', custom_instructions: '' } }); } catch { return null; } };
  const pre = await hook('PreCompact');
  if (pre && pre.blocked) { toast('Not compacted: a PreCompact hook stopped it. ' + pre.blocked, 'bad'); return; }
  const meter = $('#ctx-meter'); if (meter) meter.classList.add('compacting');
  try {
    const r = await postEventResult(`/api/chat/conversations/${enc(CHAT.conv.id)}/compact`, {});
    if (!r.compacted) { toast(r.message, 'bad'); return; }
    hook('PostCompact');
    // The reply's conversation comes without messages (they did not change); only its summary fields are taken.
    const c = r.conversation || {};
    Object.assign(CHAT.conv, { summary: c.summary, summaryThroughUtc: c.summaryThroughUtc || (r.context && r.context.summaryThroughUtc), summaryTokens: c.summaryTokens, compactedUtc: c.compactedUtc });
    updateContext(r.context);
    markCompacted(true, r.stats);
    toast(r.message, 'ok');
  } finally { if (meter) meter.classList.remove('compacting'); }
};
ACTIONS['chat-summary'] = async () => {
  if (!CHAT.conv) return;
  if (!CHAT.conv.summary) { const fresh = await get('/api/chat/conversations/' + enc(CHAT.conv.id)).catch(() => null); if (fresh) CHAT.conv.summary = fresh.summary; }
  if (!CHAT.conv.summary) return;
  openModal('Summary of earlier messages', `<div class="md answer">${chatMarkdown(CHAT.conv.summary)}</div>`, `<button class="btn primary" data-act="modal-close">Close</button>`, 'wide');
  applyRich($('#modal-card'));
};
/** The one-line stats shown when a compaction happens, e.g. "Context compacted · 142.1k → 27.3k tokens · 109 → 32 messages". */
function compactStatsText(s) {
  const k = n => (Math.max(0, n || 0) / 1000).toFixed(1) + 'k';
  return `Context compacted · ${k(s.beforeTokens)} → ${k(s.afterTokens)} tokens · ${s.beforeMessages || 0} → ${s.afterMessages || 0} messages`;
}
/** Dims the messages the model now only sees as a summary, with a divider after them. When stats are given (right after a compaction) the divider shows them. */
function markCompacted(animate, stats) {
  const thread = $('#chat-thread');
  if (!thread || !CHAT.conv) return;
  $$('.compact-divider', thread).forEach(d => d.remove());
  $$('.msg.compacted', thread).forEach(m => m.classList.remove('compacted'));
  const through = CHAT.conv.summaryThroughUtc && new Date(CHAT.conv.summaryThroughUtc).getTime();
  if (!through) return;
  // The drawn messages only (one pass over the page, not a search of it per message of the chat).
  const summarized = new Set((CHAT.conv.messages || []).filter(m => new Date(m.createdUtc).getTime() <= through).map(m => m.id));
  let last = null;
  $$(':scope > .msg[data-id]', thread).forEach(node => { if (summarized.has(node.dataset.id)) { node.classList.add('compacted'); last = node; } });
  const label = stats ? compactStatsText(stats) : 'Earlier messages are summarized for the model';
  if (last) last.insertAdjacentHTML('afterend', `<div class="compact-divider${animate ? ' fresh' : ''}"><span>${esc(label)}</span><button class="btn xs ghost" data-act="chat-summary">View summary</button></div>`);
}

// ------------------------------------------------------------------ desktop app this chat uses (seen from the web app)
function renderDeviceChip() {
  const slot = $('#device-chip');
  if (!slot) return;
  const id = CHAT.settings && CHAT.settings.device, d = id && CHAT.devices.get(id);
  if (!d || (window.chatClient && window.chatClient.deviceId === id)) { slot.innerHTML = ''; return; }
  slot.innerHTML = `<span class="device-chip${d.online ? ' online' : ''}" data-tip="${d.online ? 'Tools on this computer are available in this chat' : 'The desktop app is not connected: its tools are unavailable'}">
    <svg viewBox="0 0 24 24" class="ic"><path d="M3 5h18v11H3zM8 20h8M12 16v4"/></svg>${esc(d.name || 'Desktop')}</span>`;
}
