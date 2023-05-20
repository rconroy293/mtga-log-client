using System;
using System.Windows;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.ComponentModel;
using System.Windows.Controls;
using Microsoft.Win32;
using System.Reflection;
using log4net;
using log4net.Core;
using System.Deployment.Application;
using System.Threading.Tasks;

namespace mtga_log_client
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private static readonly TimeSpan UPDATE_CHECK_INTERVAL = TimeSpan.FromHours(6);


        private static readonly HashSet<String> REQUIRED_FILENAMES = new HashSet<string> { "Player.log", "Player-prev.log" };
        private static readonly string STARTUP_REGISTRY_CUSTOM_KEY = "17LandsMTGAClient";
        private static readonly string STARTUP_REGISTRY_LOCATION = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private static readonly string STARTUP_FILENAME = @"\17Lands.com\17Lands MTGA Client.appref-ms";
        private static readonly string DOWNLOAD_URL = "https://github.com/rconroy293/mtga-log-client";
        private static readonly int MESSAGE_HISTORY = 150;

        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private LogParser parser;
        private ApiClient client;
        BackgroundWorker worker;

        private bool isStarted = false;
        private string filePath;
        private string userToken;
        private bool runAtStartup;
        private bool minimizeAtStartup;
        private bool exitingFromTray = false;

        public MainWindow()
        {
            InitializeComponent();

            log4net.Config.XmlConfigurator.Configure();
            log.Info("        =============  Started Logging  =============        ");

            LoadSettings();
            UpdateStartupRegistryKey();
            SetupTrayMinimization();
            StartUpdateCheckThread();

            client = new ApiClient(LogMessage);

            if (!ValidateClientVersion()) return;

            if (ValidateUserInputs(false))
            {
                StartParser();
            }
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);

            if (minimizeAtStartup)
            {
                this.Hide();
            }

            if (!isStarted)
            {
                MessageBox.Show(
                    "Welcome to the 17Lands MTGA client. Please input your user token then click 'Start Parsing' to begin.",
                    "Welcome",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (exitingFromTray)
            {
                base.OnClosing(e);
                return;
            }

            if (Properties.Settings.Default.do_not_ask_on_close)
            {
                if (Properties.Settings.Default.minimize_on_close)
                {
                    e.Cancel = true;
                    this.Hide();
                }
                else
                {
                    base.OnClosing(e);
                }
                return;
            }

            ExitConfirmation dialog = new ExitConfirmation();
            dialog.ShowDialog();

            switch (dialog.GetExitState())
            {
                case ExitConfirmation.ExitState.EXIT:
                    Properties.Settings.Default.do_not_ask_on_close = dialog.GetRemember();
                    Properties.Settings.Default.minimize_on_close = false;
                    Properties.Settings.Default.Save();
                    base.OnClosing(e);
                    break;
                case ExitConfirmation.ExitState.MINIMIZE:
                    Properties.Settings.Default.do_not_ask_on_close = dialog.GetRemember();
                    Properties.Settings.Default.minimize_on_close = true;
                    Properties.Settings.Default.Save();
                    e.Cancel = true;
                    this.Hide();
                    break;
                case ExitConfirmation.ExitState.CANCEL:
                    Properties.Settings.Default.Save();
                    e.Cancel = true;
                    break;
            }
        }

        public void SetupTrayMinimization()
        {
            InitializeComponent();

            System.Windows.Forms.NotifyIcon ni = new System.Windows.Forms.NotifyIcon();

            System.Windows.Forms.MenuItem trayMenuClearPreferences = new System.Windows.Forms.MenuItem();
            trayMenuClearPreferences.Text = "C&lear Preferences";
            trayMenuClearPreferences.Click += new EventHandler(this.ClearPreferences);

            System.Windows.Forms.MenuItem trayMenuShow = new System.Windows.Forms.MenuItem();
            trayMenuShow.Text = "S&how";
            trayMenuShow.Click += new EventHandler(this.ShowClient);

            System.Windows.Forms.MenuItem trayMenuExit = new System.Windows.Forms.MenuItem();
            trayMenuExit.Text = "E&xit";
            trayMenuExit.Click += new EventHandler(this.ExitClient);

            System.Windows.Forms.ContextMenu trayMenu = new System.Windows.Forms.ContextMenu();
            trayMenu.MenuItems.AddRange(new System.Windows.Forms.MenuItem[] { trayMenuClearPreferences, trayMenuShow, trayMenuExit });

            ni.Icon = Properties.Resources.icon_white;
            ni.Visible = true;
            ni.DoubleClick += new EventHandler(this.ShowClient);
            ni.ContextMenu = trayMenu;
        }

        private void ShowClient(object Sender, EventArgs e)
        {
            this.Show();
            this.WindowState = WindowState.Normal;
        }

        private void ExitClient(object Sender, EventArgs e)
        {
            exitingFromTray = true;
            base.Close();
        }

        private void ClearPreferences(object Sender, EventArgs e)
        {
            Properties.Settings.Default.do_not_ask_on_close = false;

            Properties.Settings.Default.minimized_at_startup = false;
            StartMinimizedCheckbox.IsChecked = false;

            Properties.Settings.Default.Save();
        }

        protected override void OnStateChanged(EventArgs e)
        {
            if (WindowState == WindowState.Minimized) this.Hide();
            base.OnStateChanged(e);
        }

        private void LoadSettings()
        {
            userToken = Properties.Settings.Default.client_token;
            filePath = Properties.Settings.Default.mtga_log_filename;
            runAtStartup = Properties.Settings.Default.run_at_startup;
            minimizeAtStartup = Properties.Settings.Default.minimized_at_startup;

            filePath = MaybeSwitchLogFile(filePath);

            StartMinimizedCheckbox.IsChecked = minimizeAtStartup;
            RunAtStartupCheckbox.IsChecked = runAtStartup;
            LogFileTextBox.Text = filePath;
            ClientTokenTextBox.Text = ObfuscateToken(userToken);
        }

        private string MaybeSwitchLogFile(string filePath)
        {
            if (filePath == null || filePath.Length == 0)
            {
                filePath = Path.Combine(
                    Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.Personal)),
                    "AppData",
                    "LocalLow",
                    "Wizards of the Coast",
                    "MTGA",
                    "Player.log");
            }
            else if (filePath.EndsWith(@"\output_log.txt"))
            {
                filePath = filePath.Replace(@"\output_log.txt", @"\Player.log");
                Properties.Settings.Default.mtga_log_filename = filePath;
                Properties.Settings.Default.Save();

                MessageBox.Show(
                    String.Format("Arena updated the output log file name. 17Lands is now tracking {0}.", filePath),
                    "17Lands Update",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            return filePath;
        }

        private void SaveSettings()
        {
            Properties.Settings.Default.client_token = userToken;
            Properties.Settings.Default.mtga_log_filename = filePath;
            Properties.Settings.Default.run_at_startup = runAtStartup;
            Properties.Settings.Default.minimized_at_startup = minimizeAtStartup;
            Properties.Settings.Default.Save();
        }

        private bool ValidateClientVersion()
        {
            var versionValidation = client.GetVersionValidation();
            if (versionValidation.is_supported)
            {
                return true;
            }

            MessageBox.Show(
                "This version of the 17Lands client is no longer supported. Please update.",
                "Outdated 17Lands Client",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            System.Diagnostics.Process.Start(DOWNLOAD_URL);
            Application.Current.Shutdown();
            return false;
        }

        private void SetStatusButtonText(string status)
        {
            Application.Current.Dispatcher.Invoke((Action)delegate
            {
                StartButton.Content = status;
            });
        }

        private void StartParser()
        {
            if (worker != null && !worker.CancellationPending)
            {
                worker.CancelAsync();
            }
            isStarted = true;
            StartButton.IsEnabled = false;
            StartButton.Content = "Catching Up";

            parser = new LogParser(client, userToken, filePath, LogMessage, SetStatusButtonText);

            worker = new BackgroundWorker();
            worker.DoWork += parser.ResumeParsing;
            worker.WorkerSupportsCancellation = true;
            worker.RunWorkerAsync();
        }

        private void StopParser()
        {
            if (!isStarted) return;
            LogMessage("Stopped parsing.", Level.Info);

            if (worker != null && !worker.CancellationPending)
            {
                worker.CancelAsync();
            }
            StartButton.IsEnabled = true;
            StartButton.Content = "Start Parsing";
            isStarted = false;
        }

        private bool ValidateLogFileInput(bool promptForUpdate)
        {
            if (File.Exists(LogFileTextBox.Text) && IsValidLogFile(LogFileTextBox.Text)) return true;

            if (promptForUpdate)
            {
                if (!File.Exists(LogFileTextBox.Text))
                {
                    MessageBox.Show(
                        "Your Arena Player.log file is not in a standard location. You may need to search for it.",
                        "Choose Filename",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                }
                else
                {
                    MessageBox.Show(
                        "You must choose a valid log file name from " + String.Join(", ", REQUIRED_FILENAMES),
                        "Choose Filename",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                filePath = ChooseLogFile();
                if (filePath != null)
                {
                    return true;
                }

                MessageBox.Show(
                    "You must enter a log file.",
                    "Choose Valid Log File",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            return false;
        }

        private bool ValidateTokenInput(bool promptForUpdate)
        {
            if (IsValidToken(userToken)) return true;

            if (promptForUpdate)
            {
                MessageBox.Show(
                    "You must enter a valid token from 17lands.com",
                    "Enter Valid Token",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            return false;
        }

        private bool ValidateUserInputs(bool promptForUpdate)
        {
            if (!ValidateLogFileInput(promptForUpdate)) return false;
            filePath = LogFileTextBox.Text;

            if (!ValidateTokenInput(promptForUpdate)) return false;

            return true;
        }

        private void RunAtStartupCheckbox_onClick(object sender, EventArgs e)
        {
            runAtStartup = RunAtStartupCheckbox.IsChecked.GetValueOrDefault(false);
            SaveSettings();
            UpdateStartupRegistryKey();
        }

        private void StartMinimizedCheckbox_onClick(object sender, EventArgs e)
        {
            minimizeAtStartup = StartMinimizedCheckbox.IsChecked.GetValueOrDefault(false);
            SaveSettings();
        }

        private void UpdateStartupRegistryKey()
        {
            var startupPath = Environment.GetFolderPath(Environment.SpecialFolder.Programs) + STARTUP_FILENAME;
            if (runAtStartup)
            {
                RegistryKey key = Registry.CurrentUser.OpenSubKey(STARTUP_REGISTRY_LOCATION, true);
                key.SetValue(STARTUP_REGISTRY_CUSTOM_KEY, startupPath);
            }
            else
            {
                RegistryKey key = Registry.CurrentUser.OpenSubKey(STARTUP_REGISTRY_LOCATION, true);
                key.DeleteValue(STARTUP_REGISTRY_CUSTOM_KEY, false);
            }
        }

        private void ClientTokenTextBox_onKeyDown(object sender, EventArgs e)
        {
            StopParser();
        }

        private void ClientTokenTextBox_onGotFocus(object sender, EventArgs e)
        {
            ClientTokenTextBox.Text = userToken;
        }

        private void ClientTokenTextBox_onLostFocus(object sender, EventArgs e)
        {
            userToken = ClientTokenTextBox.Text;
            ClientTokenTextBox.Text = ObfuscateToken(userToken);
        }

        private string ObfuscateToken(string token)
        {
            return new String('*', token.Length);
        }

        private bool IsValidToken(string clientToken)
        {
            var validationResponse = client.GetTokenValidation(clientToken);
            return validationResponse.is_valid;
        }

        private void ChooseFile_onClick(object sender, RoutedEventArgs e)
        {
            string newFilename = ChooseLogFile();
            if (newFilename != null)
            {
                LogFileTextBox.Text = newFilename;
                StopParser();
            }
        }

        private string ChooseLogFile()
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "Log files (*.log)|*.log";
            openFileDialog.InitialDirectory = Path.GetDirectoryName(filePath);

            if (openFileDialog.ShowDialog() == true)
            {
                if (IsValidLogFile(openFileDialog.FileName))
                {
                    LogFileTextBox.Text = openFileDialog.FileName;
                    return openFileDialog.FileName;
                }
                else
                {
                    MessageBox.Show(
                        "You must choose a file name from one of " + String.Join(", ", REQUIRED_FILENAMES),
                        "Bad Filename",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }

            return null;
        }

        private bool IsValidLogFile(string filename)
        {
            foreach (String possibleFilename in REQUIRED_FILENAMES)
            {
                if (filename.EndsWith("\\" + possibleFilename)) {
                    return true;
                }
            }
            return false;
        }

        private void ValidateInputsApplyAndStart()
        {
            if (!ValidateUserInputs(true)) return;
            SaveSettings();
            StartParser();
        }

        private void OpenUserPageInBrowser(object sender, EventArgs e)
        {
            System.Diagnostics.Process.Start("http://www.17lands.com/user");
        }

        private void OpenAccountPageInBrowser(object sender, EventArgs e)
        {
            System.Diagnostics.Process.Start("http://www.17lands.com/account");
        }

        private void StartButton_onClick(object sender, EventArgs e)
        {
            if (!isStarted)
            {
                ValidateInputsApplyAndStart();
            }
        }

        private void LogMessage(string message, Level logLevel)
        {
            log.Logger.Log(null, logLevel, message, null);

            if (logLevel >= Level.Info)
            {
                Application.Current.Dispatcher.Invoke((Action)delegate {
                    var item = new ListBoxItem();
                    item.Content = message;
                    MessageListBox.Items.Insert(0, item);

                    while (MessageListBox.Items.Count > MESSAGE_HISTORY)
                    {
                        MessageListBox.Items.RemoveAt(MESSAGE_HISTORY);
                    }
                });
            }
        }

        private static async Task RunPeriodicAsync(Action onTick, TimeSpan initialWait, TimeSpan interval, CancellationToken token)
        {
            if (initialWait > TimeSpan.Zero)
                await Task.Delay(initialWait, token);

            while (!token.IsCancellationRequested)
            {
                onTick?.Invoke();
                if (interval > TimeSpan.Zero) await Task.Delay(interval, token);
            }
        }

        protected void StartUpdateCheckThread()
        {
            _ = RunPeriodicAsync(InstallUpdateSyncWithInfo, UPDATE_CHECK_INTERVAL, UPDATE_CHECK_INTERVAL, CancellationToken.None);
        }

        private void InstallUpdateSyncWithInfo()
        {
            LogMessage("Checking for updates", Level.Info);
            if (!ApplicationDeployment.IsNetworkDeployed)
            {
                LogMessage("Not network deployed", Level.Info);
                return;
            }

            ApplicationDeployment ad = ApplicationDeployment.CurrentDeployment;
            UpdateCheckInfo info;
            try
            {
                info = ad.CheckForDetailedUpdate();
            }
            catch (Exception e)
            {
                LogMessage(String.Format("Error {0} while checking for updates", e), Level.Error);
                return;
            }

            if (!info.UpdateAvailable)
            {
                LogMessage("No update available", Level.Info);
                return;
            }

            if (!info.IsUpdateRequired)
            {
                LogMessage("An optional update is available. Please restart the 17Lands client if you wish to apply this update.", Level.Info);
                return;
            }

            MessageBox.Show(
                "17Lands has detected a mandatory update from your current version to version " +
                info.MinimumRequiredVersion.ToString() + ". The application will now install the update and restart.",
                "Update Required",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            try
            {
                ad.Update();
                MessageBox.Show("17Lands has been upgraded and will now restart.");
                System.Windows.Forms.Application.Restart();
                Application.Current.Shutdown();
            }
            catch (DeploymentDownloadException e)
            {
                LogMessage(String.Format("Error {0} while applying updates", e), Level.Error);
                return;
            }
        }

    }

    delegate void LogMessageFunction(string message, Level logLevel);
    delegate void UpdateStatusFunction(string status);
}
