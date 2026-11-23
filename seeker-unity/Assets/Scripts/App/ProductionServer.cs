namespace Seeker.App
{
    /// <summary>
    /// サーバーブラウザがつなぐ本番環境。ハッカソン中の割り切りで接続先と管理トークンをコードに直書きしている。
    /// 管理トークンは一覧（GET /v1/admin/sessions）だけに使う。APK から読めるので、終わったらサーバーの ADMIN_TOKEN を替えること
    /// </summary>
    internal static class ProductionServer
    {
        public const string BaseUrl = "https://bibai.t0waxx.com";
        public const string AdminToken = "7c1afcf989ed5db8664bdf606c5914d6a0577346a70364fa42f94dd465533fa4";
    }
}
