#!/usr/bin/env bash
# 断线演练注入器：写断路器控制文件（ASCII JSON，避免中文编码坑）
# 用法：bash inject.sh <host_start_epoch>   —— 时间点与 drill_ops.txt 对齐
set -u
CTL="D:/IT/SuperModbus/SuperSampler/samples/InjectionLineMonitor/run/ctl.json"
T0="${1:-$(date +%s)}"

wait_until() {  # $1 = 相对秒
  local target=$((T0 + $1))
  while [ "$(date +%s)" -lt "$target" ]; do sleep 0.2; done
}

echo "[inject] T0=$T0  now=$(date +%s)"

wait_until 20
printf '{"seq":101,"commands":[{"action":"offline","port":2502,"mode":"refuse","seconds":3,"reason":"drill-refuse-3s"}]}' > "$CTL"
echo "[inject] $(date +%H:%M:%S) T+$(( $(date +%s) - T0 ))s -> offline refuse 3s @2502"

wait_until 45
printf '{"seq":102,"commands":[{"action":"offline","port":2502,"mode":"silent","seconds":5,"reason":"drill-silent-5s"}]}' > "$CTL"
echo "[inject] $(date +%H:%M:%S) T+$(( $(date +%s) - T0 ))s -> offline silent 5s @2502"

sleep 8
echo "[inject] ack:"
cat "D:/IT/SuperModbus/SuperSampler/samples/InjectionLineMonitor/run/ctl.ack.json" 2>/dev/null
echo
