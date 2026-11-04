"""Derive the updater asset from a verified Full ZIP; publish the application only once."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import zipfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--full', type=Path, required=True)
    args = parser.parse_args()
    full = args.full.resolve()
    receipt = json.loads(full.with_suffix('.zip.verification.json').read_text(encoding='utf-8-sig'))
    if not receipt['contentsVerified']:
        raise ValueError('Full ZIP must pass its packaging verification first')
    with full.open('rb') as stream:
        if hashlib.file_digest(stream, 'sha256').hexdigest().upper() != receipt['sha256'].upper():
            raise ValueError('Full archive identity changed')
    version = receipt['version']
    output = full.with_name(f'F6Launcher_Portable_{version}_{receipt["architecture"]}.zip')
    partial = output.with_suffix('.zip.partial')
    if output.exists() or partial.exists():
        raise ValueError('Updater output already exists')
    rows = {}
    with zipfile.ZipFile(full) as source, zipfile.ZipFile(partial, 'x', zipfile.ZIP_DEFLATED, compresslevel=6) as target:
        for info in source.infolist():
            if info.is_dir() or not (info.filename in ('F6Launcher.exe', 'version.ini', 'LICENSE', 'Starward_LICENSE', 'NOTICE.md') or info.filename.startswith(f'app-{version}/')):
                continue
            sha = hashlib.sha256()
            with source.open(info) as incoming, target.open(info.filename, 'w', force_zip64=True) as outgoing:
                for chunk in iter(lambda: incoming.read(1024 * 1024), b''):
                    outgoing.write(chunk)
                    sha.update(chunk)
            rows[info.filename] = sha.hexdigest()
    required = {'F6Launcher.exe', 'version.ini', 'LICENSE', 'Starward_LICENSE', 'NOTICE.md', f'app-{version}/F6Launcher.exe', f'app-{version}/HoYoShadeHub.exe'}
    if not required.issubset(rows):
        raise ValueError('Missing application entry')
    with zipfile.ZipFile(partial) as target:
        # The old updater validates this name before it can install any new code.
        if target.read('version.ini').decode('utf-8-sig').strip() != f'exe_path=app-{version}\\HoYoShadeHub.exe':
            raise ValueError('Updater target is incompatible with legacy-name-only installed updaters')
        if rows[f'app-{version}/HoYoShadeHub.exe'] != rows[f'app-{version}/F6Launcher.exe']:
            raise ValueError('Compatibility entry must be the F6 application host')
        if set(target.namelist()) != set(rows):
            raise ValueError('Updater membership mismatch')
        for name, sha in rows.items():
            with target.open(name) as stream:
                if hashlib.file_digest(stream, 'sha256').hexdigest() != sha:
                    raise ValueError('Updater content mismatch: ' + name)
    os.replace(partial, output)
    with output.open('rb') as stream:
        sha = hashlib.file_digest(stream, 'sha256').hexdigest().upper()
    result = {'version': version, 'kind': 'ordinary-portable-updater', 'zip': str(output),
              'bytes': output.stat().st_size, 'sha256': sha, 'entries': len(rows), 'contentsVerified': True,
              'derivedFromFull': str(full), 'applicationIdenticalToFull': True,
              'runtimeTested': False, 'uiTested': False, 'gameTested': False}
    output.with_suffix('.zip.verification.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    output.with_suffix('.zip.sha256').write_text(sha + '  ' + output.name + '\n', encoding='utf-8')
    print(json.dumps(result, indent=2), flush=True)


if __name__ == '__main__':
    main()
