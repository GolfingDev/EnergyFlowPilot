<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import { useRoute } from 'vue-router';

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

interface HagerEnergyAuthorizationUrlResponseDto {
  authorizationUrl: string;
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

type SectionKey = 'operations' | 'battery' | 'control' | 'sources' | 'forecast' | 'advanced';
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

interface SelectOption {
  title: string;
  value: string;
}

const sectionDefinitions: SectionDefinition[] = [
  { key: 'operations', title: 'Betrieb', description: 'Simulationsmodus, Worker-Intervalle, Dashboard-Aktualisierung und Protokollierung.' },
  { key: 'battery', title: 'Batterie', description: 'Kapazität, harte Grenzen, Planungsprofil und Wirkungsgrad.' },
  { key: 'control', title: 'Steuerung', description: 'Parameter, die direkt Lade-, Entlade- und Idle-Entscheidungen beeinflussen.' },
  { key: 'sources', title: 'Datenquellen', description: 'Live-Datenquellen sowie Victron- und Hager-Verbindungen.' },
  { key: 'forecast', title: 'Prognosen', description: 'Tibber-Preise, PV-Prognose und Verbrauchsannahmen.' },
  { key: 'advanced', title: 'Erweitert', description: 'Roh-Topics, OAuth-Endpunkte, SMTP-Details und technische Sonderwerte.' }
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
  { key: 'tibber.accessToken', section: 'forecast', subgroup: 'Tibber', category: 'critical', helpText: 'Token werden nie im Klartext angezeigt. Leer lassen behält einen vorhandenen geheimen Wert.' },
  { key: 'tibber.homeSelection', section: 'forecast', subgroup: 'Tibber', category: 'normal' },
  { key: 'forecast.horizonHours', section: 'forecast', subgroup: 'Planungshorizont', category: 'important' },
  { key: 'gridFeedIn.compensationPricePerKwh', section: 'control', subgroup: 'Wirtschaftliche Steuerung', category: 'important' },
  { key: 'pvForecast.apiKey', section: 'forecast', subgroup: 'Forecast.Solar', category: 'critical', helpText: 'Optionaler Forecast.Solar API-Key für bezahlte Pläne. Leer lassen behält einen vorhandenen geheimen Wert.' },
  { key: 'pvForecast.latitude', section: 'forecast', subgroup: 'Standort', category: 'important' },
  { key: 'pvForecast.longitude', section: 'forecast', subgroup: 'Standort', category: 'important' },
  { key: 'pvForecast.peakPowerKwp', section: 'forecast', subgroup: 'PV-Anlage', category: 'important' },
  { key: 'pvForecast.declinationDegrees', section: 'forecast', subgroup: 'PV-Anlage', category: 'normal' },
  { key: 'pvForecast.azimuthDegrees', section: 'forecast', subgroup: 'PV-Anlage', category: 'normal' },
  { key: 'pvForecast.timeZone', section: 'forecast', subgroup: 'Standort', category: 'normal', inputMode: 'timezone' },
  { key: 'consumptionForecast.averageDailyConsumptionKwh', section: 'forecast', subgroup: 'Verbrauchsprofil', category: 'important' },
  { key: 'consumptionForecast.timeZone', section: 'advanced', subgroup: 'Zeitzonen', category: 'normal' },
  { key: 'victron.dryRun', section: 'operations', subgroup: 'Betriebsmodus', category: 'critical', helpText: 'Simulationsmodus: Entscheidungen werden berechnet, aber Hardware wird nicht aktiv gesteuert.' },
  { key: 'telemetry.sources.gridImportWatts', section: 'sources', subgroup: 'Live-Datenquellen', category: 'critical', helpText: 'Quelle fuer Netzbezug und Netzeinspeisung. Fuer die Steuerung typischerweise MQTT.' },
  { key: 'telemetry.sources.pvProductionWatts', section: 'sources', subgroup: 'Live-Datenquellen', category: 'important', helpText: 'Quelle fuer die PV-Leistung. Fuer E3/DC typischerweise Hager API.' },
  { key: 'telemetry.sources.batterySocPercent', section: 'sources', subgroup: 'Live-Datenquellen', category: 'critical', helpText: 'Quelle fuer den Akkuladestand. Fuer die Steuerung typischerweise MQTT.' },
  { key: 'decisionLog.retentionDays', section: 'operations', subgroup: 'Betrieb', category: 'normal', helpText: 'Wie lange Entscheidungsprotokolle gespeichert bleiben.' },
  { key: 'decisionWorker.intervalSeconds', section: 'operations', subgroup: 'Betrieb', category: 'important', helpText: 'Intervall für den automatischen Entscheidungs-Worker im Hintergrund.' },
  { key: 'dashboard.autoRefreshIntervalSeconds', section: 'operations', subgroup: 'Betrieb', category: 'normal', helpText: 'Intervall für die automatische Aktualisierung der Dashboard-Daten. 0 deaktiviert die Automatik.' },
  { key: 'notifications.workerFailureEmail.enabled', section: 'operations', subgroup: 'Benachrichtigungen', category: 'important', helpText: 'Versendet bei Worker-Fehlern automatisch eine E-Mail an den Betreiber.' },
  { key: 'notifications.workerFailureEmail.smtpHost', section: 'advanced', subgroup: 'SMTP', category: 'important' },
  { key: 'notifications.workerFailureEmail.smtpPort', section: 'advanced', subgroup: 'SMTP', category: 'normal' },
  { key: 'notifications.workerFailureEmail.smtpUsername', section: 'advanced', subgroup: 'SMTP', category: 'normal' },
  { key: 'notifications.workerFailureEmail.smtpPassword', section: 'advanced', subgroup: 'SMTP', category: 'critical', helpText: 'Geheimer SMTP-Zugang für den Versand von Fehlermails.' },
  { key: 'notifications.workerFailureEmail.fromAddress', section: 'advanced', subgroup: 'SMTP', category: 'important' },
  { key: 'notifications.workerFailureEmail.toAddress', section: 'advanced', subgroup: 'SMTP', category: 'important' },
  { key: 'notifications.workerFailureEmail.enableSsl', section: 'advanced', subgroup: 'SMTP', category: 'important' },
  { key: 'notifications.workerFailureEmail.subjectPrefix', section: 'advanced', subgroup: 'SMTP', category: 'normal' },
  { key: 'hagerEnergy.apiBaseUrl', section: 'advanced', subgroup: 'Hager OAuth & API', category: 'important' },
  { key: 'hagerEnergy.authorizationEndpoint', section: 'advanced', subgroup: 'Hager OAuth & API', category: 'important', helpText: 'Discovery-URL aus der Hager-Doku. Die Login- und Token-URL werden daraus automatisch gelesen.' },
  { key: 'hagerEnergy.redirectUri', section: 'advanced', subgroup: 'Hager OAuth & API', category: 'important', helpText: 'Muss exakt als Redirect URI in der Hager-App registriert sein.' },
  { key: 'hagerEnergy.apiKey', section: 'sources', subgroup: 'Hager Energy API', category: 'critical', helpText: 'Optionaler api_key Header gemaess Hager-Energy-OpenAPI, falls fuer deinen Client ausgegeben.' },
  { key: 'hagerEnergy.clientId', section: 'sources', subgroup: 'Hager Energy API', category: 'critical' },
  { key: 'hagerEnergy.clientSecret', section: 'sources', subgroup: 'Hager Energy API', category: 'critical', helpText: 'Leer lassen, wenn dein OAuth-Client ohne Secret arbeitet.' },
  { key: 'hagerEnergy.installationId', section: 'sources', subgroup: 'Hager Energy API', category: 'critical' },
  { key: 'victron.host', section: 'sources', subgroup: 'Victron MQTT', category: 'critical' },
  { key: 'victron.port', section: 'sources', subgroup: 'Victron MQTT', category: 'important' },
  { key: 'victron.portalId', section: 'sources', subgroup: 'Victron MQTT', category: 'important' },
  { key: 'victron.keepAliveSeconds', section: 'advanced', subgroup: 'Victron Laufzeit', category: 'important' },
  { key: 'victron.staleAfterSeconds', section: 'advanced', subgroup: 'Victron Laufzeit', category: 'critical' },
  { key: 'victron.controlMode', section: 'control', subgroup: 'Victron Steuermodus', category: 'critical', helpText: 'External ESS nur nutzen, wenn im Cerbo ESS auf Externe Steuerung gestellt ist.' },
  { key: 'victron.externalEss.switchModeViaMqtt', section: 'control', subgroup: 'Victron Steuermodus', category: 'critical', helpText: 'Setzt den Cerbo-ESS-Modus per MQTT passend zum ausgewaehlten Steuermodus.' },
  { key: 'victron.batteryIdleThresholdWatts', section: 'control', subgroup: 'Hub4-Steuerung', category: 'critical', helpText: 'Unterhalb dieser Zielleistung werden Laden und Entladen per Hub4-Flags gesperrt.' },
  { key: 'victron.topics.gridPower', section: 'advanced', subgroup: 'MQTT-Lesethemen', category: 'normal' },
  { key: 'victron.topics.batterySoc', section: 'advanced', subgroup: 'MQTT-Lesethemen', category: 'normal' },
  { key: 'victron.topics.batteryPower', section: 'advanced', subgroup: 'MQTT-Lesethemen', category: 'normal' },
  { key: 'victron.topics.houseConsumption', section: 'advanced', subgroup: 'MQTT-Lesethemen', category: 'normal' },
  { key: 'victron.writeTopics.chargeDischargeSetpoint', section: 'advanced', subgroup: 'MQTT-Schreibthemen', category: 'critical' },
  { key: 'victron.writeTopics.hub4Mode', section: 'advanced', subgroup: 'MQTT-Schreibthemen', category: 'critical' },
  { key: 'victron.externalEss.phaseCount', section: 'advanced', subgroup: 'External ESS', category: 'critical' },
  { key: 'victron.externalEss.writeTopics.l1AcPowerSetpoint', section: 'advanced', subgroup: 'External ESS', category: 'critical' },
  { key: 'victron.externalEss.writeTopics.l2AcPowerSetpoint', section: 'advanced', subgroup: 'External ESS', category: 'important' },
  { key: 'victron.externalEss.writeTopics.l3AcPowerSetpoint', section: 'advanced', subgroup: 'External ESS', category: 'important' },
  { key: 'victron.writeTopics.disableCharge', section: 'advanced', subgroup: 'MQTT-Schreibthemen', category: 'critical' },
  { key: 'victron.writeTopics.disableFeedIn', section: 'advanced', subgroup: 'MQTT-Schreibthemen', category: 'critical' }
];

const requiredKeys = new Set(['tibber.accessToken', 'battery.totalCapacityKwh']);
const timezoneOptions = [
  'Europe/Berlin',
  'UTC'
];
const telemetrySourceOptions: SelectOption[] = [
  { title: 'MQTT', value: 'victronMqtt' },
  { title: 'Hager API', value: 'hagerEnergyApi' }
];
const victronControlModeOptions: SelectOption[] = [
  { title: 'Normales ESS', value: 'normalEss' },
  { title: 'External ESS', value: 'externalEss' }
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

const route = useRoute();
const activeSectionKey = ref<SectionKey>('battery');
const settings = ref<ControllerSettingResponseDto[]>([]);
const metadata = ref<GuiMetadataResponseDto | null>(null);
const status = ref<ControllerStatusResponseDto | null>(null);
const draftValues = ref<Record<string, string>>({});
const initialValues = ref<Record<string, string>>({});
const isLoading = ref(false);
const isSaving = ref(false);
const isStartingHagerEnergyAuthorization = ref(false);
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
  ...getFieldErrors('battery.maximumDischargePowerWatts'),
  ...getFieldErrors('victron.controlMode'),
  ...getFieldErrors('victron.externalEss.phaseCount')
])));

const warningMessages = computed(() => {
  const warnings: string[] = [];
  const planningMaximum = getNumericDraftValue('battery.planningMaximumStateOfChargePercent');
  const keepAliveSeconds = getNumericDraftValue('victron.keepAliveSeconds');
  const staleAfterSeconds = getNumericDraftValue('victron.staleAfterSeconds');
  const decisionWorkerIntervalSeconds = getNumericDraftValue('decisionWorker.intervalSeconds');
  const victronControlMode = getDraftValue('victron.controlMode') || 'normalEss';

  if (planningMaximum !== null && planningMaximum > 98) {
    warnings.push('Das Planungsmaximum liegt über 98 %. Dadurch bleibt kaum Puffer für unerwarteten PV-Überschuss.');
  }

  if (keepAliveSeconds !== null && keepAliveSeconds < 5) {
    warnings.push('Ein sehr kurzes MQTT-KeepAlive kann unnötige Systemlast erzeugen.');
  }

  if (staleAfterSeconds !== null && staleAfterSeconds < 10) {
    warnings.push('Ein sehr kurzes Stale-Timeout kann zu häufigen Sicherheits-Fallbacks führen.');
  }

  if (victronControlMode === 'externalEss' && decisionWorkerIntervalSeconds !== null && decisionWorkerIntervalSeconds > 45) {
    warnings.push('External ESS erwartet regelmaessige Setpoints. Der Worker wird zur Laufzeit auf maximal 45 Sekunden begrenzt.');
  }

  if (victronControlMode === 'externalEss' && getDraftValue('victron.externalEss.switchModeViaMqtt') !== 'true') {
    warnings.push('External ESS ist gewaehlt, aber der Cerbo-ESS-Modus wird nicht per MQTT gesetzt. Stelle ihn manuell im Cerbo um oder aktiviere den MQTT-Umschalter.');
  }

  return warnings;
});

const hagerEnergyAuthMessage = computed(() => {
  const value = typeof route.query.hagerEnergyAuth === 'string'
    ? route.query.hagerEnergyAuth
    : null;

  if (value === null) {
    return null;
  }

  if (value.startsWith('success=')) {
    return {
      type: 'success' as const,
      text: 'Hager Energy wurde autorisiert. Die Tokens wurden gespeichert.'
    };
  }

  if (value.startsWith('error=')) {
    return {
      type: 'error' as const,
      text: `Hager Energy Autorisierung fehlgeschlagen: ${decodeURIComponent(value.slice('error='.length))}`
    };
  }

  return null;
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
    { label: 'Victron Steuerung', value: formatVictronControlMode(getDraftValue('victron.controlMode') || 'normalEss') },
    { label: 'ESS-Modus MQTT', value: getDraftValue('victron.externalEss.switchModeViaMqtt') === 'true' ? 'Aktiv' : 'Inaktiv' },
    { label: 'Netzquelle', value: formatTelemetrySource(getDraftValue('telemetry.sources.gridImportWatts') || 'victronMqtt') },
    { label: 'PV-Quelle', value: formatTelemetrySource(getDraftValue('telemetry.sources.pvProductionWatts') || 'victronMqtt') },
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
    applySectionFromRoute();
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

async function startHagerEnergyAuthorization(): Promise<void> {
  isStartingHagerEnergyAuthorization.value = true;
  pageError.value = null;

  try {
    const response = await fetchJson<HagerEnergyAuthorizationUrlResponseDto>('/api/hager-energy/oauth/authorize-url');
    window.location.assign(response.authorizationUrl);
  } catch (error) {
    pageError.value = createUiError(error, 'Die Hager-Energy-Autorisierung konnte nicht gestartet werden.');
  } finally {
    isStartingHagerEnergyAuthorization.value = false;
  }
}

function resetChanges(): void {
  draftValues.value = { ...initialValues.value };
  saveSuccess.value = false;
}

function setActiveSection(sectionKey: SectionKey): void {
  activeSectionKey.value = sectionKey;
}

function applySectionFromRoute(): void {
  const routeSection = typeof route.query.section === 'string'
    ? route.query.section
    : null;
  const matchingSection = routeSection === null
    ? null
    : sections.value.find((section) => section.key === routeSection);

  if (matchingSection) {
    activeSectionKey.value = matchingSection.key;
    return;
  }

  ensureActiveSection();
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

  if (key === 'victron.controlMode') {
    const controlMode = getDraftValue(key);
    if (controlMode !== 'normalEss' && controlMode !== 'externalEss') {
      errors.push('Der Victron-Steuermodus muss Normales ESS oder External ESS sein.');
    }
  }

  if (key === 'victron.externalEss.phaseCount' && numericValue !== null && (numericValue < 1 || numericValue > 3 || !Number.isInteger(numericValue))) {
    errors.push('External ESS Phasen muss 1, 2 oder 3 sein.');
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

function shouldRenderTelemetrySourceSelect(field: FieldDefinition): boolean {
  return field.key.startsWith('telemetry.sources.');
}

function shouldRenderVictronControlModeSelect(field: FieldDefinition): boolean {
  return field.key === 'victron.controlMode';
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

function getGroupDescription(groupTitle: string): string {
  switch (groupTitle) {
    case 'Betrieb':
      return 'Zentrale Intervalle für Worker, Dashboard und Protokollierung.';
    case 'Benachrichtigungen':
      return 'Nur der Hauptschalter. SMTP-Details liegen unter Erweitert.';
    case 'Hub4-Steuerung':
      return 'Schwellenwerte für echten Victron-Stillstand ohne unbeabsichtigtes Laden oder Entladen.';
    case 'Victron Steuermodus':
      return 'Waehlt, ob Victron selbst ESS regelt oder der Controller im Cerbo-Modus Externe Steuerung die Hub4-Setpoints fuehrt.';
    case 'Live-Datenquellen':
      return 'Legt fest, aus welchem System die Steuerung Netz, PV und SoC liest.';
    case 'External ESS':
      return 'Setpoints fuer den Cerbo-ESS-Modus Externe Steuerung. Bei dreiphasigen Anlagen Phasenanzahl auf 3 setzen.';
    case 'MQTT-Lesethemen':
    case 'MQTT-Schreibthemen':
      return 'Technische Topic-Zuordnung. Normalerweise nur ändern, wenn sich das Victron-MQTT-Schema ändert.';
    case 'SMTP':
      return 'Technische Versandparameter für Worker-Fehlermails.';
    default:
      return '';
  }
}

function formatTelemetrySource(value: string): string {
  return telemetrySourceOptions.find((option) => option.value === value)?.title ?? value;
}

function formatVictronControlMode(value: string): string {
  return victronControlModeOptions.find((option) => option.value === value)?.title ?? value;
}

function isEmphasizedGroup(groupTitle: string): boolean {
  return groupTitle === 'Betrieb' ||
    groupTitle === 'Betriebsmodus' ||
    groupTitle === 'Victron Steuermodus' ||
    groupTitle === 'Hub4-Steuerung' ||
    groupTitle === 'Live-Datenquellen' ||
    groupTitle === 'Benachrichtigungen';
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

watch(() => route.query.section, () => {
  applySectionFromRoute();
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

    <v-alert v-if="hagerEnergyAuthMessage" class="mb-4" :type="hagerEnergyAuthMessage.type" variant="tonal">
      {{ hagerEnergyAuthMessage.text }}
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

          <div v-for="group in activeSubgroups" :key="group.title" class="setting-group"
            :class="{ 'setting-group--emphasized': isEmphasizedGroup(group.title) }">
            <h3>{{ group.title }}</h3>
            <p v-if="getGroupDescription(group.title)" class="setting-group__description">
              {{ getGroupDescription(group.title) }}
            </p>

            <div v-if="group.title === 'Hager Energy OAuth'" class="setting-group__actions">
              <v-btn
                color="primary"
                prepend-icon="mdi-login"
                :loading="isStartingHagerEnergyAuthorization"
                :disabled="isStartingHagerEnergyAuthorization || hasUnsavedChanges"
                @click="startHagerEnergyAuthorization"
              >
                Mit Hager Energy verbinden
              </v-btn>
              <span>Vorher Client-ID, Discovery-URL und Redirect URI speichern.</span>
            </div>

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
                    v-else-if="shouldRenderTelemetrySourceSelect(field)"
                    :id="field.key"
                    v-model="draftValues[field.key]"
                    :items="telemetrySourceOptions"
                    item-title="title"
                    item-value="value"
                    :error-messages="getFieldErrors(field.key)"
                    density="comfortable"
                    hide-details="auto"
                    variant="outlined"
                  />

                  <v-select
                    v-else-if="shouldRenderVictronControlModeSelect(field)"
                    :id="field.key"
                    v-model="draftValues[field.key]"
                    :items="victronControlModeOptions"
                    item-title="title"
                    item-value="value"
                    :error-messages="getFieldErrors(field.key)"
                    density="comfortable"
                    hide-details="auto"
                    variant="outlined"
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

