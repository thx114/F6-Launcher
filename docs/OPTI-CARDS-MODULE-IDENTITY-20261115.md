# Opti 卡片和手动 DLL 识别（2026-11-15）

测试版本继续使用 1.4.8.2。实际界面和游戏由用户验收。

## 手动 DLL 的身份

原 `ModuleRegistry.List()` 对手动 DLL 按 `dllHint` 文件名取目录里的第一项。
`catalog/modules.json` 中 DLSS-NR on AMD、DLSSG for SM86、MFG Unlock、DLSS Enabler 均使用
`version.dll`，因此无法从这个名称判断产品。原下载列表还会因此同时隐藏这些来源。

现在由可独立验证的 `ModuleDllIdentity` 统一处理文件名提示：通用代理名不提供产品身份，
其他名称也必须只对应一个目录 ID。未识别的手动 DLL 使用原文件名；通过指定来源安装的模块仍使用
其来源定义。展示与下载去重共用 `ModuleEntry.CatalogId`；手动条目的 key、游戏选择和注入路径不变。
识别不读取或计算整个 DLL 的哈希，不增加页面 I/O。

## 界面

- Opti 的 DLL 路径与标题放在同一行，去掉“注入：”；空间不足时省略，悬停显示完整路径。
- NR 版本选择框移动到 Opti 版本选择框右侧。
- 已存在配置只显示修改时间，去掉“当前文件：profiles\\<游戏>.ini”。
- Rocket 联动设置说明替换为“填入Rocket的config.ini的位置，以便启动器填入对应项目”。
- 删除准备 Rocket 后弹出的第三方模组加载器对话框；配置准备、成功状态和错误提示保留。

## 验证与部署

本轮回执目录为 `artifacts/opticards-20261115`，独立构建目录为
`C:/Users/thx11/AppData/Local/Temp/F6Launcher-opticards-20261115`。
选定回归包含模块身份、Opti 页面加载、UI 生命周期、NR 选择、Rocket INI 联动与独立 Opti 启动。
部署使用新的旁路应用目录，校验逐文件 SHA256 后切换 `version.ini`；用户退出当前进程再打开后生效。

最终 Release/x64 publish 成功，六组回归均通过，其中模块身份检查 17 项、Rocket 联动检查 129 项。
已部署至测试目录 `app-1.4.8.2-opticards-20261115`，922 个文件逐项核对，914 个复用已有内容，
仅复制 21,506,712 bytes。入口、GUI 哈希、旧 GUI 内容以及工作区 HEAD/index 再次核对一致。
没有启动或停止真实 GUI/游戏进程，没有发布 GitHub 或生成 Full 包。
