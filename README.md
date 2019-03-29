# mtga-log-client

Simple client for passing relevant events from MTG Arena logs to a REST endpoint.

## Basic Usage

On Windows, simply download and run `mtga_follower.exe`. This version will have a console window that shows you everything that's being uploaded. You can kick this off before you start MTG Arena and have it run while you play.

If you'd prefer to have something started automatically and run in the background, you can have it start itself at startup and remain hidden (it shouldn't take up much in the way of resources). To make this happen, download the `mtga_follower_hidden.exe` file and do the following two steps:
1. Create a shortcut to `mtga_follower_hidden.exe` (right click -> Create Shortcut)
2. Move the shortcut to your startup folder (paste this into your file explorer addresss bar to find the startup folder: `%appdata%\Microsoft\Windows\Start Menu\Programs\Startup`)

## Advanced Usage

Requires [Python 3.6+](https://www.python.org/downloads/), along with the [`requests` package](http://docs.python-requests.org/en/master/).

You can kick this off before you start MTG Arena and have it run in the background while you play. If you have Python installed on Windows, you can simply run the script directly. If you want to run through a terminal, the command would just be as follows:
```
python3 mtga_follower.py
```

Additional options are available by passing the `-h` flag to the program.

The log messages will show you what's being sent to the server. You can see more information about the data it's submitting here: http://www.17lands.com/ui/.

## Notes

Licensed under GNU GPL v3.0 (see included LICENSE).

This MTGA log follower is unofficial Fan Content permitted under the Fan Content Policy. Not approved/endorsed by Wizards. Portions of the materials used are property of Wizards of the Coast. ©Wizards of the Coast LLC. See https://company.wizards.com/fancontentpolicy for more details.