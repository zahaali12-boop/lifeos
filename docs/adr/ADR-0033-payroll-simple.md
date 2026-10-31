# ADR-0033: Payroll and HR, simple: components as posting keys, a thirty-day month, net pay as a payable

Date: 2026-10-31. Status: accepted. Founder programme item 8 (ROADMAP "Founder programme"), ASSUMPTIONS A-181.

## Context

The brief asks for a simple payroll: employees with a basic salary and allowances, deductions such as social
insurance, a monthly run that lands in the books and is paid from the bank, and a payslip. The working rules ask that
every financial effect goes through the posting engine, that subledgers reconcile to control accounts, and that an
admin configures what varies (which components a salary has, which accounts they land on) rather than a developer.
Labour practice in Iraq and the Gulf prorates a month as thirty days, and payrolls are approved before they are paid.

## Decision

1. **A new process module, `Quicker.Payroll`** (tier 3, ARCHITECTURE §6), with four records and no new engines:
   * a **pay component** (`pay_components`) is one piece of a salary: an earning, a deduction or an employer
     contribution, calculated as the basic salary, a fixed amount, a percent of the basic or a percent of the gross,
     with a default amount or rate, whether it applies to every employee, and the accounts it lands on by code. The
     company's `BASIC` component is created on first use on the account the chart gives the `PayrollExpense` role;
   * an **employee** (`pay_employees`, `EMP-{seq:4}`, never reset) is a partner with the employee role hired into a
     company: job, branch, department (a dimension value kept as a dimension set so the salary posts with it), hire
     and last day, basic salary in the company's currency, how they are paid (transfer or cash, bank and IBAN), the
     yearly leave entitlement, and the components they carry (`pay_employee_components`) with their own amount or rate;
   * **leave** (`pay_leaves`): annual (against the entitlement), sick, or unpaid, recorded as approved with its days;
   * a **payroll run** (`pay_runs`, `PRL-{yyyy}-{seq:3}`) is one month of one company, with one line per employee
     (`pay_run_lines`) and one item per component (`pay_run_items`), moving through calculated, (pending approval),
     approved, posted, paid; or reversed, or cancelled. One run per month unless it is cancelled or reversed.
2. **Components are posting keys.** `PostingKeys` gains `PayComponentId` and the account roles `PayrollExpense` and
   `PayrollLiability` are added (the chart template puts 6100 on the first and a new 2195 on the second). Saving a
   component writes the posting rules (role + component → account) into the company's profile through the accounting
   contract, so the run posts by role like every other document, the admin sees the mapping among the other rules,
   and a change of account is a change of rule, audited. No document names an account directly.
3. **A thirty-day month.** Days before the hire date (`day − 1`), days after the last day (`30 − day`) and unpaid
   leave inside the month come off thirty; the basic and the fixed earnings are prorated by the days paid, fixed
   deductions are not. Percentages of the basic apply to the prorated basic; percentages of the gross to the basic plus
   the other earnings, in the components' order. Every item is rounded to the currency under the company's rounding
   mode. The gross is the earnings, the net the gross less the deductions; employer contributions sit beside the
   gross and never reduce it.
4. **Net pay is a payable per employee.** Posting a run writes one journal: Dr `PayrollExpense` (key: the component)
   per earning and per employer contribution, Cr `PayrollLiability` (key: the component) per deduction and per employer
   contribution, Cr `AP` (the employee's supplier posting group) per employee for the net, with the employee's
   dimensions on every line; and opens one AP open item per employee (document `payroll_run`, instalment = line,
   due on the pay date). Paying the run settles each item through Banking's `IPayments` (one payment per employee
   from one account, transfer or cash by the account's kind), so the bank, the AP subledger and the control account
   stay reconciled and the reconciliation and cash reports see salaries as what they are. An employee is therefore
   also a supplier of the company (a supplier account in the company's currency), which the employee screen creates
   with the partner; posting refuses a run with an employee who has none (`payroll_run.employee_not_payable`).
5. **Approval through the workflow engine** (ADR-0020): submitting a run offers it as subject `payroll_run` with the
   net, the gross, the employer's cost, the head count, the period and the pay date; with no active definition the run
   is approved at once. Rejection sends it back calculated with the reason. A posted, unpaid run is reversed (the
   journal mirrored, the open items closed); a paid one is not, its payments are reversed first. Unpaid leave in a
   posted month cannot be cancelled until the run is reversed.
6. **The payslip prints through the pipeline** (ADR-0022) as document type `payslip`, keyed by the run line, from the
   shipped template `payslip.html` (A4, bilingual): period, days paid, the earnings, deductions and employer
   contributions item by item with base and rate, the net, and how it was paid.
7. **Screens** under the organization module, section "People and payroll": employees (the person, the job, the pay,
   the components; a new employee creates the partner with the employee role and the supplier account), leave, payroll
   runs (calculate, submit, post, pay, reverse, payslips), pay components. Permissions `payroll.setup.*`,
   `payroll.employee.*`, `payroll.leave.*`, `payroll.run.read/manage/pay`; role template `hr_payroll`.

## Consequences

* Salaries reconcile like everything else: the AP subledger carries what is owed to employees until the bank pays it,
  withheld amounts sit on the liability the component names until they are paid over by a manual payment, and the
  invariant harness checks all of it after every scenario.
* An admin extends payroll without code: a new allowance or deduction is a component with its accounts; a per-employee
  figure is an assignment. The arithmetic is fixed (A-181) and documented on the payslip.
* The thirty-day convention has known edges (a hire on the 31st is paid nothing for that day; February is thirty days
  too); they follow regional practice and are written down rather than configurable for now.
* Not in this slice (deferred, PROGRESS): salary advances and loans as a ledger of their own (an advance is recovered
  through a fixed deduction component), end-of-service benefits, attendance and overtime, income tax brackets, a
  bank payment file (the payments exist; the file format is a later export), payslip email runs, and a payroll
  register report (the run screen and the saved views cover it for now).
