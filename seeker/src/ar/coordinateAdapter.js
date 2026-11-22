import * as THREE from 'three';
import { matrixToTransform, transformToMatrix } from './transform.js';

// image-tracking の image space は「画像の法線 = +Z」。共有マップは Y-up なので、
// 床に置いたマーカーの法線(+Z)をマップの +Y に合わせるため、X軸まわりに +90° 回す。
// p_marker = MARKER_FROM_MAP * p_map
const MARKER_FROM_MAP = new THREE.Matrix4().makeRotationX(Math.PI / 2);

/**
 * 端末ローカル座標 ⇔ 共有マップ座標の変換。
 * マーカー中心がマップ原点。マーカーは床置き（法線が上向き）を前提にする。
 */
export class CoordinateAdapter {
  #localFromMap = null;
  #mapFromLocal = null;

  get isCalibrated() {
    return this.#localFromMap !== null;
  }

  /** @param markerPoseInLocal ローカル参照空間で観測したマーカー中心の Transform */
  calibrate(markerPoseInLocal) {
    this.#localFromMap = transformToMatrix(markerPoseInLocal).multiply(MARKER_FROM_MAP);
    this.#mapFromLocal = this.#localFromMap.clone().invert();
  }

  reset() {
    this.#localFromMap = null;
    this.#mapFromLocal = null;
  }

  /** マップ空間のオブジェクトをローカルに置くための行列（three.js の mapRoot 用） */
  getLocalFromMapMatrix() {
    this.#assertCalibrated();
    return this.#localFromMap.clone();
  }

  localToMap(transform) {
    this.#assertCalibrated();
    return matrixToTransform(this.#mapFromLocal.clone().multiply(transformToMatrix(transform)));
  }

  mapToLocal(transform) {
    this.#assertCalibrated();
    return matrixToTransform(this.#localFromMap.clone().multiply(transformToMatrix(transform)));
  }

  #assertCalibrated() {
    if (!this.isCalibrated) throw new Error('CoordinateAdapter is not calibrated: scan the marker first');
  }
}
