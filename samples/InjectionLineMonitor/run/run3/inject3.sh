#!/usr/bin/env bash
set -u
CTL="D:/IT/SuperModbus/SuperSampler/samples/InjectionLineMonitor/run/ctl.json"
T0="${1:-$(date +%s)}"
wait_until() { local target=$((T0 + $1)); while [ "$(date +%s)" -lt "$target" ]; do sleep 0.2; done; }
echo "[inject3] T0=$T0"
wait_until 19
printf '{"seq":201,"commands":[{"action":"offline","port":2502,"mode":"silent","seconds":10,"reason":"drill3-silent-10s"}]}' > "$CTL"
echo "[inject3] $(date +%H:%M:%S) -> silent 10s @2502"
wait_until 33
cat "D:/IT/SuperModbus/SuperSampler/samples/InjectionLineMonitor/run/ctl.ack.json"
