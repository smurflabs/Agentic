'use strict';
/* Agentic mobile layout: phones get the same app as the desktop (every page, every Chat control); css/mobile.css lays it
   out for a narrow screen. This script adds what CSS cannot do:
   - the height of what is visible (it shrinks when the on-screen keyboard opens), and hiding the tab bar while typing
   - the bottom tab bar: which tab is current, and the Inbox count
   - the first letter of the user's name on the round user button
   - the chat list as a drawer: closed when a chat opens, closed by tapping beside it
   - the room above the composer for the menus that open from its controls (reasoning effort, permission mode, tools) */
(function mobileLayout() {
  const narrow = matchMedia('(max-width: 900px)');
  const root = document.documentElement;
  const q = (s, r = document) => r.querySelector(s);

  // ---- the visible height (keyboard aware) -------------------------------------------------------------------------
  const vv = window.visualViewport;
  function syncHeight() {
    if (!narrow.matches) { root.style.removeProperty('--app-h'); return; }
    const h = vv ? vv.height : window.innerHeight;
    root.style.setProperty('--app-h', Math.round(h) + 'px');
    // iOS pans the page up to show a focused field; with the app sized to the visible area there is nothing to pan to.
    if (root.classList.contains('kb-open') && vv && vv.offsetTop > 0 && window.scrollY > 0) window.scrollTo(0, 0);
  }
  if (vv) { vv.addEventListener('resize', syncHeight); vv.addEventListener('scroll', syncHeight); }
  window.addEventListener('resize', syncHeight);
  narrow.addEventListener('change', syncHeight);

  const typing = el => el && (el.isContentEditable || el.tagName === 'TEXTAREA' || el.tagName === 'SELECT' ||
    (el.tagName === 'INPUT' && !['checkbox', 'radio', 'range', 'button', 'submit', 'reset', 'file', 'color'].includes(el.type)));
  let blurTimer = 0;
  document.addEventListener('focusin', ev => {
    if (!narrow.matches || !typing(ev.target)) return;
    clearTimeout(blurTimer);
    root.classList.add('kb-open');
    syncHeight();
  });
  document.addEventListener('focusout', () => {
    // Focus often moves straight to another field: wait a moment before bringing the tab bar back.
    clearTimeout(blurTimer);
    blurTimer = setTimeout(() => { if (!typing(document.activeElement)) { root.classList.remove('kb-open'); syncHeight(); } }, 120);
  });

  // ---- bottom tab bar ----------------------------------------------------------------------------------------------
  function syncTabs() {
    const path = location.pathname;
    document.querySelectorAll('#tabbar [data-tab]').forEach(t => {
      const tab = t.dataset.tab;
      const on = tab === '/' ? path === '/' : tab !== 'menu' && (path === tab || path.startsWith(tab + '/'));
      t.classList.toggle('active', on);
      if (on) t.setAttribute('aria-current', 'page'); else t.removeAttribute('aria-current');
    });
  }
  window.addEventListener('popstate', () => setTimeout(syncTabs, 0));
  document.addEventListener('click', ev => { if (ev.target.closest && ev.target.closest('a[data-nav]')) setTimeout(syncTabs, 0); });

  function syncInbox() {
    const pill = q('#nav-approvals'), badge = q('#tab-approvals');
    if (!pill || !badge) return;
    const n = parseInt(pill.textContent, 10) || 0;
    badge.textContent = n > 99 ? '99+' : String(n);
    badge.classList.toggle('hidden', pill.classList.contains('hidden') || n === 0);
  }

  function syncInitial() {
    const who = q('#whoami');
    if (!who) return;
    const name = (who.textContent || '').trim();
    who.dataset.initial = name ? name[0].toUpperCase() : '?';
    who.setAttribute('aria-label', 'Account: ' + (name || 'signed in'));
  }

  // ---- drawers ---------------------------------------------------------------------------------------------------
  // The page behind an open drawer does not scroll.
  function syncDrawer() { root.classList.toggle('drawer-open', narrow.matches && !!q('#sidebar.open')); }

  /** The chat list covers the conversation on a phone: it starts closed and closes once a chat is chosen. */
  function closeChatList() {
    const app = q('.chat-app');
    if (narrow.matches && app && !app.classList.contains('side-closed')) app.classList.add('side-closed');
  }
  document.addEventListener('chat:mounted', closeChatList);
  // Made narrow while open (a VS Code side bar dragged smaller, a window snapped to half): the list would cover the chat.
  narrow.addEventListener('change', e => { if (e.matches) closeChatList(); });

  /** Menus that open from the composer's controls get the room between the composer and the chat's header. */
  document.addEventListener('click', ev => {
    if (!narrow.matches || !ev.target.closest || !ev.target.closest('.composer-bar')) return;
    const composer = q('#composer'), head = q('.chat-top');
    if (!composer) return;
    const above = composer.getBoundingClientRect().top - (head ? head.getBoundingClientRect().bottom : 0);
    root.style.setProperty('--composer-room', Math.max(140, Math.round(above - 16)) + 'px');
  }, true);
  document.addEventListener('click', ev => {
    if (!narrow.matches) return;
    const app = q('.chat-app');
    if (!app) return;
    // A tap on the shade beside the open chat list (the list's ::after overlay belongs to .chat-app itself).
    if (ev.target === app && !app.classList.contains('side-closed')) { app.classList.add('side-closed'); return; }
    if (ev.target.closest && ev.target.closest('.chat-side [data-act="chat-new"], .chat-side a.chat-item')) setTimeout(closeChatList, 0);
  });

  // ---- start -----------------------------------------------------------------------------------------------------
  document.addEventListener('DOMContentLoaded', () => {
    syncHeight(); syncTabs(); syncInbox(); syncInitial();
    const watch = (sel, fn, opts) => { const el = q(sel); if (el) new MutationObserver(fn).observe(el, opts); };
    watch('#nav-approvals', syncInbox, { childList: true, characterData: true, subtree: true, attributes: true, attributeFilter: ['class'] });
    watch('#whoami', syncInitial, { childList: true, characterData: true, subtree: true });
    watch('#sidebar', syncDrawer, { attributes: true, attributeFilter: ['class'] });
    // Every page change renders into #main: the tab bar follows.
    watch('#main', syncTabs, { childList: true });
  });
})();
