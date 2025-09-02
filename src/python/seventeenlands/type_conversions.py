from datetime import datetime, timedelta
from typing import Any

import dateutil


MAX_MILLISECONDS_SINCE_EPOCH = int(1000 * datetime(3000, 1, 1).timestamp())


def int_to_datetime(value: Any) -> datetime:
    try:
        timestamp_value = int(value)

        if timestamp_value < MAX_MILLISECONDS_SINCE_EPOCH:
            return datetime.fromtimestamp(timestamp_value * 0.001)

        else:
            seconds_since_year_1 = timestamp_value / 10000000
            return datetime.fromordinal(1) + timedelta(seconds=seconds_since_year_1)

    except ValueError:
        return dateutil.parser.isoparse(value)
