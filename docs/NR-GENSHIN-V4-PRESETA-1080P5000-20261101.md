# DLSS F5 当前 NR DLL：原神v4_预设A_1080p5000

记录日期：2026-11-01（Asia/Shanghai）。版本名称由用户明确指定，内容身份按正式 DLSS F5 目录现有文件确认。

## 内容身份

- 显示版本：`原神v4_预设A_1080p5000`。
- 文件名：`nvngx_dlssnr.dll`。
- 来源：`D:\APPS\test\TEST\HoYoShadeHub_Portable_1.4.5.0_x64_Full\OptiScaler\official\DLSS F5\nvngx_dlssnr.dll`。
- DLL 大小：`276162560` 字节。
- DLL SHA256：`094D2B0E28EEB65A4250F99EEE8230B1BC9BEFCB459331E569DDB76B26BCC6CB`。
- PE 文件版本：`310,9,16,0`，清单归一化为 `310.9.16`；架构 `Amd64`，DLL 标志已确认。
- 源文件 LastWriteTimeUtc：`2026-10-31T17:40:25.0249656Z`。

## 远端分发

- 运行库仓库：`thx114/HoYoShadeHub-DLSSNR-Runtimes`。
- Release tag：`nr-genshin-v4-presetA-1080p5000-20261101`。
- ZIP：`nvngx_dlssnr_GenshinV4-PresetA-1080p5000.zip`，内含唯一条目 `nvngx_dlssnr.dll`。
- ZIP 大小：`198245386` 字节。
- ZIP SHA256：`8490938386B42D860B7ED99577F311A861FDFADBA205D502688E4CCBA1B0F5B5`。
- 下载地址：`https://github.com/thx114/HoYoShadeHub-DLSSNR-Runtimes/releases/download/nr-genshin-v4-presetA-1080p5000-20261101/nvngx_dlssnr_GenshinV4-PresetA-1080p5000.zip`。
- 发布附带 `nr-runtime.json` 和 `SHA256SUMS.txt`。
- 启动器消费入口：`thx114/HoYoShadeHub-DLSS5` 的 `main:catalog/dll-fingerprints.json`，同时登记 DLL 指纹与 ZIP 下载条目。

## 启动器使用

联网刷新远端目录后，在 NR DLL 列表选择 `原神v4_预设A_1080p5000` 下载；继续沿用已有归档、校验与每游戏硬链接机制。清单支持热更新，无需为这条数据重新编译启动器。自动刷新有 24 小时间隔，需要即时出现时使用设置页的强制刷新入口。

本次只记录内容身份与发布下载，未替换游戏运行目录、修改用户 NR 质量/倍率/分辨率或更改自动默认选择；上传/下载校验不代表游戏画质与性能验收。发布验证回执位于 `artifacts/nr-genshin-v4-presetA-1080p5000-20261101/`。
