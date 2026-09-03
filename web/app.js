(function () {
  "use strict";

  var state = {
    status: null,
    folders: {
      optical: null,
      markdown: null,
      vba: null
    },
    outputs: {
      markdown: null,
      vba: null
    },
    folderSignatures: {
      optical: null,
      markdown: null,
      vba: null
    },
    remotes: [],
    remoteId: null,
    remoteHeartbeatTimer: null,
    remoteFolderTimer: null,
    localRemoteTimer: null,
    role: "loading",
    activePage: null,
    opticalView: "send",
    opticalBusy: false,
    opticalSession: null,
    opticalSequence: 0,
    opticalTimer: null,
    qrSizeIndex: 2,
    markdownBusy: false,
    vbaBusy: false,
    vbaInfo: new Map(),
    selections: {
      optical: new Set(),
      markdown: new Set(),
      vba: new Set()
    }
  };

  var toast = document.getElementById("toast");
  var toastTimer = null;

  wireNavigation();
  wireControls();
  registerServiceWorker();
  setPickerDisabled(true);
  setListMessage("opticalFiles", "入力を読み込んでいます", "loading-state");
  setListMessage("markdownFiles", "入力を読み込んでいます", "loading-state");
  setListMessage("vbaFiles", "入力を読み込んでいます", "loading-state");
  loadInitialState();

  async function loadInitialState() {
    try {
      state.status = await requestJson("/api/status");
      applyStatus(state.status);
    } catch (error) {
      applyStatus(null);
      showToast(errorMessage(error));
    }

    if (state.role === "remote") {
      startRemoteConnection();
    } else if (state.role === "local") {
      pollConnectedRemotes();
    }

    var modes = state.role === "remote" ? ["optical"] : ["optical", "markdown", "vba"];
    var results = await Promise.allSettled(modes.map(function (mode) {
      return requestJson("/api/folder?mode=" + mode);
    }));
    modes.forEach(function (mode, index) {
      var result = results[index];
      if (result.status === "fulfilled") {
        applyFolder(mode, result.value);
        return;
      }
      var message = errorMessage(result.reason);
      setListMessage(mode + "Files", message, "empty-state");
      showToast(message);
    });

    var hashPage = window.location.hash.replace(/^#/, "");
    var requestedPage = isPage(hashPage) ? hashPage : state.status && isPage(state.status.initialMode) ? state.status.initialMode : null;
    navigate(requestedPage || "optical", false);
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
      button.addEventListener("click", function () {
        chooseFolder(button.dataset.pickerMode);
      });
    });

    document.querySelectorAll(".choose-files").forEach(function (button) {
      button.addEventListener("click", function () {
        chooseFiles(button.dataset.pickerMode);
      });
    });

    document.getElementById("convertMarkdown").addEventListener("click", convertMarkdown);
    document.getElementById("openMarkdownOutput").addEventListener("click", function () {
      openOutput("markdown", this);
    });
    document.getElementById("extractVba").addEventListener("click", extractVba);
    document.getElementById("openVbaOutput").addEventListener("click", function () {
      openOutput("vba", this);
    });
    document.getElementById("showQr").addEventListener("click", startOptical);
    document.getElementById("readLocalCamera").addEventListener("click", function () {
      setOpticalView("receive");
    });
    document.getElementById("showRemoteQr").addEventListener("click", function () {
      sendRemoteCommand("showQr", this);
    });
    document.getElementById("readRemoteCamera").addEventListener("click", function () {
      sendRemoteCommand("readCamera", this);
    });
    document.getElementById("remoteDeviceButton").addEventListener("click", toggleDeviceMenu);
    document.getElementById("closeQr").addEventListener("click", stopOptical);
    document.getElementById("smallerQr").addEventListener("click", function () {
      changeQrSize(-1);
    });
    document.getElementById("largerQr").addEventListener("click", function () {
      changeQrSize(1);
    });
    document.addEventListener("keydown", function (event) {
      if (event.key === "Escape" && !document.getElementById("qrOverlay").hidden) {
        stopOptical();
      } else if (event.key === "Escape") {
        closeDeviceMenu();
      }
    });
    document.addEventListener("click", function (event) {
      var picker = document.querySelector(".remote-picker");
      if (picker && !picker.contains(event.target)) {
        closeDeviceMenu();
      }
    });
    window.addEventListener("pagehide", disconnectRemote);
    applyQrSize();
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
    document.getElementById("platformTag").textContent = "この PC";
    document.getElementById("platformTag").title = platform;
    document.getElementById("remoteDeviceName").textContent = device;
    document.getElementById("readLocalCamera").textContent = role === "remote"
      ? "この端末のカメラで読む"
      : "この PC のカメラで読む";

    var capabilities = status && status.capabilities ? status.capabilities : {};
    setCapability("statusWord", capabilities.word ? "見つかった" : "なし", Boolean(capabilities.word));
    setCapability("statusOcr", capabilities.windowsOcr ? "対象" : "なし", Boolean(capabilities.windowsOcr));
    setCapability("statusCamera", "未確認", false);
    setPickerDisabled(!status || role !== "local");
    updateMarkdownAction();
    updateVbaAction();
    updateOpticalAction();
    updateOutputAction("markdown");
    updateOutputAction("vba");
  }

  function setCapability(id, text, positive) {
    var element = document.getElementById(id);
    element.textContent = text;
    element.classList.toggle("is-muted", !positive);
  }

  function applyFolder(mode, snapshot) {
    if (mode === "optical") {
      stopOptical();
    }
    var selectedSource = snapshot && snapshot.sourceKind !== "none" ? snapshot : null;
    state.folders[mode] = selectedSource;
    state.folderSignatures[mode] = folderSignature(snapshot);
    var files = selectedSource && Array.isArray(selectedSource.files) ? selectedSource.files : [];
    state.selections[mode] = new Set(files.filter(function (file) {
      return mode === "optical"
        || (mode === "markdown" && file.markdownSupported)
        || (mode === "vba" && file.vbaWorkbook);
    }).map(function (file) { return file.name; }));
    if (mode === "vba") {
      state.vbaInfo.clear();
    }

    document.querySelectorAll("[data-folder-path='" + mode + "']").forEach(function (element) {
      var displayPath = selectedSource && selectedSource.path ? selectedSource.path : "選ばれていません";
      element.textContent = displayPath;
      element.title = selectedSource && selectedSource.path ? selectedSource.path : "";
    });

    if (mode === "markdown") {
      clearMarkdownResult();
      updateMarkdownOutput();
    } else if (mode === "vba") {
      clearVbaResult();
    }
    renderFiles(mode);
  }

  function renderFiles(mode) {
    var folder = state.folders[mode];
    var files = folder && Array.isArray(folder.files) ? folder.files : [];
    if (!folder) {
      var emptyMessage = mode === "vba" ? "ブックを選んでください" : "ファイルを選んでください";
      setListMessage(mode + "Files", emptyMessage, "empty-state");
      updateSummary(mode);
      return;
    }
    if (mode === "optical") {
      renderSelectableFiles("opticalFiles", files, mode);
    } else if (mode === "markdown") {
      renderSelectableFiles("markdownFiles", files.filter(function (file) { return file.markdownSupported; }), mode);
    } else {
      renderVbaFiles(files.filter(function (file) { return file.vbaWorkbook; }));
    }
    updateSummary(mode);
  }

  function renderSelectableFiles(containerId, files, mode) {
    var container = document.getElementById(containerId);
    container.replaceChildren();

    if (files.length === 0) {
      appendMessage(
        container,
        mode === "markdown" ? "Markdown 化できるファイルはありません" : "ファイルはありません",
        "empty-state");
      return;
    }

    var fragment = document.createDocumentFragment();
    files.forEach(function (file) {
      var row = document.createElement("button");
      row.type = "button";
      row.className = "file-row";
      row.dataset.fileName = file.name;
      row.title = file.name;
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

      expand.addEventListener("click", async function () {
        var open = expand.getAttribute("aria-expanded") !== "true";
        expand.setAttribute("aria-expanded", open ? "true" : "false");
        chevron.classList.toggle("is-open", open);
        detail.hidden = !open;
        if (open) {
          await loadVbaBook(file.name, detail);
        }
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
    if (mode === "markdown") {
      updateMarkdownOutput();
      clearMarkdownResult();
    } else if (mode === "vba") {
      clearVbaResult();
    } else if (mode === "optical") {
      updateOpticalAction();
    }
  }

  function updateSummary(mode) {
    var folder = state.folders[mode];
    var panel = document.querySelector("[data-page-panel='" + mode + "']");
    var files = folder && Array.isArray(folder.files) ? folder.files : [];
    var selected = files.filter(function (file) {
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
    if (mode === "markdown") {
      updateMarkdownAction();
    } else if (mode === "vba") {
      updateVbaAction();
    } else if (mode === "optical") {
      updateOpticalAction();
    }
  }

  async function chooseFolder(mode) {
    if (!state.status || state.role === "remote" || !isPage(mode)) {
      return;
    }

    await openNativePicker(
      mode,
      "/api/pick-folder?mode=" + encodeURIComponent(mode),
      { method: "POST" },
      "フォルダを開きました");
  }

  async function chooseFiles(mode) {
    if (!state.status || state.role === "remote") {
      return;
    }

    await openNativePicker(
      mode,
      "/api/pick-files",
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ mode: mode })
      },
      "ファイルを開きました");
  }

  async function openNativePicker(mode, url, options, successMessage) {
    setPickerDisabled(true);
    try {
      var result = await requestJson(url, options);
      if (!result || result.cancelled) {
        return;
      }
      if (!result.folder) {
        throw new Error("選んだものを読み込めませんでした。");
      }
      applyFolder(mode, result.folder);
      showToast(successMessage);
    } catch (error) {
      showToast(errorMessage(error));
    } finally {
      setPickerDisabled(!state.status || state.role !== "local");
    }
  }

  function setPickerDisabled(disabled) {
    document.querySelectorAll(".choose-folder, .choose-files").forEach(function (button) {
      button.disabled = disabled;
    });
  }

  async function convertMarkdown() {
    if (state.markdownBusy || state.role !== "local") {
      return;
    }

    var files = Array.from(state.selections.markdown);
    if (files.length === 0) {
      return;
    }

    var button = document.getElementById("convertMarkdown");
    state.markdownBusy = true;
    clearMarkdownResult();
    updateMarkdownAction();
    button.setAttribute("aria-busy", "true");
    button.textContent = "Markdown 化しています…";

    try {
      var result = await requestJson("/api/markdown", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ files: files, combine: true })
      });

      var resultRow = document.getElementById("markdownResult");
      var resultPath = document.getElementById("markdownResultPath");
      resultPath.textContent = result.outputPath;
      resultPath.title = result.outputPath;
      resultRow.hidden = false;
      document.getElementById("markdownOutput").textContent = result.outputPath;
      state.outputs.markdown = result.outputPath;
      clearModeSelection("markdown");
      updateOutputAction("markdown");

      if (result.failedCount > 0) {
        showToast(result.convertedCount + " 件を Markdown 化しました。" + result.failedCount + " 件は読み取れませんでした。 ");
      } else {
        showToast(result.convertedCount + " 件を Markdown 化しました");
      }
    } catch (error) {
      showToast(errorMessage(error));
    } finally {
      state.markdownBusy = false;
      button.removeAttribute("aria-busy");
      button.textContent = "Markdown にする";
      updateMarkdownAction();
    }
  }

  function updateMarkdownAction() {
    var button = document.getElementById("convertMarkdown");
    button.disabled = state.markdownBusy
      || state.role !== "local"
      || state.selections.markdown.size === 0;
  }

  async function loadVbaBook(fileName, detail) {
    var request = state.vbaInfo.get(fileName);
    if (!request) {
      request = requestJson("/api/vba/inspect", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ file: fileName })
      });
      state.vbaInfo.set(fileName, request);
    }

    detail.classList.add("is-loading");
    detail.textContent = "モジュールを読み込んでいます…";
    try {
      var info = await request;
      renderVbaBook(detail, info);
    } catch (error) {
      state.vbaInfo.delete(fileName);
      detail.textContent = errorMessage(error);
      showToast(errorMessage(error));
    } finally {
      detail.classList.remove("is-loading");
    }
  }

  function renderVbaBook(detail, info) {
    detail.replaceChildren();

    var moduleList = document.createElement("div");
    moduleList.className = "module-list";
    (info.modules || []).forEach(function (module) {
      var row = document.createElement("div");
      row.className = "module-row";
      var name = module.name + (module.extension ? "." + String(module.extension).replace(/^\./, "") : "");
      row.appendChild(textCell(name, "mono module-name"));
      row.appendChild(textCell(module.kind, "module-kind"));
      row.appendChild(textCell(module.lineCount.toLocaleString("ja-JP") + " 行", "module-lines"));
      moduleList.appendChild(row);
    });
    if (!moduleList.childNodes.length) {
      appendMessage(moduleList, "取り出せるモジュールはありません", "module-empty");
    }
    detail.appendChild(moduleList);

    var inventory = info.inventory || {};
    var inventoryList = document.createElement("div");
    inventoryList.className = "inventory-list";
    appendInventory(inventoryList, "参照設定", inventory.references || []);
    appendInventory(inventoryList, "外部接続", inventory.connections || []);
    appendInventory(inventoryList, "バーコードフォント", inventory.barcodeFonts || []);
    appendInventoryCount(inventoryList, "ActiveX", inventory.activeXCount || 0);
    appendInventoryCount(inventoryList, "外部リンク", inventory.externalLinkCount || 0);
    appendInventoryCount(inventoryList, "Power Query", inventory.hasPowerQuery ? 1 : 0);
    appendInventoryCount(inventoryList, "VBA 署名", inventory.hasVbaSignature ? 1 : 0);
    if (info.hasWarnings || info.sourceDoubt || inventory.complete === false) {
      var warning = document.createElement("span");
      warning.className = "inventory-chip is-warning";
      warning.textContent = "読取注意あり";
      inventoryList.appendChild(warning);
    }
    detail.appendChild(inventoryList);
  }

  function appendInventory(container, label, names) {
    appendInventoryCount(container, label, names.length, names.join(" / "));
  }

  function appendInventoryCount(container, label, count, names) {
    var chip = document.createElement("span");
    chip.className = "inventory-chip";
    chip.textContent = label + " " + Number(count).toLocaleString("ja-JP");
    if (names) {
      chip.title = names;
    }
    container.appendChild(chip);
  }

  async function extractVba() {
    if (state.vbaBusy || state.role !== "local") {
      return;
    }
    var files = Array.from(state.selections.vba);
    if (files.length === 0) {
      return;
    }

    var button = document.getElementById("extractVba");
    state.vbaBusy = true;
    clearVbaResult();
    updateVbaAction();
    button.setAttribute("aria-busy", "true");
    button.textContent = "書き出しています…";
    try {
      var result = await requestJson("/api/vba/extract", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ files: files })
      });
      var row = document.getElementById("vbaResult");
      var path = document.getElementById("vbaResultPath");
      path.textContent = result.outputPath;
      path.title = result.outputPath;
      row.hidden = false;
      document.getElementById("vbaOutput").textContent = result.outputPath;
      state.outputs.vba = result.outputPath;
      clearModeSelection("vba");
      updateOutputAction("vba");
      showToast(result.bookCount + " ブック、" + result.moduleCount + " モジュールを書き出しました");
    } catch (error) {
      showToast(errorMessage(error));
    } finally {
      state.vbaBusy = false;
      button.removeAttribute("aria-busy");
      button.textContent = "書き出す";
      updateVbaAction();
    }
  }

  function updateVbaAction() {
    var button = document.getElementById("extractVba");
    button.disabled = state.vbaBusy
      || state.role !== "local"
      || state.selections.vba.size === 0;
  }

  function clearVbaResult() {
    document.getElementById("vbaResult").hidden = true;
    document.getElementById("vbaResultPath").textContent = "";
    var display = state.folders.vba
      ? previewOutputPath(state.folders.vba, "_vba.md")
      : ".\\output\\対象名_YYYYMMDD-HHmm\\_vba.md";
    document.getElementById("vbaOutput").textContent = display;
    document.getElementById("vbaOutput").title = display;
  }

  function clearModeSelection(mode) {
    state.folders[mode] = null;
    state.selections[mode] = new Set();
    if (mode === "vba") {
      state.vbaInfo.clear();
    }
    document.querySelectorAll("[data-folder-path='" + mode + "']").forEach(function (element) {
      element.textContent = "選ばれていません";
      element.title = "";
    });
    renderFiles(mode);
  }

  function updateOutputAction(kind) {
    var id = kind === "markdown" ? "openMarkdownOutput" : "openVbaOutput";
    var button = document.getElementById(id);
    button.disabled = state.role !== "local" || !state.outputs[kind];
  }

  async function openOutput(kind, button) {
    if (state.role !== "local" || button.disabled) {
      return;
    }
    button.disabled = true;
    try {
      await requestJson("/api/open-output", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ kind: kind })
      });
    } catch (error) {
      showToast(errorMessage(error));
    } finally {
      updateOutputAction(kind);
    }
  }

  async function startOptical(command) {
    var fromRemoteCommand = command && command.action === "showQr";
    var files = fromRemoteCommand && Array.isArray(command.files)
      ? command.files
      : Array.from(state.selections.optical);
    if (state.opticalBusy || !state.status || files.length === 0) {
      return;
    }

    setOpticalView("send");
    var button = document.getElementById("showQr");
    state.opticalBusy = true;
    updateOpticalAction();
    button.setAttribute("aria-busy", "true");
    button.textContent = "QR を準備しています…";
    try {
      var result = await requestJson("/api/optical/start", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          files: files,
          format: fromRemoteCommand ? command.format : activeData("transferFormat", "transferFormat", "original"),
          frameBytes: fromRemoteCommand ? command.frameBytes : Number(activeData("frameAmount", "frameBytes", "1465")),
          framesPerSecond: fromRemoteCommand ? command.framesPerSecond : Number(activeData("frameRate", "framesPerSecond", "12"))
        })
      });
      state.opticalSession = result;
      state.opticalSequence = 0;
      state.opticalPreviousFocus = document.activeElement;
      document.getElementById("qrProgress").textContent = "最初の QR を描画しています";
      document.getElementById("qrOverlay").hidden = false;
      document.getElementById("closeQr").focus({ preventScroll: true });
      requestNextQrFrame();
    } catch (error) {
      showToast(errorMessage(error));
    } finally {
      state.opticalBusy = false;
      button.removeAttribute("aria-busy");
      button.textContent = "この画面に QR を出す";
      updateOpticalAction();
    }
  }

  function requestNextQrFrame() {
    var session = state.opticalSession;
    if (!session) {
      return;
    }

    var image = document.getElementById("qrFrame");
    var sequence = state.opticalSequence >>> 0;
    var started = performance.now();
    image.onload = function () {
      if (!state.opticalSession || state.opticalSession.token !== session.token) {
        return;
      }
      var number = sequence + 1;
      document.getElementById("qrProgress").textContent =
        "フレーム " + number.toLocaleString("ja-JP") +
        " ・ 元ブロック " + session.sourceBlocks.toLocaleString("ja-JP") +
        " ・ 最短 " + formatSeconds(session.minimumSeconds);
      state.opticalSequence = sequence === 0xffffffff ? 0 : sequence + 1;
      var interval = 1000 / session.framesPerSecond;
      var delay = Math.max(0, interval - (performance.now() - started));
      state.opticalTimer = window.setTimeout(requestNextQrFrame, delay);
    };
    image.onerror = function () {
      if (state.opticalSession && state.opticalSession.token === session.token) {
        showToast("QR コードの表示を続けられませんでした。");
        stopOptical();
      }
    };
    image.src = "/api/optical/frame?token=" + encodeURIComponent(session.token) + "&seq=" + sequence;
  }

  function stopOptical() {
    window.clearTimeout(state.opticalTimer);
    state.opticalTimer = null;
    var session = state.opticalSession;
    state.opticalSession = null;
    state.opticalSequence = 0;
    var overlay = document.getElementById("qrOverlay");
    if (overlay) {
      overlay.hidden = true;
    }
    if (session) {
      fetch("/api/optical/stop", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ token: session.token })
      }).catch(function () {});
    }
    if (state.opticalPreviousFocus && document.contains(state.opticalPreviousFocus)) {
      state.opticalPreviousFocus.focus({ preventScroll: true });
    }
    state.opticalPreviousFocus = null;
    updateOpticalAction();
  }

  function updateOpticalAction() {
    updateTransferFormats();
    var hasFiles = state.selections.optical.size > 0;
    document.getElementById("showQr").disabled = state.opticalBusy
      || !state.status
      || !hasFiles;
    document.getElementById("readLocalCamera").disabled = !state.status;
    document.getElementById("showRemoteQr").disabled = state.opticalBusy
      || state.role !== "local"
      || state.remotes.length === 0
      || !hasFiles;
    document.getElementById("readRemoteCamera").disabled = state.role !== "local"
      || state.remotes.length === 0;
  }

  function updateTransferFormats() {
    var folder = state.folders.optical;
    var files = folder && Array.isArray(folder.files) ? folder.files : [];
    var selected = files.filter(function (file) {
      return state.selections.optical.has(file.name);
    });
    var markdown = document.querySelector("[data-transfer-format='markdown']");
    var vba = document.querySelector("[data-transfer-format='vba']");

    if (state.role === "local") {
      setTransferFormatAvailability(
        markdown,
        selected.some(function (file) { return file.markdownSupported; }),
        "選んだ中に Markdown 化できるファイルがありません");
      setTransferFormatAvailability(
        vba,
        selected.some(function (file) { return file.vbaWorkbook; }),
        "選んだ中に VBA を取り出せるブックがありません");
    } else {
      markdown.disabled = true;
      vba.disabled = true;
    }

    var active = document.querySelector("#transferFormat button.is-active");
    if (active && active.disabled) {
      document.querySelectorAll("#transferFormat button").forEach(function (button) {
        var original = button.dataset.transferFormat === "original";
        button.classList.toggle("is-active", original);
        button.setAttribute("aria-pressed", original ? "true" : "false");
      });
    }
  }

  function setTransferFormatAvailability(button, available, unavailableTitle) {
    button.disabled = !available;
    if (available) {
      button.removeAttribute("title");
    } else {
      button.title = unavailableTitle;
    }
  }

  async function sendRemoteCommand(action, button) {
    if (state.role !== "local" || button.disabled) {
      return;
    }
    button.disabled = true;
    try {
      var body = { action: action };
      if (action === "showQr") {
        body.files = Array.from(state.selections.optical);
        body.format = activeData("transferFormat", "transferFormat", "original");
        body.frameBytes = Number(activeData("frameAmount", "frameBytes", "1465"));
        body.framesPerSecond = Number(activeData("frameRate", "framesPerSecond", "12"));
      }
      await requestJson("/api/remotes/command", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body)
      });
    } catch (error) {
      showToast(errorMessage(error));
    } finally {
      updateOpticalAction();
    }
  }

  function activeData(groupId, property, fallback) {
    var active = document.querySelector("#" + groupId + " button.is-active");
    return active && active.dataset[property] ? active.dataset[property] : fallback;
  }

  function changeQrSize(direction) {
    var sizes = [3.5, 4.6, 5.8, 7.3, 9.6];
    state.qrSizeIndex = Math.max(0, Math.min(sizes.length - 1, state.qrSizeIndex + direction));
    applyQrSize();
  }

  function applyQrSize() {
    var sizes = [3.5, 4.6, 5.8, 7.3, 9.6];
    var value = sizes[state.qrSizeIndex];
    document.documentElement.style.setProperty("--qr-size", value + "cm");
    document.getElementById("qrSizeSetting").textContent = value.toFixed(1) + " cm";
    document.getElementById("qrSizeOutput").textContent = value.toFixed(1) + " cm";
    document.getElementById("smallerQr").disabled = state.qrSizeIndex === 0;
    document.getElementById("largerQr").disabled = state.qrSizeIndex === sizes.length - 1;
  }

  function formatSeconds(value) {
    var seconds = Math.max(0, Number(value) || 0);
    return seconds < 10 ? seconds.toFixed(1) + " 秒" : Math.ceil(seconds).toLocaleString("ja-JP") + " 秒";
  }

  function updateMarkdownOutput() {
    var folder = state.folders.markdown;
    if (!folder) {
      return;
    }

    var files = folder.files.filter(function (file) {
      return state.selections.markdown.has(file.name);
    });
    var output;
    if (folder.sourceKind === "files" && files.length === 1) {
      var name = files[0].name;
      var dot = name.lastIndexOf(".");
      var base = dot > 0 ? name.substring(0, dot) : name;
      if (files[0].extension === ".md" || files[0].extension === ".markdown") {
        base += ".converted";
      }
      output = base + ".md";
    } else {
      output = folderLeaf(folder.path) + ".md";
    }
    var display = previewOutputPath(folder, output);
    document.getElementById("markdownOutput").textContent = display;
    document.getElementById("markdownOutput").title = display;
  }

  function clearMarkdownResult() {
    document.getElementById("markdownResult").hidden = true;
    document.getElementById("markdownResultPath").textContent = "";
  }

  async function pollConnectedRemotes() {
    window.clearTimeout(state.localRemoteTimer);
    if (state.role !== "local") {
      return;
    }
    try {
      var result = await requestJson("/api/remotes");
      state.remotes = result && Array.isArray(result.remotes) ? result.remotes : [];
      renderConnectedRemotes();
      updateOpticalAction();
    } catch (_) {
      // A later poll will retry without interrupting the current operation.
    } finally {
      if (state.role === "local") {
        state.localRemoteTimer = window.setTimeout(pollConnectedRemotes, 3000);
      }
    }
  }

  function renderConnectedRemotes() {
    var container = document.getElementById("connectedRemotes");
    container.replaceChildren();
    state.remotes.forEach(function (remote) {
      var row = document.createElement("div");
      row.className = "device-row";
      var dot = document.createElement("span");
      dot.className = "device-dot";
      dot.setAttribute("aria-hidden", "true");
      row.appendChild(dot);
      row.appendChild(textCell(remote.name || "リモコン", "device-name"));
      row.appendChild(textCell("リモコン", "device-tag"));
      container.appendChild(row);
    });
  }

  function startRemoteConnection() {
    if (!state.remoteId) {
      state.remoteId = createRemoteId();
    }
    heartbeatRemote();
    pollRemoteFolder();
  }

  async function heartbeatRemote() {
    window.clearTimeout(state.remoteHeartbeatTimer);
    if (state.role !== "remote" || !state.remoteId) {
      return;
    }
    try {
      var result = await requestJson("/api/remotes/heartbeat", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ id: state.remoteId, name: describeRemoteDevice() })
      });
      if (result && result.command) {
        handleRemoteCommand(result.command);
      }
    } catch (_) {
      // Staying on the page is enough; the next heartbeat reconnects it.
    } finally {
      if (state.role === "remote") {
        state.remoteHeartbeatTimer = window.setTimeout(heartbeatRemote, 3500);
      }
    }
  }

  async function pollRemoteFolder() {
    window.clearTimeout(state.remoteFolderTimer);
    if (state.role !== "remote") {
      return;
    }
    try {
      var snapshot = await requestJson("/api/folder?mode=optical");
      if (folderSignature(snapshot) !== state.folderSignatures.optical) {
        applyFolder("optical", snapshot);
      }
    } catch (_) {
      // A transient tailnet pause leaves the last visible selection in place.
    } finally {
      if (state.role === "remote") {
        state.remoteFolderTimer = window.setTimeout(pollRemoteFolder, 2200);
      }
    }
  }

  function handleRemoteCommand(command) {
    if (command.action === "showQr") {
      if (Array.isArray(command.files)) {
        state.selections.optical = new Set(command.files);
        renderFiles("optical");
      }
      startOptical(command);
    } else if (command.action === "readCamera") {
      setOpticalView("receive");
    }
  }

  function disconnectRemote() {
    window.clearTimeout(state.remoteHeartbeatTimer);
    window.clearTimeout(state.remoteFolderTimer);
    if (state.role !== "remote" || !state.remoteId || !navigator.sendBeacon) {
      return;
    }
    var body = new Blob(
      [JSON.stringify({ id: state.remoteId })],
      { type: "application/json" });
    navigator.sendBeacon("/api/remotes/disconnect", body);
  }

  function createRemoteId() {
    if (window.crypto && typeof window.crypto.randomUUID === "function") {
      return window.crypto.randomUUID();
    }
    var values = new Uint32Array(4);
    if (window.crypto && typeof window.crypto.getRandomValues === "function") {
      window.crypto.getRandomValues(values);
      return Array.from(values).map(function (value) {
        return value.toString(16).padStart(8, "0");
      }).join("");
    }
    return String(Date.now()) + "-" + String(Math.random()).slice(2);
  }

  function describeRemoteDevice() {
    var agent = navigator.userAgent || "";
    if (/iPad/i.test(agent) || (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1)) {
      return "iPad";
    }
    if (/iPhone/i.test(agent)) {
      return "iPhone";
    }
    if (/Android/i.test(agent)) {
      return /Mobile/i.test(agent) ? "Android スマホ" : "Android タブレット";
    }
    return "リモコン";
  }

  async function toggleDeviceMenu() {
    var menu = document.getElementById("remoteDeviceMenu");
    if (!menu.hidden) {
      closeDeviceMenu();
      return;
    }
    menu.hidden = false;
    document.getElementById("remoteDeviceButton").setAttribute("aria-expanded", "true");
    setListMessage("remoteDeviceMenu", "PC を探しています…", "remote-device-loading");
    try {
      var result = await requestJson("/api/devices");
      renderDeviceOptions(result && Array.isArray(result.devices) ? result.devices : []);
    } catch (error) {
      setListMessage("remoteDeviceMenu", errorMessage(error), "remote-device-loading");
    }
  }

  function closeDeviceMenu() {
    document.getElementById("remoteDeviceMenu").hidden = true;
    document.getElementById("remoteDeviceButton").setAttribute("aria-expanded", "false");
  }

  function renderDeviceOptions(devices) {
    var menu = document.getElementById("remoteDeviceMenu");
    menu.replaceChildren();
    if (devices.length === 0) {
      appendMessage(menu, "Ferry が動いている PC は見つかりません", "remote-device-loading");
      return;
    }
    devices.forEach(function (device) {
      var button = document.createElement("button");
      button.type = "button";
      button.className = "remote-device-option";
      button.setAttribute("role", "option");
      button.setAttribute("aria-selected", device.current ? "true" : "false");
      button.classList.toggle("is-current", Boolean(device.current));
      var dot = document.createElement("span");
      dot.className = "remote-dot";
      dot.setAttribute("aria-hidden", "true");
      button.appendChild(dot);
      button.appendChild(textCell(device.name || device.host, "remote-device-option-name"));
      button.appendChild(textCell(device.current ? "この PC" : (device.platform || "PC"), "remote-device-option-platform"));
      button.addEventListener("click", function () {
        if (device.current || !device.url) {
          closeDeviceMenu();
          return;
        }
        window.location.assign(device.url + "#optical");
      });
      menu.appendChild(button);
    });
  }

  function folderSignature(snapshot) {
    return JSON.stringify(snapshot || null);
  }

  function registerServiceWorker() {
    if (!("serviceWorker" in navigator) || !window.isSecureContext) {
      return;
    }
    window.addEventListener("load", function () {
      navigator.serviceWorker.register(
        "/service-worker.js?v=__FERRY_BUILD_ID__",
        { scope: "/", updateViaCache: "none" }).catch(function () {});
    });
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

  function previewOutputPath(folder, fileName) {
    var target = folder.sourceKind === "files"
      && Array.isArray(folder.files)
      && folder.files.length === 1
      ? folder.files[0].name
      : folderLeaf(folder.path);
    return ".\\output\\" + target + "_YYYYMMDD-HHmm\\" + fileName;
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
