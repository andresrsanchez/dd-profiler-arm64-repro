L=/lib/ld-musl-aarch64.so.1
nm -D $L | grep -E ' (pthread_mutex_unlock|__pthread_mutex_unlock)$'
addr=$(nm -D $L | awk '$3=="pthread_mutex_unlock" {print $1}')
echo "pthread_mutex_unlock at 0x$addr"
readelf --debug-dump=frames-interp $L 2>/dev/null > /tmp/fde.txt
awk -v o=$((0x$addr + 16)) '/FDE cie=/ { split($0,a,"pc="); split(a[2],r,"\\.\\."); s=strtonum("0x" r[1]); e=strtonum("0x" r[2]); show = (o>=s && o<e); found = found || show } show { print } END { if (!found) print "NO FDE in .eh_frame covers pthread_mutex_unlock" }' /tmp/fde.txt | head -20
grep -c 'FDE cie=' /tmp/fde.txt | xargs echo "total FDEs in musl .eh_frame:"
objdump -d --start-address=0x$addr --stop-address=$(printf '0x%x' $((0x$addr + 0xd0))) $L | tail -52
