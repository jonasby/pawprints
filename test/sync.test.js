import test from "node:test";
import assert from "node:assert/strict";

import { createRemoteSync } from "../src/sync.js";

function withWindowTimers(callback) {
  const previousWindow = globalThis.window;
  globalThis.window = {
    clearTimeout,
    setTimeout: (fn) => {
      fn();
      return 1;
    },
  };

  return Promise.resolve()
    .then(callback)
    .finally(() => {
      globalThis.window = previousWindow;
    });
}

function waitForScheduledSync() {
  return new Promise((resolve) => {
    setTimeout(resolve, 0);
  });
}

test("GivenPendingEvents_WhenSyncRuns_ThenEventIdsAreMarkedInFlightAndSynced", async () => {
  const previousFetch = globalThis.fetch;
  const pendingEvent = {
    id: "event-1",
    type: "pee",
    occurredAt: "2026-04-26T07:00:00.000Z",
    dateKey: "2026-04-26",
  };
  const eventStatusChanges = [];
  const committedChanges = [];

  try {
    globalThis.fetch = async () => ({
      ok: true,
      status: 200,
    });

    await withWindowTimers(async () => {
      const remoteSync = createRemoteSync(
        {},
        {
          getSettings: () => ({ arrivalDate: "2026-04-19", birthDate: "2026-02-22" }),
          getPendingChanges: () => ({ upserts: [pendingEvent], deletedEventIds: [] }),
          markChangesCommitted: (_storage, changes) => committedChanges.push(changes),
          onEventsInFlightChange: (eventIds, isInFlight) => {
            eventStatusChanges.push({ eventIds, isInFlight });
          },
          onEventsSynced: (eventIds) => {
            eventStatusChanges.push({ eventIds, isSynced: true });
          },
        },
      );

      remoteSync.schedule();
      await waitForScheduledSync();
    });
  } finally {
    globalThis.fetch = previousFetch;
  }

  assert.deepEqual(eventStatusChanges, [
    { eventIds: ["event-1"], isInFlight: true },
    { eventIds: ["event-1"], isSynced: true },
    { eventIds: ["event-1"], isInFlight: false },
  ]);
  assert.deepEqual(committedChanges, [{ upsertIds: ["event-1"], deletedEventIds: [] }]);
});

test("GivenAnalyticsRequest_WhenLoadingPuppyAnalytics_ThenEndpointIsFetched", async () => {
  const previousFetch = globalThis.fetch;
  const requestedUrls = [];

  try {
    globalThis.fetch = async (url, options) => {
      requestedUrls.push({ url, options });
      return {
        ok: true,
        status: 200,
        async json() {
          return { days: [{ dateKey: "2026-04-26", poops: 1, wees: 2, sleepMinutes: 540, napMinutes: 70 }] };
        },
      };
    };

    const remoteSync = createRemoteSync({}, {});
    const analytics = await remoteSync.loadPuppyAnalytics();

    assert.deepEqual(analytics.days, [
      { dateKey: "2026-04-26", poops: 1, wees: 2, sleepMinutes: 540, napMinutes: 70 },
    ]);
    assert.deepEqual(requestedUrls, [
      {
        url: "/api/puppy-analytics",
        options: { credentials: "include" },
      },
    ]);
  } finally {
    globalThis.fetch = previousFetch;
  }
});
