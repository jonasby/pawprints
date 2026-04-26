import {
  EVENT_TYPES,
  addEventWithDefaults,
  formatCompactTimeValue,
  getPuppyAgeLabel,
  getEventType,
  getEventsForDate,
  getStoredEvents,
  getTodayKey,
  getTrackingDay,
  removeEvent,
  replaceStoredEvents,
  updateEventsTime,
} from "./events.js";
import { redirectToCanonicalHost } from "./hosting.js";
import { createApiUrl, createRemoteSync } from "./sync.js";

const dateFormatter = new Intl.DateTimeFormat(undefined, {
  month: "short",
  day: "numeric",
});

const SETTINGS_KEY = "pawprints-settings";

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

export function renderPuppyLog() {
  if (redirectToCanonicalHost()) {
    return;
  }

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
  const syncStatus = document.querySelector("[data-sync-status]");
  const appSections = document.querySelectorAll("[data-app-section]");
  const loginPanel = document.querySelector("[data-login-panel]");
  const authUrlElements = document.querySelectorAll("[data-auth-url]");
  const logoutForm = document.querySelector("[data-auth-url='/api/auth/logout']");
  const settings = loadSettings();
  const todayKey = getTodayKey();
  let isSignedIn = false;
  const remoteSync = createRemoteSync(window.localStorage, {
    getSettings: () => settings,
    loadEvents: getStoredEvents,
    onStatusChange(status) {
      syncStatus.textContent = status;
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

    loginPanel.hidden = isSignedIn;
    appSections.forEach((section) => {
      section.toggleAttribute("hidden", !isSignedIn);
    });
    if (!isSignedIn) {
      return;
    }

    setupPanel.open = !isSetupComplete;
    setupPanel.classList.toggle("is-required", !isSetupComplete);
    setupStatus.textContent = isSetupComplete ? "Edit" : "Required";
    logDateInput.min = settings.arrivalDate || "";
    activitySections.forEach((section) => {
      section.toggleAttribute("hidden", !isSetupComplete);
    });
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
      syncStatus.textContent = "Signed out";
      renderState();
    });
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

  renderButtons(eventButtons);
  remoteSync.getCurrentUser().then(async (user) => {
    isSignedIn = Boolean(user);
    if (isSignedIn) {
      const snapshot = await remoteSync.loadSnapshot();
      if (snapshot) {
        applyRemoteSettings(settings, snapshot.settings);
        applyStoredEventsSnapshot(window.localStorage, snapshot.events);
      }
    }
    renderState();
  });
  renderState();
}
