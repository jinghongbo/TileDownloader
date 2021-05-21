using BruTile;
using BruTile.Cache;
using System;
using System.Collections.Generic;
using System.Text;

namespace TileDownloader.Models
{
    public class DownloadSource
    {
        public DownloadSource(ITileSchema tileSchema, string urlFormatter, IEnumerable<string> serverNodes = null, string apiKey = null, string name = null, IPersistentCache<byte[]> persistentCache = null, Func<Uri, byte[]> tileFetcher = null, Attribution attribution = null, string userAgent = null)
        {
            TileSchema = tileSchema;
            UrlFormatter = urlFormatter;
            ServerNodes = serverNodes;
            ApiKey = apiKey;
            Name = name;
            PersistentCache = persistentCache;
            TileFetcher = tileFetcher;
            Attribution = attribution;
            UserAgent = userAgent;
        }

        public DownloadSource()
        {

        }
        public ITileSchema TileSchema { get; set; }
        public string UrlFormatter { get; set; }
        public IEnumerable<string> ServerNodes { get; set; }
        public string ApiKey { get; set; }
        public string Name { get; }
        public IPersistentCache<byte[]> PersistentCache { get; set; }

        public Func<Uri, byte[]> TileFetcher { get; set; }

        public Attribution Attribution { get; set; }
        public string UserAgent { get; set; }
    }
}
