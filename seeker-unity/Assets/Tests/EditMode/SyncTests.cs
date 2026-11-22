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
}
