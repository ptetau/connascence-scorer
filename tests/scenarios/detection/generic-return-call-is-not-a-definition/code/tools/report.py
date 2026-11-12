def render(title, rows, width, height, colour):
    return title


def summary(rows):
    return render("Summary", rows, 80, 24, "blue")


def detail(rows):
    return render("Detail", rows, 120, 40, "green")
