import json
d = json.load(open('MaichessMatchManagerService.Tests/cov.json'))
any_miss = False
for mod, files in d.items():
    for path, classes in files.items():
        base = path.replace(chr(92), '/').split('/')[-1]
        lc = lt = bc = bt = 0
        for cls, methods in classes.items():
            for m, info in methods.items():
                for ln, h in info.get('Lines', {}).items():
                    lt += 1
                    lc += 1 if h > 0 else 0
                for br in info.get('Branches', []):
                    bt += 1
                    bc += 1 if br.get('Hits', 0) > 0 else 0
        if lc != lt or bc != bt:
            any_miss = True
            print('INCOMPLETE', base, 'lines %d/%d branch %d/%d' % (lc, lt, bc, bt))
print('ALL FULL' if not any_miss else 'SOME INCOMPLETE')
