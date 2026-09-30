/* spot.js — the spotlight: one figure at a time with a line of context and a small picture, turned
 * every few seconds, held by a hover, stepped by a click. init is handed the element's id, the entries
 * as a function, the pace, when it may turn, and its shape: the sidebar's card or the players' strip.
 */
const Spot = (() => {
"use strict";
const esc = MapCore.esc, num = v => Number.isFinite(+v) ? +v : 0;

// ---------- the turn ----------
// One spotlight a page. An entry is {k, v, c, svg}: its name, the figure and the line (both html),
// and the picture, from MapCore.pic.
const S = {id: "spot", entries: () => [], every: 7000, when: () => true, wide: false, el: null, list: [], i: -1, timer: 0, held: false, html: ""};
function init(o){ Object.assign(S, {id: o.id || "spot", entries: o.entries, every: o.every || 7000, when: o.when || (() => true), wide: o.shape === "strip"}); }
function render(){
  const el = document.getElementById(S.id); if(!el) return;
  S.list = S.entries();
  el.hidden = !S.list.length; if(el.hidden) return;
  if(S.i < 0) S.i = Math.floor(Math.random()*S.list.length);
  S.i %= S.list.length;
  // a page that draws the element afresh (the players page does) has each one wired, and drawn into
  if(S.el !== el){
    S.el = el; S.html = "";
    el.classList.toggle("wide", S.wide);
    el.onmouseenter = () => { S.held = true; }; el.onmouseleave = () => { S.held = false; };
    el.onclick = () => show(true);
  }
  show(false);
  if(!S.timer) S.timer = setInterval(() => { if(!S.held && document.visibilityState === "visible" && S.when()) show(true); }, S.every);
}
function show(advance){
  const el = S.el; if(!el || !S.list.length) return;
  if(advance) S.i = (S.i + 1) % S.list.length;
  const s = S.list[S.i], n = S.list.length;
  const html = `<div class="in${advance ? " out" : ""}"><div class="eyebrow"><span>${esc(s.k)}</span><span class="n">${S.i + 1}/${n}</span></div>`
    + `<div class="v">${s.v}</div><div class="c">${s.c}</div>${s.svg}</div>`
    + `<div class="dots">${S.list.map((_, i) => `<i${i === S.i ? ' class="on"' : ""}></i>`).join("")}</div>`;
  if(!advance && html === S.html) return;      // the minute's data said nothing new: no flicker
  el.innerHTML = html; S.html = html;
  if(advance) requestAnimationFrame(() => requestAnimationFrame(() => { const i = el.querySelector(".in"); if(i) i.classList.remove("out"); S.html = el.innerHTML; }));
}
// a fresh start, as a new pick on the players page wants: anywhere in the list, the clock from nought
function reset(){ S.i = -1; clearInterval(S.timer); S.timer = 0; }

// ---------- the map's entries ----------
// One thing about the world at a time, under the tiles: how much is walked, who built the most
// of what stands, the boats by kind, the gates linked, the bosses down, where people die, the
// graves out there, the names given, and the roster's farthest, deadliest and loudest -- from
// the world as the map page holds it (W), which the tour's figures read too.
function world(W){
  const out = [], P = (W.stats && Array.isArray(W.stats.players)) ? W.stats.players : [], pic = MapCore.pic;
  const top = get => P.length ? P.slice().sort((a, b) => get(b) - get(a))[0] : null;
  const kmf = m => { const k = num(m)/1000; return k >= 100 ? Math.round(k) : k >= 10 ? k.toFixed(1) : k.toFixed(2); };
  const sum = get => P.reduce((t, q) => t + get(q), 0);
  if(W.explored != null) out.push({k: "Explored", v: `${(W.explored*100).toFixed(1)}% <small>of the world</small>`, c: "walked by someone since the fog first lifted", svg: pic.ring(W.explored, "walked, the rest still under fog")});
  if(W.pieces){ const b = top(q => num(q.pieces)), all = sum(q => num(q.pieces));
    out.push({k: "Standing", v: `${W.pieces.length.toLocaleString()} <small>pieces</small>`, c: b && num(b.pieces) ? `${esc(b.name)} built the most of it` : "built by everyone", svg: b && all ? pic.ring(num(b.pieces)/all, `of it is ${b.name}'s`) : ""}); }
  if(W.forest.stumps != null){ const b = top(q => num(q.trees));
    out.push({k: "Felled", v: `${num(W.forest.stumps).toLocaleString()} <small>trees</small>`, c: b && num(b.trees) ? `${esc(b.name)} felled the most` : "stumps standing where trees were", svg: b && num(b.trees) ? pic.strip(P, b, q => num(q.trees)) : ""}); }
  const K = W.stats && W.stats.kitchen;   // none from a mod without a kitchen
  if(K && num(K.cooked) + num(K.brewed) + num(K.smelted)){ const cook = top(q => num((q.kitchen || {}).cooked)), mine = cook ? num((cook.kitchen || {}).cooked) : 0;
    out.push({k: "Kitchen", v: `${num(K.cooked).toLocaleString()} <small>meals cooked</small>`, c: `${num(K.brewed).toLocaleString()} meads brewed · ${num(K.smelted).toLocaleString()} bars smelted`,
              svg: mine && num(K.cooked) ? pic.ring(mine/num(K.cooked), `of it cooked by ${cook.name}`) : ""}); }
  const boats = (W.vehicles || []).filter(v => v.kind === "boat");
  if(boats.length){ const kinds = {}; boats.forEach(v => { const k = MapCore.vehicleStyle(v).label; kinds[k] = (kinds[k] || 0) + 1; });
    const rows = Object.entries(kinds).sort((a, b) => b[1] - a[1]);
    out.push({k: "Afloat", v: `${boats.length} <small>${boats.length === 1 ? "boat" : "boats"}</small>`, c: rows.map(([k, n]) => `${n} ${k.toLowerCase()}${n > 1 ? "s" : ""}`).join(" \u00b7 "), svg: pic.bars(rows.slice(0, 3).map(([k, n]) => ({frac: n/boats.length, label: `${n} ${k}`})))}); }
  if(W.portals.length){ const by = new Map(W.portals.map(p => [p.id, p])); const linked = W.portals.filter(p => p.to && p.to !== "0" && p.to !== p.id && by.has(p.to)).length;
    out.push({k: "Gates", v: `${W.portals.length} <small>portals</small>`, c: `${Math.floor(linked/2)} pairs linked \u00b7 ${W.portals.length - linked} waiting for a twin`, svg: pic.ring(linked/W.portals.length, "linked to another")}); }
  if(W.stats && Array.isArray(W.stats.bosses)){ const down = MapCore.BOSSES.down(W.stats.bosses), N = Math.max(MapCore.BOSSES.order.length, down.length);
    out.push({k: "Bosses", v: `${down.length} <small>of ${N} fallen</small>`, c: down.length ? `the last to fall: ${esc(MapCore.BOSSES.label(down[down.length - 1]))}` : "every one still standing", svg: pic.ring(down.length/N, "of the bosses")}); }
  const deaths = sum(q => num(q.deaths));
  if(deaths){ const by = {}; P.forEach(q => (q.biomes || []).forEach(b => { by[b.biome] = (by[b.biome] || 0) + num(b.deaths); }));
    const rows = Object.entries(by).filter(([, n]) => n).sort((a, b) => b[1] - a[1]).slice(0, 3), placed = rows.reduce((t, r) => t + r[1], 0);
    // the ground under a death has been kept for less long than the deaths themselves: it speaks only once it covers a fair share
    const ground = placed >= deaths/4;
    out.push({k: "Deaths", v: `${deaths}`, c: ground ? `most of them in the ${esc(rows[0][0])}` : "since the count began", svg: ground ? pic.bars(rows.map(([b, n]) => ({frac: n/placed, label: `${n} ${b}`, colour: MapCore.BIOME_INK[b]}))) : ""}); }
  if(W.graves.length){ const by = {}; W.graves.forEach(g => { by[g.name] = (by[g.name] || 0) + 1; }); const rows = Object.entries(by).sort((a, b) => b[1] - a[1]).slice(0, 3);
    out.push({k: "Graves", v: `${W.graves.length} <small>out there</small>`, c: "gear still lying where someone fell", svg: pic.bars(rows.map(([n, c]) => ({frac: c/W.graves.length, label: `${c} ${n}`})))}); }
  if(W.features && W.features.length){ const named = W.features.filter(f => f.by); if(named.length){ const last = named.slice().sort((a, b) => num(b.t) - num(a.t))[0];
    out.push({k: "Named", v: `${named.length} <small>places named</small>`, c: `the latest: ${esc(last.name)}, by ${esc(last.by)}`, svg: pic.ring(named.length/W.features.length, "of the places carry a given name")}); } }
  const far = top(q => num(q.dist_m)); if(far && num(far.dist_m)) out.push({k: "Farthest", v: esc(far.name), c: `${kmf(far.dist_m)} km on foot and by boat`, svg: pic.strip(P, far, q => num(q.dist_m))});
  const kill = top(q => num(q.kills)); if(kill && num(kill.kills)) out.push({k: "Slayer", v: esc(kill.name), c: `${num(kill.kills)} kills`, svg: pic.strip(P, kill, q => num(q.kills))});
  const dead = top(q => num(q.deaths)); if(dead && num(dead.deaths)) out.push({k: "Most deaths", v: esc(dead.name), c: `${num(dead.deaths)} deaths in ${kmf(dead.dist_m)} km`, svg: pic.strip(P, dead, q => num(q.deaths))});
  const loud = top(q => num(q.chat)); if(loud && num(loud.chat)) out.push({k: "Loudest", v: esc(loud.name), c: `${num(loud.chat)} lines said in chat`, svg: pic.strip(P, loud, q => num(q.chat))});
  if(W.deaths.length){ const d = W.deaths[W.deaths.length - 1], w = MapCore.toWorld(d.px, d.py);
    out.push({k: "Latest death", v: esc(d.name), c: `${MapCore.ago(d.t*1000)} at ${Math.round(w.x)}, ${Math.round(w.z)}`, svg: ""}); }
  return out;
}

return {init, render, reset, world};
})();
