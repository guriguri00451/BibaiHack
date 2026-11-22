using System.IO;
using Newtonsoft.Json.Linq;
using Seeker.Sync;
using UnityEngine;

namespace Seeker.App
{
    /// <summary>
    /// 開発ビルドで、ゲーム進行側が無くても同期を試せるようにする。persistentDataPath/dev-connection.json に
    /// {"baseUrl","sessionId","accessToken"} を置くと読む（adb push で渡す。トークンをビルドに埋め込まない）。
    /// リリースビルドでは読まない。
    /// </summary>
    public static class DevConnectionFile
    {
        private const string FileName = "dev-connection.json";

        public static bool TryRead(out SyncConnection connection)
        {
            connection = null;
            if (!Debug.isDebugBuild) return false;
            var path = Path.Combine(Application.persistentDataPath, FileName);
            if (!File.Exists(path)) return false;

            try
            {
                var json = JObject.Parse(File.ReadAllText(path));
                connection = SyncConnection.FromBaseUrl((string)json["baseUrl"], (string)json["sessionId"], (string)json["accessToken"]);
                return true;
            }
            catch (System.Exception exception) when (exception is Newtonsoft.Json.JsonException || exception is System.ArgumentException)
            {
                Debug.LogWarning($"[DevConnection] {FileName} が不正で読めない: {exception.Message}");
                return false;
            }
        }
    }
}
