# 为 Pak 格式新增「完整块」高级选项

## Summary

在「新建下载 → 高级选项」中为两种 pak 输出格式（单文件 pak / 多文件 pak）新增复选框 **「完整块」**：

* 不勾选（默认）：保持现有行为，只下载选区范围内覆盖到的瓦片。

* 勾选后，把每个层级的枚举范围扩展到 pak 的存储分块边界：

  * **z ≤ 9**（写入 `blocks` 表 / `blocks.pak`）：扩展为**该层级全图**，即列/行均取 `0 … 2^z-1`。

  * **z ≥ 10**（写入 `blocks_{z}_{tx}_{ty}`）：扩展为覆盖**完整 512×512 分块**，即列/行取 `tx*512 … tx*512+511`（并在层级边缘裁剪到 `2^z-1`）。

因此产物语义变为：`blocks` 含该层级全部瓦片，`blocks_x_x_x` 含 512×512 全部瓦片。

## 现状分析

| 关注点            | 现状位置                                                                                                                                                                                                                                                                                            |
| -------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 瓦片枚举           | [TileDownloadEngine.cs](file:///c:/Users/JHB/source/repos/TileDownloader/Services/TileDownloadEngine.cs#L57-L87) 用 `TileUrlBuilder.ColRange/RowRange` 按层级枚举，并据此上报层级总数                                                                                                                           |
| 范围数学 / 数量预估    | [TileUrlBuilder.cs](file:///c:/Users/JHB/source/repos/TileDownloader/Services/TileUrlBuilder.cs#L36-L67)（`ColRange`/`RowRange`）、[TileUrlBuilder.cs](file:///c:/Users/JHB/source/repos/TileDownloader/Services/TileUrlBuilder.cs#L130-L158)（`CalculateLevelTileCount`/`CalculateTotalTileCount`） |
| 分块规则（单一文件 pak） | [PakBlock.cs](file:///c:/Users/JHB/source/repos/TileDownloader/Models/PakBlock.cs#L20-L28)：`z < 10 → "blocks"`，否则 `blocks_{z}_{x/512}_{y/512}`                                                                                                                                                  |
| 分块规则（多文件 pak）  | [MultiPakTileStore.cs](file:///c:/Users/JHB/source/repos/TileDownloader/Services/Stores/MultiPakTileStore.cs#L322-L333)：`z <= 9 → blocks`，否则 `blocks_{z}_{tx}_{ty}`；常量 `BlockSize = 512`、`SingleFileMaxLevel = 9`                                                                               |
| 分表/分文件创建       | [PakTileStore.cs](file:///c:/Users/JHB/source/repos/TileDownloader/Services/Stores/PakTileStore.cs#L65-L97) 预建 `blocks*` 表；[MultiPakTileStore.cs](file:///c:/Users/JHB/source/repos/TileDownloader/Services/Stores/MultiPakTileStore.cs#L64-L106) 预扫描已存在的分块文件                                   |
| 请求模型           | [TileDownloadRequest.cs](file:///c:/Users/JHB/source/repos/TileDownloader/Models/TileDownloadRequest.cs)（UI → 引擎入参 + 快照为 `TaskRecord`）                                                                                                                                                          |
| 任务持久化          | [TaskRecord.cs](file:///c:/Users/JHB/source/repos/TileDownloader/Models/TaskRecord.cs)、[TaskManager.cs](file:///c:/Users/JHB/source/repos/TileDownloader/Services/TaskManager.cs#L27-L31)（`UseAutoSyncStructure(true)`，库文件 `%LOCALAPPDATA%\TileDownloader\tasks.db`，不会丢历史任务）                    |
| 续传重建请求         | [TasksViewModel.cs](file:///c:/Users/JHB/source/repos/TileDownloader/ViewModels/TasksViewModel.cs#L162-L182)（`RebuildRequest`）                                                                                                                                                                  |
| UI             | [NewDownloadPage.xaml](file:///c:/Users/JHB/source/repos/TileDownloader/Views/NewDownloadPage.xaml#L262-L319)「高级选项」Expander；[NewDownloadViewModel.cs](file:///c:/Users/JHB/source/repos/TileDownloader/ViewModels/NewDownloadViewModel.cs#L452-L547)（范围/层级/预估数量/格式/输出路径）                        |

关键观察：**展开分块边界不会改变分表/分文件的集合**。因为 `firstCol/512` 在展开前后相同，`lastCol` 展开后为 `(lc/512)*512+511`，其 `/512` 仍等于 `lc/512`。所以 `PakTileStore`/`MultiPakTileStore` 的表/文件枚举逻辑**无需修改**，只在每个块内部多下载瓦片。

## 设计决策（已与用户确认）

1. 选项对**两种 pak 格式**可见且生效；MBTiles / 瓦片目录不受影响（选项在这些格式下隐藏，取值被忽略）。
2. z ≤ 9 勾选后扩展为**该层级全图**（`0 … 2^z-1`，全部行与列），下载量会显著增加（z=0..9 合计约 35 万张）。
3. 展开规则按层级分别计算（不能只放大 EPSG:4326 包围盒，因为不同层级的块边界不对应同一经纬度）。
4. 分块常量（512 / 9）统一由 `TileUrlBuilder` 提供，两个 store 引用同一来源，避免规则漂移。
5. `SaveTileAsync` / `TileExistsAsync` 仍以 (z,x,y) 为准，不做任何改动；续传逻辑天然支持（存在即跳过）。
6. pak 内 `infos` 表继续记录**用户选区**范围（保持原语义，不改），不做「展开后范围」回写。

## 变更清单

### 1) [TileUrlBuilder.cs](file:///c:/Users/JHB/source/repos/TileDownloader/Services/TileUrlBuilder.cs) — 范围展开数学

* 新增公共常量：`public const int BlockSize = 512;`、`public const int PakSingleFileMaxLevel = 9;`（注释说明与 `PakBlock.GetTable` / 多文件 pak 命名规则一致）。

* `ColRange(double minLon, double maxLon, int z, bool fullBlock = false)`：

  * `fullBlock == false`：现有逻辑不变。

  * `fullBlock == true` 且 `z <= PakSingleFileMaxLevel`：返回 `(0, (int)2^z - 1)`。

  * `fullBlock == true` 且 `z > PakSingleFileMaxLevel`：先按现有方式算出 `(first,last)` 并 `Clamp(0, max)`，再返回 `(first / BlockSize * BlockSize, Math.Min(last / BlockSize * BlockSize + BlockSize - 1, max))`。

* `RowRange(double minLat, double maxLat, int z, bool fullBlock = false)`：同上，行号 `2^z-1` 为上界。

* `CalculateLevelTileCount(..., int z, bool fullBlock = false)` 与 `CalculateTotalTileCount(..., int minLevel, int maxLevel, bool fullBlock = false)`：透传 `fullBlock` 给上面的重载。

* 默认参数保证所有既有调用点（含 `TileImageLoader`、两个 store）行为不变。

### 2) [TileDownloadRequest.cs](file:///c:/Users/JHB/source/repos/TileDownloader/Models/TileDownloadRequest.cs) — 请求字段

* 新增 `/// <summary>是否完整块（仅 pak 格式生效）</summary> public bool FullBlock { get; set; }`。

* `CalculateTotalTiles()` 传入 `FullBlock`；`ToRecord()` 写入 `FullBlock = FullBlock`。

* 说明：预估数量、报表层级总数、实际枚举三者必须使用同一个 `FullBlock`，否则进度会算错。

### 3) [TaskRecord.cs](file:///c:/Users/JHB/source/repos/TileDownloader/Models/TaskRecord.cs) — 持久化（续传保真）

* 新增 `[Column(Name = "full_block")] public bool? FullBlock { get; set; }`。

* 用 `bool?`（可空）而非 `bool`：旧任务库由 `AutoSyncStructure` 以 `ALTER TABLE` 增列，历史行该列为 `NULL`，可空类型可安全读出 `null`（读取端统一按 `== true` 判定），避免非空 bool 读 `NULL` 的边界问题。

### 4) [TileDownloadEngine.cs](file:///c:/Users/JHB/source/repos/TileDownloader/Services/TileDownloadEngine.cs) — 按展开范围枚举

* 两处 `ColRange/RowRange` 调用（上报层级总数、实际枚举）改为传入 `request.FullBlock`。

* 其余逻辑（并发、重试、404 跳过、存在性跳过）不动；`TileTaskOptions` **不新增字段**（store 侧不需要）。

### 5) [NewDownloadViewModel.cs](file:///c:/Users/JHB/source/repos/TileDownloader/ViewModels/NewDownloadViewModel.cs) — 选项与预估

* 新增：

  ```csharp
  /// <summary>是否完整块（仅 pak 格式生效）</summary>
  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(EstimatedTileCount))]
  [NotifyPropertyChangedFor(nameof(EstimatedTileCountText))]
  [NotifyPropertyChangedFor(nameof(StartDownloadButtonText))]
  private bool _fullBlock;
  ```

* 新增只读派生属性：

  * `public bool IsPakFormat => FormatId is "Pak" or "MultiPak";`（控制选项可见性）

  * `private bool EffectiveFullBlock => IsPakFormat && FullBlock;`（非 pak 格式忽略勾选值）

* `OnFormatIdChanged(string value)`：在既有逻辑前追加 `OnPropertyChanged(nameof(IsPakFormat));`、`OnPropertyChanged(nameof(EstimatedTileCount));`、`OnPropertyChanged(nameof(EstimatedTileCountText));`、`OnPropertyChanged(nameof(StartDownloadButtonText));`。

* `EstimatedTileCount` 改为 `TileUrlBuilder.CalculateTotalTileCount(Range.MinX, Range.MaxX, Range.MinY, Range.MaxY, min, max, EffectiveFullBlock)`。

* `StartDownloadAsync` 构造请求时加 `FullBlock = EffectiveFullBlock,`。

* 不修改任务名（`Name`），不新增确认弹窗（保持改动最小）。

### 6) [TasksViewModel.cs](file:///c:/Users/JHB/source/repos/TileDownloader/ViewModels/TasksViewModel.cs) — 续传重建

* `RebuildRequest` 中补 `FullBlock = record.FullBlock == true,`，保证「继续」时与首次下载使用相同扩展范围。

### 7) [NewDownloadPage.xaml](file:///c:/Users/JHB/source/repos/TileDownloader/Views/NewDownloadPage.xaml) — 高级选项 UI

在高级选项 Expander 内、「输出路径」区块之后追加（仅 pak 格式可见）：

```xml
<!-- 完整块（仅 pak 格式） -->
<CheckBox Margin="0,12,0,0"
          Content="完整块"
          IsChecked="{Binding FullBlock, Mode=TwoWay}"
          Visibility="{Binding IsPakFormat, Converter={StaticResource BoolToVis}}" />
<TextBlock Style="{StaticResource HintText}"
           Margin="0,4,0,0"
           Text="勾选后：z≤9 的 blocks 写满该层级全部瓦片；z≥10 的 blocks_{z}_{tx}_{ty} 写满 512×512 整块（下载量会明显增加）"
           Visibility="{Binding IsPakFormat, Converter={StaticResource BoolToVis}}" />
```

`BoolToVis` 已在 App.xaml 注册；控件用普通 `CheckBox`（与页面中 `RadioButton` 用法一致，走 Wpf.Ui 隐式样式）。

## Assumptions & Decisions

* 选项仅影响 pak 两种格式；切换格式时保留勾选状态但不生效（隐藏 + `EffectiveFullBlock` 兜底）。

* z ≤ 9 的「全图」按**任务层级范围内**的每个层级计算（例如任务 z=5..12 时，只对 z=5..9 展开为全图，不会额外下载 z=0..4）。

* pak 的 `infos` 元数据保持写入选区范围，不做展开范围回写。

* 展开后分表/分文件集合不变，故 `PakTileStore` / `MultiPakTileStore` / `TileTaskOptions` 不改动。

* 预计下载量可能极大（如 z=9 单层 262,144 张），不新增二次确认，仅由预估数量与按钮文案体现。

## 验证步骤

1. 构建：在 `c:\Users\JHB\source\repos\TileDownloader` 执行 `dotnet build TileDownloader.csproj -c Debug`，确认 0 error。
2. 预估一致性：新建下载页选择「单文件 pak」，框选一个小范围、层级设 `10–10`，观察「预估总量」；勾选「完整块」后数值应变为 512×512=262,144 的整数倍（被裁剪的边缘块除外）；切换到 MBTiles 后该复选框应隐藏且预估数量回到选区值。
3. 实际产物（单文件 pak）：以 z=10..10 单块范围跑一次勾选任务，用 SQLite 工具核对 `SELECT COUNT(*) FROM blocks_10_tx_ty;` 应为 262,144（边缘块为裁剪后数量）；不勾选时该值应等于选区瓦片数。
4. z ≤ 9：以 z=2..9 小范围跑一次勾选任务，核对 `SELECT z, COUNT(*) FROM blocks GROUP BY z;`，每个 z 的计数应为 `4^z`（z=2 → 16，z=9 → 262,144）。
5. 续传：对第 3/4 步的产物在任务中心中断后点「继续」，确认继续时仍按完整块范围补齐（已完成瓦片全部跳过、进度总数与首次一致），且重启应用后从 `tasks.db` 恢复的任务同样保持该行为。
6. 回归：不勾选时，pak / MBTiles / 瓦片目录三种格式的计数与产物结构均与改动前一致。

