'use strict';
/* Agentic console - Artifacts: what you made that runs. Pages and drawings (HTML, SVG) from your chat answers run here in
   the sandboxed preview; in the desktop app and VS Code (window.chatClient.run) the projects on the computer run too, with
   their preview, output and tests. Share gives any of them a public link (/a/{token}) that anyone can open: it runs isolated
   from the viewer's session. */

const ART = { data: null, projects: [], chat: '', q: '' };
const ART_ICON = { html: '🌐', htm: '🌐', svg: '🎨' };
Object.assign(ICONS, {
  share: '<circle cx="18" cy="5" r="3"/><circle cx="6" cy="12" r="3"/><circle cx="18" cy="19" r="3"/><path d="m8.6 13.5 6.8 4M15.4 6.5l-6.8 4"/>',
  link: '<path d="M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1"/>',
  refresh: '<path d="M20 11a8 8 0 1 0-2.3 5.7M20 5v6h-6"/>'
});

function artOpenUrl(url) {
  if (window.chatClient && window.chatClient.openUrl) { window.chatClient.openUrl(url); return; }
  window.open(url, '_blank', 'noopener');
}

function artMatches(text) { return !ART.q || String(text || '').toLowerCase().includes(ART.q); }

/** The projects on this computer, each with its ways to run and its Share button (desktop app and VS Code). */
function artProjectsHtml() {
  const rc = runClient();
  if (!rc) return '';
  const links = (ART.data && ART.data.shared) || [];
  const configs = RUN.configs.filter(c => artMatches(c.name + ' ' + (c.command || '') + ' ' + (c.projectName || '')));
  const projects = ART.projects.length ? ART.projects : [{ id: '', name: 'This project', path: '', share: null }];
  const canShare = ART.data && ART.data.canShare;
  const body = projects.map(p => {
    const own = configs.filter(c => !p.id || !c.project || c.project === p.id);
    const link = p.key ? links.find(l => l.kind === 'site' && l.key === p.key) : null;
    const share = !canShare || !rc.share ? '' : p.share
      ? `<button class="btn xs" data-act="art-share-project" data-id="${esc(p.id)}" data-tip="Uploads ${esc(p.share)} (its index.html and files) and gives you a link anyone can open">${icon('share')} ${link ? 'Update link' : 'Share'}</button>`
      : `<span class="muted small" data-tip="Sharing uploads a website: a folder with index.html (the project itself, or its build: dist, build, out). Build it first.">nothing to share yet</span>`;
    return `<div class="art-project">
      <div class="art-project-head"><b>${esc(p.name)}</b>${p.path ? `<code class="muted small art-path">${esc(p.path)}</code>` : ''}<span class="spacer"></span>
        ${link ? `<button class="btn xs ghost" data-act="art-copy" data-url="${esc(link.url)}" data-tip="${esc(link.url)}">${icon('link')} Copy link</button>` : ''}${share}</div>
      ${own.length ? own.map(runRowHtml).join('') : '<p class="muted small art-none">No way to run it was found: set it up from the Run chip in Chat (⚙), or write .agentic/launch.json.</p>'}
    </div>`;
  }).join('');
  return card('Projects on this computer', `<div id="art-projects">${body}</div>`, {
    hint: 'Run a dev server (it opens next to the chat), the app or its tests',
    actions: `<button class="btn xs ghost" data-act="art-refresh-projects">${icon('refresh')} Refresh</button>${rc.edit ? '<button class="btn xs ghost" data-act="run-edit">✎ launch.json</button>' : ''}`
  });
}

function artChatsHtml() {
  const d = ART.data;
  const items = d.items.filter(a => artMatches(a.title + ' ' + a.chatTitle + ' ' + a.lang));
  const canShare = d.canShare;
  const grid = items.length ? `<div class="art-grid">${items.map(a => {
    const data = `data-chat="${esc(a.chatId)}" data-message="${esc(a.messageId)}" data-block="${esc(a.block)}"`;
    return `<div class="art-card" data-key="${esc(a.id)}">
      <button class="art-thumb" data-act="art-run" ${data} aria-label="Run ${esc(a.title)}"><span class="art-emoji">${ART_ICON[a.lang] || '🌐'}</span><span class="art-play">${RUN_ICON} Run</span></button>
      <div class="art-info"><div class="art-title" title="${esc(a.title)}">${esc(a.title)}</div>
        <div class="muted small">${esc((a.lang || '').toUpperCase())} · ${plural(a.lines, 'line')} · ${esc(ago(a.createdUtc))}</div>
        <a class="small art-chat" href="/chat/${enc(a.chatId)}" data-nav title="${esc(a.chatTitle)}">${esc(a.chatTitle || 'Chat')}</a></div>
      <div class="art-acts">
        <button class="btn xs" data-act="art-run" ${data}>${RUN_ICON} Run</button>
        ${canShare ? (a.sharedToken
          ? `<button class="btn xs on" data-act="art-copy" data-url="${esc(artLinkUrl(a.sharedToken))}" data-tip="Shared: copy its link">${icon('link')} Copy link</button>`
          : `<button class="btn xs" data-act="art-share" ${data} data-tip="A link anyone can open">${icon('share')} Share</button>`) : ''}
        <button class="btn xs ghost" data-act="art-download" ${data} data-tip="Download it as a file">${icon('download')}</button>
      </div></div>`;
  }).join('')}</div>` : emptyState(ART.q ? 'Nothing matches' : 'No artifacts yet', ART.q ? '' : 'Pages and drawings the assistant writes in a chat (HTML or SVG code) show here.');
  const filter = ART.chat ? `<div class="notice">Artifacts of one chat. <a href="/artifacts" data-nav>Show all</a></div>` : '';
  return card('From your chats', filter + grid, { hint: `${plural(items.length, 'page')}${d.items.length >= 300 ? ' (the newest 300)' : ''}` });
}

function artLinkUrl(token) {
  const l = ((ART.data && ART.data.shared) || []).find(x => x.token === token);
  return l ? l.url : location.origin + '/a/' + token;
}

function artSharedHtml() {
  const d = ART.data;
  const rows = d.shared.filter(l => artMatches(l.title + ' ' + (l.source || '')));
  const t = table([
    { h: 'Artifact', f: l => `<b>${esc(l.title)}</b><div class="muted small">${l.kind === 'site' ? `Website · ${plural(l.files, 'file')} · ${esc(fileSize(l.bytes))}${l.source ? ' · ' + esc(l.source) : ''}` : `${esc((l.lang || '').toUpperCase())} from a chat`}</div>` },
    { h: 'Views', num: true, f: l => num(l.views) },
    { h: 'Shared', f: l => `${esc(ago(l.createdUtc))}${new Date(l.updatedUtc) - new Date(l.createdUtc) > 60000 ? `<div class="muted small">updated ${esc(ago(l.updatedUtc))}</div>` : ''}` },
    { h: '', cls: 'art-link-acts', f: l => `<button class="btn xs" data-act="art-open" data-url="${esc(l.url)}">${icon('external')} Open</button>
        <button class="btn xs" data-act="art-copy" data-url="${esc(l.url)}">${icon('link')} Copy link</button>
        <button class="btn xs danger" data-act="art-unshare" data-token="${esc(l.token)}" data-title="${esc(l.title)}">Stop sharing</button>` }
  ], rows, { key: l => l.token, empty: 'No links yet', emptyText: 'Share a page from your chats or a project on this computer: anyone with its link can open it.' });
  const off = !d.sharing ? '<div class="notice warn">Sharing is turned off on this server (Chat.ArtifactSharing): links do not open.</div>'
    : !d.canShare ? '<div class="notice">Your account cannot make new links (it needs the "Share chats" permission).</div>' : '';
  return card('Shared links', off + t, { hint: d.sharing ? `${d.shared.length} of ${d.maxShared}` : '', flush: !off });
}

function artRender() {
  const main = $('#art-body');
  if (!main) return;
  main.innerHTML = artProjectsHtml() + artChatsHtml() + artSharedHtml();
  applyPermissions(main);
}

/** Redraws the projects (what runs changed) without touching the rest of the page. */
function artRenderProjects() {
  const box = $('#art-projects');
  if (!box) { RUN_LISTENERS.delete(artRenderProjects); return; }
  const card = box.closest('.card');
  const tmp = document.createElement('div');
  tmp.innerHTML = artProjectsHtml();
  if (card && tmp.firstElementChild) card.replaceWith(tmp.firstElementChild);
}

async function artLoad() {
  const rc = runClient();
  const [d] = await Promise.all([
    get('/api/chat/artifacts' + (ART.chat ? '?chat=' + enc(ART.chat) : '')),
    rc ? refreshRun(true).catch(() => { }) : null,
    rc && rc.projects ? rc.projects().then(p => { ART.projects = p || []; }, () => { ART.projects = []; }) : null
  ]);
  ART.data = d;
}

VIEWS.artifacts = {
  live: false,
  async render() {
    setCrumbs([['Artifacts', '/artifacts']]);
    ART.chat = S.params.query.get('chat') || '';
    ART.q = '';
    await artLoad();
    RUN_LISTENERS.add(artRenderProjects);
    return pageHead('Artifacts', {
      subtitle: runClient()
        ? 'Everything you made that runs: the projects on this computer and the pages and drawings from your chats. Run them here, or share a link anyone can open.'
        : 'The pages and drawings from your chats. Run them here, or share a link anyone can open. In the desktop app and VS Code the projects on your computer are here too.',
      actions: `<input class="input art-search" type="search" placeholder="Filter" aria-label="Filter artifacts">`
    }) + `<div class="stack" id="art-body">${artProjectsHtml()}${artChatsHtml()}${artSharedHtml()}</div>`;
  }
};

// ------------------------------------------------------------------ actions
document.addEventListener('input', ev => {
  if (!ev.target.classList || !ev.target.classList.contains('art-search')) return;
  ART.q = ev.target.value.trim().toLowerCase();
  artRender();
});

async function artCode(el) {
  return get(`/api/chat/artifacts/code?chat=${enc(el.dataset.chat)}&message=${enc(el.dataset.message)}&block=${enc(el.dataset.block)}`);
}

ACTIONS['art-run'] = async el => {
  let a;
  try { a = await artCode(el); } catch (e) { toast(e.message, 'bad'); return; }
  const shareBtn = ART.data && ART.data.canShare
    ? `<button class="btn" data-act="art-share" data-chat="${esc(el.dataset.chat)}" data-message="${esc(el.dataset.message)}" data-block="${esc(el.dataset.block)}">${icon('share')} Share</button>` : '';
  openModal(a.title, `<div class="embed-modal"><div class="embed-pane sandbox" data-pane="preview"></div><textarea class="code-src" hidden>${esc(a.code)}</textarea></div>`,
    `${shareBtn}<button class="btn" data-act="art-open-chat" data-chat="${esc(el.dataset.chat)}">Open its chat</button><button class="btn primary" data-act="modal-close">Close</button>`, 'full');
  const pane = $('#modal-card [data-pane="preview"]');
  pane.dataset.lang = a.lang;
  startModalPreview(pane);
};

ACTIONS['art-open-chat'] = el => { closeModal(); navigate('/chat/' + encodeURIComponent(el.dataset.chat)); };

ACTIONS['art-share'] = async el => {
  let link;
  try { link = await post('/api/chat/artifacts/share', { chatId: el.dataset.chat, messageId: el.dataset.message, block: Number(el.dataset.block) }); } catch (e) { toast(e.message, 'bad'); return; }
  const copied = await writeClipboard(link.url);
  toast(`Shared: ${link.url}${copied ? ' (copied)' : ''}`, 'ok');
  if (ART.data) {
    if (!ART.data.shared.some(l => l.token === link.token)) ART.data.shared.unshift(link);
    const item = ART.data.items.find(x => x.chatId === el.dataset.chat && x.messageId === el.dataset.message && String(x.block) === String(el.dataset.block));
    if (item) item.sharedToken = link.token;
    if ($('#art-body')) artRender();
  }
};

ACTIONS['art-copy'] = async el => {
  if (await writeClipboard(el.dataset.url)) toast('Link copied', 'ok');
  else openModal('Link', `<input class="input" readonly value="${esc(el.dataset.url)}">`, '<button class="btn primary" data-act="modal-close">Close</button>');
};

ACTIONS['art-open'] = el => artOpenUrl(el.dataset.url);

ACTIONS['art-unshare'] = async el => {
  if (!await confirmBox(`Stop sharing “${el.dataset.title}”?`, 'Its link stops working for everyone who has it.', { confirm: 'Stop sharing', danger: true })) return;
  try { await del('/api/chat/artifacts/shared/' + enc(el.dataset.token)); } catch (e) { toast(e.message, 'bad'); return; }
  toast('No longer shared', 'ok');
  if (ART.data) {
    ART.data.shared = ART.data.shared.filter(l => l.token !== el.dataset.token);
    for (const i of ART.data.items) if (i.sharedToken === el.dataset.token) i.sharedToken = null;
    artRender();
  }
};

ACTIONS['art-download'] = async el => {
  let a;
  try { a = await artCode(el); } catch (e) { toast(e.message, 'bad'); return; }
  const name = (a.title || 'artifact').replace(/[^\w\- ]+/g, '').trim().slice(0, 60) || 'artifact';
  const url = URL.createObjectURL(new Blob([a.code], { type: a.lang === 'svg' ? 'image/svg+xml' : 'text/html' }));
  const link = document.createElement('a'); link.href = url; link.download = `${name}.${a.lang === 'svg' ? 'svg' : 'html'}`;
  document.body.appendChild(link); link.click(); link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
};

ACTIONS['art-share-project'] = async el => {
  el.disabled = true;
  const r = await shareProject(el.dataset.id);
  el.disabled = false;
  if (r && ART.data) {
    try { ART.data = await get('/api/chat/artifacts' + (ART.chat ? '?chat=' + enc(ART.chat) : '')); } catch { /* the list stays */ }
    artRender();
  }
};

ACTIONS['art-refresh-projects'] = async () => {
  try { await artLoad(); } catch (e) { toast(e.message, 'bad'); return; }
  artRender();
};
