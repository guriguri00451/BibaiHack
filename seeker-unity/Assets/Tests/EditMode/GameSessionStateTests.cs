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

        private static ServerMessage Raw(string json) => ServerMessage.Parse(json);

        private const string RealSnapshot =
            "{\"type\":\"session.snapshot\",\"round\":1,\"seq\":0,\"timestamp\":1000,\"payload\":{\"session\":{\"status\":\"playing\",\"round\":1,\"phaseEndsAt\":\"2026-01-01T00:00:30.000Z\"},\"players\":[],\"objects\":[]}}";

        [Test]
        public void Apply_ReadsPhase_FromSnapshotSessionStatus()
        {
            var state = new GameSessionState();

            state.Apply(Raw(RealSnapshot));

            Assert.That(state.Phase, Is.EqualTo(GamePhase.Playing));
        }

        [Test]
        public void Apply_ReadsPhaseEndsAt_FromSnapshot()
        {
            var state = new GameSessionState();

            state.Apply(Raw(RealSnapshot));

            Assert.That(state.PhaseEndsAtMs, Is.EqualTo(1767225630000L));
        }

        [Test]
        public void Apply_ReadsPhaseEndsAt_FromPhaseChanged()
        {
            var state = new GameSessionState();

            state.Apply(Message("session.phase_changed", 1, "{\"status\":\"preparing\",\"phaseEndsAt\":\"2026-01-01T00:01:00.000Z\"}"));

            Assert.That(state.PhaseEndsAtMs, Is.EqualTo(1767225660000L));
        }

        [Test]
        public void Apply_ClearsPhaseEndsAt_WhenFinishedHasNull()
        {
            var state = new GameSessionState();
            state.Apply(Raw(RealSnapshot));

            state.Apply(Message("session.phase_changed", 1, "{\"status\":\"finished\",\"phaseEndsAt\":null}"));

            Assert.That(state.PhaseEndsAtMs, Is.Null);
        }

        [Test]
        public void Apply_PhaseEndsAtIsNull_OnWaitingSnapshot()
        {
            var state = new GameSessionState();

            state.Apply(Raw("{\"type\":\"session.snapshot\",\"round\":1,\"seq\":0,\"timestamp\":1,\"payload\":{\"session\":{\"status\":\"waiting\",\"round\":1,\"phaseEndsAt\":null}}}"));

            Assert.That(state.PhaseEndsAtMs, Is.Null);
        }

        [Test]
        public void Apply_DoesNotChangePhase_WhenRemainingReachesZero()
        {
            var state = new GameSessionState();
            state.Apply(Raw(RealSnapshot));
            var clock = new ServerClock(() => 1767225630000L + 5000);

            clock.RemainingMs(state.PhaseEndsAtMs.Value);

            Assert.That(state.Phase, Is.EqualTo(GamePhase.Playing));
        }

        // ---- サイクル（rounds/start の resultSeconds）: server/README.md「ゲームの進み方」 ----

        private static string CycleSnapshot(long round, string status, string phaseEndsAt) =>
            $"{{\"type\":\"session.snapshot\",\"round\":{round},\"seq\":0,\"timestamp\":1,\"payload\":{{\"session\":{{\"status\":\"{status}\",\"round\":{round},\"mapId\":\"m\",\"phaseEndsAt\":{phaseEndsAt}}},\"players\":[],\"objects\":[]}}}}";

        private static GameSessionState FinishedInCycle()
        {
            var state = new GameSessionState();
            state.Apply(Message("session.phase_changed", 1, "{\"status\":\"finished\",\"phaseEndsAt\":\"2026-01-01T00:00:10.000Z\",\"result\":{\"captured\":true,\"score\":30}}"));
            return state;
        }

        [Test]
        public void IsNextRoundScheduled_IsTrue_WhenFinishedHasPhaseEndsAt()
        {
            var state = FinishedInCycle();

            Assert.That(state.IsNextRoundScheduled, Is.True);
        }

        [Test]
        public void IsNextRoundScheduled_IsFalse_WhenFinishedWithoutCycle()
        {
            var state = new GameSessionState();

            state.Apply(Message("session.phase_changed", 1, "{\"status\":\"finished\",\"phaseEndsAt\":null}"));

            Assert.That(state.IsNextRoundScheduled, Is.False);
        }

        [Test]
        public void IsNextRoundScheduled_IsFalse_WhilePlaying()
        {
            var state = new GameSessionState();

            state.Apply(Raw(RealSnapshot));

            Assert.That(state.IsNextRoundScheduled, Is.False);
        }

        [Test]
        public void Apply_ReportsChange_WhenCycleStopSnapshotClearsPhaseEndsAt()
        {
            var state = FinishedInCycle();

            var isChanged = state.Apply(Raw(CycleSnapshot(1, "finished", "null")));

            Assert.That(isChanged, Is.True);
        }

        [Test]
        public void Apply_StopsSchedulingNextRound_OnCycleStopSnapshot()
        {
            var state = FinishedInCycle();

            state.Apply(Raw(CycleSnapshot(1, "finished", "null")));

            Assert.That(state.IsNextRoundScheduled, Is.False);
        }

        [Test]
        public void Apply_KeepsResult_OnCycleStopSnapshot()
        {
            // 停止の snapshot には result が無い。同じラウンドなので結果は出したまま
            var state = FinishedInCycle();

            state.Apply(Raw(CycleSnapshot(1, "finished", "null")));

            Assert.That(state.Result.score, Is.EqualTo(30));
        }

        [Test]
        public void Apply_ClearsResult_WhenCycleStartsNextRound()
        {
            var state = FinishedInCycle();

            state.Apply(Message("session.phase_changed", 2, "{\"status\":\"preparing\",\"round\":2,\"phaseEndsAt\":\"2026-01-01T00:01:00.000Z\"}"));

            Assert.That(state.Result.score, Is.Null);
        }

        [Test]
        public void Apply_EntersPreparingOfNextRound_WhenCycleStartsNextRound()
        {
            var state = FinishedInCycle();

            state.Apply(Message("session.phase_changed", 2, "{\"status\":\"preparing\",\"round\":2,\"phaseEndsAt\":\"2026-01-01T00:01:00.000Z\"}"));

            Assert.That((state.Phase, state.Round), Is.EqualTo((GamePhase.Preparing, 2L)));
        }

        [Test]
        public void Apply_EntersNextRound_WhenOnlySnapshotArrives()
        {
            // phase_changed を取りこぼしても、送り直される snapshot だけで次のラウンドに入る
            var state = FinishedInCycle();

            state.Apply(Raw(CycleSnapshot(2, "preparing", "\"2026-01-01T00:01:00.000Z\"")));

            Assert.That((state.Phase, state.Round, state.Result.score), Is.EqualTo((GamePhase.Preparing, 2L, (int?)null)));
        }

        [Test]
        public void Apply_SchedulesNextRound_ForLateJoinerSnapshot()
        {
            // 後から入った人は finished の snapshot だけを受け取る（result 無し）
            var state = new GameSessionState();

            state.Apply(Raw(CycleSnapshot(3, "finished", "\"2026-01-01T00:00:10.000Z\"")));

            Assert.That(state.IsNextRoundScheduled, Is.True);
        }
    }
}
