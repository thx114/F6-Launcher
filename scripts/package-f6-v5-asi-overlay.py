"""Package the selected F6 ASI and FSR Bridge installation without changing it."""
from __future__ import annotations

import argparse
import configparser
import datetime as dt
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import stat
import zipfile

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = 'OptiScaler/F6/v5/'
BRIDGE = 'Modules/genshin-fsr-bridge/'
GAMES = {
    'hk4e': ('原神', 'hk4e_cn'),
    'hkrpg': ('崩铁', 'hkrpg_bilibili'),
    'nap': ('绝区零', 'nap_cn'),
}
EXPECTED = {
    'OptiScaler.dll': 'a6d12031d8b33af050a81df75029eec7096d0ac4668d47308cc2fb6d545727d6',
    'plugins/MFGUnlocker.asi': '171ac18d4d632d12dee22d5caf2085f301bacfdb9a462561d93dbcbb61d6a4f8',
    'nvngx_dlssnr.dll': 'cdfa6c1ec784867182164c7836f324f087b5c9f374d0c9fe46f54999feabf3ed',
}
BRIDGE_SHA = 'cc301b42ec7aa31059b9fcd10b29b3915b9eb21dbe363d5f431ed9201cb904a1'
SPEC = importlib.util.spec_from_file_location('portable_opti_ini', Path(__file__).with_name('package-f5-v3.py'))
PORTABLE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PORTABLE)


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def encode(value):
    return (json.dumps(value, ensure_ascii=False, indent=2) + '\n').encode('utf-8')


def check_path(path):
    for node in (path, *path.parents):
        if node.is_symlink() or getattr(node.lstat(), 'st_file_attributes', 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
            raise ValueError('Refusing reparse point: ' + str(node))


def excluded(name):
    n = name.casefold()
    return (n in {'logs', 'log', 'ngx', 'cache', 'captures', 'current-output-captures', '__pycache__'}
            or n.startswith('.') or n.endswith(('.log', '.cache', '.bin', '.dmp', '.bak', '.pending', '.zip', '.tmp'))
            or '.before' in n or 'backup' in n or 'profile-recovery' in n)


def files_under(root):
    check_path(root)
    selected, omitted = [], []
    for current, directories, names in os.walk(root, followlinks=False):
        for n in directories:
            check_path(Path(current) / n)
        for n in list(directories):
            if excluded(n):
                omitted.append(str((Path(current) / n).relative_to(root)) + '/')
        directories[:] = sorted(n for n in directories if not excluded(n))
        for n in sorted(names):
            p = Path(current) / n
            check_path(p)
            if excluded(n):
                omitted.append(p.relative_to(root).as_posix())
            else:
                selected.append(p)
    return sorted(selected), omitted


def read_ini(data):
    c = configparser.ConfigParser(interpolation=None, strict=True)
    c.read_string(data.decode('utf-8-sig'))
    return c


def portable(data, source):
    result = PORTABLE.portable_ini(data, source)
    before, after = read_ini(data), read_ini(result)
    allowed = {('Log', 'logfilename'), ('Plugins', 'path'), ('Libraries', 'optidllpath')}
    if before.sections() != after.sections():
        raise ValueError('INI sections changed')
    for section in before.sections():
        if set(before[section]) != set(after[section]):
            raise ValueError('INI keys changed: ' + section)
        for key in before[section]:
            if (section, key) not in allowed and before[section][key] != after[section][key]:
                raise ValueError('Functional INI value changed: ' + section + '.' + key)
    if after.get('Plugins', 'LoadAsiPlugins').lower() != 'true':
        raise ValueError('ASI loading must remain enabled')
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--bridge', type=Path, required=True)
    parser.add_argument('--bridge-notices', type=Path, default=Path('D:/CODE/genshin_fsr_brigde/Dx11FsrBridge'))
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    source, bridge, output = (p.absolute() for p in (args.source, args.bridge, args.output))
    if output.parent.resolve() != (ROOT / 'build/release').resolve() or output.suffix.lower() != '.zip':
        raise ValueError('Output must be a ZIP under build/release')
    partial = output.with_name(output.name + '.partial')
    if output.exists() or partial.exists():
        raise FileExistsError('Refusing to overwrite existing artifact: ' + str(output))
    selected, omitted = files_under(source)
    bridge_files, bridge_omitted = files_under(bridge)
    pins = {str(p): {'size': p.stat().st_size, 'sha256': sha(p)} for p in selected + bridge_files}
    for rel, expected in EXPECTED.items():
        if pins[str(source / rel)]['sha256'] != expected:
            raise ValueError('Selected source changed: ' + rel)
    dlls = [p for p in bridge_files if p.name.lower().startswith('dx11fsrbridge') and p.suffix.lower() == '.dll']
    if len(dlls) != 1 or pins[str(dlls[0])]['sha256'] != BRIDGE_SHA:
        raise ValueError('Selected Bridge DLL changed or is ambiguous')
    for rel in ('OptiScaler.ini', 'LICENSE', 'OptiScaler/nvngx_dlssnr.dll',
                'OptiScaler/nvngx.dll_dlssnr.dll', 'OptiScaler/streamline/sl.interposer.dll',
                'OptiScaler/streamline-starrail-2.13/sl.interposer.dll',
                *(f'profiles/{key}.ini' for _, key in GAMES.values())):
        if not (source / rel).is_file():
            raise FileNotFoundError(source / rel)
    for rel in ('Dx11FsrBridge.ini', 'nvngx_dlss.dll'):
        if not (bridge / rel).is_file():
            raise FileNotFoundError(bridge / rel)

    generated, payload = {}, {}
    for p in selected:
        relative = p.relative_to(source).as_posix()
        if relative == 'build.json':
            continue
        if p.suffix.lower() == '.ini':
            generated[RUNTIME + relative] = portable(p.read_bytes(), source)
        else:
            payload[RUNTIME + relative] = p
    for p in bridge_files:
        relative = p.relative_to(bridge).as_posix()
        if p == dlls[0]:
            relative = 'Dx11FsrBridge.dll'
        payload[BRIDGE + relative] = p
    for name in ('LICENSE', 'NOTICE'):
        p = args.bridge_notices.absolute() / name
        check_path(p)
        pins[str(p)] = {'size': p.stat().st_size, 'sha256': sha(p)}
        payload[BRIDGE + name] = p
    generated[RUNTIME + 'build.json'] = encode({
        'sourceId': 'F6', 'version': 'v5', 'assetName': output.name,
        'main': pins[str(source / 'OptiScaler.dll')], 'packageVersion': 'v5',
        'minLauncherVersion': '1.4.8.2', 'requiresLauncherCapabilities': ['reset_launch_options'],
        'defaultNr': '310.9.16-SF', 'asi': ['plugins/MFGUnlocker.asi'],
        'gameTestedByPackaging': False,
    })
    generated[BRIDGE + 'build.json'] = encode({
        'sourceId': 'genshin-fsr-bridge', 'version': '2.4.0.0', 'assetName': output.name,
        'main': pins[str(dlls[0])], 'dll': 'Dx11FsrBridge.dll', 'game': 'hk4e',
    })
    for game, (title, key) in GAMES.items():
        preset = f'F6 v5 ASI {title}'
        config = f'F6-v5-{game}.ini'
        generated[f'GamePack/{game}/{config}'] = generated[RUNTIME + f'profiles/{key}.ini']
        steps = [
            {'action': 'require_launcher_version', 'minVersion': '1.4.8.2'},
            {'action': 'reset_launch_options', 'once': True},
            {'action': 'set_opt_build', 'build': 'F6/v5', 'once': True},
            {'action': 'import_opt_config', 'file': config, 'name': preset, 'once': True},
            {'action': 'set_opt_config', 'name': preset, 'if_missing': True, 'once': True},
            {'action': 'set_opt_config', 'name': preset, 'once': True},
            {'action': 'set_opt_nr', 'default': True, 'once': True},
        ]
        if game == 'hk4e':
            steps += [
                {'action': 'set_module', 'id': 'genshin-fsr-bridge', 'enabled': True, 'once': True},
                {'action': 'set_module', 'enabled': True, 'once': True},
            ]
        generated[f'GamePack/{game}/auto.json'] = encode({'actions': [{
            'name': preset + (' + FSR Bridge' if game == 'hk4e' else ''),
            'runOnLaunch': True, 'steps': steps,
        }]})

    readme = '''# F6 > v5：ASI + FSR Bridge 三游戏覆盖包

直接将原始 ZIP 拖入启动器，确认各游戏的覆盖包动作后应用。
需要本次配套的 1.4.8.2 启动器构建（支持 reset_launch_options）；
同号旧构建不支持完整清空启动项，应先切到配套构建后重新导入。

- 原神：清空附加启动项与原模块选择，启用 OptiScaler，选 F6/v5；额外启用模块并只选 Genshin FSR Bridge。
- 崩铁、绝区零：清空附加启动项与原模块选择，只启用 OptiScaler，选 F6/v5。
- 三游戏分别导入并应用包内自己的完整 OptiScaler INI，已有配置也切换至该游戏的 F6 v5 ASI 配置。
- 包含 OptiScaler.dll、plugins/MFGUnlocker.asi、DLSS/DLSSD/DLSSG/NR、两套 Streamline、全部源配置及许可证。
- ASI 加载保持开启；内置 Ada/Ampere 解锁开关沿用源配置，不额外叠加。
- NR 使用指定 F6-ASI 源目录自带的 310.9.16-SF，清除旧的按游戏 NR 覆盖选择。
- Bridge 来自指定 temp3，原 DLL 仅规范文件名为 Dx11FsrBridge.dll，完整保留其 INI 和 nvngx_dlss.dll；仅原神加载。
- 保留三游戏的画质、分辨率、NR、多帧、方向等源参数。仅将 Opti 日志、插件、依赖库的本机绝对路径改为便携值，启动时由启动器钉入实际路径。
- once 动作在确认后应用一次，不反复覆盖用户后续调整；不携带本机动作账本。

源 DLL、ASI 与配置清单、SHA256 见 filelist.json。源目录未改动，未包含日志、缓存、转储、数据库、启动器或 ReShade 核心。
压缩包 CRC、逐文件 SHA256 与隔离导入检查不代表游戏实测通过。
'''.encode('utf-8')
    generated['README-F6-v5.md'] = readme
    for game in GAMES:
        generated[f'GamePack/{game}/README-F6-v5.md'] = readme
    if set(payload) & set(generated):
        raise ValueError('Duplicate payload/generated paths')
    for name, data in generated.items():
        if name.endswith('.ini'):
            for line in data.decode('utf-8-sig').splitlines():
                if not line.lstrip().startswith((';', '#')) and re.search(r'[A-Za-z]:[\\/]|=\s*\\\\', line):
                    raise ValueError('Absolute configuration path: ' + name)
    output.parent.mkdir(parents=True, exist_ok=True)
    rows = []
    print(f'Packing {len(payload)} source files and {len(generated)} generated files', flush=True)
    with zipfile.ZipFile(partial, 'x', zipfile.ZIP_DEFLATED, compresslevel=6, allowZip64=True) as archive:
        for name, p in sorted(payload.items()):
            digest, size = hashlib.sha256(), 0
            with p.open('rb') as incoming, archive.open(name, 'w', force_zip64=True) as outgoing:
                for chunk in iter(lambda: incoming.read(1024 * 1024), b''):
                    outgoing.write(chunk)
                    digest.update(chunk)
                    size += len(chunk)
            if digest.hexdigest() != pins[str(p)]['sha256'] or size != pins[str(p)]['size']:
                raise RuntimeError('Source changed during packaging: ' + str(p))
            rows.append({'path': name, 'size': size, 'sha256': digest.hexdigest()})
        for name, data in sorted(generated.items()):
            archive.writestr(name, data)
            rows.append({'path': name, 'size': len(data), 'sha256': hashlib.sha256(data).hexdigest()})
        manifest = {
            'hysxOverlay': 1, 'name': 'F6 v5 ASI + FSR Bridge 三游戏覆盖包', 'version': 'f6-v5-asi-fsr',
            'note': '需要支持 reset_launch_options 的 1.4.8.2 配套构建；三游戏选 F6 > v5，原神额外启用 FSR 桥。',
            'gamePacks': [{'game': g, 'path': 'GamePack/' + g} for g in GAMES],
            'targets': [{'from': 'OptiScaler', 'to': 'optiscaler'}, {'from': 'Modules', 'to': 'modules'}],
            'optiscaler': {'sourceId': 'F6', 'version': 'v5', 'select': False}, 'files': rows,
        }
        archive.writestr('filelist.json', encode(manifest))
    print('Verifying ZIP CRC, every entry SHA256 and per-game INI pairs', flush=True)
    game_ini_verification = {}
    with zipfile.ZipFile(partial) as archive:
        if archive.testzip() is not None:
            raise RuntimeError('ZIP CRC failed')
        names = archive.namelist()
        if len(set(n.casefold() for n in names)) != len(names) or set(names) != {r['path'] for r in rows} | {'filelist.json'}:
            raise RuntimeError('ZIP membership mismatch')
        for row in rows:
            with archive.open(row['path']) as stream:
                if hashlib.file_digest(stream, 'sha256').hexdigest() != row['sha256']:
                    raise RuntimeError('ZIP entry SHA256 failed: ' + row['path'])
        for game, (_, key) in GAMES.items():
            profile = RUNTIME + f'profiles/{key}.ini'
            imported = f'GamePack/{game}/F6-v5-{game}.ini'
            expected_ini = generated[profile]
            if archive.read(profile) != expected_ini or archive.read(imported) != expected_ini:
                raise RuntimeError('Game profile/import INI mismatch: ' + game)
            game_ini_verification[game] = {
                'sourceProfile': str(source / f'profiles/{key}.ini'),
                'sourceSha256': pins[str(source / f'profiles/{key}.ini')]['sha256'],
                'runtimeProfile': profile, 'importConfig': imported,
                'packagedSha256': hashlib.sha256(expected_ini).hexdigest(),
                'runtimeAndImportIdentical': True, 'functionalValuesMatchSource': True,
            }
    for name, pin in pins.items():
        if sha(Path(name)) != pin['sha256']:
            raise RuntimeError('Source changed: ' + name)
    os.replace(partial, output)
    report = {
        'zip': str(output), 'bytes': output.stat().st_size, 'sha256': sha(output), 'entries': len(rows) + 1,
        'buildId': 'F6/v5', 'source': str(source), 'bridgeSource': str(bridge),
        'sourcePins': pins, 'excludedSourceEntries': omitted, 'excludedBridgeEntries': bridge_omitted,
        'crcVerified': True, 'entryHashesVerified': True, 'sourceUnchanged': True,
        'functionalIniValuesUnchanged': True, 'asiIncludedAndEnabled': True,
        'gameIniVerification': game_ini_verification,
        'requiredLauncherCapability': 'reset_launch_options', 'gameTested': False, 'uploaded': False,
        'createdUtc': dt.datetime.now(dt.timezone.utc).isoformat(),
    }
    output.with_suffix('.zip.verification.json').write_bytes(encode(report))
    print(json.dumps({k: v for k, v in report.items() if k not in ('sourcePins',)}, ensure_ascii=False, indent=2), flush=True)


if __name__ == '__main__':
    main()
