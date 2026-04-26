import {
  EVENT_TYPES,
  addEventWithDefaults,
  formatCompactTimeValue,
  formatTimeInputValue,
  getPuppyAgeLabel,
  getEventType,
  getEventsForDate,
  getTodayKey,
  getTrackingDay,
  removeEvent,
  updateEventsTime,
} from "./events.js";

const dateFormatter = new Intl.DateTimeFormat(undefined, {
  month: "short",
  day: "numeric",
});

const SETTINGS_KEY = "pawprints-settings";

const defaultSettings = {
  arrivalDate: getTodayKey(),
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
  const day = getTrackingDay(settings.arrivalDate, dateKey);
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
  const eventButtons = document.querySelector("[data-event-buttons]");
  const eventList = document.querySelector("[data-event-list]");
  const emptyState = document.querySelector("[data-empty-state]");
  const logDateInput = document.querySelector("[data-log-date]");
  const arrivalDateInput = document.querySelector("[data-arrival-date]");
  const birthDateInput = document.querySelector("[data-birth-date]");
  const logSummary = document.querySelector("[data-log-summary]");
  const addStatus = document.querySelector("[data-add-status]");
  const settings = loadSettings();
  const todayKey = getTodayKey();

  let selectedDateKey = todayKey;

  logDateInput.value = selectedDateKey;
  logDateInput.max = todayKey;
  arrivalDateInput.value = settings.arrivalDate;
  arrivalDateInput.max = todayKey;
  birthDateInput.value = settings.birthDate;
  birthDateInput.max = todayKey;

  const renderState = () => {
    const selectedDate = createLogDate(selectedDateKey);

    renderEvents({
      eventList,
      emptyState,
      logSummary,
      date: selectedDate,
      dateKey: selectedDateKey,
      settings,
    });
  };

  eventButtons.addEventListener("click", (event) => {
    const button = event.target.closest("[data-event-type]");
    if (!button) return;

    const addedEvents = addEventWithDefaults(
      window.localStorage,
      button.dataset.eventType,
      new Date(),
      createLogDate(selectedDateKey),
    );

    addStatus.textContent =
      addedEvents.length === 0 ? "Choose today or an earlier log day." : "";
    renderState();
  });

  eventList.addEventListener("click", (event) => {
    const button = event.target.closest("[data-remove-event]");
    if (!button) return;

    removeEvent(window.localStorage, button.dataset.removeEvent, createLogDate(selectedDateKey));
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
    renderState();
  });

  logDateInput.addEventListener("change", () => {
    selectedDateKey = logDateInput.value || todayKey;
    addStatus.textContent = "";
    renderState();
  });

  arrivalDateInput.addEventListener("change", () => {
    settings.arrivalDate = arrivalDateInput.value || todayKey;
    saveSettings(settings);
    renderState();
  });

  birthDateInput.addEventListener("change", () => {
    settings.birthDate = birthDateInput.value;
    saveSettings(settings);
    renderState();
  });

  renderButtons(eventButtons);
  renderState();
}
