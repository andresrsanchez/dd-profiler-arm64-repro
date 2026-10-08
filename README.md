# dd-trace-dotnet profiler crash repro (linux-musl-arm64)

Local Docker repro for [DataDog/dd-trace-dotnet#9372](https://github.com/DataDog/dd-trace-dotnet/issues/9372)
on an arm64 Mac (or any arm64 Linux host).

The image mirrors what the Datadog Cluster Agent (v2 injection) does in Kubernetes:
`apm-inject` (`launcher.preload.so` via `LD_PRELOAD`) + `dd-lib-dotnet-init:3.55.0` copied to
`/opt/datadog/apm/library/dotnet`, Alpine 3.23, .NET 10.0.12 (or 9.0.20), profiling-only env
from `profiling.env`.

The app (`src/`) is a synthetic ASP.NET Core service that keeps doing startup-style work for
`REPRO_DURATION_SECONDS`: DI container builds and options binding, MVC controllers under local
HTTP load, EF Core + SQLite with lazy-loading proxies, expression compilation, Roslyn scripting,
time zones and cultures through ICU, collectible `AssemblyLoadContext` load/unload, and native
libraries (OpenSSL, zlib, Brotli).

## Build

```sh
python3 -I gen.py src/Generated.cs
docker build --platform linux/arm64 --build-arg TFM=net10.0 --build-arg DOTNET_RUNTIME=10.0.12 -t dd-arm64-repro:net10 .
docker build --platform linux/arm64 --build-arg TFM=net9.0 --build-arg DOTNET_RUNTIME=9.0.20 -t dd-arm64-repro:net9 .
docker build --platform linux/arm64 -f Dockerfile.analyze -t dd-arm64-repro:analyze .
```

`Dockerfile.analyze` expects `symbols/symbols/linux-musl-arm64` from the 3.55.0 release asset
`linux-native-symbols.tar.gz`.

## Run

```sh
RUNS=12 PAR=2 CPUS=3 VARIANT=full ./run.sh
ENV_FILE= RUNS=4 VARIANT=control ./run.sh
EXTRA_ENV="REPRO_ALC_WORKERS=0 REPRO_TZ_WORKERS=4 REPRO_NATIVE_WORKERS=4" VARIANT=native-heavy ./run.sh
EXTRA_ENV="REPRO_ALC_WORKERS=0 REPRO_TZ_WORKERS=4 REPRO_NATIVE_WORKERS=4 DD_PROFILING_CPU_ENABLED=0 DD_PROFILING_WALLTIME_ENABLED=0" VARIANT=no-cpu-no-walltime ./run.sh
```

Each crashed run keeps `results/<batch>/run-NNN/{core.1,stdout.txt,logs/}`. Clean runs are deleted
unless `KEEP_CLEAN=1`. Add `DD_INTERNAL_PROFILING_OUTPUT_DIR=/dumps/pprof` to `EXTRA_ENV` to keep the
profiles.

`native-heavy` (no collectible `AssemblyLoadContext` churn) isolates the unwinder crash; the default
workload also hits a second, unrelated crash in `FrameStore::GetManagedFrame` on unloaded modules.

With `DD_TRACE_DEBUG=1` the unwinder crash is more frequent, but startup can take longer than
`REPRO_DURATION_SECONDS`, and the run then ends with an unhandled `TaskCanceledException`
from Kestrel (SIGABRT on thread 1). Only count `signal 11` lines in `summary.txt`.

Workload knobs: `REPRO_{RESOLVE,DB,HTTP,EXPR,ALLOC,TZ,SCRIPT,ALC,NATIVE}_WORKERS`,
`REPRO_LOOP_SLEEP_MS`, `REPRO_DURATION_SECONDS`.

## Analyze

```sh
docker run --rm --platform linux/arm64 -v $PWD/results/<batch>:/dumps -v $PWD/analysis:/a \
  dd-arm64-repro:analyze -c 'sh /a/classify.sh /dumps/run-*/core.1'
```

`classify.sh` writes `crash-bt.txt` next to each core (the crashing thread from the
`<signal handler called>` frame down) and prints one line per crash.
`dump-unwind-recorder.sh <core>` prints the profiler's `UnwindingRecorder` trace for a crash from a
`DD_TRACE_DEBUG=1` run. `musl-eh-frame.sh` shows how little of Alpine's musl has `.eh_frame`.

`analysis/pprof_stats.py <batch>...` compares CPU and wall-time profiles between batches (managed
stack depth, empty-backtrace discards). `analysis/fp-chain.gdb.py` prints, for every thread of a
live process, gdb's backtrace next to the raw x29 frame-record chain.

## Building the profiler from source

`build-profiler/` builds `Datadog.Profiler.Native.so` for linux-musl-arm64 from a dd-trace-dotnet
checkout (Alpine, same toolchain as upstream CI) and swaps it into the repro image:

```sh
docker build --platform linux/arm64 -f build-profiler/Dockerfile.builder -t dd-profiler-builder:alpine-arm64 build-profiler
docker volume create dd-profiler-obj
docker run --rm --platform linux/arm64 -v <dd-trace-dotnet>:/src -v dd-profiler-obj:/obj -w /src dd-profiler-builder:alpine-arm64 \
  sh -c 'cmake -DCMAKE_CXX_COMPILER=clang++ -DCMAKE_C_COMPILER=clang -DCMAKE_BUILD_TYPE=Release -B /obj -S /src && cmake --build /obj --parallel --target profiler'
cp <dd-trace-dotnet>/artifacts/profiler-build/DDProf-Deploy/linux-arm64/Datadog.Profiler.Native.so build-profiler/out/
docker build --platform linux/arm64 -f build-profiler/Dockerfile.swap --build-arg SO=out/Datadog.Profiler.Native.so -t dd-arm64-repro:net10-local build-profiler
IMAGE=dd-arm64-repro:net10-local VARIANT=local ./run.sh
```

The native unit tests link a glibc-only libdatadog, so they build on Debian
(`build-profiler/Dockerfile.builder-debian`, target `profiler-native-tests`).

## Fix

`fix/hybridunwinder-stack-bounds.patch` applies to v3.55.0. Before each `unw_step`,
`HybridUnwinder::UnwindNativeFrames` stops unless SP is inside the thread's stack and not below
the previous frame's SP, and x29 is 0 or a frame record between SP and the top of the stack. It
adds `HybridUnwinderTest.cpp`, which builds the crashing frame layout in assembly.

Local builds of v3.55.0, `native-heavy` workload, counting SIGSEGV only:

| Build | Runs | SIGSEGV |
|---|---|---|
| Unpatched | 24 | 3 |
| Patched | 12 | 0 |
| Unpatched, `DD_TRACE_DEBUG=1` | 10 | 5 |
| Patched, `DD_TRACE_DEBUG=1` | 10 | 0 |

Profiles keep their depth: mean stack depth is 17.7 frames patched vs 17.8 unpatched for wall
time, and 27.9 vs 28.1 for CPU.
