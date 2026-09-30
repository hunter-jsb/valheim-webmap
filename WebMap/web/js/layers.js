/* layers.js — the map's layers card: the legend's switches, its presets, the details, the names
 * flyout and the ground, each kept by this browser (xnv.*). init is handed the rasters, the world
 * as the page holds it and the page's redraw. One global, as map-core.js.
 */
const Layers = (() => {
"use strict";
let LAYERS, world, drawRaster;

// ---------- the legend and its presets ----------
// Which layers this viewer has switched off. Classes on the stage do the hiding,
// so markers rebuilt on every tick need no extra bookkeeping.
// portals sit on top of the builds they are in, graves are someone's bad day: both off until asked for
const HIDDEN = new Set(["portal", "grave"]);
// A preset is a whole legend at once. Its chip lights while the legend matches it exactly.
const PRESETS = [
  ["Default",    ["portal", "grave"]],
  ["Travel",     ["forest", "plan", "grave"]],
  ["Builds",     ["vehicles", "portal", "grave", "trader", "pin"]],
  ["Wilds",      ["builds", "plan", "vehicles", "portal"]],
  ["Bare",       ["forest", "builds", "plan", "vehicles", "portal", "grave", "trader", "pin"]],
];
function markPresets(){
  const now = [...HIDDEN].sort().join();
  document.querySelectorAll("#presets .chip").forEach((el, i) =>
    el.classList.toggle("on", PRESETS[i][1].slice().sort().join() === now));
}
function applyHidden(){
  const st = document.getElementById("stage");
  [...st.classList].filter(c=>c.startsWith("no-")).forEach(c=>st.classList.remove(c));
  HIDDEN.forEach(k=>st.classList.add("no-"+k));
  document.querySelectorAll(".legend [data-layer]").forEach(el=>el.classList.toggle("off", HIDDEN.has(el.dataset.layer)));
  markPresets();
  try{ localStorage.setItem("xnv.hidden", JSON.stringify([...HIDDEN])); }catch(e){}
}
// as if the row had been clicked in the legend, so it sticks for this browser
function showLayer(k){ if(HIDDEN.delete(k)){ applyHidden(); drawRaster(); } }

// ---------- the details and the names ----------
// Details are extras drawn over whatever the legend shows, each its own switch.
const DETAILS = new Set();
// the names are part of the map, so they are on until switched off; that switch is kept apart
const DEFAULT_ON = new Set(["names"]), OFF = new Set();
const showDetail = k => DEFAULT_ON.has(k) ? !OFF.has(k) : DETAILS.has(k);
// which names: groups of kinds, each its own switch under the Names chip
const NAME_GROUPS = [
  ["Land",   [["land", "Continents & islands", ["continent", "island"], "#f1ecdc"], ["holm", "Holms", ["holm"], "#d8d2c2"]]],
  ["Water",  [["still", "Lakes & bays", ["lake", "bay"], "#9fd0e6"], ["river", "Rivers", ["river"], "#9fd0e6"]]],
  ["Height", [["range", "Mountain ranges", ["range"], "#ffffff"]]],
  ["Biomes", [["forest", "Black forest", ["forest"], "#a6c48a"], ["swamp", "Swamp", ["swamp"], "#caa27b"], ["plains", "Plains", ["plains"], "#e2c98f"],
              ["meadows", "Meadows", ["meadows"], "#c2d69a"], ["mistlands", "Mistlands", ["mistlands"], "#c4a4dc"],
              ["ashlands", "Ashlands", ["ashlands"], "#e6907e"], ["north", "Deep north", ["north"], "#dde8f2"]]],
];
const NAMES_OFF = new Set();
const hiddenKinds = () => { const h = new Set(); for(const [, rows] of NAME_GROUPS) for(const [key, , kinds] of rows) if(NAMES_OFF.has(key)) kinds.forEach(k => h.add(k)); return h; };
let namesMenu, namesFly;
function markNames(){
  namesMenu.querySelectorAll(".frow").forEach(el => el.classList.toggle("off", NAMES_OFF.has(el.dataset.group)));
  try{ localStorage.setItem("xnv.names.off", JSON.stringify([...NAMES_OFF])); }catch(e){}
}
function markDetails(){
  document.querySelectorAll("#details .chip").forEach(el => el.classList.toggle("on", showDetail(el.dataset.detail)));
  try{ localStorage.setItem("xnv.details", JSON.stringify([...DETAILS])); localStorage.setItem("xnv.off", JSON.stringify([...OFF])); }catch(e){}
}
function setDetail(k, on){
  if(DEFAULT_ON.has(k)) on ? OFF.delete(k) : OFF.add(k); else on ? DETAILS.add(k) : DETAILS.delete(k);
  markDetails(); drawRaster();
  if(k === "trails" && on && world.rev) LAYERS.sync(world.rev, {trails: true});
}

// ---------- the ground ----------
// The world render, or the server's flat chart of biomes that the atlas uses.
// Thumbnails are the explored ground itself, at a stamp's size, fog and all.
const GROUNDS = [["terrain", "Terrain"], ["atlas", "Atlas"]];
let GROUND = "terrain", groundsEl, layersEl;
function drawThumb(cv, ground){
  const N = 64, pix = Math.min(devicePixelRatio || 1, 2);
  if(cv.width !== N*pix){ cv.width = cv.height = N*pix; }
  const b = world.box || {x:0, y:0, w:MapCore.geom.size, h:MapCore.geom.size};
  const k = N/(Math.max(b.w, b.h)*1.15);
  MapCore.drawRasters(cv.getContext("2d"), {scale:k, tx:N/2 - (b.x + b.w/2)*k, ty:N/2 - (b.y + b.h/2)*k, w:N, h:N, pix},
    LAYERS.imgs, {bg:"#000", ground});
}
function drawThumbs(){
  const next = GROUNDS[(GROUNDS.findIndex(x => x[0] === GROUND) + 1) % GROUNDS.length];
  drawThumb(document.getElementById("lthumb"), next[0]);
  document.getElementById("lcard").title = `Switch to ${next[1]} (L)`;
  groundsEl.querySelectorAll(".gcard").forEach(el => {
    el.classList.toggle("on", el.dataset.ground === GROUND);
    drawThumb(el.querySelector("canvas"), el.dataset.ground);
  });
}
// the atlas carries no forest shading, so its legend row waits, dimmed, for the terrain
function applyGround(){
  const atlas = GROUND === "atlas";
  document.body.classList.toggle("atlas", atlas);
  document.querySelectorAll('.legend [data-layer="forest"]').forEach(el => el.title = atlas ? "not drawn on the atlas" : "");
}
function setGround(g){
  GROUND = g;
  try{ localStorage.setItem("xnv.ground", g); }catch(e){}
  applyGround(); drawThumbs(); drawRaster();
}
function nextGround(){ setGround(GROUNDS[(GROUNDS.findIndex(x => x[0] === GROUND) + 1) % GROUNDS.length][0]); }

// a link's #layers=a,b come on as if clicked, and its #details=c,d come on and stay on: true
// when it asked for a detail
function follow(p){
  (p.layers||"").split(",").map(s=>s.trim()).filter(Boolean).forEach(showLayer);
  const asked = (p.details||"").split(",").map(s=>s.trim()).filter(Boolean);
  if(asked.length){ asked.forEach(k => { if(DEFAULT_ON.has(k)) OFF.delete(k); else DETAILS.add(k); }); markDetails(); }
  return asked.length > 0;
}

// What this browser kept, the card's rows and their switches, in the order the page always built them.
function init(o){
  ({layers: LAYERS, world, drawRaster} = o);
  try{ const saved = localStorage.getItem("xnv.hidden");
       if(saved !== null){ HIDDEN.clear(); JSON.parse(saved).forEach(k=>HIDDEN.add(k)); } }catch(e){}
  document.getElementById("presets").innerHTML =
    PRESETS.map(([n]) => `<button class="chip">${n}</button>`).join("");
  document.getElementById("presets").addEventListener("click", e => {
    const el = e.target.closest(".chip"); if(!el) return;
    HIDDEN.clear(); PRESETS[[...el.parentNode.children].indexOf(el)][1].forEach(k => HIDDEN.add(k));
    applyHidden(); drawRaster();
  });
  try{ (JSON.parse(localStorage.getItem("xnv.details") || "[]")).forEach(k => DETAILS.add(k)); }catch(e){}
  try{ (JSON.parse(localStorage.getItem("xnv.off") || "[]")).forEach(k => OFF.add(k)); }catch(e){}
  try{ (JSON.parse(localStorage.getItem("xnv.names.off") || "[]")).forEach(k => NAMES_OFF.add(k)); }catch(e){}
  namesMenu = document.getElementById("namesMenu"); namesFly = document.getElementById("namesFly");
  namesMenu.innerHTML = NAME_GROUPS.map(([title, rows]) => `<b class="eyebrow">${title}</b>` + rows.map(([key, label, , ink]) =>
    `<div class="frow" data-group="${key}"><span class="sw" style="background:${ink}"></span>${label}</div>`).join("")).join("");
  namesMenu.addEventListener("click", e => {
    const el = e.target.closest(".frow"); if(!el) return;
    const k = el.dataset.group;
    NAMES_OFF.has(k) ? NAMES_OFF.delete(k) : NAMES_OFF.add(k);
    markNames(); drawRaster();
  });
  document.getElementById("namesCaret").addEventListener("click", e => { e.stopPropagation(); namesFly.classList.toggle("open"); });
  document.addEventListener("pointerdown", e => { if(!e.target.closest("#namesFly")) namesFly.classList.remove("open"); });
  markNames();
  document.getElementById("details").addEventListener("click", e => {
    const el = e.target.closest(".chip"); if(!el) return;
    const k = el.dataset.detail;
    if(DEFAULT_ON.has(k)) OFF.has(k) ? OFF.delete(k) : OFF.add(k);
    else DETAILS.has(k) ? DETAILS.delete(k) : DETAILS.add(k);
    markDetails(); drawRaster();
    // the trails raster is fetched only once asked for
    if(k === "trails" && DETAILS.has(k) && world.rev) LAYERS.sync(world.rev, {trails: true});
  });
  markDetails();
  try{ const g = localStorage.getItem("xnv.ground"); if(GROUNDS.some(x => x[0] === g)) GROUND = g; }catch(e){}
  groundsEl = document.getElementById("grounds");
  groundsEl.innerHTML = GROUNDS.map(([k, n]) =>
    `<button class="gcard" data-ground="${k}"><canvas></canvas><span>${n}</span></button>`).join("");
  applyGround();
  groundsEl.addEventListener("click", e => { const el = e.target.closest(".gcard"); if(el) setGround(el.dataset.ground); });
  // with a pointer that hovers the tray is already open, so the card itself is the
  // switch; a finger opens the tray instead and picks from it
  layersEl = document.getElementById("layers");
  document.getElementById("lcard").addEventListener("click", () => {
    if(matchMedia("(hover: hover)").matches) nextGround(); else layersEl.classList.toggle("open");
  });
  document.addEventListener("pointerdown", e => { if(!e.target.closest("#layers")) layersEl.classList.remove("open"); });
  document.querySelector(".legend").addEventListener("click", e=>{
    const el = e.target.closest("[data-layer]"); if(!el) return;
    const k = el.dataset.layer;
    HIDDEN.has(k) ? HIDDEN.delete(k) : HIDDEN.add(k);
    applyHidden();
    drawRaster();
  });
  applyHidden();
}

return {init, hidden: HIDDEN, details: DETAILS, off: OFF, applyHidden, showLayer, showDetail, markDetails, setDetail, hiddenKinds, NAME_GROUPS,
        drawThumbs, nextGround, follow, close: () => layersEl.classList.remove("open"), get ground(){ return GROUND; }};
})();
