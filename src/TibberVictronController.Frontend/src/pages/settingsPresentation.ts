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
export const settingStoredValue = (key: string, input: string): string => usesCents(key) ? scale(input, .01)
  : numericControlRules[key] ? input.trim().replace(',', '.') : input;

// Values are in backend units. Keep the same field-level boundaries as the settings service.
const numericControlRules: Record<string, [number, number, boolean?]> = {
  'battery.maximumChargePowerWatts': [1, 2147483647, true],
  'battery.maximumDischargePowerWatts': [1, 2147483647, true],
  'battery.totalCapacityKwh': [Number.MIN_VALUE, Number.MAX_VALUE],
  'battery.minimumStateOfChargePercent': [0, 100],
  'battery.planningMinimumStateOfChargePercent': [0, 100],
  'battery.planningMaximumStateOfChargePercent': [0, 100],
  'battery.targetEndStateOfChargePercent': [0, 100],
  'battery.temporaryStateOfChargePercent': [0, 100],
  'battery.roundTripEfficiencyPercent': [Number.MIN_VALUE, 100],
  'battery.wearCostPerKwh': [0, Number.MAX_VALUE],
  'decision.minimumSavingsPerKwh': [0, Number.MAX_VALUE],
  'gridFeedIn.compensationPricePerKwh': [0, Number.MAX_VALUE],
  'weishauptHotWater.cheapPricePerKwh': [0, Number.MAX_VALUE],
  'weishauptHotWater.minimumPriceAdvantagePerKwh': [.01, 1],
  'weishauptHotWater.extraEnergyPercent': [0, 100],
  'weishauptHotWater.demandHourLocal': [0, 23, true],
  'weishauptHotWater.pvSurplusWatts': [500, 20000, true],
  'weishauptHotWater.initialHoldHours': [1, 12],
  'weishauptHotWater.initialChargeMinutes': [15, 180, true],
  'forecast.horizonHours': [1, 72, true]
};

export function numericControlErrors(key: string, raw: string): string[] {
  const bounds = numericControlRules[key];
  if (!bounds) return [];
  const text = raw.trim().replace(',', '.');
  const value = Number(text);
  if (!/^[+-]?(?:\d+(?:\.\d*)?|\.\d+)$/.test(text) || !Number.isFinite(value))
    return ['Bitte eine gültige Zahl eingeben.'];
  if (bounds[2] && !/^[+-]?\d+$/.test(text)) return ['Bitte eine ganze Zahl eingeben.'];
  if (value < bounds[0] || value > bounds[1] ||
      (key === 'battery.minimumStateOfChargePercent' && value === 100))
    return ['Der Wert liegt außerhalb des zulässigen Bereichs.'];
  return [];
}

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
