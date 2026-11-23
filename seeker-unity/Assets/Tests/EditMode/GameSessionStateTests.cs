using NUnit.Framework;
using Seeker.Sync;

namespace Seeker.Ar.Tests
{
    public class GameSessionStateTests
    {
        private static ServerMessage Message(string type, long round, string payloadJson) =>
            ServerMessage.Parse($"{{\"type\":\"{type}\",\"round\":{round},\"seq\":1,\"payload\":{payloadJson}}}");

        [Test]
        public void Apply_SetsPhase_FromSnapshotStatus()
        {
            var state = new GameSessionState();

            state.Apply(Message("session.snapshot", 1, "{\"status\":\"preparing\"}"));

            Assert.That(state.Phase, Is.EqualTo(GamePhase.Preparing));
        }

        [Test]
        public void Apply_ReadsMapId_FromSnapshotSession()
        {
            var state = new GameSessionState();

            var isChanged = state.Apply(Message("session.snapshot", 1, "{\"session\":{\"status\":\"waiting\",\"mapId\":\"bibai-station\"}}"));

            Assert.That(isChanged, Is.True);
            Assert.That(state.MapId, Is.EqualTo("bibai-station"));
        }

        [Test]
        public void Apply_KeepsMapId_WhenPhaseChangedHasNone()
        {
            var state = new GameSessionState();
            state.Apply(Message("session.snapshot", 1, "{\"session\":{\"mapId\":\"bibai-station\"}}"));

            state.Apply(Message("session.phase_changed", 1, "{\"status\":\"preparing\"}"));

            Assert.That(state.MapId, Is.EqualTo("bibai-station"));
        }

        [Test]
        public void Apply_ReturnsFalse_WhenPhaseDoesNotChange()
        {
            var state = new GameSessionState();
            state.Apply(Message("session.snapshot", 1, "{\"status\":\"playing\"}"));

            var isChanged = state.Apply(Message("session.phase_changed", 1, "{\"status\":\"playing\"}"));

            Assert.That(isChanged, Is.False);
        }

        [Test]
        public void Apply_IgnoresUnknownStatus()
        {
            var state = new GameSessionState();
            state.Apply(Message("session.snapshot", 1, "{\"status\":\"playing\"}"));

            state.Apply(Message("session.phase_changed", 1, "{\"status\":\"bogus\"}"));

            Assert.That(state.Phase, Is.EqualTo(GamePhase.Playing));
        }

        [Test]
        public void Apply_IgnoresObjectEvents()
        {
            var state = new GameSessionState();

            Assert.That(state.Apply(Message("object.transform", 1, "{}")), Is.False);
        }

        [Test]
        public void Apply_ClearsResult_WhenRoundIncrements()
        {
            var state = new GameSessionState();
            state.Apply(Message("session.phase_changed", 1, "{\"status\":\"finished\",\"result\":{\"captured\":true,\"score\":10}}"));

            state.Apply(Message("session.phase_changed", 2, "{\"status\":\"waiting\"}"));

            Assert.That(state.Result.score, Is.Null);
        }

        [Test]
        public void Apply_ClearsParticipants_WhenRoundIncrements()
        {
            var state = new GameSessionState();
            state.Apply(Message("session.snapshot", 1, "{\"status\":\"waiting\",\"participants\":[\"a\",\"b\"]}"));

            state.Apply(Message("session.phase_changed", 2, "{\"status\":\"waiting\"}"));

            Assert.That(state.Participants, Is.Empty);
        }

        [Test]
        public void Apply_IgnoresPhaseChange_FromOlderRound()
        {
            var state = new GameSessionState();
            state.Apply(Message("session.snapshot", 2, "{\"status\":\"playing\"}"));

            state.Apply(Message("session.phase_changed", 1, "{\"status\":\"finished\"}"));

            Assert.That(state.Phase, Is.EqualTo(GamePhase.Playing));
        }

        [Test]
        public void Apply_ReadsParticipantNames_FromStringsAndObjects()
        {
            var state = new GameSessionState();

            state.Apply(Message("session.snapshot", 1, "{\"status\":\"waiting\",\"participants\":[\"a\",{\"name\":\"b\"}]}"));

            Assert.That(state.Participants, Is.EqualTo(new[] { "a", "b" }));
        }

        [Test]
        public void Apply_ReadsResult_OnFinished()
        {
            var state = new GameSessionState();

            state.Apply(Message("session.phase_changed", 1, "{\"status\":\"finished\",\"result\":{\"captured\":false,\"score\":42}}"));

            Assert.That(state.Result.isCaptured, Is.False);
        }

        [Test]
        public void Apply_AcceptsSnapshotOfOlderRound_AsAuthoritative()
        {
            var state = new GameSessionState();
            state.Apply(Message("session.snapshot", 3, "{\"status\":\"playing\"}"));

            state.Apply(Message("session.snapshot", 1, "{\"status\":\"waiting\"}"));

            Assert.That(state.Phase, Is.EqualTo(GamePhase.Waiting));
        }
    }
}
