# 启动时清理旧版 auto.json（2026-10-26）

- 入口：Program.Main，在 CLI/协议命令分流及主实例互斥检查之后、WinUI 和窗口创建之前调用 PackAutoStartupCleanup.DeleteExisting。
- 每次主 GUI 实例启动仅清理一次；第二实例激活、CLI、切页、游戏启动、覆盖包安装不调用。没有后台监视或定时清理。
- 范围：当前 CacheRoot/games（含 .pending-gamepacks）、UserDataFolder/GamePack、旧布局 UserDataFolder/.hysx/cache/games。只删除准确文件名 auto.json，不跟随目录或文件重解析点，不删除 ini_config.json、预设、安装包和 once 账本。
- 启动后导入覆盖包保留原安装、动作确认和 once 执行流程；新写入的 auto.json 在该运行会话内保留。下次重新启动时按用户要求再次清理。
- 无法删除时记录错误并停止本次主界面启动，避免旧动作仍然可以触发。
- 针对性测试：artifacts/auto-startup-tests-20261026.log，6 项通过。覆盖旧包及 pending 清理、其他配置保留、安装源保留、启动后导入保留、锁定文件明确失败、缺目录。
- 覆盖包安装回归：artifacts/auto-startup-package-tests-20261026.log，通过；重解析点夹具因权限不足跳过。
- 未启动启动器或游戏。test 存在运行进程，本次不关闭用户启动器、不热替换运行文件；构建产物及部署脚本待退出后使用。
Release 构建：308 警告、0 错误。主 DLL SHA256：0EDAB8DC47F435BDBF43AE4C133A4ADAC78136868AF4AEBF960910AFE4A6984C。部署脚本：artifacts/Deploy-AutoStartupCleanupTest-20261026.ps1（带运行检查、备份及逐文件哈希校验）。
