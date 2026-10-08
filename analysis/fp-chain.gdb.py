import gdb

def sym(addr):
    try:
        s = gdb.execute(f"info symbol {addr:#x}", to_string=True).strip()
    except gdb.error:
        return "??"
    if s.startswith("No symbol"):
        r = region(addr)
        if r.endswith(".dll"):
            return "MANAGED-R2R"
        if r == "anon-x":
            return "MANAGED-JIT"
        return f"[{r}]"
    return s.split(" in section")[0].split(" + ")[0][:60] + "  [" + s.split(" of ")[-1].split("/")[-1][:24] + "]"

inferior = gdb.selected_inferior()
maps = []
for line in open(f"/proc/{inferior.pid}/maps"):
    parts = line.split()
    lo, hi = (int(x, 16) for x in parts[0].split("-"))
    maps.append((lo, hi, parts[1], parts[5].split("/")[-1] if len(parts) > 5 else ""))

def region(addr):
    for lo, hi, perms, name in maps:
        if lo <= addr < hi:
            return name or ("anon-x" if "x" in perms else "anon")
    return "unmapped"
read = lambda a: int.from_bytes(inferior.read_memory(a, 8).tobytes(), "little")

for thread in inferior.threads():
    thread.switch()
    frame = gdb.newest_frame()
    pc = int(frame.read_register("pc"))
    x29 = int(frame.read_register("x29"))
    sp = int(frame.read_register("sp"))
    print(f"=== thread {thread.num} lwp={thread.ptid[1]} name={thread.name} pc={sym(pc)}")
    bt, f = [], frame
    while f is not None and len(bt) < 40:
        bt.append(sym(int(f.pc())))
        try:
            f = f.older()
        except gdb.error:
            break
    print("  dwarf:", " <- ".join(bt))
    chain, fp, prev = [], x29, 0
    while len(chain) < 40:
        if fp == 0 or fp % 8 or fp <= prev or fp < sp or fp - sp > 16 * 1024 * 1024:
            chain.append(f"STOP(fp={fp:#x})")
            break
        try:
            ip = read(fp + 8)
            nxt = read(fp)
        except gdb.MemoryError:
            chain.append("STOP(unreadable)")
            break
        if ip == 0:
            chain.append("END")
            break
        chain.append(sym(ip))
        prev, fp = fp, nxt
    print("  fpchain:", " <- ".join(chain))
