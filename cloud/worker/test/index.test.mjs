// Run with: node --test cloud/worker/test
// These tests use no network. A fake fetch stands in for the Radar API.
import test from "node:test";
import assert from "node:assert/strict";
import { handle, classifyIp, parseAsn, parseCountry, mapHijacks, mapLeaks, mapOutages, mapAnomalies, mapTraffic, iso } from "../src/index.js";

const BASE = "https://gecko-peeks-api.example.workers.dev";
const req = (path, init = {}, cf = { asn: 852, country: "CA", colo: "YVR" }) => {
  const r = new Request(BASE + path, init);
  Object.defineProperty(r, "cf", { value: cf });
  return r;
};
const body = async (res) => JSON.parse(await res.text());

test("validation", () => {
  assert.equal(parseAsn("6507"), 6507);
  assert.equal(parseAsn("AS852"), 852);
  assert.equal(parseAsn("0"), null);
  assert.equal(parseAsn("4294967296"), null);
  assert.equal(parseAsn("1e3"), null);
  assert.equal(parseCountry("ca"), "CA");
  assert.equal(parseCountry("CAN"), null);
  assert.deepEqual(classifyIp("4.68.111.181"), { ip: "4.68.111.181", public: true });
  for (const ip of ["10.0.0.1", "127.0.0.1", "169.254.1.1", "100.64.0.1", "172.16.5.5", "192.168.1.1", "224.0.0.1", "255.255.255.255", "0.1.2.3", "192.0.2.1", "198.18.0.1"]) {
    assert.equal(classifyIp(ip).public, false, ip);
  }
  for (const ip of ["::1", "::", "fe80::1", "fd00::1", "ff02::1", "2001:db8::1", "::ffff:8.8.8.8", "2002::1"]) {
    assert.equal(classifyIp(ip).public, false, ip);
  }
  assert.equal(classifyIp("2606:4700:4700::1111").public, true);
  assert.equal(classifyIp("2606:4700:4700:0:0:0:0:1111").ip, "2606:4700:4700:0:0:0:0:1111");
  assert.equal(classifyIp("01.2.3.4"), null);
  assert.equal(classifyIp("example.com"), null);
  assert.equal(classifyIp("1:2:3:4:5:6:7:8:9"), null);
});

test("iso adds Z to zone-less times", () => {
  assert.equal(iso("2026-11-21T01:44:53.019"), "2026-11-21T01:44:53.019Z");
  assert.equal(iso(null), null);
});

test("routing: 404, 405", async () => {
  let r = await handle(req("/nope"), {}, {});
  assert.equal(r.status, 404);
  assert.deepEqual(await body(r), { error: "not_found" });
  r = await handle(req("/v1/health", { method: "POST" }), {}, {});
  assert.equal(r.status, 405);
});

test("health and edge without a token", async () => {
  let r = await handle(req("/v1/health"), {}, {});
  const h = await body(r);
  assert.equal(h.ok, true);
  assert.equal(h.radar, false);
  assert.equal(r.headers.get("Cache-Control"), "no-store");
  r = await handle(req("/v1/edge"), {}, {});
  const e = await body(r);
  assert.equal(e.asn, 852);
  assert.equal(e.colo, "YVR");
  assert.equal(e.lat, null);
  assert.ok(!("ip" in e));
});

test("pulse without a token has radar:false and null fields", async () => {
  const r = await handle(req("/v1/pulse"), {}, {});
  assert.equal(r.status, 200);
  const p = await body(r);
  assert.equal(p.radar, false);
  assert.equal(p.riot.asn, 6507);
  assert.equal(p.riot.prefix, null);
  assert.equal(p.isp.asn, 852);
  assert.equal(p.isp.outages, null);
  assert.equal(p.country.code, "CA");
  assert.equal(p.country.iqi, null);
});

test("pulse bad params give 400", async () => {
  // isp and country may only name the caller's own network (cf: AS852, CA).
  for (const q of ["?isp=abc", "?isp=0", "?country=CAN", "?country=1A", "?foo=1", "?isp=3356", "?country=US"]) {
    const r = await handle(req("/v1/pulse" + q), {}, {});
    assert.equal(r.status, 400, q);
  }
  const ok = await handle(req("/v1/pulse?isp=852&country=ca"), {}, {});
  assert.equal(ok.status, 200);
});

test("no CORS header: other websites cannot read the answers", async () => {
  for (const path of ["/v1/health", "/v1/edge", "/v1/pulse", "/v1/asn?ips=4.68.111.181"]) {
    const r = await handle(req(path), {}, {});
    assert.equal(r.headers.get("Access-Control-Allow-Origin"), null, path);
  }
});

test("rate limiter: 429 on pulse and asn when the binding says no", async () => {
  const keys = [];
  const env = { RATE_LIMITER: { limit: async ({ key }) => { keys.push(key); return { success: false }; } } };
  const init = { headers: { "cf-connecting-ip": "203.0.113.9" } };
  let r = await handle(req("/v1/pulse", init), env, {});
  assert.equal(r.status, 429);
  r = await handle(req("/v1/asn?ips=4.68.111.181", init), env, {});
  assert.equal(r.status, 429);
  r = await handle(req("/v1/edge", init), env, {});
  assert.equal(r.status, 200);
  assert.deepEqual(keys, ["203.0.113.9", "203.0.113.9"]);
});

test("asn: validation and private drop", async () => {
  let r = await handle(req("/v1/asn"), {}, {});
  assert.equal(r.status, 400);
  r = await handle(req("/v1/asn?ips=hello"), {}, {});
  assert.equal(r.status, 400);
  const many = Array.from({ length: 16 }, (_, i) => `8.8.8.${i}`).join(",");
  r = await handle(req("/v1/asn?ips=" + many), {}, {});
  assert.equal(r.status, 400);
  r = await handle(req("/v1/asn?ips=4.68.111.181,10.0.0.1"), {}, {});
  const a = await body(r);
  assert.deepEqual(a, { results: [{ ip: "4.68.111.181", asn: null, name: null, country: null }] });
});

test("pulse with a fake Radar: a failed call becomes null, the rest map", async () => {
  const realFetch = globalThis.fetch;
  globalThis.fetch = async (url) => {
    const u = new URL(url);
    const ok = (result) => new Response(JSON.stringify({ success: true, result }));
    if (u.pathname.endsWith("/radar/bgp/routes/pfx2as")) return ok({ prefix_origins: [{ origin: 6507, peer_count: 73, prefix: "192.207.0.0/24", rpki_validation: "Valid" }], meta: { total_peers: 76 } });
    if (u.pathname.endsWith("/radar/entities/asns/6507")) return ok({ asn: { name: "RIOT-NA1" } });
    if (u.pathname.endsWith("/radar/netflows/timeseries")) return ok({ serie_0: { timestamps: ["2026-11-20T02:00:00Z", "2026-11-20T03:00:00Z"], values: ["0.5", "0.25"] } });
    if (u.pathname.endsWith("/radar/quality/iqi/summary")) return ok({ summary_0: { p25: "18.39", p50: "27.53", p75: "46.96" } });
    if (u.pathname.endsWith("/radar/bgp/leaks/events")) return new Response("boom", { status: 500 });
    return ok({ annotations: [], trafficAnomalies: [], events: [] });
  };
  try {
    const r = await handle(req("/v1/pulse?isp=852&country=ca"), { RADAR_TOKEN: "fake" }, {});
    const p = await body(r);
    assert.equal(p.radar, true);
    assert.equal(p.riot.name, "RIOT-NA1");
    assert.deepEqual(p.riot.prefix, { prefix: "192.207.0.0/24", origin: 6507, rpki: "Valid", peers: 73, totalPeers: 76 });
    assert.deepEqual(p.riot.traffic.values, [1, 0.5]);
    assert.equal(p.riot.leaks, null);
    assert.deepEqual(p.riot.outages, []);
    assert.equal(p.country.code, "CA");
    assert.deepEqual(p.country.iqi.latency, { p25: 18.39, p50: 27.53, p75: 46.96 });
  } finally {
    globalThis.fetch = realFetch;
  }
});

test("asn: 15 IPs stay under the subrequest budget and use the cache", async () => {
  const realFetch = globalThis.fetch;
  const realCaches = globalThis.caches;
  let calls = 0;
  globalThis.fetch = async () => { calls++; return new Response(JSON.stringify({ success: true, result: { asn: { asn: 3356, name: "LEVEL3", country: "US" } } })); };
  const store = new Map();
  globalThis.caches = { default: {
    match: async (k) => { calls++; const v = store.get(k.url); return v ? new Response(v) : undefined; },
    put: async (k, r) => { calls++; store.set(k.url, await r.text()); },
  } };
  const ips = Array.from({ length: 32 }, (_, i) => `8.8.${i + 1}.8`).join(",");
  const waits = [];
  const ctx = { waitUntil: (p) => waits.push(p) };
  try {
    // 15 IPs: cold costs 45 (match, fetch, put), warm costs 15 and does no fetch.
    const ips15 = ips.split(",").slice(0, 15).join(",");
    let r = await handle(req("/v1/asn?ips=" + ips15), { RADAR_TOKEN: "fake" }, ctx);
    await Promise.all(waits);
    let rows = (await body(r)).results;
    assert.equal(rows.length, 15);
    assert.equal(calls, 45);
    calls = 0;
    r = await handle(req("/v1/asn?ips=" + ips15), { RADAR_TOKEN: "fake" }, ctx);
    rows = (await body(r)).results;
    assert.equal(rows.filter((x) => x.asn === null).length, 0);
    assert.equal(calls, 15);
  } finally {
    globalThis.fetch = realFetch;
    globalThis.caches = realCaches;
  }
});

test("pulse: the Riot block is cached once and reused for another network", async () => {
  const realFetch = globalThis.fetch;
  const realCaches = globalThis.caches;
  const paths = [];
  globalThis.fetch = async (url) => {
    const u = new URL(url);
    paths.push(u.pathname + u.search);
    const ok = (result) => new Response(JSON.stringify({ success: true, result }));
    if (u.pathname.endsWith("/radar/entities/asns/6507")) return ok({ asn: { name: "RIOT-NA1" } });
    return ok({ annotations: [], trafficAnomalies: [], events: [] });
  };
  const store = new Map();
  globalThis.caches = { default: {
    match: async (k) => { const v = store.get(k.url); return v ? new Response(v) : undefined; },
    put: async (k, r) => { store.set(k.url, await r.text()); },
  } };
  const waits = [];
  const ctx = { waitUntil: (p) => waits.push(p) };
  try {
    await handle(req("/v1/pulse"), { RADAR_TOKEN: "fake" }, ctx);
    await Promise.all(waits);
    const riotCalls = paths.filter((p) => p.includes("6507") || p.includes("pfx2as")).length;
    assert.ok(riotCalls > 0);
    paths.length = 0;
    const r = await handle(req("/v1/pulse", {}, { asn: 3356, country: "US" }), { RADAR_TOKEN: "fake" }, ctx);
    const p = await body(r);
    assert.equal(p.riot.name, "RIOT-NA1");
    assert.equal(p.isp.asn, 3356);
    assert.equal(paths.filter((x) => x.includes("6507") || x.includes("pfx2as")).length, 0);
  } finally {
    globalThis.fetch = realFetch;
    globalThis.caches = realCaches;
  }
});

test("event mappers", () => {
  const h = mapHijacks({
    asn_info: [{ asn: 26548, org_name: "PureVoltage" }],
    events: [{ min_hijack_ts: "2026-11-21T01:44:10.447", max_hijack_ts: "2026-11-21T01:44:59.589", on_going_count: 2, hijacker_asn: 26548, prefixes: ["2602:f38e:2::/48"], victim_asns: [14475], confidence_score: 8, peer_ip_count: 2 }],
  }, { asn: 6507 });
  assert.equal(h[0].kind, "hijack");
  assert.equal(h[0].end, null);
  assert.equal(h[0].url, "https://radar.cloudflare.com/routing/as6507");
  const l = mapLeaks({ events: [{ min_ts: "2026-11-21T01:44:04", max_ts: "2026-11-21T01:45:23", finished: true, leak_asn: 268267, leak_seg: [1, 2], prefix_count: 3 }] });
  assert.equal(l[0].end, "2026-11-21T01:45:23.000Z");
  const o = mapOutages({ annotations: [{ description: "x", startDate: "2026-11-18T19:15:00Z", endDate: null, outage: { outageType: "NETWORK", outageCause: "UNKNOWN" } }] }, { country: "CA" });
  assert.equal(o[0].url, "https://radar.cloudflare.com/ca");
  const a = mapAnomalies({ trafficAnomalies: [{ startDate: "2026-11-20T12:30:00Z", endDate: null, status: "VERIFIED", asnDetails: { asn: "15796", name: "SALT-CH" } }] });
  assert.equal(a[0].url, "https://radar.cloudflare.com/as15796");
  assert.equal(mapTraffic({}), null);
});
