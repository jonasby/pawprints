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

async function createApiError(response, path) {
  let responseText = "";
  try {
    responseText = await response.text();
  } catch {
    responseText = "";
  }

  const error = new Error(`PawPrints API request failed with ${response.status} at ${path}`);
  error.status = response.status;
  error.path = path;
  error.responseText = responseText?.slice(0, 2000);
  return error;
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
    async getCurrentUser(options = {}) {
      const silent = options.silent === true;
      try {
        const response = await fetch(createApiUrl("/api/auth/me"), { credentials: "include" });
        if (!response.ok) {
          if (!silent) {
            onStatusChange?.("Sign in to sync");
          }
          return null;
        }

        const user = await response.json();
        if (!user || typeof user.email !== "string" || user.email.length === 0) {
          if (!silent) {
            onStatusChange?.("Sign in to sync");
          }
          return null;
        }

        onStatusChange?.("Signed in");
        return user;
      } catch {
        if (!silent) {
          onStatusChange?.("Offline");
        }
        return null;
      }
    },
    async loadSnapshot() {
      const path = "/api/sync";
      const response = await fetch(createApiUrl(path), { credentials: "include" });
      if (response.status === 204 || response.status === 404) {
        return null;
      }

      if (!response.ok) {
        throw await createApiError(response, path);
      }

      const raw = await response.text();
      if (!raw.trim()) {
        return null;
      }

      try {
        return JSON.parse(raw);
      } catch {
        const error = new Error(`PawPrints snapshot response was not valid JSON at ${path}`);
        error.path = path;
        error.responseText = raw.slice(0, 2000);
        throw error;
      }
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
