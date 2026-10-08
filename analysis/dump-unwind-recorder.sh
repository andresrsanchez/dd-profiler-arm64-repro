sh "$(dirname "$0")/gdb-setup.sh" >/dev/null 2>&1
core=$1
tid=$(grep -h 'Crashing thread' $(dirname $core)/stdout.txt | sed -E 's/.*Crashing thread ([0-9a-f]+).*/\1/' | head -1)
lwp=$(printf '%d' 0x$tid)
num=$(gdb -q -batch -ex "core-file $core" -ex "thread find LWP $lwp\$" /usr/share/dotnet/dotnet 2>/dev/null | sed -n -E 's/^Thread ([0-9]+) has target id.*/\1/p' | head -1)
cat > /tmp/r.gdb <<G
set pagination off
core-file $core
thread $num
bt 30
python
import gdb
f = gdb.newest_frame()
rec = None
while f is not None:
    if f.name() and "HybridUnwinder::Unwind" in f.name() and "Native" not in f.name():
        try:
            rec = f.read_var("recorder")
        except Exception as e:
            print("no recorder var", e)
        break
    f = f.older()
print("recorder =", rec)
if rec is not None and int(rec) != 0:
    r = rec.dereference()
    total = int(r["_totalEvents"])
    print("totalEvents", total)
    names = {0:"Start",1:"InitCursor",2:"NativeFrame",3:"ManagedTransition",4:"LibunwindStep",5:"FrameChainStep",6:"Finish"}
    methods = {0:"DWARF",1:"FALLBACK_FP",2:"FALLBACK_SP",3:"FALLBACK_LR",4:"FALLBACK_LR_NO_PROC_INFO"}
    for k in range(min(total, 256)):
        e = r["_entries"]["_M_elems"][k]
        t = int(e["eventType"])
        cs = e["cursorSnapshot"]
        if t in (1,4):
            print("%2d %-14s res=%d ip=%#x cfa=%#x locFp=%#x locLr=%#x step=%s dwarfret=%d" % (k, names[t], int(e["result"]), int(cs["ip"]), int(cs["cfa"]), int(cs["locFp"]), int(cs["locLr"]), methods.get(int(cs["stepMethod"]), int(cs["stepMethod"])), int(cs["dwarfStepResult"])))
        else:
            print("%2d %-14s res=%d ip=%#x fp=%#x sp=%#x" % (k, names.get(t,t), int(e["result"]), int(e["ip"]), int(e["fp"]), int(e["sp"])))
end
G
timeout 280 gdb -q -batch -x /tmp/r.gdb /usr/share/dotnet/dotnet 2>&1 | grep -v -E '^\[New LWP|warning:' | sed -n '/signal handler called/,$p' | sed -E 's/\(this=[^)]*\)//' | cut -c1-200 | head -70
