from seventeenlands.model import (
    ExtractJSONValue,
    ExtractedValueReference,
    ExtractRegexGroup,
    LogParsing,
    MessageDelimiter,
    RuleSet,
    StoreState,
    TypeConversion,
)

DEFAULT_RULE_SET = RuleSet(
    version="0.0.1",
    state_prerequisites=[],
    message_delimiters=[
        MessageDelimiter(
            regex=r"^\[(UnityCrossThreadLogger|Client GRE)\](\d[\d:/ .-]+(AM|PM)?)",
            timestamp_group=2,
        ),
        MessageDelimiter(
            regex=r"^\[(UnityCrossThreadLogger|Client GRE)\]",
            timestamp_group=None,
        ),
    ],
    rules=[
        LogParsing(
            continue_if_match=True,
            extract_json=False,
            # log_message="Extracted player id from match-to-player message: {player_id}",
            match_regex=".*: Match to (\w+):",
            extractions={
                "player_id": ExtractRegexGroup(group=1),
            },
            actions=[
                StoreState(
                    reference=ExtractedValueReference(key="player_id"),
                    state_key="player_id",
                    state_groups=["login"],
                ),
            ],
        ),
        LogParsing(
            continue_if_match=True,
            extract_json=False,
            # log_message="Extracted player id from player-to-match message: {player_id}",
            match_regex=".*: (\w+) to Match:",
            extractions={
                "player_id": ExtractRegexGroup(group=1),
            },
            actions=[
                StoreState(
                    reference=ExtractedValueReference(key="player_id"),
                    state_key="player_id",
                    state_groups=["login"],
                ),
            ],
        ),
        LogParsing(
            continue_if_match=True,
            extract_json=False,
            log_message="Extracted screen name from login message: {full_screen_name}",
            match_regex="Logged in successfully\. Display Name: (.*)",
            match_method="search",
            extractions={
                "full_screen_name": ExtractRegexGroup(group=1),
            },
            actions=[
                StoreState(
                    reference=ExtractedValueReference(key="full_screen_name"),
                    state_key="full_screen_name",
                    state_groups=["login"],
                ),
            ],
        ),
        LogParsing(
            continue_if_match=True,
            # log_message="Extracted utc_timestamp top-level timestamp: {utc_timestamp}",
            match_regex='"timestamp":',
            match_method="search",
            extractions={
                "utc_timestamp": ExtractJSONValue(path=["timestamp"]),
            },
            transformations={
                "utc_timestamp": TypeConversion.INT_TO_DATETIME,
            },
            actions=[
                StoreState(
                    reference=ExtractedValueReference(key="utc_timestamp"),
                    state_key="last_utc_time",
                ),
            ],
        ),
        LogParsing(
            continue_if_match=True,
            # log_message="Extracted event_time from request: {event_time}",
            match_regex='"request":.*\\\\"EventTime\\\\"',
            match_method="search",
            extractions={
                "event_time": ExtractJSONValue(path=["request", None, "EventTime"]),
            },
            actions=[
                StoreState(
                    reference=ExtractedValueReference(key="event_time"),
                    state_key="last_event_time",
                ),
            ],
        ),
        LogParsing(
            log_message="Extracted cur_draft_event from request: {cur_draft_event}",
            match_regex='Event_Join.*"request":.*\\\\"EventName\\\\"',
            match_method="search",
            extractions={
                "blob": ExtractJSONValue(path=[]),
                "cur_draft_event": ExtractJSONValue(
                    path=["request", None, "EventName"]
                ),
            },
            actions=[
                StoreState(
                    reference=ExtractedValueReference(key="event_time"),
                    state_key="last_event_time",
                ),
            ],
        ),
    ],
)
