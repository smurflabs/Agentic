'use strict';
/* Agentic console - Chat extras, the features people know from ChatGPT and Claude: projects (chats with shared
   instructions and files), personalization and memory, saved prompts (type / in the message box), share links and
   export, editing a question, versions of edited questions and regenerated answers, continuing a cut-off answer,
   feedback on answers, reading answers aloud, dictation, temporary chats and keyboard shortcuts. chat.js draws the
   page; this file adds these parts to it. */

const svgIcon = (d, cls = '') => `<svg viewBox="0 0 24 24" class="ic${cls ? ' ' + cls : ''}" aria-hidden="true">${d}</svg>`;
const FOLDER_ICON = svgIcon('<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>');
const SHARE_ICON = svgIcon('<path d="M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1"/>');
const MORE_ICON = svgIcon('<circle cx="5" cy="12" r="1.2"/><circle cx="12" cy="12" r="1.2"/><circle cx="19" cy="12" r="1.2"/>');
const PERSON_ICON = svgIcon('<circle cx="12" cy="8" r="4"/><path d="M4 21c0-4 4-6 8-6s8 2 8 6"/>');
const PROMPT_ICON = svgIcon('<path d="M4 5h16v11H9l-5 4z"/><path d="M13 8l-3 6"/>');
const KEYS_ICON = svgIcon('<rect x="2.5" y="6" width="19" height="12" rx="2"/><path d="M6 10h.01M10 10h.01M14 10h.01M18 10h.01M7 14h10"/>');
const TEMP_ICON = svgIcon('<path d="M4 5h16v11H9l-5 4z" stroke-dasharray="3 2.4"/>');
const KB_ICON = svgIcon('<path d="M4 5.5A1.5 1.5 0 0 1 5.5 4H11v15H5.5A1.5 1.5 0 0 0 4 20.5zM20 5.5A1.5 1.5 0 0 0 18.5 4H13v15h5.5a1.5 1.5 0 0 1 1.5 1.5z"/>');
const VIDEO_ICON = svgIcon('<rect x="2.5" y="5.5" width="14" height="13" rx="2"/><path d="M16.5 10l5-3v10l-5-3z"/>');
const IMAGE_ICON = svgIcon('<rect x="3" y="4" width="18" height="16" rx="2"/><circle cx="9" cy="10" r="2"/><path d="M21 16l-5-5-9 9"/>');
const MONEY_ICON = svgIcon('<path d="M3 17l5-5 4 4 8-8M15 8h5v5"/>');
const STOCKS_ICON = svgIcon('<path d="M7 3v4M7 17v4M5 7h4v10H5zM17 4v5M17 15v5M15 9h4v6h-4z"/>');
const POLYMARKET_ICON = svgIcon('<path d="M4 20V10M10 20V4M16 20v-7M22 20H2M16 9l3-3 3 3"/>');
const NEWS_ICON = svgIcon('<path d="M4 5h13v14H6a2 2 0 0 1-2-2zM17 9h3v8a2 2 0 0 1-2 2M7 9h7M7 12h7M7 15h4"/>');
const MIC_ICON = svgIcon('<rect x="9" y="3" width="6" height="11" rx="3"/><path d="M5 11a7 7 0 0 0 14 0M12 18v3"/>');
const PENCIL_ICON = svgIcon('<path d="M4 20h4L19 9l-4-4L4 16zM14 6l4 4"/>');
const COPY_ICON = svgIcon('<rect x="9" y="9" width="11" height="11" rx="2"/><path d="M5 15V5a1 1 0 0 1 1-1h10"/>');
const REGEN_ICON = svgIcon('<path d="M20 11a8 8 0 1 0-2.3 5.7M20 4v7h-7"/>');
const SPEAK_ICON = svgIcon('<path d="M4 9v6h4l5 4V5L8 9zM16 9a4 4 0 0 1 0 6M18.5 6.5a8 8 0 0 1 0 11"/>');
const STOP_SPEAK_ICON = svgIcon('<rect x="7" y="7" width="10" height="10" rx="2"/>');
const THUMB_UP = svgIcon('<path d="M7 11v9H4v-9zM7 11l4-8a2 2 0 0 1 2 2v4h6a2 2 0 0 1 2 2.3l-1.2 7A2 2 0 0 1 17.8 20H7"/>');
const THUMB_DOWN = svgIcon('<path d="M7 13V4H4v9zM7 13l4 8a2 2 0 0 0 2-2v-4h6a2 2 0 0 0 2-2.3l-1.2-7A2 2 0 0 0 17.8 4H7"/>');
const MEMORY_ICON = svgIcon('<path d="M9 4a3 3 0 0 0-3 3v.5A3 3 0 0 0 4 10.3 3 3 0 0 0 5 15a3 3 0 0 0 3 4h1zM15 4a3 3 0 0 1 3 3v.5a3 3 0 0 1 2 2.8 3 3 0 0 1-1 4.7 3 3 0 0 1-3 4h-1zM9 4h6M9 20h6"/>');

function busyHere() { return currentStream() || (CHAT.conv && CHAT.live.has(CHAT.conv.id)); }
/** The welcome of a new chat (temporary or in a project) after the mode or the project changed. */
function rerenderWelcome() { const t = $('#chat-thread'); if (t && !CHAT.conv) { t.innerHTML = welcomeHtml(); applyRich(t); } }
async function copyText(text, done) {
  try { await writeClipboardOrThrow(text); toast(done, 'ok'); } catch { toast('Copy is not available here.', 'bad'); }
}

// ------------------------------------------------------------------ temporary chats
function tempToggleHtml() {
  if (CHAT.conv) return '';
  return `<button class="chip-toggle temp-toggle${CHAT.temporary ? ' on' : ''}" data-act="chat-temp" aria-pressed="${!!CHAT.temporary}"
    data-tip="${CHAT.temporary ? 'Temporary chat: not in your history, no memory. Click to turn off.' : 'Temporary chat: not in your history, no memory, deleted after 24 hours'}">${TEMP_ICON}<span>Temporary</span></button>`;
}
ACTIONS['chat-temp'] = () => {
  CHAT.temporary = !CHAT.temporary;
  $('#temp-slot').innerHTML = tempToggleHtml();
  bounce($('#temp-slot .temp-toggle'));
  rerenderWelcome();
};

// ------------------------------------------------------------------ projects
function projectsHtml() {
  const list = CHAT.projects || [];
  return `<div class="proj-head"><span>Projects</span><button class="btn xs ghost icon-only" data-act="proj-new" aria-label="New project" data-tip="New project: chats with shared instructions and files">${icon('plus')}</button></div>
    ${list.length ? '' : '<div class="proj-empty muted small">Group chats with shared instructions and files.</div>'}
    ${list.map(p => `<div class="proj-item${p.id === CHAT.project ? ' active' : ''}">
      <button class="proj-open" data-act="proj-open" data-id="${esc(p.id)}">${FOLDER_ICON}<span class="t">${esc(p.name)}</span>${p.chats ? `<span class="n">${num(p.chats)}</span>` : ''}</button>
      <button class="btn xs ghost icon-only proj-gear" data-act="proj-edit" data-id="${esc(p.id)}" aria-label="Project settings" data-tip="Project settings">${PENCIL_ICON}</button></div>`).join('')}
    ${CHAT.project ? '<button class="proj-all" data-act="proj-open" data-id="">← All chats</button>' : ''}`;
}
function renderProjects() { const el = $('#chat-projects'); if (el) el.innerHTML = projectsHtml(); }
async function reloadProjects() { CHAT.projects = await get('/api/chat/projects').catch(() => CHAT.projects || []); renderProjects(); }
/** Shows a project's chats in the list; a new chat then starts in it (with its instructions and files). */
ACTIONS['proj-open'] = el => {
  CHAT.project = el.dataset.id || null;
  renderProjects();
  renderChatList();
  if (CHAT.conv && CHAT.project && CHAT.conv.projectId !== CHAT.project) navigate('/chat');
  else rerenderWelcome();
};
ACTIONS['proj-new'] = () => projectModal(null);
ACTIONS['proj-edit'] = el => projectModal((CHAT.projects || []).find(p => p.id === el.dataset.id));
function projectModal(p) {
  CHAT.projFiles = p ? (p.files || []).map(f => ({ id: f.uploadId, name: f.name, kind: f.kind, summary: f.summary })) : [];
  openModal(p ? 'Project settings' : 'New project', `<div class="proj-form">
      <label>Name<input id="proj-name" maxlength="80" value="${esc(p ? p.name : '')}" placeholder="e.g. Valheim server" autocomplete="off"></label>
      <label>Description <span class="muted small">(optional)</span><input id="proj-desc" maxlength="500" value="${esc((p && p.description) || '')}" placeholder="What the project is about" autocomplete="off"></label>
      <label>Instructions <span class="muted small">— added to every chat in the project</span><textarea id="proj-ins" rows="7" maxlength="20000" placeholder="e.g. The server runs in Portainer on 10.0.0.5. Answer with docker compose examples.">${esc((p && p.instructions) || '')}</textarea></label>
      <div class="proj-files-head"><b>Files</b> <span class="muted small">The model reads them in every chat of the project: text, code, PDFs, spreadsheets…</span></div>
      <div class="proj-files" id="proj-files">${projFilesHtml()}</div>
      <label class="btn small proj-add-file">${icon('plus')} Add files<input type="file" multiple data-change="proj-files" hidden></label>
    </div>`,
    `${p ? `<button class="btn ghost danger" data-act="proj-delete" data-id="${esc(p.id)}">Delete project</button>` : ''}<span class="spacer"></span>
     <button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="proj-save" data-id="${esc(p ? p.id : '')}">${p ? 'Save' : 'Create project'}</button>`, 'ins');
}
function projFilesHtml() {
  if (!CHAT.projFiles.length) return '<div class="muted small">No files yet.</div>';
  return CHAT.projFiles.map(f => `<div class="file-chip" data-id="${esc(f.id)}"><span class="attach-kind k-${esc(f.kind)}">${esc((f.name.split('.').pop() || '').slice(0, 4).toUpperCase())}</span>
    <span class="fc-text"><b>${esc(f.name)}</b><span class="cell-sub">${esc(f.summary || '')}</span></span><button class="btn xs ghost" data-act="proj-file-remove" data-id="${esc(f.id)}" aria-label="Remove">${icon('x')}</button></div>`).join('');
}
CHANGES['proj-files'] = async el => {
  const files = Array.from(el.files); el.value = '';
  const box = $('#proj-files');
  for (const file of files) {
    box.insertAdjacentHTML('beforeend', `<div class="file-chip pending"><span class="spinner"></span><span>${esc(file.name)}</span></div>`);
    try { const u = await uploadFile(file); CHAT.projFiles.push({ id: u.id, name: u.name, kind: u.kind, summary: u.summary }); }
    catch (e) { toast(`${file.name}: ${e.message}`, 'bad'); }
    if ($('#proj-files')) $('#proj-files').innerHTML = projFilesHtml();
  }
};
ACTIONS['proj-file-remove'] = el => { CHAT.projFiles = CHAT.projFiles.filter(f => f.id !== el.dataset.id); $('#proj-files').innerHTML = projFilesHtml(); };
ACTIONS['proj-save'] = async el => {
  const id = el.dataset.id;
  const body = { name: $('#proj-name').value.trim(), description: $('#proj-desc').value.trim(), instructions: $('#proj-ins').value, files: CHAT.projFiles.map(f => f.id) };
  if (!body.name) { $('#proj-name').focus(); toast('Give the project a name.', 'bad'); return; }
  if ($('#proj-files .pending')) { toast('Wait until the files have finished uploading.', 'bad'); return; }
  let p;
  try { p = id ? await put('/api/chat/projects/' + enc(id), body) : await post('/api/chat/projects', body); } catch (e) { toast(e.message, 'bad'); return; }
  closeModal();
  await reloadProjects();
  if (!id) {
    CHAT.project = p.id;
    renderProjects(); renderChatList();
    if (CHAT.conv) navigate('/chat'); else rerenderWelcome();
  } else if (CHAT.conv) $('#chat-head').innerHTML = headHtml();
  else rerenderWelcome();
  toast(id ? 'Project saved' : 'Project created: chats started here use its instructions and files', 'ok');
};
ACTIONS['proj-delete'] = async el => {
  const id = el.dataset.id;
  if (!await confirmBox('Delete project', 'Delete this project? Its chats stay in your chat list.', { danger: true, confirm: 'Delete' })) return;
  await del('/api/chat/projects/' + enc(id));
  if (CHAT.project === id) CHAT.project = null;
  CHAT.list.forEach(c => { if (c.projectId === id) c.projectId = null; });
  if (CHAT.conv && CHAT.conv.projectId === id) { CHAT.conv.projectId = null; $('#chat-head').innerHTML = headHtml(); }
  await reloadProjects();
  renderChatList(); rerenderWelcome();
  toast('Project deleted', 'ok');
};

// ------------------------------------------------------------------ the chat's ⋯ menu: export, project, rename, delete
ACTIONS['chat-more'] = () => {
  const menu = $('#chat-more-menu'), c = CHAT.conv;
  if (!menu || !c) return;
  if (!menu.classList.contains('hidden')) { menu.classList.add('hidden'); return; }
  const projects = CHAT.projects || [];
  menu.innerHTML = `<button data-act="chat-artifacts">${icon('play')} Artifacts in this chat</button>
    <button data-act="chat-export" data-format="md">${icon('download')} Export as Markdown</button>
    <button data-act="chat-export" data-format="json">${icon('download')} Export as JSON</button>
    <button data-act="chat-import">${icon('upload')} Import chats from a file…</button>
    ${c.temporary ? '' : `<div class="menu-title">Project</div>
      ${projects.map(p => `<button data-act="chat-move" data-id="${esc(p.id)}"${c.projectId === p.id ? ' class="on" aria-current="true"' : ''}>${FOLDER_ICON} ${esc(p.name)}${c.projectId === p.id ? ' <span class="tick">✓</span>' : ''}</button>`).join('')}
      ${c.projectId ? '<button data-act="chat-move" data-id="">Remove from the project</button>' : ''}
      <button data-act="proj-new">${icon('plus')} New project…</button>`}
    ${typeof canDelegate === 'function' && canDelegate() ? `<hr><button data-act="chat-delegate">${icon('layers')} Hand to agents…</button>` : ''}
    <hr><button data-act="chat-rename">${PENCIL_ICON} Rename</button>${c.temporary ? '' : `<button data-act="chat-archive">${icon('archive')} ${c.archived ? 'Unarchive' : 'Archive'}</button>`}<button class="danger-text" data-act="chat-delete" data-id="${esc(c.id)}">${icon('trash')} Delete chat</button>`;
  menu.classList.remove('hidden');
  setTimeout(() => document.addEventListener('click', function close(ev) {
    if (ev.target.closest('#chat-more-menu button') || !ev.target.closest('.chat-more-wrap')) { menu.classList.add('hidden'); document.removeEventListener('click', close); }
  }), 0);
};
ACTIONS['chat-artifacts'] = () => { if (CHAT.conv) navigate('/artifacts?chat=' + enc(CHAT.conv.id)); };
ACTIONS['chat-export'] = el => {
  if (!CHAT.conv) return;
  const a = document.createElement('a');
  a.href = `/api/chat/conversations/${enc(CHAT.conv.id)}/export?format=${enc(el.dataset.format || 'md')}`;
  a.download = '';
  document.body.appendChild(a); a.click(); a.remove();
};
ACTIONS['chat-move'] = async el => {
  const c = CHAT.conv; if (!c) return;
  const id = el.dataset.id || null;
  try { await patch('/api/chat/conversations/' + enc(c.id), { projectId: id || '' }); } catch (e) { toast(e.message, 'bad'); return; }
  c.projectId = id;
  const item = CHAT.list.find(x => x.id === c.id); if (item) item.projectId = id;
  $('#chat-head').innerHTML = headHtml();
  await reloadProjects();
  renderChatList();
  toast(id ? `Moved to ${((CHAT.projects || []).find(p => p.id === id) || {}).name || 'the project'}` : 'Removed from the project', 'ok');
};

// ------------------------------------------------------------------ share links
function shareUrl(token) { return serverOrigin() + '/share/' + token; }
ACTIONS['chat-share'] = () => shareModal();
function shareModal() {
  const c = CHAT.conv; if (!c) return;
  const body = c.shareToken
    ? `<p>Anyone with this link can read the conversation without signing in. Your reasoning, tool output and costs are left out; messages you add later appear on it too.</p>
       <div class="share-row"><input id="share-url" readonly value="${esc(shareUrl(c.shareToken))}"><button class="btn primary" data-act="share-copy">${COPY_ICON} Copy</button></div>
       ${c.sharedUtc ? `<p class="muted small">Shared ${esc(ago(c.sharedUtc))}.</p>` : ''}`
    : `<p>Create a read-only link to this conversation. Anyone with the link can read it without signing in. Your reasoning, tool output and costs are left out; messages you add later appear on it too.</p>`;
  openModal('Share chat', `<div class="share-modal">${body}</div>`, c.shareToken
    ? '<button class="btn ghost danger" data-act="share-stop">Stop sharing</button><span class="spacer"></span><button class="btn" data-act="share-open">Open</button><button class="btn primary" data-act="modal-close">Done</button>'
    : `<span class="spacer"></span><button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="share-create">${SHARE_ICON} Create link</button>`, 'narrow');
}
ACTIONS['share-create'] = async () => {
  let r;
  try { r = await post(`/api/chat/conversations/${enc(CHAT.conv.id)}/share`); } catch (e) { toast(e.message, 'bad'); return; }
  CHAT.conv.shareToken = r.token; CHAT.conv.sharedUtc = new Date().toISOString();
  $('#chat-head').innerHTML = headHtml();
  shareModal();
  ACTIONS['share-copy']();
  refreshList();
};
ACTIONS['share-copy'] = async () => {
  const i = $('#share-url'); if (!i) return;
  try { await writeClipboardOrThrow(i.value); toast('Link copied', 'ok'); } catch { i.select(); toast('Press Ctrl+C to copy the link.', 'warn'); }
};
ACTIONS['share-open'] = () => window.open(shareUrl(CHAT.conv.shareToken), '_blank', 'noopener');
ACTIONS['share-stop'] = async () => {
  try { await del(`/api/chat/conversations/${enc(CHAT.conv.id)}/share`); } catch (e) { toast(e.message, 'bad'); return; }
  CHAT.conv.shareToken = null;
  closeModal();
  $('#chat-head').innerHTML = headHtml();
  toast('Sharing stopped: the link no longer works', 'ok');
  refreshList();
};

// ------------------------------------------------------------------ personalization and memory
ACTIONS['chat-personalize'] = async el => {
  let p;
  try { p = await get('/api/chat/personalization'); } catch (e) { toast(e.message, 'bad'); return; }
  CHAT.personal = p;
  // Memory only with that permission (Users & access).
  const memOn = !(CHAT.meta && CHAT.meta.canMemory === false);
  openModal('Personalization', `<div class="pers">
      <div class="pers-tabs" role="tablist"><button class="pers-tab on" role="tab" data-act="pers-tab" data-tab="you">Instructions</button>
        ${memOn ? `<button class="pers-tab" role="tab" data-act="pers-tab" data-tab="memory">Memory <span class="badge" id="mem-count">${num(p.memories.length)}</span></button>` : ''}</div>
      <div class="pers-pane" data-pane="you">
        <label>What should the assistant know about you?<textarea id="pers-about" rows="4" maxlength="3000" placeholder="e.g. I run game servers on Windows and Docker; I prefer metric units.">${esc(p.aboutMe || '')}</textarea></label>
        <label>How should it respond?<textarea id="pers-how" rows="4" maxlength="3000" placeholder="e.g. Be concise. Show commands first, then explain.">${esc(p.howToRespond || '')}</textarea></label>
        <p class="muted small">Used in all your chats, also in the desktop app and the CLI. A chat's own instructions and its project's instructions add to these.</p></div>
      ${memOn ? `<div class="pers-pane hidden" data-pane="memory">
        <label class="check pers-memory-on"><input type="checkbox" id="pers-memory"${p.memory ? ' checked' : ''}><span><b>Let the assistant remember things about you</b>
          <span class="cell-sub">It saves facts and preferences you share or ask it to remember, and uses them in later chats. Temporary chats never use memory.</span></span></label>
        <div class="mem-add"><input id="mem-new" maxlength="500" placeholder="Add a memory, e.g. My server runs Ubuntu 24.04" autocomplete="off"><button class="btn" data-act="mem-add">${icon('plus')} Add</button></div>
        <div class="mem-list" id="mem-list">${memoryListHtml(p.memories)}</div></div>` : ''}
    </div>`,
    `${memOn ? `<button class="btn ghost danger" data-act="mem-clear"${p.memories.length ? '' : ' disabled'}>Clear memory</button>` : ''}<span class="spacer"></span>
     <button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="pers-save">Save</button>`, 'ins');
  $('#mem-new')?.addEventListener('keydown', ev => { if (ev.key === 'Enter') { ev.preventDefault(); ACTIONS['mem-add'](); } });
  if (memOn && el && el.dataset && el.dataset.tab === 'memory') ACTIONS['pers-tab']({ dataset: { tab: 'memory' } });
};
function memoryListHtml(list) {
  if (!list.length) return '<div class="muted small mem-empty">Nothing remembered yet.</div>';
  return list.map(m => `<div class="mem-row" data-id="${esc(m.id)}"><div class="grow"><div class="mem-text">${esc(m.content)}</div>
      <div class="muted small">${m.source === 'user' ? 'Added by you' : 'Saved in a chat'} · ${esc(ago(m.createdUtc))}</div></div>
    <button class="btn xs ghost icon-only" data-act="mem-del" data-id="${esc(m.id)}" aria-label="Forget" data-tip="Forget">${icon('trash')}</button></div>`).join('');
}
function renderMemories() {
  const l = $('#mem-list'); if (l) l.innerHTML = memoryListHtml(CHAT.personal.memories);
  const n = $('#mem-count'); if (n) n.textContent = num(CHAT.personal.memories.length);
}
ACTIONS['pers-tab'] = el => {
  const tab = el.dataset.tab;
  $$('.pers-tab').forEach(b => b.classList.toggle('on', b.dataset.tab === tab));
  $$('.pers-pane').forEach(p => p.classList.toggle('hidden', p.dataset.pane !== tab));
};
ACTIONS['pers-save'] = async () => {
  const mem = $('#pers-memory');   // missing without the Memory permission: the setting stays as it is
  try { await put('/api/chat/personalization', Object.assign({ aboutMe: $('#pers-about').value, howToRespond: $('#pers-how').value }, mem ? { memory: mem.checked } : {})); }
  catch (e) { toast(e.message, 'bad'); return; }
  closeModal();
  toast('Personalization saved', 'ok');
};
ACTIONS['mem-add'] = async () => {
  const i = $('#mem-new'), v = i.value.trim();
  if (!v) { i.focus(); return; }
  try { CHAT.personal.memories.unshift(await post('/api/chat/memories', { content: v })); } catch (e) { toast(e.message, 'bad'); return; }
  i.value = '';
  renderMemories();
  const clear = $('[data-act="mem-clear"]'); if (clear) clear.disabled = false;
};
ACTIONS['mem-del'] = async el => {
  try { await del('/api/chat/memories/' + enc(el.dataset.id)); } catch (e) { toast(e.message, 'bad'); return; }
  CHAT.personal.memories = CHAT.personal.memories.filter(m => m.id !== el.dataset.id);
  const row = el.closest('.mem-row'); if (row) row.classList.add('leaving');
  setTimeout(renderMemories, reducedMotion() ? 0 : 180);
};
ACTIONS['mem-clear'] = async el => {
  // A confirmation dialog would replace this one: the button asks once more instead.
  if (!el.dataset.sure) { el.dataset.sure = '1'; el.textContent = 'Click again to forget everything'; return; }
  try { await post('/api/chat/memories/clear'); } catch (e) { toast(e.message, 'bad'); return; }
  CHAT.personal.memories = [];
  renderMemories();
  el.textContent = 'Memory cleared'; el.disabled = true;
  toast('Memory cleared', 'ok');
};

// ------------------------------------------------------------------ saved prompts: / in the message box
async function loadPrompts(fresh) {
  if (!CHAT.prompts || fresh) CHAT.prompts = await get('/api/chat/prompts').catch(() => CHAT.prompts || []);
  return CHAT.prompts;
}
document.addEventListener('input', ev => { if (ev.target && ev.target.id === 'chat-input') promptPicker(ev.target); });
async function promptPicker(input) {
  const v = input.value, m = v.match(/^\/([\w-]*)$/);
  // The desktop app lists saved prompts with its skills and slash commands; an older one shows its own list first.
  // With the built-in commands (chatcmds.js), saved prompts are listed below them in one list.
  if (!m || (window.chatClient && window.chatClient.slashPrompts) || typeof chatCommandHits === 'function') { hidePromptPop(); return; }
  const list = await loadPrompts();
  if (input.value !== v || !$('.composer-wrap') || $('.dk-slash')) { if ($('.dk-slash')) hidePromptPop(); return; }
  const q = m[1].toLowerCase();
  const hits = list.filter(p => p.command.startsWith(q) || (q && (p.title || '').toLowerCase().includes(q))).slice(0, 8);
  let pop = $('#prompt-pop');
  if (!pop) { pop = htmlNode('<div class="prompt-pop" id="prompt-pop" role="listbox" aria-label="Saved prompts"></div>'); $('.composer-wrap').prepend(pop); }
  CHAT.promptSel = 0; CHAT.promptHits = hits;
  pop.innerHTML = (hits.length
    ? hits.map((p, i) => `<button type="button" class="prompt-opt${i === 0 ? ' on' : ''}" role="option" data-act="prompt-pick" data-i="${i}"><b>/${esc(p.command)}</b><span class="muted">${esc(p.title || p.content.slice(0, 90))}</span></button>`).join('')
    : `<div class="muted small prompt-none">${list.length ? 'No saved prompt matches.' : 'No saved prompts yet.'}</div>`) +
    `<button type="button" class="prompt-manage" data-act="prompts-manage">${icon('plus')} ${list.length ? 'Manage saved prompts' : 'Create a saved prompt'}</button>`;
}
function hidePromptPop() { $('#prompt-pop')?.remove(); CHAT.promptHits = null; }
function pickPrompt(i) {
  const p = CHAT.promptHits && CHAT.promptHits[i], input = $('#chat-input');
  if (!p || !input) return;
  input.value = p.content;
  hidePromptPop();
  autosize(); renderContext();
  input.focus(); input.setSelectionRange(input.value.length, input.value.length);
}
ACTIONS['prompt-pick'] = el => pickPrompt(Number(el.dataset.i));
// The picker's keys run before the message box's own (capture), so Enter picks instead of sending.
document.addEventListener('keydown', ev => {
  if (!CHAT.promptHits || !ev.target || ev.target.id !== 'chat-input' || !$('#prompt-pop')) return;
  const n = CHAT.promptHits.length, down = keyIs(ev, 'list.next');
  if ((down || keyIs(ev, 'list.prev')) && n) {
    ev.preventDefault(); ev.stopImmediatePropagation();
    CHAT.promptSel = (CHAT.promptSel + (down ? 1 : n - 1)) % n;
    $$('#prompt-pop .prompt-opt').forEach((b, i) => b.classList.toggle('on', i === CHAT.promptSel));
  } else if (keyIs(ev, 'list.pick') && n) { ev.preventDefault(); ev.stopImmediatePropagation(); pickPrompt(CHAT.promptSel); }
  else if (keyIs(ev, 'list.close')) { ev.stopImmediatePropagation(); hidePromptPop(); }
}, true);
document.addEventListener('mousedown', ev => { if (CHAT.promptHits && !ev.target.closest('#prompt-pop, #chat-input')) hidePromptPop(); });
ACTIONS['prompts-manage'] = async () => { hidePromptPop(); await loadPrompts(true); promptsModal(); };
function promptsModal(edit) {
  const list = CHAT.prompts || [];
  openModal('Saved prompts', `<div class="prompts-modal">
      <p class="muted small">Type <kbd>/</kbd> and a command in the message box to insert its prompt. They work in every chat.</p>
      <div class="prompt-list">${list.length ? list.map(p => `<div class="prompt-row${edit && edit.id === p.id ? ' editing' : ''}"><div class="grow"><b>/${esc(p.command)}</b> ${p.title ? `<span class="muted">${esc(p.title)}</span>` : ''}
          <div class="cell-sub">${esc(p.content.slice(0, 180))}</div></div>
        <button class="btn xs ghost" data-act="prompt-edit" data-id="${esc(p.id)}">Edit</button><button class="btn xs ghost icon-only" data-act="prompt-del" data-id="${esc(p.id)}" aria-label="Delete" data-tip="Delete">${icon('trash')}</button></div>`).join('')
        : '<div class="muted small">No saved prompts yet.</div>'}</div>
      <div class="prompt-form" id="prompt-form" data-id="${esc(edit ? edit.id : '')}"><div class="menu-title">${edit ? `Edit /${esc(edit.command)}` : 'New prompt'}</div>
        <div class="prompt-form-row"><label>Command<span class="cmd-input"><span>/</span><input id="prompt-cmd" maxlength="40" value="${esc(edit ? edit.command : '')}" placeholder="summarize" autocomplete="off"></span></label>
          <label class="grow">Title <span class="muted small">(optional)</span><input id="prompt-title" maxlength="100" value="${esc(edit ? edit.title || '' : '')}" placeholder="Summarize a text" autocomplete="off"></label></div>
        <label>Prompt<textarea id="prompt-content" rows="5" maxlength="20000" placeholder="Summarize the following text in five bullet points:">${esc(edit ? edit.content : '')}</textarea></label></div>
    </div>`,
    `${edit ? '<button class="btn ghost" data-act="prompt-new">New prompt</button>' : ''}<span class="spacer"></span><button class="btn" data-act="modal-close">Close</button>
     <button class="btn primary" data-act="prompt-save">${edit ? 'Save changes' : 'Add prompt'}</button>`, 'ins');
}
ACTIONS['prompt-edit'] = el => promptsModal((CHAT.prompts || []).find(p => p.id === el.dataset.id));
ACTIONS['prompt-new'] = () => promptsModal();
ACTIONS['prompt-save'] = async () => {
  const id = $('#prompt-form').dataset.id;
  const body = { command: $('#prompt-cmd').value.trim().replace(/^\//, ''), title: $('#prompt-title').value.trim(), content: $('#prompt-content').value };
  if (!body.command || !body.content.trim()) { toast('A command and the prompt text are needed.', 'bad'); return; }
  try { if (id) await put('/api/chat/prompts/' + enc(id), body); else await post('/api/chat/prompts', body); } catch (e) { toast(e.message, 'bad'); return; }
  await loadPrompts(true);
  promptsModal();
  toast(`Saved /${body.command.toLowerCase()}`, 'ok');
};
ACTIONS['prompt-del'] = async el => {
  try { await del('/api/chat/prompts/' + enc(el.dataset.id)); } catch (e) { toast(e.message, 'bad'); return; }
  CHAT.prompts = (CHAT.prompts || []).filter(p => p.id !== el.dataset.id);
  promptsModal();
};

// ------------------------------------------------------------------ editing a question, versions, continuing, feedback
ACTIONS['msg-edit'] = el => {
  if (busyHere()) { toast('Wait until the answer is finished.', 'bad'); return; }
  const node = el.closest('.msg'), m = CHAT.conv && CHAT.conv.messages.find(x => x.id === node.dataset.id);
  if (!m || node.classList.contains('editing')) return;
  node.classList.add('editing');
  $('.bubble-u', node).insertAdjacentHTML('afterend', `<div class="edit-box"><textarea rows="3" aria-label="Edit your message">${esc(m.content || '')}</textarea>
    <div class="edit-actions"><span class="muted small">${m.attachments && m.attachments.length ? 'Its files go with it again. ' : ''}The answer you have now stays available as a version.</span><span class="spacer"></span>
      <button class="btn small" data-act="edit-cancel">Cancel</button><button class="btn small primary" data-act="edit-send">Send</button></div></div>`);
  const ta = $('.edit-box textarea', node);
  const fit = () => { ta.style.height = 'auto'; ta.style.height = Math.min(420, ta.scrollHeight + 2) + 'px'; };
  ta.addEventListener('input', fit); fit();
  ta.focus(); ta.setSelectionRange(ta.value.length, ta.value.length);
  ta.addEventListener('keydown', ev => {
    if (ev.isComposing) return;
    if (keyIs(ev, 'chat.send') && !TOUCH_ONLY.matches) { ev.preventDefault(); ACTIONS['edit-send']($('[data-act="edit-send"]', node)); return; }
    if (keyIs(ev, 'chat.newline')) { keyNewline(ev); return; }
    if (keyIs(ev, 'chat.cancelEdit')) { ev.preventDefault(); ev.stopPropagation(); ACTIONS['edit-cancel']($('[data-act="edit-cancel"]', node)); }
  });
};
ACTIONS['edit-cancel'] = el => { const node = el.closest('.msg'); $('.edit-box', node)?.remove(); node.classList.remove('editing'); };
ACTIONS['edit-send'] = el => {
  const node = el.closest('.msg'), id = node.dataset.id, text = $('.edit-box textarea', node).value.trim();
  const m = CHAT.conv && CHAT.conv.messages.find(x => x.id === id);
  if (!m) return;
  if (!text && !(m.attachments && m.attachments.length)) { $('.edit-box textarea', node).focus(); return; }
  if (text === (m.content || '').trim()) { ACTIONS['edit-cancel'](el); return; }
  sendMessage(false, text, { editMessageId: id }).catch(e => toast(e.message, 'bad'));
};
// ↑ in an empty message box edits your last message, like other chat apps.
document.addEventListener('keydown', ev => {
  if (!ev.target || ev.target.id !== 'chat-input' || ev.target.value || CHAT.promptHits || !keyIs(ev, 'chat.editLast')) return;
  const last = CHAT.conv && [...CHAT.conv.messages].reverse().find(m => m.role === 'user' && m.finishReason !== 'auto_goal');
  const btn = last && $(`#chat-thread .msg[data-id="${CSS.escape(last.id)}"] [data-act="msg-edit"]`);
  if (btn && !busyHere()) { ev.preventDefault(); btn.click(); }
});
ACTIONS['msg-version'] = async el => {
  if (busyHere()) { toast('Wait until the answer is finished.', 'bad'); return; }
  const anchor = Number(el.closest('.ver-nav').dataset.anchor), dir = Number(el.dataset.dir);
  const p = (CHAT.conv.versions || []).find(v => v.anchorIndex === anchor);
  const target = p && p.versions[p.position - 1 + dir];
  if (!target || !target.branchId) return;
  let conv;
  try { conv = await post(`/api/chat/conversations/${enc(CHAT.conv.id)}/versions`, { branchId: target.branchId }); } catch (e) { toast(e.message, 'bad'); return; }
  const sc = $('#chat-scroll'), anchorNode = $(`#chat-thread .ver-nav[data-anchor="${anchor}"]`);
  const offset = anchorNode && sc ? anchorNode.getBoundingClientRect().top - sc.getBoundingClientRect().top : null;
  const drawnFrom = CHAT.renderedFrom;
  CHAT.conv = Object.assign(conv, { settings: CHAT.conv.settings });
  const thread = $('#chat-thread');
  thread.innerHTML = threadHtml(Math.min(Number(anchor), Number.isFinite(drawnFrom) ? drawnFrom : Infinity));
  applyRich(thread);
  if (typeof watchEarlierBar === 'function') watchEarlierBar();
  markCompacted();
  const nav = $(`#chat-thread .ver-nav[data-anchor="${anchor}"]`);
  if (nav && sc && offset !== null) { CHAT.stick = false; sc.scrollTop += nav.getBoundingClientRect().top - sc.getBoundingClientRect().top - offset; bounce(nav); }
  hideQuestion(true); showPendingQuestion();
  loadContext();
};
ACTIONS['msg-continue'] = () => { if (!busyHere()) sendMessage(false, undefined, { continue: true }).catch(e => toast(e.message, 'bad')); };
ACTIONS['msg-rate'] = async el => {
  const node = el.closest('.msg'), id = node.dataset.id, m = CHAT.conv && CHAT.conv.messages.find(x => x.id === id);
  if (!m) return;
  const v = Number(el.dataset.v), next = m.feedback === v ? 0 : v;
  let note;
  if (next === -1) {
    note = await confirmBox('What was wrong?', 'Optional: say what was wrong with this answer.', { input: 'What was wrong', multiline: true, confirm: 'Send feedback' });
    if (note === null) return;
  }
  try { await post(`/api/chat/conversations/${enc(CHAT.conv.id)}/messages/${enc(id)}/feedback`, { value: next, note: note || undefined }); } catch (e) { toast(e.message, 'bad'); return; }
  m.feedback = next || null;
  $$('.rate', node).forEach(b => { const on = Number(b.dataset.v) === next; b.classList.toggle('on', on); b.setAttribute('aria-pressed', String(on)); });
  if (next) { bounce(el); toast('Thanks for the feedback', 'ok'); }
};

// ------------------------------------------------------------------ reading answers aloud
const SPEECH = { node: null };
/** The answer as plain sentences: code, formulas and Markdown marks are not read out. */
function speakableText(md) {
  return String(md || '').replace(/```[\s\S]*?(```|$)/g, ' (code) ').replace(/\$\$[\s\S]*?\$\$/g, ' (formula) ').replace(/`([^`]+)`/g, '$1')
    .replace(/!\[[^\]]*\]\([^)]*\)/g, '').replace(/\[([^\]]+)\]\([^)]*\)/g, '$1').replace(/^#{1,6}\s+/gm, '').replace(/^\s*([-*+]|\d+[.)])\s+/gm, '')
    .replace(/^\s*>\s?(\[![A-Za-z]+\])?/gm, '').replace(/<[^>]+>/g, '').replace(/\[(\d{1,2})\]/g, '').replace(/[*_~=|#]+/g, '')
    .replace(/\n{2,}/g, '. ').replace(/\s+/g, ' ').trim();
}
function pickVoice() {
  const voices = speechSynthesis.getVoices(), lang = navigator.language || 'en-US';
  return voices.find(v => v.lang === lang && /natural|neural|online/i.test(v.name)) || voices.find(v => v.lang === lang) ||
    voices.find(v => v.lang.slice(0, 2) === lang.slice(0, 2)) || null;
}
function stopSpeaking() {
  const node = SPEECH.node; SPEECH.node = null;
  if (window.speechSynthesis) speechSynthesis.cancel();
  if (node && typeof stopAudio === 'function') stopAudio();
  const b = node && $('[data-act="msg-speak"]', node);
  if (b) { b.classList.remove('on', 'busy'); b.innerHTML = SPEAK_ICON; b.dataset.tip = 'Read aloud'; }
}
ACTIONS['msg-speak'] = async el => {
  const node = el.closest('.msg'), same = SPEECH.node === node;
  stopSpeaking();
  if (same) return;
  const m = CHAT.conv && CHAT.conv.messages.find(x => x.id === node.dataset.id);
  const text = speakableText(m ? m.content : ($('.answer', node) || {}).innerText);
  if (!text) return;
  // The speech server's voice (voice.js) when there is one; the browser's own voices otherwise.
  if (CHAT.meta && CHAT.meta.voice && typeof voiceSay === 'function') {
    SPEECH.node = node;
    el.classList.add('on', 'busy'); el.innerHTML = STOP_SPEAK_ICON; el.dataset.tip = 'Stop reading';
    try {
      await loadVoice();
      if (voiceUsable()) {
        if (SPEECH.node !== node) return;
        await voiceSay(text, { onstart: () => el.classList.remove('busy') });
        if (SPEECH.node === node) stopSpeaking();
        return;
      }
    } catch (e) {
      if (SPEECH.node === node) stopSpeaking();
      toast('Reading aloud failed: ' + e.message, 'bad');
      return;
    }
    if (SPEECH.node !== node) return;
    el.classList.remove('busy');
  }
  if (!window.speechSynthesis) { if (SPEECH.node === node) stopSpeaking(); toast('Reading aloud is not available in this browser.', 'bad'); return; }
  SPEECH.node = node;
  el.classList.add('on'); el.innerHTML = STOP_SPEAK_ICON; el.dataset.tip = 'Stop reading';
  // Sentence-sized pieces: some voices stop after about 15 seconds of one utterance.
  const parts = text.match(/[^.!?]+[.!?]+["')\]]*|[^.!?]+$/g) || [text];
  const voice = pickVoice();
  parts.forEach((p, i) => {
    const u = new SpeechSynthesisUtterance(p.trim());
    if (voice) u.voice = voice;
    u.volume = soundVolume() / 100;
    if (i === parts.length - 1) u.onend = () => { if (SPEECH.node === node) stopSpeaking(); };
    speechSynthesis.speak(u);
  });
};

// ------------------------------------------------------------------ dictation
/**
 * The browser's own speech recognition where it works (Chrome, Edge); otherwise - in the desktop app, Firefox or when the
 * browser's service is unreachable - the microphone is recorded and the server turns it into text (OpenWebUI speech-to-text).
 */
const DICTATION = { active: false, rec: null, media: null, chunks: [], noBrowser: false };
ACTIONS['chat-dictate'] = () => { if (DICTATION.active) stopDictation(); else startDictation(); };
function setMic(on, busy) {
  DICTATION.active = on;
  const b = $('#mic-btn');
  if (!b) return;
  b.classList.toggle('on', on); b.classList.toggle('busy', !!busy);
  b.dataset.tip = on ? 'Listening… click to stop' : busy ? 'Turning speech into text…' : 'Dictate: speak your message';
}
async function startDictation() {
  const SR = window.SpeechRecognition || window.webkitSpeechRecognition;
  const input = $('#chat-input');
  if (!input) return;
  if (SR && !DICTATION.noBrowser && !window.chatClient) {
    const rec = new SR();
    rec.continuous = true; rec.interimResults = true; rec.lang = navigator.language || 'en-US';
    const base = input.value ? input.value.replace(/\s*$/, ' ') : '';
    let finalText = '';
    rec.onresult = ev => {
      let interim = '';
      for (let i = ev.resultIndex; i < ev.results.length; i++) { const r = ev.results[i]; if (r.isFinal) finalText += r[0].transcript; else interim += r[0].transcript; }
      input.value = base + finalText + interim; autosize(); renderContext();
    };
    rec.onerror = ev => {
      if (DICTATION.rec !== rec) return;
      if (ev.error === 'network' || ev.error === 'service-not-allowed' || ev.error === 'language-not-supported') { DICTATION.noBrowser = true; stopDictation(); startDictation(); return; }
      if (ev.error === 'not-allowed') toast('The browser did not allow the microphone.', 'bad');
      else if (ev.error !== 'aborted' && ev.error !== 'no-speech') toast('Dictation stopped: ' + ev.error, 'bad');
      stopDictation();
    };
    rec.onend = () => { if (DICTATION.rec === rec) { DICTATION.rec = null; setMic(false); input.focus(); } };
    DICTATION.rec = rec;
    try { rec.start(); setMic(true); } catch (e) { DICTATION.rec = null; toast(e.message, 'bad'); }
    return;
  }
  if (!navigator.mediaDevices || !window.MediaRecorder) { toast('Dictation needs a microphone and a browser that can record audio.', 'bad'); return; }
  let stream;
  try { stream = await navigator.mediaDevices.getUserMedia({ audio: true }); } catch (e) { toast('No access to the microphone: ' + e.message, 'bad'); return; }
  const type = ['audio/webm;codecs=opus', 'audio/webm', 'audio/ogg;codecs=opus', 'audio/mp4'].find(t => MediaRecorder.isTypeSupported(t)) || '';
  const media = new MediaRecorder(stream, type ? { mimeType: type } : undefined);
  DICTATION.chunks = []; DICTATION.media = media;
  media.ondataavailable = e => { if (e.data && e.data.size) DICTATION.chunks.push(e.data); };
  media.onstop = async () => {
    stream.getTracks().forEach(t => t.stop());
    DICTATION.media = null;
    const blob = new Blob(DICTATION.chunks, { type: (media.mimeType || 'audio/webm').split(';')[0] });
    if (blob.size < 2000) { setMic(false); return; }
    setMic(false, true);
    try {
      const ext = /ogg/.test(blob.type) ? 'ogg' : /mp4/.test(blob.type) ? 'm4a' : 'webm';
      const res = await fetch(`/api/chat/transcribe?name=speech.${ext}&language=${enc((navigator.language || 'en').slice(0, 2))}`, {
        method: 'POST', credentials: 'same-origin', body: blob, headers: { 'Content-Type': blob.type || 'application/octet-stream', 'X-CSRF-Token': S.csrf || '' } });
      const data = await res.json().catch(() => null);
      if (!res.ok) throw new Error(data && data.error ? data.error.message : 'Speech to text failed (' + res.status + ')');
      const box = $('#chat-input');
      if (data && data.text && box) { box.value = (box.value ? box.value.replace(/\s*$/, ' ') : '') + data.text; autosize(); renderContext(); box.focus(); }
      else toast('No speech was recognized.', 'warn');
    } catch (e) { toast(e.message, 'bad'); }
    finally { setMic(false); }
  };
  media.start(1000);
  setMic(true);
}
function stopDictation() {
  const rec = DICTATION.rec; DICTATION.rec = null;
  if (rec) { try { rec.stop(); } catch { /* already stopped */ } }
  if (DICTATION.media && DICTATION.media.state !== 'inactive') { DICTATION.media.stop(); return; }
  setMic(false);
}

// ------------------------------------------------------------------ keyboard shortcuts (the list and its editor: keys.js)
function lastAnswer() { return CHAT.conv && [...CHAT.conv.messages].reverse().find(m => m.role === 'assistant' && m.content); }
function lastCodeBlock() {
  const m = lastAnswer(); if (!m) return null;
  const all = [...m.content.matchAll(/```[^\n]*\n([\s\S]*?)```/g)];
  return all.length ? all[all.length - 1][1].replace(/\n$/, '') : null;
}
document.addEventListener('keydown', ev => {
  if (!$('.chat-app') || ev.isComposing || keyPlainTyping(ev)) return;
  if (keyIs(ev, 'chat.focus')) { ev.preventDefault(); $('#chat-input')?.focus(); }
  else if (keyIs(ev, 'chat.new')) { ev.preventDefault(); navigate('/chat'); }
  else if (keyIs(ev, 'chat.toggleList')) { ev.preventDefault(); ACTIONS['chat-side'](); }
  else if (keyIs(ev, 'chat.personalize')) { ev.preventDefault(); ACTIONS['chat-personalize'](); }
  else if (keyIs(ev, 'chat.copyAnswer')) { const m = lastAnswer(); if (m) { ev.preventDefault(); copyText(m.content, 'Last answer copied'); } }
  else if (keyIs(ev, 'chat.copyCode')) { const code = lastCodeBlock(); if (code !== null) { ev.preventDefault(); copyText(code, 'Last code block copied'); } }
  else if (keyIs(ev, 'chat.delete') && CHAT.conv) { ev.preventDefault(); ACTIONS['chat-delete']({ dataset: { id: CHAT.conv.id } }); }
});

// Another chat opens: stop reading aloud, dictating into the old box and the prompt picker.
document.addEventListener('chat:mounted', () => { stopSpeaking(); hidePromptPop(); if (DICTATION.rec) stopDictation(); });
