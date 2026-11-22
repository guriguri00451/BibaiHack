import * as THREE from 'three';

// 仕様 §3.2 の Transform 型。右手系・Y-up・メートル、Quaternion は (x, y, z, w)
export function identityTransform() {
  return { position: [0, 0, 0], rotation: [0, 0, 0, 1], scale: [1, 1, 1] };
}

export function transformToMatrix({ position, rotation, scale }) {
  // 正規化せずに compose すると回転行列にスケールが混ざる
  const quaternion = new THREE.Quaternion(...rotation).normalize();
  return new THREE.Matrix4().compose(new THREE.Vector3(...position), quaternion, new THREE.Vector3(...scale));
}

export function matrixToTransform(matrix) {
  const position = new THREE.Vector3();
  const quaternion = new THREE.Quaternion();
  const scale = new THREE.Vector3();
  matrix.decompose(position, quaternion, scale);
  quaternion.normalize();
  return {
    position: position.toArray(),
    rotation: [quaternion.x, quaternion.y, quaternion.z, quaternion.w],
    scale: scale.toArray(),
  };
}
