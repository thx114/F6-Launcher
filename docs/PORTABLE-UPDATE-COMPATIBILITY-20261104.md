# 旧版到 1.4.8.1 的更新兼容

用户反馈内置更新安装时报 `version.ini must point to app-*/HoYoShadeHub.exe.`。核对已发布的 1.4.8 和 1.4.8.1 二进制均接受 F6Launcher.exe；该原样错误提示存在于更早的源码提交 `66345e2a9c5c63a807926993e0dfc61683f7a408`。执行安装的是用户当前运行的旧更新器，它在写入载荷前就拒绝新文件名。因此仅修改新版校验器无法接通旧版升级路径。截图未显示运行中的确切版本，不据此猜测用户的版本或安装路径。

`package.ps1` 继续在包根只生成 F6Launcher.exe，同时将应用 apphost 原字节复制为 `app-<版本>/HoYoShadeHub.exe`，供 version.ini 指向。两个内部入口加载同一 F6Launcher.dll，产品名、图标和源码一致，不引入上游旧程序。Full 流程复用同一规则，从 Full 派生普通包时也必须保留此入口。许可、品牌和归档检查将这项兼容性作为发行门禁。

此内部入口需在仍支持旧版直接更新期间保留。不能因为新版能接受 F6 路径就移除它；发行包必须兼容实际执行安装的最老受支持更新器。

回归脚本 `scripts/test-portable-update-compatibility.py` 从上述不可变的历史提交导出真实 GithubUpdateService、PortableUpdatePolicy 和 LauncherCommandLine，用相同控制台宿主分别编译旧版和当前更新器。它对真实 ZIP 调用 ApplyAsync，校验全部安装文件、version.ini、用户配置与旧目录；可通过 `--original-archive` 确认原包稳定复现同一错误。所有安装目录为一次性临时目录，不启动实际启动器 UI 或游戏。

本轮仍修正 1.4.8.1。普通包、源码标签和 stable / preview 元数据需同步替换，GitHub 仍只有一个普通 ZIP。用户需重新执行“下载并安装”以取得修正后的包，已下载的旧包本身不会发生变化。验证记录位于 `artifacts/update-compat-1481-20261104/`。
