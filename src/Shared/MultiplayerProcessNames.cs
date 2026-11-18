using System;
using System.Collections.Generic;

namespace F6Launcher.Background;

internal static class MultiplayerProcessNames
{
    internal static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        // 三角洲行动（腾讯 ACE）
        "DeltaForceClient-Win64-Shipping",
        // 拳头 / Vanguard
        "VALORANT-Win64-Shipping",
        // Valve / CS2
        "cs2",
        // 绝地求生
        "TslGame",
        // Apex
        "r5apex",
        // 堡垒之夜 / EAC
        "FortniteClient-Win64-Shipping",
        // 使命召唤
        "cod", "codmw",
        // 战地
        "bf2042", "bf1", "bfv", "bf4",
        // 守望先锋
        "Overwatch",
        // The Finals / EAC
        "Discovery",
        // 暗区突围无限
        "ArenaBreakoutInfinite",
        // 战术小队
        "SquadGame",
        // 猎杀对决 / EAC
        "HuntGame",
        // 腐蚀 / EAC
        "RustClient",
        // 逃离塔科夫
        "EscapeFromTarkov", "TarkovArena",
        // 命运 2（对注入极敏感）
        "destiny2",
        // 注意：只收攻击性多人游戏本体进程。反作弊服务名（SGuardSvc64 / BEService / EAC 等）
        // 不收 —— 一些二游同样带 ACE 但扫描宽松，按服务名匹配会把它们误伤、把启动器自己退掉。
    };

}
