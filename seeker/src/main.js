import * as THREE from 'three';

const startButton = document.getElementById('start');
const statusLabel = document.getElementById('status');
const overlay = document.getElementById('overlay');

let renderer;
let reticle;
let hitTestSource = null;
let isHitTestRequested = false;

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

  renderer.setAnimationLoop((_time, frame) => render(frame));
  return { scene, camera };
}

function render(frame) {
  if (frame) updateReticle(frame);
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
    statusLabel.textContent = '端末をゆっくり動かして床や机を映してください';
    return;
  }
  const pose = hits[0].getPose(renderer.xr.getReferenceSpace());
  reticle.visible = true;
  reticle.matrix.fromArray(pose.transform.matrix);
  statusLabel.textContent = '平面を検出しました';
}

async function startAr() {
  let session = null;
  let failureMessage = null;
  try {
    session = await navigator.xr.requestSession('immersive-ar', {
      requiredFeatures: ['hit-test'],
      optionalFeatures: ['dom-overlay'],
      domOverlay: { root: overlay },
    });
    session.addEventListener('end', () => {
      startButton.hidden = false;
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
});
