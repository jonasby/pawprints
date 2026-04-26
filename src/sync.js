const SYNC_DEBOUNCE_MS = 500;

export function getApiBaseUrl() {
  return (import.meta.env.VITE_API_BASE_URL ?? "").replace(/\/$/, "");
}

function createApiUrl(path) {
  return `${getApiBaseUrl()}${path}`;
}

export function getAuthUrl(path) {
  return createApiUrl(path);
}

export function createRemoteSync(storage, { getSettings, loadEvents, onStatusChange }) {
  let pendingSyncId;

  async function syncNow() {
    const settings = getSettings();
    if (!settings.arrivalDate || !settings.birthDate) {
      return;
    }

    onStatusChange?.("Saving...");
    const response = await fetch(createApiUrl("/api/sync"), {
      method: "PUT",
      headers: {
        "Content-Type": "application/json",
      },
      credentials: "include",
      body: JSON.stringify({
        settings: {
          arrivalDate: settings.arrivalDate,
          birthDate: settings.birthDate,
        },
        events: loadEvents(storage).map((event) => ({
          id: event.id,
          type: event.type,
          occurredAt: event.occurredAt,
          dateKey: event.dateKey,
        })),
      }),
    });

    if (response.status === 401 || response.status === 403) {
      onStatusChange?.("Sign in to sync");
      return;
    }

    if (!response.ok) {
      throw new Error(`PawPrints sync failed with ${response.status}`);
    }

    onStatusChange?.("Saved");
  }

  return {
    schedule() {
      window.clearTimeout(pendingSyncId);
      pendingSyncId = window.setTimeout(() => {
        syncNow().catch((error) => {
          console.error(error);
          onStatusChange?.("Sync failed");
        });
      }, SYNC_DEBOUNCE_MS);
    },
    async refreshAuth() {
      try {
        const response = await fetch(createApiUrl("/api/auth/me"), { credentials: "include" });
        onStatusChange?.(response.ok ? "Signed in" : "Sign in to sync");
      } catch {
        onStatusChange?.("Offline");
      }
    },
  };
}
