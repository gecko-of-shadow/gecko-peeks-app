// v2.2 Worker tests: refdata services, /v1/sdr, /v1/service-status and the status mappers.
// No network. Fixtures copy the shape of the real feeds; the values are made up.
import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { handle, VERSION } from "../src/index.js";
import { mapSdr, mapStatuspage, mapPsn, mapXnotify, mapNintendo, FEEDS } from "../src/services.js";

const RD = JSON.parse(readFileSync(fileURLToPath(new URL("../data/refdata.json", import.meta.url)), "utf8"));
const BASE = "https://gecko-peeks-api.example.workers.dev";
const req = (path, cf = { asn: 64500, country: "US", continent: "NA" }) => {
  const r = new Request(BASE + path);
  Object.defineProperty(r, "cf", { value: cf });
  return r;
};
const body = async (res) => JSON.parse(await res.text());

function fakeCache() {
  const store = new Map();
  return {
    store,
    default: {
      match: async (k) => { const v = store.get(k.url); return v ? new Response(v.body, { headers: v.headers }) : undefined; },
      put: async (k, r) => { store.set(k.url, { body: await r.text(), headers: Object.fromEntries(r.headers) }); },
    },
  };
}

async function withFetch(fetchFn, fn, caches = undefined) {
  const realFetch = globalThis.fetch;
  const realCaches = globalThis.caches;
  globalThis.fetch = fetchFn;
  globalThis.caches = caches;
  try {
    return await fn();
  } finally {
    globalThis.fetch = realFetch;
    globalThis.caches = realCaches;
  }
}

const jsonRes = (o, status = 200) => new Response(typeof o === "string" ? o : JSON.stringify(o), { status });
const ctx = () => { const waits = []; return { waits, waitUntil: (p) => waits.push(p) }; };

function sdrBody(popCount = 6) {
  const pops = {};
  for (let i = 0; i < popCount; i++) {
    pops[`p${i}x`] = { desc: `City ${i} (Country)`, geo: [10 + i, 50 - i], partners: 1, tier: 1, relays: [{ ipv4: `198.51.${i}.10`.replace("198.51.", "155.133."), port_range: [27015, 27060] }] };
  }
  return { revision: 1794268433, pops, success: true, certs: ["x"] };
}

// ---------- refdata ----------

test("refdata: Valve provider and the valve-sdr service", () => {
  const valve = RD.providers.find((p) => p.key === "valve");
  assert.deepEqual(valve.asns, [32590]);
  assert.deepEqual(valve.domains, ["valve.net"]);
  assert.equal(valve.tier, "destination");
  const svc = RD.services.find((s) => s.id === "valve-sdr");
  assert.equal(svc.targetClass, "owned");
  assert.equal(svc.target.kind, "sdr-nearest-pop");
  assert.equal(svc.target.appId, 730);
  assert.equal(svc.processesVerified, false, "process names are not verified");
  assert.equal(svc.status, null);
  assert.match(svc.statusNote, /no status feed/i);
  assert.equal(RD.schema, 1, "services keep schema 1");
});

test("refdata: every status feed a service names is a Worker feed", () => {
  for (const s of RD.services) {
    if (!s.status) continue;
    const ids = [s.status.feed, ...Object.values(s.status.feedByContinent || {})].filter(Boolean);
    assert.ok(ids.length > 0, s.id);
    for (const id of ids) assert.ok(Object.hasOwn(FEEDS, id), `${s.id}: ${id}`);
  }
});

test("health shows the 2.2.2 Worker", async () => {
  assert.equal(VERSION, "2.2.2");
  const res = await handle(req("/v1/health"), {}, ctx());
  assert.equal((await body(res)).version, "2.2.2");
});

// ---------- SDR mapper ----------

test("mapSdr: geo is longitude first; keeps public relays; drops POPs without relays", () => {
  const b = sdrBody(6);
  b.pops.nor = { desc: "No relays (China)", geo: [100, 30], partners: 2, tier: 1 };
  b.pops.prv = { desc: "Private only", geo: [1, 1], relays: [{ ipv4: "10.0.0.1" }, { ipv4: "192.168.1.1" }] };
  b.pops.bad = { desc: "Bad geo", geo: [200, 30], relays: [{ ipv4: "155.133.200.1" }] };
  const m = mapSdr(b, 730);
  assert.equal(m.app, 730);
  assert.equal(m.revision, 1794268433);
  assert.equal(m.pops.length, 6);
  const p0 = m.pops.find((p) => p.code === "p0x");
  assert.equal(p0.lon, 10);
  assert.equal(p0.lat, 50);
  assert.deepEqual(p0.relays, ["155.133.0.10"]);
  assert.ok(!m.pops.some((p) => ["nor", "prv", "bad"].includes(p.code)));
  assert.ok(!("certs" in m));
});

test("mapSdr: refuses unknown shapes", () => {
  assert.equal(mapSdr(null, 730), null);
  assert.equal(mapSdr({ ...sdrBody(), success: false }, 730), null);
  assert.equal(mapSdr({ ...sdrBody(), revision: "x" }, 730), null);
  assert.equal(mapSdr({ ...sdrBody(), pops: [] }, 730), null);
  assert.equal(mapSdr(sdrBody(4), 730), null, "fewer than 5 usable POPs");
});

test("mapSdr: at most 4 relays per POP, no duplicates", () => {
  const b = sdrBody(5);
  b.pops.p0x.relays = ["1", "1", "2", "3", "4", "5"].map((x) => ({ ipv4: `155.133.9.${x}` }));
  const m = mapSdr(b, 730);
  assert.deepEqual(m.pops.find((p) => p.code === "p0x").relays, ["155.133.9.1", "155.133.9.2", "155.133.9.3", "155.133.9.4"]);
});

// ---------- /v1/sdr ----------

test("/v1/sdr: only refdata app ids, no other parameters", async () => {
  const fail = () => { throw new Error("no fetch expected"); };
  await withFetch(fail, async () => {
    assert.equal((await handle(req("/v1/sdr"), {}, ctx())).status, 400);
    assert.equal((await handle(req("/v1/sdr?app=570"), {}, ctx())).status, 400);
    assert.equal((await handle(req("/v1/sdr?app=730&x=1"), {}, ctx())).status, 400);
    assert.equal((await handle(req("/v1/sdr?app=0730x"), {}, ctx())).status, 400);
  });
});

test("/v1/sdr: maps, caches for 6 hours, then serves the cache", async () => {
  let calls = 0;
  const caches = fakeCache();
  await withFetch(async (url) => {
    calls++;
    assert.equal(String(url), "https://api.steampowered.com/ISteamApps/GetSDRConfig/v1/?appid=730");
    return jsonRes(sdrBody(6));
  }, async () => {
    const c = ctx();
    const res = await handle(req("/v1/sdr?app=730"), {}, c);
    assert.equal(res.status, 200);
    assert.equal(res.headers.get("cache-control"), "public, max-age=21600");
    const b = await body(res);
    assert.equal(b.pops.length, 6);
    assert.ok(b.fetchedAt);
    await Promise.all(c.waits);
    const again = await handle(req("/v1/sdr?app=730"), {}, ctx());
    assert.equal(again.headers.get("x-gecko-cache"), "HIT");
    assert.equal(calls, 1);
  }, caches);
});

test("/v1/sdr: a broken answer is 502 and not cached", async () => {
  const caches = fakeCache();
  await withFetch(async () => jsonRes({ success: true, pops: {} }), async () => {
    const res = await handle(req("/v1/sdr?app=730"), {}, ctx());
    assert.equal(res.status, 502);
    assert.equal(res.headers.get("cache-control"), "no-store");
    assert.equal(caches.store.size, 0);
  }, caches);
  await withFetch(async () => jsonRes("nope", 503), async () => {
    assert.equal((await handle(req("/v1/sdr?app=730"), {}, ctx())).status, 502);
  });
});

// ---------- Statuspage ----------

const spBody = (over = {}) => ({
  page: { id: "abc", name: "Discord", url: "https://discordstatus.com" },
  status: { indicator: "minor", description: "Partially Degraded Service" },
  components: [
    { id: "g1", name: "Voice", status: "partial_outage", group: true },
    { id: "c1", name: "US East", status: "partial_outage", group: false, group_id: "g1" },
    { id: "c2", name: "API", status: "operational", group: false, group_id: null },
    { id: "c3", name: "Odd", status: "on_fire", group: false, group_id: null },
  ],
  incidents: [{ name: "Voice issues", status: "investigating", impact: "minor", created_at: "2026-11-22T10:00:00.000-08:00", updated_at: "2026-11-22T10:05:00.000-08:00", incident_updates: [{ body: "<b>We are</b> looking." }] }],
  scheduled_maintenances: [{ name: "Old", status: "completed", impact: "maintenance" }, { name: "Planned", status: "scheduled", impact: "maintenance", scheduled_for: "2026-11-30T00:00:00Z" }],
  ...over,
});

test("mapStatuspage: indicator, components with their group, incidents, maintenance", () => {
  const m = mapStatuspage(spBody());
  assert.equal(m.indicator, "minor");
  assert.deepEqual(m.components[0], { name: "US East", group: "Voice", status: "partial_outage" });
  assert.equal(m.components.find((c) => c.name === "Odd").status, "unknown");
  assert.ok(!m.components.some((c) => c.name === "Voice"), "groups are not components");
  assert.equal(m.incidents[0].title, "Voice issues");
  assert.equal(m.incidents[0].startedAt, "2026-11-22T18:00:00.000Z");
  assert.equal(m.incidents[0].text, "We are looking.");
  assert.deepEqual(m.maintenances.map((x) => x.title), ["Planned"]);
});

test("mapStatuspage: an unknown indicator is unknown, never none", () => {
  assert.equal(mapStatuspage(spBody({ status: { indicator: "purple" } })).indicator, "unknown");
  assert.equal(mapStatuspage({ status: {}, components: [] }), null);
  assert.equal(mapStatuspage(spBody({ components: "x" })), null);
});

// ---------- PlayStation ----------

const psnItem = (msgs, key = "msg.key") => ({ statusId: "1", startDate: "2026-11-22T01:00:00.000Z", createdDate: "2026-11-22T01:00:00.000Z", modifiedDate: "2026-11-22T02:00:00.000Z", statusType: "Outage", devices: [], message: { messageKey: key, messages: msgs } });
const psnBody = () => ({
  regionName: "SCEA",
  status: [psnItem({ "ru-RU": "Только по-русски" })],
  countries: [
    { countryCode: "US", countryName: "United States", status: [], services: [
      { serviceId: "PlayStation_Store", serviceName: "PlayStation Store", status: [psnItem({ "en-US": "Store is down." })], resources: [{ resourceName: "Purchases", status: [psnItem({}, "store.purchase")] }] },
      { serviceId: "Gaming_And_Social", serviceName: "Gaming and Social", status: [] },
    ] },
    { countryCode: "CA", countryName: "Canada", status: [psnItem({ "en-US": "Canada only" })], services: [] },
  ],
});

test("mapPsn: region items plus the caller's own country only", () => {
  const m = mapPsn(psnBody(), "US");
  assert.equal(m.indicator, "listed");
  assert.equal(m.country, "US");
  assert.deepEqual(m.incidents.map((i) => i.title), ["Listed for the whole SCEA region", "PlayStation Store", "PlayStation Store: Purchases"]);
  assert.equal(m.incidents[0].text, "Только по-русски", "falls back to any locale");
  assert.equal(m.incidents[1].text, "Store is down.");
  assert.equal(m.incidents[2].text, "store.purchase", "falls back to the message key");
  assert.ok(!m.incidents.some((i) => i.text === "Canada only"));
});

test("mapPsn: empty lists mean none; a country not in the file is null", () => {
  const b = psnBody();
  b.status = [];
  b.countries[0].services = [];
  assert.equal(mapPsn(b, "US").indicator, "none");
  assert.equal(mapPsn(b, "FR").country, null);
  assert.equal(mapPsn({ regionName: "SCEA" }, "US"), null);
});

// ---------- Xbox ----------

const xbox = (state, scenario = "None") => ({
  Status: { Overall: { State: state, Id: 1, LastUpdated: "2026-11-23T02:47:31.2021450+00:00" } },
  CoreServices: [{ Id: 1, Name: "Account & profile", Status: { Name: scenario, Id: 1 }, Scenarios: [{ Id: 2, Name: "Sign-in", Status: { Name: scenario, Id: 1 }, Devices: [], Incidents: [], Description: "You may not be able to sign in." }] }],
  Titles: [],
});

test("mapXnotify: None is none; another state is listed with the feed's own word", () => {
  assert.equal(mapXnotify(xbox("None")).indicator, "none");
  const m = mapXnotify(xbox("Impacted", "Limited"));
  assert.equal(m.indicator, "listed");
  assert.equal(m.incidents[0].title, "Account & profile: Sign-in");
  assert.equal(m.incidents[0].status, "Limited");
  assert.equal(m.description, "Overall state: Impacted");
});

test("mapXnotify: an unknown overall state with no scenario is unknown", () => {
  assert.equal(mapXnotify(xbox("Strange")).indicator, "unknown");
  assert.equal(mapXnotify({ Status: {} }), null);
});

// ---------- Nintendo ----------

const nin = () => ({
  lang: "en_US",
  operational_statuses: [
    { begin: "Friday, November 20, 2026  6 :48 AM", end: "", event_status: "1", message: "Down now.", software_title: "Nintendo eShop", services: ["Using Download Codes"] },
    { begin: "x", end: "y", event_status: "0", message: "Ended.", software_title: "Old" },
  ],
  temporary_maintenances: [
    { begin: "a", end: "b", event_status: "0", message: "Later.", software_title: "Planned" },
    { begin: "a", end: "b", event_status: "3", message: "Done.", software_title: "Done" },
  ],
});

test("mapNintendo: ongoing outages and open maintenance only; times kept as text", () => {
  const m = mapNintendo(nin());
  assert.equal(m.indicator, "listed");
  assert.deepEqual(m.incidents.map((i) => i.title), ["Nintendo eShop"]);
  assert.equal(m.incidents[0].beginText, "Friday, November 20, 2026 6 :48 AM");
  assert.equal(m.incidents[0].startedAt, null);
  assert.deepEqual(m.maintenances.map((x) => [x.title, x.status]), [["Planned", "scheduled"]]);
  const quiet = nin();
  quiet.operational_statuses = [];
  assert.equal(mapNintendo(quiet).indicator, "none");
  quiet.temporary_maintenances[0].event_status = "2";
  assert.equal(mapNintendo(quiet).indicator, "maintenance");
  assert.equal(mapNintendo({ lang: "en_US" }), null);
});

test("mapNintendo: a state code it does not know is unknown, never none", () => {
  const b = nin();
  b.operational_statuses = [{ begin: "x", end: "", event_status: "7", message: "New code.", software_title: "Online play" }];
  b.temporary_maintenances = [];
  const m = mapNintendo(b);
  assert.equal(m.indicator, "unknown");
  assert.equal(m.incidents[0].status, "unknown");
  const c = nin();
  c.operational_statuses = [];
  c.temporary_maintenances = [{ begin: "a", end: "b", event_status: "9", message: "?", software_title: "Shop" }];
  assert.equal(mapNintendo(c).indicator, "unknown");
});

// ---------- /v1/service-status ----------

test("/v1/service-status: fixed ids only", async () => {
  const fail = () => { throw new Error("no fetch expected"); };
  await withFetch(fail, async () => {
    assert.equal((await handle(req("/v1/service-status"), {}, ctx())).status, 400);
    assert.equal((await handle(req("/v1/service-status?id=steam"), {}, ctx())).status, 400);
    assert.equal((await handle(req("/v1/service-status?id=__proto__"), {}, ctx())).status, 400);
    assert.equal((await handle(req("/v1/service-status?id=discord&url=x"), {}, ctx())).status, 400);
  });
});

test("/v1/service-status: Xbox asks for JSON; Nintendo with a byte order mark parses", async () => {
  const seen = [];
  await withFetch(async (url, init) => {
    seen.push([String(url), init.headers.Accept]);
    return String(url).includes("nintendo") ? jsonRes("﻿" + JSON.stringify(nin())) : jsonRes(xbox("None"));
  }, async () => {
    const x = await body(await handle(req("/v1/service-status?id=xbox"), {}, ctx()));
    assert.equal(x.status.indicator, "none");
    assert.equal(x.pageUrl, "https://support.xbox.com/en-US/xbox-live-status");
    const n = await body(await handle(req("/v1/service-status?id=nintendo"), {}, ctx()));
    assert.equal(n.status.indicator, "listed");
  });
  assert.deepEqual(seen[0], ["https://xnotify.xboxlive.com/servicestatusv6/US/en-US", "application/json"]);
});

test("/v1/service-status: PlayStation uses the caller's country and caches per country", async () => {
  const caches = fakeCache();
  await withFetch(async () => jsonRes(psnBody()), async () => {
    const c = ctx();
    const us = await body(await handle(req("/v1/service-status?id=psn-scea"), {}, c));
    assert.equal(us.status.country, "US");
    await Promise.all(c.waits);
    const ca = await body(await handle(req("/v1/service-status?id=psn-scea", { country: "CA" }), {}, ctx()));
    assert.equal(ca.status.country, "CA");
    assert.ok(ca.status.incidents.some((i) => i.text === "Canada only"));
  }, caches);
  assert.ok([...caches.store.keys()].some((k) => k.endsWith("/psn-scea/US")));
});

test("/v1/service-status: a failed feed is status null and not cached", async () => {
  const caches = fakeCache();
  await withFetch(async () => jsonRes({ nope: 1 }), async () => {
    const res = await handle(req("/v1/service-status?id=discord"), {}, ctx());
    assert.equal(res.status, 200);
    assert.equal(res.headers.get("cache-control"), "no-store");
    const b = await body(res);
    assert.equal(b.status, null);
    assert.equal(b.id, "discord");
  }, caches);
  assert.equal(caches.store.size, 0);
});
