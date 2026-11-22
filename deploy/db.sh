#!/usr/bin/env bash
# 使い方:
#   deploy/db.sh list                              環境ごとの DB と大きさ
#   deploy/db.sh reset <環境> [--yes-prod]          DB を空にして作り直す（起動時にマイグレーションが当たる）
#   deploy/db.sh copy <元> <先> [--yes-prod]        元の DB の中身で先を置き換える
#   deploy/db.sh dump <環境> [ファイル]              DB を書き出す（既定 deploy/backups/<環境>-<日時>.dump）
#   deploy/db.sh restore <環境> <ファイル> [--yes-prod]  書き出したファイルで置き換える
#   deploy/db.sh psql <環境> [SQL]                  psql で入る。SQL を渡せばそれだけ実行する
source "$(dirname "$0")/lib.sh"

usage() { sed -n '2,8p' "$0" | sed 's/^# \{0,1\}//'; exit 1; }

YES_PROD=0
args=()
for a in "$@"; do [[ "$a" == --yes-prod ]] && YES_PROD=1 || args+=("$a"); done
set -- "${args[@]+"${args[@]}"}"
[[ $# -ge 1 ]] || usage

# 書き換える間はその環境のサーバーを止める（メモリ上の状態も捨てる）
stop_server() { compose_vm "stop server-$1" >/dev/null 2>&1 || true; }
start_server() {
  compose_vm "up -d server-$1" >/dev/null 2>&1
  info "[$1] サーバーを起動しました: $(public_url "$1")"
}
recreate_db() {
  local db; db=$(db_name "$1")
  psql_vm "-c 'DROP DATABASE IF EXISTS $db WITH (FORCE)'"
  psql_vm "-c 'CREATE DATABASE $db'"
}
pg_vm() { ssh_vm "cd $REMOTE_DIR && docker compose exec -T postgres $*"; }

case "$1" in
  list)
    psql_vm "-F ' ' -c \"SELECT datname, pg_size_pretty(pg_database_size(datname)) FROM pg_database WHERE datname LIKE 'env\\_%' ORDER BY 1\""
    ;;

  reset)
    [[ $# -eq 2 ]] || usage
    require_env "$2"; guard_prod "$2" "$YES_PROD"
    stop_server "$2"
    recreate_db "$2"
    info "[$2] DB $(db_name "$2") を空にしました"
    start_server "$2"
    ;;

  copy)
    [[ $# -eq 3 ]] || usage
    require_env "$2"; require_env "$3"; guard_prod "$3" "$YES_PROD"
    [[ "$2" != "$3" ]] || die "元と先が同じです"
    src=$(db_name "$2"); dst=$(db_name "$3")
    stop_server "$3"
    recreate_db "$3"
    # 元は止めずに pg_dump する（一貫したスナップショットが取れる）
    pg_vm "sh -c 'pg_dump -U bibai -Fc $src | pg_restore -U bibai -d $dst --no-owner --exit-on-error'"
    info "[$3] $2 の中身で置き換えました（トークンは環境ごとに違うので、ゲスト作成からやり直してください）"
    start_server "$3"
    ;;

  dump)
    [[ $# -ge 2 ]] || usage
    require_env "$2"
    mkdir -p "$DEPLOY_DIR/backups"
    out="${3:-$DEPLOY_DIR/backups/$2-$(date +%Y%m%d-%H%M%S).dump}"
    pg_vm "pg_dump -U bibai -Fc $(db_name "$2")" >"$out"
    info "[$2] 書き出しました: $out ($(du -h "$out" | cut -f1))"
    ;;

  restore)
    [[ $# -eq 3 ]] || usage
    require_env "$2"; guard_prod "$2" "$YES_PROD"
    [[ -s "$3" ]] || die "ファイルがありません: $3"
    stop_server "$2"
    recreate_db "$2"
    pg_vm "pg_restore -U bibai -d $(db_name "$2") --no-owner --exit-on-error" <"$3"
    info "[$2] $3 から戻しました"
    start_server "$2"
    ;;

  psql)
    [[ $# -ge 2 ]] || usage
    require_env "$2"
    if [[ $# -ge 3 ]]; then
      pg_vm "psql -U bibai -d $(db_name "$2") -v ON_ERROR_STOP=1 -c $(printf '%q' "$3")"
    else
      ssh -t -J "$JUMP" "$VM" "cd $REMOTE_DIR && docker compose exec postgres psql -U bibai -d $(db_name "$2")"
    fi
    ;;

  *) usage ;;
esac
