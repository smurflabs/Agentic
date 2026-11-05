'use strict';
/* Agentic console - Markdown for chat answers: fenced code (highlighted, runnable), tables, callouts, colors, citations and
   math (LaTeX through KaTeX, as MathML). Used by the Chat tab and by the public page of a shared chat (share.html), so it
   only needs esc, num, enc, icon and $$ from the page. */
const EXPAND_ICON = '<svg viewBox="0 0 24 24" class="ic"><path d="M14 4h6v6M10 20H4v-6M20 4l-7 7M4 20l7-7"/></svg>';

// ------------------------------------------------------------------ markdown with code, callouts, colors, citations
const HL_KEYWORDS = {
  js: 'await async break case catch class const continue default delete do else export extends finally for from function if import in instanceof let new of return static super switch this throw try typeof var void while yield null true false undefined',
  py: 'and as assert async await break class continue def del elif else except False finally for from global if import in is lambda None nonlocal not or pass raise return True try while with yield print self',
  cs: 'abstract as async await base bool break byte case catch char class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sealed short sizeof static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using var virtual void volatile while record init get set',
  vb: 'AddHandler AndAlso As Async Await Boolean ByRef Byte ByVal Case Catch Class Const Dim Do Double Each Else ElseIf End Enum Exit False Finally For Friend Function Get Handles If Implements Imports In Inherits Integer Interface Is IsNot Let Long Loop Me Module MustInherit MustOverride MyBase Namespace New Next Not Nothing NotInheritable Of Option Optional Or OrElse Overrides ParamArray Partial Private Property Protected Public ReadOnly Return Select Set Shared Short Single Static String Structure Sub Then Throw To True Try TypeOf Using When While With',
  sql: 'select from where join inner left right outer on group by order having insert into values update set delete create table alter drop index as and or not null is in like limit offset distinct count sum avg min max case when then else end union all primary key foreign references',
  sh: 'if then else elif fi for while do done case esac function in return export local echo cd ls sudo apt git docker npm node python pip',
  go: 'break case chan const continue default defer else fallthrough for func go goto if import interface map package range return select struct switch type var nil true false',
  rs: 'as async await break const continue crate else enum extern false fn for if impl in let loop match mod move mut pub ref return self Self static struct super trait true type unsafe use where while',
  java: 'abstract boolean break byte case catch char class const continue default do double else enum extends final finally float for if implements import instanceof int interface long new null package private protected public return short static super switch this throw throws true false try void while var',
  css: 'important media keyframes from to root'
};
const LANG_ALIAS = { javascript: 'js', jsx: 'js', ts: 'js', typescript: 'js', tsx: 'js', node: 'js', json: 'js', python: 'py', py3: 'py', csharp: 'cs', 'c#': 'cs', vbnet: 'vb', 'vb.net': 'vb', bash: 'sh', shell: 'sh', zsh: 'sh', powershell: 'sh', ps1: 'sh', golang: 'go', rust: 'rs', c: 'java', cpp: 'java', 'c++': 'java', kotlin: 'java', swift: 'java', php: 'js', ruby: 'py', scss: 'css' };
/** Blocks of plain text (logs, command output, no language given): shown as they are, not coloured as code. */
const PLAIN_LANGS = new Set(['', 'text', 'txt', 'plain', 'plaintext', 'log', 'logs', 'output', 'console', 'stdout', 'stderr', 'none', 'nohighlight']);
/** JSON tokens: a string (a key when a colon follows), true/false, null, a number, or a bracket/comma. */
const JSON_TOKEN = /("(?:[^"\\\n]|\\.)*")(\s*:)?|\b(?:true|false|null)\b|-?\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b|[{}[\],]/g;
/** JSON coloured for reading: keys, strings, numbers, true/false and null each get their own colour (.j-*). Everything is
    escaped; text that is not quite JSON (cut short, say) is still shown whole, only less coloured. */
function jsonColor(text) {
  const s = String(text ?? '');
  if (s.length > 400000) return esc(s);
  let out = '', last = 0, m;
  JSON_TOKEN.lastIndex = 0;
  while ((m = JSON_TOKEN.exec(s))) {
    const t = m[0];
    out += esc(s.slice(last, m.index));
    if (m[1]) out += m[2] ? `<span class="j-k">${esc(m[1])}</span><span class="j-p">${esc(m[2])}</span>` : `<span class="j-s">${esc(t)}</span>`;
    else out += `<span class="j-${t === 'null' ? 'z' : t === 'true' || t === 'false' ? 'b' : /^[{}[\],]$/.test(t) ? 'p' : 'n'}">${esc(t)}</span>`;
    last = m.index + t.length;
  }
  return out + esc(s.slice(last));
}
/** Text that may be JSON (tool arguments and results, stored context): an object or array is laid out and coloured; JSON cut
    short is coloured as it is; anything else is shown as plain text. */
function jsonOr(text) {
  const s = String(text ?? ''), t = s.trim();
  if (!/^[{[]/.test(t)) return esc(s);
  try { const v = JSON.parse(t); if (v && typeof v === 'object') return jsonColor(JSON.stringify(v, null, 2)); } catch { /* not whole JSON */ }
  return /^(\{\s*"|\[\s*[{["])/.test(t) ? jsonColor(s) : esc(s);
}
function highlight(code, lang) {
  if (PLAIN_LANGS.has(String(lang || '').toLowerCase())) return esc(code);
  if (String(lang).toLowerCase() === 'json') return jsonColor(code);
  // A diff: added lines green, removed lines red, hunk headers dim.
  if (lang === 'diff' || lang === 'patch') {
    return code.split('\n').map(x => {
      const c = x.startsWith('+') && !x.startsWith('+++') ? 'tk-add' : x.startsWith('-') && !x.startsWith('---') ? 'tk-del' : x.startsWith('@@') ? 'tk-hunk' : '';
      return c ? `<span class="${c}">${esc(x)}</span>` : esc(x);
    }).join('\n');
  }
  const l = LANG_ALIAS[lang] || lang;
  const kws = new Set((HL_KEYWORDS[l] || HL_KEYWORDS.js).split(' ').map(k => l === 'sql' || l === 'vb' ? k.toLowerCase() : k));
  if (lang === 'html' || lang === 'xml' || lang === 'svg') {
    return esc(code).replace(/(&lt;!--[\s\S]*?--&gt;)/g, '<span class="tk-c">$1</span>')
      .replace(/(&lt;\/?)([\w-]+)/g, '$1<span class="tk-k">$2</span>').replace(/([\w-:]+)=(&quot;[^&]*?&quot;)/g, '<span class="tk-f">$1</span>=<span class="tk-s">$2</span>');
  }
  const comment = l === 'py' || l === 'sh' ? /#[^\n]*/y : l === 'sql' ? /--[^\n]*/y : l === 'vb' ? /'[^\n]*/y : /\/\/[^\n]*|\/\*[\s\S]*?\*\//y;
  const rules = [['c', comment], ['s', /"(?:[^"\\\n]|\\.)*"|'(?:[^'\\\n]|\\.)*'|`(?:[^`\\]|\\.)*`/y], ['n', /\b\d[\d_.]*(?:e[+-]?\d+)?[a-z]*\b|0x[\da-f]+/iy], ['w', /[A-Za-z_$][\w$]*/y]];
  let out = '', i = 0;
  while (i < code.length) {
    let matched = false;
    for (const [type, re] of rules) {
      if (type === 's' && l === 'vb' && code[i] === "'") continue;
      re.lastIndex = i;
      const m = re.exec(code);
      if (!m || !m[0]) continue;
      const t = m[0];
      if (type === 'w') {
        const key = l === 'sql' || l === 'vb' ? t.toLowerCase() : t;
        out += kws.has(key) ? `<span class="tk-k">${esc(t)}</span>` : code[i + t.length] === '(' ? `<span class="tk-f">${esc(t)}</span>` : /^[A-Z]/.test(t) && l !== 'sql' ? `<span class="tk-t">${esc(t)}</span>` : esc(t);
      } else out += `<span class="tk-${type}">${esc(t)}</span>`;
      i += t.length; matched = true; break;
    }
    if (!matched) { out += esc(code[i]); i++; }
  }
  return out;
}
const RUN_BROWSER = new Set(['html', 'htm', 'svg', 'javascript', 'js', 'jsx']);
const RUN_SERVER = { python: 'python', py: 'python', python3: 'python', node: 'node', nodejs: 'node', bash: 'bash', sh: 'bash', shell: 'bash', javascript: 'node', js: 'node' };
/**
 * Markdown for chat answers: fenced code (highlighted, runnable), tables, callouts, colors, highlights, citations.
 * Code fences become placeholders first; quotes and callouts render their lines with the same list of code blocks
 * (outerBlocks), so a fence inside a quote or a list item still shows as code.
 */
function chatMarkdown(src, sources, streaming, outerBlocks) {
  const blocks = outerBlocks || [];
  // Rendered formulas, shared with nested quotes like the code blocks.
  const maths = blocks.maths || (blocks.maths = []);
  let text = String(src || '').replace(/\r\n/g, '\n');
  text = text.replace(/```([\w+#.-]*)[^\n]*\n([\s\S]*?)(```|$)/g, (m, lang, code, close) => {
    blocks.push({ lang: (lang || '').toLowerCase(), code: code.replace(/\n$/, ''), open: !close && streaming });
    return `\u0000${blocks.length - 1}\u0000`;
  });
  if (!outerBlocks) text = extractMath(text, maths);
  const srcMap = Object.fromEntries((sources || []).map(s => [s.index, s]));
  const inline = s => {
    let h = esc(s);
    h = h.replace(/`([^`]+)`/g, '<code>$1</code>')
      .replace(/\*\*([^*]+)\*\*/g, '<b>$1</b>').replace(/__([^_]+)__/g, '<b>$1</b>')
      .replace(/(^|[\s(])\*([^*\s][^*]*?)\*(?=[\s).,;:!?]|$)/g, '$1<i>$2</i>').replace(/(^|[\s(])_([^_\s][^_]*?)_(?=[\s).,;:!?]|$)/g, '$1<i>$2</i>')
      .replace(/~~([^~]+)~~/g, '<s>$1</s>').replace(/==([^=]+)==/g, '<mark>$1</mark>')
      .replace(/&lt;(span|font)\s+(?:style=&quot;\s*color\s*:\s*|color=&quot;)(#[0-9a-fA-F]{3,8}|[a-zA-Z]{3,20})\s*;?\s*&quot;&gt;([\s\S]*?)&lt;\/(?:span|font)&gt;/g, '<span class="md-color" data-color="$2">$3</span>')
      .replace(/!\[([^\]]*)\]\((https?:\/\/[^)\s]+)\)/g, '<a href="$2" target="_blank" rel="noopener noreferrer" class="md-img-link">🖼 $1</a>')
      .replace(/\[([^\]]+)\]\((https?:\/\/[^)\s]+)\)/g, '<a href="$2" target="_blank" rel="noopener noreferrer">$1</a>')
      // A link to a page of this app ("/agents/X?task=Y", from an agent's report): one leading slash only - never "//host" -
      // opened in the app.
      .replace(/\[([^\]]+)\]\((\/(?!\/)[A-Za-z0-9\-._~%!$&;*+,=:@\/?#]*)\)/g, '<a href="$2" data-nav>$1</a>')
      .replace(/(^|[\s(])(https?:\/\/[^\s<)]+)/g, '$1<a href="$2" target="_blank" rel="noopener noreferrer">$2</a>')
      .replace(/\[(\d{1,2})\]/g, (m, n) => srcMap[n] ? `<a class="cite" href="${esc(srcMap[n].url)}" target="_blank" rel="noopener noreferrer" data-tip="${esc(srcMap[n].title)}">${n}</a>` : m);
    return h.replace(/\u0002(\d+)\u0002/g, (m, n) => maths[Number(n)] || '');
  };
  const lines = text.split('\n');
  const isBlock = l => /^\u0000\d+\u0000$/.test(l.trim()) || /^#{1,6}\s/.test(l) || /^\s*([-*+]|\d+[.)])\s+/.test(l) || /^\s*\|.*\|\s*$/.test(l) || /^\s*(---|\*\*\*)\s*$/.test(l) || /^\s*>/.test(l);
  const cells = l => l.trim().replace(/^\|/, '').replace(/\|$/, '').split('|').map(c => c.trim());
  let html = '', i = 0;
  while (i < lines.length) {
    const l = lines[i];
    if (!l.trim()) { i++; continue; }
    const cb = l.trim().match(/^\u0000(\d+)\u0000$/);
    if (cb) { const b = blocks[Number(cb[1])]; if (b) html += codeBlock(b); i++; continue; }
    const h = l.match(/^(#{1,6})\s+(.*)$/);
    if (h) { html += `<h${h[1].length + 1} class="md-h">${inline(h[2])}</h${h[1].length + 1}>`; i++; continue; }
    if (/^\s*(---|\*\*\*)\s*$/.test(l)) { html += '<hr>'; i++; continue; }
    if (/^\s*>/.test(l)) {
      const q = [];
      while (i < lines.length && /^\s*>/.test(lines[i])) q.push(lines[i++].replace(/^\s*>\s?/, ''));
      // Code fenced inside the quote: its lines still carry the quote's "> ".
      q.forEach(x => { for (const m of x.matchAll(/\u0000(\d+)\u0000/g)) { const b = blocks[Number(m[1])]; if (b && !b.unquoted) { b.code = b.code.replace(/^[ \t]*>[ \t]?/gm, '').replace(/\n$/, ''); b.unquoted = true; } } });
      const call = q[0] && q[0].match(/^\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION|INFO|SUCCESS|DANGER)\]\s*(.*)$/i);
      if (call) { const kind = call[1].toLowerCase(); html += `<div class="callout c-${kind}"><div class="callout-title">${esc(call[1][0] + call[1].slice(1).toLowerCase())}${call[2] ? ' · ' + inline(call[2]) : ''}</div>${chatMarkdown(q.slice(1).join('\n'), sources, streaming, blocks)}</div>`; }
      else html += `<blockquote>${chatMarkdown(q.join('\n'), sources, streaming, blocks)}</blockquote>`;
      continue;
    }
    if (/^\s*\|.*\|\s*$/.test(l) && i + 1 < lines.length && /^\s*\|?\s*:?-{2,}/.test(lines[i + 1])) {
      const head = cells(l), align = cells(lines[i + 1]).map(c => /^:-+:$/.test(c) ? 'center' : /-:$/.test(c) ? 'right' : ''); i += 2;
      const rows = [];
      while (i < lines.length && /^\s*\|.*\|\s*$/.test(lines[i])) rows.push(cells(lines[i++]));
      const td = (c, k, tag) => `<${tag}${align[k] ? ` class="ta-${align[k]}"` : ''}>${inline(c)}</${tag}>`;
      html += `<div class="md-table-box"><button class="embed-expand-btn" data-act="embed-expand" data-kind="table" aria-label="Open full screen" data-tip="Open full screen">${EXPAND_ICON}</button><div class="table-wrap md-table"><table><thead><tr>${head.map((c, k) => td(c, k, 'th')).join('')}</tr></thead><tbody>${rows.map(r => `<tr>${r.map((c, k) => td(c, k, 'td')).join('')}</tr>`).join('')}</tbody></table></div></div>`;
      continue;
    }
    const li = l.match(/^(\s*)([-*+]|\d+[.)])\s+(.*)$/);
    if (li) {
      const ordered = /\d/.test(li[2]), items = [];
      while (i < lines.length) {
        const m = lines[i].match(/^(\s*)([-*+]|\d+[.)])\s+(.*)$/);
        if (!m) { if (lines[i].trim() && /^\s{2,}/.test(lines[i]) && items.length) { items[items.length - 1] += '\n' + lines[i].trim(); i++; continue; } break; }
        const task = m[3].match(/^\[( |x)\]\s+(.*)$/i);
        items.push((m[1].length >= 2 ? '\u0001' : '') + (task ? `<input type="checkbox" disabled${task[1].toLowerCase() === 'x' ? ' checked' : ''}> ` + task[2] : m[3])); i++;
      }
      html += `<${ordered ? 'ol' : 'ul'}>${items.map(x => `<li${x.startsWith('\u0001') ? ' class="nested"' : ''}>${x.replace('\u0001', '').split('\n').map(inline).join('<br>').replace(/&lt;input type=&quot;checkbox&quot; disabled( checked)?&gt;/, (m, c) => `<input type="checkbox" disabled${c || ''}>`)}</li>`).join('')}</${ordered ? 'ol' : 'ul'}>`;
      continue;
    }
    const para = [];
    while (i < lines.length && lines[i].trim() && !isBlock(lines[i])) para.push(lines[i++]);
    if (!para.length) para.push(lines[i++]);
    html += `<p>${para.map(inline).join('<br>')}</p>`;
  }
  // A fence that ended up inside a list item or a paragraph (indented under "1. Run this:") is shown as code there.
  return html.replace(/\u0000(\d+)\u0000/g, (m, n) => blocks[Number(n)] ? codeBlock(blocks[Number(n)]) : '');
}
/**
 * LaTeX in answers: $$…$$ and \[…\] as display formulas, \(…\) and $…$ inline (a $ followed by a space, or a closing $
 * followed by a digit, is money, not math). Each formula becomes a placeholder holding KaTeX's MathML, so the Markdown
 * rules never touch it. Code spans are left alone. Without KaTeX (or for a formula it cannot read) the TeX shows as written.
 */
function extractMath(text, maths) {
  const put = (tex, display) => {
    let html = null;
    try { if (window.katex) html = window.katex.renderToString(tex.trim(), { output: 'mathml', throwOnError: false, displayMode: display, strict: 'ignore', trust: false, maxExpand: 500 }); }
    catch { html = null; }
    html = html || `<code class="tex">${esc(display ? '$$' + tex + '$$' : '$' + tex + '$')}</code>`;
    maths.push(display ? `<span class="math-display">${html}</span>` : html);
    return `\u0002${maths.length - 1}\u0002`;
  };
  return text.split(/(`[^`\n]*`)/).map((part, i) => i % 2 ? part : part
    .replace(/\$\$([\s\S]+?)\$\$/g, (m, t) => put(t, true))
    .replace(/\\\[([\s\S]+?)\\\]/g, (m, t) => put(t, true))
    .replace(/\\\(([\s\S]+?)\\\)/g, (m, t) => put(t, false))
    .replace(/(^|[^\\$\w])\$(?=[^\s$])((?:[^$\n\\]|\\.)+?)(?<=[^\s\\])\$(?![\d$\w])/g, (m, pre, t) => pre + put(t, false))).join('');
}
function codeBlock(b) {
  const id = 'cb' + Math.random().toString(36).slice(2, 9);
  const browser = RUN_BROWSER.has(b.lang), server = RUN_SERVER[b.lang];
  const canServer = server && typeof CHAT !== 'undefined' && CHAT.meta && CHAT.meta.codeInterpreter;
  const lines = b.code ? b.code.split('\n').length : 0;
  // A finished Mermaid block is drawn as a picture (drawMermaid, chatgaps.js); "Code" shows its source.
  const mermaid = !b.open && String(b.lang).toLowerCase() === 'mermaid' && typeof drawMermaid === 'function';
  return `<div class="code-block${b.open ? ' open' : ''}${mermaid ? ' mermaid-block' : ''}" id="${id}"${mermaid ? ' data-mermaid' : ''}><div class="code-head"><span class="lang">${esc(b.lang || 'text')}</span>
    ${b.open ? '<span class="writing"><i></i><i></i><i></i> writing</span>' : `<span class="muted lines">${num(lines)} line${lines === 1 ? '' : 's'}</span>`}<span class="spacer"></span>
    ${mermaid ? `<button class="btn xs" data-act="mermaid-toggle" data-block="${id}" hidden>Code</button>` : ''}
    ${!b.open && browser ? `<button class="btn xs" data-act="code-preview" data-block="${id}">${icon('play')} ${b.lang === 'html' || b.lang === 'svg' || b.lang === 'htm' ? 'Preview' : 'Run in browser'}</button>` : ''}
    ${!b.open && canServer ? `<button class="btn xs" data-act="code-run" data-block="${id}" data-lang="${esc(server)}">${icon('bolt')} Run ${esc(server === 'node' ? 'in Node' : server === 'bash' ? 'shell' : 'Python')}</button>` : ''}
    ${!b.open && typeof codeApplyHtml === 'function' ? codeApplyHtml(id) : ''}
    <button class="btn xs ghost" data-act="code-copy" data-block="${id}">Copy</button>
    <button class="btn xs ghost icon-only" data-act="embed-expand" data-kind="code" data-block="${id}" aria-label="Open full screen" data-tip="Open full screen">${EXPAND_ICON}</button></div>
    ${mermaid ? '<div class="mermaid-view"></div>' : ''}<pre><code data-lang="${esc(b.lang)}">${highlight(b.code, b.lang)}</code></pre><textarea class="code-src" hidden>${esc(b.code)}</textarea><div class="code-out"></div></div>`;
}
/** Colors, sandboxes, embeds and highlighting for rendered answers; a failure here never hides the conversation. */
function applyRich(root) {
  try { applyRichParts(root); } catch (e) { console.error('Could not finish drawing', e); }
}
function applyRichParts(root) {
  $$('.md-color[data-color]', root).forEach(e => { e.style.color = e.dataset.color; });
  $$('[data-i]', root).forEach(e => e.style.setProperty('--i', e.dataset.i));
  if (typeof linkFileRefs === 'function') linkFileRefs(root);
  if (typeof drawMermaid === 'function') drawMermaid(root);
}
// Site icons that fail to load become the site's first letter (inline onerror handlers are not allowed by the CSP).
document.addEventListener('error', ev => {
  const img = ev.target;
  if (!img || img.tagName !== 'IMG' || !img.classList.contains('fav')) return;
  const s = document.createElement('span');
  s.className = 'fav fav-letter';
  s.textContent = (img.dataset.host || '?')[0].toUpperCase();
  img.replaceWith(s);
}, true);
function hostOf(url) { try { return new URL(url).hostname.replace(/^www\./, ''); } catch { return ''; } }
function favicon(url) { const h = hostOf(url); return h ? `<img class="fav" src="/api/favicon/${enc(h)}" alt="" loading="lazy" data-host="${esc(h)}">` : '<span class="fav fav-letter">?</span>'; }
