# 启动前配置准备耗时（2026-11-09）

范围：用户确认是启动器窗口内点击启动游戏后等待；仅处理启动器的配置准备和同步。插件崩溃由其他任务负责。

## 已确认的开销

- 原 `NrRuntimeLibrary.VersionFile` 对一份已硬链接的 NR 仍读取三遍完整内容：指纹校验、Intern 源哈希、Intern 内容库哈希。
- 原 `GameAddonPackService.Sync` 两次调用 VersionFile；原 Shader 启动入口先 Sync 一次，创建游戏的服务又 Sync 两次。该路径合计可读取 NR 18 遍。
- 本机所选 SF 文件为 276162560 字节。原同一个单次 VersionFile 实测 4548.7 / 4263.1 / 2083.7 ms；新实现 516.2 / 470.1 / 453.0 ms。测量受磁盘缓存影响，只证明这项操作，不能当作完整游戏启动验收。
- 原 `GetSelectedOptiScalerDll` 调用默认 `List()` / `GetSelected()`，会为全部构建统计目录大小；启动路径只需要 DLL 位置。
- 原 Opti 准备和游戏 INI 准备在界面线程同步执行，文件操作期间没有正常响应。

## 修改

1. 每次解析先完整计算源 SHA256，将本次计算结果用于目录指纹验证和 Intern。内容库与源经 NTFS 文件身份确认是硬链接时复用本次摘要；不同文件仍独立校验。没有跨调用的大小/时间摘要缓存。
2. 一次插件包同步只解析所选 NR 一次，复用已验证的源路径绑定根目录与已有子目录；仍强制硬链接。
3. `GameLaunchIniPreparation.PrepareForLaunch` 统一“补缺失 INI → 同步包 → 判断路线 → 初始化 INI”的顺序。新安装无需靠第二次同步补救缺失目标。
4. Shader 路线在注入器启动前完成上述准备，并向实际创建游戏的服务传递成功结果，取消后半段的重复准备。准备失败仍由原错误处理记录，不能误记成功。
5. Opti 准备使用已有异步工作队列，并先取得本次启动 reservation；INI 文件准备在 worker 运行。返回 UI 后核验页面上下文与取消状态。
6. 查询所选 Opt DLL 使用 `includeDirectorySize: false`。
7. 新日志记录 `Launch configuration` 的 discoverMs / packMs / bootstrapMs / totalMs，以及 `Launch OptiScaler preparation` 总耗时，用于下一次用户实测定位剩余等待。

## 验证和部署

- 新增准备测试 18 项；NR 选择/硬链接测试 28 项；覆盖包 INI 动作 42 项、目标初始化 30 项、绑定 23 项；进程检测 25 项，均通过。
- 包含同大小且恢复修改时间的内容变更、不同 canonical 文件损坏、已知指纹拒绝/PE 接受、首次与重复启动单次同步、用户 NR/禁用插件设置保留。
- 1.4.8.2 publish 最终退出码 0，现有 nullable/MVVM 警告仍有。首次编译发现新增 ShadeHost 参数缺少 Models using，已补齐并重新完整 publish。
- 本地部署：`D:\APPS\test\TEST\HoYoShadeHub_Portable_1.4.5.0_x64_Full\app-1.4.8.2-launch-sync-20261109`；927 个文件逐一核验后更新 version.ini。
- 记录：`artifacts\launch-sync-20261109\` 的 build-inputs.json、build.log、nr-before.log、nr-after.log、测试日志、deployment.json。HEAD/index 未变，构建输入无漂移，部署保护文件哈希未变。
- 没有启动游戏或做 UI/真实游戏验收；没有发布 GitHub 或重做 Full ZIP。用户需完全退出旧启动器（含托盘）后重开根目录 F6Launcher.exe。

分钟级等待的全部时间不能由单项基准直接归因。最新真实会话中从 injector ready 到 Start game 的约 16 秒，与重复 NR 同步阶段对应；剩余延迟以新阶段日志为准。
