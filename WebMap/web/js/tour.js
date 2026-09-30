/* tour.js — the map's cinematic: the world's places one after another, flown to on the map and
 * seen in 3D. init is handed the stage and its view, the rasters, the world as the page holds it,
 * the 3D view, the legend, the spotlight's figures and the page's redraw. One global, as map-core.js.
 */
const Tour = (() => {
"use strict";
const GEOM = MapCore.geom, PLAN_ZOOM = MapCore.PLAN_ZOOM, toPx = MapCore.toPx, esc = MapCore.esc, num = PlayerCard.num;
let stage, V, LAYERS, API, world, street, legend, figures, drawRaster, closeCard;
let tourBtn, tourCap, tourFig, pcard;

// ---------- the tour ----------
// A cinematic for a screen left open: the world's places one after another -- who is on, the
// hubs and the loneliest gate, the biggest builds and each builder's own, where people die, the
// graves, the traders, every boat and cart, the kitchen, the lands, peaks, ridges, lakes, bays
// and shores, the world's own locations, where people go -- each flown to on the map with the
// layer that makes it worth seeing, then seen in 3D where the ground is walked, by a move of its
// kind: circled, craned, dollied, flown over, or stood in at eye height. Every few stops, one of
// the world's figures. Any touch ends it; what it switched on is switched back.
// in3d: the stop whose beat is playing in 3D now, which the checks wait on rather than a clock
const TOUR = {on: false, run: 0, keep: null, pace: 1, seen: new Map(), kinds: new Map(), n: 0, cycles: 0, fig: 0, move: null, now: null, in3d: null};
const sleepT = ms => new Promise(r => setTimeout(r, ms));
const beat = ms => sleepT(ms/TOUR.pace);          // the checks run the tour faster than anyone watches it
const pick = a => a[Math.floor(Math.random()*a.length)], within = z => Array.isArray(z) ? z[0] + Math.random()*(z[1] - z[0]) : z;
const D2R = Math.PI/180, EYE_M = 1.8;
const kmOf = m => m >= 1000 ? (m/1000).toFixed(1) + " km" : Math.round(m) + " m";
const whose = n => n + (/s$/i.test(n) ? "'" : "'s");
const headingTo = (a, b) => (Math.atan2(b.x - a.x, b.z - a.z)/D2R + 360) % 360;
function flyTo(px, py, scale, ms){
  ms /= TOUR.pace;
  return new Promise(res => {
    const s0 = V.scale, c0 = V.at(stage.clientWidth/2, stage.clientHeight/2), t0 = performance.now(), run = TOUR.run;
    const step = () => {
      const t = Math.min(1, (performance.now() - t0)/ms), e = t < .5 ? 2*t*t : 1 - Math.pow(-2*t + 2, 2)/2;
      const s = Math.exp(Math.log(s0) + (Math.log(scale) - Math.log(s0))*e), cx = c0.px + (px - c0.px)*e, cy = c0.py + (py - c0.py)*e;
      // the stage measured each frame: a window resized in flight still lands on the spot
      V.scale = s; V.tx = stage.clientWidth/2 - cx*s; V.ty = stage.clientHeight/2 - cy*s; V.request();
      if(t < 1 && TOUR.on && TOUR.run === run) requestAnimationFrame(step); else res();   // a stopped tour lets its flight go
    };
    requestAnimationFrame(step);
  });
}
// the most particular named place a point lies in
function nameAt(x, z){
  let best = null;
  for(const f of world.features || []){
    if(f.kind === "river" || f.w == null || !f.name) continue;
    if(x < f.x - f.w/2 || x > f.x + f.w/2 || z < f.z - f.h/2 || z > f.z + f.h/2) continue;
    if(!best || f.w*f.h < best.w*best.h) best = f;
  }
  return best ? best.name : null;
}

// ---------- what the tour reads of the world ----------
const wOf = p => MapCore.toWorld(p.px, p.py);
// the fog as a question in world metres; null until it has decoded
function walkedFn(){ const fog = MapCore.explored(LAYERS.imgs.fog); return fog && ((x, z) => { const p = toPx(x, z); return fog(p.px, p.py); }); }
// things gathered into cells, the fullest first
function cellsOf(list, size){
  const m = new Map();
  for(const it of list){ const w = wOf(it), k = Math.floor(w.x/size) + "," + Math.floor(w.z/size); const c = m.get(k) || {n: 0, x: 0, z: 0, items: []}; c.n++; c.x += w.x; c.z += w.z; c.items.push(it); m.set(k, c); }
  return [...m.values()].map(c => ({n: c.n, x: c.x/c.n, z: c.z/c.n, items: c.items})).sort((a, b) => b.n - a.n);
}
const tally = (items, key) => { const by = {}; items.forEach(i => { by[i[key]] = (by[i[key]] || 0) + 1; }); return Object.entries(by).sort((a, b) => b[1] - a[1]); };
// The chart as a question: the biome at a world spot, "sea" for water; null until it has loaded.
const CHART_INK = [["Meadows", 146, 167, 92], ["Black Forest", 107, 116, 63], ["Swamp", 163, 114, 88], ["Mountain", 236, 238, 240], ["Plains", 231, 171, 120],
                   ["Mistlands", 92, 56, 102], ["Ashlands", 176, 49, 49], ["Deep North", 230, 236, 242], ["sea", 43, 79, 122]];
let CHARTQ = null;
function chartAt(x, z){
  const img = LAYERS.imgs.chart;
  if(!(img && img.complete && img.naturalWidth)) return null;
  if(!CHARTQ || CHARTQ.src !== img.src){
    const N = 1024, c = document.createElement("canvas"); c.width = c.height = N;
    const g = c.getContext("2d", {willReadFrequently: true}); g.imageSmoothingEnabled = false; g.drawImage(img, 0, 0, N, N);
    try{ CHARTQ = {src: img.src, N, d: g.getImageData(0, 0, N, N).data}; }catch(e){ return null; }
  }
  const {N, d} = CHARTQ, p = toPx(x, z), i = Math.min(N - 1, Math.max(0, (p.px*N/GEOM.size)|0)), j = Math.min(N - 1, Math.max(0, (p.py*N/GEOM.size)|0)), o = (j*N + i)*4;
  let best = null, bd = Infinity;
  for(const [n, r, g, b] of CHART_INK){ const dd = (d[o] - r)**2 + (d[o + 1] - g)**2 + (d[o + 2] - b)**2; if(dd < bd){ bd = dd; best = n; } }
  return best;
}
// Walked land beside the sea in a box, a chart cell apart, each with the heading that faces the water.
function shoreIn(x0, z0, x1, z1, walked, step = 24){
  const out = [];
  for(let z = z0; z <= z1; z += step) for(let x = x0; x <= x1; x += step){
    if(!walked(x, z) || chartAt(x, z) === "sea") continue;
    let sx = 0, sz = 0;
    for(const [dx, dz] of [[step, 0], [-step, 0], [0, step], [0, -step]]) if(chartAt(x + dx, z + dz) === "sea"){ sx += dx; sz += dz; }
    if(sx || sz) out.push({x, z, sea: (Math.atan2(sx, sz)/D2R + 360) % 360});
  }
  return out;
}
// The pieces as world points in 16 m buckets, for a street beat to stand clear of walls: near
// counts them within r (only what stands up -- walls, poles, props -- when solid), centre is their middle.
let PGRID = null;
function pieceGrid(){
  if(!world.pieces) return null;
  if(PGRID && PGRID.src === world.pieces) return PGRID;
  const m = new Map();
  for(const p of world.pieces){ const w = wOf(p), k = Math.floor(w.x/16) + "," + Math.floor(w.z/16); let b = m.get(k); if(!b) m.set(k, b = []); b.push(w.x, w.z, p.kind === "floor" || p.kind === "roof" ? 0 : 1); }
  const each = (x, z, r, f) => {
    for(let i = Math.floor((x - r)/16); i <= Math.floor((x + r)/16); i++) for(let j = Math.floor((z - r)/16); j <= Math.floor((z + r)/16); j++){
      const b = m.get(i + "," + j); if(b) for(let k = 0; k < b.length; k += 3) if(Math.hypot(b[k] - x, b[k + 1] - z) < r) f(b[k], b[k + 1], b[k + 2]); } };
  const near = (x, z, r, solid) => { let n = 0; each(x, z, r, (px, pz, s) => { if(s || !solid) n++; }); return n; };
  const centre = (x, z, r) => { let n = 0, sx = 0, sz = 0; each(x, z, r, (px, pz) => { n++; sx += px; sz += pz; }); return n ? {x: sx/n, z: sz/n} : null; };
  return PGRID = {src: world.pieces, near, centre};
}
// the world's locations in walked ground, from a mod that tells them; a minute old at most
let LOCATIONS = [], LOCATIONS_AT = 0;
async function fetchLocations(){
  if(Date.now() - LOCATIONS_AT < 60000) return;
  LOCATIONS_AT = Date.now();
  try{ const j = await MapCore.fetchJSON(API, "/locations"); LOCATIONS = (j && Array.isArray(j.locations)) ? j.locations : []; }catch(e){ LOCATIONS = []; }
}
// The locations by prefab, in plain words: [pattern, the group the tour draws through, one of them,
// several]. A prefab no row knows is named by its own words; a null group is never a stop -- shown
// elsewhere on the tour, or too small to visit.
const LOC = [
  [/^(Eikthyrnir|GDKing|Bonemass|Dragonqueen|GoblinKing|Mistlands_DvergrBossEntrance|Fader)/, "altar", "A boss's altar", "boss altars"],
  [/^Vegvisir/, "runestone", "A Vegvisir", "Vegvisirs"],
  [/^Runestone/, "runestone", "A runestone", "runestones"],
  [/^Crypt\d/, "crypt", "A burial chamber", "burial chambers"],
  [/^SunkenCrypt/, "sunken", "A sunken crypt", "sunken crypts"],
  [/^TrollCave/, "cave", "A troll cave", "troll caves"],
  [/^MountainCave/, "cave", "A frost cave", "frost caves"],
  [/^GoblinCamp/, "fuling", "A fuling village", "fuling villages"],
  [/^Mistlands_DvergrTownEntrance/, "mine", "An infested mine", "infested mines"],
  [/^Mistlands_(GuardTower|Lighthouse|Harbour|RoadPost)/, "dvergr", "A dvergr outpost", "dvergr outposts"],
  [/^Mistlands_Excavation/, "dvergr", "A dvergr dig", "dvergr digs"],
  [/Refinery/i, "dvergr", "An eitr refinery", "eitr refineries"],
  [/^TarPit/, "tarpit", "A tar pit", "tar pits"],
  [/^(WoodVillage|WoodFarm)/, "ruin", "An abandoned village", "abandoned villages"],
  [/^(Ruin\d|StoneTower|SwampRuin|AbandonedLogCabin|Charred|FortressRuins)/, "ruin", "Ruins", "ruins"],
  [/^(ShipWreck|FrozenShip)/, "wreck", "A wreck", "wrecks"],
  [/^(StoneCircle|StoneHenge|ShipSetting|Dolmen)/, "circle", "A stone circle", "stone circles"],
  [/^Hildir_(cave|crypt|plainsfortress)/, "hildir", "Where Hildir's chest lies", "of Hildir's hideouts"],
  [/^Mistlands_(Giant|Swords|Statue|Viaduct|RockSpire)/, "mist", "A wonder of the mist", "wonders of the mist"],
  [/^(StartTemple|Vendor_|Hildir_camp|BogWitch|Waymarker|WoodHouse|StoneHouse|SwampHut|Greydwarf|DrakeNest|FireHole|MountainGrave|MountainWell|SwampWell|BigRockClearing|Grave\d|InfestedTree)/, null],
];
const BOSS_OF = [[/eikthyr/i, "Eikthyr"], [/gdking/i, "The Elder"], [/bonemass/i, "Bonemass"], [/dragon/i, "Moder"], [/goblin/i, "Yagluth"], [/queen|seeker/i, "The Queen"], [/fader/i, "Fader"]];
const bossOf = n => (BOSS_OF.find(([re]) => re.test(n)) || [])[1];
const decamel = n => n.replace(/\d+$/, "").replace(/_/g, " ").replace(/([a-z])([A-Z])/g, "$1 $2").trim();
function locKind(prefab){
  const row = LOC.find(([re]) => re.test(prefab));
  if(row) return row[1] ? {group: row[1], one: row[2], many: row[3]} : null;
  return {group: "landmark", one: decamel(prefab), many: "of its kind"};
}
// The model library's index by name, for circling a location as tightly as its model allows.
function libraryByName(){ const m = new Map(); if(street.view) for(const p of street.view.prefabs.values()) m.set(p.n, p); return m; }
function tightDist(info, dflt){
  const b = info && info.m && info.b; if(!b) return dflt;
  const ext = Math.max(b[3] - b[0], b[5] - b[2], (b[4] - b[1])*1.5);
  return MapCore.clamp(ext*1.7, 24, 130);
}

// ---------- the places ----------
// Every place worth a stop, by kind: a kind is what a cycle draws through before any repeats.
function tourPool(){
  const pool = new Map(), walked = walkedFn(), F = world.features || [], pic = MapCore.pic, lib = libraryByName();
  const add = (group, s) => { s.group = group; s.key = group + "|" + s.title + "|" + Math.round(s.x/40) + "," + Math.round(s.z/40); (pool.get(group) || pool.set(group, []).get(group)).push(s); };
  const at = (x, z) => nameAt(x, z) || "the wild";
  const biome = (x, z) => { const b = chartAt(x, z); return b && b !== "sea" ? [[b, "biome"]] : []; };
  for(const p of ((world.players && world.players.players) || []).filter(p => p.x != null && !p.hidden))
    add("online", {kind: "Online now", title: p.name, who: p.name, line: `in ${at(p.x, p.z)}`, x: p.x, z: p.z, zoom: [34, 46], dist: 80, moves: ["orbit", "crane", "dolly"]});
  // the gates: the hubs, and the one farthest from any other
  const byId = new Map(world.portals.map(p => [p.id, p]));
  cellsOf(world.portals, 160).filter(c => c.n >= 3).slice(0, 3).forEach(c => {
    const linked = c.items.filter(p => p.to && p.to !== p.id && byId.has(p.to));
    const far = linked.map(p => { const a = wOf(p), b = wOf(byId.get(p.to)); return {p, b, d: Math.hypot(a.x - b.x, a.z - b.z)}; }).sort((a, b) => b.d - a.d)[0];
    add("hub", {kind: "Portal hub", title: nameAt(c.x, c.z) || "A hub", line: `${c.n} gates stand here`, x: c.x, z: c.z, zoom: [22, 34], dist: 140, layer: "portal",
      gates: c.items.map(wOf), moves: ["gates", "orbit", "dolly"],
      stats: [[c.n, "gates"], [linked.length, "linked"]].concat(far ? [[`${far.p.name || "unnamed"} to ${at(far.b.x, far.b.z)}`, `the farthest pair, ${kmOf(far.d)}`]] : [])});
  });
  if(world.portals.length > 1){
    const pts = world.portals.map(p => ({p, w: wOf(p)}));
    const lone = pts.map(a => ({a, d: Math.min(...pts.filter(b => b !== a).map(b => Math.hypot(a.w.x - b.w.x, a.w.z - b.w.z)))})).sort((a, b) => b.d - a.d)[0];
    if(lone.d > 250) add("lonely", {kind: "The loneliest gate", title: lone.a.p.name || "An unnamed gate", line: `${kmOf(lone.d)} from any other gate, in ${at(lone.a.w.x, lone.a.w.z)}`,
      x: lone.a.w.x, z: lone.a.w.z, zoom: [26, 38], dist: 60, layer: "portal", moves: ["dolly", "crane"],
      stats: [[kmOf(lone.d), "to the next gate"], [byId.has(lone.a.p.to) ? "linked" : "waiting for a twin", ""]]});
  }
  // the builds: the biggest stretches, and each builder's densest
  const G = pieceGrid(), PL = world.builders;
  if(world.pieces && world.pieces.length){
    const all = world.pieces.length;
    cellsOf(world.pieces, 128).slice(0, 3).forEach((c, i) => {
      const top = PL.length ? tally(c.items.filter(p => p.by != null), "by")[0] : null, who = top && PL[+top[0]];
      add("build", {kind: i ? "A great build" : "The biggest build", title: nameAt(c.x, c.z) || "A settlement", line: `${c.n.toLocaleString()} pieces in this one stretch`,
        x: c.x, z: c.z, zoom: [PLAN_ZOOM*1.05, PLAN_ZOOM*1.5], drift: true, dist: 170, cell: c, moves: ["flyover", "yard", "walk", "crane", "sweep"],
        stats: [[c.n.toLocaleString(), "pieces"], [`${(100*c.n/all).toFixed(1)}%`, "of all standing"]].concat(who ? [[`${Math.round(100*top[1]/c.n)}%`, whose(who)]] : []),
        pic: who ? pic.ring(top[1]/c.n, `of it is ${whose(who)}`) : ""});
    });
    PL.forEach((name, b) => {
      const mine = world.pieces.filter(p => p.by === b); if(mine.length < 30) return;
      const c = cellsOf(mine, 96)[0], word = c.n >= 1500 ? "stronghold" : c.n >= 600 ? "hall" : c.n >= 200 ? "homestead" : "outpost";
      add("builder", {kind: "A builder's own", title: `${whose(name)} ${word}`, line: `in ${at(c.x, c.z)}`, x: c.x, z: c.z, zoom: [PLAN_ZOOM*1.05, PLAN_ZOOM*1.5], drift: true, dist: 130, cell: c,
        moves: ["yard", "walk", "flyover", "crane"], stats: [[c.n.toLocaleString(), "pieces here"], [mine.length.toLocaleString(), "standing in all"]],
        pic: pic.ring(mine.length/all, `of everything standing is ${whose(name)}`)});
    });
  }
  // the deaths and what they left
  cellsOf(world.deaths, 96).filter(c => c.n >= 3).slice(0, 3).forEach(c => { const rows = tally(c.items, "name");
    add("deaths", {kind: "Where people die", title: nameAt(c.x, c.z) || "A bad spot", line: `${c.n} deaths here · ${rows[0][0]} fell ${rows[0][1]} of them`, x: c.x, z: c.z,
      zoom: [20, 30], dist: 120, detail: "deaths", moves: ["sweep", "orbit"], stats: [[c.n, "deaths"], [rows.length, rows.length === 1 ? "person" : "people"]],
      pic: pic.bars(rows.slice(0, 3).map(([n, k]) => ({frac: k/c.n, label: `${k} ${n}`})))}); });
  if(world.deaths.length){ const d = world.deaths[world.deaths.length - 1], w = wOf(d);
    add("latest", {kind: "The latest death", title: d.name, who: d.name, line: `${MapCore.ago(d.t*1000)}, in ${at(w.x, w.z)}`, x: w.x, z: w.z, zoom: [30, 40], dist: 100, detail: "deaths", moves: ["dolly", "orbit"]}); }
  world.graves.forEach(g => { const w = wOf(g);
    add("grave", {kind: "A grave", title: `${whose(g.name)} gear`, who: g.name, line: `still lying in ${at(w.x, w.z)}`, x: w.x, z: w.z, zoom: [32, 44], dist: 80, layer: "grave", moves: ["dolly", "sweep"]}); });
  const old = world.graves.length > 1 ? world.graves.slice().sort((a, b) => num(b.age) - num(a.age))[0] : null;
  if(old){ const w = wOf(old);
    add("oldgrave", {kind: "The oldest grave", title: `${whose(old.name)} gear`, who: old.name, line: `lying longer than any other, in ${at(w.x, w.z)}`, x: w.x, z: w.z, zoom: [32, 44], dist: 80, layer: "grave", moves: ["sweep", "dolly"],
      stats: [[world.graves.filter(g => g.name === old.name).length, "of theirs out there"], [world.graves.length, "graves in all"]]}); }
  // the traders at their camps
  const CAMP = {"Haldor": ["Vendor_BlackForest", "his wagon camp"], "Hildir": ["Hildir_camp", "her tent camp"], "Bog Witch": ["BogWitch_Camp", "her hut on stilts"]};
  world.traders.forEach(t => { const w = wOf(t), camp = CAMP[t.name] || [null, "a camp"];
    add("trader", {kind: "A trader", title: t.name, line: `keeps shop in ${at(w.x, w.z)}`, x: w.x, z: w.z, zoom: [28, 40], dist: tightDist(lib.get(camp[0]), 90), layer: "trader",
      moves: ["orbit", "crane", "sweep"], stats: [[t.name, "keeps shop"], [camp[1], ""]].concat(biome(w.x, w.z))}); });
  // every boat and every cart
  const boats = (world.vehicles || []).filter(v => v.kind === "boat"), carts = (world.vehicles || []).filter(v => v.kind === "cart");
  boats.forEach(b => { const w = wOf(b), st = MapCore.vehicleStyle(b), same = boats.filter(o => MapCore.vehicleStyle(o).label === st.label).length;
    add("boat", {kind: "Afloat", title: st.label, line: `lies at ${nameAt(w.x, w.z) || "sea"}`, x: w.x, z: w.z, zoom: [40, 52], dist: 70, layer: "vehicles", moves: ["sweep", "orbit"],
      stats: [[boats.length, boats.length === 1 ? "boat afloat" : "boats afloat"], [same, `${st.label.toLowerCase()}${same === 1 ? "" : "s"}`]]}); });
  carts.forEach(c => { const w = wOf(c);
    add("cart", {kind: "A cart", title: nameAt(w.x, w.z) || "A cart", line: "left standing where the last load came off", x: w.x, z: w.z, zoom: [40, 52], dist: 45, layer: "vehicles", moves: ["orbit", "sweep"],
      stats: [[carts.length, carts.length === 1 ? "cart" : "carts"]]}); });
  // the busiest kitchen, from a mod that counts meals
  const K = world.stats && world.stats.kitchen, kitchen = K ? MapCore.kitchens(K.stations)[0] : null;
  if(kitchen && kitchen.cooked > 0){ const c = kitchen;
    add("kitchen", {kind: "The busiest kitchen", title: nameAt(c.x, c.z) || "A kitchen", line: `${c.cooked.toLocaleString()} meals cooked here`, x: c.x, z: c.z, zoom: [34, 46], dist: 45, detail: "fires",
      moves: ["yard", "orbit"], stats: [[c.cooked.toLocaleString(), "meals cooked"], [c.stations, c.stations === 1 ? "station" : "stations"]].concat(c.brewed ? [[c.brewed.toLocaleString(), "meads brewed"]] : []),
      pic: num(K.cooked) ? pic.ring(c.cooked/num(K.cooked), "of every meal cooked") : ""});
  }
  if(walked){
    // the lands from far up, circled about the middle of what is walked of each
    const middle = f => { let n = 0, sx = 0, sz = 0; const N = 14;
      for(let i = 0; i < N; i++) for(let j = 0; j < N; j++){ const x = f.x - f.w/2 + f.w*(i + .5)/N, z = f.z - f.h/2 + f.h*(j + .5)/N; if(walked(x, z)){ n++; sx += x; sz += z; } }
      return {share: n/(N*N), x: n ? sx/n : f.x, z: n ? sz/n : f.z}; };
    const box = f => ({w: f.w, h: f.h, cx: f.x, cz: f.z}), area = f => f.area || f.w*f.h/1e6;
    F.filter(f => (f.kind === "continent" || f.kind === "island") && f.w).sort((a, b) => b.w*b.h - a.w*a.h).slice(0, 4).forEach(f => {
      const m = middle(f); if(m.share < 0.08) return;
      add("land", {kind: f.kind === "continent" ? "A continent" : "An island", title: f.name, line: `${area(f).toFixed(1)} km² · ${Math.round(100*m.share)}% of it walked`,
        x: m.x, z: m.z, fit: box(f), dist: Math.min(2600, Math.max(700, Math.sqrt(m.share)*Math.max(f.w, f.h)*1.1)), detail: "names", far: true, moves: ["tilt"],
        stats: [[area(f).toFixed(1), "km²"], [`${Math.round(100*m.share)}%`, "walked"]], pic: pic.ring(m.share, "of it walked")}); });
    // the records among what anyone has seen: the tallest peak climbed, the biggest lake known, the longest river known
    const peak = F.filter(f => f.kind === "range" && f.peak && walked(f.peak.x, f.peak.z)).sort((a, b) => b.peak.y - a.peak.y)[0];
    if(peak) add("peak", {kind: "The tallest peak climbed", title: peak.name, line: `${Math.round(peak.peak.y - 30)} m above the sea`, x: peak.peak.x, z: peak.peak.z, zoom: [18, 26], dist: 340, far: true, detail: "names",
      moves: ["peakdown", "orbit"], stats: [[`${Math.round(peak.peak.y - 30)} m`, "above the sea"]].concat(biome(peak.peak.x, peak.peak.z))});
    F.filter(f => f.kind === "range" && f.w && f.peak && walked(f.peak.x, f.peak.z) && Math.max(f.w, f.h) >= 600).forEach(f =>
      add("ridge", {kind: "Along the ridge", title: f.name, line: `the peak line, ${Math.round(f.peak.y - 30)} m at its top`, x: f.peak.x, z: f.peak.z, fit: box(f), dist: Math.max(500, Math.max(f.w, f.h)*0.8), far: true, detail: "names",
        range: f, moves: ["ridge"], need3d: true, stats: [[kmOf(Math.max(f.w, f.h)), "end to end"], [`${Math.round(f.peak.y - 30)} m`, "at the top"]]}));
    const lake = F.filter(f => f.kind === "lake" && f.w).map(f => ({f, m: middle(f)})).filter(r => r.m.share >= 0.08).sort((a, b) => b.f.area - a.f.area)[0];
    if(lake) add("lake", {kind: "The biggest lake known", title: lake.f.name, line: `${lake.f.area.toFixed(2)} km²`, x: lake.m.x, z: lake.m.z, fit: box(lake.f), dist: Math.max(500, Math.max(lake.f.w, lake.f.h)*0.8), far: true, detail: "names",
      moves: ["tilt"], stats: [[lake.f.area.toFixed(2), "km²"], [`${Math.round(100*lake.m.share)}%`, "of its shores walked"]]});
    const riverLen = f => f.line.reduce((t, p, i) => i ? t + Math.hypot(p[0] - f.line[i-1][0], p[1] - f.line[i-1][1]) : 0, 0);
    const river = F.filter(f => f.kind === "river" && f.line && f.line.length > 2 && f.line.some(p => walked(p[0], p[1]))).sort((a, b) => riverLen(b) - riverLen(a))[0];
    if(river){   // flown along the stretch someone has seen: from its first walked point to its last
      const seen = river.line.map(p => walked(p[0], p[1])), i0 = seen.indexOf(true), i1 = seen.lastIndexOf(true), path = river.line.slice(i0, i1 + 1);
      if(path.length > 1) add("river", {kind: "The longest river known", title: river.name, line: `${(riverLen(river)/1000).toFixed(1)} km from source to sea`, x: path[0][0], z: path[0][1], path, zoom: 14, detail: "names",
        stats: [[kmOf(riverLen(river)), "source to sea"]]});
    }
    // what players themselves marked, and named
    world.pins.filter(p => p.text && p.x != null).forEach(p => add("pin", {kind: "A pin", title: p.text, line: `${p.type === "dot" ? "a" : p.type} pin${p.owner ? ` by ${p.owner}` : ""}, in ${at(p.x, p.z)}`,
      x: p.x, z: p.z, zoom: [30, 40], dist: 90, layer: "pin", moves: ["dolly", "orbit", "sweep"], stats: p.owner ? [[p.owner, "pinned it"]] : []}));
    F.filter(f => f.by && f.w && f.kind !== "river" && f.kind !== "continent" && f.kind !== "island").forEach(f => { const m = middle(f); if(m.share < 0.08) return;
      add("named", {kind: "A named place", title: f.name, line: `named by ${f.by} · ${area(f).toFixed(1)} km²`, x: m.x, z: m.z, fit: box(f), dist: Math.min(2000, Math.max(400, Math.max(f.w, f.h)*0.7)), far: true, detail: "names",
        moves: ["tilt"], stats: [[f.by, "named it"], [`${Math.round(100*m.share)}%`, "walked"]]}); });
    F.filter(f => f.kind === "range" && f.w && Math.max(f.w, f.h) >= 400).map(f => ({f, m: middle(f)})).filter(r => r.m.share >= 0.08).forEach(({f, m}) =>
      add("range", {kind: "A mountain", title: f.name, line: `${Math.round((f.peak ? f.peak.y : f.elev.max) - 30)} m at its top`, x: m.x, z: m.z, fit: box(f), dist: Math.max(400, Math.max(f.w, f.h)*0.9), far: true, detail: "names",
        moves: ["tilt"], stats: [[area(f).toFixed(1), "km²"], [`${Math.round(100*m.share)}%`, "walked"]]}));
    // the bays: from above, and flown along their shore
    F.filter(f => f.kind === "bay" && f.w && f.area >= 0.15).map(f => ({f, m: middle(f)})).filter(r => r.m.share >= 0.08).forEach(({f, m}) => {
      add("bay", {kind: "A bay", title: f.name, line: `${f.area.toFixed(2)} km² of sheltered water`, x: m.x, z: m.z, fit: box(f), dist: Math.max(400, Math.max(f.w, f.h)*0.8), far: true, detail: "names",
        moves: ["tilt"], stats: [[f.area.toFixed(2), "km²"], [`${Math.round(100*m.share)}%`, "of its shores walked"]]});
      add("coast", {kind: "Along the shore", title: f.name, line: "its coast from the water", x: m.x, z: m.z, fit: box(f), dist: 400, far: true, detail: "names", bay: f, moves: ["coast"], need3d: true,
        stats: [[f.area.toFixed(2), "km² of water"]]});
    });
    // the shore: out to sea from a dock, or from any walked beach of a bay
    const docks = [];
    if(G && world.pieces) for(const c of cellsOf(world.pieces, 128).slice(0, 6)){
      const s = shoreIn(c.x - 150, c.z - 150, c.x + 150, c.z + 150, walked).filter(p => G.near(p.x, p.z, 40) >= 3).sort((a, b) => Math.hypot(a.x - c.x, a.z - c.z) - Math.hypot(b.x - c.x, b.z - c.z))[0];
      if(s) docks.push({s, dock: true});
    }
    for(const f of F.filter(f => f.kind === "bay" && f.w).sort(() => Math.random() - .5).slice(0, 3)){
      const s = shoreIn(f.x - f.w/2, f.z - f.h/2, f.x + f.w/2, f.z + f.h/2, walked, 36); if(s.length) docks.push({s: pick(s)}); }
    docks.forEach(({s, dock}) => add("shore", {kind: dock ? "From the dock" : "From the shore", title: nameAt(s.x, s.z) || "The water's edge", line: `looking out over ${nameAt(s.x + Math.sin(s.sea*D2R)*120, s.z + Math.cos(s.sea*D2R)*120) || "the sea"}`,
      x: s.x, z: s.z, zoom: [34, 46], dist: 70, shore: s, moves: ["seaward", "shorewalk"], stats: biome(s.x, s.z)}));
    // somewhere walked and left alone: no build within 300 m
    const built = (world.pieces || []).map(wOf);
    for(let k = 0, found = 0; k < 200 && found < 3; k++){ const x = (Math.random()*2 - 1)*9000, z = (Math.random()*2 - 1)*9000;
      if(!walked(x, z) || chartAt(x, z) === "sea" || built.some(b => Math.abs(b.x - x) < 300 && Math.abs(b.z - z) < 300)) continue;
      found++; add("quiet", {kind: "The quiet wild", title: nameAt(x, z) || "Nowhere in particular", line: "walked once, and left alone", x, z, zoom: [20, 28], dist: 150, moves: ["sweep", "crane"],
        stats: [["300 m", "and more to the nearest build"]].concat(biome(x, z))}); }
    // the world's own locations, a few of each kind; a location with a model is circled as tightly as it allows
    const byGroup = new Map();
    for(const l of LOCATIONS){ const k = locKind(l.kind); if(!k || !walked(l.x, l.z)) continue; const g = byGroup.get(k.group) || byGroup.set(k.group, []).get(k.group); g.push({l, k}); }
    for(const [g, list] of byGroup){
      const kinds = new Set(list.map(r => r.k.one));
      list.slice().sort(() => Math.random() - .5).slice(0, 8).forEach(({l, k}) => {
        const info = lib.get(l.kind), boss = g === "altar" || g === "runestone" ? bossOf(l.kind) : null;
        const title = g === "altar" && boss ? `${whose(boss)} altar` : nameAt(l.x, l.z) || k.one;
        const line = g === "runestone" && boss ? `it points the way to ${boss}` : g === "altar" ? `in ${at(l.x, l.z)}` : `one of ${list.length} ${kinds.size > 1 ? "such places" : k.many} walked so far`;
        add("loc:" + g, {kind: k.one, title, line, x: l.x, z: l.z, zoom: [22, 34], dist: tightDist(info, 90), moves: info && info.m ? ["orbit", "crane", "sweep"] : ["crane", "dolly", "sweep"],
          stats: [[decamel(l.kind), "kind"]].concat(biome(l.x, l.z), [[list.length, "walked"]])});
      });
    }
    // where people go: the trails over the whole walked world
    const P = (world.stats && Array.isArray(world.stats.players)) ? world.stats.players : [], walkedM = P.reduce((t, q) => t + num(q.dist_m), 0);
    if(walkedM > 0){ const by = {}; P.forEach(q => (q.biomes || []).forEach(b => { by[b.biome] = (by[b.biome] || 0) + num(b.m); }));
      // the ground under a walk has been kept for less long than the walk itself: shares of what it covers
      const rows = Object.entries(by).sort((a, b) => b[1] - a[1]), placed = rows.reduce((t, r) => t + r[1], 0);
      add("trails", {kind: "Where people go", title: "Every path walked", line: "the trails over the whole walked world", x: 0, z: 0, wide: true, far: true, detail: "trails",
        stats: [[kmOf(walkedM), "walked, all told"]].concat(placed ? [[`${Math.round(100*rows[0][1]/placed)}%`, `of it in the ${rows[0][0] === "Ocean" ? "sea" : rows[0][0]}`]] : []),
        pic: placed ? pic.bars(rows.slice(0, 3).map(([b, m]) => ({frac: m/placed, label: `${Math.round(100*m/placed)}% ${b === "Ocean" ? "sea" : b}`, colour: MapCore.BIOME_INK[b]}))) : ""}); }
  }
  add("centre", {kind: "Where it all began", title: "The sacrificial stones", line: "the centre of the world", x: 0, z: 0, zoom: [24, 34], dist: 130, moves: ["crane", "orbit"],
    stats: world.explored != null ? [[`${(world.explored*100).toFixed(1)}%`, "of the world walked since"]] : []});
  return pool;
}

// ---------- the moves ----------
// Each makes a shot: the views to build before the cut (the first is what the cut shows), where
// the 3D view stands, how long it plays, and prep and play, which set the camera and move it.
function orbitShot(s, o){
  const views = [{x: s.x, z: s.z, dist: o.dist, pitch: o.pitch}];
  if(o.end) views.push({x: s.x, z: s.z, dist: o.end.dist, pitch: o.end.pitch ?? o.pitch});
  return {views, x: s.x, z: s.z, ms: o.ms,
    prep: v => { v.setMode("orbit"); v.setOrbit({dist: o.dist, pitch: o.pitch, lift: o.lift || 0}); },
    play: v => { v.setSpin(o.spin); if(o.dolly) v.dolly(o.dolly[0], o.dolly[1], o.ms/TOUR.pace); if(o.crane) v.crane(o.crane[0], o.crane[1], o.ms/TOUR.pace); }};
}
// At eye height or over it: a stand with a look around, or a line walked or flown. A stand is
// settled at the cut, once the ground and what stands on it are built: find (a summit, say) picks
// the spot and the turn, and the stand steps to where the eye sees out across all of it.
function streetShot(x, z, heading, o){
  const views = o.path ? [{path: o.path, eye: o.eye}] : [{x, z, street: true, eye: o.eye}];
  const sh = {views, x, z, look: heading, ms: o.ms, path: o.path, pan: o.pan,
    prep: v => {
      let p = {x, z, h: heading, pan: o.pan};
      // a walk keeps to the stretch nothing stands across; with none, it is a stand at its start
      sh.route = o.path && o.eye < 5 ? clearRun(v, o.path, o.eye) : o.path;
      if(o.path && !sh.route){ const a = headingTo({x, z}, {x: o.path[1][0], z: o.path[1][1]}); p.pan = [a - 45, a + 45]; }
      if(sh.route) Object.assign(p, {x: sh.route[0][0], z: sh.route[0][1], h: headingTo({x: sh.route[0][0], z: sh.route[0][1]}, {x: sh.route[1][0], z: sh.route[1][1]})});
      if(o.find) Object.assign(p, o.find(v, x, z));
      if(p.pan) Object.assign(p, standClear(v, p.x, p.z, p.pan, o.eye, o.aim, o.reach));
      sh.pan = p.pan;
      v.setMode("street"); v.stand(p.x, p.z, p.pan ? p.pan[0] : p.h, {eye: o.eye, pitch: o.pitch || 0});
    },
    play: v => { if(sh.route) v.walk(sh.route, o.speed*TOUR.pace, {eye: o.eye, pitch: o.pitch || 0}); else if(sh.pan) v.pan(sh.pan[0], sh.pan[1], o.ms/TOUR.pace); }};
  return sh;
}
// The longest stretch of a walk with nothing standing within a few steps ahead: the pieces
// were known when it was planned, the rocks and trees only once they are built.
function clearRun(v, path, eye){
  let best = null, run = [];
  const keep = () => { const L = lineLen(run); if(run.length > 1 && L >= 16 && (!best || L > lineLen(best))) best = run; };
  for(let i = 1; i < path.length; i++){
    const [ax, az] = path[i - 1], [bx, bz] = path[i], L = Math.hypot(bx - ax, bz - az), h = headingTo({x: ax, z: az}, {x: bx, z: bz});
    for(let t = 0; t <= L; t += 2){ const x = ax + (bx - ax)*t/L, z = az + (bz - az)*t/L;
      if(v.clearance(x, z, h, eye, 3, false) >= 3) run.push([x, z]); else { keep(); run = []; } }
  }
  keep();
  return best;
}
// A stand that sees: walked, dry, nothing standing close before the eye and the ground not rising
// over it for a good way (reach, metres), across every heading it turns through -- aim(x, z) gives
// those from where it stands, when they depend on it.
function standClear(v, x, z, pan, eye, aim, reach = {things: 10, ground: 50}){
  const walked = walkedFn(), panAt = (px, pz) => aim ? aim(px, pz) : pan;
  const open = (px, pz, a) => { const y = v.heightAt(px, pz) + eye, dx = Math.sin(a*D2R), dz = Math.cos(a*D2R);
    let d = 5; for(; d <= reach.ground; d += 5){ const h = v.heightAt(px + dx*d, pz + dz*d); if(h !== null && h > y) break; }
    return Math.min(d/reach.ground, v.clearance(px, pz, a, eye, reach.things, false)/reach.things); };
  const score = (px, pz) => { const h = v.heightAt(px, pz); if(h === null || h < v.waterLevel + 0.3 || (walked && !walked(px, pz))) return -1;
    // the frame reaches past the turn's ends by most of half the field of view
    const p = panAt(px, pz); return Math.min(...[0, 1/6, 2/6, 3/6, 4/6, 5/6, 1].map(t => open(px, pz, p[0] - 20 + (p[1] - p[0] + 40)*t))); };
  let best = {x, z, s: score(x, z)};
  for(const r of [3, 6, 10, 15, 21]) for(let a = 0; a < 360 && best.s < 1; a += 45){
    const px = x + Math.sin(a*D2R)*r, pz = z + Math.cos(a*D2R)*r, s = score(px, pz); if(s > best.s) best = {x: px, z: pz, s}; }
  return {x: best.x, z: best.z, pan: panAt(best.x, best.z)};
}
// the summit near a peak, facing the way the ground falls furthest
function summit(v, x, z){
  let top = null, low = null;
  for(let dx = -40; dx <= 40; dx += 4) for(let dz = -40; dz <= 40; dz += 4){ const h = v.heightAt(x + dx, z + dz); if(h !== null && (!top || h > top.y)) top = {x: x + dx, z: z + dz, y: h}; }
  if(!top) return {};
  for(let a = 0; a < 360; a += 15){ const h = v.heightAt(top.x + Math.sin(a*D2R)*160, top.z + Math.cos(a*D2R)*160); if(h !== null && (!low || h < low.y)) low = {a, y: h}; }
  const h = low ? low.a : Math.random()*360;
  return {x: top.x, z: top.z, h, pan: [h - 55, h + 55]};
}
const lineLen = p => p.reduce((t, q, i) => i ? t + Math.hypot(q[0] - p[i-1][0], q[1] - p[i-1][1]) : 0, 0);
const SHOTS = {
  orbit: s => orbitShot(s, {dist: s.dist, pitch: within([26, 50]), spin: pick([-1, 1])*(s.far ? 2.5 : 6), ms: s.far ? 22000 : 16000}),
  // low over the ground, the circled point lifted a little
  sweep: s => orbitShot(s, {dist: Math.max(50, s.dist*0.8), pitch: within([7, 12]), lift: within([4, 9]), spin: pick([-1, 1])*within([6, 9]), ms: 15000}),
  // from a man's height up to an overview
  crane: s => { const across = Math.max(40, s.dist*0.8), h0 = 5, h1 = Math.max(110, s.dist*1.4);
    return orbitShot(s, {dist: Math.hypot(across, h0), pitch: Math.atan2(h0, across)/D2R, spin: pick([-3, 3]), ms: 16000, end: {dist: Math.hypot(across, h1), pitch: Math.atan2(h1, across)/D2R}, crane: [h0, h1]}); },
  // from far out in close
  dolly: s => { const d1 = Math.max(32, s.dist*0.7), d0 = d1*3.2; return orbitShot(s, {dist: d0, pitch: within([26, 40]), spin: pick([-2.5, 2.5]), ms: 15000, end: {dist: d1}, dolly: [d0, d1]}); },
  // a land from far off, at a slant of its own
  tilt: s => orbitShot(s, {dist: s.dist, pitch: within([14, 48]), spin: pick([-2.5, 2.5]), ms: 22000, end: {dist: s.dist*0.82}, dolly: [s.dist, s.dist*0.82]}),
  // standing in a base's yard, clear of its walls, looking the build over
  yard: s => { const c = s.cell || s, G = pieceGrid(), walked = walkedFn(); if(!G || !walked) return null;
    let best = null;
    for(let dx = -42; dx <= 42; dx += 6) for(let dz = -42; dz <= 42; dz += 6){ const x = c.x + dx, z = c.z + dz;
      if(Math.hypot(dx, dz) > 42 || !walked(x, z) || chartAt(x, z) === "sea" || G.near(x, z, 3.5)) continue;
      const n = G.near(x, z, 26) - G.near(x, z, 9)*3; if(!best || n > best.n) best = {x, z, n}; }
    if(!best || best.n < 6) return null;
    const face = G.centre(best.x, best.z, 30) || c, h = headingTo(best, face), aim = (x, z) => { const a = headingTo({x, z}, face); return [a - 40, a + 40]; };
    return streetShot(best.x, best.z, h - 40, {eye: EYE_M, pan: [h - 40, h + 40], aim, ms: 16000}); },
  // walked at a walking pace through the settlement, on the line with the fewest walls across it
  walk: s => { const c = s.cell, G = pieceGrid(), walked = walkedFn(); if(!c || !G || !walked) return null;
    let best = null;
    for(let a = 0; a < 180; a += 10) for(let off = -36; off <= 36; off += 6) for(const half of [30, 22]){
      const dx = Math.sin(a*D2R), dz = Math.cos(a*D2R), cx = c.x + dz*off, cz = c.z - dx*off, p0 = [cx - dx*half, cz - dz*half], p1 = [cx + dx*half, cz + dz*half];
      if(!walked(...p0) || !walked(...p1) || chartAt(...p0) === "sea" || chartAt(...p1) === "sea") continue;
      let hit = 0, beside = 0;
      for(let t = 0; t <= 2*half; t += 2){ const x = p0[0] + dx*t, z = p0[1] + dz*t; if(G.near(x, z, 1.5, true)) hit++; if(t % 6 === 0) beside += G.near(x, z, 14); }
      if(hit <= 1 && beside >= 20 && (!best || beside > best.beside)) best = {p0, p1, beside, L: 2*half};
    }
    if(!best) return null;
    return streetShot(best.p0[0], best.p0[1], headingTo({x: best.p0[0], z: best.p0[1]}, {x: best.p1[0], z: best.p1[1]}), {eye: EYE_M, path: [best.p0, best.p1], speed: 2.8, ms: best.L/2.8*1000 + 1500}); },
  // flown across the settlement along its long axis
  flyover: s => { const c = s.cell; if(!c || c.items.length < 20) return null;
    const pts = c.items.map(wOf); let sxx = 0, szz = 0, sxz = 0;
    for(const p of pts){ const x = p.x - c.x, z = p.z - c.z; sxx += x*x; szz += z*z; sxz += x*z; }
    const a = 0.5*Math.atan2(2*sxz, sxx - szz), dx = Math.cos(a), dz = Math.sin(a), p0 = [c.x - dx*170, c.z - dz*170], p1 = [c.x + dx*170, c.z + dz*170];
    return streetShot(p0[0], p0[1], headingTo({x: p0[0], z: p0[1]}, {x: p1[0], z: p1[1]}), {eye: 30, pitch: -24, path: [p0, p1], speed: 14, ms: 340/14*1000 + 1000}); },
  // standing among the gates of a hub, turning across them
  gates: s => { const g = s.gates, G = pieceGrid(), walked = walkedFn(); if(!g || g.length < 2 || !G || !walked) return null;
    const cx = g.reduce((t, p) => t + p.x, 0)/g.length, cz = g.reduce((t, p) => t + p.z, 0)/g.length;
    let at = null;
    for(const r of [0, 4, 7, 10, 14]) for(let a = 0; a < 360 && !at; a += r ? 30 : 360){ const x = cx + Math.sin(a*D2R)*r, z = cz + Math.cos(a*D2R)*r; if(walked(x, z) && !G.near(x, z, 2.2, true)) at = {x, z}; }
    if(!at) return null;
    // the turn that sweeps across every gate: from just past the widest gap between them round to its other side
    const aim = (x, z) => { const hs = g.map(p => headingTo({x, z}, p)).sort((a, b) => a - b);
      let gap = 360 - hs[hs.length - 1] + hs[0], from = hs[0];
      for(let i = 1; i < hs.length; i++) if(hs[i] - hs[i-1] > gap){ gap = hs[i] - hs[i-1]; from = hs[i]; }
      let span = 360 - gap + 24; from -= 12; if(span < 60){ from -= (60 - span)/2; span = 60; }
      return [from, from + Math.min(span, 160)]; };
    const pan = aim(at.x, at.z);
    return streetShot(at.x, at.z, pan[0], {eye: EYE_M, pan, aim, reach: {things: 6, ground: 25}, ms: 17000}); },
  // out to sea from the shore, turning slowly along it
  seaward: s => { const p = s.shore; if(!p) return null; return streetShot(p.x, p.z, p.sea - 35, {eye: EYE_M, pan: [p.sea - 35, p.sea + 35], ms: 16000}); },
  // along the water's edge at a walking pace
  shorewalk: s => { const p = s.shore, walked = walkedFn(); if(!p || !walked) return null;
    const along = (p.sea + 90)*D2R, up = (p.sea + 180)*D2R, back = [p.x + Math.sin(up)*6, p.z + Math.cos(up)*6];
    const p0 = [back[0] - Math.sin(along)*28, back[1] - Math.cos(along)*28], p1 = [back[0] + Math.sin(along)*28, back[1] + Math.cos(along)*28];
    if(!walked(...p0) || !walked(...p1)) return null;
    return streetShot(p0[0], p0[1], headingTo({x: p0[0], z: p0[1]}, {x: p1[0], z: p1[1]}), {eye: EYE_M, path: [p0, p1], speed: 2.6, ms: 56/2.6*1000 + 1500}); },
  // on the summit, looking down
  peakdown: s => streetShot(s.x, s.z, 0, {eye: EYE_M, pitch: -14, find: summit, ms: 18000}),
  // along the range's peak line, from the heights the server keeps
  ridge: async s => { const path = await ridgeLine(s.range); if(!path) return null; const L = lineLen(path), speed = MapCore.clamp(L/26, 20, 70);
    return streetShot(path[0][0], path[0][1], headingTo({x: path[0][0], z: path[0][1]}, {x: path[1][0], z: path[1][1]}), {eye: 70, pitch: -18, path, speed, ms: L/speed*1000 + 1000}); },
  // along a bay's shore, from over the water
  coast: s => { const path = coastLine(s.bay, walkedFn()); if(!path) return null; const L = lineLen(path), speed = MapCore.clamp(L/26, 14, 50);
    return streetShot(path[0][0], path[0][1], headingTo({x: path[0][0], z: path[0][1]}, {x: path[1][0], z: path[1][1]}), {eye: 45, pitch: -12, path, speed, ms: L/speed*1000 + 1000}); },
};
// A range's peak line: across each slice of its long axis the highest walked point, from the
// coarsest heights the server keeps (a chunk is 17 by 17 at 16 m), in order and smoothed.
async function ridgeLine(f){
  const walked = walkedFn(); if(!walked || !f) return null;
  const long = f.w >= f.h, half = Math.min(1200, Math.max(f.w, f.h)/2), mx = long ? f.peak.x : f.x, mz = long ? f.z : f.peak.z;
  const x0 = long ? mx - half : f.x - f.w/2, x1 = long ? mx + half : f.x + f.w/2, z0 = long ? f.z - f.h/2 : mz - half, z1 = long ? f.z + f.h/2 : mz + half;
  const got = new Map(), jobs = [];
  for(let cz = Math.floor(z0/256); cz <= Math.floor(z1/256); cz++) for(let cx = Math.floor(x0/256); cx <= Math.floor(x1/256); cx++){
    if(![[.5, .5], [.1, .1], [.9, .1], [.1, .9], [.9, .9]].some(([u, w]) => walked((cx + u)*256, (cz + w)*256))) continue;
    jobs.push(fetch(API + `/height?cx=${cx}&cz=${cz}&step=16&v=${(world.rev && world.rev.height) || 0}`).then(r => r.ok ? r.arrayBuffer() : null)
      .then(b => { if(b && b.byteLength === 17*17*2) got.set(cx + "," + cz, new Int16Array(b)); }).catch(() => {}));
  }
  if(!jobs.length || jobs.length > 100) return null;
  await Promise.all(jobs);
  const hAt = (x, z) => { const cx = Math.floor(x/256), cz = Math.floor(z/256), a = got.get(cx + "," + cz); if(!a) return null;
    return a[Math.round((z - cz*256)/16)*17 + Math.round((x - cx*256)/16)]/10; };
  const N = 18, M = 24, line = [];
  for(let i = 0; i < N; i++){ let best = null;
    for(let j = 0; j < M; j++){ const u = (i + .5)/N, t = (j + .5)/M, x = long ? x0 + u*(x1 - x0) : x0 + t*(x1 - x0), z = long ? z0 + t*(z1 - z0) : z0 + u*(z1 - z0);
      if(!walked(x, z)) continue; const h = hAt(x, z); if(h !== null && h > 90 && (!best || h > best.h)) best = {x, z, h}; }
    if(best) line.push(best); }
  // a ridge runs on: a point that jumps across the range is someone else's
  const step = 2*half/N, run = [];
  for(const p of line) if(!run.length || Math.hypot(p.x - run[run.length - 1].x, p.z - run[run.length - 1].z) < step*3) run.push(p);
  if(run.length < 4) return null;
  return run.map((p, i) => { const a = run[Math.max(0, i - 1)], b = run[Math.min(run.length - 1, i + 1)]; return [(a.x + p.x + b.x)/3, (a.z + p.z + b.z)/3]; });
}
// A bay's shore from over the water: its walked coast in order round the bay, the longest
// unbroken stretch of it, 40 m out and smoothed.
function coastLine(f, walked){
  if(!f || !walked) return null;
  const pad = 0.15, pts = shoreIn(f.x - f.w*(.5 + pad), f.z - f.h*(.5 + pad), f.x + f.w*(.5 + pad), f.z + f.h*(.5 + pad), walked);
  if(pts.length < 8) return null;
  pts.sort((a, b) => Math.atan2(a.x - f.x, a.z - f.z) - Math.atan2(b.x - f.x, b.z - f.z));
  let runs = [[pts[0]]];
  for(let i = 1; i < pts.length; i++){ const p = pts[i], q = pts[i-1]; if(Math.hypot(p.x - q.x, p.z - q.z) > 80) runs.push([]); runs[runs.length - 1].push(p); }
  const len = r => r.reduce((t, p, i) => i ? t + Math.hypot(p.x - r[i-1].x, p.z - r[i-1].z) : 0, 0);
  const run = runs.sort((a, b) => len(b) - len(a))[0];
  if(len(run) < 300) return null;
  const out = run.map(p => [p.x + Math.sin(p.sea*D2R)*40, p.z + Math.cos(p.sea*D2R)*40]);
  const smooth = out.map((p, i) => { const w = out.slice(Math.max(0, i - 2), i + 3); return [w.reduce((t, q) => t + q[0], 0)/w.length, w.reduce((t, q) => t + q[1], 0)/w.length]; });
  const path = [smooth[0]]; let L = 0;
  for(const p of smooth){ const q = path[path.length - 1], d = Math.hypot(p[0] - q[0], p[1] - q[1]); if(d >= 48){ path.push(p); L += d; if(L > 1600) break; } }
  return path.length > 2 ? path : null;
}
async function shotFor(s, last){
  const moves = (s.moves || []).filter(m => m !== last), order = (moves.length ? moves : s.moves || []).slice().sort(() => Math.random() - .5);
  for(const m of order){ let sh = null; try{ sh = await SHOTS[m](s); }catch(e){} if(sh){ sh.move = m; return sh; } }
  return null;
}

// ---------- a cycle ----------
// About twenty stops: who is on first, then through every kind before any repeats -- the kinds
// least lately seen first, and of each the place least lately seen -- so consecutive cycles
// differ. Every third or fourth stop, one of the world's figures.
async function tourPlan(){
  await fetchLocations();
  const pool = tourPool(), out = [...(pool.get("online") || [])];
  pool.delete("online");
  const kinds = [...pool.keys()].sort(() => Math.random() - .5).sort((a, b) => (TOUR.kinds.get(a) || 0) - (TOUR.kinds.get(b) || 0));
  for(let pass = 0; pass < 3 && out.length < 20; pass++)
    for(const k of kinds){
      if(out.length >= 20) break;
      const list = pool.get(k).filter(s => !out.includes(s)).sort(() => Math.random() - .5).sort((a, b) => (TOUR.seen.get(a.key) || 0) - (TOUR.seen.get(b.key) || 0));
      if(list.length) out.push(list[0]);
    }
  let last = TOUR.move;
  const plan = [];
  for(const s of out){
    let t = s, sh = s.moves && s.moves.length ? await shotFor(s, last) : null;
    // a kind that is nothing without its flight tries another place of its kind, or sits this cycle out
    for(const alt of s.need3d && !sh ? pool.get(s.group).filter(a => !out.includes(a)).slice(0, 3) : []){ sh = await shotFor(alt, last); if(sh){ t = alt; break; } }
    if(t.need3d && !sh) continue;
    t.shot = sh; if(sh) last = sh.move; plan.push(t);
  }
  const res = [];
  let gap = 3;
  plan.forEach((s, i) => { res.push(s); if(--gap === 0 && i < plan.length - 1){ res.push({figure: true}); gap = res.filter(r => r.figure).length % 2 ? 4 : 3; } });
  if(res.length) res[res.length - 1].end = true;
  return res;
}
const viewsOf = s => (s && s.shot) ? s.shot.views : [];
// the caption: what, which, why, and under it a strip of figures matched to the place
function caption(s){
  tourCap.querySelector(".eyebrow").textContent = s.kind; tourCap.querySelector(".tt").textContent = s.title; tourCap.querySelector(".tl").textContent = s.line;
  tourCap.querySelector(".ts").innerHTML = (s.stats || []).filter(r => r && r[0] !== "" && r[0] != null).map(([v, k]) => `<span><b>${esc(String(v))}</b>${k ? " " + esc(k) : ""}</span>`).join("");
  tourCap.querySelector(".tp").innerHTML = s.pic || "";
  tourCap.hidden = false; tourCap.classList.remove("show"); void tourCap.offsetWidth; tourCap.classList.add("show");
}
// A stop about a player carries their card, as the players page reads them: the class, the
// pentagon, the four figures, and their Viking turning where the stage has room for it.
// One turntable for the page; the card's rig is let go as the card goes.
const NARROW = matchMedia("(max-width:560px), (max-height:480px)");
let VIKING = null, PCARD_T = 0;
function showPlayerCard(name){
  const P = (world.stats && Array.isArray(world.stats.players)) ? world.stats.players : [], p = P.find(q => q.name === name);
  if(!p) return dropPlayerCard();
  const C = PlayerCard, live = ((world.players && world.players.players) || []).find(q => q.name === name);
  C.rim(P);
  const line = live ? "online now" + (live.x != null && !live.hidden ? ` · in ${nameAt(live.x, live.z) || "the wild"}` : "") : C.seen(num(p.last_seen));
  pcard.innerHTML = `<div class="ph"><span class="dot"></span><span class="nm">${esc(p.name)}</span>${C.pill(C.classify(p, P))}</div><div class="pl">${esc(line)}</div>`
    + `<div class="pb"><i id="pvslot" hidden></i><div class="pr">${C.radar(p, 112, false)}${C.figures(p)}</div></div>`;
  pcard.classList.toggle("ok", !!live);
  const look = (live && live.look) || p.look;
  if(look && !NARROW.matches){
    VIKING = VIKING || C.viking(API);
    pcard.querySelector("#pvslot").replaceWith(VIKING.el);
    VIKING.show(p.name, look, !!live, ((world.state || {}).rev || {}).models || 0);
  }else if(VIKING) VIKING.drop();
  clearTimeout(PCARD_T);
  pcard.hidden = false; pcard.classList.remove("show"); void pcard.offsetWidth; pcard.classList.add("show");
}
function dropPlayerCard(){
  if(VIKING) VIKING.drop();
  if(pcard.hidden) return;
  pcard.classList.remove("show");
  clearTimeout(PCARD_T); PCARD_T = setTimeout(() => { pcard.hidden = true; }, 700);
}
async function tourStop(s, run, next){
  const alive = () => TOUR.on && TOUR.run === run;
  if(s.figure) return tourFigure(run, next);
  // a spot still under fog has nothing to show and is not flown to at all
  const fog = MapCore.explored(LAYERS.imgs.fog), q = toPx(s.x, s.z);
  if(!s.far && !(fog && fog(q.px, q.py))) return;
  // a stop handed in with a distance and no move is circled
  if(s.shot === undefined && s.dist) s.shot = orbitShot(s, {dist: s.dist, pitch: 45, spin: s.far ? 2.5 : 6, ms: s.far ? 22000 : 16000});
  if(s.key){ TOUR.seen.set(s.key, ++TOUR.n); TOUR.kinds.set(s.group, TOUR.n); }
  TOUR.now = s;
  if(s.shot) TOUR.move = s.shot.move;
  caption(s);
  if(s.who) showPlayerCard(s.who);
  // what this stop switches on, it switches off again on the way out
  const undo = [];
  if(s.layer && legend.hidden.has(s.layer)){ legend.showLayer(s.layer); undo.push(() => { legend.hidden.add(s.layer); legend.applyHidden(); drawRaster(); }); }
  if(s.detail && !legend.showDetail(s.detail)){ legend.setDetail(s.detail, true); undo.push(() => legend.setDetail(s.detail, false)); }
  // this place and the next are built while the map flies
  if(street.view) street.view.prefetch(viewsOf(s).concat(viewsOf(next)));
  // a stopped run's layers were put back by stopTour; undoing them again would switch off
  // what the viewer, or a tour started since, has switched on
  try{ await tourLegs(s, alive, next); }finally{ if(alive()){ undo.forEach(f => f()); if(s.who) dropPlayerCard(); } }
}
async function tourLegs(s, alive, next){
  const q = toPx(s.x, s.z), W = stage.clientWidth, H = stage.clientHeight;
  // The way there is flown high: out to where the walked world reads as a whole (or
  // just far enough that here and there share the screen), then down onto the spot.
  // Up close the fog is a black wall; from up there it is the world's shape.
  const whole = world.box ? Math.min(W/(world.box.w*1.3), H/(world.box.h*1.3)) : V.fit;
  const here = V.at(W/2, H/2), far = Math.hypot(q.px - here.px, q.py - here.py);
  const high = Math.max(whole, Math.min(V.scale, (W*0.45)/Math.max(far, 1)));
  if(s.wide){       // the whole walked world, drifting slowly across
    const b = world.box || {x: 0, y: 0, w: GEOM.size, h: GEOM.size}, k = Math.min(W/(b.w*1.05), H/(b.h*1.05));
    await flyTo(b.x + b.w*.42, b.y + b.h/2, k, 4200); if(!alive()) return;
    await flyTo(b.x + b.w*.58, b.y + b.h/2, k*1.12, 14000); if(!alive()) return;
    return beat(1500);
  }
  const down = s.fit ? Math.min(W/(s.fit.w/GEOM.pixel*1.4), H/(s.fit.h/GEOM.pixel*1.4)) : within(s.zoom);
  // a build is landed on at plan zoom to one side of it, and drifted across
  const drift = s.drift ? (a => ({x: Math.sin(a)*80/GEOM.pixel, y: Math.cos(a)*80/GEOM.pixel}))(Math.random()*2*Math.PI) : null;
  const c = s.fit ? toPx(s.fit.cx, s.fit.cz) : drift ? {px: q.px - drift.x, py: q.py - drift.y} : q;
  if(high < V.scale*0.8){ await flyTo(c.px, c.py, high, 3200); if(!alive()) return; }
  await flyTo(c.px, c.py, down, s.fit ? 4500 : 4200);
  if(!alive()) return;
  if(street.view) street.view.prefetch(viewsOf(s).concat(viewsOf(next)));
  if(drift) await flyTo(q.px + drift.x, q.py + drift.y, down*1.08, 7000); else await beat(s.path ? 1200 : 2500);
  if(!alive()) return;
  if(s.path){   // along the course, at a pace that reads: about a kilometre every four seconds
    for(let i = 1; i < s.path.length; i++){ const a = s.path[i-1], b = s.path[i], p = toPx(b[0], b[1]);
      await flyTo(p.px, p.py, down, Math.max(400, Math.hypot(b[0] - a[0], b[1] - a[1])*4)); if(!alive()) return; }
    await beat(2000); return;
  }
  const went = s.shot ? await cut3D(s, alive, next) : false;
  if(!alive()) return;
  // now and then the map pulls slowly back rather than cutting away
  if(Math.random() < (went ? 0.3 : 0.45)){ const m = V.at(stage.clientWidth/2, stage.clientHeight/2); await flyTo(m.px, m.py, V.scale/3, 5000); if(!alive()) return; }
  await beat(1500);
}
// The cut into 3D, only once the place's near chunks are built: the map's beat is held up to a
// few seconds longer while they come in, and a place still loading after that stays on the map.
// Never a cut into a half-built world.
async function cut3D(s, alive, next){
  const sh = s.shot;
  let v;
  try{ v = await street.load(); }catch(e){ return false; }
  if(!alive()) return false;
  if(!street.on && world.state) street.feed(world.state);
  v.prefetch(sh.views.concat(viewsOf(next)));
  // a fetch that failed is asked again as the view updates, which the map alone would not make it do
  for(let t0 = performance.now(), i = 0; !v.ready(sh.views[0]) && performance.now() - t0 < 6000; i++){ await sleepT(200); if(!alive()) return false; if(i % 5 === 4) v.update(); }
  if(!v.ready(sh.views[0])) return false;
  // the light the beat is seen in: a land from afar or a coast flown at the low sun, the rest by day or toward dusk
  v.setClock(s.far || s.group === "coast" ? pick([0.24, 0.74]) : pick([0.36, 0.5, 0.5, 0.62, 0.7]), 0);
  await street.enter(sh.x, sh.z, sh.look ?? Math.random()*360, sh.prep);
  if(!alive()) return false;
  street.markMode();
  // while it plays the next place is built; a line walked or flown keeps its own chunks
  v.prefetch((sh.path ? sh.views : []).concat(viewsOf(next)));
  sh.play(v); TOUR.in3d = s;
  await beat(sh.ms);
  if(TOUR.in3d === s) TOUR.in3d = null;
  if(!alive()) return true;             // stopTour stilled it; a tour started since may be moving it now
  v.still(); v.setClock(null, 0);
  street.leave(true);
  return true;
}
// One of the world's figures as a card over a slow far drift of the map.
const FIGURES = ["Explored", "Standing", "Felled", "Kitchen", "Afloat", "Gates", "Bosses", "Deaths"];
async function tourFigure(run, next){
  const alive = () => TOUR.on && TOUR.run === run;
  const list = figures().filter(f => FIGURES.includes(f.k)); if(!list.length) return;
  const f = list[TOUR.fig++ % list.length];
  if(street.view) street.view.prefetch(viewsOf(next));
  tourCap.classList.remove("show");
  tourFig.innerHTML = `<div class="eyebrow"><span>The world in numbers</span><span class="n">${esc(f.k)}</span></div><div class="v">${f.v}</div><div class="c">${f.c}</div>${f.svg}`;
  tourFig.hidden = false; tourFig.classList.remove("show"); void tourFig.offsetWidth; tourFig.classList.add("show");
  try{
    const W = stage.clientWidth, H = stage.clientHeight, b = world.box || {x: 0, y: 0, w: GEOM.size, h: GEOM.size}, k = Math.min(W/(b.w*1.2), H/(b.h*1.2));
    const a = Math.random()*2*Math.PI, dx = Math.sin(a)*b.w*.12, dy = Math.cos(a)*b.h*.12;
    await flyTo(b.x + b.w/2 - dx, b.y + b.h/2 - dy, k, 3600); if(!alive()) return;
    await flyTo(b.x + b.w/2 + dx, b.y + b.h/2 + dy, k*1.08, 11000); if(!alive()) return;
  }finally{ if(alive()) hideFigure(); }
}
function hideFigure(){ tourFig.classList.remove("show"); setTimeout(() => { if(!tourFig.classList.contains("show")) tourFig.hidden = true; }, 700); }
async function runTour(){
  const run = ++TOUR.run, alive = () => TOUR.on && TOUR.run === run;
  // the 3D view comes with the tour, so the next place can be built while this one plays
  try{ await street.load(); if(alive() && world.state && !street.on) street.feed(world.state); }catch(e){}
  for(const t0 = Date.now(); alive() && street.view && !street.view.prefabs.size && Date.now() - t0 < 4000; ) await sleepT(200);
  let queue = [];
  while(alive()){
    if(queue.length < 2){
      // what is still to come counts as seen, so the next cycle does not open on it
      for(const r of queue) if(r.key){ TOUR.seen.set(r.key, TOUR.n + 1); TOUR.kinds.set(r.group, TOUR.n + 1); }
      const more = await tourPlan(); if(!alive()) return; queue = queue.concat(more);
    }
    if(!queue.length){ await sleepT(5000); continue; }
    const s = queue.shift();
    await tourStop(s, run, queue.find(r => r.shot));
    if(s.end && alive()) TOUR.cycles++;
  }
}
function startTour(){
  if(TOUR.on) return;
  TOUR.on = true;
  TOUR.keep = {details: new Set(legend.details), off: new Set(legend.off), hidden: new Set(legend.hidden), side: document.body.classList.contains("side-open")};
  document.body.classList.add("tour"); tourBtn.classList.add("on"); tourBtn.innerHTML = "&#9632;"; tourBtn.title = "Stop the tour";
  closeCard(); street.armPeg(false); legend.close(); MapCore.toggleSide(false, false);
  runTour();
}
function stopTour(){
  if(!TOUR.on) return;
  TOUR.on = false; TOUR.run++; TOUR.in3d = null;
  const v = street.view;
  if(v){ v.still(); v.setOrbitDist(140); v.setClock(null, 0); }
  if(street.on) street.leave(true);
  if(v){
    v.setMode("street"); street.markMode();
    // whatever it was building for the next place stops, and what it built goes as a map left up lets it
    v.prefetch(null);
    street.letGo();
  }
  const k = TOUR.keep;
  if(k){ legend.details.clear(); k.details.forEach(x => legend.details.add(x)); legend.off.clear(); k.off.forEach(x => legend.off.add(x)); legend.hidden.clear(); k.hidden.forEach(x => legend.hidden.add(x));
         legend.markDetails(); legend.applyHidden(); drawRaster(); MapCore.toggleSide(k.side, false); }
  tourCap.classList.remove("show"); setTimeout(() => { if(!TOUR.on) tourCap.hidden = true; }, 700);
  hideFigure();
  dropPlayerCard();
  document.body.classList.remove("tour"); tourBtn.classList.remove("on"); tourBtn.innerHTML = "&#9654;"; tourBtn.title = "Cinematic: a tour of the world's places, in 3D where the ground is walked. Any touch ends it.";
}

function init(o){
  ({stage, V, layers: LAYERS, api: API, world, street, legend, figures, drawRaster, closeCard} = o);
  tourBtn = document.getElementById("tourBtn"); tourCap = document.getElementById("tourcap"); tourFig = document.getElementById("tourfig"); pcard = document.getElementById("pcard");
  tourBtn.onclick = () => TOUR.on ? stopTour() : startTour();
  // a hand on anything ends the tour: the screen is someone's again
  for(const ev of ["pointerdown", "wheel", "keydown"])
    document.addEventListener(ev, e => { if(TOUR.on && !(e.target.closest && e.target.closest("#tourBtn"))) stopTour(); }, true);
}

return {init, start: startTour, stop: stopTour, state: TOUR, visit: tourStop, plan: tourPlan, pool: tourPool, shots: SHOTS, flyTo, walked: walkedFn,
        showCard: showPlayerCard, dropCard: dropPlayerCard, get viking(){ return VIKING; }};
})();

