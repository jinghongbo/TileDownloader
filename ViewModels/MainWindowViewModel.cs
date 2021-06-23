using BruTile;
using BruTile.Predefined;
using BruTile.Web;
using FreeSql;
using Microsoft.Win32;
using Newtonsoft.Json;
using Prism.Commands;
using Prism.Mvvm;
using ProjNet;
using ProjNet.CoordinateSystems;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using TileDownloader.Converters;
using TileDownloader.Models;

namespace TileDownloader.ViewModels
{
    public class MainWindowViewModel : BindableBase
    {
        private bool _downloading;
        private DelegateCommand _browseCmd;

        private DelegateCommand _downloadCmd;

        private ObservableCollection<DownloadTask> _status;
        private string _path;

        private double _progress;

        private DownloadSource _source;

        public string Title { get; set; } = "Tile Downloader";

        public MainWindowViewModel()
        {
            Sources = JsonConvert.DeserializeObject<List<DownloadSource>>(File.ReadAllText("Sources.json"), new DownloadSourceConverter());
        }

        public string Path
        {
            get => _path;
            set
            {
                SetProperty(ref _path, value);
                DownloadCmd.RaiseCanExecuteChanged();
            }
        }


        private string _message = "准备下载";

        public string Message
        {
            get { return _message; }
            set { SetProperty(ref _message, value); }
        }


        public List<DownloadSource> Sources { get; set; }

        public DownloadSource Source
        {
            get => _source;
            set
            {
                SetProperty(ref _source, value);
                Arguments = Argument.GetArguments(Source);
                DownloadCmd.RaiseCanExecuteChanged();
            }
        }
        private List<Argument> _arguments;
        public List<Argument> Arguments
        {
            get { return _arguments; }
            set { SetProperty(ref _arguments, value); }
        }
        public ObservableCollection<DownloadTask> Tasks
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        public double Progress
        {
            get => _progress;
            set => SetProperty(ref _progress, value);
        }

        public DelegateCommand DownloadCmd =>
            _downloadCmd ??= new DelegateCommand(ExecuteDownload);

        public DelegateCommand BrowseCmd =>
            _browseCmd ??= new DelegateCommand(ExecuteBrowse);

        public bool Downloading
        {
            get => _downloading; set
            {
                SetProperty(ref _downloading, value);
                DownloadCmd.RaiseCanExecuteChanged();
            }
        }

        public void ExecuteDownload()
        {
            try
            {
                Task.Run(async () =>
               {
                   try
                   {

                       Tasks = new ObservableCollection<DownloadTask>();

                       Downloading = true;
                       Message = "下载中"; 
                       
                     
                           await Source.DownloadAsync(Tasks); 
                   
                       Downloading = false;
                       Message = "下载完成";
                   }
                   catch (Exception e)
                   {
                   }
               });

            }
            catch (Exception e)
            {
                Message = "异常:" + e.Message;
            }
        }

        private void ExecuteBrowse()
        {
            var dialog = new SaveFileDialog { DefaultExt = ".pak", Filter = "PAK|*.pak" };
            if (dialog.ShowDialog() == true) Path = dialog.FileName;
        }
    }
}