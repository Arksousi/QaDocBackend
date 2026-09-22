using System.Data.Common;
using Npgsql;
using NpgsqlTypes;

namespace QaDocBackend.Data;

public static class SqlExtensions
{
    public static void AddInt(this NpgsqlParameterCollection p, string name, int? value) =>
        p.Add(new NpgsqlParameter(name, NpgsqlDbType.Integer) { Value = (object?)value ?? DBNull.Value });

    public static void AddText(this NpgsqlParameterCollection p, string name, string? value) =>
        p.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value });

    /// <summary>A text[] for `= ANY(@p)` filters. Null means "no filter", not "matches nothing".</summary>
    public static void AddTextArray(this NpgsqlParameterCollection p, string name, string[]? values) =>
        p.Add(new NpgsqlParameter(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = (object?)values ?? DBNull.Value });

    public static void AddBool(this NpgsqlParameterCollection p, string name, bool value) =>
        p.Add(new NpgsqlParameter(name, NpgsqlDbType.Boolean) { Value = value });

    public static string Str(this DbDataReader r, string column)
    {
        int i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? string.Empty : r.GetString(i);
    }

    public static string? NStr(this DbDataReader r, string column)
    {
        int i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    public static int Int(this DbDataReader r, string column)
    {
        int i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? 0 : Convert.ToInt32(r.GetValue(i));
    }

    public static int? NInt(this DbDataReader r, string column)
    {
        int i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i));
    }

    public static bool Bool(this DbDataReader r, string column) => r.GetBoolean(r.GetOrdinal(column));

    /// <summary>Timestamps are TIMESTAMPTZ; always hand them out as UTC so JSON carries the offset.</summary>
    public static DateTime Utc(this DbDataReader r, string column)
    {
        int i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? default : DateTime.SpecifyKind(r.GetDateTime(i).ToUniversalTime(), DateTimeKind.Utc);
    }

    /// <summary>Escapes LIKE/ILIKE wildcards (PostgreSQL's default escape is a backslash).</summary>
    public static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
