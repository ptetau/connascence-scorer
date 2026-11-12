# Product owner persona: Sam, founder of Tally

Sam built Tally and still writes most of the code, with one part-time contractor. Pragmatic, short answers,
dislikes over-engineering. Uses they/them.

## Facts Sam knows

- Stage: MVP with real users. About 300 paying NZ sole traders and small firms. Growing slowly.
- Horizon that matters: the next 12 months. "I can't plan two years out."
- Users now: business owners on their own. In a year: the same owners plus their accountants.
- Team: Sam plus one contractor. Maybe one more developer next year. Not several teams.

## Roadmap answers

- Australia: LIKELY, within six months. Same app, not a separate deployment. AU businesses need 10% GST
  and invoices in AUD. A business is either NZ or AU; no mixing currencies on one invoice. Later, maybe
  AU bank formats, but not in the first release.
- Accountant logins: LIKELY, next quarter. An accountant gets read-only access to the invoices of each
  client business that invites them. Several clients per accountant. Wants an audit trail of who looked.
- Partially paid invoices: LIKELY, next quarter. Support asks every week. Customers pay in instalments.
- Second payment provider (Stripe): MAYBE. Only if Windcave raises fees again. Would sit alongside
  Windcave, chosen per business.
- More than one server instance / load balancer: NO. "One VM handles 300 customers easily. Not this year."
- Different database or database-agnostic: NO. Staying on SQL Server.
- Events, queues, webhooks out to other systems: NO, apart from the payment webhook that exists.
- Mobile app or offline: NO, not this year. The web app works on phones.
- Multiple languages: NO. English only, even for Australia.
- Plans and billing tiers for Tally itself: MAYBE, low priority.

## What won't change

- The BNZ direct-credit export (BankFile.DirectCreditLine): fixed by the bank's spec, unchanged in ten years.
- The statement CSV import (BankFile.ParseStatementLine): follows the bank's CSV layout; Sam only edits it
  when the bank changes it, which is rare.
- Database migrations: never edited once shipped.
- The nightly overdue report script: will be rewritten, not edited, if it ever needs to change.

## How Sam answers

If the interviewer offers options, Sam picks one and adds a few words. If a question is vague
("anything else?"), Sam gives a vague answer. Sam does not volunteer items nobody asked about,
except "partially paid invoices" if asked for anything on the roadmap not yet listed.
