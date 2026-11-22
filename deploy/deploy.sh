#!/usr/bin/env bash
# server/ と deploy/ を bibai-game VM へ送り、作り直して起動する
set -euo pipefail
cd "$(dirname "$0")/.."
VM="${VM:-towa@192.168.11.2}"
SSH=(ssh -o BatchMode=yes -J root@wds-pve "$VM")

rsync -az --delete -e "ssh -o BatchMode=yes -J root@wds-pve" \
  --exclude node_modules --exclude .pglite --exclude test \
  server/ "$VM:/opt/bibai/server/"
rsync -az -e "ssh -o BatchMode=yes -J root@wds-pve" \
  deploy/compose.yaml "$VM:/opt/bibai/compose.yaml"
rsync -az -e "ssh -o BatchMode=yes -J root@wds-pve" \
  deploy/cloudflared/config.yml "$VM:/opt/bibai/cloudflared/config.yml"

"${SSH[@]}" 'cd /opt/bibai && test -f .env || {
  umask 077
  printf "SERVER_DIR=./server\nPOSTGRES_PASSWORD=%s\nJWT_SECRET=%s\n" "$(openssl rand -hex 24)" "$(openssl rand -hex 32)" > .env
}
docker compose up -d --build --remove-orphans && docker compose ps'
