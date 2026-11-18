# F6 Launcher 1.4.9 发行核验

发行源码以远端 `90da537138ed08902fb29441eb7d0f9ad9a7299e` 为基线，纳入主工作区截至 2026-11-18 的启动器源码快照。独立目录为 `D:\CODE\F6-release-1.4.9-20261118`，未切换主工作区 HEAD 或修改其 index。

## 发行包

- 普通 x64 便携包：`F6Launcher_Portable_1.4.9_x64.zip`。
- 大小：197321196 bytes；933 个条目全部与发布暂存逐文件比较 SHA256。
- SHA256：`969df4ae7b694f7f686e7921f043ecf5a5590b4333600c5dd33ac84dbfede128`。
- SDK 10.0.404，应用随附 .NET 10.0.15；轻量原生后台文件版本为 1.4.9.0。后台构建缓存同时核对发行版本及 SDK 版本。
- 原始 LICENSE、Starward_LICENSE、NOTICE.md 在包根与应用目录逐字节核验；525 个网络相关文件通过发行扫描。
- 唯一根 EXE 为 F6Launcher.exe；version.ini 指向 app-1.4.9\HoYoShadeHub.exe，内部兼容入口与 F6Launcher.exe 字节一致。
- 无 Full 框架、覆盖包、用户数据库、配置、日志或下载缓存。

## 本次执行的检查

| 检查 | 结果 |
|---|---|
| 原神精度与配置 | 397 passed / 0 failed |
| 崩铁精度与配置 | 39 passed / 0 failed |
| 游戏进程识别 | 32 passed |
| Rocket 预览解析 | 16 passed / 0 failed；本次使用离线夹具 |
| UI 生命周期 | 44 passed / 0 failed |
| Opti 页面加载 | 20 passed / 0 failed |
| Opti 下载规则、取消、超时 | 24 passed |
| NR 选择 | 28 passed / 0 failed |
| 模块 DLL 身份 | 17 passed |
| 启动自动清理 | 8 passed / 0 failed |
| 启动器组合回归 | 16 suites passed / 0 failed |
| 覆盖包生命周期 | 111 passed / 0 failed；见下述覆盖缺口 |
| 应用目录保留与回收 | 38 passed，含真实辅助进程与硬链接夹具 |
| 请求活动与缓存 | 34 passed |
| 游戏元数据与安装大小 | 19 passed |
| 最终原生后台 | 27 passed；辅助进程采样峰值私有内存 7.61 MiB |
| FPS 远程资源与会话 | 19 + 28 passed |
| 原生注入辅助程序 | 9 passed；仅诊断与自建目标，不启动或注入游戏 |
| 启动入口策略 | 13 passed |
| 历史更新器安装最终 ZIP | 938 passed，使用不可变历史源码 66345e2 |
| 当前更新器安装最终 ZIP | 938 passed |

覆盖包生命周期报告仍有 47 项动作效果或持久化分支缺口，默认退出码为 2；已执行的 111 个契约用例通过。该结果不代表全部动作真实效果均已覆盖。详细边界见 [覆盖包生命周期验证](PACK-LIFECYCLE-VERIFICATION-20261115.md)。

本次发现并更新了旧文件占用回归的预期：新版安装器应抛出包含具体文件名的 IOException，保留被锁文件的字节，释放占用后可重新安装；不得把部分复制报告为安装成功。

GUI 和真实游戏未验收，`uiTested=false`、`gameTested=false`。逐文件包校验、新旧更新器安装、辅助进程和原生后台检查不替代这些验收。

完整构建与检查日志留在独立发布目录的 `build/package-1.4.9-final.log` 和 `build/release-checks/`。GitHub 仅上传普通 ZIP；校验记录保存在源码及本地，不作为独立发行附件。
