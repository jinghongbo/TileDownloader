using System;
using System.Collections.Generic;

namespace TileDownloader.Services
{
    /// <summary>
    /// 存储格式注册表：
    /// 提供四种格式的元数据描述（供格式选择 UI 展示），
    /// 并在 <see cref="CreateStore"/> 中按 FormatId 创建对应存储实现：
    /// MultiPak（多文件 pak，默认）/ Pak（单文件 pak）/ MBTiles / Directory（瓦片目录）
    /// </summary>
    public class TileStoreRegistry
    {
        private class Descriptor : ITileStoreDescriptor
        {
            public string FormatId { get; init; } = "";
            public string DisplayName { get; init; } = "";
            public string DefaultExtension { get; init; } = "";
        }

        /// <summary>全部支持的输出格式元数据</summary>
        public IReadOnlyList<ITileStoreDescriptor> Descriptors { get; } = new List<ITileStoreDescriptor>
        {
            new Descriptor { FormatId = "MultiPak", DisplayName = "多文件 pak", DefaultExtension = "" },
            new Descriptor { FormatId = "Pak", DisplayName = "单文件 pak", DefaultExtension = ".pak" },
            new Descriptor { FormatId = "MBTiles", DisplayName = "MBTiles", DefaultExtension = ".mbtiles" },
            new Descriptor { FormatId = "Directory", DisplayName = "瓦片目录", DefaultExtension = "" },
        };

        /// <summary>
        /// 按格式 Id 创建存储实例（未知格式回退为瓦片目录）。
        /// 存储实例生命周期与单次下载任务一致（引擎负责 Initialize/Finalize）
        /// </summary>
        public ITileStore CreateStore(string formatId)
        {
            return formatId switch
            {
                "MultiPak" => new MultiPakTileStore(),
                "Pak" => new PakTileStore(),
                "MBTiles" => new MbTilesTileStore(),
                "Directory" => new DirectoryTileStore(),
                _ => new DirectoryTileStore(),
            };
        }
    }
}
