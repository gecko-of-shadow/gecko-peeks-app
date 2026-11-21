// Update channel tests. A fake KV namespace stands in for RELEASES_KV. No network.
import test from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { handle } from "../src/index.js";
import { parseVersion, MAX_EXE_BYTES, MAX_PART_BYTES, MAX_PARTS } from "../src/update.js";

const BASE = "https://gecko-peeks-api.example.workers.dev";
const KEY = "k".repeat(48);
const body = async (res) => JSON.parse(await res.text());
const hex = (bytes) => createHash("sha256").update(bytes).digest("hex");

function fakeExe(size = 1_200_000, seed = 7) {
  const b = new Uint8Array(size);
  let x = seed;
  for (let i = 0; i < size; i++) { x = (x * 1103515245 + 12345) >>> 0; b[i] = x >>> 24; }
  b[0] = 0x4d; b[1] = 0x5a;
  return b;
}

async function readAll(value) {
  if (value === null || value === undefined) return new Uint8Array(0);
  if (typeof value === "string") return new TextEncoder().encode(value);
  if (value instanceof Uint8Array) return value;
  if (value instanceof ArrayBuffer) return new Uint8Array(value);
  return new Uint8Array(await new Response(value).arrayBuffer());
}

// Enough of the KV binding for these routes. put() reads a stream to the end and stores
// nothing when the stream errors, as KV does. Set failPut to make every put() throw like a
// KV refusal (for example the 1 GB storage limit of the Free plan).
class FakeKV {
  constructor() { this.values = new Map(); this.puts = 0; this.gets = []; this.deletes = []; this.failPut = null; }
  async put(key, value, opts = {}) {
    this.puts++;
    if (this.failPut) {
      if (value && typeof value.cancel === "function") await value.cancel();
      throw new Error(this.failPut);
    }
    const bytes = await readAll(value);
    this.values.set(key, { bytes, metadata: opts.metadata ?? null });
  }
  decode(o, type) {
    if (type === "stream") return new Response(o.bytes).body;
    if (type === "json") return JSON.parse(new TextDecoder().decode(o.bytes));
    if (type === "arrayBuffer") return o.bytes.slice().buffer;
    return new TextDecoder().decode(o.bytes);
  }
  async get(key, type = "text") {
    this.gets.push(key);
    const o = this.values.get(key);
    return o ? this.decode(o, typeof type === "object" ? type.type : type) : null;
  }
  async getWithMetadata(key, type = "text") {
    this.gets.push(key);
    const o = this.values.get(key);
    return o ? { value: this.decode(o, typeof type === "object" ? type.type : type), metadata: o.metadata } : { value: null, metadata: null };
  }
  async delete(key) { this.deletes.push(key); this.values.delete(key); }
  // Pages of two keys, so the cursor path runs too.
  async list({ prefix = "", cursor } = {}) {
    const all = [...this.values.keys()].filter((k) => k.startsWith(prefix)).sort();
    const from = cursor ? Number(cursor) : 0;
    const keys = all.slice(from, from + 2).map((name) => ({ name, metadata: this.values.get(name).metadata ?? undefined }));
    const done = from + 2 >= all.length;
    return done ? { keys, list_complete: true } : { keys, list_complete: false, cursor: String(from + 2) };
  }
}

const env = (extra = {}) => ({ PUBLISH_KEY: KEY, RELEASES_KV: new FakeKV(), ...extra });

function uploadPart(e, channel, version, file, n, bytes, opts = {}) {
  const headers = { "Content-Length": String(opts.length ?? bytes.length) };
  if (opts.sha !== false) headers["X-Part-Sha256"] = opts.sha ?? hex(bytes);
  if (opts.auth !== false) headers.Authorization = `Bearer ${opts.key ?? KEY}`;
  return handle(new Request(`${BASE}/v1/admin/upload/${channel}/${version}/${file}/part/${n}`, { method: "PUT", headers, body: bytes, duplex: "half" }), e, {});
}

function finalize(e, channel, version, file, payload, opts = {}) {
  const headers = { "Content-Type": "application/json" };
  if (opts.auth !== false) headers.Authorization = `Bearer ${opts.key ?? KEY}`;
  return handle(new Request(`${BASE}/v1/admin/finalize/${channel}/${version}/${file}`, { method: "POST", headers, body: JSON.stringify(payload) }), e, {});
}

// Splits like publish-release.ps1 does, with a small part size so a test exe has several parts.
const splitExe = (exe, partSize = 500_000) => {
  const out = [];
  for (let at = 0; at < exe.length; at += partSize) out.push(exe.subarray(at, at + partSize));
  return out;
};
const finalBody = (exe, partSize = 500_000) => {
  const parts = splitExe(exe, partSize);
  return { size: exe.length, sha256: hex(exe), parts: parts.length, partSha256s: parts.map(hex) };
};

async function publishFile(e, channel, version, file, exe, partSize = 500_000) {
  const parts = splitExe(exe, partSize);
  for (let n = 0; n < parts.length; n++) {
    const r = await uploadPart(e, channel, version, file, n, parts[n]);
    assert.equal(r.status, 201, `part ${n}`);
  }
  return finalize(e, channel, version, file, finalBody(exe, partSize));
}

const del = (e, channel, version) => handle(new Request(`${BASE}/v1/admin/release/${channel}/${version}`, {
  method: "DELETE", headers: { Authorization: `Bearer ${KEY}` },
}), e, {});
const versions = (e, channel) => handle(new Request(`${BASE}/v1/admin/versions/${channel}`, { headers: { Authorization: `Bearer ${KEY}` } }), e, {});

function promote(e, channel, payload, opts = {}) {
  const headers = { "Content-Type": "application/json" };
  if (opts.auth !== false) headers.Authorization = `Bearer ${opts.key ?? KEY}`;
  return handle(new Request(`${BASE}/v1/admin/promote/${channel}`, { method: "POST", headers, body: JSON.stringify(payload) }), e, {});
}

const get = (e, path) => handle(new Request(BASE + path), e, {});

test("versions are strict three-part numbers", () => {
  for (const ok of ["2.0.1", "0.0.0", "10.20.300"]) assert.equal(parseVersion(ok), ok);
  for (const bad of ["2.0", "v2.0.1", "2.0.1-beta", "02.0.1", "2.0.1.0", " 2.0.1", "2.0.1/..", "", null]) assert.equal(parseVersion(bad), null, String(bad));
});

test("latest: 404 no_release before a promote, no-cache", async () => {
  const e = env();
  const r = await get(e, "/v1/update/latest");
  assert.equal(r.status, 404);
  assert.deepEqual(await body(r), { error: "no_release" });
  assert.equal(r.headers.get("Cache-Control"), "no-cache");
});

test("latest: bad channel or extra param is 400; no KV binding is 503", async () => {
  assert.equal((await get(env(), "/v1/update/latest?channel=beta")).status, 400);
  assert.equal((await get(env(), "/v1/update/latest?channel=stable&x=1")).status, 400);
  assert.equal((await get({}, "/v1/update/latest")).status, 503);
});

test("upload parts, finalize, promote, latest, download: the full path on devtest", async () => {
  const e = env();
  const exe = fakeExe();
  let r = await publishFile(e, "devtest", "2.0.1", "GeckoPeeksDev.exe", exe);
  assert.equal(r.status, 201);
  assert.equal(r.headers.get("Cache-Control"), "no-store");
  const fin = await body(r);
  assert.equal(fin.parts, 3);
  assert.deepEqual(fin.partSizes, [500_000, 500_000, 200_000]);
  assert.deepEqual(fin.partSha256s, splitExe(exe).map(hex));
  assert.equal(e.RELEASES_KV.values.get("devtest/2.0.1/GeckoPeeksDev.exe/part/1").metadata.sha256, hex(exe.subarray(500_000, 1_000_000)));
  const meta = JSON.parse(new TextDecoder().decode(e.RELEASES_KV.values.get("devtest/2.0.1/GeckoPeeksDev.exe/meta").bytes));
  assert.equal(meta.size, exe.length);
  assert.equal(meta.sha256, hex(exe));

  r = await promote(e, "devtest", { version: "2.0.1", sha256: hex(exe), size: exe.length, notes: "Dev test.\r\n" });
  assert.equal(r.status, 200);

  r = await get(e, "/v1/update/latest?channel=devtest");
  assert.equal(r.status, 200);
  assert.equal(r.headers.get("Cache-Control"), "no-cache");
  const m = await body(r);
  assert.equal(m.version, "2.0.1");
  assert.equal(m.sha256, hex(exe));
  assert.equal(m.size, exe.length);
  assert.equal(m.notes, "Dev test.");
  assert.equal(m.url, `${BASE}/v1/update/download/devtest/2.0.1`);
  assert.ok(!Number.isNaN(Date.parse(m.publishedAt)));

  // Stable stays empty.
  assert.equal((await get(e, "/v1/update/latest")).status, 404);

  r = await get(e, "/v1/update/download/devtest/2.0.1");
  assert.equal(r.status, 200);
  assert.equal(r.headers.get("Content-Type"), "application/octet-stream");
  assert.equal(r.headers.get("Content-Length"), String(exe.length));
  assert.match(r.headers.get("Cache-Control"), /immutable/);
  assert.equal(r.headers.get("ETag"), `"${hex(exe)}"`);
  const got = new Uint8Array(await r.arrayBuffer());
  assert.equal(got.length, exe.length);
  assert.equal(hex(got), hex(exe));
});

test("download: a single-part exe streams whole", async () => {
  const e = env();
  const exe = fakeExe(1_100_000, 3);
  assert.equal((await publishFile(e, "stable", "2.0.0", "GeckoPeeks.exe", exe, MAX_PART_BYTES)).status, 201);
  const r = await get(e, "/v1/update/download/stable/2.0.0");
  assert.equal(r.status, 200);
  assert.equal(hex(new Uint8Array(await r.arrayBuffer())), hex(exe));
});

test("download: a part lost after finalize cuts the stream short", async () => {
  const e = env();
  const exe = fakeExe();
  await publishFile(e, "devtest", "2.0.1", "GeckoPeeksDev.exe", exe);
  e.RELEASES_KV.values.delete("devtest/2.0.1/GeckoPeeksDev.exe/part/1");
  const r = await get(e, "/v1/update/download/devtest/2.0.1");
  assert.equal(r.status, 200);
  await assert.rejects(r.arrayBuffer());
  // With part 0 gone there is nothing to send, so it is a clean 404.
  e.RELEASES_KV.values.delete("devtest/2.0.1/GeckoPeeksDev.exe/part/0");
  assert.equal((await get(e, "/v1/update/download/devtest/2.0.1")).status, 404);
});

test("download: bad paths are 404, other methods 405, unfinalized parts are 404", async () => {
  const e = env();
  for (const p of ["/v1/update/download/devtest/2.0", "/v1/update/download/beta/2.0.1", "/v1/update/download/devtest/2.0.1/x", "/v1/update/download/stable/9.9.9"]) {
    assert.equal((await get(e, p)).status, 404, p);
  }
  const r = await handle(new Request(`${BASE}/v1/update/download/stable/2.0.0`, { method: "POST" }), e, {});
  assert.equal(r.status, 405);
  assert.equal((await uploadPart(e, "stable", "2.0.0", "GeckoPeeks.exe", 0, fakeExe())).status, 201);
  assert.equal((await get(e, "/v1/update/download/stable/2.0.0")).status, 404);
});

test("download is rate limited per IP on its own bucket; latest is not", async () => {
  const e = env({ DOWNLOAD_LIMITER: { limit: async () => ({ success: false }) } });
  assert.equal((await get(e, "/v1/update/download/stable/2.0.0")).status, 429);
  assert.equal((await get(e, "/v1/update/latest")).status, 404);
  // A used-up pulse/asn bucket does not block the download.
  const p = env({ RATE_LIMITER: { limit: async () => ({ success: false }) } });
  assert.equal((await get(p, "/v1/update/download/stable/2.0.0")).status, 404);
});

test("admin: 401 without a key, with a wrong key, or when PUBLISH_KEY is unset", async () => {
  const exe = fakeExe();
  for (const [e, opts] of [
    [env(), { auth: false }],
    [env(), { key: "wrong".repeat(10) }],
    [env(), { key: KEY + "x" }],
    [env({ PUBLISH_KEY: undefined }), {}],
    [env({ PUBLISH_KEY: "" }), { key: "" }],
    [env({ PUBLISH_KEY: "short" }), { key: "short" }],
  ]) {
    const u = await uploadPart(e, "devtest", "2.0.1", "GeckoPeeksDev.exe", 0, exe, opts);
    assert.equal(u.status, 401);
    assert.equal(u.headers.get("Cache-Control"), "no-store");
    const f = await finalize(e, "devtest", "2.0.1", "GeckoPeeksDev.exe", { size: exe.length, sha256: hex(exe), parts: 1, partSha256s: [hex(exe)] }, opts);
    assert.equal(f.status, 401);
    const p = await promote(e, "devtest", { version: "2.0.1", sha256: hex(exe), size: exe.length }, opts);
    assert.equal(p.status, 401);
    const auth = opts.auth === false ? {} : { Authorization: `Bearer ${opts.key ?? KEY}` };
    const d = await handle(new Request(`${BASE}/v1/admin/release/devtest/2.0.1`, { method: "DELETE", headers: auth }), e, {});
    assert.equal(d.status, 401);
    const v = await handle(new Request(`${BASE}/v1/admin/versions/devtest`, { headers: auth }), e, {});
    assert.equal(v.status, 401);
    assert.equal(e.RELEASES_KV.deletes.length, 0);
    assert.equal(e.RELEASES_KV.puts, 0);
  }
  // Wrong methods on admin paths are 405, not a write.
  for (const path of ["/v1/admin/promote/devtest", "/v1/admin/finalize/devtest/2.0.1/GeckoPeeksDev.exe"]) {
    assert.equal((await handle(new Request(BASE + path), env(), {})).status, 405, path);
  }
  const put = await handle(new Request(`${BASE}/v1/admin/upload/devtest/2.0.1/GeckoPeeksDev.exe/part/0`, { method: "POST" }), env(), {});
  assert.equal(put.status, 405);
});

test("upload part: validation of channel, file, part index, length and cap", async () => {
  const e = env();
  const part = fakeExe(1000);
  assert.equal((await uploadPart(e, "devtest", "2.0.1", "GeckoPeeks.exe", 0, part)).status, 400);
  assert.equal((await uploadPart(e, "stable", "2.0.1", "GeckoPeeksDev.exe", 0, part)).status, 400);
  assert.equal((await uploadPart(e, "beta", "2.0.1", "GeckoPeeks.exe", 0, part)).status, 400);
  assert.equal((await uploadPart(e, "stable", "2.0", "GeckoPeeks.exe", 0, part)).status, 400);
  for (const n of ["-1", "01", "x", String(MAX_PARTS)]) {
    assert.equal((await uploadPart(e, "stable", "2.0.1", "GeckoPeeks.exe", n, part)).status, 400, n);
  }
  const authed = { Authorization: `Bearer ${KEY}`, "Content-Length": "1000" };
  const wrongTail = await handle(new Request(`${BASE}/v1/admin/upload/stable/2.0.1/GeckoPeeks.exe/piece/0`, { method: "PUT", headers: authed, body: part, duplex: "half" }), e, {});
  assert.equal(wrongTail.status, 404);
  // The old single-file upload path is gone.
  const old = await handle(new Request(`${BASE}/v1/admin/upload/stable/2.0.1/GeckoPeeks.exe`, { method: "PUT", headers: authed, body: part, duplex: "half" }), e, {});
  assert.equal(old.status, 404);
  assert.equal((await uploadPart(e, "stable", "2.0.1", "GeckoPeeks.exe", 0, part, { length: MAX_PART_BYTES + 1 })).status, 413);
  assert.equal((await uploadPart(e, "stable", "2.0.1", "GeckoPeeks.exe", 0, new Uint8Array(0))).status, 400);
  assert.equal(e.RELEASES_KV.puts, 0);
});

test("upload part: a body shorter or longer than Content-Length stores nothing", async () => {
  const e = env();
  const part = fakeExe(1000);
  for (const length of ["2000", "500"]) {
    const r = await handle(new Request(`${BASE}/v1/admin/upload/stable/2.0.1/GeckoPeeks.exe/part/0`, {
      method: "PUT", headers: { Authorization: `Bearer ${KEY}`, "Content-Length": length, "X-Part-Sha256": hex(part) }, body: new Response(part).body, duplex: "half",
    }), e, {});
    assert.equal(r.status, 400, length);
  }
  assert.equal(e.RELEASES_KV.values.size, 0);
});

test("upload part: refuses to overwrite a part or a finalized version", async () => {
  const e = env();
  const exe = fakeExe();
  assert.equal((await uploadPart(e, "stable", "2.0.0", "GeckoPeeks.exe", 0, exe)).status, 201);
  const again = await uploadPart(e, "stable", "2.0.0", "GeckoPeeks.exe", 0, fakeExe(1_200_000, 99));
  assert.equal(again.status, 409);
  const ab = await body(again);
  assert.equal(ab.error, "part_exists");
  // The stored hash comes back, so the uploader can see it is not its part.
  assert.equal(ab.sha256, hex(exe));
  assert.equal(ab.size, exe.length);
  assert.equal(hex(e.RELEASES_KV.values.get("stable/2.0.0/GeckoPeeks.exe/part/0").bytes), hex(exe));
  assert.equal((await finalize(e, "stable", "2.0.0", "GeckoPeeks.exe", { size: exe.length, sha256: hex(exe), parts: 1, partSha256s: [hex(exe)] })).status, 201);
  const late = await uploadPart(e, "stable", "2.0.0", "GeckoPeeks.exe", 1, fakeExe(1000));
  assert.equal(late.status, 409);
  assert.equal((await body(late)).error, "version_exists");
});

test("finalize: checks every part, the size sum, the input and refuses a second finalize", async () => {
  const e = env();
  const exe = fakeExe();
  const file = ["devtest", "2.0.3", "GeckoPeeksDev.exe"];
  assert.equal((await uploadPart(e, ...file, 0, exe.subarray(0, 600_000))).status, 201);
  // Part 1 is not stored yet.
  const ok = { size: exe.length, sha256: hex(exe), parts: 2, partSha256s: [hex(exe.subarray(0, 600_000)), hex(exe.subarray(600_000))] };
  let r = await finalize(e, ...file, ok);
  assert.equal(r.status, 409);
  assert.equal((await body(r)).error, "missing_part");
  assert.equal((await uploadPart(e, ...file, 1, exe.subarray(600_000))).status, 201);
  r = await finalize(e, ...file, { ...ok, size: exe.length + 1 });
  assert.equal(r.status, 409);
  assert.equal((await body(r)).error, "size_mismatch");
  for (const bad of [
    { size: exe.length, parts: 2 },
    { size: exe.length, sha256: "xyz", parts: 2 },
    { sha256: hex(exe), parts: 2 },
    { size: exe.length, sha256: hex(exe) },
    { size: exe.length, sha256: hex(exe), parts: 0 },
    { size: exe.length, sha256: hex(exe), parts: MAX_PARTS + 1 },
    { size: 1000, sha256: hex(exe), parts: 2 },
    { size: exe.length, sha256: hex(exe), parts: 2 },
    { ...ok, partSha256s: [ok.partSha256s[0]] },
    { ...ok, partSha256s: [ok.partSha256s[0], "xyz"] },
  ]) {
    assert.equal((await finalize(e, ...file, bad)).status, 400, JSON.stringify(bad));
  }
  assert.equal((await finalize(e, ...file, { ...ok, size: MAX_EXE_BYTES + 1 })).status, 413);
  assert.equal(e.RELEASES_KV.values.has("devtest/2.0.3/GeckoPeeksDev.exe/meta"), false);
  r = await finalize(e, ...file, ok);
  assert.equal(r.status, 201);
  r = await finalize(e, ...file, ok);
  assert.equal(r.status, 409);
  assert.equal((await body(r)).error, "version_exists");
  // Finalize never read the meta key by get(), so no "not found" is cached for promote.
  assert.equal(e.RELEASES_KV.gets.some((k) => k.endsWith("/meta")), false);
});

test("promote refuses a wrong sha256, a wrong size, or an unfinalized exe", async () => {
  const e = env();
  const exe = fakeExe();
  await publishFile(e, "devtest", "2.0.1", "GeckoPeeksDev.exe", exe);
  await promote(e, "devtest", { version: "2.0.1", sha256: hex(exe), size: exe.length });
  const before = await body(await get(e, "/v1/update/latest?channel=devtest"));

  let r = await promote(e, "devtest", { version: "2.0.1", sha256: "a".repeat(64), size: exe.length });
  assert.equal(r.status, 409);
  assert.equal((await body(r)).error, "sha256_mismatch");
  r = await promote(e, "devtest", { version: "2.0.1", sha256: hex(exe), size: exe.length + 1 });
  assert.equal(r.status, 409);
  r = await promote(e, "devtest", { version: "2.0.2", sha256: hex(exe), size: exe.length });
  assert.equal(r.status, 404);
  // Parts without a meta are not promotable.
  await uploadPart(e, "devtest", "2.0.4", "GeckoPeeksDev.exe", 0, exe);
  r = await promote(e, "devtest", { version: "2.0.4", sha256: hex(exe), size: exe.length });
  assert.equal(r.status, 404);
  r = await promote(e, "devtest", { version: "2.0.1" });
  assert.equal(r.status, 400);

  // The manifest did not move.
  const after = await body(await get(e, "/v1/update/latest?channel=devtest"));
  assert.deepEqual(after, before);
});

test("existing routes still answer as before", async () => {
  const r = await handle(new Request(BASE + "/v1/update"), env(), {});
  assert.equal(r.status, 404);
  const h = await handle(new Request(BASE + "/v1/health"), env(), {});
  assert.equal(h.status, 200);
});

test("upload part: X-Part-Sha256 is required and must be 64 hex", async () => {
  const e = env();
  const part = fakeExe(1000);
  for (const sha of [false, "", "xyz", "a".repeat(63), "g".repeat(64)]) {
    const r = await uploadPart(e, "stable", "2.0.1", "GeckoPeeks.exe", 0, part, { sha });
    assert.equal(r.status, 400, String(sha));
  }
  assert.equal(e.RELEASES_KV.puts, 0);
});

test("re-run after a rebuild: a mixed set of parts cannot be finalized", async () => {
  // A CI job stored part 0 of build A, then a re-run rebuilt the exe as build B.
  const e = env();
  const file = ["stable", "2.0.5", "GeckoPeeks.exe"];
  const a = fakeExe(1_200_000, 1);
  const b = fakeExe(1_200_000, 2);
  assert.equal((await uploadPart(e, ...file, 0, splitExe(a)[0])).status, 201);
  // Build B: part 0 is already stored, with the hash of build A.
  const r0 = await uploadPart(e, ...file, 0, splitExe(b)[0]);
  assert.equal(r0.status, 409);
  const b0 = await body(r0);
  assert.equal(b0.sha256, hex(splitExe(a)[0]));
  assert.notEqual(b0.sha256, hex(splitExe(b)[0]));
  for (const n of [1, 2]) assert.equal((await uploadPart(e, ...file, n, splitExe(b)[n])).status, 201);
  const r = await finalize(e, ...file, finalBody(b));
  assert.equal(r.status, 409);
  const rb = await body(r);
  assert.equal(rb.error, "part_mismatch");
  assert.equal(rb.part, 0);
  assert.equal(e.RELEASES_KV.values.has("stable/2.0.5/GeckoPeeks.exe/meta"), false);
  // The mixed version can be deleted (it has no meta) and then uploaded again whole.
  assert.equal((await del(e, "stable", "2.0.5")).status, 200);
  assert.equal([...e.RELEASES_KV.values.keys()].some((k) => k.startsWith("stable/2.0.5/")), false);
  assert.equal((await publishFile(e, ...file, b)).status, 201);
});

test("finalize refuses a part stored with no hash", async () => {
  const e = env();
  const exe = fakeExe();
  await e.RELEASES_KV.put("devtest/2.0.6/GeckoPeeksDev.exe/part/0", exe, { metadata: { size: exe.length } });
  const r = await finalize(e, "devtest", "2.0.6", "GeckoPeeksDev.exe", { size: exe.length, sha256: hex(exe), parts: 1, partSha256s: [hex(exe)] });
  assert.equal(r.status, 409);
  assert.equal((await body(r)).error, "part_mismatch");
  const again = await uploadPart(e, "devtest", "2.0.6", "GeckoPeeksDev.exe", 0, exe);
  assert.equal(again.status, 409);
  assert.equal((await body(again)).sha256, null);
});

test("a meta from before per-part hashes still downloads and promotes", async () => {
  const e = env();
  const exe = fakeExe();
  const parts = splitExe(exe);
  for (let n = 0; n < parts.length; n++) {
    await e.RELEASES_KV.put(`devtest/2.0.1/GeckoPeeksDev.exe/part/${n}`, parts[n], { metadata: { size: parts[n].length } });
  }
  await e.RELEASES_KV.put("devtest/2.0.1/GeckoPeeksDev.exe/meta", JSON.stringify({
    size: exe.length, sha256: hex(exe), parts: parts.length, partSizes: parts.map((p) => p.length), finalizedAt: "2026-11-21T00:00:00.000Z",
  }));
  assert.equal((await promote(e, "devtest", { version: "2.0.1", sha256: hex(exe), size: exe.length })).status, 200);
  const r = await get(e, "/v1/update/download/devtest/2.0.1");
  assert.equal(r.status, 200);
  assert.equal(hex(new Uint8Array(await r.arrayBuffer())), hex(exe));
});

test("a KV write error is 507 with the real reason, not a length error", async () => {
  const e = env();
  e.RELEASES_KV.failPut = "KV storage limit exceeded";
  const part = fakeExe(1000);
  const r = await uploadPart(e, "stable", "2.0.1", "GeckoPeeks.exe", 0, part);
  assert.equal(r.status, 507);
  assert.deepEqual(await body(r), { error: "storage_write_failed", detail: "KV storage limit exceeded" });
  e.RELEASES_KV.failPut = null;
  const exe = fakeExe();
  for (const [n, p] of splitExe(exe).entries()) assert.equal((await uploadPart(e, "stable", "2.0.1", "GeckoPeeks.exe", n, p)).status, 201);
  e.RELEASES_KV.failPut = "daily write limit";
  assert.equal((await finalize(e, "stable", "2.0.1", "GeckoPeeks.exe", finalBody(exe))).status, 507);
  e.RELEASES_KV.failPut = null;
  assert.equal((await finalize(e, "stable", "2.0.1", "GeckoPeeks.exe", finalBody(exe))).status, 201);
  e.RELEASES_KV.failPut = "daily write limit";
  assert.equal((await promote(e, "stable", { version: "2.0.1", sha256: hex(exe), size: exe.length })).status, 507);
});

test("delete: removes meta and parts, refuses the latest version, lists versions", async () => {
  const e = env();
  const old = fakeExe(1_200_000, 4);
  const cur = fakeExe(1_200_000, 5);
  await publishFile(e, "devtest", "2.0.1", "GeckoPeeksDev.exe", old);
  await publishFile(e, "devtest", "2.0.2", "GeckoPeeksDev.exe", cur);
  await uploadPart(e, "devtest", "2.0.3", "GeckoPeeksDev.exe", 0, cur.subarray(0, 500_000));
  await publishFile(e, "stable", "2.0.1", "GeckoPeeks.exe", old);
  assert.equal((await promote(e, "devtest", { version: "2.0.2", sha256: hex(cur), size: cur.length })).status, 200);

  let r = await versions(e, "devtest");
  assert.equal(r.status, 200);
  const list = await body(r);
  assert.equal(list.latest, "2.0.2");
  assert.deepEqual(list.versions.sort((x, y) => x.version.localeCompare(y.version)), [
    { version: "2.0.1", finalized: true, parts: 3 },
    { version: "2.0.2", finalized: true, parts: 3 },
    { version: "2.0.3", finalized: false, parts: 1 },
  ]);

  r = await del(e, "devtest", "2.0.2");
  assert.equal(r.status, 409);
  assert.equal((await body(r)).error, "is_latest");
  assert.equal(e.RELEASES_KV.deletes.length, 0);

  r = await del(e, "devtest", "2.0.1");
  assert.equal(r.status, 200);
  assert.equal(e.RELEASES_KV.deletes[0], "devtest/2.0.1/GeckoPeeksDev.exe/meta");
  assert.equal([...e.RELEASES_KV.values.keys()].some((k) => k.startsWith("devtest/2.0.1/")), false);
  // The other channel and the latest version are untouched.
  assert.equal((await get(e, "/v1/update/download/stable/2.0.1")).status, 200);
  assert.equal((await get(e, "/v1/update/download/devtest/2.0.1")).status, 404);
  const d = await get(e, "/v1/update/download/devtest/2.0.2");
  assert.equal(hex(new Uint8Array(await d.arrayBuffer())), hex(cur));
  // An unfinalized version can be deleted too.
  assert.equal((await del(e, "devtest", "2.0.3")).status, 200);
  assert.deepEqual((await body(await versions(e, "devtest"))).versions.map((v) => v.version), ["2.0.2"]);

  for (const p of ["beta/2.0.1", "devtest/2.0", "devtest/2.0.1/x"]) {
    const x = await handle(new Request(`${BASE}/v1/admin/release/${p}`, { method: "DELETE", headers: { Authorization: `Bearer ${KEY}` } }), e, {});
    assert.ok([400, 404].includes(x.status), p);
  }
  assert.equal((await handle(new Request(`${BASE}/v1/admin/release/devtest/2.0.1`), e, {})).status, 405);
});
