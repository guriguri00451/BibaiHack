import { afterAll, describe, expect, test } from "bun:test";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createLocalStorage } from "../src/storage.ts";
import { JWT_SECRET, startServer } from "./helpers.ts";

const dir = mkdtempSync(join(tmpdir(), "bibai-assets-"));
// publicUrl は起動後にしか分からないので、ポートを後から差し込める形で渡す
let publicUrl = "";
const storage = createLocalStorage({ dir, get publicUrl() { return publicUrl; }, secret: JWT_SECRET });
const s = await startServer({ assetStorage: storage });
publicUrl = s.base;
afterAll(async () => {
  await s.stop();
  rmSync(dir, { recursive: true, force: true });
});

const glb = new Uint8Array([0x67, 0x6c, 0x54, 0x46, 2, 0, 0, 0, 12, 0, 0, 0]);
const put = (url: string, body: Uint8Array | string = glb, type = "model/gltf-binary") =>
  fetch(url, { method: "PUT", headers: { "content-type": type }, body });

async function issueUpload() {
  const { accessToken } = await s.post("/v1/guests", { name: "owner" });
  const session = await s.post("/v1/sessions", { mapId: "m" }, accessToken);
  const upload = await s.post(
    "/v1/assets/uploads",
    { sessionId: session.id, contentType: "model/gltf-binary", sizeBytes: glb.length },
    accessToken,
  );
  return upload as { assetKey: string; uploadUrl: string };
}

describe("ローカルディスクのアセット置き場", () => {
  test("アップロードした GLB を署名付き GET URL で同じ内容のまま取得できる", async () => {
    const { assetKey, uploadUrl } = await issueUpload();
    expect((await put(uploadUrl)).status).toBe(200);

    const downloadUrl = storage.presignDownload(assetKey, 60);
    const res = await fetch(downloadUrl);
    expect(res.status).toBe(200);
    expect(new Uint8Array(await res.arrayBuffer())).toEqual(glb);
  });

  test("署名を改ざんしたアップロードは 403", async () => {
    const { uploadUrl } = await issueUpload();
    expect((await put(uploadUrl.replace(/sig=./, "sig=0"))).status).toBe(403);
  });

  test("PUT 用の署名では GET できない", async () => {
    const { uploadUrl } = await issueUpload();
    await put(uploadUrl);
    expect((await fetch(uploadUrl)).status).toBe(403);
  });

  test("期限切れの URL は 403", async () => {
    const { assetKey } = await issueUpload();
    expect((await fetch(storage.presignDownload(assetKey, -1))).status).toBe(403);
  });

  test("GLB でない中身は 400", async () => {
    const { uploadUrl } = await issueUpload();
    expect((await put(uploadUrl, "not a glb")).status).toBe(400);
  });

  test("Content-Type が違うと 400", async () => {
    const { uploadUrl } = await issueUpload();
    expect((await put(uploadUrl, glb, "text/plain")).status).toBe(400);
  });

  test("アップロード前の GET は 404", async () => {
    const { assetKey } = await issueUpload();
    expect((await fetch(storage.presignDownload(assetKey, 60))).status).toBe(404);
  });
});
