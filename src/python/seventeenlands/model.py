from enum import Enum
import json
import re
from functools import cached_property
from typing import Annotated, Any, Literal, Optional, Union

from pydantic import BaseModel, ConfigDict, Discriminator, Field
from typing_extensions import assert_never

from seventeenlands import type_conversions


class FrozenIgnoreExtras(BaseModel):
    model_config = ConfigDict(
        frozen=True,
        extra="ignore",
    )


class TypeConversion(str, Enum):
    INT_TO_DATETIME = "int_to_datetime"

    def apply(self, value: Any) -> Any:
        if self == TypeConversion.INT_TO_DATETIME:
            return type_conversions.int_to_datetime(value)
        else:
            assert_never(self)


class ExtractRegexGroup(FrozenIgnoreExtras):
    action: Literal["extract_regex_group"] = "extract_regex_group"
    group: int = Field(
        ...,
        description=(
            "The group number (1-indexed) of the value to extract from the regular "
            + "expression that matched the log line. 0 indicates the whole match."
        ),
    )

    def extract(
        self, message: str, match: re.Match[str], blob: Optional[dict[str, Any]] = None
    ) -> Any:
        """Extract value from regex match group."""
        return match.group(self.group)


class ExtractJSONValue(FrozenIgnoreExtras):
    action: Literal["extract_json_field"] = "extract_json_field"
    path: list[Union[str, int, None]] = Field(
        ...,
        description=(
            "A sequence of keys (for objects) and indices (for arrays) to the value "
            "to extract from the JSON object. `None` indicates the corresponding "
            "value at that position is a string that should be decoded as JSON "
            "which can be indexed into further."
        ),
    )

    _DECODE_JSON_FLAG = None

    def extract(
        self,
        message: str,
        match: Optional[re.Match[str]] = None,
        blob: Optional[dict[str, Any]] = None,
    ) -> Any:
        """Extract value from JSON data using the specified path."""
        if blob is None:
            raise ValueError(
                f"No JSON data found in log message starting with {message[:50]}"
            )

        current = blob
        for step in self.path:
            if step is self._DECODE_JSON_FLAG:
                if isinstance(current, str):
                    try:
                        current = json.loads(current)
                    except json.JSONDecodeError:
                        raise ValueError(
                            f"Failed to decode JSON nested in: {current[:50]}"
                        )
                else:
                    raise ValueError(
                        f"No string to decode JSON nested in: {str(current)[:50]}"
                    )

            elif isinstance(step, str):
                if isinstance(current, dict):
                    if step in current:
                        current = current[step]
                    else:
                        raise ValueError(f"Key '{step}' not found in: {repr(current)}")
                else:
                    raise ValueError(
                        f"No object to access key '{step}' in: {str(current)[:50]}"
                    )

            elif isinstance(step, int):
                if isinstance(current, list):
                    if 0 <= step < len(current):
                        current = current[step]
                    else:
                        raise ValueError(
                            f"Array index out of bounds: {step} not in [0, {len(current)}]"
                        )
                else:
                    raise ValueError(
                        f"No array to access index '{step}' in: {str(current)[:50]}"
                    )

            else:
                raise ValueError(f"Invalid path step: {step}")

        return current


Extraction = Annotated[
    Union[ExtractRegexGroup, ExtractJSONValue],
    Discriminator("action"),
]


class StateValueReference(FrozenIgnoreExtras):
    type: Literal["state_value"] = "state_value"
    key: str


class ExtractedValueReference(FrozenIgnoreExtras):
    type: Literal["extracted_value"] = "extracted_value"
    key: str


ValueReference = Annotated[
    Union[StateValueReference, ExtractedValueReference],
    Discriminator("type"),
]


class StoreState(FrozenIgnoreExtras):
    action: Literal["store_state"] = "store_state"
    reference: ValueReference
    state_key: str
    state_groups: list[str] = []


class ClearState(FrozenIgnoreExtras):
    action: Literal["clear_state"] = "clear_state"
    state_key: str


class ClearGroupState(FrozenIgnoreExtras):
    action: Literal["clear_group_state"] = "clear_group_state"
    state_group: str


class CallAPI(FrozenIgnoreExtras):
    action: Literal["call_api"] = "call_api"
    path: str
    method: Literal["GET", "POST"]
    query_params: dict[str, ValueReference] = Field(
        ...,
        description="Query parameters to be filled by state or extracted values.",
    )
    body_params: dict[str, ValueReference] = Field(
        ...,
        description="Body parameters to be filled by state or extracted values.",
    )


Action = Annotated[
    Union[StoreState, ClearState, ClearGroupState, CallAPI],
    Discriminator("action"),
]


class MessageDelimiter(FrozenIgnoreExtras):
    regex: str = Field(
        ...,
        description="Regular expression that marks the start of a new message",
    )
    timestamp_group: Optional[int] = Field(
        ...,
        description="Regular expression group containing the message timestamp, if any.",
    )

    @cached_property
    def compiled_regex(self) -> re.Pattern[str]:
        return re.compile(self.regex)


class LogParsing(FrozenIgnoreExtras):
    continue_if_match: bool = Field(
        default=False,
        description="Whether to continue applying other parsing rules to this line if this matches.",
    )
    extract_json: bool = Field(
        default=True,
        description="Whether to extract a JSON blob from the log line.",
    )

    log_message: Optional[str] = Field(
        default=None,
        description="A message to log when this parsing rule is applied. Can include extracted values as format parameters.",
    )
    match_regex: str = Field(
        ...,
        description="A regular expression for matching log lines.",
    )
    match_method: Literal["match", "search"] = Field(
        default="match",
        description="The method to use for matching log lines.",
    )
    extractions: dict[str, Extraction] = Field(
        ...,
        description="A mapping of extraction names to their extraction definitions.",
    )
    transformations: dict[str, TypeConversion] = Field(
        default={},
        description="Type conversions to apply to each extraction before use in actions.",
    )
    actions: list[Action] = Field(
        ...,
        description="An ordered list of actions to take when a log line matches the regex.",
    )

    @cached_property
    def _compiled_regex(self) -> re.Pattern[str]:
        return re.compile(self.match_regex)

    def match(self, message: str) -> Optional[re.Match[str]]:
        if self.match_method == "match":
            return self._compiled_regex.match(message)
        elif self.match_method == "search":
            return self._compiled_regex.search(message)
        else:
            assert_never(self.match_method)


class RuleSet(FrozenIgnoreExtras):
    version: str = Field(
        ...,
        description="The version of the rule set.",
    )
    state_prerequisites: list[str] = Field(
        ...,
        description="A list of state keys that must be present before applying the rules.",
    )
    message_delimiters: list[MessageDelimiter] = Field(
        ...,
        description="Patterns that mark the beginning of new log messages",
    )
    rules: list[LogParsing] = Field(
        ...,
        description="An ordered list of rules to apply to log lines.",
    )
