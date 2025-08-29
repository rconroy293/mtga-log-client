import datetime
import time
from typing import Callable, Optional, TypeVar

import requests.exceptions

import seventeenlands.logging_utils

T = TypeVar("T")

logger = seventeenlands.logging_utils.get_logger("retry_utils")

_INITIAL_RETRY_DELAY = datetime.timedelta(seconds=1)
_MAX_RETRY_DELAY = datetime.timedelta(minutes=10)
_MAX_TOTAL_RETRY_DURATION = datetime.timedelta(hours=24)


class RetryLimitExceededError(Exception):
    pass


def retry_until_successful(
    callback: Callable[[], T],
    response_validator: Callable[[T], bool],
    error_validator: Callable[[Exception], bool],
    initial_retry_delay: datetime.timedelta,
    max_retry_delay: Optional[datetime.timedelta],
    max_total_retry_duration: Optional[datetime.timedelta],
) -> T:
    last_call_at: Optional[datetime.datetime] = None
    if max_total_retry_duration:
        last_call_at = datetime.datetime.utcnow() + max_total_retry_duration

    next_retry_delay = initial_retry_delay
    while True:
        is_last_call = (
            last_call_at is not None and last_call_at < datetime.datetime.utcnow()
        )
        try:
            result = callback()
            if response_validator(result):
                return result
            elif is_last_call:
                raise RetryLimitExceededError()
        except Exception as e:
            if is_last_call or not error_validator(e):
                raise e

        time.sleep(next_retry_delay.total_seconds())
        next_retry_delay *= 2
        if max_retry_delay and max_retry_delay < next_retry_delay:
            next_retry_delay = max_retry_delay


def retry_api_call(
    callback: Callable[[], T],
    response_validator: Callable[[T], bool],
) -> T:
    def _should_retry_error(error: Exception) -> bool:
        logger.exception(f"Error: {error}")
        error_class = type(error)
        if issubclass(error_class, requests.exceptions.ConnectionError):
            return True
        return False

    return retry_until_successful(
        callback=callback,
        response_validator=response_validator,
        error_validator=_should_retry_error,
        initial_retry_delay=_INITIAL_RETRY_DELAY,
        max_retry_delay=_MAX_RETRY_DELAY,
        max_total_retry_duration=_MAX_TOTAL_RETRY_DURATION,
    )
