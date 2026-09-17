using System;
using System.Collections.Generic;
using NetTopologySuite.Geometries;

namespace TileDownloader.Models
{
    /// <summary>
    /// 行政区划数据模型（省 / 市 / 区县）
    /// </summary>
    public class AdministrativeRegion
    {
        /// <summary>行政区划代码（adcode，如 110000、330102）</summary>
        public string Code { get; set; } = string.Empty;

        /// <summary>行政区划名称（如 北京市、杭州市、西湖区）</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>级别：province / city / district</summary>
        public string Level { get; set; } = string.Empty;

        /// <summary>
        /// 经纬度外包框 [minLon, minLat, maxLon, maxLat]（EPSG:4326）
        /// </summary>
        public double[] Bbox { get; set; } = Array.Empty<double>();

        /// <summary>下级行政区划列表</summary>
        public List<AdministrativeRegion> Children { get; set; } = new();

        /// <summary>
        /// 转换为 NetTopologySuite 的 Envelope（外包矩形，供瓦片计算与地图渲染）
        /// </summary>
        public Envelope? ToEnvelope()
        {
            if (Bbox is { Length: >= 4 })
            {
                // Envelope(minX, maxX, minY, maxY) -> (minLon, maxLon, minLat, maxLat)
                return new Envelope(Bbox[0], Bbox[2], Bbox[1], Bbox[3]);
            }
            return null;
        }

        public override string ToString() => Name;
    }

    /// <summary>
    /// 行政区划模糊检索结果项
    /// </summary>
    public class RegionSearchResult
    {
        /// <summary>目标行政区</summary>
        public AdministrativeRegion Region { get; set; } = null!;

        /// <summary>所属省份</summary>
        public AdministrativeRegion? Province { get; set; }

        /// <summary>所属城市</summary>
        public AdministrativeRegion? City { get; set; }

        /// <summary>所属区县（若本身即为区县则为此项）</summary>
        public AdministrativeRegion? District { get; set; }

        /// <summary>完整层级描述（如：浙江省 / 杭州市 / 西湖区）</summary>
        public string FullName { get; set; } = string.Empty;

        /// <summary>用于下拉检索展示的友好文本</summary>
        public string DisplayName { get; set; } = string.Empty;

        public override string ToString() => DisplayName;
    }
}
