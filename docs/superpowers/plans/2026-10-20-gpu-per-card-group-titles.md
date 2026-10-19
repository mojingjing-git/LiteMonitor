# 多显卡「每卡一个小标题」分组改造计划

- **文档日期**：2026-10-20
- **基线分支 / HEAD**：`fix/gpu-vram-d3d` @ `0230bef`（含多卡改造 + 显存串卡修复）
- **目标分支**：`feat/gpu-group-titles`（从 `fix/gpu-vram-d3d` 切出）
- **不涉及**：`chore/rebrand`（`ede8140`）分支上正在进行的 rebrand 改动，本计划一行都不碰
- **状态**：待实施

---

## 0. 一句话目标

把「所有显卡共用一个 `GPU` 分组」改成「**每张卡一个独立分组**」，每组自带一个**可自定义的小标题**（默认 = 显卡简称，如 `3080 Ti`），该卡的 6 个指标（使用率 / 温度 / 频率 / 功耗 / 风扇 / 显存）全部包裹在这个标题下面。

---

## 1. 需求原文与验收口径

> 我想给不同的卡设置不同的小标题，包裹住使用率、温度、频率、功耗、风扇、显存，这样就不用看好多个重复的显卡型号了。

用户已选定 **方案 A = 默认标题用显卡简称**（`3080 Ti` / `3080`），并且可以被用户自定义覆盖。

### 1.1 目标效果（2 卡机器）

改造前（所有卡挤在一个 `🎮GPU` 组里，项标签重复带显卡简称）：

```
🎮GPU
  3080 Ti 使用率        55 %
  3080 Ti 温度          61 °C
  3080 Ti 频率        1620 MHz
  3080 Ti 功耗         210 W
  3080 Ti 风扇           0 %
  3080 Ti 显存占用      46 %
  3080 使用率           0 %
  3080 温度            45 °C
  ...
```

改造后（每卡一组，组标题 = 小标题，组内项标签不再重复显卡型号）：

```
3080 Ti                 ← 小组标题（可自定义）
  使用率                55 %
  温度                  61 °C
  频率                1620 MHz
  功耗                 210 W
  风扇                   0 %
  显存占用              46 %

3080                   ← 小组标题（可自定义）
  使用率                 0 %
  温度                  45 °C
  ...
```

### 1.2 验收口径

| # | 验收项 | 判定方式 |
|---|--------|----------|
| A1 | 面板上每张显卡是**独立的一个圆角组框** | 真机截图 |
| A2 | 组标题默认等于**显卡简称** | 真机截图 |
| A3 | 组内 6 个指标**不再重复显卡型号** | 真机截图 |
| A4 | 设置页能**逐卡**改标题 | 真机操作 |
| A5 | 改完保存 → 重启 → 标题仍在 | 真机操作 |
| A6 | 网页端按卡拆成多张卡片，且**不白屏** | 真机操作（重点，见 §3.4） |
| A7 | 右键菜单按卡拆成多个分组 | 真机操作 |
| A8 | 构建 **0 错误**、警告数**不超过基线 232** | `dotnet build` |
| A9 | 测试**全通过**（基线 89 + 新增用例） | `dotnet test` |

---

## 2. 现状勘察结论

### 2.1 机制已经存在 90%，只差「按卡拆组」这一个开关

本功能的**全部基础设施都已就绪**，不需要新造面板渲染特性：

| 能力 | 位置 | 状态 |
|------|------|------|
| 分组别名存储 | `src/Core/Settings.cs:152` `Dictionary<string,string> GroupAliases` | ✅ 已有 |
| 设置页组标题输入框 | `src/UI/Controls/MonitorControls.cs:338-386` `MonitorGroupHeader.InputAlias` | ✅ 已有 |
| 面板画圆角组框 + 组标题 | `src/UI/UIRenderer.cs:75-104` `DrawGroupBackground` | ✅ 已有 |
| 标题解析（别名覆盖） | `UIController.cs:296-299,346-349` / `MenuMonitorHelper.cs:239-240` | ✅ 已有，但**散落 5 处** |
| 设置页按组分块 | `src/UI/Settings/MonitorPage.cs:299-307, 391-416` | ✅ 已有 |
| 网页端按组分卡 | `src/System/WebServer/LiteWebServer.cs:132,180,248` | ✅ 已有 |

### 2.2 唯一缺口：`UIGroup` 把所有显卡塞进同一组

`src/Core/Settings.cs:266`（`Key` setter 内）：

```csharp
CachedUIGroup = UIUtils.Intern(_key.Split('.')[0]);   // "GPU.0.Load" -> "GPU"
```

`src/Core/Settings.cs:331`（`UIGroup` fallback 分支）：

```csharp
return Key.Split('.')[0];                             // 同上
```

因为 `GPU.0.Load` / `GPU.1.Load` 的第一段都是 `GPU`，所以两张卡的 12 个指标全在同一个 `GPU` 组里。

**把这两处改成「带序号的显卡 Key → `GPU.{序号}`」即可**，面板 / 设置页 / 菜单 / 网页端会**自动**跟着拆开，因为它们全都按 `UIGroup` 分组。

### 2.3 已经确认**不受影响**的路径

| 路径 | 坐标 | 为什么不受影响 |
|------|------|----------------|
| `Settings.IsAnyEnabled("GPU")` | `src/Core/SettingsHelper.cs:483-486` | 走 `StartsWith` 前缀匹配，`GPU.0.Load` 仍命中 |
| `MetricUtils.GetType` | `src/Core/MetricUtils.cs` | 走子串匹配，`GPU.1.Temp` 仍含 `TEMP` |
| 任务栏 / 托盘 | `src/UI/UIController.cs:372-379` | 任务栏是**扁平排序**（`TaskbarSortIndex`），根本不分组 |
| 历史曲线 / 历史记录 | `src/System/...` | 按 series（`GPU.0.Load`）分组，不依赖 `UIGroup` |
| 主题编辑器 | `src/ThemeEditor/ThemePreviewControl.cs:100` | 用假数据，硬编码 `GPU` 组，纯预览 |
| 水平模式布局 | `src/UI/UIController.cs:384-388` | 同样按 `UIGroup` 分组，自动跟随 |
| 布局引擎 | `src/UI/UILayout.cs:68` | 遍历任意数量的组，已支持 6+ 个组 |

### 2.4 组顺序天然正确

`GpuItemSync` 给每张卡分配 `SortIndex = baseSort + i * GpuItemSync.SortStride`（步长 10，基准 301~306）：

- `GPU.0.*` → 301..306 → 组内 `min(SortIndex)` = 301
- `GPU.1.*` → 311..316 → 组内 `min(SortIndex)` = 311

组间排序用 `g.Min(x => x.SortIndex)`（`UIController.cs:279`）→ GPU.0 组在 GPU.1 组之前，**与用户期望的显卡顺序一致**，且两张卡的组是**相邻**的（中间不会插进 CPU / MEM 组）。

---

## 3. 关键设计决策

### 3.1 D1：走「按卡拆组」路线，不新造面板渲染特性

**决策**：`UIGroup` 对 `GPU.{n}.{metric}` 返回 `GPU.{n}`。

**理由**：分组机制（`GroupAliases` + `MonitorGroupHeader` + `DrawGroupBackground`）本来就是为「按硬件类别分块」设计的，把它从「按类别」细化到「按卡」是零成本的；而新造一套「卡片式小标题」渲染器要动 `UIRenderer` / `UILayout` / 主题 / 缩放 / DPI，工作量和回归风险高一个数量级。

**代价**：网页端 `groupId` 从 `"GPU"` 变成 `"GPU.0"`（含点号），见 §3.4。

### 3.2 D2：默认标题 = 显卡简称（方案 A）

`GpuRegistry.GetShortName(index)` 由 `SensorMap.Rebuild` 填充，内容来自 `HardwareRules.GetShortGpuName(hw.Name)`：

| 全名 | 简称 |
|------|------|
| `NVIDIA GeForce RTX 3080 Ti` | `3080 Ti` |
| `NVIDIA GeForce RTX 3080` | `3080` |
| `AMD Radeon RX 7900 XTX` | `7900 XTX` |
| `Intel Arc B580` | `B580` |

**取不到简称时**（硬件还没初始化完 / 驱动异常）退回 `GPU.{序号}`，保证标题永不空。

### 3.3 D3：拆组后，项标签必须**去掉**显卡简称前缀

**这是本计划最容易漏、但漏了就会让功能失去意义的一点。**

组标题已经承担了「这是哪张卡」的表达职责。如果项标签仍然带前缀，面板会变成：

```
3080 Ti
  3080 Ti 温度      ← 重复！用户明确说"这样就不用看好多个重复的显卡型号了"
```

所以 `MetricLabelResolver.ResolveLabel` 在按卡拆组模式下**不再**拼接显卡简称，只取基础标签（`Items.GPU.Temp` = `GPU温度` → 去前缀 → `温度`）。

去前缀后的对照表（语言包 `resources/lang/zh.json`）：

| Key | 语言包原文 | 拆组后面板显示 |
|-----|-----------|--------------|
| `GPU.Load` | `GPU使用率` | `使用率` |
| `GPU.Temp` | `GPU温度` | `温度` |
| `GPU.Clock` | `GPU频率` | `频率` |
| `GPU.Power` | `GPU功耗` | `功耗` |
| `GPU.Fan` | `GPU风扇` | `风扇` |
| `GPU.VRAM` | `显存占用` | `显存占用` |

**任务栏（托盘）不受此影响**：托盘是扁平的、没有组标题，所以 `ResolveShortLabel` 继续返回显卡简称（`MetricLabelResolver.cs:66-70` 不改）。

### 3.4 D4：网页端 `groupId` 必须消毒（**P0 隐患**）

`LiteWebServer.cs:180` 现状：

```csharp
string groupId = item.UIGroup.ToUpper();     // 改造后 -> "GPU.0"
```

前端 `src/System/WebServer/WebPageContent.cs` 把 `gid` 直接拼进 **DOM id** 和 **`querySelector` 选择器**：

```javascript
id='rw-${gid}'                                  // -> id="rw-GPU.0"
div.querySelector(`#rw-${gid}`)                 // -> querySelector("#rw-GPU.0")  ★
```

`#rw-GPU.0` 在 CSS 里被解析成「id=`rw-GPU` 且 class=`0`」，而 class 名不能以数字开头 → **`querySelector` 抛 `DOMException`，整个网页端渲染中断（白屏）**。

**决策**：在**后端**把分组键消毒成 JS/CSS 安全的 id，只替换非 `[A-Za-z0-9_-]` 字符：

```
"GPU.0"  -> "GPU_0"
"CPU"    -> "CPU"      （不变，零影响既有行为）
"HOST"   -> "HOST"
```

**为什么改后端而不是改前端**：
1. 前端 `WebPageContent.cs` 正在被 rebrand 分支的进行中改动占用（去 emoji），避开它可以完全避免与用户进行中的工作冲突；
2. `gid` 在前端同时被用作**对象 key**（`groups[gid]` / `cards[gid]`）和 **DOM id**，只要消毒后的值在两端一致就无需改前端；
3. 消毒在后端做一次，四处引用（对象 key / DOM id / querySelector / innerHTML 模板）自动全部一致。

### 3.5 D5：标题解析收敛到**单一入口**

现状：面板 2 处、设置页 1 处、菜单 1 处、网页端 1 处，共 5 处各算一遍标题，逻辑一散就会出现「面板显示简称、网页显示 `GPU.0`」这种不一致。

**决策**：新增 `GroupTitleResolver`，5 处全部改调它，优先级统一为：

```
用户自定义别名 (Settings.GroupAliases)  >  显卡简称 (按卡分组)  >  语言包  >  分组键本身
```

### 3.6 D6：设置页「保存别名」的判定基准必须是**输入框初值**，不能是「当前默认标题」

**这是一个真实的功能缺陷，必须在本次一并修掉。**

`MonitorPage.SaveToWorkingList` 现状（`src/UI/Settings/MonitorPage.cs:500-505`）：

```csharp
string alias = block.Header.InputAlias.Inner.Text.Trim();
string defName = LanguageManager.T("Groups." + block.Header.GroupKey);
if (!string.IsNullOrEmpty(alias) && alias != defName)
    Config.GroupAliases[block.Header.GroupKey] = alias;
else
    Config.GroupAliases.Remove(block.Header.GroupKey);
```

**故障场景**：用户在设置页打开时硬件还没初始化完 → 输入框初值 = `GPU.0`；此时 `GpuRegistry` 后来拿到了简称 → `defName` 变成 `3080 Ti`。用户**什么都没改**直接点保存 → `alias("GPU.0") != defName("3080 Ti")` → **把 `GPU.0` 写进 `GroupAliases`** → 标题被固化成 `GPU.0`，再也不会跟随简称。

**决策**：以**输入框初值**为基准判断「用户到底改没改」：

- 输入值 == 初值 → 用户没动 → `GroupAliases` 保持原样（不动）
- 输入值非空且 != 初值 → 用户改过 → 写入
- 输入值被清空 → 用户主动清空 → 删除别名，回到默认标题

> 这个改动同时修掉了旧实现「用户没动过 = 删掉已有别名」的语义混乱：旧实现里 `alias == defName` 会走 `Remove`，把「用户主动改回默认值」和「用户根本没动」混为一谈。新实现严格区分这两种情况。

### 3.7 D7：`SplitPerCard` 做成单一常量开关

`GpuGroupPolicy.SplitPerCard` 一个常量同时控制两件事：**是否按卡拆组**、**项标签是否去前缀**。

理由：这两件事必须**同生共死**——只拆组不去前缀会重复（§3.3），只去前缀不拆组会让单卡用户失去显卡型号信息。做成一个常量，日后要回退只需改一处。

### 3.8 D8：旧的 `GroupAliases["GPU"]` 不迁移

改造前用户可能给整组起过名（`"我的游戏卡"`）。拆组后不存在 `GPU` 组了，这个别名变成孤儿。

**决策**：**不迁移、不删除**（留在 `settings.json` 里无害，只是不生效）。理由：
- 迁移到每张卡会让两张卡同名，等于把「重复显卡型号」的问题原样搬回来；
- 自动删除会丢掉用户数据，不可逆。

**记入 §7 已知行为变更。**

---

## 4. 逐 Task 实施

> **实施前置**：从 `fix/gpu-vram-d3d` @ `0230bef` 切出分支 `feat/gpu-group-titles`。
> **提交约束**：工作区里还有用户 rebrand 分支的进行中改动（`resources/plugins/*.json`、`src/Core/MetricUtils.cs`、`src/Plugins/PluginConstants.cs`、`src/System/WebServer/WebPageContent.cs`、`src/UI/Controls/LiteUI.cs`、`src/UI/Settings/TaskbarPage.cs`）。
> **每个提交只能 `git add` 本任务明确列出的文件路径**，**严禁 `git add -A` / `git add .` / `git commit -a`**，否则会把用户的进行中改动卷进提交。
> **提交信息含中文** → 必须写入 UTF-8 消息文件后 `git commit -F <file>`（shell 内联中文会乱码）。

---

### Task 0 — 提交本计划文档

**新增文件**：`docs/superpowers/plans/2026-10-20-gpu-per-card-group-titles.md`

- 行尾：LF
- 编码：UTF-8 无 BOM

**提交**：

```
docs(gpu): 新增多显卡分组小标题改造计划
```

---

### Task 1 — 新增按卡拆组的分组策略与标题解析器

**新增文件**：`src/Core/GpuGroupPolicy.cs`

```csharp
using System;
using System.Globalization;

namespace LiteMonitor.src.Core
{
    /// <summary>
    /// 多显卡「按卡拆组」策略。
    ///
    /// 背景: 之前所有显卡的监控项都归到同一个 "GPU" 分组, 面板上会并排出现多组
    ///       重复的显卡型号。本策略把每张卡拆成独立分组 ("GPU.0" / "GPU.1"),
    ///       每组自带一个可自定义的小标题, 该卡的 6 个指标全部包裹在这个标题下。
    ///
    /// 分组键推导规则 (集中在这里, 避免各调用点各写一套):
    ///   "GPU.0.Load" -> "GPU.0"   (按卡拆组)
    ///   "GPU.Temp"   -> "GPU"     (旧版单卡格式, 没有序号, 不拆)
    ///   "CPU.Load"   -> "CPU"
    /// </summary>
    public static class GpuGroupPolicy
    {
        /// <summary>
        /// 是否启用「按卡拆组」。
        ///
        /// 打开时:
        ///   1. 面板 / 设置页 / 右键菜单 / 网页端都会按显卡拆成独立分组;
        ///   2. 组内项标签不再重复显卡简称 —— 分组标题已经承担了「这是哪张卡」的表达职责,
        ///      两边都带就是重复 (面板会变成 "3080 Ti" / "3080 Ti 温度")。
        ///
        /// 这两件事必须同生共死, 所以由同一个常量控制。
        /// </summary>
        public const bool SplitPerCard = true;

        /// <summary>按卡分组的分组键前缀, 例如 "GPU.0"</summary>
        public const string PerCardGroupPrefix = GpuKey.Prefix;

        /// <summary>
        /// 取监控项所属的「按卡分组」键。
        /// "GPU.0.Load" -> "GPU.0" ; 其余 (含旧版 "GPU.Load") 返回 false。
        /// </summary>
        public static bool TryGetPerCardGroupKey(string itemKey, out string groupKey)
        {
            groupKey = "";
            if (!GpuKey.TryParse(itemKey, out int index, out _)) return false;
            groupKey = PerCardGroupPrefix + index.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>
        /// 判断一个分组键是不是「按卡分组」, 并取回显卡序号。
        /// "GPU.0" -> true, 0 ; "GPU" -> false ; "GPU.0.Load" -> false ; "CPU" -> false
        /// </summary>
        public static bool TryGetCardIndex(string groupKey, out int index)
        {
            index = -1;
            if (string.IsNullOrEmpty(groupKey)) return false;
            if (!groupKey.StartsWith(GpuKey.Prefix, StringComparison.OrdinalIgnoreCase)) return false;

            string rest = groupKey.Substring(GpuKey.Prefix.Length);
            // 按卡分组键里只允许再出现一个数字段: "GPU.<序号>"
            if (rest.Length == 0 || rest.IndexOf('.') >= 0) return false;
            if (!int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out index)) return false;
            return index >= 0;
        }

        /// <summary>
        /// 按卡分组的默认标题 (方案 A = 显卡简称)。
        /// 简称取不到 (硬件还没初始化完) 时退回 "GPU.{序号}", 保证标题永不空。
        /// </summary>
        public static string GetDefaultTitle(int cardIndex)
        {
            string shortName = GpuRegistry.GetShortName(cardIndex);
            if (!string.IsNullOrEmpty(shortName)) return shortName;
            return PerCardGroupPrefix + cardIndex.ToString(CultureInfo.InvariantCulture);
        }
    }
}
```

**新增文件**：`src/Core/GroupTitleResolver.cs`

```csharp
using System;
using System.Text;

namespace LiteMonitor.src.Core
{
    /// <summary>
    /// 分组标题的统一解析入口。
    ///
    /// 优先级: 用户自定义别名 (Settings.GroupAliases) > 按卡分组的显卡简称 > 语言包 > 分组键本身。
    ///
    /// 为什么要统一: 面板 / 设置页 / 右键菜单 / 网页端 4 个地方各自算了一遍标题,
    /// 逻辑一散就会出现「面板显示简称、网页显示 GPU.0」这种前后端不一致。
    /// </summary>
    public static class GroupTitleResolver
    {
        /// <summary>
        /// 取分组的「默认标题」 (不含用户自定义别名)。
        /// </summary>
        public static string GetDefaultTitle(string groupKey)
        {
            if (string.IsNullOrEmpty(groupKey)) return "";

            if (GpuGroupPolicy.TryGetCardIndex(groupKey, out int cardIndex))
                return GpuGroupPolicy.GetDefaultTitle(cardIndex);

            string t = LanguageManager.T(UIUtils.Intern("Groups." + groupKey));
            return string.IsNullOrEmpty(t) ? groupKey : t;
        }

        /// <summary>
        /// 取分组的最终显示标题 (用户别名优先)。
        /// </summary>
        public static string Resolve(Settings cfg, string groupKey)
        {
            if (string.IsNullOrEmpty(groupKey)) return "";

            if (cfg?.GroupAliases != null
                && cfg.GroupAliases.TryGetValue(groupKey, out string alias)
                && !string.IsNullOrEmpty(alias))
            {
                return alias;
            }

            return GetDefaultTitle(groupKey);
        }

        /// <summary>
        /// 把分组键转成网页端可安全用作 DOM id / CSS 选择器的标识符。
        ///
        /// 网页前端会把 gid 拼进 querySelector('#rw-' + gid), 而 "GPU.0" 里的点号
        /// 会被当成 class 选择器 —— class 名不能以数字开头, querySelector 会抛
        /// DOMException, 整个网页端渲染中断 (白屏)。
        /// 这里统一把非 [A-Za-z0-9_-] 的字符替换成下划线: "GPU.0" -> "GPU_0"。
        /// 既有分组 ("CPU" / "HOST" / "DISK" ...) 全部保持原样, 零影响。
        /// </summary>
        public static string ToSafeGroupId(string groupKey)
        {
            if (string.IsNullOrEmpty(groupKey)) return "OTHER";

            var sb = new StringBuilder(groupKey.Length);
            foreach (char c in groupKey.ToUpperInvariant())
            {
                sb.Append((char.IsLetterOrDigit(c) || c == '_' || c == '-') ? c : '_');
            }
            return sb.ToString();
        }
    }
}
```

**提交**：

```
feat(gpu): 新增按卡拆组的分组策略与标题解析器
```

---

### Task 2 — 显卡监控项按卡拆成独立分组（核心）

**改文件**：`src/Core/Settings.cs`

**改动 2.1** — `Key` setter 内，`src/Core/Settings.cs:265-266`：

原：

```csharp
                else
                    CachedUIGroup = UIUtils.Intern(_key.Split('.')[0]); 
```

改为：

```csharp
                else
                {
                    // ★★★ [按卡拆组] 带序号的显卡 Key 归到「按卡分组」("GPU.0" / "GPU.1"),
                    //   这样每张卡的 6 个指标会被包在自己的小标题下面, 面板上不再并排
                    //   出现多组重复的显卡型号。
                    //   非显卡项 (CPU / MEM / HOST / DASH / ...) 保持原逻辑不变。
                    if (GpuGroupPolicy.TryGetPerCardGroupKey(_key, out string gpuGroup))
                        CachedUIGroup = UIUtils.Intern(gpuGroup);
                    else
                        CachedUIGroup = UIUtils.Intern(_key.Split('.')[0]);
                }
```

**改动 2.2** — `UIGroup` fallback 分支，`src/Core/Settings.cs:326-331`：

原：

```csharp
                if (Key.StartsWith("DASH."))
                {
                    return "DASH";
                }

                return Key.Split('.')[0]; 
```

改为：

```csharp
                if (Key.StartsWith("DASH."))
                {
                    return "DASH";
                }

                // ★★★ [按卡拆组] 必须与 Key setter 里的推导规则保持完全一致 ★★★
                if (GpuGroupPolicy.TryGetPerCardGroupKey(Key, out string gpuGroup))
                    return gpuGroup;

                return Key.Split('.')[0]; 
```

**兼容性检查**：

- `CachedUIGroup` 的原型缓存 `_protoCache`（`src/Core/Settings.cs:218`）只按 `_key` 索引，缓存值只依赖 `_key` → 安全；
- 旧 `settings.json` 里 `GPU.Load`（无序号）→ 仍归 `GPU` 组（该组在按卡模式下通常为空，不显示）；
- `MEM.Load` / `MOBO.Temp` / `DISK.Temp` / `CASE.Fan` / `FPS` → `HOST`（在 setter 之前就命中，不受影响）；
- `DASH.*` → `DASH`（不受影响）。

**提交**：

```
feat(gpu): 显卡监控项按卡拆成独立分组
```

---

### Task 3 — 统一分组标题解析 + 去掉项标签里的显卡前缀

#### 改动 3.1 — `src/UI/UIController.cs`（面板，2 处）

`:296-299`：

```csharp
                    var gr = new GroupLayoutInfo(currentGroupKey, currentGroupList);
                    string gName = GroupTitleResolver.Resolve(_cfg, currentGroupKey);
                    gr.Label = gName;
```

`:346-349`：

```csharp
                var gr = new GroupLayoutInfo(currentGroupKey, currentGroupList);
                string gName = GroupTitleResolver.Resolve(_cfg, currentGroupKey);
                gr.Label = gName;
```

#### 改动 3.2 — `src/UI/Helpers/MenuMonitorHelper.cs`（右键菜单，1 处）

`:239-240`：

```csharp
                    // 分组标题
                    string gName = GroupTitleResolver.Resolve(cfg, g.Key);
```

#### 改动 3.3 — `src/UI/Controls/MonitorControls.cs`（设置页组标题输入框）

`MonitorGroupHeader` 新增属性（放在 `InputAlias` 声明之后）：

```csharp
        /// <summary>
        /// 输入框的初始文本 (用户上次保存的别名, 或分组默认标题)。
        ///
        /// 保存时必须用它判断「用户到底改没改」, 不能拿「当前默认标题」去比:
        /// 默认标题会随硬件刷新变化 (显卡简称可能在设置页打开之后才拿到),
        /// 拿它当基准会把「用户没动过」误判成「用户改过」, 把默认值固化成别名。
        /// </summary>
        public string InputInitial { get; private set; } = "";
```

构造函数内 `:362-366`：

原：

```csharp
            string defGName = LanguageManager.T("Groups." + groupKey);
            if (defGName.StartsWith("Groups.")) defGName = groupKey;
            
            InputAlias = new LiteUnderlineInput(string.IsNullOrEmpty(alias) ? defGName : alias, "", "", 100) 
            { Location = new Point(MonitorLayout.X_COL2, UIUtils.S(8)) };
```

改为：

```csharp
            // ★★★ [按卡拆组] 默认标题统一走 GroupTitleResolver:
            //   按卡分组的默认标题是显卡简称 ("3080 Ti"), 不是语言包里的 "GPU"。
            string defGName = GroupTitleResolver.GetDefaultTitle(groupKey);
            InputInitial = string.IsNullOrEmpty(alias) ? defGName : alias;

            InputAlias = new LiteUnderlineInput(InputInitial, "", "", 100) 
            { Location = new Point(MonitorLayout.X_COL2, UIUtils.S(8)) };
```

#### 改动 3.4 — `src/UI/Settings/MonitorPage.cs`（保存别名）

`:500-505`：

原：

```csharp
                    string alias = block.Header.InputAlias.Inner.Text.Trim();
                    string defName = LanguageManager.T("Groups." + block.Header.GroupKey);
                    if (!string.IsNullOrEmpty(alias) && alias != defName) 
                        Config.GroupAliases[block.Header.GroupKey] = alias;
                    else 
                        Config.GroupAliases.Remove(block.Header.GroupKey);
```

改为：

```csharp
                    // ★★★ [按卡拆组] 判定基准是「输入框初值」, 不是「当前默认标题」 ★★★
                    //   默认标题会随硬件刷新变化 (显卡简称可能在设置页打开之后才拿到),
                    //   拿它当基准会把「用户没动过」误判成「用户改过」, 把 "GPU.0" 固化成别名。
                    string input = block.Header.InputAlias.Inner.Text.Trim();
                    string initial = block.Header.InputInitial;

                    if (string.Equals(input, initial, StringComparison.Ordinal))
                    {
                        // 用户没动过 -> GroupAliases 保持原样 (既有的别名不删, 也没有的不加)
                    }
                    else if (!string.IsNullOrEmpty(input))
                    {
                        // 用户改过 -> 保存新别名
                        Config.GroupAliases[block.Header.GroupKey] = input;
                    }
                    else
                    {
                        // 用户清空了 -> 回到分组默认标题
                        Config.GroupAliases.Remove(block.Header.GroupKey);
                    }
```

#### 改动 3.5 — `src/Core/MetricLabelResolver.cs`（项标签去前缀）

`:19-28`：

原：

```csharp
            // 2. [多显卡] 标签带上显卡简称, 例如 "3080 Ti 温度"
            if (GpuKey.TryParse(config.Key, out int gpuIndex, out string metric))
            {
                string shortName = GpuRegistry.GetShortName(gpuIndex);
                if (!string.IsNullOrEmpty(shortName))
                {
                    string baseLabel = LanguageManager.T(UIUtils.Intern("Items." + GpuKey.BaseKey(metric)));
                    return $"{shortName} {StripGpuPrefix(baseLabel)}".Trim();
                }
            }
```

改为：

```csharp
            // 2. [多显卡] 显卡项的标签
            //    ★ [按卡拆组] 组标题已经标明「这是哪张卡」, 项标签再带一遍显卡简称
            //      就是重复 (面板会变成 "3080 Ti" / "3080 Ti 温度"), 所以拆组模式下
            //      只取基础标签 ("温度")。
            //    ★ 旧版单组模式保留「简称 + 基础标签」, 由 SplitPerCard 一个开关切换。
            if (GpuKey.TryParse(config.Key, out int gpuIndex, out string metric))
            {
                string baseLabel = LanguageManager.T(UIUtils.Intern("Items." + GpuKey.BaseKey(metric)));
                string plainLabel = StripGpuPrefix(baseLabel);

                if (GpuGroupPolicy.SplitPerCard) return plainLabel;

                string shortName = GpuRegistry.GetShortName(gpuIndex);
                if (!string.IsNullOrEmpty(shortName))
                {
                    return $"{shortName} {plainLabel}".Trim();
                }
                // 简称还没拿到 -> 继续往下走动态标签分支 (与旧行为一致)
            }
```

**注意**：`ResolveShortLabel`（`:57-86`，任务栏简称）**不改** —— 托盘是扁平的、没有组标题，仍然需要简称来区分卡。

**提交**：

```
feat(gpu): 统一分组标题解析并去掉项标签的显卡前缀
```

---

### Task 4 — 网页端分组 id 消毒

**改文件**：`src/System/WebServer/LiteWebServer.cs`

`:180-183`：

原：

```csharp
                string groupId = item.UIGroup.ToUpper();
                // [Optimization] Use cached keys
                string groupsKey = !string.IsNullOrEmpty(item.CachedGroupsKey) ? item.CachedGroupsKey : UIUtils.Intern("Groups." + item.UIGroup);
                string groupDisplay = LanguageManager.T(groupsKey);
```

改为：

```csharp
                // ★★★ [按卡拆组] gid 会被前端拼进 DOM id 和 querySelector 选择器,
                //   "GPU.0" 里的点号是非法 CSS 选择器字符 (会被当成 class, 而 class 不能以数字开头),
                //   querySelector 会抛 DOMException 导致整个网页端白屏 -> 必须先消毒成 "GPU_0"。
                string groupId = GroupTitleResolver.ToSafeGroupId(item.UIGroup);
                // 分组标题统一走 GroupTitleResolver, 保证与桌面端一致 (别名 / 显卡简称)。
                string groupDisplay = GroupTitleResolver.Resolve(_cfg, item.UIGroup);
```

**前端不需要改动的理由**（写进 plan 供审核）：

- `gid` 在前端同时被用作**对象 key**（`groups[gid]` / `cards[gid]`）和 **DOM id**（`id='rw-${gid}'` / `id='list-${gid}'` / `id='rp-${gid}'` …）以及 **`querySelector` 参数**；
- 三者用的是**同一个** `gid` 变量，后端消毒一次，前端三处自动保持一致；
- 消毒是**幂等**的：`ToSafeGroupId("GPU_0")` == `"GPU_0"`；
- 既有分组（`CPU` / `HOST` / `DISK` / `NET` / `DATA` / `DASH` / `BAT`）不含非法字符，消毒后**完全不变**，零回归面；
- 前端 `['NET','DISK','DATA'].includes(gid)` 与 `gid === 'DASH'` 两个硬编码判断不受影响。

**提交**：

```
fix(web): 消毒分组 id 避免非法 CSS 选择器
```

---

### Task 5 — 回归测试

**新增文件**：`tests/LiteMonitor.Tests/GpuGroupTests.cs`

```csharp
using LiteMonitor.src.Core;
using Xunit;

namespace LiteMonitor.Tests
{
    public class GpuGroupTests
    {
        // ---------- 分组键推导 ----------

        [Theory]
        [InlineData("GPU.0.Load", "GPU.0")]
        [InlineData("GPU.1.Temp", "GPU.1")]
        [InlineData("GPU.7.VRAM", "GPU.7")]
        public void TryGetPerCardGroupKey_带序号的显卡Key_推出按卡分组键(string key, string expected)
        {
            Assert.True(GpuGroupPolicy.TryGetPerCardGroupKey(key, out string group));
            Assert.Equal(expected, group);
        }

        [Theory]
        [InlineData("GPU.Load")]
        [InlineData("CPU.Load")]
        [InlineData("MEM.Load")]
        [InlineData("GPU.")]
        [InlineData("GPU.x.Temp")]
        [InlineData("")]
        [InlineData(null)]
        public void TryGetPerCardGroupKey_非带序号或非法Key_返回false(string key)
        {
            Assert.False(GpuGroupPolicy.TryGetPerCardGroupKey(key, out _));
        }

        [Theory]
        [InlineData("GPU.0", 0)]
        [InlineData("GPU.3", 3)]
        public void TryGetCardIndex_按卡分组键_取回序号(string groupKey, int expected)
        {
            Assert.True(GpuGroupPolicy.TryGetCardIndex(groupKey, out int index));
            Assert.Equal(expected, index);
        }

        [Theory]
        [InlineData("GPU")]
        [InlineData("GPU.0.Load")]
        [InlineData("CPU")]
        [InlineData("HOST")]
        [InlineData("")]
        [InlineData(null)]
        public void TryGetCardIndex_非按卡分组键_返回false(string groupKey)
        {
            Assert.False(GpuGroupPolicy.TryGetCardIndex(groupKey, out _));
        }

        // ---------- UIGroup 按卡拆组 ----------

        [Theory]
        [InlineData("GPU.0.Load", "GPU.0")]
        [InlineData("GPU.1.Temp", "GPU.1")]
        public void UIGroup_带序号的显卡Key_归到按卡分组(string key, string expected)
        {
            Assert.Equal(expected, new MonitorItemConfig { Key = key }.UIGroup);
        }

        [Theory]
        [InlineData("CPU.Load", "CPU")]
        [InlineData("GPU.Load", "GPU")]
        [InlineData("MEM.Load", "HOST")]
        [InlineData("MOBO.Temp", "HOST")]
        [InlineData("DISK.Temp", "HOST")]
        [InlineData("CASE.Fan", "HOST")]
        [InlineData("FPS", "HOST")]
        [InlineData("DASH.HOST", "DASH")]
        public void UIGroup_非显卡Key_保持原分组不变(string key, string expected)
        {
            Assert.Equal(expected, new MonitorItemConfig { Key = key }.UIGroup);
        }

        // ---------- 网页端分组 id 消毒 ----------

        [Theory]
        [InlineData("GPU.0", "GPU_0")]
        [InlineData("GPU.1", "GPU_1")]
        [InlineData("GPU.0.Load", "GPU_0_LOAD")]
        [InlineData("CPU", "CPU")]
        [InlineData("HOST", "HOST")]
        [InlineData("NET", "NET")]
        [InlineData("DASH", "DASH")]
        public void ToSafeGroupId_非法选择器字符被替换成下划线(string groupKey, string expected)
        {
            Assert.Equal(expected, GroupTitleResolver.ToSafeGroupId(groupKey));
        }

        [Fact]
        public void ToSafeGroupId_空输入_返回OTHER()
        {
            Assert.Equal("OTHER", GroupTitleResolver.ToSafeGroupId(""));
            Assert.Equal("OTHER", GroupTitleResolver.ToSafeGroupId(null));
        }

        [Fact]
        public void ToSafeGroupId_是幂等的()
        {
            Assert.Equal("GPU_0", GroupTitleResolver.ToSafeGroupId(GroupTitleResolver.ToSafeGroupId("GPU.0")));
        }

        [Fact]
        public void ToSafeGroupId_结果可安全用作CSS选择器()
        {
            // 网页前端会把它拼进 querySelector('#rw-' + gid)
            string gid = GroupTitleResolver.ToSafeGroupId("GPU.0");
            Assert.DoesNotContain(".", gid);
            Assert.Matches("^[A-Za-z0-9_-]+$", gid);
        }

        // ---------- 标题解析 ----------

        [Fact]
        public void Resolve_有用户别名_返回别名()
        {
            var cfg = new Settings();
            cfg.GroupAliases["GPU.0"] = "我的游戏卡";
            Assert.Equal("我的游戏卡", GroupTitleResolver.Resolve(cfg, "GPU.0"));
        }

        [Fact]
        public void Resolve_无别名_回退到默认标题()
        {
            var cfg = new Settings();
            Assert.Equal(GroupTitleResolver.GetDefaultTitle("GPU.0"),
                         GroupTitleResolver.Resolve(cfg, "GPU.0"));
        }

        [Fact]
        public void Resolve_空别名_回退到默认标题()
        {
            var cfg = new Settings();
            cfg.GroupAliases["GPU.0"] = "";
            Assert.Equal(GroupTitleResolver.GetDefaultTitle("GPU.0"),
                         GroupTitleResolver.Resolve(cfg, "GPU.0"));
        }

        [Fact]
        public void GetDefaultTitle_按卡分组_取到显卡简称或退回序号()
        {
            string title = GroupTitleResolver.GetDefaultTitle("GPU.0");
            Assert.False(string.IsNullOrEmpty(title));
            // 硬件未初始化时退回 "GPU.0"; 初始化后是简称 (由 GpuRegistry 提供)
            Assert.True(title == "GPU.0" || title == GpuRegistry.GetShortName(0));
        }

        [Fact]
        public void GetDefaultTitle_普通分组_走语言包()
        {
            string title = GroupTitleResolver.GetDefaultTitle("CPU");
            Assert.False(string.IsNullOrEmpty(title));
            Assert.NotEqual("Groups.CPU", title);
        }
    }
}
```

**提交**：

```
test(gpu): 补充按卡拆组的回归测试
```

---

### Task 6 — 验证

#### 6.1 构建

```powershell
cd G:\TOOL\LiteMonitor-fork
& 'G:\TOOL\dotnet-sdk\dotnet.exe' build -c Release
```

**判定**：`0 错误`，警告数 **≤ 232**（基线）。

> ⚠️ `-v minimal` 会把同一条警告打印两遍，逐行 grep 得 464 是虚高。基线口径：**逐行去重后 232**。

#### 6.2 测试

```powershell
& 'G:\TOOL\dotnet-sdk\dotnet.exe' test -c Release
```

**判定**：全通过（基线 89 + 本次新增用例）。

#### 6.3 真机验证（Windows 本机，2 张卡：3080 Ti + 3080）

1. `dotnet publish` 到 `G:\TOOL\LiteMonitor-multi`
2. 跑 `G:\TOOL\启动多卡测试.ps1` 启动
3. **面板**：确认两个独立圆角组框；标题分别是 `3080 Ti` / `3080`；组内 6 项标签是 `使用率` / `温度` / `频率` / `功耗` / `风扇` / `显存占用`（不带显卡型号）
4. **设置页 → 监控项显示**：确认出现两个组块，各带一个可编辑标题框；把 0 号卡改成 `测试标题A`，保存
5. **重启**：确认 `测试标题A` 仍在，且 1 号卡仍是 `3080`
6. **右键菜单**：确认按卡拆成两个分组
7. **网页端**（`WebServerEnabled = true`）：确认按卡拆成两张卡片，**页面不白屏**（重点回归 §3.4 的 P0）
8. 恢复：把测试标题改回默认

#### 6.4 清理

真机验证用的临时改动（测试标题）必须还原；`G:\TOOL\LiteMonitor-multi` 的发布产物保留供用户使用。

---

## 5. 验收清单（逐条打勾）

- [ ] 构建 0 错误，警告 ≤ 232（去重后）
- [ ] 测试全通过
- [ ] 面板：每卡一个独立组框（A1）
- [ ] 面板：组标题 = 显卡简称（A2）
- [ ] 面板：组内项标签不带显卡型号（A3）
- [ ] 设置页：逐卡可改标题（A4）
- [ ] 改完保存 → 重启仍生效（A5）
- [ ] 网页端：按卡拆卡且不白屏（A6）
- [ ] 右键菜单：按卡拆分（A7）
- [ ] 旧 `GroupAliases["GPU"]` 不导致任何异常
- [ ] 任务栏 / 托盘显示不变（仍是简称）
- [ ] 历史曲线 / 记录功能正常
- [ ] 主题编辑器预览正常

---

## 6. 风险登记册

| # | 风险 | 等级 | 缓解措施 |
|---|------|------|----------|
| R1 | 网页端 `gid="GPU.0"` 导致 `querySelector` 抛异常 → **白屏** | **P0** | §3.4 + Task 4 后端消毒；§4 Task 5 有专门的「结果可安全用作 CSS 选择器」用例 |
| R2 | 只拆组不去前缀 → 面板出现 `3080 Ti` / `3080 Ti 温度`，功能等于没做 | **P0** | §3.3 + Task 3.5；`SplitPerCard` 单开关强制两者同生共死 |
| R3 | 设置页「保存别名」把默认值固化成别名（简称晚到场景） | **P1** | §3.6 + Task 3.4 改用「输入框初值」作基准 |
| R4 | 5 处标题解析逻辑分散 → 前后端标题不一致 | **P1** | §3.5 + Task 3 收敛到 `GroupTitleResolver` |
| R5 | `UIGroup` 的 setter 与 fallback 两处推导规则不一致 | **P1** | Task 2 两处同步改；Task 5 用 `UIGroup` 属性测（走 setter 路径）+ 显式覆盖 fallback |
| R6 | 提交时把用户 rebrand 的进行中改动卷进去 | **P1** | 每个提交只 `git add` 明确文件路径，禁用 `-A` / `.` / `-a`；Task 6 提交前 `git status` 核对 |
| R7 | 拆组后组数 +1，面板高度不够导致内容被裁 | P2 | 布局引擎支持任意组数（§2.3）；真机验证时检查两卡是否都完整显示 |
| R8 | 同型号双卡简称相同 → 两个组标题一样 | P2 | 用户可自定义区分；记入 §7 已知限制 |
| R9 | `GpuRegistry` 简称晚于设置页打开 → 输入框初值是 `GPU.0` | P2 | Task 3.3/3.4 的「初值基准」已消解副作用；面板标题是实时解析的，始终正确 |
| R10 | 旧 `GroupAliases["GPU"]` 失效 | P3 | §3.8 决定不迁移，留在配置里无害；记入 §7 |
| R11 | `CachedGroupsKey` 在 `LiteWebServer` 里不再被使用 | P3 | 它是 `public` 属性，不产生 CS0169 警告；保留以免影响其他潜在调用方 |

---

## 7. 已知行为变更与限制

### 7.1 行为变更（用户可感知）

1. **面板上 GPU 组的标题从 `🎮GPU` 变成显卡简称**（单卡机器也从 `🎮GPU` 变成 `3080 Ti`）。
2. **GPU 指标的行标签去掉了显卡型号前缀**（`3080 Ti 温度` → `温度`）。
3. **设置页「监控项显示」里 GPU 从 1 个组块变成 N 个组块**（N = 监控的显卡数）。
4. **右键菜单里 GPU 从 1 个分组变成 N 个分组**。
5. **网页端 GPU 从 1 张卡片变成 N 张卡片**。
6. **旧配置里的 `GroupAliases["GPU"]` 不再生效**（不迁移、不删除，见 §3.8）。

### 7.2 已知限制

1. **同型号双卡简称相同** → 两个组默认标题一样，需要用户手动区分（`GpuRegistry` 层面就分不出来，plan 附录 B.1 已记录此上游限制）。
2. **任务栏 / 托盘不分组** → 托盘上两张卡的同名指标都显示简称，靠数值区分（本次不改托盘，因为托盘是一行紧凑显示，塞不下组标题）。
3. **历史曲线仍按 series 分组**（`GPU.0.Load` / `GPU.1.Load` 是两条独立曲线），不合并成按卡的视图。
4. **浏览器端自定义标题**依赖 `Settings.GroupAliases`，网页端通过 `GroupTitleResolver.Resolve` 读取 → 与桌面端一致。

---

## 8. 附录

### 8.1 关键坐标索引

| 主题 | 文件:行 |
|------|---------|
| `UIGroup` 主计算（**主改点**） | `src/Core/Settings.cs:266` |
| `UIGroup` fallback（**同步改**） | `src/Core/Settings.cs:309-332` |
| `GroupAliases` 存储 | `src/Core/Settings.cs:152` |
| `GpuKey` 前缀 / 解析 | `src/Core/GpuKey.cs:20,29,49` |
| `GpuRegistry` 简称 / 全名 | `src/Core/GpuRegistry.cs:39-50` |
| `GpuItemSync` 指标表 / 排序步长 | `src/Core/GpuItemSync.cs:19-27,30,122` |
| 面板分组构建 + 标题解析（**2 处**） | `src/UI/UIController.cs:296-299, 346-349` |
| 面板画组框 + 组标题 | `src/UI/UIRenderer.cs:75-104` |
| 面板布局（遍历任意组数） | `src/UI/UILayout.cs:68` |
| 设置页组块创建 | `src/UI/Settings/MonitorPage.cs:299-307, 391-416` |
| 设置页保存别名（**主改点**） | `src/UI/Settings/MonitorPage.cs:500-505` |
| 设置页组标题输入框（**主改点**） | `src/UI/Controls/MonitorControls.cs:338-386` |
| 右键菜单分组标题 | `src/UI/Helpers/MenuMonitorHelper.cs:239-240` |
| 网页端分组 / groupId（**主改点**） | `src/System/WebServer/LiteWebServer.cs:132, 180-183, 248` |
| 网页端前端 gid 用法（**只读参考**） | `src/System/WebServer/WebPageContent.cs:404-489` |
| 指标标签解析（**主改点**） | `src/Core/MetricLabelResolver.cs:19-28` |
| 任务栏简称（不改） | `src/Core/MetricLabelResolver.cs:57-86` |
| `IsAnyEnabled`（不受影响） | `src/Core/SettingsHelper.cs:483-486` |
| 语言包 Groups 段 | `resources/lang/zh.json` → `Groups` |

### 8.2 语言包对照（`resources/lang/zh.json`）

```json
"Groups": {
  "DASH": "ℹ️看板",
  "CPU": "💻CPU",
  "GPU": "🎮GPU",
  "HOST": "🖥️主机",
  "DISK": "💽磁盘",
  "NET": "🌐网络",
  "DATA": "📈流量统计",
  "BAT": "🔋电池"
}
```

拆组后 `Groups.GPU` **不再被使用**（没有 `GPU` 组了），`Groups.GPU.0` 也不需要加语言包条目——按卡分组的默认标题来自 `GpuRegistry` 的显卡简称，是**运行时动态值**。

`Items` / `Short` 段保持不变（`GpuKey.ToBaseKey` 仍会归一化到 `Items.GPU.*`）。

### 8.3 参考：既有 plan（本文档不修改它们）

- `docs/superpowers/plans/2026-10-20-gpu-multi-card.md` — 多卡改造
- `docs/superpowers/plans/2026-10-20-gpu-vram-d3d-fix.md` — 显存串卡修复
- `docs/superpowers/plans/2026-10-20-rebrand-compliance.md` — rebrand（**不碰**）
