using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Seeker.Sync.Lobby;

namespace Seeker.Ar.Tests
{
    public class LobbyTests
    {
        private const string BaseUrl = "https://example.com/env";

        private sealed class MemoryStore : IKeyValueStore
        {
            public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
            public string Get(string key) => Values.TryGetValue(key, out var value) ? value : null;
            public void Set(string key, string value) => Values[key] = value;
            public void Delete(string key) => Values.Remove(key);
        }

        private static string Session(string id, string createdAt) =>
            $"{{\"id\":\"{id}\",\"status\":\"waiting\",\"round\":1,\"mapId\":\"m\",\"hostName\":\"H\",\"playerCount\":2,\"connectedCount\":1,\"phaseEndsAt\":null,\"createdAt\":\"{createdAt}\"}}";

        // ---------- 一覧 ----------

        [Test]
        public void Parse_SortsNewestFirst_EvenIfServerOrderDiffers()
        {
            var body = $"{{\"sessions\":[{Session("old", "2026-11-23T01:00:00.000Z")},{Session("new", "2026-11-23T02:00:00.000Z")}]}}";
            var ids = new List<string>();
            foreach (var s in SessionList.Parse(body)) ids.Add(s.Id);
            Assert.That(ids, Is.EqualTo(new[] { "new", "old" }));
        }

        [Test]
        public void Parse_ReadsCounts()
        {
            var body = $"{{\"sessions\":[{Session("a", "2026-11-23T01:00:00.000Z")}]}}";
            var session = SessionList.Parse(body)[0];
            Assert.That((session.HostName, session.PlayerCount, session.ConnectedCount), Is.EqualTo(("H", 2, 1)));
        }

        [Test]
        public void Parse_DropsEntriesWithoutId()
        {
            Assert.That(SessionList.Parse("{\"sessions\":[{\"status\":\"waiting\"}]}"), Is.Empty);
        }

        [Test]
        public void Parse_ReturnsEmpty_WhenSessionsIsMissing()
        {
            Assert.That(SessionList.Parse("{}"), Is.Empty);
        }

        [TestCase("waiting", "待機中")]
        [TestCase("preparing", "準備中")]
        [TestCase("playing", "本番中")]
        [TestCase("finished", "結果")]
        [TestCase("unknown", "不明")]
        [TestCase(null, "不明")]
        public void StatusLabel_MapsServerStatus(string status, string expected)
        {
            Assert.That(SessionStatusLabel.Of(status), Is.EqualTo(expected));
        }

        // ---------- エラー ----------

        [TestCase(0, null, LobbyErrorKind.Unreachable)]
        [TestCase(401, "{\"error\":{\"code\":\"UNAUTHORIZED\"}}", LobbyErrorKind.Unauthorized)]
        [TestCase(404, "{\"error\":{\"code\":\"SESSION_NOT_FOUND\"}}", LobbyErrorKind.SessionNotFound)]
        [TestCase(404, "<html>not found</html>", LobbyErrorKind.Other)]
        [TestCase(500, "", LobbyErrorKind.Other)]
        public void Classify_ByStatusAndCode(long statusCode, string body, LobbyErrorKind expected)
        {
            Assert.That(LobbyErrors.Classify(statusCode, body), Is.EqualTo(expected));
        }

        // ---------- 保存値 ----------

        [TestCase("  https://example.com/env/  ", "https://example.com/env")]
        [TestCase("http://localhost:3000", "http://localhost:3000")]
        public void NormalizeBaseUrl_TrimsSpacesAndTrailingSlash(string input, string expected)
        {
            LobbySettings.TryNormalizeBaseUrl(input, out var normalized);
            Assert.That(normalized, Is.EqualTo(expected));
        }

        [TestCase("")]
        [TestCase(null)]
        [TestCase("example.com")]
        [TestCase("ftp://example.com")]
        public void NormalizeBaseUrl_RejectsNonHttpUrls(string input)
        {
            Assert.That(LobbySettings.TryNormalizeBaseUrl(input, out _), Is.False);
        }

        [TestCase("", false)]
        [TestCase("   ", false)]
        [TestCase("a", true)]
        [TestCase("12345678901234567890123456789012", true)]
        [TestCase("123456789012345678901234567890123", false)]
        public void NormalizePlayerName_AcceptsOneTo32Chars(string input, bool expected)
        {
            Assert.That(LobbySettings.TryNormalizePlayerName(input, out _), Is.EqualTo(expected));
        }

        [Test]
        public void Token_IsKeptPerBaseUrl()
        {
            var settings = new LobbySettings(new MemoryStore());
            settings.SaveToken(BaseUrl, "t1");
            Assert.That(settings.GetToken("https://other.example.com"), Is.Null);
        }

        // ---------- トークンの使い回し ----------

        [Test]
        public async Task Run_ReusesSavedToken_WithoutCreatingGuest()
        {
            var settings = new LobbySettings(new MemoryStore());
            settings.SaveToken(BaseUrl, "saved");
            var created = 0;
            var provider = new GuestTokenProvider(settings, BaseUrl, () => { created++; return Task.FromResult("new"); });
            await provider.RunAsync(token => Task.FromResult(token));
            Assert.That(created, Is.EqualTo(0));
        }

        [Test]
        public async Task Run_CreatesAndSavesGuest_WhenNoToken()
        {
            var settings = new LobbySettings(new MemoryStore());
            var provider = new GuestTokenProvider(settings, BaseUrl, () => Task.FromResult("new"));
            await provider.RunAsync(token => Task.FromResult(token));
            Assert.That(settings.GetToken(BaseUrl), Is.EqualTo("new"));
        }

        [Test]
        public async Task Run_RenewsTokenOnce_On401()
        {
            var settings = new LobbySettings(new MemoryStore());
            settings.SaveToken(BaseUrl, "stale");
            var provider = new GuestTokenProvider(settings, BaseUrl, () => Task.FromResult("fresh"));
            var used = await provider.RunAsync(token => token == "stale"
                ? Task.FromException<string>(new LobbyException(LobbyErrorKind.Unauthorized, "401"))
                : Task.FromResult(token));
            Assert.That(used, Is.EqualTo("fresh"));
        }

        [Test]
        public void Run_DoesNotRetry_WhenFreshTokenIsRejected()
        {
            var settings = new LobbySettings(new MemoryStore());
            var created = 0;
            var provider = new GuestTokenProvider(settings, BaseUrl, () => { created++; return Task.FromResult($"t{created}"); });
            Assert.ThrowsAsync<LobbyException>(() => provider.RunAsync<string>(
                _ => Task.FromException<string>(new LobbyException(LobbyErrorKind.Unauthorized, "401"))));
            Assert.That(created, Is.EqualTo(1));
        }
    }
}
