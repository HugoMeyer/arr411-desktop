/* Arr411 Desktop: gives the web app its window.Native, the way the Android app does (see SpaBridge.cs).
   Injected at document creation, before any script of the page runs. */
(function () {
  'use strict';

  var ORIGIN = 'https://arr411.local';
  var webview = window.chrome && window.chrome.webview;
  // Only the top-level web app gets the bridge: not frames, not other sites.
  if (window.top !== window || location.origin !== ORIGIN || window.Native || !webview) return;

  // The web app reads its settings synchronously, so they must be here before it starts.
  // Fetching them also tells the window that a new page is loading.
  var state;
  try {
    var xhr = new XMLHttpRequest();
    xhr.open('GET', '/native/state', false);
    xhr.send();
    if (xhr.status !== 200) throw new Error('state ' + xhr.status);
    state = JSON.parse(xhr.responseText);
  } catch (e) {
    console.error('Arr411: native state unavailable', e);
    return;
  }

  function isObject(value) {
    return !!value && typeof value === 'object' && !Array.isArray(value);
  }

  var config = {};
  try {
    var initial = JSON.parse(state.config);
    if (isObject(initial)) config = initial;
  } catch (e) {
    config = {};
  }

  // Answers of the network calls, until the page takes them.
  var bodies = Object.create(null);
  var inFlight = Object.create(null);

  function post(message) {
    webview.postMessage(message);
  }

  function merge(patch, dropTracked) {
    if (!isObject(patch)) return;
    Object.keys(patch).forEach(function (key) {
      // The download tracking belongs to the engine; the page only holds a stale copy.
      if (dropTracked && key === 'tracked') return;
      config[key] = patch[key];
    });
  }

  function app(name) {
    var App = window.App;
    if (!App || typeof App[name] !== 'function') return undefined;
    return App[name].apply(App, Array.prototype.slice.call(arguments, 1));
  }

  var native = {
    ready: function () {
      post({ t: 'ready' });
    },
    http: function (request, id) {
      id = String(id);
      inFlight[id] = true;
      post({ t: 'http', id: id, req: String(request) });
    },
    take: function (id) {
      id = String(id);
      var body = bodies[id];
      delete bodies[id];
      return body === undefined ? null : body;
    },
    config: function () {
      return JSON.stringify(config);
    },
    saveConfig: function (patchJson) {
      var patch;
      try {
        patch = JSON.parse(patchJson);
      } catch (e) {
        return;
      }
      if (!isObject(patch)) return;
      merge(patch, true);
      post({ t: 'saveConfig', patch: String(patchJson) });
    },
    currentTab: function () {
      return state.tab || '';
    },
    appVersion: function () {
      return state.version || '';
    },
    selectTab: function (name) {
      post({ t: 'selectTab', tab: String(name) });
    },
    openC411: function (url) {
      post({ t: 'openC411', url: String(url) });
    },
    reloadC411: function () {
      post({ t: 'reloadC411' });
    },
    openUrl: function (url) {
      post({ t: 'openUrl', url: String(url) });
    },
    toast: function (text) {
      post({ t: 'toast', text: String(text) });
    },
    setBadge: function (count) {
      post({ t: 'setBadge', n: Number(count) || 0 });
    },
    copy: function (text) {
      post({ t: 'copy', text: String(text) });
    },
    haptic: function () {
      // No vibration on a PC.
    }
  };

  var fromWindow = {
    httpDone: function (m) {
      var id = String(m.id);
      if (!inFlight[id]) return;
      delete inFlight[id];
      bodies[id] = m.body == null ? '' : String(m.body);
      try {
        if (typeof window.__nativeDone === 'function') window.__nativeDone(id, Number(m.status) || 0, m.error || null);
      } finally {
        delete bodies[id];
      }
    },
    config: function (m) {
      merge(m.patch, false);
    },
    showTab: function (m) {
      app('showTab', m.tab);
    },
    tabReselect: function (m) {
      app('popToRoot', m.tab);
      app('showTab', m.tab);
    },
    shared: function (m) {
      app('handleShared', m.data);
    },
    foreground: function (m) {
      app('setForeground', !!m.on);
    },
    loadCfg: function () {
      app('loadCfg');
    },
    back: function (m) {
      var handled = false;
      try {
        handled = !!app('back');
      } catch (e) {
        handled = false;
      }
      post({ t: 'reply', id: String(m.id), value: handled });
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

  // The "Mode ordinateur" switch of the settings makes the Android WebView ask sites for their desktop version. This window already is a
  // desktop one and nothing reads the setting, so the switch is hidden. The observer runs before the page is painted again: no flash.
  function hideDesktopMode() {
    var switches = document.querySelectorAll('label.switch');
    for (var i = 0; i < switches.length; i++) {
      var caption = switches[i].querySelector('.lbl > span');
      if (caption && caption.textContent.trim() === 'Mode ordinateur') switches[i].style.setProperty('display', 'none', 'important');
    }
  }
  new MutationObserver(hideDesktopMode).observe(document, { childList: true, subtree: true });

  Object.defineProperty(window, 'Native', { value: Object.freeze(native), enumerable: false });

  // The web app is made for a phone: its library is three columns of posters, or rows as wide as the screen. In a wider window the library
  // gets as many columns as fit, with posters of a reasonable size, as the web version of the app does. Nothing below may stop the app from starting.
  try {
    var layout = new CSSStyleSheet();
    layout.replaceSync([
      '@media (min-width: 560px) { .grid { grid-template-columns: repeat(auto-fill, minmax(160px, 1fr)); } }',
      '@media (min-width: 760px) {',
      '  .rows { display: grid; grid-template-columns: repeat(auto-fill, minmax(380px, 1fr)); gap: 8px 10px; }',
      '  .rows .lrow { margin-bottom: 0; }',
      '}'
    ].join('\n'));
    document.adoptedStyleSheets = document.adoptedStyleSheets.concat([layout]);
  } catch (e) {
    console.error('Arr411: layout', e);
  }

  // The web app loads a long list piece by piece (App.chunked): when a marker at the end of the list comes near the viewport, it adds the next
  // piece. An IntersectionObserver tells when the marker comes near, not that it still is once the piece is there. With the many columns of a wide
  // window a piece is shorter than the window, so the marker stays near and the rest of the list would never load. After the page has handled
  // a marker that is near and has pushed down, the observer is asked again.
  try {
    var Observer = window.IntersectionObserver;
    var targets = new WeakMap();

    var lookAgain = function (observer, entry) {
      var watched = targets.get(observer);
      var target = entry.target;
      if (!watched || !entry.isIntersecting || !watched.has(target)) return;
      if (Math.abs(target.getBoundingClientRect().top - entry.boundingClientRect.top) < 1) return;
      requestAnimationFrame(function () {
        if (!watched.has(target)) return;
        Observer.prototype.unobserve.call(observer, target);
        Observer.prototype.observe.call(observer, target);
      });
    };

    window.IntersectionObserver = class extends Observer {
      constructor(callback, options) {
        super(function (entries, observer) {
          callback.call(observer, entries, observer);
          entries.forEach(function (entry) {
            lookAgain(observer, entry);
          });
        }, options);
        targets.set(this, new Set());
      }

      observe(target) {
        targets.get(this).add(target);
        super.observe(target);
      }

      unobserve(target) {
        targets.get(this).delete(target);
        super.unobserve(target);
      }

      disconnect() {
        targets.get(this).clear();
        super.disconnect();
      }
    };
  } catch (e) {
    console.error('Arr411: lazy lists', e);
  }
})();
