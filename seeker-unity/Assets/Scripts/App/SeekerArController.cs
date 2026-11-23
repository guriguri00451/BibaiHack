using Seeker.Ar;
using Seeker.Sync;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace Seeker.App
{
    /// <summary>
    /// マーカーを読んでいる間は共有マップの原点を合わせ続け、読み込んだ3Dマップをその原点に置く。XR Origin は動かさない。
    /// 目印のオブジェクトは ARTrackedImageManager の Tracked Image Prefab がマーカーに追従して出す。
    /// マーカーを見失ったら最後に合わせた原点を維持する。
    /// 置くマップはセッションの mapId（snapshot）に従い、替わったら置き直す。接続情報が無いときだけモックを読む。
    /// </summary>
    public sealed class SeekerArController : MonoBehaviour
    {
        [SerializeField] private ARTrackedImageManager imageManager;
        [SerializeField] private SyncedObjectsController syncedObjects;
        // 接続情報が無い（サーバーに繋がない）ときに StreamingAssets/mock から読むマップ
        [FormerlySerializedAs("mapId")]
        [SerializeField] private string fallbackMapId = "bibai-museum";

        private readonly CoordinateAdapter adapter = new CoordinateAdapter();
        private readonly MapLoadTracker mapLoads = new MapLoadTracker();
        private MapService mapService = new MapService();
        private bool isSyncStarted;
        private MapPresenter mapPresenter;
        private Text statusLabel;
        private Text mapStatusLabel;
        private PhasePanelsView phasePanels;
        private PoseSender poseSender;
        private const float CaptureMessageSeconds = 3f;
        private string statusBeforeTrackingLost;
        private string statusBeforeCapture;
        private string captureMessage;
        private float captureMessageHideAt;
        private bool wasTrackingLost;

        private void Awake()
        {
            BuildUi();
            mapPresenter = new MapPresenter(transform);
            // シーンに置いていなければ実行時に足す。接続は StartSync が呼ばれるまでしない
            if (syncedObjects == null) syncedObjects = gameObject.AddComponent<SyncedObjectsController>();
            poseSender = gameObject.AddComponent<PoseSender>();
            poseSender.Initialize(syncedObjects, adapter);
            var captureInput = gameObject.AddComponent<CaptureInput>();
            captureInput.Initialize(syncedObjects);
            captureInput.Captured += OnCaptured;
            syncedObjects.Rejected += () => statusLabel.text = "サーバーに接続を拒否されました";
            syncedObjects.SessionChanged += RenderPhase;
            RenderPhase();
        }

        /// <summary>
        /// ゲーム進行側が guest / session を作ったあとに呼び、オブジェクト同期を始める。
        /// マップもこの接続先から取り、snapshot が mapId を載せてくるたびに読み込む。
        /// </summary>
        public void StartSync(SyncConnection connection)
        {
            syncedObjects.Configure(adapter, connection);
            isSyncStarted = true;
            mapService = new MapService(connection.RestBaseUrl, connection.AccessToken);
            syncedObjects.MapIdReceived += RequestMap;
        }

        private void OnDestroy()
        {
            if (syncedObjects == null) return;
            syncedObjects.SessionChanged -= RenderPhase;
            syncedObjects.MapIdReceived -= RequestMap;
        }

        // サーバー時刻補正後の残り時間を毎フレーム描く。0 でも勝手に遷移せず phase_changed を待つ
        private void Update()
        {
            phasePanels.RenderTimer(syncedObjects.Session, syncedObjects.Clock);
            RenderTrackingLost();
            RestoreStatusAfterCapture();
        }

        // 演出・スコア HUD は後続タスク。ここでは結果を statusLabel に数秒だけ出す
        private void OnCaptured(CaptureOutcome outcome)
        {
            if (captureMessage == null) statusBeforeCapture = statusLabel.text;
            captureMessage = outcome.result.Message;
            captureMessageHideAt = Time.unscaledTime + CaptureMessageSeconds;
            statusLabel.text = captureMessage;
        }

        private void RestoreStatusAfterCapture()
        {
            if (captureMessage == null || Time.unscaledTime < captureMessageHideAt) return;
            // 他の処理が文言を書き換えていたら上書きしない
            if (statusLabel.text == captureMessage) statusLabel.text = statusBeforeCapture;
            captureMessage = null;
        }

        // トラッキング切れの間だけ statusLabel を上書きし、復帰したら直前の文言に戻す
        private void RenderTrackingLost()
        {
            var isLost = poseSender.IsTrackingLost;
            if (isLost == wasTrackingLost) return;
            wasTrackingLost = isLost;
            if (isLost)
            {
                statusBeforeTrackingLost = statusLabel.text;
                statusLabel.text = "トラッキングが切れています";
            }
            else if (statusLabel.text == "トラッキングが切れています")
            {
                statusLabel.text = statusBeforeTrackingLost;
            }
        }

        private void RenderPhase() => phasePanels.Render(syncedObjects.Session);

        private void Start()
        {
            if (!isSyncStarted && DevConnectionFile.TryRead(out var devConnection)) StartSync(devConnection);
            // 繋ぐ場合は snapshot の mapId を待つ。あとから StartSync されたら snapshot のマップで置き換わる
            if (!isSyncStarted) RequestMap(fallbackMapId);
        }

        /// <summary>
        /// マップ定義を取り、GLB が変わっていれば読み込み直して置く。連続で呼ばれたら最後の呼び出しだけ反映する。
        /// 失敗しても表示中のマップは残す。
        /// </summary>
        private async void RequestMap(string mapId)
        {
            var request = mapLoads.BeginRequest();
            bool IsLatest() => this != null && mapLoads.IsLatest(request);
            try
            {
                var definition = await mapService.FetchAsync(mapId);
                if (!IsLatest()) return;
                if (mapLoads.NeedsReload(definition))
                {
                    if (!await mapPresenter.LoadAsync(definition, IsLatest)) return;
                    mapLoads.MarkLoaded(definition);
                    Debug.Log($"[SeekerAr] マップを差し替えた mapId={definition.mapId} model={definition.modelUrl} splat={definition.splatUrl}");
                }
                else
                {
                    // GLB は同じでも、管理画面で置き方だけ変えていることがある
                    mapPresenter.SetTransform(definition);
                }
                ShowMapError(null);
                // 読み込み前にマーカーを捉えていた場合に備えて、ここでも置く
                mapPresenter.Place(adapter);
            }
            catch (MapFetchException e) when (e.IsNotFound)
            {
                Debug.LogWarning($"[SeekerAr] {e.Message}");
                if (IsLatest()) ShowMapError($"マップ「{mapId}」がサーバーにありません");
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
                if (IsLatest()) ShowMapError("マップを読み込めません（通信を確認）");
            }
        }

        private void ShowMapError(string message)
        {
            mapStatusLabel.text = message ?? "";
            mapStatusLabel.transform.parent.gameObject.SetActive(message != null);
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
            // マーカー状況は playing（探索中）の HUD。他フェーズでは全画面パネルが出る
            phasePanels = new PhasePanelsView(canvasObject.transform, statusLabel.transform.parent.gameObject);
            // マーカー状況は毎フレーム書き換わるので、マップのエラーは別の枠に出す。フェーズの全画面パネルより後に作って手前に出す
            mapStatusLabel = CreateText(canvasObject.transform, "", new Vector2(0.5f, 0f), new Vector2(0f, 160f), new Vector2(980f, 120f));
            ShowMapError(null);
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
