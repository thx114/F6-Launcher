# F6 Launcher

F6 Launcher 是 thx114 维护的非官方分支，基于 HoYoShade Hub 修改。
本分支与 HoYoShade Hub、HoYoShade 框架及其作者没有官方隶属关系或背书关系。
分支的问题反馈、发行和启动器更新仅由本仓库维护：
https://github.com/thx114/HoYoShadeHub-DLSS5

上游启动器：HoYoShade Hub，作者 哆啦D夢|DuolaD。
https://github.com/DuolaD/HoYoShade-Hub

本分发保留上游 MIT 许可证原文：`LICENSE`，包含
`Copyright (c) 2025 哆啦D夢|DuolaD`。
包含的 Starward 代码许可证原文：`Starward_LICENSE`，包含
`Copyright (c) 2023 Scighost`。
F6 Launcher 的修改同样按 MIT 许可证提供，不替换或删除上游版权声明。

Full 版本还包含 HoYoShade 框架及 ReShade；其原有名称用于准确标识第三方组件。
对应许可证位于 `HoYoShade/LICENSE` 与 `HoYoShade/ReShade_LICENSE`，与启动器许可证分别保存。
其它第三方组件的许可证保留在相应目录。

随启动器提供的 F6ShadeInjector 基于 Patrick Mours 和 DuolaD 的 BSD-3-Clause
注入器修改，修正残留进程导致的就绪死循环；不修改 ReShade 核心。
其版权和许可保留在应用目录的 `F6ShadeInjector.LICENSE` 与
`F6ShadeInjector.ReShade_LICENSE`。

F6 Launcher 不使用 HoYoShade Hub 官方运营的私有镜像 API。
下载默认直连；用户可选择界面列出的独立公共 GitHub 代理。
不适用上游产品的用户协议、服务承诺或官方支持渠道。

为兼容已有便携版、配置及覆盖包，部分程序集、配置目录和可执行文件仍使用
`HoYoShadeHub` 技术标识；这不代表本产品是官方 HoYoShade Hub。
