"""SPZ v4 の点を間引いて、SPZ v4 のまま書き出す。

スマホで重いゾーンを軽くするために使う。画面への寄与が小さい点（薄くて小さい点）から捨てる。
残した点の値は量子化し直さないので、残った点の見た目は元と同じ。

使い方:
  uv run --with numpy --with zstandard deploy/spz_decimate.py in.spz out.spz [--keep 0.5] [--sh 3]
"""
import argparse
import struct

import numpy as np
import zstandard

HEADER_SIZE = 32
TOC_ENTRY_SIZE = 16


def read_streams(raw):
    magic, version, count = struct.unpack_from("<III", raw, 0)
    degree, fractional, flags, stream_count = struct.unpack_from("<BBBB", raw, 12)
    if magic != 0x5053474E or version != 4:
        raise ValueError(f"SPZ v4 だけ対応（version={version}）")
    (toc_offset,) = struct.unpack_from("<I", raw, 16)
    sizes = [struct.unpack_from("<QQ", raw, toc_offset + i * TOC_ENTRY_SIZE) for i in range(stream_count)]
    # 目次の直後から、属性ごとの zstd ストリームが順に並ぶ
    offset = toc_offset + stream_count * TOC_ENTRY_SIZE
    streams = []
    for compressed, expanded in sizes:
        data = zstandard.ZstdDecompressor().decompress(raw[offset:offset + compressed], max_output_size=expanded)
        streams.append(np.frombuffer(data, dtype=np.uint8).reshape(count, -1))
        offset += compressed
    return {"count": count, "degree": degree, "fractional": fractional, "flags": flags}, streams


def importance(streams):
    """不透明度 × 見かけの面積（大きさの積の 2/3 乗）。"""
    opacity = streams[1][:, 0].astype(np.float32) / 255.0
    log_scale = streams[3].astype(np.float32) / 16.0 - 10.0
    return opacity * np.exp(log_scale.sum(axis=1) * (2.0 / 3.0))


def reduce_sh(stream, degree, target):
    """SH 係数は次数の低い順に並ぶので、先頭だけ残せば次数を下げられる。"""
    return stream[:, :((target + 1) ** 2 - 1) * 3]


def write_spz(path, header, streams):
    count = streams[0].shape[0]
    compressor = zstandard.ZstdCompressor(level=12)
    payloads = [compressor.compress(np.ascontiguousarray(s).tobytes()) for s in streams]
    out = bytearray(struct.pack("<IIIBBBBI", 0x5053474E, 4, count, header["degree"], header["fractional"],
                                header["flags"], len(streams), HEADER_SIZE))
    out += b"\0" * 12  # 予約領域（0 でなければ読み手が弾く）
    for stream, payload in zip(streams, payloads):
        out += struct.pack("<QQ", len(payload), stream.size)
    for payload in payloads:
        out += payload
    open(path, "wb").write(out)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("src")
    parser.add_argument("dst")
    parser.add_argument("--keep", type=float, default=0.5, help="残す割合（0〜1）")
    parser.add_argument("--sh", type=int, default=None, help="SH の次数をここまで下げる（0〜元の次数）")
    args = parser.parse_args()

    header, streams = read_streams(open(args.src, "rb").read())
    keep_count = max(1, int(header["count"] * args.keep))
    order = np.argsort(-importance(streams))[:keep_count]
    order.sort()  # 元の並び（空間的に近い点が近くにある）を保つと圧縮が効く
    streams = [s[order] for s in streams]

    if args.sh is not None and args.sh < header["degree"]:
        if args.sh == 0:
            streams = streams[:5]  # 0 次では SH のストリーム自体を置かない
        else:
            streams[5] = reduce_sh(streams[5], header["degree"], args.sh)
        header["degree"] = args.sh

    write_spz(args.dst, header, streams)
    print(f"点 {header['count']:,} → {keep_count:,}、SH {header['degree']} 次")


if __name__ == "__main__":
    main()
