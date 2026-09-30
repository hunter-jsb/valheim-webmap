// The map's spotlight entries under `node --test`: spot.js loads after map-core.js, as the
// page loads them, each a plain script defining one global. Nothing here draws.
import { readFileSync } from "node:fs";
import { test } from "node:test";
import assert from "node:assert/strict";

const src = f => readFileSync(new URL(`../../WebMap/web/${f}`, import.meta.url), "utf8");
const browser = {window: {}, document: {readyState: "complete", addEventListener(){}, querySelector: () => null, querySelectorAll: () => []},
  localStorage: {getItem: () => null, setItem(){}}, matchMedia: () => ({matches: false, addEventListener(){}}), location: {hash: "", pathname: "/", search: ""}};
const MapCore = new Function(...Object.keys(browser), src("map-core.js") + "\nreturn MapCore;")(...Object.values(browser));
const Spot = new Function("MapCore", src("js/spot.js") + "\nreturn Spot;")(MapCore);
// the page before its first tick: empty lists, nothing fetched
const bare = {stats: null, explored: null, pieces: null, forest: {}, vehicles: null, portals: [], graves: [], features: null, deaths: []};

// the tour's world-in-numbers interludes pick among these by name
test("a world with nothing yet has nothing to spotlight, and one with one of everything has every figure", () => {
  assert.deepEqual(Spot.world(bare), []);
  const Rurik = {name: "Rurik", pieces: 9, trees: 4, deaths: 2, dist_m: 1500, kills: 3, chat: 5, kitchen: {cooked: 1}, biomes: [{biome: "Swamp", deaths: 2}]};
  const all = {stats: {players: [Rurik], bosses: [{key: "defeated_eikthyr"}], kitchen: {cooked: 1}}, explored: 0.1, pieces: [{}], forest: {stumps: 3},
    vehicles: [{kind: "boat", name: "Karve"}], portals: [{id: "a", to: "b"}, {id: "b", to: "a"}], graves: [{name: "Rurik"}],
    features: [{name: "Rurik's Rest", by: "Rurik", t: 1}], deaths: [{name: "Rurik", t: 1, px: 1024, py: 1024}]};
  assert.deepEqual(Spot.world(all).map(e => e.k), ["Explored", "Standing", "Felled", "Kitchen", "Afloat", "Gates", "Bosses", "Deaths", "Graves", "Named",
    "Farthest", "Slayer", "Most deaths", "Loudest", "Latest death"]);
});

test("the deaths name their ground only once the ground covers a quarter of them", () => {
  const deaths = placed => Spot.world({...bare, stats: {players: [{name: "Rurik", deaths: 20, biomes: [{biome: "Swamp", deaths: placed}]}]}}).find(e => e.k === "Deaths");
  assert.match(deaths(5).c, /Swamp/);
  assert.notEqual(deaths(5).svg, "");
  assert.doesNotMatch(deaths(4).c, /Swamp/);
  assert.equal(deaths(4).svg, "");
});
