// A DOM the size of this page's needs, so tensoragent.js can be RUN by a test.
//
// Everything else about the page is checked by reading its source for a string,
// which cannot tell whether a photo comes back when a saved chat is reopened --
// the bug this exists for. What is stubbed is only what the script touches:
// elements with children, classes, text and handlers; a fetch that answers from
// a table the test fills in and records every request; and the two timers.
(function () {
  'use strict';

  function Element(tag) {
    this.tagName = String(tag || 'div').toUpperCase();
    this.children = [];
    this.attrs = {};
    this.handlers = {};
    this.parentNode = null;
    this._class = '';
    this._text = '';
    this._html = '';
    this.style = { setProperty: function () {}, height: '', display: '' };
    this.classList = {
      _el: this,
      add: function (c) { this._el.className = (this._el.className + ' ' + c).trim(); },
      remove: function (c) {
        this._el.className = this._el.className.split(/\s+/).filter(function (x) { return x && x !== c; }).join(' ');
      },
      toggle: function (c, on) { on ? this.add(c) : this.remove(c); },
      contains: function (c) { return this._el.className.split(/\s+/).indexOf(c) >= 0; },
    };
  }

  Object.defineProperty(Element.prototype, 'className', {
    get: function () { return this._class; },
    set: function (v) { this._class = v == null ? '' : String(v); },
  });
  Object.defineProperty(Element.prototype, 'textContent', {
    get: function () {
      if (this.children.length === 0) return this._text;
      return this.children.map(function (c) { return c.textContent; }).join('');
    },
    set: function (v) { this._text = v == null ? '' : String(v); this.children = []; },
  });
  // innerHTML is kept as the raw markup the page produced. The page renders its
  // own Markdown into it, so parsing would only re-implement a browser; what a
  // test needs is what was written, and that is what is stored.
  Object.defineProperty(Element.prototype, 'innerHTML', {
    get: function () { return this._html; },
    set: function (v) { this._html = v == null ? '' : String(v); this.children = []; this._text = ''; },
  });

  Element.prototype.appendChild = function (child) {
    child.parentNode = this;
    this.children.push(child);
    return child;
  };
  Element.prototype.insertBefore = function (child, before) {
    var at = this.children.indexOf(before);
    child.parentNode = this;
    if (at < 0) this.children.push(child); else this.children.splice(at, 0, child);
    return child;
  };
  Element.prototype.remove = function () {
    if (!this.parentNode) return;
    var at = this.parentNode.children.indexOf(this);
    if (at >= 0) this.parentNode.children.splice(at, 1);
    this.parentNode = null;
  };
  Element.prototype.setAttribute = function (k, v) { this.attrs[k] = String(v); };
  Element.prototype.getAttribute = function (k) { return Object.prototype.hasOwnProperty.call(this.attrs, k) ? this.attrs[k] : null; };
  Element.prototype.removeAttribute = function (k) { delete this.attrs[k]; };
  Element.prototype.addEventListener = function (name, fn) { (this.handlers[name] = this.handlers[name] || []).push(fn); };
  Element.prototype.removeEventListener = function () {};
  Element.prototype.focus = function () {};
  Element.prototype.dispatch = function (name, event) {
    (this.handlers[name] || []).forEach(function (fn) { fn(event || { preventDefault: function () {} }); });
    var direct = this['on' + name];
    if (typeof direct === 'function') direct(event || {});
  };

  function matches(el, selector) {
    if (selector.charAt(0) === '.') return el.classList.contains(selector.slice(1));
    return el.tagName === selector.toUpperCase();
  }
  Element.prototype.querySelectorAll = function (selector) {
    var last = selector.split(/\s+/).pop(), found = [];
    (function walk(node) {
      node.children.forEach(function (c) { if (matches(c, last)) found.push(c); walk(c); });
    })(this);
    return found;
  };
  Element.prototype.querySelector = function (selector) {
    var all = this.querySelectorAll(selector);
    return all.length ? all[0] : null;
  };

  var byId = {};
  function element(tag) { return new Element(tag); }

  var document = {
    body: element('body'),
    documentElement: element('html'),
    visibilityState: 'visible',
    createElement: element,
    createTextNode: function (t) { var n = element('span'); n.textContent = t; return n; },
    getElementById: function (id) {
      if (!byId[id]) { byId[id] = element('div'); byId[id].attrs.id = id; }
      return byId[id];
    },
    querySelectorAll: function (selector) {
      // Only the two sheet-scoped lookups the page makes, and both are lists of
      // buttons a test never presses.
      var scoped = selector.split(/\s+/);
      if (scoped.length > 1 && scoped[0].charAt(0) === '#') {
        return document.getElementById(scoped[0].slice(1)).querySelectorAll(scoped.slice(1).join(' '));
      }
      return document.body.querySelectorAll(selector);
    },
    addEventListener: function (name, fn) { (document._handlers[name] = document._handlers[name] || []).push(fn); },
    _handlers: {},
  };
  document.documentElement.style = { setProperty: function () {} };

  // The one element with structure the page reads back out of it.
  var activity = document.getElementById('activity');
  var label = element('div'); label.className = 'label'; activity.appendChild(label);
  var tail = element('div'); tail.className = 'tail'; activity.appendChild(tail);

  // ---- the network ---------------------------------------------------------
  // Answers come from a table the test fills in; every request is recorded, so a
  // test can assert on the body the page SENT as well as on what it drew.
  var routes = {};
  var calls = [];

  /** An event stream, frame by frame, the way the page reads one. */
  function sseBody(frames) {
    var at = 0;
    return {
      getReader: function () {
        return {
          read: function () {
            if (at >= frames.length) return Promise.resolve({ done: true });
            var line = 'data: ' + JSON.stringify(frames[at++]) + '\n';
            return Promise.resolve({ done: false, value: line });
          },
        };
      },
    };
  }

  function reply(body, ok, headers) {
    // A route may answer with frames instead of a document: { __sse: [...] } is a
    // server-sent-event stream, which is how every generation actually arrives.
    var frames = body && body.__sse;
    var text = typeof body === 'string' ? body : JSON.stringify(body);
    return Promise.resolve({
      ok: ok !== false,
      status: ok === false ? 500 : 200,
      headers: { get: function (n) { return (headers || {})[n] || null; } },
      json: function () { return Promise.resolve(JSON.parse(text)); },
      text: function () { return Promise.resolve(text); },
      body: frames ? sseBody(frames) : null,
    });
  }

  function fetch(url, init) {
    var path = String(url).split('?')[0];
    var record = { url: String(url), path: path, method: (init && init.method) || 'GET', body: null };
    if (init && typeof init.body === 'string') { try { record.body = JSON.parse(init.body); } catch (e) { record.body = init.body; } }
    calls.push(record);
    var answer = Object.prototype.hasOwnProperty.call(routes, String(url)) ? routes[String(url)]
      : (Object.prototype.hasOwnProperty.call(routes, path) ? routes[path] : null);
    if (typeof answer === 'function') answer = answer(record);
    return reply(answer === null || answer === undefined ? {} : answer);
  }

  var globals = {
    document: document,
    fetch: fetch,
    // The shim's own TextDecoder wants bytes; this stream yields the text a
    // decoded chunk would already be, so decode is the identity here.
    TextDecoder: function () { this.decode = function (value) { return value == null ? '' : String(value); }; },
    AbortController: function () { this.signal = {}; this.abort = function () {}; },
    navigator: { clipboard: null },
    location: { href: 'http://127.0.0.1/' },
    innerHeight: 800,
    scrollY: 0,
    scrollTo: function () {},
    visualViewport: null,
    addEventListener: function () {},
  };
  Object.keys(globals).forEach(function (k) { globalThis[k] = globals[k]; });
  globalThis.window = globalThis;

  globalThis.__page = {
    byId: byId,
    routes: routes,
    calls: calls,
    element: element,
    /** Every request whose path matches, newest last. */
    requests: function (path) { return calls.filter(function (c) { return c.path === path; }); },
    /** The transcript as a shape a test can assert on. */
    transcript: function () {
      return document.getElementById('chat').children.map(function (turn) {
        var media = [];
        (function walk(node) {
          node.children.forEach(function (c) {
            if (['IMG', 'AUDIO', 'VIDEO', 'A'].indexOf(c.tagName) >= 0) {
              media.push({ tag: c.tagName, src: c.src || c.href || '', text: c.textContent, poster: c.poster || '' });
            }
            walk(c);
          });
        })(turn);
        var html = '';
        (function walk(node) {
          html += node.innerHTML || '';
          node.children.forEach(walk);
        })(turn);
        return {
          role: turn.className.indexOf('me') >= 0 ? 'user' : 'assistant',
          html: html,
          text: turn.textContent,
          media: media,
        };
      });
    },
  };
})();
