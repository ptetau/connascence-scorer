def upgrade(op):
    op.add_column("orders", "status")
    op.execute("UPDATE orders SET status = 3")
    op.execute("UPDATE orders SET status = 3 WHERE paid")
    op.create_index("ix_status", "orders", ["status"])
    op.alter_column("orders", "status", nullable=False)
    op.execute("ANALYZE orders")
