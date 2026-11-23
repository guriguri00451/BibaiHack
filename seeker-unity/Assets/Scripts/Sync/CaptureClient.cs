using System;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace Seeker.Sync
{
    /// <summary>POST {RestBaseUrl}/sessions/{SessionId}/captures を送る。トークンはログに出さない。</summary>
    public static class CaptureClient
    {
        public static async Task<CaptureResult> SendAsync(SyncConnection connection, string objectId)
        {
            var url = $"{connection.RestBaseUrl}/sessions/{Uri.EscapeDataString(connection.SessionId)}/captures";
            var payload = Encoding.UTF8.GetBytes($"{{\"objectId\":\"{objectId}\"}}");
            using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(payload);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", "Bearer " + connection.AccessToken);

                var completion = new TaskCompletionSource<bool>();
                request.SendWebRequest().completed += _ => completion.TrySetResult(true);
                await completion.Task;

                // 通信失敗（接続できない等）は statusCode 0 として汎用文言にまとめる
                if (request.result == UnityWebRequest.Result.ConnectionError) return CaptureResult.Parse(0, null, null);
                double? retryAfter = null;
                var header = request.GetResponseHeader("Retry-After");
                if (double.TryParse(header, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) retryAfter = seconds;
                return CaptureResult.Parse(request.responseCode, request.downloadHandler.text, retryAfter);
            }
        }
    }
}
