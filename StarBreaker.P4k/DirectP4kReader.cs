﻿using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DotNext.IO;
using ZstdSharp;

namespace StarBreaker.P4k;

public sealed class DirectP4kReader
{
    private readonly string p4kPath;
    private readonly string _comment;
    private readonly MyZipEntry[] _entries;

    public DirectP4kReader(string filePath)
    {
        p4kPath = filePath;
        using var _stream = new FileStream(p4kPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 16);
        using var reader = new BinaryReader(_stream, Encoding.UTF8, true);

        var eocdLocation = reader.Locate(EOCDRecord.Magic);
        reader.BaseStream.Seek(eocdLocation, SeekOrigin.Begin);
        var eocd = reader.ReadStruct<EOCDRecord>();
        _comment = reader.ReadStringCustom(eocd.CommentLength);

        if (!eocd.IsZip64)
            throw new Exception("Not a zip64 archive");

        var bytesFromEnd = _stream.Length - eocdLocation;
        var zip64LocatorLocation = reader.Locate(Zip64Locator.Magic, bytesFromEnd);
        reader.BaseStream.Seek(zip64LocatorLocation, SeekOrigin.Begin);
        var zip64Locator = reader.ReadStruct<Zip64Locator>();

        reader.BaseStream.Seek((long)zip64Locator.Zip64EOCDOffset, SeekOrigin.Begin);

        var eocd64 = reader.ReadStruct<EOCD64Record>();
        if (eocd64.Signature != BitConverter.ToUInt32(EOCD64Record.Magic))
            throw new Exception("Invalid zip64 end of central directory locator");

        _entries = new MyZipEntry[eocd64.EntriesOnDisk];

        reader.BaseStream.Seek((long)eocd64.CentralDirectoryOffset, SeekOrigin.Begin);

        for (var i = 0; i < (int)eocd64.TotalEntries; i++)
        {
            var header = reader.ReadStruct<CentralDirectoryFileHeader>();
            var fileName = reader.ReadStringCustom(header.FileNameLength);
            ulong compressedSize = header.CompressedSize;
            ulong uncompressedSize = header.UncompressedSize;
            ulong localHeaderOffset = header.LocalFileHeaderOffset;
            ulong diskNumberStart = header.DiskNumberStart;

            var beforeExtraField = reader.BaseStream.Position;

            if (reader.ReadUInt16() != 1)
                throw new Exception();

            var zip64HeaderSize = reader.ReadUInt16();

            if (uncompressedSize == uint.MaxValue)
                uncompressedSize = reader.ReadUInt64();

            if (compressedSize == uint.MaxValue)
                compressedSize = reader.ReadUInt64();

            if (localHeaderOffset == uint.MaxValue)
                localHeaderOffset = reader.ReadUInt64();

            if (diskNumberStart == ushort.MaxValue)
                diskNumberStart = reader.ReadUInt32();

            if (reader.ReadUInt16() != 0x5000)
                throw new Exception("Invalid extra field id");
            var extra0x5000Size = reader.ReadUInt16();
            reader.BaseStream.Seek(extra0x5000Size - 4, SeekOrigin.Current);

            if (reader.ReadUInt16() != 0x5002)
                throw new Exception("Invalid extra field id");
            if (reader.ReadUInt16() != 6)
                throw new Exception("Invalid extra field size");
            var isCrypted = reader.ReadUInt16() == 1;

            if (reader.ReadUInt16() != 0x5003)
                throw new Exception("Invalid extra field id");
            var extra0x5003Size = reader.ReadUInt16();
            reader.BaseStream.Seek(extra0x5003Size - 4, SeekOrigin.Current);

            var size = reader.BaseStream.Position - beforeExtraField;
            if (size != header.ExtraFieldLength)
                throw new Exception("Extra field size mismatch");

            var fileComment = reader.ReadStringCustom(header.FileCommentLength);

            _entries[i] = new MyZipEntry(fileName, fileComment, compressedSize, uncompressedSize, header.CompressionMethod, isCrypted, localHeaderOffset);
        }

        var cryptedSizes = _entries.Where(x => x.IsCrypted).Select(x => x.CompressedSize).Order().ToArray();

        Console.WriteLine($"Crypted entries: {cryptedSizes.Length}");
    }

    public void Extract(string outputDir, IProgress<double>? progress = null)
    {
        var numberOfEntries = _entries.Length;
        var fivePercent = numberOfEntries / 20;
        var processedEntries = 0;

        Parallel.ForEach(_entries,
            entry =>
            {
                using var p4kStream = new FileStream(p4kPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 16);
                using var p4kReader = new BinaryReader(p4kStream, Encoding.UTF8, true);

                p4kStream.Seek((long)entry.Offset, SeekOrigin.Begin);
                var asd = p4kReader.ReadUInt32();
                if (asd != 0x14034B50 && asd != 0x04034B50) //CIG-specific local file header
                    throw new Exception("Invalid local file header");

                var header = p4kReader.ReadStruct<LocalFileHeader>();
                //var name2 = reader.ReadStringCustom(header.FileNameLength);
                //var extraField = reader.ReadBytes(header.ExtraFieldLength);
                p4kStream.Seek(header.FileNameLength + header.ExtraFieldLength, SeekOrigin.Current);

                var entryPath = Path.Combine(outputDir, entry.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(entryPath) ?? throw new InvalidOperationException());

                var segment = new StreamSegment(p4kStream, true);
                segment.Adjust(p4kStream.Position, (long)entry.CompressedSize);

                Stream entryStream = segment;

                if (entry.IsCrypted)
                {
                    using var aes = Aes.Create();
                    aes.Key =
                    [
                        0x5E, 0x7A, 0x20, 0x02,
                        0x30, 0x2E, 0xEB, 0x1A,
                        0x3B, 0xB6, 0x17, 0xC3,
                        0x0F, 0xDE, 0x1E, 0x47
                    ];
                    aes.IV = new byte[16];
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.None;

                    var cipher = aes.CreateDecryptor();

                    var crypto = new CryptoStream(entryStream, cipher, CryptoStreamMode.Read);

                    var buffer = new MemoryStream();
                    crypto.CopyTo(buffer);

                    // Trim NULL off end of stream
                    buffer.Seek(-1, SeekOrigin.End);
                    while (buffer.Position > 1 && buffer.ReadByte() == 0) buffer.Seek(-2, SeekOrigin.Current);
                    buffer.SetLength(buffer.Position);

                    buffer.Seek(0, SeekOrigin.Begin);

                    entryStream = buffer;
                }

                if (entry.CompressionMethod == 0) //stored
                {
                    if (entry.CompressedSize != entry.UncompressedSize)
                        throw new Exception("Invalid stored file");

                    using var writeStream = new FileStream(entryPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 16);

                    entryStream.CopyTo(writeStream);
                }

                if (entry.CompressionMethod == 100) //zstd
                {
                    using var decompressor = new DecompressionStream(entryStream);
                    using var writeStream = new FileStream(entryPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 16);

                    decompressor.CopyTo(writeStream);
                }

                entryStream.Dispose();

                lock (_entries)
                {
                    processedEntries++;
                    if (processedEntries == numberOfEntries || processedEntries % fivePercent == 0)
                        progress?.Report(processedEntries / (double)numberOfEntries);
                }
            });
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct LocalFileHeader
{
    public readonly ushort VersionNeededToExtract;
    public readonly ushort GeneralPurposeBitFlag;
    public readonly ushort CompressionMethod;
    public readonly ushort LastModFileTime;
    public readonly ushort LastModFileDate;
    public readonly uint Crc32;
    public readonly uint CompressedSize;
    public readonly uint UncompressedSize;
    public readonly ushort FileNameLength;
    public readonly ushort ExtraFieldLength;
}