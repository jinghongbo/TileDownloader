using Newtonsoft.Json;
using Prism.Commands;
using Prism.Mvvm;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using TileDownloader.Converters;
using TileDownloader.Models;

namespace TileDownloader.ViewModels
{
    public class MainWindowViewModel : BindableBase
    {
        private DelegateCommand _downloadCmd;

        private List<DownloadTask> _downloadTasks;

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

        public List<DownloadTask> DownloadTasks
        {
            get => _downloadTasks;
            set => SetProperty(ref _downloadTasks, value);
        }

        public DelegateCommand DownloadCmd =>
            _downloadCmd ??= new DelegateCommand(ExecuteDownload);

        public async void ExecuteDownload()
        {
            DownloadTasks = await Source.GetDownloadTasksAsync();
            await Source.DownloadAsync(DownloadTasks);
        }
    }
}