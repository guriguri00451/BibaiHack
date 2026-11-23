using System;
using System.Collections.Generic;
using Seeker.Sync;
using Seeker.Sync.Lobby;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace Seeker.App
{
    /// <summary>
    /// 接続先と名前を入れ、GET /v1/sessions の一覧から選んで参加する画面（サーバーブラウザ）。
    /// 参加できたら <see cref="Joined"/> に SyncConnection を渡して自分は閉じる。
    /// 判定・保存は Seeker.Sync.Lobby の純 C# に任せ、ここは uGUI の組み立てと呼び出しだけ。
    /// </summary>
    public sealed class SessionBrowserView : MonoBehaviour
    {
        private const int SortingOrder = 100;
        private static readonly Color BackgroundTint = new Color(0.05f, 0.08f, 0.14f, 0.97f);
        private static readonly Color FieldColor = new Color(1f, 1f, 1f, 0.12f);
        private static readonly Color ButtonColor = new Color(0.2f, 0.45f, 0.85f, 1f);

        public event Action<SyncConnection> Joined;

        private LobbySettings settings;
        private InputField baseUrlField;
        private InputField nameField;
        private Text messageLabel;
        private RectTransform listContent;
        private readonly List<GameObject> rows = new List<GameObject>();
        private bool isBusy;
        // 一覧を取った時の接続先。入力欄を書き換えても、参加はこの接続先で行う（トークンもこの接続先のもの）
        private string listedBaseUrl;
        private GuestTokenProvider tokens;
        private LobbyClient client;

        private void Awake()
        {
            settings = new LobbySettings(new PlayerPrefsStore());
            EnsureEventSystem();
            BuildUi();
            baseUrlField.text = settings.BaseUrl;
            nameField.text = settings.PlayerName;
            ShowMessage("接続先と名前を入れて「一覧を取得」");
        }

        // シーンに EventSystem が無いとボタン・入力欄が反応しない。Input System のみの設定なので専用モジュールを使う
        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
        }

        private async void FetchSessions()
        {
            if (isBusy) return;
            if (!LobbySettings.TryNormalizeBaseUrl(baseUrlField.text, out var baseUrl))
            {
                ShowMessage("接続先は https:// で始まる URL を入れてください");
                return;
            }
            if (!LobbySettings.TryNormalizePlayerName(nameField.text, out var playerName))
            {
                ShowMessage($"名前は 1〜{LobbySettings.MaxPlayerNameLength} 文字で入れてください");
                return;
            }
            settings.BaseUrl = baseUrl;
            settings.PlayerName = playerName;
            client = new LobbyClient(baseUrl);
            tokens = new GuestTokenProvider(settings, baseUrl, () => client.CreateGuestAsync(playerName));
            listedBaseUrl = baseUrl;

            isBusy = true;
            ShowMessage("一覧を取得中…");
            try
            {
                var sessions = await tokens.RunAsync(token => client.ListSessionsAsync(token));
                if (this == null) return;
                RenderSessions(sessions);
                ShowMessage(sessions.Count == 0 ? "参加できるセッションがありません（ホストの作成を待って更新）" : "参加するセッションを選んでください");
            }
            catch (Exception e)
            {
                HandleError(e);
            }
            finally
            {
                isBusy = false;
            }
        }

        private async void JoinSession(SessionSummary session)
        {
            if (isBusy || tokens == null) return;
            isBusy = true;
            ShowMessage("参加中…");
            try
            {
                await tokens.RunAsync(token => client.JoinAsync(token, session.Id));
                if (this == null) return;
                var connection = SyncConnection.FromBaseUrl(listedBaseUrl, session.Id, tokens.CurrentToken);
                gameObject.SetActive(false);
                Joined?.Invoke(connection);
            }
            catch (Exception e)
            {
                HandleError(e);
            }
            finally
            {
                isBusy = false;
            }
        }

        private void HandleError(Exception e)
        {
            if (this == null) return;
            if (e is LobbyException lobby)
            {
                Debug.LogWarning($"[SessionBrowser] {lobby.Kind}: {lobby.Message}");
                ShowMessage(lobby.UserMessage);
                return;
            }
            Debug.LogException(e);
            ShowMessage("サーバーの応答を読めません");
        }

        private void ShowMessage(string message) => messageLabel.text = message;

        private void RenderSessions(IReadOnlyList<SessionSummary> sessions)
        {
            foreach (var row in rows) Destroy(row);
            rows.Clear();
            foreach (var session in sessions)
            {
                var label = $"{session.MapId}  [{session.StatusLabel}]\nホスト {session.HostName} ・ {session.PlayerCount}人（接続中 {session.ConnectedCount}）";
                var captured = session;
                rows.Add(CreateButton(listContent, label, 150f, () => JoinSession(captured)).gameObject);
            }
        }

        // ---------- uGUI の組み立て ----------

        private void BuildUi()
        {
            var canvasObject = new GameObject("SessionBrowserCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // AR の HUD やフェーズの全画面パネルより手前に出す
            canvas.sortingOrder = SortingOrder;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);

            var root = CreatePanel(canvasObject.transform, BackgroundTint);
            var layout = root.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(60, 60, 140, 80);
            layout.spacing = 24f;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            CreateLabel(root, "セッションを選ぶ", 64, 100f);
            CreateLabel(root, "接続先", 40, 50f);
            baseUrlField = CreateInputField(root, "https://example.com/env", InputField.ContentType.Standard);
            CreateLabel(root, "名前", 40, 50f);
            nameField = CreateInputField(root, "Player", InputField.ContentType.Standard);
            nameField.characterLimit = LobbySettings.MaxPlayerNameLength;
            CreateButton(root, "一覧を取得 / 更新", 120f, FetchSessions);
            messageLabel = CreateLabel(root, "", 40, 110f);
            listContent = CreateScrollList(root);
        }

        private static RectTransform CreatePanel(Transform parent, Color color)
        {
            var go = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = color;
            var rect = (RectTransform)go.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            return rect;
        }

        private static Font UiFont => Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        private static Text CreateLabel(Transform parent, string content, int fontSize, float height)
        {
            var go = new GameObject("Label", typeof(RectTransform), typeof(Text), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            go.GetComponent<LayoutElement>().preferredHeight = height;
            var text = go.GetComponent<Text>();
            ApplyTextStyle(text, fontSize, TextAnchor.MiddleLeft);
            text.text = content;
            return text;
        }

        private static void ApplyTextStyle(Text text, int fontSize, TextAnchor alignment)
        {
            text.font = UiFont;
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = Color.white;
        }

        private static Text CreateFillText(Transform parent, int fontSize, TextAnchor alignment, Color color)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(24f, 0f);
            rect.offsetMax = new Vector2(-24f, 0f);
            var text = go.GetComponent<Text>();
            ApplyTextStyle(text, fontSize, alignment);
            text.color = color;
            // InputField の子は raycast を受けない方が、フィールド本体のタップが確実に拾える
            text.raycastTarget = false;
            return text;
        }

        private static InputField CreateInputField(Transform parent, string placeholder, InputField.ContentType contentType)
        {
            var go = new GameObject("InputField", typeof(RectTransform), typeof(Image), typeof(InputField), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            go.GetComponent<LayoutElement>().preferredHeight = 110f;
            go.GetComponent<Image>().color = FieldColor;
            var field = go.GetComponent<InputField>();
            field.placeholder = CreateFillText(go.transform, 40, TextAnchor.MiddleLeft, new Color(1f, 1f, 1f, 0.4f));
            ((Text)field.placeholder).text = placeholder;
            field.textComponent = CreateFillText(go.transform, 40, TextAnchor.MiddleLeft, Color.white);
            field.contentType = contentType;
            field.lineType = InputField.LineType.SingleLine;
            return field;
        }

        private static Button CreateButton(Transform parent, string label, float height, Action onClick)
        {
            var go = new GameObject("Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            go.GetComponent<LayoutElement>().preferredHeight = height;
            go.GetComponent<Image>().color = ButtonColor;
            var button = go.GetComponent<Button>();
            button.onClick.AddListener(() => onClick());
            CreateFillText(go.transform, 40, TextAnchor.MiddleLeft, Color.white).text = label;
            return button;
        }

        /// <summary>残りの高さを埋める縦スクロールの一覧。行を足す先（Content）を返す</summary>
        private static RectTransform CreateScrollList(Transform parent)
        {
            var viewport = new GameObject("SessionList", typeof(RectTransform), typeof(Image), typeof(RectMask2D), typeof(ScrollRect), typeof(LayoutElement));
            viewport.transform.SetParent(parent, false);
            viewport.GetComponent<LayoutElement>().flexibleHeight = 1f;
            viewport.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.25f);

            var content = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            content.transform.SetParent(viewport.transform, false);
            var rect = (RectTransform)content.transform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            var layout = content.GetComponent<VerticalLayoutGroup>();
            layout.spacing = 16f;
            layout.padding = new RectOffset(16, 16, 16, 16);
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scroll = viewport.GetComponent<ScrollRect>();
            scroll.content = rect;
            scroll.viewport = (RectTransform)viewport.transform;
            scroll.horizontal = false;
            return rect;
        }
    }
}
