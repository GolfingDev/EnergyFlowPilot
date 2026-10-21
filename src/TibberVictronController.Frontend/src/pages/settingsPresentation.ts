// New settings default to setup-only. Frequency and safety are separate: an everyday control may still be critical.
const everydayKeys = new Set([
  'decision.minimumSavingsPerKwh',
  'battery.planningMinimumStateOfChargePercent',
  'battery.planningMaximumStateOfChargePercent',
  'battery.maximumChargePowerWatts',
  'battery.maximumDischargePowerWatts',
  'forecast.horizonHours',
  'weishauptHotWater.enabled',
  'weishauptHotWater.minimumPriceAdvantagePerKwh',
  'weishauptHotWater.extraEnergyPercent',
  'weishauptHotWater.demandHourLocal',
  'weishauptHotWater.pvSurplusWatts'
]);

const centKeys = new Set([
  'decision.minimumSavingsPerKwh',
  'battery.wearCostPerKwh',
  'gridFeedIn.compensationPricePerKwh',
  'weishauptHotWater.minimumPriceAdvantagePerKwh'
]);

export const isEverydaySetting = (key: string): boolean => everydayKeys.has(key);
export const usesCents = (key: string): boolean => centKeys.has(key);

function scale(raw: string, factor: number): string {
  if (raw.trim() === '') return '';
  const value = Number(raw.replace(',', '.'));
  // Avoid artifacts such as 0.07 EUR becoming 7.000000000000001 ct in the input.
  return Number.isFinite(value) ? String(Number((value * factor).toPrecision(15))) : raw;
}

// Drafts stay in backend units. Opening the page or toggling modes never converts or rewrites persisted values.
export const settingInputValue = (key: string, stored: string): string => usesCents(key) ? scale(stored, 100) : stored;
export const settingStoredValue = (key: string, input: string): string => usesCents(key) ? scale(input, .01) : input;

export function secretPresentation(isConfigured: boolean): { placeholder: string; hint: string } {
  return isConfigured
    ? { placeholder: '********', hint: 'Gespeichert. Nur zum Ersetzen einen neuen Wert eingeben; leer lassen behält den bisherigen Wert.' }
    : { placeholder: 'Noch nicht eingerichtet', hint: 'Es ist noch kein geheimer Wert gespeichert.' };
}

export function settingUpdatePayload(
  setting: { isSensitive: boolean; isConfigured: boolean }, raw: string
): { value: string | null } | null {
  const value = raw.trim();
  if (setting.isSensitive && value === '' && setting.isConfigured) return null;
  return { value: value === '' ? null : value };
}
