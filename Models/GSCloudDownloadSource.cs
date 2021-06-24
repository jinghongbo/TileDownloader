using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Converters;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TileDownloader.Attributes;

namespace TileDownloader.Models
{
    public class GSCloudDownloadSource : DownloadSource
    {
        public class GSCloudPage<T>
        {
            [JsonProperty("keyid")]
            public string KeyId { get; set; }

            [JsonProperty("total")]
            public int Total { get; set; }

            [JsonProperty("pageSize")]
            public int PageSize { get; set; }

            [JsonProperty("data")]
            public List<T> Data { get; set; }
        }

        public class GSCloudData
        {
            [JsonProperty("dataid")]
            public string DataId { get; set; }

            [JsonProperty("dataexists")]
            public int DataExists { get; set; }

            [JsonProperty("the_geom")]
            public Polygon TheGeom { get; set; }

            [JsonProperty("productid")]
            public string ProductId { get; set; }
        }

        [Argument("产品编号")]
        public string ProductId { get; set; }

        [Argument("输出目录")]
        public string OutputDir { get; set; } = "gscloud";


        public override async Task DownloadAsync(ObservableCollection<DownloadTask> downloadTasks)
        {
            downloadTasks.Clear();
            Directory.CreateDirectory(OutputDir);

            using var handler = new HttpClientHandler();
            var baseUrl = new Uri(Url);
            handler.CookieContainer.SetCookies(baseUrl, Cookies);
            using var client = new HttpClient(handler);
            client.BaseAddress = baseUrl;

            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", Referer);

            var list = new List<GSCloudData>();
            var offset = 0;
            var pageSize = 100;
            do
            {
                var page = await SearchAsync(client, offset, pageSize);

                list.AddRange(page.Data);
                offset += pageSize;
                if (offset > page.Total)
                {
                    break;
                }
            } while (true);

            using var semaphore = new SemaphoreSlim(Concurrent);
            var tasks = new List<Task>();
            foreach (var data in list)
            {
                if (data.DataExists == 1)
                {
                    await semaphore.WaitAsync();
                    var downloadTask = new DownloadTask()
                    {
                        Name = data.DataId,
                        Total = 1,
                    };

                    downloadTasks.Add(downloadTask);

                    var task = Task.Run(async () =>
                    {
                        try
                        {
                            var file = System.IO.Path.Combine(OutputDir, $"{data.DataId}.zip");
                            await using var output = System.IO.File.Create(file);
                            await using var stream = await client.GetStreamAsync($"sources/download/{data.ProductId}/{data.DataId}");
                            await stream.CopyToAsync(output);

                            downloadTask.Success++;
                        }
                        catch (Exception e)
                        {
                            downloadTask.Fail++;
                            downloadTask.ErrorMessage = e.Message;
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    });
                    tasks.Add(task);
                }
            }

            await Task.WhenAll(tasks);
        }

        private async Task<GSCloudPage<GSCloudData>> SearchAsync(HttpClient client, int offset = 0, int pageSize = 10)
        {
            var serializerSettings = new JsonSerializerSettings();
            serializerSettings.Converters.Add(new GeometryConverter());
            serializerSettings.Converters.Add(new CoordinateConverter());
            var reader = new NetTopologySuite.IO.WKTReader();
            var geom = reader.Read(Range);
            var tableInfo = new { offset = offset, pageSize = pageSize };
            var query = new { productid = new { @in = new[] { ProductId } }, geom_params = new { qtype = 1, value = geom } };

            var res = await client.PostAsync("/wsd/gscloud_wsd/dataset/p_search", new FormUrlEncodedContent(new Dictionary<string, string>()
            {
                {nameof(tableInfo),JsonConvert.SerializeObject(tableInfo,serializerSettings) },
                {nameof(query),JsonConvert.SerializeObject(query,serializerSettings)  },
            }));

            var json = await res.Content.ReadAsStringAsync();

            var page = JsonConvert.DeserializeObject<GSCloudPage<GSCloudData>>(json, serializerSettings);
            return page;
        }
    }
}