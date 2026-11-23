"""One driver's run through DaffyZoom, for scripts/verify-drivers.ps1: pyodbc (any ODBC driver) or
pymssql (FreeTDS).

Reads its plan from PROBE_* environment variables and prints one JSON line per step:
{"step", "seconds", "rows", "digest", "text", "error"}. A step's seconds cover the query and its
rows, not the connection. The digest covers the column names and every value as this driver
decoded it, so two runs compare only within one driver.
"""
import hashlib
import json
import os
import sys
import time

KIND = os.environ["PROBE_KIND"]               # odbc | pymssql
PAUSE = float(os.environ.get("PROBE_PAUSE", "0.5"))
BATCH = os.environ["PROBE_BATCH"]
PARAM = os.environ["PROBE_PARAM"]             # uses @fromYear
VALUES = [int(v) for v in os.environ["PROBE_VALUES"].split(",")]
STATE = os.environ["PROBE_STATE"]
WHO = os.environ["PROBE_WHO"]

if KIND == "odbc":
    import pyodbc
    MARKER = "?"
else:
    import pymssql
    MARKER = "%d"


def connect():
    if KIND == "odbc":
        return pyodbc.connect(os.environ["PROBE_CONNECT"], autocommit=True)
    return pymssql.connect(server=os.environ["PROBE_HOST"], port=os.environ["PROBE_PORT"],
                           user=os.environ["PROBE_USER"], password=os.environ["PROBE_PASSWORD"],
                           database=os.environ["PROBE_DATABASE"], appname=os.environ["PROBE_APP"],
                           tds_version=os.environ.get("PROBE_TDS", "7.4"), autocommit=True)


def emit(step, **fields):
    fields["step"] = step
    print(json.dumps(fields), flush=True)


def run(step, connection, sql, *args):
    """Runs sql, reads every row of every result set, and reports it."""
    try:
        cursor = connection.cursor()
        clock = time.perf_counter()
        cursor.execute(sql, *args) if args else cursor.execute(sql)
        digest = hashlib.sha256()
        rows = 0
        first = None
        while True:
            if cursor.description:
                digest.update(repr([d[0] for d in cursor.description]).encode())
                for row in cursor.fetchall():
                    digest.update(repr(tuple(row)).encode())
                    rows += 1
                    if first is None:
                        first = "|".join("" if v is None else str(v) for v in row)
            more = cursor.nextset()
            if not more:
                break
        seconds = time.perf_counter() - clock
        cursor.close()
        emit(step, seconds=seconds, rows=rows, digest=digest.hexdigest(), text=first)
    except Exception as ex:   # reported, not raised: the orchestrator decides what a failure means
        emit(step, error=f"{type(ex).__name__}: {ex}")


def fresh(step, sql, *args):
    """A run on its own connection, as a pooled application's requests are."""
    try:
        connection = connect()
    except Exception as ex:
        emit(step, error=f"connect: {type(ex).__name__}: {ex}")
        return
    try:
        run(step, connection, sql, *args)
    finally:
        connection.close()


param_sql = PARAM.replace("@fromYear", MARKER)

fresh("who", WHO)
for i in (1, 2):
    fresh(f"batch{i}", BATCH)
    time.sleep(PAUSE)                         # the analyzer and the filler work off the relay

try:
    connection = connect()
except Exception as ex:
    connection = None
    for step in ("stateBefore", "batch3", "stateAfter"):
        emit(step, error=f"connect: {type(ex).__name__}: {ex}")
if connection is not None:
    try:
        run("stateBefore", connection, STATE)
        run("batch3", connection, BATCH)
        run("stateAfter", connection, STATE)
    finally:
        connection.close()

for i in (1, 2, 3):
    fresh(f"param{i}", param_sql, (VALUES[0],))
    time.sleep(PAUSE)
fresh("paramOther", param_sql, (VALUES[1],))
