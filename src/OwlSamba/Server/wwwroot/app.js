// OwlSamba web dashboard. Vanilla ES module, no build step. Mirrors the desktop app.
// Every value that can come from an attacker (user names, workstations, notes…) goes through esc().
import { ICONS } from "./icons.js";

const $ = (sel, el = document) => el.querySelector(sel);
const esc = (v) => String(v ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const icon = (name, cls = "") => `<svg class="i ${cls}" viewBox="0 0 24 24"><path d="${ICONS[name] ?? ""}"/></svg>`;
const n0 = (v) => Number(v ?? 0).toLocaleString("en-US");

// ------------------------------------------------------------------ formatting
const pad = (n) => String(n).padStart(2, "0");
function fmtTime(ms, full = false) {
  if (!ms) return "—";
  const d = new Date(ms), today = new Date();
  const hms = `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
  if (full) return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${hms}`;
  return d.toDateString() === today.toDateString() ? hms
    : `${d.toLocaleString("en-US", { month: "short" })} ${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}
function fmtAgo(ms) {
  if (!ms) return "—";
  const s = (Date.now() - ms) / 1000;
  if (s < 0) return "now";
  if (s < 60) return `${Math.floor(s)}s ago`;
  if (s < 3600) return `${Math.floor(s / 60)} min ago`;
  if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
  return `${Math.floor(s / 86400)} d ago`;
}
function fmtRemaining(until) {
  if (until == null) return "PERMANENT";
  const s = Math.floor((until - Date.now()) / 1000);
  if (s <= 0) return "expiring…";
  const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60);
  return h >= 1 ? `${h}h ${pad(m)}m` : `${pad(m)}:${pad(s % 60)}`;
}
const srcLabel = (s) => ({ security: "4625", smb: "SMB", web: "WEB" }[s] ?? s ?? "");
const kindLabel = (k, until) => k === "temp" ? "TEMPORARY" : k === "perm" ? "PERMANENT" : until == null ? "MANUAL · PERM" : "MANUAL";
const kindTone = (k, until) => until == null ? "r" : k === "manual" ? "o" : "y";
const toneColor = { y: "var(--accent)", r: "var(--danger)", o: "var(--warn)", g: "var(--ok)" };
const ipLink = (ip, cls = "") => `<button class="iplink ${cls}" data-ip="${esc(ip)}" title="Open in the IP explorer">${esc(ip)}</button>`;
const cc = (code) => code ? `<span class="cc">${esc(code)}</span>` : "";
const DURATIONS = [["15 minutes", 15], ["1 hour", 60], ["24 hours", 1440], ["7 days", 10080], ["30 days", 43200], ["Permanent", ""]];
const durationOptions = (sel = "") => DURATIONS.map(([l, v]) => `<option value="${v}" ${String(v) === String(sel) ? "selected" : ""}>${l}</option>`).join("");

// ------------------------------------------------------------------ API
async function api(path, opts = {}) {
  const init = { method: opts.method ?? "GET", credentials: "same-origin", headers: {} };
  if (init.method !== "GET") init.headers["X-OwlSamba"] = "1";
  if (opts.body !== undefined) { init.headers["Content-Type"] = "application/json"; init.body = JSON.stringify(opts.body); }
  const r = await fetch(path, init);
  if (r.status === 401 && !path.startsWith("/api/auth")) { showLogin(); throw new Error("Session expired"); }
  const text = await r.text();
  const data = text ? (() => { try { return JSON.parse(text); } catch { return text; } })() : null;
  if (!r.ok) throw new Error(data?.error ?? `Error ${r.status}`);
  return data;
}

function toast(msg, kind = "info") {
  const el = document.createElement("div");
  el.className = `toast ${kind}`;
  el.textContent = msg;
  $("#toasts").append(el);
  setTimeout(() => el.remove(), kind === "error" ? 8000 : 4500);
}

async function run(fn, ok) {
  try { await fn(); if (ok) toast(ok, "ok"); return true; }
  catch (e) { toast(e.message, "error"); return false; }
}

function confirmBox(title, text, okLabel = "OK", danger = false) {
  return new Promise((resolve) => {
    const bg = document.createElement("div");
    bg.className = "modal-bg";
    bg.innerHTML = `<div class="modal ${danger ? "danger" : ""}"><h3>${esc(title)}</h3><p>${esc(text)}</p>
      <div class="acts"><button class="btn" data-a="0">Cancel</button><button class="btn ${danger ? "danger" : "primary"}" data-a="1">${esc(okLabel)}</button></div></div>`;
    bg.addEventListener("click", (e) => {
      const a = e.target.closest("[data-a]")?.dataset.a ?? (e.target === bg ? "0" : null);
      if (a !== null) { bg.remove(); resolve(a === "1"); }
    });
    document.body.append(bg);
    $(".btn.primary, .btn.danger", bg).focus();
  });
}

// ------------------------------------------------------------------ state
const S = { user: null, host: "", version: "", status: null, page: null, cleanup: null, stream: null };

const PAGES = {
  dashboard: { title: "Dashboard", icon: "layout-dashboard", render: pageDashboard },
  activity: { title: "Live activity", icon: "radio", sub: "Every failed logon attempt, the instant it happens.", render: pageActivity },
  ips: { title: "IP explorer", icon: "search", sub: "Search any IP, review its history and ban or unban it manually.", render: pageIps },
  bans: { title: "Bans", icon: "shield-ban", sub: "Active Windows Firewall blocks and the full ban history.", render: pageBans },
  whitelist: { title: "Whitelist", icon: "shield-check", sub: "IPs, CIDR ranges and domains that are never banned. Loopback and the server's own IPs are always trusted.", render: pageWhitelist },
  audit: { title: "Audit log", icon: "scroll-text", sub: "Who banned, unbanned or changed the configuration, and when.", render: pageAudit },
  settings: { title: "Settings", icon: "settings", sub: "Ban policy, event sources, notifications and data.", render: pageSettings },
};
const NAV = [["MONITORING", ["dashboard", "activity", "ips"]], ["PROTECTION", ["bans", "whitelist", "audit"]], ["SYSTEM", ["settings"]]];
const listeners = new Set();   // live-event handlers of the current page

// ------------------------------------------------------------------ boot / auth
async function boot() {
  const st = await api("/api/auth/state");
  S.host = st.host; S.version = st.version;
  if (!st.authenticated) return showLogin(st);
  S.user = st.user;
  renderShell();
}

function showLogin(st) {
  S.stream?.close();
  $("#root").innerHTML = `<div class="login"><form autocomplete="on">
      <div class="brand"><img src="favicon.svg" alt=""> OWLSAMBA <span class="ver">v${esc(S.version)}</span></div>
      <div class="overline">${esc(S.host)}</div>
      ${st && !st.passwordSet ? `<div class="note warn" style="margin-top:16px">${icon("triangle-alert")}<div>No password set yet. Set one in the desktop app (Settings → Web dashboard).</div></div>` : ""}
      <label for="u">USERNAME</label><input id="u" type="text" autocomplete="username" required>
      <label for="p">PASSWORD</label><input id="p" type="password" autocomplete="current-password" required>
      <button class="btn primary" type="submit">${icon("lock")} SIGN IN</button>
      <div class="err" id="err"></div>
    </form></div>`;
  $("#u").focus();
  $(".login form").addEventListener("submit", async (e) => {
    e.preventDefault();
    $("#err").textContent = "";
    try {
      await api("/api/auth/login", { method: "POST", body: { username: $("#u").value, password: $("#p").value } });
      boot();
    } catch (err) { $("#err").textContent = err.message; $("#p").select(); }
  });
}

function renderShell() {
  $("#root").innerHTML = `<div class="app">
    <header class="topbar">
      <button class="btn ghost menu-btn" id="menu">${icon("layout-dashboard")}</button>
      <div class="brand"><img src="favicon.svg" alt=""> OWLSAMBA <span class="ver">v${esc(S.version)}</span></div>
      <div class="host">${icon("server")}<span>${esc(S.host)}</span></div>
      <span class="state" id="state">REAL-TIME PROTECTION</span>
      <div class="spacer"></div>
      <span class="user">${esc(S.user)}</span>
      <button class="btn ghost" id="logout" title="Sign out">${icon("log-out")}</button>
    </header>
    <nav class="sidebar" id="sidebar">${NAV.map(([h, keys]) => `<div class="nav-h">${h}</div><div class="nav">${keys.map((k) =>
      `<a href="#/${k}" data-k="${k}">${icon(PAGES[k].icon)}${PAGES[k].title}</a>`).join("")}</div>`).join("")}</nav>
    <main id="main"></main>
  </div>`;
  $("#logout").onclick = async () => { await api("/api/auth/logout", { method: "POST" }).catch(() => {}); location.hash = ""; boot(); };
  $("#menu").onclick = () => $("#sidebar").classList.toggle("open");
  document.addEventListener("click", (e) => {
    const b = e.target.closest(".iplink[data-ip]");
    if (b) { e.preventDefault(); location.hash = `#/ips/${encodeURIComponent(b.dataset.ip)}`; }
  });
  window.onhashchange = route;
  route();
  connectStream();
  refreshStatus();
  setInterval(refreshStatus, 10000);
}

async function refreshStatus() {
  try {
    S.status = await api("/api/status");
    const el = $("#state");
    if (!el) return;
    el.className = "state" + (S.status.banningEnabled ? "" : " mon");
    el.textContent = S.status.banningEnabled ? "REAL-TIME PROTECTION" : "MONITOR MODE (NO BANS)";
  } catch { /* handled by api() */ }
}

function connectStream() {
  S.stream?.close();
  const es = new EventSource("/api/stream");
  es.onmessage = (m) => { try { const e = JSON.parse(m.data); listeners.forEach((fn) => fn(e)); } catch { /* ignore */ } };
  es.onerror = () => { const el = $("#state"); if (el) { el.className = "state off"; el.textContent = "RECONNECTING…"; } };
  es.onopen = () => refreshStatus();
  S.stream = es;
}

function route() {
  const [, key = "dashboard", arg] = location.hash.split("/");
  const page = PAGES[key] ? key : "dashboard";
  document.querySelectorAll(".nav a").forEach((a) => a.classList.toggle("on", a.dataset.k === page));
  $("#sidebar")?.classList.remove("open");
  S.cleanup?.(); listeners.clear();
  const p = PAGES[page];
  const main = $("#main");
  main.innerHTML = `<div class="page-h"><h1>${p.title}</h1>${p.sub ? `<p>${p.sub}</p>` : ""}</div><div id="page"></div>`;
  S.cleanup = p.render($("#page"), arg ? decodeURIComponent(arg) : null) ?? null;
}
const onLive = (fn) => listeners.add(fn);
function debounce(fn, ms) { let t; return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); }; }

// ------------------------------------------------------------------ feed items
function feedItem(e) {
  if (e.type === "attempt" && e.attempt) {
    const a = e.attempt;
    return { kind: "attempt", at: a.occurredAt, ip: a.ip, cc: a.countryCode, title: a.user && a.user !== "-" ? a.user : "Failed attempt",
      detail: `${srcLabel(a.source)} · ${a.workstation ?? "-"}${a.whitelisted ? " · whitelisted" : ""}` };
  }
  if (e.type === "ban" && e.ban) return { kind: "ban", at: e.at, ip: e.ip, cc: e.ban.countryCode,
    title: e.ban.expiresAt == null ? "PERMANENT BAN" : `BANNED ${fmtRemaining(e.ban.expiresAt)}`, detail: e.ban.reason };
  if (e.type === "unban") return { kind: "unban", at: e.at, ip: e.ip, title: "UNBANNED", detail: e.message === "expired" ? "ban expired" : e.message };
  if (e.type === "alert") return { kind: "alert", at: e.at, ip: e.ip, title: "ALERT", detail: e.message };
  return null;
}
const FEED_ICON = { attempt: "fingerprint", ban: "shield-ban", unban: "lock-open", alert: "triangle-alert" };
const feedHtml = (f) => `<div class="fi"><span class="edge t-${f.kind}"></span>${icon(FEED_ICON[f.kind], "t-" + f.kind)}
  <div style="min-width:0"><div class="t">${esc(f.title)}</div><div class="s">${cc(f.cc)}${f.ip ? ipLink(f.ip) : ""}<span class="d">${f.detail ? " · " + esc(f.detail) : ""}</span></div></div>
  <div class="tm">${fmtTime(f.at)}</div></div>`;

// ================================================================== pages

function pageDashboard(el) {
  el.innerHTML = `
    <div class="grid tiles">
      <div class="tile chamfer"><div class="ic">${icon("fingerprint")}</div><div class="overline">FAILED ATTEMPTS · 24 H</div><div class="num" id="k-att">—</div><div class="dim" id="k-all"></div></div>
      <div class="tile chamfer"><div class="ic">${icon("globe")}</div><div class="overline">ATTACKING IPS · 24 H</div><div class="num" id="k-ips">—</div><div class="dim" id="k-last"></div></div>
      <div class="tile chamfer"><div class="ic">${icon("timer")}</div><div class="overline">TEMPORARY BLOCKS</div><div class="num y" id="k-temp">—</div><div class="dim" id="k-b24"></div></div>
      <div class="tile chamfer"><div class="ic red">${icon("shield-ban")}</div><div class="overline">PERMANENT BANS</div><div class="num r" id="k-perm">—</div><div class="dim">Blocked in the firewall</div></div>
    </div>
    <div class="grid dash">
      <div class="grid dash-left">
        <div class="card"><div class="card-t">Attempts per hour · last 24 h</div><div class="chart-wrap"><div class="chart" id="chart"></div></div></div>
        <div class="grid two">
          <div class="card"><div class="card-t">Top attacking IPs · 24 h</div><div id="top-ips"></div></div>
          <div class="card"><div class="card-t">Most targeted usernames · 24 h</div><div id="top-users"></div></div>
        </div>
      </div>
      <div class="card flush" style="display:flex;flex-direction:column;max-height:calc(100vh - 290px);min-height:420px">
        <div style="display:flex;align-items:center;padding:18px 18px 10px"><div class="card-t" style="margin:0">Live</div>
          <div style="margin-left:auto;display:flex;align-items:center;gap:6px"><span class="pulse"></span><span class="overline">STREAM</span></div></div>
        <div class="feed" id="feed"></div>
      </div>
    </div>`;
  let stats = null;
  const feed = [];
  const drawFeed = () => { $("#feed").innerHTML = feed.length ? feed.map(feedHtml).join("") : `<div class="empty">${icon("shield-check")}All quiet. Attempts will show up here instantly.</div>`; };
  const draw = () => {
    if (!stats) return;
    $("#k-att").textContent = n0(stats.attempts24h);
    $("#k-all").textContent = `All time: ${n0(stats.attemptsTotal)}`;
    $("#k-ips").textContent = n0(stats.uniqueIps24h);
    $("#k-last").textContent = `Last attempt: ${fmtAgo(stats.lastAttemptAt)}`;
    $("#k-temp").textContent = n0(stats.activeTempBans);
    $("#k-b24").textContent = `Bans in 24 h: ${n0(stats.bans24h)}`;
    $("#k-perm").textContent = n0(stats.permanentBans);
    const max = Math.max(1, ...stats.hourly);
    $("#chart").innerHTML = stats.hourly.map((v, i) => {
      const d = new Date(stats.hourlyStart + i * 3600000);
      return `<div class="bar" title="${pad(d.getHours())}:00 · ${v} attempt(s)"><i style="height:${v ? Math.max(3, v / max * 100) : 1}%"></i>${i % 3 === 2 ? `<span>${pad(d.getHours())}:00</span>` : ""}</div>`;
    }).join("");
    $("#top-ips").innerHTML = stats.topIps.length ? stats.topIps.map((t) => `<div class="rank">${cc(t.countryCode)}${ipLink(t.key)}<span class="n">${t.count}</span></div>`).join("")
      : `<div class="dim">No attempts in the last 24 hours</div>`;
    $("#top-users").innerHTML = stats.topUsers.length ? stats.topUsers.map((t) => `<div class="rank">${icon("user", "")}<span class="ellip">${esc(t.key)}</span><span class="n">${t.count}</span></div>`).join("")
      : `<div class="dim">No attempts in the last 24 hours</div>`;
  };
  const load = async () => { stats = await api("/api/stats"); draw(); };
  const reload = debounce(() => load().catch(() => {}), 2000);
  load().catch((e) => toast(e.message, "error"));
  api("/api/attempts?limit=60").then((list) => { list.forEach((a) => feed.push(feedItem({ type: "attempt", attempt: a }))); drawFeed(); }).catch(() => {});
  onLive((e) => {
    const f = feedItem(e);
    if (!f) return;
    feed.unshift(f); feed.length = Math.min(feed.length, 200); drawFeed();
    if (e.type === "attempt" && stats && !e.attempt.whitelisted) {
      stats.attempts24h++; stats.attemptsTotal++; stats.lastAttemptAt = e.attempt.occurredAt;
      const b = Math.floor((e.attempt.occurredAt - stats.hourlyStart) / 3600000);
      if (b >= 0 && b < 24) stats.hourly[b]++;
      draw();
    }
    reload();
  });
  const t = setInterval(draw, 5000);
  return () => clearInterval(t);
}

function pageActivity(el) {
  el.innerHTML = `
    <div class="toolbar">
      <div class="search">${icon("search")}<input type="text" id="q" placeholder="Filter by IP, user or workstation…"></div>
      <label class="toggle" style="margin:0"><input type="checkbox" id="live" checked><span class="tr"></span>Live</label>
      <label class="toggle" style="margin:0"><input type="checkbox" id="hidewl"><span class="tr"></span>Hide whitelisted</label>
      <button class="btn" id="refresh">${icon("refresh-cw")}Refresh</button>
    </div>
    <div class="card flush"><div class="tbl-wrap" style="max-height:calc(100vh - 250px)"><table>
      <thead><tr><th>TIME</th><th>IP</th><th>USER</th><th>WORKSTATION</th><th>SOURCE</th><th>NT STATUS</th><th></th></tr></thead>
      <tbody id="rows"></tbody></table></div>
      <div style="text-align:center;padding:6px"><button class="btn ghost" id="more" hidden>${icon("history")}Load older</button></div></div>`;
  let items = [];
  const row = (a) => `<tr><td class="mono muted">${fmtTime(a.occurredAt, true)}</td><td>${ipLink(a.ip)} ${cc(a.countryCode)}</td>
    <td>${esc(a.user)}</td><td>${esc(a.workstation)}</td><td><span class="pill">${srcLabel(a.source)}</span></td>
    <td class="mono dim">${esc(a.status)}</td><td>${a.whitelisted ? `<span class="pill g">WHITELISTED</span>` : ""}</td></tr>`;
  const visible = (a) => !($("#hidewl").checked && a.whitelisted);
  const draw = () => { $("#rows").innerHTML = items.filter(visible).map(row).join("") || `<tr><td colspan="7" class="empty">No attempts</td></tr>`; };
  const load = async (more = false) => {
    const q = encodeURIComponent($("#q").value.trim());
    const list = await api(`/api/attempts?limit=300&search=${q}${more && items.length ? `&beforeId=${items[items.length - 1].id}` : ""}`);
    items = more ? items.concat(list) : list;
    $("#more").hidden = list.length < 300;
    draw();
  };
  $("#q").oninput = debounce(() => load().catch((e) => toast(e.message, "error")), 350);
  $("#hidewl").onchange = draw;
  $("#refresh").onclick = () => load();
  $("#more").onclick = () => load(true);
  load().catch((e) => toast(e.message, "error"));
  onLive((e) => {
    if (e.type !== "attempt" || !$("#live").checked) return;
    const q = $("#q").value.trim().toLowerCase(), a = e.attempt;
    if (q && ![a.ip, a.user, a.workstation].some((v) => (v ?? "").toLowerCase().includes(q))) return;
    items.unshift(a); items.length = Math.min(items.length, 3000);
    if (visible(a)) $("#rows").insertAdjacentHTML("afterbegin", row(a));
  });
}

function pageIps(el, focusIp) {
  el.innerHTML = `
    <div class="grid ips">
      <div>
        <div class="toolbar">
          <div class="search">${icon("search")}<input type="text" id="q" placeholder="Search IP, user, workstation, country, ISP or note…"></div>
          <select id="status"><option value="all">All</option><option value="banned">Banned</option><option value="temp">Temporary</option><option value="perm">Permanent</option><option value="free">Not banned</option><option value="suspicious">Suspicious</option></select>
          <select id="sort"><option value="last_seen">Last activity</option><option value="attempts">Attempts</option><option value="first_seen">First seen</option><option value="ip">IP</option><option value="country">Country</option></select>
        </div>
        <div id="unknown"></div>
        <div class="card flush"><div class="tbl-wrap" style="max-height:calc(100vh - 300px)"><table>
          <thead><tr><th>IP</th><th>COUNTRY</th><th>ATTEMPTS</th><th>LAST USER</th><th>LAST SEEN</th></tr></thead><tbody id="rows"></tbody></table></div></div>
        <div style="display:flex;align-items:center;margin-top:10px"><span class="dim" id="pg"></span><span style="flex:1"></span>
          <button class="btn sm" id="prev">Previous</button>&nbsp;<button class="btn sm" id="next">Next</button></div>
      </div>
      <div class="card detail" id="detail"><div class="empty">${icon("search")}Select an IP to see its history</div></div>
    </div>`;
  let page = 0, total = 0, selected = focusIp;
  const PS = 100;
  if (focusIp) $("#q").value = focusIp;
  const load = async () => {
    const q = $("#q").value.trim(), sort = $("#sort").value;
    const r = await api(`/api/ips?q=${encodeURIComponent(q)}&status=${$("#status").value}&sort=${sort}&desc=${sort === "ip" || sort === "country" ? "false" : "true"}&page=${page}&pageSize=${PS}`);
    total = r.total;
    $("#rows").innerHTML = r.items.map((i) => {
      const banned = i.banKind && (i.banUntil == null || i.banUntil > Date.now());
      return `<tr class="click ${i.ip === selected ? "sel" : ""}" data-sel="${esc(i.ip)}">
        <td class="edge-l" style="border-left-color:${banned ? toneColor[kindTone(i.banKind, i.banUntil)] : "transparent"}"><span class="mono">${esc(i.ip)}</span>
          ${i.suspiciousAt ? `<span title="Successful logon after failed attempts" style="color:var(--warn)">${icon("triangle-alert")}</span>` : ""}</td>
        <td class="muted">${esc(i.country ?? "")}</td><td style="font:600 14px var(--display);color:var(--accent)">${i.totalAttempts}</td>
        <td>${esc(i.lastUser ?? "")}</td><td class="muted">${fmtAgo(i.lastSeen)}</td></tr>`;
    }).join("") || `<tr><td colspan="5" class="empty">No results</td></tr>`;
    $("#pg").textContent = total ? `${page * PS + 1}–${Math.min(total, (page + 1) * PS)} of ${total}` : "0 results";
    $("#prev").disabled = page === 0; $("#next").disabled = (page + 1) * PS >= total;
    const qv = $("#q").value.trim();
    $("#unknown").innerHTML = !r.items.length && /^(\d{1,3}\.){3}\d{1,3}$|^[0-9a-fA-F:]+:[0-9a-fA-F:]*$/.test(qv)
      ? `<div class="card" style="border-color:var(--accent);margin-bottom:14px;display:flex;gap:10px;align-items:center;flex-wrap:wrap">
          ${icon("info")}<span class="muted" style="flex:1">No records for <span class="mono" style="color:var(--text)">${esc(qv)}</span>. You can ban it preemptively.</span>
          <select id="u-dur">${durationOptions("")}</select><button class="btn danger" id="u-ban">${icon("ban")}Ban this IP</button></div>` : "";
    $("#u-ban")?.addEventListener("click", () => ban(qv, $("#u-dur").value, ""));
    if (selected && r.items.some((i) => i.ip === selected)) loadDetail(selected);
  };
  const ban = async (ip, minutes, reason) => {
    const label = minutes ? `for ${DURATIONS.find((d) => String(d[1]) === String(minutes))[0]}` : "permanently";
    if (!await confirmBox(`Ban ${ip} ${label}?`, "A block rule will be added to Windows Firewall and its open SMB sessions will be closed.", "BAN", true)) return;
    if (await run(() => api("/api/bans", { method: "POST", body: { ip, minutes: minutes ? Number(minutes) : null, reason } }), `${ip} banned`)) { selected = ip; load(); }
  };
  async function loadDetail(ip) {
    let d;
    try { d = await api(`/api/ips/${encodeURIComponent(ip)}`); } catch (e) { toast(e.message, "error"); return; }
    const r = d.record, banned = r.banKind && (r.banUntil == null || r.banUntil > Date.now());
    const state = banned ? `${kindLabel(r.banKind, r.banUntil)} · ${fmtRemaining(r.banUntil)}` : r.whitelisted ? "WHITELISTED" : "NOT BANNED";
    $("#detail").innerHTML = `
      <div style="display:flex;align-items:center"><div class="mono" style="font-size:20px;font-weight:600;flex:1;overflow-wrap:anywhere">${esc(r.ip)}</div>
        <button class="btn ghost" id="copy" title="Copy IP">${icon("copy")}</button></div>
      <span class="pill ${banned ? kindTone(r.banKind, r.banUntil) : r.whitelisted ? "g" : ""}" style="margin-top:8px">${state}</span>
      ${banned && r.banReason ? `<div class="dim" style="margin-top:6px">${esc(r.banReason)}</div>` : ""}
      <dl class="facts"><dt>Country</dt><dd>${esc(r.country ?? "—")}</dd><dt>ISP</dt><dd>${esc(r.isp ?? "—")}</dd>
        <dt>Attempts</dt><dd style="color:var(--accent);font:600 14px var(--display)">${r.totalAttempts}</dd>
        <dt>First seen</dt><dd>${fmtTime(r.firstSeen, true)}</dd><dt>Last seen</dt><dd>${fmtTime(r.lastSeen, true)}</dd>
        <dt>Last user</dt><dd>${esc(r.lastUser ?? "—")}</dd><dt>Times banned</dt><dd>${r.banCount}</dd></dl>
      <hr><div class="overline" style="margin-bottom:10px">ACTIONS</div>
      ${banned ? `<button class="btn primary" id="unban" style="width:100%">${icon("lock-open")}Unban</button>
          <label class="toggle" style="margin-top:10px"><input type="checkbox" id="reset" checked><span class="tr"></span>Reset attempt counter</label>`
        : `<div style="display:flex;gap:10px"><select id="dur" style="flex:1">${durationOptions("")}</select><button class="btn danger" id="ban">${icon("ban")}Ban</button></div>
          <input type="text" id="reason" placeholder="Reason (optional)" style="width:100%;margin-top:8px">`}
      ${r.whitelisted ? "" : `<button class="btn" id="wl" style="width:100%;margin-top:10px">${icon("shield-check")}Add to whitelist</button>`}
      <div class="overline" style="margin:20px 0 8px">NOTES</div>
      <textarea id="notes" rows="3" style="width:100%" placeholder="Internal notes about this IP…">${esc(r.notes ?? "")}</textarea>
      <div style="text-align:right;margin-top:6px"><button class="btn ghost sm" id="save">${icon("save")}Save note</button></div>
      <div class="overline" style="margin:16px 0 8px">BAN HISTORY</div>
      ${d.bans.length ? d.bans.map((b) => `<div style="border-left:2px solid ${toneColor[kindTone(b.kind, b.expiresAt)]};padding:4px 10px;margin-bottom:6px">
          <div style="font-size:12.5px">${fmtTime(b.startedAt, true)} <span class="muted">· ${esc(b.kind)}</span></div><div class="dim">${esc(b.reason)}</div></div>`).join("")
        : `<div class="dim">Never banned</div>`}
      <div class="overline" style="margin:16px 0 8px">RECENT ATTEMPTS</div>
      ${d.attempts.slice(0, 100).map((a) => `<div style="display:grid;grid-template-columns:140px 1fr auto;gap:8px;margin-bottom:5px;font-size:12px">
          <span class="mono dim">${fmtTime(a.occurredAt, true)}</span><span class="ellip">${esc(a.user)}</span><span class="dim">${srcLabel(a.source)}</span></div>`).join("")}`;
    $("#copy").onclick = () => navigator.clipboard?.writeText(r.ip).then(() => toast(`${r.ip} copied`, "ok"));
    $("#ban")?.addEventListener("click", () => ban(r.ip, $("#dur").value, $("#reason").value));
    $("#unban")?.addEventListener("click", async () => {
      if (await run(() => api("/api/unban", { method: "POST", body: { ip: r.ip, resetCounters: $("#reset").checked } }), `${r.ip} unbanned`)) load();
    });
    $("#wl")?.addEventListener("click", async () => {
      if (!await confirmBox(`Add ${r.ip} to the whitelist?`, "It will never be banned, and any current ban will be lifted.", "ADD")) return;
      if (await run(() => api("/api/whitelist", { method: "POST", body: { value: r.ip, note: "Added from the web dashboard" } }), `${r.ip} whitelisted`)) load();
    });
    $("#save").onclick = () => run(() => api("/api/ips/notes", { method: "POST", body: { ip: r.ip, notes: $("#notes").value } }), "Note saved");
  }
  $("#rows").onclick = (e) => {
    if (e.target.closest(".iplink")) return;
    const tr = e.target.closest("tr[data-sel]");
    if (!tr) return;
    selected = tr.dataset.sel;
    document.querySelectorAll("#rows tr").forEach((x) => x.classList.toggle("sel", x === tr));
    loadDetail(selected);
  };
  $("#q").oninput = debounce(() => { page = 0; load(); }, 350);
  $("#status").onchange = $("#sort").onchange = () => { page = 0; load(); };
  $("#prev").onclick = () => { page--; load(); };
  $("#next").onclick = () => { page++; load(); };
  load().catch((e) => toast(e.message, "error"));
  const reload = debounce(() => load().catch(() => {}), 2000);
  onLive((e) => { if (["attempt", "ban", "unban", "changed"].includes(e.type)) reload(); });
}

function pageBans(el) {
  el.innerHTML = `
    <div class="card" style="margin-bottom:18px"><div class="card-t">Manual ban</div>
      <div style="display:grid;grid-template-columns:minmax(160px,240px) 150px 1fr auto;gap:10px" class="ban-form">
        <input type="text" id="ip" class="mono" placeholder="IP (e.g. 203.0.113.10)"><select id="dur">${durationOptions("")}</select>
        <input type="text" id="reason" placeholder="Reason (optional)"><button class="btn danger" id="ban">${icon("ban")}Ban</button></div></div>
    <div class="toolbar"><div class="tabs"><button class="on" data-t="active" id="t-active">ACTIVE</button><button data-t="history">HISTORY</button></div>
      <span style="flex:1"></span>
      <label class="toggle" style="margin:0"><input type="checkbox" id="reset" checked><span class="tr"></span>Reset counters when unbanning</label>
      <button class="btn ghost" id="sync" title="Re-creates missing rules and removes stale ones">${icon("network")}Sync firewall</button>
      <button class="btn ghost" id="lift">${icon("lock-open")}Lift temporary</button></div>
    <div class="card flush" id="list"></div>`;
  let tab = "active", active = [];
  const drawActive = () => {
    if (tab !== "active") return;
    $("#t-active").textContent = `ACTIVE (${active.length})`;
    $("#list").innerHTML = active.length ? active.map((b) => {
      const tone = kindTone(b.kind, b.expiresAt), prog = b.expiresAt ? Math.min(1, (Date.now() - b.startedAt) / (b.expiresAt - b.startedAt)) : 1;
      return `<div class="ban-row"><span class="edge" style="background:${toneColor[tone]}"></span>
        <div style="padding-left:4px">${ipLink(b.ip)} ${cc(b.countryCode)}<div class="dim">${b.totalAttempts} attempts</div></div>
        <div><span class="pill ${tone}">${kindLabel(b.kind, b.expiresAt)}</span></div>
        <div style="min-width:0"><div class="ellip">${esc(b.reason)}</div><div class="dim">${fmtTime(b.startedAt)} · by ${esc(b.createdBy)}</div></div>
        <div><div class="rem" style="color:${toneColor[tone]}">${fmtRemaining(b.expiresAt)}</div><div class="progress"><i style="width:${prog * 100}%;background:${toneColor[tone]}"></i></div></div>
        <button class="btn" data-unban="${esc(b.ip)}">${icon("lock-open")}Unban</button></div>`;
    }).join("") : `<div class="empty">${icon("shield-check")}No IPs are blocked right now</div>`;
  };
  const load = async () => {
    if (tab === "active") { active = await api("/api/bans/active"); drawActive(); return; }
    const h = await api("/api/bans/history?limit=1000");
    $("#list").innerHTML = `<div class="tbl-wrap" style="max-height:calc(100vh - 330px)"><table><thead><tr><th>STARTED</th><th>IP</th><th>KIND</th><th>REASON</th><th>ENDED</th><th>END REASON</th><th>BY</th></tr></thead><tbody>
      ${h.map((b) => `<tr><td class="mono muted">${fmtTime(b.startedAt, true)}</td><td>${ipLink(b.ip)}</td><td><span class="pill ${kindTone(b.kind, b.expiresAt)}">${esc(b.kind)}</span></td>
        <td>${esc(b.reason)}</td><td class="muted">${fmtTime(b.endedAt)}</td><td class="muted">${esc(b.endReason ?? "")}</td><td class="muted">${esc(b.createdBy)}</td></tr>`).join("")}</tbody></table></div>`;
  };
  el.querySelectorAll(".tabs button").forEach((b) => b.onclick = () => {
    tab = b.dataset.t; el.querySelectorAll(".tabs button").forEach((x) => x.classList.toggle("on", x === b)); load();
  });
  $("#ban").onclick = async () => {
    const ip = $("#ip").value.trim(), m = $("#dur").value;
    if (!ip) return toast("Enter a valid IP (IPv4 or IPv6)", "error");
    if (await run(() => api("/api/bans", { method: "POST", body: { ip, minutes: m ? Number(m) : null, reason: $("#reason").value } }), `${ip} banned`)) {
      $("#ip").value = $("#reason").value = ""; load();
    }
  };
  $("#list").onclick = async (e) => {
    const b = e.target.closest("[data-unban]");
    if (!b) return;
    const ip = b.dataset.unban;
    if (await run(() => api("/api/unban", { method: "POST", body: { ip, resetCounters: $("#reset").checked } }), `${ip} unbanned`)) load();
  };
  $("#sync").onclick = () => run(async () => { const r = await api("/api/firewall/reconcile", { method: "POST" }); toast(`Firewall synced: ${r.added} rule(s) created, ${r.removed} removed`, "ok"); });
  $("#lift").onclick = async () => {
    const temps = active.filter((b) => b.expiresAt != null);
    if (!temps.length || !await confirmBox(`Lift ${temps.length} temporary ban(s)?`, "Permanent bans are not touched.", "UNBAN")) return;
    await run(async () => { for (const b of temps) await api("/api/unban", { method: "POST", body: { ip: b.ip, resetCounters: $("#reset").checked } }); }, "Temporary bans lifted");
    load();
  };
  load().catch((e) => toast(e.message, "error"));
  onLive((e) => {
    if (e.type === "ban" && e.ban) { active = [e.ban, ...active.filter((b) => b.ip !== e.ban.ip)]; drawActive(); }
    if (e.type === "unban") { active = active.filter((b) => b.ip !== e.ip); drawActive(); }
  });
  const t = setInterval(drawActive, 1000);
  return () => clearInterval(t);
}

function pageWhitelist(el) {
  el.innerHTML = `
    <div class="card" style="margin-bottom:18px"><div class="card-t">Add entry</div>
      <div style="display:grid;grid-template-columns:minmax(200px,300px) 1fr auto;gap:10px">
        <input type="text" id="v" class="mono" placeholder="192.168.1.20 · 192.168.0.0/24 · nas.mydomain.com">
        <input type="text" id="n" placeholder="Note (e.g. Office, NAS, VPN)"><button class="btn primary" id="add">${icon("plus")}Add</button></div>
      <div class="dim" style="margin-top:10px">Domains are re-resolved every 10 minutes. Whitelisting a banned IP lifts its ban automatically.</div></div>
    <div class="card flush" id="list"></div>`;
  const load = async () => {
    const items = await api("/api/whitelist");
    $("#list").innerHTML = items.length ? `<div class="tbl-wrap"><table><thead><tr><th>VALUE</th><th>KIND</th><th>NOTE</th><th>ADDED</th><th></th></tr></thead><tbody>
      ${items.map((w) => `<tr><td class="mono">${esc(w.value)}</td><td><span class="pill">${esc(w.kind)}</span></td><td>${esc(w.note ?? "")}</td>
        <td class="muted">${fmtTime(w.createdAt)}</td><td style="text-align:right"><button class="btn ghost sm" data-del="${w.id}" data-v="${esc(w.value)}" title="Remove">${icon("trash-2")}</button></td></tr>`).join("")}
      </tbody></table></div>` : `<div class="empty">${icon("list-checks")}The whitelist is empty</div>`;
  };
  $("#add").onclick = async () => {
    const value = $("#v").value.trim();
    if (!value) return;
    if (await run(() => api("/api/whitelist", { method: "POST", body: { value, note: $("#n").value } }), `${value} added`)) { $("#v").value = $("#n").value = ""; load(); }
  };
  $("#list").onclick = async (e) => {
    const b = e.target.closest("[data-del]");
    if (!b || !await confirmBox(`Remove ${b.dataset.v} from the whitelist?`, "It will be evaluated by the detection engine again.", "REMOVE")) return;
    if (await run(() => api(`/api/whitelist/${b.dataset.del}`, { method: "DELETE" }), `${b.dataset.v} removed`)) load();
  };
  load().catch((e) => toast(e.message, "error"));
}

function pageAudit(el) {
  el.innerHTML = `<div class="toolbar" style="justify-content:flex-end"><button class="btn" id="refresh">${icon("refresh-cw")}Refresh</button></div><div class="card flush" id="list"></div>`;
  const load = async () => {
    const items = await api("/api/audit?limit=1000");
    $("#list").innerHTML = `<div class="tbl-wrap" style="max-height:calc(100vh - 250px)"><table><thead><tr><th>DATE</th><th>ACTOR</th><th>ACTION</th><th>TARGET</th><th>DETAILS</th></tr></thead><tbody>
      ${items.map((a) => `<tr><td class="mono muted">${fmtTime(a.at, true)}</td><td>${esc(a.actor)}</td><td><span class="pill y">${esc(a.action)}</span></td>
        <td class="mono">${esc(a.target ?? "")}</td><td class="muted" title="${esc(a.details ?? "")}">${esc(a.details ?? "")}</td></tr>`).join("")}</tbody></table></div>`;
  };
  $("#refresh").onclick = load;
  load().catch((e) => toast(e.message, "error"));
}

function pageSettings(el) {
  const num = (k, label, hint) => `<div class="row"><div class="lbl">${label}${hint ? `<small>${hint}</small>` : ""}</div><input class="num" type="number" min="0" data-k="${k}"></div>`;
  const tog = (k, label, extra = "") => `<label class="toggle" ${extra}><input type="checkbox" data-k="${k}"><span class="tr"></span>${label}</label>`;
  el.innerHTML = `
    <div class="grid two" style="align-items:start">
      <div class="grid">
        <div class="card"><div class="card-t">Ban policy</div>
          ${num("burstThreshold", "Attempts before a temporary block", "Failures in a row that trigger the block")}
          ${num("burstWindowSeconds", "Detection window (seconds)", "Time span in which those failures must happen")}
          ${num("tempBanMinutes", "Temporary block duration (minutes)")}
          ${num("permanentBanThreshold", "Accumulated attempts for a permanent ban", "Attempts keep adding up across temporary blocks")}
          ${num("accumulationWindowHours", "Accumulation window (hours)", "0 = attempts never expire")}
          <div class="note" style="margin:2px 0 16px">${icon("zap")}<div id="summary"></div></div>
          ${tog("enableBanning", "Block in Windows Firewall (off = monitor only)")}
          ${tog("killSmbSessionsOnBan", "Close the IP's open SMB sessions when banning it", 'style="margin:0"')}</div>
        <div class="card"><div class="card-t">Real-time detection</div>
          ${tog("monitorSecurityLog", "Security log · event 4625 (failed logon)")}
          ${tog("monitorSmbServerLog", "SMBServer/Security · event 551 (SMB authentication failure)")}
          ${tog("autoEnableAuditPolicy", "Automatically enable failed-logon auditing")}
          ${tog("alertOnSuccessAfterFailures", "Alert when an IP with failures logs on successfully")}
          ${tog("banOnSuccessAfterFailures", "…and ban it permanently right away", 'style="margin-left:50px"')}
          ${num("successAfterFailuresLookbackMinutes", "Alert look-back window (minutes)")}
          <hr><div class="overline" style="margin-bottom:10px">SOURCE STATUS</div><div id="sources"></div></div>
      </div>
      <div class="grid">
        <div class="card"><div class="card-t">Notifications</div>
          ${tog("discordEnabled", "Send alerts to Discord")}
          <input type="url" data-k="discordWebhookUrl" class="mono" placeholder="https://discord.com/api/webhooks/…" style="width:100%;margin-bottom:12px;font-size:12px">
          ${tog("discordNotifyTempBans", "Temporary bans")}${tog("discordNotifyPermanentBans", "Permanent bans")}${tog("discordNotifySuspiciousLogons", "Suspicious successful logons")}
          <button class="btn" id="test">${icon("bell")}Send test</button></div>
        <div class="card"><div class="card-t">Geolocation</div>${tog("geoIpEnabled", "Show country and ISP of attacking IPs")}
          <div class="dim">Uses ip-api.com: attacking public IPs are sent to that service to look up their location. Private IPs are labeled locally.</div></div>
        <div class="card"><div class="card-t">Web dashboard</div>
          <div class="overline" style="margin-bottom:8px">CHANGE PASSWORD</div>
          <div style="display:grid;grid-template-columns:1fr 1fr;gap:10px"><input type="password" id="pw1" placeholder="New password (10+ chars)" autocomplete="new-password"><input type="password" id="pw2" placeholder="Repeat password" autocomplete="new-password"></div>
          <button class="btn" id="setpw" style="margin-top:10px">${icon("key-round")}Set password</button>
          <div class="dim" style="margin-top:10px">Port, HTTPS and enabling/disabling the dashboard are managed from the desktop app.</div></div>
        <div class="card"><div class="card-t">Data</div>
          ${num("retentionDays", "Keep attempts (days)", "IPs and bans are always kept")}
          <div class="overline" style="margin:4px 0 8px">EXPORT TO CSV</div>
          <div style="display:flex;gap:8px;flex-wrap:wrap"><a class="btn" href="/api/export/attempts?days=3650">${icon("file-down")}Attempts</a>
            <a class="btn" href="/api/export/ips">${icon("file-down")}IPs</a><a class="btn" href="/api/export/bans">${icon("file-down")}Bans</a>
            <a class="btn" href="/api/backup">${icon("download")}Download backup</a></div></div>
      </div>
    </div>
    <div class="card" id="savebar" hidden style="position:sticky;bottom:0;margin-top:14px;border-top:2px solid var(--accent);display:flex;align-items:center;gap:10px">
      ${icon("info")}<span style="flex:1">You have unsaved changes. They apply instantly, no service restart needed.</span>
      <button class="btn" id="discard">Discard</button><button class="btn primary" id="save">${icon("save")}Save &amp; apply</button></div>`;
  let loaded = null;
  const fields = el.querySelectorAll("[data-k]");
  const fill = (s) => fields.forEach((f) => { const v = s[f.dataset.k]; if (f.type === "checkbox") f.checked = !!v; else f.value = v ?? ""; });
  const read = () => { const s = { ...loaded }; fields.forEach((f) => { s[f.dataset.k] = f.type === "checkbox" ? f.checked : f.type === "number" ? Number(f.value) : f.value; }); return s; };
  const summary = () => {
    const s = read(), cycles = Math.max(0, Math.ceil(s.permanentBanThreshold / Math.max(1, s.burstThreshold)) - 1);
    $("#summary").textContent = `With these settings an attacker can only try ${s.burstThreshold} password(s) before being blocked for ${s.tempBanMinutes} min, ` +
      `and after ${s.permanentBanThreshold} accumulated attempts ${s.accumulationWindowHours ? `within ${s.accumulationWindowHours} h ` : "(never expiring) "}` +
      `it is banned forever (≈ ${cycles * s.tempBanMinutes} min of continuous attack).`;
  };
  const dirty = () => { $("#savebar").hidden = false; $("#savebar").style.display = "flex"; summary(); };
  const clean = () => { $("#savebar").hidden = true; $("#savebar").style.display = "none"; };
  const load = async () => {
    loaded = await api("/api/settings"); fill(loaded); summary(); clean();
    const st = await api("/api/status");
    $("#sources").innerHTML = st.sources.map((s) => `<div style="display:flex;gap:10px;align-items:center;margin-bottom:8px"><span class="dot ${s.active ? "g" : "r"}"></span>
        <div style="flex:1">${esc(s.name)}${s.error ? `<div class="dim" style="color:var(--danger)">${esc(s.error)}</div>` : ""}</div><span class="dim">${s.eventsSeen} events</span></div>`).join("") +
      `<div style="display:flex;gap:10px;align-items:center;margin-top:4px"><span class="dot ${st.auditPolicyOk ? "g" : "r"}"></span><span class="muted">${esc(st.auditPolicyInfo)}</span></div>
       <div style="display:flex;gap:10px;align-items:center;margin-top:8px"><span class="dot ${st.firewallAllProfilesOn ? "g" : "r"}"></span><span class="muted">${esc(st.firewallError ?? "Windows Firewall is on for all profiles")}</span></div>`;
  };
  fields.forEach((f) => f.addEventListener(f.type === "checkbox" ? "change" : "input", dirty));
  $("#discard").onclick = () => { fill(loaded); summary(); clean(); };
  $("#save").onclick = async () => {
    const s = read();
    if (!s.enableBanning && loaded.enableBanning && !await confirmBox("Disable banning?", "OwlSamba will only monitor and alert: no attacker will be blocked.", "DISABLE", true)) return;
    if (await run(async () => { loaded = await api("/api/settings", { method: "PUT", body: s }); }, "Settings saved and applied")) clean();
  };
  $("#test").onclick = () => run(() => api("/api/discord/test", { method: "POST", body: { url: $('[data-k="discordWebhookUrl"]').value } }), "Test message sent to Discord");
  $("#setpw").onclick = async () => {
    const a = $("#pw1").value, b = $("#pw2").value;
    if (a !== b) return toast("Passwords do not match", "error");
    if (await run(() => api("/api/settings/password", { method: "POST", body: { username: S.user, password: a } }), "Password updated — please sign in again")) setTimeout(boot, 1200);
  };
  load().catch((e) => toast(e.message, "error"));
}

boot().catch((e) => { $("#root").innerHTML = `<div class="empty">${esc(e.message)}</div>`; });
