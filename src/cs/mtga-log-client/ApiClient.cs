﻿using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using System.Threading;
using log4net.Core;
using Newtonsoft.Json;
using System.IO.Compression;

namespace mtga_log_client
{
    class ApiClient
    {
        private const string API_BASE_URL = "https://api.17lands.com";
        private const string ENDPOINT_ACCOUNT = "/api/client/add_mtga_account"; // Formerly "/api/account"
        private const string ENDPOINT_COLLECTION = "/api/client/update_card_collection"; // Formerly "/collection"
        private const string ENDPOINT_DECK = "/api/client/add_deck"; // Formerly "/deck"
        private const string ENDPOINT_EVENT = "/api/client/add_event"; // Formerly "/event"
        private const string ENDPOINT_EVENT_COURSE = "/api/client/update_event_course"; // Formerly "/event_course"
        private const string ENDPOINT_GAME = "/api/client/add_game"; // Formerly "/game"
        private const string ENDPOINT_INVENTORY = "/api/client/update_inventory"; // Formerly "/inventory"
        private const string ENDPOINT_PACK = "/api/client/add_pack"; // Formerly "/pack"
        private const string ENDPOINT_PICK = "/api/client/add_pick"; // Formerly "/pick"
        private const string ENDPOINT_PLAYER_PROGRESS = "/api/client/update_player_progress"; // Formerly "/player_progress"
        private const string ENDPOINT_HUMAN_DRAFT_PICK = "/api/client/add_human_draft_pick"; // Formerly "/human_draft_pick"
        private const string ENDPOINT_HUMAN_DRAFT_PACK = "/api/client/add_human_draft_pack"; // Formerly "/human_draft_pack"
        private const string ENDPOINT_CLIENT_VERSION_VALIDATION = "/api/client/client_version_validation"; // Formerly "/api/version_validation"
        private const string ENDPOINT_TOKEN_VERSION_VALIDATION = "/api/client/token_validation"; // Formerly "/api/token_validation"
        private const string ENDPOINT_ERROR_INFO = "/api/client/log_errors"; // Formerly "/api/client_errors"
        private const string ENDPOINT_RANK = "/api/client/add_rank"; // Formerly "/api/rank"
        private const string ENDPOINT_ONGOING_EVENTS = "/api/client/update_ongoing_events"; // Formerly "/ongoing_events"
        private const string ENDPOINT_JOIN_EVENT = "/api/client/record_event_join";
        private const string ENDPOINT_EVENT_ENDED = "/api/client/mark_event_ended"; // Formerly "/event_ended"
        private const string ENDPOINT_TIME_FORMATS = "/api/client/get_client_time_formats"; // Formerly "/data/client_time_formats"

        private HttpClient client;
        private readonly LogMessageFunction messageFunction;

        private const int ERROR_COOLDOWN_MINUTES = 2;
        private DateTime? lastErrorPosted = null;

        private TimeSpan INITIAL_RETRY_DELAY = TimeSpan.FromSeconds(1);
        private TimeSpan MAX_RETRY_DELAY = TimeSpan.FromMinutes(10);
        private TimeSpan MAX_TOTAL_RETRY_DURATION = TimeSpan.FromHours(24);

        [DataContract]
        public class VersionValidationResponse
        {
            [DataMember]
            internal bool is_supported;
            [DataMember]
            internal string latest_version;
        }

        [DataContract]
        public class TimeFormatsResponse
        {
            [DataMember]
            internal List<string> formats;
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

        T RetryApiCall<T>(Func<T> callback, Func<T, bool> isValidResponse)
        {
            bool shouldRetryError (Exception e)
            {
                LogMessage(String.Format("Error {0}\n{1}", e, e.StackTrace), Level.Warn);

                if (e is HttpRequestException)
                {
                    return true;
                }
                if (e is AggregateException)
                {
                    return e.InnerException is HttpRequestException;
                }

                return false;
            }

            return RetryUtils.RetryUntilSuccessful(
                callback,
                isValidResponse,
                shouldRetryError,
                INITIAL_RETRY_DELAY,
                MAX_RETRY_DELAY,
                MAX_TOTAL_RETRY_DURATION
            );
        }

        private Stream GetJson(string endpoint)
        {
            HttpResponseMessage sendRequest()
            {
                return client.GetAsync(endpoint).Result;
            }

            bool isValidResponse(HttpResponseMessage response)
            {
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
                else
                {
                    LogMessage(String.Format("Got error response {0} ({1})", (int)response.StatusCode, response.ReasonPhrase), Level.Warn);
                    return false;
                }
            }

            try
            {
                HttpResponseMessage successfulResponse = RetryApiCall(sendRequest, isValidResponse);
                return successfulResponse.Content.ReadAsStreamAsync().Result;
            } catch (RetryLimitExceededException e)
            {
                return null;
            }
        }

        private void PostJson(string endpoint, JObject blob, bool useGzip = false, bool skipLogging = false)
        {
            String stringifiedBlob = blob.ToString(Formatting.None);
            if (!skipLogging)
            {
                LogMessage(message: $"Posting {endpoint} of {stringifiedBlob}", Level.Info);
            }

            HttpResponseMessage sendRequest()
            {
                if (useGzip)
                {
                    using (var stream = new MemoryStream())
                    {
                        byte[] encoded = Encoding.UTF8.GetBytes(stringifiedBlob);
                        using (var compressedStream = new GZipStream(stream, CompressionMode.Compress, true))
                        {
                            compressedStream.Write(encoded, 0, encoded.Length);
                        }

                        stream.Position = 0;
                        HttpContent content = new StreamContent(stream);
                        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                        content.Headers.ContentEncoding.Add("gzip");
                        content.Headers.ContentLength = stream.Length;
                        return client.PostAsync(endpoint, content).Result;
                    }
                }
                else
                {
                    HttpContent content = new StringContent(stringifiedBlob, Encoding.UTF8, "application/json");
                    return client.PostAsync(endpoint, content).Result;
                }
            }

            bool validateResponse(HttpResponseMessage response)
            {
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
                else
                {
                    LogMessage(String.Format("Got error response {0} ({1})", (int)response.StatusCode, response.ReasonPhrase), Level.Warn);
                    return false;
                }
            }

            try
            {
                RetryApiCall(sendRequest, validateResponse);
            }
            catch (RetryLimitExceededException e)
            {
                LogMessage(String.Format("Exceeded retry limit posting to {0}. Skipping.", endpoint), Level.Error);
            }
        }

        public VersionValidationResponse GetVersionValidation()
        {
            var jsonResponse = GetJson(ENDPOINT_CLIENT_VERSION_VALIDATION
                + "?client=" + LogParser.CLIENT_TYPE + "&version="
                + LogParser.CLIENT_VERSION.TrimEnd(new char[] { '.', 'w' }));
            if (jsonResponse == null)
            {
                return null;
            }
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(VersionValidationResponse));
            return ((VersionValidationResponse)serializer.ReadObject(jsonResponse));
        }

        public List<string> GetTimeFormats()
        {
            var jsonResponse = GetJson(ENDPOINT_TIME_FORMATS + "?client=" + LogParser.CLIENT_TYPE);
            if (jsonResponse == null)
            {
                return null;
            }
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(TimeFormatsResponse));
            return ((TimeFormatsResponse)serializer.ReadObject(jsonResponse)).formats;
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

        public void PostMTGAAccount(JObject account)
        {
            PostJson(ENDPOINT_ACCOUNT, account);
        }

        public void PostPack(JObject pack)
        {
            PostJson(ENDPOINT_PACK, pack);
        }

        public void PostPick(JObject pick)
        {
            PostJson(ENDPOINT_PICK, pick);
        }

        public void PostHumanDraftPick(JObject pick)
        {
            PostJson(ENDPOINT_HUMAN_DRAFT_PICK, pick);
        }

        public void PostHumanDraftPack(JObject pack)
        {
            PostJson(ENDPOINT_HUMAN_DRAFT_PACK, pack);
        }

        public void PostDeck(JObject deck)
        {
            PostJson(ENDPOINT_DECK, deck);
        }

        public void PostGame(JObject game)
        {
            PostJson(ENDPOINT_GAME, game, useGzip: true, skipLogging: true);
        }

        public void PostEvent(JObject event_)
        {
            PostJson(ENDPOINT_EVENT, event_);
        }

        public void PostEventCourse(JObject eventCourse)
        {
            PostJson(ENDPOINT_EVENT_COURSE, eventCourse);
        }

        public void PostCollection(JObject collection)
        {
            PostJson(ENDPOINT_COLLECTION, collection);
        }

        public void PostInventory(JObject inventory)
        {
            PostJson(ENDPOINT_INVENTORY, inventory);
        }

        public void PostPlayerProgress(JObject progress)
        {
            PostJson(ENDPOINT_PLAYER_PROGRESS, progress);
        }

        public void PostRank(JObject rank)
        {
            PostJson(ENDPOINT_RANK, rank);
        }

        public void PostErrorInfo(JObject errorInfo)
        {
            DateTime now = DateTime.UtcNow;
            if (lastErrorPosted != null && now < lastErrorPosted.GetValueOrDefault().AddMinutes(ERROR_COOLDOWN_MINUTES))
            {
                LogMessage(String.Format("Waiting to post another error, as last message was sent recently at {0}", lastErrorPosted), Level.Warn);
                return;
            }

            lastErrorPosted = now;
            PostJson(ENDPOINT_ERROR_INFO, errorInfo, useGzip: true, skipLogging: true);
        }

        public void PostOngoingEvents(JObject events)
        {
            PostJson(ENDPOINT_ONGOING_EVENTS, events, useGzip: true);
        }

        public void PostEventJoined(JObject joinedEvent)
        {
            PostJson(ENDPOINT_JOIN_EVENT, joinedEvent);
        }

        public void PostEventEnded(JObject eventEnded)
        {
            PostJson(ENDPOINT_EVENT_ENDED, eventEnded);
        }

        private void LogMessage(string message, Level logLevel)
        {
            messageFunction(message, logLevel);
        }
    }
}
