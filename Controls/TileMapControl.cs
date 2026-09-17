using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TileDownloader.Models;
using TileDownloader.Services;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
// 本文件中的 Point 均指 WPF 屏幕坐标点
using Point = System.Windows.Point;

namespace TileDownloader.Controls
{
    /// <summary>
    /// 完全自研的 XYZ 瓦片地图控件（Web Mercator / EPSG:3857 投影，EPSG:4326 经纬度输入输出）。
    /// 交互约定：
    /// - 左键拖拽 = 框选范围（松开后转换为 Envelope 写入 SelectedRange，支持双向绑定）；
    /// - 右键拖拽（或 Ctrl+左键拖拽）= 平移地图；
    /// - 滚轮 = 以鼠标位置为锚点整数级缩放（0–20）。
    /// 瓦片通过 ITileImageLoader 异步加载（复用其内存/磁盘缓存），当前级未就绪时自动回退绘制祖先瓦片。
    /// 直接以 XAML 使用：<code>xmlns:controls="clr-namespace:TileDownloader.Controls"</code>
    /// </summary>
    public class TileMapControl : FrameworkElement
    {
        // ---------- 常量 ----------

        public const int MinZoom = 0;
        public const int MaxZoom = 20;

        /// <summary>Web Mercator 可表示的最大纬度（度），lat 超出时 clamp 到该值</summary>
        public const double MaxLatitude = 85.05112878;

        /// <summary>瓦片边长（世界像素；绘制时 1 DIP = 1 世界像素）</summary>
        private const double TileSize = 256.0;

        /// <summary>请求瓦片时同时预取的祖先层数（保证低层级回退有图可绘）</summary>
        private const int AncestorPrefetchLevels = 2;

        /// <summary>绘制时最多向上回退的祖先层数（再多放大倍数过高，无意义）</summary>
        private const int MaxAncestorFallback = 3;

        /// <summary>瓦片位图缓存容量（超出后整体清空；重新请求会命中加载器的磁盘缓存）</summary>
        private const int TileCacheCapacity = 1024;

        /// <summary>拖拽框选的最小尺寸（DIP），小于该值视为点击、不改变选区</summary>
        private const double MinDragSelectionSize = 4.0;

        /// <summary>瓦片网络请求并发上限</summary>
        private const int FetchConcurrency = 6;

        /// <summary>视野变化后延迟多久才重启瓦片请求（合并连续平移/缩放，避免请求风暴）</summary>
        private static readonly TimeSpan ViewDirtyDebounce = TimeSpan.FromMilliseconds(80);

        // ---------- 静态绘制资源（全部 Freeze） ----------

        private static readonly SolidColorBrush TransparentBrush = MakeBrush(Color.FromArgb(0, 0, 0, 0));
        private static readonly SolidColorBrush MapBackgroundBrush = MakeBrush(Color.FromRgb(0x1E, 0x1E, 0x24));
        private static readonly SolidColorBrush SelectionFillBrush = MakeBrush(Color.FromArgb(0x33, 0x4C, 0xC2, 0xFF));
        private static readonly Pen SelectionPen = MakePen(Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF), 1.5);
        private static readonly SolidColorBrush OverlayBackBrush = MakeBrush(Color.FromArgb(0xB0, 0x00, 0x00, 0x00));
        private static readonly SolidColorBrush OverlayTextBrush = MakeBrush(Colors.White);
        private static readonly Typeface OverlayTypeface =
            new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        private const double OverlayFontSize = 12.0;

        private static SolidColorBrush MakeBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static Pen MakePen(Color color, double thickness)
        {
            var pen = new Pen(MakeBrush(color), thickness);
            pen.Freeze();
            return pen;
        }

        // ---------- 依赖属性 ----------

        /// <summary>瓦片源（换源时清空瓦片缓存并重新请求）</summary>
        public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
            nameof(Source), typeof(DownloadSource), typeof(TileMapControl),
            new FrameworkPropertyMetadata(null,
                FrameworkPropertyMetadataOptions.AffectsRender, OnSourceChanged));

        /// <summary>选区范围（EPSG:4326 度）。双向绑定：框选写回源；外部赋值也会在地图上反向渲染</summary>
        public static readonly DependencyProperty SelectedRangeProperty = DependencyProperty.Register(
            nameof(SelectedRange), typeof(Envelope), typeof(TileMapControl),
            new FrameworkPropertyMetadata(null,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
                OnSelectedRangeChanged));

        /// <summary>缩放级别（0–20，整数）</summary>
        public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
            nameof(Zoom), typeof(int), typeof(TileMapControl),
            new FrameworkPropertyMetadata(4,
                FrameworkPropertyMetadataOptions.AffectsRender, OnViewChanged, CoerceZoom));

        /// <summary>地图中心经度（度，clamp 到 [-180, 180]）</summary>
        public static readonly DependencyProperty CenterLonProperty = DependencyProperty.Register(
            nameof(CenterLon), typeof(double), typeof(TileMapControl),
            new FrameworkPropertyMetadata(105.0,
                FrameworkPropertyMetadataOptions.AffectsRender, OnViewChanged, CoerceCenterLon));

        /// <summary>地图中心纬度（度，clamp 到 ±85.05112878）</summary>
        public static readonly DependencyProperty CenterLatProperty = DependencyProperty.Register(
            nameof(CenterLat), typeof(double), typeof(TileMapControl),
            new FrameworkPropertyMetadata(35.0,
                FrameworkPropertyMetadataOptions.AffectsRender, OnViewChanged, CoerceCenterLat));

        public DownloadSource? Source
        {
            get => (DownloadSource?)GetValue(SourceProperty);
            set => SetValue(SourceProperty, value);
        }

        public Envelope? SelectedRange
        {
            get => (Envelope?)GetValue(SelectedRangeProperty);
            set => SetValue(SelectedRangeProperty, value);
        }

        public int Zoom
        {
            get => (int)GetValue(ZoomProperty);
            set => SetValue(ZoomProperty, value);
        }

        public double CenterLon
        {
            get => (double)GetValue(CenterLonProperty);
            set => SetValue(CenterLonProperty, value);
        }

        public double CenterLat
        {
            get => (double)GetValue(CenterLatProperty);
            set => SetValue(CenterLatProperty, value);
        }

        private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((TileMapControl)d).ResetTilesAndRefetch();
        }

        private static void OnViewChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((TileMapControl)d).MarkViewDirty();
        }

        private static void OnSelectedRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (TileMapControl)d;
            // 内部框选 SetValue 与外部赋值都会走这里：统一在此触发事件（AffectsRender 已自动重绘）
            if (e.NewValue is Envelope env && !env.IsNull)
            {
                c.SelectionChanged?.Invoke(env);
            }
        }

        private static object CoerceZoom(DependencyObject d, object baseValue)
            => baseValue is int v ? Math.Clamp(v, MinZoom, MaxZoom) : 4;

        private static object CoerceCenterLon(DependencyObject d, object baseValue)
            => baseValue is double v ? Math.Clamp(v, -180.0, 180.0) : 0.0;

        private static object CoerceCenterLat(DependencyObject d, object baseValue)
            => baseValue is double v ? Math.Clamp(v, -MaxLatitude, MaxLatitude) : 0.0;

        // ---------- 运行时状态 ----------

        /// <summary>瓦片加载器（可在 XAML 宿主中直接赋值；Loaded 时若为空则从 App.Services 解析兜底）</summary>
        public ITileImageLoader? Loader { get; set; }

        /// <summary>选区变化事件（内部框选完成或外部修改 SelectedRange 时触发）</summary>
        public event Action<Envelope>? SelectionChanged;

        // 瓦片缓存与在途请求（跨线程访问，必须持 _cacheLock）
        // 在途记录 key → 发起时的源代际：视野重启后同 key 的新请求会覆盖旧代际记录，
        // 旧请求的迟到清理只有代际匹配才移除，避免误删新请求的记录导致重复发起
        private readonly SemaphoreSlim _fetchGate = new(FetchConcurrency, FetchConcurrency);
        private readonly object _cacheLock = new();
        private readonly Dictionary<(int Z, int X, int Y), BitmapImage?> _tileCache = new();
        private readonly Queue<(int Z, int X, int Y)> _cacheOrder = new();
        private readonly Dictionary<(int Z, int X, int Y), int> _pendingRequests = new();

        // 按视野状态生成的取消源：视野重启时取消全部在途请求（仅 UI 线程访问，不 Dispose：token 已被任务捕获，避免竞态）
        private CancellationTokenSource? _viewCts;

        // 换源代际：丢弃旧源在途请求的迟到结果
        private int _sourceGeneration;

        // 视野变化防抖计时器（UI 线程）
        private readonly DispatcherTimer _viewDirtyTimer;

        // 交互状态（仅 UI 线程）
        private bool _isSelecting;
        private bool _isPanning;
        private bool _panByRight;
        private Point _dragStart;
        private Point _dragCurrent;
        private Point _panLast;

        public TileMapControl()
        {
            ClipToBounds = true;
            Focusable = false;
            Cursor = Cursors.Cross;
            // 祖先回退放大绘制时使用较低质量插值，换取流畅度
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);

            _viewDirtyTimer = new DispatcherTimer { Interval = ViewDirtyDebounce };
            _viewDirtyTimer.Tick += (_, _) =>
            {
                _viewDirtyTimer.Stop();
                RestartTileFetchCore();
            };

            Loaded += OnControlLoaded;
            Unloaded += OnControlUnloaded;
        }

        private void OnControlUnloaded(object sender, RoutedEventArgs e)
        {
            _viewDirtyTimer.Stop();
            _viewCts?.Cancel();
        }

        private void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            // 兜底：宿主未显式指定 Loader 时从全局容器解析
            if (Loader == null)
            {
                try
                {
                    Loader = App.Services.GetRequiredService<ITileImageLoader>();
                }
                catch
                {
                    // 服务容器不可用（如设计器）：保持 null，OnRender 会跳过瓦片加载
                }
            }

            RestartTileFetchCore();
        }

        /// <summary>把 UI 操作调度回 UI 线程（后台瓦片回调的唯一入口）；Dispatcher 关闭时静默放弃</summary>
        private void Post(Action action)
        {
            try
            {
                Dispatcher.BeginInvoke(action);
            }
            catch
            {
                // 窗口/Dispatcher 已关闭：视觉刷新无意义，忽略即可
            }
        }

        // ---------- 投影变换（Web Mercator，纯静态无状态） ----------

        /// <summary>经度（度）→ 世界像素 X（zoom 级）。worldPx = 256 * 2^z，x = (lon+180)/360*worldPx</summary>
        private static double LonToWorldX(double lon, int z)
        {
            return (lon + 180.0) / 360.0 * WorldSize(z);
        }

        /// <summary>纬度（度）→ 世界像素 Y（zoom 级）。y = (1 - ln(tanφ+secφ)/π)/2 * worldPx，lat 先 clamp</summary>
        private static double LatToWorldY(double lat, int z)
        {
            lat = Math.Clamp(lat, -MaxLatitude, MaxLatitude);
            var rad = lat * Math.PI / 180.0;
            var y = (1.0 - Math.Asinh(Math.Tan(rad)) / Math.PI) / 2.0;
            return y * WorldSize(z);
        }

        /// <summary>世界像素 X（zoom 级）→ 经度（度）。lon = px/worldPx*360 - 180</summary>
        private static double WorldXToLon(double wx, int z)
        {
            return wx / WorldSize(z) * 360.0 - 180.0;
        }

        /// <summary>世界像素 Y（zoom 级）→ 纬度（度）。n = π(1 - 2·py/worldPx)，lat = atan(sinh(n))，结果 clamp</summary>
        private static double WorldYToLat(double wy, int z)
        {
            var world = WorldSize(z);
            var n = Math.PI * (1.0 - 2.0 * wy / world);
            var lat = Math.Atan(Math.Sinh(n)) * 180.0 / Math.PI;
            return Math.Clamp(lat, -MaxLatitude, MaxLatitude);
        }

        /// <summary>当前级世界边长（像素）= 256 * 2^z</summary>
        private static double WorldSize(int z)
        {
            return TileSize * (double)(1L << z);
        }

        // ---------- 屏幕与地理互转 ----------

        private (double Lon, double Lat) ScreenToGeo(Point p)
        {
            var z = Zoom;
            var wx = LonToWorldX(CenterLon, z) - ActualWidth / 2.0 + p.X;
            var wy = LatToWorldY(CenterLat, z) - ActualHeight / 2.0 + p.Y;
            return (WorldXToLon(wx, z), WorldYToLat(wy, z));
        }

        private Envelope ScreenRectToEnvelope(Rect rect)
        {
            var p1 = ScreenToGeo(rect.TopLeft);
            var p2 = ScreenToGeo(rect.BottomRight);
            var minLon = Math.Min(p1.Lon, p2.Lon);
            var maxLon = Math.Max(p1.Lon, p2.Lon);
            var minLat = Math.Min(p1.Lat, p2.Lat);
            var maxLat = Math.Max(p1.Lat, p2.Lat);
            return new Envelope(minLon, maxLon, minLat, maxLat);
        }

        /// <summary>
        /// 计算指定层级下视野覆盖的瓦片范围（clamp 到 [0, 2^z-1]）。
        /// 仅在 UI 线程调用（读取视图 DP）。
        /// </summary>
        private (int FirstCol, int LastCol, int FirstRow, int LastRow) ComputeVisibleTileBounds(double width, double height, int level)
        {
            var max = (int)((1L << level) - 1);
            var viewLeft = LonToWorldX(CenterLon, level) - width / 2.0;
            var viewTop = LatToWorldY(CenterLat, level) - height / 2.0;

            var firstCol = (int)Math.Floor(viewLeft / TileSize);
            var lastCol = (int)Math.Floor((viewLeft + width) / TileSize);
            var firstRow = (int)Math.Floor(viewTop / TileSize);
            var lastRow = (int)Math.Floor((viewTop + height) / TileSize);

            return (
                Math.Clamp(firstCol, 0, max),
                Math.Clamp(lastCol, 0, max),
                Math.Clamp(firstRow, 0, max),
                Math.Clamp(lastRow, 0, max));
        }

        // ---------- 视野变化 → 重绘 + 防抖重启瓦片请求 ----------

        private void MarkViewDirty()
        {
            InvalidateVisual();
            // Stop + Start 重置计时器，合并连续的平移/缩放
            _viewDirtyTimer.Stop();
            _viewDirtyTimer.Start();
        }

        /// <summary>视野状态变化（防抖到期）后重启瓦片请求：取消过期请求，补充缺失瓦片</summary>
        private void RestartTileFetchCore()
        {
            var width = ActualWidth;
            var height = ActualHeight;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            var source = Source;
            var loader = Loader;
            if (source == null || loader == null)
            {
                return;
            }

            // 取消上一轮视野的在途请求（后台任务感知取消后不会写缓存/重绘）
            _viewCts?.Cancel();
            _viewCts = new CancellationTokenSource();
            var ct = _viewCts.Token;

            var z = Zoom;
            var generation = _sourceGeneration;
            var sourceName = source.Name ?? string.Empty;
            var urlTemplate = source.Url ?? string.Empty;
            var subdomains = source.GetSubdomains();
            var apiKey = source.Key ?? string.Empty;
            var userAgent = source.UserAgent ?? string.Empty;
            var referer = source.Referer ?? string.Empty;
            var cookies = source.Cookies ?? string.Empty;

            // 当前级 + 两级祖先级一并纳入请求，保证回退绘制始终有图
            var minLevel = Math.Max(0, z - AncestorPrefetchLevels);
            for (var level = minLevel; level <= z; level++)
            {
                var (firstCol, lastCol, firstRow, lastRow) = ComputeVisibleTileBounds(width, height, level);
                for (var x = firstCol; x <= lastCol; x++)
                {
                    for (var y = firstRow; y <= lastRow; y++)
                    {
                        var key = (level, x, y);
                        var shouldFetch = false;
                        lock (_cacheLock)
                        {
                            if (_tileCache.ContainsKey(key))
                            {
                                continue;
                            }

                            // 同代际已在途 → 跳过；旧代际残留记录（请求已被取消）→ 覆盖并重新发起
                            if (_pendingRequests.TryGetValue(key, out var pendingGen) && pendingGen == generation)
                            {
                                continue;
                            }

                            _pendingRequests[key] = generation;
                            shouldFetch = true;
                        }

                        if (!shouldFetch)
                        {
                            continue;
                        }

                        _ = FetchTileAsync(loader, generation, sourceName, urlTemplate, subdomains, apiKey,
                            userAgent, referer, cookies, level, x, y, ct);
                    }
                }
            }
        }

        /// <summary>换源：取消在途请求、清空缓存与请求记录，立即重取（AffectsRender 已触发重绘）</summary>
        private void ResetTilesAndRefetch()
        {
            _sourceGeneration++;
            _viewCts?.Cancel();

            lock (_cacheLock)
            {
                _tileCache.Clear();
                _cacheOrder.Clear();
                _pendingRequests.Clear();
            }

            RestartTileFetchCore();
            InvalidateVisual();
        }

        /// <summary>
        /// 后台瓦片加载（受限并发）。返回结果经 Dispatcher 调度回 UI 线程写缓存并重绘；
        /// 被取消或属于旧源代际的结果直接丢弃。
        /// </summary>
        private async Task FetchTileAsync(
            ITileImageLoader loader, int generation,
            string sourceName, string urlTemplate, string[] subdomains, string apiKey,
            string userAgent, string referer, string cookies,
            int z, int x, int y, CancellationToken ct)
        {
            try
            {
                await _fetchGate.WaitAsync(ct);

                byte[]? bytes;
                try
                {
                    bytes = await loader.GetTileAsync(sourceName, urlTemplate, subdomains, apiKey,
                        userAgent, referer, cookies, z, x, y, ct);
                }
                finally
                {
                    _fetchGate.Release();
                }

                if (ct.IsCancellationRequested || generation != _sourceGeneration)
                {
                    return;
                }

                if (bytes == null || bytes.Length == 0)
                {
                    // 失败标记（缓存 null），避免同一视野内对坏瓦片反复请求
                    Post(() =>
                    {
                        if (generation == _sourceGeneration)
                        {
                            SetTileInCache(z, x, y, null);
                        }
                    });
                    return;
                }

                var bitmap = CreateBitmap(bytes);
                Post(() =>
                {
                    if (generation == _sourceGeneration)
                    {
                        SetTileInCache(z, x, y, bitmap);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // 视野过期被取消：正常路径，丢弃即可
            }
            catch
            {
                // 其他异常也标记为失败，避免无限重试
                Post(() =>
                {
                    if (generation == _sourceGeneration)
                    {
                        SetTileInCache(z, x, y, null);
                    }
                });
            }
            finally
            {
                lock (_cacheLock)
                {
                    // 仅当代际匹配才移除：防止旧请求的迟到清理误删新一代际的同 key 在途记录
                    if (_pendingRequests.TryGetValue((z, x, y), out var pendingGen) && pendingGen == generation)
                    {
                        _pendingRequests.Remove((z, x, y));
                    }
                }
            }
        }

        /// <summary>从字节构造可跨线程使用的冻结位图（OnLoad 立即解码，后台线程安全）</summary>
        private static BitmapImage CreateBitmap(byte[] bytes)
        {
            var image = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = ms;
            image.EndInit();
            image.Freeze();
            return image;
        }

        /// <summary>写入瓦片缓存（UI 线程）并触发重绘；超容量时按 FIFO 平滑淘汰最旧的一半瓦片</summary>
        private void SetTileInCache(int z, int x, int y, BitmapImage? image)
        {
            var key = (z, x, y);
            lock (_cacheLock)
            {
                _tileCache[key] = image;
                _cacheOrder.Enqueue(key);
                if (_tileCache.Count > TileCacheCapacity)
                {
                    var removeCount = TileCacheCapacity / 2;
                    for (var i = 0; i < removeCount && _cacheOrder.Count > 0; i++)
                    {
                        var oldest = _cacheOrder.Dequeue();
                        _tileCache.Remove(oldest);
                    }
                }
            }

            InvalidateVisual();
        }

        private BitmapImage? GetCachedTile(int z, int x, int y)
        {
            lock (_cacheLock)
            {
                return _tileCache.TryGetValue((z, x, y), out var image) ? image : null;
            }
        }

        // ---------- 公共 API ----------

        /// <summary>
        /// 调整中心与层级使指定范围（EPSG:4326 度）尽量充满控件（供"定位到范围"使用）。
        /// </summary>
        public void ZoomToWorld(double minx, double miny, double maxx, double maxy)
        {
            if (minx > maxx)
            {
                (minx, maxx) = (maxx, minx);
            }

            if (miny > maxy)
            {
                (miny, maxy) = (maxy, miny);
            }

            // 尺寸未布局时用 1 兜底，避免范围计算失真
            var width = Math.Max(ActualWidth, 1.0);
            var height = Math.Max(ActualHeight, 1.0);

            // 从 0 级向上找最大能容纳该范围的整数层级（累乘比较，避免除零）
            var best = MinZoom;
            for (var z = MinZoom; z <= MaxZoom; z++)
            {
                var spanX = LonToWorldX(maxx, z) - LonToWorldX(minx, z);
                var spanY = LatToWorldY(miny, z) - LatToWorldY(maxy, z);
                if (spanX <= width && spanY <= height)
                {
                    best = z;
                }
                else
                {
                    break;
                }
            }

            Zoom = best;
            CenterLon = (minx + maxx) / 2.0;
            CenterLat = (miny + maxy) / 2.0;
            MarkViewDirty();
        }

        // ---------- 渲染 ----------

        protected override void OnRender(DrawingContext dc)
        {
            var width = ActualWidth;
            var height = ActualHeight;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            var view = new Rect(0, 0, width, height);
            // 透明底保证命中测试可用；深色底在瓦片未加载时兜底
            dc.DrawRectangle(TransparentBrush, null, view);
            dc.DrawRectangle(MapBackgroundBrush, null, view);

            var z = Zoom;
            DrawTiles(dc, width, height, z);
            DrawSelectedRange(dc, width, height, z);
            if (_isSelecting)
            {
                DrawDragSelection(dc);
            }

            DrawOverlay(dc, width, height);
        }

        private void DrawTiles(DrawingContext dc, double width, double height, int z)
        {
            var viewLeft = LonToWorldX(CenterLon, z) - width / 2.0;
            var viewTop = LatToWorldY(CenterLat, z) - height / 2.0;
            var (firstCol, lastCol, firstRow, lastRow) = ComputeVisibleTileBounds(width, height, z);

            for (var tx = firstCol; tx <= lastCol; tx++)
            {
                for (var ty = firstRow; ty <= lastRow; ty++)
                {
                    // 瓦片世界坐标 → 屏幕坐标（1 DIP = 1 世界像素）
                    var dest = new Rect(tx * TileSize - viewLeft, ty * TileSize - viewTop, TileSize, TileSize);
                    var image = GetCachedTile(z, tx, ty);
                    if (image != null)
                    {
                        dc.DrawImage(image, dest);
                        continue;
                    }

                    DrawAncestorFallback(dc, dest, z, tx, ty);
                }
            }
        }

        /// <summary>当前级瓦片未就绪时，向上查找已就绪的祖先瓦片，裁出对应子区域放大绘制</summary>
        private void DrawAncestorFallback(DrawingContext dc, Rect dest, int z, int x, int y)
        {
            for (var s = 1; s <= MaxAncestorFallback && z - s >= 0; s++)
            {
                var ancestorZ = z - s;
                var ancestorX = x >> s;
                var ancestorY = y >> s;
                var image = GetCachedTile(ancestorZ, ancestorX, ancestorY);
                if (image == null)
                {
                    continue;
                }

                // 当前瓦片在祖先瓦片内的子块偏移（以像素计）
                var mask = (1 << s) - 1;
                var offX = x & mask;
                var offY = y & mask;
                var sub = (int)(TileSize / (1 << s));
                if (sub < 1)
                {
                    continue;
                }

                // 用 CroppedBitmap 从祖先位图裁出子区域再放大绘制（DrawImage 无 sourceRect 重载）
                var crop = new CroppedBitmap(image, new Int32Rect(offX * sub, offY * sub, sub, sub));
                dc.DrawImage(crop, dest);
                return;
            }
        }

        /// <summary>绘制 SelectedRange 对应的选区矩形（外部赋值也会在此反向渲染）</summary>
        private void DrawSelectedRange(DrawingContext dc, double width, double height, int z)
        {
            var env = SelectedRange;
            if (env == null || env.IsNull)
            {
                return;
            }

            var viewLeft = LonToWorldX(CenterLon, z) - width / 2.0;
            var viewTop = LatToWorldY(CenterLat, z) - height / 2.0;

            var x1 = LonToWorldX(env.MinX, z) - viewLeft;
            var x2 = LonToWorldX(env.MaxX, z) - viewLeft;
            var y1 = LatToWorldY(env.MinY, z) - viewTop; // 南边界 → 屏幕下方（y 更大）
            var y2 = LatToWorldY(env.MaxY, z) - viewTop;

            var rect = new Rect(
                Math.Min(x1, x2), Math.Min(y1, y2),
                Math.Abs(x2 - x1), Math.Abs(y2 - y1));
            dc.DrawRectangle(SelectionFillBrush, SelectionPen, rect);
        }

        /// <summary>绘制拖拽中的临时框选矩形（屏幕坐标）</summary>
        private void DrawDragSelection(DrawingContext dc)
        {
            var rect = new Rect(_dragStart, _dragCurrent);
            dc.DrawRectangle(SelectionFillBrush, SelectionPen, rect);
        }

        /// <summary>左下角信息浮层：中心经纬度 / 层级 / 选区 / 操作提示</summary>
        private void DrawOverlay(DrawingContext dc, double width, double height)
        {
            var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            string[] lines =
            {
                string.Format(CultureInfo.InvariantCulture, "中心: {0:F4}, {1:F4}", CenterLon, CenterLat),
                string.Format(CultureInfo.InvariantCulture, "缩放: {0}", Zoom),
                "选区: " + BuildSelectionText(),
                "左键拖拽框选 · 右键/Ctrl+左键拖拽平移 · 滚轮缩放",
            };

            const double padX = 10.0;
            const double padY = 7.0;
            const double gap = 3.0;

            var formatted = new List<FormattedText>(lines.Length);
            double lineHeight = 0;
            double maxLineWidth = 0;
            foreach (var line in lines)
            {
                var ft = new FormattedText(
                    line,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    OverlayTypeface,
                    OverlayFontSize,
                    OverlayTextBrush,
                    pixelsPerDip);
                formatted.Add(ft);
                lineHeight = Math.Max(lineHeight, ft.Height);
                maxLineWidth = Math.Max(maxLineWidth, ft.Width);
            }

            var boxWidth = maxLineWidth + padX * 2;
            var boxHeight = formatted.Count * lineHeight + (formatted.Count - 1) * gap + padY * 2;
            var box = new Rect(8, height - boxHeight - 8, boxWidth, boxHeight);
            dc.DrawRoundedRectangle(OverlayBackBrush, null, box, 6, 6);

            var textY = box.Top + padY;
            foreach (var ft in formatted)
            {
                dc.DrawText(ft, new Point(box.Left + padX, textY));
                textY += lineHeight + gap;
            }
        }

        private string BuildSelectionText()
        {
            var env = SelectedRange;
            if (env == null || env.IsNull)
            {
                return "未框选";
            }

            return string.Format(CultureInfo.InvariantCulture, "{0:F4},{1:F4} ~ {2:F4},{3:F4}",
                env.MinX, env.MinY, env.MaxX, env.MaxY);
        }

        // ---------- 尺寸 / DPI 变化 ----------

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            MarkViewDirty();
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            InvalidateVisual(); // 浮层文字的 pixelsPerDip 每次绘制时现取，这里只需重绘
        }

        // ---------- 鼠标交互 ----------

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (!_isPanning && !_isSelecting)
            {
                var pos = e.GetPosition(this);
                if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
                {
                    // Ctrl+左键 = 平移
                    StartPan(pos, byRight: false);
                }
                else
                {
                    // 左键 = 框选（再次拖拽即重新选择）
                    _isSelecting = true;
                    _dragStart = pos;
                    _dragCurrent = pos;
                    CaptureMouse();
                    InvalidateVisual();
                }

                e.Handled = true;
            }

            base.OnMouseLeftButtonDown(e);
        }

        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            if (!_isPanning && !_isSelecting)
            {
                StartPan(e.GetPosition(this), byRight: true);
                e.Handled = true;
            }

            base.OnMouseRightButtonDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_isSelecting)
            {
                _dragCurrent = e.GetPosition(this);
                InvalidateVisual();
            }
            else if (_isPanning)
            {
                var pos = e.GetPosition(this);
                var dx = pos.X - _panLast.X;
                var dy = pos.Y - _panLast.Y;
                _panLast = pos;
                if (dx != 0 || dy != 0)
                {
                    PanBy(dx, dy);
                }
            }

            base.OnMouseMove(e);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (_isSelecting)
            {
                _isSelecting = false;
                ReleaseCaptureIfAny();

                var rect = new Rect(_dragStart, _dragCurrent);
                InvalidateVisual();

                // 过小的拖拽视为点击，不改变选区
                if (rect.Width >= MinDragSelectionSize || rect.Height >= MinDragSelectionSize)
                {
                    // DP 为 TwoWay：SetValue 写回绑定源；DP 回调触发 SelectionChanged 与重绘
                    SelectedRange = ScreenRectToEnvelope(rect);
                }

                e.Handled = true;
            }
            else if (_isPanning && !_panByRight)
            {
                EndPan();
                e.Handled = true;
            }

            base.OnMouseLeftButtonUp(e);
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            if (_isPanning && _panByRight)
            {
                EndPan();
                e.Handled = true;
            }

            base.OnMouseRightButtonUp(e);
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            var direction = e.Delta > 0 ? 1 : -1;
            var newZoom = Math.Clamp(Zoom + direction, MinZoom, MaxZoom);
            e.Handled = true;
            if (newZoom == Zoom)
            {
                return;
            }

            // 锚点缩放：滚轮处的地理点在缩放前后保持在同一屏幕位置
            var oldZoom = Zoom;
            var pos = e.GetPosition(this);
            var viewLeft = LonToWorldX(CenterLon, oldZoom) - ActualWidth / 2.0;
            var viewTop = LatToWorldY(CenterLat, oldZoom) - ActualHeight / 2.0;
            var anchorWx = viewLeft + pos.X;
            var anchorWy = viewTop + pos.Y;

            var scale = Math.Pow(2, newZoom - oldZoom);
            var offsetX = anchorWx - LonToWorldX(CenterLon, oldZoom);
            var offsetY = anchorWy - LatToWorldY(CenterLat, oldZoom);

            Zoom = newZoom;
            // 新中心 = 锚点新级世界坐标 - 原屏幕偏移（偏移量与层级无关，均为 DIP）
            CenterLon = WorldXToLon(anchorWx * scale - offsetX, newZoom);
            CenterLat = WorldYToLat(anchorWy * scale - offsetY, newZoom);

            base.OnMouseWheel(e);
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            // 捕获被系统抢走（如切窗口）时复位交互状态，避免卡在拖拽中
            _isSelecting = false;
            _isPanning = false;
            _panByRight = false;
            InvalidateVisual();
            base.OnLostMouseCapture(e);
        }

        private void StartPan(Point pos, bool byRight)
        {
            _isPanning = true;
            _panByRight = byRight;
            _panLast = pos;
            CaptureMouse();
        }

        private void EndPan()
        {
            _isPanning = false;
            _panByRight = false;
            ReleaseCaptureIfAny();
        }

        private void ReleaseCaptureIfAny()
        {
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }
        }

        /// <summary>按屏幕位移平移地图（更新中心点；DP coerce 负责边界 clamp）</summary>
        private void PanBy(double dx, double dy)
        {
            var z = Zoom;
            var centerWx = LonToWorldX(CenterLon, z) - dx;
            var centerWy = LatToWorldY(CenterLat, z) - dy;
            CenterLon = WorldXToLon(centerWx, z);
            CenterLat = WorldYToLat(centerWy, z);
            // DP 变更回调触发 MarkViewDirty：立即重绘 + 防抖重启瓦片请求
        }
    }
}
