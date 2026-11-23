#!/usr/bin/env bash
# ステージのマップ（Gaussian Splat の SPZ）を各環境に登録する。登録するものは deploy/stage-maps.tsv に書く。
# 使い方:
#   deploy/zones.sh push <ローカルのディレクトリ> <VM 側の置き場>   SPZ を VM の /opt/bibai/incoming/<置き場>/ に送る
#   deploy/zones.sh register <環境|all> [--yes-prod]          stage-maps.tsv の全部を登録する（同じものは飛ばす）
#   deploy/zones.sh list <環境>                               その環境のステージのマップと URL
# SPZ は https://bibai.t0waxx.com/<環境>/v1/maps/<mapId>/splat.spz（docs/stage-zones.md）
source "$(dirname "$0")/lib.sh"

usage() { sed -n '3,7p' "$0" | sed 's/^# \{0,1\}//'; exit 1; }

# VM 上の SPZ の置き場。中身は git に入れない
INCOMING="$REMOTE_DIR/incoming"
# 全マップ共通の置き方。ゾーンは Y が下向きで入っているので、X 軸まわりに 180° 回して天地を直す
STAGE_TRANSFORM='{"position":[0,0,0],"rotation":[1,0,0,0],"scale":[1,1,1]}'

YES_PROD=0
args=()
for a in "$@"; do [[ "$a" == --yes-prod ]] && YES_PROD=1 || args+=("$a"); done
set -- "${args[@]+"${args[@]}"}"
[[ $# -ge 1 ]] || usage

# 一覧と登録の処理を VM に送り、VM の中で動かす（ADMIN_TOKEN を VM の外に出さない）
send_tools() {
  ssh_vm "mkdir -p $INCOMING"
  rsync_vm "$DEPLOY_DIR/stage-maps.tsv" "$DEPLOY_DIR/stage_register.py" "$VM:$INCOMING/"
}

register_one() {
  local e="$1" base; base=$(public_url "$e")
  info "[$e] ステージのマップを登録します: $base"
  ssh_vm "python3 $INCOMING/stage_register.py $base $INCOMING/stage-maps.tsv $INCOMING '$STAGE_TRANSFORM'"
}

case "$1" in
  push)
    [[ $# -eq 3 ]] || usage
    [[ -d "$2" ]] || die "$2 がありません"
    [[ "$3" =~ ^[A-Za-z0-9._-]+$ ]] || die "置き場の名前は英数字・._- にしてください: $3"
    ssh_vm "mkdir -p $INCOMING/$3"
    rsync_vm "$2/" "$VM:$INCOMING/$3/"
    info "VM の $INCOMING/$3 に送りました"
    ;;

  register)
    [[ $# -eq 2 ]] || usage
    send_tools
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
    if m.get(\"splatUrl\"): print(m[\"mapId\"], m[\"name\"], m[\"splatSize\"], m[\"splatUrl\"].split(\"?\")[0])'"
    ;;

  *) usage ;;
esac
