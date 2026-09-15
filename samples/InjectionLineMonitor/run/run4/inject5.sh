#!/usr/bin/env bash
set -u
T0="${1:-$(date +%s)}"
SIMDIR="D:/IT/SuperModbus/_simulator_design/tools"
LOG="D:/IT/SuperModbus/SuperSampler/samples/InjectionLineMonitor/run/complex_sim.log"
wait_until() { local target=$((T0 + $1)); while [ "$(date +%s)" -lt "$target" ]; do sleep 0.2; done; }

wait_until 19
PID=$(netstat -ano | grep -E "0.0.0.0:16002 .*LISTENING" | awk '{print $NF}' | head -1)
echo "[inject5] $(date +%H:%M:%S) 停桩 pid=$PID"
taskkill //F //PID "$PID" > /dev/null 2>&1 || echo "[inject5] taskkill 失败"

wait_until 39
echo "[inject5] $(date +%H:%M:%S) 重新起桩"
( cd "$SIMDIR" && python complex_sim.py >> "$LOG" 2>&1 & )
sleep 6
netstat -ano | grep -E "0.0.0.0:1600[2-5] .*LISTENING" | head -4
