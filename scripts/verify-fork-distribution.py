"""Offline distribution audit: exact license bytes, independent artwork and prohibited active URLs."""
from pathlib import Path
import argparse, hashlib, json, re, zipfile
from distribution_binary_strings import managed_user_strings
root=Path(__file__).resolve().parents[1]
p=argparse.ArgumentParser(description=__doc__)
p.add_argument('archives',nargs='+',type=Path)
p.add_argument('--output',type=Path,default=root/'artifacts/fork-compliance-20261103/distribution-audit.json')
args=p.parse_args()
copyrights={'LICENSE':'Copyright (c) 2025 哆啦D夢|DuolaD','Starward_LICENSE':'Copyright (c) 2023 Scighost'}
for name,expected in copyrights.items():
    if expected not in (root/name).read_text(encoding='utf-8-sig'):
        raise ValueError('Original copyright missing: '+name)
blocked=re.compile(r'https?://[^\s\x00"<>]*?(?:storage\.hub\.hoyosha\.de|hoyoshadehub-glasses\.pages\.dev)',re.I)
results=[]
for path in args.archives:
    with zipfile.ZipFile(path) as z:
        names=set(z.namelist())
        app=[n for n in names if re.fullmatch(r'app-[^/]+/F6Launcher.exe',n)]
        if len(app)!=1: raise ValueError('Expected exactly one application version')
        prefix=app[0].rsplit('/',1)[0]+'/'
        # Packages must pass the validator already running in older installations.
        if z.read('version.ini').decode('utf-8-sig').strip() != 'exe_path='+prefix.rstrip('/')+'\\HoYoShadeHub.exe':
            raise ValueError('version.ini is incompatible with legacy-name-only installed updaters')
        if z.read(prefix+'HoYoShadeHub.exe') != z.read(prefix+'F6Launcher.exe'):
            raise ValueError('Internal compatibility entry differs from the F6 application host')
        for location in ('',prefix):
            for name in ('LICENSE','Starward_LICENSE','NOTICE.md'):
                if z.read(location+name)!=(root/name).read_bytes():
                    raise ValueError('License bytes differ: '+location+name)
        for name in ('f6-logo.ico','f6-logo.png'):
            if z.read(prefix+'Assets/'+name)!=(root/'src/HoYoShadeHub/Assets'/name).read_bytes():
                raise ValueError('F6 artwork mismatch')
        if prefix+'Assets/logo.png' in names or prefix+'Assets/logo.ico' in names:
            raise ValueError('Upstream product artwork included')
        root_executables={n for n in names if '/' not in n and n.lower().endswith('.exe')}
        if root_executables != {'F6Launcher.exe'}:
            raise ValueError('Expected only F6Launcher.exe at archive root: '+repr(root_executables))
        scanned=0
        for name in names:
            if not name.startswith(prefix) or not name.endswith(('.dll','.exe','.json','.xaml','.pri')): continue
            data=z.read(name)
            literals = managed_user_strings(data) if name.endswith(('.dll','.exe')) else None
            if literals is None:
                # Preserve terminators. Removing all NUL bytes merges unrelated literals.
                literals = [data.decode('utf-8',errors='replace')]
                literals += [data[offset:].decode('utf-16le',errors='replace') for offset in (0,1)]
            for literal in literals:
                if blocked.search(literal):
                    raise ValueError('Upstream service URL embedded: '+name)
            scanned+=1
        if 'HoYoShade/ReShade64.dll' in names:
            for name in ('HoYoShade/LICENSE','HoYoShade/ReShade_LICENSE'):
                if not z.read(name): raise ValueError('Framework license missing')
        results.append({'archive':str(path.resolve()),'bytes':path.stat().st_size,
                        'sha256':hashlib.file_digest(path.open('rb'),'sha256').hexdigest(),
                        'entries':len(names),'networkFilesScanned':scanned,'licenseBytesVerified':True,
                        'independentArtworkVerified':True,'runtimeTested':False})
output=args.output
output.parent.mkdir(parents=True,exist_ok=True)
output.write_text(json.dumps(results,ensure_ascii=False,indent=2),encoding='utf-8',newline='\n')
print(json.dumps(results,ensure_ascii=False,indent=2))
