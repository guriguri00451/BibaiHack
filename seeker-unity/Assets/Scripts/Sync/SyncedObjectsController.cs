using System;
using System.Collections.Generic;
using Seeker.Ar;
using UnityEngine;

namespace Seeker.Sync
{
    /// <summary>
    /// サーバー同期オブジェクトを AR 空間に出す。受信 → ストア更新 → 変化を GameObject に反映、の橋渡し。
    /// マーカーで原点が決まるまでは何も表示しない（CoordinateAdapter 未較正）。
    /// </summary>
    public sealed class SyncedObjectsController : MonoBehaviour
    {
        // 実機からは PC の LAN IP を指定する（localhost は端末自身を指す）。接続情報は本来ゲーム進行側から Configure で渡す
        [SerializeField] private string wsUrl = "ws://localhost:8787/v1/ws";
        [SerializeField] private string restBaseUrl = "http://localhost:8787/v1";
        [SerializeField] private string sessionId = "dev-session";
        // 認証トークンはコミットしない。モックは任意の値を受け付ける
        [SerializeField] private string accessToken = "dev-token";

        private readonly SyncedObjectStore store = new SyncedObjectStore();
        private readonly GameSessionState session = new GameSessionState();
        private readonly Dictionary<string, SyncedObjectView> views = new Dictionary<string, SyncedObjectView>();
        private CoordinateAdapter adapter;
        private SeekerWsClient client;
        private GlbCache glbCache;

        public GameSessionState Session => session;

        /// <summary>フェーズ・ラウンド・参加者・結果のいずれかが変わったとき。</summary>
        public event Action SessionChanged;

        public void Configure(CoordinateAdapter coordinateAdapter) => adapter = coordinateAdapter;

        private void Start()
        {
            glbCache = new GlbCache(new RestAssetLocator(restBaseUrl, accessToken));
            client = new SeekerWsClient(wsUrl, accessToken, sessionId);
            client.MessageReceived += OnMessage;
            client.Start();
        }

        private void Update()
        {
            client?.DispatchMessages();
            if (adapter == null) return;
            // preparing 中は配置情報が来ない前提で何も描かない。playing 以外はオブジェクトを隠す
            var isPlaying = session.Phase == GamePhase.Playing;
            foreach (var view in views.Values)
            {
                if (isPlaying) view.Render(adapter, Time.deltaTime);
                else if (view.gameObject.activeSelf) view.gameObject.SetActive(false);
            }
        }

        private void OnDestroy()
        {
            client?.Dispose();
            glbCache?.Dispose();
        }

        private void OnMessage(string json)
        {
            IReadOnlyList<SyncChange> changes;
            try
            {
                var message = ServerMessage.Parse(json);
                if (session.Apply(message)) SessionChanged?.Invoke();
                changes = store.Apply(message);
            }
            catch (System.Exception exception) when (exception is System.FormatException || exception is Newtonsoft.Json.JsonException)
            {
                // 1通の不正メッセージで同期全体を止めない
                Debug.LogWarning($"[SyncedObjects] 不正なメッセージを無視: {exception.Message}\n{json}");
                return;
            }
            foreach (var change in changes) Apply(change);
        }

        private void Apply(SyncChange change)
        {
            switch (change.kind)
            {
                case SyncChangeKind.Added:
                    CreateView(change);
                    break;
                case SyncChangeKind.AssetChanged:
                    if (views.TryGetValue(change.objectId, out var view)) LoadModel(view, change.assetKey);
                    break;
                case SyncChangeKind.TransformChanged:
                    if (views.TryGetValue(change.objectId, out var target)) target.SetTarget(change.transform);
                    break;
                case SyncChangeKind.Removed:
                    if (views.TryGetValue(change.objectId, out var removed))
                    {
                        views.Remove(change.objectId);
                        Destroy(removed.gameObject);
                    }
                    break;
            }
        }

        private void CreateView(SyncChange change)
        {
            var view = new GameObject($"Synced_{change.objectId}").AddComponent<SyncedObjectView>();
            view.SetTarget(change.transform);
            views[change.objectId] = view;
            LoadModel(view, change.assetKey);
        }

        private async void LoadModel(SyncedObjectView view, string assetKey)
        {
            view.AssetKey = assetKey;
            var container = new GameObject($"Model_{assetKey}");
            container.transform.SetParent(view.transform, false);
            var isLoaded = await glbCache.InstantiateInto(assetKey, container.transform);

            // await 中にオブジェクトが消えた / 別アセットへ差し替わった場合は結果を使わない
            var isStale = view == null || view.AssetKey != assetKey;
            if (!isLoaded || isStale)
            {
                Destroy(container);
                return;
            }
            view.AttachModel(container);
        }
    }
}
