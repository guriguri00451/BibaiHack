import type { ContentfulStatusCode } from "hono/utils/http-status";

export type ErrorBody = { error: { code: string; message: string } };

export function errorBody(code: string, message: string): ErrorBody {
  return { error: { code, message } };
}

/** ハンドラから投げると、仕様書 9章の形式のエラー応答になる */
export class ApiError extends Error {
  constructor(
    readonly status: ContentfulStatusCode,
    readonly code: string,
    message: string,
  ) {
    super(message);
  }

  get body(): ErrorBody {
    return errorBody(this.code, this.message);
  }
}
