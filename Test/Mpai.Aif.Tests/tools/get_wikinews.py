import json,sys,urllib.request,urllib.parse,re
UA={'User-Agent':'MPAI-test/1.0 (leonardo@chiariglione.org)'}
def api(l,**p):
    p['format']='json'
    u=f'https://{l}.wikinews.org/w/api.php?'+urllib.parse.urlencode(p)
    return json.load(urllib.request.urlopen(urllib.request.Request(u,headers=UA),timeout=40))
out={}
for l in sys.argv[1:]:
    got=[]
    for _ in range(25):
        d=api(l,action='query',generator='random',grnnamespace=0,grnlimit=20,prop='extracts|info',explaintext=1,exlimit=20,exintro=0,inprop='url')
        for pid,pg in d.get('query',{}).get('pages',{}).items():
            t=pg.get('extract','') or ''
            n=len(t)
            if 1800<=n<=4200 and 'Wikinews' not in t[:40] and '\n\n\n' not in t[:2000]:
                got.append({'title':pg['title'],'url':pg['fullurl'],'text':t,'pageid':pg['pageid'],'lastrevid':pg.get('lastrevid')})
        if len(got)>=8: break
    out[l]=got; print(l,len(got),[g['title'][:40] for g in got[:4]])
json.dump(out,open('wn.json','w',encoding='utf-8'),ensure_ascii=False,indent=1)
