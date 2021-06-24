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
using TileDownloader.Attributes;

namespace TileDownloader.Models
{
    public class TileDownloadSource : DownloadSource
    {
        [Argument("Schema")]
        public string Schema { get; set; }

        [Argument("最小级别")]
        public int MinLevel { get; set; }

        [Argument("最大级别")]
        public int MaxLevel { get; set; } = 15;

        [Argument("输出PAK")]
        public string OutputPak { get; set; } = "map.pak";

        public override async Task DownloadAsync(List<DownloadTask> downloadTasks)
        {
            using var handler = new HttpClientHandler();
            using var client = new HttpClient();
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", Referer);

            var schema = new GlobalSphericalMercator();
            var source = new HttpTileSource(schema, Url,
                Servers.Split(","), Key, Name);

            using var freesql = new FreeSqlBuilder().UseConnectionString(DataType.Sqlite, $"data source={OutputPak}")
           .UseAutoSyncStructure(true).Build();

            var tasks = new List<Task>();

            int test = 0;
            using var semaphore = new SemaphoreSlim(Concurrent);
            foreach (var downloadTask in downloadTasks)
            {
                await semaphore.WaitAsync();
                foreach (var tileInfo in source.Schema.GetTileInfos(downloadTask.Extent, downloadTask.Level))
                {
                    var task = Task.Run(async () =>
                    {
                        try
                        {
                            Interlocked.Increment(ref test);
                            bool successed = false;
                            for (int i = 0; i < Retry; i++)
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
                                        var uri = source.GetUri(tileInfo);

                                        var tile = await client.GetByteArrayAsync(uri);

                                        freesql.Insert<PakBlock>().AsTable(_ => table)
                                            .AppendData(new PakBlock { X = x, Y = y, Z = z, Tile = tile })
                                            .ExecuteAffrows();
                                    }
                                    successed = true;
                                    break;
                                }
                                catch (Exception e)
                                {
                                    lock (downloadTask)
                                        downloadTask.ErrorMessage = (e.InnerException ?? e).Message;
                                }
                            }
                            lock (downloadTask)
                            {
                                if (successed)
                                {
                                    downloadTask.Success++;
                                }
                                else
                                {
                                    downloadTask.Fail++;
                                }
                                downloadTask.Progress = (downloadTask.Success + downloadTask.Fail) * 100D / downloadTask.Total;
                                //downloadTask.TimeLeft = (DateTime.Now - downloadTask.StartTime) / (100D / downloadTask.Progress);
                            }
                        }
                        finally
                        {
                            semaphore.Release();
                            Interlocked.Decrement(ref test);
                        }
                    });
                    Console.WriteLine(test);
                    tasks.Add(task);
                    tasks = tasks.Where(x => x.Status != TaskStatus.RanToCompletion).ToList();
                }
            }

            await Task.WhenAll(tasks);
        }

        public override async Task<List<DownloadTask>> GetDownloadTasksAsync()
        {
            var downloadTasks = new List<DownloadTask>();
            var schema = new GlobalSphericalMercator();

            var reader = new WKTReader();

            var geom = reader.Read(Range);
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

            using var freesql = new FreeSqlBuilder().UseConnectionString(DataType.Sqlite, $"data source={OutputPak}")
           .UseAutoSyncStructure(true).Build();

            await freesql.Delete<PakInfo>().Where(x => true).ExecuteAffrowsAsync();
            await freesql.Insert<PakInfo>().AppendData(info).ExecuteAffrowsAsync();

            envelope = geom.Project(4326, 3857).EnvelopeInternal;

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

                downloadTasks.Add(downloadTask);
            }
            return downloadTasks;
        }
    }
}