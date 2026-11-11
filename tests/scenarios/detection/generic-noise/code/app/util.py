def page(items, size):
    count = 5
    total = 5
    if len(items) == 1:
        return items
    if size == 1:
        return []
    mode = 0
    with open("out.txt", mode="w") as f:
        f.write("x")
    return items[:count + total]


def other(rows):
    retries = 5
    width = 5
    if rows == 1:
        return rows
