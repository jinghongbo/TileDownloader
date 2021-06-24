using Newtonsoft.Json;
using Prism.Commands;
using Prism.Mvvm;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using MapDownloader.Converters;
using MapDownloader.Models;

namespace MapDownloader.ViewModels
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

        private bool _downloading;
        public bool Downloading
        {
            get { return _downloading; }
            set
            {
                SetProperty(ref _downloading, value);
                DownloadCmd.RaiseCanExecuteChanged();
            }
        }

        public DelegateCommand DownloadCmd =>
            _downloadCmd ??= new DelegateCommand(ExecuteDownload, () => !Downloading);

        public async void ExecuteDownload()
        {
            try
            {
                Downloading = true;
                DownloadTasks = await Source.GetDownloadTasksAsync();
                await Source.DownloadAsync(DownloadTasks);
                System.Windows.MessageBox.Show("下载完成");
            }
            catch (System.Exception e)
            {
                System.Windows.MessageBox.Show((e.InnerException ?? e).Message);
            }
            finally
            {

                Downloading = false;
            }
        }
    }
}