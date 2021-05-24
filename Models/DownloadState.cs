using Prism.Mvvm;

namespace TileDownloader.Models
{
    public class DownloadState : BindableBase
    {
        private int level;
        private long total;
        private long success;
        private long fail;

        private double progress;
        private double speed;
        private string message;

        public int Level
        {
            get => level;
            set => SetProperty(ref level, value);
        }

        public long Total
        {
            get => total;
            set => SetProperty(ref total, value);
        }

        public long Success
        {
            get => success;
            set => SetProperty(ref success, value);
        }

        public long Fail
        {
            get => fail;
            set => SetProperty(ref fail, value);
        }

        public double Progress
        {
            get => progress;
            set => SetProperty(ref progress, value);
        }

        public double Speed
        {
            get => speed;
            set => SetProperty(ref speed, value);
        }

        public string Message
        {
            get => message;
            set => SetProperty(ref message, value);
        }
    }
}