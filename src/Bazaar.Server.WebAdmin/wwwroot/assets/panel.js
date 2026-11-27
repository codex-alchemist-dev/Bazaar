/* Bazaar admin panel. Plain JavaScript, no framework, no external requests.
 * Every piece of server data is written with textContent, never as markup. */
(function () {
  "use strict";

  var API = "/admin/api/";
  var WRITE_HEADER = "X-Bazaar-Admin";
  var MAX_DEPTH = 4;

  var TEXT = {
    forbidden: "You do not have permission to view this section.",
    unavailable: "This section is unavailable.",
    expired: "Your session has ended. Sign in again.",
    network: "Could not reach the server. Try again.",
    conflict: "The module selection changed elsewhere. Reload to see the latest selection.",
    loading: "Loading..."
  };

  var SECTIONS = [
    { id: "status", label: "Status", capability: null },
    { id: "kodo", label: "Kodo health", capability: "kodo.view.status" },
    { id: "users", label: "Agents and members", capability: "admin.members" },
    { id: "devices", label: "Devices", capability: "admin.devices" },
    { id: "audit", label: "Audit log", capability: "kodo.view.audit" },
    { id: "approvals", label: "Approvals", capability: "approvals.decide" },
    { id: "modules", label: "Modules", capability: "admin.modules" }
  ];

  var el = {
    serverName: document.getElementById("server-name"),
    signedOut: document.getElementById("signed-out"),
    signedOutNote: document.getElementById("signed-out-note"),
    signinLink: document.getElementById("signin-link"),
    app: document.getElementById("app"),
    tablist: document.getElementById("tabs"),
    panels: document.getElementById("panels"),
    accountBar: document.getElementById("account-bar"),
    signout: document.getElementById("signout"),
    announce: document.getElementById("announce")
  };

  var tabs = [];

  /* ---------- small DOM helpers ---------- */

  function make(tag, className, text) {
    var node = document.createElement(tag);
    if (className) {
      node.className = className;
    }
    if (text !== undefined && text !== null) {
      node.textContent = String(text);
    }
    return node;
  }

  function clear(node) {
    while (node.firstChild) {
      node.removeChild(node.firstChild);
    }
  }

  function note(parent, text, isError) {
    var p = make("p", isError ? "notice error" : "notice", text);
    parent.appendChild(p);
    return p;
  }

  function announce(text) {
    el.announce.textContent = text;
  }

  function isPlainObject(v) {
    return v !== null && typeof v === "object" && !Array.isArray(v);
  }

  function hasOwn(obj, key) {
    return Object.prototype.hasOwnProperty.call(obj, key);
  }

  function newKey() {
    var c = window.crypto;
    if (c && typeof c.randomUUID === "function") {
      return c.randomUUID();
    }
    var b = new Uint8Array(16);
    c.getRandomValues(b);
    b[6] = (b[6] & 0x0f) | 0x40;
    b[8] = (b[8] & 0x3f) | 0x80;
    var hex = [];
    for (var i = 0; i < b.length; i++) {
      hex.push(("0" + b[i].toString(16)).slice(-2));
    }
    return hex.slice(0, 4).join("") + "-" + hex.slice(4, 6).join("") + "-" +
      hex.slice(6, 8).join("") + "-" + hex.slice(8, 10).join("") + "-" + hex.slice(10).join("");
  }

  /* ---------- HTTP ---------- */

  // Resolves to { status, ok, data }. data is parsed JSON or null. Never rejects on HTTP errors.
  function request(method, path, body, extraHeaders) {
    var headers = { Accept: "application/json" };
    var init = { method: method, credentials: "same-origin", cache: "no-store", headers: headers };
    if (method !== "GET") {
      headers[WRITE_HEADER] = "1";
      headers["Content-Type"] = "application/json";
      init.body = JSON.stringify(body === undefined ? {} : body);
    }
    if (extraHeaders) {
      Object.keys(extraHeaders).forEach(function (k) {
        headers[k] = extraHeaders[k];
      });
    }
    return fetch(path, init).then(function (res) {
      return res.text().then(function (text) {
        var data = null;
        if (text) {
          try {
            data = JSON.parse(text);
          } catch (e) {
            data = null;
          }
        }
        return { status: res.status, ok: res.ok, data: data };
      });
    });
  }

  function httpFailure(res) {
    return "Could not load this section (HTTP " + res.status + ").";
  }

  /* ---------- sign-in state ---------- */

  function showSignedOut(message) {
    tabs = [];
    clear(el.tablist);
    clear(el.panels);
    el.app.hidden = true;
    el.accountBar.hidden = true;
    el.signedOut.hidden = false;
    el.signedOutNote.textContent = message || "";
  }

  function expire() {
    showSignedOut(TEXT.expired);
  }

  function allowedSections(session) {
    var caps = Array.isArray(session.capabilities) ? session.capabilities : null;
    var listed = Array.isArray(session.sections) ? session.sections : null;
    return SECTIONS.filter(function (s) {
      if (listed) {
        return listed.indexOf(s.id) !== -1;
      }
      if (!s.capability || !caps) {
        return true;
      }
      return caps.indexOf(s.capability) !== -1;
    });
  }

  function enterPanel(session) {
    if (typeof session.serverName === "string" && session.serverName) {
      el.serverName.textContent = session.serverName;
    }
    el.signedOut.hidden = true;
    el.signedOutNote.textContent = "";
    el.accountBar.hidden = false;
    el.app.hidden = false;

    var allowed = allowedSections(session);
    buildTabs(allowed);
    if (allowed.length === 0) {
      note(el.panels, "No admin sections are available for this account.", true);
      return;
    }
    activate(allowed[0].id);
  }

  /* ---------- tabs ---------- */

  function buildTabs(sections) {
    clear(el.tablist);
    clear(el.panels);
    tabs = [];

    sections.forEach(function (s) {
      var tab = make("button", "tab", s.label);
      tab.type = "button";
      tab.id = "tab-" + s.id;
      tab.setAttribute("role", "tab");
      tab.setAttribute("aria-controls", "panel-" + s.id);
      tab.setAttribute("aria-selected", "false");
      tab.tabIndex = -1;
      tab.addEventListener("click", function () {
        activate(s.id);
      });
      el.tablist.appendChild(tab);

      var panel = make("section", "panel");
      panel.id = "panel-" + s.id;
      panel.setAttribute("role", "tabpanel");
      panel.setAttribute("aria-labelledby", "tab-" + s.id);
      panel.tabIndex = -1;
      panel.hidden = true;

      var head = make("div", "panel-head");
      head.appendChild(make("h2", "", s.label));
      var reload = make("button", "button secondary", "Reload");
      reload.type = "button";
      reload.addEventListener("click", function () {
        load(s.id, true);
      });
      head.appendChild(reload);

      var body = make("div", "content");
      panel.appendChild(head);
      panel.appendChild(body);
      el.panels.appendChild(panel);

      tabs.push({ section: s, tab: tab, panel: panel, body: body, loaded: false, revision: null, items: [] });
    });
  }

  function findTab(id) {
    for (var i = 0; i < tabs.length; i++) {
      if (tabs[i].section.id === id) {
        return tabs[i];
      }
    }
    return null;
  }

  function activate(id) {
    tabs.forEach(function (t) {
      var on = t.section.id === id;
      t.tab.setAttribute("aria-selected", on ? "true" : "false");
      t.tab.tabIndex = on ? 0 : -1;
      t.panel.hidden = !on;
    });
    var current = findTab(id);
    if (current && !current.loaded) {
      load(id, false);
    }
  }

  function onTabKey(ev) {
    var idx = -1;
    for (var i = 0; i < tabs.length; i++) {
      if (tabs[i].tab === ev.target) {
        idx = i;
      }
    }
    if (idx < 0) {
      return;
    }
    var n = tabs.length;
    var next;
    switch (ev.key) {
      case "ArrowRight":
        next = (idx + 1) % n;
        break;
      case "ArrowLeft":
        next = (idx - 1 + n) % n;
        break;
      case "Home":
        next = 0;
        break;
      case "End":
        next = n - 1;
        break;
      default:
        return;
    }
    ev.preventDefault();
    tabs[next].tab.focus();
    activate(tabs[next].section.id);
  }

  /* ---------- loading ---------- */

  function setBusy(container, busy) {
    container.setAttribute("aria-busy", busy ? "true" : "false");
  }

  function load(id, force) {
    var t = findTab(id);
    if (!t) {
      return;
    }
    if (t.loaded && !force) {
      return;
    }
    t.loaded = true;
    if (id === "modules") {
      loadModules(t);
    } else if (id === "users") {
      t.memberKind = "";
      t.memberOffset = 0;
      loadMembers(t);
    } else {
      loadSection(t);
    }
  }

  // Shared rendering for a read response: 401 handled by the caller, 403 and errors shown as text.
  function fillFromResponse(container, res, label) {
    clear(container);
    if (res.status === 403) {
      note(container, TEXT.forbidden, true);
      return;
    }
    if (!res.ok) {
      note(container, label || httpFailure(res), true);
      return;
    }
    renderData(container, res.data);
    renderData(container, unwrapEnvelope(res.data));
  }

  // Read routes reply {"available":true,"data":...} or {"available":false}; hand back the payload.
  function unwrapEnvelope(body) {
    if (isPlainObject(body) && typeof body.available === "boolean") {
      return body.available ? body.data : body;
    }
    return body;
  }

  function loadSection(t) {
    setBusy(t.body, true);
    clear(t.body);
    t.body.appendChild(make("p", "meta", TEXT.loading));
    request("GET", API + t.section.id).then(function (res) {
      if (res.status === 401) {
        expire();
        return;
      }
      setBusy(t.body, false);
      fillFromResponse(t.body, res);
    }).catch(function () {
      setBusy(t.body, false);
      clear(t.body);
      note(t.body, TEXT.network, true);
    });
  }

  function renderData(container, data) {
    if (isPlainObject(data) && data.available === false) {
      note(container, TEXT.unavailable);
      if (typeof data.reason === "string" && data.reason) {
        note(container, data.reason);
      }
      return;
    }
    var wrap = make("div", "data");
    renderValue(wrap, data, 0);
    container.appendChild(wrap);
  }

  /* ---------- generic value rendering (textContent only) ---------- */

  function renderValue(parent, value, depth) {
    if (value === null || value === undefined) {
      parent.appendChild(make("span", "muted", "none"));
      return;
    }
    var kind = typeof value;
    if (kind === "boolean") {
      parent.appendChild(make("span", "", value ? "Yes" : "No"));
      return;
    }
    if (kind === "number" || kind === "string") {
      parent.appendChild(make("span", "value-text", value));
      return;
    }
    if (depth >= MAX_DEPTH) {
      parent.appendChild(make("pre", "", JSON.stringify(value, null, 2)));
      return;
    }
    if (Array.isArray(value)) {
      renderArray(parent, value, depth);
      return;
    }
    renderObject(parent, value, depth);
  }

  function renderArray(parent, value, depth) {
    if (value.length === 0) {
      parent.appendChild(make("p", "muted", "No items."));
      return;
    }
    if (value.every(isPlainObject)) {
      renderTable(parent, value, depth);
      return;
    }
    var ul = make("ul", "list");
    value.forEach(function (item) {
      var li = make("li");
      renderValue(li, item, depth + 1);
      ul.appendChild(li);
    });
    parent.appendChild(ul);
  }

  function renderTable(parent, rows, depth) {
    var keys = [];
    rows.forEach(function (row) {
      Object.keys(row).forEach(function (k) {
        if (keys.length < 16 && keys.indexOf(k) === -1) {
          keys.push(k);
        }
      });
    });

    var wrap = make("div", "table-wrap");
    wrap.setAttribute("role", "region");
    wrap.setAttribute("aria-label", "Data table, scrolls sideways when wide");
    wrap.tabIndex = 0;

    var table = make("table");
    var headRow = make("tr");
    keys.forEach(function (k) {
      var th = make("th", "", k);
      th.scope = "col";
      headRow.appendChild(th);
    });
    var thead = make("thead");
    thead.appendChild(headRow);
    table.appendChild(thead);

    var tbody = make("tbody");
    rows.forEach(function (row) {
      var tr = make("tr");
      keys.forEach(function (k) {
        var td = make("td");
        if (hasOwn(row, k)) {
          renderValue(td, row[k], depth + 1);
        }
        tr.appendChild(td);
      });
      tbody.appendChild(tr);
    });
    table.appendChild(tbody);
    wrap.appendChild(table);
    parent.appendChild(wrap);
  }

  function renderObject(parent, obj, depth) {
    var keys = Object.keys(obj);
    if (keys.length === 0) {
      parent.appendChild(make("p", "muted", "Empty."));
      return;
    }
    var dl = make("dl", "kv");
    keys.forEach(function (k) {
      dl.appendChild(make("dt", "", k));
      var dd = make("dd");
      renderValue(dd, obj[k], depth + 1);
      dl.appendChild(dd);
    });
    parent.appendChild(dl);
  }

  /* ---------- modules ---------- */

  function selectedIds(t) {
    return t.items.filter(function (item) {
      return item.checkbox.checked;
    }).map(function (item) {
      return item.id;
    });
  }

  function makeButton(label, onClick) {
    var b = make("button", "button secondary", label);
    b.type = "button";
    b.addEventListener("click", onClick);
    return b;
  }

  var MEMBER_PAGE = 25;

  // The Agents and members tab: one paged list from /admin/api/members with a kind filter. All text goes in with textContent.
  function loadMembers(t) {
    setBusy(t.body, true);
    clear(t.body);
    t.body.appendChild(make("p", "meta", TEXT.loading));
    var query = "?limit=" + MEMBER_PAGE + "&offset=" + t.memberOffset + (t.memberKind ? "&kind=" + t.memberKind : "");
    request("GET", API + "members" + query).then(function (res) {
      if (res.status === 401) {
        expire();
        return;
      }
      setBusy(t.body, false);
      clear(t.body);
      if (res.status === 403) {
        note(t.body, TEXT.forbidden, true);
        return;
      }
      if (!res.ok) {
        note(t.body, httpFailure(res), true);
        return;
      }
      if (isPlainObject(res.data) && res.data.available === false) {
        note(t.body, TEXT.unavailable);
        return;
      }
      var page = isPlainObject(res.data) ? res.data.data : null;
      if (!isPlainObject(page) || !Array.isArray(page.rows)) {
        note(t.body, "The members list has an unexpected shape.", true);
        return;
      }
      renderMembers(t, page);
    }).catch(function () {
      setBusy(t.body, false);
      clear(t.body);
      note(t.body, TEXT.network, true);
    });
  }

  function renderMembers(t, page) {
    var bar = make("div", "toolbar");
    [["", "Everyone"], ["agent", "Agents"], ["human", "People"]].forEach(function (f) {
      var b = makeButton(f[1], function () {
        t.memberKind = f[0];
        t.memberOffset = 0;
        loadMembers(t);
      });
      if (t.memberKind === f[0]) {
        b.setAttribute("aria-pressed", "true");
      }
      bar.appendChild(b);
    });
    t.body.appendChild(bar);

    var rows = page.rows.map(function (m) {
      return {
        name: m.name,
        kind: m.kind,
        role: m.role,
        status: m.status,
        lastSeen: m.lastSeen || "never",
        model: m.model || "",
        tokens24h: m.tokens24h
      };
    });
    var total = typeof page.total === "number" ? page.total : rows.length;
    var from = rows.length ? t.memberOffset + 1 : 0;
    t.body.appendChild(make("p", "meta", "Showing " + from + " to " + (t.memberOffset + rows.length) + " of " + total));
    if (rows.length) {
      renderTable(t.body, rows, 0);
    } else {
      note(t.body, "No members to show.");
    }

    var nav = make("div", "toolbar");
    var prev = makeButton("Previous", function () {
      t.memberOffset = Math.max(0, t.memberOffset - MEMBER_PAGE);
      loadMembers(t);
    });
    prev.disabled = t.memberOffset === 0;
    var next = makeButton("Next", function () {
      t.memberOffset = page.nextOffset;
      loadMembers(t);
    });
    next.disabled = typeof page.nextOffset !== "number";
    nav.appendChild(prev);
    nav.appendChild(next);
    t.body.appendChild(nav);
  }

  function loadModules(t) {
    setBusy(t.body, true);
    clear(t.body);
    t.body.appendChild(make("p", "meta", TEXT.loading));
    request("GET", API + "modules").then(function (res) {
      if (res.status === 401) {
        expire();
        return;
      }
      setBusy(t.body, false);
      clear(t.body);
      if (res.status === 403) {
        note(t.body, TEXT.forbidden, true);
        return;
      }
      if (!res.ok) {
        note(t.body, httpFailure(res), true);
        return;
      }
      if (isPlainObject(res.data) && res.data.available === false) {
        note(t.body, TEXT.unavailable);
        return;
      }
      if (!isPlainObject(res.data) || !Array.isArray(res.data.modules)) {
        note(t.body, "The module list has an unexpected shape.", true);
        return;
      }
      renderModules(t, res.data);
      var catalogue = unwrapEnvelope(res.data);
      if (isPlainObject(catalogue) && catalogue.available === false) {
        note(t.body, TEXT.unavailable);
        return;
      }
      if (!isPlainObject(catalogue) || !Array.isArray(catalogue.modules)) {
        note(t.body, "The module list has an unexpected shape.", true);
        return;
      }
      renderModules(t, catalogue);
    }).catch(function () {
      setBusy(t.body, false);
      clear(t.body);
      note(t.body, TEXT.network, true);
    });
  }

  function renderModules(t, data) {
    t.revision = data.revision;
    t.items = [];
    clear(t.body);

    t.body.appendChild(make("p", "meta", "Revision " + (data.revision === undefined ? "unknown" : String(data.revision))));

    var fieldset = make("fieldset", "module-list");
    fieldset.appendChild(make("legend", "", "Modules in the company build"));

    data.modules.forEach(function (m, i) {
      var id = m && m.id !== undefined ? String(m.id) : "";
      var row = make("div", "module-row");

      var checkbox = make("input");
      checkbox.type = "checkbox";
      checkbox.id = "module-" + i;
      checkbox.checked = m && m.selected === true;

      var label = make("label");
      label.htmlFor = checkbox.id;
      label.appendChild(make("span", "module-name", m && (m.name || m.id)));
      if (m && m.version !== undefined && m.version !== null) {
        label.appendChild(make("span", "module-version", m.version));
      }
      if (m && m.tag) {
        label.appendChild(make("span", "module-tag", m.tag));
      }

      var deps = m && Array.isArray(m.dependsOn) ? m.dependsOn : [];
      var hint = make("p", "hint", deps.length ? "Depends on: " + deps.join(", ") : "No dependencies");
      hint.id = "module-hint-" + i;
      checkbox.setAttribute("aria-describedby", hint.id);

      row.appendChild(checkbox);
      row.appendChild(label);
      row.appendChild(hint);
      fieldset.appendChild(row);

      t.items.push({ id: id, checkbox: checkbox });
    });
    t.body.appendChild(fieldset);

    var actions = make("div", "actions");
    var previewBtn = makeButton("Preview", function () {
      previewSelection(t);
    });
    var saveBtn = makeButton("Save selection", function () {
      saveSelection(t);
    });
    var rebuildBtn = makeButton("Rebuild", function () {
      rebuild(t, rebuildBtn);
    });
    rebuildBtn.classList.remove("secondary");
    rebuildBtn.classList.add("primary");
    actions.appendChild(previewBtn);
    actions.appendChild(saveBtn);
    actions.appendChild(rebuildBtn);
    t.body.appendChild(actions);

    t.result = make("div", "result");
    t.result.setAttribute("aria-live", "polite");
    t.body.appendChild(t.result);

    t.body.appendChild(make("h3", "builds-head", "Build reports"));
    t.buildsBox = make("div", "builds");
    t.body.appendChild(t.buildsBox);
    refreshBuilds(t);
    renderCatalogue(t);
  }

  /* Gateway model catalogue: "check for newer catalogue" (doc 05). Shows ids and counts only. */
  function renderCatalogue(t) {
    t.body.appendChild(make("h3", "builds-head", "Gateway model catalogue"));
    var box = make("div", "catalogue");
    box.setAttribute("aria-live", "polite");
    var checkBtn = makeButton("Check for newer catalogue", function () {
      checkCatalogue(box, checkBtn);
    });
    t.body.appendChild(checkBtn);
    t.body.appendChild(box);
    request("GET", API + "gateway/catalogue").then(function (res) {
      if (res.status === 401) {
        expire();
        return;
      }
      fillFromResponse(box, res);
    }).catch(function () {
      clear(box);
      note(box, TEXT.network, true);
    });
  }

  function checkCatalogue(box, button) {
    button.disabled = true;
    clear(box);
    box.appendChild(make("p", "meta", TEXT.loading));
    request("POST", API + "gateway/catalogue/refresh", {}).then(function (res) {
      button.disabled = false;
      if (res.status === 401) {
        expire();
        return;
      }
      clear(box);
      if (res.status === 403) {
        note(box, TEXT.forbidden, true);
        return;
      }
      if (!res.ok) {
        note(box, failureMessage(res), true);
        return;
      }
      renderData(box, res.data);
      announce("Catalogue check finished.");
    }).catch(function () {
      button.disabled = false;
      clear(box);
      note(box, TEXT.network, true);
    });
  }

  function showResult(t, heading, data) {
    clear(t.result);
    t.result.appendChild(make("h3", "", heading));
    var wrap = make("div", "data");
    renderValue(wrap, data, 0);
    t.result.appendChild(wrap);
  }

  function failureMessage(res) {
    if (isPlainObject(res.data) && typeof res.data.message === "string" && res.data.message) {
      return res.data.message + " (HTTP " + res.status + ")";
    }
    return "The request failed (HTTP " + res.status + ").";
  }

  function previewSelection(t) {
    clear(t.result);
    t.result.appendChild(make("p", "meta", TEXT.loading));
    request("POST", API + "modules/preview", { selected: selectedIds(t) }).then(function (res) {
      if (res.status === 401) {
        expire();
        return;
      }
      clear(t.result);
      if (res.status === 403) {
        note(t.result, TEXT.forbidden, true);
        return;
      }
      if (!res.ok) {
        note(t.result, failureMessage(res), true);
        return;
      }
      showResult(t, "Preview", res.data);
      announce("Preview ready.");
    }).catch(function () {
      clear(t.result);
      note(t.result, TEXT.network, true);
    });
  }

  function conflictNote(t) {
    clear(t.result);
    note(t.result, TEXT.conflict, true);
    t.result.appendChild(makeButton("Reload modules", function () {
      loadModules(t);
    }));
  }

  function saveSelection(t) {
    clear(t.result);
    t.result.appendChild(make("p", "meta", TEXT.loading));
    request("PUT", API + "modules/selection", {
      selected: selectedIds(t),
      expectedRevision: t.revision
    }).then(function (res) {
      if (res.status === 401) {
        expire();
        return;
      }
      if (res.status === 409) {
        conflictNote(t);
        announce(TEXT.conflict);
        return;
      }
      clear(t.result);
      if (res.status === 403) {
        note(t.result, TEXT.forbidden, true);
        return;
      }
      if (!res.ok) {
        note(t.result, failureMessage(res), true);
        return;
      }
      announce("Module selection saved.");
      loadModules(t);
    }).catch(function () {
      clear(t.result);
      note(t.result, TEXT.network, true);
    });
  }

  function rebuild(t, button) {
    button.disabled = true;
    clear(t.result);
    t.result.appendChild(make("p", "meta", TEXT.loading));
    request("POST", API + "modules/rebuild", {}, { "Idempotency-Key": newKey() }).then(function (res) {
      button.disabled = false;
      if (res.status === 401) {
        expire();
        return;
      }
      clear(t.result);
      if (res.status === 403) {
        note(t.result, TEXT.forbidden, true);
        return;
      }
      if (!res.ok) {
        note(t.result, failureMessage(res), true);
        return;
      }
      showResult(t, "Rebuild", res.data);
      announce("Rebuild requested.");
      refreshBuilds(t);
    }).catch(function () {
      button.disabled = false;
      clear(t.result);
      note(t.result, TEXT.network, true);
    });
  }

  function refreshBuilds(t) {
    clear(t.buildsBox);
    t.buildsBox.appendChild(make("p", "meta", TEXT.loading));
    request("GET", API + "modules/builds").then(function (res) {
      if (res.status === 401) {
        expire();
        return;
      }
      fillFromResponse(t.buildsBox, res);
    }).catch(function () {
      clear(t.buildsBox);
      note(t.buildsBox, TEXT.network, true);
    });
  }

  /* ---------- session and sign-out ---------- */

  function start() {
    request("GET", API + "session").then(function (res) {
      if (res.status === 200 && isPlainObject(res.data)) {
        enterPanel(res.data);
      } else if (res.status === 403) {
        showSignedOut("This account does not have admin access.");
      } else {
        showSignedOut("");
      }
    }).catch(function () {
      showSignedOut(TEXT.network);
    });
  }

  el.tablist.addEventListener("keydown", onTabKey);

  el.signout.addEventListener("click", function () {
    el.signout.disabled = true;
    request("POST", API + "signout", {}).then(function (res) {
      el.signout.disabled = false;
      if (res.ok || res.status === 401) {
        showSignedOut("Signed out.");
        el.signinLink.focus();
        announce("Signed out.");
      } else {
        announce("Sign-out failed. Try again.");
      }
    }).catch(function () {
      el.signout.disabled = false;
      announce("Sign-out failed. Try again.");
    });
  });

  start();
})();
