using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DMSLib
{
    public class DMSTable
    {
        public List<DMSColumn> Columns = new List<DMSColumn>();
        public CompareResult CompareResult = new CompareResult() { Status = DMSCompareStatus.NONE };
        public string DBName;
        public DMSRecordMetadata Metadata;
        public string Name;
        public List<DMSRow> Rows = new List<DMSRow>();
        public string WhereClause;
        public override string ToString()
        {
            return Name;
        }

        public DMSColumn GetColumnByName(string columnName)
        {
            return Columns.FirstOrDefault(c => c.Name == columnName);
        }

        public string GetColumnName(int index)
        {
            return Columns[index].Name;
        }
        public void AddColumn(DMSNewColumn newColumn, DMSColumn colBefore, string defaultValue)
        {
            if (newColumn == null) throw new ArgumentNullException(nameof(newColumn));
            if (newColumn.FieldName == null || !Regex.IsMatch(newColumn.FieldName, @"^[A-Z][A-Z0-9_]{0,17}$"))
                throw new ArgumentException("Field names must be 1-18 uppercase letters, digits or underscores, starting with a letter.");
            if (Columns.Any(c => c.Name == newColumn.FieldName))
                throw new ArgumentException("Column already exists: " + newColumn.FieldName);
            if (colBefore != null && !Columns.Contains(colBefore))
                throw new ArgumentException("The preceding column does not belong to this table.", nameof(colBefore));
            if (Metadata == null || Metadata.FieldCount != Columns.Count || Metadata.FieldMetadata.Count != Columns.Count ||
                !Metadata.FieldMetadata.Select(f => f.FieldName).SequenceEqual(Columns.Select(c => c.Name)) ||
                Rows.Any(r => r.ColumnCount != Columns.Count))
                throw new InvalidOperationException("Column definitions, field metadata and row counts must agree before adding a column.");
            if (newColumn.FieldLength < 0 || newColumn.DecimalPositions < 0 ||
                newColumn.DecimalPositions > newColumn.FieldLength || newColumn.VersionNumber < 0)
                throw new ArgumentException("Invalid length, decimal positions or version number.");
            if (!Enum.IsDefined(typeof(FieldFormats), newColumn.FieldFormat) ||
                !Enum.IsDefined(typeof(GUIControls), newColumn.DefaultGUIControl))
                throw new ArgumentException("Unknown field format or GUI control.");

            var type = ColumnType(newColumn.FieldType);
            if (newColumn.DecimalPositions > 0 && type != "NUMBER")
                throw new ArgumentException("Only numeric columns may have decimal positions.");
            if ((type == "CHAR" || type == "NUMBER") && newColumn.FieldLength == 0)
                throw new ArgumentException("Character and numeric columns require a positive length.");
            ValidateDefault(newColumn, defaultValue);

            // A null preceding column inserts at the beginning (also supports empty tables).
            var colIndex = colBefore == null ? 0 : Columns.IndexOf(colBefore) + 1;
            var newFieldMeta = new DMSRecordFieldMetadata(newColumn, this);
            var newDMSCol = new DMSColumn
            {
                Name = newColumn.FieldName,
                Type = type,
                Size = newColumn.FieldLength.ToString(CultureInfo.InvariantCulture) +
                    (newColumn.DecimalPositions > 0 ? "," + newColumn.DecimalPositions.ToString(CultureInfo.InvariantCulture) : "")
            };
            // Prepare every row before changing the table, so invalid input leaves it untouched.
            var stagedRows = Rows.Select(row =>
            {
                var copy = new DMSRow { Values = row.Values, Indexes = row.Indexes };
                copy.InsertValueString(colIndex, defaultValue);
                return copy;
            }).ToList();
            Metadata.FieldMetadata.Insert(colIndex, newFieldMeta);
            Columns.Insert(colIndex, newDMSCol);
            Metadata.FieldCount = Columns.Count;
            for (var i = 0; i < Rows.Count; i++)
            {
                Rows[i].Values = stagedRows[i].Values;
                Rows[i].Indexes = stagedRows[i].Indexes;
                Rows[i].GenerateHashes();
            }
        }

        private static string ColumnType(FieldTypes type)
        {
            switch (type)
            {
                case FieldTypes.CHAR: case FieldTypes.IMAGE_REF: return "CHAR";
                case FieldTypes.LONG_CHAR: return "LONG";
                case FieldTypes.NUMBER: case FieldTypes.SIGNED_NUMBER: return "NUMBER";
                case FieldTypes.DATE: return "DATE";
                case FieldTypes.TIME: return "TIME";
                case FieldTypes.DATETIME: return "DATETIME";
                case FieldTypes.IMG_OR_ATTACH: return "IMAGE";
                default: throw new ArgumentException("Unsupported field type: " + type);
            }
        }

        private static void ValidateDefault(DMSNewColumn column, string value)
        {
            var bytes = DMSRow.DecodeValueString(value);
            var isNull = bytes.Length == 1 && bytes[0] == 0;
            var type = ColumnType(column.FieldType);
            if (isNull)
            {
                if (type == "CHAR" || type == "NUMBER")
                    throw new ArgumentException("Use an explicit character or numeric default for this column type.");
                return;
            }
            if (type == "IMAGE" || type == "LONG") return;
            var text = new UTF8Encoding(false, true).GetString(bytes);
            if (type == "CHAR")
            {
                if (text.Length == 0 || text.Length > column.FieldLength)
                    throw new ArgumentException("Character default must contain 1 to FieldLength characters; use a literal space for blank.");
            }
            else if (type == "NUMBER")
            {
                if (!Regex.IsMatch(text, @"^[+-]?\d+(\.\d+)?$"))
                    throw new ArgumentException("Numeric default must be an invariant decimal number.");
                var digits = text.TrimStart('+', '-').Split('.');
                if (digits[0].Length > column.FieldLength - column.DecimalPositions ||
                    (digits.Length > 1 && digits[1].Length > column.DecimalPositions))
                    throw new ArgumentException("Numeric default exceeds the column's precision or scale.");
            }
            else
            {
                var format = type == "DATE" ? "yyyy-MM-dd" : type == "TIME" ? "HH.mm.ss.ffffff" : "yyyy-MM-dd-HH.mm.ss.ffffff";
                DateTime parsed;
                if (!DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                    throw new ArgumentException("Default must be null or use format " + format + ".");
            }
        }

        public void WriteToStream(StreamWriter sw, bool saveOnlyDiffs = false)
        {
            if (WhereClause.Length > 0)
            {
                sw.WriteLine($"EXPORT  {Name}.{DBName} WHERE ");
                WriteWhereClause(sw, WhereClause);
            }
            else
            {
                sw.WriteLine($"EXPORT  {Name}.{DBName} ");
            }

            sw.WriteLine("/");

            /* Write table metadata */
            Metadata.WriteToStream(sw);

            /* Write the column info */
            WriteColumns(sw, Columns);

            /* Write each row of data */
            foreach (DMSRow row in Rows)
            {
                if (saveOnlyDiffs)
                {
                    if (row.CompareResult.Status == DMSCompareStatus.NONE || row.CompareResult.Status == DMSCompareStatus.SAME)
                    {
                        /* skip this row */
                        continue;
                    }
                }

                row.WriteToStream(sw);
                /* WriteTableRow(stream, row);
                stream.WriteLine("//");*/
            }

            sw.WriteLine("/");
        }

        private void WriteWhereClause(StreamWriter stream, string where)
        {
            stream.WriteLine(where);
        }

        private void WriteColumns(StreamWriter stream, List<DMSColumn> columns)
        {
            var maxLength = 68;
            var curLineLength = 0;
            stream.WriteLine("/");
            foreach (DMSColumn col in columns)
            {
                var nextColumn = $"{col.Name}:{col.Type}({col.Size})~~~";
                if (curLineLength + nextColumn.Length < maxLength)
                {
                    stream.Write(nextColumn);
                    curLineLength += nextColumn.Length;
                }
                else
                {
                    stream.WriteLine();
                    stream.Write(nextColumn);
                    curLineLength = nextColumn.Length;
                }
            }

            stream.WriteLine();
            stream.WriteLine("/");
        }

        public void DropColumn(DMSColumn selectedColumn)
        {
            /* Update DMSRecord metadata */
            Metadata.FieldCount--;

            /* Add the field metadata */
            var colIndex = Columns.IndexOf(selectedColumn);

            /* Remove column from metadata */
            Metadata.FieldMetadata.Remove(Metadata.FieldMetadata.Where(p => p.FieldName == selectedColumn.Name)
                .First());

            /* Remove the DMSColumn */
            Columns.Remove(selectedColumn);

            /* remove the value from the rows */
            foreach (var row in Rows)
            {
                row.DeleteValue(colIndex);
            }
        }

    }

    public class DMSNewColumn
    {
        public int DecimalPositions;
        public GUIControls DefaultGUIControl;
        public FieldFormats FieldFormat;
        public int FieldLength;
        public string FieldName;
        public FieldTypes FieldType;

        public UseEditFlags UseEditMask;
        public int VersionNumber;

        public DMSNewColumn(string fieldName, int version, int length, int decPositions, UseEditFlags useEdit,
            FieldTypes type, FieldFormats format, GUIControls gui = GUIControls.DEFAULT)
        {
            FieldName = fieldName;
            VersionNumber = version;
            FieldLength = length;
            DecimalPositions = decPositions;
            UseEditMask = useEdit;
            FieldType = type;
            FieldFormat = format;
            DefaultGUIControl = gui;
        }
    }
}
