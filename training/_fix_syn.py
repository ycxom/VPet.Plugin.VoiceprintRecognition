from pathlib import Path
import re

p = Path("training/scripts/generate_mimo_tts.py")
t = p.read_text(encoding="utf-8")

# Fix broken replace strings: .replace("\", "/") -> .replace("\\", "/")
t = t.replace('.replace("\\", "/")', '.replace("\\\\", "/")')  # may double
# simpler: fix the broken pattern explicitly
t = t.replace('.replace("\\", "/")', 'REPLACemarker')
# broken form from syntax error is: .replace("\", "/")
t = t.replace('.replace("\\", "/")', '.replace("\\\\", "/")')

# Read raw lines around 556
lines = t.splitlines()
for i,l in enumerate(lines):
    if 'relative_to(root)).replace' in l:
        print(i+1, repr(l))
