"""
Rule-based log parser for MTGA logs.

This module provides a generic implementation for parsing log files using
configurable rule sets defined in the model.py file.
"""

import json
import re
from collections import defaultdict
from typing import Any, Optional

from typing_extensions import assert_never

import seventeenlands.logging_utils
from seventeenlands import __version__
from seventeenlands.model import (
    Action,
    CallAPI,
    ClearGroupState,
    ClearState,
    Condition,
    ConditionOperator,
    ExtractedValueReference,
    LogParsing,
    RuleSet,
    StateValueReference,
    StoreState,
    ValueReference,
)

logger = seventeenlands.logging_utils.get_logger("17Lands")


JSON_START_PATTERN = re.compile(r"[\[\{]")


class RuleBasedParser:
    """
    A generic parser that applies rules from a RuleSet to log lines.
    """

    def __init__(self, rule_set: RuleSet, client_token: str) -> None:
        self.rule_set = rule_set
        self.state: dict[str, Any] = {
            "client_version": f"{__version__}.p",
            "token": client_token,
        }
        self.group_keys: dict[str, set[str]] = defaultdict(set)
        self.json_decoder = json.JSONDecoder()

        self.check_prerequisites()

    def process_message(self, message: str) -> None:
        """
        Process a single log line through the rule set.
        """
        for rule in self.rule_set.rules:
            if match := rule.match(message):
                extractions = self._extract_values(message, match, rule)
                if not self._conditions_are_met(rule.conditions, extractions):
                    continue

                self._perform_actions(rule.actions, extractions)

                if rule.log_message:
                    try:
                        logger.info(rule.log_message.format(**extractions))
                    except (KeyError, ValueError) as e:
                        logger.warning(
                            f"Failed to format log message '{rule.log_message}': {e}"
                        )

                if not rule.continue_if_match:
                    break

    def _extract_values(
        self, message: str, regex_match: re.Match, rule: LogParsing
    ) -> dict[str, Any]:
        """
        Extract values from a log message based on the rule's extraction definitions.
        """
        json_blob = (
            self._parse_json_from_message(message) if rule.extract_json else None
        )

        extractions = {}
        for name, extraction in rule.extractions.items():
            try:
                value = extraction.extract(
                    message=message, match=regex_match, blob=json_blob
                )
                if value is not None:
                    if transformation := rule.transformations.get(name):
                        value = transformation.apply(value)
                    extractions[name] = value

            except (IndexError, KeyError, json.JSONDecodeError) as e:
                logger.warning(
                    f"Failed to extract '{name}' from message; attempting processing anyway. {e}"
                )
                continue

        return extractions

    def _conditions_are_met(
        self, conditions: list[Condition], extractions: dict[str, Any]
    ) -> bool:
        for condition in conditions:
            left_value = self._resolve_value_reference(condition.left, extractions)
            right_value = (
                self._resolve_value_reference(condition.right, extractions)
                if isinstance(
                    condition.right, (StateValueReference, ExtractedValueReference)
                )
                else condition.right
            )

            if condition.operator == ConditionOperator.EQUALS:
                if left_value != right_value:
                    return False

        return True

    def _parse_json_from_message(self, message: str) -> Optional[dict[str, Any]]:
        """
        Attempt to parse JSON from a log message.
        """
        if (match := JSON_START_PATTERN.search(message)) is None:
            return None

        blob, end = self.json_decoder.raw_decode(message, match.start())
        return blob

    def _perform_actions(
        self, actions: list[Action], extractions: dict[str, Any]
    ) -> None:
        """
        Perform the specified actions using the extracted values.
        """
        for action in actions:
            try:
                if isinstance(action, StoreState):
                    self._store_state(action, extractions)

                elif isinstance(action, ClearState):
                    self._clear_state(action)

                elif isinstance(action, ClearGroupState):
                    self._clear_group_state(action)

                elif isinstance(action, CallAPI):
                    # TODO: Implement API calling logic
                    logger.info(f"API call action: {action}")
                    pass

                else:
                    assert_never(action)

            except Exception as e:
                logger.warning(f"Failed to perform action {action}: {e}")
                continue

    def _store_state(self, action: StoreState, extractions: dict[str, Any]) -> None:
        """
        Store a value in state based on the action configuration.
        """
        value = self._resolve_value_reference(action.reference, extractions)
        self.state[action.state_key] = value

        for group in action.state_groups:
            self.group_keys[group].add(action.state_key)

    def _clear_state(self, action: ClearState) -> None:
        """
        Clear a specific state key.
        """
        self.state.pop(action.state_key, None)

        for group_keys in self.group_keys.values():
            group_keys.discard(action.state_key)

    def _clear_group_state(self, action: ClearGroupState) -> None:
        """
        Clear all state for a specific group.
        """
        for key in self.group_keys[action.state_group]:
            self.state.pop(key, None)

        self.group_keys[action.state_group].clear()

    def _resolve_value_reference(
        self, reference: ValueReference, extractions: dict[str, Any]
    ) -> Any:
        """
        Resolve a value reference to an actual value.
        """
        if isinstance(reference, ExtractedValueReference):
            return extractions.get(reference.key)

        elif isinstance(reference, StateValueReference):
            return self.state.get(reference.key)

        else:
            assert_never(reference)

    def get_state(self) -> dict[str, Any]:
        """
        Get the current parser state.
        """
        return self.state.copy()

    def get_group_state(self, group: str) -> dict[str, Any]:
        """
        Get the current state for a specific group.
        """
        group_state = {}
        for key in self.group_keys.get(group, set()):
            if key in self.state:
                group_state[key] = self.state[key]
        return group_state

    def check_prerequisites(self) -> None:
        """
        Check if all state prerequisites are met. Raise an error if not.
        """
        for prerequisite in self.rule_set.state_prerequisites:
            if prerequisite not in self.state:
                raise RuntimeError(f"Missing prerequisite state: {prerequisite}")
