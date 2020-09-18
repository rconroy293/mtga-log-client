import setuptools

setuptools.setup(
    name="test-seventeenlands",
    version="0.2.0",
    author="Example Author",
    author_email="author@example.com",
    description="Script to upload MTG Arena data to 17lands",
    long_description="Long description",
    long_description_content_type="text/plain",
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
