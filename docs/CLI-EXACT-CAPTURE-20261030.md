# 精确单帧游戏画面 CLI（2026-10-30）

只允许显式 CLI 的单动作、单步骤 capture_game。调用者先确认当前原神进程的 PID 与创建 Windows FILETIME；不接受按游戏名模糊匹配。示例值不可用于真实调用：

~~~json
{"steps":[{"action":"capture_game","expectedPid":43024,"expectedCreationFileTime":134377430398371062}]}
~~~

~~~powershell
& 'D:\APPS\test\TEST\HoYoShadeHub_Portable_1.4.5.0_x64_Full\HoYoShadeHub.exe' run --biz hk4e_cn --file 'D:\absolute\single-capture.json'
~~~

仅接受 hk4e_cn、整数 PID/FILETIME 和单步骤。覆盖包 auto.json、其他游戏、混合/重复动作不能调用。先用进程句柄验证 PID、创建时间、完整游戏 EXE 路径、未退出状态；只截取已在前台的同 PID 可见顶层窗口，不改变焦点、光标或游戏输入。捕获完成后重新验证同一进程、窗口和客户区大小；失败不落图。

使用 Windows Graphics Capture 获取一帧 PNG，窗口边框存在时裁到客户区；无法可靠确定客户区裁剪时拒绝。最多等待五秒。自动文件名写入启动器 log\cli-captures，CLI 返回绝对 PNG 路径、真实 PNG 尺寸和 SHA256。不能由调用者指定输出路径或覆盖文件。游戏可能返回黑帧；CLI 成功不等于已理解画面，更不代表任何点击已执行。

源码：ExactGameCapture.cs、ExactGameFrameCapture.cs、Program.cs、LauncherActionRunner.cs。离线假窗口测试：exact-capture-tests，覆盖业务/批次拒绝、PID 复用、EXE 路径失配、前台变更、进程退出、只写一次 PNG。测试不启动游戏，不操作真实窗口。真实画面观测与任何 click_game 的时机、坐标，均由 F5 唯一运行 owner 决定。

本轮 Release/x64 构建通过（307 个既有警告、0 错误）；17 项假窗口捕获回归与原有 22 项精确点击回归通过。构建记录：artifacts/exact-capture-build-20261030.log。没有将本功能部署到 test，也没有真实游戏截图验证。部署需要先确认当时没有 Hub/游戏占用，按精确点击流程仅替换必要的两个程序集，并在替换前备份及校验 SHA256。
## 部署记录（V3-R143，2026-10-30）

指定 Release/x64 `--no-restore` 构建实际退出码 0（307 个警告、0 个错误）。在确认无 HoYoShadeHub、YuanShen、PresentMon 进程且目标 DLL 可独占写入后，于 **2026-10-30 15:59:39 北京时间**仅部署 `HoYoShadeHub.dll` 与 `HoYoShadeHub.Extensions.dll` 至 test `app-1.4.6.12`；`version.ini` 保持 `exe_path=app-1.4.6.12\HoYoShadeHub.exe`。

- `HoYoShadeHub.dll`：源/目标 LastWriteTimeUtc `2026-10-30T07:58:18.6211712Z`，SHA256 `4F1192CCC38B9DD017769F1BF2786DD50C583605EF750EACF08B6C52703AFABD`。
- `HoYoShadeHub.Extensions.dll`：源/目标 LastWriteTimeUtc `2026-10-30T07:57:37.4073256Z`，SHA256 `D5075363482E9FAD953814EF11268D4FB6AE98E63FDEC7039B8492375BA08D7E`。
- 回滚备份：`D:\APPS\test\TEST\HoYoShadeHub_Portable_1.4.5.0_x64_Full\_backup-exact-capture-20261030-155939`；机器回执为同目录 `deployment-receipt.json`。未改配置或 F5 产品文件，未启动游戏/Hub，未执行真实截图、世界或视觉测试。

调用仍只允许单动作单步骤，并将调用者从目标游戏进程重新取得的精确 PID 与创建 Windows FILETIME 写入绝对路径 JSON：

~~~powershell
& 'D:\APPS\test\TEST\HoYoShadeHub_Portable_1.4.5.0_x64_Full\HoYoShadeHub.exe' run --biz hk4e_cn --file 'D:\absolute\single-capture.json'
~~~
