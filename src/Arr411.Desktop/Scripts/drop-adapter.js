/* Arr411 Desktop: lets the user drop magnet links and .torrent files on a page (the web app, the C411 site), the way the window takes them
   on its navigation bar (see MainForm.cs). The browser hands a drop to the page and never to the window: this script reports it.
   Injected at document creation, after the adapter of the page. */
(function () {
  'use strict';

  var webview = window.chrome && window.chrome.webview;
  // Only the top-level page: not its frames.
  if (window.top !== window || !webview) return;

  // The limits of the window (LaunchRequest, TorrentFile, DroppedContent), which checks everything again: they only keep a mistake from
  // reading a big file or sending a lot of data.
  var MAX_FILES = 20;
  var MAX_FILE_BYTES = 20 * 1024 * 1024;
  var MAX_TOTAL_BYTES = 2 * MAX_FILE_BYTES;
  var MAX_TEXT = 64 * 1024 + 1; // one more than the window takes, so that it sees a text that is too long

  // What is worth reporting in a text: a magnet link, a link to C411.
  var LINK = /magnet:|https?:\/\/([a-z0-9-]+\.)*c411\.org\b/i;

  function has(transfer, type) {
    var types = transfer && transfer.types;
    for (var i = 0; types && i < types.length; i++) {
      if (types[i] === type) return true;
    }
    return false;
  }

  // Text dropped in a field, a file dropped on a file input: the browser handles them, as the user expects.
  function editable(target) {
    return !!target && target.nodeType === 1 && (target.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName));
  }

  // A zone of the site that took the drag keeps it: it already said so (defaultPrevented).
  function ours(event) {
    var transfer = event.dataTransfer;
    return (
      !event.defaultPrevented &&
      !editable(event.target) &&
      (has(transfer, 'Files') || has(transfer, 'text/plain') || has(transfer, 'text/uri-list'))
    );
  }

  // The links of the drag, one per line: the dragged text, else the address of a dragged link. The text comes first because the browser
  // makes one address out of a text of several lines (it drops the line breaks), where the text keeps each link apart.
  function linkText(transfer) {
    var text = transfer.getData('text/plain') || '';
    if (!LINK.test(text)) {
      text = (transfer.getData('text/uri-list') || '')
        .split(/\r?\n/)
        .filter(function (line) {
          return line && line.charAt(0) !== '#';
        })
        .join('\n');
    }
    return LINK.test(text) ? text.slice(0, MAX_TEXT) : '';
  }

  function isTorrent(file) {
    return /\.torrent$/i.test(file.name);
  }

  // Only a .torrent file of a reasonable size is read (a big file dropped by mistake is not); the others are named, so that the window can say so.
  function read(file, entry) {
    return new Promise(function (resolve) {
      if (!entry) {
        resolve({ name: file.name });
        return;
      }
      var reader = new FileReader();
      reader.onload = function () {
        var result = String(reader.result);
        resolve({ name: file.name, base64: result.slice(result.indexOf(',') + 1) });
      };
      reader.onerror = function () {
        resolve({ name: file.name });
      };
      reader.readAsDataURL(file);
    });
  }

  // One file more than the window takes, so that it sees there are too many and says so; that one is never read.
  function collect(files) {
    var budget = MAX_TOTAL_BYTES;
    return Promise.all(
      files.map(function (file, index) {
        var wanted = index < MAX_FILES && isTorrent(file) && file.size <= MAX_FILE_BYTES && file.size <= budget;
        if (wanted) budget -= file.size;
        return read(file, wanted);
      })
    );
  }

  window.addEventListener('dragover', function (event) {
    if (!ours(event)) return;
    event.preventDefault();
    event.dataTransfer.dropEffect = 'copy';
  });

  window.addEventListener('drop', function (event) {
    if (!event.isTrusted || !ours(event)) return;

    // Whatever it is, the page does not navigate to it: the window is told, and says if there is nothing it can use. Only what is worth it
    // (a link, a .torrent file) is read; the rest of what was dropped stays here.
    event.preventDefault();
    var transfer = event.dataTransfer;
    var text = linkText(transfer);
    var files = Array.prototype.slice.call(transfer.files || [], 0, MAX_FILES + 1);

    collect(files).then(function (entries) {
      var message = { t: 'drop' };
      if (text) message.text = text;
      if (entries.length > 0) message.files = entries;
      webview.postMessage(message);
    });
  });
})();
