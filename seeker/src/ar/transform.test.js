import { describe, expect, it } from 'vitest';
import { identityTransform, matrixToTransform, transformToMatrix } from './transform.js';

describe('transform', () => {
  it('identityTransform は単位 Quaternion とスケール1', () => {
    expect(identityTransform()).toEqual({ position: [0, 0, 0], rotation: [0, 0, 0, 1], scale: [1, 1, 1] });
  });

  it('Transform → Matrix4 → Transform で値が保たれる', () => {
    const original = { position: [1, 2, 3], rotation: [0, Math.SQRT1_2, 0, Math.SQRT1_2], scale: [1, 2, 3] };
    const restored = matrixToTransform(transformToMatrix(original));
    original.position.forEach((v, i) => expect(restored.position[i]).toBeCloseTo(v, 6));
    original.rotation.forEach((v, i) => expect(restored.rotation[i]).toBeCloseTo(v, 6));
    original.scale.forEach((v, i) => expect(restored.scale[i]).toBeCloseTo(v, 6));
  });

  it('正規化されていない Quaternion も行列化時に正規化される', () => {
    const matrix = transformToMatrix({ position: [0, 0, 0], rotation: [0, 0, 0, 2], scale: [1, 1, 1] });
    expect(Math.hypot(...matrixToTransform(matrix).rotation)).toBeCloseTo(1, 6);
  });
});
