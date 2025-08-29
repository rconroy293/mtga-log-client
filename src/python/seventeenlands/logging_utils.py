import logging
import logging.handlers
import os

_LOG_FOLDER = os.path.join(os.path.expanduser("~"), ".seventeenlands")
if not os.path.exists(_LOG_FOLDER):
    os.makedirs(_LOG_FOLDER)
_LOG_FILENAME = os.path.join(_LOG_FOLDER, "seventeenlands.log")

_LOG_FORMATTER = logging.Formatter(
    "%(asctime)s.%(msecs)03d,%(levelname)s,%(name)s,%(message)s",
    datefmt="%Y%m%d %H%M%S",
)
_HANDLERS: set[logging.Handler] = {
    logging.handlers.TimedRotatingFileHandler(
        _LOG_FILENAME,
        when="D",
        interval=1,
        backupCount=7,
        utc=True,
    ),
    logging.StreamHandler(),
}

_loggers: dict[str, logging.Logger] = {}


def get_logger(name: str) -> logging.Logger:
    if name in _loggers:
        return _loggers[name]

    logger = logging.getLogger(name)
    for handler in _HANDLERS:
        handler.setFormatter(_LOG_FORMATTER)
        logger.addHandler(handler)

    logger.setLevel(logging.INFO)
    logger.info(f"Saving logs to {_LOG_FILENAME}")

    _loggers[name] = logger

    return logger
