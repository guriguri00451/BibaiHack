using System.Collections;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.Hands;

namespace Tumamu.Phase0
{
    // フェーズ0（spec.md）の実機検証用。カメラ映像・手の関節・壁の平面が取れるかを
    // 頭に固定した表示と logcat（タグ "[Phase0]"）の両方に出す。本実装が始まったら削除する。
    public class Phase0Diagnostics : MonoBehaviour
    {
        const string LogPrefix = "[Phase0]";
        const string HandTrackingPermission = "android.permission.HAND_TRACKING";
        const string SceneUnderstandingPermission = "android.permission.SCENE_UNDERSTANDING_COARSE";
        const int RequestedWidth = 1920;
        const int RequestedHeight = 1080;
        const int RequestedFps = 30;
        const float ReportIntervalSec = 2f;
        const float PanelDistance = 1.0f;
        const string PreviewMaterialName = "Phase0Preview";

        WebCamTexture _webCamTexture;
        TextMesh _statusText;
        int _updatedFrameCount;
        string _cameraStatus = "camera: waiting permission";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            new GameObject(nameof(Phase0Diagnostics)).AddComponent<Phase0Diagnostics>();
        }

        IEnumerator Start()
        {
            RequestPermissions();
            yield return new WaitUntil(() => Camera.main != null);
            BuildPanels(Camera.main.transform);
            yield return new WaitUntil(() => Permission.HasUserAuthorizedPermission(Permission.Camera));
            StartCamera();
            StartCoroutine(ReportLoop());
        }

        static void RequestPermissions()
        {
            var permissions = new[] { Permission.Camera, HandTrackingPermission, SceneUnderstandingPermission };
            var missing = permissions.Where(p => !Permission.HasUserAuthorizedPermission(p)).ToArray();
            if (missing.Length > 0)
                Permission.RequestUserPermissions(missing);
        }

        void BuildPanels(Transform head)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(quad.GetComponent<Collider>());
            quad.name = "Phase0CameraPreview";
            quad.transform.SetParent(head, false);
            quad.transform.localPosition = new Vector3(0.25f, 0f, PanelDistance);
            quad.transform.localScale = new Vector3(0.32f, 0.18f, 1f);
            // CreatePrimitive の既定マテリアルは Built-in 用で、URP では描画できずピンクになる
            quad.GetComponent<Renderer>().material = new Material(Resources.Load<Material>(PreviewMaterialName));

            var textObject = new GameObject("Phase0Status");
            textObject.transform.SetParent(head, false);
            textObject.transform.localPosition = new Vector3(-0.35f, 0.1f, PanelDistance);
            _statusText = textObject.AddComponent<TextMesh>();
            _statusText.characterSize = 0.005f;
            _statusText.fontSize = 48;
            _statusText.color = Color.white;
        }

        void StartCamera()
        {
            var devices = WebCamTexture.devices;
            foreach (var device in devices)
                Debug.Log($"{LogPrefix} webcam device name={device.name} front={device.isFrontFacing} kind={device.kind}");

            if (devices.Length == 0)
            {
                _cameraStatus = "camera: NO DEVICES";
                Debug.LogError($"{LogPrefix} WebCamTexture.devices is empty");
                return;
            }

            // dumpsys で world-facing RGB が公開 ID 0（auracamerahal/0, 1920x1080）だと確認済み
            var worldCamera = devices[0];
            _webCamTexture = new WebCamTexture(worldCamera.name, RequestedWidth, RequestedHeight, RequestedFps);
            _webCamTexture.Play();
            GameObject.Find("Phase0CameraPreview").GetComponent<Renderer>().material.mainTexture = _webCamTexture;
            _cameraStatus = $"camera: playing {worldCamera.name}";
        }

        void Update()
        {
            if (_webCamTexture != null && _webCamTexture.didUpdateThisFrame)
                _updatedFrameCount++;
        }

        IEnumerator ReportLoop()
        {
            var wait = new WaitForSeconds(ReportIntervalSec);
            while (true)
            {
                var report = BuildReport();
                Debug.Log($"{LogPrefix} {report.Replace("\n", " | ")}");
                if (_statusText != null)
                    _statusText.text = report;
                _updatedFrameCount = 0;
                yield return wait;
            }
        }

        string BuildReport()
        {
            var builder = new StringBuilder();
            builder.AppendLine(DescribeCamera());
            builder.AppendLine(DescribeHands());
            builder.Append(DescribePlanes());
            return builder.ToString();
        }

        string DescribeCamera()
        {
            if (_webCamTexture == null)
                return _cameraStatus;
            var fps = _updatedFrameCount / ReportIntervalSec;
            // 真っ黒なフレームが来ていないかを数値で確かめるため、中心ピクセルの色も出す
            var center = _webCamTexture.GetPixel(_webCamTexture.width / 2, _webCamTexture.height / 2);
            return $"{_cameraStatus} {_webCamTexture.width}x{_webCamTexture.height} {fps:F1}fps center=({center.r:F2},{center.g:F2},{center.b:F2})";
        }

        static string DescribeHands()
        {
            var subsystems = new System.Collections.Generic.List<XRHandSubsystem>();
            SubsystemManager.GetSubsystems(subsystems);
            var handSubsystem = subsystems.FirstOrDefault(s => s.running);
            if (handSubsystem == null)
                return "hands: NO RUNNING SUBSYSTEM";
            return $"hands: L={DescribeIndexTip(handSubsystem.leftHand)} R={DescribeIndexTip(handSubsystem.rightHand)}";
        }

        static string DescribeIndexTip(XRHand hand)
        {
            if (!hand.isTracked)
                return "untracked";
            return hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out var pose)
                ? pose.position.ToString("F2")
                : "tip-missing";
        }

        // spec の「使う壁の選び方」のしきい値（面積・距離）を決めるための生データ。パネルには出さず logcat のみ
        static void LogPlaneDetails(System.Collections.Generic.List<ARPlane> planes)
        {
            var head = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            foreach (var plane in planes)
            {
                var area = plane.size.x * plane.size.y;
                var distance = Vector3.Distance(head, plane.center);
                Debug.Log($"{LogPrefix} plane id={plane.trackableId} align={plane.alignment} class={plane.classifications} " +
                          $"area={area:F2}m2 dist={distance:F2}m state={plane.trackingState} subsumed={(plane.subsumedBy != null)}");
            }
        }

        static string DescribePlanes()
        {
            var planeManager = FindAnyObjectByType<ARPlaneManager>();
            if (planeManager == null)
                return "planes: NO ARPlaneManager";
            // TrackableCollection は IEnumerable を実装していないので LINQ に渡す前に詰め替える
            var planes = new System.Collections.Generic.List<ARPlane>();
            foreach (var plane in planeManager.trackables)
                planes.Add(plane);
            LogPlaneDetails(planes);
            var verticalCount = planes.Count(p => p.alignment == UnityEngine.XR.ARSubsystems.PlaneAlignment.Vertical);
            var classifications = string.Join(",", planes.Select(p => p.classifications.ToString()).Distinct());
            return $"planes: total={planes.Count} vertical={verticalCount} classes=[{classifications}]";
        }
    }
}
