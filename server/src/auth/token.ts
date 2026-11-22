import { sign, verify } from "hono/jwt";

const TOKEN_TTL_SECONDS = 30 * 24 * 60 * 60;

/** ゲスト登録時に発行する署名付きトークン（HS256）。sub = userId */
export async function issueToken(userId: string, secret: string): Promise<string> {
  const now = Math.floor(Date.now() / 1000);
  return sign({ sub: userId, iat: now, exp: now + TOKEN_TTL_SECONDS }, secret, "HS256");
}

/** 不正・期限切れなら null */
export async function verifyToken(token: string, secret: string): Promise<string | null> {
  try {
    const payload = await verify(token, secret, "HS256");
    return typeof payload.sub === "string" ? payload.sub : null;
  } catch {
    return null;
  }
}
