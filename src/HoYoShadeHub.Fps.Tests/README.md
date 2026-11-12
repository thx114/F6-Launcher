# FPS remote lifetime fixture

在仓库根目录运行（复用本地 .NET 10 SDK）：

```powershell
& D:/CODE/HoyoDLSS5/.dotnet10/dotnet.exe run --project D:/CODE/HoyoDLSS5/src/HoYoShadeHub.Fps.Tests/HoYoShadeHub.Fps.Tests.csproj -c Release
```

此工程直接链接生产 `FpsUnlocker.cs` / `FpsRemoteLifetime*.cs`，只替换 WinUI 依赖的多人游戏守卫。无 NuGet 测试框架依赖，不构建主应用。`bin` / `obj` 均位于此独立工程内。完整输出保存在 `test-results.txt`。

19 项用例覆盖：线程/进程 WAIT_TIMEOUT、WAIT_FAILED、native wait 异常；同步线程结束但目标进程仍存活；部分写入、写入失败、Flush 失败、创建线程失败、线程创建后异常；free / close 失败重试；Attach/local loop pending；并发 Attach/Dispose/SetTarget 和重复 Dispose；维护写入短写；构造时 slot 初始化失败及释放失败；Attach 初始 slot 写入异常后 Dispose 退休；payload 范围校验；后台强引用和无超时 drain；同 PID 不同 process identity 的拒绝。

两个 Windows x64 用例启动本 fixture 的 `--helper` 子进程，用 `CreateRemoteThread` 执行受控等待开关的短代码。验证真实线程 timeout -> signaled 后仍保留远端代码和 slot；验证 process 退出结束仍在运行的线程，且退出码 259 仍由 process handle signaled 判定退出。不运行实际游戏 shellcode，不启动游戏，不访问测试安装。

Attach 在首次 await 前取得 `game.SafeHandle` 并 DangerousAddRef，保留到 Attach 完成后 DangerousRelease。native Open 后、任何目标内存 Read/Allocate/Write 之前调用 CompareObjectHandles，必须和固定的原始 handle 指向同一 kernel process object。身份不匹配用例要求 Read/Allocate/Write/Start 计数全部为零。helper 同时验证真实相同/不同 process handle 的比较结果。

## 对接接口

- `FpsUnlocker.HasPendingRetirements`：同步登记的退休 owner 队列是否非空。
- `FpsUnlocker.DrainRetirementsAsync()`：无调用方取消、无任意超时，等待已移交 owner 完成退休。调用方须先完成活跃 session 的交接，再 drain。
- GUI `Dispose()` 取消 local loop 并登记后台退休，不等待目标退出。

每个 owner 必须等 Attach 和 local loop 的 task 均真正完成，才能开始释放。已启动 payload 的远端分配和 slot 均保留到原始持有的 process handle signaled。未启动线程的失败路径可调用 VirtualFreeEx；失败时保留全部剩余所有权重试。process 退出后由 Windows 回收地址空间，不再调用 VirtualFreeEx，也不重新用 PID 打开进程。

fallback 在入口读取 PID，此后正常循环回到 +0x80；失败补丁也没有全局 stop 协议。退休路径不尝试清零 PID，也不调用 TerminateThread。当前 fallback 未发现向游戏外发 hook 的安装；下载的 shellcode 没有全局静止证明，因此即使同步线程 signaled 也保守等待 process 退出。

这是本地生命周期/受控 helper 证据，不是游戏视觉验证、主应用构建或用户 dump 因果证明。用户 dump 的 armed 后异常仍仅可能相关。

## 独立复核边界

`independent-review.txt` / `.log` 是只读子代理的独立源代码复核，正文回传 sentinel 已核验。其审阅快照早于最后的原始进程身份固定补丁及 Attach 初始 slot 写入顺序补丁，不能作为当前最终快照全覆盖认可；最终复核和主应用构建由主代理负责。

该复核未报阻塞性 P1/P2，指出的补强空间包括：真实 Attach 在成功启动线程后、发布 loop 前的托管调度异常，目前由 owner 层模拟；process-close-only 失败，目前优先覆盖 thread close 失败；多 owner 并发 drain 的专门用例；GC 测试可能受 JIT 局部变量寿命影响。shutdown 调用方必须先停止生产和交接 active session，再 drain，不能把 drain 当作未来新 Attach 的屏障。
