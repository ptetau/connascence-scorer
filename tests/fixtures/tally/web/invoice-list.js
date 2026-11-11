// Renders the invoice list page.
const STATUS_LABELS = { 1: "Draft", 3: "Sent", 4: "Paid", 5: "Overdue" };

export function badge(invoice) {
  if (invoice.status === 5) return "badge-red";
  if (invoice.status === 3) return "badge-amber";
  return "badge-grey";
}

export function canPay(invoice) {
  return invoice.status === 3 || invoice.status === 5;
}

export function label(invoice) {
  return STATUS_LABELS[invoice.status] || "Unknown";
}

export function formatDue(invoice) {
  const d = new Date(invoice.due);
  return d.toLocaleDateString("en-NZ");
}
