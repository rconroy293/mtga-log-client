"""
Main entry point for rule-based MTGA log parsing.
"""

import argparse
import datetime
import json
import os
import pathlib
import time

import seventeenlands.logging_utils
from seventeenlands.default_rule_set import DEFAULT_RULE_SET
from seventeenlands.log_message_assembler import LogMessageAssembler
from seventeenlands.model import RuleSet
from seventeenlands.rule_based_parser import RuleBasedParser

logger = seventeenlands.logging_utils.get_logger("17Lands")

# Constants
SLEEP_TIME = datetime.timedelta(seconds=0.5)
FILE_UPDATED_FORCE_REFRESH_SECONDS = datetime.timedelta(seconds=60)


def parse_log_file(
    rule_set: RuleSet, filename: str, client_token: str, follow: bool = False, verbose: bool = False
) -> None:
    """
    Parse a log file using the specified rule set.
    """
    while True:
        assembler = LogMessageAssembler(rule_set.message_delimiters)
        parser = RuleBasedParser(rule_set, client_token=client_token)

        last_read_time = time.time()
        last_file_size = 0

        try:
            with open(filename, errors="replace") as f:
                while True:
                    line = f.readline()
                    file_size = pathlib.Path(filename).stat().st_size

                    if line:
                        for complete_message in assembler.process_line(line):
                            parser.process_message(complete_message)
                        last_read_time = time.time()
                        last_file_size = file_size
                    else:
                        for complete_message in assembler.get_remainder():
                            parser.process_message(complete_message)

                        if not follow:
                            break

                        last_modified_time = os.stat(filename).st_mtime
                        if file_size < last_file_size:
                            logger.info(
                                f"File size decreased (was {last_file_size}, now {file_size}). "
                                "Restarting from beginning."
                            )
                            break
                        elif (
                            last_modified_time
                            > last_read_time
                            + FILE_UPDATED_FORCE_REFRESH_SECONDS.total_seconds()
                        ):
                            logger.info(
                                "File updated much more recently than last read. Restarting from beginning."
                            )
                            break
                        else:
                            time.sleep(SLEEP_TIME.total_seconds())

        except FileNotFoundError:
            logger.warning(f"File {filename} not found. Waiting...")

        except Exception as e:
            logger.error(f"Error processing log file: {e}")

        if not follow:
            if verbose:
                logger.info("Final parser state:")
                logger.info(f"  Main state: {parser.get_state()}")
                logger.info(f"  Login group state: {parser.get_group_state('login')}")
                logger.info(f"  Last timestamp: {assembler.get_last_timestamp()}")
            logger.info("Done processing file.")
            break

        time.sleep(SLEEP_TIME.total_seconds())


def main() -> None:
    """
    Main entry point for rule-based log parsing.
    """
    parser = argparse.ArgumentParser(description="Rule-based MTGA log parser")

    parser.add_argument(
        "-l",
        "--log_file",
        required=True,
        help="Log filename to process",
    )
    parser.add_argument(
        "--once",
        action="store_true",
        help="Process the file once and exit (default is to follow the file for updates)",
    )
    parser.add_argument(
        "-v",
        "--verbose",
        action="store_true",
        help="Enable verbose logging of message processing",
    )
    parser.add_argument(
        "-t",
        "--token",
        required=True,
        help="Client token for API authentication",
    )

    args = parser.parse_args()

    follow = not args.once
    logger.info(f"Starting rule-based parser for {args.log_file} ({follow=})")

    parse_log_file(
        rule_set=DEFAULT_RULE_SET,
        filename=args.log_file,
        client_token=args.token,
        follow=follow,
        verbose=args.verbose,
    )


if __name__ == "__main__":
    main()
