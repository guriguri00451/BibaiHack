# deploy.sh と db.sh の共通部分。直接は実行しない
set -euo pipefail

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$DEPLOY_DIR/.." && pwd)"
PUBLIC_HOST="bibai.t0waxx.com"
# bibai-game VM（wds-pve VMID 100）。VM は tailnet に居ないので wds-pve を踏み台にする
VM="${BIBAI_VM:-towa@192.168.11.2}"
JUMP="${BIBAI_JUMP:-root@wds-pve}"
REMOTE_DIR="/opt/bibai"

die() { echo "error: $*" >&2; exit 1; }
info() { echo "==> $*" >&2; }

envs() { grep -vE '^\s*(#|$)' "$DEPLOY_DIR/envs.txt"; }
env_exists() { envs | grep -qx -- "$1"; }
require_env() { env_exists "$1" || die "知らない環境です: $1（deploy/envs.txt を見てください）"; }
# 前置き env_ を付ける。素の "dev" だと Bun.sql が接続に失敗する（Bun 1.3.14 で確認）
db_name() { echo "env_${1//-/_}"; }
base_path() { [[ "$1" == prod ]] && echo "" || echo "/$1"; }
# macOS の bash 3.2 でも動くよう tr で大文字にする
secret_var() { echo "JWT_SECRET_$(echo "${1//-/_}" | tr "[:lower:]" "[:upper:]")"; }
public_url() { echo "https://$PUBLIC_HOST$(base_path "$1")"; }

# 本番を書き換える操作は --yes-prod が無ければ止める
guard_prod() {
  local target="$1" yes="$2"
  if [[ "$target" == prod && "$yes" != 1 ]]; then
    die "prod を書き換える操作です。本当に行うなら --yes-prod を付けてください"
  fi
}

ssh_vm() { ssh -o BatchMode=yes -J "$JUMP" "$VM" "$@"; }
rsync_vm() { rsync -az -e "ssh -o BatchMode=yes -J $JUMP" "$@"; }
# VM 上の compose を呼ぶ
compose_vm() { ssh_vm "cd $REMOTE_DIR && docker compose $*"; }
# VM 上の postgres コンテナで psql を実行する（管理用 DB に繋ぐ）
psql_vm() { ssh_vm "cd $REMOTE_DIR && docker compose exec -T postgres psql -U bibai -d bibai -v ON_ERROR_STOP=1 -qtA $*"; }
