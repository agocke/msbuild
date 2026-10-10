#!/usr/bin/env python3
"""Operation-kind breakdown of the samples under a root frame (optionally excluding a nested frame).

usage: perf script ... | kinds_under.py <root-regex> [exclude-regex]
Kinds are taken from classify.py, preceded by MSBuild-specific buckets checked over the frames
between the root and the leaf (innermost first)."""
import re, sys
from collections import defaultdict
sys.path.insert(0, __import__("os").path.dirname(__file__))
import classify

root = re.compile(sys.argv[1])
exclude = re.compile(sys.argv[2]) if len(sys.argv) > 2 and sys.argv[2] else None

# Checked against every frame below the root, innermost first; first match wins.
PATHS = [
    ("property lookup", re.compile(r"PropertyExpander__LookupProperty|PropertyDictionary_1__GetProperty|RetrievableEntryHashSet|PropertyTrackingEvaluatorDataWrapper_4__GetProperty|MSBuildNameIgnoreCaseComparer|IPropertyProvider")),
    ("path adjust / fs", re.compile(r"MaybeAdjustFilePath|LooksLikeUnixFilePath|FileUtilities__|PathInternal|FileSystem|SystemNative")),
    ("property-function call", re.compile(r"Expander_2_Function|FunctionCallExpressionNode|WellKnownFunctions|IntrinsicFunctions")),
    ("number parse", re.compile(r"Number__TryParse|ConversionUtilities|ValidDecimalNumber|TryConvertDecimal|IsHex")),
    ("expand: scan/split", re.compile(r"FindClosingParenthesis|ExpressionShredder|IndexOf|ScanForClosing|SinkValidName")),
    ("expand: build string", re.compile(r"SpanBasedStringBuilder|StringBuilder|ExpandIntoStringLeaveEscaped|ExpandPropertiesLeave|EscapingUtilities|String__Concat|Memmove")),
    ("parse-cache lookup", re.compile(r"ConcurrentDictionary_2__TryGetValue|GetNonRandomizedHashCode|Parser__Parse|ConditionParser")),
    ("lock/pool", re.compile(r"Monitor__|ReaderWriterLockSlim|Stack_1__Pop|Stack_1__Push|ObjectPool|Lock__")),
    ("conditioned-property tracking", re.compile(r"UpdateConditionedProperties|ConditionedProperties")),
    ("reflection", re.compile(r"System_Reflection|NativeFormat|TypeInfo")),
]
totals = defaultdict(int); grand = 0
header = re.compile(r"^(\S.*?)\s+(\d+)\s+(\d+)\s*$")
period = 0; frames = []
def flush():
    global grand
    if not frames: return
    idx = None
    for i, f in enumerate(frames):
        if root.search(f): idx = i
    if idx is None: return
    below = frames[:idx]  # innermost-first, excluding root
    if exclude and any(exclude.search(f) for f in below): return
    _, kind = classify.classify("x", frames[:idx + 1])
    bucket = None
    if kind in ("gc", "alloc", "write-barrier", "cast/dispatch", "kernel"):
        bucket = kind
    else:
        # Innermost frame that matches any bucket decides (lookups inside an expansion are lookups).
        for f in below:
            bucket = next((name for name, pat in PATHS if pat.search(f)), None)
            if bucket is not None:
                break
    totals[bucket or ("node logic: " + kind)] += period
    grand += period
for line in sys.stdin:
    if not line.strip():
        flush(); frames = []; continue
    if not line[0].isspace():
        flush(); frames = []
        m = header.match(line.rstrip("\n")); period = int(m.group(3)) if m else 0
        continue
    parts = line.strip().split(" ", 1); frames.append(parts[1] if len(parts) > 1 else parts[0])
flush()
print(f"# total {grand/1e9:.2f}G")
for k, v in sorted(totals.items(), key=lambda x: -x[1]):
    print(f"{v/grand*100:5.1f}%  {v/1e9:6.2f}G  {k}")
