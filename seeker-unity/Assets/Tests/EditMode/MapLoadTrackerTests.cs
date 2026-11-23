using NUnit.Framework;

namespace Seeker.Ar.Tests
{
    public class MapLoadTrackerTests
    {
        private static MapDefinition Map(string mapId, string modelUrl, string splatUrl = null) =>
            new MapDefinition { mapId = mapId, modelUrl = modelUrl, splatUrl = splatUrl };

        [Test]
        public void NeedsReload_ReturnsTrue_WhenNothingIsLoaded()
        {
            var tracker = new MapLoadTracker();

            Assert.That(tracker.NeedsReload(Map("a", "https://h/v1/maps/a/model.glb")), Is.True);
        }

        [Test]
        public void NeedsReload_ReturnsFalse_WhenMapIdAndModelUrlAreSame()
        {
            var tracker = new MapLoadTracker();
            tracker.MarkLoaded(Map("a", "https://h/v1/maps/a/model.glb?v=1"));

            Assert.That(tracker.NeedsReload(Map("a", "https://h/v1/maps/a/model.glb?v=1")), Is.False);
        }

        [Test]
        public void NeedsReload_ReturnsTrue_WhenMapIdChanges()
        {
            var tracker = new MapLoadTracker();
            tracker.MarkLoaded(Map("a", "https://h/v1/maps/a/model.glb"));

            Assert.That(tracker.NeedsReload(Map("b", "https://h/v1/maps/a/model.glb")), Is.True);
        }

        [Test]
        public void NeedsReload_ReturnsTrue_WhenModelUrlChangesWithSameMapId()
        {
            var tracker = new MapLoadTracker();
            tracker.MarkLoaded(Map("a", "https://h/v1/maps/a/model.glb?v=1"));

            Assert.That(tracker.NeedsReload(Map("a", "https://h/v1/maps/a/model.glb?v=2")), Is.True);
        }

        [Test]
        public void NeedsReload_ReturnsTrue_WhenSplatUrlChangesWithSameMapId()
        {
            var tracker = new MapLoadTracker();
            tracker.MarkLoaded(Map("z", "https://h/v1/maps/z/model.glb?v=1", "https://h/v1/maps/z/splat.spz?v=1"));

            Assert.That(tracker.NeedsReload(Map("z", "https://h/v1/maps/z/model.glb?v=1", "https://h/v1/maps/z/splat.spz?v=2")), Is.True);
        }

        [Test]
        public void NeedsReload_ReturnsFalse_WhenSplatUrlIsSame()
        {
            var tracker = new MapLoadTracker();
            tracker.MarkLoaded(Map("z", "u", "https://h/v1/maps/z/splat.spz?v=1"));

            Assert.That(tracker.NeedsReload(Map("z", "u", "https://h/v1/maps/z/splat.spz?v=1")), Is.False);
        }

        [Test]
        public void IsLatest_ReturnsFalse_ForOlderRequestAfterNewerOne()
        {
            var tracker = new MapLoadTracker();
            var older = tracker.BeginRequest();
            tracker.BeginRequest();

            Assert.That(tracker.IsLatest(older), Is.False);
        }

        [Test]
        public void IsLatest_ReturnsTrue_ForNewestRequest()
        {
            var tracker = new MapLoadTracker();
            tracker.BeginRequest();
            var newest = tracker.BeginRequest();

            Assert.That(tracker.IsLatest(newest), Is.True);
        }

        [Test]
        public void NeedsReload_ReturnsTrue_AfterOnlyAnOlderRequestFinished()
        {
            // 古い要求の結果は MarkLoaded されないので、最後の要求のマップは改めて読み込み対象になる
            var tracker = new MapLoadTracker();
            var older = tracker.BeginRequest();
            tracker.BeginRequest();
            if (tracker.IsLatest(older)) tracker.MarkLoaded(Map("a", "u-a"));

            Assert.That(tracker.NeedsReload(Map("b", "u-b")), Is.True);
        }
    }
}
