# 覆盖包与 CLI 按游戏选择 OptiScaler NR

## 已有能力与本次补齐

现有 test 安装版已支持在游戏 OptiScaler 卡片的 `NR 版本` 下拉框选择独立 NR。每个游戏的持久设置是 `nr_version_opti_<GameBiz>`；同一 Opt 构建在 `cache/games/<GameBiz>/OptiRuntime/<构建 ID 哈希>/` 下分别准备加载目录。NR 按内容归档并硬链接，不把不同游戏的选择写回共享 Opt 构建。

本次补齐覆盖包 `auto.json` 和 `run` CLI 的 `set_opt_nr` 动作，别名为 `set_optiscaler_nr`。旧版 `switch_dll` 面向插件目录，不能代替这个动作。

## 用法

先安装目标 Opt 构建，再通过现有 `set_opt_build` 选给当前游戏。随后执行：

```json
{
  "steps": [
    { "action": "set_opt_build", "build": "official/DLSS F5" },
    { "action": "set_opt_nr", "version": "原神v4_预设A_1080p5000" }
  ]
}
```

`version` 必须是明确的版本名。默认优先使用本地归档；缺失时从现有组件目录下载到版本库，再为当前游戏建立 NR 硬链接。下载不覆盖共享插件或共享构建的活动 DLL。离线部署使用 `"download": false`，版本不存在即失败。

恢复构建自带 NR：

```json
{ "steps": [{ "action": "set_opt_nr", "default": true }] }
```

`default: true` 不得与非空 `version` 同时使用；省略两者会失败，不隐式重置原选择。

CLI 通过 `run --biz hk4e_cn --file <绝对路径.json>` 指定原神；通过 `run --biz hkrpg_cn --file <另一个绝对路径.json>` 为崩铁选择另一版本。覆盖包动作沿用所应用游戏的上下文，各游戏可使用不同动作内容；该步骤不修改其他游戏的选择，也不隐式启用 Opt 启动项。`set_opt_build` 的已有行为仍会启用该游戏 Opt。

覆盖包沿用已有动作同意与 once 机制。新安装应先选构建、导入和应用 Opt 配置，再设置 NR，确保 NR 隔离运行目录准备时已有正式配置。成功后重启目标游戏生效；不会热替换已加载模块。

## 验证与交付

- 对当前 test 安装目录实际 DLL 的 NR 隔离回归：20 项通过。
- 新增动作的实际 Release CLI 在独立便携目录验证：两个游戏分别选择不同 NR、归档硬链接、切换互不干扰、共享构建不变、恢复默认、缺失版本与无效参数失败、持久化及两份官方 profile 保留，均通过。
- 测试使用仅供文件操作的模拟 DLL，从不加载或注入它们；未启动游戏、未修改用户数据库或游戏配置。
- 构建命令：`C:\Users\thx11\.dotnet10\dotnet.exe build src/HoYoShadeHub/HoYoShadeHub.csproj -c Release -p:Platform=x64 -p:Version=1.4.6.14 --no-restore`。
- 构建日志：`artifacts/opt-nr-pack-build-20261103.log`；CLI 测试脚本：`artifacts/Test-OptNrPackCli-20261103.py`；回执日志：`artifacts/opt-nr-pack-cli-tests-20261103.log`。
- 启动器更新 ZIP 是针对当前 test `app-1.4.6.12` 布局的程序覆盖更新；不是包含全部依赖的 Full 便携包，也不是游戏插件包。用户退出启动器后可解压到便携根目录；保留数据库和游戏配置。

发布 NR 包与源码开发不等于游戏加载或画质/性能验收。新增动作已编译并通过独立 CLI 验证；用户正在运行的启动器不会自动获得新动作。

## 2026-11-03 有界源码补充：离线 NR 与首次配置初始化

本节为新增源码 contract；上节历史构建与 CLI 验证记录不覆盖本节改动。本轮未构建、未运行测试、未部署或操作游戏，由主线程负责打包和测试。仅封装已有 F5；未完成的 F5 特性留在 F6。

### `set_opt_nr` 包内文件分支

```json
{
  "action": "set_opt_nr",
  "version": "原神v4_预设A_1080p5000",
  "file": "nr/nvngx_dlssnr.dll",
  "sha256": "<必须替换为该文件真实的64位十六进制SHA256>",
  "download": false,
  "once": true
}
```

上例 SHA256 为说明占位符，不能直接执行。`file` 相对各游戏的动作 `PackRoot`；CLI 没有 `PackRoot` 时必须给绝对路径。

- 出现 `file` 或 `sha256` 字段即进入离线分支，包括空值、错误类型；必须同时提供非空 `file`、明确 `version`、完整 64 位 ASCII 十六进制 `sha256`。不能与 `default:true` 同用。错误直接失败，无论 `download` 值如何都不查远端或回退下载。
- 文件 basename 只能是 `nvngx_dlssnr.dll`（大小写不敏感），不接受 ZIP、其他 DLL 或重命名候选。
- NR 动作内继续调用共用 `ResolvePackFile`，并独立约束：`PackRoot` 非空时用 `GetRelativePath` 拒绝越界路径或不同卷；即使输入为绝对路径也必须位于包内。源文件及所有祖先不得是重解析点。共用解析器和其他动作未修改。
- `version` 去除首尾空白后必须与 `OptiScalerLibrary.Sanitize(version)` 完全一致，拒绝目录遍历与清理后别名。
- 先验证内容 SHA256、PE32+、AMD64、DLL 标记；有匹配内容指纹时校验其 PE 元数据，有该版本的已知 NR 指纹时必须匹配。只使用已加载的本地指纹快照，不刷新网络。
- 归档到 `DllVersionStore` 的 `dlssnr/<version>/nvngx_dlssnr.dll`。已有相同标签必须先通过相同 SHA/PE/指纹检查，异内容拒绝且不调用 Archive 覆盖；相同内容可复用。新版本调用 `Archive`，不自行复制兜底。
- 归档后重新读取版本文件，验证内容，再调用 `NrRuntimeLibrary.VersionFile` 并重新验证返回文件的 SHA/PE/指纹和归档硬链接关系。只有全部成功、上下文仍有效、`GameNrRuntimeService.SetOptiVersion` 准备成功后才持久保存当前游戏 NR 选择。失败可能留下已归档内容，但不保存新的游戏选择。
- 不带 `file/sha256` 的已有版本下载/本地选择及 `default:true` 用法保留；覆盖包必须显式携带上述文件字段，不能依赖本地已缓存版本来替代包内文件。

已确认的包配置映射：原神用 v4 A1080p5000，崩铁和绝区零用 `310.9.16-SF`。具体文件内容与真实 SHA256 由主线程确认并写入动作；不按文件大小猜版本。

### `set_opt_config` 的 `if_missing:true`

```json
{ "action": "set_opt_config", "name": "F5 v3 原神", "if_missing": true, "once": true }
```

- 按当前游戏 `GetSelectedOptiScalerId` 在 OptiScaler 库中查找原始构建 DLL 所在目录；不使用隔离 `OptiRuntime`，不回退另一个全局构建。
- 若 `OptiScalerProfiles.ProfilePath(buildDir, gameKey)` 已存在，返回成功 skip；不 Apply、不激活、不创建 main、不写 preset/follow 设置。
- profile 缺失时按已有 Apply 流程初始化。若 root `OptiScaler.ini` 同时缺失，先 `OptiScalerPresets.Read(name)`，用 `FileMode.CreateNew`、UTF-8 无 BOM 初始化 main；创建冲突只保留已存在文件，其他写入错误返回失败。bootstrap 不覆盖已有 main；随后 Apply 仍按既有语义写 profile 并激活 main。
- 本分支仅在 Apply 成功后保存 preset/follow 设置。省略或关闭 `if_missing` 时保持原有 `set_opt_config` 解析、写设置及 Apply 语义，不额外 bootstrap。
- 包内完全不包含运行时 root `OptiScaler.ini` 和 `profiles/`，防止 OverlayTree 覆盖用户配置及破坏 runtime baseline。配置通过各游戏 GamePack 内 INI 导入为预设，首次动作顺序为 `set_opt_build` → `import_opt_config` → `set_opt_config(if_missing:true)` → `set_opt_nr(file+sha256)`；保留主线程已有启用动作和 once 约定。

主线程应覆盖坏哈希、错误架构/非 DLL、包路径越界/重解析点、归档同名异哈希、归档失败保持原 NR 选择，以及 profile 已有/缺失与 main 已有/缺失的初始化组合。本轮不执行这些验证。
