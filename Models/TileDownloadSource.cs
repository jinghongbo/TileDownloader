using BruTile;
using BruTile.Predefined;
using BruTile.Web;
using FreeSql;
using NetTopologySuite.IO;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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
        public int MaxLevel { get; set; } = 9;


        public override async Task DownloadAsync(ObservableCollection<DownloadTask> tasks)
        {


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

            using var freesql = new FreeSqlBuilder().UseConnectionString(DataType.Sqlite, $"data source={Path}")
           .UseAutoSyncStructure(true).Build();

            await freesql.Delete<PakInfo>().Where(x => true).ExecuteAffrowsAsync();
            await freesql.Insert<PakInfo>().AppendData(info).ExecuteAffrowsAsync();

            envelope = geom.Project(4326, 3857).EnvelopeInternal;

            var extent = new Extent(envelope.MinX, envelope.MinY, envelope.MaxX, envelope.MaxY);

            using var semaphore = new SemaphoreSlim(Concurrent);
            for (var level = MinLevel; level <= MaxLevel; level++)
            {
                var range = TileTransform.WorldToTile(extent, level, schema);
                var task = new DownloadTask
                {
                    Name = $"第{level}层级",
                    Total = range.ColCount * range.RowCount
                };
                Application.Current.Dispatcher.Invoke(() =>
                {
                    tasks.Add(task);
                });


                task.StartTime = DateTime.Now;
                foreach (var tileInfo in source.Schema.GetTileInfos(extent, level))
                {
                    await semaphore.WaitAsync();
                    _ = Task.Run(async () =>
                           {
                               try
                               {
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
                                           lock (task)
                                           {
                                               task.Success++;
                                               task.Progress = (task.Success + task.Fail) * 100D / task.Total;
                                               task.TimeLeft = (DateTime.Now - task.StartTime) / (100D / task.Progress);
                                           }
                                           //var message = "预计";
                                           //if (task.TimeLeft.Days > 1)
                                           //{
                                           //    message += $"{task.TimeLeft.Days:#}天";
                                           //}
                                           //if (task.TimeLeft.Hours > 1)
                                           //{
                                           //    message += $"{task.TimeLeft.Hours:#}时";
                                           //}
                                           //if (task.TimeLeft.Minutes > 1)
                                           //{
                                           //    message += $"{task.TimeLeft.Minutes:#}分";
                                           //}
                                           //if (task.TimeLeft.Seconds > 1)
                                           //{
                                           //    message += $"{task.TimeLeft.Seconds:#}秒";
                                           //}
                                           //task.Message = message;
                                           return;
                                       }
                                       catch (Exception e)
                                       {
                                           task.Message = "下载失败：" + e.Message;
                                       }
                                   }
                               }
                               finally
                               {
                                   semaphore.Release();
                               }
                               lock (task)
                               {

                                   task.Fail++;
                               }
                           });
                }
            }
        }
    }
}