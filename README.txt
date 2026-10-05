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

It needs the .NET 8 Desktop Runtime (or newer). If that is missing, Windows
says so on first launch and offers the download. Get the one that matches your
computer, x64 or Arm64, from https://dotnet.microsoft.com/download/dotnet/8.0


Google Drive

No Google credentials ship with the app. In Google Cloud console, create an
OAuth client of type Desktop app. Then in Explorer Native open Preferences
(Ctrl+P), Google Drive, and paste its client ID and client secret into the two
fields. When you press OK they are checked with Google; if Google accepts
them they are saved encrypted and the browser opens for you to sign in.

Everything Explorer Native saves in AppData (settings, the client ID and
secret, the sign-in, logs) is encrypted for your Windows account.


Updates

Help menu (Alt, then H), Check for updates. It asks GitHub whether a newer
version is out, tells you what changed, and on Yes downloads it, installs it
over the running copy and restarts Explorer Native on the new version. Nothing
checks automatically.


Building

Needs the .NET 8 SDK.

dotnet publish ExplorerNative.csproj -c Release -r win-x64 -o out\x64
dotnet publish ExplorerNative.csproj -c Release -r win-arm64 -o out\arm64

Tests: dotnet run --project tests\SelfTest.csproj



Releasing

Set the version in ExplorerNative.csproj, then push a tag of the same number:

git tag v1.0.1
git push origin v1.0.1

GitHub Actions builds both architectures and publishes the release that Check
for updates finds.
