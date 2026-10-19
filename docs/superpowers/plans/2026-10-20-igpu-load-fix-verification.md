# Intel 核显占用率恒为 0 修复 —— 实机验证记录

> 对应计划：`docs/superpowers/plans/2026-10-20-igpu-load-fix.md`
> 执行日期：2026-10-20
> 开发机：`AMD Ryzen 7 5800X`（**无 Intel 核显**）+ `NVIDIA RTX 3080 Ti` + `NVIDIA RTX 3080`

## 0. 结论摘要

| 项 | 状态 | 说明 |
|---|---|---|
| 编译（Release，`--no-incremental`） | ✅ 通过 | 0 错误 / 228 警告（全部为本计划之前的存量警告） |
| 单元测试 | ✅ 通过 | **228/228**（基线 221 + 本计划新增 7） |
| 独显零回归（预热触发次数） | ✅ **实测 = 0 次** | 见 §2，决定性证据 |
| 虚拟适配器被误预热的风​险 | ✅ **实测不存在** | 与计划预期不符，已更正计划 |
| Intel 核显占用率随负载变化 | ⛔ **未验证** | 开发机无核显，须由 Intel 核显真机完成（§4） |
| 10 分钟兜底重建不归零 | ⛔ **未验证** | 需连续运行 ≥11 分钟并观察 UI（§4） |
| 切换首选显卡 / 热插拔 | ⛔ **未验证** | 需 UI 交互与硬件操作（§4） |

**一句话**：代码层与缓存层的修复已通过编译、全部单测，且在本机实测证明**不会给独显机器带来任何额外开销**；但"核显占用率真的会随负载变化"这一**行为**结论，本机无核显，无法验证。

---

## 1. 计划执行前的行号重核（Step 0）

计划撰写后，仓库因并发计划线继续提交（尤其 `c068d2c fix(hardware): 让硬件层故障可见并修复映射表被永久清空` 重构了 `HardwareMonitor.cs`），**`HardwareMonitor.cs` 的行号已全部漂移**。

重核结果：

| 文件 | 计划行号是否命中 | 处理 |
|---|---|---|
| `SensorMap.cs`（`:58` EnsureFresh / `:171` `//hw.Update();`） | ✅ 全部命中 | 直接使用 |
| `HardwareValueProvider.cs`（`:54` / `:132` / `:221` / `:503-504`） | ✅ 全部命中 | 无需改动 |
| `SensorMatcher.cs`（`:137` / `:139` / `:167-169`） | ✅ 全部命中 | 无需改动 |
| `HardwareMonitor.cs` | ❌ **全部漂移** | 按源码实际位置重新定位，见下表 |

`HardwareMonitor.cs` 实际插入点（执行时实测）：

| 用途 | 计划写的行号 | **实际行号** |
|---|---|---|
| `UpdateAll` 里 `EnsureFresh` 调用 | `:188` | **`:196`** |
| 启动流程 `WarmUpBatterySensors()` 之后 | `:316-321` | **`:333` 之后** |
| 重载流程 `if (opened)` 块内预热插入 | `:491-493` | **`:541` 之后** |
| `WarmUpBatterySensors()` 定义结束 / `UpdateTiming()` 之前 | `:380-392` / `:394` | **`:412` / `:414`** |

> 已确认三处新结构下的锁约束仍然成立：启动流程插入点在 `lock (_lock)`（`:327`）内；重载流程插入点在 `lock (_lock)`（`:488`）内且位于 `if (opened)` 分支里；`EnsureFresh` 在 `lock (_lock)`（`:191`）内。计划对锁的全部论证未因重构失效。

### 计划文档的一处事实更正

计划 Step 1.2 备注曾担心"4 个虚拟显示适配器（GameViewer / Meta / MuMu / Todesk）会被 LHM 归入 GPU 类型、从而每次启动都触发预热"。**实测该担心不成立**：`Computer.Open()` 后 `Computer.Hardware` 中的 GPU 类硬件**只有 2 个**（两张 NVIDIA 卡），虚拟显示适配器未被 LHM 枚举为 GPU 硬件。详见 §2。

---

## 2. 独显零回归实测（Step 5.3 第 4 项，**本机可测，已测**）

方法：反射加载 `LibreHardwareMonitorLib 0.9.6`，`Computer.Open()` 之后、任何 `Update()` 之前，枚举所有 GPU 类硬件的传感器类型 —— 这正是 `SensorMap.Rebuild` 建表的瞬间状态，也是 `GpuWarmup.NeedsWarmUp` 的判定输入。

实测输出：

| 硬件类型 | 名称 | Open 后是否已有 `Load` 传感器 | `NeedsWarmUp` |
|---|---|---|---|
| `GpuNvidia` | NVIDIA GeForce RTX 3080 Ti | **是**（传感器共 38 个，含多个 Load） | **false** |
| `GpuNvidia` | NVIDIA GeForce RTX 3080 | **是**（同上） | **false** |

**需预热的 GPU 数量 = 0。**

推论（本机成立）：

1. **`WarmUpGpuSensors()` 在双 NVIDIA 机器上是一次 `Update()` 都不会触发的空操作** → 计划"不得改变独显机器的启动性能"这一硬约束**实测满足**，启动耗时无需担心。
2. **虚拟显示适配器确实不会被预热** —— 它们压根不在 `Computer.Hardware` 的 GPU 列表里，`HardwareScanner.IsGpuHardware()` 也就无从命中。计划里"评估是否需要追加跳过虚拟适配器规则"的事项**可以关闭**。
3. 顺带证实了"独显构造函数自调 `Update()` → 传感器开箱即就绪"这一根因前提在真实机器上成立（不是仅靠 IL 静态分析）。

> 局限：本机两张卡都是 `GpuNvidia`。`GpuIntel`（核显）路径在真实硬件上仍未被观测过 —— 不能把本节结论外推到核显。

---

## 3. 代码层与缓存层修复的静态核对

| 修复点 | 落位 | 核对结果 |
|---|---|---|
| 缺陷 A：建表前预热 | `HardwareMonitor.WarmUpGpuSensors()`，启动 `:334` / 重载 `:546` | 两处均在 `lock (_lock)` 内 ✅ |
| 缺陷 A 判定逻辑 | `GpuWarmup.NeedsWarmUp(IEnumerable<SensorType>)` | 纯函数，3 个用例覆盖 ✅ |
| 缺陷 B：兜底重建后刷新快照 | `SensorMap.EnsureFresh` 返回 `bool`；`HardwareMonitor:198` 据此 `PreCacheAllSensors` | `EnsureFresh` 全仓仅 1 个调用点，签名变更无遗漏 ✅ |
| 未改动项 | `SensorMatcher` / `HardwareValueProvider` / `GpuKey` / `GpuRegistry` | 均未改动，符合计划的 YAGNI 约束 ✅ |

`SensorMatcher.MatchGpu("D3D 3D", SensorType.Load, true)` → `"GPU.Load"` 已由单测固定，说明核显传感器命名规则确实已能命中，问题只在数据层不送传感器 —— 与计划的根因判定一致。

---

## 4. 仍需真机完成的验证（本机无法进行）

以下各项**必须在有 Intel 核显的机器（或真实用户反馈）上完成**，本记录**不声称已验证**：

| # | 验证项 | 判定标准 |
|---|---|---|
| 5.2 | 核显占用率随负载变化 | 启动后不切换任何设置，`GPU.Load` 立即有值且随负载变化，与任务管理器同量级 |
| 5.2 | 核显显存 | `GPU.VRAM.Used` 有值（来自 `D3D Shared Memory Used`） |
| 5.4 | 10 分钟兜底重建 | 连续运行 ≥11 分钟，占用率全程有值、重建瞬间不归零 |
| 5.5 | 切换首选显卡 | A→B→A 切换后占用率立即更新，无空白/归零 |
| 5.6 | 热插拔 | 拔出/重插显卡后自动重载，占用率恢复有值（验证 Task 1.4 的重载预热） |

> 5.4 / 5.5 其实**本机也可以测**（不依赖核显，验证的是缓存刷新与自愈路径是否被破坏），但需要启动 GUI 并长时间观察，本轮未执行。建议发布前补测。

**另需注意**：AMD APU 机器的"占用率 0"是**另一种失效模式**（键存在但值为 null，源自 ADL 通路不提供 GFX activity），本修复**对它无效**，不应把本记录当作 AMD 侧的修复证据。

---

## 5. 提交记录

| 提交 | 内容 |
|---|---|
| `a20012b` | `fix(hardware)`: 新增 `GpuWarmup` + 两处预热调用（缺陷 A） |
| `08913ed` | `fix(hardware)`: `EnsureFresh` 返回 `bool` + 重建后刷新缓存（缺陷 B） |
| `e9bf91a` | `test(hardware)`: 7 个回归用例 |

回滚方式：`git revert e9bf91a 08913ed a20012b`（或整体 `git revert a20012b^..e9bf91a`）。若仅需临时关掉预热，注释 `WarmUpGpuSensors()` 的两处调用即可回到改动前行为，其余功能不受影响。
