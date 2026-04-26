import test from "node:test";
import assert from "node:assert/strict";

import {
  EVENT_TYPES,
  addEvent,
  createEvent,
  getEventsForDate,
  getTodayKey,
  loadEventsForDate,
  saveEventsForDate,
} from "../src/events.js";

function createMemoryStorage(seed = {}) {
  const values = new Map(Object.entries(seed));

  return {
    getItem(key) {
      return values.has(key) ? values.get(key) : null;
    },
    setItem(key, value) {
      values.set(key, String(value));
    },
    removeItem(key) {
      values.delete(key);
    },
  };
}

test("GivenAValidEventType_WhenCreatingAnEvent_ThenItIncludesDisplayMetadata", () => {
  const happenedAt = new Date("2026-04-26T07:00:00.000Z");

  const event = createEvent("pee", happenedAt);

  assert.equal(event.type, "pee");
  assert.equal(event.occurredAt, "2026-04-26T07:00:00.000Z");
  assert.equal(event.dateKey, "2026-04-26");
  assert.match(event.id, /^1745650800000-pee-/);
});

test("GivenAnUnknownEventType_WhenCreatingAnEvent_ThenItIsRejected", () => {
  assert.throws(() => createEvent("zoomies"), /Unknown puppy event type/);
});

test("GivenEventsInStorage_WhenLoadingToday_ThenNewestEventsComeFirst", () => {
  const dateKey = "2026-04-26";
  const olderEvent = createEvent("eat", new Date("2026-04-26T07:00:00.000Z"));
  const newerEvent = createEvent("poop", new Date("2026-04-26T08:00:00.000Z"));
  const storage = createMemoryStorage({
    [`puppy-events:${dateKey}`]: JSON.stringify([olderEvent, newerEvent]),
  });

  const events = loadEventsForDate(storage, dateKey);

  assert.deepEqual(
    events.map((event) => event.type),
    ["poop", "eat"],
  );
});

test("GivenEventsToPersist_WhenSavingForDate_ThenTheyAreStoredUnderThatDay", () => {
  const dateKey = "2026-04-26";
  const storage = createMemoryStorage();
  const events = [createEvent("nap", new Date("2026-04-26T09:00:00.000Z"))];

  saveEventsForDate(storage, dateKey, events);

  assert.deepEqual(JSON.parse(storage.getItem(`puppy-events:${dateKey}`)), events);
});

test("GivenAnEventType_WhenAddingAnEvent_ThenItAppendsToExistingStorage", () => {
  const dateKey = "2026-04-26";
  const storage = createMemoryStorage({
    [`puppy-events:${dateKey}`]: JSON.stringify([
      createEvent("pee", new Date("2026-04-26T07:00:00.000Z")),
    ]),
  });

  const event = createEvent("poop", new Date("2026-04-26T08:00:00.000Z"));
  addEvent(storage, event);

  assert.deepEqual(
    loadEventsForDate(storage, dateKey).map((storedEvent) => storedEvent.type),
    ["poop", "pee"],
  );
});

test("GivenASelectedDate_WhenGettingEventsForDate_ThenItUsesThatDayKey", () => {
  const selectedDate = new Date("2026-04-26T12:00:00");
  const storage = createMemoryStorage({
    "puppy-events:2026-04-26": JSON.stringify([
      createEvent("pee", new Date("2026-04-26T07:00:00.000Z")),
    ]),
    "puppy-events:2026-04-25": JSON.stringify([
      createEvent("sleep", new Date("2026-04-25T20:00:00.000Z")),
    ]),
  });

  assert.deepEqual(
    getEventsForDate(storage, selectedDate).map((event) => event.type),
    ["pee"],
  );
});

test("GivenALocalDate_WhenGettingTodayKey_ThenItUsesCalendarDateFormat", () => {
  assert.equal(getTodayKey(new Date(2026, 3, 26, 23, 30)), "2026-04-26");
});

test("GivenSupportedEventTypes_WhenRendered_ThenTheyCoverInitialPuppyEvents", () => {
  assert.deepEqual(
    EVENT_TYPES.map((eventType) => eventType.id),
    ["pee", "poop", "eat", "nap", "sleep"],
  );
});
