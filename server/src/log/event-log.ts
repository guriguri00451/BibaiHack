import type { ObjectStorage } from "../storage/storage.ts";

/**
 * 非同期ログ（仕様書 7章）。イベントはメモリに溜め、まとめて Object Storage に JSONL で書く。
 *   sessions/{sessionId}/rounds/{round}/events/{firstSeq}-{lastSeq}.jsonl
 *   sessions/{sessionId}/rounds/{round}/snapshots/{seq}.json
 * 未 Flush の分はクラッシュで失われる（仕様書どおり、完全な再現は保証しない）。
 * 擬態先などを含む内部ログなので、外へはラウンド終了後のリプレイ API からだけ出す。
 */
export type LogEntry = {
  sessionId: string;
  round: number;
  seq: number;
  timestamp: number;
  playerId: string | null;
  type: string;
  payload: unknown;
};

const pad = (n: number) => String(n).padStart(9, "0");
const roundPrefix = (sessionId: string, round: number) => `sessions/${sessionId}/rounds/${round}/`;

export class EventLog {
  /** "sessionId round" → 溜まっているイベント */
  private readonly buffers = new Map<string, LogEntry[]>();
  private readonly timer: ReturnType<typeof setInterval>;
  private flushing: Promise<void> = Promise.resolve();

  constructor(
    private readonly storage: ObjectStorage,
    opts: { flushIntervalMs?: number } = {},
  ) {
    this.timer = setInterval(() => void this.flush(), opts.flushIntervalMs ?? 2_000);
    this.timer.unref?.();
  }

  append(entry: LogEntry): void {
    const k = `${entry.sessionId} ${entry.round}`;
    let buf = this.buffers.get(k);
    if (!buf) this.buffers.set(k, (buf = []));
    buf.push(entry);
  }

  /** 溜まっている分を書く。sessionId を渡すとそのセッションだけ */
  flush(sessionId?: string): Promise<void> {
    const run = this.flushing.then(() => this.flushNow(sessionId));
    this.flushing = run.catch(() => {});
    return run;
  }

  private async flushNow(sessionId?: string) {
    for (const [k, buf] of [...this.buffers]) {
      if (buf.length === 0 || (sessionId && !k.startsWith(`${sessionId} `))) continue;
      this.buffers.delete(k);
      const first = buf[0]!;
      const last = buf[buf.length - 1]!;
      const key = `${roundPrefix(first.sessionId, first.round)}events/${pad(first.seq)}-${pad(last.seq)}.jsonl`;
      const body = new TextEncoder().encode(buf.map((e) => JSON.stringify(e)).join("\n") + "\n");
      try {
        await this.storage.put(key, body, "application/x-ndjson");
      } catch (e) {
        console.error(`ログを書けません: ${key}`, e);
      }
    }
  }

  async saveSnapshot(sessionId: string, round: number, seq: number, data: unknown): Promise<void> {
    const key = `${roundPrefix(sessionId, round)}snapshots/${pad(seq)}.json`;
    try {
      await this.storage.put(key, new TextEncoder().encode(JSON.stringify(data)), "application/json");
    } catch (e) {
      console.error(`Snapshot を書けません: ${key}`, e);
    }
  }

  /** 書いてあるログ・Snapshot の最大の seq（再起動しても seq を巻き戻さないため）。無ければ 0 */
  async lastSeq(sessionId: string, round: number): Promise<number> {
    let max = 0;
    for (const key of await this.storage.list(roundPrefix(sessionId, round))) {
      const m = /(?:-(\d+)\.jsonl|snapshots\/(\d+)\.json)$/.exec(key);
      if (m) max = Math.max(max, Number(m[1] ?? m[2]));
    }
    return max;
  }

  /** リプレイ用に、そのラウンドの Snapshot とイベントを seq 順に読む */
  async read(sessionId: string, round: number): Promise<{ snapshots: unknown[]; events: LogEntry[] }> {
    await this.flush(sessionId);
    const snapshots: { seq: number; data: unknown }[] = [];
    const events: LogEntry[] = [];
    const decoder = new TextDecoder();
    for (const key of await this.storage.list(roundPrefix(sessionId, round))) {
      const got = await this.storage.get(key);
      if (!got) continue;
      const text = decoder.decode(got.body);
      const snap = /snapshots\/(\d+)\.json$/.exec(key);
      if (snap) snapshots.push({ seq: Number(snap[1]), data: JSON.parse(text) });
      else if (key.endsWith(".jsonl")) {
        for (const line of text.split("\n")) if (line) events.push(JSON.parse(line) as LogEntry);
      }
    }
    snapshots.sort((a, b) => a.seq - b.seq);
    events.sort((a, b) => a.seq - b.seq);
    return { snapshots: snapshots.map((s) => s.data), events };
  }

  async dispose(): Promise<void> {
    clearInterval(this.timer);
    await this.flush();
  }
}
