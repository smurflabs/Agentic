'use strict';
/* Agentic console - Chat: hand the conversation to the workforce. A user who may manage tasks (tasks.manage) or global
   tasks (global.tasks) can turn a chat into a task for one agent, or a global task across all agents, with the
   conversation attached as context. They write the prompt; the chat goes along so the agents have the background. */

function canDelegate() { return can('tasks.manage') || can('global.tasks'); }

/** The conversation as plain text for an agent: "You"/"the assistant" turns, newest kept, tool and question noise dropped. */
function chatTranscript(max = 12000) {
  const msgs = (CHAT.conv && CHAT.conv.messages) || [];
  const lines = msgs.filter(m => (m.content || '').trim()).map(m => `${m.role === 'user' ? 'User' : 'Assistant'}: ${m.content.trim()}`);
  let text = lines.join('\n\n');
  if (text.length > max) text = '…(earlier messages left out)…\n\n' + text.slice(text.length - max);
  return text;
}

ACTIONS['chat-delegate'] = async () => {
  if (!CHAT.conv) { toast('Open a conversation first.', 'bad'); return; }
  $('#chat-more-menu')?.classList.add('hidden');
  const taskOk = can('tasks.manage'), globalOk = can('global.tasks');
  let agents = [];
  if (taskOk) { try { agents = await get('/api/agents'); } catch (e) { /* may be none */ } }
  const scopes = [];
  if (taskOk && agents.length) scopes.push(['agent', 'One agent']);
  if (globalOk) scopes.push(['global', 'All agents (global task)']);
  if (!scopes.length) { toast(taskOk ? 'No agents are available to take a task.' : 'You do not have permission to create agent tasks.', 'bad'); return; }
  const def = scopes[0][0];
  const title = (CHAT.conv.title && CHAT.conv.title !== 'New chat') ? CHAT.conv.title : '';
  openModal('Hand this chat to agents', `<form id="delegate-form" class="form-grid" data-form="chat-delegate">
      <div class="full" data-errors></div>
      <p class="full muted">Give the work to your agents with this conversation as context. Write what they should do; the chat goes along so they have the background.</p>
      ${scopes.length > 1 ? `<div class="full seg-radios">${scopes.map(([v, l], i) => `<label class="check"><input type="radio" name="scope" value="${v}"${i === 0 ? ' checked' : ''} data-change="delegate-scope"><span>${esc(l)}</span></label>`).join('')}</div>` : `<input type="hidden" name="scope" value="${def}">`}
      ${taskOk && agents.length ? `<label class="full" data-scope="agent"${def === 'agent' ? '' : ' hidden'}>Agent<select name="agentId">${agents.map(a => `<option value="${esc(a.id)}">${esc(a.name)}${a.role ? ' — ' + esc(a.role) : ''} · ${esc(a.state)}</option>`).join('')}</select></label>` : ''}
      <label class="full">Task name<input name="name" required maxlength="200" value="${esc(title)}" placeholder="Short, descriptive name"></label>
      <label class="full">What should they do?<textarea name="prompt" rows="6" required placeholder="The goal, constraints and what the result should look like. The conversation below is attached as background."></textarea></label>
      <label class="check full"><input type="checkbox" name="includeChat" checked data-change="delegate-ctx"> Attach this conversation as context</label>
      <label class="full" data-ctx>Context sent to the agents<textarea name="context" rows="7" class="mono">${esc(chatTranscript())}</textarea><span class="help">Edit or trim it. Leave the box above unticked to send none.</span></label>
      <label>Priority<select name="priority"><option>Low</option><option selected>Normal</option><option>High</option><option>Critical</option></select></label>
      <label data-scope="global"${def === 'global' ? '' : ' hidden'}>Max agents<input name="maxAgents" type="number" min="1" max="20" value="4"><span class="help">Upper limit; the plan uses as few as needed</span></label>
      <label class="check full"><input type="checkbox" name="reportBack" checked> Post the result back into this chat when it is done
        <span class="help">You can keep chatting meanwhile; the agents' answer arrives here as a message you can ask about.</span></label>
    </form><div data-errors></div>`,
    `<button class="btn" data-act="modal-close">Cancel</button><button class="btn primary" data-act="submit-form" data-form-id="delegate-form">${icon('layers')} Create</button>`, 'wide');
};
CHANGES['delegate-scope'] = el => {
  const scope = el.value, card = el.closest('.modal-card');
  $$('[data-scope]', card).forEach(x => x.classList.toggle('hidden', x.dataset.scope !== scope));
};
CHANGES['delegate-ctx'] = el => { const box = el.closest('.modal-card').querySelector('[data-ctx]'); if (box) box.classList.toggle('hidden', !el.checked); };
FORMS['chat-delegate'] = async (f, data) => {
  const prompt = (data.prompt || '').trim();
  if (!prompt) throw new Error('Write what the agents should do.');
  const fd = new FormData(f);
  const ctx = (fd.get('includeChat') === 'on' && (data.context || '').trim())
    ? `${prompt}\n\n--- Context: a conversation from the Chat tab ---\n${data.context.trim()}` : prompt;
  // Reporting back keeps you in the chat: the hand-over and, later, the result appear here as messages from the agents.
  const back = fd.get('reportBack') === 'on' && CHAT.conv ? { chatId: CHAT.conv.id } : {};
  if (data.scope === 'global') {
    const r = await post('/api/global-tasks', Object.assign({ name: data.name, description: ctx, priority: data.priority, maxAgents: Number(data.maxAgents || 4), agentIds: [] }, back));
    closeModal();
    if (back.chatId) toast('Global task created — its final answer will be posted here', 'ok');
    else { toast('Global task created — planning…', 'ok'); navigate('/global-tasks/' + enc(r.id)); }
  } else {
    if (!data.agentId) throw new Error('Choose an agent.');
    const r = await post(`/api/agents/${enc(data.agentId)}/task`, Object.assign({ name: data.name, description: ctx, handler: 'reasoning', priority: data.priority }, back));
    closeModal();
    if (back.chatId) toast(`Task queued for ${data.agentId} — its result will be posted here`, 'ok');
    else { toast('Task queued for ' + data.agentId + ': ' + r.taskId, 'ok'); navigate(`/agents/${enc(data.agentId)}?task=${enc(r.taskId)}`); }
  }
};

// ------------------------------------------------------------------ agents' work → a new chat about it
/** Opens a new chat about a task, a global task or a result: the work is its first message, then you ask about it. */
ACTIONS['discuss-work'] = async el => {
  const d = el.dataset, body = d.result ? { resultId: d.result } : d.global ? { globalTaskId: d.global } : { agentId: d.agent, taskId: d.task };
  let r;
  try { r = await post('/api/chat/discuss', body); } catch (e) { toast(e.message, 'bad'); return; }
  closeModal();
  toast('Opened a chat about it — ask away', 'ok');
  navigate('/chat/' + enc(r.id));
};
/** The "Discuss in chat" button for agents' work, shown to people who may chat. */
function discussButton(attrs, small) {
  return can('models.use') ? `<button class="btn${small ? ' xs' : ''}" data-act="discuss-work" ${attrs}>${icon('layers')} Discuss in chat</button>` : '';
}

// A /command and a composer entry, so it is reachable without opening the … menu.
if (typeof CHAT_COMMANDS !== 'undefined') {
  CHAT_COMMANDS.push({ name: 'delegate', group: 'Work', desc: 'Hand this chat to an agent or all agents as a task', when: canDelegate, run: () => ACTIONS['chat-delegate']() });
  if (typeof EXTERNAL_COMMANDS !== 'undefined') EXTERNAL_COMMANDS.add('delegate');
}
