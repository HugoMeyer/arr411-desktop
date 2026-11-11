/* Arr411 Desktop: gives the C411 page its window.QbtPage, the way the Android app does (see PageBridge.cs).
   Injected at document creation, before the bundle of shim-page.js / page-hook.js / content.js (see PageBundle.cs) looks for it. */
(function () {
  'use strict';

  var webview = window.chrome && window.chrome.webview;

  function isC411(host) {
    return host === 'c411.org' || host.endsWith('.c411.org');
  }

  // Only the page of C411 itself gets the bridge: not its frames (the Cloudflare challenge...), not other sites.
  if (window.top !== window || location.protocol !== 'https:' || !isC411(location.hostname) || window.QbtPage || !webview) return;

  function post(message) {
    webview.postMessage(message);
  }

  var native = {
    send: function (json, id) {
      post({ t: 'send', id: String(id), msg: String(json) });
    },
    download: function (url, filename) {
      post({ t: 'download', url: String(url), filename: String(filename || '') });
    },
    // Only the Android app needs it (its browser cannot save a blob). Here the browser saves downloads itself, so the page may not
    // write files: there is nothing to send, and the bridge has no such message.
    saveFile: function () {},
    toast: function (text) {
      post({ t: 'toast', text: String(text) });
    },
    fetchBytes: function (url, referer, id) {
      post({ t: 'fetchBytes', url: String(url), referer: String(referer || ''), id: String(id) });
    }
  };

  // The shim defines these two when it starts; a message that comes before has nobody to go to.
  var fromWindow = {
    reply: function (m) {
      if (typeof window.__qbtReply === 'function') window.__qbtReply(String(m.id), String(m.json));
    },
    event: function (m) {
      if (typeof window.__qbtEvent === 'function') window.__qbtEvent(String(m.json));
    }
  };

  webview.addEventListener('message', function (event) {
    var message = event.data;
    if (!message || typeof message !== 'object' || !Object.prototype.hasOwnProperty.call(fromWindow, message.t)) return;
    try {
      fromWindow[message.t](message);
    } catch (e) {
      console.error('Arr411: ' + message.t, e);
    }
  });

  Object.defineProperty(window, 'QbtPage', { value: Object.freeze(native), enumerable: false });
})();
