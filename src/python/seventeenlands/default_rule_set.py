from seventeenlands.model import (
    CallAPI,
    ClearGroupState,
    Condition,
    ConditionOperator,
    ExtractedValueReference,
    ExtractJSONValue,
    ExtractRegexGroup,
    LogParsing,
    MessageDelimiter,
    RuleSet,
    StoreState,
    TypeConversion,
)

_CLEAR_GAME_DATA = ClearGroupState(
    state_group="game",
)

DEFAULT_RULE_SET = RuleSet(
    version="0.0.1",
    state_prerequisites=[
        "_token",
        "_client_version",
    ],
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
            log_message="Extracted cur_draft_event from request: {event_name}",
            match_regex='Event_Join.*"request":.*\\\\"EventName\\\\"',
            match_method="search",
            extractions={
                "event_name": ExtractJSONValue(path=["request", None, "EventName"]),
            },
            actions=[
                StoreState(
                    reference=ExtractedValueReference(key="event_name"),
                    state_key="cur_draft_event",
                ),
            ],
        ),
        LogParsing(
            log_message="Handled bot draft pick via DraftStatus message",
            match_regex='"CurrentModule": ?"BotDraft".*\\\\"DraftStatus\\\\"',
            match_method="search",
            extractions={
                "blob": ExtractJSONValue(path=[]),
                "draft_status": ExtractJSONValue(
                    path=["Payload", None, "DraftStatus"],
                ),
                "event_name": ExtractJSONValue(
                    path=["Payload", None, "EventName"],
                ),
                "pack_number": ExtractJSONValue(
                    path=["Payload", None, "PackNumber"],
                ),
                "pick_number": ExtractJSONValue(
                    path=["Payload", None, "PickNumber"],
                ),
                "card_ids": ExtractJSONValue(
                    path=["Payload", None, "DraftPack"],
                ),
            },
            conditions=[
                Condition(
                    left=ExtractedValueReference(key="draft_status"),
                    operator=ConditionOperator.EQUALS,
                    right="PickNext",
                )
            ],
            actions=[
                _CLEAR_GAME_DATA,
                StoreState(
                    reference=ExtractedValueReference(key="event_name"),
                    state_key="cur_draft_event",
                ),
                CallAPI(
                    path="api/client/add_pack",
                    method="POST",
                    body_params={
                        "payload": ExtractedValueReference(key="blob"),
                        "event_name": ExtractedValueReference(key="event_name"),
                        "pack_number": ExtractedValueReference(key="pack_number"),
                        "pick_number": ExtractedValueReference(key="pick_number"),
                        "card_ids": ExtractedValueReference(key="card_ids"),
                    },
                ),
            ],
        ),
    ],
)
