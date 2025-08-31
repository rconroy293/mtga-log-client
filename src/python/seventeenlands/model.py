from typing import Annotated, Literal, Union
from pydantic import BaseModel, ConfigDict, Discriminator, Field


class FrozenIgnoreExtras(BaseModel):
    model_config = ConfigDict(
        frozen=True,
        extra="ignore",
    )


class ExtractRegexGroup(FrozenIgnoreExtras):
    action: Literal["extract_regex_group"] = "extract_regex_group"
    group: int = Field(
        ...,
        description=(
            "The group number (1-indexed) of the value to extract from the regular "
            + "expression that matched the log line. 0 indicates the whole match."
        ),
    )


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


class LogParsing(FrozenIgnoreExtras):
    continue_if_match: bool = Field(
        default=False,
        description="Whether to continue applying other parsing rules to this line if this matches.",
    )
    extract_json: bool = Field(
        default=True, description="Whether to extract a JSON blob from the log line."
    )

    match_regex: str = Field(
        ...,
        description="A regular expression for matching log lines.",
    )
    extractions: dict[str, Extraction] = Field(
        ...,
        description="A mapping of extraction names to their extraction definitions.",
    )
    actions: list[Action] = Field(
        ...,
        description="An ordered list of actions to take when a log line matches the regex.",
    )


class RuleSet(FrozenIgnoreExtras):
    version: str = Field(
        ...,
        description="The version of the rule set.",
    )
    state_prerequisites: list[str] = Field(
        ...,
        description="A list of state keys that must be present before applying the rules.",
    )
    rules: list[LogParsing] = Field(
        ...,
        description="An ordered list of rules to apply to log lines.",
    )
