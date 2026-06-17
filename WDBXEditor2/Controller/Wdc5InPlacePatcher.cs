using DBCD;
using DBCD.IO;
using DBCD.IO.Attributes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace WDBXEditor2.Controller
{
    internal static class Wdc5InPlacePatcher
    {
        private const int HeaderSize = 204;
        private const int SectionHeaderSize = 40;
        private const int ColumnMetaSize = 24;

        public static Wdc5PatchSaveResult Save(IDBCDStorage storage, string sourceFile, string targetFile)
        {
            byte[] data = File.ReadAllBytes(sourceFile);
            var layout = Wdc5Layout.Parse(data);
            layout.ValidateForInPlacePatch(storage);

            var patched = (byte[])data.Clone();
            int changedRows = 0;
            int editableRows = 0;

            foreach (var row in storage.Values)
            {
                if (!layout.TryGetRecordOffset(row.ID, out int recordOffset))
                    continue;

                if (WriteRow(patched, recordOffset, row, layout))
                    changedRows++;

                editableRows++;
            }

            if (editableRows == 0)
                throw new InvalidOperationException("未在未加密 section 中找到可编辑的 WDC5 行。");

            var patchedLayout = Wdc5Layout.Parse(patched);
            ValidateSavedPatch(layout, patchedLayout, data.Length, patched.Length);
            string backupFile = SafeReplace(targetFile, patched);

            return new Wdc5PatchSaveResult
            {
                ChangedRows = changedRows,
                EditableRows = editableRows,
                RecordsCount = patchedLayout.RecordsCount,
                SectionsCount = patchedLayout.SectionsCount,
                FileSize = patched.Length,
                BackupFile = backupFile
            };
        }

        private static bool WriteRow(byte[] data, int recordOffset, DBCDRow row, Wdc5Layout layout)
        {
            var originalRecordBits = new byte[layout.RecordSize];
            Buffer.BlockCopy(data, recordOffset, originalRecordBits, 0, layout.RecordSize);

            var recordBits = new byte[layout.RecordSize];
            Buffer.BlockCopy(data, recordOffset, recordBits, 0, layout.RecordSize);

            for (int i = 0; i < layout.Columns.Count; i++)
            {
                var column = layout.Columns[i];
                object value = row[column.Name];
                uint rawValue = ToRawUInt32(value);

                if (!column.PalletIndexByValue.TryGetValue(rawValue, out uint palletIndex))
                    throw new InvalidOperationException(
                        $"第 {row.ID} 行字段 {column.Name} 的值 {rawValue} 不存在于原始 WDC5 pallet 数据中。"
                    );

                WriteBits(recordBits, column.BitOffset, column.BitWidth, palletIndex);
            }

            bool changed = !originalRecordBits.SequenceEqual(recordBits);
            Buffer.BlockCopy(recordBits, 0, data, recordOffset, layout.RecordSize);
            return changed;
        }

        private static void ValidateSavedPatch(Wdc5Layout original, Wdc5Layout saved, long originalFileSize, long savedFileSize)
        {
            if (savedFileSize != originalFileSize)
                throw new InvalidDataException($"安全 patch 后文件大小发生变化：{originalFileSize} -> {savedFileSize}。");

            if (saved.RecordsCount != original.RecordsCount)
                throw new InvalidDataException($"安全 patch 后记录数发生变化：{original.RecordsCount} -> {saved.RecordsCount}。");

            if (saved.SectionsCount != original.SectionsCount)
                throw new InvalidDataException($"安全 patch 后 section 数发生变化：{original.SectionsCount} -> {saved.SectionsCount}。");

            if (saved.LayoutHash != original.LayoutHash)
                throw new InvalidDataException($"安全 patch 后 layout hash 发生变化：0x{original.LayoutHash:X8} -> 0x{saved.LayoutHash:X8}。");
        }

        private static uint ToRawUInt32(object value)
        {
            return value switch
            {
                byte typedValue => typedValue,
                sbyte typedValue => unchecked((uint)typedValue),
                short typedValue => unchecked((uint)typedValue),
                ushort typedValue => typedValue,
                int typedValue => unchecked((uint)typedValue),
                uint typedValue => typedValue,
                long typedValue => unchecked((uint)typedValue),
                ulong typedValue => unchecked((uint)typedValue),
                _ => Convert.ToUInt32(value)
            };
        }

        private static string SafeReplace(string targetFile, byte[] data)
        {
            string targetDirectory = Path.GetDirectoryName(targetFile);
            if (string.IsNullOrEmpty(targetDirectory))
                targetDirectory = Directory.GetCurrentDirectory();

            Directory.CreateDirectory(targetDirectory);

            string tempFile = Path.Combine(
                targetDirectory,
                $".{Path.GetFileName(targetFile)}.{Guid.NewGuid():N}.tmp"
            );

            string backupFile = null;

            try
            {
                File.WriteAllBytes(tempFile, data);

                if (File.Exists(targetFile))
                {
                    backupFile = $"{targetFile}.{DateTime.Now:yyyyMMddHHmmss}.bak";
                    File.Copy(targetFile, backupFile, overwrite: false);
                }

                File.Copy(tempFile, targetFile, overwrite: true);
                return backupFile;
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
            public int RecordsCount { get; private set; }
            public int SectionsCount { get; private set; }
            public uint LayoutHash { get; private set; }
            public int RecordSize { get; private set; }
            public int IdFieldIndex { get; private set; }
            public DB2Flags Flags { get; private set; }
            public int EditableRecordCount => editableRecordOffsets.Count;
            public List<Wdc5Column> Columns { get; } = new List<Wdc5Column>();

            private readonly Dictionary<int, int> editableRecordOffsets = new Dictionary<int, int>();

            public static Wdc5Layout Parse(byte[] data)
            {
                using var stream = new MemoryStream(data, writable: false);
                using var reader = new BinaryReader(stream);

                string magic = new string(reader.ReadChars(4));
                if (magic != "WDC5")
                    throw new InvalidDataException($"需要 WDC5 文件，但实际是 {magic}。");

                if (data.Length < HeaderSize)
                    throw new InvalidDataException("WDC5 文件长度小于 header。");

                stream.Position = 136;
                int recordsCount = reader.ReadInt32();
                int fieldsCount = reader.ReadInt32();
                int recordSize = reader.ReadInt32();
                int stringTableSize = reader.ReadInt32();
                reader.ReadUInt32(); // table hash
                uint layoutHash = reader.ReadUInt32();
                reader.ReadInt32(); // min index
                reader.ReadInt32(); // max index
                reader.ReadInt32(); // locale
                var flags = (DB2Flags)reader.ReadUInt16();
                int idFieldIndex = reader.ReadUInt16();
                int totalFieldCount = reader.ReadInt32();
                reader.ReadInt32(); // packed data offset
                int lookupColumnCount = reader.ReadInt32();
                int columnMetaDataSize = reader.ReadInt32();
                int commonDataSize = reader.ReadInt32();
                int palletDataSize = reader.ReadInt32();
                int sectionsCount = reader.ReadInt32();

                if (recordsCount <= 0 || fieldsCount <= 0 || totalFieldCount != fieldsCount)
                    throw new InvalidDataException("不支持的 WDC5 header。");

                if (flags != DB2Flags.Index)
                    throw new InvalidDataException($"安全 patch 目前只支持 indexed non-sparse WDC5 文件。Flags: {flags}。");

                if (lookupColumnCount != 1 || commonDataSize != 0 || columnMetaDataSize != fieldsCount * ColumnMetaSize)
                    throw new InvalidDataException("不支持的 WDC5 lookup 或 column metadata 布局。");

                if (sectionsCount <= 0)
                    throw new InvalidDataException("WDC5 文件没有 section。");

                var sections = new List<Wdc5Section>();
                for (int i = 0; i < sectionsCount; i++)
                {
                    EnsureAvailable(data, stream.Position, SectionHeaderSize, "WDC5 section headers");

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
                    EnsureAvailable(data, stream.Position, ColumnMetaSize, "WDC5 column metadata");

                    ushort recordOffset = reader.ReadUInt16();
                    ushort size = reader.ReadUInt16();
                    uint additionalDataSize = reader.ReadUInt32();
                    uint compressionType = reader.ReadUInt32();
                    int bitOffset = reader.ReadInt32();
                    int bitWidth = reader.ReadInt32();
                    reader.ReadInt32(); // cardinality

                    if (compressionType != 3)
                        throw new InvalidDataException("安全 patch 目前只支持 pallet 压缩的 WDC5 字段。");

                    if (recordOffset != bitOffset || size != bitWidth)
                        throw new InvalidDataException("不支持的 WDC5 pallet bit 布局。");

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
                    if (column.AdditionalDataSize % 4 != 0)
                        throw new InvalidDataException("WDC5 pallet 数据大小异常。");

                    EnsureAvailable(data, stream.Position, column.AdditionalDataSize, "WDC5 pallet data");

                    for (uint i = 0; i < valueCount; i++)
                    {
                        uint value = reader.ReadUInt32();
                        column.PalletIndexByValue[value] = i;
                    }
                }

                if (stream.Position != HeaderSize + sectionsCount * SectionHeaderSize + fieldsCount * 4 + columnMetaDataSize + palletDataSize)
                    throw new InvalidDataException("WDC5 metadata 大小异常。");

                var layout = new Wdc5Layout
                {
                    RecordsCount = recordsCount,
                    SectionsCount = sectionsCount,
                    LayoutHash = layoutHash,
                    RecordSize = recordSize,
                    IdFieldIndex = idFieldIndex,
                    Flags = flags
                };

                layout.Columns.AddRange(columns);

                if (!sections.Any(section => section.TactKeyLookup == 0))
                    throw new InvalidDataException("没有可用于安全 patch 的未加密 WDC5 section。");

                foreach (var section in sections.Where(section => section.TactKeyLookup == 0))
                    layout.ReadEditableSection(data, section, stringTableSize);

                return layout;
            }

            private static void EnsureAvailable(byte[] data, long offset, int size, string description)
            {
                if (offset < 0 || size < 0 || offset + size > data.Length)
                    throw new InvalidDataException($"读取 {description} 时遇到异常的文件结尾。");
            }

            public void ValidateForInPlacePatch(IDBCDStorage storage)
            {
                if (storage.FormatIdentifier != "WDC5")
                    throw new InvalidOperationException($"需要 WDC5 storage，但实际是 {storage.FormatIdentifier}。");

                var recordFields = GetInlineRecordFields(storage).ToList();
                if (recordFields.Count != Columns.Count)
                    throw new InvalidOperationException(
                        $"DBD inline 字段数与 WDC5 column 数不匹配。Inline fields: {recordFields.Count}, WDC5 columns: {Columns.Count}, IdFieldIndex: {IdFieldIndex}, Flags: {Flags}。"
                    );

                for (int i = 0; i < Columns.Count; i++)
                    Columns[i].Name = recordFields[i].Name;
            }

            private static IEnumerable<FieldInfo> GetInlineRecordFields(IDBCDStorage storage)
            {
                Type recordType = storage.Values.FirstOrDefault()?.GetUnderlyingType();
                if (recordType == null)
                    throw new InvalidOperationException("WDC5 安全保存至少需要加载一行数据。");

                var fieldsByName = recordType
                    .GetFields(BindingFlags.Instance | BindingFlags.Public)
                    .ToDictionary(field => field.Name, StringComparer.Ordinal);

                foreach (string columnName in storage.AvailableColumns)
                {
                    if (!fieldsByName.TryGetValue(columnName, out FieldInfo field))
                        throw new InvalidOperationException($"无法把 DBD 字段 '{columnName}' 映射到生成的 storage 类型。");

                    if (IsNonInline(field))
                        continue;

                    yield return field;
                }
            }

            private static bool IsNonInline(FieldInfo field)
            {
                var index = field.GetCustomAttribute<IndexAttribute>();
                if (index != null && index.NonInline)
                    return true;

                var relation = field.GetCustomAttribute<RelationAttribute>();
                return relation != null && relation.IsNonInline;
            }

            public bool TryGetRecordOffset(int id, out int recordOffset) => editableRecordOffsets.TryGetValue(id, out recordOffset);

            private void ReadEditableSection(byte[] data, Wdc5Section section, int totalStringTableSize)
            {
                if (section.OffsetRecordsEndOffset != 0 || section.OffsetMapIdCount != 0 || section.CopyTableCount != 0)
                    throw new InvalidDataException("安全 patch 目前不支持 sparse 或 copied WDC5 行。");

                if (section.StringTableSize != totalStringTableSize)
                    throw new InvalidDataException("安全 patch 目前不支持 multi-section string table。");

                if (section.IndexDataSize != section.NumRecords * 4)
                    throw new InvalidDataException("WDC5 index 数据大小异常。");

                int indexOffset = section.FileOffset + section.NumRecords * RecordSize + section.StringTableSize;
                int recordDataSize = section.NumRecords * RecordSize;
                EnsureAvailable(data, section.FileOffset, recordDataSize, "WDC5 record data");
                EnsureAvailable(data, indexOffset, section.IndexDataSize, "WDC5 index data");

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

    internal sealed class Wdc5PatchSaveResult
    {
        public int ChangedRows { get; set; }
        public int EditableRows { get; set; }
        public int RecordsCount { get; set; }
        public int SectionsCount { get; set; }
        public long FileSize { get; set; }
        public string BackupFile { get; set; }
    }
}
