import setuptools

setuptools.setup(
    name="seventeenlands",
    version="0.2.0",
    author="Example Author",
    author_email="author@example.com",
    description="Script to upload MTG Arena data to 17lands",
    url="https://github.com/rconroy293/mtga-log-client",
    packages=setuptools.find_packages(),
    classifiers=[
        "Programming Language :: Python :: 3",
        "Operating System :: OS Independent",
    ],
    python_requires='>=3.6',
    entry_points={
        'console_scripts': [
            '17lands=seventeenlands.mtga_follower:main',
        ],
    },
    install_requires=["requests", "python-dateutil"]

)
