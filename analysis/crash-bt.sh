sh "$(dirname "$0")/gdb-setup.sh"
for core in "$@"; do
  dir=$(dirname "$core")
  tid=$(grep -h 'Crashing thread' "$dir/stdout.txt" 2>/dev/null | sed -E 's/.*Crashing thread ([0-9a-f]+).*/\1/' | head -1)
  lwp=$(printf '%d' "0x${tid:-0}")
  num=$(gdb -q -batch -ex "core-file $core" -ex "thread find LWP $lwp\$" /usr/share/dotnet/dotnet 2>/dev/null | sed -n -E 's/^Thread ([0-9]+) has target id.*/\1/p' | head -1)
  echo "=== $core lwp=$lwp gdbthread=$num"
  timeout 240 gdb -q -batch -ex "set pagination off" -ex "core-file $core" -ex "thread ${num:-1}" -ex "bt 30" /usr/share/dotnet/dotnet 2>/dev/null | sed -n '/<signal handler called>/,$p' | head -24
done
