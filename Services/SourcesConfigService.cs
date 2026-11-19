using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TileDownloader.Models;
using Newtonsoft.Json;

namespace TileDownloader.Services
{
    /// <summary>
    /// 来源配置服务：优先加载 Sources 目录下的一源一 JSON；
    /// 目录不存在或为空时（单文件发布），回退到程序集内嵌的同名资源。
    /// </summary>
    public class SourcesConfigService
    {
        /// <summary>加载全部瓦片来源（Sources/*.json，每文件一个来源对象，按文件名排序保证顺序稳定）</summary>
        public List<DownloadSource> LoadSources()
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Sources");
            if (!Directory.Exists(dir))
            {
                dir = "Sources";
            }

            var list = LoadFromDirectory(dir);
            return list.Count > 0 ? list : LoadEmbedded();
        }

        /// <summary>从外部目录加载（用户可在 exe 旁放 Sources 目录自定义/追加来源）</summary>
        private static List<DownloadSource> LoadFromDirectory(string dir)
        {
            var list = new List<DownloadSource>();
            if (!Directory.Exists(dir))
            {
                return list;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
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

        /// <summary>从程序集内嵌资源加载（单文件发布时使用）</summary>
        private static List<DownloadSource> LoadEmbedded()
        {
            var list = new List<DownloadSource>();
            var assembly = Assembly.GetExecutingAssembly();
            var names = assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith("TileDownloader.Sources.", StringComparison.Ordinal)
                            && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

            foreach (var name in names)
            {
                try
                {
                    using var stream = assembly.GetManifestResourceStream(name);
                    if (stream == null)
                    {
                        continue;
                    }

                    using var reader = new StreamReader(stream);
                    var src = JsonConvert.DeserializeObject<DownloadSource>(reader.ReadToEnd());
                    if (src != null && !string.IsNullOrWhiteSpace(src.Name))
                    {
                        list.Add(src);
                    }
                }
                catch
                {
                    // 忽略单个损坏的内嵌来源
                }
            }

            return list;
        }
    }
}
