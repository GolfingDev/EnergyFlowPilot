import type { HeatingHistoryRange } from './heatingTypes';

// Display boundary: all API timestamps are UTC, the household reads Europe/Berlin.
const displayTimeZone = 'Europe/Berlin';

export function formatHeatingDateTime(value: string | number): string {
  return new Intl.DateTimeFormat('de-DE', {
    timeZone: displayTimeZone,
    dateStyle: 'short',
    timeStyle: 'short'
  }).format(new Date(value));
}

export function formatHeatingAxisTick(value: number, range: HeatingHistoryRange): string {
  const options: Intl.DateTimeFormatOptions = range === 'day'
    ? { timeZone: displayTimeZone, hour: '2-digit', minute: '2-digit' }
    : { timeZone: displayTimeZone, day: '2-digit', month: '2-digit', hour: range === 'week' ? '2-digit' : undefined };

  return new Intl.DateTimeFormat('de-DE', options).format(new Date(value));
}

export function formatHeatingValue(value: number | null | undefined, unit: string, digits = 1): string {
  if (typeof value !== 'number') {
    return '–';
  }

  return `${new Intl.NumberFormat('de-DE', { minimumFractionDigits: digits, maximumFractionDigits: digits }).format(value)} ${unit}`;
}

/**
 * Categorical palette with enough distinct hues for all rooms and zones of one household.
 */
export const heatingSeriesPalette = [
  '#2f6fb2',
  '#e07b24',
  '#2f9e6b',
  '#c8414b',
  '#7b5fb5',
  '#b38a12',
  '#1f9bb0',
  '#c2549a',
  '#6d7f2b',
  '#8a5a3c',
  '#4f5fd1',
  '#d1662e'
];
