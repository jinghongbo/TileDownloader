using Prism.Mvvm;
using BruTile;
using Prism.Commands;
using ProjNet;
using ProjNet.CoordinateSystems;
using System.Collections.Generic;
using BruTile.Web;
using BruTile.Predefined;
using TileDownloader.Models;
using System.Threading.Tasks;
using FreeSql;
using System.Linq;
using System;
using System.Threading;
using System.Windows;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;

namespace TileDownloader.ViewModels
{
    public class MainWindowViewModel : BindableBase
    {
        public string Title { get; set; } = "瓦片下载器";
        public string FilePath { get => _filePath; set => SetProperty(ref _filePath, value); }
        public double MinX { get; set; } = 111;

        public double MinY { get; set; } = 41;

        public double MaxX { get; set; } = 114;

        public double MaxY { get; set; } = 44;

        public int MaxLevel { get; set; } = 15;

        //https://t0.tianditu.gov.cn/DataServer?T=img_w&x={x}&y={y}&l={z}&tk={k}
        private List<DownloadSource> _sources = new List<DownloadSource>
        {
            new DownloadSource(
                new GlobalSphericalMercator(),
                "http://{s}.tianditu.gov.cn/DataServer?T=vec_w&x={x}&y={y}&l={z}&tk={k}",
                serverNodes: new[] { "t0", "t1", "t2", "t3", "t4", "t5", "t6", "t7" },
                apiKey:"5d22d49fdc586cb5caed68bfb12d1e6b",
                name: "天地图矢量底图",
                userAgent:"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/90.0.4430.212 Safari/537.36 Edg/90.0.818.62"
                ),
            new DownloadSource(
                new GlobalSphericalMercator(),
                "http://{s}.tianditu.gov.cn/DataServer?T=cva_w&x={x}&y={y}&l={z}&tk={k}",
                serverNodes: new[] { "t0", "t1", "t2", "t3", "t4", "t5", "t6", "t7" },
                apiKey:"5d22d49fdc586cb5caed68bfb12d1e6b",
                name: "天地图矢量注记",
                userAgent:"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/90.0.4430.212 Safari/537.36 Edg/90.0.818.62"

                ),
            new DownloadSource(
                new GlobalSphericalMercator(),
                "http://{s}.tianditu.gov.cn/DataServer?T=img_w&x={x}&y={y}&l={z}&tk={k}",
                serverNodes: new[] { "t0", "t1", "t2", "t3", "t4", "t5", "t6", "t7" },
                apiKey:"5d22d49fdc586cb5caed68bfb12d1e6b",
                name: "天地图影像底图",
                userAgent:"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/90.0.4430.212 Safari/537.36 Edg/90.0.818.62"

                ),
            new DownloadSource(
                new GlobalSphericalMercator(),
                "http://{s}.tianditu.gov.cn/DataServer?T=cia_w&x={x}&y={y}&l={z}&tk={k}",
                serverNodes: new[] { "t0", "t1", "t2", "t3", "t4", "t5", "t6", "t7" },
                apiKey:"5d22d49fdc586cb5caed68bfb12d1e6b",
                name: "天地图影像注记",
                userAgent:"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/90.0.4430.212 Safari/537.36 Edg/90.0.818.62"

                ),
            new DownloadSource(
                new GlobalSphericalMercator(),
                "http://{s}.tianditu.gov.cn/DataServer?T=ter_w&x={x}&y={y}&l={z}&tk={k}",
                serverNodes: new[] { "t0", "t1", "t2", "t3", "t4", "t5", "t6", "t7" },
                apiKey:"5d22d49fdc586cb5caed68bfb12d1e6b",
                name: "天地图地形晕渲",
                userAgent:"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/90.0.4430.212 Safari/537.36 Edg/90.0.818.62"

                ),
            new DownloadSource(
                new GlobalSphericalMercator(),
                "http://{s}.tianditu.gov.cn/DataServer?T=cta_w&x={x}&y={y}&l={z}&tk={k}",
                serverNodes: new[] { "t0", "t1", "t2", "t3", "t4", "t5", "t6", "t7" },
                apiKey:"5d22d49fdc586cb5caed68bfb12d1e6b",
                name: "天地图地形注记",
                userAgent:"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/90.0.4430.212 Safari/537.36 Edg/90.0.818.62"

                ),
            new DownloadSource(
                new GlobalSphericalMercator(),
                "http://{s}.tianditu.gov.cn/DataServer?T=ibo_w&x={x}&y={y}&l={z}&tk={k}",
                serverNodes: new[] { "t0", "t1", "t2", "t3", "t4", "t5", "t6", "t7" },
                apiKey:"5d22d49fdc586cb5caed68bfb12d1e6b",
                name: "天地图全球境界",
                userAgent:"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/90.0.4430.212 Safari/537.36 Edg/90.0.818.62"

                ),
            new DownloadSource(
                new GlobalSphericalMercator(),
                "http://{s}.tianditu.gov.cn/DataServer?T=eva_w&x={x}&y={y}&l={z}&tk={k}",
                serverNodes: new[] { "t0", "t1", "t2", "t3", "t4", "t5", "t6", "t7" },
                apiKey:"5d22d49fdc586cb5caed68bfb12d1e6b",
                name: "天地图矢量英文注记",
                userAgent:"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/90.0.4430.212 Safari/537.36 Edg/90.0.818.62"

                ),
            new DownloadSource(
                new GlobalSphericalMercator(),
                "http://{s}.tianditu.gov.cn/DataServer?T=eia_w&x={x}&y={y}&l={z}&tk={k}",
                serverNodes: new[] { "t0", "t1", "t2", "t3", "t4", "t5", "t6", "t7" },
                apiKey:"5d22d49fdc586cb5caed68bfb12d1e6b",
                name: "天地图影像英文注记",
                userAgent:"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/90.0.4430.212 Safari/537.36 Edg/90.0.818.62"
                ),
        };
        public List<DownloadSource> Sources
        {
            get { return _sources; }
            set { SetProperty(ref _sources, value); }
        }

        private DownloadSource _selectedSource;
        public DownloadSource SelectedSource
        {
            get { return _selectedSource; }
            set { SetProperty(ref _selectedSource, value); }
        }

        private ObservableCollection<DownloadItem> _downloadItems;
        public ObservableCollection<DownloadItem> DownloadItems
        {
            get { return _downloadItems; }
            set { SetProperty(ref _downloadItems, value); }
        }
        public MainWindowViewModel()
        {
        }

        private double _progress;
        public double Progress
        {
            get { return _progress; }
            set { SetProperty(ref _progress, value); }
        }

        private DelegateCommand _downloadCmd;
        public DelegateCommand DownloadCmd =>
            _downloadCmd ?? (_downloadCmd = new DelegateCommand(ExecuteDownload));


        public void ExecuteDownload()
        {
            if (SelectedSource == null)
            {
                MessageBox.Show("请选择下载来源");
                return;
            }

            if (string.IsNullOrEmpty(FilePath))
            {
                MessageBox.Show("请选择下载文件");
                return;
            }
            try
            {
                DownloadItems = new ObservableCollection<DownloadItem>();
                var freesql = new FreeSqlBuilder().UseConnectionString(DataType.Sqlite, $"data source={FilePath}").UseAutoSyncStructure(true).Build();
                var source = new HttpTileSource(SelectedSource.TileSchema, SelectedSource.UrlFormatter, SelectedSource.ServerNodes, SelectedSource.ApiKey, SelectedSource.Name, SelectedSource.PersistentCache, SelectedSource.TileFetcher, SelectedSource.Attribution, SelectedSource.UserAgent);
                var info = new PakInfo
                {
                    MinX = MinX,
                    MinY = MinY,
                    MaxX = MaxX,
                    MaxY = MaxY,
                    Type = "image",
                    Source = source.Name,
                    MinLevel = 0,
                    MaxLevel = MaxLevel,
                };

                var wgs84 = GeographicCoordinateSystem.WGS84;
                var webMercator = ProjectedCoordinateSystem.WebMercator;
                var services = new CoordinateSystemServices();
                var transformation = services.CreateTransformation(wgs84, webMercator);

                freesql.Delete<PakInfo>().Where(x => true).ExecuteAffrows();
                freesql.Insert<PakInfo>().AppendData(info).ExecuteAffrows();

                var (minX, minY) = transformation.MathTransform.Transform(info.MinX, info.MinY);
                var (maxX, maxY) = transformation.MathTransform.Transform(info.MaxX, info.MaxY);
                var extent = new Extent(minX, minY, maxX, maxY);

                for (int level = info.MinLevel; level <= info.MaxLevel; level++)
                {
                    var range = TileTransform.WorldToTile(extent, level, source.Schema);
                    DownloadItems.Add(new DownloadItem()
                    {
                        Level = level,
                        Total = range.ColCount * range.RowCount
                    });
                }

                foreach (var item in DownloadItems)
                {

                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        var stopwatch = Stopwatch.StartNew();
                        var count = 0;
                        //source.Schema.GetTileInfos(extent, item.Level).AsParallel().ForAll(tileInfo =>
                        foreach (var tileInfo in source.Schema.GetTileInfos(extent, item.Level))
                        {
                            try
                            {

                                var z = tileInfo.Index.Level;
                                var x = tileInfo.Index.Row;
                                var y = tileInfo.Index.Col;
                                var table = PakBlock.GetTable(z, x, y);
                                if (!freesql.Select<PakBlock>().AsTable((_, n) => table).Any(b => b.X == x && b.Y == y && b.Z == z && b.Tile != null))
                                {
                                    var tile = source.GetTile(tileInfo);
                                    freesql.Insert<PakBlock>().AsTable(_ => table).AppendData(new PakBlock() { X = x, Y = y, Z = z, Tile = tile }).ExecuteAffrows();

                                    //File.WriteAllBytes($"{z}_{x}_{y}.png", tile);
                                }
                                item.Success++;
                            }
                            catch (Exception e)
                            {
                                item.Fail++;
                                item.Message = e.Message;
                            }
                            count++;
                            item.Speed = count / stopwatch.Elapsed.TotalSeconds;
                            if (stopwatch.Elapsed.TotalSeconds < 5)
                            {
                                stopwatch.Restart();
                                count = 0;
                            }
                            item.Progress = (item.Success + item.Fail) * 100D / item.Total;
                            Progress = DownloadItems.Sum(x => x.Success + x.Fail) * 100D / DownloadItems.Sum(x => x.Total);
                        }
                        //});
                    });
                }
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message);
            }

        }

        //public bool CanExecuteDownload()
        //{
        //    //return SelectedSource != null && !string.IsNullOrEmpty(FilePath);
        //}

        private DelegateCommand _browseCmd;
        private string _filePath;

        public DelegateCommand BrowseCmd =>
_browseCmd ?? (_browseCmd = new DelegateCommand(ExecuteCommandName));

        void ExecuteCommandName()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog() { };
            if (dialog.ShowDialog() == true)
            {

                FilePath = dialog.FileName;
            }

        }
    }
}
