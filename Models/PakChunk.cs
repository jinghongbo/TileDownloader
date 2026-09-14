using FreeSql.DataAnnotations;

namespace MapDownloader.Models
{
    /// <summary>
    /// 多文件 pak 的分块索引（存于主文件 .pak 的 chunks 表）：
    /// 每个 (z, tx, ty) 逻辑分块对应一个物理分块文件，记录文件名/瓦片总数/完成状态，
    /// 供续传时按文件粒度跳过已完成分块
    /// </summary>
    [Table(Name = "chunks")]
    public class PakChunk
    {
        /// <summary>缩放级别（主键）</summary>
        [Column(Name = "z", IsPrimary = true)]
        public int Z { get; set; }

        /// <summary>分块列号 = x / 512（主键）</summary>
        [Column(Name = "tx", IsPrimary = true)]
        public int Tx { get; set; }

        /// <summary>分块行号 = y / 512（主键）</summary>
        [Column(Name = "ty", IsPrimary = true)]
        public int Ty { get; set; }

        /// <summary>分块物理文件名（相对主文件所在目录）</summary>
        [Column(Name = "filename", StringLength = 260)]
        public string Filename { get; set; }

        /// <summary>该分块内属于任务范围的瓦片总数</summary>
        [Column(Name = "tile_count")]
        public int TileCount { get; set; }

        /// <summary>状态：0=进行中 1=完成（分块内任务范围瓦片全部落盘）</summary>
        [Column(Name = "status")]
        public int Status { get; set; }
    }
}
