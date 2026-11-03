"""Package frozen F5 and three explicit offline NR selections, without modifying the installation."""
from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import stat
import zipfile

ROOT = Path(__file__).resolve().parents[1]
PREFIX = 'OptiScaler/official/DLSS F5/'
MAIN = '5986bc7377a34a5451ed14dc0ce481a20f30f8808b966adc47583cf845986bb3'
NR = {
    'genshin': ('原神v4_预设A_1080p5000', '094d2b0e28eeb65a4250f99eee8230b1bc9befcb459331e569ddb76b26bcc6cb'),
    'sf': ('310.9.16-SF', 'cdfa6c1ec784867182164c7836f324f087b5c9f374d0c9fe46f54999feabf3ed'),
}
GAMES = {
    'hk4e': ('原神', 'profiles/hk4e_cn.ini', 'genshin'),
    'hkrpg': ('崩铁', 'profiles/hkrpg_bilibili.ini', 'sf'),
    'nap': ('绝区零', 'profiles/nap_cn.ini', 'sf'),
}
ROOT_DLLS = {
    'OptiScaler.dll', 'NextGenNRSDK.1.dll', 'nvngx.dll_dlssnr.dll', 'nvngx_dlss.dll',
    'D3D12Core.dll', 'amd_fidelityfx_framegeneration_dx12.dll', 'libxess_fg.dll',
}
DIRS = ('OptiScaler', 'plugins', 'licenses')
SPEC = importlib.util.spec_from_file_location('f5_portable_ini', Path(__file__).with_name('package-f5-v3.py'))
PORTABLE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PORTABLE)


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def encode(value):
    return (json.dumps(value, ensure_ascii=False, indent=2) + '\n').encode('utf-8')


def check_path(path):
    for node in (path, *path.parents):
        if node.is_symlink() or getattr(node.stat(follow_symlinks=False), 'st_file_attributes', 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
            raise ValueError('Reparse point is not a packaging source: ' + str(node))


def excluded(name):
    name = name.casefold()
    return (name.startswith('.') or any(x in name for x in ('backup', '.before', 'profile-recovery'))
            or name in {'genshincapture', 'current-output-captures', 'profiles', 'redist'}
            or name.endswith(('.pending', '.bak', '.log', '.dmp', '.bin', '.zip')))


def selected(source):
    files = []
    for name in sorted(ROOT_DLLS | {'LICENSE'}):
        path = source / name
        if not path.is_file():
            raise FileNotFoundError(path)
        files.append(path)
    for directory in DIRS:
        parent = source / directory
        if not parent.exists():
            continue
        check_path(parent)
        for current, directories, names in os.walk(parent, followlinks=False):
            directories[:] = sorted(n for n in directories if not excluded(n))
            for name in directories:
                check_path(Path(current) / name)
            for name in sorted(names):
                path = Path(current) / name
                if not excluded(name) and (path.suffix.casefold() in {'.dll', '.asi', '.cfg', '.ini', '.md', '.txt'} or name == 'LICENSE'):
                    # Explicit NR selection comes from the package's validated library.
                    if name.casefold() != 'nvngx_dlssnr.dll':
                        files.append(path)
    for path in files:
        check_path(path)
    return sorted(files, key=lambda p: p.relative_to(source).as_posix().casefold())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--genshin-nr', type=Path, required=True, help='Published original v4 ZIP')
    parser.add_argument('--sf-nr', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    source, output = args.source.absolute(), args.output.absolute()
    if source.name != 'DLSS F5' or output.parent.resolve() != (ROOT / 'build/release').resolve() or output.suffix != '.zip':
        raise ValueError('Expected frozen DLSS F5 source and build/release ZIP output')
    partial = output.with_name(output.name + '.partial')
    if output.exists() or partial.exists():
        raise FileExistsError(output)
    for path in (source, args.genshin_nr.absolute(), args.sf_nr.absolute()):
        check_path(path)
    main_stat = (source / 'OptiScaler.dll').stat()
    if main_stat.st_size != 33359360 or digest(source / 'OptiScaler.dll') != MAIN:
        raise ValueError('Frozen main DLL identity changed')
    if digest(args.sf_nr) != NR['sf'][1]:
        raise ValueError('SF identity does not match launcher fingerprint')
    if digest(args.genshin_nr) != '8490938386b42d860b7ed99577f311a861fdfadba205d502688e4ccba1b0f5b5':
        raise ValueError('Published Genshin v4 ZIP identity changed')
    runtime = selected(source)
    pins = {p.relative_to(source).as_posix(): {'size': p.stat().st_size, 'sha256': digest(p)} for p in runtime}
    profile_pins = {profile: digest(source / profile) for _, profile, _ in GAMES.values()}
    generated = {}
    for game, (title, profile, nr_key) in GAMES.items():
        name = f'F5 Final 20261103 {title}'
        config = f'F5-Final-{game}.ini'
        version, sha = NR[nr_key]
        generated[f'GamePack/{game}/{config}'] = PORTABLE.portable_ini((source / profile).read_bytes(), source)
        steps = [
            {'action': 'require_launcher_version', 'minVersion': '1.4.7.1'},
            {'action': 'set_opt_build', 'build': 'official/DLSS F5', 'once': True},
            {'action': 'import_opt_config', 'file': config, 'name': name, 'once': True},
            {'action': 'set_opt_config', 'name': name, 'if_missing': True, 'once': True},
            {'action': 'set_opt_nr', 'version': version, 'file': 'NR/nvngx_dlssnr.dll', 'sha256': sha, 'download': False, 'once': True},
        ]
        generated[f'GamePack/{game}/auto.json'] = encode({'actions': [{'name': name, 'runOnLaunch': True, 'steps': steps}]})
    generated[PREFIX + 'build.json'] = encode({
        'sourceId': 'official', 'version': 'DLSS F5', 'assetName': output.name,
        'packageVersion': 'final-20261103', 'main': pins['OptiScaler.dll'],
        'requiresLauncherCapabilities': ['set_opt_nr.file.sha256', 'set_opt_config.if_missing', 'require_launcher_version'],
        'minLauncherVersion': '1.4.7.1',
        'defaultNr': NR['sf'][0], 'gameNr': {g: NR[key][0] for g, (_, _, key) in GAMES.items()},
        'gameTestedByPackaging': False,
    })
    generated['README-F5-Final.md'] = '''# F5 三游戏覆盖包（2026-11-03 冻结版）

先将启动器升级至 1.4.7.1 或更新版本，再将本原始 ZIP 导入启动器全局插件页。
新安装使用 Full 包；游戏覆盖包直接导入，不先解压。不再使用 app-1.4.6.12 专用旧增量。
动作首先检查启动器最低版本，不足时提示更新并停止后续步骤。
在各游戏插件页确认一次性动作后，三款游戏分别选择 official/DLSS F5：
- 原神：原神v4_预设A_1080p5000。
- 崩铁与绝区零：310.9.16-SF（制作时启动器远端目录收录的最新 SF）。

NR DLL 随包提供，动作以 SHA256 校验后归档、硬链接至独立游戏 runtime，不依赖下载。
已有游戏 profile 保留，只有缺失时才初始化对应便携配置；不清质量、倍率、分辨率或方向设置。
官方目录不携带主 INI 或 profiles，避免直接导入覆盖用户设置。共享默认 NR 为 SF，原神专属版本由动作设置。
动作是 once；不携带本机执行账本。不改变全局 Opt 选择，不启停游戏或启动器。
只包含冻结 F5、现有运行依赖、许可与三份游戏初始化配置。不包含 F6、ReShade 核心、外置原神 Bridge、数据库、日志、转储或截图。
两套现有 Streamline 目录分别保留。现有 plugins 为空，不附加 MFGUnlocker ASI。
包校验和隔离导入/CLI 验证不等于游戏视觉、NR/FG、深度或性能验收。
'''.encode('utf-8')
    rows = []
    output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(args.genshin_nr) as nr_zip:
        matches = [n for n in nr_zip.namelist() if Path(n).name == 'nvngx_dlssnr.dll']
        if len(matches) != 1:
            raise ValueError('Published NR ZIP must have exactly one NR DLL')
        nr_member = matches[0]
        if nr_zip.getinfo(nr_member).file_size != 276162560:
            raise ValueError('Unexpected Genshin NR size')
        with nr_zip.open(nr_member) as stream:
            if hashlib.file_digest(stream, 'sha256').hexdigest() != NR['genshin'][1]:
                raise ValueError('Genshin v4 DLL hash mismatch')
        with zipfile.ZipFile(partial, 'x', zipfile.ZIP_DEFLATED, compresslevel=6, allowZip64=True) as archive:
            def write_stream(name, incoming, expected):
                sha, count = hashlib.sha256(), 0
                with archive.open(name, 'w', force_zip64=True) as outgoing:
                    for chunk in iter(lambda: incoming.read(1024 * 1024), b''):
                        outgoing.write(chunk)
                        sha.update(chunk)
                        count += len(chunk)
                if sha.hexdigest() != expected:
                    raise RuntimeError('Input changed while packaging: ' + name)
                rows.append({'path': name, 'size': count, 'sha256': sha.hexdigest()})

            for path in runtime:
                relative = path.relative_to(source).as_posix()
                if path.suffix.casefold() == '.ini':
                    generated[PREFIX + relative] = PORTABLE.portable_ini(path.read_bytes(), source)
                else:
                    with path.open('rb') as stream:
                        write_stream(PREFIX + relative, stream, pins[relative]['sha256'])
            for game, (_, _, key) in GAMES.items():
                stream = nr_zip.open(nr_member) if key == 'genshin' else args.sf_nr.open('rb')
                with stream:
                    write_stream(f'GamePack/{game}/NR/nvngx_dlssnr.dll', stream, NR[key][1])
            with args.sf_nr.open('rb') as stream:
                write_stream(PREFIX + 'nvngx_dlssnr.dll', stream, NR['sf'][1])
            for name, data in generated.items():
                archive.writestr(name, data)
                rows.append({'path': name, 'size': len(data), 'sha256': hashlib.sha256(data).hexdigest()})
            manifest = {
                'hysxOverlay': 1, 'name': 'F5 三游戏覆盖包', 'version': 'final-20261103-launcher-1.4.7.1',
                'note': '要求启动器至少 1.4.7.1；先更新，再导入。已有游戏配置保留。',
                'gamePacks': [{'game': g, 'path': 'GamePack/' + g} for g in GAMES],
                'targets': [{'from': 'OptiScaler', 'to': 'optiscaler'}],
                'optiscaler': {'sourceId': 'official', 'version': 'DLSS F5', 'select': False},
                'files': rows,
            }
            archive.writestr('filelist.json', encode(manifest))
    with zipfile.ZipFile(partial) as archive:
        if archive.testzip() is not None:
            raise RuntimeError('ZIP CRC failed')
        if len(archive.namelist()) != len(rows) + 1 or set(archive.namelist()) != {r['path'] for r in rows} | {'filelist.json'}:
            raise RuntimeError('ZIP membership mismatch')
        for row in rows:
            with archive.open(row['path']) as stream:
                if hashlib.file_digest(stream, 'sha256').hexdigest() != row['sha256']:
                    raise RuntimeError('ZIP entry hash mismatch: ' + row['path'])
    for rel, pin in pins.items():
        if digest(source / rel) != pin['sha256']:
            raise RuntimeError('Frozen source changed: ' + rel)
    for rel, sha in profile_pins.items():
        if digest(source / rel) != sha:
            raise RuntimeError('Source profile changed: ' + rel)
    if digest(args.sf_nr) != NR['sf'][1]:
        raise RuntimeError('SF library changed')
    os.replace(partial, output)
    report = {
        'zip': str(output), 'bytes': output.stat().st_size, 'sha256': digest(output),
        'entries': len(rows) + 1, 'source': str(source), 'sourcePins': pins,
        'profileSourceHashes': profile_pins, 'mainFrozenMtimeUtc': dt.datetime.fromtimestamp(main_stat.st_mtime, dt.timezone.utc).isoformat(),
        'gameNr': {g: {'version': NR[key][0], 'sha256': NR[key][1]} for g, (_, _, key) in GAMES.items()},
        'crcVerified': True, 'entryHashesVerified': True, 'sourceUnchanged': True,
        'gameTested': False, 'uploaded': False, 'createdUtc': dt.datetime.now(dt.timezone.utc).isoformat(),
    }
    output.with_suffix('.zip.verification.json').write_bytes(encode(report))
    output.with_suffix('.zip.sha256').write_text(report['sha256'] + '  ' + output.name + '\n', encoding='utf-8')
    print(json.dumps({k: v for k, v in report.items() if k not in ('sourcePins', 'profileSourceHashes')}, ensure_ascii=False, indent=2), flush=True)


if __name__ == '__main__':
    main()
