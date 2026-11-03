# F6 Launcher 完整包构建

本文记录仓库内 Full 便携包的正式构建方式。启动器和应用必须由仓库脚本构建；不要手工复制旧的 `HoYoShadeHub.exe` 或 `app-*` 目录来代替构建。

## 构建链

入口脚本是仓库根目录的 `package-full.ps1`。它会先调用 `package.ps1 -SkipZip`，由该脚本使用 `dotnet publish` 发布主应用和外层单文件启动器，然后把 HoYoShade 框架及必要资源加入输出目录，最后创建 Full ZIP。基础发布不再额外压缩普通便携 ZIP。

Full ZIP 的底层入口是 `package-full.ps1`；`package.ps1` 只生成普通便携包。`publish.ps1` 是本地发布准备入口，调用 Full 脚本后从同一个 Full ZIP 提取普通更新包、生成本分支元数据并检查许可、图标和网络策略，不自动上传 GitHub。`build.ps1` 仅保留为普通便携目录构建的兼容入口。

当前远端发布规则：GitHub 只上传普通便携 ZIP；Full 成品仅留在本地，不上传 F5 覆盖包、兼容命名副本、独立许可文件或校验附件。两种本地成品仍需通过许可和内容校验，普通 ZIP 包内必须保留原始 MIT `LICENSE`、`Starward_LICENSE` 和 `NOTICE.md`。

## 前置条件

- 在仓库根目录运行 PowerShell。
- 使用符合仓库 `global.json` 的 .NET 10 SDK（`10.0.101`，允许 latestFeature）。`package.ps1` 会自动查找 SDK；本机复用 `C:\Users\thx11\.dotnet10\dotnet.exe`，不要为打包复制新的 SDK。
- HoYoShade 源目录必须包含 `ReShade64.dll`。默认探测 `HYSHADE_SHADE_SOURCE`、仓库内 `HoYoShade`、`D:\APPS\HoYoShadeHub\HoYoShade`；也可以通过 `-ShadeSource` 明确指定。
- 不要把用户状态目录、日志、插件或截图当作框架源文件的一部分。Full 脚本会按其实现筛选框架内容，并在输出包根写入 `.portable` 和 `portable-layout.json`。

## 构建命令

在 PowerShell 中：

```powershell
Set-Location 'D:\CODE\HoyoDLSS5'
$env:HYSHADE_DOTNET = 'C:\Users\thx11\.dotnet10\dotnet.exe'

# 使用已核对存在 ReShade64.dll 的本机 HoYoShade 源目录
.\package-full.ps1 `
    -Version '1.4.8' `
    -Architecture x64 `
    -Configuration Release `
    -ShadeSource 'D:\APPS\test\TEST\HoYoShadeHub_Portable_1.4.5.0_x64_Full\HoYoShade' `
    -DisabledAddons ''
```

发布新版本时只需把 `-Version` 改为目标版本。`-DisabledAddons ''` 会清空随包 `ReShade.ini` 里的本机插件禁用状态，避免把构建机的开关状态预置给用户。Full ZIP 默认使用 `build/full-<版本>/HoYoShadeHub` 作为暂存目录，输出到：

```text
<ReleaseDirectory>/F6Launcher_Portable_<版本>_x64_Full.zip
```

`-ReleaseDirectory` 可选；省略时输出到 `build/release`。仓库磁盘空间不足时可指定其他磁盘上的目录。

当前机器已核对的框架源是上例中的 test/HoYoShade。脚本只提取框架及资源，不携带 test 的用户状态、插件或下载缓存。

## 空间规则（2026-10-25 用户要求）

每次打包前删除之前的打包占用，不能逐版本积累。成功后只保留本次最终 ZIP、校验 JSON 和小型日志，立即清掉本次解包暂存。Full 脚本通过 `tools/package-storage.ps1` 自动执行：

- 清理仓库 `build` 下的 `full-<版本>`、`portable-<版本>`、`HoYoShadeHub`、旧 `portable-launcher` 暂存目录，以及默认或指定 release 目录内的 `HoYoShadeHub_Portable_*.zip` / `.7z`、`F6Launcher_Portable_*.zip` / `.7z` 和同前缀解包目录。
- `-SkipBaseBuild` 仅在打包前保留明确指定的待复用暂存，打包结束仍清理该暂存。不要把运行中的用户安装或唯一框架源当作暂存目录。
- 删除失败则移动到 `D:\DATA\Documents\ChatGPT\DLSS F5 NEXT\cleanup\HoyoDLSS5-package-<时间>-<标识>`；原路径、逻辑字节数、去向和错误写入 `build/package-storage-cleanup.json`。同盘转移只减少工作区占用，不能宣称释放该磁盘空间。删除和移动均失败时中止并报告。
- 路径必须处于核准范围，拒绝重解析点；不清理 `.git`、SDK、`_research` 的研究源码、源码 `bin/obj` 构建缓存、test 安装、覆盖包或历史取证记录。截图中 `_research` 与 SDK 的占用不计作已清理的打包临时文件。
- 临时解包校验使用流式读取，不再完整解压第二份。逐文件哈希核验与必要结构检查通过后，记录最终 ZIP 大小、SHA256、条目数到 `<Full.zip>.verification.json`。

运行其他打包入口时也必须遵守上述清理要求，不能留下旧打包副本。规则摘要同步在 `AGENTS.md`；本次修改前备份位于 `docs/memory-backups/20261025-151952`。

## 构建后核验

脚本会核验 ZIP 中的必要路径，包括 `version.ini`、`portable-layout.json`、外层启动器、版本化应用、HoYoShade 的 `ReShade64.dll`、`inject.exe` 和 `ReShade.ini`。完成后另外读取每个 ZIP 条目，以检测压缩数据是否可完整解压，并计算文件大小与 SHA256：

```powershell
$zip = (Resolve-Path 'build\release\F6Launcher_Portable_1.4.8_x64_Full.zip').Path
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName.EndsWith('/')) { continue }
        $stream = $entry.Open()
        try {
            $buffer = New-Object byte[] 65536
            while ($stream.Read($buffer, 0, $buffer.Length) -gt 0) { }
        }
        finally { $stream.Dispose() }
    }
    $archive.Entries.Count
}
finally { $archive.Dispose() }

Get-Item -LiteralPath $zip | Select-Object FullName, Length, LastWriteTime
Get-FileHash -LiteralPath $zip -Algorithm SHA256
```

只有脚本成功退出、条目完整读取无异常，并记录了实际输出的文件大小和 SHA256，才能报告 Full 包构建与 ZIP 校验完成。根目录和应用目录必须原样附带 `LICENSE`、`Starward_LICENSE`、`NOTICE.md`，主入口是 `F6Launcher.exe`；打包门禁检查可执行文件产品名和独立图标。可再运行 `scripts/verify-fork-distribution.py` 检查实际 ZIP。构建成功不代表已上传、部署或通过真实游戏运行测试。

## 历史工作区记录

### 1.4.7 Full（2026-11-03）

- 入口：`package-full.ps1 -Version 1.4.7 -Architecture x64 -Configuration Release -ShadeSource 'D:\APPS\test\TEST\HoYoShadeHub_Portable_1.4.5.0_x64_Full\HoYoShade' -DisabledAddons ''`；复用用户 .NET 10 SDK，实际成功退出。
- 输出：`build/release/HoYoShadeHub_Portable_1.4.7_x64_Full.zip`，339386193 字节，2012 个条目，SHA256 `AE28D3DEEC34C363FF7FE2243687A2F9F1109919CB0052D6FDDEC146101CAA00`。
- ZIP 逐文件与暂存源 SHA256 一致，额外 CRC、必要结构、相对路径和用户状态排除检查通过。Full 内 922 个程序文件与此前已回归验证的 1.4.7 普通包逐文件一致；框架主 DLL 与来源一致。
- 框架包含 730 个滤镜、161 个材质，插件和截图目录留空。Full 不捆绑 F5/F6 Opt 或大型 NR DLL；升级 1.4.7 后可直接导入 F5 Final，无需旧 app-1.4.6.12 增量。
- 首次 1.4.7 发布误用 `package.ps1` 仅生成普通包，此次由正式 Full 入口补齐。普通包保留在远端，供现有启动器的更新文件名匹配使用；新安装推荐 Full。
- 构建日志 `artifacts/package-full-1.4.7-20261103.log`，校验回执 `<Full.zip>.verification.json`。本轮暂存已由脚本清理，没有覆盖本机 test，也未启动启动器或游戏。

截至 2026-10-20，`package-full.ps1` 已支持 `-ReleaseDirectory`。版本 `1.4.6.13` Full ZIP 已由脚本构建并校验，输出位于 `build/release/HoYoShadeHub_Portable_1.4.6.13_x64_Full.zip`，大小 `339315491` 字节、2000 个条目，SHA256 为 `A39C449E7DD8B0799ACEED2AA94B60DE4BCD4275D71A2E2E38401B8749C1F60B`。该构建已解压部署到 Downloads 下同名目录；没有进行真实游戏运行测试。

## 本次成品（2026-10-25）

- Full ZIP：`D:\CODE\HoyoDLSS5\build\release\HoYoShadeHub_Portable_1.4.6.14_x64_Full.zip`，339300085 字节（323.6 MiB），2014 个条目。
- SHA256：`C8FCB838F61F9DBA4CB671860A20FC72AC15D8DEA5235D20BE35D08EA5D79097`。
- 由正式脚本重新 publish 主程序和外层启动器；包内包含当日 NR 默认库记账修复、当前指纹种子、HoYoShade 框架及资源。插件目录按标准 Full 口径留空，未混入 test 的数据库、游戏选择、下载缓存或插件。
- 逐条 SHA256 与暂存源一致，最终包结构、版本入口及便携标记检查通过。校验记录：同目录 `<Full.zip>.verification.json`；构建日志：`artifacts/package-full-1.4.6.14-20261025.log`。
- 实际删除旧 `build/portable-launcher`（73612644 逻辑字节）及空的旧 TestedStack 暂存；本轮 604887019 逻辑字节的解包暂存也已删除。没有生成普通便携 ZIP，没有遗留 `build/full-1.4.6.14`。本轮清理均成功，未使用 cleanup 转移；字节数不等同于硬链接/压缩情况下的物理释放量。
- 清理边界与删除失败转移行为的 8 项检查通过，包含范围外路径、范围根目录和 junction 拒绝，以及模拟删除失败后的实际移动。日志：`artifacts/package-storage-tests-20261025.log`。没有启动启动器或游戏，也未上传发布。
