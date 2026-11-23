# DaffyZoom

A result cache for SQL Server and Azure SQL, in front of the database as a gateway.

Some queries take minutes, repeat exactly, and read tables that change once a night. DaffyZoom
answers the repeats from an encrypted copy of the first answer. A replay that takes a second
replaces a query that took two minutes. Everything else is relayed to SQL Server byte for byte.

Clients connect to DaffyZoom as if it were SQL Server. It authenticates them with its own logins
and logs in to the target as the identity mapped to each one (a SQL login, or an Entra ID
application for Azure SQL). That front end is DaffyTee's, unchanged.

**It fails closed.** Whenever DaffyZoom is unsure a cached answer is right, it sends the query to
SQL Server. Everything that could go wrong on a cache hit is checked before the first byte is sent,
within a deadline, so falling back is invisible to the client.

**Install** it as a .NET tool from [nuget.org](https://www.nuget.org/packages/DaffyZoom):

```
dotnet tool install -g DaffyZoom
```

The package's page, [NUGET.md](NUGET.md), is the user guide: setup, both modes, every command.
This README is for working on the source.

- [Status](#status) · [Quick start](#quick-start) · [Caching a database](#caching-a-database) · [Freshness modes](#freshness-modes)
- [Clients](#clients) · [SQL Server permissions](#sql-server-permissions) · [Testing](#testing) · [Layout](#layout) · [Licence](#licence)

## Status

Built in phases:

| Phase | | State |
|---|---|---|
| 0 | Gateway: DaffyTee's front end, login mapping and relay, without capture | **done** |
| 1 | Observe: find slow, exactly repeating queries; classify them; watch list; "would have served" | **done** |
| 2 | `ttl` mode: fill, encrypted store, replay, expiry, flush signals | **done**: 19 gateway and 29 cache checks end to end |
| 3 | `tracked` mode: Change Tracking checked on every hit | **done**: 24 checks end to end; 513 unit tests in all |
| 4 | First release: both modes, documentation, packaging | **released**: 0.1.0 on nuget.org; 12 drivers checked end to end, prepared statements (`sp_prepexec`) served ([Clients](#clients)); SSMS and DBeaver by hand. 0.1.1: database names matched in any case, whitespace around a batch ignored, log files and transcripts deleted after 24 h, and clearer setup. 0.1.2: runs sent back to back (`GO 4`) served from the third, and a `db set` that changes nothing keeps the cache. 0.1.3: popular answers shared in memory, tracked mode's checks capped and shared, and a log that never holds up a query. 0.1.4: the TDS 7.1 drivers built into Windows connect whatever their login holds, and are refused readably. Left for later: `:watch` |

Measured end to end against the test server (`scripts/verify-cache.ps1`), the client being
System.Data.SqlClient with pooled connections; each answer from the cache is identical to SQL
Server's:

| Query | Runs at SQL Server | From the cache |
|---|---|---|
| q01, 10 rows from 30M fact rows | 7.7 s, 7.6 s | 0.02 s |
| q03, distinct counts over a join | 7.6 s, 7.7 s | 0.01 s |
| q09, 500,000 rows | 10.1 s, 10.2 s | 1.3 s (1.5 s at packet size 512) |
| a parameterised query (sp_executesql) | 3.5 s, 3.6 s | 0.01 s |
| a batch with leading `SET`s | 1.2 s, 1.3 s | 0.01 s, the `SET`s still applied to the session |
| q10, medians over 30M rows (`-Long`) | 77.8 s, 93.1 s | 0.01 s |

In `tracked` mode (`scripts/verify-tracked.ps1`) every hit first asks SQL Server whether anything
it reads changed: q01 is served in 0.03 s with that check, and the view on a view (q02) in 0.03 s
against 5 s. After a row of `DimProduct` changes, q01 goes to SQL Server once and is cached again.

## Caching a database

Nothing is cached until a database is given a policy:

```
daffyzoom db set 127.0.0.1,14340/AdventureWorksDW2022 --mode ttl --max-age 4h
daffyzoom db list
```

The server is the SQL Server as the logins' targets name it (`host`, or `host,port` off 1433), not
a DaffyZoom login: `db set` warns when no login's target is on it, and so do the startup banner and
`EXEC daffyzoom.databases`. The database name is matched in any case, for policies and flushes
alike, since a session's database is spelled as its client spelled it; the cache key keeps the
spelling, so answers stay apart on a case-sensitive server. Policies live in the encrypted store,
so a DaffyZoom already running picks a change up within a second. Any change to a database's policy
ends what was cached for it. `daffyzoom db` lists the options, with examples: `--idle`,
`--flush-at`, `--blackout`, `--watermark`, `--allow-proc`.

**What gets cached.** A request is considered once it runs for at least `--slow-ms` (default
5000). It has to repeat exactly: the same text byte for byte (spacing, line breaks, case and
comments included), the same parameter values, the same mapped login, database and session
settings. In a SQL batch, whitespace that cannot change the answer is left out of the key:
whitespace at its end, and spaces or tabs before its first line. Line breaks before its first
line still count, because they move the line numbers in the messages a hit replays
(`CacheKey.Body`). Its second slow run with a clean answer fills the cache, and the third is
answered from it - also when `GO 4` in SSMS sends the runs back to back. Its next run comes in a
millisecond after the last one ends, before the analyzer has recorded that run and before the filler
has written its answer, so in `ttl` mode a run is copied early, while the run before it is still
being analyzed, and kept only if that run makes it one to keep; and a run arriving while its answer
is being saved waits for it, briefly. In `tracked` mode, runs sent back to
back are served from the fourth. Shared across DaffyZoom logins that map to the
same target login, never across target logins. The analyzer refuses what may not be cached, saying why
(`:watchlist`, `EXEC daffyzoom.watchlist`): writes, temporary tables, dynamic SQL, the clock and
the like ([Freshness modes](#freshness-modes)).

**A hit.** The entry is decrypted and verified to its last byte within the hit deadline: 2 s,
never more than 10% of what the query took (`--hit-deadline-ms`). Anything that fails or runs late
goes to SQL Server instead, unseen by the client. The first hit leaves the answer decrypted in
memory, and the hits after it share that copy rather than each reading the disk again. The server
still gets what it must do for the session, on a request of DaffyZoom's own: a pooled connection's
reset, a batch's leading `SET`s. A cancel during a replay is acknowledged as SQL Server would. Five
failed hits in a minute pause serving, from 30 s up to 10 min.

**Telling DaffyZoom that data changed** (`ttl` mode):

```sql
-- the last step of the ETL job, as its own DaffyZoom login (no admin needed for its own server)
EXEC daffyzoom.flush 'AdventureWorksDW2022';
```

```
# a watermark, polled as a mapping: any change to its answer flushes. A watermark that cannot be
# read stops serving the database until it can, and then flushes it.
daffyzoom db set 127.0.0.1,14340/AdventureWorksDW2022 --watermark "SELECT MAX(LoadId) FROM etl.LoadStatus" --watermark-as etl --every 60s

# no serving or filling while the nightly load runs; a flush as the window opens and closes
daffyzoom db set 127.0.0.1,14340/AdventureWorksDW2022 --blackout 02:00-02:45 --flush-at 06:00
```

A write seen through DaffyZoom also flushes its database: `INSERT`, `UPDATE`, `DELETE`, `MERGE`,
`TRUNCATE`, DDL, a bulk load. Loads that go straight to SQL Server need one of the signals above,
or a short max age.

**Looking at it**, at the prompt or in T-SQL:

| Prompt | T-SQL | |
|---|---|---|
| `:db` | `EXEC daffyzoom.databases` | each policy, and whether it is serving now, or why not (blackout, a failing watermark) |
| `:entries` | `EXEC daffyzoom.entries` | what is cached: size, hits, time saved |
| `:watchlist` | `EXEC daffyzoom.watchlist` | slow query shapes, and what the cache does with each, or why it refuses |
| | `EXEC daffyzoom.tracking ['DB'[, 'all']]` | Change Tracking as your own login sees it: on or off, versions, retention, the tracked tables (`'all'`: every table, and whether it could be), what your login may read, and the `GRANT` or `ALTER` that fixes what is missing. No admin needed |
| `:serve` | `EXEC daffyzoom.health` | whether it is serving, and its counters: hits, fills, fallbacks, flushes |
| `:serve off` | `EXEC daffyzoom.set_serving 0` | the kill switch: everything goes to SQL Server, at once |
| `:flush DB` | `EXEC daffyzoom.flush 'DB'` | drop a database's entries |

**Where it is kept.** In `--cache-dir`, one file per entry, compressed and encrypted (AES-256-GCM)
with a key that lives only in memory. The default is `cache` under the config directory:
`%LOCALAPPDATA%\DaffyZoom\cache` for the account DaffyZoom runs as, or `~/.local/share/DaffyZoom/cache`
on Linux. The directory is emptied at every start, so a restart begins with an empty cache.

**How large it gets.** The cache may grow to the smaller of two limits:
- `--cache-max-gb` (default 10);
- what its disk can spare while leaving `--cache-min-free-gb` free. By default that's the larger of
  1 GB and 5% of the disk, whatever else is filling it.

Every 30 s, entries past their max age or idle time go. Above 90% of that limit, the entries that
save the least time per byte go, down to 75%. A new answer makes room the same way before it is
written, but only by displacing entries worth less than itself. If the disk fills up anyway (other
software wrote to it), DaffyZoom frees room and tries once more. If that fails too, the answer is
not cached, and the query that produced it was never affected. A disk short of space is logged at
most every ten minutes. Answers larger than `--max-entry-mb` (default 256) are not cached.

**On slower disks.** The size of the cache does not slow it down: the index is in memory, and a hit
reads just its own entry. A hit must be read and verified within its deadline (at most 2 s, and at
most 10% of the original run time), or the query goes to SQL Server. So a slow disk can cost hits,
never time. Answers being hit are kept decrypted in memory, so a popular one is read from the disk
once: up to `--memory-cache-mb` in all (default: the smaller of 1 GB and an eighth of memory), at
most a quarter of that each, the least recently hit making room. Other answers up to 8 MB are
replayed from memory after their check; larger ones are streamed from the disk.
On spinning disks, prefer an SSD for `--cache-dir`, or lower `--max-entry-mb` (64, say).

## Quick start

From source, against the test server (see [Testing](#testing)):

```
dotnet run --project src/DaffyZoom -- user add bob --target "Server=tcp:127.0.0.1,14340;User Id=zoom_reports;Initial Catalog=AdventureWorksDW2022;TrustServerCertificate=True"
dotnet run --project src/DaffyZoom
```

`user add` asks for Bob's DaffyZoom password, and for the target password the connection string
leaves out. Secrets are typed, never put on a command line. The target login is tried before
anything is saved. Then point any SQL Server client at `tcp:127.0.0.1,1433` as `bob`, with
`Encrypt=Mandatory;TrustServerCertificate=True`.

Users, their targets, the certificate, the prompt (`:help`) and the `daffyzoom.*` commands in T-SQL
(`EXEC daffyzoom.help`) are described in full in [NUGET.md](NUGET.md#commands).

## Freshness modes

Set per target server and database. Every cached entry also has a maximum age, in every mode.

| Mode | How DaffyZoom knows a cached answer is still right | Setup |
|---|---|---|
| `off` (default) | nothing is cached | none |
| `ttl` | you say so: the tables change only through loads you can signal. Max age, plus a flush from the ETL job (`EXEC daffyzoom.flush 'ReportDb'`), a watermark poll, scheduled flushes, blackout windows during loads, and writes seen through DaffyZoom | none |
| `tracked` | SQL Server's Change Tracking, checked on every hit in one round trip of a few milliseconds | the DBA enables Change Tracking; tables need primary keys |

Always refused, in every mode:
- queries whose answer depends on when they run (`GETDATE()`, `NEWID()` and the like);
- writes, temporary tables, dynamic SQL;
- requests inside a transaction, on MARS sessions, or in sessions that changed identity or context
  (`EXECUTE AS`, `sp_set_session_context`).

### `tracked` mode

```
daffyzoom db set 127.0.0.1,14340/AdventureWorksDW2022 --mode tracked --max-age 4h
```

The first slow run of a query is resolved to its tables in the background, as the client's own
mapped login: views, functions and synonyms are followed through `sys.sql_expression_dependencies`
to the tables underneath. Its next slow run fills, with the database's Change Tracking version read
before that run went to the server as its baseline. Each hit then asks SQL Server, as the same
login, in one small query of a few milliseconds:
- whether any of those tables changed since the baseline (`CHANGETABLE(CHANGES ...)`);
- whether that record is still complete (`CHANGE_TRACKING_MIN_VALID_VERSION`);
- whether any of the objects was altered, dropped or recreated (`modify_date`).

Anything other than "nothing changed", including an error, sends the request to SQL Server. A
change to a table a query does not read leaves it served, which `ttl` mode cannot tell apart.
Writes through DaffyZoom do not flush a tracked database, since Change Tracking sees them.

Also refused in `tracked` mode, with the reason on the watch list:
- a table without Change Tracking (a heap: Change Tracking needs a primary key), or another database;
- a definition the login cannot see, or a name resolved per caller;
- dirty reads (`NOLOCK`, `READ UNCOMMITTED`): Change Tracking never records a change that rolls back;
- a database with an enabled row-level security policy;
- stored procedures, whose tables cannot be known from outside them.

**Checking a database's Change Tracking**, as your own login sees it, from any client:

```sql
EXEC daffyzoom.tracking 'AdventureWorksDW2022'          -- the database, and its tracked tables
EXEC daffyzoom.tracking 'AdventureWorksDW2022', 'all'   -- every table: tracked, and if not, whether it could be
```

The first result set covers the database:
- whether Change Tracking is on, its retention and auto-cleanup;
- its current version, and the last one DaffyZoom saw;
- DaffyZoom's mode for it, your login and database user;
- `VIEW DEFINITION`, row-level security, and how many tracked tables your login can read.

The second has a row per table:
- whether it is tracked, and whether it has a primary key;
- its minimum valid version;
- whether your login has `VIEW CHANGE TRACKING` and `SELECT` on it;
- how many cached entries depend on it, and the oldest version any of them was filled at.

What your login cannot see is reported as unseen, not as off. The messages say what stands in the
way and give the statement that removes it: the `GRANT` for a missing permission, the
`ALTER DATABASE` that turns Change Tracking on, or a warning that cleanup has passed an entry's
starting version, so that entry will go to SQL Server once.

## Clients

Every driver below runs through DaffyZoom against SQL Server 2022 in `scripts/verify-drivers.ps1`:
a slow query, a slow parameterised one and, where the driver has them, a prepared statement, three
times each, the third answered from the cache and identical to SQL Server's own answer as that
driver decodes it. Served is what DaffyZoom's own hit counter says, and the timings agree. After
every hit the session is as SQL Server would have left it (`@@OPTIONS`, text size, language,
`DATEFIRST`, lock timeout, open transactions, database).

| Driver | TDS | Client encryption | Query | Parameterised | Prepared |
|---|---|---|---|---|---|
| Microsoft.Data.SqlClient 6.1 (EF Core, most .NET) | 7.4 | Mandatory | served | served | served [^handle] |
| the same, `Encrypt=Strict;ServerCertificate=daffyzoom.cer` | 8.0 | Strict, TLS 1.3 | served | served | served |
| the same, `MultipleActiveResultSets=True` | 7.4 | Mandatory | relayed | relayed | relayed |
| System.Data.SqlClient (.NET Framework 4.8) | 7.4 | Mandatory | served | served | |
| ODBC Driver 18 (pyodbc), also `Encrypt=strict` | 7.4 | Mandatory; Strict, TLS 1.3 | served | served, as `sp_prepexec` [^pyodbc] | |
| ODBC Driver 17 (pyodbc) | 7.4 | Mandatory | served | served, as `sp_prepexec` | |
| OLE DB: MSOLEDBSQL 19 | 7.4 | Mandatory | served | served | |
| mssql-jdbc 13.4, also `encrypt=strict` | 7.4; 8.0 | Mandatory; Strict, TLS 1.3 | served | served | served [^handle] |
| tedious 20.3 (Node: mssql, Sequelize, TypeORM) | 7.4 | Mandatory | served, as a batch and as `sp_executesql` | served | |
| pymssql 2.4 (FreeTDS 1.5) | 7.4 | upgraded to TLS | served | served (FreeTDS sends the values inline) | |
| sqlcmd 15 (ODBC) and sqlcmd 1.10 (Go) | 7.4 | Mandatory | served | | |
| Windows' own "SQL Server" ODBC driver and SQLOLEDB | 7.1 | upgraded to TLS | relayed | relayed | |

[^handle]: `SqlCommand.Prepare()`, and a JDBC `PreparedStatement` from its second run: the driver
    sends `sp_prepexec`, which is served, and then runs the statement by the handle in that answer
    (`sp_execute`), which goes to SQL Server. It works because a hit gets its handle from SQL Server
    for the session it serves (see below).

[^pyodbc]: pyodbc prepares every parameterised query, so ODBC sends each one as `sp_prepexec`:
    SQLAlchemy's and pandas' parameterised queries over pyodbc are served this way.

**Prepared statements.** An `sp_prepexec` answer holds one thing that belongs to its session: the
handle SQL Server issued for the statement, which the client later runs (`sp_execute`) or frees
(`sp_unprepare`). A hit first prepares the same statement on the client's session with an
`sp_prepare` of DaffyZoom's own, made of the client's own arguments, and the replay carries the
handle SQL Server answers with. If SQL Server will not prepare it, the request goes to SQL Server
as it came. `sp_execute` by handle is not cached.

**Relayed** means every request goes to SQL Server byte for byte and the client works as it would
without DaffyZoom; nothing it sends is cached:

- **MARS** sessions: their requests travel in SMP frames whose sequence numbers SQL Server keeps.
- **TDS before 7.3**, which the drivers built into Windows speak (they get `date` and `time` columns
  as text): the cache answers 7.3 and later only. A `USE` in such a session is still followed, so
  a write after it flushes its database in `ttl` mode. These drivers cannot be told to trust a
  self-signed certificate either: connect them without encryption
  options, and DaffyZoom upgrades the connection to TLS anyway (or trust the certificate with
  `daffyzoom cert trust`). With `Encrypt=yes` they hang up after the TLS handshake, and DaffyZoom's
  log says so (`session.closed_early`). Before 0.1.4 a long login from them (a long application
  name or database) could be refused as a password change, and any refusal reached them as "SQL
  Server version 6.5 and all previous versions are no longer supported".

Each session's TDS version and encryption are in DaffyZoom's log (`session.open`), with "relayed
only" when the cache will never answer it; the watch list says why a query is not cached.

## SQL Server permissions

Both columns are what the end-to-end checks run with, against SQL Server 2022: `zoom_reports`
(`db_datareader`) in `ttl` mode, and `zoom_tracked` (`db_datareader` plus the grants below) in
`tracked` mode (`docker/seed/tracked.sql`). A login without the `tracked` grants is never served,
and the watch list says which grant it lacks.

| Who | `ttl` | `tracked` |
|---|---|---|
| The mapped target login, which clients run as | nothing beyond what the application already needs | also: `VIEW DEFINITION` on the database; `VIEW CHANGE TRACKING` on the tracked tables or their schema; `SELECT` on them, as the queries already need |
| The login named for a watermark poll | `SELECT` on the watermark table | not used |
| DBA, once | nothing | `ALTER DATABASE ... SET CHANGE_TRACKING = ON`; `ALTER TABLE ... ENABLE CHANGE_TRACKING` per table (primary key required) |

```sql
-- tracked mode, for a reporting login that already reads the tables
GRANT VIEW DEFINITION TO report_login;
GRANT VIEW CHANGE TRACKING ON SCHEMA::dbo TO report_login;
```

No second, service login is needed. In `tracked` mode DaffyZoom checks freshness as the client's
own mapped login, so access revoked after an answer was cached still ends in SQL Server's own
permission error.

## Testing

```
powershell -File scripts/setup-testdb.ps1     # SQL Server 2022 in Docker (port 14340), AdventureWorksDW2022 inflated to ~42M fact rows, Change Tracking on
powershell -File scripts/calibrate.ps1        # how long each test query takes on this machine
dotnet test                                   # unit tests
powershell -File scripts/verify-relay.ps1     # the gateway: a real DaffyZoom, a real client, the real test server
powershell -File scripts/verify-cache.ps1     # the cache in ttl mode, its flush signals, cancel; -Long adds a 70 s query
powershell -File scripts/verify-tracked.ps1   # tracked mode: Change Tracking checked on every hit, refusals, revoked access
powershell -File scripts/verify-drivers.ps1   # every SQL Server driver found on this machine, through DaffyZoom (see Clients)
powershell -File scripts/try-gateway.ps1      # a throwaway DaffyZoom with its prompt, for trying SSMS, DBeaver and the like by hand
```

`verify-drivers.ps1` runs the probes in `scripts/drivers/`. It installs pyodbc, pymssql and tedious
into a temporary tools folder on first use, finds Java and the JDBC jar on `PATH` or in DBeaver's
folders, and skips whatever is missing.

The test container is capped at 4 CPUs and 6 GB, so its queries take seconds to minutes as they
would on a modest reporting server. `docker/queries/` holds the timed queries (5 s to 2+ min) and
the cases that must be refused. See [its README](docker/queries/README.md).

## Layout

```
src/DaffyZoom/
  Tds/        wire format: framing, PRELOGIN, LOGIN7, TLS shim, requests and responses, SMP (MARS)
  Proxy/      listener, front session (handshake, auth), upstream connector, relay, sessions
  Cache/      the observer on each session, session context, keys, the classifier (ScriptDom), the
              analyzer and the watch list; policies, fills, the encrypted store, the index, replay,
              the sweeper and the flush signals
  Security/   the encrypted user store, password hashing, key protection
  Commands/   daffyzoom.* in T-SQL
  Cli/ Repl/  command line and the prompt
tests/DaffyZoom.Tests/   unit tests
docker/                  the test SQL Server, its seed scripts and queries
scripts/                 setup, calibration and end-to-end checks
scripts/drivers/         one probe per driver family (Python, Java, Node, .NET), run by verify-drivers.ps1
NUGET.md                 the package's page on nuget.org, and the user guide
```

## Licence

[MIT](LICENSE.txt). Copyright (c) 2026 Scott Traube and TroBeeOne LLC. Use it, change it and ship
it freely; the licence's one condition is that this copyright notice and the licence text stay with
every copy, or substantial part, of the software. Third-party components keep their own licences:
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

DaffyZoom implements the open [MS-TDS] specification. Its implementation of the protocol is its
own and contains no Microsoft code; Microsoft's MIT-licensed ScriptDom parses T-SQL. It is not
affiliated with or endorsed by Microsoft. SQL Server, Azure and Entra are trademarks of Microsoft
Corporation.
