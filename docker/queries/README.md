# Test queries

Slow, repeatable queries against the inflated AdventureWorksDW2022 (`scripts/setup-testdb.ps1`).

## Timed queries (`q*.sql`)

`scripts/calibrate.ps1` times each query on the current machine and writes
`calibration-<machine>.txt` (git-ignored). The times below are warm runs, measured with the
container capped at 4 CPUs and 6 GB.

Several queries carry `OPTION (MAXDOP n)`. With the data in memory, the parallel plans finished in
a second or two, which is below DaffyZoom's default 5 s threshold.

| File | Measured | What it exercises |
|---|---|---|
| `q01_category_year.sql` | ~9 s | one scan, four joins, a distinct count |
| `q02_monthly_view.sql` | ~7 s | a view on a view, a scalar function |
| `q03_customer_value.sql` | ~7 s | COUNT DISTINCT per customer, TOP with demographics |
| `q04_running_totals.sql` | ~14 s | window functions over a full sort |
| `q05_cohort_retention.sql` | ~15 s | the fact joined to itself, distinct counts |
| `q06_market_basket.sql` | ~30 s | a self-join on order number |
| `q07_reseller_medians.sql` | ~26 s | PERCENTILE_CONT over the heap (no primary key) |
| `q08_procedure.sql` | ~9 s | a stored procedure (cached only when allowed by name) |
| `q09_big_result.sql` | ~10 s | a 500,000-row result |
| `q10_internet_medians.sql` | ~70 s | three PERCENTILE_CONT windows over 30M rows |
| `q11_distribution_profile.sql` | ~5.5 min | six of them |

## Refusal and correctness cases (`r*.sql`)

Each of these must either be refused or cached in a particular way. The unit tests
(`ClassifierTests.TheTestQueries`) check every file in this folder.

| File | Expectation |
|---|---|
| `r01_getdate.sql` | refused: `NonDeterministic` |
| `r02_temp_table.sql` | refused: `TempTable` |
| `r03_leading_set.sql` | cached; its `SET` statements are re-applied on every hit |
| `r04_write.sql` | refused: `Write`. In `ttl` mode it also flushes the database |
