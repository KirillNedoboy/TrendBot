using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TradingBot.Infrastructure;

internal static class SqliteBoundedStorage
{
    public static void Configure(SqliteConnection connection, long maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (!StringComparer.OrdinalIgnoreCase.Equals(Scalar(connection, "PRAGMA journal_mode;")?.ToString(), "delete"))
        {
            throw new InvalidOperationException("Bounded SQLite storage requires DELETE journal mode.");
        }

        long pageSize = Convert.ToInt64(Scalar(connection, "PRAGMA page_size;"), CultureInfo.InvariantCulture);
        long maximumPages = maximumBytes / pageSize;
        if (maximumPages < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        long applied = Convert.ToInt64(Scalar(connection,
            $"PRAGMA max_page_count = {maximumPages.ToString(CultureInfo.InvariantCulture)};"),
            CultureInfo.InvariantCulture);
        if (applied != maximumPages)
        {
            throw new InvalidOperationException("The SQLite database cannot satisfy its page budget.");
        }

        Scalar(connection, "PRAGMA synchronous = FULL;");
        Scalar(connection, "PRAGMA temp_store = MEMORY;");
        Scalar(connection, "PRAGMA cache_spill = OFF;");
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
