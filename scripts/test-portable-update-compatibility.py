"""Install a real release ZIP with both the legacy-name-only updater and current sources.

Uses disposable portable directories and a console fixture; no launcher UI or game is started.
The legacy sources come from an immutable Git commit so changing today's validator cannot
accidentally make the backwards-compatibility test pass.
"""
from pathlib import Path
import argparse, hashlib, json, os, shutil, stat, subprocess, tempfile

ROOT = Path(__file__).resolve().parents[1]
LEGACY_COMMIT = '66345e2a9c5c63a807926993e0dfc61683f7a408'


def safe_remove(path, allowed):
    path = path.absolute()
    assert path.parent == allowed.absolute() and path.name.startswith('f6-update-fixture-')
    for entry in [path, *path.rglob('*')]:
        info = entry.lstat()
        if info.st_file_attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT:
            raise RuntimeError('Refusing to remove a reparse point: ' + str(entry))
        if entry.is_file() and info.st_nlink != 1:
            raise RuntimeError('Refusing to remove a hardlink: ' + str(entry))
    shutil.rmtree(path)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--archive', type=Path, required=True)
    parser.add_argument('--original-archive', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--dotnet', type=Path, default=Path.home()/'.dotnet10/dotnet.exe')
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    original = args.original_archive.resolve() if args.original_archive else None
    archive = args.archive.resolve()
    source = output/'legacy-source'
    files = ['Features/Update/GithubUpdateService.cs', 'Helpers/PortableUpdatePolicy.cs', 'Helpers/LauncherCommandLine.cs']
    snapshots = {}
    for name in files:
        content = subprocess.check_output(['git', 'show', LEGACY_COMMIT+':src/HoYoShadeHub/'+name], cwd=ROOT)
        path = source/name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)
        snapshots[name] = hashlib.sha256(content).hexdigest()
    assert b'version.ini must point to app-*/HoYoShadeHub.exe.' in (source/'Helpers/PortableUpdatePolicy.cs').read_bytes()
    results = []
    env = os.environ.copy()
    env['DOTNET_ROOT'] = str(args.dotnet.resolve().parent)

    def command(arguments, log):
        with log.open('w', encoding='utf-8') as stream:
            result = subprocess.run(arguments, cwd=ROOT, env=env, stdout=stream, stderr=subprocess.STDOUT, timeout=600)
        if result.returncode:
            raise RuntimeError(log.read_text('utf-8', errors='replace')[-3500:])

    for kind in ('legacy', 'current'):
        # The fixture's parent is deliberately disposable: the real updater installs there.
        fixture = Path(tempfile.mkdtemp(prefix='f6-update-fixture-'))
        app = fixture/'app-fixture'
        try:
            args_build = [str(args.dotnet), 'publish', 'src/HoYoShadeHub.Update.Tests', '-c', 'Release', '-o', str(app), '-m:1', '-nodeReuse:false', '--nologo']
            if kind == 'legacy': args_build.append('-p:UpdaterSourceRoot='+str(source))
            command(args_build, output/(kind+'-build.log'))
            if kind == 'legacy' and original:
                command([str(app/'HoYoShadeHub.Update.Tests.exe'), str(original), '--expect-legacy-rejection'], output/'original-legacy-rejection.log')
                results.append({'updater': kind, 'archive': str(original), 'expectedRejectionVerified': True})
            command([str(app/'HoYoShadeHub.Update.Tests.exe'), str(archive)], output/(kind+'-install.log'))
            tail = (output/(kind+'-install.log')).read_text('utf-8').strip().splitlines()[-1]
            results.append({'updater': kind, 'archive': str(archive), 'installedContentsVerified': True, 'result': tail})
            print(tail, flush=True)
        finally:
            safe_remove(fixture, Path(tempfile.gettempdir()))
    receipt = {'legacyCommit': LEGACY_COMMIT, 'legacySourceSha256': snapshots, 'archiveSha256': hashlib.file_digest(archive.open('rb'), 'sha256').hexdigest(), 'results': results, 'uiTested': False, 'gameTested': False}
    (output/'compatibility-verification.json').write_text(json.dumps(receipt, indent=2)+'\n', encoding='utf-8')


if __name__ == '__main__':
    main()
