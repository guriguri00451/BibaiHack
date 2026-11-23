#!/usr/bin/env bash
# 美唄のゾーン（Gaussian Splat の SPZ）をマップとして各環境に登録する。
# 使い方:
#   deploy/zones.sh push <ローカルのディレクトリ>          A.spz…H.spz・rest.spz・zones.json を VM に送る
#   deploy/zones.sh register <環境|all> [--yes-prod]      VM のゾーンを管理画面の API で登録する（同じ大きさのものは飛ばす）
#   deploy/zones.sh list <環境>                           その環境のゾーンのマップと URL
# マップの ID は bibai-zone-<a〜h|rest>。SPZ は https://bibai.t0waxx.com/<環境>/v1/maps/<ID>/splat.spz
source "$(dirname "$0")/lib.sh"

usage() { sed -n '3,7p' "$0" | sed 's/^# \{0,1\}//'; exit 1; }

# VM 上のゾーンの置き場。中身は git に入れない（合わせて約 150MB）
ZONES_DIR="$REMOTE_DIR/incoming/zones_wide"
ZONE_IDS="A B C D E F G H rest"

YES_PROD=0
args=()
for a in "$@"; do [[ "$a" == --yes-prod ]] && YES_PROD=1 || args+=("$a"); done
set -- "${args[@]+"${args[@]}"}"
[[ $# -ge 1 ]] || usage

# 1つの環境に登録する。ADMIN_TOKEN は VM の .env から VM の中で読み、外に出さない
register_one() {
  local e="$1" base; base=$(public_url "$e")
  info "[$e] ゾーンを登録します: $base"
  ssh_vm "set -eu; cd $ZONES_DIR; T=\$(grep '^ADMIN_TOKEN=' $REMOTE_DIR/.env | cut -d= -f2-)
    [ -n \"\$T\" ] || { echo 'ADMIN_TOKEN がありません（deploy.sh を一度流してください）' >&2; exit 1; }
    curl -sf -H \"authorization: Bearer \$T\" $base/v1/admin/maps > /tmp/zones-maps-$e.json
    for f in $ZONE_IDS; do
      id=bibai-zone-\$(echo \$f | tr A-Z a-z)
      size=\$(stat -c %s \$f.spz)
      have=\$(python3 -c 'import json,sys; m=[x for x in json.load(open(sys.argv[1]))[\"maps\"] if x[\"mapId\"]==sys.argv[2]]; print(m[0][\"splatSize\"] if m else \"\")' /tmp/zones-maps-$e.json \$id)
      if [ \"\$have\" = \"\$size\" ]; then echo \"  \$id: 登録済み（\$size バイト）\"; continue; fi
      name=\$(python3 -c 'import json,sys; z={x[\"id\"]:x[\"label\"] for x in json.load(open(\"zones.json\"))[\"zones\"]}; print(\"美唄 \" + z.get(sys.argv[1], \"その他\") + \"（\" + sys.argv[1] + \"）\")' \$f)
      code=\$(curl -s -o /tmp/zones-up-$e.json -w '%{http_code}' -H \"authorization: Bearer \$T\" \
        -F mapId=\$id -F \"name=\$name\" -F splat=@\$f.spz $base/v1/admin/maps)
      [ \"\$code\" = 201 ] || { echo \"  \$id: 失敗 HTTP \$code \$(cat /tmp/zones-up-$e.json)\" >&2; exit 1; }
      echo \"  \$id: 登録しました（\$size バイト）\"
    done
    rm -f /tmp/zones-maps-$e.json /tmp/zones-up-$e.json"
}

case "$1" in
  push)
    [[ $# -eq 2 ]] || usage
    for f in $ZONE_IDS; do [[ -f "$2/$f.spz" ]] || die "$2/$f.spz がありません"; done
    [[ -f "$2/zones.json" ]] || die "$2/zones.json がありません"
    ssh_vm "mkdir -p $ZONES_DIR"
    rsync_vm "$2/" "$VM:$ZONES_DIR/"
    info "VM の $ZONES_DIR に送りました"
    ;;

  register)
    [[ $# -eq 2 ]] || usage
    if [[ "$2" == all ]]; then
      guard_prod prod "$YES_PROD"
      for e in $(envs); do register_one "$e"; done
    else
      require_env "$2"; guard_prod "$2" "$YES_PROD"
      register_one "$2"
    fi
    ;;

  list)
    [[ $# -eq 2 ]] || usage
    require_env "$2"
    base=$(public_url "$2")
    ssh_vm "T=\$(grep '^ADMIN_TOKEN=' $REMOTE_DIR/.env | cut -d= -f2-)
      curl -sf -H \"authorization: Bearer \$T\" $base/v1/admin/maps |
      python3 -c 'import json,sys
for m in json.load(sys.stdin)[\"maps\"]:
    if m[\"mapId\"].startswith(\"bibai-zone-\"): print(m[\"mapId\"], m[\"name\"], m[\"splatSize\"], m[\"splatUrl\"].split(\"?\")[0])'"
    ;;

  *) usage ;;
esac
