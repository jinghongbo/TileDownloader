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
using Newtonsoft.Json.Linq;

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

        [DonwloadArgument("产品编号")]
        public string ProductId { get; set; }

        [DonwloadArgument("Cookies")]
        public string Cookies { get; set; }

        public override async Task DownloadAsync(ViewModels.MainWindowViewModel vm)
        {

            vm.Tasks = new System.Collections.ObjectModel.ObservableCollection<DownloadTask>();

            Directory.CreateDirectory(vm.Path);

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
                var page = await SearchAsync(client, vm.Range, offset, pageSize, vm.CancellationTokenSource.Token);

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
                    DataId = data.DataId,
                };
                vm.Tasks.Add(downloadTask);
            }



            using var semaphore = new SemaphoreSlim(vm.Concurrent);
            foreach (var downloadTask in vm.Tasks)
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
                           var file = Path.Combine(vm.Path, $"{downloadTask.DataId}.zip");
                           if (File.Exists(file))
                           {
                               await using var fs = File.OpenRead(file);
                               downloadTask.Total = downloadTask.Completed = fs.Length;
                               vm.Progress = vm.Tasks.Sum(x => x.Progress) / vm.Tasks.Count;
                               return;
                           }

                           var tmp = Path.Combine(vm.Path, $"{downloadTask.DataId}.tmp");
                           for (int i = 0; i < vm.Retry; i++)
                           {
                               try
                               {
                                   if (vm.CancellationTokenSource.IsCancellationRequested)
                                   {
                                       return;
                                   }
                                   using var res = await client.GetAsync($"sources/download/{ProductId}/{downloadTask.DataId}", HttpCompletionOption.ResponseHeadersRead, vm.CancellationTokenSource.Token);

                                   if (!res.IsSuccessStatusCode)
                                   {
                                       throw new Exception("未知错误:" + res.StatusCode);
                                   }
                                   downloadTask.Total = res.Content.Headers.ContentLength.Value;
                                   downloadTask.Completed = 0;
                                   await using var stream = await res.Content.ReadAsStreamAsync(vm.CancellationTokenSource.Token);
                                   await using var output = File.Create(tmp);

                                   var bufferSize = 2048;
                                   var buffer = new byte[bufferSize];
                                   var result = new List<byte>();

                                   var readBytes = 0;
                                   while (downloadTask.Completed < downloadTask.Total && (readBytes = stream.Read(buffer)) != 0)
                                   {
                                       await output.WriteAsync(buffer.AsMemory(0, readBytes), vm.CancellationTokenSource.Token);
                                       downloadTask.Completed += readBytes;
                                       vm.Progress = vm.Tasks.Sum(x => x.Progress) / vm.Tasks.Count;
                                   }
                                   await output.DisposeAsync();
                                   File.Move(tmp, file);



                                   break;
                               }
                               catch (Exception e)
                               {
                                   downloadTask.Error = "第" + (i + 1) + "次下载失败：" + (e.InnerException ?? e).Message;
                               }
                           }
                       }
                       finally
                       {
                           semaphore.Release();
                           downloadTask.Progress = 100;
                       }
                   }, vm.CancellationTokenSource.Token);
            }
            await vm.DownloadTask;
        }

        private async Task<GSCloudPage<GSCloudData>> SearchAsync(HttpClient client, Envelope range, int offset, int pageSize, CancellationToken cancellationToken)
        {
            var serializerSettings = new JsonSerializerSettings();
            serializerSettings.Converters.Add(new GeometryConverter());
            serializerSettings.Converters.Add(new CoordinateConverter());
            var geom = range.ToPolygon();
            var tableInfo = new { offset, pageSize };
            var query = new { productid = new { @in = new[] { ProductId } }, geom_params = new { qtype = 1, value = geom } };

            var res = await client.PostAsync("/wsd/gscloud_wsd/dataset/p_search", new FormUrlEncodedContent(new Dictionary<string, string>()
            {
                {nameof(tableInfo),JsonConvert.SerializeObject(tableInfo,serializerSettings) },
                {nameof(query),JsonConvert.SerializeObject(query,serializerSettings)  },
            }), cancellationToken);

            var json = await res.Content.ReadAsStringAsync(cancellationToken);

            var page = JsonConvert.DeserializeObject<GSCloudPage<GSCloudData>>(json, serializerSettings);
            return page;
        }

    }
}