#!/usr/bin/env bash
set -uo pipefail

cd "$(dirname "$0")"

IMAGE=${IMAGE:-dd-arm64-repro:net10}
RUNS=${RUNS:-20}
PAR=${PAR:-3}
CPUS=${CPUS:-3}
DURATION=${DURATION:-120}
VARIANT=${VARIANT:-default}
ENV_FILE=${ENV_FILE-profiling.env}
EXTRA_ENV=${EXTRA_ENV:-}
KEEP_CLEAN=${KEEP_CLEAN:-0}
OUT=${OUT:-$PWD/results/$(date +%Y%m%dT%H%M%S)-${VARIANT}}

mkdir -p "$OUT"
echo "image=$IMAGE runs=$RUNS par=$PAR cpus=$CPUS duration=$DURATION env_file=$ENV_FILE extra_env='$EXTRA_ENV'" > "$OUT/config.txt"

one() {
  local i=$1
  local dir="$OUT/run-$(printf %03d "$i")"
  local name="repro-${VARIANT}-$$-$i"
  mkdir -p "$dir"
  local extra=()
  for kv in $EXTRA_ENV; do extra+=(-e "$kv"); done
  local env_args=()
  [ -n "$ENV_FILE" ] && env_args=(--env-file "$ENV_FILE")
  local start
  start=$(date +%s)
  docker run --name "$name" --platform linux/arm64 --cpus "$CPUS" --memory 3g \
    "${env_args[@]}" "${extra[@]}" \
    -e DD_INJECT_START_TIME="$(date +%s)" -e REPRO_DURATION_SECONDS="$DURATION" \
    -v "$dir:/dumps" "$IMAGE" > "$dir/stdout.txt" 2>&1
  local code
  code=$(docker inspect "$name" --format '{{.State.ExitCode}}')
  echo $(( $(date +%s) - start )) > "$dir/elapsed_seconds"
  docker rm "$name" > /dev/null
  echo "$code" > "$dir/exit_code"
  if [ "$code" = "0" ]; then
    echo "run $i: clean"
    [ "$KEEP_CLEAN" = "1" ] || rm -rf "$dir"
  else
    echo "run $i: CRASH exit=$code after ~$(cat "$dir/elapsed_seconds")s $(grep -h '^\[createdump\] Crashing thread' "$dir/stdout.txt" | head -1)"
  fi
}
export -f one
export OUT IMAGE CPUS DURATION VARIANT ENV_FILE EXTRA_ENV KEEP_CLEAN

seq 1 "$RUNS" | xargs -P "$PAR" -I{} bash -c 'one {}' | tee "$OUT/summary.txt"

crashes=$(grep -c CRASH "$OUT/summary.txt" || true)
echo "variant=$VARIANT runs=$RUNS crashes=$crashes out=$OUT" | tee -a "$OUT/summary.txt"
