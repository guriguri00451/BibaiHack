using NUnit.Framework;
using Seeker.Sync;

namespace Seeker.Ar.Tests
{
    public class ServerClockTests
    {
        [Test]
        public void RemainingMs_IsAccurate_WhenDeviceClockIsAhead60Seconds()
        {
            var serverNow = 1_000_000L;
            var deviceNow = serverNow + 60_000;
            var clock = new ServerClock(() => deviceNow);
            clock.Observe(serverNow);

            Assert.That(clock.RemainingMs(serverNow + 30_000), Is.EqualTo(30_000));
        }

        [Test]
        public void Observe_UsesSmallestOffset_ToIgnoreNetworkDelay()
        {
            var deviceNow = 0L;
            var clock = new ServerClock(() => deviceNow);
            deviceNow = 1_500; // 遅延 500ms のメッセージ（ズレ 1000 + 遅延 500）
            clock.Observe(1_000 - 1_000 + 0);
            deviceNow = 1_100; // 遅延 100ms のメッセージ
            clock.Observe(100);

            Assert.That(clock.ServerNowMs, Is.EqualTo(100));
        }

        [Test]
        public void RemainingMs_IsNeverNegative()
        {
            var clock = new ServerClock(() => 10_000);

            Assert.That(clock.RemainingMs(1_000), Is.EqualTo(0));
        }

        [Test]
        public void ServerNowMs_FallsBackToDeviceClock_BeforeFirstObserve()
        {
            var clock = new ServerClock(() => 777);

            Assert.That(clock.ServerNowMs, Is.EqualTo(777));
        }

        [Test]
        public void Format_ShowsZero_WhenTimeIsUp()
        {
            Assert.That(TimerFormat.Format(0), Is.EqualTo("00:00"));
        }

        [Test]
        public void Format_RoundsUp_PartialSecond()
        {
            Assert.That(TimerFormat.Format(61_500), Is.EqualTo("01:02"));
        }

        [Test]
        public void IsWarning_IsTrueAtTenSeconds()
        {
            Assert.That(TimerFormat.IsWarning(10_000), Is.True);
        }

        [Test]
        public void IsWarning_IsFalseAboveTenSeconds()
        {
            Assert.That(TimerFormat.IsWarning(10_001), Is.False);
        }

        [Test]
        public void FormatNextRound_ShowsHostWait_WhenNotScheduled()
        {
            Assert.That(TimerFormat.FormatNextRound(null), Is.EqualTo("ホストの開始を待っています"));
        }

        [Test]
        public void FormatNextRound_ShowsSecondsRoundedUp()
        {
            Assert.That(TimerFormat.FormatNextRound(4_200), Is.EqualTo("次のラウンドまで 5 秒"));
        }

        [Test]
        public void FormatNextRound_ShowsStartingSoon_WhenTimeIsUp()
        {
            // 0 秒でもクライアントからは進めない。サーバーの phase_changed を待つ間の文言
            Assert.That(TimerFormat.FormatNextRound(0), Is.EqualTo("まもなく次のラウンドが始まります"));
        }

        [Test]
        public void ServerMessage_ReadsTimestamp()
        {
            var message = ServerMessage.Parse("{\"type\":\"x\",\"timestamp\":123,\"payload\":{}}");

            Assert.That(message.Timestamp, Is.EqualTo(123L));
        }
    }
}
