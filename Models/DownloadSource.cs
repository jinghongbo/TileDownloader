namespace MapDownloader.Models
{
    /// <summary>
    /// 地图下载来源（仅通用瓦片源，来自 Sources.json 配置）
    /// </summary>
    public class DownloadSource
    {
        /// <summary>来源类型，当前固定为 "Tile"</summary>
        public string Type { get; set; }

        /// <summary>显示名称</summary>
        public string Name { get; set; }

        /// <summary>瓦片 URL 模板，支持 {s} 子域名、{z}/{x}/{y} 瓦片坐标、{k} 密钥占位符</summary>
        public string Url { get; set; }

        /// <summary>请求 User-Agent</summary>
        public string UserAgent { get; set; }

        /// <summary>请求 Referer</summary>
        public string Referer { get; set; }

        /// <summary>请求 Cookies（分号分隔）</summary>
        public string Cookies { get; set; }

        /// <summary>密钥（对应 URL 模板中的 {k}）</summary>
        public string Key { get; set; }

        /// <summary>子域名列表（逗号分隔，对应 URL 模板中的 {s}）</summary>
        public string Subdomains { get; set; }

        /// <summary>默认最小层级</summary>
        public int MinLevel { get; set; } = 2;

        /// <summary>默认最大层级</summary>
        public int MaxLevel { get; set; } = 15;

        /// <summary>解析子域名为数组</summary>
        public string[] GetSubdomains()
        {
            if (string.IsNullOrWhiteSpace(Subdomains))
            {
                return System.Array.Empty<string>();
            }
            return Subdomains.Split(',');
        }

        /// <summary>URL 模板含 {k} 时需要用户填写密钥（密钥不预置在来源配置中）</summary>
        public bool RequiresKey => !string.IsNullOrEmpty(Url) && Url.Contains("{k}");

        /// <summary>浅拷贝（用于注入用户填写密钥后生成运行时来源，不改动配置实例）</summary>
        public DownloadSource Clone() => (DownloadSource)MemberwiseClone();
    }
}
