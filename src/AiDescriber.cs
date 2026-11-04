using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>The companies the picture describer can talk to. New ones go at the end.</summary>
    public enum AiProvider { Claude, Gemini, OpenAI, OpenRouter }

    /// <summary>A failure said as a sentence. KeyRefused means the key itself was turned down.</summary>
    public sealed class AiException : Exception
    {
        public bool KeyRefused { get; }
        public bool OutOfCredit { get; }

        public AiException(string message, bool keyRefused = false, bool outOfCredit = false) : base(message)
        {
            KeyRefused = keyRefused;
            OutOfCredit = outOfCredit;
        }
    }

    /// <summary>A model a key can use. Name is what the list shows.</summary>
    public sealed record AiModel(string Id, string Name);

    /// <summary>One message in a conversation: the person's or the AI's.</summary>
    public sealed record AiTurn(bool FromPerson, string Text);

    /// <summary>The picture as it is sent: at most 1568 pixels on its long side.</summary>
    public sealed record AiPicture(byte[] Bytes, string MediaType, int Width, int Height);

    /// <summary>
    /// An answer, with what it used. Cost is the provider's own figure when it
    /// gives one (OpenRouter does), otherwise null and worked out from prices.
    /// </summary>
    public sealed record AiReply(string Text, long InputTokens, long OutputTokens,
        long CacheWriteTokens, long CacheReadTokens, decimal? Cost, bool CutShort);

    /// <summary>Dollars per token.</summary>
    public sealed record AiPrice(decimal Input, decimal Output, decimal? CacheRead, decimal? CacheWrite);

    /// <summary>
    /// The picture describer's talk with Claude, Gemini, OpenAI and OpenRouter:
    /// key checks, model lists, the conversation itself, prices, and getting a
    /// picture into a shape every one of them takes. No UI here, so the suite
    /// covers it with a stand-in for the network (<see cref="Http"/>).
    /// </summary>
    internal static class AiDescriber
    {
        /// <summary>Every request goes through this. The suite swaps it for a fake.</summary>
        internal static HttpClient Http = new(GoogleNet.Handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

        /// <summary>How long one answer may take before it is given up on.</summary>
        internal static TimeSpan AnswerTimeout = TimeSpan.FromSeconds(120);

        /// <summary>How long a key check or a model list may take.</summary>
        internal static TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);

        /// <summary>Waits between tries when a provider is busy. The suite makes these short.</summary>
        internal static TimeSpan[] RetryWaits = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5) };

        /// <summary>The longest side a picture is sent at. Anthropic's own advice; the others take it too.</summary>
        internal const int LongestSide = 1568;

        /// <summary>And at most this many pixels in all (Anthropic: about 1.15 megapixels).</summary>
        internal const int MostPixels = 1_150_000;

        /// <summary>A file bigger than this is not read at all.</summary>
        internal const long LargestFile = 60L * 1024 * 1024;

        /// <summary>A picture sent bigger than this is sent as JPEG instead (every provider takes 3.75 MB).</summary>
        internal const int LargestSent = 3_500_000;

        /// <summary>A PNG bigger than this is a photo saved as PNG, and goes as JPEG: smaller and quicker, and photos lose nothing.</summary>
        internal const int LargestPng = 1_500_000;

        public static readonly AiProvider[] All = { AiProvider.Claude, AiProvider.Gemini, AiProvider.OpenAI, AiProvider.OpenRouter };

        // ---------- names ----------

        /// <summary>"Claude by Anthropic", as the buttons and lists say it.</summary>
        public static string Name(AiProvider provider) => provider switch
        {
            AiProvider.Claude => "Claude by Anthropic",
            AiProvider.Gemini => "Google Gemini",
            AiProvider.OpenAI => "OpenAI",
            AiProvider.OpenRouter => "OpenRouter",
            _ => provider.ToString(),
        };

        /// <summary>The company, for sentences about keys and accounts.</summary>
        public static string Company(AiProvider provider) => provider switch
        {
            AiProvider.Claude => "Anthropic",
            AiProvider.Gemini => "Google",
            AiProvider.OpenAI => "OpenAI",
            AiProvider.OpenRouter => "OpenRouter",
            _ => provider.ToString(),
        };

        /// <summary>
        /// Who you are talking to: "Claude", "Gemini", "GPT". Through OpenRouter
        /// it is whoever made the chosen model, so "Talk to Claude" there too.
        /// </summary>
        public static string Speaker(AiProvider provider, string model)
        {
            switch (provider)
            {
                case AiProvider.Claude: return "Claude";
                case AiProvider.Gemini: return model.Contains("gemma", StringComparison.OrdinalIgnoreCase) ? "Gemma" : "Gemini";
                case AiProvider.OpenAI: return "GPT";
            }
            var id = (model ?? "").ToLowerInvariant();
            if (id.Contains("claude")) return "Claude";
            if (id.Contains("gemini")) return "Gemini";
            if (id.Contains("gemma")) return "Gemma";
            if (id.Contains("gpt") || id.StartsWith("openai/", StringComparison.Ordinal)) return "GPT";
            if (id.Contains("grok")) return "Grok";
            if (id.Contains("llama")) return "Llama";
            if (id.Contains("mistral") || id.Contains("pixtral")) return "Mistral";
            if (id.Contains("qwen")) return "Qwen";
            int slash = id.IndexOf('/');
            var rest = slash >= 0 ? id[(slash + 1)..] : id;
            var word = new string(rest.TakeWhile(char.IsLetter).ToArray());
            return word.Length == 0 ? "the AI" : char.ToUpperInvariant(word[0]) + word[1..];
        }

        /// <summary>Where a person gets a key.</summary>
        public static string KeyPage(AiProvider provider) => provider switch
        {
            AiProvider.Claude => "https://console.anthropic.com/settings/keys",
            AiProvider.Gemini => "https://aistudio.google.com/app/apikey",
            AiProvider.OpenAI => "https://platform.openai.com/api-keys",
            AiProvider.OpenRouter => "https://openrouter.ai/settings/keys",
            _ => "",
        };

        /// <summary>One sentence on what it is and what it costs, for the wizard.</summary>
        public static string Blurb(AiProvider provider) => provider switch
        {
            AiProvider.Claude =>
                "Claude, made by Anthropic. Very good at describing pictures and reading text in them. " +
                "You pay Anthropic for what you use, usually about a cent a picture.",
            AiProvider.Gemini =>
                "Gemini, made by Google. Fast, and the Flash models cost a fraction of a cent a picture. " +
                "Google has a free tier with daily limits.",
            AiProvider.OpenAI =>
                "GPT, made by OpenAI, the company behind ChatGPT. You pay OpenAI for what you use, " +
                "from a fraction of a cent to a few cents a picture depending on the model.",
            AiProvider.OpenRouter =>
                "OpenRouter: one key and one account for hundreds of models from many companies, " +
                "including Claude, Gemini and GPT. You buy credit with OpenRouter and choose any model.",
            _ => "",
        };

        /// <summary>How to get a key, step by step, for the wizard.</summary>
        public static string KeySteps(AiProvider provider) => provider switch
        {
            AiProvider.Claude =>
                "Sign in at console.anthropic.com, add some credit under Billing, then go to API keys and " +
                "choose Create key. Copy the key, which starts with sk-ant, and paste it below.",
            AiProvider.Gemini =>
                "Sign in at aistudio.google.com with your Google account, choose Get API key, then Create API key. " +
                "Copy the key, which starts with AIza, and paste it below.",
            AiProvider.OpenAI =>
                "Sign in at platform.openai.com, add some credit under Billing, then go to API keys and " +
                "choose Create new secret key. Copy the key, which starts with sk-, and paste it below.",
            AiProvider.OpenRouter =>
                "Sign in at openrouter.ai, add some credit, then go to Keys and choose Create key. " +
                "Copy the key, which starts with sk-or, and paste it below.",
            _ => "",
        };

        /// <summary>
        /// A key pasted into the wrong provider's box, said before anything is
        /// sent: an Anthropic key given to OpenAI would only come back as "not
        /// accepted", which sounds like a typing mistake. Null when it could be right.
        /// </summary>
        public static string? WrongKind(AiProvider provider, string key)
        {
            key = (key ?? "").Trim();
            if (key.Length == 0) return "Paste your API key first.";
            if (key.Any(char.IsWhiteSpace)) return "That key has spaces in it. Copy it again, on its own.";
            AiProvider? looks =
                key.StartsWith("sk-ant-", StringComparison.Ordinal) ? AiProvider.Claude
                : key.StartsWith("sk-or-", StringComparison.Ordinal) ? AiProvider.OpenRouter
                : key.StartsWith("AIza", StringComparison.Ordinal) ? AiProvider.Gemini
                : null;
            if (looks is { } other && other != provider)
                return $"That looks like a key for {Name(other)}, not {Name(provider)}.";
            if (provider == AiProvider.OpenAI && !key.StartsWith("sk-", StringComparison.Ordinal))
                return "OpenAI keys start with sk-. That doesn't look like one.";
            return null;
        }

        // ---------- pictures ----------

        private static readonly HashSet<string> PictureExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".dib", ".tif", ".tiff",
            ".webp", ".heic", ".heif", ".avif", ".ico", ".jxr", ".wdp",
        };

        /// <summary>A picture the describer takes, by name only.</summary>
        public static bool IsPicture(string? path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try { return PictureExtensions.Contains(Path.GetExtension(path)); }
            catch { return false; }
        }

        /// <summary>
        /// The picture ready to send: turned the right way up, at most
        /// <see cref="LongestSide"/> on its long side, transparent parts on white,
        /// PNG for things like screenshots (text stays sharp) and JPEG otherwise.
        /// Formats GDI+ cannot read (HEIC, AVIF, WebP) come through Windows'
        /// own thumbnail maker when <paramref name="localPath"/> is a file on
        /// this PC, which reads whatever Windows can show. Throws a sentence.
        /// </summary>
        public static AiPicture PreparePicture(byte[] bytes, string? localPath)
        {
            if (bytes == null || bytes.Length == 0) throw new AiException("The picture is empty.");

            Bitmap? drawn = null;
            bool sharp = LooksLike(bytes, "png") || LooksLike(bytes, "gif") || LooksLike(bytes, "bmp");
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                using var image = Image.FromStream(stream, useEmbeddedColorManagement: true, validateImageData: true);
                drawn = Fit(image, Orientation(image));
            }
            catch (Exception) when (drawn == null) { }

            if (drawn == null && !string.IsNullOrEmpty(localPath))
            {
                using var thumb = ShellPicture(localPath, LongestSide);
                if (thumb != null) drawn = Fit(thumb, RotateFlipType.RotateNoneFlipNone);
            }

            if (drawn == null)
            {
                // Sent as it is, when it is a kind every provider takes and small enough.
                string? kind = LooksLike(bytes, "jpeg") ? "image/jpeg" : LooksLike(bytes, "png") ? "image/png"
                    : LooksLike(bytes, "gif") ? "image/gif" : LooksLike(bytes, "webp") ? "image/webp" : null;
                if (kind != null && bytes.Length <= LargestSent) return new AiPicture(bytes, kind, 0, 0);
                throw new AiException("Explorer Native can't open this picture. It may be damaged, or a kind of picture Windows can't show.");
            }

            using (drawn)
            {
                if (sharp)
                {
                    var png = Encode(drawn, ImageFormat.Png, null);
                    if (png.Length <= LargestPng) return new AiPicture(png, "image/png", drawn.Width, drawn.Height);
                }
                foreach (long quality in new long[] { 88, 75, 60 })
                {
                    var jpeg = Encode(drawn, ImageFormat.Jpeg, quality);
                    if (jpeg.Length <= LargestSent) return new AiPicture(jpeg, "image/jpeg", drawn.Width, drawn.Height);
                }
                throw new AiException("The picture is too big to send, even made smaller.");
            }
        }

        private static bool LooksLike(byte[] b, string kind) => kind switch
        {
            "jpeg" => b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF,
            "png" => b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47,
            "gif" => b.Length > 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F',
            "bmp" => b.Length > 2 && b[0] == 'B' && b[1] == 'M',
            "webp" => b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' &&
                      b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P',
            _ => false,
        };

        /// <summary>The turn an EXIF orientation tag asks for, so a phone photo is not sent on its side.</summary>
        private static RotateFlipType Orientation(Image image)
        {
            try
            {
                if (!image.PropertyIdList.Contains(0x0112)) return RotateFlipType.RotateNoneFlipNone;
                var value = image.GetPropertyItem(0x0112)?.Value;
                int o = value is { Length: > 0 } ? value[0] : 1;
                return o switch
                {
                    2 => RotateFlipType.RotateNoneFlipX,
                    3 => RotateFlipType.Rotate180FlipNone,
                    4 => RotateFlipType.Rotate180FlipX,
                    5 => RotateFlipType.Rotate90FlipX,
                    6 => RotateFlipType.Rotate90FlipNone,
                    7 => RotateFlipType.Rotate270FlipX,
                    8 => RotateFlipType.Rotate270FlipNone,
                    _ => RotateFlipType.RotateNoneFlipNone,
                };
            }
            catch { return RotateFlipType.RotateNoneFlipNone; }
        }

        /// <summary>The size a picture is sent at: never bigger than the limits, and tiny ones doubled up to be seen.</summary>
        internal static Size SentSize(int width, int height)
        {
            if (width <= 0 || height <= 0) return new Size(1, 1);
            double scale = Math.Min(1.0, Math.Min((double)LongestSide / Math.Max(width, height),
                Math.Sqrt((double)MostPixels / ((double)width * height))));
            if (Math.Max(width, height) < 128) scale = 256.0 / Math.Max(width, height);
            return new Size(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
        }

        private static Bitmap Fit(Image image, RotateFlipType turn)
        {
            bool sideways = turn is RotateFlipType.Rotate90FlipNone or RotateFlipType.Rotate90FlipX or
                RotateFlipType.Rotate270FlipNone or RotateFlipType.Rotate270FlipX;
            int w = sideways ? image.Height : image.Width, h = sideways ? image.Width : image.Height;
            var size = SentSize(w, h);

            Image source = image;
            Bitmap? turned = null;
            if (turn != RotateFlipType.RotateNoneFlipNone)
            {
                turned = new Bitmap(image);
                turned.RotateFlip(turn);
                source = turned;
            }
            try
            {
                var result = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
                using var g = Graphics.FromImage(result);
                g.Clear(Color.White);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                using var attributes = new ImageAttributes();
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(source, new Rectangle(0, 0, size.Width, size.Height), 0, 0, source.Width, source.Height,
                    GraphicsUnit.Pixel, attributes);
                return result;
            }
            finally { turned?.Dispose(); }
        }

        private static byte[] Encode(Bitmap bitmap, ImageFormat format, long? quality)
        {
            using var output = new MemoryStream();
            if (quality is { } q)
            {
                var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using var parameters = new EncoderParameters(1);
                parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, q);
                bitmap.Save(output, codec, parameters);
            }
            else bitmap.Save(output, format);
            return output.ToArray();
        }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr bitmap);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize { public int Width; public int Height; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);

        /// <summary>
        /// Windows' own rendering of a picture file, at most <paramref name="size"/>
        /// on a side, or null. Thumbnails only (never the file type's icon). Its
        /// own STA thread, given 20 seconds.
        /// </summary>
        internal static Bitmap? ShellPicture(string path, int size)
        {
            Bitmap? result = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var iid = typeof(IShellItemImageFactory).GUID;
                    if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0 || factory == null) return;
                    try
                    {
                        const int BiggerSizeOk = 0x1, ThumbnailOnly = 0x8;
                        if (factory.GetImage(new NativeSize { Width = size, Height = size }, BiggerSizeOk | ThumbnailOnly, out var hbitmap) != 0 ||
                            hbitmap == IntPtr.Zero) return;
                        try { result = Image.FromHbitmap(hbitmap); }
                        finally { DeleteObject(hbitmap); }
                    }
                    finally { Marshal.ReleaseComObject(factory); }
                }
                catch { result = null; }
            }) { IsBackground = true, Name = "Picture thumbnail" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return thread.Join(TimeSpan.FromSeconds(20)) ? result : null;
        }

        // ---------- what the AI is told ----------

        /// <summary>
        /// The standing instructions: describe for someone who cannot see it,
        /// plain text for a screen reader, and stay on this picture, turning
        /// anything else down kindly rather than flatly.
        /// </summary>
        public static string Instructions(string fileName) =>
            "You are the picture describer inside Explorer Native, a file manager made for people who are blind " +
            "and use screen readers. The person you are talking to can't see the picture in this conversation, " +
            "so you are their eyes.\n\n" +
            "Describe what is really there: whether it is a photo, screenshot, drawing, document or something else; " +
            "the setting; people, with their appearance, clothing, expressions and what they are doing; animals and objects; " +
            "colours, light and layout; the mood; and every bit of readable text, quoted exactly. Don't say who a person is " +
            "from their face, but you may say who they are when text or the file name says so. If something is too small, " +
            "blurry or cut off to be sure of, say so rather than guessing.\n\n" +
            "Your replies are read aloud by a screen reader, so write in plain sentences and paragraphs. Never use Markdown: " +
            "no asterisks, pound signs, bullet symbols, numbered lists, tables or headings. Spell out symbols when they matter. " +
            "Answer in the language the person writes in.\n\n" +
            "Stay on the subject of this picture. Every question about it is welcome: details, reading or translating its text, " +
            "what something in it is, advice that depends on it (whether colours go together, what a sign or error message " +
            "means, what a plant or a dish might be), and follow-ups on what you said before. If the person asks for something " +
            "that has nothing to do with the picture, such as an essay, trivia, code, or chat about another subject, " +
            "kindly say in a sentence or two that you're only here to help with this picture, and offer something about it " +
            "you could help with instead. Don't lecture, and don't refuse questions that really are about the picture.\n\n" +
            "The picture's file is called \"" + (fileName ?? "") + "\".";

        /// <summary>What is asked first, without the person typing it.</summary>
        public static string FirstQuestion(bool brief) => brief
            ? "Please describe this picture briefly, in two or three sentences."
            : "Please describe this picture in full.";

        // ---------- keys and models ----------

        /// <summary>
        /// Asks the provider whether the key is good, and which models it can use
        /// to look at pictures. Throws an <see cref="AiException"/> that says why not.
        /// </summary>
        public static async Task<IReadOnlyList<AiModel>> CheckKeyAsync(AiProvider provider, string key, CancellationToken token)
        {
            key = (key ?? "").Trim();
            if (WrongKind(provider, key) is { } wrong) throw new AiException(wrong, keyRefused: true);

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(CheckTimeout);
            var t = limit.Token;
            try
            {
                switch (provider)
                {
                    case AiProvider.Claude:
                    {
                        var json = await SendAsync(provider, "", HttpMethod.Get, "https://api.anthropic.com/v1/models?limit=1000",
                            Headers(provider, key), null, t, retry: false).ConfigureAwait(false);
                        return ClaudeModels(json);
                    }
                    case AiProvider.Gemini:
                    {
                        var json = await SendAsync(provider, "", HttpMethod.Get,
                            "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000",
                            Headers(provider, key), null, t, retry: false).ConfigureAwait(false);
                        return GeminiModels(json);
                    }
                    case AiProvider.OpenAI:
                    {
                        var json = await SendAsync(provider, "", HttpMethod.Get, "https://api.openai.com/v1/models",
                            Headers(provider, key), null, t, retry: false).ConfigureAwait(false);
                        return OpenAiModels(json);
                    }
                    case AiProvider.OpenRouter:
                    {
                        // The model list is public, so the key is checked on its own.
                        await SendAsync(provider, "", HttpMethod.Get, "https://openrouter.ai/api/v1/key",
                            Headers(provider, key), null, t, retry: false).ConfigureAwait(false);
                        var json = await SendAsync(provider, "", HttpMethod.Get, "https://openrouter.ai/api/v1/models",
                            new Dictionary<string, string>(), null, t, retry: false).ConfigureAwait(false);
                        RememberOpenRouterPrices(json);
                        return OpenRouterModels(json);
                    }
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new AiException($"{Company(provider)} took too long to answer. Check your internet connection and try again.");
            }
            throw new AiException("That provider isn't known.");
        }

        internal static IReadOnlyList<AiModel> ClaudeModels(JsonNode? json)
        {
            var list = new List<AiModel>();
            foreach (var m in json?["data"]?.AsArray() ?? new JsonArray())
            {
                var id = Text(m?["id"]);
                if (id.Length == 0 || !id.StartsWith("claude", StringComparison.OrdinalIgnoreCase)) continue;
                var name = Text(m?["display_name"]);
                list.Add(new AiModel(id, name.Length > 0 ? name : id));
            }
            if (list.Count == 0) throw new AiException("Anthropic accepted the key but offered no Claude models.");
            return list;
        }

        internal static IReadOnlyList<AiModel> GeminiModels(JsonNode? json)
        {
            var list = new List<AiModel>();
            foreach (var m in json?["models"]?.AsArray() ?? new JsonArray())
            {
                var id = Text(m?["name"]);
                if (id.StartsWith("models/", StringComparison.Ordinal)) id = id[7..];
                var methods = m?["supportedGenerationMethods"]?.AsArray().Select(x => Text(x)).ToList() ?? new List<string>();
                if (!methods.Contains("generateContent")) continue;
                var lower = id.ToLowerInvariant();
                if (!(lower.StartsWith("gemini", StringComparison.Ordinal) || lower.StartsWith("gemma", StringComparison.Ordinal))) continue;
                if (new[] { "embedding", "tts", "image-generation", "-image", "live", "audio", "robotics", "computer-use", "aqa" }
                    .Any(w => lower.Contains(w))) continue;
                var name = Text(m?["displayName"]);
                list.Add(new AiModel(id, name.Length > 0 ? name : id));
            }
            if (list.Count == 0) throw new AiException("Google accepted the key but offered no Gemini models that can look at pictures.");
            return list.OrderByDescending(x => Version(x.Id)).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
        }

        internal static IReadOnlyList<AiModel> OpenAiModels(JsonNode? json)
        {
            var list = new List<AiModel>();
            var skip = new[]
            {
                "audio", "realtime", "transcribe", "tts", "search", "embedding", "image", "dall-e", "whisper",
                "moderation", "codex", "instruct", "davinci", "babbage", "gpt-3.5", "gpt-4-0", "gpt-4-32k", "gpt-4-1106",
                "computer-use", "deep-research", "sora", "omni-moderation",
            };
            foreach (var m in json?["data"]?.AsArray() ?? new JsonArray())
            {
                var id = Text(m?["id"]);
                var lower = id.ToLowerInvariant();
                bool family = lower.StartsWith("gpt-", StringComparison.Ordinal) || lower.StartsWith("chatgpt-", StringComparison.Ordinal) ||
                              Regex.IsMatch(lower, "^o[1-9]");
                if (!family || lower == "gpt-4" || skip.Any(s => lower.Contains(s))) continue;
                list.Add(new AiModel(id, id));
            }
            if (list.Count == 0) throw new AiException("OpenAI accepted the key but offered no models that can look at pictures.");
            return list.OrderByDescending(x => Version(x.Id)).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
        }

        internal static IReadOnlyList<AiModel> OpenRouterModels(JsonNode? json)
        {
            var list = new List<AiModel>();
            foreach (var m in json?["data"]?.AsArray() ?? new JsonArray())
            {
                var id = Text(m?["id"]);
                if (id.Length == 0 || id.EndsWith(":batch", StringComparison.Ordinal)) continue;
                var arch = m?["architecture"];
                var inputs = arch?["input_modalities"]?.AsArray().Select(x => Text(x)).ToList() ?? new List<string>();
                var outputs = arch?["output_modalities"]?.AsArray().Select(x => Text(x)).ToList() ?? new List<string>();
                if (!inputs.Contains("image") || !outputs.Contains("text") || outputs.Contains("image") || outputs.Contains("audio")) continue;
                var name = Text(m?["name"]);
                list.Add(new AiModel(id, name.Length > 0 ? name : id));
            }
            if (list.Count == 0) throw new AiException("OpenRouter offered no models that can look at pictures.");
            return list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>The first version number in a model id: "claude-sonnet-4-5-20250929" is 4.5, "gpt-5.4-mini" 5.4.</summary>
        internal static double Version(string id)
        {
            var normal = Normalise(id);
            var match = Regex.Match(normal, @"(?<![a-z])(\d+)(?:-(\d{1,2}))?(?![0-9])");
            if (!match.Success) return 0;
            double whole = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (whole > 100) return 0;
            double part = match.Groups[2].Success ? double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            return whole + part / (part >= 10 ? 100 : 10);
        }

        /// <summary>The model chosen for a key just checked, when the person has not picked one.</summary>
        public static string Recommended(AiProvider provider, IReadOnlyList<AiModel> models)
        {
            if (models.Count == 0) return "";
            IEnumerable<AiModel> Pick(Func<string, bool> fits) => models.Where(m => fits(m.Id.ToLowerInvariant()));
            AiModel? Newest(IEnumerable<AiModel> some) => some.OrderByDescending(m => Version(m.Id)).FirstOrDefault();

            AiModel? choice = provider switch
            {
                AiProvider.Claude => Newest(Pick(id => id.Contains("sonnet"))),
                AiProvider.Gemini => Newest(Pick(id => id.StartsWith("gemini") && id.Contains("flash") && !id.Contains("lite") &&
                                                       !id.Contains("preview") && !id.Contains("exp") && !id.Contains("latest"))) ??
                                     Newest(Pick(id => id.StartsWith("gemini") && id.Contains("flash"))),
                AiProvider.OpenAI => Newest(Pick(id => id.StartsWith("gpt-") && id.Contains("mini") && !id.Contains("pro") &&
                                                       !Regex.IsMatch(id, @"\d{4}-\d{2}-\d{2}"))) ??
                                     Newest(Pick(id => id.StartsWith("gpt-"))),
                AiProvider.OpenRouter => Newest(Pick(id => id.StartsWith("anthropic/claude-sonnet"))) ??
                                         Newest(Pick(id => id.StartsWith("google/gemini") && id.Contains("flash"))),
                _ => null,
            };
            return (choice ?? models[0]).Id;
        }

        // ---------- the conversation ----------

        /// <summary>
        /// One answer to the conversation so far. <paramref name="turns"/> starts
        /// with the person's first question, which goes with the picture, and
        /// alternates from there. Busy providers are tried again twice.
        /// </summary>
        public static async Task<AiReply> AskAsync(AiProvider provider, string key, string model, AiPicture picture,
            string fileName, IReadOnlyList<AiTurn> turns, CancellationToken token)
        {
            if (turns.Count == 0 || !turns[0].FromPerson) throw new ArgumentException("The conversation starts with the person.");
            key = (key ?? "").Trim();
            if (key.Length == 0) throw new AiException($"There's no {Name(provider)} key. Set one up in Preferences, AI image describer.", keyRefused: true);
            if (string.IsNullOrWhiteSpace(model)) throw new AiException($"No {Name(provider)} model is chosen. Choose one in Preferences, AI image describer.");

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(AnswerTimeout);
            var system = Instructions(fileName);
            try
            {
                switch (provider)
                {
                    case AiProvider.Claude:
                    {
                        var json = await SendAsync(provider, model, HttpMethod.Post, "https://api.anthropic.com/v1/messages",
                            Headers(provider, key), ClaudeBody(model, system, picture, turns), limit.Token).ConfigureAwait(false);
                        return ClaudeReply(json);
                    }
                    case AiProvider.Gemini:
                    {
                        var url = "https://generativelanguage.googleapis.com/v1beta/models/" + Uri.EscapeDataString(model) + ":generateContent";
                        var json = await SendAsync(provider, model, HttpMethod.Post, url, Headers(provider, key),
                            GeminiBody(system, picture, turns), limit.Token).ConfigureAwait(false);
                        return GeminiReply(json);
                    }
                    case AiProvider.OpenAI:
                    {
                        var json = await SendAsync(provider, model, HttpMethod.Post, "https://api.openai.com/v1/responses",
                            Headers(provider, key), OpenAiBody(model, system, picture, turns), limit.Token).ConfigureAwait(false);
                        return OpenAiReply(json);
                    }
                    case AiProvider.OpenRouter:
                    {
                        var json = await SendAsync(provider, model, HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions",
                            Headers(provider, key), OpenRouterBody(model, system, picture, turns), limit.Token).ConfigureAwait(false);
                        return OpenRouterReply(json);
                    }
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new AiException($"{Speaker(provider, model)} took too long to answer. Press Enter to try again.");
            }
            throw new AiException("That provider isn't known.");
        }

        private static string DataUrl(AiPicture picture) => "data:" + picture.MediaType + ";base64," + Convert.ToBase64String(picture.Bytes);

        internal static JsonObject ClaudeBody(string model, string system, AiPicture picture, IReadOnlyList<AiTurn> turns)
        {
            var messages = new JsonArray();
            for (int i = 0; i < turns.Count; i++)
            {
                var turn = turns[i];
                if (i == 0)
                {
                    messages.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "image",
                                ["source"] = new JsonObject
                                {
                                    ["type"] = "base64",
                                    ["media_type"] = picture.MediaType,
                                    ["data"] = Convert.ToBase64String(picture.Bytes),
                                },
                                // Follow-up questions read the picture from Anthropic's cache at a tenth of the price.
                                ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
                            },
                            new JsonObject { ["type"] = "text", ["text"] = turn.Text },
                        },
                    });
                }
                else messages.Add(new JsonObject { ["role"] = turn.FromPerson ? "user" : "assistant", ["content"] = turn.Text });
            }
            return new JsonObject
            {
                ["model"] = model,
                ["max_tokens"] = 2048,
                ["system"] = system,
                ["messages"] = messages,
            };
        }

        internal static AiReply ClaudeReply(JsonNode? json)
        {
            var text = new StringBuilder();
            foreach (var block in json?["content"]?.AsArray() ?? new JsonArray())
                if (Text(block?["type"]) == "text") text.Append(Text(block?["text"]));
            var stop = Text(json?["stop_reason"]);
            var usage = json?["usage"];
            var said = text.ToString().Trim();
            if (said.Length == 0)
                throw new AiException(stop == "refusal" ? "Claude declined to describe this picture." : "Claude sent back an empty answer. Press Enter to try again.");
            return new AiReply(said, Number(usage?["input_tokens"]), Number(usage?["output_tokens"]),
                Number(usage?["cache_creation_input_tokens"]), Number(usage?["cache_read_input_tokens"]), null, stop == "max_tokens");
        }

        internal static JsonObject GeminiBody(string system, AiPicture picture, IReadOnlyList<AiTurn> turns)
        {
            var contents = new JsonArray();
            for (int i = 0; i < turns.Count; i++)
            {
                var turn = turns[i];
                var parts = new JsonArray();
                if (i == 0)
                    parts.Add(new JsonObject
                    {
                        ["inline_data"] = new JsonObject
                        {
                            ["mime_type"] = picture.MediaType,
                            ["data"] = Convert.ToBase64String(picture.Bytes),
                        },
                    });
                parts.Add(new JsonObject { ["text"] = turn.Text });
                contents.Add(new JsonObject { ["role"] = turn.FromPerson ? "user" : "model", ["parts"] = parts });
            }
            return new JsonObject
            {
                ["system_instruction"] = new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = system } } },
                ["contents"] = contents,
                ["generationConfig"] = new JsonObject { ["maxOutputTokens"] = 8192 },
            };
        }

        internal static AiReply GeminiReply(JsonNode? json)
        {
            var blocked = Text(json?["promptFeedback"]?["blockReason"]);
            if (blocked.Length > 0) throw new AiException("Gemini declined to look at this picture (" + Humanised(blocked) + ").");
            var candidate = json?["candidates"]?.AsArray().FirstOrDefault();
            var finish = Text(candidate?["finishReason"]);
            var text = new StringBuilder();
            foreach (var part in candidate?["content"]?["parts"]?.AsArray() ?? new JsonArray())
                if (part?["thought"]?.GetValueKind() != System.Text.Json.JsonValueKind.True) text.Append(Text(part?["text"]));
            var usage = json?["usageMetadata"];
            var said = text.ToString().Trim();
            if (said.Length == 0)
                throw new AiException(finish is "SAFETY" or "PROHIBITED_CONTENT" or "BLOCKLIST" or "RECITATION" or "IMAGE_SAFETY"
                    ? "Gemini declined to describe this picture (" + Humanised(finish) + ")."
                    : "Gemini sent back an empty answer. Press Enter to try again.");
            long cached = Number(usage?["cachedContentTokenCount"]);
            return new AiReply(said, Number(usage?["promptTokenCount"]) - cached,
                Number(usage?["candidatesTokenCount"]) + Number(usage?["thoughtsTokenCount"]), 0, cached, null, finish == "MAX_TOKENS");
        }

        internal static JsonObject OpenAiBody(string model, string system, AiPicture picture, IReadOnlyList<AiTurn> turns)
        {
            var input = new JsonArray();
            for (int i = 0; i < turns.Count; i++)
            {
                var turn = turns[i];
                if (i == 0)
                    input.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray
                        {
                            new JsonObject { ["type"] = "input_image", ["image_url"] = DataUrl(picture), ["detail"] = "high" },
                            new JsonObject { ["type"] = "input_text", ["text"] = turn.Text },
                        },
                    });
                else input.Add(new JsonObject { ["role"] = turn.FromPerson ? "user" : "assistant", ["content"] = turn.Text });
            }
            return new JsonObject
            {
                ["model"] = model,
                ["instructions"] = system,
                ["input"] = input,
                ["max_output_tokens"] = 8000,
                ["store"] = false,
            };
        }

        internal static AiReply OpenAiReply(JsonNode? json)
        {
            var text = new StringBuilder();
            string refusal = "";
            foreach (var item in json?["output"]?.AsArray() ?? new JsonArray())
            {
                if (Text(item?["type"]) != "message") continue;
                foreach (var part in item?["content"]?.AsArray() ?? new JsonArray())
                {
                    var type = Text(part?["type"]);
                    if (type == "output_text") text.Append(Text(part?["text"]));
                    else if (type == "refusal") refusal = Text(part?["refusal"]);
                }
            }
            var said = text.ToString().Trim();
            bool cut = Text(json?["status"]) == "incomplete";
            if (said.Length == 0)
                throw new AiException(refusal.Length > 0 ? "GPT declined: " + Short(refusal)
                    : cut ? "GPT ran out of room before it said anything. Try a different model in Preferences."
                    : "GPT sent back an empty answer. Press Enter to try again.");
            var usage = json?["usage"];
            long cached = Number(usage?["input_tokens_details"]?["cached_tokens"]);
            return new AiReply(said, Number(usage?["input_tokens"]) - cached, Number(usage?["output_tokens"]), 0, cached, null, cut);
        }

        internal static JsonObject OpenRouterBody(string model, string system, AiPicture picture, IReadOnlyList<AiTurn> turns)
        {
            var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
            for (int i = 0; i < turns.Count; i++)
            {
                var turn = turns[i];
                if (i == 0)
                    messages.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray
                        {
                            new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = DataUrl(picture) } },
                            new JsonObject { ["type"] = "text", ["text"] = turn.Text },
                        },
                    });
                else messages.Add(new JsonObject { ["role"] = turn.FromPerson ? "user" : "assistant", ["content"] = turn.Text });
            }
            return new JsonObject
            {
                ["model"] = model,
                ["messages"] = messages,
                ["max_tokens"] = 4096,
                ["usage"] = new JsonObject { ["include"] = true },
            };
        }

        internal static AiReply OpenRouterReply(JsonNode? json)
        {
            // OpenRouter can answer 200 with the model's failure inside.
            if (json?["error"] is JsonNode error)
                throw new AiException("OpenRouter: " + Short(Text(error["message"]).Length > 0 ? Text(error["message"]) : "the model failed."));
            var choice = json?["choices"]?.AsArray().FirstOrDefault();
            var message = choice?["message"];
            string said = "";
            if (message?["content"] is JsonValue value) said = Text(value).Trim();
            else if (message?["content"] is JsonArray parts)
                said = string.Concat(parts.Select(p => Text(p?["text"]))).Trim();
            var finish = Text(choice?["finish_reason"]);
            if (said.Length == 0)
            {
                var refusal = Text(message?["refusal"]);
                throw new AiException(refusal.Length > 0 ? "The model declined: " + Short(refusal)
                    : finish == "content_filter" ? "The model declined to describe this picture."
                    : "The model sent back an empty answer. Press Enter to try again.");
            }
            var usage = json?["usage"];
            decimal? cost = usage?["cost"] is JsonValue c && c.TryGetValue(out decimal dollars) ? dollars : null;
            long cached = Number(usage?["prompt_tokens_details"]?["cached_tokens"]);
            return new AiReply(said, Number(usage?["prompt_tokens"]) - cached, Number(usage?["completion_tokens"]), 0, cached, cost,
                finish == "length");
        }

        // ---------- the wire ----------

        private static Dictionary<string, string> Headers(AiProvider provider, string key) => provider switch
        {
            AiProvider.Claude => new() { ["x-api-key"] = key, ["anthropic-version"] = "2023-06-01" },
            AiProvider.Gemini => new() { ["x-goog-api-key"] = key },
            AiProvider.OpenRouter => new()
            {
                ["Authorization"] = "Bearer " + key,
                ["HTTP-Referer"] = "https://github.com/ce2004/explorer-native",
                ["X-Title"] = "Explorer Native",
            },
            _ => new() { ["Authorization"] = "Bearer " + key },
        };

        /// <summary>
        /// One request, answered as JSON, or a sentence saying what went wrong.
        /// Busy (429 without "quota", 500, 502, 503, 504, 529) is tried again
        /// after <see cref="RetryWaits"/>; nothing else is.
        /// </summary>
        private static async Task<JsonNode?> SendAsync(AiProvider provider, string model, HttpMethod method, string url,
            Dictionary<string, string> headers, JsonObject? body, CancellationToken token, bool retry = true)
        {
            var payload = body?.ToJsonString();
            for (int attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                using var request = new HttpRequestMessage(method, url);
                foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
                request.Headers.TryAddWithoutValidation("User-Agent", "ExplorerNative");
                if (payload != null) request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                HttpResponseMessage response;
                try { response = await Http.SendAsync(request, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (HttpRequestException ex)
                {
                    if (retry && attempt < RetryWaits.Length) { await Task.Delay(RetryWaits[attempt], token).ConfigureAwait(false); continue; }
                    throw new AiException($"Can't reach {Company(provider)}. Check your internet connection, then try again. ({Short(GoogleNet.Reason(ex))})");
                }

                using (response)
                {
                    string text;
                    try { text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        if (retry && attempt < RetryWaits.Length) { await Task.Delay(RetryWaits[attempt], token).ConfigureAwait(false); continue; }
                        throw new AiException($"The answer from {Company(provider)} was cut off. Try again. ({Short(ex.Message)})");
                    }

                    if (response.IsSuccessStatusCode)
                    {
                        try { return JsonNode.Parse(text); }
                        catch { throw new AiException($"{Company(provider)} sent back something Explorer Native couldn't read. Try again."); }
                    }

                    var failure = Failure(provider, model, (int)response.StatusCode, text);
                    bool busy = !failure.KeyRefused && !failure.OutOfCredit &&
                                (int)response.StatusCode is 429 or 500 or 502 or 503 or 504 or 529;
                    if (busy && retry && attempt < RetryWaits.Length)
                    {
                        var wait = RetryWaits[attempt];
                        if (response.Headers.RetryAfter?.Delta is { } after && after < TimeSpan.FromSeconds(20)) wait = after;
                        await Task.Delay(wait, token).ConfigureAwait(false);
                        continue;
                    }
                    throw failure;
                }
            }
        }

        /// <summary>A provider's refusal as a sentence a person can act on.</summary>
        internal static AiException Failure(AiProvider provider, string model, int status, string body)
        {
            string message = "", code = "", type = "";
            try
            {
                var json = JsonNode.Parse(body);
                var error = json?["error"];
                if (error is JsonValue) message = Text(error);
                else if (error != null)
                {
                    message = Text(error["message"]);
                    code = Text(error["code"]) + " " + Text(error["status"]);
                    type = Text(error["type"]);
                }
                if (message.Length == 0) message = Text(json?["message"]);
            }
            catch { message = body.TrimStart().StartsWith('<') ? "" : body; }

            var lower = (message + " " + code + " " + type).ToLowerInvariant();
            var who = Company(provider);
            var where = "Preferences, AI image describer";

            if (status == 401 || lower.Contains("api key not valid") || lower.Contains("api_key_invalid") ||
                lower.Contains("invalid x-api-key") || lower.Contains("invalid_api_key") || lower.Contains("incorrect api key"))
                return new AiException($"{who} didn't accept the API key. It may have a typing mistake, or it was deleted. Check it in {where}.", keyRefused: true);
            if (status == 402 || lower.Contains("credit balance") || lower.Contains("insufficient_quota") ||
                lower.Contains("insufficient credits") || lower.Contains("billing") || lower.Contains("exceeded your current quota"))
                return new AiException($"Your {who} account is out of credit. Add some on {who}'s website, then try again.", outOfCredit: true);
            if (status == 403)
                return lower.Contains("flagged") || lower.Contains("moderation")
                    ? new AiException($"{who}'s safety check turned this picture down.")
                    : new AiException($"{who} refused: {Short(message.Length > 0 ? message : "this key isn't allowed to do that.")}", keyRefused: lower.Contains("permission") || lower.Contains("key"));
            if (status == 404 || lower.Contains("model_not_found") || lower.Contains("not_found_error"))
                return new AiException(model.Length > 0
                    ? $"The model {model} isn't available to your {who} account. Choose another one in {where}."
                    : $"{who} couldn't find that. {Short(message)}");
            if (status == 413 || lower.Contains("too large") || lower.Contains("request_too_large"))
                return new AiException($"The picture is too large for {who}.");
            if (status == 429)
                return new AiException($"{who} says you've sent too many requests. Wait a minute, then press Enter to try again.");
            if (status == 529 || lower.Contains("overloaded"))
                return new AiException($"{who} is overloaded right now. Wait a moment, then press Enter to try again.");
            if (status >= 500)
                return new AiException($"{who} is having problems right now (error {status}). Try again in a minute.");
            return new AiException($"{who} couldn't take that request (error {status}). {Short(message)}".Trim());
        }

        // ---------- prices ----------

        private static readonly object PricesGate = new();
        private static Dictionary<string, AiPrice>? _openRouterPrices;
        private static DateTime _openRouterPricesAt;

        /// <summary>
        /// Known prices for when OpenRouter's list cannot be had, per million
        /// tokens: input, output. Taken from OpenRouter's list on 2026-11-03.
        /// </summary>
        private static readonly (string Model, decimal Input, decimal Output)[] KnownPrices =
        {
            ("claude-haiku-5-5", 0.10m, 0.50m), ("claude-sonnet-5-5", 2m, 10m), ("claude-opus-5-5", 4m, 20m),
            ("claude-fable-5-1", 10m, 50m), ("claude-sonnet-5", 2m, 10m), ("claude-opus-5", 5m, 25m), ("claude-fable-5", 10m, 50m),
            ("claude-opus-4-8", 5m, 25m), ("claude-opus-4-7", 5m, 25m), ("claude-opus-4-6", 5m, 25m), ("claude-opus-4-5", 5m, 25m),
            ("claude-sonnet-4-6", 3m, 15m), ("claude-sonnet-4-5", 3m, 15m), ("claude-haiku-4-5", 1m, 5m),
            ("gemini-3-8-flash", 0.825m, 4.125m), ("gemini-3-5-flash", 1.5m, 9m), ("gemini-3-5-flash-lite", 0.3m, 2.5m),
            ("gemini-2-5-flash", 0.3m, 2.5m), ("gemini-2-5-flash-lite", 0.1m, 0.4m), ("gemini-2-5-pro", 1.25m, 10m),
            ("gpt-5-mini", 0.25m, 2m), ("gpt-5", 1.25m, 10m), ("gpt-5-nano", 0.05m, 0.4m), ("gpt-4o", 2.5m, 10m),
            ("gpt-4o-mini", 0.15m, 0.6m), ("gpt-4-1", 2m, 8m), ("gpt-4-1-mini", 0.4m, 1.6m), ("gpt-5-4-mini", 0.75m, 4.5m),
        };

        /// <summary>
        /// "claude-sonnet-4-5-20250929", "anthropic/claude-sonnet-4.5" and
        /// "models/gemini-2.5-flash" all come down to one spelling, so a model
        /// from any provider finds its price in OpenRouter's list.
        /// </summary>
        internal static string Normalise(string id)
        {
            var s = (id ?? "").Trim().ToLowerInvariant().TrimStart('~');
            if (s.StartsWith("models/", StringComparison.Ordinal)) s = s[7..];
            int colon = s.IndexOf(':');
            if (colon >= 0) s = s[..colon];
            int slash = s.LastIndexOf('/');
            if (slash >= 0) s = s[(slash + 1)..];
            s = s.Replace('.', '-').Replace('_', '-');
            s = Regex.Replace(s, @"-(\d{8}|\d{4}-\d{2}-\d{2})$", "");
            return s;
        }

        internal static void RememberOpenRouterPrices(JsonNode? json)
        {
            var prices = new Dictionary<string, AiPrice>(StringComparer.Ordinal);
            foreach (var m in json?["data"]?.AsArray() ?? new JsonArray())
            {
                var id = Text(m?["id"]);
                if (id.Length == 0 || id.EndsWith(":batch", StringComparison.Ordinal) || id.EndsWith(":free", StringComparison.Ordinal)) continue;
                var p = m?["pricing"];
                if (!Dollars(p?["prompt"], out var input) || !Dollars(p?["completion"], out var output) || input < 0 || output < 0) continue;
                decimal? read = Dollars(p?["input_cache_read"], out var r) ? r : null;
                decimal? write = Dollars(p?["input_cache_write"], out var w) ? w : null;
                var price = new AiPrice(input, output, read, write);
                prices[id] = price;
                var normal = Normalise(id);
                if (!id.StartsWith("~", StringComparison.Ordinal) && !prices.ContainsKey("=" + normal)) prices["=" + normal] = price;
            }
            if (prices.Count == 0) return;
            lock (PricesGate) { _openRouterPrices = prices; _openRouterPricesAt = DateTime.UtcNow; }
        }

        /// <summary>For the suite: forget or set OpenRouter's prices.</summary>
        internal static void ForgetPricesForTests() { lock (PricesGate) { _openRouterPrices = null; } }

        /// <summary>OpenRouter's price list, fetched at most once a day. Never throws.</summary>
        private static async Task LoadPricesAsync(CancellationToken token)
        {
            lock (PricesGate)
                if (_openRouterPrices != null && DateTime.UtcNow - _openRouterPricesAt < TimeSpan.FromHours(24)) return;
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(TimeSpan.FromSeconds(15));
                var json = await SendAsync(AiProvider.OpenRouter, "", HttpMethod.Get, "https://openrouter.ai/api/v1/models",
                    new Dictionary<string, string>(), null, limit.Token, retry: false).ConfigureAwait(false);
                RememberOpenRouterPrices(json);
            }
            catch { }
        }

        /// <summary>A model's price per token, or null when nobody knows it.</summary>
        public static async Task<AiPrice?> PriceAsync(AiProvider provider, string model, CancellationToken token)
        {
            await LoadPricesAsync(token).ConfigureAwait(false);
            return PriceNow(provider, model);
        }

        /// <summary>The price from what is already held, without asking anyone.</summary>
        public static AiPrice? PriceNow(AiProvider provider, string model)
        {
            Dictionary<string, AiPrice>? prices;
            lock (PricesGate) prices = _openRouterPrices;
            if (string.IsNullOrWhiteSpace(model)) return null;

            if (prices != null)
            {
                if (provider == AiProvider.OpenRouter && prices.TryGetValue(model, out var exact)) return exact;
                var normal = Normalise(model);
                if (provider != AiProvider.OpenRouter)
                {
                    // Trimmed a part at a time: "gemini-2-5-flash-preview-09-2025" finds gemini-2-5-flash.
                    for (var name = normal; name.Length > 0;)
                    {
                        if (prices.TryGetValue("=" + name, out var found)) return found;
                        int dash = name.LastIndexOf('-');
                        if (dash <= 0) break;
                        name = name[..dash];
                        if (!name.Any(char.IsDigit)) break;
                    }
                }
            }
            {
                var normal = Normalise(model);
                for (var name = normal; name.Length > 0;)
                {
                    foreach (var known in KnownPrices)
                        if (known.Model == name) return new AiPrice(known.Input / 1_000_000m, known.Output / 1_000_000m, null, null);
                    int dash = name.LastIndexOf('-');
                    if (dash <= 0 || provider == AiProvider.OpenRouter && !model.Contains('/')) break;
                    name = name[..dash];
                    if (!name.Any(char.IsDigit)) break;
                }
            }
            return null;
        }

        /// <summary>
        /// What a reply cost in dollars: the provider's own figure when it gave
        /// one, otherwise worked out from the price. Cache writes are 1.25 times
        /// the input price and reads a tenth when the list does not say.
        /// </summary>
        public static decimal? CostOf(AiReply reply, AiPrice? price)
        {
            if (reply.Cost is { } given) return given;
            if (price == null) return null;
            return reply.InputTokens * price.Input +
                   reply.CacheWriteTokens * (price.CacheWrite ?? price.Input * 1.25m) +
                   reply.CacheReadTokens * (price.CacheRead ?? price.Input * 0.1m) +
                   reply.OutputTokens * price.Output;
        }

        /// <summary>About what one picture's description costs: 2,000 tokens in and 500 out.</summary>
        public static decimal? PerPicture(AiPrice? price) => price == null ? null : 2000 * price.Input + 500 * price.Output;

        /// <summary>
        /// Dollars in words a screen reader reads well: "less than a tenth of a
        /// cent", "about 1.8 cents", "about 2 dollars 40 cents".
        /// </summary>
        public static string Money(decimal dollars)
        {
            var c = CultureInfo.InvariantCulture;
            if (dollars <= 0) return "nothing";
            decimal cents = dollars * 100;
            if (cents < 0.1m) return "less than a tenth of a cent";
            if (cents < 10)
            {
                var rounded = Math.Round(cents, 1, MidpointRounding.AwayFromZero);
                return "about " + rounded.ToString("0.#", c) + (rounded == 1 ? " cent" : " cents");
            }
            if (cents < 100)
            {
                var whole = Math.Round(cents, 0, MidpointRounding.AwayFromZero);
                return "about " + whole.ToString("0", c) + " cents";
            }
            var total = Math.Round(cents, 0, MidpointRounding.AwayFromZero);
            long d = (long)(total / 100), rest = (long)(total % 100);
            return "about " + d.ToString(c) + (d == 1 ? " dollar" : " dollars") +
                   (rest == 0 ? "" : " " + rest.ToString(c) + (rest == 1 ? " cent" : " cents"));
        }

        /// <summary>The line after the edit box.</summary>
        public static string CostSentence(decimal dollars, bool someUnknown, int answers)
        {
            if (answers == 0) return "Nothing has been spent on this conversation yet.";
            if (someUnknown && dollars == 0) return "The price of this model isn't known, so the cost of this conversation can't be worked out.";
            var said = "This conversation has cost " + Money(dollars) + " so far.";
            return someUnknown ? said + " Some answers came from a model whose price isn't known, so it may be more." : said;
        }

        /// <summary>A model as the list shows it, with about what a picture costs when that is known.</summary>
        public static string ModelLabel(AiProvider provider, AiModel model)
        {
            var name = model.Name.Length > 0 ? model.Name : model.Id;
            var each = PerPicture(PriceNow(provider, model.Id));
            return each is { } d ? name + ", " + Money(d) + " a picture" : name;
        }

        // ---------- small things ----------

        private static string Text(JsonNode? node)
        {
            try
            {
                if (node is JsonValue v)
                {
                    if (v.TryGetValue(out string? s)) return s ?? "";
                    return v.ToJsonString();
                }
            }
            catch { }
            return "";
        }

        private static long Number(JsonNode? node)
        {
            try
            {
                if (node is JsonValue v)
                {
                    if (v.TryGetValue(out long l)) return Math.Max(0, l);
                    if (v.TryGetValue(out double d)) return Math.Max(0, (long)d);
                }
            }
            catch { }
            return 0;
        }

        private static bool Dollars(JsonNode? node, out decimal value)
        {
            value = 0;
            try
            {
                if (node is not JsonValue v) return false;
                if (v.TryGetValue(out string? s))
                    return decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
                if (v.TryGetValue(out decimal d)) { value = d; return true; }
            }
            catch { }
            return false;
        }

        private static string Humanised(string reason) => reason.Replace('_', ' ').ToLowerInvariant();

        /// <summary>At most 300 characters, never markup.</summary>
        internal static string Short(string? text)
        {
            var s = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (s.StartsWith('<')) return "";
            return s.Length <= 300 ? s : s[..297] + "...";
        }
    }
}
