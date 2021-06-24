using FreeSql.DataAnnotations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MapDownloader.Models
{
    [Table(Name = "blocks")]
    public class PakBlock
    {
        [Column(Name = "z", IsPrimary = true)]
        public int Z { get; set; }
        [Column(Name = "x", IsPrimary = true)]
        public int X { get; set; }
        [Column(Name = "y", IsPrimary = true)]
        public int Y { get; set; }

        [Column(Name = "tile")]
        public byte[] Tile { get; set; }


        public static string GetTable(int z, int x, int y)
        {
            if (z < 10)
            {
                return "blocks";
            }
            var tx = x / 512;
            var ty = y / 512;
            return $"blocks_{z}_{tx}_{ty}";
        }
    }
}
