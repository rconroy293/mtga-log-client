"""
Follows along a Magic Arena log, parses the messages, and passes along
the parsed data to an API endpoint.

Licensed under GNU GPL v3.0 (see included LICENSE).

This MTGA log follower is unofficial Fan Content permitted under the Fan
Content Policy. Not approved/endorsed by Wizards. Portions of the
materials used are property of Wizards of the Coast. (C) Wizards of the
Coast LLC. See https://company.wizards.com/fancontentpolicy for more
details.
"""

import argparse
import datetime
import json
import getpass
import gzip
import itertools
import logging
import logging.handlers
import os
import os.path
import pathlib
import re
import time
import traceback
import uuid

from collections import defaultdict, namedtuple

import dateutil.parser
import requests
import wx

LOG_FOLDER = os.path.join(os.path.expanduser('~'), '.seventeenlands')
if not os.path.exists(LOG_FOLDER):
    os.makedirs(LOG_FOLDER)
LOG_FILENAME = os.path.join(LOG_FOLDER, 'seventeenlands.log')

log_formatter = logging.Formatter('%(asctime)s,%(levelname)s,%(message)s', datefmt='%Y%m%d %H%M%S')
handlers = {
    logging.handlers.TimedRotatingFileHandler(LOG_FILENAME, when='D', interval=1, backupCount=7, utc=True),
    logging.StreamHandler(),
}
logger = logging.getLogger('17Lands')
for handler in handlers:
    handler.setFormatter(log_formatter)
    logger.addHandler(handler)
logger.setLevel(logging.INFO)
logger.info(f'Saving logs to {LOG_FILENAME}')

CLIENT_VERSION = '0.1.20.p'

FILE_UPDATED_FORCE_REFRESH_SECONDS = 60

OSX_LOG_ROOT = os.path.join('Library','Logs')
WINDOWS_LOG_ROOT = os.path.join('users', getpass.getuser(), 'AppData', 'LocalLow')
LOG_INTERMEDIATE = os.path.join('Wizards Of The Coast', 'MTGA')
CURRENT_LOG = 'Player.log'
PREVIOUS_LOG = 'Player-prev.log'
CURRENT_LOG_PATH = os.path.join(LOG_INTERMEDIATE, CURRENT_LOG)
PREVIOUS_LOG_PATH = os.path.join(LOG_INTERMEDIATE, PREVIOUS_LOG)

POSSIBLE_ROOTS = (
    # Windows
    os.path.join('C:/', WINDOWS_LOG_ROOT),
    os.path.join('D:/', WINDOWS_LOG_ROOT),
    # Lutris
    os.path.join(os.path.expanduser('~'), 'Games', 'magic-the-gathering-arena', 'drive_c', WINDOWS_LOG_ROOT),
    # Wine
    os.path.join(os.path.expanduser('~'), '.wine', 'drive_c', WINDOWS_LOG_ROOT),
    # OSX
    os.path.join(os.path.expanduser('~'), OSX_LOG_ROOT),
)

POSSIBLE_CURRENT_FILEPATHS = list(map(lambda root_and_path: os.path.join(*root_and_path), itertools.product(POSSIBLE_ROOTS, (CURRENT_LOG_PATH, ))))
POSSIBLE_PREVIOUS_FILEPATHS = list(map(lambda root_and_path: os.path.join(*root_and_path), itertools.product(POSSIBLE_ROOTS, (PREVIOUS_LOG_PATH, ))))

CONFIG_FILE = os.path.join(os.path.expanduser('~'), '.mtga_follower.ini')

LOG_START_REGEX_TIMED = re.compile(r'^\[(UnityCrossThreadLogger|Client GRE)\]([\d:/ -]+(AM|PM)?)')
LOG_START_REGEX_UNTIMED = re.compile(r'^\[(UnityCrossThreadLogger|Client GRE)\]')
TIMESTAMP_REGEX = re.compile('^([\\d/.-]+[ T][\\d]+:[\\d]+:[\\d]+( AM| PM)?)')
STRIPPED_TIMESTAMP_REGEX = re.compile('^(.*?)[: /]*$')
JSON_START_REGEX = re.compile(r'[\[\{]')
ACCOUNT_INFO_REGEX = re.compile(r'.*Updated account\. DisplayName:(.*), AccountID:(.*), Token:.*')
SLEEP_TIME = 0.5

TIME_FORMATS = (
    '%Y-%m-%d %I:%M:%S %p',
    '%Y-%m-%d %H:%M:%S',
    '%m/%d/%Y %I:%M:%S %p',
    '%m/%d/%Y %H:%M:%S',
    '%Y/%m/%d %I:%M:%S %p',
    '%Y/%m/%d %H:%M:%S',
    '%d/%m/%Y %H:%M:%S',
)
OUTPUT_TIME_FORMAT = '%Y%m%d%H%M%S'

API_ENDPOINT = 'https://www.17lands.com'
ENDPOINT_USER = 'api/account'
ENDPOINT_DECK_SUBMISSION = 'deck'
ENDPOINT_EVENT_SUBMISSION = 'event'
ENDPOINT_EVENT_COURSE_SUBMISSION = 'event_course'
ENDPOINT_GAME_RESULT = 'game'
ENDPOINT_DRAFT_PACK = 'pack'
ENDPOINT_DRAFT_PICK = 'pick'
ENDPOINT_HUMAN_DRAFT_PICK = 'human_draft_pick'
ENDPOINT_HUMAN_DRAFT_PACK = 'human_draft_pack'
ENDPOINT_COLLECTION = 'collection'
ENDPOINT_INVENTORY = 'inventory'
ENDPOINT_PLAYER_PROGRESS = 'player_progress'
ENDPOINT_CLIENT_VERSION = 'min_client_version'
ENDPOINT_RANK = 'api/rank'

RETRIES = 2
IS_CODE_FOR_RETRY = lambda code: code >= 500 and code < 600
IS_SUCCESS_CODE = lambda code: code >= 200 and code < 300
DEFAULT_RETRY_SLEEP_TIME = 1


def extract_time(time_str):
    """
    Convert a time string in various formats to a datetime.

    :param time_str: The string to convert.

    :returns: The resulting datetime object.
    :raises ValueError: Raises an exception if it cannot interpret the string.
    """
    time_str = STRIPPED_TIMESTAMP_REGEX.match(time_str).group(1)
    if ': ' in time_str:
        time_str = time_str.split(': ')[0]

    for possible_format in TIME_FORMATS:
        try:
            return datetime.datetime.strptime(time_str, possible_format)
        except ValueError:
            pass
    raise ValueError(f'Unsupported time format: "{time_str}"')

def json_value_matches(expectation, path, blob):
    """
    Check if the value nested at a given path in a JSON blob matches the expected value.

    :param expectation: The value to check against.
    :param path:        A list of keys for the nested value.
    :param blob:        The JSON blob to check in.

    :returns: Whether or not the value exists at the given path and it matches expectation.
    """
    for p in path:
        if p in blob:
            blob = blob[p]
        else:
            return False
    return blob == expectation

def get_rank_string(rank_class, level, percentile, place, step):
    """
    Convert the components of rank into a serializable value for recording

    :param rank_class: Class (e.g. Bronze, Mythic)
    :param level:      Level within the class
    :param percentile: Percentile (within Mythic)
    :param place:      Leaderboard place (within Mythic)
    :param step:       Step towards next level

    :returns: Serialized rank string (e.g. "Gold-3-0.0-0-2")
    """
    return '-'.join(str(x) for x in [rank_class, level, percentile, place, step])

class Follower:
    """Follows along a log, parses the messages, and passes along the parsed data to the API endpoint."""

    def __init__(self, token, host):
        self.host = host
        self.token = token
        self.buffer = []
        self.cur_log_time = datetime.datetime.fromtimestamp(0)
        self.last_utc_time = datetime.datetime.fromtimestamp(0)
        self.last_raw_time = ''
        self.json_decoder = json.JSONDecoder()
        self.cur_user = None
        self.cur_draft_event = None
        self.cur_constructed_level = None
        self.cur_limited_level = None
        self.cur_opponent_level = None
        self.cur_opponent_match_id = None
        self.current_match_event_id = None
        self.starting_team_id = None
        self.objects_by_owner = defaultdict(dict)
        self.opening_hand_count_by_seat = defaultdict(int)
        self.opening_hand = defaultdict(list)
        self.drawn_hands = defaultdict(list)
        self.drawn_cards_by_instance_id = defaultdict(dict)
        self.cards_in_hand = defaultdict(list)
        self.user_screen_name = None
        self.screen_names = defaultdict(lambda: '')
        self.game_history_events = []


    def __retry_post(self, endpoint, blob, num_retries=RETRIES, sleep_time=DEFAULT_RETRY_SLEEP_TIME, use_gzip=False):
        """
        Add client version to a JSON blob and send the data to an endpoint via post
        request, retrying on server errors.

        :param endpoint:    The http endpoint to hit.
        :param blob:        The JSON data to send in the body of the post request.
        :param num_retries: The number of times to retry upon failure.
        :param sleep_time:  In seconds, the time to sleep between tries.

        :returns: The response object (including status_code and text fields).
        """
        blob['client_version'] = CLIENT_VERSION
        blob['token'] = self.token
        blob['utc_time'] = self.last_utc_time.isoformat()

        tries_left = num_retries + 1
        while tries_left > 0:
            tries_left -= 1
            if use_gzip:
                data = gzip.compress(json.dumps(blob).encode('utf8'))
                response = requests.post(endpoint, data=data, headers={
                    'content-type': 'application/json',
                    'content-encoding': 'gzip',
                })
            else:
                response = requests.post(endpoint, json=blob)
            if not IS_CODE_FOR_RETRY(response.status_code):
                break
            logger.warning(f'Got response code {response.status_code}; retrying {tries_left} more times')
            time.sleep(sleep_time)
        logger.info(f'{response.status_code} Response: {response.text}')
        return response

    def parse_log(self, filename, follow):
        """
        Parse messages from a log file and pass the data along to the API endpoint.

        :param filename: The filename for the log file to parse.
        :param follow:   Whether or not to continue looking for updates to the file after parsing
                         all the initial lines.
        """
        while True:
            last_read_time = time.time()
            last_file_size = 0
            try:
                with open(filename) as f:
                    while True:
                        line = f.readline()
                        file_size = pathlib.Path(filename).stat().st_size
                        if line:
                            self.__append_line(line)
                            last_read_time = time.time()
                            last_file_size = file_size
                        else:
                            self.__handle_complete_log_entry()
                            last_modified_time = os.stat(filename).st_mtime
                            if file_size < last_file_size:
                                logger.info(f'Starting from beginning of file as file is smaller than before (previous = {last_file_size}; current = {file_size})')
                                break
                            elif last_modified_time > last_read_time + FILE_UPDATED_FORCE_REFRESH_SECONDS:
                                logger.info(f'Starting from beginning of file as file has been updated much more recently than the last read (previous = {last_read_time}; current = {last_modified_time})')
                                break
                            elif follow:
                                time.sleep(SLEEP_TIME)
                            else:
                                break
            except FileNotFoundError:
                time.sleep(SLEEP_TIME)

            if not follow:
                logger.info('Done processing file.')
                break

    def __append_line(self, line):
        """Add a complete line (not necessarily a complete message) from the log."""
        self.__maybe_handle_account_info(line)

        timestamp_match = TIMESTAMP_REGEX.match(line)
        if timestamp_match:
            self.last_raw_time = timestamp_match.group(1)
            self.cur_log_time = extract_time(self.last_raw_time)

        match = LOG_START_REGEX_UNTIMED.match(line)
        if match:
            self.__handle_complete_log_entry()

            timed_match = LOG_START_REGEX_TIMED.match(line)
            if timed_match:
                self.last_raw_time = timed_match.group(2)
                self.cur_log_time = extract_time(self.last_raw_time)
                self.buffer.append(line[timed_match.end():])
            else:
                self.buffer.append(line[match.end():])
        else:
            self.buffer.append(line)

    def __handle_complete_log_entry(self):
        """Mark the current log message complete. Should be called when waiting for more log messages."""
        if len(self.buffer) == 0:
            return
        if self.cur_log_time is None:
            self.buffer = []
            return

        full_log = ''.join(self.buffer)
        try:
            self.__handle_blob(full_log)
        except Exception as e:
            logger.error(f'Error {e} while processing {full_log}')
            logger.error(traceback.format_exc())

        self.buffer = []
        # self.cur_log_time = None

    def __maybe_get_utc_timestamp(self, blob):
        timestamp = None
        if 'timestamp' in blob:
            timestamp = blob['timestamp']
        elif 'timestamp' in blob.get('payloadObject', {}):
            timestamp = blob['payloadObject']['timestamp']
        elif 'timestamp' in blob.get('params', {}).get('payloadObject', {}):
            timestamp = blob['params']['payloadObject']['timestamp']
        
        if timestamp is None:
            return None

        try:
            seconds_since_year_1 = int(timestamp) / 10000000
            return datetime.datetime.fromordinal(1) + datetime.timedelta(seconds=seconds_since_year_1)
        except ValueError:
            return dateutil.parser.isoparse(timestamp)

    def __handle_blob(self, full_log):
        """Attempt to parse a complete log message and send the data if relevant."""
        match = JSON_START_REGEX.search(full_log)
        if not match:
            return

        try:
            json_obj, end = self.json_decoder.raw_decode(full_log, match.start())
        except json.JSONDecodeError as e:
            logger.debug(f'Ran into error {e} when parsing at {self.cur_log_time}. Data was: {full_log}')
            return

        json_obj = self.__extract_payload(json_obj)
        if type(json_obj) != dict: return

        try:
            maybe_time = self.__maybe_get_utc_timestamp(json_obj)
            if maybe_time is not None:
                self.last_utc_time = maybe_time
        except:
            pass

        if json_value_matches('Client.Connected', ['params', 'messageName'], json_obj):
            self.__handle_login(json_obj)
        # elif json_value_matches('DuelScene.GameStop', ['params', 'messageName'], json_obj):
        #     self.__handle_game_end(json_obj)
        elif 'DraftStatus' in json_obj:
            self.__handle_draft_log(json_obj)
        elif json_value_matches('Draft.MakePick', ['method'], json_obj):
            self.__handle_draft_pick(json_obj)
        elif json_value_matches('Draft.MakeHumanDraftPick', ['method'], json_obj):
            self.__handle_human_draft_pick(json_obj)
        elif json_value_matches('Event.JoinPodmaking', ['method'], json_obj):
            self.__handle_joined_pod(json_obj)
        elif json_value_matches('Event.DeckSubmit', ['method'], json_obj):
            self.__handle_deck_submission(json_obj)
        elif json_value_matches('Event.DeckSubmitV3', ['method'], json_obj):
            self.__handle_deck_submission_v3(json_obj)
        elif json_value_matches('DoneWithMatches', ['CurrentEventState'], json_obj):
            self.__handle_event_completion(json_obj)
        elif json_obj.get('ModuleInstanceData', {}).get('HumanDraft._internalState', {}).get('DraftId') is not None:
            self.__handle_event_course(json_obj)
        elif 'matchGameRoomStateChangedEvent' in json_obj:
            self.__handle_match_started(json_obj)
        elif 'greToClientEvent' in json_obj and 'greToClientMessages' in json_obj['greToClientEvent']:
            for message in json_obj['greToClientEvent']['greToClientMessages']:
                self.__handle_gre_to_client_message(message)
        elif json_value_matches('ClientToMatchServiceMessageType_ClientToGREMessage', ['clientToMatchServiceMessageType'], json_obj):
            self.__handle_client_to_gre_message(json_obj.get('payload', {}))
        elif json_value_matches('ClientToMatchServiceMessageType_ClientToGREUIMessage', ['clientToMatchServiceMessageType'], json_obj):
            self.__handle_client_to_gre_ui_message(json_obj.get('payload', {}))
        elif 'limitedStep' in json_obj:
            self.__handle_self_rank_info(json_obj)
        elif 'opponentRankingClass' in json_obj:
            self.__handle_match_created(json_obj)
        elif ' PlayerInventory.GetPlayerCardsV3 ' in full_log and 'method' not in json_obj:
            self.__handle_collection(json_obj)
        elif ' PlayerInventory.GetPlayerInventory ' in full_log and 'method' not in json_obj:
            self.__handle_inventory(json_obj)
        elif ' Progression.GetPlayerProgress ' in full_log and 'method' not in json_obj:
            self.__handle_player_progress(json_obj)
        elif 'Draft.Notify ' in full_log and 'method' not in json_obj:
            self.__handle_human_draft_pack(json_obj)
        elif 'Draft.Notification ' in full_log and 'method' not in json_obj:
            self.__handle_draft_notification(json_obj)
        elif 'FrontDoorConnection.Close ' in full_log:
            self.__reset_current_user()

    def __extract_payload(self, blob):
        if 'id' not in blob: return blob
        if 'payload' in blob:
            try:
                json_obj, end = self.json_decoder.raw_decode(blob['payload'])
                return json_obj
            except Exception as e:
                return blob['payload']
        if 'request' in blob:
            try:
                json_obj, end = self.json_decoder.raw_decode(blob['request'])
                return json_obj
            except Exception as e:
                pass

        return blob

    def __update_screen_name(self, screen_name):
        if self.user_screen_name == screen_name or '#' not in screen_name:
            return

        self.user_screen_name = screen_name
        user_info = {
            'player_id': self.cur_user,
            'screen_name': self.user_screen_name,
            'raw_time': self.last_raw_time,
        }
        logger.info(f'Updating user info: {user_info}')
        self.__retry_post(f'{self.host}/{ENDPOINT_USER}', blob=user_info)

    def __handle_match_started(self, blob):
        game_room_config = blob.get(
            'matchGameRoomStateChangedEvent', {}
        ).get(
            'gameRoomInfo', {}
        ).get(
            'gameRoomConfig', {}
        )

        if 'eventId' in game_room_config and 'matchId' in game_room_config:
            self.current_match_event_id = (game_room_config['matchId'], game_room_config['eventId'])

        if 'reservedPlayers' in game_room_config:
            for player in game_room_config['reservedPlayers']:
                self.screen_names[player['systemSeatId']] = player['playerName'].split('#')[0]
                # Backfill the current user's screen name when possible
                if player['userId'] == self.cur_user:
                    self.__update_screen_name(player['playerName'])

    def __handle_gre_to_client_message(self, message_blob):
        """Handle messages in the 'greToClientEvent' field."""
        # Add to game history before processing the messsage, since we may submit the game right away.
        if message_blob['type'] in ['GREMessageType_QueuedGameStateMessage', 'GREMessageType_GameStateMessage']:
            self.game_history_events.append(message_blob)
        elif message_blob['type'] == 'GREMessageType_UIMessage' and 'onChat' in message_blob['uiMessage']:
            self.game_history_events.append(message_blob)

        if message_blob['type'] == 'GREMessageType_GameStateMessage':
            game_state_message = message_blob.get('gameStateMessage', {})
            self.__maybe_handle_game_over_stage(message_blob.get('systemSeatIds', []), game_state_message)
            for game_object in game_state_message.get('gameObjects', []):
                if game_object['type'] not in ('GameObjectType_Card', 'GameObjectType_SplitCard'):
                    continue
                owner = game_object['ownerSeatId']
                instance_id = game_object['instanceId']
                card_id = game_object['overlayGrpId']

                self.objects_by_owner[owner][instance_id] = card_id
                
            for zone in game_state_message.get('zones', []):
                if zone['type'] == 'ZoneType_Hand':
                    owner = zone['ownerSeatId']
                    player_objects = self.objects_by_owner[owner]
                    hand_card_ids = zone.get('objectInstanceIds', [])
                    self.cards_in_hand[owner] = [player_objects.get(instance_id) for instance_id in hand_card_ids if instance_id]
                    for instance_id in hand_card_ids:
                        card_id = player_objects.get(instance_id)
                        if instance_id is not None and card_id is not None:
                            self.drawn_cards_by_instance_id[owner][instance_id] = card_id

            turn_info = game_state_message.get('turnInfo', {})
            players_deciding_hand = {
                (p['systemSeatNumber'], p.get('mulliganCount', 0))
                for p in game_state_message.get('players', [])
                if p.get('pendingMessageType') == 'ClientMessageType_MulliganResp'
            }
            for (player_id, mulligan_count) in players_deciding_hand:
                if self.starting_team_id is None:
                    self.starting_team_id = turn_info.get('activePlayer')
                self.opening_hand_count_by_seat[player_id] += 1

                if mulligan_count == len(self.drawn_hands[player_id]):
                    self.drawn_hands[player_id].append(self.cards_in_hand[player_id].copy())

            if len(self.opening_hand) == 0 and ('Phase_Beginning', 'Step_Upkeep', 1) == (turn_info.get('phase'), turn_info.get('step'), turn_info.get('turnNumber')):
                for (owner, hand) in self.cards_in_hand.items():
                    self.opening_hand[owner] = hand.copy()

    def __handle_client_to_gre_message(self, payload):
        if payload['type'] == 'ClientMessageType_SelectNResp':
            self.game_history_events.append(payload)

        if payload['type'] == 'ClientMessageType_SubmitDeckResp':
            deck_info = payload['submitDeckResp']['deck']
            deck = {
                'player_id': self.cur_user,
                'time': self.cur_log_time.isoformat(),
                'maindeck_card_ids': deck_info['deckCards'],
                'sideboard_card_ids': deck_info.get('sideboardCards', []),
                'companion': deck_info.get('companionGRPId', deck_info.get('companion', deck_info.get('deckMessageFieldFour', 0))),
                'is_during_match': True,
            }
            logger.info(f'Deck submission via __handle_client_to_gre_message: {deck}')
            response = self.__retry_post(f'{self.host}/{ENDPOINT_DECK_SUBMISSION}', blob=deck)

    def __handle_client_to_gre_ui_message(self, payload):
        if 'onChat' in payload['uiMessage']:
            self.game_history_events.append(payload)

    def __maybe_handle_game_over_stage(self, system_seat_ids, game_state_message):
        game_info = game_state_message.get('gameInfo', {})
        if game_info.get('stage') != 'GameStage_GameOver':
            return

        results = game_info.get('results', [])
        for result in reversed(results):
            if result.get('scope') != 'MatchScope_Game':
                continue

            seat_id = system_seat_ids[0]
            match_id = game_info['matchID']
            event_id = None
            if self.current_match_event_id is not None and self.current_match_event_id[0] == match_id:
                event_id = self.current_match_event_id[1]

            maybe_turn_number = game_state_message.get('turnInfo', {}).get('turnNumber')
            if maybe_turn_number is None:
                players = game_state_message.get('players', [])
                if len(players) > 0:
                    maybe_turn_number = sum(p['turnNumber'] for p in players)
                    # If one of the player structs is missing, double the turn number to acount for it
                    if len(players) == 1:
                        maybe_turn_number *= 2
                else:
                    maybe_turn_number = -1

            self.__send_game_end(
                seat_id=seat_id,
                match_id=match_id,
                mulliganed_hands=self.drawn_hands[seat_id][:-1],
                drawn_hands=self.drawn_hands[seat_id],
                drawn_cards=list(self.drawn_cards_by_instance_id[seat_id].values()),
                event_name=event_id,
                on_play=seat_id == self.starting_team_id,
                won=seat_id == result['winningTeamId'],
                win_type=result['result'],
                game_end_reason=result['reason'],
                turn_count=maybe_turn_number,
                duration=-1,
            )

            if game_info.get('matchState') == 'MatchState_MatchComplete':
                self.__clear_match_data()

            return


    def __clear_game_data(self):
        self.objects_by_owner.clear()
        self.opening_hand_count_by_seat.clear()
        self.opening_hand.clear()
        self.drawn_hands.clear()
        self.drawn_cards_by_instance_id.clear()
        self.starting_team_id = None
        self.game_history_events.clear()

    def __clear_match_data(self):
        self.screen_names.clear()

    def __maybe_handle_account_info(self, line):
        match = ACCOUNT_INFO_REGEX.match(line)
        if match:
            screen_name = match.group(1)
            self.cur_user = match.group(2)
            self.__update_screen_name(screen_name)

    def __handle_event_completion(self, json_obj):
        """Handle messages upon event completion."""
        event = {
            'player_id': self.cur_user,
            'event_name': json_obj['InternalEventName'],
            'time': self.cur_log_time.isoformat(),
            'entry_fee': json_obj['ModuleInstanceData']['HasPaidEntry'],
            'wins': json_obj['ModuleInstanceData']['WinLossGate']['CurrentWins'],
            'losses': json_obj['ModuleInstanceData']['WinLossGate']['CurrentLosses'],
        }
        logger.info(f'Event submission: {event}')
        response = self.__retry_post(f'{self.host}/{ENDPOINT_EVENT_SUBMISSION}', blob=event)

    def __handle_event_course(self, json_obj):
        """Handle messages linking draft id to event name."""
        event = {
            'player_id': self.cur_user,
            'event_name': json_obj['InternalEventName'],
            'time': self.cur_log_time.isoformat(),
            'draft_id': json_obj['ModuleInstanceData']['HumanDraft._internalState']['DraftId'],
        }
        logger.info(f'Event course: {event}')
        self.__retry_post(f'{self.host}/{ENDPOINT_EVENT_COURSE_SUBMISSION}', blob=event)
        self.__update_screen_name(json_obj['ModuleInstanceData']['HumanDraft._internalState']['ScreenName'])

    def __handle_game_end(self, json_obj):
        """Handle 'DuelScene.GameStop' messages."""
        blob = json_obj['params']['payloadObject']
        self.__send_game_end(
            seat_id=blob['seatId'],
            match_id=blob['matchId'],
            mulliganed_hands=[[x['grpId'] for x in hand] for hand in blob['mulliganedHands']],
            drawn_hands=None,
            drawn_cards=None,
            event_name=blob['eventId'],
            on_play=blob['teamId'] == blob['startingTeamId'],
            won=blob['teamId'] == blob['winningTeamId'],
            win_type=blob['winningType'],
            game_end_reason=blob['winningReason'],
            turn_count=blob['turnCount'],
            duration=blob['secondsCount'],
        )

    def __send_game_end(self, seat_id, match_id, mulliganed_hands, drawn_hands, drawn_cards, event_name, on_play, won, win_type, game_end_reason, turn_count, duration):
        logger.debug(f'End of game. Cards by owner: {self.objects_by_owner}')

        opponent_id = 2 if seat_id == 1 else 1
        opponent_card_ids = [c for c in self.objects_by_owner.get(opponent_id, {}).values()]

        if match_id != self.cur_opponent_match_id:
            self.cur_opponent_level = None

        game = {
            'player_id': self.cur_user,
            'event_name': event_name,
            'match_id': match_id,
            'time': self.cur_log_time.isoformat(),
            'on_play': on_play,
            'won': won,
            'win_type': win_type,
            'game_end_reason': game_end_reason,
            'opening_hand': self.opening_hand[seat_id],
            'mulligans': mulliganed_hands,
            'drawn_hands': drawn_hands,
            'drawn_cards': drawn_cards,
            'mulligan_count': self.opening_hand_count_by_seat[seat_id] - 1,
            'opponent_mulligan_count': self.opening_hand_count_by_seat[opponent_id] - 1,
            'turns': turn_count,
            'duration': duration,
            'opponent_card_ids': opponent_card_ids,
            'limited_rank': self.cur_limited_level,
            'constructed_rank': self.cur_constructed_level,
            'opponent_rank': self.cur_opponent_level,
        }
        logger.info(f'Completed game: {game}')

        # Add the history to the blob after logging to avoid printing excessive logs
        logger.info(f'Adding game history ({len(self.game_history_events)} events)')
        game['history'] = {
            'seat_id': seat_id,
            'opponent_seat_id': opponent_id,
            'screen_name': self.screen_names[seat_id],
            'opponent_screen_name': self.screen_names[opponent_id],
            'events': self.game_history_events,
        }

        response = self.__retry_post(f'{self.host}/{ENDPOINT_GAME_RESULT}', blob=game, use_gzip=True)
        self.__clear_game_data()

    def __handle_login(self, json_obj):
        """Handle 'Client.Connected' messages."""
        self.__clear_game_data()

        self.cur_user = json_obj['params']['payloadObject']['playerId']
        screen_name = json_obj['params']['payloadObject']['screenName']
        self.__update_screen_name(screen_name)

    def __handle_draft_log(self, json_obj):
        """Handle 'draftStatus' messages."""
        if json_obj['DraftStatus'] == 'Draft.PickNext':
            self.__clear_game_data()
            (user, event_name, other) = json_obj['DraftId'].rsplit(':', 2)
            pack = {
                'player_id': self.cur_user,
                'event_name': event_name,
                'time': self.cur_log_time.isoformat(),
                'pack_number': int(json_obj['PackNumber']),
                'pick_number': int(json_obj['PickNumber']),
                'card_ids': [int(x) for x in json_obj['DraftPack']],
            }
            logger.info(f'Draft pack: {pack}')
            response = self.__retry_post(f'{self.host}/{ENDPOINT_DRAFT_PACK}', blob=pack)

    def __handle_draft_pick(self, json_obj):
        """Handle 'Draft.MakePick messages."""
        self.__clear_game_data()
        inner_obj = json_obj['params']
        (user, event_name, other) = inner_obj['draftId'].rsplit(':', 2)

        pick = {
            'player_id': self.cur_user,
            'event_name': event_name,
            'time': self.cur_log_time.isoformat(),
            'pack_number': int(inner_obj['packNumber']),
            'pick_number': int(inner_obj['pickNumber']),
            'card_id': int(inner_obj['cardId']),
        }
        logger.info(f'Draft pick: {pick}')
        response = self.__retry_post(f'{self.host}/{ENDPOINT_DRAFT_PICK}', blob=pick)

    def __handle_joined_pod(self, json_obj):
        """Handle 'Event.JoinPodmaking messages."""
        self.__clear_game_data()
        inner_obj = json_obj['params']
        self.cur_draft_event = inner_obj['queueId']

        logger.info(f'Joined draft pod: {self.cur_draft_event}')

    def __handle_human_draft_pick(self, json_obj):
        """Handle 'Draft.MakeHumanDraftPick messages."""
        self.__clear_game_data()
        inner_obj = json_obj['params']

        pick = {
            'player_id': self.cur_user,
            'time': self.cur_log_time.isoformat(),
            'draft_id': inner_obj['draftId'],
            'event_name': self.cur_draft_event,
            'pack_number': int(inner_obj['packNumber']),
            'pick_number': int(inner_obj['pickNumber']),
            'card_id': int(inner_obj['cardId']),
        }
        logger.info(f'Human draft pick: {pick}')
        response = self.__retry_post(f'{self.host}/{ENDPOINT_HUMAN_DRAFT_PICK}', blob=pick)

    def __handle_human_draft_pack(self, json_obj):
        """Handle 'Draft.Notify messages."""
        self.__clear_game_data()

        pack = {
            'player_id': self.cur_user,
            'time': self.cur_log_time.isoformat(),
            'draft_id': json_obj['draftId'],
            'event_name': self.cur_draft_event,
            'pack_number': int(json_obj['SelfPack']),
            'pick_number': int(json_obj['SelfPick']),
            'card_ids': [int(x) for x in json_obj['PackCards'].split(',')],
        }
        logger.info(f'Human draft pack: {pack}')
        response = self.__retry_post(f'{self.host}/{ENDPOINT_HUMAN_DRAFT_PACK}', blob=pack)

    def __handle_draft_notification(self, json_obj):
        """Handle 'Draft.Notification messages."""
        if json_obj.get('PickInfo') is None:
            return

        self.__clear_game_data()

        pick_info = json_obj['PickInfo']

        pack = {
            'player_id': self.cur_user,
            'time': self.cur_log_time.isoformat(),
            'draft_id': json_obj['DraftId'],
            'event_name': self.cur_draft_event,
            'pack_number': int(pick_info['SelfPack']),
            'pick_number': int(pick_info['SelfPick']),
            'card_ids': pick_info['PackCards'],
        }
        logger.info(f'Human draft pack via notification: {pack}')
        response = self.__retry_post(f'{self.host}/{ENDPOINT_HUMAN_DRAFT_PACK}', blob=pack)

    def __handle_deck_submission(self, json_obj):
        """Handle 'Event.DeckSubmit' messages."""
        self.__clear_game_data()
        inner_obj = json_obj['params']
        deck_info = json.loads(inner_obj['deck'])
        deck = {
            'player_id': self.cur_user,
            'event_name': inner_obj['eventName'],
            'time': self.cur_log_time.isoformat(),
            'maindeck_card_ids': [d['Id'] for d in deck_info['mainDeck'] for i in range(d['Quantity'])],
            'sideboard_card_ids': [d['Id'] for d in deck_info['sideboard'] for i in range(d['Quantity'])],
            'is_during_match': False,
        }
        logger.info(f'Deck submission via __handle_deck_submission: {deck}')
        response = self.__retry_post(f'{self.host}/{ENDPOINT_DECK_SUBMISSION}', blob=deck)

    def __handle_deck_submission_v3(self, json_obj):
        """Handle 'Event.DeckSubmitV3' messages."""
        self.__clear_game_data()
        inner_obj = json_obj['params']
        deck_info = json.loads(inner_obj['deck'])
        deck = {
            'player_id': self.cur_user,
            'event_name': inner_obj['eventName'],
            'time': self.cur_log_time.isoformat(),
            'maindeck_card_ids': self.__get_card_ids_from_decklist_v3(deck_info['mainDeck']),
            'sideboard_card_ids': self.__get_card_ids_from_decklist_v3(deck_info['sideboard']),
            'is_during_match': False,
            'companion': deck_info.get('companionGRPId'),
        }
        logger.info(f'Deck submission via __handle_deck_submission_v3: {deck}')
        response = self.__retry_post(f'{self.host}/{ENDPOINT_DECK_SUBMISSION}', blob=deck)

    def __handle_self_rank_info(self, json_obj):
        """Handle 'Event.GetCombinedRankInfo' messages."""
        self.cur_limited_level = get_rank_string(
            rank_class=json_obj.get('limitedClass'),
            level=json_obj.get('limitedLevel'),
            percentile=json_obj.get('limitedPercentile'),
            place=json_obj.get('limitedLeaderboardPlace'),
            step=json_obj.get('limitedStep'),
        )
        self.cur_constructed_level = get_rank_string(
            rank_class=json_obj.get('constructedClass'),
            level=json_obj.get('constructedLevel'),
            percentile=json_obj.get('constructedPercentile'),
            place=json_obj.get('constructedLeaderboardPlace'),
            step=json_obj.get('constructedStep'),
        )
        self.cur_user = json_obj.get('playerId', self.cur_user)
        logger.info(f'Parsed rank info for {self.cur_user} as limited {self.cur_limited_level} and constructed {self.cur_constructed_level}')
        response = self.__retry_post(f'{self.host}/{ENDPOINT_RANK}', blob={
            'player_id':self.cur_user,
            'time': self.cur_log_time.isoformat(),
            'limited_rank': self.cur_limited_level,
            'constructed_rank': self.cur_constructed_level,
        })

    def __handle_match_created(self, json_obj):
        """Handle 'Event.MatchCreated' messages."""
        self.__clear_game_data()
        self.cur_opponent_level = get_rank_string(
            rank_class=json_obj.get('opponentRankingClass'),
            level=json_obj.get('opponentRankingTier'),
            percentile=json_obj.get('opponentMythicPercentile'),
            place=json_obj.get('opponentMythicLeaderboardPlace'),
            step=None,
        )
        self.cur_opponent_match_id = json_obj.get('matchId')
        logger.info(f'Parsed opponent rank info as limited {self.cur_opponent_level} in match {self.cur_opponent_match_id}')

    def __handle_collection(self, json_obj):
        """Handle 'PlayerInventory.GetPlayerCardsV3' messages."""
        if self.cur_user is None:
            logger.info(f'Skipping collection submission because player id is still unknown')
            return

        collection = {
            'player_id': self.cur_user,
            'time': self.cur_log_time.isoformat(),
            'card_counts': json_obj,
        }
        logger.info(f'Collection submission of {len(json_obj)} cards')
        self.__retry_post(f'{self.host}/{ENDPOINT_COLLECTION}', blob=collection)

    def __handle_inventory(self, json_obj):
        """Handle 'PlayerInventory.GetPlayerInventory' messages."""
        # Opportunistically update playerId if available
        self.cur_user = json_obj.get('playerId', self.cur_user)

        json_obj.pop('vanityItems', None)
        json_obj.pop('vanitySelections', None)
        json_obj.pop('starterDecks', None)
        blob = {
            'player_id': self.cur_user,
            'time': self.cur_log_time.isoformat(),
            'inventory': json_obj,
        }
        logger.info(f'Submitting inventory')
        self.__retry_post(f'{self.host}/{ENDPOINT_INVENTORY}', blob=blob)

    def __handle_player_progress(self, json_obj):
        """Handle 'Progression.GetPlayerProgress' messages."""
        blob = {
            'player_id': self.cur_user,
            'time': self.cur_log_time.isoformat(),
            'progress': json_obj,
        }
        logger.info(f'Submitting mastery progress')
        self.__retry_post(f'{self.host}/{ENDPOINT_PLAYER_PROGRESS}', blob=blob)

    def __get_card_ids_from_decklist_v3(self, decklist):
        """Parse a list of [card_id_1, count_1, card_id_2, count_2, ...] elements."""
        assert len(decklist) % 2 == 0
        result = []
        for i in range(len(decklist) // 2):
            card_id = decklist[2 * i]
            count = decklist[2 * i + 1]
            for j in range(count):
                result.append(card_id)
        return result

    def __reset_current_user(self):
        logger.info('User logged out')
        self.cur_user = None
        self.user_screen_name = None

def validate_uuid_v4(maybe_uuid):
    if maybe_uuid is None:
        return None
    try:
        uuid.UUID(maybe_uuid, version=4)
        return maybe_uuid
    except ValueError:
        return None


class TokenEntryApp(wx.App):

    def __init__(self, token, *args, **kwargs):
        self.token = token
        super().__init__(*args, **kwargs)

    def OnInit(self):
        entry_dialog = wx.TextEntryDialog(
            None,
            'Please enter your client token from 17lands.com/account:',
            '17Lands: Enter Token',
        )
        token = None
        while True:
            result = entry_dialog.ShowModal()
            if result != wx.ID_OK:
                logger.warning('Cancelled from token entry')
                entry_dialog.Destroy()
                wx.MessageBox(
                    '17Lands cannot continue without specifying a client token. Exiting.',
                    '17Lands',
                    wx.OK | wx.ICON_WARNING,
                )
                return True

            token = entry_dialog.GetValue()
            if validate_uuid_v4(token) is None:
                logger.warning(f'Invalid token entered: {token}')
                entry_dialog.SetLabel('Try Again - Invalid 17Lands Token')
            else:
                self.token.set(token)
                logger.info(f'Token entered successfully')
                break

        entry_dialog.Destroy()
        return True

def get_client_token_visual():
    class Settable:
        def __init__(self):
            self.value = None
        def set(self, value):
            self.value = value

    token = Settable()
    app = TokenEntryApp(token, 0)
    app.MainLoop()
    if token.value is None:
        logger.warning(f'No token entered. Exiting.')
        exit(1)

    logger.info(f'Got token: {token.value}')
    return token.value


def get_client_token_cli():
    message = 'Please enter your client token from 17lands.com/account: '
    while True:
        token = input(message)

        if token is None:
            print('Error: The program cannot continue without specifying a client token. Exiting.')
            exit(1)

        if validate_uuid_v4(token) is None:
            message = 'That token is invalid. Please specify a valid client token. See 17lands.com/getting_started for more details. Token: '
        else:
            return token

def get_config():
    import configparser
    token = None
    config = configparser.ConfigParser()
    if os.path.exists(CONFIG_FILE):
        config.read(CONFIG_FILE)
        if 'client' in config:
            token = validate_uuid_v4(config['client'].get('token'))

    if token is None or validate_uuid_v4(token) is None:
        try:
            token = get_client_token_visual()
        except ModuleNotFoundError:
            token = get_client_token_cli()

        if 'client' not in config:
            config['client'] = {}
        config['client']['token'] = token
        with open(CONFIG_FILE, 'w') as f:
            config.write(f)

    return token

def verify_version(host):
    for i in range(3):
        response = requests.get(f'{host}/{ENDPOINT_CLIENT_VERSION}')
        if not IS_CODE_FOR_RETRY(response.status_code):
            break
        logger.warning(f'Got response code {response.status_code}; retrying')
        time.sleep(DEFAULT_RETRY_SLEEP_TIME)
    else:
        logger.warning('Could not get response from server for minimum client version. Assuming version is valid.')
        return

    logger.info(f'Got minimum client version response: {response.text}')
    blob = json.loads(response.text)
    this_version = [int(i) for i in CLIENT_VERSION.split('.')[:-1]]
    min_supported_version = [int(i) for i in blob['min_version'].split('.')]
    logger.info(f'Minimum supported version: {min_supported_version}; this version: {this_version}')

    if this_version >= min_supported_version:
        return

    wx.MessageBox(
        (f'17Lands update required! The minimum supported version for the client is {blob["min_version"]}. '
            + f'Your current version is {CLIENT_VERSION}. Please update with one of the following '
            + 'commands in the terminal, depending on your installation method:\n'
            + 'brew update && brew upgrade seventeenlands\n'
            + 'pip3 install --user --upgrade seventeenlands'),
        '17Lands',
        wx.OK | wx.ICON_WARNING,
    )
    exit(1)


def processing_loop(args, token):
    filepaths = POSSIBLE_CURRENT_FILEPATHS
    if args.log_file is not None:
        filepaths = (args.log_file, )

    follow = not args.once

    follower = Follower(token, host=args.host)

    # if running in "normal" mode...
    if args.log_file is None and args.host == API_ENDPOINT and follow:
        # parse previous log once at startup to catch up on any missed events
        for filename in POSSIBLE_PREVIOUS_FILEPATHS:
            if os.path.exists(filename):
                logger.info(f'Parsing the previous log {filename} once')
                follower.parse_log(filename=filename, follow=False)
                break

    # tail and parse current logfile to handle ongoing events
    for filename in filepaths:
        if os.path.exists(filename):
            logger.info(f'Following along {filename}')
            follower.parse_log(filename=filename, follow=follow)

    logger.info(f'Exiting')


def main():
    parser = argparse.ArgumentParser(description='MTGA log follower')
    parser.add_argument('-l', '--log_file',
        help=f'Log filename to process. If not specified, will try one of {POSSIBLE_CURRENT_FILEPATHS}')
    parser.add_argument('--host', default=API_ENDPOINT,
        help=f'Host to submit requests to. If not specified, will use {API_ENDPOINT}')
    parser.add_argument('--once', action='store_true',
        help='Whether to stop after parsing the file once (default is to continue waiting for updates to the file)')

    args = parser.parse_args()

    app = wx.App()
    verify_version(args.host)

    token = get_config()
    logger.info(f'Using token {token[:4]}...{token[-4:]}')

    processing_loop(args, token)


if __name__ == '__main__':
    main()
