// map-core.js's pure functions under `node --test`: the file loads as the pages
// load it, a plain script defining one global, with just enough of a browser
// for what it touches as it loads. Nothing here draws.
import { readFileSync } from "node:fs";
import { test } from "node:test";
import assert from "node:assert/strict";

const src = readFileSync(new URL("../../WebMap/web/map-core.js", import.meta.url), "utf8");
const browser = {
  window: {},                                            // no site-config.js: the mod's own defaults
  document: {readyState: "complete", addEventListener(){}, querySelector: () => null, querySelectorAll: () => []},
  localStorage: {getItem: () => null, setItem(){}, removeItem(){}},
  matchMedia: () => ({matches: false, addEventListener(){}}),
  location: {hash: "", pathname: "/", search: "", href: "http://localhost/"},
  history: {replaceState(){}},
};
const MapCore = new Function(...Object.keys(browser), src + "\nreturn MapCore;")(...Object.values(browser));
const near = (a, b) => assert.ok(Math.abs(a - b) < 1e-6, `${a} is not ${b}`);

// ---------- pins ----------

test("a pin line is placer,id,type,owner,x,z,text and the text keeps its commas", () => {
  const [p] = MapCore.parsePins("Steam_76561198055335685,1789275213-7472,fire,Grant,10.00,-20.00,Camp, north ");
  const {px, py} = MapCore.toPx(10, -20);
  assert.deepEqual(p, {id: "1789275213-7472", type: "fire", owner: "Grant", site: false, text: "Camp, north", x: 10, z: -20, px, py});
});

test("a pin placed on the site is marked as one", () => {
  assert.equal(MapCore.parsePins(["web,w1727000000000,dot,Withers,0,0,"])[0].site, true);
});

test("a line that is not a pin is dropped, not drawn at the origin", () => {
  const pins = MapCore.parsePins(["", "web,w1,dot,Withers,1,2", "web,w2,dot,Withers", "web,w3,dot,Withers,east,2,x", "web,w4,dot,Withers,1,NaN,x"].join("\n"));
  assert.deepEqual(pins.map(p => p.id), ["w1"]);
  assert.deepEqual(MapCore.parsePins(null), []);
});

// ---------- pieces ----------

const prefabs = [
  {n: "wood_floor", w: 2, d: 2, c: "966c42"}, {n: "wood_roof", w: 2, d: 2, c: "a0522d"}, {n: "stake_wall", w: 2, d: 0.3, c: "966c42"},
  {n: "piece_groundtorch_wood", w: 0.5, d: 0.5, c: "c08a4a"}, {n: "fire_pit", w: 2, d: 2, c: "777777"}, {n: "piece_chest_wood", w: 1, d: 0.5, c: "966c42"},
  {n: "wood_beam", w: 4, d: 1, c: "966c42"},
];

test("each piece is a footprint of its kind, drawn floors first and roofs last", () => {
  const out = MapCore.parsePieces({prefabs, pieces: [[1, 0, 0, 0], [5, 4, 0, 0], [2, 8, 0, 0], [0, 12, 0, 0]]});
  assert.deepEqual(out.map(p => p.kind), ["floor", "wall", "prop", "roof"]);
});

test("every storey of a build lands on one footprint", () => {
  const out = MapCore.parsePieces({prefabs, pieces: [[0, 10, 10, 0], [0, 10, 10, 0], [0, 10.02, 10, 0], [0, 10, 10, 90]]});
  assert.equal(out.length, 2);                         // the same spot and turn is one; a turn is another
});

test("a fire carries its fuel, and one from an older server is lit", () => {
  const out = MapCore.parsePieces({prefabs, pieces: [[3, 0, 0, 0, 0], [4, 10, 0, 0], [5, 20, 0, 0]]});
  const at = x => out.find(p => Math.abs(p.px - MapCore.toPx(x, 0).px) < 1e-6);
  assert.equal(at(0).fire, 0);
  assert.equal(at(10).fire, 1);
  assert.equal("fire" in at(20), false);
});

test("a piece turns as the game turns it: a positive yaw takes east round to south, clockwise on screen", () => {
  const [flat] = MapCore.parsePieces({prefabs, pieces: [[6, 0, 0, 0]]});
  const [turned] = MapCore.parsePieces({prefabs, pieces: [[6, 0, 0, 90]]});
  const span = o => [Math.max(o[0], o[2], o[4], o[6]) - Math.min(o[0], o[2], o[4], o[6]), Math.max(o[1], o[3], o[5], o[7]) - Math.min(o[1], o[3], o[5], o[7])];
  near(span(flat.o)[0], 4/12); near(span(flat.o)[1], 1/12);       // a 4 m beam at 12 m a pixel, running east
  near(span(turned.o)[0], 1/12); near(span(turned.o)[1], 4/12);
  const [a45] = MapCore.parsePieces({prefabs, pieces: [[6, 0, 0, 45]]});
  const pts = [0, 2, 4, 6].map(i => [a45.o[i], a45.o[i + 1]]);
  const east = pts.reduce((m, p) => p[0] > m[0] ? p : m);          // its eastmost corner has come round below
  assert.ok(east[1] > 0);
});

// ---------- the map's geometry ----------

test("world and map pixels round-trip, north up and east right", () => {
  for (const [x, z] of [[0, 0], [1234.5, -987.25], [-12000, 12000]]){
    const {px, py} = MapCore.toPx(x, z), w = MapCore.toWorld(px, py);
    near(w.x, x); near(w.z, z);
  }
  const c = MapCore.toPx(0, 0), ne = MapCore.toPx(100, 100);
  assert.ok(ne.px > c.px && ne.py < c.py);
  try{
    MapCore.setGeom({pixel_size: 6, texture_size: 4096});
    const q = MapCore.toPx(600, -600);
    assert.deepEqual([q.px, q.py], [2148, 2148]);
  }finally{ MapCore.setGeom({}); }
});

test("a tap finds the name drawn under it, and nothing off every name", () => {
  const lake = {id: "lake@0,0"}, range = {id: "range@0,0"};
  const boxes = [{x0: 10, y0: 10, x1: 50, y1: 20, f: lake}, {x0: 60, y0: 10, x1: 90, y1: 30, f: range}];
  assert.equal(MapCore.hitName(boxes, 30, 15), lake);
  assert.equal(MapCore.hitName(boxes, 90, 30), range);
  assert.equal(MapCore.hitName(boxes, 55, 15), null);
  assert.equal(MapCore.hitName(null, 30, 15), null);
});

// ---------- odds and ends ----------

test("text from players cannot become markup", () => {
  assert.equal(MapCore.esc(`<img src=x onerror="alert('hi')">&`), "&lt;img src=x onerror=&quot;alert(&#39;hi&#39;)&quot;&gt;&amp;");
  assert.equal(MapCore.esc(42), "42");
});

test("an age reads in the unit it is best read in, and nothing for a bad time", () => {
  const ago = s => MapCore.ago(new Date(Date.now() - s*1000).toISOString());
  assert.equal(ago(-120), ago(5));                     // a server clock ahead of ours is still just now
  assert.match(ago(10*60), /^10m/);
  assert.match(ago(5*3600), /^5h/);
  assert.match(ago(3*86400), /^3d/);
  assert.equal(MapCore.ago("not a time"), "");
});

test("a boat is drawn as its hull, and one the viewer has never heard of is still a boat", () => {
  assert.equal(MapCore.vehicleStyle({kind: "boat", name: "VikingShip"}).icon, "longship");
  assert.equal(MapCore.vehicleStyle({kind: "boat", name: "karve"}).label, "Karve");
  assert.deepEqual(MapCore.vehicleStyle({kind: "boat", name: "Drakkar"}), {icon: "karve", label: "Drakkar", size: 18});
  assert.equal(MapCore.vehicleStyle({kind: "cart", name: "Wagon"}).icon, "cart");
  assert.equal(MapCore.vehicleStyle({kind: "boat"}).label, "Boat");
});
