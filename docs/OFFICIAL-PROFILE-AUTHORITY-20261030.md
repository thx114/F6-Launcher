# 正式游戏 profile 与隔离运行时

状态：Sol 编码完成候选；仅静态 diff 检查，未编译/运行测试/部署/启动游戏，等待独立审查及唯一 DeepSeek 执行者验证。R147 独立证据确认原实现只首次复制非 DLL、cache DLL 优先、退出仅回写 cache，偏离用户入口。

持久入口：test\OptiScaler\official\DLSS F5\profiles\<biz>.ini。所有选中构建采用同一规则，隔离 runtime 只承载 DLL/NR 选择和动态机器路径。NR/DLL 链接逻辑不变。

启动前 GameNrRuntimeService.PrepareOpti 在准备 DLL 后调用 PrepareAuthoritativeProfile，登记 official 构建和 gameKey。首次迁移保留 cache 快照后以 official profile 为准，不将没有来源基线的旧 cache 自动导回 official。官方 profile 不存在时只允许通过官方构建既有 Activate 规则建立；已有绑定后缺少 official 文件则阻断，不用 cache 重建。

登记后 CollectRuntimeChangesChecked/Store 统一按本次 official baseline、当前 official、运行时 main 三方合并。official 改过的键优先，runtime 只回写 official 相对 baseline 未改的键；运行时独立修改/删除可保留。OptiDllPath 恢复官方原文/缺失，不把机器路径写入持久 profile；cache 投影保留运行时路径。投影同时同步 cache main/profile/baseline 和官方来源基线。一般启动恢复不允许绕过 authority 失败走旧 runtime-precedence 覆盖。

所有提交先保留 snapshot，投影输入直接取 snapshot 中的 official 字节；比较准备期间内容是否变化，逐文件写前再次比较，使用原子单文件替换和事务标记。提交记录实际写入文件与所写字节；失败仅恢复实际写过且当前仍等于本事务写入内容的文件。不触碰事务没写过的 official，不覆盖写后发生的外部编辑；发现外部变更/回滚不完整保留阻断标记。该文件集事务不是跨进程文件系统事务，字节比较与原子替换之间仍存在极窄系统竞争窗口。首次迁移仅备份旧 cache，不接受其为用户权威。未跟踪旧 runtime 修改可从 profile-recovery 手动恢复。

UI BuildDirectory 指向构建源目录；注入 DllPath 仍指向隔离目录。Follow 读取官方 profile；preset 构建身份经 authority 绑定解析回官方构建。启动专用 SyncIntoProfileForLaunchChecked 对所有已有游戏 profile（包括无 NR/无 cache 的直接 official 路径）保留当前 profile，不隐式重放旧 named preset；显式 Apply 仍可替换 profile，首次缺少 profile 仍走既有初始化。

R150 三项 P2 delta 仅修改 OptiScalerPresets.cs、OptiScalerProfiles.Authority.cs、OptiScalerProfileAuthorityTests.cs 及本文。新增确定性交错测试：snapshot 前官方 A→B 必须投影 B；投影失败不恢复未写过的 official；自身 runtime 写入失败完整回滚；collect 写过的 official 被外部替换后不回滚覆盖且保留阻断标记；无 cache 启动保留官方编辑且显式 Apply 仍有效。测试使用 internal AsyncLocal checkpoint 注入实际 I/O 交错，不使用 sleep 碰概率。此 delta 未编译/运行，等待下一轮源码复审。

R154 首次执行在第 10 项断言 `runtime machine path retained in cache` 失败，后续测试、产品构建及部署均未执行，回执见 `D:\DATA\Documents\ChatGPT\DLSS F5 NEXT\audit\v3-r154-native-ds-launcher-deploy-20261030\RESULT.md`。源码核对确认 `IniDocument.GetValue` 返回等号后的原始文本，`OptiScalerRuntime.WriteIniValue` 写入 `key = value`，旧测试将带前导空格的路径与无空格路径直接比较。此处需修正测试的语义读取，不能据此认定运行时路径丢失。

R157 test-only 修正已在当前测试文件中存在，本轮核对后保留：Value helper 增加 `.Trim()`；加强 main/cache profile/cache baseline 的机器路径断言、official/authority baseline 的 `auto` 断言，并检查 FG 冲突胜者、独立 NR 修改及两组基线字节一致性。原有失败回滚、外部修改冲突、无 cache 启动、显式 Apply、锁定重试、游戏隔离和缺失 authority 测试保持不变。两个冻结产品源码哈希与 R150 一致。本轮只更新本文并静态核对，不重复修改测试，不编译、运行测试、部署或启动/控制产品和游戏；尚不能报告修正后测试通过。交接所述 R157 独立审查报告路径未在本机找到，以上原因已从 R154 回执及当前源码核对。

当前冻结源码 SHA256（2026-10-30；R157 test-only 修正后，供下一轮唯一执行者绑定）：
- OptiScalerPresets.cs：49158F1C7CEC8EEA385F09805A9AC250D287CB88BC2B93E631D0ADD5A3AC9C80
- OptiScalerProfiles.Authority.cs：B768093B5FC51F79BB4D58FAFA8538E403F708CFCAEA823E002CD8629CEB3D7C
- OptiScalerProfileAuthorityTests.cs：1A3882D876AF7B11E928D187D34EB5E473EE754DECEA315AF76804391FF3319B

文件范围：
- Extensions/OptiScaler/OptiScalerProfiles.Authority.cs（新）
- Extensions/OptiScaler/OptiScalerProfiles.cs（集合入口分流）
- Extensions/OptiScaler/OptiScalerProfiles.LaunchRecovery.cs（authority 阻断保护；撤去 FG 归一候选）
- HoYoShadeHub/Features/Plugins/GameNrRuntimeService.cs
- HoYoShadeHub/Features/OptiScaler/OptiScalerPage.xaml.cs、OptiScalerPresets.cs
- Extensions.Tests/OptiScalerProfileAuthorityTests.cs（新）、Program.cs

源码根 D:\CODE\HoyoDLSS5。由 DeepSeek 后续执行：

~~~powershell
& '.\.dotnet10\dotnet.exe' run --project src/HoYoShadeHub.Extensions.Tests/HoYoShadeHub.Extensions.Tests.csproj -c Release --no-restore -- profile-authority-tests
& '.\.dotnet10\dotnet.exe' run --project src/HoYoShadeHub.Extensions.Tests/HoYoShadeHub.Extensions.Tests.csproj -c Release --no-restore -- launcher-regression-tests
& '.\.dotnet10\dotnet.exe' run --project src/HoYoShadeHub.Extensions.Tests/HoYoShadeHub.Extensions.Tests.csproj -c Release --no-restore -- exact-activation-tests
& '.\.dotnet10\dotnet.exe' build src/HoYoShadeHub/HoYoShadeHub.csproj -c Release -p:Platform=x64 --no-restore -v:q
~~~

上述测试只使用临时目录/假窗口，不启动产品或游戏；未执行不可报告通过。激活 CLI 可以与正式入口修复同次候选构建；本工作树存在其他人的修改，部署仍须单独核对差异和哈希。
