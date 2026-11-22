import { describe, expect, it } from 'vitest';
import { CoordinateAdapter } from './coordinateAdapter.js';
import { identityTransform } from './transform.js';

const HALF_SQRT = Math.SQRT1_2;

// image-tracking の image space は Z が画像の法線。床置きマーカーなら Z が真上を向く
// ＝ X軸まわり -90° 回転した姿勢（マーカーの Z → ローカルの Y）
const FLOOR_MARKER_ROTATION = [-HALF_SQRT, 0, 0, HALF_SQRT];

function floorMarkerAt(position) {
  return { position, rotation: FLOOR_MARKER_ROTATION, scale: [1, 1, 1] };
}

function expectVec(actual, expected) {
  actual.forEach((value, i) => expect(value).toBeCloseTo(expected[i], 5));
}

describe('CoordinateAdapter', () => {
  it('未キャリブレーションでは isCalibrated が false で変換は例外を投げる', () => {
    const adapter = new CoordinateAdapter();
    expect(adapter.isCalibrated).toBe(false);
    expect(() => adapter.localToMap(identityTransform())).toThrow(/calibrat/i);
  });

  it('床置きマーカーの位置がマップ原点になる', () => {
    const adapter = new CoordinateAdapter();
    adapter.calibrate(floorMarkerAt([1, 0, -2]));
    const map = adapter.localToMap({ ...identityTransform(), position: [1, 0, -2] });
    expectVec(map.position, [0, 0, 0]);
  });

  it('床置きマーカーではマップの Y(上) がローカルの Y(上) と一致する', () => {
    const adapter = new CoordinateAdapter();
    adapter.calibrate(floorMarkerAt([1, 0, -2]));
    const map = adapter.localToMap({ ...identityTransform(), position: [1, 0.5, -2] });
    expectVec(map.position, [0, 0.5, 0]);
  });

  it('マップ座標のキューブをローカルへ変換して戻すと元の Transform になる', () => {
    const adapter = new CoordinateAdapter();
    adapter.calibrate(floorMarkerAt([0.3, 0.1, -1.2]));
    const cube = { position: [0.4, 0.05, -0.7], rotation: [0, HALF_SQRT, 0, HALF_SQRT], scale: [1, 2, 3] };
    const roundTrip = adapter.localToMap(adapter.mapToLocal(cube));
    expectVec(roundTrip.position, cube.position);
    expectVec(roundTrip.rotation, cube.rotation);
    expectVec(roundTrip.scale, cube.scale);
  });

  it('変換後の Quaternion は正規化されている', () => {
    const adapter = new CoordinateAdapter();
    adapter.calibrate(floorMarkerAt([0, 0, 0]));
    const { rotation } = adapter.mapToLocal({ ...identityTransform(), rotation: [0, 0.6, 0, 0.8] });
    expect(Math.hypot(...rotation)).toBeCloseTo(1, 6);
  });

  it('reset 後は未キャリブレーションに戻り再度 calibrate できる', () => {
    const adapter = new CoordinateAdapter();
    adapter.calibrate(floorMarkerAt([1, 0, 0]));
    adapter.reset();
    expect(adapter.isCalibrated).toBe(false);
    adapter.calibrate(floorMarkerAt([5, 0, 0]));
    expectVec(adapter.mapToLocal(identityTransform()).position, [5, 0, 0]);
  });

  it('2台の端末が別々のローカル座標で同じマーカーを読んでも同じマップ座標になる', () => {
    // 端末Bのローカル原点は端末Aから Y軸まわり90°回転・(2,0,0)平行移動した位置にある
    const deviceA = new CoordinateAdapter();
    const deviceB = new CoordinateAdapter();
    deviceA.calibrate(floorMarkerAt([1, 0, 0]));
    const yawQuarter = [0, HALF_SQRT, 0, HALF_SQRT];
    // マーカー姿勢 = B系への回転(yaw +90°) × 床置き姿勢
    const markerRotationInB = multiplyQuat(yawQuarter, FLOOR_MARKER_ROTATION);
    deviceB.calibrate({ position: [0, 0, 1], rotation: markerRotationInB, scale: [1, 1, 1] });
    const cubeInMap = { position: [0.2, 0.05, 0.3], rotation: [0, 0, 0, 1], scale: [1, 1, 1] };
    // B 系の点 = yaw(+90°) * (A系の点 - (2,0,0))
    const cubeInA = deviceA.mapToLocal(cubeInMap);
    const [ax, ay, az] = cubeInA.position;
    const expectedInB = [az, ay, -(ax - 2)];
    expectVec(deviceB.mapToLocal(cubeInMap).position, expectedInB);
  });
});

function multiplyQuat([ax, ay, az, aw], [bx, by, bz, bw]) {
  return [
    aw * bx + ax * bw + ay * bz - az * by,
    aw * by - ax * bz + ay * bw + az * bx,
    aw * bz + ax * by - ay * bx + az * bw,
    aw * bw - ax * bx - ay * by - az * bz,
  ];
}
