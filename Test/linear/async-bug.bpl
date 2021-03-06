// RUN: %boogie -noinfer -typeEncoding:m -useArrayTheory -doModSetAnalysis "%s" > "%t"
// RUN: %diff "%s.expect" "%t"
const GcTid:int;

procedure {:yields} {:phase 100} Initialize({:linear "tid"} tid:int)
requires{:phase 100} tid == GcTid;
{
    yield;
    assert{:phase 100} tid == GcTid;

    call GarbageCollect(tid);

    yield;
    assert{:phase 100} tid == GcTid;

    async call GarbageCollect(tid);

    yield;
    assert{:phase 100} tid == GcTid;

    async call GarbageCollect(tid);

    yield;
    assert{:phase 100} tid == GcTid;

    yield;
    assert{:phase 100} tid == GcTid;
}

procedure {:yields} {:phase 100} GarbageCollect({:linear "tid"} tid:int)
requires{:phase 100} tid == GcTid;
{
    yield;
    assert{:phase 100} tid == GcTid;
}

