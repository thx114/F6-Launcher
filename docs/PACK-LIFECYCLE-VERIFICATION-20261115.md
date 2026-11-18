# 覆盖包生命周期修复与离线验证（2026-11-15）

四类生产问题已修复，最终新增契约 **111 PASS / 0 FAIL**；六组既有回归 **368 PASS / 0 FAIL**。Core、Extensions 与离线测试宿主编译 **0 errors**，232 项既有警告保留。生产源码已冻结并与本次构建快照逐文件核对，不再扩大生产修改；父任务负责统一 WinUI 构建与部署。

68 个动作名/别名中 **24 个实际执行生产共享 handler 或编译链接的原 handler 并检查文件/返回效果**，44 个只执行策略边界。另有 3 个 Opt 配置别名仍缺真实 AppConfig 数据库持久化集成，因此机器报告 `actionEffectGaps=47`、`allProductionActionEffectsVerified=false`，默认退出码 **2**。不能宣称 68 个动作全部真实效果通过。

未启动或停止任何真实游戏、未启动 GUI、未操作桌面；无网络请求或实际下载。所有夹具、SDK 构建、ProjectReference 的 bin/obj 均在 C 盘。未改测试安装、用户 INI/数据库、HEAD/index，也未发布或打包。符号链接夹具因本机无创建权限跳过，不计为通过；同卷真实硬链接夹具已执行通过。

## 运行与回执

```powershell
pwsh -NoProfile -File .\scripts\verify-pack-lifecycle-20261115.ps1
```

SDK：`C:\Users\thx11\.dotnet10\dotnet.exe`。脚本先复制 Core、Extensions、Tests 和必要链接源码到独立 C 盘目录，再使用 `--artifacts-path` 构建；NuGet sources 为空，仅用已安装缓存。脚本不删除既有文件。

本次最终运行目录：

```text
C:\Users\thx11\AppData\Local\Temp\F6-pack-lifecycle-20261115\run-20261115-092112-94a96474
```

| 文件 | 内容 |
|---|---|
| `contract/results.json` | 111 个用例结果、68 行动作矩阵、明确的 47 项效果/分支缺口 |
| `contract/actions.md` | 完整动作名/别名矩阵 |
| `verification.json` | 构建与七组套件退出码、原工作区 HEAD/index 前后值 |
| `build.log` | 0 errors / 232 warnings |
| `source-files.json` | 独立源码副本逐文件 SHA256 |
| `build-locations.json` | 实读 NuGet restore 图：所有 csproj、ProjectReference、obj 均位于 C 盘 |
| `production-freeze.json` | 本次 16 个生产文件与已构建快照逐字节一致的 SHA256 |
| `contract/fixtures-*` | 真实 ZIP、INI、预设、profile、账本、锁文件与放置文件备份 |

`HEAD=66345e2a9c5c63a807926993e0dfc61683f7a408`；index SHA256 前后均为 `465E6ED513AC4ADF9668D59083A94D6CFAA6A1924D2687DF4B46C5C4078AA64B`。本任务在 D 盘仅修改源码/测试/文档/脚本，未新增 D 盘 bin/obj，未删除既有文件。

退出码：`1` 表示构建、契约或既有回归失败；`2` 表示已执行行为通过但真实动作效果仍有缺口；`0` 才表示所选门禁通过。`-AllowCoverageGaps` 仅显式放行 2，不能放行行为失败；`-SkipExistingSuites` 可跳过六组既有回归。

## 四类修复

| 原问题 | 生产修复 | 真实回归 |
|---|---|---|
| `ini_config.json` 每次启动覆盖用户 INI（原/崩/绝三个失败） | `GameIniBootstrap` 通过 `PackApplicationBatch` 对 `ini_config` 应用一次；使用 `ApplyStrict`，异常保持 pending | 三游戏首次默认值应用；用户保存后多次启动保留；同包/新版/不同包显式导入新批次可再次应用 |
| 包内预设每次按内容覆盖用户保存 | `SyncPresets` 逐预设记录批次成功；私有临时副本 + rename 替换，不写穿旧硬链接 | 用户预设编辑保留；锁定失败不记 success，解锁重试；显式新批次覆盖；真实硬链接 peer 字节不变 |
| 启动递归删除未确认的新 `auto.json` | `Program` 移除递归清理；兼容 `PackAutoStartupCleanup` 返回 0，不枚举/打开/删除文件 | 已安装、pending、深层隐藏动作及被独占锁定动作均保留；导入后延后确认、重启仍可应用 |
| GamePack-only 包在落位前被拒绝 | `LocalPackageInstaller` 将有效 GamePack 算作载荷，共用完整 stage/commit；卡住文件不发布新批次 | 三游戏纯动作包正常安装/绑定/应用；损坏 ZIP 和被锁目标失败不清账、解锁可重新导入 |

`PackApplicationBatch` 的 `.hysx_apply.json` 是安装器生成的本地状态，包内禁止提供 `.hysx_apply*` 或 `.hysx_once*`。仅完整显式导入建立新 GUID，普通内容/hash/路径变化不会授权重放。首次使用无记录旧包会建立本地首应用状态，损坏记录保持拒绝应用，不静默重新初始化。隐式旧包状态不会改变现有 auto.json 字节 hash/同意算法。

安装器将新批次与 GamePack 一起暂存提交，并在该 stage 内清除旧 once 账本。`PackActionPrompt` 只刷新同意，不再提前删除 once；pending 迁移失败时旧内容/记录保留。无新批次标记的遗留 pending 迁移继续保留既有 once 历史，不凭普通迁移猜测用户授权重放。`PackAutoActionSnapshot` 同时捕获批次身份，完全相同 auto.json 的重新导入也会使旧快照失效。

导入涉及多个游戏时按游戏目录逐个事务提交，不声称跨游戏全包原子回滚；若后续目标失败，已完整安装的前面游戏仍保留有效新批次，失败目标保留旧批次。

## 已执行的新增分支

- INI 首次写入、模板无关键保留、set/remove、三游戏隔离、普通启动后用户编辑保留。
- 完全相同包、新版本同逻辑身份、不同包新增 plan：显式导入都能开始新批次，随后恢复 once。
- 默认 INI/预设的真实独占锁失败、pending、解锁重试；非 INI once 失败不自动重放；INI 显式重试只恢复失败项与尾部。
- 单步骤勾选/禁用、整动作禁用、原索引稳定；拒绝项不预留、不记 success。
- 被锁 GamePack 安装失败不换批次、不清完成账本，后续重导入可恢复；包不能注入本地批次账本。
- 三个 Opt 配置别名 × 三游戏的真实 `if_missing`：main/profile 缺失时首次创建并激活，用户 Save Settings 后已有 profile 跳过且不改选择。
- Opt import/capture/apply handler：编译链接原生产 partial，真实写预设/profile/main；错误/锁定配置不能返回成功。
- 真实版本比较（满足/过低/坏版本）、等待参数（0ms/非法/超范围），不进行长时间等待。
- 插件禁用、预设导入与选择、两份 INI 删除及失败报告、game/launcher 目录覆盖、game/launcher/shade 显式文件放置与备份。
- shader 文本导入/删除、technique 开关和排序幂等；只处理文本，未编译 shader 或使用 GPU。

## 生产执行边界与诚实覆盖矩阵

`PackLocalActionExecutor` 是从现有 WinUI runner 移出的原本地 handler，环境仅由 `Context` 提供。生产 runner 的薄适配器继续调用它；父任务新增的 `override_files`、`import_shader` 的 `Task.Run` worker 边界保留。`clear_game_ini` 的删除失败由原先吞异常改为明确失败，不能误记 once 成功。

`LauncherActionRunner.OptConfig.cs` 从原 runner 原样拆出三个配置 handler，并通过 csproj 编译链接进离线宿主。`PackActionHandlerFixture` 只给出上下文和原本已有的 AppConfig 环境端口，不复制 handler 业务。AppConfig 替身仅提供根目录、已选构建映射、配置读写字典；真实数据库持久化仍列为缺口。预设、profile、main、冲突处理均用原生产服务。

权威动作清单仍是生产 `DispatchStepAsync`，测试检查 42 组、68 名完全一致。策略回调的成功不会被计作 handler 效果证据。

| 动作名/别名 | 实际证据 | 剩余缺口 |
|---|---|---|
| `require_launcher_version` | 真实共享 handler + 临时文件效果 | WinUI 展示不属于离线验收 |
| `request_launcher_version` | 真实共享 handler + 临时文件效果 | WinUI 展示不属于离线验收 |
| `set_addons` | 真实共享 handler + 临时文件效果 | — |
| `set_addon` | 真实共享 handler + 临时文件效果 | — |
| `import_preset` | 真实共享 handler + 临时文件效果 | — |
| `apply_preset` | 真实共享 handler + 临时文件效果 | — |
| `clear_game_ini` | 真实共享 handler + 临时文件效果 | — |
| `set_ini_keys` | 真实共享 handler + 临时文件效果 | — |
| `set_ini` | 真实共享 handler + 临时文件效果 | — |
| `write_ini` | 真实共享 handler + 临时文件效果 | — |
| `set_dx12` | 仅真实快照/同意选择/once 策略；handler 待验 | AppConfig game settings adapter is not linked. |
| `set_smooth_motion` | 仅真实快照/同意选择/once 策略；handler 待验 | GPU/driver and page integration is outside the offline fixture. |
| `set_ai_frame` | 仅真实快照/同意选择/once 策略；handler 待验 | GPU/driver and page integration is outside the offline fixture. |
| `set_ai_interpolation` | 仅真实快照/同意选择/once 策略；handler 待验 | GPU/driver and page integration is outside the offline fixture. |
| `set_inject_mode` | 仅真实快照/同意选择/once 策略；handler 待验 | AppConfig game settings adapter is not linked. |
| `set_cmd_launch` | 仅真实快照/同意选择/once 策略；handler 待验 | AppConfig game settings adapter is not linked. |
| `set_cmd_args` | 仅真实快照/同意选择/once 策略；handler 待验 | AppConfig argument adapter is not linked. |
| `set_launch_option` | 仅真实快照/同意选择/once 策略；handler 待验 | AppConfig game settings adapter is not linked. |
| `reset_launch_options` | 仅真实快照/同意选择/once 策略；handler 待验 | Production per-game reset adapter is not linked. |
| `set_opt` | 仅真实快照/同意选择/once 策略；handler 待验 | AppConfig selection adapter is not linked. |
| `set_optiscaler` | 仅真实快照/同意选择/once 策略；handler 待验 | AppConfig selection adapter is not linked. |
| `set_opt_build` | 仅真实快照/同意选择/once 策略；handler 待验 | Production build selection/persistence adapter is not linked. |
| `set_optiscaler_build` | 仅真实快照/同意选择/once 策略；handler 待验 | Production build selection/persistence adapter is not linked. |
| `select_opt_build` | 仅真实快照/同意选择/once 策略；handler 待验 | Production build selection/persistence adapter is not linked. |
| `set_opt_nr` | 仅真实快照/同意选择/once 策略；handler 待验 | Production hash/PE validation and selection adapter is not linked; a transport fixture is insufficient. |
| `set_optiscaler_nr` | 仅真实快照/同意选择/once 策略；handler 待验 | Production hash/PE validation and selection adapter is not linked; a transport fixture is insufficient. |
| `set_module` | 仅真实快照/同意选择/once 策略；handler 待验 | Production module global/game selection adapter is not linked. |
| `import_opt_config` | 编译链接原生产 OptConfig handler + 真实预设/profile/main | — |
| `update_opt_config` | 编译链接原生产 OptConfig handler + 真实预设/profile/main | — |
| `set_opt_config` | 编译链接原生产 OptConfig handler + 真实预设/profile/main | AppConfig database persistence is an environment port; real DB is not opened. |
| `apply_opt_config` | 编译链接原生产 OptConfig handler + 真实预设/profile/main | AppConfig database persistence is an environment port; real DB is not opened. |
| `select_opt_config` | 编译链接原生产 OptConfig handler + 真实预设/profile/main | AppConfig database persistence is an environment port; real DB is not opened. |
| `set_opt_dll` | 仅真实快照/同意选择/once 策略；handler 待验 | AppConfig injection-name adapter is not linked. |
| `import_xxmi` | 仅真实快照/同意选择/once 策略；handler 待验 | Production per-game mod directory lease is not exercised. |
| `switch_dll` | 仅真实快照/同意选择/once 策略；handler 待验 | Production DLL family resolution/download/selection is not exercised. |
| `set_inject_delay` | 仅真实快照/同意选择/once 策略；handler 待验 | AppConfig delay adapter is not linked. |
| `set_game_setting` | 仅真实快照/同意选择/once 策略；handler 待验 | Production game settings persistence is not exercised. |
| `set_window_mode` | 仅真实快照/同意选择/once 策略；handler 待验 | Real game registry writes are forbidden in this fixture. |
| `force_window_mode` | 仅真实快照/同意选择/once 策略；handler 待验 | Real game registry writes are forbidden in this fixture. |
| `override_files` | 真实共享 handler + 临时文件效果 | — |
| `copy_file` | 真实共享 handler + 临时文件效果 | — |
| `place_file` | 真实共享 handler + 临时文件效果 | — |
| `import_shader` | 真实共享 handler + 临时文件效果 | — |
| `set_shader` | 真实共享 handler + 临时文件效果 | — |
| `delete_shader` | 真实共享 handler + 临时文件效果 | — |
| `run_compat_check` | 仅真实快照/同意选择/once 策略；handler 待验 | Environment/driver checks are outside this fixture. |
| `add_game_from_registry` | 仅真实快照/同意选择/once 策略；handler 待验 | Real installed-game registry access is outside this fixture. |
| `launch_game` | 仅真实快照/同意选择/once 策略；handler 待验 | Starting games is forbidden in this offline fixture. |
| `launch` | 仅真实快照/同意选择/once 策略；handler 待验 | Starting games is forbidden in this offline fixture. |
| `start_game` | 仅真实快照/同意选择/once 策略；handler 待验 | Starting games is forbidden in this offline fixture. |
| `stop_game` | 仅真实快照/同意选择/once 策略；handler 待验 | Stopping games is forbidden in this offline fixture. |
| `close_game` | 仅真实快照/同意选择/once 策略；handler 待验 | Stopping games is forbidden in this offline fixture. |
| `kill_game` | 仅真实快照/同意选择/once 策略；handler 待验 | Stopping games is forbidden in this offline fixture. |
| `stopgame` | 仅真实快照/同意选择/once 策略；handler 待验 | Stopping games is forbidden in this offline fixture. |
| `wait` | 真实共享 handler + 临时文件效果 | — |
| `sleep` | 真实共享 handler + 临时文件效果 | — |
| `delay` | 真实共享 handler + 临时文件效果 | — |
| `click_game` | 仅真实快照/同意选择/once 策略；handler 待验 | Real input/window actions are forbidden in this fixture. |
| `click_window` | 仅真实快照/同意选择/once 策略；handler 待验 | Real input/window actions are forbidden in this fixture. |
| `capture_game` | 仅真实快照/同意选择/once 策略；handler 待验 | Real game capture is forbidden in this fixture. |
| `activate_game` | 仅真实快照/同意选择/once 策略；handler 待验 | Real window activation is forbidden in this fixture. |
| `press_key` | 仅真实快照/同意选择/once 策略；handler 待验 | Real input actions are forbidden in this fixture. |
| `send_key` | 仅真实快照/同意选择/once 策略；handler 待验 | Real input actions are forbidden in this fixture. |
| `test_game` | 仅真实快照/同意选择/once 策略；handler 待验 | Starting/stopping games is forbidden in this fixture. |
| `launch_wait_stop` | 仅真实快照/同意选择/once 策略；handler 待验 | Starting/stopping games is forbidden in this fixture. |
| `self_test` | 仅真实快照/同意选择/once 策略；handler 待验 | Starting/stopping games is forbidden in this fixture. |
| `test_map` | 仅真实快照/同意选择/once 策略；handler 待验 | Starting games and sending input is forbidden in this fixture. |
| `map_test` | 仅真实快照/同意选择/once 策略；handler 待验 | Starting games and sending input is forbidden in this fixture. |

## 剩余环境动作的最小可维护后续方案

| 类别 | 最小共享边界 | 当前后续验收 |
|---|---|---|
| AppConfig 启动项、命令参数、构建/模块/NR/DLL 名/时机选择 | 将 handler 的语义校验和每游戏 mutation plan 下沉共享层；生产注入一个真实 AppConfig 适配器，离线仅替换 persistence port，不复制 switch | 在 C 盘临时用户库做真实持久化往返，再 GUI 确认三游戏隔离；NR 还需真实 SHA/PE/硬链接链路 |
| SmoothMotion/兼容性检测 | 显式传入 GPU/驱动能力快照，文件/策略执行与 WinUI 展示拆开 | 驱动能力与提示由父任务/用户实际机器验收，不能由 fixture 硬填成功 |
| XXMI 导入 | 共享输入路径与每游戏 backend/mod-dir lease，原导入服务执行真实 ZIP/目录文件操作 | 真实 backend 根隔离的 fixture，再 GUI 选择确认 |
| registry 游戏设置/加游戏 | 只抽注册表读取/写入端口，保持已有游戏解析和 mutation 逻辑唯一 | 临时 registry hive 或只读录制数据，不写用户真实游戏键；GUI 当前游戏结果待验 |
| DLL 清单/下载/选择 | 注入 catalog source 和 file-transport；下载结果以真实本地文件走安装/校验 | 离线本地传输可验安装；公网下载、显卡默认选择与 GUI 提示仍需集成 |
| 游戏启动/关闭、按键、窗口、截图、test_map | 共享动作参数/计划校验，外部进程/窗口/输入接口为显式端口 | 只能验证解析/计划或专用非游戏 helper；本次未启动/停止/发送输入到真实游戏 |

本次不为这些缺口构建数十个 WinUI/AppConfig 假类型，也不重写 handler 业务。部署后的页面响应、同意弹窗/3 秒倒计时、后台下载转移展示、真实用户库与游戏进程效果由父任务和用户统一验收。

## 改动文件归属

生产：`GameIniBootstrap.cs`、`GameAddonPackUserContent.cs`、`GamePackIniConfig.cs`、`PackAutoStartupCleanup.cs`、`PackApplicationBatch.cs`、`PackAutoAction.cs`、`PackActionOncePolicy.cs`、`PackPendingMigration.cs`、`LocalPackageInstaller.cs`、启动器 `Program.cs` 的旧清理块、`PackActionPrompt.cs` 的提前清账调用，以及 `LauncherActionRunner` 的本地执行适配与拆出的 `OptConfig` partial。

测试/工具：`PackLifecycleContractTests.cs`、`PackActionHandlerFixture.cs`、`OptiScalerPresetSelectionTests.cs` 的最小环境端口、`PackAutoStartupCleanupTests.cs`、`PackOnceNavigationTests.cs` 的新事务预期、测试 `Program.cs` 的入口与旧预设覆盖预期、测试 csproj 的原 handler 链接、`verify-pack-lifecycle-20261115.ps1`、本文档。未触碰父任务处理的 PageBase/GlobalPluginPage/DllConfigPage/Setting/Xxmi/DownloadService/BackgroundModeCoordinator。
