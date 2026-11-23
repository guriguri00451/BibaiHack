"""VM の上で動かす。deploy/stage-maps.tsv のマップを、1つの環境に管理画面の API で登録する（deploy/zones.sh から呼ぶ）。

    python3 stage_register.py <環境の URL> <stage-maps.tsv> <SPZ の置き場> <transform の JSON>

- まだ無い・SPZ の大きさが違う → SPZ ごと送る（POST /v1/admin/maps）
- 名前か置き方だけ違う → 定義だけ直す（PATCH /v1/admin/maps/<mapId>）
- 同じ → 何もしない
ADMIN_TOKEN は /opt/bibai/.env から読み、表示しない。
"""

import json
import os
import subprocess
import sys
import urllib.request

base, manifest, root, transform_json = sys.argv[1:5]
transform = json.loads(transform_json)

token = next(
    line.split("=", 1)[1].strip()
    for line in open("/opt/bibai/.env")
    if line.startswith("ADMIN_TOKEN=")
)
if not token:
    sys.exit("ADMIN_TOKEN がありません（deploy.sh を一度流してください）")
# Cloudflare が Python の既定の User-Agent（Python-urllib）を 403 で弾くので、名前を付ける
auth = {"authorization": f"Bearer {token}", "user-agent": "bibai-deploy/1"}


def api(method, path, body=None):
    req = urllib.request.Request(base + "/v1/admin" + path, method=method, headers=dict(auth))
    if body is not None:
        req.data = json.dumps(body).encode()
        req.add_header("content-type", "application/json")
    with urllib.request.urlopen(req, timeout=60) as res:
        return json.loads(res.read() or b"null")


have = {m["mapId"]: m for m in api("GET", "/maps")["maps"]}

for line in open(manifest, encoding="utf-8"):
    if not line.strip() or line.startswith("#"):
        continue
    map_id, rel, name = line.rstrip("\n").split("\t")
    path = os.path.join(root, rel)
    size = os.path.getsize(path)
    cur = have.get(map_id)
    if cur is None or cur.get("splatSize") != size:
        # multipart は curl に任せる（トークンはヘッダで渡し、コマンドラインに残さないよう -H @- で読ませる）
        out = subprocess.run(
            ["curl", "-s", "-o", "/dev/stderr", "-w", "%{http_code}", "-H", "@-",
             "-F", f"mapId={map_id}", "-F", f"name={name}", "-F", f"transform={json.dumps(transform)}",
             "-F", f"splat=@{path}", base + "/v1/admin/maps"],
            input=f"authorization: Bearer {token}\n", capture_output=True, text=True,
        )
        if out.stdout != "201":
            sys.exit(f"  {map_id}: 失敗 HTTP {out.stdout} {out.stderr[:300]}")
        print(f"  {map_id}: SPZ を送りました（{size} バイト）")
    elif cur.get("name") != name or cur.get("transform") != transform:
        api("PATCH", f"/maps/{map_id}", {"name": name, "transform": transform})
        print(f"  {map_id}: 名前・置き方を直しました")
    else:
        print(f"  {map_id}: そのまま")
