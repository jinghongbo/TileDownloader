using FreeSql.DataAnnotations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TileDownloader.Models
{
    [Table(Name = "infos")]
    public class PakInfo
    {
        [Column(Name = "minx", IsPrimary = true)]
        public double MinX { get; set; }
        [Column(Name = "maxx", IsPrimary = true)]
        public double MaxX { get; set; }
        [Column(Name = "miny", IsPrimary = true)]
        public double MinY { get; set; }
        [Column(Name = "maxy", IsPrimary = true)]
        public double MaxY { get; set; }
        [Column(Name = "minlevel", IsPrimary = true)]
        public int MinLevel { get; set; }
        [Column(Name = "maxlevel", IsPrimary = true)]
        public int MaxLevel { get; set; }
        [Column(Name = "source")]
        public string Source { get; set; }
        [Column(Name = "type")]
        public string Type { get; set; }
        [Column(Name = "cur_level")]
        public int CurLevel { get; set; }
        [Column(Name = "cur_x")]
        public int CurX { get; set; }
        [Column(Name = "cur_y")]
        public int CurY { get; set; }
    }
}
