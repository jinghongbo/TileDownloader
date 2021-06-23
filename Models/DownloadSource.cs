using System.Collections.ObjectModel;
using System.Threading.Tasks;
using TileDownloader.Attributes;

namespace TileDownloader.Models
{
    public class DownloadSource
    {
        [Argument("名称", IsReadOnly = true)]
        public string Name { get; set; }

        [Argument("地址")]
        public string Url { get; set; }

        [Argument("密钥")]
        public string Key { get; set; }

        [Argument("节点")]
        public string Servers { get; set; }

        [Argument("User Agent")]
        public string UserAgent { get; set; }

        [Argument("Referer")]
        public string Referer { get; set; }

        [Argument("Cookies")]
        public string Cookies { get; set; }

        [Argument("范围")]
        public string Range { get; set; } = "POLYGON ((30 10, 40 40, 20 40, 10 20, 30 10))";
        [Argument("并发")]
        public int Concurrent { get; set; } = 10;

        [Argument("重试")]
        public int Retry { get; set; } = 3;

        [Argument("路径")]
        public string Path { get; set; } = "";
        public virtual Task DownloadAsync(ObservableCollection<DownloadTask> tasks)
        {
            throw new System.Exception();
        }
    }
}