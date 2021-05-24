using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using BruTile;
using BruTile.Predefined;
using BruTile.Web;
using FreeSql;
using Microsoft.Win32;
using Newtonsoft.Json;
using Prism.Commands;
using Prism.Mvvm;
using ProjNet;
using ProjNet.CoordinateSystems;
using TileDownloader.Models;

namespace TileDownloader.ViewModels
{
    public class MainWindowViewModel : BindableBase
    {
        private DelegateCommand _browseCmd;

        private DelegateCommand _downloadCmd;

        private ObservableCollection<DownloadState> _status;
        private string _filePath;

        private double _progress;

        private DownloadSource _selectedSource;
        private List<DownloadSource> _sources = JsonConvert.DeserializeObject<List<DownloadSource>>(File.ReadAllText("sources.json"));

        public string Title { get; set; } = "瓦片下载器";

        public string FilePath
        {
            get => _filePath;
            set => SetProperty(ref _filePath, value);
        }

        public int Concurrent { get; set; } = 10;
        public double MinX { get; set; } = 111;

        public double MinY { get; set; } = 41;

        public double MaxX { get; set; } = 114;

        public double MaxY { get; set; } = 44;

        public int MaxLevel { get; set; } = 15;



        public List<DownloadSource> Sources
        {
            get => _sources;
            set => SetProperty(ref _sources, value);
        }

        public DownloadSource SelectedSource
        {
            get => _selectedSource;
            set => SetProperty(ref _selectedSource, value);
        }

        public ObservableCollection<DownloadState> Status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        public double Progress
        {
            get => _progress;
            set => SetProperty(ref _progress, value);
        }

        public DelegateCommand DownloadCmd =>
            _downloadCmd ??= new DelegateCommand(ExecuteDownload);

        public DelegateCommand BrowseCmd =>
            _browseCmd ??= new DelegateCommand(ExecuteBrowse);

    
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
                Status = new ObservableCollection<DownloadState>();
                var freesql = new FreeSqlBuilder().UseConnectionString(DataType.Sqlite, $"data source={FilePath}")
                    .UseAutoSyncStructure(true).Build();
                var schema = SelectedSource.TileSchema switch
                {
                    nameof(GlobalSphericalMercator) => new GlobalSphericalMercator(),
                    _ => new TileSchema(),
                };
                var source = new HttpTileSource(schema, SelectedSource.UrlFormatter,
                    SelectedSource.ServerNodes, SelectedSource.ApiKey, SelectedSource.Name,
                    userAgent: SelectedSource.UserAgent);
                var info = new PakInfo
                {
                    MinX = MinX,
                    MinY = MinY,
                    MaxX = MaxX,
                    MaxY = MaxY,
                    Type = "image",
                    Source = source.Name,
                    MinLevel = 0,
                    MaxLevel = MaxLevel
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

                for (var level = info.MinLevel; level <= info.MaxLevel; level++)
                {
                    var range = TileTransform.WorldToTile(extent, level, source.Schema);
                    Status.Add(new DownloadState
                    {
                        Level = level,
                        Total = range.ColCount * range.RowCount
                    });
                }

                Task.Run(async () =>
                {
                    var tasks = new List<Task<bool>>();
                    foreach (var state in Status)
                    {
                        foreach (var tileInfo in source.Schema.GetTileInfos(extent, state.Level))
                        {
                            tasks.Add(Task.Run(() =>
                            {
                                for (int i = 0; i < 3; i++)
                                {
                                    try
                                    {
                                        var z = tileInfo.Index.Level;
                                        var x = tileInfo.Index.Col;
                                        var y = tileInfo.Index.Row;
                                        var table = PakBlock.GetTable(z, x, y);
                                        if (!freesql.Select<PakBlock>().AsTable((_, n) => table).Any(b =>
                                            b.X == x && b.Y == y && b.Z == z && b.Tile != null))
                                        {
                                            var tile = source.GetTile(tileInfo);
                                            freesql.Insert<PakBlock>().AsTable(_ => table)
                                                .AppendData(new PakBlock { X = x, Y = y, Z = z, Tile = tile })
                                                .ExecuteAffrows();
                                        }
                                        return true;
                                    }
                                    catch (Exception e)
                                    {
                                        state.Message = e.Message;
                                    }
                                }

                                return false;
                            }));

                            if (tasks.Count >= Concurrent)
                                await RunTasksAsync(tasks, state);
                        }
                        await RunTasksAsync(tasks, state);
                    }

                    MessageBox.Show("下载完成");
                });
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message);
            }
        }

        private async Task RunTasksAsync(List<Task<bool>> tasks, DownloadState item)
        {
            var stopwatch = Stopwatch.StartNew();
            var results = await Task.WhenAll(tasks);

            item.Success += results.Count(x => x);
            item.Fail += results.Count(x => !x);
            item.Speed = tasks.Count / stopwatch.Elapsed.TotalSeconds;
            item.Progress = (item.Success + item.Fail) * 100D / item.Total;
            Progress = Status.Sum(x => x.Success + x.Fail) * 100D / Status.Sum(x => x.Total);

            tasks.Clear();
        }

        private void ExecuteBrowse()
        {
            var dialog = new SaveFileDialog { DefaultExt = ".pak", Filter = "PAK|*.pak" };
            if (dialog.ShowDialog() == true) FilePath = dialog.FileName;
        }
    }
}