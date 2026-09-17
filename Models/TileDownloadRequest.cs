using NetTopologySuite.Geometries;
using TileDownloader.Services;

namespace TileDownloader.Models
{
    /// <summary>
    /// 瓦片下载请求（引擎入参）
    /// </summary>
    public class TileDownloadRequest
    {
        /// <summary>地图来源</summary>
        public DownloadSource Source { get; set; }

        /// <summary>下载范围（EPSG:4326）</summary>
        public Envelope Range { get; set; }

        /// <summary>最小层级</summary>
        public int MinLevel { get; set; }

        /// <summary>最大层级</summary>
        public int MaxLevel { get; set; }

        /// <summary>输出路径（文件或目录，取决于格式）</summary>
        public string OutputPath { get; set; }

        /// <summary>输出格式 Id</summary>
        public string FormatId { get; set; }

        /// <summary>并发数</summary>
        public int Concurrent { get; set; } = 4;

        /// <summary>失败重试次数</summary>
        public int Retry { get; set; } = 4;

        /// <summary>下载是否使用系统代理（默认 false=直连，配合 Google Hosts 加速；true=跟随系统代理/VPN）</summary>
        public bool UseProxy { get; set; } = false;

        /// <summary>是否完整块（仅 pak 格式生效：blocks 写满该层级全部瓦片，blocks_{z}_{tx}_{ty} 写满 512×512 整块）</summary>
        public bool FullBlock { get; set; }

        /// <summary>提前计算该请求对应的瓦片总量</summary>
        public long CalculateTotalTiles()
        {
            if (Range == null || Range.IsNull) return 0;
            return TileUrlBuilder.CalculateTotalTileCount(Range.MinX, Range.MaxX, Range.MinY, Range.MaxY, MinLevel, MaxLevel, FullBlock);
        }

        /// <summary>快照为持久化任务记录</summary>
        public TaskRecord ToRecord(string name)
        {
            return new TaskRecord
            {
                Name = name,
                SourceName = Source?.Name ?? string.Empty,
                OutputPath = OutputPath,
                FormatId = FormatId,
                MinLevel = MinLevel,
                MaxLevel = MaxLevel,
                MinX = Range?.MinX ?? 0,
                MinY = Range?.MinY ?? 0,
                MaxX = Range?.MaxX ?? 0,
                MaxY = Range?.MaxY ?? 0,
                Total = CalculateTotalTiles(),
                Completed = 0,
                FullBlock = FullBlock,
                CreatedAt = System.DateTime.Now,
                Status = (int)TaskStatus2.Running,
            };
        }
    }
}
