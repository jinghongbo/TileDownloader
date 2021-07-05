using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Threading.Tasks;
using MapDownloader.Attributes;

namespace MapDownloader.Models
{
    public abstract class DownloadSource
    {
        public string Type { get; set; }
        public string Name { get; set; }

        [DonwloadArgument("地址")]
        public string Url { get; set; }

        [DonwloadArgument("User Agent")]
        public string UserAgent { get; set; }

        [DonwloadArgument("Referer")]
        public string Referer { get; set; }
        [DonwloadArgument("Cookies")]
        public string Cookies { get; set; }

        public abstract Task DownloadAsync(ViewModels.MainWindowViewModel vm);


        public HttpClient CreateClient(Uri baseUri)
        {
            var handler = new HttpClientHandler();
            handler.CookieContainer.SetCookies(baseUri, Cookies.Replace(";", ","));
            var client = new HttpClient(handler);
            client.BaseAddress = baseUri;
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", Referer);
            return client;
        }

    }
}