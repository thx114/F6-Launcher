# F6 Launcher 发行整改记录

## 产品与来源

产品更名为 **F6 Launcher**，thx114 独立维护。独立几何 F6 图标由本仓库脚本生成；不使用上游 Logo 输入。窗口标题、欢迎页及关于页显著标注非官方分支，保留 HoYoShade Hub 原作者哆啦D夢|DuolaD 和 Starward 的来源说明。远端仓库现名 `thx114/F6-Launcher`；1.4.8 已嵌入的旧分支仓库地址通过 GitHub 的仓库更名兼容继续可用，已匿名核验元数据与普通包下载。内部程序集和兼容入口不作为产品名称。

原始 `LICENSE` 与 `Starward_LICENSE` 原文不改；新增 `NOTICE.md`。主程序、便携外层、RPC 和 Setup 直接构建/发布也复制许可说明。普通包和 Full 包根目录及版本化应用目录逐字核验，Full 的 HoYoShade/ReShade 许可分别保留。可执行文件 ProductName 与图标进入打包门禁；持续集成检查源码默认线路、许可和可见署名。

## 下载、反馈与更新

- 关于页反馈/发行、便携版下载提示、启动器元数据和更新安装统一指向 `thx114/HoYoShadeHub-DLSS5`；原项目链接仅用于明确标识的来源致谢。
- `CloudProxyManager`、更新线路、插件选择和下载服务器 UI 移除上游私有 API，旧 ID 1..3 兼容映射为直连。自动候选为直连和独立公共 GitHub 代理，不包含上游服务。
- 下载层拒绝指向已停用上游域名的缓存 URL、嵌套代理目标和 HTTP 重定向；curl 回退不自行跟随重定向。不得把阻止列表里的域名字符串误判为实际下载入口。
- 主动更新使用本仓库完整便携更新包，元数据由校验通过的实际包和 SHA256 生成；弃用脚本不再依赖上游 diff 后端。当前发行仅有实际构建的 x64，其他架构不伪造下载元数据。
- HoYoShade 框架本身的上游组件下载和第三方许可仍按组件来源准确标识，不冒充本分支自产代码。

## 交互性能与日志

全局插件缓存优先展示；远端目录并行获取、共享刷新并节流失败重试。Opt 设置编辑与构建选择分离；NR/DLL 的扫描、内容哈希和硬链接事务后台串行执行，保留失败即停止、过期 UI 结果保护及原有游戏配置。

窗口恢复的现有日志没有 GC/清理耗时证据，无法把用户感知秒数归因于外部清理。已移除隐藏窗口时 UI 线程显式完整 GC，激活通知延后调度并记录排队/订阅者/后续耗时。详细边界见 `LAUNCHER-WINDOW-RESTORE-20261104.md`。

## 发行核验边界

正式版本 1.4.8 使用 `package-full.ps1`；普通更新包直接从同一个 Full ZIP 提取，应用内容相同。核验完整读取 ZIP、逐文件内容、原始许可字节、独立图标、禁止的活动服务地址和最终 SHA256。包不含测试安装的用户数据库、游戏配置、下载缓存、日志或测试插件。

主程序整合编译通过，既有 nullable/MVVM 警告仍存在。1.4.8 已部署至既有 test 便携目录，929 个程序文件逐一核验，8029 个受保护文件的元数据与配置哈希保留，旧 app-1.4.6.12 与根入口备份可回退。代理未运行启动器 UI、CLI 或游戏测试；用户自行实测后报告全局插件加载阻断与模块页面卡顿，已进入后续修复，不能把编译和部署视作交互验收。

## 历史发行处理

执行前保存所有旧 release ID、tag 与资产清单。普通包下载入口可用并核验后，将含旧品牌/线路的公开历史启动器发行改为草稿，保留标签、资产和恢复能力，不删除仓库历史。历史发行中附带的 F5 资产随该发行撤回公开下载，不重新上传。

按维护者最终指示，1.4.8 远端只保留普通 `F6Launcher_Portable_1.4.8_x64.zip`；MIT `LICENSE`、`Starward_LICENSE` 和 `NOTICE.md` 已在 ZIP 根目录及应用目录内。不上传 Full、F5、旧名兼容副本或独立许可/校验附件；Full 与 F5 成品保留本地。

本文件描述实现和处理策略；远端实际执行结果以 `artifacts/fork-compliance-20261103/` 的发布回执和最终远端核验为准。上游回复文本位于 `UPSTREAM-REPLY-DRAFT-F6.md`，未经用户明确要求不发送。

最终远端核验：v1.4.8 已公开，只有一个普通 ZIP 附件，大小 195520239 字节，SHA256 为 `7E1CC0B8FAED6D42E428F060734920BBA8C2B5434CA07D89EF61CED7ADDEC515`；原 35 个公开历史发行已转为草稿并逐项确认原资产 ID、名称、大小保留，未删除标签或历史资产。匿名 release 列表只显示 v1.4.8，旧 1.4.7.1 Full 下载返回 404。F5 本轮未上传，旧 F5 附件随历史发行撤回公开下载。

## Release pause requested by maintainer (2026-11-04)

The preceding publication result is a historical snapshot. v1.4.8 is now draft; its existing ordinary ZIP is retained without uploading or deleting assets. Stable and preview metadata use 0.0.0 as a no-update sentinel, clear download links, and record release_paused=true. DisableAutoUpdate alone is insufficient because existing clients also use it for framework-update UI. The historical 1.4.8 descriptor retains its version but no downloadable package. No old version is offered as a fallback. Local 1.4.8.1 remains for maintainer testing and has no published release or asset.
