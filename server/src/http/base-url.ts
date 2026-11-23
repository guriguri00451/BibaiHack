/**
 * 外から見たこの環境の URL（…/a-kun-01 まで）。署名付き URL・modelUrl をここへ向ける。
 * PUBLIC_URL を明示していればそれ、無ければリクエストの Host から作る
 * （手元の LAN で実機から繋いだとき、localhost の URL を配らないように）
 */
export function externalBase(req: Request, opts: { assetBaseUrl?: string; basePath?: string }): string {
  if (opts.assetBaseUrl) return opts.assetBaseUrl;
  const u = new URL(req.url);
  const proto = req.headers.get("x-forwarded-proto") ?? u.protocol.replace(/:$/, "");
  const host = req.headers.get("x-forwarded-host") ?? req.headers.get("host") ?? u.host;
  return `${proto}://${host}${opts.basePath ?? ""}`;
}
