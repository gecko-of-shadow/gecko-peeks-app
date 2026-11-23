// v2.1 Worker tests: refdata, /v1/refdata, /v1/riot-status, edge continent, pulse upstreams.
// No network. All fixtures are neutral (documentation ASNs 64496-64511, synthetic feeds).
import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync, statSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { handle, mapRels, REFDATA_ETAG, VERSION } from "../src/index.js";

const REFDATA_PATH = fileURLToPath(new URL("../data/refdata.json", import.meta.url));
const RAW = readFileSync(REFDATA_PATH);
const RD = JSON.parse(RAW.toString("utf8"));

const BASE = "https://gecko-peeks-api.example.workers.dev";
const DEMO_ISP = 64500;
const req = (path, init = {}, cf = { asn: DEMO_ISP, country: "US", continent: "NA", colo: "DEN" }) => {
  const r = new Request(BASE + path, init);
  Object.defineProperty(r, "cf", { value: cf });
  return r;
};
const body = async (res) => JSON.parse(await res.text());

// A fake caches.default that counts calls.
function fakeCache(counter) {
  const store = new Map();
  return {
    store,
    default: {
      match: async (k) => { counter.n++; const v = store.get(k.url); return v ? new Response(v.body, { headers: v.headers }) : undefined; },
      put: async (k, r) => { counter.n++; store.set(k.url, { body: await r.text(), headers: Object.fromEntries(r.headers) }); },
    },
  };
}

async function withGlobals({ fetch, caches }, fn) {
  const realFetch = globalThis.fetch;
  const realCaches = globalThis.caches;
  if (fetch) globalThis.fetch = fetch;
  if (caches !== undefined) globalThis.caches = caches;
  try {
    return await fn();
  } finally {
    globalThis.fetch = realFetch;
    globalThis.caches = realCaches;
  }
}

// ---------- refdata ----------

function isPublicV4(ip) {
  const m = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(ip);
  if (!m) return false;
  const [a, b, c] = m.slice(1).map(Number);
  if ([a, b, c, Number(m[4])].some((x) => x > 255)) return false;
  if (a === 0 || a === 10 || a === 127 || a >= 224) return false;
  if (a === 100 && b >= 64 && b <= 127) return false;
  if (a === 169 && b === 254) return false;
  if (a === 172 && b >= 16 && b <= 31) return false;
  if (a === 192 && b === 168) return false;
  if (a === 192 && b === 0 && (c === 0 || c === 2)) return false;
  if (a === 198 && (b === 18 || b === 19)) return false;
  if (a === 198 && b === 51 && c === 100) return false;
  if (a === 203 && b === 0 && c === 113) return false;
  return true;
}

// Section 2.2 rules. Returns a list of problems.
function validateRefdata(d, bytes) {
  const errs = [];
  if (d.schema !== 1) errs.push("schema");
  if (typeof d.version !== "string" || !d.version) errs.push("version");
  const ids = new Set();
  for (const r of d.regions || []) {
    if (!/^[a-z]{2,3}\d?$/.test(r.id)) errs.push(`region id ${r.id}`);
    if (ids.has(r.id)) errs.push(`dup region ${r.id}`);
    ids.add(r.id);
    if (!isPublicV4(r.targetIp)) errs.push(`targetIp ${r.id}`);
  }
  if (!ids.has(d.defaultRegion)) errs.push("defaultRegion");
  for (const map of ["aliases", "countryToRegion", "continentToRegion"]) {
    for (const [k, v] of Object.entries(d[map] || {})) if (!ids.has(v)) errs.push(`${map}.${k}`);
  }
  const keys = new Set();
  const asns = new Set();
  for (const p of d.providers || []) {
    if (keys.has(p.key)) errs.push(`dup provider ${p.key}`);
    keys.add(p.key);
    for (const a of p.asns) {
      if (asns.has(a)) errs.push(`dup asn ${a}`);
      asns.add(a);
    }
    if (p.hostRegex !== null) {
      try { new RegExp(p.hostRegex); } catch { errs.push(`regex ${p.key}`); }
    }
  }
  if (bytes >= 512 * 1024) errs.push("size");
  return errs;
}

test("refdata: passes every section 2.2 rule, no BOM, under 512 KB", () => {
  assert.notDeepEqual([...RAW.subarray(0, 3)], [0xef, 0xbb, 0xbf]);
  assert.ok(statSync(REFDATA_PATH).size < 512 * 1024);
  assert.deepEqual(validateRefdata(RD, RAW.length), []);
  assert.match(RD.version, /^\d{4}\.\d{2}\.\d{2}-\d+$/);
});

test("refdata: each 2.2 rule catches a broken copy", () => {
  const broken = [
    (d) => { d.schema = 2; },
    (d) => { d.version = ""; },
    (d) => { d.regions[1].id = "na1"; },
    (d) => { d.regions[0].id = "NA-1"; },
    (d) => { d.defaultRegion = "zz9"; },
    (d) => { d.aliases.kr1 = "kr1"; },
    (d) => { d.countryToRegion.US = "us"; },
    (d) => { d.continentToRegion.EU = "eu"; },
    (d) => { d.regions[0].targetIp = "192.168.1.1"; },
    (d) => { d.regions[0].targetIp = "not-an-ip"; },
    (d) => { d.providers[0].hostRegex = "(["; },
    (d) => { d.providers[1].key = d.providers[0].key; },
    (d) => { d.providers[1].asns = [d.providers[0].asns[0]]; },
  ];
  for (const [i, f] of broken.entries()) {
    const copy = structuredClone(RD);
    f(copy);
    assert.notDeepEqual(validateRefdata(copy, RAW.length), [], `case ${i}`);
  }
  assert.notDeepEqual(validateRefdata(RD, 512 * 1024), []);
});

test("refdata: 15 regions and the frozen values for na1, euw1, kr, ru", () => {
  assert.equal(RD.regions.length, 15);
  assert.deepEqual(RD.regions.map((r) => r.id), ["na1", "euw1", "eun1", "kr", "jp1", "br1", "la1", "la2", "oc1", "tr1", "ru", "me1", "sg2", "tw2", "vn2"]);
  const by = Object.fromEntries(RD.regions.map((r) => [r.id, r]));
  const ANY = "Riot Direct anycast prefix 192.207.0.0/24 (AS6507)";
  assert.deepEqual(by.na1, { id: "na1", shortName: "NA", name: "North America", lolStatusId: "na1", valorantShard: "na", targetIp: "192.207.0.5", targetKind: "anycast", targetBasis: ANY, targetInRegion: true, serverCity: "Chicago", cityConfirmed: true, lat: 41.88, lon: -87.63 });
  assert.deepEqual(by.euw1, { id: "euw1", shortName: "EUW", name: "EU West", lolStatusId: "euw1", valorantShard: "eu", targetIp: "185.40.64.65", targetKind: "dns", targetBasis: "prod.euw1.lol.riotgames.com resolved to this address (AS6507)", targetInRegion: true, serverCity: "Amsterdam", cityConfirmed: true, lat: 52.37, lon: 4.9 });
  assert.deepEqual(by.kr, { id: "kr", shortName: "KR", name: "Korea", lolStatusId: "kr1", valorantShard: "kr", targetIp: "45.250.208.40", targetKind: "dns", targetBasis: "prod.kr.lol.riotgames.com resolved to this address (AS6507)", targetInRegion: true, serverCity: "Seoul", cityConfirmed: true, lat: 37.57, lon: 126.98 });
  assert.deepEqual(by.ru, { id: "ru", shortName: "RU", name: "Russia", lolStatusId: "ru1", valorantShard: "eu", targetIp: "192.207.0.5", targetKind: "anycast", targetBasis: ANY, targetInRegion: false, serverCity: "", cityConfirmed: false, lat: null, lon: null });
  for (const r of RD.regions) {
    assert.equal(r.targetInRegion, ["na1", "euw1", "kr"].includes(r.id), r.id);
    assert.ok(["anycast", "dns"].includes(r.targetKind), r.id);
    if (r.targetKind === "anycast") assert.equal(r.targetBasis, ANY, r.id);
  }
});

test("refdata: maps, providers and city codes", () => {
  assert.deepEqual(RD.aliases, { kr1: "kr", ru1: "ru", ph2: "sg2", th2: "sg2" });
  assert.equal(RD.countryToRegion.PH, "sg2");
  assert.equal(RD.countryToRegion.TH, "sg2");
  assert.equal(RD.countryToRegion.KR, "kr");
  assert.equal(RD.countryToRegion.RU, "ru");
  assert.equal(RD.countryToRegion.US, "na1");
  assert.ok(!("CN" in RD.countryToRegion));
  for (const k of Object.keys(RD.countryToRegion)) assert.match(k, /^[A-Z]{2}$/);
  assert.deepEqual(RD.providers.map((p) => p.key), ["zayo", "cogent", "telus", "wolfpaw", "he", "lumen", "ntt", "gtt", "telia", "riotdirect", "valve", "cloudflare", "akamai", "microsoft"]);
  const p = Object.fromEntries(RD.providers.map((x) => [x.key, x]));
  assert.equal(p.telia.tier, "backbone");
  assert.equal(p.riotdirect.tier, "destination");
  assert.equal(p.riotdirect.name, "Riot Direct");
  // Group 1 is the city code.
  const city = (key, host) => new RegExp(p[key].hostRegex).exec(host)?.[1];
  assert.equal(city("zayo", "ae0.cs2.den5.us.eth.zayo.com"), "den");
  assert.equal(city("riotdirect", "ae1.er01.chi01.riotdirect.net"), "chi");
  assert.equal(city("ntt", "ae-3.r24.chcgil09.us.bb.gin.ntt.net"), "chcgil");
  assert.equal(city("lumen", "ae1.chicago2.level3.net"), "chicago");
  assert.equal(city("lumen", "ae1.chicago2.net.lumen.tech"), "chicago");
  assert.equal(city("telia", "dls-b24-link.ip.twelve99.net"), "dls");
  assert.equal(city("he", "100ge0-1.core2.chi1.he.net"), "chi");
  const seen = new Set();
  for (const c of RD.cityCodes) {
    assert.deepEqual(Object.keys(c), ["group", "code", "city", "country", "lat", "lon"]);
    assert.match(c.code, /^[a-z]+$/);
    assert.ok(!seen.has(`${c.group}/${c.code}`), `${c.group}/${c.code}`);
    seen.add(`${c.group}/${c.code}`);
    assert.equal(Math.round(c.lat * 100) / 100, c.lat);
    assert.equal(Math.round(c.lon * 100) / 100, c.lon);
  }
  assert.ok(RD.cityCodes.some((c) => c.group === "telia" && c.code === "dls" && c.city === "Dallas"));
});

// ---------- health, edge, refdata route ----------

test("health has the Worker version and the refdata version; edge has continent", async () => {
  assert.equal(VERSION, "2.2.2");
  const h = await body(await handle(req("/v1/health"), {}, {}));
  assert.equal(h.version, "2.2.2");
  assert.equal(h.refdata, RD.version);
  const e = await body(await handle(req("/v1/edge"), {}, {}));
  assert.equal(e.continent, "NA");
  const e2 = await body(await handle(req("/v1/edge", {}, {}), {}, {}));
  assert.equal(e2.continent, null);
});

test("/v1/refdata: 200 with ETag, 304 on a match, 400 on a parameter, 405 on POST", async () => {
  assert.equal(REFDATA_ETAG, `"rd-${RD.version}"`);
  let r = await handle(req("/v1/refdata"), {}, {});
  assert.equal(r.status, 200);
  assert.equal(r.headers.get("ETag"), REFDATA_ETAG);
  assert.equal(r.headers.get("Cache-Control"), "public, max-age=86400");
  assert.equal(r.headers.get("Content-Type"), "application/json; charset=utf-8");
  assert.equal(r.headers.get("X-Content-Type-Options"), "nosniff");
  assert.deepEqual(await body(r), RD);
  for (const inm of [REFDATA_ETAG, `W/${REFDATA_ETAG}`, `"other", ${REFDATA_ETAG}`, "*"]) {
    r = await handle(req("/v1/refdata", { headers: { "If-None-Match": inm } }), {}, {});
    assert.equal(r.status, 304, inm);
    assert.equal(await r.text(), "");
    assert.equal(r.headers.get("ETag"), REFDATA_ETAG);
    assert.equal(r.headers.get("Cache-Control"), "public, max-age=86400");
  }
  r = await handle(req("/v1/refdata", { headers: { "If-None-Match": '"rd-old"' } }), {}, {});
  assert.equal(r.status, 200);
  r = await handle(req("/v1/refdata?x=1"), {}, {});
  assert.equal(r.status, 400);
  assert.equal((await body(r)).error, "bad_request");
  r = await handle(req("/v1/refdata", { method: "POST" }), {}, {});
  assert.equal(r.status, 405);
});

test("/v1/refdata makes no subrequests and skips the rate limiter", async () => {
  let calls = 0;
  const env = { RATE_LIMITER: { limit: async () => { calls++; return { success: false }; } } };
  await withGlobals({ fetch: async () => { calls++; throw new Error("no"); } }, async () => {
    const r = await handle(req("/v1/refdata"), env, {});
    assert.equal(r.status, 200);
  });
  assert.equal(calls, 0);
});

// ---------- riot status ----------

const FUTURE = "2099-01-01T00:00:00+00:00";
const PAST = "2020-01-01T00:00:00+00:00";

function lolFeed(id = "SG2", name = "Southeast Asia") {
  return {
    id, name, locales: ["en_US", "de_DE"],
    maintenances: [
      { id: 101, maintenance_status: "scheduled", incident_severity: null, created_at: "2026-11-20T10:00:00+00:00", updated_at: null, archive_at: FUTURE,
        titles: [{ locale: "en_US", content: "  Demo: scheduled maintenance  " }],
        updates: [
          { publish: true, created_at: "2026-11-20T10:00:00+00:00", translations: [{ locale: "en_US", content: "Old text" }] },
          { publish: true, created_at: "2026-11-20T11:00:00+00:00", translations: [{ locale: "de_DE", content: "Neu" }, { locale: "en_US", content: "New text" }] },
          { publish: false, created_at: "2026-11-20T12:00:00+00:00", translations: [{ locale: "en_US", content: "Draft" }] },
        ],
        platforms: ["windows", 7, "macos"] },
      { id: 102, maintenance_status: "complete", created_at: "2026-11-19T10:00:00+00:00", archive_at: FUTURE, titles: [{ locale: "en_US", content: "Done" }], updates: [] },
      { id: 103, maintenance_status: "in_progress", created_at: "2024-12-20T10:00:00+00:00", archive_at: PAST, titles: [{ locale: "en_US", content: "Archived" }], updates: [] },
    ],
    incidents: [
      { id: 201, incident_severity: "warning", created_at: "2026-11-21T08:00:00+00:00", updated_at: "2026-11-21T09:00:00+00:00", archive_at: null,
        titles: [{ locale: "de_DE", content: "Nur Deutsch" }], updates: [], platforms: ["windows"] },
      { id: 202, incident_severity: "odd", created_at: "2026-11-21T07:00:00+00:00", titles: [{ locale: "en_US", content: "   " }], updates: [] },
      { id: 203, incident_severity: "critical", created_at: "2026-11-21T06:00:00+00:00", titles: [{ locale: "en_US", content: "x".repeat(300) }], updates: [] },
    ],
  };
}
const valFeed = () => ({ id: "AP", name: "Asia Pacific", locales: ["en_US"], maintenances: [], incidents: [] });

function feedFetch(urls, { lol = lolFeed(), val = valFeed(), lolStatus = 200, valStatus = 200 } = {}) {
  return async (url) => {
    urls.push(String(url));
    const u = String(url);
    if (u.startsWith("https://lol.secure.dyn.riotcdn.net/")) {
      if (lolStatus !== 200) return new Response("no", { status: lolStatus });
      return new Response(typeof lol === "string" ? lol : JSON.stringify(lol));
    }
    if (u.startsWith("https://valorant.secure.dyn.riotcdn.net/")) {
      if (valStatus !== 200) return new Response("no", { status: valStatus });
      return new Response(typeof val === "string" ? val : JSON.stringify(val));
    }
    throw new Error("unexpected " + u);
  };
}

test("riot-status: bad input gives 400", async () => {
  for (const q of ["", "?region=", "?region=zz9", "?region=NA-1", "?region=pbe", "?region=na1&x=1", "?foo=1"]) {
    const r = await handle(req("/v1/riot-status" + q), {}, {});
    assert.equal(r.status, 400, q);
    assert.equal((await body(r)).error, "bad_request");
  }
  const r = await handle(req("/v1/riot-status?region=zz9"), {}, {});
  assert.deepEqual(await body(r), { error: "bad_request", detail: "unknown region" });
});

test("riot-status: alias ph2 -> sg2, mapping, drops, locale fallback, at most 4 subrequests", async () => {
  const urls = [];
  const counter = { n: 0 };
  const caches = fakeCache(counter);
  const waits = [];
  const ctx = { waitUntil: (p) => waits.push(p) };
  await withGlobals({ fetch: feedFetch(urls), caches }, async () => {
    const r = await handle(req("/v1/riot-status?region=PH2"), {}, ctx);
    await Promise.all(waits);
    assert.equal(r.status, 200);
    assert.equal(r.headers.get("Cache-Control"), "public, max-age=60");
    const s = await body(r);
    assert.equal(s.region, "sg2");
    assert.equal(s.lolStatusId, "sg2");
    assert.equal(s.valorantShard, "ap");
    assert.ok(!Number.isNaN(Date.parse(s.fetchedAt)));
    assert.deepEqual(urls.sort(), [
      "https://lol.secure.dyn.riotcdn.net/channels/public/x/status/sg2.json",
      "https://valorant.secure.dyn.riotcdn.net/channels/public/x/status/ap.json",
    ]);
    assert.equal(urls.length + counter.n, 4); // 2 fetches, match, put
    assert.equal(s.lol.id, "SG2");
    assert.equal(s.lol.name, "Southeast Asia");
    // Complete and archived maintenances are gone.
    assert.equal(s.lol.maintenances.length, 1);
    assert.deepEqual(s.lol.maintenances[0], {
      id: 101, type: "maintenance", severity: null, status: "scheduled",
      title: "Demo: scheduled maintenance", text: "New text", platforms: ["windows", "macos"],
      createdAt: "2026-11-20T10:00:00.000Z", updatedAt: null, archiveAt: "2099-01-01T00:00:00.000Z",
    });
    // Empty title dropped; non-English title used when no en_US; title cut to 200.
    assert.equal(s.lol.incidents.length, 2);
    assert.equal(s.lol.incidents[0].title, "Nur Deutsch");
    assert.equal(s.lol.incidents[0].severity, "warning");
    assert.equal(s.lol.incidents[0].status, null);
    assert.equal(s.lol.incidents[0].text, null);
    assert.equal(s.lol.incidents[1].title.length, 200);
    assert.equal(s.lol.incidents[1].severity, "critical");
    assert.deepEqual(s.valorant, { id: "AP", name: "Asia Pacific", incidents: [], maintenances: [] });

    // A second call is a cache hit: one subrequest, no feed fetch.
    counter.n = 0;
    urls.length = 0;
    const r2 = await handle(req("/v1/riot-status?region=sg2"), {}, ctx);
    assert.equal(r2.headers.get("X-Gecko-Cache"), "HIT");
    assert.equal(urls.length, 0);
    assert.equal(counter.n, 1);
    assert.equal((await body(r2)).region, "sg2");
  });
});

test("riot-status: kr1 and ru1 aliases use the right feeds", async () => {
  for (const [q, lol, val] of [["kr1", "kr1", "kr"], ["ru1", "ru1", "eu"], ["th2", "sg2", "ap"], ["na1", "na1", "na"]]) {
    const urls = [];
    await withGlobals({ fetch: feedFetch(urls), caches: null }, async () => {
      const r = await handle(req(`/v1/riot-status?region=${q}`), {}, {});
      assert.equal(r.status, 200, q);
    });
    assert.ok(urls.some((u) => u.endsWith(`/status/${lol}.json`) && u.includes("lol.")), q);
    assert.ok(urls.some((u) => u.endsWith(`/status/${val}.json`) && u.includes("valorant.")), q);
  }
});

test("riot-status: kept lists are the newest 10", async () => {
  const many = lolFeed();
  many.incidents = Array.from({ length: 14 }, (_, i) => ({
    id: i, incident_severity: "info", created_at: `2026-11-${String(i + 1).padStart(2, "0")}T00:00:00+00:00`,
    titles: [{ locale: "en_US", content: `Event ${i}` }], updates: [],
  }));
  await withGlobals({ fetch: feedFetch([], { lol: many }), caches: null }, async () => {
    const s = await body(await handle(req("/v1/riot-status?region=na1"), {}, {}));
    assert.equal(s.lol.incidents.length, 10);
    assert.equal(s.lol.incidents[0].id, 13);
    assert.equal(s.lol.incidents[9].id, 4);
  });
});

test("riot-status: one feed failing is null with 200; both failing is no-store and not cached", async () => {
  const counter = { n: 0 };
  const caches = fakeCache(counter);
  const waits = [];
  const ctx = { waitUntil: (p) => waits.push(p) };
  await withGlobals({ fetch: feedFetch([], { valStatus: 503 }), caches }, async () => {
    const r = await handle(req("/v1/riot-status?region=na1"), {}, ctx);
    await Promise.all(waits);
    const s = await body(r);
    assert.equal(r.status, 200);
    assert.equal(s.valorant, null);
    assert.ok(s.lol);
    assert.equal(r.headers.get("Cache-Control"), "public, max-age=60");
  });
  await withGlobals({ fetch: feedFetch([], { lol: "not json", valStatus: 404 }), caches }, async () => {
    caches.store.clear();
    const r = await handle(req("/v1/riot-status?region=euw1"), {}, ctx);
    await Promise.all(waits);
    const s = await body(r);
    assert.equal(r.status, 200);
    assert.equal(s.lol, null);
    assert.equal(s.valorant, null);
    assert.equal(r.headers.get("Cache-Control"), "no-store");
    assert.equal(caches.store.size, 0);
  });
});

test("riot-status: a feed over 256 KB is null", async () => {
  const big = lolFeed();
  big.pad = "x".repeat(300 * 1024);
  await withGlobals({ fetch: feedFetch([], { lol: big }), caches: null }, async () => {
    const s = await body(await handle(req("/v1/riot-status?region=na1"), {}, {}));
    assert.equal(s.lol, null);
    assert.ok(s.valorant);
  });
});

test("riot-status: rate limiter applies after validation", async () => {
  const keys = [];
  const env = { RATE_LIMITER: { limit: async ({ key }) => { keys.push(key); return { success: false }; } } };
  let r = await handle(req("/v1/riot-status?region=zz9"), env, {});
  assert.equal(r.status, 400);
  r = await handle(req("/v1/riot-status?region=na1", { headers: { "cf-connecting-ip": "203.0.113.9" } }), env, {});
  assert.equal(r.status, 429);
  assert.deepEqual(keys, ["203.0.113.9"]);
});

// ---------- pulse upstreams ----------

// Shape recorded from Radar /radar/entities/asns/{asn}/rel (2026-11-21), with neutral ASNs.
// The asked ASN can be asn1 or asn2. "provider-customer" means asn1 is the provider of asn2.
const REL_FIXTURE = {
  meta: { data_time: "2026-11-21T06:00:00", query_time: "2026-11-21T09:00:00Z", total_peers: 76 },
  rels: [
    { asn1: 6939, asn1_country: "US", asn1_name: "Hurricane Electric LLC", asn2: DEMO_ISP, asn2_country: "US", asn2_name: "Demo ISP", aspa_validated: false, rel: "provider-customer" },
    { asn1: 6939, asn1_country: "US", asn1_name: "Hurricane Electric LLC", asn2: DEMO_ISP, asn2_country: "US", asn2_name: "Demo ISP", aspa_validated: false, rel: "peer" },
    { asn1: 174, asn1_country: "US", asn1_name: "Cogent Communications", asn2: DEMO_ISP, asn2_country: "US", asn2_name: "Demo ISP", aspa_validated: false, rel: "provider-customer" },
    { asn1: 64501, asn1_country: "US", asn1_name: "", asn2: DEMO_ISP, asn2_country: "US", asn2_name: "Demo ISP", aspa_validated: false, rel: "provider-customer" },
    { asn1: DEMO_ISP, asn1_country: "US", asn1_name: "Demo ISP", asn2: 64502, asn2_country: "US", asn2_name: "Demo Customer", aspa_validated: false, rel: "provider-customer" },
    { asn1: DEMO_ISP, asn1_country: "US", asn1_name: "Demo ISP", asn2: 3356, asn2_country: "US", asn2_name: "Lumen (Level 3)", aspa_validated: false, rel: "peer" },
    { asn1: 1299, asn1_country: "SE", asn1_name: "Arelion (Twelve99)", asn2: DEMO_ISP, asn2_country: "US", asn2_name: "Demo ISP", aspa_validated: false, rel: "peer" },
    { asn1: 64503, asn1_country: "US", asn1_name: "Demo IX peer", asn2: DEMO_ISP, asn2_country: "US", asn2_name: "Demo ISP", aspa_validated: false, rel: "peer" },
    { asn1: 6507, asn1_country: "US", asn1_name: "Riot Games", asn2: DEMO_ISP, asn2_country: "US", asn2_name: "Demo ISP", aspa_validated: false, rel: "peer" },
    { asn1: 64504, asn1_country: "US", asn1_name: "Other", asn2: 64505, asn2_country: "US", asn2_name: "Other", aspa_validated: false, rel: "provider-customer" },
  ],
};

test("mapRels: providers first, then upstream/backbone peers, ascending ASN", () => {
  assert.deepEqual(mapRels(REL_FIXTURE, DEMO_ISP), [
    { asn: 174, name: "Cogent Communications", rel: "provider" },
    { asn: 6939, name: "Hurricane Electric LLC", rel: "provider" },
    { asn: 64501, name: null, rel: "provider" },
    { asn: 1299, name: "Arelion (Twelve99)", rel: "peer" },
    { asn: 3356, name: "Lumen (Level 3)", rel: "peer" },
  ]);
  assert.deepEqual(mapRels({ rels: [] }, DEMO_ISP), []);
  // Short forms, and the refdata name when Radar gives none.
  assert.deepEqual(mapRels({ rels: [
    { asn1: DEMO_ISP, asn2: 2914, asn2_name: "", rel: "C2P" },
    { asn1: 3257, asn2: DEMO_ISP, rel: "P2C" },
    { asn1: DEMO_ISP, asn2: 64506, rel: "P2C" },
  ] }, DEMO_ISP), [
    { asn: 2914, name: "NTT", rel: "provider" },
    { asn: 3257, name: "GTT", rel: "provider" },
  ]);
  // Caps: 12 providers, 8 peers.
  const lots = { rels: [
    ...Array.from({ length: 20 }, (_, i) => ({ asn1: 64496 + (i % 16) + i * 1000, asn2: DEMO_ISP, rel: "provider-customer" })),
  ] };
  assert.equal(mapRels(lots, DEMO_ISP).length, 12);
});

// A fake Radar that answers everything, and counts subrequests (fetch + cache calls).
function radarFetch(counter, paths, { relStatus = 200, rel = REL_FIXTURE } = {}) {
  return async (url) => {
    counter.n++;
    const u = new URL(url);
    paths.push(u.pathname);
    const ok = (result) => new Response(JSON.stringify({ success: true, result }));
    if (u.pathname.endsWith("/rel")) return relStatus === 200 ? ok(rel) : new Response("no", { status: relStatus });
    if (u.pathname.endsWith(`/radar/entities/asns/${DEMO_ISP}`)) return ok({ asn: { name: "Demo ISP" } });
    return ok({ annotations: [], trafficAnomalies: [], events: [] });
  };
}

test("pulse: isp.upstreams is mapped, cached per ISP, and the cold total stays at or under 30 subrequests", async () => {
  const counter = { n: 0 };
  const paths = [];
  const caches = fakeCache(counter);
  const waits = [];
  const ctx = { waitUntil: (p) => waits.push(p) };
  await withGlobals({ fetch: radarFetch(counter, paths), caches }, async () => {
    const r = await handle(req("/v1/pulse"), { RADAR_TOKEN: "fake" }, ctx);
    await Promise.all(waits);
    const p = await body(r);
    assert.equal(p.isp.asn, DEMO_ISP);
    assert.equal(p.isp.upstreams.length, 5);
    assert.deepEqual(p.isp.upstreams[0], { asn: 174, name: "Cogent Communications", rel: "provider" });
    assert.equal(paths.filter((x) => x.endsWith("/rel")).length, 1);
    assert.ok(paths.includes(`/client/v4/radar/entities/asns/${DEMO_ISP}/rel`));
    assert.ok(counter.n <= 30, `cold pulse used ${counter.n} subrequests`);
    assert.equal(counter.n, 23); // 17 Radar calls, 3 matches, 3 puts
    assert.ok(caches.store.has(`${BASE}/__cache/v1/rel/${DEMO_ISP}`));

    // Another country (new pulse key) reuses the cached rel: no second Radar rel call.
    paths.length = 0;
    const r2 = await handle(req("/v1/pulse", {}, { asn: DEMO_ISP, country: "CA" }), { RADAR_TOKEN: "fake" }, ctx);
    await Promise.all(waits);
    assert.equal(paths.filter((x) => x.endsWith("/rel")).length, 0);
    assert.equal((await body(r2)).isp.upstreams.length, 5);
  });
});

test("pulse: upstreams null on a failed call, [] on no rows, skipped for AS6507 and without a token", async () => {
  const counter = { n: 0 };
  let paths = [];
  await withGlobals({ fetch: radarFetch(counter, paths, { relStatus: 500 }), caches: null }, async () => {
    const p = await body(await handle(req("/v1/pulse"), { RADAR_TOKEN: "fake" }, {}));
    assert.equal(p.isp.upstreams, null);
  });
  paths = [];
  await withGlobals({ fetch: radarFetch(counter, paths, { rel: { rels: [] } }), caches: null }, async () => {
    const p = await body(await handle(req("/v1/pulse"), { RADAR_TOKEN: "fake" }, {}));
    assert.deepEqual(p.isp.upstreams, []);
  });
  paths = [];
  await withGlobals({ fetch: radarFetch(counter, paths), caches: null }, async () => {
    const p = await body(await handle(req("/v1/pulse", {}, { asn: 6507, country: "US" }), { RADAR_TOKEN: "fake" }, {}));
    assert.equal(p.isp.upstreams, null);
    assert.equal(paths.filter((x) => x.endsWith("/rel")).length, 0);
  });
  const p = await body(await handle(req("/v1/pulse"), {}, {}));
  assert.equal(p.isp.upstreams, null);
});

test("pulse: a rel body over 1 MB gives upstreams null and keeps the rest of the pulse", async () => {
  const counter = { n: 0 };
  const paths = [];
  const huge = { rels: Array.from({ length: 16000 }, (_, i) => ({ asn1: DEMO_ISP, asn1_name: "Demo ISP", asn2: 100000 + i, asn2_name: "Some demo network name", rel: "peer" })) };
  assert.ok(JSON.stringify(huge).length > 1024 * 1024);
  await withGlobals({ fetch: radarFetch(counter, paths, { rel: huge }), caches: null }, async () => {
    const r = await handle(req("/v1/pulse"), { RADAR_TOKEN: "fake" }, {});
    assert.equal(r.status, 200);
    const p = await body(r);
    assert.equal(p.isp.upstreams, null);
    assert.equal(p.isp.name, "Demo ISP");
    assert.deepEqual(p.riot.outages, []);
  });
});

test("pulse: a failed rel lookup is cached for an hour, so the next pulse makes no second rel call", async () => {
  const counter = { n: 0 };
  const paths = [];
  const caches = fakeCache(counter);
  const waits = [];
  const ctx = { waitUntil: (p) => waits.push(p) };
  const huge = { rels: Array.from({ length: 16000 }, (_, i) => ({ asn1: DEMO_ISP, asn1_name: "Demo ISP", asn2: 100000 + i, asn2_name: "Some demo network name", rel: "peer" })) };
  await withGlobals({ fetch: radarFetch(counter, paths, { rel: huge }), caches }, async () => {
    const r = await handle(req("/v1/pulse"), { RADAR_TOKEN: "fake" }, ctx);
    await Promise.all(waits);
    assert.equal((await body(r)).isp.upstreams, null);
    assert.equal(paths.filter((x) => x.endsWith("/rel")).length, 1);
    const key = `${BASE}/__cache/v1/rel/${DEMO_ISP}`;
    assert.ok(caches.store.has(key));

    // Another country (new pulse key): the negative entry answers, no second Radar rel call.
    paths.length = 0;
    const r2 = await handle(req("/v1/pulse", {}, { asn: DEMO_ISP, country: "CA" }), { RADAR_TOKEN: "fake" }, ctx);
    await Promise.all(waits);
    assert.equal((await body(r2)).isp.upstreams, null);
    assert.equal(paths.filter((x) => x.endsWith("/rel")).length, 0);
  });
});
