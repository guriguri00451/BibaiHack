using NUnit.Framework;
using UnityEngine;

namespace Seeker.Ar.Tests
{
    public class CoordinateAdapterTests
    {
        private const float Tolerance = 1e-4f;

        private static Pose MarkerOnFloor(Vector3 position, float yawDegrees = 0f) =>
            new Pose(position, Quaternion.Euler(0f, yawDegrees, 0f));

        private static void AssertVector(Vector3 expected, Vector3 actual) =>
            Assert.That(Vector3.Distance(expected, actual), Is.LessThan(Tolerance), $"expected {expected} but was {actual}");

        [Test]
        public void LocalToMap_Throws_WhenNotCalibrated()
        {
            var adapter = new CoordinateAdapter();

            Assert.Throws<System.InvalidOperationException>(() => adapter.LocalToMap(Pose.identity));
        }

        [Test]
        public void LocalToMap_ReturnsOrigin_AtMarkerPosition()
        {
            var adapter = new CoordinateAdapter();
            adapter.Calibrate(MarkerOnFloor(new Vector3(1f, 0f, -2f), 30f));

            var map = adapter.LocalToMap(new Pose(new Vector3(1f, 0f, -2f), Quaternion.identity));

            AssertVector(Vector3.zero, map.position);
        }

        [Test]
        public void LocalToMap_KeepsUpAxis_ForFloorMarker()
        {
            var adapter = new CoordinateAdapter();
            adapter.Calibrate(MarkerOnFloor(new Vector3(1f, 0f, -2f), 30f));

            var map = adapter.LocalToMap(new Pose(new Vector3(1f, 0.5f, -2f), Quaternion.identity));

            AssertVector(new Vector3(0f, 0.5f, 0f), map.position);
        }

        [Test]
        public void MapToLocal_FlipsZ_BecauseMapIsRightHandedAndUnityIsLeftHanded()
        {
            var adapter = new CoordinateAdapter();
            adapter.Calibrate(MarkerOnFloor(Vector3.zero));

            var local = adapter.MapToLocal(new MapTransform(new Vector3(0f, 0f, 1f), Quaternion.identity, Vector3.one));

            AssertVector(new Vector3(0f, 0f, -1f), local.position);
        }

        [Test]
        public void MapToLocal_NegatesYawSign_WhenConvertingHandedness()
        {
            var adapter = new CoordinateAdapter();
            adapter.Calibrate(MarkerOnFloor(Vector3.zero));
            // 右手系で +Y 軸まわり +90° 回すと、+X は -Z を向く。Z を反転した Unity 側では +Z になる
            var rightHandedYaw90 = Quaternion.Euler(0f, 90f, 0f);

            var local = adapter.MapToLocal(new MapTransform(Vector3.zero, rightHandedYaw90, Vector3.one));
            AssertVector(new Vector3(0f, 0f, 1f), local.rotation * Vector3.right);
        }

        [Test]
        public void MapToLocal_ThenLocalToMap_RestoresOriginalTransform()
        {
            var adapter = new CoordinateAdapter();
            adapter.Calibrate(MarkerOnFloor(new Vector3(0.3f, 0.1f, -1.2f), 47f));
            var cube = new MapTransform(new Vector3(0.4f, 0.05f, -0.7f), Quaternion.Euler(10f, 80f, -20f), new Vector3(1f, 2f, 3f));

            var restored = adapter.LocalToMap(adapter.MapToLocal(cube));

            AssertVector(cube.position, restored.position);
            AssertVector(cube.scale, restored.scale);
            Assert.That(Quaternion.Angle(cube.rotation, restored.rotation), Is.LessThan(0.01f));
        }

        [Test]
        public void MapToLocal_ReturnsNormalizedQuaternion()
        {
            var adapter = new CoordinateAdapter();
            adapter.Calibrate(MarkerOnFloor(Vector3.zero, 15f));

            var local = adapter.MapToLocal(new MapTransform(Vector3.zero, new Quaternion(0f, 0.6f, 0f, 0.8f), Vector3.one));

            var q = local.rotation;
            Assert.That(Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w), Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void Calibrate_UsesNewMarkerPose_AfterReset()
        {
            var adapter = new CoordinateAdapter();
            adapter.Calibrate(MarkerOnFloor(new Vector3(1f, 0f, 0f)));
            adapter.Reset();
            Assert.That(adapter.IsCalibrated, Is.False);

            adapter.Calibrate(MarkerOnFloor(new Vector3(5f, 0f, 0f)));

            AssertVector(new Vector3(5f, 0f, 0f), adapter.MapToLocal(MapTransform.Identity).position);
        }

        [Test]
        public void MapToLocal_ShowsSameRealSpot_OnTwoDevicesWithDifferentLocalOrigins()
        {
            // 端末Bのローカル座標は、端末Aから「(2,0,0) だけ平行移動し、Y軸まわりに90°回した」もの
            var deviceA = new CoordinateAdapter();
            var deviceB = new CoordinateAdapter();
            var markerInA = MarkerOnFloor(new Vector3(1f, 0f, 0f), 20f);
            var worldToB = Quaternion.Euler(0f, 90f, 0f);
            var originOfBInA = new Vector3(2f, 0f, 0f);
            deviceA.Calibrate(markerInA);
            deviceB.Calibrate(new Pose(worldToB * (markerInA.position - originOfBInA), worldToB * markerInA.rotation));
            var cube = new MapTransform(new Vector3(0.2f, 0.05f, 0.3f), Quaternion.identity, Vector3.one);

            var cubeInA = deviceA.MapToLocal(cube).position;
            var cubeInB = deviceB.MapToLocal(cube).position;

            AssertVector(worldToB * (cubeInA - originOfBInA), cubeInB);
        }
    }
}
