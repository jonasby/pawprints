import {
  EVENT_TYPES,
  addEventWithDefaults,
  applyStoredEventsSnapshot,
  formatCompactTimeValue,
  getPendingSyncChanges,
  getPuppyAgeLabel,
  getEventType,
  getEventsForDate,
  markSyncCommitted,
  getStoredEvents,
  getTodayKey,
  getTrackingDay,
  removeEvent,
  updateEventsTime,
} from "./events.js";
import { createApiUrl, createRemoteSync } from "./sync.js";

const dateFormatter = new Intl.DateTimeFormat(undefined, {
  month: "short",
  day: "numeric",
});

const SETTINGS_KEY = "pawprints-settings";
const HAS_SIGNED_IN_KEY = "pawprints-has-signed-in";

const defaultSettings = {
  arrivalDate: "",
  birthDate: "",
};

function loadSettings() {
  try {
    return {
      ...defaultSettings,
      ...JSON.parse(window.localStorage.getItem(SETTINGS_KEY)),
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

function stripInviteQueryParam() {
  try {
    const url = new URL(window.location.href);
    if (!url.searchParams.has("invite")) {
      return;
    }

    url.searchParams.delete("invite");
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

function shiftDateKey(dateKey, dayOffset) {
  const date = createLogDate(dateKey);
  date.setDate(date.getDate() + dayOffset);

  return getTodayKey(date);
}

function hasRequiredSettings(settings) {
  return Boolean(settings.arrivalDate && settings.birthDate);
}

function getEventGroups(events) {
  const groups = new Map();

  events.forEach((event) => {
    if (!groups.has(event.occurredAt)) {
      groups.set(event.occurredAt, []);
    }

    groups.get(event.occurredAt).push(event);
  });

  return Array.from(groups.entries()).map(([occurredAt, groupEvents]) => ({
    occurredAt,
    events: groupEvents,
  }));
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
  const eventGroups = getEventGroups(events);

  logSummary.textContent = getLogSummary({ date, dateKey, settings, events });
  emptyState.hidden = events.length > 0;
  eventList.replaceChildren();

  eventGroups.forEach((eventGroup) => {
    const knownEvents = eventGroup.events
      .map((event) => ({ event, eventType: getEventType(event.type) }))
      .filter(({ eventType }) => Boolean(eventType));

    if (knownEvents.length === 0) return;

    const item = document.createElement("li");
    item.className = "event-list-item";
    const eventIds = knownEvents.map(({ event }) => event.id).join(",");
    const eventNames = knownEvents.map(({ eventType }) => eventType.label).join(" + ");

    item.innerHTML = `
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
  const settings = loadSettings();
  const todayKey = getTodayKey();
  let isSignedIn = false;
  let isBootstrapping = false;
  let bootstrapError = null;
  let accountProfile = null;
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
  });

  authUrlElements.forEach((element) => {
    const path = element.dataset.authUrl;
    if (!path) return;

    const url = new URL(createApiUrl(path), window.location.href);
    if (path === "/api/auth/login") {
      url.searchParams.set("returnUrl", window.location.href);
    }

    if (element.tagName === "FORM") {
      element.action = url.toString();
      return;
    }

    element.href = url.toString();
  });

  let selectedDateKey = todayKey;

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
      ? "Authentication did not complete or snapshot loading failed. Retry sign-in in a regular browser tab, then retry loading."
      : "";
    bootErrorDetails.hidden = !bootstrapError;
    bootErrorDetails.textContent = bootstrapError ? formatBootstrapError(bootstrapError) : "";
    bootRetryButton.hidden = !bootstrapError;
    if (bootLoginLink) {
      bootLoginLink.hidden = !bootstrapError;
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

    renderEvents({
      eventList,
      emptyState,
      logSummary,
      date: selectedDate,
      dateKey: selectedDateKey,
      settings,
    });
  };

  logoutForm.addEventListener("submit", (event) => {
    event.preventDefault();
    remoteSync.signOut().then(() => {
      isSignedIn = false;
      accountProfile = null;
      bootstrapError = null;
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
    remoteSync.schedule();
    renderState();
  });

  eventList.addEventListener("click", (event) => {
    const button = event.target.closest("[data-remove-event]");
    if (!button) return;

    removeEvent(window.localStorage, button.dataset.removeEvent, createLogDate(selectedDateKey));
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

  const bootstrapSignedInState = async () => {
    bootstrapError = null;

    try {
      const inviteToken = readInviteTokenFromLocation();
      const user = await remoteSync.getCurrentUser({ silent: true });
      accountProfile = user;
      isSignedIn = Boolean(user);

      if (!isSignedIn) {
        if (inviteToken) {
          bootstrapError = {
            message:
              "Invite sign-in did not complete. Please tap 'Sign in with Google' again in a regular browser tab (not in-app browser) and return to this invite link.",
            status: 401,
            path: "/api/auth/me",
          };
          if (syncStatus) {
            syncStatus.textContent = "Invite sign-in failed. Retry in a browser tab.";
          }
        } else if (hasSignedInBefore()) {
          bootstrapError = {
            message:
              "We couldn't restore your signed-in session. Sign in again to continue syncing your puppy log.",
            status: 401,
            path: "/api/auth/me",
          };
          if (syncStatus) {
            syncStatus.textContent = "Session expired. Sign in again.";
          }
        } else if (syncStatus) {
          bootstrapError = null;
          syncStatus.textContent = "";
        }
        renderState();
        return;
      }
      markSignedIn();

      if (inviteToken) {
        try {
          await remoteSync.acceptInvite(inviteToken);
          stripInviteQueryParam();
          accountProfile = await remoteSync.getCurrentUser({ silent: true });
        } catch (inviteError) {
          console.error(inviteError);
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
          applyStoredEventsSnapshot(window.localStorage, snapshot.events);
        }
      } catch (snapshotError) {
        console.error(snapshotError);
        bootstrapError = snapshotError;
      }
    } catch (error) {
      console.error(error);
      bootstrapError = error;
      isSignedIn = false;
    } finally {
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
