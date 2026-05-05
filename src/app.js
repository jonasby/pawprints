import {
  EVENT_TYPES,
  addEventWithDefaults,
  applyStoredEventsSnapshot,
  applyStoredEventsSnapshotToWindow,
  clampDateKey,
  clampOccurredAtForLogDay,
  formatCompactTimeValue,
  formatEventTime,
  getEventGroupsDescending,
  getEventsForDate,
  getPendingSyncChanges,
  getResolvedEventType,
  getPuppyAgeLabel,
  getStoredEvents,
  getTodayKey,
  getTrackingDay,
  insertEventPreservingOrder,
  markSyncCommitted,
  pickSnappedTimeMsBetweenNeighbors,
  removeEvent,
  shiftDateKey,
  updateEventsTime,
} from "./events.js";
import {
  applyImportPreview,
  buildImportPreview,
  fetchAiImportHints,
  mergeAiHintsIntoPreview,
} from "./import.js";
import { createApiUrl, createRemoteSync } from "./sync.js";

const dateFormatter = new Intl.DateTimeFormat(undefined, {
  month: "short",
  day: "numeric",
});

const SETTINGS_KEY = "pawprints-settings";
const HAS_SIGNED_IN_KEY = "pawprints-has-signed-in";
const AUTH_RETURN_QUERY_KEY = "authReturn";

const defaultSettings = {
  arrivalDate: "",
  birthDate: "",
  customEventTypes: [],
};

function loadSettings() {
  try {
    const parsed = JSON.parse(window.localStorage.getItem(SETTINGS_KEY));
    const customEventTypes = Array.isArray(parsed?.customEventTypes) ? parsed.customEventTypes : [];

    return {
      ...defaultSettings,
      ...parsed,
      customEventTypes,
    };
  } catch {
    return { ...defaultSettings };
  }
}

function saveSettings(settings) {
  window.localStorage.setItem(SETTINGS_KEY, JSON.stringify(settings));
}

function hasSignedInBefore() {
  return window.localStorage.getItem(HAS_SIGNED_IN_KEY) === "1";
}

function markSignedIn() {
  window.localStorage.setItem(HAS_SIGNED_IN_KEY, "1");
}

function clearSignedInMarker() {
  window.localStorage.removeItem(HAS_SIGNED_IN_KEY);
}

function readInviteTokenFromLocation() {
  try {
    return new URLSearchParams(window.location.search).get("invite");
  } catch {
    return null;
  }
}

function hasAuthReturnFlagInLocation() {
  try {
    return new URLSearchParams(window.location.search).get(AUTH_RETURN_QUERY_KEY) === "1";
  } catch {
    return false;
  }
}

function createLoginReturnUrl() {
  try {
    const url = new URL(window.location.href);
    url.searchParams.set(AUTH_RETURN_QUERY_KEY, "1");
    return url.toString();
  } catch {
    return window.location.href;
  }
}

function stripQueryParam(paramName) {
  try {
    const url = new URL(window.location.href);
    if (!url.searchParams.has(paramName)) {
      return;
    }

    url.searchParams.delete(paramName);
    const search = url.searchParams.toString();
    window.history.replaceState({}, "", `${url.pathname}${search ? `?${search}` : ""}${url.hash}`);
  } catch {
    // ignore malformed URLs
  }
}

function applyRemoteSettings(settings, remoteSettings) {
  settings.arrivalDate = remoteSettings.arrivalDate ?? "";
  settings.birthDate = remoteSettings.birthDate ?? "";
  saveSettings(settings);
}

function renderButtons(eventButtons) {
  eventButtons.replaceChildren();

  EVENT_TYPES.forEach((eventType) => {
    const button = document.createElement("button");
    button.className = "event-button";
    button.type = "button";
    button.dataset.eventType = eventType.id;
    button.setAttribute("aria-label", `Record ${eventType.label}`);
    button.innerHTML = `
      <span class="event-emoji" aria-hidden="true">${eventType.emoji}</span>
      <span class="event-label">${eventType.label}</span>
    `;

    eventButtons.appendChild(button);
  });
}

function createLogDate(dateKey) {
  const [year, month, day] = dateKey.split("-").map(Number);
  return new Date(year, month - 1, day);
}

function hasRequiredSettings(settings) {
  return Boolean(settings.arrivalDate && settings.birthDate);
}

function getLogSummary({ date, dateKey, settings, events }) {
  const day = settings.arrivalDate ? getTrackingDay(settings.arrivalDate, dateKey) : 1;
  const ageLabel = getPuppyAgeLabel(settings.birthDate, dateKey);
  const pieces = [`Day ${day}`, dateFormatter.format(date)];

  if (ageLabel) {
    pieces.push(ageLabel);
  }

  pieces.push(events.length === 1 ? "1 event" : `${events.length} events`);

  return pieces.join(" · ");
}

function renderEvents({ eventList, emptyState, logSummary, date, dateKey, settings }) {
  const events = getEventsForDate(window.localStorage, date);
  const eventGroups = getEventGroupsDescending(events);

  logSummary.textContent = getLogSummary({ date, dateKey, settings, events });
  emptyState.hidden = events.length > 0;
  eventList.replaceChildren();

  eventGroups.forEach((eventGroup) => {
    const knownEvents = eventGroup.events.map((event) => ({
      event,
      eventType: getResolvedEventType(event.type, settings),
    }));

    if (knownEvents.length === 0) return;

    const item = document.createElement("li");
    item.className = "event-list-item";
    const eventIds = knownEvents.map(({ event }) => event.id).join(",");
    const eventNames = knownEvents.map(({ eventType }) => eventType.label).join(" + ");

    item.innerHTML = `
      <button
        type="button"
        class="event-drag-handle"
        data-drag-handle
        aria-label="Drag to reorder ${eventNames}"
      >
        ⋮
      </button>
      <label class="event-time-control">
        <span class="visually-hidden">Time for ${eventNames}</span>
        <input
          class="event-time-input"
          type="text"
          inputmode="numeric"
          pattern="[0-9]{4}"
          maxlength="4"
          value="${formatCompactTimeValue(eventGroup.occurredAt)}"
          data-event-time="${eventIds}"
          aria-label="Adjust ${eventNames} time"
        />
      </label>
      <span class="event-stack" aria-label="${eventNames}">
        ${knownEvents
          .map(
            ({ event, eventType }) => `
              <button
                class="event-chip"
                type="button"
                data-remove-event="${event.id}"
                aria-label="Remove ${eventType.label} event"
              >
                <span class="event-chip-emoji" aria-hidden="true">${eventType.emoji}</span>
                <span class="event-chip-label">${eventType.label}</span>
              </button>
            `,
          )
          .join("")}
      </span>
    `;

    eventList.appendChild(item);
  });
}

function formatBootstrapError(error) {
  const lines = [
    `Time: ${new Date().toISOString()}`,
    `Message: ${error?.message ?? "Unknown bootstrap error"}`,
  ];

  if (error?.phase) {
    lines.push(`Auth phase: ${error.phase}`);
  }

  if (error?.path) {
    lines.push(`API path: ${error.path}`);
  }

  if (typeof error?.status === "number") {
    lines.push(`HTTP status: ${error.status}`);
  }

  if (error?.responseText) {
    lines.push("");
    lines.push("Response body:");
    lines.push(error.responseText);
  }

  return lines.join("\n");
}

function createSessionOutcomeError(session) {
  if (session?.error) {
    const baseError = session.error;
    if (!baseError.phase) {
      baseError.phase = "session-check";
    }
    return baseError;
  }

  if (session?.reason === "unauthorized") {
    const status = typeof session.status === "number" ? session.status : 401;
    const error = new Error("No active signed-in session found.");
    error.status = status;
    error.path = session.path ?? "/api/auth/me";
    error.phase = "session-check";
    return error;
  }

  const error = new Error("Session validation returned an unexpected result.");
  error.path = session?.path ?? "/api/auth/me";
  error.phase = "session-check";
  return error;
}

function isExpiredSessionOutcome(session) {
  if (!session) {
    return false;
  }

  if (session.reason === "unauthorized") {
    return true;
  }

  return session.status === 401 || session.status === 403;
}

function paintImportPreviewOut(container, preview, errors) {
  if (!container) {
    return;
  }

  container.replaceChildren();
  const hasRows = Boolean(preview?.previewRows?.length);
  const hasErrors = Boolean(errors?.length);
  container.hidden = !hasRows && !hasErrors;

  if (hasErrors) {
    const list = document.createElement("ul");
    list.className = "import-errors";
    for (const message of errors) {
      const item = document.createElement("li");
      item.textContent = message;
      list.appendChild(item);
    }
    container.appendChild(list);
  }

  if (hasRows) {
    const table = document.createElement("table");
    table.className = "import-preview-table";
    const headRow = document.createElement("tr");
    for (const label of ["Time", "Line", "Matched"]) {
      const th = document.createElement("th");
      th.textContent = label;
      headRow.appendChild(th);
    }
    const thead = document.createElement("thead");
    thead.appendChild(headRow);
    table.appendChild(thead);
    const tbody = document.createElement("tbody");
    for (const row of preview.previewRows) {
      const tr = document.createElement("tr");
      const timeCell = document.createElement("td");
      timeCell.textContent = `${String(row.hours).padStart(2, "0")}${String(row.minutes).padStart(2, "0")}`;
      const lineCell = document.createElement("td");
      lineCell.textContent = row.rawLine;
      const matchCell = document.createElement("td");
      matchCell.textContent = row.tokenPreviews
        .map((tp) => `${tp.raw} → ${tp.resolution.typeId} (${tp.resolution.source})`)
        .join("; ");
      tr.append(timeCell, lineCell, matchCell);
      tbody.appendChild(tr);
    }
    table.appendChild(tbody);
    container.appendChild(table);
  }
}

function formatPredictionTime(value) {
  if (!value) {
    return "";
  }

  return formatEventTime(value);
}

function getPredictionLabel(type) {
  if (type === "nap_wake") {
    return "Nap wake";
  }

  if (type === "poop_need") {
    return "Poo";
  }

  return "Prediction";
}

function renderPredictions({ predictionList, predictions }) {
  if (!predictionList) {
    return;
  }

  predictionList.replaceChildren();
  if (!predictions.length) {
    const item = document.createElement("li");
    item.className = "prediction-list-item is-empty";
    item.textContent = "No active predictions yet.";
    predictionList.appendChild(item);
    return;
  }

  predictions.forEach((prediction) => {
    const item = document.createElement("li");
    item.className = "prediction-list-item";
    const label = getPredictionLabel(prediction.type);
    const windowStart = formatPredictionTime(prediction.windowStart);
    const bestGuess = formatPredictionTime(prediction.bestGuessAt);
    const windowEnd = formatPredictionTime(prediction.windowEnd);
    const confidence = Number.isFinite(Number(prediction.confidence))
      ? `${Math.round(Number(prediction.confidence) * 100)}%`
      : "";

    item.innerHTML = `
      <span class="prediction-type">${label}</span>
      <span class="prediction-window">${windowStart}-${windowEnd}</span>
      <span class="prediction-detail">${bestGuess ? `Best guess ${bestGuess}` : ""}${confidence ? ` · ${confidence}` : ""}</span>
    `;
    predictionList.appendChild(item);
  });
}

async function notifyUser(notification) {
  if (!("Notification" in window)) {
    return false;
  }

  if (Notification.permission === "default") {
    await Notification.requestPermission();
  }

  if (Notification.permission !== "granted") {
    return false;
  }

  new Notification(notification.title, {
    body: notification.body,
    tag: `pawprints-${notification.id}`,
  });
  return true;
}

export function renderPuppyLog() {
  const eventButtons = document.querySelector("[data-event-buttons]");
  const eventList = document.querySelector("[data-event-list]");
  const emptyState = document.querySelector("[data-empty-state]");
  const activitySections = document.querySelectorAll("[data-activity-section]");
  const setupPanel = document.querySelector("[data-setup-panel]");
  const setupStatus = document.querySelector("[data-setup-status]");
  const logDateInput = document.querySelector("[data-log-date]");
  const arrivalDateInput = document.querySelector("[data-arrival-date]");
  const birthDateInput = document.querySelector("[data-birth-date]");
  const logSummary = document.querySelector("[data-log-summary]");
  const previousDayButton = document.querySelector("[data-previous-day]");
  const nextDayButton = document.querySelector("[data-next-day]");
  const addStatus = document.querySelector("[data-add-status]");
  const syncStatus = document.querySelector("[data-login-status]");
  const appSections = document.querySelectorAll("[data-app-section]");
  const loginPanel = document.querySelector("[data-login-panel]");
  const bootPanel = document.querySelector("[data-boot-panel]");
  const bootStatus = document.querySelector("[data-boot-status]");
  const bootHelp = document.querySelector("[data-boot-help]");
  const bootErrorDetails = document.querySelector("[data-boot-error-details]");
  const bootRetryButton = document.querySelector("[data-boot-retry]");
  const bootLoginLink = document.querySelector("[data-boot-login]");
  const authUrlElements = document.querySelectorAll("[data-auth-url]");
  const logoutForm = document.querySelector("[data-auth-url='/api/auth/logout']");
  const shareCollaboratorNote = document.querySelector("[data-share-collaborator]");
  const shareOwnerTools = document.querySelector("[data-share-owner]");
  const createInviteButton = document.querySelector("[data-create-invite]");
  const shareInviteBlock = document.querySelector("[data-share-invite-block]");
  const inviteUrlInput = document.querySelector("[data-invite-url]");
  const inviteExpiryLabel = document.querySelector("[data-invite-expiry]");
  const whatsappInviteLink = document.querySelector("[data-whatsapp-invite]");
  const copyInviteButton = document.querySelector("[data-copy-invite]");
  const shareStatus = document.querySelector("[data-share-status]");
  const importSection = document.querySelector("[data-import-section]");
  const importText = document.querySelector("[data-import-text]");
  const importPreviewButton = document.querySelector("[data-import-preview]");
  const importAiButton = document.querySelector("[data-import-ai]");
  const importApplyButton = document.querySelector("[data-import-apply]");
  const importPreviewOut = document.querySelector("[data-import-preview-out]");
  const importStatus = document.querySelector("[data-import-status]");
  const undoButton = document.querySelector("[data-undo]");
  const predictionSection = document.querySelector("[data-predictions-section]");
  const predictionList = document.querySelector("[data-prediction-list]");
  const predictionStatus = document.querySelector("[data-prediction-status]");
  const settings = loadSettings();
  const todayKey = getTodayKey();
  let isSignedIn = false;
  let isBootstrapping = false;
  let isCheckingSession = false;
  let bootstrapError = null;
  let accountProfile = null;
  let activePredictions = [];
  let notificationPollId = 0;
  if (syncStatus) {
    syncStatus.classList.add("sync-status");
  }

  const remoteSync = createRemoteSync(window.localStorage, {
    getSettings: () => settings,
    loadEvents: getStoredEvents,
    getPendingChanges: getPendingSyncChanges,
    markChangesCommitted: markSyncCommitted,
    onStatusChange(status) {
      if (syncStatus) syncStatus.textContent = status;
    },
    onInFlightChange(isInFlight) {
      document.body.classList.toggle("is-syncing", isInFlight);
      if (syncStatus) {
        syncStatus.classList.toggle("is-in-flight", isInFlight);
      }
    },
    onSyncComplete() {
      loadAndRenderPredictions();
    },
  });

  authUrlElements.forEach((element) => {
    const path = element.dataset.authUrl;
    if (!path) return;

    const url = new URL(createApiUrl(path), window.location.href);
    if (path === "/api/auth/login") {
      url.searchParams.set("returnUrl", createLoginReturnUrl());
    }

    if (element.tagName === "FORM") {
      element.action = url.toString();
      return;
    }

    element.href = url.toString();
  });

  let selectedDateKey = todayKey;
  let cachedRemoteSnapshotEvents = [];
  let loadedEventWindowMinKey = "";
  let loadedEventWindowMaxKey = "";
  const undoStack = [];
  let importPreview = null;

  const TEN_MINUTES_MS = 10 * 60 * 1000;
  const NOTIFICATION_POLL_MS = 5 * 60 * 1000;

  function materializeRemoteEventsWindow(windowMinKey, windowMaxKey) {
    loadedEventWindowMinKey = windowMinKey;
    loadedEventWindowMaxKey = windowMaxKey;
    applyStoredEventsSnapshotToWindow(window.localStorage, cachedRemoteSnapshotEvents, windowMinKey, windowMaxKey);
  }

  function ensureEventBufferForSelectedDay() {
    if (!settings.arrivalDate) {
      return;
    }

    const arrival = settings.arrivalDate;
    const needMin = clampDateKey(shiftDateKey(selectedDateKey, -1), arrival, todayKey);
    const needMax = clampDateKey(shiftDateKey(selectedDateKey, 1), arrival, todayKey);

    if (loadedEventWindowMinKey === "") {
      const spanMin = clampDateKey(shiftDateKey(todayKey, -2), arrival, todayKey);
      const spanMax = clampDateKey(shiftDateKey(todayKey, 2), arrival, todayKey);
      const nextMin = clampDateKey(Math.min(spanMin, needMin), arrival, todayKey);
      const nextMax = clampDateKey(Math.max(spanMax, needMax), arrival, todayKey);

      materializeRemoteEventsWindow(nextMin, nextMax);
      return;
    }

    let nextMin = needMin < loadedEventWindowMinKey ? needMin : loadedEventWindowMinKey;
    let nextMax = needMax > loadedEventWindowMaxKey ? needMax : loadedEventWindowMaxKey;
    nextMin = clampDateKey(nextMin, arrival, todayKey);
    nextMax = clampDateKey(nextMax, arrival, todayKey);

    if (nextMin === loadedEventWindowMinKey && nextMax === loadedEventWindowMaxKey) {
      return;
    }

    materializeRemoteEventsWindow(nextMin, nextMax);
  }

  function pushUndoEntry(entry) {
    undoStack.push(entry);
    if (undoStack.length > 25) {
      undoStack.shift();
    }
    if (undoButton) {
      undoButton.disabled = false;
    }
  }

  function refreshUndoControl() {
    if (undoButton) {
      undoButton.disabled = undoStack.length === 0;
    }
  }

  async function loadAndRenderPredictions() {
    if (!isSignedIn) {
      activePredictions = [];
      renderState();
      return;
    }

    try {
      activePredictions = await remoteSync.loadPredictions();
      if (predictionStatus) {
        predictionStatus.textContent = activePredictions.length
          ? "Notifications watch these windows."
          : "Predictions appear after enough matching history.";
      }
      renderState();
    } catch (error) {
      console.error(error);
      if (predictionStatus) {
        predictionStatus.textContent = "Could not load predictions.";
      }
    }
  }

  async function pollDueNotifications() {
    if (!isSignedIn) {
      return;
    }

    try {
      const notifications = await remoteSync.claimDueNotifications();
      for (const notification of notifications) {
        const displayed = await notifyUser(notification);
        if (!displayed && predictionStatus) {
          predictionStatus.textContent = `${notification.title}: ${notification.body}`;
        }
      }
      if (notifications.length > 0) {
        await loadAndRenderPredictions();
      }
    } catch (error) {
      console.error(error);
    }
  }

  function startNotificationPolling() {
    window.clearInterval(notificationPollId);
    pollDueNotifications();
    notificationPollId = window.setInterval(pollDueNotifications, NOTIFICATION_POLL_MS);
  }

  function stopNotificationPolling() {
    window.clearInterval(notificationPollId);
    notificationPollId = 0;
  }

  logDateInput.value = selectedDateKey;
  logDateInput.max = todayKey;
  arrivalDateInput.value = settings.arrivalDate;
  arrivalDateInput.max = todayKey;
  birthDateInput.value = settings.birthDate;
  birthDateInput.max = todayKey;

  const renderState = () => {
    const selectedDate = createLogDate(selectedDateKey);
    const isSetupComplete = hasRequiredSettings(settings);
    const showBootPanel = isBootstrapping || Boolean(bootstrapError);

    bootPanel.hidden = !showBootPanel;
    bootStatus.textContent = isBootstrapping
      ? "Signing in and loading your synced snapshot..."
      : "PawPrints could not load your account snapshot.";
    bootHelp.hidden = !bootstrapError;
    bootHelp.textContent = bootstrapError
      ? "Sign-in or snapshot loading did not complete. Review the details, then sign in again and retry."
      : "";
    bootErrorDetails.hidden = !bootstrapError;
    bootErrorDetails.textContent = bootstrapError ? formatBootstrapError(bootstrapError) : "";
    bootRetryButton.hidden = !bootstrapError;
    if (bootLoginLink) {
      bootLoginLink.hidden = !bootstrapError;
    }

    if (syncStatus) {
      syncStatus.classList.toggle("is-loading", isCheckingSession || isBootstrapping);
    }

    loginPanel.hidden = isSignedIn || showBootPanel;
    appSections.forEach((section) => {
      section.toggleAttribute("hidden", !isSignedIn || showBootPanel);
    });
    if (!isSignedIn || showBootPanel) {
      return;
    }

    setupPanel.open = !isSetupComplete;
    setupPanel.classList.toggle("is-required", !isSetupComplete);
    setupStatus.textContent = isSetupComplete ? "Edit" : "Required";
    logDateInput.min = settings.arrivalDate || "";
    activitySections.forEach((section) => {
      section.toggleAttribute("hidden", !isSetupComplete);
    });

    if (importSection) {
      importSection.toggleAttribute("hidden", !isSetupComplete);
    }

    if (predictionSection) {
      predictionSection.toggleAttribute("hidden", !isSetupComplete);
    }

    if (importAiButton) {
      importAiButton.hidden = !isSignedIn || !isSetupComplete;
    }

    const collaborationRole = accountProfile?.collaboration?.role ?? "owner";
    if (shareCollaboratorNote && shareOwnerTools) {
      if (collaborationRole === "collaborator") {
        shareCollaboratorNote.hidden = false;
        const ownerEmail = accountProfile?.collaboration?.ownerEmail;
        shareCollaboratorNote.textContent = ownerEmail
          ? `You're viewing a shared puppy log (${ownerEmail}).`
          : "You're viewing a shared puppy log.";
        shareOwnerTools.hidden = true;
      } else {
        shareCollaboratorNote.hidden = true;
        shareOwnerTools.hidden = false;
      }
    }
    addStatus.textContent = isSetupComplete ? addStatus.textContent : "Add arrival and birth dates first.";
    previousDayButton.disabled = !settings.arrivalDate || selectedDateKey <= settings.arrivalDate;
    nextDayButton.disabled = selectedDateKey >= todayKey;

    if (isSetupComplete && settings.arrivalDate) {
      ensureEventBufferForSelectedDay();
    }

    refreshUndoControl();

    renderEvents({
      eventList,
      emptyState,
      logSummary,
      date: selectedDate,
      dateKey: selectedDateKey,
      settings,
    });
    renderPredictions({ predictionList, predictions: activePredictions });
  };

  logoutForm.addEventListener("submit", (event) => {
    event.preventDefault();
    remoteSync.signOut().then(() => {
      isSignedIn = false;
      accountProfile = null;
      bootstrapError = null;
      stopNotificationPolling();
      activePredictions = [];
      cachedRemoteSnapshotEvents = [];
      loadedEventWindowMinKey = "";
      loadedEventWindowMaxKey = "";
      activePredictions = [];
      stopNotificationPolling();
      undoStack.length = 0;
      if (undoButton) {
        undoButton.disabled = true;
      }
      clearSignedInMarker();
      if (syncStatus) syncStatus.textContent = "";
      if (shareInviteBlock) shareInviteBlock.hidden = true;
      if (shareStatus) shareStatus.textContent = "";
      if (inviteUrlInput) inviteUrlInput.value = "";
      renderState();
    });
  });

  createInviteButton?.addEventListener("click", async () => {
    if (!createInviteButton || !shareInviteBlock || !inviteUrlInput || !whatsappInviteLink || !shareStatus) {
      return;
    }

    shareStatus.textContent = "";

    try {
      const body = await remoteSync.createInvite();
      const inviteParam = encodeURIComponent(body.token);
      const shareUrl = `${window.location.origin}${window.location.pathname}?invite=${inviteParam}`;
      inviteUrlInput.value = shareUrl;
      if (inviteExpiryLabel) {
        inviteExpiryLabel.textContent = body.expiresAt
          ? `Expires ${new Date(body.expiresAt).toLocaleString()}`
          : "";
      }

      whatsappInviteLink.href = `https://wa.me/?text=${encodeURIComponent(`Join our puppy log on PawPrints: ${shareUrl}`)}`;
      shareInviteBlock.hidden = false;
    } catch (error) {
      console.error(error);
      shareStatus.textContent = error?.message ?? "Could not create invite";
    }
  });

  copyInviteButton?.addEventListener("click", async () => {
    if (!inviteUrlInput || !shareStatus) {
      return;
    }

    try {
      await navigator.clipboard.writeText(inviteUrlInput.value);
      shareStatus.textContent = "Copied";
    } catch (error) {
      console.error(error);
      shareStatus.textContent = "Could not copy";
    }
  });

  const saveAndSync = () => {
    saveSettings(settings);
    remoteSync.schedule();
  };

  eventButtons.addEventListener("click", (event) => {
    const button = event.target.closest("[data-event-type]");
    if (!button) return;

    if (!hasRequiredSettings(settings)) {
      addStatus.textContent = "Add arrival and birth dates first.";
      renderState();
      return;
    }

    const addedEvents = addEventWithDefaults(
      window.localStorage,
      button.dataset.eventType,
      new Date(),
      createLogDate(selectedDateKey),
    );

    addStatus.textContent =
      addedEvents.length === 0 ? "Choose today or an earlier log day." : "";
    if (addedEvents.length > 0) {
      pushUndoEntry({
        type: "add",
        dateKey: selectedDateKey,
        eventIds: addedEvents.map((item) => item.id),
      });
    }
    remoteSync.schedule();
    renderState();
  });

  eventList.addEventListener("click", (event) => {
    const button = event.target.closest("[data-remove-event]");
    if (!button) return;

    const logDate = createLogDate(selectedDateKey);
    const removed = getEventsForDate(window.localStorage, logDate).find(
      (item) => item.id === button.dataset.removeEvent,
    );

    removeEvent(window.localStorage, button.dataset.removeEvent, logDate);

    if (removed) {
      pushUndoEntry({ type: "remove", event: removed });
    }

    remoteSync.schedule();
    renderState();
  });

  eventList.addEventListener("change", (event) => {
    const input = event.target.closest("[data-event-time]");
    if (!input) return;

    updateEventsTime(
      window.localStorage,
      input.dataset.eventTime.split(","),
      input.value,
      createLogDate(selectedDateKey),
    );
    remoteSync.schedule();
    renderState();
  });

  logDateInput.addEventListener("change", () => {
    selectedDateKey = logDateInput.value || todayKey;
    if (selectedDateKey > todayKey) {
      selectedDateKey = todayKey;
      logDateInput.value = selectedDateKey;
    }
    if (settings.arrivalDate && selectedDateKey < settings.arrivalDate) {
      selectedDateKey = settings.arrivalDate;
      logDateInput.value = selectedDateKey;
    }
    addStatus.textContent = "";
    renderState();
  });

  undoButton?.addEventListener("click", () => {
    const entry = undoStack.pop();
    if (!entry) {
      return;
    }

    if (entry.type === "remove") {
      insertEventPreservingOrder(window.localStorage, entry.event);
    } else if (entry.type === "add") {
      entry.eventIds.forEach((eventId) => {
        removeEvent(window.localStorage, eventId, createLogDate(entry.dateKey));
      });
    }

    refreshUndoControl();
    remoteSync.schedule();
    renderState();
  });

  let listDrag = null;

  function pointerToInsertIndex(clientY, listElement, skipElement) {
    const children = [...listElement.querySelectorAll(":scope > .event-list-item")];
    let index = 0;

    for (const child of children) {
      if (child === skipElement) {
        continue;
      }

      const rect = child.getBoundingClientRect();
      if (clientY < rect.top + rect.height / 2) {
        return index;
      }

      index += 1;
    }

    return index;
  }

  eventList.addEventListener("pointerdown", (event) => {
    const handle = event.target.closest("[data-drag-handle]");
    if (!handle || !hasRequiredSettings(settings)) {
      return;
    }

    const item = handle.closest(".event-list-item");
    if (!item || !eventList.contains(item)) {
      return;
    }

    event.preventDefault();
    handle.setPointerCapture(event.pointerId);
    const items = [...eventList.querySelectorAll(":scope > .event-list-item")];
    const fromIndex = items.indexOf(item);

    if (fromIndex === -1) {
      return;
    }

    listDrag = {
      pointerId: event.pointerId,
      handle,
      item,
      fromIndex,
    };
    item.classList.add("is-dragging");
  });

  eventList.addEventListener("pointermove", (event) => {
    if (!listDrag || event.pointerId !== listDrag.pointerId) {
      return;
    }

    event.preventDefault();
  });

  eventList.addEventListener("pointerup", (event) => {
    if (!listDrag || event.pointerId !== listDrag.pointerId) {
      return;
    }

    const { handle, item, fromIndex } = listDrag;
    item.classList.remove("is-dragging");

    try {
      handle.releasePointerCapture(event.pointerId);
    } catch {
      // ignore release errors
    }

    listDrag = null;

    const insertAt = pointerToInsertIndex(event.clientY, eventList, item);
    const logDate = createLogDate(selectedDateKey);
    const groups = getEventGroupsDescending(getEventsForDate(window.localStorage, logDate));

    if (groups.length < 2 || fromIndex === insertAt) {
      return;
    }

    const reordered = [...groups];
    const [moved] = reordered.splice(fromIndex, 1);
    reordered.splice(insertAt, 0, moved);

    const chrono = [...reordered].reverse();
    const movedAt = moved.occurredAt;
    const mi = chrono.findIndex((group) => group.occurredAt === movedAt);

    if (mi === -1) {
      return;
    }

    const prevMs = mi > 0 ? new Date(chrono[mi - 1].occurredAt).getTime() : null;
    const nextMs =
      mi < chrono.length - 1 ? new Date(chrono[mi + 1].occurredAt).getTime() : null;

    let hintMs = new Date(moved.occurredAt).getTime();
    if (prevMs != null && nextMs != null) {
      hintMs = (prevMs + nextMs) / 2;
    }

    const snapMs = pickSnappedTimeMsBetweenNeighbors(prevMs, nextMs, hintMs);
    let adjusted = clampOccurredAtForLogDay(new Date(snapMs), logDate, new Date());

    if (prevMs != null && adjusted.getTime() <= prevMs) {
      adjusted = clampOccurredAtForLogDay(new Date(prevMs + TEN_MINUTES_MS), logDate, new Date());
    }

    if (nextMs != null && adjusted.getTime() >= nextMs) {
      adjusted = clampOccurredAtForLogDay(new Date(nextMs - TEN_MINUTES_MS), logDate, new Date());
    }

    const hh = String(adjusted.getHours()).padStart(2, "0");
    const mm = String(adjusted.getMinutes()).padStart(2, "0");

    updateEventsTime(
      window.localStorage,
      moved.events.map((ev) => ev.id),
      `${hh}${mm}`,
      logDate,
    );
    remoteSync.schedule();
    renderState();
  });

  eventList.addEventListener("pointercancel", (event) => {
    if (!listDrag || event.pointerId !== listDrag.pointerId) {
      return;
    }

    listDrag.item.classList.remove("is-dragging");

    try {
      listDrag.handle.releasePointerCapture(event.pointerId);
    } catch {
      // ignore release errors
    }

    listDrag = null;
  });

  arrivalDateInput.addEventListener("change", () => {
    settings.arrivalDate = arrivalDateInput.value;
    if (settings.arrivalDate && selectedDateKey < settings.arrivalDate) {
      selectedDateKey = settings.arrivalDate;
      logDateInput.value = selectedDateKey;
    }
    saveAndSync();
    addStatus.textContent = "";
    renderState();
  });

  birthDateInput.addEventListener("change", () => {
    settings.birthDate = birthDateInput.value;
    saveAndSync();
    addStatus.textContent = "";
    renderState();
  });

  previousDayButton.addEventListener("click", () => {
    selectedDateKey = shiftDateKey(selectedDateKey, -1);
    if (settings.arrivalDate && selectedDateKey < settings.arrivalDate) {
      selectedDateKey = settings.arrivalDate;
    }
    logDateInput.value = selectedDateKey;
    addStatus.textContent = "";
    renderState();
  });

  nextDayButton.addEventListener("click", () => {
    selectedDateKey = shiftDateKey(selectedDateKey, 1);
    if (selectedDateKey > todayKey) {
      selectedDateKey = todayKey;
    }
    logDateInput.value = selectedDateKey;
    addStatus.textContent = "";
    renderState();
  });

  importPreviewButton?.addEventListener("click", () => {
    if (importStatus) importStatus.textContent = "";
    importPreview = buildImportPreview(settings, importText?.value ?? "");
    paintImportPreviewOut(importPreviewOut, importPreview, importPreview.errors);
    const ready = Boolean(importPreview.previewRows?.length);
    if (importApplyButton) importApplyButton.disabled = !ready;
    if (importStatus) {
      importStatus.textContent = ready
        ? `${importPreview.previewRows.length} row(s) ready.`
        : importPreview.errors?.length
          ? "Fix paste or parse errors."
          : "Nothing to import.";
    }
  });

  importAiButton?.addEventListener("click", async () => {
    if (!importPreview?.previewRows?.length) {
      if (importStatus) importStatus.textContent = "Preview first.";
      return;
    }

    if (importStatus) importStatus.textContent = "Refining with AI…";
    const tokens = importPreview.previewRows.flatMap((row) =>
      row.tokenPreviews.map((tp) => tp.raw),
    );
    const response = await fetchAiImportHints(tokens, settings);
    if (!response?.matches?.length) {
      if (importStatus) {
        if (response?.aiAvailable === false) {
          importStatus.textContent = "AI matching is not configured on the server.";
        } else {
          importStatus.textContent = "AI refine returned no suggestions.";
        }
      }
      return;
    }

    mergeAiHintsIntoPreview(importPreview, response.matches, settings);
    saveSettings(settings);
    paintImportPreviewOut(importPreviewOut, importPreview, importPreview.errors);
    if (importStatus) importStatus.textContent = "AI hints applied.";
    if (importApplyButton) importApplyButton.disabled = false;
  });

  importApplyButton?.addEventListener("click", () => {
    if (!importPreview?.previewRows?.length) {
      if (importStatus) importStatus.textContent = "Preview first.";
      return;
    }

    const count = applyImportPreview(window.localStorage, settings, selectedDateKey, importPreview);
    saveSettings(settings);
    remoteSync.schedule();
    if (importStatus) importStatus.textContent = `Imported ${count} events for ${selectedDateKey}.`;
    importPreview = null;
    paintImportPreviewOut(importPreviewOut, null, []);
    if (importApplyButton) importApplyButton.disabled = true;
    renderState();
  });

  const bootstrapSignedInState = async () => {
    bootstrapError = null;

    try {
      const inviteToken = readInviteTokenFromLocation();
      const authReturnAttempted = hasAuthReturnFlagInLocation();
      const hadSignedInBefore = hasSignedInBefore();
      const shouldCheckSession = Boolean(inviteToken || authReturnAttempted || hadSignedInBefore);

      if (!shouldCheckSession) {
        isSignedIn = false;
        accountProfile = null;
        if (syncStatus) {
          syncStatus.textContent = "Sign in to sync your puppy log.";
        }
        renderState();
        return;
      }

      isCheckingSession = true;
      if (syncStatus) {
        syncStatus.textContent = "Checking your session...";
      }
      renderState();

      const session = await remoteSync.checkSession();
      if (!session.isActive) {
        isSignedIn = false;
        accountProfile = null;
        if (authReturnAttempted || inviteToken) {
          bootstrapError = createSessionOutcomeError(session);
          if (syncStatus) {
            syncStatus.textContent = "Sign-in did not complete. See details below.";
          }
        } else if (hadSignedInBefore) {
          if (isExpiredSessionOutcome(session)) {
            if (syncStatus) {
              syncStatus.textContent = "Session expired. Please sign in again.";
            }
          } else {
            bootstrapError = createSessionOutcomeError(session);
            if (syncStatus) {
              syncStatus.textContent = "We could not verify your session. See details below.";
            }
          }
        } else if (syncStatus) {
          syncStatus.textContent = "Sign in to sync your puppy log.";
        }
        renderState();
        return;
      }

      accountProfile = session.user;
      isSignedIn = true;
      markSignedIn();
      if (authReturnAttempted) {
        stripQueryParam(AUTH_RETURN_QUERY_KEY);
      }

      if (inviteToken) {
        try {
          await remoteSync.acceptInvite(inviteToken);
          stripQueryParam("invite");
          stripQueryParam(AUTH_RETURN_QUERY_KEY);
          accountProfile = await remoteSync.getCurrentUser({ silent: true });
        } catch (inviteError) {
          console.error(inviteError);
          if (!inviteError.phase) {
            inviteError.phase = "invite-accept";
          }
          bootstrapError = inviteError;
          renderState();
          return;
        }
      }

      isBootstrapping = true;
      renderState();

      try {
        const snapshot = await remoteSync.loadSnapshot();
        if (snapshot) {
          applyRemoteSettings(settings, snapshot.settings);
          arrivalDateInput.value = settings.arrivalDate;
          birthDateInput.value = settings.birthDate;
          cachedRemoteSnapshotEvents = snapshot.events ?? [];

          if (settings.arrivalDate) {
            const initialMin = clampDateKey(shiftDateKey(todayKey, -2), settings.arrivalDate, todayKey);
            const initialMax = clampDateKey(shiftDateKey(todayKey, 2), settings.arrivalDate, todayKey);
            materializeRemoteEventsWindow(initialMin, initialMax);
          } else {
            applyStoredEventsSnapshot(window.localStorage, cachedRemoteSnapshotEvents);
            loadedEventWindowMinKey = "";
            loadedEventWindowMaxKey = "";
          }
        }

        await loadAndRenderPredictions();
      } catch (snapshotError) {
        console.error(snapshotError);
        if (!snapshotError.phase) {
          snapshotError.phase = "snapshot-load";
        }
        bootstrapError = snapshotError;
      }

      if (!bootstrapError) {
        startNotificationPolling();
      }
    } catch (error) {
      console.error(error);
      if (!error.phase) {
        error.phase = "bootstrap";
      }
      bootstrapError = error;
      isSignedIn = false;
    } finally {
      isCheckingSession = false;
      isBootstrapping = false;
      renderState();
    }
  };

  bootRetryButton.addEventListener("click", () => {
    bootstrapSignedInState();
  });

  renderButtons(eventButtons);
  bootstrapSignedInState();
  renderState();
}
