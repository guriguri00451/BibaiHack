import type { OpenAPIHono } from "@hono/zod-openapi";
import type { Context } from "hono";
import { ASSET_CONTENT_TYPE, ASSET_KEY_PATTERN, ASSET_MAX_BYTES } from "../contracts/rest.ts";
import { LOCAL_ASSET_PATH, type AssetStorage, type LocalAssetStorage } from "../storage.ts";
import { ApiError } from "./errors.ts";

export function isLocalAssetStorage(storage: AssetStorage | undefined): storage is LocalAssetStorage {
  return !!storage && "verify" in storage;
}

/** 署名付き URL の先。認証は署名そのもの（Bearer は付けられない: Unity の UnityWebRequest も S3 と同じ流儀で叩くため） */
export function registerLocalAssetRoutes(app: OpenAPIHono, storage: LocalAssetStorage) {
  function authorize(c: Context, method: "PUT" | "GET"): string {
    const key = c.req.path.slice(LOCAL_ASSET_PATH.length + 1);
    const expires = Number(c.req.query("expires"));
    // キー形式を先に弾く: 署名が正しくても、パターン外のキーでディスクを触らない
    if (!ASSET_KEY_PATTERN.test(key) || !storage.verify(method, key, expires, c.req.query("sig") ?? "")) {
      throw new ApiError(403, "INVALID_SIGNATURE", "The signed URL is invalid or expired.");
    }
    return key;
  }

  app.put(`${LOCAL_ASSET_PATH}/*`, async (c) => {
    const key = authorize(c, "PUT");
    if (c.req.header("content-type") !== ASSET_CONTENT_TYPE) {
      throw new ApiError(400, "INVALID_REQUEST", `Content-Type must be ${ASSET_CONTENT_TYPE}.`);
    }
    // 申告サイズで先に弾き、実際に読んだ量でも確認する（申告が嘘でもメモリを使い切らせない）
    if (Number(c.req.header("content-length") ?? 0) > ASSET_MAX_BYTES) {
      throw new ApiError(413, "ASSET_TOO_LARGE", `Asset must be ${ASSET_MAX_BYTES} bytes or less.`);
    }
    const bytes = new Uint8Array(await c.req.arrayBuffer());
    if (bytes.length === 0 || bytes.length > ASSET_MAX_BYTES) {
      throw new ApiError(413, "ASSET_TOO_LARGE", `Asset must be 1 to ${ASSET_MAX_BYTES} bytes.`);
    }
    // GLB の先頭 4 バイトは "glTF"
    if (new TextDecoder().decode(bytes.subarray(0, 4)) !== "glTF") {
      throw new ApiError(400, "INVALID_ASSET", "The body is not a GLB file.");
    }
    await storage.save(key, bytes);
    return c.body(null, 200);
  });

  app.get(`${LOCAL_ASSET_PATH}/*`, (c) => {
    const file = storage.load(authorize(c, "GET"));
    if (!file) throw new ApiError(404, "ASSET_NOT_FOUND", "Asset has not been uploaded yet.");
    return new Response(file, { headers: { "content-type": ASSET_CONTENT_TYPE } });
  });
}
