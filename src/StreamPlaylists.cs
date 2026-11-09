using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ExplorerNative
{
    /// <summary>
    /// Playlist files that point at internet streams (5.4.0): .pls, .m3u, .m3u8,
    /// .asx and .xspf. Enter on one plays the stream inside it (RadioStream);
    /// one with no web addresses in it opens in its own app, as it always did.
    /// </summary>
    internal static class StreamPlaylists
    {
        public static readonly string[] Extensions = { ".pls", ".m3u", ".m3u8", ".asx", ".xspf" };

        /// <summary>By name only, no reads.</summary>
        public static bool IsPlaylistName(string? path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string extension;
            try { extension = Path.GetExtension(path); } catch { return false; }
            return Extensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase));
        }

        internal sealed record Entry(string Url, string? Title);

        /// <summary>HLS: a playlist of segments or of variants, not a list of stations.</summary>
        public static bool IsHls(string text) =>
            text.Contains("#EXT-X-TARGETDURATION", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("#EXT-X-MEDIA-SEQUENCE", StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether the start of a download is a playlist rather than sound.</summary>
        public static bool LooksLikePlaylist(ReadOnlySpan<byte> head)
        {
            var text = Encoding.UTF8.GetString(head).TrimStart('﻿', ' ', '\t', '\r', '\n');
            return text.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("[playlist]", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("<asx", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A content type that means a playlist.</summary>
        public static bool IsPlaylistType(string? contentType)
        {
            if (string.IsNullOrEmpty(contentType)) return false;
            var t = contentType.ToLowerInvariant();
            return t.Contains("mpegurl") || t.Contains("scpls") || t.Contains("x-ms-asf") ||
                   t.Contains("xspf") || t.Contains("x-ms-wax") || t.Contains("x-ms-asx");
        }

        /// <summary>
        /// The entries of a playlist, in order, with relative ones made absolute
        /// against <paramref name="baseUri"/> when there is one.
        /// </summary>
        public static List<Entry> Parse(string text, Uri? baseUri)
        {
            text = text.TrimStart('﻿');
            var trimmed = text.TrimStart();
            List<Entry> found;
            if (trimmed.StartsWith("[playlist]", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(text, @"^\s*File\d+\s*=", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                found = ParsePls(text);
            else if (trimmed.StartsWith("<", StringComparison.Ordinal))
                found = ParseXml(text);
            else
                found = ParseM3u(text);

            var result = new List<Entry>();
            foreach (var e in found)
            {
                var url = e.Url.Trim();
                if (url.Length == 0) continue;
                if (baseUri != null && !url.Contains("://", StringComparison.Ordinal) &&
                    Uri.TryCreate(baseUri, url, out var absolute))
                    url = absolute.ToString();
                result.Add(e with { Url = url });
            }
            return result;
        }

        /// <summary>Only the entries that are on the web.</summary>
        public static List<Entry> WebEntries(IEnumerable<Entry> entries) =>
            entries.Where(e => IsWebAddress(e.Url)).ToList();

        public static bool IsWebAddress(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var u) &&
            (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps || u.Scheme == "icy");

        private static List<Entry> ParsePls(string text)
        {
            var files = new SortedDictionary<int, string>();
            var titles = new Dictionary<int, string>();
            foreach (var raw in Lines(text))
            {
                var m = Regex.Match(raw, @"^\s*(File|Title)(\d+)\s*=\s*(.*)$", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                int n = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (m.Groups[1].Value.Equals("File", StringComparison.OrdinalIgnoreCase)) files[n] = m.Groups[3].Value.Trim();
                else titles[n] = m.Groups[3].Value.Trim();
            }
            return files.Select(f => new Entry(f.Value, titles.TryGetValue(f.Key, out var t) && t.Length > 0 ? t : null)).ToList();
        }

        private static List<Entry> ParseM3u(string text)
        {
            var list = new List<Entry>();
            string? title = null;
            foreach (var raw in Lines(text))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
                {
                    int comma = line.IndexOf(',');
                    title = comma >= 0 && comma + 1 < line.Length ? line[(comma + 1)..].Trim() : null;
                    if (title?.Length == 0) title = null;
                    continue;
                }
                if (line.StartsWith('#')) continue;
                list.Add(new Entry(line, title));
                title = null;
            }
            return list;
        }

        /// <summary>ASX (ref href) and XSPF (location), read loosely: these are rarely well formed.</summary>
        private static List<Entry> ParseXml(string text)
        {
            var list = new List<Entry>();
            foreach (Match m in Regex.Matches(text, @"<ref\s+href\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase))
                list.Add(new Entry(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value), null));
            foreach (Match m in Regex.Matches(text, @"<location>\s*([^<]+?)\s*</location>", RegexOptions.IgnoreCase))
                list.Add(new Entry(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value), null));
            if (list.Count > 0)
            {
                var title = Regex.Match(text, @"<title>\s*([^<]+?)\s*</title>", RegexOptions.IgnoreCase);
                if (title.Success) list[0] = list[0] with { Title = System.Net.WebUtility.HtmlDecode(title.Groups[1].Value) };
            }
            return list;
        }

        private static IEnumerable<string> Lines(string text) =>
            text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        /// <summary>
        /// Reads a playlist file off disk, at most a megabyte, as UTF-8 (or the
        /// ANSI code page when it is not valid UTF-8, as old .m3u files are).
        /// </summary>
        public static string ReadFile(string path)
        {
            using var file = new FileStream(NameRules.ExactPath(path), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[(int)Math.Min(file.Length, 1024 * 1024)];
            int got = 0;
            while (got < bytes.Length)
            {
                int n = file.Read(bytes, got, bytes.Length - got);
                if (n <= 0) break;
                got += n;
            }
            return Decode(bytes.AsSpan(0, got));
        }

        public static string Decode(ReadOnlySpan<byte> bytes)
        {
            // Notepad's "Unicode" and some Windows tools save UTF-16 with a mark in front.
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes[2..]);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes[2..]);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) bytes = bytes[3..];
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
        }
    }
}
