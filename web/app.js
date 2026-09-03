(function () {
  "use strict";

  var state = {
    status: null,
    folder: null,
    role: "loading",
    activePage: null,
    opticalView: "send",
    browserListing: null,
    selections: {
      optical: new Set(),
      markdown: new Set(),
      vba: new Set()
    }
  };

  var folderDialog = document.getElementById("folderDialog");
  var directoryList = document.getElementById("directoryList");
  var browserPath = document.getElementById("browserPath");
  var folderUp = document.getElementById("folderUp");
  var selectCurrentFolder = document.getElementById("selectCurrentFolder");
  var folderDialogError = document.getElementById("folderDialogError");
  var toast = document.getElementById("toast");
  var toastTimer = null;

  wireNavigation();
  wireControls();
  setListMessage("opticalFiles", "フォルダを読み込んでいます", "loading-state");
  setListMessage("markdownFiles", "フォルダを読み込んでいます", "loading-state");
  setListMessage("vbaFiles", "フォルダを読み込んでいます", "loading-state");
  loadInitialState();

  async function loadInitialState() {
    var results = await Promise.allSettled([
      requestJson("/api/status"),
      requestJson("/api/folder")
    ]);

    if (results[0].status === "fulfilled") {
      state.status = results[0].value;
      applyStatus(state.status);
    } else {
      applyStatus(null);
      showToast(errorMessage(results[0].reason));
    }

    if (results[1].status === "fulfilled") {
      applyFolder(results[1].value);
    } else {
      var message = errorMessage(results[1].reason);
      setListMessage("opticalFiles", message, "empty-state");
      setListMessage("markdownFiles", message, "empty-state");
      setListMessage("vbaFiles", message, "empty-state");
      showToast(message);
    }

    var hashPage = window.location.hash.replace(/^#/, "");
    var requestedPage = isPage(hashPage) ? hashPage : state.status && isPage(state.status.initialMode) ? state.status.initialMode : null;
    navigate(requestedPage || (state.role === "remote" ? "optical" : "vba"), false);
  }

  function wireNavigation() {
    document.querySelectorAll("[data-page]").forEach(function (button) {
      button.addEventListener("click", function () {
        navigate(button.dataset.page, true);
      });
    });

    window.addEventListener("hashchange", function () {
      var page = window.location.hash.replace(/^#/, "");
      if (isPage(page) && page !== state.activePage) {
        navigate(page, false);
      }
    });

    document.querySelectorAll("#opticalDirection button").forEach(function (button) {
      button.addEventListener("click", function () {
        setOpticalView(button.dataset.view);
      });
    });
  }

  function wireControls() {
    document.querySelectorAll(".segmented:not(#opticalDirection)").forEach(function (group) {
      group.querySelectorAll("button").forEach(function (button) {
        button.addEventListener("click", function () {
          group.querySelectorAll("button").forEach(function (candidate) {
            var active = candidate === button;
            candidate.classList.toggle("is-active", active);
            candidate.setAttribute("aria-pressed", active ? "true" : "false");
          });
        });
      });
    });

    document.querySelectorAll(".switch").forEach(function (button) {
      button.addEventListener("click", function () {
        var on = button.getAttribute("aria-checked") !== "true";
        button.setAttribute("aria-checked", on ? "true" : "false");
        button.classList.toggle("is-on", on);
      });
    });

    document.querySelectorAll(".choose-folder").forEach(function (button) {
      button.addEventListener("click", openFolderBrowser);
    });

    document.getElementById("closeFolderDialog").addEventListener("click", closeFolderBrowser);
    document.getElementById("cancelFolderDialog").addEventListener("click", closeFolderBrowser);

    folderUp.addEventListener("click", function () {
      if (!state.browserListing) {
        return;
      }

      var target = state.browserListing.parentPath;
      browseDirectories(target || null);
    });

    selectCurrentFolder.addEventListener("click", selectBrowsedFolder);
  }

  function navigate(page, moveFocus) {
    if (state.role === "remote") {
      page = "optical";
    }

    if (!isPage(page)) {
      return;
    }

    state.activePage = page;
    document.querySelectorAll("[data-page-panel]").forEach(function (panel) {
      panel.hidden = panel.dataset.pagePanel !== page;
    });

    document.querySelectorAll("[data-page]").forEach(function (button) {
      var active = button.dataset.page === page;
      button.classList.toggle("is-active", active);
      if (active) {
        button.setAttribute("aria-current", "page");
      } else {
        button.removeAttribute("aria-current");
      }
    });

    if (window.location.hash !== "#" + page) {
      window.history.replaceState(null, "", "#" + page);
    }

    document.title = page === "optical" ? "光学転送 - Ferry" : page === "markdown" ? "Markdown 化 - Ferry" : "VBA 抽出 - Ferry";
    if (moveFocus) {
      document.getElementById("workspace").focus({ preventScroll: true });
      document.getElementById("workspace").scrollTo({ top: 0, behavior: "smooth" });
    }
  }

  function setOpticalView(view) {
    if (view !== "send" && view !== "receive") {
      return;
    }

    state.opticalView = view;
    document.querySelectorAll("#opticalDirection button").forEach(function (button) {
      var active = button.dataset.view === view;
      button.classList.toggle("is-active", active);
      button.setAttribute("aria-pressed", active ? "true" : "false");
    });
    document.querySelectorAll("[data-optical-view]").forEach(function (panel) {
      panel.hidden = panel.dataset.opticalView !== view;
    });
  }

  function isPage(value) {
    return value === "optical" || value === "markdown" || value === "vba";
  }

  function applyStatus(status) {
    var device = status && status.device ? status.device : "この PC";
    var platform = status && status.platform ? status.platform : "不明";
    var browserHost = window.location.hostname.toLowerCase();
    var fallbackRole = browserHost === "localhost" || browserHost === "127.0.0.1" ? "local" : "remote";
    var role = status && (status.role === "local" || status.role === "remote") ? status.role : fallbackRole;
    state.role = role;
    document.documentElement.classList.toggle("role-remote", role === "remote");
    document.documentElement.classList.toggle("role-local", role === "local");
    document.getElementById("appShell").dataset.role = role;
    document.getElementById("deviceName").textContent = device;
    document.getElementById("platformTag").textContent = platform;
    var remoteDevice = device;
    if (role === "remote" && window.location.hostname.indexOf(".") > 0) {
      remoteDevice = window.location.hostname.split(".")[0];
    }
    document.getElementById("remoteDeviceName").textContent = remoteDevice;

    var capabilities = status && status.capabilities ? status.capabilities : {};
    setCapability("statusWord", capabilities.word ? "見つかった" : "なし", Boolean(capabilities.word));
    setCapability("statusOcr", capabilities.windowsOcr ? "対象" : "なし", Boolean(capabilities.windowsOcr));
    setCapability("statusCamera", "未確認", false);
  }

  function setCapability(id, text, positive) {
    var element = document.getElementById(id);
    element.textContent = text;
    element.classList.toggle("is-muted", !positive);
  }

  function applyFolder(snapshot) {
    state.folder = snapshot;
    var files = Array.isArray(snapshot.files) ? snapshot.files : [];
    state.selections.optical = new Set(files.map(function (file) { return file.name; }));
    state.selections.markdown = new Set(files.filter(function (file) { return file.markdownSupported; }).map(function (file) { return file.name; }));
    state.selections.vba = new Set(files.filter(function (file) { return file.vbaWorkbook; }).map(function (file) { return file.name; }));

    document.querySelectorAll("[data-folder-path]").forEach(function (element) {
      element.textContent = snapshot.path;
      element.title = snapshot.path;
    });

    var outputName = folderLeaf(snapshot.path) + ".md";
    document.getElementById("markdownOutput").textContent = outputName;
    renderAllFiles();
  }

  function renderAllFiles() {
    var files = state.folder && Array.isArray(state.folder.files) ? state.folder.files : [];
    renderSelectableFiles("opticalFiles", files, "optical");
    renderSelectableFiles("markdownFiles", files, "markdown");
    renderVbaFiles(files.filter(function (file) { return file.vbaWorkbook; }));
    updateSummary("optical");
    updateSummary("markdown");
    updateSummary("vba");
  }

  function renderSelectableFiles(containerId, files, mode) {
    var container = document.getElementById(containerId);
    container.replaceChildren();

    if (files.length === 0) {
      appendMessage(container, "このフォルダにファイルはありません", "empty-state");
      return;
    }

    var fragment = document.createDocumentFragment();
    files.forEach(function (file) {
      var supported = mode !== "markdown" || file.markdownSupported;
      var row = document.createElement("button");
      row.type = "button";
      row.className = "file-row";
      row.dataset.fileName = file.name;
      row.title = supported ? file.name : file.name + "（Markdown 化の対象外）";
      row.disabled = !supported;
      row.setAttribute("aria-pressed", state.selections[mode].has(file.name) ? "true" : "false");
      row.classList.toggle("is-selected", state.selections[mode].has(file.name));
      row.appendChild(fileNameCell(file));
      row.appendChild(textCell(formatBytes(file.size), "file-size"));
      row.appendChild(textCell(formatDate(file.modifiedUtc), "file-date"));
      row.addEventListener("click", function () {
        toggleSelection(mode, file.name, row);
      });
      fragment.appendChild(row);
    });
    container.appendChild(fragment);
  }

  function renderVbaFiles(files) {
    var container = document.getElementById("vbaFiles");
    container.replaceChildren();

    if (files.length === 0) {
      appendMessage(container, "このフォルダにマクロブックはありません", "empty-state");
      return;
    }

    var fragment = document.createDocumentFragment();
    files.forEach(function (file) {
      var group = document.createElement("div");
      group.className = "book-group";

      var row = document.createElement("div");
      row.className = "file-row book-row";
      row.classList.toggle("is-selected", state.selections.vba.has(file.name));

      var nameCell = document.createElement("div");
      nameCell.className = "file-name";

      var select = document.createElement("button");
      select.type = "button";
      select.className = "book-select";
      select.setAttribute("role", "checkbox");
      select.setAttribute("aria-checked", state.selections.vba.has(file.name) ? "true" : "false");
      select.setAttribute("aria-label", file.name + " を選ぶ");
      var check = document.createElement("span");
      check.className = "book-check";
      check.textContent = "✓";
      select.appendChild(check);

      var expand = document.createElement("button");
      expand.type = "button";
      expand.className = "book-expand";
      expand.setAttribute("aria-expanded", "false");
      expand.title = file.name;
      var chevron = svgUse("icon-chevron", "book-chevron");
      expand.appendChild(chevron);
      expand.appendChild(fileBadge(file));
      expand.appendChild(textCell(file.name, "file-name-text"));

      nameCell.appendChild(select);
      nameCell.appendChild(expand);
      row.appendChild(nameCell);
      row.appendChild(textCell(formatBytes(file.size), "file-size"));
      row.appendChild(textCell(formatDate(file.modifiedUtc), "file-date"));

      var detail = document.createElement("div");
      detail.className = "book-detail";
      detail.hidden = true;
      detail.textContent = "モジュール情報は未読み込みです";

      select.addEventListener("click", function () {
        var selected = state.selections.vba.has(file.name);
        if (selected) {
          state.selections.vba.delete(file.name);
        } else {
          state.selections.vba.add(file.name);
        }
        row.classList.toggle("is-selected", !selected);
        select.setAttribute("aria-checked", !selected ? "true" : "false");
        updateSummary("vba");
      });

      expand.addEventListener("click", function () {
        var open = expand.getAttribute("aria-expanded") !== "true";
        expand.setAttribute("aria-expanded", open ? "true" : "false");
        chevron.classList.toggle("is-open", open);
        detail.hidden = !open;
      });

      group.appendChild(row);
      group.appendChild(detail);
      fragment.appendChild(group);
    });
    container.appendChild(fragment);
  }

  function fileNameCell(file) {
    var cell = document.createElement("span");
    cell.className = "file-name";
    cell.appendChild(fileBadge(file));
    cell.appendChild(textCell(file.name, "file-name-text"));
    return cell;
  }

  function fileBadge(file) {
    var badge = document.createElement("span");
    badge.className = "file-badge kind-" + String(file.kind || "other").toLowerCase();
    badge.textContent = file.badge || "FILE";
    badge.setAttribute("aria-hidden", "true");
    return badge;
  }

  function textCell(text, className) {
    var cell = document.createElement("span");
    cell.className = className;
    cell.textContent = text;
    return cell;
  }

  function svgUse(symbol, className) {
    var svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("class", className);
    svg.setAttribute("aria-hidden", "true");
    var use = document.createElementNS("http://www.w3.org/2000/svg", "use");
    use.setAttribute("href", "#" + symbol);
    svg.appendChild(use);
    return svg;
  }

  function toggleSelection(mode, fileName, row) {
    var selected = state.selections[mode].has(fileName);
    if (selected) {
      state.selections[mode].delete(fileName);
    } else {
      state.selections[mode].add(fileName);
    }
    row.classList.toggle("is-selected", !selected);
    row.setAttribute("aria-pressed", !selected ? "true" : "false");
    updateSummary(mode);
  }

  function updateSummary(mode) {
    if (!state.folder) {
      return;
    }

    var panel = document.querySelector("[data-page-panel='" + mode + "']");
    var selected = state.folder.files.filter(function (file) {
      return state.selections[mode].has(file.name);
    });
    panel.querySelector("[data-summary-count]").textContent = String(selected.length);
    panel.querySelector("[data-summary-size]").textContent = formatBytes(selected.reduce(function (total, file) {
      return total + file.size;
    }, 0));

    var typeContainer = panel.querySelector("[data-summary-types]");
    typeContainer.replaceChildren();
    var order = ["PDF", "Word", "Excel", "PowerPoint", "Text", "Other"];
    order.forEach(function (kind) {
      var count = selected.filter(function (file) { return file.kind === kind; }).length;
      if (count === 0) {
        return;
      }
      var pill = document.createElement("i");
      pill.className = "summary-pill";
      pill.textContent = kind + " " + count;
      typeContainer.appendChild(pill);
    });
  }

  async function openFolderBrowser() {
    if (state.role === "remote") {
      return;
    }

    clearDialogError();
    if (!folderDialog.open) {
      folderDialog.showModal();
    }
    await browseDirectories(state.folder ? state.folder.path : null);
  }

  function closeFolderBrowser() {
    if (folderDialog.open) {
      folderDialog.close();
    }
  }

  async function browseDirectories(path) {
    browserPath.textContent = "読み込み中";
    directoryList.setAttribute("aria-busy", "true");
    directoryList.replaceChildren();
    appendMessage(directoryList, "フォルダを読み込んでいます", "loading-state");
    selectCurrentFolder.disabled = true;
    folderUp.disabled = true;
    clearDialogError();

    try {
      var url = "/api/directories";
      if (path) {
        url += "?path=" + encodeURIComponent(path);
      }
      var listing = await requestJson(url);
      state.browserListing = listing;
      renderDirectories(listing);
    } catch (error) {
      state.browserListing = null;
      directoryList.replaceChildren();
      appendMessage(directoryList, "この場所を表示できません", "empty-state");
      showDialogError(errorMessage(error));
    } finally {
      directoryList.removeAttribute("aria-busy");
    }
  }

  function renderDirectories(listing) {
    browserPath.textContent = listing.path || "ドライブ";
    browserPath.title = listing.path || "ドライブ";
    selectCurrentFolder.disabled = !listing.path;
    folderUp.disabled = !canGoUp(listing);
    directoryList.replaceChildren();

    if (!listing.directories || listing.directories.length === 0) {
      appendMessage(directoryList, "中にフォルダはありません", "empty-state");
      return;
    }

    var fragment = document.createDocumentFragment();
    listing.directories.forEach(function (directory) {
      var row = document.createElement("button");
      row.type = "button";
      row.className = "directory-row";
      row.title = directory.path;
      row.appendChild(svgUse("icon-folder", "folder-glyph"));
      row.appendChild(textCell(directory.name, "file-name-text"));
      row.appendChild(svgUse("icon-chevron", "chevron"));
      row.addEventListener("click", function () {
        browseDirectories(directory.path);
      });
      fragment.appendChild(row);
    });
    directoryList.appendChild(fragment);
  }

  function canGoUp(listing) {
    if (!listing.path) {
      return false;
    }
    if (listing.parentPath) {
      return true;
    }
    return /^[a-zA-Z]:[\\/]*$/.test(listing.path);
  }

  async function selectBrowsedFolder() {
    if (!state.browserListing || !state.browserListing.path) {
      return;
    }

    selectCurrentFolder.disabled = true;
    clearDialogError();
    try {
      var snapshot = await requestJson("/api/folder", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ path: state.browserListing.path })
      });
      applyFolder(snapshot);
      closeFolderBrowser();
      showToast("フォルダを開きました");
    } catch (error) {
      showDialogError(errorMessage(error));
      selectCurrentFolder.disabled = false;
    }
  }

  async function requestJson(url, options) {
    var response = await fetch(url, options || {});
    var body;
    try {
      body = await response.json();
    } catch (_) {
      body = null;
    }
    if (!response.ok) {
      throw new Error(body && body.error ? body.error : "Ferry に接続できませんでした。");
    }
    return body;
  }

  function setListMessage(id, message, className) {
    var container = document.getElementById(id);
    container.replaceChildren();
    appendMessage(container, message, className);
  }

  function appendMessage(container, message, className) {
    var element = document.createElement("div");
    element.className = className;
    element.textContent = message;
    container.appendChild(element);
  }

  function clearDialogError() {
    folderDialogError.hidden = true;
    folderDialogError.textContent = "";
  }

  function showDialogError(message) {
    folderDialogError.textContent = message;
    folderDialogError.hidden = false;
  }

  function showToast(message) {
    window.clearTimeout(toastTimer);
    toast.textContent = message;
    toast.hidden = false;
    toastTimer = window.setTimeout(function () {
      toast.hidden = true;
    }, 4200);
  }

  function errorMessage(error) {
    return error && error.message ? error.message : "Ferry の処理中にエラーが起きました。";
  }

  function folderLeaf(path) {
    var parts = String(path || "output").split(/[\\/]/).filter(Boolean);
    return parts.length ? parts[parts.length - 1] : "output";
  }

  function formatBytes(value) {
    var bytes = Number(value) || 0;
    if (bytes < 1024) {
      return bytes + " B";
    }
    var units = ["KB", "MB", "GB", "TB"];
    var size = bytes;
    var unit = -1;
    do {
      size /= 1024;
      unit++;
    } while (size >= 1024 && unit < units.length - 1);
    var digits = size >= 100 ? 0 : size >= 10 ? 1 : 1;
    return size.toLocaleString("ja-JP", { maximumFractionDigits: digits }) + " " + units[unit];
  }

  function formatDate(value) {
    var date = new Date(value);
    if (Number.isNaN(date.getTime())) {
      return "";
    }
    return pad(date.getMonth() + 1) + "/" + pad(date.getDate()) + " " + pad(date.getHours()) + ":" + pad(date.getMinutes());
  }

  function pad(value) {
    return String(value).padStart(2, "0");
  }
})();
