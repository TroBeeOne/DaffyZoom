// One driver's run through DaffyZoom, for scripts/verify-drivers.ps1: tedious, the driver under
// most Node applications (the mssql package, Sequelize, TypeORM, Prisma's older adapter).
//
//   node probe.js <path to a folder with node_modules/tedious>
//
// Reads its plan from PROBE_* environment variables and prints one JSON line per step, as
// probe.py does. tedious has two ways to send a query, and both are probed: execSqlBatch (a SQL
// batch: batch1..3) and execSql (always sp_executesql, parameters or not: rpc1..3, param1..3).
'use strict';
const path = require('path');
const crypto = require('crypto');
const { Connection, Request, TYPES } = require(path.join(process.argv[2], 'node_modules', 'tedious'));

const env = process.env;
const pause = Number(env.PROBE_PAUSE || '0.5') * 1000;
const [year, other] = env.PROBE_VALUES.split(',').map(Number);

function emit(step, fields) { console.log(JSON.stringify({ step, ...fields })); }
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

function connect() {
  return new Promise((resolve, reject) => {
    const connection = new Connection({
      server: env.PROBE_HOST,
      authentication: { type: 'default', options: { userName: env.PROBE_USER, password: env.PROBE_PASSWORD } },
      options: {
        port: Number(env.PROBE_PORT), database: env.PROBE_DATABASE, appName: env.PROBE_APP,
        encrypt: true, trustServerCertificate: true, requestTimeout: 300000, rowCollectionOnRequestCompletion: false,
      },
    });
    connection.on('connect', error => (error ? reject(error) : resolve(connection)));
    connection.connect();
  });
}

// Runs one request on a connection and reports it: rows of every result set, digested in order.
function run(step, connection, sql, { rpc = false, value = undefined } = {}) {
  return new Promise(resolve => {
    const digest = crypto.createHash('sha256');
    let rows = 0, first = null, start;
    const request = new Request(sql, error => {
      const seconds = Number(process.hrtime.bigint() - start) / 1e9;
      if (error) emit(step, { error: String(error) });
      else emit(step, { seconds, rows, digest: digest.digest('hex'), text: first });
      resolve();
    });
    if (value !== undefined) request.addParameter('fromYear', TYPES.Int, value);
    request.on('columnMetadata', columns => digest.update(JSON.stringify(columns.map(c => c.colName))));
    request.on('row', columns => {
      const line = columns.map(c => (c.value === null ? '' : c.value instanceof Date ? c.value.toISOString() : String(c.value))).join('|');
      digest.update(line + '\n');
      if (first === null) first = line;
      rows++;
    });
    start = process.hrtime.bigint();
    if (rpc || value !== undefined) connection.execSql(request);
    else connection.execSqlBatch(request);
  });
}

async function fresh(step, sql, options) {
  let connection;
  try { connection = await connect(); }
  catch (error) { emit(step, { error: 'connect: ' + String(error) }); return; }
  try { await run(step, connection, sql, options); }
  finally { connection.close(); }
}

(async () => {
  await fresh('who', env.PROBE_WHO);
  for (const i of [1, 2]) { await fresh('batch' + i, env.PROBE_BATCH); await sleep(pause); }

  let connection;
  try { connection = await connect(); }
  catch (error) { emit('stateBefore', { error: 'connect: ' + String(error) }); }
  if (connection) {
    await run('stateBefore', connection, env.PROBE_STATE);
    await run('batch3', connection, env.PROBE_BATCH);
    await run('stateAfter', connection, env.PROBE_STATE);
    connection.close();
  }

  // The same query as sp_executesql with no parameters: its own key, filled and served the same way.
  for (const i of [1, 2, 3]) { await fresh('rpc' + i, env.PROBE_BATCH, { rpc: true }); await sleep(pause); }

  for (const i of [1, 2, 3]) { await fresh('param' + i, env.PROBE_PARAM, { value: year }); await sleep(pause); }
  await fresh('paramOther', env.PROBE_PARAM, { value: other });
})();
