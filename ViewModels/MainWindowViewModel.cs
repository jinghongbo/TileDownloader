using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
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
        private bool _downloading;
        private DelegateCommand _browseCmd;

        private DelegateCommand _downloadCmd;

        private ObservableCollection<DownloadState> _status;
        private string _filePath;

        private double _progress;

        private DownloadSource _selectedSource;
        private List<DownloadSource> _sources = JsonConvert.DeserializeObject<List<DownloadSource>>(File.ReadAllText("sources.json"));

        public string Title { get; set; } = "Tile Downloader";

        public string FilePath
        {
            get => _filePath;
            set
            {
                SetProperty(ref _filePath, value);
                DownloadCmd.RaiseCanExecuteChanged();
            }
        }

        public int ConcurrentCount { get; set; } = 10;
        public Extent Extent { get; set; } = new Extent(-180, -85, 180, 85);
        public int MaxLevel { get; set; } = 9;

        private string _message;
        public string Message
        {
            get { return _message; }
            set { SetProperty(ref _message, value); }
        }

        public DateTime StartTime { get; set; }



        public List<DownloadSource> Sources
        {
            get => _sources;
            set => SetProperty(ref _sources, value);
        }

        public DownloadSource SelectedSource
        {
            get => _selectedSource;
            set
            {
                SetProperty(ref _selectedSource, value);
                DownloadCmd.RaiseCanExecuteChanged();
            }
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
            _downloadCmd ??= new DelegateCommand(ExecuteDownload, () =>
            {
                if (Downloading || string.IsNullOrEmpty(FilePath) || SelectedSource == null)
                {
                    return false;
                }
                else
                {
                    return true;
                }
            });

        public DelegateCommand BrowseCmd =>
            _browseCmd ??= new DelegateCommand(ExecuteBrowse);

        public bool Downloading
        {
            get => _downloading; set
            {
                SetProperty(ref _downloading, value);
                DownloadCmd.RaiseCanExecuteChanged();
            }
        }

        public void ExecuteDownload()
        {
            try
            {
                StartTime = DateTime.Now;
                Status = new ObservableCollection<DownloadState>();

                var schema = new GlobalSphericalMercator();
                var source = new HttpTileSource(schema, SelectedSource.Url,
                    SelectedSource.Nodes, SelectedSource.Key, SelectedSource.Name,
                    tileFetcher: (url) =>
                    {
                        using var client = new HttpClient();
                        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", SelectedSource.Referer);
                        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", SelectedSource.UserAgent);

                        return client.GetByteArrayAsync(url).ConfigureAwait(false).GetAwaiter().GetResult();

                    });
                var info = new PakInfo
                {
                    MinX = Extent.MinX,
                    MinY = Extent.MinY,
                    MaxX = Extent.MaxX,
                    MaxY = Extent.MaxY,
                    Type = "image",
                    Source = source.Name,
                    MinLevel = 0,
                    MaxLevel = MaxLevel
                };

                var services = new CoordinateSystemServices();
                var wgs84 = GeographicCoordinateSystem.WGS84;
                var webMercator = ProjectedCoordinateSystem.WebMercator;
                var transformation = services.CreateTransformation(wgs84, webMercator);
                var (minX, minY) = transformation.MathTransform.Transform(info.MinX, info.MinY);
                var (maxX, maxY) = transformation.MathTransform.Transform(info.MaxX, info.MaxY);
                var extent = new Extent(minX, minY, maxX, maxY);

                if (extent.Area < 0)
                {
                    MessageBox.Show("面积太小");
                    return;
                }
                Downloading = true;


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
                    using var freesql = new FreeSqlBuilder().UseConnectionString(DataType.Sqlite, $"data source={FilePath}")
                   .UseAutoSyncStructure(true).Build();

                    await freesql.Delete<PakInfo>().Where(x => true).ExecuteAffrowsAsync();
                    await freesql.Insert<PakInfo>().AppendData(info).ExecuteAffrowsAsync();


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

                            if (tasks.Count >= ConcurrentCount)
                                await RunTasksAsync(tasks, state);
                        }
                        if (tasks.Count>0)
                        {
                            await RunTasksAsync(tasks, state);
                        }
                    }
                    Downloading = false;
                    Message = "下载完成";
                });
            }
            catch (Exception e)
            {
                Message = "异常:" + e.Message;
            }
        }

        private async Task RunTasksAsync(List<Task<bool>> tasks, DownloadState state)
        {
            var stopwatch = Stopwatch.StartNew();
            var results = await Task.WhenAll(tasks);

            state.Success += results.Count(x => x);
            state.Fail += results.Count(x => !x);
            state.Speed = tasks.Count / stopwatch.Elapsed.TotalSeconds;
            state.Progress = (state.Success + state.Fail) * 100D / state.Total;

            Progress = Status.Sum(x => x.Success + x.Fail) * 100D / Status.Sum(x => x.Total);
            var timeSpan = (DateTime.Now - StartTime) / Status.Sum(x => x.Success + x.Fail) * Status.Sum(x => x.Total - x.Success - x.Fail);

            var message = "预计";
            if (timeSpan.Days > 1)
            {
                message += $"{timeSpan.Days:#}天";
            }
            if (timeSpan.Hours > 1)
            {
                message += $"{timeSpan.Hours:#}时";
            }
            if (timeSpan.Minutes > 1)
            {
                message += $"{timeSpan.Minutes:#}分";
            }
            if (timeSpan.Seconds > 1)
            {
                message += $"{timeSpan.Seconds:#}秒";
            }
            Message = message;
            tasks.Clear();
        }

        private void ExecuteBrowse()
        {
            var dialog = new SaveFileDialog { DefaultExt = ".pak", Filter = "PAK|*.pak" };
            if (dialog.ShowDialog() == true) FilePath = dialog.FileName;

        }


    }
}