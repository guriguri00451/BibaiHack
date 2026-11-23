#!/usr/bin/env bash
# deploy/envs.txt から compose.yaml と cloudflared/config.yml を作り直す（生成物もコミットする）
source "$(dirname "$0")/lib.sh"

compose="$DEPLOY_DIR/compose.yaml"
tunnel="$DEPLOY_DIR/cloudflared/config.yml"

{
  cat <<'YAML'
# 生成物。直接編集せず deploy/envs.txt を直して ./deploy/render.sh を実行する
# bibai-game VM（wds-pve VMID 100）の /opt/bibai に置いて使う。https://bibai.t0waxx.com
name: bibai

x-server: &server
  restart: unless-stopped
  depends_on:
    postgres:
      condition: service_healthy

services:
  cloudflared:
    image: cloudflare/cloudflared:2026.11.1
    command: tunnel --no-autoupdate --config /etc/cloudflared/config.yml run
    volumes:
      - ./cloudflared:/etc/cloudflared:ro
    restart: unless-stopped

  postgres:
    image: postgres:18-alpine
    environment:
      POSTGRES_USER: bibai
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:?}
      POSTGRES_DB: bibai
    volumes:
      - pgdata:/var/lib/postgresql
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U bibai -d bibai"]
      interval: 5s
      timeout: 3s
      retries: 10
    restart: unless-stopped
YAML
  for e in $(envs); do
    cat <<YAML

  server-$e:
    <<: *server
    build: ./envs/$e/server
    image: bibai-server:$e
    environment:
      NODE_ENV: production
      BASE_PATH: "$(base_path "$e")"
      PUBLIC_URL: https://$PUBLIC_HOST
      DATABASE_URL: postgres://bibai:\${POSTGRES_PASSWORD:?}@postgres:5432/$(db_name "$e")
      JWT_SECRET: \${$(secret_var "$e"):?}
    volumes:
      - assets-$e:/data/assets
YAML
  done
  printf '\nvolumes:\n  pgdata:\n'
  for e in $(envs); do printf '  assets-%s:\n' "$e"; done
} >"$compose"

{
  cat <<'YAML'
# 生成物。直接編集せず deploy/envs.txt を直して ./deploy/render.sh を実行する
# Cloudflare Tunnel "bibai-game"（DNS: bibai.t0waxx.com → このトンネル）
# credentials.json は秘密情報なのでリポジトリに入れない（VM の /opt/bibai/cloudflared にだけ置く）
tunnel: 90911730-55e1-4078-af70-593b387bbb8c
credentials-file: /etc/cloudflared/credentials.json
# この回線では QUIC だと4本中1本しか繋がらない（CRYPTO_ERROR 0x178）ので HTTP/2 にする
protocol: http2
ingress:
YAML
  # 前置きのある環境を先に並べ、最後に prod（前置きなし）を置く
  for e in $(envs); do
    [[ "$e" == prod ]] && continue
    printf '  - hostname: %s\n    path: ^/%s(/|$)\n    service: http://server-%s:8080\n' "$PUBLIC_HOST" "$e" "$e"
  done
  env_exists prod && printf '  - hostname: %s\n    service: http://server-prod:8080\n' "$PUBLIC_HOST"
  printf '  - service: http_status:404\n'
} >"$tunnel"

info "生成しました: deploy/compose.yaml, deploy/cloudflared/config.yml（環境 $(envs | wc -l | tr -d ' ') 個）"
