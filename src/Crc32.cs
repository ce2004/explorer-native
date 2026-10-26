using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Arm = System.Runtime.Intrinsics.Arm;
using X86 = System.Runtime.Intrinsics.X86;

namespace ExplorerNative
{
    /// <summary>
    /// CRC-32 (the IEEE 802.3 polynomial, reflected), which is the checksum both
    /// zip and gzip are defined in terms of.
    ///
    /// Hand-written because there is nowhere else to get it. .NET has a CRC-32 —
    /// <c>System.IO.Hashing.Crc32</c> — and it is not in the framework: it is a
    /// NuGet package, and this project takes packages only where there is no
    /// other way (see the csproj, and the two audio codecs that are the whole of
    /// that exception). Thirty lines of table lookup is not "no other way".
    ///
    /// Two implementations, and the fast one is a single ARM instruction. See
    /// <see cref="Append"/>, which explains why the hardware CRC32 on this
    /// machine is the right checksum and its near neighbour would be a disaster.
    ///
    /// The fallback is slicing-by-eight rather than the textbook byte-at-a-time
    /// loop. Every byte of every file being archived passes through here, so
    /// this sits directly on the number the whole feature is judged by, and the
    /// difference between the three is not small: measured over 256MB on this
    /// machine, 134 MB/s a byte at a time, 300 MB/s from the tables, 2.0 GB/s
    /// from the instruction.
    ///
    /// The eight tables are built once, at first use, from the polynomial rather
    /// than pasted in as eight kilobytes of hex constants that nobody could ever
    /// check.
    /// </summary>
    public static class Crc32
    {
        private const uint Polynomial = 0xEDB88320u;

        /// <summary>
        /// Eight tables of 256 entries. Table 0 is the ordinary CRC table; table
        /// <c>n</c> is table <c>n-1</c> advanced by one more byte position, which
        /// is what lets eight bytes be folded in at once.
        /// </summary>
        private static readonly uint[][] Tables = BuildTables();

        private static uint[][] BuildTables()
        {
            var tables = new uint[8][];
            for (int i = 0; i < 8; i++) tables[i] = new uint[256];

            for (uint i = 0; i < 256; i++)
            {
                uint value = i;
                for (int bit = 0; bit < 8; bit++)
                    value = (value & 1) != 0 ? (value >> 1) ^ Polynomial : value >> 1;
                tables[0][i] = value;
            }

            for (uint i = 0; i < 256; i++)
            {
                uint value = tables[0][i];
                for (int t = 1; t < 8; t++)
                {
                    value = (value >> 8) ^ tables[0][value & 0xFF];
                    tables[t][i] = value;
                }
            }

            return tables;
        }

        /// <summary>The checksum of nothing, and the value a running one starts at.</summary>
        public const uint Seed = 0;

        /// <summary>
        /// Folds <paramref name="data"/> into a running checksum.
        ///
        /// The caller keeps the running value between calls, which is what makes
        /// this usable from a loop reading a file in blocks — and from the zip
        /// writer, which needs the checksum of an entry it is streaming past
        /// rather than holding.
        /// </summary>
        public static uint Append(uint crc, ReadOnlySpan<byte> data)
        {
            // ARMv8 has this instruction, and it is this exact polynomial.
            //
            // Worth knowing because it reads like a coincidence and is not:
            // ARM defines two families, CRC32 and CRC32C, and the first is
            // 0x04C11DB7 — the one zip and gzip are defined in terms of. The
            // second is Castagnoli, which is a different checksum and would
            // silently produce archives nothing could verify. This is
            // <see cref="Arm.Crc32"/>, never <c>Arm.Crc32C</c>.
            //
            // This application is built for win-arm64 and nothing else, so on
            // every machine it actually runs on this is the path taken. The
            // tables below are not dead code all the same: the suite runs on
            // whatever it is run on, and this is a checksum, so a fallback that
            // is never exercised is not a fallback.
            //
            // Measured over 256MB on this machine: 134 MB/s a byte at a time,
            // 300 MB/s from the tables, 2.0 GB/s here — fifteen times the naive
            // loop. What makes that worth having is not the deflate, which is
            // thirty times slower than any of the three; it is the *stored*
            // entry. A folder of jpegs and video goes into a zip without being
            // compressed at all, so the only work is a checksum and a copy, and
            // at 300 MB/s the checksum was what held back a disk measured at
            // 2.3 GB/s.
            if (Arm.Crc32.Arm64.IsSupported)
            {
                uint running = ~crc;
                int at = 0;

                while (data.Length - at >= 8)
                {
                    running = Arm.Crc32.Arm64.ComputeCrc32(running,
                        BinaryPrimitives.ReadUInt64LittleEndian(data[at..]));
                    at += 8;
                }

                if (data.Length - at >= 4)
                {
                    running = Arm.Crc32.ComputeCrc32(running,
                        BinaryPrimitives.ReadUInt32LittleEndian(data[at..]));
                    at += 4;
                }

                for (; at < data.Length; at++)
                    running = Arm.Crc32.ComputeCrc32(running, data[at]);

                return ~running;
            }

            // x64 (4.2.5): carry-less multiplication folds 64 bytes a step, the
            // method of Intel's "Fast CRC Computation for Generic Polynomials
            // Using PCLMULQDQ" and of Linux's crc32-pclmul. Measured on this
            // x64 machine, the tables below gave one core ~1GB/s, and the zip
            // writer checksums every byte of a big file on its one thread
            // while eight workers deflate it. The tail under 16 bytes goes
            // through the tables.
            if (data.Length >= 64 && X86.Pclmulqdq.IsSupported && X86.Sse41.IsSupported)
            {
                int bulk = data.Length & ~15;
                crc = ~Folded(~crc, data[..bulk]);
                data = data[bulk..];
                if (data.IsEmpty) return crc;
            }

            return Table(crc, data);
        }

        /// <summary>The slicing-by-eight tables alone, whatever the machine. Also what the tests check the others against.</summary>
        internal static uint Table(uint crc, ReadOnlySpan<byte> data)
        {
            uint current = ~crc;
            var tables = Tables;

            // Eight at a time while there are eight to take. The four reads are
            // deliberate: two 32-bit loads and their table lookups have no
            // dependency on each other, so the processor can have both in flight,
            // which is where the speed over the one-byte loop actually comes from.
            int i = 0;
            while (data.Length - i >= 8)
            {
                uint low = (uint)(data[i] | (data[i + 1] << 8) | (data[i + 2] << 16) | (data[i + 3] << 24)) ^ current;
                uint high = (uint)(data[i + 4] | (data[i + 5] << 8) | (data[i + 6] << 16) | (data[i + 7] << 24));

                current =
                    tables[7][low & 0xFF] ^
                    tables[6][(low >> 8) & 0xFF] ^
                    tables[5][(low >> 16) & 0xFF] ^
                    tables[4][low >> 24] ^
                    tables[3][high & 0xFF] ^
                    tables[2][(high >> 8) & 0xFF] ^
                    tables[1][(high >> 16) & 0xFF] ^
                    tables[0][high >> 24];

                i += 8;
            }

            for (; i < data.Length; i++)
                current = (current >> 8) ^ tables[0][(current ^ data[i]) & 0xFF];

            return ~current;
        }

        // The folding constants for the reflected IEEE polynomial: x^(4*128+32),
        // x^(4*128-32), x^(128+32), x^(128-32), x^64 mod P, and Barrett's
        // mu and P itself, as Linux's crc32-pclmul_asm.S has them.

        /// <summary>
        /// The running value (not inverted on the way in or out) over
        /// <paramref name="data"/>, at least 64 bytes and a multiple of 16.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        internal static uint Folded(uint running, ReadOnlySpan<byte> data)
        {
            // Built here rather than kept in static fields, so the JIT can hold
            // them in registers as constants.
            var K1K2 = Vector128.Create(0x154442bd4UL, 0x1c6e41596UL);
            var K3K4 = Vector128.Create(0x1751997d0UL, 0x0ccaa009eUL);
            var K5 = Vector128.Create(0x163cd6124UL, 0UL);
            var PolyMu = Vector128.Create(0x1db710641UL, 0x1f7011641UL);
            var Low32 = Vector128.Create(0xFFFFFFFFUL, 0UL);

            ref byte at = ref MemoryMarshal.GetReference(data);
            nuint length = (nuint)data.Length;

            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            static Vector128<ulong> Load(ref byte from, nuint offset) =>
                Vector128.LoadUnsafe(ref from, offset).AsUInt64();

            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            static Vector128<ulong> Fold(Vector128<ulong> x, Vector128<ulong> k) =>
                X86.Pclmulqdq.CarrylessMultiply(x, k, 0x00) ^ X86.Pclmulqdq.CarrylessMultiply(x, k, 0x11);

            var x1 = Load(ref at, 0) ^ Vector128.CreateScalar((ulong)running);
            var x2 = Load(ref at, 16);
            var x3 = Load(ref at, 32);
            var x4 = Load(ref at, 48);
            nuint offset = 64;

            // Four lanes of 16 bytes, each folded 64 bytes forward a step.
            while (length - offset >= 64)
            {
                x1 = Fold(x1, K1K2) ^ Load(ref at, offset);
                x2 = Fold(x2, K1K2) ^ Load(ref at, offset + 16);
                x3 = Fold(x3, K1K2) ^ Load(ref at, offset + 32);
                x4 = Fold(x4, K1K2) ^ Load(ref at, offset + 48);
                offset += 64;
            }

            // The four into one, then what is left 16 bytes at a time.
            x1 = Fold(x1, K3K4) ^ x2;
            x1 = Fold(x1, K3K4) ^ x3;
            x1 = Fold(x1, K3K4) ^ x4;
            while (length - offset >= 16)
            {
                x1 = Fold(x1, K3K4) ^ Load(ref at, offset);
                offset += 16;
            }

            // 128 bits to 64, adding the 32 zero bits the CRC is defined with.
            x1 = X86.Sse2.ShiftRightLogical128BitLane(x1, 8) ^ X86.Pclmulqdq.CarrylessMultiply(K3K4, x1, 0x01);

            // 64 to 32.
            var high = X86.Sse2.ShiftRightLogical128BitLane(x1, 4);
            x1 = X86.Pclmulqdq.CarrylessMultiply(x1 & Low32, K5, 0x00) ^ high;

            // Barrett reduction to the remainder.
            var keep = x1;
            x1 = X86.Pclmulqdq.CarrylessMultiply(x1 & Low32, PolyMu, 0x10);
            x1 = X86.Pclmulqdq.CarrylessMultiply(x1 & Low32, PolyMu, 0x00) ^ keep;
            return X86.Sse41.Extract(x1.AsUInt32(), 1);
        }
    }
}
