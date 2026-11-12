# 拖入覆盖包后立即绑定 ReShade.ini

## 问题与行为

用户指定的本地 Full：`build/release/F6Launcher_Portable_1.4.8.2_x64_Full`。
其 `log/HoYoShadeHub_261106.log` 在 00:53:32 记录原神 4.2 覆盖包文件安装完成，
00:53:41 的游戏插件页却扫描游戏目录下的 `reshade-shaders/Addons`，得到 0 个插件。
文件安装与游戏 INI 的路径绑定没有在所有导入分支闭合。

导入流程现在先迁移 GamePack、铺设游戏专属 Addons，并准备和绑定游戏 ReShade.ini，
然后才读取、分组及确认 auto.json 动作。没有 auto.json 或选择稍后执行动作也能完成绑定。
包内动作仍遵守原同意与 once 规则；动作结束后再次绑定，修复旧覆盖包写回相对 AddonPath 的情况。

`GamePackIniBinding` 共用于导入和显式包同步。缺失主 INI 时复用已有模板/宿主初始化，
已有 INI 只修改 AddonPath，不清 NR、预设、插件开关或其他用户键。目标目录缺失或 INI 无法写入时明确报错，
不会把这部分失败作为完整安装成功。常规导航仍只读，不在切页时自动安装或绑定。

## 验证与交付

`artifacts/pack-import-binding-20261106/` 保存隔离检查和后续构建/部署证据。
INI 动作 42 项、初始化 30 项、绑定 23 项、同意和迁移 20 项通过；安装进度与包安装检查通过，
其中创建重解析点的负例因本机权限不足跳过。未启动或关闭游戏，不将隔离检查等同于 UI/游戏验收。

沿用 1.4.8.2，不增加测试版本号。使用正式 `package-full.ps1` 构建，保护上述已在使用的 Full 目录，
部署时使用新的版本化应用目录并切换 version.ini，保留旧程序供运行中进程使用。
Full ZIP 仍位于 `build/release/F6Launcher_Portable_1.4.8.2_x64_Full.zip`；不上传 GitHub。

正式构建退出码 0，2019 个 ZIP 条目通过逐文件校验和额外 CRC 检查；成品 339175851 字节，
SHA256 `DEE915C8F113115784C8883A19C069356EEAB5543A17237C8F442B2CF6E50948`。
765 个源码/构建输入在构建期间无变化。924 个应用文件已按包内哈希部署到
`app-1.4.8.2-pack-binding-20261106`，version.ini 已切换；原用户数据、插件、缓存、配置及硬链接身份未变，
HEAD/index 未变。没有重启已有启动器；用户需退出并重新打开该 Full 根目录的 F6Launcher.exe，重新拖入覆盖包验收。
