# 精确单次游戏点击 CLI（2026-10-30）

## 调用形式

只有显式的 CLI 单动作可用。动作文件必须只有一个 action、一个 step；也可用同形态的 --json。坐标是目标游戏客户区的归一化比例，x/y 均为 [0,1)；必须显式提供，不允许默认值、像素坐标或越界裁剪。


~~~json
{
  "steps": [
    {
      "action": "click_game",
      "expectedPid": 43024,
      "expectedCreationFileTime": 134377430398371062,
      "x": 0.5,
      "y": 0.75
    }
  ]
}
~~~

~~~powershell
& 'D:\APPS\test\TEST\HoYoShadeHub_Portable_1.4.5.0_x64_Full\HoYoShadeHub.exe' run --biz hk4e_cn --file 'D:\absolute\single-click.json'
~~~

示例 PID/FILETIME 仅用于展示格式，不代表当前仍运行的游戏。必须从本次目标进程重新取得精确身份；创建时间用 Windows FILETIME 十进制整数，不用 Unix 时间、毫秒或浮点数。

## 执行边界

- 仅接受 hk4e_cn、单动作单步骤 click_game，其他业务、click_window 别名、混合 launch/stop/click、重复点击均拒绝。覆盖包 auto.json 和 UI 动作无法执行此点击。现存 test_game 内部点击也不能绕过此校验。
- 参数在请求 UAC 前校验。管理员 CLI 才可执行；非管理员通过既有 UAC 输出接力提权后重新校验。没有游戏启动或关闭副作用。
- 一次 OpenProcess 持有目标 PID 的句柄，验证未退出、精确创建 FILETIME、GameCatalog 中的完整游戏 EXE 路径。无法验证则拒绝，不按游戏名回退查找。
- 只接受**已经位于前台**的可见顶层窗口，并要求窗口 PID 与句柄绑定 PID 一致。不自动激活窗口。读取客户区坐标，变换为屏幕坐标；执行前再次检查进程身份、窗口身份和焦点。
- 一次 SendInput 发送鼠标按下与松开；仅发送了按下时尝试松开避免按钮卡住。输出和 Hub 日志记录 PID、FILETIME、客户区与屏幕坐标以及成败。操作系统焦点在最后检查与输入之间仍可能变化；CLI 成功只证明发送成功，不证明游戏消费了点击。
- CLI 成功退出码 0，拒绝或动作失败返回 1；具体原因看 stdout/stderr 和便携包根 log 目录的当日日志。不会自动重试、连续点击、登录或处理验证码。

## 验证与部署

- 源码：src/HoYoShadeHub.Extensions/Games/ExactGameClick.cs、src/HoYoShadeHub/Program.cs、src/HoYoShadeHub/Features/Plugins/LauncherActionRunner.cs。
- 回归：src/HoYoShadeHub.Extensions.Tests/ExactGameClickTests.cs；命令 dotnet run --project src/HoYoShadeHub.Extensions.Tests/HoYoShadeHub.Extensions.Tests.csproj -c Release --no-restore -- exact-click-tests。仅假进程/假窗口和本进程错误 FILETIME 拒绝路径，不发送真实点击。
- 日志：artifacts/exact-click-tests-20261030.log、artifacts/exact-click-build-20261030.log。
- 2026-10-30 源码 Release/x64 构建通过：309 个既有警告、0 错误；22 项精确点击回归通过。未启动游戏或真实点击。
- 2026-10-30 13:50:19（北京时间）已在当时无 Hub/游戏进程的安全窗口部署至 test 的 app-1.4.6.12。仅替换 HoYoShadeHub.dll（CE34F3D9C1F7050CA86EE7EE964E5413A7D1B57729D8C225F43F4873F491D93D）和 HoYoShadeHub.Extensions.dll（C07A90C8248C010FB1DFAFBA9327131B165E9526B70A4D7CA279A61839751283）；version.ini、EXE、deps/runtimeconfig 均未改变。回执：artifacts/exact-click-deploy-20261030-135018/receipt.json；回滚备份：test 根下 _backup-exact-click-20261030-135018。
- 随后收到 F5 新一轮 BeforeNR 游戏运行通知，安全窗口已结束。本次部署发生在该通知之前；收到通知后不再触碰 test 文件。未进行任何游戏窗口实测或真实点击。F5 唯一 CLI owner 决定何时调用，须先确认当局目标 PID/FILETIME 和前台身份。
