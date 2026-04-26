import test from "node:test";
import assert from "node:assert/strict";

import {
  EVENT_TYPES,
  addEvent,
  addEventWithDefaults,
  createEvent,
  formatTimeInputValue,
  getEventsForDate,
  getStorageKey,
  getTodayKey,
  loadEventsForDate,
  saveEventsForDate,
  updateEventsTime,
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
  assert.match(event.id, /^1777186800000-pee-/);
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
    ["pee", "poop", "eat", "nap", "sleep", "wake"],
  );
});

test("GivenANapWasThePreviousEvent_WhenAddingWake_ThenItDefaultsToOneHourLater", () => {
  const dateKey = "2026-04-26";
  const nap = createEvent("nap", new Date("2026-04-26T08:10:00.000Z"));
  const storage = createMemoryStorage({
    [getStorageKey(dateKey)]: JSON.stringify([nap]),
  });

  addEventWithDefaults(storage, "wake", new Date("2026-04-26T09:45:00.000Z"));

  const events = loadEventsForDate(storage, dateKey);
  assert.deepEqual(
    events.map((event) => [event.type, event.occurredAt]),
    [
      ["wake", "2026-04-26T09:10:00.000Z"],
      ["nap", "2026-04-26T08:10:00.000Z"],
    ],
  );
});

test("GivenASuggestedEventFallsOnTomorrow_WhenAddingIt_ThenItIsStoredUnderTomorrow", () => {
  const todayKey = "2026-04-26";
  const tomorrowKey = "2026-04-27";
  const nap = createEvent("nap", new Date("2026-04-26T23:30:00.000Z"));
  const storage = createMemoryStorage({
    [getStorageKey(todayKey)]: JSON.stringify([nap]),
  });

  addEventWithDefaults(storage, "wake", new Date("2026-04-26T23:40:00.000Z"));

  assert.deepEqual(
    loadEventsForDate(storage, todayKey).map((event) => event.type),
    ["nap"],
  );
  assert.deepEqual(
    loadEventsForDate(storage, tomorrowKey).map((event) => event.type),
    ["wake"],
  );
});

test("GivenASleepingEventWasPrevious_WhenAddingWee_ThenWakeIsAddedAtTheSameTime", () => {
  const dateKey = "2026-04-26";
  const sleep = createEvent("sleep", new Date("2026-04-26T06:00:00.000Z"));
  const storage = createMemoryStorage({
    [getStorageKey(dateKey)]: JSON.stringify([sleep]),
  });

  addEventWithDefaults(storage, "pee", new Date("2026-04-26T07:30:00.000Z"));

  const events = loadEventsForDate(storage, dateKey);
  assert.deepEqual(
    events.map((event) => [event.type, event.occurredAt]),
    [
      ["wake", "2026-04-26T07:00:00.000Z"],
      ["pee", "2026-04-26T07:00:00.000Z"],
      ["sleep", "2026-04-26T06:00:00.000Z"],
    ],
  );
});

test("GivenEventsAtTheSameTimestamp_WhenUpdatingTheirTime_ThenTheyMoveTogether", () => {
  const date = new Date("2026-04-26T12:00:00");
  const dateKey = "2026-04-26";
  const wake = createEvent("wake", new Date("2026-04-26T07:00:00.000Z"));
  const pee = createEvent("pee", new Date("2026-04-26T07:00:00.000Z"));
  const storage = createMemoryStorage({
    [getStorageKey(dateKey)]: JSON.stringify([wake, pee]),
  });

  updateEventsTime(storage, [wake.id, pee.id], "07:20", date);

  assert.deepEqual(
    loadEventsForDate(storage, dateKey).map((event) => [
      event.type,
      formatTimeInputValue(event.occurredAt),
    ]),
    [
      ["wake", "07:20"],
      ["pee", "07:20"],
    ],
  );
});
