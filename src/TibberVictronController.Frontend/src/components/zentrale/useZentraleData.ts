import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue';
import { errorText, getJson, sendJson } from './zentraleApi';
import type {
  CarStatusDto, ControllerSettingResponseDto, ControllerStatusResponseDto, DashboardSummaryDto, EvBaseChoiceDto, EvChargePlanDto,
  EvControlDto, HotWaterReportDto, JointBatteryShadowDto, ManualChargeStatusResponseDto, WallboxStatusDto
} from './zentraleTypes';

/** Power below this is measurement noise; such a flow is shown as inactive instead of flickering. */
export const activeFlowWatts = 50;
/** Live values older than this are not presented as current. */
export const staleAfterMilliseconds = 120_000;

const fastIntervalMilliseconds = 15_000;
const slowIntervalMilliseconds = 60_000;

/*
 * One shared store for shell, dashboard and pages. Several components on screen must not each poll the Raspberry Pi,
 * so polling runs once while at least one component uses the data.
 */
const state = reactive({
  summary: null as DashboardSummaryDto | null,
  shadow: null as JointBatteryShadowDto | null,
  wallbox: null as WallboxStatusDto | null,
  car: null as CarStatusDto | null,
  evControl: null as EvControlDto | null,
  evPlan: null as EvChargePlanDto | null,
  baseChoice: null as EvBaseChoiceDto | null,
  hotWater: null as HotWaterReportDto | null,
  status: null as ControllerStatusResponseDto | null,
  legacyManualCharge: null as ManualChargeStatusResponseDto | null,
  settings: {} as Record<string, ControllerSettingResponseDto>,
  errors: {} as Record<string, string>,
  lastLoadedAtUtc: null as string | null
});
const now = ref(Date.now());
let subscribers = 0;
let fastTimer: number | undefined;
let slowTimer: number | undefined;
let clockTimer: number | undefined;
let fastRunning = false;
let slowRunning = false;

async function load<T>(source: string, url: string, assign: (value: T | null) => void, optional = false): Promise<void> {
  try {
    assign(await getJson<T>(url));
    delete state.errors[source];
  } catch (reason) {
    // Optional devices (wallbox, car, heat pump) may simply not be set up; that is not an error to show.
    if (optional) assign(null);
    else state.errors[source] = errorText(reason, `${source}: Daten konnten nicht geladen werden.`);
  }
}

async function refreshFast(): Promise<void> {
  if (fastRunning) return;
  fastRunning = true;
  try {
    await Promise.all([
      load<DashboardSummaryDto>('Energiedaten', '/api/dashboard/summary', v => { if (v) state.summary = v; }),
      load<JointBatteryShadowDto>('Akkusteuerung', '/api/joint-battery/shadow', v => { state.shadow = v; }, true),
      load<WallboxStatusDto>('Wallbox', '/api/ev-charging/status', v => { state.wallbox = v; }, true)
    ]);
    state.lastLoadedAtUtc = new Date().toISOString();
  } finally {
    fastRunning = false;
  }
}

async function refreshSlow(): Promise<void> {
  if (slowRunning) return;
  slowRunning = true;
  try {
    await Promise.all([
      load<CarStatusDto>('Fahrzeug', '/api/ev-charging/car', v => { state.car = v; }, true),
      load<EvControlDto>('Ladesteuerung', '/api/ev-charging/control', v => { state.evControl = v; }, true),
      load<EvChargePlanDto>('Ladeplan', '/api/ev-charging/charge-plan', v => { state.evPlan = v; }, true),
      load<EvBaseChoiceDto>('Grund-Ladestand', '/api/ev-charging/base-choice', v => { state.baseChoice = v; }, true),
      load<HotWaterReportDto>('Warmwasser', '/api/weishaupt/hot-water', v => { state.hotWater = v; }, true),
      load<ControllerStatusResponseDto>('Systemstatus', '/api/status', v => { state.status = v; }),
      load<ManualChargeStatusResponseDto>('Manuelle Ladung', '/api/manual-charge', v => { state.legacyManualCharge = v; }, true),
      refreshSettings()
    ]);
  } finally {
    slowRunning = false;
  }
}

async function refreshSettings(): Promise<void> {
  await load<{ settings: ControllerSettingResponseDto[] }>('Einstellungen', '/api/settings', v => {
    if (v) state.settings = Object.fromEntries(v.settings.map(s => [s.key, s]));
  });
}

async function refreshAll(): Promise<void> {
  await Promise.all([refreshFast(), refreshSlow()]);
}

function start(): void {
  void refreshAll();
  fastTimer = window.setInterval(() => void refreshFast(), fastIntervalMilliseconds);
  slowTimer = window.setInterval(() => void refreshSlow(), slowIntervalMilliseconds);
  clockTimer = window.setInterval(() => { now.value = Date.now(); }, 5_000);
}

function stop(): void {
  window.clearInterval(fastTimer);
  window.clearInterval(slowTimer);
  window.clearInterval(clockTimer);
}

function setting(key: string): string | null {
  return state.settings[key]?.value ?? null;
}

const live = computed(() => state.summary?.live ?? null);
const isStale = computed(() => !live.value || now.value - Date.parse(live.value.measuredAtUtc) > staleAfterMilliseconds);

/** Joint control owns both batteries; then commands go through the joint endpoints, otherwise only Victron is steerable. */
const jointControlActive = computed(() => !!state.shadow && state.shadow.status !== 'Disabled' && !state.shadow.isShadow);

const batteries = computed(() => (['victron', 'e3dc'] as const).map((id) => {
  const unit = state.shadow?.batteries.find(b => b.id === id) ?? null;
  const soc = id === 'victron' ? live.value?.batterySocPercent ?? unit?.socPercent ?? null : live.value?.supplementaryBatterySocPercent ?? unit?.socPercent ?? null;
  const watts = id === 'victron' ? live.value?.batteryPowerWatts ?? null : live.value?.supplementaryBatteryPowerWatts ?? null;
  const execution = state.shadow?.execution;
  const targetWatts = jointControlActive.value && execution?.ownsControl ? (id === 'victron' ? execution.victronTargetWatts : execution.e3dcTargetWatts) : null;
  const usableKwh = unit && soc != null ? Math.max(0, unit.capacityKwh * soc / 100 - unit.reserveKwh) : null;
  return {
    id, index: id === 'victron' ? 1 : 2,
    name: id === 'victron' ? 'Akku 1' : 'Akku 2',
    device: id === 'victron' ? 'Victron' : 'E3/DC',
    soc, watts, targetWatts, usableKwh,
    capacityKwh: unit?.capacityKwh ?? null,
    reserveKwh: unit?.reserveKwh ?? null,
    available: soc != null || watts != null
  };
}));

const evAvailable = computed(() => state.wallbox?.ocppEnabled === true);
const heatPumpAvailable = computed(() => setting('weishauptBiblockModbus.enabled') === 'true' || (state.hotWater?.samples.length ?? 0) > 0);

async function startJointManualCharge(targetKwh: number, maximumPowerKw: number | null): Promise<void> {
  await sendJson('POST', '/api/joint-battery/manual-charge', { targetKwh, maximumPowerKw }, 'Manuelle Ladung wurde nicht angenommen');
  await refreshFast();
}

async function stopJointManualCharge(): Promise<void> {
  await sendJson('DELETE', '/api/joint-battery/manual-charge', undefined, 'Manuelle Ladung konnte nicht beendet werden');
  await refreshFast();
}

async function startLegacyManualCharge(durationMinutes: number, powerKw: number): Promise<void> {
  state.legacyManualCharge = await sendJson<ManualChargeStatusResponseDto>('POST', '/api/manual-charge', { durationMinutes, powerKw }, 'Manuelle Ladung wurde nicht angenommen');
  await refreshFast();
}

async function stopLegacyManualCharge(): Promise<void> {
  state.legacyManualCharge = await sendJson<ManualChargeStatusResponseDto>('DELETE', '/api/manual-charge', undefined, 'Manuelle Ladung konnte nicht beendet werden');
  await refreshFast();
}

async function saveSetting(key: string, value: string): Promise<ControllerSettingResponseDto | null> {
  const saved = await sendJson<ControllerSettingResponseDto>('PUT', `/api/settings/${encodeURIComponent(key)}`, { value }, 'Einstellung wurde nicht gespeichert');
  // Read back from the store instead of trusting the response alone: only a persisted value counts as confirmed.
  await refreshSettings();
  return saved;
}

async function evAction(path: 'base-choice' | 'base-choice/stopped' | 'base-choice/target', body: Record<string, unknown>): Promise<void> {
  const transactionId = state.baseChoice?.transactionId;
  if (!transactionId) throw new Error('Es läuft kein Ladevorgang, auf den sich der Befehl beziehen kann.');
  await sendJson('PUT', `/api/ev-charging/${path}`, { transactionId, ...body }, 'Befehl an die Ladesteuerung wurde nicht angenommen');
  await refreshSlow();
}

export function useZentraleData() {
  onMounted(() => {
    subscribers += 1;
    if (subscribers === 1) start();
  });
  onBeforeUnmount(() => {
    subscribers -= 1;
    if (subscribers === 0) stop();
  });

  return {
    state, now, live, isStale, batteries, jointControlActive, evAvailable, heatPumpAvailable,
    setting, refreshAll, refreshFast, refreshSlow, refreshSettings, saveSetting,
    startJointManualCharge, stopJointManualCharge, startLegacyManualCharge, stopLegacyManualCharge, evAction
  };
}

export type ZentraleData = ReturnType<typeof useZentraleData>;
