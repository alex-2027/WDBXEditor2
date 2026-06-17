using DBCD;
using DBCD.IO;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WDBXEditor2.Controller
{
    internal static class Wdc5InPlacePatcher
    {
        private const int HeaderSize = 204;
        private const int SectionHeaderSize = 40;
        private const int ColumnMetaSize = 24;

        public static void Save(IDBCDStorage storage, string sourceFile, string targetFile)
        {
            byte[] data = File.ReadAllBytes(sourceFile);
            var layout = Wdc5Layout.Parse(data);
            layout.ValidateForInPlacePatch(storage);

            var patched = (byte[])data.Clone();
            int patchedRows = 0;

            foreach (var row in storage.Values)
            {
                if (!layout.TryGetRecordOffset(row.ID, out int recordOffset))
                    continue;

                WriteRow(patched, recordOffset, row, layout);
                patchedRows++;
            }

            if (patchedRows == 0)
                throw new InvalidOperationException("No editable WDC5 rows were found in the unencrypted section.");

            SafeReplace(targetFile, patched);
        }

        private static void WriteRow(byte[] data, int recordOffset, DBCDRow row, Wdc5Layout layout)
        {
            var recordBits = new byte[layout.RecordSize];

            for (int i = 0; i < layout.Columns.Count; i++)
            {
                var column = layout.Columns[i];
                object value = row[column.Name];
                uint rawValue = Convert.ToUInt32(value);

                if (!column.PalletIndexByValue.TryGetValue(rawValue, out uint palletIndex))
                    throw new InvalidOperationException(
                        $"Row {row.ID} column {column.Name} value {rawValue} is not present in the original WDC5 pallet data."
                    );

                WriteBits(recordBits, column.BitOffset, column.BitWidth, palletIndex);
            }

            Buffer.BlockCopy(recordBits, 0, data, recordOffset, layout.RecordSize);
        }

        private static void SafeReplace(string targetFile, byte[] data)
        {
            string targetDirectory = Path.GetDirectoryName(targetFile);
            if (string.IsNullOrEmpty(targetDirectory))
                targetDirectory = Directory.GetCurrentDirectory();

            Directory.CreateDirectory(targetDirectory);

            string tempFile = Path.Combine(
                targetDirectory,
                $".{Path.GetFileName(targetFile)}.{Guid.NewGuid():N}.tmp"
            );

            try
            {
                File.WriteAllBytes(tempFile, data);

                if (File.Exists(targetFile))
                {
                    string backupFile = $"{targetFile}.{DateTime.Now:yyyyMMddHHmmss}.bak";
                    File.Copy(targetFile, backupFile, overwrite: false);
                }

                File.Copy(tempFile, targetFile, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
        }

        private static void WriteBits(byte[] buffer, int bitOffset, int bitWidth, uint value)
        {
            for (int i = 0; i < bitWidth; i++)
            {
                int absoluteBit = bitOffset + i;
                int byteIndex = absoluteBit / 8;
                int bitIndex = absoluteBit % 8;

                if (((value >> i) & 1) == 1)
                    buffer[byteIndex] = (byte)(buffer[byteIndex] | (1 << bitIndex));
                else
                    buffer[byteIndex] = (byte)(buffer[byteIndex] & ~(1 << bitIndex));
            }
        }

        private sealed class Wdc5Layout
        {
            public int RecordSize { get; private set; }
            public int EditableRecordCount => editableRecordOffsets.Count;
            public List<Wdc5Column> Columns { get; } = new List<Wdc5Column>();

            private readonly Dictionary<int, int> editableRecordOffsets = new Dictionary<int, int>();

            public static Wdc5Layout Parse(byte[] data)
            {
                using var stream = new MemoryStream(data, writable: false);
                using var reader = new BinaryReader(stream);

                string magic = new string(reader.ReadChars(4));
                if (magic != "WDC5")
                    throw new InvalidDataException($"Expected WDC5, got {magic}.");

                stream.Position = 136;
                int recordsCount = reader.ReadInt32();
                int fieldsCount = reader.ReadInt32();
                int recordSize = reader.ReadInt32();
                int stringTableSize = reader.ReadInt32();
                reader.ReadUInt32(); // table hash
                reader.ReadUInt32(); // layout hash
                reader.ReadInt32(); // min index
                reader.ReadInt32(); // max index
                reader.ReadInt32(); // locale
                var flags = (DB2Flags)reader.ReadUInt16();
                reader.ReadUInt16(); // id field index
                int totalFieldCount = reader.ReadInt32();
                reader.ReadInt32(); // packed data offset
                int lookupColumnCount = reader.ReadInt32();
                int columnMetaDataSize = reader.ReadInt32();
                int commonDataSize = reader.ReadInt32();
                int palletDataSize = reader.ReadInt32();
                int sectionsCount = reader.ReadInt32();

                if (recordsCount <= 0 || fieldsCount <= 0 || totalFieldCount != fieldsCount)
                    throw new InvalidDataException("Unsupported WDC5 header.");

                if (flags != DB2Flags.Index)
                    throw new InvalidDataException($"Only indexed non-sparse WDC5 files are supported for safe patching. Flags: {flags}.");

                if (lookupColumnCount != 1 || commonDataSize != 0 || columnMetaDataSize != fieldsCount * ColumnMetaSize)
                    throw new InvalidDataException("Unsupported WDC5 lookup or column metadata layout.");

                var sections = new List<Wdc5Section>();
                for (int i = 0; i < sectionsCount; i++)
                {
                    sections.Add(new Wdc5Section
                    {
                        TactKeyLookup = reader.ReadUInt64(),
                        FileOffset = reader.ReadInt32(),
                        NumRecords = reader.ReadInt32(),
                        StringTableSize = reader.ReadInt32(),
                        OffsetRecordsEndOffset = reader.ReadInt32(),
                        IndexDataSize = reader.ReadInt32(),
                        ParentLookupDataSize = reader.ReadInt32(),
                        OffsetMapIdCount = reader.ReadInt32(),
                        CopyTableCount = reader.ReadInt32()
                    });
                }

                long fieldMetaOffset = stream.Position;
                stream.Position = fieldMetaOffset + fieldsCount * 4;

                var columns = new List<Wdc5Column>();
                for (int i = 0; i < fieldsCount; i++)
                {
                    ushort recordOffset = reader.ReadUInt16();
                    ushort size = reader.ReadUInt16();
                    uint additionalDataSize = reader.ReadUInt32();
                    uint compressionType = reader.ReadUInt32();
                    int bitOffset = reader.ReadInt32();
                    int bitWidth = reader.ReadInt32();
                    reader.ReadInt32(); // cardinality

                    if (compressionType != 3)
                        throw new InvalidDataException("Only pallet-compressed WDC5 columns are supported for safe patching.");

                    if (recordOffset != bitOffset || size != bitWidth)
                        throw new InvalidDataException("Unsupported WDC5 pallet bit layout.");

                    columns.Add(new Wdc5Column
                    {
                        Name = string.Empty,
                        BitOffset = bitOffset,
                        BitWidth = bitWidth,
                        AdditionalDataSize = checked((int)additionalDataSize)
                    });
                }

                foreach (var column in columns)
                {
                    int valueCount = column.AdditionalDataSize / 4;
                    for (uint i = 0; i < valueCount; i++)
                    {
                        uint value = reader.ReadUInt32();
                        column.PalletIndexByValue[value] = i;
                    }
                }

                if (stream.Position != HeaderSize + sectionsCount * SectionHeaderSize + fieldsCount * 4 + columnMetaDataSize + palletDataSize)
                    throw new InvalidDataException("Unexpected WDC5 metadata size.");

                var layout = new Wdc5Layout
                {
                    RecordSize = recordSize
                };

                layout.Columns.AddRange(columns);

                var editableSection = sections.SingleOrDefault(section => section.TactKeyLookup == 0);
                if (editableSection == null)
                    throw new InvalidDataException("No unencrypted WDC5 section is available for safe patching.");

                foreach (var section in sections.Where(section => section.TactKeyLookup == 0))
                    layout.ReadEditableSection(data, section, stringTableSize);

                return layout;
            }

            public void ValidateForInPlacePatch(IDBCDStorage storage)
            {
                if (storage.FormatIdentifier != "WDC5")
                    throw new InvalidOperationException($"Expected WDC5 storage, got {storage.FormatIdentifier}.");

                var columnNames = storage.AvailableColumns;
                if (columnNames.Length != Columns.Count)
                    throw new InvalidOperationException("DBD field count does not match WDC5 column count.");

                for (int i = 0; i < Columns.Count; i++)
                    Columns[i].Name = columnNames[i];
            }

            public bool TryGetRecordOffset(int id, out int recordOffset) => editableRecordOffsets.TryGetValue(id, out recordOffset);

            private void ReadEditableSection(byte[] data, Wdc5Section section, int totalStringTableSize)
            {
                if (section.OffsetRecordsEndOffset != 0 || section.OffsetMapIdCount != 0 || section.CopyTableCount != 0)
                    throw new InvalidDataException("Sparse or copied WDC5 rows are not supported for safe patching.");

                if (section.StringTableSize != totalStringTableSize)
                    throw new InvalidDataException("Multi-section string tables are not supported for safe patching.");

                if (section.IndexDataSize != section.NumRecords * 4)
                    throw new InvalidDataException("Unexpected WDC5 index data size.");

                int indexOffset = section.FileOffset + section.NumRecords * RecordSize + section.StringTableSize;

                for (int i = 0; i < section.NumRecords; i++)
                {
                    int id = BitConverter.ToInt32(data, indexOffset + i * 4);
                    int recordOffset = section.FileOffset + i * RecordSize;
                    editableRecordOffsets[id] = recordOffset;
                }
            }
        }

        private sealed class Wdc5Column
        {
            public string Name { get; set; }
            public int BitOffset { get; set; }
            public int BitWidth { get; set; }
            public int AdditionalDataSize { get; set; }
            public Dictionary<uint, uint> PalletIndexByValue { get; } = new Dictionary<uint, uint>();
        }

        private sealed class Wdc5Section
        {
            public ulong TactKeyLookup { get; set; }
            public int FileOffset { get; set; }
            public int NumRecords { get; set; }
            public int StringTableSize { get; set; }
            public int OffsetRecordsEndOffset { get; set; }
            public int IndexDataSize { get; set; }
            public int ParentLookupDataSize { get; set; }
            public int OffsetMapIdCount { get; set; }
            public int CopyTableCount { get; set; }
        }
    }
}
