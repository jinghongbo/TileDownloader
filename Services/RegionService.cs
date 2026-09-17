using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using TileDownloader.Models;

namespace TileDownloader.Services
{
    /// <summary>
    /// 全国省市区行政区划服务：加载本地内置数据，提供层级遍历与快速检索
    /// </summary>
    public class RegionService
    {
        private readonly List<AdministrativeRegion> _provinces = new();
        private readonly List<RegionSearchResult> _searchIndex = new();

        public IReadOnlyList<AdministrativeRegion> Provinces => _provinces;

        public RegionService()
        {
            LoadRegions();
            BuildSearchIndex();
        }

        private void LoadRegions()
        {
            try
            {
                string? json = null;

                // 1. 优先从应用程序运行目录 Resources/regions.json 读取
                var localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "regions.json");
                if (File.Exists(localPath))
                {
                    json = File.ReadAllText(localPath);
                }
                else
                {
                    var parentPath = Path.Combine(AppContext.BaseDirectory, "Resources", "regions.json");
                    if (File.Exists(parentPath))
                    {
                        json = File.ReadAllText(parentPath);
                    }
                }

                // 2. 若文件未就绪，尝试从嵌入资源读取兜底
                if (string.IsNullOrEmpty(json))
                {
                    var asm = Assembly.GetExecutingAssembly();
                    var resourceName = asm.GetManifestResourceNames()
                        .FirstOrDefault(n => n.EndsWith("regions.json", StringComparison.OrdinalIgnoreCase));
                    if (resourceName != null)
                    {
                        using var stream = asm.GetManifestResourceStream(resourceName);
                        if (stream != null)
                        {
                            using var reader = new StreamReader(stream);
                            json = reader.ReadToEnd();
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(json))
                {
                    var items = JsonConvert.DeserializeObject<List<AdministrativeRegion>>(json);
                    if (items != null)
                    {
                        _provinces.AddRange(items);
                    }
                }
            }
            catch
            {
                // 静默容错，避免文件异常导致应用崩溃
            }
        }

        private void BuildSearchIndex()
        {
            foreach (var prov in _provinces)
            {
                // 省级
                _searchIndex.Add(new RegionSearchResult
                {
                    Region = prov,
                    Province = prov,
                    FullName = prov.Name,
                    DisplayName = prov.Name
                });

                foreach (var city in prov.Children)
                {
                    // 市级（若市名与省名不同，如非直辖市）
                    if (city.Name != prov.Name)
                    {
                        _searchIndex.Add(new RegionSearchResult
                        {
                            Region = city,
                            Province = prov,
                            City = city,
                            FullName = $"{prov.Name} / {city.Name}",
                            DisplayName = $"{city.Name} ({prov.Name})"
                        });
                    }

                    // 区县级
                    foreach (var dist in city.Children)
                    {
                        var parentDesc = city.Name == prov.Name ? prov.Name : $"{prov.Name}·{city.Name}";
                        _searchIndex.Add(new RegionSearchResult
                        {
                            Region = dist,
                            Province = prov,
                            City = city,
                            District = dist,
                            FullName = $"{prov.Name} / {city.Name} / {dist.Name}",
                            DisplayName = $"{dist.Name} ({parentDesc})"
                        });
                    }
                }
            }
        }

        /// <summary>
        /// 模糊检索行政区划（支持按名称前缀/包含快速匹配）
        /// </summary>
        public IReadOnlyList<RegionSearchResult> Search(string keyword, int maxResults = 25)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return Array.Empty<RegionSearchResult>();
            }

            var query = keyword.Trim();

            // 优先名字完全一致或前缀匹配的项，其次包含匹配
            return _searchIndex
                .Where(r => r.Region.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            r.FullName.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.Region.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(r => r.Region.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                .Take(maxResults)
                .ToList();
        }
    }
}
