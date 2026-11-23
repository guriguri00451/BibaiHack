using NUnit.Framework;
using Seeker.Sync;

namespace Seeker.Ar.Tests
{
    public class CaptureResultTests
    {
        private const string CaughtBody = "{\"result\":\"caught\",\"scoreDelta\":100,\"score\":250,\"caughtPlayerId\":\"p-1\"}";

        [Test]
        public void Parse_ReturnsCaught_WhenResultIsCaught()
        {
            Assert.AreEqual(CaptureStatus.Caught, CaptureResult.Parse(200, CaughtBody, null).Status);
        }

        [Test]
        public void Parse_ReadsScoreAndCaughtPlayer_WhenCaught()
        {
            var result = CaptureResult.Parse(200, CaughtBody, null);
            Assert.AreEqual((100, 250, "p-1"), (result.ScoreDelta, result.Score, result.CaughtPlayerId));
        }

        [Test]
        public void Parse_ReturnsMiss_WhenResultIsMiss()
        {
            var result = CaptureResult.Parse(200, "{\"result\":\"miss\",\"scoreDelta\":-10,\"score\":40}", null);
            Assert.AreEqual((CaptureStatus.Miss, -10, 40, (string)null), (result.Status, result.ScoreDelta, result.Score, result.CaughtPlayerId));
        }

        [Test]
        public void Parse_ReturnsGenericError_WhenSuccessBodyIsBroken()
        {
            Assert.AreEqual(CaptureResult.GenericErrorMessage, CaptureResult.Parse(200, "not json", null).Message);
        }

        [Test]
        public void Parse_ReturnsGenericError_WhenResultValueIsUnknown()
        {
            Assert.IsTrue(CaptureResult.Parse(200, "{\"result\":\"x\",\"scoreDelta\":1,\"score\":1}", null).IsError);
        }

        [TestCase(409, "INVALID_GAME_PHASE", "今は捕獲できません")]
        [TestCase(409, "CONFLICT", "ほかの人が先に捕まえました")]
        [TestCase(404, "OBJECT_NOT_FOUND", "そのオブジェクトはもうありません")]
        [TestCase(403, "FORBIDDEN_ROLE", "ケイだけが捕獲できます")]
        [TestCase(500, "SOMETHING_ELSE", CaptureResult.GenericErrorMessage)]
        public void Parse_MapsErrorCodeToMessage(int statusCode, string code, string expected)
        {
            var body = "{\"error\":{\"code\":\"" + code + "\",\"message\":\"server detail\"}}";
            Assert.AreEqual(expected, CaptureResult.Parse(statusCode, body, null).Message);
        }

        [Test]
        public void Parse_ReturnsGenericError_WhenNetworkFailed()
        {
            Assert.AreEqual(CaptureResult.GenericErrorMessage, CaptureResult.Parse(0, null, null).Message);
        }

        [Test]
        public void Parse_DoesNotShowServerMessage_WhenCodeIsUnknown()
        {
            var body = "{\"error\":{\"code\":\"X\",\"message\":\"secret detail\"}}";
            StringAssert.DoesNotContain("secret detail", CaptureResult.Parse(500, body, null).Message);
        }

        [Test]
        public void Parse_ReturnsRetryAfterSeconds_WhenRateLimited()
        {
            var result = CaptureResult.Parse(429, "{\"error\":{\"code\":\"RATE_LIMITED\",\"message\":\"m\"}}", 2);
            Assert.AreEqual(2d, result.RetryAfterSeconds.Value);
        }

        [Test]
        public void Parse_MentionsWaitSeconds_WhenRateLimited()
        {
            StringAssert.Contains("2秒", CaptureResult.Parse(429, "", 2).Message);
        }

        [Test]
        public void Parse_HasNoRetryAfter_WhenHeaderMissingOn429()
        {
            Assert.IsNull(CaptureResult.Parse(429, "", null).RetryAfterSeconds);
        }
    }

    public class CaptureCooldownTests
    {
        private double now;

        [SetUp]
        public void SetUp() => now = 100;

        [Test]
        public void CanSend_IsTrue_BeforeAnySend()
        {
            Assert.IsTrue(new CaptureCooldown(() => now).CanSend);
        }

        [Test]
        public void CanSend_IsFalse_WithinInterval()
        {
            var cooldown = new CaptureCooldown(() => now);
            cooldown.MarkSent();
            now += 1.4;
            Assert.IsFalse(cooldown.CanSend);
        }

        [Test]
        public void CanSend_IsTrue_AfterInterval()
        {
            var cooldown = new CaptureCooldown(() => now);
            cooldown.MarkSent();
            now += 1.5;
            Assert.IsTrue(cooldown.CanSend);
        }

        [Test]
        public void CanSend_IsFalse_UntilRetryAfterElapses()
        {
            var cooldown = new CaptureCooldown(() => now);
            cooldown.MarkSent();
            cooldown.ApplyRetryAfter(5);
            now += 4.9;
            Assert.IsFalse(cooldown.CanSend);
        }

        [Test]
        public void CanSend_IsTrue_WhenRetryAfterElapsed()
        {
            var cooldown = new CaptureCooldown(() => now);
            cooldown.MarkSent();
            cooldown.ApplyRetryAfter(5);
            now += 5;
            Assert.IsTrue(cooldown.CanSend);
        }

        [Test]
        public void CanSend_StaysFalse_WhenRetryAfterIsShorterThanInterval()
        {
            var cooldown = new CaptureCooldown(() => now);
            cooldown.MarkSent();
            cooldown.ApplyRetryAfter(0.5);
            now += 1;
            Assert.IsFalse(cooldown.CanSend);
        }
    }

    public class CaptureTapPolicyTests
    {
        [TestCase(GamePhase.Waiting)]
        [TestCase(GamePhase.Preparing)]
        [TestCase(GamePhase.Finished)]
        public void ShouldHandle_IsFalse_WhenNotPlaying(GamePhase phase)
        {
            Assert.IsFalse(CaptureTapPolicy.ShouldHandle(phase, false));
        }

        [Test]
        public void ShouldHandle_IsFalse_WhenPointerIsOverUi()
        {
            Assert.IsFalse(CaptureTapPolicy.ShouldHandle(GamePhase.Playing, true));
        }

        [Test]
        public void ShouldHandle_IsTrue_WhenPlayingAndNotOverUi()
        {
            Assert.IsTrue(CaptureTapPolicy.ShouldHandle(GamePhase.Playing, false));
        }
    }
}
