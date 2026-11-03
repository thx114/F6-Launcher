"""Publish the fork's real, archive-verified metadata; no upstream metadata fallback."""
from pathlib import Path
import argparse, hashlib, json, datetime, re

root=Path(__file__).resolve().parents[1]
p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--updater',required=True,type=Path)
p.add_argument('--repository',default='thx114/F6-Launcher')
p.add_argument('--receipt-output',type=Path,default=root/'artifacts/fork-compliance-20261103/release-metadata-receipt.json')
args=p.parse_args()
if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+',args.repository):
    raise ValueError('Expected a GitHub owner/repository')
archive=args.updater.resolve()
receipt=json.loads(archive.with_suffix('.zip.verification.json').read_text(encoding='utf-8-sig'))
with archive.open('rb') as stream:
    digest=hashlib.file_digest(stream,'sha256').hexdigest()
if not receipt['contentsVerified'] or receipt['sha256'].lower()!=digest:
    raise ValueError('Release archive has not been verified')
version=receipt['version']
url=f'https://github.com/{args.repository}/releases/download/v{version}/{archive.name}'
now=datetime.datetime.now(datetime.timezone.utc).isoformat()
detail={'version':version,'architecture':'X64','install_type':'Portable','build_time':now,
        'disable_auto_update':False,'package_url':url,'package_size':archive.stat().st_size,
        'package_hash':digest,'manifest_url':'','diffs':{}}
metadata=root/'metadata'
def save(path,data):
    path.parent.mkdir(parents=True,exist_ok=True)
    path.write_text(json.dumps(data,ensure_ascii=False,indent=2)+'\n',encoding='utf-8',newline='\n')
info={'version':version,'releases':{'x64-portable':detail}}
for channel in ('stable','preview'):
    save(metadata/f'release_info_{channel}.json',info)
save(metadata/'history'/f'release_info_{version}.json',info)
core={'Version':version,'Architecture':'X64','BuildTime':now,'DisableAutoUpdate':False,
      'Portable':url,'PortableSize':archive.stat().st_size,'PortableHash':digest,
      'SeparateFiles':None}
for api in ('v1','dev'):
    for channel in ('stable','preview'):
        for kind in ('version','release'):
            save(metadata/api/f'{kind}_{channel}_x64.json',core)
save(args.receipt_output,{'version':version,'sha256':digest,'url':url})
print('Prepared x64 fork metadata for',version)
