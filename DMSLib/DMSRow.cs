using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DMSLib
{
    public class DMSRow
    {
        public CompareResult CompareResult = new CompareResult() { Status = DMSCompareStatus.NONE};
        public byte[] Values;
        public int[] Indexes;
        public DMSTable OwningTable;
        public long KeyHash;
        public long ValueHash;
        public long DateHash;
        public long VersionHash;
        public long RowHash;

        public int ColumnCount
        {
            get
            {
                return Indexes.Length - 1;
            }
        }

        public FieldTypes GetFieldType(int index)
        {
            if (OwningTable != null)
            {
                return OwningTable.Metadata.FieldMetadata[index].FieldType;
            } else
            {
                return FieldTypes.NOT_SET;
            }
        }

        public string GetColumnName(int index)
        {
            if (OwningTable != null)
            {
                return OwningTable.Columns[index].Name;
            } else
            {
                return "";
            }
        }

        public void InsertValueString(int index, string val)
        {
            var currentValues = GetRawValues();
            currentValues.Insert(index, DecodeValueString(val));
            SetRawValues(currentValues);
        }

        public void DeleteValue(int index)
        {
            var currentValues = GetRawValues();
            currentValues.RemoveAt(index);
            SetRawValues(currentValues);
        }
        public void ChangeValue(int index, string val)
        {
            var currentValues = GetRawValues();
            currentValues[index] = DecodeValueString(val);
            SetRawValues(currentValues);
        }

        /// <summary>Returns a copy, preserving binary data without a text conversion.</summary>
        public byte[] GetValueBytes(int index)
        {
            var data = new byte[Indexes[index + 1] - Indexes[index]];
            Array.Copy(Values, Indexes[index], data, 0, data.Length);
            return data;
        }

        private List<byte[]> GetRawValues()
        {
            return Enumerable.Range(0, ColumnCount).Select(GetValueBytes).ToList();
        }

        // null is the DAT null sentinel, not an empty string or a literal space.
        internal static byte[] DecodeValueString(string value)
        {
            if (value == null) return new byte[] { 0 };
            if (value.StartsWith("B{", StringComparison.Ordinal) && value.EndsWith("}", StringComparison.Ordinal))
                return HexStringToBytes(value.Substring(2, value.Length - 3));
            return Encoding.UTF8.GetBytes(value);
        }

        private void SetRawValues(List<byte[]> vals)
        {
            var newIndexes = new List<int>();
            MemoryStream ms = new MemoryStream();

            foreach(byte[] valBytes in vals)
            {
                newIndexes.Add((int)ms.Length);
                ms.Write(valBytes, 0, valBytes.Length);
            }
            Values = ms.ToArray();
            newIndexes.Add(Values.Length);
            ms.Dispose();
            Indexes = newIndexes.ToArray();
            newIndexes.Clear();
        }

        private static byte[] HexStringToBytes(string s)
        {
            const string HEX_CHARS = "0123456789ABCDEF";

            if (s.Length == 0)
                return new byte[0];

            if (s.Length % 2 != 0)
                throw new FormatException();

            byte[] bytes = new byte[s.Length / 2];

            for (int i = 0; i < s.Length; i += 2)
            {
                var high = HEX_CHARS.IndexOf(Char.ToUpperInvariant(s[i]));
                var low = HEX_CHARS.IndexOf(Char.ToUpperInvariant(s[i + 1]));
                if (high < 0 || low < 0) throw new FormatException("Invalid binary hexadecimal value.");
                bytes[i / 2] = (byte)((high << 4) | low);
            }

            return bytes;
        }

        internal void GenerateHashes()
        {
            /* generate hash of key fields */
            var keyFieldIndexes = OwningTable.Metadata.FieldMetadata
                            .Where(m => m.UseEditMask.HasFlag(UseEditFlags.KEY))
                            .Select(t => OwningTable.Columns.IndexOf(OwningTable.Columns
                                .First(c => c.Name == t.FieldName)));
            
            var dateFields = OwningTable.Metadata.FieldMetadata.Where(m => m.FieldType == FieldTypes.DATE 
                                                                        || m.FieldType == FieldTypes.DATETIME 
                                                                        || m.FieldType == FieldTypes.TIME)
                            .Select(t => OwningTable.Columns.IndexOf(OwningTable.Columns
                            .First(c => c.Name == t.FieldName)));

            var versionFields = OwningTable.Metadata.FieldMetadata.Where(m => m.FieldName == "VERSION")
                            .Select(t => OwningTable.Columns.IndexOf(OwningTable.Columns
                            .First(c => c.Name == t.FieldName)));

            var valueFields = Enumerable.Range(0, OwningTable.Metadata.FieldCount).Where(i => dateFields.Contains(i) == false && versionFields.Contains(i) == false);

            unchecked
            {
                int hash = 17;
                foreach (var idx in keyFieldIndexes)
                {
                    hash = hash * 23 + GetStringValue(idx).GetHashCode();
                }
                KeyHash = hash;

                hash = 17;
                foreach (var idx in dateFields)
                {
                    hash = hash * 23 + GetStringValue(idx).GetHashCode();
                }
                DateHash = hash;

                hash = 17;
                foreach (var idx in versionFields)
                {
                    hash = hash * 23 + GetStringValue(idx).GetHashCode();
                }
                VersionHash = hash;

                hash = 17;
                foreach (var idx in valueFields)
                {
                    hash = hash * 23 + GetStringValue(idx).GetHashCode();
                }
                ValueHash = hash;

            }
            RowHash = HashCode.Combine(KeyHash, DateHash, VersionHash, ValueHash);
        }

        public string GetStringValue(int index)
        {
            var start = Indexes[index];
            var end = Indexes[index + 1];
            
            var data = new byte[end - start];
            Array.Copy(Values, start, data, 0, end - start);
            var utf8Enc = Encoding.UTF8.GetString(data);
            if (Encoding.UTF8.GetBytes(utf8Enc).SequenceEqual(data))
            {
                return utf8Enc;
            }
            else
            {
                return "B{" + BitConverter.ToString(data).Replace("-", "") + "}";
            }
        }
        public dynamic GetValue(int index)
        {
            switch (GetFieldType(index))
            {
                case FieldTypes.CHAR:
                    return GetStringValue(index);
                case FieldTypes.LONG_CHAR:
                    return GetStringValue(index);
                case FieldTypes.NUMBER:
                case FieldTypes.SIGNED_NUMBER:
                    if (OwningTable.Metadata.FieldMetadata[index].DecimalPositions > 0)
                        return Decimal.Parse(GetStringValue(index), CultureInfo.InvariantCulture);
                    return Int64.Parse(GetStringValue(index), CultureInfo.InvariantCulture);
                case FieldTypes.DATE:
                    if (GetStringValue(index) == "\0")
                    {
                        return null;
                    }
                    return DateTime.ParseExact(GetStringValue(index), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                case FieldTypes.DATETIME:
                    if (GetStringValue(index) == "\0")
                    {
                        return null;
                    }
                    return DateTime.ParseExact(GetStringValue(index), "yyyy-MM-dd-HH.mm.ss.ffffff", CultureInfo.InvariantCulture);
                case FieldTypes.TIME:
                    if (GetStringValue(index) == "\0")
                    {
                        return null;
                    }
                    return DateTime.ParseExact(GetStringValue(index), "HH.mm.ss.ffffff", CultureInfo.InvariantCulture);
                case FieldTypes.IMG_OR_ATTACH:
                    var start = Indexes[index];
                    var end = Indexes[index + 1];
                    var data = new byte[end - start];
                    Array.Copy(Values, start, data, 0, end - start);
                    return data;
                default:
                    Debugger.Break();
                    return null;
            }
        }
        public string[] GetValuesAsString()
        {
            string[] values = new string[Indexes.Length - 1];
            for(var x = 0; x < Indexes.Length - 1; x++)
            {
                values[x] = GetStringValue(x);
            }

            return values;
        }

        internal void WriteToStream(StreamWriter sw)
        {
            StringBuilder sb = new StringBuilder();

            for(var x = 0; x < Indexes.Length-1;x++)
            {
                var start = Indexes[x];
                var end = Indexes[x + 1];
                var data = new byte[end - start];
                Array.Copy(Values, start, data, 0, end - start);
                var strEnc = DMSEncoder.EncodeData(data);
                sb.Append(strEnc).Append(",");
            }
            
            var encodedLines = DMSEncoder.FormatEncodedData(sb.ToString());

            foreach(var line in encodedLines)
            {
                sw.WriteLine(line);
            }

            sw.WriteLine("//");
        }

    }
}
