using System.IO.Hashing;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DaffyZoom.Cache;

/// <summary>Why a request will not be cached. <see cref="None"/>: it may be.</summary>
internal enum Refusal
{
    None,
    ParseFailed,
    NotSelect,
    Write,
    DynamicSql,
    NonDeterministic,
    TempTable,
    TableVariable,
    ExternalData,
    SystemObject,
    SessionState,
    Procedure,
    Prepared,
    Cursor,
    MultipleCalls,
    NoMetadata,
    Encrypted,
    TooLarge,
    ResponseHadError,
    Cancelled,
    NotWorthIt,
    NotTracked,
    DirtyRead,
}

/// <summary>
/// What the analyzer concluded about a request, and its shape: the text with literals replaced by
/// ?, comments and spacing gone - the same for every run of a query whatever its values, which is
/// what the watch list groups by.
/// </summary>
internal sealed record Classification(Refusal Refusal, string? Detail, int LeadingSets, string Shape, ulong ShapeHash, string? LeadingSetText = null)
{
    public bool Cacheable => Refusal == Refusal.None;

    /// <summary>Every object the request names - tables, views, synonyms, functions - for tracked mode to resolve to its tables.</summary>
    public IReadOnlyList<ObjectReference> References { get; init; } = [];

    /// <summary>A NOLOCK or READUNCOMMITTED hint, or SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED: it may read uncommitted rows.</summary>
    public bool DirtyRead { get; init; }

    /// <summary>
    /// An sp_prepexec: its answer carries the handle SQL Server issued for the statement it prepared,
    /// which a hit must have issued afresh on the session it serves (see <see cref="PreparedStatements"/>).
    /// </summary>
    public bool Prepares { get; init; }

    public string Describe() => Cacheable ? "cacheable" : Detail is null ? Refusal.ToString() : $"{Refusal}: {Detail}";

    public static Classification Refused(Refusal refusal, string? detail, string shape) =>
        new(refusal, detail, 0, shape, Classifier.HashShape(shape));
}

/// <summary>An object a request names, as written: [database.][schema.]name, with the parts' values (unquoted).</summary>
internal sealed record ObjectReference(string? Database, string? Schema, string Name)
{
    /// <summary>The name for OBJECT_ID, the database left out: [schema].[name], or [name] to resolve as the query's own login would.</summary>
    public string Quoted => Schema is null ? Quote(Name) : $"{Quote(Schema)}.{Quote(Name)}";

    public override string ToString() => Database is null ? Quoted : $"{Quote(Database)}.{(Schema is null ? "." : Quote(Schema) + ".")}{Quote(Name)}";

    public static string Quote(string part) => "[" + part.Replace("]", "]]") + "]";
}

/// <summary>
/// Decides whether a request's text may be cached, with Microsoft's T-SQL parser, once per distinct
/// request and never on the relay. Everything not positively allowed is refused: the parser gives
/// the structure, and the rules only let through what is known to be read-only and to depend on
/// nothing but the data and the session context in the key.
///
/// Allowed: SELECT statements (with CTEs), DECLARE of scalar variables, SET @variable, PRINT, and
/// SET options - leading ones in a batch (a cache hit re-applies them), anywhere in sp_executesql
/// (whose options revert when it ends). Refused: everything else, and anything that reads the
/// clock, randomness, the session's identity or state, temporary tables, table variables, system
/// objects or another server.
/// </summary>
internal static class Classifier
{
    public static Classification Batch(string sql, bool quotedIdentifier) => Classify(sql, quotedIdentifier, isolated: false);

    /// <summary>The statement of an sp_executesql: its SET options are its own and revert when it ends.</summary>
    public static Classification Statement(string sql, bool quotedIdentifier) => Classify(sql, quotedIdentifier, isolated: true);

    public static ulong HashShape(string shape) => XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(shape));

    private static Classification Classify(string sql, bool quotedIdentifier, bool isolated)
    {
        var parser = new TSql170Parser(quotedIdentifier);
        TSqlFragment fragment;
        IList<ParseError> errors;
        using (var reader = new StringReader(sql)) fragment = parser.Parse(reader, out errors);

        string shape = Shape(fragment, sql);
        Classification Refuse(Refusal refusal, string? detail) => Classification.Refused(refusal, detail, shape);

        if (errors.Count > 0) return Refuse(Refusal.ParseFailed, $"line {errors[0].Line}: {errors[0].Message}");
        if (fragment is not TSqlScript script || script.Batches.Count != 1) return Refuse(Refusal.NotSelect, "more than one batch");

        var statements = script.Batches[0].Statements;
        int leadingSets = 0;
        bool other = false, anySelect = false;
        foreach (var statement in statements)
        {
            if (Check(statement, isolated, statements.Count, ref leadingSets, ref other, ref anySelect) is { } refused)
                return Refuse(refused.Refusal, refused.Detail);
        }
        if (!anySelect) return Refuse(Refusal.NotSelect, "no SELECT");

        var visitor = new Rules();
        fragment.Accept(visitor);
        if (visitor.Found is { } found) return Refuse(found.Refusal, found.Detail);

        // What each statement names, its own CTEs left out: a CTE's name hides a table of that name
        // only within the statement that defines it.
        var references = new List<ObjectReference>();
        foreach (var statement in statements)
        {
            var collector = new References(CteNames(statement));
            statement.Accept(collector);
            foreach (var reference in collector.Found)
                if (!references.Contains(reference)) references.Add(reference);
        }
        bool dirtyRead = visitor.DirtyRead || statements.OfType<SetTransactionIsolationLevelStatement>().Any(s => s.Level == IsolationLevel.ReadUncommitted);

        // A batch's leading SETs change the session, and a hit must change it the same way: their
        // text, exactly as sent, is what the hit sends the server on its own account.
        string? leadingSetText = null;
        if (leadingSets > 0 && !isolated)
        {
            var first = statements[0];
            var last = statements[leadingSets - 1];
            leadingSetText = sql.Substring(first.StartOffset, last.StartOffset + last.FragmentLength - first.StartOffset);
        }

        return new Classification(Refusal.None, null, leadingSets, shape, HashShape(shape), leadingSetText)
        {
            References = references, DirtyRead = dirtyRead,
        };
    }

    private static HashSet<string> CteNames(TSqlStatement statement)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (statement is StatementWithCtesAndXmlNamespaces { WithCtesAndXmlNamespaces.CommonTableExpressions: { } ctes })
            foreach (var cte in ctes) names.Add(cte.ExpressionName.Value);
        return names;
    }

    /// <summary>
    /// The objects one statement names: tables, views and synonyms in FROM, table-valued functions,
    /// and schema-qualified scalar functions (a user function is always called with its schema).
    /// A one-part name that is one of the statement's CTEs is the CTE, not an object.
    /// </summary>
    private sealed class References(HashSet<string> ctes) : TSqlFragmentVisitor
    {
        public List<ObjectReference> Found { get; } = [];

        public override void Visit(NamedTableReference node) => Add(node.SchemaObject);
        public override void Visit(SchemaObjectFunctionTableReference node) => Add(node.SchemaObject);

        public override void Visit(FunctionCall node)
        {
            // schema.function(), or database.schema.function(). (A method on a column - x.value() on
            // XML, a CLR type's method - looks the same, resolves to nothing, and is refused: safe.)
            if (node.CallTarget is MultiPartIdentifierCallTarget { MultiPartIdentifier.Identifiers: { Count: > 0 } parts })
                Add(new ObjectReference(parts.Count >= 2 ? parts[^2].Value : null, parts[^1].Value, node.FunctionName.Value));
        }

        private void Add(SchemaObjectName? name)
        {
            if (name?.BaseIdentifier?.Value is not { Length: > 0 } baseName) return;
            if (name.SchemaIdentifier is null && name.DatabaseIdentifier is null && ctes.Contains(baseName)) return;
            Add(new ObjectReference(name.DatabaseIdentifier?.Value, name.SchemaIdentifier?.Value, baseName));
        }

        private void Add(ObjectReference reference)
        {
            if (!Found.Contains(reference)) Found.Add(reference);
        }
    }

    private static (Refusal Refusal, string? Detail)? Check(
        TSqlStatement statement, bool isolated, int count, ref int leadingSets, ref bool other, ref bool anySelect)
    {
        switch (statement)
        {
            case SelectStatement select:
                if (select.Into is { } into)
                    return into.BaseIdentifier?.Value.StartsWith('#') == true
                        ? (Refusal.TempTable, $"SELECT INTO {into.BaseIdentifier.Value}")
                        : (Refusal.Write, "SELECT INTO");
                other = anySelect = true;
                return null;

            case DeclareTableVariableStatement:
                return (Refusal.TableVariable, "DECLARE @table TABLE");

            case DeclareVariableStatement or SetVariableStatement or PrintStatement:
                other = true;
                return null;

            case SetIdentityInsertStatement:
                return (Refusal.Write, "SET IDENTITY_INSERT");

            case SetUserStatement:
                return (Refusal.SessionState, "SETUSER");

            case ExecuteStatement execute:
                return Execute(execute, count);

            case UseStatement:
                return (Refusal.SessionState, "USE");

            case TransactionStatement:
                return (Refusal.SessionState, "a transaction statement");

            case DataModificationStatement modification:
                return (Refusal.Write, Readable(modification.GetType().Name));

            case WaitForStatement:
                return (Refusal.NotSelect, "WAITFOR");

            case DeclareCursorStatement or CursorStatement:
                return (Refusal.Cursor, null);

            // Session options: SET NOCOUNT ON parses as a PredicateSetStatement, SET STATISTICS as a
            // SetStatisticsStatement (both SetOnOffStatements), SET LANGUAGE and friends as a
            // SetCommandStatement. Any other Set... class the parser grows is caught by its name below.
            case SetOnOffStatement or SetTransactionIsolationLevelStatement or SetRowCountStatement or SetTextSizeStatement
                 or SetCommandStatement or SetErrorLevelStatement:
                return SessionOption(statement, isolated, other, ref leadingSets);

            default:
            {
                string name = statement.GetType().Name;
                if (name.StartsWith("Set", StringComparison.Ordinal)) return SessionOption(statement, isolated, other, ref leadingSets);

                return WritePrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal))
                    ? (Refusal.Write, Readable(name))
                    : (Refusal.NotSelect, Readable(name));
            }
        }
    }

    /// <summary>
    /// A SET option: allowed when it leads the batch (a hit re-applies it) or anywhere inside
    /// sp_executesql, unless it changes what the server sends back.
    /// </summary>
    private static (Refusal, string?)? SessionOption(TSqlStatement statement, bool isolated, bool other, ref int leadingSets)
    {
        if (ChangesWhatComesBack(statement) is { } what) return (Refusal.SessionState, what);
        if (other && !isolated) return (Refusal.SessionState, "a SET after other statements");
        leadingSets++;
        return null;
    }

    /// <summary>Statement kinds that change the database or the server, by their parser class's name.</summary>
    private static readonly string[] WritePrefixes =
    [
        "Create", "Alter", "Drop", "Truncate", "Grant", "Revoke", "Deny", "Bulk", "Backup", "Restore", "Dbcc", "Kill",
        "Checkpoint", "Reconfigure", "Shutdown", "Insert", "Update", "Delete", "Merge", "Enable", "Disable", "Add",
        "Move", "Send", "Receive", "Open", "Close", "Rename",
    ];

    /// <summary>
    /// SET options under which the server answers something other than the query's results - only
    /// metadata, a plan, statistics - or starts transactions behind the client's back.
    /// </summary>
    private static string? ChangesWhatComesBack(TSqlStatement statement)
    {
        switch (statement)
        {
            case PredicateSetStatement predicate:
            {
                string options = predicate.Options.ToString();
                foreach (string option in (string[])["FmtOnly", "NoExec", "ParseOnly", "ShowPlan"])
                    if (options.Contains(option, StringComparison.OrdinalIgnoreCase)) return $"SET {option.ToUpperInvariant()}";
                if (predicate.IsOn && options.Contains("ImplicitTransactions", StringComparison.OrdinalIgnoreCase))
                    return "SET IMPLICIT_TRANSACTIONS ON";
                return null;
            }

            case SetStatisticsStatement:
                return "SET STATISTICS";

            case SetCommandStatement command
                when command.Commands.OfType<GeneralSetCommand>()
                    .Any(c => c.CommandType.ToString().Contains("ContextInfo", StringComparison.OrdinalIgnoreCase)):
                return "SET CONTEXT_INFO";

            default:
                return null;
        }
    }

    private static (Refusal, string?) Execute(ExecuteStatement execute, int count)
    {
        var specification = execute.ExecuteSpecification;
        if (specification.LinkedServer is not null) return (Refusal.ExternalData, $"EXEC AT {specification.LinkedServer.Value}");

        switch (specification.ExecutableEntity)
        {
            case ExecutableStringList:
                return (Refusal.DynamicSql, "EXEC (string)");

            case ExecutableProcedureReference { ProcedureReference: { } reference }:
            {
                if (reference.ProcedureVariable is not null) return (Refusal.DynamicSql, "EXEC @variable");
                string name = Name(reference.ProcedureReference?.Name);
                if (name.EndsWith("sp_executesql", StringComparison.OrdinalIgnoreCase)) return (Refusal.DynamicSql, "sp_executesql inside a batch");
                return (Refusal.Procedure, count == 1 ? name : $"{name}, among other statements");
            }

            default:
                return (Refusal.NotSelect, "EXEC");
        }
    }

    /// <summary>The rules that apply anywhere in the tree, however deep: functions, variables, tables.</summary>
    private sealed class Rules : TSqlFragmentVisitor
    {
        public (Refusal Refusal, string Detail)? Found { get; private set; }

        /// <summary>A hint to read uncommitted rows. Not a refusal here: only tracked mode minds (Change Tracking never sees a rollback).</summary>
        public bool DirtyRead { get; private set; }

        private void Refuse(Refusal refusal, string detail) => Found ??= (refusal, detail);

        public override void Visit(TableHint node)
        {
            if (node.HintKind is TableHintKind.NoLock or TableHintKind.ReadUncommitted) DirtyRead = true;
        }

        public override void Visit(FunctionCall node)
        {
            if (node.CallTarget is null && NonDeterministic.Contains(node.FunctionName.Value))
                Refuse(Refusal.NonDeterministic, $"{node.FunctionName.Value.ToUpperInvariant()}()");
        }

        public override void Visit(ParameterlessCall node)
        {
            if (node.ParameterlessCallType == ParameterlessCallType.CurrentTimestamp) Refuse(Refusal.NonDeterministic, "CURRENT_TIMESTAMP");
        }

        public override void Visit(GlobalVariableExpression node)
        {
            if (!SafeGlobals.Contains(node.Name)) Refuse(Refusal.NonDeterministic, node.Name.ToUpperInvariant());
        }

        public override void Visit(OdbcFunctionCall node)
        {
            if (OdbcClock.Contains(node.Name.Value)) Refuse(Refusal.NonDeterministic, $"{{fn {node.Name.Value.ToUpperInvariant()}()}}");
        }

        public override void Visit(NextValueForExpression node) => Refuse(Refusal.NonDeterministic, "NEXT VALUE FOR");
        public override void Visit(TableSampleClause node) => Refuse(Refusal.NonDeterministic, "TABLESAMPLE");
        public override void Visit(ChangeTableChangesTableReference node) => Refuse(Refusal.NonDeterministic, "CHANGETABLE");
        public override void Visit(ChangeTableVersionTableReference node) => Refuse(Refusal.NonDeterministic, "CHANGETABLE");

        public override void Visit(VariableTableReference node) => Refuse(Refusal.TableVariable, node.Variable.Name);
        public override void Visit(OpenRowsetTableReference node) => Refuse(Refusal.ExternalData, "OPENROWSET");
        public override void Visit(BulkOpenRowset node) => Refuse(Refusal.ExternalData, "OPENROWSET(BULK)");
        public override void Visit(InternalOpenRowset node) => Refuse(Refusal.ExternalData, "OPENROWSET");
        public override void Visit(OpenQueryTableReference node) => Refuse(Refusal.ExternalData, "OPENQUERY");
        public override void Visit(OpenXmlTableReference node) => Refuse(Refusal.ExternalData, "OPENXML");
        public override void Visit(AdHocTableReference node) => Refuse(Refusal.ExternalData, "OPENDATASOURCE");
        public override void Visit(DataModificationTableReference node) => Refuse(Refusal.Write, "a data modification in FROM");
        public override void Visit(BrowseForClause node) => Refuse(Refusal.NotSelect, "FOR BROWSE");

        public override void Visit(NamedTableReference node) => Check(node.SchemaObject);
        public override void Visit(SchemaObjectFunctionTableReference node) => Check(node.SchemaObject);

        private void Check(SchemaObjectName? name)
        {
            if (name is null) return;
            if (name.ServerIdentifier is not null) { Refuse(Refusal.ExternalData, $"linked server {name.ServerIdentifier.Value}"); return; }

            string table = name.BaseIdentifier?.Value ?? "";
            string? schema = name.SchemaIdentifier?.Value;
            if (table.StartsWith('#')) Refuse(Refusal.TempTable, table);
            else if (string.Equals(name.DatabaseIdentifier?.Value, "tempdb", StringComparison.OrdinalIgnoreCase)) Refuse(Refusal.TempTable, "tempdb");
            else if (schema is not null && SystemSchemas.Contains(schema)) Refuse(Refusal.SystemObject, $"{schema}.{table}");
            else if (schema is null && CompatibilityViews.Contains(table)) Refuse(Refusal.SystemObject, table);
        }
    }

    /// <summary>
    /// Built-in functions whose answer is not a function of the data and the key's context alone:
    /// the clock, randomness, the request or transaction, permissions (which change without the data
    /// changing), server and file state, and encryption with random IVs or session-opened keys.
    /// </summary>
    private static readonly HashSet<string> NonDeterministic = new(StringComparer.OrdinalIgnoreCase)
    {
        "GETDATE", "GETUTCDATE", "SYSDATETIME", "SYSUTCDATETIME", "SYSDATETIMEOFFSET", "CURRENT_DATE", "CURRENT_TIMEZONE",
        "CURRENT_TIMEZONE_ID", "NEWID", "NEWSEQUENTIALID", "RAND", "CRYPT_GEN_RANDOM",
        "SESSION_CONTEXT", "CONTEXT_INFO", "APP_NAME", "HOST_NAME", "HOST_ID", "CURRENT_REQUEST_ID", "CURRENT_TRANSACTION_ID",
        "SCOPE_IDENTITY", "IDENT_CURRENT", "XACT_STATE", "CURSOR_STATUS", "GET_FILESTREAM_TRANSACTION_CONTEXT",
        "ERROR_LINE", "ERROR_MESSAGE", "ERROR_NUMBER", "ERROR_PROCEDURE", "ERROR_SEVERITY", "ERROR_STATE",
        "IS_MEMBER", "IS_ROLEMEMBER", "IS_SRVROLEMEMBER", "HAS_PERMS_BY_NAME", "PERMISSIONS", "HAS_DBACCESS",
        "MIN_ACTIVE_ROWVERSION", "CHANGE_TRACKING_CURRENT_VERSION", "CHANGE_TRACKING_MIN_VALID_VERSION",
        "SERVERPROPERTY", "DATABASEPROPERTYEX", "CONNECTIONPROPERTY", "SESSIONPROPERTY", "LOGINPROPERTY", "STATS_DATE",
        "FILEPROPERTY", "FILEPROPERTYEX",
        "PWDENCRYPT", "ENCRYPTBYKEY", "ENCRYPTBYPASSPHRASE", "ENCRYPTBYCERT", "ENCRYPTBYASYMKEY", "DECRYPTBYKEY",
        "DECRYPTBYKEYAUTOASYMKEY", "DECRYPTBYKEYAUTOCERT",
    };

    /// <summary>The @@ variables that are fixed for the server or follow the session settings in the key. Every other one is refused.</summary>
    private static readonly HashSet<string> SafeGlobals = new(StringComparer.OrdinalIgnoreCase)
    {
        "@@VERSION", "@@MICROSOFTVERSION", "@@SERVERNAME", "@@SERVICENAME", "@@MAX_PRECISION", "@@DATEFIRST", "@@LANGUAGE",
        "@@LANGID", "@@TEXTSIZE", "@@OPTIONS", "@@LOCK_TIMEOUT",
    };

    private static readonly HashSet<string> OdbcClock = new(StringComparer.OrdinalIgnoreCase)
    {
        "NOW", "CURDATE", "CURTIME", "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "RAND",
    };

    private static readonly HashSet<string> SystemSchemas = new(StringComparer.OrdinalIgnoreCase) { "sys", "INFORMATION_SCHEMA" };

    /// <summary>SQL Server 2000's system tables, still answered as views, which name no schema.</summary>
    private static readonly HashSet<string> CompatibilityViews = new(StringComparer.OrdinalIgnoreCase)
    {
        "sysobjects", "syscolumns", "sysindexes", "sysindexkeys", "sysusers", "sysmembers", "sysprocesses", "sysdatabases",
        "syslogins", "syscomments", "sysfiles", "sysfilegroups", "syslocks", "sysreferences", "systypes", "sysconstraints",
        "sysdepends", "sysforeignkeys", "syspermissions", "sysprotects", "sysservers", "sysperfinfo", "syscacheobjects",
        "sysconfigures", "syscurconfigs", "syslanguages", "sysmessages", "sysaltfiles", "syscharsets", "sysdevices",
        "sysoledbusers", "sysremotelogins", "sysfulltextcatalogs",
    };

    /// <summary>
    /// The text as the parser saw it: literals as ?, comments and spacing gone, keywords upper case,
    /// names lower case. Runs of a query with different values share it.
    /// </summary>
    private static string Shape(TSqlFragment? fragment, string sql)
    {
        if (fragment?.ScriptTokenStream is not { Count: > 0 } tokens)
            return string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        var shape = new StringBuilder(sql.Length);
        foreach (var token in tokens)
        {
            switch (token.TokenType)
            {
                case TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment or TSqlTokenType.EndOfFile:
                    continue;
                case TSqlTokenType.Integer or TSqlTokenType.Numeric or TSqlTokenType.Real or TSqlTokenType.Money
                     or TSqlTokenType.AsciiStringLiteral or TSqlTokenType.UnicodeStringLiteral or TSqlTokenType.HexLiteral:
                    shape.Append('?');
                    break;
                case TSqlTokenType.Identifier or TSqlTokenType.QuotedIdentifier or TSqlTokenType.Variable:
                    shape.Append(token.Text.ToLowerInvariant());
                    break;
                default:
                    shape.Append(token.Text.ToUpperInvariant());
                    break;
            }
            shape.Append(' ');
        }
        return shape.ToString().TrimEnd();
    }

    private static string Name(SchemaObjectName? name) =>
        name is null ? "?" : string.Join('.', name.Identifiers.Select(i => i.Value));

    /// <summary>"InsertStatement" as "INSERT", "CreateTableStatement" as "CREATE TABLE".</summary>
    private static string Readable(string typeName)
    {
        string name = typeName.EndsWith("Statement", StringComparison.Ordinal) ? typeName[..^"Statement".Length] : typeName;
        var words = new StringBuilder();
        foreach (char c in name)
        {
            if (char.IsUpper(c) && words.Length > 0) words.Append(' ');
            words.Append(char.ToUpperInvariant(c));
        }
        return words.ToString();
    }
}
