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

namespace mtga_log_client
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            ApiClient client = new ApiClient();
            var minimumVersion = client.GetMinimumApiVersion();
            Console.WriteLine(minimumVersion);

            /*
            MTGAAccount account = new MTGAAccount();
            account.client_version = "0.0.1-test";
            account.player_id = "12345";
            account.screen_name = "test-user";
            account.token = "1a2b3c";
            client.PostMTGAAccount(account);
            */

            LogParser parser = new LogParser(client,
                "d1c297f8ff8d4b75a9ce60691458486b",
                // "C:\\Users\\Rob\\AppData\\LocalLow\\Wizards Of The Coast\\MTGA\\output_log.txt");
                "E:\\output_log_simple.txt");
            parser.ParseLog();
        }
    }

    class LogParser
    {
        private const string CLIENT_VERSION = "0.1.2";

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
        private Dictionary<int, Dictionary<int, int>> objectsByOwner = new Dictionary<int, Dictionary<int, int>>();

        private ApiClient apiClient;
        private string apiToken;
        private string filePath;

        public LogParser(ApiClient apiClient, string apiToken, string filePath)
        {
            this.apiClient = apiClient;
            this.apiToken = apiToken;
            this.filePath = filePath;
        }

        public void ParseLog() {
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
                        while (true)
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
                Console.WriteLine("Error parsing log: {0}", e);
            }
        }

        private void ProcessLine(string line)
        {
            var match = LOG_START_REGEX_UNTIMED.Match(line);
            var match2 = LOG_START_REGEX_UNTIMED_2.Match(line);
            if (match.Success || match2.Success)
            {
                handleCompleteLogEntry();
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

        private string getDatetimeString(DateTime value)
        {
            return value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private void handleCompleteLogEntry()
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
                handleBlob(fullLog);
            }
            catch (Exception e)
            {
                Console.WriteLine("Error {0} while processing {1}", e, fullLog);
            }

            buffer.Clear();
            currentLogTime = null;
        }

        private long blobsProcessed = 0;
        private void handleBlob(string fullLog)
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

            if (++blobsProcessed % 100 == 0)
            {
                Console.WriteLine("Processed {0} blobs so far", blobsProcessed);
            }

            if (maybeHandleLogin(blob)) return;
            if (maybeHandleGameEnd(blob)) return;
            if (maybeHandleDraftLog(blob)) return;
            if (maybeHandleDraftPick(blob)) return;
            if (maybeHandleDeckSubmission(blob)) return;
        }

        private bool maybeHandleLogin(JObject blob)
        {
            try
            {
                JToken token;
                if (!blob.TryGetValue("params", out token)) return false;
                if (!token.Value<JObject>().TryGetValue("messageName", out token)) return false;
                if (!token.Value<String>().Equals("Client.Connected")) return false;

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
                return false;
            }
        }

        private bool maybeHandleGameEnd(JObject blob)
        {
            try
            {
                JToken token;
                if (!blob.TryGetValue("params", out token)) return false;
                if (!token.Value<JObject>().TryGetValue("messageName", out token)) return false;
                if (!token.Value<String>().Equals("DuelScene.GameStop")) return false;

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
                game.time = getDatetimeString(currentLogTime.Value);

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
                return false;
            }
        }

        private bool maybeHandleDraftLog(JObject blob)
        {
            try
            {
                if (!blob.ContainsKey("draftStatus")) return false;
                if (!"Draft.PickNext".Equals(blob["draftStatus"].Value<String>())) return false;

                Pack pack = new Pack();
                pack.token = apiToken;
                pack.client_version = CLIENT_VERSION;
                pack.player_id = currentUser;
                pack.time = getDatetimeString(currentLogTime.Value);

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
                return false;
            }
        }

        private bool maybeHandleDraftPick(JObject blob)
        {
            try
            {
                if (!blob.ContainsKey("method")) return false;
                if (!"Draft.MakePick".Equals(blob["method"].Value<String>())) return false;

                var parameters = blob["params"].Value<JObject>();
                var draftIdComponents = parameters["draftId"].Value<String>().Split(':');

                Pick pick = new Pick();
                pick.token = apiToken;
                pick.client_version = CLIENT_VERSION;
                pick.player_id = currentUser;
                pick.time = getDatetimeString(currentLogTime.Value);

                pick.event_name = draftIdComponents[1];
                pick.pack_number = parameters["packNumber"].Value<int>();
                pick.pick_number = parameters["pickNumber"].Value<int>();
                pick.card_id = parameters["cardId"].Value<int>();

                apiClient.PostPick(pick);

                return true;
            }
            catch (Exception e)
            {
                return false;
            }
        }

        private bool maybeHandleDeckSubmission(JObject blob)
        {
            try
            {
                if (!blob.ContainsKey("method")) return false;
                if (!"Event.DeckSubmit".Equals(blob["method"].Value<String>())) return false;

                Deck deck = new Deck();
                deck.token = apiToken;
                deck.client_version = CLIENT_VERSION;
                deck.player_id = currentUser;
                deck.time = getDatetimeString(currentLogTime.Value);

                var parameters = blob["params"].Value<JObject>();
                var deckInfo = JObject.Parse(parameters["deck"].Value<String>());

                var maindeckCardIds = new List<int>();
                foreach (JObject cardInfo in deckInfo["mainDeck"].Value<JArray>())
                {
                    maindeckCardIds.Add(cardInfo["id"].Value<int>());
                }

                var sideboardCardIds = new List<int>();
                foreach (JObject cardInfo in deckInfo["sideboard"].Value<JArray>())
                {
                    sideboardCardIds.Add(cardInfo["id"].Value<int>());
                }

                deck.event_name = parameters["eventName"].Value<String>();
                deck.maindeck_card_ids = maindeckCardIds;
                deck.sideboard_card_ids = sideboardCardIds;
                deck.is_during_match = false;

                apiClient.PostDeck(deck);
                return true;
            }
            catch (Exception e)
            {
                return false;
            }
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
        private const string ENDPOINT_MIN_CLIENT_VERSION = "min_client_version";

        private static readonly DataContractJsonSerializer SERIALIZER_MTGA_ACCOUNT = new DataContractJsonSerializer(typeof(MTGAAccount));
        private static readonly DataContractJsonSerializer SERIALIZER_PACK = new DataContractJsonSerializer(typeof(Pack));
        private static readonly DataContractJsonSerializer SERIALIZER_PICK = new DataContractJsonSerializer(typeof(Pick));
        private static readonly DataContractJsonSerializer SERIALIZER_DECK = new DataContractJsonSerializer(typeof(Deck));
        private static readonly DataContractJsonSerializer SERIALIZER_GAME = new DataContractJsonSerializer(typeof(Game));
        private static readonly DataContractJsonSerializer SERIALIZER_EVENT = new DataContractJsonSerializer(typeof(Event));

        private HttpClient client;

        [DataContract]
        internal class MinVersionResponse
        {
            [DataMember]
            internal string min_version;
        }

        public ApiClient()
        {
            initializeClient();
        }

        public void initializeClient()
        {
            client = new HttpClient();
            client.BaseAddress = new Uri(API_BASE_URL);
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public void stopClient()
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
                Console.WriteLine("Got error response {0} ({1})", (int) response.StatusCode, response.ReasonPhrase);
                return null;
            }
        }

        private void PostJson(string endpoint, String blob)
        {
            Console.WriteLine("Sending post to {0} of {1}", endpoint, blob);
            var content = new StringContent(blob, Encoding.UTF8, "application/json");
            var response = client.PostAsync(endpoint, content).Result;
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine("Got error response {0} ({1})", (int)response.StatusCode, response.ReasonPhrase);
            }
        }

        public string GetMinimumApiVersion()
        {
            var jsonResponse = GetJson(ENDPOINT_MIN_CLIENT_VERSION);
            if (jsonResponse == null)
            {
                return null;
            }
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(MinVersionResponse));
            return ((MinVersionResponse)serializer.ReadObject(jsonResponse)).min_version;
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
