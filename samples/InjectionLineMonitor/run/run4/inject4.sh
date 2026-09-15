#!/usr/bin/env bash
set -u
CTL="D:/IT/SuperModbus/SuperSampler/samples/InjectionLineMonitor/run/ctl.json"
T0="${1:-$(date +%s)}"
wait_until() { local target=$((T0 + $1)); while [ "$(date +%s)" -lt "$target" ]; do sleep 0.2; done; }
wait_until 19
printf '{"seq":301,"commands":[{"action":"offline","port":2502,"mode":"refuse","seconds":8,"reason":"drill4-refuse-8s"}]}' > "$CTL"
echo "[inject4] $(date +%H:%M:%S) -> refuse 8s @2502"
wait_until 31
cat "D:/IT/SuperModbus/SuperSampler/samples/InjectionLineMonitor/run/ctl.ack.json"
