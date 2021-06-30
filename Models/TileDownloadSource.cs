using BruTile;
using BruTile.Predefined;
using BruTile.Web;
using FreeSql;
using NetTopologySuite.IO;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MapDownloader.Attributes;
using System.Net;

namespace MapDownloader.Models
{
    public class TileDownloadSource : DownloadSource
    {
        [DonwloadArgument("最小层级")]
        public int MinLevel { get; set; }

        [DonwloadArgument("最大层级")]
        public int MaxLevel { get; set; } = 15;


        [DonwloadArgument("密钥")]
        public string Key { get; set; }

        [DonwloadArgument("子域名")]
        public string Subdomains { get; set; }


        public override async Task DownloadAsync(ViewModels.MainWindowViewModel vm)
        {
            vm.Tasks = new ObservableCollection<DownloadTask>();
            using var handler = new HttpClientHandler();
            using var client = new HttpClient(handler);
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", Referer);
            var subdomains = Subdomains.Split(",");
            var schema = new GlobalSphericalMercator();
            var source = new HttpTileSource(schema, Url, subdomains, Key, Name);

            var info = new PakInfo
            {
                Type = "image",
                Source = Name,
                MinX = vm.Range.MinX,
                MinY = vm.Range.MinY,
                MaxX = vm.Range.MaxX,
                MaxY = vm.Range.MaxY,
                MinLevel = MinLevel,
                MaxLevel = MaxLevel
            };

            using var freesql = new FreeSqlBuilder().UseConnectionString(DataType.Sqlite, $"data source={vm.Path}")
           .UseAutoSyncStructure(true).Build();

            await freesql.Delete<PakInfo>().Where(x => true).ExecuteAffrowsAsync();
            await freesql.Insert<PakInfo>().AppendData(info).ExecuteAffrowsAsync();

            var envelope = vm.Range.Projection(4326, 3857);

            var extent1 = new Extent(envelope.MinX, envelope.MinY, envelope.MaxX, envelope.MaxY);

            var extent = new Extent(-20037508, -20037508, 20037508, 20037508);

            for (var level = MinLevel; level <= MaxLevel; level++)
            {
                var range = TileTransform.WorldToTile(extent, level, schema);
                var downloadTask = new DownloadTask
                {
                    Name = $"{level}",
                    Total = range.ColCount * range.RowCount,
                    Level = level,
                };

                vm.Tasks.Add(downloadTask);

                if (level > 10)
                {
                    var minx = (int)Math.Floor(range.FirstCol / 512d);
                    var miny = (int)Math.Floor(range.FirstRow / 512d);
                    var maxx = (int)Math.Ceiling(range.FirstCol / 512d);
                    var maxy = (int)Math.Ceiling(range.FirstRow / 512d);
                    for (int x = minx; x <= maxx; x++)
                    {
                        for (int y = miny; y <= maxy; y++)
                        {
                            var table = $"blocks_{level}_{x}_{y}";
                            freesql.CodeFirst.SyncStructure(typeof(PakBlock), table);
                        }
                    }
                }
            }
            freesql.CodeFirst.SyncStructure(typeof(PakBlock), "blocks");
            using var semaphore = new SemaphoreSlim(vm.Concurrent);
            foreach (var downloadTask in vm.Tasks)
            {
                foreach (var tileInfo in schema.GetTileInfos(extent, downloadTask.Level))
                {
                    if (vm.CancellationTokenSource.IsCancellationRequested)
                    {
                        freesql.Dispose();
                        return;
                    }
                    await semaphore.WaitAsync();
                    _ = Task.Run(async () =>
                     {
                         try
                         {
                             for (int i = 0; i < vm.Retry; i++)
                             {
                                 try
                                 {
                                     if (vm.CancellationTokenSource.IsCancellationRequested)
                                     {
                                         return;
                                     }
                                     var z = tileInfo.Index.Level;
                                     var x = tileInfo.Index.Col;
                                     var y = tileInfo.Index.Row;
                                     var table = PakBlock.GetTable(z, x, y);
                                     if (!freesql.Select<PakBlock>().AsTable((_, n) => table).Any(b =>
                                         b.X == x && b.Y == y && b.Z == z && b.Tile != null))
                                     {
                                         var uri = source.GetUri(tileInfo);
                                         using var res = await client.GetAsync(uri, vm.CancellationTokenSource.Token);
                                         if (!res.IsSuccessStatusCode)
                                         {
                                             throw new Exception("未知错误:" + res.StatusCode);
                                         }
                                         var tile = await res.Content.ReadAsByteArrayAsync(vm.CancellationTokenSource.Token);
                                         await freesql.Insert<PakBlock>().AsTable(_ => table)
                                                .AppendData(new PakBlock { X = x, Y = y, Z = z, Tile = tile })
                                                .ExecuteAffrowsAsync(vm.CancellationTokenSource.Token);
                                     }
                                     break;
                                 }
                                 catch (Exception e)
                                 {
                                     lock (downloadTask)
                                         downloadTask.Error = "第" + (i + 1) + "次下载失败：" + (e.InnerException ?? e).Message;
                                 }
                             }
                         }
                         finally
                         {
                             lock (downloadTask)
                             {
                                 downloadTask.Completed++;
                                 vm.Progress = vm.Tasks.Sum(x => x.Completed) * 100d / vm.Tasks.Sum(x => x.Total);
                             }
                             semaphore.Release();
                         }
                     }, vm.CancellationTokenSource.Token);

                }
            }
            await vm.DownloadTask;
        }

    }
}