using System.Collections.Generic;
using System.IO;
using System.Linq;
using TileDownloader.Models;
using Newtonsoft.Json;

namespace TileDownloader.Services
{
    /// <summary>
    /// 来源配置服务：加载 Sources 目录下的一源一 JSON 配置文件
    /// </summary>
    public class SourcesConfigService
    {
        /// <summary>加载全部瓦片来源（Sources/*.json，每文件一个来源对象，按文件名排序保证顺序稳定）</summary>
        public List<DownloadSource> LoadSources()
        {
            var dir = Path.Combine(System.AppContext.BaseDirectory, "Sources");
            if (!Directory.Exists(dir))
            {
                dir = "Sources";
            }
            var list = new List<DownloadSource>();
            if (!Directory.Exists(dir))
            {
                return list;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, System.StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var src = JsonConvert.DeserializeObject<DownloadSource>(File.ReadAllText(file));
                    if (src != null && !string.IsNullOrWhiteSpace(src.Name))
                    {
                        list.Add(src);
                    }
                }
                catch
                {
                    // 单个配置文件损坏不应影响其余来源加载
                }
            }
            return list;
        }
    }
}
