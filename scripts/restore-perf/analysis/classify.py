#!/usr/bin/env python3
"""Partition perf instruction samples into subsystem x operation-kind buckets.

usage: perf script -i perf.data -F comm,tid,period,ip,sym,dso | classify.py

Subsystem comes from the thread (GC/JIT threads) or the innermost recognizable stack frame; operation kind comes from the leaf frames (innermost recognizable frame).
"""
import re
import sys
from collections import defaultdict

# Subsystem rules, checked against the whole stack (innermost first). First match wins.
SUBSYSTEMS = [
    ("eval:globbing", re.compile(r"FileMatcher::|EngineFileUtilities::GetFileList")),
    ("eval:conditions", re.compile(r"ConditionEvaluator::|ExpressionNode::|MultipleComparisonNode|OperatorExpressionNode")),
    ("eval:items", re.compile(r"LazyItemEvaluator|EvaluateItemGroupElement|EvaluateItemElement|ItemSpec`|EngineFileUtilities")),
    ("eval:imports", re.compile(r"EvaluateImportElement|ExpandAndLoadImports|ProjectRootElementCache|ProjectRootElement::|SdkResolver")),
    ("eval:properties", re.compile(r"EvaluatePropertyGroupElement|EvaluatePropertyElement|TryEvaluatePropertyElement")),
    ("eval:usingtasks", re.compile(r"TaskRegistry::")),
    ("eval:targets", re.compile(r"ReadTargetElement|AddBeforeAndAfterTargetMappings|ProjectTargetInstance")),
    ("eval:other", re.compile(r"Evaluator`4")),
    # Methods declared in Microsoft.Build.Graph, not frames that only mention a Graph type as a generic argument.
    ("graph", re.compile(r"\] Microsoft\.Build\.Graph\.|^Microsoft_Build_Microsoft_Build_Graph_")),
    ("binlog", re.compile(r"BinaryLogger::|BuildEventArgsWriter::")),
    ("logging", re.compile(r"LoggingService::|EventSourceSink::|ProjectLoggingContext|LoggingContext::")),
    ("execution:tasks", re.compile(r"TaskBuilder::|TaskExecutionHost|TaskHost::|CompiledActionGraph")),
    ("execution:targets", re.compile(r"TargetEntry::|TargetBuilder::|TargetUpToDateChecker|IntrinsicTask")),
    ("execution:request", re.compile(r"RequestBuilder::|BuildRequestEngine|Scheduler::|BuildManager::")),
    ("nuget", re.compile(r"\[NuGet\.|^NuGet_[A-Za-z_]*?NuGet_")),
]

# Operation kinds, checked against the innermost frames first. First match wins.
KINDS = [
    ("gc", re.compile(r"SVR::|WKS::|GCHeap|gc_heap|GcInfoDecoder|GcSlotDecoder|libunwind|StackFrameIterator|RhpWaitForGC|RhpGcPoll|SuspendEE|RestartEE")),
    ("alloc", re.compile(r"RhpNew|RhNew|RhpAlloc|Alloc\(|AllocateObject|JIT_New|Allocator|AllocateString|FastAllocate")),
    ("write-barrier", re.compile(r"WriteBarrier|BulkMoveWithWriteBarrier|AssignRef|RhBulkMoveWithWriteBarrier")),
    ("cast/dispatch", re.compile(r"InterfaceDispatch|TypeCast|RhTypeCast|CastHelpers|VSD_|ResolveWorker|JIT_IsInstance|IsInstanceOf|ChkCast|StubDispatch|DispatchStub")),
    ("hash/lookup", re.compile(r"Dictionary`2|HashSet`1|RetrievableEntryHashSet|Comparer.*GetHashCode|GetHashCode|ComputeHash|Marvin|FowlerNoll|FindValue|TryGetValue|ImmutableDictionary|ImmutableSortedDictionary|PropertyDictionary|ItemDictionary|InternableString|Strings::WeakIntern|WeakStringCache|ConcurrentDictionary")),
    ("compare", re.compile(r"Comparer.*Equals|::Equals\(|OrdinalIgnoreCase|SequenceCompareTo|SequenceEqual|::Compare\(|CompareTo|StartsWith|EndsWith")),
    ("string-scan", re.compile(r"IndexOf|SpanHelpers|PackedSpanHelpers|FindClosing|Tokenizer|ScanFor|Scanner::|::Contains\(|LastIndexOf|IsValid.*Character")),
    ("string-build", re.compile(r"StringBuilder|ValueStringBuilder|String::Concat|String::Substring|String::Create|SpanBasedStringBuilder|Memmove|memmove|memcpy|memset|String::Join|String::Replace|EscapingUtilities")),
    ("path/fs", re.compile(r"FileUtilities|PathInternal|Path::|FileSystem|File::|Directory::|NativeMethods|SystemNative|FileStatus|stat|statx|link_path_walk")),
    ("regex", re.compile(r"RegularExpressions")),
    ("xml", re.compile(r"System\.Xml|XmlUtilities|XmlDocument|XmlReader|XmlTextReader")),
    ("linq", re.compile(r"System\.Linq")),
    ("locks/threads", re.compile(r"Monitor::|ReaderWriterLock|SpinWait|ThreadNative_SpinWait|Interlocked|SemaphoreSlim|osq_lock|futex|t_join|Lock::")),
    ("jit/typeload", re.compile(r"libclrjit|MethodTable|MethodDesc|ClassLoader|TypeHandle|SigPointer|EEClass|JIT_|Prestub|CallCounting|ThePreStub")),
    ("kernel", re.compile(r"\[kernel")),
]

THREAD_SUBSYSTEMS = {
    ".NET Server GC": "runtime:gc-threads",
    ".NET BGC": "runtime:gc-threads",
    ".NET Tiered JIT": "runtime:jit-thread",
}

KIND_DEPTH = 3  # leaf frames consulted for the operation kind


def _both_symbol_styles(rules):
    """Native AOT flattens 'Namespace.Type`1::Method' to 'Assembly_Namespace_Type_1__Method'; accept both."""
    out = []
    for name, pattern in rules:
        src = pattern.pattern.replace(r"\.", r"[._]").replace("`", "[`_]").replace("::", "(?:::|__)")
        out.append((name, re.compile(src)))
    return out


SUBSYSTEMS = _both_symbol_styles(SUBSYSTEMS)
KINDS = _both_symbol_styles(KINDS)


def classify(comm, frames):
    subsystem = THREAD_SUBSYSTEMS.get(comm)
    if subsystem is None:
        # Innermost frame that matches any rule decides, so nested work (a property inside an import,
        # a condition inside an item operation) is charged to the most specific construct.
        subsystem = "other"
        for f in frames:
            match = next((name for name, pattern in SUBSYSTEMS if pattern.search(f)), None)
            if match is not None:
                subsystem = match
                break
    kind = "other"
    for f in frames[:KIND_DEPTH]:
        for name, pattern in KINDS:
            if pattern.search(f):
                kind = name
                break
        if kind != "other":
            break
    return subsystem, kind


def main():
    totals = defaultdict(int)
    comm = None
    period = 0
    frames = []
    header = re.compile(r"^(\S.*?)\s+(\d+)\s+(\d+)\s*$")

    def flush():
        if comm is not None:
            totals[classify(comm, frames)] += period

    for line in sys.stdin:
        if not line.strip():
            flush()
            comm, period, frames = None, 0, []
            continue
        if not line[0].isspace():
            flush()
            m = header.match(line.rstrip("\n"))
            if m:
                comm, period, frames = m.group(1).strip(), int(m.group(3)), []
            else:
                comm, period, frames = None, 0, []
            continue
        parts = line.strip().split(" ", 1)
        frames.append(parts[1] if len(parts) > 1 else parts[0])
    flush()

    grand = sum(totals.values())
    subsystems = sorted({s for s, _ in totals}, key=lambda s: -sum(v for (t, _), v in totals.items() if t == s))
    kinds = sorted({k for _, k in totals}, key=lambda k: -sum(v for (_, t), v in totals.items() if t == k))
    print("subsystem\ttotal_G\tshare\t" + "\t".join(kinds))
    for s in subsystems:
        row = [totals.get((s, k), 0) for k in kinds]
        total = sum(row)
        print(f"{s}\t{total / 1e9:.1f}\t{total / grand * 100:.1f}%\t" + "\t".join(f"{v / total * 100:.0f}%" if total else "" for v in row))
    col = [sum(totals.get((s, k), 0) for s in subsystems) for k in kinds]
    print(f"ALL\t{grand / 1e9:.1f}\t100%\t" + "\t".join(f"{v / grand * 100:.1f}%" for v in col))


if __name__ == "__main__":
    main()
