// Sky, sun, moon and haze for the 3D view, driven by the game's time of day.
// Ported from f00d4tehg0dz/valheim-webmap (MIT).
//
// A sky dome (gradient + sun disc + stars) follows the camera; the sun carries the light and
// a cool sky only fills, so a slope turned from the sun falls into shade; at night a faint
// moon takes over. The dome, over a dim ground, is baked into a small environment map so
// water and metal catch the sky. What the view draws is hazed toward the sky with distance.
// Day fraction is the game's: 0 midnight, 0.25 sunrise, 0.5 noon, 0.75 sunset.

import * as THREE from 'three';

const skyVert = `
varying vec3 vDir;
void main() {
  vDir = position;
  vec4 wp = modelMatrix * vec4(position, 1.0);
  gl_Position = projectionMatrix * viewMatrix * wp;
  gl_Position.z = gl_Position.w;   // always at the far plane
}`;

const skyFrag = `
varying vec3 vDir;
uniform vec3 uSun; uniform vec3 uZenith; uniform vec3 uHorizon; uniform vec3 uSunTint; uniform float uNight; uniform float uSunDisc; uniform vec3 uGround;
float hash(vec3 p) { return fract(sin(dot(p, vec3(12.9898, 78.233, 37.719))) * 43758.5453); }
void main() {
  vec3 d = normalize(vDir);
  float h = max(d.y, 0.0);
  vec3 col = mix(uHorizon, uZenith, pow(h, 0.5));
  float sd = max(dot(d, uSun), 0.0);
  col += uSunTint * (pow(sd, 400.0) * uSunDisc + pow(sd, 10.0) * 0.22 + pow(sd, 2.5) * 0.06);
  if (uNight > 0.001) {
    vec3 p = floor(d * 260.0);
    float s = hash(p);
    float star = smoothstep(0.992, 1.0, s) * uNight * smoothstep(0.02, 0.25, d.y);
    col += vec3(star * 0.8);
  }
  #ifdef GROUND
  col = mix(col, uGround, smoothstep(0.0, 0.2, -d.y));   // the environment sees lit ground below, not more sky
  #endif
  gl_FragColor = vec4(col, 1.0);
  #include <tonemapping_fragment>
  #include <colorspace_fragment>
}`;

// Aerial perspective: an exponential height fog (uHaze: density at the sea, 1 / scale height,
// the sea's height) summed along each sight line, so it thickens with distance, most along
// level lines low down and least looking down from high; what it veils fades toward the
// sky's own colour that way. The scene fog's near and far close it at the edge of what is loaded.
const hazePars = `
varying vec3 vHazeRay;
uniform vec3 uHorizon; uniform vec3 uSun; uniform vec3 uSunTint; uniform vec3 uHaze;
vec3 hazed(vec3 col) {
  vec3 ray = vHazeRay * mat3(viewMatrix);
  float len = length(ray), k = ray.y * uHaze.y;
  float depth = uHaze.x * len * exp((uHaze.z - cameraPosition.y) * uHaze.y) * (abs(k) < 1e-3 ? 1.0 : (1.0 - exp(-k)) / k);
  float f = 1.0 - exp(-depth) * (1.0 - smoothstep(fogNear, fogFar, len));
  float sd = max(dot(ray / max(len, 1e-3), uSun), 0.0);
  vec3 sky = uHorizon + uSunTint * (pow(sd, 10.0) * 0.22 + pow(sd, 2.5) * 0.06);
  #ifdef TONE_MAPPING
  sky = toneMapping(sky);
  #endif
  sky = linearToOutputTexel(vec4(sky, 1.0)).rgb;
  col = mix(col, vec3(dot(col, vec3(0.2126, 0.7152, 0.0722))), f * 0.4);
  return mix(col, sky, f);
}`;
export const HAZE = '\n#ifdef USE_FOG\ngl_FragColor.rgb = hazed(gl_FragColor.rgb);\n#endif\n';

const c = (hex) => new THREE.Color(hex);
const NIGHT = { zenith: c(0x05081a), horizon: c(0x0e1730) };
const DAY = { zenith: c(0x3d7ad4), horizon: c(0xb9d3ee) };
const DUSK = { zenith: c(0x3a4f8e), horizon: c(0xf3a457) };
const GROUND = c(0x4a5a3a), WHITE = c(0xffffff), DARK = c(0x0a0c12);
// The day's balance: the sun, 45 degrees up at noon rather than overhead, carries the light;
// the sky fills from above over a dim ground, so a slope turned from the sun shades.
const SUN = 3.4, SKY = 0.9, NOON = 45 * Math.PI / 180;
const lerp = (a, b, t) => a + (b - a) * t;
const smooth = (a, b, x) => { const t = Math.min(1, Math.max(0, (x - a) / (b - a))); return t * t * (3 - 2 * t); };

export class Lighting {
  constructor(scene, renderer, { sea = 30 } = {}) {
    this.scene = scene;
    this.renderer = renderer;
    this.frac = 0.5;
    this.shadows = false;

    this.hemi = new THREE.HemisphereLight(0xdde9ff, 0x4a5a3a, 1);
    this.sun = new THREE.DirectionalLight(0xfff2dc, 2.4);
    this.moon = new THREE.DirectionalLight(0x8fa8ff, 0);
    this.sun.shadow.mapSize.set(2048, 2048);
    this.sun.shadow.bias = -0.0006;
    this.sun.shadow.normalBias = 0.6;
    const cam = this.sun.shadow.camera;
    cam.near = 50; cam.far = 4500;
    scene.add(this.hemi, this.sun, this.sun.target, this.moon);

    this.uniforms = {
      uSun: { value: new THREE.Vector3(0, 1, 0) }, uZenith: { value: DAY.zenith.clone() }, uHorizon: { value: DAY.horizon.clone() },
      uSunTint: { value: new THREE.Color(1, 0.95, 0.85) }, uNight: { value: 0 }, uSunDisc: { value: 1.6 },
      uGround: { value: new THREE.Color() }, uHaze: { value: new THREE.Vector3(1 / 3800, 1 / 1200, sea) },
    };
    this.hazeHook = (shader) => this.hazeShader(shader);
    this.dome = new THREE.Mesh(new THREE.SphereGeometry(1, 32, 16), new THREE.ShaderMaterial({ uniforms: this.uniforms, vertexShader: skyVert, fragmentShader: skyFrag, side: THREE.BackSide, depthWrite: false, depthTest: false, fog: false }));
    this.dome.scale.setScalar(20000);
    this.dome.renderOrder = -1000;
    this.dome.frustumCulled = false;
    scene.add(this.dome);

    scene.fog = new THREE.Fog(DAY.horizon.clone(), 2500, 9500);
    scene.background = DAY.horizon.clone();
    this.exposure = renderer.toneMappingExposure;
    this.pmrem = new THREE.PMREMGenerator(renderer);
    this.envScene = new THREE.Scene();
    this.envDome = new THREE.Mesh(this.dome.geometry, new THREE.ShaderMaterial({ uniforms: this.uniforms, defines: { GROUND: 1 }, vertexShader: skyVert, fragmentShader: skyFrag, side: THREE.BackSide, depthWrite: false, depthTest: false, fog: false }));
    this.envDome.scale.setScalar(100);
    this.envScene.add(this.envDome);
    this.envTarget = null;
    this.setTime(0.5);
  }

  // sun direction for a day fraction: rises east, crosses the north (the side the map tiles
  // are shaded from) NOON up, sets west
  sunDir(frac) {
    const a = (frac - 0.25) * Math.PI * 2;
    return new THREE.Vector3(Math.cos(a), Math.sin(a) * Math.sin(NOON), -Math.sin(a) * Math.cos(NOON));
  }

  setTime(frac) {
    this.frac = ((frac % 1) + 1) % 1;
    // the sky's colours and the light's strength follow the clock (0 at sunrise and sunset, 1 at
    // noon); the sun's own path runs lower than that
    const dir = this.sunDir(this.frac), e = Math.sin((this.frac - 0.25) * Math.PI * 2);
    const day = smooth(-0.06, 0.22, e), dusk = 1 - smooth(0, 0.3, Math.abs(e)), night = 1 - smooth(-0.25, 0.02, e);
    const zenith = NIGHT.zenith.clone().lerp(DAY.zenith, day).lerp(DUSK.zenith, dusk * 0.7);
    const horizon = NIGHT.horizon.clone().lerp(DAY.horizon, day).lerp(DUSK.horizon, dusk * 0.85);
    this.uniforms.uSun.value.copy(dir);
    this.uniforms.uZenith.value.copy(zenith);
    this.uniforms.uHorizon.value.copy(horizon);
    this.uniforms.uNight.value = night;
    this.uniforms.uSunDisc.value = e > -0.05 ? 1.6 : 0;
    this.uniforms.uSunTint.value.set(1, lerp(0.55, 0.95, smooth(0, 0.3, e)), lerp(0.25, 0.82, smooth(0, 0.35, e))).multiplyScalar(smooth(-0.1, 0.02, e));

    // low and warm at the ends of the day, high and whiter at noon
    this.sun.color.set(1, lerp(0.58, 0.97, smooth(0, 0.35, e)), lerp(0.28, 0.93, smooth(0, 0.5, e)));
    this.sun.intensity = SUN * smooth(-0.02, 0.2, e);
    this.sunDirection = dir;
    this.hemi.color.copy(zenith).lerp(WHITE, 0.2);
    this.hemi.groundColor.copy(GROUND).lerp(DARK, night);
    this.hemi.intensity = lerp(0.32, SKY, day);
    this.envIntensity = lerp(0.35, 1, day);
    this.stopDown = lerp(1, lerp(0.78, 0.57, smooth(0.3, 0.9, e)), day);   // the overview's exposure, the higher the sun the lower
    this.uniforms.uGround.value.copy(GROUND).lerp(horizon, 0.3).multiplyScalar(lerp(0.1, 1, day));
    this.moon.intensity = 0.55 * night;
    this.moonDirection = new THREE.Vector3(-dir.x, Math.max(0.35, -dir.y), -dir.z).normalize();
    this.sun.castShadow = this.shadows && this.sun.intensity > 0.05;
    this.moon.castShadow = this.shadows && this.sun.intensity <= 0.05;

    this.scene.fog.color.copy(horizon);
    this.scene.background.copy(horizon);
    this.envDirty = true;
  }

  // Ease to a day fraction over ms, the short way round the clock.
  easeTo(frac, ms) {
    const span = ((frac - this.frac) % 1 + 1.5) % 1 - 0.5;
    this.ease = ms > 0 && Math.abs(span) > 1e-4 ? { from: this.frac, span, t0: performance.now(), ms } : null;
    if (!this.ease) this.setTime(frac);
  }

  // Haze a material the view draws: its scene fog becomes the aerial perspective. late: the
  // caller applies HAZE itself, after its own last touches.
  haze(mat) {
    if (!Object.hasOwn(mat, 'onBeforeCompile')) mat.onBeforeCompile = this.hazeHook;
    return mat;
  }
  hazeShader(shader, late) {
    for (const k of ['uHorizon', 'uSun', 'uSunTint', 'uHaze']) shader.uniforms[k] = this.uniforms[k];
    shader.vertexShader = shader.vertexShader
      .replace('#include <fog_pars_vertex>', '#include <fog_pars_vertex>\n#ifdef USE_FOG\nvarying vec3 vHazeRay;\n#endif')
      .replace('#include <fog_vertex>', '#include <fog_vertex>\n#ifdef USE_FOG\nvHazeRay = mvPosition.xyz;\n#endif');
    shader.fragmentShader = shader.fragmentShader
      .replace('#include <fog_pars_fragment>', '#include <fog_pars_fragment>\n#ifdef USE_FOG' + hazePars + '\n#endif')
      .replace('#include <fog_fragment>', late ? '' : HAZE);
  }

  // Bake the sky into an environment map (reflections on water, sheen on metal). Cheap; done
  // only when the time of day changed, a few times a second while it eases.
  updateEnvironment() {
    if (!this.envDirty || (this.ease && performance.now() - this.envAt < 250)) return;
    this.envDirty = false;
    this.envAt = performance.now();
    const old = this.envTarget;
    this.envTarget = this.pmrem.fromScene(this.envScene, 0.04, 10, 200);
    this.scene.environment = this.envTarget.texture;
    this.scene.environmentIntensity = this.envIntensity;
    if (old) old.dispose();
  }

  setShadows(on) {
    this.shadows = on;
    this.renderer.shadowMap.enabled = on;
    this.renderer.shadowMap.type = THREE.PCFSoftShadowMap;
    this.setTime(this.frac);
    this.scene.traverse((o) => { if (o.material) o.material.needsUpdate = true; });
  }

  // Per frame: keep the dome on the camera and the shadow box around the point of interest, sized
  // to the view distance so close-ups get sharp shadows and overviews still get some.
  update(camera, target) {
    if (this.ease) {
      const t = Math.min(1, (performance.now() - this.ease.t0) / this.ease.ms);
      this.setTime(this.ease.from + this.ease.span * t * t * (3 - 2 * t));
      if (t >= 1) this.ease = null;
    }
    this.dome.position.copy(camera.position);
    // the eye adjusts: from high up the view is all sunlit ground and haze, which glares at the
    // exposure made for standing in it, so the overview stops down as it climbs
    this.renderer.toneMappingExposure = this.exposure * lerp(1, this.stopDown, smooth(150, 1500, camera.position.y - target.y));
    const dist = camera.position.distanceTo(target);
    const half = Math.min(1400, Math.max(120, dist * 0.9));
    for (const [light, dir] of [[this.sun, this.sunDirection], [this.moon, this.moonDirection]]) {
      light.target.position.copy(target);
      light.position.copy(target).addScaledVector(dir, 2200);
      light.target.updateMatrixWorld();
      const sc = light.shadow.camera;
      if (sc.left !== -half) { sc.left = sc.bottom = -half; sc.right = sc.top = half; sc.updateProjectionMatrix(); }
    }
    this.updateEnvironment();
  }
}
