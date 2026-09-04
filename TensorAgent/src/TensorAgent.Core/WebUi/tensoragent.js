// ============================================================================
// TensorAgent — the phone client.
//
// It speaks the same loopback API the desktop Web UI speaks, so every capability
// the app has is still reachable; what is different is the shape of the surface
// around it. Written as one file with no dependencies because it is served from
// the app bundle over 127.0.0.1 and a build step for a single page would be a
// cost with no return.
// ============================================================================
(function () {
  'use strict';

  var $ = function (id) { return document.getElementById(id); };
  var chat = $('chat'), text = $('text'), send = $('send'), busy = $('busy');
  var modelBtn = $('model'), hold = $('hold'), abc = $('abc');

  var state = {
    model: null, arch: null, backend: null,
    session: null, conversation: null,
    history: [],            // {role, content, attachments}
    attachments: [],        // /api/upload responses
    skills: [],             // selected skill names
    catalogSkills: [],
    conversations: [],      // the saved chats, for the menu
    generating: false,
    abort: null,
    // The id of the generation the HOST is running for this conversation. It is not
    // the same thing as `abort`, and that is the whole point: aborting stops this
    // page reading, while the turn keeps going and can be attached to again.
    turn: null,
    liveView: null,         // the assistant bubble a turn is being rendered into
    resuming: false,
    modelInfo: null,        // { id, name, state, loading, error } from /api/agent/engine
    modelWatch: 0,
    maxTokens: 2048,
    speech: '',            // BCP-47 for dictation; empty follows the device
    settings: null,
    native: false,         // true when the page is inside the app, not a browser
    netMsg: '',            // the host's own wording for a network refusal
    voice: false,          // the composer is the hold-to-talk button
    // Whether the model reasons before answering. It used to be a switch under the
    // composer; it is a Settings choice now ("Show reasoning by default"), because a
    // permanent control for something a user decides once is a poor trade for the only
    // row of chrome a phone composer has. Re-read whenever the chat comes back to the
    // front, so changing it in Settings applies to the very next message.
    think: false,
  };

  // ---- tiny helpers --------------------------------------------------------
  function el(tag, cls, txt) {
    var n = document.createElement(tag);
    if (cls) n.className = cls;
    if (txt != null) n.textContent = txt;
    return n;
  }
  function post(url, body) {
    return fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body || {}),
    });
  }
  function atBottom() { return chat.scrollHeight - chat.scrollTop - chat.clientHeight < 90; }
  function toBottom() { chat.scrollTop = chat.scrollHeight; }

  // ---- the layout follows the VISIBLE viewport ----------------------------
  // The single most important line in this file for a phone. Without it the
  // keyboard pushes the composer below the fold and WebKit scrolls the document
  // to chase it, taking the conversation off the top of the screen.
  var vv = window.visualViewport, pendingVh = 0;
  function applyVh() {
    document.documentElement.style.setProperty('--vh', (vv ? vv.height : window.innerHeight) + 'px');
    if (window.scrollY !== 0) window.scrollTo(0, 0);
  }
  function scheduleVh() {
    if (pendingVh) return;
    pendingVh = requestAnimationFrame(function () { pendingVh = 0; applyVh(); });
  }
  if (vv) { vv.addEventListener('resize', scheduleVh); vv.addEventListener('scroll', scheduleVh); }
  window.addEventListener('orientationchange', function () { setTimeout(applyVh, 200); });
  applyVh();

  var stickBottom = true;
  chat.addEventListener('scroll', function () { stickBottom = atBottom(); });
  if (vv) vv.addEventListener('resize', function () { if (stickBottom) setTimeout(toBottom, 60); });
  text.addEventListener('focus', function () { if (stickBottom) { setTimeout(toBottom, 60); setTimeout(toBottom, 350); } });

  // ---- markdown ------------------------------------------------------------
  // Deliberately small: fenced code, inline code, bold/italic, links, headings
  // and lists. Everything is escaped first, so a model that emits HTML cannot
  // put nodes into this page.
  function esc(s) {
    return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  }
  function render(md) {
    var out = '', rest = String(md == null ? '' : md), fence = /```([a-zA-Z0-9_+-]*)\n([\s\S]*?)(?:```|$)/;
    var m;
    while ((m = fence.exec(rest))) {
      out += inline(rest.slice(0, m.index));
      out += '<pre><code>' + esc(m[2]) + '</code></pre>';
      rest = rest.slice(m.index + m[0].length);
    }
    return out + inline(rest);
  }
  function inline(s) {
    var t = esc(s);
    t = t.replace(/`([^`\n]+)`/g, function (_, c) { return '<code>' + c + '</code>'; });
    t = t.replace(/\*\*([^*\n]+)\*\*/g, '<strong>$1</strong>');
    t = t.replace(/(^|[^*])\*([^*\n]+)\*/g, '$1<em>$2</em>');
    // Images before links: an image is a link with a bang in front of it.
    t = t.replace(/!\[([^\]]*)\]\(([^)\s]+)\)/g, '<img alt="$1" src="$2">');
    t = t.replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, '<a href="$2" target="_blank" rel="noopener">$1</a>');
    t = t.replace(/^### (.*)$/gm, '<strong>$1</strong>');
    t = t.replace(/^## (.*)$/gm, '<strong>$1</strong>');
    t = t.replace(/^# (.*)$/gm, '<strong>$1</strong>');
    return t.split(/\n{2,}/).map(function (p) {
      return '<p>' + p.replace(/\n/g, '<br>') + '</p>';
    }).join('');
  }

  // ---- attachments, as the transcript holds them ---------------------------
  //
  // An attachment survives a chat being closed only if what is saved is enough to
  // rebuild it, and for a long time it was not: the page sent the model its file
  // paths and told the transcript nothing, so reopening a chat gave back the words
  // and a blank where the photo had been. What is saved now is the chip itself —
  // the stored name, the name the user knows it by, what kind of thing it is — and
  // every URL is derived from the stored name rather than remembered, so a saved
  // chat cannot point at an address that has moved.

  /** Where the loopback server serves an upload from, by its stored name. */
  function uploadUrl(name) {
    return name ? '/uploads/' + encodeURIComponent(name) : '';
  }
  /** The stored name out of a /uploads/ URL, for a reply that gave us one. */
  function uploadName(url) {
    if (!url) return '';
    try { return decodeURIComponent(String(url).split('/').pop()); } catch (e) { return String(url).split('/').pop(); }
  }
  /** The file itself: the original the user attached. */
  function fileUrlOf(a) { return a.url || uploadUrl(a.file); }
  /** What to SHOW. A HEIC has a PNG beside it because no browser renders HEIC. */
  function previewOf(a) {
    return a.previewUrl || (a.previewFile ? uploadUrl(a.previewFile) : fileUrlOf(a));
  }
  /** The chip, reduced to what has to survive: no page-session URLs, no file text. */
  function chipOf(a) {
    var chip = {
      file: a.file,
      fileName: a.fileName || a.file,
      mediaType: a.mediaType || 'text',
    };
    var preview = a.previewFile || (a.previewUrl ? uploadName(a.previewUrl) : '');
    if (preview) chip.previewFile = preview;
    if (a.frames && a.frames.length) chip.frames = a.frames.slice();
    if (typeof a.pageCount === 'number') chip.pageCount = a.pageCount;
    if (typeof a.extractedPageCount === 'number') chip.extractedPageCount = a.extractedPageCount;
    if (typeof a.renderedAsImages === 'boolean') chip.renderedAsImages = a.renderedAsImages;
    return chip;
  }

  /**
   * What the user typed, out of a message that also carries a file's content.
   *
   * A text upload is sent to the model as "[File: notes.md] … [End of file]" in front
   * of the question, because that is how the model reads it. Rendering that back into
   * the bubble on a resumed chat would show the user a wall of their own file where
   * their sentence used to be. Mirrors Conversation.StripFileEnvelopes on the host.
   */
  function displayText(content) {
    var text = content == null ? '' : String(content);
    if (text.indexOf('[File: ') !== 0) return text;
    var end = text.lastIndexOf('[End of file]');
    return end < 0 ? text : text.slice(end + '[End of file]'.length).replace(/^\s+/, '');
  }

  // ---- the transcript ------------------------------------------------------
  function clearEmpty() { var e = $('empty'); if (e) e.remove(); }

  /** One attachment, rendered as the thing it is rather than as a paperclip. */
  function attachmentNode(a) {
    var kind = a.mediaType || 'text';
    if (kind === 'image') {
      var img = document.createElement('img');
      img.src = previewOf(a); img.alt = a.fileName || 'image';
      img.loading = 'lazy';
      return img;
    }
    if (kind === 'audio') {
      var audio = document.createElement('audio');
      audio.controls = true; audio.preload = 'none'; audio.src = fileUrlOf(a);
      return audio;
    }
    if (kind === 'video') {
      var video = document.createElement('video');
      video.controls = true; video.preload = 'none'; video.playsInline = true;
      if (a.frames && a.frames.length) video.poster = uploadUrl(a.frames[0]);
      video.src = fileUrlOf(a);
      return video;
    }
    // A document is a link, not a label. It was a label, and a user who reopened a
    // chat could see that they had attached a PDF and had no way to open it.
    var link = document.createElement('a');
    link.className = 'filechip';
    link.href = fileUrlOf(a);
    link.target = '_blank';
    link.rel = 'noopener';
    link.textContent = (kind === 'pdf' ? '📕 ' : '📄 ') + (a.fileName || a.file);
    return link;
  }

  function addTurn(role, content, attachments, extra) {
    clearEmpty();
    var turn = el('div', 'turn ' + (role === 'user' ? 'me' : 'bot'));
    var b = el('div', 'bubble');
    if (attachments && attachments.length) {
      attachments.forEach(function (a) {
        if (a && a.file) b.appendChild(attachmentNode(a));
      });
    }
    if (content) {
      var body = el('div');
      body.innerHTML = render(content);
      b.appendChild(body);
    }
    if (extra && extra.imageUrl) {
      var made = document.createElement('img');
      made.src = extra.imageUrl; made.alt = 'generated image';
      b.appendChild(made);
    }
    turn.appendChild(b);
    chat.appendChild(turn);
    // The files this turn produced, put back the way the live turn showed them.
    // They are the point of the turn far more often than the prose is.
    if (extra && extra.artifacts) extra.artifacts.forEach(function (f) { fileLine({ turn: turn, bubble: b }, f); });
    if (stickBottom) toBottom();
    return { turn: turn, bubble: b };
  }

  // What the assistant is doing, and what it did.
  //
  // The desktop page shows a live activity block and DELETES it when the step
  // finishes, which suits a wide screen you are watching. On a phone the useful
  // thing is the opposite: a short trace that stays, so a user who looked away can
  // see that it read a skill, ran a script and edited a file, without scrolling
  // through the raw output of each. Live status while it runs; one line per step
  // once it is done.
  var TOOL_LABEL = {
    shell: ['Generating code', 'Running code'],
    apply_patch: ['Preparing patch', 'Applying patch'],
    read_file: ['Preparing read', 'Reading file'],
    edit_file: ['Preparing edit', 'Editing file'],
    write_file: ['Preparing file', 'Writing file'],
    skills_list: ['Preparing lookup', 'Checking skills'],
    skills_read: ['Preparing read', 'Reading skill'],
    skills_run: ['Preparing run', 'Running skill'],
  };
  function labelFor(tool, phase) {
    var pair = TOOL_LABEL[tool];
    if (pair) return pair[phase === 'writing' ? 0 : 1];
    var name = tool ? String(tool).replace(/_/g, ' ') : 'operation';
    return (phase === 'writing' ? 'Preparing ' : 'Running ') + name;
  }

  // ---- what it is doing RIGHT NOW ------------------------------------------
  //
  // A turn can spend a minute between the question and the first word of the
  // answer: reading a skill, writing a program, running it, reading the output.
  // For all of that the bubble is empty, and an empty bubble on a phone is
  // indistinguishable from an app that has died. So the turn carries a live
  // panel — the step it is on, and the last few lines of whatever text it is
  // producing, be that its reasoning or the command it is typing — which is
  // taken down the moment the answer exists. Shown by default: it is not detail
  // for the curious, it is the only evidence the app is working.
  var TAIL_LINES = 3, TAIL_CHARS = 400;

  /** The last few non-blank lines of a growing text, bounded so a single unbroken
   *  paragraph cannot push the composer off the screen. */
  function tailOf(text) {
    var lines = String(text == null ? '' : text).split('\n');
    var kept = [];
    for (var i = lines.length - 1; i >= 0 && kept.length < TAIL_LINES; i--) {
      if (lines[i].trim().length) kept.unshift(lines[i]);
    }
    var s = kept.join('\n');
    return s.length > TAIL_CHARS ? '…' + s.slice(s.length - TAIL_CHARS) : s;
  }

  // Both of these are called once per TOKEN, so both compare before they write.
  // Setting the same textContent again is a style recalculation and a scroll on every
  // token of a two-thousand-token answer, on the device with the least to spare.
  //
  // The panel is ONE element pinned under the message box, not one per turn. It began
  // inside the turn, which is where the activity happens — and that is exactly why it
  // did not work: by the time a program has been written and run, the answer is
  // streaming and the top of the turn is several screens up, so the user had to scroll
  // away from the words arriving to find out whether anything was happening. Under the
  // thumb, above the keyboard, it is the same information without the scroll.
  var activity = $('activity');
  var activityLabel = activity.querySelector('.label');
  var activityTail = activity.querySelector('.tail');
  var shownLabel = null, shownTail = null, shownClass = null;

  function progress(label, kind) {
    var cls = 'on' + (kind ? ' ' + kind : '');
    if (shownClass !== cls) { shownClass = cls; activity.className = cls; }
    if (label && shownLabel !== label) {
      shownLabel = label;
      activityLabel.textContent = label;
    }
    return activity;
  }
  function progressTail(text) {
    var tail = tailOf(text);
    if (shownTail === tail) return;
    shownTail = tail;
    activityTail.textContent = tail;
  }
  function progressDone() {
    shownLabel = shownTail = null;
    shownClass = '';
    activity.className = '';
    activityLabel.textContent = '';
    activityTail.textContent = '';
  }

  /** One line's worth of a longer string: collapsed, trimmed, elided. */
  function shorten(s, n) {
    var t = String(s == null ? '' : s).replace(/\s+/g, ' ').trim();
    return t.length > n ? t.slice(0, n - 1) + '…' : t;
  }

  function stepLine(view, cls, text) {
    var line = el('div', 'step ' + cls);
    line.appendChild(el('span', 'dot'));
    line.appendChild(el('span', 'txt', text));
    view.turn.insertBefore(line, view.bubble);
    if (stickBottom) toBottom();
    return line;
  }

  // A file a skill script or a command produced. Rendered from the frame rather
  // than from the model's answer, because a small model repeats a download link
  // erratically and the file is the thing the user actually asked for.
  function fileLine(view, file) {
    if (!file || !file.url) return;
    var line = el('div', 'step file');
    line.appendChild(el('span', 'dot'));
    var a = document.createElement('a');
    a.href = file.url;
    a.target = '_blank';
    a.rel = 'noopener';
    a.textContent = '📄 ' + (file.name || 'file')
      + (file.bytes ? ' · ' + Math.max(1, Math.round(file.bytes / 1024)) + ' KB' : '');
    line.appendChild(a);
    view.turn.insertBefore(line, view.bubble);
    if (stickBottom) toBottom();
  }

  // One kept line per finished step, and the live label while a step runs. The
  // desktop deletes its activity block when the step ends and renders no history
  // at all; on a phone the trace IS the answer to "what did it just do for 40
  // seconds", so it stays — and it names the thing rather than the category: which
  // skill was read, which file was edited, which command was run.
  function trace(view, f) {
    var phase = String(f.tool_progress || '');
    if (f.tool) view.tool = String(f.tool);
    var tool = view.tool || '';

    if (phase === 'finished') {
      var secs = Math.round(Number(f.seconds) || 0);
      var step = view.step || {};
      // Best first: the skill and resource the host recorded, then the command the
      // model actually ran, then the frame's own detail, then just the label.
      var what = step.skill
        ? step.skill + (step.detail ? ' · ' + step.detail : '')
        : (step.detail || view.detail || f.detail || '');
      var text = labelFor(tool, 'running')
        + (what ? ' · ' + shorten(what, 70) : '')
        + (secs ? ' · ' + secs + 's' : '');

      stepLine(view, step.ok === false ? 'fail' : 'done', text);
      (step.files || []).forEach(function (file) { fileLine(view, file); });

      view.tool = '';
      view.step = null;
      view.detail = '';
      // The strip keeps saying what just finished until the next thing starts. A
      // generation between two tool calls is silent for many seconds, and "Working…"
      // over a blank line says less than the step that has just come back.
      progress(text, step.ok === false ? 'fail' : 'done');
      progressTail('');
      return;
    }
    if (phase !== 'writing' && phase !== 'running') return;
    if (phase === 'running' && f.detail) view.detail = String(f.detail);
    // The elapsed seconds tick once a second while a command runs, which is the
    // difference between "it is doing something" and "it has stopped".
    var elapsed = phase === 'running' ? Math.round(Number(f.seconds) || 0) : 0;
    progress(labelFor(tool, phase) + '…' + (elapsed ? ' ' + elapsed + 's' : ''));
  }

  // What the host did on the model's behalf, recorded as it happened: which skill it
  // read, which script it ran, whether that worked, and what it produced. The frame
  // arrives just before the tool's `finished`, so it is held and used to write that
  // one line rather than adding a second one saying the same thing twice.
  function skillStep(view, f) {
    view.step = {
      skill: f.skill ? String(f.skill) : '',
      detail: f.detail ? String(f.detail) : '',
      ok: f.ok !== false,
      files: f.files || null,
    };
  }

  function addCopy(turn, getText) {
    var c = el('button', 'copy', 'Copy');
    c.addEventListener('click', function () {
      var t = getText();
      if (navigator.clipboard) navigator.clipboard.writeText(t);
      c.textContent = 'Copied'; setTimeout(function () { c.textContent = 'Copy'; }, 1200);
    });
    turn.appendChild(c);
  }

  function notice(msg, kind) {
    clearEmpty();
    var n = el('div', 'notice' + (kind === 'error' ? ' error' : ''), msg);
    chat.appendChild(n);
    if (stickBottom) toBottom();
    return n;
  }

  // A refusal the user can act on, rather than prose about a switch they have to go
  // and find. The research skill's failure is the case this exists for: it needs the
  // network, the network is off by default, and "network access is disabled by the
  // user" told the reader what happened without telling them what to do about it.
  function noticeWithAction(msg, label, run) {
    var n = notice(msg, 'error');
    var b = el('button', 'notice-action', label);
    b.type = 'button';
    b.addEventListener('click', function () {
      b.disabled = true;
      Promise.resolve(run()).then(function (ok) {
        b.textContent = ok === false ? 'Could not change it' : 'Done';
      });
    });
    n.appendChild(document.createElement('br'));
    n.appendChild(b);
    return n;
  }

  function turnNetworkOn() {
    var next = Object.assign({}, state.settings || {}, { allowNetwork: true });
    return post('/api/agent/settings', next)
      .then(function (r) { return r.json(); })
      .then(function (s) {
        state.settings = s;
        notice('Network is on. Ask again and the assistant can reach the web.');
        return true;
      })
      .catch(function () { return false; });
  }

  // Offered at most once a turn: a skill that retries three times must not stack
  // three identical buttons.
  function offerNetworkIfRefused(textSeen, offered) {
    if (offered || !state.netMsg) return offered;
    if (String(textSeen).indexOf(state.netMsg) < 0) return offered;
    if (state.settings && state.settings.allowNetwork) return offered;
    noticeWithAction(
      'That needed the internet, and network access is off. Everything else runs on '
      + 'this device; only this step needs to go out.',
      'Turn on Network', turnNetworkOn);
    return true;
  }

  // ---- model state ---------------------------------------------------------
  function paintModel(d) {
    state.model = (d && d.loaded) || null;
    state.arch = (d && d.architecture) || null;
    state.backend = (d && d.loadedBackend) || null;
    state.maxTokens = (d && d.defaultMaxTokens) || 2048;
    paintModelButton();
  }

  // Three states, not two. The app now loads the model the user last used by itself,
  // and reading four gigabytes off flash takes seconds -- during which "No model yet"
  // is not merely unhelpful, it is wrong, and it sends the user to a Models list to
  // choose the model they have already chosen and which is at that moment loading.
  function paintModelButton() {
    if (state.model) {
      modelBtn.className = '';
      modelBtn.textContent = pretty(state.model);
      modelBtn.appendChild(el('span', 'sub',
        state.backend === 'ggml_metal' ? '  GPU' : state.backend === 'ggml_cpu' ? '  CPU' : ''));
    } else if (loadingModel()) {
      modelBtn.className = 'empty';
      modelBtn.textContent = 'Loading ' + (state.modelInfo.name || 'the model') + '…';
    } else {
      modelBtn.className = 'empty';
      modelBtn.textContent = 'No model yet';
    }
    send.disabled = !state.model && !state.generating;
  }
  function loadingModel() {
    return !!(state.modelInfo && state.modelInfo.loading);
  }
  function pretty(file) {
    return String(file).replace(/\.gguf$/i, '').replace(/-(it|instruct)\b/i, '').replace(/[-_]/g, ' ');
  }

  function refreshModel() {
    return fetch('/api/models').then(function (r) { return r.json(); }).then(function (d) {
      paintModel(d);
      return d;
    }).catch(function () { return null; });
  }

  // While the startup load runs, keep asking. It is the only way the page finds out
  // that the model it was told about has arrived: nothing pushes to this page, and a
  // send button that stays disabled after the weights are in memory is the same bug
  // as the one this whole path exists to fix, arriving a few seconds later.
  function watchModelLoad() {
    if (state.modelWatch) return;
    var deadline = Date.now() + 5 * 60 * 1000;
    state.modelWatch = setInterval(function () {
      if (state.model || !loadingModel() || Date.now() > deadline) {
        clearInterval(state.modelWatch);
        state.modelWatch = 0;
        return;
      }
      refreshModel().then(refreshEngine);
    }, 1500);
  }

  // ---- sessions and conversations -----------------------------------------
  function newSession(conversationId) {
    // Written as one literal rather than assembled, so the route this binds a
    // conversation with is greppable -- a test pins exactly this string, because a
    // session opened without a conversation silently loses the transcript.
    var url = conversationId
      ? '/api/sessions?conversation=' + encodeURIComponent(conversationId)
      : '/api/sessions?conversation=new';
    return post(url).then(function (r) { return r.json(); }).then(function (s) {
      state.session = s.sessionId || null;
      state.conversation = s.conversation || s.conversationId || conversationId || null;
      return s;
    });
  }

  function emptyState(line) {
    var e = el('div', null, '');
    e.id = 'empty';
    e.innerHTML = '<h1>TensorAgent</h1><p>' + esc(line) + '</p>';
    var b = el('button', 'cta', state.model || loadingModel() ? 'Manage models' : 'Choose a model');
    b.type = 'button';
    b.addEventListener('click', function () { post('/api/agent/events', { type: 'open-models' }); });
    e.appendChild(b);
    chat.appendChild(e);
  }

  /**
   * Put a saved transcript back on the screen, and back into the history.
   *
   * Both halves matter and only the first one is visible. The message is pushed into
   * `state.history` WHOLE — its image paths, its audio, the names of the documents
   * whose text is inlined in it — because that array is what the next request is
   * built from and what the host saves over the top of the stored copy. Keeping only
   * the role and the text, which is what this did, meant a resumed chat lost every
   * attachment twice: the model could no longer see the photo being discussed, and
   * the next turn wrote a transcript with the photo missing from it.
   */
  function renderMessages(messages) {
    (messages || []).forEach(function (m) {
      state.history.push(m);
      addTurn(m.role, displayText(m.content), m.attachments,
        { artifacts: m.artifacts, imageUrl: m.imageUrl });
    });
  }

  /**
   * Show a saved chat, or start a new one, WITHOUT reloading the page.
   *
   * Reloading is what this used to do, and it cost more than it bought: the WebView
   * starts over, every fetch in flight is cut, and on a phone that includes the answer
   * the model is in the middle of writing. Rebuilding the three things a chat actually
   * consists of -- the transcript, the engine session, and whatever generation is still
   * running for it -- is both faster and the only version that can hand the user back a
   * turn that carried on while they were somewhere else.
   */
  function openConversation(conversationId) {
    detach();
    state.history = [];
    state.attachments = [];
    paintChips();
    chat.innerHTML = '';
    return newSession(conversationId).then(function (s) {
      var msgs = (s && s.messages) || [];
      // Assigned in every case, never only when there is something to assign. A saved
      // chat with no skills means no skills; leaving the previous chat's selection in
      // place ran this one with a skill nobody chose for it, and then wrote that skill
      // into its saved record.
      var fresh = !msgs.length;
      state.think = !fresh && typeof s.think === 'boolean' ? s.think : thinkDefault();
      // A chat saved while skills were on remembers which ones. Re-selecting them
      // after the feature was turned off would be the saved chat overruling the
      // setting, which is the wrong way round.
      state.skills = !skillsOn() ? []
        : (!fresh && Array.isArray(s.skills) ? s.skills.slice() : defaultSkills());
      paintSkillChips();
      if (!fresh) {
        renderMessages(msgs);
        toBottom();
      } else {
        emptyState(state.model || loadingModel()
          ? 'New chat. Nothing you type leaves the device.'
          : 'Private AI that runs on this iPhone.');
      }
      // The answer a previous page left running here. Attached to, not restarted:
      // the tokens it produced while nobody was reading arrive first, then the rest
      // as they come.
      if (s && s.activeTurn && s.activeTurn.running) attachTurn(s.activeTurn.id);
      return s;
    }).catch(function (e) {
      emptyState('Could not open that chat: ' + ((e && e.message) || e));
      return null;
    });
  }

  // Open the most recent conversation, so the app resumes where the user left off
  // instead of greeting them with a blank page every launch.
  function resumeLatest() {
    return loadConversations().then(function (list) {
      return openConversation(list.length ? list[0].id : null);
    });
  }

  function loadConversations() {
    return fetch('/api/agent/conversations').then(function (r) { return r.json(); }).then(function (d) {
      state.conversations = (d && d.conversations) || [];
      return state.conversations;
    }).catch(function () { return state.conversations; });
  }

  // ---- sending -------------------------------------------------------------
  function setGenerating(on) {
    state.generating = !!on;
    busy.className = on ? 'on' : '';
    send.textContent = on ? '■' : '➤';
    send.className = 'round ' + (on ? 'stop' : 'send');
    send.disabled = !on && !state.model;
  }

  function sendMessage() {
    if (state.generating) { stop(); return; }
    var t = text.value.trim();
    if (!t && !state.attachments.length) return;
    if (!state.model) {
      // Two different answers, because they ask for two different things. A model
      // that is loading needs a few seconds; no model at all needs a download.
      if (loadingModel()) notice((state.modelInfo.name || 'The model') + ' is still loading. One moment.');
      else openSheet('model-sheet');
      return;
    }

    var atts = state.attachments.slice();
    addTurn('user', t, atts);
    text.value = ''; autoGrow();
    state.attachments = []; paintChips();

    var msg = messageFor(t, atts);
    state.history.push(msg);

    var body = {
      // The history AS IT IS, not a copy of it with the attachments removed. The
      // whole array used to be flattened to {role, content} on its way out, which
      // dropped every image path from every previous turn -- so the model could
      // answer a follow-up question about a photo it had been shown, and could not
      // see it any more.
      messages: state.history.slice(),
      maxTokens: state.maxTokens,
      think: !!state.think,
    };
    if (state.session) body.sessionId = state.session;
    if (skillsOn() && state.skills.length) body.skills = state.skills;

    stream(body);
  }

  /**
   * One user message, in the shape /api/chat reads and the transcript stores.
   *
   * The five path lists are not interchangeable and the server treats each
   * differently: `imagePaths` is what the vision encoder sees (a video's frames go in
   * here too), `stillImagePaths` is the pictures the user actually attached,
   * `videoFilePaths` and `audioPaths` are the media themselves, and `textFilePaths`
   * names the documents whose text has been inlined into the content. `attachments`
   * is the sixth and it is the one the user sees: what the chips said, so a reopened
   * chat says it again -- and, on the host side, which files to stage into the
   * working directory of anything the model runs.
   */
  function messageFor(typed, atts) {
    var msg = { role: 'user', content: typed || describe(atts) };
    var imagePaths = [], stillImagePaths = [], videoFilePaths = [], audioPaths = [];
    var textFilePaths = [], textFileNames = [], textParts = [];
    var isVideo = false;

    atts.forEach(function (a) {
      var kind = a.mediaType || 'text';
      if (kind === 'image') {
        imagePaths.push(a.file);
        stillImagePaths.push(a.file);
      } else if (kind === 'video') {
        isVideo = true;
        if (a.file) videoFilePaths.push(a.file);
        (a.frames || []).forEach(function (f) { imagePaths.push(f); });
      } else if (kind === 'audio') {
        audioPaths.push(a.file);
      } else if (a.textContent) {
        // Text and born-digital PDFs alike: the content goes in front of the
        // question, and the file is named so a program can open the whole of it.
        textParts.push('[File: ' + (a.fileName || a.file) + ']\n' + a.textContent + '\n[End of file]');
        if (a.file) { textFilePaths.push(a.file); textFileNames.push(a.fileName || a.file); }
      } else if (kind === 'pdf' && a.frames && a.frames.length) {
        // A scanned PDF has no text layer; its pages are pictures for a vision model.
        if (a.file) { textFilePaths.push(a.file); textFileNames.push(a.fileName || a.file); }
        a.frames.forEach(function (f) { imagePaths.push(f); });
      } else if (a.file) {
        textFilePaths.push(a.file); textFileNames.push(a.fileName || a.file);
      }
    });

    if (textParts.length) msg.content = textParts.join('\n\n') + '\n\n' + msg.content;
    if (imagePaths.length) msg.imagePaths = imagePaths;
    if (stillImagePaths.length) msg.stillImagePaths = stillImagePaths;
    if (videoFilePaths.length) msg.videoFilePaths = videoFilePaths;
    if (audioPaths.length) msg.audioPaths = audioPaths;
    if (textFilePaths.length) msg.textFilePaths = textFilePaths;
    if (textFileNames.length) msg.textFileNames = textFileNames;
    if (isVideo) msg.isVideo = true;
    if (atts.length) msg.attachments = atts.map(chipOf);
    return msg;
  }
  function describe(atts) {
    return atts.map(function (a) { return (a.fileName || a.file); }).join(', ');
  }

  /**
   * Stop the model, which is a different act from stopping this page reading it.
   *
   * The turn belongs to the app now, so aborting the fetch would only leave it
   * generating for nobody. The Stop button has to say so out loud.
   */
  function stop() {
    if (state.turn) {
      post('/api/agent/turns/' + encodeURIComponent(state.turn) + '/stop');
      detach();
      return;
    }
    // The id has not arrived yet. It rides on the stream's headers, and those are held
    // back until the first frame so that a refusal can still be a status code -- which
    // means that for the whole of a prefill, which is the minute a user is most likely
    // to change their mind in, this page does not know what to stop. Ask the host what
    // this conversation is generating.
    var conversation = state.conversation;
    detach();
    if (!conversation) return;
    fetch('/api/agent/turns?conversation=' + encodeURIComponent(conversation))
      .then(function (r) { return r.json(); })
      .then(function (d) {
        if (d && d.turn && d.turn.running) post('/api/agent/turns/' + encodeURIComponent(d.turn.id) + '/stop');
      })
      .catch(function () {});
  }

  /** Stop READING. The turn carries on; this is what leaving a chat does. */
  function detach() {
    if (state.abort) { try { state.abort.abort(); } catch (e) {} }
    state.abort = null;
    state.turn = null;
    state.liveView = null;
    progressDone();
    setGenerating(false);
  }

  /**
   * Pick the generation back up, if this page has lost hold of one.
   *
   * Called whenever the page becomes visible again, because that is exactly when it
   * may have missed something: WebKit suspends a WKWebView's content process the
   * moment its view leaves the window -- which is what opening any other screen in
   * this app does -- and a suspended process is not reading a stream. The turn itself
   * never stopped; it belongs to the host. This is how the page finds out what was
   * said while nobody was listening.
   *
   * A finished turn is attached to as well as a running one, and deliberately: an
   * answer that completed while the user was on another screen is replayed in full
   * rather than left as the half sentence they walked away from.
   */
  function resumeTurn() {
    if (state.abort || !state.conversation || state.resuming) return true;
    // Which chat this lookup is FOR, held across the round trip. Coming back to the app
    // and opening a different saved chat are the same gesture a moment apart -- the app
    // asks the page to resume as the chat reappears, and the answer arrives after the
    // page has moved on -- so without this the previous chat's answer streams into the
    // one now on screen and is saved under it.
    var conversation = state.conversation;
    state.resuming = true;
    fetch('/api/agent/turns?conversation=' + encodeURIComponent(conversation))
      .then(function (r) { return r.json(); })
      .then(function (d) {
        state.resuming = false;
        var t = d && d.turn;
        if (!t || state.abort || state.conversation !== conversation) return;
        if (t.running || state.liveView) attachTurn(t.id);
      })
      .catch(function () { state.resuming = false; });
    return true;
  }

  function failed(view, e) {
    progressDone();
    state.abort = null;
    if (e && e.name === 'AbortError') { setGenerating(false); return; }
    // A read that broke while a turn is still the app's is this page losing its
    // connection, not the model failing. Saying "The request failed" for that would be
    // telling the user their answer is gone while it is still being written. Take it
    // up again instead; the replay starts from the first frame either way.
    if (state.turn) {
      setGenerating(false);
      setTimeout(resumeTurn, 800);
      return;
    }
    notice((e && e.message) || 'The request failed.', 'error');
    setGenerating(false);
    state.liveView = null;
  }

  /**
   * The assistant turn a generation is being rendered into.
   *
   * Reused rather than added again when a stream is picked up after being interrupted,
   * because the replay starts at the very first frame: a second bubble would leave the
   * half-written one above it on the screen for good.
   */
  function liveView() {
    var live = state.liveView;
    if (live && live.turn.parentNode) {
      live.bubble.innerHTML = '';
      Array.prototype.slice.call(live.turn.querySelectorAll('.step, .think, .copy'))
        .forEach(function (n) { n.remove(); });
      live.step = null; live.tool = ''; live.detail = '';
      return live;
    }
    state.liveView = addTurn('assistant', '');
    return state.liveView;
  }

  function stream(body) {
    state.liveView = null;
    var view = liveView();
    setGenerating(true);
    // Immediately, before a single byte comes back: the gap between pressing send
    // and the first frame is itself seconds long on a phone.
    progress('Thinking…');

    var ctrl = new AbortController();
    state.abort = ctrl;
    fetch('/api/chat', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
      signal: ctrl.signal,
    }).then(function (res) {
      // The turn this stream is a view of. Held so the Stop button can stop the
      // model rather than merely stopping us listening to it.
      state.turn = res.headers.get('X-TensorAgent-Turn') || null;
      if (!res.ok) {
        return res.text().then(function (t) { throw new Error(reasonOf(t) || ('HTTP ' + res.status)); });
      }
      return read(res, view);
    }).catch(function (e) { failed(view, e); });
  }

  /**
   * Pick up a generation that is already running -- one this page did not start, or
   * started and then stopped watching.
   *
   * The host replays every frame from the beginning, so the answer is rebuilt exactly
   * as it would have been had nobody looked away, and then continues live.
   */
  function attachTurn(id) {
    var view = liveView();
    setGenerating(true);
    state.turn = id;
    progress('Still working…');

    var ctrl = new AbortController();
    state.abort = ctrl;
    fetch('/api/agent/turns/' + encodeURIComponent(id), { signal: ctrl.signal })
      .then(function (res) {
        if (!res.ok) {
          // The turn finished and was forgotten between being announced and being
          // asked for. The saved transcript is the record, so this bubble would only
          // ever be an empty one under it.
          if (!view.bubble.innerHTML) view.turn.remove();
          state.liveView = null;
          state.turn = null;
          progressDone();
          setGenerating(false);
          return null;
        }
        return read(res, view);
      })
      .catch(function (e) { failed(view, e); });
  }

  /** The error sentence out of a JSON refusal body, or the body itself. */
  function reasonOf(text) {
    try {
      var b = JSON.parse(text);
      if (b && typeof b.error === 'string') return b.error;
    } catch (e) {}
    return text;
  }

  /**
   * Read one event stream into one assistant turn.
   *
   * Shared by the request that starts a generation and by a page attaching to one that
   * is already running, because those differ only in how the response was obtained --
   * every frame after that means the same thing, and two copies of this loop would be
   * two renderings of the same answer that stop agreeing.
   */
  function read(res, view) {
    var answer = '', thinking = '', thinkBox = null, thinkBody = null;
    var steps = '', offered = false, draft = '';
    // What this turn PRODUCED: the files its tools wrote, and a picture it made.
    // Kept so the history entry carries them, because the history is what the next
    // request rewrites the saved transcript from -- an entry that has forgotten the
    // PDF erases the PDF from a chat that had one.
    var made = [], madeSeen = {}, madeImage = null;
    var reader = res.body.getReader(), dec = new TextDecoder(), buf = '';

    function pump() {
      return reader.read().then(function (r) {
        if (r.done) return finish();
        buf += dec.decode(r.value, { stream: true });
        var parts = buf.split('\n');
        buf = parts.pop();
        parts.forEach(function (line) {
          if (line.indexOf('data: ') !== 0) return;
          var f;
          try { f = JSON.parse(line.slice(6)); } catch (e) { return; }
          handle(f);
        });
        return pump();
      });
    }
    function handle(f) {
      if (f.thinking) {
        thinking += f.thinking;
        if (!thinkBox) {
          thinkBox = el('details', 'think');
          thinkBox.appendChild(el('summary', null, 'Reasoning'));
          thinkBody = el('div', 'body');
          thinkBox.appendChild(thinkBody);
          view.turn.insertBefore(thinkBox, view.bubble);
        }
        thinkBody.textContent = thinking;
        // The whole of it is one tap away in the box above; the tail is what
        // says, without being asked, what the model is thinking about now.
        progress('Thinking…');
        progressTail(thinking);
      }
      if (f.token || f.replace) {
        // The answer is on the screen from here on, so the live tail would only
        // be a second, staler copy of it.
        progress('Writing the answer…');
        progressTail('');
      }
      if (f.token) { answer += f.token; view.bubble.innerHTML = render(answer); }
      if (f.replace) { answer = f.replace; view.bubble.innerHTML = render(answer); }
      // Before trace(): the host's record of the call arrives just ahead of the
      // tool's `finished`, and it is what makes that line name a skill instead of
      // a category.
      if (f.skill_step) skillStep(view, f);
      if (f.tool_progress) {
        trace(view, f);
        // A tool call is written a token at a time and can be a whole heredoc;
        // showing it as it is typed is the difference between "it is doing
        // something" and "it is writing THIS".
        if (f.tool_progress === 'writing' && f.text) { draft += f.text; progressTail(draft); }
        else if (f.tool_progress === 'running') {
          // The first `running` frame carries the command and no output; the ones
          // after it carry the output as it is printed. So the draft is emptied
          // once and then refilled with what the command is SAYING, which is the
          // more useful of the two by the time there is any.
          if (f.text) draft += f.text; else draft = '';
          progressTail(draft || String(f.detail || ''));
        }
        else if (f.tool_progress === 'finished') { draft = ''; progressTail(''); }
      }
      if (f.detail || f.output) steps += ' ' + (f.detail || '') + ' ' + (f.output || '');
      if (f.error) {
        steps += ' ' + f.error;
        offered = offerNetworkIfRefused(String(f.error), offered);
        if (!offered) notice(String(f.error), 'error');
      }
      if (f.files) f.files.forEach(function (file) {
        if (!file || !file.url || madeSeen[file.url]) return;
        madeSeen[file.url] = 1;
        made.push({ name: file.name || file.url, bytes: file.bytes || 0, url: file.url });
      });
      if (f.image || f.imageUrl) {
        madeImage = f.imageUrl || f.image;
        var img = document.createElement('img');
        img.src = madeImage;
        view.bubble.appendChild(img);
      }
      if (stickBottom) toBottom();
    }
    function finish() {
      progressDone();
      // A file the last step produced and no `finished` frame came back to render.
      // The download is the thing the user asked for; losing it to a stream that
      // ended a frame early would be the worst possible way to lose it.
      if (view.step && view.step.files) {
        view.step.files.forEach(function (file) { fileLine(view, file); });
        view.step = null;
      }
      offered = offerNetworkIfRefused(answer + ' ' + steps, offered);
      // Everything the turn produced, not only its prose. The host writes the same
      // three things down when the turn ends; the page has to hold them too, because
      // the next request sends this array and the host saves what it is sent.
      var entry = { role: 'assistant', content: answer };
      if (thinking) entry.thinking = thinking;
      if (made.length) entry.artifacts = made;
      if (madeImage) entry.imageUrl = madeImage;
      state.history.push(entry);
      if (answer) addCopy(view.turn, function () { return answer; });
      setGenerating(false);
      state.abort = null;
      state.turn = null;
      state.liveView = null;
      // The list in the menu is titled from the first thing the user said and dated
      // by the last thing that happened, so it is stale the moment a turn ends.
      loadConversations().then(paintNavChats);
    }
    return pump();
  }

  // ---- attachments ---------------------------------------------------------
  function paintChips() {
    var box = $('chips');
    box.innerHTML = '';
    state.attachments.forEach(function (a, i) {
      var c = el('div', 'chip');
      if (a.mediaType === 'image') {
        var img = document.createElement('img'); img.src = a.url; c.appendChild(img);
      } else {
        c.appendChild(el('span', 'ic', a.mediaType === 'video' ? '🎬' : a.mediaType === 'audio' ? '🎧' : '📄'));
      }
      c.appendChild(el('span', 'nm', a.fileName || a.file));
      var x = el('button', 'x', '✕');
      x.addEventListener('click', function () { state.attachments.splice(i, 1); paintChips(); });
      c.appendChild(x);
      box.appendChild(c);
    });
  }

  function upload(file) {
    var fd = new FormData();
    fd.append('file', file, file.name);
    return fetch('/api/upload', { method: 'POST', body: fd })
      .then(function (r) { return r.json(); })
      .then(function (a) {
        if (!a || !a.ok) { notice((a && a.error) || 'Upload failed', 'error'); return; }
        state.attachments.push(a); paintChips();
      });
  }

  $('file-input').addEventListener('change', function (e) {
    Array.prototype.forEach.call(e.target.files || [], upload);
    e.target.value = '';
  });

  // ---- sheets --------------------------------------------------------------
  function openSheet(id) { $('sheet-bg').classList.add('on'); $(id).classList.add('on'); }
  function closeSheets() {
    $('sheet-bg').classList.remove('on');
    ['attach-sheet', 'skills-sheet', 'model-sheet', 'nav-sheet', 'skill-sheet', 'skill-add-sheet'].forEach(function (s) { $(s).classList.remove('on'); });
  }
  $('sheet-bg').addEventListener('click', closeSheets);

  // Requirement 6: one "+" opens a list, instead of four buttons on a row.
  $('plus').addEventListener('click', function () { openSheet('attach-sheet'); });
  document.querySelectorAll('#attach-sheet .opt').forEach(function (b) {
    b.addEventListener('click', function () {
      var kind = b.getAttribute('data-pick');
      closeSheets();
      // The native side owns the camera, the library and the document picker;
      // the file input is the fallback when the page is open in a browser.
      // The app owns the camera, the library and the document picker. It is asked
      // over the same loopback transport everything else uses, so this file needs no
      // iOS-specific object; MainPage.OnPageEvent turns it into a native picker.
      if (state.native) { post('/api/agent/events', { type: 'pick', what: kind }); return; }
      var input = $('file-input');
      input.setAttribute('accept',
        kind === 'photo' ? 'image/*' : kind === 'video' ? 'video/*' : kind === 'camera' ? 'image/*' : '*/*');
      if (kind === 'camera') input.setAttribute('capture', 'environment'); else input.removeAttribute('capture');
      input.click();
    });
  });

  // ---- the menu ------------------------------------------------------------
  //
  // A drawer from the LEFT edge, not a sheet from the bottom, and it carries the saved
  // chats themselves rather than a row that leads to them. Both changes are the same
  // observation: this is the app's main menu, the thing reached most often through it
  // is a chat the user has already had, and a bottom sheet that fits five rows can
  // only ever offer the word "Chats" -- one more tap, and a whole screen, in front of
  // the one thing being looked for. A left drawer is full height, so the list fits.
  $('menu').addEventListener('click', function () {
    openSheet('nav-sheet');
    // Painted from what is already known so the drawer is never empty for a frame,
    // then again from the store, because a chat may have been renamed or deleted on
    // the native Chats page since.
    paintNavChats();
    loadConversations().then(paintNavChats);
  });

  $('nav-new').addEventListener('click', function () {
    closeSheets();
    openConversation(null);
  });

  function paintNavChats() {
    var box = $('nav-chats');
    if (!box) return;
    box.innerHTML = '';
    if (!state.conversations.length) {
      box.appendChild(el('div', 'navempty', 'No saved chats yet.'));
      return;
    }
    state.conversations.forEach(function (c) {
      var row = el('button', 'navchat' + (c.id === state.conversation ? ' on' : ''));
      row.type = 'button';
      row.appendChild(el('span', 'nm', c.title || 'Chat'));
      row.appendChild(el('span', 'ds', when(c.updatedAt) + ' · '
        + (c.messageCount === 1 ? '1 message' : (c.messageCount || 0) + ' messages')));
      row.addEventListener('click', function () {
        closeSheets();
        if (c.id === state.conversation) return;
        openConversation(c.id).then(function () { paintNavChats(); });
      });
      box.appendChild(row);
    });
  }

  /** A date a person reads at a glance: a time today, a day this week, a date before that. */
  function when(iso) {
    var d = new Date(iso);
    if (isNaN(d.getTime())) return '';
    var now = new Date();
    var sameDay = d.toDateString() === now.toDateString();
    if (sameDay) return d.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
    if (now - d < 6 * 24 * 3600 * 1000) return d.toLocaleDateString([], { weekday: 'short' });
    return d.toLocaleDateString([], { month: 'short', day: 'numeric' });
  }

  document.querySelectorAll('#nav-sheet .opt').forEach(function (b) {
    b.addEventListener('click', function () {
      closeSheets();
      // Two kinds of menu item: the app's native routes, and this page's own sheets.
      // Skills is the second kind — it is a list this page already holds, and sending
      // it through the shell would mean registering a native page to show it.
      var sheet = b.getAttribute('data-sheet');
      if (sheet) {
        if (sheet === 'skills-sheet') loadSkills().then(function () { openSheet(sheet); });
        else openSheet(sheet);
        return;
      }
      post('/api/agent/events', { type: 'open-route', route: b.getAttribute('data-route') });
    });
  });

  modelBtn.addEventListener('click', function () {
    var info = $('model-info');
    info.innerHTML = '';
    info.appendChild(el('div', 'skillrow',
      state.model ? (pretty(state.model) + ' · ' + (state.arch || '?') + ' · ' + (state.backend || '')) : 'No model loaded yet.'));
    openSheet('model-sheet');
  });
  $('open-models').addEventListener('click', function () {
    closeSheets();
    post('/api/agent/events', { type: 'open-route', route: 'models' });
  });
  var cta = $('empty-cta');
  if (cta) cta.addEventListener('click', function () { post('/api/agent/events', { type: 'open-models' }); });

  // ---- skills --------------------------------------------------------------
  //
  // Two switches, and they answer different questions. The one inside a skill is
  // "use this one in THIS chat"; the one at the top of the list is whether the
  // feature exists at all. The second is a setting rather than a chat property
  // because it is not a per-conversation decision: with twelve skills declaring
  // themselves, having them on costs thousands of prompt tokens on every turn of
  // every chat, and a user who wants a plain assistant wants it for good.
  //
  // It is enforced on the host, not here. The page not sending `skills` would be a
  // request nobody checked; ServerHostingOptions.SkillsEnabled makes the request
  // planner build no plan, so no skill is declared, none is reachable, and a stale
  // page that still names one changes nothing.
  function skillsOn() {
    return !(state.settings && state.settings.skillsEnabled === false);
  }

  function paintSkillsMaster() {
    var box = $('skills-master');
    if (!box) return;
    var on = skillsOn();
    box.checked = on;
    var label = $('skills-master-label');
    if (label) label.textContent = on ? 'Use skills' : 'Skills are off';
    var list = $('skills-list');
    if (list) list.className = on ? '' : 'off';
    var add = $('skill-add');
    if (add) add.style.display = on ? '' : 'none';
  }

  function setSkillsEnabled(on) {
    var next = Object.assign({}, state.settings || {}, { skillsEnabled: !!on });
    // Painted from the intent first: the round trip is a loopback POST, but the
    // switch must not sit in its old position while it happens.
    state.settings = next;
    if (!on) { state.skills = []; paintSkillChips(); }
    paintSkillsMaster();
    return post('/api/agent/settings', next)
      .then(function (r) { return r.json(); })
      .then(function (saved) {
        state.settings = saved || next;
        if (!skillsOn()) { state.skills = []; paintSkillChips(); }
        paintSkillsMaster();
      })
      .catch(function (e) { notice('That setting could not be saved: ' + e, 'error'); });
  }

  (function () {
    var box = $('skills-master');
    if (box) box.addEventListener('change', function () { setSkillsEnabled(box.checked); });
  })();

  function loadSkills() {
    return fetch('/api/skills').then(function (r) { return r.json(); }).then(function (d) {
      state.catalogSkills = (d && d.skills) || [];
      // The host is the authority on whether the feature is on; the settings copy
      // this page holds may predate a change made anywhere else.
      if (d && typeof d.enabled === 'boolean') {
        state.settings = Object.assign({}, state.settings || {}, { skillsEnabled: d.enabled });
      }
      paintSkillsMaster();
      var list = $('skills-list');
      list.innerHTML = '';
      if (!state.catalogSkills.length) list.appendChild(el('div', 'notice', 'No skills are installed.'));
      state.catalogSkills.forEach(function (s) {
        // The whole row opens the skill. A description is almost always longer
        // than the two lines a list can spare, and truncating it to a tooltip
        // nobody can hover on a phone is the same as not shipping it.
        var row = el('div', 'skillrow');
        var meta = el('div', 'meta');
        meta.appendChild(el('div', 'nm', (state.skills.indexOf(s.name) >= 0 ? '● ' : '') + s.name));
        meta.appendChild(el('div', 'ds', s.description || ''));
        row.appendChild(meta);
        row.appendChild(el('span', 'chev', '›'));
        row.addEventListener('click', function () { openSkill(s); });
        list.appendChild(row);
      });
      return state.catalogSkills;
    });
  }

  // One skill, in full: the name, everything the description says, whether it is
  // on for this chat, and the way to remove it.
  function openSkill(s) {
    closeSheets();
    $('skill-name').textContent = s.name;
    var body = $('skill-body');
    body.innerHTML = '';
    var bits = [];
    if (s.scripts) bits.push(s.scripts + (s.scripts === 1 ? ' script' : ' scripts'));
    if (s.origin) bits.push(String(s.origin));
    if (bits.length) body.appendChild(el('div', 'meta', bits.join(' · ')));
    body.appendChild(document.createTextNode(s.description || 'This skill has no description.'));

    var on = $('skill-on');
    on.checked = state.skills.indexOf(s.name) >= 0;
    on.onchange = function () {
      var i = state.skills.indexOf(s.name);
      if (on.checked && i < 0) state.skills.push(s.name);
      if (!on.checked && i >= 0) state.skills.splice(i, 1);
      paintSkillChips();
    };

    $('skill-remove').onclick = function () {
      fetch('/api/skills/' + encodeURIComponent(s.name), { method: 'DELETE' })
        .then(function (r) { return r.json(); })
        .then(function () {
          var i = state.skills.indexOf(s.name);
          if (i >= 0) { state.skills.splice(i, 1); paintSkillChips(); }
          closeSheets();
          notice(s.name + ' was removed.');
        })
        .catch(function (e) { notice('Could not remove it: ' + e, 'error'); });
    };
    openSheet('skill-sheet');
  }

  // ---- adding a skill ------------------------------------------------------
  $('skill-add').addEventListener('click', function () { closeSheets(); openSheet('skill-add-sheet'); });
  $('skill-zip').addEventListener('click', function () {
    var input = $('skill-zip-input');
    input.value = '';
    input.click();
  });
  $('skill-zip-input').addEventListener('change', function (e) {
    var file = (e.target.files || [])[0];
    if (!file) return;
    var fd = new FormData();
    fd.append('file', file, file.name);
    fetch('/api/skills', { method: 'POST', body: fd })
      .then(function (r) { return r.json().then(function (b) { return { ok: r.ok, body: b }; }); })
      .then(function (res) { afterInstall(res); })
      .catch(function (err) { notice('That skill could not be installed: ' + err, 'error'); });
  });
  $('skill-fetch').addEventListener('click', function () {
    var url = ($('skill-url').value || '').trim();
    if (!url) return;
    $('skill-fetch').disabled = true;
    post('/api/skills/from-url', { url: url })
      .then(function (r) { return r.json().then(function (b) { return { ok: r.ok, body: b }; }); })
      .then(function (res) { $('skill-fetch').disabled = false; $('skill-url').value = ''; afterInstall(res); })
      .catch(function (err) { $('skill-fetch').disabled = false; notice('That link did not work: ' + err, 'error'); });
  });
  function afterInstall(res) {
    if (!res.ok) {
      var msg = (res.body && (res.body.error || res.body.message)) || 'The skill was refused.';
      notice(typeof msg === 'string' ? msg : JSON.stringify(msg), 'error');
      return;
    }
    closeSheets();
    // A list install reports both halves; say how many landed and how many did not.
    if (res.body && typeof res.body.count === 'number') {
      var failed = (res.body.failed || []).length;
      notice('Installed ' + res.body.count + (res.body.count === 1 ? ' skill' : ' skills')
        + (failed ? ', ' + failed + ' could not be installed' : '') + '.');
    } else {
      notice('Installed ' + ((res.body && res.body.name) || 'the skill') + '.');
    }
    loadSkills().then(function () { openSheet('skills-sheet'); });
  }
  function paintSkillChips() {
    var box = $('skillchips');
    box.innerHTML = '';
    if (!skillsOn()) return;
    state.skills.forEach(function (n) { box.appendChild(el('span', 'skillchip', '🧩 ' + n)); });
  }

  // ---- composer ------------------------------------------------------------
  function autoGrow() {
    text.style.height = 'auto';
    text.style.height = Math.min(text.scrollHeight, window.innerHeight * 0.26) + 'px';
  }
  text.addEventListener('input', autoGrow);
  send.addEventListener('click', sendMessage);
  $('new').addEventListener('click', function () { openConversation(null); });

  // ---- voice ---------------------------------------------------------------
  //
  // There is no Voice switch. It spent a permanent slot on the only row of
  // chrome this design has, to say something the composer can say by changing
  // shape — and it made speaking a two-step act: find the switch, then find the
  // button. Holding the message box is the gesture every phone messenger has
  // already taught, and it lands the thumb on the hold-to-talk button it just
  // conjured, ready to be held.
  function setVoice(on) {
    state.voice = !!on;
    document.body.classList.toggle('voice', state.voice);
    if (!state.voice) text.focus();
  }

  var pressTimer = 0, pressAt = null, touching = false;
  function cancelPress() {
    if (pressTimer) { clearTimeout(pressTimer); pressTimer = 0; }
    pressAt = null;
  }
  function movePress(x, y) {
    // A press that travels is a scroll or a selection drag, not a hold.
    if (pressAt && (Math.abs(x - pressAt.x) > 10 || Math.abs(y - pressAt.y) > 10)) cancelPress();
  }
  function beginPress(x, y) {
    if (state.voice) return;
    cancelPress();
    pressAt = { x: x, y: y };
    pressTimer = setTimeout(function () {
      pressTimer = 0;
      pressAt = null;
      if (!state.native) {
        // In a browser there is no recogniser to switch to, and a composer that
        // turned into a dead button would be worse than not switching.
        notice('Voice input is only available in the app.', 'error');
        return;
      }
      // Blurring first is what dismisses the selection callout iOS raises for a
      // long press on a text field, and it collapses the keyboard so the button
      // lands where the thumb already is.
      text.blur();
      setVoice(true);
    }, 450);
  }

  // TOUCH events lead and pointer events fill in, rather than pointer alone. WebKit
  // fires `pointercancel` the moment it decides a touch belongs to its own gesture —
  // and a long press on a text field is one of its own gestures, the selection
  // callout — so a timer cancelled by that would never reach the half-second this
  // needs. The touch sequence is not cancelled the same way, so it wins while it is
  // running; pointer events still drive a mouse, and a page opened in a desktop
  // browser behaves the same.
  text.addEventListener('touchstart', function (e) {
    touching = true;
    var t = e.touches[0];
    if (t) beginPress(t.clientX, t.clientY);
  }, { passive: true });
  text.addEventListener('touchmove', function (e) {
    var t = e.touches[0];
    if (t) movePress(t.clientX, t.clientY);
  }, { passive: true });
  ['touchend', 'touchcancel'].forEach(function (n) {
    text.addEventListener(n, function () { touching = false; cancelPress(); });
  });

  text.addEventListener('pointerdown', function (e) { if (!touching) beginPress(e.clientX, e.clientY); });
  text.addEventListener('pointermove', function (e) { if (!touching) movePress(e.clientX, e.clientY); });
  ['pointerup', 'pointercancel', 'pointerleave'].forEach(function (n) {
    text.addEventListener(n, function () { if (!touching) cancelPress(); });
  });
  // A keystroke means they meant to type, whatever the finger was doing.
  text.addEventListener('input', cancelPress);

  abc.addEventListener('click', function () { setVoice(false); });

  function startRec() {
    if (!state.native) { notice('Voice input is only available in the app.', 'error'); return; }
    hold.classList.add('rec');
    $('holdlabel').textContent = 'Listening… release to stop';
    post('/api/agent/events', { type: 'dictate-start' });
  }
  function stopRec() {
    if (!hold.classList.contains('rec')) return;
    $('holdlabel').textContent = 'Transcribing…';
    post('/api/agent/events', { type: 'dictate-stop' });
  }
  // The app says when the session has really ended, because the transcription
  // arrives after the finger lifts and the button must not look idle before it does.
  function dictationEnded() {
    hold.classList.remove('rec');
    $('holdlabel').textContent = 'Hold to talk';
    // Hand back to the text box with what was said already in it. Speaking is how
    // the message STARTS; reading it back, fixing a word and pressing send is how it
    // finishes, and staying in voice mode hides the very text the user needs to
    // check. Only leave voice mode if we are still in it -- the user may have
    // switched already.
    if (state.voice) setVoice(false);
    autoGrow();
    text.focus();
    // Put the caret at the end so typing continues the sentence rather than
    // landing in front of it.
    try { text.setSelectionRange(text.value.length, text.value.length); } catch (e) {}
  }

  // iOS recognises ONE language per session and does not detect which is being
  // spoken, so a bilingual user has to say which -- and the place to say it is next
  // to the button they are about to hold, not three screens away in Settings.
  var LANGS = [
    { id: '', label: 'Auto' },
    { id: 'en-US', label: 'EN' },
    { id: 'zh-CN', label: '中文' }
  ];
  function paintLang() {
    var box = $('lang');
    box.innerHTML = '';
    LANGS.forEach(function (l) {
      var b = el('button', 'langbtn' + (state.speech === l.id ? ' on' : ''), l.label);
      b.type = 'button';
      b.addEventListener('click', function () {
        state.speech = l.id;
        paintLang();
        post('/api/agent/settings', Object.assign({}, state.settings || {}, { speechLanguage: l.id }));
      });
      box.appendChild(b);
    });
  }
  ['pointerdown'].forEach(function (e) { hold.addEventListener(e, function (ev) { ev.preventDefault(); startRec(); }); });
  ['pointerup', 'pointercancel', 'pointerleave'].forEach(function (e) { hold.addEventListener(e, stopRec); });

  // ---- settings ------------------------------------------------------------
  // Requirement 8: "Show reasoning by default" is a setting the composer must
  // actually start from. It used to be read into a control the page then reset.
  // What the host is, and what it is doing about the model the user last used. Both
  // come from the same place because the page needs them at the same moment: as it
  // paints for the first time, before anything can be sent.
  function refreshEngine() {
    return fetch('/api/agent/engine').then(function (r) { return r.json(); }).then(function (e) {
      if (e && typeof e.networkDisabledMessage === 'string') state.netMsg = e.networkDisabledMessage;
      state.modelInfo = (e && e.model) || null;
      paintModelButton();
      if (loadingModel()) watchModelLoad();
      return e;
    }).catch(function () { return null; });
  }

  /**
   * Re-read the settings.
   *
   * `seed` is the difference between "start a chat from these" and "these have
   * changed": reasoning and the skill selection belong to the CHAT once it exists, and
   * this runs again every time the app comes back to the chat page. Overwriting them
   * unconditionally silently unticked a skill the user had chosen for this
   * conversation, every time they glanced at any other screen.
   */
  function applySettings(seed) {
    return fetch('/api/agent/settings').then(function (r) { return r.json(); }).then(function (s) {
      state.settings = s || null;
      if (s && typeof s.speechLanguage === 'string') state.speech = s.speechLanguage;
      if (seed) {
        state.think = thinkDefault();
        state.skills = defaultSkills();
      }
      // Not only on seed: the setting can be changed from the native Settings
      // screen, and a page that came back with skills still chipped under the
      // composer would be showing something that is no longer true.
      if (!skillsOn() && state.skills.length) state.skills = [];
      paintSkillChips();
      paintSkillsMaster();
      paintLang();
      return s;
    }).catch(function () { return null; });
  }

  function thinkDefault() {
    return !!(state.settings && state.settings.thinkByDefault);
  }
  function defaultSkills() {
    if (!skillsOn()) return [];
    return state.settings && Array.isArray(state.settings.defaultSkills)
      ? state.settings.defaultSkills.slice() : [];
  }

  // ---- the bridge the native side uses ------------------------------------
  window.TensorAgent = {
    notice: notice,
    addAttachment: function (a) {
      if (!a || !a.ok) { notice((a && a.error) || 'Upload failed', 'error'); return; }
      state.attachments.push(a); paintChips();
    },
    insertText: function (t) {
      if (!t) return;
      text.value = text.value && !/\s$/.test(text.value) ? text.value + ' ' + t : text.value + t;
      autoGrow();
      if (!state.voice) text.focus();
    },
    send: sendMessage,
    stop: stop,
    isGenerating: function () { return state.generating; },
    setThink: function (on) { state.think = !!on; },
    /** Re-read the settings, because Settings is a native page and this one outlives it. */
    refreshSettings: function () { applySettings(false); return true; },
    /**
     * Show a saved chat, or start a new one. Called by the native Chats page instead of
     * navigating the WebView, which used to be how this worked and which threw away
     * every generation in flight along with the page.
     */
    openConversation: function (id) { openConversation(id || null); return true; },
    /** The menu's list of chats, after something outside the page changed it. */
    refreshChats: function () { loadConversations().then(paintNavChats); return true; },
    /** What the app is doing about the model, after the Models page changed it. */
    refreshEngine: function () { refreshEngine(); return true; },
    /** Take the generation back up, after this page was away and could not read it. */
    resumeTurn: resumeTurn,
    /**
     * Open the main menu. For a screenshot, and it exists because neither simctl nor
     * devicectl can touch the screen: without it, the one surface this app's navigation
     * lives on could never be pictured, only measured.
     */
    openMenu: function () {
      openSheet('nav-sheet');
      paintNavChats();
      loadConversations().then(paintNavChats);
      return true;
    },
    setSkills: function (n) { state.skills = skillsOn() ? (n || []).slice() : []; paintSkillChips(); },
    /** Whether the skills feature is on at all, for the app's own checks. */
    skillsEnabled: skillsOn,
    history: function () { return state.history; },
    // Synchronous on purpose: WKWebView's evaluateJavaScript does not await a
    // promise, so an async function here can never report success to native code.
    refreshModel: function () { refreshModel(); return true; },
    hasModel: function () { return !!state.model; },
    dictationEnded: dictationEnded,
    /** A refusal only the user can lift, with a button that opens iOS Settings. */
    noticeWithSettings: function (msg) {
      noticeWithAction(msg, 'Open Settings', function () {
        post('/api/agent/events', { type: 'open-settings' });
        return true;
      });
    },
    /** The app calls this once at startup so the page knows native pickers exist. */
    nativeReady: function () { state.native = true; return true; },
  };

  // The page heals itself, without needing the app to tell it to. Opening any other
  // screen takes this WebView out of the window, and WebKit suspends a content process
  // whose view is not in one -- so the reader stops mid-answer and the page is told
  // nothing. `visibilitychange` is the event WebKit fires for exactly that transition,
  // in both directions, which makes it the one hook that cannot be missed: it works
  // when the app forgets to call, when the app is backgrounded and comes back, and when
  // the content process was killed and reloaded.
  document.addEventListener('visibilitychange', function () {
    if (document.visibilityState !== 'visible') return;
    resumeTurn();
    refreshModel();
  });
  window.addEventListener('pageshow', function () { resumeTurn(); });

  // ---- start ---------------------------------------------------------------
  refreshEngine()
    .then(function () { return applySettings(true); })
    .then(refreshModel)
    .then(resumeLatest)
    .then(function () { post('/api/agent/events', { type: 'ready', conversation: state.conversation }); })
    .catch(function (e) { notice('Could not start: ' + ((e && e.message) || e), 'error'); });
})();
