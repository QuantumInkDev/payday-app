/**
 * Invented SAMPLE data for the home page's paycheck illustration. It is not anyone's real bills,
 * amounts, pay dates or due days, and the page never reads or stores user data.
 *
 * Source: the inline `bills`/`schedules` fixture in design/site/v3-open-relay.html. The page renders
 * the default view from this module at build time, and the small client script imports the same
 * module, so the server-rendered and interactive views cannot drift apart. Totals are always
 * derived from the displayed rows, never typed in.
 */

export const bills = {
  home: { name: "Sample home", amount: 480 },
  power: { name: "Sample power", amount: 48 },
  streaming: { name: "Sample streaming", amount: 12 },
  card: { name: "Sample card", amount: 65 },
  loan: { name: "Sample loan", amount: 135 },
  installments: { name: "Sample installments", amount: 40 },
} as const;

export type BillKey = keyof typeof bills;

export type ScheduleId = "weekly" | "biweekly" | "semimonthly" | "monthly";

/** Which sample paycheck is shown: 0 is "This paycheck", 1 is "Next paycheck". */
export type PeriodIndex = 0 | 1;

type Schedule = {
  /** Short name used in the preview feedback line. */
  name: string;
  /** Relative period labels for this and the next paycheck; never calendar dates. */
  labels: readonly [string, string];
  /** How far apart the two sample paychecks are. */
  gap: string;
  /** The bills each sample paycheck covers. */
  items: readonly [readonly BillKey[], readonly BillKey[]];
};

const everyBill: readonly BillKey[] = ["home", "power", "streaming", "card", "loan", "installments"];

export const schedules: Readonly<Record<ScheduleId, Schedule>> = {
  weekly: {
    name: "Weekly",
    labels: ["This sample week", "Next sample week"],
    gap: "Next paycheck begins 1 week later.",
    items: [
      ["power", "streaming"],
      ["card", "installments"],
    ],
  },
  biweekly: {
    name: "Every 2 weeks",
    labels: ["This 2-week period", "Next 2-week period"],
    gap: "Next paycheck begins 2 weeks later.",
    items: [
      ["home", "power", "streaming"],
      ["card", "loan", "installments"],
    ],
  },
  semimonthly: {
    name: "Twice a month",
    labels: ["First half of a sample month", "Second half of that sample month"],
    gap: "Two paychecks per sample month: one for each half.",
    items: [
      ["home", "card", "streaming"],
      ["power", "loan", "installments"],
    ],
  },
  monthly: {
    name: "Monthly",
    labels: ["This sample month", "Next sample month"],
    gap: "Next paycheck begins in the next sample month.",
    items: [everyBill, everyBill],
  },
};

/** Windows supports every 2 weeks only, so that is the default and the only schedule without the preview. */
export const defaultSchedule: ScheduleId = "biweekly";

export function isScheduleId(value: string): value is ScheduleId {
  return Object.hasOwn(schedules, value);
}

export function formatMoney(value: number): string {
  return `$${value.toLocaleString("en-US")}`;
}

export type PeriodView = {
  label: string;
  rows: readonly { name: string; amount: string }[];
  total: string;
  feedback: string;
};

/** Everything the sample panel shows for one schedule and paycheck. */
export function periodView(scheduleId: ScheduleId, period: PeriodIndex): PeriodView {
  const schedule = schedules[scheduleId];
  const entries = schedule.items[period].map((key) => bills[key]);
  const total = entries.reduce((sum, entry) => sum + entry.amount, 0);
  return {
    label: schedule.labels[period],
    rows: entries.map((entry) => ({ name: entry.name, amount: formatMoney(entry.amount) })),
    total: formatMoney(total),
    feedback: `Sample view: ${schedule.name}. ${schedule.gap}`,
  };
}
