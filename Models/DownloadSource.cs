using TileDownloader.Attributes;

namespace TileDownloader.Models
{
    /// <summary>
    /// 来源
    /// </summary>
    public class DownloadSource
    {
        [Argument("名称", IsReadOnly = true)]
        public string Name { get; set; }
        [Argument("地址")]
        public string Url { get; set; }
        [Argument("密钥")]
        public string Key { get; set; }
        [Argument("节点")]
        public string[] Nodes { get; set; }
        [Argument("User Agent")]
        public string UserAgent { get; set; }
        [Argument("Referer")]
        public string Referer { get; set; }
        [Argument("Cookies")]
        public string Cookies { get; set; }


    }
}