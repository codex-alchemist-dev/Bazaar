// Athanor editor page bridge (doc 57 §4). Runs inside the WebView2 island next to the pinned Monaco bundle.
//
// Rules this file keeps:
//  - Every message in either direction is one JSON text with a "type" and a rising "seq".
//  - The page never builds code from message data: no dynamic code evaluation and no HTML string assignment. Content is only
//    ever passed to Monaco as a string value.
//  - A host message whose seq does not rise is refused with an "error" message and is not applied.
//  - The only keys the page swallows are the chords in the host's claim table, and never a Ctrl chord (doc 57 §3a, §7).
(function () {
  'use strict';

  var webview = window.chrome && window.chrome.webview;
  if (!webview) {
    return; // opened outside a WebView2 host: nothing to talk to
  }

  var outSeq = 0;
  var inSeq = 0;
  var claimed = new Set();
  var documents = new Map(); // documentId -> { model, baseline, version, readOnly }
  var monacoApi = null;
  var editor = null;
  var diffEditor = null;
  var diffHost = null;
  var activeId = null;
  var applyingHostEdit = false;

  function send(message) {
    outSeq += 1;
    message.seq = outSeq;
    webview.postMessage(JSON.stringify(message));
  }

  function sendError(code, text, documentId) {
    var message = { type: 'error', seq: 0, code: code, message: String(text) };
    if (documentId) {
      message.documentId = documentId;
    }
    send(message);
  }

  // JSON and JSONC are not Monaco basic languages (they come from a language service the trimmed bundle drops), so the
  // page registers a small tokenizer for them.
  function registerJson(languages) {
    var rules = {
      tokenizer: {
        root: [
          [/"(?:[^"\\]|\\.)*"(?=\s*:)/, 'type'],
          [/"(?:[^"\\]|\\.)*"/, 'string'],
          [/-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?/, 'number'],
          [/\b(?:true|false|null)\b/, 'keyword'],
          [/\/\/.*$/, 'comment'],
          [/\/\*/, 'comment', '@comment'],
          [/[{}[\],:]/, 'delimiter']
        ],
        comment: [
          [/[^/*]+/, 'comment'],
          [/\*\//, 'comment', '@pop'],
          [/[/*]/, 'comment']
        ]
      }
    };
    ['json', 'jsonc'].forEach(function (id) {
      languages.register({ id: id });
      languages.setMonarchTokensProvider(id, rules);
    });
  }

  function chordOf(event) {
    var parts = [];
    if (event.altKey) { parts.push('Alt'); }
    if (event.metaKey) { parts.push('Meta'); }
    if (event.shiftKey) { parts.push('Shift'); }
    parts.push(event.code);
    return parts.join('+');
  }

  window.addEventListener('keydown', function (event) {
    if (event.ctrlKey || claimed.size === 0) {
      return;
    }
    if (claimed.has(chordOf(event))) {
      event.preventDefault();
      event.stopPropagation();
      send({
        type: 'keyPressed',
        seq: 0,
        key: event.code,
        shift: event.shiftKey,
        alt: event.altKey,
        meta: event.metaKey,
        ctrl: false
      });
    }
  }, true);

  function showEditor() {
    if (diffEditor) {
      var pair = diffEditor.getModel();
      diffEditor.dispose();
      if (pair) {
        pair.original.dispose();
        pair.modified.dispose();
      }
      diffEditor = null;
    }
    if (diffHost) {
      diffHost.remove();
      diffHost = null;
    }
    document.getElementById('ed').style.display = '';
  }

  function applyReadOnly() {
    var entry = activeId ? documents.get(activeId) : null;
    editor.updateOptions({ readOnly: entry ? entry.readOnly : false });
  }

  function open(message) {
    var id = message.documentId;
    var entry = documents.get(id);
    if (!entry) {
      var uri = monacoApi.Uri.from({ scheme: 'athanor', path: '/' + id });
      var model = monacoApi.editor.createModel(message.content, message.languageId, uri);
      entry = { model: model, baseline: message.content, version: 0, readOnly: !!message.readOnly };
      documents.set(id, entry);
      model.onDidChangeContent(function () {
        if (applyingHostEdit) {
          return;
        }
        entry.version += 1;
        var text = model.getValue();
        send({ type: 'changed', seq: 0, documentId: id, version: entry.version, content: text, dirty: text !== entry.baseline });
      });
    } else {
      applyingHostEdit = true;
      entry.model.setValue(message.content);
      applyingHostEdit = false;
      entry.baseline = message.content;
      entry.readOnly = !!message.readOnly;
    }
    showEditor();
    activeId = id;
    editor.setModel(entry.model);
    applyReadOnly();
    editor.layout();
  }

  function close(message) {
    var entry = documents.get(message.documentId);
    if (!entry) {
      return;
    }
    if (activeId === message.documentId) {
      editor.setModel(null);
      activeId = null;
    }
    entry.model.dispose();
    documents.delete(message.documentId);
  }

  function setTheme(message) {
    var theme = JSON.parse(message.themeJson);
    if (!theme || typeof theme !== 'object' || !Array.isArray(theme.rules) || typeof theme.colors !== 'object') {
      throw new Error('theme payload is not a Monaco defineTheme object');
    }
    monacoApi.editor.defineTheme('athanor', theme);
    monacoApi.editor.setTheme('athanor');
  }

  function showDiff(message) {
    showEditor();
    document.getElementById('ed').style.display = 'none';
    diffHost = document.createElement('div');
    diffHost.style.cssText = 'position:absolute;inset:0';
    document.body.appendChild(diffHost);
    diffEditor = monacoApi.editor.createDiffEditor(diffHost, { automaticLayout: true, renderSideBySide: true, readOnly: true });
    diffEditor.setModel({
      original: monacoApi.editor.createModel(message.original, message.languageId),
      modified: monacoApi.editor.createModel(message.modified, message.languageId)
    });
  }

  function applyEdit(message) {
    var entry = documents.get(message.documentId);
    if (!entry) {
      throw new Error('unknown document');
    }
    var range = new monacoApi.Range(message.startLine, message.startColumn, message.endLine, message.endColumn);
    entry.model.applyEdits([{ range: range, text: message.text }]);
  }

  var handlers = {
    open: open,
    close: close,
    setTheme: setTheme,
    setReadOnly: function (m) {
      var entry = documents.get(m.documentId);
      if (entry) {
        entry.readOnly = !!m.readOnly;
        applyReadOnly();
      }
    },
    showDiff: showDiff,
    applyEdit: applyEdit,
    focus: function () { (diffEditor ? diffEditor.getModifiedEditor() : editor).focus(); },
    setClaimedKeys: function (m) { claimed = new Set(Array.isArray(m.keys) ? m.keys : []); }
  };

  webview.addEventListener('message', function (event) {
    var message;
    try {
      message = JSON.parse(event.data);
    } catch (e) {
      sendError('bad-json', 'the host message is not JSON');
      return;
    }
    if (!message || typeof message.type !== 'string' || typeof message.seq !== 'number') {
      sendError('bad-message', 'the host message has no type or seq');
      return;
    }
    if (message.seq <= inSeq) {
      sendError('stale-seq', 'stale seq ' + message.seq + ' (last ' + inSeq + ')');
      return;
    }
    inSeq = message.seq;
    if (!editor) {
      sendError('not-ready', 'the editor is not ready');
      return;
    }
    var handler = Object.prototype.hasOwnProperty.call(handlers, message.type) ? handlers[message.type] : null;
    if (!handler) {
      sendError('unknown-type', 'unknown message type ' + message.type);
      return;
    }
    try {
      handler(message);
    } catch (e) {
      sendError('handler-failed', e && e.message ? e.message : e, typeof message.documentId === 'string' ? message.documentId : undefined);
    }
  });

  self.MonacoEnvironment = { getWorkerUrl: function () { return 'monaco/vs/editor/editor.worker.js'; } };
  require.config({ paths: { vs: 'monaco/vs' } });
  require(['vs/editor', 'vs/basic-languages/monaco.contribution'], function (api) {
    monacoApi = api;
    registerJson(api.languages);
    editor = api.editor.create(document.getElementById('ed'), {
      automaticLayout: true,
      model: null,
      minimap: { enabled: false },
      fixedOverflowWidgets: true
    });
    editor.onDidChangeCursorSelection(function (e) {
      if (!activeId) {
        return;
      }
      var s = e.selection;
      send({
        type: 'selection',
        seq: 0,
        documentId: activeId,
        startLine: s.selectionStartLineNumber,
        startColumn: s.selectionStartColumn,
        endLine: s.positionLineNumber,
        endColumn: s.positionColumn
      });
    });
    send({ type: 'ready', seq: 0 });
  }, function (e) {
    sendError('load-failed', e && e.message ? e.message : 'the Monaco bundle failed to load');
  });
})();
