export const EVENT_TYPES = [
  { id: "pee", label: "Pee", emoji: "💧", description: "Quick potty break" },
  { id: "poop", label: "Poop", emoji: "💩", description: "Bathroom success" },
  { id: "eat", label: "Eat", emoji: "🍽️", description: "Meal or snack" },
  { id: "nap", label: "Nap", emoji: "😴", description: "Short daytime rest" },
  { id: "sleep", label: "Sleep", emoji: "🌙", description: "Longer sleep stretch" },
];

const STORAGE_PREFIX = "puppy-events";

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
