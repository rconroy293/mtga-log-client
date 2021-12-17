# Draft Dash Client

This is a locally-hosted Dash app (requires basic python knowledge) that helps you keep track of your drafts
in real time (...almost). It works by extending the functionality of the default `mtga_client` and removing
the interaction with `17lands.com`. Instead, it regularly re-parses your log file (only bothering with the
events it needs to watch your draft) to provide you the 17Lands stats relating to your chosen cards. It also
provides basic summaries of your deck.

![Demo of the app](/img/example.png)

## Considerations

This is not meant to replace the `17Lands` client, you can still have that running.

This is forked from the client itself, and all the credit for the hard work goes to 
the team at http://www.17lands.com.

## Installation/Run Instructions

- clone the repo
- get a python env of your choice (conda, venv, pyenv, etc)
- pip install dash pandas lxml nb_black dash-bootstrap-components
- download card_list.csv from 17Lands https://17lands-public.s3.amazonaws.com/analysis_data/cards/card_list.csv
- save each set you care about as a different page under one dir. Filename should be data/{set}_card_ratings.html 
  - this can be done by going to https://www.17lands.com/card_ratings and changing the set, then right-clicking and using "save as"
- start this jupyter notebook (making sure the card_list and set data are in data/) with `jupyter notebook` to start the server, then open the file
- update the path to your log file (can be found in 17Lands client)
- run all the cells - the last one will start the app, and will run continuously
- go to localhost:8050/ to see the app!


## Notes

Licensed under GNU GPL v3.0 (see included LICENSE).

This MTGA log follower is unofficial Fan Content permitted under the Fan Content Policy. Not approved/endorsed by Wizards. Portions of the materials used are property of Wizards of the Coast. ©Wizards of the Coast LLC. See https://company.wizards.com/fancontentpolicy for more details.