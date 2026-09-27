// The viewer end to end: each page in a headless Chrome over the DevTools protocol,
// against a live server behind tools/sameorigin.py. One line per check, and a
// non-zero exit if any failed. Needs Chrome listening on --remote-debugging-port.
//
//   python3 tools/sameorigin.py WebMap/web http://your_ip:port 8766 &
//   flatpak run com.google.Chrome --headless=new --use-angle=swiftshader --enable-unsafe-swiftshader --remote-debugging-port=9334 \
//     --user-data-dir=/tmp/check-profile --window-size=1400,900 about:blank &
//   node tools/check.mjs http://127.0.0.1:8766 9334 [/tmp/shots]
//
// Given a directory, it also leaves a screenshot of each page there, as it stood
// after its checks.
//
// Headless Chrome has no hover, so what a mouse reaches by hovering is reached the
// way a finger reaches it: the Layers card opens on a tap, the names on their caret.
import { writeFileSync } from "node:fs";
const [BASE, PORT, OUT] = process.argv.slice(2);
if (!BASE || !PORT) { console.error("usage: node tools/check.mjs <proxy base> <devtools port> [screenshot dir]"); process.exit(2); }
const sleep = ms => new Promise(r => setTimeout(r, ms));
let failed = 0;
const check = (name, ok, detail = "") => { if (!ok) failed++; console.log(`${ok ? "ok  " : "FAIL"} ${name}${detail ? " -- " + detail : ""}`); };

async function open(path) {
  const tab = await (await fetch(`http://127.0.0.1:${PORT}/json/new?about:blank`, { method: "PUT" })).json();
  const ws = new WebSocket(tab.webSocketDebuggerUrl); await new Promise(r => ws.onopen = r);
  let id = 0; const pending = new Map(), errors = [], broken = [];
  ws.onmessage = e => { const m = JSON.parse(e.data);
    if (m.id && pending.has(m.id)) { pending.get(m.id)(m.result ?? m.error); pending.delete(m.id); }
    if (m.method === "Runtime.exceptionThrown") errors.push((m.params.exceptionDetails.exception?.description ?? m.params.exceptionDetails.text).split("\n")[0].slice(0, 200));
    if (m.method === "Network.responseReceived" && ["Document", "Script", "Stylesheet"].includes(m.params.type) && m.params.response.status >= 400)
      broken.push(m.params.response.status + " " + new URL(m.params.response.url).pathname); };
  const send = (method, params = {}) => new Promise(r => { pending.set(++id, r); ws.send(JSON.stringify({ id, method, params })); });
  // a probe that throws (a page global not declared yet) reads as nothing, not as the page's exception
  const ev = async expression => { const r = await send("Runtime.evaluate", { returnByValue: true, awaitPromise: true, expression }); return r.exceptionDetails ? undefined : r.result?.value; };
  const until = async (expression, ms = 45000) => { for (const t0 = Date.now(); Date.now() - t0 < ms; await sleep(250)) if (await ev(expression)) return true; return false; };
  // down and up in one spot: a tap, as index.html's endPtr tells one from a drag
  const tap = async (x, y) => { for (const type of ["mousePressed", "mouseReleased"]) await send("Input.dispatchMouseEvent", { type, x, y, button: "left", clickCount: 1 }); await sleep(300); };
  // pressed at one spot, carried to another in steps, released: a drag, as the pegman wants
  const drag = async (x0, y0, x1, y1) => {
    await send("Input.dispatchMouseEvent", { type: "mousePressed", x: x0, y: y0, button: "left", clickCount: 1 });
    for (let i = 1; i <= 8; i++) await send("Input.dispatchMouseEvent", { type: "mouseMoved", x: x0 + (x1 - x0) * i / 8, y: y0 + (y1 - y0) * i / 8, button: "left", buttons: 1 });
    await send("Input.dispatchMouseEvent", { type: "mouseReleased", x: x1, y: y1, button: "left", clickCount: 1 }); await sleep(300); };
  const click = async sel => { const p = await ev(`(() => { const el = document.querySelector(${JSON.stringify(sel)}); if (!el || !el.offsetParent) return null;
    const r = el.getBoundingClientRect(); return {x: r.left + r.width/2, y: r.top + r.height/2}; })()`); if (p) await tap(p.x, p.y); return !!p; };
  await send("Runtime.enable"); await send("Page.enable"); await send("Network.enable"); await send("Network.setCacheDisabled", { cacheDisabled: true });
  // every run from the defaults: no layers, names or sidebar remembered from the last
  await send("Storage.clearDataForOrigin", { origin: new URL(BASE).origin, storageTypes: "local_storage" });
  await send("Page.navigate", { url: BASE + path });
  const done = async name => {
    check(`${name}: no exceptions`, !errors.length, errors.join(" | "));
    check(`${name}: its page, scripts and styles all load`, !broken.length, broken.join(", "));
    if (OUT) writeFileSync(`${OUT}/${name}.png`, Buffer.from((await send("Page.captureScreenshot", { format: "png" })).data, "base64"));
    ws.close(); await fetch(`http://127.0.0.1:${PORT}/json/close/${tab.id}`);
  };
  return { ev, until, tap, click, drag, done, send };
}

async function map() {
  const p = await open("/");
  const drawn = await p.until(`document.querySelectorAll("#markers .marker").length`);
  check("map: draws its markers", drawn, `${await p.ev(`document.querySelectorAll("#markers .marker").length`)} markers`);

  // a mid zoom: the walked world fitted, then two steps in, by the map's own buttons
  const ready = await p.until(`FEATURES && FEATURES.length && LAYERS.ready("fog")`);
  if (ready) for (const b of ["#zoomFit", "#zoomIn", "#zoomIn"]) await p.click(b);
  const named = ready && await p.until(`NAMEVIEW && NAMEVIEW.scale === V.scale && NAMEBOXES.length`, 10000);
  check("map: sets the names at a mid zoom", named, `${await p.ev("NAMEBOXES.length")} names at zoom ${await p.ev("V.scale.toFixed(2)")}`);

  // a name clear of every marker and control, tapped where it was drawn
  const at = named ? await p.ev(`(() => {
    const r = stage.getBoundingClientRect(), dx = NAMEVIEW.tx - V.tx, dy = NAMEVIEW.ty - V.ty;
    for (const b of NAMEBOXES) {
      const x = r.left + (b.x0 + b.x1)/2 - dx, y = r.top + (b.y0 + b.y1)/2 - dy;
      const el = document.elementFromPoint(x, y);
      if (el && el.closest("#stage") && !el.closest(".marker, .mapctl, .layers, #mini, #namer")) return {x, y, name: b.f.name};
    }
    return null; })()`) : null;
  if (at) await p.tap(at.x, at.y);
  const card = !!at && await p.until(`!namer.hidden && (namer.querySelector(".nm") || {textContent: ""}).textContent.includes(${JSON.stringify(at ? at.name : "")})`, 15000);
  check("map: a tap on a name opens its place card", card, at ? `"${at.name}"` : "no name clear of the controls to tap");

  // the kinds of name, as a finger reaches them; one switched off leaves the map
  const kind = await p.ev(`NAMEBOXES.length ? NAMEBOXES[0].f.kind : null`);
  const group = await p.ev(`(NAME_GROUPS.flatMap(g => g[1]).find(r => r[2].includes(${JSON.stringify(kind)})) || [])[0]`);
  const opened = await p.click("#lcard") && await p.click("#namesCaret") && await p.until(`getComputedStyle(namesMenu).display !== "none"`, 3000);
  const off = opened && !!group && await p.click(`#namesMenu .frow[data-group="${group}"]`)
    && await p.until(`NAMEVIEW.scale === V.scale && !NAMEBOXES.some(b => b.f.kind === ${JSON.stringify(kind)})`, 10000);
  check("map: the names flyout opens on its caret and its switch takes a kind off", off, `${kind} in "${group}"${opened ? "" : ", flyout never opened"}`);
  await p.done("map");
}

async function portals() {
  const p = await open("/portals.html");
  const built = await p.until(`HUBS.length && LIST.length && SPOKES.length`);
  const h = await p.ev(`({hubs: HUBS.length, panels: document.querySelectorAll("#net .node.hub").length})`) || {};
  check("portals: builds its hubs, a panel each", built && h.hubs > 0 && h.panels === h.hubs, `${h.hubs} hubs, ${h.panels} panels`);
  // tagged, with no twin standing: it gets one dial line, to a hub not its own
  const d = await p.ev(`(() => {
    const ids = new Set(LIST.map(q => q.id));
    const loose = LIST.filter(q => q.name && !(q.to && q.to !== q.id && ids.has(q.to)));
    const own = q => HUBS.find(h => h.portals.includes(q));
    const wrong = loose.filter(q => SPOKES.filter(s => s.kind === "dial" && s.target === q.id).length !== (HUBS.some(h => h !== own(q)) ? 1 : 0));
    return {loose: loose.length, wrong: wrong.map(q => q.name)}; })()`);
  check("portals: one dashed spoke for each tagged portal standing unlinked", built && d && !d.wrong.length,
        d ? `${d.loose} standing unlinked` + (d.wrong.length ? `; wrong: ${d.wrong.join(", ")}` : "") : "");
  // a spoke row: the click opens the far side close up with the gate you arrive at lit
  const t = await p.ev(`(() => {
    const row = document.querySelector(".spokes a.go[data-go]"); if(!row) return null;
    row.click(); const lit = document.querySelector("#inMarks .mk.here");
    return {to: row.dataset.go, at: row.dataset.at, open: !document.getElementById("inside").hidden, key: IN && IN.key, lit: lit && lit.dataset.id, marks: document.querySelectorAll("#inMarks .mk:not(.pin)").length, page: location.pathname}; })()`);
  check("portals: a gate opens the far side close up, the arrival gate lit", built && (!t || (t.open && t.key === t.to && t.lit === t.at && t.marks > 0 && t.page.endsWith("portals.html"))),
        t ? `${t.marks} gates at ${t.key}, ${t.lit} lit` : "no linked gate to try");
  await p.done("portals");
}

async function world() {
  const p = await open("/world.html");
  const loaded = await p.until(`RECORDS.length && document.querySelectorAll("#lands tbody tr").length`);
  const w = await p.ev(`({records: RECORDS.length, lands: document.querySelectorAll("#lands tbody tr").length, glance: document.querySelectorAll("#glance .stat").length, spot: !document.getElementById("spot").hidden})`) || {};
  check("world: its records, lands and glance are built", loaded && w.records >= 6 && w.lands > 0 && w.glance >= 8 && w.spot, `${w.records} records, ${w.lands} lands shown`);
  await p.done("world");
}

async function players() {
  const want = (await (await fetch(BASE + "/stats/players")).json()).players.length;
  const p = await open("/players.html");
  const loaded = await p.until(`LOADED`);
  const got = await p.ev(`document.querySelectorAll("#list .row").length`);
  check("players: a row per player", loaded && got === want, `${got} rows for ${want} players`);
  // a row opens the player's page beside the roster
  const name = await p.ev(`(() => { const b = document.querySelector("#list .row"); if(!b) return null; b.click(); return b.dataset.name; })()`);
  const opened = name && await p.until(`app.classList.contains("picked") && document.querySelector("#dhead h2")`);   // the hash carries the pick, a tick later
  const t = opened ? await p.ev(`({shown: getComputedStyle(detail).visibility === "visible", head: document.querySelector("#dhead h2").textContent.trim(), figs: document.querySelectorAll("#dbody .fig").length})`) : null;
  check("players: a row opens the player's page", loaded && (!name || (opened && t.shown && t.head.startsWith(name) && t.figs > 0)), name ? `${name}, ${t ? t.figs : 0} figures` : "no rows");   // the head carries a class pill after the name
  // a player with a look, live or remembered, stands above the pentagon; with none on the
  // server, the page is handed one shaped like RigExporter.LookJson's, of the body alone
  const body = Object.values((await (await fetch(BASE + "/prefabs")).json()).prefabs || {}).find(e => e.c === "rig" && /^Player@body\d+$/.test(e.n));
  const look = body && { model: +body.n.slice(11), skin: [1, 0.82, 0.68], hair: [0.55, 0.32, 0.14], slots: {}, parts: [body.n] };
  const who = loaded && body ? await p.ev(`(() => {
    const live = q => q.online && (ONLINE.find(o => o.name === q.name) || {}).look;
    let q = DATA.find(x => x.look || live(x));
    if(!q && DATA.length){ q = DATA[DATA.length - 1]; q.look = ${JSON.stringify(look)}; }
    if(q) pick(q.name);
    return q && q.name; })()`) : null;
  const stood = !!who && await p.until(`VK.rig && VK.rig.children.length && !VK.el.hidden && VK.el.isConnected && VK.el.querySelector("canvas").clientWidth > 0`, 60000);
  check("players: a player with a look stands above the pentagon", !body || stood,
        body ? `${who}, ${await p.ev("VK.rig ? VK.rig.children.length : 0")} meshes, ${await p.ev(`VK.el.textContent`)}` : "no body in the library to draw");
  // the live tallies may hold no kitchen yet: a cook handed to the page shows its group and its trade
  const cook = loaded ? await p.ev(`(() => {
    const q = {name: "A cook", online: false, last_seen: 1, deaths: 0, dist_m: 0, pieces: 0,
      kitchen: {cooked: 143, burnt: 6, brewed: 12, honey: 3, smelted: 20, dishes: {CookedMeat: 90, Bread: 30, MeadHealthMinor: 12}}};
    DATA.push(q); render(); pick(q.name);
    const g = [...document.querySelectorAll("#dbody .group")].find(s => s.querySelector("h3").textContent === "Kitchen"), c = document.querySelector("#dhead .cls");
    return {figs: g ? g.querySelectorAll(".fig").length : 0, line: g && g.querySelector(".line") ? g.querySelector(".line").textContent : "", cls: c ? c.textContent : "", why: c ? c.title : ""}; })()`) : null;
  check("players: a cook's Kitchen group and trade", !!cook && cook.figs === 5 && /Cooked Meat/.test(cook.line) && cook.cls === "Cook", cook ? `${cook.cls || "no class"}: ${cook.why}` : "");
  await p.done("players");
}

// The 3D mode of the map at the world's start, which any played world has walked: in by
// the #3d link and by the pegman, out by the Map button. WebGL in a headless Chrome
// wants --use-angle=swiftshader --enable-unsafe-swiftshader.
async function view3d() {
  const c = await (await fetch(BASE + "/config")).json();
  const [x, , z] = String(c.world_start_pos || "0,0,0").split(",").map(Number);
  const p = await open("/");
  await p.until(`document.querySelectorAll("#markers .marker").length && LAYERS_READY`);
  const early = await p.ev(`performance.getEntriesByType("resource").some(e => /three|view3d/.test(e.name))`);
  check("3d: the map loads without three.js", early === false);
  await p.ev(`location.hash = "#3d=${Math.round(x)},${Math.round(z)}"`);
  const status = `document.getElementById("v3dStatus").textContent`;
  const ground = await p.until(`+document.getElementById("v3d").dataset.ground > 0`, 90000);
  check("3d: #3d stands you on the walked ground at the start", ground, await p.ev(status));
  const things = ground && await p.until(`+document.getElementById("v3d").dataset.objects > 0`, 30000);
  check("3d: the world's objects stand on it", things, await p.ev(status));
  await p.click("#v3dExit");
  const back = await p.until(`document.getElementById("v3d").hidden && location.hash.startsWith("#at=")`, 5000);
  check("3d: Map brings the map back where you stood", back, await p.ev("location.hash"));
  const at = await p.ev(`(() => { const q = MapCore.toPx(${x}, ${z}), r = stage.getBoundingClientRect(), b = pegman.getBoundingClientRect();
    return {x: r.left + V.tx + q.px*V.scale, y: r.top + V.ty + q.py*V.scale, bx: b.left + b.width/2, by: b.top + b.height/2}; })()`);
  if (at) await p.drag(at.bx, at.by, at.x, at.y);
  const dropped = !!at && await p.until(`IN3D && location.hash.startsWith("#3d=")`, 10000);
  check("3d: the pegman dropped on walked ground stands you there", dropped, await p.ev("location.hash"));
  await p.done("3d");
}

// The tour's own faults: a flight whose window is resized under it, a tour stopped
// mid-stop and started again, whose old stop wakes into the new tour, a player's card
// whose turntable must stop as the card goes, a cycle of too few kinds, a street beat off
// walked ground or off eye height, a 3D view that keeps what the tour has left, and a stop
// that leaves anything behind.
async function tour() {
  const c = await (await fetch(BASE + "/config")).json();
  const [x, , z] = String(c.world_start_pos || "0,0,0").split(",").map(Number);
  const p = await open("/");
  await p.until(`FEATURES && FEATURES.length && LAYERS_READY && PIECES_ALL`);
  await p.send("Emulation.setDeviceMetricsOverride", { width: 1400, height: 900, deviceScaleFactor: 1, mobile: false });
  await p.ev(`void (TOUR.on = true, flyTo(1000, 1000, 4, 2000).then(() => { const q = V.at(stage.clientWidth/2, stage.clientHeight/2); window.__off = Math.hypot(q.px - 1000, q.py - 1000)*4; }))`);
  await sleep(700);
  await p.send("Emulation.setDeviceMetricsOverride", { width: 1000, height: 700, deviceScaleFactor: 1, mobile: false });
  const off = await p.until(`window.__off !== undefined`, 10000) && await p.ev(`window.__off`);
  check("tour: a flight resized under it lands on its spot", off !== false && off < 2, `${off.toFixed ? off.toFixed(0) : off} px off`);
  await p.ev(`void (TOUR.on = false, HIDDEN.add("portal"), applyHidden(), startTour(), TOUR.run++,
    window.__old = tourStop({kind: "", title: "", line: "", x: ${x}, z: ${z}, zoom: 30, dist: 100, layer: "portal"}, TOUR.run).then(() => window.__woke = true))`);
  const circling = await p.until(`IN3D && V3D && V3D.spin > 0`, 60000);
  await p.ev(`void (stopTour(), HIDDEN.delete("portal"), applyHidden(), TOUR.on = true, TOUR.run++, enter3D(${x}, ${z}, 0).then(() => { V3D.setMode("orbit"); V3D.setSpin(6); }))`);
  const woke = circling && await p.until(`window.__woke`, 30000);
  const after = await p.ev(`({spin: V3D && V3D.spin, portals: !HIDDEN.has("portal")})`) || {};
  check("tour: a stop of a stopped tour leaves the next tour and the layers alone", woke && after.spin === 6 && after.portals, JSON.stringify(after));
  await p.ev(`stopTour()`);
  // a player's card, built for a roster entry directly since nobody need be online; one the
  // server remembers no look for is handed the body alone, as on the players page
  const body = Object.values((await (await fetch(BASE + "/prefabs")).json()).prefabs || {}).find(e => e.c === "rig" && /^Player@body\d+$/.test(e.n));
  const look = body && { model: +body.n.slice(11), skin: [1, 0.82, 0.68], hair: [0.55, 0.32, 0.14], slots: {}, parts: [body.n] };
  const who = await p.until(`STATS && STATS.players && STATS.players.length`, 20000) && await p.ev(`(() => {
    const P = STATS.players, q = P.find(x => x.look && PlayerCard.classify(x, P)) || P.find(x => PlayerCard.classify(x, P)) || P[0];
    if(!q.look) q.look = ${JSON.stringify(look || null)};
    showPlayerCard(q.name); return q.name; })()`);
  const stood = !!who && !!body && await p.until(`VIKING && VIKING.rig && VIKING.raf && pcard.contains(VIKING.el) && VIKING.el.querySelector("canvas").clientWidth > 0`, 60000);
  const shown = await p.ev(`({pill: !!pcard.querySelector(".ph .cls"), radar: !!pcard.querySelector(".pr svg.radar .fill")})`) || {};
  const frames = async () => { const f0 = await p.ev(`VIKING ? VIKING.v.renderer.info.render.frame : 0`); await sleep(600); return await p.ev(`VIKING ? VIKING.v.renderer.info.render.frame : 0`) - f0; };
  const spun = stood ? await frames() : 0;
  await p.ev(`dropPlayerCard()`);
  const gone = await p.until(`pcard.hidden && (!VIKING || (!VIKING.raf && !VIKING.rig))`, 3000), still = stood ? await frames() : 0;
  check("tour: a player's card shows their class, pentagon and turning Viking, and stops turning as it goes",
        !!who && shown.pill && shown.radar && (!body || spun > 0) && gone && still === 0,
        `${who}: ${shown.pill ? "pill" : "no pill"}, ${shown.radar ? "radar" : "no radar"}, ${body ? `${spun} frames turning, ${still} after` : "no body in the library to draw"}`);
  const kinds = await p.ev(`tourPlan().then(plan => [...new Set(plan.filter(s => !s.figure).map(s => s.group))])`) || [];
  check("tour: a cycle draws from at least eight kinds of place", kinds.length >= 8, kinds.join(", "));
  // a stand at eye height, played as the tour plays it, a little faster
  const street = await p.ev(`(async () => {
    for(const s of [...tourPool().values()].flat().sort(() => Math.random() - .5)) for(const m of ["yard", "gates", "seaward", "peakdown"]) if((s.moves || []).includes(m)){
      const sh = await SHOTS[m](s); if(!sh) continue;
      sh.move = m; s.shot = sh; startTour(); TOUR.run++; TOUR.pace = 3; tourStop(s, TOUR.run, null);
      return s.kind + " / " + s.title + " (" + m + ")"; }
    return null; })()`);
  const stood3d = !!street && await p.until(`IN3D && V3D.running && V3D.mode === "street" && V3D.moves.length`, 60000);
  const eye = stood3d ? await p.ev(`(() => { const w = walkedFn(); return {walked: w(V3D.me.x, V3D.me.z), eye: +(V3D.me.y - V3D.standAt(V3D.me.x, V3D.me.z)).toFixed(2)}; })()`) : null;
  check("tour: a street beat stands on walked ground at eye height", stood3d && eye.walked && Math.abs(eye.eye - 1.8) < 0.25, `${street || "no stand in this world"}: ${JSON.stringify(eye)}`);
  await p.ev(`stopTour()`);
  // Two whole cycles, fast: what the 3D view holds levels off rather than growing with every
  // place built ahead; then a stop puts every layer, the sidebar and the 3D view back.
  const was = `({hidden: [...HIDDEN].sort().join(), details: [...DETAILS].sort().join(), off: [...OFF].sort().join(), side: document.body.classList.contains("side-open")})`;
  const before = await p.ev(was);
  await p.ev(`TOUR.pace = 8; TOUR.cycles = 0; startTour()`);
  const per = [];
  for (const t0 = Date.now(); Date.now() - t0 < 1200000; await sleep(1000)) {
    const m = await p.ev(`({c: TOUR.cycles, g: V3D ? V3D.renderer.info.memory.geometries : 0, t: V3D ? V3D.renderer.info.memory.textures : 0, n: V3D ? V3D.chunks.size : 0})`);
    if (!m || m.c >= 2) break;
    const r = per[m.c] || (per[m.c] = { g: 0, t: 0, n: 0 });
    r.g = Math.max(r.g, m.g); r.t = Math.max(r.t, m.t); r.n = Math.max(r.n, m.n);
  }
  const [c1, c2] = per;
  check("tour: across two cycles the 3D view's geometries and textures level off", !!(c1 && c2) && c2.g <= c1.g*1.3 + 300 && c2.t <= c1.t*1.3 + 60 && c2.n <= 1600,
        per.map((r, i) => `cycle ${i + 1}: at most ${r.g} geometries, ${r.t} textures, ${r.n} chunks`).join("; "));
  await p.ev(`stopTour()`); await sleep(900);
  const left = await p.ev(`Object.assign(${was}, {spin: V3D.spin, dist: V3D.orbitDist, mode: V3D.mode, eye: V3D.eye, ahead: V3D.ahead.length, jobs: V3D.jobs.length, moves: V3D.moves.length, held: V3D.held,
    in3d: IN3D, tour: document.body.classList.contains("tour"), cap: tourCap.hidden, fig: tourFig.hidden, card: pcard.hidden})`) || {};
  const back = ["hidden", "details", "off", "side"].every(k => left[k] === before[k]);
  check("tour: stopping it puts back every layer, the sidebar, the 3D view's camera and clock, and lets what it was building go",
        back && left.spin === 0 && left.dist === 140 && left.mode === "street" && left.eye === 1.8 && !left.ahead && !left.jobs && !left.moves && left.held === null && !left.in3d && !left.tour && left.cap && left.fig && left.card,
        JSON.stringify(left));
  await p.done("tour");
}

async function plan() {
  const p = await open("/plan.html");
  const ok = await p.until(`RASTER.ready("base") && RASTER.ready("fog") && PIECES_ALL && PIECES_ALL.length`);
  check("plan: loads the world and its builds", ok, `${await p.ev("PIECES_ALL && PIECES_ALL.length")} pieces`);
  await p.done("plan");
}

for (const page of [map, view3d, tour, world, portals, players, plan]) {
  try { await page(); } catch (e) { check(`${page.name}: runs`, false, e.message); }
}
process.exit(failed ? 1 : 0);
