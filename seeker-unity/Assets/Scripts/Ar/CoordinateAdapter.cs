using System;
using UnityEngine;

namespace Seeker.Ar
{
    /// <summary>
    /// 端末ローカル（Unity ワールド）座標 ⇔ 共有マップ座標の変換。
    /// マーカー中心がマップ原点。マーカーは床置き（AR Foundation の ARTrackedImage は +Y が画像の法線）を前提にする。
    /// </summary>
    public sealed class CoordinateAdapter
    {
        private Pose markerPose;

        public bool IsCalibrated { get; private set; }

        /// <param name="trackedMarkerPose">ARTrackedImage.transform の pose（Unity ワールド）</param>
        public void Calibrate(Pose trackedMarkerPose)
        {
            // 回転は正規化しておかないと、以降の変換すべてにスケールが混ざる
            markerPose = new Pose(trackedMarkerPose.position, trackedMarkerPose.rotation.Normalized());
            IsCalibrated = true;
        }

        public void Reset() => IsCalibrated = false;

        public MapTransform LocalToMap(LocalTransform local)
        {
            AssertCalibrated();
            var inverseMarker = Quaternion.Inverse(markerPose.rotation);
            var positionInMarker = inverseMarker * (local.position - markerPose.position);
            var rotationInMarker = inverseMarker * local.rotation;
            return new MapTransform(
                FlipHandedness(positionInMarker),
                FlipHandedness(rotationInMarker).Normalized(),
                local.scale);
        }

        public LocalTransform MapToLocal(MapTransform map)
        {
            AssertCalibrated();
            var positionInMarker = FlipHandedness(map.position);
            var rotationInMarker = FlipHandedness(map.rotation);
            return new LocalTransform(
                markerPose.position + markerPose.rotation * positionInMarker,
                (markerPose.rotation * rotationInMarker).Normalized(),
                map.scale);
        }

        // 右手系 ⇔ 左手系は Z 軸の反転。回転は x,y 成分の符号が反転する（軸ベクトルの Z 反転 + 回転方向の反転）
        private static Vector3 FlipHandedness(Vector3 v) => new Vector3(v.x, v.y, -v.z);

        private static Quaternion FlipHandedness(Quaternion q) => new Quaternion(-q.x, -q.y, q.z, q.w);

        private void AssertCalibrated()
        {
            if (!IsCalibrated) throw new InvalidOperationException("CoordinateAdapter is not calibrated: scan the marker first");
        }
    }

    internal static class QuaternionExtensions
    {
        public static Quaternion Normalized(this Quaternion q)
        {
            var magnitude = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return new Quaternion(q.x / magnitude, q.y / magnitude, q.z / magnitude, q.w / magnitude);
        }
    }
}
