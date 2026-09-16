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
    opticalStartGeneration: 0,
    screenWakeLock: null,
    wakeLockPending: false,
    opticalSession: null,
    opticalRenderer: null,
    qrDisplaySize: 900,
    cameraStream: null,
    cameraWorkers: [],
    cameraGeneration: 0,
    cameraStarting: false,
    cameraRunning: false,
    cameraComplete: false,
    cameraHasDecoded: false,
    cameraUpload: null,
    cameraTracking: null,
    cameraLastResultId: -1,
    cameraWorkerReason: null,
    cameraLastCaptureAt: 0,
    cameraLastDecodedAt: 0,
    cameraFrameId: 0,
    cameraQuietTimer: null,
    cameraReceiverId: null,
    markdownBusy: false,
    vbaBusy: false,
    themePreference: null,
    vbaInfo: new Map(),
    selections: {
      optical: new Set(),
      markdown: new Set(),
      vba: new Set()
    }
  };

  var opticalCore = window.FerryOptical;
  var toast = document.getElementById("toast");
  var toastTimer = null;
  var cameraCanvas = document.createElement("canvas");
  var darkModeQuery = typeof window.matchMedia === "function"
    ? window.matchMedia("(prefers-color-scheme: dark)")
    : null;
  var infoTooltip = document.getElementById("infoTooltip");
  var infoTooltipTarget = null;

  loadThemePreference();
  wireNavigation();
  wireControls();
  wireInfoTips();
  registerServiceWorker();
  watchCameraChanges();
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

    if (state.role === "local") {
      pollConnectedRemotes();
      loadRemoteEntry();
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
    // Do not accept remote commands before initial folder loading can overwrite them.
    if (state.role === "remote") startRemoteConnection();
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

    document.querySelectorAll("[data-selection-action]").forEach(function (button) {
      button.addEventListener("click", function () {
        setAllSelections(
          button.dataset.selectionMode,
          button.dataset.selectionAction === "all");
      });
    });

    document.querySelectorAll("[data-theme-toggle]").forEach(function (button) {
      button.addEventListener("click", toggleTheme);
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
    document.getElementById("readLocalCamera").addEventListener("click", toggleCamera);
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
    document.getElementById("qrDisplaySize").addEventListener("input", function () {
      setQrDisplaySize(Number(this.value));
    });
    ["cameraWidth", "cameraFps", "cameraWorkers"].forEach(function (id) {
      document.getElementById(id).addEventListener("change", function () {
        applyReceiveSettings();
      });
    });
    document.getElementById("cameraNoSignalHelp").addEventListener("click", function () {
      document.getElementById("cameraHelpDialog").showModal();
    });
    document.getElementById("cameraNoSignalDismiss").addEventListener("click", dismissCameraQuietHint);
    document.getElementById("cameraHelpClose").addEventListener("click", function () {
      document.getElementById("cameraHelpDialog").close();
    });
    document.getElementById("cameraHelpDialog").addEventListener("close", dismissCameraQuietHint);
    document.getElementById("cameraHelpDialog").addEventListener("click", function (event) {
      if (event.target === this) {
        this.close();
      }
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
    window.addEventListener("pagehide", shutdownCamera);
    window.addEventListener("pagehide", function () { stopOptical(true); });
    document.addEventListener("visibilitychange", function () {
      if (document.visibilityState === "visible" && (state.opticalSession || state.cameraRunning)) requestScreenWakeLock();
    });
    document.getElementById("errorCorrection").addEventListener("change", updateFrameAmountOptions);
    window.addEventListener("resize", function () {
      if (state.opticalRenderer) {
        sizeQrCanvas(state.opticalRenderer);
      }
    });
    applyQrSize();
  }

  function loadThemePreference() {
    var preference = null;
    try {
      var stored = window.localStorage.getItem("ferry-theme");
      if (stored === "light" || stored === "dark") {
        preference = stored;
      }
    } catch (_) {
      // The toggle still works for this session when storage is unavailable.
    }
    applyThemePreference(preference, false);
    if (darkModeQuery) {
      var updateFromSystem = function () {
        if (!state.themePreference) {
          updateThemeToggle();
        }
      };
      if (typeof darkModeQuery.addEventListener === "function") {
        darkModeQuery.addEventListener("change", updateFromSystem);
      } else if (typeof darkModeQuery.addListener === "function") {
        darkModeQuery.addListener(updateFromSystem);
      }
    }
  }

  function toggleTheme() {
    applyThemePreference(isDarkThemeActive() ? "light" : "dark", true);
  }

  function applyThemePreference(preference, persist) {
    state.themePreference = preference;
    if (preference) {
      document.documentElement.dataset.theme = preference;
    } else {
      document.documentElement.removeAttribute("data-theme");
    }
    if (persist) {
      try {
        window.localStorage.setItem("ferry-theme", preference);
      } catch (_) {
        // Keep the selected theme for this session when storage is unavailable.
      }
    }
    updateThemeToggle();
  }

  function isDarkThemeActive() {
    if (state.themePreference) {
      return state.themePreference === "dark";
    }
    return Boolean(darkModeQuery && darkModeQuery.matches);
  }

  function updateThemeToggle() {
    var dark = isDarkThemeActive();
    document.querySelectorAll("[data-theme-toggle]").forEach(function (button) {
      button.setAttribute("aria-pressed", dark ? "true" : "false");
      button.setAttribute("aria-label", dark ? "ライトにする" : "ダークにする");
    });
  }

  function wireInfoTips() {
    document.querySelectorAll("[data-info-tip]").forEach(function (target) {
      target.addEventListener("pointerenter", function () {
        showInfoTip(target);
      });
      target.addEventListener("pointerleave", function () {
        hideInfoTip(target, false);
      });
      target.addEventListener("focus", function () {
        showInfoTip(target);
      });
      target.addEventListener("blur", function () {
        hideInfoTip(target, false);
      });
      target.addEventListener("click", function () {
        showInfoTip(target);
      });
      target.addEventListener("keydown", function (event) {
        if (event.key === "Escape") {
          hideInfoTip(target, true);
          target.blur();
        }
      });
    });
    document.addEventListener("pointerdown", function (event) {
      if (infoTooltipTarget && !infoTooltipTarget.contains(event.target)) {
        hideInfoTip(infoTooltipTarget, true);
      }
    });
    document.addEventListener("scroll", function () {
      if (infoTooltipTarget) {
        hideInfoTip(infoTooltipTarget, true);
      }
    }, true);
    window.addEventListener("resize", function () {
      if (infoTooltipTarget) {
        hideInfoTip(infoTooltipTarget, true);
      }
    });
  }

  function showInfoTip(target) {
    var text = target.dataset.infoTip;
    if (!text) {
      return;
    }
    infoTooltipTarget = target;
    infoTooltip.textContent = text;
    infoTooltip.hidden = false;
    infoTooltip.style.left = "0px";
    infoTooltip.style.top = "0px";

    var margin = 10;
    var gap = 8;
    var targetBounds = target.getBoundingClientRect();
    var tipBounds = infoTooltip.getBoundingClientRect();
    var left = targetBounds.left + targetBounds.width / 2 - tipBounds.width / 2;
    left = Math.max(margin, Math.min(window.innerWidth - tipBounds.width - margin, left));
    var top = targetBounds.top - tipBounds.height - gap;
    if (top < margin) {
      top = targetBounds.bottom + gap;
    }
    top = Math.max(margin, Math.min(window.innerHeight - tipBounds.height - margin, top));
    infoTooltip.style.left = Math.round(left) + "px";
    infoTooltip.style.top = Math.round(top) + "px";
  }

  function hideInfoTip(target, force) {
    if (infoTooltipTarget !== target) {
      return;
    }
    if (!force && (document.activeElement === target || target.matches(":hover"))) {
      return;
    }
    infoTooltipTarget = null;
    infoTooltip.hidden = true;
  }

  function navigate(page, moveFocus) {
    if (state.role === "remote") {
      page = "optical";
    }

    if (!isPage(page)) {
      return;
    }

    if (page !== "optical" && (state.cameraRunning || state.cameraStarting)) {
      stopCamera(true, false);
      resetReceivePanel();
    }

    if (page !== "optical" && (state.opticalSession || state.opticalBusy)) stopOptical();
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

    if (view === "send" && (state.cameraRunning || state.cameraStarting)) {
      stopCamera(true, false);
      resetReceivePanel();
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
    var browserHost = window.location.hostname.toLowerCase();
    var fallbackRole = browserHost === "localhost" || browserHost === "127.0.0.1" ? "local" : "remote";
    var role = status && (status.role === "local" || status.role === "remote") ? status.role : fallbackRole;
    state.role = role;
    document.documentElement.classList.toggle("role-remote", role === "remote");
    document.documentElement.classList.toggle("role-local", role === "local");
    document.getElementById("appShell").dataset.role = role;
    document.getElementById("deviceName").textContent = device;
    document.getElementById("remoteDeviceName").textContent = device;
    document.getElementById("readLocalCamera").textContent = role === "remote"
      ? "この端末のカメラで読む"
      : "この PC のカメラで読む";

    var capabilities = status && status.capabilities ? status.capabilities : {};
    setCapability("statusWord", capabilities.word ? "見つかった" : "なし", Boolean(capabilities.word));
    setCapability("statusOcr", capabilities.windowsOcr ? "対象" : "なし", Boolean(capabilities.windowsOcr));
    refreshCameraStatus();
    setPickerDisabled(!status || role !== "local");
    updateMarkdownAction();
    updateVbaAction();
    updateOpticalAction();
    updateOutputAction("markdown");
    updateOutputAction("vba");
  }

  // カメラは抜き差しされる。API の有無だけでなく実際の台数を数え、
  // devicechange で数え直す。再起動しなくても付けた瞬間に表に出る。
  function refreshCameraStatus() {
    var api = Boolean(navigator.mediaDevices && navigator.mediaDevices.getUserMedia);
    if (!api) {
      setCapability("statusCamera", "利用不可", false);
      return;
    }
    if (!navigator.mediaDevices.enumerateDevices) {
      setCapability("statusCamera", "利用可能", true);
      return;
    }
    navigator.mediaDevices.enumerateDevices().then(function (devices) {
      var count = 0;
      for (var i = 0; i < devices.length; i++) {
        if (devices[i].kind === "videoinput") { count++; }
      }
      // 権限が無いと label は空になるが、台数は数えられる。
      setCapability("statusCamera", count > 0 ? (count + " 台") : "なし", count > 0);
      updateOpticalAction();
    }).catch(function () {
      setCapability("statusCamera", "利用可能", true);
    });
  }

  function watchCameraChanges() {
    if (!navigator.mediaDevices || !navigator.mediaDevices.addEventListener) { return; }
    navigator.mediaDevices.addEventListener("devicechange", function () {
      refreshCameraStatus();
    });
  }

  function setCapability(id, text, positive) {
    var element = document.getElementById(id);
    element.textContent = text;
    element.classList.toggle("is-muted", !positive);
  }

  function applyFolder(mode, snapshot, preserveTransfer) {
    if (mode === "optical" && !preserveTransfer) {
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
    var files = selectableFiles(mode);
    if (!folder) {
      var emptyMessage = mode === "vba" ? "ブックを選んでください" : "ファイルを選んでください";
      setListMessage(mode + "Files", emptyMessage, "empty-state");
      updateSelectionActions(mode, files);
      updateSummary(mode);
      return;
    }
    if (mode === "optical") {
      renderSelectableFiles("opticalFiles", files, mode);
    } else if (mode === "markdown") {
      renderSelectableFiles("markdownFiles", files, mode);
    } else {
      renderVbaFiles(files);
    }
    updateSelectionActions(mode, files);
    updateSummary(mode);
  }

  function selectableFiles(mode) {
    var folder = state.folders[mode];
    var files = folder && Array.isArray(folder.files) ? folder.files : [];
    if (mode === "markdown") {
      return files.filter(function (file) { return file.markdownSupported; });
    }
    if (mode === "vba") {
      return files.filter(function (file) { return file.vbaWorkbook; });
    }
    return files;
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
      row.setAttribute("role", "checkbox");
      row.setAttribute("aria-checked", state.selections[mode].has(file.name) ? "true" : "false");
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
    var check = document.createElement("span");
    check.className = "book-check";
    check.textContent = "✓";
    check.setAttribute("aria-hidden", "true");
    cell.appendChild(check);
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
    row.setAttribute("aria-checked", !selected ? "true" : "false");
    updateSelectionActions(mode, selectableFiles(mode));
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

  function setAllSelections(mode, select) {
    if (mode !== "optical" && mode !== "markdown" && mode !== "vba") {
      return;
    }
    selectableFiles(mode).forEach(function (file) {
      if (select) {
        state.selections[mode].add(file.name);
      } else {
        state.selections[mode].delete(file.name);
      }
    });
    renderFiles(mode);
    if (mode === "markdown") {
      updateMarkdownOutput();
      clearMarkdownResult();
    } else if (mode === "vba") {
      clearVbaResult();
    }
  }

  function updateSelectionActions(mode, files) {
    var allButton = document.querySelector(
      "[data-selection-mode='" + mode + "'][data-selection-action='all']");
    var noneButton = document.querySelector(
      "[data-selection-mode='" + mode + "'][data-selection-action='none']");
    if (!allButton || !noneButton) {
      return;
    }
    var selectedCount = files.filter(function (file) {
      return state.selections[mode].has(file.name);
    }).length;
    allButton.disabled = files.length === 0 || selectedCount === files.length;
    noneButton.disabled = selectedCount === 0;
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
    if (files.length === 0 && !markdownOmissionsOnly()) {
      return;
    }

    var button = document.getElementById("convertMarkdown");
    state.markdownBusy = true;
    updateMarkdownAction();

    try {
      var excludeLargeFiles = await chooseLargeMarkdownFiles();
      if (excludeLargeFiles === null) { return; }
      clearMarkdownResult();
      button.setAttribute("aria-busy", "true");
      button.textContent = "Markdown 化しています…";
      var result = await requestJson("/api/markdown", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ files: files, combine: true, excludeLargeFiles: excludeLargeFiles })
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

      if (result.convertedCount === 0 && result.failedCount === 0) {
        showToast("対象外の一覧を書き出しました");
      } else if (result.failedCount > 0) {
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

  function chooseLargeMarkdownFiles() {
    var folder = state.folders.markdown;
    var large = folder.files.filter(function (file) {
      return file.markdownSupported && state.selections.markdown.has(file.name)
        && file.size > state.status.markdownLargeFileBytes;
    });
    if (large.length === 0) { return Promise.resolve(false); }
    var dialog = document.getElementById("markdownLargeDialog");
    var list = document.getElementById("markdownLargeFiles");
    list.replaceChildren();
    var total = 0;
    large.forEach(function (file) {
      var row = document.createElement("li");
      row.appendChild(textCell(file.name, "large-file-name"));
      row.appendChild(textCell(formatBytes(file.size), "large-file-size mono"));
      list.appendChild(row);
      total += file.size;
    });
    document.getElementById("markdownLargeTotal").textContent =
      "1 MB を超えるファイルが " + large.length + " 件、合計 " + formatBytes(total) + " あります。";
    dialog.returnValue = "";
    return new Promise(function (resolve) {
      dialog.addEventListener("close", function () {
        resolve(dialog.returnValue === "exclude" ? true : dialog.returnValue === "include" ? false : null);
      }, { once: true });
      dialog.showModal();
    });
  }

  function updateMarkdownAction() {
    var button = document.getElementById("convertMarkdown");
    var omissionsOnly = markdownOmissionsOnly();
    button.disabled = state.markdownBusy
      || state.role !== "local"
      || (state.selections.markdown.size === 0 && !omissionsOnly);
    if (!state.markdownBusy) {
      button.textContent = omissionsOnly ? "対象外の一覧を書き出す" : "Markdown にする";
    }
  }

  function markdownOmissionsOnly() {
    var folder = state.folders.markdown;
    return folder && Array.isArray(folder.files) && folder.files.length > 0
      && folder.files.every(function (file) { return !file.markdownSupported; });
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

    if (state.opticalSession) stopOptical();
    var startGeneration = ++state.opticalStartGeneration;
    setOpticalView("send");
    var button = document.getElementById("showQr");
    state.opticalBusy = true;
    updateOpticalAction();
    button.setAttribute("aria-busy", "true");
    button.textContent = "QR を準備しています…";
    try {
      var requestedFrameBytes = fromRemoteCommand
        ? Number(command.frameBytes)
        : Number(document.getElementById("frameAmount").value || "1000");
      var framesPerSecond = fromRemoteCommand
        ? Number(command.framesPerSecond)
        : Number(document.getElementById("frameRate").value || "30");
      var errorCorrection = fromRemoteCommand
        ? String(command.errorCorrection || "L")
        : String(document.getElementById("errorCorrection").value || "L");
      var displaySize = fromRemoteCommand
        ? Number(command.displaySize || 900)
        : Number(document.getElementById("qrDisplaySize").value || "900");
      setQrDisplaySize(displaySize);
      var result = await requestJson("/api/optical/start", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          files: files,
          format: fromRemoteCommand ? command.format : activeData("transferFormat", "transferFormat", "original"),
          frameBytes: requestedFrameBytes,
          framesPerSecond: framesPerSecond,
          errorCorrection: errorCorrection
        })
      });
      if (startGeneration !== state.opticalStartGeneration) {
        fetch("/api/optical/stop", {
          method: "POST", headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ token: result.token })
        }).catch(function () {});
        return;
      }
      // Keep the selected budget after a tiny transfer; do not silently force
      // the next, larger transfer to use the tiny transfer's actual frame size.
      setActualFrameAmount(Math.max(result.frameBytes, Math.min(requestedFrameBytes,
        opticalCore.maximumFrameBytes(result.errorCorrection || errorCorrection))));
      document.getElementById("frameRate").value = String(result.framesPerSecond);
      document.getElementById("errorCorrection").value = String(result.errorCorrection || errorCorrection);
      updateFrameAmountOptions();
      if (!confirmLargeOptical(result)) {
        await requestJson("/api/optical/stop", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ token: result.token })
        });
        return;
      }
      state.opticalSession = result;
      requestScreenWakeLock();
      state.opticalPreviousFocus = document.activeElement;
      document.getElementById("qrProgress").textContent = "最初の QR を描画しています";
      document.getElementById("qrOverlay").hidden = false;
      document.getElementById("closeQr").focus({ preventScroll: true });
      startQrRenderer(result);
    } catch (error) {
      showToast(errorMessage(error));
    } finally {
      state.opticalBusy = false;
      button.removeAttribute("aria-busy");
      button.textContent = "この画面に QR を出す";
      updateOpticalAction();
    }
  }

  function confirmLargeOptical(result) {
    var largeTransferBytes = 16 * 1024 * 1024;
    if (Number(result.originalBytes || 0) <= largeTransferBytes) {
      return true;
    }
    return window.confirm(
      "選んだ内容は " + formatBytes(result.originalBytes) + " あります。大きいため転送に時間がかかります。\n\n" +
      "見込みは最短 " + formatSeconds(result.minimumSeconds) + "（" +
      Number(result.frameBytes).toLocaleString("ja-JP") + " bytes / frame・" +
      Number(result.framesPerSecond).toLocaleString("ja-JP") + " fps）です。\n" +
      "実際には、取りこぼしたフレームを噴水符号で補うぶん長くなります。\n\n転送を始めますか？");
  }

  function setActualFrameAmount(value) {
    var control = document.getElementById("frameAmount");
    control.querySelectorAll("[data-actual]").forEach(function (option) { option.remove(); });
    if (!Array.from(control.options).some(function (option) { return Number(option.value) === value; })) {
      var option = document.createElement("option");
      option.value = String(value);
      option.textContent = Number(value).toLocaleString("ja-JP") + " bytes（自動調整）";
      option.dataset.actual = "true";
      control.appendChild(option);
    }
    control.value = String(value);
  }

  function updateFrameAmountOptions() {
    var maximum = opticalCore.maximumFrameBytes(document.getElementById("errorCorrection").value);
    var control = document.getElementById("frameAmount");
    Array.from(control.options).forEach(function (option) { option.disabled = Number(option.value) > maximum; });
    if (Number(control.value) > maximum) {
      control.value = Array.from(control.options).filter(function (option) { return !option.disabled; })
        .reduce(function (value, option) { return Math.max(value, Number(option.value)); }, 0).toString();
    }
  }

  function startQrRenderer(session) {
    var side = 17 + 4 * Number(session.qrVersion) + 8;
    var renderer = {
      session: session, side: side, nextSequence: 0, queue: [],
      staging: document.createElement("canvas"), interval: 1000 / session.framesPerSecond,
      nextAt: performance.now(), animationFrame: 0, stopped: false,
      batches: new Set(), pendingBatches: 0, drawn: 0, startedAt: 0, lastStatusAt: 0, lastImage: null
    };
    renderer.staging.width = side;
    renderer.staging.height = side;
    state.opticalRenderer = renderer;
    sizeQrCanvas(renderer);
    pumpQrFrames(renderer);
    var tick = function (now) {
      if (!isCurrentQrRenderer(renderer)) return;
      renderer.animationFrame = window.requestAnimationFrame(tick);
      if (now < renderer.nextAt) return;
      var entry = renderer.queue[0];
      if (!entry || !entry.image) return; // keep the last complete QR visible
      renderer.queue.shift();
      pumpQrFrames(renderer);
      drawQrFrame(renderer, entry.image);
      if (!renderer.startedAt) renderer.startedAt = now;
      renderer.drawn++;
      if (now - renderer.lastStatusAt >= 250) {
        var actualFps = (renderer.drawn - 1) * 1000 / Math.max(1, now - renderer.startedAt);
        document.getElementById("qrProgress").textContent =
          "フレーム " + renderer.drawn.toLocaleString("ja-JP") +
          " ・ 表示 " + actualFps.toFixed(1) + " / " + session.framesPerSecond + " fps" +
          " ・ " + session.frameBytes.toLocaleString("ja-JP") + " B/QR" +
          " ・ 元ブロック " + session.sourceBlocks.toLocaleString("ja-JP");
        renderer.lastStatusAt = now;
      }
      renderer.nextAt += renderer.interval;
      // Do not rapidly flash several catch-up frames after a stalled request/tab.
      if (renderer.nextAt <= now) renderer.nextAt = now + renderer.interval;
    };
    renderer.animationFrame = window.requestAnimationFrame(tick);
  }

  function pumpQrFrames(renderer) {
    while (isCurrentQrRenderer(renderer) && renderer.pendingBatches < 2 && renderer.queue.length < 24) {
      var batch = { sequence: renderer.nextSequence >>> 0, entries: [], failures: 0, retryTimer: null, controller: null };
      for (var index = 0; index < Math.min(8, 24 - renderer.queue.length); index++) {
        batch.entries.push({ sequence: (batch.sequence + index) >>> 0, image: null });
      }
      renderer.nextSequence = (batch.sequence + batch.entries.length) >>> 0;
      Array.prototype.push.apply(renderer.queue, batch.entries);
      renderer.pendingBatches++;
      renderer.batches.add(batch);
      fetchQrBatch(renderer, batch);
    }
  }

  async function fetchQrBatch(renderer, batch) {
    if (!isCurrentQrRenderer(renderer)) return;
    batch.controller = new AbortController();
    var timeout = window.setTimeout(function () { batch.controller.abort(); }, 10000);
    try {
      var response = await fetch("/api/optical/frames?token=" + encodeURIComponent(renderer.session.token) +
        "&seq=" + batch.sequence + "&count=" + batch.entries.length,
        { cache: "no-store", signal: batch.controller.signal });
      if (!response.ok) {
        var httpError = new Error("QR frame HTTP " + response.status);
        httpError.status = response.status;
        throw httpError;
      }
      var buffer = await response.arrayBuffer();
      if (!isCurrentQrRenderer(renderer)) return;
      var frames = opticalCore.unpackFrames(buffer, renderer.side, batch.sequence, batch.entries.length);
      frames.forEach(function (frame, index) {
        batch.entries[index].image = new ImageData(frame.pixels, renderer.side, renderer.side);
      });
      renderer.batches.delete(batch);
      renderer.pendingBatches--;
      pumpQrFrames(renderer);
    } catch (error) {
      if (!isCurrentQrRenderer(renderer)) return;
      if (error.status >= 400 && error.status < 500 && error.status !== 408 && error.status !== 429) {
        showToast("QR 表示のセッションが終了しました。もう一度開始してください。");
        stopOptical();
        return;
      }
      batch.failures++;
      document.getElementById("qrProgress").textContent = "QR の通信を再試行しています…（表示を保持中）";
      batch.retryTimer = window.setTimeout(function () {
        batch.retryTimer = null;
        fetchQrBatch(renderer, batch);
      }, Math.min(3000, 150 * Math.pow(2, Math.min(batch.failures - 1, 5))));
    } finally {
      window.clearTimeout(timeout);
    }
  }

  function drawQrFrame(renderer, image) {
    renderer.lastImage = image;
    var stagingContext = renderer.staging.getContext("2d");
    stagingContext.putImageData(image, 0, 0);
    var canvas = document.getElementById("qrFrame");
    var context = canvas.getContext("2d");
    context.imageSmoothingEnabled = false;
    context.drawImage(renderer.staging, 0, 0, canvas.width, canvas.height);
  }

  function sizeQrCanvas(renderer) {
    if (!isCurrentQrRenderer(renderer)) {
      return;
    }
    var canvas = document.getElementById("qrFrame");
    var dpr = window.devicePixelRatio || 1;
    var overlay = document.getElementById("qrOverlay");
    var overlayStyle = getComputedStyle(overlay);
    var horizontalChrome = Number.parseFloat(overlayStyle.paddingLeft) +
      Number.parseFloat(overlayStyle.paddingRight) +
      Number.parseFloat(overlayStyle.borderLeftWidth) +
      Number.parseFloat(overlayStyle.borderRightWidth);
    var containerWidth = overlay.getBoundingClientRect().width || window.innerWidth;
    var verticalChrome = 70;
    overlay.querySelectorAll(".qr-controls, .qr-progress, .qr-hint").forEach(function (element) {
      if (getComputedStyle(element).display !== "none") verticalChrome += element.getBoundingClientRect().height + 12;
    });
    var viewportBudget = Math.max(1, Math.min(window.innerWidth - 36, window.innerHeight - verticalChrome));
    var containerBudget = Math.max(1, containerWidth - horizontalChrome);
    var cssBudget = Math.max(1, Math.min(
      viewportBudget,
      containerBudget,
      state.qrDisplaySize));
    var scale = Math.max(1, Math.floor((cssBudget * dpr) / renderer.side));
    var pixelSide = renderer.side * scale;
    if (canvas.width !== pixelSide || canvas.height !== pixelSide) {
      canvas.width = pixelSide;
      canvas.height = pixelSide;
    }
    canvas.style.width = (canvas.width / dpr) + "px";
    canvas.style.height = (canvas.height / dpr) + "px";
    if (renderer.lastImage) drawQrFrame(renderer, renderer.lastImage);
  }

  function isCurrentQrRenderer(renderer) {
    return !renderer.stopped && state.opticalRenderer === renderer &&
      state.opticalSession && state.opticalSession.token === renderer.session.token;
  }

  async function requestScreenWakeLock() {
    if (state.screenWakeLock || state.wakeLockPending || !navigator.wakeLock || document.visibilityState !== "visible") return;
    state.wakeLockPending = true;
    try {
      var lock = await navigator.wakeLock.request("screen");
      if (!state.opticalSession && !state.cameraRunning) { await lock.release(); return; }
      state.screenWakeLock = lock;
      lock.addEventListener("release", function () {
        if (state.screenWakeLock === lock) state.screenWakeLock = null;
      });
    } catch (_) {
      // Best effort: unsupported browsers and battery-saving mode may refuse it.
    } finally { state.wakeLockPending = false; }
  }

  function releaseScreenWakeLock() {
    if (state.opticalSession || state.cameraRunning) return;
    var lock = state.screenWakeLock;
    state.screenWakeLock = null;
    if (lock) lock.release().catch(function () {});
  }

  function stopOptical(unloading) {
    state.opticalStartGeneration++;
    var renderer = state.opticalRenderer;
    state.opticalRenderer = null;
    if (renderer) {
      renderer.stopped = true;
      window.cancelAnimationFrame(renderer.animationFrame);
      renderer.batches.forEach(function (batch) {
        window.clearTimeout(batch.retryTimer);
        if (batch.controller) batch.controller.abort();
      });
      renderer.batches.clear();
      renderer.queue = [];
    }
    var session = state.opticalSession;
    state.opticalSession = null;
    var overlay = document.getElementById("qrOverlay");
    if (overlay) {
      overlay.hidden = true;
    }
    releaseScreenWakeLock();
    if (session && unloading === true && navigator.sendBeacon) {
      navigator.sendBeacon("/api/optical/stop", new Blob([JSON.stringify({ token: session.token })], { type: "application/json" }));
    } else if (session) {
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

  async function toggleCamera() {
    if (state.cameraRunning || state.cameraStarting) {
      stopCamera(true, false);
      resetReceivePanel();
      return;
    }
    await startCamera();
  }

  async function startCamera() {
    if (state.cameraRunning || state.cameraStarting || !state.status) {
      return;
    }

    if (state.opticalSession || state.opticalBusy) {
      stopOptical();
    }
    setOpticalView("receive");
    resetReceivePanel();
    state.cameraStarting = true;
    state.cameraComplete = false;
    state.cameraHasDecoded = false;
    state.cameraTracking = null;
    state.cameraLastResultId = -1;
    state.cameraLastCaptureAt = 0;
    state.cameraLastDecodedAt = 0;
    state.cameraFrameId = 0;
    var generation = ++state.cameraGeneration;
    setCameraLabel("カメラを準備しています…");
    updateOpticalAction();

    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
      finishCameraError(window.isSecureContext
        ? "このブラウザではカメラを使えません。"
        : "カメラを使うには https の安全な接続で開いてください。");
      return;
    }

    var captureWidth = Number(document.getElementById("cameraWidth").value || "1280");
    var captureFps = Number(document.getElementById("cameraFps").value || "60");
    var stream = null;
    var constraints = {
      facingMode: "environment",
      width: { ideal: captureWidth },
      height: { ideal: Math.round(captureWidth * 3 / 4) }
    };
    try {
      try {
        stream = await navigator.mediaDevices.getUserMedia({
          audio: false,
          video: Object.assign({}, constraints, { frameRate: { exact: captureFps } })
        });
      } catch (error) {
        if (generation !== state.cameraGeneration) return;
        if (error.name !== "OverconstrainedError" && error.name !== "ConstraintNotSatisfiedError") throw error;
        stream = await navigator.mediaDevices.getUserMedia({
          audio: false,
          video: Object.assign({}, constraints, { frameRate: { ideal: captureFps } })
        });
      }

      if (generation !== state.cameraGeneration) {
        stream.getTracks().forEach(function (track) { track.stop(); });
        return;
      }

      // A unique ID prevents a delayed POST/stop from an earlier start erasing this run.
      state.cameraReceiverId = createRemoteId();
      await requestJson("/api/optical/receive/start", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ receiverId: state.cameraReceiverId })
      });

      if (generation !== state.cameraGeneration) {
        stream.getTracks().forEach(function (track) { track.stop(); });
        return;
      }

      state.cameraStream = stream;
      var video = document.getElementById("cameraVideo");
      video.srcObject = stream;
      video.hidden = false;
      await video.play();
      if (generation !== state.cameraGeneration) {
        stream.getTracks().forEach(function (track) { track.stop(); });
        return;
      }
      var receiverId = state.cameraReceiverId;
      state.cameraUpload = new opticalCore.UploadQueue({
        send: function (frames, signal) {
          return requestJson("/api/optical/receive/frames", {
            method: "POST", headers: { "Content-Type": "application/json" }, signal: signal,
            body: JSON.stringify({ receiverId: receiverId, frames: frames.map(bytesToBase64),
              openWhenDone: document.getElementById("receiveOpenFolder").getAttribute("aria-checked") === "true" })
          }, 15000);
        },
        onProgress: function (result) {
          if (generation === state.cameraGeneration && state.cameraRunning) applyReceiveProgress(result);
        },
        onTransient: function () {
          if (generation === state.cameraGeneration && state.cameraRunning)
            setCameraLabel("接続を再試行しています…（受信済みデータは保持）");
        },
        onFatal: function (error) {
          if (generation === state.cameraGeneration) finishCameraError(errorMessage(error));
        }
      });
      startCameraWorkers(generation);
      state.cameraStarting = false;
      state.cameraRunning = true;
      requestScreenWakeLock();
      stream.getVideoTracks().forEach(function (track) {
        track.addEventListener("ended", function () {
          if (generation === state.cameraGeneration && state.cameraRunning)
            finishCameraError("カメラが切断されました。接続を確認して再開してください。");
        });
      });
      document.getElementById("cameraPreview").classList.add("is-live");
      document.getElementById("cameraFrame").classList.add("is-live");
      reportCameraSettings();
      applyCameraExtras();
      setCameraLabel("ストリームを探しています…");
      setCapability("statusCamera", "利用可能", true);
      updateOpticalAction();
      armCameraQuietHint(generation);
      scheduleCameraFrame(generation);
    } catch (error) {
      if (stream) {
        stream.getTracks().forEach(function (track) { track.stop(); });
      }
      if (generation === state.cameraGeneration) {
        finishCameraError(cameraErrorMessage(error));
      }
    }
  }

  function reportCameraSettings() {
    var track = state.cameraStream && state.cameraStream.getVideoTracks()[0];
    if (!track || typeof track.getSettings !== "function") {
      return;
    }
    var settings = track.getSettings();
    var askedFps = Number(document.getElementById("cameraFps").value || "60");
    var gotFps = Math.round(Number(settings.frameRate || 0));
    var fpsNote = gotFps && gotFps !== askedFps ? "（指定 " + askedFps + "）" : "";
    document.getElementById("cameraActual").textContent =
      String(settings.width || "—") + "×" + String(settings.height || "—") +
      " @ " + (gotFps || "—") + " fps" + fpsNote + "・worker " +
      state.cameraWorkers.length;
  }

  async function applyCameraExtras() {
    var track = state.cameraStream && state.cameraStream.getVideoTracks()[0];
    if (!track || typeof track.getCapabilities !== "function") {
      return;
    }
    var capabilities;
    try {
      capabilities = track.getCapabilities();
    } catch (_) {
      return;
    }

    var focusModes = capabilities && capabilities.focusMode;
    if (Array.isArray(focusModes) && focusModes.indexOf("continuous") >= 0) {
      try {
        await track.applyConstraints({ advanced: [{ focusMode: "continuous" }] });
      } catch (_) {
        // 上流と同じく、continuous focus を拒むカメラは元の設定のまま使う。
      }
    }

    var maxFrameRate = capabilities && capabilities.frameRate && capabilities.frameRate.max;
    if (maxFrameRate) {
      Array.from(document.getElementById("cameraFps").options).forEach(function (option) {
        option.disabled = Number(option.value) > Number(maxFrameRate);
      });
    }
  }

  async function applyReceiveSettings() {
    if (!state.cameraRunning || state.cameraComplete) {
      document.getElementById("cameraActual").textContent = "カメラ開始時に適用";
      return;
    }

    startCameraWorkers(state.cameraGeneration);
    var track = state.cameraStream && state.cameraStream.getVideoTracks()[0];
    if (!track) {
      return;
    }
    var width = Number(document.getElementById("cameraWidth").value || "1280");
    try {
      await track.applyConstraints({
        width: { ideal: width },
        height: { ideal: Math.round(width * 3 / 4) },
        frameRate: { ideal: Number(document.getElementById("cameraFps").value || "60") }
      });
    } catch (_) {
      document.getElementById("cameraActual").textContent =
        "このカメラは実行中の変更を拒みました。再起動すると適用されます";
      return;
    }
    reportCameraSettings();
  }

  function startCameraWorkers(generation) {
    stopCameraWorkers();
    state.cameraTracking = null;
    var workerCount = Math.max(1, Math.min(3, Number(document.getElementById("cameraWorkers").value || "2")));
    for (var index = 0; index < workerCount; index++) {
      (function () {
        var worker = new Worker("/qr-worker.js?v=__FERRY_BUILD_ID__");
        var slot = { worker: worker, busy: false, failed: false, ready: false, frameId: -1, initTimer: null, region: null };
        var fail = function (reason) {
          window.clearTimeout(slot.initTimer);
          slot.busy = false;
          slot.failed = true;
          worker.terminate();
          // The worker already knows why it died. Showing only the fixed sentence
          // hid the real cause and left nothing to act on.
          if (reason && !state.cameraWorkerReason) state.cameraWorkerReason = String(reason);
          if (generation === state.cameraGeneration && state.cameraWorkers.indexOf(slot) >= 0 &&
              state.cameraWorkers.every(function (candidate) { return candidate.failed; }))
            finishCameraError("QR 読み取り機能を読み込めませんでした。画面を再読み込みしてください。"
              + (state.cameraWorkerReason ? "（" + state.cameraWorkerReason + "）" : ""));
        };
        slot.initTimer = window.setTimeout(fail, 15000);
        worker.onmessage = function (event) {
          if (generation !== state.cameraGeneration || state.cameraWorkers.indexOf(slot) < 0) return;
          var message = event.data || {};
          if (message.fatal) { fail(message.error); return; }
          if (message.id === -1) {
            window.clearTimeout(slot.initTimer);
            slot.ready = message.ready === true;
            if (!slot.ready) fail(message.error);
            return;
          }
          if (message.id !== slot.frameId) return;
          slot.busy = false;
          if (!state.cameraRunning) return;
          var bytes = message.bytes ? new Uint8Array(message.bytes) : null;
          var frame = bytes && opticalCore.parseFrame(bytes);
          if (frame && state.cameraUpload && state.cameraUpload.session &&
              frame.session !== state.cameraUpload.session) frame = null;
          if (message.id > state.cameraLastResultId) {
            state.cameraLastResultId = message.id;
            if (frame) {
              state.cameraLastDecodedAt = performance.now();
              var tracking = opticalCore.trackPosition(message.position, slot.region,
                slot.videoWidth, slot.videoHeight, state.cameraLastDecodedAt);
              if (tracking) state.cameraTracking = tracking;
            } else if (state.cameraTracking) state.cameraTracking.misses++;
          }
          if (frame && state.cameraUpload) state.cameraUpload.push(bytes);
        };
        worker.onerror = fail;
        state.cameraWorkers.push(slot);
      })();
    }
  }

  function stopCameraWorkers() {
    state.cameraWorkers.forEach(function (slot) {
      window.clearTimeout(slot.initTimer);
      slot.worker.terminate();
    });
    state.cameraWorkers = [];
  }

  function scheduleCameraFrame(generation) {
    if (generation !== state.cameraGeneration || !state.cameraRunning) {
      return;
    }
    var video = document.getElementById("cameraVideo");
    var next = function () {
      if (generation !== state.cameraGeneration || !state.cameraRunning) {
        return;
      }
      captureCameraFrame(generation);
      scheduleCameraFrame(generation);
    };
    if (typeof video.requestVideoFrameCallback === "function") {
      video.requestVideoFrameCallback(next);
    } else {
      window.requestAnimationFrame(next);
    }
  }

  function captureCameraFrame(generation) {
    var slot = state.cameraWorkers.find(function (candidate) { return candidate.ready && !candidate.busy && !candidate.failed; });
    if (!slot) return;
    var video = document.getElementById("cameraVideo");
    var width = video.videoWidth, height = video.videoHeight;
    if (!width || !height) return;
    var now = performance.now();
    // Avoid burning CPU on blank video; immediately return to the capture rate on detection.
    if ((!state.cameraTracking || now - state.cameraLastDecodedAt > 1200) && now - state.cameraLastCaptureAt < 65) return;
    state.cameraLastCaptureAt = now;
    var frameId = state.cameraFrameId++;
    var region = opticalCore.scanRegion(width, height, state.cameraTracking, frameId, now);
    if (cameraCanvas.width !== region.width || cameraCanvas.height !== region.height) {
      cameraCanvas.width = region.width;
      cameraCanvas.height = region.height;
    }
    try {
      var context = cameraCanvas.getContext("2d", { willReadFrequently: true });
      context.drawImage(video, region.x, region.y, region.width, region.height, 0, 0, region.width, region.height);
      var image = context.getImageData(0, 0, region.width, region.height);
      slot.busy = true;
      slot.frameId = frameId;
      slot.region = region;
      slot.videoWidth = width;
      slot.videoHeight = height;
      slot.worker.postMessage({ id: frameId, buf: image.data.buffer, w: region.width, h: region.height,
        session: state.cameraUpload && state.cameraUpload.session }, [image.data.buffer]);
    } catch (error) {
      slot.busy = false;
      if (generation === state.cameraGeneration) finishCameraError(cameraErrorMessage(error));
    }
  }

  function applyReceiveProgress(result) {
    if (!result || !result.recognized || state.cameraComplete) {
      return;
    }

    state.cameraHasDecoded = true;
    window.clearTimeout(state.cameraQuietTimer);
    state.cameraQuietTimer = null;
    document.getElementById("cameraNoSignal").hidden = true;
    var helpDialog = document.getElementById("cameraHelpDialog");
    if (helpDialog.open) {
      helpDialog.close();
    }
    var percent = Math.max(0, Math.min(100, Math.round(Number(result.progress || 0) * 100)));
    document.getElementById("receiveProgressBar").style.width = percent + "%";
    document.getElementById("receiveProgress").setAttribute("aria-valuenow", String(percent));
    document.getElementById("receiveState").textContent = result.complete ? "受信完了" : "受信中 " + percent + "%";
    document.getElementById("receiveRate").textContent = formatRate(result.kilobytesPerSecond);
    document.getElementById("receiveFrames").textContent = Number(result.framesCollected || 0).toLocaleString("ja-JP") + " 枚";
    var elapsed = Number(result.elapsedSeconds || 0);
    var progress = Number(result.progress || 0);
    var remaining = progress > 0 && progress < 1 ? elapsed * (1 - progress) / progress : 0;
    document.getElementById("receiveEta").textContent = result.complete
      ? formatSeconds(elapsed) + " 合計"
      : remaining > 0
        ? "残り約 " + formatSeconds(remaining)
        : "見積もり中";
    document.getElementById("receiveDetail").textContent =
      Number(result.solvedBlocks || 0).toLocaleString("ja-JP") + " / " +
      Number(result.sourceBlocks || 0).toLocaleString("ja-JP") + " ブロック";
    document.getElementById("receiveItem").textContent = formatBytes(result.totalBytes) + " の転送";
    setCameraLabel(result.complete ? "受信完了" : "QR を読み取っています…");

    if (!result.complete) {
      armCameraQuietHint(state.cameraGeneration, 4000);
      return;
    }

    state.cameraComplete = true;
    document.getElementById("cameraFrame").classList.add("is-complete");
    document.getElementById("receiveProgressBar").style.width = "100%";
    document.getElementById("receiveProgress").setAttribute("aria-valuenow", "100");
    document.getElementById("receiveDetail").textContent =
      Number(result.fileCount || 0).toLocaleString("ja-JP") + " ファイルを検算して保存しました";
    document.getElementById("receiveItem").textContent =
      (result.label || "受信データ") + "（" + Number(result.fileCount || 0).toLocaleString("ja-JP") + " ファイル）";
    var output = document.getElementById("receiveOutputPath");
    output.textContent = result.outputPath || ".\\output";
    output.title = result.outputPath || "";
    stopCamera(false, true);
    setCameraLabel("受信完了");
    showToast(Number(result.fileCount || 0).toLocaleString("ja-JP") + " ファイルを受け取りました");
    if (result.openError) {
      showToast("受信は完了しました。フォルダを開けませんでした: " + result.openError);
    }
  }

  function stopCamera(clearServer, keepLabel) {
    state.cameraGeneration++;
    if (state.cameraUpload) state.cameraUpload.stop();
    state.cameraUpload = null;
    state.cameraTracking = null;
    window.clearTimeout(state.cameraQuietTimer);
    state.cameraQuietTimer = null;
    document.getElementById("cameraNoSignal").hidden = true;
    stopCameraWorkers();
    if (state.cameraStream) {
      state.cameraStream.getTracks().forEach(function (track) { track.stop(); });
    }
    state.cameraStream = null;
    state.cameraStarting = false;
    state.cameraRunning = false;
    releaseScreenWakeLock();
    var helpDialog = document.getElementById("cameraHelpDialog");
    if (helpDialog.open) {
      helpDialog.close();
    }
    document.getElementById("cameraFrame").classList.remove("is-live");
    document.getElementById("cameraPreview").classList.remove("is-live");
    var video = document.getElementById("cameraVideo");
    video.pause();
    video.srcObject = null;
    video.hidden = true;
    if (!keepLabel) {
      setCameraLabel("カメラは未接続");
    }
    if (clearServer && state.cameraReceiverId) {
      fetch("/api/optical/receive/stop", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ receiverId: state.cameraReceiverId })
      }).catch(function () {});
    }
    updateOpticalAction();
  }

  function finishCameraError(message) {
    stopCamera(true, true);
    setCameraLabel(message);
    document.getElementById("receiveState").textContent = "読み取り停止";
    document.getElementById("receiveDetail").textContent = message;
    showToast(message);
  }

  function resetReceivePanel() {
    state.cameraComplete = false;
    state.cameraHasDecoded = false;
    document.getElementById("cameraFrame").classList.remove("is-complete");
    document.getElementById("receiveState").textContent = "受信待ち";
    document.getElementById("receiveRate").textContent = "0 KB/s";
    document.getElementById("receiveProgressBar").style.width = "0%";
    document.getElementById("receiveProgress").setAttribute("aria-valuenow", "0");
    document.getElementById("receiveDetail").textContent = "フレームを待っています";
    document.getElementById("receiveFrames").textContent = "0 枚";
    document.getElementById("receiveEta").textContent = "見積もり中";
    document.getElementById("cameraNoSignal").hidden = true;
    document.getElementById("receiveItem").textContent = "未定";
    var output = document.getElementById("receiveOutputPath");
    output.textContent = ".\\output\\受信名_YYYYMMDD-HHmm";
    output.title = "";
  }

  function armCameraQuietHint(generation, delay) {
    window.clearTimeout(state.cameraQuietTimer);
    state.cameraQuietTimer = window.setTimeout(function () {
      if (generation !== state.cameraGeneration || !state.cameraRunning || state.cameraComplete) {
        return;
      }
      document.getElementById("cameraNoSignal").hidden = false;
      if (state.cameraHasDecoded) setCameraLabel("QR を再探索しています…（受信済みデータは保持）");
    }, delay || 8000);
  }

  function dismissCameraQuietHint() {
    document.getElementById("cameraNoSignal").hidden = true;
    if (state.cameraRunning && !state.cameraComplete) {
      armCameraQuietHint(state.cameraGeneration, 15000);
    }
  }

  function setCameraLabel(message) {
    document.getElementById("cameraLabel").textContent = message;
  }

  function cameraErrorMessage(error) {
    var name = error && error.name ? error.name : "";
    if (name === "NotAllowedError" || name === "SecurityError") {
      return "カメラの使用が許可されていません。ブラウザの設定で許可して、もう一度押してください。";
    }
    if (name === "NotFoundError" || name === "DevicesNotFoundError") {
      setCapability("statusCamera", "なし", false);
      return "カメラが見つかりません。";
    }
    if (name === "NotReadableError" || name === "TrackStartError") {
      return "カメラを開けません。ほかのアプリが使っていないか確認してください。";
    }
    return errorMessage(error);
  }

  function bytesToBase64(bytes) {
    var binary = "";
    for (var index = 0; index < bytes.length; index++) {
      binary += String.fromCharCode(bytes[index]);
    }
    return window.btoa(binary);
  }

  function formatRate(value) {
    var rate = Math.max(0, Number(value) || 0);
    return rate.toLocaleString("ja-JP", { maximumFractionDigits: 1 }) + " KB/s";
  }

  function shutdownCamera() {
    if (!state.cameraRunning && !state.cameraStarting) {
      return;
    }
    var receiverId = state.cameraReceiverId;
    stopCamera(false, true);
    if (!receiverId || !navigator.sendBeacon) {
      return;
    }
    navigator.sendBeacon(
      "/api/optical/receive/stop",
      new Blob([JSON.stringify({ receiverId: receiverId })], { type: "application/json" }));
  }

  function updateOpticalAction() {
    updateTransferFormats();
    var hasFiles = state.selections.optical.size > 0;
    document.getElementById("showQr").disabled = state.opticalBusy
      || !state.status
      || !hasFiles;
    var cameraButton = document.getElementById("readLocalCamera");
    cameraButton.disabled = !state.status;
    cameraButton.textContent = state.cameraRunning
      ? "読み取りを止める"
      : state.cameraStarting
        ? "カメラの準備を中止"
        : state.role === "remote"
          ? "この端末のカメラで読む"
          : "この PC のカメラで読む";
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
        body.frameBytes = Number(document.getElementById("frameAmount").value || "1000");
        body.framesPerSecond = Number(document.getElementById("frameRate").value || "30");
        body.errorCorrection = String(document.getElementById("errorCorrection").value || "L");
        body.displaySize = Number(document.getElementById("qrDisplaySize").value || "900");
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
    setQrDisplaySize(state.qrDisplaySize + direction * 50);
  }

  function applyQrSize() {
    setQrDisplaySize(Number(document.getElementById("qrDisplaySize").value || "900"));
  }

  function setQrDisplaySize(value) {
    var control = document.getElementById("qrDisplaySize");
    var minimum = Number(control.min || "300");
    var maximum = Number(control.max || "1200");
    value = Math.round(Number(value) / 50) * 50;
    value = Math.max(minimum, Math.min(maximum, value || 900));
    state.qrDisplaySize = value;
    control.value = String(value);
    document.getElementById("qrSizeSetting").textContent = value.toLocaleString("ja-JP") + " px";
    document.getElementById("qrSizeOutput").textContent = value.toLocaleString("ja-JP") + " px";
    document.getElementById("smallerQr").disabled = value === minimum;
    document.getElementById("largerQr").disabled = value === maximum;
    if (state.opticalRenderer) {
      sizeQrCanvas(state.opticalRenderer);
    }
  }

  function formatSeconds(value) {
    var seconds = Math.max(0, Number(value) || 0);
    if (seconds < 10) {
      return seconds.toFixed(1) + " 秒";
    }
    if (seconds < 60) {
      return Math.ceil(seconds).toLocaleString("ja-JP") + " 秒";
    }
    var rounded = Math.ceil(seconds);
    var minutes = Math.floor(rounded / 60);
    var remainder = rounded % 60;
    return minutes.toLocaleString("ja-JP") + " 分" + (remainder ? " " + remainder + " 秒" : "");
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

  async function loadRemoteEntry() {
    if (state.role !== "local") {
      return;
    }
    var image = document.getElementById("pairingQr");
    var status = document.getElementById("pairingStatus");
    var url = document.getElementById("pairingUrl");
    try {
      var entry = await requestJson("/api/remote-entry");
      if (!entry || !entry.url) {
        throw new Error("Tailscale のスマホ連携 URL を取得できませんでした。");
      }
      image.onload = function () {
        image.hidden = false;
        status.textContent = "スマホで読んで接続";
      };
      image.onerror = function () {
        image.hidden = true;
        status.textContent = "スマホ連携の QR を表示できませんでした";
      };
      url.textContent = entry.url;
      url.title = entry.url;
      url.hidden = false;
      image.src = "/api/remote-entry/qr";
    } catch (error) {
      image.hidden = true;
      url.hidden = true;
      status.textContent = errorMessage(error);
    }
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
        // The active QR payload is immutable. Metadata refresh must not close it.
        applyFolder("optical", snapshot, true);
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
      startCamera();
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

  async function requestJson(url, options, timeoutMs) {
    var settings = Object.assign({}, options || {});
    var controller = timeoutMs ? new AbortController() : null;
    var originalSignal = settings.signal;
    var forwardAbort = function () { if (controller) controller.abort(); };
    var timer = null;
    if (controller) {
      settings.signal = controller.signal;
      if (originalSignal) {
        if (originalSignal.aborted) controller.abort();
        else originalSignal.addEventListener("abort", forwardAbort, { once: true });
      }
      timer = window.setTimeout(function () { controller.abort(); }, timeoutMs);
    }
    try {
      var response = await fetch(url, settings);
      var body;
      try { body = await response.json(); } catch (_) { body = null; }
      if (!response.ok || body === null) {
        var error = new Error(body && body.error ? body.error : "Ferry に接続できませんでした。");
        error.status = response.ok ? 502 : response.status;
        throw error;
      }
      return body;
    } finally {
      window.clearTimeout(timer);
      if (originalSignal && controller) originalSignal.removeEventListener("abort", forwardAbort);
    }
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
