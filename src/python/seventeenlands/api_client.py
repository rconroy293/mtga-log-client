import datetime
import gzip
import json
from typing import Any, Dict, Optional

import requests

import seventeenlands.logging_utils
import seventeenlands.retry_utils


logger = seventeenlands.logging_utils.get_logger('api_client')

DEFAULT_HOST = 'https://www.17lands.com'

_ERROR_COOLDOWN = datetime.timedelta(minutes=2)


class ApiClient:

    def __init__(self, host: str):
        self.host = host
        self._last_error_posted_at = datetime.datetime.utcnow() - _ERROR_COOLDOWN

    def _retry_post(self, endpoint: str, blob: Any, use_gzip=False):
        def _send_request() -> requests.Response:
            args: Dict[str, Any] = {
                "url": f'{self.host}/{endpoint}',
            }

            if use_gzip:
                args["data"] = gzip.compress(json.dumps(blob).encode('utf8'))
                args["headers"] = {
                    "content-type": "application/json",
                    "content-encoding": "gzip",
                }
            else:
                args["json"] = blob

            logger.debug(f'Sending POST request: {args}')
            return requests.post(**args)

        def _validate_response(response: requests.Response) -> bool:
            logger.debug(f'{response.status_code} Response: {response.text}')
            return response.status_code < 500 or response.status_code >= 600

        return seventeenlands.retry_utils.retry_api_call(
            callback=_send_request,
            response_validator=_validate_response,
        )

    def _retry_get(self, endpoint, params):
        def _send_request() -> requests.Response:
            logger.debug(f'Sending GET to {self.host}/{endpoint}: {params}')
            return requests.get(f'{self.host}/{endpoint}', params=params)

        def _validate_response(response: requests.Response) -> bool:
            logger.debug(f'{response.status_code} Response: {response.text}')
            return response.status_code < 500 or response.status_code >= 600

        return seventeenlands.retry_utils.retry_api_call(
            callback=_send_request,
            response_validator=_validate_response,
        )

    def get_client_version_info(self, params: Dict):
        return self._retry_get(
            endpoint='api/version_validation',
            params=params,
        )

    def submit_collection(self, blob: Dict):
        return self._retry_post(endpoint='collection', blob=blob)

    def submit_deck_submission(self, blob: Dict):
        return self._retry_post(endpoint='deck', blob=blob)

    def submit_draft_pack(self, blob: Dict):
        return self._retry_post(endpoint='pack', blob=blob)

    def submit_draft_pick(self, blob: Dict):
        return self._retry_post(endpoint='pick', blob=blob)

    def submit_event_course_submission(self, blob: Dict):
        return self._retry_post(endpoint='event_course', blob=blob)

    def submit_event_ended(self, blob: Dict):
        return self._retry_post(endpoint='event_ended', blob=blob)

    def submit_event_submission(self, blob: Dict):
        return self._retry_post(endpoint='event', blob=blob)

    def submit_game_result(self, blob: Dict):
        return self._retry_post(endpoint='game', blob=blob, use_gzip=True)

    def submit_human_draft_pack(self, blob: Dict):
        return self._retry_post(endpoint='human_draft_pack', blob=blob)

    def submit_human_draft_pick(self, blob: Dict):
        return self._retry_post(endpoint='human_draft_pick', blob=blob)

    def submit_inventory(self, blob: Dict):
        return self._retry_post(endpoint='inventory', blob=blob)

    def submit_ongoing_events(self, blob: Dict):
        return self._retry_post(endpoint='ongoing_events', blob=blob)

    def submit_player_progress(self, blob: Dict):
        return self._retry_post(endpoint='player_progress', blob=blob)

    def submit_rank(self, blob: Dict):
        return self._retry_post(endpoint='api/rank', blob=blob)

    def submit_user(self, blob: Dict):
        return self._retry_post(endpoint='api/account', blob=blob)

    def submit_error_info(self, blob: Dict):
        now = datetime.datetime.utcnow()
        if self._last_error_posted_at > now - _ERROR_COOLDOWN:
            logger.warning(f'Waiting to post another error; last message was sent too recently ({self._last_error_posted_at.isoformat()})')
            return

        self._last_error_posted_at = now
        return self._retry_post(endpoint='api/client_errors', blob=blob, use_gzip=True)
