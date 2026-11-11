"""Offline, standard-library-only QuickClip acceptance ledger reporter.
Usage: python progress.py [TRACKER.json]
Update a gate status to 'pass' only after providing explicit evidence in TRACKER.json.
"""
import json, pathlib, sys
file=pathlib.Path(sys.argv[1]) if len(sys.argv)>1 else pathlib.Path(__file__).with_name('TRACKER.json')
data=json.loads(file.read_text(encoding='utf-8'))
valid={'pass','pending','reported','failed'}
print('QuickClip quality gates —',data['baseline_sha'][:12])
print('Gate completion is evidence-based; pending/reported do NOT count as passed.\n')
print(f"{'Feature':<9} {'Tier':<4} {'Code':>7} {'Accept':>7}  {'Status':<8} Name")
print('-'*107)
by_tier={}
for f in data['features']:
    if f['lifecycle']=='retired':continue
    groups={p:[g for g in f['gates'] if g['phase']==p] for p in ('implementation','acceptance')}
    if not all(g['status'] in valid for g in f['gates']):raise ValueError('Invalid gate status')
    counts={p:(sum(g['status']=='pass' for g in arr),len(arr)) for p,arr in groups.items()}
    done=all(n==d for n,d in counts.values()) and f['lifecycle']=='active'
    tier=by_tier.setdefault(f['priority'],{'pass':0,'total':0,'reported':0})
    tier['pass']+=sum(n for n,d in counts.values());tier['total']+=sum(d for n,d in counts.values())
    tier['reported']+=sum(g['status']=='reported' for g in f['gates'])
    print(f"{f['id']:<9} {f['priority']:<4} {counts['implementation'][0]:>2}/{counts['implementation'][1]:<4} {counts['acceptance'][0]:>2}/{counts['acceptance'][1]:<4}  {('DONE' if done else f['lifecycle']):<8} {f['title']}")
print('\nTier summaries:')
for k in sorted(by_tier):
 v=by_tier[k];pct=100*v['pass']/v['total'] if v['total'] else 100
 print(f"{k}: {v['pass']}/{v['total']} gates accepted ({pct:.1f}% of explicitly defined gates), {v['reported']} reports awaiting verification")
p0=[f for f in data['features'] if f['priority']=='P0' and f['lifecycle']=='active']
unpassed=[g for f in p0 for g in f['gates'] if g['status']!='pass']
print(f'\nP0 gate closure: {sum(len(f["gates"]) for f in p0)-len(unpassed)}/{sum(len(f["gates"]) for f in p0)}. Remaining: {len(unpassed)}.')
print('Next P0 gates:')
for g in unpassed[:12]:print(' -',g['id'],g['description'])
