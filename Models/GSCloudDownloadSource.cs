using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Converters;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MapDownloader.Attributes;

namespace MapDownloader.Models
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

        public override async Task DownloadAsync(List<DownloadTask> downloadTasks)
        {
            Directory.CreateDirectory(OutputDir);

            using var handler = new HttpClientHandler();
            var baseUri = new Uri(Url);
            handler.CookieContainer.SetCookies(baseUri, Cookies);
            using var client = new HttpClient(handler);
            client.BaseAddress = baseUri;

            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", Referer);

            var tasks = new List<Task>();
            using var semaphore = new SemaphoreSlim(Concurrent);
            foreach (var downloadTask in downloadTasks)
            {
                await semaphore.WaitAsync();
                var task = Task.Run(async () =>
                {
                    try
                    {
                        var file = Path.Combine(OutputDir, $"{downloadTask.DataId}.zip");
                        var tmp = Path.Combine(OutputDir, $"{downloadTask.DataId}.tmp");
                        if (File.Exists(file))
                        {
                            await using var fs = File.OpenRead(file);
                            downloadTask.Total = downloadTask.Completed = fs.Length;
                            return;
                        }

                        for (int i = 0; i < Retry; i++)
                        {
                            try
                            {
                                using var res = await client.GetAsync($"sources/download/{downloadTask.ProductId}/{downloadTask.DataId}", HttpCompletionOption.ResponseHeadersRead);
                                downloadTask.Total = res.Content.Headers.ContentLength.Value;
                                await using var stream = await res.Content.ReadAsStreamAsync();

                                downloadTask.Completed = 0;

                                await using var output = File.Create(tmp);

                                var bufferSize = 2048;
                                var buffer = new byte[bufferSize];
                                var result = new List<byte>();

                                var readBytes = 0;
                                while (downloadTask.Completed < downloadTask.Total && (readBytes = stream.Read(buffer)) != 0)
                                {
                                    await output.WriteAsync(buffer, 0, readBytes);
                                    downloadTask.Completed += readBytes;
                                }

                                await output.DisposeAsync();

                                File.Move(tmp, file);
                                break;
                            }
                            catch (Exception e)
                            {
                                downloadTask.ErrorMessage = "第" + i + "次：" + (e.InnerException ?? e).Message;
                            }
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });
                tasks.Add(task);
                tasks = tasks.Where(x => x.Status == TaskStatus.RanToCompletion).ToList();
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

        public override async Task<List<DownloadTask>> GetDownloadTasksAsync()
        {
            var downloadTasks = new List<DownloadTask>();
            using var handler = new HttpClientHandler();
            var baseUri = new Uri(Url);
            handler.CookieContainer.SetCookies(baseUri, Cookies);
            using var client = new HttpClient(handler);
            client.BaseAddress = baseUri;

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

            foreach (var data in list)
            {
                if (data.DataExists != 1)
                {
                    continue;
                }
                var downloadTask = new DownloadTask()
                {
                    Name = data.DataId,
                    ProductId = data.ProductId,
                    DataId = data.DataId,
                };
                downloadTasks.Add(downloadTask);
            }

            return downloadTasks;
        }
    }
}