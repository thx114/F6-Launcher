# 精确停止游戏 CLI（2026-10-27）

状态：源码完成，Release/x64 构建通过（309 警告、0 错误）。28 项回归通过。test 存在运行中的启动器 PID 56512，本次未关闭它、未部署、未启动或停止真实游戏。F5 夹具必须等待对应构建实际部署及文件哈希核对后再启用此语法。

## CLI 合约

```text
HoYoShadeHub.exe stopgame --biz hk4e_cn --expected-pid <正十进制PID> --expected-creation-filetime <正十进制Int64 FILETIME>
```

`killgame` 接受相同参数。两个 expected 参数必须同时出现且各一次，使用空格分隔参数值；拒绝等号形式、缺值、重复、负数、零、非整数、溢出和超出 Windows FILETIME 日期范围。FILETIME 为 Windows 自 1601-01-01 UTC 起的 100ns 计数，不能传 Unix 时间或毫秒，也不能经浮点数转换。

PowerShell 夹具应在捕获目标进程时保存精确创建时间字符串：

```powershell
$expectedCreationFileTime = $capturedProcess.StartTime.ToFileTimeUtc().ToString([Globalization.CultureInfo]::InvariantCulture)
```

未提供任何 expected 参数的旧 `stopgame --biz ...` / `killgame --biz ...` 保持原行为。精确参数只适用于这两个 CLI 动词；没有扩展覆盖包动作或 `run --json` 的接口。

精确模式退出码：0 为该绑定进程已退出；1 为参数错误、游戏无法解析、权限不足、身份核验失败或停止失败。普通权限调用沿用现有 UAC 输出接力，提权后完整重新核验条件。失败不得自行重试不带 expected 参数的旧命令。

## 身份和操作边界

- 从 `--biz` 对应 GameCatalog 条目取得完整游戏 EXE 路径；没有绝对路径时拒绝。
- 仅对 expected PID 调用一次 OpenProcess，并持有同一 SafeProcessHandle。请求 QUERY_LIMITED_INFORMATION、SYNCHRONIZE、TERMINATE 权限；无法取得权限立即失败。
- 从该句柄读取 GetProcessTimes 创建 FILETIME、QueryFullProcessImageNameW 路径以及退出状态，精确核验 PID、创建时间和游戏完整路径。不按 PE 文件名猜测游戏。
- 校验后向该进程窗口请求关闭，等待最多 5 秒。正常退出即结束；仍未退出时 TerminateProcess 使用同一持有的句柄，再等待最多 5 秒确认退出。
- 不调用 GetGameProcessAsync、不再按游戏名寻找进程、不重新按 PID 打开、不调用按名 taskkill、不遍历杀进程树。窗口枚举仅用于已绑定进程的 WM_CLOSE。
- 已退出、身份失配、读取失败或权限拒绝会直接返回失败。原进程在等待中退出时，不操作其后启动的新游戏。窗口消息发送失败也直接返回失败。

## 测试和交付

- 实现：`src/HoYoShadeHub.Extensions/Games/ExactProcessStop.cs`，CLI 分流：`src/HoYoShadeHub/Program.cs`。
- 测试：`src/HoYoShadeHub.Extensions.Tests/ExactProcessStopTests.cs`，命令 `exact-stop-tests`。
- 回归覆盖：旧命令分流、100ns 精度、PID 重用、游戏路径不匹配、进程已退出、进程退出后新开、绑定权限拒绝、强制停止权限拒绝、参数缺失/非法/超范围、同一句柄强制分支和无回退。
- 28 项检查使用假进程进行停止操作；原生适配器只对自测宿主执行故意错误创建时间的拒绝检查，不发送关闭或终止请求。
- 日志：`artifacts/exact-stop-tests-20261027.log`、`artifacts/exact-stop-build-20261027.log`。
- 文件身份：`artifacts/exact-stop-identity-20261027.json`，包含 EXE、主 DLL、Extensions DLL 的完整路径、长度、SHA256。
- 构建根：`src/HoYoShadeHub/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64`。
- 已准备部署脚本 `artifacts/Deploy-ExactStopTest-20261027.ps1`，要求 test 启动器退出，核验预期主 DLL SHA256 后备份、替换、逐文件验证。部署后不得自动执行真实停止验收。
- 本次未修改 F5 profile、插件或测试夹具。未改变真实游戏状态。

## 实际部署完成：2026-10-27 13:33（Asia/Shanghai）

用户确认正常退出后重新核验：部署前无 HoYoShadeHub 进程，4 个待替换目标均通过独占读取检查；三份源文件哈希与交付身份清单一致。未启动启动器或游戏。

- 状态：APPLIED，980 文件逐一 SHA256 匹配，4 文件替换，便携根文件未改变。
- 实际目的地：D:\APPS\test\TEST\HoYoShadeHub_Portable_1.4.5.0_x64_Full\app-1.4.6.12。
- 备份：D:\APPS\test\TEST\HoYoShadeHub_Portable_1.4.5.0_x64_Full\_backup-nr-20261027-133315。
- 原始 receipt：artifacts/nr-test-deploy-20261027-133315/receipt.json；包含完整部署文件前像、后像及备份路径的 plan.json 位于同目录。
- 汇总 receipt（含实际目的地完整文件身份）：artifacts/exact-stop-deployment-20261027.json。
- HoYoShadeHub.exe SHA256：336C1A09A36924D4206E0741F35F20E6A3B7C8F1E54F6188F92B4B98D51CB8D6。
- HoYoShadeHub.dll SHA256：7EACDE9EEE9F4FB715BF37B67A181146D8A146CAAC43330F75AA9EE403A6B68A。
- HoYoShadeHub.Extensions.dll SHA256：5AD2B3838CE76C74DA2C74265A870B96CBD1949250A19AB386FA454C4725AA4D。

本节取代前文的“尚未部署”状态。仅完成构建、回归和文件部署验证，不代表真实游戏停止行为已实测。F5 可据上述目的地和身份接入精确停止 adapter。
