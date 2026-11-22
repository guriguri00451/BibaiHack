FROM oven/bun:1.3-alpine
WORKDIR /app
COPY package.json bun.lock ./
RUN bun install --frozen-lockfile --production
COPY src ./src
COPY drizzle ./drizzle
ENV NODE_ENV=production PORT=8080
USER bun
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --retries=3 CMD wget -qO- "http://127.0.0.1:8080${BASE_PATH}/healthz" || exit 1
CMD ["bun", "src/index.ts"]
