using System;
using System.Windows;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System.Threading;
using System.ComponentModel;
using System.Windows.Controls;
using Microsoft.Win32;
using System.Reflection;
using log4net;
using log4net.Core;
using System.Deployment.Application;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Linq;
using System.IO.Compression;

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
        private bool gameHistoryEnabled;
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

            Properties.Settings.Default.game_history_enabled = true;
            gameHistoryEnabled = true;
            GameHistoryCheckbox.IsChecked = true;
            if (parser != null)
            {
                parser.SetGameHistoryEnabled(true);
            }

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
            gameHistoryEnabled = Properties.Settings.Default.game_history_enabled;

            filePath = MaybeSwitchLogFile(filePath);

            StartMinimizedCheckbox.IsChecked = minimizeAtStartup;
            GameHistoryCheckbox.IsChecked = gameHistoryEnabled;
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
            Properties.Settings.Default.game_history_enabled = gameHistoryEnabled;
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

            parser = new LogParser(client, userToken, filePath, gameHistoryEnabled, LogMessage, SetStatusButtonText);

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

        private void GameHistoryCheckbox_onClick(object sender, EventArgs e)
        {
            gameHistoryEnabled = GameHistoryCheckbox.IsChecked.GetValueOrDefault(false);
            if (parser != null)
            {
                parser.SetGameHistoryEnabled(gameHistoryEnabled);
            }
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

    class LogParser
    {
        public const string CLIENT_VERSION = "0.1.36.w";
        public const string CLIENT_TYPE = "windows";

        private const int SLEEP_TIME = 750;
        private const int BUFFER_SIZE = 65536;
        private static readonly Regex LOG_START_REGEX = new Regex(
            "^\\[(UnityCrossThreadLogger|Client GRE)\\]([\\d/.-]+[ T][\\d]+:[\\d]+:[\\d]+( AM| PM)?)");
        private static readonly Regex TIMESTAMP_REGEX = new Regex(
            "^([\\d/.-]+[ T][\\d]+:[\\d]+:[\\d]+( AM| PM)?)");
        private static readonly Regex LOG_START_REGEX_UNTIMED = new Regex(
            "^\\[(UnityCrossThreadLogger|Client GRE)\\]");
        private static readonly Regex LOG_START_REGEX_UNTIMED_2 = new Regex(
            "^\\(Filename:");
        private static readonly Regex JSON_DICT_REGEX = new Regex("\\{.+\\}");
        private static readonly Regex JSON_LIST_REGEX = new Regex("\\[.+\\]");

        private static readonly Regex ACCOUNT_INFO_REGEX = new Regex(
            ".*Updated account\\. DisplayName:(.{1,1000}), AccountID:(.{1,1000}), Token:.*");
        private static readonly Regex MATCH_ACCOUNT_INFO_REGEX = new Regex(
            ".*: ((\\w+) to Match|Match to (\\w+)):");

        private static readonly HashSet<String> GAME_HISTORY_MESSAGE_TYPES = new HashSet<string> {
            "GREMessageType_GameStateMessage",
            "GREMessageType_QueuedGameStateMessage"
        };

        private static readonly List<string> TIME_FORMATS = new List<string>() {
            "yyyy-MM-dd h:mm:ss tt",
            "yyyy-MM-dd HH:mm:ss",
            "M/d/yyyy h:mm:ss tt",
            "M/d/yyyy HH:mm:ss",
            "yyyy/MM/dd h:mm:ss tt",
            "yyyy/MM/dd HH:mm:ss",
            "dd/MM/yyyy HH:mm:ss",
            "dd.MM.yyyy HH:mm:ss"
        };

        private static readonly List<String> SUB_PAYLOAD_KEYS = new List<String>()
        {
            "payload",
            "Payload",
            "request"
        };

        private static readonly List<String> INVENTORY_KEYS = new List<String>()
        {
            "Gems",
            "Gold",
            "TotalVaultProgress",
            "wcTrackPosition",
            "WildCardCommons",
            "WildCardUnCommons",
            "WildCardRares",
            "WildCardMythics",
            "DraftTokens",
            "SealedTokens",
            "Boosters"
        };

        private static long SECONDS_AT_YEAR_2000 = 63082281600L;
        private static DateTime YEAR_2000 = new DateTime(2000, 1, 1);

        private bool first = true;
        private long farthestReadPosition = 0;
        private List<string> buffer = new List<string>();
        private Nullable<DateTime> currentLogTime = new DateTime(0);
        private Nullable<DateTime> lastUtcTime = new DateTime(0);
        private string lastRawTime = "";
        private string disconnectedUser = null;
        private string disconnectedScreenName = null;
        private string currentUser = null;
        private string currentScreenName = null;
        private string currentDraftEvent = null;
        private string currentConstructedLevel = null;
        private string currentLimitedLevel = null;
        private string currentOpponentLevel = null;
        private string currentOpponentMatchId = null;
        private string currentMatchId = null;
        private string currentEventName = null;
        private int startingTeamId = -1;
        private int seatId = 0;
        private int turnCount = 0;
        private readonly Dictionary<int, string> screenNames = new Dictionary<int, string>();
        private readonly Dictionary<int, Dictionary<int, int>> objectsByOwner = new Dictionary<int, Dictionary<int, int>>();
        private readonly Dictionary<int, List<int>> cardsInHand = new Dictionary<int, List<int>>();
        private readonly Dictionary<int, List<List<int>>> drawnHands = new Dictionary<int, List<List<int>>>();
        private readonly Dictionary<int, Dictionary<int, int>> drawnCardsByInstanceId = new Dictionary<int, Dictionary<int, int>>();
        private readonly Dictionary<int, List<int>> openingHand = new Dictionary<int, List<int>>();
        private readonly List<JToken> gameHistoryEvents = new List<JToken>();

        private const int ERROR_LINES_RECENCY = 10;
        private LinkedList<string> recentLines = new LinkedList<string>();
        private string lastBlob = "";
        private string currentDebugBlob = "";

        private readonly ApiClient apiClient;
        private readonly string apiToken;
        private readonly string filePath;
        private bool gameHistoryEnabled;
        private readonly LogMessageFunction messageFunction;
        private readonly UpdateStatusFunction statusFunction;

        public LogParser(ApiClient apiClient, string apiToken, string filePath, bool gameHistoryEnabled, LogMessageFunction messageFunction, UpdateStatusFunction statusFunction)
        {
            this.apiClient = apiClient;
            this.apiToken = apiToken;
            this.filePath = filePath;
            this.gameHistoryEnabled = gameHistoryEnabled;
            this.messageFunction = messageFunction;
            this.statusFunction = statusFunction;
        }

        public void SetGameHistoryEnabled(bool flag)
        {
            gameHistoryEnabled = flag;
        }

        public void ResumeParsing(object sender, DoWorkEventArgs e)
        {
            LogMessage("Starting parsing of " + filePath, Level.Info);
            BackgroundWorker worker = sender as BackgroundWorker;

            while (!worker.CancellationPending)
            {
                ParseRemainderOfLog(worker);
                Thread.Sleep(SLEEP_TIME);
            }
        }

        public void ParseRemainderOfLog(BackgroundWorker worker) {
            try
            {
                using (FileStream filestream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BUFFER_SIZE))
                {
                    bool catchingUp = first || filestream.Length < farthestReadPosition;
                    if (catchingUp)
                    {
                        filestream.Position = 0;
                        farthestReadPosition = filestream.Length;
                    }
                    else if (filestream.Length >= farthestReadPosition)
                    {
                        filestream.Position = farthestReadPosition;
                        farthestReadPosition = filestream.Length;
                    }
                    first = false;

                    using (StreamReader reader = new StreamReader(filestream))
                    {
                        while (!worker.CancellationPending)
                        {
                            string line = line = reader.ReadLine();
                            if (line == null)
                            {
                                if (catchingUp)
                                {
                                    LogMessage("Initial parsing has caught up to the end of the log file. It will continue to monitor for any new updates from MTGA.", Level.Info);
                                    statusFunction("Monitoring");
                                }
                                break;
                            }
                            ProcessLine(line);
                        }
                    }
                }
            }
            catch (FileNotFoundException e)
            {
                LogMessage(String.Format("File not found error while parsing log. If this message persists, please email seventeenlands@gmail.com: {0}", e), Level.Warn);
            }
            catch (Exception e)
            {
                LogError(String.Format("Error parsing log: {0}", e), e.StackTrace, Level.Error);
            }
        }

        private DateTime ParseDateTime(string dateString)
        {
            if (dateString.EndsWith(":") || dateString.EndsWith(" "))
            {
                dateString = dateString.TrimEnd(':', ' ');
            }

            try
            {
                return DateTime.Parse(dateString);
            }
            catch (FormatException)
            {
                // pass
            }

            DateTime readDate;
            foreach (string format in TIME_FORMATS)
            {
                if (DateTime.TryParseExact(dateString, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out readDate))
                {
                    return readDate;
                }
            }
            return DateTime.Parse(dateString);
        }

        private void ProcessLine(string line)
        {
            if (recentLines.Count >= ERROR_LINES_RECENCY) recentLines.RemoveFirst();
            recentLines.AddLast(line);

            if (line.StartsWith("DETAILED LOGS: DISABLED"))
            {
                LogMessage("Warning! Detailed logs disabled in MTGA.", Level.Error);
                ShowMessageBoxAsync(
                    "17Lands needs detailed logging enabled in MTGA. To enable this, click the gear at the top right of MTGA, then 'View Account' (at the bottom), then check 'Detailed Logs', then restart MTGA.",
                    "MTGA Logging Disabled",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else if (line.StartsWith("DETAILED LOGS: ENABLED"))
            {
                LogMessage("Detailed logs enabled in MTGA", Level.Info);
            }

            MaybeHandleAccountInfo(line);

            var timestampMatch = TIMESTAMP_REGEX.Match(line);
            if (timestampMatch.Success)
            {
                lastRawTime = timestampMatch.Groups[1].Value;
                currentLogTime = ParseDateTime(lastRawTime);
            }

            var match = LOG_START_REGEX_UNTIMED.Match(line);
            var match2 = LOG_START_REGEX_UNTIMED_2.Match(line);
            if (match.Success || match2.Success)
            {
                HandleCompleteLogEntry();

                if (match.Success)
                {
                    buffer.Add(line.Substring(match.Length));
                }
                else
                {
                    buffer.Add(line.Substring(match2.Length));
                }

                var timedMatch = LOG_START_REGEX.Match(line);
                if (timedMatch.Success)
                {
                    lastRawTime = timedMatch.Groups[2].Value;
                    currentLogTime = ParseDateTime(lastRawTime);
                }
            }
            else
            {
                buffer.Add(line);
            }
        }

        private void HandleCompleteLogEntry()
        {
            if (buffer.Count == 0)
            {
                return;
            }
            if (!currentLogTime.HasValue)
            {
                buffer.Clear();
                return;
            }

            String fullLog;
            try
            {
                fullLog = String.Join("", buffer);
            }
            catch (OutOfMemoryException)
            {
                LogMessage("Ran out of memory trying to process the last blob. Arena may have been idle for too long. 17Lands should auto-recover.", Level.Warn);
                ClearGameData();
                ClearMatchData();
                buffer.Clear();
                return;
            }

            currentDebugBlob = fullLog;
            try
            {
                HandleBlob(fullLog);
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} while processing {1}", e, fullLog), e.StackTrace, Level.Error);
            }
            lastBlob = fullLog;

            buffer.Clear();
            // currentLogTime = null;
        }

        private void HandleBlob(string fullLog)
        {
            var dictMatch = JSON_DICT_REGEX.Match(fullLog);
            if (!dictMatch.Success)
            {
                return;
            }

            var listMatch = JSON_LIST_REGEX.Match(fullLog);
            if (listMatch.Success && listMatch.Value.Length > dictMatch.Value.Length && listMatch.Index < dictMatch.Index)
            {
                return;
            }

            var blob = ParseBlob(dictMatch.Value);
            blob = ExtractPayload(blob);
            if (blob == null) return;

            DateTime? maybeUtcTimestamp = MaybeGetUtcTimestamp(blob);
            if (maybeUtcTimestamp != null)
            {
                lastUtcTime = maybeUtcTimestamp;
            }

            if (MaybeHandleLogin(blob)) return;
            if (MaybeHandleJoinPod(fullLog, blob)) return;
            if (MaybeHandleBotDraftPack(blob)) return;
            if (MaybeHandleBotDraftPick(fullLog, blob)) return;
            if (MaybeHandleHumanDraftCombined(fullLog, blob)) return;
            if (MaybeHandleHumanDraftPack(fullLog, blob)) return;
            if (MaybeHandleDeckSubmission(fullLog, blob)) return;
            if (MaybeHandleOngoingEvents(fullLog, blob)) return;
            if (MaybeHandleClaimPrize(fullLog, blob)) return;
            // if (MaybeHandleEventCompletion(blob)) return;
            if (MaybeHandleEventCourse(fullLog, blob)) return;
            if (MaybeHandleScreenNameUpdate(fullLog, blob)) return;
            if (MaybeHandleMatchStateChanged(blob)) return;
            if (MaybeHandleGreToClientMessages(blob)) return;
            if (MaybeHandleClientToGreMessage(blob)) return;
            if (MaybeHandleClientToGreUiMessage(blob)) return;
            if (MaybeHandleSelfRankInfo(fullLog, blob)) return;
            // if (MaybeHandleMatchCreated(blob)) return;
            // if (MaybeHandleCollection(fullLog, blob)) return;
            if (MaybeHandleInventory(fullLog, blob)) return;
            if (MaybeHandlePlayerProgress(fullLog, blob)) return;
            // if (MaybeHandleDraftNotification(fullLog, blob)) return;
            if (MaybeHandleFrontDoorConnectionClose(fullLog, blob)) return;
            if (MaybeHandleReconnectResult(fullLog, blob)) return;
        }

        private JObject TryDecode(JObject blob, String key)
        {
            try
            {
                var subBlob = blob[key].Value<String>();
                if (subBlob == null)
                {
                    return null;
                }
                return ParseBlob(subBlob);
            }
            catch (Exception)
            {
                return blob[key].Value<JObject>();
            }
        }

        private JObject ExtractPayload(JObject blob)
        {
            if (blob == null || blob.ContainsKey("clientToMatchServiceMessageType"))
            {
                return blob;
            }

            foreach (String key in SUB_PAYLOAD_KEYS)
            {
                if (blob.ContainsKey(key))
                {
                    try
                    {
                        return ExtractPayload(TryDecode(blob, key));
                    }
                    catch (Exception)
                    {
                        // pass
                    }
                }
            }

            return blob;
        }

        private DateTime? MaybeGetUtcTimestamp(JObject blob)
        {
            String timestamp;
            if (blob.ContainsKey("timestamp"))
            {
                timestamp = blob["timestamp"].Value<String>();
            }
            else if (blob.ContainsKey("payloadObject") && blob.GetValue("payloadObject").Value<JObject>().ContainsKey("timestamp"))
            {
                timestamp = blob.GetValue("payloadObject").Value<JObject>().GetValue("timestamp").Value<String>();
            }
            else if (blob.ContainsKey("params") 
                && blob.GetValue("params").Value<JObject>().ContainsKey("payloadObject")
                && blob.GetValue("params").Value<JObject>().GetValue("payloadObject").Value<JObject>().ContainsKey("timestamp"))
            {
                timestamp = blob.GetValue("params").Value<JObject>().GetValue("payloadObject").Value<JObject>().GetValue("timestamp").Value<String>();
            }
            else
            {
                return null;
            }

            long secondsSinceYear2000;
            if (long.TryParse(timestamp, out secondsSinceYear2000))
            {
                secondsSinceYear2000 /= 10000000L;
                secondsSinceYear2000 -= SECONDS_AT_YEAR_2000;
                return YEAR_2000.AddSeconds(secondsSinceYear2000);
            }
            else
            {
                DateTime output;
                if (DateTime.TryParse(timestamp, out output))
                {
                    return output;
                }
                else
                {
                    return null;
                }
            }
        }

        private JObject ParseBlob(String blob)
        {
            JsonReaderException firstError = null;
            var endIndex = blob.Length - 1;
            while (true)
            {
                try
                {
                    return JObject.Parse(blob.Substring(0, endIndex + 1));
                }
                catch (JsonReaderException e)
                {
                    if (firstError == null)
                    {
                        firstError = e;
                    }

                    var nextIndex = blob.LastIndexOf("}", endIndex - 1);
                    if (nextIndex == endIndex)
                    {
                        LogError(String.Format("endIndex didn't change: {0}", endIndex), "", Level.Error);
                        throw e;
                    }
                    else if (nextIndex < 0)
                    {
                        throw firstError;
                    }
                    else
                    {
                        endIndex = nextIndex;
                    }
                }
            }
        }

        private String GetRankString(String rankClass, String level, String percentile, String place, String step)
        {
            return String.Format("{0}-{1}-{2}-{3}-{4}", rankClass, level, percentile, place, step == null ? "None" : step);
        }

        private bool HasPendingGameData()
        {
            return drawnCardsByInstanceId.Count > 0 && gameHistoryEvents.Count > 5;
        }

        private void ClearGameData()
        {
            objectsByOwner.Clear();
            drawnHands.Clear();
            drawnCardsByInstanceId.Clear();
            openingHand.Clear();
            gameHistoryEvents.Clear();
            startingTeamId = -1;
            seatId = -1;
            turnCount = 0;
        }

        private void ClearMatchData()
        {
            screenNames.Clear();
        }

        private void ResetCurrentUser()
        {
            LogMessage("User logged out from MTGA", Level.Info);
            currentUser = null;
            currentScreenName = null;
        }

        private void MaybeHandleAccountInfo(String line)
        {
            if (line.Contains("Updated account. DisplayName:"))
            {
                var match = ACCOUNT_INFO_REGEX.Match(line);
                if (match.Success)
                {
                    var screenName = match.Groups[1].Value;
                    currentUser = match.Groups[2].Value;

                    UpdateScreenName(screenName);
                    return;
                }
            }

            if (line.Contains(" to Match") || line.Contains("Match to "))
            {
                var match = MATCH_ACCOUNT_INFO_REGEX.Match(line);
                if (match.Success)
                {
                    currentUser = match.Groups[2].Value;
                    if (String.IsNullOrEmpty(currentUser))
                    {
                        currentUser = match.Groups[3].Value;
                    }
                }
            }
        }

        private void UpdateScreenName(String newScreenName)
        {
            if (newScreenName.Equals(currentScreenName))
            {
                return;
            }

            currentScreenName = newScreenName;

            MTGAAccount account = new MTGAAccount();
            account.token = apiToken;
            account.client_version = CLIENT_VERSION;
            account.player_id = currentUser;
            account.raw_time = lastRawTime;
            account.screen_name = currentScreenName;
            apiClient.PostMTGAAccount(account);
        } 

        private bool MaybeHandleLogin(JObject blob)
        {
            JToken token;
            if (!blob.TryGetValue("params", out token)) return false;
            if (!token.Value<JObject>().TryGetValue("messageName", out token)) return false;
            if (!token.Value<String>().Equals("Client.Connected")) return false;

            ClearGameData();

            try
            {
                var payload = blob["params"]["payloadObject"];

                currentUser = payload["playerId"].Value<String>();
                var screenName = payload["screenName"].Value<String>();

                UpdateScreenName(screenName);

                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing login from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool SendHandleGameEnd(bool won, string winType, string gameEndReason)
        {
            if (!HasPendingGameData()) return false;

            try
            {
                var opponentId = seatId == 1 ? 2 : 1;
                var opponentCardIds = new List<int>();
                if (objectsByOwner.ContainsKey(opponentId))
                {
                    foreach (KeyValuePair<int, int> entry in objectsByOwner[opponentId])
                    {
                        opponentCardIds.Add(entry.Value);
                    }
                }
                var mulligans = new List<List<int>>();
                if (drawnHands.ContainsKey(seatId) && drawnHands[seatId].Count > 0)
                {
                    mulligans = drawnHands[seatId].GetRange(0, drawnHands[seatId].Count - 1);
                }

                if (!currentMatchId.Equals(currentOpponentMatchId))
                {
                    currentOpponentLevel = null;
                }

                JObject game = new JObject();

                game.Add("token", JToken.FromObject(apiToken));
                game.Add("client_version", JToken.FromObject(CLIENT_VERSION));
                game.Add("player_id", JToken.FromObject(currentUser));
                game.Add("time", JToken.FromObject(GetDatetimeString(currentLogTime.Value)));
                game.Add("utc_time", JToken.FromObject(GetDatetimeString(lastUtcTime.Value)));

                game.Add("event_name", JToken.FromObject(currentEventName));
                game.Add("match_id", JToken.FromObject(currentMatchId));
                game.Add("on_play", JToken.FromObject(seatId.Equals(startingTeamId)));
                game.Add("won", JToken.FromObject(won));
                game.Add("win_type", JToken.FromObject(winType));
                game.Add("game_end_reason", JToken.FromObject(gameEndReason));


                if (openingHand.ContainsKey(seatId) && openingHand[seatId].Count > 0)
                {
                    game.Add("opening_hand", JToken.FromObject(openingHand[seatId]));
                }

                if (drawnHands.ContainsKey(opponentId) && drawnHands[opponentId].Count > 0)
                {
                    game.Add("opponent_mulligan_count", JToken.FromObject(drawnHands[opponentId].Count - 1));
                }

                if (drawnHands.ContainsKey(seatId) && drawnHands[seatId].Count > 0)
                {
                    game.Add("drawn_hands", JToken.FromObject(drawnHands[seatId]));
                }

                if (drawnCardsByInstanceId.ContainsKey(seatId))
                {
                    game.Add("drawn_cards", JToken.FromObject(drawnCardsByInstanceId[seatId].Values.ToList()));
                }

                game.Add("mulligans", JToken.FromObject(mulligans));
                game.Add("turns", JToken.FromObject(turnCount));
                if (currentLimitedLevel != null)
                {
                    game.Add("limited_rank", JToken.FromObject(currentLimitedLevel));
                }
                if (currentConstructedLevel != null)
                {
                    game.Add("constructed_rank", JToken.FromObject(currentConstructedLevel));
                }
                if (currentOpponentLevel != null)
                {
                    game.Add("opponent_rank", JToken.FromObject(currentOpponentLevel));
                }
                game.Add("duration", JToken.FromObject(-1));
                game.Add("opponent_card_ids", JToken.FromObject(opponentCardIds));

                LogMessage(String.Format("Posting game of {0}", game.ToString(Formatting.None)), Level.Info);
                if (gameHistoryEnabled && apiClient.ShouldSubmitGameHistory(apiToken))
                {
                    LogMessage(String.Format("Including game history of {0} events", gameHistoryEvents.Count()), Level.Info);
                    JObject history = new JObject();
                    history.Add("seat_id", seatId);
                    history.Add("opponent_seat_id", opponentId);
                    history.Add("screen_name", screenNames[seatId]);
                    history.Add("opponent_screen_name", screenNames[opponentId]);
                    history.Add("events", JToken.FromObject(gameHistoryEvents));

                    game.Add("history", history);
                }

                apiClient.PostGame(game);
                ClearGameData();

                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} sending game result", e), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool MaybeHandleBotDraftPack(JObject blob)
        {
            if (!blob.ContainsKey("DraftStatus")) return false;
            if (!"PickNext".Equals(blob["DraftStatus"].Value<String>())) return false;

            ClearGameData();

            try
            {
                currentDraftEvent = blob["EventName"].Value<String>();
                Pack pack = new Pack();
                pack.token = apiToken;
                pack.client_version = CLIENT_VERSION;
                pack.player_id = currentUser;
                pack.time = GetDatetimeString(currentLogTime.Value);
                pack.utc_time = GetDatetimeString(lastUtcTime.Value);

                var cardIds = new List<int>();
                foreach (JToken cardString in blob["DraftPack"].Value<JArray>())
                {
                    cardIds.Add(int.Parse(cardString.Value<String>()));
                }

                pack.event_name = currentDraftEvent;
                pack.pack_number = blob["PackNumber"].Value<int>();
                pack.pick_number = blob["PickNumber"].Value<int>();
                pack.card_ids = cardIds;

                apiClient.PostPack(pack);
                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing draft pack from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool MaybeHandleBotDraftPick(String fullLog, JObject blob)
        {
            if (!fullLog.Contains("BotDraft_DraftPick")) return false;
            if (!blob.ContainsKey("PickInfo")) return false;

            ClearGameData();

            try
            {
                var pickInfo = blob["PickInfo"].Value<JObject>();
                currentDraftEvent = pickInfo["EventName"].Value<String>();

                Pick pick = new Pick();
                pick.token = apiToken;
                pick.client_version = CLIENT_VERSION;
                pick.player_id = currentUser;
                pick.time = GetDatetimeString(currentLogTime.Value);
                pick.utc_time = GetDatetimeString(lastUtcTime.Value);

                pick.event_name = currentDraftEvent;
                pick.pack_number = pickInfo["PackNumber"].Value<int>();
                pick.pick_number = pickInfo["PickNumber"].Value<int>();
                pick.card_id = pickInfo["CardId"].Value<int>();

                apiClient.PostPick(pick);

                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing draft pick from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool MaybeHandleJoinPod(String fullLog, JObject blob)
        {
            if (!fullLog.Contains("Event_Join")) return false;
            if (!blob.ContainsKey("EventName")) return false;

            ClearGameData();

            try
            {
                currentDraftEvent = blob["EventName"].Value<String>();
                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing join pod event from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool MaybeHandleHumanDraftCombined(String fullLog, JObject blob)
        {
            if (!fullLog.Contains("LogBusinessEvents")) return false;
            if (!blob.ContainsKey("PickGrpId")) return false;

            ClearGameData();

            try
            {
                currentDraftEvent = blob["EventId"].Value<String>();

                HumanDraftPack pack = new HumanDraftPack();
                pack.method = "LogBusinessEvents";
                pack.token = apiToken;
                pack.client_version = CLIENT_VERSION;
                pack.player_id = currentUser;
                pack.time = GetDatetimeString(currentLogTime.Value);
                pack.utc_time = GetDatetimeString(lastUtcTime.Value);

                var cardIds = new List<int>();
                foreach (JToken cardId in blob["CardsInPack"].Value<JArray>())
                {
                    cardIds.Add(cardId.Value<int>());
                }

                pack.draft_id = blob["DraftId"].Value<String>();
                pack.pack_number = blob["PackNumber"].Value<int>();
                pack.pick_number = blob["PickNumber"].Value<int>();
                pack.card_ids = cardIds;
                pack.event_name = currentDraftEvent;

                apiClient.PostHumanDraftPack(pack);
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing combined human draft pack from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }

            try
            {
                HumanDraftPick pick = new HumanDraftPick();
                pick.token = apiToken;
                pick.client_version = CLIENT_VERSION;
                pick.player_id = currentUser;
                pick.time = GetDatetimeString(currentLogTime.Value);
                pick.utc_time = GetDatetimeString(lastUtcTime.Value);

                pick.draft_id = blob["DraftId"].Value<String>();
                pick.pack_number = blob["PackNumber"].Value<int>();
                pick.pick_number = blob["PickNumber"].Value<int>();
                pick.card_id = blob["PickGrpId"].Value<int>();
                pick.event_name = currentDraftEvent;
                pick.auto_pick = blob["AutoPick"].Value<bool>();
                pick.time_remaining = blob["TimeRemainingOnPick"].Value<float>();

                apiClient.PostHumanDraftPick(pick);
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing combined human draft pick from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }

            return true;
        }
        private bool MaybeHandleHumanDraftPack(String fullLog, JObject blob)
        {
            if (!fullLog.Contains("Draft.Notify ")) return false;
            if (blob.ContainsKey("method")) return false;

            ClearGameData();

            try
            {
                HumanDraftPack pack = new HumanDraftPack();
                pack.method = "Draft.Notify";
                pack.token = apiToken;
                pack.client_version = CLIENT_VERSION;
                pack.player_id = currentUser;
                pack.time = GetDatetimeString(currentLogTime.Value);
                pack.utc_time = GetDatetimeString(lastUtcTime.Value);

                var cardIds = new List<int>();
                var cardIdBlob = JArray.Parse(String.Format("[{0}]", blob["PackCards"].Value<String>()));
                foreach (JToken cardId in cardIdBlob)
                {
                    cardIds.Add(cardId.Value<int>());
                }

                pack.draft_id = blob["draftId"].Value<String>();
                pack.pack_number = blob["SelfPack"].Value<int>();
                pack.pick_number = blob["SelfPick"].Value<int>();
                pack.card_ids = cardIds;
                pack.event_name = currentDraftEvent;

                apiClient.PostHumanDraftPack(pack);
                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing human draft pack from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool MaybeHandleDraftNotification(String fullLog, JObject blob)
        {
            if (!fullLog.Contains("Draft.Notification ")) return false;
            if (blob.ContainsKey("method")) return false;
            if (!blob.ContainsKey("PickInfo")) return false;
            if (blob["PickInfo"].Value<JObject>() == null) return false;

            ClearGameData();

            try
            {
                var pickInfo = blob["PickInfo"].Value<JObject>();
                HumanDraftPack pack = new HumanDraftPack();
                pack.method = "Draft.Notification";
                pack.token = apiToken;
                pack.client_version = CLIENT_VERSION;
                pack.player_id = currentUser;
                pack.time = GetDatetimeString(currentLogTime.Value);
                pack.utc_time = GetDatetimeString(lastUtcTime.Value);

                var cardIds = new List<int>();
                var cardIdJArray = pickInfo["PackCards"].Value<JArray>();
                foreach (JToken cardId in cardIdJArray)
                {
                    cardIds.Add(cardId.Value<int>());
                }

                pack.draft_id = blob["DraftId"].Value<String>();
                pack.pack_number = pickInfo["SelfPack"].Value<int>();
                pack.pick_number = pickInfo["SelfPick"].Value<int>();
                pack.card_ids = cardIds;
                pack.event_name = currentDraftEvent;

                apiClient.PostHumanDraftPack(pack);
                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing human draft pack from notification {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }
        
        private bool MaybeHandleFrontDoorConnectionClose(String fullLog, JObject blob)
        {
            if (!fullLog.Contains("FrontDoorConnection.Close ")) return false;

            if (currentUser != null)
            {
                disconnectedUser = currentUser;
                disconnectedScreenName = currentScreenName;
            }

            ResetCurrentUser();

            return true;
        }

        private bool MaybeHandleReconnectResult(String fullLog, JObject blob)
        {
            if (!fullLog.Contains("Reconnect result : Connected")) return false;

            LogMessage("Reconnected - restoring prior user info", Level.Info);
            currentUser = disconnectedUser;
            currentScreenName = disconnectedScreenName;

            return true;
        }

        private bool MaybeHandleDeckSubmission(String fullLog, JObject blob)
        {
            if (!fullLog.Contains("Event_SetDeck")) return false;
            if (!blob.ContainsKey("EventName")) return false;

            ClearGameData();

            try
            {

                Deck deck = new Deck();
                deck.token = apiToken;
                deck.client_version = CLIENT_VERSION;
                deck.player_id = currentUser;
                deck.time = GetDatetimeString(currentLogTime.Value);
                deck.utc_time = GetDatetimeString(lastUtcTime.Value);

                var deckInfo = blob["Deck"].Value<JObject>();

                deck.maindeck_card_ids = GetCardIdsFromDeck(deckInfo["MainDeck"].Value<JArray>());
                deck.sideboard_card_ids = GetCardIdsFromDeck(deckInfo["Sideboard"].Value<JArray>());
                foreach (int companion in GetCardIdsFromDeck(deckInfo["Companions"].Value<JArray>()))
                {
                    deck.companion = companion;
                }

                deck.event_name = blob["EventName"].Value<String>();
                deck.is_during_match = false;

                apiClient.PostDeck(deck);
                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing deck submission from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool MaybeHandleOngoingEvents(String fullLog, JObject blob)
        {
            if (!fullLog.Contains("Event_GetCourses")) return false;
            if (!blob.ContainsKey("Courses")) return false;

            try
            {
                JObject event_ = new JObject();
                event_.Add("token", JToken.FromObject(apiToken));
                event_.Add("client_version", JToken.FromObject(CLIENT_VERSION));
                if (currentUser != null)
                {
                    event_.Add("player_id", JToken.FromObject(currentUser));
                }
                event_.Add("time", JToken.FromObject(GetDatetimeString(currentLogTime.Value)));
                event_.Add("utc_time", JToken.FromObject(GetDatetimeString(lastUtcTime.Value)));

                event_.Add("courses", JArray.FromObject(blob["Courses"]));

                apiClient.PostOngoingEvents(event_);

                LogMessage(String.Format("Parsed ongoing event"), Level.Info);

                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing ongoing event from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool MaybeHandleClaimPrize(String fullLog, JObject blob)
        {
            if (!fullLog.Contains("Event_ClaimPrize")) return false;
            if (!blob.ContainsKey("EventName")) return false;

            try
            {
                JObject event_ = new JObject();
                event_.Add("token", JToken.FromObject(apiToken));
                event_.Add("client_version", JToken.FromObject(CLIENT_VERSION));
                if (currentUser != null)
                {
                    event_.Add("player_id", JToken.FromObject(currentUser));
                }
                event_.Add("time", JToken.FromObject(GetDatetimeString(currentLogTime.Value)));
                event_.Add("utc_time", JToken.FromObject(GetDatetimeString(lastUtcTime.Value)));

                event_.Add("event_name", blob["EventName"].Value<String>());

                apiClient.PostEventEnded(event_);

                LogMessage(String.Format("Parsed claim prize event"), Level.Info);

                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing claim prize event from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool MaybeHandleScreenNameUpdate(String fullLog, JObject blob)
        {
            if (!blob.ContainsKey("authenticateResponse")) return false;

            try
            {
                UpdateScreenName(blob["authenticateResponse"].Value<JObject>()["screenName"].Value<String>());
                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing screen name from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool MaybeHandleEventCompletion(JObject blob)
        {
            if (!blob.ContainsKey("CurrentEventState")) return false;
            if (!"DoneWithMatches".Equals(blob["CurrentEventState"].Value<String>())) return false;

            try
            {
                Event event_ = new Event();
                event_.token = apiToken;
                event_.client_version = CLIENT_VERSION;
                event_.player_id = currentUser;
                event_.time = GetDatetimeString(currentLogTime.Value);
                event_.utc_time = GetDatetimeString(lastUtcTime.Value);

                event_.event_name = blob["InternalEventName"].Value<String>();
                if (blob["ModuleInstanceData"]["HasPaidEntry"] != null)
                {
                    event_.entry_fee = blob["ModuleInstanceData"]["HasPaidEntry"].Value<String>();
                }
                else
                {
                    event_.entry_fee = "None";
                }
                event_.wins = blob["ModuleInstanceData"]["WinLossGate"]["CurrentWins"].Value<int>();
                event_.losses = blob["ModuleInstanceData"]["WinLossGate"]["CurrentLosses"].Value<int>();

                apiClient.PostEvent(event_);
                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing event completion from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool MaybeHandleEventCourse(String fullLog, JObject blob)
        {
            if (!fullLog.Contains("Draft_CompleteDraft")) return false;
            if (!blob.ContainsKey("DraftId")) return false;

            try
            {
                EventCourse event_ = new EventCourse();
                event_.token = apiToken;
                event_.client_version = CLIENT_VERSION;
                event_.time = GetDatetimeString(currentLogTime.Value);
                event_.utc_time = GetDatetimeString(lastUtcTime.Value);
                event_.player_id = currentUser;

                event_.event_name = blob["InternalEventName"].Value<String>();
                event_.draft_id = blob["DraftId"].Value<String>();

                apiClient.PostEventCourse(event_);

                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing partial event course from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool MaybeHandleGreMessage_DeckSubmission(JToken blob)
        {
            if (!"ClientMessageType_SubmitDeckResp".Equals(blob["type"].Value<string>())) return false;

            try
            {
                objectsByOwner.Clear();

                Deck deck = new Deck();
                deck.token = apiToken;
                deck.client_version = CLIENT_VERSION;
                deck.player_id = currentUser;
                deck.time = GetDatetimeString(currentLogTime.Value);
                deck.utc_time = GetDatetimeString(lastUtcTime.Value);

                deck.event_name = null;
                JToken deckInfo = blob["submitDeckResp"]["deck"];
                deck.maindeck_card_ids = JArrayToIntList(deckInfo["deckCards"].Value<JArray>());
                if (deckInfo["sideboardCards"] == null) {
                    deck.sideboard_card_ids = new List<int>();
                }
                else
                {
                    deck.sideboard_card_ids = JArrayToIntList(blob["submitDeckResp"]["deck"]["sideboardCards"].Value<JArray>());
                }

                if (deckInfo["companionGRPId"] != null)
                {
                    deck.companion = deckInfo["companionGRPId"].Value<int>();
                }
                else if (deckInfo["companion"] != null)
                {
                    deck.companion = deckInfo["companion"].Value<int>();
                }
                else if (deckInfo["deckMessageFieldFour"] != null)
                {
                    deck.companion = deckInfo["deckMessageFieldFour"].Value<int>();
                }

                deck.is_during_match = true;

                apiClient.PostDeck(deck);
                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing GRE deck submission from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool MaybeHandleGreMessage_GameState(JToken blob)
        {
            if (!"GREMessageType_GameStateMessage".Equals(blob["type"].Value<string>())) return false;

            try
            {
                if (blob.Value<JObject>().ContainsKey("systemSeatIds"))
                {
                    var systemSeatIds = blob["systemSeatIds"].Value<JArray>();
                    if (systemSeatIds.Count > 0)
                    {
                        seatId = systemSeatIds[0].Value<int>();
                    }
                }

                var gameStateMessage = blob["gameStateMessage"].Value<JObject>();

                if (gameStateMessage.ContainsKey("gameInfo"))
                {
                    var gameInfo = gameStateMessage["gameInfo"].Value<JObject>();
                    if (gameInfo.ContainsKey("matchID"))
                    {
                        currentMatchId = gameInfo["matchID"].Value<string>();
                    }
                }

                if (gameStateMessage.ContainsKey("gameObjects"))
                {
                    foreach (JToken gameObject in gameStateMessage["gameObjects"].Value<JArray>())
                    {
                        var objectType = gameObject["type"].Value<string>();
                        if (!"GameObjectType_Card".Equals(objectType) && !"GameObjectType_SplitCard".Equals(objectType)) continue;

                        var owner = gameObject["ownerSeatId"].Value<int>();
                        var instanceId = gameObject["instanceId"].Value<int>();
                        var cardId = gameObject["overlayGrpId"].Value<int>();

                        if (!objectsByOwner.ContainsKey(owner))
                        {
                            objectsByOwner.Add(owner, new Dictionary<int, int>());
                        }
                        objectsByOwner[owner][instanceId] = cardId;
                    }

                }
                if (gameStateMessage.ContainsKey("zones"))
                {
                    foreach (JObject zone in gameStateMessage["zones"].Value<JArray>())
                    {
                        
                        if (!"ZoneType_Hand".Equals(zone["type"].Value<string>())) continue;

                        var owner = zone["ownerSeatId"].Value<int>();
                        var cards = new List<int>();
                        if (!drawnCardsByInstanceId.ContainsKey(owner))
                        {
                            drawnCardsByInstanceId[owner] = new Dictionary<int, int>();
                        }
                        if (zone.ContainsKey("objectInstanceIds"))
                        {
                            var playerObjects = objectsByOwner.ContainsKey(owner) ? objectsByOwner[owner] : new Dictionary<int, int>();
                            foreach (JToken objectInstanceId in zone["objectInstanceIds"].Value<JArray>())
                            {
                                if (objectInstanceId != null && playerObjects.ContainsKey(objectInstanceId.Value<int>()))
                                {
                                    int instanceId = objectInstanceId.Value<int>();
                                    int cardId = playerObjects[instanceId];
                                    cards.Add(cardId);
                                    drawnCardsByInstanceId[owner][instanceId] = cardId;
                                }
                            }
                        }
                        cardsInHand[owner] = cards;
                    }

                }
                if (gameStateMessage.ContainsKey("players"))
                {
                    foreach (JObject player in gameStateMessage.GetValue("players").Value<JArray>())
                    {
                        if (player.ContainsKey("pendingMessageType") && player.GetValue("pendingMessageType").Value<string>().Equals("ClientMessageType_MulliganResp"))
                        {
                            JToken tmp;
                            if (gameStateMessage.ContainsKey("turnInfo"))
                            {
                                var turnInfo = gameStateMessage.GetValue("turnInfo").Value<JObject>();
                                if (startingTeamId == -1 && turnInfo.TryGetValue("activePlayer", out tmp))
                                {
                                    startingTeamId = tmp.Value<int>();
                                }
                            }

                            var playerId = player.GetValue("systemSeatNumber").Value<int>();

                            if (!drawnHands.ContainsKey(playerId))
                            {
                                drawnHands.Add(playerId, new List<List<int>>());
                            }
                            var mulliganCount = 0;
                            if (player.TryGetValue("mulliganCount", out tmp))
                            {
                                mulliganCount = tmp.Value<int>();
                            }
                            if (mulliganCount == drawnHands[playerId].Count)
                            {
                                drawnHands[playerId].Add(new List<int>(cardsInHand[playerId]));
                            }
                        }
                    }
                }
                if (gameStateMessage.ContainsKey("turnInfo"))
                {
                    var turnInfo = gameStateMessage.GetValue("turnInfo").Value<JObject>();

                    if (turnInfo.ContainsKey("turnNumber"))
                    {
                        turnCount = turnInfo["turnNumber"].Value<int>();
                    }
                    else if (gameStateMessage.ContainsKey("players"))
                    {
                        turnCount = 0;
                        var players = gameStateMessage["players"].Value<JArray>();
                        foreach (JToken turnToken in players)
                        {
                            if (turnToken.Value<JObject>().ContainsKey("turnNumber"))
                            {
                                turnCount += turnToken.Value<JObject>()["turnNumber"].Value<int>(0);
                            }
                        }
                        if (players.Count == 1)
                        {
                            turnCount *= 2;
                        }
                    }

                    if (openingHand.Count == 0 && turnInfo.ContainsKey("phase") && turnInfo.ContainsKey("step"))
                    {
                        if (turnInfo.GetValue("phase").Value<string>().Equals("Phase_Beginning") && turnInfo.GetValue("step").Value<string>().Equals("Step_Upkeep") && turnCount == 1)
                        {
                            LogMessage("Recording opening hands", Level.Info);
                            foreach (int playerId in cardsInHand.Keys)
                            {
                                openingHand[playerId] = new List<int>(cardsInHand[playerId]);
                            }
                        }
                    }
                }

                MaybeHandleGameOverStage(gameStateMessage);
                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing GRE message from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool MaybeHandleGameOverStage(JObject gameStateMessage)
        {
            if (!gameStateMessage.ContainsKey("gameInfo")) return false;
            var gameInfo = gameStateMessage["gameInfo"].Value<JObject>();
            if (!gameInfo.ContainsKey("stage") || !gameInfo["stage"].Value<String>().Equals("GameStage_GameOver")) return false;
            if (!gameInfo.ContainsKey("results")) return false;

            var results = gameInfo["results"].Value<JArray>();
            for (int i = results.Count - 1; i >= 0; i--)
            {
                var result = results[i].Value<JObject>();
                if (!result.ContainsKey("scope") || !result["scope"].Value<String>().Equals("MatchScope_Game")) continue;

                var won = seatId.Equals(result["winningTeamId"].Value<int>());
                var winType = result["result"].Value<String>();
                var gameEndReason = result["reason"].Value<String>();

                var success = SendHandleGameEnd(won, winType, gameEndReason);

                if (gameInfo.ContainsKey("matchState") && gameInfo["matchState"].Value<String>().Equals("MatchState_MatchComplete"))
                {
                    ClearMatchData();
                }

                return success;
            }
            return false;
        }

        private bool MaybeHandleMatchStateChanged(JObject blob)
        {
            if (!blob.ContainsKey("matchGameRoomStateChangedEvent")) return false;
            var matchEvent = blob["matchGameRoomStateChangedEvent"].Value<JObject>();
            if (!matchEvent.ContainsKey("gameRoomInfo")) return false;
            var gameRoomInfo = matchEvent["gameRoomInfo"].Value<JObject>();
            if (!gameRoomInfo.ContainsKey("gameRoomConfig")) return false;
            var gameRoomConfig = gameRoomInfo["gameRoomConfig"].Value<JObject>();

            if (gameRoomConfig.ContainsKey("eventId") && gameRoomConfig.ContainsKey("matchId"))
            {
                currentEventName = gameRoomConfig["eventId"].Value<String>();
            }

            if (gameRoomConfig.ContainsKey("reservedPlayers"))
            {
                String opponentPlayerId = null;
                foreach (JToken player in gameRoomConfig["reservedPlayers"])
                {
                    screenNames[player["systemSeatId"].Value<int>()] = player["playerName"].Value<String>().Split('#')[0];

                    var playerId = player["userId"].Value<String>();
                    if (playerId.Equals(currentUser))
                    {
                        UpdateScreenName(player["playerName"].Value<String>());
                    }
                    else
                    {
                        opponentPlayerId = playerId;
                    }
                }

                if (opponentPlayerId != null && gameRoomConfig.ContainsKey("clientMetadata"))
                {
                    var metadata = gameRoomConfig["clientMetadata"].Value<JObject>();

                    currentOpponentLevel = GetRankString(
                        GetOrEmpty(metadata, opponentPlayerId + "_RankClass"),
                        GetOrEmpty(metadata, opponentPlayerId + "_RankTier"),
                        GetOrEmpty(metadata, opponentPlayerId + "_LeaderboardPercentile"),
                        GetOrEmpty(metadata, opponentPlayerId + "_LeaderboardPlacement"),
                        null
                    );
                    currentOpponentMatchId = gameRoomConfig["matchId"].Value<string>();
                    LogMessage(String.Format("Parsed opponent rank info as {0} in match {1}", currentOpponentLevel, currentOpponentMatchId), Level.Info);
                }
            }

            if (gameRoomInfo.ContainsKey("finalMatchResult"))
            {
                // If the regular game end message is lost, try to submit remaining game data on match end.
                var finalMatchResult = gameRoomInfo["finalMatchResult"].Value<JObject>();
                if (finalMatchResult.ContainsKey("resultList")) {
                    var results = finalMatchResult["resultList"].Value<JArray>();
                    for (int i = results.Count - 1; i >= 0; i--)
                    {
                        var result = results[i].Value<JObject>();
                        if (!result.ContainsKey("scope") || !result["scope"].Value<String>().Equals("MatchScope_Game")) continue;

                        var won = seatId.Equals(result["winningTeamId"].Value<int>());
                        var winType = result["result"].Value<String>();

                        var success = SendHandleGameEnd(won, winType, "");
                        ClearMatchData();
                        return success;
                    }
                }
                return false;
            }

            return false;
        }

        private bool MaybeHandleGreToClientMessages(JObject blob)
        {
            if (!blob.ContainsKey("greToClientEvent")) return false;
            if (!blob["greToClientEvent"].Value<JObject>().ContainsKey("greToClientMessages")) return false;

            try
            {
                foreach (JToken message in blob["greToClientEvent"]["greToClientMessages"])
                {
                    AddGameHistoryEvents(message);
                    if (MaybeHandleGreMessage_GameState(message)) continue;
                }
                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing GRE to client messages from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private void AddGameHistoryEvents(JToken message)
        {
            if (!gameHistoryEnabled) return;

            if (GAME_HISTORY_MESSAGE_TYPES.Contains(message["type"].Value<String>()))
            {
                gameHistoryEvents.Add(message);
            }
            else if (message["type"].Value<String>() == "GREMessageType_UIMessage")
            {
                if (message.Value<JObject>().ContainsKey("uiMessage"))
                {
                    var uiMessage = message["uiMessage"].Value<JObject>();
                    if (uiMessage.ContainsKey("onChat"))
                    {
                        gameHistoryEvents.Add(message);
                    }
                }
            }
        }

        private bool MaybeHandleClientToGreMessage(JObject blob)
        {
            if (!blob.ContainsKey("clientToMatchServiceMessageType")) return false;
            if (!"ClientToMatchServiceMessageType_ClientToGREMessage".Equals(blob["clientToMatchServiceMessageType"].Value<String>())) return false;

            try
            {
                if (blob.ContainsKey("payload"))
                {
                    var payload = blob["payload"].Value<JObject>();
                    if (gameHistoryEnabled && payload["type"].Value<String>().Equals("ClientMessageType_SelectNResp"))
                    {
                        gameHistoryEvents.Add(payload);
                    }
                    if (MaybeHandleGreMessage_DeckSubmission(payload)) return true;
                }

                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing GRE to client messages from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool MaybeHandleClientToGreUiMessage(JObject blob)
        {
            if (!blob.ContainsKey("clientToMatchServiceMessageType")) return false;
            if (!"ClientToMatchServiceMessageType_ClientToGREUIMessage".Equals(blob["clientToMatchServiceMessageType"].Value<String>())) return false;

            try
            {
                if (gameHistoryEnabled)
                {
                    if (blob.ContainsKey("payload"))
                    {
                        var payload = blob["payload"].Value<JObject>();
                        if (payload.ContainsKey("uiMessage"))
                        {
                            var uiMessage = payload["uiMessage"].Value<JObject>();
                            if (uiMessage.ContainsKey("onChat"))
                            {
                                gameHistoryEvents.Add(payload);
                            }
                        }
                    }
                }

                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing GRE to client UI messages from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private String GetOrEmpty(JObject blob, String key)
        {
            if (blob.ContainsKey(key))
            {
                return blob[key].Value<String>();
            }
            return "None";
        }

        private bool MaybeHandleSelfRankInfo(String fullLog, JObject blob)
        {
            if (!fullLog.Contains("Rank_GetCombinedRankInfo")) return false;
            if (!blob.ContainsKey("limitedClass")) return false;

            try
            {
                currentLimitedLevel = GetRankString(
                    GetOrEmpty(blob, "limitedClass"),
                    GetOrEmpty(blob, "limitedLevel"),
                    GetOrEmpty(blob, "limitedPercentile"),
                    GetOrEmpty(blob, "limitedLeaderboardPlace"),
                    GetOrEmpty(blob, "limitedStep")
                );
                currentConstructedLevel = GetRankString(
                    GetOrEmpty(blob, "constructedClass"),
                    GetOrEmpty(blob, "constructedLevel"),
                    GetOrEmpty(blob, "constructedPercentile"),
                    GetOrEmpty(blob, "constructedLeaderboardPlace"),
                    GetOrEmpty(blob, "constructedStep")
                );

                if (blob.ContainsKey("playerId"))
                {
                    currentUser = blob["playerId"].Value<String>();
                }

                LogMessage(String.Format("Parsed rank info for {0} as limited {1} and constructed {2}", currentUser, currentLimitedLevel, currentConstructedLevel), Level.Info);

                JObject rankBlob = new JObject();
                rankBlob.Add("token", JToken.FromObject(apiToken));
                rankBlob.Add("client_version", JToken.FromObject(CLIENT_VERSION));
                if (currentUser != null)
                {
                    rankBlob.Add("player_id", JToken.FromObject(currentUser));
                }
                rankBlob.Add("time", JToken.FromObject(GetDatetimeString(currentLogTime.Value)));
                rankBlob.Add("limited_rank", JToken.FromObject(currentLimitedLevel));
                rankBlob.Add("constructed_rank", JToken.FromObject(currentConstructedLevel));
                apiClient.PostRank(rankBlob);

                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing self rank info from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool MaybeHandleMatchCreated(JObject blob)
        {
            if (!blob.ContainsKey("opponentRankingClass")) return false;

            ClearGameData();

            try
            {
                currentOpponentLevel = GetRankString(
                    GetOrEmpty(blob, "opponentRankingClass"),
                    GetOrEmpty(blob, "opponentRankingTier"),
                    GetOrEmpty(blob, "opponentMythicPercentile"),
                    GetOrEmpty(blob, "opponentMythicLeaderboardPlace"),
                    null
                );

                if (blob.ContainsKey("matchId"))
                {
                    currentMatchId = blob["matchId"].Value<String>();
                }

                LogMessage(String.Format("Parsed opponent rank info as {0} in match {1}", currentOpponentLevel, currentMatchId), Level.Info);

                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing match creation from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool MaybeHandleCollection(String fullLog, JObject blob)
        {
            if (!fullLog.Contains(" PlayerInventory.GetPlayerCardsV3 ")) return false;
            if (blob.ContainsKey("method")) return false;

            if (currentUser == null)
            {
                LogMessage("Skipping collection submission while player id is unknown", Level.Info);
                return true;
            }

            try
            {
                Collection collection = new Collection();
                collection.token = apiToken;
                collection.client_version = CLIENT_VERSION;
                collection.player_id = currentUser;
                collection.time = GetDatetimeString(currentLogTime.Value);
                collection.utc_time = GetDatetimeString(lastUtcTime.Value);
                collection.card_counts = blob.ToObject<Dictionary<string, int>>();

                apiClient.PostCollection(collection);

                LogMessage(String.Format("Parsed collection"), Level.Info);

                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing collection from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool MaybeHandleInventory(String fullLog, JObject blob)
        {
            if (!blob.ContainsKey("InventoryInfo")) return false;

            try
            {
                var inventoryInfo = blob["InventoryInfo"].Value<JObject>();

                JObject inventory = new JObject();
                inventory.Add("token", JToken.FromObject(apiToken));
                inventory.Add("client_version", JToken.FromObject(CLIENT_VERSION));
                if (currentUser != null)
                {
                    inventory.Add("player_id", JToken.FromObject(currentUser));
                }
                inventory.Add("time", JToken.FromObject(GetDatetimeString(currentLogTime.Value)));
                inventory.Add("utc_time", JToken.FromObject(GetDatetimeString(lastUtcTime.Value)));

                JObject contents = new JObject();
                foreach (String key in INVENTORY_KEYS)
                {
                    if (inventoryInfo.ContainsKey(key))
                    {
                        contents.Add(key, JToken.FromObject(inventoryInfo[key]));
                    }
                }
                inventory.Add("inventory", JToken.FromObject(contents));

                apiClient.PostInventory(inventory);

                LogMessage(String.Format("Parsed inventory"), Level.Info);

                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing inventory from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool MaybeHandlePlayerProgress(String fullLog, JObject blob)
        {
            if (!blob.ContainsKey("NodeStates")) return false;
            if (!blob["NodeStates"].Value<JObject>().ContainsKey("RewardTierUpgrade")) return false;

            try
            {
                JObject progress = new JObject();
                progress.Add("token", JToken.FromObject(apiToken));
                progress.Add("client_version", JToken.FromObject(CLIENT_VERSION));
                if (currentUser != null)
                {
                    progress.Add("player_id", JToken.FromObject(currentUser));
                }
                progress.Add("time", JToken.FromObject(GetDatetimeString(currentLogTime.Value)));
                progress.Add("utc_time", JToken.FromObject(GetDatetimeString(lastUtcTime.Value)));
                progress.Add("progress", JToken.FromObject(blob));

                apiClient.PostPlayerProgress(progress);

                LogMessage(String.Format("Parsed mastery progress"), Level.Info);

                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing mastery progress from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private void LogMessage(string message, Level logLevel)
        {
            messageFunction(message, logLevel);
        }

        private void LogError(string message, string stacktrace, Level logLevel)
        {
            LogMessage(message, logLevel);

            messageFunction(String.Format("Current blob: {0}", currentDebugBlob), Level.Debug);
            messageFunction(String.Format("Previous blob: {0}", lastBlob), Level.Debug);
            messageFunction("Recent lines:", Level.Debug);
            foreach (string line in recentLines)
            {
                messageFunction(line, Level.Debug);
            }

            var errorInfo = new ErrorInfo();
            errorInfo.client_version = CLIENT_VERSION;
            errorInfo.token = apiToken;
            errorInfo.blob = currentDebugBlob;
            errorInfo.recent_lines = new List<string>(recentLines);
            errorInfo.stacktrace = String.Format("{0}\r\n{1}", message, stacktrace);
            apiClient.PostErrorInfo(errorInfo);
        }

        private string GetDatetimeString(DateTime value)
        {
            return value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private List<int> JArrayToIntList(JArray arr)
        {
            var output = new List<int>();
            foreach (JToken token in arr)
            {
                output.Add(token.Value<int>());
            }
            return output;
        }

        private List<int> GetCardIdsFromDeck(JArray decklist)
        {
            var cardIds = new List<int>();
            foreach (JObject cardInfo in decklist)
            {
                int cardId = cardInfo["cardId"].Value<int>();
                for (int i = 0; i < cardInfo["quantity"].Value<int>(); i++)
                {
                    cardIds.Add(cardId);
                }
            }
            return cardIds;
        }

        private delegate void ShowMessageBoxDelegate(string strMessage, string strCaption, MessageBoxButton enmButton, MessageBoxImage enmImage);
        private static void ShowMessageBox(string strMessage, string strCaption, MessageBoxButton enmButton, MessageBoxImage enmImage)
        {
            MessageBox.Show(strMessage, strCaption, enmButton, enmImage);
        }
        private static void ShowMessageBoxAsync(string strMessage, string strCaption, MessageBoxButton enmButton, MessageBoxImage enmImage)
        {
            ShowMessageBoxDelegate caller = new ShowMessageBoxDelegate(ShowMessageBox);
            caller.BeginInvoke(strMessage, strCaption, enmButton, enmImage, null, null);
        }

    }

    class ApiClient
    {
        private const string API_BASE_URL = "https://www.17lands.com";
        private const string ENDPOINT_ACCOUNT = "api/account";
        private const string ENDPOINT_COLLECTION = "collection";
        private const string ENDPOINT_DECK = "deck";
        private const string ENDPOINT_EVENT = "event";
        private const string ENDPOINT_EVENT_COURSE = "event_course";
        private const string ENDPOINT_GAME = "game";
        private const string ENDPOINT_INVENTORY = "inventory";
        private const string ENDPOINT_PACK = "pack";
        private const string ENDPOINT_PICK = "pick";
        private const string ENDPOINT_PLAYER_PROGRESS = "player_progress";
        private const string ENDPOINT_HUMAN_DRAFT_PICK = "human_draft_pick";
        private const string ENDPOINT_HUMAN_DRAFT_PACK = "human_draft_pack";
        private const string ENDPOINT_CLIENT_VERSION_VALIDATION = "api/version_validation";
        private const string ENDPOINT_TOKEN_VERSION_VALIDATION = "api/token_validation";
        private const string ENDPOINT_GAME_HISTORY_ENABLED = "api/game_history_enabled";
        private const string ENDPOINT_ERROR_INFO = "api/client_errors";
        private const string ENDPOINT_RANK = "api/rank";
        private const string ENDPOINT_ONGOING_EVENTS = "ongoing_events";
        private const string ENDPOINT_EVENT_ENDED = "event_ended";

        private static readonly DataContractJsonSerializerSettings SIMPLE_SERIALIZER_SETTINGS = new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true };

        private static readonly DataContractJsonSerializer SERIALIZER_MTGA_ACCOUNT = new DataContractJsonSerializer(typeof(MTGAAccount));
        private static readonly DataContractJsonSerializer SERIALIZER_PACK = new DataContractJsonSerializer(typeof(Pack));
        private static readonly DataContractJsonSerializer SERIALIZER_PICK = new DataContractJsonSerializer(typeof(Pick));
        private static readonly DataContractJsonSerializer SERIALIZER_HUMAN_DRAFT_PICK = new DataContractJsonSerializer(typeof(HumanDraftPick));
        private static readonly DataContractJsonSerializer SERIALIZER_HUMAN_DRAFT_PACK = new DataContractJsonSerializer(typeof(HumanDraftPack));
        private static readonly DataContractJsonSerializer SERIALIZER_DECK = new DataContractJsonSerializer(typeof(Deck));
        private static readonly DataContractJsonSerializer SERIALIZER_EVENT = new DataContractJsonSerializer(typeof(Event));
        private static readonly DataContractJsonSerializer SERIALIZER_EVENT_COURSE = new DataContractJsonSerializer(typeof(EventCourse));
        private static readonly DataContractJsonSerializer SERIALIZER_COLLECTION = new DataContractJsonSerializer(typeof(Collection), SIMPLE_SERIALIZER_SETTINGS);
        private static readonly DataContractJsonSerializer SERIALIZER_ERROR_INFO = new DataContractJsonSerializer(typeof(ErrorInfo));

        private HttpClient client;
        private readonly LogMessageFunction messageFunction;

        private const int ERROR_COOLDOWN_MINUTES = 2;
        private DateTime? lastErrorPosted = null;

        private const int SERVER_SIDE_GAME_HISTORY_ENABLED_CHECK_INTERVAL_MINUTES = 30;
        private DateTime? lastGameHistoryEnableCheck = null;
        private bool serverSideGameHistoryEnabled = true;

        private const int POST_TRIES = 3;
        private const int POST_RETRY_INTERVAL_MILLIS = 10000;

        [DataContract]
        public class VersionValidationResponse
        {
            [DataMember]
            internal bool is_supported;
            [DataMember]
            internal string latest_version;
        }

        [DataContract]
        public class TokenValidationResponse
        {
            [DataMember]
            internal bool is_valid;
        }

        public ApiClient(LogMessageFunction messageFunction)
        {
            this.messageFunction = messageFunction;
            InitializeClient();
        }

        public void InitializeClient()
        {
            client = new HttpClient();
            client.BaseAddress = new Uri(API_BASE_URL);
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public void StopClient()
        {
            client.Dispose();
        }

        private Stream GetJson(string endpoint)
        {
            HttpResponseMessage response = client.GetAsync(endpoint).Result;
            if (response.IsSuccessStatusCode)
            {
                return response.Content.ReadAsStreamAsync().Result;
            }
            else
            {
                LogMessage(String.Format("Got error response {0} ({1})", (int) response.StatusCode, response.ReasonPhrase), Level.Warn);
                return null;
            }
        }

        private void PostJson(string endpoint, String blob, bool useGzip = false, bool skipLogging = false)
        {
            if (!skipLogging)
            {
                LogMessage(String.Format("Posting {0} of {1}", endpoint, blob), Level.Info);
            }
            for (int tryNumber = 0; tryNumber < POST_TRIES; tryNumber++)
            {
                HttpResponseMessage response;
                if (useGzip)
                {
                    using (var stream = new MemoryStream())
                    {
                        byte[] encoded = Encoding.UTF8.GetBytes(blob);
                        using (var compressedStream = new GZipStream(stream, CompressionMode.Compress, true))
                        {
                            compressedStream.Write(encoded, 0, encoded.Length);
                        }

                        stream.Position = 0;
                        var content = new StreamContent(stream);
                        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                        content.Headers.ContentEncoding.Add("gzip");
                        content.Headers.ContentLength = stream.Length;
                        response = client.PostAsync(endpoint, content).Result;
                    }
                }
                else
                {
                    var content = new StringContent(blob, Encoding.UTF8, "application/json");
                    response = client.PostAsync(endpoint, content).Result;
                }
                if (response.IsSuccessStatusCode)
                {
                    break;
                }
                else 
                {
                    LogMessage(
                        String.Format(
                            "Got error response {0} ({1}) on try {2} of {3}",
                            (int)response.StatusCode, response.ReasonPhrase, tryNumber + 1, POST_TRIES
                        ),
                        Level.Warn);
                    Thread.Sleep(POST_RETRY_INTERVAL_MILLIS);
                }
            }
        }

        public VersionValidationResponse GetVersionValidation()
        {
            var jsonResponse = GetJson(ENDPOINT_CLIENT_VERSION_VALIDATION
                + "?client=" + LogParser.CLIENT_TYPE + "&version="
                + LogParser.CLIENT_VERSION.TrimEnd(new char [] { '.', 'w'}));
            if (jsonResponse == null)
            {
                return null;
            }
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(VersionValidationResponse));
            return ((VersionValidationResponse)serializer.ReadObject(jsonResponse));
        }

        public TokenValidationResponse GetTokenValidation(string token)
        {
            var jsonResponse = GetJson(ENDPOINT_TOKEN_VERSION_VALIDATION + "?token=" + token);
            if (jsonResponse == null)
            {
                return null;
            }
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(TokenValidationResponse));
            return ((TokenValidationResponse)serializer.ReadObject(jsonResponse));
        }

        public bool ShouldSubmitGameHistory(string token)
        {
            DateTime now = DateTime.UtcNow;
            if (lastGameHistoryEnableCheck == null || lastGameHistoryEnableCheck.GetValueOrDefault().AddMinutes(SERVER_SIDE_GAME_HISTORY_ENABLED_CHECK_INTERVAL_MINUTES) < now)
            {
                HttpResponseMessage response = client.GetAsync(ENDPOINT_GAME_HISTORY_ENABLED + "/" + token).Result;
                if (response.IsSuccessStatusCode)
                {
                    serverSideGameHistoryEnabled = response.Content.ReadAsStringAsync().Result == "true";
                }
                lastGameHistoryEnableCheck = now;
            }
            return serverSideGameHistoryEnabled;
        }

        public void PostMTGAAccount(MTGAAccount account)
        {
            MemoryStream stream = new MemoryStream();
            SERIALIZER_MTGA_ACCOUNT.WriteObject(stream, account);
            string jsonString = Encoding.UTF8.GetString(stream.ToArray());
            PostJson(ENDPOINT_ACCOUNT, jsonString);
        }

        public void PostPack(Pack pack)
        {
            MemoryStream stream = new MemoryStream();
            SERIALIZER_PACK.WriteObject(stream, pack);
            string jsonString = Encoding.UTF8.GetString(stream.ToArray());
            PostJson(ENDPOINT_PACK, jsonString);
        }

        public void PostPick(Pick pick)
        {
            MemoryStream stream = new MemoryStream();
            SERIALIZER_PICK.WriteObject(stream, pick);
            string jsonString = Encoding.UTF8.GetString(stream.ToArray());
            PostJson(ENDPOINT_PICK, jsonString);
        }

        public void PostHumanDraftPick(HumanDraftPick pick)
        {
            MemoryStream stream = new MemoryStream();
            SERIALIZER_HUMAN_DRAFT_PICK.WriteObject(stream, pick);
            string jsonString = Encoding.UTF8.GetString(stream.ToArray());
            PostJson(ENDPOINT_HUMAN_DRAFT_PICK, jsonString);
        }

        public void PostHumanDraftPack(HumanDraftPack pack)
        {
            MemoryStream stream = new MemoryStream();
            SERIALIZER_HUMAN_DRAFT_PACK.WriteObject(stream, pack);
            string jsonString = Encoding.UTF8.GetString(stream.ToArray());
            PostJson(ENDPOINT_HUMAN_DRAFT_PACK, jsonString);
        }

        public void PostDeck(Deck deck)
        {
            MemoryStream stream = new MemoryStream();
            SERIALIZER_DECK.WriteObject(stream, deck);
            string jsonString = Encoding.UTF8.GetString(stream.ToArray());
            PostJson(ENDPOINT_DECK, jsonString);
        }

        public void PostGame(JObject game)
        {
            PostJson(ENDPOINT_GAME, game.ToString(Formatting.None), useGzip: true, skipLogging: false);
        }

        public void PostEvent(Event event_)
        {
            MemoryStream stream = new MemoryStream();
            SERIALIZER_EVENT.WriteObject(stream, event_);
            string jsonString = Encoding.UTF8.GetString(stream.ToArray());
            PostJson(ENDPOINT_EVENT, jsonString);
        }

        public void PostEventCourse(EventCourse event_)
        {
            MemoryStream stream = new MemoryStream();
            SERIALIZER_EVENT_COURSE.WriteObject(stream, event_);
            string jsonString = Encoding.UTF8.GetString(stream.ToArray());
            PostJson(ENDPOINT_EVENT_COURSE, jsonString);
        }

        public void PostCollection(Collection collection)
        {
            MemoryStream stream = new MemoryStream();
            SERIALIZER_COLLECTION.WriteObject(stream, collection);
            string jsonString = Encoding.UTF8.GetString(stream.ToArray());
            PostJson(ENDPOINT_COLLECTION, jsonString);
        }

        public void PostInventory(JObject inventory)
        {
            PostJson(ENDPOINT_INVENTORY, inventory.ToString(Formatting.None));
        }

        public void PostPlayerProgress(JObject playerProgress)
        {
            PostJson(ENDPOINT_PLAYER_PROGRESS, playerProgress.ToString(Formatting.None));
        }

        public void PostRank(JObject inventory)
        {
            PostJson(ENDPOINT_RANK, inventory.ToString(Formatting.None));
        }

        public void PostErrorInfo(ErrorInfo errorInfo)
        {
            DateTime now = DateTime.UtcNow;
            if (lastErrorPosted != null && now < lastErrorPosted.GetValueOrDefault().AddMinutes(ERROR_COOLDOWN_MINUTES))
            {
                LogMessage(String.Format("Waiting to post another error, as last message was sent recently at {0}", lastErrorPosted), Level.Warn);
                return;
            }
            else
            {
                lastErrorPosted = now;
                MemoryStream stream = new MemoryStream();
                SERIALIZER_ERROR_INFO.WriteObject(stream, errorInfo);
                string jsonString = Encoding.UTF8.GetString(stream.ToArray());
                PostJson(ENDPOINT_ERROR_INFO, jsonString);
            }

        }

        public void PostOngoingEvents(JObject info)
        {
            PostJson(ENDPOINT_ONGOING_EVENTS, info.ToString(Formatting.None));
        }

        public void PostEventEnded(JObject info)
        {
            PostJson(ENDPOINT_EVENT_ENDED, info.ToString(Formatting.None));
        }

        private void LogMessage(string message, Level logLevel)
        {
            messageFunction(message, logLevel);
        }
    }

    [DataContract]
    internal class MTGAAccount
    {
        [DataMember]
        internal string client_version;
        [DataMember]
        internal string token;
        [DataMember]
        internal string player_id;
        [DataMember]
        internal string screen_name;
        [DataMember]
        internal string raw_time;
    }
    [DataContract]
    internal class Pack
    {
        [DataMember]
        internal string client_version;
        [DataMember]
        internal string token;
        [DataMember]
        internal string player_id;
        [DataMember]
        internal string event_name;
        [DataMember]
        internal string time;
        [DataMember]
        internal int pack_number;
        [DataMember]
        internal int pick_number;
        [DataMember]
        internal List<int> card_ids;
        [DataMember]
        internal string utc_time;
    }
    [DataContract]
    internal class Pick
    {
        [DataMember]
        internal string client_version;
        [DataMember]
        internal string token;
        [DataMember]
        internal string player_id;
        [DataMember]
        internal string event_name;
        [DataMember]
        internal string time;
        [DataMember]
        internal int pack_number;
        [DataMember]
        internal int pick_number;
        [DataMember]
        internal int card_id;
        [DataMember]
        internal string utc_time;
    }
    [DataContract]
    internal class HumanDraftPick
    {
        [DataMember]
        internal string client_version;
        [DataMember]
        internal string token;
        [DataMember]
        internal string time;
        [DataMember]
        internal string utc_time;
        [DataMember]
        internal string player_id;
        [DataMember]
        internal string draft_id;
        [DataMember]
        internal string event_name;
        [DataMember]
        internal int pack_number;
        [DataMember]
        internal int pick_number;
        [DataMember]
        internal int card_id;
        [DataMember]
        internal bool auto_pick;
        [DataMember]
        internal float time_remaining;
    }
    [DataContract]
    internal class HumanDraftPack
    {
        [DataMember]
        internal string client_version;
        [DataMember]
        internal string token;
        [DataMember]
        internal string time;
        [DataMember]
        internal string utc_time;
        [DataMember]
        internal string player_id;
        [DataMember]
        internal string method;
        [DataMember]
        internal string draft_id;
        [DataMember]
        internal string event_name;
        [DataMember]
        internal int pack_number;
        [DataMember]
        internal int pick_number;
        [DataMember]
        internal List<int> card_ids;
    }
    [DataContract]
    internal class Deck
    {
        [DataMember]
        internal string client_version;
        [DataMember]
        internal string token;
        [DataMember]
        internal string player_id;
        [DataMember]
        internal string time;
        [DataMember]
        internal string event_name;
        [DataMember]
        internal List<int> maindeck_card_ids;
        [DataMember]
        internal List<int> sideboard_card_ids;
        [DataMember]
        internal int companion;
        [DataMember]
        internal bool is_during_match;
        [DataMember]
        internal string utc_time;
    }
    [DataContract]
    internal class Event
    {
        [DataMember]
        internal string client_version;
        [DataMember]
        internal string token;
        [DataMember]
        internal string player_id;
        [DataMember]
        internal string event_name;
        [DataMember]
        internal string time;
        [DataMember]
        internal string entry_fee;
        [DataMember]
        internal int wins;
        [DataMember]
        internal int losses;
        [DataMember]
        internal string utc_time;
    }
    [DataContract]
    internal class EventCourse
    {
        [DataMember]
        internal string client_version;
        [DataMember]
        internal string token;
        [DataMember]
        internal string time;
        [DataMember]
        internal string utc_time;
        [DataMember]
        internal string player_id;
        [DataMember]
        internal string draft_id;
        [DataMember]
        internal string event_name;
    }
    [DataContract]
    internal class ErrorInfo
    {
        [DataMember]
        internal string client_version;
        [DataMember]
        internal string token;
        [DataMember]
        internal string stacktrace;
        [DataMember]
        internal string blob;
        [DataMember]
        internal List<string> recent_lines;
    }
    [DataContract]
    internal class Collection
    {
        [DataMember]
        internal string client_version;
        [DataMember]
        internal string token;
        [DataMember]
        internal string player_id;
        [DataMember]
        internal string time;
        [DataMember]
        internal string utc_time;
        [DataMember]
        internal Dictionary<string, int> card_counts;
    }
}
