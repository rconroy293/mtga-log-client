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

import datetime
import json
import getpass
import logging
import os
import os.path
import re
import time
import traceback
import uuid

from collections import namedtuple

import dateutil.parser
import requests

logging.basicConfig(
    format='%(asctime)s,%(levelname)s,%(message)s',
    datefmt='%Y%m%d %H%M%S',
    level=logging.INFO,
)

CLIENT_VERSION = '0.1.3'

PATH_ON_DRIVE = os.path.join('users',getpass.getuser(),'AppData','LocalLow','Wizards Of The Coast','MTGA','output_log.txt')
POSSIBLE_FILEPATHS = (
    # Windows
    os.path.join('C:/',PATH_ON_DRIVE),
    os.path.join('D:/',PATH_ON_DRIVE),
    # Lutris
    os.path.join(os.path.expanduser('~'),'Games','magic-the-gathering-arena','drive_c',PATH_ON_DRIVE),
    # Wine
    os.path.join(os.path.expanduser('~'),'.wine','drive_c',PATH_ON_DRIVE),
)

CONFIG_FILE = os.path.join(os.path.expanduser('~'), '.mtga_follower.ini')

LOG_START_REGEX_TIMED = re.compile(r'^\[(UnityCrossThreadLogger|Client GRE)\]([\d:/ -]+(AM|PM)?)')
LOG_START_REGEX_UNTIMED = re.compile(r'^\[(UnityCrossThreadLogger|Client GRE)\]')
TIMESTAMP_REGEX = re.compile('^([\\d/.-]+[ T][\\d]+:[\\d]+:[\\d]+( AM| PM)?)')
JSON_START_REGEX = re.compile(r'[[{]')
SLEEP_TIME = 0.5

TIME_FORMATS = (
    '%Y-%m-%d %I:%M:%S %p',
    '%Y-%m-%d %H:%M:%S',
    '%m/%d/%Y %I:%M:%S %p',
    '%m/%d/%Y %H:%M:%S',
    '%Y/%m/%d %I:%M:%S %p',
    '%Y/%m/%d %H:%M:%S',
)
OUTPUT_TIME_FORMAT = '%Y%m%d%H%M%S'

API_ENDPOINT = 'https://www.17lands.com'
ENDPOINT_USER = 'api/account'
ENDPOINT_DECK_SUBMISSION = 'deck'
ENDPOINT_EVENT_SUBMISSION = 'event'
ENDPOINT_GAME_RESULT = 'game'
ENDPOINT_DRAFT_PACK = 'pack'
ENDPOINT_DRAFT_PICK = 'pick'
ENDPOINT_CLIENT_VERSION = 'min_client_version'

RETRIES = 2
IS_CODE_FOR_RETRY = lambda code: code >= 500 and code < 600
DEFAULT_RETRY_SLEEP_TIME = 1

def extract_time(time_str):
    """
    Convert a time string in various formats to a datetime.

    :param time_str: The string to convert.

    :returns: The resulting datetime object.
    :raises ValueError: Raises an exception if it cannot interpret the string.
    """
    for possible_format in TIME_FORMATS:
        try:
            return datetime.datetime.strptime(time_str, possible_format)
        except ValueError:
            pass
    raise ValueError(f'Unsupported time format: {time_str}')

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
        self.objects_by_owner = {}

    def __retry_post(self, endpoint, blob, num_retries=RETRIES, sleep_time=DEFAULT_RETRY_SLEEP_TIME):
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
            response = requests.post(endpoint, json=blob)
            if not IS_CODE_FOR_RETRY(response.status_code):
                break
            logging.warning(f'Got response code {response.status_code}; retrying {tries_left} more times')
            time.sleep(sleep_time)
        logging.info(f'{response.status_code} Response: {response.text}')
        return response

    def parse_log(self, filename, follow):
        """
        Parse messages from a log file and pass the data along to the API endpoint.

        :param filename: The filename for the log file to parse.
        :param follow:   Whether or not to continue looking for updates to the file after parsing
                         all the initial lines.
        """
        last_read_time = time.time()
        while True:
            with open(filename) as f:
                while True:
                    line = f.readline()
                    if line:
                        self.__append_line(line)
                        last_read_time = time.time()
                    else:
                        self.__handle_complete_log_entry()
                        last_modified_time = os.stat(filename).st_mtime
                        if last_modified_time > last_read_time:
                            break
                        elif follow:
                            time.sleep(SLEEP_TIME)
                        else:
                            break
            if not follow:
                logging.info('Done processing file.')
                break

    def __append_line(self, line):
        """Add a complete line (not necessarily a complete message) from the log."""
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
            logging.error(f'Error {e} while processing {full_log}')
            logging.error(traceback.format_exc())

        self.buffer = []
        # self.cur_log_time = None

    def __maybe_get_utc_timestamp(self, blob):
        timestamp = None
        if 'timestamp' in blob:
            timestamp = blob['timestamp']
        elif 'timestamp' in blob.get('payloadObject', {}):
            timestamp = blob['payloadObject']['timestamp']
        
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
            logging.debug(f'Ran into error {e} when parsing at {self.cur_log_time}. Data was: {full_log}')
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
        elif json_value_matches('DuelScene.GameStop', ['params', 'messageName'], json_obj):
            self.__handle_game_end(json_obj)
        elif 'DraftStatus' in json_obj:
            self.__handle_draft_log(json_obj)
        elif json_value_matches('Draft.MakePick', ['method'], json_obj):
            self.__handle_draft_pick(json_obj)
        elif json_value_matches('Event.DeckSubmit', ['method'], json_obj):
            self.__handle_deck_submission(json_obj)
        elif json_value_matches('Event.DeckSubmitV3', ['method'], json_obj):
            self.__handle_deck_submission_v3(json_obj)
        elif json_value_matches('DoneWithMatches', ['CurrentEventState'], json_obj):
            self.__handle_event_completion(json_obj)
        elif 'greToClientEvent' in json_obj and 'greToClientMessages' in json_obj['greToClientEvent']:
            for message in json_obj['greToClientEvent']['greToClientMessages']:
                self.__handle_gre_to_client_message(message)

    def __extract_payload(self, blob):
        if 'id' not in blob: return blob
        if 'payload' in blob: return blob['payload']
        if 'request' in blob:
            try:
                json_obj, end = self.json_decoder.raw_decode(blob['request'])
                return json_obj
            except Exception as e:
                pass

        return blob


    def __handle_gre_to_client_message(self, message_blob):
        """Handle messages in the 'greToClientEvent' field."""
        if message_blob['type'] == 'GREMessageType_SubmitDeckReq':
            deck = {
                'player_id': self.cur_user,
                'time': self.cur_log_time.isoformat(),
                'maindeck_card_ids': message_blob['submitDeckReq']['deck']['deckCards'],
                'sideboard_card_ids': message_blob['submitDeckReq']['deck']['sideboardCards'],
                'is_during_match': True,
            }
            logging.info(f'Deck submission: {deck}')
            response = self.__retry_post(f'{self.host}/{ENDPOINT_DECK_SUBMISSION}', blob=deck)
        elif message_blob['type'] == 'GREMessageType_GameStateMessage':
            for game_object in message_blob['gameStateMessage'].get('gameObjects', []):
                if game_object['type'] != 'GameObjectType_Card':
                    continue
                owner = game_object['ownerSeatId']
                instance_id = game_object['instanceId']
                card_id = game_object['overlayGrpId']

                if owner not in self.objects_by_owner:
                    self.objects_by_owner[owner] = {}
                self.objects_by_owner[owner][instance_id] = card_id

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
        logging.info(f'Event submission: {event}')
        response = self.__retry_post(f'{self.host}/{ENDPOINT_EVENT_SUBMISSION}', blob=event)

    def __handle_game_end(self, json_obj):
        """Handle 'DuelScene.GameStop' messages."""
        logging.debug(f'End of game. Cards by owner: {self.objects_by_owner}')

        blob = json_obj['params']['payloadObject']

        opponent_id = 2 if blob['seatId'] == 1 else 1
        opponent_card_ids = [c for c in self.objects_by_owner.get(opponent_id, {}).values()]
        self.objects_by_owner = {}

        game = {
            'player_id': self.cur_user,
            'event_name': blob['eventId'],
            'match_id': blob['matchId'],
            'time': self.cur_log_time.isoformat(),
            'on_play': blob['teamId'] == blob['startingTeamId'],
            'won': blob['teamId'] == blob['winningTeamId'],
            'win_type': blob['winningType'],
            'game_end_reason': blob['winningReason'],
            'mulligans': [[x['grpId'] for x in hand] for hand in blob['mulliganedHands']],
            'turns': blob['turnCount'],
            'duration': blob['secondsCount'],
            'opponent_card_ids': opponent_card_ids,
        }
        logging.info(f'Completed game: {game}')
        response = self.__retry_post(f'{self.host}/{ENDPOINT_GAME_RESULT}', blob=game)

    def __handle_login(self, json_obj):
        """Handle 'Client.Connected' messages."""
        self.cur_user = json_obj['params']['payloadObject']['playerId']
        screen_name = json_obj['params']['payloadObject']['screenName']

        user_info = {
            'player_id': self.cur_user,
            'screen_name': screen_name,
            'raw_time': self.last_raw_time,
        }
        logging.info(f'Adding user: {user_info}')
        response = self.__retry_post(f'{self.host}/{ENDPOINT_USER}', blob=user_info)

    def __handle_draft_log(self, json_obj):
        """Handle 'draftStatus' messages."""
        if json_obj['DraftStatus'] == 'Draft.PickNext':
            (user, event_name, other) = json_obj['DraftId'].rsplit(':', 2)
            pack = {
                'player_id': self.cur_user,
                'event_name': event_name,
                'time': self.cur_log_time.isoformat(),
                'pack_number': int(json_obj['PackNumber']),
                'pick_number': int(json_obj['PickNumber']),
                'card_ids': [int(x) for x in json_obj['DraftPack']],
            }
            logging.info(f'Draft pack: {pack}')
            response = self.__retry_post(f'{self.host}/{ENDPOINT_DRAFT_PACK}', blob=pack)

    def __handle_draft_pick(self, json_obj):
        """Handle 'Draft.MakePick messages."""
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
        logging.info(f'Draft pick: {pick}')
        response = self.__retry_post(f'{self.host}/{ENDPOINT_DRAFT_PICK}', blob=pick)

    def __handle_deck_submission(self, json_obj):
        """Handle 'Event.DeckSubmit' messages."""
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
        logging.info(f'Deck submission: {deck}')
        response = self.__retry_post(f'{self.host}/{ENDPOINT_DECK_SUBMISSION}', blob=deck)

    def __handle_deck_submission_v3(self, json_obj):
        """Handle 'Event.DeckSubmitV3' messages."""
        inner_obj = json_obj['params']
        deck_info = json.loads(inner_obj['deck'])
        deck = {
            'player_id': self.cur_user,
            'event_name': inner_obj['eventName'],
            'time': self.cur_log_time.isoformat(),
            'maindeck_card_ids': self.__get_card_ids_from_decklist_v3(deck_info['mainDeck']),
            'sideboard_card_ids': self.__get_card_ids_from_decklist_v3(deck_info['sideboard']),
            'is_during_match': False,
        }
        logging.info(f'Deck submission: {deck}')
        response = self.__retry_post(f'{self.host}/{ENDPOINT_DECK_SUBMISSION}', blob=deck)

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

def validate_uuid_v4(maybe_uuid):
    try:
        uuid.UUID(maybe_uuid, version=4)
        return maybe_uuid
    except ValueError:
        return None

def get_client_token():
    import configparser
    token = None
    config = configparser.ConfigParser()
    if os.path.exists(CONFIG_FILE):
        config.read(CONFIG_FILE)
        if 'client' in config:
            token = validate_uuid_v4(config['client'].get('token'))

    if token is None or validate_uuid_v4(token) is None:
        import tkinter
        import tkinter.simpledialog
        import tkinter.messagebox

        window = tkinter.Tk()
        window.wm_withdraw()

        message = 'Please enter your client token from 17lands.com/account:'
        while True:
            token = tkinter.simpledialog.askstring('MTGA Log Client Token', message)

            if token is None:
                tkinter.messagebox.showerror(
                    'Error: Client Token Needed',
                    'The program cannot continue without specifying a client token. Exiting.'
                )
                exit(1)

            if validate_uuid_v4(token) is None:
                message = 'That token is invalid. Please specify a valid client token. See 17lands.com/getting_started for more details.'
            else:
                break

        config['client'] = {'token': token}
        with open(CONFIG_FILE, 'w') as f:
            config.write(f)

    return token

def verify_valid_version(host):
    for i in range(3):
        response = requests.get(f'{host}/{ENDPOINT_CLIENT_VERSION}')
        if not IS_CODE_FOR_RETRY(response.status_code):
            break
        logging.warning(f'Got response code {response.status_code}; retrying')
        time.sleep(DEFAULT_RETRY_SLEEP_TIME)
    else:
        logging.warning('Could not get response from server for minimum client version. Assuming version is valid.')
        return

    logging.info(f'Got minimum client version response: {response.text}')
    blob = json.loads(response.text)
    this_version = [int(i) for i in CLIENT_VERSION.split('.')]
    min_supported_version = [int(i) for i in blob['min_version'].split('.')]
    logging.info(f'Minimum supported version: {min_supported_version}; this version: {this_version}')

    if this_version >= min_supported_version:
        return

    import tkinter
    import tkinter.messagebox
    window = tkinter.Tk()
    window.wm_withdraw()
    tkinter.messagebox.showerror(
        'MTGA Log Client Error: Client Update Needed',
        (f'The minimum supported version for the client is {blob["min_version"]}. '
            + f'Your current version is {CLIENT_VERSION}. Please download the latest '
            + 'version of the client from https://github.com/rconroy293/mtga-log-client')
    )
    exit(1)


if __name__ == '__main__':
    import argparse

    parser = argparse.ArgumentParser(description='MTGA log follower')
    parser.add_argument('-l', '--log_file',
        help=f'Log filename to process. If not specified, will try one of {POSSIBLE_FILEPATHS}')
    parser.add_argument('--host', default=API_ENDPOINT,
        help=f'Host to submit requsts to. If not specified, will use {API_ENDPOINT}')
    parser.add_argument('--once', action='store_true',
        help='Whether to stop after parsing the file once (default is to continue waiting for updates to the file)')

    args = parser.parse_args()

    verify_valid_version(args.host)

    token = get_client_token()
    logging.info(f'Using token {token}')

    filepaths = POSSIBLE_FILEPATHS
    if args.log_file is not None:
        filepaths = (args.log_file, )

    follow = not args.once

    follower = Follower(token, host=args.host)
    for filename in filepaths:
        if os.path.exists(filename):
            logging.info(f'Following along {filename}')
            follower.parse_log(filename=filename, follow=follow)