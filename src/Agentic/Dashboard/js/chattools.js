'use strict';
/* How a tool call reads in the chat, like Claude Code: what it did and to what - "Read src/Server/Pipeline.cs",
   "Edit Pipeline.cs +1 −1", "Run dotnet build" - with an edit's diff, a command and its output, or the result, instead of
   raw JSON. Used by the cards in an answer (toolCardHtml), the status line while a tool works, and the approval card. */

const TOOL_VERBS = {
  read_file: 'Read', Read: 'Read', read_page: 'Read', list_dir: 'List', LS: 'List', glob_files: 'Find', Glob: 'Find', grep: 'Search', Grep: 'Search',
  edit_file: 'Edit', Edit: 'Edit', MultiEdit: 'Edit', str_replace_editor: 'Edit', write_file: 'Write', Write: 'Write',
  edit_notebook: 'Edit notebook', NotebookEdit: 'Edit notebook',
  run_command: 'Run', Bash: 'Run', PowerShell: 'Run', run_command_background: 'Start', command_output: 'Check', BashOutput: 'Check',
  kill_command: 'Stop', KillShell: 'Stop', monitor: 'Watch', Monitor: 'Watch',
  search_web: 'Web search', WebSearch: 'Web search', WebFetch: 'Fetch', fetch_url: 'Fetch',
  open_file: 'Open', vscode_diagnostics: 'Problems', vscode_editor_context: 'Editor', agentic_api: 'Agentic API',
  preview: 'Preview', preview_app: 'Preview', run_code: 'Run code', code_interpreter: 'Run code',
  update_todos: 'Tasks', TodoWrite: 'Tasks', remember: 'Remember', forget: 'Forget', generate_image: 'Create image', generate_video: 'Create video',
  share_file: 'Share', take_screenshot: 'Screenshot', load_tools: 'Load tools', computer: 'Computer', list_windows: 'Windows', window: 'Window', clipboard: 'Clipboard', open_on_computer: 'Open', Skill: 'Skill', skill_file: 'Skill', ask_user: 'Ask'
};
const TOOL_GERUNDS = {
  Read: 'Reading', List: 'Listing', Find: 'Finding', Search: 'Searching', Edit: 'Editing', 'Edit notebook': 'Editing', Write: 'Writing',
  Run: 'Running', Start: 'Starting', Check: 'Checking', Stop: 'Stopping', Watch: 'Watching', 'Web search': 'Searching the web:', Fetch: 'Fetching',
  Open: 'Opening', Problems: 'Reading the problems', Editor: 'Reading the editor', 'Agentic API': 'Calling the Agentic API:', Preview: 'Previewing',
  'Run code': 'Running code', Tasks: 'Updating the task list', Remember: 'Remembering', Forget: 'Forgetting', 'Create image': 'Creating an image',
  'Create video': 'Creating a video', Share: 'Sharing', Screenshot: 'Taking a screenshot', Skill: 'Loading the skill'
};
const EDIT_TOOLS = new Set(['edit_file', 'Edit', 'MultiEdit', 'str_replace_editor']);
const WRITE_TOOLS = new Set(['write_file', 'Write']);
const COMMAND_TOOLS = new Set(['run_command', 'Bash', 'PowerShell', 'run_command_background']);
/** Tools whose result is the point (a file, matches, a listing, a command's output so far): shown as text. */
const RESULT_TOOLS = new Set(['read_file', 'Read', 'list_dir', 'LS', 'glob_files', 'Glob', 'grep', 'Grep', 'command_output', 'BashOutput', 'kill_command',
  'KillShell', 'read_page', 'WebFetch', 'vscode_diagnostics', 'vscode_editor_context', 'open_file']);
/** Tools that work on one file: on this computer (VS Code, the desktop app) a click on the path opens it. */
const FILE_TOOLS = new Set(['read_file', 'Read', 'edit_file', 'Edit', 'MultiEdit', 'str_replace_editor', 'write_file', 'Write', 'edit_notebook', 'NotebookEdit', 'open_file']);

function toolArgs(t) { try { const a = JSON.parse(t.arguments || '{}'); return a && typeof a === 'object' ? a : {}; } catch { return {}; } }
function toolFirstLine(s) { const l = String(s ?? '').trim().split(/\r?\n/); return l[0] + (l.length > 1 ? ' …' : ''); }
function toolHumanize(n) { const s = String(n || '').replace(/[_-]+/g, ' ').trim(); return s ? s.charAt(0).toUpperCase() + s.slice(1) : 'Tool'; }
/** "server__tool" (MCP, also "mcp__server__tool"): the server; '' for other tools. */
function toolServerOf(name) { const m = /^(?:mcp__)?(.+?)__(.+)$/.exec(name || ''); return m ? m[1] : ''; }
/** A tool's name for people: its verb ("Read", "Run"), or the humanized tool name of an MCP server's tool. */
function toolVerb(name) {
  if (TOOL_VERBS[name]) return TOOL_VERBS[name];
  const m = /^(?:mcp__)?(.+?)__(.+)$/.exec(name || '');
  return toolHumanize(m ? m[2] : name);
}
/** A long path keeps its end: the file name and as many folders before it as fit. */
function toolShortPath(p, max) {
  const s = String(p || ''), lim = max || 70;
  if (s.length <= lim) return s;
  const parts = s.split(/[\\/]/);
  let out = parts.pop();
  while (parts.length && out.length + parts[parts.length - 1].length + 2 <= lim) out = parts.pop() + '/' + out;
  return '…/' + out;
}
/** What a call acts on: a path, a command, a search and where, an address, an id. */
function toolTarget(name, a) {
  const p = a.path || a.file_path || a.notebook_path || a.file || a.filename || a.folder;
  if ((name === 'grep' || name === 'Grep') && a.pattern) return `"${a.pattern}"` + (p ? ' in ' + p : '');
  if ((name === 'glob_files' || name === 'Glob') && a.pattern) return a.pattern + (p ? ' in ' + p : '');
  if (name === 'agentic_api') return `${String(a.method || 'GET').toUpperCase()} ${a.path || ''}`;
  if (p) return String(p);
  if (a.command) return toolFirstLine(a.command);
  if (a.url) return String(a.url);
  if (a.query) return String(a.query);
  if (a.id || a.bash_id || a.shell_id) return String(a.id || a.bash_id || a.shell_id);
  if (a.description) return String(a.description);
  if (a.code) return toolFirstLine(a.code);
  if (a.prompt) return toolFirstLine(a.prompt);
  return '';
}
/** What the status line says while a tool works: "Reading src/app.js…", "Running dotnet build…". */
function toolStatusText(call) {
  const verb = toolVerb(call.name), target = toolTarget(call.name, toolArgs(call));
  const g = TOOL_GERUNDS[verb] || ('Using ' + verb);
  return `${g}${target ? ' ' + toolShortPath(target, 60) : ''}…`;
}

/** Lines an edit removes and adds; the lines it keeps at the start and end are not counted (a couple show as context). */
function diffLines(oldText, newText) {
  const a = String(oldText ?? '').split('\n'), b = String(newText ?? '').split('\n');
  let s = 0;
  while (s < a.length && s < b.length && a[s] === b[s]) s++;
  let e = 0;
  while (e < a.length - s && e < b.length - s && a[a.length - 1 - e] === b[b.length - 1 - e]) e++;
  return { before: a.slice(Math.max(0, s - 2), s), removed: a.slice(s, a.length - e), added: b.slice(s, b.length - e), after: a.slice(a.length - e, Math.min(a.length, a.length - e + 2)) };
}
function diffHtml(oldText, newText) {
  const d = diffLines(oldText, newText);
  const row = (cls, sign, l) => `<div class="dl ${cls}"><span class="ds">${sign}</span><span class="dt">${esc(l) || ' '}</span></div>`;
  return `<div class="tc-diff">${d.before.map(l => row('ctx', ' ', l)).join('')}${d.removed.map(l => row('del', '−', l)).join('')}` +
    `${d.added.map(l => row('add', '+', l)).join('')}${d.after.map(l => row('ctx', ' ', l)).join('')}</div>`;
}
function toolEdits(name, a) {
  if (name === 'MultiEdit') return (a.edits || []).map(e => ({ old: e.old_string, new: e.new_string }));
  if (EDIT_TOOLS.has(name)) return [{ old: a.old_string ?? a.old_str, new: a.new_string ?? a.new_str }];
  return [];
}
function toolExitCode(result) { const m = /exit code[:\s]+(-?\d+)/i.exec(result || ''); return m ? +m[1] : null; }
function toolLineCount(s) { const t = String(s ?? '').replace(/\s+$/, ''); return t ? t.split('\n').length : 0; }
/** A tool's arguments as a short list (not JSON) when they are simple; JSON otherwise. */
function toolArgsHtml(a) {
  const keys = Object.keys(a);
  if (!keys.length) return '';
  const simple = keys.every(k => a[k] === null || ['string', 'number', 'boolean'].includes(typeof a[k]));
  if (!simple) return `<pre class="tc-code">${jsonColor(JSON.stringify(a, null, 2))}</pre>`;
  return `<dl class="tc-args">${keys.map(k => `<dt>${esc(k)}</dt><dd>${esc(String(a[k]))}</dd>`).join('')}</dl>`;
}

/**
 * How a tool call shows: {verb, target, server, meta, metaClass, body, open}. meta says what came of it in a few words
 * ("+3 −1", "exit 0", "17 lines", or the error); body is what the card shows when opened (a diff, the command and its
 * output, the result); open: shown opened (an edit's diff, a failure). withBody false: body is left out (a closed card's
 * details are drawn only when it opens).
 */
function toolView(t, withBody = true) {
  const name = t.name || '', a = toolArgs(t), running = t.ok === undefined || t.result === undefined;
  const result = String(t.result ?? ''), verb = toolVerb(name), target = toolTarget(name, a);
  let meta = '', metaClass = '', body = '', open = false;
  const edits = toolEdits(name, a);
  if (edits.length) {
    let plus = 0, minus = 0;
    body = edits.map(e => { const d = diffLines(e.old, e.new); plus += d.added.length; minus += d.removed.length; return withBody ? diffHtml(e.old, e.new) : ''; }).join('');
    meta = `<span class="plus">+${plus}</span> <span class="minus">−${minus}</span>`;
    open = !running && t.ok !== false;
  } else if (WRITE_TOOLS.has(name)) {
    const content = String(a.content ?? '');
    meta = `${num(toolLineCount(content))} line${toolLineCount(content) === 1 ? '' : 's'}`;
    if (withBody) body = `<pre class="tc-code">${esc(content.split('\n').slice(0, 400).join('\n'))}</pre>`;
  } else if (COMMAND_TOOLS.has(name)) {
    // The exit code is in the card's line ("exit 1"): the output leaves out the line that says it.
    const out = withBody ? result.replace(/^\s*exit code:?\s*-?\d+\.?[^\S\n]*(\r?\n|$)/i, '').replace(/\r?\n\(exit code -?\d+\)\s*$/i, '') : '';
    if (withBody) body = `<div class="tc-cmd"><span class="tc-prompt">$</span><code>${esc(a.command || '')}</code></div>` +
      (running ? '' : `<pre class="tc-out">${jsonOr(out.trim() || '(no output)')}</pre>`);
    if (!running) {
      const code = toolExitCode(result);
      if (/still running after|in the background (as|with id)|running in background with id/i.test(result)) meta = 'in the background';
      // A command that failed shows its output (the error is what you want to see).
      else if (code !== null) { meta = `exit ${code}`; if (code !== 0) { metaClass = 'bad'; open = true; } }
    }
  } else if (RESULT_TOOLS.has(name)) {
    if (withBody) body = (running ? '' : `<pre class="tc-out">${jsonOr(result.trim() || '(nothing)')}</pre>`);
    if (!running && t.ok !== false) {
      const n = toolLineCount(result);
      if (/^(no matches|no files|nothing found|\(no new output\))/i.test(result.trim())) meta = 'nothing found';
      else if (verb === 'Read') meta = `${num(n)} line${n === 1 ? '' : 's'}`;
      else if (verb === 'Search' || verb === 'Find' || verb === 'List') meta = `${num(n)} result${n === 1 ? '' : 's'}`;
    }
  } else if (withBody) {
    body = toolArgsHtml(a) + (running ? '' : `<pre class="tc-out">${jsonOr(result.trim() || '(nothing)')}</pre>`);
  }
  const file = toolLocalFile(t, a, edits);
  if (!running && t.ok === false) {
    // A command that exited with an error says "exit 1" (its output is in the card); other failures say the error.
    const code = COMMAND_TOOLS.has(name) ? toolExitCode(result) : null;
    meta = code !== null ? `exit ${code}` : (esc(toolFirstLine(result).slice(0, 160)) || 'failed'); metaClass = 'bad';
    // Opened to show the error, unless its line already says all of it.
    open = code !== null || toolLineCount(result) > 1 || result.trim().length > 160;
    return { verb, target, server: toolServerOf(name), meta, metaClass, body, open, metaIsHtml: true, file };
  }
  return { verb, target, server: toolServerOf(name), meta, metaClass, body, open, metaIsHtml: true, file };
}

// ------------------------------------------------------------------ files on this computer open where you work
/** VS Code opens a file in the editor, the desktop app shows it in its folder; null in the browser. */
function localFileOpener() {
  const c = window.chatClient;
  return c && typeof c.openFile === 'function' ? c : null;
}
/**
 * The file a call worked on, when it ran on this computer and can be opened here: {path, line, find} (find: the text an
 * edit wrote, so the editor shows the edit). Null for the server's own tools, MCP tools and other computers' calls.
 */
function toolLocalFile(t, a, edits) {
  const c = localFileOpener();
  if (!c || t.ok !== true || !FILE_TOOLS.has(t.name || '')) return null;
  const dev = typeof CHAT !== 'undefined' && CHAT.settings && CHAT.settings.device;
  if (dev && c.deviceId && dev !== c.deviceId) return null;
  const path = String(a.path || a.file_path || a.notebook_path || '');
  if (!path) return null;
  const firstLine = s => (String(s ?? '').split(/\r?\n/).find(l => l.trim()) || '').trim().slice(0, 200);
  const e = (edits || []).find(x => firstLine(x.new));
  return { path, line: Number(a.line || a.offset) || 0, find: e ? firstLine(e.new) : '' };
}
function localFileAttrs(f) {
  if (!f) return '';
  return ` data-act="open-local-file" data-path="${esc(f.path)}" data-line="${f.line || ''}" data-find="${esc(f.find || '')}" role="link" tabindex="0"`;
}
function localFileTip() { const c = localFileOpener(); return (c && c.openFileLabel) || 'Open'; }
ACTIONS['open-local-file'] = async el => {
  const c = localFileOpener();
  if (!c) return;
  await c.openFile({ path: el.dataset.path, line: Number(el.dataset.line) || 0, find: el.dataset.find || '' });
};
// A file link reached with Tab opens with Enter, like a link.
document.addEventListener('keydown', ev => {
  const el = ev.key === 'Enter' && ev.target && ev.target.closest && ev.target.closest('[data-act="open-local-file"][role="link"]');
  if (!el) return;
  ev.preventDefault();
  el.click();
});
/**
 * In an answer, `src/app.js` written as code is a link to the file when a call in this chat worked on that file on this
 * computer (so it is a real file here): like Claude Code's file references.
 */
/**
 * The files this chat's calls worked on, kept between drawings: rebuilt only when the conversation gained messages or calls.
 * (Building it on every drawing - each frame of an answer being written - read every call of a long chat again and again,
 * which made the desktop app and VS Code lag.) found: what a written name resolved to, so each is looked up once.
 */
let FILE_REFS = { key: '', known: new Map(), names: new Map(), found: new Map() };
function fileRefIndex() {
  const msgs = CHAT.conv.messages || [];
  const last = msgs[msgs.length - 1];
  const key = `${CHAT.conv.id}|${msgs.length}|${last ? last.id : ''}|${last && last.toolCalls ? last.toolCalls.length : 0}`;
  if (FILE_REFS.key === key) return FILE_REFS;
  const known = new Map(), names = new Map();
  for (const m of msgs) {
    for (const t of m.toolCalls || []) {
      const f = toolLocalFile(t, toolArgs(t), toolEdits(t.name, toolArgs(t)));
      if (!f) continue;
      const norm = f.path.replace(/\\/g, '/');
      known.set(norm, f.path);
      const base = norm.slice(norm.lastIndexOf('/') + 1);
      names.set(base, names.has(base) && names.get(base) !== f.path ? null : f.path);
    }
  }
  FILE_REFS = { key, known, names, found: new Map() };
  return FILE_REFS;
}
function linkFileRefs(root) {
  if (!root || !localFileOpener() || typeof CHAT === 'undefined' || !CHAT.conv) return;
  const codes = $$('.msg .md code', root);
  if (!codes.length) return;
  const { known, names, found } = fileRefIndex();
  if (!known.size) return;
  codes.forEach(code => {
    if (code.closest('pre, a, .file-ref')) return;
    const text = code.textContent.trim().replace(/\\/g, '/');
    const m = /^(.+?)(?::(\d+))?$/.exec(text);
    if (!m) return;
    let path = found.get(m[1]);
    if (path === undefined) {
      path = known.get(m[1]) || names.get(m[1]) || null;
      // "Server/Pipeline.cs" for src/Server/Pipeline.cs, or the full path "C:/code/shop/src/Server/Pipeline.cs".
      if (!path) { for (const [k, v] of known) { if (k.endsWith('/' + m[1]) || m[1].endsWith('/' + k)) { path = v; break; } } }
      found.set(m[1], path);
    }
    if (!path) return;
    code.classList.add('file-ref');
    code.dataset.act = 'open-local-file';
    code.dataset.path = path;
    if (m[2]) code.dataset.line = m[2];
    code.dataset.tip = `${localFileTip()} ${path}`;
    code.setAttribute('role', 'link');
    code.tabIndex = 0;
  });
}

// ------------------------------------------------------------------ a run of reads folds into one line
/** Calls that only look (no change). Three or more in a row that worked, with nothing written between them, fold into
 *  one line in a finished answer ("Read 3 files, searched for 2 patterns"), like Claude Code; a click shows each one. */
const LOOK_WORDS = {
  Read: k => `read ${k} file${k === 1 ? '' : 's'}`,
  Search: k => `searched for ${k} pattern${k === 1 ? '' : 's'}`,
  Find: k => `looked for files ${k} time${k === 1 ? '' : 's'}`,
  List: k => `listed ${k} folder${k === 1 ? '' : 's'}`,
  Fetch: k => `read ${k} page${k === 1 ? '' : 's'}`,
  'Web search': k => `searched the web ${k} time${k === 1 ? '' : 's'}`,
  Check: k => `checked ${k} command${k === 1 ? '' : 's'}`,
  Problems: k => `read the problems${k > 1 ? ` ${k} times` : ''}`,
  Editor: k => `read the editor${k > 1 ? ` ${k} times` : ''}`
};
const TOOL_GROUP_MIN = 3;
function toolGroupable(t) {
  return !!t && t.ok === true && t.result !== undefined && !!TOOL_VERBS[t.name] && !!LOOK_WORDS[toolVerb(t.name)];
}
function toolGroupText(calls) {
  const counts = new Map();
  calls.forEach(t => { const v = toolVerb(t.name); counts.set(v, (counts.get(v) || 0) + 1); });
  const s = [...counts].map(([v, k]) => LOOK_WORDS[v](k)).join(', ');
  return s.charAt(0).toUpperCase() + s.slice(1);
}
function toolGroupHtml(calls) {
  return `<details class="tool-group"><summary>${icon('check')}<span class="tg-text">${esc(toolGroupText(calls))}</span>` +
    `<svg viewBox="0 0 24 24" class="ic chev"><path d="M6 9l6 6 6-6"/></svg></summary><div class="tg-list">${calls.map(toolCardHtml).join('')}</div></details>`;
}

/** The approval card's title: "Run dotnet build?", "Edit src/app.js?", "Use github › create_issue?". */
function approvalTitleHtml(a) {
  const verb = toolVerb(a.name), target = toolTarget(a.name, argsOf(a.arguments)), server = toolServerOf(a.name);
  return `${esc(verb)}${server ? ` <span class="tc-server">${esc(server)}</span>` : ''}${target ? ` <code>${esc(toolShortPath(target, 80))}</code>` : ''}?`;
}

const MODEL_WORDS = { gpt: 'GPT', glm: 'GLM', llm: 'LLM', ai: 'AI', vl: 'VL', moe: 'MoE', deepseek: 'DeepSeek', qwen: 'Qwen', llama: 'Llama',
  mistral: 'Mistral', mixtral: 'Mixtral', claude: 'Claude', gemini: 'Gemini', gemma: 'Gemma', grok: 'Grok', kimi: 'Kimi', minimax: 'MiniMax', phi: 'Phi' };
/**
 * A model id people can read when it has no display name: "sev/claude-opus-5-5" becomes "Claude Opus 5.5",
 * "gpt-4o-mini" "GPT-4o Mini", "deepseek-v4.1-flash" "DeepSeek V4.1 Flash". An id in capitals (FLINT) stays as it is.
 */
// ------------------------------------------------------------------ ideas on the new-chat screen
/** A hundred things to start with: [chip label, text put in the message box]. A new chat shows six of them, picked at random. */
const WELCOME_IDEAS = [
  ['Draft an email', 'Draft a short, clear email to '],
  ['Write a cover letter', 'Write a cover letter for this job, based on my experience: '],
  ['Polish my writing', 'Improve the clarity and flow of this text without changing its meaning:\n\n'],
  ['Make it shorter', 'Shorten this to half its length, keeping every key point:\n\n'],
  ['Write a LinkedIn post', 'Write a LinkedIn post about '],
  ['Draft a polite reply', 'Write a polite but firm reply to this message:\n\n'],
  ['Product description', 'Write a punchy product description for '],
  ['Tagline ideas', 'Give me 10 tagline ideas for '],
  ['Write a toast', 'Write a short, warm toast for '],
  ['Explain it to a kid', 'Explain this so a 10-year-old understands it:\n\n'],
  ['Clean up meeting notes', 'Turn these rough notes into clean meeting minutes with decisions and action items:\n\n'],
  ['Outline a blog post', 'Outline a blog post about '],
  ['Write a press release', 'Write a press release announcing '],
  ['Fix my grammar', 'Fix the grammar and spelling in this text:\n\n'],
  ['Write a thank-you note', 'Write a heartfelt thank-you note to '],
  ['Research a topic', '/search '],
  ['Explain from scratch', 'Explain from scratch, with an example: '],
  ['Compare two options', 'Compare these two options in a table (pros, cons, cost, best for): '],
  ['Latest news on…', '/search latest news on '],
  ['Summarize a document', 'Summarize the attached document: the key points, decisions and open questions.'],
  ['Teach me something', 'Teach me this in 5 minutes, then quiz me: '],
  ['Find good sources', '/search reliable sources about '],
  ['A short history of…', 'Give me a short history of '],
  ['Pros and cons', 'List the pros and cons of '],
  ['Fact-check a claim', 'Fact-check this claim and cite sources: '],
  ['Explain a paper', 'Explain the main idea, method and results of this paper: '],
  ['Say it in Spanish', 'How do I say this naturally in Spanish: '],
  ['Define a term', 'Define this term simply, with an example: '],
  ['Make flashcards', 'Make 15 flashcards (question → answer) about '],
  ['Summarize a book', 'Summarize the key ideas of the book '],
  ["What's the difference?", 'What is the difference between '],
  ['Deep research', '/research '],
  ['Market overview', '/search market overview of '],
  ['How does it work?', 'How does this work, step by step: '],
  ['Build a timeline', 'Build a timeline of the key events in '],
  ['Analyze data', 'Analyze this data and show what stands out: '],
  ['Make a chart', 'Make a chart from this data: '],
  ['Spot trends', 'What trends and outliers do you see in this data?\n\n'],
  ['SWOT analysis', 'Do a SWOT analysis of '],
  ['Size a market', 'Estimate the market size for '],
  ['Calculate ROI', 'Calculate the return on this investment, step by step: '],
  ['Clean up a CSV', 'Clean up this CSV (headers, dates, duplicates) and explain the changes:\n\n'],
  ['Write a SQL query', 'Write a SQL query that '],
  ['Excel formula', 'Write an Excel formula that '],
  ['Decision matrix', 'Build a weighted decision matrix for choosing between '],
  ['Make a budget', 'Make a monthly budget from these numbers: '],
  ['Forecast numbers', 'Forecast the next 6 months from this data and explain the method:\n\n'],
  ['Survey insights', 'Summarize the key insights from these survey answers:\n\n'],
  ['Assess the risks', 'List the main risks, their likelihood and impact, and how to reduce them for '],
  ['Compare costs', 'Compare the yearly cost of these options: '],
  ['Debug my code', 'Find the bug in this code and fix it:\n\n'],
  ['Explain this code', 'Explain what this code does, step by step:\n\n'],
  ['Write a script', 'Write a script that '],
  ['Write a regex', 'Write a regular expression that matches '],
  ['Review my code', 'Review this code for bugs, security issues and readability:\n\n'],
  ['Write unit tests', 'Write unit tests for this function:\n\n'],
  ['Build a web page', 'Build a single-page HTML app that '],
  ['Port to Python', 'Convert this code to Python:\n\n'],
  ['Speed up a query', 'Make this SQL query faster and explain why:\n\n'],
  ['Design an API', 'Design a REST API (endpoints, payloads, errors) for '],
  ['Shell one-liner', 'Give me a shell one-liner to '],
  ['Explain an error', 'What does this error mean and how do I fix it?\n\n'],
  ['Make a small game', 'Build a small browser game: '],
  ['Git help', 'Which git commands do I need to '],
  ['Write a Dockerfile', 'Write a Dockerfile for '],
  ['Architecture advice', 'Suggest an architecture for '],
  ['Add comments', 'Add clear comments and docstrings to this code:\n\n'],
  ['Refactor this', 'Refactor this code to be simpler and easier to read:\n\n'],
  ['Security check', 'Review this for security vulnerabilities:\n\n'],
  ['Draw a diagram', 'Draw a Mermaid diagram of '],
  ['Plan my week', 'Help me plan my week. My priorities are: '],
  ['Make a checklist', 'Make a checklist for '],
  ['Break down a project', 'Break this project into milestones and tasks: '],
  ['Prep for a meeting', 'Help me prepare for a meeting about '],
  ['Set clear goals', 'Turn this into specific, measurable goals: '],
  ['Plan a trip', 'Plan a 5-day trip to '],
  ['Make a study plan', 'Make a 4-week study plan for '],
  ['Prioritize my tasks', 'Prioritize these tasks by impact and urgency:\n\n'],
  ['Interview prep', 'Give me likely interview questions, with strong answers, for a '],
  ['Negotiation prep', 'Help me prepare to negotiate '],
  ['Write an agenda', 'Write a 30-minute meeting agenda for '],
  ['Build a habit', 'Help me build a habit of '],
  ['Plan an event', 'Plan an event: '],
  ['Help me decide', 'Help me decide: '],
  ['Pitch an idea', 'Help me pitch this idea in 60 seconds: '],
  ['Brainstorm ideas', 'Brainstorm 15 ideas for '],
  ['Write a short story', 'Write a short story about '],
  ['Write a poem', 'Write a short poem about '],
  ['Name my project', 'Suggest 20 names for '],
  ['Plan healthy dinners', 'Plan a week of healthy dinners for '],
  ['Cook from my fridge', 'What can I cook with: '],
  ['Gift ideas', 'Suggest thoughtful gift ideas for '],
  ['Workout plan', 'Make a 3-days-a-week workout plan for '],
  ['Surprising facts', 'Tell me 5 surprising facts about '],
  ['Write a riddle', 'Write a clever riddle about '],
  ['Make a quiz', 'Make a 10-question quiz about '],
  ['Write song lyrics', 'Write song lyrics about '],
  ['Party game ideas', 'Suggest party games for '],
  ['Logo concepts', 'Describe 3 logo concepts for '],
  ['Bedtime story', 'Write a short bedtime story about ']
];
/** n of the ideas, at random and all different. */
function randomWelcomeIdeas(n, maxChars = 84) {
  const a = WELCOME_IDEAS.slice();
  for (let i = a.length - 1; i > 0; i--) { const j = Math.floor(Math.random() * (i + 1)); [a[i], a[j]] = [a[j], a[i]]; }
  // One line in a normal chat column: random ideas whose labels fit maxChars together (the shortest fill in if needed).
  const pick = [];
  let used = 0;
  for (const idea of a) {
    if (pick.length === n) break;
    if (used + idea[0].length <= maxChars - (n - pick.length - 1) * 9) { pick.push(idea); used += idea[0].length; }
  }
  for (const idea of a.filter(x => !pick.includes(x)).sort((x, y) => x[0].length - y[0].length)) { if (pick.length === n) break; pick.push(idea); }
  return pick;
}

/** A message's start as plain words for the chat list: no Markdown marks, code fences ("```text") or link addresses. */
function previewText(s) {
  return String(s || '').replace(/```[\w+.-]*/g, ' ').replace(/`/g, '').replace(/!?\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/^\s*(#{1,6}|>|[-*+]|\d+\.)\s+/gm, '').replace(/\*\*|__|~~|\*/g, '').replace(/\|/g, ' ').replace(/\s+/g, ' ').trim();
}
/** A model's own display name, or '' when it only repeats its id ("gpt-5" for "sev/gpt-5"). */
function modelDisplayName(m) {
  const n = m && m.name ? String(m.name) : '', id = String((m && m.id) || '');
  return n && n !== id && n !== id.slice(id.lastIndexOf('/') + 1) ? n : '';
}
function prettyModelId(id) {
  const raw = String(id || '');
  const base = raw.slice(raw.lastIndexOf('/') + 1);
  if (!base || base === base.toUpperCase()) return base || raw;
  const s = base.replace(/(\d+)-(\d+)(?=$|-)/g, '$1.$2');
  const words = s.split(/[-_ ]+/).filter(Boolean).map(w => {
    const lw = w.toLowerCase();
    if (MODEL_WORDS[lw]) return MODEL_WORDS[lw];
    if (/^\d+(\.\d+)?b$/i.test(w)) return w.toUpperCase();
    if (/^v\d/i.test(w)) return 'V' + w.slice(1);
    if (/^\d/.test(w) || /^o\d/i.test(w)) return w;
    return w.charAt(0).toUpperCase() + w.slice(1);
  });
  return words.join(' ').replace(/^GPT (\S+)/, 'GPT-$1');
}


/** A subagent's step as the server words it ("read_file src/app.js") with the tool's verb ("Read src/app.js"). */
function friendlyStep(text) {
  const m = /^([A-Za-z][\w.-]*)(\s|$)/.exec(text || '');
  if (!m) return text;
  const name = m[1];
  if (!TOOL_VERBS[name] && !/__/.test(name)) return text;
  return toolVerb(name) + text.slice(name.length);
}

// ------------------------------------------------------------------ the task list above the message box while it works
/**
 * While an answer works through a task list, the list stays in view above the message box (like Claude Code's): the step
 * it is on, and how far it got. It goes away when the answer ends; the card in the answer keeps the final list.
 */
function setLiveTodos(chatId, call) {
  CHAT.liveTodos = CHAT.liveTodos || new Map();
  if (call) CHAT.liveTodos.set(chatId, call); else CHAT.liveTodos.delete(chatId);
  renderTodoDock();
}
function renderTodoDock() {
  const c = $('#composer');
  let dock = $('#todo-dock');
  const call = c && CHAT.conv && CHAT.liveTodos && CHAT.liveTodos.get(CHAT.conv.id);
  const list = call ? todosOf(call) : [];
  if (!list.length) { if (dock) dock.remove(); return; }
  if (!dock) { dock = document.createElement('div'); dock.id = 'todo-dock'; dock.className = 'todo-dock'; c.parentNode.insertBefore(dock, c); }
  const done = list.filter(x => x.status === 'completed').length;
  const now = list.find(x => x.status === 'in_progress') || list.find(x => x.status !== 'completed');
  const open = dock.querySelector('details') ? dock.querySelector('details').open : false;
  const pct = Math.round(100 * done / list.length);
  const prev = dock.querySelector('.td-progress i');
  dock.innerHTML = `<details${open ? ' open' : ''}><summary><span class="td-progress" role="progressbar" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${pct}"><i></i></span>
      <b>${done} of ${list.length}</b>${now ? `<span class="td-now">${esc(now.content)}</span>` : '<span class="td-now muted">All done</span>'}</summary>
    <ul class="td-list">${list.map(x => `<li class="td-${esc(x.status)}">${TODO_ICONS[x.status] || TODO_ICONS.pending}<span>${esc(x.content)}</span></li>`).join('')}</ul></details>`;
  // The page's security policy ignores style attributes in markup: the bar's width is set here (it slides from where it was).
  const bar = dock.querySelector('.td-progress i');
  bar.style.width = prev ? prev.style.width : '0%';
  requestAnimationFrame(() => { bar.style.width = pct + '%'; });
}

// ------------------------------------------------------------------ what an answer changed
/** "Changed 2 files +4 −1" under an answer that edited files: each file with its lines added and removed; a click shows its edit. */
function changesSummaryHtml(m) {
  const files = new Map();
  for (const t of m.toolCalls || []) {
    if (t.ok !== true) continue;
    const a = toolArgs(t), path = a.path || a.file_path || a.notebook_path;
    if (!path) continue;
    const edits = toolEdits(t.name, a);
    if (!edits.length && !WRITE_TOOLS.has(t.name)) continue;
    const f = files.get(path) || { plus: 0, minus: 0, id: t.id, file: toolLocalFile(t, a, edits) };
    if (edits.length) edits.forEach(e => { const d = diffLines(e.old, e.new); f.plus += d.added.length; f.minus += d.removed.length; });
    else f.plus += toolLineCount(a.content);
    files.set(path, f);
  }
  if (!files.size) return '';
  let plus = 0, minus = 0;
  files.forEach(f => { plus += f.plus; minus += f.minus; });
  return `<details class="changes-sum"><summary><svg viewBox="0 0 24 24" class="ic"><path d="M4 20h4L19 9l-4-4L4 16zM13 7l4 4"/></svg>Changed ${files.size} file${files.size === 1 ? '' : 's'}
      <span class="plus">+${plus}</span><span class="minus">−${minus}</span></summary>
    <div class="changes-list">${[...files].map(([p, f]) => `<div class="change-line"><button type="button" class="change-row" data-act="goto-call" data-id="${esc(f.id)}" data-tip="Show the change"><span class="cr-path">${esc(toolShortPath(p, 80))}</span><span class="plus">+${f.plus}</span><span class="minus">−${f.minus}</span></button>` +
      (f.file ? `<button type="button" class="btn xs ghost icon-only cr-open" aria-label="${esc(localFileTip())}" data-tip="${esc(localFileTip())} ${esc(p)}"${localFileAttrs(f.file).replace(' role="link" tabindex="0"', '')}><svg viewBox="0 0 24 24" class="ic"><path d="M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5"/></svg></button>` : '') + '</div>').join('')}</div></details>`;
}
ACTIONS['bubble-more'] = el => {
  const b = el.closest('.bubble-u');
  if (!b) return;
  const open = b.classList.toggle('expanded');
  el.setAttribute('aria-expanded', String(open));
  el.firstChild.textContent = open ? 'Show less ' : 'Show more ';
};
ACTIONS['goto-call'] = el => {
  const card = document.querySelector(`.flow [data-call="${CSS.escape(el.dataset.id || '')}"]`);
  if (!card) return;
  const group = card.closest('details.tool-group');
  if (group) group.open = true;
  if (card.tagName === 'DETAILS') card.open = true;
  card.scrollIntoView({ behavior: reducedMotion() ? 'auto' : 'smooth', block: 'center' });
  card.classList.remove('flash'); void card.offsetWidth; card.classList.add('flash');
};
