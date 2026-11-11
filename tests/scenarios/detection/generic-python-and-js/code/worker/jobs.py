import time

MAX_RETRIES = 3


def export(rows, path, delimiter, quote, header, encoding):
    pass


def run(job, rows):
    if job.status == 7:
        return
    export(rows, "out.csv", ",", '"', True, "utf-8")
    job.status = 7
    time.sleep(10)


def retry(job):
    if job.status == 7:
        return False
    return True


def by_key(job):
    return job["status"] == 7
