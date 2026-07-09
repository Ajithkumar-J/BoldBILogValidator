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
  const valueInput = node.querySelector("[data-json-filter-value]");
  const nodeType = normalizeJsonSearchText(node.dataset.jsonType || "");
  const originalPreview = previewElement.dataset.jsonOriginalPreview || previewElement.textContent || "";
  const hasActiveFilter = propertySelect instanceof HTMLSelectElement
    && valueInput instanceof HTMLInputElement
    && propertySelect.value
    && valueInput.value.trim().length > 0;

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
  const valueInput = node.querySelector("[data-json-filter-value]");
  const childrenContainer = getJsonNodeChildrenContainer(node);

  if (!(propertySelect instanceof HTMLSelectElement) ||
      !(operatorSelect instanceof HTMLSelectElement) ||
      !(valueInput instanceof HTMLInputElement) ||
      !childrenContainer) {
    return;
  }

  const propertyPath = propertySelect.value;
  const operator = operatorSelect.value || "equals";
  const filterValue = valueInput.value;

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
    const valueInput = node.querySelector("[data-json-filter-value]");
    const clearButton = node.querySelector("[data-json-search-clear]");

    populateJsonPropertyOptions(node);

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
      applyJsonObjectFilter(node);
    });

    operatorSelect?.addEventListener("change", () => {
      applyJsonObjectFilter(node);
    });

    valueInput?.addEventListener("input", () => {
      applyJsonObjectFilter(node);
    });

    clearButton?.addEventListener("click", () => {
      if (!(propertySelect instanceof HTMLSelectElement) ||
          !(operatorSelect instanceof HTMLSelectElement) ||
          !(valueInput instanceof HTMLInputElement)) {
        return;
      }

      propertySelect.value = "";
      operatorSelect.value = "equals";
      valueInput.value = "";
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
  const form = document.querySelector("form");
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
  const clearButtons = document.querySelectorAll(".input-clear-button");
  const harSelectButtons = document.querySelectorAll("[data-har-select]");
  const harRequestDetailsRoot = document.getElementById("harRequestDetailsRoot");
  let harSelectionTriggered = false;
  const harRequestCache = new Map();
  const isHarValidationPage = !!document.getElementById("harValidationFile") || !!harRequestDetailsRoot;

  if (browserTimeZoneInput) {
    browserTimeZoneInput.value = Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";
  }

  if (form) {
    form.addEventListener("submit", () => {
      if (fromUtcInput) {
        fromUtcInput.value = toUtcIso(fromInput ? fromInput.value : "");
      }

      if (toUtcInput) {
        toUtcInput.value = toUtcIso(toInput ? toInput.value : "");
      }

      const isUploadSubmit = (uploadInput && uploadInput.files && uploadInput.files.length > 0)
        || (harInput && harInput.files && harInput.files.length > 0);

      if (submitButton) {
        submitButton.disabled = true;
        submitButton.textContent = isUploadSubmit ? "Uploading..." : "Analyzing...";
      }

      if (analyzeStatusText) {
        analyzeStatusText.classList.remove("d-none");
        if (harSelectionTriggered) {
          analyzeStatusText.textContent = "Loading the selected API request details...";
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

      if (overlay && (isUploadSubmit || harSelectionTriggered)) {
        if (loadingTitle) {
          loadingTitle.textContent = harSelectionTriggered
            ? "Loading API request details..."
            : isHarValidationPage
              ? "Uploading and analyzing HAR..."
              : "Uploading and analyzing logs...";
        }

        if (loadingDescription) {
          if (harSelectionTriggered) {
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
    harSelectButtons.forEach((button) => {
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
    if (!(selectedKeyField instanceof HTMLInputElement) || !selectedKeyField.value) {
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

  harSelectButtons.forEach((button) => {
    button.addEventListener("click", async () => {
      if (!form || !(button instanceof HTMLButtonElement) || !selectedRequestKeyInput || !harRequestDetailsRoot) {
        return;
      }

      const requestKey = button.dataset.requestKey || "";
      if (!requestKey) {
        return;
      }

      const currentSelectedKey = selectedRequestKeyInput.value;
      if (currentSelectedKey === requestKey && harRequestCache.has(requestKey)) {
        setHarActiveButton(requestKey);
        return;
      }

      const activeTab = getActiveHarTab();
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
    });
  });
});
