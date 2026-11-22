import * as THREE from 'three';

// XR 空間に浮かぶボタンとスライダー。指先で押す・なぞる。
// ボタン:     { id, label (文字列 or 関数), x, y, w, h, onPress, isActive, isEnabled, color, swatch }
//   swatch: true なら色見本 (常に color で塗り、選ばれていると白い太枠)
// スライダー: { id, type: 'slider', label, x, y, w, h, min, max, get, set, format }
export class ButtonPanel {
  constructor(defs, { title = [] } = {}) {
    this.group = new THREE.Group();
    this.buttons = [];
    const _v = new THREE.Vector3();
    this._v = _v;

    // 背景の板
    let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
    for (const d of defs) {
      minX = Math.min(minX, d.x - d.w / 2); maxX = Math.max(maxX, d.x + d.w / 2);
      minY = Math.min(minY, d.y - d.h / 2); maxY = Math.max(maxY, d.y + d.h / 2);
    }
    const pad = 0.012;
    const titleH = title.length ? 0.016 * title.length + 0.01 : 0;
    const plateW = maxX - minX + pad * 2, plateH = maxY - minY + pad * 2 + titleH;
    const plate = new THREE.Mesh(
      new THREE.PlaneGeometry(plateW, plateH),
      new THREE.MeshBasicMaterial({ color: 0x14171c, transparent: true, opacity: 0.82 }),
    );
    plate.position.set((minX + maxX) / 2, (minY + maxY) / 2 + titleH / 2, -0.003);
    this.group.add(plate);

    if (title.length) {
      const tp = makeTextPlane(title, plateW - pad * 2, titleH - 0.006);
      tp.position.set((minX + maxX) / 2, maxY + pad / 2 + titleH / 2, 0);
      this.group.add(tp);
    }

    for (const d of defs) {
      const canvas = document.createElement('canvas');
      canvas.width = 256;
      canvas.height = Math.round(256 * d.h / d.w);
      const tex = new THREE.CanvasTexture(canvas);
      tex.colorSpace = THREE.SRGBColorSpace;
      const mesh = new THREE.Mesh(
        new THREE.PlaneGeometry(d.w, d.h),
        new THREE.MeshBasicMaterial({ map: tex, transparent: true }),
      );
      mesh.position.set(d.x, d.y, 0);
      this.group.add(mesh);
      this.buttons.push({ ...d, canvas, tex, mesh, key: '', pressedAt: -1e9 });
    }
    this.refresh();
  }

  _drawSlider(b, label, v) {
    const ctx = b.canvas.getContext('2d');
    const W = b.canvas.width, H = b.canvas.height;
    ctx.clearRect(0, 0, W, H);
    ctx.beginPath();
    ctx.roundRect(4, 4, W - 8, H - 8, 18);
    ctx.fillStyle = '#2c313b';
    ctx.fill();
    const fs = Math.round(H * 0.3);
    ctx.font = `bold ${fs}px system-ui, sans-serif`;
    ctx.textBaseline = 'middle';
    ctx.fillStyle = '#e8eaee';
    ctx.textAlign = 'left';
    ctx.fillText(label, 18, H * 0.3);
    ctx.textAlign = 'right';
    ctx.fillStyle = '#c9ced8';
    ctx.fillText(b.format ? b.format(v) : v.toFixed(2), W - 18, H * 0.3);
    // つまみの溝
    const t = (v - b.min) / (b.max - b.min);
    const x0 = 24, x1 = W - 24, y = H * 0.7;
    ctx.lineCap = 'round';
    ctx.lineWidth = H * 0.12;
    ctx.strokeStyle = '#4a5160';
    ctx.beginPath(); ctx.moveTo(x0, y); ctx.lineTo(x1, y); ctx.stroke();
    ctx.strokeStyle = b.color || '#c98060';
    ctx.beginPath(); ctx.moveTo(x0, y); ctx.lineTo(x0 + (x1 - x0) * t, y); ctx.stroke();
    ctx.fillStyle = '#ffffff';
    ctx.beginPath(); ctx.arc(x0 + (x1 - x0) * t, y, H * 0.13, 0, Math.PI * 2); ctx.fill();
    b.tex.needsUpdate = true;
  }

  _draw(b, active, flash, label, enabled = true) {
    const ctx = b.canvas.getContext('2d');
    const W = b.canvas.width, H = b.canvas.height;
    ctx.clearRect(0, 0, W, H);
    ctx.globalAlpha = enabled ? 1 : 0.35;
    ctx.beginPath();
    ctx.roundRect(4, 4, W - 8, H - 8, Math.min(26, H * 0.3));
    if (b.swatch) {
      ctx.fillStyle = b.color;
      ctx.fill();
      ctx.lineWidth = active ? 14 : 4;
      ctx.strokeStyle = active ? '#ffffff' : '#4a5160';
      ctx.stroke();
    } else {
      ctx.fillStyle = flash ? '#ffffff' : active ? (b.color || '#4fc3f7') : '#2c313b';
      ctx.fill();
      ctx.lineWidth = 4;
      ctx.strokeStyle = active ? '#ffffff' : '#4a5160';
      ctx.stroke();
    }
    if (label) {
      ctx.fillStyle = active || flash ? '#111' : '#e8eaee';
      // 幅に収まるまで文字を小さくする
      let fs = Math.round(H * 0.42);
      ctx.font = `bold ${fs}px system-ui, sans-serif`;
      while (fs > 12 && ctx.measureText(label).width > W - 28) {
        fs -= 2;
        ctx.font = `bold ${fs}px system-ui, sans-serif`;
      }
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      ctx.fillText(label, W / 2, H / 2 + 2);
    }
    ctx.globalAlpha = 1;
    b.tex.needsUpdate = true;
  }

  refresh() {
    const now = performance.now();
    for (const b of this.buttons) {
      if (b.type === 'slider') {
        const v = b.get();
        const label = typeof b.label === 'function' ? b.label() : b.label;
        const key = `${label}|${b.format ? b.format(v) : v.toFixed(3)}`;
        if (key !== b.key) { b.key = key; this._drawSlider(b, label, v); }
        continue;
      }
      const active = b.isActive ? b.isActive() : false;
      const enabled = b.isEnabled ? b.isEnabled() : true;
      const flash = now - b.pressedAt < 180;
      const label = typeof b.label === 'function' ? b.label() : b.label;
      const key = `${active}|${flash}|${label}|${enabled}`;
      if (key !== b.key) {
        b.key = key;
        this._draw(b, active, flash, label, enabled);
      }
    }
  }

  // 指先が押していれば true (そのフレームは道具を使わない)
  press(tipWorld, hs) {
    if (!this.group.visible) return false;
    this.group.updateWorldMatrix(true, false);
    const p = this.group.worldToLocal(this._v.copy(tipWorld));
    let hit = null;
    for (const b of this.buttons) {
      if (!b.onPress && b.type !== 'slider') continue;
      if (b.isEnabled && !b.isEnabled()) continue;
      if (Math.abs(p.x - b.x) < b.w / 2 && Math.abs(p.y - b.y) < b.h / 2 && p.z < 0.015 && p.z > -0.025) {
        hit = b;
        break;
      }
    }
    if (!hit) {
      hs.pressed = null;
      return false;
    }
    if (hit.type === 'slider') {
      // なぞっている間ずっと、指の左右の位置で値を決める (端は少し余白をとる)
      const t = Math.min(1, Math.max(0, (p.x - (hit.x - hit.w / 2)) / hit.w * 1.1 - 0.05));
      hit.set(hit.min + t * (hit.max - hit.min));
      hs.pressed = hit.id;
      return true;
    }
    if (hs.pressed !== hit.id) {
      hs.pressed = hit.id;
      hit.pressedAt = performance.now();
      hit.onPress();
    }
    return true;
  }
}

function makeTextPlane(lines, w, h) {
  const canvas = document.createElement('canvas');
  canvas.width = 512;
  canvas.height = Math.round(512 * h / w);
  const ctx = canvas.getContext('2d');
  const lh = canvas.height / lines.length;
  ctx.fillStyle = '#c9ced8';
  ctx.font = `${Math.round(lh * 0.62)}px system-ui, sans-serif`;
  ctx.textBaseline = 'middle';
  lines.forEach((l, i) => ctx.fillText(l, 6, lh * (i + 0.5)));
  const tex = new THREE.CanvasTexture(canvas);
  tex.colorSpace = THREE.SRGBColorSpace;
  return new THREE.Mesh(new THREE.PlaneGeometry(w, h), new THREE.MeshBasicMaterial({ map: tex, transparent: true }));
}
