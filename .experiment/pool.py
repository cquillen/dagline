import re,sys,glob,os,statistics as st
S=sys.argv[1]
data={}
for label in ['A1','A2','A3','A4','A5','A6','A7','B1','B2','B3','B4','B5','B6','B7']:
    p=os.path.join(S,'bdn',label+'.log')
    if not os.path.exists(p): continue
    cur=None
    for line in open(p,encoding='utf-8',errors='replace'):
        m=re.match(r'// Benchmark: RunBenchmarks\.(\w+):',line)
        if m: cur=m.group(1)
        m=re.match(r'WorkloadResult\s+\d+: (\d+) op, ([\d.]+) ns',line)
        if m and cur:
            data.setdefault((cur,label[0]),{}).setdefault(label,[]).append(float(m.group(2))/int(m.group(1))/1e6)
for (name,build),runs in sorted(data.items()):
    allv=[v for r in runs.values() for v in r]
    q=st.quantiles(allv,n=4)
    print(f"{name:20s} {'baseline' if build=='A' else 'variant '} runs={len(runs)} n={len(allv)} median={st.median(allv):8.2f} q1={q[0]:8.2f} q3={q[2]:8.2f} min={min(allv):8.2f} max={max(allv):8.2f} mean={st.mean(allv):8.2f}")
    if name=='BeginAndEndWaiting':
        print("    iterations under 90 ms:",sum(v<90 for v in allv),"of",len(allv),"; per run:",{l:sum(v<90 for v in r) for l,r in runs.items()})
