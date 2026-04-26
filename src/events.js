export const EVENT_TYPES = [
  { id: "pee", label: "Wee", emoji: "💧" },
  { id: "poop", label: "Poop", emoji: "💩" },
  { id: "eat", label: "Eat", emoji: "🍽️" },
  { id: "nap", label: "Nap", emoji: "😴" },
  { id: "sleep", label: "Sleep", emoji: "🌙" },
  { id: "wake", label: "Wake", emoji: "☀️" },
];

const STORAGE_PREFIX = "puppy-events";
const TEN_MINUTES_IN_MS = 10 * 60 * 1000;
const ONE_HOUR_IN_MS = 60 * 60 * 1000;
const SLEEPING_EVENT_TYPES = new Set(["nap", "sleep"]);
const AWAKE_EVENT_TYPES = new Set(["pee", "poop", "eat"]);

export function getTodayKey(date = new Date()) {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");

  return `${year}-${month}-${day}`;
}

export function getEventType(eventTypeId) {
  return EVENT_TYPES.find((eventType) => eventType.id === eventTypeId);
}

export function getStorageKey(dateKey) {
  return `${STORAGE_PREFIX}:${dateKey}`;
}

export function formatEventTime(timestamp) {
  return new Intl.DateTimeFormat(undefined, {
    hour: "numeric",
    minute: "2-digit",
  }).format(new Date(timestamp));
}

export function formatTimeInputValue(timestamp) {
  const date = new Date(timestamp);
  const hours = String(date.getHours()).padStart(2, "0");
  const minutes = String(date.getMinutes()).padStart(2, "0");

  return `${hours}:${minutes}`;
}

export function roundToNearestTenMinutes(date = new Date()) {
  return new Date(Math.round(date.getTime() / TEN_MINUTES_IN_MS) * TEN_MINUTES_IN_MS);
}

export function getSuggestedEventTime(eventTypeId, events = [], now = new Date()) {
  const latestEvent = events[0];

  if (latestEvent && SLEEPING_EVENT_TYPES.has(latestEvent.type)) {
    return new Date(new Date(latestEvent.occurredAt).getTime() + ONE_HOUR_IN_MS);
  }

  return roundToNearestTenMinutes(now);
}

function createEventId(eventTypeId, occurredAt) {
  const randomId = globalThis.crypto?.randomUUID?.() ?? Math.random().toString(36).slice(2);

  return `${occurredAt.getTime()}-${eventTypeId}-${randomId}`;
}

export function createEvent(eventTypeId, occurredAt = new Date()) {
  const eventType = getEventType(eventTypeId);

  if (!eventType) {
    throw new Error(`Unknown puppy event type: ${eventTypeId}`);
  }

  return {
    id: createEventId(eventTypeId, occurredAt),
    type: eventTypeId,
    occurredAt: occurredAt.toISOString(),
    dateKey: getTodayKey(occurredAt),
  };
}

export function loadEventsForDate(storage, dateKey = getTodayKey()) {
  const rawEvents = storage.getItem(getStorageKey(dateKey));

  if (!rawEvents) {
    return [];
  }

  try {
    const events = JSON.parse(rawEvents);
    return Array.isArray(events)
      ? events.slice().sort((first, second) => {
          return new Date(second.occurredAt) - new Date(first.occurredAt);
        })
      : [];
  } catch {
    return [];
  }
}

export function saveEventsForDate(storage, dateKey, events) {
  storage.setItem(getStorageKey(dateKey), JSON.stringify(events));
}

export function getEventsForDate(storage, date = new Date()) {
  return loadEventsForDate(storage, getTodayKey(date));
}

export function addEvent(storage, event) {
  const events = loadEventsForDate(storage, event.dateKey);

  saveEventsForDate(storage, event.dateKey, [event, ...events]);

  return event;
}

export function addEventWithDefaults(storage, eventTypeId, now = new Date()) {
  const dateKey = getTodayKey(now);
  const events = loadEventsForDate(storage, dateKey);
  const latestEvent = events[0];
  const occurredAt = getSuggestedEventTime(eventTypeId, events, now);
  const event = createEvent(eventTypeId, occurredAt);
  const targetEvents =
    event.dateKey === dateKey ? events : loadEventsForDate(storage, event.dateKey);
  const eventsToAdd = [];

  if (
    latestEvent &&
    SLEEPING_EVENT_TYPES.has(latestEvent.type) &&
    AWAKE_EVENT_TYPES.has(eventTypeId)
  ) {
    eventsToAdd.push(createEvent("wake", occurredAt));
  }

  eventsToAdd.push(event);
  saveEventsForDate(storage, event.dateKey, [...eventsToAdd, ...targetEvents]);

  return eventsToAdd;
}

export function updateEventsTime(storage, eventIds, timeValue, date = new Date()) {
  const dateKey = getTodayKey(date);
  const [hours, minutes] = timeValue.split(":").map(Number);

  if (
    !Number.isInteger(hours) ||
    !Number.isInteger(minutes) ||
    hours < 0 ||
    hours > 23 ||
    minutes < 0 ||
    minutes > 59
  ) {
    throw new Error(`Invalid event time: ${timeValue}`);
  }

  const updatedAt = new Date(date);
  updatedAt.setHours(hours, minutes, 0, 0);

  const selectedEventIds = new Set(eventIds);
  const events = loadEventsForDate(storage, dateKey).map((event) => {
    if (!selectedEventIds.has(event.id)) {
      return event;
    }

    return {
      ...event,
      occurredAt: updatedAt.toISOString(),
      dateKey,
    };
  });

  saveEventsForDate(storage, dateKey, events);

  return loadEventsForDate(storage, dateKey);
}

export function removeEvent(storage, eventId, date = new Date()) {
  const dateKey = getTodayKey(date);
  const remainingEvents = loadEventsForDate(storage, dateKey).filter((event) => {
    return event.id !== eventId;
  });

  saveEventsForDate(storage, dateKey, remainingEvents);

  return remainingEvents;
}

export function clearEventsForDate(storage, date = new Date()) {
  saveEventsForDate(storage, getTodayKey(date), []);
}
