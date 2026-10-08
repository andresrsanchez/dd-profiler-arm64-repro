import glob
import gzip
import json
import subprocess
import statistics
import sys


def varint(buf, i):
    shift = result = 0
    while True:
        b = buf[i]
        i += 1
        result |= (b & 0x7F) << shift
        if b < 0x80:
            return result, i
        shift += 7


def fields(buf):
    i = 0
    while i < len(buf):
        key, i = varint(buf, i)
        number, wire = key >> 3, key & 7
        if wire == 0:
            value, i = varint(buf, i)
        elif wire == 2:
            length, i = varint(buf, i)
            value = buf[i:i + length]
            i += length
        elif wire == 1:
            value = buf[i:i + 8]
            i += 8
        elif wire == 5:
            value = buf[i:i + 4]
            i += 4
        else:
            raise ValueError(wire)
        yield number, wire, value


def packed(wire, value):
    if wire == 0:
        return [value]
    out, i = [], 0
    while i < len(value):
        v, i = varint(value, i)
        out.append(v)
    return out


def parse(path):
    raw = open(path, "rb").read()
    if raw[:4] == b"\x28\xb5\x2f\xfd":
        data = subprocess.run(["zstd", "-dc"], input=raw, capture_output=True, check=True).stdout
    elif raw[:2] == b"\x1f\x8b":
        data = gzip.decompress(raw)
    else:
        data = raw
    strings, sample_types, samples, locations, functions = [], [], [], {}, {}
    for number, wire, value in fields(data):
        if number == 6:
            strings.append(value.decode("utf-8", "replace"))
        elif number == 1:
            sample_types.append(dict((n, v) for n, _, v in fields(value)))
        elif number == 2:
            locs, vals = [], []
            for n, w, v in fields(value):
                if n == 1:
                    locs += packed(w, v)
                elif n == 2:
                    vals += packed(w, v)
            samples.append((locs, vals))
        elif number == 4:
            loc_id, funcs = None, []
            for n, w, v in fields(value):
                if n == 1:
                    loc_id = v
                elif n == 4:
                    funcs.append(dict((a, b) for a, _, b in fields(v)).get(1))
            locations[loc_id] = funcs
        elif number == 5:
            f = dict((n, v) for n, _, v in fields(value))
            functions[f.get(1)] = f.get(2, 0)
    types = [strings[t.get(1, 0)] for t in sample_types]
    def frame_names(locs):
        return [strings[functions.get(f, 0)] for loc in locs for f in locations.get(loc, [])]
    return types, [(frame_names(locs), vals) for locs, vals in samples]


def main(batch_dirs):
    for batch in batch_dirs:
        name = batch.rstrip("/").split("/")[-1]
        for kind in ("cpu", "wall"):
            depths, managed, total = [], 0, 0
            for path in glob.glob(f"{batch}/run-*/pprof/*.pprof"):
                types, samples = parse(path)
                if kind not in types:
                    continue
                k = types.index(kind)
                for names, vals in samples:
                    if k >= len(vals) or vals[k] == 0:
                        continue
                    total += 1
                    depths.append(len(names))
                    if any("|lm:" in n and "|fn:" in n for n in names):
                        managed += 1
            if total:
                print(f"{name} {kind}: samples={total} mean_depth={statistics.mean(depths):.2f} "
                      f"median_depth={statistics.median(depths)} with_managed_frame={managed / total:.2%}")
        discards = {}
        for path in glob.glob(f"{batch}/run-*/pprof/metrics_*.json"):
            for key, value in json.load(open(path)):
                if "empty_backtrace" in key or key.endswith("sampling_requests_count"):
                    discards[key] = discards.get(key, 0) + value
        for key in sorted(discards):
            print(f"{name} {key}={discards[key]:.0f}")


if __name__ == "__main__":
    main(sys.argv[1:])
