function toUtcIso(value) {
  if (!value) {
    return "";
  }

  const localDate = new Date(value);
  if (Number.isNaN(localDate.getTime())) {
    return "";
  }

  return localDate.toISOString();
}

function formatUtcElement(element, formatter) {
  const utcValue = element.dataset.utc;
  if (!utcValue) {
    return;
  }

  const date = new Date(utcValue);
  if (Number.isNaN(date.getTime())) {
    return;
  }

  element.textContent = formatter.format(date);
}

function escapeHtml(value) {
  return (value || "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

function initializeCopyButtons(root) {
  const scope = root instanceof HTMLElement || root instanceof Document ? root : document;
  const buttons = scope.querySelectorAll(".copy-button");
  buttons.forEach((button) => {
    if (button.dataset.copyInitialized === "true") {
      return;
    }

    button.dataset.copyInitialized = "true";
    button.addEventListener("click", async () => {
      const detailsWrapper = button.closest(".details-content-wrap");
      const detailsContent = detailsWrapper ? detailsWrapper.querySelector(".details-content") : null;
      const text = detailsContent ? detailsContent.textContent || "" : button.getAttribute("data-copy-text") || "";
      if (!text.length) {
        return;
      }

      try {
        await navigator.clipboard.writeText(text);
        const originalText = button.textContent;
        button.textContent = "Copied";
        window.setTimeout(() => {
          button.textContent = originalText;
        }, 1200);
      } catch {
        const textArea = document.createElement("textarea");
        textArea.value = text;
        textArea.setAttribute("readonly", "readonly");
        textArea.style.position = "absolute";
        textArea.style.left = "-9999px";
        document.body.appendChild(textArea);
        textArea.select();
        document.execCommand("copy");
        document.body.removeChild(textArea);
        const originalText = button.textContent;
        button.textContent = "Copied";
        window.setTimeout(() => {
          button.textContent = originalText;
        }, 1200);
      }
    });
  });
}

function normalizeJsonSearchText(value) {
  return (value || "").toLowerCase().replace(/\s+/g, " ").trim();
}

function isJsonArrayIndexKey(value) {
  return /^\[\d+\]$/.test((value || "").trim());
}

function getJsonNodeSummaryText(node) {
  const summary = node.firstElementChild;
  return summary ? normalizeJsonSearchText(summary.textContent) : "";
}

function getJsonNodeChildrenContainer(node) {
  return Array.from(node.children).find((child) => child.classList && child.classList.contains("json-tree-node__children")) || null;
}

function getJsonNodePreviewElement(node) {
  return node.querySelector(":scope > summary [data-json-preview]");
}

function getVisibleJsonChildCount(node) {
  const childrenContainer = getJsonNodeChildrenContainer(node);
  if (!childrenContainer) {
    return 0;
  }

  return Array.from(childrenContainer.children).filter((child) => child instanceof HTMLElement && !child.classList.contains("d-none")).length;
}

function updateJsonNodePreview(node) {
  if (!(node instanceof HTMLElement)) {
    return;
  }

  const previewElement = getJsonNodePreviewElement(node);
  if (!(previewElement instanceof HTMLElement)) {
    return;
  }

  const propertySelect = node.querySelector("[data-json-filter-property]");
  const filterValue = getJsonNodeFilterValue(node);
  const nodeType = normalizeJsonSearchText(node.dataset.jsonType || "");
  const originalPreview = previewElement.dataset.jsonOriginalPreview || previewElement.textContent || "";
  const hasActiveFilter = propertySelect instanceof HTMLSelectElement
    && propertySelect.value
    && filterValue.length > 0;

  if (!hasActiveFilter) {
    previewElement.textContent = originalPreview;
    return;
  }

  const visibleCount = getVisibleJsonChildCount(node);
  if (nodeType === "array") {
    previewElement.textContent = `${visibleCount} item(s)`;
    return;
  }

  if (nodeType === "object") {
    previewElement.textContent = `${visibleCount} field(s)`;
    return;
  }

  previewElement.textContent = originalPreview;
}

function getJsonLeafMetadata(element) {
  return {
    key: normalizeJsonSearchText(element.dataset.jsonKey || ""),
    value: normalizeJsonSearchText(element.dataset.jsonValue || ""),
    combined: normalizeJsonSearchText(element.dataset.jsonSearchText || element.textContent)
  };
}

function appendJsonPathSegment(currentPath, segment) {
  const trimmedSegment = (segment || "").trim();
  if (!trimmedSegment || isJsonArrayIndexKey(trimmedSegment)) {
    return currentPath;
  }

  return currentPath ? `${currentPath}.${trimmedSegment}` : trimmedSegment;
}

function collectJsonLeafPaths(element, currentPath = "", displayPath = "") {
  if (!(element instanceof HTMLElement)) {
    return [];
  }

  if (element.hasAttribute("data-json-leaf")) {
    const rawKey = element.dataset.jsonKey || "";
    const metadata = getJsonLeafMetadata(element);
    const normalizedPath = appendJsonPathSegment(currentPath, metadata.key);
    const resolvedDisplayPath = appendJsonPathSegment(displayPath, rawKey);

    return [{
      path: normalizedPath,
      displayPath: resolvedDisplayPath,
      value: metadata.value,
      key: metadata.key
    }];
  }

  if (!element.hasAttribute("data-json-node")) {
    return [];
  }

  const rawNodeKey = (element.dataset.jsonKey || "").trim();
  const nodeKey = normalizeJsonSearchText(element.dataset.jsonKey || "");
  const nodeType = normalizeJsonSearchText(element.dataset.jsonType || "");
  const nextPath = nodeType === "array"
    ? currentPath
    : appendJsonPathSegment(currentPath, nodeKey);
  const nextDisplayPath = nodeType === "array"
    ? displayPath
    : appendJsonPathSegment(displayPath, rawNodeKey);

  const childrenContainer = getJsonNodeChildrenContainer(element);
  if (!childrenContainer) {
    return [];
  }

  return Array.from(childrenContainer.children).flatMap((child) => collectJsonLeafPaths(child, nextPath, nextDisplayPath));
}

function populateJsonPropertyOptions(node) {
  const propertySelect = node.querySelector("[data-json-filter-property]");
  const childrenContainer = getJsonNodeChildrenContainer(node);
  if (!(propertySelect instanceof HTMLSelectElement) || !childrenContainer) {
    return;
  }

  const propertyPaths = new Map();
  Array.from(childrenContainer.children).forEach((child) => {
    collectJsonLeafPaths(child).forEach((leaf) => {
      if (leaf.path) {
        propertyPaths.set(leaf.path, leaf.displayPath || leaf.path);
      }
    });
  });

  const existingValue = propertySelect.value;
  propertySelect.innerHTML = "";

  const defaultOption = document.createElement("option");
  defaultOption.value = "";
  defaultOption.textContent = "Choose property";
  propertySelect.appendChild(defaultOption);

  Array.from(propertyPaths.entries())
    .sort((left, right) => left[1].localeCompare(right[1]))
    .forEach(([path, label]) => {
      const option = document.createElement("option");
      option.value = path;
      option.textContent = label;
      propertySelect.appendChild(option);
    });

  if (existingValue && propertyPaths.has(existingValue)) {
    propertySelect.value = existingValue;
  }
}

function getJsonPropertyValues(node, propertyPath) {
  const childrenContainer = getJsonNodeChildrenContainer(node);
  if (!childrenContainer || !propertyPath) {
    return [];
  }

  const normalizedPath = normalizeJsonSearchText(propertyPath);
  const values = new Map();

  Array.from(childrenContainer.children).forEach((child) => {
    collectJsonLeafPaths(child).forEach((leaf) => {
      if (normalizeJsonSearchText(leaf.path) !== normalizedPath) {
        return;
      }

      if (!values.has(leaf.value)) {
        values.set(leaf.value, leaf.value);
      }
    });
  });

  return Array.from(values.values()).sort((left, right) => left.localeCompare(right));
}

function populateJsonValueOptions(node) {
  const propertySelect = node.querySelector("[data-json-filter-property]");
  const valueSelect = node.querySelector("[data-json-filter-value-select]");
  if (!(propertySelect instanceof HTMLSelectElement) || !(valueSelect instanceof HTMLSelectElement)) {
    return;
  }

  const existingValue = valueSelect.value;
  const values = getJsonPropertyValues(node, propertySelect.value);
  valueSelect.innerHTML = "";

  const defaultOption = document.createElement("option");
  defaultOption.value = "";
  defaultOption.textContent = propertySelect.value ? "Choose value" : "Choose property first";
  valueSelect.appendChild(defaultOption);

  values.forEach((value) => {
    const option = document.createElement("option");
    option.value = value;
    option.textContent = value || "(empty)";
    valueSelect.appendChild(option);
  });

  if (existingValue && values.includes(existingValue)) {
    valueSelect.value = existingValue;
  }
}

function syncJsonFilterInputMode(node) {
  const operatorSelect = node.querySelector("[data-json-filter-operator]");
  const valueSelect = node.querySelector("[data-json-filter-value-select]");
  const valueInput = node.querySelector("[data-json-filter-value]");
  if (!(operatorSelect instanceof HTMLSelectElement) ||
      !(valueSelect instanceof HTMLSelectElement) ||
      !(valueInput instanceof HTMLInputElement)) {
    return;
  }

  const useSelect = (operatorSelect.value || "equals") === "equals";
  valueSelect.classList.toggle("d-none", !useSelect);
  valueInput.classList.toggle("d-none", useSelect);
}

function getJsonNodeFilterValue(node) {
  const operatorSelect = node.querySelector("[data-json-filter-operator]");
  const valueSelect = node.querySelector("[data-json-filter-value-select]");
  const valueInput = node.querySelector("[data-json-filter-value]");
  const operator = operatorSelect instanceof HTMLSelectElement
    ? (operatorSelect.value || "equals")
    : "equals";

  if (operator === "equals" && valueSelect instanceof HTMLSelectElement) {
    return (valueSelect.value || "").trim();
  }

  return valueInput instanceof HTMLInputElement
    ? valueInput.value.trim()
    : "";
}

function matchesJsonCandidate(child, propertyPath, operator, filterValue) {
  if (!propertyPath || !filterValue) {
    return true;
  }

  const normalizedPath = normalizeJsonSearchText(propertyPath);
  const normalizedValue = normalizeJsonSearchText(filterValue);
  const candidateLeaves = collectJsonLeafPaths(child);

  return candidateLeaves.some((leaf) => {
    if (normalizeJsonSearchText(leaf.path) !== normalizedPath) {
      return false;
    }

    if (operator === "contains") {
      return leaf.value.includes(normalizedValue);
    }

    return leaf.value === normalizedValue;
  });
}

function applyJsonObjectFilter(node) {
  const propertySelect = node.querySelector("[data-json-filter-property]");
  const operatorSelect = node.querySelector("[data-json-filter-operator]");
  const childrenContainer = getJsonNodeChildrenContainer(node);

  if (!(propertySelect instanceof HTMLSelectElement) ||
      !(operatorSelect instanceof HTMLSelectElement) ||
      !childrenContainer) {
    return;
  }

  const propertyPath = propertySelect.value;
  const operator = operatorSelect.value || "equals";
  const filterValue = getJsonNodeFilterValue(node);

  Array.from(childrenContainer.children).forEach((child) => {
    if (!(child instanceof HTMLElement)) {
      return;
    }

    const isMatch = matchesJsonCandidate(child, propertyPath, operator, filterValue);
    child.classList.toggle("d-none", !isMatch);
  });

  updateJsonNodePreview(node);
}

function initializeHarTabs(root) {
  const tabButtons = root.querySelectorAll("[data-har-tab-target]");
  tabButtons.forEach((button) => {
    if (button.__harTabInitialized === true) {
      return;
    }

    button.__harTabInitialized = true;
    button.addEventListener("click", () => {
      const target = button.dataset.harTabTarget;
      if (!target) {
        return;
      }

      const tabRoot = button.closest(".panel-card");
      if (!tabRoot) {
        return;
      }

      tabRoot.querySelectorAll("[data-har-tab-target]").forEach((item) => {
        item.classList.toggle("har-tab-button--active", item === button);
      });

      tabRoot.querySelectorAll("[data-har-tab-panel]").forEach((panel) => {
        panel.classList.toggle("har-tab-panel--active", panel.dataset.harTabPanel === target);
      });
    });
  });
}

function initializeJsonNodeControls(root) {
  const jsonNodeControls = root.querySelectorAll("[data-json-node]");
  jsonNodeControls.forEach((node) => {
    if (node.__jsonInitialized === true) {
      return;
    }

    node.__jsonInitialized = true;

    const expandButton = node.querySelector("[data-json-expand]");
    const collapseButton = node.querySelector("[data-json-collapse]");
    const propertySelect = node.querySelector("[data-json-filter-property]");
    const operatorSelect = node.querySelector("[data-json-filter-operator]");
    const valueSelect = node.querySelector("[data-json-filter-value-select]");
    const valueInput = node.querySelector("[data-json-filter-value]");
    const clearButton = node.querySelector("[data-json-search-clear]");

    populateJsonPropertyOptions(node);
    populateJsonValueOptions(node);
    syncJsonFilterInputMode(node);

    expandButton?.addEventListener("click", () => {
      const childrenContainer = getJsonNodeChildrenContainer(node);
      if (!childrenContainer) {
        return;
      }

      Array.from(childrenContainer.children).forEach((child) => {
        if (child instanceof HTMLDetailsElement && child.hasAttribute("data-json-node")) {
          child.open = true;
        }
      });
    });

    collapseButton?.addEventListener("click", () => {
      const childrenContainer = getJsonNodeChildrenContainer(node);
      if (!childrenContainer) {
        return;
      }

      Array.from(childrenContainer.children).forEach((child) => {
        if (child instanceof HTMLDetailsElement && child.hasAttribute("data-json-node")) {
          child.open = false;
        }
      });
      node.open = true;
    });

    propertySelect?.addEventListener("change", () => {
      if (valueSelect instanceof HTMLSelectElement) {
        valueSelect.value = "";
      }
      if (valueInput instanceof HTMLInputElement) {
        valueInput.value = "";
      }
      populateJsonValueOptions(node);
      applyJsonObjectFilter(node);
    });

    operatorSelect?.addEventListener("change", () => {
      syncJsonFilterInputMode(node);
      applyJsonObjectFilter(node);
    });

    valueSelect?.addEventListener("change", () => {
      applyJsonObjectFilter(node);
    });

    valueInput?.addEventListener("input", () => {
      applyJsonObjectFilter(node);
    });

    clearButton?.addEventListener("click", () => {
      if (!(propertySelect instanceof HTMLSelectElement) ||
          !(operatorSelect instanceof HTMLSelectElement) ||
          !(valueInput instanceof HTMLInputElement) ||
          !(valueSelect instanceof HTMLSelectElement)) {
        return;
      }

      propertySelect.value = "";
      operatorSelect.value = "equals";
      valueSelect.value = "";
      valueInput.value = "";
      populateJsonValueOptions(node);
      syncJsonFilterInputMode(node);
      const childrenContainer = getJsonNodeChildrenContainer(node);
      if (!childrenContainer) {
        return;
      }

      Array.from(childrenContainer.children).forEach((child) => {
        if (child instanceof HTMLElement) {
          child.classList.remove("d-none");
        }
      });
      updateJsonNodePreview(node);
      propertySelect.focus();
    });
  });
}

document.addEventListener("DOMContentLoaded", () => {
  const form = document.getElementById("analysisForm")
    || document.querySelector(".har-validation-form")
    || document.querySelector('form[action*="RawView"]');
  const overlay = document.getElementById("analysisLoadingOverlay");
  const loadingTitle = document.getElementById("loadingTitle");
  const loadingDescription = document.getElementById("loadingDescription");
  const browserTimeZoneInput = document.querySelector('input[name="Filter.BrowserTimeZone"]');
  const fromInput = document.querySelector('input[name="Filter.From"]');
  const toInput = document.querySelector('input[name="Filter.To"]');
  const fromUtcInput = document.querySelector('input[name="Filter.FromUtc"]');
  const toUtcInput = document.querySelector('input[name="Filter.ToUtc"]');
  const selectedRequestKeyInput = document.querySelector('input[name="Filter.SelectedRequestKey"]');
  const useLocalCheckbox = document.querySelector('input[name="Filter.UseLocalLogPath"]');
  const uploadInput = document.getElementById("logFiles");
  const harInput = document.getElementById("harFile") || document.getElementById("harValidationFile");
  const submitButton = form ? form.querySelector('button[type="submit"]') : null;
  const analyzeStatusText = document.getElementById("analyzeStatusText") || document.getElementById("harAnalyzeStatusText");
  const detailFilters = document.querySelectorAll(".detail-service-filter");
  const repeatedQueryControls = document.querySelectorAll(".repeated-query-control");
  const timelineQueryControls = document.querySelectorAll(".timeline-query-control");
  const clearButtons = document.querySelectorAll(".input-clear-button");
  const harSelectButtons = document.querySelectorAll("[data-har-select]");
  const harRequestDetailsRoot = document.getElementById("harRequestDetailsRoot");
  const harApiList = document.getElementById("harApiList");
  const harApiLoadTrigger = document.getElementById("harApiLoadTrigger");
  const harApiLoadingState = document.getElementById("harApiLoadingState");
  const harApiSkeletonState = document.getElementById("harApiSkeletonState");
  const repeatedLogList = document.getElementById("repeatedLogList");
  const repeatedLogScroll = document.getElementById("repeatedLogScroll");
  const repeatedLoadTrigger = document.getElementById("repeatedLoadTrigger");
  const repeatedLoadingState = document.getElementById("repeatedLoadingState");
  const repeatedSkeletonState = document.getElementById("repeatedSkeletonState");
  const timelineLogList = document.getElementById("timelineLogList");
  const timelineLogScroll = document.getElementById("timelineLogScroll");
  const timelineLoadTrigger = document.getElementById("timelineLoadTrigger");
  const timelineLoadingState = document.getElementById("timelineLoadingState");
  const timelineSkeletonState = document.getElementById("timelineSkeletonState");
  const restoreSubmitterState = (button, originalText) => {
    if (!(button instanceof HTMLButtonElement)) {
      return;
    }

    button.disabled = false;
    button.textContent = originalText;
  };

  const hideOverlayState = () => {
    if (overlay) {
      overlay.classList.add("d-none");
      overlay.setAttribute("aria-hidden", "true");
    }

    if (analyzeStatusText) {
      analyzeStatusText.classList.add("d-none");
      analyzeStatusText.textContent = "";
    }
  };

  const triggerBrowserDownload = (blob, fileName) => {
    const objectUrl = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = objectUrl;
    anchor.download = fileName || "dashboard-reconstruction.zip";
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    window.setTimeout(() => URL.revokeObjectURL(objectUrl), 1500);
  };

  const extractDownloadFileName = (response) => {
    const disposition = response.headers.get("Content-Disposition") || "";
    const utfMatch = disposition.match(/filename\*=UTF-8''([^;]+)/i);
    if (utfMatch && utfMatch[1]) {
      return decodeURIComponent(utfMatch[1]);
    }

    const plainMatch = disposition.match(/filename=\"?([^\";]+)\"?/i);
    return plainMatch && plainMatch[1] ? plainMatch[1] : "dashboard-reconstruction.zip";
  };

  const handleDownloadSubmit = async (submitter) => {
    if (!(form instanceof HTMLFormElement) || !(submitter instanceof HTMLButtonElement)) {
      return false;
    }

    const originalText = submitter.textContent || "Generate Reconstruction Package";
    const formData = new FormData(form);
    const endpoint = submitter.formAction || form.action;
    const method = submitter.formMethod || form.method || "post";

    submitter.disabled = true;
    submitter.textContent = "Generating...";

    if (analyzeStatusText) {
      analyzeStatusText.classList.remove("d-none");
      analyzeStatusText.textContent = "Generating the dashboard reconstruction package from the cached HAR response...";
    }

    if (overlay) {
      if (loadingTitle) {
        loadingTitle.textContent = submitter.dataset.loadingTitle || "Generating dashboard package...";
      }

      if (loadingDescription) {
        loadingDescription.textContent = submitter.dataset.loadingDescription || "The app is generating the reconstruction package. Please wait.";
      }

      overlay.classList.remove("d-none");
      overlay.setAttribute("aria-hidden", "false");
    }

    try {
      const response = await fetch(endpoint, {
        method: method.toUpperCase(),
        body: formData,
        credentials: "same-origin"
      });

      if (!response.ok) {
        const errorText = await response.text();
        throw new Error(errorText || `Download failed with status ${response.status}`);
      }

      const blob = await response.blob();
      triggerBrowserDownload(blob, extractDownloadFileName(response));
      return true;
    } catch (error) {
      const message = error instanceof Error ? error.message : "Unable to generate the reconstruction package right now.";
      if (analyzeStatusText) {
        analyzeStatusText.classList.remove("d-none");
        analyzeStatusText.textContent = "Package generation failed. Please try again.";
      }

      window.alert(message);
      return false;
    } finally {
      hideOverlayState();
      restoreSubmitterState(submitter, originalText);
    }
  };
  let harSelectionTriggered = false;
  const harRequestCache = new Map();
  const isHarValidationPage = !!document.getElementById("harValidationFile") || !!harRequestDetailsRoot;

  if (browserTimeZoneInput) {
    browserTimeZoneInput.value = Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";
  }

  if (form) {
    form.addEventListener("submit", (event) => {
      const submitter = event.submitter instanceof HTMLButtonElement
        ? event.submitter
        : submitButton;

      if (submitter instanceof HTMLButtonElement && submitter.dataset.downloadSubmit === "true") {
        event.preventDefault();
        void handleDownloadSubmit(submitter);
        return;
      }

      if (fromUtcInput) {
        fromUtcInput.value = toUtcIso(fromInput ? fromInput.value : "");
      }

      if (toUtcInput) {
        toUtcInput.value = toUtcIso(toInput ? toInput.value : "");
      }

      const isUploadSubmit = (uploadInput && uploadInput.files && uploadInput.files.length > 0)
        || (harInput && harInput.files && harInput.files.length > 0);
      const customLoadingTitle = submitter instanceof HTMLButtonElement ? (submitter.dataset.loadingTitle || "") : "";
      const customLoadingDescription = submitter instanceof HTMLButtonElement ? (submitter.dataset.loadingDescription || "") : "";

      if (submitter instanceof HTMLButtonElement) {
        submitter.disabled = true;
        submitter.textContent = customLoadingTitle
          ? "Generating..."
          : isUploadSubmit ? "Uploading..." : "Analyzing...";
      }

      if (analyzeStatusText) {
        analyzeStatusText.classList.remove("d-none");
        if (harSelectionTriggered) {
          analyzeStatusText.textContent = "Loading the selected API request details...";
        } else if (customLoadingTitle) {
          analyzeStatusText.textContent = "Generating the dashboard reconstruction package from the cached HAR response...";
        } else if (isHarValidationPage) {
          analyzeStatusText.textContent = isUploadSubmit
            ? "Uploading the HAR file and preparing dashboard API validation..."
            : "Running dashboard HAR validation by using the already uploaded HAR file...";
        } else {
          analyzeStatusText.textContent = isUploadSubmit
            ? "Uploading selected files and preparing the analysis..."
            : "Running the analysis by using the already uploaded files...";
        }
      }

      if (overlay && (isUploadSubmit || harSelectionTriggered || customLoadingTitle)) {
        if (loadingTitle) {
          loadingTitle.textContent = customLoadingTitle
            ? customLoadingTitle
            : harSelectionTriggered
            ? "Loading API request details..."
            : isHarValidationPage
              ? "Uploading and analyzing HAR..."
              : "Uploading and analyzing logs...";
        }

        if (loadingDescription) {
          if (customLoadingDescription) {
            loadingDescription.textContent = customLoadingDescription;
          } else if (harSelectionTriggered) {
            loadingDescription.textContent = "The app is applying your HAR filters and preparing the selected request, headers, payload, and decoded response tree. Please wait.";
          } else if (isHarValidationPage) {
            loadingDescription.textContent = "The app is saving the HAR file, reading the API entries, applying your filters, and preparing the dashboard validation summary. Please wait until the upload and analysis complete.";
          } else {
            loadingDescription.textContent = "The app is saving the selected files, reading the entries, correlating identifiers, and building the summary. Please wait until the upload and analysis complete.";
          }
        }

        overlay.classList.remove("d-none");
        overlay.setAttribute("aria-hidden", "false");
      }

      if (submitter instanceof HTMLButtonElement) {
        if (submitter.hasAttribute("formaction") && submitter.formAction) {
          form.action = submitter.formAction;
        }

        if (submitter.hasAttribute("formmethod") && submitter.formMethod) {
          form.method = submitter.formMethod;
        }
      }
    });
  }

  clearButtons.forEach((button) => {
    button.addEventListener("click", () => {
      const targetId = button.dataset.clearTarget;
      if (!targetId) {
        return;
      }

      const targetInput = document.getElementById(targetId);
      if (!(targetInput instanceof HTMLInputElement)) {
        return;
      }

      targetInput.value = "";

      if (targetInput === fromInput && fromUtcInput) {
        fromUtcInput.value = "";
      }

      if (targetInput === toInput && toUtcInput) {
        toUtcInput.value = "";
      }

      targetInput.dispatchEvent(new Event("input", { bubbles: true }));
      targetInput.dispatchEvent(new Event("change", { bubbles: true }));
      targetInput.focus();
    });
  });

  initializeCopyButtons(document);

  if (useLocalCheckbox && uploadInput) {
    const toggleUploadState = () => {
      uploadInput.disabled = useLocalCheckbox.checked;
    };

    toggleUploadState();
    useLocalCheckbox.addEventListener("change", toggleUploadState);
  }

  const dateTimeFormatter = new Intl.DateTimeFormat(undefined, {
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit"
  });

  document.querySelectorAll(".utc-display").forEach((element) => {
    formatUtcElement(element, dateTimeFormatter);
  });

  const renderTimelineEntry = (entry) => {
    const article = document.createElement("article");
    article.className = "timeline-log-item";

    const preview = (entry.message || "").length > 180
      ? `${entry.message.slice(0, 180)}...`
      : (entry.message || "");

    article.innerHTML = `
      <div class="timeline-log-item__header">
        <div class="timeline-log-item__title" title="${escapeHtml(entry.message || "")}">${escapeHtml(preview)}</div>
        <span class="severity-pill severity-pill--timeline">${escapeHtml(entry.severity || "")}</span>
      </div>
      <div class="timeline-log-item__meta">
        <span class="utc-display" data-utc="${escapeHtml(entry.timestamp || "")}">${escapeHtml(entry.timestamp || "")}</span>
        <span>${escapeHtml(entry.service || "")}</span>
        <span>${escapeHtml(entry.fileName || "")}:${escapeHtml(String(entry.lineNumber ?? ""))}</span>
        ${(entry.correlationId || "").length > 0 ? `<span>Corr: ${escapeHtml(entry.correlationId)}</span>` : ""}
      </div>
      <details class="details-block">
        <summary>Show entire message</summary>
        <div class="details-content-wrap">
          <button type="button" class="copy-button" data-copy-from-details="true">Copy full message</button>
          <div class="details-content">${escapeHtml(entry.rawLine || "")}</div>
        </div>
      </details>`;

    article.querySelectorAll(".utc-display").forEach((element) => {
      formatUtcElement(element, dateTimeFormatter);
    });
    initializeCopyButtons(article);
    return article;
  };

  const renderRepeatedLogEntry = (entry) => {
    const article = document.createElement("article");
    article.className = "repeated-log-item";

    article.innerHTML = `
      <div class="repeated-log-item__header">
        <div class="repeated-log-item__title">${escapeHtml(entry.signature || "")}</div>
        <span class="pill-number">${escapeHtml(String(entry.count ?? ""))}</span>
      </div>
      <div class="repeated-log-item__meta">
        <span>Service: ${escapeHtml(entry.service || "")}</span>
        <span>Severity: ${escapeHtml(entry.severity || "")}</span>
      </div>
      <details class="details-block">
        <summary>Show entire message</summary>
        <div class="details-content-wrap">
          <button type="button" class="copy-button" data-copy-from-details="true">Copy full message</button>
          <div class="details-content">${escapeHtml(entry.exampleMessage || "")}</div>
        </div>
      </details>`;

    initializeCopyButtons(article);
    return article;
  };

  const renderHarApiEntry = (api, selectedRequestKey) => {
    const button = document.createElement("button");
    const methodClass = (api.method || "get").toLowerCase();
    const statusClass = Number(api.statusCode || 0) >= 400 ? "error" : "ok";
    button.type = "button";
    button.className = `har-api-item ${selectedRequestKey === (api.key || "") ? "har-api-item--active" : ""}`;
    button.dataset.harSelect = "true";
    button.dataset.requestKey = api.key || "";
    button.innerHTML = `
      <div class="har-api-item__top">
        <span class="har-method har-method--${escapeHtml(methodClass)}">${escapeHtml(api.method || "")}</span>
        <span class="har-status har-status--${statusClass}">${escapeHtml(String(api.statusCode ?? "-"))}</span>
        <span class="har-duration">${escapeHtml(String(api.durationMs ?? 0))} ms</span>
      </div>
      <div class="har-api-item__path">${escapeHtml(api.displayPath || "")}</div>
      <div class="har-api-item__meta">
        <span>${escapeHtml(api.contentType || "")}</span>
        <span>${escapeHtml(api.category || "")}</span>
        ${api.isLoadDashboard ? '<span class="service-pill">LoadDashboard</span>' : ""}
      </div>`;
    return button;
  };

  if (repeatedLogList && repeatedLogScroll && repeatedLoadTrigger && repeatedLoadingState && form) {
    const antiForgeryTokenInput = form.querySelector('input[name="__RequestVerificationToken"]');
    const repeatedServiceFilter = document.getElementById("repeatedServiceFilter");
    let repeatedLoading = false;
    let repeatedResetLoading = false;

    const updateRepeatedLoadState = (hasMore) => {
      repeatedLogList.dataset.hasMore = hasMore ? "true" : "false";
      repeatedLoadTrigger.classList.toggle("d-none", !hasMore);
      repeatedLoadingState.classList.toggle("d-none", !hasMore && !repeatedResetLoading);
      repeatedLoadingState.textContent = hasMore
        ? "Scroll down to load more repeated logs..."
        : "All repeated logs are loaded.";
    };

    const fetchRepeatedLogEntries = async (skip, reset) => {
      if (repeatedLoading || (repeatedLogList.dataset.hasMore !== "true" && !reset)) {
        return;
      }

      const analysisSessionId = repeatedLogList.dataset.analysisSessionId || "";
      const loadedCount = reset ? 0 : Number.parseInt(repeatedLogList.dataset.loadedCount || "0", 10);
      if (!analysisSessionId) {
        updateRepeatedLoadState(false);
        return;
      }

      repeatedLoading = true;
      repeatedResetLoading = reset;
      repeatedLoadingState.classList.remove("d-none");
      repeatedLoadingState.textContent = reset ? "Refreshing repeated logs..." : "Loading more repeated logs...";
      if (repeatedSkeletonState) {
        repeatedSkeletonState.classList.toggle("d-none", !reset);
      }
      if (reset) {
        repeatedLogList.innerHTML = "";
      }

      const requestBody = new URLSearchParams();
      requestBody.set("AnalysisSessionId", analysisSessionId);
      requestBody.set("Skip", String(loadedCount));
      requestBody.set("Service", repeatedServiceFilter instanceof HTMLSelectElement ? repeatedServiceFilter.value : "");
      if (antiForgeryTokenInput instanceof HTMLInputElement) {
        requestBody.set("__RequestVerificationToken", antiForgeryTokenInput.value);
      }

      try {
        const response = await fetch("/Home/RepeatedLogEntries", {
          method: "POST",
          headers: {
            "Content-Type": "application/x-www-form-urlencoded; charset=UTF-8"
          },
          body: requestBody.toString()
        });

        if (!response.ok) {
          throw new Error(`Repeated logs request failed with status ${response.status}`);
        }

        const payload = await response.json();
        const entries = Array.isArray(payload.entries) ? payload.entries : [];
        entries.forEach((entry) => {
          repeatedLogList.appendChild(renderRepeatedLogEntry(entry));
        });

        const returnedCount = Number.parseInt(String(payload.returnedCount || entries.length), 10);
        const nextLoadedCount = loadedCount + Math.max(returnedCount, entries.length);
        repeatedLogList.dataset.loadedCount = String(nextLoadedCount);
        repeatedLogList.dataset.totalCount = String(payload.totalCount || repeatedLogList.dataset.totalCount || "0");
        updateRepeatedLoadState(Boolean(payload.hasMore));

        if (reset && entries.length === 0) {
          repeatedLoadingState.textContent = "No repeated logs are available for the selected service.";
          repeatedLoadingState.classList.remove("d-none");
        }
      } catch {
        repeatedLoadingState.textContent = "Unable to load more repeated logs right now.";
        repeatedLoadingState.classList.remove("d-none");
      } finally {
        if (repeatedSkeletonState) {
          repeatedSkeletonState.classList.add("d-none");
        }
        repeatedLoading = false;
        repeatedResetLoading = false;
      }
    };

    updateRepeatedLoadState(repeatedLogList.dataset.hasMore === "true");

    const repeatedObserver = new IntersectionObserver((entries) => {
      if (entries.some((entry) => entry.isIntersecting)) {
        fetchRepeatedLogEntries(Number.parseInt(repeatedLogList.dataset.loadedCount || "0", 10), false);
      }
    }, {
      root: repeatedLogScroll,
      rootMargin: "200px 0px 200px 0px"
    });

    repeatedObserver.observe(repeatedLoadTrigger);

    repeatedQueryControls.forEach((control) => {
      control.addEventListener("change", () => {
        fetchRepeatedLogEntries(0, true);
      });
    });
  }

  if (timelineLogList && timelineLogScroll && timelineLoadTrigger && timelineLoadingState && form) {
    const antiForgeryTokenInput = form.querySelector('input[name="__RequestVerificationToken"]');
    const timelineServiceFilter = document.getElementById("timelineServiceFilter");
    const timelineSortSelect = document.querySelector('select[name="Filter.TimelineSortOrder"]');
    let timelineLoading = false;
    let timelineResetLoading = false;

    const updateTimelineLoadState = (hasMore) => {
      timelineLogList.dataset.hasMore = hasMore ? "true" : "false";
      timelineLoadTrigger.classList.toggle("d-none", !hasMore);
      timelineLoadingState.classList.toggle("d-none", !hasMore && !timelineResetLoading);
      timelineLoadingState.textContent = hasMore
        ? "Scroll down to load more timeline logs..."
        : "All timeline logs are loaded.";
    };

    const fetchTimelineEntries = async (skip, reset) => {
      if (timelineLoading || timelineLogList.dataset.hasMore !== "true") {
        if (!reset) {
          return;
        }
      }

      const analysisSessionId = timelineLogList.dataset.analysisSessionId || "";
      const loadedCount = reset ? 0 : Number.parseInt(timelineLogList.dataset.loadedCount || "0", 10);
      if (!analysisSessionId) {
        updateTimelineLoadState(false);
        return;
      }

      timelineLoading = true;
      timelineResetLoading = reset;
      timelineLoadingState.classList.remove("d-none");
      timelineLoadingState.textContent = reset ? "Refreshing timeline logs..." : "Loading more timeline logs...";
      if (timelineSkeletonState) {
        timelineSkeletonState.classList.toggle("d-none", !reset);
      }
      if (reset) {
        timelineLogList.innerHTML = "";
      }

      const requestBody = new URLSearchParams();
      requestBody.set("AnalysisSessionId", analysisSessionId);
      requestBody.set("Skip", String(loadedCount));
      requestBody.set("TimelineService", timelineServiceFilter instanceof HTMLSelectElement ? timelineServiceFilter.value : "");
      requestBody.set("TimelineSortOrder", timelineSortSelect instanceof HTMLSelectElement ? timelineSortSelect.value : "desc");
      if (antiForgeryTokenInput instanceof HTMLInputElement) {
        requestBody.set("__RequestVerificationToken", antiForgeryTokenInput.value);
      }

      try {
        const response = await fetch("/Home/TimelineEntries", {
          method: "POST",
          headers: {
            "Content-Type": "application/x-www-form-urlencoded; charset=UTF-8"
          },
          body: requestBody.toString()
        });

        if (!response.ok) {
          throw new Error(`Timeline request failed with status ${response.status}`);
        }

        const payload = await response.json();
        const entries = Array.isArray(payload.entries) ? payload.entries : [];
        entries.forEach((entry) => {
          timelineLogList.appendChild(renderTimelineEntry(entry));
        });

        const returnedCount = Number.parseInt(String(payload.returnedCount || entries.length), 10);
        const nextLoadedCount = loadedCount + Math.max(returnedCount, entries.length);
        timelineLogList.dataset.loadedCount = String(nextLoadedCount);
        timelineLogList.dataset.totalCount = String(payload.totalCount || timelineLogList.dataset.totalCount || "0");
        updateTimelineLoadState(Boolean(payload.hasMore));

        if (reset && entries.length === 0) {
          timelineLoadingState.textContent = "No timeline log lines are available for the selected filters.";
          timelineLoadingState.classList.remove("d-none");
        }
      } catch {
        timelineLoadingState.textContent = "Unable to load more timeline logs right now.";
        timelineLoadingState.classList.remove("d-none");
      } finally {
        if (timelineSkeletonState) {
          timelineSkeletonState.classList.add("d-none");
        }
        timelineLoading = false;
        timelineResetLoading = false;
      }
    };

    updateTimelineLoadState(timelineLogList.dataset.hasMore === "true");

    const timelineObserver = new IntersectionObserver((entries) => {
      if (entries.some((entry) => entry.isIntersecting)) {
        fetchTimelineEntries(Number.parseInt(timelineLogList.dataset.loadedCount || "0", 10), false);
      }
    }, {
      root: timelineLogScroll,
      rootMargin: "200px 0px 200px 0px"
    });

    timelineObserver.observe(timelineLoadTrigger);

    timelineQueryControls.forEach((control) => {
      control.addEventListener("change", () => {
        fetchTimelineEntries(0, true);
      });
    });
  }

  const applyDetailFilter = (groupName, selectedService) => {
    const group = document.querySelector(`.filterable-group[data-group="${groupName}"]`);
    if (!group) {
      return;
    }

    const items = group.querySelectorAll(".filterable-item");
    let visibleCount = 0;

    items.forEach((item) => {
      const services = (item.dataset.services || "")
        .split(",")
        .map((value) => value.trim().toLowerCase())
        .filter((value) => value.length > 0);
      const shouldShow = selectedService === "all" || services.includes(selectedService.toLowerCase());
      item.classList.toggle("d-none", !shouldShow);
      if (shouldShow) {
        visibleCount += 1;
      }
    });

    let emptyState = group.parentElement.querySelector(".filtered-empty-state");
    if (visibleCount === 0) {
      if (!emptyState) {
        emptyState = document.createElement("div");
        emptyState.className = "empty-state filtered-empty-state";
        emptyState.textContent = "No items matched the selected service in this section.";
        group.after(emptyState);
      }
    } else if (emptyState) {
      emptyState.remove();
    }
  };

  detailFilters.forEach((filter) => {
    applyDetailFilter(filter.dataset.targetGroup, filter.value);
    filter.addEventListener("change", () => {
      applyDetailFilter(filter.dataset.targetGroup, filter.value);
    });
  });

  initializeHarTabs(document);
  initializeJsonNodeControls(document);

  const getActiveHarTab = () => {
    if (!harRequestDetailsRoot) {
      return "request-headers";
    }

    const activeButton = harRequestDetailsRoot.querySelector("[data-har-tab-target].har-tab-button--active");
    return activeButton instanceof HTMLElement
      ? activeButton.dataset.harTabTarget || "request-headers"
      : "request-headers";
  };

  const applyHarTabSelection = (target) => {
    if (!harRequestDetailsRoot || !target) {
      return;
    }

    const tabButton = harRequestDetailsRoot.querySelector(`[data-har-tab-target="${target}"]`);
    if (tabButton instanceof HTMLButtonElement) {
      tabButton.click();
    }
  };

  const updateHarSelectedRequestKey = () => {
    if (!harRequestDetailsRoot || !selectedRequestKeyInput) {
      return;
    }

    const selectedKeyField = harRequestDetailsRoot.querySelector("[data-har-selected-request-key]");
    if (selectedKeyField instanceof HTMLInputElement) {
      selectedRequestKeyInput.value = selectedKeyField.value;
    }
  };

  const setHarActiveButton = (requestKey) => {
    document.querySelectorAll("[data-har-select]").forEach((button) => {
      if (!(button instanceof HTMLElement)) {
        return;
      }

      button.classList.toggle("har-api-item--active", (button.dataset.requestKey || "") === requestKey);
    });
  };

  const cacheCurrentHarDetails = () => {
    if (!harRequestDetailsRoot) {
      return;
    }

    const selectedKeyField = harRequestDetailsRoot.querySelector("[data-har-selected-request-key]");
    const populatedField = harRequestDetailsRoot.querySelector("[data-har-details-populated]");
    const hasPopulatedDetails = populatedField instanceof HTMLInputElement
      && populatedField.value === "true";

    if (!(selectedKeyField instanceof HTMLInputElement) || !selectedKeyField.value || !hasPopulatedDetails) {
      return;
    }

    harRequestCache.set(selectedKeyField.value, harRequestDetailsRoot.innerHTML);
  };

  const renderHarDetailsHtml = (html, preferredTab) => {
    if (!harRequestDetailsRoot) {
      return;
    }

    harRequestDetailsRoot.innerHTML = html;
    initializeHarTabs(harRequestDetailsRoot);
    initializeJsonNodeControls(harRequestDetailsRoot);
    if (preferredTab) {
      applyHarTabSelection(preferredTab);
    }
    updateHarSelectedRequestKey();
    cacheCurrentHarDetails();
  };

  cacheCurrentHarDetails();
  updateHarSelectedRequestKey();

  const loadHarRequestDetails = async (requestKey, preferredTab) => {
    if (!form || !selectedRequestKeyInput || !harRequestDetailsRoot) {
      return;
    }

    if (!requestKey) {
      return;
    }

    const currentSelectedKey = selectedRequestKeyInput.value;
    if (currentSelectedKey === requestKey && harRequestCache.has(requestKey)) {
      setHarActiveButton(requestKey);
      return;
    }

    const activeTab = preferredTab || getActiveHarTab();
    setHarActiveButton(requestKey);

    if (harRequestCache.has(requestKey)) {
      renderHarDetailsHtml(harRequestCache.get(requestKey), activeTab);
      selectedRequestKeyInput.value = requestKey;
      return;
    }

    const antiForgeryTokenInput = form.querySelector('input[name="__RequestVerificationToken"]');
    if (!(antiForgeryTokenInput instanceof HTMLInputElement)) {
      return;
    }

    const endpoint = harRequestDetailsRoot.dataset.harDetailsEndpoint || form.getAttribute("action") || "";
    if (!endpoint) {
      return;
    }

    harSelectionTriggered = true;

    if (analyzeStatusText) {
      analyzeStatusText.classList.remove("d-none");
      analyzeStatusText.textContent = "Loading the selected API request details...";
    }

    if (overlay) {
      if (loadingTitle) {
        loadingTitle.textContent = "Loading API request details...";
      }

      if (loadingDescription) {
        loadingDescription.textContent = "The app is reading the selected HAR request, parsing payload and response JSON, and preparing the detail panels. Please wait.";
      }

      overlay.classList.remove("d-none");
      overlay.setAttribute("aria-hidden", "false");
    }

    try {
      const response = await fetch(endpoint, {
        method: "POST",
        headers: {
          "Content-Type": "application/x-www-form-urlencoded; charset=UTF-8"
        },
        body: new URLSearchParams({
          __RequestVerificationToken: antiForgeryTokenInput.value,
          requestKey
        })
      });

      if (!response.ok) {
        throw new Error(`HAR request details failed with status ${response.status}`);
      }

      const html = await response.text();
      harRequestCache.set(requestKey, html);
      selectedRequestKeyInput.value = requestKey;
      renderHarDetailsHtml(html, activeTab);
    } catch (error) {
      console.error(error);
      if (analyzeStatusText) {
        analyzeStatusText.classList.remove("d-none");
        analyzeStatusText.textContent = "Could not load the selected API details. Please try again.";
      }
    } finally {
      harSelectionTriggered = false;
      if (overlay) {
        overlay.classList.add("d-none");
        overlay.setAttribute("aria-hidden", "true");
      }
    }
  };

  document.addEventListener("click", (event) => {
    const trigger = event.target instanceof HTMLElement ? event.target.closest("[data-har-select]") : null;
    if (!(trigger instanceof HTMLButtonElement)) {
      return;
    }

    loadHarRequestDetails(trigger.dataset.requestKey || "", getActiveHarTab());
  });

  if (selectedRequestKeyInput && selectedRequestKeyInput.value && !harRequestCache.has(selectedRequestKeyInput.value)) {
    window.setTimeout(() => {
      loadHarRequestDetails(selectedRequestKeyInput.value, "request-headers");
    }, 60);
  }

  if (harApiList && harApiLoadTrigger && harApiLoadingState && form) {
    const antiForgeryTokenInput = form.querySelector('input[name="__RequestVerificationToken"]');
    let harApiLoading = false;

    const updateHarApiLoadState = (hasMore) => {
      harApiList.dataset.hasMore = hasMore ? "true" : "false";
      harApiLoadTrigger.classList.toggle("d-none", !hasMore);
      harApiLoadingState.classList.toggle("d-none", !hasMore);
      if (hasMore) {
        harApiLoadingState.textContent = "Scroll down to load more API requests...";
      }
    };

    const loadMoreHarApis = async () => {
      if (harApiLoading || harApiList.dataset.hasMore !== "true") {
        return;
      }

      if (!(antiForgeryTokenInput instanceof HTMLInputElement)) {
        return;
      }

      const analysisSessionId = harApiList.dataset.analysisSessionId || "";
      const loadedCount = Number.parseInt(harApiList.dataset.loadedCount || "0", 10);
      if (!analysisSessionId) {
        updateHarApiLoadState(false);
        return;
      }

      harApiLoading = true;
      harApiLoadingState.classList.remove("d-none");
      harApiLoadingState.textContent = "Loading more API requests...";
      if (harApiSkeletonState) {
        harApiSkeletonState.classList.remove("d-none");
      }

      const requestBody = new URLSearchParams();
      requestBody.set("AnalysisSessionId", analysisSessionId);
      requestBody.set("Skip", String(loadedCount));
      requestBody.set("__RequestVerificationToken", antiForgeryTokenInput.value);

      try {
        const response = await fetch("/Home/HarApiEntries", {
          method: "POST",
          headers: {
            "Content-Type": "application/x-www-form-urlencoded; charset=UTF-8"
          },
          body: requestBody.toString()
        });

        if (!response.ok) {
          throw new Error(`HAR API request failed with status ${response.status}`);
        }

        const payload = await response.json();
        const entries = Array.isArray(payload.entries) ? payload.entries : [];
        const selectedRequestKey = selectedRequestKeyInput ? selectedRequestKeyInput.value : "";
        entries.forEach((api) => {
          harApiList.appendChild(renderHarApiEntry(api, selectedRequestKey));
        });

        const returnedCount = Number.parseInt(String(payload.returnedCount || entries.length), 10);
        const nextLoadedCount = loadedCount + Math.max(returnedCount, entries.length);
        harApiList.dataset.loadedCount = String(nextLoadedCount);
        harApiList.dataset.totalCount = String(payload.totalCount || harApiList.dataset.totalCount || "0");
        updateHarApiLoadState(Boolean(payload.hasMore));

        if (!payload.hasMore && entries.length === 0) {
          harApiLoadingState.textContent = "All API requests are loaded.";
          harApiLoadingState.classList.remove("d-none");
        }
      } catch (error) {
        console.error(error);
        harApiLoadingState.textContent = "Unable to load more API requests right now.";
        harApiLoadingState.classList.remove("d-none");
      } finally {
        harApiLoading = false;
        if (harApiSkeletonState) {
          harApiSkeletonState.classList.add("d-none");
        }
      }
    };

    updateHarApiLoadState(harApiList.dataset.hasMore === "true");

    const harApiObserver = new IntersectionObserver((entries) => {
      if (entries.some((entry) => entry.isIntersecting)) {
        loadMoreHarApis();
      }
    }, {
      root: harApiList,
      rootMargin: "200px 0px 200px 0px"
    });

    harApiObserver.observe(harApiLoadTrigger);
  }
});
