const SYNC_DEBOUNCE_MS = 500;

export function getApiBaseUrl() {
  return (import.meta.env.VITE_API_BASE_URL ?? "").replace(/\/$/, "");
}

export function createApiUrl(path) {
  return `${getApiBaseUrl()}${path}`;
}

export function createLoginUrl(returnUrl = window.location.href) {
  const loginUrl = new URL(createApiUrl("/api/auth/login"), window.location.origin);
  loginUrl.searchParams.set("returnUrl", returnUrl);

  return loginUrl.toString();
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
    createApiUrl,
    createLoginUrl,
    schedule() {
      window.clearTimeout(pendingSyncId);
      pendingSyncId = window.setTimeout(() => {
        syncNow().catch((error) => {
          console.error(error);
          onStatusChange?.("Sync failed");
        });
      }, SYNC_DEBOUNCE_MS);
    },
    async getCurrentUser() {
      try {
        const response = await fetch(createApiUrl("/api/auth/me"), { credentials: "include" });
        if (!response.ok) {
          onStatusChange?.("Sign in to sync");
          return null;
        }

        const user = await response.json();
        onStatusChange?.("Signed in");
        return user;
      } catch {
        onStatusChange?.("Offline");
        return null;
      }
    },
    async loadSnapshot() {
      const response = await fetch(createApiUrl("/api/sync"), { credentials: "include" });
      if (response.status === 204 || response.status === 404) {
        return null;
      }

      if (!response.ok) {
        throw new Error(`PawPrints snapshot load failed with ${response.status}`);
      }

      return response.json();
    },
    async signOut() {
      await fetch(createApiUrl("/api/auth/logout"), {
        method: "POST",
        credentials: "include",
      });
      onStatusChange?.("Signed out");
    },
  };
}
