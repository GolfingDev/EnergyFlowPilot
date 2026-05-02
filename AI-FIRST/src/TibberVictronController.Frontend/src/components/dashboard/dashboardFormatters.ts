export function translateDecisionState(value: string): string {
  const translations: Record<string, string> = {
    Charge: 'Laden',
    Discharge: 'Entladen',
    Idle: 'Idle'
  };

  return translations[value] ?? value;
}

export function formatDateTime(value: string): string {
  return new Intl.DateTimeFormat('de-DE', {
    dateStyle: 'short',
    timeStyle: 'short'
  }).format(new Date(value));
}

export function formatNumber(value: number, digits = 1): string {
  return new Intl.NumberFormat('de-DE', {
    minimumFractionDigits: digits,
    maximumFractionDigits: digits
  }).format(value);
}

export function formatCurrency(value: number, currency: string): string {
  return new Intl.NumberFormat('de-DE', {
    style: 'currency',
    currency
  }).format(value);
}

export function formatPower(value: number | null | undefined): string {
  return typeof value === 'number' ? `${formatNumber(value, 0)} W` : 'Nicht verfuegbar';
}

export function formatPrice(value: number | null | undefined, currency: string | null | undefined): string {
  return typeof value === 'number' ? `${formatCurrency(value, currency ?? 'EUR')} / kWh` : 'Nicht verfuegbar';
}

export function formatPercent(value: number | null | undefined): string {
  return typeof value === 'number' ? `${formatNumber(value, 1)} %` : 'Nicht verfuegbar';
}

export function getDecisionLabel(decisionState: string, chargeSource: string | null): string {
  if (decisionState === 'Charge' && chargeSource) {
    return `Laden (${chargeSource})`;
  }

  return translateDecisionState(decisionState);
}
