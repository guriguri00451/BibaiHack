import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { XRHandModelFactory } from 'three/addons/webxr/XRHandModelFactory.js';
import { GLTFExporter } from 'three/addons/exporters/GLTFExporter.js';
import { mergeGeometries } from 'three/addons/utils/BufferGeometryUtils.js';
import { ClayField } from './sdf.js?v=0.6.6';
import { ButtonPanel } from './panel.js?v=0.6.6';
import { initPost } from './post/ui.js?v=0.6.6';

// ================= 設定 =================
const CLAY_HOME = new THREE.Vector3(0, 1.1, -0.45);
// さわる: XR では手の押し込みの深さで、軽く触れる=なでる / 深く押す=こねる に自動で切り替わる。
//   画面では、ドラッグ=こねる / Shift+ドラッグ=なでる
const MODES = {
  touch: { label: 'さわる', color: '#4fc3f7' },
  pull: { label: 'つまむ', color: '#ff8a65' }, // 画面用 (XR ではピンチが常に「つまむ」)
  paint: { label: '塗る', color: '#f48fb1' },
  // 画面の「さわる」の中身 (ボタンには出さない)
  knead: { label: 'こねる', color: '#4fc3f7', hidden: true },
  smooth: { label: 'なでる', color: '#ffd54f', hidden: true },
};
// 押し込みの深さ (本物の手が粘土の表面からめり込んだ深さ, m) と、なでる→こねるの切り替え
const TOUCH = {
  kneadFrom: 0.004, // これより浅いと、こねずになでるだけ
  kneadSpan: 0.010, // ここから kneadFrom + kneadSpan まで、なでる→こねるへなめらかに切り替わる
};
// 塗るときの色 (sRGB)
const PALETTE = [
  '#c98060', '#8d5a3b', '#f2e6d0', '#ffffff', '#3b3b3b', '#d84a3a',
  '#f29a38', '#f2d14a', '#6bbf59', '#3f8fd2', '#7a5cc7', '#ef8fb5',
];
const state = { mode: 'touch', brushR: 0.015, color: PALETTE[0] };
let _rgbKey = '', _rgb = [0, 0, 0];
function colorRGB() {
  if (_rgbKey !== state.color) {
    _rgbKey = state.color;
    const n = parseInt(state.color.slice(1), 16);
    _rgb = [(n >> 16) & 255, (n >> 8) & 255, n & 255];
  }
  return _rgb;
}
const grabR = () => state.brushR * 2.2;
const PINCH_ON = 0.02, PINCH_OFF = 0.04, PRE_PINCH = 0.05; // m

// ================= レンダラーとシーン =================
const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
renderer.setSize(innerWidth, innerHeight);
renderer.xr.enabled = true;
renderer.xr.setReferenceSpaceType('local-floor');
document.getElementById('app').appendChild(renderer.domElement);

const scene = new THREE.Scene();
const BG = new THREE.Color(0x1d2026);
scene.background = BG;

const camera = new THREE.PerspectiveCamera(45, innerWidth / innerHeight, 0.01, 50);
camera.position.set(0.24, 1.3, -0.02);

scene.add(new THREE.HemisphereLight(0xffffff, 0x3a3020, 1.3));
const sun = new THREE.DirectionalLight(0xffffff, 2.2);
sun.position.set(0.6, 2.5, 0.8);
scene.add(sun);
const rim = new THREE.DirectionalLight(0xaabbff, 0.7);
rim.position.set(-1, 1.2, -1.5);
scene.add(rim);

const floor = new THREE.GridHelper(6, 30, 0x454b57, 0x2c3038);
scene.add(floor);

// 作業台 (台座とパネル)。「手元へ」で丸ごと移動する
const workspace = new THREE.Group();
workspace.position.copy(CLAY_HOME);
scene.add(workspace);
const pedestal = new THREE.Mesh(
  new THREE.CylinderGeometry(0.11, 0.13, 0.02, 48),
  new THREE.MeshStandardMaterial({ color: 0x4d525c, roughness: 0.6 }),
);
pedestal.position.y = -0.085;
workspace.add(pedestal);

// ================= 粘土 =================
const clay = new THREE.Group();
clay.position.copy(CLAY_HOME);
scene.add(clay);

const field = new ClayField({ N: 80, size: 0.4 });
field.resetCube(0.07, 0.01);

// 色は頂点カラー (格子点の色を補間したもの)
const clayMat = new THREE.MeshStandardMaterial({ color: 0xffffff, vertexColors: true, roughness: 0.78, metalness: 0 });
const chunkMeshes = new Map();

// 編集できる範囲 (格子の外枠) をうっすら表示
const bounds = new THREE.LineSegments(
  new THREE.EdgesGeometry(new THREE.BoxGeometry(field.size, field.size, field.size)),
  new THREE.LineBasicMaterial({ color: 0x8890a0, transparent: true, opacity: 0.15 }),
);
clay.add(bounds);

function remeshDirty() {
  const keys = field.takeDirty();
  for (const key of keys) {
    const { positions, normals, colors, indices } = field.meshChunk(key);
    let m = chunkMeshes.get(key);
    if (!m) {
      m = new THREE.Mesh(new THREE.BufferGeometry(), clayMat);
      clay.add(m);
      chunkMeshes.set(key, m);
    }
    m.geometry.dispose();
    const g = new THREE.BufferGeometry();
    if (indices.length) {
      g.setAttribute('position', new THREE.BufferAttribute(positions, 3));
      g.setAttribute('normal', new THREE.BufferAttribute(normals, 3));
      g.setAttribute('color', new THREE.BufferAttribute(colors, 3));
      g.setIndex(new THREE.BufferAttribute(indices, 1));
      g.computeBoundingSphere();
    }
    m.geometry = g;
    m.visible = indices.length > 0;
  }
  return keys.length;
}

// 座標変換: ワールド (m) ⇔ 格子座標
const _w = new THREE.Vector3();
function worldToGrid(w, out = new THREE.Vector3()) {
  out.copy(w);
  clay.worldToLocal(out);
  return out.addScalar(field.half).divideScalar(field.h);
}
function gridToWorld(g, out = new THREE.Vector3()) {
  out.copy(g).multiplyScalar(field.h).addScalar(-field.half);
  return clay.localToWorld(out);
}
const worldLenToGrid = (len) => len / (field.h * clay.scale.x);

function applyTool(mode, worldPos, strength = 0.5, dir = null) {
  const g = worldToGrid(worldPos, _w);
  const r = worldLenToGrid(state.brushR);
  if (mode === 'knead' && dir) {
    // デスクトップのこねる: 押した向きに粘土を押しのける (削らずに動かす)
    const g2 = worldToGrid(new THREE.Vector3().copy(worldPos).addScaledVector(dir, state.brushR * 0.3));
    return field.pull(g, g2.sub(g), r * 1.6);
  }
  if (mode === 'paint') return field.paint(g, r, colorRGB(), strength);
  if (mode === 'smooth') return field.smooth(g, r * 1.4, strength);
  return false;
}

function pullBetween(gFrom, gTo) {
  const delta = new THREE.Vector3().subVectors(gTo, gFrom);
  return field.pull(gFrom, delta, worldLenToGrid(grabR()));
}

// ================= Undo =================
const undoStack = [];
function pushUndo() {
  undoStack.push(field.snapshot());
  if (undoStack.length > 30) undoStack.shift();
}
function undo() {
  const s = undoStack.pop();
  if (s) field.restore(s);
}
function restoreSnapshot(snap) {
  pushUndo();
  field.restore(snap);
}
// 立方体に戻して作業台の上に置き直す (「新しく作る」も同じ)
function resetClay() {
  pushUndo();
  field.resetCube(0.07, 0.01);
  clay.position.copy(workspace.position);
  clay.quaternion.copy(workspace.quaternion);
  clay.scale.setScalar(1);
  clay.updateMatrixWorld();
}

// ================= ブラシ表示 =================
function makeCursor() {
  const m = new THREE.Mesh(
    new THREE.SphereGeometry(1, 24, 16),
    new THREE.MeshBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.28, depthWrite: false }),
  );
  m.renderOrder = 10;
  m.visible = false;
  scene.add(m);
  return m;
}
function showCursor(m, pos, radius, color, opacity) {
  m.visible = true;
  m.position.copy(pos);
  m.scale.setScalar(radius);
  m.material.color.set(color);
  m.material.opacity = opacity;
}
const desktopCursor = makeCursor();

// ================= デスクトップ操作 =================
// 粘土の上で左ドラッグ = 造形 / 何もない所で左ドラッグ = 視点回転
const el = renderer.domElement;
const raycaster = new THREE.Raycaster();
const ndc = new THREE.Vector2();
let stroke = null;

function rayFromEvent(e) {
  const rect = el.getBoundingClientRect();
  ndc.set(((e.clientX - rect.left) / rect.width) * 2 - 1, -((e.clientY - rect.top) / rect.height) * 2 + 1);
  raycaster.setFromCamera(ndc, camera);
  return raycaster.ray;
}

function raymarchWorld(ray, arr) {
  clay.updateMatrixWorld();
  const o = worldToGrid(ray.origin);
  const o2 = worldToGrid(new THREE.Vector3().copy(ray.origin).add(ray.direction));
  const dir = o2.sub(o).normalize();
  const t = field.raymarch(o, dir, arr);
  if (t === null) return null;
  return gridToWorld(o.addScaledVector(dir, t));
}

function applyDesktopStroke(ray, first) {
  const m = stroke.mode;
  if (m === 'pull') {
    const np = ray.intersectPlane(stroke.plane, new THREE.Vector3());
    if (!np) return;
    const gTo = worldToGrid(np.clone().add(stroke.offset));
    pullBetween(stroke.lastG, gTo);
    stroke.lastG.copy(gTo);
    showCursor(desktopCursor, np, grabR(), MODES.pull.color, 0.3);
    return;
  }
  // 削る・盛るはストローク開始時の形に当てる (同じ所をなぞっても掘り進まない・盛り上がり続けない)
  const arr = m === 'smooth' || m === 'paint' ? field.d : stroke.base.d;
  const hit = raymarchWorld(ray, arr);
  if (!hit) return;
  const center = hit.clone();
  if (m === 'knead') center.addScaledVector(ray.direction, state.brushR * 0.3);
  showCursor(desktopCursor, hit, state.brushR, MODES[m].color, 0.25);
  if (first || m === 'smooth') {
    applyTool(m, center, 0.5, ray.direction);
    stroke.lastC = center.clone();
    return;
  }
  const step = state.brushR * 0.3;
  const dist = center.distanceTo(stroke.lastC);
  if (dist < step) return;
  const n = Math.ceil(dist / step);
  const p = new THREE.Vector3();
  for (let i = 1; i <= n; i++) applyTool(m, p.lerpVectors(stroke.lastC, center, i / n), 0.5, ray.direction);
  stroke.lastC.copy(center);
}

// OrbitControls より先に登録して、粘土に当たったら視点操作を止める
el.addEventListener('pointerdown', (e) => {
  if (renderer.xr.isPresenting || e.button !== 0) return;
  const ray = rayFromEvent(e);
  const p = raymarchWorld(ray, field.d);
  if (!p) return;
  controls.enabled = false;
  el.setPointerCapture(e.pointerId);
  pushUndo();
  const offset = ray.direction.clone().multiplyScalar(grabR() * 0.25); // つまむ: 表面の少し内側をつかむ
  stroke = {
    id: e.pointerId,
    mode: state.mode === 'touch' ? (e.shiftKey ? 'smooth' : 'knead') : state.mode,
    base: undoStack[undoStack.length - 1],
    offset,
    lastG: worldToGrid(p.clone().add(offset)),
    plane: new THREE.Plane().setFromNormalAndCoplanarPoint(camera.getWorldDirection(new THREE.Vector3()), p),
  };
  applyDesktopStroke(ray, true);
});
el.addEventListener('pointermove', (e) => {
  if (renderer.xr.isPresenting) return;
  const ray = rayFromEvent(e);
  if (stroke && e.pointerId === stroke.id) {
    applyDesktopStroke(ray, false);
    return;
  }
  const p = raymarchWorld(ray, field.d);
  if (p) {
    const pull = state.mode === 'pull';
    showCursor(desktopCursor, p, pull ? grabR() : state.brushR, MODES[state.mode].color, 0.22);
  } else desktopCursor.visible = false;
});
const endStroke = (e) => {
  if (!stroke || e.pointerId !== stroke.id) return;
  stroke = null;
  controls.enabled = true;
  if (el.hasPointerCapture(e.pointerId)) el.releasePointerCapture(e.pointerId);
};
el.addEventListener('pointerup', endStroke);
el.addEventListener('pointercancel', endStroke);
el.addEventListener('pointerleave', () => { if (!stroke) desktopCursor.visible = false; });

const controls = new OrbitControls(camera, el);
controls.target.copy(CLAY_HOME);
controls.enableDamping = true;
controls.minDistance = 0.15;
controls.maxDistance = 3;
controls.update();

// ================= XR: 手 =================
class OneEuro3 {
  constructor(minCutoff = 1.2, beta = 6, dCutoff = 1) {
    Object.assign(this, { minCutoff, beta, dCutoff });
    this.x = null;
    this.dx = new THREE.Vector3();
    this.t = 0;
    this._raw = new THREE.Vector3();
  }
  static alpha(dt, cutoff) {
    const tau = 1 / (2 * Math.PI * cutoff);
    return 1 / (1 + tau / dt);
  }
  filter(v, t) {
    if (!this.x || t - this.t > 0.2) {
      this.x = v.clone();
      this.dx.set(0, 0, 0);
      this.t = t;
      return this.x;
    }
    const dt = Math.max(t - this.t, 1e-3);
    this.t = t;
    this._raw.subVectors(v, this.x).divideScalar(dt);
    this.dx.lerp(this._raw, OneEuro3.alpha(dt, this.dCutoff));
    const cutoff = this.minCutoff + this.beta * this.dx.length();
    this.x.lerp(v, OneEuro3.alpha(dt, cutoff));
    return this.x;
  }
}

// ---- 手の形: 骨ごとのカプセルで手のひら全体を表す ----
const FINGERS = ['index-finger', 'middle-finger', 'ring-finger', 'pinky-finger'];
const PALM_R = 0.014, FINGER_R = 0.009; // m (0 = 関節の半径を使う)
const BONES = [];
for (const f of FINGERS) {
  BONES.push(
    ['wrist', `${f}-phalanx-proximal`, PALM_R],
    [`${f}-phalanx-proximal`, `${f}-phalanx-intermediate`, 0],
    [`${f}-phalanx-intermediate`, `${f}-phalanx-distal`, 0],
    [`${f}-phalanx-distal`, `${f}-tip`, 0],
  );
}
BONES.push(
  ['wrist', 'thumb-metacarpal', PALM_R],
  ['thumb-metacarpal', 'thumb-phalanx-proximal', 0],
  ['thumb-phalanx-proximal', 'thumb-phalanx-distal', 0],
  ['thumb-phalanx-distal', 'thumb-tip', 0],
  ['thumb-metacarpal', 'index-finger-phalanx-proximal', PALM_R], // 親指の付け根のふくらみ
  ['index-finger-phalanx-proximal', 'pinky-finger-phalanx-proximal', PALM_R], // 指の付け根の列
  ['index-finger-metacarpal', 'pinky-finger-metacarpal', PALM_R], // 手のひらの中ほど
);
const JOINT_NAMES = [...new Set(BONES.flatMap(([a, b]) => [a, b]))];

// 粘性: 手が動いた量のうち、粘土が付いてくる割合
const VISC = {
  F: 0.012,      // 手の表面からどこまで粘土が引きずられるか (m)
  push: 0.25,    // 押し込む向き (小さいほど硬い。足りない分は下の LIMIT.pressure でゆっくり流れる)
  slide: 0.45,   // 表面に沿ってなでる向き
  stick: 0.25,   // 手を離す向き (少しだけ引っ張られる)
  maxStep: 0.012, // 1フレームで動かす上限 (m)
};

// 押し込みの制限 (タッチリミッティング / 疑似触覚)
// 本物の手が粘土にめり込んでも、見えている手は表面近くで徐々に止まる。
// 見えている手との差 (押し込み過ぎた分) は「押す力」として、粘土をゆっくり流れさせる。
const LIMIT = {
  depth: 0.012,   // 見えている手がめり込める深さの上限 (m)。浅いほど硬く感じる
  pressure: 1.0,  // 押し込み過ぎた分 1m あたり、毎秒どれだけ粘土を流すか (1/s)。大きいほど早く沈む
  follow: 25,     // 見えている手が目標に追いつく速さ (1/s)
  release: 10,    // 離したとき見えている手が本物の手に戻る速さ (1/s)
};

const handFactory = new XRHandModelFactory();
const hands = [0, 1].map((i) => {
  const hand = renderer.xr.getHand(i);
  // 見えている手 (表面で止まる) と、本物の手の位置を薄く示すゴースト
  const model = handFactory.createHandModel(hand, 'mesh');
  const ghost = handFactory.createHandModel(hand, 'mesh');
  hand.add(model, ghost);
  scene.add(hand);
  const hs = {
    hand, model, ghost, connected: false, handedness: null,
    offset: new THREE.Vector3(), n: new THREE.Vector3(0, 1, 0), s: 0, excess: 0, styled: false,
    jreal: new Map(JOINT_NAMES.map((n) => [n, new THREE.Vector3()])),
    tipF: new OneEuro3(), pinchF: new OneEuro3(),
    tip: new THREE.Vector3(), pinchPos: new THREE.Vector3(),
    pinching: false, pinchMode: null, lastG: new THREE.Vector3(), grabOffset: new THREE.Vector3(),
    touching: false, pressed: null,
    jointF: new Map(JOINT_NAMES.map((n) => [n, new OneEuro3()])),
    jpos: new Map(JOINT_NAMES.map((n) => [n, new THREE.Vector3()])),
    jprev: new Map(JOINT_NAMES.map((n) => [n, new THREE.Vector3()])),
    valid: new Set(), prevValid: new Set(),
    contact: false, lastContactT: 0,
    cursor: makeCursor(),
    ctrl: renderer.xr.getController(i), // 手から伸びるレイ (OS のカーソルと同じもの)
    rayMode: null, uiPinch: null, uiHit: null,
  };
  scene.add(hs.ctrl);
  hs.ctrl.addEventListener('connected', (e) => { hs.rayMode = e.data?.targetRayMode || null; log(`レイ(${hs.handedness || i}): ${hs.rayMode}`); });
  hand.addEventListener('connected', (e) => { hs.connected = true; hs.handedness = e.data.handedness; });
  hand.addEventListener('disconnected', () => {
    hs.connected = false; endPinch(hs); hs.touching = false; hs.contact = false;
    hs.cursor.visible = false; hs.prevValid.clear(); hs.offset.set(0, 0, 0); hs.excess = 0; hs.s = 0;
    hs.uiPinch = null; hs.uiHit = null;
  });
  return hs;
});

let manipDirty = true;
let manip = null;

function startPinch(hs) {
  hs.pinching = true;
  manipDirty = true;
  const g = worldToGrid(hs.pinchPos);
  const s = field.sample(g);
  const R = worldLenToGrid(grabR());
  if (s < R * 0.5) {
    hs.pinchMode = 'pull';
    pushUndo();
    // つまんだ所から表面の少し内側をつかむ (つかむ点と指の位置関係は保つ)
    const n = field.gradient(g, new THREE.Vector3());
    const inside = g.clone().addScaledVector(n, -(s + R * 0.25));
    hs.grabOffset.subVectors(gridToWorld(inside), hs.pinchPos);
    hs.lastG.copy(inside);
  } else {
    hs.pinchMode = 'move';
  }
}
function endPinch(hs) {
  if (!hs.pinching) return;
  hs.pinching = false;
  hs.pinchMode = null;
  manipDirty = true;
}

// 何もない所をつまむ: 片手で移動、両手で回転・拡大縮小
function updateObjectManip(movers) {
  if (manipDirty || !manip || manip.count !== movers.length) {
    manipDirty = false;
    manip = { count: movers.length };
    if (movers.length === 1) {
      manip.offset = clay.position.clone().sub(movers[0].pinchPos);
    } else if (movers.length >= 2) {
      const [a, b] = movers;
      manip.v0 = b.pinchPos.clone().sub(a.pinchPos);
      manip.mid0 = a.pinchPos.clone().add(b.pinchPos).multiplyScalar(0.5);
      manip.pos0 = clay.position.clone();
      manip.quat0 = clay.quaternion.clone();
      manip.scale0 = clay.scale.x;
    }
  }
  if (movers.length === 1) {
    clay.position.copy(movers[0].pinchPos).add(manip.offset);
  } else if (movers.length >= 2 && manip.v0.length() > 0.01) {
    const [a, b] = movers;
    const v = b.pinchPos.clone().sub(a.pinchPos);
    const mid = a.pinchPos.clone().add(b.pinchPos).multiplyScalar(0.5);
    const newScale = THREE.MathUtils.clamp(manip.scale0 * v.length() / manip.v0.length(), 0.25, 4);
    const s = newScale / manip.scale0;
    const q = new THREE.Quaternion().setFromUnitVectors(manip.v0.clone().normalize(), v.normalize());
    clay.quaternion.copy(q).multiply(manip.quat0);
    clay.scale.setScalar(newScale);
    clay.position.copy(mid).add(manip.pos0.clone().sub(manip.mid0).applyQuaternion(q).multiplyScalar(s));
  }
  clay.updateMatrixWorld();
}

// 関節の位置を読み、震えを抑える
function readJoints(hs, t) {
  hs.valid.clear();
  for (const name of JOINT_NAMES) {
    const j = hs.hand.joints[name];
    if (!j || !j.visible) continue;
    j.getWorldPosition(_tipRaw);
    hs.jreal.get(name).copy(hs.jointF.get(name).filter(_tipRaw, t));
    hs.valid.add(name);
  }
}

// 見えている手 (プロキシ) のずらし量を決める。
// 粘土の内部の距離はくずれやすいので、表面近くにいる「見えている手」のめり込みだけを測り、
// 本物の手の深さ = 見えている手のめり込み + これまでのずらし量 として扱う (god-object 方式)
const _lg = new THREE.Vector3(), _ln = new THREE.Vector3(), _lp = new THREE.Vector3();
function updateTouchLimit(hs, dt, active) {
  let best = -Infinity;
  if (active) {
    for (const [a, b, rFixed] of BONES) {
      if (!hs.valid.has(a) || !hs.valid.has(b)) continue;
      const r = worldLenToGrid(rFixed || FINGER_R);
      const pa = hs.jreal.get(a), pb = hs.jreal.get(b);
      for (const k of [0, 0.5, 1]) {
        _lp.lerpVectors(pa, pb, k).add(hs.offset);
        const g = worldToGrid(_lp, _lg);
        const depth = r - field.sample(g); // 格子単位。正ならめり込み、負なら表面までのすき間
        if (depth > best) {
          best = depth;
          if (depth > -2) field.gradient(g, _ln);
        }
      }
    }
  }
  let sTarget = 0;
  let depthNow = 0;
  if (best > -Infinity) {
    const proxyDepth = best * field.h * clay.scale.x; // m
    if (proxyDepth > -0.005) {
      // 押す向き (粘土の外向き) をなめらかに追う
      _ln.applyQuaternion(clay.quaternion);
      if (hs.s <= 1e-4) hs.n.copy(_ln); else hs.n.lerp(_ln, 0.15).normalize();
    }
    const D = Math.max(0, proxyDepth + hs.s); // 本物の手のめり込み
    depthNow = D;
    // 見せるめり込み = L(1 - e^(-D/L)): 浅いうちはほぼそのまま、深いほど止まる
    const L = LIMIT.depth;
    sTarget = D - L * (1 - Math.exp(-D / L));
  }
  // 押し込みの深さをなめらかに追う (深くなるのは速く、浅くなるのはゆっくり)
  hs.depth = THREE.MathUtils.lerp(hs.depth || 0, depthNow, 1 - Math.exp(-(depthNow > (hs.depth || 0) ? 30 : 12) * dt));
  const rate = sTarget > hs.s ? LIMIT.follow : LIMIT.release;
  hs.s += (sTarget - hs.s) * (1 - Math.exp(-rate * dt));
  if (hs.s < 1e-5) hs.s = 0;
  hs.excess = hs.s;
  hs.offset.copy(hs.n).multiplyScalar(hs.s);
  for (const name of hs.valid) hs.jpos.get(name).copy(hs.jreal.get(name)).add(hs.offset);
}

// 手のモデルの見た目: 見えている手はずらした位置に、ゴーストは本物の位置に
// 手は常に不透明で見せる
const handMat = new THREE.MeshStandardMaterial({ color: 0xe3c4b3, roughness: 0.55 });
const ghostMat = new THREE.MeshBasicMaterial({ color: 0x9fd3ff, transparent: true, opacity: 0, depthTest: false, depthWrite: false });
function updateHandVisuals(hs) {
  if (!hs.styled) {
    const m = hs.model.getObjectByProperty('type', 'SkinnedMesh');
    const g = hs.ghost.getObjectByProperty('type', 'SkinnedMesh');
    if (m && g) {
      m.material = handMat.clone();
      g.material = ghostMat.clone();
      g.renderOrder = 50;
      hs.styled = true;
    }
  }
  const off = hs.offset.length();
  hs.model.position.copy(hs.offset);
  const ar = xrMode === 'immersive-ar';
  hs.model.visible = true;
  hs.ghost.visible = !ar && off > 0.004;
  if (hs.styled) {
    hs.ghost.getObjectByProperty('type', 'SkinnedMesh').material.opacity = THREE.MathUtils.smoothstep(off, 0.004, 0.02) * 0.3;
  }
}
function rememberJoints(hs) {
  hs.prevValid.clear();
  for (const name of hs.valid) {
    hs.jprev.get(name).copy(hs.jpos.get(name));
    hs.prevValid.add(name);
  }
}

// 手のカプセルを格子座標にして、粘土に近いものだけ返す
const _gm = new THREE.Vector3(), _n = new THREE.Vector3(), _u = new THREE.Vector3();
function handCapsules(hs, dt = 1 / 72) {
  const caps = [];
  let contact = false;
  const F = worldLenToGrid(VISC.F);
  const maxStep = worldLenToGrid(VISC.maxStep);
  for (const [a, b, rFixed] of BONES) {
    if (!hs.valid.has(a) || !hs.valid.has(b)) continue;
    let rw = rFixed;
    if (!rw) {
      const ra = hs.hand.joints[a].jointRadius, rb = hs.hand.joints[b].jointRadius;
      rw = ra && rb ? (ra + rb) / 2 : FINGER_R;
    }
    const r = worldLenToGrid(rw);
    const ga = worldToGrid(hs.jpos.get(a)), gb = worldToGrid(hs.jpos.get(b));
    _gm.addVectors(ga, gb).multiplyScalar(0.5);
    const sMin = Math.min(field.sample(ga), field.sample(gb), field.sample(_gm));
    if (sMin > r + F + ga.distanceTo(gb) * 0.25) continue;
    if (sMin < r) contact = true;

    // このフレームの手の動き (両端の平均)
    _u.set(0, 0, 0);
    if (hs.prevValid.has(a) && hs.prevValid.has(b)) {
      const qa = worldToGrid(hs.jprev.get(a)), qb = worldToGrid(hs.jprev.get(b));
      _u.set((ga.x - qa.x + gb.x - qb.x) / 2, (ga.y - qa.y + gb.y - qb.y) / 2, (ga.z - qa.z + gb.z - qb.z) / 2);
      // 粘性: 押し込む向きはよく付いてくる、横はやや遅れる、離す向きは少しだけ引っ張られる
      field.gradient(_gm, _n);
      const un = _u.dot(_n);
      _u.addScaledVector(_n, -un).multiplyScalar(VISC.slide)
        .addScaledVector(_n, un * (un > 0 ? VISC.stick : VISC.push));
    }
    // 押し込み過ぎた分は「押す力」: 触れている所の粘土を内側へゆっくり流す
    if (hs.excess > 0 && sMin < r) {
      field.gradient(_gm, _n);
      _u.addScaledVector(_n, -LIMIT.pressure * worldLenToGrid(hs.excess) * dt);
    }
    if (_u.length() > maxStep) _u.setLength(maxStep);
    caps.push({ ax: ga.x, ay: ga.y, az: ga.z, bx: gb.x, by: gb.y, bz: gb.z, r, ux: _u.x, uy: _u.y, uz: _u.z });
  }
  return { caps, contact, F };
}

// 手のひら全体でさわる (なでる / こねる)
function handSculpt(hs, dt, t) {
  const { caps, contact, F } = handCapsules(hs, dt);
  if (contact) {
    if (!hs.contact) { pushUndo(); hs.contact = true; }
    hs.lastContactT = t;
  } else if (hs.contact && t - hs.lastContactT > 0.3) {
    hs.contact = false;
  }
  if (!caps.length) return;
  // 押し込みの深さで なでる ⇔ こねる を切り替える。k = 0: なでる / k = 1: こねる
  const k = THREE.MathUtils.smoothstep(hs.depth || 0, TOUCH.kneadFrom, TOUCH.kneadFrom + TOUCH.kneadSpan);
  if (k > 0.02) {
    const moving = [];
    for (const c of caps) {
      c.ux *= k; c.uy *= k; c.uz *= k; // 浅いうちは粘土を動かす量も少なくする
      if (Math.hypot(c.ux, c.uy, c.uz) > 0.01) moving.push(c);
    }
    field.displace(moving, F);
  }
  if (contact && k < 0.98) {
    const strength = dt * 5 * (1 - k);
    for (const c of caps) {
      const R = c.r + F;
      field.smooth({ x: (c.ax + c.bx) / 2, y: (c.ay + c.by) / 2, z: (c.az + c.bz) / 2 }, R, strength);
      field.smooth({ x: c.bx, y: c.by, z: c.bz }, R, strength);
    }
  }
}

const _tipRaw = new THREE.Vector3(), _thumbRaw = new THREE.Vector3(), _mid = new THREE.Vector3();

// ---- レイで指す (OS と同じ: 手のレイにカーソルが出て、つまむとクリック) ----
const uiPanels = () => [panel, propPanel, ...post.panels];
const _ro = new THREE.Vector3(), _rd = new THREE.Vector3(), _rq = new THREE.Quaternion();
function handRay(hs) {
  const c = hs.ctrl;
  if (!c || !c.visible) return null;
  c.updateWorldMatrix(true, false);
  c.getWorldPosition(_ro);
  c.getWorldQuaternion(_rq);
  _rd.set(0, 0, -1).applyQuaternion(_rq);
  return { origin: _ro, dir: _rd };
}
// カーソル (板の上の輪) とレイの線
function makeUiPointer() {
  const ring = new THREE.Mesh(
    new THREE.RingGeometry(0.007, 0.011, 32),
    new THREE.MeshBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.95, depthTest: false, depthWrite: false, side: THREE.DoubleSide }),
  );
  const dot = new THREE.Mesh(
    new THREE.CircleGeometry(0.0028, 16),
    new THREE.MeshBasicMaterial({ color: 0xffffff, depthTest: false, depthWrite: false }),
  );
  ring.add(dot);
  ring.renderOrder = 60; dot.renderOrder = 61;
  ring.visible = false;
  const geo = new THREE.BufferGeometry().setFromPoints([new THREE.Vector3(), new THREE.Vector3()]);
  const line = new THREE.Line(geo, new THREE.LineBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.35, depthTest: false }));
  line.frustumCulled = false; line.renderOrder = 59; line.visible = false;
  scene.add(ring, line);
  return { ring, line };
}
for (const hs of hands) hs.ptr = makeUiPointer();
const _q2 = new THREE.Quaternion(), _nrm = new THREE.Vector3();
function showUiPointer(hs, hit, origin, pressed) {
  const { ring, line } = hs.ptr;
  hit.panel.group.getWorldQuaternion(_q2);
  _nrm.set(0, 0, 1).applyQuaternion(_q2);
  ring.visible = line.visible = true;
  ring.position.copy(hit.point).addScaledVector(_nrm, 0.004);
  ring.quaternion.copy(_q2);
  ring.scale.setScalar(pressed ? 0.7 : hit.button ? 1 : 1.4);
  ring.material.color.set(pressed ? '#ffd54f' : '#ffffff');
  const p = line.geometry.attributes.position;
  p.setXYZ(0, origin.x, origin.y - 0.02, origin.z);
  p.setXYZ(1, ring.position.x, ring.position.y, ring.position.z);
  p.needsUpdate = true;
}
function hideUiPointer(hs) { hs.ptr.ring.visible = hs.ptr.line.visible = false; }

function updateXR(dt, t) {
  clay.updateMatrixWorld();
  const movers = [];
  for (const p of uiPanels()) p.hover.clear();
  for (const hs of hands) {
    const tipJ = hs.hand.joints['index-finger-tip'];
    const thumbJ = hs.hand.joints['thumb-tip'];
    if (!hs.connected || !tipJ || !thumbJ || !tipJ.visible || !thumbJ.visible) {
      hs.cursor.visible = false;
      hideUiPointer(hs);
      hs.uiPinch = null;
      endPinch(hs);
      hs.touching = false;
      hs.contact = false;
      hs.prevValid.clear();
      hs.offset.set(0, 0, 0);
      hs.excess = 0;
      hs.s = 0;
      continue;
    }
    readJoints(hs, t);
    // レイが板を指しているか (粘土のすぐそばにある手は、粘土を触る方を優先して指さない)
    const ray = handRay(hs);
    let hit = null;
    if (ray && (!hs.pinching || hs.uiPinch)) {
      const tipG = worldToGrid(hs.jreal.get('index-finger-tip'), _w);
      const nearClay = !hs.uiPinch && field.sample(tipG) < worldLenToGrid(0.06);
      if (!nearClay) {
        for (const pn of uiPanels()) {
          const h = pn.rayHit(ray.origin, ray.dir);
          if (h && (!hit || h.t < hit.t)) hit = h;
        }
      }
    }
    if (hs.uiPinch && !hit) hit = hs.uiHit; // つまんでいる間は、レイが板をはずれても同じ板を操作し続ける
    const pointing = !!hit;
    updateTouchLimit(hs, dt, !pointing && !hs.pinching && state.mode === 'touch');
    tipJ.getWorldPosition(_tipRaw);
    thumbJ.getWorldPosition(_thumbRaw);
    hs.tip.copy(hs.tipF.filter(_tipRaw, t));
    _mid.addVectors(_tipRaw, _thumbRaw).multiplyScalar(0.5);
    hs.pinchPos.copy(hs.pinchF.filter(_mid, t));
    const pinchDist = _tipRaw.distanceTo(_thumbRaw);
    hs.cursor.visible = false;

    // ボタン
    if (!hs.pinching && (panel.press(hs.tip, hs) || propPanel.press(hs.tip, hs) || post.xrPanelPress(hs.tip, hs))) {
      rememberJoints(hs);
      continue;
    }

    // レイでクリック: 指して、つまむ。つまんでいる間はスライダーをなぞれる
    if (pointing && !hs.pinching) {
      if (hs.uiPinch) {
        if (pinchDist > PINCH_OFF) hs.uiPinch = null;
        else hs.uiPinch.panel.rayDrag(hs.uiPinch.button, ray.origin, ray.dir);
      } else if (pinchDist < PINCH_ON) {
        hit.panel.rayDown(hit);
        hs.uiPinch = { panel: hit.panel, button: hit.button };
      }
      hs.uiHit = hit;
      if (hit.button) hit.panel.hover.add(hit.button.id);
      showUiPointer(hs, hit, ray.origin, !!hs.uiPinch);
      rememberJoints(hs);
      continue;
    }
    hs.uiPinch = null;
    hideUiPointer(hs);

    // ピンチ (ヒステリシス付き)
    if (!hs.pinching && pinchDist < PINCH_ON) startPinch(hs);
    else if (hs.pinching && pinchDist > PINCH_OFF) endPinch(hs);

    if (hs.pinching) {
      if (hs.pinchMode === 'pull') {
        const g = worldToGrid(_w.addVectors(hs.pinchPos, hs.grabOffset));
        pullBetween(hs.lastG, g);
        hs.lastG.copy(g);
      } else {
        movers.push(hs);
      }
      rememberJoints(hs);
      continue;
    }

    if (state.mode === 'paint') {
      // 塗る: 人差し指の先で。指を閉じかけている間は止める
      const allowed = pinchDist > PRE_PINCH;
      const r = worldLenToGrid(state.brushR);
      const s = field.sample(worldToGrid(hs.tip, _w));
      if (allowed && s < r * 0.6) {
        if (!hs.touching) { pushUndo(); hs.touching = true; }
        applyTool(state.mode, hs.tip, 0.35);
      } else if (s > r || !allowed) {
        hs.touching = false;
      }
      showCursor(hs.cursor, hs.tip, state.brushR, state.color, allowed ? (hs.touching ? 0.4 : 0.18) : 0.06);
    } else if (state.mode === 'touch') {
      handSculpt(hs, dt, t);
    }
    rememberJoints(hs);
  }
  for (const hs of hands) updateHandVisuals(hs);
  updateObjectManip(movers);
}

// ================= XR: パネル =================
const bw = 0.056, bh = 0.04, gap = 0.006;
const col = (i) => (i - 1.5) * (bw + gap);
const row = (j) => -j * (bh + gap);
const PANEL_W = 4 * bw + 3 * gap;
const swatchW = (PANEL_W - 5 * gap) / 6, shh = 0.03; // 色見本
const panel = new ButtonPanel([
  ...['touch', 'paint'].map((m, i) => ({
    id: m, label: m === 'touch' ? 'さわる (なでる・こねる)' : MODES[m].label,
    x: (col(i * 2) + col(i * 2 + 1)) / 2, y: row(0), w: 2 * bw + gap, h: bh, color: MODES[m].color,
    onPress: () => setMode(m), isActive: () => state.mode === m,
  })),
  { id: 'smaller', label: '− 小', x: col(0), y: row(1), w: bw, h: bh, onPress: () => setBrush(state.brushR - 0.003) },
  { id: 'size', label: () => `${Math.round(state.brushR * 1000)}mm`, x: col(1), y: row(1), w: bw, h: bh },
  { id: 'bigger', label: '+ 大', x: col(2), y: row(1), w: bw, h: bh, onPress: () => setBrush(state.brushR + 0.003) },
  { id: 'undo', label: '↶ 戻す', x: col(3), y: row(1), w: bw, h: bh, onPress: undo },
  { id: 'recenter', label: '手元へ', x: col(0), y: row(2), w: bw, h: bh, onPress: recenter },
  { id: 'reset', label: 'リセット', x: col(1), y: row(2), w: bw, h: bh, onPress: resetClay },
  ...PALETTE.map((c, i) => ({
    id: `color${i}`, label: '', swatch: true, color: c,
    x: -PANEL_W / 2 + swatchW / 2 + (i % 6) * (swatchW + gap), y: row(3) + bh / 2 - shh / 2 - Math.floor(i / 6) * (shh + gap),
    w: swatchW, h: shh, onPress: () => setColor(c), isActive: () => state.color === c,
  })),
], {
  title: ['さわる: 手のひらで軽く触れる=なでる / 深く押す=こねる', '塗る: 人差し指の先で (下の色を使う)', 'つまむ: 引っ張る / 空中: 移動・両手で回転'],
});
panel.group.position.set(-0.3, 0.02, 0.08);
panel.group.rotation.set(-0.3, 0.6, 0, 'YXZ');
panel.group.visible = false;
workspace.add(panel.group);

// ================= 粘土の性質 (スライダー) =================
const pct = (v) => `${Math.round(v * 100)}%`;
const PARAMS = [
  { id: 'push', label: '柔らかさ', hint: '押したとき粘土が手に付いてくる割合。上げると柔らかく、下げると硬い',
    min: 0.05, max: 0.9, get: () => VISC.push, set: (v) => { VISC.push = v; }, format: pct },
  { id: 'pressure', label: '沈む速さ', hint: '押さえ続けたとき粘土がゆっくり沈む速さ',
    min: 0, max: 4, get: () => LIMIT.pressure, set: (v) => { LIMIT.pressure = v; }, format: (v) => v.toFixed(1) },
  { id: 'depth', label: '手が止まる深さ', hint: '見えている手がめり込める深さ。浅いほど硬く感じる',
    min: 0.003, max: 0.04, get: () => LIMIT.depth, set: (v) => { LIMIT.depth = v; }, format: (v) => `${Math.round(v * 1000)}mm` },
  { id: 'stick', label: 'くっつき', hint: '手を離すとき粘土が引っ張られる強さ',
    min: 0, max: 0.8, get: () => VISC.stick, set: (v) => { VISC.stick = v; }, format: pct },
  { id: 'slide', label: '引きずり', hint: '表面をなでたとき粘土が横に付いてくる割合',
    min: 0.05, max: 0.9, get: () => VISC.slide, set: (v) => { VISC.slide = v; }, format: pct },
  { id: 'relax', label: 'なめらかさ', hint: '手が触れた所を毎フレームならす強さ',
    min: 0, max: 0.5, get: () => field.relax, set: (v) => { field.relax = v; }, format: (v) => `${Math.round(v * 200)}%` },
];
PARAMS.push({ id: 'kneadFrom', label: 'こねる開始', hint: '手を押し込んだ深さがこれより浅いとなでるだけ。深いほどこねる。小さくすると、軽く押しただけでこねる',
  min: 0.002, max: 0.02, get: () => TOUCH.kneadFrom, set: (v) => { TOUCH.kneadFrom = v; }, format: (v) => `${(v * 1000).toFixed(0)}mm` });
const PARAM_DEFAULTS = Object.fromEntries(PARAMS.map((p) => [p.id, p.get()]));
const PARAM_KEY = 'clayxr.params.v1';
let saveTimer = 0;
function setParam(p, v) {
  p.set(THREE.MathUtils.clamp(v, p.min, p.max));
  syncParamInputs();
  clearTimeout(saveTimer);
  saveTimer = setTimeout(saveParams, 300);
}
function saveParams() {
  try { localStorage.setItem(PARAM_KEY, JSON.stringify(Object.fromEntries(PARAMS.map((p) => [p.id, p.get()])))); } catch { /* 保存できなくても動く */ }
}
function loadParams() {
  try {
    const saved = JSON.parse(localStorage.getItem(PARAM_KEY) || '{}');
    for (const p of PARAMS) if (typeof saved[p.id] === 'number') p.set(THREE.MathUtils.clamp(saved[p.id], p.min, p.max));
  } catch { /* 初期値のまま */ }
}
function resetParams() {
  for (const p of PARAMS) p.set(PARAM_DEFAULTS[p.id]);
  syncParamInputs();
  saveParams();
}

// XR: 粘土の右側に浮かぶスライダーの板
const sw = 0.2, sh = 0.04, sgap = 0.006;
const propPanel = new ButtonPanel([
  ...PARAMS.map((p, j) => ({
    id: p.id, type: 'slider', label: p.label, x: 0, y: -j * (sh + sgap), w: sw, h: sh,
    min: p.min, max: p.max, get: p.get, set: (v) => setParam(p, v), format: p.format,
  })),
  { id: 'paramReset', label: '初期値に戻す', x: 0, y: -PARAMS.length * (sh + sgap), w: sw, h: bh, onPress: resetParams },
], { title: ['粘土の性質 (指でなぞって調整)'] });
propPanel.group.position.set(0.3, 0.1, 0.08);
propPanel.group.rotation.set(-0.3, -0.6, 0, 'YXZ');
propPanel.group.visible = false;
workspace.add(propPanel.group);

// 頭の前 (少し下) に作業台と粘土を持ってくる
// XR 中、camera には直前に描いたフレームの頭の姿勢が入っている
function recenter() {
  recenterFrom(camera.position, camera.quaternion);
}
const RECENTER = { forward: 0.45, drop: 0 }; // drop = 0: 粘土の中心が目と同じ高さ // m。粘土の中心の位置 (目から前へ・下へ)
function recenterFrom(headPos, headQuat) {
  const head = new THREE.Vector3().copy(headPos);
  const fwd = new THREE.Vector3(0, 0, -1).applyQuaternion(headQuat);
  fwd.y = 0;
  if (fwd.lengthSq() < 1e-4) fwd.set(0, 0, -1);
  fwd.normalize();
  // 頭 (目) の高さが基準: 水平の視線方向に FORWARD、目から DROP だけ下に置く
  workspace.position.copy(head).addScaledVector(fwd, RECENTER.forward).add(new THREE.Vector3(0, -RECENTER.drop, 0));
  workspace.rotation.set(0, Math.atan2(-fwd.x, -fwd.z), 0);
  workspace.updateMatrixWorld();
  clay.position.copy(workspace.position);
  clay.quaternion.copy(workspace.quaternion);
  clay.updateMatrixWorld();
  manipDirty = true;
}

// ================= XR セッション =================
let xrMode = null;
let savedCam = null;
let recenterPending = false;
let xrStartT = 0;

// ---- 状態表示 (画面の UI と、XR 中は目の前の文字板) ----
const VERSION = '0.6.6';
const logLines = [];
function log(msg) {
  const line = `${new Date().toLocaleTimeString()} ${msg}`;
  console.log('[clay]', msg);
  logLines.push(line);
  if (logLines.length > 6) logLines.shift();
  setXRMessage(msg);
  hud.draw();
  hud.until = performance.now() / 1000 + 8;
}
const hud = (() => {
  const canvas = document.createElement('canvas');
  canvas.width = 1024; canvas.height = 320;
  const tex = new THREE.CanvasTexture(canvas);
  tex.colorSpace = THREE.SRGBColorSpace;
  const mesh = new THREE.Mesh(
    new THREE.PlaneGeometry(0.5, 0.156),
    new THREE.MeshBasicMaterial({ map: tex, transparent: true, depthTest: false }),
  );
  mesh.renderOrder = 100;
  mesh.visible = false;
  scene.add(mesh);
  return {
    mesh, until: 0,
    draw() {
      const ctx = canvas.getContext('2d');
      ctx.clearRect(0, 0, 1024, 320);
      ctx.fillStyle = 'rgba(15,17,21,0.85)';
      ctx.beginPath(); ctx.roundRect(0, 0, 1024, 320, 24); ctx.fill();
      ctx.fillStyle = '#e8eaee';
      ctx.font = '34px system-ui, sans-serif';
      ctx.textBaseline = 'top';
      ctx.fillText(`めっちゃ美唄 Clay XR v${VERSION}`, 24, 16);
      logLines.forEach((l, i) => ctx.fillText(l, 24, 64 + i * 42));
      tex.needsUpdate = true;
    },
    // 頭の 0.8m 前、少し下に置く
    follow() {
      const now = performance.now() / 1000;
      mesh.visible = renderer.xr.isPresenting && now < this.until;
      if (!mesh.visible) return;
      const fwd = new THREE.Vector3(0, 0, -1).applyQuaternion(camera.quaternion);
      mesh.position.copy(camera.position).addScaledVector(fwd, 0.8).add(new THREE.Vector3(0, -0.12, 0));
      mesh.quaternion.copy(camera.quaternion);
    },
  };
})();
addEventListener('error', (e) => log(`エラー: ${e.message}`));
addEventListener('unhandledrejection', (e) => log(`エラー: ${e.reason?.message || e.reason}`));

function applyEnvironment() {
  const ar = xrMode === 'immersive-ar';
  scene.background = ar ? null : BG;
  floor.visible = !ar;
  for (const hs of hands) { hs.offset.set(0, 0, 0); hs.s = 0; } // 手の見え方は updateHandVisuals で毎フレーム決める
  panel.group.visible = !!xrMode;
  propPanel.group.visible = !!xrMode;
  bounds.visible = !xrMode;
  desktopCursor.visible = false;
  post.onXRChange();
}

async function startXR(mode) {
  if (renderer.xr.isPresenting) return;
  try {
    log(`${mode} を要求中…`);
    const session = await navigator.xr.requestSession(mode, { optionalFeatures: ['local-floor', 'hand-tracking'] });
    const feats = session.enabledFeatures;
    log(`開始: 機能 = ${feats ? feats.join(', ') || 'なし' : '不明'}`);
    renderer.xr.setReferenceSpaceType(feats && !feats.includes('local-floor') ? 'local' : 'local-floor');
    savedCam = { pos: camera.position.clone(), quat: camera.quaternion.clone(), target: controls.target.clone() };
    xrMode = mode;
    if (state.mode === 'pull') setMode('touch');
    session.addEventListener('end', () => {
      log('XR を終了しました');
      xrMode = null;
      applyEnvironment();
      for (const hs of hands) { endPinch(hs); hs.cursor.visible = false; hs.uiPinch = null; hideUiPointer(hs); }
      if (savedCam) {
        camera.position.copy(savedCam.pos);
        camera.quaternion.copy(savedCam.quat);
        controls.target.copy(savedCam.target);
        controls.update();
      }
    });
    await renderer.xr.setSession(session);
    applyEnvironment();
    recenterPending = true; // 頭の位置が取れたフレームで手元に配置する
    xrStartT = performance.now() / 1000;
    for (const hs of hands) hs.reported = false;
    log('頭の位置を待っています…');
  } catch (err) {
    log(`XR を開始できませんでした: ${err.name}: ${err.message}`);
  }
}

// 頭の姿勢が取れた最初のフレームで、目の前に粘土とパネルを置く
const _hp = new THREE.Vector3(), _hq = new THREE.Quaternion();
function tryRecenterFromFrame(frame) {
  const ref = renderer.xr.getReferenceSpace();
  const pose = frame && ref ? frame.getViewerPose(ref) : null;
  if (!pose) {
    if (performance.now() / 1000 - xrStartT > 5) {
      log('頭の位置が取れません。初期位置に置きます');
      recenterPending = false;
    }
    return;
  }
  const { position: p, orientation: o } = pose.transform;
  _hp.set(p.x, p.y, p.z);
  _hq.set(o.x, o.y, o.z, o.w);
  recenterFrom(_hp, _hq);
  recenterPending = false;
  const w = clay.position;
  log(`配置: 頭 y=${p.y.toFixed(2)}m → 粘土 (${w.x.toFixed(2)}, ${w.y.toFixed(2)}, ${w.z.toFixed(2)})`);
}

// ================= 画面 UI =================
const ui = {
  modes: document.getElementById('modes'),
  size: document.getElementById('size'),
  sizeVal: document.getElementById('sizeVal'),
  xrmsg: document.getElementById('xrmsg'),
};
function setXRMessage(t) { ui.xrmsg.textContent = t; }

for (const [m, def] of Object.entries(MODES)) {
  if (def.hidden) continue;
  const b = document.createElement('button');
  b.dataset.mode = m;
  b.textContent = def.label;
  b.style.setProperty('--accent', def.color);
  b.onclick = () => setMode(m);
  ui.modes.appendChild(b);
}
function setMode(m) {
  state.mode = m;
  for (const b of ui.modes.children) b.classList.toggle('active', b.dataset.mode === m);
}

// 画面: 色見本と、好きな色を選ぶ入力
const paletteEl = document.getElementById('palette');
for (const c of PALETTE) {
  const b = document.createElement('button');
  b.className = 'swatch';
  b.dataset.color = c;
  b.style.background = c;
  b.title = c;
  b.onclick = () => setColor(c);
  paletteEl.appendChild(b);
}
const colorPick = document.getElementById('colorPick');
colorPick.oninput = () => setColor(colorPick.value);
function setColor(c) {
  state.color = c.toLowerCase();
  for (const b of paletteEl.children) b.classList.toggle('active', b.dataset.color === state.color);
  colorPick.value = state.color;
  // 色を選んだら「塗る」にする
  if (state.mode !== 'paint') setMode('paint');
}
function setBrush(r) {
  state.brushR = THREE.MathUtils.clamp(r, 0.006, 0.04);
  ui.size.value = Math.round(state.brushR * 1000);
  ui.sizeVal.textContent = `${Math.round(state.brushR * 1000)}mm`;
}
ui.size.oninput = () => setBrush(ui.size.value / 1000);
document.getElementById('undo').onclick = undo;
document.getElementById('reset').onclick = resetClay;
document.getElementById('export').onclick = exportGLB;
document.getElementById('vr').onclick = () => startXR('immersive-vr');
document.getElementById('ar').onclick = () => startXR('immersive-ar');
setMode(state.mode);
setBrush(state.brushR);
for (const b of paletteEl.children) b.classList.toggle('active', b.dataset.color === state.color);
colorPick.value = state.color;
document.querySelector('#ui .fold').onclick = () => document.getElementById('ui').classList.toggle('collapsed');

// 画面: 粘土の性質のスライダー
const paramInputs = new Map();
for (const p of PARAMS) {
  const row = document.createElement('label');
  row.className = 'param';
  row.title = p.hint;
  row.innerHTML = `<span class="pname">${p.label}</span><input type="range"><span class="pval"></span>`;
  const input = row.querySelector('input');
  input.min = p.min; input.max = p.max; input.step = (p.max - p.min) / 200;
  input.oninput = () => setParam(p, parseFloat(input.value));
  document.getElementById('params').appendChild(row);
  paramInputs.set(p.id, { input, val: row.querySelector('.pval') });
}
function syncParamInputs() {
  for (const p of PARAMS) {
    const el = paramInputs.get(p.id);
    if (!el) continue;
    const v = p.get();
    if (document.activeElement !== el.input) el.input.value = v;
    el.val.textContent = p.format(v);
  }
}
document.getElementById('paramReset').onclick = resetParams;
loadParams();
syncParamInputs();

addEventListener('keydown', (e) => {
  if (e.target.closest?.('input, textarea, select')) return; // 名前などを入力中
  if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'z') { e.preventDefault(); undo(); return; }
  const keys = { 1: 'touch', 2: 'pull', 3: 'paint' };
  if (keys[e.key]) setMode(keys[e.key]);
  if (e.key === '[') setBrush(state.brushR - 0.002);
  if (e.key === ']') setBrush(state.brushR + 0.002);
});

async function checkXR() {
  const vrBtn = document.getElementById('vr'), arBtn = document.getElementById('ar');
  if (!navigator.xr) {
    setXRMessage(window.isSecureContext
      ? 'このブラウザは WebXR に対応していません (デスクトップ操作のみ)。'
      : 'WebXR には HTTPS か localhost が必要です。');
    vrBtn.disabled = arBtn.disabled = true;
    return;
  }
  const [vr, ar] = await Promise.all([
    navigator.xr.isSessionSupported('immersive-vr').catch(() => false),
    navigator.xr.isSessionSupported('immersive-ar').catch(() => false),
  ]);
  vrBtn.disabled = !vr;
  arBtn.disabled = !ar;
  if (!vr && !ar) setXRMessage('XR デバイスが見つかりません (デスクトップ操作のみ)。');
  else setXRMessage(`対応: VR ${vr ? '○' : '×'} / パススルー ${ar ? '○' : '×'}`);
}
checkXR();

// 投稿するモデルの体積 (m³)。作ったモデルは、この体積になるように大きさをそろえて投稿する。
// 初期の立方体 (一辺 14cm、角を丸めたもの) がほぼ等倍になる値
const POST_VOLUME = 0.0027;
const POST_FACTOR = [0.2, 5]; // 補正の倍率の範囲 (小さすぎる・大きすぎる粘土でも極端にならないように)

// 閉じたメッシュの体積 (符号付きの四面体の合計)
function meshVolume(geo) {
  const pos = geo.attributes.position.array, idx = geo.index.array;
  let v = 0;
  for (let i = 0; i < idx.length; i += 3) {
    const a = idx[i] * 3, b = idx[i + 1] * 3, c = idx[i + 2] * 3;
    v += pos[a] * (pos[b + 1] * pos[c + 2] - pos[b + 2] * pos[c + 1])
      - pos[a + 1] * (pos[b] * pos[c + 2] - pos[b + 2] * pos[c])
      + pos[a + 2] * (pos[b] * pos[c + 1] - pos[b + 1] * pos[c]);
  }
  return Math.abs(v) / 6;
}

// 粘土を 1 つのメッシュ (頂点カラー付き) の GLB にする。
// 体積が POST_VOLUME になるように大きさをそろえる (手で粘土を拡大・縮小していても、作った形の量が同じなら同じ大きさになる)。
// 原点は底面の中心。配置 (Transform) はこの点の位置と粘土の回転で、scale は 1
async function exportClay() {
  clay.updateMatrixWorld();
  const geos = [];
  for (const m of chunkMeshes.values()) if (m.visible && m.geometry.index) geos.push(m.geometry);
  if (!geos.length) throw new Error('粘土がありません');
  const merged = mergeGeometries(geos, false);
  merged.computeBoundingBox();
  const bb = merged.boundingBox;
  const cx = (bb.min.x + bb.max.x) / 2, cz = (bb.min.z + bb.max.z) / 2, by = bb.min.y;
  merged.translate(-cx, -by, -cz);
  // 大きさをそろえる (底面の中心を動かさずに拡大・縮小する)
  const rawVolume = meshVolume(merged);
  const factor = rawVolume > 1e-9
    ? THREE.MathUtils.clamp(Math.cbrt(POST_VOLUME / rawVolume), POST_FACTOR[0], POST_FACTOR[1]) : 1;
  merged.scale(factor, factor, factor);
  const mesh = new THREE.Mesh(merged, new THREE.MeshStandardMaterial({ color: 0xffffff, vertexColors: true, roughness: 0.78, metalness: 0 }));
  mesh.name = 'clay';
  const bytes = await new GLTFExporter().parseAsync(mesh, { binary: true });
  merged.dispose();
  mesh.material.dispose();
  return {
    bytes,
    anchor: clay.localToWorld(new THREE.Vector3(cx, by, cz)),
    quaternion: clay.quaternion.clone(),
    scale: 1,
    factor,                       // 大きさの補正の倍率
    volume: rawVolume * factor ** 3, // 投稿するモデルの体積 (m³)
  };
}

function exportGLB() {
  exportClay().then(({ bytes }) => {
    const url = URL.createObjectURL(new Blob([bytes], { type: 'model/gltf-binary' }));
    const a = Object.assign(document.createElement('a'), { href: url, download: 'clay.glb' });
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }, (err) => alert(`書き出しに失敗しました: ${err.message || err}`));
}

// ================= 投稿 (めっちゃ美唄) =================
const post = initPost({
  renderer, workspace, ButtonPanel,
  exportClay,
  snapshot: () => field.snapshot(),
  restore: restoreSnapshot,
  newClay: resetClay,
  log,
  xrMode: () => xrMode,
});

addEventListener('resize', () => {
  camera.aspect = innerWidth / innerHeight;
  camera.updateProjectionMatrix();
  renderer.setSize(innerWidth, innerHeight);
});

// ================= ループ =================
let lastT = 0;
let lastErr = '';
renderer.setAnimationLoop((tms, frame) => {
  const t = tms / 1000;
  const dt = lastT ? Math.min(0.05, t - lastT) : 1 / 60;
  lastT = t;
  try {
    if (renderer.xr.isPresenting) {
      if (recenterPending) tryRecenterFromFrame(frame);
      for (const hs of hands) {
        if (hs.connected && !hs.reported) { hs.reported = true; log(`手を認識: ${hs.handedness}`); }
      }
      updateXR(dt, t);
      panel.refresh();
      propPanel.refresh();
    } else {
      controls.update();
    }
    post.update();
    remeshDirty();
  } catch (err) {
    const msg = `${err.name}: ${err.message}`;
    if (msg !== lastErr) { lastErr = msg; log(`エラー: ${msg}`); console.error(err); }
  }
  renderer.render(scene, camera);
  hud.follow();
});
document.getElementById('ver').textContent = `v${VERSION}`;

// デバッグ用
window.__clay = { field, clay, state, setMode, setBrush, setColor, undo, applyTool, chunkMeshes, camera, controls, hands, updateXR, panel, workspace, undoStack, recenterFrom, tryRecenterFromFrame, log, hud, LIMIT, VISC, PARAMS, propPanel, exportClay, post };
