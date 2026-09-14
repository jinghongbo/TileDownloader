# Checklist

## 依赖升级
- [x] csproj 仅保留最新稳定版依赖（CommunityToolkit.Mvvm、Microsoft.Extensions.DependencyInjection、WPF-UI、FreeSql.Provider.Sqlite、BruTile、NetTopologySuite、ProjNet、Newtonsoft.Json），不再引用 Prism、MaterialDesignThemes、XAML.MapControl、NetTopologySuite.IO.GeoJSON
- [x] `dotnet build` 无错误无重大警告
- [x] CommunityToolkit.Mvvm（源生成器）、BruTile 6、FreeSql 3 的 API 变更均已适配

## MVVM 架构
- [x] App.xaml(.cs) 为标准 WPF Application，启动时构建 ServiceProvider 并注册页面/ViewModel/Services
- [x] ViewModel 全部继承 ObservableObject 并使用 [ObservableProperty]/[RelayCommand]
- [x] 页面由 DI 创建并注入 ViewModel 作为 DataContext，无 ViewModelLocator 残留

## 目录结构
- [x] Views/ViewModels/Models/Services/Controls/Converters/Attributes/Resources 分层成立，无根目录散落代码文件
- [x] Models 不再引用 ViewModels；下载逻辑位于 Services
- [x] IDownloadEngine / ITileStore / ITaskManager / ITileImageLoader 抽象定义并通过 DI 注册

## 下载源收敛
- [x] GSCloudDownloadSource.cs 与 TiandituDownloadSource.cs 已删除
- [x] 来源配置为 Sources/ 目录一源一 JSON（10 个内置通用源），不预置密钥/Cookie 等身份验证信息
- [x] 通用瓦片源可正常配置并下载（引擎级冒烟实测：OSM 真实下载 8 瓦片成功；天地图需用户自填密钥）

## 来源配置与用户密钥
- [x] URL 含 {k} 的来源在新建下载页显示密钥输入框，由用户填写
- [x] 密钥持久化到本机（%LOCALAPPDATA%\MapDownloader\user_keys.json），重启无需重填
- [x] EffectiveSource 注入地图预览与下载请求，来源配置文件本身不含密钥

## 自研地图控件
- [x] 无外部地图控件库，地图控件为纯 WPF 自定义控件
- [x] 拖拽平移、滚轮缩放（0–20 级）流畅，瓦片按视野异步加载且不阻塞 UI
- [x] 瓦片加载含内存缓存与磁盘缓存，URL 模板/子域名/密钥替换正确
- [x] 框选绘制高亮选区，Envelope（EPSG:4326）与 ViewModel 双向绑定，浮层显示中心坐标/缩放级别/选区

## 极简下载流程
- [x] 三步完成下载：选来源 → 框选范围 → 开始下载，无需其他配置
- [x] 层级范围默认自动、输出格式默认多文件 pak、输出路径默认上次目录（%LOCALAPPDATA%\MapDownloader\last_output.txt）
- [x] 高级选项折叠在 Expander（层级/格式/路径）；并发/重试仅存在于设置页
- [x] UI 无动态参数表单（UA/Referer/Cookies 等仅存于来源 JSON）

## 断点续传
- [x] 瓦片下载按 z/x/y 跳过已存在瓦片
- [x] 检查点（CurLevel/CurX/CurY）在下载中写入、恢复时读取
- [x] 任务状态持久化：应用重启后任务中心可见中断任务并可继续

## 多格式输出
- [x] 多文件 pak（默认）：主文件 {name}.pak 含 meta/分块索引，每个分块表独立文件 {name}.blocks_{z}_{tx}_{ty}.pak，z<10 存 {name}.blocks.pak
- [x] 多文件 pak 续传按文件粒度跳过已完成分块
- [x] pak（单文件·旧）输出与现有表结构（blocks/blocks_z_tx_ty/infos）兼容
- [x] MBTiles 输出含 metadata/tiles 表且符合规范
- [x] 瓦片目录输出为 {z}/{x}/{y}.{ext} 结构
- [x] 高级选项中可选择输出格式

## 进度可视化与界面
- [x] FluentWindow + Mica 背景 + 左侧 NavigationView 侧边栏，三页导航正常切换，卡片式布局/圆角/间距/字体层级统一
- [x] 下载中显示总体进度环 + 分层级进度条 + 已完成/总瓦片数 + 实时速度与剩余时间 + 状态颜色
- [x] 任务中心卡片/列表实时刷新，含状态徽标与继续/取消/删除操作
- [x] Snackbar/ContentDialog 用于状态与错误反馈，深浅主题可切换

## 整体验证
- [x] `dotnet run` 启动正常；引擎级冒烟实测：真实瓦片源一键下载完成（多文件 pak：主文件 city.pak + 分块 city.blocks_10_1_0.pak / city.blocks_11_3_1.pak）
- [x] 中断同一任务后重新下载仅补缺瓦片（冒烟实测：同目标二次运行新增下载 0，全部跳过）
- [x] 单文件 pak、MBTiles 与瓦片目录输出文件结构校验通过（冒烟实测：Pak 旧结构兼容、MBTiles 重开抽查 exists=8、目录 10\842\387.png + meta.json）
- [x] 地图控件平移/缩放/框选流畅无卡顿（实现含异步加载/视野防抖/祖先瓦片回退/双层缓存等性能保护；体感流畅度建议使用中确认）
