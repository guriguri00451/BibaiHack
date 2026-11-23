#!/usr/bin/env bash
# 使い方:
#   deploy/deploy.sh <環境> <ブランチ|コミット> [--yes-prod]   その環境にデプロイする
#   deploy/deploy.sh all <ブランチ|コミット> [--yes-prod]       全環境に同じものを入れる
#   deploy/deploy.sh status                                   環境ごとの中身と状態
#   deploy/deploy.sh logs <環境> [行数]                        サーバーのログ
source "$(dirname "$0")/lib.sh"

usage() { sed -n '2,6p' "$0" | sed 's/^# \{0,1\}//'; exit 1; }

YES_PROD=0
args=()
for a in "$@"; do [[ "$a" == --yes-prod ]] && YES_PROD=1 || args+=("$a"); done
set -- "${args[@]+"${args[@]}"}"
[[ $# -ge 1 ]] || usage

# compose・トンネル設定・.env を VM に揃える。トンネル設定が変わったら cloudflared を再起動する
sync_infra() {
  "$DEPLOY_DIR/render.sh"
  ssh_vm "mkdir -p $REMOTE_DIR/cloudflared $REMOTE_DIR/envs"
  local before after
  before=$(ssh_vm "sha256sum $REMOTE_DIR/cloudflared/config.yml 2>/dev/null || true")
  rsync_vm "$DEPLOY_DIR/compose.yaml" "$VM:$REMOTE_DIR/compose.yaml"
  rsync_vm "$DEPLOY_DIR/cloudflared/config.yml" "$VM:$REMOTE_DIR/cloudflared/config.yml"
  after=$(ssh_vm "sha256sum $REMOTE_DIR/cloudflared/config.yml")

  # 秘密情報は VM 上でだけ作る。足りない鍵だけ足し、既存の値は変えない
  # ADMIN_TOKEN は管理画面（https://…/<環境>/admin）のトークン。全環境で同じ値
  local vars="POSTGRES_PASSWORD ADMIN_TOKEN"
  for e in $(envs); do vars="$vars $(secret_var "$e")"; done
  ssh_vm "cd $REMOTE_DIR && umask 077 && touch .env && for v in $vars; do
    grep -q \"^\$v=\" .env || echo \"\$v=\$(openssl rand -hex 32)\" >> .env; done"

  compose_vm "up -d postgres" >/dev/null 2>&1
  if [[ "$before" != "$after" ]]; then
    info "トンネルの設定が変わったので cloudflared を再起動します"
    compose_vm "up -d --force-recreate cloudflared" >/dev/null 2>&1
  else
    compose_vm "up -d cloudflared" >/dev/null 2>&1
  fi
}

resolve_ref() {
  local ref="$1" c
  git -C "$REPO_DIR" fetch -q origin 2>/dev/null || true
  c=$(git -C "$REPO_DIR" rev-parse -q --verify "$ref^{commit}" 2>/dev/null) ||
    c=$(git -C "$REPO_DIR" rev-parse -q --verify "origin/$ref^{commit}" 2>/dev/null) ||
    die "ブランチかコミットが見つかりません: $ref"
  git -C "$REPO_DIR" cat-file -e "$c:server/Dockerfile" 2>/dev/null ||
    die "$ref には server/ がありません（サーバーが入る前のブランチです）"
  echo "$c"
}

deploy_one() {
  local e="$1" ref="$2" commit="$3"
  local db; db=$(db_name "$e")
  info "[$e] $ref ($(git -C "$REPO_DIR" log -1 --format='%h %s' "$commit")) を入れます"

  # コミット済みの server/ だけを送る（作業中の変更は入らない）
  git -C "$REPO_DIR" archive --format=tar "$commit" server |
    ssh_vm "set -e; d=$REMOTE_DIR/envs/$e; rm -rf \$d/server.new; mkdir -p \$d/server.new;
      tar -x -C \$d/server.new --strip-components=1; rm -rf \$d/server; mv \$d/server.new \$d/server"

  psql_vm "-c \"SELECT 1 FROM pg_database WHERE datname='$db'\"" | grep -q 1 ||
    psql_vm "-c 'CREATE DATABASE $db'"

  compose_vm "up -d --build server-$e" >/dev/null 2>&1 || {
    compose_vm "logs --tail 40 server-$e" || true
    die "[$e] 起動に失敗しました"
  }
  local ok=0
  for _ in $(seq 1 30); do
    if [[ "$(ssh_vm "docker inspect -f '{{.State.Health.Status}}' bibai-server-$e-1" 2>/dev/null)" == healthy ]]; then ok=1; break; fi
    sleep 2
  done
  [[ $ok == 1 ]] || { compose_vm "logs --tail 40 server-$e" || true; die "[$e] healthcheck が通りません"; }

  ssh_vm "printf 'ref=%s\ncommit=%s\ndeployed_at=%s\ndeployed_by=%s\n' '$ref' '$commit' \"\$(date -Iseconds)\" '$(git -C "$REPO_DIR" config user.name)' > $REMOTE_DIR/envs/$e/DEPLOYED"
  local code
  code=$(ssh_vm "curl -s -o /dev/null -w '%{http_code}' $(public_url "$e")/healthz" || true)
  info "[$e] 完了: $(public_url "$e")  (公開 URL の healthz: $code)"
}

case "$1" in
  status)
    ssh_vm "cd $REMOTE_DIR && for d in envs/*/; do e=\$(basename \$d);
      c=\$(grep ^commit= \$d/DEPLOYED 2>/dev/null | cut -c8-14); r=\$(grep ^ref= \$d/DEPLOYED 2>/dev/null | cut -d= -f2-);
      t=\$(grep ^deployed_at= \$d/DEPLOYED 2>/dev/null | cut -d= -f2 | cut -c1-16);
      h=\$(docker inspect -f '{{.State.Health.Status}}' bibai-server-\$e-1 2>/dev/null || echo none);
      printf '%-10s %-9s %-8s %-16s %s\n' \$e \$h \"\$c\" \"\$t\" \"\$r\"; done"
    ;;
  logs)
    [[ $# -ge 2 ]] || usage
    require_env "$2"
    compose_vm "logs --tail ${3:-100} server-$2"
    ;;
  all)
    [[ $# -ge 2 ]] || usage
    guard_prod prod "$YES_PROD"
    commit=$(resolve_ref "$2")
    sync_infra
    for e in $(envs); do deploy_one "$e" "$2" "$commit"; done
    ;;
  *)
    [[ $# -ge 2 ]] || usage
    require_env "$1"
    guard_prod "$1" "$YES_PROD"
    commit=$(resolve_ref "$2")
    sync_infra
    deploy_one "$1" "$2" "$commit"
    ;;
esac
