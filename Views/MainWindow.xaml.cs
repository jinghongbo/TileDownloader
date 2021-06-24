using System;
using System.Windows;
using System.Windows.Threading;

namespace MapDownloader.Views
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent(); 


        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            MapWindow window = new MapWindow();
            window.ShowDialog();
        }
    }
}
