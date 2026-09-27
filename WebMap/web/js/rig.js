// A player's rig, stood from the model library's parts, for the 3D view and a player's page.
// three.js comes in as an argument, so the parts that need none run under `node --test`.

// What a rig is built from: the body, the parts drawn and the two colours. The same look
// gives the same key, so a player is dressed again only when what they wear changes.
export function lookKey(look) {
  if (!look || !Array.isArray(look.parts) || !look.parts.length) return null;
  const c = (v) => (Array.isArray(v) ? v.map((x) => Math.round(x * 1000)).join(',') : '');
  return [look.model | 0, look.parts.join('|'), c(look.skin), c(look.hair)].join('/');
}

// The body's chest and legs paint: the garment worn over each, else the body's own
// (entries: the library's, in the look's order, body first; ct / lt the texture files).
export function paintOf(entries) {
  const body = entries[0] || {};
  let chest = body.ct || null, legs = body.lt || null;
  for (const e of entries.slice(1)) {
    if (e && e.ct) chest = e.ct;
    if (e && e.lt) legs = e.lt;
  }
  return { chest, legs };
}

// Rigs for everyone a page draws. Each part's glTF is baked once in its standing pose and
// shared by every rig that wears it; a skin is painted once per colour and paint.
// THREE: the three.js module; gltf: a GLTFLoader; api: the server ("" for the mod's own).
export class RigBuilder {
  constructor(THREE, gltf, { api = '', shadows = true, aniso = 1 } = {}) {
    this.THREE = THREE; this.gltf = gltf; this.api = api;
    this.shadows = shadows; this.aniso = Math.min(4, aniso);
    this.index = new Map();    // prefab hash -> the library's entry
    this.names = new Map();    // rig part name -> hash
    this.parts = new Map();    // rig part hash -> Promise<pieces[] | null>, baked in the standing pose
    this.images = new Map();   // texture file -> Promise<image | null>, for the body's paint
    this.mats = new Map();     // skin and hair materials in a player's colours
  }

  // The library's index (/prefabs' entries by hash). True when a rig part is new or
  // re-exported: the rigs drawn from the old index should be built again.
  setLibrary(index) {
    const names = new Map();
    let moved = false;
    for (const [h, p] of index) {
      if (p.c !== 'rig') continue;
      names.set(p.n, h);
      const o = this.index.get(h);
      if (!o || o.m !== p.m || o.v !== p.v || o.ct !== p.ct || o.lt !== p.lt) { this.parts.delete(h); moved = true; }
    }
    this.index = index; this.names = names;
    return moved;
  }

  // A look's parts stood together at the origin, facing +z; null while the library lacks the
  // body. Every part carries the same posed skeleton, so each lands where it belongs. The
  // meshes share the builder's geometry and materials: drop the group, dispose the builder.
  async build(look) {
    if (!lookKey(look)) return null;
    const hashes = look.parts.map((n) => this.names.get(n));
    const entries = hashes.map((h) => (h === undefined ? null : this.index.get(h)));
    if (!entries[0] || !entries[0].m) return null;
    const parts = await Promise.all(hashes.map((h) => (h === undefined ? null : this.part(h))));
    if (!parts[0]) return null;
    const paint = paintOf(entries), g = new this.THREE.Group();
    for (const pieces of parts)
      for (const pc of pieces || []) {
        const mat = pc.role === 'skin' ? await this.skin(pc.material, look.skin, paint)
          : pc.role === 'hair' ? this.tinted(pc.material, look.hair) : pc.material;
        const m = new this.THREE.Mesh(pc.geometry, mat);
        m.castShadow = this.shadows; m.receiveShadow = true;
        m.rotation.y = Math.PI;                       // the parts face north, -z
        g.add(m);
      }
    g.userData.missing = hashes.filter((h) => h === undefined).length;   // parts not exported yet
    return g;
  }

  // A rig part's glTF, its skinned meshes baked in the pose its skeleton stands in: plain
  // meshes, shared by every player who wears it. [] for a part that only paints the body.
  part(hash) {
    if (this.parts.has(hash)) return this.parts.get(hash);
    const T = this.THREE, info = this.index.get(hash);
    const file = (hash >>> 0).toString(16).padStart(8, '0');
    const p = !info || !info.m ? Promise.resolve([]) : this.gltf.loadAsync(`${this.api}/models/${file}.glb?v=${info.v || 0}`).then((g) => {
      const pieces = [];
      g.scene.updateMatrixWorld(true);
      g.scene.traverse((o) => {
        if (!o.isMesh) return;
        const geometry = o.isSkinnedMesh ? bakeSkin(T, o) : o.geometry.clone().applyMatrix4(o.matrixWorld);
        if (!geometry.attributes.normal) geometry.computeVertexNormals();
        const m = o.material;
        m.side = T.DoubleSide; m.metalness = 0; m.roughness = Math.max(0.7, m.roughness);
        if (m.map) m.map.anisotropy = this.aniso;
        pieces.push({ geometry, material: m, role: m.name === 'webmap:skin' ? 'skin' : m.name === 'webmap:hair' ? 'hair' : null });
      });
      return pieces;
    }).catch((e) => { console.warn('rig part', file, e.message || e); return null; });
    this.parts.set(hash, p);
    return p;
  }

  // The body's skin: its texture in the player's skin colour, the legs' and then the chest's
  // paint laid over it (a garment's or the body's own), drawn once per look on a canvas.
  async skin(base, rgb, paint) {
    const T = this.THREE, key = ['skin', base.uuid, (rgb || []).join(), paint.chest, paint.legs].join('|');
    if (this.mats.has(key)) return this.mats.get(key);
    const [legs, chest] = await Promise.all([this.image(paint.legs), this.image(paint.chest)]);
    if (this.mats.has(key)) return this.mats.get(key);
    const img = base.map && base.map.image, S = img ? img.width : 256;
    const c = document.createElement('canvas'); c.width = c.height = S;
    const ctx = c.getContext('2d');
    if (img) ctx.drawImage(img, 0, 0, S, S); else { ctx.fillStyle = '#fff'; ctx.fillRect(0, 0, S, S); }
    const col = new T.Color().setRGB(...(rgb || [1, 1, 1]), T.SRGBColorSpace);
    ctx.globalCompositeOperation = 'multiply';
    ctx.fillStyle = '#' + col.getHexString(T.SRGBColorSpace); ctx.fillRect(0, 0, S, S);
    ctx.globalCompositeOperation = 'source-over';
    for (const over of [legs, chest]) if (over) ctx.drawImage(over, 0, 0, S, S);
    const t = new T.CanvasTexture(c);
    t.flipY = false; t.colorSpace = T.SRGBColorSpace; t.anisotropy = this.aniso;
    const mat = base.clone(); mat.map = t; mat.color.set(0xffffff);
    this.mats.set(key, mat);
    return mat;
  }

  // hair and beard: their grey texture in the player's hair colour
  tinted(base, rgb) {
    const key = ['hair', base.uuid, (rgb || []).join()].join('|');
    if (!this.mats.has(key)) {
      const mat = base.clone();
      mat.color.setRGB(...(rgb || [1, 1, 1]), this.THREE.SRGBColorSpace);
      this.mats.set(key, mat);
    }
    return this.mats.get(key);
  }

  image(file) {
    if (!file) return Promise.resolve(null);
    if (!this.images.has(file)) this.images.set(file, new Promise((resolve) => {
      const im = new Image(); im.crossOrigin = 'anonymous';
      im.onload = () => resolve(im); im.onerror = () => resolve(null);
      im.src = `${this.api}/models/${file}`;
    }));
    return this.images.get(file);
  }

  // Everything baked, loaded and painted, freed; the rigs built from it draw no more.
  dispose() {
    const free = (m) => { if (m.map) m.map.dispose(); m.dispose(); };
    for (const p of this.parts.values())
      p.then((pieces) => { for (const pc of pieces || []) { pc.geometry.dispose(); free(pc.material); } });
    for (const m of this.mats.values()) free(m);
    this.parts.clear(); this.mats.clear(); this.images.clear();
  }
}

// A skinned mesh as its skeleton stands it, as plain geometry in the scene's frame: the
// pose never moves, so skinning it once beats skinning it every frame for every player.
function bakeSkin(T, o) {
  o.skeleton.update();
  const g = o.geometry, pos = g.attributes.position, nrm = g.attributes.normal, si = g.attributes.skinIndex, sw = g.attributes.skinWeight;
  const bones = o.skeleton.boneMatrices, n = pos.count;
  const P = new Float32Array(n * 3), N = nrm ? new Float32Array(n * 3) : null;
  const acc = new T.Matrix4(), full = new T.Matrix4(), nm = new T.Matrix3(), v = new T.Vector3();
  const post = new T.Matrix4().multiplyMatrices(o.matrixWorld, o.bindMatrixInverse), e = acc.elements;
  for (let i = 0; i < n; i++) {
    e.fill(0);
    for (let k = 0; k < 4; k++) {
      const w = sw.getComponent(i, k);
      if (!w) continue;
      const j = si.getComponent(i, k) * 16;
      for (let c = 0; c < 16; c++) e[c] += w * bones[j + c];
    }
    full.multiplyMatrices(post, acc).multiply(o.bindMatrix);
    v.fromBufferAttribute(pos, i).applyMatrix4(full);
    P[i * 3] = v.x; P[i * 3 + 1] = v.y; P[i * 3 + 2] = v.z;
    if (N) { v.fromBufferAttribute(nrm, i).applyMatrix3(nm.getNormalMatrix(full)).normalize(); N[i * 3] = v.x; N[i * 3 + 1] = v.y; N[i * 3 + 2] = v.z; }
  }
  const out = new T.BufferGeometry();
  out.setAttribute('position', new T.BufferAttribute(P, 3));
  if (N) out.setAttribute('normal', new T.BufferAttribute(N, 3));
  if (g.attributes.uv) out.setAttribute('uv', g.attributes.uv);
  if (g.index) out.setIndex(g.index);
  return out;
}
