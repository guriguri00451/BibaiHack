using System.Linq;
using NUnit.Framework;
using Seeker.Ar;
using Seeker.Sync;
using UnityEngine;

namespace Seeker.Ar.Tests
{
    public class EventSequencerTests
    {
        [Test]
        public void TryAccept_RejectsEvent_WhenSeqIsNotNewerInSameRound()
        {
            var sequencer = new EventSequencer();
            sequencer.Reset(1, 10);

            Assert.That(sequencer.TryAccept(1, 10), Is.False);
        }

        [Test]
        public void TryAccept_AcceptsEvent_WhenRoundIsNewerEvenIfSeqRestarts()
        {
            var sequencer = new EventSequencer();
            sequencer.Reset(1, 99);

            Assert.That(sequencer.TryAccept(2, 1), Is.True);
        }

        [Test]
        public void TryAccept_RejectsEvent_FromOlderRound()
        {
            var sequencer = new EventSequencer();
            sequencer.Reset(2, 1);

            Assert.That(sequencer.TryAccept(1, 500), Is.False);
        }

        [Test]
        public void TryAccept_AcceptsFirstEvent_WhenNoBaseline()
        {
            var sequencer = new EventSequencer();
            sequencer.Reset(null, null);

            Assert.That(sequencer.TryAccept(1, 0), Is.True);
        }
    }

    public class TransformSmootherTests
    {
        private static MapTransform At(float x) => new MapTransform(new Vector3(x, 0, 0), Quaternion.identity, Vector3.one);

        [Test]
        public void Step_ReturnsFirstTargetImmediately()
        {
            var smoother = new TransformSmoother();
            smoother.SetTarget(At(5f));

            Assert.That(smoother.Step(0.016f).position.x, Is.EqualTo(5f));
        }

        [Test]
        public void Step_MovesPartwayTowardNewTarget()
        {
            var smoother = new TransformSmoother();
            smoother.SetTarget(At(0f));
            smoother.SetTarget(At(10f));

            var x = smoother.Step(0.016f).position.x;

            Assert.That(x, Is.GreaterThan(0f).And.LessThan(10f));
        }

        [Test]
        public void Step_ConvergesToTarget_AfterLongTime()
        {
            var smoother = new TransformSmoother();
            smoother.SetTarget(At(0f));
            smoother.SetTarget(At(10f));

            Assert.That(smoother.Step(5f).position.x, Is.EqualTo(10f).Within(1e-3f));
        }

        [Test]
        public void Step_ReachesSamePositionRegardlessOfFrameRate()
        {
            var fast = new TransformSmoother();
            var slow = new TransformSmoother();
            fast.SetTarget(At(0f)); fast.SetTarget(At(10f));
            slow.SetTarget(At(0f)); slow.SetTarget(At(10f));

            var fastX = 0f;
            for (var i = 0; i < 10; i++) fastX = fast.Step(0.01f).position.x;
            var slowX = slow.Step(0.1f).position.x;

            Assert.That(fastX, Is.EqualTo(slowX).Within(1e-3f));
        }
    }

    public class SyncedObjectStoreTests
    {
        private const string Transform = "{\"position\":[1,0,2],\"rotation\":[0,0,0,1],\"scale\":[1,1,1]}";

        private static ServerMessage Message(string type, long round, long seq, string payload) =>
            ServerMessage.Parse($"{{\"type\":\"{type}\",\"round\":{round},\"seq\":{seq},\"payload\":{payload}}}");

        private static ServerMessage Snapshot(long round, long seq, string objects) =>
            ServerMessage.Parse($"{{\"type\":\"session.snapshot\",\"round\":{round},\"seq\":{seq},\"payload\":{{\"objects\":[{objects}]}}}}");

        private static string Object(string id, string key) =>
            $"{{\"objectId\":\"{id}\",\"assetKey\":\"{key}\",\"transform\":{Transform}}}";

        [Test]
        public void Apply_Created_CarriesAssetUrlFromPayload()
        {
            var store = new SyncedObjectStore();

            var changes = store.Apply(Message("object.created", 1, 1,
                $"{{\"objectId\":\"a\",\"assetKey\":\"k\",\"assetUrl\":\"https://s/k?sig=1\",\"transform\":{Transform}}}"));

            Assert.That(changes[0].assetUrl, Is.EqualTo("https://s/k?sig=1"));
        }

        [Test]
        public void Apply_Updated_DoesNotReportAssetChange_WhenOnlySignedUrlChanged()
        {
            var store = new SyncedObjectStore();
            store.Apply(Message("object.created", 1, 1, $"{{\"objectId\":\"a\",\"assetKey\":\"k\",\"assetUrl\":\"https://s/k?sig=1\",\"transform\":{Transform}}}"));

            var changes = store.Apply(Message("object.updated", 1, 2, "{\"objectId\":\"a\",\"assetKey\":\"k\",\"assetUrl\":\"https://s/k?sig=2\"}"));

            Assert.That(changes, Is.Empty);
        }

        [Test]
        public void Apply_Snapshot_AddsObjects()
        {
            var store = new SyncedObjectStore();

            var changes = store.Apply(Snapshot(1, 5, Object("a", "k1")));

            Assert.That(changes[0].kind, Is.EqualTo(SyncChangeKind.Added));
        }

        [Test]
        public void Apply_ObjectTransform_IgnoresStaleSeq()
        {
            var store = new SyncedObjectStore();
            store.Apply(Snapshot(1, 5, Object("a", "k1")));

            var changes = store.Apply(Message("object.transform", 1, 5, $"{{\"objectId\":\"a\",\"transform\":{Transform}}}"));

            Assert.That(changes, Is.Empty);
        }

        [Test]
        public void Apply_ObjectTransform_EmitsTransformChange_WhenNewer()
        {
            var store = new SyncedObjectStore();
            store.Apply(Snapshot(1, 5, Object("a", "k1")));

            var changes = store.Apply(Message("object.transform", 1, 6, $"{{\"objectId\":\"a\",\"transform\":{Transform}}}"));

            Assert.That(changes[0].kind, Is.EqualTo(SyncChangeKind.TransformChanged));
        }

        [Test]
        public void Apply_ObjectTransform_IgnoresUnknownObject()
        {
            var store = new SyncedObjectStore();
            store.Apply(Snapshot(1, 5, ""));

            var changes = store.Apply(Message("object.transform", 1, 6, $"{{\"objectId\":\"ghost\",\"transform\":{Transform}}}"));

            Assert.That(changes, Is.Empty);
        }

        [Test]
        public void Apply_ObjectUpdated_EmitsAssetChanged_WhenKeyDiffers()
        {
            var store = new SyncedObjectStore();
            store.Apply(Snapshot(1, 5, Object("a", "k1")));

            var changes = store.Apply(Message("object.updated", 1, 6, "{\"objectId\":\"a\",\"assetKey\":\"k2\"}"));

            Assert.That(changes[0].kind, Is.EqualTo(SyncChangeKind.AssetChanged));
        }

        [Test]
        public void Apply_ObjectDeleted_RemovesObject()
        {
            var store = new SyncedObjectStore();
            store.Apply(Snapshot(1, 5, Object("a", "k1")));

            var changes = store.Apply(Message("object.deleted", 1, 6, "{\"objectId\":\"a\"}"));

            Assert.That(changes[0].kind, Is.EqualTo(SyncChangeKind.Removed));
        }

        [Test]
        public void Apply_SecondSnapshot_RemovesObjectsMissingFromIt()
        {
            var store = new SyncedObjectStore();
            store.Apply(Snapshot(1, 5, Object("a", "k1") + "," + Object("b", "k2")));

            var changes = store.Apply(Snapshot(1, 9, Object("a", "k1")));

            Assert.That(changes, Has.Some.Matches<SyncChange>(c => c.kind == SyncChangeKind.Removed && c.objectId == "b"));
        }

        [Test]
        public void Apply_SecondSnapshot_DiscardsEventsOlderThanItsBaseline()
        {
            var store = new SyncedObjectStore();
            store.Apply(Snapshot(1, 5, Object("a", "k1")));
            store.Apply(Snapshot(1, 20, Object("a", "k1")));

            var changes = store.Apply(Message("object.transform", 1, 12, $"{{\"objectId\":\"a\",\"transform\":{Transform}}}"));

            Assert.That(changes, Is.Empty);
        }

        [Test]
        public void Apply_ObjectEventWithoutSeq_IsIgnored()
        {
            var store = new SyncedObjectStore();
            store.Apply(Snapshot(1, 5, Object("a", "k1")));

            var changes = store.Apply(ServerMessage.Parse($"{{\"type\":\"object.transform\",\"payload\":{{\"objectId\":\"a\",\"transform\":{Transform}}}}}"));

            Assert.That(changes, Is.Empty);
        }

        [Test]
        public void Parse_Throws_WhenRotationIsZeroQuaternion()
        {
            var message = Message("object.created", 1, 1, "{\"objectId\":\"a\",\"assetKey\":\"k\",\"transform\":{\"position\":[0,0,0],\"rotation\":[0,0,0,0],\"scale\":[1,1,1]}}");

            Assert.Throws<System.FormatException>(() => new SyncedObjectStore().Apply(message));
        }
    }

    public class SyncConnectionTests
    {
        [Test]
        public void FromBaseUrl_BuildsWssAndHttpsUrls_FromHttpsEnvironmentUrl()
        {
            var connection = SyncConnection.FromBaseUrl("https://bibai.t0waxx.com/so-ma-01", "session", "token");

            Assert.That(connection.WsUrl, Is.EqualTo("wss://bibai.t0waxx.com/so-ma-01/v1/ws"));
        }

        [Test]
        public void FromBaseUrl_BuildsHttpsRestBase_FromHttpsEnvironmentUrl()
        {
            var connection = SyncConnection.FromBaseUrl("https://bibai.t0waxx.com/so-ma-01", "session", "token");

            Assert.That(connection.RestBaseUrl, Is.EqualTo("https://bibai.t0waxx.com/so-ma-01/v1"));
        }

        [Test]
        public void FromBaseUrl_BuildsWsUrl_FromHttpLocalUrlWithTrailingSlash()
        {
            var connection = SyncConnection.FromBaseUrl("http://192.168.0.5:8787/", "session", "token");

            Assert.That(connection.WsUrl, Is.EqualTo("ws://192.168.0.5:8787/v1/ws"));
        }

        [Test]
        public void FromBaseUrl_Throws_WhenSchemeIsNotHttp()
        {
            Assert.Throws<System.ArgumentException>(() => SyncConnection.FromBaseUrl("ftp://example.com", "session", "token"));
        }

        [Test]
        public void Constructor_Throws_WhenAccessTokenIsEmpty()
        {
            Assert.Throws<System.ArgumentException>(() => new SyncConnection("wss://x/v1/ws", "https://x/v1", "session", ""));
        }
    }
}

namespace Seeker.Ar.Tests
{
    public class PoseSendPolicyTests
    {
        private long now = 1_700_000_000_000;

        private PoseSendPolicy NewPolicy() => new PoseSendPolicy(() => now);

        private static MapTransform Pose(Quaternion rotation) => new MapTransform(new Vector3(1, 2, 3), rotation, Vector3.one);

        private bool TrySend(PoseSendPolicy policy, out string json, bool calibrated = true, bool tracking = true, bool playing = true)
            => policy.TryCreateMessage(calibrated, tracking, playing, () => Pose(Quaternion.identity), out json);

        private static long ClientSeqOf(string json) => (long)Newtonsoft.Json.Linq.JObject.Parse(json)["payload"]["clientSeq"];

        [Test]
        public void TryCreateMessage_Sends_OnFirstCall()
        {
            Assert.That(TrySend(NewPolicy(), out _), Is.True);
        }

        [Test]
        public void TryCreateMessage_DoesNotSend_WhenCalledWithinInterval()
        {
            var policy = NewPolicy();
            TrySend(policy, out _);
            now += 30;

            Assert.That(TrySend(policy, out _), Is.False);
        }

        [Test]
        public void TryCreateMessage_Sends_WhenIntervalElapsed()
        {
            var policy = NewPolicy();
            TrySend(policy, out _);
            now += 67;

            Assert.That(TrySend(policy, out _), Is.True);
        }

        [Test]
        public void TryCreateMessage_SendsAbout15TimesPerSecond_WhenCalledEveryFrameAt60Fps()
        {
            var policy = NewPolicy();
            var sentCount = 0;
            for (var frame = 0; frame < 60; frame++)
            {
                if (TrySend(policy, out _)) sentCount++;
                now += 1000 / 60;
            }

            Assert.That(sentCount, Is.InRange(12, 16));
        }

        [Test]
        public void TryCreateMessage_IncreasesClientSeqMonotonically()
        {
            var policy = NewPolicy();
            TrySend(policy, out var first);
            now += 100;
            TrySend(policy, out var second);

            Assert.That(ClientSeqOf(second), Is.GreaterThan(ClientSeqOf(first)));
        }

        [Test]
        public void TryCreateMessage_ClientSeqDoesNotGoBack_WhenPolicyIsRecreated()
        {
            var policy = NewPolicy();
            long last = 0;
            for (var i = 0; i < 100; i++)
            {
                TrySend(policy, out var json);
                last = ClientSeqOf(json);
                now += 100;
            }
            var restarted = NewPolicy();
            TrySend(restarted, out var afterRestart);

            Assert.That(ClientSeqOf(afterRestart), Is.GreaterThan(last));
        }

        [Test]
        public void TryCreateMessage_NormalizesRotation()
        {
            var policy = NewPolicy();
            policy.TryCreateMessage(true, true, true, () => Pose(new Quaternion(0, 0, 0, 5f)), out var json);

            var rotation = Newtonsoft.Json.Linq.JObject.Parse(json)["payload"]["transform"]["rotation"];
            var length = Mathf.Sqrt(rotation.Sum(c => (float)c * (float)c));
            Assert.That(length, Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void TryCreateMessage_DoesNotSend_WhenNotCalibrated()
        {
            Assert.That(TrySend(NewPolicy(), out _, calibrated: false), Is.False);
        }

        [Test]
        public void TryCreateMessage_DoesNotSend_WhenNotTracking()
        {
            Assert.That(TrySend(NewPolicy(), out _, tracking: false), Is.False);
        }

        [Test]
        public void TryCreateMessage_DoesNotSend_WhenNotPlaying()
        {
            Assert.That(TrySend(NewPolicy(), out _, playing: false), Is.False);
        }

        [Test]
        public void TryCreateMessage_DoesNotReadPose_WhenNotCalibrated()
        {
            var isRead = false;
            NewPolicy().TryCreateMessage(false, true, true, () => { isRead = true; return Pose(Quaternion.identity); }, out _);

            Assert.That(isRead, Is.False);
        }

        [Test]
        public void TryCreateMessage_BuildsPlayerTransformShape()
        {
            TrySend(NewPolicy(), out var json);

            var root = Newtonsoft.Json.Linq.JObject.Parse(json);
            var transform = root["payload"]["transform"];
            Assert.That(
                (string)root["type"] == "player.transform"
                && transform["position"].Count() == 3
                && transform["rotation"].Count() == 4
                && transform["scale"].Count() == 3
                && (float)transform["scale"][0] == 1f,
                Is.True);
        }
    }
}
