# 精确原神窗口激活 CLI

状态：R160 回执确认 authority + activate 候选已于 2026-10-30 增量构建（exit 0，307 warnings / 0 errors），仅部署两个 DLL 至 test 的 app-1.4.6.12。2026-10-31 另有隐藏主窗口恢复源码候选，尚未编译/部署，详见文末；不能把 R160 的部署状态当作新参数已经可用。本 owner 不构建、部署、运行测试或操作真实窗口。假窗口测试是否通过不能由构建结果推定。

单独调用 activate_game；按本轮明确授权，成功后单独调用 click_game。不 capture/view 图片。每个调用只能包含一个动作的一个步骤，不能放进同一个批次。下方旧 JSON 示例 PID、FILETIME、HWND 均为占位值，必须替换为本局准确身份；expectedWindow 为十进制 HWND 整数，不能用窗口标题或猜测句柄。

~~~json
{"steps":[{"action":"activate_game","expectedPid":43024,"expectedCreationFileTime":134377430398371062,"expectedWindow":1234}]}
~~~

~~~powershell
& 'D:\APPS\test\TEST\HoYoShadeHub_Portable_1.4.5.0_x64_Full\HoYoShadeHub.exe' run --biz hk4e_cn --file 'D:\absolute\activate.json'
~~~

边界：只允许管理员 hk4e_cn 单动作单步骤 CLI；覆盖包和 GUI 动作不能执行。参数在 UAC 前验证。持有目标进程句柄，验证 PID、创建 FILETIME、GameCatalog 完整 EXE 路径以及显式 HWND 的进程归属、顶层和非 cloaked 状态。默认可见模式在需要时发一次 ShowWindowAsync(SW_RESTORE)、一次 SetForegroundWindow；新增显式隐藏恢复模式见末节。请求后重新验证身份、可见性与实际前台。Windows 不授予前台时直接失败，不循环抢焦点、不 AttachThreadInput、不伪造 Alt、不发送键盘/鼠标输入、不点击、不改 topmost。API 与焦点之间仍存在系统竞争，后续 click_game 仍独立验证前台。本轮禁止 capture/view，不调用截图入口。

Windows 官方接口限制：SetForegroundWindow 受前台锁与进程交互资格限制，管理员权限也不保证成功。保留现有 capture_game 的安全拒绝，未实现后台截图回退。来源为 Microsoft Learn 的 SetForegroundWindow 与 ShowWindowAsync Win32 文档。

源码：ExactGameActivation.cs、Program.cs、LauncherActionRunner.cs。假窗口测试入口 exact-activation-tests，测试不操作真实桌面。部署负责人应同时运行 exact-click-tests 与 exact-capture-tests，确认新增激活入口没有放宽既有截图/点击契约。

2026-10-31 现场调用交接：已有入口足够，无需新增 focus_game 或改 click_game 的前台拒绝行为。调用辅助脚本为 `D:\CODE\HoyoDLSS5\scripts\Invoke-ExactGameEntry.ps1`；必须由唯一现场 owner 从已提权的 PowerShell 7 会话调用。脚本先调用 activate_game，exit 0 才调用一次 click_game；两个 CLI 子进程均隐藏控制台，避免新控制台抢焦点和重复 UAC。Windows 仍可拒绝激活，任何非零退出立即停止，不重试、不模拟其他输入。脚本仅经静态语法检查，尚未在真实窗口执行，不能宣称已经成功聚焦或点击。

~~~powershell
# 在已经提权的 PowerShell 7 中，先将现场核验的十进制 HWND 赋给 $verifiedWindow。
# 70376 / 134378763449837287 是协调者本轮提供的身份，现场变化后必须重新核验。
& 'D:\CODE\HoyoDLSS5\scripts\Invoke-ExactGameEntry.ps1' `
    -ExpectedPid 70376 -ExpectedCreationFileTime 134378763449837287 `
    -ExpectedWindow $verifiedWindow -X 0.5 -Y 0.949
~~~

脚本实际执行文件为 `D:\APPS\test\TEST\HoYoShadeHub_Portable_1.4.5.0_x64_Full\app-1.4.6.12\HoYoShadeHub.exe`，调用形式仍为 `run --biz hk4e_cn --file <单步JSON>`。activate JSON 含 action/expectedPid/expectedCreationFileTime/expectedWindow；click JSON 含 action/expectedPid/expectedCreationFileTime/x/y。完整 EXE 身份由启动器 GameCatalog 独立核验，不接受任意传入的 EXE 路径覆盖。归一化坐标不裁剪；CLI 再次验证身份和实际前台，成功退出仅代表操作回执，不代表已进入世界。每次记录保留于 `%TEMP%\hys-exact-entry-<GUID>`，不写 auto.json、不更改 F5 产品或 profile。

R160 部署记录：Hub DLL UTC `2026-10-30T10:09:56.2105057Z`；Extensions DLL UTC `2026-10-30T10:08:44.0121130Z`。本轮只读元数据与该时间一致；未重复计算哈希。原两 DLL 最小回滚位置为 `D:\DATA\Documents\ChatGPT\DLSS F5 NEXT\audit\v3-r160-interrupted-work-inventory-20261030\rollback\app-1.4.6.12`，完整执行回执在同一 audit 目录的 RESULT.md。

现场补充：DS 已核验本轮 HWND=`42336494`，PID/creation 仍为 `70376` / `134378763449837287`，但其 PowerShell 非管理员。现有 RPC 的 Env/GameInstaller/HoYoShadeInstaller/Updater 接口未提供窗口激活或点击 broker，当前 CLI 的提权是 runas 子进程，不是可复用的动作 broker。因此脚本不能在非管理员环境直接执行，也不能承诺无人值守绕过 UAC。可由现场 owner 使用以下命令一次提权整段脚本，成功授权后两次 CLI 均继承管理员身份，不再次 UAC；如 UAC 需要人工确认而现场无法确认，须报告阻塞，不尝试绕过。下列命令本编写者未执行。

~~~powershell
$entryArgs = @(
    '-NoLogo', '-NoProfile', '-NonInteractive', '-File',
    '"D:\CODE\HoyoDLSS5\scripts\Invoke-ExactGameEntry.ps1"',
    '-ExpectedPid', '70376', '-ExpectedCreationFileTime', '134378763449837287',
    '-ExpectedWindow', '42336494', '-X', '0.5', '-Y', '0.949'
)
$entryProcess = Start-Process `
    -FilePath 'C:\Users\thx11\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe' `
    -Verb RunAs -WindowStyle Hidden -ArgumentList $entryArgs -Wait -PassThru
if ($entryProcess.ExitCode -ne 0) { throw "Exact entry failed: $($entryProcess.ExitCode)" }
~~~

## R269 窗口选择修正（2026-10-31）

现场回执：R263 的 70376/42336494 曾成功；R269 的 31760/24381046 和 17928/7473350 均报可见顶层窗口校验错误，没有执行 click，两个进程均已停止。早期据该文案推断“请求激活前拒绝”，现由 R278 同阶段异常栈核对纠正：这两个 R269 调用同样经过 VerifyForeground，是激活后的实际前台校验失败，不能当作目标主窗口隐藏证据。本轮不重试任何历史 PID/FILETIME/HWND。`Process.MainWindowHandle` 只能作为候选句柄，`Refresh`、窗口标题和 Responding=true 不能替代 HWND 归属/层级/可见性校验。

现交付 **脚本修正，不修改产品 DLL、不需要 build/deploy**：

- `D:\CODE\HoyoDLSS5\scripts\Invoke-ExactGameEntry.ps1`
- `D:\CODE\HoyoDLSS5\scripts\ExactGameWindowInspection.cs`（脚本同目录，Add-Type 在 helper 自身进程内加载只读窗口枚举器；不向游戏加载任何模块）。

新增 `-ResolveWindow` 模式：在管理员 helper 中持有目标进程句柄，验证 PID/creation FILETIME，并记录实际完整 EXE 路径；EnumWindows 按精确 PID 枚举当前桌面窗口。只选择唯一的仍有效、该 PID 所有、可见顶层、无 owner、DWM 未 cloaked 且客户区有效的候选。零个或多个均拒绝，不按标题、不取第一个、不使用缓存 MainWindowHandle、不轮询重试。选定 HWND 在 activate 前及 activate 后再次校验；后者还必须是实际前台，才提交一次 click。现有 CLI 保留管理员、GameCatalog 完整 EXE 路径、PID/creation 与前台的独立校验。显式 `-ExpectedWindow` 模式仍可用，但会拒绝失效/不合格句柄，不自动换窗；两种模式不能混用。窗口切换与输入之间仍存在系统竞争窗口，不能宣称跨两次 CLI 调用原子化。

窗口元数据保存在每轮 `%TEMP%\hys-exact-entry-<GUID>\windows.json`，包含 Exists/Pid/TopLevel/Visible/Minimized/Owner/CloakStatus/Cloaked/ClientAvailable/Width/Height/ClassName；不读取屏幕图像。Exists=false 是失效；Pid 不符是归属不符；TopLevel=false 是非顶层；Visible=false 是隐藏；Visible=true 且 Minimized=true 的候选可进入现有 activate 的一次正常 Restore；cloaked 或 DWM 查询失败不能当普通最小化处理。明确隐藏窗口不通过 ShowWindow 强行显示。显式/选定窗口二次检查失败另存 rejected-window.json；运行异常写 failure.txt；activate 非零则不生成 click.json。

现场 owner 使用 **新启动、再次核验的身份** 调用，禁止照抄历史 PID：

~~~powershell
# 已提权 PowerShell 7；$verifiedPid / $verifiedCreationFileTime 由现场 owner 提供。
& 'D:\CODE\HoyoDLSS5\scripts\Invoke-ExactGameEntry.ps1' `
    -ExpectedPid $verifiedPid -ExpectedCreationFileTime $verifiedCreationFileTime `
    -ResolveWindow -X 0.5 -Y 0.949
~~~

非管理员会话仍须整段一次 RunAs（UAC 不能绕过）；替换旧 $entryArgs 中 ExpectedWindow 两项为单项 `-ResolveWindow`，并使用新身份。不得给窗口解析器增加 launch/stop 或把此 helper 用于启动游戏。本轮只修改两个脚本文件及本文；PowerShell 语法已静态解析，C# 枚举器尚未编译/执行，未运行 helper、产品或游戏，未验证实际聚焦/点击结果。现场唯一 DS 持有所有游戏/CLI 执行权限。

启动隐藏标志源码核对：`GameLauncherService.StartReservedGameAsync` 的直接 ProcessStartInfo 采用 UseShellExecute=true，未设置 Hidden，也未读取/转传父进程 STARTUPINFO；`StartGameWithCMD` 分支会设置 CMD 的 CreateNoWindow=true 和 WindowStyle=Hidden，属于需结合现场启动日志确认的独立路径。`XxmiInjector` 的 CreateProcessW 使用新建零初始化 STARTUPINFO（仅 cb），未设置 STARTF_USESHOWWINDOW/wShowWindow；`InjectorHelper` 的 CreateNoWindow=true 用来隐藏注入器控制台，不是游戏窗口。入口脚本只运行 activate/click，绝不 launch。以上是源码边界，不足以证明某次已退出进程的实际隐藏来源；本轮未修改启动路径和任何 profile/config，也没有建立隐藏标志继承的因果结论。

## 隐藏主窗口正常恢复候选（2026-10-31，待编译部署）

用户补充明确授权正常恢复精确游戏主窗口。前节“隐藏直接停止”仅描述旧行为和未带恢复参数的默认模式，不作为显式授权恢复的永久限制。新增能力在原有 `activate_game` 内实现，不增加输入或注入路径：JSON 显式带 `restoreHiddenWindow: true`，严格校验 boolean；缺省/false 继续只接受可见窗口并允许正常恢复最小化。管理员/hk4e_cn/单动作单步骤预检与 GameCatalog 全 EXE 路径、PID/FILETIME 绑定均保持。

带恢复参数时，启动器在持有目标进程句柄的情况下独立 EnumWindows，要求精确进程只有一个无 owner、有效顶层、类名精确为 UnityWndClass 的主窗口，且等于 expectedWindow。该类名是明确接受的主窗口语义，未知类名拒绝，不用标题推断。选定窗口不得 cloaked，DWM 状态必须可验证，客户区必须有效；其他同类主窗口即使隐藏也计入歧义，不能静默忽略。验证后若隐藏且未最小化，执行一次 ShowWindowAsync(SW_SHOWNORMAL)；最小化则一次 ShowWindowAsync(SW_RESTORE)，不叠加 show。等待后重新核对归属、唯一性、窗口类、可见性、客户区及最小化状态，再按原入口至多请求一次 SetForegroundWindow 并验证实际前台。显示失败、仍隐藏、进程退出、窗口变为其他 owner/class、多主窗口或前台拒绝均失败，绝不发送鼠标/键盘输入。已有 click_game 仍独立验证前台并只发送一次左键。

改动范围：

- `src\HoYoShadeHub.Extensions\Games\ExactGameActivation.cs`（请求显式授权、精确主窗口验证和正常 show/restore）
- `src\HoYoShadeHub.Extensions.Tests\ExactGameActivationTests.cs`（补 fake-window 测试：隐藏授权/无授权、唯一/多/无/未知类窗口、hidden+minimized、归属/类/主窗口数量在显示后改变、PID复用/路径不符、cloaked、show拒绝/仍隐藏/退出及前台拒绝；本 owner 未执行）
- `scripts\ExactGameWindowInspection.cs`、`scripts\Invoke-ExactGameEntry.ps1`（新增 RecoverableMain、-RestoreHiddenWindow；只做枚举/诊断并将授权传入原有 activate_game，自身不调用 show/focus/input）。

**现已可用的是旧可见窗口 activate/click 及脚本枚举入口；恢复隐藏主窗口需要唯一部署 owner 先编译验证并更新安装版 DLL。** 旧安装版不能靠只更新脚本获得恢复能力，未知 JSON 字段可能被旧解析器忽略；隐藏时仍会拒绝。不得把这一行为报告为恢复候选测试失败或已可用。无需更改启动逻辑/F5 DLL/profile/config。产品源码中的 ExactGameClick.cs、Program.cs、LauncherActionRunner.cs 本轮未修改。

本轮最新协调指令：SOURCEFROZEN；停止写入本候选源码和辅助脚本，交唯一 DS 单次增量 build 与原位部署。用户明确不执行 tests/hash/manifest；本文之前的历史测试建议不构成本轮门禁。补充的 fake-window 测试源码仅留作后续维护，不在本轮执行。本 owner 不 build/deploy/run。

唯一 DS 本轮实际现成 build 入口（工作目录 D:\CODE\HoyoDLSS5；本 owner 未执行）：

~~~powershell
& '.\.dotnet10\dotnet.exe' build src/HoYoShadeHub/HoYoShadeHub.csproj -c Release -p:Platform=x64 --no-restore -v:q
~~~

实际输出目录沿用 R160：`D:\CODE\HoyoDLSS5\src\HoYoShadeHub\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64`；部署目标 `D:\APPS\test\TEST\HoYoShadeHub_Portable_1.4.5.0_x64_Full\app-1.4.6.12`。新增功能实现位于 `HoYoShadeHub.Extensions.dll`，该 DLL 必须更新；沿用此前成对发布方案仅同步 `HoYoShadeHub.dll` 与 `HoYoShadeHub.Extensions.dll`，不需要 RPC/EXE/F5 DLL 或配置更新。仅记录本次 actual exit/mtime；不重复 tests/hash/manifest。只有部署后的版本才可声称支持 restoreHiddenWindow。脚本和其同目录 C# 元数据读取器从工作树直接调用，无需复制进安装目录。

编译验证及部署完成后，由唯一现场 owner 从已提权 PowerShell 7 使用新核验身份调用：

~~~powershell
& 'D:\CODE\HoyoDLSS5\scripts\Invoke-ExactGameEntry.ps1' `
    -ExpectedPid $verifiedPid -ExpectedCreationFileTime $verifiedCreationFileTime `
    -ResolveWindow -RestoreHiddenWindow -X 0.5 -Y 0.949
~~~

也可对已核验的确切 HWND 使用 `-ExpectedWindow $verifiedWindow -RestoreHiddenWindow`，不要与 -ResolveWindow 混用。生成的 activate 单步 JSON 形如 `{"steps":[{"action":"activate_game","expectedPid":123,"expectedCreationFileTime":134378789374397894,"expectedWindow":456,"restoreHiddenWindow":true}]}`，其中数字为格式占位值，禁止照抄用于现场。激活 exit 0 且脚本再核对选定 HWND 为可见非最小化前台后，才生成并执行原有 click_game 单步 JSON。非管理员调用仍使用前述整段一次 RunAs，在 $entryArgs 中包含 -ResolveWindow 和 -RestoreHiddenWindow；未实现也不假定无人值守 UAC 绕过。

本轮验证边界：仅静态检查及 PowerShell AST 解析，不编译/执行 C# 或测试、不部署、不读图、不启动/停止/操作游戏，也未重试 R269 旧进程。新增的恢复候选不等于已部署或已经恢复实际游戏主窗口。

## R278 前台校验定位与局部调用修正（2026-10-31）

实际证据：`C:\Users\thx11\AppData\Local\Temp\hys-exact-entry-3771f744a7434b8ebe273b43c6e5e8b3` 中 activate.json 精确传入 PID=78692、FILETIME=134378819141083834、HWND=20711026，没有 restoreHiddenWindow；windows.json 显示该窗口为可见、有效、无 owner、未 cloaked 的 UnityWndClass。helper 和 CLI 观测的并非同一个校验对象：`D:\APPS\test\TEST\HoYoShadeHub_Portable_1.4.5.0_x64_Full\log\HoYoShadeHub_261031.log:2476-2482`（09:00:24.799，CLI PID81112）异常栈明确是 `VerifyWindowIdentity -> VerifyForeground -> ExactGameActivation.ExecuteAsync`。源代码中前置校验使用 expectedWindow；末尾 VerifyForeground 重新取得系统实际前台，随后仍调用同一个 VerifyWindowIdentity。该实际前台不是合格游戏窗口时，它先抛泛化错误，使后面的 `actual.Window != request.Window` 专用前台失败错误不可达。原日志没有记录那一刻实际前台句柄及 SetForegroundWindow 返回值，不能补造这些值，也不能断言是 API 直接拒绝还是激活后丢失前台。

因此已确认的是 **最终前台校验失败及诊断混淆**，并非 helper/activate 对同一目标句柄的可见性检测必然矛盾。对已安装 Extensions DLL 的只读 PE/IL 检查也未发现 expectedWindow 数值截断或 GetAncestor 参数错误：GetAncestor 使用 GA_ROOT=2，句柄为 native int，PID 为 32-bit，JSON 原文整数保留。此次 PE 检查不加载/运行产品、不计算 hash。现场 R278 报告为 `D:\DATA\Documents\ChatGPT\DLSS F5 NEXT\audit\v3-r278-slot-isolation-runtime-20261031\RESULT.md`，游戏已停止、未点击，配置已恢复，不重试该身份。

局部修正包含两部分，**均尚未运行验证**：

1. 优先用于现有安装版的脚本参数 `-DelegateForeground`：启动隐藏 activation CLI 子进程后，向这个精确子进程 PID 调用正常的 AllowSetForegroundWindow，不使用 ASFW_ANY、不授权任意进程。仅等待此单次 activation 调用结束；CLI exit=0、委托未被拒绝且选定窗口再次核验为实际前台后，才执行原有 click_game。只增加权限委托，不自行显示窗口、激活、点击或注入。原有不带该开关的流程保留。
2. 新 DLL 源码诊断修正：ExactGameActivation.ExecuteAsync 在最终 VerifyForeground 前先比较实际前台与 expectedWindow，并输出 requestedWindow、actualForeground、actualForegroundPid、apiAccepted；若随后校验遇到竞争变化，外层错误明确标记 Final foreground verification，保留内部异常，不再单独显示含糊的目标窗口不可见文案。仍不降低身份/可见/前台校验，不增加输入。这一部分需单次 build/deploy 才生效。

Windows 委托调用本身也受正常前台权限限制；管理员身份不等于前台权限。建议从有正常前台交互资格的管理员 PowerShell 会话发起整段 helper。后台 agent 的已提权 helper 未必具备该资格，AllowSetForegroundWindow 被拒绝时本候选明确停止，不能承诺无人值守绕过前台锁。未增加任何 AttachThreadInput、伪造输入、反复抢焦点或其他绕过路径。由于本轮不增加启动握手协议，CLI 启动后立即委托仍有调度竞争；若子进程已退出或在委托生效前尝试激活，必须按失败回执处理，绝不盲重试，不宣称确定性修复 Windows 前台权限。

现场唯一 DS 可使用 **新核验进程身份** 调用已安装的旧 activate/click，无需先为脚本参数更新 DLL：

~~~powershell
& 'D:\CODE\HoyoDLSS5\scripts\Invoke-ExactGameEntry.ps1' `
    -ExpectedPid $verifiedPid -ExpectedCreationFileTime $verifiedCreationFileTime `
    -ResolveWindow -DelegateForeground -X 0.5 -Y 0.949
~~~

本次可见窗口问题不依赖 -RestoreHiddenWindow，也不能用该参数解决前台权限；不带它。额外记录只有正常操作回执 `foreground-grant.json`（指定 CLI PID、委托 accepted/Win32 error、当时 UTC、授权前前台）和 `activate.result.json` / `click.result.json`（实际退出码及前后前台）。旧 CLI 不输出 apiAccepted，不能由 helper 伪造。委托未获准、子进程失败、身份变化或最终前台不符时均无 click.json/点击。

新冻结范围：`ExactGameActivation.cs`（既有恢复候选加前台阶段诊断）、`Invoke-ExactGameEntry.ps1`、`ExactGameWindowInspection.cs`、本文。没有新协议或独立点击/注入入口，不修改启动路径、F5 DLL、profile/config。本 owner 仅静态读取/PowerShell AST 检查，未 tests/hash/manifest/build/deploy/run；停止写入，候选实际效果待唯一 DS 现场验证。此前已冻结的假窗口测试源码未在 R278 追加或执行。
