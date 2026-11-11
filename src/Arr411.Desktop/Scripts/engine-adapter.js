/* Arr411 Desktop: gives the hidden engine page its window.QbtEngine, the way the Android app does (see EngineBridge.cs).
   Injected at document creation, before shim-engine.js captures it. */
(function () {
  'use strict';

  var ORIGIN = 'https://engine.arr411.local';
  var webview = window.chrome && window.chrome.webview;
  // Only the engine page itself gets the bridge: not frames, not other sites.
  if (window.top !== window || location.origin !== ORIGIN || window.QbtEngine || !webview) return;

  function isObject(value) {
    return !!value && typeof value === 'object' && !Array.isArray(value);
  }

  // The engine reads its settings synchronously, so they must be here before it starts.
  // Fetching them also tells the window that a new page is loading.
  var config = {};
  try {
    var xhr = new XMLHttpRequest();
    xhr.open('GET', '/native/state', false);
    xhr.send();
    if (xhr.status !== 200) throw new Error('state ' + xhr.status);
    var initial = JSON.parse(JSON.parse(xhr.responseText).config);
    if (isObject(initial)) config = initial;
  } catch (e) {
    console.error('Arr411: engine state unavailable', e);
    return;
  }

  function post(message) {
    webview.postMessage(message);
  }

  function merge(patch) {
    if (!isObject(patch)) return;
    Object.keys(patch).forEach(function (key) {
      config[key] = patch[key];
    });
  }

  var native = {
    ready: function () {
      post({ t: 'ready' });
    },
    reply: function (id, json) {
      post({ t: 'reply', id: String(id), json: String(json) });
    },
    emit: function (json) {
      post({ t: 'emit', json: String(json) });
    },
    storageGet: function () {
      return JSON.stringify(config);
    },
    storageSet: function (json) {
      var patch;
      try {
        patch = JSON.parse(json);
      } catch (e) {
        return;
      }
      if (!isObject(patch)) return;
      merge(patch);
      post({ t: 'storageSet', json: String(json) });
    },
    openUrl: function (url) {
      post({ t: 'openUrl', url: String(url) });
    },
    notify: function (title, text) {
      post({ t: 'notify', title: String(title), text: String(text) });
    },
    http: function (request, id) {
      post({ t: 'http', id: String(id), req: String(request) });
    }
  };

  var fromWindow = {
    handle: function (m) {
      var id = String(m.id);
      try {
        window.__engineHandle(id, String(m.raw));
      } catch (e) {
        // Whatever went wrong, the caller must not wait for an answer that will never come.
        console.error('Arr411: engine request failed', e);
        post({ t: 'reply', id: id, json: JSON.stringify({ ok: false, error: 'Moteur non prêt' }) });
      }
    },
    httpDone: function (m) {
      var outcome = m.error ? { error: String(m.error) } : { status: Number(m.status) || 0, text: m.text == null ? '' : String(m.text) };
      window.__httpDone(String(m.id), JSON.stringify(outcome));
    },
    config: function (m) {
      merge(m.patch);
    }
  };

  webview.addEventListener('message', function (event) {
    var message = event.data;
    if (!isObject(message) || !Object.prototype.hasOwnProperty.call(fromWindow, message.t)) return;
    try {
      fromWindow[message.t](message);
    } catch (e) {
      console.error('Arr411: ' + message.t, e);
    }
  });

  Object.defineProperty(window, 'QbtEngine', { value: Object.freeze(native), enumerable: false });
})();
