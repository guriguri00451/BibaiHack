using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AKun
{
    // 投稿 (Web 版 a-kun/src/post/ui.js の XR 部分の移植)。
    // 流れ: 粘土で形を作る → 「塗る」で色を付ける → 投稿 (GLB を上げて、オブジェクトとして登録)
    //   投稿したあとも粘土はそのまま残り、直してから「更新して投稿」で新しい版を上げられる。
    //   「新しく作る」で球に戻すと、次は別のオブジェクトとして投稿される。
    // 投稿先: 最初は「この端末内」。起動時の設定 (Intent の extra か config.json) にサーバーがあれば接続する
    public class Post
    {
        public class Item { public string id; public int n, version; public byte[] bytes; public ClayField.Snapshot snapshot; }

        readonly ClayApp app;
        IPostTarget target = new LocalTarget();
        public readonly Dictionary<string, Item> posted = new Dictionary<string, Item>();
        int counter;
        string editingId;
        bool busy;
        readonly ButtonPanel xrPanel;

        public IPostTarget Target => target;
        public ButtonPanel Panel => xrPanel;

        public Post(ClayApp app, Transform workspace, float panelZ)
        {
            this.app = app;
            const float bw = 0.09f, bh = 0.036f, gap = 0.006f;
            xrPanel = new ButtonPanel(workspace, "post-panel", new List<ButtonDef>
            {
                new ButtonDef { id = "status", label = XRStatus, x = 0, y = 0, w = 2 * bw + gap, h = 0.03f },
                new ButtonDef
                {
                    id = "post", x = -(bw + gap) / 2, y = -0.04f, w = bw, h = bh, color = "#81c784",
                    label = () => busy ? "送信中…" : editingId != null && posted.TryGetValue(editingId, out var it) ? $"#{it.n} を更新" : "投稿する",
                    onPress = DoPost, isEnabled = () => !busy,
                },
                new ButtonDef { id = "new", label = () => "新しく作る", x = (bw + gap) / 2, y = -0.04f, w = bw, h = bh, onPress = NewClay, isEnabled = () => !busy },
            });
            xrPanel.group.localPosition = new Vector3(0, 0.2f, -panelZ);
        }

        string XRStatus()
        {
            string e = editingId != null && posted.TryGetValue(editingId, out var it) ? $" · #{it.n} を編集中" : "";
            return $"投稿 {posted.Count} 件 · {target.Label}{e}";
        }

        // 体積をそろえるために大きさを直した倍率 (ほぼ 1 のときは出さない)
        static string SizeNote(ClayApp.ExportResult ex) => Mathf.Abs(ex.factor - 1) > 0.01f ? $"、大きさを ×{ex.factor:0.00} にそろえた" : "";
        static string Kb(int n) => $"{Mathf.Max(1, Mathf.RoundToInt(n / 1024f))}KB";

        // 仕様 3.5 の Transform。位置は粘土の底面の中心。座標は Web 版 (WebXR, 右手系) にそろえる (z を反転)
        static Dictionary<string, object> TransformOf(ClayApp.ExportResult ex)
        {
            var p = ex.anchor; var q = ex.rotation;
            return new Dictionary<string, object>
            {
                ["position"] = new List<object> { p.x, p.y, -p.z },
                ["rotation"] = new List<object> { -q.x, -q.y, q.z, q.w },
                ["scale"] = new List<object> { 1f, 1f, 1f },
            };
        }

        async void DoPost()
        {
            if (busy) return;
            busy = true;
            try
            {
                var ex = app.ExportClay();
                string assetUrl = await target.UploadAsset(ex.bytes);
                var transform = TransformOf(ex);
                if (editingId != null && posted.TryGetValue(editingId, out var item))
                {
                    await target.UpdateObject(item.id, assetUrl, transform);
                    item.version++; item.bytes = ex.bytes; item.snapshot = app.field.TakeSnapshot();
                    string path = Save(item);
                    app.Log($"#{item.n} を更新しました ({Kb(ex.bytes.Length)}{SizeNote(ex)})");
                    Debug.Log($"[clay] 保存: {path}");
                }
                else
                {
                    string objectId = await target.CreateObject(assetUrl, transform);
                    var it = new Item { id = objectId, n = ++counter, version = 1, bytes = ex.bytes, snapshot = app.field.TakeSnapshot() };
                    posted[objectId] = it;
                    editingId = objectId;
                    string path = Save(it);
                    app.Log($"#{counter} を投稿しました ({Kb(ex.bytes.Length)}{SizeNote(ex)})");
                    Debug.Log($"[clay] 保存: {path}");
                }
            }
            catch (Exception err)
            {
                app.Log($"投稿できませんでした: {err.Message}");
            }
            finally
            {
                busy = false;
            }
        }

        // 投稿した GLB を端末にも保存する (adb pull で取り出せる)
        static string Save(Item it)
        {
            try
            {
                string dir = Path.Combine(Application.persistentDataPath, "posts");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, $"clay-{it.n}-v{it.version}.glb");
                File.WriteAllBytes(path, it.bytes);
                return path;
            }
            catch (Exception e) { return $"(保存できません: {e.Message})"; }
        }

        void NewClay()
        {
            editingId = null;
            app.ResetClay();
        }

        // ---------- サーバー ----------
        public async void Connect(string server, string code, string name)
        {
            busy = true;
            try
            {
                var t = new ServerTarget(server.Trim());
                await t.Join(code.Trim().ToUpperInvariant(), name);
                t.onClose = () =>
                {
                    if (target != t) return;
                    SwitchTarget(new LocalTarget());
                    app.Log("サーバーとの接続が切れました。投稿先をこの端末内に戻しました");
                };
                SwitchTarget(t);
                app.Log($"サーバーにつながりました ({t.Label})");
            }
            catch (Exception err)
            {
                app.Log($"サーバーに接続できません: {err.Message}");
            }
            finally { busy = false; }
        }

        // 投稿先を替えると、それまでの投稿の一覧は空にする (別の場所の話になるので)
        void SwitchTarget(IPostTarget t)
        {
            target.Close();
            target = t;
            posted.Clear();
            editingId = null;
            counter = 0;
        }

        public void Close() => target.Close();

        public void Update()
        {
            if (xrPanel.Visible) xrPanel.Refresh();
        }
    }
}
