# Tally

Invoicing for New Zealand sole traders and small firms. One business per install.

- **Who uses it:** the business owner, on their own. A few have asked to give their accountant a login.
- **How:** a web front end (`web/`) over a JSON API (`Tally.Api`). A nightly Python script emails overdue reports.
- **Money:** invoices are in NZD with 15% GST.
- **Payments:** card payments through one provider (`PaymentGateway`, Windcave). Webhooks confirm payment.
- **Banks:** customers upload their bank statement as CSV (`BankFile.ParseStatementLine`). Payroll-style
  direct credits go out in the BNZ direct-credit file format (`BankFile.DirectCreditLine`). That layout is
  fixed by the bank's published spec and has not changed in ten years.
- **Data:** one SQL Server database. Runs as a single instance on one VM.

## Invoice lifecycle

Draft → Sent → Paid, or Sent → Overdue → Paid. Credit notes are a separate invoice kind.

## Roadmap (from the issue tracker)

- Australian customers keep asking: GST is 10% there and invoices are in AUD.
- Several users want Stripe as an alternative to Windcave.
- Accountants want their own login to see their clients' invoices.
- "Partially paid" invoices come up in support every week.
