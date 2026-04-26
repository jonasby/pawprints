import {
  EVENT_TYPES,
  addEventWithDefaults,
  formatTimeInputValue,
  getEventType,
  getEventsForDate,
  removeEvent,
  updateEventsTime,
} from "./events.js";

const dateFormatter = new Intl.DateTimeFormat(undefined, {
  weekday: "long",
  month: "long",
  day: "numeric",
});

const timeFormatter = new Intl.DateTimeFormat(undefined, {
  hour: "numeric",
  minute: "2-digit",
});

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

function renderEvents({ eventList, emptyState, todayLabel, countLabel }) {
  const today = new Date();
  const events = getEventsForDate(window.localStorage, today);
  const eventGroups = getEventGroups(events);

  todayLabel.textContent = dateFormatter.format(today);
  countLabel.textContent =
    events.length === 1 ? "1 event logged" : `${events.length} events logged`;
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
    const eventNames = knownEvents.map(({ eventType }) => eventType.label).join(", ");

    item.innerHTML = `
      <label class="event-time-control">
        <span class="visually-hidden">Time for ${eventNames}</span>
        <input
          class="event-time-input"
          type="time"
          step="600"
          value="${formatTimeInputValue(eventGroup.occurredAt)}"
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
                <span aria-hidden="true">${eventType.emoji}</span>
              </button>
            `,
          )
          .join("")}
      </span>
      <time class="event-list-time visually-hidden" datetime="${eventGroup.occurredAt}">
        ${timeFormatter.format(new Date(eventGroup.occurredAt))}
      </time>
    `;

    eventList.appendChild(item);
  });
}

export function renderPuppyLog() {
  const eventButtons = document.querySelector("[data-event-buttons]");
  const eventList = document.querySelector("[data-event-list]");
  const emptyState = document.querySelector("[data-empty-state]");
  const todayLabel = document.querySelector("[data-today-label]");
  const countLabel = document.querySelector("[data-count-label]");

  const renderState = () => {
    renderEvents({ eventList, emptyState, todayLabel, countLabel });
  };

  eventButtons.addEventListener("click", (event) => {
    const button = event.target.closest("[data-event-type]");
    if (!button) return;

    addEventWithDefaults(window.localStorage, button.dataset.eventType);
    renderState();
  });

  eventList.addEventListener("click", (event) => {
    const button = event.target.closest("[data-remove-event]");
    if (!button) return;

    removeEvent(window.localStorage, button.dataset.removeEvent);
    renderState();
  });

  eventList.addEventListener("change", (event) => {
    const input = event.target.closest("[data-event-time]");
    if (!input) return;

    updateEventsTime(window.localStorage, input.dataset.eventTime.split(","), input.value);
    renderState();
  });

  renderButtons(eventButtons);
  renderState();
}
