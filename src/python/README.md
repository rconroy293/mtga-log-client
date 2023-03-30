# mtga-log-client

Simple client for passing relevant events from MTG Arena logs to the 17Lands REST endpoint.

## Usage

To run, simply enter `seventeenlands` in your terminal. On first run, you will be prompted for your user token.

## Local Development Tools

It is recommended to do local development in a [virtualenv](https://virtualenv.pypa.io/en/latest/) In order to set
up a virtualenv for this project, run the following:
```shell
pip install virtualenv
virtualenv venv
. ./venv/bin/activate 
pip install --upgrade pip
pip install -r requirements.txt
```

To leave the virtualenv, simple run the following:
```shell
deactivate
```

### Mypy

This project requires usage of type hinting. This is enforced using
[mypy](http://mypy-lang.org/). The configuration we have set for mypy enforces every
function declaration to have type hints, including an explict `None` return type if
applicable.

Run the following from inside the virtualenv to validate types:
```shell
mypy --install-types --non-interactive
mypy seventeenlands
```

### Flake8

[Flake8](http://flake8.pycqa.org/en/latest/) handles some other more specific
linting. It takes care of line length for docstrings and comments. It also handles the
ordering of imports. In order to run flake8, run the following from inside the virtualenv:

```shell
flake8 seventeenlands
```

## Notes

Licensed under GNU GPL v3.0 (see included LICENSE).

This MTGA log follower is unofficial Fan Content permitted under the Fan Content Policy. Not approved/endorsed by Wizards. Portions of the materials used are property of Wizards of the Coast. ©Wizards of the Coast LLC. See https://company.wizards.com/fancontentpolicy for more details.
