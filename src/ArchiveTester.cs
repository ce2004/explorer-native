using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace ExplorerNative
{
    /// <summary>What Test archive found.</summary>
    public sealed record ArchiveTestResult(string ArchiveName, int Total, IReadOnlyList<string> Bad, bool ReadToEnd)
    {
        public bool Fine => ReadToEnd && Bad.Count == 0;

        /// <summary>The one sentence said at the end.</summary>
        public string Sentence
        {
            get
            {
                if (Fine)
                    return Total switch
                    {
                        0 => $"{ArchiveName} is fine, but it holds no files.",
                        1 => $"{ArchiveName} is fine: its one file is intact.",
                        _ => $"{ArchiveName} is fine: all {Total:N0} files are intact.",
                    };
                if (!ReadToEnd)
                    return Total == 0
                        ? $"{ArchiveName} is damaged: none of it could be read."
                        : $"{ArchiveName} is damaged: it could be read only part way, and the files after {Bad.LastOrDefault() ?? "that"} could not be checked.";
                return $"{ArchiveName} is damaged: {Bad.Count:N0} of {Total:N0} {(Total == 1 ? "file" : "files")} {(Bad.Count == 1 ? "is" : "are")} bad.";
            }
        }
    }

    /// <summary>
    /// Test archive: reads every member and checks it, writing nothing. Zip
    /// through our own reader with its CRCs, tar and tar.gz through the tar
    /// reader (gzip checks its own CRC), and the rest through "7za t".
    /// </summary>
    public static class ArchiveTester
    {
        /// <summary>Whether Test archive is offered for this name. By name only, like every menu decision.</summary>
        public static bool CanTest(string? path) =>
            ArchiveFormats.FromName(path)?.Id is "zip" or "tar" or "tar.gz" or "gz" ||
            (ArchiveFormats.FromName(path)?.Id is "7z" or "tar.xz" or "tar.bz2" or "xz" or "bz2" &&
             BsdTar.SevenZipExecutable != null);

        /// <summary>On a worker. Asks for a password through <see cref="ArchivePasswords"/> when it needs one.</summary>
        /// <exception cref="NotSupportedException">Not an archive this can test.</exception>
        public static ArchiveTestResult Test(string archivePath, IProgress<TransferProgress>? progress, CancellationToken token)
        {
            var name = Path.GetFileName(archivePath);
            var exact = NameRules.ExactPath(archivePath);
            var format = ArchiveFormats.Identify(exact)
                         ?? throw new NotSupportedException($"{name} is not an archive this can test.");
            var reporter = new ArchiveEngine.Reporter(progress);
            try
            {
                switch (format.Id)
                {
                    case "zip":
                        if (ArchiveEngine.IsProtectedZip(exact, format, token))
                            return BsdTar.TestWithSevenZip(exact, name, compressedTar: false, reporter, token);
                        return TestZip(exact, name, reporter, token);
                    case "tar":
                    case "tar.gz":
                    case "gz":
                        return TestTar(exact, name, format, reporter, token);
                    case "7z":
                    case "xz":
                    case "bz2":
                    case "tar.xz":
                    case "tar.bz2":
                        if (BsdTar.SevenZipExecutable == null) break;
                        return BsdTar.TestWithSevenZip(exact, name, compressedTar: format.Id.StartsWith("tar.", StringComparison.Ordinal),
                            reporter, token);
                }
            }
            finally { reporter.Done(); }
            throw new NotSupportedException($"Explorer Native can't test {format.Name} archives.");
        }

        private static ArchiveTestResult TestZip(string path, string name, ArchiveEngine.Reporter reporter, CancellationToken token)
        {
            ZipArchive archive;
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            try { archive = ZipEngine.Open(file); }
            catch (InvalidDataException)
            {
                file.Dispose();
                return new ArchiveTestResult(name, 0, Array.Empty<string>(), ReadToEnd: false);
            }

            using (file)
            using (archive)
            {
                var files = archive.Entries.Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\')).ToList();
                reporter.SetTotals(files.Sum(e => Math.Max(0, e.Length)), files.Count);
                var bad = new List<string>();
                var buffer = new byte[1 << 20];

                foreach (var entry in files)
                {
                    token.ThrowIfCancellationRequested();
                    reporter.Starting(entry.FullName);
                    if (ZipEngine.IsLink(entry)) { reporter.Finished(0); continue; }
                    bool ok;
                    try
                    {
                        using var source = entry.Open();
                        uint crc = Crc32.Seed;
                        long length = 0;
                        int got;
                        while ((got = source.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            token.ThrowIfCancellationRequested();
                            crc = Crc32.Append(crc, buffer.AsSpan(0, got));
                            length += got;
                            reporter.Advance(got);
                        }
                        ok = length == entry.Length && crc == entry.Crc32;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { ok = false; }
                    if (!ok) bad.Add(entry.FullName);
                    reporter.Finished(0);
                }
                return new ArchiveTestResult(name, files.Count, bad, ReadToEnd: true);
            }
        }

        private static ArchiveTestResult TestTar(string path, string name, ArchiveFormat format,
            ArchiveEngine.Reporter reporter, CancellationToken token)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20,
                FileOptions.SequentialScan);
            reporter.Estimate(file.Length, () => file.Position);
            using var source = new TarEngine.SourceStream(file);
            Stream data = format.Id == "tar" ? source : new ParallelGZip.Reader(source, ArchiveWorkers.Threads, token);

            int total = 0;
            string? current = null;
            using (data)
            {
                if (format.Id == "gz")
                {
                    // One file: read through, and gzip checks its own CRC and length.
                    current = ArchiveFormats.BaseName(path);
                    try { data.CopyTo(Stream.Null, 1 << 20); }
                    catch (Exception ex) when (ex is InvalidDataException or IOException and not TarEngine.ArchiveReadException)
                    {
                        return new ArchiveTestResult(name, 1, new[] { current }, ReadToEnd: true);
                    }
                    return new ArchiveTestResult(name, 1, Array.Empty<string>(), ReadToEnd: true);
                }

                using var reader = new TarReader(data, leaveOpen: true);
                var buffer = new byte[1 << 20];
                try
                {
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        var entry = reader.GetNextEntry(copyData: false);
                        if (entry == null) break;
                        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile
                            or TarEntryType.ContiguousFile or TarEntryType.SparseFile)) continue;
                        current = entry.Name;
                        total++;
                        reporter.Starting(entry.Name);
                        if (entry.DataStream is { } member)
                            while (member.Read(buffer, 0, buffer.Length) > 0) token.ThrowIfCancellationRequested();
                        reporter.Finished(0);
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or FormatException or EndOfStreamException ||
                                           (ex is IOException && ex is not TarEngine.ArchiveReadException))
                {
                    // Damage stops a stream: the file being read is the bad one,
                    // and what comes after it cannot be reached.
                    return new ArchiveTestResult(name, total, current == null ? Array.Empty<string>() : new[] { current },
                        ReadToEnd: false);
                }
            }
            return new ArchiveTestResult(name, total, Array.Empty<string>(), ReadToEnd: true);
        }
    }
}
