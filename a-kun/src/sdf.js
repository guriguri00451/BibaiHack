// 粘土の形を「符号付き距離場 (SDF)」のボクセル格子で持つ。
// 値は格子単位の距離。負 = 粘土の内側、正 = 外側。
// 表面は Surface Nets でチャンク(16^3)ごとにメッシュ化し、
// 編集したチャンクだけを作り直す。
// 色は格子点ごとの RGB (sRGB, 0-255) で持ち、押す・引っ張るときは形と一緒に動かす。

// 立方体セルの12本の辺(角の番号のペア)。角の番号 = bx + 2*by + 4*bz
const EA = [], EB = [];
for (let i = 0; i < 8; i++) {
  for (const bit of [1, 2, 4]) {
    if (!(i & bit)) { EA.push(i); EB.push(i | bit); }
  }
}

// sRGB (0-255) → リニア (頂点カラーはリニアで渡す)
const SRGB_TO_LINEAR = new Float32Array(256);
for (let i = 0; i < 256; i++) {
  const c = i / 255;
  SRGB_TO_LINEAR[i] = c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
}

export class ClayField {
  constructor({ N = 80, size = 0.4, chunk = 16 } = {}) {
    this.N = N;
    this.size = size;           // 格子が覆う立方体の一辺 (ローカル座標, m)
    this.h = size / (N - 1);    // 格子の間隔
    this.half = size / 2;
    this.S = chunk;
    this.K = Math.ceil(N / chunk);
    const total = N * N * N;
    this.d = new Float32Array(total);
    this.tmp = new Float32Array(total);
    this.col = new Uint8Array(total * 3);    // 格子点の色 (sRGB)
    this.tmpCol = new Uint8Array(total * 3);
    this.colVersion = 0;                     // 色が変わるたびに増やす (Undo で色を共有するため)
    this.baseColor = [0xc9, 0x80, 0x60];
    this.dirty = new Set();
    this.conserveBand = 6;   // 押しのけた分を盛り上げる帯の幅 (格子)
    this.conserveMax = 1.0;  // 1回で盛り上げる上限 (格子)
    this.relax = 0.15;       // 手が触れた所をなじませる強さ (0〜1)
    this.V0 = null;          // 保つ体積 (占有する格子の数)
    this._edit = null;       // 前回の conserve のあとに変えた範囲
    this._rebase = false;    // 盛る・削るで体積を変えたので、基準を取り直す

    const L = chunk + 1;
    this._L = L;
    this._map = new Int32Array(L * L * L);
    this._pos = new Float32Array(L * L * L * 3);
    this._nor = new Float32Array(L * L * L * 3);
    this._col = new Float32Array(L * L * L * 3);
    this._wts = new Float32Array(8);
    this._ids = new Int32Array(8);
    this._idx = new Uint32Array(chunk * chunk * chunk * 3 * 6);
    this._c = new Float32Array(8);
  }

  // ---------- 初期形状 ----------

  resetCube(b = 0.07, r = 0.01, color = this.baseColor) {
    const { N, h, half, d } = this;
    this.fillColor(color);
    for (let z = 0; z < N; z++) {
      for (let y = 0; y < N; y++) {
        for (let x = 0; x < N; x++) {
          const qx = Math.abs(-half + x * h) - b + r;
          const qy = Math.abs(-half + y * h) - b + r;
          const qz = Math.abs(-half + z * h) - b + r;
          const ox = Math.max(qx, 0), oy = Math.max(qy, 0), oz = Math.max(qz, 0);
          let dist = Math.hypot(ox, oy, oz) + Math.min(Math.max(qx, qy, qz), 0) - r;
          dist /= h;
          const border = x === 0 || y === 0 || z === 0 || x === N - 1 || y === N - 1 || z === N - 1;
          d[x + N * (y + N * z)] = border ? Math.max(dist, 1) : dist;
        }
      }
    }
    this.markAllDirty();
    this.V0 = this.volume();
    this._edit = null;
  }

  fillColor([r, g, b]) {
    const col = this.col;
    for (let i = 0; i < col.length; i += 3) { col[i] = r; col[i + 1] = g; col[i + 2] = b; }
    this.colVersion++;
    this.markAllDirty();
  }

  // ---------- 読み出し ----------

  _tri(arr, x, y, z) {
    const N = this.N, m = N - 1.0001;
    x = x < 0 ? 0 : x > m ? m : x;
    y = y < 0 ? 0 : y > m ? m : y;
    z = z < 0 ? 0 : z > m ? m : z;
    const ix = x | 0, iy = y | 0, iz = z | 0;
    const fx = x - ix, fy = y - iy, fz = z - iz;
    const NN = N * N;
    const i = ix + N * iy + NN * iz;
    const c00 = arr[i] * (1 - fx) + arr[i + 1] * fx;
    const c10 = arr[i + N] * (1 - fx) + arr[i + N + 1] * fx;
    const c01 = arr[i + NN] * (1 - fx) + arr[i + NN + 1] * fx;
    const c11 = arr[i + NN + N] * (1 - fx) + arr[i + NN + N + 1] * fx;
    const c0 = c00 * (1 - fy) + c10 * fy;
    const c1 = c01 * (1 - fy) + c11 * fy;
    return c0 * (1 - fz) + c1 * fz;
  }

  // 格子座標 g での距離 (格子単位)。格子の外なら Infinity
  sample(g) {
    const m = this.N - 1;
    if (g.x < 0 || g.y < 0 || g.z < 0 || g.x > m || g.y > m || g.z > m) return Infinity;
    return this._tri(this.d, g.x, g.y, g.z);
  }

  // 距離場の勾配 (外向き、正規化済み)
  gradient(g, out) {
    const e = 0.5, d = this.d;
    out.x = this._tri(d, g.x + e, g.y, g.z) - this._tri(d, g.x - e, g.y, g.z);
    out.y = this._tri(d, g.x, g.y + e, g.z) - this._tri(d, g.x, g.y - e, g.z);
    out.z = this._tri(d, g.x, g.y, g.z + e) - this._tri(d, g.x, g.y, g.z - e);
    const l = Math.hypot(out.x, out.y, out.z) || 1;
    out.x /= l; out.y /= l; out.z /= l;
    return out;
  }

  // 格子座標でのレイ (dir は正規化済み) と表面の交点までの距離。当たらなければ null
  raymarch(o, dir, arr = this.d) {
    const lo = 0.5, hi = this.N - 1.5;
    let tmin = 0, tmax = Infinity;
    for (const ax of ['x', 'y', 'z']) {
      const oo = o[ax], dd = dir[ax];
      if (Math.abs(dd) < 1e-9) {
        if (oo < lo || oo > hi) return null;
        continue;
      }
      let t1 = (lo - oo) / dd, t2 = (hi - oo) / dd;
      if (t1 > t2) [t1, t2] = [t2, t1];
      tmin = Math.max(tmin, t1);
      tmax = Math.min(tmax, t2);
      if (tmin > tmax) return null;
    }
    const step = 0.5;
    let t = tmin;
    let prev = this._tri(arr, o.x + dir.x * t, o.y + dir.y * t, o.z + dir.z * t);
    if (prev < 0) return t;
    while (t < tmax) {
      const t2 = Math.min(t + step, tmax);
      const v = this._tri(arr, o.x + dir.x * t2, o.y + dir.y * t2, o.z + dir.z * t2);
      if (v < 0) return t + (t2 - t) * prev / (prev - v);
      prev = v;
      t = t2;
    }
    return null;
  }

  // ---------- 編集 ----------

  _box(c, r) {
    const N = this.N;
    return [
      Math.max(1, Math.floor(c.x - r)), Math.min(N - 2, Math.ceil(c.x + r)),
      Math.max(1, Math.floor(c.y - r)), Math.min(N - 2, Math.ceil(c.y + r)),
      Math.max(1, Math.floor(c.z - r)), Math.min(N - 2, Math.ceil(c.z + r)),
    ];
  }

  _copyToTmp(c, r, withCol = false) {
    const N = this.N;
    const xa = Math.max(0, Math.floor(c.x - r)), xb = Math.min(N - 1, Math.ceil(c.x + r));
    const ya = Math.max(0, Math.floor(c.y - r)), yb = Math.min(N - 1, Math.ceil(c.y + r));
    const za = Math.max(0, Math.floor(c.z - r)), zb = Math.min(N - 1, Math.ceil(c.z + r));
    if (xa > xb) return;
    for (let z = za; z <= zb; z++) {
      for (let y = ya; y <= yb; y++) {
        const row = N * (y + N * z);
        this.tmp.set(this.d.subarray(row + xa, row + xb + 1), row + xa);
        if (withCol) this.tmpCol.set(this.col.subarray((row + xa) * 3, (row + xb + 1) * 3), (row + xa) * 3);
      }
    }
  }

  // tmpCol の最寄りの格子点の色を i に写す
  _copyColNearest(i, x, y, z) {
    const N = this.N, m = N - 1;
    const ix = x < 0 ? 0 : x > m ? m : Math.round(x);
    const iy = y < 0 ? 0 : y > m ? m : Math.round(y);
    const iz = z < 0 ? 0 : z > m ? m : Math.round(z);
    const j = (ix + N * (iy + N * iz)) * 3, k = i * 3;
    const col = this.col, tc = this.tmpCol;
    col[k] = tc[j]; col[k + 1] = tc[j + 1]; col[k + 2] = tc[j + 2];
  }

  // 球で削る (押し込む)
  carve(c, r) {
    return this._sphereOp(c, r, true);
  }

  // 球を足す (盛る)。rgb を渡すと足した所をその色にする
  add(c, r, rgb = null) {
    return this._sphereOp(c, r, false, rgb);
  }

  _sphereOp(c, r, subtract, rgb = null) {
    const { N, d, col } = this;
    this._rebase = true; // 盛る・削るは体積を変える道具なので、保つ体積を取り直す
    const [x0, x1, y0, y1, z0, z1] = this._box(c, r);
    let bx0 = Infinity, bx1 = -1, by0 = Infinity, by1 = -1, bz0 = Infinity, bz1 = -1;
    for (let z = z0; z <= z1; z++) {
      const dz = z - c.z;
      for (let y = y0; y <= y1; y++) {
        const dy = y - c.y;
        let i = x0 + N * (y + N * z);
        for (let x = x0; x <= x1; x++, i++) {
          const dx = x - c.x;
          const sd = Math.sqrt(dx * dx + dy * dy + dz * dz) - r;
          let changed = false;
          if (subtract) {
            if (-sd > d[i] + 1e-4) { d[i] = -sd; changed = true; }
          } else if (sd < d[i] - 1e-4) {
            d[i] = sd; changed = true;
            if (rgb) { const k = i * 3; col[k] = rgb[0]; col[k + 1] = rgb[1]; col[k + 2] = rgb[2]; }
          }
          if (changed) {
            if (x < bx0) bx0 = x; if (x > bx1) bx1 = x;
            if (y < by0) by0 = y; if (y > by1) by1 = y;
            if (z < bz0) bz0 = z; if (z > bz1) bz1 = z;
          }
        }
      }
    }
    if (bx1 < 0) return false;
    if (rgb && !subtract) this.colVersion++;
    this.markDirtyPoints(bx0, bx1, by0, by1, bz0, bz1);
    return true;
  }

  // 塗る: 球の中の色を rgb に寄せる (中心ほど強く)。形は変えない
  paint(c, r, rgb, strength = 0.5) {
    const { N, col } = this;
    const [x0, x1, y0, y1, z0, z1] = this._box(c, r);
    let any = false;
    for (let z = z0; z <= z1; z++) {
      const dz = z - c.z;
      for (let y = y0; y <= y1; y++) {
        const dy = y - c.y;
        let i = x0 + N * (y + N * z);
        for (let x = x0; x <= x1; x++, i++) {
          const dx = x - c.x;
          const dist = Math.sqrt(dx * dx + dy * dy + dz * dz);
          if (dist >= r) continue;
          const t = dist / r;
          const w = Math.min(1, (1 - t * t) * strength * 3); // 中ほどはしっかり、縁はぼかす
          const k = i * 3;
          for (let ch = 0; ch < 3; ch++) {
            const v = col[k + ch];
            const nv = Math.round(v + (rgb[ch] - v) * w);
            if (nv !== v) { col[k + ch] = nv; any = true; }
          }
        }
      }
    }
    if (any) {
      this.colVersion++;
      this.markDirtyPoints(x0, x1, y0, y1, z0, z1);
    }
    return any;
  }

  // なでる (ラプラシアン平滑化)
  smooth(c, r, strength = 0.5) {
    const { N, d, tmp } = this;
    const NN = N * N;
    this._copyToTmp(c, r + 2);
    const [x0, x1, y0, y1, z0, z1] = this._box(c, r);
    let any = false;
    for (let z = z0; z <= z1; z++) {
      const dz = z - c.z;
      for (let y = y0; y <= y1; y++) {
        const dy = y - c.y;
        let i = x0 + N * (y + N * z);
        for (let x = x0; x <= x1; x++, i++) {
          const dx = x - c.x;
          const dist = Math.sqrt(dx * dx + dy * dy + dz * dz);
          if (dist >= r) continue;
          const t = dist / r;
          const w = Math.min(1, (1 - t * t) * (1 - t * t) * strength);
          const avg = (tmp[i - 1] + tmp[i + 1] + tmp[i - N] + tmp[i + N] + tmp[i - NN] + tmp[i + NN]) / 6;
          const nv = tmp[i] + (avg - tmp[i]) * w;
          if (Math.abs(nv - d[i]) > 1e-4) { d[i] = nv; any = true; }
        }
      }
    }
    if (any) this.markDirtyPoints(x0, x1, y0, y1, z0, z1);
    return any;
  }

  // つまんで引っ張る: c の周りの粘土を delta だけ減衰付きで動かす (スネークフック)
  pull(c, delta, R) {
    const len = Math.hypot(delta.x, delta.y, delta.z);
    if (len < 1e-3) return false;
    const steps = Math.max(1, Math.ceil(len / (R * 0.35)));
    const sx = delta.x / steps, sy = delta.y / steps, sz = delta.z / steps;
    const cur = { x: c.x, y: c.y, z: c.z };
    let any = false;
    for (let s = 0; s < steps; s++) {
      any = this._pullStep(cur, sx, sy, sz, R) || any;
      cur.x += sx; cur.y += sy; cur.z += sz;
    }
    return any;
  }

  _pullStep(c, vx, vy, vz, R) {
    const { N, d, tmp } = this;
    const e = { x: c.x + vx, y: c.y + vy, z: c.z + vz };
    const vlen = Math.hypot(vx, vy, vz);
    this._copyToTmp(e, R + vlen + 2, true);
    const [x0, x1, y0, y1, z0, z1] = this._box(e, R);
    let any = false;
    for (let z = z0; z <= z1; z++) {
      const dz = z - e.z;
      for (let y = y0; y <= y1; y++) {
        const dy = y - e.y;
        let i = x0 + N * (y + N * z);
        for (let x = x0; x <= x1; x++, i++) {
          const dx = x - e.x;
          const dist = Math.sqrt(dx * dx + dy * dy + dz * dz);
          if (dist >= R) continue;
          // 中心付近はまるごと付いてくる (細い糸にならないように)
          const u = Math.max(0, (dist / R - 0.35) / 0.65);
          const w = (1 - u * u) * (1 - u * u);
          const sx = x - w * vx, sy = y - w * vy, sz = z - w * vz;
          const v = this._tri(tmp, sx, sy, sz);
          if (Math.abs(v - d[i]) > 1e-5) { d[i] = v; any = true; this._copyColNearest(i, sx, sy, sz); }
        }
      }
    }
    if (any) { this.colVersion++; this.markDirtyPoints(x0, x1, y0, y1, z0, z1); }
    return any;
  }

  // 手で押しのける (粘性のある変形)
  // caps: [{ ax, ay, az, bx, by, bz, r, ux, uy, uz }] 格子座標のカプセルと、このフレームの移動量 u
  // カプセルの中と、その周り F までの粘土を u だけ動かす (中ほど強く、外側ほど弱く)
  // conserve: 押しのけて減った分を、手の縁のまわりの表面に盛り上げて戻す (粘土らしい体積の保存)
  displace(caps, F, conserve = true) {
    if (!caps.length) return false;
    const { N, d } = this;
    const NN = N * N;
    if (!this._uw) {
      const total = N * N * N;
      this._ux = new Float32Array(total);
      this._uy = new Float32Array(total);
      this._uz = new Float32Array(total);
      this._uw = new Float32Array(total);
      this._touch = new Int32Array(total);
      this._sh = new Uint8Array(total);
      this._shell = new Int32Array(total);
    }
    const ux = this._ux, uy = this._uy, uz = this._uz, uw = this._uw, touch = this._touch;
    const sh = this._sh, shell = this._shell;
    const B = conserve ? this.conserveBand : 0; // 盛り上げる帯の幅 (格子)
    let nt = 0, ns = 0, maxU = 0;
    let gx0 = Infinity, gx1 = -Infinity, gy0 = Infinity, gy1 = -Infinity, gz0 = Infinity, gz1 = -Infinity;

    for (const c of caps) {
      const R = c.r + F + B;
      const x0 = Math.max(1, Math.floor(Math.min(c.ax, c.bx) - R)), x1 = Math.min(N - 2, Math.ceil(Math.max(c.ax, c.bx) + R));
      const y0 = Math.max(1, Math.floor(Math.min(c.ay, c.by) - R)), y1 = Math.min(N - 2, Math.ceil(Math.max(c.ay, c.by) + R));
      const z0 = Math.max(1, Math.floor(Math.min(c.az, c.bz) - R)), z1 = Math.min(N - 2, Math.ceil(Math.max(c.az, c.bz) + R));
      if (x0 > x1 || y0 > y1 || z0 > z1) continue;
      gx0 = Math.min(gx0, x0); gx1 = Math.max(gx1, x1);
      gy0 = Math.min(gy0, y0); gy1 = Math.max(gy1, y1);
      gz0 = Math.min(gz0, z0); gz1 = Math.max(gz1, z1);
      maxU = Math.max(maxU, Math.hypot(c.ux, c.uy, c.uz));
      const ex = c.bx - c.ax, ey = c.by - c.ay, ez = c.bz - c.az;
      const ee = ex * ex + ey * ey + ez * ez || 1e-9;
      const coreR2 = (c.r + F) * (c.r + F), shellR2 = (c.r + F + B) * (c.r + F + B);
      for (let z = z0; z <= z1; z++) {
        for (let y = y0; y <= y1; y++) {
          let i = x0 + N * y + NN * z;
          for (let x = x0; x <= x1; x++, i++) {
            const px = x - c.ax, py = y - c.ay, pz = z - c.az;
            let t = (px * ex + py * ey + pz * ez) / ee;
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            const qx = px - t * ex, qy = py - t * ey, qz = pz - t * ez;
            const q2 = qx * qx + qy * qy + qz * qz;
            if (q2 >= coreR2) {
              // 帯: 表面の近くだけ見ればよい
              if (q2 < shellR2 && d[i] > -1 && d[i] < 2 && !sh[i]) { sh[i] = 1; shell[ns++] = i; }
              continue;
            }
            const sd = Math.sqrt(q2) - c.r;
            let w = 1;
            if (sd > 0) { const s = sd / F; w = (1 - s * s) * (1 - s * s); }
            if (uw[i] === 0) touch[nt++] = i;
            if (w > uw[i]) { uw[i] = w; ux[i] = c.ux; uy[i] = c.uy; uz[i] = c.uz; }
          }
        }
      }
    }
    if (!nt) {
      for (let k = 0; k < ns; k++) sh[shell[k]] = 0;
      return false;
    }
    // 別のカプセルの内側に入っている点は帯から外す
    let nk = 0;
    for (let k = 0; k < ns; k++) {
      const i = shell[k];
      sh[i] = 0;
      if (uw[i] === 0) shell[nk++] = i;
    }

    // 動かす前の値を tmp に写してから、そこを引いて読む
    const m = maxU + 2;
    this._copyBox(gx0 - m, gx1 + m, gy0 - m, gy1 + m, gz0 - m, gz1 + m, true);
    const tmp = this.tmp;
    let bx0 = Infinity, bx1 = -1, by0 = Infinity, by1 = -1, bz0 = Infinity, bz1 = -1;
    const occ = (v) => (v <= -0.5 ? 1 : v >= 0.5 ? 0 : 0.5 - v);
    let lost = 0;
    for (let k = 0; k < nt; k++) {
      const i = touch[k];
      const w = uw[i];
      uw[i] = 0;
      const x = i % N, y = ((i / N) | 0) % N, z = (i / NN) | 0;
      const sx = x - w * ux[i], sy = y - w * uy[i], sz = z - w * uz[i];
      const v = this._tri(tmp, sx, sy, sz);
      if (Math.abs(v - d[i]) > 1e-5) {
        lost += occ(d[i]) - occ(v);
        d[i] = v;
        this._copyColNearest(i, sx, sy, sz);
        if (x < bx0) bx0 = x; if (x > bx1) bx1 = x;
        if (y < by0) by0 = y; if (y > by1) by1 = y;
        if (z < bz0) bz0 = z; if (z > bz1) bz1 = z;
      }
    }

    // 減った体積を、手の縁の表面を少し外へ押し出して戻す。
    // 一度に大きく押し出すと戻る量が頭打ちになるので、実際に戻った量を測りながら数回に分ける
    let remaining = lost;
    for (let iter = 0; iter < 4 && remaining > 0.5 && nk; iter++) {
      let A = 0;
      for (let k = 0; k < nk; k++) {
        const v = d[shell[k]];
        if (v > -0.5 && v < 1.5) A += 1 - Math.abs(v - 0.5) / 1.5;
      }
      if (A <= 0) break;
      const delta = Math.min(remaining / A, this.conserveMax);
      let gained = 0;
      for (let k = 0; k < nk; k++) {
        const i = shell[k];
        const v = d[i];
        const a = 1 - Math.abs(v - 0.5) / 1.5;
        if (a <= 0) continue;
        const nv = v - delta * a;
        gained += occ(nv) - occ(v);
        d[i] = nv;
        const x = i % N, y = ((i / N) | 0) % N, z = (i / NN) | 0;
        if (x < bx0) bx0 = x; if (x > bx1) bx1 = x;
        if (y < by0) by0 = y; if (y > by1) by1 = y;
        if (z < bz0) bz0 = z; if (z > bz1) bz1 = z;
      }
      remaining -= gained;
      if (gained < 0.1) break;
    }
    if (bx1 < 0) return false;
    this.colVersion++;

    // なじませる: 手が触れた所と縁を、まわりの平均へ少し寄せる (筋やギザギザを消し、柔らかく見せる)
    if (this.relax > 0) {
      this._copyBox(bx0 - 1, bx1 + 1, by0 - 1, by1 + 1, bz0 - 1, bz1 + 1);
      const r = this.relax;
      const lists = [[touch, nt], [shell, nk]];
      for (const [list, n] of lists) {
        for (let k = 0; k < n; k++) {
          const i = list[k];
          const v = tmp[i];
          if (v > 3 || v < -3) continue; // 表面から遠い所は触らない
          const x = i % N, y = ((i / N) | 0) % N, z = (i / NN) | 0;
          if (x < bx0 || x > bx1 || y < by0 || y > by1 || z < bz0 || z > bz1) continue;
          const avg = (tmp[i - 1] + tmp[i + 1] + tmp[i - N] + tmp[i + N] + tmp[i - NN] + tmp[i + NN]) / 6;
          d[i] = v + (avg - v) * r;
        }
      }
    }
    this.markDirtyPoints(bx0, bx1, by0, by1, bz0, bz1);
    return true;
  }

  _copyBox(xa, xb, ya, yb, za, zb, withCol = false) {
    const N = this.N;
    xa = Math.max(0, Math.floor(xa)); xb = Math.min(N - 1, Math.ceil(xb));
    ya = Math.max(0, Math.floor(ya)); yb = Math.min(N - 1, Math.ceil(yb));
    za = Math.max(0, Math.floor(za)); zb = Math.min(N - 1, Math.ceil(zb));
    for (let z = za; z <= zb; z++) {
      for (let y = ya; y <= yb; y++) {
        const row = N * (y + N * z);
        this.tmp.set(this.d.subarray(row + xa, row + xb + 1), row + xa);
        if (withCol) this.tmpCol.set(this.col.subarray((row + xa) * 3, (row + xb + 1) * 3), (row + xa) * 3);
      }
    }
  }

  // ---------- 体積の保存 ----------
  // 粘土の体積 (占有する格子の数。表面をまたぐ格子は距離に応じた割合)
  volume() {
    const d = this.d;
    let v = 0;
    for (let i = 0; i < d.length; i++) {
      const x = d[i];
      v += x <= -0.5 ? 1 : x >= 0.5 ? 0 : 0.5 - x;
    }
    return v;
  }

  // 変えた範囲のまわりの表面を少し出し入れして、体積を V0 に戻す (1 フレームに 1 回呼ぶ)。
  // 変えていなければ何もしない。盛る・削るのあとは、新しい体積を基準にする
  conserve(margin = 8) {
    const e = this._edit;
    if (!e) return false;
    this._edit = null;
    const V = this.volume();
    if (this._rebase || this.V0 === null) {
      this._rebase = false;
      this.V0 = V;
      return false;
    }
    let remaining = this.V0 - V;
    if (Math.abs(remaining) < 0.5) return false;
    const { N, d } = this;
    const NN = N * N;
    const x0 = Math.max(1, e[0] - margin), x1 = Math.min(N - 2, e[1] + margin);
    const y0 = Math.max(1, e[2] - margin), y1 = Math.min(N - 2, e[3] + margin);
    const z0 = Math.max(1, e[4] - margin), z1 = Math.min(N - 2, e[5] + margin);
    const occ = (v) => (v <= -0.5 ? 1 : v >= 0.5 ? 0 : 0.5 - v);
    let changed = false;
    for (let iter = 0; iter < 6 && Math.abs(remaining) >= 0.5; iter++) {
      let A = 0;
      for (let z = z0; z <= z1; z++) for (let y = y0; y <= y1; y++) {
        let i = x0 + N * y + NN * z;
        for (let x = x0; x <= x1; x++, i++) {
          const v = d[i];
          if (v > -1 && v < 2) A += 1 - Math.abs(v - 0.5) / 1.5;
        }
      }
      if (A <= 0) break;
      const delta = Math.max(-this.conserveMax, Math.min(this.conserveMax, remaining / A));
      let gained = 0;
      for (let z = z0; z <= z1; z++) for (let y = y0; y <= y1; y++) {
        let i = x0 + N * y + NN * z;
        for (let x = x0; x <= x1; x++, i++) {
          const v = d[i];
          if (v <= -1 || v >= 2) continue;
          const nv = v - delta * (1 - Math.abs(v - 0.5) / 1.5);
          gained += occ(nv) - occ(v);
          d[i] = nv;
        }
      }
      changed = true;
      remaining -= gained;
      if (Math.abs(gained) < 0.05) break;
    }
    if (changed) { this.markDirtyPoints(x0, x1, y0, y1, z0, z1); this._edit = null; }
    return changed;
  }

  // ---------- Undo ----------
  // 色は変わっていなければ前のスナップショットと同じ配列を共有する (メモリ節約。スナップショットは書き換えない)

  snapshot() {
    if (!this._colSnap || this._colSnapVersion !== this.colVersion) {
      this._colSnap = this.col.slice();
      this._colSnapVersion = this.colVersion;
    }
    return { d: this.d.slice(), col: this._colSnap };
  }

  restore(s) {
    this.d.set(s.d);
    this.col.set(s.col);
    this.colVersion++;
    this.markAllDirty();
    this.V0 = this.volume();
    this._edit = null;
  }

  // ---------- チャンク管理 ----------

  markAllDirty() {
    const K = this.K;
    for (let k = 0; k < K * K * K; k++) this.dirty.add(k);
  }

  // 格子点 [xa..xb] などが変わったとき、その点を読むチャンクを全部ダーティにする
  markDirtyPoints(xa, xb, ya, yb, za, zb) {
    const { S, K } = this;
    const e = this._edit;
    if (!e) this._edit = [xa, xb, ya, yb, za, zb];
    else {
      if (xa < e[0]) e[0] = xa; if (xb > e[1]) e[1] = xb;
      if (ya < e[2]) e[2] = ya; if (yb > e[3]) e[3] = yb;
      if (za < e[4]) e[4] = za; if (zb > e[5]) e[5] = zb;
    }
    const kx0 = Math.max(0, Math.floor((xa - 1) / S)), kx1 = Math.min(K - 1, Math.floor((xb + 1) / S));
    const ky0 = Math.max(0, Math.floor((ya - 1) / S)), ky1 = Math.min(K - 1, Math.floor((yb + 1) / S));
    const kz0 = Math.max(0, Math.floor((za - 1) / S)), kz1 = Math.min(K - 1, Math.floor((zb + 1) / S));
    for (let kz = kz0; kz <= kz1; kz++)
      for (let ky = ky0; ky <= ky1; ky++)
        for (let kx = kx0; kx <= kx1; kx++) this.dirty.add(kx + K * (ky + K * kz));
  }

  takeDirty() {
    const list = [...this.dirty];
    this.dirty.clear();
    return list;
  }

  // ---------- Surface Nets ----------
  // チャンクは格子点 [x0, x0+S) を「持つ」。持っている点から出る辺の面を出す。
  // 面の頂点はセル [x0-1, x0+S) にあるので、そこまで頂点を計算する。
  meshChunk(key) {
    const { N, S, K, d, h, half } = this;
    const L = this._L, map = this._map, pos = this._pos, nor = this._nor, idx = this._idx, c = this._c;
    const vcol = this._col, col = this.col, LIN = SRGB_TO_LINEAR, wts = this._wts, ids = this._ids;
    const NN = N * N;
    const kx = key % K, ky = Math.floor(key / K) % K, kz = Math.floor(key / (K * K));
    const x0 = kx * S, y0 = ky * S, z0 = kz * S;
    const ox = x0 - 1, oy = y0 - 1, oz = z0 - 1;
    map.fill(-1);

    // 1) セルごとの頂点
    let nv = 0;
    const cxa = Math.max(ox, 0), cxb = Math.min(x0 + S - 1, N - 2);
    const cya = Math.max(oy, 0), cyb = Math.min(y0 + S - 1, N - 2);
    const cza = Math.max(oz, 0), czb = Math.min(z0 + S - 1, N - 2);
    for (let z = cza; z <= czb; z++) {
      for (let y = cya; y <= cyb; y++) {
        for (let x = cxa; x <= cxb; x++) {
          const i0 = x + N * y + NN * z;
          c[0] = d[i0]; c[1] = d[i0 + 1];
          c[2] = d[i0 + N]; c[3] = d[i0 + N + 1];
          c[4] = d[i0 + NN]; c[5] = d[i0 + NN + 1];
          c[6] = d[i0 + NN + N]; c[7] = d[i0 + NN + N + 1];
          let mask = 0;
          for (let k = 0; k < 8; k++) if (c[k] < 0) mask |= 1 << k;
          if (mask === 0 || mask === 255) continue;

          let sx = 0, sy = 0, sz = 0, cnt = 0;
          for (let e = 0; e < 12; e++) {
            const a = EA[e], b = EB[e];
            const va = c[a], vb = c[b];
            if ((va < 0) !== (vb < 0)) {
              const t = va / (va - vb);
              const ax = a & 1, ay = (a >> 1) & 1, az = (a >> 2) & 1;
              sx += ax + t * ((b & 1) - ax);
              sy += ay + t * (((b >> 1) & 1) - ay);
              sz += az + t * (((b >> 2) & 1) - az);
              cnt++;
            }
          }
          const fx = sx / cnt, fy = sy / cnt, fz = sz / cnt;

          // 法線 = 三線形補間した距離場の勾配
          let gx = (1 - fy) * (1 - fz) * (c[1] - c[0]) + fy * (1 - fz) * (c[3] - c[2]) + (1 - fy) * fz * (c[5] - c[4]) + fy * fz * (c[7] - c[6]);
          let gy = (1 - fx) * (1 - fz) * (c[2] - c[0]) + fx * (1 - fz) * (c[3] - c[1]) + (1 - fx) * fz * (c[6] - c[4]) + fx * fz * (c[7] - c[5]);
          let gz = (1 - fx) * (1 - fy) * (c[4] - c[0]) + fx * (1 - fy) * (c[5] - c[1]) + (1 - fx) * fy * (c[6] - c[2]) + fx * fy * (c[7] - c[3]);
          const gl = Math.hypot(gx, gy, gz) || 1;
          gx /= gl; gy /= gl; gz /= gl;

          const p = nv * 3;
          pos[p] = -half + (x + fx) * h;
          pos[p + 1] = -half + (y + fy) * h;
          pos[p + 2] = -half + (z + fz) * h;
          nor[p] = gx; nor[p + 1] = gy; nor[p + 2] = gz;
          // 色 = 8 隅の色の三線形補間
          const w0x = 1 - fx, w0y = 1 - fy, w0z = 1 - fz;
          wts[0] = w0x * w0y * w0z; wts[1] = fx * w0y * w0z; wts[2] = w0x * fy * w0z; wts[3] = fx * fy * w0z;
          wts[4] = w0x * w0y * fz; wts[5] = fx * w0y * fz; wts[6] = w0x * fy * fz; wts[7] = fx * fy * fz;
          ids[0] = i0; ids[1] = i0 + 1; ids[2] = i0 + N; ids[3] = i0 + N + 1;
          ids[4] = i0 + NN; ids[5] = i0 + NN + 1; ids[6] = i0 + NN + N; ids[7] = i0 + NN + N + 1;
          let cr = 0, cg = 0, cb = 0;
          for (let k = 0; k < 8; k++) {
            const j = ids[k] * 3, wk = wts[k];
            cr += LIN[col[j]] * wk; cg += LIN[col[j + 1]] * wk; cb += LIN[col[j + 2]] * wk;
          }
          vcol[p] = cr; vcol[p + 1] = cg; vcol[p + 2] = cb;
          map[(x - ox) + L * ((y - oy) + L * (z - oz))] = nv++;
        }
      }
    }

    // 2) 符号が変わる辺ごとに四角形
    let ni = 0;
    const cell = (x, y, z) => map[(x - ox) + L * ((y - oy) + L * (z - oz))];
    const emit = (a, b, cc, dd, inside) => {
      if (a < 0 || b < 0 || cc < 0 || dd < 0) return;
      if (inside) {
        idx[ni++] = a; idx[ni++] = b; idx[ni++] = cc;
        idx[ni++] = a; idx[ni++] = cc; idx[ni++] = dd;
      } else {
        idx[ni++] = a; idx[ni++] = cc; idx[ni++] = b;
        idx[ni++] = a; idx[ni++] = dd; idx[ni++] = cc;
      }
    };
    const pxb = Math.min(x0 + S, N), pyb = Math.min(y0 + S, N), pzb = Math.min(z0 + S, N);
    for (let z = z0; z < pzb; z++) {
      for (let y = y0; y < pyb; y++) {
        for (let x = x0; x < pxb; x++) {
          const i = x + N * y + NN * z;
          const in0 = d[i] < 0;
          const yzOK = y >= 1 && z >= 1 && y <= N - 2 && z <= N - 2;
          const xzOK = x >= 1 && z >= 1 && x <= N - 2 && z <= N - 2;
          const xyOK = x >= 1 && y >= 1 && x <= N - 2 && y <= N - 2;
          if (x < N - 1 && yzOK && in0 !== (d[i + 1] < 0)) {
            emit(cell(x, y - 1, z - 1), cell(x, y, z - 1), cell(x, y, z), cell(x, y - 1, z), in0);
          }
          if (y < N - 1 && xzOK && in0 !== (d[i + N] < 0)) {
            emit(cell(x - 1, y, z - 1), cell(x - 1, y, z), cell(x, y, z), cell(x, y, z - 1), in0);
          }
          if (z < N - 1 && xyOK && in0 !== (d[i + NN] < 0)) {
            emit(cell(x - 1, y - 1, z), cell(x, y - 1, z), cell(x, y, z), cell(x - 1, y, z), in0);
          }
        }
      }
    }

    return {
      positions: pos.slice(0, nv * 3),
      normals: nor.slice(0, nv * 3),
      colors: vcol.slice(0, nv * 3),
      indices: idx.slice(0, ni),
    };
  }
}
