import { createHmac, timingSafeEqual } from "node:crypto";

/**
 * サーバー自身が配る期限付き署名 URL（仕様書 5.1・9章「署名付きアップロードURLには短い期限とサイズ上限」）。
 *   PUT <base>/v1/storage/<key>?exp=&ct=&size=&owner=&sig=   アップロード
 *   GET <base>/v1/storage/<key>?exp=&sig=                    ダウンロード
 * 署名は method・key・期限・（アップロードなら）形式・サイズ・所有者をまとめて HMAC-SHA256 する。
 */
export type UploadGrant = { key: string; exp: number; contentType: string; size: number; ownerPlayerId: string };
export type DownloadGrant = { key: string; exp: number };

export class UrlSigner {
  private readonly secret: string;

  constructor(jwtSecret: string) {
    // トークンの鍵をそのまま使わず、用途を混ぜて別の鍵にする
    this.secret = createHmac("sha256", jwtSecret).update("bibai-storage-url").digest("hex");
  }

  private mac(parts: (string | number)[]) {
    return createHmac("sha256", this.secret).update(parts.join("\n")).digest("base64url");
  }

  private static same(a: string, b: string) {
    const x = Buffer.from(a);
    const y = Buffer.from(b);
    return x.length === y.length && timingSafeEqual(x, y);
  }

  uploadQuery(g: UploadGrant): string {
    const sig = this.mac(["PUT", g.key, g.exp, g.contentType, g.size, g.ownerPlayerId]);
    return new URLSearchParams({
      exp: String(g.exp),
      ct: g.contentType,
      size: String(g.size),
      owner: g.ownerPlayerId,
      sig,
    }).toString();
  }

  downloadQuery(g: DownloadGrant): string {
    return new URLSearchParams({ exp: String(g.exp), sig: this.mac(["GET", g.key, g.exp]) }).toString();
  }

  /** 署名が正しく期限内なら中身を返す */
  verifyUpload(key: string, q: URLSearchParams, nowSec = Date.now() / 1000): UploadGrant | null {
    const exp = Number(q.get("exp"));
    const size = Number(q.get("size"));
    const contentType = q.get("ct") ?? "";
    const ownerPlayerId = q.get("owner") ?? "";
    const sig = q.get("sig") ?? "";
    if (!Number.isInteger(exp) || !Number.isInteger(size) || exp < nowSec) return null;
    if (!UrlSigner.same(sig, this.mac(["PUT", key, exp, contentType, size, ownerPlayerId]))) return null;
    return { key, exp, contentType, size, ownerPlayerId };
  }

  verifyDownload(key: string, q: URLSearchParams, nowSec = Date.now() / 1000): boolean {
    const exp = Number(q.get("exp"));
    if (!Number.isInteger(exp) || exp < nowSec) return false;
    return UrlSigner.same(q.get("sig") ?? "", this.mac(["GET", key, exp]));
  }
}
