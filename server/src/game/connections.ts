/** 送信と切断だけできる接続。Bun の ServerWebSocket をそのまま渡せる */
export type Peer = { send(text: string): unknown; close(code: number, reason: string): void };

/** sessionId → playerId → 接続。WS ハンドラと REST（オブジェクトの変更を配る側）で共有する */
export class Connections {
  private readonly rooms = new Map<string, Map<string, Peer>>();

  /** 同じプレイヤーの古い接続を返す（呼び出し側が REPLACED で閉じる） */
  set(sessionId: string, playerId: string, peer: Peer): Peer | undefined {
    let room = this.rooms.get(sessionId);
    if (!room) this.rooms.set(sessionId, (room = new Map()));
    const old = room.get(playerId);
    room.set(playerId, peer);
    return old;
  }

  get(sessionId: string, playerId: string): Peer | undefined {
    return this.rooms.get(sessionId)?.get(playerId);
  }

  delete(sessionId: string, playerId: string): void {
    const room = this.rooms.get(sessionId);
    room?.delete(playerId);
    if (room?.size === 0) this.rooms.delete(sessionId);
  }

  entries(sessionId: string): IterableIterator<[string, Peer]> {
    return (this.rooms.get(sessionId) ?? new Map<string, Peer>()).entries();
  }
}
