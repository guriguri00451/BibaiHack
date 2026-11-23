using NUnit.Framework;
using Seeker.Sync;

namespace Seeker.Ar.Tests
{
    public class ScoreTests
    {
        private const string Me = "me";

        private static ServerMessage Message(string type, string payloadJson) =>
            ServerMessage.Parse($"{{\"type\":\"{type}\",\"round\":1,\"seq\":1,\"payload\":{payloadJson}}}");

        private static GameSessionState WithSnapshot(string youJson, string playersJson)
        {
            var state = new GameSessionState();
            state.Apply(Message("session.snapshot", $"{{\"session\":{{\"status\":\"playing\"}},{youJson}\"players\":{playersJson}}}"));
            return state;
        }

        private static GameSessionState PlayingAsMe(int score = 10) => WithSnapshot(
            "\"you\":{\"playerId\":\"me\"},",
            $"[{{\"playerId\":\"other\",\"score\":99}},{{\"playerId\":\"me\",\"score\":{score}}}]");

        private static ServerMessage Caught(string by, int seekerScore) =>
            Message("player.caught", $"{{\"playerId\":\"dro\",\"byPlayerId\":\"{by}\",\"objectId\":\"o\",\"score\":5,\"seekerScore\":{seekerScore}}}");

        [Test]
        public void Snapshot_ReadsOwnScore_NotOthers()
        {
            Assert.That(PlayingAsMe(10).MyScore, Is.EqualTo(10));
        }

        [Test]
        public void Snapshot_ScoreIsNull_WhenYouIsMissing()
        {
            var state = WithSnapshot("", "[{\"playerId\":\"me\",\"score\":10}]");
            Assert.That(state.MyScore, Is.Null);
        }

        [Test]
        public void Snapshot_ScoreIsNull_WhenPlayersLacksMe()
        {
            var state = WithSnapshot("\"you\":{\"playerId\":\"me\"},", "[{\"playerId\":\"other\",\"score\":99}]");
            Assert.That(state.MyScore, Is.Null);
        }

        [Test]
        public void PlayerCaught_ByMe_OverwritesScoreWithSeekerScore()
        {
            var state = PlayingAsMe(10);
            state.Apply(Caught(Me, 30));
            Assert.That(state.MyScore, Is.EqualTo(30));
        }

        [Test]
        public void PlayerCaught_ByOther_KeepsScore()
        {
            var state = PlayingAsMe(10);
            state.Apply(Caught("other", 70));
            Assert.That(state.MyScore, Is.EqualTo(10));
        }

        [Test]
        public void PlayerCaught_ByOther_RaisesNotice()
        {
            var state = PlayingAsMe(10);
            var isChanged = state.Apply(Caught("other", 70));
            Assert.That(isChanged && state.OtherSeekerCaughtCount == 1, Is.True);
        }

        [Test]
        public void PlayerCaught_AfterCaptureResponse_DoesNotDoubleCount()
        {
            var state = PlayingAsMe(10);
            state.ApplyScore(30);
            state.Apply(Caught(Me, 30));
            Assert.That(state.MyScore, Is.EqualTo(30));
        }

        [Test]
        public void CaptureFeedback_Miss_ShowsNoDelta()
        {
            var miss = CaptureResult.Parse(200, "{\"result\":\"miss\",\"scoreDelta\":0,\"score\":10}", null);
            var text = ScoreText.CaptureFeedback(miss);
            Assert.That(text, Is.EqualTo("ハズレ"));
        }

        [Test]
        public void CaptureFeedback_Caught_ShowsPlusDelta()
        {
            var caught = CaptureResult.Parse(200, "{\"result\":\"caught\",\"scoreDelta\":20,\"score\":30}", null);
            Assert.That(ScoreText.CaptureFeedback(caught), Is.EqualTo("見つけた！ +20"));
        }

        [Test]
        public void CaptureFeedback_Error_IsNull()
        {
            Assert.That(ScoreText.CaptureFeedback(CaptureResult.Failure("x")), Is.Null);
        }

        [TestCase("time_up", "時間切れ")]
        [TestCase("all_caught", "ドロを全員見つけた")]
        [TestCase("host", "ホストが終了した")]
        public void ResultText_ContainsReasonAndScore(string reason, string expected)
        {
            var text = ScoreText.BuildResult(new GameResult(true, 42, reason));
            Assert.That(text, Does.Contain(expected).And.Contain("スコア: 42"));
        }

        [Test]
        public void PhaseChanged_FinishedResult_ReadsReason()
        {
            var state = PlayingAsMe();
            state.Apply(Message("session.phase_changed", "{\"status\":\"finished\",\"result\":{\"reason\":\"host\",\"captured\":false,\"score\":5,\"scores\":[]}}"));
            Assert.That(state.Result.reason, Is.EqualTo("host"));
        }
    }
}
