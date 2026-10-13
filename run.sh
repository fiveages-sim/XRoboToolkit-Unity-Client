#!/usr/bin/env bash
# run_all_frames.sh
# 遍历 ./pull/Download 下所有 frames_YYYYMMDD_HHMMSS.jsonl，
# 逐个调用 ./Docs/for4u_frames_to_mp4.sh ./pull/Download <时间戳>

set -euo pipefail

DIR="./pull/Download"
SCRIPT="./Docs/for4u_frames_to_mp4.sh"

shopt -s nullglob

for f in "$DIR"/frames_*.jsonl; do
    # 取文件名（去掉目录），再去掉前缀 frames_ 和后缀 .jsonl
    base="$(basename "$f")"
    ts="${base#frames_}"
    ts="${ts%.jsonl}"

    echo ">>> 处理: $f  (ts=$ts)"
    "$SCRIPT" "$DIR" "$ts"
done

echo "全部完成。"
