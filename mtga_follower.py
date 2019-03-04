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

from collections import namedtuple

import requests

logging.basicConfig(
    format='%(asctime)s,%(levelname)s,%(message)s',
    datefmt='%Y%m%d %H%M%S',
    level=logging.INFO,
)

PATH_ON_DRIVE = os.path.join('users',getpass.getuser(),'AppData','LocalLow','Wizards Of The Coast','MTGA','output_log.txt')
POSSIBLE_FILEPATHS = (
    # Windows
    os.path.join('C:/',PATH_ON_DRIVE),
    os.path.join('D:/',PATH_ON_DRIVE),
    # Lutris
    os.path.join(os.path.expanduser("~"),'Games','magic-the-gathering-arena','drive_c',PATH_ON_DRIVE),
    # Wine
    os.path.join(os.path.expanduser("~"),'.wine','drive_c',PATH_ON_DRIVE),
)

LOG_START_REGEX = re.compile(r'^\[(UnityCrossThreadLogger|Client GRE)\]([\d:/ -]+(AM|PM)?)')
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

API_ENDPOINT = 'https://mtg-draft-logger.herokuapp.com'
ENDPOINT_DECK_SUBMISSION = 'deck'
ENDPOINT_GAME_RESULT = 'game'
ENDPOINT_DRAFT_PACK = 'pack'
ENDPOINT_DRAFT_PICK = 'pick'

RETRIES = 2
IS_CODE_FOR_RETRY = lambda code: code >= 500 and code < 600
DEFAULT_RETRY_SLEEP_TIME = 1

def retry_post(endpoint, blob, num_retries=RETRIES, sleep_time=DEFAULT_RETRY_SLEEP_TIME):
    """
    Send data to an endpoint via post request, retrying on server errors.

    :param endpoint:    The http endpoint to hit.
    :param blob:        The JSON data to send in the body of the post request.
    :param num_retries: The number of times to retry upon failure.
    :param sleep_time:  In seconds, the time to sleep between tries.

    :returns: The response object (including status_code and text fields).
    """
    tries_left = num_retries + 1
    while tries_left > 0:
        tries_left -= 1
        response = requests.post(endpoint, json=blob)
        if not IS_CODE_FOR_RETRY(response.status_code):
            return response
        logging.warning(f'Got response code {response.status_code}; retrying {tries_left} more times')
        time.sleep(sleep_time)
    return response

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

    def __init__(self):
        self.buffer = []
        self.cur_log_time = None
        self.json_decoder = json.JSONDecoder()
        self.draft_data = None
        self.cur_user = None

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
        match = LOG_START_REGEX.match(line)
        if match:
            self.__handle_complete_log_entry()
            self.cur_logger, self.cur_log_time = (match.group(1), match.group(2))
            self.cur_log_time = extract_time(self.cur_log_time)
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
        self.cur_logger = None
        self.cur_log_time = None

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

        if json_value_matches('Client.SceneChange', ['params', 'messageName'], json_obj):
            self.__handle_scene_change(json_obj)
        elif json_value_matches('DuelScene.GameStop', ['params', 'messageName'], json_obj):
            self.__handle_game_end(json_obj)
        elif 'draftStatus' in json_obj:
            self.__handle_draft_log(json_obj)
        elif json_value_matches('Draft.MakePick', ['method'], json_obj):
            self.__handle_draft_pick(json_obj)
        elif json_value_matches('Event.DeckSubmit', ['method'], json_obj):
            self.__handle_deck_submission(json_obj)
        elif 'greToClientEvent' in json_obj and 'greToClientMessages' in json_obj['greToClientEvent']:
            for message in json_obj['greToClientEvent']['greToClientMessages']:
                self.__handle_gre_to_client_message(message)

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
            response = retry_post(f'{API_ENDPOINT}/{ENDPOINT_DECK_SUBMISSION}', blob=deck)
            logging.info(f'{response.status_code} Response: {response.text}')

    def __handle_game_end(self, json_obj):
        """Handle 'DuelScene.GameStop' messages."""
        blob = json_obj['params']['payloadObject']
        assert blob['playerId'] == self.cur_user, f'Expected user {blob["playerId"]} to be {self.cur_user}'

        game = {
            'player_id': self.cur_user,
            'event_name': blob['eventId'],
            'match_id': blob['matchId'],
            'time': self.cur_log_time.isoformat(),
            'on_play': blob['teamId'] == blob['startingTeamId'],
            'won': blob['teamId'] == blob['winningTeamId'],
            'game_end_reason': blob['winningReason'],
            'mulligans': [[x['grpId'] for x in hand] for hand in blob['mulliganedHands']],
            'turns': blob['turnCount'],
            'duration': blob['secondsCount'],
        }
        logging.info(f'Completed game: {game}')
        response = retry_post(f'{API_ENDPOINT}/{ENDPOINT_GAME_RESULT}', blob=game)
        logging.info(f'{response.status_code} Response: {response.text}')

    def __handle_scene_change(self, json_obj):
        """Handle 'Client.SceneChange' messages."""
        self.cur_user = json_obj['params']['payloadObject']['playerId']

    def __handle_draft_log(self, json_obj):
        """Handle 'draftStatus' messages."""
        if json_obj['draftStatus'] == 'Draft.PickNext':
            pack = {
                'player_id': self.cur_user,
                'event_name': json_obj['eventName'],
                'time': self.cur_log_time.isoformat(),
                'pack_number': int(json_obj['packNumber']),
                'pick_number': int(json_obj['pickNumber']),
                'card_ids': [int(x) for x in json_obj['draftPack']],
            }
            logging.info(f'Draft pack: {pack}')
            response = retry_post(f'{API_ENDPOINT}/{ENDPOINT_DRAFT_PACK}', blob=pack)
            logging.info(f'{response.status_code} Response: {response.text}')

    def __handle_draft_pick(self, json_obj):
        """Handle 'Draft.MakePick messages."""
        inner_obj = json_obj['params']
        (user, event_name, other) = inner_obj['draftId'].rsplit(':', 2)
        assert user == self.cur_user, f'Expected user {user} to be {self.cur_user}'

        pick = {
            'player_id': self.cur_user,
            'event_name': event_name,
            'time': self.cur_log_time.isoformat(),
            'pack_number': int(inner_obj['packNumber']),
            'pick_number': int(inner_obj['pickNumber']),
            'card_id': int(inner_obj['cardId']),
        }
        logging.info(f'Draft pick: {pick}')
        response = retry_post(f'{API_ENDPOINT}/{ENDPOINT_DRAFT_PICK}', blob=pick)
        logging.info(f'{response.status_code} Response: {response.text}')

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
        response = retry_post(f'{API_ENDPOINT}/{ENDPOINT_DECK_SUBMISSION}', blob=deck)
        logging.info(f'{response.status_code} Response: {response.text}')

if __name__ == '__main__':
    import argparse

    parser = argparse.ArgumentParser(description='MTGA log follower')
    parser.add_argument('-l', '--log_file',
        help=f'Log filename to process. If not specified, will try one of {POSSIBLE_FILEPATHS}')
    parser.add_argument('--once', action='store_true',
        help='Whether to stop after parsing the file once (default is to continue waiting for updates to the file)')

    args = parser.parse_args()

    filepaths = POSSIBLE_FILEPATHS
    if args.log_file is not None:
        filepaths = (args.log_file, )

    follow = not args.once

    follower = Follower()
    for filename in filepaths:
        if os.path.exists(filename):
            logging.info(f'Following along {filename}')
            follower.parse_log(filename=filename, follow=follow)