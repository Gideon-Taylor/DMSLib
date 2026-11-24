using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DMSLib
{
    /// <summary>Per-table outcome of loading DMS rows into SQLite.</summary>
    public class SQLTableLoadResult
    {
        public string DBName;
        public long RowsRead;
        public long RowsInserted;
        public long RowsFailed;
        /// <summary>Text values that weren't valid UTF-8 and were stored as B{hex}.</summary>
        public long NonUtf8Values;
        /// <summary>The first few failures, for diagnosis. RowsFailed has the full count.</summary>
        public List<string> SampleErrors = new List<string>();
    }

    /// <summary>Outcome of a load. Nothing is dropped silently: every failed row is counted here.</summary>
    public class SQLConversionReport
    {
        public const int MaxSampleErrorsPerTable = 20;

        public Dictionary<string, SQLTableLoadResult> Tables = new Dictionary<string, SQLTableLoadResult>(StringComparer.OrdinalIgnoreCase);

        public long TotalFailed => Tables.Values.Sum(t => t.RowsFailed);

        internal SQLTableLoadResult For(string dbName)
        {
            if (!Tables.TryGetValue(dbName, out var result))
            {
                result = new SQLTableLoadResult { DBName = dbName };
                Tables[dbName] = result;
            }
            return result;
        }
    }

    public class SQLConverter
    {
        public static async Task<SqliteConnection> DMSFileToSQLAsync(DMSFile file, IProgress<float> progress, CancellationToken cancellationToken)
        {
            return await LoadInMemoryAsync(file.Tables, progress, cancellationToken);
        }

        public static async Task<SqliteConnection> DMSTableToSQLAsync(List<DMSTable> tables, IProgress<float> progress, CancellationToken cancellationToken)
        {
            return await LoadInMemoryAsync(tables, progress, cancellationToken);
        }

        /// <summary>
        /// Opens (creating if needed) a SQLite database file to load DMS files into.
        /// </summary>
        public static SqliteConnection OpenFile(string path)
        {
            SQLitePCL.Batteries_V2.Init();
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
            connection.Open();
            using (var pragma = connection.CreateCommand())
            {
                /* Bulk load: a failed run is simply re-run from the .dat files. */
                pragma.CommandText = "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF;";
                pragma.ExecuteNonQuery();
            }
            return connection;
        }

        /// <summary>
        /// Loads every table of a DMS file into an open connection, one transaction per table.
        /// Tables are created if missing, so several DMS files can be loaded into one database.
        /// Failed rows are counted in the report rather than dropped silently.
        /// </summary>
        public static void LoadInto(SqliteConnection connection, IEnumerable<DMSTable> tables, SQLConversionReport report, IProgress<float> progress, CancellationToken cancellationToken)
        {
            var tableList = tables.ToList();
            long totalRowCount = tableList.Sum(t => (long)t.Rows.Count);
            long rowsDone = 0;

            foreach (var table in tableList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = report?.For(table.DBName);

                using (var transaction = connection.BeginTransaction())
                {
                    using (var makeTable = connection.CreateCommand())
                    {
                        makeTable.Transaction = transaction;
                        makeTable.CommandText = GetTableDDL(table);
                        makeTable.ExecuteNonQuery();
                    }

                    using (var insertCmd = BuildInsert(connection, transaction, table))
                    {
                        foreach (var row in table.Rows)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (result != null) result.RowsRead++;

                            try
                            {
                                for (var x = 0; x < table.Columns.Count; x++)
                                {
                                    object value = row.GetValue(x);
                                    if (result != null && value is string s && s.StartsWith("B{") && IsText(table, x))
                                    {
                                        result.NonUtf8Values++;
                                    }
                                    insertCmd.Parameters[x].Value = value ?? DBNull.Value;
                                }
                                insertCmd.Parameters[table.Columns.Count].Value = row.RowHash;
                                insertCmd.ExecuteNonQuery();
                                if (result != null) result.RowsInserted++;
                            }
                            catch (Exception ex) when (ex is SqliteException || ex is FormatException || ex is OverflowException)
                            {
                                if (result != null)
                                {
                                    result.RowsFailed++;
                                    if (result.SampleErrors.Count < SQLConversionReport.MaxSampleErrorsPerTable)
                                    {
                                        result.SampleErrors.Add($"{ex.Message} | key: {DescribeKey(table, row)}");
                                    }
                                }
                            }

                            rowsDone++;
                            if (totalRowCount > 0 && rowsDone % 1000 == 0)
                            {
                                progress?.Report(rowsDone / (float)totalRowCount * 100);
                            }
                        }
                    }

                    transaction.Commit();
                }
            }
            progress?.Report(100);
        }

        public static async Task<SqliteDataReader> ExecuteQuery(SqliteConnection queryConnection, string query)
        {
            if (queryConnection == null)
            {
                throw new InvalidOperationException("queryConnection cannot be null.");
            }

            var queryCommand = queryConnection.CreateCommand();
            queryCommand.CommandText = query;

            return await queryCommand.ExecuteReaderAsync();
        }

        private static async Task<SqliteConnection> LoadInMemoryAsync(List<DMSTable> tables, IProgress<float> progress, CancellationToken cancellationToken)
        {
            SQLitePCL.Batteries_V2.Init();
            var queryConnection = new SqliteConnection("Data Source=:memory:");
            queryConnection.Open();

            try
            {
                await Task.Run(() => LoadInto(queryConnection, tables, null, progress, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                queryConnection.Close();
                return null;
            }

            return queryConnection;
        }

        private static SqliteCommand BuildInsert(SqliteConnection connection, SqliteTransaction transaction, DMSTable table)
        {
            var insertCmd = connection.CreateCommand();
            insertCmd.Transaction = transaction;

            var names = table.Metadata.FieldMetadata.Select(f => f.FieldName).ToList();
            var placeholders = Enumerable.Range(1, table.Columns.Count + 1).Select(x => $"@p{x}");
            insertCmd.CommandText = $"INSERT INTO {table.DBName}({string.Join(",", names)},__rowHash) VALUES({string.Join(",", placeholders)});";

            for (var x = 1; x <= table.Columns.Count + 1; x++)
            {
                insertCmd.Parameters.Add(new SqliteParameter($"@p{x}", DBNull.Value));
            }
            insertCmd.Prepare();
            return insertCmd;
        }

        private static bool IsText(DMSTable table, int index)
        {
            var type = table.Metadata.FieldMetadata[index].FieldType;
            return type == FieldTypes.CHAR || type == FieldTypes.LONG_CHAR;
        }

        private static string DescribeKey(DMSTable table, DMSRow row)
        {
            var parts = new List<string>();
            for (var x = 0; x < table.Metadata.FieldMetadata.Count && x < row.ColumnCount; x++)
            {
                var field = table.Metadata.FieldMetadata[x];
                if (field.UseEditMask.HasFlag(UseEditFlags.KEY))
                {
                    var value = row.GetStringValue(x);
                    parts.Add($"{field.FieldName}={(value.Length > 60 ? value.Substring(0, 60) + "..." : value)}");
                }
            }
            return parts.Count > 0 ? string.Join(", ", parts) : "(no key fields)";
        }

        internal static string GetTableDDL(DMSTable table)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("CREATE TABLE IF NOT EXISTS ");
            sb.Append(table.DBName);
            sb.Append("(");
            foreach (var col in table.Metadata.FieldMetadata)
            {
                sb.Append($"{col.FieldName} {GetDataType(col)},");
            }
            sb.Append("__rowHash NUMBER");
            List<DMSRecordFieldMetadata> keys = table.Metadata.FieldMetadata.Where(m => m.UseEditMask.HasFlag(UseEditFlags.KEY)).ToList();
            if (keys.Count() > 0)
            {
                sb.Append(", PRIMARY KEY (");
                foreach (var key in keys)
                {
                    sb.Append(key.FieldName);
                    sb.Append(",");
                }
                sb.Length--;
                sb.Append(")");
            }
            sb.Append(");");
            return sb.ToString();
        }

        internal static string GetDataType(DMSRecordFieldMetadata fieldInfo)
        {

            var type = fieldInfo.FieldType;
            var decimals = fieldInfo.DecimalPositions;
            switch (type)
            {
                case FieldTypes.CHAR:
                    return "TEXT";
                case FieldTypes.LONG_CHAR:
                    return "TEXT";
                case FieldTypes.NUMBER:
                    if (decimals > 0)
                    {
                        return "REAL";
                    }
                    return "INTEGER";
                case FieldTypes.SIGNED_NUMBER:
                    if (decimals > 0)
                    {
                        return "REAL";
                    }
                    return "INTEGER";
                case FieldTypes.DATE:
                    return "TEXT";
                case FieldTypes.TIME:
                    return "TEXT";
                case FieldTypes.DATETIME:
                    return "TEXT";
                case FieldTypes.IMG_OR_ATTACH:
                    return "BLOB";
                case FieldTypes.IMAGE_REF:
                    return "BLOB";
                default:
                    Debugger.Break();
                    return "";
            }

        }

    }
}
