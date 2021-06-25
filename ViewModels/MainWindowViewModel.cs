using MapDownloader.Converters;
using MapDownloader.Models;
using Microsoft.Win32;
using Newtonsoft.Json;
using Prism.Commands;
using Prism.Mvvm;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MapDownloader.ViewModels
{
    public class MainWindowViewModel : BindableBase
    {
        private DelegateCommand _downloadCmd;

        private ObservableCollection<DownloadTask> _tasks;

        private DownloadSource _source;

        public MainWindowViewModel()
        {
            Sources = JsonConvert.DeserializeObject<List<DownloadSource>>(File.ReadAllText("Sources.json"), new DownloadSourceConverter());
        }

        public List<DownloadSource> Sources { get; set; }

        public DownloadSource Source
        {
            get => _source;
            set
            {
                SetProperty(ref _source, value);
                Arguments = DownloadArgument.GetArguments(Source);
                DownloadCmd.RaiseCanExecuteChanged();
            }
        }

        private List<DownloadArgument> _arguments;

        public List<DownloadArgument> Arguments
        {
            get { return _arguments; }
            set { SetProperty(ref _arguments, value); }
        }

        public ObservableCollection<DownloadTask> Tasks
        {
            get => _tasks;
            set => SetProperty(ref _tasks, value);
        }

        private double _progress;

        public double Progress
        {
            get { return _progress; }
            set { SetProperty(ref _progress, value); }
        }

        private string _path;

        public string Path
        {
            get { return _path; }
            set
            {
                SetProperty(ref _path, value);
                DownloadCmd.RaiseCanExecuteChanged();
            }
        }

        private int _concurrent = 4;

        public int Concurrent
        {
            get { return _concurrent; }
            set { SetProperty(ref _concurrent, value); }
        }

        private int _retry = 4;

        public int Retry
        {
            get { return _retry; }
            set { SetProperty(ref _retry, value); }
        }

        private string _range = "POLYGON ((103.88 30.81, 103.88 30.56, 104.23 30.56, 104.23 30.81, 103.88 30.81))";

        public string Range
        {
            get { return _range; }
            set { SetProperty(ref _range, value); }
        }

        private string _message;

        public string Message
        {
            get { return _message; }
            set { SetProperty(ref _message, value); }
        }

        private bool _downloading;

        public bool Downloading
        {
            get { return _downloading; }
            set
            {
                SetProperty(ref _downloading, value);
                DownloadCmd.RaiseCanExecuteChanged();
                CancelCmd.RaiseCanExecuteChanged();
            }
        }

        public DelegateCommand DownloadCmd =>
            _downloadCmd ??= new DelegateCommand(ExecuteDownload, () => !Downloading && !string.IsNullOrEmpty(Path));

        public async void ExecuteDownload()
        {
            try
            {
                CancellationTokenSource = new CancellationTokenSource();
                DownloadTask = Task.Run(async () =>
               {
                   var startTime = DateTime.Now;
                   while (Progress < 100 && !CancellationTokenSource.IsCancellationRequested && Downloading)
                   {
                       var used = DateTime.Now - startTime;
                       var left = used / ((100 - Progress) / 100);
                       Message = $"完成进度:{Progress:F}%,剩余时间:{left},已用时间:{used}";
                       await Task.Delay(100);
                   }
               }, CancellationTokenSource.Token);
                Downloading = true;
                await Source.DownloadAsync(this);
                await DownloadTask;
                Message = "下载完成";
            }
            catch (System.Exception e)
            {
                Message = (e.InnerException ?? e).Message;
            }
            finally
            {
                Downloading = false;
            }
        }

        private DelegateCommand _cancelCmd;

        public DelegateCommand CancelCmd =>
            _cancelCmd ?? (_cancelCmd = new DelegateCommand(ExecuteCancel, () => Downloading));

        private CancellationTokenSource _cancellationTokenSource;

        private void ExecuteCancel()
        {
            CancellationTokenSource.Cancel();
        }

        private DelegateCommand _browseCmd;

        public DelegateCommand BrowseCmd =>
            _browseCmd ?? (_browseCmd = new DelegateCommand(ExecuteBrowse));

        public CancellationTokenSource CancellationTokenSource { get => _cancellationTokenSource; set => _cancellationTokenSource = value; }

        private void ExecuteBrowse()
        {
            if (Source.Type == "Tile")
            {
                var dialog = new SaveFileDialog();
                dialog.Filter = "pak|*.pak";

                if (dialog.ShowDialog() == true)
                {
                    Path = dialog.FileName;
                }
            }
            else if (Source.Type == "GSCloud")
            {
                var dialog = new SaveFileDialog();
                dialog.FileName = "[当前目录]";
                dialog.Filter = "目录|dir";
                if (dialog.ShowDialog() == true)
                {
                    Path = System.IO.Path.GetDirectoryName(dialog.FileName);
                }
            }
        }

        public Task DownloadTask { get; set; }

        //public void UpdateMessageByTime(DateTime startTime)
        //{
        //    var used = DateTime.Now - startTime;
        //    var left = used / (Progress / 100);
        //    var message = $"{left}/{used}";
        //    Message = message;
        //    //if (timeSpan.Days > 1)
        //    //{
        //    //    message += $"{timeSpan.Days:#}天";
        //    //}
        //    //if (timeSpan.Hours > 1)
        //    //{
        //    //    message += $"{timeSpan.Hours:#}时";
        //    //}
        //    //if (timeSpan.Minutes > 1)
        //    //{
        //    //    message += $"{timeSpan.Minutes:#}分";
        //    //}
        //    //if (timeSpan.Seconds > 1)
        //    //{
        //    //    message += $"{timeSpan.Seconds:#}秒";
        //    //}
        //    //message += "完成";
        //}
    }
}