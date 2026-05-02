<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';

interface ControllerStatusResponseDto {
  status: string;
  knownSettingsCount: number;
  persistedSettingsCount: number;
  configuredSensitiveSettingsCount: number;
  generatedAtUtc: string;
  victronMqttStatus: string | null;
  victronMqttLastError: string | null;
  victronMqttLastSuccessfulMessageAtUtc: string | null;
}

interface ControllerSettingResponseDto {
  key: string;
  value: string | null;
  isSensitive: boolean;
  isConfigured: boolean;
  updatedAtUtc: string;
}

interface ControllerSettingsResponseDto {
  settings: ControllerSettingResponseDto[];
}

interface UpdateControllerSettingRequestDto {
  value: string | null;
}

interface SettingMetadataDto {
  key: string;
  displayName: string;
  description: string;
  group: string;
  inputKind: string;
  unit: string | null;
  isSensitive: boolean;
  defaultValue: string | null;
}

interface GuiMetadataResponseDto {
  settings: SettingMetadataDto[];
}

interface SettingsErrorDto {
  message: string;
  exceptionMessage?: string;
  exceptionType?: string;
  traceId?: string;
}

interface UiError {
  message: string;
  details: string;
}

type SectionKey = 'battery' | 'price' | 'forecast' | 'consumption' | 'decision' | 'system';
type FieldCategory = 'normal' | 'important' | 'critical';

interface SectionDefinition {
  key: SectionKey;
  title: string;
  description: string;
}

interface FieldDefinition {
  key: string;
  section: SectionKey;
  subgroup: string;
  category: FieldCategory;
  helpText?: string;
  inputMode?: 'default' | 'timezone';
}

const sectionDefinitions: SectionDefinition[] = [
  { key: 'battery', title: 'Batterie', description: 'Kapazität, harte Grenzen, Planungsprofil und Wirkungsgrad.' },
  { key: 'price', title: 'Tibber', description: 'Zugangsdaten und Preisparameter für dynamische Strompreise.' },
  { key: 'forecast', title: 'PV / Prognose', description: 'Standort, PV-Leistung und Prognoseanbieter für die Planung.' },
  { key: 'consumption', title: 'Verbrauch', description: 'Annahmen für den Last- und Verbrauchsforecast.' },
  { key: 'decision', title: 'Entscheidungslogik', description: 'Verhalten der Entscheidungslogik, sofern Einstellungen vorhanden sind.' },
  { key: 'system', title: 'Victron Connection', description: 'MQTT, Laufzeitverhalten und sicherheitsrelevante Verbindungswerte.' }
];

const fieldDefinitions: FieldDefinition[] = [
  { key: 'battery.totalCapacityKwh', section: 'battery', subgroup: 'Harte Grenzen', category: 'critical' },
  { key: 'battery.minimumStateOfChargePercent', section: 'battery', subgroup: 'Harte Grenzen', category: 'critical', helpText: 'Harte Untergrenze zum Schutz des Akkus.' },
  { key: 'battery.maximumChargePowerWatts', section: 'battery', subgroup: 'Harte Grenzen', category: 'critical' },
  { key: 'battery.maximumDischargePowerWatts', section: 'battery', subgroup: 'Harte Grenzen', category: 'critical' },
  { key: 'battery.planningMinimumStateOfChargePercent', section: 'battery', subgroup: 'Planungsprofil', category: 'important' },
  { key: 'battery.planningMaximumStateOfChargePercent', section: 'battery', subgroup: 'Planungsprofil', category: 'critical', helpText: 'Sehr hohe Werte lassen wenig Platz für unerwarteten PV-Überschuss.' },
  { key: 'battery.targetEndStateOfChargePercent', section: 'battery', subgroup: 'Planungsprofil', category: 'important' },
  { key: 'battery.roundTripEfficiencyPercent', section: 'battery', subgroup: 'Wirkungsgrad', category: 'important' },
  { key: 'tibber.accessToken', section: 'price', subgroup: 'Tibber API', category: 'critical', helpText: 'Token werden nie im Klartext angezeigt. Leer lassen behält einen vorhandenen geheimen Wert.' },
  { key: 'tibber.homeSelection', section: 'price', subgroup: 'Tibber API', category: 'normal' },
  { key: 'forecast.horizonHours', section: 'price', subgroup: 'Preisplanung', category: 'important' },
  { key: 'gridFeedIn.compensationPricePerKwh', section: 'price', subgroup: 'Preisplanung', category: 'important' },
  { key: 'pvForecast.apiKey', section: 'forecast', subgroup: 'Forecast.Solar', category: 'critical', helpText: 'Optionaler Forecast.Solar API-Key für bezahlte Pläne. Leer lassen behält einen vorhandenen geheimen Wert.' },
  { key: 'pvForecast.latitude', section: 'forecast', subgroup: 'Standort', category: 'important' },
  { key: 'pvForecast.longitude', section: 'forecast', subgroup: 'Standort', category: 'important' },
  { key: 'pvForecast.peakPowerKwp', section: 'forecast', subgroup: 'PV-Anlage', category: 'important' },
  { key: 'pvForecast.declinationDegrees', section: 'forecast', subgroup: 'PV-Anlage', category: 'normal' },
  { key: 'pvForecast.azimuthDegrees', section: 'forecast', subgroup: 'PV-Anlage', category: 'normal' },
  { key: 'pvForecast.timeZone', section: 'forecast', subgroup: 'Standort', category: 'normal', inputMode: 'timezone' },
  { key: 'consumptionForecast.averageDailyConsumptionKwh', section: 'consumption', subgroup: 'Lastprofil', category: 'important' },
  { key: 'consumptionForecast.timeZone', section: 'consumption', subgroup: 'Lastprofil', category: 'normal' },
  { key: 'victron.dryRun', section: 'decision', subgroup: 'Betriebsmodus', category: 'critical', helpText: 'Simulationsmodus: Entscheidungen werden berechnet, aber Hardware wird nicht aktiv gesteuert.' },
  { key: 'decisionLog.retentionDays', section: 'decision', subgroup: 'Nachvollziehbarkeit', category: 'normal' },
  { key: 'dashboard.autoRefreshIntervalSeconds', section: 'system', subgroup: 'Dashboard', category: 'normal', helpText: 'Intervall fuer die automatische Aktualisierung der Dashboard-Daten. 0 deaktiviert die Automatik.' },
  { key: 'victron.host', section: 'system', subgroup: 'Victron MQTT', category: 'critical' },
  { key: 'victron.port', section: 'system', subgroup: 'Victron MQTT', category: 'important' },
  { key: 'victron.portalId', section: 'system', subgroup: 'Victron MQTT', category: 'important' },
  { key: 'victron.keepAliveSeconds', section: 'system', subgroup: 'Laufzeit', category: 'important' },
  { key: 'victron.staleAfterSeconds', section: 'system', subgroup: 'Laufzeit', category: 'critical' },
  { key: 'victron.topics.gridPower', section: 'system', subgroup: 'MQTT-Themen', category: 'normal' },
  { key: 'victron.topics.batterySoc', section: 'system', subgroup: 'MQTT-Themen', category: 'normal' },
  { key: 'victron.topics.batteryPower', section: 'system', subgroup: 'MQTT-Themen', category: 'normal' },
  { key: 'victron.topics.houseConsumption', section: 'system', subgroup: 'MQTT-Themen', category: 'normal' },
  { key: 'victron.writeTopics.chargeDischargeSetpoint', section: 'system', subgroup: 'MQTT-Themen', category: 'critical' }
];

const requiredKeys = new Set(['tibber.accessToken', 'battery.totalCapacityKwh', 'victron.host']);
const timezoneOptions = [
  'Europe/Berlin',
  'UTC'
];
const fallbackMetadata: SettingMetadataDto[] = [
  {
    key: 'pvForecast.apiKey',
    displayName: 'Forecast.Solar API-Key',
    description: 'Optionaler API-Key für bezahlte Forecast.Solar Pläne.',
    group: 'Forecast',
    inputKind: 'password',
    unit: null,
    isSensitive: true,
    defaultValue: null
  }
];

const activeSectionKey = ref<SectionKey>('battery');
const settings = ref<ControllerSettingResponseDto[]>([]);
const metadata = ref<GuiMetadataResponseDto | null>(null);
const status = ref<ControllerStatusResponseDto | null>(null);
const draftValues = ref<Record<string, string>>({});
const initialValues = ref<Record<string, string>>({});
const isLoading = ref(false);
const isSaving = ref(false);
const saveSuccess = ref(false);
const pageError = ref<UiError | null>(null);

const metadataByKey = computed(() => new Map([
  ...fallbackMetadata,
  ...(metadata.value?.settings ?? [])
].map((entry) => [entry.key, entry])));
const settingByKey = computed(() => new Map(settings.value.map((entry) => [entry.key, entry])));

const availableFields = computed(() => fieldDefinitions.filter((field) => metadataByKey.value.has(field.key)));

const sections = computed(() => sectionDefinitions
  .map((section) => ({
    ...section,
    fields: availableFields.value.filter((field) => field.section === section.key)
  }))
  .filter((section) => section.fields.length > 0));

const activeSection = computed(() => sections.value.find((section) => section.key === activeSectionKey.value) ?? sections.value[0]);

const activeSubgroups = computed(() => {
  const groups = new Map<string, FieldDefinition[]>();

  for (const field of activeSection.value?.fields ?? []) {
    const existingFields = groups.get(field.subgroup) ?? [];
    existingFields.push(field);
    groups.set(field.subgroup, existingFields);
  }

  return Array.from(groups.entries()).map(([title, fields]) => ({ title, fields }));
});

const hasUnsavedChanges = computed(() => Object.keys(draftValues.value).some((key) => draftValues.value[key] !== initialValues.value[key]));

const validationErrors = computed(() => Array.from(new Set([
  ...getFieldErrors('battery.totalCapacityKwh'),
  ...getFieldErrors('battery.minimumStateOfChargePercent'),
  ...getFieldErrors('battery.planningMinimumStateOfChargePercent'),
  ...getFieldErrors('battery.planningMaximumStateOfChargePercent'),
  ...getFieldErrors('battery.targetEndStateOfChargePercent'),
  ...getFieldErrors('battery.roundTripEfficiencyPercent'),
  ...getFieldErrors('battery.maximumChargePowerWatts'),
  ...getFieldErrors('battery.maximumDischargePowerWatts')
])));

const warningMessages = computed(() => {
  const warnings: string[] = [];
  const planningMaximum = getNumericDraftValue('battery.planningMaximumStateOfChargePercent');
  const keepAliveSeconds = getNumericDraftValue('victron.keepAliveSeconds');
  const staleAfterSeconds = getNumericDraftValue('victron.staleAfterSeconds');

  if (planningMaximum !== null && planningMaximum > 98) {
    warnings.push('Das Planungsmaximum liegt über 98 %. Dadurch bleibt kaum Puffer für unerwarteten PV-Überschuss.');
  }

  if (keepAliveSeconds !== null && keepAliveSeconds < 5) {
    warnings.push('Ein sehr kurzes MQTT-KeepAlive kann unnötige Systemlast erzeugen.');
  }

  if (staleAfterSeconds !== null && staleAfterSeconds < 10) {
    warnings.push('Ein sehr kurzes Stale-Timeout kann zu häufigen Sicherheits-Fallbacks führen.');
  }

  return warnings;
});

const canSave = computed(() => hasUnsavedChanges.value && validationErrors.value.length === 0 && !isSaving.value);

const summaryItems = computed(() => {
  const totalCapacity = getNumericDraftValue('battery.totalCapacityKwh');
  const planningMinimum = getNumericDraftValue('battery.planningMinimumStateOfChargePercent');
  const planningMaximum = getNumericDraftValue('battery.planningMaximumStateOfChargePercent');
  const targetEnd = getNumericDraftValue('battery.targetEndStateOfChargePercent');
  const usableCapacity = totalCapacity !== null && planningMinimum !== null && planningMaximum !== null
    ? totalCapacity * ((planningMaximum - planningMinimum) / 100)
    : null;

  return [
    { label: 'Planungsbereich', value: planningMinimum !== null && planningMaximum !== null ? `${formatPercentValue(planningMinimum)} bis ${formatPercentValue(planningMaximum)}` : 'Nicht verfügbar' },
    { label: 'Nutzbare Planungskapazität', value: usableCapacity !== null ? `${formatDecimal(usableCapacity, 2)} kWh` : 'Nicht verfügbar' },
    { label: 'Ziel-SoC am Ende', value: targetEnd !== null ? formatPercentValue(targetEnd) : 'Nicht verfügbar' },
    { label: 'Simulationsmodus', value: getDraftValue('victron.dryRun') === 'true' ? 'Aktiv' : 'Inaktiv' },
    { label: 'Tibber Token', value: isSettingConfigured('tibber.accessToken') ? 'Konfiguriert' : 'Fehlt' },
    { label: 'Victron MQTT', value: status.value?.victronMqttStatus ?? 'Unbekannt' }
  ];
});

const planningRangeStyle = computed(() => {
  const minimum = clampPercent(getNumericDraftValue('battery.minimumStateOfChargePercent') ?? 0);
  const planningMinimum = clampPercent(getNumericDraftValue('battery.planningMinimumStateOfChargePercent') ?? minimum);
  const planningMaximum = clampPercent(getNumericDraftValue('battery.planningMaximumStateOfChargePercent') ?? 100);
  const targetEnd = clampPercent(getNumericDraftValue('battery.targetEndStateOfChargePercent') ?? planningMinimum);

  return {
    '--hard-min': `${minimum}%`,
    '--planning-start': `${planningMinimum}%`,
    '--planning-width': `${Math.max(0, planningMaximum - planningMinimum)}%`,
    '--target-end': `${targetEnd}%`
  };
});

async function loadPageData(): Promise<void> {
  isLoading.value = true;
  pageError.value = null;

  try {
    const [settingsResponse, metadataResponse, statusResponse] = await Promise.all([
      fetchJson<ControllerSettingsResponseDto>('/api/settings'),
      fetchJson<GuiMetadataResponseDto>('/api/gui/metadata'),
      fetchJson<ControllerStatusResponseDto>('/api/status')
    ]);

    settings.value = settingsResponse.settings;
    metadata.value = metadataResponse;
    status.value = statusResponse;
    initializeDrafts();
    ensureActiveSection();
    saveSuccess.value = false;
  } catch (error) {
    pageError.value = createUiError(error, 'Die Einstellungen konnten nicht geladen werden.');
  } finally {
    isLoading.value = false;
  }
}

async function saveSettings(): Promise<void> {
  if (!canSave.value) {
    return;
  }

  isSaving.value = true;
  pageError.value = null;
  saveSuccess.value = false;

  try {
    const changedKeys = Object.keys(draftValues.value).filter((key) => draftValues.value[key] !== initialValues.value[key]);

    for (const key of changedKeys) {
      const setting = getSettingOrFallback(key);
      const payload = createUpdatePayload(setting, draftValues.value[key]);

      if (payload === null) {
        continue;
      }

      await fetchJson<ControllerSettingResponseDto>(`/api/settings/${encodeURIComponent(key)}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });
    }

    await loadPageData();
    saveSuccess.value = true;
  } catch (error) {
    pageError.value = createUiError(error, 'Die Einstellungen konnten nicht gespeichert werden.');
  } finally {
    isSaving.value = false;
  }
}

function resetChanges(): void {
  draftValues.value = { ...initialValues.value };
  saveSuccess.value = false;
}

function setActiveSection(sectionKey: SectionKey): void {
  activeSectionKey.value = sectionKey;
}

function setBooleanDraft(key: string, value: boolean | null): void {
  draftValues.value[key] = value ? 'true' : 'false';
}

function getDraftValue(key: string): string {
  return draftValues.value[key] ?? '';
}

function getNumericDraftValue(key: string): number | null {
  const rawValue = getDraftValue(key).trim();

  if (rawValue === '') {
    return null;
  }

  const parsedValue = Number(rawValue.replace(',', '.'));

  return Number.isFinite(parsedValue) ? parsedValue : null;
}

function getFieldErrors(key: string): string[] {
  const errors: string[] = [];
  const numericValue = getNumericDraftValue(key);

  if (requiredKeys.has(key) && !isSettingConfigured(key)) {
        errors.push('Diese Einstellung ist erforderlich.');
  }

  switch (key) {
    case 'battery.totalCapacityKwh':
      if (numericValue !== null && numericValue <= 0) {
        errors.push('Die Kapazität muss größer als 0 sein.');
      }
      break;
    case 'battery.minimumStateOfChargePercent':
      if (numericValue !== null && (numericValue < 0 || numericValue > 100)) {
        errors.push('Der Minimum-SoC muss zwischen 0 und 100 liegen.');
      }
      break;
    case 'battery.planningMinimumStateOfChargePercent': {
      const minimum = getNumericDraftValue('battery.minimumStateOfChargePercent');
      if (numericValue !== null && minimum !== null && numericValue < minimum) {
        errors.push('Das Planungsminimum muss größer oder gleich dem harten Minimum sein.');
      }
      break;
    }
    case 'battery.planningMaximumStateOfChargePercent': {
      const planningMinimum = getNumericDraftValue('battery.planningMinimumStateOfChargePercent');
      if (numericValue !== null && numericValue > 100) {
        errors.push('Das Planungsmaximum darf höchstens 100 % betragen.');
      }
      if (numericValue !== null && planningMinimum !== null && numericValue <= planningMinimum) {
        errors.push('Das Planungsmaximum muss größer als das Planungsminimum sein.');
      }
      break;
    }
    case 'battery.targetEndStateOfChargePercent': {
      const planningMinimum = getNumericDraftValue('battery.planningMinimumStateOfChargePercent');
      const planningMaximum = getNumericDraftValue('battery.planningMaximumStateOfChargePercent');
      if (numericValue !== null && planningMinimum !== null && planningMaximum !== null && (numericValue < planningMinimum || numericValue > planningMaximum)) {
        errors.push('Der Ziel-SoC muss innerhalb des Planungsbereichs liegen.');
      }
      break;
    }
    case 'battery.roundTripEfficiencyPercent':
      if (numericValue !== null && (numericValue < 50 || numericValue > 100)) {
        errors.push('Der Wirkungsgrad muss zwischen 50 und 100 % liegen.');
      }
      break;
    case 'battery.maximumChargePowerWatts':
    case 'battery.maximumDischargePowerWatts':
      if (numericValue !== null && numericValue <= 0) {
        errors.push('Die Leistung muss größer als 0 sein.');
      }
      break;
  }

  return errors;
}

function shouldRenderSwitch(field: FieldDefinition): boolean {
  const metadataEntry = metadataByKey.value.get(field.key);
  const currentValue = getDraftValue(field.key) || metadataEntry?.defaultValue || '';

  return currentValue === 'true' || currentValue === 'false' || metadataEntry?.inputKind === 'boolean';
}

function shouldRenderNumber(field: FieldDefinition): boolean {
  return metadataByKey.value.get(field.key)?.inputKind === 'number';
}

function shouldRenderSensitive(field: FieldDefinition): boolean {
  return metadataByKey.value.get(field.key)?.isSensitive === true;
}

function getFieldLabel(field: FieldDefinition): string {
  return metadataByKey.value.get(field.key)?.displayName ?? field.key;
}

function getFieldDescription(field: FieldDefinition): string {
  return field.helpText ?? metadataByKey.value.get(field.key)?.description ?? '';
}

function getSettingOrFallback(key: string): ControllerSettingResponseDto {
  const setting = settingByKey.value.get(key);
  const metadataEntry = metadataByKey.value.get(key);

  if (setting) {
    return setting;
  }

  return {
    key,
    value: null,
    isSensitive: metadataEntry?.isSensitive ?? false,
    isConfigured: false,
    updatedAtUtc: new Date().toISOString()
  };
}

function isSettingConfigured(key: string): boolean {
  const setting = settingByKey.value.get(key);
  const draftValue = getDraftValue(key).trim();

  if (!setting) {
    return draftValue !== '';
  }

  if (setting.isSensitive) {
    return setting.isConfigured || draftValue !== '';
  }

  return draftValue !== '';
}

function initializeDrafts(): void {
  const nextDrafts: Record<string, string> = {};

  for (const [key, metadataEntry] of metadataByKey.value.entries()) {
    const setting = settingByKey.value.get(key);
    nextDrafts[key] = setting?.isSensitive ? '' : (setting?.value ?? metadataEntry.defaultValue ?? '');
  }

  draftValues.value = nextDrafts;
  initialValues.value = { ...nextDrafts };
}

function ensureActiveSection(): void {
  if (!sections.value.some((section) => section.key === activeSectionKey.value)) {
    activeSectionKey.value = sections.value[0]?.key ?? 'battery';
  }
}

async function fetchJson<TResponse>(url: string, requestInit?: RequestInit): Promise<TResponse> {
  const response = await fetch(url, requestInit);

  if (!response.ok) {
    const error = (await response.json().catch(() => null)) as SettingsErrorDto | null;
    throw new Error(createApiErrorMessage(url, response.status, error));
  }

  return (await response.json()) as TResponse;
}

function createApiErrorMessage(url: string, status: number, error: SettingsErrorDto | null): string {
  const message = error?.exceptionMessage ?? error?.message ?? `Der API-Request ist mit HTTP ${status} fehlgeschlagen.`;
  const apiMessage = error?.message && error.message !== message ? `\nAPI-Meldung: ${error.message}` : '';
  const exceptionType = error?.exceptionType ? `\nException: ${error.exceptionType}` : '';
  const traceId = error?.traceId ? `\nTraceId: ${error.traceId}` : '';

  return `${message}\nURL: ${url}\nHTTP-Status: ${status}${apiMessage}${exceptionType}${traceId}`;
}

function createUiError(error: unknown, fallbackMessage: string): UiError {
  if (!(error instanceof Error)) {
    return {
      message: fallbackMessage,
      details: fallbackMessage
    };
  }

  const [message, ...detailLines] = error.message.split('\n');

  return {
    message,
    details: detailLines.length > 0 ? detailLines.join('\n') : error.message
  };
}

function createUpdatePayload(setting: ControllerSettingResponseDto, rawValue: string): UpdateControllerSettingRequestDto | null {
  const trimmedValue = rawValue.trim();

  if (setting.isSensitive && trimmedValue === '' && setting.isConfigured) {
    return null;
  }

  return { value: trimmedValue === '' ? null : trimmedValue };
}

function formatPercentValue(value: number): string {
  return `${formatDecimal(value, 1)} %`;
}

function formatDecimal(value: number, digits: number): string {
  return new Intl.NumberFormat('de-DE', {
    minimumFractionDigits: digits,
    maximumFractionDigits: digits
  }).format(value);
}

function clampPercent(value: number): number {
  return Math.max(0, Math.min(100, value));
}

onMounted(() => {
  void loadPageData();
});
</script>

<template>
  <v-container class="settings-page" fluid>
    <header class="settings-header">
      <div>
        <span class="settings-header__eyebrow">Konfiguration</span>
        <h1>Einstellungen</h1>
        <p>Controller-Konfiguration für Batterie, Preise, Prognosen und Systemverhalten</p>
      </div>

      <div class="settings-header__meta">
        <v-chip
          class="settings-header__state-chip"
          :class="hasUnsavedChanges ? 'settings-header__state-chip--dirty' : 'settings-header__state-chip--saved'"
          size="small"
          variant="flat"
        >
          {{ hasUnsavedChanges ? 'Ungespeichert' : 'Gespeichert' }}
        </v-chip>
        <span>Änderungen werden beim nächsten Entscheidungszyklus aktiv. Verbindungswerte können einen Reconnect erfordern.</span>
      </div>
    </header>

    <div v-if="isLoading" class="loading-panel">
      <v-progress-circular indeterminate color="primary" size="24" />
      <span>Einstellungen werden geladen...</span>
    </div>

    <v-alert v-if="pageError" class="mb-4" type="error" variant="tonal">
      {{ pageError.message }}
      <v-expansion-panels v-if="pageError.details" class="error-details" variant="accordion">
        <v-expansion-panel title="Fehlerdetails anzeigen">
          <v-expansion-panel-text>
            <pre>{{ pageError.details }}</pre>
          </v-expansion-panel-text>
        </v-expansion-panel>
      </v-expansion-panels>
    </v-alert>

    <v-alert v-if="status?.victronMqttLastError" class="mb-4" type="warning" variant="tonal">
      {{ status.victronMqttLastError }}
    </v-alert>

    <v-alert v-if="saveSuccess" class="mb-4" type="success" variant="tonal">
      Einstellungen wurden gespeichert.
    </v-alert>

    <v-alert v-if="validationErrors.length > 0" class="mb-4" type="error" variant="tonal">
      <strong>Bitte pruefen</strong>
      <ul class="message-list">
        <li v-for="error in validationErrors" :key="error">{{ error }}</li>
      </ul>
    </v-alert>

    <v-alert v-if="warningMessages.length > 0" class="mb-4" type="warning" variant="tonal">
      <strong>Hinweise</strong>
      <ul class="message-list">
        <li v-for="warning in warningMessages" :key="warning">{{ warning }}</li>
      </ul>
    </v-alert>

    <div class="settings-layout" :class="{ 'settings-layout--disabled': isLoading }">
      <nav class="section-nav" aria-label="Einstellungsbereiche">
        <span class="section-nav__title">Bereiche</span>
        <button
          v-for="section in sections"
          :key="section.key"
          class="section-nav__item"
          :class="{ 'section-nav__item--active': activeSectionKey === section.key }"
          type="button"
          @click="setActiveSection(section.key)"
        >
          <strong>{{ section.title }}</strong>
        </button>
      </nav>

      <main v-if="activeSection" class="settings-content">
        <section class="section-panel">
          <div class="section-panel__header">
            <div>
              <h2>{{ activeSection.title }}</h2>
              <p>{{ activeSection.description }}</p>
            </div>
          </div>

          <div v-if="activeSection.key === 'battery'" class="planning-range" :style="planningRangeStyle">
            <div class="planning-range__labels">
              <span>Hartes Minimum {{ formatPercentValue(getNumericDraftValue('battery.minimumStateOfChargePercent') ?? 0) }}</span>
              <span>Planung {{ formatPercentValue(getNumericDraftValue('battery.planningMinimumStateOfChargePercent') ?? 0) }} - {{ formatPercentValue(getNumericDraftValue('battery.planningMaximumStateOfChargePercent') ?? 0) }}</span>
              <span>Ziel am Ende {{ formatPercentValue(getNumericDraftValue('battery.targetEndStateOfChargePercent') ?? 0) }}</span>
            </div>
            <div class="planning-range__track" aria-hidden="true">
              <span class="planning-range__hard-min" />
              <span class="planning-range__window" />
              <span class="planning-range__target" />
            </div>
            <p>Planungsgrenzen nutzt der Forecast. Harte Grenzen gelten als Sicherheitsrahmen.</p>
          </div>

          <div v-for="group in activeSubgroups" :key="group.title" class="setting-group">
            <h3>{{ group.title }}</h3>

            <div class="setting-rows">
              <div
                v-for="field in group.fields"
                :key="field.key"
                class="setting-row"
                :class="{
                  'setting-row--important': field.category === 'important',
                  'setting-row--critical': field.category === 'critical'
                }"
              >
                <div class="setting-row__copy">
                  <label class="setting-row__label" :for="field.key">{{ getFieldLabel(field) }}</label>
                  <p>{{ getFieldDescription(field) }}</p>
                  <small v-if="field.category === 'critical'">Sicherheitsrelevant: Bitte Wert bewusst setzen.</small>
                  <small v-else-if="field.category === 'important'">Beeinflusst die Planung und Entscheidungen.</small>
                </div>

                <div class="setting-row__input">
                  <v-switch
                    v-if="shouldRenderSwitch(field)"
                    :id="field.key"
                    :model-value="getDraftValue(field.key) === 'true'"
                    color="primary"
                    density="compact"
                    hide-details="auto"
                    inset
                    :label="getDraftValue(field.key) === 'true' ? 'Aktiv' : 'Inaktiv'"
                    @update:model-value="setBooleanDraft(field.key, $event)"
                  />

                  <v-select
                    v-else-if="field.inputMode === 'timezone'"
                    :id="field.key"
                    v-model="draftValues[field.key]"
                    :items="timezoneOptions"
                    :error-messages="getFieldErrors(field.key)"
                    density="comfortable"
                    hide-details="auto"
                    variant="outlined"
                  />

                  <v-text-field
                    v-else
                    :id="field.key"
                    v-model="draftValues[field.key]"
                    :type="shouldRenderSensitive(field) ? 'password' : shouldRenderNumber(field) ? 'number' : 'text'"
                    :suffix="metadataByKey.get(field.key)?.unit ?? undefined"
                    :placeholder="metadataByKey.get(field.key)?.defaultValue ?? undefined"
                    :hint="shouldRenderSensitive(field) && settingByKey.get(field.key)?.isConfigured ? 'Gespeichert. Leer lassen, um den geheimen Wert beizubehalten.' : undefined"
                    :persistent-hint="shouldRenderSensitive(field) && settingByKey.get(field.key)?.isConfigured"
                    :error-messages="getFieldErrors(field.key)"
                    density="comfortable"
                    hide-details="auto"
                    variant="outlined"
                  />
                </div>
              </div>
            </div>
          </div>
        </section>
      </main>

      <aside class="summary-panel">
      <div class="summary-panel__header">
        <h2>Zusammenfassung</h2>
        <p>Aktueller Planungsrahmen und Betriebsstatus.</p>
        </div>

        <div class="summary-list">
          <div v-for="item in summaryItems" :key="item.label" class="summary-item">
            <span>{{ item.label }}</span>
            <strong>{{ item.value }}</strong>
          </div>
        </div>
      </aside>
    </div>

    <div class="action-bar">
      <div class="action-bar__copy">
        <strong>{{ hasUnsavedChanges ? 'Änderungen vorhanden' : 'Alles gespeichert' }}</strong>
        <span>Speichern schreibt die Werte in die Controller-Datenbank.</span>
      </div>

      <div class="action-bar__buttons">
        <v-btn variant="text" :disabled="isSaving || !hasUnsavedChanges" @click="resetChanges">Zurücksetzen</v-btn>
        <v-btn variant="text" :disabled="isLoading || isSaving" @click="loadPageData">Neu laden</v-btn>
        <v-btn color="primary" :disabled="!canSave" :loading="isSaving" prepend-icon="mdi-content-save" @click="saveSettings">Einstellungen speichern</v-btn>
      </div>
    </div>
  </v-container>
</template>

<style scoped src="./SettingsPage.css"></style>
