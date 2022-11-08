import setuptools

with open("README.md", "r") as fh:
    long_description = fh.read()

setuptools.setup(
    name="seventeenlands",
    version="0.1.35",
    author="Robert Conroy",
    author_email="seventeenlands@gmail.com",
    description="Utility to upload MTG Arena data to 17Lands.com",
    long_description=long_description,
    long_description_content_type="text/markdown",
    url="https://github.com/rconroy293/mtga-log-client",
    packages=setuptools.find_packages(),
    classifiers=[
        "Programming Language :: Python :: 3",
        "Operating System :: OS Independent",
    ],
    python_requires='>=3.6',
    entry_points={
        'console_scripts': [
            'seventeenlands=seventeenlands.mtga_follower:main',
        ],
    },
    install_requires=[
        "requests",
        "python-dateutil",
    ],
)
