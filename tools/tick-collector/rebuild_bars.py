#!/usr/bin/env python3
"""Rebuild 1-minute bars from the raw ticks table in DuckDB.

The live aggregator can be disturbed by late ticks around minute boundaries;
the ticks table is append-only and is the source of truth. Run this after the
collector has stopped (the DuckDB file is write-locked while it runs).

Usage:
    python tools/tick-collector/rebuild_bars.py --db data/bars_live.duckdb
"""

import argparse
import sys

import duckdb


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--db", default="data/bars_live.duckdb")
    parser.add_argument("--keep-old", action="store_true", default=True)
    parser.add_argument("--no-keep-old", dest="keep_old", action="store_false")
    args = parser.parse_args()

    con = duckdb.connect(args.db)
    try:
        tables = {r[0] for r in con.execute("SHOW TABLES").fetchall()}
        if "ticks" not in tables:
            print(f"ticks table not found in {args.db}", file=sys.stderr)
            return 2

        if "bars_1m" in tables:
            if "bars_1m_live_old" in tables:
                con.execute("DROP TABLE bars_1m_live_old")
            if args.keep_old:
                con.execute("CREATE TABLE bars_1m_live_old AS SELECT * FROM bars_1m")
                print("saved previous live bars to bars_1m_live_old")

        con.execute("DROP TABLE IF EXISTS bars_1m_rebuilt")
        con.execute(
            """
            CREATE TABLE bars_1m_rebuilt AS
            WITH src AS (
                SELECT
                    instrument,
                    instrument_ctp,
                    exchange,
                    trading_day,
                    ts,
                    last_price,
                    volume,
                    turnover,
                    open_interest,
                    date_trunc('minute', ts) AS bar_time,
                    lag(volume) OVER (
                        PARTITION BY instrument ORDER BY ts, volume
                    ) AS prev_vol,
                    lag(turnover) OVER (
                        PARTITION BY instrument ORDER BY ts, volume
                    ) AS prev_turnover
                FROM ticks
                WHERE instrument IS NOT NULL
                  AND ts IS NOT NULL
                  AND last_price IS NOT NULL
            ),
            agg AS (
                SELECT
                    instrument,
                    min(instrument_ctp) AS instrument_ctp,
                    min(exchange) AS exchange,
                    min(trading_day) AS trading_day,
                    bar_time,
                    arg_min(last_price, ts) AS open,
                    max(last_price) AS high,
                    min(last_price) AS low,
                    arg_max(last_price, ts) AS close,
                    sum(
                        CASE
                            WHEN prev_vol IS NULL THEN 0
                            WHEN volume < prev_vol THEN volume
                            ELSE volume - prev_vol
                        END
                    ) AS volume,
                    sum(
                        CASE
                            WHEN prev_turnover IS NULL THEN 0
                            WHEN turnover < prev_turnover THEN turnover
                            ELSE turnover - prev_turnover
                        END
                    ) AS turnover,
                    arg_max(open_interest, ts) AS open_interest,
                    count(*) AS tick_count,
                    max(ts) AS updated_at
                FROM src
                GROUP BY instrument, bar_time
            )
            SELECT
                instrument,
                instrument_ctp,
                exchange,
                trading_day,
                bar_time,
                open,
                high,
                low,
                close,
                volume,
                turnover,
                open_interest,
                tick_count,
                updated_at
            FROM agg
            """
        )

        con.execute("BEGIN")
        con.execute("DROP TABLE IF EXISTS bars_1m")
        con.execute("ALTER TABLE bars_1m_rebuilt RENAME TO bars_1m")
        con.execute(
            "CREATE UNIQUE INDEX IF NOT EXISTS bars_1m_pk "
            "ON bars_1m(instrument, bar_time)"
        )
        con.execute("COMMIT")

        bars = con.execute("SELECT count(*) FROM bars_1m").fetchone()[0]
        unique_bars = con.execute(
            "SELECT count(*) FROM (SELECT DISTINCT instrument, bar_time FROM bars_1m)"
        ).fetchone()[0]
        ticks = con.execute("SELECT count(*) FROM ticks").fetchone()[0]
        print(f"rebuilt bars_1m: rows={bars}, unique={unique_bars}, ticks={ticks}")
        if bars != unique_bars:
            print("WARNING: duplicate (instrument, bar_time) rows remain", file=sys.stderr)

        # Volume reconciliation: sum of bar volume should equal the last
        # cumulative volume minus the first cumulative volume per instrument,
        # except for explicit resets (negative deltas).
        bad = con.execute(
            """
            WITH bar_sum AS (
                SELECT instrument, sum(volume) AS bar_volume
                FROM bars_1m
                GROUP BY instrument
            ),
            tick_span AS (
                SELECT instrument, max(volume) - min(volume) AS tick_volume_span
                FROM ticks
                GROUP BY instrument
            )
            SELECT count(*)
            FROM bar_sum
            JOIN tick_span USING (instrument)
            WHERE bar_volume > tick_volume_span + 1
               OR bar_volume < tick_volume_span - 1
            """
        ).fetchone()[0]
        print(f"volume reconciliation instruments with mismatch: {bad}")
        return 0
    finally:
        con.close()


if __name__ == "__main__":
    raise SystemExit(main())
