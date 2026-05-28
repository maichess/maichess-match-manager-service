import re, json, os, sys

latest = sorted(d for d in os.listdir('.') if os.path.isdir(d))[-1]
with open(os.path.join(latest, 'reports', 'mutation-report.html'), 'r', encoding='utf-8') as f:
    html = f.read()
m = re.search(r'app\.report\s*=\s*(\{.*?\});\s*\n', html, re.DOTALL)
data = json.loads(m.group(1))
total = 0
for path, info in data.get('files', {}).items():
    short = os.path.basename(path) if '\\' in path else path
    survived = [mt for mt in info.get('mutants', []) if mt['status'] in ('Survived', 'Timeout', 'NoCoverage')]
    if survived:
        total += len(survived)
        print(f"--- {short} ({len(survived)}) ---")
        for mt in survived:
            loc = mt.get('location', {}).get('start', {})
            print(f"  [{mt['status']}] L{loc.get('line')}:{loc.get('column')} [{mt['mutatorName']}] -> {mt.get('replacement', '')[:120]}")
print(f"\nTotal: {total}")
