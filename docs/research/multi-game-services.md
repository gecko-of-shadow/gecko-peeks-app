# Gecko Peeks: other games and services (research for the release after v2.1.4)

Date of research: 2026-11-21. This is research only. It changes no code.

## 0. Summary

**Product goal (from the project owner):** "provide insight about a service you are using or might be having network issues with, and generate diagnostic data that could prove useful to helping fix problems."

**Main findings:**

1. **The last hop means different things for different services.** For Riot and Valve, the last hop is inside the service's own network. For most other services, the last hop is a CDN or a cloud edge. The app must state, for each service, which class the target is in (section 2). Without that label, a good result can look like "the service is fine", and that can be an incorrect fact.
2. **Valve (Steam, CS2, Dota 2) is the best next target.** A keyless Valve API lists every Steam Datagram Relay (SDR) point of presence (POP) with relay IPs and coordinates. The relays are in Valve's AS32590, and they answered ICMP echo in our test.
3. **Discord voice runs mostly on Cloudflare.** A trace shows Cloudflare (AS13335), not Discord. Discord has a keyless Statuspage feed. Discord also has an official local RPC event that gives the voice server hostname and Discord's own ping, but only for an app that Discord approves.
4. **GTA Online, Apex Legends and Call of Duty have no stable public game-server target.** For these games, the honest option is "status only", or a trace to the publisher's login services with a clear label.
5. **For consoles, a PC can measure only the shared path.** That is the home router, the ISP and the platform's front-door edge. It cannot measure the console's NAT type, its Wi-Fi or its in-game ping. Keyless status JSON exists for PSN, Xbox and Nintendo, but none of the three is documented.
6. **Web, social and streaming services sit behind CDNs.** Examples: Twitch web on Fastly, X and Kick on Cloudflare, TikTok on Akamai. Measure them as websites, with HTTPS edge timing, and say what was not measured. Twitch has a keyless ingest list, so a streamer's upload path can be traced (section 6).
7. **Support teams want traces taken during the problem, to a known target, plus game logs.** Riot publishes a fixed tracert IP for each region. For NA it is `192.207.0.1`. The app uses `192.207.0.5`. The v2.1.5 export can match what each support team asks for (section 8).
8. **The data model can grow without breaking old apps.** Today's loader reads only the keys it knows, so a new optional `services` block in `refdata.json` keeps schema 1 (section 9).

**Recommended order (section 10):**
- **Phase 1:**
  - Valve SDR: Steam, CS2 and Dota 2.
  - Discord.
  - The status of the three console networks.
- **Phase 2:**
  - Fortnite and Roblox.
  - Status-only cards for GTA Online, Apex Legends and Call of Duty.
  - Twitch and YouTube Live ingest, with a new HTTPS edge probe.
  - CDN and cloud status.
  - GeForce NOW and Xbox Cloud Gaming: status and front door.
- **Phase 3:**
  - Discord RPC.
  - User-entered targets.
  - The other web services.
  - Cloud-gaming stream servers.

---

## 1. How to read this report

**Confidence labels:**
- **high:** we fetched or tested the primary source in this session.
- **medium:** an official source says it, but we saw it only in a search summary, or the claim comes from one sample of an undocumented endpoint.
- **low:** a community source, or an inference.
- **not verified:** we found no source. Do not build on it, and do not show it in the app as a fact.

**Live tests:**
- All live tests ran from one Windows 11 PC on one home connection, on 2026-11-21. Results from other networks can differ.
- For ICMP, this report says only "answered" or "did not answer". It gives no round-trip times, no edge IPs that depend on the resolver, and no hops from the home network. This keeps personal data out of a public repo.
- DNS results for CDN names depend on the resolver location. CDN edge IPs are examples only.

**ASN lookups:** each ASN in this report can be checked again with one of these calls:
- RIPEstat prefix overview: `https://stat.ripe.net/data/prefix-overview/data.json?resource=<ip>`
- RIPEstat AS overview: `https://stat.ripe.net/data/as-overview/data.json?resource=AS<n>`
- PeeringDB: `https://www.peeringdb.com/api/net?asn=<n>`

**Claims with no link:** a claim that says "RIPEstat" uses the query URLs above, with the IP or ASN that the claim names. A claim that says "measured", "file seen" or "answered" comes from a live test in this session. The test can be repeated with `Resolve-DnsName`, `ping` or `tracert`. A claim that names another section has its source in that section.

**App files read for this report:**
- `README.md`
- `GeckoPeeks/Routing/RefData.cs` and `GeckoPeeks/Routing/RouteClassifier.cs`
- `cloud/worker/data/refdata.json`
- `GeckoPeeks/App/LeagueMatch.cs`, `GeckoPeeks/App/LeagueProcess.cs` and `GeckoPeeks/App/ProbeScheduler.cs`
- `GeckoPeeks/Net/TestRunner.cs`
- `cloud/worker/src/riot.js` and `cloud/worker/src/index.js`

---

## 2. The model: what the last hop means

This classification is the base for the data model, the UI label and the ranking.

| Target class | What a good trace proves | What it does not prove | Services |
|---|---|---|---|
| **Owned network** | The path to the service's own network is good, up to a server of that service. | The path to the actual match server. | Riot (AS6507), Valve SDR relays (AS32590), Roblox edge (AS22697), Xbox auth hosts (AS8075) |
| **Cloud region proxy** | The path to the cloud region that hosts the game is good. | That the match server is in that region, or that it is healthy. | Fortnite region ping hosts (AWS) |
| **CDN or website edge** | The path to the nearest CDN edge is good, and the edge answers. | Anything about the service behind the CDN. | Discord (Cloudflare), PSN (Akamai), Nintendo (Akamai), Steam store and web (Akamai), most web and social services |
| **Status only** | Nothing about the path. The app shows only the vendor's own status feed. | Any network fact. | GTA Online, Apex Legends, Call of Duty |
| **User-entered** | The path to the address that the user typed. | That the address is the game server. The app shows it as "your address". | Minecraft servers, an Overwatch IP from the in-game Network Graph |

**UI rule:** each service card names its class in plain words. Examples:
- "Route to Valve's nearest relay."
- "Route to Cloudflare's edge for Discord. This is not Discord's voice server."
- "No public game-server address. Status only."

---

## 3. Riot (the baseline that the app has today)

| Item | Finding | Source | Conf. |
|---|---|---|---|
| Riot's published tracert targets | Riot asks users to run `tracert` to one fixed IP for each region, for example NA `192.207.0.1` and EU `162.249.72.1`. The output is saved as `Tracert.txt` and attached to a ticket. | https://support.riotgames.com/valorant/connectivity/how-to-use-tracert-to-obtain-network-logs | high (fetched; both IPs seen on the page) |
| App target for NA | `192.207.0.5`, in the Riot Direct anycast prefix `192.207.0.0/24` (AS6507). | `cloud/worker/data/refdata.json` region `na1`; `GeckoPeeks/Net/TestRunner.cs:25` | high |
| What Riot asks for in a League ticket | Three logs: Network Analysis, Trace Route and Process List. | https://support.riotgames.com/league-of-legends/connectivity/basic-connections-troubleshooting-guide | high |
| League per-match netlog | It is in `Logs\GameLogs\<timestamped folder>`. Riot's Log Reader turns it into a 0 to 100 connection score. | https://support.riotgames.com/en-us/league-of-legends/connectivity/connection-troubleshooting-log-reader | high |
| NetworkInfo shows your IP | Riot warns that the NetworkInfo file shows your IP, so do not post it in public. | https://support.riotgames.com/en-us/riot/performance/network-system-and-logs-windows | high |

**Finding:** the app and Riot support use different addresses in the same /24. That is not an error. But a support agent knows `192.207.0.1`.
- **Option:** the export can include one extra trace to the IP that Riot publishes for the user's region.
- **Label:** "the address Riot support asks for".
- **Not verified:** whether both addresses take the same path on every network.

---

## 4. PC games and apps

### 4.1 Steam, CS2, Dota 2 (Valve, Steam Datagram Relay)

**Trace target (high):**
- Valve publishes a keyless relay list:
  - CS2: `https://api.steampowered.com/ISteamApps/GetSDRConfig/v1/?appid=730`
  - Dota 2: `https://api.steampowered.com/ISteamApps/GetSDRConfig/v1/?appid=570`
- `GetSDRConfig` is in Valve's keyless API list: `https://api.steampowered.com/ISteamWebAPIUtil/GetSupportedAPIList/v1/`. Both responses returned HTTP 200 without a key in this session.
- JSON shape: `pops.<code>` contains these fields:
  - `desc`, for example "Amsterdam (Netherlands)".
  - `geo`: `[longitude, latitude]`. The longitude comes first.
  - `tier` and `partners`. Their meaning is not documented.
  - `relays`: a list of `{ ipv4, port_range }`.
- The response also has `typical_pings` (POP-to-POP ms) and a `revision`.
- On this date the CS2 list had 53 POPs, and 45 of them had relays. Dota 2 had 70 POPs. POPs without a relay list include the Alibaba, Tencent, Hetzner and Datapacket partner sites.
- The relay IPs that we checked are in AS32590 "VALVE-CORPORATION". Relay reverse DNS uses the form `a-b-c-d.valve.net`, and some relays have no PTR record. Check one with: `https://stat.ripe.net/data/prefix-overview/data.json?resource=162.254.199.170`.
- The China POPs list relays in carrier address space. Do not assume that every relay is in AS32590 (high for the POP descriptions; prefix holders not verified).
- A trace to a relay ended on a `*.valve.net` hop (one vantage point). Check the PTR pattern with a reverse lookup of a relay IP, for example `162.254.199.170`.

**ICMP (high, one vantage point):** the four SDR relays that we tested answered echo. Steam connection-manager servers did not answer. Valve does not document ICMP on relays, so this can change.

**Last hop vs game ping:**
- With SDR, game traffic goes to a Valve relay, then through Valve's network to the server. Valve's partner docs say that with relaying, "IP addresses are never revealed". Fake IPs "can't be pinged". Source: https://partner.steamgames.com/doc/features/multiplayer/steamdatagramrelay (high).
- SDR measures its own ping to relays. GameNetworkingSockets says that ping times to Valve relays build a location "marker" (https://github.com/ValveSoftware/GameNetworkingSockets/blob/master/include/steam/isteamnetworkingutils.h, high). The open headers do not say that this ping uses UDP rather than ICMP (inference: medium).
- **Honest label:** "Route to your nearest Valve relay (ICMP). In-game ping goes through Valve's relay network, so it can be higher or lower."

**Steam store and web:**
- `store.steampowered.com`, `api.steampowered.com` and `steamcommunity.com` resolved to Akamai (AS16625 or AS20940) (high, RIPEstat).
- A trace to them ends at a CDN edge. It does not show the route to Valve.

**Detect the game:**
- `steam.exe` is in the Steam folder (high, file seen).
- `cs2.exe` and `dota2.exe` are not verified. The names come from memory and community sources.
- Valve's GSI wiki gives the Steam path in registry `HKCU\Software\Valve\Steam`, value `SteamPath`. It gives the library folders in `steamapps\libraryfolders.vdf` (high, archived wiki copy: https://web.archive.org/web/20260107102636/https://developer.valvesoftware.com/wiki/Counter-Strike:_Global_Offensive_Game_State_Integration).

**Local data:**
- **Game State Integration (GSI)** is Valve's own local HTTP push.
  - A cfg file in the game's `cfg` folder tells the game where to POST JSON.
  - It gives the map, round and player state.
  - It has no ping, server address or data-center field (high for CS2, from the archived wiki).
  - The wiki says that GSI "will help third parties develop their tools". That is an invitation. It is not a formal statement that GSI is safe for VAC.
  - **Use:** "in a match: yes or no", and the map. Do not use it to find the server.
- `-condebug` (writes a console log): not verified for CS2 or Dota 2. Under SDR, the log would not show a routable server address anyway (low).

**Anti-cheat (VAC, CS2 Trusted Mode):**
- VAC bans for "any third-party modifications to a game designed to give one player an advantage". Source: https://help.steampowered.com/en/faqs/view/571A-97DA-70E9-FF74 (high).
- CS2 Trusted Mode "blocks third-party files from interacting with the game". Valve bans for "any targeted process tampering". Source: https://help.steampowered.com/en/faqs/view/09A0-4879-4353-EF95 (high).
- Section 7 gives the rules.

**Status feed:**
- We found no official Valve status page (medium; absence is hard to prove).
- `ICSGOServers_730/GetGameServersStatus` returned HTTP 200 without a key once in this session. A second request returned HTTP 403 with "verify your key= parameter" (high for both results). It is not in the keyless list. **Do not use it without a key.**
- steamstat.us says that it "is an unofficial website and is not affiliated with Valve". It blocks scripts with HTTP 403. Do not use it.
- **Honest option:** no status feed for Steam. Show "Valve publishes no status feed".

### 4.2 Discord

**Trace target:**
- Discord says that "more than 80% of our voice and video traffic" runs on the Cloudflare edge. Source: https://discord.com/blog/how-we-moved-discord-voice-to-the-edge (high, fetched).
- `discord.com`, `gateway.discord.gg`, `media.discordapp.net` and `latency.discord.media` resolved into `162.159.128.0/19`, which is AS13335 Cloudflare (high, RIPEstat).
- PeeringDB has no network named Discord (high, `https://www.peeringdb.com/api/net?name__contains=Discord`).
- `https://latency.discord.media/rtc` returns keyless JSON, with no documentation. It is a short list of voice regions with 5 IPs each. The IP we checked is in `104.29.154.0/24`, AS13335 (medium; `https://stat.ripe.net/data/prefix-overview/data.json?resource=104.29.154.58`).
  - It returned only regions near this PC.
  - Not verified: whether the list depends on the caller's location.
  - Do not use the region names as facts.
- The region IPs did not send an echo reply. A Cloudflare router answered with "TTL expired in transit" instead (high). Windows `ping.exe` counts that as "Received". The app's destination ping counts only `IPStatus.Success`, so the app counts it as lost (high, `GeckoPeeks/Net/TestRunner.cs:426`).
- **Honest option:**
  - Trace to `latency.discord.media` (it answered ICMP).
  - Label it "Cloudflare edge used by Discord".
  - Never call it "your voice server".

**Local data (official):**
- The Discord RPC event `VOICE_CONNECTION_STATUS` includes the "hostname of the connected voice server" and Discord's ping values. Source: https://docs.discord.com/developers/topics/rpc (high, fetched; both strings are on the page).
- RPC access needs an app that Discord approves, plus user consent (high, same page). This makes it a phase 3 item, and it depends on Discord's approval.

**Detect the app:**
- `Discord.exe` is in `%LOCALAPPDATA%\Discord\app-<version>\` (high, file seen).
- Running Discord does not mean the user is in voice. Without RPC, the app cannot tell.

**Status feed (high):**
- Atlassian Statuspage, keyless: `https://discordstatus.com/api/v2/summary.json`.
- The components include "Voice" and voice regions (`/api/v2/components.json`).

**Support:**
- Discord asks for the voice debug log: User Settings > Voice & Video > Debugging (medium, https://support.discord.com/hc/en-us/articles/1500006052822-How-to-Report-a-Bug).
- Discord says that "RTC Connecting" and "No Route" usually mean something on the user's side, such as a firewall or a VPN (medium, https://support.discord.com/hc/en-us/articles/115001310031).

### 4.3 Fortnite (Epic)

**Trace target:**
- Region ping hosts `ping-<region>.ds.on.epicgames.com` resolve to AWS (AS16509 or AS14618). Regions seen: `nae`, `naw`, `nac`, `eu`, `oce`, `br`, `asia`, `me` (medium: live DNS; the names are not documented, and the IPs change).
- `ping-nae` answered ICMP. The other regions were not pinged.
- Epic's own AS4356 is not in the live path. The backend hosts resolve to AWS or Cloudflare (high, RIPEstat).
- **Honest option:**
  - Resolve the host before each run.
  - Label it "AWS region used for Fortnite ping".
  - The match server is a different host.
- Epic tells users to tell their ISP that the ping test uses ICMP, while gameplay uses UDP (medium, search summary of https://www.epicgames.com/help/en-US/c5719335176219/c5719372265755/a5720393283867).

**Detect:** `FortniteClient-Win64-Shipping.exe` is not verified.

**Anti-cheat:** Easy Anti-Cheat is required (medium, https://www.epicgames.com/help/c-202300000001636/a202300000010358).

**Status feed (high):** Statuspage, keyless: `https://status.epicgames.com/api/v2/summary.json`. The components include Fortnite, Matchmaking, Login and Voice Chat.

### 4.4 Roblox

**Trace target:**
- `www.roblox.com` resolved into `128.116.63.0/24`, AS22697 "ROBLOX-PRODUCTION" (high, `https://stat.ripe.net/data/prefix-overview/data.json?resource=128.116.63.3`). It answered ICMP (high).
- Community posts say that the client log has a "Connecting to UDMUX server" line with an IP and a port. Sources: https://devforum.roblox.com/t/2526452 and https://devforum.roblox.com/t/exploiters-dosing-servers-with-ip-found-in-logs/160921/12 (low).
  - The log path `%LOCALAPPDATA%\Roblox\logs` is not verified.
  - The log format is not documented.
  - The UDMUX address is a Roblox proxy. Its ICMP behavior was not tested.

**Anti-cheat:**
- Hyperion (Byfron) is on the client (medium, https://devforum.roblox.com/t/welcoming-byfron-to-roblox/2018233).
- Roblox acts against modified clients. It does not support third-party tools (medium, https://devforum.roblox.com/t/an-update-on-automated-action-against-modified-clients/3640609/1).

**Status feed (high):**
- Roblox uses status.io, not Statuspage.
- Keyless: `https://api.status.io/1.0/status/59db90dbcdeb2f04dadcf16d`. The page ID comes from the status.roblox.com HTML.

### 4.5 GTA Online (Rockstar)

**Trace target:**
- Rockstar services `prod.ros.rockstargames.com` and `socialclub.rockstargames.com` are in AS46555 "RSOE-NA - TAKE-TWO INTERACTIVE SOFTWARE" (high, RIPEstat). Neither answered ICMP (high).
- Rockstar's NAT article lists UDP 6672 and 61455 to 61458, and it explains which NAT types can play together (medium, https://support.rockstargames.com/articles/1AQFnU2Km6PPYPG6x2sY9g/how-to-resolve-errors-in-gta-online-about-strict-nat-type). This fits a peer-to-peer session model. Not verified: a Rockstar statement that sessions are peer-to-peer.
- **Honest option:**
  - Status only. Or show a route to the Rockstar online services, labeled "Rockstar login and cloud services, not a game server", with "target does not answer ping".

**Anti-cheat:** BattlEye on PC (medium, search summary of https://support.rockstargames.com/articles/1nenwhZlVrJY6CTFeSS2Fx).

**Status feed:**
- The page is `https://support.rockstargames.com/servicestatus`.
- Our fetches were reset or timed out. A keyless JSON feed is **not verified**.

### 4.6 Apex Legends (EA), Call of Duty (Activision), Blizzard games

- **Apex:**
  - EA is AS14891 (high). `accounts.ea.com` resolved to AWS and did not answer ICMP (high).
  - We found no public game-server target (not verified).
  - `status.ea.com` does not resolve. `https://help.ea.com/en/server-status/` has no keyless JSON that we could find.
  - **Status only, and no feed.**
- **Call of Duty:**
  - No stable public target was found.
  - Activision's online services page uses an undocumented keyless JSON: `https://prod-psapi.infra-ext.activision.com/open/api/apexrest/oshp/landingpage`. It returned HTTP 200 JSON in this session, with `serverStatuses[]` items (medium: no documentation).
  - RICOCHET uses a kernel driver that "monitors and reports applications that attempt to interact with" the game (medium, https://support.activision.com/articles/ricochet-overview).
- **Blizzard (Overwatch 2, WoW):**
  - Blizzard holds AS57976, AS32163 and AS55497 (high).
  - `us.actual.battle.net` resolved to Google Cloud (AS396982) and answered ICMP. It is the Battle.net login service, not a game server.
  - Overwatch shows the server IP only on screen, in the Network Graph (medium, search summary of https://support.blizzard.com/en/article/7779). The user can paste it as a user-entered target.
  - We found no keyless status feed (medium, https://support.blizzard.com/en/article/2886).
  - Blizzard asks for traceroute, then pathping, then WinMTR, run during the problem, to the game IP (medium, search summaries of https://support.blizzard.com/article/000007870 , https://us.support.blizzard.com/en-us/article/7871 and https://support.blizzard.com/article/000027780).
  - A Blizzard staff forum post says that the Looking Glass tool was shut down (low, https://us.forums.blizzard.com/en/wow/t/blizzard-looking-glass-down/1568188).
- **Ubisoft:** RIPEstat lists AS8361, AS22634 and other Ubisoft ASNs (high, `https://stat.ripe.net/data/searchcomplete/data.json?resource=Ubisoft`). We did no other research. Not verified.

### 4.7 Minecraft

- Most multiplayer servers are run by third parties, so the target is the server that the user joins. Make it a **user-entered** target.
- `sessionserver.mojang.com` and the Realms API resolve to Azure Front Door, AS8075 (high, RIPEstat).
- `%APPDATA%\.minecraft\logs\latest.log` is the Java log (medium, https://hopper.minecraft.net/help/guides/getting-minecraft-latest-log/). Whether it writes the server address on join is **not verified**.
- `status.mojang.com` does not resolve. We found no official status feed (high for the DNS result).

---

## 5. Consoles (PlayStation, Xbox, Nintendo)

**What a PC on the same home network can measure (high):** the shared part of the path. That is the router, the ISP access and the ISP's path to the platform's front-door edge.

**What it cannot measure:**
- The console's NAT type.
- The console's Wi-Fi.
- The console's port mappings.
- The console's ping to a match. Most matches run on publisher servers or peer to peer. The Microsoft GDK says that P2P titles "are still expected to provide their own public IP address and port discovery along with a NAT punching solution". Source: https://learn.microsoft.com/en-us/gaming/gdk/_content/gc/networking/overviews/game-mesh/preferred-local-udp-multiplayer-port-networking (high).

**DNS rule (high, measured):**
- A Nintendo CDN name gave a different edge through DNS over HTTPS (DoH) than through the system resolver.
- For console proxies, resolve with the system resolver. The console most likely uses the same resolver, through the router (medium: the user can set custom DNS on a console).

| Platform | Candidate target | Owner of the target (ASN) | ICMP | Conf. |
|---|---|---|---|---|
| Xbox | `user.auth.xboxlive.com` (sign-in) | Microsoft AS8075 | answered | high (one sample) |
| PlayStation | `ps5.np.playstation.net` | Akamai, not Sony (Sony is AS33353) | answered | high (one sample) |
| Nintendo | `ctest.cdn.nintendo.net` (connection test; the name comes from the community) | Akamai, not Nintendo (Nintendo is AS11278) | answered | DNS and ASN high; role low |
| Nintendo | `conntest.nintendowifi.net` | AWS AS16509 | did not answer | high |

**Status feeds (all keyless and undocumented; each is the data file of the official status page) (high, fetched in this session):**
- **PSN:** `https://status.playstation.com/data/statuses/region/{SCEA|SCEE|SCEJA}.json`.
  - The page reloads it every 5 minutes (`status.playstation.com/config/app.json`).
  - Some messages exist in only one locale, so fall back to `messageKey`.
- **Xbox:** `https://xnotify.xboxlive.com/servicestatusv6/{CC}/{locale}`.
  - It returns XML by default, or JSON with `Accept: application/json`.
  - `support.xbox.com/en-US/xbox-live-status` loads it.
  - State values other than `None` are not verified.
- **Nintendo:** `https://www.nintendo.co.jp/netinfo/{locale}/status.json`.
  - Its Content-Type is `application/octet-stream`.
  - Dates are local text ("Pacific Time" on the `en_US` page). Show the raw text when the app cannot parse a date.

**Official port lists (show as information only; never tell the user to open ports):**
- **PS5:** TCP 80, 443 and 3478 to 3480. UDP 3478, 3479 and 49152 to 65535 (high, https://www.playstation.com/en-us/support/error-codes/ps5/ce-106444-9).
- **Xbox:** UDP 88, 500, 3544 and 4500. UDP/TCP 3074 and 53. TCP 80 (medium, search summary of https://support.microsoft.com/en-gb/help/4026770/xbox-open-these-network-ports-for-xbox-one, which now redirects to support.xbox.com).
- **Switch:** UDP 1024 to 65535 (medium, search summary of https://www.nintendo.co.uk/Support/Nintendo-Switch/How-to-Set-Up-a-Router-s-Port-Forwarding-for-a-Nintendo-Switch-Console-1498000.html). One news site quotes 1 to 65535 (https://www.nintendolife.com/news/2022/09/psa-switch-connection-problems-we-wouldnt-recommend-nintendos-network-fix).

**Detect console play:**
- The Xbox presence API needs a sign-in token (high, https://devdocs.xbox.com/reference/live/rest/uri/presence/uri-usersmeget).
- We found no public PSN or Nintendo presence API (medium).
- LAN packet capture and ARP tricks are **out of scope**. They watch another person's device, they need admin rights and a capture driver, and they usually see nothing on a switched network. ARP spoofing is a man-in-the-middle attack.
- **Honest option:** a manual "I am playing on console" switch that sets the 5-minute interval.

---

## 6. Web, social and streaming services

**These services are websites or CDN customers, not game paths.** A trace ends at a CDN or cloud edge, and sometimes at a cache inside the user's own ISP. So the honest measurement is "the edge is reachable, with this connect time", not "the service is up".

### 6.1 How to measure them

- **New probe kind: HTTPS edge timing.** Measure DNS time, TCP connect time, TLS handshake time and time to the first byte to the service's main hostname. Then trace to the resolved IP. The app has no HTTPS probe today; `TestRunner` sends only ICMP (`GeckoPeeks/Net/TestRunner.cs:301-347`, high).
- **Resolve and look up the ASN at each run.** CDN answers change with the user's location. Use an "expected owner" list only to put a name on a hop, or to flag "the edge is in an unexpected network". Do not hard-code IPs (high: geo-selected DNS answers were seen).
- **In-ISP caches.** Some Akamai-hosted names (TikTok video, Spotify audio) answered from an address in the user's own ISP's ASN. Show this as "CDN cache inside your ISP". Do not call it Akamai's network (high: seen; the ISP name is left out on purpose).
- **ICMP is optional evidence.** All 17 tested web hostnames answered echo from one vantage point (medium). A missing reply means "did not answer", not "down".
- **Cloudflare `/cdn-cgi/trace`** shows the serving data center (`colo=`). It also shows the client's public IP. If the app uses it, it must drop the `ip=` line before it saves or exports anything (high: field seen; the privacy rule is a proposal).

### 6.2 Targets, owners and status feeds

ASN holders: RIPEstat `as-overview` (high, unless the table says otherwise). "Keyless JSON" means that the URL returned JSON to curl with no key in this session.

| Service | Target hostname (what it is) | Owner of the edge seen | Status feed | Conf. |
|---|---|---|---|---|
| Twitch (viewers) | `www.twitch.tv` (web) | Fastly AS54113 | Statuspage `https://status.twitch.com/api/v2/summary.json` (documented at https://status.twitch.com/api) | high |
| Twitch (streamers) | The nearest ingest from the keyless list `https://ingest.twitch.tv/ingests`. Remove the `{stream_key}` part. Never ask for or store a stream key. | Amazon AS16509 (the ingest network) | same | high |
| YouTube / YouTube Live | `www.youtube.com` (web); `a.rtmp.youtube.com` (Live ingest) | Google AS15169 | **None official.** `status.youtube.com` does not resolve. Google Cloud's feed is not YouTube status. | high |
| Kick | `kick.com` (web) | Cloudflare AS13335 | `status.kick.com` is a Datadog page. No keyless JSON found. Link only. | high |
| Facebook, Instagram, WhatsApp, Messenger | `www.facebook.com`, `www.instagram.com`, `web.whatsapp.com`; `live-api-s.facebook.com` (Facebook Live ingest) | Meta AS32934 | **None for consumers.** `metastatus.com` covers "Meta Business Products" only. | high |
| X | `x.com` | Cloudflare AS13335 (not Twitter's own ASNs) | **None official.** `status.x.com` does not resolve. | high |
| Reddit | `www.reddit.com` | Fastly AS54113 | Statuspage `https://www.redditstatus.com/api/v2/summary.json` | high |
| TikTok | `www.tiktok.com` | Akamai, or a cache inside the ISP | **None official.** | high |
| Snapchat | `www.snapchat.com` | Google Cloud AS396982 | **None official.** | high |
| Zoom | `zoom.us` (web front) | Cloudflare Spectrum AS209242, not Zoom's AS30103 | Statuspage: `https://status.zoom.us/api/v2/summary.json` redirects to `https://www.zoomstatus.com/api/v2/summary.json` | high |
| Microsoft Teams | `teams.microsoft.com` (front door) | Microsoft AS8075 | `https://status.cloud.microsoft/api/posts/m365Consumer` returned keyless JSON, but it is not documented. Tenant health needs an admin. | high (fetched); low (stability) |
| Spotify | `open.spotify.com` (web) | Fastly; the API front is on Google Cloud | No keyless JSON found. AS8403 is often called Spotify, but RIPEstat shows no holder. Do not label any hop "Spotify" from it. | high (DNS); not verified (AS8403) |
| Netflix | `www.netflix.com` (site) | Netflix AS40027. Video comes from Open Connect appliances, often inside the ISP (https://openconnect.netflix.com/en/). Not measured. | No status API. Netflix tells users to test speed at fast.com (https://help.netflix.com/en/node/306). | high |
| GeForce NOW | `play.geforcenow.com` (web front) | Amazon CloudFront AS16509. PeeringDB lists "NVIDIA GFN" AS20347 and AS50889 for streaming. Not seen in a trace. | Statuspage `https://status.geforcenow.com/api/v2/summary.json` | high (front, status); medium (GFN ASNs) |
| Xbox Cloud Gaming | `xgpuweb.gssv-play-prod.xboxlive.com` (front) | Microsoft AS8075 | The Xbox xnotify feed (section 5) | high (front); medium (feed stability) |
| Cloudflare | `one.one.one.one` | AS13335 | Statuspage `https://www.cloudflarestatus.com/api/v2/summary.json` | high |
| Akamai | any customer name on `*.akamaiedge.net` | AS20940 / AS16625 | Statuspage `https://www.akamaistatus.com/api/v2/summary.json` | high |
| Fastly | `www.fastly.com` | AS54113 | The JSON works only with a full browser User-Agent. Use `https://www.fastlystatus.com/rss`, and never send a fake browser User-Agent. | high |
| AWS | a CloudFront name | AS16509 | `https://health.aws.amazon.com/public/currentevents`: keyless JSON in UTF-16, not documented | high (fetched); medium (stability) |
| Google Cloud | `www.google.com` | AS15169 | `https://status.cloud.google.com/incidents.json`, documented at https://cloud.google.com/support/docs/dashboard. Google Cloud only. | high |
| Azure | `www.azurefd.net` (Front Door) | AS8075 | RSS `https://azure.status.microsoft/en-us/status/feed/` | high |

**Twitch ASN note:** AS46489's RIPEstat holder is "TWITCH". PeeringDB now names it "Amazon IVS" (https://www.peeringdb.com/api/net?asn=46489). No Twitch hostname that we resolved was in AS46489 (high). Use AS46489 only as a label when a measured hop is in it.

**Official tools to link (do not copy them):**
- Twitch Inspector: https://inspector.twitch.tv/. It finds dropped frames and bitrate problems, which ICMP cannot show.
- Zoom test meeting: https://zoom.us/test.
- Microsoft 365 connectivity test: https://connectivity.office.com/.
- Netflix's fast.com.

### 6.3 What is honest to show

- "Twitch web edge (Fastly): reachable, TLS handshake in N ms. The route ends in AS54113 Fastly."
- "Nearest Twitch ingest: TCP connect in N ms. The route ends in AS16509 Amazon." Bitrate and dropped frames are not measured.
- "Netflix site: reachable. Video servers (Open Connect): not measured."
- "Zoom web front: reachable. Meeting media: not measured."
- "No official status feed" where none exists.
- Show a feed only under its owner's name.
- Show a CDN's status feed as "Cloudflare status", never as the status of its customers.
- **Never** output "<service> is fine" or "<service> is down" from our own probes.

### 6.4 Ranking (for a gamer or streamer on a home PC)

1. **Discord:** used in most gaming sessions. Good status feed (sections 4.2 and 10).
2. **Twitch:** the largest streamer use. A keyless ingest list gives a real, measurable upload path.
3. **YouTube / YouTube Live:** the second streaming platform. A fixed ingest host.
4. **Cloudflare (status only):** it fronts Discord, X, Kick and Zoom's web. One feed explains many outages at once.
5. **GeForce NOW and Xbox Cloud Gaming:** real game streams where latency matters. The stream servers need their own design later. Phase 2 can show the status and the front door only.
6. **Kick:** a growing streamer platform. Edge only, and a status link only.
7. **AWS, Fastly, Akamai (status only):** under Twitch, Reddit, Spotify, TikTok and Steam's web.
8. **Spotify and Netflix:** heavy home use, but the media path cannot be measured. Link to fast.com.
9. **Zoom and Teams:** occasional use at home. Strong official tools exist, so link them.
10. **Meta apps, Reddit, X, TikTok, Snapchat:** browsing. Edge reachability only. Most have no status feed.

---

## 7. Detecting a game safely (all anti-cheats)

**Vendor statements:**
- VAC and CS2 Trusted Mode (high, section 4.1).
- RICOCHET reports applications that "attempt to interact with" the game (medium, section 4.6).
- EA keeps a denylist of software that blocks play (medium, https://help.ea.com/help/faq/troubleshoot-easy-anti-cheat).
- Roblox acts against modified clients (medium, section 4.4).
- We found no vendor page that names external ping or traceroute tools as allowed (not verified).

**What Gecko Peeks must not do:**
- Do not read or write game memory. Do not open a game process with more than the rights needed to read its start time. `LeagueProcess.StartedUtc` sets the pattern.
- Do not inject DLLs, hook APIs or draw an overlay in a game.
- Do not capture, filter or inspect game packets: no Npcap, no WinDivert, no WFP filters on game traffic.
- Do not change game files, game executables or launch options. A GSI cfg file is a text file that the game reads. Write it only after the user asks, and tell the user where it goes.
- Do not use undocumented tokens from a signed-in session, for example for PSN presence.

**Low-risk pattern (same as the League feature today):**
- Find the process by name in the process list.
- Read a log that the game writes for the user. Read it read-only, with shared access, only while the game runs. Take only the one field that is needed. `LeagueMatch.cs` sets the pattern.
- Send ICMP from Gecko Peeks's own process to a public address.
- Fetch a public status JSON.
- Use an official local API that the user approves: Discord RPC, or Valve GSI.

---

## 8. Diagnostic evidence: what support teams ask for, and what the export should hold

### 8.1 What vendors ask for

| Vendor | Evidence requested | Source | Conf. |
|---|---|---|---|
| Riot | `tracert` to the regional IP; Network Analysis, Trace Route and Process List logs; per-match netlog | section 3 | high |
| Blizzard | traceroute, then pathping, then WinMTR, each to the game IP, during the problem | https://support.blizzard.com/article/000027780 ; https://us.support.blizzard.com/en-us/article/7871 | medium |
| Epic | ping test to the subregion, plus the game mode; tell the ISP that the test is ICMP and the game is UDP | section 4.3 | medium |
| EA | UO Trace to `easo.ea.com`; remove your IP before you post it | https://forums.ea.com/discussions/apex-legends-technical-issues-en/how-do-i-run-traceroute-on-apex-servers/5340458 | low to medium |
| Discord | voice debug log, the clients affected, the device list | section 4.2 | medium |
| Rockstar, Activision, consoles | NAT type, ports, router restart, a test on another network such as a mobile hotspot | sections 4.5 and 5 | medium |
| Zoom | the client's Network Connectivity Tool (MTR, ping, traceroute); QoS logs | https://support.zoom.com/hc/en/article?id=zm_kb&sysparm_article=KB0058114 | medium |
| Microsoft Teams | the Network Assessment Tool; it can run on the client and at the network edge, to test each segment | https://learn.microsoft.com/en-us/training/modules/plan-configure-network-settings-for-microsoft-teams/08-assess-network-readiness-using-network-connectivity-test-tool | medium |
| Network operators | MTR in both directions for at least 10 minutes, the client IP, the start time of the problem, the affected application | https://www.ionos.com/help/server-cloud-infrastructure/troubleshooting/required-information-for-network-tickets/ ; https://www.akamai.com/cloud/guides/diagnosing-network-issues-with-mtr | high |

What a residential ISP's ticket form asks for is not verified. We found no ISP that publishes its ticket fields.

### 8.2 Method: "my side" against "the service's side"

**Primary source:** Richard Steenbergen, "A Practical Guide to (Correctly) Troubleshooting with Traceroute", NANOG 80. https://storage.googleapis.com/site-media-prod/meetings/NANOG80/2259/20201020_Steenbergen_Troubleshooting_With_Traceroute_v1.pdf (high, slides read).

**What the slides say:**
- A router makes ICMP replies on a slow CPU, and "ICMP Generation is NOT a priority for the router".
- Loss or delay at one hop that does not "persist across ALL future hops" is cosmetic.
- Traceroute shows only the forward path. The return path can be different.
- Ask for traces in both directions, with the real source and destination IPs.

**The method for Gecko Peeks:**

1. **Split the path by owner, with the ASNs the app already looks up:**
   - Home: from the PC to the gateway (hop 1).
   - ISP: the public hops in the user's ISP ASN.
   - Handoff: the first hop in another ASN. The app already calls this the handoff.
   - Service: the hops in the target's owner ASN. That is the service's own ASN, or the CDN or cloud ASN for those classes.
2. **Blame a segment only for loss that persists.** Show loss at a middle hop as real only when it continues to the destination. Otherwise, show it in grey as "probably ICMP rate limiting".
3. **Find where the persistent loss starts:**
   - Loss that starts at hop 1 points to the home network or Wi-Fi.
   - Loss that starts inside the ISP ASN points to the ISP.
   - Loss that starts at or after the handoff is "probably beyond your ISP".
4. **State the limits on the page:**
   - The return path is not tested.
   - ICMP can be treated differently from game UDP.
   - "Beyond the handoff" is probable, not proven.
5. **Use time.** Show how many runs support a verdict, for example "9 of 12 runs between 19:00 and 22:00".
6. **Use a control target.** Add a trace to one neutral anycast target, for example Cloudflare's `one.one.one.one`. This is a new target. Today the app traces only the Riot target. Loss on both targets from the same hop points to the shared path (home or ISP). Loss only on the service target, after its handoff, points to the service side.

### 8.3 Proposed per-service export (builds on the v2.1.5 "Export diagnostics" zip)

This is a proposal that follows from 8.1 and 8.2. It is not a vendor requirement.

**Header:**
- The app version.
- The UTC time of the export, plus the local offset.
- The service and the region.
- The target host and IP, its class (section 2), and why the app chose it, for example "Riot-published tracert IP".
- The time window, the number of runs and the interval.

**For each run:**
- Every hop: IP, reverse DNS, ASN and name, RTT, loss.
- The destination result on its own line: answered or not, RTT, loss.
- The method: ICMP, TTL-limited, the probe count.
- The handoff hop.
- The service status feed state at that time.

**Formats that support staff know:**
- `tracert`-style text for Riot and Blizzard.
- An MTR/WinMTR-style table (hop, loss %, sent, best, avg, worst) for ISPs.

**User-chosen context:**
- The problem window that the user reports.
- Wi-Fi or Ethernet.
- VPN on or off.
- A pasted speed-test URL.

**Log paths only:**
- List where the game and app logs are, for example the League netlog folder or `%APPDATA%\discord\logs`. Do not copy their contents unless the user asks.

**Plain-words summary:**
- The verdict for each segment, with the rule that produced it.
- A "what to send where" block.

**Privacy (a proposal that follows Riot's and EA's warnings):**
- The public IP is off by default. The user can turn it on for an ISP ticket, because Steenbergen and IONOS want the real source IP.
- Mask the ISP-internal hop IPs in "forum" mode.
- Remove LAN IPs, MAC addresses, the SSID and the Windows user name. Write `%USERPROFILE%` for the user folder.
- Never collect game account IDs automatically.
- Do not include a process list.
- Add a `REDACTIONS.txt` file that lists what the app removed.

---

## 9. Design: data model, app seams, UI

### 9.1 Refdata: add an optional `services` block (schema stays 1)

**Facts (high, code read):**
- The loader refuses a file whose `schema` is not 1 (`GeckoPeeks/Routing/RefData.cs:234-236`).
- The loader reads only the keys it knows, with `TryGetProperty`: `regions`, `providers`, `cityCodes` and the other known keys (`RefData.cs:242`, `:299`, `:358`).
- The parse function has no check that refuses unknown top-level keys (`RefData.cs:225-300`, read in full).
- The size limit is 512 KB (`RefData.cs:36`, `:226`). Today's file is about 120 KB, so a `services` block has room.
- So an old app ignores a new `services` key and keeps working. A schema bump to 2 would make every old app refuse the file and fall back to its built-in copy.
- **Recommendation:** add `services` under schema 1. Gate new behavior on the app version, not on the schema.

**Proposed shape (illustration, not final):**

```json
"services": [
  {
    "id": "valve-sdr",
    "name": "Steam (CS2, Dota 2)",
    "targetClass": "owned",
    "owner": { "name": "Valve", "asns": [32590] },
    "target": {
      "kind": "sdr-nearest-pop",
      "configUrl": "https://api.steampowered.com/ISteamApps/GetSDRConfig/v1/?appid=730",
      "label": "Route to your nearest Valve relay (ICMP)",
      "icmpExpected": true
    },
    "processes": ["cs2.exe", "dota2.exe"],
    "processesVerified": false,
    "status": null,
    "statusNote": "Valve publishes no status feed",
    "caveat": "In-game ping goes through Valve's relay network.",
    "support": { "asks": ["traceroute"], "url": null }
  },
  {
    "id": "discord",
    "name": "Discord",
    "targetClass": "cdn-edge",
    "owner": { "name": "Cloudflare (for Discord)", "asns": [13335] },
    "target": { "kind": "hostname", "host": "latency.discord.media", "resolver": "system", "icmpExpected": true,
                "label": "Cloudflare edge used by Discord. Not your voice server." },
    "processes": ["Discord.exe"],
    "processesVerified": true,
    "status": { "kind": "statuspage", "url": "https://discordstatus.com/api/v2/summary.json" }
  }
]
```

**Target kinds that the app needs:**
- `ip`: today's model. Riot uses it.
- `hostname`: resolve before each run. Use `resolver: "system"` for CDN and console targets.
- `sdr-nearest-pop`: fetch the relay list once a day, then pick a POP.
- `user-entered`: the user types the address.
- `none`: status only.

**SDR POP choice:**
- Pick the POP with the lowest ICMP RTT among a few candidates near the user. Show the POP's own `desc`.
- Do not hard-code relay IPs. The list changes with `revision`.

**Status kinds:**
- `statuspage`: Discord, Epic.
- `statusio`: Roblox.
- `riot`: the feed that the app reads today.
- `psn`, `xnotify`, `nintendo`.
- `activision`.
- Each kind needs its own mapper, like `cloud/worker/src/riot.js`, which caps the size and the field lengths.

### 9.2 App seams that the change touches (high, code read)

- **The target is an IP only.** `ProbeRequest(bool LeagueRunning, string TargetIp, ...)` (`GeckoPeeks/Net/TestRunner.cs:17`). The default is `192.207.0.5` (`TestRunner.cs:25`). Hostname and SDR targets need a resolve step before each run. That step must record the resolved IP and the resolver used.
- **Riot names are fixed in the probe and the CSV.** Examples: the `RiotHopAvg` and `RiotHopLossPct` fields and the "Ping Riot" progress text (`TestRunner.cs:278-287`). The CSV columns are fixed, and other tools may read them. Keep the Riot CSV as it is. Write one CSV for each extra service, or add a `Service` column only in a new file. That is a decision for the implementation.
- **Game detection is League-only.** `LeagueProcess.IsRunning` looks for `League of Legends` (`GeckoPeeks/App/LeagueProcess.cs:12`). The 5-minute interval follows `_league` (`ProbeScheduler.cs:421`). Generalize this to "any watched service is running".
- **The Worker pulse is Riot-only.** `buildPulse` reads Radar for `RIOT_ASN` (`cloud/worker/src/index.js:473-516`). Per-service Radar data would mean more Radar calls. Free-plan limits and caching need a check before that work.
- **The README promises the exact calls that the app makes** (the "Cloudflare live data" section). The README lists `GET /v1/edge`, `/v1/pulse` and `/v1/asn`. Each new probe target and status feed changes that promise. **Recommendation:**
  - Fetch every status feed through the Worker, the way `/v1/riot-status` works today. Then the app still talks to one host, the Worker caps sizes, and the cache serves all users.
  - Let the app fetch `GetSDRConfig` through the Worker too, once a day.
  - Update the README list in the same release.
- **The ASN lookup already sends public hop IPs to the Worker.** New targets add more IPs, so the 15-IP cap and the cache stay as they are.

### 9.3 UI

- **Service picker in Settings:**
  - A list of services with switches. Riot is on by default; the others are off.
  - Each row shows the target class in plain words, for example "Valve relay", "Cloudflare edge" or "Status only".
- **Overview:**
  - One compact card for each service that is on. Each card shows its status feed state, the last route verdict by segment, and the target class label.
  - A service with no measurement shows "not measured", not a blank or a zero.
- **Route page:**
  - A selector for the service.
  - The hop table colors segments by owner: home, ISP, handoff, service. It greys out cosmetic loss (section 8.2).
- **Consoles:**
  - One card for each platform, with the status feed and the shared path.
  - A "Playing on console" switch.
  - A fixed note: "Shared path from your home to the {platform} edge. This is not your console's ping, NAT type or Wi-Fi."
- **Export:** "Export diagnostics" asks which service and which time window. Then it writes the files from section 8.3.

---

## 10. Phased plan

**Ranking rule:**
- First: owned-network targets with a keyless target list and a working ICMP reply.
- Then: services with a good status feed.
- Last: services that need approval or user input.

| Phase | Services | Why | Work |
|---|---|---|---|
| **1** | Valve SDR (Steam, CS2, Dota 2) | Owned network (AS32590). Keyless relay list with coordinates. Relays answered ICMP. A large audience. | `sdr-nearest-pop` target, a Worker proxy for `GetSDRConfig`, a "Valve" provider entry (AS32590, `valve.net`), a "no status feed" note. |
| **1** | Discord | Most users run it next to a game. Keyless Statuspage feed with voice-region components. The edge target answers ICMP. | `hostname` target, a `statuspage` mapper in the Worker, the "Cloudflare edge" label. |
| **1** | Console status: PSN, Xbox, Nintendo | Three keyless feeds. Strong user demand. The shared-path proxy is honest when labeled. | Three mappers. Hostname targets with the system resolver. The "Playing on console" switch. |
| **2** | Fortnite | Region ping hosts answer ICMP. Keyless Statuspage. | `hostname` target with a region picker. The host names are not documented, so add a check that turns the target off when the name stops resolving. |
| **2** | Roblox | Owned network (AS22697). Keyless status.io feed. Possibly a server address from the client log. | Edge target first. The log reader only after a real test proves the log line and path. |
| **2** | Call of Duty, GTA Online, Apex | No public target. | Status-only cards. CoD has an undocumented feed. GTA and Apex: "no feed found". |
| **3** | Discord RPC voice server and ping | It gives Discord's own numbers. | Needs Discord's approval of the app and user consent. Apply first, then build. |
| **3** | User-entered targets (Minecraft servers, an Overwatch IP) | Covers games with no public target. | The `user-entered` kind, labeled "your address". |
| **2** | Twitch and YouTube Live ingest; CDN and cloud status (Cloudflare, AWS, Akamai, Fastly RSS); GeForce NOW and Xbox Cloud Gaming status and front door | Streamers get a real upload path. One CDN feed explains many outages at once. Cloud gamers get the status and the front door. | A new HTTPS edge-timing probe (section 6.1). Ingest targets from Twitch's keyless list, or YouTube's fixed host. The GFN Statuspage mapper. The Xbox feed is already in phase 1. |
| **3** | Other web, social and streaming services (section 6.4) | Browsing services. Edge reachability only. | They reuse the HTTPS probe. Each card shows "not measured" for the media path. |
| **3** | GeForce NOW and Xbox Cloud Gaming stream servers | Real game streams, but we found no public stream-server target. | Phase 2 shows the status and the front door only. The stream path needs its own research. |

**Order inside phase 1:**
1. The `services` model and the Worker proxies.
2. Valve.
3. Discord.
4. Consoles.

Each step is one release that can be tested on its own.

---

## 11. Risks

| Risk | Effect | Control |
|---|---|---|
| Undocumented endpoints change (SDR config, Discord RTC list, Fortnite ping hosts, all three console feeds, Activision feed) | A target or feed stops working, or shows wrong data | Proxy through the Worker. Validate the shape and refuse unknown data. Show "not available" instead of old data. Ship refdata updates to turn off a broken service. |
| A CDN edge result read as "the service is fine" | An incorrect fact (the project's hard rule) | The target class label on every card and in every export (section 2). |
| ICMP is rate-limited or treated differently from game UDP | Wrong blame | The persistence rule, grey cosmetic loss, the "ICMP, not game traffic" note (section 8.2). |
| Anti-cheat flags the app | User bans, loss of trust | Only the low-risk pattern in section 7. No new process access beyond the start time. |
| More traces from each PC | More load and more ICMP from each user | Trace only the services that are on. Keep the 15-minute interval. Do the 5-minute interval only while that service's game runs. |
| The privacy promise in the README | A broken promise if new calls are not listed | Route feeds through the Worker. Update the README call list in the same release. |
| Process names unverified (most games) | No detection, or a false detection | Mark `processesVerified: false` in refdata. Confirm on a real install before turning a name on. |
| Console DNS differs from the PC's | Wrong edge traced | The system resolver for console targets. The label says "probably the same edge". |
| The CSV format changes | Other tools that read the CSV break | Keep the Riot CSV as it is. Put each new service in its own file. |

---

## 12. Not verified (open items)

- Windows process names for CS2, Dota 2, Fortnite, Apex, CoD, Overwatch, WoW, Minecraft Bedrock and Roblox. Only `steam.exe` and `Discord.exe` were seen on disk.
- Whether `latency.discord.media/rtc` depends on the caller's location.
- Whether SDR relays and the other targets answer ICMP from other networks and regions.
- The CS2 and Dota 2 `-condebug` log, and what CS2 shows as the server under SDR.
- The Roblox log path and line format. The ICMP behavior of the UDMUX proxy.
- Whether Minecraft's `latest.log` writes the server address on join.
- A Rockstar status JSON. An EA status JSON. A Blizzard status JSON.
- State values other than "no incident" in the Xbox and PSN feeds.
- The Xbox and Nintendo port lists on a page we fetched ourselves.
- Residential ISP ticket fields.
- Any vendor page that names external ping or traceroute tools as allowed during play.
