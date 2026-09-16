# TileDownloader 升级与重构 Spec

## Why
当前项目依赖陈旧（MaterialDesignThemes 4.1、Prism 8、BruTile 3.1、FreeSql 2.5、XAML.MapControl 5.6），界面布局陈旧且交互分散；下载流程暴露过多配置细节（并发、重试、UA/Referer/Cookies 动态表单），普通用户难以上手；下载进度仅一个进度条，缺乏可视化；外部地图控件笨重且样式难以统一；瓦片源仅能输出单文件 pak 格式；代码目录扁平、下载逻辑直接耦合在 Models 与 ViewModel 之间。此外现有 GSCloud/天地图特殊下载逻辑属于特定平台 hack，不再维护。

## What Changes
- 升级 NuGet 依赖至最新稳定版（目标框架保持 net10.0-windows）：
  - CommunityToolkit.Mvvm 8.4.0 替换 Prism.DryIoc（**BREAKING**：MVVM 框架更换，[ObservableProperty]/[RelayCommand] 源生成器模式）
  - Microsoft.Extensions.DependencyInjection 10.x 承担依赖注入（替换 Prism 容器）
  - WPF-UI 4.3.0 替换 MaterialDesignThemes（**BREAKING**：UI 框架更换）
  - FreeSql.Provider.Sqlite 3.5.311、BruTile 6.0.0、NetTopologySuite 2.6.0、ProjNet 2.0.0（保持最新）、Newtonsoft.Json 13.0.4
  - **BREAKING**：移除 XAML.MapControl 与 NetTopologySuite.IO.GeoJSON，地图控件完全自研
- MVVM 重构（**BREAKING**）：
  - App.xaml 改为标准 WPF Application + Microsoft.Extensions.DependencyInjection 构建 ServiceProvider
  - ViewModel 继承 ObservableObject，命令与属性使用 CommunityToolkit 源生成器；页面由 DI 构造并注入 ViewModel，移除 Prism ViewModelLocator
- 自研地图控件（**BREAKING**）：
  - 纯 WPF 自定义控件（无外部地图库）：基于 XYZ 瓦片方案的 Canvas/WriteableBitmap 渲染
  - 支持：鼠标拖拽平移、滚轮缩放（含惯性）、按缩放级别异步加载瓦片（内存 + 磁盘缓存）、背景加载不卡 UI、图源 URL 模板与密钥替换
  - 框选交互：拖拽绘制选区矩形并高亮，选区范围（Envelope，EPSG:4326）双向绑定到 ViewModel
  - 半透明信息浮层显示当前中心坐标/缩放级别/选区范围
- 极简下载流程（**BREAKING**：交互重构）：
  - 用户仅需三步：选择地图来源 → 在地图上框选范围 → 点击「开始下载」
  - 层级范围默认自动（跟随地图当前缩放 ± 预设跨度），输出格式默认多文件 pak，输出路径默认上次使用目录
  - 高级选项（层级范围、输出格式、输出路径）折叠在「高级选项」Expander；并发/重试等引擎参数收进设置页，普通界面不再暴露
  - UA/Referer/Cookies/子域名/密钥等属于来源配置，仅存在于 Sources.json，UI 不再展示动态参数表单
- 下载进度可视化：
  - 下载中显示总体进度环 + 各层级进度条 + 已完成/总瓦片数 + 实时速度与剩余时间 + 状态颜色（进行中/已完成/失败/已中断）
  - 地图选区内叠加瓦片完成度可视化（已下载瓦片高亮）为可选增强
- **BREAKING**：仅支持通用瓦片下载，移除 GSCloud 下载源与天地图令牌自动申请逻辑（Tianditu 可作为普通瓦片源通过配置 URL+用户填写密钥使用）
- 来源配置：Sources/ 目录一源一 JSON（内置 OSM/CARTO/ArcGIS/高德等免密钥源），密钥等身份验证信息由用户在界面填写并仅保存本机
- 断点续传（瓦片类）：下载前扫描已有瓦片并跳过，写入检查点；中断后重新发起自动续传；任务状态持久化，应用重启后可在任务中心继续
- 输出格式（瓦片下载可选，默认多文件 pak）：
  - **pak（多文件·新）**：主文件 {name}.pak 存 meta（infos/格式版本/分块索引），每个分块表独立存一个文件 {name}.blocks_{z}_{tx}_{ty}.pak（z<10 低层级存 {name}.blocks.pak），各自为独立 SQLite 文件
  - pak（单文件·旧）：保持现有 blocks/blocks_{z}_{tx}_{ty} + infos 单 SQLite 文件结构（向后兼容）
  - MBTiles：标准 metadata + tiles 表
  - 瓦片目录：{输出目录}/{z}/{x}/{y}.{ext}
- 目录结构重组（**BREAKING**：代码文件位置调整）：
  ```
  TileDownloader/
  ├── App.xaml(.cs)          # 标准启动 + DI 容器构建
  ├── Views/                 # 窗口与页面
  ├── ViewModels/            # CommunityToolkit MVVM
  ├── Models/                # 领域模型与实体
  ├── Services/              # 下载引擎、存储 Provider、任务管理、瓦片加载
  ├── Controls/              # 自研地图控件等复合控件
  ├── Converters/            # 值转换器
  ├── Attributes/            # 参数元数据特性
  └── Resources/             # 图标等静态资源
  ```
- **BREAKING**：`DownloadSource.DownloadAsync(MainWindowViewModel)` 签名废弃，下载逻辑迁移到 Services 层（`IDownloadEngine` / `ITileStore` / `ITaskManager`），Models 不再引用 ViewModel

## Impact
- Affected specs: 无既有 spec（首个）
- Affected code: TileDownloader.csproj、App.xaml(.cs)、Views/、ViewModels/、Models/、Controls/MapPickControl（重写为自研地图控件）、Converters/、Attributes/；新增 Services/ 与 Sources/（一源一 JSON）；删除 GSCloudDownloadSource.cs、TiandituDownloadSource.cs、Sources.json

## ADDED Requirements

### Requirement: 依赖升级至最新版本
系统 SHALL 使用以下最新稳定版本构建：CommunityToolkit.Mvvm 8.4.0、Microsoft.Extensions.DependencyInjection 10.x、WPF-UI 4.3.0、FreeSql.Provider.Sqlite 3.5.311、BruTile 6.0.0（下载引擎瓦片计算）、NetTopologySuite 2.6.0、ProjNet 最新稳定版、Newtonsoft.Json 13.0.4，且项目 SHALL 能在 net10.0-windows 下成功编译运行。SHALL NOT 再引用 Prism、MaterialDesignThemes、XAML.MapControl、NetTopologySuite.IO.GeoJSON。

#### Scenario: 全量升级后编译通过
- **WHEN** 替换 csproj 中所有包引用并完成 API 兼容迁移（BruTile 6、FreeSql 3、MVVM Toolkit）
- **THEN** `dotnet build` 无错误，应用可启动并完成一次瓦片下载

### Requirement: CommunityToolkit.Mvvm 架构
系统 SHALL 以 CommunityToolkit.Mvvm 为 MVVM 基础：ViewModel 继承 ObservableObject 并使用 [ObservableProperty]、[RelayCommand] 源生成器；DI SHALL 使用 Microsoft.Extensions.DependencyInjection，在 App 启动时构建 ServiceProvider 并注册页面、ViewModel 与 Services；SHALL NOT 依赖 Prism。

#### Scenario: 页面与 ViewModel 装配
- **WHEN** NavigationView 切换页面
- **THEN** 页面由 DI 创建并注入对应 ViewModel 作为 DataContext

### Requirement: 自研地图控件
系统 SHALL 提供完全自研的 WPF 地图控件（不使用外部地图控件库），基于 XYZ 瓦片方案：SHALL 支持鼠标拖拽平移与滚轮缩放（级别 0–20，含缩放动画/惯性可选）、按当前视野异步加载瓦片（网络获取 + 内存缓存 + 磁盘缓存，不阻塞 UI 线程）、根据瓦片源 URL 模板与密钥加载图源；SHALL 支持拖拽框选范围（绘制高亮选区矩形）并通过依赖属性与 ViewModel 的 Envelope（EPSG:4326）双向绑定；SHALL 在半透明浮层显示中心坐标/缩放级别/选区范围。

#### Scenario: 地图浏览
- **WHEN** 用户拖拽或滚轮缩放地图
- **THEN** 瓦片按需异步加载渲染，无 UI 卡顿，跨级别过渡自然

#### Scenario: 框选范围
- **WHEN** 用户在地图上按住鼠标拖出矩形
- **THEN** 显示高亮选区，ViewModel 的范围值同步更新，且可从输入框回填

### Requirement: 极简下载流程
系统 SHALL 提供极简下载交互：用户仅需选择地图来源（卡片/下拉选择器）→ 在地图上框选范围 → 点击「开始下载」即可完成下载；层级范围 SHALL 默认自动确定，输出格式默认「多文件 pak」，输出路径默认上次使用目录；高级选项（层级范围、输出格式、输出路径）SHALL 折叠在「高级选项」Expander 中；并发/重试 SHALL 收纳于设置页；UA/Referer/Cookies/子域名/密钥 SHALL 仅存在于 Sources.json 来源配置，UI SHALL NOT 展示动态参数表单。

#### Scenario: 三步完成下载
- **WHEN** 用户选择来源、框选范围并点击「开始下载」
- **THEN** 无需其他配置即开始下载，任务出现在任务中心并实时展示进度

#### Scenario: 高级选项
- **WHEN** 用户展开高级选项
- **THEN** 可修改层级范围、输出格式与输出路径，保存后生效

### Requirement: 下载进度可视化
系统 SHALL 可视化展示下载进度：总体进度环、各层级进度条、已完成/总瓦片计数、实时速度与剩余时间、状态颜色标识（进行中/已完成/失败/已中断）；任务中心以卡片/列表实时刷新。

#### Scenario: 实时进度
- **WHEN** 下载进行中
- **THEN** 进度环与层级进度条实时更新，速度与剩余时间随实际速率计算

### Requirement: 通用瓦片下载
系统 SHALL 仅支持通用瓦片下载：基于 GlobalSphericalMercator 方案按 z/x/y 下载指定层级与范围瓦片；SHALL 移除 GSCloud 下载源与天地图令牌自动申请逻辑。SHALL NOT 再依赖 BruTile 的瓦片计算（引擎自研 XYZ 数学，BruTile 仅作为依赖保留或随后移除均可接受——当前实现已不引用）。

#### Scenario: 通用瓦片源下载
- **WHEN** 用户选择一个已配置瓦片源并完成三步下载
- **THEN** 按设置页的并发与重试配置下载瓦片并写入所选格式存储，任务中心实时显示进度

### Requirement: 来源配置与用户密钥
来源配置 SHALL 存放于 Sources 目录且一源一 JSON 文件（便于增删与分发），系统 SHALL 内置多个免密钥通用源（OpenStreetMap、CARTO 浅/深色、ArcGIS 影像/街道/地形、高德矢量/影像等）；SHALL NOT 在来源配置中预置密钥/Cookie 等身份验证信息；**WHEN** 所选来源的 URL 模板含 {k} 占位符，**THEN** 新建下载页 SHALL 显示密钥输入框由用户填写，密钥仅保存在本机（%LOCALAPPDATA%）并注入地图预览与下载请求。

#### Scenario: 用户填写密钥
- **WHEN** 用户选择「天地图（需密钥）」
- **THEN** 出现密钥输入框；填写后地图预览与下载均携带该密钥，且重启应用无需重填

### Requirement: 断点续传（瓦片类）
瓦片下载（任意存储格式）SHALL 支持续传：启动时按 z/x/y 检查目标存储中已存在的瓦片并跳过；下载中定期写入检查点；**WHEN** 中断（取消/异常/关闭应用）后对相同目标重新发起下载，**THEN** 仅下载缺失瓦片并更新检查点直至完成。

### Requirement: 多文件 pak 输出
瓦片下载 SHALL 支持「多文件 pak」格式并作为默认输出：主文件 {name}.pak 持久化元数据（范围、层级、来源、格式版本、分块索引）；每个分块表（blocks_{z}_{tx}_{ty}）SHALL 单独存储为一个同目录文件 {name}.blocks_{z}_{tx}_{ty}.pak，z<10 低层级表存储为 {name}.blocks.pak，每个文件为独立 SQLite 库；续传与校验 SHALL 按文件粒度进行。SHALL 同时保留「pak（单文件·旧）」格式选项以向后兼容。

#### Scenario: 输出多文件 pak
- **WHEN** 用户完成一次 11–13 级下载
- **THEN** 目标目录出现 1 个主文件与每个涉及分块表各一个 .pak 文件，可被读取端按索引定位

#### Scenario: 多文件 pak 续传
- **WHEN** 中断后重新下载相同任务
- **THEN** 已完成的分块文件被跳过，仅补齐缺失瓦片

### Requirement: 多格式输出
瓦片下载 SHALL 支持四种输出格式：pak（多文件·默认）、pak（单文件·旧）、MBTiles（符合 1.x 规范）、瓦片目录（{z}/{x}/{y}.{ext}）；用户 SHALL 能在高级选项中选择输出格式。

#### Scenario: 输出为 MBTiles
- **WHEN** 用户选择 MBTiles 格式并完成下载
- **THEN** 生成可通过标准 MBTiles 工具读取的 .mbtiles 文件

### Requirement: 目录结构优化
代码 SHALL 按 Views/ViewModels/Models/Services/Controls/Converters/Attributes/Resources 分层组织；Services 层 SHALL 抽象 `IDownloadEngine`（引擎）、`ITileStore`（各存储格式 Provider）、`ITaskManager`（任务管理/持久化）与瓦片加载服务（供地图控件与下载引擎复用），ViewModels 仅依赖抽象接口。

#### Scenario: 新增存储格式不改 UI
- **WHEN** 需要新增一种存储格式
- **THEN** 仅需新增一个 ITileStore 实现并在格式选择处注册

## MODIFIED Requirements

### Requirement: 下载任务管理
系统 SHALL 以任务为中心管理下载：每个下载请求生成任务记录（名称、目标路径、格式、进度、状态、错误信息），任务中心以卡片/列表展示实时进度环/进度条与状态徽标；**WHEN** 用户取消或应用异常退出，**THEN** 任务标记为「已中断」，可从任务中心继续而非仅能重新发起。

## REMOVED Requirements

### Requirement: MaterialDesignThemes 界面
**Reason**: 被 WPF-UI 完全替换
**Migration**: 移除 MaterialDesign 包与全部 md: 命名空间引用，样式迁移到 WPF-UI 资源体系

### Requirement: Prism 框架
**Reason**: 按 MVVM 策略改用 CommunityToolkit.Mvvm + Microsoft.Extensions.DependencyInjection
**Migration**: 移除 Prism.DryIoc 包，BindableBase→ObservableObject、DelegateCommand→RelayCommand、ViewModelLocator→DI 装配、PrismApplication→标准 Application

### Requirement: GSCloud 下载源
**Reason**: 仅保留通用瓦片下载，不再支持特定平台大文件下载
**Migration**: 删除 GSCloudDownloadSource.cs 与 Sources.json 中对应配置项

### Requirement: 天地图令牌自动申请
**Reason**: 特定平台 hack，仅保留通用瓦片下载；天地图可作为普通瓦片源配置使用
**Migration**: 删除 TiandituDownloadSource.cs；如需天地图，在 Sources.json 中配置瓦片 URL 模板并填入密钥

### Requirement: XAML.MapControl 外部地图控件
**Reason**: 改为自研地图控件，减少外部依赖并统一视觉与交互
**Migration**: 移除 XAML.MapControl.WPF 包；MapPickControl 重写为自研控件，保留框选范围与范围双向绑定能力
