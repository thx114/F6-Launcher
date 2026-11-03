"""Static release gate for license preservation and independent network/branding defaults."""
from pathlib import Path
import re, sys, xml.etree.ElementTree as ET
root=Path(__file__).resolve().parents[1]
issues=[]
for filename,copyright in [('LICENSE','Copyright (c) 2025 哆啦D夢|DuolaD'),('Starward_LICENSE','Copyright (c) 2023 Scighost')]:
    if copyright not in (root/filename).read_text(encoding='utf-8-sig'):
        issues.append('Missing original copyright: '+filename)
blocked=re.compile(r'https?://[^\s"<>]*?(?:storage\.hub\.hoyosha\.de|hoyoshadehub-glasses\.pages\.dev)',re.I)
for directory in ('src','catalog'):
    for p in (root/directory).rglob('*'):
        if not p.is_file() or any(part in ('bin','obj') for part in p.parts): continue
        if p.suffix not in ('.cs','.xaml','.resx','.csproj','.props','.targets','.json'): continue
        raw=p.read_bytes()
        try: text=raw.decode('utf-16' if raw.startswith((b'\xff\xfe',b'\xfe\xff')) else 'utf-8-sig')
        except UnicodeDecodeError: continue
        if blocked.search(text): issues.append('Upstream service endpoint: '+str(p.relative_to(root)))
        if p.suffix in ('.xaml','.csproj','.props','.targets','.resx'):
            try: ET.fromstring(text)
            except ET.ParseError as error: issues.append(str(p.relative_to(root))+': '+str(error))
for relative in ('metadata.ps1','publish.ps1','build.ps1','package.ps1','package-full.ps1'):
    if blocked.search((root/relative).read_text(encoding='utf-8-sig')): issues.append('Upstream service in '+relative)
for relative in ('package.ps1','package-full.ps1'):
    text=(root/relative).read_text(encoding='utf-8-sig')
    if 'Copy-DistributionLicenses' not in text or 'Assert-ForkDistribution' not in text: issues.append('Missing distribution gate: '+relative)
about=(root/'src/HoYoShadeHub/Features/Setting/AboutSetting.xaml').read_text(encoding='utf-8-sig')
if '非官方分支' not in about or '哆啦D夢|DuolaD' not in about or 'HoYoShade Hub' not in about: issues.append('Missing visible fork attribution')
if issues:
    print('\n'.join(issues),file=sys.stderr)
    raise SystemExit(1)
print('PASS: original license copyrights, XML syntax, no upstream private endpoints, visible attribution, package gates.')
