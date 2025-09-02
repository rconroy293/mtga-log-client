"""
Log message assembler for MTGA logs.

This module handles the buffering and assembly of multi-line log messages
using configurable delimiter patterns.
"""

import re
from collections.abc import Iterator
from typing import Optional
from venv import logger

from seventeenlands.model import MessageDelimiter


class LogMessageAssembler:
    """
    Assembles complete log messages from individual lines using delimiter patterns.

    MTGA logs can have messages that span multiple lines, with specific patterns
    that indicate the start of a new message. This class buffers lines until
    a delimiter is encountered, then yields the complete message.
    """

    def __init__(self, delimiters: list[MessageDelimiter]):
        self.delimiters = delimiters
        self.buffer: list[str] = []
        # TODO: Convert this to a datetime
        self.last_timestamp: Optional[str] = None

    def process_line(self, line: str) -> Iterator[str]:
        """
        Process a single line and yield any complete messages.
        """
        if delimiter_match := self._check_delimiters(line):
            if self.buffer:
                complete_message = "".join(self.buffer)
                yield complete_message
                self.buffer.clear()

            delimiter, match = delimiter_match
            if delimiter.timestamp_group is not None:
                try:
                    self.last_timestamp = match.group(delimiter.timestamp_group)
                except IndexError:
                    logger.warning("Failed to extract timestamp")
                    pass

            if message_content := line[match.end() :]:
                self.buffer.append(message_content)
        else:
            self.buffer.append(line)

    def get_remainder(self) -> Iterator[str]:
        """
        Yield any remaining buffered content as a final message.
        """
        if self.buffer:
            complete_message = "".join(self.buffer)
            yield complete_message
            self.buffer.clear()

    def _check_delimiters(
        self, line: str
    ) -> Optional[tuple[MessageDelimiter, re.Match[str]]]:
        """
        Check if the line matches any delimiter patterns.
        """
        for delimiter in self.delimiters:
            if match := delimiter.compiled_regex.match(line):
                return (delimiter, match)
        return None

    def get_last_timestamp(self) -> Optional[str]:
        """
        Get the last extracted timestamp.
        """
        return self.last_timestamp
