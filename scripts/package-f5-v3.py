"""Stream the current installed F5 into a three-game launcher overlay, without a staging copy."""
from __future__ import annotations

import argparse
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
RUNTIME = 'OptiScaler/official/DLSS F5/'
PROFILES = {
    'hk4e': ('profiles/hk4e_cn.ini', '原神'),
    'hkrpg': ('profiles/hkrpg_bilibili.ini', '崩铁'),
    'nap': ('profiles/nap_cn.ini', '绝区零'),
}


def sha(path: Path) -> str:
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def json_bytes(value) -> bytes:
    return (json.dumps(value, ensure_ascii=False, indent=2) + '\n').encode('utf-8')


def portable_ini(data: bytes, source: Path) -> bytes:
    """Change only three machine-specific values, preserving all other bytes/settings."""
    prefix = str(source).replace('/', '\\').rstrip('\\')
    replacements = {
        ('log', 'logfilename'): 'OptiScaler.log',
        ('plugins', 'path'): 'auto',
        ('libraries', 'optidllpath'): '.\\OptiScaler',
    }
    section = ''
    result = []
    for line in data.splitlines(keepends=True):
        text = line.decode('utf-8-sig')
        header = re.match(r'^\s*\[([^]]+)\]', text)
        if header:
            section = header[1].strip().casefold()
        match = re.match(r'^([ \t]*([^;#=]+?)[ \t]*=[ \t]*)([^\r\n]*)(\r?\n)?$', text)
        if match and (section, match[2].strip().casefold()) in replacements:
            value = match[3].strip()
            if re.match(r'^[a-zA-Z]:[\\/]|^\\\\', value):
                if not value.replace('/', '\\').casefold().startswith(prefix.casefold() + '\\'):
                    raise ValueError(f'Unrecognized external configuration path: {section}/{match[2]}')
                bom = b'\xef\xbb\xbf' if line.startswith(b'\xef\xbb\xbf') else b''
                line = bom + (match[1] + replacements[(section, match[2].strip().casefold())]
                              + (match[4] or '')).encode('utf-8')
        result.append(line)
    output = b''.join(result)
    for line in output.decode('utf-8-sig').splitlines():
        if not line.lstrip().startswith((';', '#')) and re.search(r'[A-Za-z]:[\\/]|^\s*[^=]+\s*=\s*\\\\', line):
            raise ValueError('Remaining machine-specific absolute INI path')
    return output


def selected_files(source: Path) -> list[Path]:
    spec = importlib.util.spec_from_file_location('f5_overlay_selection', Path(__file__).with_name('package-f5-multigame-overlay.py'))
    helper = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(helper)
    selected = set(helper.selected_files(source))
    selected.update(source / rel for rel, _ in PROFILES.values())
    # Keep the already-installed Streamline/license metadata, not historical build claims.
    selected.update(p for p in (source / 'licenses').rglob('*') if p.is_file() and p.name == 'LICENSE')
    for relative in ('redist/streamline/manifest.json',):
        if (source / relative).is_file():
            selected.add(source / relative)
    for path in selected:
        for part in (path, *path.parents):
            if part == source.parent:
                break
            if part.is_symlink() or (getattr(part.lstat(), 'st_file_attributes', 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT):
                raise ValueError(f'Refusing linked source: {path}')
        if not path.is_file():
            raise FileNotFoundError(path)
    return sorted(selected, key=lambda p: p.relative_to(source).as_posix().casefold())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--expected-main', required=True)
    args = parser.parse_args()
    source, output = args.source.absolute(), args.output.absolute()
    release = (ROOT / 'build/release').resolve()
    if output.parent.resolve() != release or output.suffix != '.zip':
        raise ValueError('Output must be a ZIP directly under repository build/release')
    partial = output.with_name(output.name + '.partial')
    if output.exists() or partial.exists():
        raise FileExistsError('Clean previous generated outputs using the PowerShell entry point')
    if sha(source / 'OptiScaler.dll') != args.expected_main.lower():
        raise ValueError('Active F5 DLL changed since selection; recheck provenance before packaging')
    files = selected_files(source)
    pins = {p.relative_to(source).as_posix(): {'bytes': p.stat().st_size, 'sha256': sha(p)} for p in files}
    if pins['OptiScaler.dll']['sha256'] != args.expected_main.lower():
        raise RuntimeError('F5 main DLL changed while pinning inputs')
    generated = {}
    for game, (profile, title) in PROFILES.items():
        config_name = f'F5 v3 {title}'
        config_file = f'F5-v3-{game}.ini'
        generated[f'GamePack/{game}/{config_file}'] = portable_ini((source / profile).read_bytes(), source)
        generated[f'GamePack/{game}/auto.json'] = json_bytes({'actions': [{
            'name': config_name, 'runOnLaunch': True,
            'steps': [
                {'action': 'set_optiscaler', 'enabled': True, 'once': True},
                {'action': 'set_opt_build', 'build': 'official/DLSS F5', 'once': True},
                {'action': 'import_opt_config', 'file': config_file, 'name': config_name, 'once': True},
                {'action': 'set_opt_config', 'name': config_name, 'once': True},
            ],
        }]})
    generated[RUNTIME + 'build.json'] = json_bytes({
        'sourceId': 'official', 'version': 'DLSS F5', 'assetName': output.name,
        'packageVersion': '3', 'main': pins['OptiScaler.dll'],
        'runtimeTestedByPackaging': False,
    })
    generated['README-F5-v3.md'] = (
        '# F5 覆盖包 v3\n\n'
        '从当前已安装的 DLSS F5 目录打包，包含原神、崩铁、绝区零三份独立配置。\n'
        '直接将原始 ZIP 拖入 HoYoShade Hub，或在全局插件页选择本地安装；不要先解压。\n'
        '覆盖之前请正常退出使用这套 F5 的游戏并自行保留必要回滚；本包不会强退任何程序。\n'
        '进入各游戏插件页确认覆盖包动作后，选择 official/DLSS F5 并导入/应用对应 F5 v3 配置。\n'
        '动作均为 once，仅在确认后执行；不携带已执行账本。三款游戏的方向、质量、倍率设置保持原样。\n'
        '仅将日志路径、插件目录和 OptiDllPath 的本机绝对路径改为可迁移值；Hub 启动时钉入本机依赖路径。\n'
        '不包含启动器、ReShade 核心、外置原神 Bridge、用户数据库、日志、转储或截图。\n'
        '保留当前 NextGenNRSDK helper、Streamline/NR 依赖及已有 MFGUnlocker ASI，不额外引入加载器。\n'
        '本包复刻现有配置，不修正现有内置/外置 MFG 解锁开关组合，也不保证已达成 6X。\n'
        '打包和离线导入校验不等于游戏 NR/FG、深度、视觉或性能验收；没有修改当前安装。\n'
    ).encode('utf-8')
    rows = []
    output.parent.mkdir(parents=True, exist_ok=True)
    succeeded = False
    try:
        with zipfile.ZipFile(partial, 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=6, allowZip64=True) as archive:
            for path in files:
                relative = path.relative_to(source).as_posix()
                name = RUNTIME + relative
                if path.suffix.casefold() == '.ini':
                    data = portable_ini(path.read_bytes(), source)
                    archive.writestr(name, data)
                    digest, size = hashlib.sha256(data).hexdigest(), len(data)
                else:
                    digest, size = hashlib.sha256(), 0
                    with path.open('rb') as incoming, archive.open(name, 'w', force_zip64=True) as outgoing:
                        for chunk in iter(lambda: incoming.read(1024 * 1024), b''):
                            outgoing.write(chunk)
                            digest.update(chunk)
                            size += len(chunk)
                    digest = digest.hexdigest()
                    if digest != pins[relative]['sha256'] or size != pins[relative]['bytes']:
                        raise RuntimeError(f'Source changed during ZIP writing: {relative}')
                rows.append({'path': name, 'size': size, 'sha256': digest})
            for name, data in generated.items():
                archive.writestr(name, data)
                rows.append({'path': name, 'size': len(data), 'sha256': hashlib.sha256(data).hexdigest()})
            manifest = {
                'hysxOverlay': 1, 'name': 'F5 覆盖包', 'version': '3',
                'note': '当前已安装 F5，原神/崩铁/绝区零分别确认一次性动作；保留当前画质与方向配置。',
                'gamePacks': [{'game': game, 'path': 'GamePack/' + game} for game in PROFILES],
                'targets': [{'from': 'OptiScaler', 'to': 'optiscaler'}],
                'optiscaler': {'sourceId': 'official', 'version': 'DLSS F5', 'select': True},
                'files': rows,
            }
            archive.writestr('filelist.json', json_bytes(manifest))
        with zipfile.ZipFile(partial) as archive:
            if archive.testzip() is not None:
                raise RuntimeError('ZIP CRC failed')
            names = archive.namelist()
            if len(names) != len(set(names)) or set(names) != {row['path'] for row in rows} | {'filelist.json'}:
                raise RuntimeError('ZIP membership mismatch')
            for row in rows:
                with archive.open(row['path']) as stream:
                    if hashlib.file_digest(stream, 'sha256').hexdigest() != row['sha256']:
                        raise RuntimeError('ZIP entry hash mismatch: ' + row['path'])
            for relative, pin in pins.items():
                if sha(source / relative) != pin['sha256']:
                    raise RuntimeError('Installed source changed during packaging: ' + relative)
        os.replace(partial, output)
        report = {
            'zip': str(output), 'bytes': output.stat().st_size, 'sha256': sha(output),
            'entries': len(rows) + 1, 'games': list(PROFILES), 'source': str(source),
            'sourcePins': pins, 'createdUtc': dt.datetime.now(dt.timezone.utc).isoformat(),
            'crcVerified': True, 'entryHashesVerified': True, 'sourceUnchanged': True,
            'stagingUsed': False, 'gameTested': False, 'uploaded': False,
        }
        output.with_name(output.name + '.verification.json').write_bytes(json_bytes(report))
        output.with_name(output.name + '.sha256').write_text(report['sha256'] + '  ' + output.name + '\n', encoding='utf-8')
        succeeded = True
        print(json.dumps({k: v for k, v in report.items() if k != 'sourcePins'}, ensure_ascii=False, indent=2), flush=True)
    finally:
        if not succeeded:
            raise RuntimeError('Packaging incomplete; use the PowerShell wrapper for safe partial-output cleanup')


if __name__ == '__main__':
    main()
