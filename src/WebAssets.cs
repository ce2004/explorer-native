using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace ExplorerNative
{
    /// <summary>
    /// The Explorer Connect web app: plain HTML, CSS and JavaScript embedded in the
    /// executable under web/, served by ConnectServer at "/", "/app/..." and
    /// "/sw.js". The files are the app's shell only and hold no data, so they are
    /// served without the pairing code; everything they show comes from /api/,
    /// which still needs it.
    /// </summary>
    internal static class WebAssets
    {
        private static readonly Dictionary<string, Asset> Cache = new(StringComparer.Ordinal);

        /// <summary>The longest name of a file in web\; anything longer is not one of them.</summary>
        internal const int MaxName = 64;

        /// <summary>How many names are remembered, for tests: never more than the files that exist.</summary>
        internal static int Cached { get { lock (Cache) return Cache.Count; } }

        /// <summary>
        /// One file of the shell as it is served: its bytes, its type, a tag that changes when the bytes do, and,
        /// for text, the same bytes compressed once (brotli and gzip) so no request pays for compressing them.
        /// index.html names app.js and app.css with their tags (<c>?v=</c>), so those two can be cached for good
        /// by the phone and a new build is still picked up: a new build is a new index.html naming new URLs.
        /// </summary>
        internal sealed record Asset(byte[] Bytes, string Type, string Tag, byte[]? Brotli, byte[]? Gzip);

        /// <summary>The embedded file for a request path, or false when the path is not part of the web app.</summary>
        public static bool TryGet(string path, out byte[] bytes, out string contentType)
        {
            bytes = Array.Empty<byte>();
            contentType = "";
            if (!TryGetAsset(path, out var asset)) return false;
            bytes = asset.Bytes;
            contentType = asset.Type;
            return true;
        }

        /// <summary>The embedded file for a request path with its tag and compressed forms; false when it is not ours.</summary>
        internal static bool TryGetAsset(string path, out Asset asset)
        {
            asset = null!;
            string? name = path switch
            {
                "/" or "/index.html" => "index.html",
                "/sw.js" => "sw.js",
                "/manifest.webmanifest" => "manifest.webmanifest",
                "/apple-touch-icon.png" => "apple-touch-icon.png",
                "/favicon.ico" => "icon-192.png",
                _ when path.StartsWith("/app/", StringComparison.Ordinal) && !path.Contains("..") => path[5..],
                _ => null,
            };
            // This runs before the pairing code is asked for, so anybody can ask for any name: only a name that
            // could be one of ours is looked up, and only what was found is remembered. Remembering every miss
            // was a cache that grew by one entry per made-up name for as long as the application ran.
            if (name == null || name.Length == 0 || name.Length > MaxName) return false;

            Asset? found;
            lock (Cache) found = Find(name);
            if (found == null) return false;
            asset = found;
            return true;
        }

        /// <summary>The tag of one of the shell's files, as index.html names it; empty when there is no such file.</summary>
        internal static string TagOf(string name)
        {
            lock (Cache) return Find(name)?.Tag ?? "";
        }

        /// <summary>Under the lock: a name looked up once, and remembered only when it is one of ours.</summary>
        private static Asset? Find(string name)
        {
            if (!Cache.TryGetValue(name, out Asset? found))
            {
                found = Load(name);
                if (found != null) Cache[name] = found;
            }
            return found;
        }

        private static Asset? Load(string name)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("web/" + name);
            if (stream == null) return null;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            var bytes = copy.ToArray();

            // The page names its script and style sheet by their tags, so the phone can keep them for good.
            if (name == "index.html")
            {
                var html = Encoding.UTF8.GetString(bytes);
                foreach (var part in new[] { "app.js", "app.css" })
                {
                    var tag = Find(part)?.Tag;
                    if (tag != null) html = html.Replace($"\"/app/{part}\"", $"\"/app/{part}?v={tag}\"", StringComparison.Ordinal);
                }
                bytes = Encoding.UTF8.GetBytes(html);
            }

            var type = TypeOf(name);
            var hash = Convert.ToHexString(SHA256.HashData(bytes), 0, 8).ToLowerInvariant();
            bool text = type.StartsWith("text/", StringComparison.Ordinal) || type.Contains("json", StringComparison.Ordinal) ||
                        type.Contains("svg", StringComparison.Ordinal);
            return new Asset(bytes, type, hash, text ? Brotli(bytes, 9) : null, text ? Gzip(bytes, CompressionLevel.Optimal) : null);
        }

        /// <summary>Brotli at <paramref name="quality"/> (0 to 11), with a 4 MB window.</summary>
        internal static byte[] Brotli(byte[] data, int quality)
        {
            var into = new byte[BrotliEncoder.GetMaxCompressedLength(data.Length)];
            return BrotliEncoder.TryCompress(data, into, out int written, quality, 22) ? into.AsSpan(0, written).ToArray() : data;
        }

        internal static byte[] Gzip(byte[] data, CompressionLevel level)
        {
            using var into = new MemoryStream(data.Length / 3 + 64);
            using (var gzip = new GZipStream(into, level, leaveOpen: true)) gzip.Write(data);
            return into.ToArray();
        }

        /// <summary>
        /// Which coding to answer with for an Accept-Encoding header: "br", "gzip", or "" for none. A coding with
        /// q=0 is refused, and "*" is not taken as a yes, so a client that did not name one gets plain bytes.
        /// </summary>
        internal static string PickEncoding(string? accept)
        {
            if (string.IsNullOrEmpty(accept)) return "";
            bool br = false, gzip = false;
            foreach (var part in accept.Split(','))
            {
                var bits = part.Split(';');
                var coding = bits[0].Trim();
                bool refused = false;
                for (int i = 1; i < bits.Length; i++)
                {
                    var p = bits[i].Trim();
                    if (p.StartsWith("q=", StringComparison.OrdinalIgnoreCase) &&
                        double.TryParse(p[2..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var q) &&
                        q <= 0) refused = true;
                }
                if (refused) continue;
                if (coding.Equals("br", StringComparison.OrdinalIgnoreCase)) br = true;
                else if (coding.Equals("gzip", StringComparison.OrdinalIgnoreCase)) gzip = true;
            }
            return br ? "br" : gzip ? "gzip" : "";
        }

        public static string TypeOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".webmanifest" => "application/manifest+json",
            ".png" => "image/png",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream",
        };
    }
}
