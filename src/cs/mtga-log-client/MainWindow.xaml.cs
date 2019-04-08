using System;
using System.Windows;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.IO;
using System.Collections.Generic;
using System.Text;

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

            MTGAAccount account = new MTGAAccount();
            account.client_version = "0.0.1-test";
            account.player_id = "12345";
            account.screen_name = "test-user";
            account.token = "1a2b3c";
            // client.PostMTGAAccount(account);
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

        private DataContractJsonSerializer SERIALIZER_MTGA_ACCOUNT = new DataContractJsonSerializer(typeof(MTGAAccount));
        private DataContractJsonSerializer SERIALIZER_PACK = new DataContractJsonSerializer(typeof(Pack));
        private DataContractJsonSerializer SERIALIZER_PICK = new DataContractJsonSerializer(typeof(Pick));
        private DataContractJsonSerializer SERIALIZER_DECK = new DataContractJsonSerializer(typeof(Deck));
        private DataContractJsonSerializer SERIALIZER_GAME = new DataContractJsonSerializer(typeof(Game));
        private DataContractJsonSerializer SERIALIZER_EVENT = new DataContractJsonSerializer(typeof(Event));

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
