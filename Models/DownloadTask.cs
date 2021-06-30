using Prism.Mvvm;
using System;
using System.ComponentModel;
using System.Diagnostics;

namespace MapDownloader.Models
{
    public class DownloadTask : BindableBase
    {
        private string _name;
        private long _completed;
        private long _total;
        private double _progress;
        private string _error;

        public string Name { get => _name; set => SetProperty(ref _name, value); }
        public long Total
        {
            get => _total; set
            {
                SetProperty(ref _total, value);
                Progress = Completed * 100d / Total;
            }
        }
        public long Completed
        {
            get => _completed; set
            {
                SetProperty(ref _completed, value);
                Progress = Completed * 100d / Total;
            }

        }
        public double Progress { get => _progress; set => SetProperty(ref _progress, value); }

        public string Error { get => _error; set => SetProperty(ref _error, value); }


        public int Level { get; set; }

        public string DataId { get; set; }

    }
}