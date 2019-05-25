using System;
using System.Threading;
using System.Windows;

namespace mtga_log_client
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {

    }

    public class Program {
        static Mutex mutex = new Mutex(true, "a77ab7c0fe4a4e1c8e84b50df6cfdf35");

        /// <summary>
        /// Application Entry Point.
        /// </summary>
        [System.STAThreadAttribute()]
        public static void Main()
        {
            if (mutex.WaitOne(TimeSpan.Zero, true))
            {
                App app = new App();
                app.InitializeComponent();
                app.Run();
            }
            else
            {
                MessageBox.Show("Another instance of 17Lands is already running. Try checking the system tray for the other.");
            }
        }
    }
}
