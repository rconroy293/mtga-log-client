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

namespace mtga_log_client
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private static readonly string REQUIRED_FILENAME = "output_log.txt";
        private static readonly int REQUIRED_TOKEN_LENGTH = 32;
        private static readonly int MESSAGE_HISTORY = 500;

        private LogParser parser;
        private ApiClient client;
        BackgroundWorker worker;

        private bool isStarted = false;
        private string filePath = Path.Combine(@"E:\", "output_asdflog_simple.txt");
        private string userToken = "d1c297f8ff8d4b75a9ce60691458486b";
        private string downloadUrl = "https://github.com/rconroy293/mtga-log-client";

        public MainWindow()
        {
            InitializeComponent();

            LogFileTextBox.Text = filePath;
            ClientTokenTextBox.Text = userToken;

            client = new ApiClient(LogMessage);

            if (!ValidateClientVersion()) return;

            if (!ValidateUserInputs()) return;
            StartParser();
        }

        private bool ValidateClientVersion()
        {
            var versionValidation = client.GetVersionValidation();
            if (versionValidation.is_supported) return true;

            MessageBox.Show(
                "This version of the client is no longer supported. Please update.",
                "Outdated Client",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            System.Diagnostics.Process.Start(downloadUrl);
            Application.Current.Shutdown();
            return false;
        }

        private void StartParser()
        {
            if (worker != null && !worker.CancellationPending)
            {
                worker.CancelAsync();
            }

            parser = new LogParser(client, userToken, filePath, LogMessage);
            StartButton.IsEnabled = false;

            worker = new BackgroundWorker();
            worker.DoWork += parser.ResumeParsing;
            worker.WorkerSupportsCancellation = true;
            worker.RunWorkerAsync();

            isStarted = true;
        }

        private bool ValidateUserInputs()
        {
            if (!File.Exists(LogFileTextBox.Text) || !IsValidLogFile(LogFileTextBox.Text))
            {
                MessageBox.Show(
                    "You must choose a valid log file named " + REQUIRED_FILENAME,
                    "Choose Filename",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                filePath = ChooseLogFile();

                if (filePath == null)
                {
                    MessageBox.Show(
                        "You must enter a log file.",
                        "Choose Valid Log File",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return false;
                }
            }
            else
            {
                filePath = LogFileTextBox.Text;
            }

            if (!IsValidToken(ClientTokenTextBox.Text))
            {
                MessageBox.Show(
                    "You must enter a valid token from 17lands.com",
                    "Enter Valid Token",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }
            else
            {
                userToken = ClientTokenTextBox.Text;
            }

            return true;
        }

        private void ClientTokenTextBox_onTextChanged(object sender, EventArgs e)
        {
            EnableApply();
        }

        private void EnableApply()
        {
            ApplyButton.IsEnabled = true;
        }

        private bool IsValidToken(string clientToken)
        {
            return clientToken.Length == REQUIRED_TOKEN_LENGTH;
        }

        private void ChooseFile_onClick(object sender, RoutedEventArgs e)
        {
            string newFilename = ChooseLogFile();
            if (newFilename != null)
            {
                LogFileTextBox.Text = newFilename;
                ApplyButton.IsEnabled = true;
            }
        }

        private string ChooseLogFile()
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "Text files (*.txt)|*.txt";
            openFileDialog.InitialDirectory = filePath;

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
                        "You must choose a file named " + REQUIRED_FILENAME,
                        "Bad Filename",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }

            return null;
        }

        private bool IsValidLogFile(string filename)
        {
            return filename.EndsWith("\\" + REQUIRED_FILENAME);
        }

        private void ApplyChanges_onClick(object sender, EventArgs e)
        {
            ApplyChangedSettings();
        }

        private void ApplyChangedSettings()
        {
            if (!ValidateUserInputs()) return;
            StartButton.IsEnabled = false;

            filePath = LogFileTextBox.Text;
            userToken = ClientTokenTextBox.Text;

            StartParser();
            ApplyButton.IsEnabled = false;
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
                ApplyChangedSettings();
                return;
            }
        }

        private void LogMessage(string message)
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

    delegate void LogMessageFunction(string message);

    class LogParser
    {
        public const string CLIENT_VERSION = "0.1.0";
        public const string CLIENT_TYPE = "windows";

        private const int SLEEP_TIME = 1000;
        private const int BUFFER_SIZE = 65536;
        private static readonly Regex LOG_START_REGEX = new Regex(
            "^\\[(UnityCrossThreadLogger|Client GRE)\\]([\\d:/ -]+(AM|PM)?)");
        private static readonly Regex LOG_START_REGEX_UNTIMED = new Regex(
            "^\\[(UnityCrossThreadLogger|Client GRE)\\]");
        private static readonly Regex LOG_START_REGEX_UNTIMED_2 = new Regex(
            "^\\(Filename:");
        private static readonly Regex JSON_DICT_REGEX = new Regex("\\{.+\\}");
        private static readonly Regex JSON_LIST_REGEX = new Regex("\\[.+\\]");

        private bool first = true;
        private long farthestReadPosition = 0;
        private List<string> buffer = new List<string>();
        private Nullable<DateTime> currentLogTime = null;
        private string currentUser = null;
        private readonly Dictionary<int, Dictionary<int, int>> objectsByOwner = new Dictionary<int, Dictionary<int, int>>();

        private readonly ApiClient apiClient;
        private readonly string apiToken;
        private readonly string filePath;
        private readonly LogMessageFunction messageFunction;

        public LogParser(ApiClient apiClient, string apiToken, string filePath, LogMessageFunction messageFunction)
        {
            this.apiClient = apiClient;
            this.apiToken = apiToken;
            this.filePath = filePath;
            this.messageFunction = messageFunction;
        }

        public void ResumeParsing(object sender, DoWorkEventArgs e)
        {
            LogMessage("Starting parsing of " + filePath);
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
                    if (first || filestream.Length < farthestReadPosition)
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
                                break;
                            }
                            ProcessLine(line);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                LogMessage(String.Format("Error parsing log: {0}", e));
            }
        }

        private void ProcessLine(string line)
        {
            var match = LOG_START_REGEX_UNTIMED.Match(line);
            var match2 = LOG_START_REGEX_UNTIMED_2.Match(line);
            if (match.Success || match2.Success)
            {
                HandleCompleteLogEntry();
                var timedMatch = LOG_START_REGEX.Match(line);
                if (timedMatch.Success)
                {
                    currentLogTime = DateTime.Parse(timedMatch.Groups[2].Value);
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

            var fullLog = String.Join("", buffer);
            try
            {
                HandleBlob(fullLog);
            }
            catch (Exception e)
            {
                LogMessage(String.Format("Error {0} while processing {1}", e, fullLog));
            }

            buffer.Clear();
            currentLogTime = null;
        }

        private void HandleBlob(string fullLog)
        {
            var dictMatch = JSON_DICT_REGEX.Match(fullLog);
            if (!dictMatch.Success)
            {
                return;
            }

            var listMatch = JSON_LIST_REGEX.Match(fullLog);
            if (listMatch.Success && listMatch.Value.Length > dictMatch.Value.Length)
            {
                return;
            }

            var blob = JObject.Parse(dictMatch.Value);

            if (MaybeHandleLogin(blob)) return;
            if (MaybeHandleGameEnd(blob)) return;
            if (MaybeHandleDraftLog(blob)) return;
            if (MaybeHandleDraftPick(blob)) return;
            if (MaybeHandleDeckSubmission(blob)) return;
            if (MaybeHandleDeckSubmissionV3(blob)) return;
            if (MaybeHandleEventCompletion(blob)) return;
            if (MaybeHandleGreToClientMessages(blob)) return;
        }

        private bool MaybeHandleLogin(JObject blob)
        {
            JToken token;
            if (!blob.TryGetValue("params", out token)) return false;
            if (!token.Value<JObject>().TryGetValue("messageName", out token)) return false;
            if (!token.Value<String>().Equals("Client.Connected")) return false;

            try
            {
                var payload = blob["params"]["payloadObject"];

                currentUser = payload["playerId"].Value<String>();
                var screenName = payload["screenName"].Value<String>();

                MTGAAccount account = new MTGAAccount();
                account.token = apiToken;
                account.client_version = CLIENT_VERSION;
                account.player_id = currentUser;

                account.screen_name = screenName;
                apiClient.PostMTGAAccount(account);

                return true;
            }
            catch (Exception e)
            {
                LogMessage(String.Format("Error {0} parsing login from {1}", e, blob));
                return false;
            }
        }

        private bool MaybeHandleGameEnd(JObject blob)
        {
            JToken token;
            if (!blob.TryGetValue("params", out token)) return false;
            if (!token.Value<JObject>().TryGetValue("messageName", out token)) return false;
            if (!token.Value<String>().Equals("DuelScene.GameStop")) return false;

            try
            {
                var payload = blob["params"]["payloadObject"];

                var opponentId = payload["seatId"].Value<int>() == 1 ? 2 : 1;
                var opponentCardIds = new List<int>();
                if (objectsByOwner.ContainsKey(opponentId))
                {
                    foreach(KeyValuePair<int, int> entry in objectsByOwner[opponentId])
                    {
                        opponentCardIds.Add(entry.Value);
                    }
                }
                objectsByOwner.Clear();

                var mulligans = new List<List<int>>();
                foreach (JArray hand in payload["mulliganedHands"].Value<JArray>())
                {
                    var mulliganHand = new List<int>();
                    foreach (JObject card in hand)
                    {
                        mulliganHand.Add(card["grpId"].Value<int>());
                    }
                    mulligans.Add(mulliganHand);
                }

                Game game = new Game();
                game.token = apiToken;
                game.client_version = CLIENT_VERSION;
                game.player_id = currentUser;
                game.time = GetDatetimeString(currentLogTime.Value);

                game.event_name = payload["eventId"].Value<string>();
                game.match_id = payload["matchId"].Value<string>();
                game.on_play = payload["teamId"].Value<int>() == payload["startingTeamId"].Value<int>();
                game.won = payload["teamId"].Value<int>() == payload["winningTeamId"].Value<int>();
                game.game_end_reason = payload["winningReason"].Value<string>();
                game.mulligans = mulligans;
                game.turns = payload["turnCount"].Value<int>();
                game.duration = payload["secondsCount"].Value<int>();
                game.opponent_card_ids = opponentCardIds;

                apiClient.PostGame(game);

                return true;
            }
            catch (Exception e)
            {
                LogMessage(String.Format("Error {0} parsing game result from {1}", e, blob));
                return false;
            }
        }

        private bool MaybeHandleDraftLog(JObject blob)
        {
            if (!blob.ContainsKey("draftStatus")) return false;
            if (!"Draft.PickNext".Equals(blob["draftStatus"].Value<String>())) return false;

            try
            {
                Pack pack = new Pack();
                pack.token = apiToken;
                pack.client_version = CLIENT_VERSION;
                pack.player_id = currentUser;
                pack.time = GetDatetimeString(currentLogTime.Value);

                var cardIds = new List<int>();
                foreach (JToken cardString in blob["draftPack"].Value<JArray>())
                {
                    cardIds.Add(int.Parse(cardString.Value<String>()));
                }

                pack.event_name = blob["eventName"].Value<String>();
                pack.pack_number = blob["packNumber"].Value<int>();
                pack.pick_number = blob["pickNumber"].Value<int>();
                pack.card_ids = cardIds;

                apiClient.PostPack(pack);
                return true;
            }
            catch (Exception e)
            {
                LogMessage(String.Format("Error {0} parsing draft pack from {1}", e, blob));
                return false;
            }
        }

        private bool MaybeHandleDraftPick(JObject blob)
        {
            if (!blob.ContainsKey("method")) return false;
            if (!"Draft.MakePick".Equals(blob["method"].Value<String>())) return false;

            try
            {
                var parameters = blob["params"].Value<JObject>();
                var draftIdComponents = parameters["draftId"].Value<String>().Split(':');

                Pick pick = new Pick();
                pick.token = apiToken;
                pick.client_version = CLIENT_VERSION;
                pick.player_id = currentUser;
                pick.time = GetDatetimeString(currentLogTime.Value);

                pick.event_name = draftIdComponents[1];
                pick.pack_number = parameters["packNumber"].Value<int>();
                pick.pick_number = parameters["pickNumber"].Value<int>();
                pick.card_id = parameters["cardId"].Value<int>();

                apiClient.PostPick(pick);

                return true;
            }
            catch (Exception e)
            {
                LogMessage(String.Format("Error {0} parsing draft pick from {1}", e, blob));
                return false;
            }
        }

        private bool MaybeHandleDeckSubmission(JObject blob)
        {
            if (!blob.ContainsKey("method")) return false;
            if (!"Event.DeckSubmit".Equals(blob["method"].Value<String>())) return false;

            try
            {
                objectsByOwner.Clear();

                Deck deck = new Deck();
                deck.token = apiToken;
                deck.client_version = CLIENT_VERSION;
                deck.player_id = currentUser;
                deck.time = GetDatetimeString(currentLogTime.Value);

                var parameters = blob["params"].Value<JObject>();
                var deckInfo = JObject.Parse(parameters["deck"].Value<String>());

                var maindeckCardIds = GetCardIdsFromDeck(deckInfo["mainDeck"].Value<JArray>());
                var sideboardCardIds = GetCardIdsFromDeck(deckInfo["sideboard"].Value<JArray>());

                deck.event_name = parameters["eventName"].Value<String>();
                deck.maindeck_card_ids = maindeckCardIds;
                deck.sideboard_card_ids = sideboardCardIds;
                deck.is_during_match = false;

                apiClient.PostDeck(deck);
                return true;
            }
            catch (Exception e)
            {
                LogMessage(String.Format("Error {0} parsing deck submission from {1}", e, blob));
                return false;
            }
        }

        private bool MaybeHandleDeckSubmissionV3(JObject blob)
        {
            if (!blob.ContainsKey("method")) return false;
            if (!"Event.DeckSubmitV3".Equals(blob["method"].Value<String>())) return false;

            try
            {
                objectsByOwner.Clear();

                Deck deck = new Deck();
                deck.token = apiToken;
                deck.client_version = CLIENT_VERSION;
                deck.player_id = currentUser;
                deck.time = GetDatetimeString(currentLogTime.Value);

                var parameters = blob["params"].Value<JObject>();
                var deckInfo = JObject.Parse(parameters["deck"].Value<String>());

                var maindeckCardIds = GetCardIdsFromDecklistV3(deckInfo["mainDeck"].Value<JArray>());
                var sideboardCardIds = GetCardIdsFromDecklistV3(deckInfo["sideboard"].Value<JArray>());

                deck.event_name = parameters["eventName"].Value<String>();
                deck.maindeck_card_ids = maindeckCardIds;
                deck.sideboard_card_ids = sideboardCardIds;
                deck.is_during_match = false;

                apiClient.PostDeck(deck);
                return true;
            }
            catch (Exception e)
            {
                LogMessage(String.Format("Error {0} parsing v3 deck submission from {1}", e, blob));
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

                event_.event_name = blob["InternalEventName"].Value<String>();
                event_.entry_fee = blob["ModuleInstanceData"]["HasPaidEntry"].Value<String>();
                event_.wins = blob["ModuleInstanceData"]["WinLossGate"]["CurrentWins"].Value<int>();
                event_.losses = blob["ModuleInstanceData"]["WinLossGate"]["CurrentLosses"].Value<int>();

                apiClient.PostEvent(event_);
                return true;
            }
            catch (Exception e)
            {
                LogMessage(String.Format("Error {0} parsing event completion from {1}", e, blob));
                return false;
            }
        }

        private bool MaybeHandleGreMessage_DeckSubmission(JToken blob)
        {
            if (!"GREMessageType_SubmitDeckReq".Equals(blob["type"].Value<string>())) return false;

            try
            {
                objectsByOwner.Clear();

                Deck deck = new Deck();
                deck.token = apiToken;
                deck.client_version = CLIENT_VERSION;
                deck.player_id = currentUser;
                deck.time = GetDatetimeString(currentLogTime.Value);

                deck.event_name = null;
                deck.maindeck_card_ids = JArrayToIntList(blob["submitDeckReq"]["deck"]["deckCards"].Value<JArray>());
                deck.sideboard_card_ids = JArrayToIntList(blob["submitDeckReq"]["deck"]["sideboardCards"].Value<JArray>());
                deck.is_during_match = true;

                apiClient.PostDeck(deck);
                return true;
            }
            catch (Exception e)
            {
                LogMessage(String.Format("Error {0} parsing GRE deck submission from {1}", e, blob));
                return false;
            }
        }

        private bool MaybeHandleGreToClientMessages(JObject blob)
        {
            if (!blob.ContainsKey("greToClientEvent")) return false;
            if (!blob["greToClientEvent"].Value<JObject>().ContainsKey("greToClientMessages")) return false;

            try
            {
                foreach (JToken message in blob["greToClientEvent"]["greToClientMessages"])
                {
                    if (MaybeHandleGreMessage_DeckSubmission(message)) continue;
                }
                return true;
            }
            catch (Exception e)
            {
                LogMessage(String.Format("Error {0} parsing event completion from {1}", e, blob));
                return false;
            }
        }

        private void LogMessage(string message)
        {
            messageFunction(message);
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
                int cardId;
                if (cardInfo.ContainsKey("id"))
                {
                    cardId = cardInfo["id"].Value<int>();
                }
                else
                {
                    cardId = cardInfo["Id"].Value<int>();
                }

                for (int i = 0; i < cardInfo["Quantity"].Value<int>(); i++)
                {
                    cardIds.Add(cardId);
                }
            }
            return cardIds;
        }

        private List<int> GetCardIdsFromDecklistV3(JArray decklist)
        {
            var cardIds = new List<int>();
            for (int i = 0; i < decklist.Count / 2; i++)
            {
                var cardId = decklist[2 * i].Value<int>();
                var count = decklist[2 * i + 1].Value<int>();
                for (int j = 0; j < count; j++)
                {
                    cardIds.Add(cardId);
                }
            }
            return cardIds;
        }

    }

    class ApiClient
    {
        private const string API_BASE_URL = "https://www.17lands.com";
        private const string ENDPOINT_ACCOUNT = "api/account";
        private const string ENDPOINT_DECK = "deck";
        private const string ENDPOINT_EVENT = "event";
        private const string ENDPOINT_GAME = "game";
        private const string ENDPOINT_PACK = "pack";
        private const string ENDPOINT_PICK = "pick";
        private const string ENDPOINT_CLIENT_VERSION_VALIDATION = "api/version_validation";

        private static readonly DataContractJsonSerializer SERIALIZER_MTGA_ACCOUNT = new DataContractJsonSerializer(typeof(MTGAAccount));
        private static readonly DataContractJsonSerializer SERIALIZER_PACK = new DataContractJsonSerializer(typeof(Pack));
        private static readonly DataContractJsonSerializer SERIALIZER_PICK = new DataContractJsonSerializer(typeof(Pick));
        private static readonly DataContractJsonSerializer SERIALIZER_DECK = new DataContractJsonSerializer(typeof(Deck));
        private static readonly DataContractJsonSerializer SERIALIZER_GAME = new DataContractJsonSerializer(typeof(Game));
        private static readonly DataContractJsonSerializer SERIALIZER_EVENT = new DataContractJsonSerializer(typeof(Event));

        private HttpClient client;
        private readonly LogMessageFunction messageFunction;

        [DataContract]
        public class VersionValidationResponse
        {
            [DataMember]
            internal bool is_supported;
            [DataMember]
            internal string latest_version;
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
                LogMessage(String.Format("Got error response {0} ({1})", (int) response.StatusCode, response.ReasonPhrase));
                return null;
            }
        }

        private void PostJson(string endpoint, String blob)
        {
            LogMessage(String.Format("Posting {0} of {1}", endpoint, blob));
            var content = new StringContent(blob, Encoding.UTF8, "application/json");
            var response = client.PostAsync(endpoint, content).Result;
            if (!response.IsSuccessStatusCode)
            {
                LogMessage(String.Format("Got error response {0} ({1})", (int)response.StatusCode, response.ReasonPhrase));
            }
        }

        public VersionValidationResponse GetVersionValidation()
        {
            var jsonResponse = GetJson(ENDPOINT_CLIENT_VERSION_VALIDATION + "?client=" + LogParser.CLIENT_TYPE + "&version=" + LogParser.CLIENT_VERSION);
            if (jsonResponse == null)
            {
                return null;
            }
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(VersionValidationResponse));
            return ((VersionValidationResponse)serializer.ReadObject(jsonResponse));
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

        public void PostDeck(Deck deck)
        {
            MemoryStream stream = new MemoryStream();
            SERIALIZER_DECK.WriteObject(stream, deck);
            string jsonString = Encoding.UTF8.GetString(stream.ToArray());
            PostJson(ENDPOINT_DECK, jsonString);
        }

        public void PostGame(Game game)
        {
            MemoryStream stream = new MemoryStream();
            SERIALIZER_GAME.WriteObject(stream, game);
            string jsonString = Encoding.UTF8.GetString(stream.ToArray());
            PostJson(ENDPOINT_GAME, jsonString);
        }

        public void PostEvent(Event event_)
        {
            MemoryStream stream = new MemoryStream();
            SERIALIZER_EVENT.WriteObject(stream, event_);
            string jsonString = Encoding.UTF8.GetString(stream.ToArray());
            PostJson(ENDPOINT_EVENT, jsonString);
        }

        private void LogMessage(string message)
        {
            messageFunction(message);
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
        internal bool is_during_match;
    }
    [DataContract]
    internal class Game
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
        internal string match_id;
        [DataMember]
        internal string time;
        [DataMember]
        internal bool on_play;
        [DataMember]
        internal bool won;
        [DataMember]
        internal string game_end_reason;
        [DataMember]
        internal List<List<int>> mulligans;
        [DataMember]
        internal int turns;
        [DataMember]
        internal int duration;
        [DataMember]
        internal List<int> opponent_card_ids;
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
    }
}
