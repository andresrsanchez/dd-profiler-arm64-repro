set -e
for f in /ddsym/*.debug; do
  id=$(readelf -n "$f" | awk '/Build ID/ {print $3}')
  mkdir -p /usr/lib/debug/.build-id/${id:0:2}
  ln -sf "$f" /usr/lib/debug/.build-id/${id:0:2}/${id:2}.debug
done
