/** One formatting vocabulary for the Zentrale: power in kW/W, energy in kWh, prices in ct/kWh, times in Europe/Berlin. */
const timeZone = 'Europe/Berlin';

export function kw(watts: number | null | undefined, digits = 2): string {
  if (watts == null || !Number.isFinite(watts)) return '–';
  const value = Math.abs(watts);
  return value < 1000
    ? `${Math.round(value).toLocaleString('de-DE')} W`
    : `${(value / 1000).toLocaleString('de-DE', { minimumFractionDigits: digits, maximumFractionDigits: digits })} kW`;
}

export function kwh(value: number | null | undefined, digits = 1): string {
  return value == null || !Number.isFinite(value) ? '–' : `${value.toLocaleString('de-DE', { minimumFractionDigits: digits, maximumFractionDigits: digits })} kWh`;
}

export function percent(value: number | null | undefined): string {
  return value == null || !Number.isFinite(value) ? '–' : `${Math.round(value).toLocaleString('de-DE')} %`;
}

export function priceText(value: number | null | undefined, currency: string | null | undefined = 'EUR'): string {
  if (value == null || !Number.isFinite(value)) return '–';
  return !currency || currency === 'EUR'
    ? `${(value * 100).toLocaleString('de-DE', { minimumFractionDigits: 1, maximumFractionDigits: 1 })} ct/kWh`
    : `${value.toLocaleString('de-DE', { minimumFractionDigits: 3, maximumFractionDigits: 3 })} ${currency}/kWh`;
}

export function euro(value: number | null | undefined): string {
  return value == null || !Number.isFinite(value) ? '–' : value.toLocaleString('de-DE', { style: 'currency', currency: 'EUR' });
}

export function clock(iso: string | null | undefined): string {
  return iso ? new Date(iso).toLocaleTimeString('de-DE', { timeZone, hour: '2-digit', minute: '2-digit' }) : '–';
}

/** Time with weekday when the moment is not today, so "07:00" is never mistaken for this morning. */
export function when(iso: string | null | undefined, reference = Date.now()): string {
  if (!iso) return '–';
  const date = new Date(iso);
  const day = (d: Date) => d.toLocaleDateString('de-DE', { timeZone });
  return day(date) === day(new Date(reference))
    ? clock(iso)
    : date.toLocaleString('de-DE', { timeZone, weekday: 'short', hour: '2-digit', minute: '2-digit' });
}

export function age(iso: string | null | undefined, reference = Date.now()): string {
  if (!iso) return 'unbekannt';
  const seconds = Math.max(0, Math.round((reference - Date.parse(iso)) / 1000));
  if (seconds < 60) return `vor ${seconds} s`;
  if (seconds < 3600) return `vor ${Math.round(seconds / 60)} min`;
  return `vor ${Math.round(seconds / 3600)} h`;
}

/** Signed battery power as words; positive means charging, as delivered by the backend. */
export function batteryActivity(watts: number | null | undefined): { label: string; kind: 'charge' | 'discharge' | 'idle' | 'unknown' } {
  if (watts == null) return { label: 'Keine Messung', kind: 'unknown' };
  if (watts > 50) return { label: 'Lädt', kind: 'charge' };
  if (watts < -50) return { label: 'Entlädt', kind: 'discharge' };
  return { label: 'Ruht', kind: 'idle' };
}
