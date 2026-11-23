namespace Seeker.App
{
    /// <summary>
    /// サーバーブラウザの一覧（GET /v1/admin/sessions）に使う管理トークン。
    /// ハッカソン中の割り切りでコードに直書きしている。APK から読めるので、終わったらサーバーの ADMIN_TOKEN を替えること
    /// </summary>
    internal static class AdminToken
    {
        public const string Value = "7c1afcf989ed5db8664bdf606c5914d6a0577346a70364fa42f94dd465533fa4";
    }
}
