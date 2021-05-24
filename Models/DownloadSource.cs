using BruTile;
using BruTile.Cache;
using System;
using System.Collections.Generic;
using System.Text;

namespace TileDownloader.Models
{
    public class DownloadSource
    {
        public string TileSchema { get; set; }
        public string UrlFormatter { get; set; }
        public string[] ServerNodes { get; set; }
        public string ApiKey { get; set; }
        public string Name { get; set; }
        public string UserAgent { get; set; }
    }
}
