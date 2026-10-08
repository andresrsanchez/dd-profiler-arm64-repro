sh "$(dirname "$0")/gdb-setup.sh" >/dev/null 2>&1
for core in "$@"; do
  dir=$(dirname "$core")
  tid=$(grep -h 'Crashing thread' "$dir/stdout.txt" 2>/dev/null | sed -E 's/.*Crashing thread ([0-9a-f]+).*/\1/' | head -1)
  lwp=$(printf '%d' "0x${tid:-0}")
  bt=$(timeout 240 gdb -q -batch -ex "set pagination off" -ex "core-file $core" -ex "thread find LWP $lwp\$" /usr/share/dotnet/dotnet 2>/dev/null | sed -n -E 's/^Thread ([0-9]+) has target id.*/\1/p' | head -1 | xargs -I{} timeout 240 gdb -q -batch -ex "set pagination off" -ex "core-file $core" -ex "thread {}" -ex "bt 40" /usr/share/dotnet/dotnet 2>/dev/null | sed -n '/<signal handler called>/,$p')
  echo "$bt" > "$dir/crash-bt.txt"
  if echo "$bt" | grep -q 'SamplesCollector::'; then kind="A:SamplesCollector/$(echo "$bt" | grep -o -m1 -E 'FrameStore::[A-Za-z]+|[A-Za-z]+::[A-Za-z]+ \(' | head -1)";
  elif echo "$bt" | grep -q 'TimerCreateCpuProfiler::Collect'; then kind="B:SIGPROF-handler/$(echo "$bt" | grep -o -m1 -E '_ULaarch64_[a-z_]+|dwarf_[a-z_]+|HybridUnwinder::[A-Za-z]+' | head -1)";
  elif echo "$bt" | grep -q 'LinuxStackFramesCollector'; then kind="C:SIGUSR1-walltime";
  else kind="other"; fi
  interrupted=$(echo "$bt" | awk '/<signal handler called>/{n++} n==2{getline; print; exit}' | sed -E 's/^#[0-9]+ +(0x[0-9a-f]+ in )?//' | cut -c1-110)
  echo "$(basename $dir) $kind | interrupted: ${interrupted:--}"
done
