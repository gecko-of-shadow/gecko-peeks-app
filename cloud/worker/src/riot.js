// Riot public status feeds (League of Legends and VALORANT).
// The feeds need no key. This file maps one feed into the small Event shape the app reads.
import { iso as isoDate } from "./time.js";

export const LOL_STATUS_BASE = "https://lol.secure.dyn.riotcdn.net/channels/public/x/status/";
export const VALORANT_STATUS_BASE = "https://valorant.secure.dyn.riotcdn.net/channels/public/x/status/";
export const FEED_TIMEOUT_MS = 5000;
export const FEED_MAX_BYTES = 256 * 1024;
const MAX_EVENTS = 10;
const MAX_TITLE = 200;
const MAX_TEXT = 600;
const MAX_PLATFORMS = 8;
const SEVERITIES = new Set(["info", "warning", "critical"]);
const STATUSES = new Set(["scheduled", "in_progress"]);

// The en_US entry, else the first entry with text.
function pickLocale(list) {
  if (!Array.isArray(list)) return "";
  const items = list.filter((x) => x && typeof x.content === "string");
  const en = items.find((x) => x.locale === "en_US");
  const pick = en || items[0];
  return pick ? pick.content.trim() : "";
}

function newestPublishedText(updates) {
  if (!Array.isArray(updates)) return null;
  const published = updates
    .filter((u) => u && u.publish === true)
    .map((u) => ({ u, t: Date.parse(u.updated_at || u.created_at || "") || 0 }))
    .sort((a, b) => b.t - a.t);
  for (const { u } of published) {
    const text = pickLocale(u.translations);
    if (text) return text.slice(0, MAX_TEXT);
  }
  return null;
}

function mapEvent(src, type, nowMs) {
  if (!src || typeof src !== "object") return null;
  const status = STATUSES.has(src.maintenance_status) ? src.maintenance_status : null;
  if (src.maintenance_status === "complete") return null;
  const title = pickLocale(src.titles).slice(0, MAX_TITLE);
  if (!title) return null;
  const archiveAt = isoDate(src.archive_at);
  if (archiveAt && Date.parse(archiveAt) < nowMs) return null;
  const id = Number(src.id);
  return {
    id: Number.isSafeInteger(id) ? id : null,
    type,
    severity: SEVERITIES.has(src.incident_severity) ? src.incident_severity : null,
    status,
    title,
    text: newestPublishedText(src.updates),
    platforms: Array.isArray(src.platforms) ? src.platforms.filter((p) => typeof p === "string").slice(0, MAX_PLATFORMS) : [],
    createdAt: isoDate(src.created_at),
    updatedAt: isoDate(src.updated_at),
    archiveAt,
  };
}

function mapList(list, type, nowMs) {
  if (!Array.isArray(list)) return [];
  const sortKey = (e) => Date.parse(e.updatedAt || e.createdAt || "") || 0;
  return list
    .map((x) => mapEvent(x, type, nowMs))
    .filter(Boolean)
    .sort((a, b) => sortKey(b) - sortKey(a))
    .slice(0, MAX_EVENTS);
}

// Maps one parsed feed. Returns null when the body is not a feed object.
export function mapFeed(body, nowMs = Date.now()) {
  if (!body || typeof body !== "object" || Array.isArray(body)) return null;
  return {
    id: typeof body.id === "string" ? body.id : null,
    name: typeof body.name === "string" ? body.name : null,
    incidents: mapList(body.incidents, "incident", nowMs),
    maintenances: mapList(body.maintenances, "maintenance", nowMs),
  };
}

// Reads a response body as text, but stops at maxBytes. Returns null when it is too big.
export async function readCapped(res, maxBytes) {
  const declared = Number(res.headers.get("content-length"));
  if (Number.isFinite(declared) && declared > maxBytes) {
    try { await res.body?.cancel(); } catch { /* ignore */ }
    return null;
  }
  if (!res.body) return "";
  const reader = res.body.getReader();
  const chunks = [];
  let total = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.byteLength;
    if (total > maxBytes) {
      try { await reader.cancel(); } catch { /* ignore */ }
      return null;
    }
    chunks.push(value);
  }
  const all = new Uint8Array(total);
  let at = 0;
  for (const c of chunks) { all.set(c, at); at += c.byteLength; }
  return new TextDecoder().decode(all);
}

// Fetches and maps one feed. Any failure gives null.
export async function fetchFeed(url, nowMs = Date.now()) {
  const ctl = new AbortController();
  const timer = setTimeout(() => ctl.abort(), FEED_TIMEOUT_MS);
  try {
    const res = await fetch(url, { headers: { Accept: "application/json" }, signal: ctl.signal });
    if (res.status !== 200) {
      try { await res.body?.cancel(); } catch { /* ignore */ }
      return null;
    }
    const text = await readCapped(res, FEED_MAX_BYTES);
    if (text === null) return null;
    return mapFeed(JSON.parse(text), nowMs);
  } catch {
    return null;
  } finally {
    clearTimeout(timer);
  }
}
