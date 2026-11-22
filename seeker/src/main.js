import * as THREE from 'three';
import { CoordinateAdapter } from './ar/coordinateAdapter.js';
import { calibrateFromFrame, createTrackedImages } from './ar/markerTracker.js';

const startButton = document.getElementById('start');
const statusLabel = document.getElementById('status');
const overlay = document.getElementById('overlay');
const rescanButton = document.getElementById('rescan');

let renderer;
let reticle;
let hitTestSource = null;
let isHitTestRequested = false;

const coordinateAdapter = new CoordinateAdapter();
// 再スキャン待ちの間だけ true。原点がブレ続けないよう、合わせたらすぐ false に戻す
let isScanningMarker = true;
// マップ座標で配置するオブジェクトの親。キャリブレーション後に行列を流し込む
let mapRoot;

// iOS Safari など WebXR 未対応の端末はここで弾く（iPhone対応は別Issue）
async function isArSupported() {
  if (!navigator.xr) return false;
  try {
    return await navigator.xr.isSessionSupported('immersive-ar');
  } catch (error) {
    console.error('isSessionSupported failed', error);
    return false;
  }
}

function createScene() {
  const scene = new THREE.Scene();
  const camera = new THREE.PerspectiveCamera(70, window.innerWidth / window.innerHeight, 0.01, 20);

  renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
  renderer.setPixelRatio(window.devicePixelRatio);
  renderer.setSize(window.innerWidth, window.innerHeight);
  renderer.xr.enabled = true;
  // three のデフォルトは 'local-floor' だが、requiredFeatures に入れていないと
  // requestReferenceSpace が失敗する。hit-test には 'local' で足りる
  renderer.xr.setReferenceSpaceType('local');
  document.body.appendChild(renderer.domElement);

  // hit-test の姿勢行列を毎フレーム直接流し込むので自動更新は切る
  reticle = new THREE.Mesh(
    new THREE.RingGeometry(0.07, 0.1, 32).rotateX(-Math.PI / 2),
    new THREE.MeshBasicMaterial({ color: 0x00ff99 }),
  );
  reticle.matrixAutoUpdate = false;
  reticle.visible = false;
  scene.add(reticle);

  // 完了条件の確認用: マップ原点(QR中心)の上に置く 10cm キューブ。2台で同じ場所に見えるはず
  mapRoot = new THREE.Group();
  mapRoot.matrixAutoUpdate = false;
  mapRoot.visible = false;
  const cube = new THREE.Mesh(
    new THREE.BoxGeometry(0.1, 0.1, 0.1),
    new THREE.MeshBasicMaterial({ color: 0xff3366 }),
  );
  cube.position.set(0, 0.05, 0);
  mapRoot.add(cube);
  scene.add(mapRoot);

  renderer.setAnimationLoop((_time, frame) => render(frame));
  return { scene, camera };
}

function updateMarkerCalibration(frame) {
  if (!isScanningMarker) return;
  if (!calibrateFromFrame(frame, renderer.xr.getReferenceSpace(), coordinateAdapter)) return;
  isScanningMarker = false;
  mapRoot.matrix.copy(coordinateAdapter.getLocalFromMapMatrix());
  mapRoot.visible = true;
  rescanButton.hidden = false;
  statusLabel.textContent = 'マーカーで原点を合わせました';
}

function startRescan() {
  coordinateAdapter.reset();
  mapRoot.visible = false;
  isScanningMarker = true;
  rescanButton.hidden = true;
  statusLabel.textContent = 'マーカーを映してください';
}

function render(frame) {
  if (frame) {
    updateMarkerCalibration(frame);
    updateReticle(frame);
  }
  renderer.render(scene, camera);
}

async function requestHitTestSource(session) {
  const viewerSpace = await session.requestReferenceSpace('viewer');
  hitTestSource = await session.requestHitTestSource({ space: viewerSpace });
  session.addEventListener('end', () => {
    hitTestSource = null;
    isHitTestRequested = false;
    reticle.visible = false;
  });
}

function updateReticle(frame) {
  const session = renderer.xr.getSession();
  if (!isHitTestRequested) {
    isHitTestRequested = true;
    requestHitTestSource(session).catch((error) => {
      console.error('hit-test source request failed', error);
      statusLabel.textContent = '平面検出を開始できませんでした';
    });
  }
  if (!hitTestSource) return;

  const hits = frame.getHitTestResults(hitTestSource);
  if (hits.length === 0) {
    reticle.visible = false;
    if (isScanningMarker) statusLabel.textContent = '端末をゆっくり動かして床や机を映し、マーカーを映してください';
    return;
  }
  const pose = hits[0].getPose(renderer.xr.getReferenceSpace());
  reticle.visible = true;
  reticle.matrix.fromArray(pose.transform.matrix);
  if (isScanningMarker) statusLabel.textContent = '平面を検出しました。マーカーを映してください';
}

async function startAr() {
  let session = null;
  let failureMessage = null;
  try {
    // image-tracking は Chrome のフラグが必要なので optional。無効でも AR 自体は起動する
    const trackedImages = await createTrackedImages();
    session = await navigator.xr.requestSession('immersive-ar', {
      requiredFeatures: ['hit-test'],
      optionalFeatures: ['dom-overlay', 'image-tracking'],
      trackedImages,
      domOverlay: { root: overlay },
    });
    if (!session.enabledFeatures?.includes('image-tracking')) {
      statusLabel.textContent = 'image-tracking が無効です（chrome://flags の WebXR Incubations を有効化）';
    }
    session.addEventListener('end', () => {
      startButton.hidden = false;
      rescanButton.hidden = true;
      // 起動失敗で end() した場合は原因表示を上書きしない
      statusLabel.textContent = failureMessage ?? 'ARを終了しました';
    });
    await renderer.xr.setSession(session);
    startButton.hidden = true;
  } catch (error) {
    console.error('AR session start failed', error);
    failureMessage = `ARを開始できませんでした: ${error.message}`;
    statusLabel.textContent = failureMessage;
    // setSession 等で失敗してもセッションが残ると再タップが「already active」で弾かれる
    if (session) session.end().catch(() => {});
  }
}

const { scene, camera } = createScene();
window.addEventListener('resize', () => {
  camera.aspect = window.innerWidth / window.innerHeight;
  camera.updateProjectionMatrix();
  renderer.setSize(window.innerWidth, window.innerHeight);
});

isArSupported().then((isSupported) => {
  if (!isSupported) {
    statusLabel.textContent = 'この端末・ブラウザはWebXR ARに非対応です（Android Chromeで開いてください）';
    return;
  }
  statusLabel.textContent = 'ARを開始できます';
  startButton.hidden = false;
  startButton.addEventListener('click', startAr);
  rescanButton.addEventListener('click', startRescan);
});
