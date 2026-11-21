/* Gecko Peeks v2 web UI.
   Talks to the host through WebView2 postMessage (Contract 2).
   Without a host it runs on a built-in mock state.
   All state strings go into the page with textContent only. */
(function () {
  'use strict';

  // ---------- Small helpers ----------
  var SVGNS = 'http://www.w3.org/2000/svg';
  function $(sel, root) { return (root || document).querySelector(sel); }
  function $all(sel, root) { return Array.prototype.slice.call((root || document).querySelectorAll(sel)); }

  // h('div', {class:'x', onclick:fn}, 'text', childNode)
  function h(tag, props) {
    var node = document.createElement(tag);
    applyProps(node, props);
    for (var i = 2; i < arguments.length; i++) append(node, arguments[i]);
    return node;
  }
  function s(tag, props) {
    var node = document.createElementNS(SVGNS, tag);
    applyProps(node, props);
    for (var i = 2; i < arguments.length; i++) append(node, arguments[i]);
    return node;
  }
  function applyProps(node, props) {
    if (!props) return;
    Object.keys(props).forEach(function (k) {
      var v = props[k];
      if (v === null || v === undefined || v === false) return;
      if (k.slice(0, 2) === 'on' && typeof v === 'function') node.addEventListener(k.slice(2), v);
      else if (k === 'text') node.textContent = String(v);
      else node.setAttribute(k, v === true ? '' : String(v));
    });
  }
  function append(node, child) {
    if (child === null || child === undefined || child === false) return;
    if (Array.isArray(child)) { child.forEach(function (c) { append(node, c); }); return; }
    node.appendChild(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  function clear(node) { while (node.firstChild) node.removeChild(node.firstChild); return node; }
  function setText(node, text) { if (node && node.textContent !== text) node.textContent = text; }
  function isNum(v) { return typeof v === 'number' && isFinite(v); }
  function str(v) { return typeof v === 'string' ? v : (v === null || v === undefined ? '' : String(v)); }
  function ms(v) { return isNum(v) ? Math.round(v) + ' ms' : '—'; }
  function pct(v) { return isNum(v) ? (Math.round(v * 10) / 10) + '%' : '—'; }
  function plural(n, one, many) { return n + ' ' + (n === 1 ? one : many); }
  var MON = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
  var DOW = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
  function pad(n) { return (n < 10 ? '0' : '') + n; }
  function hhmm(d) { return pad(d.getHours()) + ':' + pad(d.getMinutes()); }
  function dayLabel(d) { return MON[d.getMonth()] + ' ' + d.getDate(); }
  // League's own in-game ping row (its netlog), or null.
  function gamePingOf(g) {
    if (!g || typeof g !== 'object' || !isNum(g.ms)) return null;
    var at = parseIso(g.atUtc);
    return { ms: g.ms, lossPct: isNum(g.lossPct) ? g.lossPct : null, jitterMs: isNum(g.jitterMs) ? g.jitterMs : null, at: at, reconnect: g.reconnect === true };
  }
  // With the year when it is not this year, so "since Nov 3" never hides a year.
  function dayLabelY(d) { return dayLabel(d) + (d.getFullYear() !== new Date().getFullYear() ? ', ' + d.getFullYear() : ''); }
  // True when the host left the oldest tests out of the history it sent.
  function historyCut() { return isNum(S.historyTotal) && S.historyTotal > S.history.length; }
  function parseLocal(t) {
    var m = /^(\d{4})-(\d{2})-(\d{2})[T ](\d{2}):(\d{2})(?::(\d{2}))?/.exec(str(t));
    if (!m) return null;
    return new Date(+m[1], +m[2] - 1, +m[3], +m[4], +m[5], +(m[6] || 0));
  }
  function parseIso(t) { var d = t ? new Date(t) : null; return d && !isNaN(d.getTime()) ? d : null; }
  function agoText(sec) {
    if (!isNum(sec)) return '';
    if (sec < 45) return 'just now';
    if (sec < 3600) return Math.max(1, Math.round(sec / 60)) + ' min ago';
    if (sec < 86400) return Math.round(sec / 3600) + ' h ago';
    return Math.round(sec / 86400) + ' d ago';
  }
  function countdownText(msLeft) {
    if (msLeft <= 0) return 'due now';
    var t = Math.ceil(msLeft / 1000), hh = Math.floor(t / 3600), mm = Math.floor((t % 3600) / 60), ss = t % 60;
    return hh > 0 ? hh + ':' + pad(mm) + ':' + pad(ss) : mm + ':' + pad(ss);
  }
  function safeRadarUrl(u) {
    u = str(u);
    return /^https:\/\/radar\.cloudflare\.com\//.test(u) ? u : '';
  }
  function safeGithubUrl(u) {
    u = str(u);
    return /^https:\/\/github\.com\/gecko-of-shadow\/gecko-peeks-app(\/|$)/.test(u) ? u : '';
  }

  // ---------- Places ----------
  // Handoff codes (from hop host names) and Cloudflare colo codes.
  var PLACES = {
    sea: ['Seattle', 47.61, -122.33], den: ['Denver', 39.74, -104.99], chi: ['Chicago', 41.88, -87.63],
    ord: ['Chicago', 41.88, -87.63], yyc: ['Calgary', 51.05, -114.07], bil: ['Billings', 45.78, -108.5],
    lax: ['Los Angeles', 34.05, -118.24], sjc: ['San Jose', 37.34, -121.89], sfo: ['San Francisco', 37.77, -122.42],
    dfw: ['Dallas', 32.78, -96.8], dal: ['Dallas', 32.78, -96.8], iad: ['Ashburn', 39.04, -77.49],
    ash: ['Ashburn', 39.04, -77.49], nyc: ['New York', 40.71, -74.01], ewr: ['Newark', 40.74, -74.17],
    atl: ['Atlanta', 33.75, -84.39], mia: ['Miami', 25.76, -80.19], phx: ['Phoenix', 33.45, -112.07],
    slc: ['Salt Lake City', 40.76, -111.89], pdx: ['Portland', 45.52, -122.68], yvr: ['Vancouver', 49.28, -123.12],
    van: ['Vancouver', 49.28, -123.12], yyz: ['Toronto', 43.65, -79.38], tor: ['Toronto', 43.65, -79.38],
    yul: ['Montreal', 45.5, -73.57], kanc: ['Kansas City', 39.1, -94.58], mci: ['Kansas City', 39.1, -94.58],
    palo: ['Palo Alto', 37.44, -122.14], yeg: ['Edmonton', 53.55, -113.49], ywg: ['Winnipeg', 49.9, -97.14],
    msp: ['Minneapolis', 44.98, -93.27], bos: ['Boston', 42.36, -71.06], hou: ['Houston', 29.76, -95.37],
    las: ['Las Vegas', 36.17, -115.14], san: ['San Diego', 32.72, -117.16], stl: ['St. Louis', 38.63, -90.2],
    den1: ['Denver', 39.74, -104.99], yow: ['Ottawa', 45.42, -75.7], yxe: ['Saskatoon', 52.13, -106.67],
    clt: ['Charlotte', 35.23, -80.84], bna: ['Nashville', 36.16, -86.78], det: ['Detroit', 42.33, -83.05],
    dtw: ['Detroit', 42.33, -83.05], phl: ['Philadelphia', 39.95, -75.17], pit: ['Pittsburgh', 40.44, -79.99],
    anc: ['Anchorage', 61.22, -149.9], hnl: ['Honolulu', 21.31, -157.86], mex: ['Mexico City', 19.43, -99.13]
  };
  function place(code) {
    var c = str(code).toLowerCase().trim();
    if (!c) return null;
    if (PLACES[c]) return PLACES[c];
    var letters = (/^[a-z]+/.exec(c) || [''])[0];
    return PLACES[letters] || null;
  }

  // ---------- State ----------
  function obj(v) { return v && typeof v === 'object' && !Array.isArray(v) ? v : {}; }
  function arr(v) { return Array.isArray(v) ? v : []; }
  function bool(v) { return v === true; }
  function numOrNull(v) { return isNum(v) ? v : null; }

  var EDGE_LABEL = 'Riot Direct edge (AS6507)';
  var PLATFORM_LABEL = 'Riot platform address (AS6507)';
  function regionId(v) { v = str(v).toLowerCase(); return /^[a-z]{2,3}\d?$/.test(v) ? v : ''; }

  function normalize(raw) {
    raw = obj(raw);
    var app = obj(raw.app), p = obj(raw.probe), log = obj(raw.log), c = obj(raw.cloud), u = obj(raw.update), st = obj(raw.settings);
    var lh = obj(p.lastHop), rt = obj(p.riot), rg = obj(raw.region), tg = obj(rg.target), rd = c.refdata ? obj(c.refdata) : null;
    var kind = str(tg.kind) === 'dns' ? 'dns' : 'anycast';
    return {
      app: { version: str(app.version) || '2.1.0', profile: str(app.profile) || 'Production', demo: bool(app.demo) },
      region: {
        id: regionId(rg.id), shortName: str(rg.shortName), name: str(rg.name), auto: rg.auto !== false, basis: str(rg.basis),
        serverCity: str(rg.serverCity), cityConfirmed: bool(rg.cityConfirmed), lat: numOrNull(rg.lat), lon: numOrNull(rg.lon),
        lolStatusId: str(rg.lolStatusId), valorantShard: str(rg.valorantShard),
        target: { ip: str(tg.ip), label: str(tg.label) || (kind === 'dns' ? PLATFORM_LABEL : EDGE_LABEL), kind: kind, inRegion: tg.inRegion !== false },
        lastTestTarget: str(rg.lastTestTarget)
      },
      regions: arr(raw.regions).map(function (r) {
        r = obj(r);
        return { id: regionId(r.id), shortName: str(r.shortName), name: str(r.name), serverCity: str(r.serverCity), cityConfirmed: bool(r.cityConfirmed) };
      }).filter(function (r) { return r.id; }),
      probe: {
        paused: bool(p.paused), leagueRunning: bool(p.leagueRunning), testRunning: bool(p.testRunning),
        matchServer: str(p.matchServer), matchSameAsTarget: bool(p.matchSameAsTarget),
        gamePing: gamePingOf(p.gamePing), testGamePing: gamePingOf(p.testGamePing), testLastHopIp: str(p.testLastHopIp),
        phase: str(p.phase), mode: str(p.mode), nextDueUtc: p.nextDueUtc || null, lastError: str(p.lastError),
        activityLine: str(p.activityLine), hasRoute: bool(p.hasRoute), routeLine: str(p.routeLine),
        upstream: str(p.upstream), handoffNetwork: str(p.handoffNetwork), handoffCity: str(p.handoffCity),
        handoffCityName: str(p.handoffCityName),
        upstreamSource: str(p.upstreamSource), handoffSource: str(p.handoffSource), upstreamAsn: numOrNull(p.upstreamAsn),
        upstreamAsns: arr(p.upstreamAsns).filter(isNum), noMoreTests: str(p.noMoreTests),
        handoffLat: numOrNull(p.handoffLat), handoffLon: numOrNull(p.handoffLon),
        lastHop: { avgMs: numOrNull(lh.avgMs), lossPct: numOrNull(lh.lossPct), figure: str(lh.figure) || '—' },
        riot: { avgMs: numOrNull(rt.avgMs), lossPct: numOrNull(rt.lossPct), figure: str(rt.figure) || '—' },
        contextLine: str(p.contextLine), changeLine: str(p.changeLine), changedHops: arr(p.changedHops),
        testTimestamp: str(p.testTimestamp), adapter: str(p.adapter), wifi: str(p.wifi), gateway: str(p.gateway),
        vpn: bool(p.vpn), league: bool(p.league),
        hops: arr(p.hops).map(function (x) {
          x = obj(x);
          return {
            n: isNum(x.n) ? x.n : 0, ip: str(x.ip), host: str(x.host), rttMs: numOrNull(x.rttMs),
            responded: bool(x.responded), reachedTarget: bool(x.reachedTarget), changed: bool(x.changed),
            isPrivate: bool(x.isPrivate), asn: numOrNull(x.asn), asName: x.asName ? str(x.asName) : '',
            network: str(x.network), nameSource: str(x.nameSource), cityName: str(x.cityName)
          };
        }),
        hopsAddressesOnly: bool(p.hopsAddressesOnly), tooltip: str(p.tooltip)
      },
      log: {
        path: str(log.path), folder: str(log.folder), fileName: str(log.fileName), health: str(log.health),
        chip: str(log.chip), chipTone: str(log.chipTone) || 'normal', status: str(log.status),
        statusTone: str(log.statusTone) || 'normal', unsavedRows: isNum(log.unsavedRows) ? log.unsavedRows : 0
      },
      // How many tests the log holds; more than history.length means the oldest were not sent.
      historyTotal: raw.history === undefined && S ? S.historyTotal : (isNum(raw.historyTotal) ? raw.historyTotal : null),
      // The host leaves "history" out when it has not changed: keep the copy the page has.
      history: raw.history === undefined && S ? S.history : arr(raw.history).map(function (r) {
        r = obj(r);
        return {
          t: str(r.t), d: parseLocal(r.t), upstream: str(r.upstream), handoffNetwork: str(r.handoffNetwork),
          handoffCity: str(r.handoffCity), handoffCityName: str(r.handoffCityName), lastHopAvg: numOrNull(r.lastHopAvg), lastHopLoss: numOrNull(r.lastHopLoss),
          riotAvg: numOrNull(r.riotAvg), riotLoss: numOrNull(r.riotLoss), league: bool(r.league), vpn: bool(r.vpn),
          adapter: str(r.adapter), namedLater: bool(r.namedLater)
        };
      }).filter(function (r) { return r.d; }),
      cloud: {
        enabled: bool(c.enabled), status: str(c.status) || 'off', error: str(c.error),
        edgeAgeSec: numOrNull(c.edgeAgeSec), pulseAgeSec: numOrNull(c.pulseAgeSec),
        edge: c.edge && typeof c.edge === 'object' ? c.edge : null,
        pulse: c.pulse && typeof c.pulse === 'object' ? c.pulse : null,
        riotStatus: c.riotStatus && typeof c.riotStatus === 'object' && !Array.isArray(c.riotStatus) ? c.riotStatus : null,
        riotStatusAgeSec: numOrNull(c.riotStatusAgeSec),
        refdata: rd ? { version: str(rd.version), source: str(rd.source), ageSec: numOrNull(rd.ageSec) } : null
      },
      update: {
        state: str(u.state) || 'idle', current: str(u.current) || str(app.version), available: u.available ? str(u.available) : null,
        progress: numOrNull(u.progress), notes: str(u.notes), releaseUrl: str(u.releaseUrl), error: str(u.error),
        lastCheckUtc: u.lastCheckUtc || null, autoInstall: bool(u.autoInstall)
      },
      settings: {
        startWithWindows: bool(st.startWithWindows), autoInstallUpdates: bool(st.autoInstallUpdates), cloudEnabled: bool(st.cloudEnabled),
        region: str(st.region) === 'auto' ? 'auto' : (regionId(st.region) || 'auto')
      }
    };
  }

  // ---------- Region wording ----------
  function inRiotPrefix(ip) { return /^192\.207\.0\.\d{1,3}$/.test(str(ip)); }
  // The label for a Riot address on the route. 192.207.0.0/24 is always the Riot Direct edge.
  function labelForIp(ip) {
    if (inRiotPrefix(ip)) return EDGE_LABEL;
    if (ip && ip === S.region.target.ip) return S.region.target.label;
    return PLATFORM_LABEL;
  }
  function riotShort() { return S.region.shortName ? 'Riot ' + S.region.shortName : 'Riot'; }
  // "Riot NA servers (Chicago)", "(Amsterdam, not confirmed)", "(location not published)".
  function serversLabel() {
    var r = S.region;
    if (!r.serverCity || !isNum(r.lat) || !isNum(r.lon)) return riotShort() + ' servers (location not published)';
    return riotShort() + ' servers (' + r.serverCity + (r.cityConfirmed ? '' : ', not confirmed') + ')';
  }
  function serverCityText() {
    var r = S.region;
    return r.serverCity ? r.serverCity + (r.cityConfirmed ? '' : ' (not confirmed)') : 'location not published';
  }
  // Empty when the trace ends in the region. Else the frozen caveat.
  function latencyCaveat() {
    var r = S.region;
    if (r.target.inRegion || !r.id) return '';
    return 'Latency is to a shared Riot Direct address, not to ' + (r.serverCity || 'the server city') + '.';
  }
  function targetText() { return S.region.target.label + (S.region.target.ip ? ' · ' + S.region.target.ip : ''); }
  // The target the shown test traced, when it is not the region target (the region changed
  // after that test). Null when they agree or no test is known.
  function testedTarget() {
    var r = S.region, ip = r.lastTestTarget;
    if (!ip || !r.target.ip || ip === r.target.ip) return null;
    return { ip: ip, label: inRiotPrefix(ip) ? EDGE_LABEL : PLATFORM_LABEL };
  }
  // "Region changed. The next test traces the EUW Riot platform address (AS6507) 185.40.64.65."
  function nextTargetNote() {
    var r = S.region;
    return 'Region changed. The table shows the last test, to ' + r.lastTestTarget + '. The next test traces the '
      + (r.shortName ? r.shortName + ' ' : '') + r.target.label + ' · ' + r.target.ip + '.';
  }
  // The upstream provider's ASNs (refdata), else the single upstream ASN.
  function upstreamAsns() {
    var p = S.probe;
    if (p.upstreamAsns.length) return p.upstreamAsns;
    return isNum(p.upstreamAsn) ? [p.upstreamAsn] : [];
  }
  // The hop the handoff was named from: the first hop after the upstream whose network names it.
  function handoffHop() {
    var p = S.probe, net = p.handoffNetwork;
    if (!net) return null;
    for (var i = 0; i < p.hops.length; i++) {
      var x = p.hops[i];
      if (x.network && x.network.split(/\s*\+\s*/).indexOf(net) >= 0 && x.network !== net) return x;
    }
    return null;
  }
  // "Zayo → Telia (interconnect)" when the handoff came from an upstream hop's host name
  // (a mixed name like zayo-telia) whose address belongs to the upstream. Else the network.
  function handoffNetworkText() {
    var p = S.probe, x = handoffHop();
    if (p.handoffSource === 'hostname' && x && p.upstream && isNum(x.asn) && upstreamAsns().indexOf(x.asn) >= 0)
      return p.upstream + ' → ' + p.handoffNetwork + ' (interconnect)';
    return p.handoffNetwork;
  }
  function handoffText() {
    var p = S.probe;
    return [handoffNetworkText(), p.handoffCityName || p.handoffCity].filter(Boolean).join(' · ');
  }

  var S = normalize({});
  var receivedAt = Date.now();
  var hasState = false;

  // ---------- Bridge ----------
  var webview = window.chrome && window.chrome.webview ? window.chrome.webview : null;
  var mock = null;
  var histVersion = 0;
  function send(msg) {
    if (webview) { try { webview.postMessage(msg); } catch (e) { /* host gone */ } }
    else if (mock) mock.handle(msg);
  }
  function receive(data) {
    if (typeof data === 'string') { try { data = JSON.parse(data); } catch (e) { return; } }
    data = obj(data);
    if (data.type === 'state') {
      // A new history copy (renamed rows too) always redraws the history views.
      if (data.history !== undefined) histVersion++;
      S = normalize(data);
      receivedAt = Date.now();
      hasState = true;
      render();
    } else if (data.type === 'toast') {
      toast(str(data.text), str(data.tone) || 'normal');
    }
  }
  function openUrl(url) {
    if (!url) return;
    if (webview) send({ cmd: 'openUrl', url: url });
    else window.open(url, '_blank', 'noopener');
  }

  // ---------- Toasts ----------
  function toast(text, tone) {
    if (!text) return;
    var box = $('#toasts');
    var t = h('div', { class: 'toast tone-' + (/^(good|warn|bad|normal)$/.test(tone) ? tone : 'normal'), role: 'status' }, h('span', { text: text }));
    box.appendChild(t);
    while (box.children.length > 4) box.removeChild(box.firstChild);
    setTimeout(function () {
      t.classList.add('out');
      setTimeout(function () { if (t.parentNode) t.parentNode.removeChild(t); }, 220);
    }, 4500);
  }

  // ---------- Router ----------
  var PAGES = ['overview', 'route', 'history', 'internet', 'settings'];
  var page = 'overview';
  function route() {
    var hash = location.hash || '';
    if (hash && hash.indexOf('#/') !== 0) { render(); return; } // in-page anchors such as the skip link
    var m = /^#\/(\w+)/.exec(hash);
    var next = m && PAGES.indexOf(m[1]) >= 0 ? m[1] : 'overview';
    var changed = next !== page;
    page = next;
    $all('.page').forEach(function (sec) { sec.hidden = sec.getAttribute('data-page') !== page; });
    $all('.nav a').forEach(function (a) {
      if (a.getAttribute('data-page') === page) a.setAttribute('aria-current', 'page');
      else a.removeAttribute('aria-current');
    });
    document.title = 'Gecko Peeks · ' + page.charAt(0).toUpperCase() + page.slice(1);
    render();
    if (changed) { window.scrollTo(0, 0); }
  }

  // ---------- Sidebar ----------
  function initNav() {
    var saved = null;
    try { saved = localStorage.getItem('gp.nav'); } catch (e) { saved = null; }
    if (saved === 'collapsed') document.body.setAttribute('data-nav', saved);
    applyNav();
    $('#btnNav').addEventListener('click', function () {
      var collapsed = document.body.classList.contains('nav-collapsed') || isAutoCollapsed();
      var next = collapsed ? 'expanded' : 'collapsed';
      document.body.setAttribute('data-nav', next);
      // Only "collapsed" is remembered. "Expanded" lasts for this session, so narrow windows still auto-collapse next time.
      try { if (next === 'collapsed') localStorage.setItem('gp.nav', next); else localStorage.removeItem('gp.nav'); } catch (e) { /* no storage */ }
      applyNav();
      setTimeout(function () { mapView.fit(false); redrawCharts(); }, 230);
    });
    window.addEventListener('resize', applyNav);
  }
  function isAutoCollapsed() { return document.body.getAttribute('data-nav') === 'auto' && window.innerWidth <= 1000; }
  function applyNav() {
    var mode = document.body.getAttribute('data-nav');
    document.body.classList.toggle('nav-collapsed', mode === 'collapsed');
    var collapsed = mode === 'collapsed' || isAutoCollapsed();
    var btn = $('#btnNav');
    var label = collapsed ? 'Expand sidebar' : 'Collapse sidebar';
    btn.setAttribute('aria-label', label);
    btn.setAttribute('title', label);
    $all('.nav a').forEach(function (a) {
      if (collapsed) a.setAttribute('title', a.textContent.trim()); else a.removeAttribute('title');
    });
  }

  // ---------- Range selector ----------
  var rangeDays = 7;
  function initRange() {
    try {
      var saved = localStorage.getItem('gp.range'), r = +saved;
      if (saved !== null && (r === 0 || r === 1 || r === 7 || r === 30 || r === 90)) rangeDays = r;
    } catch (e) { /* none */ }
    var root = $('#rangeSelect'), btn = $('.select-btn', root), menu = $('.select-menu', root);
    function label() {
      var li = $('li[data-value="' + rangeDays + '"]', menu);
      setText($('.select-value', root), li ? li.textContent : 'Last 7 days');
      $all('li', menu).forEach(function (x) { x.setAttribute('aria-selected', x === li ? 'true' : 'false'); });
    }
    function open(v) {
      menu.hidden = !v;
      btn.setAttribute('aria-expanded', v ? 'true' : 'false');
      if (v) { var sel = $('li[aria-selected="true"]', menu) || menu.firstElementChild; sel.focus(); }
    }
    function pick(li) {
      rangeDays = +li.getAttribute('data-value');
      try { localStorage.setItem('gp.range', String(rangeDays)); } catch (e) { /* none */ }
      label(); open(false); btn.focus();
      chartSig = ''; renderLatencyChart();
    }
    btn.addEventListener('click', function () { open(menu.hidden); });
    menu.addEventListener('click', function (e) { var li = e.target.closest('li'); if (li) pick(li); });
    menu.addEventListener('keydown', function (e) {
      var items = $all('li', menu), i = items.indexOf(document.activeElement);
      if (e.key === 'ArrowDown') { items[Math.min(items.length - 1, i + 1)].focus(); e.preventDefault(); }
      else if (e.key === 'ArrowUp') { items[Math.max(0, i - 1)].focus(); e.preventDefault(); }
      else if (e.key === 'Enter' || e.key === ' ') { if (i >= 0) pick(items[i]); e.preventDefault(); }
      else if (e.key === 'Escape' || e.key === 'Tab') { open(false); if (e.key === 'Escape') btn.focus(); }
    });
    document.addEventListener('click', function (e) { if (!root.contains(e.target)) open(false); });
    label();
  }

  // ---------- Top bar ----------
  function toneClass(t) { return /^(good|warn|bad)$/.test(t) ? 'tone-' + t : ''; }
  function setTone(node, base, tone) { node.className = base + (toneClass(tone) ? ' ' + toneClass(tone) : ''); }

  function nextDueMs() {
    var d = parseIso(S.probe.nextDueUtc);
    return d ? d.getTime() - Date.now() : null;
  }
  // The time left, like "8:38". Null when paused, testing, or no time is known yet.
  function nextLeftText() {
    if (S.probe.paused || S.probe.testRunning) return null;
    var left = nextDueMs();
    return left === null ? null : countdownText(left);
  }
  // The KPI value. A bare "8:38" reads like a clock time, so the countdown gets "in".
  function nextText() {
    if (S.probe.paused) return 'Paused';
    if (S.probe.testRunning) return 'Testing now';
    if (S.probe.noMoreTests) return 'None';
    var left = nextLeftText();
    if (left === null) return 'Soon';
    return left === 'due now' ? 'Due now' : 'in ' + left;
  }
  function clockAt(ms) {
    var d = new Date(Date.now() + ms);
    return pad(d.getHours()) + ':' + pad(d.getMinutes());
  }

  // The host sends a bare status word ("On", "Paused", "Blocked"). Name the subject.
  function logChipText(chip, paused) {
    chip = str(chip);
    if (!chip) return paused ? 'Logging · paused' : 'Logging';
    return /^(on|off|saved|paused|blocked|waiting)$/i.test(chip) ? 'Logging · ' + chip.toLowerCase() : chip;
  }

  function renderTopbar() {
    var p = S.probe;
    var chipLog = $('#chipLog');
    setTone(chipLog, 'chip', S.log.chipTone);
    setText($('.chip-text', chipLog), logChipText(S.log.chip, p.paused));
    chipLog.title = S.log.status || 'Log status';

    var phase = $('#chipPhase');
    if (p.testRunning) {
      phase.hidden = false; phase.className = 'chip chip-accent';
      setText($('.chip-text', phase), p.phase || 'Testing');
      $('.spinner', phase).hidden = false;
    } else if (p.paused) {
      phase.hidden = false; phase.className = 'chip tone-warn';
      setText($('.chip-text', phase), 'Paused');
      $('.spinner', phase).hidden = true;
    } else phase.hidden = true;

    $('#chipNext').hidden = p.testRunning || p.paused;
    tickTopbar();

    $all('#btnRunTop, #btnRunRoute').forEach(function (b) {
      // The one Dev probe is spent: the button stays off, and its title says why.
      b.disabled = p.testRunning || !!p.noMoreTests;
      b.title = p.noMoreTests || '';
      setText($('span', b), p.testRunning ? 'Testing…' : 'Run test now');
    });
    renderUpdateCtl();
    $('#demoBadge').hidden = !S.app.demo;
    setText($('#sideVersion'), 'v' + S.app.version + (S.app.profile === 'Dev' ? ' · Dev' : ''));
  }
  function tickTopbar() {
    var left = nextLeftText();
    setText($('#chipNext .chip-text'), S.probe.noMoreTests ? 'No more tests in this Dev run'
      : left === null ? 'Next test soon' : left === 'due now' ? 'Next test due now' : 'Next test in ' + left);
  }

  var updSig = '';
  function renderUpdateCtl() {
    var u = S.update, box = $('#updateCtl');
    var sig = u.state + '|' + u.available + '|' + (u.state === 'downloading' ? Math.round((u.progress || 0) * 100) : '');
    if (sig === updSig) return;
    var prevState = updSig.split('|')[0];
    updSig = sig;
    if (u.state === 'downloading' && prevState === 'downloading') {
      var bar = $('.bar', box);
      if (bar) {
        var c = 2 * Math.PI * 14;
        bar.setAttribute('stroke-dashoffset', String(c * (1 - clamp01(u.progress))));
        box.firstChild.title = 'Downloading update ' + Math.round(clamp01(u.progress) * 100) + '%';
        box.firstChild.setAttribute('aria-label', box.firstChild.title);
        return;
      }
    }
    clear(box);
    if (u.state === 'checking') {
      box.appendChild(h('span', { class: 'upd-check', title: 'Checking for updates', role: 'status', 'aria-label': 'Checking for updates' }, h('span', { class: 'spinner', 'aria-hidden': 'true' })));
    } else if (u.state === 'downloading') {
      var circ = 2 * Math.PI * 14, p = clamp01(u.progress);
      var ring = h('span', { class: 'upd-ring', role: 'progressbar', 'aria-valuemin': '0', 'aria-valuemax': '100', 'aria-valuenow': String(Math.round(p * 100)), title: 'Downloading update ' + Math.round(p * 100) + '%', 'aria-label': 'Downloading update ' + Math.round(p * 100) + '%' });
      ring.appendChild(s('svg', { class: 'ring', viewBox: '0 0 34 34', 'aria-hidden': 'true' },
        s('circle', { class: 'track', cx: 17, cy: 17, r: 14, fill: 'none', 'stroke-width': 3 }),
        s('circle', { class: 'bar', cx: 17, cy: 17, r: 14, fill: 'none', 'stroke-width': 3, 'stroke-linecap': 'round', 'stroke-dasharray': String(circ), 'stroke-dashoffset': String(circ * (1 - p)) })));
      ring.appendChild(s('svg', { class: 'arrow ico', viewBox: '0 0 24 24', 'aria-hidden': 'true' }, s('path', { d: 'M12 5v12M7 12l5 5 5-5' })));
      box.appendChild(ring);
    } else if (u.state === 'ready' || u.state === 'installing') {
      var installing = u.state === 'installing';
      var pill = h('button', { type: 'button', class: 'upd-pill', disabled: installing, 'aria-label': installing ? 'Installing update' : 'Restart to update' + (u.available ? ' to version ' + u.available : '') },
        installing ? h('span', { class: 'spinner', 'aria-hidden': 'true' }) : s('svg', { class: 'ico', viewBox: '0 0 24 24', 'aria-hidden': 'true' }, s('path', { d: 'M12 4v11M7 10l5 5 5-5M5 20h14' })),
        h('span', { text: installing ? 'Installing…' : 'Restart to update' }),
        !installing && u.available ? h('span', { class: 'ver', text: 'v' + u.available }) : null);
      pill.addEventListener('click', function () { send({ cmd: 'installUpdate' }); });
      box.appendChild(pill);
    }
  }
  function clamp01(v) { return isNum(v) ? Math.max(0, Math.min(1, v)) : 0; }

  // ---------- Overview: KPIs ----------
  function figureNode(fig) {
    var m = /^(-?\d+(?:\.\d+)?)\s*ms$/.exec(fig);
    if (m) return [m[1], h('small', { text: 'ms' })];
    return fig;
  }
  function kpi(id, valueNodes, valueClass, subNodes) {
    var k = $('#' + id), v = $('.kpi-value', k), sub = $('.kpi-sub', k);
    v.className = 'kpi-value' + (valueClass ? ' ' + valueClass : '');
    clear(v); append(v, valueNodes);
    clear(sub); append(sub, subNodes);
  }
  function isNoReply(fig) { return /no reply/i.test(fig); }

  function renderKpis() {
    var p = S.probe;
    var lastFig = p.lastHop.figure;
    kpi('kpiLast', figureNode(lastFig), isNoReply(lastFig) ? 'muted' : (lastFig === '—' ? 'muted' : ''),
      p.handoffCityName || p.handoffNetwork ? [handoffNetworkText(), p.handoffCityName].filter(Boolean).join(' · ') : 'Last responding hop');
    var riotFig = p.riot.figure;
    // The figure was measured to the shown test's target, so name that address.
    var tested = testedTarget();
    var riotIp = S.region.lastTestTarget || S.region.target.ip;
    kpi('kpiRiot', figureNode(riotFig), isNoReply(riotFig) || riotFig === '—' ? 'muted' : '',
      riotIp ? riotIp + ' · AS6507' : 'AS6507');
    var riotTip = [(tested ? tested.label : S.region.target.label) + (riotIp ? ' at ' + riotIp + '.' : '.'),
      tested ? 'The next test traces ' + S.region.target.ip + '.' : '',
      isNoReply(riotFig) ? 'Riot often does not answer ping.' : '', latencyCaveat()].filter(Boolean).join(' ');
    $('#kpiRiot').title = riotTip;
    var loss = p.lastHop.lossPct;
    kpi('kpiLoss', isNum(loss) ? [String(Math.round(loss * 10) / 10), h('small', { text: '%' })] : '—',
      !isNum(loss) ? 'muted' : loss === 0 ? 'good' : loss < 10 ? 'warn' : 'bad',
      isNum(p.riot.lossPct) ? 'Last hop · Riot ' + pct(p.riot.lossPct) : 'Last hop');
    // In a match: League's own in-game ping (its netlog) and the server the game connected to (its match log).
    var gp = p.leagueRunning ? p.gamePing : null;
    kpi('kpiLeague',
      gp ? Math.round(gp.ms) + ' ms' : p.leagueRunning ? (p.matchServer ? 'In a match' : 'Running') : 'Not running',
      'text' + (p.leagueRunning ? ' good' : ''),
      gp
        ? 'In-game ping (League)' + (p.matchServer ? ' · server ' + p.matchServer : '')
        : p.leagueRunning && p.matchServer
          ? 'Game server ' + p.matchServer + (p.matchSameAsTarget ? ' · the test target' : ' · not the test target')
          : p.mode || (p.leagueRunning ? 'Tests every 5 min' : 'Tests every 15 min'));
    // The link to the comparison shows only when there is a measured figure to compare.
    var pel = $('#pingExplainLink');
    if (pel) pel.parentNode.hidden = !(p.hasRoute && (isNum(p.lastHop.avgMs) || isNum(p.riot.avgMs)));
    var kl = $('#kpiLeague');
    if (kl) kl.title = gp
      ? 'League’s own network log: ping ' + Math.round(gp.ms) + ' ms' + (isNum(gp.lossPct) ? ', loss ' + gp.lossPct + '%' : '')
        + (isNum(gp.jitterMs) ? ', jitter ' + gp.jitterMs + ' ms' : '') + (gp.at ? ', at ' + hhmm(gp.at) : '') + '.'
      : '';
    var adapterSub = [];
    if (p.adapter || p.vpn) adapterSub.push(h('span', { class: 'badge ' + (p.vpn ? 'warn' : ''), text: p.vpn ? 'VPN ON' : 'NO VPN' }));
    if (p.wifi) adapterSub.push(h('span', { text: p.wifi }));
    kpi('kpiAdapter', p.adapter || '—', 'text' + (p.adapter ? '' : ' muted'), adapterSub);
    tickKpiNext(true);
  }
  function tickKpiNext(full) {
    var p = S.probe, k = $('#kpiNext'), v = $('.kpi-value', k);
    var txt = nextText(), left = nextLeftText();
    var counting = left !== null && left !== 'due now';
    var cls = 'kpi-value' + (p.paused ? ' warn text' : p.testRunning || !counting ? ' text' : '');
    if (v.className !== cls) v.className = cls;
    if (counting) {
      // "in" is the small unit; the digits stay big.
      var num = $('.kpi-num', v);
      if (!num) { clear(v); v.appendChild(h('small', { class: 'pre', text: 'in' })); num = h('span', { class: 'kpi-num' }); v.appendChild(num); }
      setText(num, left);
    } else setText(v, txt);
    var subText = p.testRunning ? (p.phase || 'Tracing route') : p.paused ? 'Logging is paused'
      : p.noMoreTests ? 'No more tests in this Dev run'
      : counting ? (p.phase || 'Waiting') + ' · at ' + clockAt(nextDueMs())
      : left === null ? 'Starting soon' : (p.phase || 'Waiting');
    var sub = $('.kpi-sub', k);
    if (full || sub.textContent !== subText) { clear(sub); append(sub, subText); }
    // thin progress bar along the tile bottom
    var bar = $('.bar-mini', k);
    if (!bar) { bar = h('div', { class: 'bar-mini' }, h('i')); k.appendChild(bar); }
    var left = nextDueMs(), interval = (p.leagueRunning ? 5 : 15) * 60000;
    var frac = left === null || p.paused || p.testRunning ? 0 : 1 - Math.max(0, Math.min(1, left / interval));
    bar.firstChild.style.transform = 'scaleX(' + frac.toFixed(4) + ')';
  }

  // ---------- Overview: map ----------
  var MAP = window.GECKO_MAP || { w: 2000, h: 892, scale: 370, tx: 1000, ty: 510, countries: [] };
  function project(lat, lon) {
    var lambda = lon * Math.PI / 180, phi = lat * Math.PI / 180;
    var phi2 = phi * phi, phi4 = phi2 * phi2;
    var x = lambda * (0.8707 - 0.131979 * phi2 + phi4 * (-0.013791 + phi4 * (0.003971 * phi2 - 0.001529 * phi4)));
    var y = phi * (1.007226 + phi2 * (0.015085 + phi4 * (-0.044475 + 0.028874 * phi2 - 0.005916 * phi4)));
    return [MAP.tx + MAP.scale * x, MAP.ty - MAP.scale * y];
  }

  var LAND = ['#3a1d0e', '#43210f', '#4d2510', '#572a11', '#622f13', '#6d3313', '#7a3814'];
  // ISO 3166 alpha-2 to the numeric ids in map-data.js, for the country that Cloudflare sees.
  var COUNTRY_NUM = {
    US: '840', CA: '124', MX: '484', BR: '076', AR: '032', CL: '152', CO: '170', PE: '604', GB: '826', IE: '372',
    FR: '250', DE: '276', NL: '528', BE: '056', ES: '724', PT: '620', IT: '380', SE: '752', NO: '578', FI: '246',
    DK: '208', PL: '616', CZ: '203', AT: '040', CH: '756', RO: '642', GR: '300', TR: '792', RU: '643', UA: '804',
    JP: '392', KR: '410', TW: '158', VN: '704', SG: '702', PH: '608', TH: '764', MY: '458', ID: '360', AU: '036',
    NZ: '554', IN: '356', AE: '784', SA: '682', EG: '818', ZA: '710'
  };
  function landFill(id) {
    var n = 0; for (var i = 0; i < id.length; i++) n = (n * 31 + id.charCodeAt(i)) % 997;
    return LAND[n % LAND.length];
  }

  var mapView = (function () {
    var host, landSvg, landG, overlay, tip, view = { cx: 0, cy: 0, k: 1 }, size = { w: 1, h: 1 }, userMoved = false;
    var lit = '';
    var KMIN = 1, KMAX = 9;
    var markers = [];

    function build() {
      host = $('#map'); tip = $('#mapTip');
      landSvg = s('svg', { class: 'land-svg', 'aria-hidden': 'true', preserveAspectRatio: 'xMidYMid meet' });
      var g = s('g', { class: 'land' });
      MAP.countries.forEach(function (c) {
        g.appendChild(s('path', { d: c[2], fill: landFill(c[0]), 'data-name': c[1], 'data-id': c[0] }));
      });
      landSvg.appendChild(g);
      landG = g;
      overlay = s('svg', { class: 'overlay', style: 'position:absolute;inset:0;pointer-events:none' });
      host.appendChild(landSvg);
      host.appendChild(overlay);

      g.addEventListener('pointermove', function (e) {
        if (dragging) return;
        var name = e.target.getAttribute && e.target.getAttribute('data-name');
        if (name) showTip(e.clientX, e.clientY, name, '');
      });
      g.addEventListener('pointerleave', hideTip);

      // drag to pan
      var dragging = false, start = null;
      host.addEventListener('pointerdown', function (e) {
        if (e.button !== 0) return;
        dragging = true; start = { x: e.clientX, y: e.clientY, cx: view.cx, cy: view.cy };
        host.setPointerCapture(e.pointerId); host.classList.add('dragging'); hideTip();
      });
      host.addEventListener('pointermove', function (e) {
        if (!dragging) return;
        var u = unit();
        view.cx = start.cx - (e.clientX - start.x) * u;
        view.cy = start.cy - (e.clientY - start.y) * u;
        userMoved = true; apply();
      });
      function end() { dragging = false; host.classList.remove('dragging'); }
      host.addEventListener('pointerup', end);
      host.addEventListener('pointercancel', end);

      var hintTimer = 0;
      host.addEventListener('wheel', function (e) {
        if (!e.ctrlKey) {
          var hint = $('#mapHint'); hint.classList.add('show');
          clearTimeout(hintTimer); hintTimer = setTimeout(function () { hint.classList.remove('show'); }, 1200);
          return;
        }
        e.preventDefault();
        var r = host.getBoundingClientRect();
        zoomAt(e.deltaY < 0 ? 1.25 : 0.8, e.clientX - r.left, e.clientY - r.top);
      }, { passive: false });

      $('#zoomIn').addEventListener('click', function () { zoomAt(1.5); });
      $('#zoomOut').addEventListener('click', function () { zoomAt(1 / 1.5); });
      $('#zoomReset').addEventListener('click', function () { userMoved = false; fit(true); });

      // The info card can fold to its title, so it does not cover the map. The choice is remembered.
      var infoCard = $('#mapInfo'), infoToggle = $('#mapInfoToggle');
      function setInfoCollapsed(on) {
        infoCard.classList.toggle('collapsed', on);
        infoToggle.setAttribute('aria-expanded', on ? 'false' : 'true');
        var label = on ? 'Show details' : 'Hide details';
        infoToggle.setAttribute('aria-label', label); infoToggle.title = label;
      }
      var infoSaved = null;
      try { infoSaved = localStorage.getItem('gp.mapInfo'); } catch (e) { infoSaved = null; }
      setInfoCollapsed(infoSaved === 'collapsed');
      infoToggle.addEventListener('click', function () {
        var on = !infoCard.classList.contains('collapsed');
        setInfoCollapsed(on);
        try { if (on) localStorage.setItem('gp.mapInfo', 'collapsed'); else localStorage.removeItem('gp.mapInfo'); } catch (e) { /* no storage */ }
        fit(false);
      });
      host.addEventListener('keydown', function (e) {
        var step = 60 * unit();
        if (e.key === '+' || e.key === '=') zoomAt(1.5);
        else if (e.key === '-') zoomAt(1 / 1.5);
        else if (e.key === 'ArrowLeft') { view.cx -= step; apply(); }
        else if (e.key === 'ArrowRight') { view.cx += step; apply(); }
        else if (e.key === 'ArrowUp') { view.cy -= step; apply(); }
        else if (e.key === 'ArrowDown') { view.cy += step; apply(); }
        else return;
        userMoved = true; e.preventDefault();
      });
      host.tabIndex = 0;

      if (window.ResizeObserver) new ResizeObserver(function () { fit(false); }).observe(host);
    }

    function unit() { return (MAP.w / view.k) / size.w; } // map units per css px
    function measure() {
      var r = host.getBoundingClientRect();
      size.w = Math.max(1, r.width); size.h = Math.max(1, r.height);
    }
    // Brightens the country that Cloudflare sees you in. Unknown country: no highlight.
    function highlight(alpha2) {
      var id = COUNTRY_NUM[str(alpha2).toUpperCase()] || '';
      if (!landG || id === lit) return;
      $all('path', landG).forEach(function (p) {
        var pid = p.getAttribute('data-id');
        if (pid === lit) p.setAttribute('fill', landFill(pid));
        if (pid === id) p.setAttribute('fill', '#c2410c');
      });
      lit = id;
    }
    function homeBox() {
      // The route points. In the Americas (or with no points) North America frames them.
      var pts = [];
      markers.forEach(function (m) { pts.push([m.lat, m.lon]); });
      var americas = pts.every(function (p) { return p[1] < -30; });
      if (americas) pts.push([64, -140], [64, -58], [20, -128], [20, -68]);
      else {
        var la = pts.map(function (p) { return p[0]; }), lo = pts.map(function (p) { return p[1]; });
        var la0 = Math.min.apply(null, la), la1 = Math.max.apply(null, la), lo0 = Math.min.apply(null, lo), lo1 = Math.max.apply(null, lo);
        var padLat = Math.max(8, (la1 - la0) * 0.2), padLon = Math.max(20, (lo1 - lo0) * 0.22);
        pts.push([Math.min(80, la1 + padLat), lo0 - padLon], [Math.max(-60, la0 - padLat), lo1 + padLon]);
      }
      var xs = [], ys = [];
      pts.forEach(function (p) { var q = project(p[0], p[1]); xs.push(q[0]); ys.push(q[1]); });
      return { x0: Math.min.apply(null, xs), x1: Math.max.apply(null, xs), y0: Math.min.apply(null, ys), y1: Math.max.apply(null, ys) };
    }
    function fit(force) {
      if (!host) return;
      measure();
      if (userMoved && !force) { apply(); return; }
      var b = homeBox(), bw = b.x1 - b.x0, bh = b.y1 - b.y0;
      var aspect = size.h / size.w;
      // Keep the route clear of the info card. Wide map: the card is top-left, so the route goes
      // either to its right or below it, whichever leaves the route bigger. Narrow map: the card
      // is along the bottom, so the route goes above it.
      var info = $('#mapInfo'), wide = size.w > 760;
      var iw = info ? info.offsetWidth + 28 : 0, ih = info ? info.offsetHeight + 20 : 0;
      function kFor(aw, ah) { return Math.min((aw / size.w) * MAP.w / (bw * 1.06), (MAP.w * (ah / size.w)) / (bh * 1.08)); }
      var shiftX = 0, shiftY = 0, k;
      if (!wide) {
        var availB = Math.max(size.h * 0.5, size.h - ih);
        k = kFor(size.w, availB); shiftY = (size.h - availB) / 2;          // route moves up
      } else {
        var availT = Math.max(size.h * 0.5, size.h - ih);
        var kSide = kFor(size.w - iw, size.h), kTop = kFor(size.w, availT);
        if (kSide >= kTop) { k = kSide; shiftX = -iw / 2; }                // route moves right
        else { k = kTop; shiftY = -(size.h - availT) / 2; }                // route moves down
      }
      view.k = Math.max(KMIN, Math.min(KMAX, k));
      var u = (MAP.w / view.k) / size.w;
      view.cx = (b.x0 + b.x1) / 2 + shiftX * u; view.cy = (b.y0 + b.y1) / 2 + shiftY * u;
      apply();
    }
    function zoomAt(f, px, py) {
      measure();
      if (px === undefined) { px = size.w / 2; py = size.h / 2; }
      var u = unit();
      var mx = view.cx + (px - size.w / 2) * u, my = view.cy + (py - size.h / 2) * u;
      var nk = Math.max(KMIN, Math.min(KMAX, view.k * f));
      var nu = (MAP.w / nk) / size.w;
      view.k = nk;
      view.cx = mx - (px - size.w / 2) * nu; view.cy = my - (py - size.h / 2) * nu;
      userMoved = true; apply();
    }
    function apply() {
      var vw = MAP.w / view.k, vh = vw * size.h / size.w;
      // keep the map in view
      view.cx = Math.max(vw / 2 - vw * 0.3, Math.min(MAP.w - vw / 2 + vw * 0.3, view.cx));
      if (vh < MAP.h) view.cy = Math.max(vh / 2, Math.min(MAP.h - vh / 2, view.cy));
      else view.cy = MAP.h / 2;
      landSvg.setAttribute('viewBox', [(view.cx - vw / 2).toFixed(2), (view.cy - vh / 2).toFixed(2), vw.toFixed(2), vh.toFixed(2)].join(' '));
      $('#zoomIn').disabled = view.k >= KMAX - 1e-6;
      $('#zoomOut').disabled = view.k <= KMIN + 1e-6;
      drawOverlay();
    }
    function toScreen(lat, lon) {
      var q = project(lat, lon), u = unit();
      return [(q[0] - view.cx) / u + size.w / 2, (q[1] - view.cy) / u + size.h / 2];
    }
    function arcPath(a, b) {
      var dx = b[0] - a[0], dy = b[1] - a[1], d = Math.sqrt(dx * dx + dy * dy);
      var mx = (a[0] + b[0]) / 2, my = (a[1] + b[1]) / 2;
      var nx = -dy / (d || 1), ny = dx / (d || 1);
      if (ny > 0) { nx = -nx; ny = -ny; } // bow upward
      var bend = Math.min(140, d * 0.22);
      return 'M' + a[0].toFixed(1) + ' ' + a[1].toFixed(1) + 'Q' + (mx + nx * bend).toFixed(1) + ' ' + (my + ny * bend).toFixed(1) + ' ' + b[0].toFixed(1) + ' ' + b[1].toFixed(1);
    }
    function drawOverlay() {
      clear(overlay);
      var byKey = {};
      markers.forEach(function (m) { m.pt = toScreen(m.lat, m.lon); byKey[m.key] = m; });
      var arcs = s('g', {});
      var you = byKey.you, edge = byKey.edge, hand = byKey.hand, riot = byKey.riot;
      if (you && edge && dist(you.pt, edge.pt) > 6) arcs.appendChild(s('path', { class: 'arc arc-edge', d: arcPath(you.pt, edge.pt) }));
      // A handoff in the Riot server city shares the Riot dot: one ringed dot, one label.
      var merged = !!(hand && riot && dist(hand.pt, riot.pt) < 14);
      var chain = [you, merged ? null : hand, riot].filter(Boolean);
      for (var i = 0; i + 1 < chain.length; i++) {
        if (dist(chain[i].pt, chain[i + 1].pt) < 4) continue;
        var d = arcPath(chain[i].pt, chain[i + 1].pt);
        arcs.appendChild(s('path', { class: 'arc arc-glow', d: d }));
        arcs.appendChild(s('path', { class: 'arc arc-main', d: d }));
      }
      overlay.appendChild(arcs);

      // An edge dot close to the handoff dot shares one label: "Seattle · PDX edge".
      var labels = {};
      markers.forEach(function (m) { labels[m.key] = m.label; });
      if (edge && hand && dist(edge.pt, hand.pt) < 40) {
        labels.hand = hand.label + ' · ' + edge.label + ' edge';
        labels.edge = '';
      }
      // Boxes [x0, y0, x1, y1] that a label must not cover: every dot first, then each placed label.
      var boxes = markers.map(function (m) { return [m.pt[0] - 12, m.pt[1] - 12, m.pt[0] + 12, m.pt[1] + 12]; });
      function hits(b) {
        return boxes.some(function (o) { return b[0] < o[2] && b[2] > o[0] && b[1] < o[3] && b[3] > o[1]; });
      }
      function placeLabel(m, text) {
        var w = text.length * 7 + 6, x = m.pt[0], y = m.pt[1];
        // [lx, ly (baseline), anchor]; above, below, right, left.
        var tries = [[0, -16, 'middle'], [0, 26, 'middle'], [16, 4, 'start'], [-16, 4, 'end']];
        if (x > size.w - 70) tries = [[-16, 4, 'end'], [0, -16, 'middle'], [0, 26, 'middle']];
        else if (x < 70) tries = [[16, 4, 'start'], [0, -16, 'middle'], [0, 26, 'middle']];
        function box(t) {
          var x0 = t[2] === 'middle' ? x + t[0] - w / 2 : t[2] === 'start' ? x + t[0] : x + t[0] - w;
          return [x0, y + t[1] - 12, x0 + w, y + t[1] + 3];
        }
        var pick = tries[0];
        // A label must stay inside the map and clear of the zoom buttons (top right).
        function outside(b) { return b[0] < 6 || b[2] > size.w - 6 || b[1] < 6 || b[3] > size.h - 6 || (b[2] > size.w - 64 && b[1] < 150); }
        for (var i = 0; i < tries.length; i++) { if (!hits(box(tries[i])) && !outside(box(tries[i]))) { pick = tries[i]; break; } }
        if (outside(box(pick))) {
          var left = [-16, 4, 'end'], right = [16, 4, 'start'];
          pick = !outside(box(left)) ? left : !outside(box(right)) ? right : pick;
        }
        boxes.push(box(pick));
        return pick;
      }

      markers.forEach(function (m) {
        if (merged && m.key === 'hand') return;
        var title = m.title, sub = m.sub;
        if (merged && m.key === 'riot') { title = m.title; sub = hand.title + ' · ' + m.sub; }
        var g = s('g', { class: 'mk mk-' + m.key + (merged && m.key === 'riot' ? ' merged' : ''), transform: 'translate(' + m.pt[0].toFixed(1) + ' ' + m.pt[1].toFixed(1) + ')', style: 'pointer-events:auto' });
        if (m.key === 'you') g.appendChild(s('circle', { class: 'pulse', r: 9 }));
        g.appendChild(s('circle', { class: 'halo', r: 11 }));
        g.appendChild(s('circle', { class: 'core', r: m.key === 'hand' ? 5.5 : 6 }));
        var text = labels[m.key];
        if (text) {
          var at = placeLabel(m, text);
          g.appendChild(s('text', { class: 'mk-label', x: at[0], y: at[1], 'text-anchor': at[2], text: text }));
        }
        g.addEventListener('pointerenter', function (e) { showTip(e.clientX, e.clientY, title, sub); });
        g.addEventListener('pointermove', function (e) { showTip(e.clientX, e.clientY, title, sub); });
        g.addEventListener('pointerleave', hideTip);
        overlay.appendChild(g);
      });
    }
    function dist(a, b) { var x = a[0] - b[0], y = a[1] - b[1]; return Math.sqrt(x * x + y * y); }
    function showTip(cx, cy, title, sub) {
      var r = host.parentNode.getBoundingClientRect();
      clear(tip);
      tip.appendChild(h('div', { class: 't', text: title }));
      if (sub) tip.appendChild(h('div', { class: 's', text: sub }));
      tip.hidden = false;
      var x = cx - r.left + 14, y = cy - r.top + 14;
      var tw = tip.offsetWidth, th = tip.offsetHeight;
      if (x + tw > r.width - 8) x = cx - r.left - tw - 14;
      if (y + th > r.height - 8) y = cy - r.top - th - 14;
      tip.style.left = Math.max(8, x) + 'px'; tip.style.top = Math.max(8, y) + 'px';
    }
    function hideTip() { if (tip) tip.hidden = true; }

    var sig = '';
    function setMarkers(list) {
      var nsig = JSON.stringify(list.map(function (m) { return [m.key, m.lat, m.lon, m.label, m.title, m.sub]; }));
      if (nsig === sig) return;
      var first = sig === '';
      sig = nsig; markers = list;
      if (first || !userMoved) fit(false); else drawOverlay();
    }
    return { build: build, fit: fit, setMarkers: setMarkers, highlight: highlight };
  })();

  // "HE → Riot Direct · Chicago". One builder for the probe and for history rows, so the
  // subtitle, the map card and the history table say the same thing.
  function routeText(r, withSource) {
    var city = r.handoffCityName || (r.handoffCity ? cityName(r.handoffCity) : '');
    var far = [r.handoffNetwork, city].filter(Boolean).join(' · ');
    var up = (r.upstream || 'Unknown') + (withSource && r.upstreamSource === 'asn' ? ' (named by ASN)' : '');
    return up + (far ? ' → ' + far : '');
  }

  function renderMap() {
    var p = S.probe, r = S.region, edge = S.cloud.edge ? obj(S.cloud.edge) : null;
    var list = [];
    var known = !!edge && isNum(edge.lat) && isNum(edge.lon);
    var edgeCity = edge ? str(edge.city) : '';
    var isp = edge ? str(edge.asOrg) : '';
    // No edge position: no "You" dot. A guessed dot would point at the wrong place.
    if (known) {
      list.push({ key: 'you', lat: edge.lat, lon: edge.lon, label: 'You', title: edgeCity || 'You',
        sub: isp ? 'On ' + isp : 'Your location, as seen by Cloudflare' });
    }
    // The Cloudflare datacenter is not on the route to Riot, so it is not plotted.
    var hp = isNum(p.handoffLat) && isNum(p.handoffLon) ? [p.handoffCityName || cityName(p.handoffCity), p.handoffLat, p.handoffLon] : place(p.handoffCity);
    if (p.hasRoute && hp) {
      var hname = p.handoffCityName || hp[0];
      list.push({ key: 'hand', lat: hp[1], lon: hp[2], label: hname,
        title: 'Handoff · ' + [p.handoffNetwork, hname].filter(Boolean).join(' in '),
        sub: [p.upstream, p.handoffNetwork].filter(Boolean).join(' → ') || 'Where your ISP hands traffic to the next network' });
    }
    var hasRiot = isNum(r.lat) && isNum(r.lon) && !!r.serverCity;
    if (hasRiot) {
      list.push({ key: 'riot', lat: r.lat, lon: r.lon, label: serversLabel(), title: serversLabel(), sub: 'Trace target: ' + targetText() });
    }
    $('#lgEdge').hidden = !list.some(function (m) { return m.key === 'edge'; });
    $('#lgRiot').hidden = !hasRiot;
    $('#lgYou').hidden = !known;
    setText($('#lgRiot .lg-text'), riotShort() + ' servers');
    mapView.highlight(edge ? edge.country : '');

    // info card, like Radar's "connected to the Seattle (SEA) datacenter"
    var info = $('#mapInfo');
    setText($('.map-info-title', info), known ? (edgeCity || 'Your connection') : 'Your connection');
    var t = clear($('.map-info-text', info));
    if (known && isp) {
      t.appendChild(document.createTextNode('You are on '));
      t.appendChild(h('b', { text: isp }));
      if (isNum(edge.asn)) t.appendChild(document.createTextNode(' (AS' + edge.asn + ')'));
      t.appendChild(document.createTextNode('.'));
    } else if (known) {
      t.appendChild(document.createTextNode('Your location, as seen by Cloudflare.'));
    } else {
      t.appendChild(document.createTextNode('Your position is unknown. ' + (!S.cloud.enabled || S.cloud.status === 'off' ? 'Cloudflare live data is off.'
        : S.cloud.status === 'error' ? 'Cloudflare data is not available.' : 'Cloudflare data is loading.')));
    }
    if (p.hasRoute) {
      t.appendChild(h('br'));
      t.appendChild(document.createTextNode('Route '));
      t.appendChild(h('b', { text: routeText(p, true) }));
      t.appendChild(document.createTextNode(' to the ' + r.target.label + '. '));
      t.appendChild(document.createTextNode(hasRiot ? riotShort() + ' servers: ' + serverCityText() + '.' : serversLabel() + '.'));
    } else if (!hasRiot && r.id) {
      t.appendChild(h('br'));
      t.appendChild(document.createTextNode(serversLabel() + '.'));
    }
    var caveat = latencyCaveat();
    if (caveat && p.hasRoute) t.appendChild(h('span', { class: 'map-info-note', text: caveat }));
    mapView.setMarkers(list);
  }

  // ---------- Charts ----------
  var charts = [];
  function niceMax(v) {
    if (!(v > 0)) return 10;
    var p = Math.pow(10, Math.floor(Math.log10(v))), n = v / p;
    var steps = [1, 1.2, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10];
    for (var i = 0; i < steps.length; i++) if (n <= steps[i]) return steps[i] * p;
    return 10 * p;
  }
  function timeTicks(t0, t1, width) {
    var H = 3600000, D = 86400000;
    var steps = [H, 2 * H, 3 * H, 4 * H, 6 * H, 12 * H, D, 2 * D, 3 * D, 5 * D, 7 * D];
    var maxTicks = Math.max(2, Math.floor(width / 96));
    var step = steps[steps.length - 1];
    for (var i = 0; i < steps.length; i++) if ((t1 - t0) / steps[i] <= maxTicks) { step = steps[i]; break; }
    var out = [], d = new Date(t0);
    if (step < D) { d.setMinutes(0, 0, 0); var hrs = step / H; while (d.getHours() % hrs) d.setHours(d.getHours() + 1); }
    else { d.setHours(0, 0, 0, 0); d.setDate(d.getDate() + 1); }
    for (var guard = 0; d.getTime() <= t1 && guard < 60; guard++) {
      if (d.getTime() >= t0) out.push({ t: d.getTime(), label: step < D ? (d.getHours() === 0 ? dayLabelY(d) : hhmm(d)) : (step === D && (t1 - t0) <= 8 * D ? DOW[d.getDay()] + ' ' + d.getDate() : dayLabelY(d)) });
      if (step < D) d = new Date(d.getTime() + step); else d.setDate(d.getDate() + step / D);
    }
    return out;
  }

  // opts: { times:[ms], series:[{name, cls, color, values:[num|null], fmt}], yFmt, area, t0, t1, empty }
  function drawChart(box, opts) {
    box.__opts = opts;
    if (charts.indexOf(box) < 0) {
      charts.push(box);
      if (window.ResizeObserver) new ResizeObserver(function () { if (box.__opts && box.offsetParent) paint(box); }).observe(box);
    }
    paint(box);
  }
  function redrawCharts() { charts.forEach(function (b) { if (b.__opts && b.offsetParent) paint(b); }); }

  function paint(box) {
    var o = box.__opts;
    var W = box.clientWidth, H = box.clientHeight;
    if (W < 10 || H < 10) return;
    if (box.__size === W + 'x' + H + '|' + o.sig) return;
    box.__size = W + 'x' + H + '|' + o.sig;
    clear(box);
    var n = o.times.length;
    var any = o.series.some(function (sr) { return sr.values.some(isNum); });
    if (!n || !any) { box.appendChild(h('div', { class: 'chart-empty', text: o.empty || 'No data yet' })); return; }

    var padL = 44, padR = 12, padT = 12, padB = 28;
    var iw = W - padL - padR, ih = H - padT - padB;
    var t0 = o.t0 !== undefined ? o.t0 : o.times[0], t1 = o.t1 !== undefined ? o.t1 : o.times[n - 1];
    if (t1 <= t0) t1 = t0 + 1;
    var maxV = 0;
    o.series.forEach(function (sr) { sr.values.forEach(function (v) { if (isNum(v) && v > maxV) maxV = v; }); });
    var yMax = o.yMax || niceMax(maxV * 1.12);
    function X(t) { return padL + (t - t0) / (t1 - t0) * iw; }
    function Y(v) { return padT + ih - (v / yMax) * ih; }

    var svg = s('svg', { viewBox: '0 0 ' + W + ' ' + H, role: 'img', 'aria-label': o.label || 'Chart' });
    var defs = s('defs');
    svg.appendChild(defs);
    // grid + y labels
    var g = s('g', { class: 'axis' });
    for (var i = 0; i <= 4; i++) {
      var v = yMax * i / 4, y = Y(v);
      g.appendChild(s('line', { class: 'gridline', x1: padL, x2: W - padR, y1: y.toFixed(1), y2: y.toFixed(1) }));
      g.appendChild(s('text', { x: padL - 8, y: (y + 4).toFixed(1), 'text-anchor': 'end', text: o.yFmt ? o.yFmt(v) : String(Math.round(v)) }));
    }
    timeTicks(t0, t1, iw).forEach(function (tk) {
      var x = X(tk.t);
      if (x < padL + 14 || x > W - padR - 14) return;
      g.appendChild(s('text', { x: x.toFixed(1), y: H - 8, 'text-anchor': 'middle', text: tk.label }));
    });
    svg.appendChild(g);

    var uid = 'g' + Math.random().toString(36).slice(2, 8);
    o.series.forEach(function (sr, si) {
      var gid = uid + si;
      var grad = s('linearGradient', { id: gid, x1: 0, x2: 0, y1: 0, y2: 1 },
        s('stop', { offset: '0%', 'stop-color': sr.color, 'stop-opacity': sr.areaOpacity || .28 }),
        s('stop', { offset: '100%', 'stop-color': sr.color, 'stop-opacity': 0 }));
      defs.appendChild(grad);
      // split into runs at nulls
      var runs = [], cur = [];
      for (var k = 0; k < n; k++) {
        var val = sr.values[k];
        if (isNum(val)) cur.push([X(o.times[k]), Y(val)]);
        else if (cur.length) { runs.push(cur); cur = []; }
      }
      if (cur.length) runs.push(cur);
      runs.forEach(function (run) {
        if (run.length === 1) {
          svg.appendChild(s('circle', { cx: run[0][0].toFixed(1), cy: run[0][1].toFixed(1), r: 1.8, fill: sr.color }));
          return;
        }
        var d = 'M' + run.map(function (p) { return p[0].toFixed(1) + ' ' + p[1].toFixed(1); }).join('L');
        if (o.area !== false) {
          svg.appendChild(s('path', { d: d + 'L' + run[run.length - 1][0].toFixed(1) + ' ' + (padT + ih) + 'L' + run[0][0].toFixed(1) + ' ' + (padT + ih) + 'Z', fill: 'url(#' + gid + ')' }));
        }
        svg.appendChild(s('path', { d: d, class: sr.cls }));
      });
    });

    // hover
    var hover = s('g', { style: 'display:none' });
    var xh = s('line', { class: 'xhair', y1: padT, y2: padT + ih });
    hover.appendChild(xh);
    var dots = o.series.map(function (sr) { var c = s('circle', { class: 'hdot', r: 4.5, fill: sr.color }); hover.appendChild(c); return c; });
    svg.appendChild(hover);
    var catcher = s('rect', { x: padL, y: padT, width: iw, height: ih, fill: 'transparent' });
    svg.appendChild(catcher);
    box.appendChild(svg);
    var tipEl = h('div', { class: 'chart-tip', hidden: true });
    box.appendChild(tipEl);

    function nearest(px) {
      var t = t0 + (px - padL) / iw * (t1 - t0), lo = 0, hi = n - 1;
      while (hi - lo > 1) { var mid = (lo + hi) >> 1; if (o.times[mid] < t) lo = mid; else hi = mid; }
      return Math.abs(o.times[lo] - t) <= Math.abs(o.times[hi] - t) ? lo : hi;
    }
    function move(e) {
      var r = svg.getBoundingClientRect();
      var px = (e.clientX - r.left) * (W / r.width);
      var idx = nearest(px), x = X(o.times[idx]);
      hover.style.display = '';
      xh.setAttribute('x1', x.toFixed(1)); xh.setAttribute('x2', x.toFixed(1));
      o.series.forEach(function (sr, si) {
        var v = sr.values[idx];
        if (isNum(v)) { dots[si].style.display = ''; dots[si].setAttribute('cx', x.toFixed(1)); dots[si].setAttribute('cy', Y(v).toFixed(1)); }
        else dots[si].style.display = 'none';
      });
      clear(tipEl);
      tipEl.appendChild(h('div', { class: 'tt', text: o.tipTitle ? o.tipTitle(o.times[idx], idx) : new Date(o.times[idx]).toLocaleString() }));
      o.series.forEach(function (sr) {
        var v = sr.values[idx];
        tipEl.appendChild(h('div', { class: 'tr' },
          h('span', {}, h('i', { class: 'sw', style: 'background:' + sr.color }), sr.name),
          h('b', { text: sr.fmt ? sr.fmt(v, idx) : (isNum(v) ? String(v) : '—') })));
      });
      tipEl.hidden = false;
      var tw = tipEl.offsetWidth;
      var left = x + 14;
      if (left + tw > W) left = x - tw - 14;
      tipEl.style.left = Math.max(0, left) + 'px';
    }
    catcher.addEventListener('pointermove', move);
    catcher.addEventListener('pointerdown', move);
    catcher.addEventListener('pointerleave', function () { hover.style.display = 'none'; tipEl.hidden = true; });
  }

  // Latency history chart from history[]
  var chartSig = '';
  function renderLatencyChart() {
    var box = $('#latChart');
    // rangeDays 0 = all tests: the chart starts at the first saved test. When the host left the
    // oldest tests out, the chart never starts before the oldest test it has, so an empty stretch
    // is never drawn where tests exist.
    var end = Date.now();
    var first = S.history.length ? S.history[0].d.getTime() : null;
    var cut = historyCut();
    var start = rangeDays > 0 ? end - rangeDays * 86400000 : (first !== null ? Math.min(first, end - 86400000) : end - 86400000);
    var clipped = cut && first !== null && (rangeDays === 0 || start < first);
    if (clipped) start = first;
    var rows = S.history.filter(function (r) { var t = r.d.getTime(); return t >= start && t <= end + 3600000; });
    var last = rows.length ? rows[rows.length - 1].t : '';
    var sig = histVersion + '|' + rangeDays + '|' + rows.length + '|' + last + '|' + S.history.length;
    if (sig === chartSig && box.__opts) return;
    chartSig = sig;
    // bucket so the chart stays light: about one point per 2 px
    var maxPts = Math.max(60, Math.floor((box.clientWidth || 800) / 2));
    var times = [], lastV = [], riotV = [], counts = [];
    // With many tests, one point is the average of a time bucket: the tooltip says so.
    var bucketCounts = null, bucketSpan = 0;
    if (rows.length > maxPts) {
      var span = (end - start) / maxPts;
      for (var b = 0; b < maxPts; b++) { times.push(start + (b + .5) * span); lastV.push([0, 0]); riotV.push([0, 0]); counts.push(0); }
      var soloT = [];
      rows.forEach(function (r) {
        var i = Math.min(maxPts - 1, Math.max(0, Math.floor((r.d.getTime() - start) / span)));
        counts[i]++;
        soloT[i] = r.d.getTime();
        if (isNum(r.lastHopAvg)) { lastV[i][0] += r.lastHopAvg; lastV[i][1]++; }
        if (isNum(r.riotAvg)) { riotV[i][0] += r.riotAvg; riotV[i][1]++; }
      });
      var keep = counts.map(function (c) { return c > 0; });
      // A bucket with one test is drawn and labelled at that test's own time.
      times = times.map(function (t, i) { return counts[i] === 1 ? soloT[i] : t; });
      times = times.filter(function (_, i) { return keep[i]; });
      bucketCounts = counts.filter(function (_, i) { return keep[i]; });
      bucketSpan = span;
      lastV = lastV.filter(function (_, i) { return keep[i]; }).map(function (a) { return a[1] ? a[0] / a[1] : null; });
      riotV = riotV.filter(function (_, i) { return keep[i]; }).map(function (a) { return a[1] ? a[0] / a[1] : null; });
    } else {
      rows.forEach(function (r) { times.push(r.d.getTime()); lastV.push(r.lastHopAvg); riotV.push(r.riotAvg); });
    }
    var label = clipped
      ? 'since ' + dayLabelY(new Date(first)) + ' · ' + (S.historyTotal - S.history.length) + ' older tests are in the log file only'
      : rangeDays === 0 ? (rows.length ? 'since ' + dayLabelY(rows[0].d) : 'all tests')
      : rangeDays === 1 ? 'last 24 hours' : 'last ' + rangeDays + ' days';
    setText($('#chartSub'), !rows.length ? 'Average round trip per test'
      : clipped ? plural(rows.length, 'test', 'tests') + ' · ' + label
      : plural(rows.length, 'test', 'tests') + ' · ' + label);
    drawChart(box, {
      sig: sig + '|' + maxPts, times: times, t0: start, t1: end, label: 'Latency history',
      empty: S.history.length ? 'No tests in the ' + label : 'No tests yet. The first test runs soon.',
      yFmt: function (v) { return Math.round(v) + ''; },
      tipTitle: function (t, idx) {
        if (bucketCounts && bucketCounts[idx] > 1) {
          var a = new Date(t - bucketSpan / 2), b = new Date(t + bucketSpan / 2);
          var same = a.toDateString() === b.toDateString();
          return 'Average of ' + bucketCounts[idx] + ' tests · ' + DOW[a.getDay()] + ' ' + dayLabelY(a) + ', ' + hhmm(a) + ' to '
            + (same ? '' : DOW[b.getDay()] + ' ' + dayLabelY(b) + ', ') + hhmm(b);
        }
        var d = new Date(t); return DOW[d.getDay()] + ' ' + dayLabelY(d) + ', ' + hhmm(d);
      },
      series: [
        { name: 'Last hop', cls: 'line-last', color: '#f6821f', values: lastV, fmt: function (v) { return ms(v); } },
        { name: 'Riot ICMP', cls: 'line-riot', color: '#a78bfa', values: riotV, areaOpacity: .07, fmt: function (v) { return isNum(v) ? ms(v) : 'no reply'; } }
      ]
    });
  }

  // ---------- Overview: pulse ----------
  function evCount(list) { return Array.isArray(list) ? list.length : null; }
  function countChip(n, one, many) {
    var known = isNum(n);
    return h('span', { class: 'count' + (known && n > 0 ? ' hot' : '') }, h('b', { text: known ? String(n) : '–' }), n === 1 ? one : many);
  }
  function cloudAge() {
    var c = S.cloud;
    if (!isNum(c.pulseAgeSec)) return null;
    return c.pulseAgeSec + (Date.now() - receivedAt) / 1000;
  }
  function stateMsg(title, text, button) {
    return h('div', { class: 'state-msg' }, h('strong', { text: title }), text, button ? h('div', { style: 'margin-top:12px' }, button) : null);
  }
  function turnOnButton() {
    return h('button', { type: 'button', class: 'btn', onclick: function () { send({ cmd: 'setSetting', key: 'cloudEnabled', value: true }); } }, h('span', { text: 'Turn on Cloudflare data' }));
  }
  function retryButton() {
    return h('button', { type: 'button', class: 'btn', onclick: function () { send({ cmd: 'refreshCloud' }); } }, h('span', { text: 'Try again' }));
  }

  function renderPulse() {
    var body = clear($('#pulseBody')), c = S.cloud, pulse = c.pulse ? obj(c.pulse) : null;
    var age = cloudAge();
    setText($('#pulseAge'), !c.enabled ? 'Cloudflare Radar · off' : age !== null ? 'Cloudflare Radar · updated ' + agoText(age) : 'Cloudflare Radar');
    if (!c.enabled || c.status === 'off') {
      body.appendChild(stateMsg('Cloudflare live data is off', 'Turn it on to see how Riot, your ISP and your country look right now.', turnOnButton()));
    } else if (!pulse && c.status === 'error') {
      body.appendChild(stateMsg('Cloudflare data is not available', c.error || 'The service did not answer. Gecko Peeks keeps testing your route.', retryButton()));
    } else if (!pulse) {
      body.appendChild(stateMsg('Loading Cloudflare data', 'This takes a few seconds.'));
    } else {
      var riot = obj(pulse.riot), isp = obj(pulse.isp), country = obj(pulse.country);
      var pre = riot.prefix ? obj(riot.prefix) : null;
      if (pulse.radar === false) {
        body.appendChild(stateMsg('Radar data is not available', 'The Gecko Peeks service has no Radar access right now.'));
      }
      if (pre) {
        var rpki = str(pre.rpki) || 'Unknown';
        var tone = /^valid$/i.test(rpki) ? 'good' : /^invalid$/i.test(rpki) ? 'bad' : 'warn';
        body.appendChild(prow('Riot prefix', str(pre.prefix) + ' · AS' + (isNum(pre.origin) ? pre.origin : 6507),
          [h('span', { class: 'badge ' + tone, text: 'RPKI ' + rpki.toUpperCase() })]));
        if (isNum(pre.peers)) {
          var share = isNum(pre.totalPeers) && pre.totalPeers > 0 ? Math.round(pre.peers / pre.totalPeers * 100) + '% of ' + pre.totalPeers + ' peers' : 'route collector peers';
          body.appendChild(prow('Visible to peers', share, String(pre.peers)));
        }
      }
      var evRow = prow('Riot events · AS6507', 'Last 7 days', h('span', { class: 'counts' },
        countChip(evCount(riot.outages), 'outage', 'outages'), countChip(evCount(riot.leaks), 'leak', 'leaks'), countChip(evCount(riot.hijacks), 'hijack', 'hijacks')));
      evRow.classList.add('stack');
      body.appendChild(evRow);
      // null means Radar did not answer: show it as unknown, never as good news
      var ispLists = [isp.outages, isp.anomalies, isp.hijacks, isp.leaks];
      var ispKnown = ispLists.every(Array.isArray), ispAny = ispLists.some(Array.isArray);
      var ispN = ispLists.reduce(function (n, l) { return n + (evCount(l) || 0); }, 0);
      body.appendChild(prow(str(isp.name) || 'Your ISP', isNum(isp.asn) ? 'AS' + isp.asn + ' · last 7 days' : 'last 7 days',
        ispN > 0 ? h('span', { class: 'badge warn', text: plural(ispN, 'event', 'events').toUpperCase() })
          : ispKnown ? h('span', { class: 'badge good', text: 'NO INCIDENTS' })
          : h('span', { class: 'badge', text: ispAny ? 'PARTIAL DATA' : 'UNKNOWN' })));
      var lat = country.iqi && obj(country.iqi).latency ? obj(obj(country.iqi).latency) : null;
      var cOut = evCount(country.outages);
      if (country.code) {
        body.appendChild(prow('Country ' + str(country.code), lat && isNum(lat.p50) ? 'Median latency ' + Math.round(lat.p50) + ' ms' : 'Outages · last 7 days',
          !isNum(cOut) ? h('span', { class: 'badge', text: 'UNKNOWN' })
          : cOut > 0 ? h('span', { class: 'badge warn', text: plural(cOut, 'outage', 'outages').toUpperCase() }) : h('span', { class: 'badge good', text: 'NO OUTAGES' })));
      }
      if (c.status === 'error') body.appendChild(prow('Last refresh failed', c.error || 'Showing older data', ''));
    }
    if (S.probe.changeLine) {
      body.appendChild(h('div', { class: 'change-banner', style: 'margin-top:12px' }, h('span', { text: S.probe.changeLine })));
    }
  }
  function prow(k, small, v) {
    return h('div', { class: 'prow' },
      h('div', { class: 'prow-k' }, k, small ? h('small', { text: small }) : null),
      h('div', { class: 'prow-v' }, v));
  }

  function renderOverview() {
    var p = S.probe;
    setText($('#ovSubtitle'), p.hasRoute ? routeText(p, true) : (hasState ? 'Waiting for the first test' : 'Connecting to Gecko Peeks…'));
    renderKpis();
    renderMap();
    renderLatencyChart();
    renderPulse();
    renderRiotStatus($('#rsOverview'));
  }

  // ---------- Riot service status (Overview and Internet) ----------
  function evTone(e) {
    if (e.type === 'maintenance') return e.status === 'in_progress' ? 'warn' : 'normal';
    return e.severity === 'critical' ? 'bad' : e.severity === 'warning' ? 'warn' : 'normal';
  }
  function evChipText(e) {
    if (e.type === 'maintenance') return e.status === 'in_progress' ? 'In progress' : e.status === 'scheduled' ? 'Scheduled' : '';
    return e.severity ? e.severity.charAt(0).toUpperCase() + e.severity.slice(1) : '';
  }
  function riotEvents(game) {
    game = obj(game);
    var out = [];
    [['incidents', 'incident'], ['maintenances', 'maintenance']].forEach(function (k) {
      arr(game[k[0]]).forEach(function (e) {
        e = obj(e);
        var title = str(e.title).trim();
        if (!title) return;
        out.push({
          type: str(e.type) === 'maintenance' || k[1] === 'maintenance' ? 'maintenance' : 'incident',
          severity: /^(info|warning|critical)$/.test(str(e.severity)) ? str(e.severity) : '',
          status: /^(scheduled|in_progress)$/.test(str(e.status)) ? str(e.status) : '',
          title: title, text: str(e.text), platforms: arr(e.platforms).filter(function (x) { return typeof x === 'string'; }),
          at: parseIso(e.updatedAt) || parseIso(e.createdAt)
        });
      });
    });
    out.sort(function (a, b) { return (b.at ? b.at.getTime() : 0) - (a.at ? a.at.getTime() : 0); });
    return out;
  }
  function riotGame(name, game) {
    var box = h('div', { class: 'rs-game' });
    var head = h('div', { class: 'rs-game-head' });
    var dot = h('i', { class: 'gdot' });
    head.appendChild(h('div', { class: 'rs-game-name' }, dot, name));
    box.appendChild(head);
    if (!game || typeof game !== 'object') {
      head.appendChild(h('span', { class: 'badge', text: 'UNKNOWN' }));
      box.appendChild(h('div', { class: 'rs-none', text: 'Status not available.' }));
      return box;
    }
    var list = riotEvents(game);
    if (!list.length) {
      dot.className = 'gdot good';
      head.appendChild(h('span', { class: 'badge good', text: 'NO ISSUES' }));
      box.appendChild(h('div', { class: 'rs-none', text: 'No incidents or maintenance reported.' }));
      return box;
    }
    var worst = list.some(function (e) { return evTone(e) === 'bad'; }) ? 'bad' : list.some(function (e) { return evTone(e) === 'warn'; }) ? 'warn' : 'normal';
    // Maintenance only and nothing in progress: violet, like the SCHEDULED chip, not the grey "no data" dot.
    dot.className = 'gdot ' + (worst === 'normal' ? 'violet' : worst);
    // The word follows the content: incidents are counted as incidents, maintenance as maintenance.
    var incidents = list.filter(function (e) { return e.type === 'incident'; }).length;
    var badgeText = incidents ? plural(incidents, 'incident', 'incidents')
      + (list.length > incidents ? ' · ' + plural(list.length - incidents, 'maintenance', 'maintenances') : '')
      : list.length === 1 ? 'maintenance' : plural(list.length, 'maintenance', 'maintenances');
    head.appendChild(h('span', { class: 'badge' + (worst === 'normal' ? ' violet' : ' ' + worst), text: badgeText.toUpperCase() }));
    list.slice(0, 6).forEach(function (e) {
      var tone = evTone(e), chipText = evChipText(e);
      var meta = h('div', { class: 'rs-ev-meta' },
        h('span', { class: 'badge', text: e.type === 'maintenance' ? 'MAINTENANCE' : 'INCIDENT' }),
        chipText ? h('span', { class: 'badge' + (tone === 'normal' ? ' violet' : ' ' + tone), text: chipText.toUpperCase() }) : null,
        e.platforms.length ? h('span', { class: 'rs-ev-when', style: 'margin:0', text: e.platforms.slice(0, 4).join(', ') }) : null);
      box.appendChild(h('div', { class: 'rs-ev' },
        h('div', { class: 'rs-ev-title', text: e.title }),
        meta,
        e.text ? h('div', { class: 'rs-ev-text', text: e.text }) : null,
        e.at ? h('div', { class: 'rs-ev-when', text: 'Updated ' + agoText((Date.now() - e.at.getTime()) / 1000) }) : null));
    });
    return box;
  }
  function renderRiotStatus(card) {
    if (!card) return;
    var c = S.cloud, rs = c.riotStatus;
    var sig = [c.enabled, c.status, S.region.shortName, JSON.stringify(rs), Math.floor(Date.now() / 60000)].join('|');
    if (card.__sig === sig) return;
    card.__sig = sig;
    clear(card);
    var age = isNum(c.riotStatusAgeSec) ? c.riotStatusAgeSec + (Date.now() - receivedAt) / 1000 : null;
    var head = h('div', { class: 'card-head' },
      h('div', {}, h('h2', { text: 'Riot service status' }),
        h('p', { class: 'card-sub', text: (S.region.shortName ? S.region.shortName + ' · ' : '') + 'League of Legends and VALORANT' })),
      age !== null && rs ? h('span', { class: 'age', text: 'Updated ' + agoText(age) }) : null);
    card.appendChild(head);
    if (!c.enabled || c.status === 'off') {
      card.appendChild(stateMsg('Cloudflare live data is off', 'Gecko Peeks reads the Riot status feed through its Cloudflare service.', turnOnButton()));
    } else if (!rs) {
      card.appendChild(h('div', { class: 'rs-games' },
        riotGame('League of Legends', null), riotGame('VALORANT', null)));
    } else {
      card.appendChild(h('div', { class: 'rs-games' },
        riotGame('League of Legends', rs.lol), riotGame('VALORANT', rs.valorant)));
    }
    card.appendChild(h('p', { class: 'card-foot', text: 'Source: Riot Games public status feed' }));
  }

  // ---------- Route page ----------
  var hopSig = '';
  // ---------- "How does this compare with my in-game ping?" (Route page) ----------
  // Built from live data only: the region, the traced address, the match server, the last test and,
  // during a match, League's own in-game ping (its netlog on this PC). With no match running the user
  // can type the ping League showed; that number is kept in memory only, never shown as current later.
  var typedGamePing = null, typedInvalid = false;
  var TEST_OLD_MS = 20 * 60000;
  // What the last test measured, and up to which address.
  function measuredPart() {
    var p = S.probe;
    if (!p.hasRoute || !p.testTimestamp) return null;
    var tested = testedTarget();
    var ip = tested ? tested.ip : (S.region.target.ip || '');
    if (isNum(p.riot.avgMs)) return { ms: p.riot.avgMs, atTarget: true, point: ip, regionChanged: !!tested };
    if (isNum(p.lastHop.avgMs)) {
      // The router the saved last-hop figure belongs to (not the live hop list, which can be newer).
      return { ms: p.lastHop.avgMs, atTarget: false, point: p.testLastHopIp || '', regionChanged: !!tested };
    }
    return null;
  }
  function testDate() { var p = S.probe; return p.testTimestamp ? parseLocal(p.testTimestamp) : null; }
  function whenText(d) {
    if (!d) return '';
    var today = new Date().toDateString() === d.toDateString();
    return (today ? '' : DOW[d.getDay()] + ' ' + dayLabelY(d) + ', ') + hhmm(d);
  }
  function testIsOld() {
    var d = testDate();
    return S.probe.paused || !d || Date.now() - d.getTime() > TEST_OLD_MS;
  }
  function pingExplainParas() {
    var p = S.probe, rg = S.region, tg = rg.target || {};
    var city = rg.serverCity && rg.cityConfirmed ? ' in ' + rg.serverCity : '';
    var entry = tg.kind === 'anycast';
    var gp = p.leagueRunning ? p.gamePing : null;
    var m = measuredPart();
    var d = testDate();
    var paras = [];
    // Name the region's server and city only when the match uses the region target or no match server is known.
    var server = p.leagueRunning && p.matchServer && !p.matchSameAsTarget
      ? 'the game server of your match'
      : 'the ' + (rg.shortName ? rg.shortName + ' ' : '') + 'game server' + city;
    if (gp) {
      paras.push('League measures in-game ping itself, from this PC to ' + server + ', and writes it to its own network log on this PC about every 10 seconds. Gecko Peeks only reads that log. Latest: '
        + Math.round(gp.ms) + ' ms' + (isNum(gp.lossPct) ? ', loss ' + gp.lossPct + '%' : '') + (isNum(gp.jitterMs) ? ', jitter ' + gp.jitterMs + ' ms' : '')
        + (gp.at ? ' (' + hhmm(gp.at) + ')' : '') + '.');
    } else if (p.leagueRunning) {
      paras.push('League measures in-game ping itself, from this PC to ' + server + ', and writes it to its own network log during a match. No match ping was written in the last minute, so none is shown.');
    } else {
      paras.push('League measures in-game ping itself, from this PC to ' + server + '. During a match Gecko Peeks reads it from League’s own network log on this PC. No match is running now: you can type the ping League showed you below.');
    }
    var tested = testedTarget();
    var reach;
    if (!m) {
      reach = p.hasRoute ? 'The last test has no latency figure, so nothing is measured to compare.' : 'No test has run yet, so nothing is measured yet.';
    } else if (m.regionChanged) {
      reach = 'The last test traced ' + tested.ip + ', before the region changed' + (rg.shortName ? ' to ' + rg.shortName : '') + '. The next test traces '
        + (tg.ip || 'the new target') + '; the comparison waits for it.';
    } else if (m.atTarget) {
      reach = 'Gecko Peeks measures your path up to the ' + (tg.label || 'Riot address') + (m.point ? ' ' + m.point : '') + ': ' + Math.round(m.ms) + ' ms in the test at ' + whenText(d)
        + ' (that address answered ping). ' + (entry ? 'That is where Riot’s own network starts; traceroute cannot see past it.' : 'Traceroute cannot see past it.');
    } else {
      reach = 'Riot did not answer ping in the test at ' + whenText(d) + ', so Gecko Peeks measures up to the last router that answered' + (m.point ? ' (' + m.point + ')' : '')
        + ': ' + Math.round(m.ms) + ' ms. Traceroute cannot see past the routers that stay silent.';
    }
    if (p.leagueRunning && p.matchServer) {
      reach += p.matchSameAsTarget
        ? ' Your current match connected to ' + p.matchServer + ', the address the tests trace.'
        : ' Your current match connected to ' + p.matchServer + ', a different address that the tests do not trace.';
    }
    if (m && !m.regionChanged && testIsOld()) {
      reach += ' This test is from ' + whenText(d) + (p.paused ? ' and logging is paused' : '') + ', so it may not match the game right now.';
    }
    paras.push(reach);
    paras.push('How to use it: compare figures from the same minute. If in-game ping rises while the measured part stays flat, the extra delay is past the measured point. If both rise together, the cause is most likely on your side of the path: your home network, your ISP or its upstream.');
    return paras;
  }
  // The split: League's ping at the test time (or the typed ping with no match) = measured part + the rest.
  function pingSplit() {
    var p = S.probe, tg = S.region.target || {};
    var m = measuredPart();
    var gp = p.leagueRunning ? p.gamePing : null;
    var paired = p.leagueRunning ? p.testGamePing : null;
    var game = null, source = '';
    if (paired) { game = Math.round(paired.ms); source = 'League’s ping at that test' + (paired.at ? ' (' + hhmm(paired.at) + ')' : ''); }
    else if (gp) return { msg: 'League’s log has no ping from the time of the last test. A test run during the match is compared with League’s ping from the same moment.' };
    else if (typedInvalid) return { msg: 'Type a whole number from 1 to 999.' };
    else if (isNum(typedGamePing)) { game = typedGamePing; source = 'the ' + typedGamePing + ' ms you typed'; }
    else return { msg: 'Type it to split it into the measured part and the rest.', muted: true };
    if (!m) return { msg: 'Nothing is measured yet, so the split waits for a test with a figure.' };
    if (m.regionChanged) return { msg: 'The last test traced the old region’s address, so the split waits for a test to the new one.' };
    if (!paired && testIsOld()) return { msg: 'The last test is from ' + whenText(testDate()) + (p.paused ? ' and logging is paused' : '') + '. Run a test while the game shows this ping, then compare.' };
    var mine = Math.round(m.ms), rest = game - mine;
    if (rest < 0) return { msg: (paired ? 'League’s figure (' + game + ' ms)' : 'The ' + game + ' ms you typed') + ' is lower than the measured part (' + mine + ' ms). They are not from the same moment or path; compare figures from the same minute.' };
    var mineLabel = m.atTarget ? 'Up to ' + (m.point || 'the traced address') + ' (measured)' : 'Up to the last router that answered' + (m.point ? ' (' + m.point + ')' : '') + ' (measured)';
    var sameServer = !(p.leagueRunning && p.matchServer) || p.matchSameAsTarget;
    var restLabel = m.atTarget && tg.kind === 'anycast' && sameServer
      ? 'The rest: Riot’s network past ' + (m.point || 'that address') + ' and the server’s own time (not measured)'
      : 'The rest: past the measured point, including the server’s own time (not measured)';
    return { game: game, mine: mine, rest: rest, mineLabel: mineLabel, restLabel: restLabel, source: source };
  }
  function renderPingExplain() {
    var box = $('#pingExplainText');
    if (!box) return;
    var paras = pingExplainParas();
    var sig = paras.join('|');
    if (box.__sig !== sig) {
      box.__sig = sig;
      clear(box);
      paras.forEach(function (t) { box.appendChild(h('p', { text: t })); });
    }
    // The typed box is only for when League's own ping is not available.
    var live = S.probe.leagueRunning && (S.probe.gamePing || S.probe.testGamePing);
    var lbl = $('#pingInputLabel'), row = $('#pingInputRow');
    if (lbl) lbl.hidden = !!live;
    if (row) row.hidden = !!live;
    renderPingSplit();
  }
  function renderPingSplit() {
    var out = $('#pingSplitOut');
    if (!out) return;
    var s = pingSplit();
    var sig = JSON.stringify(s);
    if (out.__sig === sig) return;
    out.__sig = sig;
    clear(out);
    if (s.msg) { out.appendChild(h('span', { class: s.muted ? 'muted' : '', text: s.msg })); return; }
    var total = Math.max(1, s.game);
    out.appendChild(h('div', { class: 'split-bar', role: 'img', 'aria-label': s.mineLabel + ' ' + s.mine + ' ms, ' + s.restLabel + ' ' + s.rest + ' ms' },
      h('i', { class: 'split-mine', style: 'width:' + (s.mine / total * 100).toFixed(1) + '%' }),
      h('i', { class: 'split-riot', style: 'width:' + (s.rest / total * 100).toFixed(1) + '%' })));
    out.appendChild(h('div', { class: 'split-legend' },
      h('span', {}, h('i', { class: 'sw split-mine' }), s.mineLabel + ': ' + s.mine + ' ms'),
      h('span', {}, h('i', { class: 'sw split-riot' }), s.restLabel + ': ' + s.rest + ' ms')));
    out.appendChild(h('div', { class: 'split-src', text: 'Total ' + s.game + ' ms: ' + s.source + '.' }));
  }

  function renderRoute() {
    var p = S.probe;
    renderPingExplain();
    // The table shows the last test. After a region change its target is not the region target,
    // so the subtitle and the Target chip name the tested address, and a note names the next one.
    var tested = testedTarget();
    setText($('#rtSubtitle'), tested
      ? 'Every hop from this PC to the ' + tested.label + ', in the last test'
      : 'Every hop from this PC to the ' + (S.region.shortName ? S.region.shortName + ' ' : '') + S.region.target.label);
    var banner = $('#rtChange');
    banner.hidden = !p.changeLine;
    setText(banner, p.changeLine);
    var note = $('#rtRegionNote');
    note.hidden = !tested;
    setText(note, tested ? nextTargetNote() : '');

    var chips = clear($('#routeChips'));
    function chip(k, v, tag) { return h('div', { class: 'rchip' }, h('span', { class: 'k', text: k }), h('span', { class: 'v', text: v }), tag ? h('span', { class: 'mini-tag', style: 'margin-left:0', text: tag }) : null); }
    function arrow() { return s('svg', { class: 'ico rchip-arrow', viewBox: '0 0 24 24', 'aria-hidden': 'true' }, s('path', { d: 'M5 12h14M13 6l6 6-6 6' })); }
    if (p.hasRoute) {
      chips.appendChild(chip('Upstream', p.upstream || 'Unknown', p.upstreamSource === 'asn' ? 'BY ASN' : ''));
      chips.appendChild(arrow());
      var byName = handoffNetworkText() !== p.handoffNetwork;
      chips.appendChild(chip('Handoff', handoffText() || 'Unknown', p.handoffSource === 'asn' ? 'BY ASN' : byName ? 'BY NAME' : ''));
      chips.appendChild(arrow());
    }
    chips.appendChild(chip('Target', tested ? tested.label + ' · ' + tested.ip : targetText()));
    if (p.hasRoute && p.testTimestamp) chips.appendChild(chip('Tested', p.testTimestamp));

    var sig = JSON.stringify(p.hops) + p.testTimestamp + '|' + JSON.stringify(S.region);
    if (sig === hopSig) return;
    hopSig = sig;
    var tbody = clear($('#hopTable tbody'));
    var empty = $('#hopEmpty');
    $('#hopTable').hidden = !p.hops.length;
    empty.hidden = !!p.hops.length;
    setText(empty, p.testRunning ? 'The test is running. Hops appear when it finishes.' : 'No route yet. Run a test to see every hop.');
    var maxRtt = 0;
    p.hops.forEach(function (x) { if (isNum(x.rttMs) && x.rttMs > maxRtt) maxRtt = x.rttMs; });
    var changed = {}; p.changedHops.forEach(function (n) { changed[n] = true; });
    p.hops.forEach(function (x) {
      var isChanged = x.changed || changed[x.n];
      var isTarget = x.reachedTarget || (!!x.ip && x.ip === S.region.lastTestTarget);
      var riotLabel = isTarget || (x.responded && inRiotPrefix(x.ip)) ? labelForIp(x.ip) : '';
      var tr = h('tr', { class: (isChanged ? 'changed ' : '') + (isTarget ? 'target' : '') });
      tr.appendChild(h('td', { class: 'num c-n', text: String(x.n) }));
      if (x.isPrivate) tr.appendChild(h('td', { class: 'addr muted', text: 'private' }));
      else tr.appendChild(h('td', { class: 'addr' + (x.ip ? '' : ' muted'), text: x.ip || '*' }));
      var host = h('td', { class: 'host' });
      var hasHost = x.host && x.host !== x.ip;
      if (x.isPrivate) appendBreakable(host, 'Home or ISP network');
      else if (hasHost) appendBreakable(host, x.host);
      else if (riotLabel) host.appendChild(h('span', { class: 'target-label', text: riotLabel }));
      else host.appendChild(document.createTextNode(x.responded ? '—' : 'no reply'));
      if (hasHost && riotLabel) host.appendChild(h('span', { class: 'net-sub', style: 'display:block;color:var(--text)', text: riotLabel }));
      if (isTarget) host.appendChild(h('span', { class: 'tag', text: 'TARGET' }));
      if (isChanged) host.appendChild(h('span', { class: 'tag warn', text: 'CHANGED' }));
      tr.appendChild(host);
      var asnTd = h('td', { class: 'num-l' });
      if (isNum(x.asn) && !x.isPrivate) {
        var a = h('a', { href: 'https://radar.cloudflare.com/as' + x.asn, class: 'asn-link', text: 'AS' + x.asn, 'aria-label': 'Open AS' + x.asn + ' on Cloudflare Radar' });
        a.addEventListener('click', function (e) { e.preventDefault(); openUrl('https://radar.cloudflare.com/as' + x.asn); });
        asnTd.appendChild(a);
      } else asnTd.appendChild(h('span', { class: 'muted', text: '—' }));
      tr.appendChild(asnTd);
      var net = h('td', { class: 'net' });
      if (x.network) {
        net.appendChild(h('b', { text: x.network }));
        if (x.nameSource === 'asn') net.appendChild(h('span', { class: 'mini-tag', text: 'by ASN', title: 'Named from the address owner (ASN), not from a host name' }));
        var subParts = [x.cityName, x.asName && x.asName !== x.network ? x.asName : ''].filter(Boolean);
        if (subParts.length) net.appendChild(h('span', { class: 'net-sub', text: subParts.join(' · ') }));
      } else if (x.asName) {
        net.appendChild(document.createTextNode(x.asName));
        if (x.cityName) net.appendChild(h('span', { class: 'net-sub', text: x.cityName }));
      } else {
        net.className = 'net muted';
        net.textContent = x.isPrivate ? 'Private' : '—';
      }
      tr.appendChild(net);
      var rttTd = h('td', { class: 'c-rtt' });
      if (isNum(x.rttMs)) {
        var w = maxRtt > 0 ? Math.max(2, x.rttMs / maxRtt * 100) : 0;
        rttTd.appendChild(h('div', { class: 'rtt' }, h('div', { class: 'rtt-bar' }, h('i', { style: 'width:' + w.toFixed(1) + '%' })), h('span', { class: 'rtt-val', text: ms(x.rttMs) })));
      } else rttTd.appendChild(h('div', { class: 'rtt' }, h('div', { class: 'rtt-bar' }), h('span', { class: 'rtt-val muted', text: 'no reply' })));
      tr.appendChild(rttTd);
      tbody.appendChild(tr);
    });
    var foot = [];
    if (p.hopsAddressesOnly) foot.push('Host names are not resolved for this test.');
    if (p.hops.length) foot.push('ASN links open Cloudflare Radar.');
    // Only when the target did not answer in this test; else the table shows its reply.
    var targetAnswered = p.hops.some(function (x) { return x.reachedTarget && isNum(x.rttMs); });
    if (p.hops.length && !targetAnswered) foot.push('Riot often does not answer ping; the last hop shows your real route latency.');
    if (p.hops.length && latencyCaveat()) foot.push(latencyCaveat());
    // In a match: say which address the game uses, and that Riot's own network past it is not visible.
    if (p.leagueRunning && p.matchServer) {
      var regionIp = S.region && S.region.target ? S.region.target.ip : '';
      // "Past it" only holds when the target is a Riot Direct entry point (anycast), not a server itself.
      var entry = S.region && S.region.target && S.region.target.kind === 'anycast';
      foot.push(p.matchSameAsTarget
        ? 'In a match: League connected to ' + p.matchServer + ', the address the tests trace.'
          + (entry ? ' Past it, Riot carries the game on its own network, so in-game ping is usually higher than these figures.' : '')
        : 'In a match: League connected to ' + p.matchServer + '. The tests trace the region target' + (regionIp ? ' ' + regionIp : '') + ', not this server.');
    }
    if (p.contextLine) foot.push(p.contextLine + '.');
    setText($('#hopFoot'), foot.join(' '));
  }

  // Host names break after dots and dashes, not in the middle of a label.
  function appendBreakable(node, text) {
    text.split(/(?<=[.\-])/).forEach(function (part, i) {
      if (i) node.appendChild(document.createElement('wbr'));
      node.appendChild(document.createTextNode(part));
    });
  }

  // ---------- History page ----------
  var histSig = '';
  // The table shows the newest HIST_PAGE rows; "Show more" adds HIST_PAGE older rows each time.
  var HIST_PAGE = 200, histShown = HIST_PAGE;
  function initHistMore() {
    $('#histMore').addEventListener('click', function () {
      histShown += HIST_PAGE; renderHistory();
      // The last page hides the button: move focus to the table summary, which says all rows are shown.
      if ($('#histMoreWrap').hidden) $('#recentSub').focus();
    });
  }
  // One precision for every row: whole percent, or "<1%".
  function shareText(share) { return share > 0 && share < 1 ? '<1%' : Math.round(share) + '%'; }
  function cityName(code) { var pl = place(code); return pl ? pl[0] : code; }
  function renderHistory() {
    var hist = S.history;
    var named = 0;
    hist.forEach(function (r) { if (r.namedLater) named++; });
    var sig = histVersion + '|' + hist.length + '|' + histShown + '|' + named + '|' + S.historyTotal + '|' + (hist.length ? hist[hist.length - 1].t + hist[hist.length - 1].handoffCityName + hist[0].handoffCityName : '');
    if (sig === histSig) return;
    histSig = sig;
    var up = clear($('#upstreams'));
    if (!hist.length) {
      up.appendChild(h('div', { class: 'empty', text: 'No tests yet. History fills in as tests run.' }));
      setText($('#upSub'), 'Share of tests');
    } else {
      setText($('#upSub'), 'Share of ' + plural(hist.length, 'test', 'tests') + ' since ' + dayLabelY(hist[0].d)
        + (historyCut() ? ' (' + (S.historyTotal - hist.length) + ' older tests are in the log file only)' : '')
        + (named ? ' · ' + plural(named, 'older test', 'older tests') + ' named later from saved routers' : ''));
      var groups = {};
      hist.forEach(function (r) {
        var k = r.upstream || 'Unknown';
        var g = groups[k] || (groups[k] = { name: k, n: 0, sum: 0, cnt: 0, replies: 0, hand: {} });
        g.n++;
        if (isNum(r.lastHopAvg)) { g.sum += r.lastHopAvg; g.cnt++; }
        if (isNum(r.riotAvg)) g.replies++;
        var hk = [r.handoffNetwork, r.handoffCityName || (r.handoffCity ? cityName(r.handoffCity) : '')].filter(Boolean).join(' · ');
        if (hk) g.hand[hk] = (g.hand[hk] || 0) + 1;
      });
      Object.keys(groups).map(function (k) { return groups[k]; }).sort(function (a, b) { return b.n - a.n; }).forEach(function (g) {
        var share = g.n / hist.length * 100;
        var topHand = Object.keys(g.hand).sort(function (a, b) { return g.hand[b] - g.hand[a]; })[0];
        up.appendChild(h('div', { class: 'up-row' },
          h('div', { class: 'up-name' }, g.name, h('small', { text: topHand ? 'Mostly via ' + topHand : plural(g.n, 'test', 'tests') })),
          h('div', { class: 'up-bar', role: 'img', 'aria-label': g.name + ' ' + share.toFixed(1) + ' percent of tests' },
            h('i', { style: 'width:' + Math.max(1.5, share).toFixed(1) + '%' }),
            // A short fill puts the label after the fill, so the text has one background.
            h('span', share < 20
              ? { class: 'out', style: 'left:calc(' + Math.max(1.5, share).toFixed(1) + '% + 8px)', text: shareText(share) + ' · ' + plural(g.n, 'test', 'tests') }
              : { text: shareText(share) + ' · ' + plural(g.n, 'test', 'tests') })),
          h('div', { class: 'up-stat' }, h('b', { text: g.cnt ? ms(g.sum / g.cnt) : '—' }), h('small', { text: 'avg last hop' })),
          h('div', { class: 'up-stat' }, h('b', { text: Math.round(g.replies / g.n * 100) + '%' }), h('small', { text: 'Riot replies' }))));
      });
    }
    var tbody = clear($('#histTable tbody'));
    var rows = hist.slice(-histShown).reverse();
    $('#histTable').hidden = !rows.length;
    $('#histEmpty').hidden = !!rows.length;
    setText($('#histEmpty'), 'No tests yet.');
    setText($('#recentSub'), !rows.length ? 'Newest first'
      : rows.length === hist.length ? 'Newest first · all ' + plural(hist.length, 'test', 'tests') + (historyCut() ? ' loaded' : '')
      : 'Newest first · ' + rows.length + ' of ' + plural(hist.length, 'test', 'tests'));
    var left = hist.length - rows.length;
    $('#histMoreWrap').hidden = left <= 0;
    setText($('#histMore span'), 'Show ' + Math.min(HIST_PAGE, left) + ' more');
    var frag = document.createDocumentFragment();
    rows.forEach(function (r) {
      var routeCell = r.namedLater
        ? h('td', { title: 'Saved as Unknown. Named later from the routers saved with this test.' }, routeText(r), h('span', { class: 'named-later', text: 'named later' }))
        : h('td', { text: routeText(r) });
      frag.appendChild(h('tr', {},
        h('td', { class: 'tnum', text: DOW[r.d.getDay()] + ' ' + dayLabelY(r.d) + ', ' + hhmm(r.d) }),
        routeCell,
        h('td', { class: 'num' + (isNum(r.lastHopAvg) ? '' : ' muted'), text: isNum(r.lastHopAvg) ? ms(r.lastHopAvg) : (isNum(r.lastHopLoss) && r.lastHopLoss >= 100 ? 'no reply' : '—') }),
        h('td', { class: 'num' + (isNum(r.riotAvg) ? '' : ' muted'), text: isNum(r.riotAvg) ? ms(r.riotAvg) : 'no reply' }),
        h('td', { class: r.league ? 'yes' : 'no', text: r.league ? 'Yes' : 'No' }),
        h('td', { class: r.vpn ? 'yes' : 'no', text: r.vpn ? 'Yes' : 'No' })));
    });
    tbody.appendChild(frag);
  }

  // ---------- Internet page ----------
  var netSig = '';
  function renderInternet() {
    var c = S.cloud, pulse = c.pulse ? obj(c.pulse) : null, edge = c.edge ? obj(c.edge) : null;
    var age = cloudAge();
    setText($('#netAge'), c.status === 'loading' ? 'Updating…' : age !== null ? 'Updated ' + agoText(age) : '');
    var btn = $('#btnRefresh');
    btn.disabled = !c.enabled || c.status === 'loading';
    var stBox = $('#netState'), body = $('#netBody');
    var mode = !c.enabled || c.status === 'off' ? 'off' : (!pulse && !edge && c.status === 'error') ? 'error' : (!pulse && !edge) ? 'loading' : 'ok';
    stBox.hidden = mode === 'ok';
    body.hidden = mode !== 'ok';
    if (mode === 'ok') renderRiotStatus($('#rsInternet'));
    var sig = mode + '|' + c.status + '|' + c.error + '|' + JSON.stringify(pulse) + JSON.stringify(edge) + '|' + upstreamAsns().join(',') + '|' + S.probe.lastHop.avgMs;
    if (sig === netSig) return;
    netSig = sig;
    if (mode !== 'ok') {
      clear(stBox);
      stBox.className = 'state-card' + (mode === 'error' ? ' tone-bad' : '');
      var icon = h('div', { class: 'st-ico' });
      if (mode === 'loading') icon.appendChild(h('span', { class: 'spinner' }));
      else icon.appendChild(s('svg', { class: 'ico', viewBox: '0 0 24 24', 'aria-hidden': 'true' }, s('circle', { cx: 12, cy: 12, r: 9 }), s('path', { d: mode === 'error' ? 'M12 7v6M12 16.5v.5' : 'M3 12h18M12 3a14 14 0 0 1 0 18M12 3a14 14 0 0 0 0 18' })));
      var txt = h('div', {});
      if (mode === 'off') {
        txt.appendChild(h('h2', { text: 'Cloudflare live data is off' }));
        txt.appendChild(h('p', { text: 'Gecko Peeks still tests your route. Turn this on to add Cloudflare Radar data about Riot, your ISP and your country. No personal data is sent; the service sees only your public connection, like any website.' }));
        txt.appendChild(turnOnButton());
      } else if (mode === 'error') {
        txt.appendChild(h('h2', { text: 'Cloudflare data is not available right now' }));
        txt.appendChild(h('p', { text: c.error || 'The Gecko Peeks service did not answer. Your route tests are not affected.' }));
        txt.appendChild(retryButton());
      } else {
        txt.appendChild(h('h2', { text: 'Loading Cloudflare data' }));
        txt.appendChild(h('p', { text: 'This takes a few seconds.' }));
      }
      stBox.appendChild(icon); stBox.appendChild(txt);
      return;
    }

    // edge facts
    var facts = clear($('#edgeFacts'));
    function fact(k, v, small) { facts.appendChild(h('dt', { text: k })); facts.appendChild(h('dd', {}, v || '—', small ? h('small', { text: small }) : null)); }
    if (edge) {
      fact('ISP', str(edge.asOrg), isNum(edge.asn) ? 'AS' + edge.asn : '');
      var colo = str(edge.colo), cp = place(colo);
      fact('Cloudflare colo', colo ? colo.toUpperCase() : '', cp ? cp[0] : '');
      fact('Location', [str(edge.city), str(edge.region), str(edge.country)].filter(Boolean).join(', '));
      var proto = [str(edge.httpProtocol), str(edge.tlsVersion)].filter(Boolean).join(' · ');
      if (proto) fact('Connection', proto);
      if (edge.timezone) fact('Time zone', str(edge.timezone));
    } else fact('Edge', 'Not available');
    if (c.status === 'error') fact('Last refresh', 'Failed', c.error || '');

    pulse = pulse || {};
    var riot = obj(pulse.riot), isp = obj(pulse.isp), country = obj(pulse.country);
    renderUpstreams(isp);
    // traffic charts
    function traffic(boxId, tr, cls, color, name) {
      tr = tr ? obj(tr) : {};
      var ts = arr(tr.timestamps).map(function (t) { var d = parseIso(t); return d ? d.getTime() : null; });
      var vals = arr(tr.values).map(function (v) { return isNum(v) ? v * 100 : null; });
      var times = [], vv = [];
      ts.forEach(function (t, i) { if (t !== null && i < vals.length) { times.push(t); vv.push(vals[i]); } });
      drawChart($('#' + boxId), {
        sig: JSON.stringify(tr), times: times, yMax: 100, label: name + ' traffic', empty: 'No traffic data',
        yFmt: function (v) { return Math.round(v) + '%'; },
        tipTitle: function (t) { var d = new Date(t); return dayLabel(d) + ', ' + hhmm(d); },
        series: [{ name: name, cls: 'line-traffic ' + cls, color: color, values: vv, fmt: function (v) { return isNum(v) ? Math.round(v) + '%' : '—'; } }]
      });
    }
    traffic('riotTraffic', riot.traffic, '', '#f6821f', 'Riot');
    setText($('#ispTrafficTitle'), (isp.name ? str(isp.name) : 'ISP') + ' traffic');
    setText($('#ispTrafficSub'), (isNum(isp.asn) ? 'AS' + isp.asn + ' · ' : '') + 'last 24 hours · normalized');
    traffic('ispTraffic', isp.traffic, 'isp', '#a78bfa', isp.name ? str(isp.name) : 'ISP');

    // IQI
    var iqiBox = clear($('#iqi'));
    var iqi = country.iqi ? obj(country.iqi) : {}, lat = iqi.latency ? obj(iqi.latency) : null, bw = iqi.bandwidth ? obj(iqi.bandwidth) : null;
    setText($('#iqiSub'), 'Internet quality index' + (country.code ? ' · ' + str(country.code) : ''));
    if (lat && (isNum(lat.p25) || isNum(lat.p50) || isNum(lat.p75))) {
      var mx = Math.max(lat.p25 || 0, lat.p50 || 0, lat.p75 || 0) * 1.15 || 1;
      [['p25', '#34d399'], ['p50', '#f6821f'], ['p75', '#a78bfa']].forEach(function (q) {
        var v = lat[q[0]];
        iqiBox.appendChild(h('div', { class: 'iqi-row' },
          h('span', { class: 'k', text: q[0].toUpperCase() }),
          h('div', { class: 'iqi-bar' }, h('i', { style: 'width:' + (isNum(v) ? Math.max(2, v / mx * 100) : 0).toFixed(1) + '%;background:' + q[1] })),
          h('span', { class: 'v', text: ms(v) })));
      });
      var yours = S.probe.lastHop.avgMs;
      var note = 'Lower is better. P50 is the median connection in ' + (country.code ? str(country.code) : 'your country') + '.';
      if (isNum(yours)) note += ' Your last hop to Riot: ' + ms(yours) + '.';
      if (bw && isNum(bw.p50)) note += ' Median bandwidth ' + Math.round(bw.p50) + ' Mbps.';
      iqiBox.appendChild(h('p', { class: 'iqi-note', text: note }));
    } else iqiBox.appendChild(h('div', { class: 'chart-empty', style: 'position:static;padding:30px 0', text: 'No latency data for this country' }));

    // events
    var evBox = clear($('#events'));
    var all = [];
    var lists = 0, missing = 0;
    function add(list, scope) { lists++; if (!Array.isArray(list)) missing++; arr(list).forEach(function (e) { e = obj(e); all.push({ e: e, scope: scope, t: parseIso(e.start) }); }); }
    ['outages', 'anomalies', 'hijacks', 'leaks'].forEach(function (k) {
      add(riot[k], 'Riot · AS6507');
      add(isp[k], (isp.name ? str(isp.name) : 'Your ISP') + (isNum(isp.asn) ? ' · AS' + isp.asn : ''));
    });
    add(country.outages, 'Country · ' + (country.code ? str(country.code) : ''));
    all.sort(function (a, b) { return (b.t ? b.t.getTime() : 0) - (a.t ? a.t.getTime() : 0); });
    if (!all.length) {
      if (missing === lists) evBox.appendChild(h('div', { class: 'state-msg', text: 'Radar did not return incident data right now. Try Refresh later.' }));
      else {
        evBox.appendChild(h('div', { class: 'ev-empty', text: 'No incidents in the last 7 days' }));
        if (missing) evBox.appendChild(h('p', { class: 'iqi-note', text: 'Some Radar sources did not answer, so this list may be incomplete.' }));
      }
    } else {
      all.forEach(function (x) {
        var e = x.e, kind = str(e.kind) || 'event';
        var tone = kind === 'hijack' || kind === 'outage' ? 'bad' : kind === 'leak' ? 'warn' : 'violet';
        var end = parseIso(e.end);
        var when = x.t ? dayLabel(x.t) + ', ' + hhmm(x.t) : '';
        var dur = x.t && end ? Math.max(1, Math.round((end - x.t) / 60000)) : null;
        var right = h('div', { class: 'ev-when' }, when, h('div', { text: end ? (dur >= 120 ? Math.round(dur / 60) + ' h' : dur + ' min') : 'ongoing' }));
        var url = safeRadarUrl(e.url);
        var titleNode = url ? h('a', { href: url, class: 'ev-title', text: str(e.title) || kind, onclick: function (ev) { ev.preventDefault(); openUrl(url); } }) : h('div', { class: 'ev-title', text: str(e.title) || kind });
        evBox.appendChild(h('div', { class: 'ev' },
          h('div', {}, h('span', { class: 'badge ' + tone, text: kind.toUpperCase() })),
          h('div', {}, titleNode, e.detail ? h('div', { class: 'ev-detail', text: str(e.detail) }) : null, h('div', { class: 'ev-scope', text: x.scope })),
          right));
      });
    }
  }

  // Radar AS relations for the ISP: providers first, then peers, as the Worker sends them.
  function renderUpstreams(isp) {
    var box = clear($('#ispUpstreams'));
    var name = str(isp.name);
    setText($('#ispUpSub'), (name || 'Your ISP') + (isNum(isp.asn) ? ' · AS' + isp.asn : '') + ' · Cloudflare Radar');
    if (!Array.isArray(isp.upstreams)) { box.appendChild(h('div', { class: 'ups-empty', text: 'Not available.' })); return; }
    var rows = isp.upstreams.map(obj).filter(function (u) { return isNum(u.asn) && u.asn > 0; });
    if (!rows.length) { box.appendChild(h('div', { class: 'ups-empty', text: 'Radar lists no upstreams for this network.' })); return; }
    rows.forEach(function (u) {
      var rel = str(u.rel) === 'peer' ? 'peer' : 'provider';
      // Any ASN of the upstream provider (Lumen has three), not only the one the test saved.
      var now = upstreamAsns().indexOf(u.asn) >= 0;
      var url = 'https://radar.cloudflare.com/as' + u.asn;
      var link = h('a', { href: url, class: 'asn-link', text: 'AS' + u.asn, 'aria-label': 'Open AS' + u.asn + ' on Cloudflare Radar' });
      link.addEventListener('click', function (e) { e.preventDefault(); openUrl(url); });
      box.appendChild(h('div', { class: 'ups-row' + (now ? ' now' : '') },
        h('div', { class: 'ups-main' }, link,
          h('span', { class: 'sep', text: '·' }), h('span', { class: 'name', text: str(u.name) || 'Unknown network' }),
          h('span', { class: 'sep', text: '·' }), h('span', { class: 'rel', text: rel })),
        now ? h('span', { class: 'badge good caps', text: 'in use now' }) : null));
    });
  }

  // ---------- Settings page ----------
  var BASIS_TEXT = { country: 'Picked from your country.', continent: 'Picked from your continent.', saved: 'Last automatic pick.', default: 'Default region.' };
  function renderRegion() {
    var sel = $('#regionSelect'), r = S.region;
    var autoText = r.auto && r.shortName ? 'Automatic (' + r.shortName + ' · ' + r.name + ')' : 'Automatic';
    var sig = autoText + '|' + S.regions.map(function (x) { return x.id + x.shortName + x.name; }).join(',');
    if (sel.__sig !== sig) {
      sel.__sig = sig;
      clear(sel);
      sel.appendChild(h('option', { value: 'auto', text: autoText }));
      S.regions.forEach(function (x) { sel.appendChild(h('option', { value: x.id, text: x.shortName + ' · ' + x.name })); });
    }
    var want = S.settings.region === 'auto' || !S.regions.some(function (x) { return x.id === S.settings.region; }) ? 'auto' : S.settings.region;
    if (sel.value !== want) sel.value = want;
    sel.disabled = !hasState;
    var line;
    if (!r.id) line = hasState ? 'Region data is not available.' : 'Loading the region.';
    else {
      line = 'Tests trace the ' + r.target.label + ' at ' + (r.target.ip || 'an unknown address') + '. ';
      line += r.serverCity ? 'Riot servers: ' + r.serverCity + (r.cityConfirmed ? '' : ' (not confirmed by Riot)') + '.' : 'Riot does not publish this server location.';
      if (r.auto && BASIS_TEXT[r.basis]) line += ' ' + BASIS_TEXT[r.basis];
    }
    setText($('#regionLine'), line);
    var cav = $('#regionCaveat'), ct = latencyCaveat();
    cav.hidden = !ct;
    setText(cav, ct);
  }
  function renderSettings() {
    renderRegion();
    $all('.switch').forEach(function (sw) {
      var key = sw.getAttribute('data-key');
      sw.setAttribute('aria-checked', S.settings[key] ? 'true' : 'false');
    });
    var p = S.probe;
    var pause = $('#btnPause');
    setText($('span', pause), p.paused ? 'Resume logging' : 'Pause logging');
    pause.setAttribute('aria-label', p.paused ? 'Resume logging' : 'Pause logging');
    pause.className = 'btn' + (p.paused ? ' btn-primary' : '');
    var ls = $('#logStatus');
    setText(ls, S.log.status || 'Waiting for the first test');
    ls.className = 'card-sub ' + toneClass(S.log.statusTone);
    setText($('#logPath'), S.log.path || 'The log path appears after the app starts.');
    var hasPath = !!S.log.path;
    $('#btnOpenLog').disabled = !hasPath; $('#btnCopyPath').disabled = !hasPath; $('#btnOpenFolder').disabled = !(S.log.folder || hasPath);

    var u = S.update;
    setText($('#verLine'), 'Gecko Peeks ' + S.app.version + (S.app.profile === 'Dev' ? ' · Dev build' : '') + (S.app.demo ? ' · Demo' : ''));
    var st = clear($('#updState'));
    var last = parseIso(u.lastCheckUtc), lastTxt = last ? 'Last checked ' + agoText((Date.now() - last.getTime()) / 1000) + '.' : '';
    if (u.state === 'checking') st.appendChild(h('span', { class: 'spinner' })), st.appendChild(h('span', { text: 'Checking for updates…' }));
    else if (u.state === 'downloading') {
      st.appendChild(h('span', { text: 'Downloading ' + (u.available ? 'version ' + u.available : 'update') }));
      st.appendChild(h('div', { class: 'progress', role: 'progressbar', 'aria-valuenow': String(Math.round(clamp01(u.progress) * 100)), 'aria-valuemin': '0', 'aria-valuemax': '100' }, h('i', { style: 'width:' + (clamp01(u.progress) * 100).toFixed(0) + '%' })));
      st.appendChild(h('span', { class: 'tnum', text: Math.round(clamp01(u.progress) * 100) + '%' }));
    } else if (u.state === 'ready') st.appendChild(h('span', { class: 'tone-good', text: 'Version ' + (u.available || '') + ' is ready. Restart to finish the update.' }));
    else if (u.state === 'installing') st.appendChild(h('span', { class: 'spinner' })), st.appendChild(h('span', { text: 'Installing the update…' }));
    else if (u.state === 'upToDate') st.appendChild(h('span', { class: 'tone-good', text: 'You are up to date. ' })), st.appendChild(h('span', { text: lastTxt }));
    else if (u.state === 'error') st.appendChild(h('span', { class: 'tone-bad', text: u.error || 'Could not check for updates.' }));
    else st.appendChild(h('span', { text: lastTxt || 'Gecko Peeks checks for updates in the background.' }));

    var gh = safeGithubUrl(u.releaseUrl);
    var aSig = u.state + '|' + gh;
    var actions = $('#updActions');
    if (actions.__sig !== aSig) { actions.__sig = aSig; buildUpdActions(actions, u, gh); }
    var notes = $('#updNotes');
    notes.hidden = !u.notes || !(u.state === 'ready' || u.state === 'downloading' || u.state === 'installing');
    setText(notes, u.notes);
    setText($('#refLine'), refdataText());
  }
  // "Reference data 2026.11.21-1 (worker, updated 1 h ago)".
  function refdataText() {
    var rd = S.cloud.refdata;
    if (!rd || !rd.version) return '';
    var age = isNum(rd.ageSec) ? rd.ageSec + (Date.now() - receivedAt) / 1000 : null;
    return 'Reference data ' + rd.version + ' (' + (rd.source || 'builtin') + (age !== null ? ', updated ' + agoText(age) : '') + ')';
  }
  function buildUpdActions(actions, u, gh) {
    clear(actions);
    if (u.state === 'ready') {
      actions.appendChild(h('button', { type: 'button', class: 'btn btn-good', onclick: function () { send({ cmd: 'installUpdate' }); } }, h('span', { text: 'Restart to update' })));
    }
    if (gh) actions.appendChild(h('button', { type: 'button', class: 'btn', onclick: function () { openUrl(gh); } }, h('span', { text: 'Release page' })));
    actions.appendChild(h('button', { type: 'button', class: 'btn', disabled: u.state === 'checking' || u.state === 'downloading' || u.state === 'installing', onclick: function () { send({ cmd: 'checkUpdate' }); } }, h('span', { text: 'Check for updates' })));
  }

  // ---------- Render ----------
  function render() {
    renderTopbar();
    if (page === 'overview') renderOverview();
    else if (page === 'route') renderRoute();
    else if (page === 'history') renderHistory();
    else if (page === 'internet') renderInternet();
    else if (page === 'settings') renderSettings();
  }
  function tick() {
    tickTopbar();
    if (page === 'overview') {
      tickKpiNext(false);
      var age = cloudAge();
      if (age !== null && S.cloud.enabled) setText($('#pulseAge'), 'Cloudflare Radar · updated ' + agoText(age));
      renderRiotStatus($('#rsOverview'));
    } else if (page === 'internet') {
      if (!$('#netBody').hidden) renderRiotStatus($('#rsInternet'));
      var a = cloudAge();
      if (a !== null && S.cloud.status !== 'loading') setText($('#netAge'), 'Updated ' + agoText(a));
    }
  }

  // ---------- Wire up ----------
  function wire() {
    $('#pingInput').addEventListener('input', function (e) {
      var raw = String(e.target.value || '').trim();
      var ok = /^\d{1,3}$/.test(raw) && +raw >= 1 && +raw <= 999;
      typedGamePing = ok ? +raw : null;
      typedInvalid = raw.length > 0 && !ok;
      renderPingSplit();
    });
    // The Overview link opens the explainer on the Route page.
    $('#pingExplainLink').addEventListener('click', function () {
      setTimeout(function () {
        var d = $('#pingExplain');
        if (!d) return;
        d.open = true;
        d.scrollIntoView({ block: 'start', behavior: 'smooth' });
      }, 60);
    });
    $('#btnRunTop').addEventListener('click', function () { send({ cmd: 'runNow' }); });
    $('#btnRunRoute').addEventListener('click', function () { send({ cmd: 'runNow' }); });
    $('#btnCopyDiag').addEventListener('click', function () { send({ cmd: 'copyDiagnosis' }); });
    $('#btnRefresh').addEventListener('click', function () { send({ cmd: 'refreshCloud' }); });
    $('#btnPause').addEventListener('click', function () { send({ cmd: 'togglePause' }); });
    $('#btnOpenLog').addEventListener('click', function () { send({ cmd: 'openLog' }); });
    $('#btnOpenFolder').addEventListener('click', function () { send({ cmd: 'openFolder' }); });
    $('#btnCopyPath').addEventListener('click', function () { send({ cmd: 'copyPath' }); });
    $('#regionSelect').addEventListener('change', function (e) {
      var id = e.target.value === 'auto' ? 'auto' : regionId(e.target.value);
      if (id) send({ cmd: 'setRegion', id: id });
    });
    $all('.switch').forEach(function (sw) {
      sw.addEventListener('click', function () {
        var key = sw.getAttribute('data-key'), next = sw.getAttribute('aria-checked') !== 'true';
        sw.setAttribute('aria-checked', next ? 'true' : 'false');
        send({ cmd: 'setSetting', key: key, value: next });
      });
    });
    // Never navigate the WebView: external links go through the host.
    document.addEventListener('click', function (e) {
      if (e.defaultPrevented) return;
      var a = e.target.closest && e.target.closest('a[href]');
      if (!a) return;
      var href = a.getAttribute('href');
      if (href.charAt(0) === '#') return;
      e.preventDefault();
      var url = safeRadarUrl(href) || safeGithubUrl(href);
      if (url) openUrl(url);
    });
  }

  function init() {
    initNav();
    initRange();
    initHistMore();
    wire();
    mapView.build();
    window.addEventListener('hashchange', route);
    if (webview) {
      webview.addEventListener('message', function (e) { receive(e.data); });
      route();
      send({ cmd: 'ready' });
    } else {
      mock = createMock();
      route();
      mock.start();
    }
    setInterval(tick, 1000);
  }

  // ---------- Mock host (browser only) ----------
  function createMock() {
    var variant = (/[?&]mock=([\w-]+)/.exec(location.search) || [])[1] || 'full';
    var now = Date.now();
    function isoLocal(d) {
      return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()) + 'T' + pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds());
    }
    function csvLocal(d) { return isoLocal(d).replace('T', ' '); }
    // seeded noise so screenshots are stable
    var seed = 7;
    function rnd() { seed = (seed * 16807) % 2147483647; return (seed - 1) / 2147483646; }
    function gauss() { return (rnd() + rnd() + rnd() - 1.5) / 1.5; }

    var hist = [];
    // ?mock=longhistory: 45 days, and day 2 saved as Unknown but named later (v2.1.2 history)
    // ?mock=cuthistory: the same, and the host says 5000 older tests were not sent
    var longHistory = variant === 'longhistory' || variant === 'cuthistory';
    var step = 15 * 60000, startT = now - (longHistory ? 45 : 7) * 86400000;
    startT = Math.floor(startT / step) * step;
    for (var t = startT; t <= now - 60000; t += step) {
      var d = new Date(t), hr = d.getHours() + d.getMinutes() / 60;
      var dayIdx = Math.floor((t - startT) / 86400000);
      var evening = Math.max(0, Math.sin((hr - 14) / 10 * Math.PI)) * 7;
      // Demo route (neutral): HE → Riot Direct chi01, and Cogent on day 3 afternoon.
      var cogent = dayIdx === 3 && hr >= 13 && hr < 18;
      var upstream = cogent ? 'Cogent' : 'HE';
      var handNet = 'Riot Direct';
      var handCity = 'chi01';
      var base = cogent ? 44 : 38;
      var last = base + evening + gauss() * 2.2 + (rnd() < 0.015 ? 30 + rnd() * 40 : 0);
      var loss = rnd() < 0.03 ? Math.round(rnd() * 20) : 0;
      var missing = rnd() < 0.004;
      var riotReplies = dayIdx < 2 || (dayIdx === 5 && hr > 10);
      var league = hr >= 19 && hr <= 23.5 && dayIdx % 2 === 0;
      hist.push({
        t: isoLocal(d), upstream: upstream, handoffNetwork: handNet, handoffCity: handCity, handoffCityName: 'Chicago',
        lastHopAvg: missing ? null : Math.round(last * 10) / 10, lastHopLoss: missing ? 100 : loss,
        riotAvg: riotReplies && !missing ? Math.round((last + 1.5 + gauss()) * 10) / 10 : null,
        riotLoss: riotReplies ? 0 : 100, league: league, vpn: dayIdx === 4 && hr > 9 && hr < 12, adapter: 'Ethernet',
        namedLater: longHistory && dayIdx === 2
      });
    }
    // The frozen neutral demo route (contract 6.4). It is nobody's real connection.
    var hops = [
      { n: 1, ip: '192.168.1.1', host: '', rttMs: 1, isPrivate: true },
      { n: 2, ip: '100.64.0.1', host: '', rttMs: 6, isPrivate: true },
      { n: 3, ip: '*', host: '', rttMs: null },
      { n: 4, ip: '184.105.64.1', host: '', rttMs: 17, asn: 6939, asName: 'Hurricane Electric LLC', network: 'HE', nameSource: 'asn' },
      { n: 5, ip: '184.105.80.1', host: '100ge0-1.core2.chi1.he.net', rttMs: 38, asn: 6939, asName: 'Hurricane Electric LLC', network: 'HE', nameSource: 'hostname', cityName: 'Chicago' },
      { n: 6, ip: '104.160.131.1', host: 'ae1.er01.chi01.riotdirect.net', rttMs: 39, asn: 6507, asName: 'Riot Games', network: 'Riot Direct', nameSource: 'hostname', cityName: 'Chicago' },
      { n: 7, ip: '192.207.0.5', host: '', rttMs: 40, asn: 6507, asName: 'Riot Games', network: 'Riot Direct', nameSource: 'asn', reachedTarget: true }
    ].map(mockHop);
    function mockHop(x) {
      return { n: x.n, ip: x.ip, host: x.host, rttMs: x.rttMs, responded: isNum(x.rttMs), reachedTarget: !!x.reachedTarget,
        changed: !!x.changed, isPrivate: !!x.isPrivate, asn: x.asn || null, asName: x.asName || null,
        network: x.network || '', nameSource: x.nameSource || '', cityName: x.cityName || '' };
    }
    // The frozen refdata region table, for the mock region picker only. The real host sends
    // one region and a short list; it never sends this table.
    var REGIONS = [
      ['na1', 'NA', 'North America', 'na1', 'na', '192.207.0.5', 'anycast', true, 'Chicago', true, 41.88, -87.63],
      ['euw1', 'EUW', 'EU West', 'euw1', 'eu', '185.40.64.65', 'dns', true, 'Amsterdam', true, 52.37, 4.90],
      ['eun1', 'EUNE', 'EU Nordic & East', 'eun1', 'eu', '192.207.0.5', 'anycast', false, 'Amsterdam', false, 52.37, 4.90],
      ['kr', 'KR', 'Korea', 'kr1', 'kr', '45.250.208.40', 'dns', true, 'Seoul', true, 37.57, 126.98],
      ['jp1', 'JP', 'Japan', 'jp1', 'ap', '192.207.0.5', 'anycast', false, 'Tokyo', true, 35.68, 139.69],
      ['br1', 'BR', 'Brazil', 'br1', 'br', '192.207.0.5', 'anycast', false, 'Sao Paulo', true, -23.55, -46.63],
      ['la1', 'LAN', 'Latin America North', 'la1', 'latam', '192.207.0.5', 'anycast', false, 'Miami', true, 25.76, -80.19],
      ['la2', 'LAS', 'Latin America South', 'la2', 'latam', '192.207.0.5', 'anycast', false, 'Santiago', true, -33.45, -70.67],
      ['oc1', 'OCE', 'Oceania', 'oc1', 'ap', '192.207.0.5', 'anycast', false, 'Sydney', true, -33.87, 151.21],
      ['tr1', 'TR', 'Turkey', 'tr1', 'eu', '192.207.0.5', 'anycast', false, 'Istanbul', true, 41.01, 28.98],
      ['ru', 'RU', 'Russia', 'ru1', 'eu', '192.207.0.5', 'anycast', false, '', false, null, null],
      ['me1', 'ME', 'Middle East & North Africa', 'me1', 'eu', '192.207.0.5', 'anycast', false, 'Bahrain', false, 26.23, 50.59],
      ['sg2', 'SEA', 'Southeast Asia', 'sg2', 'ap', '192.207.0.5', 'anycast', false, 'Singapore', false, 1.35, 103.82],
      ['tw2', 'TW', 'Taiwan', 'tw2', 'ap', '192.207.0.5', 'anycast', false, 'Taipei', false, 25.03, 121.57],
      ['vn2', 'VN', 'Vietnam', 'vn2', 'ap', '192.207.0.5', 'anycast', false, 'Ho Chi Minh City', false, 10.82, 106.63]
    ];
    function mockRegion(id, auto, basis) {
      var r = REGIONS.filter(function (x) { return x[0] === id; })[0] || REGIONS[0];
      return {
        id: r[0], shortName: r[1], name: r[2], auto: auto, basis: auto ? basis : 'manual',
        serverCity: r[8], cityConfirmed: r[9], lat: r[10], lon: r[11], lolStatusId: r[3], valorantShard: r[4],
        target: { ip: r[5], label: r[6] === 'dns' ? PLATFORM_LABEL : EDGE_LABEL, kind: r[6], inRegion: r[7] },
        lastTestTarget: '192.207.0.5'
      };
    }
    function mockRiotStatus(region, lol, valorant) {
      return { region: region.id, lolStatusId: region.lolStatusId, valorantShard: region.valorantShard, fetchedAt: new Date(now - 40000).toISOString(),
        lol: lol, valorant: valorant };
    }
    var demoMaint = { id: 1001, type: 'maintenance', severity: null, status: 'scheduled', title: 'Demo: scheduled maintenance',
      text: 'Demo text. Ranked queues close for a short time during the update.', platforms: ['windows', 'macos'],
      createdAt: new Date(now - 5 * 3600000).toISOString(), updatedAt: new Date(now - 2 * 3600000).toISOString(), archiveAt: null };
    function series(n, fn) {
      var ts = [], vs = [], end = Math.floor(now / 3600000) * 3600000;
      for (var i = n - 1; i >= 0; i--) { ts.push(new Date(end - i * 3600000).toISOString()); }
      for (var j = 0; j < n; j++) vs.push(fn(new Date(ts[j]).getHours()));
      var mx = Math.max.apply(null, vs);
      return { timestamps: ts, values: vs.map(function (v) { return Math.round(v / mx * 1000) / 1000; }) };
    }
    var st = {
      type: 'state',
      app: { version: '2.1.0', profile: 'Production', demo: true },
      probe: {
        paused: false, leagueRunning: false, testRunning: false, phase: 'Waiting for the next test', mode: 'Tests every 15 min',
        nextDueUtc: new Date(now + 11 * 60000 + 23000).toISOString(), lastError: '', activityLine: '',
        hasRoute: true, routeLine: 'HE → Riot Direct · Chicago', upstream: 'HE', handoffNetwork: 'Riot Direct',
        handoffCity: 'chi01', handoffCityName: 'Chicago',
        upstreamSource: 'hostname', handoffSource: 'hostname', upstreamAsn: 6939, upstreamAsns: [6939], handoffLat: 41.88, handoffLon: -87.63,
        // Demo hop 7 (the target) answers at 40 ms, so the Riot figure does too.
        lastHop: { avgMs: 39, lossPct: 0, figure: '39 ms' }, riot: { avgMs: 40, lossPct: 0, figure: '40 ms' },
        contextLine: 'League not running · Ethernet · 3 min ago', changeLine: '', changedHops: [],
        testTimestamp: csvLocal(new Date(now - 3 * 60000)), adapter: 'Ethernet', wifi: '', gateway: '192.168.1.1', vpn: false, league: false,
        hops: hops, hopsAddressesOnly: false, tooltip: ''
      },
      log: { path: 'C:\\Users\\Player\\Documents\\RiotRouteLog\\riot-route-log.csv', folder: 'C:\\Users\\Player\\Documents\\RiotRouteLog', fileName: 'riot-route-log.csv',
        health: 'Saved', chip: 'Logging · saved', chipTone: 'good', status: 'Saved 3 min ago · ' + hist.length + ' rows', statusTone: 'good', unsavedRows: 0 },
      history: hist,
      historyTotal: hist.length + (variant === 'cuthistory' ? 5000 : 0),
      cloud: {
        enabled: true, status: 'ok', error: '', edgeAgeSec: 95, pulseAgeSec: 120,
        edge: { asn: 64500, asOrg: 'Demo ISP', colo: 'DEN', city: 'Denver', region: 'Colorado', country: 'US', continent: 'NA', lat: 39.74, lon: -104.99,
          timezone: 'America/Denver', httpProtocol: 'HTTP/2', tlsVersion: 'TLSv1.3', time: new Date(now).toISOString() },
        pulse: {
          time: new Date(now - 120000).toISOString(), radar: true,
          riot: {
            asn: 6507, name: 'Riot Games',
            prefix: { prefix: '192.207.0.0/24', origin: 6507, rpki: 'Valid', peers: 1874, totalPeers: 1902 },
            traffic: series(24, function (hh) { return 40 + 55 * Math.max(0, Math.sin((hh - 9) / 15 * Math.PI)) + rnd() * 6; }),
            outages: [], anomalies: [], hijacks: [],
            leaks: [{ kind: 'leak', start: new Date(now - 2 * 86400000 - 5 * 3600000).toISOString(), end: new Date(now - 2 * 86400000 - 4 * 3600000 - 20 * 60000).toISOString(),
              title: 'Route leak seen for a Riot prefix', detail: 'AS64512 leaked 192.207.0.0/24 learned from AS1299 to its peers. 37 route collector peers saw it.', url: 'https://radar.cloudflare.com/routing/as6507' }]
          },
          isp: { asn: 64500, name: 'Demo ISP', traffic: series(24, function (hh) { return 30 + 70 * Math.max(0, Math.sin((hh - 7) / 17 * Math.PI)) + rnd() * 5; }),
            outages: [], anomalies: [], hijacks: [], leaks: [],
            upstreams: [{ asn: 174, name: 'Cogent Communications', rel: 'provider' }, { asn: 6939, name: 'Hurricane Electric LLC', rel: 'provider' }, { asn: 3356, name: 'Lumen (Level 3)', rel: 'peer' }] },
          country: { code: 'US', outages: [], iqi: { latency: { p25: 17, p50: 31, p75: 58 }, bandwidth: { p25: 22, p50: 86, p75: 210 } } }
        },
        riotStatus: null, riotStatusAgeSec: 40,
        refdata: { version: '2026.11.21-1', source: 'builtin', ageSec: null }
      },
      update: { state: 'upToDate', current: '2.1.0', available: null, progress: null, notes: '', releaseUrl: 'https://github.com/gecko-of-shadow/gecko-peeks-app/releases', error: '',
        lastCheckUtc: new Date(now - 2 * 3600000).toISOString(), autoInstall: true },
      settings: { startWithWindows: true, autoInstallUpdates: true, cloudEnabled: true, region: 'auto' }
    };
    st.region = mockRegion('na1', true, 'country');
    st.regions = REGIONS.map(function (r) { return { id: r[0], shortName: r[1], name: r[2], serverCity: r[8], cityConfirmed: r[9] }; });
    function setStatus() {
      st.cloud.riotStatus = mockRiotStatus(st.region,
        { id: st.region.lolStatusId.toUpperCase(), name: st.region.name, incidents: [], maintenances: [demoMaint] },
        { id: st.region.valorantShard.toUpperCase(), name: st.region.name, incidents: [], maintenances: [] });
    }
    setStatus();

    baseCloud = JSON.parse(JSON.stringify(st.cloud));

    // variants for design checks (?mock=<name>)
    var regionVariant = /^(na1|euw1|eun1|kr|jp1|br1|la1|la2|oc1|tr1|ru|me1|sg2|tw2|vn2)$/.test(variant) ? variant : '';
    if (regionVariant) {
      st.region = mockRegion(regionVariant, false, 'manual'); st.settings.region = regionVariant; setStatus();
    } else if (variant === 'empty') {
      st.probe = { paused: false, leagueRunning: false, testRunning: false, phase: 'Starting', mode: '', nextDueUtc: new Date(now + 40000).toISOString(),
        hasRoute: false, routeLine: '', lastHop: { avgMs: null, lossPct: null, figure: '—' }, riot: { avgMs: null, lossPct: null, figure: '—' }, hops: [] };
      st.history = []; st.cloud = { enabled: true, status: 'loading', edge: null, pulse: null, refdata: st.cloud.refdata };
      st.log = { path: '', chip: 'Logging', chipTone: 'normal', status: 'Waiting for the first test', statusTone: 'normal' };
      st.update = { state: 'idle', current: '2.1.0' };
      st.region = mockRegion('na1', true, 'default'); st.region.lastTestTarget = '';
    } else if (variant === 'cloudoff') {
      st.cloud = { enabled: false, status: 'off', edge: null, pulse: null, refdata: st.cloud.refdata }; st.settings.cloudEnabled = false;
      st.region = mockRegion('na1', true, 'saved');
    } else if (variant === 'clouderror') {
      st.cloud = { enabled: true, status: 'error', error: 'The Gecko Peeks service did not answer (timeout after 10 s).', edge: null, pulse: null, refdata: st.cloud.refdata };
    } else if (variant === 'nostatus') {
      st.cloud.riotStatus.valorant = null;
      st.cloud.riotStatus.lol.incidents = [{ id: 1002, type: 'incident', severity: 'warning', status: null, title: 'Demo: some players may be unable to log in',
        text: 'Demo text. We are looking into it.', platforms: ['windows'], createdAt: new Date(now - 50 * 60000).toISOString(), updatedAt: new Date(now - 12 * 60000).toISOString(), archiveAt: null },
        { id: 1003, type: 'incident', severity: 'critical', status: null, title: 'Demo: ranked games unavailable', text: null, platforms: [],
          createdAt: new Date(now - 30 * 60000).toISOString(), updatedAt: null, archiveAt: null }];
      st.cloud.pulse.isp.upstreams = null;
    } else if (variant === 'noups') {
      st.cloud.riotStatus = null; st.cloud.pulse.isp.upstreams = [];
      st.probe.upstreamSource = 'asn'; st.probe.hops[4].nameSource = 'asn'; st.probe.hops[4].host = '';
    } else if (variant === 'downloading') {
      st.update = { state: 'downloading', current: '2.1.0', available: '2.1.1', progress: 0.42, notes: 'Faster route map.\nFixes the countdown after sleep.', releaseUrl: st.update.releaseUrl };
    } else if (variant === 'ready') {
      st.update = { state: 'ready', current: '2.1.0', available: '2.1.1', progress: 1, notes: 'Faster route map.\nFixes the countdown after sleep.', releaseUrl: st.update.releaseUrl };
    } else if (variant === 'changed') {
      st.probe.changeLine = 'Route changed: HE → Riot Direct Chicago → Cogent → Riot Direct Chicago';
      st.probe.routeLine = 'Cogent → Riot Direct · Chicago'; st.probe.upstream = 'Cogent'; st.probe.upstreamAsn = 174;
      st.probe.changedHops = [4, 5];
      st.probe.hops[3] = mockHop({ n: 4, ip: '154.54.0.1', host: '', rttMs: 18, asn: 174, asName: 'Cogent Communications', network: 'Cogent', nameSource: 'asn', changed: true });
      st.probe.hops[4] = mockHop({ n: 5, ip: '154.54.1.1', host: 'be2000.ccr41.ord01.atlas.cogentco.com', rttMs: 41, asn: 174, asName: 'Cogent Communications', network: 'Cogent', nameSource: 'hostname', cityName: 'Chicago', changed: true });
    } else if (variant === 'regionswitch') {
      // The region was set to EUW after the last test (to the NA edge). No new test has run yet.
      st.region = mockRegion('euw1', false, 'manual'); st.settings.region = 'euw1'; setStatus();
    } else if (variant === 'devspent') {
      // Dev probe mode after its one test.
      st.app.profile = 'Dev'; st.probe.nextDueUtc = null; st.probe.noMoreTests = 'No more tests in this Dev run.';
    } else if (variant === 'interconnect') {
      // The handoff is named from an upstream router's mixed host name; its address is the upstream's.
      st.probe.upstream = 'Zayo'; st.probe.upstreamAsn = 6461; st.probe.upstreamAsns = [6461];
      st.probe.handoffNetwork = 'Telia'; st.probe.routeLine = 'Zayo → Telia · Chicago';
      st.probe.hops[3] = mockHop({ n: 4, ip: '203.0.113.4', host: 'ae1.core1.den1.us.zip.zayo.com', rttMs: 17, asn: 6461, asName: 'Zayo Bandwidth', network: 'Zayo', nameSource: 'hostname', cityName: 'Denver' });
      st.probe.hops[4] = mockHop({ n: 5, ip: '203.0.113.5', host: 'telia-ic-1.ord1.us.zip.zayo.com', rttMs: 38, asn: 6461, asName: 'Zayo Bandwidth', network: 'Zayo + Telia', nameSource: 'hostname', cityName: 'Chicago' });
      st.probe.hops.length = 5; st.probe.riot = { avgMs: null, lossPct: 100, figure: 'no reply' };
      st.cloud.pulse.isp.upstreams = [{ asn: 6461, name: 'Zayo Bandwidth', rel: 'provider' }, { asn: 174, name: 'Cogent Communications', rel: 'provider' }];
    } else if (variant === 'match' || variant === 'matchother') {
      // A League match: the game connected to the test target (match) or to another Riot address (matchother).
      st.probe.leagueRunning = true; st.probe.mode = 'Tests every 5 min';
      st.probe.matchServer = variant === 'match' ? '192.207.0.5:7363' : '192.207.0.9:7363';
      st.probe.matchSameAsTarget = variant === 'match';
      // League's own netlog ping (demo values), and the row paired with the shown test.
      st.probe.gamePing = { ms: 58, lossPct: 0, jitterMs: 2, atUtc: new Date(now - 4000).toISOString(), reconnect: false };
      st.probe.testGamePing = { ms: 57, lossPct: 0, jitterMs: 3, atUtc: new Date(now - 3 * 60000 + 20000).toISOString(), reconnect: false };
    } else if (variant === 'vpn') {
      st.probe.vpn = true; st.probe.adapter = 'WireGuard Tunnel'; st.probe.leagueRunning = true; st.probe.mode = 'Tests every 5 min';
      st.probe.nextDueUtc = new Date(now + 3 * 60000).toISOString();
    } else if (variant === 'testing') {
      st.probe.testRunning = true; st.probe.phase = 'Tracing route · hop 7 of 30';
    } else if (variant === 'paused') {
      st.probe.paused = true; st.log.chip = 'Logging paused'; st.log.chipTone = 'warn'; st.log.status = 'Paused by you'; st.log.statusTone = 'warn';
    }

    function push() { receive(JSON.parse(JSON.stringify(st))); }
    function later(ms, fn) { setTimeout(function () { fn(); push(); }, ms); }
    return {
      start: function () { setTimeout(push, 60); },
      handle: function (m) {
        m = obj(m);
        switch (m.cmd) {
          case 'runNow':
            if (st.probe.testRunning) return;
            st.probe.testRunning = true; st.probe.phase = 'Tracing route'; push();
            later(3500, function () {
              st.probe.testRunning = false; st.probe.phase = 'Waiting for the next test';
              var d = new Date();
              var avg = Math.round((45 + rnd() * 6) * 10) / 10;
              st.probe.lastHop = { avgMs: avg, lossPct: 0, figure: Math.round(avg) + ' ms' };
              st.probe.testTimestamp = csvLocal(d);
              st.probe.nextDueUtc = new Date(Date.now() + 15 * 60000).toISOString();
              st.history.push({ t: isoLocal(d), upstream: st.probe.upstream || 'HE', handoffNetwork: st.probe.handoffNetwork || 'Riot Direct', handoffCity: st.probe.handoffCity || 'chi01',
                handoffCityName: st.probe.handoffCityName || 'Chicago', lastHopAvg: avg, lastHopLoss: 0, riotAvg: null, riotLoss: 100, league: false, vpn: false, adapter: 'Ethernet' });
              toast('Test saved · last hop ' + Math.round(avg) + ' ms', 'good');
            });
            break;
          case 'togglePause':
            st.probe.paused = !st.probe.paused;
            st.log.chip = st.probe.paused ? 'Logging paused' : 'Logging · saved';
            st.log.chipTone = st.probe.paused ? 'warn' : 'good';
            st.log.status = st.probe.paused ? 'Paused by you' : 'Logging resumed';
            st.log.statusTone = st.probe.paused ? 'warn' : 'good';
            push(); toast(st.probe.paused ? 'Logging paused' : 'Logging resumed', st.probe.paused ? 'warn' : 'good');
            break;
          case 'copyDiagnosis': toast('Diagnosis copied to the clipboard', 'good'); break;
          case 'copyPath': toast('Log path copied', 'good'); break;
          case 'openLog': toast('Opening the log (mock)', 'normal'); break;
          case 'openFolder': toast('Opening the log folder (mock)', 'normal'); break;
          case 'setSetting':
            if (m.key in st.settings) st.settings[m.key] = !!m.value;
            if (m.key === 'cloudEnabled') {
              st.cloud.enabled = !!m.value;
              st.cloud.status = m.value ? 'loading' : 'off';
              push();
              if (m.value) later(1200, function () { st.cloud = createMockCloud(); });
            } else push();
            break;
          case 'refreshCloud':
            if (!st.cloud.enabled) return;
            st.cloud.status = 'loading'; push();
            later(1200, function () { st.cloud = createMockCloud(); });
            break;
          case 'checkUpdate':
            st.update.state = 'checking'; push();
            later(1200, function () { st.update.state = 'downloading'; st.update.available = '2.1.1'; st.update.progress = 0; st.update.notes = 'Faster route map.\nFixes the countdown after sleep.'; });
            [0.2, 0.45, 0.7, 0.9, 1].forEach(function (p, i) { later(1800 + i * 700, function () { st.update.progress = p; if (p >= 1) st.update.state = 'ready'; }); });
            break;
          case 'installUpdate':
            st.update.state = 'installing'; push(); toast('Restarting to update (mock)', 'normal');
            break;
          case 'setRegion':
            var rid = str(m.id).toLowerCase();
            if (rid === 'auto') { st.region = mockRegion('na1', true, 'country'); st.settings.region = 'auto'; }
            else if (REGIONS.some(function (x) { return x[0] === rid; })) { st.region = mockRegion(rid, false, 'manual'); st.settings.region = rid; }
            else { toast('Unknown region.', 'bad'); push(); break; }
            st.region.lastTestTarget = '192.207.0.5';
            if (st.cloud.riotStatus) setStatus();
            push();
            break;
          case 'ready': push(); break;
          default: break;
        }
      }
    };
    function createMockCloud() {
      var c = JSON.parse(JSON.stringify(baseCloud));
      c.pulseAgeSec = 0; c.edgeAgeSec = 0; c.status = 'ok'; c.enabled = true;
      return c;
    }
  }
  var baseCloud = null;

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
  else init();
})();
