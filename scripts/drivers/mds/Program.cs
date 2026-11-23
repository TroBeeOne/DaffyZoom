// One driver's run through DaffyZoom, for scripts/verify-drivers.ps1: Microsoft.Data.SqlClient.
//
// Reads its plan from PROBE_* environment variables and prints one JSON line per step, as
// probe.py does. PROBE_CONNECT is the whole connection string, so one build covers every variant
// (Mandatory, Strict with the gateway's certificate, MARS).
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

string Env(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} is not set");

string connect = Env("PROBE_CONNECT");
string batch = Env("PROBE_BATCH"), param = Env("PROBE_PARAM"), state = Env("PROBE_STATE");
int[] values = Env("PROBE_VALUES").Split(',').Select(int.Parse).ToArray();
int pause = (int)(double.Parse(Environment.GetEnvironmentVariable("PROBE_PAUSE") ?? "0.5") * 1000);

void Emit(object fields) => Console.WriteLine(JsonSerializer.Serialize(fields));

void Run(string step, SqlConnection connection, string sql, int? year = null)
{
    using var command = NewCommand(connection, sql, year);
    Execute(step, command);
}

SqlCommand NewCommand(SqlConnection connection, string sql, int? year)
{
    var command = new SqlCommand(sql, connection) { CommandTimeout = 300 };
    if (year is { } value) command.Parameters.Add("@fromYear", System.Data.SqlDbType.Int).Value = value;
    return command;
}

void Execute(string step, SqlCommand command)
{
    try
    {
        var clock = Stopwatch.StartNew();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long rows = 0;
        string? first = null;
        using (var reader = command.ExecuteReader())
        {
            do
            {
                var names = string.Join(",", Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));
                hash.AppendData(Encoding.UTF8.GetBytes(names));
                var row = new object[reader.FieldCount];
                while (reader.Read())
                {
                    reader.GetValues(row);
                    string line = string.Join("|", row.Select(v => v is DBNull ? "" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)));
                    hash.AppendData(Encoding.UTF8.GetBytes(line + "\n"));
                    first ??= line;
                    rows++;
                }
            } while (reader.NextResult());
        }
        Emit(new { step, seconds = clock.Elapsed.TotalSeconds, rows, digest = Convert.ToHexStringLower(hash.GetHashAndReset()), text = first });
    }
    catch (Exception ex)
    {
        Emit(new { step, error = $"{ex.GetType().Name}: {ex.Message}" });
    }
}

void Fresh(string step, string sql, int? year = null)
{
    try
    {
        using var connection = new SqlConnection(connect);
        connection.Open();
        Run(step, connection, sql, year);
    }
    catch (Exception ex)
    {
        Emit(new { step, error = $"connect: {ex.GetType().Name}: {ex.Message}" });
    }
}

Fresh("who", Env("PROBE_WHO"));
for (int i = 1; i <= 2; i++) { Fresh($"batch{i}", batch); Thread.Sleep(pause); }

try
{
    using var connection = new SqlConnection(connect);
    connection.Open();
    Run("stateBefore", connection, state);
    Run("batch3", connection, batch);
    Run("stateAfter", connection, state);
}
catch (Exception ex)
{
    Emit(new { step = "stateBefore", error = $"connect: {ex.GetType().Name}: {ex.Message}" });
}

for (int i = 1; i <= 3; i++) { Fresh($"param{i}", param, values[0]); Thread.Sleep(pause); }
Fresh("paramOther", param, values[1]);

// A prepared command (SqlCommand.Prepare): its first run is sp_prepexec, the next sp_execute by the
// handle the first one's answer carried. prepexec1..3 on a connection each; on the third, served from
// the cache, the handle must be one this session's server issued, or "execute" fails.
for (int i = 1; i <= 3; i++)
{
    try
    {
        using var connection = new SqlConnection(connect);
        connection.Open();
        using var command = NewCommand(connection, param, values[0]);
        command.Prepare();
        Execute($"prepexec{i}", command);
        if (i == 3) Execute("execute", command);
    }
    catch (Exception ex)
    {
        Emit(new { step = $"prepexec{i}", error = $"connect: {ex.GetType().Name}: {ex.Message}" });
    }
    Thread.Sleep(pause);
}
