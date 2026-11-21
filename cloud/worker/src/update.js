// Update channel for the Gecko Peeks desktop app.
// The exe and a small manifest live in the Workers KV namespace bound as RELEASES_KV.
// A KV value holds at most 25 MiB, so the exe is stored in parts:
//   <channel>/latest.json                       the promoted release
//   <channel>/<version>/<file>/part/<n>         one part of the exe, n = 0, 1, 2, ...
//                                               KV metadata {size, sha256} of that part
//   <channel>/<version>/<file>/meta             {size, sha256, parts, partSizes, partSha256s}
// Public routes read them. Admin routes need the PUBLISH_KEY secret.
// Parts and meta are never overwritten. Only DELETE /v1/admin/release removes a version,
// and it refuses the version that latest.json points to.
//
// The Free plan gives 10 ms of CPU per request. So the Worker never hashes an exe or a part.
// It records the SHA-256 values that the uploader sends: one per part and one for the whole
// file. Finalize refuses a set of parts whose recorded hashes differ from the uploader's list,
// so parts from two different builds cannot become one release. The app checks the SHA-256
// of the whole file after the download. Bodies go through native streams, not through JS loops.
//
// Workers KV on the Free plan holds 1 GB per account. The publish script deletes old versions.

export const CHANNELS = { stable: "GeckoPeeks.exe", devtest: "GeckoPeeksDev.exe" };
// One part fits in one KV value (25 MiB) with room to spare.
export const MAX_PART_BYTES = 24 * 1024 * 1024;
export const MAX_EXE_BYTES = 150 * 1024 * 1024;
export const MAX_PARTS = Math.ceil(MAX_EXE_BYTES / MAX_PART_BYTES);
const MIN_EXE_BYTES = 1_000_000;
const MAX_NOTES = 4000;
const MAX_ADMIN_JSON = 16 * 1024;

function reply(body, status = 200, cacheControl = "no-store", extra = {}) {
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

const fail = (status, error, detail) => reply(detail ? { error, detail } : { error }, status);

// Three numeric parts, no leading zeros, no pre-release tags. The app compares only these.
export function parseVersion(text) {
  if (typeof text !== "string") return null;
  return /^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$/.test(text) ? text : null;
}

export const parseChannel = (text) => (typeof text === "string" && Object.hasOwn(CHANNELS, text) ? text : null);

export function parseSha256(text) {
  if (typeof text !== "string") return null;
  const t = text.trim().toLowerCase();
  return /^[0-9a-f]{64}$/.test(t) ? t : null;
}

function parsePartIndex(text) {
  if (typeof text !== "string" || !/^(0|[1-9][0-9]{0,2})$/.test(text)) return null;
  const n = Number(text);
  return n < MAX_PARTS ? n : null;
}

const fileKey = (channel, version) => `${channel}/${version}/${CHANNELS[channel]}`;
const partKey = (channel, version, n) => `${fileKey(channel, version)}/part/${n}`;
const metaKey = (channel, version) => `${fileKey(channel, version)}/meta`;
const latestKey = (channel) => `${channel}/latest.json`;

// ---------- auth ----------

async function digest(text) {
  return new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text)));
}

// Both sides are hashed first, so the compare always runs on 32 bytes.
async function sameSecret(given, expected) {
  const a = await digest(given);
  const b = await digest(expected);
  if (typeof crypto.subtle.timingSafeEqual === "function") return crypto.subtle.timingSafeEqual(a, b);
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a[i] ^ b[i];
  return diff === 0;
}

async function authorized(request, env) {
  const key = env && typeof env.PUBLISH_KEY === "string" ? env.PUBLISH_KEY.trim() : "";
  if (key.length < 32) return false; // no key, or a key too short to trust, opens nothing
  const header = request.headers.get("Authorization") || "";
  const m = /^Bearer ([\x21-\x7e]{1,512})$/.exec(header);
  if (!m) return false;
  return sameSecret(m[1], key);
}

const unauthorized = () => reply({ error: "unauthorized" }, 401, "no-store", { "WWW-Authenticate": "Bearer" });

function store(env) {
  const kv = env && env.RELEASES_KV;
  const ok = kv && ["get", "getWithMetadata", "put", "list", "delete"].every((m) => typeof kv[m] === "function");
  return ok ? kv : null;
}

const noStorage = () => fail(503, "storage_unavailable");

// The key names stored under one file. list() is used for the "refuse to overwrite" checks
// because a get() of a missing key can cache "not found" for up to a minute at that location.
// That cached miss would then hide the part from finalize, or the meta from promote.
// list() itself can lag a fresh write, so these checks guard against mistakes, not races.
async function storedNames(kv, prefix) {
  const names = new Map();
  let cursor;
  do {
    const page = await kv.list({ prefix, cursor });
    for (const k of page.keys) names.set(k.name, k.metadata ?? null);
    cursor = page.list_complete ? undefined : page.cursor;
  } while (cursor);
  return names;
}

async function readMeta(kv, channel, version) {
  const m = await kv.get(metaKey(channel, version), "json");
  if (!m) return null;
  const sha256 = parseSha256(m.sha256);
  const ok = sha256 && Number.isSafeInteger(m.size) && m.size > 0 && Number.isSafeInteger(m.parts) &&
    m.parts >= 1 && m.parts <= MAX_PARTS && Array.isArray(m.partSizes) && m.partSizes.length === m.parts;
  if (!ok) return "bad";
  // A meta written before per-part hashes existed has no partSha256s. It stays valid.
  const partSha256s = Array.isArray(m.partSha256s) && m.partSha256s.length === m.parts ? m.partSha256s : null;
  return { size: m.size, sha256, parts: m.parts, partSizes: m.partSizes, partSha256s };
}

// Thrown only by the Node fallback below, so a test can tell a length error from a KV error.
class LengthMismatch extends Error {}

// A native stream that sets Content-Length and errors when the byte count differs.
// workerd has FixedLengthStream. The Node tests do not, so they get a counting fallback.
// Over HTTP a live client cannot send a body that differs from its Content-Length: a short
// body means the client dropped the connection, and then nobody reads the reply.
function fixedLength(length) {
  if (typeof FixedLengthStream === "function") return new FixedLengthStream(length);
  let seen = 0;
  return new TransformStream({
    transform(chunk, controller) {
      seen += chunk.byteLength;
      if (seen > length) throw new LengthMismatch("stream longer than its length");
      controller.enqueue(chunk);
    },
    flush() {
      if (seen !== length) throw new LengthMismatch("stream shorter than its length");
    },
  });
}

// The real reason of a KV error, short, for the admin caller. It holds no secret.
const reason = (err) => String((err && err.message) || err || "unknown error").slice(0, 300);

async function readSmallJson(request) {
  const lengthText = request.headers.get("Content-Length");
  if (lengthText && Number(lengthText) > MAX_ADMIN_JSON) return { error: fail(413, "too_large") };
  try {
    const text = await request.text();
    if (text.length > MAX_ADMIN_JSON) return { error: fail(413, "too_large") };
    return { value: JSON.parse(text) };
  } catch {
    return { error: fail(400, "bad_request", "body must be JSON") };
  }
}

// ---------- public ----------

export async function handleLatest(request, env, url) {
  for (const k of url.searchParams.keys()) {
    if (k !== "channel") return fail(400, "bad_request", `unknown parameter: ${k}`);
  }
  const channel = url.searchParams.has("channel") ? parseChannel(url.searchParams.get("channel")) : "stable";
  if (!channel) return fail(400, "bad_request", "channel must be stable or devtest");
  const kv = store(env);
  if (!kv) return noStorage();
  const text = await kv.get(latestKey(channel), "text");
  if (text === null) return reply({ error: "no_release" }, 404, "no-cache");
  let m;
  try {
    m = JSON.parse(text);
  } catch {
    return fail(500, "bad_manifest");
  }
  const version = parseVersion(m && m.version);
  const sha256 = parseSha256(m && m.sha256);
  if (!version || !sha256 || !Number.isSafeInteger(m.size) || m.size <= 0) return fail(500, "bad_manifest");
  return reply({
    version,
    sha256,
    size: m.size,
    notes: typeof m.notes === "string" ? m.notes : "",
    publishedAt: typeof m.publishedAt === "string" ? m.publishedAt : null,
    url: `${url.origin}/v1/update/download/${channel}/${version}`,
  }, 200, "no-cache");
}

export async function handleDownload(request, env, parts, ctx) {
  // parts = ["<channel>", "<version>"]
  if (parts.length !== 2) return fail(404, "not_found");
  const channel = parseChannel(parts[0]);
  const version = parseVersion(parts[1]);
  if (!channel || !version) return fail(404, "not_found");
  const kv = store(env);
  if (!kv) return noStorage();
  const meta = await readMeta(kv, channel, version);
  if (!meta) return fail(404, "not_found");
  if (meta === "bad") return fail(500, "bad_meta");

  // Get part 0 before the headers go out, so a missing part is a clean 404, not a cut stream.
  const first = await kv.get(partKey(channel, version, 0), "stream");
  if (!first) return fail(404, "not_found");

  const { readable, writable } = fixedLength(meta.size);
  // Each part is piped by the runtime into one writable. No JS runs per chunk.
  const pump = (async () => {
    try {
      await first.pipeTo(writable, { preventClose: true });
      for (let n = 1; n < meta.parts; n++) {
        const next = await kv.get(partKey(channel, version, n), "stream");
        if (!next) throw new Error(`part ${n} is missing`);
        await next.pipeTo(writable, { preventClose: true });
      }
      await writable.close();
    } catch (err) {
      // The client sees a short body; the app then rejects it on size or SHA-256.
      try { await writable.abort(err); } catch { /* already errored */ }
    }
  })();
  if (ctx && typeof ctx.waitUntil === "function") ctx.waitUntil(pump);

  return new Response(readable, {
    status: 200,
    headers: {
      "Content-Type": "application/octet-stream",
      "Content-Length": String(meta.size),
      "Content-Disposition": `attachment; filename="${CHANNELS[channel]}"`,
      "Cache-Control": "public, max-age=31536000, immutable",
      ETag: `"${meta.sha256}"`,
      "X-Content-Type-Options": "nosniff",
    },
  });
}

// ---------- admin ----------

// parts = ["<channel>", "<version>", "<file>"] and, for an upload, then "part", "<n>".
function parseFileTarget(parts, extra) {
  if (parts.length !== 3 + extra) return { error: fail(404, "not_found") };
  const channel = parseChannel(parts[0]);
  const version = parseVersion(parts[1]);
  if (!channel || !version) return { error: fail(400, "bad_request", "bad channel or version") };
  if (parts[2] !== CHANNELS[channel]) return { error: fail(400, "bad_request", `file must be ${CHANNELS[channel]}`) };
  return { channel, version };
}

export async function handleUploadPart(request, env, parts) {
  if (!(await authorized(request, env))) return unauthorized();
  const t = parseFileTarget(parts, 2);
  if (t.error) return t.error;
  if (parts[3] !== "part") return fail(404, "not_found");
  const n = parsePartIndex(parts[4]);
  if (n === null) return fail(400, "bad_request", `part must be 0 to ${MAX_PARTS - 1}`);
  const lengthText = request.headers.get("Content-Length");
  if (!lengthText || !/^[0-9]{1,10}$/.test(lengthText)) return fail(411, "length_required");
  const length = Number(lengthText);
  if (length > MAX_PART_BYTES) return fail(413, "too_large", `a part holds at most ${MAX_PART_BYTES} bytes`);
  if (length < 1 || !request.body) return fail(400, "bad_request", "the part is empty");
  // The uploader's SHA-256 of this part. The Worker records it; it does not hash the body.
  const sha256 = parseSha256(request.headers.get("X-Part-Sha256"));
  if (!sha256) return fail(400, "bad_request", "X-Part-Sha256 must be the SHA-256 of the part (64 hex)");
  const kv = store(env);
  if (!kv) return noStorage();

  const key = partKey(t.channel, t.version, n);
  const names = await storedNames(kv, fileKey(t.channel, t.version) + "/");
  if (names.has(metaKey(t.channel, t.version))) {
    return fail(409, "version_exists", "this version is finalized; publish a new version");
  }
  if (names.has(key)) {
    // The uploader compares this with its own part. null means the part has no recorded hash.
    const m = names.get(key);
    return reply({
      error: "part_exists",
      detail: "this part is already stored",
      size: m && Number.isSafeInteger(m.size) ? m.size : null,
      sha256: (m && parseSha256(m.sha256)) || null,
    }, 409);
  }

  try {
    // The fixed-length stream errors when the body is not exactly Content-Length bytes,
    // and then KV stores nothing.
    await kv.put(key, request.body.pipeThrough(fixedLength(length)), { metadata: { size: length, sha256 } });
  } catch (err) {
    if (err instanceof LengthMismatch) return fail(400, "bad_request", "the body did not match Content-Length");
    // A KV refusal, for example the 1 GB storage or the daily write limit of the Free plan.
    // 507 tells the uploader not to send the same bytes again.
    return fail(507, "storage_write_failed", reason(err));
  }
  return reply({ ok: true, key, part: n, size: length, sha256 }, 201);
}

export async function handleFinalize(request, env, parts) {
  if (!(await authorized(request, env))) return unauthorized();
  const t = parseFileTarget(parts, 0);
  if (t.error) return t.error;
  const body = await readSmallJson(request);
  if (body.error) return body.error;
  const input = body.value;
  const sha256 = parseSha256(input && input.sha256);
  const size = input && input.size;
  const count = input && input.parts;
  if (!sha256 || !Number.isSafeInteger(size) || !Number.isSafeInteger(count)) {
    return fail(400, "bad_request", "size, sha256, parts and partSha256s are required");
  }
  if (size < MIN_EXE_BYTES) return fail(400, "bad_request", "the exe is too small");
  if (size > MAX_EXE_BYTES) return fail(413, "too_large", `at most ${MAX_EXE_BYTES} bytes`);
  if (count < 1 || count > MAX_PARTS) return fail(400, "bad_request", `parts must be 1 to ${MAX_PARTS}`);
  const wanted = Array.isArray(input.partSha256s) ? input.partSha256s.map(parseSha256) : [];
  if (wanted.length !== count || wanted.some((h) => !h)) {
    return fail(400, "bad_request", "partSha256s must hold one SHA-256 for each part");
  }
  const kv = store(env);
  if (!kv) return noStorage();

  const names = await storedNames(kv, fileKey(t.channel, t.version) + "/");
  if (names.has(metaKey(t.channel, t.version))) return fail(409, "version_exists", "this version is already finalized");
  const partSizes = [];
  for (let n = 0; n < count; n++) {
    // list() can lag a fresh write by up to a minute, so each part is read by key.
    // Only its metadata is used; the body stream is cancelled at once.
    const got = await kv.getWithMetadata(partKey(t.channel, t.version, n), "stream");
    if (!got || got.value === null) return fail(409, "missing_part", `part ${n} is not stored`);
    try { await got.value.cancel(); } catch { /* nothing to free */ }
    const s = got.metadata && got.metadata.size;
    if (!Number.isSafeInteger(s) || s < 1 || s > MAX_PART_BYTES) return fail(409, "bad_part", `part ${n} has no valid size`);
    // A part from another build has another hash. A part with no hash is refused too.
    const h = parseSha256(got.metadata.sha256);
    if (h !== wanted[n]) {
      return reply({ error: "part_mismatch", detail: `part ${n} is not the part of this exe; delete the version or publish a new one`, part: n, sha256: h }, 409);
    }
    partSizes.push(s);
  }
  const total = partSizes.reduce((a, b) => a + b, 0);
  if (total !== size) return fail(409, "size_mismatch", `the parts hold ${total} bytes`);

  const meta = { size, sha256, parts: count, partSizes, partSha256s: wanted, finalizedAt: new Date().toISOString() };
  try {
    await kv.put(metaKey(t.channel, t.version), JSON.stringify(meta));
  } catch (err) {
    return fail(507, "storage_write_failed", reason(err));
  }
  return reply({ ok: true, key: metaKey(t.channel, t.version), ...meta }, 201);
}

export async function handlePromote(request, env, parts) {
  if (!(await authorized(request, env))) return unauthorized();
  if (parts.length !== 1) return fail(404, "not_found");
  const channel = parseChannel(parts[0]);
  if (!channel) return fail(400, "bad_request", "channel must be stable or devtest");
  const body = await readSmallJson(request);
  if (body.error) return body.error;
  const input = body.value;
  const version = parseVersion(input && input.version);
  const sha256 = parseSha256(input && input.sha256);
  const size = input && input.size;
  if (!version || !sha256 || !Number.isSafeInteger(size) || size <= 0) {
    return fail(400, "bad_request", "version, sha256 and size are required");
  }
  const notes = input.notes === undefined || input.notes === null ? "" : input.notes;
  if (typeof notes !== "string") return fail(400, "bad_request", "notes must be text");
  const kv = store(env);
  if (!kv) return noStorage();

  const meta = await readMeta(kv, channel, version);
  if (!meta) return fail(404, "not_uploaded", "upload and finalize the exe first");
  if (meta === "bad") return fail(500, "bad_meta");
  if (meta.size !== size) return fail(409, "size_mismatch", `stored size is ${meta.size}`);
  if (meta.sha256 !== sha256) return fail(409, "sha256_mismatch", "sha256 does not match the stored exe");

  const manifest = {
    version,
    sha256,
    size,
    notes: notes.replace(/\r\n/g, "\n").trim().slice(0, MAX_NOTES),
    publishedAt: new Date().toISOString(),
  };
  try {
    await kv.put(latestKey(channel), JSON.stringify(manifest));
  } catch (err) {
    return fail(507, "storage_write_failed", reason(err));
  }
  return reply({ ok: true, channel, ...manifest });
}

// The version that <channel>/latest.json points to, or null when there is none.
async function latestVersion(kv, channel) {
  const text = await kv.get(latestKey(channel), "text");
  if (text === null) return null;
  try {
    return parseVersion(JSON.parse(text).version) || "bad";
  } catch {
    return "bad";
  }
}

// GET /v1/admin/versions/<channel>: every stored version, so the publish script can delete
// old ones. list() can lag a fresh write by up to a minute, so this is a guide, not a lock.
export async function handleVersions(request, env, parts) {
  if (!(await authorized(request, env))) return unauthorized();
  if (parts.length !== 1) return fail(404, "not_found");
  const channel = parseChannel(parts[0]);
  if (!channel) return fail(400, "bad_request", "channel must be stable or devtest");
  const kv = store(env);
  if (!kv) return noStorage();
  const found = new Map();
  for (const name of (await storedNames(kv, `${channel}/`)).keys()) {
    const m = /^[^/]+\/([^/]+)\/[^/]+\/(meta|part\/[0-9]+)$/.exec(name);
    const version = m && parseVersion(m[1]);
    if (!version) continue;
    const v = found.get(version) || { version, finalized: false, parts: 0 };
    if (m[2] === "meta") v.finalized = true;
    else v.parts++;
    found.set(version, v);
  }
  const latest = await latestVersion(kv, channel);
  return reply({ channel, latest, versions: [...found.values()] });
}

// DELETE /v1/admin/release/<channel>/<version>: removes the meta, then every part.
// It refuses the version that latest.json points to, so installs never lose their download.
// A version with no meta (a part-way or mixed upload) can be deleted too, and then uploaded again.
export async function handleDeleteRelease(request, env, parts) {
  if (!(await authorized(request, env))) return unauthorized();
  if (parts.length !== 2) return fail(404, "not_found");
  const channel = parseChannel(parts[0]);
  const version = parseVersion(parts[1]);
  if (!channel || !version) return fail(400, "bad_request", "bad channel or version");
  const kv = store(env);
  if (!kv) return noStorage();
  const latest = await latestVersion(kv, channel);
  if (latest === "bad") return fail(500, "bad_manifest");
  if (latest === version) return fail(409, "is_latest", "promote another version before this one is deleted");
  try {
    // Meta first: from then on the version is not downloadable or promotable.
    // Parts are deleted by key, not from list(), because list() can miss a fresh part.
    await kv.delete(metaKey(channel, version));
    for (let n = 0; n < MAX_PARTS; n++) await kv.delete(partKey(channel, version, n));
  } catch (err) {
    return fail(503, "storage_delete_failed", reason(err));
  }
  return reply({ ok: true, channel, version });
}
