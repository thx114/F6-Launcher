# Opti 下载与导入 NR 整理

版本沿用 1.4.8.2；没有执行游戏、发布 GitHub 或生成 Full 包。

## 下载修复

`janblade-f5` 目录原规则 `OptiScaler-DLSSNR-F5-*.zip` 不匹配用户选择的 `v0.1.37.2-Optiscaler-F5.zip`。目录改为 `*OptiScaler*F5*.zip`，兼容新旧主包命名，排除该版两个 Optional 组件包。

已有远端目录缓存可能仍是旧规则。下载器保留“实际可用文件”与“规则是否匹配”两个结果；规则过期时 UI 强制打开明确的文件选择，并提示 Optional / Patch 为可选组件。非交互自动安装不会在规则失配后猜选其他文件。

此前 UI 提前显示“正在下载”，读取资产清单却未传入任务取消令牌，且空清单返回后全局状态未更新。现在分别显示读取清单、连接服务器、字节进度和 NR 整理；读取清单限时 20 秒并可取消，错误同时写入卡片和全局状态。记录请求仓库、tag、线路和选择文件到日志。

`DownloadService` 增加响应头等待上限 30 秒、正文无数据上限 60 秒。每次实际读取单独计时，主动暂停等待不计入读取超时，用户取消保持取消语义。失败保留已有最终文件，部分下载仍按既有 ETag/Range 规则处理。

## NR 整理范围

此前每次本地 Opti/模块导入与 Opti 在线安装完成后，递归整理 Addons、整个 Opti 库和整个缓存。检查时测试安装含 5 个 Opti NR 文件和 59 个缓存 NR 路径；这说明原扫描范围很大，不能据此声称每次节省了固定秒数。

独立 Opti/模块现在只整理本次安装的 `TargetPath`，在线 Opti 只整理新构建的 `build.Directory`。已有构建和各游戏 NR 选择不受影响。显式全库整理及多目标覆盖包仍保留完整范围。

共享整理实现排除重复和包含的根目录、排除重解析点、显示文件计数，并在枚举及 SHA256 分块读取时响应取消。每次涉及的文件仍计算完整 SHA256 并验证内容寻址库，NR 硬链接失败不降级为复制；没有使用跨调用的大小/时间缓存代替内容校验。

## 验证证据

- `opti-download-tests`：资产新旧规则、旧缓存失配时显式选文件、禁止默认猜包、metadata 取消、响应头/正文超时、用户取消、NR 有限范围与重复根、实际本地导入和硬链接校验。
- 既有 `download-tests`、`package-install-tests`、`nr-selection-tests` 和 `pack-permission-tests` 纳入本轮构建回归。
- 显式运行 `opti-download-probe`，通过真实 `OptiScalerDownloader` + `DownloadService` 下载、解压 `v0.1.37.2` 主 ZIP。下载传输 146070006 字节，整个过程 16319 ms，解压内容 253103859 字节，识别主 `OptiScaler.dll` SHA256 为 `43CA640914B2B75A8844833C80E6E8FAC883C9A1283E26E79432217C7F105B9C`。隔离目录中的 DLL 从未加载。
- 证据：`artifacts/opti-download-nr-20261110`。临时载荷删除被自动审批拒绝，已转移至 `D:\DATA\Documents\ChatGPT\DLSS F5 NEXT\cleanup\HoyoDLSS5-opti-download-probe-20261110` 待手动删除；同盘移动没有释放磁盘空间。真实 UI 和游戏效果由用户实测。
