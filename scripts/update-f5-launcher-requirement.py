"""Add the launcher version prerequisite to an existing frozen F5 ZIP without changing binaries or profiles."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import zipfile


def encode(value):
    return (json.dumps(value, ensure_ascii=False, indent=2) + '\n').encode('utf-8')


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--min-version', default='1.4.7.1')
    args = parser.parse_args()
    source, output = args.source.resolve(), args.output.resolve()
    partial = output.with_suffix('.zip.partial')
    if source == output or output.exists() or partial.exists():
        raise ValueError('A separate, unused output ZIP is required')
    changes = {}
    with zipfile.ZipFile(source) as old:
        names = old.namelist()
        if len(names) != len(set(names)):
            raise ValueError('Duplicate source entries')
        manifest = json.loads(old.read('filelist.json'))
        rows = {row['path']: row for row in manifest['files']}
        if set(names) != set(rows) | {'filelist.json'}:
            raise ValueError('Source manifest membership mismatch')
        for game in ('hk4e', 'hkrpg', 'nap'):
            name = f'GamePack/{game}/auto.json'
            actions = json.loads(old.read(name))
            for action in actions['actions']:
                guard = {'action': 'require_launcher_version', 'minVersion': args.min_version}
                action['steps'] = [guard] + [step for step in action['steps']
                    if step.get('action', '').lower() not in ('require_launcher_version', 'request_launcher_version')]
            changes[name] = encode(actions)
        name = 'OptiScaler/official/DLSS F5/build.json'
        build = json.loads(old.read(name))
        build['minLauncherVersion'] = args.min_version
        build['requiresLauncherCapabilities'] = list(dict.fromkeys(build.get('requiresLauncherCapabilities', []) + ['require_launcher_version']))
        changes[name] = encode(build)
        changes['README-F5-Final.md'] = (
            f'# F5 v4.1 三游戏覆盖包\n\n先升级启动器至 {args.min_version} 或更新版本，再将本原始 ZIP 导入全局插件页。\n'
            '新安装下载启动器 Full 包；已有安装使用启动器更新。旧 app-1.4.6.12 专用增量不再使用。\n'
            '此前首次导入若提示找不到 NR 文件，升级后须重新导入本 ZIP，才能恢复丢失的子目录。\n'
            '动作先检查最低启动器版本；不足时提示更新并停止后续动作，不被 once 成功记录跳过。\n\n'
            '三游戏使用 official/DLSS F5；原神 NR 为 原神v4_预设A_1080p5000；崩铁、绝区零为 310.9.16-SF。\n'
            'Opt/NR DLL 与游戏配置沿用原冻结 F5，已有 profile、质量、倍率和分辨率保留。\n'
            '只修改动作与交付元数据。本轮运行、UI 及游戏验证由用户执行；构建和 ZIP 校验不是游戏验收。\n'
        ).encode('utf-8')
        manifest['version'] = 'final-20261103-launcher-' + args.min_version
        manifest['note'] = f'要求启动器至少 {args.min_version}；先更新，再导入。已有游戏配置保留。'
        for name, data in changes.items():
            rows[name] = {'path': name, 'size': len(data), 'sha256': hashlib.sha256(data).hexdigest()}
        manifest['files'] = [rows[row['path']] for row in manifest['files']]
        changes['filelist.json'] = encode(manifest)
        with zipfile.ZipFile(partial, 'x', zipfile.ZIP_DEFLATED, compresslevel=6, allowZip64=True) as new:
            for info in old.infolist():
                if info.filename in changes:
                    new.writestr(info.filename, changes[info.filename])
                    continue
                sha = hashlib.sha256()
                with old.open(info) as incoming, new.open(info.filename, 'w', force_zip64=True) as outgoing:
                    for chunk in iter(lambda: incoming.read(1024 * 1024), b''):
                        outgoing.write(chunk)
                        sha.update(chunk)
                if sha.hexdigest() != rows[info.filename]['sha256']:
                    raise ValueError('Frozen source hash mismatch: ' + info.filename)
    with zipfile.ZipFile(partial) as new:
        if set(new.namelist()) != set(names):
            raise ValueError('Output membership changed')
        for row in manifest['files']:
            with new.open(row['path']) as stream:
                if hashlib.file_digest(stream, 'sha256').hexdigest() != row['sha256']:
                    raise ValueError('Output content hash mismatch: ' + row['path'])
    os.replace(partial, output)
    receipt = {'zip': str(output), 'bytes': output.stat().st_size, 'sha256': digest(output),
               'source': str(source), 'sourceSha256': digest(source), 'minLauncherVersion': args.min_version,
               'entries': len(names), 'changedEntries': sorted(changes), 'frozenBinariesAndProfilesUnchanged': True,
               'entryHashesVerified': True, 'gameTested': False, 'runtimeTested': False, 'uploaded': False}
    output.with_suffix('.zip.verification.json').write_bytes(encode(receipt))
    output.with_suffix('.zip.sha256').write_text(receipt['sha256'] + '  ' + output.name + '\n', encoding='utf-8')
    print(json.dumps(receipt, ensure_ascii=False, indent=2), flush=True)


if __name__ == '__main__':
    main()
