/* card.js — a player as the roster reads them, shared by the players page and the map's tour:
 * the class, the pentagon, the four figures and the Viking on a turntable.
 *
 * One global, no modules, as map-core.js: both pages' scripts are plain scripts. three.js
 * is imported only when a look is first shown.
 */
const PlayerCard = (() => {
"use strict";
const esc = MapCore.esc;
function num(v){ return Number.isFinite(+v) ? +v : 0; }

// Coarse on purpose: this says how stale a tally is, never how long anyone played.
function ago(secs){
  const d = Math.max(0, Math.floor(Date.now()/1000) - secs);
  if(d < 90) return "moments ago";
  if(d < 5400) return Math.round(d/60) + " min ago";
  if(d < 172800) return Math.round(d/3600) + " h ago";
  return Math.round(d/86400) + " d ago";
}
// last_seen 0 is a name the sweep found on a grave or a build without ever
// seeing its owner online: no session behind it, so no elapsed time to show.
function seen(t){ return t ? "last seen " + ago(t) : "not seen since the tally began"; }

const km = m => { const k = num(m)/1000; return k >= 100 ? Math.round(k) : k >= 10 ? k.toFixed(1) : k.toFixed(2); };
// deaths per km reads as "1 per N km": one death every so far, which is what people say
const per = (deaths, m) => { deaths = num(deaths); const k = num(m)/1000; if(!deaths) return k >= 1 ? "none yet" : "—"; if(k <= 0) return "—"; const n = k/deaths; return "1 per " + (n >= 10 ? Math.round(n) : n >= 1 ? n.toFixed(1) : n.toFixed(2)) + " km"; };

// ---------- the pentagon ----------
// Five sides, each the player's share of the roster's best, square-rooted: a
// giant still reaches the rim, and everyone else keeps a shape worth reading.
// A side made of several figures is the mean of their shares, over the figures
// anyone on the roster has at all.
const sea = p => num(((p.biomes || []).find(b => b.biome === "Ocean") || {}).m);
const kpd = p => num(p.dist_m) >= 1000 ? num(p.dist_m)/1000/Math.max(1, num(p.deaths)) : 0;
const kit = p => p.kitchen || {};   // none from a mod that counts no kitchen
const PARTS = {km: p => num(p.dist_m), hops: p => num(p.hops), sea, pieces: p => num(p.pieces), kpd, kills: p => num(p.kills), trees: p => num(p.trees), rocks: p => num(p.rocks),
  cooked: p => num(kit(p).cooked), smelted: p => num(kit(p).smelted)};
const BEST = {};
// the rim is the roster's best on each side
function rim(roster){ for(const k in PARTS) BEST[k] = Math.max(0, ...roster.map(PARTS[k])); }
const share = (p, keys) => { const has = keys.filter(k => BEST[k] > 0); return has.length ? has.reduce((t, k) => t + Math.min(1, PARTS[k](p)/BEST[k]), 0)/has.length : 0; };
const AXES = [
  ["Explorer", ["km", "hops", "sea"], p => `${km(p.dist_m)} km · ${num(p.hops)} hops${BEST.sea > 0 ? ` · ${km(sea(p))} at sea` : ""}`],
  ["Builder", ["pieces"], p => `${num(p.pieces)} pieces`],
  ["Survivor", ["kpd"], p => `${kpd(p) >= 10 ? Math.round(kpd(p)) : kpd(p).toFixed(1)} km per death`],
  ["Slayer", ["kills"], p => `${num(p.kills)} kills`],
  ["Harvester", ["trees", "rocks", "cooked", "smelted"], p => `${num(p.trees)} trees · ${num(p.rocks)} rocks${BEST.cooked > 0 || BEST.smelted > 0 ? ` · ${num(kit(p).cooked) + num(kit(p).smelted)} made` : ""}`],
];
function radar(p, size, big){
  const c = size/2, R = big ? c - 30 : c - 3, pts = [];
  AXES.forEach(([, keys], i) => {
    const a = -Math.PI/2 + i*2*Math.PI/5, r = R*Math.sqrt(share(p, keys));
    pts.push([c + r*Math.cos(a), c + r*Math.sin(a), a]);
  });
  const ring = k => AXES.map((_, i) => { const a = -Math.PI/2 + i*2*Math.PI/5; return `${(c + R*k*Math.cos(a)).toFixed(1)},${(c + R*k*Math.sin(a)).toFixed(1)}`; }).join(" ");
  let out = `<svg class="radar${big ? " big" : ""}" viewBox="0 0 ${size} ${size}" aria-hidden="true">`
    + [.25, .5, .75, 1].map(k => `<polygon class="grid" points="${ring(k)}"/>`).join("")
    + pts.map(([, , a]) => `<line class="spoke" x1="${c}" y1="${c}" x2="${(c + R*Math.cos(a)).toFixed(1)}" y2="${(c + R*Math.sin(a)).toFixed(1)}"/>`).join("")
    + `<polygon class="fill" points="${pts.map(([x, y]) => x.toFixed(1) + "," + y.toFixed(1)).join(" ")}"/>`;
  if(big){
    out += pts.map(([x, y]) => `<circle class="pt" cx="${x.toFixed(1)}" cy="${y.toFixed(1)}" r="2.6"/>`).join("");
    AXES.forEach(([name, , text], i) => {
      const a = -Math.PI/2 + i*2*Math.PI/5, lx = c + (R + 14)*Math.cos(a), ly = c + (R + 14)*Math.sin(a);
      const anchor = Math.abs(Math.cos(a)) < .2 ? "middle" : Math.cos(a) > 0 ? "start" : "end";
      out += `<text class="lb" x="${lx.toFixed(1)}" y="${(ly + (Math.sin(a) > .5 ? 6 : Math.sin(a) < -.5 ? -4 : 0)).toFixed(1)}" text-anchor="${anchor}">${name}</text>`
        + `<text class="lv" x="${lx.toFixed(1)}" y="${(ly + (Math.sin(a) > .5 ? 17 : Math.sin(a) < -.5 ? 7 : 11)).toFixed(1)}" text-anchor="${anchor}">${esc(text(p))}</text>`;
    });
  }
  return out + `</svg>`;
}
// A class is three parts. The first word is a compound of two: what is fought with
// (the prefix: battle, blade, shield, bow, knife, spell) and what the body is (the root:
// mage, knight, warrior, rogue, brute), read from the clothes and the food. The second
// word is the trade, from the deeds. The gear decides the body -- robes and eitr make a
// mage however long the hammer was out tonight -- and any part may be missing. The
// server sees blows landed only on creatures another player owns, and never the foods
// themselves, only the health, stamina and eitr they add; time in hand is the evidence.
const FAMILY_WORD = {bow: "a bow", crossbow: "a crossbow", staff: "a staff", shield: "a shield", knife: "a knife", sword: "a sword", axe: "an axe", mace: "a mace", spear: "a spear", polearm: "an atgeir", hammer: "the hammer", hoe: "the hoe", cultivator: "the cultivator", pickaxe: "a pickaxe", torch: "a torch", fishing: "a fishing rod", tool: "a tool"};
const ARMOUR_WORD = {mage: "eitr-weave", heavy: "heavy armour", medium: "medium armour", light: "light armour", none: "no armour"};
// an item's family from its prefab name, for what is carried but not being held
function familyOf(name){
  const n = String(name || "").toLowerCase(); if(!n) return null;
  if(/crossbow|arbalest/.test(n)) return "crossbow";
  if(/^bow/.test(n)) return "bow";
  if(/^staff/.test(n)) return "staff";
  if(/^shield/.test(n)) return "shield";
  if(/^knife/.test(n)) return "knife";
  if(/^(thsword|sword)/.test(n)) return "sword";
  if(/^(battleaxe|axe)/.test(n)) return "axe";
  if(/^(sledge|mace|club)/.test(n)) return "mace";
  if(/^spear/.test(n)) return "spear";
  if(/^atgeir/.test(n)) return "polearm";
  if(/^hammer$/.test(n)) return "hammer";
  if(/^hoe$/.test(n)) return "hoe";
  if(/^cultivator$/.test(n)) return "cultivator";
  if(/^pickaxe/.test(n)) return "pickaxe";
  if(/^(torch|lantern)/.test(n)) return "torch";
  if(/fishingrod/.test(n)) return "fishing";
  return "tool";
}
const FIGHT = {staff: "magic", bow: "ranged", crossbow: "ranged", shield: "shield", knife: "knife", sword: "melee", axe: "melee", mace: "melee", spear: "melee", polearm: "melee"};
const twoHander = n => /^(thsword|battleaxe|sledge|atgeir)/i.test(String(n || ""));
const seaShare = p => { const b = p.biomes || [], all = b.reduce((t, x) => t + num(x.m), 0); return all ? num((b.find(x => x.biome === "Ocean") || {}).m)/all : 0; };
// the compound, by the body (rows) and what it fights with (columns)
const TITLE = {
  mage:    {none: "Mage", spell: "Mage", battle: "Battlemage", blade: "Spellsword", shield: "Spellguard", bow: "Arcane Archer", knife: "Nightblade"},
  knight:  {none: "Knight", spell: "Templar", battle: "Champion", blade: "Warrior", shield: "Paladin", bow: "Marksman", knife: "Executioner"},
  warrior: {none: "Soldier", spell: "Warlock", battle: "Reaver", blade: "Fighter", shield: "Warden", bow: "Ranger", knife: "Rogue"},
  rogue:   {none: "Rogue", spell: "Enchanter", battle: "Berserker", blade: "Duelist", shield: "Skirmisher", bow: "Archer", knife: "Assassin"},
  brute:   {none: "Brute", spell: "Shaman", battle: "Berserker", blade: "Bruiser", shield: "Guardian", bow: "Hunter", knife: "Cutthroat"},
  none:    {none: null, spell: "Sorcerer", battle: "Berserker", blade: "Brawler", shield: "Sentinel", bow: "Archer", knife: "Cutpurse"},
};
const DIET_WORD = {hearty: "hearty food", quick: "light, quick food", eitr: "eitr food", balanced: "a balanced table"};
// what the stone oven bakes: bread, the pies, and the platters that go in raw
const OVEN = /^(Bread|FishAndBread|LoxPie|HoneyGlazedChicken|MeatPlatter|MisthareSupreme|MagicallyStuffedShroom|PiquantPie|RoastedCrustPie)$/;
// roster: everyone tallied, for the roster's best and the pace of its deaths
function classify(p, roster){
  const g = p.gear || {};
  // 1. the body: clothes, and the food behind the health, stamina and eitr
  const arm = g.armor || {}, armTotal = Object.values(arm).reduce((t, v) => t + num(v), 0);
  const A = armTotal ? Object.entries(arm).sort((a, b) => num(b[1]) - num(a[1]))[0][0] : "none";
  const diet = g.diet || {}, fed = Object.entries(diet).filter(([k]) => k !== "none"), fedTotal = fed.reduce((t, [, v]) => t + num(v), 0);
  const D = fedTotal && fedTotal >= num(diet.none) ? fed.sort((a, b) => num(b[1]) - num(a[1]))[0][0] : null;   // unfed more often than not says nothing
  const root = A === "mage" || (D === "eitr" && A !== "heavy") ? "mage" : A === "heavy" ? "knight" : A === "medium" ? (D === "quick" ? "rogue" : "warrior")
    : A === "light" ? (D === "hearty" ? "warrior" : "rogue") : D === "hearty" ? "brute" : "none";
  const food = g.food || null, fedWords = food ? [num(food.hp) && `${Math.round(num(food.hp))} health`, num(food.st) && `${Math.round(num(food.st))} stamina`, num(food.eitr) && `${Math.round(num(food.eitr))} eitr`].filter(Boolean) : [];
  const bodyWhy = [armTotal && (ARMOUR_WORD[A] || A), D && DIET_WORD[D], fedWords.length ? "fed for " + fedWords.join(", ") : food && !D && "unfed"].filter(Boolean);
  // 2. the weapons: a minute of fighting gear in hand decides; before that, what is carried
  const hand = g.hand || {}, held = Object.entries(hand).filter(([k]) => k !== "none" && k !== "twohanded"), heldTotal = held.reduce((t, [, v]) => t + num(v), 0);
  const fought = held.filter(([k]) => FIGHT[k]), fightTotal = fought.reduce((t, [, v]) => t + num(v), 0);
  const worn = g.worn || {}, carriedNames = [worn.right, worn.left, worn.rightBack, worn.leftBack].filter(n => FIGHT[familyOf(n)]), carried = [...new Set(carriedNames.map(familyOf))];
  const F = {magic: 0, ranged: 0, shield: 0, knife: 0, melee: 0}, FS = {}, armsWhy = [];
  let twoh = 0;
  if(fightTotal >= 60){
    for(const [k, v] of fought){ FS[k] = num(v)/fightTotal; F[FIGHT[k]] += FS[k]*(k === "axe" ? .5 : 1); }   // an axe is half a tool
    twoh = num(hand.twohanded)/fightTotal;
    armsWhy.push(fought.slice().sort((a, b) => num(b[1]) - num(a[1])).slice(0, 2).map(([k, v]) => `${FAMILY_WORD[k] || k} ${Math.round(100*num(v)/heldTotal)}%`).join(" and ") + " of the time in hand" + (twoh >= .5 ? ", two-handed" : ""));
  } else if(carried.length){
    for(const k of carried){ FS[k] = 1; F[FIGHT[k]] += 1; }
    twoh = carriedNames.some(twoHander) ? 1 : 0;
    armsWhy.push(`carries ${carried.map(k => FAMILY_WORD[k]).join(" and ")}`);
  }
  const hits = g.hits || {}, hitTotal = num(hits.melee) + num(hits.ranged) + num(hits.magic);
  if(hitTotal >= 10){
    F.magic += num(hits.magic)/hitTotal*.5; F.ranged += num(hits.ranged)/hitTotal*.5; F.melee += num(hits.melee)/hitTotal*.3; F.knife += num(hits.backstab)/hitTotal*.5;
    const kind = [["ranged", "shots"], ["magic", "spells"], ["backstab", "backstabs"], ["melee", "blows"]].sort((a, b) => num(hits[b[0]]) - num(hits[a[0]]))[0];
    armsWhy.push(`${num(hits[kind[0]])} ${kind[1]} landed`);
  }
  // a quarter of the fighting counts as a way of fighting; a mage's staff is already in the root, so the rest names the prefix
  const facets = Object.entries(F).filter(([, v]) => v >= .25).sort((a, b) => b[1] - a[1]), has = k => facets.some(f => f[0] === k);
  const lead = (root === "mage" ? facets.find(f => f[0] !== "magic") || facets[0] : facets[0])?.[0] || null, shielded = has("shield") || carried.includes("shield");
  const fam = k => num(FS[k]), steel = ["sword", "axe", "mace", "spear", "polearm"].sort((a, b) => fam(b) - fam(a))[0];
  const prefix = lead === "magic" ? "spell" : lead === "ranged" ? "bow" : lead === "knife" ? "knife" : lead === "shield" ? "shield" : lead === "melee" ? (twoh >= .5 ? "battle" : shielded ? "shield" : "blade") : "none";
  const hasGear = armTotal > 0 || heldTotal > 0 || carried.length > 0 || hitTotal > 0;
  let fighter = TITLE[root][prefix];
  // the weapon itself colours a few
  if(root !== "mage"){
    if(prefix === "bow" && fam("crossbow") > fam("bow")) fighter = "Arbalist";
    else if(prefix === "bow" && (has("knife") || fam("spear") >= .25)) fighter = "Hunter";
    else if(prefix === "battle" && steel === "polearm") fighter = "Vanguard";
    else if(prefix === "shield" && steel === "axe" && (root === "knight" || root === "warrior")) fighter = "Huscarl";
    else if(prefix === "shield" && steel === "spear") fighter = "Hoplite";
    else if(prefix === "blade" && steel === "axe" && root !== "knight") fighter = "Raider";
  }
  if(prefix === "knife" && num(hits.backstab) >= 10 && num(hits.backstab)*2 >= hitTotal) fighter = "Assassin";
  // nothing seen in hand yet: dying more often than half the roster says something too
  const perDeath = q => num(q.deaths) ? num(q.dist_m)/num(q.deaths) : Infinity, pers = roster.map(perDeath).filter(isFinite).sort((a, b) => a - b), median = pers.length ? pers[Math.floor(pers.length/2)] : 0;
  if(!fighter && !hasGear && num(p.deaths) >= 20 && perDeath(p) <= median){ fighter = "Berserker"; armsWhy.push(`${num(p.deaths)} deaths, one every ${(perDeath(p)/1000).toFixed(1)} km`); }
  // 3. the trade, from the deeds: building against the roster's best, the rest in plain numbers
  const best = k => Math.max(1, ...roster.map(q => num(q[k])));
  const pieces = num(p.pieces), bestPieces = best("pieces"), trees = num(p.trees), rocks = num(p.rocks), taken = trees + rocks, roam = num(p.dist_m)/best("dist_m"), kills = num(p.kills), hops = num(p.hops);
  const tool = k => heldTotal ? num(hand[k])/heldTotal : 0, farm = tool("hoe") + tool("cultivator"), fish = tool("fishing"), sea = seaShare(p);
  const building = pieces >= 1000 || (pieces >= 300 && pieces/bestPieces >= .2), harvesting = taken >= 100 || (taken >= 10 && tool("pickaxe") + tool("axe") >= .25);
  const roaming = (roam >= .5 || num(p.ships) >= 3) && !(building && pieces/bestPieces >= .25);   // a builder who mostly roams is an explorer
  // the kitchen's trade, by what leads among what came out of the stations; over a builder only
  // when it is the larger share of the roster's best
  const K = kit(p), cooked = num(K.cooked), burnt = num(K.burnt), brewed = num(K.brewed), smelted = num(K.smelted);
  const baked = Object.entries(K.dishes || {}).reduce((t, [k, v]) => t + (OVEN.test(k) ? num(v) : 0), 0);
  const made = q => num(kit(q).cooked) + num(kit(q).brewed) + num(kit(q).smelted), bestMade = Math.max(1, ...roster.map(made));
  const craft = made(p) >= 50 ? [["Cook", cooked - baked], ["Baker", baked], ["Brewer", brewed], ["Smith", smelted]].sort((a, b) => b[1] - a[1])[0][0] : null;
  const trade = building && pieces >= bestPieces ? "Architect"
    : building && harvesting ? (trees > rocks ? "Carpenter" : "Engineer")
    : building && farm >= .2 ? "Homesteader"
    : craft && (!building || made(p)/bestMade > pieces/bestPieces) ? craft
    : kills >= 50 && kills >= .8*best("kills") ? "Slayer"
    : building && !roaming ? "Builder"
    : harvesting ? (rocks >= trees ? "Miner" : "Lumberjack")
    : farm >= .2 ? "Farmer"
    : roaming ? (sea >= .3 ? "Seafarer" : "Explorer")
    : fish >= .1 ? "Angler"
    : hops >= 200 && hops >= .8*best("hops") ? "Wayfarer" : null;
  const dishWhy = {Cook: `${cooked} dishes`, Baker: `${baked} from the oven`, Brewer: `${brewed} meads brewed`, Smith: `${smelted} bars smelted`}[trade];
  const deedsWhy = [dishWhy, dishWhy && burnt && (trade === "Cook" || trade === "Baker") && `${burnt} burnt`,
    building && `${pieces.toLocaleString()} pieces built`, trees >= 10 && `${trees} trees felled`, rocks >= 10 && `${rocks} rocks broken`,
    trade === "Slayer" && `${kills} kills`, (trade === "Homesteader" || trade === "Farmer") && `the hoe and cultivator ${Math.round(farm*100)}% of the time in hand`,
    (trade === "Seafarer" || trade === "Explorer") && `${Math.round(num(p.dist_m)/1000)} km travelled`, trade === "Seafarer" && `${Math.round(sea*100)}% of it at sea`,
    trade === "Angler" && `a fishing rod ${Math.round(fish*100)}% of the time in hand`, trade === "Wayfarer" && `${hops.toLocaleString()} portal hops`].filter(Boolean);
  const name = [fighter, trade].filter(Boolean).join(" ") || (hasGear ? "Wanderer" : "");
  if(!name) return null;
  // the reason names the three parts: prefix (weapons), root (body), trade (deeds)
  const part = (label, words) => words.length ? (label ? `${label}: ` : "") + words.join(", ") : null;
  const why = [part(fighter && prefix !== "none" ? prefix : null, armsWhy), part(fighter && root !== "none" ? root : null, bodyWhy), part(trade && trade.toLowerCase(), deedsWhy)].filter(Boolean);
  // faint until ten minutes have had something in hand or the hits have come in
  const early = !hasGear || (heldTotal < 600 && hitTotal < 10);
  return {name, early, why: why.join(" \u00b7 ") || "nothing to go on yet", top: heldTotal ? held.slice().sort((a, b) => num(b[1]) - num(a[1])).slice(0, 3).map(([k, v]) => ({fam: k, share: num(v)/heldTotal})) : []};
}
// the class as its pill, the reason under the pointer
const pill = c => c ? `<span class="cls${c.early ? " early" : ""}" title="${esc(c.why)}">${c.name}</span>` : "";
const mini = (v, k, cls) => `<span class="m${cls ? " " + cls : ""}"><span class="v">${v}</span><span class="k">${k}</span></span>`;
// the four figures people compare
function figures(p){
  const d = num(p.deaths), m = num(p.dist_m);
  return `<span class="mini">` + mini(d, "deaths", d ? "" : "nil") + mini(km(m), "km", m ? "" : "nil")
    + mini(per(d, m).replace("1 per ", ""), "per death", d && m ? "" : "nil")
    + mini(num(p.pieces), "pieces", num(p.pieces) ? "" : "nil") + `</span>`;
}
// ---------- the Viking ----------
// The player as the 3D view draws them, on a slow turntable on the map's ground colour
// under a key light. three.js and the rig load with the first look shown, one renderer
// for the page; a rig is built again only when the look's key changes.
const STILL = matchMedia("(prefers-reduced-motion: reduce)");
function viking(api){
  // lib: the library's index as fetched, {rev, p}; gen counts the fetches that moved a rig part, built the one the rig is from
  const VK = {el: document.createElement("figure"), ready: null, v: null, lib: null, gen: 0, built: -1, key: undefined, name: null, rig: null, missing: 0, want: null, raf: 0, w: 0, h: 0};
  VK.el.className = "card viking"; VK.el.hidden = true;
  VK.el.innerHTML = `<canvas aria-hidden="true"></canvas><figcaption></figcaption>`;
  function load(){
    if(!VK.ready) VK.ready = Promise.all([import("three"), import("three/addons/loaders/GLTFLoader.js"), import("./rig.js")]).then(([THREE, {GLTFLoader}, rig]) => {
      const renderer = new THREE.WebGLRenderer({canvas: VK.el.querySelector("canvas"), antialias: true, alpha: true});
      renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
      renderer.outputColorSpace = THREE.SRGBColorSpace;
      renderer.toneMapping = THREE.ACESFilmicToneMapping; renderer.toneMappingExposure = 1.05;
      const scene = new THREE.Scene(), camera = new THREE.PerspectiveCamera(30, 232/300, 0.1, 50);
      camera.position.set(0, 1.2, 5); camera.lookAt(0, 0.9, 0);    // room for a cape to swing past
      // a warm key high on the left, the sky's cool fill, a rim off the dark behind
      const key = new THREE.DirectionalLight(0xfff0dc, 2.8); key.position.set(-2.2, 3.4, 3); scene.add(key);
      const rim = new THREE.DirectionalLight(0xa8bcd8, 1.4); rim.position.set(1.6, 2.4, -3); scene.add(rim);
      scene.add(new THREE.HemisphereLight(0xd0d8e0, 0x3a3326, 1.7));
      // the spot they stand on, a soft pool of light
      const c = document.createElement("canvas"); c.width = c.height = 64;
      const x = c.getContext("2d"), grad = x.createRadialGradient(32, 32, 0, 32, 32, 32);
      grad.addColorStop(0, "rgba(201,161,90,.22)"); grad.addColorStop(1, "rgba(201,161,90,0)");
      x.fillStyle = grad; x.fillRect(0, 0, 64, 64);
      const glow = new THREE.CanvasTexture(c); glow.colorSpace = THREE.SRGBColorSpace;
      const pool = new THREE.Mesh(new THREE.CircleGeometry(0.75, 48), new THREE.MeshBasicMaterial({map: glow, transparent: true, depthWrite: false}));
      pool.rotation.x = -Math.PI/2; pool.position.y = 0.002; scene.add(pool);
      const turn = new THREE.Group(); scene.add(turn);
      VK.v = {rig, renderer, scene, camera, turn, builder: new rig.RigBuilder(THREE, new GLTFLoader(), {api, shadows: false, aniso: renderer.capabilities.getMaxAnisotropy()})};
      return VK.v;
    });
    return VK.ready;
  }
  // name's rig in look, captioned "online now" or "as last seen"; models: the library's revision, from /state
  VK.show = async (name, look, online, models) => {
    const el = VK.el, me = VK.want = {};
    if(!look || !(look.parts || []).length){ still(); return; }
    let v;
    try{ v = await load(); }catch(e){ still(); return; }   // no WebGL: no card
    if(VK.want !== me) return;
    const key = v.rig.lookKey(look);
    el.classList.toggle("ok", !!online);
    el.querySelector("figcaption").textContent = online ? "online now" : "as last seen";
    if(name !== VK.name) still();                  // never someone else's rig under this name
    // the index again only when the library moved and this look may have gained a part
    if(!VK.lib || (VK.lib.rev !== models && (key !== VK.key || VK.missing))){
      const lib = VK.lib = {rev: models};
      lib.p = MapCore.fetchJSON(api, `/prefabs?v=${models}`)
        .then(j => { if(v.builder.setLibrary(new Map(Object.entries(j.prefabs || {}).map(([h, e]) => [+h, e])))) VK.gen++; })
        .catch(() => { if(VK.lib === lib) VK.lib = null; });
    }
    await VK.lib.p;
    if(VK.want !== me) return;
    VK.name = name;
    if(key === VK.key && VK.built === VK.gen){ if(VK.rig) turning(); return; }
    const gen = VK.gen;
    let g = null;
    try{ g = await v.builder.build(look); }catch(e){ console.warn("rig", e.message || e); }
    if(VK.want !== me) return;
    if(VK.rig) v.turn.remove(VK.rig);
    VK.rig = g; VK.key = key; VK.built = gen; VK.missing = g ? g.userData.missing : 1;
    if(!g){ still(); return; }                     // the library has no body yet
    v.turn.add(g); turning();
  };
  // put away; a rig still being built is not shown
  VK.hide = () => { VK.want = null; still(); };
  // the card gone: its rig let go and what was baked for it freed; the renderer waits for the next
  VK.drop = () => {
    VK.hide();
    if(VK.rig) VK.v.turn.remove(VK.rig);
    if(VK.v) VK.v.builder.dispose();
    VK.rig = null; VK.key = undefined; VK.name = null;
  };
  function turning(){ VK.el.hidden = false; if(!VK.raf) VK.raf = requestAnimationFrame(spin); }
  function still(){ VK.el.hidden = true; cancelAnimationFrame(VK.raf); VK.raf = 0; }
  // a turn every 25 s; with reduced motion, one still from a little to the side
  function spin(t){
    VK.raf = 0;
    const v = VK.v, c = v.renderer.domElement, w = c.clientWidth, h = c.clientHeight;
    if(VK.el.hidden || !w || !h) return;
    if(w !== VK.w || h !== VK.h){ VK.w = w; VK.h = h; v.renderer.setSize(w, h, false); v.camera.aspect = w/h; v.camera.updateProjectionMatrix(); }
    v.turn.rotation.y = STILL.matches ? 0.5 : 0.5 + t*0.00025;
    v.renderer.render(v.scene, v.camera);
    if(!STILL.matches) VK.raf = requestAnimationFrame(spin);
  }
  addEventListener("resize", () => { if(!VK.el.hidden && !VK.raf) VK.raf = requestAnimationFrame(spin); });
  return VK;
}

return {num, km, per, seen, sea, kpd, BEST, rim, radar, classify, pill, figures, viking};
})();
