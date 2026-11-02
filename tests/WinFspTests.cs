using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ExplorerNative
{
    /// <summary>
    /// The WinFsp bindings on their own, before anything of Google Drive is
    /// stood on them: a file system held in memory, mounted on a real letter,
    /// and used through ordinary Windows file calls. A wrong layout or a wrong
    /// callback slot shows here as a failed check, not as a lost file.
    /// </summary>
    internal static class WinFspTests
    {
        private static Action<string, bool, string?> _check = null!;
        private static void Check(string what, bool ok, string? detail = null) => _check(what, ok, detail);

        public static Task RunAll(Action<string, bool, string?> check)
        {
            _check = check;
            Console.WriteLine("WinFsp bindings, with a file system in memory:");

            Check("the layouts match winfsp.h", WinFsp.CheckLayout() == null, WinFsp.CheckLayout());
            if (!WinFsp.TryLoad(out var problem))
            {
                Check("WinFsp is installed and loads", false, problem);
                return Task.CompletedTask;
            }
            Check("WinFsp is installed and loads", true);

            var letter = FreeLetter();
            if (letter == null) { Check("a drive letter is free for the test", false); return Task.CompletedTask; }

            var fs = new MemoryFs();
            using var host = new WinFsp.Host(fs);
            host.Create(@"\ExplorerNativeTest\mem" + Environment.ProcessId, "MemTest", 1000, 4);
            bool mounted = host.Mount(letter, out int status);
            Check("it mounts on a free letter", mounted, $"0x{status:X8}");
            if (!mounted) return Task.CompletedTask;

            var root = letter + "\\";
            try
            {
                Check("the root is a directory", Directory.Exists(root));
                File.WriteAllText(root + "hello.txt", "hello there");
                Check("a file written through the letter reads back", File.ReadAllText(root + "hello.txt") == "hello there");
                Check("and its size is right", new FileInfo(root + "hello.txt").Length == 11);

                Directory.CreateDirectory(root + "folder");
                File.WriteAllBytes(root + @"folder\data.bin", Enumerable.Range(0, 300_000).Select(i => (byte)i).ToArray());
                var back = File.ReadAllBytes(root + @"folder\data.bin");
                Check("a 300 KB file round-trips byte for byte",
                    back.Length == 300_000 && back.Select((b, i) => b == (byte)i).All(x => x));

                var names = Directory.GetFileSystemEntries(root).Select(Path.GetFileName).OrderBy(n => n).ToArray();
                Check("the root lists both", string.Join(",", names) == "folder,hello.txt", string.Join(",", names));

                File.Move(root + "hello.txt", root + @"folder\renamed.txt");
                Check("a move between folders lands", File.Exists(root + @"folder\renamed.txt") && !File.Exists(root + "hello.txt"));

                File.Delete(root + @"folder\renamed.txt");
                Check("a delete removes it", !File.Exists(root + @"folder\renamed.txt"));

                bool refused = false;
                try { Directory.Delete(root + "folder"); } catch (IOException) { refused = true; }
                Check("a folder with something in it will not be removed bare", refused);

                File.Delete(root + @"folder\data.bin");
                Directory.Delete(root + "folder");
                Check("an empty folder can be removed", !Directory.Exists(root + "folder"));

                var drive = new DriveInfo(letter);
                Check("the volume reports its own size", drive.TotalSize == 1L << 40, drive.TotalSize.ToString());
            }
            catch (Exception ex)
            {
                Check("the in-memory drive works through Windows", false, ex.GetType().Name + ": " + ex.Message);
            }

            host.Unmount();
            Check("taken off the letter it is gone", !Directory.Exists(root));
            return Task.CompletedTask;
        }

        internal static string? FreeLetter()
        {
            for (char c = 'Z'; c >= 'M'; c--)
                if (!DriveLetter.InUse(c)) return c + ":";
            return null;
        }

        /// <summary>A whole file system in a dictionary. Paths are keys, lower case.</summary>
        private sealed unsafe class MemoryFs : WinFsp.FileSystem
        {
            private sealed class Node
            {
                public string Name = "";
                public bool Folder;
                public List<byte> Data = new();
                public ulong Stamp = (ulong)DateTime.UtcNow.ToFileTimeUtc();
                public ulong Index;
            }

            private sealed class Handle
            {
                public string Key = "";
                public IntPtr DirBuffer;
            }

            private readonly Dictionary<string, Node> _nodes = new(StringComparer.OrdinalIgnoreCase);
            private readonly object _gate = new();
            private ulong _next = 1;

            public MemoryFs() => _nodes["\\"] = new Node { Name = "", Folder = true, Index = _next++ };

            public override byte[] Security { get; } = WinFsp.UserOnlySecurity();

            private static string Parent(string key)
            {
                int cut = key.LastIndexOf('\\');
                return cut <= 0 ? "\\" : key[..cut];
            }

            private WinFsp.FileInfo Info(Node n) => new()
            {
                FileAttributes = n.Folder ? WinFsp.AttributeDirectory : WinFsp.AttributeNormal,
                FileSize = (ulong)n.Data.Count,
                AllocationSize = ((ulong)n.Data.Count + 4095) / 4096 * 4096,
                CreationTime = n.Stamp, LastAccessTime = n.Stamp, LastWriteTime = n.Stamp, ChangeTime = n.Stamp,
                IndexNumber = n.Index,
            };

            public override int GetVolumeInfo(out ulong total, out ulong free, out string label)
            {
                total = 1UL << 40; free = 1UL << 39; label = "MemTest";
                return WinFsp.Success;
            }

            public override int GetAttributes(string path, out uint attributes)
            {
                lock (_gate)
                {
                    attributes = 0;
                    if (!_nodes.TryGetValue(path, out var n))
                        return _nodes.ContainsKey(Parent(path)) ? WinFsp.ObjectNameNotFound : WinFsp.ObjectPathNotFound;
                    attributes = n.Folder ? WinFsp.AttributeDirectory : WinFsp.AttributeNormal;
                    return WinFsp.Success;
                }
            }

            public override int Create(string path, uint createOptions, uint grantedAccess, uint attributes,
                out object? context, out WinFsp.FileInfo info, out string? normalized)
            {
                lock (_gate)
                {
                    context = null; info = default; normalized = null;
                    if (_nodes.ContainsKey(path)) return WinFsp.ObjectNameCollision;
                    if (!_nodes.TryGetValue(Parent(path), out var parent) || !parent.Folder) return WinFsp.ObjectPathNotFound;
                    var n = new Node
                    {
                        Name = path[(path.LastIndexOf('\\') + 1)..],
                        Folder = (createOptions & WinFsp.FileDirectoryFile) != 0,
                        Index = _next++,
                    };
                    _nodes[path] = n;
                    context = new Handle { Key = path };
                    info = Info(n);
                    return WinFsp.Success;
                }
            }

            public override int Open(string path, uint createOptions, uint grantedAccess,
                out object? context, out WinFsp.FileInfo info, out string? normalized)
            {
                lock (_gate)
                {
                    context = null; info = default; normalized = null;
                    if (!_nodes.TryGetValue(path, out var n)) return WinFsp.ObjectNameNotFound;
                    context = new Handle { Key = path };
                    info = Info(n);
                    return WinFsp.Success;
                }
            }

            public override int Overwrite(object context, uint attributes, bool replaceAttributes, out WinFsp.FileInfo info)
            {
                lock (_gate)
                {
                    var n = _nodes[((Handle)context).Key];
                    n.Data.Clear();
                    info = Info(n);
                    return WinFsp.Success;
                }
            }

            public override void Cleanup(object context, string? path, uint flags)
            {
                if ((flags & WinFsp.CleanupDelete) == 0) return;
                lock (_gate) _nodes.Remove(((Handle)context).Key);
            }

            public override void Close(object context)
            {
                var h = (Handle)context;
                WinFsp.DirBuffer.Delete(ref h.DirBuffer);
            }

            public override int Read(object context, IntPtr buffer, ulong offset, uint length, out uint transferred)
            {
                lock (_gate)
                {
                    var n = _nodes[((Handle)context).Key];
                    transferred = 0;
                    if (offset >= (ulong)n.Data.Count) return WinFsp.EndOfFile;
                    int count = (int)Math.Min(length, (ulong)n.Data.Count - offset);
                    var span = new Span<byte>((void*)buffer, count);
                    for (int i = 0; i < count; i++) span[i] = n.Data[(int)offset + i];
                    transferred = (uint)count;
                    return WinFsp.Success;
                }
            }

            public override int Write(object context, IntPtr buffer, ulong offset, uint length, bool toEnd,
                bool constrained, out uint transferred, out WinFsp.FileInfo info)
            {
                lock (_gate)
                {
                    var n = _nodes[((Handle)context).Key];
                    if (toEnd) offset = (ulong)n.Data.Count;
                    if (constrained)
                    {
                        if (offset >= (ulong)n.Data.Count) { transferred = 0; info = Info(n); return WinFsp.Success; }
                        length = (uint)Math.Min(length, (ulong)n.Data.Count - offset);
                    }
                    var span = new ReadOnlySpan<byte>((void*)buffer, (int)length);
                    while ((ulong)n.Data.Count < offset + length) n.Data.Add(0);
                    for (int i = 0; i < length; i++) n.Data[(int)offset + i] = span[i];
                    transferred = length;
                    info = Info(n);
                    return WinFsp.Success;
                }
            }

            public override int Flush(object? context, out WinFsp.FileInfo info)
            {
                lock (_gate) info = context == null ? default : Info(_nodes[((Handle)context).Key]);
                return WinFsp.Success;
            }

            public override int GetFileInfo(object context, out WinFsp.FileInfo info)
            {
                lock (_gate) info = Info(_nodes[((Handle)context).Key]);
                return WinFsp.Success;
            }

            public override int SetBasicInfo(object context, uint attributes, ulong created, ulong accessed,
                ulong written, ulong changed, out WinFsp.FileInfo info)
            {
                lock (_gate)
                {
                    var n = _nodes[((Handle)context).Key];
                    if (written != 0) n.Stamp = written;
                    info = Info(n);
                    return WinFsp.Success;
                }
            }

            public override int SetFileSize(object context, ulong size, bool allocation, out WinFsp.FileInfo info)
            {
                lock (_gate)
                {
                    var n = _nodes[((Handle)context).Key];
                    if (!allocation || size < (ulong)n.Data.Count)
                    {
                        while ((ulong)n.Data.Count > size) n.Data.RemoveAt(n.Data.Count - 1);
                        while ((ulong)n.Data.Count < size) n.Data.Add(0);
                    }
                    info = Info(n);
                    return WinFsp.Success;
                }
            }

            public override int CanDelete(object context, string path)
            {
                lock (_gate)
                {
                    var key = ((Handle)context).Key;
                    var n = _nodes[key];
                    if (n.Folder && _nodes.Keys.Any(k => k != key && Parent(k).Equals(key, StringComparison.OrdinalIgnoreCase)))
                        return WinFsp.DirectoryNotEmpty;
                    return WinFsp.Success;
                }
            }

            public override int Rename(object context, string path, string newPath, bool replace)
            {
                lock (_gate)
                {
                    if (_nodes.ContainsKey(newPath) && !replace) return WinFsp.ObjectNameCollision;
                    var moving = _nodes.Keys.Where(k => k.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                                                         k.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase)).ToList();
                    foreach (var k in moving)
                    {
                        var n = _nodes[k];
                        _nodes.Remove(k);
                        _nodes[newPath + k[path.Length..]] = n;
                    }
                    _nodes[newPath].Name = newPath[(newPath.LastIndexOf('\\') + 1)..];
                    ((Handle)context).Key = newPath;
                    return WinFsp.Success;
                }
            }

            public override int ReadDirectory(object context, char* pattern, char* marker, IntPtr buffer, uint length,
                out uint transferred)
            {
                var h = (Handle)context;
                uint n = 0;
                int status = WinFsp.DirBuffer.Read(ref h.DirBuffer, marker, buffer, length, &n, () =>
                {
                    lock (_gate)
                    {
                        var self = _nodes[h.Key];
                        var list = new List<(string, WinFsp.FileInfo)>();
                        if (h.Key != "\\")
                        {
                            list.Add((".", Info(self)));
                            list.Add(("..", Info(_nodes[Parent(h.Key)])));
                        }
                        foreach (var (k, node) in _nodes)
                            if (k != "\\" && k != h.Key && Parent(k).Equals(h.Key, StringComparison.OrdinalIgnoreCase))
                                list.Add((node.Name, Info(node)));
                        return list;
                    }
                });
                transferred = n;
                return status;
            }
        }
    }
}
