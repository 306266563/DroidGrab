#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
生成一个结构合法的合成 payload.bin，用于验证 C# 解析器。

覆盖的操作类型：REPLACE(raw) / REPLACE_XZ / REPLACE_BZ / ZERO
覆盖的结构特征：多分区、单分区多 operation、单 operation 多 extent、
              不连续 extent（含空洞）、非零起始块、partition sha256。

manifest 用标准 protobuf wire-format 手工编码，字段号与
update_engine/update_metadata.proto 对齐：
  DeltaArchiveManifest: block_size=3, minor_version=12, partitions=13
  PartitionUpdate:      partition_name=1, new_partition_info=7, operations=8
  PartitionInfo:        size=1, hash=2
  InstallOperation:     type=1, data_offset=2, data_length=3, dst_extents=6, data_sha256=8
  Extent:               start_block=1, num_blocks=2
"""
import argparse
import bz2
import hashlib
import json
import lzma
import os
import random
import struct

BLOCK = 4096
REPLACE, REPLACE_BZ, ZERO, REPLACE_XZ = 0, 1, 6, 8


def varint(value: int) -> bytes:
    out = bytearray()
    while True:
        b = value & 0x7F
        value >>= 7
        if value:
            out.append(b | 0x80)
        else:
            out.append(b)
            return bytes(out)


def tag(field: int, wire: int) -> bytes:
    return varint((field << 3) | wire)


def f_varint(field: int, value: int) -> bytes:
    return tag(field, 0) + varint(value)


def f_bytes(field: int, data: bytes) -> bytes:
    return tag(field, 2) + varint(len(data)) + data


def f_string(field: int, text: str) -> bytes:
    return f_bytes(field, text.encode("utf-8"))


def extent(start_block: int, num_blocks: int) -> bytes:
    return f_varint(1, start_block) + f_varint(2, num_blocks)


def install_op(op_type: int, data_offset: int, data_length: int,
               dst_extents, data_sha256: bytes = b"") -> bytes:
    body = f_varint(1, op_type) + f_varint(2, data_offset) + f_varint(3, data_length)
    for start, count in dst_extents:
        body += f_bytes(6, extent(start, count))
    if data_sha256:
        body += f_bytes(8, data_sha256)
    return body


def partition_update(name: str, size: int, part_hash: bytes, ops) -> bytes:
    info = f_varint(1, size) + f_bytes(2, part_hash)
    body = f_string(1, name) + f_bytes(7, info)
    for op in ops:
        body += f_bytes(8, op)
    return body


def delta_manifest(partitions) -> bytes:
    body = f_varint(3, BLOCK) + f_varint(12, 0)
    for part in partitions:
        body += f_bytes(13, part)
    return body


class Builder:
    def __init__(self):
        self.blobs = bytearray()
        self.offset = 0
        self.partitions = []
        self.expected = {}

    def _append(self, blob: bytes) -> int:
        start = self.offset
        self.blobs += blob
        self.offset += len(blob)
        return start

    def add_partition(self, name: str, content: bytes, op_type: int, chunk_blocks: int = 512):
        """按块切分，每块一个 operation，dst_extent 连续。"""
        assert len(content) % BLOCK == 0, "内容必须是块大小整数倍"
        chunk = chunk_blocks * BLOCK
        ops = []
        for i in range(0, len(content), chunk):
            piece = content[i:i + chunk]
            blocks = len(piece) // BLOCK
            start_block = i // BLOCK
            if op_type == REPLACE_XZ:
                blob = lzma.compress(piece, format=lzma.FORMAT_XZ)
            elif op_type == REPLACE_BZ:
                blob = bz2.compress(piece)
            else:
                blob = piece
            off = self._append(blob)
            ops.append(install_op(op_type, off, len(blob),
                                  [(start_block, blocks)], hashlib.sha256(blob).digest()))
        self._add(name, content, ops)

    def add_sparse_layout(self, name: str, total_blocks: int, segments, hole_blocks: int):
        """
        segments: [(start_block, data_bytes)]，之间留 hole_blocks 个零块。
        生成的文件内容 = 各 seg 数据按序铺开，中间补零。
        """
        ops = []
        expected = bytearray(b"\x00" * (total_blocks * BLOCK))
        for start_block, data in segments:
            off = self._append(data)
            ops.append(install_op(REPLACE, off, len(data), [(start_block, len(data) // BLOCK)]))
            expected[start_block * BLOCK:start_block * BLOCK + len(data)] = data
        # 中间空洞用 ZERO 操作显式声明
        cursor = 0
        for start_block, data in sorted(segments):
            if start_block > cursor:
                ops.append(install_op(ZERO, 0, 0, [(cursor, start_block - cursor)]))
            cursor = start_block + len(data) // BLOCK
        if cursor < total_blocks:
            ops.append(install_op(ZERO, 0, 0, [(cursor, total_blocks - cursor)]))
        self._add(name, bytes(expected), ops)

    def _add(self, name, content, ops):
        self.partitions.append(partition_update(name, len(content),
                                                hashlib.sha256(content).digest(), ops))
        self.expected[name] = {
            "size": len(content),
            "sha256": hashlib.sha256(content).hexdigest(),
        }
        os.makedirs(os.path.join(os.path.dirname(os.path.abspath(__file__)), "samples", "expected"),
                    exist_ok=True)

    def build(self, path: str):
        manifest = delta_manifest(self.partitions)
        header = b"CrAU" + struct.pack(">Q", 2) + struct.pack(">Q", len(manifest)) + struct.pack(">I", 0)
        with open(path, "wb") as f:
            f.write(header)
            f.write(manifest)
            f.write(self.blobs)
        return len(header) + len(manifest) + len(self.blobs)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", default=os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                                      "samples", "sample_full.bin"))
    args = parser.parse_args()
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    rng = random.Random(20261027)

    def blob(nbytes):
        return bytes(rng.getrandbits(8) for _ in range(nbytes))

    builder = Builder()

    # raw + 单 extent，4 MB
    builder.add_partition("boot", blob(4 * 1024 * 1024), REPLACE)
    # xz + 多 operation，12 MB
    builder.add_partition("system", blob(12 * 1024 * 1024), REPLACE_XZ)
    # bz2 + 多 operation，6 MB
    builder.add_partition("vendor", blob(6 * 1024 * 1024), REPLACE_BZ)
    # 空洞布局 + ZERO 操作：4 段数据夹零块
    seg_a, seg_b, seg_c = blob(2 * BLOCK), blob(4 * BLOCK), blob(1 * BLOCK)
    builder.add_sparse_layout("product", total_blocks=20,
                              segments=[(3, seg_a), (9, seg_b), (16, seg_c)], hole_blocks=2)
    # 单分区纯 ZERO
    builder.partitions.append(partition_update("odm_dlkm", 8 * BLOCK,
                                              hashlib.sha256(b"\x00" * (8 * BLOCK)).digest(),
                                              [install_op(ZERO, 0, 0, [(0, 8)])]))
    builder.expected["odm_dlkm"] = {"size": 8 * BLOCK,
                                    "sha256": hashlib.sha256(b"\x00" * (8 * BLOCK)).hexdigest()}

    total = builder.build(args.out)
    expected_path = os.path.join(os.path.dirname(args.out), "expected.json")
    with open(expected_path, "w", encoding="utf-8") as f:
        json.dump({"payload": os.path.basename(args.out), "partitions": builder.expected}, f, indent=2)

    print(f"生成 {args.out}  {total/1024/1024:.2f} MB")
    print(f"分区: {', '.join(builder.expected)}")
    for name, info in builder.expected.items():
        print(f"  {name:10s} {info['size']:>10,d} B  {info['sha256'][:16]}…")


if __name__ == "__main__":
    main()
