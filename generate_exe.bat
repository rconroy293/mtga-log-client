del mtga_follower.exe
pyinstaller --onefile mtga_follower.py
move dist\mtga_follower.exe .\
pyinstaller --onefile --noconsole mtga_follower.py
move dist\mtga_follower.exe .\mtga_follower_hidden.exe
rmdir /s /q __pycache__ dist build
del mtga_follower.spec