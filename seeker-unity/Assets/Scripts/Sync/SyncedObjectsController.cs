using System;
using System.Collections.Generic;
using Seeker.Ar;
using UnityEngine;

namespace Seeker.Sync
{
    /// <summary>
    /// サーバー同期オブジェクトを AR 空間に出す。受信 → ストア更新 → 変化を GameObject に反映、の橋渡し。
    /// マーカーで原点が決まるまでは何も表示しない（CoordinateAdapter 未較正）。接続先は Configure で渡される。
    /// </summary>
    public sealed class SyncedObjectsController : MonoBehaviour
    {
        private readonly SyncedObjectStore store = new SyncedObjectStore();
        private readonly GameSessionState session = new GameSessionState();
        private readonly Dictionary<string, SyncedObjectView> views = new Dictionary<string, SyncedObjectView>();
        private readonly ServerClock clock = new ServerClock(ServerClock.SystemNowMs);
        private CoordinateAdapter adapter;
        private SeekerWsClient client;
        private GlbCache glbCache;

        /// <summary>サーバーに拒否されて同期を諦めたとき（再接続しても直らない）。</summary>
        public event Action Rejected;

        public GameSessionState Session => session;
        /// <summary>Configure 前は null。REST（捕獲など）の送信先と認証に使う。</summary>
        public SyncConnection Connection { get; private set; }
        public ServerClock Clock => clock;

        /// <summary>フェーズ・ラウンド・参加者・結果のいずれかが変わったとき。</summary>
        public event Action SessionChanged;

        /// <summary>
        /// snapshot がセッションの mapId を載せてきたとき（同じ mapId でも毎回）。
        /// 同じ mapId でも GLB が差し替わっていることがあるので、変化の判定は受け手がマップ定義を見て行う。
        /// </summary>
        public event Action<string> MapIdReceived;

        /// <summary>
        /// 接続情報が揃ってから呼ぶ（呼ぶまでは接続しない）。1インスタンスにつき1回だけ。
        /// セッションを切り替えるときは GameObject ごと作り直す。
        /// </summary>
        public void Configure(CoordinateAdapter coordinateAdapter, SyncConnection connection)
        {
            if (client != null) throw new System.InvalidOperationException("SyncedObjectsController はすでに接続済み");
            adapter = coordinateAdapter;
            Connection = connection;
            glbCache = new GlbCache();
            client = new SeekerWsClient(connection);
            client.MessageReceived += OnMessage;
            client.Rejected += () => Rejected?.Invoke();
            client.Start();
        }

        /// <summary>player.transform の JSON を送る。未接続なら捨てる。</summary>
        public void SendPlayerTransform(string json) => client?.SendText(json);

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
                if (message.Timestamp.HasValue) clock.Observe(message.Timestamp.Value);
                if (session.Apply(message)) SessionChanged?.Invoke();
                var mapId = GameSessionState.ReadMapId(message);
                if (mapId != null) MapIdReceived?.Invoke(mapId);
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
                    if (views.TryGetValue(change.objectId, out var view)) LoadModel(view, change.assetKey, change.assetUrl);
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
            view.ObjectId = change.objectId;
            view.SetTarget(change.transform);
            views[change.objectId] = view;
            LoadModel(view, change.assetKey, change.assetUrl);
        }

        private async void LoadModel(SyncedObjectView view, string assetKey, string assetUrl)
        {
            view.AssetKey = assetKey;
            var container = new GameObject($"Model_{assetKey}");
            container.transform.SetParent(view.transform, false);
            var isLoaded = await glbCache.InstantiateInto(assetKey, assetUrl, container.transform);

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
