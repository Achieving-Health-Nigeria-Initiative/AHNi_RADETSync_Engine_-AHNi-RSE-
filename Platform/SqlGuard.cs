using System.Text;
using System.Text.RegularExpressions;

namespace AHNiRSE.Platform;

/// <summary>
/// Checks a custom SQL script (force backfill report type "custom") before it runs, and turns its parameters
/// into PostgreSQL's $1, $2 ... The Backfill app runs the same checks (backfill_app/sqlguard.py) when a script
/// is uploaded, so a script that passes there passes here.
///
/// RULES - the script must be:
///   • one statement (a trailing ; is fine)
///   • a query: it starts with SELECT or WITH
///   • free of INSERT, UPDATE, DELETE, MERGE, TRUNCATE, DROP, ALTER, CREATE, GRANT, REVOKE and SELECT INTO
///   • free of functions that act outside the query (pg_terminate_backend, dblink, set_config, lo_export,
///     pg_read_file, pg_sleep, query_to_xml, nextval ...)
/// Strings, quoted names and comments are skipped, so "-- delete later" or 'Update' in a literal is fine.
/// These checks are the first line only: the query also runs in a READ ONLY transaction that is always rolled
/// back, with a statement timeout and a row limit (PostgresService.ExecuteReadOnlyAsync).
///
/// PARAMETERS
///   standard   :facility_id ?1   :start_date ?2   :end_date ?3   :previous_quarter_end ?4
///              :previous_previous_quarter_end ?5   ('?2' quoted, as in the report module's queries, works too)
///   custom     any other :name, e.g. :min_age - its value is set when the run is requested in the Backfill app
///              and sent to PostgreSQL as an untyped value, so the column it is compared with decides its type.
///              Where nothing does (e.g. in the SELECT list) it is text; cast it for another type: :min_age::int.
/// </summary>
public static class SqlGuard
{
    private static readonly string[] Standard = QueryParameters.PlaceholderNames;

    private static readonly Regex FirstWord = new(@"^\s*\(*\s*([A-Za-z_]+)", RegexOptions.Compiled);

    private static readonly Regex BlockedWord = new(
        @"\b(insert|update|delete|merge|truncate|drop|alter|create|grant|revoke|into)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BlockedFunction = new(
        @"\b(pg_terminate_backend|pg_cancel_backend|pg_reload_conf|pg_rotate_logfile|set_config|dblink\w*|lo_\w+|" +
        @"pg_read_file|pg_read_binary_file|pg_ls_\w+|pg_stat_file|pg_file_\w+|pg_sleep\w*|pg_advisory\w*|" +
        @"pg_try_advisory\w*|pg_switch_wal|pg_create_\w+|pg_drop_\w+|pg_promote|pg_logical_\w+|pg_replication_\w+|" +
        @"query_to_xml\w*|cursor_to_xml\w*|ts_stat|nextval|setval|pg_notify)\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QuotedPositional = new(@"^\?(\d+)$", RegexOptions.Compiled);

    /// <summary>A parameter in the script: where it is and what it stands for.</summary>
    public sealed record Placeholder(int Start, int Length, string Name);

    /// <summary>What <see cref="Analyze"/> found.</summary>
    public sealed class Analysis
    {
        public required string Sql { get; init; }
        public required IReadOnlyList<string> Errors { get; init; }
        public required IReadOnlyList<Placeholder> Placeholders { get; init; }

        public bool IsValid => Errors.Count == 0;

        /// <summary>Standard parameters the script uses (facility_id, start_date ...), in binding order.</summary>
        public IReadOnlyList<string> StandardParameters =>
            Standard.Where(s => Placeholders.Any(p => p.Name == s)).ToList();

        /// <summary>Custom parameters, in the order they first appear.</summary>
        public IReadOnlyList<string> CustomParameters =>
            Placeholders.Select(p => p.Name).Where(n => !Standard.Contains(n)).Distinct().ToList();

        /// <summary>
        /// The script with every parameter as $n: the standard ones $1..$5 and the custom ones $6, $7 ... in
        /// <see cref="CustomParameters"/> order.
        /// </summary>
        public string ToPostgres()
        {
            var custom = CustomParameters;
            var sb = new StringBuilder(Sql.Length);
            var at = 0;
            foreach (var p in Placeholders.OrderBy(p => p.Start))
            {
                sb.Append(Sql, at, p.Start - at);
                var index = Array.IndexOf(Standard, p.Name);
                sb.Append('$').Append(index >= 0 ? index + 1 : Standard.Length + 1 + IndexOf(custom, p.Name));
                at = p.Start + p.Length;
            }
            sb.Append(Sql, at, Sql.Length - at);
            return sb.ToString();
        }

        private static int IndexOf(IReadOnlyList<string> list, string value)
        {
            for (var i = 0; i < list.Count; i++)
                if (list[i] == value) return i;
            return -1;
        }
    }

    public static Analysis Analyze(string sql)
    {
        sql = sql.TrimStart('﻿');
        var errors = new List<string>();
        var placeholders = new List<Placeholder>();
        var mask = sql.ToCharArray(); // the script with strings, quoted names, comments and parameters blanked
        var n = sql.Length;

        void Blank(int from, int to)
        {
            for (var k = from; k < to && k < n; k++)
                if (mask[k] is not ('\n' or '\r')) mask[k] = ' ';
        }

        void Positional(int start, int length, string digits)
        {
            var number = int.TryParse(digits, out var v) ? v : 0;
            if (number < 1 || number > Standard.Length)
                errors.Add($"?{digits} is not a parameter: use ?1 to ?{Standard.Length}, or a :name.");
            else
                placeholders.Add(new Placeholder(start, length, Standard[number - 1]));
            Blank(start, start + length);
        }

        var i = 0;
        while (i < n)
        {
            var c = sql[i];
            var next = i + 1 < n ? sql[i + 1] : '\0';

            if (c == '-' && next == '-')
            {
                var end = sql.IndexOf('\n', i);
                if (end < 0) end = n;
                Blank(i, end);
                i = end;
            }
            else if (c == '/' && next == '*')
            {
                int depth = 1, j = i + 2;
                while (j < n && depth > 0)
                {
                    if (sql[j] == '/' && j + 1 < n && sql[j + 1] == '*') { depth++; j += 2; }
                    else if (sql[j] == '*' && j + 1 < n && sql[j + 1] == '/') { depth--; j += 2; }
                    else j++;
                }
                if (depth > 0) errors.Add("A /* comment */ is not closed.");
                Blank(i, j);
                i = j;
            }
            else if (c == '\'')
            {
                // E'...' strings take backslash escapes; date'...' does not (the E must start a word).
                var backslash = i > 0 && (sql[i - 1] is 'E' or 'e') && (i < 2 || !IsWord(sql[i - 2]));
                int j = i + 1;
                var closed = false;
                while (j < n)
                {
                    if (backslash && sql[j] == '\\') { j += 2; continue; }
                    if (sql[j] == '\'')
                    {
                        if (j + 1 < n && sql[j + 1] == '\'') { j += 2; continue; }
                        closed = true;
                        j++;
                        break;
                    }
                    j++;
                }
                j = Math.Min(j, n);
                if (!closed)
                {
                    errors.Add("A 'string' is not closed.");
                    Blank(i, j);
                }
                else if (QuotedPositional.Match(sql[(i + 1)..(j - 1)]) is { Success: true } m)
                {
                    Positional(i, j - i, m.Groups[1].Value); // '?2' as in the report module's queries
                }
                else
                {
                    Blank(i, j);
                }
                i = j;
            }
            else if (c == '"')
            {
                int j = i + 1;
                var closed = false;
                while (j < n)
                {
                    if (sql[j] == '"')
                    {
                        if (j + 1 < n && sql[j + 1] == '"') { j += 2; continue; }
                        closed = true;
                        j++;
                        break;
                    }
                    j++;
                }
                if (!closed) errors.Add("A \"quoted name\" is not closed.");
                Blank(i, j);
                i = j;
            }
            else if (c == '$' && char.IsAsciiDigit(next) && (i == 0 || !IsWord(sql[i - 1])))
            {
                int j = i + 1;
                while (j < n && char.IsAsciiDigit(sql[j])) j++;
                Positional(i, j - i, sql[(i + 1)..j]);
                i = j;
            }
            else if (c == '$' && DollarTag(sql, i) is { } tag)
            {
                var close = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                var end = close < 0 ? n : close + tag.Length;
                if (close < 0) errors.Add($"A {tag} quoted string is not closed.");
                Blank(i, end);
                i = end;
            }
            else if (c == '?' && char.IsAsciiDigit(next))
            {
                int j = i + 1;
                while (j < n && char.IsAsciiDigit(sql[j])) j++;
                Positional(i, j - i, sql[(i + 1)..j]);
                i = j;
            }
            else if (c == ':' && IsNameStart(next) && (i == 0 || (sql[i - 1] != ':' && !IsWord(sql[i - 1]))))
            {
                int j = i + 1;
                while (j < n && IsWord(sql[j])) j++;
                placeholders.Add(new Placeholder(i, j - i, sql[(i + 1)..j].ToLowerInvariant()));
                Blank(i, j);
                i = j;
            }
            else
            {
                i++;
            }
        }

        var code = new string(mask);
        if (string.IsNullOrWhiteSpace(code.Replace(";", " ")))
        {
            errors.Insert(0, "The script is empty.");
        }
        else
        {
            var semicolon = code.IndexOf(';');
            if (semicolon >= 0 && !string.IsNullOrWhiteSpace(code[semicolon..].Replace(";", " ")))
                errors.Add("Only one statement is allowed: remove everything after the first ;.");

            var first = FirstWord.Match(code);
            if (!first.Success || first.Groups[1].Value.ToLowerInvariant() is not ("select" or "with"))
                errors.Add("The script must be a query that starts with SELECT or WITH.");

            var words = BlockedWord.Matches(code).Select(m => m.Value.ToUpperInvariant()).Distinct().ToList();
            if (words.Count > 0)
                errors.Add($"Not allowed in a read-only query: {string.Join(", ", words)}.");

            var functions = BlockedFunction.Matches(code).Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct().ToList();
            if (functions.Count > 0)
                errors.Add($"Not allowed: {string.Join(", ", functions)}().");
        }

        return new Analysis { Sql = sql, Errors = errors, Placeholders = placeholders };
    }

    // $$ or $tag$ at sql[i], or null when it isn't one.
    private static string? DollarTag(string sql, int i)
    {
        if (i > 0 && IsWord(sql[i - 1]))
            return null;
        var j = i + 1;
        if (j < sql.Length && IsNameStart(sql[j]))
            while (j < sql.Length && IsWord(sql[j])) j++;
        return j < sql.Length && sql[j] == '$' ? sql[i..(j + 1)] : null;
    }

    private static bool IsNameStart(char c) => char.IsAsciiLetter(c) || c == '_';
    private static bool IsWord(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}
