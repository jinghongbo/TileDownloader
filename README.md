# 通用瓦片地图下载工具（TileDownloader）

Windows 桌面应用（WPF / .NET 10），支持按行政区划或地图框选范围批量下载 XYZ 瓦片，并输出为多种离线地图格式。

- 仓库地址：<https://github.com/jinghongbo/TileDownloader>
- 问题反馈：<https://github.com/jinghongbo/TileDownloader/issues>

## 功能特性

- **两种选区方式**：省 / 市 / 区三级联动选择（支持关键词搜索，如「西湖区」），或在地图上手动拖拽框选矩形范围
- **多地图源**：内置 16 个地图源配置，在 `Sources` 目录新增 JSON 文件即可扩展；需要密钥的来源可在界面中填写，密钥仅保存在本机
- **多种输出格式**：多文件 pak（默认）/ 单文件 pak / MBTiles / 瓦片目录
- **层级与预估**：自定义最小、最大层级（0–20），实时显示预估瓦片总量
- **任务中心**：总体与分级进度、实时速度、剩余时间，支持取消、断点续传（仅补齐缺失瓦片）与历史任务记录
- **并发与重试**：可配置并发数与失败重试次数
- **Google Hosts 加速**：内置 Google IP 探测，一键复制 hosts 条目，改善谷歌地图源访问
- **外观主题**：深色 / 浅色 / 跟随系统

## 内置地图源

| 分类 | 地图源 |
| --- | --- |
| 谷歌 | 道路、卫星影像、混合（卫星+路网）、地形、谷歌中国（GCJ-02）道路 / 影像 |
| 高德 | 矢量地图、卫星影像 |
| 天地图 | 矢量底图（需密钥）、影像底图（需密钥） |
| ArcGIS | 街道地图、地形地图、全球影像 |
| CARTO | 浅色底图、深色底图 |
| 其他 | OpenStreetMap 标准地图 |

## 输出格式

| 格式 | 扩展名 | 说明 |
| --- | --- | --- |
| 多文件 pak | 目录 | z ≤ 9 写入 `blocks`，z ≥ 10 写入 `blocks_{z}_{x}_{y}` 分块文件，适合大体量数据 |
| 单文件 pak | `.pak` | 全部瓦片存于单个 pak 文件 |
| MBTiles | `.mbtiles` | 标准 SQLite 瓦片库（TMS 行号），可被 QGIS、Mapbox 等直接读取 |
| 瓦片目录 | 目录 | 标准 XYZ 目录结构 `{z}/{x}/{y}.png` |

> pak 格式的「完整块」选项：勾选后 z ≤ 9 的 blocks 与 z ≥ 10 的 `blocks_{z}_{tx}_{ty}`（512×512）会写满整块，便于按块读取，但下载量会明显增加。

## 环境要求

- Windows 10 / 11
- .NET 10 SDK（自行编译时需要）
- 运行框架依赖版需要 .NET 10 Desktop Runtime

## 构建

```powershell
git clone https://github.com/jinghongbo/TileDownloader.git
cd TileDownloader
dotnet restore TileDownloader.csproj
dotnet build TileDownloader.csproj -c Release
```

发布单文件版本：

```powershell
dotnet publish TileDownloader.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

## 自动构建与发布

[.github/workflows/build.yml](.github/workflows/build.yml) 在 push / PR 到 `main`、`master` 时构建并产出两种 zip 包（自包含与框架依赖）；推送 `v*` 标签时会自动创建 GitHub Release：

- `TileDownloader-v{版本}-win-x64-self-contained.zip`：自带 .NET 运行时，解压即用
- `TileDownloader-v{版本}-win-x64.zip`：体积更小，需已安装 .NET 10 Desktop Runtime

## 技术栈

- .NET 10 / WPF，界面基于 WPF-UI 4.3（Fluent 风格）
- CommunityToolkit.Mvvm、Microsoft.Extensions.DependencyInjection
- BruTile（瓦片方案）、NetTopologySuite + ProjNet（几何与坐标系）
- FreeSql.Provider.Sqlite（任务记录与 MBTiles）、Newtonsoft.Json

## 项目结构

```
Controls/     自研地图控件（预览、框选）
Converters/   XAML 值转换器
Models/       下载请求、任务、地图源、pak 元数据等模型
Services/     下载引擎、任务管理、地图源与行政区划服务、瓦片存储实现（Stores/）
Sources/      地图源配置（一源一 JSON）
ViewModels/   各页面视图模型
Views/        主窗口与页面（新建下载 / 任务中心 / 设置）
```
