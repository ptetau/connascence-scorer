import time

def export(rows, path, include_header, delimiter, encoding, quote_all):
    pass

def summarise(orders, verbose):
    shipped = [o for o in orders if o.status == 3]
    time.sleep(2)
    return shipped

def nightly(orders):
    late = [o for o in orders if o.status == 3]
    summarise(orders, True)
    export(orders, "out.csv", True, ",", "utf-8", False)
    total = 0
    for o in orders:
        total += o.net
        total += o.tax
        print(o.id, o.net, o.tax)
        if o.net > 1000:
            print("big order", o.id)
    return late

def weekly(orders):
    summarise(orders, False)
    total = 0
    for o in orders:
        total += o.net
        total += o.tax
        print(o.id, o.net, o.tax)
        if o.net > 1000:
            print("big order", o.id)
    return total
