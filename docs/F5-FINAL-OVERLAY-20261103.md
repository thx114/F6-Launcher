# F5 三游戏覆盖包交付（2026-11-03）

已按用户最后确认制作冻结 F5 覆盖包；三游戏均选择 `official/DLSS F5`。

## 当前修订：仅覆盖包 F5 v4.2，兼容启动器 1.4.7.1

用户要求只修改覆盖包；不发布新启动器版本。v4.1 只启用 Opt，没有关闭其他组件；`set_opt_config(if_missing:true)` 在已有 profile 时不选择或应用配置。

当前交付为 `build/release/[原铁绝]F5_v4.2.zip`。使用现有 1.4.7.1 启动器，重新导入 ZIP 并确认动作即可。三个游戏顺序统一为：

`require_launcher_version(1.4.7.1)` → 六个 `set_launch_option(enabled:false)` → `set_opt_build(official/DLSS F5)` → `import_opt_config` → `set_opt_config(if_missing:true)` → `set_opt_config` → `set_opt_nr(file+sha256)`。

六个现有启动项为 `useXxmiInject`、`useModules`、`useHoYoShade`、`useOpenHoYoShade`、`useFpsUnlock`、`useOptiScaler`。随后 `set_opt_build` 重新启用 Opt；只修改正在应用动作的游戏。

现有 1.4.7.1 动作没有 Rocket、Starward、Blender、跳过 HoYoShade 注入器的写入接口；只改覆盖包不能清空这些项，若已勾选需手动取消。本包不包含未知新动作，不写数据库或启动器二进制。

配置先按旧接口初始化缺失 main/profile，再通过普通 `set_opt_config` 强制应用包内对应游戏的 F5 配置，并保存下拉选择及 follow 绑定；已有 profile 也会切换。重新导入开启新的 once 批次，日常启动不重复覆盖后续设置。

v4.2 仅修改动作及交付元数据，所有冻结 Opt/NR DLL 和包内 INI 的逐项 SHA256 保持 v4.1 内容不变。本轮只核验打包文件，运行、UI 和游戏测试由用户执行。下面的 v4.1 说明与历史验证记录不代表 v4.2 运行验收。

| 游戏 | NR 版本 | DLL SHA256 |
|---|---|---|
| 原神 `hk4e*` | 原神v4_预设A_1080p5000 | `094D2B0E28EEB65A4250F99EEE8230B1BC9BEFCB459331E569DDB76B26BCC6CB` |
| 崩铁 `hkrpg*` | 310.9.16-SF | `CDFA6C1EC784867182164C7836F324F087B5C9F374D0C9FE46F54999FEABF3ED` |
| 绝区零 `nap*` | 310.9.16-SF | 同上 |

SF 已对照制作时远端 `main:catalog/dll-fingerprints.json` 的最新收录；没有把 SF-lora7 或其他自定义 DLL 冒认为标准 SF。

## 历史 v4.1 交付与安装顺序

1. 推荐先升级启动器至 **1.4.7.1**。新安装使用 `HoYoShadeHub_Portable_1.4.7.1_x64_Full.zip`，其中包含 HoYoShade 框架；现有安装可通过启动器检查更新使用普通便携更新包。1.4.7.1 修复首次导入时丢失 NR 子目录的问题，并支持包的最低版本检查，不再应用旧增量 ZIP。
2. 游戏覆盖包：`D:\CODE\HoyoDLSS5\build\release\[原铁绝]F5_v4.1.zip`。将原始 ZIP 导入全局插件页；再到对应游戏插件页确认一次性动作。三款游戏都先执行 `require_launcher_version(minVersion: "1.4.7.1")`。此前已经导入失败的安装也需重新导入新 ZIP，程序升级本身不会恢复被旧版删除的 NR 子目录。

本次未应用到正在使用的启动器，未启动或停止游戏，未更改真实用户数据库、profile 或画质设置。

2026-11-03 后续修订：用户要求自行测试，因此版本门槛新增后的 UI、安装、动作与游戏验证由用户执行；构建和 ZIP 内容校验不视为该轮运行验收。F5 v4.1 只更新动作及交付元数据，冻结 Opt/NR DLL 与游戏配置不变。下表 SHA256 为原 Final 历史记录；v4.1 的校验值以相邻 `.sha256` 与 `.verification.json` 为准。

`D:\CODE\HoyoDLSS5\build\release\HoYoShadeHub_F5OfflineNr_20261103_update.zip` 仅保留为历史 `app-1.4.6.12` 基线的专用增量，不适用于 1.4.7；不要与 1.4.7 重复安装，也不要作为游戏覆盖包导入。

| 交付 | 字节数 | ZIP SHA256 |
|---|---:|---|
| F5 三游戏覆盖包 | 989518202 | `01C1995E99C4DA16FB87AD4D5BFB4B2F44E559F20F3301C9A555197E97277CC1` |
| 历史 app-1.4.6.12 专用增量（1.4.7 不需要） | 2294437 | `26AF58651BF2C4B453C8EA94A1EF1132D552739617BC955B97FCC9385C4BED28` |

## 历史 v4.1 配置与内容边界

冻结目录：`D:\APPS\test\TEST\HoYoShadeHub_Portable_1.4.5.0_x64_Full\OptiScaler\official\DLSS F5`。主 DLL 为 33359360 字节，LastWriteTimeUtc `2026-11-03T02:15:49.8266061Z`，SHA256 `5986BC7377A34A5451ED14DC0CE481A20F30F8808B966ADC47583CF845986BB3`。

运行依赖使用根白名单及 OptiScaler/plugins/licenses 目录白名单，保留两套各自独立的 Streamline。排除 F6、备份、捕获、日志、转储、`.pending`、过期 redist manifest 和旧 README。plugins 当前为空，没有额外补入 ASI。

包内 59 个条目，所有 NR DLL 随包提供。共享默认 NR 为 SF；原神动作选择 v4。每个游戏单独包含 NR 文件与 `auto.json`，以包内相对路径、SHA256、x64 PE、已知指纹验证后归档。运行时使用严格硬链接。v4.1 的动作顺序为 `require_launcher_version(minVersion: "1.4.7.1")` → `set_opt_build` → `import_opt_config` → `set_opt_config(if_missing:true)` → `set_opt_nr(file+sha256)`。配置动作按 once 执行；最低版本作为前置条件每次重查。

不向官方目录直接覆盖 root INI 或 profiles。已有游戏 profile 和对应 preset/follow 绑定保留，仅缺失 profile 才从对应 F5 便携配置初始化。便携转换只处理日志路径、Plugins Path 和 OptiDllPath；其余质量、倍率、方向参数沿用冻结配置。全局 Opt 选择不改变。

## 历史验证证据

- Release 构建实际 exit 0；最终增量构建 0 错误、89 警告。
- 游戏 ZIP CRC、条目集合、逐条 SHA256、源内容未变化校验通过；程序更新 14 个变更文件逐条校验。
- 用现有 `LocalPackageInstaller` 实际导入最终 ZIP，然后在隔离便携目录运行原始 PackRoot 相对路径动作及 once/consent 流程，验证 `hk4e_cn`、`hkrpg_bilibili`、`nap_cn` 的构建选择、NR 内容、独立 profile 投影和真实硬链接。
- 三款游戏重复执行通过 once 账本；已有原神配置字节保留；全新缺失 main/profile 初始化通过。
- 坏哈希、同名内容冲突、无效 PE、PackRoot 外绝对路径、父路径越界、祖先 junction、归档失败均拒绝；选择保留，没有复制 fallback。
- 原有两游戏 CLI 选择、默认恢复、失败不改选择的 11 项回归通过。
- 程序更新叠加到既有安装基线后，975 个程序文件的内容与实际测试构建一致。

回执：`artifacts/f5-final-validation-receipt-20261103.json`、`artifacts/f5-launcher-update-reconstruction-20261103.json`；日志：`artifacts/f5-final-isolation-tests-20261103.log`、`artifacts/f5-nr-pack-path-tests-20261103.log`、`artifacts/f5-offline-nr-regression-20261103.log`。ZIP 旁另有 `.sha256` 与 `.verification.json`。

清理状态：自动审批以 `blocked by policy` 拒绝删除三个已核对的生成测试目录，因此未继续删除或改用其他方式绕过。保留 `artifacts/f5-final-validation-d38fbdb7`、`artifacts/f5-final-validation-fba91098` 与 `artifacts/opt-nr-pack-cli-20261103-5d6360ef`；这不影响交付 ZIP。覆盖包安装器自己的临时解包目录已由其正常 finally 清理。

这些是包内容、真实导入和隔离动作的证据；没有据此宣称三游戏本轮视觉、深度、NR/FG 或性能已验收。F5 游戏包尚未上传远端；此前原神 v4 NR 独立下载发布不变。
