using BruTile;
using BruTile.Predefined;
using BruTile.Web;
using FreeSql;
using NetTopologySuite.IO;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using TileDownloader.Attributes;
using TileDownloader.ViewModels;

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

        public override async Task DownloadAsync(ObservableCollection<DownloadTask> downloadTasks)
        {
            downloadTasks.Clear();
            using var handler = new HttpClientHandler();
            using var client = new HttpClient();
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", Referer);

            var schema = new GlobalSphericalMercator();
            var source = new HttpTileSource(schema, Url,
                Servers.Split(","), Key, Name);

            var reader = new WKTReader();

            var geom = reader.Read(Range);
            var envelope = geom.EnvelopeInternal;

            var info = new PakInfo
            {
                Type = "image",
                Source = source.Name,
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

            var tasks = new List<Task>();

            var queue = new ConcurrentQueue<(DownloadTask downloadTask, TileInfo tileInfo)>();
            var completed = false;
            //using var semaphore = new SemaphoreSlim(Concurrent);
            for (int i = 0; i < Concurrent; i++)
            {
                var task = Task.Run(async () =>
                {
                    while (true)
                    {
                        if (queue.TryDequeue(out var item))
                        {
                            var downloadTask = item.downloadTask;
                            var tileInfo = item.tileInfo;

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
                        else
                        {
                            if (completed)
                            {
                                return;
                            }
                            await Task.Yield();
                        }
                    }

                });

                tasks.Add(task);
            }



            for (var level = MinLevel; level <= MaxLevel; level++)
            {
                var range = TileTransform.WorldToTile(extent, level, schema);
                var downloadTask = new DownloadTask
                {
                    Name = $"{level}",
                    Total = range.ColCount * range.RowCount
                };
                downloadTasks.Add(downloadTask);

                downloadTask.StartTime = DateTime.Now;
                foreach (var tileInfo in source.Schema.GetTileInfos(extent, level))
                {
                    //while (queue.Count > Concurrent * 2)
                    //{
                    //    await Task.Delay(10);
                    //}
                    queue.Enqueue((downloadTask, tileInfo));
                }


            }
            completed = true;
            await Task.WhenAll(tasks);
        }
    }
}