/* map-core.js — the world map, shared by the live map and the planning board.
 *
 * The rendering rule: everything crisp is blitted or stroked into one
 * screen-space canvas per view change, never scaled by a CSS transform. Under a
 * transform Chrome GPU-scales a 1:1 raster with bilinear filtering (12 m pixels
 * came out a smear, and image-rendering cannot reach that path) and caps how
 * finely it rasterises a 2048 px vector layer (builds came out as blobs). Drawn
 * at 1:1 against the current view, both stay sharp at any zoom.
 *
 * One global, no modules, no build step: two pages load it with a plain script tag.
 */
const MapCore = (() => {
"use strict";

// ---------- the deployment ----------
// site-config.js, loaded before this file, is the one thing a deployment edits:
// where the server is, what the world render is called, whose name is on the bar.
// Absent or partial, the defaults are the mod serving these pages itself -- the
// API same-origin, the render its own /map.jpg, no brand and no links but its own.
const XNV = (typeof window !== "undefined" && window.XNV) || {};
const cfg = {
  api:    XNV.api || "",
  base:   XNV.base || "/map.jpg",
  brand:  XNV.brand || null,
  links:  Array.isArray(XNV.links) ? XNV.links
          : [{label: "Map mod", href: "https://github.com/hunter-jsb/valheim-webmap"}],
  status: XNV.status || null,
  repo:   XNV.repo || null,
};

// ---------- geometry ----------
// The map is a square texture of the world: 2048 px at 12 m a pixel, per /config.
const geom = {pixel: 12, size: 2048, world: "Mothership"};
let worldName = null;                   // what the server calls its world, once it has said
function setGeom(c){
  c = c || {};
  geom.pixel = c.pixel_size || 12;
  geom.size  = c.texture_size || 2048;
  geom.world = c.world_name || "Mothership";
  worldName = c.world_name || worldName;
  if(navEl) nav(navPage, navEl);        // an unbranded deployment wears the world's own name
  return geom;
}
// world -> texture pixels, the same transform the mod's own UI uses
function toPx(x, z){ return {px: x/geom.pixel + geom.size/2, py: geom.size/2 - z/geom.pixel}; }
function toWorld(px, py){ return {x: (px - geom.size/2)*geom.pixel, z: (geom.size/2 - py)*geom.pixel}; }

// Roofs until zoom 16: below that a 2 m piece is under 3 px and a floor plan
// would be a smudge. At 120 a 2 m piece is 20 px and a plan can be read.
const PLAN_ZOOM = 16, MAX_ZOOM = 120;

// ---------- the server ----------
// No committed snapshots: a page reads the game server live, at cfg.api -- our
// deployment through a Cloudflare Worker that adds HTTPS + CORS, the mod's own
// viewer at its own origin.
async function api(base, path){
  const r = await fetch(base + path, {cache: "no-store"});
  if(!r.ok) throw new Error(path + " " + r.status);
  return r;
}
const fetchJSON = (base, path) => api(base, path).then(r => r.json());
// a signed-in write: the session rides as a bearer, the answer is the mod's JSON
async function post(base, path, body){
  const r = await fetch(base + path, {method: "POST", headers: {"content-type": "application/json", ...authHeaders()}, body: JSON.stringify(body)});
  let j = {}; try{ j = await r.json(); }catch(e){}
  if(!r.ok) throw new Error(j.error || ("HTTP " + r.status));
  return j;
}
// One document per tick carries every small block a page shows, plus a revision
// per large layer.
const fetchState = base => fetchJSON(base, "/state");
async function fetchConfig(base){ return setGeom(await fetchJSON(base, "/config")); }

// ---------- layers ----------
// The rasters are megabytes; a revision in /state says when one actually moved,
// and ?v=<rev> lets the edge keep that version for an hour. A replacement decodes
// off screen and is swapped in, so a refresh never blanks the map.
const BASE_TEX = cfg.base;              // the world render, versioned by hand
function layers(base, onLoad){
  const imgs = {}, rev = {};
  for(const k of ["base", "forest", "struct", "fog", "chart", "trails"]){
    const img = new Image();
    img.crossOrigin = "anonymous";
    img.addEventListener("load", () => { if(onLoad) onLoad(k); });
    imgs[k] = img;
  }
  const ready = k => { const i = imgs[k]; return !!(i && i.complete && i.naturalWidth); };
  function load(k, path, v, after){
    const img = imgs[k], p = new Image();
    p.crossOrigin = "anonymous";
    p.onload = () => { img.src = p.src; if(after) after(); };
    p.src = base + path + (v == null ? "" : "?v=" + v);
  }
  // The base render is the whole world: it must never be seen without the fog
  // mask over it, so a page reveals itself only when both have decoded.
  function whenReady(keys, fn){
    let n = 0;
    const hit = () => { if(++n >= keys.length) fn(); };
    for(const k of keys){ if(ready(k)) hit(); else imgs[k].addEventListener("load", hit, {once: true}); }
  }
  // o: {pieces(list, json), structures (bool or fn, checked after pieces), onFog()}
  async function sync(r, o){
    o = o || {}; r = r || {};
    if(o.pieces && r.pieces !== rev.pieces){
      try{
        const pj = await fetchJSON(base, "/pieces?v=" + r.pieces);
        if(pj && pj.pieces){ rev.pieces = r.pieces; o.pieces(parsePieces(pj), pj); }
      }catch(e){}
    }
    if(r.forest !== rev.forest){ rev.forest = r.forest; load("forest", "/forest", r.forest); }
    const wantStruct = typeof o.structures === "function" ? o.structures() : o.structures;
    if(wantStruct && r.structures !== rev.structures){ rev.structures = r.structures; load("struct", "/structures", r.structures); }
    if(r.fog !== rev.fog){ rev.fog = r.fog; load("fog", "/fog", r.fog, o.onFog); }
    if(o.chart && r.chart && r.chart !== rev.chart){ rev.chart = r.chart; load("chart", "/chart", r.chart); }
    // the trails come only when asked for, like the structures raster
    const wantTrails = typeof o.trails === "function" ? o.trails() : o.trails;
    if(wantTrails && r.trails && r.trails !== rev.trails){ rev.trails = r.trails; load("trails", "/trails", r.trails); }
  }
  return {imgs, rev, ready, load, whenReady, sync,
          loadBase: () => load("base", BASE_TEX, "4k")};
}

// ---------- rasters ----------
// One blit per layer, of the visible window only: base, forest twice, the
// structures raster while the pieces are absent, fog last. Nearest-neighbour for
// the base once a texture pixel is bigger than a screen pixel.
// v: {scale, tx, ty, w, h, pix?}   o: {bg, forest, structures, fogReady, ground, trails}
// ground: "terrain" (the render) or "atlas" (the server's flat biome chart, when it has come)
function drawRasters(g, v, imgs, o){
  o = o || {};
  const W = v.w, H = v.h, pix = v.pix || 1;
  g.setTransform(pix, 0, 0, pix, 0, 0);
  g.globalAlpha = 1;
  g.globalCompositeOperation = "source-over";
  g.fillStyle = o.bg || "#0a0c0a";
  g.fillRect(0, 0, W, H);
  const sx = -v.tx/v.scale, sy = -v.ty/v.scale, sw = W/v.scale, sh = H/v.scale;
  const blit = (img, smooth, op) => {
    if(!img || !img.complete || !img.naturalWidth) return;
    const k = img.naturalWidth/geom.size;            // source px per texture px
    g.imageSmoothingEnabled = smooth;
    g.globalCompositeOperation = op;
    try{ g.drawImage(img, sx*k, sy*k, sw*k, sh*k, 0, 0, W, H); }catch(e){}
  };
  const fogIn = o.fogReady === undefined
    ? !!(imgs.fog && imgs.fog.complete && imgs.fog.naturalWidth) : !!o.fogReady;
  if(fogIn){
    const atlas = o.ground === "atlas" && imgs.chart && imgs.chart.complete && imgs.chart.naturalWidth;
    // the render goes crisp once its pixels outgrow the screen's; the flat chart
    // stays smoothed, a soft edge between two biomes reading better than a stair
    blit(atlas ? imgs.chart : imgs.base, !!atlas || v.scale < 3, "source-over");
    // multiplied twice, which squares the effect: dense woods go markedly darker
    // while a thinned patch barely moves, so the difference reads at a glance
    if(o.forest){ blit(imgs.forest, true, "multiply"); blit(imgs.forest, true, "multiply"); }
    if(o.structures) blit(imgs.struct, true, "source-over");
    blit(imgs.fog, true, "multiply");                // unexplored ground stays dark
    if(o.trails) blit(imgs.trails, true, "source-over");   // walked ground only, so it sits over the fog
  }
  g.globalCompositeOperation = "source-over";
  g.imageSmoothingEnabled = true;
}

// ---------- builds ----------
// The same rules the server uses for footprints, applied to how a piece is drawn.
const kindOf = n => { n = n.toLowerCase();
  return n.includes("sapling") ? "crop" : n.includes("roof") ? "roof" : n.includes("floor") ? "floor"
       : /wall|fence|gate|beam|stake/.test(n) ? "wall" : n.includes("pole") ? "pole" : "prop"; };
// Floors, walls, furniture, then roofs: up close the roof tint hides nothing,
// zoomed out the solid roof covers the beams and interior walls beneath it.
const ORDER = ["floor", "wall", "pole", "prop", "crop", "roof"];
// the pieces that burn; a fifth field on them says whether they still have fuel
const FIRE = /torch|fire_pit|bonfire|hearth|brazier|sconce|fairylight|candle|lantern/;
// Roofs are lit from the north-west: the slope facing the light goes lighter and
// the far slope darker, so a gable reads as a roof rather than a flat tile.
const LIGHT = 315;
function shade(hex, yaw){
  const f = 1 + 0.35*Math.cos((yaw - LIGHT)*Math.PI/180), v = parseInt(hex, 16);
  const ch = b => Math.min(255, Math.round(((v >> b) & 255)*f)).toString(16).padStart(2, "0");
  return ch(16) + ch(8) + ch(0);
}
// Each piece becomes a rect centred on its position, sized in texture px from its
// footprint in metres and turned by its yaw. Every storey of a build lands on the
// same footprint, so one rect per (kind, position, yaw) survives; a ridge piece
// is flat and keeps its plain colour.
// the four corners of a footprint, as offsets from its centre in texture px,
// turned the way the canvas turns a rect under rotate(yaw)
function corners(w, d, yaw){
  const t = yaw*Math.PI/180, c = Math.cos(t), s = Math.sin(t), hw = w/2, hd = d/2;
  return [-hw*c + hd*s, -hw*s - hd*c,   hw*c + hd*s,  hw*s - hd*c,
           hw*c - hd*s,  hw*s + hd*c,  -hw*c - hd*s, -hw*s + hd*c];
}
function parsePieces(json){
  const per = 1/geom.pixel, half = geom.size/2, prefabs = (json && json.prefabs) || [];
  const seen = new Set(), out = [];
  for(const [k, x, z, yaw, lit] of (json && json.pieces) || []){
    const p = prefabs[k]; if(!p) continue;
    const kind = kindOf(p.n);
    const key = kind + ":" + x.toFixed(1) + ":" + z.toFixed(1) + ":" + yaw;
    if(seen.has(key)) continue; seen.add(key);
    const q = {kind, yaw,
               px: x*per + half, py: half - z*per,
               w: Math.max(p.w*per, 0.08), d: Math.max(p.d*per, 0.08),
               c: "#" + (kind === "roof" && !p.n.includes("_top") ? shade(p.c, yaw) : p.c)};
    if(FIRE.test(p.n.toLowerCase())) q.fire = lit === undefined ? 1 : lit;   // an older server says nothing: lit
    q.o = corners(q.w, q.d, yaw);
    out.push(q);
  }
  return out.sort((a, b) => ORDER.indexOf(a.kind) - ORDER.indexOf(b.kind));
}
// The fog as a question: one downsampled read per fog image, shared by whatever
// asks whether a spot has been walked. explored(fog) is null until it has decoded.
const FOGN = 512;
let fogKey = null, fogBits = null;
function explored(fogImg){
  if(!(fogImg && fogImg.complete && fogImg.naturalWidth)) return null;
  if(fogImg.src !== fogKey){
    try{
      const c = document.createElement("canvas"); c.width = c.height = FOGN;
      const g = c.getContext("2d", {willReadFrequently: true});
      g.drawImage(fogImg, 0, 0, FOGN, FOGN);
      fogBits = g.getImageData(0, 0, FOGN, FOGN).data; fogKey = fogImg.src;
    }catch(e){ fogBits = null; fogKey = null; }
  }
  const d = fogBits; if(!d) return null;
  const k = FOGN/geom.size;
  return (px, py) => {
    const x = Math.min(FOGN-1, Math.max(0, (px*k)|0)), y = Math.min(FOGN-1, Math.max(0, (py*k)|0));
    return d[(y*FOGN + x)*4] > 40;
  };
}
// A build in ground nobody has walked is not drawn. Until the fog has decoded,
// nothing is dropped rather than everything.
function filterExplored(pieces, fogImg){
  const ok = explored(fogImg);
  return ok ? pieces.filter(p => ok(p.px, p.py)) : pieces;
}
// Up close a base reads as a floor plan: walls as lines, floors a tint, roofs
// barely there, furniture solid -- every storey lands on the same footprint, so
// opaque fills would stack to a slab. fill 1 / stroke 1 mean the piece's own colour.
const PLAN_STYLE = {
  floor: {fill: 1, alpha: .30, stroke: "rgba(40,28,16,.30)", lw: 1},
  wall:  {stroke: "rgba(38,26,14,.55)", lw: 1.2},
  pole:  {fill: 1, alpha: .8},
  prop:  {fill: 1, alpha: .9, stroke: "rgba(0,0,0,.5)", lw: 1},
  crop:  {fill: "#7fb35a", alpha: .9},
  roof:  {fill: 1, alpha: .10},
};
// Zoomed out a base is what the sky sees: its roofs. Each roof is fattened by a
// stroke in its own lit or shadowed tone, so a base stays a mark at world zoom
// without a halo colour of its own; walls thin to the faint lines of the plan.
const ROOF_STYLE = {
  floor: {fill: 1, alpha: .7},
  wall:  {stroke: "rgba(30,20,10,.45)", lw: 1},
  pole:  {fill: 1, alpha: .9},
  prop:  {fill: 1, alpha: .9},
  crop:  {fill: "#7fb35a", alpha: .9},
  roof:  {fill: 1, alpha: 1, stroke: 1, lw: 1.5},
};
// o: {plan} -- the floor plan, or the roofs. Pieces arrive already in draw order.
// One path per colour rather than a fill per piece: a base is thousands of
// footprints, and a draw call each was the cost of every frame.
function drawPieces(g, v, pieces, o){
  if(!pieces || !pieces.length) return;
  const S = (o && o.plan) ? PLAN_STYLE : ROOF_STYLE, pix = v.pix || 1, s = v.scale;
  g.save();
  g.setTransform(pix, 0, 0, pix, 0, 0);
  g.globalCompositeOperation = "source-over";
  g.lineJoin = "miter";
  let kind = null, st = null, paths = null;
  const flush = () => {
    if(!paths) return;
    for(const [c, path] of paths){
      if(st.fill){ g.globalAlpha = st.alpha; g.fillStyle = st.fill === 1 ? c : st.fill; g.fill(path); }
      if(st.stroke){ g.globalAlpha = 1; g.lineWidth = st.lw; g.strokeStyle = st.stroke === 1 ? c : st.stroke; g.stroke(path); }
    }
    paths = null;
  };
  for(const p of pieces){
    if(p.kind !== kind){ flush(); kind = p.kind; st = S[kind]; paths = st ? new Map() : null; }
    if(!paths) continue;
    const x = v.tx + p.px*s, y = v.ty + p.py*s, m = (p.w + p.d)*s;
    if(x < -m || y < -m || x > v.w + m || y > v.h + m) continue;
    const key = (st.fill === 1 || st.stroke === 1) ? p.c : "";
    let path = paths.get(key);
    if(!path){ path = new Path2D(); paths.set(key, path); }
    const q = p.o;
    path.moveTo(x + q[0]*s, y + q[1]*s);
    path.lineTo(x + q[2]*s, y + q[3]*s);
    path.lineTo(x + q[4]*s, y + q[5]*s);
    path.lineTo(x + q[6]*s, y + q[7]*s);
    path.closePath();
  }
  flush();
  g.restore();
  g.globalAlpha = 1;
}

// ---------- glows ----------
// A soft point of light at a place: the glow grows with the zoom but stays a
// point, marking the spot rather than lighting it. Drawn additively, so a
// cluster merges into one brighter glow.
function glow(g, x, y, r, c){
  const grad = g.createRadialGradient(x, y, 0, x, y, r);
  grad.addColorStop(0, `rgba(${c[0]},${c[1]},${c[2]},.95)`);
  grad.addColorStop(.35, `rgba(${c[3]},${c[4]},${c[5]},.55)`);
  grad.addColorStop(1, `rgba(${c[3]},${c[4]},${c[5]},0)`);
  g.globalCompositeOperation = "lighter";
  g.fillStyle = grad;
  g.beginPath(); g.arc(x, y, r, 0, Math.PI*2); g.fill();
  g.globalCompositeOperation = "source-over";
}
const EMBER = [255, 196, 96, 255, 140, 40], BLOOD = [255, 120, 120, 200, 40, 40];
// Torches, fire pits and hearths: warm points, merging into one glow where a
// base is kept; one that has burnt out is a grey ring.
function drawFires(g, v, pieces){
  if(!pieces || !pieces.length) return;
  const pix = v.pix || 1, r = Math.min(14, Math.max(3.5, v.scale*1.1));
  g.save();
  g.setTransform(pix, 0, 0, pix, 0, 0);
  for(const p of pieces){
    if(p.fire === undefined) continue;
    const x = v.tx + p.px*v.scale, y = v.ty + p.py*v.scale;
    if(x < -r || y < -r || x > v.w + r || y > v.h + r) continue;
    if(p.fire){
      glow(g, x, y, r, EMBER);
      g.fillStyle = "#fff1c4";
      g.beginPath(); g.arc(x, y, Math.max(1, r*.18), 0, Math.PI*2); g.fill();
    }else{
      g.strokeStyle = "rgba(210,210,200,.7)"; g.lineWidth = 1;
      g.beginPath(); g.arc(x, y, Math.max(2, r*.35), 0, Math.PI*2); g.stroke();
    }
  }
  g.restore();
}
// Where people died: a red glow each, fading with age over a month, so the
// places that keep killing burn brightest. deaths: [{px, py, t}]
function drawDeaths(g, v, deaths, now){
  if(!deaths || !deaths.length) return;
  const pix = v.pix || 1, r = Math.min(22, Math.max(5, v.scale*1.6));
  now = now || Date.now()/1000;
  g.save();
  g.setTransform(pix, 0, 0, pix, 0, 0);
  for(const d of deaths){
    const x = v.tx + d.px*v.scale, y = v.ty + d.py*v.scale;
    if(x < -r || y < -r || x > v.w + r || y > v.h + r) continue;
    const age = Math.max(0, now - (d.t || now)) / 86400;
    g.globalAlpha = age < 1 ? 1 : Math.max(.35, 1 - age/30);
    glow(g, x, y, r, BLOOD);
  }
  g.globalAlpha = 1;
  g.restore();
}

// ---------- names ----------
// The geography's names, set the way an atlas sets them: landmasses and ranges
// spaced out in capitals, water in italics, the biomes muted, rivers written
// along their course. A name shows once the fog has lifted somewhere on the
// place, and at the zooms where the place is between a thumb's width and a few
// screens; bigger kinds win the ground when two would overlap.
// features: /features rows (world metres). Returns the boxes drawn, for a tap.
const FONT = '-apple-system, "Segoe UI", Roboto, Helvetica, Arial, sans-serif';
const WATER_INK = "#9fd0e6";
const NAME_STYLE = {
  continent: {tier: 0, caps: true, space: .28, weight: 700, ink: "#f1ecdc", min: 13, max: 30, div: 9},
  range:     {tier: 1, caps: true, space: .22, weight: 600, ink: "#ffffff", min: 11, max: 20, div: 10},
  island:    {tier: 2, caps: true, space: .16, weight: 600, ink: "#e7e2d4", min: 11, max: 20, div: 8},
  lake:      {tier: 3, italic: true, ink: WATER_INK, min: 11, max: 16, div: 7},
  bay:       {tier: 3, italic: true, ink: WATER_INK, min: 11, max: 16, div: 7},
  river:     {tier: 4, italic: true, ink: WATER_INK, min: 11, max: 15},
  forest:    {tier: 5, italic: true, ink: "#a6c48a", min: 11, max: 15, div: 10, lo: 140},
  swamp:     {tier: 5, italic: true, ink: "#caa27b", min: 11, max: 15, div: 10, lo: 140},
  plains:    {tier: 5, italic: true, ink: "#e2c98f", min: 11, max: 15, div: 10, lo: 140},
  meadows:   {tier: 5, italic: true, ink: "#c2d69a", min: 11, max: 15, div: 10, lo: 140},
  mistlands: {tier: 5, italic: true, ink: "#c4a4dc", min: 11, max: 15, div: 10, lo: 140},
  ashlands:  {tier: 5, italic: true, ink: "#e6907e", min: 11, max: 15, div: 10, lo: 140},
  north:     {tier: 5, italic: true, ink: "#dde8f2", min: 11, max: 15, div: 10, lo: 140},
  holm:      {tier: 6, ink: "#d8d2c2", min: 10, max: 13, div: 6, lo: 36},
};
const overlaps = (a, b) => a.x0 < b.x1 && a.x1 > b.x0 && a.y0 < b.y1 && a.y1 > b.y0;
function font(st, fs){ return `${st.italic ? "italic " : ""}${st.weight || 500} ${fs}px ${FONT}`; }
function halo(g, fs){ g.lineJoin = "round"; g.lineWidth = Math.max(2, fs/4.5); g.strokeStyle = "rgba(10,12,10,.82)"; }
// letters set apart by hand: the canvas property is not everywhere yet
function spacedWidth(g, text, gap){ let w = 0; for(const ch of text) w += g.measureText(ch).width + gap; return w - gap; }
function spacedText(g, text, x, y, gap){
  for(const ch of text){ g.strokeText(ch, x, y); g.fillText(ch, x, y); x += g.measureText(ch).width + gap; }
}
function drawNames(g, v, features, ok, o){
  const boxes = [];
  if(!features || !features.length || !ok) return boxes;
  const pix = v.pix || 1, s = v.scale, mPx = s/geom.pixel;        // screen px per metre
  const far = 6*Math.max(v.w, v.h);
  g.save();
  g.setTransform(pix, 0, 0, pix, 0, 0);
  g.globalAlpha = 1; g.globalCompositeOperation = "source-over";
  g.textBaseline = "middle";
  const sx = (x, z) => { const p = toPx(x, z); return [v.tx + p.px*s, v.ty + p.py*s]; };
  const seen = (x, z) => { const p = toPx(x, z); return ok(p.px, p.py); };
  const list = features.filter(f => NAME_STYLE[f.kind]).slice()
    .sort((a, b) => NAME_STYLE[a.kind].tier - NAME_STYLE[b.kind].tier || b.area - a.area);
  for(const f of list){
    const st = NAME_STYLE[f.kind];
    if(f.kind === "river"){ river(g, v, f, st, ok, boxes, sx); continue; }
    const E = Math.max(f.w, f.h)*mPx;
    if(E < (st.lo || 70) || E > far) continue;
    if(!(seen(f.x, f.z) || seen(f.x - f.w/4, f.z) || seen(f.x + f.w/4, f.z) || seen(f.x, f.z - f.h/4) || seen(f.x, f.z + f.h/4))) continue;
    const [cx, cy] = sx(f.x, f.z);
    if(cx < -200 || cy < -100 || cx > v.w + 200 || cy > v.h + 100) continue;
    const fs = Math.round(Math.min(st.max, Math.max(st.min, E/st.div)));
    g.font = font(st, fs);
    const text = st.caps ? (f.name || "").toUpperCase() : (f.name || "");
    const gap = st.space ? st.space*fs : 0;
    const w = st.space ? spacedWidth(g, text, gap) : g.measureText(text).width;
    const box = {x0: cx - w/2 - 4, y0: cy - fs*.6 - 2, x1: cx + w/2 + 4, y1: cy + fs*.6 + 2, f};
    if(boxes.some(b => overlaps(b, box))) continue;
    halo(g, fs); g.fillStyle = st.ink;
    if(st.space){ g.textAlign = "left"; spacedText(g, text, cx - w/2, cy, gap); }
    else { g.textAlign = "center"; g.strokeText(text, cx, cy); g.fillText(text, cx, cy); }
    boxes.push(box);
    // a range's highest point, once the range fills a good part of the screen
    if(f.peak && E >= 260 && seen(f.peak.x, f.peak.z)){
      const [px, py] = sx(f.peak.x, f.peak.z), t = "\u25B2 " + Math.round(f.peak.y) + " m", pf = 11;
      g.font = `500 ${pf}px ${FONT}`; g.textAlign = "left";
      const pw = g.measureText(t).width, pb = {x0: px - 2, y0: py - pf, x1: px + pw + 6, y1: py + pf, f};
      if(!boxes.some(b => overlaps(b, pb))){ halo(g, pf); g.fillStyle = st.ink; g.strokeText(t, px + 4, py); g.fillText(t, px + 4, py); boxes.push(pb); }
    }
  }
  g.restore();
  return boxes;
}
// a river's name follows its course, centred on the middle of it, reading left to right
function river(g, v, f, st, ok, boxes, sx){
  if(!f.line || f.line.length < 2) return;
  let pts = f.line.map(([x, z]) => sx(x, z));
  if(pts[pts.length - 1][0] < pts[0][0]) pts = pts.reverse();
  const L = [0]; for(let i = 1; i < pts.length; i++) L.push(L[i-1] + Math.hypot(pts[i][0]-pts[i-1][0], pts[i][1]-pts[i-1][1]));
  const len = L[L.length - 1];
  if(len < 140) return;
  if(!f.line.some((q, i) => i % 3 === 0 && (() => { const p = toPx(q[0], q[1]); return ok(p.px, p.py); })())) return;
  const fs = Math.round(Math.min(st.max, Math.max(st.min, v.scale*1.4)));
  g.font = font(st, fs); g.textAlign = "center";
  const text = f.name || "", gap = fs*.08;
  const w = spacedWidth(g, text, gap);
  if(w > len*.85) return;
  const at = d => {                                  // point and angle at distance d along the line
    let i = 1; while(i < L.length - 1 && L[i] < d) i++;
    const t = (d - L[i-1])/Math.max(1e-6, L[i] - L[i-1]);
    const [ax, ay] = pts[i-1], [bx, by] = pts[i];
    return [ax + (bx-ax)*t, ay + (by-ay)*t, Math.atan2(by-ay, bx-ax)];
  };
  let d = (len - w)/2;
  const box = {x0: 1e9, y0: 1e9, x1: -1e9, y1: -1e9, f};
  const glyphs = [];
  for(const ch of text){
    const cw = g.measureText(ch).width, [x, y, a] = at(d + cw/2);
    glyphs.push([ch, x, y, a]);
    box.x0 = Math.min(box.x0, x - fs); box.y0 = Math.min(box.y0, y - fs); box.x1 = Math.max(box.x1, x + fs); box.y1 = Math.max(box.y1, y + fs);
    d += cw + gap;
  }
  if(box.x1 < 0 || box.y1 < 0 || box.x0 > v.w || box.y0 > v.h) return;
  if(boxes.some(b => overlaps(b, box))) return;
  halo(g, fs); g.fillStyle = st.ink;
  for(const [ch, x, y, a] of glyphs){
    g.save(); g.translate(x, y); g.rotate(a); g.strokeText(ch, 0, -fs*.15); g.fillText(ch, 0, -fs*.15); g.restore();
  }
  boxes.push(box);
}
const hitName = (boxes, x, y) => { const b = (boxes || []).find(b => x >= b.x0 && x <= b.x1 && y >= b.y0 && y <= b.y1); return b ? b.f : null; };

// ---------- view cache ----------
// The scene, rendered once at a zoom over a window wider than the screen: a pan
// slides it and a zoom stretches it, and the scene renders again only when the
// view runs off the window or the zoom changes -- at once where a render is
// quick, else once the zoom has settled, the stretch holding the screen until
// then. render(g, v) draws the scene for a view; fresh() hears of a render that
// landed late. The window is capped: iOS refuses a canvas past 16M px.
const CACHE_PX = 12e6, CACHE_MARGIN = .5, QUICK_MS = 20, SETTLE_MS = 90;
function viewCache(render, fresh){
  const cv = document.createElement("canvas"), g = cv.getContext("2d");
  let at = null, cost = 0, timer = 0;
  function renderAt(v){
    const pix = v.pix || 1;
    const m = Math.max(0, Math.min(CACHE_MARGIN, (Math.sqrt(CACHE_PX/(v.w*v.h*pix*pix)) - 1)/2));
    const mw = Math.round(v.w*m), mh = Math.round(v.h*m), W = v.w + 2*mw, H = v.h + 2*mh;
    if(cv.width !== W*pix || cv.height !== H*pix){ cv.width = W*pix; cv.height = H*pix; }
    const t0 = performance.now();
    at = {scale: v.scale, tx: v.tx + mw, ty: v.ty + mh, w: W, h: H, pix};
    render(g, at);
    cost = performance.now() - t0;
  }
  // paints the view onto dst, leaving its transform at the view's pixel ratio the
  // way drawRasters does; false when a stretch stood in and a render is due
  function draw(dst, v, bg){
    clearTimeout(timer);
    const pix = v.pix || 1;
    dst.setTransform(pix, 0, 0, pix, 0, 0);
    dst.globalAlpha = 1; dst.globalCompositeOperation = "source-over";
    if(at && at.pix === pix && at.scale !== v.scale && cost >= QUICK_MS){
      const r = v.scale/at.scale;
      dst.fillStyle = bg; dst.fillRect(0, 0, v.w, v.h);
      dst.imageSmoothingEnabled = true;
      dst.drawImage(cv, v.tx - at.tx*r, v.ty - at.ty*r, at.w*r, at.h*r);
      timer = setTimeout(() => { renderAt(v); fresh(); }, SETTLE_MS);
      return false;
    }
    let dx = at ? at.tx - v.tx : -1, dy = at ? at.ty - v.ty : -1;   // where the screen sits in the window
    if(!at || at.pix !== pix || at.scale !== v.scale || dx < 0 || dy < 0 || dx + v.w > at.w || dy + v.h > at.h){
      renderAt(v); dx = at.tx - v.tx; dy = at.ty - v.ty;
    }
    dst.imageSmoothingEnabled = false;
    dst.drawImage(cv, Math.round(dx*pix), Math.round(dy*pix), v.w*pix, v.h*pix, 0, 0, v.w, v.h);
    return true;
  }
  function invalidate(){ at = null; clearTimeout(timer); }
  return {draw, invalidate, cost: () => cost};
}

// ---------- marker icons ----------
// [path, fill, strokeless?] -- flat silhouettes with a dark halo (paint-order:
// stroke) so they hold over meadow green, rock grey and water; one hue per
// family. One source feeds the SVG symbols and the canvas stamps.
const ICONS = {
  "raft":[["M10 4.6h8.4v6.8h-6v3.2h-2.4z","#ae5139"],
          ["M2.6 13.8h5.8v5.6H2.6zM9.3 13.8h5.8v5.6H9.3zM16 13.8h5.8v5.6H16z","#d2a271"]],
  "karve":[["M6.4 3.2h11.2v7.6h-4.2v4h-2.8v-4H6.4z","#ae5139"],
           ["M1.8 12.6h20.4c-1 4.4-4.8 7-10.2 7S2.8 17 1.8 12.6z","#d2a271"]],
  "longship":[["M4.6 2.4h14.8v8.2h-6v4.4h-2.8v-4.4H4.6z","#ae5139"],
              ["M1.6 8.6c1.8.8 2.6 2.2 2.6 4.2h15.6c0-2 .8-3.4 2.6-4.2-.8 2-.4 3.6 0 5.2-.8 4.4-5 6.8-10.4 6.8S2.4 18.2 1.6 13.8c.4-1.6.8-3.2 0-5.2z","#d2a271"]],
  "cart":[["M16.6 7 21 3.6l1.4 2-4.4 3.4z","#c08a4a"],
          ["M2.2 6.6h17.2l-2.4 7.6H4.6z","#c08a4a"],
          ["M5.1 17a2.9 2.9 0 1 0 5.8 0a2.9 2.9 0 1 0-5.8 0","#e3bd85"],
          ["M12.5 17a2.9 2.9 0 1 0 5.8 0a2.9 2.9 0 1 0-5.8 0","#e3bd85"]],
  "portal":[["M4.4 20.6v-9a7.6 7.6 0 0 1 15.2 0v9h-4.4v-9a3.2 3.2 0 0 0-6.4 0v9z","#6fd8e6"],
            ["M9.6 20.6v-9a2.4 2.4 0 0 1 4.8 0v9z","rgba(111,216,230,.38)",1]],
  "grave":[["M12 3.2c-4.9 0-8.4 3.3-8.4 7.9 0 2.7 1.3 4.7 3 5.8v2.1c0 1 .8 1.8 1.8 1.8h7.2c1 0 1.8-.8 1.8-1.8v-2.1c1.7-1.1 3-3.1 3-5.8 0-4.6-3.5-7.9-8.4-7.9z","#d9c7a6"],
           ["M5.9 11.2a2.5 2.8 0 1 0 5 0a2.5 2.8 0 1 0-5 0","#14130e",1],
           ["M13.1 11.2a2.5 2.8 0 1 0 5 0a2.5 2.8 0 1 0-5 0","#14130e",1],
           ["M12 14.2 13.6 17h-3.2z","#14130e",1]],
  "pin-dot":[["M6.2 12a5.8 5.8 0 1 0 11.6 0a5.8 5.8 0 1 0-11.6 0","#e8b45f"]],
  "pin-fire":[["M13 2.4c.8 4-1.6 5.2-3.4 7.2-2 2.2-3.2 4.4-3.2 6.6 0 3.6 2.6 6 5.6 6s5.6-2.4 5.6-6c0-4.4-2.8-8.6-4.6-13.8z","#ef8b3f"],
              ["M12 11.6c2 2.2 3 3.6 3 5 0 1.8-1.3 3-3 3s-3-1.2-3-3c0-1.4 1-2.8 3-5z","#f8cf72",1]],
  "pin-mine":[["M5.8 18.8 9 21 16.8 9.4 13.6 7.2z","#c2914b"],
              ["M8.6 4.2Q20.4 1.2 21.6 13 15.2 8 8.6 4.2z","#e8b45f"]],
  "pin-house":[["M12 3.2 22 12h-3.2v8.4H5.2V12H2z","#e8b45f"],
               ["M10.2 20.4v-5.2h3.6v5.2z","#14130e",1]],
  "pin-cave":[["M1.8 20.6 6.4 11.4 9.2 13.4 13 7.6 16.8 13.2 18.8 11.6 22.2 20.6z","#e8b45f"],
              ["M8.4 20.6c0-3.8 1.6-6.2 3.6-6.2s3.6 2.4 3.6 6.2z","#14130e",1]],
  "trader":[["M9.2 6.6 12 2.8l2.8 3.8c3.4 1.3 5.6 4.2 5.6 7.8 0 4.4-3.7 7.6-8.4 7.6S3.6 18.8 3.6 14.4c0-3.6 2.2-6.5 5.6-7.8z","#e8b45f"],
            ["M9.4 6.2h5.2v1.7H9.4z","#14130e",1],
            ["M12 10.2c-1.9 0-3.1.9-3.1 2.2 0 2.6 6.2 1.1 6.2 3.8 0 1.4-1.4 2.3-3.1 2.3","#14130e",1]],
  "plan":[["M3.4 3.4h17.2v17.2H3.4z","#a8784a"],
          ["M11.2 3.4h1.6v17.2h-1.6zM3.4 11.2h17.2v1.6H3.4z","#0f1310",1]],
};
function spriteSVG(icons){
  icons = icons || ICONS;
  return Object.keys(icons).map(k =>
    `<symbol id="ic-${k}" viewBox="0 0 24 24" stroke="#0b0e0b" stroke-width="2.6" stroke-linejoin="round" paint-order="stroke">`
    + icons[k].map(p => `<path d="${p[0]}" fill="${p[1]}"${p[2] ? ' stroke="none"' : ""}/>`).join("")
    + `</symbol>`).join("");
}
// once per page, before anything referencing #ic-* is parsed
function injectSprite(el, icons){
  if(!el || el.dataset.sprite) return;
  el.innerHTML = spriteSVG(icons);
  el.dataset.sprite = "1";
}
// the same paths as Path2D, for stamping icons onto a canvas
function iconPaths(icons){
  icons = icons || ICONS;
  const out = {};
  for(const k in icons) out[k] = icons[k].map(p => [new Path2D(p[0]), p[1], p[2]]);
  return out;
}
// Keyed on the prefab name the mod reports. A hull it doesn't know still draws as
// a boat rather than vanishing, so a ship added in a later patch needs no change.
const VEHICLE = {
  raft:       {icon: "raft",     label: "Raft",     size: 15},
  karve:      {icon: "karve",    label: "Karve",    size: 18},
  vikingship: {icon: "longship", label: "Longship", size: 22},
  cart:       {icon: "cart",     label: "Cart",     size: 16},
};
function vehicleStyle(m){
  const v = VEHICLE[(m.name || "").toLowerCase()];
  if(v) return v;
  return m.kind === "cart" ? VEHICLE.cart : {icon: "karve", label: m.name || "Boat", size: 18};
}
const PIN_ICON = {dot: "pin-dot", fire: "pin-fire", mine: "pin-mine", house: "pin-house", cave: "pin-cave"};

// ---------- odds and ends ----------
// The mod's pin CSV: placer,id,type,owner,x,z,text -- and the text may hold commas.
// The placer is a player's platform id, or "web" for a pin placed on the site.
function parsePins(lines){
  if(typeof lines === "string") lines = lines.split("\n");
  return (lines || []).map(line => {
    const f = String(line).split(",");
    if(f.length < 6) return null;
    const x = parseFloat(f[4]), z = parseFloat(f[5]);
    if(!isFinite(x) || !isFinite(z)) return null;
    return Object.assign({id: f[1], type: f[2], owner: f[3], site: f[0] === "web", text: f.slice(6).join(",").trim(), x, z}, toPx(x, z));
  }).filter(Boolean);
}
function ago(iso){
  const d = (Date.now() - new Date(iso).getTime())/1000;
  if(!isFinite(d)) return "";
  if(d < 90) return "just now";
  if(d < 3600) return Math.round(d/60) + "m ago";
  if(d < 86400) return Math.round(d/3600) + "h ago";
  return Math.round(d/86400) + "d ago";
}
function esc(t){ return String(t).replace(/[&<>"']/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"}[c])); }

// ---------- the site chrome ----------
// Four pages wear the same bar, so a link added here reaches all of them; the
// rules it needs are in site.css. Whose name it carries and where it points out
// to are the deployment's, not this file's.
const NAV_PAGES = [["index", "Map"], ["portals", "Portals"], ["plan", "Plan"], ["players", "Players"]];
let navEl = null, navPage = "";
// Nameless until the world names itself, and "Valheim" until even that arrives.
function brand(){ return cfg.brand || worldName || "Valheim"; }
// The map is the world's own page and wears both names; the rest are "<page> -- <brand>".
function setTitle(page){
  const row = NAV_PAGES.find(p => p[0] === page);
  document.title = (!row || page === "index")
    ? (cfg.brand && worldName ? cfg.brand + " \u2014 " + worldName : brand())
    : row[1] + " \u2014 " + brand();
}
// The footer credits whoever runs this one; a deployment that names no repo has none.
function credits(){
  for(const el of document.querySelectorAll(".credits")){
    if(!cfg.repo){ el.style.display = "none"; continue; }
    for(const a of el.querySelectorAll("a.repo")) a.href = cfg.repo;
  }
}
// The sidebar, folded or not, is one choice for the whole site, kept in this
// browser; a phone ignores it and starts with its drawer shut. A toggle fires
// resize so a stage that fills the rest re-measures itself.
const PHONE = typeof matchMedia === "function" ? matchMedia("(max-width:760px)") : {matches: false};
let sideOpen = true;
function applySide(){
  try{ sideOpen = PHONE.matches ? false : localStorage.getItem("xnv.side") !== "closed"; }
  catch(e){ sideOpen = !PHONE.matches; }
  document.body.classList.toggle("side-open", sideOpen);
}
function toggleSide(on){
  sideOpen = on === undefined ? !sideOpen : !!on;
  document.body.classList.toggle("side-open", sideOpen);
  if(!PHONE.matches){ try{ localStorage.setItem("xnv.side", sideOpen ? "open" : "closed"); }catch(e){} }
  dispatchEvent(new Event("resize"));
}
if(typeof document !== "undefined"){
  if(PHONE.addEventListener) PHONE.addEventListener("change", () => { applySide(); dispatchEvent(new Event("resize")); });
  // the drawer's own ×, and a touch on the stage beside it
  document.addEventListener("click", e => { if(e.target.closest(".sideclose")) toggleSide(false); });
  document.addEventListener("pointerdown", e => {
    if(PHONE.matches && sideOpen && !e.target.closest(".sidebar, .nav")) toggleSide(false);
  });
}
// ---------- who you are ----------
// A deployment's Worker signs people in with Discord and sends the page back with
// a session token in its hash; the page keeps the token and sends it as a bearer.
// Served by the mod alone there is no /auth route, and the bar shows nothing.
let session = null, me = null;
try{
  const parts = location.hash.replace(/^#/, "").split("&").filter(Boolean);
  const got = parts.find(q => q.startsWith("session="));
  if(got){
    localStorage.setItem("xnv.session", got.slice(8));
    const rest = parts.filter(q => q !== got).join("&");
    history.replaceState(null, "", location.pathname + location.search + (rest ? "#" + rest : ""));
  }
  session = localStorage.getItem("xnv.session");
}catch(e){}
const authHeaders = () => session ? {authorization: "Bearer " + session} : {};
const user = () => me;
async function whoami(){
  let r;
  try{ r = await fetch(cfg.api + "/auth/me", {headers: authHeaders(), cache: "no-store"}); }catch(e){ return null; }
  if(r.status === 200){ me = await r.json(); }
  else if(r.status === 401){ me = null; if(session){ session = null; try{ localStorage.removeItem("xnv.session"); }catch(e){} } }
  else return null;                        // no sign-in here
  renderWho();
  return me;
}
function signIn(){ location.href = cfg.api + "/auth/login?to=" + encodeURIComponent(location.href); }
function signOut(){
  session = null; me = null;
  try{ localStorage.removeItem("xnv.session"); }catch(e){}
  renderWho();
}
function renderWho(){
  const el = navEl && navEl.querySelector(".who"); if(!el) return;
  if(!me){ el.innerHTML = '<a href="#" class="in">Sign in</a>'; el.querySelector(".in").onclick = e => { e.preventDefault(); signIn(); }; return; }
  const av = me.avatar ? `<img class="av" alt="" src="https://cdn.discordapp.com/avatars/${encodeURIComponent(me.id)}/${encodeURIComponent(me.avatar)}.png?size=64">`
                       : `<span class="av">${esc((me.name || "?").slice(0, 1))}</span>`;
  el.innerHTML = av + `<span class="nm">${esc(me.name || "")}</span><a href="#" class="out" title="Sign out">&times;</a>`;
  el.querySelector(".out").onclick = e => { e.preventDefault(); signOut(); };
}

// current defaults to the nav element's data-page; a nav with data-side gets the ☰
function nav(current, el){
  el = el || document.querySelector("nav.nav");
  if(!el) return null;
  current = current || el.dataset.page || "";
  navEl = el; navPage = current;
  const side = el.hasAttribute("data-side");
  const mark = p => p === current ? ' class="here" aria-current="page"' : "";
  el.innerHTML = (side ? '<button class="sidebtn" type="button" title="Show or hide the sidebar" aria-label="Sidebar">&#9776;</button>' : "")
    + '<span class="brand">' + esc(brand()) + '</span><div class="navlinks">'
    + NAV_PAGES.map(([p, label]) => `<a${mark(p)} href="${p}.html">${label}</a>`).join("")
    + '<span class="ext">'
    + cfg.links.map(l => `<a href="${esc(l.href)}" target="_blank" rel="noopener">${esc(l.label)}</a>`).join("")
    + '</span><span class="who"></span></div>';
  if(side){ el.querySelector(".sidebtn").addEventListener("click", () => toggleSide()); applySide(); }
  whoami();
  setTitle(current);
  if(document.readyState === "loading") document.addEventListener("DOMContentLoaded", credits, {once: true});
  else credits();
  return el;
}

return {cfg, brand, setTitle, credits, toggleSide,
        geom, setGeom, toPx, toWorld, PLAN_ZOOM, MAX_ZOOM,
        api, fetchJSON, fetchState, fetchConfig, layers, BASE_TEX,
        drawRasters, kindOf, ORDER, shade, parsePieces, explored, filterExplored, drawPieces, drawFires, drawDeaths, drawNames, hitName, viewCache, post,
        ICONS, spriteSVG, injectSprite, iconPaths, VEHICLE, vehicleStyle, PIN_ICON,
        parsePins, ago, esc, nav, user, authHeaders, whoami, signIn, signOut};
})();
