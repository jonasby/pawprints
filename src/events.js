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

export function getDateKeyFromStorageKey(storageKey) {
  return storageKey.startsWith(`${STORAGE_PREFIX}:`)
    ? storageKey.slice(STORAGE_PREFIX.length + 1)
    : "";
}

export function getStoredDateKeys(storage) {
  const dateKeys = [];

  for (let index = 0; index < storage.length; index += 1) {
    const key = storage.key(index);
    if (key?.startsWith(`${STORAGE_PREFIX}:`)) {
      dateKeys.push(key.slice(STORAGE_PREFIX.length + 1));
    }
  }

  return dateKeys.sort();
}

export function createLocalDate(dateKey) {
  const [year, month, day] = dateKey.split("-").map(Number);

  if (!Number.isInteger(year) || !Number.isInteger(month) || !Number.isInteger(day)) {
    throw new Error(`Invalid date key: ${dateKey}`);
  }

  return new Date(year, month - 1, day);
}

export function getDaysBetweenDateKeys(startDateKey, endDateKey) {
  const start = createLocalDate(startDateKey);
  const end = createLocalDate(endDateKey);

  start.setHours(12, 0, 0, 0);
  end.setHours(12, 0, 0, 0);

  return Math.round((end - start) / (24 * 60 * 60 * 1000));
}

export function getTrackingDay(arrivalDateKey, dateKey) {
  return getDaysBetweenDateKeys(arrivalDateKey, dateKey) + 1;
}

export function getPuppyAgeLabel(birthDateKey, dateKey) {
  if (!birthDateKey) {
    return "";
  }

  const ageInDays = getDaysBetweenDateKeys(birthDateKey, dateKey);

  if (ageInDays < 0) {
    return "";
  }

  const weeks = Math.floor(ageInDays / 7);
  const days = ageInDays % 7;
  const weekLabel = weeks === 1 ? "1 week" : `${weeks} weeks`;

  if (days === 0) {
    return weekLabel;
  }

  return `${weekLabel} ${days}d`;
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

export function formatCompactTimeValue(timestamp) {
  return formatTimeInputValue(timestamp).replace(":", "");
}

export function floorToTenMinutes(date = new Date()) {
  return new Date(Math.floor(date.getTime() / TEN_MINUTES_IN_MS) * TEN_MINUTES_IN_MS);
}

function withTimeFromDate(date, timeSource) {
  const result = new Date(date);
  result.setHours(timeSource.getHours(), timeSource.getMinutes(), 0, 0);

  return result;
}

function getLatestEventTime(events) {
  return events.length > 0 ? new Date(events[0].occurredAt) : null;
}

function getLatestAllowedAddTime(date, now) {
  const dateKey = getTodayKey(date);
  const todayKey = getTodayKey(now);

  if (dateKey > todayKey) {
    return null;
  }

  if (dateKey === todayKey) {
    return floorToTenMinutes(now);
  }

  const endOfDay = new Date(date);
  endOfDay.setHours(23, 50, 0, 0);

  return endOfDay;
}

export function getSuggestedEventTime(eventTypeId, events = [], now = new Date(), date = now) {
  const latestEvent = events[0];
  const latestAllowedTime = getLatestAllowedAddTime(date, now);

  if (!latestAllowedTime) {
    return null;
  }

  let suggestedTime = latestAllowedTime;

  if (latestEvent && SLEEPING_EVENT_TYPES.has(latestEvent.type)) {
    suggestedTime = new Date(new Date(latestEvent.occurredAt).getTime() + ONE_HOUR_IN_MS);
  }

  const latestEventTime = getLatestEventTime(events);
  const constrainedTime = new Date(Math.min(suggestedTime.getTime(), latestAllowedTime.getTime()));

  if (latestEventTime && constrainedTime < latestEventTime) {
    return latestEventTime <= latestAllowedTime ? latestEventTime : null;
  }

  return withTimeFromDate(date, constrainedTime);
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

export function getStoredEvents(storage) {
  return getStoredDateKeys(storage)
    .flatMap((dateKey) => loadEventsForDate(storage, dateKey))
    .sort((first, second) => new Date(second.occurredAt) - new Date(first.occurredAt));
}

export function replaceStoredEvents(storage, events) {
  getStoredDateKeys(storage).forEach((dateKey) => {
    storage.removeItem(getStorageKey(dateKey));
  });

  const eventsByDateKey = new Map();
  events.forEach((event) => {
    if (!eventsByDateKey.has(event.dateKey)) {
      eventsByDateKey.set(event.dateKey, []);
    }

    eventsByDateKey.get(event.dateKey).push(event);
  });

  eventsByDateKey.forEach((dateEvents, dateKey) => {
    saveEventsForDate(storage, dateKey, dateEvents);
  });
}

export function applyStoredEventsSnapshot(storage, events = []) {
  replaceStoredEvents(
    storage,
    events.map((event) => ({
      id: event.id,
      type: event.type,
      occurredAt: event.occurredAt,
      dateKey: event.dateKey,
    })),
  );
}

export function addEvent(storage, event) {
  const events = loadEventsForDate(storage, event.dateKey);

  saveEventsForDate(storage, event.dateKey, [event, ...events]);

  return event;
}

export function addEventWithDefaults(storage, eventTypeId, now = new Date(), date = now) {
  const dateKey = getTodayKey(date);
  const events = loadEventsForDate(storage, dateKey);
  const latestEvent = events[0];
  const occurredAt = getSuggestedEventTime(eventTypeId, events, now, date);

  if (!occurredAt) {
    return [];
  }

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
  const normalizedTimeValue =
    timeValue.length === 4 && !timeValue.includes(":")
      ? `${timeValue.slice(0, 2)}:${timeValue.slice(2)}`
      : timeValue;
  const [hours, minutes] = normalizedTimeValue.split(":").map(Number);

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
