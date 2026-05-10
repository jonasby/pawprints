import {
  EVENT_TYPES,
  createFlexibleEvent,
  createLocalDate,
  getEventType,
  normalizeEventTypeSlug,
  registerCustomEventType,
  loadEventsForDate,
  saveEventsForDate,
} from "./events.js";
import { createApiUrl } from "./sync.js";

const LINE_WITH_COMPACT_TIME = /^\s*(\d{4})\s+(.+)$/;
const LINE_WITH_COLON_TIME = /^\s*(\d{1,2}):(\d{2})\s+(.+)$/;

export function stripEmojis(text) {
  return text
    .replace(/\p{Extended_Pictographic}/gu, "")
    .replace(/\uFE0F/g, "")
    .replace(/\s+/g, " ")
    .trim();
}

function buildSynonymMap() {
  /** @type {Map<string, string>} */
  const map = new Map();

  const add = (phrase, typeId) => {
    const key = phrase.toLowerCase().trim();
    if (key) {
      map.set(key, typeId);
    }
  };

  for (const t of EVENT_TYPES) {
    add(t.id, t.id);
    add(t.label, t.id);
  }

  const extras = [
    ["pee", "pee"],
    ["wee", "pee"],
    ["wees", "pee"],
    ["weeing", "pee"],
    ["urine", "pee"],
    ["tinkle", "pee"],
    ["wee wee", "pee"],
    ["poop", "poop"],
    ["poo", "poop"],
    ["poos", "poop"],
    ["poot", "poop"],
    ["toilet", "poop"],
    ["number two", "poop"],
    ["eat", "eat"],
    ["food", "eat"],
    ["feed", "eat"],
    ["feeding", "eat"],
    ["meal", "eat"],
    ["breakfast", "eat"],
    ["lunch", "eat"],
    ["dinner", "eat"],
    ["snack", "eat"],
    ["nap", "nap"],
    ["naps", "nap"],
    ["napping", "nap"],
    ["snooze", "nap"],
    ["sleep", "sleep"],
    ["sleeps", "sleep"],
    ["sleeping", "sleep"],
    ["bed", "sleep"],
    ["doze", "sleep"],
    ["wake", "wake"],
    ["woke", "wake"],
    ["wakes", "wake"],
    ["waking", "wake"],
    ["awake", "wake"],
    ["up", "wake"],
    ["rise", "wake"],
    ["walk", "walkies"],
    ["walks", "walkies"],
    ["walking", "walkies"],
    ["walkies", "walkies"],
    ["walkie", "walkies"],
    ["stroll", "walkies"],
    ["train", "training"],
    ["trains", "training"],
    ["training", "training"],
    ["practice", "training"],
    ["chill", "chill"],
    ["chilling", "chill"],
    ["relaxed", "chill"],
    ["chew", "chew"],
    ["chewing", "chew"],
    ["chews", "chew"],
  ];

  for (const [phrase, id] of extras) {
    add(phrase, id);
  }

  return map;
}

const SYNONYM_TO_TYPE = buildSynonymMap();

export function levenshtein(a, b) {
  if (a === b) {
    return 0;
  }
  const aLen = a.length;
  const bLen = b.length;
  if (aLen === 0) {
    return bLen;
  }
  if (bLen === 0) {
    return aLen;
  }

  /** @type {number[]} */
  let prev = new Array(bLen + 1);
  /** @type {number[]} */
  let cur = new Array(bLen + 1);

  for (let j = 0; j <= bLen; j += 1) {
    prev[j] = j;
  }

  for (let i = 1; i <= aLen; i += 1) {
    cur[0] = i;
    const aChar = a.charCodeAt(i - 1);

    for (let j = 1; j <= bLen; j += 1) {
      const cost = aChar === b.charCodeAt(j - 1) ? 0 : 1;
      cur[j] = Math.min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + cost);
    }

    const swap = prev;
    prev = cur;
    cur = swap;
  }

  return prev[bLen];
}

export function normalizeImportToken(rawToken) {
  const cleaned = stripEmojis(rawToken).toLowerCase().trim();

  return cleaned.replace(/\s+/g, " ");
}

export function slugFromNormalized(normalized) {
  return normalizeEventTypeSlug(normalized);
}

/**
 * @param {string} normalized
 * @param {import("./events.js").PawPrintsSettings} settings
 */
export function resolveImportTokenLocal(normalized, settings) {
  const customTypes = settings.customEventTypes ?? [];
  const direct = SYNONYM_TO_TYPE.get(normalized);

  if (direct) {
    const builtIn = getEventType(direct);
    if (builtIn) {
      return {
        source: /** @type {const} */ ("synonym"),
        typeId: direct,
        confidence: 1,
        isCustom: false,
      };
    }

    const custom = customTypes.find((c) => c.id === direct);
    if (custom) {
      return {
        source: /** @type {const} */ ("synonym"),
        typeId: direct,
        confidence: 1,
        isCustom: true,
      };
    }
  }

  for (const c of customTypes) {
    if (c.id === normalized || c.label.toLowerCase() === normalized) {
      return {
        source: /** @type {const} */ ("custom"),
        typeId: c.id,
        confidence: 1,
        isCustom: true,
      };
    }
  }

  const candidates = [];

  for (const t of EVENT_TYPES) {
    const id = t.id;
    const label = t.label.toLowerCase();
    const d1 = levenshtein(normalized, id);
    const d2 = levenshtein(normalized, label);
    const best = Math.min(d1, d2);
    const maxLen = Math.max(normalized.length, id.length, label.length);
    const score = 1 - best / Math.max(maxLen, 1);
    candidates.push({ typeId: id, distance: best, score });
  }

  for (const c of customTypes) {
    const d1 = levenshtein(normalized, c.id);
    const d2 = levenshtein(normalized, c.label.toLowerCase());
    const best = Math.min(d1, d2);
    const maxLen = Math.max(normalized.length, c.id.length, c.label.length);
    const score = 1 - best / Math.max(maxLen, 1);
    candidates.push({ typeId: c.id, distance: best, score, isCustom: true });
  }

  candidates.sort((a, b) => a.distance - b.distance || b.score - a.score);
  const best = candidates[0];

  const maxDistance =
    normalized.length <= 3 ? 0 : normalized.length <= 6 ? 1 : normalized.length <= 10 ? 2 : 2;

  if (best && best.distance <= maxDistance && best.score >= 0.55) {
    return {
      source: /** @type {const} */ ("fuzzy"),
      typeId: best.typeId,
      confidence: best.score,
      isCustom: Boolean(best.isCustom),
    };
  }

  const slug = slugFromNormalized(normalized);

  return {
    source: /** @type {const} */ ("new"),
    typeId: slug || "custom",
    confidence: 0.25,
    isCustom: true,
    suggestedLabel: normalized
      ? normalized.replace(/\b\w/g, (character) => character.toUpperCase())
      : "Custom",
  };
}

export function parseImportLine(line) {
  const trimmed = line.trim();
  if (!trimmed || trimmed.startsWith("#")) {
    return null;
  }

  let hours;
  let minutes;
  let rest;

  const compact = LINE_WITH_COMPACT_TIME.exec(trimmed);
  if (compact) {
    const hhmm = compact[1];
    hours = Number.parseInt(hhmm.slice(0, 2), 10);
    minutes = Number.parseInt(hhmm.slice(2, 4), 10);
    rest = compact[2];
  } else {
    const colon = LINE_WITH_COLON_TIME.exec(trimmed);
    if (!colon) {
      return { error: `Could not read a time from: ${trimmed}` };
    }
    hours = Number.parseInt(colon[1], 10);
    minutes = Number.parseInt(colon[2], 10);
    rest = colon[3];
  }

  if (
    !Number.isInteger(hours) ||
    !Number.isInteger(minutes) ||
    hours < 0 ||
    hours > 23 ||
    minutes < 0 ||
    minutes > 59
  ) {
    return { error: `Invalid time in line: ${trimmed}` };
  }

  return { hours, minutes, rest: rest.trim() };
}

export function splitEventPhrase(rest) {
  return rest
    .split(/\s*\+\s*/)
    .map((part) => stripEmojis(part).trim())
    .filter(Boolean);
}

export function parseImportText(text) {
  const lines = text.split(/\r?\n/);
  /** @type {Array<{ rawLine: string; hours: number; minutes: number; tokens: string[] }>} */
  const rows = [];
  /** @type {string[]} */
  const errors = [];

  for (const line of lines) {
    const parsed = parseImportLine(line);
    if (!parsed) {
      continue;
    }
    if ("error" in parsed && parsed.error) {
      errors.push(parsed.error);
      continue;
    }
    if (!("rest" in parsed)) {
      continue;
    }

    const tokens = splitEventPhrase(parsed.rest);
    if (tokens.length === 0) {
      errors.push(`No events after time in: ${line.trim()}`);
      continue;
    }

    rows.push({
      rawLine: line.trim(),
      hours: parsed.hours,
      minutes: parsed.minutes,
      tokens,
    });
  }

  return { rows, errors };
}

/**
 * @param {import("./events.js").PawPrintsSettings} settings
 */
export function buildImportPreview(settings, text) {
  const { rows, errors } = parseImportText(text);
  const previewRows = [];

  for (const row of rows) {
    const tokenPreviews = row.tokens.map((raw) => {
      const normalized = normalizeImportToken(raw);
      const resolution = resolveImportTokenLocal(normalized, settings);
      return { raw, normalized, resolution };
    });
    previewRows.push({
      rawLine: row.rawLine,
      hours: row.hours,
      minutes: row.minutes,
      tokenPreviews,
    });
  }

  return { previewRows, errors };
}

/**
 * @param {*} preview
 * @param {Array<{ token: string; typeId: string; label?: string; emoji?: string; isNew?: boolean }>} hints
 * @param {import("./events.js").PawPrintsSettings} settings
 */
export function mergeAiHintsIntoPreview(preview, hints, settings) {
  if (!hints?.length) {
    return preview;
  }

  const byToken = new Map();
  for (const h of hints) {
    const key = normalizeImportToken(h.token);
    if (key) {
      byToken.set(key, h);
    }
  }

  for (const row of preview.previewRows) {
    for (const tp of row.tokenPreviews) {
      const hint = byToken.get(tp.normalized);
      if (!hint?.typeId) {
        continue;
      }

      const slug =
        normalizeEventTypeSlug(hint.typeId) ||
        hint.typeId.toLowerCase().replace(/\s+/g, "-").slice(0, 40);

      tp.resolution = {
        source: /** @type {const} */ ("ai"),
        typeId: slug,
        confidence: 0.9,
        isCustom: Boolean(hint.isNew),
        suggestedLabel: hint.label,
        suggestedEmoji: hint.emoji,
      };

      if (hint.isNew && slug) {
        const exists = (settings.customEventTypes ?? []).some((c) => c.id === slug);
        if (!exists) {
          if (!settings.customEventTypes) {
            settings.customEventTypes = [];
          }
          settings.customEventTypes.push({
            id: slug,
            label:
              hint.label?.trim() ||
              tp.raw.replace(/\b\w/g, (character) => character.toUpperCase()),
            emoji: hint.emoji?.trim() || "✨",
          });
        }
      }
    }
  }

  return preview;
}

export function buildOccurredAtForDateKey(dateKey, hours, minutes) {
  const date = createLocalDate(dateKey);
  date.setHours(hours, minutes, 0, 0);

  return date;
}

/**
 * @param {import("./events.js").PawPrintsSettings} settings
 * @param {*} tp
 */
function ensureTypeRegisteredForImport(settings, tp) {
  const resolution = tp.resolution;
  const typeId = resolution.typeId;

  if (!typeId) {
    return;
  }

  if (getEventType(typeId)) {
    return;
  }

  if ((settings.customEventTypes ?? []).some((entry) => entry.id === typeId)) {
    return;
  }

  registerCustomEventType(settings, {
    id: typeId,
    label: resolution.suggestedLabel ?? typeId,
    emoji: resolution.suggestedEmoji ?? "✨",
  });
}

/**
 * @param {string[]} tokens
 * @param {import("./events.js").PawPrintsSettings} settings
 */
export async function fetchAiImportHints(tokens, settings) {
  const unique = [...new Set(tokens.map((token) => token.trim()).filter(Boolean))];
  if (unique.length === 0) {
    return null;
  }

  const knownTypes = [...EVENT_TYPES, ...(settings.customEventTypes ?? [])].map((entry) => ({
    id: entry.id,
    label: entry.label,
  }));

  try {
    const response = await fetch(createApiUrl("/api/import/resolve-tokens"), {
      method: "POST",
      credentials: "include",
      headers: {
        "Content-Type": "application/json",
      },
      body: JSON.stringify({ tokens: unique, knownTypes }),
    });

    if (!response.ok) {
      return null;
    }

    return response.json();
  } catch {
    return null;
  }
}

/**
 * @param {Storage} storage
 * @param {import("./events.js").PawPrintsSettings} settings
 * @param {string} dateKey
 * @param {*} preview
 */
export function applyImportPreview(storage, settings, dateKey, preview) {
  /** @type {Array<{ id: string; type: string; occurredAt: string; dateKey: string; committed: boolean }>} */
  const newEvents = [];

  for (const row of preview.previewRows) {
    const occurredAt = buildOccurredAtForDateKey(dateKey, row.hours, row.minutes);

    for (const tokenPreview of row.tokenPreviews) {
      ensureTypeRegisteredForImport(settings, tokenPreview);
      const resolution = tokenPreview.resolution;
      const event = createFlexibleEvent(resolution.typeId, occurredAt, settings);
      newEvents.push(event);
    }
  }

  const existing = loadEventsForDate(storage, dateKey);
  const merged = [...newEvents, ...existing].sort(
    (first, second) => new Date(second.occurredAt) - new Date(first.occurredAt),
  );
  saveEventsForDate(storage, dateKey, merged);

  return newEvents.length;
}
