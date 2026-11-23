# DaffyZoom

A result cache for SQL Server and Azure SQL, in front of the database as a gateway.

Some queries take minutes, repeat exactly, and read tables that change once a night. DaffyZoom
answers the repeats from an encrypted copy of the first answer. A replay that takes a second
replaces a query that took two minutes. Everything else is relayed to SQL Server byte for byte.
Applications connect to DaffyZoom as they would to SQL Server: only the connection string changes.

**Video:** [DaffyZoom Mechanics Overview](https://www.youtube.com/watch?v=eS907Xjy044) on YouTube.

## How it works

The same report query, run three times through DaffyZoom:

```
Run 1   a query takes 40 s   →  SQL Server answers. DaffyZoom notes it: slow, and safe to cache.
Run 2   the same query       →  SQL Server answers again; DaffyZoom keeps an encrypted copy.
Run 3   the same query       →  ttl mode:      answered from the cache, in milliseconds, until
                                               its max age or your next flush.
                                tracked mode:  DaffyZoom first asks SQL Server's Change Tracking
                                               whether any table the query reads has changed:
                                                 nothing changed  →  answered from the cache
                                                 a table changed  →  SQL Server answers, and that
                                                                     new answer is kept instead
Any doubt, at any step       →  SQL Server answers, as it would without DaffyZoom.
```

"Slow" means at least `--slow-ms` (5 s by default). "The same" means the same text byte for byte,
with the same parameters, login, database and session settings. A query that is never safe to
cache, such as a write or anything using `GETDATE()`, is simply relayed every time.

## Two modes

DaffyZoom has two ways of knowing a cached answer is still right, chosen per database. A database
without one is never cached.

| | `ttl`: you say when the data changed | `tracked`: SQL Server says when the data changed |
|---|---|---|
| For | tables that change only through loads you run: a warehouse loaded nightly, a reporting copy | tables that change whenever applications write to them |
| On each hit | nothing is asked of SQL Server | SQL Server is asked first, as the client's own login, whether any table the query reads has changed |
| An answer is dropped | at its max age, or when you signal a change: a flush from the ETL job, a watermark poll, a scheduled flush, a blackout window during the load, or a write seen through DaffyZoom | as soon as a table under it changes, and at its max age |
| Setup | none | Change Tracking on the database and its tables ([below](#change-tracking-for-tracked-mode)) |

**In `tracked` mode a cached answer is never served once the data under it has changed.** Every hit
first asks SQL Server's Change Tracking whether any table the query reads, including those under
its views and functions, has had a change committed since the answer was cached. If one has, or if
the question cannot be answered at all, the query goes to SQL Server, and its fresh answer is
cached in place of the old one. That check is one small query of a few milliseconds.

**Both modes fail closed.** Whenever DaffyZoom is unsure a cached answer is right, the query goes
to SQL Server. A cache hit is checked to its last byte before the first is sent, within a deadline
of at most 2 s, so falling back is invisible to the client. One command,
`EXEC daffyzoom.set_serving 0`, sends everything to SQL Server at once.

## Get started

```
dotnet tool install -g DaffyZoom      # install
dotnet tool update -g DaffyZoom       # upgrade to the latest version
daffyzoom --version                   # which version is installed
```

Three steps, on the machine DaffyZoom runs on: a login, a database to cache, and start it.

**1. A login.** Clients log in to DaffyZoom with a DaffyZoom login. DaffyZoom logs in to SQL Server
as the SQL login mapped to it, its *target*:

```
daffyzoom user add reports --target "Server=sql01;User Id=svc_reports;Initial Catalog=Sales;Encrypt=True;TrustServerCertificate=True"
```

`user add` asks for the DaffyZoom password, then the target login's password: secrets are typed,
never put on a command line. It logs in to the target before it saves anything.
`TrustServerCertificate=True` accepts SQL Server's certificate without checking who issued it,
which a server still on the self-signed certificate SQL Server makes for itself needs. If the
server's certificate is trusted on this machine, you can leave it out.

**2. A database to cache.** Nothing is cached until its database has a policy:

```
daffyzoom db set sql01/Sales --mode ttl --max-age 4h
```

`sql01/Sales` is the server and the database. The server is the SQL Server as the login's target
names it (`Server=sql01` above), not the DaffyZoom login. The database name is matched in any case.

| To | Run |
|---|---|
| cache `Sales`, each answer kept at most 4 hours | `daffyzoom db set sql01/Sales --mode ttl --max-age 4h` |
| the same, on a server on another port (`Server=sql01,14333`) | `daffyzoom db set sql01,14333/Sales --mode ttl` |
| the same, on Azure SQL Database | `daffyzoom db set myserver.database.windows.net/Sales --mode ttl` |
| check Change Tracking on every hit instead | `daffyzoom db set sql01/Sales --mode tracked` |
| change one setting later; the others stay as they are | `daffyzoom db set sql01/Sales --max-age 1h` |
| stop caching it, keeping its settings | `daffyzoom db set sql01/Sales --mode off` |
| stop caching it, and forget its settings | `daffyzoom db remove sql01/Sales` |
| see the policies | `daffyzoom db list`, or `EXEC daffyzoom.databases` in T-SQL |

Policies are set on DaffyZoom's host: from a second window while it runs, which it picks up within
a second, or at its prompt as `:db set sql01/Sales --mode ttl`. T-SQL shows them but does not
change them. Any change flushes the database's cached answers. If no login's target is on the
server you name, `db set` says so; `EXEC daffyzoom.status` shows your session's target and database.

**3. Start it.**

```
daffyzoom
```

Point any SQL Server client at DaffyZoom (port 1433, `--port` to change it, `--bind 0.0.0.0` to
listen beyond this machine) and log in as `reports`. The banner shows the connection strings to use
and the databases being cached. `EXEC daffyzoom.help` lists what you can run in T-SQL; every command
is in [Commands](#commands).

**What gets cached.** A query that runs at least `--slow-ms` (default 5 s) is filled into the cache
on its second slow run and answered from it on the third, even when the runs come back to back, as
`GO 4` in SSMS sends them. (In tracked mode runs sent back to back are served from the fourth:
its second run can only be copied once the first has been looked up in Change Tracking.) It has to repeat exactly: the
same text byte for byte, spacing, line breaks, case and comments included, with the same parameter
values, mapped login, database and session settings. Two kinds of whitespace are the exception,
because they cannot change the answer: spaces, tabs and line breaks after the end of the text, and
spaces or tabs before the start of its first line. Line breaks before the text still count, because
they shift the line numbers SQL Server reports in messages. Deciding that two otherwise different
texts mean the same thing would mean parsing T-SQL for equivalence, and a mistake there would serve
a wrong answer; reports, dashboards and ORMs send the same text every time anyway. Queries on the
clock (`GETDATE()`), writes, temporary tables, dynamic SQL and requests inside transactions are
never cached.

**Trying it in SSMS.** SSMS sends the text of the batch up to its `GO`, or only what is selected.
A query run from two windows therefore matches only if the text sent matches. A trailing blank line
or the line break before a `GO` doesn't matter; a blank line before the query does. A window that
has had Include Actual Execution Plan, Live Query Statistics, or `SET STATISTICS IO` or `TIME` on
is never served until it reconnects, because the replay would not carry the plan or the statistics.
`EXEC daffyzoom.watchlist` counts each different text as its own `exact_requests`.

## Where it fits

DaffyZoom pays off where the same slow queries run again and again over data that changes on a
schedule, or rarely:

- **Reporting and BI on data loaded in batches:** a data warehouse, a reporting copy or a replica
  loaded nightly or hourly, in `ttl` mode. The ETL job ends with `EXEC daffyzoom.flush 'Sales'`,
  or DaffyZoom polls a watermark.
- **Dashboards and scheduled reports** that many people open, each sending the same queries.
- **A server short of capacity**, licensed per core or priced per vCore, where a few repeated
  heavy queries crowd out everything else. A hit costs SQL Server nothing in `ttl` mode, and one
  small query in `tracked`.
- **Tables that change now and then but are read all day**, in `tracked` mode: answers are served
  only while nothing they read has changed.

It can be tried without risk. With no policy, nothing is cached, but the watch list
(`EXEC daffyzoom.watchlist`) still shows the slow queries that repeat, whether each could be cached,
and if not, why. For one that could, it gives the `db set` line that would cache its database.

## Where it doesn't

- **OLTP:** short queries and frequent writes. Nothing slow repeats unchanged, and every request
  still makes one more network hop, through DaffyZoom.
- **Ad hoc analysis** where every query is new: nothing repeats byte for byte.
- **Data written all day outside DaffyZoom, without Change Tracking.** `ttl` mode would serve
  answers up to their max age old. Use `tracked`, or leave the database off.
- **Read auditing in SQL Server.** Hits never reach it, so SQL Audit, Extended Events and Query
  Store do not see them. DaffyZoom logs every hit, but if an audit must come from SQL Server
  itself, do not cache that database.
- **Answers that may not be stored outside SQL Server**, even encrypted, by policy.
- **Windows or Entra authentication from the client.** Clients log in with DaffyZoom logins;
  DaffyZoom logs in to SQL Server as the login mapped to each.
- **MARS applications and the TDS 7.1 drivers built into Windows**: relayed, never cached.

Queries whose time goes into sending rows rather than finding them gain less: 500,000 rows took
10.1 s from SQL Server and 1.3 s from the cache.

## `ttl` mode: telling DaffyZoom the data changed

```sql
-- the last step of the ETL job, through DaffyZoom, as its own DaffyZoom login
EXEC daffyzoom.flush 'Sales';
```

```
# or a watermark, polled: any change to its answer flushes the database
daffyzoom db set sql01/Sales --watermark "SELECT MAX(LoadId) FROM etl.LoadStatus" --watermark-as etl --every 60s

# no serving or filling while the nightly load runs, and a flush as the window opens and closes
daffyzoom db set sql01/Sales --blackout 02:00-02:45 --flush-at 06:00
```

A write seen through DaffyZoom (`INSERT`, `UPDATE`, `DELETE`, `MERGE`, `TRUNCATE`, DDL, a bulk load)
flushes its database too. A watermark that cannot be read stops serving the database until it can.
Loads that go straight to SQL Server need one of these signals, or a short max age.

## Stored procedures

Stored procedures are not cached unless you name them. DaffyZoom decides from a query's text
whether its answer may be cached: that it only reads, and that it does not depend on the clock, a
temporary table or the session. A procedure call shows DaffyZoom only a name and parameter values;
what the procedure does is inside SQL Server. It might write, call `GETDATE()`, fill a temporary
table, build dynamic SQL, or answer differently for each caller, and DaffyZoom could not tell. So a
procedure is cached only on your word, per database:

```
daffyzoom db set sql01/Sales --allow-proc dbo.usp_SalesByTerritory,dbo.usp_TopProducts
daffyzoom db set sql01/Sales --allow-proc none        # clear the list
```

- **Name only procedures that read**, and whose answer depends only on their parameters and the
  tables they read: not on the time, the caller, or anything they write. A write inside an allowed
  procedure is not seen, so it does not flush the database.
- **`ttl` mode only.** `tracked` mode refuses procedures, because the tables they read cannot be
  known from outside them.
- **Either way of calling it** counts: an RPC by name (`CommandType.StoredProcedure` in .NET),
  or a batch that is a single `EXEC`. Names match with or without `dbo.`,
  in any case.
- **The parameter values are part of the key**, so each set of values is cached on its own. The
  usual rules still apply: slow enough, and repeated exactly.
- **Changing the list flushes the database**, as any change to its policy does. At the prompt the
  same works as `:db set sql01/Sales --allow-proc ...`; `EXEC daffyzoom.databases` shows the list.

## Change Tracking, for `tracked` mode

Change Tracking is built into SQL Server, in every edition since 2008, Express included, and into
Azure SQL Database and Azure SQL Managed Instance. Many databases have never had it switched on.
For each table it is enabled on, SQL Server records which rows changed, by primary key, and in
which version: a number the database raises with each transaction that commits a change. It keeps
no copy of the data and no old values. (That is Change Data Capture, a different and heavier
feature, which DaffyZoom does not use.)

DaffyZoom uses it to ask one question on each hit: has anything this answer was built from
changed since version *N*? SQL Server answers from its change records in a few milliseconds.

### Turning it on

Once, by the DBA. Every table a cached query reads must be tracked, including the tables under its
views and functions, and each needs a primary key.

```sql
-- the database: how long change records are kept, and their cleanup
ALTER DATABASE Sales SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = ON);

-- each table the cached queries read
ALTER TABLE dbo.FactSales   ENABLE CHANGE_TRACKING;
ALTER TABLE dbo.DimProduct  ENABLE CHANGE_TRACKING;
ALTER TABLE dbo.DimCustomer ENABLE CHANGE_TRACKING;

-- the login DaffyZoom maps clients to, which already reads the tables
GRANT VIEW DEFINITION TO report_login;
GRANT VIEW CHANGE TRACKING ON SCHEMA::dbo TO report_login;
```

```
daffyzoom db set sql01/Sales --mode tracked --max-age 4h
```

- **Retention** should be longer than `--max-age`. If cleanup removes change records an entry still
  depends on, that entry goes to SQL Server once and is cached again; it is never served unchecked.
- **`TRACK_COLUMNS_UPDATED`** is not needed. Leave it off, its default.
- **Snapshot isolation** is not needed either.
- **Refused in `tracked` mode**, with the reason on the watch list: a table without Change Tracking,
  dirty reads (`NOLOCK`, `READ UNCOMMITTED`: Change Tracking never records a change that rolls back),
  stored procedures (their tables cannot be known from outside), and databases with a row-level
  security policy.

To turn it off again: `ALTER TABLE ... DISABLE CHANGE_TRACKING` on each table, then
`ALTER DATABASE ... SET CHANGE_TRACKING = OFF`.

### Checking it from DaffyZoom

From any client connected through DaffyZoom, no admin needed:

```sql
EXEC daffyzoom.tracking 'Sales';          -- the database, and its tracked tables
EXEC daffyzoom.tracking 'Sales', 'all';   -- every table: tracked, and if not, whether it could be
```

It runs as your own mapped login, so it shows exactly what tracked mode will be able to see for
you. On the test server, for a login with the grants above (columns trimmed):

```
change_tracking  current_version  retention  auto_cleanup  daffyzoom_mode  login         view_definition  tables_tracked  tables_readable
on               27               2 days     yes           tracked         zoom_tracked  yes              9               9

table                         tracked  primary_key  min_valid_version  view_change_tracking  select  entries  state
[dbo].[DimProduct]            yes      yes          0                  yes                   yes     1        ok
[dbo].[FactInternetSalesBig]  yes      yes          0                  yes                   yes     3        ok
...

zoom_tracked can use tracked mode in [AdventureWorksDW2022]: 9 tables tracked, all readable by it.
```

With `'all'`, the tables that are not tracked say what it would take:

```
[dbo].[DimCustomer]           no       yes   ...  not tracked: ALTER TABLE [dbo].[DimCustomer] ENABLE CHANGE_TRACKING;
[dbo].[FactResellerSalesBig]  no       no    ...  not tracked; it needs a primary key before it can be
```

For a login without the grants, the messages name each one:

```
zoom_reports has no VIEW DEFINITION in [AdventureWorksDW2022], so tracked mode refuses every query
for it (it cannot follow views to their tables), ...: GRANT VIEW DEFINITION TO [zoom_reports];
zoom_reports cannot read the changes of 8 tracked tables in [dbo] (...), so their queries are never
served: GRANT VIEW CHANGE TRACKING ON SCHEMA::[dbo] TO [zoom_reports]; (or ON each table)
```

What your login cannot see is reported as unseen, never as off. If Change Tracking is off for the
database, the message gives the `ALTER DATABASE` that turns it on. If cleanup has passed the version
an entry was cached at, it says that entry will go to SQL Server once.

### What it costs SQL Server

- **Writes.** Each insert, update or delete on a tracked table also writes a row, its primary key
  and version, to an internal change table, in the same transaction. Microsoft compares the cost to
  maintaining one more index on the table. Reads are not affected.
- **Bulk loads.** Every row loaded is a change too: a nightly load of 10 million rows writes 10
  million change records, kept for the retention period. For tables that change only through such
  a load, `ttl` with a flush at the end of the load is the lighter choice.
- **Storage.** The change records, until the retention period ends and the automatic cleanup
  removes them. A short retention keeps them small; it only has to outlast `--max-age`.
- **Partition switching.** `ALTER TABLE ... SWITCH` fails on a tracked table (error 4900). If the
  warehouse loads by switching partitions, use `ttl` for that database.
- **Each hit.** One small query, as the client's login, reading the change records from the
  entry's version on: a few milliseconds.

## Clients

Checked end to end, each served from the cache and decoding exactly what SQL Server sent:
Microsoft.Data.SqlClient (also `Encrypt=Strict`), System.Data.SqlClient, ODBC Driver 17 and 18,
OLE DB (MSOLEDBSQL 19), mssql-jdbc (also `encrypt=strict`), tedious (Node), FreeTDS (pymssql),
and both sqlcmds. SSMS and DBeaver were checked by hand.

Prepared statements (`sp_prepexec`: pyodbc's parameterised queries, `SqlCommand.Prepare()`, JDBC's
reused statements) are served too: each hit first has SQL Server prepare the statement on the
client's own session, so the handle the client gets back is real. Relayed but never cached: MARS
sessions, the TDS 7.1 drivers built into Windows, and `sp_execute` by handle.

## What the SQL Server logins need

| Who | `ttl` | `tracked` |
|---|---|---|
| The mapped target login, which clients run as | nothing beyond what the application already needs | also, per database: `VIEW DEFINITION`; `VIEW CHANGE TRACKING` and `SELECT` on the tracked tables |
| The login named for a watermark poll | `SELECT` on the watermark table | not used |
| DBA, once | nothing | enable Change Tracking on the database and each table (primary key required) |

Both columns are verified end to end against SQL Server 2022. No second, service login is needed:
`tracked` mode checks as each client's own mapped login.

## Storage

Cached answers are compressed and encrypted, one file each, in `--cache-dir`. The default is
`%LOCALAPPDATA%\DaffyZoom\cache` for the account DaffyZoom runs as; the directory is emptied at every
start. The cache grows to the smaller of `--cache-max-gb` (default 10) and what its disk can spare
while leaving `--cache-min-free-gb` free (default: the larger of 1 GB and 5% of the disk). The
answers saving the least time per byte make way first, and only for answers worth more. A full disk
never fails a query: the answer simply isn't cached. A slow disk can cost hits, never time, because
a hit that cannot be read within its deadline goes to SQL Server instead.

DaffyZoom writes logs to disk only when asked (`--log-file`, `--transcript`). Those files don't pile
up either: each is deleted 24 hours after its last write (`--log-retention`). Only files DaffyZoom
named are touched. Transcripts hold SQL in the clear, which is one more reason not to keep them.

Writing the log never holds up a query. The console and the log file are each written on a thread
of their own, so a console window someone has clicked into, which Windows stops until a key is
pressed, stalls neither the sessions nor the file. One that falls far behind leaves routine lines
out rather than make anyone wait, and says how many (`log.dropped`).

## Commands

DaffyZoom is run from three places:

- **T-SQL**, from SSMS or any client connected through DaffyZoom: `EXEC daffyzoom.help`.
  DaffyZoom answers these itself; they never reach SQL Server.
- **The prompt**, in the window DaffyZoom runs in: `:help`.
- **The command line**, on the host: `daffyzoom --help`.

Admin commands need an admin login: the first is made on the host, with
`daffyzoom user admin NAME on`. What needs a restart or the host itself, such as the port, the
certificate or the cache directory, is set only on the host; `EXEC daffyzoom.settings` shows it.

### In T-SQL

Arguments go by position or by name (`@database = 'Sales'`), as for a stored procedure. Passwords
given to these commands are never logged.

**Anyone:**

| Command | What it does |
|---|---|
| `EXEC daffyzoom.help ['command']` | what you can run here, with examples; or one command in full |
| `EXEC daffyzoom.status` | your session: its id here, where it is relayed to (the server `db set` names), its SPID there, the database it is in now, how it is encrypted |
| `EXEC daffyzoom.tracking ['database'[, 'all']]` | Change Tracking as your login sees it, and the `GRANT` or `ALTER` that fixes what is missing ([above](#checking-it-from-daffyzoom)) |
| `EXEC daffyzoom.flush 'database'` | drop a database's cached answers, on the server your login maps to: the last step of an ETL job. The name is matched in any case, and if no policy caches that database, it says so. Admins may add a server (`'host:port'`), or flush everything with `'*'` |

**Admins, the cache:**

| Command | What it does |
|---|---|
| `EXEC daffyzoom.watchlist [n]` | slow queries that repeat: how often, how slow, whether each is cached, or why not (for a database without a policy, the `db set` line that would cache it), and the time saved (default 20) |
| `EXEC daffyzoom.entries [n]` | what is cached now, most useful first: the query, its size, its hits and the time they saved (default 20) |
| `EXEC daffyzoom.databases` | each database's policy, and whether it is serving now, or why not (a blackout, a failing watermark, a server no login's target is on) |
| `EXEC daffyzoom.health` | uptime, sessions, logins, and the cache's counters: hits, fills, fallbacks, flushes |
| `EXEC daffyzoom.set_serving 0` | the kill switch: every request goes to SQL Server and nothing is filled; `1` serves again |

**Admins, sessions and operation:**

| Command | What it does |
|---|---|
| `EXEC daffyzoom.sessions` | every connected session: who, relayed where, SPID, database, application, traffic, idle time |
| `EXEC daffyzoom.kill_session id` | disconnect a session, by its id in `daffyzoom.sessions` (not its SPID); SQL Server rolls back what it had open |
| `EXEC daffyzoom.settings` | how this DaffyZoom is configured, and where each setting is changed |
| `EXEC daffyzoom.log [n]` | DaffyZoom's own log: sessions, refused logins, admin commands (default 50 lines) |

**Admins, logins:**

| Command | What it does |
|---|---|
| `EXEC daffyzoom.users` | every DaffyZoom login: admin or not, enabled or not, its target, when that was last logged in to |
| `EXEC daffyzoom.add_user 'name', 'password', 'target'` | create a login and its target connection string; DaffyZoom logs in to the target first and saves nothing if that fails |
| `EXEC daffyzoom.set_user_password 'name', 'password'` | change a login's DaffyZoom password |
| `EXEC daffyzoom.set_target 'name', 'target'` | change where a login's sessions go; the target's password is kept if the new string names the same login without one |
| `EXEC daffyzoom.set_target_password 'name', 'password'` | change the password (or Entra client secret) DaffyZoom logs in to the target with |
| `EXEC daffyzoom.test_user 'name'` | log in to a login's target now, and say how it went |
| `EXEC daffyzoom.enable_user 'name'`, `disable_user 'name'` | let a login log in, or stop it; its open sessions continue (`kill_session` ends them) |
| `EXEC daffyzoom.set_admin 'name', 1` | make a login an admin (`1`), or not (`0`) |
| `EXEC daffyzoom.remove_user 'name'` | remove a login and its target; its open sessions continue until they disconnect |

### At the prompt

| Command | What it does |
|---|---|
| `:status` | the listener, sessions, logins, and the cache |
| `:sessions` (`:s`) | who is connected, where to, and how much |
| `:kill ID` | disconnect a session by its id in `:sessions` |
| `:users` | the logins and their targets |
| `:user add NAME` | create a login (prompts for passwords; tries the target before saving) |
| `:user test\|show\|passwd\|target\|enable\|disable\|remove NAME` | as `daffyzoom user ...` below |
| `:user admin NAME on\|off` | let NAME run the admin commands in T-SQL |
| `:watchlist [n]` (`:wl`) | slow queries that repeat, and whether they are or would be cached |
| `:entries [n]` | what is cached now, most useful first |
| `:db [list \| set SERVER/DB ... \| remove SERVER/DB]` | which databases are cached, and how: as `daffyzoom db` below |
| `:flush SERVER/DB \| DB \| all` | drop cached answers: one database, or everything |
| `:serve [on\|off]` | the kill switch; with nothing after it, whether it is serving and its counters |
| `:cert [trust\|untrust]` | the certificate clients see, and whether Windows trusts it |
| `:log [n\|follow]` | the last n lines of DaffyZoom's log (default 20), or follow it until a key is pressed |
| `:help`, `:quit` | this list; disconnect everyone and stop |

### On the command line

| Command | What it does |
|---|---|
| `daffyzoom [serve] [options]` | listen for clients and relay each session to its login's target (options below) |
| `daffyzoom user add NAME [--target "..."]` | create a login: prompts for its password, then the target's (unless in `--target`), logs in to the target, then saves. For Azure SQL as an Entra ID application: `Authentication=ActiveDirectoryServicePrincipal`, its client id as `User Id` |
| `daffyzoom user list`, `user show NAME` | the logins, or one of them |
| `daffyzoom user test NAME` | log in to NAME's target again, now |
| `daffyzoom user passwd NAME` | change NAME's DaffyZoom password |
| `daffyzoom user target NAME [--target "..."]` | change NAME's target, and try it |
| `daffyzoom user enable NAME`, `user disable NAME` | let NAME log in, or stop it |
| `daffyzoom user admin NAME on\|off` | let NAME run the admin commands in T-SQL |
| `daffyzoom user remove NAME` | remove NAME |
| `daffyzoom db list` | each database's policy |
| `daffyzoom db set SERVER/DB --mode ttl\|tracked\|off [options]` | cache a database, or change how (options below). Any change flushes it; the same settings again keep what is cached |
| `daffyzoom db remove SERVER/DB` | stop caching a database, and forget its policy |
| `daffyzoom cert` | the certificate clients see: where it is, its names, when it expires, whether Windows trusts it |
| `daffyzoom cert trust`, `cert untrust` | (Windows) add it to your own trusted roots, for clients that check the certificate and cannot be told not to, such as Power BI's SQL Server connector; or take it out |
| `daffyzoom --version`, `--help` | |

`SERVER` is the SQL Server as the logins' targets name it, not a DaffyZoom login: `host`, or
`host,port` off port 1433 (`sql01,14333/Sales`). `DB` is matched in any case. Examples are in
[Get started](#get-started).

**`db set` options:**

| Option | |
|---|---|
| `--mode ttl\|tracked\|off` | how the database's answers are kept right ([Two modes](#two-modes)) |
| `--max-age 4h` | the longest any answer is kept (default 4h) |
| `--idle 1h` | drop an answer not used for this long; `0` for never (default 1h) |
| `--flush-at 02:30,14:00` | flush at these times of day, local time |
| `--blackout 02:00-02:45` | no serving or filling during these windows, while a load runs, and a flush as each starts and ends |
| `--watermark "SQL"` | flush when this query's answer changes, e.g. `"SELECT MAX(LoadId) FROM etl.LoadStatus"` |
| `--watermark-as LOGIN` | the DaffyZoom login whose target runs the watermark (it needs `SELECT` on what it reads) |
| `--every 60s` | how often the watermark runs (default 60s, at least 5s) |
| `--allow-proc NAME,...` | stored procedures whose answers may be cached ([Stored procedures](#stored-procedures)) |

`none` clears `--flush-at`, `--blackout`, `--watermark` or `--allow-proc`.

**Run options** (`daffyzoom serve`):

| Option | |
|---|---|
| `--port N` | listen on N (default 1433) |
| `--bind ADDRESS` | `127.0.0.1` (default), `0.0.0.0` or `any` for every IPv4 interface, `::` for IPv4 and IPv6, or one address |
| `--allow-plaintext` | also accept clients that will not encrypt (refused by default) |
| `--cert-file FILE.pfx`, `--cert-password PW` | present this certificate instead of the generated self-signed one |
| `--regen-cert` | replace the generated certificate |
| `--machine-scope` | (Windows) protect a new store's key for the whole machine, for a service running under another account |
| `--slow-ms N` | a request at least this slow is considered for the cache (default 5000) |
| `--min-runs N` | the slow run that fills: 2 means the second fills and the third is served (default 2, at least 2) |
| `--hit-deadline-ms N` | the most a hit may take before its first byte, or it goes to SQL Server (default 2000, never more than 10% of what the query took) |
| `--cache-dir DIR` | where cached answers are kept, encrypted (emptied at every start) |
| `--cache-max-gb N` | the cache's size on disk (default 10) |
| `--cache-min-free-gb N` | what the cache always leaves free on its disk (default: the larger of 1 GB and 5%) |
| `--max-entry-mb N` | the largest answer that is cached (default 256) |
| `--fill-budget-mb N` | memory for answers being filled, all together (default 256) |
| `--memory-cache-mb N` | memory for the answers being hit, kept decrypted and shared by every hit on them, so a popular answer is read from the disk once (default: the smaller of 1024 and an eighth of memory; 0 turns it off) |
| `--check-connections N` | `tracked` mode: DaffyZoom's own connections to each target for the check on every hit, at most; more hits than that wait for one, and identical checks waiting share one (default 8) |
| `--log-file FILE` | also write the log as JSON lines; `{date}` in the name rolls it daily |
| `--log-retention 24h` | how long DaffyZoom's log files and transcripts are kept: each is deleted once nothing has been written to it for this long, and a log file without `{date}` is moved to `NAME.old` and started afresh instead (default 24h; `7d`, `90m`; `off` keeps them) |
| `--no-repl` | no prompt, just listen (automatic when output is redirected) |
| `--quiet` | log startup and warnings, not every session |
| `--trace`, `--transcript DIR` | for debugging: every decoded TDS message, or a transcript per session. Slow, and they show SQL exactly as sent, passwords included |
| `--config-dir DIR` | keep the user store, key and certificate in DIR |

## Good to know

- **DaffyZoom is in the connection path.** Its cache fails closed, but if DaffyZoom itself is down,
  clients connecting through it cannot reach SQL Server. Run it where you would run any gateway
  your applications depend on.
- **A restart empties the cache.** Its key lives only in memory, so the next slow runs fill it
  again.
- **`ttl` trusts what you tell it.** A write that bypasses DaffyZoom and sends no signal, or a write
  inside a stored procedure, goes unseen until max age. So does a permission revoked from the
  target login on SQL Server: flush the database when you revoke one. `tracked` mode checks as the
  client's login on every hit, so there revoked access ends in SQL Server's own error.
- **Across batches**, `@@ROWCOUNT` and `@@ERROR` after a hit reflect the server's last real
  statement, not the cached one.

## Tested

Against SQL Server 2022 in Docker, capped at 4 CPUs and 6 GB like a modest reporting server, with
AdventureWorksDW2022 inflated to about 42 million fact rows:

| What | Checks |
|---|---|
| Unit tests | 513 |
| The gateway, end to end: a real DaffyZoom, a real client, the real server | 19 |
| `ttl` mode: fill, serve, expiry, every flush signal, cancel during a replay | 29 |
| `tracked` mode: a change per table, refusals with their reasons, revoked access | 24 |
| Twelve drivers: a slow query, a parameterised one and, where the driver has them, a prepared one, three times each; and the two TDS 7.1 drivers built into Windows with a long login, a wrong password, a `USE` and `Encrypt=yes` | 155 |

Timed through DaffyZoom, every answer from the cache identical to SQL Server's:

| Query | At SQL Server | From the cache |
|---|---|---|
| 10 rows from 30M fact rows | 7.7 s, 7.6 s | 0.02 s |
| a parameterised query (`sp_executesql`) | 3.5 s, 3.6 s | 0.01 s |
| 500,000 rows | 10.1 s, 10.2 s | 1.3 s |
| medians over 30M rows | 77.8 s, 93.1 s | 0.01 s |
| the same 10 rows, `tracked` mode, Change Tracking checked first | 7.7 s | 0.03 s |

A demo script of eight slow reporting queries and four that are never cached, run three times in
SSMS: 4:05, 4:21 (filling), then 0:19, most of which is the four uncached queries.

What the tests found:

- **Every driver decodes a hit exactly as it decoded SQL Server**, and after every hit the session
  is as SQL Server would have left it: `@@OPTIONS`, language, `DATEFIRST`, text size, lock timeout,
  open transactions, database.
- **A change to a tracked table sends its queries to SQL Server once**, and they are cached again.
  A change to a table a query does not read leaves it served.
- **Access revoked after an answer was cached** (`DENY SELECT`) ends in SQL Server's own error 229
  in `tracked` mode, never a cached answer.
- **Prepared statements mattered more than expected.** pyodbc prepares every parameterised query,
  which covers SQLAlchemy and pandas over pyodbc; so does JDBC from a statement's second run. A
  replayed answer would carry another session's handle, so each hit has SQL Server prepare the
  statement on the client's session and hands the client that handle.
- **MARS and TDS 7.1 cannot be served safely** and are relayed: the session log says so per
  session.
- **The TDS 7.1 drivers built into Windows needed more than relaying.** Their login is laid out
  differently, and 0.1.3 could read a long one as a request to change the password and refuse it.
  Its refusals reached them as "SQL Server version 6.5 and all previous versions are no longer
  supported". 0.1.4 reads their logins, writes its refusals as SQL Server does for them, and follows
  their `USE`.

0.1.1 was also run in `ttl` mode against SQL Server 2025 on another machine: a 15 s report query of
25,000 rows was answered from the cache in under 0.1 s, and a flush sent it back to SQL Server.
0.1.2 sent that query five times back to back, as `GO 5` does: the first two runs went to SQL
Server, and the other three were answered from the cache in under 0.1 s each, the third after
waiting 46 ms for the second's answer to be saved.

0.1.3 was given 60 clients asking for one cached answer at the same moment, on the Docker server.
All 60 were served in both modes: in `tracked` mode with a median of 15 ms (0.1.2: 53 ms) and 10
logins at SQL Server for the checks (0.1.2: 61), and in `ttl` mode with a median of 4 ms (0.1.2:
19 ms).

The cache has so far been tested end to end against SQL Server 2022, and in `ttl` mode against SQL
Server 2025. Azure SQL Database as a target
(its redirects, Entra ID applications as the mapped login) comes with the front end DaffyZoom
shares with DaffyTee; the cache itself has not yet been run against it.

## How others have cached query results

SQL Server caches plans and data pages, never answers. The best-known engine that did cache
answers is MySQL, whose query cache ran from MySQL 4.0 until it was deprecated in 5.7.20 and removed
in 8.0. It matched queries byte for byte, as DaffyZoom does, and for the same reason. Where it
lived is what ended it:

| | MySQL query cache | DaffyZoom |
|---|---|---|
| Where | inside the server, in its memory, beside the buffer pool | its own process and disk; SQL Server's memory is untouched, and it can run on another machine |
| Concurrency | one lock for every lookup, insert and invalidation; under load, sessions queued on it (`Waiting for query cache lock`) | the index is a concurrent map, read without locking; each hit reads its own entry |
| What it cached | every eligible `SELECT`, once switched on | only databases you give a policy, and only queries slow enough to matter that repeat |
| Knowing data changed | every write to a table purged every result that read it, under the same lock, so busy tables kept it churning | `ttl`: your signals, max age, and writes seen; `tracked`: Change Tracking, asked on each hit, as the client's own login |
| When the cache is the problem | contention on its lock slowed every query, cached or not; the remedy was to switch it off | a hit that fails any check, or runs late, goes to SQL Server, unseen by the client; repeated failures pause serving |

MySQL turned it off by default in 5.6, its own team saying it did not scale with high-throughput
workloads on multi-core machines. DaffyZoom gives something up in return for sitting outside the
engine: every request makes one more hop, and it cannot see writes that do not pass through it,
which is why `ttl` needs your signals and `tracked` asks SQL Server.

When MySQL 8.0 dropped its query cache, its team pointed users to ProxySQL as a man-in-the-middle
cache: the same idea, outside the engine. ProxySQL caches answers for a time-to-live per query rule;
pgpool-II does the same for PostgreSQL, dropping a table's answers on writes it relays. DaffyZoom
does it for SQL Server and Azure SQL.

A gateway that speaks SQL Server's protocol is not unusual either, and not Microsoft's alone. TDS
began at Sybase in the 1980s, and SAP ASE, Sybase's own server, still speaks it. It has many
independent implementations: FreeTDS, jTDS and Tiberius on the client side; on the server side,
Babelfish for PostgreSQL, which lets PostgreSQL answer SQL Server clients. Every connection to
Azure SQL Database itself first reaches a gateway that speaks TDS.

## Protocol and trademarks

DaffyZoom is one more implementation of TDS, built from Microsoft's published [MS-TDS]
specification; it contains no Microsoft code, apart from Microsoft's MIT-licensed ScriptDom, which
parses T-SQL. It is not affiliated with, endorsed by, or sponsored by Microsoft Corporation.
Microsoft, SQL Server, Azure and Entra are trademarks of Microsoft Corporation. MySQL is a trademark
of Oracle and/or its affiliates. Other names are trademarks of their respective owners.

## License

MIT. Copyright (c) 2026 Scott Traube and TroBeeOne LLC: keep this notice and the license text with
every copy, or substantial part, of the software. Bundled dependencies keep their own licenses, listed in
`THIRD-PARTY-NOTICES.txt`: Microsoft.SqlServer.TransactSql.ScriptDom, System.IO.Hashing and
System.Security.Cryptography.ProtectedData (all MIT).

## Contact

TroBeeOne LLC: [trobee.one](https://trobee.one) ·
[LinkedIn](https://www.linkedin.com/company/trobee-one-llc) ·
source on [GitHub](https://github.com/TroBeeOne/DaffyZoom)
