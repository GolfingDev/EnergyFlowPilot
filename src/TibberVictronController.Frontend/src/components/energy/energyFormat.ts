// Display formatting for the electricity cost page: German numbers, a dash for unknown values (never "0").

const kwhFormat = new Intl.NumberFormat('de-DE', { maximumFractionDigits: 1, minimumFractionDigits: 1 });
const smallKwhFormat = new Intl.NumberFormat('de-DE', { maximumFractionDigits: 3, minimumFractionDigits: 2 });
const euroFormat = new Intl.NumberFormat('de-DE', { style: 'currency', currency: 'EUR' });
const centFormat = new Intl.NumberFormat('de-DE', { maximumFractionDigits: 1, minimumFractionDigits: 1 });
const percentFormat = new Intl.NumberFormat('de-DE', { maximumFractionDigits: 0 });

export const missing = '–';

export function kwh(value: number | null | undefined, small = false): string {
  return value === null || value === undefined ? missing : `${(small ? smallKwhFormat : kwhFormat).format(value)} kWh`;
}

export function euro(value: number | null | undefined): string {
  return value === null || value === undefined ? missing : euroFormat.format(value);
}

export function centPerKwh(eurPerKwh: number | null | undefined): string {
  return eurPerKwh === null || eurPerKwh === undefined ? missing : `${centFormat.format(eurPerKwh * 100)} ct/kWh`;
}

export function percent(value: number | null | undefined): string {
  return value === null || value === undefined ? missing : `${percentFormat.format(value)} %`;
}

/** "+12 %" / "−3 %" for a change; null when not comparable. */
export function change(percentValue: number | null | undefined): string | null {
  if (percentValue === null || percentValue === undefined) return null;
  const rounded = Math.round(percentValue);
  return `${rounded > 0 ? '+' : rounded < 0 ? '−' : '±'}${percentFormat.format(Math.abs(rounded))} %`;
}

export function localDate(isoDate: string): string {
  const [year, month, day] = isoDate.split('-').map(Number);
  return new Date(Date.UTC(year, month - 1, day)).toLocaleDateString('de-DE', { timeZone: 'UTC', day: 'numeric', month: 'numeric', year: 'numeric' });
}

/** Today in Europe/Berlin as YYYY-MM-DD (the server's calendar). */
export function berlinToday(): string {
  return new Intl.DateTimeFormat('en-CA', { timeZone: 'Europe/Berlin', year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date());
}

/** Calendar arithmetic on YYYY-MM-DD strings, independent of the browser's time zone. */
export function addDays(isoDate: string, days: number): string {
  const [year, month, day] = isoDate.split('-').map(Number);
  return new Date(Date.UTC(year, month - 1, day + days)).toISOString().slice(0, 10);
}

export function daysBetween(fromIso: string, toIso: string): number {
  const toTime = (iso: string) => { const [y, m, d] = iso.split('-').map(Number); return Date.UTC(y, m - 1, d); };
  return Math.round((toTime(toIso) - toTime(fromIso)) / 86_400_000);
}
