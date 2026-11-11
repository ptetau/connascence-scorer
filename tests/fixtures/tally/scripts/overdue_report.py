"""Nightly job: email each business a list of overdue invoices."""
import csv
import sqlite3
import time


def fetch_invoices(db_path):
    conn = sqlite3.connect(db_path)
    rows = conn.execute("SELECT id, reference, status, total, due FROM invoices").fetchall()
    conn.close()
    return [dict(zip(["id", "reference", "status", "total", "due"], r)) for r in rows]


def overdue(invoices):
    # 5 = overdue, 3 = sent (see Invoice.Status in Tally.Domain)
    return [i for i in invoices if i["status"] == 5]


def still_sent(invoices):
    return [i for i in invoices if i["status"] == 3]


def write_report(invoices, path):
    with open(path, "w", newline="") as f:
        w = csv.writer(f)
        for i in invoices:
            w.writerow([i["reference"], i["total"], i["due"]])


def send_report(mailer, to, path):
    for attempt in range(3):
        if mailer.send(to, "Overdue invoices", path):
            return True
        time.sleep(30)
    return False


if __name__ == "__main__":
    rows = fetch_invoices("tally.db")
    late = overdue(rows)
    if any(i["status"] == 5 for i in rows):
        write_report(late, "overdue.csv")
