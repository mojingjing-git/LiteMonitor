# GPU 多显卡监控改造 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 LiteMonitor 同时显示 N 张显卡的独立指标（温度/占用/功耗/频率/显存/风扇），并顺带修复「GPU.Fan 风扇读数串到另一张卡」的既有 bug。

**Architecture:** 监控项 Key 从单卡的 `GPU.Load` 扩展为带序号的 `GPU.{index}.{Metric}`。序号即被监控显卡在监控集合中的下标。该命名同时满足三个既有机制，使它们零改动继续工作：`MetricUtils.GetType` 走子串匹配（`GPU.1.Temp` 仍含 `TEMP`）、`Settings.IsAnyEnabled("GPU")` 走前缀匹配、`MonitorItemConfig.UIGroup` 取 `Key.Split('.')[0]`（仍归入 `GPU` 分组）。数据层在 `SensorMap.Rebuild` 中按序号为每张卡建立独立映射；显示层在标签/阈值/峰值/历史四处做「带序号 Key → 基础 Key」的归一化回退。

**Tech Stack:** C# / .NET 8 (`net8.0-windows`) / WinForms / LibreHardwareMonitorLib 0.9.6 / xUnit（仅测试工程）

## Global Constraints

- 目标框架固定 `net8.0-windows`；使用本地 SDK `G:\TOOL\dotnet-sdk\dotnet.exe`（版本 8.0.426），**不得**安装到系统或修改系统 PATH。
- 除测试工程外**不得新增任何 NuGet 依赖**；主工程依赖保持 `LibreHardwareMonitorLib 0.9.6` + `System.Text.Json 9.0.10` 不变。
- **向后兼容硬约束**：`Settings.MonitoredGpuCount` 默认值必须是 `1`。在默认配置下，程序行为与改造前**逐项一致**（含 GPU.Fan 读数来源从「最后一张卡」纠正为「第一张卡」——这是 bug 修复，属预期差异）。
- 不得修改 `resources/plugins/` 下 13 个内置插件 JSON 的任何内容。
- 不得修改 `resources/themes/`、`resources/lang/` 中除新增菜单文案以外的任何现有条目。
- 提交粒度：**每个 Task 结束提交一次**，提交信息用中文正文 + 英文 type 前缀（如 `feat(gpu): ...`）。
- 全部分支工作基于 `feat/multi-gpu` 分支，从 `master`（`169e90a`）切出。
- 复用既有锁策略：硬件层持 `_lock`（`HardwareMonitor._lock`），UI 线程取值走 `Monitor.TryEnter(10ms)`，**不得**在 UI 线程发起阻塞式硬件调用。
- 注释语言：与被改文件保持一致（`src/System/**` 用中文，`src/Core/**` 中英皆可），新增文件统一中文注释。

## 仓库与工具链现状（已就绪，无需重复准备）

- Fork: `https://github.com/mojingjing-git/LiteMonitor`（上游 `Diorser/LiteMonitor`）
- 工作副本: `G:\TOOL\LiteMonitor-fork`，`origin`=fork，`upstream`=上游
- SDK: `G:\TOOL\dotnet-sdk\dotnet.exe`（8.0.426），命令前缀见下方「命令速查」
- 基线编译已通过（Release，0 错误 0 警告）
- `使用说明(必读).txt` 已被 csproj 引用但上游仓库未提交，已从本机安装目录补入工作区（**待 Task 1 提交**）

### 命令速查

```powershell
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$dotnet = 'G:\TOOL\dotnet-sdk\dotnet.exe'
Set-Location 'G:\TOOL\LiteMonitor-fork'
```

---

## File Structure

### 新建文件

| 文件 | 职责 |
|---|---|
| `src/Core/GpuKey.cs` | 显卡 Key 的解析/构造/归一化。纯静态、无依赖。 |
| `src/Core/GpuRegistry.cs` | 运行时「序号 → 显卡简称/全名」注册表，供 UI 打标签。 |
| `src/Core/GpuItemSync.cs` | 保证 `Settings.MonitorItems` 中的显卡项与 `MonitoredGpuCount` 一致，并完成旧格式迁移。 |
| `tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj` | xUnit 测试工程。 |
| `tests/LiteMonitor.Tests/GpuKeyTests.cs` | GpuKey 解析测试。 |
| `tests/LiteMonitor.Tests/GpuItemSyncTests.cs` | 监控项同步/迁移测试。 |
| `tests/LiteMonitor.Tests/HardwareRulesGpuNameTests.cs` | 显卡简称提取测试。 |
| `tests/LiteMonitor.Tests/MetricUtilsGpuKeyTests.cs` | 归一化与类型判定测试。 |

### 修改文件

| 文件 | 改动 |
|---|---|
| `src/Core/Settings.cs` | 新增 `MonitoredGpuCount`、`RecordedMaxGpuDynamic`、`DetectedGpuVramTotalGBByKey`。 |
| `src/Core/SettingsHelper.cs` | `Load()` 中调用 `GpuItemSync.Ensure`；`UpdateMaxRecord` 改为按基础 Key 比对并记录每卡峰值。 |
| `src/Core/MetricUtils.cs` | `GetType` 天然兼容（不改）；`GetAdaptivePercentage` 归一化 + 每卡峰值优先；显存容量模式按卡取总容量。 |
| `src/Core/MetricLabelResolver.cs` | 多卡标签加显卡简称；任务栏简称用显卡简称。 |
| `src/Core/HardwareHistoryLogger.cs` | 历史采样支持带序号的显卡 Key。 |
| `src/System/HardwareServices/HardwareRules.cs` | 新增 `GetShortGpuName`。 |
| `src/System/HardwareServices/SensorMap.cs` | 多卡映射核心；`CachedGpus` 列表；修 GPU.Fan 串卡。 |
| `src/System/HardwareServices/ComponentProcessor.cs` | Clock/Power 按序号取对应显卡。 |
| `src/System/HardwareServices/HardwareValueProvider.cs` | 显存按序号取；`OnUpdateTickStarted` 感知显卡数量变化。 |
| `src/System/HardwareMonitor.cs` | 刷新所有被监控显卡。 |
| `src/UI/Settings/SystemHardwarPage.cs` | 新增「监控显卡数量」下拉。 |
| `resources/lang/*.json`（9 个） | 新增 `Menu.GpuMonitorCount` 与 `Menu.GpuMonitorCountTip`。 |
| `.gitignore` | 忽略 `bin/`、`obj/`。 |
| `使用说明(必读).txt` | 提交（修复上游全新克隆编译不过的问题）。 |

---

## Task 1: 工程基线与测试骨架

**Files:**
- Create: `tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj`
- Create: `tests/LiteMonitor.Tests/Placeholder.cs`
- Modify: `.gitignore`
- Modify: `使用说明(必读).txt`（新增，从未提交状态转为已提交）
- Modify: `LiteMonitor.sln`（加入测试工程）

**Interfaces:**
- Consumes: 无
- Produces: 可运行的 `dotnet test` 命令；`G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj` 引用主工程 `LiteMonitor.csproj`

- [ ] **Step 1: 切分支**

```powershell
Set-Location 'G:\TOOL\LiteMonitor-fork'
git checkout -b feat/multi-gpu
git branch --show-current
```

Expected: 输出 `feat/multi-gpu`

- [ ] **Step 2: 写 .gitignore**

覆盖 `bin/`、`obj/`、`*.user`、`settings.json` 等运行期产物。写入 `G:\TOOL\LiteMonitor-fork\.gitignore`：

```gitignore
# 构建产物
bin/
obj/
*.user
*.suo

# 运行期数据（不应进版本库）
settings.json
settings.json.bak
HardwareHistory.json
HardwareHistory.json.bak
TrafficHistory.json
TrafficHistory.json.bak
*.log
```

- [ ] **Step 3: 创建测试工程文件**

写入 `G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <IsPackable>false</IsPackable>
    <Nullable>disable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>LiteMonitor.Tests</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\LiteMonitor.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 4: 写占位测试文件**

写入 `G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\Placeholder.cs`：

```csharp
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 冒烟测试：确认测试工程能引用主工程并运行。
    /// 后续 Task 会用真实测试替换本文件。
    /// </summary>
    public class Placeholder
    {
        [Fact]
        public void TestHarness_Works()
        {
            Assert.True(true);
        }
    }
}
```

- [ ] **Step 5: 把测试工程加入解决方案**

```powershell
$dotnet sln 'G:\TOOL\LiteMonitor-fork\LiteMonitor.sln' add 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj'
```

Expected: 输出 `Project ... added to the solution.`

- [ ] **Step 6: 运行测试确认骨架可用**

```powershell
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: `通过! - 失败: 0, 已通过: 1, 已跳过: 0, 总计: 1`（中文本地化输出）或等价的 `Passed! - Failed: 0, Passed: 1`

- [ ] **Step 7: 提交**

```powershell
git add .gitignore '使用说明(必读).txt' tests/ LiteMonitor.sln
git commit -m "chore: 建立多显卡改造的工程基线与测试骨架

- 新增 xUnit 测试工程 tests/LiteMonitor.Tests
- .gitignore 忽略构建产物与运行期数据文件
- 补交 使用说明(必读).txt（上游 csproj 引用了它但未入库，导致全新克隆编译不过）"
```

---

## Task 2: GpuKey 与 GpuRegistry

**Files:**
- Create: `src/Core/GpuKey.cs`
- Create: `src/Core/GpuRegistry.cs`
- Create: `tests/LiteMonitor.Tests/GpuKeyTests.cs`
- Delete: `tests/LiteMonitor.Tests/Placeholder.cs`

**Interfaces:**
- Consumes: 无
- Produces:
  - `LiteMonitor.src.Core.GpuKey`：`const string Prefix = "GPU."`、`const int MaxGpuCount = 8`、`static bool TryParse(string key, out int index, out string metric)`、`static string Build(int index, string metric)`、`static string BaseKey(string metric)`、`static string ToBaseKey(string key)`
  - `LiteMonitor.src.Core.GpuRegistry`：`static void Set(int index, string shortName, string fullName)`、`static string GetShortName(int index)`、`static string GetFullName(int index)`、`static void Clear()`

- [ ] **Step 1: 写失败测试**

写入 `G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\GpuKeyTests.cs`：

```csharp
using LiteMonitor.src.Core;
using Xunit;

namespace LiteMonitor.Tests
{
    public class GpuKeyTests
    {
        [Theory]
        [InlineData("GPU.0.Temp", 0, "Temp")]
        [InlineData("GPU.1.Temp", 1, "Temp")]
        [InlineData("GPU.10.VRAM", 10, "VRAM")]
        [InlineData("gpu.3.power", 3, "power")]
        public void TryParse_带序号的Key_解析出序号与指标(string key, int idx, string metric)
        {
            bool ok = GpuKey.TryParse(key, out int index, out string m);
            Assert.True(ok);
            Assert.Equal(idx, index);
            Assert.Equal(metric, m);
        }

        [Theory]
        [InlineData("GPU.Temp")]
        [InlineData("GPU.VRAM")]
        [InlineData("CPU.Temp")]
        [InlineData("GPU.")]
        [InlineData("GPU.-1.Temp")]
        [InlineData("GPU.x.Temp")]
        [InlineData("")]
        [InlineData(null)]
        public void TryParse_非带序号或非法Key_返回false(string key)
        {
            Assert.False(GpuKey.TryParse(key, out _, out _));
        }

        [Fact]
        public void Build_拼接出带序号Key()
        {
            Assert.Equal("GPU.2.Power", GpuKey.Build(2, "Power"));
        }

        [Fact]
        public void BaseKey_还原基础Key()
        {
            Assert.Equal("GPU.Temp", GpuKey.BaseKey("Temp"));
        }

        [Theory]
        [InlineData("GPU.1.Temp", "GPU.Temp")]
        [InlineData("GPU.Temp", "GPU.Temp")]
        [InlineData("CPU.Power", "CPU.Power")]
        public void ToBaseKey_归一化到基础Key(string input, string expected)
        {
            Assert.Equal(expected, GpuKey.ToBaseKey(input));
        }
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```powershell
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 编译错误 `CS0246: 未能找到类型或命名空间 "GpuKey"`

- [ ] **Step 3: 实现 GpuKey**

写入 `G:\TOOL\LiteMonitor-fork\src\Core\GpuKey.cs`：

```csharp
using System;
using System.Globalization;

namespace LiteMonitor.src.Core
{
    /// <summary>
    /// 多显卡监控的 Key 命名规则。
    ///
    /// 单显卡 (旧格式) : "GPU.Load"
    /// 多显卡 (新格式) : "GPU.0.Load"  —— 序号 = 被监控显卡在监控集合中的下标 (从 0 开始)
    ///
    /// 之所以这样设计, 是为了让既有逻辑零改动地继续工作:
    ///   - MetricUtils.GetType 走子串匹配, "GPU.1.Temp" 仍含 "TEMP" -> Temperature
    ///   - Settings.IsAnyEnabled("GPU") 走前缀匹配, 仍能命中
    ///   - MonitorItemConfig.UIGroup 取 Key.Split('.')[0], 仍归到 "GPU" 分组
    /// </summary>
    public static class GpuKey
    {
        /// <summary>显卡 Key 前缀</summary>
        public const string Prefix = "GPU.";

        /// <summary>设置项允许的显卡数量上限</summary>
        public const int MaxGpuCount = 8;

        /// <summary>
        /// 解析带序号的显卡 Key。
        /// "GPU.1.Temp" -> (index=1, metric="Temp") ; "GPU.Temp" -> false
        /// </summary>
        public static bool TryParse(string key, out int index, out string metric)
        {
            index = -1;
            metric = "";
            if (string.IsNullOrEmpty(key)) return false;
            if (!key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;

            string rest = key.Substring(Prefix.Length);
            int dot = rest.IndexOf('.');
            if (dot <= 0) return false;

            if (!int.TryParse(rest.Substring(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out index))
                return false;
            if (index < 0) return false;

            metric = rest.Substring(dot + 1);
            return metric.Length > 0;
        }

        /// <summary>构造带序号的 Key: (1, "Temp") -> "GPU.1.Temp"</summary>
        public static string Build(int index, string metric) => $"{Prefix}{index}.{metric}";

        /// <summary>基础 Key: "Temp" -> "GPU.Temp" (语言包 / 阈值回退用)</summary>
        public static string BaseKey(string metric) => Prefix + metric;

        /// <summary>
        /// 把带序号的 Key 归一化成基础 Key, 用于查语言包 / 阈值 / 峰值记录。
        /// "GPU.1.Temp" -> "GPU.Temp" ; "GPU.Temp" -> "GPU.Temp"
        /// </summary>
        public static string ToBaseKey(string key)
        {
            if (TryParse(key, out _, out string metric)) return BaseKey(metric);
            return key ?? "";
        }
    }
}
```

- [ ] **Step 4: 实现 GpuRegistry**

写入 `G:\TOOL\LiteMonitor-fork\src\Core\GpuRegistry.cs`：

```csharp
using System;

namespace LiteMonitor.src.Core
{
    /// <summary>
    /// 运行时显卡信息注册表: 把 "GPU.{序号}" 对应到显卡的简称 / 全名, 供 UI 打标签用。
    /// 由 SensorMap 在建立映射时写入。
    /// </summary>
    public static class GpuRegistry
    {
        private static readonly string[] _shortNames = new string[GpuKey.MaxGpuCount];
        private static readonly string[] _fullNames = new string[GpuKey.MaxGpuCount];

        /// <summary>登记某序号对应的显卡</summary>
        public static void Set(int index, string shortName, string fullName)
        {
            if (index < 0 || index >= _shortNames.Length) return;
            _shortNames[index] = shortName ?? "";
            _fullNames[index] = fullName ?? "";
        }

        /// <summary>取显卡简称 (如 "3080 Ti")，越界返回空串</summary>
        public static string GetShortName(int index)
            => (index >= 0 && index < _shortNames.Length) ? (_shortNames[index] ?? "") : "";

        /// <summary>取显卡全名 (如 "NVIDIA GeForce RTX 3080 Ti")，越界返回空串</summary>
        public static string GetFullName(int index)
            => (index >= 0 && index < _fullNames.Length) ? (_fullNames[index] ?? "") : "";

        /// <summary>清空注册表 (硬件重载时调用)</summary>
        public static void Clear()
        {
            Array.Clear(_shortNames, 0, _shortNames.Length);
            Array.Clear(_fullNames, 0, _fullNames.Length);
        }
    }
}
```

- [ ] **Step 5: 删除占位测试并运行测试**

删除 `G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\Placeholder.cs`，然后：

```powershell
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 全部通过（11 个测试用例）

- [ ] **Step 6: 提交**

```powershell
git add src/Core/GpuKey.cs src/Core/GpuRegistry.cs tests/
git commit -m "feat(gpu): 新增多显卡 Key 命名规则与运行时显卡注册表

GpuKey 负责带序号 Key 的解析/构造/归一化, 使既有 GetType/IsAnyEnabled/UIGroup
逻辑零改动兼容多卡; GpuRegistry 供 UI 在打标签时取显卡简称。"
```

---

## Task 3: HardwareRules 显卡简称提取

**Files:**
- Modify: `src/System/HardwareServices/HardwareRules.cs`（在 `ShouldUseSharedMemory` 之后追加方法）
- Create: `tests/LiteMonitor.Tests/HardwareRulesGpuNameTests.cs`

**Interfaces:**
- Consumes: 无
- Produces: `LiteMonitor.src.SystemServices.HardwareRules.GetShortGpuName(string name) -> string`

- [ ] **Step 1: 写失败测试**

写入 `G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\HardwareRulesGpuNameTests.cs`：

```csharp
using LiteMonitor.src.SystemServices;
using Xunit;

namespace LiteMonitor.Tests
{
    public class HardwareRulesGpuNameTests
    {
        [Theory]
        [InlineData("NVIDIA GeForce RTX 3080 Ti", "3080 Ti")]
        [InlineData("NVIDIA GeForce RTX 3080", "3080")]
        [InlineData("AMD Radeon RX 7900 XTX", "7900 XTX")]
        [InlineData("AMD Radeon(TM) Graphics", "Graphics")]
        [InlineData("Intel Arc B580", "B580")]
        [InlineData("Intel(R) UHD Graphics 770", "UHD Graphics 770")]
        [InlineData("Intel Arc Graphics", "Graphics")]
        public void GetShortGpuName_剥离厂商与系列前缀(string full, string expected)
        {
            Assert.Equal(expected, HardwareRules.GetShortGpuName(full));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void GetShortGpuName_空输入返回空串(string input)
        {
            Assert.Equal("", HardwareRules.GetShortGpuName(input));
        }
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```powershell
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 编译错误 `CS0117: "HardwareRules" 不包含 "GetShortGpuName" 的定义`

- [ ] **Step 3: 实现 GetShortGpuName**

在 `src/System/HardwareServices/HardwareRules.cs` 的 `ShouldUseSharedMemory` 方法之后、`class` 结束括号之前插入：

```csharp
        /// <summary>
        /// 从显卡全名提取简短显示名, 用于多显卡监控时区分不同显卡。
        /// "NVIDIA GeForce RTX 3080 Ti" -> "3080 Ti"
        /// "AMD Radeon RX 7900 XTX"      -> "7900 XTX"
        /// "Intel Arc B580"              -> "B580"
        /// </summary>
        public static string GetShortGpuName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";

            string n = name.Trim();

            // 1. 剥离厂商前缀
            string[] vendorPrefixes =
            {
                "NVIDIA GeForce ", "NVIDIA ", "AMD Radeon ", "AMD ",
                "Intel(R) ", "Intel ", "Apple ", "Microsoft "
            };
            foreach (var p in vendorPrefixes)
            {
                if (n.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                {
                    n = n.Substring(p.Length).Trim();
                    break;
                }
            }

            // 2. 剥离系列前缀 (RTX / RX / Arc ...)
            string[] seriesPrefixes = { "RTX ", "GTX ", "RX ", "Radeon ", "Arc " };
            foreach (var s in seriesPrefixes)
            {
                if (n.StartsWith(s, StringComparison.OrdinalIgnoreCase))
                {
                    n = n.Substring(s.Length).Trim();
                    break;
                }
            }

            return n.Length > 0 ? n : "";
        }
```

- [ ] **Step 4: 运行测试确认通过**

```powershell
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 全部通过

- [ ] **Step 5: 提交**

```powershell
git add src/System/HardwareServices/HardwareRules.cs tests/LiteMonitor.Tests/HardwareRulesGpuNameTests.cs
git commit -m "feat(gpu): 新增显卡简称提取规则

从显卡全名剥离厂商前缀与系列前缀, 得到用于区分多卡的短名 (如 3080 Ti)。"
```

---

## Task 4: GpuItemSync 监控项同步与迁移

**Files:**
- Create: `src/Core/GpuItemSync.cs`
- Create: `tests/LiteMonitor.Tests/GpuItemSyncTests.cs`
- Modify: `src/Core/Settings.cs`（新增 `MonitoredGpuCount`，供 GpuItemSync 读取数量）

**Interfaces:**
- Consumes: `LiteMonitor.src.Core.GpuKey`（Task 2）、`LiteMonitor.src.Core.Settings`（既有）、`LiteMonitor.src.Core.MonitorItemConfig`（既有）
- Produces:
  - `Settings.MonitoredGpuCount : int`（默认 1）
  - `LiteMonitor.src.Core.GpuItemSync.Ensure(Settings cfg)`；常量 `GpuItemSync.Metrics`、`GpuItemSync.SortStride = 10`

- [ ] **Step 1: 在 Settings 中新增 MonitoredGpuCount**

在 `src/Core/Settings.cs` 中，把这一行：

```csharp
        public List<MonitorItemConfig> MonitorItems { get; set; } = new List<MonitorItemConfig>();
```

替换为：

```csharp
        public List<MonitorItemConfig> MonitorItems { get; set; } = new List<MonitorItemConfig>();

        // ★★★ [多显卡] 监控显卡数量: 1 = 只监控 1 张 (沿用旧行为), N = 按优先级监控前 N 张 ★★★
        public int MonitoredGpuCount { get; set; } = 1;
```

> `Settings.cs` 已 `using System.Collections.Generic;`，无需补 using。

- [ ] **Step 2: 写失败测试**

写入 `G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\GpuItemSyncTests.cs`：

```csharp
using System.Collections.Generic;
using System.Linq;
using LiteMonitor.src.Core;
using Xunit;

namespace LiteMonitor.Tests
{
    public class GpuItemSyncTests
    {
        private static Settings NewSettings(int count, params MonitorItemConfig[] items)
        {
            var s = new Settings { MonitoredGpuCount = count };
            s.MonitorItems = new List<MonitorItemConfig>(items);
            return s;
        }

        [Fact]
        public void Ensure_旧单卡配置_迁移成带序号形式()
        {
            var s = NewSettings(1,
                new MonitorItemConfig { Key = "CPU.Load", VisibleInPanel = true },
                new MonitorItemConfig { Key = "GPU.Load", VisibleInPanel = true, VisibleInTaskbar = true },
                new MonitorItemConfig { Key = "GPU.Temp", VisibleInPanel = true });

            GpuItemSync.Ensure(s);

            Assert.DoesNotContain(s.MonitorItems, x => x.Key == "GPU.Load");
            Assert.Contains(s.MonitorItems, x => x.Key == "GPU.0.Load");
            Assert.Contains(s.MonitorItems, x => x.Key == "GPU.0.Temp");
            // 可见性从旧项继承
            Assert.True(s.MonitorItems.First(x => x.Key == "GPU.0.Load").VisibleInTaskbar);
        }

        [Fact]
        public void Ensure_两张卡_生成两组共十二项()
        {
            var s = NewSettings(2);
            GpuItemSync.Ensure(s);

            var gpuItems = s.MonitorItems.Where(x => x.Key.StartsWith("GPU.")).ToList();
            Assert.Equal(12, gpuItems.Count);
            Assert.Contains(s.MonitorItems, x => x.Key == "GPU.0.Load");
            Assert.Contains(s.MonitorItems, x => x.Key == "GPU.1.Load");
        }

        [Fact]
        public void Ensure_减少数量_裁掉多余序号的项()
        {
            var s = NewSettings(2);
            GpuItemSync.Ensure(s);
            Assert.Contains(s.MonitorItems, x => x.Key == "GPU.1.Temp");

            s.MonitoredGpuCount = 1;
            GpuItemSync.Ensure(s);

            Assert.DoesNotContain(s.MonitorItems, x => x.Key.StartsWith("GPU.1."));
            Assert.Contains(s.MonitorItems, x => x.Key == "GPU.0.Temp");
        }

        [Fact]
        public void Ensure_重复调用_不产生重复项()
        {
            var s = NewSettings(2);
            GpuItemSync.Ensure(s);
            int first = s.MonitorItems.Count;
            GpuItemSync.Ensure(s);
            Assert.Equal(first, s.MonitorItems.Count);
        }

        [Fact]
        public void Ensure_保留用户已有的自定义()
        {
            var s = NewSettings(1);
            GpuItemSync.Ensure(s);

            var item = s.MonitorItems.First(x => x.Key == "GPU.0.Temp");
            item.UserLabel = "我的温度";
            item.VisibleInTaskbar = true;

            GpuItemSync.Ensure(s);

            var again = s.MonitorItems.First(x => x.Key == "GPU.0.Temp");
            Assert.Equal("我的温度", again.UserLabel);
            Assert.True(again.VisibleInTaskbar);
        }

        [Fact]
        public void Ensure_数量非法时_夹到一和上限之间()
        {
            var s = NewSettings(0);
            GpuItemSync.Ensure(s);
            Assert.Equal(6, s.MonitorItems.Count(x => x.Key.StartsWith("GPU.")));

            s.MonitoredGpuCount = 999;
            GpuItemSync.Ensure(s);
            Assert.Equal(6 * GpuKey.MaxGpuCount, s.MonitorItems.Count(x => x.Key.StartsWith("GPU.")));
        }
    }
}
```

- [ ] **Step 3: 运行测试确认失败**

```powershell
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 编译错误 `CS0246: 未能找到类型或命名空间 "GpuItemSync"`

- [ ] **Step 4: 实现 GpuItemSync**

写入 `G:\TOOL\LiteMonitor-fork\src\Core\GpuItemSync.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteMonitor.src.Core
{
    /// <summary>
    /// 显卡监控项同步器。
    ///
    /// 职责: 保证 Settings.MonitorItems 中的显卡监控项与 Settings.MonitoredGpuCount 保持一致。
    ///   - 每个被监控的显卡生成一组 "GPU.{序号}.{指标}" 监控项
    ///   - 从旧版单卡格式 ("GPU.Load") 迁移时继承可见性 / 排序 / 单位, 但不继承用户自定义名称
    ///     (名称改由显卡简称自动生成, 否则两张卡会显示同一个名字)
    ///   - 已存在的同 Key 项保留用户的全部自定义
    /// </summary>
    public static class GpuItemSync
    {
        /// <summary>显卡基础指标定义: (指标名, 排序基准, 主界面默认可见, 任务栏默认可见)</summary>
        public static readonly (string metric, int sort, bool panel, bool taskbar)[] Metrics =
        {
            ("Load",  301, true,  true),
            ("Temp",  302, true,  false),
            ("Clock", 303, false, false),
            ("Power", 304, false, false),
            ("Fan",   305, false, false),
            ("VRAM",  306, true,  false),
        };

        /// <summary>每张卡占用的排序位宽度, 让同一分组内按卡顺序排列</summary>
        public const int SortStride = 10;

        /// <summary>
        /// 同步显卡监控项。可重复调用, 幂等。
        /// </summary>
        public static void Ensure(Settings cfg)
        {
            if (cfg?.MonitorItems == null) return;

            int count = cfg.MonitoredGpuCount;
            if (count < 1) count = 1;
            if (count > GpuKey.MaxGpuCount) count = GpuKey.MaxGpuCount;

            var items = cfg.MonitorItems;

            // 1. 旧版单卡项作为迁移模板
            var legacy = new Dictionary<string, MonitorItemConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in items)
            {
                if (it.Key.StartsWith(GpuKey.Prefix, StringComparison.OrdinalIgnoreCase)
                    && !GpuKey.TryParse(it.Key, out _, out _))
                {
                    legacy[it.Key] = it;
                }
            }

            // 2. 已存在的带序号项 (保留用户自定义)
            var indexed = new Dictionary<string, MonitorItemConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in items)
            {
                if (GpuKey.TryParse(it.Key, out _, out _))
                {
                    indexed[it.Key] = it;
                }
            }

            // 3. 清掉全部显卡相关项 (旧格式 + 不再需要的序号)
            items.RemoveAll(it => it.Key.StartsWith(GpuKey.Prefix, StringComparison.OrdinalIgnoreCase));

            // 4. 按当前数量重新生成
            for (int i = 0; i < count; i++)
            {
                foreach (var (metric, sort, panel, taskbar) in Metrics)
                {
                    string key = GpuKey.Build(i, metric);
                    if (indexed.TryGetValue(key, out var existing))
                    {
                        items.Add(existing);
                        continue;
                    }

                    string baseKey = GpuKey.BaseKey(metric);
                    legacy.TryGetValue(baseKey, out var tpl);

                    int taskbarSort = tpl != null && tpl.TaskbarSortIndex != 0
                        ? tpl.TaskbarSortIndex
                        : sort;
                    taskbarSort += i * SortStride;

                    items.Add(new MonitorItemConfig
                    {
                        Key = key,
                        SortIndex = sort + i * SortStride,
                        TaskbarSortIndex = taskbarSort,
                        VisibleInPanel = tpl?.VisibleInPanel ?? panel,
                        VisibleInTaskbar = tpl?.VisibleInTaskbar ?? taskbar,
                        UnitPanel = tpl?.UnitPanel,
                        UnitTaskbar = tpl?.UnitTaskbar,
                    });
                }
            }
        }
    }
}
```

- [ ] **Step 5: 运行测试确认通过**

```powershell
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 全部通过

- [ ] **Step 6: 提交**

```powershell
git add src/Core/GpuItemSync.cs src/Core/Settings.cs tests/LiteMonitor.Tests/GpuItemSyncTests.cs
git commit -m "feat(gpu): 新增显卡监控项同步器

按 MonitoredGpuCount 生成 GPU.{序号}.{指标} 监控项, 并把旧的单卡格式
(GPU.Load 等) 迁移过来, 保留可见性/单位/用户自定义。"
```

---

## Task 5: Settings 字段与 SettingsHelper 接入

**Files:**
- Modify: `src/Core/Settings.cs`（在 `MonitorItems` 属性附近新增三个成员）
- Modify: `src/Core/SettingsHelper.cs`（`Load()` 中调用 `GpuItemSync.Ensure`；`UpdateMaxRecord` 改为归一化比对）
- Modify: `tests/LiteMonitor.Tests/GpuItemSyncTests.cs`（追加一个 `Ensure` 与 `Load` 联动的用例可选；本步至少保证既有测试仍通过）

**Interfaces:**
- Consumes: `GpuItemSync.Ensure`（Task 4）、`GpuKey.ToBaseKey`（Task 2）
- Produces:
  - `Settings.RecordedMaxGpuDynamic : Dictionary<string, float>`
  - `Settings.DetectedGpuVramTotalGBByKey : Dictionary<string, float>`（静态，`[JsonIgnore]`）

- [ ] **Step 1: 在 Settings 中新增字段**

在 `src/Core/Settings.cs` 中，把这一行：

```csharp
        public List<MonitorItemConfig> MonitorItems { get; set; } = new List<MonitorItemConfig>();
```

替换为：

```csharp
        public List<MonitorItemConfig> MonitorItems { get; set; } = new List<MonitorItemConfig>();

        // ★★★ [多显卡] 各显卡各自的峰值记录 (key = 完整 Key, 如 "GPU.1.Power"), 供进度条按卡自适应 ★★★
        public Dictionary<string, float> RecordedMaxGpuDynamic { get; set; } = new Dictionary<string, float>();

        // ★★★ [多显卡] 运行时缓存: 各显卡的显存总容量 (GB), key = "GPU" / "GPU.0" / "GPU.1" ★★★
        [JsonIgnore]
        public static Dictionary<string, float> DetectedGpuVramTotalGBByKey { get; } = new Dictionary<string, float>();
```

> 注：`MonitoredGpuCount` 已在 Task 4 加入（`GpuItemSync` 依赖它），此处不要重复添加。
> `Settings.cs` 已 `using System.Collections.Generic;` 与 `using System.Text.Json.Serialization;`，无需补 using。若编译报 `JsonIgnore` 找不到，补 `using System.Text.Json.Serialization;`。

- [ ] **Step 2: 在 SettingsHelper.Load 中接入同步**

在 `src/Core/SettingsHelper.cs` 的 `Load()` 方法中，把这一段：

```csharp
            else
            {
                // 2. Version check
                bool isLegacyConfig = s.MonitorItems.All(x => x.TaskbarSortIndex == 0);

                if (isLegacyConfig)
                {
                    s.RebuildAndMigrateSettings();
                }
                else
                {
                    s.CheckAndAppendMissingItems();
                }
            }
```

替换为：

```csharp
            else
            {
                // 2. Version check
                bool isLegacyConfig = s.MonitorItems.All(x => x.TaskbarSortIndex == 0);

                if (isLegacyConfig)
                {
                    s.RebuildAndMigrateSettings();
                }
                else
                {
                    s.CheckAndAppendMissingItems();
                }
            }

            // 3. [多显卡] 同步显卡监控项。
            //    必须放在 CheckAndAppendMissingItems 之后 —— 那一步会把缺失的默认项
            //    (含旧的 "GPU.Load" 单卡项) 补回来, 这里再统一迁移成 "GPU.{序号}.{指标}"。
            GpuItemSync.Ensure(s);
```

- [ ] **Step 3: 改造 UpdateMaxRecord**

在 `src/Core/SettingsHelper.cs` 中，把 `UpdateMaxRecord` 方法整体替换为：

```csharp
        public static void UpdateMaxRecord(this Settings settings, string key, float val)
        {
            bool changed = false;
            if (val <= 0 || float.IsNaN(val) || float.IsInfinity(val)) return;
            
            if (key.Contains("Clock") && val > 10000) return; 
            if (key.Contains("Power") && val > 1000) return;
            if ((key.Contains("Fan") || key.Contains("Pump")) && val > 10000) return;

            // ★★★ [多显卡] 带序号的 Key ("GPU.1.Power") 归一化成基础 Key ("GPU.Power") 后再比对 ★★★
            string baseKey = GpuKey.ToBaseKey(key);

            if (baseKey == "CPU.Power" && val > settings.RecordedMaxCpuPower) { settings.RecordedMaxCpuPower = val; changed = true; }
            else if (baseKey == "CPU.Clock" && val > settings.RecordedMaxCpuClock) { settings.RecordedMaxCpuClock = val; changed = true; }
            else if (baseKey == "GPU.Power" && val > settings.RecordedMaxGpuPower) { settings.RecordedMaxGpuPower = val; changed = true; }
            else if (baseKey == "GPU.Clock" && val > settings.RecordedMaxGpuClock) { settings.RecordedMaxGpuClock = val; changed = true; }
            
            else if (baseKey == "CPU.Fan" && val > settings.RecordedMaxCpuFan) { settings.RecordedMaxCpuFan = val; changed = true; }
            else if (baseKey == "CPU.Pump" && val > settings.RecordedMaxCpuPump) { settings.RecordedMaxCpuPump = val; changed = true; }
            else if (baseKey == "GPU.Fan" && val > settings.RecordedMaxGpuFan) { settings.RecordedMaxGpuFan = val; changed = true; }
            else if (baseKey == "CASE.Fan" && val > settings.RecordedMaxChassisFan) { settings.RecordedMaxChassisFan = val; changed = true; }

            // ★★★ [多显卡] 每张卡单独记一份峰值, 让各自的进度条独立自适应 ★★★
            if (key != baseKey)
            {
                if (!settings.RecordedMaxGpuDynamic.TryGetValue(key, out float cur) || val > cur)
                {
                    settings.RecordedMaxGpuDynamic[key] = val;
                    changed = true;
                }
            }

            if (changed && (DateTime.Now - settings.LastAutoSaveTime).TotalSeconds > 30)
            {
                settings.Save();
                settings.LastAutoSaveTime = DateTime.Now;
            }
        }
```

- [ ] **Step 4: 编译并跑测试**

```powershell
$dotnet build 'G:\TOOL\LiteMonitor-fork\LiteMonitor.csproj' -c Release -v minimal
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 编译 0 错误；测试全部通过

- [ ] **Step 5: 提交**

```powershell
git add src/Core/Settings.cs src/Core/SettingsHelper.cs
git commit -m "feat(gpu): 新增多显卡配置项并在配置加载时同步监控项

新增 MonitoredGpuCount / RecordedMaxGpuDynamic / DetectedGpuVramTotalGBByKey;
UpdateMaxRecord 改为按归一化后的基础 Key 比对, 并按卡单独记录峰值。"
```

---

## Task 6: SensorMap 多卡映射核心（同时修 GPU.Fan 串卡）

**Files:**
- Modify: `src/System/HardwareServices/SensorMap.cs`

**Interfaces:**
- Consumes: `GpuKey`（Task 2）、`GpuRegistry`（Task 2）、`HardwareRules.GetShortGpuName`（Task 3）
- Produces:
  - `SensorMap.CachedGpus : List<IHardware>`（被监控显卡，按序号排列）
  - `SensorMap.GetGpuByIndex(int index) -> IHardware?`

- [ ] **Step 1: 新增 CachedGpus 字段与访问器**

在 `src/System/HardwareServices/SensorMap.cs` 中，把：

```csharp
        // 显卡硬件缓存 (用于快速定位)
        public IHardware? CachedGpu { get; private set; }
```

替换为：

```csharp
        // 显卡硬件缓存 (用于快速定位)
        public IHardware? CachedGpu { get; private set; }

        // ★★★ [多显卡] 被监控的显卡列表, 下标即 Key 中的序号 ★★★
        public List<IHardware> CachedGpus { get; private set; } = new List<IHardware>();

        /// <summary>取指定序号的被监控显卡, 越界返回 null</summary>
        public IHardware? GetGpuByIndex(int index)
        {
            lock (_lock)
            {
                if (index < 0 || index >= CachedGpus.Count) return null;
                return CachedGpus[index];
            }
        }
```

- [ ] **Step 2: 在 Clear 中一并清理**

在 `Clear()` 方法里，把：

```csharp
                CachedGpu = null;
                CpuBusSpeedSensor = null;
```

替换为：

```csharp
                CachedGpu = null;
                CachedGpus = new List<IHardware>();
                CpuBusSpeedSensor = null;
                GpuRegistry.Clear();
```

- [ ] **Step 3: 新增选卡方法 SelectMonitoredGpus**

在 `src/System/HardwareServices/SensorMap.cs` 的 `Rebuild` 方法之前插入：

```csharp
        /// <summary>
        /// 选出被监控的显卡集合。
        /// 规则:
        ///   - MonitoredGpuCount == 1 : 沿用旧逻辑, 优先用 PreferredGpu 指定的卡, 否则取优先级最高的一张
        ///   - MonitoredGpuCount >= 2 : 按优先级取前 N 张
        /// 顺序与 Rebuild 中的遍历顺序一致, 保证 Key 中的序号稳定。
        /// </summary>
        private static List<IHardware> SelectMonitoredGpus(Computer computer, Settings cfg)
        {
            var all = computer.Hardware
                .Where(HardwareScanner.IsGpuHardware)
                .OrderBy(h => HardwareRules.GetHwPriority(h))
                .ToList();

            if (all.Count == 0) return new List<IHardware>();

            int want = cfg.MonitoredGpuCount;
            if (want < 1) want = 1;

            if (want == 1)
            {
                var selected = HardwareScanner.FindGpuByConfig(computer, cfg.PreferredGpu ?? "");
                if (selected != null) return new List<IHardware> { selected };
                return new List<IHardware> { all[0] };
            }

            return all.Take(Math.Min(want, all.Count)).ToList();
        }

        /// <summary>
        /// 在被监控显卡列表中按引用查找序号, 找不到返回 -1。
        /// 这里刻意不用 Dictionary + ReferenceEqualityComparer —— 该比较器只实现了
        /// IEqualityComparer&lt;object&gt;, 不能直接用于 Dictionary&lt;IHardware, int&gt;。
        /// </summary>
        private static int FindGpuIndex(List<IHardware> monitored, IHardware hw)
        {
            for (int i = 0; i < monitored.Count; i++)
            {
                if (ReferenceEquals(monitored[i], hw)) return i;
            }
            return -1;
        }
```

> 需要 `using System.Collections.Generic;`（文件已有）与 `using System.Linq;`（文件已有）。

- [ ] **Step 4: 改造 Rebuild 的选卡与注册逻辑**

在 `Rebuild` 方法中，把：

```csharp
            string preferredGpu = cfg.PreferredGpu ?? "";
            IHardware? selectedGpu = HardwareScanner.FindGpuByConfig(computer, preferredGpu);
```

替换为：

```csharp
            // ★★★ [多显卡] 选出被监控的显卡集合 (序号即 Key 中的序号) ★★★
            var monitoredGpus = SelectMonitoredGpus(computer, cfg);
```

- [ ] **Step 5: 改造 RegisterTo 的签名与显卡分支**

在 `Rebuild` 中，把：

```csharp
            // 局部递归函数
            void RegisterTo(IHardware hw)
            {
                //hw.Update();

                bool isGpuHardware = HardwareScanner.IsGpuHardware(hw);

                // 用户选择的显卡仍存在时只映射指定显卡；配置失效或旧名称无法唯一定位时回退自动优先级。
                if (isGpuHardware &&
                    selectedGpu != null &&
                    !ReferenceEquals(hw, selectedGpu))
                {
                    return;
                }
```

替换为：

```csharp
            // 局部递归函数
            void RegisterTo(IHardware hw, int gpuIndex)
            {
                //hw.Update();

                bool isGpuHardware = HardwareScanner.IsGpuHardware(hw);

                // ★★★ [多显卡] 只有在监控集合里的显卡才继续处理 ★★★
                if (isGpuHardware && gpuIndex < 0) return;
```

- [ ] **Step 6: 改造 GPU 缓存与风扇映射分支**

在 `RegisterTo` 内，把：

```csharp
                // --- 填充 GPU 缓存 (优化版：完全依赖优先级排序) ---
                if (isGpuHardware)
                {
                    // 因为外层循环已经按照 GetHwPriority 排序（强力显卡在前），
                    // 所以第一个遇到的显卡就是最强的，直接锁定即可。
                    // 移除了复杂的“后来者居上”判断逻辑，避免过度设计。
                    if (newGpu == null)
                    {
                        newGpu = hw;
                    }
                    
                    // ★★★ [新增] GPU 风扇映射 ★★★
                    var fan = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan);
                    if (fan == null) fan = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Control);
                    if (fan != null) newMap["GPU.Fan"] = fan;
                }
```

替换为：

```csharp
                // --- 填充 GPU 缓存 (优化版：完全依赖优先级排序) ---
                if (isGpuHardware)
                {
                    if (newGpu == null)
                    {
                        newGpu = hw;
                    }

                    // ★★★ [多显卡] 登记显卡简称, 供 UI 打标签 ★★★
                    GpuRegistry.Set(gpuIndex, HardwareRules.GetShortGpuName(hw.Name), hw.Name);

                    // ★★★ [多显卡] GPU 风扇映射 (按序号写各自的 Key) ★★★
                    // 修复: 旧代码这里是无条件赋值 newMap["GPU.Fan"] = fan,
                    //       多卡时后一张卡会覆盖前一张, 导致风扇读数串卡。
                    //       现在每张卡写进自己的 "GPU.{序号}.Fan", 互不干扰。
                    var fan = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan);
                    if (fan == null) fan = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Control);
                    if (fan != null)
                    {
                        string fanKey = GpuKey.Build(gpuIndex, "Fan");
                        if (!newMap.ContainsKey(fanKey)) newMap[fanKey] = fan;

                        // 第 0 张卡额外写入旧格式 Key, 保证旧配置继续可用
                        if (gpuIndex == 0 && !newMap.ContainsKey("GPU.Fan")) newMap["GPU.Fan"] = fan;
                    }
                }
```

- [ ] **Step 7: 改造普通传感器映射以带上序号**

在 `RegisterTo` 内，把：

```csharp
                // --- 普通传感器映射 ---
                foreach (var s in hw.Sensors)
                {
                    string? key = SensorMatcher.Match(hw, s); // 调用 SensorMatcher 进行识别
                    if (!string.IsNullOrEmpty(key))
                    {
                        if (!newMap.ContainsKey(key))
                        {
                            newMap[key] = s;
                        }
                        else
                        {
                            // [Fix] 冲突解决策略：优先选择 Vendor (非 D3D) 传感器
                            // 必须严格保护高优先级显卡的数据，防止被低优先级显卡覆盖！
                            var existing = newMap[key];
                            
                            // 1. 如果现有数据来自"更强"的显卡 (Priority数值更小)，绝对禁止覆盖！
                            // (注意：RegisterTo 按照 0->100 顺序遍历，所以 existing 的优先级只可能 <= 当前 hw 的优先级)
                            // 如果 existing < hw，说明它是更强的显卡写入的，直接跳过。
                            if (HardwareRules.GetHwPriority(existing.Hardware) < HardwareRules.GetHwPriority(hw))
                            {
                                continue;
                            }

                            // 2. 如果优先级相同 (通常是同一张显卡的不同传感器，或者是两张同样的显卡)，
                            // 则应用 D3D vs Vendor 优选逻辑。
                            bool existingIsD3D = existing.Name.Contains("D3D", StringComparison.OrdinalIgnoreCase);
                            bool newIsD3D = s.Name.Contains("D3D", StringComparison.OrdinalIgnoreCase);
                            
                            // 如果现有的是 D3D 而新来的不是 D3D -> 替换为新来的 (Upgrade)
                            if (existingIsD3D && !newIsD3D)
                            {
                                newMap[key] = s;
                            }
                            // 其他情况 (Vendor vs Vendor, Vendor vs D3D) -> 保持现有 (First Wins / Vendor Wins)
                        }
                    }
                }

                foreach (var sub in hw.SubHardware) RegisterTo(sub);
            }

            // 按优先级排序并注册
            var ordered = computer.Hardware.OrderBy(h => HardwareRules.GetHwPriority(h));
            foreach (var hw in ordered) RegisterTo(hw);
```

替换为：

```csharp
                // --- 普通传感器映射 ---
                foreach (var s in hw.Sensors)
                {
                    string? key = SensorMatcher.Match(hw, s); // 调用 SensorMatcher 进行识别
                    if (!string.IsNullOrEmpty(key))
                    {
                        // ★★★ [多显卡] 显卡指标改写成带序号的形式 ★★★
                        bool isGpuKey = isGpuHardware
                                        && key.StartsWith(GpuKey.Prefix, StringComparison.OrdinalIgnoreCase);
                        string finalKey = key;
                        if (isGpuKey)
                        {
                            string metric = key.Substring(GpuKey.Prefix.Length);
                            finalKey = GpuKey.Build(gpuIndex, metric);
                        }

                        if (!newMap.ContainsKey(finalKey))
                        {
                            newMap[finalKey] = s;
                        }
                        else
                        {
                            // [Fix] 冲突解决策略：优先选择 Vendor (非 D3D) 传感器
                            // 必须严格保护高优先级显卡的数据，防止被低优先级显卡覆盖！
                            var existing = newMap[finalKey];
                            
                            // 1. 如果现有数据来自"更强"的显卡 (Priority数值更小)，绝对禁止覆盖！
                            if (HardwareRules.GetHwPriority(existing.Hardware) < HardwareRules.GetHwPriority(hw))
                            {
                                continue;
                            }

                            // 2. 如果优先级相同，则应用 D3D vs Vendor 优选逻辑。
                            bool existingIsD3D = existing.Name.Contains("D3D", StringComparison.OrdinalIgnoreCase);
                            bool newIsD3D = s.Name.Contains("D3D", StringComparison.OrdinalIgnoreCase);
                            
                            // 如果现有的是 D3D 而新来的不是 D3D -> 替换为新来的 (Upgrade)
                            if (existingIsD3D && !newIsD3D)
                            {
                                newMap[finalKey] = s;
                            }
                            // 其他情况 (Vendor vs Vendor, Vendor vs D3D) -> 保持现有 (First Wins / Vendor Wins)
                        }

                        // ★★★ [多显卡] 第 0 张卡额外写入旧格式 Key, 兼容旧配置 ★★★
                        if (isGpuKey && gpuIndex == 0 && !newMap.ContainsKey(key))
                        {
                            newMap[key] = s;
                        }
                    }
                }

                foreach (var sub in hw.SubHardware) RegisterTo(sub, gpuIndex);
            }

            // 按优先级排序并注册
            var ordered = computer.Hardware.OrderBy(h => HardwareRules.GetHwPriority(h));
            foreach (var hw in ordered)
            {
                int gpuIndex = FindGpuIndex(monitoredGpus, hw);
                RegisterTo(hw, gpuIndex);
            }
```

- [ ] **Step 8: 在原子交换处写入 CachedGpus**

在 `Rebuild` 的原子交换块中，把：

```csharp
                CpuCoreCache = newCpuCache;
                CachedGpu = newGpu;
                CpuBusSpeedSensor = newBusSensor; // ★ 更新 Bus Sensor 缓存
```

替换为：

```csharp
                CpuCoreCache = newCpuCache;
                CachedGpu = newGpu;
                CachedGpus = new List<IHardware>(monitoredGpus); // ★★★ [多显卡] ★★★
                CpuBusSpeedSensor = newBusSensor; // ★ 更新 Bus Sensor 缓存
```

- [ ] **Step 9: 编译验证**

```powershell
$dotnet build 'G:\TOOL\LiteMonitor-fork\LiteMonitor.csproj' -c Release -v minimal
```

Expected: 0 错误。**特别确认**没有 `CS0165 使用了未赋值的局部变量 "selectedGpu"` 之类残留（Step 4 已把 `selectedGpu` 的声明删掉，Step 7 也不再引用它）。

- [ ] **Step 10: 提交**

```powershell
git add src/System/HardwareServices/SensorMap.cs
git commit -m "feat(gpu): SensorMap 支持多显卡映射并修复风扇串卡

每张被监控显卡写入独立的 GPU.{序号}.{指标} 映射; 顺带修复旧代码里
newMap[\"GPU.Fan\"] 无条件赋值导致多卡时风扇读数被后一张卡覆盖的问题。"
```

---

## Task 7: ComponentProcessor 按序号取频率与功耗

**Files:**
- Modify: `src/System/HardwareServices/ComponentProcessor.cs`

**Interfaces:**
- Consumes: `GpuKey.TryParse`（Task 2）、`SensorMap.GetGpuByIndex`（Task 6）
- Produces: `ComponentProcessor.GetCompositeValue` 支持 `GPU.{序号}.Clock` / `GPU.{序号}.Power`

- [ ] **Step 1: 改造 GPU 分支**

在 `src/System/HardwareServices/ComponentProcessor.cs` 中，把：

```csharp
            if (key.StartsWith("GPU"))
            {
                var gpu = _sensorMap.CachedGpu;
                if (gpu == null) return null;
                if (key == "GPU.Clock")
                {
                    var s = gpu.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Clock && (SensorMap.Has(x.Name, "graphics") || SensorMap.Has(x.Name, "core") || SensorMap.Has(x.Name, "shader")));
                    if (s != null && s.Value.HasValue) 
                    { 
                        float val = s.Value.Value; 
                        if (val > 6000.0f) return null; 
                        _cfg.UpdateMaxRecord(key, val); 
                        return val; 
                    }
                }
                else if (key == "GPU.Power")
                {
                    var s = gpu.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Power && (SensorMap.Has(x.Name, "package") || SensorMap.Has(x.Name, "ppt") || SensorMap.Has(x.Name, "board") || SensorMap.Has(x.Name, "core") || SensorMap.Has(x.Name, "gpu")));
                    if (s != null && s.Value.HasValue) 
                    { 
                        float val = s.Value.Value; 
                        if (val > 1200.0f) return null; 
                        _cfg.UpdateMaxRecord(key, val); 
                        return val; 
                    }
                }
            }
```

替换为：

```csharp
            if (key.StartsWith("GPU", StringComparison.OrdinalIgnoreCase))
            {
                // ★★★ [多显卡] 解析出显卡序号, 取对应那张卡的传感器 ★★★
                int gpuIndex = -1;
                string metric = key.Substring(GpuKey.Prefix.Length);
                if (GpuKey.TryParse(key, out int parsedIndex, out string parsedMetric))
                {
                    gpuIndex = parsedIndex;
                    metric = parsedMetric;
                }

                var gpu = gpuIndex >= 0 ? _sensorMap.GetGpuByIndex(gpuIndex) : _sensorMap.CachedGpu;
                if (gpu == null) return null;

                if (metric.Equals("Clock", StringComparison.OrdinalIgnoreCase))
                {
                    var s = gpu.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Clock && (SensorMap.Has(x.Name, "graphics") || SensorMap.Has(x.Name, "core") || SensorMap.Has(x.Name, "shader")));
                    if (s != null && s.Value.HasValue) 
                    { 
                        float val = s.Value.Value; 
                        if (val > 6000.0f) return null; 
                        _cfg.UpdateMaxRecord(key, val); 
                        return val; 
                    }
                }
                else if (metric.Equals("Power", StringComparison.OrdinalIgnoreCase))
                {
                    var s = gpu.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Power && (SensorMap.Has(x.Name, "package") || SensorMap.Has(x.Name, "ppt") || SensorMap.Has(x.Name, "board") || SensorMap.Has(x.Name, "core") || SensorMap.Has(x.Name, "gpu")));
                    if (s != null && s.Value.HasValue) 
                    { 
                        float val = s.Value.Value; 
                        if (val > 1200.0f) return null; 
                        _cfg.UpdateMaxRecord(key, val); 
                        return val; 
                    }
                }
            }
```

- [ ] **Step 2: 编译并跑测试**

```powershell
$dotnet build 'G:\TOOL\LiteMonitor-fork\LiteMonitor.csproj' -c Release -v minimal
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 编译 0 错误，测试全通过

- [ ] **Step 3: 提交**

```powershell
git add src/System/HardwareServices/ComponentProcessor.cs
git commit -m "feat(gpu): 频率与功耗按显卡序号取对应显卡的传感器"
```

---

## Task 8: 显存按卡计算

**Files:**
- Modify: `src/System/HardwareServices/HardwareValueProvider.cs`
- Modify: `src/Core/MetricUtils.cs`

**Interfaces:**
- Consumes: `GpuKey.TryParse`（Task 2）、`Settings.DetectedGpuVramTotalGBByKey`（Task 5）
- Produces:
  - `HardwareValueProvider.GetGpuVramPercent(string key) -> float?`（private）
  - `MetricUtils.GetGpuVramTotalGB(string key) -> double`（private static）

- [ ] **Step 1: 改造 HardwareValueProvider 的显存分支**

在 `src/System/HardwareServices/HardwareValueProvider.cs` 的 `GetValue` 方法中，把：

```csharp
                    // 7. 显存
                    case "GPU.VRAM":
                        float? used = GetValue("GPU.VRAM.Used");
                        float? total = GetValue("GPU.VRAM.Total");
                        if (used.HasValue && total.HasValue && total > 0)
                        {
                            if (Settings.DetectedGpuVramTotalGB <= 0) Settings.DetectedGpuVramTotalGB = total.Value / 1024f;
                            if (total > 10485760) { used /= 1048576f; total /= 1048576f; }
                            result = used / total * 100f;
                        }
                        else
                        {
                            if (_manualSensorCache.TryGetValue("GPU.VRAM.Load", out var s) && s.Value.HasValue) result = s.Value;
                        }
                        break;
```

替换为：

```csharp
                    // 7. 显存
                    case "GPU.VRAM":
                        result = GetGpuVramPercent(key);
                        break;
```

- [ ] **Step 2: 在 default 分支加带序号的显存处理**

在 `GetValue` 的 `default:` 分支中，把：

```csharp
                        else if (key.Contains("Clock") || key.Contains("Power"))
                        {
                            if (useCounter && key == "CPU.Clock")
                            {
                                result = _perfManager.GetCpuFreq();
                            }
                            
                            if (result == null)
                            {
                                result = _componentProcessor.GetCompositeValue(key, _manualSensorCache);
                            }
                        }
                        break;
```

替换为：

```csharp
                        else if (key.Contains("Clock") || key.Contains("Power"))
                        {
                            if (useCounter && key == "CPU.Clock")
                            {
                                result = _perfManager.GetCpuFreq();
                            }
                            
                            if (result == null)
                            {
                                result = _componentProcessor.GetCompositeValue(key, _manualSensorCache);
                            }
                        }
                        else if (GpuKey.TryParse(key, out _, out string gpuMetric)
                                 && gpuMetric.Equals("VRAM", StringComparison.OrdinalIgnoreCase))
                        {
                            // ★★★ [多显卡] "GPU.1.VRAM" 等带序号的显存项 ★★★
                            result = GetGpuVramPercent(key);
                        }
                        break;
```

- [ ] **Step 3: 新增 GetGpuVramPercent 方法**

在 `src/System/HardwareServices/HardwareValueProvider.cs` 中，把：

```csharp
        private float? ReadMoboTemperature(ISensor sensor)
```

替换为：

```csharp
        /// <summary>
        /// 计算显存占用百分比。
        /// key 形如 "GPU.VRAM" (旧) 或 "GPU.1.VRAM" (多卡), 由此派生出 Used / Total 子 Key。
        /// </summary>
        private float? GetGpuVramPercent(string key)
        {
            // "GPU.VRAM" -> "GPU." ; "GPU.1.VRAM" -> "GPU.1."
            string prefix = key.Substring(0, key.Length - "VRAM".Length);

            float? used = GetValue(prefix + "VRAM.Used");
            float? total = GetValue(prefix + "VRAM.Total");
            if (used.HasValue && total.HasValue && total > 0)
            {
                // ★★★ 单位归一化: LHM 0.9.x 报的是 MB, 新版 LHM 报的是字节 ★★★
                // 阈值 10485760 MB 实际不可能出现, 所以命中即代表单位是字节
                if (total > 10485760) { used /= 1048576f; total /= 1048576f; }

                // 现在 total 的单位是 MB
                float gb = total.Value / 1024f;

                // 旧单卡缓存 (保留原有语义)
                if (Settings.DetectedGpuVramTotalGB <= 0 && gb > 0 && gb < 1024)
                {
                    Settings.DetectedGpuVramTotalGB = gb;
                }

                // ★★★ [多显卡] 每张卡单独记录自己的显存总容量 ★★★
                string cardKey = prefix.TrimEnd('.');
                if (gb > 0 && gb < 1024 && !Settings.DetectedGpuVramTotalGBByKey.ContainsKey(cardKey))
                {
                    Settings.DetectedGpuVramTotalGBByKey[cardKey] = gb;
                }

                return used / total * 100f;
            }

            if (_manualSensorCache.TryGetValue(prefix + "VRAM.Load", out var s) && s.Value.HasValue) return s.Value;
            return null;
        }

        private float? ReadMoboTemperature(ISensor sensor)
```

- [ ] **Step 4: 改造 MetricUtils 的容量模式取值**

在 `src/Core/MetricUtils.cs` 的 `GetValueStr` 方法中，把：

```csharp
                 var cfg = Settings.Load();
                 if (cfg.MemoryDisplayMode == 1) // 容量模式
                 {
                     double totalGB = (key.IndexOf("MEM", StringComparison.OrdinalIgnoreCase) >= 0) 
                        ? Settings.DetectedRamTotalGB 
                        : Settings.DetectedGpuVramTotalGB;

                     if (totalGB > 0)
                     {
                         // 转换为字节 -> 格式化 -> 取数值部分
                         // [Refactor] 使用自适应格式化 (-1)，确保 100GB 时不显示 .0
                         return FormatDataSizeParts((v / 100.0) * totalGB * 1073741824.0, -1).val;
                     }
                 }
```

替换为：

```csharp
                 var cfg = Settings.Load();
                 if (cfg.MemoryDisplayMode == 1) // 容量模式
                 {
                     double totalGB = (key.IndexOf("MEM", StringComparison.OrdinalIgnoreCase) >= 0) 
                        ? Settings.DetectedRamTotalGB 
                        : GetGpuVramTotalGB(key);

                     if (totalGB > 0)
                     {
                         // 转换为字节 -> 格式化 -> 取数值部分
                         // [Refactor] 使用自适应格式化 (-1)，确保 100GB 时不显示 .0
                         return FormatDataSizeParts((v / 100.0) * totalGB * 1073741824.0, -1).val;
                     }
                 }
```

- [ ] **Step 5: 改造 MetricUtils 的单位取值**

在 `src/Core/MetricUtils.cs` 的 `GetUnitStr` 方法中，把：

```csharp
                double totalGB = (key.IndexOf("MEM", StringComparison.OrdinalIgnoreCase) >= 0) ? Settings.DetectedRamTotalGB : Settings.DetectedGpuVramTotalGB;
```

替换为：

```csharp
                double totalGB = (key.IndexOf("MEM", StringComparison.OrdinalIgnoreCase) >= 0) ? Settings.DetectedRamTotalGB : GetGpuVramTotalGB(key);
```

- [ ] **Step 6: 新增 GetGpuVramTotalGB 辅助方法**

在 `src/Core/MetricUtils.cs` 中，把：

```csharp
        public static double GetAdaptivePercentage(string key, double val)
```

替换为：

```csharp
        /// <summary>
        /// 取指定 Key 对应显卡的显存总容量 (GB)。
        /// "GPU.1.VRAM" -> 查 "GPU.1" ; "GPU.VRAM" -> 查 "GPU" (旧格式回退)
        /// </summary>
        private static double GetGpuVramTotalGB(string key)
        {
            string prefix = key ?? "";
            int vramPos = prefix.LastIndexOf("VRAM", StringComparison.OrdinalIgnoreCase);
            if (vramPos > 0) prefix = prefix.Substring(0, vramPos);
            prefix = prefix.TrimEnd('.');

            if (Settings.DetectedGpuVramTotalGBByKey.TryGetValue(prefix, out float total) && total > 0)
                return total;

            return Settings.DetectedGpuVramTotalGB; // 旧单卡缓存回退
        }

        public static double GetAdaptivePercentage(string key, double val)
```

- [ ] **Step 7: 编译并跑测试**

```powershell
$dotnet build 'G:\TOOL\LiteMonitor-fork\LiteMonitor.csproj' -c Release -v minimal
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 编译 0 错误，测试全通过

- [ ] **Step 8: 提交**

```powershell
git add src/System/HardwareServices/HardwareValueProvider.cs src/Core/MetricUtils.cs
git commit -m "feat(gpu): 显存占用按卡独立计算

带序号的显存 Key 派生出各自的 Used/Total 子 Key; 容量模式按每张卡自己的
显存总容量换算, 并把单位归一化提到换算之前, 同时兼容 MB 与字节两种单位。"
```

---

## Task 9: 阈值自适应按卡归一化

**Files:**
- Modify: `src/Core/MetricUtils.cs`（`GetAdaptivePercentage`）
- Create: `tests/LiteMonitor.Tests/MetricUtilsGpuKeyTests.cs`

**Interfaces:**
- Consumes: `GpuKey.ToBaseKey`（Task 2）、`Settings.RecordedMaxGpuDynamic`（Task 5）
- Produces: `MetricUtils.GetAdaptivePercentage(string key, double val)` 对 `GPU.{序号}.*` 返回按该卡峰值算出的比例

- [ ] **Step 1: 写失败测试**

写入 `G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\MetricUtilsGpuKeyTests.cs`：

```csharp
using LiteMonitor.src.Core;
using Xunit;

namespace LiteMonitor.Tests
{
    public class MetricUtilsGpuKeyTests
    {
        [Theory]
        [InlineData("GPU.0.Temp", MetricType.Temperature)]
        [InlineData("GPU.1.Temp", MetricType.Temperature)]
        [InlineData("GPU.1.VRAM", MetricType.Memory)]
        [InlineData("GPU.1.Load", MetricType.Percent)]
        [InlineData("GPU.1.Power", MetricType.Power)]
        [InlineData("GPU.1.Clock", MetricType.Frequency)]
        [InlineData("GPU.1.Fan", MetricType.RPM)]
        public void GetType_带序号的Key_类型判定不变(string key, MetricType expected)
        {
            Assert.Equal(expected, MetricUtils.GetType(key));
        }

        [Fact]
        public void GetAdaptivePercentage_带序号的Key_使用该卡自己的峰值()
        {
            var cfg = Settings.Load();
            cfg.RecordedMaxGpuDynamic["GPU.1.Power"] = 200f;

            double pct = MetricUtils.GetAdaptivePercentage("GPU.1.Power", 100.0);
            Assert.Equal(0.5, pct, 3);

            // 另一张卡没有单独峰值时, 走全局 RecordedMaxGpuPower
            cfg.RecordedMaxGpuDynamic.Remove("GPU.2.Power");
            cfg.RecordedMaxGpuPower = 400f;
            double pct2 = MetricUtils.GetAdaptivePercentage("GPU.2.Power", 100.0);
            Assert.Equal(0.25, pct2, 3);
        }
    }
}
```

> 注：测试依赖 `Settings.Load()` 单例。若后续 Task 引入会污染单例的用例，需在测试里 `cfg.RecordedMaxGpuDynamic.Clear()` 复位。

- [ ] **Step 2: 运行测试确认失败**

```powershell
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: `GetAdaptivePercentage_带序号的Key_使用该卡自己的峰值` 失败（当前实现对 `GPU.1.Power` 取不到 max，返回 1.0 而非 0.5）

- [ ] **Step 3: 改造 GetAdaptivePercentage**

在 `src/Core/MetricUtils.cs` 中，把：

```csharp
        public static double GetAdaptivePercentage(string key, double val)
        {
            var cfg = Settings.Load();
            float max = 1.0f;

            if (key == "CPU.Clock") max = cfg.RecordedMaxCpuClock;
            else if (key == "CPU.Power") max = cfg.RecordedMaxCpuPower;
            else if (key == "GPU.Clock") max = cfg.RecordedMaxGpuClock;
            else if (key == "GPU.Power") max = cfg.RecordedMaxGpuPower;
            else if (key == "CPU.Fan") max = cfg.RecordedMaxCpuFan;
            else if (key == "CPU.Pump") max = cfg.RecordedMaxCpuPump;
            else if (key == "CASE.Fan") max = cfg.RecordedMaxChassisFan;
            else if (key == "GPU.Fan") max = cfg.RecordedMaxGpuFan;
            else if (key == "FPS") max = cfg.RecordedMaxFps;
            else if (key == "CPU.Voltage") max = 1.6f;
            else if (key == "BAT.Power") max = 100f;
            else if (key == "BAT.Voltage") max = 20f;
            else if (key == "BAT.Current") max = 5f;

            if (max < 1) max = 1;
            double pct = val / max;
            return pct > 1.0 ? 1.0 : pct;
        }
```

替换为：

```csharp
        public static double GetAdaptivePercentage(string key, double val)
        {
            var cfg = Settings.Load();
            float max = 1.0f;

            // ★★★ [多显卡] 带序号的 Key ("GPU.1.Power") 归一化成基础 Key ("GPU.Power") ★★★
            string baseKey = GpuKey.ToBaseKey(key);

            if (baseKey == "CPU.Clock") max = cfg.RecordedMaxCpuClock;
            else if (baseKey == "CPU.Power") max = cfg.RecordedMaxCpuPower;
            else if (baseKey == "GPU.Clock") max = cfg.RecordedMaxGpuClock;
            else if (baseKey == "GPU.Power") max = cfg.RecordedMaxGpuPower;
            else if (baseKey == "CPU.Fan") max = cfg.RecordedMaxCpuFan;
            else if (baseKey == "CPU.Pump") max = cfg.RecordedMaxCpuPump;
            else if (baseKey == "CASE.Fan") max = cfg.RecordedMaxChassisFan;
            else if (baseKey == "GPU.Fan") max = cfg.RecordedMaxGpuFan;
            else if (baseKey == "FPS") max = cfg.RecordedMaxFps;
            else if (baseKey == "CPU.Voltage") max = 1.6f;
            else if (baseKey == "BAT.Power") max = 100f;
            else if (baseKey == "BAT.Voltage") max = 20f;
            else if (baseKey == "BAT.Current") max = 5f;

            // ★★★ [多显卡] 该卡有独立峰值时优先使用, 让每张卡的进度条各自自适应 ★★★
            if (key != baseKey
                && cfg.RecordedMaxGpuDynamic != null
                && cfg.RecordedMaxGpuDynamic.TryGetValue(key, out float dynMax)
                && dynMax > 1f)
            {
                max = dynMax;
            }

            if (max < 1) max = 1;
            double pct = val / max;
            return pct > 1.0 ? 1.0 : pct;
        }
```

- [ ] **Step 4: 运行测试确认通过**

```powershell
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 全部通过

- [ ] **Step 5: 提交**

```powershell
git add src/Core/MetricUtils.cs tests/LiteMonitor.Tests/MetricUtilsGpuKeyTests.cs
git commit -m "fix(gpu): 阈值自适应对带序号的 Key 做归一化

否则 GPU.{序号}.Power 匹配不到全局最大值, 进度条会恒满且恒为红色。"
```

---

## Task 10: 多卡标签

**Files:**
- Modify: `src/Core/MetricLabelResolver.cs`

**Interfaces:**
- Consumes: `GpuKey.TryParse`（Task 2）、`GpuRegistry.GetShortName`（Task 2）
- Produces:
  - `MetricLabelResolver.ResolveLabel` 对 `GPU.{序号}.{指标}` 返回 `"{显卡简称} {基础标签}"`
  - `MetricLabelResolver.ResolveShortLabel` 对 `GPU.{序号}.{指标}` 返回显卡简称

- [ ] **Step 1: 改造 ResolveLabel**

在 `src/Core/MetricLabelResolver.cs` 中，把：

```csharp
        public static string ResolveLabel(MonitorItemConfig config)
        {
            if (config == null) return "";

            // 1. 用户自定义 (最高优先级)
            if (!string.IsNullOrEmpty(config.UserLabel)) 
                return config.UserLabel;
            
            // 2. 运行时动态注入 (高优先级)
```

替换为：

```csharp
        public static string ResolveLabel(MonitorItemConfig config)
        {
            if (config == null) return "";

            // 1. 用户自定义 (最高优先级)
            if (!string.IsNullOrEmpty(config.UserLabel)) 
                return config.UserLabel;

            // 2. [多显卡] 标签带上显卡简称, 例如 "3080 Ti 温度"
            if (GpuKey.TryParse(config.Key, out int gpuIndex, out string metric))
            {
                string shortName = GpuRegistry.GetShortName(gpuIndex);
                if (!string.IsNullOrEmpty(shortName))
                {
                    string baseLabel = LanguageManager.T("Items." + GpuKey.BaseKey(metric));
                    return $"{shortName} {StripGpuPrefix(baseLabel)}".Trim();
                }
            }

            // 3. 运行时动态注入 (高优先级)
```

并把该方法剩余的注释编号顺延（原「3. 配置中的动态标签」改为「4.」，原「4. 默认/失败」改为「5.」）——**仅改注释，不改逻辑**。

- [ ] **Step 2: 改造 ResolveShortLabel**

在 `src/Core/MetricLabelResolver.cs` 中，把：

```csharp
        public static string ResolveShortLabel(MonitorItemConfig config)
        {
            if (config == null) return "";

            // 1. 用户自定义
            if (!string.IsNullOrEmpty(config.TaskbarLabel)) 
                return config.TaskbarLabel;

            // 2. 运行时动态注入
```

替换为：

```csharp
        public static string ResolveShortLabel(MonitorItemConfig config)
        {
            if (config == null) return "";

            // 1. 用户自定义
            if (!string.IsNullOrEmpty(config.TaskbarLabel)) 
                return config.TaskbarLabel;

            // 2. [多显卡] 任务栏用显卡简称区分 (数值单位已能区分指标: % / °C / W / GHz)
            if (GpuKey.TryParse(config.Key, out int gpuIndex, out _))
            {
                string shortName = GpuRegistry.GetShortName(gpuIndex);
                if (!string.IsNullOrEmpty(shortName)) return shortName;
            }

            // 3. 运行时动态注入
```

并把该方法剩余的注释编号顺延（原「3. 配置中的动态标签」改为「4.」）——**仅改注释，不改逻辑**。

- [ ] **Step 3: 新增 StripGpuPrefix 辅助方法**

在 `src/Core/MetricLabelResolver.cs` 的 `ResolveShortLabel` 方法之后、`class` 结束括号之前插入：

```csharp
        /// <summary>
        /// 去掉基础标签里的 "GPU" 前缀, 避免拼成 "3080 Ti GPU温度"。
        /// "GPU温度" -> "温度" ; "GPU Load" -> "Load" ; "显存占用" -> "显存占用"
        /// </summary>
        private static string StripGpuPrefix(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return "";
            string t = label.Trim();
            if (t.StartsWith("GPU", StringComparison.OrdinalIgnoreCase))
            {
                t = t.Substring(3).Trim();
            }
            return t.Length > 0 ? t : label.Trim();
        }
```

- [ ] **Step 4: 确认 using 完整**

`src/Core/MetricLabelResolver.cs` 顶部需要 `using System;`（用到了 `StringComparison`）与 `using LiteMonitor.src.Core;`（同命名空间，可省）。若编译报 `StringComparison` 未找到，补 `using System;`。

- [ ] **Step 5: 编译并跑测试**

```powershell
$dotnet build 'G:\TOOL\LiteMonitor-fork\LiteMonitor.csproj' -c Release -v minimal
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 编译 0 错误，测试全通过

- [ ] **Step 6: 提交**

```powershell
git add src/Core/MetricLabelResolver.cs
git commit -m "feat(gpu): 多显卡监控项标签带上显卡简称

主界面显示 \"3080 Ti 温度\" 这种形式, 任务栏简称用显卡名区分, 单位仍能区分指标。"
```

---

## Task 11: 历史记录支持多卡

**Files:**
- Modify: `src/Core/HardwareHistoryLogger.cs`

**Interfaces:**
- Consumes: `GpuKey.TryParse`（Task 2）
- Produces: `HardwareHistoryLogger.GetEnabledSupportedKeys` 额外产出带序号的显卡 Key（去重）

- [ ] **Step 1: 改造 GetEnabledSupportedKeys**

在 `src/Core/HardwareHistoryLogger.cs` 中，把：

```csharp
        private static IEnumerable<string> GetEnabledSupportedKeys(Settings cfg)
        {
            foreach (string key in _supportedKeys)
            {
                bool enabled = cfg.MonitorItems.Any(item =>
                    item.Key.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                    (item.VisibleInPanel || item.VisibleInTaskbar));

                if (enabled) yield return key;
            }
        }
```

替换为：

```csharp
        private static IEnumerable<string> GetEnabledSupportedKeys(Settings cfg)
        {
            // 用 HashSet 去重 —— 同一个 key 出现两次会让采样值被重复累加
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string key in _supportedKeys)
            {
                bool enabled = cfg.MonitorItems.Any(item =>
                    item.Key.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                    (item.VisibleInPanel || item.VisibleInTaskbar));

                if (enabled) result.Add(key);
            }

            // ★★★ [多显卡] 带序号的显卡 Key (如 "GPU.1.Temp") 也要记录 ★★★
            foreach (var item in cfg.MonitorItems)
            {
                if (!(item.VisibleInPanel || item.VisibleInTaskbar)) continue;
                if (GpuKey.TryParse(item.Key, out _, out _)) result.Add(item.Key);
            }

            return result;
        }
```

- [ ] **Step 2: 确认 using 完整**

`src/Core/HardwareHistoryLogger.cs` 顶部需要 `using System.Collections.Generic;`（`HashSet`）。若编译报未找到，补上。

- [ ] **Step 3: 编译并跑测试**

```powershell
$dotnet build 'G:\TOOL\LiteMonitor-fork\LiteMonitor.csproj' -c Release -v minimal
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 编译 0 错误，测试全通过

- [ ] **Step 4: 提交**

```powershell
git add src/Core/HardwareHistoryLogger.cs
git commit -m "feat(gpu): 硬件历史记录支持多显卡 Key

用 HashSet 去重, 避免同一 key 被重复采样累加。"
```

---

## Task 12: 硬件刷新层支持多卡

**Files:**
- Modify: `src/System/HardwareMonitor.cs`
- Modify: `src/System/HardwareServices/HardwareValueProvider.cs`（`OnUpdateTickStarted` 感知显卡数量变化）

**Interfaces:**
- Consumes: `SensorMap.CachedGpus`（Task 6）、`Settings.MonitoredGpuCount`（Task 5）
- Produces: `HardwareMonitor.ShouldUpdateGpuHardware(IHardware hw)` 对所有被监控显卡返回 true

- [ ] **Step 1: 改造 ShouldUpdateGpuHardware**

在 `src/System/HardwareMonitor.cs` 中，把：

```csharp
        private bool ShouldUpdateGpuHardware(IHardware hw)
        {
            var activeGpu = _sensorMap.CachedGpu;
            if (activeGpu == null) return true;

            // 当前面板只有一组 GPU 指标，刷新非当前显卡只会增加启动和轮询耗时。
            return ReferenceEquals(hw, activeGpu);
        }
```

替换为：

```csharp
        private bool ShouldUpdateGpuHardware(IHardware hw)
        {
            var activeGpu = _sensorMap.CachedGpu;
            if (activeGpu != null && ReferenceEquals(hw, activeGpu)) return true;

            // ★★★ [多显卡] 更新所有被监控的显卡 ★★★
            foreach (var g in _sensorMap.CachedGpus)
            {
                if (ReferenceEquals(g, hw)) return true;
            }

            return false;
        }
```

- [ ] **Step 2: 在 HardwareValueProvider 感知显卡数量变化**

在 `src/System/HardwareServices/HardwareValueProvider.cs` 的字段区，把：

```csharp
        private string _lastPrefNet = "";
        private string _lastPrefGpu = "";
```

替换为：

```csharp
        private string _lastPrefNet = "";
        private string _lastPrefGpu = "";
        private int _lastGpuCount = 1;
```

在 `PreCacheAllSensors` 里，把：

```csharp
            _lastPrefNet = _cfg.PreferredNetwork;
            _lastPrefGpu = _cfg.PreferredGpu ?? "";
```

替换为：

```csharp
            _lastPrefNet = _cfg.PreferredNetwork;
            _lastPrefGpu = _cfg.PreferredGpu ?? "";
            _lastGpuCount = _cfg.MonitoredGpuCount;
```

在 `OnUpdateTickStarted` 里，把：

```csharp
                bool gpuChanged = _lastPrefGpu != (_cfg.PreferredGpu ?? "");
                if (gpuChanged)
                {
                    _sensorMap.Rebuild(_computer, _cfg);
                }

                // 自动检测配置变更：如果用户更改了首选风扇/磁盘/显卡，立即自动预热
                if (gpuChanged ||
                    _lastPrefCpuFan != _cfg.PreferredCpuFan ||
```

替换为：

```csharp
                bool gpuChanged = _lastPrefGpu != (_cfg.PreferredGpu ?? "");
                bool gpuCountChanged = _lastGpuCount != _cfg.MonitoredGpuCount;
                if (gpuChanged || gpuCountChanged)
                {
                    // ★★★ [多显卡] 显卡数量变了, 重建映射 ★★★
                    _sensorMap.Rebuild(_computer, _cfg);
                }

                // 自动检测配置变更：如果用户更改了首选风扇/磁盘/显卡，立即自动预热
                if (gpuChanged || gpuCountChanged ||
                    _lastPrefCpuFan != _cfg.PreferredCpuFan ||
```

- [ ] **Step 3: 编译并跑测试**

```powershell
$dotnet build 'G:\TOOL\LiteMonitor-fork\LiteMonitor.csproj' -c Release -v minimal
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 编译 0 错误，测试全通过

- [ ] **Step 4: 提交**

```powershell
git add src/System/HardwareMonitor.cs src/System/HardwareServices/HardwareValueProvider.cs
git commit -m "feat(gpu): 硬件刷新层支持多张显卡

被监控的所有显卡都会参与轮询; 显卡数量变化时自动重建传感器映射。"
```

---

## Task 13: 设置页与语言包

**Files:**
- Modify: `src/UI/Settings/SystemHardwarPage.cs`
- Modify: `resources/lang/zh.json`、`en.json`、`zh-tw.json`、`ja.json`、`ko.json`、`ru.json`、`fr.json`、`de.json`、`es.json`

**Interfaces:**
- Consumes: `GpuItemSync.Ensure`（Task 4）、`Settings.MonitoredGpuCount`（Task 5）
- Produces: 设置页 → 系统与硬件 → 「监控显卡数量」下拉；语言条目 `Menu.GpuMonitorCount` / `Menu.GpuMonitorCountTip`

- [ ] **Step 1: 确认配置单例链路（已核实，无需改动）**

配置链路已核实为共享单例，combo 的 setter 直接改 `Config` 即可全局生效：

```
MainForm._cfg  = Settings.Load()          // MainForm_Transparent.cs:16 —— 单例
  -> UIController._cfg                     // UIController.cs:14
    -> new SettingsForm(_cfg, _ui, ...)    // MainFormBizHelper.cs:326 —— 同一个实例
      -> SettingsPageBase.Config
```

因此**不需要**任何额外的同步桥接代码。若后续重构改变了这条链路，`GpuItemSync.Ensure(Config)` 仍只在同一实例上操作，不会产生分裂。

- [ ] **Step 2: 新增数量下拉**

在 `src/UI/Settings/SystemHardwarPage.cs` 的 `CreateSourceCard` 方法中，把：

```csharp
            _cbGpu = (LiteComboBox)group.AddComboPair(this, LanguageManager.T("Menu.GpuSource"),
                new[] { new { Label = strAuto, Value = "" } },
                () => Config?.PreferredGpu ?? "",
                v => { if (Config != null) Config.PreferredGpu = v ?? ""; });
```

替换为：

```csharp
            _cbGpu = (LiteComboBox)group.AddComboPair(this, LanguageManager.T("Menu.GpuSource"),
                new[] { new { Label = strAuto, Value = "" } },
                () => Config?.PreferredGpu ?? "",
                v => { if (Config != null) Config.PreferredGpu = v ?? ""; });

            // ★★★ [多显卡] 监控显卡数量下拉 ★★★
            // 1 = 只监控 1 张 (沿用旧行为), 2 及以上 = 按优先级监控前 N 张
            var gpuCountOptions = Enumerable.Range(1, GpuKey.MaxGpuCount).Select(c => c.ToString()).ToList();
            group.AddCombo(this, "Menu.GpuMonitorCount", gpuCountOptions,
                () => (Config?.MonitoredGpuCount ?? 1).ToString(),
                v => {
                    if (Config == null) return;
                    Config.MonitoredGpuCount = MetricUtils.ParseInt(v);
                    // 立即同步监控项, 让设置页保存后界面能重建出新的显卡分组
                    GpuItemSync.Ensure(Config);
                }
            );
            group.AddHint(LanguageManager.T("Menu.GpuMonitorCountTip"));
```

- [ ] **Step 3: 补齐 using**

`src/UI/Settings/SystemHardwarPage.cs` 顶部需要 `using LiteMonitor.src.Core;`（`GpuKey` / `GpuItemSync` / `MetricUtils`）。该文件已有 `using LiteMonitor.src.Core;` —— 确认即可。

- [ ] **Step 4: 添加语言条目**

在 `resources/lang/zh.json` 的 `Menu` 段中，在 `"GpuSource": "首选显卡",` 这一行后面插入：

```json
    "GpuMonitorCount": "监控显卡数量",
    "GpuMonitorCountTip": "⚠️多显卡模式下按性能优先级取前 N 张显卡，温度/占用/功耗等指标会分卡显示",
```

在 `resources/lang/en.json` 的 `Menu` 段中，在 `"GpuSource"` 对应行后面插入：

```json
    "GpuMonitorCount": "Monitored GPU count",
    "GpuMonitorCountTip": "⚠️In multi-GPU mode the top N GPUs by priority are monitored; temperature / load / power are shown per GPU",
```

其余语言包（`zh-tw.json` / `ja.json` / `ko.json` / `ru.json` / `fr.json` / `de.json` / `es.json`）在各自 `Menu` 段的 `"GpuSource"` 对应行后插入：

`zh-tw.json`:
```json
    "GpuMonitorCount": "監控顯示卡數量",
    "GpuMonitorCountTip": "⚠️多顯示卡模式下按效能優先級取前 N 張顯示卡，溫度/佔用/功耗等指標會分卡顯示",
```

`ja.json`:
```json
    "GpuMonitorCount": "監視する GPU の数",
    "GpuMonitorCountTip": "⚠️複数 GPU モードでは優先度の上位 N 枚を監視し、温度/使用率/消費電力は GPU ごとに表示します",
```

`ko.json`:
```json
    "GpuMonitorCount": "모니터링할 GPU 수",
    "GpuMonitorCountTip": "⚠️다중 GPU 모드에서는 우선순위 상위 N개를 모니터링하며 온도/사용률/전력은 GPU별로 표시됩니다",
```

`ru.json`:
```json
    "GpuMonitorCount": "Количество отслеживаемых GPU",
    "GpuMonitorCountTip": "⚠️В режиме нескольких GPU отслеживаются N видеокарт с наивысшим приоритетом; температура/загрузка/мощность показываются по каждой карте",
```

`fr.json`:
```json
    "GpuMonitorCount": "Nombre de GPU surveillés",
    "GpuMonitorCountTip": "⚠️En mode multi-GPU, les N GPU les plus prioritaires sont surveillées ; température / charge / puissance sont affichées par GPU",
```

`de.json`:
```json
    "GpuMonitorCount": "Anzahl überwachter GPUs",
    "GpuMonitorCountTip": "⚠️Im Multi-GPU-Modus werden die N GPUs mit höchster Priorität überwacht; Temperatur / Auslastung / Leistung werden pro GPU angezeigt",
```

`es.json`:
```json
    "GpuMonitorCount": "Número de GPU monitorizadas",
    "GpuMonitorCountTip": "⚠️En modo multi-GPU se monitorizan las N GPU con mayor prioridad; temperatura / carga / potencia se muestran por GPU",
```

> 注意：JSON 里的 `⚠️` 是合法字符，不需要转义。插入后必须用 JSON 解析器校验每个文件合法。

- [ ] **Step 5: 校验所有语言包 JSON 合法**

```powershell
Get-ChildItem 'G:\TOOL\LiteMonitor-fork\resources\lang\*.json' | ForEach-Object {
  try { Get-Content $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json | Out-Null; "OK  $($_.Name)" }
  catch { "FAIL $($_.Name): $($_.Exception.Message)" }
}
```

Expected: 9 个文件全部 `OK`

- [ ] **Step 6: 编译并跑测试**

```powershell
$dotnet build 'G:\TOOL\LiteMonitor-fork\LiteMonitor.csproj' -c Release -v minimal
$dotnet test 'G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 编译 0 错误，测试全通过

- [ ] **Step 7: 提交**

```powershell
git add src/UI/Settings/SystemHardwarPage.cs resources/lang/
git commit -m "feat(gpu): 设置页新增监控显卡数量选项

按 1~8 张选择被监控的显卡数量, 切换后立即同步监控项; 补齐 9 种语言文案。"
```

---

## Task 14: 真机双卡验证

**Files:**
- 无源码改动；若发现问题，回到对应 Task 修复

**Interfaces:**
- Consumes: 全部前置 Task 的产出
- Produces: 一份可运行的 Release 构建 + 真机验证结论

- [ ] **Step 1: 发布构建到独立目录（不动现有安装）**

```powershell
$dotnet publish 'G:\TOOL\LiteMonitor-fork\LiteMonitor.csproj' -c Release -r win-x64 --self-contained false `
  -o 'G:\TOOL\LiteMonitor-multi' -v minimal
```

Expected: 生成 `G:\TOOL\LiteMonitor-multi\LiteMonitor.exe` 与 `resources/` 目录

> **不要**覆盖 `G:\TOOL\LiteMonitor`（你正在使用的版本）。新目录的 `resources\plugins` 等由 publish 复制；`settings.json` 不存在时会自动生成默认配置。

- [ ] **Step 2: 启动并确认单卡默认行为不变**

```powershell
Start-Process 'G:\TOOL\LiteMonitor-multi\LiteMonitor.exe' -Verb RunAs
```

打开「设置 → 系统与硬件」，确认「监控显卡数量」默认为 `1`；关闭设置，观察主界面 GPU 区域与改造前一致（只显示一组 GPU 指标）。

- [ ] **Step 3: 切到 2 张并核对数据源**

在设置里把「监控显卡数量」改为 `2`，点保存。预期主界面 GPU 分组下出现两组指标，标签形如 `3080 Ti 温度` / `3080 温度`。

用 `nvidia-smi --query-gpu=index,name,temperature.gpu,power.draw,fan.speed --format=csv` 取真值对照：

| 期望 | 依据 |
|---|---|
| 第一组显示 3080 Ti 的温度/功耗 | LHM 枚举顺序为 Ti(priority 0) → 3080(priority 0)，`OrderBy` 稳定排序保序 |
| 第二组显示 3080 的温度/功耗 | 同上 |
| 两组数值与 nvidia-smi 对应行一致 | — |

- [ ] **Step 4: 验证 GPU.Fan 不再串卡**

在设置 → 监控项显示里把 `GPU.1.Fan`（第二张卡的风扇）设为可见，保存。
本机参考值：RTX 3080 风扇 0%（待机副卡），RTX 3080 Ti 风扇 55%。
预期：第一张卡的风扇显示约 55%，第二张卡显示约 0%。**改造前两张卡会都显示 3080 的 0%**（旧代码无条件覆盖导致）。

- [ ] **Step 5: 验证任务栏与历史曲线**

- 任务栏：把 `GPU.0.Temp` / `GPU.1.Temp` 设为任务栏可见，确认分别显示 `3080 Ti` / `3080` 前缀，数值与主界面一致。
- 历史曲线：等 1 分钟（采样间隔 10 秒），打开「监控历史」，确认 `GPU.0.Temp` 与 `GPU.1.Temp` 是两条独立曲线。

- [ ] **Step 6: 验证网页版（可选）**

设置 → 主界面设置 → 开启「网页版显示」，浏览器访问 `http://127.0.0.1:5000`，确认 JSON 快照里同时出现 `GPU.0.*` 与 `GPU.1.*` 两组键。

- [ ] **Step 7: 回退到 1 张并确认配置正确裁剪**

把「监控显卡数量」改回 `1`，保存。检查 `G:\TOOL\LiteMonitor-multi\settings.json`，确认 `MonitorItems` 中不再有 `GPU.1.*` 的项。

- [ ] **Step 8: 回归确认旧安装不受影响**

```powershell
Get-Process -Name 'LiteMonitor' -ErrorAction SilentlyContinue |
  Select-Object Id, Path, StartTime | Format-Table -AutoSize
```

Expected: 原有 `G:\TOOL\LiteMonitor\LiteMonitor.exe` 进程仍在运行且配置未变。

- [ ] **Step 9: 提交最终验证记录**

在 `G:\TOOL\LiteMonitor-fork\docs\superpowers\plans\` 下新建 `2026-10-20-gpu-multi-card-verification.md`，写入 Step 2~8 的实际观察值（尤其是 nvidia-smi 对照表与风扇读数），然后：

```powershell
git add docs/
git commit -m "docs(gpu): 记录多显卡改造的真机验证结果"
```

- [ ] **Step 10: 推送分支**

```powershell
git push -u origin feat/multi-gpu
```

---

## 回滚方案

每个 Task 一次独立提交，若某步出问题：

```powershell
git log --oneline
git revert <commit-sha>          # 保留历史，安全回退
# 或
git reset --hard <commit-sha>    # 仅限未推送的本地分支
```

功能开关式回退：把设置里的「监控显卡数量」改回 `1`，程序即回到单卡行为（旧格式 Key 仍然被映射，第 0 张卡额外写入 `GPU.*` 旧键）。

---

## 附录 A: 改造前后 Key 对照

| 场景 | 改造前 | 改造后 |
|---|---|---|
| 单卡默认 | `GPU.Load` | `GPU.0.Load`（同时保留 `GPU.Load` 旧键映射） |
| 双卡 | 不支持 | `GPU.0.Load` + `GPU.1.Load` |
| 三卡 | 不支持 | `GPU.0.*` + `GPU.1.*` + `GPU.2.*` |

## 附录 B: 已知不做的事（YAGNI）

- 不做「全部显卡」自动模式：固定 1~8 的数字下拉，配置与 UI 都能立即确定，无需运行时探测硬件。
- 不做插件市场的多卡支持：插件 Key 走 `DASH.*` 命名，与 `GPU.*` 无关。
- 不改 `resources/plugins/` 下 13 个内置插件 JSON。
- 不引入插件远程安装能力（另一条独立计划线）。
- 不做每张卡的独立阈值：温度/占用/功耗阈值继续全局共享，峰值按卡独立自适应即可覆盖绝大多数场景。
