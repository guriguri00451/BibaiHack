import { LocalTarget, ServerTarget } from './uploader.js';

// 投稿のパネル (画面) と、XR の中の投稿ボタン。
// 流れ: 粘土で形を作る → 「塗る」で色を付ける → 投稿 (GLB を上げて、オブジェクトとして登録)
//   投稿したあとも粘土はそのまま残り、直してから「更新して投稿」で新しい版を上げられる。
//   「新しく作る」で立方体に戻すと、次は別のオブジェクトとして投稿される。

const NAME_KEY = 'mechabibai.name';
const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const kb = (n) => `${Math.max(1, Math.round(n / 1024))}KB`;
const $ = (sel) => document.querySelector(sel);

// h: main.js から渡す道具
//   renderer, workspace, ButtonPanel, exportClay() → { bytes, anchor, quaternion, scale },
//   snapshot(), restore(snap), newClay(), log(msg), xrMode()
export function initPost(h) {
  const dom = { panel: $('#post'), body: $('#pbody'), status: $('#pstatus'), toasts: $('#toasts') };
  const params = new URLSearchParams(location.search);
  const draft = {
    name: (() => { try { return localStorage.getItem(NAME_KEY) || ''; } catch { return ''; } })(),
    code: (params.get('room') || '').toUpperCase(),
    server: params.get('server') || '',
  };

  let target = new LocalTarget();
  const posted = new Map();  // objectId → { id, n, version, bytes, snapshot }
  let counter = 0;
  let editingId = null;      // 粘土がいま表している投稿 (あれば「更新」になる)
  let busy = false;
  let lastHTML = '';

  function toast(msg, kind = 'info') {
    const el = document.createElement('div');
    el.className = `toast ${kind}`;
    el.textContent = msg;
    dom.toasts.appendChild(el);
    setTimeout(() => el.classList.add('out'), 3600);
    setTimeout(() => el.remove(), 4200);
    h.log(msg);
  }

  // 体積をそろえるために大きさを直した倍率 (ほぼ 1 のときは出さない)
  const sizeNote = (ex) => (Math.abs(ex.factor - 1) > 0.01 ? `、大きさを ×${ex.factor.toFixed(2)} にそろえた` : '');

  // 粘土の位置・回転・大きさ (仕様 3.5 の Transform)。位置は底面の中心
  const transformOf = (ex) => ({
    position: ex.anchor.toArray(),
    rotation: ex.quaternion.toArray(),
    scale: [ex.scale, ex.scale, ex.scale],
  });

  // ---------- 操作 ----------
  async function post() {
    if (busy) return;
    busy = true;
    render();
    try {
      const ex = await h.exportClay();
      const assetUrl = await target.uploadAsset(ex.bytes);
      const transform = transformOf(ex);
      const item = editingId && posted.get(editingId);
      if (item) {
        await target.updateObject({ objectId: item.id, assetUrl, transform });
        Object.assign(item, { version: item.version + 1, bytes: ex.bytes, snapshot: h.snapshot() });
        toast(`#${item.n} を更新しました (${kb(ex.bytes.byteLength)}${sizeNote(ex)})`, 'good');
      } else {
        const { objectId } = await target.createObject({ assetUrl, transform });
        posted.set(objectId, { id: objectId, n: ++counter, version: 1, bytes: ex.bytes, snapshot: h.snapshot() });
        editingId = objectId;
        toast(`#${counter} を投稿しました (${kb(ex.bytes.byteLength)}${sizeNote(ex)})`, 'good');
      }
    } catch (err) {
      toast(err?.message || String(err), 'error');
    } finally {
      busy = false;
      render();
    }
  }

  function newClay() {
    editingId = null;
    h.newClay();
    render();
  }

  function reedit(id) {
    const item = posted.get(id);
    if (!item) return;
    h.restore(item.snapshot);
    editingId = id;
    render();
    toast(`#${item.n} を粘土に読み込みました。直したら「更新して投稿」`);
  }

  async function remove(id) {
    const item = posted.get(id);
    if (!item || !confirm(`#${item.n} の投稿を取り消しますか？`)) return;
    try {
      await target.deleteObject(id);
      posted.delete(id);
      if (editingId === id) editingId = null;
      render();
    } catch (err) { toast(err.message, 'error'); }
  }

  function download(id) {
    const item = posted.get(id);
    if (!item) return;
    const url = URL.createObjectURL(new Blob([item.bytes], { type: 'model/gltf-binary' }));
    const a = Object.assign(document.createElement('a'), { href: url, download: `clay-${item.n}-v${item.version}.glb` });
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }

  // 投稿先を替えると、それまでの投稿の一覧は空にする (別の場所の話になるので)
  function switchTarget(t) {
    target.close();
    target = t;
    posted.clear();
    editingId = null;
    counter = 0;
  }

  async function connect() {
    try { localStorage.setItem(NAME_KEY, draft.name); } catch { /* 無視 */ }
    busy = true;
    render();
    try {
      const t = new ServerTarget(draft.server.trim());
      await t.join({ code: draft.code.trim().toUpperCase(), name: draft.name });
      t.onclose = () => {
        if (target !== t) return;
        switchTarget(new LocalTarget());
        toast('サーバーとの接続が切れました。投稿先をこのブラウザ内に戻しました', 'error');
        render();
      };
      switchTarget(t);
      toast(`サーバーにつながりました (${t.label})`, 'good');
    } catch (err) {
      toast(err?.message || String(err), 'error');
    } finally {
      busy = false;
      render();
    }
  }

  function disconnect() {
    switchTarget(new LocalTarget());
    render();
  }

  // ---------- 画面のパネル ----------
  function render() {
    const editing = editingId && posted.get(editingId);
    const items = [...posted.values()];
    const html = `
      <ol class="steps">
        <li>左のパネルの道具で<b>形を作る</b></li>
        <li>「塗る」と色見本で<b>色を付ける</b></li>
        <li><b>投稿</b>する (直したら「更新して投稿」)。大きさは体積でそろえられる</li>
      </ol>
      <button class="primary wide" data-act="post" ${busy ? 'disabled' : ''}>${busy ? '送信中…' : editing ? `#${editing.n} を更新して投稿` : '投稿する'}</button>
      <div class="row2">
        <button data-act="newclay" ${busy ? 'disabled' : ''}>新しく作る</button>
        ${editing ? '<button data-act="stopedit">編集をやめる</button>' : ''}
      </div>
      <div class="label">投稿したモデル (${items.length})</div>
      ${items.length ? `<ul class="objs">${items.map((it) => `
        <li class="${editingId === it.id ? 'sel' : ''}">
          <span class="objname" title="${editingId === it.id ? '粘土で編集中' : ''}"><b>#${it.n}</b> <span class="muted">v${it.version} · ${kb(it.bytes.byteLength)}</span></span>
          <span class="objbtns">
            <button data-act="reedit" data-id="${esc(it.id)}">作り直す</button>
            <button data-act="download" data-id="${esc(it.id)}" title="GLB を保存">GLB</button>
            <button data-act="delete" data-id="${esc(it.id)}">取消</button>
          </span>
        </li>`).join('')}</ul>` : '<p class="muted">まだ投稿していません。</p>'}
      <details class="sub" ${target.kind === 'ws' ? 'open' : ''}>
        <summary>投稿先: ${esc(target.label)}</summary>
        ${target.kind === 'ws' ? `
          <p class="hint">${esc(target.url)} の部屋 ${esc(target.session?.code)} に投稿しています。</p>
          <button data-act="disconnect">切断して、このブラウザ内に戻す</button>`
    : `
          <p class="hint">いまはサーバーにつながっていないので、投稿はこのページの中にだけ置かれます (読み込み直すと消えます)。ゲームサーバーができたら、ここから接続してください。</p>
          <label class="field"><span>名前</span><input data-draft="name" maxlength="20" value="${esc(draft.name)}"></label>
          <label class="field"><span>部屋のコード</span><input data-draft="code" maxlength="8" value="${esc(draft.code)}" autocapitalize="characters"></label>
          <label class="field"><span>サーバー (WebSocket)</span><input data-draft="server" placeholder="wss://…" value="${esc(draft.server)}"></label>
          <button data-act="connect" ${busy || !draft.server.trim() ? 'disabled' : ''}>接続する</button>`}
      </details>`;
    if (html !== lastHTML) {
      dom.body.innerHTML = html;
      lastHTML = html;
    }
    dom.status.innerHTML = `<span class="chip">${esc(target.label)}</span><span class="chip">投稿 ${posted.size}</span>`;
  }

  dom.body.addEventListener('click', (e) => {
    const b = e.target.closest('[data-act]');
    if (!b || b.disabled) return;
    const id = b.dataset.id;
    switch (b.dataset.act) {
      case 'post': post(); break;
      case 'newclay': newClay(); break;
      case 'stopedit': editingId = null; render(); break;
      case 'reedit': reedit(id); break;
      case 'download': download(id); break;
      case 'delete': remove(id); break;
      case 'connect': connect(); break;
      case 'disconnect': disconnect(); break;
      default: break;
    }
  });
  dom.body.addEventListener('input', (e) => {
    const key = e.target.dataset.draft;
    if (!key) return;
    draft[key] = key === 'code' ? e.target.value.toUpperCase() : e.target.value;
    // 接続ボタンの有効・無効だけ切り替える (入力中に書き換えない)
    const btn = dom.body.querySelector('[data-act="connect"]');
    if (btn) btn.disabled = busy || !draft.server.trim();
  });
  dom.panel.querySelector('.fold').addEventListener('click', () => dom.panel.classList.toggle('collapsed'));
  addEventListener('pagehide', () => target.close());

  // ---------- XR: 投稿のボタン ----------
  const bw = 0.09, bh = 0.036, gap = 0.006;
  const xrPanel = new h.ButtonPanel([
    { id: 'status', label: () => xrStatus(), x: 0, y: 0, w: 2 * bw + gap, h: 0.03 },
    { id: 'post', label: () => (busy ? '送信中…' : editingId ? `#${posted.get(editingId)?.n} を更新` : '投稿する'),
      x: -(bw + gap) / 2, y: -0.04, w: bw, h: bh, color: '#81c784', onPress: post, isEnabled: () => !busy },
    { id: 'new', label: '新しく作る', x: (bw + gap) / 2, y: -0.04, w: bw, h: bh, onPress: newClay, isEnabled: () => !busy },
  ]);
  xrPanel.group.position.set(0, 0.2, -0.08);
  xrPanel.group.visible = false;
  h.workspace.add(xrPanel.group);

  function xrStatus() {
    const e = editingId && posted.get(editingId);
    return `投稿 ${posted.size} 件 · ${target.label}${e ? ` · #${e.n} を編集中` : ''}`;
  }

  render();

  return {
    xrPanelPress(tip, hs) { return xrPanel.group.visible && xrPanel.press(tip, hs); },
    onXRChange() { xrPanel.group.visible = !!h.xrMode(); },
    update() { if (xrPanel.group.visible) xrPanel.refresh(); },
    get panels() { return [xrPanel]; },
    get target() { return target; },
    posted,
  };
}
