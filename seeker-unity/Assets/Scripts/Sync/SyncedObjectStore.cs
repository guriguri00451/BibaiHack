using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Seeker.Ar;

namespace Seeker.Sync
{
    /// <summary>
    /// サーバーから届いたオブジェクトの最新状態。メッセージを適用して「何が変わったか」を返す純ロジック。
    /// payload の形は仕様に明記が無いため、ここ(と ServerMessage)に閉じ込めている。想定:
    ///   session.snapshot: { round?, seq?, objects: [{ objectId, assetKey|assetUrl, transform }] }
    ///   object.created / object.updated: { objectId, assetKey|assetUrl?, transform? }
    ///   object.deleted: { objectId } / object.transform: { objectId, transform }
    /// </summary>
    public sealed class SyncedObjectStore
    {
        private sealed class Entry
        {
            public string assetKey;
            public MapTransform transform;
        }

        private readonly Dictionary<string, Entry> objects = new Dictionary<string, Entry>();
        private readonly EventSequencer sequencer = new EventSequencer();

        public int Count => objects.Count;

        public IReadOnlyList<SyncChange> Apply(ServerMessage message)
        {
            var changes = new List<SyncChange>();
            if (message.Type == "session.snapshot")
            {
                ApplySnapshot(message, changes);
                return changes;
            }

            if (!IsObjectEvent(message.Type)) return changes;
            // round/seq が無い object イベントは順序を判定できないので、古い更新を弾けない。サーバー契約違反として無視する
            if (!message.Round.HasValue || !message.Seq.HasValue) return changes;
            if (!sequencer.TryAccept(message.Round.Value, message.Seq.Value)) return changes;

            switch (message.Type)
            {
                case "object.created":
                case "object.updated":
                    Upsert(message.Payload, changes);
                    break;
                case "object.transform":
                    UpdateTransform(message.Payload, changes);
                    break;
                case "object.deleted":
                    Remove((string)message.Payload["objectId"], changes);
                    break;
            }
            return changes;
        }

        private static bool IsObjectEvent(string type) =>
            type == "object.created" || type == "object.updated" || type == "object.transform" || type == "object.deleted";

        private void ApplySnapshot(ServerMessage message, List<SyncChange> changes)
        {
            // snapshot は「その時点の全状態」。未確定の古い更新は破棄して基準を張り直す（仕様 §6.2-9）
            sequencer.Reset(message.Round ?? (long?)message.Payload["round"], message.Seq ?? (long?)message.Payload["seq"]);

            var received = new HashSet<string>();
            if (message.Payload["objects"] is JArray array)
            {
                foreach (var item in array)
                {
                    var id = (string)item["objectId"];
                    if (string.IsNullOrEmpty(id)) continue;
                    received.Add(id);
                    Upsert((JObject)item, changes);
                }
            }

            // 全消し→作り直しにするとチラつくので、含まれていないものだけ消す
            foreach (var staleId in new List<string>(objects.Keys))
            {
                if (!received.Contains(staleId)) Remove(staleId, changes);
            }
        }

        private void Upsert(JObject payload, List<SyncChange> changes)
        {
            var id = (string)payload["objectId"];
            if (string.IsNullOrEmpty(id)) throw new FormatException("objectId が無い");
            var assetKey = (string)(payload["assetKey"] ?? payload["assetUrl"]);
            var hasTransform = payload["transform"] != null && payload["transform"].Type != JTokenType.Null;
            var transform = hasTransform ? ServerMessage.ParseTransform(payload["transform"]) : MapTransform.Identity;

            if (!objects.TryGetValue(id, out var entry))
            {
                if (string.IsNullOrEmpty(assetKey)) throw new FormatException($"新規オブジェクト {id} に assetKey が無い");
                objects[id] = new Entry { assetKey = assetKey, transform = transform };
                changes.Add(new SyncChange(SyncChangeKind.Added, id, assetKey, transform));
                return;
            }

            if (!string.IsNullOrEmpty(assetKey) && assetKey != entry.assetKey)
            {
                entry.assetKey = assetKey;
                changes.Add(new SyncChange(SyncChangeKind.AssetChanged, id, assetKey, entry.transform));
            }
            if (hasTransform)
            {
                entry.transform = transform;
                changes.Add(new SyncChange(SyncChangeKind.TransformChanged, id, entry.assetKey, transform));
            }
        }

        private void UpdateTransform(JObject payload, List<SyncChange> changes)
        {
            var id = (string)payload["objectId"];
            // created より先に transform が届いた場合などは、状態を持っていないので捨てる（次の snapshot / created で揃う）
            if (id == null || !objects.TryGetValue(id, out var entry)) return;
            entry.transform = ServerMessage.ParseTransform(payload["transform"]);
            changes.Add(new SyncChange(SyncChangeKind.TransformChanged, id, entry.assetKey, entry.transform));
        }

        private void Remove(string id, List<SyncChange> changes)
        {
            if (id == null || !objects.Remove(id)) return;
            changes.Add(new SyncChange(SyncChangeKind.Removed, id, null, MapTransform.Identity));
        }
    }
}
