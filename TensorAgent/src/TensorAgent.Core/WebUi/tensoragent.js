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
  var modelBtn = $('model'), think = $('think'), voice = $('voice'), hold = $('hold');

  var state = {
    model: null, arch: null, backend: null,
    session: null, conversation: null,
    history: [],            // {role, content, attachments}
    attachments: [],        // /api/upload responses
    skills: [],             // selected skill names
    catalogSkills: [],
    generating: false,
    abort: null,
    maxTokens: 2048,
    speech: '',            // BCP-47 for dictation; empty follows the device
    settings: null,
    native: false,         // true when the page is inside the app, not a browser
    netMsg: '',            // the host's own wording for a network refusal
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

  // ---- the transcript ------------------------------------------------------
  function clearEmpty() { var e = $('empty'); if (e) e.remove(); }

  function addTurn(role, content, attachments) {
    clearEmpty();
    var turn = el('div', 'turn ' + (role === 'user' ? 'me' : 'bot'));
    var b = el('div', 'bubble');
    if (attachments && attachments.length) {
      attachments.forEach(function (a) {
        if (a.mediaType === 'image') {
          var img = document.createElement('img');
          img.src = a.url; img.alt = a.fileName || 'image';
          b.appendChild(img);
        } else {
          b.appendChild(el('div', null, '📄 ' + (a.fileName || a.file)));
        }
      });
    }
    if (content) {
      var body = el('div');
      body.innerHTML = render(content);
      b.appendChild(body);
    }
    turn.appendChild(b);
    chat.appendChild(turn);
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

  function trace(view, f) {
    var phase = String(f.tool_progress || '');
    if (phase === 'finished') {
      if (view.live) {
        // Keep it, as a finished line. The desktop removes this; on a phone the
        // trace IS the answer to "what did it just do for 40 seconds".
        var secs = Math.round(Number(f.seconds) || 0);
        view.live.className = 'step done';
        view.live.querySelector('.txt').textContent =
          (f.detail ? String(f.detail) : view.live._label || 'Done')
          + (secs ? ' · ' + secs + 's' : '');
        view.live = null;
      }
      return;
    }
    if (phase !== 'writing' && phase !== 'running') return;

    if (!view.live) {
      view.live = el('div', 'step live');
      view.live.appendChild(el('span', 'dot'));
      view.live.appendChild(el('span', 'txt'));
      view.turn.insertBefore(view.live, view.bubble);
    }
    var label = labelFor(f.tool ? String(f.tool) : (view.live._tool || ''), phase);
    if (f.tool) view.live._tool = String(f.tool);
    view.live._label = label;
    view.live.querySelector('.txt').textContent = label + '…';
    if (stickBottom) toBottom();
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
    if (!state.model) {
      modelBtn.className = 'empty';
      modelBtn.textContent = 'No model yet';
    } else {
      modelBtn.className = '';
      modelBtn.textContent = pretty(state.model);
      var sub = el('span', 'sub', state.backend === 'ggml_metal' ? '  GPU' : state.backend === 'ggml_cpu' ? '  CPU' : '');
      modelBtn.appendChild(sub);
    }
    send.disabled = !state.model;
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

  // Requirement 9: open the most recent conversation, so the app resumes where
  // the user left off instead of greeting them with a blank page every launch.
  function resumeLatest() {
    return fetch('/api/agent/conversations').then(function (r) { return r.json(); }).then(function (d) {
      var list = (d && d.conversations) || [];
      if (!list.length) return newSession(null);
      var latest = list[0];
      return fetch('/api/agent/conversations/' + encodeURIComponent(latest.id))
        .then(function (r) { return r.json(); })
        .then(function (c) {
          var msgs = (c && c.messages) || [];
          if (msgs.length) {
            clearEmpty();
            msgs.forEach(function (m) {
              state.history.push({ role: m.role, content: m.content });
              addTurn(m.role, m.content, m.attachments);
            });
            toBottom();
          }
          return newSession(latest.id);
        })
        .catch(function () { return newSession(null); });
    }).catch(function () { return newSession(null); });
  }

  // ---- sending -------------------------------------------------------------
  function setGenerating(on) {
    state.generating = on;
    busy.className = on ? 'on' : '';
    send.textContent = on ? '■' : '➤';
    send.className = 'round ' + (on ? 'stop' : 'send');
    send.disabled = !on && !state.model;
  }

  function sendMessage() {
    if (state.generating) { stop(); return; }
    var t = text.value.trim();
    if (!t && !state.attachments.length) return;
    if (!state.model) { openSheet('model-sheet'); return; }

    var atts = state.attachments.slice();
    addTurn('user', t, atts);
    state.history.push({ role: 'user', content: t });
    text.value = ''; autoGrow();
    state.attachments = []; paintChips();

    var msg = { role: 'user', content: t || describe(atts) };
    var images = atts.filter(function (a) { return a.mediaType === 'image'; }).map(function (a) { return a.file; });
    var audio = atts.filter(function (a) { return a.mediaType === 'audio'; }).map(function (a) { return a.file; });
    var others = atts.filter(function (a) { return a.mediaType !== 'image' && a.mediaType !== 'audio'; }).map(function (a) { return a.file; });
    if (images.length) msg.imagePaths = images;
    if (audio.length) msg.audioPaths = audio;
    if (others.length) msg.filePaths = others;

    var body = {
      messages: state.history.slice(0, -1).map(function (h) { return { role: h.role, content: h.content }; }).concat([msg]),
      maxTokens: state.maxTokens,
      think: !!think.checked,
    };
    if (state.session) body.sessionId = state.session;
    if (state.skills.length) body.skills = state.skills;

    stream(body);
  }
  function describe(atts) {
    return atts.map(function (a) { return (a.fileName || a.file); }).join(', ');
  }

  function stop() {
    if (state.abort) { try { state.abort.abort(); } catch (e) {} }
    setGenerating(false);
  }

  function stream(body) {
    setGenerating(true);
    var view = addTurn('assistant', '');
    var answer = '', thinking = '', thinkBox = null, thinkBody = null;
    var steps = '', offered = false;
    var ctrl = new AbortController();
    state.abort = ctrl;

    fetch('/api/chat', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
      signal: ctrl.signal,
    }).then(function (res) {
      if (!res.ok) {
        return res.text().then(function (t) { throw new Error(t || ('HTTP ' + res.status)); });
      }
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
        }
        if (f.token) { answer += f.token; view.bubble.innerHTML = render(answer); }
        if (f.replace) { answer = f.replace; view.bubble.innerHTML = render(answer); }
        if (f.tool_progress) trace(view, f);
        if (f.detail || f.output) steps += ' ' + (f.detail || '') + ' ' + (f.output || '');
        if (f.error) {
          steps += ' ' + f.error;
          offered = offerNetworkIfRefused(String(f.error), offered);
          if (!offered) notice(String(f.error), 'error');
        }
        if (f.image || f.imageUrl) {
          var img = document.createElement('img');
          img.src = f.imageUrl || f.image;
          view.bubble.appendChild(img);
        }
        if (stickBottom) toBottom();
      }
      function finish() {
        if (view.live) { view.live.className = 'step done'; view.live = null; }
        offered = offerNetworkIfRefused(answer + ' ' + steps, offered);
        state.history.push({ role: 'assistant', content: answer });
        if (answer) addCopy(view.turn, function () { return answer; });
        setGenerating(false);
        state.abort = null;
      }
      return pump();
    }).catch(function (e) {
      if (e && e.name === 'AbortError') { setGenerating(false); return; }
      notice((e && e.message) || 'The request failed.', 'error');
      setGenerating(false);
      state.abort = null;
    });
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

  $('menu').addEventListener('click', function () { openSheet('nav-sheet'); });
  document.querySelectorAll('#nav-sheet .opt').forEach(function (b) {
    b.addEventListener('click', function () {
      closeSheets();
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
  function loadSkills() {
    return fetch('/api/skills').then(function (r) { return r.json(); }).then(function (d) {
      state.catalogSkills = (d && d.skills) || [];
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

  $('skills-btn').addEventListener('click', function () {
    loadSkills().then(function () { openSheet('skills-sheet'); });
  });

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
    state.skills.forEach(function (n) { box.appendChild(el('span', 'skillchip', '🧩 ' + n)); });
  }

  // ---- composer ------------------------------------------------------------
  function autoGrow() {
    text.style.height = 'auto';
    text.style.height = Math.min(text.scrollHeight, window.innerHeight * 0.26) + 'px';
  }
  text.addEventListener('input', autoGrow);
  send.addEventListener('click', sendMessage);
  $('new').addEventListener('click', function () {
    state.history = []; state.attachments = []; paintChips();
    chat.innerHTML = '';
    var e = el('div', null, '');
    e.id = 'empty';
    e.innerHTML = '<h1>TensorAgent</h1><p>New chat.</p>';
    chat.appendChild(e);
    newSession(null);
  });

  // Requirement 5: a voice mode you hold, not a button you hunt for.
  voice.addEventListener('change', function () {
    document.body.classList.toggle('voice', voice.checked);
    if (!voice.checked) text.focus();
  });
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
    if (voice.checked) {
      voice.checked = false;
      document.body.classList.remove('voice');
    }
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
  function learnHostWording() {
    return fetch('/api/agent/engine').then(function (r) { return r.json(); }).then(function (e) {
      if (e && typeof e.networkDisabledMessage === 'string') state.netMsg = e.networkDisabledMessage;
    }).catch(function () {});
  }

  function applySettings() {
    return fetch('/api/agent/settings').then(function (r) { return r.json(); }).then(function (s) {
      state.settings = s || null;
      if (s && typeof s.thinkByDefault === 'boolean') think.checked = s.thinkByDefault;
      if (s && Array.isArray(s.defaultSkills)) { state.skills = s.defaultSkills.slice(); paintSkillChips(); }
      if (s && typeof s.speechLanguage === 'string') state.speech = s.speechLanguage;
      paintLang();
      return s;
    }).catch(function () { return null; });
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
      if (!voice.checked) text.focus();
    },
    send: sendMessage,
    stop: stop,
    isGenerating: function () { return state.generating; },
    setThink: function (on) { think.checked = !!on; },
    setSkills: function (n) { state.skills = (n || []).slice(); paintSkillChips(); },
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

  // ---- start ---------------------------------------------------------------
  learnHostWording()
    .then(applySettings)
    .then(refreshModel)
    .then(resumeLatest)
    .then(function () { post('/api/agent/events', { type: 'ready', conversation: state.conversation }); })
    .catch(function (e) { notice('Could not start: ' + ((e && e.message) || e), 'error'); });
})();
