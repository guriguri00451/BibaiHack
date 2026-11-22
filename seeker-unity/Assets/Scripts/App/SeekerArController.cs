using Seeker.Ar;
using Seeker.Sync;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace Seeker.App
{
    /// <summary>
    /// マーカーを読んでいる間は共有マップの原点を合わせ続け、読み込んだ3Dマップをその原点に置く。XR Origin は動かさない。
    /// 目印のオブジェクトは ARTrackedImageManager の Tracked Image Prefab がマーカーに追従して出す。
    /// マーカーを見失ったら最後に合わせた原点を維持する。
    /// </summary>
    public sealed class SeekerArController : MonoBehaviour
    {
        [SerializeField] private ARTrackedImageManager imageManager;
        [SerializeField] private SyncedObjectsController syncedObjects;
        [SerializeField] private string mapId = "bibai-museum";
        // 空ならモック(StreamingAssets/mock)を使う。API 実装後に https://host/v1 を入れる
        [SerializeField] private string apiBaseUrl = "";

        private readonly CoordinateAdapter adapter = new CoordinateAdapter();
        private MapPresenter mapPresenter;
        private Text statusLabel;

        private void Awake()
        {
            BuildUi();
            mapPresenter = new MapPresenter(transform);
            // シーンに置いていなければ実行時に足す。接続は StartSync が呼ばれるまでしない
            if (syncedObjects == null) syncedObjects = gameObject.AddComponent<SyncedObjectsController>();
            syncedObjects.Rejected += () => statusLabel.text = "サーバーに接続を拒否されました";
        }

        /// <summary>ゲーム進行側が guest / session を作ったあとに呼び、オブジェクト同期を始める。</summary>
        public void StartSync(SyncConnection connection) => syncedObjects.Configure(adapter, connection);

        private async void Start()
        {
            if (DevConnectionFile.TryRead(out var devConnection)) StartSync(devConnection);
            try
            {
                var definition = await new MapService(apiBaseUrl).FetchAsync(mapId);
                await mapPresenter.LoadAsync(definition);
                // 読み込み前にマーカーを捉えていた場合に備えて、ここでも置く
                mapPresenter.Place(adapter);
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
                statusLabel.text = "マップの読み込みに失敗しました";
            }
        }

        private void OnEnable() => imageManager.trackablesChanged.AddListener(OnTrackablesChanged);

        private void OnDisable() => imageManager.trackablesChanged.RemoveListener(OnTrackablesChanged);

        private void OnTrackablesChanged(ARTrackablesChangedEventArgs<ARTrackedImage> args)
        {
            foreach (var image in args.added) TryCalibrate(image);
            foreach (var image in args.updated) TryCalibrate(image);
        }

        private void TryCalibrate(ARTrackedImage image)
        {
            // Limited はカメラ外での推定のみで姿勢が古いので、原点合わせには使わない。
            // 見失った間は最後の原点をそのまま残す
            if (image.trackingState != TrackingState.Tracking)
            {
                if (adapter.IsCalibrated) statusLabel.text = "マーカーを見失いました(最後の原点を維持)";
                return;
            }
            adapter.Calibrate(new Pose(image.transform.position, image.transform.rotation));
            mapPresenter.Place(adapter);
            statusLabel.text = "マーカーを追従中";
        }

        private void BuildUi()
        {
            var canvasObject = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler));
            canvasObject.transform.SetParent(transform, false);
            canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);

            statusLabel = CreateText(canvasObject.transform, "マーカーを映してください", new Vector2(0.5f, 1f), new Vector2(0f, -120f), new Vector2(980f, 120f));
        }

        private static Font UiFont => Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        private static Text CreateText(Transform parent, string content, Vector2 anchor, Vector2 position, Vector2 size)
        {
            // Image と Text はどちらも Graphic で [DisallowMultipleComponent] のため、同一 GameObject に付けられない。
            // 背景(Image)と文字(Text)を親子に分ける。
            var go = new GameObject("Status", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.6f);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = anchor;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            var textObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(go.transform, false);
            var textRect = (RectTransform)textObject.transform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = textRect.offsetMax = Vector2.zero;
            var text = textObject.GetComponent<Text>();
            text.font = UiFont;
            text.fontSize = 44;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.text = content;
            return text;
        }
    }
}
