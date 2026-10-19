# LiteMonitor 外观改造实施计划

> 目标：为 LiteMonitor 增加 **机能风（Cyberpunk）** 与 **液态玻璃（Liquid Glass）** 两套视觉能力，
> 同时保证主题系统向后兼容、对外观改动可插拔、与仓库内其他并行开发低冲突。
>
> 本文档**不引用具体行号**。所有定位点均以 `文件路径 → 类型名 → 方法名` 表示，
> 便于在其他分支/并行改动存在时仍然有效。
>
> **v2 —— 已按独立复审意见修订。** 修订要点见文末《附录 C：复审修订记录》。

---

## 0. 设计前提

### 0.1 当前架构关键事实（改动的依据）

| 事实 | 位置 | 对改造的含义 |
|---|---|---|
| 竖屏渲染入口 | `src/UI/UIRenderer.cs` → `UIRenderer.Render` | 背景/装饰的主战场 |
| 竖屏背景溢出策略 | `UIRenderer.Render` | 从 `(-5,-5)` 起填充（**溢出 5px，修物理像素死角**）。横屏**没有**这个策略，是两处独立实现 |
| 横屏渲染入口 | `src/UI/HorizontalRenderer.cs` → `HorizontalRenderer.Render` | 需与竖屏保持视觉一致 |
| 任务栏渲染入口 | `src/UI/TaskbarRenderer.cs` → `TaskbarRenderer.Render` | **只画文字、零背景绘制，且完全不消费 `Theme`**（颜色是硬编码常量 + `Settings.GetStyle()`）→ 任务栏连现有主题都不跟随 |
| 主窗体绘制 | `src/UI/MainForm_Transparent.cs` → `MainForm.OnPaint` / `OnPaintBackground` | `OnPaintBackground` 为空实现（不画系统背景） |
| 主题模型 | `src/Core/ThemeManager.cs` → `Theme` / `LayoutConfig` / `FontConfig` / `ThresholdConfig` / `ColorConfig` | 外观能力的唯一配置源 |
| 颜色解析 | `src/Core/ThemeManager.cs` → `ThemeManager.ParseColor` | 只支持 `#RRGGBB` / `#AARRGGBB` / 颜色名。**3 位 `#RGB` 不支持**（会退化为全透明）。**不支持 `rgba()`** |
| `rgba()` 的实际行为 | `ThemeManager.ParseColor` → `Color.FromName` | **不抛异常**，返回 ARGB=0 的全透明色 → **静默失效**，不是崩溃。排查时容易误判为"没设置" |
| 绘制工具 | `src/Core/UIUtils.cs` → `RoundRect` / `FillRoundRect` / `DrawBar` / `GetBrush` / `GetPen` | 圆角路径、进度条的改造点 |
| 画刷缓存 | `UIUtils.GetBrush` / `GetPen` | `GetBrush` 返回类型固定为 `SolidBrush`、key 是颜色字符串；`GetPen` 返回**共享实例**、key 是 `色值_宽度`。**两者都无法承载 `TextureBrush` / `LinearGradientBrush`** |
| 圆角/穿透/透明度 | `src/UI/Helpers/MainFormWinHelper.cs` → `ApplyRoundedCorners` / `SetClickThrough` / `StartFadeIn` | Win11 走 DWM 系统圆角，Win10 走 WinForms `Form.Region` 属性（**不是 `SetWindowRgn` P/Invoke**） |
| 主题文件读写 | `src/ThemeEditor/ThemeFileService.cs` → `LoadTheme` / `SaveTheme` | 序列化整个 `Theme` 对象，**未映射字段会被丢弃**；且 `SaveTheme` 的 options 无 `DefaultIgnoreCondition`，**`null` 会被写成 `"Prop": null`** |
| 主题编辑器 UI | `src/ThemeEditor/ThemeEditorForm.cs` | **纯代码构建 UI**（无 `.Designer.cs`），拆新文件到同目录是合适的 |
| 主题预览 | `src/ThemeEditor/ThemePreviewControl.cs` → `OnPaint` | 内部调用 `UIRenderer.Render` → 装饰层挂进 `UIRenderer` 后**预览自动生效** |
| 主题预览的主题拷贝 | `ThemePreviewControl` → `CreatePreviewTheme` / `CreateScaledTheme` | 两处**各自手写** `new LayoutConfig { ... }` 字段拷贝 → **新增的 `LayoutConfig` 字段会被静默丢成默认值** |
| 网页版面板 | `src/System/WebServer/WebPageContent.cs` | 599 行内联 HTML/CSS，自带 `--bg` / `--c-0..2` / `--card` / `--border` 等变量，**对 `ThemeManager` 引用数为 0 → 与主题完全零耦合** |

### 0.2 已存在但被忽略的主题字段（当前丢失）

| 丢失位置 | 现象 |
|---|---|
| `color.border` / `color.shadow` / `color.textSecondary` | 10 个内置主题中有 **7 个**声明了（`AuroraDream_Purple`、`CyberBlue_Neon`、`DeepOcean_Blue`、`Dracula_Vampire`、`LightPure_Elegant`、`MatrixGreen_Terminal`、`Monokai_Pro`），但 `ColorConfig` **没有**对应属性 → 反序列化时丢弃 |
| `font.scale` | **全部 10 个**主题都声明了，但 `FontConfig` **没有** `Scale` 属性 → 同样被丢弃 |
| `color.shadow` 的值格式 | 7 个主题用的是 `rgba(0,255,255,0.2)` 这种写法，而 `ParseColor` **不识别 `rgba()`** → 即使属性补上了，值也会退化为全透明 |

**结论：机能风所需的 schema 已由作者预留，本次工作属于"补完半成品"，不是从零设计。**

### 0.3 外观消费点全景（明确范围，避免验收时被当成遗漏）

| 消费点 | 是否跟随主题 | 本次是否改造 | 说明 |
|---|---|---|---|
| 主窗口竖屏 `UIRenderer` | ✅ | ✅ 阶段二/三 | 主战场 |
| 主窗口横屏 `HorizontalRenderer` | ✅ | ✅ 阶段二/三 | 与竖屏共用装饰逻辑 |
| 任务栏 `TaskbarRenderer` | ❌ **完全不跟随** | ❌ 排除 | 零背景绘制 + 硬编码颜色；且窗体被 `SetParent` 挂到 `Shell_TrayWnd` 并设为 `WS_CHILD`，**子窗口不可能有 DWM 材质** |
| 任务栏 tooltip `LiteTooltipForm` / `TaskbarTooltipHelper` | ✅ | ❌ 自动受益 | 跟随主题变色，但装饰层不覆盖（它不是主面板） |
| `SpeedTestForm` / `AboutForm` / `CleanMemoryForm` | ✅ | ❌ 自动受益 | 已消费 `ThemeManager` / `ParseColor` |
| `MenuManager` / `MainPanelPage` | ✅（各 1 处引用） | ❌ 自动受益 | 引用量极小 |
| 网页版 `WebPageContent` | ❌ **零耦合** | ⚠️ 阶段四决策点 | 见 §4.3 |
| `ThemePreviewControl` | ✅ | ✅ 阶段四 | 需补齐两处 `LayoutConfig` 拷贝（见 §4.2） |
| `HardwareTrendForm` / `HardwareInfoForm` | ❌ 硬编码色 | ❌ **明确不改** | 独立对话框/详情页，维持现状浅色风格 |
| `LiteUI.UIColors` / `LiteTreeView` / `TrendChartControl` | ❌ 硬编码色 | ❌ **明确不改** | 设置页/图表控件，独立于主面板视觉 |
| 托盘图标 `Properties.Resources.AppIcon` | ❌ 静态资源 | ❌ 明确不改 | 主题不涉及系统资源 |

### 0.4 与并行开发的协作守则

本计划假定仓库内存在其他并行开发（插件市场、设置项、菜单等）。遵守以下规则：

1. **新增优先，修改最少。** 绝大部分新能力放在**新文件**里，现有文件只做"挂载点"级别的改动。
2. **每个现有文件最多一个挂载点。** 挂载点用方法名而非行号描述。即使他人同时修改同一文件，冲突面也被压到最小。
3. **不改现有公开方法的签名。** 需要扩展时用重载或可选参数，保留旧调用路径。
4. **新主题字段全部可空 + 缺省关闭。** 内置 10 个主题在未显式声明新字段时，渲染结果与现在**完全一致**。
5. **主题未知字段保真。** 通过 `JsonExtensionData` 保留未映射字段，
   使**其他人正在加的主题字段不会因本功能上线而丢失**。
   ⚠️ **注意层级**：`[JsonExtensionData]` 只捕获**同一层级**的未知键。
   顶层未知键挂 `Theme`；嵌套未知键必须挂到对应的 `ColorConfig` / `FontConfig` / `LayoutConfig`。
   详见 §1.4。
6. **命名空间隔离。** 新代码放在 `LiteMonitor.src.UI.Decor` / `LiteMonitor.src.SystemServices.Backdrop` / `LiteMonitor.ThemeEditor`。
7. **每阶段独立可发布、可回滚。** 任一阶段不合预期，可单独回退。

> **命名空间约定（仓库本身不统一，以下为实测值 + 例外清单）**
> - `src/UI/*.cs` → `LiteMonitor`；**例外**：`CleanMemoryForm` / `HardwareInfoForm` / `SettingsForm` 是 `LiteMonitor.src.UI`
> - `src/UI/Controls/*.cs` → `LiteMonitor.src.UI.Controls`；**例外**：`TrendChartControl` 是 `LiteMonitor`
> - `src/UI/Helpers/*.cs` → `LiteMonitor.src.UI.Helpers`（10/10 一致）
> - `src/Core/*.cs` → `LiteMonitor.src.Core`；**例外**：`MetricItem` / `NetworkSpeedTester` / `Settings` / `SettingsHelper` 是 `LiteMonitor`
> - `src/System/HardwareServices/*.cs`、`src/System/*.cs` → `LiteMonitor.src.SystemServices`
> - `src/System/WebServer/*.cs` → `LiteMonitor.src.WebServer`（**不是** `src.SystemServices.WebServer`）
> - `src/System/InfoService/*.cs` → `LiteMonitor.src.SystemServices.InfoService`
> - `src/Plugins/*.cs` → `LiteMonitor.src.Plugins`
> - `src/ThemeEditor/*.cs` → `LiteMonitor.ThemeEditor`（**不是** `src.ThemeEditor`）

---

## 1. 阶段一：主题系统补齐（地基）

**目标**：让主题成为外观能力的完整描述载体，并修复字段丢失。
**风险**：低。**工作量**：约 2–3 天。**可独立发布。**

### 1.1 扩展 `ColorConfig`

文件：`src/Core/ThemeManager.cs` → `ColorConfig`

新增三个可空属性（`string?`，默认 `null` 表示"未声明"）：

| 属性 | 用途 | 建议值 |
|---|---|---|
| `Border` | 面板/组块描边色 | `#AARRGGBB` 或 `rgba()` |
| `Shadow` | 阴影/辉光色 | 半透明色 |
| `TextSecondary` | 次级文字色 | `#RRGGBB` |

保留现有 12 个颜色属性不动。

### 1.2 扩展 `LayoutConfig`

文件：`src/Core/ThemeManager.cs` → `LayoutConfig`

| 属性 | 类型 | 默认 | 用途 | 是否参与缩放 |
|---|---|---|---|---|
| `FrameStyle` | `int` | `0` | 0=圆角(现状) 1=直角切角 2=圆角+描边 3=全息边框 | ❌ |
| `FrameWidth` | `int` | `0` | 描边宽度(px) | ✅ |
| `FrameLength` | `int` | `0` | 切角边长(px) | ✅ |
| `Scanline` | `int` | `0` | 扫描线不透明度 0–100，0=关闭 | ❌ |
| `Glow` | `int` | `0` | 辉光强度 0–100，0=关闭 | ❌ |

> `FrameWidth` / `FrameLength` 属于像素尺寸，需一并加入 `LayoutConfig.Scale`，否则高 DPI 下描边过细。
> `FrameStyle` / `Scanline` / `Glow` 属于枚举/比例，**不缩放**。

### 1.3 `ParseColor` 支持 `rgba()`

文件：`src/Core/ThemeManager.cs` → `ThemeManager.ParseColor`

- 现有分支保持不变（`#` 开头 / 颜色名）
- 新增 `rgba(r,g,b,a)` 解析，a 支持 `0–1`（小数）或 `0–255`（整数）
- 解析失败仍回退全透明（保持现有容错行为）
- 该方法带颜色缓存，扩展时缓存 key 用原始字符串，无需改动

> 备选：把主题里的 `rgba(...)` 全部改写为 `#AARRGGBB`（解析器已支持）。
> 但 7 个主题文件已采用 `rgba()` 写法，**扩展解析器对用户更友好**，两者不冲突。

### 1.4 主题未知字段保真（并行开发关键项，⚠️ 挂错层级就不会生效）

文件：`src/Core/ThemeManager.cs`

**`[JsonExtensionData]` 只捕获同一层级的未知键。** 因此需要**多层级**挂载：

| 挂载位置 | 保护什么 |
|---|---|
| `Theme` | 未来的**顶层**字段（如 `backdrop` 若做成顶层） |
| `ColorConfig` | **现存 7 个主题的 `border` / `shadow` / `textSecondary`** |
| `FontConfig` | **全部 10 个主题的 `font.scale`** |
| `LayoutConfig` | 防御性，为并行开发预留的布局字段 |

写法（以 `ColorConfig` 为例）：

```csharp
[JsonExtensionData] public Dictionary<string, JsonElement>? ExtraFields { get; set; }
```

**实测结论（必须知道的三点）：**
1. `JsonElement` **无生命周期风险** —— 强制 GC 后仍可读取，STJ 内部会 clone，无需额外保活 `JsonDocument`。
2. 扩展槽可正常写回 —— `SaveTheme` 序列化时会自动还原。
3. ⚠️ **键大小写会混杂** —— `SaveTheme` 的 options 只有 `WriteIndented = true`、**无 naming policy**，
   已知属性写成 PascalCase（`"Background"`），扩展数据保留原 camelCase（`"border"`），
   同一文件里两种风格并存。**可接受，但要知道**。

> `ThemeManager.cs` 已 `using System.Text.Json.Serialization;`，无需新增 using。

### 1.5 顺带修掉两个序列化副作用

文件：`src/ThemeEditor/ThemeFileService.cs` → `SaveTheme`；`src/Core/ThemeManager.cs` → `FontConfig`

| 问题 | 修法（选属性级，零侵入） |
|---|---|
| `SaveTheme` 无 `DefaultIgnoreCondition`，`null` 属性会被写成 `"Border": null`，污染 10 个主题文件 | 给新增的可空属性加 `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`（**不动 `SaveTheme` 的 options**，避免影响既有行为） |
| `FontConfig.ItemSize`（`{ get; internal set; }`）每次保存都被写进主题 JSON（实测输出含 `"ItemSize": 0`） | 加 `[JsonIgnore]` |

> 另一条可选修法是给 `SaveTheme` 的 options 加 `DefaultIgnoreCondition = WhenWritingNull`，
> 但那会改变**所有**现有属性的写出行为，与并行开发冲突面更大 → **不采用**。

### 1.6 验证方式

- 加载 `CyberBlue_Neon`，断言能读到 `Color.Border`、`Color.Shadow`、`Color.TextSecondary`
- 加载任意主题，断言能读到 `Font.Scale`（当前是丢失的）
- 用 `ThemeEditorForm` 打开并保存 `CyberBlue_Neon`，再次加载，断言**三个字段 + `font.scale` 均未丢失**，且**输出中不出现 `null` 值**、不出现 `"ItemSize"`**
- 渲染结果与改造前**逐像素一致**（新字段全部缺省关闭）
- **主题 JSON 文件内容在"只读打开不保存"路径下零变化**

---

## 2. 阶段二：机能风（装饰层）

**目标**：实现切角边框、霓虹描边、角标、扫描线、网格、辉光、纹理进度条。
**风险**：中。**工作量**：基础版 2 天，完整版 4–5 天。**依赖阶段一。**

### 2.1 新增绘制工具

文件：`src/Core/UIUtils.cs`（追加静态方法，**不改动既有方法**）

| 方法 | 作用 |
|---|---|
| `CutCornerPath(Rectangle, int radius, int cut)` | 圆角+切角混合路径（`cut=0` 退化为纯圆角） |
| `FramePath(Rectangle, int radius, int cut)` | 按 `FrameStyle` 语义生成边框路径 |
| `GlowPath(Graphics, GraphicsPath, Color, int layers)` | 多层递减 alpha 描边，模拟辉光 |
| `GetTextureBrush(string key, ...)` | **新增独立缓存**，`TextureBrush` 无法用 `GetBrush` |
| `GetGradientBrush(string key, ...)` | **新增独立缓存**，`LinearGradientBrush` 无法用 `GetBrush` |

**⚠️ 三个必须遵守的约束：**

1. **`GlowPath` 不得 Dispose `UIUtils.GetPen` 的返回值。**
   `GetPen` 返回 `_penCache` 中的**共享实例**，`UIRenderer.DrawTextItem`（分割线）等调用方仍在使用。
   Dispose 掉会污染缓存 → 后续 `g.DrawLine(pen, ...)` 拿到已释放对象 → **GDI+ 通用错误**。
   → `GlowPath` 自行 `using var pen = new Pen(...)`，逐层创建逐层释放。
   计划中所有"缓存对象一律不 Dispose"。

2. **`TextureBrush` / `LinearGradientBrush` 需要新缓存。**
   `UIUtils.GetBrush` 返回类型固定为 `SolidBrush`、key 是颜色字符串，对渐变/纹理毫无意义。
   新缓存需：独立上限、随 `UIUtils.ClearBrushCache()` 一起清理、
   **纹理位图与渐变参数（起点/终点色）必须参与 key**，否则主题切换后会串色。

3. **现有缓存上限偏小。**
   `GetPen` 的 `MAX_BRUSH_CACHE = 32`，淘汰策略是**清空全部**。
   辉光 N 层 × M 色会很快撞上限触发"全清" → 缓存抖动。
   → 辉光的 `Pen` 走自建路径（见约束 1），不占用 `GetPen` 缓存。

### 2.2 新增装饰渲染器

文件：**新建** `src/UI/Decor/DecorRenderer.cs`
命名空间：`LiteMonitor.src.UI.Decor`

对外接口（全部静态方法，只依赖 `Graphics` + `Theme`）：

| 方法 | 作用 |
|---|---|
| `DrawWindowFrame(g, bounds, theme)` | 窗体外框：切角/描边/辉光/角标 |
| `DrawGroupFrame(g, bounds, theme)` | 分组块描边 + 辉光 |
| `DrawOverlay(g, bounds, theme)` | 全局覆盖层：扫描线 / 网格底纹 |

设计约束：
- 全部由主题字段驱动，字段缺省时**直接 return**，零视觉变化
- 不触碰 `MetricItem` / 布局计算，纯绘制层
- 不自行创建 `Font`（复用 `Theme` 已构建的字体对象）
- 画刷/画笔：`SolidBrush`/`Pen` 可用 `UIUtils.GetBrush`/`GetPen`（**只读不释放**）；
  渐变/纹理走 §2.1 的新缓存

### 2.3 挂载点（现有文件仅此三处）

| 文件 | 挂载位置 | 说明 |
|---|---|---|
| `src/UI/UIRenderer.cs` → `Render` | 背景填充之后、组块绘制之前 | 调 `DrawWindowFrame` |
| `src/UI/UIRenderer.cs` → `DrawGroupBackground` | 圆角背景填充之后 | 调 `DrawGroupFrame` |
| `src/UI/HorizontalRenderer.cs` → `Render` | 背景填充之后 | 调 `DrawWindowFrame` |

> 竖屏与横屏共用同一套装饰逻辑，保证两种布局观感一致。

### 2.4 圆角改为主题驱动（⚠️ Win11 需要额外一步）

文件：`src/UI/Helpers/MainFormWinHelper.cs` → `ApplyRoundedCorners`

**现状问题：**
- Win11 分支用 DWM 系统圆角（`DWMWCP_ROUND`）→ **切角风格的尖角会被 DWM 裁掉**
- Win10 分支用的是 WinForms 的 `Form.Region` 属性（**不是 `SetWindowRgn` P/Invoke**）
  —— `Region` 接受任意 `GraphicsPath`，**完全能满足切角需求**，Win10/Win11 通用
- 该方法只绑定在 `Resize` 事件上，**主题切换时不重算**（`UIController.ApplyTheme` 全文无调用）
- `Resize` 事件被绑了**两次**（`MainFormWinHelper.InitializeStyle` + `MainForm.BindEvents`）→ 每次 resize 跑两遍

**改造要点：**

1. **统一为"先按 `FrameStyle` 决定 `Form.Region`，再按是否 Win11 决定 DWM 偏好"**，避免两套代码漂移：
   - 切角/直角样式：`Form.Region = new Region(cutCornerPath)` **且** 显式设 `DWMWCP_NONE`(=0) 撤销 DWM 系统圆角
   - 圆角样式：维持现状（Win11 `DWMWCP_ROUND` + `Region = null`；Win10 仅 `Region`）
2. 新增常量 `DWMWCP_NONE = 0`
3. 提供一个可被主题切换调用的公开入口，由 `UIController.ApplyTheme` 在应用新主题后调用一次
4. 顺手解绑重复的 `Resize` 绑定（保留一处）

### 2.5 进度条纹理化

文件：`src/Core/UIUtils.cs` → `DrawBar`

- 现状：底槽圆角实心 + 前景圆角实心，**无渐变、无纹理、无高光**
- 改造：底槽可叠加斜线纹理（`TextureBrush` + 斜线画刷，纹理位图**需缓存**避免每帧重建）；
  前景改 `LinearGradientBrush`；末端可加亮头
- 保持现有三色阈值取色逻辑（`UIUtils.GetStateColor`）不变
- 主题未开启纹理时走原有路径（性能无变化）
- **顺带**：`GetStateColor(state, t, isValueText: false)` 的"安全态"取的是 `t.Color.ValueSafe` 而**不是** `t.Color.BarLow`，
  而 `BarLow` 目前**从未被使用**（既有死字段）→ 可复用于新的进度条配色

### 2.6 任务栏模式的边界

`TaskbarRenderer` **零背景绘制**，且**完全不消费 `Theme`**（颜色硬编码），
背景靠 `TaskbarWinHelper.ApplyLayeredStyle` 的 `LWA_COLORKEY` 镂空，
窗体还被 `TaskbarStrategyWin11.Attach` 用 `SetParent` 挂到 `Shell_TrayWnd` 并设为 `WS_CHILD`
（**子窗口不可能有 DWM 材质**）。

→ **阶段二的装饰效果仅作用于主窗口（竖屏/横屏）。**
→ 任务栏 tooltip 会跟随主题变色（自动受益），但不在装饰层覆盖范围内 —— 验收时需注意区分。
→ 任务栏模式的外观升级（放弃色键、改为自绘底块）列为**阶段四的可选子项**，不在本计划主线。

---

## 3. 阶段三：液态玻璃（背景层）

**目标**：窗口背后呈现模糊 + 折射 + 噪点质感的半透明玻璃。
**风险**：高。**工作量**：**1–2 周**（见 §7.3，原估 3–5 天偏乐观）。**依赖阶段一。**

### 3.1 技术路线决策

| 路线 | 结论 |
|---|---|
| Windows 11 系统材质（`DWMWA_SYSTEMBACKDROP_TYPE`） | **不采用**。理由（按可靠度排序）：<br>① 微软官方的 Win32 系统材质路径需要 **Windows App SDK + `MicaController`/`DesktopAcrylicController` + `Compositor` + `DispatcherQueue`**，对纯 WinForms 项目是重量级新依赖<br>② 官方定义 Mica 是"**an opaque material** that incorporates the user's theme and **desktop wallpaper** ... it only captures the background wallpaper **once**" → **壁纸感知、一次性采样**，与"置顶悬浮条"语义完全不符<br>③ 官方定位 Acrylic "used **only for transient, light-dismiss surfaces** such as flyouts and context menus" → 与常驻悬浮面板不符<br>④ 该属性需 **Win11 build 22621+**，而项目要兼容 Win10（`ApplyRoundedCorners` 有完整 Win10 分支） |
| Aero Glass（`DwmExtendFrameIntoClientArea`） | 备选。模糊为矩形、不跟随圆角，Win10 1803 后效果被淡化 |
| `SetWindowCompositionAttribute`（未文档化） | 备选。跨版本不稳定；且未文档化的 DWM 合成参数改写是杀软误报常见触发点，与项目已有的 PawnIO 驱动叠加会放大拦截风险 |
| **自绘假亚克力** | **采用**。不依赖任何未文档化 API，完整保留透明度/穿透/圆角能力，跨 Win10/11 表现一致，且可做出比系统 Acrylic 更"液态"的观感 |

> **说明**：先前版本把"分层窗口无法使用 DWM 系统材质"当作主要论据。
> 实测该属性在 `WS_EX_LAYERED` 窗口上**调用不被拒绝、值也会被存下来**，
> 其约束属于**渲染期**而非设置期，无法用 API 返回值证明。
> → **已删除该论据**，改用上面四条官方文档依据。结论（采用自绘）不变，但依据更硬。

### 3.2 新增背景服务

文件：**新建** `src/System/Backdrop/BackdropService.cs`
命名空间：`LiteMonitor.src.SystemServices.Backdrop`

**⚠️ 并发模型（必须写死，否则会出撕裂/GDI+ 错误）：**

1. 抓屏 + 模糊在**后台线程**执行（否则卡 UI 线程）
2. 后台线程**只写自己的 `Bitmap`**，绝不碰 UI 线程正在绘制的位图
3. 通过 `Interlocked.Exchange` **原子交换**位图引用；UI 线程读取当前引用并**整帧持有**
4. 装饰动画计时器必须用 `System.Windows.Forms.Timer`（UI 线程回调），
   **不得用 `System.Threading.Timer`**（线程池回调 → 跨线程绘制）
5. 后台线程持有 `Theme` 的**快照**而非读 `ThemeManager.Current`
   （后者是可变静态字段，`ApplyTheme` 整体替换，可能拿到半初始化对象）

### 3.3 新增背景渲染器

文件：**新建** `src/System/Backdrop/BackdropRenderer.cs`
命名空间：`LiteMonitor.src.SystemServices.Backdrop`

职责：把缓存位图画进窗口背景 → 叠加半透明色调层（tint）→ 叠加噪点纹理（亚克力质感来源）→ 绘制边缘高光（liquid 感的关键）

### 3.4 抓屏与模糊实现要点

| 要点 | 做法 |
|---|---|
| 抓屏 | `BitBlt` 屏幕 DC 对应矩形。**弃用 `PrintWindow`**：跨进程取窗常返回黑屏。提权（`app.manifest` 的 `requireAdministrator`）对屏幕 DC 无影响，反而削弱了 UIPI 限制 → 选 `BitBlt` 的理由成立 |
| **排除自身** | ⚠️ **两个直觉方案都不成立**，见下方决策表 |
| 降采样 | 抓取后立即缩放到 1/4 尺寸 |
| 模糊 | 降采样到 1/4 → 写入**中间 `Bitmap`**（`Graphics.FromImage`）→ 第二次 `DrawImage` 缩回目标尺寸。**两次均须设** `InterpolationMode = HighQualityBicubic`、`PixelOffsetMode = HighQuality`、`SmoothingMode = HighQuality`，否则 1/4 降采样再放大必然出现块状锯齿。中间位图复用，不每帧 `new` |
| 缓存 | 缓存低分辨率位图，仅在窗口移动/尺寸变化/节流到期时重算 |
| 节流 | 背景刷新 4–10 fps 即可，**文字仍按原 `RefreshMs` 刷新**，两者解耦 |
| 性能 | 面板宽度约 240px，抓取区域极小，开销可控 |

#### 排除自身的三个可选方案（决策表）

| 方案 | 做法 | 代价 |
|---|---|---|
| **① 移出抓取区**（推荐） | 抓屏时 `SetWindowPos` 把自身临时移开 → 抓 → 移回 | 每帧有 1 次位置抖动风险；实现最直接 |
| ② 上一帧回填 | 抓全屏后用**上一帧的自身区域**回填（自引用缓存） | 无抖动；需多存一份位图 |
| ③ 接受并掩盖 | 直接抓（含自身），靠**强模糊 + tint 层**掩盖 | 成本最低，流媒体背景的通行做法 |

> **为什么不能用 `SetWindowDisplayAffinity`**：微软文档定义 `WDA_EXCLUDEFROMCAPTURE`(0x11, Win10 2004+) 为
> "the window does not appear at all" → 抓屏里留下一个**洞**，而不是露出真实背景。
> 对液态玻璃是**反效果**（玻璃要的是真实背景）。同文档亦写明"no guarantee ... will strictly protect windowed content"。
> **为什么不能用"按窗口位置偏移抓取"**：窗口**物理遮挡**着那块屏幕区域，偏移矩形同样覆盖自身像素，几何上不成立。

### 3.5 主题字段

在 `Theme` 下新增 `BackdropConfig`（可空对象）：

| 属性 | 默认 | 用途 |
|---|---|---|
| `Mode` | `"solid"` | `solid` / `gradient` / `glass` |
| `Opacity` | `0.75` | 玻璃不透明度 |
| `Blur` | `12` | 模糊强度 |
| `Tint` | `null` | 色调层颜色 |
| `Noise` | `0` | 噪点强度 0–100 |
| `EdgeGlow` | `0` | 边缘高光强度 0–100 |
| `Fps` | `6` | 背景刷新帧率上限 |

**⚠️ "null = 关闭"的假设脆弱：**
本仓库的 `Theme` / `LayoutConfig` / `FontConfig` / `ThresholdConfig` / `ColorConfig` **全部**用 `= new()` 初始化器。
只要新属性照抄这个风格，缺键时得到的是**非 null 默认实例**（`Mode="solid"`），"null=关闭"直接失效。

**采用判据（不依赖 null）：** 渲染入口统一写

```
if (t.Backdrop?.Mode != "glass") { 走原路径; return; }
```

这种写法在"有/无初始化器"两种情况下都正确。**推荐此方案**，不依赖 null 语义。

### 3.6 挂载点

| 文件 | 挂载位置 | 说明 |
|---|---|---|
| `src/UI/UIRenderer.cs` → `Render` | 替换现有背景填充调用 | 按 `Backdrop.Mode` 分派：solid / gradient / glass |
| `src/UI/HorizontalRenderer.cs` → `Render` | 替换现有背景填充调用 | 同上 |

> **溢出绘制策略两处不同，不要照抄：**
> - **竖屏**：现状**已有** `(-5,-5)` 溢出绘制 → 改为玻璃/渐变后**保留**该策略，否则边缘漏黑边
> - **横屏**：现状从 `(0,0)` 起填，**没有**溢出策略 → 改玻璃后**需新引入**同样的溢出绘制（属新增行为，验收单列一条）

### 3.7 已知风险与对策

| 风险 | 对策 |
|---|---|
| 抓屏包含窗口自身 | 见 §3.4 决策表三选一 |
| 全屏应用下抓屏偏色 | 用 `Tint` 色调层统一校正 |
| 抓屏在部分驱动/远程桌面下失败 | 捕获异常后回退到纯色背景，不影响可用性 |
| 高频抓屏导致 GPU 占用上升 | 严格节流 + 低分辨率缓存 + 尺寸变化才重算 |
| 玻璃与 `Opacity` 叠加导致过淡 | 玻璃自身不透明度与窗口 `Opacity` 是**两级**（全局 alpha 会同比例削弱文字与背景、降低对比度），需分别调参 |
| **首帧跳变** | `MainForm` 构造时 `this.Opacity = 0` + `StartFadeIn` 渐入，而玻璃首帧需先抓屏 → 会看到"先纯色后玻璃"的跳变。→ 玻璃模式启动时用 `t.Color.Background` 作首帧底色，渐入完成后再切玻璃 |

---

## 4. 阶段四：主题编辑器与打磨

**目标**：让外观新字段可在可视化编辑器中编辑，并做兼容性与性能收尾。
**风险**：低～中。**工作量**：约 3–4 天（`ThemeEditorForm` 是 722 行纯代码 UI，非偏乐观）。**依赖阶段一至三。**

### 4.1 主题编辑器支持新字段

文件：`src/ThemeEditor/ThemeEditorForm.cs`（+ `ThemeEditorForm.resx`）
命名空间：`LiteMonitor.ThemeEditor`

- `ColorConfig` 新增的 Border / Shadow / TextSecondary → 颜色选择器
- `LayoutConfig` 新增的 FrameStyle / FrameWidth / FrameLength / Scanline / Glow → 数值/下拉控件
- `BackdropConfig` 整组 → 新增一个"背景材质"分组
- 新控件在字段缺省时显示为"未设置"，保存时**保持缺省**：
  靠 §1.5 的 `[JsonIgnore(Condition = WhenWritingNull)]` 保证不写出 `null`

> `ThemeEditorForm` 是仓库内较大的文件之一，若并行开发也在改它，
> 建议把新控件构建拆到**独立新文件** `src/ThemeEditor/AppearanceFields.cs`（命名空间 `LiteMonitor.ThemeEditor`），
> 由 `ThemeEditorForm` 调用一次挂载。

### 4.2 主题预览

文件：`src/ThemeEditor/ThemePreviewControl.cs` → `CreatePreviewTheme` / `CreateScaledTheme`

- `OnPaint` 内部调用 `UIRenderer.Render` → 阶段二若把装饰挂进 `UIRenderer`，**预览自动生效** ✅
- ⚠️ **但两处 `new LayoutConfig { ... }` 各自手写字段拷贝**，只复制 10 个字段 →
  新增的 `FrameStyle`/`FrameWidth`/`FrameLength`/`Scanline`/`Glow` **会被静默丢成默认值（= 关闭）**，预览里永远看不到装饰
- **修法（两选一）**：
  - 简单：两处拷贝补齐新字段
  - 更稳：在 `ThemeManager.cs` 加 `LayoutConfig.CopyLayoutFrom(LayoutConfig src)`（或 `Clone()`），两处统一调用 → 今后再加字段不会再漏
- `Color = original.Color` 是引用共享 → 新增的 `Border`/`Shadow`/`TextSecondary` 能透传 ✅
- `Backdrop` 是 `Theme` 顶层属性，`new Theme{...}` 不复制 → 预览恒为无玻璃（与 §4.3 说明一致）

### 4.3 网页版面板的取舍（需拍板）

文件：`src/System/WebServer/WebPageContent.cs`（599 行，自带 `--bg`/`--c-0..2`/`--card`/`--border` 与十余处硬编码色）

该文件对 `ThemeManager` / `ParseColor` 引用数为 **0** → **切到 CyberBlue 主题后网页版纹丝不动**。
用户若开启 WebServer，会看到"App 是赛博朋克、网页是默认灰黑"的割裂。

**两个选项：**

| 选项 | 做法 | 代价 |
|---|---|---|
| A. 声明在范围外 | 在 §0.3 的消费点全景里标注"网页版不跟随主题"，本计划不处理 | 0 成本，但用户会看到割裂 |
| B. 补上（**性价比更高**） | 在 `LiteWebServer` 渲染页面时，把 `ThemeManager.Current.Color` 生成的 8~10 个 CSS 变量注入 `<style>` | 改动面很小（`WebPageContent` 是字符串拼装，注入点明确） |

**建议选 B**，但需先确认 `WebPageContent` 的 HTML 是如何拼装的（是否有统一的 CSS 变量块可替换）。

### 4.4 兼容性与性能收尾

- 新增主题字段在旧版 LiteMonitor 上打开会被忽略（`System.Text.Json` 默认行为），无需处理
- 玻璃模式在低端机 / 老显卡上的降级策略（自动降级到 gradient 或 solid）
- 扫描线/网格/辉光的绘制成本评估，避免在 1 秒刷新下产生可感卡顿
- `ThemeManager` / `UIUtils` 的画刷、画笔、位图缓存需确认无句柄泄漏
- ⚠️ `ThemeManager.ClearCaches()` **已实现但全仓无任何调用点（死代码）** → 主题切换后颜色缓存不失效。
  装饰层若缓存派生自主题的画刷，**必须显式接线**（在 `UIController.ApplyTheme` 里调用）。
  同时注意 `ApplyTheme` 目前调的 `UIRenderer.ClearCache()` 与 `UIUtils.ClearBrushCache()` 最终都落到同一个方法 → **重复调用，可顺手合并**。

---

## 5. 里程碑与验收

| 阶段 | 内容 | 验收标准 | 可发布 |
|---|---|---|---|
| 一 | 主题 schema + rgba + 未知字段保真 | 7 个主题的 border/shadow/textSecondary + 全部主题的 font.scale 可读；编辑器保存后**字段不丢、不产生 null、不产生 ItemSize**；渲染与改造前一致 | ✅ |
| 二 | 机能风装饰层 | 切角/描边/辉光/扫描线/角标/网格生效；10 个主题均可配置；**Win11 下切角不被 DWM 裁掉**；性能无明显回退 | ✅ |
| 三 | 液态玻璃 | 玻璃背景在 Win10/Win11 表现一致；保留透明度/穿透/圆角；节流有效；**竖屏与横屏均无黑边**；首帧无跳变 | ✅ |
| 四 | 编辑器 + 打磨 | 新字段可视化编辑；**预览能显示装饰**；网页版策略落地；低端机降级正常 | ✅ |

**每阶段结束跑一次渲染对比**：同一主题同一数据下，改造前后的输出应**逐像素一致**
（阶段一、二的默认路径），确保"不启用新外观 = 零变化"这条底线。
追加一条：**主题 JSON 文件内容在只读路径下零变化**。

---

## 6. 改动文件清单

### 新增（与并行开发零冲突）

```
src/UI/Decor/DecorRenderer.cs              阶段二   namespace LiteMonitor.src.UI.Decor
src/System/Backdrop/BackdropService.cs     阶段三   namespace LiteMonitor.src.SystemServices.Backdrop
src/System/Backdrop/BackdropRenderer.cs    阶段三   namespace LiteMonitor.src.SystemServices.Backdrop
src/ThemeEditor/AppearanceFields.cs        阶段四   namespace LiteMonitor.ThemeEditor
```

### 修改（每个文件仅少量挂载点）

```
src/Core/ThemeManager.cs           阶段一：ColorConfig / FontConfig / LayoutConfig 加字段+扩展槽；
                                   ParseColor 加 rgba()；LayoutConfig.Scale 加新尺寸字段；
                                   可选：LayoutConfig.CopyLayoutFrom 辅助方法
src/Core/UIUtils.cs                阶段二：CutCornerPath / FramePath / GlowPath /
                                   GetTextureBrush / GetGradientBrush；DrawBar 纹理分支
src/UI/UIRenderer.cs               阶段二、三：3 处挂载点 + 背景分派
src/UI/HorizontalRenderer.cs       阶段二、三：2 处挂载点 + 背景分派 + 溢出绘制
src/UI/Helpers/MainFormWinHelper.cs 阶段二：圆角策略统一 + DWMWCP_NONE + 主题切换入口 + 解绑重复 Resize
src/UI/UIController.cs             阶段二：ApplyTheme 末尾调圆角重算 + 接线 ThemeManager.ClearCaches()
src/ThemeEditor/ThemePreviewControl.cs  阶段四：两处 LayoutConfig 拷贝补齐
src/ThemeEditor/ThemeEditorForm.cs       阶段四：挂载新字段 UI
src/System/WebServer/WebPageContent.cs  阶段四（可选 B）：CSS 变量注入
src/Core/Settings.cs               阶段三（可选）：1 行新增 AppearanceConfig 属性
```

> `src/Core/Settings.cs` 是并行开发高概率改动文件。
> 为降低冲突，**阶段一、二完全不触碰它** —— 外观配置全部走主题 JSON。
> 只有阶段三需要"全局覆盖"时才新增，且尽量控制在 1 行。

---

## 7. 建议执行顺序与决策点

1. **先做阶段一**（地基）—— 纯增量、风险最低，且立刻修复主题字段丢失这个真实 bug，
   同时为并行开发提供保护（`JsonExtensionData` 多层级挂载）。
2. **再做阶段二**（机能风）—— 用户感知最直接，且不涉及抓屏等系统级调用。
3. **阶段三（液态玻璃）先做技术验证，作为前置门禁。**
   鉴于 §3.4 的"排除自身"两个直觉方案均不成立、跨屏/DPI/RDP/多显示器负坐标等边界未实测，
   建议先写一个**最小验证程序**（只验证：抓屏 + 排除自身 + 模糊质量 + 自身区域伪影），
   拿到像素级对比截图后再投入完整实现。
   → 阶段三整体 **1–2 周**，不设 3–5 天的乐观预期。
4. **阶段四**收尾。

### 待确认的设计决策

| 决策点 | 选项 | 建议 |
|---|---|---|
| 玻璃实现路线 | 自绘假亚克力 / 系统 Acrylic / Aero Glass | 自绘假亚克力（依据见 §3.1） |
| 外观配置归属 | 全部进主题 JSON / 进全局 Settings | 阶段一二全进主题；阶段三再考虑全局覆盖 |
| 主题开关判据 | 依赖 `null` / 依赖 Mode 值 | 依赖 Mode 值（§3.5），不依赖 null |
| 网页版是否跟随主题 | A 声明不管 / B 注入 CSS 变量 | B |
| 任务栏模式是否支持新外观 | 支持（需重做背景层）/ 暂不支持 | 暂不支持，列为后续项 |
| 硬编码界面的自绘控件是否主题化 | 是 / 否 | 否（明确写在 §0.3，避免反复被提） |
| 是否允许闭源分发 | — | MIT 允许闭源分发，但需保留原版权声明与许可证全文 |

---

## 附录 A：关键 API 参考

| 用途 | API | 备注 |
|---|---|---|
| 系统材质（**未采用**） | `DwmSetWindowAttribute(hwnd, 38, &type, 4)` | type: 2=Mica 3=Acrylic；需 Win11 build 22621+；官方 Win32 路径需 Windows App SDK；Mica 为壁纸感知、一次性采样 |
| 系统圆角（现有） | `DwmSetWindowAttribute(hwnd, 33, &pref, 4)` | pref: **0= NONE（切角必需）/ 2=ROUND** |
| 自绘区域（切角用） | WinForms `Form.Region` 属性 + `GraphicsPath` | **不是 `SetWindowRgn` P/Invoke**；`Region` 接受任意 path，Win10/Win11 通用 |
| 屏幕抓取 | `BitBlt`（屏幕 DC） | 比 `PrintWindow` 可靠；提权不影响屏幕 DC |
| 排除自身 | 移出抓取区 / 上一帧回填 / 强模糊掩盖 | `SetWindowDisplayAffinity` 会留洞，**不适用** |
| 模糊 | 两次 `DrawImage` 缩放 + 中间 `Bitmap` | 须设 `InterpolationMode.HighQualityBicubic` 等 |
| 噪点 | 预生成 `Bitmap` + `TextureBrush` | 缓存复用，避免每帧重建 |
| 分层窗口（透明度/穿透） | `WS_EX_LAYERED` / `WS_EX_TRANSPARENT` | 现有实现保留，玻璃路线不得触碰 |

## 附录 B：调研中发现的既有缺陷（与外观改造相邻，建议一并修）

| 缺陷 | 位置 | 说明 | 处理阶段 |
|---|---|---|---|
| 主题嵌套字段被丢弃 | `src/ThemeEditor/ThemeFileService.cs` → `SaveTheme` | 序列化 `Theme` 对象，未映射键直接丢 | 阶段一 §1.4 |
| `font.scale` 被丢弃 | `src/Core/ThemeManager.cs` → `FontConfig` | 全部 10 个主题都声明了，但无对应属性 | 阶段一 §1.4 |
| 保存时写出 `null` | `src/ThemeEditor/ThemeFileService.cs` → `SaveTheme` | 无 `DefaultIgnoreCondition` | 阶段一 §1.5 |
| 写出 `ItemSize` | `src/Core/ThemeManager.cs` → `FontConfig` | 内部 setter 泄漏到 JSON | 阶段一 §1.5 |
| 换主题不刷新圆角 | `src/UI/Helpers/MainFormWinHelper.cs` → `ApplyRoundedCorners` | 只绑 `Resize`；`ApplyTheme` 无调用 | 阶段二 §2.4 |
| `Resize` 重复绑定 | `MainFormWinHelper.InitializeStyle` + `MainForm.BindEvents` | 每次 resize 跑两遍 | 阶段二 §2.4 |
| 字体缩放未乘 DPI | `src/Core/ThemeManager.cs` → `Theme.Scale` | 布局用 `dpiScale*userScale`，字体只用 `userScale`；高 DPI 下字号偏小 | 阶段二（可选） |
| `ClearCaches()` 是死代码 | `src/Core/ThemeManager.cs` → `ThemeManager.ClearCaches` | **已实现但全仓无调用点** → 主题切换后颜色缓存不失效（不是"无机制"，是"没接线"） | 阶段四 §4.4 |
| `ApplyTheme` 重复清缓存 | `src/UI/UIController.cs` → `ApplyTheme` | `UIRenderer.ClearCache()` 与 `UIUtils.ClearBrushCache()` 最终落到同一方法 | 阶段四 §4.4 |
| 预览丢新字段 | `src/ThemeEditor/ThemePreviewControl.cs` → `CreatePreviewTheme` / `CreateScaledTheme` | 两处手写 `LayoutConfig` 拷贝 | 阶段四 §4.2 |
| 网页版与主题零耦合 | `src/System/WebServer/WebPageContent.cs` | 切主题后网页版不变 | 阶段四 §4.3 |

## 附录 C：复审修订记录（v2）

独立复审发现 19 项问题，已全部处理。要点：

| 级别 | 问题 | 处理 |
|---|---|---|
| **P0** | `[JsonExtensionData]` 挂 `Theme` 上对嵌套字段无效 → 计划宣称的 bug 修复不会发生 | §1.4 改为**多层级挂载**（`Theme` + `ColorConfig` + `FontConfig` + `LayoutConfig`） |
| **P0** | `GlowPath` "用完即 Dispose" 与 "复用 `GetPen` 缓存" 自相矛盾 → 会释放共享 Pen 导致 GDI+ 错误 | §2.1 约束 1：`GlowPath` 自建 `Pen`，**禁止 Dispose 缓存对象** |
| **P0** | `TextureBrush`/`LinearGradientBrush` 无法进入 `GetBrush`（返回 `SolidBrush`） | §2.1 约束 2：新增独立缓存 |
| P1 | Win11 切角需显式 `DWMWCP_NONE`，否则 DWM 仍裁尖角 | §2.4 补 `DWMWCP_NONE` 常量与策略 |
| P1 | 圆角实现是 `Form.Region` 而非 `SetWindowRgn`，措辞错误 | §2.4 / 附录 A 更正 |
| P1 | `SetWindowDisplayAffinity` 会留洞、"偏移抓取"几何不成立 | §3.4 改为三方案决策表 |
| P1 | 模糊缺 GDI+ 关键设置与中间位图 | §3.4 补全 |
| P1 | `SaveTheme` 会写出 `null` | §1.5 改用属性级 `JsonIgnore(WhenWritingNull)` |
| P1 | `BackdropConfig` 的 "null=关闭" 与仓库 `= new()` 风格冲突 | §3.5 改用 Mode 值判据 |
| P1 | `(-5,-5)` 溢出只存在于竖屏，横屏是新增 | §3.6 拆分说明 |
| P1 | 玻璃首帧与 `Opacity` 渐入会跳变 | §3.7 补风险行 |
| P2 | 网页版与主题零耦合（完全遗漏） | §0.3 补入 + §4.3 决策点 |
| P2 | 预览的两处 `LayoutConfig` 拷贝会丢新字段 | §4.2 补修法 |
| P2 | 8+ 个主题消费点未列出 | §0.3 全景表 |
| P2 | 命名空间表不完整/有例外 | §0.4 补例外清单 |
| P2 | "颜色缓存无失效机制"表述有误 | 附录 B 更正为"死代码，没接线" |
| P2 | 阶段三并发模型未定义 | §3.2 写死 5 条约束 |
| P2 | 工作量偏乐观 | 阶段三改为 1–2 周，阶段四 3–4 天；阶段三增设前置技术验证门禁 |
| P3 | 3 位 `#RGB` 不支持 / `rgba()` 静默失效 / `BarLow` 死字段 / 托盘图标 / `ItemSize` | §0.1、§2.5、附录 B 分别记录 |

**保留的优点**（复审确认值得坚持）：新增优先与单挂载点策略、任务栏能力边界的主动划定、
`JsonExtensionData` 保护并行开发者字段的动机、阶段三先做技术验证的判断、
拒绝未文档化 `SetWindowCompositionAttribute`、`不启用新外观 = 零变化`的验收底线。
