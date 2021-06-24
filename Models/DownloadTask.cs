using Prism.Mvvm;
using System;
using System.ComponentModel;
using System.Diagnostics;

namespace TileDownloader.Models
{
    public class DownloadTask : BindableBase
    {
        private long _success;
        private double _progress;
        private string _errorMessage;
        private TimeSpan _timeLeft;
        private string _name;
        private long _total;
        private long _fail;
        private DateTime _startTime;
        public string Name { get => _name; set => SetProperty(ref _name, value); }
        public long Total { get => _total; set => SetProperty(ref _total, value); }
        public long Success { get => _success; set => SetProperty(ref _success, value); }
        public long Fail { get => _fail; set => SetProperty(ref _fail, value); }
        public double Progress { get => _progress; set => SetProperty(ref _progress, value); }

        public string ErrorMessage { get => _errorMessage; set => SetProperty(ref _errorMessage, value); }
        public DateTime StartTime { get => _startTime; set => SetProperty(ref _startTime, value); }
        public TimeSpan TimeLeft { get => _timeLeft; set => SetProperty(ref _timeLeft, value); }


        public int Level { get; set; }
        public BruTile.Extent Extent { get; set; }

        public string ProductId { get; set; }
        public string DataId { get; set; }

    }
}