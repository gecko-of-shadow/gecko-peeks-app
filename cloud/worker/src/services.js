// Other services (v2.2): Valve's relay list and the public status feeds of Discord, PlayStation,
// Xbox and Nintendo. Every source URL is fixed in this file. Each mapper checks the shape of the
// answer and returns a small object, or null when the shape is not the one we know.
// An unknown state value becomes "unknown". It never becomes "none" (no problem).
import { readCapped } from "./riot.js";
import { iso } from "./time.js";

export const SOURCE_TIMEOUT_MS = 5000;
// The PlayStation Europe file was 236 KB on 2026-11-22; Xbox was 149 KB.
export const SOURCE_MAX_BYTES = 768 * 1024;
const MAX_ITEMS = 10;
const MAX_COMPONENTS = 40;
const MAX_TITLE = 200;
const MAX_TEXT = 600;
const MAX_POPS = 120;
const MAX_RELAYS_PER_POP = 4;
// A relay list with fewer usable POPs than this is treated as broken.
const MIN_POPS = 5;

export const SDR_URL = (appId) => `https://api.steampowered.com/ISteamApps/GetSDRConfig/v1/?appid=${appId}`;

// Status feeds by id. The app asks for one of these ids; anything else is a 400.
export const FEEDS = {
  discord: { kind: "statuspage", name: "Discord", url: "https://discordstatus.com/api/v2/summary.json", page: "https://discordstatus.com/" },
  "psn-scea": { kind: "psn", name: "PlayStation Network (Americas)", url: "https://status.playstation.com/data/statuses/region/SCEA.json", page: "https://status.playstation.com/" },
  "psn-scee": { kind: "psn", name: "PlayStation Network (Europe, Middle East, Africa, Oceania)", url: "https://status.playstation.com/data/statuses/region/SCEE.json", page: "https://status.playstation.com/" },
  "psn-sceja": { kind: "psn", name: "PlayStation Network (Japan and Asia)", url: "https://status.playstation.com/data/statuses/region/SCEJA.json", page: "https://status.playstation.com/" },
  xbox: { kind: "xnotify", name: "Xbox network (United States feed)", url: "https://xnotify.xboxlive.com/servicestatusv6/US/en-US", page: "https://support.xbox.com/en-US/xbox-live-status" },
  nintendo: { kind: "nintendo", name: "Nintendo network (United States feed)", url: "https://www.nintendo.co.jp/netinfo/en_US/status.json", page: "https://www.nintendo.co.jp/netinfo/en_US/index.html" },
};

// ---------- small helpers ----------

const isObj = (v) => v !== null && typeof v === "object" && !Array.isArray(v);

function text(v, max = MAX_TEXT) {
  if (typeof v !== "string") return null;
  // Feeds can carry HTML in a message. Keep plain text only.
  const t = v.replace(/<[^>]*>/g, " ").replace(/&nbsp;/g, " ").replace(/\s+/g, " ").trim();
  return t ? t.slice(0, max) : null;
}

function parseV4(s) {
  const m = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(typeof s === "string" ? s : "");
  if (!m) return null;
  const p = m.slice(1).map(Number);
  if (p.some((x, i) => x > 255 || (m[i + 1].length > 1 && m[i + 1][0] === "0"))) return null;
  return p;
}

export function isPublicV4(s) {
  const p = parseV4(s);
  if (!p) return false;
  const [a, b, c] = p;
  if (a === 0 || a === 10 || a === 127 || a >= 224) return false;
  if (a === 100 && b >= 64 && b <= 127) return false;
  if (a === 169 && b === 254) return false;
  if (a === 172 && b >= 16 && b <= 31) return false;
  if (a === 192 && b === 168) return false;
  if (a === 192 && b === 0 && (c === 0 || c === 2)) return false;
  if (a === 192 && b === 88 && c === 99) return false;
  if (a === 198 && (b === 18 || b === 19)) return false;
  if (a === 198 && b === 51 && c === 100) return false;
  if (a === 203 && b === 0 && c === 113) return false;
  return true;
}

// Fetches one fixed source. Returns the parsed JSON, or null on any failure.
export async function fetchSource(url, headers = {}) {
  const ctl = new AbortController();
  const timer = setTimeout(() => ctl.abort(), SOURCE_TIMEOUT_MS);
  try {
    const res = await fetch(url, { headers: { Accept: "application/json", ...headers }, signal: ctl.signal });
    if (res.status !== 200) {
      try { await res.body?.cancel(); } catch { /* ignore */ }
      return null;
    }
    const raw = await readCapped(res, SOURCE_MAX_BYTES);
    if (raw === null) return null;
    // The Nintendo file is sent as octet-stream and can start with a byte order mark.
    return JSON.parse(raw.replace(/^﻿/, ""));
  } catch {
    return null;
  } finally {
    clearTimeout(timer);
  }
}

// ---------- Valve relay list (GetSDRConfig) ----------

// Keeps POPs with a description, valid coordinates and at least one public IPv4 relay.
// geo is [longitude, latitude] in Valve's file.
export function mapSdr(body, appId) {
  if (!isObj(body) || body.success !== true || !isObj(body.pops)) return null;
  const revision = Number(body.revision);
  if (!Number.isSafeInteger(revision) || revision <= 0) return null;
  const pops = [];
  for (const [code, pop] of Object.entries(body.pops)) {
    if (pops.length >= MAX_POPS) break;
    if (!/^[a-z0-9]{2,8}$/.test(code) || !isObj(pop)) continue;
    const desc = text(pop.desc, 80);
    const geo = pop.geo;
    if (!desc || !Array.isArray(geo) || geo.length !== 2) continue;
    const lon = Number(geo[0]);
    const lat = Number(geo[1]);
    if (!Number.isFinite(lat) || !Number.isFinite(lon) || Math.abs(lat) > 90 || Math.abs(lon) > 180) continue;
    if (!Array.isArray(pop.relays)) continue;
    const relays = [];
    for (const r of pop.relays) {
      if (isObj(r) && isPublicV4(r.ipv4) && !relays.includes(r.ipv4)) relays.push(r.ipv4);
      if (relays.length >= MAX_RELAYS_PER_POP) break;
    }
    if (!relays.length) continue;
    pops.push({ code, desc, lat: Math.round(lat * 100) / 100, lon: Math.round(lon * 100) / 100, relays });
  }
  if (pops.length < MIN_POPS) return null;
  pops.sort((a, b) => a.code.localeCompare(b.code));
  return { app: appId, revision, pops };
}

// ---------- status feeds ----------

const SP_INDICATORS = new Set(["none", "minor", "major", "critical", "maintenance"]);
const SP_COMPONENT = new Set(["operational", "degraded_performance", "partial_outage", "major_outage", "under_maintenance"]);
const SP_INCIDENT = new Set(["investigating", "identified", "monitoring", "resolved", "postmortem", "scheduled", "in_progress", "verifying", "completed"]);
const SP_IMPACT = new Set(["none", "minor", "major", "critical", "maintenance"]);

function item({ title, status = null, impact = null, startedAt = null, updatedAt = null, text: body = null, beginText = null, endText = null }) {
  return { title, status, impact, startedAt, updatedAt, text: body, beginText, endText };
}

function spItems(list) {
  if (!Array.isArray(list)) return [];
  const out = [];
  for (const x of list) {
    if (!isObj(x)) continue;
    const title = text(x.name, MAX_TITLE);
    if (!title) continue;
    const updates = Array.isArray(x.incident_updates) ? x.incident_updates.filter(isObj) : [];
    out.push(item({
      title,
      status: SP_INCIDENT.has(x.status) ? x.status : "unknown",
      impact: SP_IMPACT.has(x.impact) ? x.impact : "unknown",
      startedAt: iso(x.started_at || x.scheduled_for || x.created_at),
      updatedAt: iso(x.updated_at),
      text: updates.length ? text(updates[0].body) : null,
    }));
    if (out.length >= MAX_ITEMS) break;
  }
  return out;
}

// Atlassian Statuspage summary.json (Discord).
export function mapStatuspage(body) {
  if (!isObj(body) || !isObj(body.page) || typeof body.page.id !== "string" || !isObj(body.status)
    || !Array.isArray(body.components)) return null;
  const groups = new Map();
  for (const c of body.components) if (isObj(c) && c.group === true && typeof c.id === "string") groups.set(c.id, text(c.name, 80));
  const components = [];
  for (const c of body.components) {
    if (!isObj(c) || c.group === true) continue;
    const name = text(c.name, 80);
    if (!name) continue;
    components.push({ name, group: groups.get(c.group_id) || null, status: SP_COMPONENT.has(c.status) ? c.status : "unknown" });
    if (components.length >= MAX_COMPONENTS) break;
  }
  return {
    indicator: SP_INDICATORS.has(body.status.indicator) ? body.status.indicator : "unknown",
    description: text(body.status.description, MAX_TITLE),
    incidents: spItems(body.incidents),
    maintenances: spItems(body.scheduled_maintenances).filter((m) => m.status !== "completed"),
    components,
  };
}

function psnMessage(m) {
  if (!isObj(m)) return null;
  const msgs = isObj(m.messages) ? m.messages : {};
  // Some items have only one locale. Use en-US, then any English, then any text, then the key.
  const en = msgs["en-US"] ?? Object.entries(msgs).find(([k]) => /^en/i.test(k))?.[1] ?? Object.values(msgs).find((v) => typeof v === "string");
  return text(en) || text(m.messageKey, MAX_TITLE);
}

function psnItems(list, title, out) {
  if (!Array.isArray(list)) return false;
  for (const s of list) {
    if (!isObj(s)) continue;
    if (out.length >= MAX_ITEMS) return true;
    out.push(item({
      title,
      status: text(s.statusType, 40) || "unknown",
      startedAt: iso(s.startDate),
      updatedAt: iso(s.modifiedDate || s.createdDate),
      text: psnMessage(s.message),
    }));
  }
  return true;
}

// PlayStation region file. Region-wide items, plus the items of the caller's own country.
export function mapPsn(body, country) {
  if (!isObj(body) || typeof body.regionName !== "string" || !Array.isArray(body.status) || !Array.isArray(body.countries)) return null;
  const incidents = [];
  psnItems(body.status, `Listed for the whole ${body.regionName} region`, incidents);
  const cc = typeof country === "string" ? country.toUpperCase() : null;
  const mine = cc ? body.countries.find((c) => isObj(c) && c.countryCode === cc) : null;
  if (mine) {
    psnItems(mine.status, text(mine.countryName, 60) || cc, incidents);
    for (const svc of Array.isArray(mine.services) ? mine.services : []) {
      if (!isObj(svc)) continue;
      const name = text(svc.serviceName, 80) || text(svc.serviceId, 80) || "A service";
      psnItems(svc.status, name, incidents);
      for (const r of Array.isArray(svc.resources) ? svc.resources : []) {
        if (isObj(r)) psnItems(r.status, name + ": " + (text(r.resourceName, 80) || "part"), incidents);
      }
    }
  }
  return {
    indicator: incidents.length ? "listed" : "none",
    description: incidents.length ? `${incidents.length} item(s) listed` : "No problems listed",
    country: mine ? cc : null,
    incidents,
    maintenances: [],
    components: [],
  };
}

// Xbox xnotify v6 JSON. Only "None" is a known state (no problem). Any other state name is shown
// as the feed wrote it, and the overall state becomes "listed" (the feed lists a problem).
export function mapXnotify(body) {
  if (!isObj(body) || !isObj(body.Status) || !isObj(body.Status.Overall) || typeof body.Status.Overall.State !== "string"
    || !Array.isArray(body.CoreServices)) return null;
  const incidents = [];
  for (const group of [...body.CoreServices, ...(Array.isArray(body.Titles) ? body.Titles : [])]) {
    if (!isObj(group)) continue;
    const gname = text(group.Name, 80) || "Xbox";
    for (const sc of Array.isArray(group.Scenarios) ? group.Scenarios : []) {
      const state = isObj(sc) && isObj(sc.Status) && typeof sc.Status.Name === "string" ? sc.Status.Name : null;
      if (state === "None") continue;
      if (incidents.length >= MAX_ITEMS) break;
      incidents.push(item({ title: `${gname}: ${text(sc?.Name, 80) || "a feature"}`, status: text(state, 40) || "unknown", text: text(sc?.Description) }));
    }
  }
  const overall = body.Status.Overall.State;
  return {
    indicator: incidents.length ? "listed" : overall === "None" ? "none" : "unknown",
    description: overall === "None" && !incidents.length ? "No problems listed" : `Overall state: ${text(overall, 40) || "unknown"}`,
    updatedAt: iso(body.Status.Overall.LastUpdated),
    incidents,
    maintenances: [],
    components: [],
  };
}

// Nintendo netinfo status.json. Outages: event_status "1" = ongoing, "0" = ended (dropped).
// Maintenance: "0" = scheduled, "1" or "2" = in progress, "3" = completed (dropped).
// begin and end are local text with odd spaces; they are kept as text, not parsed.
export function mapNintendo(body) {
  if (!isObj(body) || !Array.isArray(body.operational_statuses) || !Array.isArray(body.temporary_maintenances)) return null;
  const one = (e, status) => item({
    title: text(e.software_title, MAX_TITLE) || (Array.isArray(e.services) ? text(e.services.join(", "), MAX_TITLE) : null) || "Nintendo network service",
    status,
    text: text(e.message),
    beginText: text(e.begin, 80),
    endText: text(e.end, 80),
  });
  // A status code the Worker does not know is kept as "unknown", so it can never read as "no problem".
  let unknown = 0;
  const incidents = [];
  for (const e of body.operational_statuses) {
    if (!isObj(e) || incidents.length >= MAX_ITEMS) continue;
    const s = String(e.event_status);
    if (s === "1") incidents.push(one(e, "ongoing"));
    else if (s !== "0") { incidents.push(one(e, "unknown")); unknown++; }
  }
  const maintenances = [];
  for (const e of body.temporary_maintenances) {
    if (!isObj(e) || maintenances.length >= MAX_ITEMS) continue;
    const s = String(e.event_status);
    if (s === "1" || s === "2") maintenances.push(one(e, "in_progress"));
    else if (s === "0") maintenances.push(one(e, "scheduled"));
    else if (s !== "3") { maintenances.push(one(e, "unknown")); unknown++; }
  }
  const active = maintenances.some((m) => m.status === "in_progress");
  const ongoing = incidents.filter((i) => i.status === "ongoing").length;
  return {
    indicator: ongoing ? "listed" : unknown ? "unknown" : active ? "maintenance" : "none",
    description: ongoing ? `${ongoing} ongoing problem(s) listed`
      : unknown ? "The feed lists an item with a state this app does not know" : active ? "Maintenance in progress" : "No ongoing problems listed",
    timesAre: "Times are as Nintendo writes them (Pacific Time on the en_US page).",
    incidents,
    maintenances,
    components: [],
  };
}

// One feed: fetch and map. Returns the mapped object or null.
export async function fetchFeedStatus(id, country) {
  const feed = FEEDS[id];
  if (!feed) return null;
  const body = await fetchSource(feed.url);
  if (body === null) return null;
  try {
    switch (feed.kind) {
      case "statuspage": return mapStatuspage(body);
      case "psn": return mapPsn(body, country);
      case "xnotify": return mapXnotify(body);
      case "nintendo": return mapNintendo(body);
      default: return null;
    }
  } catch {
    return null;
  }
}
