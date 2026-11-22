using Seeker.Ar;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace Seeker.App
{
    /// <summary>
    /// マーカーを読んで共有マップの原点を合わせ、マップ座標のキューブを置く。
    /// 2台で同じ場所にキューブが見えれば原点合わせは成功。
    /// </summary>
    public sealed class SeekerArController : MonoBehaviour
    {
        // マップ座標 (右手系・Y-up・m)。原点(マーカー中心)の上に 10cm キューブを置く
        private static readonly MapTransform CubeInMap = new MapTransform(new Vector3(0f, 0.05f, 0f), Quaternion.identity, Vector3.one * 0.1f);

        [SerializeField] private ARTrackedImageManager imageManager;
        [SerializeField] private Transform cube;

        private readonly CoordinateAdapter adapter = new CoordinateAdapter();
        private Text statusLabel;
        private Button rescanButton;
        // 合わせた後もマーカーは Updated を送り続ける。原点が動き続けないよう、合わせたら再スキャンまで無視する
        private bool isScanning = true;

        private void Awake()
        {
            BuildUi();
            cube.gameObject.SetActive(false);
        }

        private void OnEnable() => imageManager.trackablesChanged.AddListener(OnTrackablesChanged);

        private void OnDisable() => imageManager.trackablesChanged.RemoveListener(OnTrackablesChanged);

        private void OnTrackablesChanged(ARTrackablesChangedEventArgs<ARTrackedImage> args)
        {
            if (!isScanning) return;
            foreach (var image in args.added) TryCalibrate(image);
            foreach (var image in args.updated) TryCalibrate(image);
        }

        private void TryCalibrate(ARTrackedImage image)
        {
            // Limited はカメラ外での推定のみで姿勢が古いので、原点合わせには使わない
            if (image.trackingState != TrackingState.Tracking) return;
            adapter.Calibrate(new Pose(image.transform.position, image.transform.rotation));
            var local = adapter.MapToLocal(CubeInMap);
            cube.SetPositionAndRotation(local.position, local.rotation);
            cube.localScale = local.scale;
            cube.gameObject.SetActive(true);
            isScanning = false;
            rescanButton.gameObject.SetActive(true);
            statusLabel.text = "マーカーで原点を合わせました";
        }

        private void StartRescan()
        {
            adapter.Reset();
            cube.gameObject.SetActive(false);
            isScanning = true;
            rescanButton.gameObject.SetActive(false);
            statusLabel.text = "マーカーを映してください";
        }

        private void BuildUi()
        {
            var eventSystem = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            eventSystem.transform.SetParent(transform, false);

            var canvasObject = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);
            canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);

            statusLabel = CreateText(canvasObject.transform, "マーカーを映してください", new Vector2(0.5f, 1f), new Vector2(0f, -120f), new Vector2(980f, 120f));
            rescanButton = CreateButton(canvasObject.transform, "原点を再スキャン", new Vector2(0.5f, 0f), new Vector2(0f, 160f), new Vector2(520f, 130f));
            rescanButton.onClick.AddListener(StartRescan);
            rescanButton.gameObject.SetActive(false);
        }

        private static Font UiFont => Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        private static Text CreateText(Transform parent, string content, Vector2 anchor, Vector2 position, Vector2 size)
        {
            var go = new GameObject("Status", typeof(RectTransform), typeof(Image), typeof(Text));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.6f);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = anchor;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            var text = go.GetComponent<Text>();
            text.font = UiFont;
            text.fontSize = 44;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.text = content;
            return text;
        }

        private static Button CreateButton(Transform parent, string label, Vector2 anchor, Vector2 position, Vector2 size)
        {
            var go = new GameObject("RescanButton", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = anchor;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            var labelObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
            labelObject.transform.SetParent(go.transform, false);
            var labelRect = (RectTransform)labelObject.transform;
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;
            var text = labelObject.GetComponent<Text>();
            text.font = UiFont;
            text.fontSize = 44;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.black;
            text.text = label;
            return go.GetComponent<Button>();
        }
    }
}
