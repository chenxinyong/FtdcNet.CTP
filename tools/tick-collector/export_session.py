#!/usr/bin/env python3
"""Copy a finished session database and export ticks/bars to Parquet.

Run after the collector has exited. The database is copied to outdir so it
survives workspace cleanups; ticks and bars are exported with ZSTD.
"""

import argparse
import os
import shutil

import duckdb


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--db", required=True)
    parser.add_argument("--outdir", required=True)
    args = parser.parse_args()

    if not os.path.exists(args.db):
        print(f"database not found: {args.db}")
        return 2

    os.makedirs(args.outdir, exist_ok=True)
    db_copy = os.path.join(args.outdir, os.path.basename(args.db))
    if os.path.abspath(db_copy) != os.path.abspath(args.db):
        shutil.copy2(args.db, db_copy)

    con = duckdb.connect(args.db, read_only=True)
    try:
        tables = {r[0] for r in con.execute("SHOW TABLES").fetchall()}
        if "ticks" in tables:
            con.execute(
                f"COPY ticks TO '{os.path.join(args.outdir, 'ticks.parquet').replace(os.sep, '/')}' "
                "(FORMAT PARQUET, COMPRESSION ZSTD)"
            )
        if "bars_1m" in tables:
            con.execute(
                f"COPY bars_1m TO '{os.path.join(args.outdir, 'bars_1m.parquet').replace(os.sep, '/')}' "
                "(FORMAT PARQUET, COMPRESSION ZSTD)"
            )
        summary_src = os.path.splitext(args.db)[0] + ".summary.json"
        if os.path.exists(summary_src):
            shutil.copy2(summary_src, os.path.join(args.outdir, os.path.basename(summary_src)))
        print(f"exported to {args.outdir}")
        return 0
    finally:
        con.close()


if __name__ == "__main__":
    raise SystemExit(main())
