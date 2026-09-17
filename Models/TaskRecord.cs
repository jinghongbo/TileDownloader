using System;
using FreeSql.DataAnnotations;

namespace TileDownloader.Models
{
    /// <summary>
    /// 任务持久化状态（数据库 int 存储）
    /// </summary>
    public enum TaskStatus2
    {
        /// <summary>下载中</summary>
        Running = 0,
        /// <summary>已完成</summary>
        Completed = 1,
        /// <summary>失败</summary>
        Failed = 2,
        /// <summary>已中断（取消/异常退出）</summary>
        Cancelled = 3,
    }

    /// <summary>
    /// 下载任务持久化记录（SQLite via FreeSql，tasks 表）
    /// </summary>
    [Table(Name = "tasks")]
    public class TaskRecord
    {
        /// <summary>主键自增</summary>
        [Column(Name = "id", IsPrimary = true, IsIdentity = true)]
        public long Id { get; set; }

        /// <summary>任务名称</summary>
        [Column(Name = "name", StringLength = 200)]
        public string Name { get; set; }

        /// <summary>来源名称</summary>
        [Column(Name = "source_name", StringLength = 200)]
        public string SourceName { get; set; }

        /// <summary>输出路径</summary>
        [Column(Name = "output_path", StringLength = 500)]
        public string OutputPath { get; set; }

        /// <summary>输出格式 Id</summary>
        [Column(Name = "format_id", StringLength = 50)]
        public string FormatId { get; set; }

        /// <summary>最小层级</summary>
        [Column(Name = "min_level")]
        public int MinLevel { get; set; }

        /// <summary>最大层级</summary>
        [Column(Name = "max_level")]
        public int MaxLevel { get; set; }

        /// <summary>范围（EPSG:4326）</summary>
        [Column(Name = "min_x")]
        public double MinX { get; set; }
        [Column(Name = "min_y")]
        public double MinY { get; set; }
        [Column(Name = "max_x")]
        public double MaxX { get; set; }
        [Column(Name = "max_y")]
        public double MaxY { get; set; }

        /// <summary>总瓦片数</summary>
        [Column(Name = "total")]
        public long Total { get; set; }

        /// <summary>已完成瓦片数</summary>
        [Column(Name = "completed")]
        public long Completed { get; set; }

        /// <summary>状态（int 存储）</summary>
        [Column(Name = "status")]
        public int Status { get; set; }

        /// <summary>错误信息</summary>
        [Column(Name = "error", StringLength = 1000)]
        public string Error { get; set; }

        /// <summary>创建时间</summary>
        [Column(Name = "created_at")]
        public DateTime CreatedAt { get; set; }

        /// <summary>检查点：当前层级</summary>
        [Column(Name = "cur_level")]
        public int CurLevel { get; set; }

        /// <summary>检查点：当前 X</summary>
        [Column(Name = "cur_x")]
        public int CurX { get; set; }

        /// <summary>检查点：当前 Y</summary>
        [Column(Name = "cur_y")]
        public int CurY { get; set; }

        /// <summary>是否完整块（仅 pak 格式生效；历史记录为 NULL，按未勾选处理）</summary>
        [Column(Name = "full_block")]
        public bool? FullBlock { get; set; }
    }
}
