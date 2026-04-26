import {
  EVENT_TYPES,
  addEvent,
  createEvent,
  getEventType,
  getEventsForDate,
  removeEvent,
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
      <span class="event-description">${eventType.description}</span>
    `;

    eventButtons.appendChild(button);
  });
}

function renderEvents({ eventList, emptyState, todayLabel, countLabel }) {
  const today = new Date();
  const events = getEventsForDate(window.localStorage, today);

  todayLabel.textContent = dateFormatter.format(today);
  countLabel.textContent =
    events.length === 1 ? "1 event logged" : `${events.length} events logged`;
  emptyState.hidden = events.length > 0;
  eventList.replaceChildren();

  events.forEach((event) => {
    const eventType = getEventType(event.type);
    if (!eventType) return;

    const item = document.createElement("li");
    item.className = "event-list-item";
    item.innerHTML = `
      <span class="event-list-emoji" aria-hidden="true">${eventType.emoji}</span>
      <span>
        <span class="event-list-text">${eventType.label}</span>
        <time class="event-list-time" datetime="${event.occurredAt}">
          ${timeFormatter.format(new Date(event.occurredAt))}
        </time>
      </span>
      <button
        class="delete-event"
        type="button"
        data-remove-event="${event.id}"
        aria-label="Remove ${eventType.label} event"
      >
        Undo
      </button>
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

    addEvent(window.localStorage, createEvent(button.dataset.eventType));
    renderState();
  });

  eventList.addEventListener("click", (event) => {
    const button = event.target.closest("[data-remove-event]");
    if (!button) return;

    removeEvent(window.localStorage, button.dataset.removeEvent);
    renderState();
  });

  renderButtons(eventButtons);
  renderState();
}
