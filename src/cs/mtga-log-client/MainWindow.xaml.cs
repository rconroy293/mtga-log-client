using System;
using System.Windows;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.IO;

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
        }
    }

    class ApiClient
    {
        private const string API_BASE_URL = "https://www.17lands.com";
        private const string ENDPOINT_MIN_VERSION = "min_client_version";

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
            HttpResponseMessage response = client.GetAsync(ENDPOINT_MIN_VERSION).Result;
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

        public string GetMinimumApiVersion()
        {
            var jsonResponse = GetJson(ENDPOINT_MIN_VERSION);
            if (jsonResponse == null)
            {
                return null;
            }
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(MinVersionResponse));
            return ((MinVersionResponse)serializer.ReadObject(jsonResponse)).min_version;
        }
    }
}
