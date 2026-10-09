"""Print the sts2 process memory (MiB) from an xctrace sysmon-process table on stdin, or NA."""

import re
import sys

x = sys.stdin.read()
rows = [r for r in re.findall(r"<row>.*?</row>", x, re.DOTALL) if "sts2 (" in r]
mems = re.findall(r'fmt="([\d.]+) [KMG]iB"', rows[-1]) if rows else []
print(mems[2] if len(mems) > 2 else "NA")
