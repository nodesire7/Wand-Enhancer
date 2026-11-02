using System;
using System.Threading.Tasks;
using System.Windows;
using WandEnhancer.Core;
using WandEnhancer.Core.Services;
using WandEnhancer.View.MainWindow;
using MessageBox = System.Windows.Forms.MessageBox;

namespace WandEnhancer
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            LocalizationManager.Initialize();
            // GBTGAME.ME fork: applies the fork build information without touching upstream XAML.
            WandEnhancer.Branding.BrandingUi.Apply(this.MainWindow);
            this.MainWindow.Show();
        }

        public new static void Shutdown()
        {
            Current.Dispatcher.Invoke(() => Current.Shutdown());
        }
    }
}