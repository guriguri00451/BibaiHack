import * as THREE from 'three';
import { matrixToTransform } from './transform.js';

// 印刷するマーカー画像（public/marker.png、余白込み）の一辺。実物とずれると位置がスケール分ずれる
export const MARKER_WIDTH_METERS = 0.15;
const MARKER_IMAGE_URL = '/marker.png';

/** image-tracking に渡す trackedImages を作る */
export async function createTrackedImages() {
  const response = await fetch(MARKER_IMAGE_URL);
  if (!response.ok) throw new Error(`marker image not found: ${MARKER_IMAGE_URL}`);
  const image = await createImageBitmap(await response.blob());
  return [{ image, widthInMeters: MARKER_WIDTH_METERS }];
}

/**
 * 毎フレーム呼び、マーカーが 'tracked' のときだけ adapter を（再）キャリブレーションする。
 * 'emulated'（画面外で推定のみ）は姿勢が古いので原点合わせには使わない。
 * @returns {boolean} このフレームでキャリブレーションしたか
 */
export function calibrateFromFrame(frame, referenceSpace, adapter) {
  if (typeof frame.getImageTrackingResults !== 'function') return false;
  const result = frame.getImageTrackingResults().find((r) => r.trackingState === 'tracked');
  if (!result) return false;
  const pose = frame.getPose(result.imageSpace, referenceSpace);
  if (!pose) return false;
  adapter.calibrate(matrixToTransform(new THREE.Matrix4().fromArray(pose.transform.matrix)));
  return true;
}
