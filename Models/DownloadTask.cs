using Prism.Mvvm;
using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Timers;

namespace TileDownloader.Models
{
    public class DownloadTask : BindableBase
    { 

        public long Total { get; set; }
        public long Success { get; set; }
        public long Fail { get; set; }
        public double Progress { get; set; }

        public double Speed { get; set; }
        public string Message { get; set; }
        public string Name { get; set; }
        public DateTime StartTime { get; set; }

        public TimeSpan TimeLeft { get; set; }
    }
}