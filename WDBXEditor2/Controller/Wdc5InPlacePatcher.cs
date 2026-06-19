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
    public static class Wdc5InPlacePatcher
    {
        private const int HeaderSize = 204;
        private const int SectionHeaderSize = 40;
        private const int FieldMetaSize = 4;
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
                WriteColumnValue(data, recordBits, row.ID, column, value);
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
                float typedValue => BitConverter.ToUInt32(BitConverter.GetBytes(typedValue), 0),
                _ => Convert.ToUInt32(value)
            };
        }

        private static void WriteColumnValue(byte[] fileData, byte[] recordBits, int rowId, Wdc5Column column, object value)
        {
            if (column.IsStringLike)
            {
                throw new InvalidOperationException(
                    $"字段 {column.Name} 是字符串列，当前 WDC5 安全 patch 暂不支持修改字符串字段。"
                );
            }

            if (value is Array arrayValue)
            {
                WriteArrayColumnValue(recordBits, rowId, column, arrayValue);
                return;
            }

            uint rawValue = ToRawUInt32(value);

            switch (column.CompressionType)
            {
                case Wdc5CompressionType.None:
                case Wdc5CompressionType.Immediate:
                case Wdc5CompressionType.SignedImmediate:
                    WriteBits(recordBits, column.BitOffset, column.BitWidth, rawValue);
                    break;
                case Wdc5CompressionType.Common:
                    WriteCommonValue(fileData, rowId, column, rawValue);
                    break;
                case Wdc5CompressionType.Pallet:
                    if (!column.PalletIndexByValue.TryGetValue(rawValue, out uint palletIndex))
                    {
                        throw new InvalidOperationException(
                            $"第 {rowId} 行字段 {column.Name} 的值 {rawValue} 不存在于原始 WDC5 pallet 数据中。当前安全 patch 只能写回原文件 palette 中已经存在的值，不能新增 palette 项。"
                        );
                    }

                    WriteBits(recordBits, column.BitOffset, column.BitWidth, palletIndex);
                    break;
                case Wdc5CompressionType.PalletArray:
                    if (column.Cardinality != 1)
                        throw new InvalidOperationException($"字段 {column.Name} 使用了 cardinality={column.Cardinality} 的 pallet-array，当前安全 patch 尚不支持。");

                    if (!column.PalletIndexByValue.TryGetValue(rawValue, out uint palletArrayIndex))
                    {
                        throw new InvalidOperationException(
                            $"第 {rowId} 行字段 {column.Name} 的值 {rawValue} 不存在于原始 WDC5 pallet-array 数据中。当前安全 patch 只能写回原文件 palette 中已经存在的值，不能新增 palette 项。"
                        );
                    }

                    WriteBits(recordBits, column.BitOffset, column.BitWidth, palletArrayIndex);
                    break;
                default:
                    throw new InvalidOperationException($"字段 {column.Name} 使用了暂不支持的压缩类型 {column.CompressionType}。");
            }
        }

        private static void WriteArrayColumnValue(byte[] recordBits, int rowId, Wdc5Column column, Array value)
        {
            if (column.IsStringLike)
            {
                throw new InvalidOperationException(
                    $"字段 {column.Name} 是字符串数组列，当前 WDC5 安全 patch 暂不支持修改字符串数组字段。"
                );
            }

            switch (column.CompressionType)
            {
                case Wdc5CompressionType.None:
                case Wdc5CompressionType.Immediate:
                case Wdc5CompressionType.SignedImmediate:
                    WriteInlineArrayBits(recordBits, rowId, column, value);
                    break;
                case Wdc5CompressionType.PalletArray:
                    WritePalletArrayValue(recordBits, rowId, column, value);
                    break;
                default:
                    throw new InvalidOperationException($"字段 {column.Name} 目前不支持数组安全 patch。");
            }
        }

        private static void WriteInlineArrayBits(byte[] recordBits, int rowId, Wdc5Column column, Array value)
        {
            if (column.ArrayLength <= 0)
                throw new InvalidOperationException($"字段 {column.Name} 缺少数组长度信息，无法安全 patch。");

            if (value.Length != column.ArrayLength)
            {
                throw new InvalidOperationException(
                    $"第 {rowId} 行字段 {column.Name} 的数组长度从 {column.ArrayLength} 变成了 {value.Length}，当前安全 patch 不支持改变数组长度。"
                );
            }

            if (column.StorageBitSize > 0 && checked(value.Length * column.BitWidth) != column.StorageBitSize)
            {
                throw new InvalidOperationException(
                    $"字段 {column.Name} 的数组布局与 WDC5 存储位宽不匹配。定义长度={value.Length}, BitWidth={column.BitWidth}, StorageBits={column.StorageBitSize}。"
                );
            }

            for (int i = 0; i < value.Length; i++)
            {
                uint rawValue = ToRawUInt32(value.GetValue(i));
                WriteBits(recordBits, column.BitOffset + (i * column.BitWidth), column.BitWidth, rawValue);
            }
        }

        private static void WritePalletArrayValue(byte[] recordBits, int rowId, Wdc5Column column, Array value)
        {
            if (column.Cardinality <= 0)
                throw new InvalidOperationException($"字段 {column.Name} 缺少 pallet-array cardinality，无法安全 patch。");

            if (value.Length != column.Cardinality)
            {
                throw new InvalidOperationException(
                    $"第 {rowId} 行字段 {column.Name} 的数组长度从 {column.Cardinality} 变成了 {value.Length}，当前安全 patch 不支持改变数组长度。"
                );
            }

            uint[] rawValues = new uint[value.Length];
            for (int i = 0; i < value.Length; i++)
                rawValues[i] = ToRawUInt32(value.GetValue(i));

            string key = string.Join("|", rawValues);
            if (!column.PalletArrayIndexByValueKey.TryGetValue(key, out uint palletIndex))
            {
                throw new InvalidOperationException(
                    $"第 {rowId} 行字段 {column.Name} 的数组值 [{string.Join(", ", rawValues)}] 不存在于原始 WDC5 pallet-array 数据中。当前安全 patch 只能写回原文件 palette 中已经存在的数组组合，不能新增 palette 项。"
                );
            }

            WriteBits(recordBits, column.BitOffset, column.BitWidth, palletIndex);
        }

        private static void WriteCommonValue(byte[] fileData, int rowId, Wdc5Column column, uint rawValue)
        {
            if (column.CommonValueOffsetById.TryGetValue(rowId, out int valueOffset))
            {
                byte[] bytes = BitConverter.GetBytes(rawValue);
                Buffer.BlockCopy(bytes, 0, fileData, valueOffset, bytes.Length);
                return;
            }

            if (rawValue != column.CommonDefaultValue)
            {
                throw new InvalidOperationException(
                    $"第 {rowId} 行字段 {column.Name} 使用 common 压缩，原文件里没有该 ID 的 common 项，无法在不改动文件结构的情况下写入值 {rawValue}。"
                );
            }
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

                if (lookupColumnCount != 1 || columnMetaDataSize != fieldsCount * ColumnMetaSize)
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
                EnsureAvailable(data, fieldMetaOffset, fieldsCount * FieldMetaSize, "WDC5 field metadata");

                var fieldMetas = new List<Wdc5FieldMeta>(fieldsCount);
                for (int i = 0; i < fieldsCount; i++)
                {
                    fieldMetas.Add(new Wdc5FieldMeta
                    {
                        Bits = reader.ReadInt16(),
                        Offset = reader.ReadInt16()
                    });
                }

                var columns = new List<Wdc5Column>();
                for (int i = 0; i < fieldsCount; i++)
                {
                    EnsureAvailable(data, stream.Position, ColumnMetaSize, "WDC5 column metadata");

                    ushort recordOffset = reader.ReadUInt16();
                    ushort size = reader.ReadUInt16();
                    uint additionalDataSize = reader.ReadUInt32();
                    uint compressionTypeValue = reader.ReadUInt32();
                    int dataA = reader.ReadInt32();
                    int dataB = reader.ReadInt32();
                    int dataC = reader.ReadInt32();

                    var compressionType = (Wdc5CompressionType)compressionTypeValue;
                    var fieldMeta = fieldMetas[i];
                    var column = new Wdc5Column
                    {
                        Name = string.Empty,
                        CompressionType = compressionType,
                        AdditionalDataSize = checked((int)additionalDataSize),
                        Cardinality = dataC,
                        StorageBitSize = size
                    };

                    switch (compressionType)
                    {
                        case Wdc5CompressionType.None:
                            column.BitOffset = recordOffset;
                            column.BitWidth = GetNoneBitWidth(fieldMeta, size, dataB);
                            column.ArrayLength = GetArrayLength(size, column.BitWidth);
                            break;
                        case Wdc5CompressionType.Immediate:
                        case Wdc5CompressionType.SignedImmediate:
                            column.BitOffset = dataA;
                            column.BitWidth = dataB;
                            column.ArrayLength = GetArrayLength(size, column.BitWidth);
                            break;
                        case Wdc5CompressionType.Common:
                            column.CommonDefaultValue = unchecked((uint)dataA);
                            break;
                        case Wdc5CompressionType.Pallet:
                        case Wdc5CompressionType.PalletArray:
                            column.BitOffset = dataA;
                            column.BitWidth = dataB;
                            if (recordOffset != dataA || size != dataB)
                                throw new InvalidDataException("不支持的 WDC5 pallet bit 布局。");
                            break;
                        default:
                            throw new InvalidDataException($"安全 patch 目前不支持压缩类型 {compressionTypeValue}。");
                    }

                    if (UsesInlineRecordBits(compressionType) && (column.BitOffset < 0 || column.BitWidth <= 0))
                        throw new InvalidDataException($"字段 {i} 的 WDC5 位布局无效。");

                    columns.Add(column);
                }

                foreach (var column in columns)
                {
                    switch (column.CompressionType)
                    {
                        case Wdc5CompressionType.Pallet:
                        case Wdc5CompressionType.PalletArray:
                            int valueCount = column.AdditionalDataSize / 4;
                            if (column.AdditionalDataSize % 4 != 0)
                                throw new InvalidDataException("WDC5 pallet 数据大小异常。");

                            EnsureAvailable(data, stream.Position, column.AdditionalDataSize, "WDC5 pallet data");

                            for (uint i = 0; i < valueCount; i++)
                            {
                                uint value = reader.ReadUInt32();
                                column.PalletValues.Add(value);
                                if (!column.PalletIndexByValue.ContainsKey(value))
                                    column.PalletIndexByValue[value] = i;
                            }

                            if (column.CompressionType == Wdc5CompressionType.PalletArray && column.Cardinality > 1)
                            {
                                if (valueCount % column.Cardinality != 0)
                                    throw new InvalidDataException("WDC5 pallet-array 数据大小与 cardinality 不匹配。");

                                for (uint paletteIndex = 0; paletteIndex < valueCount / column.Cardinality; paletteIndex++)
                                {
                                    string key = string.Join(
                                        "|",
                                        Enumerable.Range(0, column.Cardinality)
                                            .Select(offset => column.PalletValues[(int)(paletteIndex * column.Cardinality) + offset].ToString())
                                    );
                                    column.PalletArrayIndexByValueKey[key] = paletteIndex;
                                }
                            }
                            break;
                        case Wdc5CompressionType.Common:
                            if (column.AdditionalDataSize % 8 != 0)
                                throw new InvalidDataException("WDC5 common 数据大小异常。");

                            EnsureAvailable(data, stream.Position, column.AdditionalDataSize, "WDC5 common data");

                            for (int i = 0; i < column.AdditionalDataSize / 8; i++)
                            {
                                int id = reader.ReadInt32();
                                int valueOffset = checked((int)stream.Position);
                                uint value = reader.ReadUInt32();
                                column.CommonValueById[id] = value;
                                column.CommonValueOffsetById[id] = valueOffset;
                            }
                            break;
                    }
                }

                if (stream.Position != HeaderSize + sectionsCount * SectionHeaderSize + fieldsCount * FieldMetaSize + columnMetaDataSize + palletDataSize + commonDataSize)
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

            private static bool UsesInlineRecordBits(Wdc5CompressionType compressionType)
            {
                return compressionType == Wdc5CompressionType.None ||
                    compressionType == Wdc5CompressionType.Immediate ||
                    compressionType == Wdc5CompressionType.SignedImmediate ||
                    compressionType == Wdc5CompressionType.Pallet ||
                    compressionType == Wdc5CompressionType.PalletArray;
            }

            private static int GetNoneBitWidth(Wdc5FieldMeta fieldMeta, ushort columnSize, int fallbackBitWidth)
            {
                int bitWidth = 32 - fieldMeta.Bits;
                if (bitWidth <= 0)
                    bitWidth = fallbackBitWidth;
                if (bitWidth <= 0)
                    bitWidth = columnSize;
                return bitWidth;
            }

            private static int GetArrayLength(int storageBitSize, int bitWidth)
            {
                if (storageBitSize <= 0 || bitWidth <= 0 || storageBitSize == bitWidth)
                    return 1;

                if (storageBitSize % bitWidth != 0)
                    return 1;

                return storageBitSize / bitWidth;
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
                        $"DBD inline 字段数与 WDC5 column 数不匹配。Inline fields: {recordFields.Count}, WDC5 columns: {Columns.Count}, IdFieldIndex: {IdFieldIndex}, Flags: {Flags}。\n" +
                        "这通常表示当前加载使用的 definition build 与文件实际 layout 不一致；请尝试在加载时改用更匹配的 definition，或使用“自动选择”重新打开该 DB2。"
                    );

                for (int i = 0; i < Columns.Count; i++)
                {
                    var field = recordFields[i];
                    var column = Columns[i];
                    var semantics = Wdc5FieldSemantics.FromField(field);

                    column.Name = field.Name;
                    column.IsArray = semantics.IsArray;
                    column.IsStringLike = semantics.IsStringLike;
                    column.ArrayLength = semantics.ArrayLength;

                    ValidateColumnSemantics(column);
                }
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

            private static void ValidateColumnSemantics(Wdc5Column column)
            {
                bool inlineBits = column.CompressionType == Wdc5CompressionType.None ||
                    column.CompressionType == Wdc5CompressionType.Immediate ||
                    column.CompressionType == Wdc5CompressionType.SignedImmediate;

                bool layoutLooksArray = inlineBits &&
                    column.StorageBitSize > 0 &&
                    column.BitWidth > 0 &&
                    column.StorageBitSize != column.BitWidth &&
                    column.StorageBitSize % column.BitWidth == 0;

                if (column.IsArray)
                {
                    if (column.ArrayLength <= 0)
                        throw new InvalidOperationException($"字段 {column.Name} 的数组长度无效。");

                    if (layoutLooksArray)
                    {
                        int inferredLength = column.StorageBitSize / column.BitWidth;
                        if (inferredLength != column.ArrayLength)
                        {
                            throw new InvalidOperationException(
                                $"字段 {column.Name} 的 DBD 数组长度与 WDC5 布局不匹配。DBD={column.ArrayLength}, WDC5={inferredLength}。"
                            );
                        }
                    }

                    if (column.CompressionType == Wdc5CompressionType.PalletArray &&
                        column.Cardinality > 0 &&
                        column.Cardinality != column.ArrayLength)
                    {
                        throw new InvalidOperationException(
                            $"字段 {column.Name} 的 DBD 数组长度与 pallet-array cardinality 不匹配。DBD={column.ArrayLength}, WDC5={column.Cardinality}。"
                        );
                    }

                    return;
                }

                if (layoutLooksArray)
                {
                    throw new InvalidOperationException(
                        $"字段 {column.Name} 在 DBD 中是标量，但 WDC5 位布局看起来像数组。当前安全 patch 已停止，避免写坏文件。"
                    );
                }

                column.ArrayLength = 1;
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
            public Wdc5CompressionType CompressionType { get; set; }
            public int BitOffset { get; set; }
            public int BitWidth { get; set; }
            public int StorageBitSize { get; set; }
            public int ArrayLength { get; set; }
            public int Cardinality { get; set; }
            public int AdditionalDataSize { get; set; }
            public uint CommonDefaultValue { get; set; }
            public bool IsArray { get; set; }
            public bool IsStringLike { get; set; }
            public Dictionary<uint, uint> PalletIndexByValue { get; } = new Dictionary<uint, uint>();
            public List<uint> PalletValues { get; } = new List<uint>();
            public Dictionary<string, uint> PalletArrayIndexByValueKey { get; } = new Dictionary<string, uint>();
            public Dictionary<int, uint> CommonValueById { get; } = new Dictionary<int, uint>();
            public Dictionary<int, int> CommonValueOffsetById { get; } = new Dictionary<int, int>();
        }

        private sealed class Wdc5FieldSemantics
        {
            public bool IsArray { get; private set; }
            public bool IsStringLike { get; private set; }
            public int ArrayLength { get; private set; }

            public static Wdc5FieldSemantics FromField(FieldInfo field)
            {
                bool isArray = field.FieldType.IsArray;
                Type elementType = isArray ? field.FieldType.GetElementType() : field.FieldType;
                int arrayLength = isArray ? field.GetCustomAttribute<CardinalityAttribute>()?.Count ?? 0 : 1;

                if (isArray && arrayLength <= 0)
                    throw new InvalidOperationException($"字段 {field.Name} 缺少 CardinalityAttribute，无法确定数组长度。");

                return new Wdc5FieldSemantics
                {
                    IsArray = isArray,
                    IsStringLike = elementType == typeof(string),
                    ArrayLength = arrayLength
                };
            }
        }

        private sealed class Wdc5FieldMeta
        {
            public short Bits { get; set; }
            public short Offset { get; set; }
        }

        private enum Wdc5CompressionType : uint
        {
            None = 0,
            Immediate = 1,
            Common = 2,
            Pallet = 3,
            PalletArray = 4,
            SignedImmediate = 5
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

    public sealed class Wdc5PatchSaveResult
    {
        public int ChangedRows { get; set; }
        public int EditableRows { get; set; }
        public int RecordsCount { get; set; }
        public int SectionsCount { get; set; }
        public long FileSize { get; set; }
        public string BackupFile { get; set; }
    }
}
