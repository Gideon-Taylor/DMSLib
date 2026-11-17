using System;
using System.IO;
using System.Linq;
using System.Text;
using DMSLib;

// No test-framework dependency: build this project and run DMSLib.Tests.exe.
internal static class Program
{
    private static int tests;
    private static void Check(bool value, string description)
    {
        if (!value) throw new Exception(description);
        tests++;
    }

    private static DMSNewColumn Column(string name, FieldTypes type, int length, int scale = 0)
    {
        return new DMSNewColumn(name, 1, length, scale, 0, type, FieldFormats.UPPER_OR_DEFAULT);
    }

    private static DMSTable Table()
    {
        // Minimal synthetic record header: no fields, indexes or tablespaces.
        var t = new DMSTable { Name = "TEST_REC", DBName = "PS_TEST_REC", WhereClause = "", Metadata = new DMSRecordMetadata(new byte[320], true) };
        t.Rows.Add(new DMSRow { OwningTable = t, Values = new byte[0], Indexes = new[] { 0 } });
        t.AddColumn(Column("KEY_FIELD", FieldTypes.CHAR, 10), null, "KEY");
        return t;
    }

    private static void Reject(Action action, DMSTable t)
    {
        var count = t.Columns.Count;
        var bytes = (byte[])t.Rows[0].Values.Clone();
        var rejected = false;
        try { action(); }
        catch (ArgumentException) { rejected = true; }
        catch (FormatException) { rejected = true; }
        Check(rejected, "Invalid input must fail");
        Check(t.Columns.Count == count && t.Metadata.FieldCount == count && t.Rows[0].Values.SequenceEqual(bytes), "Failure must leave table untouched");
    }

    private static int Main(string[] args)
    {
        try
        {
            var t = Table();
            t.AddColumn(Column("NULL_DATE", FieldTypes.DATE, 10), t.Columns.Last(), null);
            Check(t.Columns.Last().Type == "DATE", "DATE column type");
            Check(t.Rows[0].GetValue(1) == null && t.Rows[0].GetValueBytes(1).SequenceEqual(new byte[] { 0 }), "Proper date NULL");
            t.AddColumn(Column("PRICE", FieldTypes.SIGNED_NUMBER, 7, 2), t.Columns.Last(), "-12.34");
            Check(t.Columns.Last().Type == "NUMBER" && t.Columns.Last().Size == "7,2", "Preserve numeric precision");
            Check((decimal)t.Rows[0].GetValue(2) == -12.34m, "Read decimal numbers");
            var binary = new byte[] { 0xE0, 0x80, 0x80, 0, 0xFF, 0x01 };
            t.AddColumn(Column("PAYLOAD", FieldTypes.IMG_OR_ATTACH, 0), t.Columns.Last(), "B{E0808000FF01}");
            Check(t.Rows[0].GetValueBytes(3).SequenceEqual(binary), "Decode all hexadecimal bytes");
            t.AddColumn(Column("BLANK", FieldTypes.CHAR, 1), t.Columns.Last(), " ");
            Check(t.Rows[0].GetStringValue(4) == " ", "Space is distinct from NULL");
            Check(t.Rows[0].GetValueBytes(3).SequenceEqual(binary), "Adding preserves invalid UTF-8 binary bytes");
            t.Rows[0].ChangeValue(4, "N");
            Check(t.Rows[0].GetValueBytes(3).SequenceEqual(binary), "Editing preserves other raw values");
            t.Rows[0].ChangeValue(4, "");
            Check(t.Rows[0].GetValueBytes(4).Length == 0, "Empty value does not throw");
            t.Rows[0].ChangeValue(4, "N");
            t.Rows[0].InsertValueString(0, ""); t.Rows[0].DeleteValue(0);
            Check(t.Rows[0].GetValueBytes(3).SequenceEqual(binary), "Insert/delete preserve binary bytes");
            Reject(() => t.AddColumn(Column("BAD", FieldTypes.CHAR, 1), t.Columns.Last(), " N"), t);
            Reject(() => t.AddColumn(Column("BAD", FieldTypes.DATE, 10), t.Columns.Last(), " "), t);
            Reject(() => t.AddColumn(Column("BAD", FieldTypes.NUMBER, 3, 1), t.Columns.Last(), "123.4"), t);
            Reject(() => t.AddColumn(Column("BAD", FieldTypes.NOT_SET, 1), t.Columns.Last(), "x"), t);
            Reject(() => t.AddColumn(Column("BLANK", FieldTypes.CHAR, 1), t.Columns.Last(), "x"), t);
            Reject(() => t.AddColumn(Column("BAD", FieldTypes.CHAR, 1), new DMSColumn(), "x"), t);
            Reject(() => t.AddColumn(Column("BAD", FieldTypes.IMG_OR_ATTACH, 0), t.Columns.Last(), "B{0X}"), t);
            Reject(() => t.AddColumn(Column("BAD", FieldTypes.CHAR, 1), t.Columns.Last(), null), t);
            Reject(() => t.AddColumn(Column("BAD", FieldTypes.CHAR, 1), t.Columns.Last(), ""), t);
            Reject(() => t.AddColumn(Column("BAD", FieldTypes.DATE, 10), t.Columns.Last(), "2026-02-30"), t);
            t.AddColumn(Column("REAL_DATE", FieldTypes.DATE, 10), t.Columns.Last(), "2026-11-17");
            Check((DateTime)t.Rows[0].GetValue(t.Columns.Count - 1) == new DateTime(2026, 11, 17), "Valid non-null date");
            t.AddColumn(Column("BINARY_NULL", FieldTypes.DATE, 10), t.Columns.Last(), "B{00}");
            Check(t.Rows[0].GetValue(t.Columns.Count - 1) == null, "Legacy binary null syntax");
            var empty = Table(); empty.Rows.Clear();
            empty.AddColumn(Column("DATE_FIRST", FieldTypes.DATE, 10), null, null);
            Check(empty.Columns[0].Type == "DATE" && empty.Metadata.FieldCount == 2, "Insert first in an empty export");
            foreach (var type in new[] { FieldTypes.TIME, FieldTypes.DATETIME, FieldTypes.LONG_CHAR, FieldTypes.IMAGE_REF })
            {
                var name = "T_" + type;
                t.AddColumn(Column(name, type, type == FieldTypes.TIME ? 15 : type == FieldTypes.DATETIME ? 26 : 30), t.Columns.Last(), type == FieldTypes.IMAGE_REF ? " " : null);
                Check(t.Columns.Last().Type != "CHAR" || type == FieldTypes.IMAGE_REF, "Map " + type);
            }
            var file = new DMSFile { Version = "8.5_8.62:1:0", Endian = "LE", BaseLanguage = "ENG", Database = "TEST", Started = "TEST", Ended = "TEST", DDLs = new DDLDefaults(new byte[16], true) };
            file.Tables.Add(t);
            var path = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dat");
            try
            {
                DMSWriter.Write(path, file);
                var reread = DMSReader.Read(path);
                var read = reread.Tables.Single();
                Check(reread.Ended == file.Ended, "Preserve ending timestamp comment");
                Check(read.Columns.Select(c => c.Type + c.Size).SequenceEqual(t.Columns.Select(c => c.Type + c.Size)), "Round trip column types and precision");
                Check(read.Metadata.FieldCount == t.Columns.Count && read.Rows[0].ColumnCount == t.Columns.Count, "Round trip metadata and row count");
                for (var i = 0; i < t.Columns.Count; i++)
                    Check(read.Rows[0].GetValueBytes(i).SequenceEqual(t.Rows[0].GetValueBytes(i)), "Round trip raw field " + i);
                Check(read.Rows[0].GetValue(1) == null, "Round trip NULL date");
                Check(File.ReadAllText(path).Contains("B(AA)"), "DAT NULL encoding");
            }
            finally { if (args.Length == 0) File.Delete(path); }
            Console.WriteLine("PASS: " + tests + " checks");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
