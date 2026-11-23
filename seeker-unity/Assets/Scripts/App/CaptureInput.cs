using System;
using Seeker.Sync;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Seeker.App
{
    /// <summary>捕獲の結果。後続（演出・スコア HUD）が受け取る。</summary>
    public readonly struct CaptureOutcome
    {
        public readonly string objectId;
        public readonly CaptureResult result;

        public CaptureOutcome(string objectId, CaptureResult result)
        {
            this.objectId = objectId;
            this.result = result;
        }
    }

    /// <summary>
    /// playing 中のタップ位置から Raycast でオブジェクトを選び、捕獲を送る。
    /// 実機はタッチ、エディタはマウスで確認できる。送信中は選んだオブジェクトをハイライトする。
    /// </summary>
    public sealed class CaptureInput : MonoBehaviour
    {
        private SyncedObjectsController syncedObjects;
        private CaptureCooldown cooldown;
        private bool isSending;

        public event Action<CaptureOutcome> Captured;

        public void Initialize(SyncedObjectsController controller)
        {
            syncedObjects = controller;
            cooldown = new CaptureCooldown(() => Time.realtimeSinceStartupAsDouble);
        }

        private void Update()
        {
            if (syncedObjects == null || isSending || !cooldown.CanSend) return;
            if (!TryGetTap(out var position, out var touchId)) return;
            if (!CaptureTapPolicy.ShouldHandle(syncedObjects.Session.Phase, IsPointerOverUi(touchId))) return;
            if (syncedObjects.Connection == null) return;

            var camera = Camera.main;
            if (camera == null) return;
            if (!Physics.Raycast(camera.ScreenPointToRay(position), out var hit)) return;
            var view = hit.collider.GetComponentInParent<SyncedObjectView>();
            if (view == null || string.IsNullOrEmpty(view.ObjectId)) return;
            Send(view);
        }

        private async void Send(SyncedObjectView view)
        {
            isSending = true;
            cooldown.MarkSent();
            var objectId = view.ObjectId;
            view.SetHighlighted(true);
            var result = await CaptureClient.SendAsync(syncedObjects.Connection, objectId);
            // 待っている間にオブジェクトが消えることがある（他のケイに捕獲された等）
            if (view != null) view.SetHighlighted(false);
            if (result.RetryAfterSeconds.HasValue) cooldown.ApplyRetryAfter(result.RetryAfterSeconds.Value);
            isSending = false;
            Captured?.Invoke(new CaptureOutcome(objectId, result));
        }

        // 離した瞬間ではなく押した瞬間を採用（長押しの移動を誤爆させない）
        private static bool TryGetTap(out Vector2 position, out int touchId)
        {
            var touchscreen = Touchscreen.current;
            if (touchscreen != null && touchscreen.primaryTouch.press.wasPressedThisFrame)
            {
                position = touchscreen.primaryTouch.position.ReadValue();
                touchId = touchscreen.primaryTouch.touchId.ReadValue();
                return true;
            }
            var mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame)
            {
                position = mouse.position.ReadValue();
                touchId = -1;
                return true;
            }
            position = default;
            touchId = -1;
            return false;
        }

        private static bool IsPointerOverUi(int touchId)
        {
            var eventSystem = EventSystem.current;
            if (eventSystem == null) return false;
            return touchId >= 0 ? eventSystem.IsPointerOverGameObject(touchId) : eventSystem.IsPointerOverGameObject();
        }
    }
}
