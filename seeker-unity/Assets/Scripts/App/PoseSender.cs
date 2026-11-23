using Seeker.Ar;
using Seeker.Sync;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace Seeker.App
{
    /// <summary>
    /// ARカメラの pose を共有マップ座標にして SyncedObjectsController 経由で送る。
    /// 送るかどうかは PoseSendPolicy が決める（playing 中・較正済み・トラッキング中のみ、約15Hz）。
    /// </summary>
    public sealed class PoseSender : MonoBehaviour
    {
        private readonly PoseSendPolicy policy = new PoseSendPolicy(ServerClock.SystemNowMs);
        private SyncedObjectsController syncedObjects;
        private CoordinateAdapter adapter;

        /// <summary>トラッキングが切れている間 true。表示の切り替え用。</summary>
        public bool IsTrackingLost { get; private set; }

        public void Initialize(SyncedObjectsController controller, CoordinateAdapter coordinateAdapter)
        {
            syncedObjects = controller;
            adapter = coordinateAdapter;
        }

        private void Update()
        {
            if (syncedObjects == null) return;
            IsTrackingLost = ARSession.state != ARSessionState.SessionTracking;
            var camera = Camera.main;
            if (camera == null) return;

            var isPlaying = syncedObjects.Session.Phase == GamePhase.Playing;
            var isSent = policy.TryCreateMessage(
                adapter.IsCalibrated,
                !IsTrackingLost,
                isPlaying,
                () => adapter.LocalToMap(new Pose(camera.transform.position, camera.transform.rotation)),
                out var json);
            if (isSent) syncedObjects.SendPlayerTransform(json);
        }
    }
}
