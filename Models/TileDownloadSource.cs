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
        [DonwloadArgument("Schema")]
        public string Schema { get; set; }


        public override async Task DownloadAsync(ViewModels.MainWindowViewModel vm)
        {
            vm.Tasks = new ObservableCollection<DownloadTask>();

            using var handler = new HttpClientHandler();
            using var client = new HttpClient();
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", Referer);
            var schema = new GlobalSphericalMercator();
            var source = new HttpTileSource(schema, Url,
                Subdomains.Split(","), Key, Name);
            var reader = new WKTReader();

            var geom = reader.Read(vm.Range);
            var envelope = geom.EnvelopeInternal;

            var info = new PakInfo
            {
                Type = "image",
                Source = Name,
                MinX = envelope.MinX,
                MinY = envelope.MinY,
                MaxX = envelope.MaxX,
                MaxY = envelope.MaxY,
                MinLevel = MinLevel,
                MaxLevel = MaxLevel
            };

            using var freesql = new FreeSqlBuilder().UseConnectionString(DataType.Sqlite, $"data source={vm.Path}")
           .UseAutoSyncStructure(true).Build();

            await freesql.Delete<PakInfo>().Where(x => true).ExecuteAffrowsAsync();
            await freesql.Insert<PakInfo>().AppendData(info).ExecuteAffrowsAsync();

            var projection = geom.Projection(4326, 3857);
            envelope = projection.EnvelopeInternal;

            var extent = new Extent(envelope.MinX, envelope.MinY, envelope.MaxX, envelope.MaxY);

            for (var level = MinLevel; level <= MaxLevel; level++)
            {
                var range = TileTransform.WorldToTile(extent, level, schema);
                var downloadTask = new DownloadTask
                {
                    Name = $"{level}",
                    Total = range.ColCount * range.RowCount,
                    Level = level,
                    Extent = extent,
                };

                vm.Tasks.Add(downloadTask);
            }
            var tables = new ConcurrentDictionary<string, object>();
            using var semaphore = new SemaphoreSlim(vm.Concurrent);
            foreach (var downloadTask in vm.Tasks)
            {
                foreach (var tileInfo in source.Schema.GetTileInfos(downloadTask.Extent, downloadTask.Level))
                {
                    if (vm.CancellationTokenSource.IsCancellationRequested)
                    {
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
                                     tables.GetOrAdd(table, _ =>
                                     {
                                         freesql.CodeFirst.SyncStructure(typeof(PakBlock), table);
                                         return new object { };
                                     });

                                     if (!freesql.Select<PakBlock>().AsTable((_, n) => table).Any(b =>
                                         b.X == x && b.Y == y && b.Z == z && b.Tile != null))
                                     {
                                         var uri = source.GetUri(tileInfo);

                                         var tile = await client.GetByteArrayAsync(uri, vm.CancellationTokenSource.Token);

                                         await freesql.Insert<PakBlock>().AsTable(_ => table)
                                                .AppendData(new PakBlock { X = x, Y = y, Z = z, Tile = tile })
                                                .ExecuteAffrowsAsync(vm.CancellationTokenSource.Token);
                                     }
                                     break;
                                 }
                                 catch (Exception e)
                                 {
                                     lock (downloadTask)
                                         downloadTask.Error = "第" + (i + 1) + "次：" + (e.InnerException ?? e).Message;
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
            freesql.Dispose();
        }

    }
}