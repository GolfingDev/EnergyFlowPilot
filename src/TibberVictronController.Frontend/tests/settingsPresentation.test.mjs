import { test } from 'node:test';
import assert from 'node:assert/strict';
import { isEverydaySetting, usesCents, settingInputValue, settingStoredValue, secretPresentation, settingUpdatePayload } from '../src/pages/settingsPresentation.ts';

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
