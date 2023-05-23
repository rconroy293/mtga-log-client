using System;
using System.Windows;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System.Threading;
using System.ComponentModel;
using log4net.Core;
using Newtonsoft.Json;
using System.Linq;

namespace mtga_log_client
{
    class LogParser
    {
        public const string CLIENT_VERSION = "0.2.1.2.w";
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

        private List<string> timeFormats = new List<string>() {
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
        private List<int> currentGameMaindeck = new List<int>();
        private List<int> currentGameSideboard = new List<int>();
        private JObject currentGameAdditionalDeckInfo = new JObject();
        private int startingTeamId = -1;
        private int seatId = 0;
        private int turnCount = 0;
        private JObject pendingGameSubmission = null;
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
        private readonly LogMessageFunction messageFunction;
        private readonly UpdateStatusFunction statusFunction;

        public LogParser(ApiClient apiClient, string apiToken, string filePath, LogMessageFunction messageFunction, UpdateStatusFunction statusFunction)
        {
            this.apiClient = apiClient;
            this.apiToken = apiToken;
            this.filePath = filePath;
            this.messageFunction = messageFunction;
            this.statusFunction = statusFunction;
        }

        public void ResumeParsing(object sender, DoWorkEventArgs e)
        {
            LogMessage("Starting parsing of " + filePath, Level.Info);
            BackgroundWorker worker = sender as BackgroundWorker;

            updateTimeFormats();

            while (!worker.CancellationPending)
            {
                ParseRemainderOfLog(worker);
                Thread.Sleep(SLEEP_TIME);
            }
        }

        void updateTimeFormats()
        {
            try
            {
                List<string> updatedFormats = apiClient.GetTimeFormats();
                if (updatedFormats != null)
                {
                    LogMessage(String.Format("Updating to use {0} time formats", updatedFormats.Count), Level.Info);
                    timeFormats = updatedFormats;
                }
            }
            catch (Exception)
            {

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
                        if (!first)
                        {
                            LogMessage("Restarting parsing of " + filePath, Level.Info);
                        }
                        ClearMatchData();
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
                LogMessage(String.Format("File not found error while parsing log. If this message persists, please email support@17lands.com: {0}", e), Level.Warn);
            }
            catch (IOException e)
            {
                LogMessage(String.Format("File access error while parsing log. If this message persists, please email support@17lands.com: {0}", e), Level.Warn);
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
            foreach (string format in timeFormats)
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
            if (MaybeHandleEventCourse(fullLog, blob)) return;
            if (MaybeHandleScreenNameUpdate(fullLog, blob)) return;
            if (MaybeHandleMatchStateChanged(blob)) return;
            if (MaybeHandleGreToClientMessages(blob)) return;
            if (MaybeHandleClientToGreMessage(blob)) return;
            if (MaybeHandleClientToGreUiMessage(blob)) return;
            if (MaybeHandleSelfRankInfo(fullLog, blob)) return;
            if (MaybeHandleInventory(fullLog, blob)) return;
            if (MaybeHandlePlayerProgress(fullLog, blob)) return;
            if (MaybeHandleFrontDoorConnectionClose(fullLog, blob)) return;
            if (MaybeHandleReconnectResult(fullLog, blob)) return;

            if (pendingGameSubmission != null)
            {
                apiClient.PostGame(pendingGameSubmission);
                ClearGameData();
                pendingGameSubmission = null;
            }
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
            currentGameMaindeck = new List<int>();
            currentGameSideboard = new List<int>();
            currentGameAdditionalDeckInfo = new JObject();
        }

        private void ClearMatchData()
        {
            screenNames.Clear();
            currentMatchId = null;
            currentEventName = null;
            seatId = 0;
            ClearGameData();
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

            var account = CreateObjectWithBaseData();
            account.Add("screen_name", currentScreenName);
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

        private bool SendHandleGameEnd(JArray results)
        {
            if (!HasPendingGameData()) return false;

            try
            {
                var gameResults = new List<JObject>();
                var matchResult = new JObject();
                for (int i = 0; i < results.Count; i++)
                {
                    var result = results[i].Value<JObject>();
                    var scope = result["scope"]?.Value<String>();
                    if (scope == "MatchScope_Game")
                    {
                        gameResults.Add(result);
                    }
                    else if (scope == "MatchScope_Match")
                    {
                        matchResult = result;
                    }

                }

                if (gameResults.Count == 0)
                {
                    return true;
                }
                var thisGameResult = gameResults.Last();
                var won = seatId.Equals(thisGameResult["winningTeamId"]?.Value<int>());
                var gameNumber = gameResults.Count;
                var winType = thisGameResult["result"].Value<String>();
                var gameEndReason = thisGameResult["reason"].Value<String>();

                var wonMatch = seatId.Equals(matchResult["winningTeamId"]?.Value<int>());
                var matchResultType = matchResult["result"]?.Value<String>();
                var matchEndReason = matchResult["reason"]?.Value<String>();

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

                var game = CreateObjectWithBaseData();
                game.Add("event_name", currentEventName);
                game.Add("match_id", currentMatchId);
                game.Add("game_number", gameNumber);
                game.Add("on_play", seatId.Equals(startingTeamId));
                game.Add("won", won);
                game.Add("win_type", winType);
                game.Add("game_end_reason", gameEndReason);
                game.Add("won_match", wonMatch);
                game.Add("match_result_type", matchResultType);
                game.Add("match_end_reason", matchEndReason);


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
                game.Add("turns", turnCount);
                game.Add("limited_rank", currentLimitedLevel);
                game.Add("constructed_rank", currentConstructedLevel);
                game.Add("opponent_rank", currentOpponentLevel);
                game.Add("duration", -1);
                game.Add("opponent_card_ids", JToken.FromObject(opponentCardIds));

                game.Add("maindeck_card_ids", JToken.FromObject(currentGameMaindeck));
                game.Add("sideboard_card_ids", JToken.FromObject(currentGameSideboard));
                game.Add("additional_deck_info", currentGameAdditionalDeckInfo);

                LogMessage(String.Format("Posting game of {0}", game.ToString(Formatting.None)), Level.Info);
                LogMessage(String.Format("Including game history of {0} events", gameHistoryEvents.Count()), Level.Info);
                JObject history = new JObject
                {
                    { "seat_id", seatId },
                    { "opponent_seat_id", opponentId },
                    { "screen_name", screenNames[seatId] },
                    { "opponent_screen_name", screenNames[opponentId] },
                    { "events", JToken.FromObject(gameHistoryEvents) }
                };

                game.Add("history", history);

                pendingGameSubmission = (JObject) game.DeepClone();

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
                var pack = CreateObjectWithBaseData();

                var cardIds = new List<int>();
                foreach (JToken cardString in blob["DraftPack"].Value<JArray>())
                {
                    cardIds.Add(int.Parse(cardString.Value<String>()));
                }

                pack.Add("event_name", currentDraftEvent);
                pack.Add("pack_number", blob["PackNumber"].Value<int>());
                pack.Add("pick_number", blob["PickNumber"].Value<int>());
                pack.Add("card_ids", JToken.FromObject(cardIds));

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

                var pick = CreateObjectWithBaseData();

                pick.Add("event_name", currentDraftEvent);
                pick.Add("pack_number", pickInfo["PackNumber"].Value<int>());
                pick.Add("pick_number", pickInfo["PickNumber"].Value<int>());
                pick.Add("card_id", pickInfo["CardId"].Value<int>());

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

                var cardIds = new List<int>();
                foreach (JToken cardId in blob["CardsInPack"].Value<JArray>())
                {
                    cardIds.Add(cardId.Value<int>());
                }

                var pack = CreateObjectWithBaseData();
                pack.Add("method", "LogBusinessEvents");
                pack.Add("draft_id", blob["DraftId"].Value<String>());
                pack.Add("pack_number", blob["PackNumber"].Value<int>());
                pack.Add("pick_number", blob["PickNumber"].Value<int>());
                pack.Add("card_ids", JToken.FromObject(cardIds));
                pack.Add("event_name", currentDraftEvent);

                apiClient.PostHumanDraftPack(pack);
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing combined human draft pack from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }

            try
            {
                var pick = CreateObjectWithBaseData();
                pick.Add("draft_id", blob["DraftId"].Value<String>());
                pick.Add("pack_number", blob["PackNumber"].Value<int>());
                pick.Add("pick_number", blob["PickNumber"].Value<int>());
                pick.Add("card_id", blob["PickGrpId"].Value<int>());
                pick.Add("event_name", currentDraftEvent);
                pick.Add("auto_pick", blob["AutoPick"].Value<bool>());
                pick.Add("time_remaining", blob["TimeRemainingOnPick"].Value<float>());

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

                var cardIds = new List<int>();
                var cardIdBlob = JArray.Parse(String.Format("[{0}]", blob["PackCards"].Value<String>()));
                foreach (JToken cardId in cardIdBlob)
                {
                    cardIds.Add(cardId.Value<int>());
                }

                var pack = CreateObjectWithBaseData();
                pack.Add("method", "Draft.Notify");
                pack.Add("draft_id", blob["draftId"].Value<String>());
                pack.Add("pack_number", blob["SelfPack"].Value<int>());
                pack.Add("pick_number", blob["SelfPick"].Value<int>());
                pack.Add("card_ids", JToken.FromObject(cardIds));
                pack.Add("event_name", currentDraftEvent);

                apiClient.PostHumanDraftPack(pack);
                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing human draft pack from {1}", e, blob), e.StackTrace, Level.Warn);
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


                var deckInfo = blob["Deck"].Value<JObject>();

                var deck = CreateObjectWithBaseData();
                deck.Add("maindeck_card_ids", JToken.FromObject(GetCardIdsFromDeck(deckInfo["MainDeck"].Value<JArray>())));
                deck.Add("sideboard_card_ids", JToken.FromObject(GetCardIdsFromDeck(deckInfo["Sideboard"].Value<JArray>())));
                foreach (int companion in GetCardIdsFromDeck(deckInfo["Companions"].Value<JArray>()))
                {
                    deck.Add("companion", companion);
                }
                deck.Add("event_name", blob["EventName"].Value<String>());
                deck.Add("is_during_match", false);

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
                JObject event_ = CreateObjectWithBaseData();
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
                JObject event_ = CreateObjectWithBaseData();
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

        private bool MaybeHandleEventCourse(String fullLog, JObject blob)
        {
            if (!fullLog.Contains("Draft_CompleteDraft")) return false;
            if (!blob.ContainsKey("DraftId")) return false;

            try
            {
                var eventCourse = CreateObjectWithBaseData();
                eventCourse.Add("event_name", blob["InternalEventName"].Value<String>());
                eventCourse.Add("draft_id", blob["DraftId"].Value<String>());
                eventCourse.Add("course_id", blob["CourseId"].Value<String>());
                eventCourse.Add("card_pool", blob["CardPool"]);

                apiClient.PostEventCourse(eventCourse);

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

            ClearGameData();

            try
            {
                currentGameAdditionalDeckInfo = blob?["submitDeckResp"]?["deck"]?.Value<JObject>();
                if (currentGameAdditionalDeckInfo != null)
                {
                    if (currentGameAdditionalDeckInfo["deckCards"] != null)
                    {
                        currentGameMaindeck = JArrayToIntList(currentGameAdditionalDeckInfo["deckCards"].Value<JArray>());
                        currentGameAdditionalDeckInfo.Remove("deckCards");
                    }
                    else
                    {
                        currentGameMaindeck = new List<int>();
                    }

                    if (currentGameAdditionalDeckInfo["sideboardCards"] != null)
                    {
                        currentGameSideboard = JArrayToIntList(currentGameAdditionalDeckInfo["sideboardCards"].Value<JArray>());
                        currentGameAdditionalDeckInfo.Remove("sideboardCards");
                    }
                    else
                    {
                        currentGameSideboard = new List<int>();
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing GRE deck submission from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool MaybeHandleGreConnectResponse(JToken blob)
        {
            if (!"GREMessageType_ConnectResp".Equals(blob["type"].Value<string>())) return false;

            try
            {
                currentGameAdditionalDeckInfo = blob?["connectResp"]?["deckMessage"]?.Value<JObject>();
                if (currentGameAdditionalDeckInfo != null)
                {
                    if (currentGameAdditionalDeckInfo["deckCards"] != null)
                    {
                        currentGameMaindeck = JArrayToIntList(currentGameAdditionalDeckInfo["deckCards"].Value<JArray>());
                        currentGameAdditionalDeckInfo.Remove("deckCards");
                    } 
                    else
                    {
                        currentGameMaindeck = new List<int>();
                    }

                    if (currentGameAdditionalDeckInfo["sideboardCards"] != null)
                    {
                        currentGameSideboard = JArrayToIntList(currentGameAdditionalDeckInfo["sideboardCards"].Value<JArray>());
                        currentGameAdditionalDeckInfo.Remove("sideboardCards");
                    }
                    else
                    {
                        currentGameSideboard = new List<int>();
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing GRE connect response from {1}", e, blob), e.StackTrace, Level.Warn);
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
                        var matchId = gameInfo["matchID"].Value<string>();
                        if (matchId != currentMatchId)
                        {
                            currentMatchId = matchId;
                            currentEventName = null;
                        }
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
            if (results.Count > 0)
            {
                var success = SendHandleGameEnd(results);
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

            string updatedMatchId = gameRoomConfig["matchId"]?.Value<string>();
            string updatedEventName = gameRoomConfig["eventId"]?.Value<string>();

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
                        updatedEventName = player["eventId"]?.Value<string>() ?? updatedEventName;
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

            if (updatedMatchId != null && updatedEventName != null)
            {
                currentMatchId = updatedMatchId;
                currentEventName = updatedEventName;
            }

            if (gameRoomInfo.ContainsKey("finalMatchResult"))
            {
                // If the regular game end message is lost, try to submit remaining game data on match end.
                var finalMatchResult = gameRoomInfo["finalMatchResult"].Value<JObject>();
                if (finalMatchResult.ContainsKey("resultList")) {
                    var results = finalMatchResult["resultList"].Value<JArray>();
                    if (results.Count > 0)
                    {
                        var success = SendHandleGameEnd(results);
                        ClearMatchData();
                        return success;
                    }
                }
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
                    if (MaybeHandleGreConnectResponse(message)) continue;
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
                    if (payload["type"].Value<String>().Equals("ClientMessageType_SelectNResp"))
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

                JObject rankBlob = CreateObjectWithBaseData();
                rankBlob.Add("limited_rank", currentLimitedLevel);
                rankBlob.Add("constructed_rank", currentConstructedLevel);
                apiClient.PostRank(rankBlob);

                return true;
            }
            catch (Exception e)
            {
                LogError(String.Format("Error {0} parsing self rank info from {1}", e, blob), e.StackTrace, Level.Warn);
                return false;
            }
        }

        private bool MaybeHandleInventory(String fullLog, JObject blob)
        {
            if (!blob.ContainsKey("DTO_InventoryInfo")) return false;

            try
            {
                var inventoryInfo = blob["DTO_InventoryInfo"].Value<JObject>();

                JObject inventory = CreateObjectWithBaseData();

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
                JObject progress = CreateObjectWithBaseData();
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

            var errorInfo = CreateObjectWithBaseData();
            errorInfo.Add("blob", JToken.FromObject(currentDebugBlob));
            errorInfo.Add("recent_lines", JToken.FromObject(new List<string>(recentLines)));
            errorInfo.Add("stacktrace", String.Format("{0}\r\n{1}", message, stacktrace));
            apiClient.PostErrorInfo(errorInfo);
        }

        private string GetDatetimeString(DateTime value)
        {
            return value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private JObject CreateObjectWithBaseData()
        {
            return new JObject
            {
                { "token", apiToken },
                { "client_version", CLIENT_VERSION },
                { "player_id", currentUser },
                { "time", GetDatetimeString(currentLogTime.Value) },
                { "utc_time", GetDatetimeString(lastUtcTime.Value) },
                { "raw_time", lastRawTime }
            };
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
}
