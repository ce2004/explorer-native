# Internet streams

Part of the Explorer Native notes for Claude; CLAUDE.md says when to read this file.

## Internet streams (5.4.0)

Conner: the playlist files are pointers; what matters is the stream inside them.
- Enter on .pls/.m3u/.m3u8/.asx/.xspf (`StreamPlaylists.IsPlaylistName`, by name) goes to the player. `AudioPlayer.StartDirectCore` makes a `RadioStream.FromPlaylistFile`; a playlist with no web addresses (`NoStreamsInPlaylist`) is handed to its own app through `Failed`, as before.
- `RadioStream` (src\RadioStream.cs) is an endless IStream (`FakeLength` 1TB; reads far past the live edge answer 0 at once) filled by one synchronous network thread. Shoutcast answers "ICY 200 OK", which HttpClient refuses, so stations go over our own socket (HTTP/1.0, redirects, chunked); HLS goes through HttpClient. Playlists inside playlists are followed (depth 5), by content type, by content, or by name.
- ICY titles are stripped by `IcyStripper` and become `Song`; the player's display name is "song on station", the station being the playlist's title first (SomaFM's icy-name is a whole sentence), then icy-name, then the file name, and `audio.stream.song` (status bar) is raised. A stream that ends raises `audio.stream.stopped` with `Problem`, not `Finished`.
- HLS: `ChooseVariant` takes the best audio-only variant, else the default audio rendition, else the smallest variant (its sound is demuxed). Live starts three segments from the end. TS is demuxed by `TsAudio` (AAC ADTS or MP3 out), packed audio has its ID3 skipped, AES-128 is decrypted (IV or sequence), SAMPLE-AES refused, fMP4 (`#EXT-X-MAP`) passed through as Mp4.
- At most `MaxAhead` (4MB) unread: a paused stream stalls its socket instead of growing. A drop reconnects (backoff to 5s); `GiveUpAfter` (30s) with no sound ends it. 404/403/401/410 and wrong formats (`WrongFormat`, then handed to its own app) are final.
- `TrackDecoder.Open` sends a RadioStream to `LiveDecoder.Open`: OggDecoder for Ogg, else Media Foundation, wrapped so Duration is 0 and SeekTo refuses; the decoder owns the stream. `WasapiPlayback._live` keeps the deep ring and a 45s open. `AudioPlayer.IsLive`: skips say it can't be skipped, length questions say it's live, resume never rewinds, resume after an end reconnects. Next/Previous track step through station files (`AudioFilesShown(folder, stations)`).
- Short reads (`Read` answers what has come after 150ms): Media Foundation's MP3 reader takes one as the end once playing (181.fm stopped after 5s), its AAC reader needs them (a 64kbps AAC station was silent 20s). So short reads while `Opening`, and always for anything but MP3.
- `General`: pinned servers of big networks go stale (WBAP 403 on 14223.live.streamtheworld.com, KRLD no answer on prod-3-88-28-70.amperwave.net), so they are swapped for the network's redirect address before connecting. Conner's test stations are in G:	extadio stations.
- Tests: `EXPLORERNATIVE_SELFTEST_ONLY=radio` (offline: a pretend ICY station and an encrypted TS HLS server on 127.0.0.1, MP3 from Windows' encoder), `radioprobe` (real stations; `EXPLORERNATIVE_RADIOPROBE_URLS` to choose).
