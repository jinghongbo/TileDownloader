using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TileDownloader.Attributes;

namespace TileDownloader.Models
{
    public class GSCloudDownloadSource : DownloadSource
    {
        [Argument("产品编号")]
        public string ProductId { get; set; }
    }
}
