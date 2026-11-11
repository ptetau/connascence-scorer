def shipped(order):
    return order.status == 6


def mark(order):
    order.status = 6
