#!/usr/bin/env bash
# 30 分钟长稳的故障注入序列（与宿主运行时间对齐）
set -u
CTL="D:/IT/SuperModbus/SuperSampler/samples/InjectionLineMonitor/run5/ctl.json"
T0="${1:-$(date +%s)}"
wait_until() { local target=$((T0 + $1)); while [ "$(date +%s)" -lt "$target" ]; do sleep 0.2; done; }
step() { printf '%s' "$2" > "$CTL"; echo "[inject] T+$(( $(date +%s) - T0 ))s $1"; }

wait_until 300;  step "refuse 3s @2502"  '{"seq":201,"commands":[{"action":"offline","port":2502,"mode":"refuse","seconds":3}]}'
wait_until 600;  step "silent 5s @2502"  '{"seq":202,"commands":[{"action":"offline","port":2502,"mode":"silent","seconds":5}]}'
wait_until 900;  step "drop 30% @2503"   '{"seq":203,"commands":[{"action":"drop","port":2503,"percent":30,"direction":"both"}]}'
wait_until 990;  step "clear @2503"      '{"seq":204,"commands":[{"action":"online","port":2503}]}'
wait_until 1200; step "delay 300ms @2502" '{"seq":205,"commands":[{"action":"delay","port":2502,"ms":300}]}'
wait_until 1290; step "clear @2502"      '{"seq":206,"commands":[{"action":"online","port":2502}]}'
wait_until 1500; step "halfopen 10s @2504" '{"seq":207,"commands":[{"action":"offline","port":2504,"mode":"halfopen","seconds":10}]}'
wait_until 1620; step "refuse 4s @2505"  '{"seq":208,"commands":[{"action":"offline","port":2505,"mode":"refuse","seconds":4}]}'
wait_until 1700; step "final clear"      '{"seq":209,"commands":[{"action":"online","port":2502},{"action":"online","port":2503},{"action":"online","port":2504},{"action":"online","port":2505}]}'
echo "[inject] done"
