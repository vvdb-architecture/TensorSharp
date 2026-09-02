// TensorAgent's additions to TensorSharp.Server's Web UI, injected by the loopback
// server after the page's own script. The page itself is served byte-for-byte from the
// Server's wwwroot; everything the app needs beyond it lives here:
//
//   * session resume: the app opens the page with ?conversation=<id>, this script binds
//     the engine session to that conversation and re-renders its saved messages through
//     the page's own bubble builders (so a resumed chat looks exactly like a live one);
//   * native attachments: the app's camera / microphone / file flows upload through the
//     same /api/upload route and hand the response to TensorAgent.addAttachment, which
//     puts it in the page's pending list;
//   * dictation: TensorAgent.insertText appends recognised speech to the composer;
//   * WKWebView has no alert()/confirm() UI, so those become in-page notices.
//
// The page declares its state with top-level let/function bindings, which a later
// <script> in the same document shares, so no page change is needed.
(function () {
  'use strict';

  const params = new URLSearchParams(location.search);
  const requested = params.get('conversation');
  const state = { conversationId: requested && requested !== 'new' ? requested : null, ready: false };

  // ---- alert / confirm without a WKUIDelegate ------------------------------------
  function notice(text, kind) {
    let host = document.getElementById('ta-notices');
    if (!host) {
      host = document.createElement('div');
      host.id = 'ta-notices';
      host.style.cssText = 'position:fixed;left:50%;bottom:96px;transform:translateX(-50%);z-index:9999;display:flex;flex-direction:column;gap:8px;max-width:90vw;pointer-events:none';
      document.body.appendChild(host);
    }
    const el = document.createElement('div');
    el.textContent = text;
    el.style.cssText = 'background:' + (kind === 'error' ? '#7a2b2b' : '#2a2f45') + ';color:#fff;padding:10px 14px;border-radius:10px;font-size:14px;box-shadow:0 4px 16px rgba(0,0,0,.35);pointer-events:auto';
    host.appendChild(el);
    setTimeout(() => el.remove(), 5000);
  }
  window.alert = (m) => notice(String(m), 'error');
  // confirm() cannot be made asynchronous; the two confirmations in the page (skill
  // delete) are re-implemented below as an in-page dialog.
  window.confirm = () => true;

  function askConfirm(text) {
    return new Promise(resolve => {
      const overlay = document.createElement('div');
      overlay.style.cssText = 'position:fixed;inset:0;background:rgba(0,0,0,.55);z-index:9998;display:flex;align-items:center;justify-content:center';
      const box = document.createElement('div');
      box.style.cssText = 'background:#1c2033;color:#fff;padding:18px 20px;border-radius:14px;max-width:80vw;font-size:15px';
      const p = document.createElement('div'); p.textContent = text; p.style.marginBottom = '14px';
      const row = document.createElement('div'); row.style.cssText = 'display:flex;gap:10px;justify-content:flex-end';
      const no = document.createElement('button'); no.textContent = 'Cancel';
      const yes = document.createElement('button'); yes.textContent = 'Delete'; yes.className = 'danger';
      [no, yes].forEach(b => b.style.cssText = 'padding:8px 14px;border-radius:8px;border:0;font-size:14px');
      no.onclick = () => { overlay.remove(); resolve(false); };
      yes.onclick = () => { overlay.remove(); resolve(true); };
      row.append(no, yes); box.append(p, row); overlay.append(box); document.body.appendChild(overlay);
    });
  }

  if (typeof deleteSkill === 'function') {
    const original = deleteSkill;
    // Same signature the modal's buttons call; the page's version asks confirm() first.
    deleteSkill = async function (idx) {
      const sk = (typeof skillsRoster !== 'undefined' && skillsRoster[idx]) || null;
      const name = sk ? (sk.name || sk.id || 'this skill') : 'this skill';
      if (!(await askConfirm('Delete skill "' + name + '" from this device?'))) return;
      const realConfirm = window.confirm;
      window.confirm = () => true;
      try { return await original(idx); } finally { window.confirm = realConfirm; }
    };
  }

  // ---- session binding -----------------------------------------------------------
  // The page creates one engine session per load; the app needs to know which saved
  // conversation that session belongs to, so the create call carries the id (or 'new').
  createSession = async function () {
    try {
      const target = state.conversationId ? state.conversationId : 'new';
      const res = await fetch('/api/sessions?conversation=' + encodeURIComponent(target), { method: 'POST' });
      const data = await res.json();
      if (data && data.sessionId) currentSessionId = data.sessionId;
      if (data && data.conversationId) {
        state.conversationId = data.conversationId;
        postNative({ type: 'conversation', id: state.conversationId });
      }
    } catch (e) {
      console.error('Failed to create chat session:', e);
      currentSessionId = null;
    }
  };

  // After the page's "New Chat" the next createSession must mint a fresh conversation.
  if (typeof clearChat === 'function') {
    const originalClear = clearChat;
    clearChat = async function () {
      state.conversationId = null;
      await originalClear();
    };
  }

  // ---- resume ----------------------------------------------------------------------
  function renderThinking(assistDiv, thinking) {
    if (!thinking) return;
    const bubble = assistDiv.querySelector('.bubble');
    const text = bubble.querySelector('.bubble-text');
    const block = document.createElement('div');
    block.className = 'thinking-block';
    const header = document.createElement('div');
    header.className = 'thinking-header';
    header.innerHTML = '<span class="arrow">&#9654;</span> Reasoning';
    header.onclick = () => toggleThinking(header);
    const content = document.createElement('div');
    content.className = 'thinking-content';
    content.textContent = thinking;
    block.append(header, content);
    const on = document.getElementById('reasoning-toggle');
    if (!(on && on.checked)) header.style.display = 'none';
    bubble.insertBefore(block, text);
  }

  function renderAssistant(msg, idx) {
    const assistDiv = addAssistantBubble();
    assistDiv.dataset.idx = idx;
    const bubble = assistDiv.querySelector('.bubble');
    const text = bubble.querySelector('.bubble-text');
    text.textContent = msg.content || '';
    renderThinking(assistDiv, msg.thinking);
    const typing = assistDiv.querySelector('.typing-indicator');
    if (typing) typing.remove();
    if (msg.imageUrl) {
      const img = document.createElement('img');
      img.src = msg.imageUrl;
      img.style.cssText = 'max-width:100%;max-height:512px;border-radius:10px;display:block;margin-top:8px';
      bubble.insertBefore(img, assistDiv.querySelector('.stats'));
    }
    if (msg.artifacts && msg.artifacts.length && typeof appendArtifactFiles === 'function') {
      try { appendArtifactFiles(assistDiv, msg.artifacts); } catch (e) { console.warn(e); }
    }
    if (typeof renderArtifactLinks === 'function') {
      try { renderArtifactLinks(text); } catch (e) { /* older page */ }
    }
    if (msg.stats) assistDiv.querySelector('.stats').textContent = msg.stats;
    const actions = document.createElement('div');
    actions.className = 'msg-actions';
    actions.innerHTML = '<button class="danger" onclick="revertFrom(' + idx + ')" title="Remove this response">&#x21A9; Revert</button>';
    bubble.appendChild(actions);
  }

  function loadConversation(conv) {
    if (!conv || !Array.isArray(conv.messages)) return;
    chatHistory = [];
    chatContainer.innerHTML = '';
    if (typeof pendingAttachments !== 'undefined') { pendingAttachments = []; attachmentsDiv.innerHTML = ''; }
    conv.messages.forEach((m, idx) => {
      const entry = { role: m.role, content: m.content };
      ['imagePaths', 'stillImagePaths', 'videoFilePaths', 'audioPaths', 'textFilePaths', 'textFileNames'].forEach(k => {
        if (Array.isArray(m[k]) && m[k].length) entry[k] = m[k].slice();
      });
      if (m.isVideo) entry.isVideo = true;
      if (m.role === 'assistant' && m.thinking) entry.thinking = m.thinking;
      chatHistory.push(entry);
      if (m.role === 'user') {
        addUserBubble(m.display || m.content, m.attachments || [], idx);
      } else {
        renderAssistant(m, idx);
      }
    });
    const toggle = document.getElementById('reasoning-toggle');
    if (toggle && typeof conv.think === 'boolean') toggle.checked = conv.think;
    if (Array.isArray(conv.skills) && typeof setSelectedSkills === 'function') {
      try { setSelectedSkills(conv.skills); } catch (e) { console.warn(e); }
    }
    // A resumed conversation always starts from a fresh engine session: nothing is in
    // the KV cache yet, so the first turn re-prefills (the flag keeps the page honest).
    needsCacheReset = true;
    if (chatHistory.length === 0 && typeof renderEmptyState === 'function') renderEmptyState();
    scrollChatToBottom();
  }

  async function resume() {
    if (!state.conversationId) return;
    try {
      const res = await fetch('/api/agent/conversations/' + encodeURIComponent(state.conversationId));
      if (!res.ok) return;
      loadConversation(await res.json());
    } catch (e) {
      console.error('resume failed', e);
    }
  }

  // fetchServerState() ran at the end of the page script and is creating the session
  // right now; wait for it, then hydrate.
  async function whenReady() {
    for (let i = 0; i < 200 && !currentSessionId; i++) await new Promise(r => setTimeout(r, 25));
    await resume();
    state.ready = true;
    postNative({ type: 'ready', conversation: state.conversationId });
  }

  // ---- copy that only makes sense on a server --------------------------------------
  // The page is served byte-for-byte from TensorSharp.Server, and its empty state
  // tells the reader to restart the server with --model. On a phone there is no
  // command line and no server to restart: the model is chosen in the app's own
  // Models list. Rewriting the sentence here keeps index.html unforked.
  function retitleEmptyState() {
    const replacement = 'No model yet. Open Models in the menu above, pick one that fits this '
      + 'device, and download it; the chat starts working as soon as it is ready.';
    document.querySelectorAll('p').forEach(p => {
      if (/Start TensorSharp\.Server with/.test(p.textContent || '')) {
        p.textContent = replacement;
      }
    });
  }

  // The page rebuilds its empty state whenever the model state is refetched, so the
  // rewrite has to survive that rather than run once at load.
  const observer = new MutationObserver(retitleEmptyState);
  observer.observe(document.body, { childList: true, subtree: true });
  retitleEmptyState();

  // ---- native bridge ---------------------------------------------------------------
  function postNative(message) {
    try {
      // The app polls /api/agent/events; the WebView route below is the cheap path.
      fetch('/api/agent/events', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(message) }).catch(() => {});
    } catch (e) { /* ignore */ }
  }

  window.TensorAgent = {
    get conversationId() { return state.conversationId; },
    get ready() { return state.ready; },
    loadConversation,
    notice,
    /** Add an /api/upload response object to the composer's pending attachments. */
    addAttachment(att) {
      if (!att || !att.ok) { notice((att && att.error) || 'Upload failed', 'error'); return; }
      pendingAttachments.push(att);
      renderAttachments();
    },
    /** Append dictated text to the composer. */
    insertText(text) {
      if (!text) return;
      const cur = messageInput.value;
      messageInput.value = cur && !/\s$/.test(cur) ? cur + ' ' + text : cur + text;
      messageInput.dispatchEvent(new Event('input'));
      messageInput.focus();
    },
    send() { return sendMessage(); },
    stop() { if (typeof abortGeneration === 'function') abortGeneration(); },
    isGenerating() { return isGenerating; },
    setThink(on) { const t = document.getElementById('reasoning-toggle'); if (t) t.checked = !!on; },
    setSkills(names) { if (typeof setSelectedSkills === 'function') setSelectedSkills(names || []); },
    history() { return chatHistory; },
  };

  whenReady();
})();
