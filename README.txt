Explorer Native

A keyboard and screen reader first file manager for Windows, built to be fast
and to read well under NVDA. It runs on both ARM64 and x64 Windows 11 and 10.


Download

Open the Releases page of this repository and download the file for your
computer:

ExplorerNative-x64.exe for ordinary Intel and AMD PCs.
ExplorerNative-arm64.exe for Snapdragon and other ARM PCs.

It is one file. Run it from anywhere. Making it the default file explorer in
Preferences, or installing an update, puts a copy in
%LOCALAPPDATA%\Programs\ExplorerNative, and that copy is the one that runs from
then on.

Nothing else needs installing: everything it needs is inside the one file,
except WinFsp for Google Drive, which it offers to install when you connect.

Explorer Native is free and open source under the MIT licence (LICENSE.txt).


Finding files

Press F1 in any folder, type what you are looking for and press Enter. It
searches that folder and every folder inside it, on your PC or in Google Drive,
and lists what it finds with where each one is. Enter on a result takes you to
it in its folder. Escape stops a search that is still running, or takes you
back from the results. F5 runs the search again.

Type words in any order, without worrying about capitals or accents: greedy
tate finds Tate McRae - greedy.flac, and beyonce finds Beyonce with its accent.
A word can also be the name of a folder the file is in, so tate greedy finds
greedy.flac inside a Tate McRae folder. Put words in quotes to keep them
together. A star stands for any number of letters and a question mark for
one, so a star followed by .flac finds every FLAC file. Put a minus sign before a word to leave out anything with it, like love
-remix.


Keyboard shortcuts

Help, Keyboard shortcuts (Ctrl+F1) lists every key built into the window, a
section at a time: moving around, selecting, files and folders, searching and
the app. Arrow through it; Ctrl+Down and Ctrl+Up jump between sections. The
audio player's keys are your own, set in Preferences.


Favourite folders

Press Ctrl+D in a folder to make it a favourite. Ctrl+Shift+1 to Ctrl+Shift+9
go straight to favourites 1 to 9 from anywhere in the window. The Favourites
menu (Alt+V) lists them, adds or removes the folder you're in, and has
Organise favourites, where Alt+Up and Alt+Down change the order, Delete
removes one and Enter goes there.

Updates

If the window is open when Explorer Native updates, it opens again by itself
afterwards, in the same folders and tabs. If it was in the system tray, it
stays there.


Google Drive

No Google credentials ship with the app. In Google Cloud console, create an
OAuth client of type Desktop app. Then in Explorer Native open Preferences
(Ctrl+P), Google Drive, and paste its client ID and client secret into the two
fields. When you press OK they are checked with Google; if Google accepts
them they are saved encrypted and the browser opens for you to sign in.

Google Drive is a real drive, G: unless you choose another letter. Every
program can open, save, rename, move and delete files on it, and everything it
does happens in your Google Drive too. Nothing is kept on your PC's disk: a
big file goes up while it is being written, and a small one a few seconds after
it is closed, so a program's temporary files never reach Google. Deleting sends
things to the Google Drive trash. When Google cannot be reached, the drive
letter goes away, and it comes back by itself when the connection does.

The drive is made with WinFsp - Windows File System Proxy, Copyright (C) Bill
Zissimopoulos, https://github.com/winfsp/winfsp. If WinFsp is not on your PC
yet, Explorer Native offers to install it, from the WinFsp project, the first
time you connect Google Drive. Windows asks for permission once. If you say
no, Google Drive is turned off until you connect it again in Preferences.

In a folder you sync both ways, press Shift+F10 on a folder inside it and
choose Keep on Drive only. Once every file in it is checked to be in Google
Drive, it is removed from this PC and stays in Google Drive, and the rest
keeps syncing both ways. Put back on PC, on the same folder in Google Drive,
brings it back. The synced folder's settings list these folders too.

Everything Explorer Native saves in AppData (settings, the client ID and
secret, the sign-in, logs) is encrypted for your Windows account.


Describing pictures

Explorer Native can describe a picture out loud using an AI from Anthropic
(Claude), Google (Gemini), OpenAI (GPT) or OpenRouter. You need an API key from
one of them, and you pay that company for what you use, usually around a cent
a picture. Open Preferences (Ctrl+P), AI image describer, and choose Set up an
AI provider. It explains where to get a key, checks the key with the company,
and lets you choose a model. To change a key or add another company, run Set
up an AI provider again. Until an AI is set up, Enter on a picture tells you
so. Each time you go to a model list it asks the company for its current list.

Then press Enter on any picture. A window opens and the description is read
out. The edit box has the focus: type a question about the picture and press
Enter, such as what a sign says or what colour something is. Shift+Tab goes to
the conversation, where Ctrl+C copies a line and Ctrl+A then Ctrl+C copies all
of it. Tab after the edit box says what the conversation has cost so far.
Escape closes the window. Shift+Enter on a picture still opens it in its own
app, and Shift+F10 on a picture has AI description.

The picture and your questions are sent to the company you chose. It only
talks about the picture, and politely turns down anything else.


Apple Music

With an Apple Music subscription you can browse and play Apple Music inside
Explorer Native. It appears in your list of drives. Open it with the arrow
keys like any folder to reach your library, search and radio, and press Enter
on a song to play it. F1 searches Apple Music from anywhere inside it. Your play, pause, stop, skip and volume keys work on it,
and it plays on through an album, a playlist or a station on its own. When a playlist or album ends, Autoplay keeps playing songs like the last one; you can turn it off in Preferences, Apple Music. The Sort
menu offers orders that suit music, like recently added, artist, release date
and length.

No Apple credentials ship with the app. Sign in at music.apple.com in a
browser, export your cookies to a cookies.txt file (the same kind gamdl uses),
then in Explorer Native open Preferences (Ctrl+P), Apple Music, and load that
file. Your sign-in is saved encrypted on this PC only and is never put in a
settings export.

Playing goes through Apple's own player (MusicKit), so it uses the Microsoft
Edge WebView2 runtime that ships with Windows 11. Nothing is installed and
nothing is left on disk between runs. It plays through the device chosen in
Output device on the Audio menu, like your files. Songs and albums read the
artist first, so typing an artist's first letters takes you to their songs.
Songs you have favorited say so at the end. Shift+F10 on a song has Play
next, Favorite or Unfavorite, Add to playlist (with New playlist), Go to artist,
Go to album, and Add to or Remove from library. Shift+F10 anywhere in Apple
Music also has Shuffle, Repeat (off, all, this song) and, while a song plays,
Up next: the songs to come, which you can move with Alt+Up and Alt+Down, take
out with Delete, play with Enter, or clear. Enter on a station starts playing
it.

Next track and Previous track have no keys until you choose them in
Preferences, Audio, Configure keyboard shortcuts. In Apple Music they move
through the song's list; for your own files, through the audio files in the
track's folder.

The speed, pitch, equaliser, limiter and fades work on Apple Music the same
way they do for your own files, with the same settings and the same keys.
Silence reduction works too, with one difference: a file has its silences cut
out, while in Apple Music a silence is played through at four times the speed
until the sound comes back.

Ambience, on the Audio menu, plays only the ambience of a recording: the
room, the reverb, the noise and the space, with the voice taken out by a
neural network (DeepFilterNet) running on your own computer. It is at its
best on spoken and sung recordings; in a full music mix it takes out the
voice and leaves the instruments. It works on your own files, not Apple Music.


Internet radio and live streams

Press Enter on a .pls, .m3u, .m3u8, .asx or .xspf file that points at an
internet radio station or a live stream, and it plays in Explorer Native.
Shoutcast and Icecast stations work, and so does HLS, the kind of stream TV
and radio websites use, including ones with video, where you just hear the
sound. If the station sends song titles, Say what is playing tells you the
song and the station, and each new song shows in the status bar (you can have
it read out in Preferences, Speech). If the connection drops, it reconnects
by itself. Pausing holds your place for a few minutes. Live streams have no
length, so the skip keys don't apply. Next track and Previous track move to
the next or previous station file in the same folder. A playlist that lists
files on your PC instead of streams opens in its own app, as before.


Sharing a folder with other devices: SMB, FTP and SFTP servers

Explorer Native can share a folder or a whole drive from this PC to your
other computers, phones and media players. Preferences (Ctrl+P) has three
pages for it: SMB server, FTP server and SFTP server. Each works the same
way. Choose the folder or drive, the port, a user name and a password of at
least 8 characters, tick Turn on, and press OK. Tick Read only if nothing
should be changed or deleted through it. Your Google Drive letter can be
shared like any other drive. A server keeps running while Explorer Native is
running, in the tray too, and starts again with it.

SMB is the way Windows and Samba share files, so the other device opens it as
a network drive. It uses port 4450 unless you change it, not the usual 445,
because Windows keeps 445 for its own file sharing. On Windows 11 (24H2 or
later): net use Z: \\this-pc\files /user:name * /tcpport:4450. On a Mac, in
Finder choose Go, Connect to Server, and enter smb://this-pc:4450/files. On
Linux, iPhone and Android, type the port into the file program's port box.
Older versions of Windows cannot connect to SMB on another port; use FTP or
SFTP from those.

FTP works with nearly everything. Programs that ask for it get an encrypted
connection (explicit FTP over TLS). Plain FTP sends the password unprotected,
so keep it to your own network or Tailscale. Only passive connections are
taken, which is what current programs use.

SFTP is the file transfer that comes with SSH, encrypted from the first
byte: sftp -P 2222 name@this-pc. Only files are offered. Nobody gets a
command line on this PC.

Over Tailscale there is nothing to set up. While a server is on, your other
devices on your Tailscale network reach it at this PC's Tailscale name and
the server's port; the page's status line says the name. Tailscale carries
the connection, so Windows Firewall is not involved, and Tailscale opens
nothing to the internet. FTP's file transfers use the eight ports after the
FTP port (2122 to 2129 with the usual 2121), and those are carried too.

For devices that are not on your Tailscale network: each server listens on
every network this PC joins, over IPv4 and IPv6, and Windows Firewall blocks
them until it is told to let them in. Each page has a button, Allow through
Windows Firewall, that does it once and asks for an administrator. It covers
every network this PC joins, public ones too, and the internet itself if
this PC has an internet address of its own, so the password is what
protects your files there. Once it is allowed the button says Remove from
Windows Firewall and takes it away again. Nothing here opens your router.

How the servers protect your files: nobody gets in without the user name
and password, and ten wrong passwords from one address lock that address
out for five minutes. Nothing outside the shared folder can be reached, and
a link inside it that leads somewhere else on the PC is not followed. SFTP
is encrypted throughout. FTP is encrypted for programs that ask, and the FTP
page has a box, Encrypted connections only, that refuses the rest. SMB signs
in without sending the password itself, but the files travel unencrypted, so
outside your own network use it over Tailscale, which encrypts everything.

The same can be done from a command line: ExplorerNative --do smb-server-on,
smb-server-off and smb-server-status, and the same for ftp and sftp. Every
setting has a command too: ExplorerNative --do settings lists them, and
ExplorerNative --do setting smb-server-folder D:\Music changes one.
Passwords and keys are never shown, only whether they are set.

The SMB server uses SMBLibrary by Tal Aloni, under the GNU Lesser General
Public License. ExplorerNative --licence writes out its licence with the
others.


Updates

Help menu (Alt, then H), Check for updates. It asks GitHub whether a newer
version is out, tells you what changed, and on Yes downloads it, installs it
over the running copy and restarts Explorer Native on the new version. Nothing
checks automatically.


Reporting a bug

Run ExplorerNative --do bug-report from a command prompt. It writes one text
file on your desktop with the version, your Windows build, your settings and
the last few hundred lines of the logs. Passwords, keys, sign-ins and email
addresses are taken out, and passwords in the settings only say whether they
are set. Read it before you send it. It works even when Explorer Native is
stuck or has closed. To write it somewhere else, give a path:
ExplorerNative --do bug-report D:\report.txt


Building

Needs the .NET 10 SDK.

dotnet publish ExplorerNative.csproj -c Release -r win-x64 -o out\x64
dotnet publish ExplorerNative.csproj -c Release -r win-arm64 -o out\arm64

Tests: dotnet run --project tests\SelfTest.csproj



Releasing

Add the version and its changes to CHANGES.txt (the version on its own line,
then one change per line, plain text). Set the version in ExplorerNative.csproj,
then push a tag of the same number:

git tag v1.0.1
git push origin v1.0.1

GitHub Actions builds both architectures and runs the tests at the same time.
The release is published, and Check for updates finds it, only when the tests
pass. Every push to main and every pull request is built and tested too.
