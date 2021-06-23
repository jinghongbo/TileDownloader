namespace TileDownloader.Models
{
    /// <summary>
    /// 来源
    /// </summary>
    public class DownloadSource
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public string Key { get; set; }
        public string[] Nodes { get; set; }
        public string UserAgent { get; set; }
        public string Referer { get; set; }
        public string Cookies { get; set; }


    }
}