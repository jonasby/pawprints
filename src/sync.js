const SYNC_DEBOUNCE_MS = 500;

export function getApiBaseUrl() {
  return (import.meta.env?.VITE_API_BASE_URL ?? "").replace(/\/$/, "");
}

export function createApiUrl(path) {
  return `${getApiBaseUrl()}${path}`;
}

export function createLoginUrl(returnUrl = window.location.href) {
  const loginUrl = new URL(createApiUrl("/api/auth/login"), window.location.origin);
  loginUrl.searchParams.set("returnUrl", returnUrl);

  return loginUrl.toString();
}

function createNetworkError(path, cause) {
  const error = new Error(`PawPrints could not reach ${path}`);
  error.path = path;
  if (cause instanceof Error) {
    error.cause = cause;
    if (cause.message) {
      error.message = `${error.message}: ${cause.message}`;
    }
  }
  return error;
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

export function createRemoteSync(
  storage,
  {
    getSettings,
    loadEvents,
    getPendingChanges,
    markChangesCommitted,
    onStatusChange,
    onInFlightChange,
    onEventsInFlightChange,
    onEventsSynced,
  },
) {
  let pendingSyncId;
  const authPath = "/api/auth/me";

  async function syncNow() {
    const settings = getSettings();
    if (!settings.arrivalDate || !settings.birthDate) {
      return;
    }

    let pendingUpsertIds = [];

    try {
      const pendingChanges = getPendingChanges?.(storage) ?? { upserts: [], deletedEventIds: [] };
      pendingUpsertIds = pendingChanges.upserts.map((event) => event.id);

      onInFlightChange?.(true);
      onEventsInFlightChange?.(pendingUpsertIds, true);
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
          upserts: pendingChanges.upserts,
          deletedEventIds: pendingChanges.deletedEventIds,
        }),
      });

      if (response.status === 401 || response.status === 403) {
        onStatusChange?.("Sign in to sync");
        return;
      }

      if (!response.ok) {
        throw new Error(`PawPrints sync failed with ${response.status}`);
      }

      markChangesCommitted?.(storage, {
        upsertIds: pendingUpsertIds,
        deletedEventIds: pendingChanges.deletedEventIds,
      });
      onEventsSynced?.(pendingUpsertIds);
      onStatusChange?.("Saved");
    } finally {
      onEventsInFlightChange?.(pendingUpsertIds, false);
      onInFlightChange?.(false);
    }
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
          onInFlightChange?.(false);
        });
      }, SYNC_DEBOUNCE_MS);
    },
    async checkSession() {
      try {
        const response = await fetch(createApiUrl(authPath), { credentials: "include" });
        if (response.status === 401 || response.status === 403) {
          return {
            isActive: false,
            status: response.status,
            path: authPath,
            reason: "unauthorized",
          };
        }

        if (!response.ok) {
          return {
            isActive: false,
            status: response.status,
            path: authPath,
            reason: "http-error",
            error: await createApiError(response, authPath),
          };
        }

        const user = await response.json();
        if (!user || typeof user.email !== "string" || user.email.length === 0) {
          return {
            isActive: false,
            path: authPath,
            reason: "invalid-user",
            error: new Error("Authenticated session returned an invalid account profile."),
          };
        }

        return {
          isActive: true,
          user,
        };
      } catch (error) {
        return {
          isActive: false,
          path: authPath,
          reason: "network-error",
          error: createNetworkError(authPath, error),
        };
      }
    },
    async getCurrentUser(options = {}) {
      const silent = options.silent === true;
      const session = await this.checkSession();
      if (session.isActive) {
        const { user } = session;
        onStatusChange?.("Signed in");
        return user;
      }

      if (!silent) {
        onStatusChange?.(session.reason === "network-error" ? "Offline" : "Sign in to sync");
      }
      return null;
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
    async loadPuppyAnalytics() {
      const path = "/api/puppy-analytics";
      const response = await fetch(createApiUrl(path), { credentials: "include" });
      if (!response.ok) {
        throw await createApiError(response, path);
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
    async createInvite() {
      const path = "/api/invites";
      const response = await fetch(createApiUrl(path), {
        method: "POST",
        credentials: "include",
      });

      if (response.status === 401) {
        onStatusChange?.("Sign in to sync");
        const error = new Error("Sign in required");
        error.status = 401;
        throw error;
      }

      if (response.status === 403) {
        const error = new Error("Collaborators cannot create invites");
        error.status = 403;
        throw error;
      }

      if (response.status === 404) {
        const error = new Error("Save your puppy log once before sharing.");
        error.status = 404;
        throw error;
      }

      if (!response.ok) {
        throw await createApiError(response, path);
      }

      return response.json();
    },
    async acceptInvite(token) {
      const trimmed = typeof token === "string" ? token.trim() : "";
      if (!trimmed) {
        throw new Error("Missing invite token");
      }

      const path = `/api/invites/${encodeURIComponent(trimmed)}/accept`;
      const response = await fetch(createApiUrl(path), {
        method: "POST",
        credentials: "include",
      });

      if (response.status === 204) {
        return;
      }

      throw await createApiError(response, path);
    },
  };
}
