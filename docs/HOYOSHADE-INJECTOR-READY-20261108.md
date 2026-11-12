# HoYoShade 注入器未就绪、游戏不能启动

## 现场

测试目录：`D:\APPS\test\TEST\HoYoShadeHub_Portable_1.4.5.0_x64_Full`。
现场日志 `log\HoYoShadeHub_261108.log` 中，`inject.exe YuanShen.exe`
启动后连续等待 30000ms，未收到 `HOYOSHADE_READY:9999`，随后被本次启动清理。
例如日志原始时间 `23:28:43.406` 启动 PID 120876，`23:29:13.408` 超时。
禁用插件后仍出现同样的就绪超时。

同时仍可枚举到原神 PID 51144、60652，两者线程数为 0、工作集 40960；
用户明确确认它们是已退出记录。正常原神实例与这些残留记录应分别处理。

本地 HoYoShade 注入器源码在发送 READY 之前存在无限循环：枚举第一个同名
进程、以 PROCESS_TERMINATE 打开并终止，再重新枚举；打开失败也不退出循环。
无法清除的同名残留记录使流程无法走到 READY，与当前超时现场相符。
原来的注入目标枚举也不检查线程数，直接移除等待前循环还不够：随后可能
错误地把残留 PID 选成注入目标。

此前日志中的超时后启动命令属于后续启动操作；仅凭时间顺序不能断言是失败
分支自动回退。当前正常启动函数在注入器就绪失败后直接返回，没有自动裸启动。

## 实现

- 新的 `native\HoYoShadeInjector` 保留上游版权与 BSD 许可，记录来源源码哈希。
  删除就绪前终止同名进程的循环；目标筛选同时检查名称、当前会话和非零线程数。
  不再调用注入成功后的游戏目录删除逻辑。
- 随启动器应用目录携带 `F6ShadeInjector.exe`，由 MSBuild 从源码编译并随普通
  publish/package/Full 流程分发，附带两份组件许可证。
- `InjectorHelper` 统一使用这个版本化注入器，通过 `--shade-root` 指定原有
  HoYoShade/OpenHoYoShade 框架。现有框架注入器、ReShade DLL 和用户配置不被替换。
- stdout、stderr 均识别 READY，超时或提前退出会记录有限长度的输出摘要。
  退出事件先订阅后启用，处理调用方释放对象与回调并发的情况。
- 进程发现放行可成功读取到的零线程记录。服务和实际进程创建处的独立防重复
  检查继续保护正在运行的实例；游戏时长发现使用同一筛选，避免旧 PID 覆盖真实 PID。

版本保持 `1.4.8.2`，没有递增测试版本。

## 验证与边界

证据目录：`artifacts\injector-ready-fix-20261108`。

- `injector-tests.log`：9 项通过。真实框架的只读 READY 检查通过且 INI 内容一致；
  生产 `InjectorHelper` 在隔离框架中收到 READY、保持等待、完成 INIBuild；
  缺文件会返回 1001。最终一次就绪交接检查耗时 457ms。
- `process-tests.log`：25 项通过，覆盖零线程、受限查询、真实无窗口子进程、
  死记录与活进程混排、实际创建回调阻断和含点号进程名。
- `process-snapshot.log`：当前 YuanShen 无活目标，允许启动。
- `build-exit.txt`、`build-final.log`：完整自包含应用发布结果；保留现有编译警告。
- `Deploy.ps1` / `deployment.json`：源码输入哈希、逐文件部署哈希、许可、原入口
  指针及受保护文件哈希；应用目录同盘移动，避免重复复制整棵发布树。

测试只使用自建子进程或唯一的虚构目标名称；没有启动、结束或注入真实游戏。
`--check-ready` 不运行 INIBuild、不枚举游戏、不尝试注入、不写 INI。
构建、READY 交接和部署不代表真实游戏注入或视觉验收完成。

部署目标：
`D:\APPS\test\TEST\HoYoShadeHub_Portable_1.4.5.0_x64_Full\app-1.4.8.2-injector-fix-20261108`。
部署脚本在全部检查通过后才更新根目录 `version.ini`。旧启动器进程保留，
用户须从托盘完全退出后，再从根目录 `F6Launcher.exe` 打开以载入新应用。
本轮未更新 Full ZIP、未发布 GitHub。
