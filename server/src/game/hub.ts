import type { ServerMessage } from "../contracts/ws.ts";

/** WS の接続のうち、配信に使うところだけ */
export type Conn = {
  send(data: string): unknown;
  close(code?: number, reason?: string): void;
};

/** sessionId → playerId → 接続。REST から起きた変化も、ここを通して配る */
export class Hub {
  private readonly rooms = new Map<string, Map<string, Conn>>();

  /** 繋いだ接続を登録する。同じプレイヤーの古い接続があれば返す */
  attach(sessionId: string, playerId: string, conn: Conn): Conn | undefined {
    let room = this.rooms.get(sessionId);
    if (!room) this.rooms.set(sessionId, (room = new Map()));
    const old = room.get(playerId);
    room.set(playerId, conn);
    return old === conn ? undefined : old;
  }

  /** 今の接続だったときだけ外して true（入れ替わった古い接続なら false） */
  detach(sessionId: string, playerId: string, conn: Conn): boolean {
    const room = this.rooms.get(sessionId);
    if (room?.get(playerId) !== conn) return false;
    room.delete(playerId);
    if (room.size === 0) this.rooms.delete(sessionId);
    return true;
  }

  members(sessionId: string): [string, Conn][] {
    return [...(this.rooms.get(sessionId) ?? [])];
  }

  send(conn: Conn, msg: ServerMessage): void {
    conn.send(JSON.stringify(msg));
  }

  sendTo(sessionId: string, playerId: string, msg: ServerMessage): void {
    const conn = this.rooms.get(sessionId)?.get(playerId);
    if (conn) this.send(conn, msg);
  }

  /** 受け手ごとにメッセージを作って送る。null を返した受け手には送らない */
  fanout(sessionId: string, build: (playerId: string) => ServerMessage | null, except?: string): void {
    for (const [playerId, conn] of this.members(sessionId)) {
      if (playerId === except) continue;
      const msg = build(playerId);
      if (msg) this.send(conn, msg);
    }
  }

  closeAll(): void {
    for (const room of this.rooms.values()) for (const conn of room.values()) conn.close(1001, "server shutting down");
    this.rooms.clear();
  }
}
