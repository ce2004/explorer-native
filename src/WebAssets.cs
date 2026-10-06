using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

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
        private static readonly Dictionary<string, (byte[] Bytes, string Type)?> Cache = new(StringComparer.Ordinal);

        /// <summary>The longest name of a file in web\; anything longer is not one of them.</summary>
        internal const int MaxName = 64;

        /// <summary>How many names are remembered, for tests: never more than the files that exist.</summary>
        internal static int Cached { get { lock (Cache) return Cache.Count; } }

        /// <summary>The embedded file for a request path, or false when the path is not part of the web app.</summary>
        public static bool TryGet(string path, out byte[] bytes, out string contentType)
        {
            bytes = Array.Empty<byte>();
            contentType = "";
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

            (byte[] Bytes, string Type)? found;
            lock (Cache)
            {
                if (!Cache.TryGetValue(name, out found))
                {
                    found = Load(name);
                    if (found != null) Cache[name] = found;
                }
            }
            if (found == null) return false;
            bytes = found.Value.Bytes;
            contentType = found.Value.Type;
            return true;
        }

        private static (byte[], string)? Load(string name)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("web/" + name);
            if (stream == null) return null;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return (copy.ToArray(), TypeOf(name));
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
