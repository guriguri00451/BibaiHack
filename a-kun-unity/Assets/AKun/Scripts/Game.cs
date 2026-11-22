using System;
using System.Collections.Generic;
using System.Globalization;

namespace AKun
{
    // ゲームの状態 (仕様書 docs/whiteboard-brainstorm.md の 4 章・6 章)。通信はしない純ロジック。
    //   waiting → preparing → playing → finished → preparing (次のラウンド)
    // サーバーから来たもの (GET /sessions/{id}、session.snapshot、session.phase_changed、player.caught) を当てていく。
    // サーバーのフェーズ API がまだ無いときは、この端末 (ホスト) がフェーズを進める (LocalPhase)。
    // その間は、サーバーから来るフェーズ (まだ waiting のまま) で上書きしない。
    public class GamePlayer
    {
        public string id, userId, name, role; // role: hider / seeker / null (ラウンド開始前)
        public int score;
        public bool isCaught, connected = true;
    }

    public class GameState
    {
        public const string WAITING = "waiting", PREPARING = "preparing", PLAYING = "playing", FINISHED = "finished";

        public string sessionId, mapId, hostUserId, myUserId, myPlayerId;
        public string status = WAITING;
        public int round = 1;
        public DateTime? phaseEndsAt; // UTC
        public string myRole;
        public bool localPhase;        // フェーズをこの端末で進めている (サーバーのフェーズ API が未実装)
        public double clockOffsetMs;   // サーバー時刻 − この端末の時刻
        public readonly Dictionary<string, GamePlayer> players = new Dictionary<string, GamePlayer>();

        public bool IsHost => hostUserId != null && hostUserId == myUserId;
        public GamePlayer Me => myPlayerId != null && players.TryGetValue(myPlayerId, out var p) ? p : null;
        public bool IsCaught => Me?.isCaught ?? false;
        public bool IsHider => myRole == "hider";
        public bool InRound => status == PREPARING || status == PLAYING;
        public bool CanHide => IsHider && InRound && !IsCaught;

        public int Count(string role)
        {
            int n = 0;
            foreach (var p in players.Values) if (p.role == role) n++;
            return n;
        }

        // 残り秒 (期限が無ければ null)。now は UTC
        public double? Remaining(DateTime nowUtc)
        {
            if (phaseEndsAt == null) return null;
            var serverNow = nowUtc.AddMilliseconds(clockOffsetMs);
            return Math.Max(0, (phaseEndsAt.Value - serverNow).TotalSeconds);
        }

        // 投稿できない理由 (できるときは null)
        public string HideBlockReason()
        {
            if (IsCaught) return "見つかりました";
            if (status == WAITING) return "準備が始まるまで待ってください";
            if (status == FINISHED) return "ラウンドが終わりました";
            if (myRole == "seeker") return "このラウンドはケイです";
            if (!IsHider) return "役割が決まるまで待ってください";
            return null;
        }

        // ---------- サーバーから来たものを当てる ----------

        // GET /v1/sessions/{id} の応答
        public void ApplySessionDetail(Dictionary<string, object> d)
        {
            if (d == null) return;
            hostUserId = d.Str("hostUserId") ?? hostUserId;
            mapId = d.Str("mapId") ?? mapId;
            if (d.TryGetValue("players", out var pl) && pl is List<object> list)
            {
                var seen = new HashSet<string>();
                foreach (var o in list)
                {
                    if (!(o is Dictionary<string, object> pd)) continue;
                    string id = pd.Str("id") ?? pd.Str("playerId");
                    if (id == null) continue;
                    seen.Add(id);
                    var p = Player(id);
                    p.userId = pd.Str("userId") ?? p.userId;
                    p.name = pd.Str("name") ?? p.name;
                    if (pd.TryGetValue("score", out var sc) && sc != null) p.score = Convert.ToInt32(sc, CultureInfo.InvariantCulture);
                    if (!localPhase)
                    {
                        p.role = pd.Str("role");
                        p.isCaught = Bool(pd, "isCaught", p.isCaught);
                    }
                }
                foreach (var id in new List<string>(players.Keys)) if (!seen.Contains(id)) players.Remove(id);
            }
            if (myPlayerId != null && players.TryGetValue(myPlayerId, out var me) && !localPhase) myRole = me.role;
            if (!localPhase) ApplyPhase(d.Str("status"), Int(d, "round"), d, "phaseEndsAt");
        }

        // WebSocket のメッセージ。ログに出す一言を返す (何も無ければ null)
        public string ApplyMessage(Dictionary<string, object> msg)
        {
            if (msg == null) return null;
            string type = msg.Str("type");
            var payload = msg.TryGetValue("payload", out var pv) ? pv as Dictionary<string, object> : null;
            if (msg.TryGetValue("timestamp", out var ts) && ts != null)
                clockOffsetMs = Convert.ToDouble(ts, CultureInfo.InvariantCulture) - NowMs();
            switch (type)
            {
                case "session.snapshot": return ApplySnapshot(msg, payload);
                case "session.phase_changed": return ApplyPhaseChanged(msg, payload);
                case "player.caught": return ApplyCaught(payload);
                default: return null;
            }
        }

        string ApplySnapshot(Dictionary<string, object> msg, Dictionary<string, object> payload)
        {
            if (payload == null) return null;
            var session = payload.TryGetValue("session", out var sv) ? sv as Dictionary<string, object> : null;
            if (payload.TryGetValue("you", out var yv) && yv is Dictionary<string, object> you)
            {
                myPlayerId = you.Str("playerId") ?? myPlayerId;
                if (!localPhase) myRole = you.Str("role");
            }
            if (payload.TryGetValue("players", out var pl) && pl is List<object> list)
            {
                foreach (var o in list)
                {
                    if (!(o is Dictionary<string, object> pd)) continue;
                    string id = pd.Str("playerId") ?? pd.Str("id");
                    if (id == null) continue;
                    var p = Player(id);
                    p.name = pd.Str("name") ?? p.name;
                    p.connected = Bool(pd, "connected", p.connected);
                    if (!localPhase)
                    {
                        p.role = pd.Str("role");
                        p.isCaught = Bool(pd, "isCaught", p.isCaught);
                    }
                }
            }
            if (!localPhase && session != null)
            {
                int? r = Int(msg, "round") ?? Int(session, "round");
                ApplyPhase(session.Str("status"), r, session, "phaseEndsAt");
            }
            return null;
        }

        string ApplyPhaseChanged(Dictionary<string, object> msg, Dictionary<string, object> payload)
        {
            if (payload == null) return null;
            var src = payload.TryGetValue("session", out var sv) && sv is Dictionary<string, object> s ? s : payload;
            int? r = Int(msg, "round") ?? Int(src, "round");
            if (r != null && r.Value < round) return null; // 古いラウンドの知らせ
            localPhase = false; // サーバーがフェーズを進めている
            string before = status;
            int beforeRound = round;
            ApplyPhase(src.Str("status"), r, src, "phaseEndsAt");
            // 役割が一緒に来たら当てる
            if (payload.TryGetValue("roles", out var rv) && rv is List<object> roles)
            {
                foreach (var o in roles)
                {
                    if (!(o is Dictionary<string, object> rd)) continue;
                    string id = rd.Str("playerId");
                    if (id == null) continue;
                    Player(id).role = rd.Str("role");
                    if (id == myPlayerId) myRole = rd.Str("role");
                }
            }
            if (status == before && round == beforeRound) return null;
            return $"ラウンド {round}: {PhaseName(status)}";
        }

        string ApplyCaught(Dictionary<string, object> payload)
        {
            if (payload == null) return null;
            string id = payload.Str("playerId");
            if (id == null) return null;
            var p = Player(id);
            p.isCaught = true;
            if (payload.TryGetValue("score", out var sc) && sc != null) p.score = Convert.ToInt32(sc, CultureInfo.InvariantCulture);
            return id == myPlayerId ? "見つかりました！ このラウンドはもう動かせません" : $"{p.name ?? "ドロ"} が見つかりました";
        }

        // ---------- この端末で進める (フェーズ API が未実装のとき) ----------

        // 準備を始める。hiderId をドロに、ほかの全員をケイにする
        public void LocalStart(double preparingSeconds, string hiderId, DateTime nowUtc)
        {
            if (status == FINISHED) round++;
            localPhase = true;
            status = PREPARING;
            phaseEndsAt = nowUtc.AddMilliseconds(clockOffsetMs).AddSeconds(preparingSeconds);
            foreach (var p in players.Values) { p.role = p.id == hiderId ? "hider" : "seeker"; p.isCaught = false; }
            myRole = myPlayerId == hiderId ? "hider" : "seeker";
        }

        public void LocalPlay(double playingSeconds, DateTime nowUtc)
        {
            localPhase = true;
            status = PLAYING;
            phaseEndsAt = nowUtc.AddMilliseconds(clockOffsetMs).AddSeconds(playingSeconds);
        }

        public void LocalFinish()
        {
            localPhase = true;
            status = FINISHED;
            phaseEndsAt = null;
        }

        // 全てのドロが見つかったか (ドロが 1 人もいなければ false)
        public bool AllHidersCaught()
        {
            int hiders = 0;
            foreach (var p in players.Values)
            {
                if (p.role != "hider") continue;
                hiders++;
                if (!p.isCaught) return false;
            }
            return hiders > 0;
        }

        // ---------- 小物 ----------

        public static string PhaseName(string s) => s switch
        {
            WAITING => "待機中",
            PREPARING => "準備中",
            PLAYING => "本番",
            FINISHED => "終了",
            _ => s ?? "?",
        };

        public static string RoleName(string r) => r switch { "hider" => "ドロ", "seeker" => "ケイ", _ => "未定" };

        public static double NowMs() => (DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;

        GamePlayer Player(string id)
        {
            if (!players.TryGetValue(id, out var p)) players[id] = p = new GamePlayer { id = id };
            return p;
        }

        void ApplyPhase(string s, int? r, Dictionary<string, object> src, string endsKey)
        {
            if (r != null && r.Value != round)
            {
                // 新しいラウンド: 見つかった印は消える
                foreach (var p in players.Values) p.isCaught = false;
                round = r.Value;
            }
            if (s != null) status = s;
            if (src != null && src.ContainsKey(endsKey)) phaseEndsAt = ParseTime(src.Str(endsKey));
        }

        public static DateTime? ParseTime(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return null;
            return DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
                ? t : (DateTime?)null;
        }

        static int? Int(Dictionary<string, object> d, string k)
            => d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : (int?)null;

        static bool Bool(Dictionary<string, object> d, string k, bool fallback)
            => d != null && d.TryGetValue(k, out var v) && v is bool b ? b : fallback;
    }

    // サーバーの URL と、応答の見分け方
    public static class GameUrls
    {
        // 設定の server から REST の前置き (…/v1) と WebSocket (…/v1/ws) を作る。
        // https://bibai.t0waxx.com/a-kun-01 のような「接続先」でも、wss://…/v1/ws のような WebSocket の URL でもよい
        public static (string api, string ws) Endpoints(string server)
        {
            if (string.IsNullOrWhiteSpace(server)) throw new Exception("サーバーの URL がありません");
            var u = new Uri(server.Trim());
            string scheme = u.Scheme switch
            {
                "ws" => "http", "wss" => "https", "http" => "http", "https" => "https",
                _ => throw new Exception("サーバーの URL は https:// か wss:// で始めてください"),
            };
            string path = u.AbsolutePath.TrimEnd('/');
            foreach (var tail in new[] { "/v1/ws", "/ws", "/v1" })
                if (path.EndsWith(tail, StringComparison.Ordinal)) { path = path.Substring(0, path.Length - tail.Length); break; }
            string http = $"{scheme}://{u.Authority}{path}";
            string ws = $"{(scheme == "https" ? "wss" : "ws")}://{u.Authority}{path}/v1/ws";
            return ($"{http}/v1", ws);
        }

        // サーバーに「その API がまだ無い」と見なす応答。
        // 今のサーバーは、無い経路に 404 {"error":{"code":"NOT_FOUND"}} を返す (セッションが無いときは SESSION_NOT_FOUND など別の code)
        public static bool IsNotImplemented(long status, string body)
        {
            if (status == 405 || status == 501) return true;
            if (status != 404) return false;
            if (string.IsNullOrEmpty(body)) return true;
            try
            {
                if (MiniJson.Parse(body) is Dictionary<string, object> d && d.TryGetValue("error", out var e) && e is Dictionary<string, object> err)
                    return err.Str("code") == "NOT_FOUND";
            }
            catch { /* JSON でない 404 は、経路が無いとみなす */ }
            return true;
        }
    }
}
