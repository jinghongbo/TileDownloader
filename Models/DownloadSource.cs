using System.Collections.Generic;
using System.Collections.ObjectModel;
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


        public abstract Task DownloadAsync(ViewModels.MainWindowViewModel vm);

    }
}