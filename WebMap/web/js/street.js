/* street.js — the map's 3D mode, stood in as Street View is: in by the pegman, the View row, a
 * card's See in 3D or #3d=, out by Map or Esc. init is handed the stage and its view, the server,
 * the rasters, the world as the page holds it, and the page's card, tray and tour. One global.
 */
const Street = (() => {
"use strict";
const esc = MapCore.esc, toPx = MapCore.toPx;
let stage, V, API, LAYERS, world, closeCard, closeTray, touring;
let v3dEl, pegEl;

// ---------- 3D ----------
// The world where you stand, as the server holds it: a mode over the stage, entered by
// the pegman, the View row in the Layers card, a card's See in 3D or #3d=<x>,<z>. The
// renderer and three.js load on the first entry and never before. Leaving puts the map
// where the camera stood and the hash back to #at.
const v3d$ = id => document.getElementById(id);
const PHONE3D = matchMedia("(max-width:760px), (pointer:coarse)").matches;
const DIRS = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
let V3D = null, V3D_LOADING = null, IN3D = false, V3D_TIME = "server", V3D_SHADOWS = !PHONE3D, RELEASE_T = 0;
function loadView3D(){
  // beside this file: a classic script's import() resolves from its own address, as card.js finds rig.js
  if(!V3D_LOADING) V3D_LOADING = import("./view3d.js").then(({View3D}) => {
    V3D = new View3D(v3d$("v3dCanvas"), {api: API, phone: PHONE3D, geom: {size: MapCore.geom.size, pixel: MapCore.geom.pixel}});
    V3D.onMove = moved3D;
    V3D.onStatus = status3D;
    V3D.setShadows(V3D_SHADOWS);
    return V3D;
  });
  return V3D_LOADING;
}
function markView(){ document.querySelectorAll("#viewsw .chip").forEach(el => el.classList.toggle("on", (el.dataset.view === "3d") === IN3D)); }
function veil3D(t){ v3d$("v3dVeil").textContent = t || ""; }
async function enter3D(x, z, look, prep){
  closeCard(); armPeg(false); closeTray();
  const q = toPx(x, z); V.centre(q.px, q.py, Math.max(V.scale, 6));   // the map behind stands where you do, close enough to see it
  const first = !IN3D;
  IN3D = true; v3dEl.hidden = false; markView();
  if(!V3D) veil3D("Loading the 3D view…");
  let v;
  try{ v = await loadView3D(); }catch(e){ veil3D("The 3D view did not load: " + (e.message || e)); return; }
  if(!IN3D) return;                                  // left again while it loaded
  if(first && world.state) feed3D(world.state);
  v.goTo(x, z, look || 0);
  if(prep) prep(v);                                  // the tour's shot, set before the first frame draws
  if(first){ v.start(); hint3D(); }
}
function leave3D(keepHash){
  if(!IN3D) return;
  IN3D = false; v3dEl.hidden = true; markView();
  v3d$("v3dTray").classList.remove("open");
  if(!V3D) return;
  const f = V3D.focus(), q = toPx(f.x, f.z);
  V3D.stop();
  // back in within two minutes is instant; a map left up longer holds no 3D world (the tour hops back in)
  clearTimeout(RELEASE_T);
  if(!touring()) letGo();
  V.centre(q.px, q.py);
  if(keepHash !== true) history.replaceState(null, "", location.pathname + location.search + `#at=${Math.round(f.x)},${Math.round(f.z)},${V.scale.toFixed(2)}`);
}
// the world built goes two minutes after the last step out, unless someone steps back in
function letGo(){ clearTimeout(RELEASE_T); RELEASE_T = setTimeout(() => { if(!IN3D && !touring()) V3D.release(); }, 120000); }
// the view's class follows its camera
function markMode(){ v3dEl.classList.toggle("orbit", V3D.mode === "orbit"); }
function feed3D(s){
  if(!V3D) return;
  V3D.setRevs(s.rev || {});
  V3D.setPlayers((s.players && s.players.players) || []);
  V3D.setTime(V3D_TIME === "server" ? (s.time ? s.time.frac : 0.5) : +V3D_TIME);
}
// where you are, in the hash as you go (a copy of the link lands on the same spot)
let placeT = 0;
function moved3D(x, z, heading){
  if(!IN3D) return;
  history.replaceState(null, "", location.pathname + location.search
    + `#3d=${Math.round(x)},${Math.round(z)}` + (heading != null ? `&look=${Math.round(heading)}` : ""));
  const at = `${Math.round(x)}, ${Math.round(z)}` + (heading != null ? ` · facing ${DIRS[Math.round(heading/45) % 8]}` : "");
  v3d$("v3dWhere").innerHTML = (v3d$("v3dWhere").dataset.name ? `<b>${esc(v3d$("v3dWhere").dataset.name)}</b> · ` : "") + at;
  clearTimeout(placeT);
  placeT = setTimeout(async () => {
    let name = "";
    try{ const j = await MapCore.fetchJSON(API, `/at?x=${x.toFixed(1)}&z=${z.toFixed(1)}`); name = ((j && j.here) || [])[0]?.name || ""; }catch(e){}
    v3d$("v3dWhere").dataset.name = name;
    v3d$("v3dWhere").innerHTML = (name ? `<b>${esc(name)}</b> · ` : "") + at;
  }, 600);
}
function status3D(s){
  const lib = s.library;
  v3dEl.dataset.ground = s.ground; v3dEl.dataset.objects = s.objects;
  v3d$("v3dStatus").textContent = `${MapCore.plural(s.ground, "chunk")} of ground \u00b7 ${s.objects.toLocaleString()} objects`
    + (lib ? ` \u00b7 ${lib.readable} models` + (lib.queued ? `, ${lib.queued} exporting` : "") : "");
  // a mod from before the 3D view answers /state without its revisions
  const before = world.state && world.state.rev && world.state.rev.objects === undefined;
  veil3D(before ? "The server’s map mod has no 3D view yet: it comes with the mod’s next restart."
    : s.unwalked ? "Nobody has walked here yet, so there is nothing to show."
    : s.ground ? "" : "Reading the ground…");
}
let hintT = 0;
function hint3D(orbit){
  const h = v3d$("v3dHint");
  h.textContent = orbit
    ? (PHONE3D ? "Drag to pan · two fingers to turn and zoom"
               : "Drag to pan, right-drag to turn · W A S D to fly over · E and Q up and down · Shift faster")
    : PHONE3D ? "Drag to look around · tap the ground to walk there"
    : "Drag to look · W A S D to walk · click the ground to go there · Esc for the map";
  h.classList.remove("gone");
  clearTimeout(hintT); hintT = setTimeout(() => h.classList.add("gone"), 6000);
}
// The pegman: dragged from the column and dropped on the map, or on a touch screen
// armed by a tap and dropped by the next tap on the map. Only onto walked ground.
let PEG_ARMED = false, pegDrag = null;
function armPeg(on){ PEG_ARMED = on; pegEl.classList.toggle("on", on); stage.classList.toggle("dropping", on); }

function dropPeg(x, y){
  armPeg(false);
  const q = V.at(x, y), w = MapCore.toWorld(q.px, q.py), walked = MapCore.explored(LAYERS.imgs.fog);
  if(walked && !walked(q.px, q.py)){
    const n = document.createElement("div");
    n.className = "dropnote glass"; n.textContent = "Nobody has walked here yet";
    n.style.left = x + "px"; n.style.top = y + "px";
    stage.appendChild(n); setTimeout(() => n.remove(), 1800);
    return;
  }
  location.hash = `#3d=${Math.round(w.x)},${Math.round(w.z)}`;
}
function pegEnd(e){
  const d = pegDrag; if(!d || d.id !== e.pointerId) return;
  pegDrag = null;
  if(!d.ghost){ if(e.type === "pointerup") armPeg(!PEG_ARMED); return; }   // a tap arms it
  d.ghost.remove(); armPeg(false);
  if(e.type !== "pointerup") return;
  const r = stage.getBoundingClientRect(), x = e.clientX - r.left, y = e.clientY - r.top;
  const over = document.elementFromPoint(e.clientX, e.clientY);
  if(x < 0 || y < 0 || x > r.width || y > r.height || (over && over.closest(".mapctl, .layers, #mini, #namer"))) return;
  dropPeg(x, y);
}
// a link's #3d=<x>,<z>[&look=<degrees>]: true when it named a spot to stand on
function follow(p){
  const [x, z] = p["3d"].split(",").map(Number), look = p.look !== undefined && isFinite(+p.look) ? +p.look : undefined;
  if(!isFinite(x) || !isFinite(z)) return false;
  enter3D(x, z, look); return true;
}

function init(o){
  ({stage, V, api: API, layers: LAYERS, world, closeCard, closeTray, touring} = o);
  v3dEl = document.getElementById("v3d"); pegEl = document.getElementById("pegman");
  v3d$("v3dExit").onclick = leave3D;
  v3d$("v3dMode").onclick = () => {
    if(!V3D) return;
    const m = V3D.mode === "street" ? "orbit" : "street";
    V3D.setMode(m);
    v3dEl.classList.toggle("orbit", m === "orbit");
    v3d$("v3dMode").title = m === "street" ? "Overview: circle the spot from above" : "Street: stand on the spot";
    hint3D(m === "orbit");
  };
  v3d$("v3dOpts").onclick = () => v3d$("v3dTray").classList.toggle("open");
  v3d$("v3dTime").addEventListener("click", e => {
    const el = e.target.closest(".chip[data-t]"); if(!el) return;
    V3D_TIME = el.dataset.t;
    v3d$("v3dTime").querySelectorAll(".chip[data-t]").forEach(b => b.classList.toggle("on", b === el));
    if(world.state) feed3D(world.state);
  });
  v3d$("v3dShadows").classList.toggle("on", V3D_SHADOWS);
  v3d$("v3dShadows").onclick = () => {
    V3D_SHADOWS = !V3D_SHADOWS; v3d$("v3dShadows").classList.toggle("on", V3D_SHADOWS);
    if(V3D) V3D.setShadows(V3D_SHADOWS);
  };
  v3d$("v3dCats").addEventListener("click", e => {
    const el = e.target.closest(".chip"); if(!el) return;
    el.classList.toggle("on");
    if(V3D) V3D.setCategories([...v3d$("v3dCats").querySelectorAll(".chip.on")].map(b => b.dataset.c));
  });
  // the map's own view must not pan or zoom under the 3D one
  v3dEl.addEventListener("wheel", e => e.stopPropagation());
  // the View row: 3D stands you in the middle of the map
  document.getElementById("viewsw").addEventListener("click", e => {
    const el = e.target.closest(".chip"); if(!el) return;
    if(el.dataset.view === "map") return leave3D();
    const q = V.at(stage.clientWidth/2, stage.clientHeight/2), w = MapCore.toWorld(q.px, q.py);
    location.hash = `#3d=${Math.round(w.x)},${Math.round(w.z)}`;
  });
  pegEl.addEventListener("pointerdown", e => {
    e.preventDefault();
    try{ pegEl.setPointerCapture(e.pointerId); }catch(err){}
    pegDrag = {id: e.pointerId, x: e.clientX, y: e.clientY, ghost: null};
  });
  pegEl.addEventListener("pointermove", e => {
    const d = pegDrag; if(!d || d.id !== e.pointerId) return;
    if(!d.ghost && Math.hypot(e.clientX - d.x, e.clientY - d.y) > 6){
      d.ghost = document.createElementNS("http://www.w3.org/2000/svg", "svg");
      d.ghost.setAttribute("class", "pegghost"); d.ghost.innerHTML = '<use href="#ic-pegman"></use>';
      document.body.appendChild(d.ghost);
      pegEl.classList.add("on"); stage.classList.add("dropping");
    }
    if(d.ghost){ d.ghost.style.left = e.clientX + "px"; d.ghost.style.top = e.clientY + "px"; }
  });
  pegEl.addEventListener("pointerup", pegEnd);
  pegEl.addEventListener("pointercancel", pegEnd);
}

return {init, load: loadView3D, feed: feed3D, enter: enter3D, leave: leave3D, follow, armPeg, drop: dropPeg, markMode, letGo,
        get view(){ return V3D; }, get on(){ return IN3D; }, get armed(){ return PEG_ARMED; }};
})();
