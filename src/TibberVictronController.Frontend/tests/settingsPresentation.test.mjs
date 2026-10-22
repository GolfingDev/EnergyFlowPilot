import { test } from 'node:test';
import assert from 'node:assert/strict';
import { isEverydaySetting, usesCents, settingInputValue, settingStoredValue, secretPresentation, settingUpdatePayload, numericControlErrors } from '../src/pages/settingsPresentation.ts';

test('control inputs reject empty, fractional watts and invalid ranges; decimal commas are normalized', () => {
  for (const raw of ['', 'no', '4000.5', '4000,5', '0', '-1', '2147483648', '0x10'])
    assert.ok(numericControlErrors('battery.maximumChargePowerWatts', raw).length > 0, raw);
  assert.deepEqual(numericControlErrors('battery.maximumChargePowerWatts', '4000'), []);
  assert.equal(settingStoredValue('battery.totalCapacityKwh', '19,2'), '19.2');
  assert.equal(settingStoredValue('mqtt.password', 'abc,xyz'), 'abc,xyz');
  assert.ok(numericControlErrors('weishauptHotWater.demandHourLocal', '18.5').length);
  assert.ok(numericControlErrors('weishauptHotWater.pvSurplusWatts', '499').length);
  assert.ok(numericControlErrors('forecast.horizonHours', '73').length);
  assert.ok(numericControlErrors('battery.minimumStateOfChargePercent', '100').length);
  assert.deepEqual(numericControlErrors('battery.targetEndStateOfChargePercent', '25'), []);
  assert.deepEqual(numericControlErrors('battery.planningMinimumStateOfChargePercent', '30'), []);
});

test('everyday mode highlights battery and hot-water controls, not installation details', () => {
  for (const key of ['decision.minimumSavingsPerKwh', 'battery.planningMinimumStateOfChargePercent', 'weishauptHotWater.enabled'])
    assert.equal(isEverydaySetting(key), true);
  for (const key of ['tibber.accessToken', 'octopus.apiKey', 'homematic.apiToken', 'hagerEnergy.clientSecret',
    'electricityPrices.provider', 'battery.totalCapacityKwh', 'battery.minimumStateOfChargePercent',
    'victron.host', 'victron.writeTopics.disableCharge', 'new.unknownSetting'])
    assert.equal(isEverydaySetting(key), false);
});

test('currency display is ct/kWh; API drafts and saved payloads remain EUR/kWh', () => {
  const key = 'decision.minimumSavingsPerKwh';
  assert.equal(settingInputValue(key, '0.03'), '3');
  assert.equal(settingStoredValue(key, '3'), '0.03');
  assert.equal(settingStoredValue(key, '0,5'), '0.005');
  assert.equal(settingInputValue(key, '0.07'), '7');
  for (const value of ['0', '0.03', '0.07', '0.123456'])
    assert.equal(settingStoredValue(key, settingInputValue(key, value)), value);
  assert.equal(settingStoredValue(key, ''), '');
  assert.equal(settingStoredValue(key, 'invalid'), 'invalid');
  assert.equal(settingInputValue('battery.totalCapacityKwh', '19.2'), '19.2');
  assert.equal(usesCents('weishauptHotWater.minimumPriceAdvantagePerKwh'), true);
});

test('configured secrets show a mask without loading any actual secret into the draft', () => {
  assert.equal(secretPresentation(true).placeholder, '********');
  assert.equal(secretPresentation(false).placeholder, 'Noch nicht eingerichtet');
  assert.equal(settingUpdatePayload({ isSensitive: true, isConfigured: true }, ''), null);
  assert.equal(settingUpdatePayload({ isSensitive: true, isConfigured: true }, '   '), null);
  assert.deepEqual(settingUpdatePayload({ isSensitive: true, isConfigured: true }, 'replacement-key'), { value: 'replacement-key' });
  assert.deepEqual(settingUpdatePayload({ isSensitive: false, isConfigured: true }, settingStoredValue('decision.minimumSavingsPerKwh', '3')), { value: '0.03' });
});
