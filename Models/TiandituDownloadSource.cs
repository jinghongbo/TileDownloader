using MapDownloader.ViewModels;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MapDownloader.Models
{
    public class TiandituDownloadSource : TileDownloadSource
    {
        private async Task<string> CreateToken(CancellationToken cancellationToken)
        {
            {
                using var client = CreateClient(new Uri("https://console.tianditu.gov.cn"));
                JObject page1 = await GetPageAsync(client, 0, cancellationToken);
                foreach (var item in page1["result"])
                {
                    var appName = item["appName"].Value<string>();

                    if (appName == "MapDownloader")
                    {
                        var res1 = await client.PostAsync("https://console.tianditu.gov.cn/key/recycle", new FormUrlEncodedContent(new Dictionary<string, string>() {
                            {"appName",appName },
                            {"appType",item["appType"].ToString() },
                            {"appkey",item["appkey"].ToString()  },
                            {"keyState","1"  },

                         }), cancellationToken);
                        break;

                    }
                }

                JObject page2 = await GetPageAsync(client, 1, cancellationToken);
                foreach (var item in page2["result"])
                {
                    var appName = item["appName"].Value<string>();

                    if (appName == "MapDownloader")
                    {

                        var res2 = await client.PostAsync("https://console.tianditu.gov.cn/key/delrecycle", new FormUrlEncodedContent(new Dictionary<string, string>() {
                            {"appName",appName },
                            {"appType",item["appType"].ToString() },
                            {"appkey",item["appkey"].ToString()  },
                            {"keyState","1"  },

                         }), cancellationToken);
                        break;

                    }
                }



                var res3 = await client.PostAsync("https://console.tianditu.gov.cn/key/insert", new FormUrlEncodedContent(new Dictionary<string, string>() {
                            {"appName","MapDownloader" },
                            {"industryType","21" },
                            {"appType","0" },
                            {"appVerifyinfo","" },
                            {"permission","{checkway:{type:0,verify:\"\"}}}" },
                         }));


                var page3 = await GetPageAsync(client, 0, cancellationToken);

                foreach (var item in page3["result"])
                {
                    var appName = item["appName"].Value<string>();

                    if (appName == "MapDownloader")
                        return item["appkey"].Value<string>();
                }
            }

            throw new Exception("创建天地图APP失败");
        }

        private async Task<JObject> GetPageAsync(HttpClient client, int keyState, CancellationToken cancellationToken)
        {
            var res = await client.PostAsync("https://console.tianditu.gov.cn/key/page", new FormUrlEncodedContent(new Dictionary<string, string>() {
                    {"module","0" },
                    {"count","10" },
                    {"currentPage","1" },
                    {"orderColumn","" },
                    {"orderType","" },
                    {"keyState",keyState.ToString() },
                    {"queryStatus","2" },

                    }));
            var json = await res.Content.ReadAsStringAsync();
            var page = JObject.Parse(json);
            return page;
        }

        private int _count = 0;
        private SemaphoreSlim _semaphoreSlim;
        private string _token;

        public override Task DownloadAsync(MainWindowViewModel vm)
        {
            _count = 0;
            _semaphoreSlim = new SemaphoreSlim(1);
            return base.DownloadAsync(vm);
        }
        public override async Task<Uri> FormatUri(Uri uri, CancellationToken cancellationToken)
        {
            try
            {
                await _semaphoreSlim.WaitAsync(cancellationToken);
                Interlocked.Decrement(ref _count);

                if (_count < 0)
                {
                    Interlocked.Exchange(ref _count, 10000);

                    _token = await CreateToken(cancellationToken);
                }

            }
            finally
            {

                _semaphoreSlim.Release();
            }



            var builder = new UriBuilder(uri);
            var parmas = builder.Query.Split("&").Select(x => x.Split("=")).ToDictionary(x => x[0], x => x[1]);
            parmas["tk"] = _token;
            builder.Query = string.Join("&", parmas.Select(x => $"{x.Key}={x.Value}"));


            return builder.Uri;


        }
    }
}
