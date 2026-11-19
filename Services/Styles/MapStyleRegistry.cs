using System;
using System.Collections.Generic;
using System.Linq;
using TileDownloader.Models;
using TileDownloader.Models.Styles;

namespace TileDownloader.Services.Styles
{
    /// <summary>样式适配器注册表：按来源声明解析适配器。</summary>
    public class MapStyleRegistry
    {
        private readonly List<IMapStyleAdapter> _adapters;

        public MapStyleRegistry(IEnumerable<IMapStyleAdapter> adapters)
        {
            _adapters = adapters?.ToList() ?? new List<IMapStyleAdapter>();
        }

        public IReadOnlyList<IMapStyleAdapter> Adapters => _adapters;

        /// <summary>按 source.StyleProvider 找适配器；没有声明则返回 null（不显示样式入口）</summary>
        public IMapStyleAdapter? Resolve(DownloadSource? source)
        {
            if (source == null || string.IsNullOrWhiteSpace(source.StyleProvider))
            {
                return null;
            }

            return _adapters.FirstOrDefault(a => a.Supports(source));
        }

        public IMapStyleAdapter? ById(string? id)
            => string.IsNullOrWhiteSpace(id) ? null : _adapters.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
    }
}
