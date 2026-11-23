import { handleLatest, handleDownload, handleUploadPart, handleFinalize, handlePromote, handleVersions, handleDeleteRelease } from "./update.js";
import { LOL_STATUS_BASE, VALORANT_STATUS_BASE, fetchFeed, readCapped } from "./riot.js";
import { iso } from "./time.js";
import { FEEDS, SDR_URL, fetchSource, mapSdr, fetchFeedStatus } from "./services.js";
import refdata from "../data/refdata.json" with { type: "json" };

// Gecko Peeks API Worker (gecko-peeks-api).
// It reads a fixed set of Cloudflare Radar endpoints, Riot's status feeds and (v2.2) Valve's relay
// list and other public status feeds, and returns small JSON documents for the Gecko Peeks desktop
// app. It is not a proxy: every source path is fixed in code and every input is validated first.

const VERSION = "2.2.1";
// Computed once per isolate. The ETag follows the refdata version string.
const REFDATA_BODY = JSON.stringify(refdata);
const REFDATA_ETAG = `"rd-${refdata.version}"`;
const REFDATA_CACHE_CONTROL = "public, max-age=86400";
const RIOT_STATUS_TTL = 60;
// v2.2 services. The relay list changes rarely; status feeds change in minutes.
const SDR_TTL = 21600;
const SERVICE_STATUS_TTL = 120;
// Valve app ids the relay route accepts: only the ones a refdata service names.
const SDR_APPS = new Set((Array.isArray(refdata.services) ? refdata.services : [])
  .filter((s) => s && s.target && s.target.kind === "sdr-nearest-pop" && Number.isSafeInteger(s.target.appId))
  .map((s) => s.target.appId));
const REL_TTL = 86400;
// A failed rel lookup (too large, Radar error) is remembered this long.
const REL_FAIL_TTL = 3600;
const MAX_UPSTREAM_PROVIDERS = 12;
const MAX_UPSTREAM_PEERS = 8;
// A transit network's rel list can be several MB (AS6939: about 2.7 MB). Parsing that would
// break the 10 ms CPU limit, so a bigger body is treated as a failed call (upstreams null).
// 1 MB parses and maps in about 2.5 ms; large eyeball ISPs are well under 300 KB.
const REL_MAX_BYTES = 1024 * 1024;
const REGIONS = new Map(refdata.regions.map((r) => [r.id, r]));
// ASN -> refdata provider. Peers are kept only for upstream and backbone providers.
const PROVIDER_BY_ASN = new Map();
for (const p of refdata.providers) for (const a of p.asns) PROVIDER_BY_ASN.set(a, p);
const RADAR_BASE = "https://api.cloudflare.com/client/v4";
const RIOT_ASN = 6507;
const RIOT_PREFIX = "192.207.0.0/24";
const CALL_TIMEOUT_MS = 6000;
const PULSE_TTL = 300;
const ASN_TTL = 86400;
// The app sends at most 15. At 15 every IP fits the cached path (3 subrequests each).
const MAX_IPS = 15;
const MAX_EVENTS = 25;
// Free Workers get 50 subrequests per request. Cache API calls count too.
const SUBREQUEST_BUDGET = 45;

// ---------- responses ----------

function json(body, status = 200, cacheControl = "no-store", extra = {}) {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      "Content-Type": "application/json; charset=utf-8",
      "Cache-Control": cacheControl,
      "X-Content-Type-Options": "nosniff",
      ...extra,
    },
  });
}

const badRequest = (detail) => json({ error: "bad_request", detail }, 400);

// Radar calls spend one shared token quota. The RATE_LIMITER binding (when it is
// bound) limits each caller IP. Without the binding every call is allowed.
// The exe download uses its own DOWNLOAD_LIMITER bucket, so a pulse or asn poll (or other
// installs behind one NAT) cannot use up the budget of an update download.
async function rateLimited(request, env, binding = "RATE_LIMITER") {
  const limiter = env && env[binding];
  if (!limiter || typeof limiter.limit !== "function") return false;
  const key = request.headers.get("cf-connecting-ip") || "unknown";
  try {
    const { success } = await limiter.limit({ key });
    return !success;
  } catch {
    return false;
  }
}

const tooMany = () => json({ error: "rate_limited" }, 429, "no-store", { "Retry-After": "60" });

// ---------- validation ----------

function parseAsn(text) {
  if (typeof text !== "string" || !/^(?:AS)?[0-9]{1,10}$/i.test(text.trim())) return null;
  const n = Number(text.trim().replace(/^AS/i, ""));
  return Number.isSafeInteger(n) && n >= 1 && n <= 4294967295 ? n : null;
}

function parseCountry(text) {
  if (typeof text !== "string") return null;
  const t = text.trim().toUpperCase();
  return /^[A-Z]{2}$/.test(t) ? t : null;
}

function parseIPv4(s) {
  const m = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(s);
  if (!m) return null;
  const p = m.slice(1).map(Number);
  if (p.some((x, i) => x > 255 || (m[i + 1].length > 1 && m[i + 1][0] === "0"))) return null;
  return p;
}

function isPublicIPv4(p) {
  const [a, b, c] = p;
  if (a === 0 || a === 10 || a === 127) return false;          // this-net, private, loopback
  if (a === 100 && b >= 64 && b <= 127) return false;           // CGNAT 100.64/10
  if (a === 169 && b === 254) return false;                     // link-local
  if (a === 172 && b >= 16 && b <= 31) return false;            // private
  if (a === 192 && b === 168) return false;                     // private
  if (a === 192 && b === 0 && (c === 0 || c === 2)) return false; // IETF, TEST-NET-1
  if (a === 192 && b === 88 && c === 99) return false;          // 6to4 relay
  if (a === 198 && (b === 18 || b === 19)) return false;        // benchmarking
  if (a === 198 && b === 51 && c === 100) return false;         // TEST-NET-2
  if (a === 203 && b === 0 && c === 113) return false;          // TEST-NET-3
  if (a >= 224) return false;                                   // multicast, reserved, broadcast
  return true;
}

// Returns 8 16-bit groups, or null.
function parseIPv6(s) {
  if (!/^[0-9a-f:.]+$/i.test(s) || s.length > 45) return null;
  let str = s;
  if (s.includes(".")) {
    // Turn an embedded IPv4 tail ("::ffff:1.2.3.4") into two hex groups.
    const lastColon = s.lastIndexOf(":");
    const v4 = parseIPv4(s.slice(lastColon + 1));
    if (!v4) return null;
    str = s.slice(0, lastColon + 1) + ((v4[0] << 8) | v4[1]).toString(16) + ":" + ((v4[2] << 8) | v4[3]).toString(16);
  }
  const halves = str.split("::");
  if (halves.length > 2) return null;
  const toGroups = (h) => (h === "" ? [] : h.split(":"));
  const head = toGroups(halves[0]);
  const rest = halves.length === 2 ? toGroups(halves[1]) : [];
  const all = [...head, ...rest];
  if (all.some((g) => !/^[0-9a-f]{1,4}$/.test(g))) return null;
  const nums = (arr) => arr.map((g) => parseInt(g, 16));
  if (halves.length === 1) return all.length === 8 ? nums(all) : null;
  if (all.length > 7) return null;
  return [...nums(head), ...new Array(8 - all.length).fill(0), ...nums(rest)];
}

function isPublicIPv6(g) {
  // Only global unicast 2000::/3, minus documentation and special blocks.
  if (g[0] < 0x2000 || g[0] > 0x3fff) return false;
  if (g[0] === 0x2001 && g[1] === 0x0db8) return false;  // documentation
  if (g[0] === 0x2001 && g[1] < 0x0200) return false;    // 2001::/23 IETF (Teredo, ORCHID, ...)
  if (g[0] === 0x2002) return false;                     // 6to4
  if (g[0] === 0x3fff && g[1] < 0x1000) return false;    // 3fff::/20 documentation
  return true;
}

// Returns { ip, public } with a normalized ip string, or null when the text is not an IP.
function classifyIp(text) {
  const s = String(text || "").trim().toLowerCase();
  if (!s) return null;
  const v4 = parseIPv4(s);
  if (v4) return { ip: v4.join("."), public: isPublicIPv4(v4) };
  if (s.includes(":")) {
    const v6 = parseIPv6(s);
    if (v6) return { ip: v6.map((x) => x.toString(16)).join(":"), public: isPublicIPv6(v6) };
  }
  return null;
}

// ---------- Radar calls ----------

async function radar(env, path, query) {
  const url = new URL(RADAR_BASE + path);
  for (const [k, v] of Object.entries(query || {})) {
    if (v !== undefined && v !== null) url.searchParams.set(k, String(v));
  }
  const ctl = new AbortController();
  const timer = setTimeout(() => ctl.abort(), CALL_TIMEOUT_MS);
  try {
    const res = await fetch(url.toString(), {
      headers: { Authorization: `Bearer ${env.RADAR_TOKEN}`, Accept: "application/json" },
      signal: ctl.signal,
    });
    if (!res.ok) throw new Error(`radar ${path} ${res.status}`);
    const body = await res.json();
    if (!body || body.success === false || !body.result) throw new Error(`radar ${path} failed`);
    return body.result;
  } finally {
    clearTimeout(timer);
  }
}

// Like radar(), but reads at most maxBytes of the body. Over the cap it throws.
async function radarCapped(env, path, maxBytes) {
  const ctl = new AbortController();
  const timer = setTimeout(() => ctl.abort(), CALL_TIMEOUT_MS);
  try {
    const res = await fetch(RADAR_BASE + path, {
      headers: { Authorization: `Bearer ${env.RADAR_TOKEN}`, Accept: "application/json" },
      signal: ctl.signal,
    });
    if (!res.ok) {
      try { await res.body?.cancel(); } catch { /* ignore */ }
      throw new Error(`radar ${path} ${res.status}`);
    }
    const text = await readCapped(res, maxBytes);
    if (text === null) throw new Error(`radar ${path} too large`);
    const body = JSON.parse(text);
    if (!body || body.success === false || !body.result) throw new Error(`radar ${path} failed`);
    return body.result;
  } finally {
    clearTimeout(timer);
  }
}

// Runs named async jobs in parallel. A failed or rejected job becomes null.
async function settleAll(jobs) {
  const names = Object.keys(jobs);
  const results = await Promise.allSettled(names.map((n) => jobs[n]()));
  const out = {};
  names.forEach((n, i) => {
    out[n] = results[i].status === "fulfilled" ? results[i].value : null;
  });
  return out;
}

// ---------- mapping ----------

const radarAsUrl = (asn) => (asn ? `https://radar.cloudflare.com/as${asn}` : null);
const radarRoutingUrl = (asn) => (asn ? `https://radar.cloudflare.com/routing/as${asn}` : null);
const radarCountryUrl = (cc) => (cc ? `https://radar.cloudflare.com/${cc.toLowerCase()}` : null);
const words = (s) => String(s || "").toLowerCase().replace(/_/g, " ");

function byStartDesc(a, b) {
  return (b.start || "").localeCompare(a.start || "");
}

function finishEvents(list) {
  return list.filter((e) => e.start).sort(byStartDesc).slice(0, MAX_EVENTS);
}

function mapOutages(result, ctx = {}) {
  const items = Array.isArray(result?.annotations) ? result.annotations : [];
  return finishEvents(items.map((a) => {
    const parts = [];
    if (a.outage?.outageType) parts.push(`Type: ${words(a.outage.outageType)}`);
    if (a.outage?.outageCause) parts.push(`cause: ${words(a.outage.outageCause)}`);
    const where = [
      ...(a.asnsDetails || []).map((x) => `${x.name} (AS${x.asn})`),
      ...(a.locationsDetails || []).map((x) => x.name),
    ];
    if (where.length) parts.push(`scope: ${where.join(", ")}`);
    return {
      kind: "outage",
      start: iso(a.startDate),
      end: iso(a.endDate),
      title: a.description || "Internet outage",
      detail: parts.length ? parts.join("; ") : null,
      url: ctx.asn ? radarAsUrl(ctx.asn) : ctx.country ? radarCountryUrl(ctx.country) : "https://radar.cloudflare.com/outage-center",
    };
  }));
}

function mapAnomalies(result, ctx = {}) {
  const items = Array.isArray(result?.trafficAnomalies) ? result.trafficAnomalies : [];
  return finishEvents(items.map((a) => {
    let where = "an unknown network";
    let url = null;
    if (a.asnDetails) {
      where = `${a.asnDetails.name} (AS${a.asnDetails.asn})`;
      url = radarAsUrl(a.asnDetails.asn);
    } else if (a.locationDetails) {
      where = a.locationDetails.name || a.locationDetails.code;
      url = radarCountryUrl(a.locationDetails.code);
    } else if (a.originDetails) {
      where = a.originDetails.name || a.originDetails.origin || where;
    }
    const parts = [];
    if (a.status) parts.push(`Status: ${words(a.status)}`);
    if (Array.isArray(a.visibleInDataSources) && a.visibleInDataSources.length) {
      parts.push(`seen in ${a.visibleInDataSources.join(", ")} data`);
    }
    return {
      kind: "anomaly",
      start: iso(a.startDate),
      end: iso(a.endDate),
      title: `Traffic anomaly in ${where}`,
      detail: parts.length ? parts.join("; ") : null,
      url: url || (ctx.asn ? radarAsUrl(ctx.asn) : radarCountryUrl(ctx.country)),
    };
  }));
}

function orgMap(result) {
  const m = new Map();
  for (const x of result?.asn_info || []) m.set(Number(x.asn), x.org_name);
  return m;
}

const asLabel = (asn, orgs) => (orgs.get(Number(asn)) ? `AS${asn} (${orgs.get(Number(asn))})` : `AS${asn}`);

function mapHijacks(result, ctx = {}) {
  const items = Array.isArray(result?.events) ? result.events : [];
  const orgs = orgMap(result);
  return finishEvents(items.map((e) => {
    const prefixes = Array.isArray(e.prefixes) ? e.prefixes : [];
    const victims = (e.victim_asns || []).map((a) => asLabel(a, orgs));
    const parts = [];
    if (victims.length) parts.push(`Victim: ${victims.join(", ")}`);
    if (prefixes.length > 1) parts.push(`${prefixes.length} prefixes`);
    if (e.confidence_score !== undefined) parts.push(`confidence ${e.confidence_score}`);
    if (e.peer_ip_count !== undefined) parts.push(`seen by ${e.peer_ip_count} peers`);
    const ongoing = Number(e.on_going_count) > 0;
    return {
      kind: "hijack",
      start: iso(e.min_hijack_ts),
      end: ongoing ? null : iso(e.max_hijack_ts),
      title: `Possible hijack of ${prefixes[0] || "a prefix"} by ${asLabel(e.hijacker_asn, orgs)}`,
      detail: parts.length ? parts.join("; ") : null,
      url: radarRoutingUrl(ctx.asn || e.hijacker_asn),
    };
  }));
}

function mapLeaks(result, ctx = {}) {
  const items = Array.isArray(result?.events) ? result.events : [];
  const orgs = orgMap(result);
  return finishEvents(items.map((e) => {
    const parts = [];
    if (Array.isArray(e.leak_seg) && e.leak_seg.length) parts.push(`Path: ${e.leak_seg.map((a) => `AS${a}`).join(" > ")}`);
    if (e.prefix_count !== undefined) parts.push(`${e.prefix_count} prefixes`);
    if (e.origin_count !== undefined) parts.push(`${e.origin_count} origins`);
    if (e.peer_count !== undefined) parts.push(`seen by ${e.peer_count} peers`);
    return {
      kind: "leak",
      start: iso(e.min_ts || e.detected_ts),
      end: e.finished ? iso(e.max_ts) : null,
      title: `Route leak by ${asLabel(e.leak_asn, orgs)}`,
      detail: parts.length ? parts.join("; ") : null,
      url: radarRoutingUrl(ctx.asn || e.leak_asn),
    };
  }));
}

function mapTraffic(result) {
  const s = result?.serie_0;
  if (!s || !Array.isArray(s.timestamps) || !Array.isArray(s.values)) return null;
  const n = Math.min(s.timestamps.length, s.values.length);
  const ts = [];
  const raw = [];
  for (let i = 0; i < n; i++) {
    const t = iso(s.timestamps[i]);
    const v = Number(s.values[i]);
    if (t && Number.isFinite(v)) { ts.push(t); raw.push(v); }
  }
  if (!ts.length) return null;
  const max = Math.max(...raw);
  const values = raw.map((v) => (max > 0 ? Math.round((v / max) * 10000) / 10000 : 0));
  return { timestamps: ts, values };
}

function mapPrefix(result) {
  const o = result?.prefix_origins?.[0];
  if (!o) return null;
  return {
    prefix: o.prefix || RIOT_PREFIX,
    origin: Number(o.origin),
    rpki: o.rpki_validation || "Unknown",
    peers: Number(o.peer_count) || 0,
    totalPeers: Number(result?.meta?.total_peers) || 0,
  };
}

function mapIqi(result) {
  const s = result?.summary_0;
  if (!s) return null;
  const num = (v) => (Number.isFinite(Number(v)) && v !== null && v !== "" ? Math.round(Number(v) * 100) / 100 : null);
  const out = { p25: num(s.p25), p50: num(s.p50), p75: num(s.p75) };
  return out.p50 === null && out.p25 === null && out.p75 === null ? null : out;
}

// Radar names some networks only "ASN852". Use the longer "aka" name then.
function entityName(result) {
  const a = result?.asn;
  if (!a) return null;
  if (a.name && !/^ASN?\d+$/i.test(a.name)) return a.name;
  return a.aka || a.name || null;
}

// Radar /radar/entities/asns/{asn}/rel rows (checked live 2026-11-21):
// { asn1, asn1_name, asn2, asn2_name, rel } with rel "provider-customer" (asn1 is the
// provider of asn2) or "peer". The asked ASN can be on either side, and one pair can have
// both a provider-customer row and a peer row. The short forms P2C, C2P and P2P are read too.
function relKind(rel, ispIsAsn1) {
  const r = String(rel || "").toLowerCase();
  if (r === "peer" || r === "p2p") return "peer";
  if (r === "provider-customer" || r === "p2c") return ispIsAsn1 ? null : "provider";
  if (r === "customer-provider" || r === "c2p") return ispIsAsn1 ? "provider" : null;
  return null;
}

function mapRels(result, isp) {
  const rows = Array.isArray(result?.rels) ? result.rels : [];
  const found = new Map(); // asn -> { asn, name, rel }
  for (const row of rows) {
    const a1 = Number(row?.asn1);
    const a2 = Number(row?.asn2);
    let other;
    let name;
    let kind;
    if (a1 === isp && a2 !== isp) {
      other = a2; name = row.asn2_name; kind = relKind(row.rel, true);
    } else if (a2 === isp && a1 !== isp) {
      other = a1; name = row.asn1_name; kind = relKind(row.rel, false);
    } else {
      continue;
    }
    if (!kind || !Number.isSafeInteger(other) || other < 1 || other > 4294967295) continue;
    const provider = PROVIDER_BY_ASN.get(other);
    if (kind === "peer" && !(provider && (provider.tier === "upstream" || provider.tier === "backbone"))) continue;
    const prev = found.get(other);
    if (prev && (prev.rel === "provider" || kind === "peer")) continue; // provider wins over peer
    found.set(other, { asn: other, name: strOrNull(name) || (provider ? provider.name : null), rel: kind });
  }
  const all = [...found.values()].sort((x, y) => x.asn - y.asn);
  return [
    ...all.filter((x) => x.rel === "provider").slice(0, MAX_UPSTREAM_PROVIDERS),
    ...all.filter((x) => x.rel === "peer").slice(0, MAX_UPSTREAM_PEERS),
  ];
}

// The ISP's upstreams, cached per ISP for a day. Cost: match, Radar call, put (at most 3).
// Returns null when the Radar call fails. A failure is cached for REL_FAIL_TTL too: a large
// network's rel list is always over REL_MAX_BYTES, and each retry would stream up to 1 MB and
// spend one Radar call on every pulse cache miss.
async function ispUpstreams(request, env, ctx, isp) {
  const cache = getCache();
  const key = cacheKey(request, `/__cache/v1/rel/${isp}`);
  if (cache) {
    try {
      const hit = await cache.match(key);
      if (hit) {
        const v = await hit.json();
        if (Array.isArray(v)) return v;
        if (v && v.failed === true) return null;
      }
    } catch {
      // A broken cache entry is a miss.
    }
  }
  let upstreams;
  try {
    upstreams = mapRels(await radarCapped(env, `/radar/entities/asns/${isp}/rel`, REL_MAX_BYTES), isp);
  } catch {
    if (cache) ctx?.waitUntil?.(cache.put(key, json({ failed: true }, 200, `public, max-age=${REL_FAIL_TTL}`)));
    return null;
  }
  if (cache) ctx?.waitUntil?.(cache.put(key, json(upstreams, 200, `public, max-age=${REL_TTL}`)));
  return upstreams;
}

// ---------- Radar groups ----------

function asnJobs(env, asn) {
  return {
    name: async () => entityName(await radar(env, `/radar/entities/asns/${asn}`)),
    traffic: async () => mapTraffic(await radar(env, "/radar/netflows/timeseries", { asn, dateRange: "1d", aggInterval: "1h" })),
    outages: async () => mapOutages(await radar(env, "/radar/annotations/outages", { asn, dateRange: "7d", limit: MAX_EVENTS }), { asn }),
    anomalies: async () => mapAnomalies(await radar(env, "/radar/traffic_anomalies", { asn, dateRange: "7d", limit: MAX_EVENTS }), { asn }),
    hijacks: async () => mapHijacks(await radar(env, "/radar/bgp/hijacks/events", { involvedAsn: asn, dateRange: "7d", per_page: MAX_EVENTS, sortBy: "TIME", sortOrder: "DESC" }), { asn }),
    leaks: async () => mapLeaks(await radar(env, "/radar/bgp/leaks/events", { involvedAsn: asn, dateRange: "7d", per_page: MAX_EVENTS, sortBy: "TIME", sortOrder: "DESC" }), { asn }),
  };
}

const prefixJob = (jobs, name, fn) => {
  const out = {};
  for (const [k, v] of Object.entries(jobs)) out[`${name}.${k}`] = v;
  return { ...out, ...fn };
};

// riotCached: a riot block from the cache. Then the Riot calls are skipped.
async function buildPulse(env, isp, country, now = new Date(), riotCached = null) {
  const hasRadar = Boolean(env && env.RADAR_TOKEN);
  const pulse = {
    time: now.toISOString(),
    radar: hasRadar,
    riot: { asn: RIOT_ASN, name: null, prefix: null, traffic: null, outages: null, anomalies: null, hijacks: null, leaks: null },
    isp: { asn: isp, name: null, traffic: null, outages: null, anomalies: null, hijacks: null, leaks: null, upstreams: null },
    country: { code: country, outages: null, iqi: null },
  };
  if (!hasRadar) return { pulse, anyOk: false };

  let jobs = {};
  if (riotCached) {
    pulse.riot = { ...pulse.riot, ...riotCached, asn: RIOT_ASN };
  } else {
    jobs = prefixJob(asnJobs(env, RIOT_ASN), "riot", {
      "riot.prefix": async () => mapPrefix(await radar(env, "/radar/bgp/routes/pfx2as", { prefix: RIOT_PREFIX })),
    });
  }
  if (isp && isp !== RIOT_ASN) jobs = { ...jobs, ...prefixJob(asnJobs(env, isp), "isp", {}) };
  if (country) {
    jobs["country.outages"] = async () => mapOutages(await radar(env, "/radar/annotations/outages", { location: country, dateRange: "7d", limit: MAX_EVENTS }), { country });
    jobs["country.latency"] = async () => mapIqi(await radar(env, "/radar/quality/iqi/summary", { metric: "latency", location: country, dateRange: "7d" }));
    jobs["country.bandwidth"] = async () => mapIqi(await radar(env, "/radar/quality/iqi/summary", { metric: "bandwidth", location: country, dateRange: "7d" }));
  }

  const r = await settleAll(jobs);
  let anyOk = Boolean(riotCached);
  for (const [key, value] of Object.entries(r)) {
    if (value !== null) anyOk = true;
    const [group, field] = key.split(".");
    if (group === "riot" || group === "isp") pulse[group][field] = value;
  }
  if (isp === RIOT_ASN) {
    for (const f of ["name", "traffic", "outages", "anomalies", "hijacks", "leaks"]) pulse.isp[f] = pulse.riot[f];
  }
  if (country) {
    pulse.country.outages = r["country.outages"];
    const lat = r["country.latency"];
    const bw = r["country.bandwidth"];
    pulse.country.iqi = lat || bw ? { latency: lat, bandwidth: bw } : null;
  }
  const riotOk = !riotCached && Object.keys(r).some((k) => k.startsWith("riot.") && r[k] !== null);
  return { pulse, anyOk, riotOk };
}

// ---------- handlers ----------

function cacheKey(request, path) {
  return new Request(new URL(path, new URL(request.url).origin).toString(), { method: "GET" });
}

function getCache() {
  try {
    return globalThis.caches && globalThis.caches.default ? globalThis.caches.default : null;
  } catch {
    return null;
  }
}

function handleHealth(env) {
  return json({ ok: true, version: VERSION, radar: Boolean(env && env.RADAR_TOKEN), refdata: refdata.version, time: new Date().toISOString() });
}

function numOrNull(v) {
  if (v === null || v === undefined || v === "") return null;
  const n = Number(v);
  return Number.isFinite(n) ? n : null;
}
const strOrNull = (v) => (typeof v === "string" && v.length ? v : null);

function handleEdge(request) {
  const cf = request.cf || {};
  return json({
    asn: numOrNull(cf.asn),
    asOrg: strOrNull(cf.asOrganization),
    colo: strOrNull(cf.colo),
    city: strOrNull(cf.city),
    region: strOrNull(cf.region),
    country: strOrNull(cf.country),
    continent: strOrNull(cf.continent),
    lat: numOrNull(cf.latitude),
    lon: numOrNull(cf.longitude),
    timezone: strOrNull(cf.timezone),
    httpProtocol: strOrNull(cf.httpProtocol),
    tlsVersion: strOrNull(cf.tlsVersion),
    time: new Date().toISOString(),
  });
}

// True when an If-None-Match header names the current refdata ETag (or "*").
function refdataEtagMatches(header) {
  if (!header) return false;
  return header.split(",").some((part) => {
    const tag = part.trim().replace(/^W\//, "");
    return tag === "*" || tag === REFDATA_ETAG;
  });
}

// GET /v1/refdata: the reference data file. No rate limit and no subrequests.
function handleRefdata(request, url) {
  if ([...url.searchParams.keys()].length) return badRequest("no parameters allowed");
  const headers = { ETag: REFDATA_ETAG, "Cache-Control": REFDATA_CACHE_CONTROL };
  if (refdataEtagMatches(request.headers.get("if-none-match"))) {
    return new Response(null, { status: 304, headers });
  }
  return new Response(REFDATA_BODY, {
    status: 200,
    headers: { "Content-Type": "application/json; charset=utf-8", "X-Content-Type-Options": "nosniff", ...headers },
  });
}

// Region id or alias -> canonical region, or null.
function resolveRegion(text) {
  if (typeof text !== "string") return null;
  const t = text.trim().toLowerCase();
  if (!/^[a-z]{2,3}\d?$/.test(t)) return null;
  const id = Object.hasOwn(refdata.aliases, t) ? refdata.aliases[t] : t;
  return REGIONS.get(id) || null;
}

// GET /v1/riot-status?region=<id>. At most 4 subrequests: match, 2 feeds, put.
async function handleRiotStatus(request, env, ctx, url) {
  for (const k of url.searchParams.keys()) {
    if (k !== "region") return badRequest(`unknown parameter: ${k}`);
  }
  if (!url.searchParams.has("region")) return badRequest("region is required");
  const region = resolveRegion(url.searchParams.get("region"));
  if (!region) return badRequest("unknown region");
  if (await rateLimited(request, env)) return tooMany();

  const cache = getCache();
  const key = cacheKey(request, `/__cache/v1/riot-status/${region.id}`);
  if (cache) {
    try {
      const hit = await cache.match(key);
      if (hit) {
        const res = new Response(hit.body, hit);
        res.headers.set("X-Gecko-Cache", "HIT");
        return res;
      }
    } catch {
      // A cache failure is a miss.
    }
  }
  const now = Date.now();
  const [lol, valorant] = await Promise.all([
    fetchFeed(`${LOL_STATUS_BASE}${region.lolStatusId}.json`, now),
    fetchFeed(`${VALORANT_STATUS_BASE}${region.valorantShard}.json`, now),
  ]);
  const body = {
    region: region.id,
    lolStatusId: region.lolStatusId,
    valorantShard: region.valorantShard,
    fetchedAt: new Date(now).toISOString(),
    lol,
    valorant,
  };
  if (lol === null && valorant === null) return json(body, 200, "no-store", { "X-Gecko-Cache": "BYPASS" });
  const res = json(body, 200, `public, max-age=${RIOT_STATUS_TTL}`, { "X-Gecko-Cache": "MISS" });
  if (cache) ctx?.waitUntil?.(cache.put(key, res.clone()));
  return res;
}

// A cached answer for key, or null. A cache failure is a miss.
async function cachedResponse(cache, key) {
  if (!cache) return null;
  try {
    const hit = await cache.match(key);
    if (!hit) return null;
    const res = new Response(hit.body, hit);
    res.headers.set("X-Gecko-Cache", "HIT");
    return res;
  } catch {
    return null;
  }
}

// GET /v1/sdr?app=<appid>: Valve's relay list (GetSDRConfig), cut down to POPs with relays.
// At most 3 subrequests: match, fetch, put. A broken or unknown answer is 502 and not cached.
async function handleSdr(request, env, ctx, url) {
  for (const k of url.searchParams.keys()) {
    if (k !== "app") return badRequest(`unknown parameter: ${k}`);
  }
  const raw = url.searchParams.get("app") || "";
  const app = /^[0-9]{1,7}$/.test(raw) ? Number(raw) : null;
  if (app === null || !SDR_APPS.has(app)) return badRequest("unknown app");
  if (await rateLimited(request, env)) return tooMany();
  const cache = getCache();
  const key = cacheKey(request, `/__cache/v1/sdr/${app}`);
  const hit = await cachedResponse(cache, key);
  if (hit) return hit;
  const mapped = mapSdr(await fetchSource(SDR_URL(app)), app);
  if (!mapped) return json({ error: "source_invalid", detail: "Valve's relay list was not available or not in the known shape" }, 502, "no-store", { "X-Gecko-Cache": "BYPASS" });
  const res = json({ ...mapped, fetchedAt: new Date().toISOString() }, 200, `public, max-age=${SDR_TTL}`, { "X-Gecko-Cache": "MISS" });
  if (cache) ctx?.waitUntil?.(cache.put(key, res.clone()));
  return res;
}

// GET /v1/service-status?id=<feed>: one public status feed, mapped. PlayStation also keeps the
// items of the caller's own country (request.cf.country), so its cache key has the country.
async function handleServiceStatus(request, env, ctx, url) {
  for (const k of url.searchParams.keys()) {
    if (k !== "id") return badRequest(`unknown parameter: ${k}`);
  }
  const id = url.searchParams.get("id") || "";
  if (!Object.hasOwn(FEEDS, id)) return badRequest("unknown id");
  if (await rateLimited(request, env)) return tooMany();
  const feed = FEEDS[id];
  let country = feed.kind === "psn" ? parseCountry(String(request.cf?.country ?? "")) : null;
  if (country === "XX" || country === "T1") country = null;
  const cache = getCache();
  const key = cacheKey(request, `/__cache/v1/service-status/${id}/${country ?? ""}`);
  const hit = await cachedResponse(cache, key);
  if (hit) return hit;
  const status = await fetchFeedStatus(id, country);
  const body = { id, name: feed.name, source: feed.kind, pageUrl: feed.page, fetchedAt: new Date().toISOString(), status };
  if (status === null) return json(body, 200, "no-store", { "X-Gecko-Cache": "BYPASS" });
  const res = json(body, 200, `public, max-age=${SERVICE_STATUS_TTL}`, { "X-Gecko-Cache": "MISS" });
  if (cache) ctx?.waitUntil?.(cache.put(key, res.clone()));
  return res;
}

async function handlePulse(request, env, ctx, url) {
  for (const k of url.searchParams.keys()) {
    if (k !== "isp" && k !== "country") return badRequest(`unknown parameter: ${k}`);
  }
  const cf = request.cf || {};
  let isp = null;
  let country = null;
  const ownIsp = parseAsn(String(cf.asn ?? ""));
  let ownCountry = parseCountry(String(cf.country ?? ""));
  if (ownCountry === "XX" || ownCountry === "T1") ownCountry = null;
  // The params may only name the caller's own network and country. Any other value
  // would be a new cache key, and each one costs about 16 Radar calls.
  if (url.searchParams.has("isp")) {
    isp = parseAsn(url.searchParams.get("isp"));
    if (isp === null) return badRequest("isp must be an ASN from 1 to 4294967295");
    if (isp !== ownIsp) return badRequest("isp must be the caller's own network");
  } else {
    isp = ownIsp;
  }
  if (url.searchParams.has("country")) {
    country = parseCountry(url.searchParams.get("country"));
    if (country === null) return badRequest("country must be 2 letters A-Z");
    if (country !== ownCountry) return badRequest("country must be the caller's own country");
  } else {
    country = ownCountry;
  }
  if (await rateLimited(request, env)) return tooMany();

  const cache = getCache();
  const key = cacheKey(request, `/__cache/v1/pulse?isp=${isp ?? ""}&country=${country ?? ""}`);
  if (cache && env && env.RADAR_TOKEN) {
    const hit = await cache.match(key);
    if (hit) {
      const res = new Response(hit.body, hit);
      res.headers.set("X-Gecko-Cache", "HIT");
      return res;
    }
  }
  // The Riot block is the same for every caller, so it has its own cache key.
  const riotKey = cacheKey(request, "/__cache/v1/riot");
  let riotCached = null;
  if (cache && env && env.RADAR_TOKEN) {
    try {
      const hit = await cache.match(riotKey);
      if (hit) riotCached = await hit.json();
    } catch {
      riotCached = null;
    }
  }
  const wantRel = Boolean(env && env.RADAR_TOKEN) && isp !== null && isp !== RIOT_ASN;
  const [built, upstreams] = await Promise.all([
    buildPulse(env, isp, country, new Date(), riotCached),
    wantRel ? ispUpstreams(request, env, ctx, isp) : Promise.resolve(null),
  ]);
  const { pulse, anyOk, riotOk } = built;
  pulse.isp.upstreams = upstreams;
  if (cache && riotOk) {
    ctx?.waitUntil?.(cache.put(riotKey, json(pulse.riot, 200, `public, max-age=${PULSE_TTL}`)));
  }
  if (!anyOk) return json(pulse, 200, "no-store", { "X-Gecko-Cache": "BYPASS" });
  const res = json(pulse, 200, `public, max-age=${PULSE_TTL}`, { "X-Gecko-Cache": "MISS" });
  if (cache) ctx?.waitUntil?.(cache.put(key, res.clone()));
  return res;
}

async function fetchIpRow(env, ip) {
  const row = { ip, asn: null, name: null, country: null };
  try {
    const a = (await radar(env, "/radar/entities/asns/ip", { ip }))?.asn;
    if (a) {
      row.asn = numOrNull(a.asn);
      row.name = strOrNull(a.name);
      row.country = strOrNull(a.country);
    }
  } catch {
    // The row stays null. The response is still 200.
  }
  return row;
}

// Looks up each IP. With the cache, a hit costs 1 subrequest and a miss costs 3
// (match, fetch, put). Cache calls count toward the 50-subrequest limit.
// MAX_IPS (15) times 3 fits the budget, so every IP goes through the cache.
async function lookupIps(request, env, ctx, ips) {
  const cache = getCache();
  const keyFor = (ip) => cacheKey(request, `/__cache/v1/asn/${encodeURIComponent(ip)}`);
  const rows = new Array(ips.length).fill(null);
  let budget = SUBREQUEST_BUDGET;
  if (cache) {
    budget -= ips.length;
    const hits = await Promise.allSettled(ips.map(async (ip) => {
      const hit = await cache.match(keyFor(ip));
      return hit ? hit.json() : null;
    }));
    hits.forEach((h, i) => {
      if (h.status === "fulfilled" && h.value && h.value.asn !== null && h.value.asn !== undefined) rows[i] = h.value;
    });
  }
  // Reserve the subrequests now, in order: a fetch, plus a put when the cache is on.
  // Over the limit, a row stays null; the next call fills more rows from the cache.
  const misses = ips.map((_, i) => i).filter((i) => !rows[i]);
  const plan = misses.map((i) => {
    if (budget <= 0) return { i, fetch: false, put: false };
    budget -= 1;
    const put = Boolean(cache) && budget > 0;
    if (put) budget -= 1;
    return { i, fetch: true, put };
  });
  await Promise.allSettled(plan.filter((p) => p.fetch).map(async (p) => {
    const row = await fetchIpRow(env, ips[p.i]);
    rows[p.i] = row;
    if (p.put && row.asn !== null) {
      ctx?.waitUntil?.(cache.put(keyFor(ips[p.i]), json(row, 200, `public, max-age=${ASN_TTL}`)));
    }
  }));
  return rows;
}

async function handleAsn(request, env, ctx, url) {
  for (const k of url.searchParams.keys()) {
    if (k !== "ips") return badRequest(`unknown parameter: ${k}`);
  }
  const raw = url.searchParams.get("ips");
  if (!raw) return badRequest("ips is required");
  const parts = raw.split(",").map((s) => s.trim()).filter(Boolean);
  if (!parts.length) return badRequest("ips is required");
  if (parts.length > MAX_IPS) return badRequest(`at most ${MAX_IPS} ips`);
  if (await rateLimited(request, env)) return tooMany();
  const seen = new Set();
  const lookups = [];
  for (const p of parts) {
    const c = classifyIp(p);
    if (!c) return badRequest("ips must be IPv4 or IPv6 addresses");
    if (!c.public || seen.has(c.ip)) continue; // private and reserved IPs are dropped
    seen.add(c.ip);
    // The row echoes the caller's text; the normalized form is the cache key.
    lookups.push({ input: p.toLowerCase(), ip: c.ip });
  }
  const empty = (ip) => ({ ip, asn: null, name: null, country: null });
  let results;
  if (env && env.RADAR_TOKEN) {
    const rows = await lookupIps(request, env, ctx, lookups.map((l) => l.ip));
    results = rows.map((row, i) => (row ? { ...row, ip: lookups[i].input } : empty(lookups[i].input)));
  } else {
    results = lookups.map((l) => empty(l.input));
  }
  const allOk = results.length > 0 && results.every((r) => r.asn !== null);
  return json({ results }, 200, allOk ? "public, max-age=3600" : "no-store");
}

// Update channel routes. Each has one method. The path tail after the prefix is split on "/".
const UPDATE_ROUTES = [
  { prefix: "/v1/update/download/", method: "GET", run: (request, env, parts, ctx) => handleDownload(request, env, parts, ctx), limiter: "DOWNLOAD_LIMITER" },
  { prefix: "/v1/admin/upload/", method: "PUT", run: (request, env, parts) => handleUploadPart(request, env, parts) },
  { prefix: "/v1/admin/finalize/", method: "POST", run: (request, env, parts) => handleFinalize(request, env, parts) },
  { prefix: "/v1/admin/promote/", method: "POST", run: (request, env, parts) => handlePromote(request, env, parts) },
  { prefix: "/v1/admin/versions/", method: "GET", run: (request, env, parts) => handleVersions(request, env, parts) },
  { prefix: "/v1/admin/release/", method: "DELETE", run: (request, env, parts) => handleDeleteRelease(request, env, parts) },
];

async function handleUpdateRoute(request, env, ctx, url) {
  if (url.pathname === "/v1/update/latest") {
    if (request.method !== "GET") return json({ error: "method_not_allowed" }, 405, "no-store", { Allow: "GET" });
    return handleLatest(request, env, url);
  }
  const route = UPDATE_ROUTES.find((r) => url.pathname.startsWith(r.prefix));
  if (!route) return null;
  if (request.method !== route.method) {
    return json({ error: "method_not_allowed" }, 405, "no-store", { Allow: route.method });
  }
  if (route.limiter && (await rateLimited(request, env, route.limiter))) return tooMany();
  const parts = url.pathname.slice(route.prefix.length).split("/");
  return route.run(request, env, parts, ctx);
}

async function handle(request, env, ctx) {
  const url = new URL(request.url);
  if (url.pathname.startsWith("/v1/update/") || url.pathname.startsWith("/v1/admin/")) {
    try {
      return (await handleUpdateRoute(request, env, ctx, url)) || json({ error: "not_found" }, 404);
    } catch {
      return json({ error: "internal_error" }, 500);
    }
  }
  const routes = {
    "/v1/health": () => handleHealth(env),
    "/v1/edge": () => handleEdge(request),
    "/v1/pulse": () => handlePulse(request, env, ctx, url),
    "/v1/asn": () => handleAsn(request, env, ctx, url),
    "/v1/refdata": () => handleRefdata(request, url),
    "/v1/riot-status": () => handleRiotStatus(request, env, ctx, url),
    "/v1/sdr": () => handleSdr(request, env, ctx, url),
    "/v1/service-status": () => handleServiceStatus(request, env, ctx, url),
  };
  const route = routes[url.pathname];
  if (!route) return json({ error: "not_found" }, 404);
  if (request.method !== "GET") {
    return json({ error: "method_not_allowed" }, 405, "no-store", { Allow: "GET" });
  }
  try {
    return await route();
  } catch {
    return json({ error: "internal_error" }, 500);
  }
}

// ---------- exports ----------
export { VERSION, REFDATA_ETAG, parseAsn, parseCountry, classifyIp, iso, mapOutages, mapAnomalies, mapHijacks, mapLeaks, mapTraffic, mapPrefix, mapIqi, mapRels, buildPulse, handle };
export default { fetch: (request, env, ctx) => handle(request, env, ctx) };
