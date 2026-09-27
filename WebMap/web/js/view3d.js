// The 3D view: one spot of the world, stood in at eye height -- a mode of the map page.
//
// The model path -- prefab glTFs instanced per chunk, canopy billboards over the
// foliage the models leave out, the sky -- is ported from f00d4tehg0dz/valheim-webmap
// (MIT). The ground and the camera are ours: the ground is the server's /height grid
// per 256 m chunk, coloured by the biome chart and darkened where nobody has walked,
// and the camera is a street view -- stand, look around, walk -- with their orbit
// camera a button away.
//
// World -> scene: X = x, Y = height, Z = -z (Valheim's north is -Z here).

import * as THREE from 'three';
import { MapControls } from 'three/addons/controls/MapControls.js';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { Lighting } from './sky.js';

const CHUNK = 256, N = 257;              // a chunk's edge in metres; its height samples a side, both edges
const EYE = 1.8;                         // a Viking's eyes above the ground
const WALK = 5, RUN = 16;                // metres a second; Shift runs
const FOV = 62, FOV_MIN = 28, FOV_MAX = 78;

export class View3D {
  // api: where the server is ("" for the mod's own origin); phone: a small, weak screen
  constructor(canvas, { api = '', phone = false, water = 30 } = {}) {
    this.canvas = canvas;
    this.api = api;
    this.phone = phone;
    this.waterLevel = water;
    this.renderer = new THREE.WebGLRenderer({ canvas, antialias: !phone, powerPreference: 'high-performance' });
    this.renderer.setPixelRatio(Math.min(devicePixelRatio, phone ? 1.5 : 2));
    this.renderer.outputColorSpace = THREE.SRGBColorSpace;
    this.renderer.toneMapping = THREE.ACESFilmicToneMapping;
    this.renderer.toneMappingExposure = 1.05;
    this.scene = new THREE.Scene();
    this.camera = new THREE.PerspectiveCamera(FOV, 1, 0.3, 8000);
    this.maxAniso = this.renderer.capabilities.getMaxAnisotropy();

    // sky dome, sun, moon, fog colour and an environment map, all from the game's time of day
    this.lighting = new Lighting(this.scene, this.renderer);
    this.lighting.setShadows(!phone);      // shadows cost a phone most of its frame rate
    this.frac = 0.5;

    // the ground's colour comes from the server's own rasters, sampled in world space:
    // the biome chart and the explored mask, both 2048 px over the world at 12 m a pixel
    const pixel = (r, g, b) => { const t = new THREE.DataTexture(new Uint8Array([r, g, b, 255]), 1, 1); t.needsUpdate = true; return t; };
    this.ground = {
      uChart: { value: pixel(120, 130, 90) }, uFog: { value: pixel(255, 255, 255) }, uFogOn: { value: 0 },
      uWorld: { value: 1 / (2048 * 12) }, uFogShift: { value: 0.5 / 2048 }, uWater: { value: water },
      uDetail: { value: this.detailTexture() },
    };
    this.waterNormals = this.waterNormalTexture();
    const waterMat = this.groundMaterial({ color: 0x1b4668, transparent: true, opacity: 0.8, roughness: 0.12, metalness: 0.0,
      normalMap: this.waterNormals, normalScale: new THREE.Vector2(0.35, 0.35), envMapIntensity: 1.2 }, 'water');
    this.water = new THREE.Mesh(new THREE.PlaneGeometry(6000, 6000), waterMat);
    this.water.rotation.x = -Math.PI / 2;
    this.water.position.y = water;
    this.water.receiveShadow = true;
    this.scene.add(this.water);
    this.terrainMat = this.groundMaterial({ color: 0xffffff, roughness: 0.95, metalness: 0 }, 'ground');

    this.chunks = new Map();     // "cx,cz" -> {cx, cz, heights, mesh, step, rev, missing}
    this.objChunks = new Map();  // "cx,cz" -> {group, prefabs:Set, count, old: the entry it replaces}
    this.objData = new Map();    // "cx,cz" -> {rev, objs}, as fetched
    this.models = new Map();     // prefab hash -> Promise<parts[] | null>
    this.prefabs = new Map();    // prefab hash -> the library's entry
    this.cats = new Set(['piece', 'other', 'rock', 'bush', 'tree']);
    this.rev = {};               // the server's content revisions, from /state
    this.gltf = new GLTFLoader();
    this.loader = new THREE.TextureLoader();
    this.loader.setCrossOrigin('anonymous');
    this.matCache = new Map();
    this.box = new THREE.BoxGeometry(1, 1, 1);
    this.players = new Map();
    this.playerGroup = new THREE.Group();
    this.scene.add(this.playerGroup);
    this.raycaster = new THREE.Raycaster();

    // where you stand and where you look: heading in degrees clockwise from north, pitch up
    this.me = { x: 0, z: 0, y: water + EYE, heading: 0, pitch: 0 };
    this.mode = 'street';
    this.orbit = new MapControls(this.camera, canvas);
    this.orbit.enabled = false;
    this.orbit.enableDamping = true;
    this.orbit.dampingFactor = 0.1;
    this.orbit.maxPolarAngle = Math.PI * 0.49;
    this.orbit.minDistance = 8;
    this.orbit.maxDistance = 1400;
    this.orbit.screenSpacePanning = false;
    this.orbit.addEventListener('change', () => this.scheduleUpdate());
    this.keys = new Set();
    this.glide = null;
    this.running = false;
    this.onMove = null;          // (x, z, heading) after the view settles somewhere new
    this.onStatus = null;        // ({ground, objects, models}) as things load
    this.bindInput();
    addEventListener('resize', () => this.resize());
  }

  // ---------------------------------------------------------------- the server
  url(path) { return this.api + path; }

  async getBuffer(path) {
    const r = await fetch(this.url(path));
    if (r.status === 404) return null;            // nobody has walked there
    if (!r.ok) throw new Error(path + ' ' + r.status);
    return r.arrayBuffer();
  }

  // The revisions from /state: a layer is fetched again only when its own moved.
  setRevs(rev) {
    const old = this.rev;
    this.rev = Object.assign({}, rev);
    if (rev.chart && rev.chart !== old.chart) this.loadRaster('uChart', `/chart?v=${rev.chart}`, false);
    if (rev.fog !== old.fog) {
      this.loadRaster('uFog', `/fog?v=${rev.fog}`, true);
      // ground walked since: the chunks that were closed may be open now
      for (const [k, c] of this.chunks) if (c.missing) this.chunks.delete(k);
      for (const [k, o] of this.objChunks) if (o.missing) { this.objChunks.delete(k); this.objData.delete(k); }
    }
    if (old.height !== undefined && rev.height !== old.height) for (const c of this.chunks.values()) c.stale = true;
    if (old.objects !== undefined && rev.objects !== old.objects) for (const o of this.objChunks.values()) o.stale = o.stale || 'data';
    if (rev.models !== old.models) this.loadPrefabs(rev.models);
    this.scheduleUpdate();
  }

  loadRaster(uniform, path, linear) {
    this.loader.load(this.url(path), (t) => {
      t.colorSpace = linear ? THREE.NoColorSpace : THREE.SRGBColorSpace;
      t.minFilter = THREE.LinearFilter; t.magFilter = THREE.LinearFilter; t.generateMipmaps = false;
      t.wrapS = t.wrapT = THREE.ClampToEdgeWrapping;
      const old = this.ground[uniform].value;
      this.ground[uniform].value = t;
      if (uniform === 'uFog') this.ground.uFogOn.value = 1;
      if (old) old.dispose();
    });
  }

  // The model library's index. A prefab whose model is new or re-exported sends the
  // chunks that hold it back for a rebuild; the rest keep what they have.
  async loadPrefabs(v) {
    let d;
    try { d = await (await fetch(this.url(`/prefabs?v=${v}`))).json(); } catch (e) { return; }
    const next = new Map(Object.entries(d.prefabs || {}).map(([k, p]) => [+k, p]));
    const moved = new Set();
    for (const [h, p] of next) {
      const o = this.prefabs.get(h);
      if (!o || o.m !== p.m || o.v !== p.v || (o.k || []).join() !== (p.k || []).join() || o.kt !== p.kt) moved.add(h);
    }
    this.prefabs = next;
    this.library = { exported: d.exported, readable: d.readable, queued: d.queued, textures: d.texturesPresent, meshes: d.meshesPresent };
    for (const h of moved) this.models.delete(h);
    for (const o of this.objChunks.values()) if (o.prefabs && [...o.prefabs].some((h) => moved.has(h))) o.stale = 'look';
    this.scheduleUpdate();
  }

  setCategories(cats) {
    this.cats = new Set(cats);
    for (const o of this.objChunks.values()) o.stale = 'look';
    this.scheduleUpdate();
  }

  // ---------------------------------------------------------------- the ground
  // Height at a world spot from whichever chunk holds it, bilinear; null where none is loaded.
  heightAt(x, z) {
    const cx = Math.floor(x / CHUNK), cz = Math.floor(z / CHUNK);
    const c = this.chunks.get(cx + ',' + cz);
    if (!c || !c.heights) return null;
    const u = x - cx * CHUNK, v = z - cz * CHUNK;
    const i = Math.min(N - 2, Math.floor(u)), j = Math.min(N - 2, Math.floor(v));
    const tx = u - i, tz = v - j, h = c.heights;
    const a = h[j * N + i], b = h[j * N + i + 1], d = h[(j + 1) * N + i], e = h[(j + 1) * N + i + 1];
    return (a + (b - a) * tx) * (1 - tz) + (d + (e - d) * tx) * tz;
  }
  // the ground you stand on: land, or the water's surface over it
  standAt(x, z) { const h = this.heightAt(x, z); return h === null ? null : Math.max(h, this.waterLevel - 1.2); }

  async loadChunk(cx, cz, step) {
    const key = cx + ',' + cz;
    const entry = this.chunks.get(key) || { cx, cz };
    entry.loading = true; entry.failed = 0;
    this.chunks.set(key, entry);
    let buf;
    try { buf = await this.getBuffer(`/height?cx=${cx}&cz=${cz}&v=${this.rev.height || 0}`); }
    catch (e) { entry.loading = false; entry.failed = Date.now(); return; }
    if (this.chunks.get(key) !== entry) return;
    entry.loading = false; entry.stale = false;
    if (!buf) { entry.missing = true; this.status(); return; }
    // the revision is the world's: a terraform elsewhere moves it, and this chunk stands as it was
    const sum = checksum(buf);
    if (entry.mesh && entry.sum === sum) return;
    entry.sum = sum;
    const raw = new Int16Array(buf), heights = new Float32Array(N * N);
    for (let i = 0; i < heights.length; i++) heights[i] = raw[i] / 10;
    entry.heights = heights;
    this.buildTerrain(entry, step);
    // the neighbours' edge normals read this chunk's heights: rebuild theirs to match
    for (const [dx, dz] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
      const n = this.chunks.get((cx + dx) + ',' + (cz + dz));
      if (n && n.mesh) this.buildTerrain(n, n.step);
    }
    this.settle();
    this.status();
  }

  // A chunk's ground as one mesh, every step metres (1 near, coarser far or on a phone).
  buildTerrain(entry, step) {
    const n = Math.floor((N - 1) / step) + 1, h = entry.heights;
    const pos = new Float32Array(n * n * 3), nrm = new Float32Array(n * n * 3);
    const x0 = entry.cx * CHUNK, z0 = entry.cz * CHUNK;
    const at = (i, j) => {
      if (i >= 0 && j >= 0 && i < N && j < N) return h[j * N + i];
      const o = this.heightAt(x0 + i, z0 + j);
      return o === null ? h[Math.min(N - 1, Math.max(0, j)) * N + Math.min(N - 1, Math.max(0, i))] : o;
    };
    for (let j = 0, k = 0; j < n; j++)
      for (let i = 0; i < n; i++, k++) {
        const gi = i * step, gj = j * step, y = h[gj * N + gi];
        pos[k * 3] = gi; pos[k * 3 + 1] = y; pos[k * 3 + 2] = -gj;
        // normal from the height field (across the seam where a neighbour is loaded): (-dy/dx, 1, dy/dz)
        const fx = (at(gi + 1, gj) - at(gi - 1, gj)) / 2, fz = (at(gi, gj + 1) - at(gi, gj - 1)) / 2;
        const len = Math.hypot(fx, 1, fz);
        nrm[k * 3] = -fx / len; nrm[k * 3 + 1] = 1 / len; nrm[k * 3 + 2] = fz / len;
      }
    const idx = new Uint32Array((n - 1) * (n - 1) * 6);
    for (let j = 0, o = 0; j < n - 1; j++)
      for (let i = 0; i < n - 1; i++) {
        const a = j * n + i, b = a + 1, c = a + n, d = c + 1;   // c is a metre north of a
        idx[o++] = a; idx[o++] = b; idx[o++] = c;
        idx[o++] = b; idx[o++] = d; idx[o++] = c;
      }
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
    geo.setAttribute('normal', new THREE.BufferAttribute(nrm, 3));
    geo.setIndex(new THREE.BufferAttribute(idx, 1));
    geo.computeBoundingSphere();
    if (entry.mesh) { this.scene.remove(entry.mesh); entry.mesh.geometry.dispose(); }
    const mesh = new THREE.Mesh(geo, this.terrainMat);
    mesh.position.set(x0, 0, -z0);
    mesh.receiveShadow = true;
    mesh.userData.ground = true;
    entry.mesh = mesh; entry.step = step;
    this.scene.add(mesh);
  }

  dropChunk(key) {
    const c = this.chunks.get(key);
    this.chunks.delete(key);
    if (c && c.mesh) { this.scene.remove(c.mesh); c.mesh.geometry.dispose(); }
  }

  // A MeshStandardMaterial whose colour is the ground's: the biome chart, rock on the
  // steep, sand at the shore, fine grain up close, and the fog of war over it all.
  groundMaterial(params, kind) {
    const mat = new THREE.MeshStandardMaterial(params);
    const u = this.ground, ground = kind === 'ground';
    mat.onBeforeCompile = (shader) => {
      Object.assign(shader.uniforms, u);
      shader.vertexShader = shader.vertexShader
        .replace('#include <common>', '#include <common>\nvarying vec3 vGroundPos; varying vec3 vGroundNrm;')
        .replace('#include <worldpos_vertex>', '#include <worldpos_vertex>\nvGroundPos = (modelMatrix * vec4(transformed, 1.0)).xyz; vGroundNrm = normal;');
      shader.fragmentShader = shader.fragmentShader
        .replace('#include <common>', `#include <common>
varying vec3 vGroundPos; varying vec3 vGroundNrm;
uniform sampler2D uChart; uniform sampler2D uFog; uniform sampler2D uDetail;
uniform float uFogOn; uniform float uWorld; uniform float uFogShift; uniform float uWater;`)
        .replace('#include <map_fragment>', ground ? `#include <map_fragment>
{
  float wx = vGroundPos.x, wz = -vGroundPos.z;
  // the chart is the map's flat pastel: a little deeper and richer on the ground
  vec3 biome = texture2D(uChart, vec2(wx * uWorld + 0.5, wz * uWorld + 0.5)).rgb;
  float luma = dot(biome, vec3(0.3, 0.59, 0.11));
  biome = max(mix(vec3(luma), biome, 1.35), 0.0) * 0.78;
  float slope = 1.0 - normalize(vGroundNrm).y;
  biome = mix(biome, vec3(0.13, 0.12, 0.11), smoothstep(0.22, 0.42, slope));
  float above = vGroundPos.y - uWater;
  biome = mix(biome, vec3(0.20, 0.16, 0.10), 1.0 - smoothstep(0.3, 1.4, above));
  biome = mix(biome, vec3(0.10, 0.09, 0.06), 1.0 - smoothstep(-2.0, -0.3, above));
  vec2 dp = vec2(wx, wz);
  float grain = (texture2D(uDetail, dp / 24.0).r - 0.5) * 0.30 + (texture2D(uDetail, dp / 5.0).g - 0.5) * 0.18;
  diffuseColor.rgb *= biome * (1.0 + grain);
}` : '#include <map_fragment>')
        .replace('#include <dithering_fragment>', `#include <dithering_fragment>
if (uFogOn > 0.5) {
  float wx = vGroundPos.x, wz = -vGroundPos.z;
  float explored = texture2D(uFog, vec2(wx * uWorld + 0.5 + uFogShift, wz * uWorld + 0.5 + uFogShift)).r;
  gl_FragColor.rgb = mix(gl_FragColor.rgb, vec3(0.0), 0.8 * (1.0 - smoothstep(0.35, 0.65, explored)));
}`);
    };
    mat.customProgramCacheKey = () => 'ground-' + kind;
    return mat;
  }

  // Tileable grain for the ground: red = soft blotches, green = finer mottling. Built once.
  detailTexture() {
    const S = 256, c = document.createElement('canvas'); c.width = c.height = S;
    const ctx = c.getContext('2d'), img = ctx.createImageData(S, S), d = img.data;
    let seed = 7;
    const rnd = () => { seed = (seed * 16807) % 2147483647; return seed / 2147483647; };
    const lattice = (n) => { const l = new Float32Array(n * n); for (let i = 0; i < l.length; i++) l[i] = rnd(); return l; };
    const smooth = (lat, n, u, v) => {
      const x = u * n, y = v * n, x0 = Math.floor(x), y0 = Math.floor(y), fx = x - x0, fy = y - y0;
      const sx = fx * fx * (3 - 2 * fx), sy = fy * fy * (3 - 2 * fy);
      const a = lat[(y0 % n) * n + (x0 % n)], b = lat[(y0 % n) * n + ((x0 + 1) % n)], cc = lat[((y0 + 1) % n) * n + (x0 % n)], dd = lat[((y0 + 1) % n) * n + ((x0 + 1) % n)];
      return (a * (1 - sx) + b * sx) * (1 - sy) + (cc * (1 - sx) + dd * sx) * sy;
    };
    const l1 = lattice(16), l2 = lattice(64), l3 = lattice(32), l4 = lattice(128);
    for (let y = 0; y < S; y++)
      for (let x = 0; x < S; x++) {
        const u = x / S, v = y / S, o = (y * S + x) * 4;
        d[o] = Math.round((0.65 * smooth(l1, 16, u, v) + 0.35 * smooth(l2, 64, u, v)) * 255);
        d[o + 1] = Math.round((0.6 * smooth(l3, 32, u, v) + 0.4 * smooth(l4, 128, u, v)) * 255);
        d[o + 2] = 128; d[o + 3] = 255;
      }
    ctx.putImageData(img, 0, 0);
    const t = new THREE.CanvasTexture(c);
    t.wrapS = t.wrapT = THREE.RepeatWrapping; t.anisotropy = 4; t.minFilter = THREE.LinearMipmapLinearFilter;
    return t;
  }

  // Tileable normal map for the water: a few summed sine ripples.
  waterNormalTexture() {
    const S = 256, c = document.createElement('canvas'); c.width = c.height = S;
    const ctx = c.getContext('2d'), img = ctx.createImageData(S, S), d = img.data;
    const waves = [[3, 1, 1.0], [-2, 4, 0.7], [5, -3, 0.5], [1, 7, 0.35], [-6, -2, 0.3]];
    const h = (x, y) => { let v = 0; for (const [a, b, w] of waves) v += w * Math.sin(2 * Math.PI * (a * x + b * y) / S); return v; };
    for (let y = 0; y < S; y++)
      for (let x = 0; x < S; x++) {
        const dx = (h(x + 1, y) - h(x - 1, y)) * 0.5, dy = (h(x, y + 1) - h(x, y - 1)) * 0.5;
        const nx = -dx * 0.9, ny = -dy * 0.9, len = Math.hypot(nx, ny, 1), o = (y * S + x) * 4;
        d[o] = Math.round((nx / len * 0.5 + 0.5) * 255); d[o + 1] = Math.round((ny / len * 0.5 + 0.5) * 255); d[o + 2] = Math.round((1 / len * 0.5 + 0.5) * 255); d[o + 3] = 255;
      }
    ctx.putImageData(img, 0, 0);
    const t = new THREE.CanvasTexture(c);
    t.wrapS = t.wrapT = THREE.RepeatWrapping; t.repeat.set(6000 / 40, 6000 / 40); t.anisotropy = 4;
    return t;
  }

  // ---------------------------------------------------------------- world objects
  // Loads a prefab's glTF once; resolves to [{geometry, material}] or null when the library has no model.
  model(hash) {
    if (this.models.has(hash)) return this.models.get(hash);
    const info = this.prefabs.get(hash);
    if (!info || !info.m) { const p = Promise.resolve(null); this.models.set(hash, p); return p; }
    const file = (hash >>> 0).toString(16).padStart(8, '0');
    const p = this.gltf.loadAsync(this.url(`/models/${file}.glb?v=${info.v || 0}`)).then((g) => {
      const parts = [];
      g.scene.updateMatrixWorld(true);
      g.scene.traverse((o) => {
        if (!o.isMesh) return;
        const geo = o.geometry;
        if (!o.matrixWorld.equals(IDENTITY)) geo.applyMatrix4(o.matrixWorld);
        if (!geo.attributes.normal) geo.computeVertexNormals();
        const mats = Array.isArray(o.material) ? o.material : [o.material];
        for (const m of mats) { m.side = THREE.DoubleSide; if (m.map) m.map.anisotropy = Math.min(4, this.maxAniso); m.metalness = 0; m.roughness = Math.max(0.7, m.roughness); }
        parts.push({ geometry: geo, material: o.material });
      });
      return parts.length ? parts : null;
    }).catch((e) => { console.warn('model', file, e.message || e); return null; });
    this.models.set(hash, p);
    return p;
  }

  // A chunk's objects, fetched only when the server's revision moved: a new model or a
  // category switched rebuilds from what is already here.
  async loadObjects(cx, cz) {
    const key = cx + ',' + cz, rev = this.rev.objects || 0, prev = this.objChunks.get(key);
    const entry = { group: new THREE.Group(), prefabs: new Set(), loading: true, old: prev };
    this.objChunks.set(key, entry);
    let data = this.objData.get(key);
    if (!data || data.rev !== rev) {
      let buf;
      try { buf = await this.getBuffer(`/objects?cx=${cx}&cz=${cz}&v=${rev}`); }
      catch (e) { entry.loading = false; entry.failed = Date.now(); return; }
      const sum = buf ? checksum(buf) : 0;
      // the revision is the world's: someone building elsewhere moves it, and this chunk stands as it was
      if (data && data.sum === sum && prev && prev.stale === 'data' && this.objChunks.get(key) === entry) {
        data.rev = rev; prev.stale = false; this.objChunks.set(key, prev);
        return;
      }
      data = { rev, sum, objs: buf ? decodeObjects(buf) : null };
      this.objData.set(key, data);
    }
    if (this.objChunks.get(key) !== entry) return;
    if (!data.objs) { entry.loading = false; entry.missing = true; this.disposeObjects(entry, true); return; }
    const objs = data.objs;
    entry.count = objs.length;
    const byPrefab = new Map();
    for (const o of objs) {
      entry.prefabs.add(o.prefab);
      const info = this.prefabs.get(o.prefab);
      if (!info || !this.cats.has(info.c)) continue;
      if (!byPrefab.has(o.prefab)) byPrefab.set(o.prefab, []);
      byPrefab.get(o.prefab).push(o);
    }
    const q = new THREE.Quaternion(), pos = new THREE.Vector3(), scl = new THREE.Vector3(), mtx = new THREE.Matrix4(), off = new THREE.Matrix4(), out = new THREE.Matrix4();
    const place = (o) => { pos.set(o.x, o.y, -o.z); q.set(-o.qx, -o.qy, o.qz, o.qw); scl.set(o.sx, o.sy, o.sz); return mtx.compose(pos, q, scl); };
    const group = entry.group;
    this.scene.add(group);
    // every prefab's model loads in parallel; the old chunk stays up until the new one has its models
    await Promise.all([...byPrefab].map(async ([hash, list]) => {
      const info = this.prefabs.get(hash);
      const parts = await this.model(hash);
      if (this.objChunks.get(key) !== entry) return;
      const cat = info ? info.c : 'other';
      const shadows = !this.phone;
      if (parts) {
        for (const [p, part] of parts.entries()) {
          // a part the mining took off stands on none of the rocks it is gone from
          const on = info && info.pa ? list.filter((o) => partShows(info, p, o)) : list;
          if (!on.length) continue;
          const im = new THREE.InstancedMesh(part.geometry, part.material, on.length);
          im.castShadow = shadows; im.receiveShadow = true;
          on.forEach((o, i) => im.setMatrixAt(i, place(o)));
          im.instanceMatrix.needsUpdate = true;
          im.computeBoundingSphere();
          group.add(im);
        }
      } else if (info && info.b && info.u && (cat === 'piece' || cat === 'other')) {
        // a model still waiting on its locked meshes: a box of its size and colour meanwhile
        const b = info.b;
        off.compose(new THREE.Vector3((b[0] + b[3]) / 2, (b[1] + b[4]) / 2, (b[2] + b[5]) / 2), new THREE.Quaternion(),
          new THREE.Vector3(Math.max(0.1, b[3] - b[0]), Math.max(0.1, b[4] - b[1]), Math.max(0.1, b[5] - b[2])));
        const im = new THREE.InstancedMesh(this.box, this.plain(fallbackColor(info)), list.length);
        im.castShadow = shadows; im.receiveShadow = true;
        list.forEach((o, i) => { out.multiplyMatrices(place(o), off); im.setMatrixAt(i, out); });
        im.instanceMatrix.needsUpdate = true;
        im.computeBoundingSphere();
        group.add(im);
      }
      // canopy: billboard leaves over the foliage bounds (the model carries only trunk and branches)
      if (info && info.k && info.k.length === 6 && info.k[3] > info.k[0]) {
        // trees carry low branches in their bounds, so the crown starts a third of the way up and is a
        // little narrower than the outermost leaf; bushes keep their full bounds
        const k = info.k, tree = cat === 'tree';
        const h = k[4] - k[1], y0 = tree ? k[1] + h * 0.3 : k[1], shrink = tree ? 0.8 : 1;
        const b = [k[0] * shrink, y0, k[2] * shrink, k[3] * shrink, k[4], k[5] * shrink];
        off.compose(new THREE.Vector3((b[0] + b[3]) / 2, (b[1] + b[4]) / 2, (b[2] + b[5]) / 2), new THREE.Quaternion(),
          new THREE.Vector3(Math.max(0.3, b[3] - b[0]), Math.max(0.3, b[4] - b[1]), Math.max(0.3, b[5] - b[2])));
        const im = new THREE.InstancedMesh(this.canopyGeometry(), this.canopyMaterial(info), list.length);
        im.castShadow = shadows; im.receiveShadow = true;
        list.forEach((o, i) => { out.multiplyMatrices(place(o), off); im.setMatrixAt(i, out); });
        im.instanceMatrix.needsUpdate = true;
        im.computeBoundingSphere();
        group.add(im);
      }
    }));
    entry.loading = false; entry.stale = false;
    this.disposeObjects(entry, true);
    this.status();
  }

  // an entry's group, and every one it stood in for (oldOnly: keep the entry's own)
  disposeObjects(entry, oldOnly) {
    for (let e = oldOnly ? entry.old : entry; e; e = e.old) {
      this.scene.remove(e.group);
      e.group.traverse((o) => { if (o.isInstancedMesh) o.dispose(); });
    }
    entry.old = null;
  }
  dropObjects(key) {
    const o = this.objChunks.get(key);
    this.objChunks.delete(key);
    this.objData.delete(key);
    if (o) this.disposeObjects(o);
  }

  // A unit canopy: three vertical quads 60 degrees apart, textured with the tree's own leaf
  // texture (tiled) or a soft procedural leaf mask.
  canopyGeometry() {
    if (this._canopyGeo) return this._canopyGeo;
    const pos = [], nrm = [], uv = [], idx = [];
    const quad = (fn, n) => {
      const s = pos.length / 3;
      for (const [u, v] of [[0, 0], [1, 0], [1, 1], [0, 1]]) { const p = fn(u - 0.5, v - 0.5); pos.push(p[0], p[1], p[2]); nrm.push(n[0], n[1], n[2]); uv.push(u, v); }
      idx.push(s, s + 1, s + 2, s, s + 2, s + 3);
    };
    for (let k = 0; k < 3; k++) {
      const a = k * Math.PI / 3, c = Math.cos(a), sn = Math.sin(a);
      quad((u, v) => [u * c, v, u * sn], [-sn, 0, c]);
    }
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
    g.setAttribute('normal', new THREE.Float32BufferAttribute(nrm, 3));
    g.setAttribute('uv', new THREE.Float32BufferAttribute(uv, 2));
    g.setIndex(idx);
    this._canopyGeo = g;
    return g;
  }

  canopyMaterial(info) {
    const key = 'canopy:' + (info.kt || '') + ':' + (info.kc || []).join(',');
    if (this.matCache.has(key)) return this.matCache.get(key);
    const tint = info.kc && info.kc.length === 3 ? new THREE.Color(info.kc[0], info.kc[1], info.kc[2]) : new THREE.Color(0.35, 0.55, 0.25);
    const mat = new THREE.MeshStandardMaterial({ color: info.kt ? 0xffffff : tint, roughness: 0.95, metalness: 0, side: THREE.DoubleSide, alphaTest: 0.5 });
    mat.map = this.leafMask();   // stand-in until the real leaf texture arrives (or for good)
    mat.alphaMap = this.crownMask();
    if (info.kt) {
      this.loader.load(this.url(`/models/${info.kt}`), (t) => {
        t.colorSpace = THREE.SRGBColorSpace; t.wrapS = t.wrapT = THREE.RepeatWrapping; t.repeat.set(3, 3); t.anisotropy = Math.min(4, this.maxAniso);
        mat.map = t; mat.needsUpdate = true;
      });
    }
    this.matCache.set(key, mat);
    return mat;
  }

  leafMask() {
    if (this._leafMask) return this._leafMask;
    const S = 128, c = document.createElement('canvas'); c.width = c.height = S;
    const ctx = c.getContext('2d');
    let seed = 11;
    const rnd = () => { seed = (seed * 16807) % 2147483647; return seed / 2147483647; };
    for (let i = 0; i < 70; i++) {
      const x = rnd() * S, y = rnd() * S, rad = 5 + rnd() * 9, g = 150 + rnd() * 105;
      ctx.fillStyle = `rgb(${g},${g},${g})`;
      for (const dx of [-S, 0, S]) for (const dy of [-S, 0, S]) { ctx.beginPath(); ctx.ellipse(x + dx, y + dy, rad, rad * 0.65, rnd() * 3, 0, Math.PI * 2); ctx.fill(); }
    }
    const t = new THREE.CanvasTexture(c);
    t.colorSpace = THREE.SRGBColorSpace; t.wrapS = t.wrapT = THREE.RepeatWrapping; t.repeat.set(3, 3);
    return (this._leafMask = t);
  }

  // Crown silhouette (alphaMap): an ellipse with a ragged, fading rim.
  crownMask() {
    if (this._crownMask) return this._crownMask;
    const S = 256, c = document.createElement('canvas'); c.width = c.height = S;
    const ctx = c.getContext('2d');
    ctx.fillStyle = '#000'; ctx.fillRect(0, 0, S, S);
    const g = ctx.createRadialGradient(S / 2, S / 2, 0, S / 2, S / 2, S / 2);
    g.addColorStop(0, '#fff'); g.addColorStop(0.72, '#fff'); g.addColorStop(1, '#000');
    ctx.fillStyle = g; ctx.beginPath(); ctx.ellipse(S / 2, S / 2, S / 2, S / 2, 0, 0, Math.PI * 2); ctx.fill();
    let seed = 5;
    const rnd = () => { seed = (seed * 16807) % 2147483647; return seed / 2147483647; };
    ctx.fillStyle = '#000';
    for (let i = 0; i < 40; i++) {
      const a = rnd() * Math.PI * 2, r = S * (0.42 + rnd() * 0.1);
      ctx.beginPath(); ctx.arc(S / 2 + Math.cos(a) * r, S / 2 + Math.sin(a) * r, 8 + rnd() * 14, 0, Math.PI * 2); ctx.fill();
    }
    const t = new THREE.CanvasTexture(c);
    t.wrapS = t.wrapT = THREE.ClampToEdgeWrapping;
    return (this._crownMask = t);
  }

  plain(hex) {
    if (!this.matCache.has(hex)) this.matCache.set(hex, new THREE.MeshStandardMaterial({ color: new THREE.Color(hex), roughness: 0.9, metalness: 0.02 }));
    return this.matCache.get(hex);
  }

  // ---------------------------------------------------------------- players
  setPlayers(list) {
    const seen = new Set();
    for (const p of list || []) {
      if (p.x === undefined || p.z === undefined) continue;
      seen.add(p.name);
      let e = this.players.get(p.name);
      if (!e) {
        const body = new THREE.Mesh(new THREE.CapsuleGeometry(0.4, 1.0, 4, 8), this.plain('#6fb7ff'));
        body.castShadow = true; body.position.y = 0.9;
        const label = makeLabel(p.name, '#6fd8e6');
        label.position.y = 2.5;
        const g = new THREE.Group(); g.add(body); g.add(label);
        e = { group: g, label };
        this.players.set(p.name, e);
        this.playerGroup.add(g);
      }
      e.x = p.x; e.z = p.z;
      e.group.position.set(p.x, this.standAt(p.x, p.z) ?? this.waterLevel, -p.z);
    }
    for (const [id, e] of this.players) if (!seen.has(id)) { this.playerGroup.remove(e.group); this.players.delete(id); }
  }

  // ---------------------------------------------------------------- the camera
  // Stand at (x, z) facing heading (degrees clockwise from north).
  goTo(x, z, heading) {
    this.me.x = x; this.me.z = z;
    if (heading !== undefined) this.me.heading = heading;
    this.me.pitch = 0;
    this.glide = null;
    if (this.mode === 'orbit') this.aimOrbit();
    this.update();
    this.settle();
  }

  setMode(mode) {
    if (mode === this.mode) return;
    if (mode === 'orbit') {
      this.mode = 'orbit';
      this.aimOrbit();
      this.orbit.enabled = true;
    } else {
      // stand where the overview was looking, facing the way it faced
      const t = this.orbit.target, d = t.clone().sub(this.camera.position);
      this.me.x = t.x; this.me.z = -t.z;
      this.me.heading = (Math.atan2(d.x, -d.z) * 180 / Math.PI + 360) % 360;
      this.me.pitch = 0;
      this.orbit.enabled = false;
      this.mode = 'street';
    }
    this.camera.fov = FOV; this.camera.updateProjectionMatrix();
    this.applyFog();
    this.update();
  }

  // The overview: from behind and above where you stood, looking at it.
  aimOrbit() {
    const y = this.standAt(this.me.x, this.me.z) ?? this.waterLevel, h = this.me.heading * Math.PI / 180, d = 140;
    this.orbit.target.set(this.me.x, y, -this.me.z);
    this.camera.position.set(this.me.x - Math.sin(h) * d * 0.7, y + d * 0.7, -this.me.z + Math.cos(h) * d * 0.7);
    this.lift = 0;                   // metres the circled point rides above the ground (E and Q)
    this.orbit.update();
  }

  applyFog() {
    const f = this.scene.fog;
    if (this.mode === 'street') { f.near = 160; f.far = 520; this.camera.far = 3000; }
    else { f.near = 700; f.far = 2600; this.camera.far = 8000; }
    this.camera.updateProjectionMatrix();
  }

  bindInput() {
    const cv = this.canvas, pts = new Map();
    let down = null, pinch = null;
    cv.addEventListener('pointerdown', (e) => {
      pts.set(e.pointerId, { x: e.clientX, y: e.clientY });
      if (pts.size === 1) down = { x: e.clientX, y: e.clientY, moved: false };
      if (pts.size === 2) { const [a, b] = [...pts.values()]; pinch = { d: Math.hypot(a.x - b.x, a.y - b.y), fov: this.camera.fov }; }
      if (this.mode === 'street') cv.setPointerCapture(e.pointerId);
    });
    cv.addEventListener('pointermove', (e) => {
      const p = pts.get(e.pointerId);
      if (!p) return;
      const dx = e.clientX - p.x, dy = e.clientY - p.y;
      p.x = e.clientX; p.y = e.clientY;
      if (down && Math.hypot(e.clientX - down.x, e.clientY - down.y) > 5) down.moved = true;
      if (this.mode !== 'street') return;
      if (pts.size === 2 && pinch) {
        const [a, b] = [...pts.values()];
        this.setFov(pinch.fov * pinch.d / Math.max(10, Math.hypot(a.x - b.x, a.y - b.y)));
        return;
      }
      // drag the world: the view turns the other way, degrees per pixel scaled by the zoom
      const k = this.camera.fov / (this.canvas.clientHeight || 600);
      this.me.heading = (this.me.heading - dx * k + 360) % 360;
      this.me.pitch = Math.max(-85, Math.min(85, this.me.pitch + dy * k));
      this.glide = null;
    });
    const up = (e) => {
      pts.delete(e.pointerId);
      if (pts.size < 2) pinch = null;
      if (e.type === 'pointerup' && down && !down.moved && pts.size === 0) this.click(e.clientX, e.clientY);
      if (pts.size === 0) { down = null; this.settle(); }
    };
    cv.addEventListener('pointerup', up);
    cv.addEventListener('pointercancel', up);
    cv.addEventListener('wheel', (e) => {
      if (this.mode !== 'street') return;
      e.preventDefault();
      this.setFov(this.camera.fov * Math.exp(e.deltaY * 0.0012));
    }, { passive: false });
    // keys: WASD or the arrows walk, Q E turn, Shift runs
    addEventListener('keydown', (e) => {
      if (!this.running || /INPUT|SELECT|TEXTAREA/.test(e.target.tagName) || e.metaKey || e.ctrlKey || e.altKey) return;
      const k = e.key.length === 1 ? e.key.toLowerCase() : e.key;
      if (KEYS.has(k)) { this.keys.add(k); e.preventDefault(); }
    });
    addEventListener('keyup', (e) => { this.keys.delete(e.key.length === 1 ? e.key.toLowerCase() : e.key); if (!this.keys.size) this.settle(); });
    addEventListener('blur', () => this.keys.clear());
  }

  setFov(f) { this.camera.fov = Math.max(FOV_MIN, Math.min(FOV_MAX, f)); this.camera.updateProjectionMatrix(); }

  // A click on the ground walks there; on a wall or a tree, to just this side of it.
  click(cx, cy) {
    if (this.mode !== 'street') return;
    const r = this.canvas.getBoundingClientRect();
    const ndc = new THREE.Vector2(((cx - r.left) / r.width) * 2 - 1, -((cy - r.top) / r.height) * 2 + 1);
    this.raycaster.setFromCamera(ndc, this.camera);
    this.raycaster.far = 600;
    const targets = [...this.chunks.values()].filter((c) => c.mesh).map((c) => c.mesh);
    for (const o of this.objChunks.values()) targets.push(o.group);
    const hit = this.raycaster.intersectObjects(targets, true)[0];
    if (!hit) return;
    const p = hit.point.clone();
    if (!hit.object.userData.ground) p.addScaledVector(this.raycaster.ray.direction, -1.5);
    this.glide = { x: p.x, z: -p.z, t0: performance.now(), x0: this.me.x, z0: this.me.z };
  }

  // The overview flies on the same keys: W A S D or the arrows carry the point it circles,
  // and the camera with it, over the ground the way the camera faces; E or Space raise it,
  // Q or Shift on its own lower it; Shift with any other key goes three times as fast.
  orbitKeys(dt) {
    const K = this.keys, t = this.orbit.target, cam = this.camera.position;
    const off = _v2.copy(cam).sub(t), dist = off.length();
    const moving = ['w', 's', 'a', 'd', 'e', ' ', 'q', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'].some((k) => K.has(k));
    const speed = Math.max(25, dist) * 0.8 * (K.has('Shift') && moving ? 3 : 1) * dt;
    let fx = -off.x, fz = -off.z;
    const l = Math.hypot(fx, fz) || 1; fx /= l; fz /= l;            // forward on the ground; right is (-fz, fx)
    let f = 0, s = 0, up = 0;
    if (K.has('w') || K.has('ArrowUp')) f += 1;
    if (K.has('s') || K.has('ArrowDown')) f -= 1;
    if (K.has('d') || K.has('ArrowRight')) s += 1;
    if (K.has('a') || K.has('ArrowLeft')) s -= 1;
    if (K.has('e') || K.has(' ')) up += 1;
    if (K.has('q') || (K.has('Shift') && !moving)) up -= 1;
    if (f || s) {
      const n = Math.hypot(f, s), dx = (fx * f - fz * s) / n * speed, dz = (fz * f + fx * s) / n * speed;
      t.x += dx; t.z += dz; cam.x += dx; cam.z += dz;
    }
    if (up) this.lift = Math.min(800, Math.max(0, this.lift + up * speed * 0.6));
    if (f || s || up) this.scheduleUpdate();
  }

  handleKeys(dt) {
    const K = this.keys;
    if (K.size === 0) return;
    if (this.mode === 'orbit') return this.orbitKeys(dt);
    const speed = (K.has('Shift') ? RUN : WALK) * dt, h = this.me.heading * Math.PI / 180;
    let f = 0, s = 0;
    if (K.has('w') || K.has('ArrowUp')) f += 1;
    if (K.has('s') || K.has('ArrowDown')) f -= 1;
    if (K.has('d') || K.has('ArrowRight')) s += 1;
    if (K.has('a') || K.has('ArrowLeft')) s -= 1;
    if (K.has('q')) this.me.heading = (this.me.heading - 90 * dt + 360) % 360;
    if (K.has('e')) this.me.heading = (this.me.heading + 90 * dt) % 360;
    if (f || s) {
      const len = Math.hypot(f, s);
      this.me.x += (Math.sin(h) * f + Math.cos(h) * s) / len * speed;
      this.me.z += (Math.cos(h) * f - Math.sin(h) * s) / len * speed;
      this.glide = null;
      this.scheduleUpdate();
    }
  }

  placeCamera(dt) {
    if (this.glide) {
      const g = this.glide, t = Math.min(1, (performance.now() - g.t0) / Math.max(300, Math.hypot(g.x - g.x0, g.z - g.z0) * 25));
      const e = t * t * (3 - 2 * t);
      this.me.x = g.x0 + (g.x - g.x0) * e; this.me.z = g.z0 + (g.z - g.z0) * e;
      if (t >= 1) { this.glide = null; this.settle(); }
      this.scheduleUpdate();
    }
    const ground = this.standAt(this.me.x, this.me.z);
    if (ground !== null) {
      const want = ground + EYE;
      // steps and slopes ease rather than jolt; a fresh arrival snaps
      this.me.y = Math.abs(want - this.me.y) > 30 ? want : this.me.y + (want - this.me.y) * Math.min(1, dt * 12);
    }
    const h = this.me.heading * Math.PI / 180, p = this.me.pitch * Math.PI / 180;
    this.camera.position.set(this.me.x, this.me.y, -this.me.z);
    this.camera.lookAt(this.me.x + Math.sin(h) * Math.cos(p), this.me.y + Math.sin(p), -this.me.z - Math.cos(h) * Math.cos(p));
  }

  // where the view is centred: you, or what the overview looks at
  focus() {
    if (this.mode === 'orbit') return { x: this.orbit.target.x, z: -this.orbit.target.z };
    return { x: this.me.x, z: this.me.z };
  }

  // the view came to rest: say where, so the page can keep its link current
  settle() {
    clearTimeout(this.settleTimer);
    this.settleTimer = setTimeout(() => {
      const f = this.focus();
      if (this.onMove) this.onMove(f.x, f.z, this.mode === 'street' ? this.me.heading : null);
    }, 250);
  }

  // ---------------------------------------------------------------- lifecycle
  // The map page shows and hides the view: stop() leaves everything loaded, so going
  // back into 3D is instant, and draws nothing while the map is up.
  start() {
    this.resize();
    this.applyFog();
    this.running = true;
    this.lastFrame = 0;
    this.update();
    if (!this.frame) this.loop();
  }

  stop() {
    this.running = false;
    this.keys.clear();
    if (this.frame) { cancelAnimationFrame(this.frame); this.frame = 0; }
  }

  resize() {
    if (!this.canvas.clientWidth) return;       // hidden: sized again on the way back in
    const w = this.canvas.clientWidth, h = this.canvas.clientHeight;
    this.renderer.setSize(w, h, false);
    this.camera.aspect = w / h;
    this.camera.updateProjectionMatrix();
  }

  setTime(frac) {
    if (Math.abs(frac - this.frac) > 0.002 || this.lighting.frac === undefined) { this.frac = frac; this.lighting.setTime(frac); }
  }
  setShadows(on) {
    this.lighting.setShadows(on);
    for (const o of this.objChunks.values()) o.group.traverse((m) => { if (m.isInstancedMesh) m.castShadow = on; });
  }

  loop() {
    if (!this.running) { this.frame = 0; return; }
    this.frame = requestAnimationFrame(() => this.loop());
    const now = performance.now(), dt = Math.min(0.1, (now - (this.lastFrame || now)) / 1000);
    this.lastFrame = now;
    if (this.mode === 'street') { this.handleKeys(dt); this.placeCamera(dt); }
    else {
      this.handleKeys(dt);
      this.orbit.update();
      // the circled point follows the ground, lifted by what E and Q have made of it
      const t = this.orbit.target, h = this.heightAt(t.x, -t.z);
      if (h !== null) {
        const want = Math.max(h, this.waterLevel) + (this.lift || 0);
        if (Math.abs(want - t.y) > 0.05) { const d = (want - t.y) * Math.min(1, dt * 10); t.y += d; this.camera.position.y += d; }
      }
    }
    for (const p of this.players.values()) p.label.quaternion.copy(this.camera.quaternion);
    // the shadow box stands a little ahead of you, where the eye is
    const target = this.mode === 'street'
      ? _ahead.set(this.me.x + Math.sin(this.me.heading * Math.PI / 180) * 40, this.me.y, -this.me.z - Math.cos(this.me.heading * Math.PI / 180) * 40)
      : this.orbit.target;
    this.lighting.update(this.camera, target);
    this.water.position.x = this.camera.position.x; this.water.position.z = this.camera.position.z;
    const t = now / 1000;
    this.waterNormals.offset.set((t * 0.012 + this.camera.position.x / 40) % 1, (t * 0.009 - this.camera.position.z / 40) % 1);
    this.renderer.render(this.scene, this.camera);
  }

  scheduleUpdate() {
    if (this.updateTimer) return;
    this.updateTimer = setTimeout(() => { this.updateTimer = null; this.update(); }, 150);
  }

  // The chunks around the focus: the one you are in and its eight neighbours; the
  // overview, pulled far out, a ring more of ground (coarser) without objects.
  update() {
    if (!this.running) return;
    const f = this.focus(), cx = Math.floor(f.x / CHUNK), cz = Math.floor(f.z / CHUNK);
    const far = this.mode === 'orbit' && this.camera.position.distanceTo(this.orbit.target) > 260;
    const ring = far ? 2 : 1, now = Date.now();
    const wantG = new Map(), wantO = new Set();
    for (let dz = -ring; dz <= ring; dz++)
      for (let dx = -ring; dx <= ring; dx++) {
        const outer = Math.max(Math.abs(dx), Math.abs(dz)) > 1;
        wantG.set((cx + dx) + ',' + (cz + dz), { cx: cx + dx, cz: cz + dz, step: outer ? 4 : this.phone ? 2 : 1 });
        if (!outer) wantO.add((cx + dx) + ',' + (cz + dz));
      }
    for (const [key, w] of wantG) {
      const c = this.chunks.get(key);
      if (!c || c.stale || (c.failed && now - c.failed > 5000)) { if (!c || !c.loading) this.loadChunk(w.cx, w.cz, w.step); }
      else if (c.mesh && c.step !== w.step) this.buildTerrain(c, w.step);
    }
    for (const key of [...this.chunks.keys()]) if (!wantG.has(key)) this.dropChunk(key);
    for (const key of wantO) {
      const o = this.objChunks.get(key);
      const [ox, oz] = key.split(',').map(Number);
      if (!o || (o.stale && !o.loading) || (o.failed && now - o.failed > 5000)) this.loadObjects(ox, oz);
    }
    for (const key of [...this.objChunks.keys()]) if (!wantO.has(key)) this.dropObjects(key);
    this.status();
  }

  status() {
    if (!this.onStatus) return;
    let ground = 0, walked = 0, objects = 0;
    for (const c of this.chunks.values()) { if (c.mesh) ground++; if (!c.missing) walked++; }
    for (const o of this.objChunks.values()) objects += o.count || 0;
    const here = this.chunks.get(Math.floor(this.focus().x / CHUNK) + ',' + Math.floor(this.focus().z / CHUNK));
    this.onStatus({ ground, chunks: this.chunks.size, objects, unwalked: !!(here && here.missing), library: this.library });
  }
}

const KEYS = new Set(['w', 'a', 's', 'd', 'q', 'e', ' ', 'Shift', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight']);
const IDENTITY = new THREE.Matrix4();
const _ahead = new THREE.Vector3(), _v2 = new THREE.Vector3();

// OBJ1, little-endian: 'OBJ1', u32 count, u32 prefabs, i32[prefabs] hashes, then per object
// u16 prefab index, u8 flags (1 = player-built), u8 pad, f32 x y z, qx qy qz qw, sx sy sz.
// OBJ2 then carries the mined rocks: u32 count, and per rock u32 object index, u16 bits and
// bits/8 bytes, bit i set when hit area i is broken off. OBJ1, from an older mod, has no table.
function decodeObjects(buf) {
  const dv = new DataView(buf), objs = [];
  const magic = buf.byteLength >= 12 ? String.fromCharCode(dv.getUint8(0), dv.getUint8(1), dv.getUint8(2), dv.getUint8(3)) : '';
  if (magic !== 'OBJ1' && magic !== 'OBJ2') return objs;
  const n = dv.getUint32(4, true), np = dv.getUint32(8, true), table = new Int32Array(np);
  let o = 12;
  for (let i = 0; i < np; i++) { table[i] = dv.getInt32(o, true); o += 4; }
  for (let i = 0; i < n; i++) {
    const pi = dv.getUint16(o, true), flags = dv.getUint8(o + 2); o += 4;
    const f = (k) => dv.getFloat32(o + k * 4, true);
    objs.push({ prefab: table[pi], creator: (flags & 1) !== 0, x: f(0), y: f(1), z: f(2), qx: f(3), qy: f(4), qz: f(5), qw: f(6), sx: f(7), sy: f(8), sz: f(9) });
    o += 40;
  }
  if (magic === 'OBJ2' && o + 4 <= buf.byteLength) {
    const mined = dv.getUint32(o, true); o += 4;
    for (let k = 0; k < mined && o + 6 <= buf.byteLength; k++) {
      const i = dv.getUint32(o, true), bits = dv.getUint16(o + 4, true); o += 6;
      if (objs[i]) objs[i].gone = new Uint8Array(buf.slice(o, o + bits / 8));
      o += bits / 8;
    }
  }
  return objs;
}

// Does this part of the model stand on this object? A mined rock's broken-off areas do not;
// MineRock (rk 1) with a whole-rock model (area -2) shows that until the first piece falls, and its
// pieces only after.
function partShows(info, part, o) {
  const a = info && info.pa ? info.pa[part] ?? -1 : -1;
  if (a === -1) return true;
  const gone = o.gone;
  if (a === -2) return !gone;
  if (info.rk === 1 && !gone && (info.whole ??= info.pa.includes(-2))) return false;
  return !(gone && (gone[a >> 3] >> (a & 7)) & 1);
}

// FNV-1a over the bytes, a word at a time: is this the chunk we already have?
function checksum(buf) {
  const w = new Uint32Array(buf, 0, buf.byteLength >> 2), tail = new Uint8Array(buf, w.length * 4);
  let h = 2166136261 ^ buf.byteLength;
  for (let i = 0; i < w.length; i++) h = Math.imul(h ^ w[i], 16777619);
  for (let i = 0; i < tail.length; i++) h = Math.imul(h ^ tail[i], 16777619);
  return h >>> 0;
}

// Colour for a prefab still without its mesh, from what the library says it is.
function fallbackColor(info) {
  const n = (info.n || '').toLowerCase();
  if (n.includes('portal')) return '#5ac8d2';
  if (n.includes('stone') || n.includes('grausten')) return '#9a9892';
  if (n.includes('iron') || n.includes('metal')) return '#767e8c';
  if (n.includes('darkwood')) return '#5c422e';
  if (n.includes('roof') || n.includes('thatch')) return '#c6a258';
  return info.c === 'piece' ? '#a07446' : '#9a8c78';
}

function makeLabel(text, color) {
  const c = document.createElement('canvas'), ctx = c.getContext('2d');
  ctx.font = '600 28px system-ui, sans-serif';
  const w = Math.ceil(ctx.measureText(text).width) + 36;
  c.width = w; c.height = 48;
  ctx.font = '600 28px system-ui, sans-serif';
  ctx.fillStyle = 'rgba(10,12,18,.7)';
  ctx.beginPath(); ctx.roundRect(0, 0, w, 48, 12); ctx.fill();
  ctx.fillStyle = color; ctx.beginPath(); ctx.arc(18, 24, 8, 0, Math.PI * 2); ctx.fill();
  ctx.fillStyle = '#fff'; ctx.textBaseline = 'middle'; ctx.fillText(text, 32, 25);
  const tex = new THREE.CanvasTexture(c);
  tex.colorSpace = THREE.SRGBColorSpace;
  const sprite = new THREE.Sprite(new THREE.SpriteMaterial({ map: tex, depthTest: false, transparent: true, toneMapped: false }));
  sprite.scale.set(w / 48 * 0.9, 0.9, 1);
  sprite.renderOrder = 999;
  return sprite;
}
