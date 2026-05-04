import test from "node:test";
import assert from "node:assert/strict";

import {
  buildImportPreview,
  normalizeImportToken,
  parseImportText,
  resolveImportTokenLocal,
  stripEmojis,
} from "../src/import.js";

const emptySettings = { arrivalDate: "", birthDate: "", customEventTypes: [] };

test("GivenEmojiHeavyLine_WhenParsing_ThenTokensIgnoreEmoji", () => {
  const { rows } = parseImportText("0500 wee + poo 💩 ");
  assert.equal(rows.length, 1);
  assert.deepEqual(rows[0].tokens, ["wee", "poo"]);
});

test("GivenCompactTime_WhenBuildingPreview_ThenMapsSynonyms", () => {
  const preview = buildImportPreview(emptySettings, "0700 wake + wee + poo");
  assert.equal(preview.previewRows.length, 1);
  const types = preview.previewRows[0].tokenPreviews.map((tp) => tp.resolution.typeId);
  assert.deepEqual(types, ["wake", "pee", "poop"]);
});

test("GivenFoodToken_WhenResolving_ThenMapsToEat", () => {
  const resolution = resolveImportTokenLocal(normalizeImportToken("food"), emptySettings);
  assert.equal(resolution.typeId, "eat");
});

test("GivenChewToken_WhenResolving_ThenCreatesNewSlug", () => {
  const resolution = resolveImportTokenLocal(normalizeImportToken("chew"), emptySettings);
  assert.equal(resolution.source, "new");
  assert.equal(resolution.typeId, "chew");
});

test("GivenEmojiText_WhenStripping_ThenLettersRemain", () => {
  assert.equal(stripEmojis("sleep 💤").trim(), "sleep");
});
