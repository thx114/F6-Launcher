# F6 > v5 ASI + FSR Bridge 覆盖包

产物：`build/release/[原铁绝]F6_v5_ASI_FSR.zip`，630340311 字节（601.14 MiB）。
SHA256：`1155c8a87d07078ad0cca27b205e17c456f27955f0f67d8ff0c6619f18dd1cef`。

## 最新部署刷新（2026-11-12）

用户通知 F6 再次部署后，按同一 F6-ASI 目录重新打包，仍使用 F6/v5 和启动器 1.4.8.2。
主 DLL SHA256 更新为 `a6d12031d8b33af050a81df75029eec7096d0ac4668d47308cc2fb6d545727d6`。
原神 profile 同步源目录当前的 Sharpness=0.736000、MenuPage=overlay；仍只转换三个本机路径。
与上一包相比，仅主 DLL、构建信息、原神 profile、原神导入配置及文件清单变化；三游戏动作、崩铁/绝区零配置、NR、ASI 和 Bridge 均未变化。

本次重新完成 70 条目的 CRC、逐文件 SHA256、源文件稳定性和 INI 功能值校验，并检查 HEAD/index 未变化。未重跑下文原始构建的 76 项隔离安装检查，未启动 GUI 或游戏、未上传。
刷新证据：`artifacts/f6-v5-refresh-20261112-235630/refresh-verification.json`；ZIP 旁 verification.json 为本次新清单。
旧 ZIP 和校验记录同盘移至 `D:\cleanup\F6-v5-before-refresh-20261112-235630` 供回退，未删除，也未释放空间。

## 来源与内容

- F6：`D:\APPS\test\TEST\HoYoShadeHub_Portable_1.4.5.0_x64_Full\OptiScaler\local\F6-ASI`。
- FSR：同一便携根下 `temp3`。DLL `Dx11FsrBridge (2).dll` 原字节规范为 `Dx11FsrBridge.dll`，PE 版本 2.4.0.0。
- 包含全部非运行产物：OptiScaler DLL、MFGUnlocker.asi、根目录和依赖目录中的 DLSS/DLSSD/DLSSG/NR、两套 Streamline、完整主配置和三个 profile、许可证。Bridge 包含 DLL、完整 INI、nvngx_dlss.dll，并补充桥源码目录的 LICENSE/NOTICE。
- 只排除 logs/ngx 日志目录以及桥的 features/render-scale cache、日志。源 DLL、ASI、配置未修改；INIs 仅将三个本机路径值转换成便携值，所有功能键和键值逐项比较一致。
- 包内不含启动器、ReShade 核心、用户数据库、执行账本。

## 安装动作

库路径 `OptiScaler/F6/v5`，build sourceId=`F6`、version=`v5`，卡片显示 F6，版本 v5。

各游戏动作顺序：要求版本 1.4.8.2 → 清空附加启动项和旧模块选择 → 选 F6/v5 → 导入对应游戏完整 INI → 初始化缺失 profile → 强制应用对应配置 → NR 恢复构建默认。
本次指定源的默认 NR 是 310.9.16-SF，SHA256 `cdfa6c1ec784867182164c7836f324f087b5c9f374d0c9fe46f54999feabf3ed`。
原神再启用 genshin-fsr-bridge 及模块启动项；崩铁、绝区零保持模块关闭。ASI 由 Opti 的 Plugins.LoadAsiPlugins=true 加载，三游戏一致。
动作沿用逐项确认和 once；不会在后续启动反复覆盖手动设置。

## 必要的启动器接口补充

现有 set_launch_option 仅实现六个开关，注释列出的 Starward/Blender 并未实现，且无法清除 Rocket 或旧模块选择。因此增加单个动作 reset_launch_options，统一通过既有 AppConfig setter 关闭十种附加启动选项，清空当前游戏模块列表，并保持游戏本体启动。
只影响当前游戏；不关闭其他游戏或全局模块，不改驱动、游戏启动参数或游戏进程。
修改范围：LauncherActionRunner 分派、新增 LauncherActionRunner.LaunchOptions.cs、PackActionConsent 描述，以及 CLI.md 文档。

配套构建保持 1.4.8.2，未升版、未上传 GitHub。已部署到测试便携根的 `app-1.4.8.2-f6-overlay-20261112`，version.ini 已切换；当前打开的旧进程需正常退出后重启。
旧同号 1.4.8.2 无法凭数值版本区分能力：请先使用此配套构建。旧构建会在未知 reset_launch_options 处阻断后续动作，不能按 README 的完整效果使用。

## 验证与重建

脚本：`scripts/package-f6-v5-asi-overlay.py`。只读源，直接流式生成 ZIP，不生成整包暂存副本，拒绝覆盖已有产物。

```powershell
python scripts/package-f6-v5-asi-overlay.py --source 'D:\APPS\test\TEST\HoYoShadeHub_Portable_1.4.5.0_x64_Full\OptiScaler\local\F6-ASI' --bridge 'D:\APPS\test\TEST\HoYoShadeHub_Portable_1.4.5.0_x64_Full\temp3' --output 'D:\CODE\HoyoDLSS5\build\release\[原铁绝]F6_v5_ASI_FSR.zip'
```

- 70 个 ZIP 条目，CRC、逐文件 SHA256、源文件未变化、INI 功能值不变均通过。
- 实际 LocalPackageInstaller 隔离安装、三个 GamePack 的真实 once 执行流程通过 76 项检查，覆盖预先启用的旧开关、模块精确选择、其他游戏/全局模块不变、缺失游戏拒绝及重复执行不重置用户改动。
- 安装后的 55 个 runtime/ASI/Bridge 文件再次与 ZIP 清单逐一 SHA256 比较通过。
- Release 构建通过；部署 927 个文件全部逐文件 SHA256 校验；保护源文件、HEAD 和 index。
- 未启动 GUI 或游戏，不代表 NR/FG/画面/性能实测。

证据位于 `artifacts/f6-v5-overlay-20261112/`；ZIP 旁有 verification.json，源文件 SHA256/排除项完整记录在其中。
