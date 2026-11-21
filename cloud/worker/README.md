# gecko-peeks-api (Cloudflare Worker)

This Worker gives Gecko Peeks v2 a small JSON API with live Cloudflare Radar data.
It runs at `https://gecko-peeks-api.gecko-apps.workers.dev`.

It is not a proxy. Each Radar path is fixed in `src/index.js`. Each input is validated.

## Endpoints

The data endpoints below accept GET only. The update channel (`/v1/update/*`, `/v1/admin/*`) is described in [Update channel](#update-channel). A wrong method gets `405`. Unknown paths get `404 {"error":"not_found"}`.
Bad parameters get `400 {"error":"bad_request","detail":"..."}`.

| Path | What it returns | Cache |
|---|---|---|
| `/v1/health` | `{ ok, version, radar, refdata, time }`. `radar` is `false` when the `RADAR_TOKEN` secret is missing. `refdata` is the refdata version. | none |
| `/v1/edge` | Facts from `request.cf`: ASN, org, colo, city, region, country, continent, lat, lon, timezone, HTTP protocol, TLS version. It never returns the client IP. | none |
| `/v1/pulse?isp=<asn>&country=<CC>` | Riot (AS6507) prefix, traffic, outages, anomalies, hijacks and leaks; the same for your ISP, plus `isp.upstreams`; country outages and IQI. Both params are optional and default to `request.cf`. A param must equal the caller's own `request.cf` value, else `400`. | edge 300 s; the Riot block has its own 300 s key; upstreams 1 day per ISP |
| `/v1/asn?ips=a,b,c` | `{ results:[{ ip, asn, name, country }] }`. At most 15 IPs. | per IP 1 day |
| `/v1/refdata` | The reference data file `data/refdata.json` (regions, country map, providers, city codes). No parameters. Sends `ETag: "rd-<version>"`; `If-None-Match` with that tag gives `304`. | 1 day |
| `/v1/riot-status?region=<id>` | Riot's public status for one League region and its VALORANT shard (see below). | 60 s per region |

### `/v1/refdata`

- The file is `data/refdata.json`. It is the only copy. The app embeds the same file.
- Bump `version` (`YYYY.MM.DD-N`) on every content change. The ETag follows it.
- Any query parameter gives `400`. No rate limit, no subrequests.

### `/v1/riot-status`

- `region` is a refdata region id or alias (`kr1` is `kr`, `ru1` is `ru`, `ph2` and `th2` are `sg2`). An unknown id is `400 {"error":"bad_request","detail":"unknown region"}`.
- The Worker reads `https://lol.secure.dyn.riotcdn.net/channels/public/x/status/<lolStatusId>.json` and
  `https://valorant.secure.dyn.riotcdn.net/channels/public/x/status/<valorantShard>.json` in parallel (5 s timeout, 256 KB cap each).
- Answer: `{ region, lolStatusId, valorantShard, fetchedAt, lol, valorant }`. `lol` and `valorant` are
  `{ id, name, incidents:[Event], maintenances:[Event] }`, or `null` when that feed failed.
- Event: `{ id, type, severity, status, title, text, platforms, createdAt, updatedAt, archiveAt }`.
  Completed maintenances and events whose `archiveAt` is past are dropped. The newest 10 per list are kept.
  Text is `en_US` when the feed has it, else the first locale.
- When both feeds fail the answer is `no-store` and is not cached. One call makes at most 4 subrequests.
- It uses the `RATE_LIMITER` binding.

### `isp.upstreams` in `/v1/pulse`

- From Radar `/radar/entities/asns/<isp>/rel`. Radar rows have `asn1`, `asn2`, their names, and `rel`
  (`provider-customer`, where `asn1` is the provider of `asn2`, or `peer`). The asked ASN can be on either side.
- Output: `[{ asn, name, rel: "provider"|"peer" }]`. All providers (at most 12), then peers that are a refdata
  upstream or backbone provider (at most 8). Each group is in ascending ASN order.
- `null` when the Radar call failed, `[]` when Radar lists no rows. Skipped for AS6507 and without a token.
- Credit: Cloudflare Radar (CC BY-NC 4.0).

Rules:

- ASN must be an integer from 1 to 4294967295 (`AS852` is also accepted).
- Country must be 2 letters A-Z.
- `/v1/asn` drops private, loopback, link-local, CGNAT, multicast, documentation and reserved IPs. They do not appear in `results`. Match rows by `ip`, not by index.
- A text that is not an IP makes the whole `/v1/asn` request a `400`.
- Each Radar call has a 6 s timeout. A failed call becomes `null` in its field. The response is still `200`.
- A pulse where every Radar call failed is not cached.
- The header `X-Gecko-Cache` shows `HIT`, `MISS` or `BYPASS` for `/v1/pulse`.
- The cache is per data center (`caches.default`). A call that lands on a new data center is a `MISS`.
- A free Worker can make 50 subrequests per request. Cache calls count too. `/v1/asn` keeps to 45.
  With the cache, an IP costs 3 on a miss and 1 on a hit. 15 IPs fit, so every IP uses the cache.
- The `RATE_LIMITER` binding (`wrangler.toml`) allows 30 calls per minute per caller IP on `/v1/pulse`, `/v1/asn` and `/v1/riot-status`. The `DOWNLOAD_LIMITER` binding allows 20 calls per minute per caller IP on `/v1/update/download`.
  Over the limit the answer is `429 {"error":"rate_limited"}`. Without the binding every call is allowed.
- No response sends `Access-Control-Allow-Origin`. Only the desktop app calls this Worker, and it does not need CORS.
  Other websites cannot read the answers from a visitor's browser.
- A cold `/v1/pulse` makes up to 17 Radar calls (16 plus the upstreams call) and stays at or under 30 subrequests. Do not poll it faster than every 300 s.
- `name` is the Radar AS name. When Radar names a network only `ASN852`, the Worker uses the longer `aka` name.

Event shape: `{ kind:"outage"|"anomaly"|"hijack"|"leak", start, end, title, detail, url }`.
`end` is `null` while an event is still open. `url` points to a `https://radar.cloudflare.com/` page.

## Update channel

The app reads releases from Workers KV through these routes. The KV namespace binding is `RELEASES_KV`.

- `GET /v1/update/latest?channel=stable` returns `{version, sha256, size, notes, publishedAt, url}`, or 404 `{"error":"no_release"}`. It is sent with `Cache-Control: no-cache`.
- `GET /v1/update/download/<channel>/<version>` streams the exe as an attachment (`GeckoPeeks.exe`). It is cached for a year. `DOWNLOAD_LIMITER` allows 20 calls per minute per caller IP.
- Channels: `stable` holds `GeckoPeeks.exe`. `devtest` holds `GeckoPeeksDev.exe` and is only for tests with a Dev build.
- One KV value holds at most 25 MiB. So the exe is stored in parts of at most 24 MiB: `<channel>/<version>/<file>/part/<n>`. `<channel>/<version>/<file>/meta` holds `{size, sha256, parts, partSizes, partSha256s, finalizedAt}`. `<channel>/latest.json` is the promoted release.
- The Worker refuses an exe over 150 MiB.

Admin routes need `Authorization: Bearer <PUBLISH_KEY>`. Without it they return 401.

- `PUT /v1/admin/upload/<channel>/<version>/<file>/part/<n>` stores one part. The header `X-Part-Sha256` must hold the SHA-256 of the part. A stored part gets 409 `part_exists`.
- `POST /v1/admin/finalize/<channel>/<version>/<file>` checks that every part is stored, that the sizes add up and that the recorded part hashes match. Then it writes the meta.
- `POST /v1/admin/promote/<channel>` checks the stored size and SHA-256, then moves `<channel>/latest.json`.
- `GET /v1/admin/versions/<channel>` lists the stored versions.
- `DELETE /v1/admin/release/<channel>/<version>` deletes one version. It refuses the latest one (409 `is_latest`).

The Free plan gives 10 ms of CPU per request, so the Worker does not hash the exe. It records the SHA-256 that the publish script sends. The app checks the SHA-256 of the whole file. `cloud/publish-release.ps1` does the upload, finalize and promote.

## Secret

The Worker reads the Radar API token from the secret `RADAR_TOKEN`.
Use a Cloudflare API token with only the "Radar Read" permission.
The token is never stored in this repo or in the app.

For `wrangler dev`, put the local token only in `cloud/worker/.dev.vars`. `.gitignore` keeps that file,
`.wrangler/` and `node_modules/` out of git. Never put the token in any other file.

To set or replace it:

```powershell
npx wrangler secret put RADAR_TOKEN
```

The update channel also needs the secret `PUBLISH_KEY` (32 characters or more). Only `/v1/admin/*` uses it. Type it at the prompt. Never put it in a file in this repo:

```powershell
npx wrangler secret put PUBLISH_KEY
```

## Test

```powershell
npm test
```

`npm test` runs `node --test test/index.test.mjs test/update.test.mjs test/v21.test.mjs`.

The tests use no network. A fake `fetch` stands in for the Radar API.

## Redeploy

Run these from `cloud/worker`:

```powershell
npx wrangler login
```

```powershell
npx wrangler deploy
```

`wrangler deploy` keeps the existing `RADAR_TOKEN` secret.

If you deploy with the raw API (`PUT /accounts/{id}/workers/scripts/gecko-peeks-api` as multipart), put these in the metadata:

1. `"keep_bindings": ["secret_text"]`. Without it, the upload removes the secrets and `/v1/health` shows `radar:false`.
2. The `ratelimit` bindings `RATE_LIMITER` (namespace `1001`, 30 per 60 s) and `DOWNLOAD_LIMITER` (namespace `1002`, 20 per 60 s).
3. The KV binding `RELEASES_KV`.

After a deploy, check that `/v1/health` shows `radar:true`.
