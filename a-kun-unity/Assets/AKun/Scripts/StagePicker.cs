using System;
using System.Collections.Generic;
using UnityEngine;

namespace AKun
{
    // ステージ (マップ) を選ぶパネル。隠す場所を選ぶ前に、サーバーのステージ一覧から選んで、そのモデルを取ってくる。
    //   開く → サーバーから一覧を取り直す (GET /v1/maps) → ステージを押す (PUT /v1/sessions/{id}/map)
    //   → そのモデルを取得する (GET /v1/maps/{mapId} → GLB) → 「投稿する」で、取れたモデルの中に隠す場所を選ぶ
    // ステージは部屋に 1 つ (ケイにも同じものが出る) なので、替えられるのはホストが待機中かラウンドの終わった後だけ。
    // ほかの人・ほかのフェーズでは、いま使っているステージと取得の状態を見るだけ。
    // 一覧は固定の枠 (SLOTS) とページ送りで出す (ButtonPanel は作るときにボタンの数が決まるため)
    public class StagePicker
    {
        const int SLOTS = 4;
        readonly ClayApp app;
        readonly Func<ServerTarget> server;
        readonly ButtonPanel panel;
        int page;

        public ButtonPanel Panel => panel;
        public bool IsOpen => panel.Visible;

        public StagePicker(ClayApp app, Transform workspace, float panelZ, Func<ServerTarget> server, Action close)
        {
            this.app = app;
            this.server = server;
            const float gap = 0.006f, bh = 0.036f, hh = 0.03f;
            const float prevW = 0.05f, midW = 0.092f, W = 2 * prevW + 2 * midW + 3 * gap;
            var defs = new List<ButtonDef>
            {
                new ButtonDef { id = "stageHead", label = Head, x = 0, y = 0, w = W, h = hh },
            };
            for (int i = 0; i < SLOTS; i++)
            {
                int slot = i;
                defs.Add(new ButtonDef
                {
                    id = $"stage{slot}", color = "#4fc3f7", x = 0, y = -0.04f - slot * (bh + gap), w = W, h = bh,
                    label = () => SlotLabel(slot), onPress = () => Pick(slot),
                    isActive = () => IsCurrent(slot), isEnabled = () => SlotEnabled(slot),
                });
            }
            float yNav = -0.04f - SLOTS * (bh + gap);
            float x0 = -W / 2;
            defs.Add(new ButtonDef
            {
                id = "stagePrev", label = () => "◀", x = x0 + prevW / 2, y = yNav, w = prevW, h = bh,
                onPress = () => page = Mathf.Max(0, page - 1), isEnabled = () => page > 0,
            });
            defs.Add(new ButtonDef
            {
                id = "stageRefresh", label = () => server()?.MapsLoading == true ? "取得中…" : "一覧を更新", x = x0 + prevW + gap + midW / 2, y = yNav, w = midW, h = bh,
                onPress = () => Refresh(), isEnabled = () => server() != null && !server().MapsLoading,
            });
            defs.Add(new ButtonDef
            {
                id = "stageNext", label = () => "▶", x = x0 + prevW + midW + 2 * gap + prevW / 2, y = yNav, w = prevW, h = bh,
                onPress = () => page = Mathf.Min(PageCount - 1, page + 1), isEnabled = () => page < PageCount - 1,
            });
            defs.Add(new ButtonDef
            {
                id = "stageClose", label = () => "閉じる", x = x0 + 2 * prevW + midW + 3 * gap + midW / 2, y = yNav, w = midW, h = bh,
                onPress = close,
            });
            panel = new ButtonPanel(workspace, "stage-panel", defs, new[] { "ステージを選ぶ" });
            // 投稿のパネルと入れ替えて出す。タイトルの行まで含めた板の上端が、上の「今やること」の帯 (下端 y=0.26) に触れない高さ
            panel.group.localPosition = new Vector3(0, 0.2f, -panelZ);
            panel.Visible = false;
        }

        // 開く: すぐ今の一覧を出し、サーバーから取り直す (管理画面で足したステージもここで出る)
        public void Open()
        {
            panel.Visible = true;
            page = PageOfCurrent();
            Refresh();
        }

        public void Close() => panel.Visible = false;

        public void Update()
        {
            page = Mathf.Clamp(page, 0, PageCount - 1);
            if (panel.Visible) panel.Refresh();
        }

        async void Refresh()
        {
            var s = server();
            if (s == null || s.MapsLoading) return;
            await s.LoadMaps();
            page = PageOfCurrent();
        }

        // ---------- 一覧 ----------

        // サーバーの一覧。取れていなくても、いま使っているステージは出す
        List<ServerTarget.MapEntry> Entries()
        {
            var list = new List<ServerTarget.MapEntry>();
            var s = server();
            if (s == null) return list;
            list.AddRange(s.maps);
            string cur = s.game.mapId;
            if (!string.IsNullOrEmpty(cur) && !list.Exists(m => m.id == cur)) list.Insert(0, new ServerTarget.MapEntry { id = cur, name = s.MapName(cur) });
            return list;
        }

        int PageCount => Mathf.Max(1, (Entries().Count + SLOTS - 1) / SLOTS);

        int PageOfCurrent()
        {
            var s = server();
            int i = s == null ? -1 : Entries().FindIndex(m => m.id == s.game.mapId);
            return i < 0 ? 0 : i / SLOTS;
        }

        ServerTarget.MapEntry? EntryAt(int slot)
        {
            var list = Entries();
            int i = page * SLOTS + slot;
            return i < list.Count ? list[i] : (ServerTarget.MapEntry?)null;
        }

        bool IsCurrent(int slot)
        {
            var e = EntryAt(slot);
            return e != null && e.Value.id == server()?.game.mapId;
        }

        // いま使っているステージは、取れていなければ取り直せる。ほかのステージはホストが替えられるときだけ
        bool SlotEnabled(int slot)
        {
            var s = server();
            var e = EntryAt(slot);
            if (s == null || e == null || s.MapBusy) return false;
            return e.Value.id == s.game.mapId || s.CanChangeMap;
        }

        string SlotLabel(int slot)
        {
            var e = EntryAt(slot);
            if (e == null) return "";
            var s = server();
            if (s == null || e.Value.id != s.game.mapId) return e.Value.name;
            return $"{e.Value.name}{StateMark(e.Value.id)}";
        }

        // いま使っているステージの、モデルの取得の状態
        string StateMark(string id)
        {
            if (app.StageId != id) return " · 取得待ち";
            switch (app.Stage)
            {
                case ClayApp.StageState.Loading: return " · 取得中…";
                case ClayApp.StageState.Ready: return " · 取得済み";
                case ClayApp.StageState.Failed: return " · 取得できません (押すと取り直す)";
                default: return "";
            }
        }

        // 押した: いま使っているステージなら、モデルを取り直す。別のステージならホストが替えて、すぐモデルを取りにいく
        void Pick(int slot)
        {
            var s = server();
            var e = EntryAt(slot);
            if (s == null || e == null) return;
            if (e.Value.id == s.game.mapId) { app.PreloadStage(e.Value.id); return; }
            Select(e.Value.id);
        }

        async void Select(string id)
        {
            var s = server();
            if (s != null && await s.SelectMap(id)) app.PreloadStage(id);
        }

        string Head()
        {
            var s = server();
            if (s == null) return "";
            string pages = PageCount > 1 ? $" · {page + 1}/{PageCount}" : "";
            if (s.MapsLoading) return "サーバーからステージ一覧を取っています…";
            if (s.MapBusy) return "ステージを替えています…";
            if (s.CanChangeMap) return $"ステージを選んでください ({s.maps.Count} 件){pages}";
            if (!s.game.IsHost) return $"ステージはホストが選びます{pages}";
            if (s.game.status != GameState.WAITING && s.game.status != GameState.FINISHED) return $"ラウンド中は替えられません{pages}";
            return s.maps.Count == 0 ? "ステージの一覧を取れません" : "選べるステージは 1 つだけです";
        }
    }
}
