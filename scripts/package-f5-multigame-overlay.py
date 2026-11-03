#!/usr/bin/env python3
"""Package the current DLSS F5 OptiScaler build with separate HSR and ZZZ actions."""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import zipfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_SOURCE = Path(
    r"D:\APPS\test\TEST\HoYoShadeHub_Portable_1.4.5.0_x64_Full"
    r"\OptiScaler\official\DLSS F5"
)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def is_transient(relative: Path) -> bool:
    parts = [part.casefold() for part in relative.parts]
    name = relative.name.casefold()
    return (
        any(part.startswith(".") or "backup" in part for part in parts)
        or "genshincapture" in parts
        or name.endswith((".log", ".dmp", ".bin", ".zip", ".bak"))
        or ".before" in name
        or name in {"build.json", "filelist.json"}
    )


def selected_files(source: Path) -> list[Path]:
    files: list[Path] = []
    for path in source.rglob("*"):
        if not path.is_file():
            continue
        relative = path.relative_to(source)
        if is_transient(relative):
            continue
        suffix = path.suffix.casefold()
        rel = relative.as_posix().casefold()
        if suffix == ".dll":
            files.append(path)
        elif rel in {"optiscaler.ini", "license", "readme.txt"}:
            files.append(path)
        elif rel in {"profiles/nap_cn.ini", "profiles/hkrpg_bilibili.ini"}:
            files.append(path)
        elif relative.parts[0].casefold() in {"optiScaler".casefold(), "plugins", "licenses"} and suffix in {
            ".asi", ".cfg", ".ini", ".md", ".txt"
        }:
            files.append(path)
    return sorted(files, key=lambda p: p.relative_to(source).as_posix().casefold())


def action_json(game_name: str, config_file: str, config_name: str) -> bytes:
    actions = {
        "actions": [
            {
                "name": f"DLSS F5 OptiScaler - {game_name}",
                "runOnLaunch": True,
                "steps": [
                    {"action": "set_optiscaler", "enabled": True, "once": True},
                    {"action": "set_opt_build", "build": "official/DLSS F5", "once": True},
                    {
                        "action": "import_opt_config",
                        "file": config_file,
                        "name": config_name,
                        "once": True,
                    },
                    {"action": "set_opt_config", "name": config_name, "once": True},
                ],
            }
        ]
    }
    return (json.dumps(actions, ensure_ascii=False, indent=2) + "\n").encode("utf-8")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=DEFAULT_SOURCE)
    parser.add_argument("--version", default="1.4.6.13")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--stage", type=Path)
    args = parser.parse_args()

    source = args.source.resolve()
    if not (source / "OptiScaler.dll").is_file():
        raise SystemExit(f"Missing current OptiScaler.dll: {source}")
    profiles = {
        "nap": ("profiles/nap_cn.ini", "F5-DLSS-绝区零.ini", "F5 DLSS 绝区零"),
        "hkrpg": ("profiles/hkrpg_bilibili.ini", "F5-DLSS-崩铁.ini", "F5 DLSS 崩铁"),
    }
    for source_profile, _, _ in profiles.values():
        if not (source / Path(source_profile)).is_file():
            raise SystemExit(f"Missing current per-game profile: {source / Path(source_profile)}")

    zip_path = (args.output or ROOT / "build" / "release" / f"DLSS-F5-OptiScaler-nap-hkrpg-{args.version}.zip").resolve()
    stage = (args.stage or ROOT / "build" / f"f5-multigame-overlay-{args.version}").resolve()
    if stage.exists():
        raise SystemExit(f"Refusing to overwrite existing staging directory: {stage}")
    if zip_path.exists():
        raise SystemExit(f"Refusing to overwrite existing overlay ZIP: {zip_path}")

    files = selected_files(source)
    dll_files = [path for path in files if path.suffix.casefold() == ".dll"]
    if not dll_files:
        raise SystemExit("No OptiScaler runtime DLLs were selected.")

    runtime_root = Path("OptiScaler/official/DLSS F5")
    for file in files:
        destination = stage / runtime_root / file.relative_to(source)
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(file, destination)

    game_packs = [
        {"game": "nap", "path": "GamePack/nap"},
        {"game": "hkrpg", "path": "GamePack/hkrpg"},
    ]
    for game, (source_profile, config_file, config_name) in profiles.items():
        game_root = stage / "GamePack" / game
        game_root.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source / Path(source_profile), game_root / config_file)
        game_name = "绝区零" if game == "nap" else "崩铁"
        (game_root / "auto.json").write_bytes(action_json(game_name, config_file, config_name))

    members = sorted(
        path for path in stage.rglob("*") if path.is_file()
    )
    manifest = {
        "hysxOverlay": 1,
        "name": "DLSS F5 OptiScaler - 绝区零 + 崩铁",
        "version": args.version,
        "note": "共用当前 DLSS F5 OptiScaler 构建；绝区零与崩铁分别导入各自配置并各自启用。",
        "gamePacks": game_packs,
        "targets": [{"from": "OptiScaler", "to": "optiscaler"}],
        "optiscaler": {"sourceId": "official", "version": "DLSS F5", "select": True},
        "files": [
            {"path": path.relative_to(stage).as_posix(), "size": path.stat().st_size}
            for path in members
        ],
    }
    (stage / "filelist.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )

    zip_path.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(zip_path, "x", compression=zipfile.ZIP_DEFLATED, compresslevel=1) as archive:
        for path in sorted(p for p in stage.rglob("*") if p.is_file()):
            archive.write(path, path.relative_to(stage).as_posix())

    with zipfile.ZipFile(zip_path) as archive:
        bad = archive.testzip()
        if bad is not None:
            raise RuntimeError(f"ZIP CRC failure: {bad}")
        names = set(archive.namelist())
        expected_dlls = {
            (runtime_root / path.relative_to(source)).as_posix() for path in dll_files
        }
        found_dlls = {name for name in names if name.casefold().endswith(".dll")}
        if expected_dlls != found_dlls:
            raise RuntimeError("ZIP DLL payload differs from the selected current F5 DLL set")
        for game, (_, config_file, _) in profiles.items():
            if f"GamePack/{game}/auto.json" not in names or f"GamePack/{game}/{config_file}" not in names:
                raise RuntimeError(f"Missing per-game auto/config payload: {game}")
        for forbidden in ("OptiScaler_crash.dmp", "OptiScaler.log", "GenshinCapture/"):
            if any(forbidden.casefold() in name.casefold() for name in names):
                raise RuntimeError(f"Transient test data leaked into overlay: {forbidden}")
        for name in names:
            if name.endswith("auto.json"):
                actions = json.loads(archive.read(name))["actions"][0]["steps"]
                if not actions or not all(step.get("once") is True for step in actions):
                    raise RuntimeError(f"Expected one-time actions in {name}")

    report = {
        "zip": str(zip_path),
        "stage": str(stage),
        "source": str(source),
        "sourceDllCount": len(dll_files),
        "zipEntryCount": len(names),
        "zipBytes": zip_path.stat().st_size,
        "sha256": sha256(zip_path),
        "crc": "PASS",
        "dllSet": "PASS",
        "gamePacks": ["nap", "hkrpg"],
        "runtimeTested": False,
    }
    report_path = zip_path.with_suffix(".build-report.json")
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
