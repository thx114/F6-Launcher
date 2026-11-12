# NR 选择器闪退修复（2026-11-09）

## 现场证据及归因边界

- 最新启动器日志 `HoYoShadeHub_261109.log`：12:36:28 进入原神插件页，12:36:31 出现 `COMException 0x80070490`（找不到元素）。无可用托管调用栈。
- Windows Application 事件 1000：12:36:32，PID 126736，故障模块 `Microsoft.UI.Xaml.dll`，异常码 `0xc000027b`；实际运行目录是 `app-1.4.8.2-launch-sync-20261109`。当时尚未重启到先前部署的 addon-progress 构建。
- 原 `NrVersionPicker.Fill` 在 DropDownOpened 发起读取，完成后立即 Clear / Add 所有 ComboBoxItem；没有检查弹出层是否还在打开或处理点击。
- 原插件页在 SelectionChanged 中立即禁用控件；切换 NR 成功后调用 LoadGame，清空并重建全部 Addons，销毁当前选择器。Opt 页也有相同的无条件重建选项逻辑。
- 上述控件时序与 UI 层错误相符，但日志缺少原生堆栈，此任务没有声称已在 UI 中复现或独立证明唯一根因。

## 修改

- 新增纯托管 `DeferredPickerUpdate<T>`，保留待绑定快照，打开期间不消费；用户选择、新请求和卸载会使旧结果失效。
- NR 选择器统一以低优先级 dispatcher 回调发布快照，弹出层打开时保留到 DropDownClosed 后再次排队。版本、可用状态和选择都不变时不重绑。
- 完整构建新选项集合后一次绑定 ItemsSource，避免逐项 Clear / Add；程序绑定保留 Tag 抑制，不误触发文件事务。
- SelectionChanged 先让出当前 UI 回调，在后续 dispatcher 回调关闭弹出层，再禁用控件 / 开始后台 IO。
- 插件页切换成功只更新 NR 选择器，保留 Addons 集合、卡片展开及焦点。忙碌时的回退也排到当前点击之后。
- Opt 页复用相同绑定逻辑，忙碌状态有操作归属，旧回调不会清除另一个操作的 busy 状态。
- 保留 SHA256 内容校验、严格 NR 硬链接、按游戏设置、失败回滚及现有 NR 参数；增加选择开始 / 完成日志。

## 验证

- UI runtime：44 PASS，其中新增 15 项快照调度检查；不含真实 WinUI 验收。
- NR selection：28 PASS，含硬链接和内容识别回归。
- Opt page loading：10 PASS。原有源码契约使用过期方法签名/边界，导致检查本身越界；已按当前 async handler、快照参数和回调抑制方式更新，文件读写行为检查保持。
- 发布和部署结果以 `artifacts/nr-picker-crash-20261109/` 的 build.log、build-inputs.json、deployment.json 为准。保持 1.4.8.2，不发布 GitHub，不打 Full 包；用户负责真实 UI / 游戏验收。

## 部署结果

- Release publish 退出码 0。927 个文件在移动前后逐文件核对 SHA256，再原子更新 version.ini。
- 当前入口：`exe_path=app-1.4.8.2-nr-picker-20261109\HoYoShadeHub.exe`。
- 构建输入无漂移，HEAD/index 保留；根入口、配置及 ReShade 保护文件未变。未停止运行中的启动器或游戏。
- 同时包含此前 addon 拖入进度修复；需完全退出启动器（含托盘），再打开根目录 F6Launcher.exe。未做真实 UI 或游戏测试。
