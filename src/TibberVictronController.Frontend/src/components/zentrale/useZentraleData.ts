import { HubConnectionBuilder, HttpTransportType, LogLevel, type HubConnection } from '@microsoft/signalr';
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue';
import { errorText, getJson, sendJson } from './zentraleApi';
import type {
  AutomationStatusDto, CarStatusDto, ControllerSettingResponseDto, ControllerStatusResponseDto, DashboardSummaryDto, EvBaseChoiceDto, EvChargePlanDto,
  EvControlDto, HotWaterReportDto, JointBatteryShadowDto, ManualChargeStatusResponseDto, WallboxStatusDto
} from './zentraleTypes';
import type { DashboardLiveStateDto } from '../leitstand/leitstandTypes';

/** Power below this is measurement noise; such a flow is shown as inactive instead of flickering. */
export const activeFlowWatts = 50;
/** Live values older than this are not presented as current. */
export const staleAfterMilliseconds = 120_000;

/*
 * The server sends changes as they happen (SignalR): every stored live sample with its values, and a short note when
 * a decision, the joint battery control, the wallbox or the car charging control changed. Only the changed area is
 * reloaded. Timers remain as safety nets only: a slow resync for sources without change notes (car, hot water,
 * settings changed elsewhere) and polling while the live connection is down.
 */
const resyncMilliseconds = 60_000;
const fallbackPollMilliseconds = 15_000;
const reconnectMilliseconds = 5_000;

export type LiveConnectionState = 'connecting' | 'connected' | 'disconnected';

/*
 * One shared store for shell, dashboard and pages. Several components on screen must not each talk to the Raspberry Pi,
 * so the connection and the loads run once while at least one component uses the data.
 */
const state = reactive({
  summary: null as DashboardSummaryDto | null,
  pushedLive: null as DashboardLiveStateDto | null,
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
  automations: null as AutomationStatusDto[] | null,
  errors: {} as Record<string, string>,
  lastLoadedAtUtc: null as string | null,
  connection: 'connecting' as LiveConnectionState
});
const now = ref(Date.now());
let subscribers = 0;
let resyncTimer: number | undefined;
let fallbackTimer: number | undefined;
let reconnectTimer: number | undefined;
let clockTimer: number | undefined;
let hub: HubConnection | null = null;
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

const loadSummary = () => load<DashboardSummaryDto>('Energiedaten', '/api/dashboard/summary', v => { if (v) state.summary = v; });
const loadShadow = () => load<JointBatteryShadowDto>('Akkusteuerung', '/api/joint-battery/shadow', v => { state.shadow = v; }, true);
const loadWallbox = () => load<WallboxStatusDto>('Wallbox', '/api/ev-charging/status', v => { state.wallbox = v; }, true);
const loadLegacyManualCharge = () => load<ManualChargeStatusResponseDto>('Manuelle Ladung', '/api/manual-charge', v => { state.legacyManualCharge = v; }, true);

async function loadEv(): Promise<void> {
  await Promise.all([
    load<CarStatusDto>('Fahrzeug', '/api/ev-charging/car', v => { state.car = v; }, true),
    load<EvControlDto>('Ladesteuerung', '/api/ev-charging/control', v => { state.evControl = v; }, true),
    load<EvChargePlanDto>('Ladeplan', '/api/ev-charging/charge-plan', v => { state.evPlan = v; }, true),
    load<EvBaseChoiceDto>('Grund-Ladestand', '/api/ev-charging/base-choice', v => { state.baseChoice = v; }, true)
  ]);
}

async function refreshFast(): Promise<void> {
  if (fastRunning) return;
  fastRunning = true;
  try {
    await Promise.all([loadSummary(), loadShadow(), loadWallbox()]);
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
      loadEv(),
      load<HotWaterReportDto>('Warmwasser', '/api/weishaupt/hot-water', v => { state.hotWater = v; }, true),
      refreshStatus(),
      refreshAutomations(),
      loadLegacyManualCharge(),
      refreshSettings()
    ]);
  } finally {
    slowRunning = false;
  }
}

async function refreshAutomations(): Promise<void> {
  await load<AutomationStatusDto[]>('Automationen', '/api/automations', v => { state.automations = v; });
}

async function refreshStatus(): Promise<void> {
  await load<ControllerStatusResponseDto>('Systemstatus', '/api/status', v => { state.status = v; });
}

async function refreshSettings(): Promise<void> {
  await load<{ settings: ControllerSettingResponseDto[] }>('Einstellungen', '/api/settings', v => {
    if (v) state.settings = Object.fromEntries(v.settings.map(s => [s.key, s]));
  });
}

async function refreshAll(): Promise<void> {
  await Promise.all([refreshFast(), refreshSlow()]);
}

/**
 * Runs a reload at most once per gap, however many change notes arrive. A note during a running reload is not lost:
 * it schedules exactly one more reload afterwards, so the last change is always fetched.
 */
function coalesced(task: () => Promise<unknown>, gapMilliseconds: number): () => void {
  let running = false;
  let pending = false;
  let lastStartedAt = 0;
  let timer: number | undefined;
  async function run(): Promise<void> {
    timer = undefined;
    if (running) { pending = true; return; }
    const wait = lastStartedAt + gapMilliseconds - Date.now();
    if (wait > 0) { timer = window.setTimeout(() => void run(), wait); return; }
    running = true;
    lastStartedAt = Date.now();
    try {
      await task();
    } finally {
      running = false;
      if (pending) { pending = false; void run(); }
    }
  }
  return () => { if (timer === undefined) void run(); };
}

// The summary contains the full plan calculation; one reload per burst of decisions is plenty.
const onDecisionChanged = coalesced(async () => {
  await Promise.all([loadSummary(), refreshAutomations(), refreshStatus(), loadLegacyManualCharge()]);
  state.lastLoadedAtUtc = new Date().toISOString();
}, 10_000);
const onJointChanged = coalesced(() => Promise.all([loadShadow(), refreshAutomations()]), 3_000);
const onWallboxChanged = coalesced(loadWallbox, 2_000);
const onEvChanged = coalesced(() => Promise.all([loadEv(), refreshAutomations()]), 5_000);

function applyLive(live: DashboardLiveStateDto): void {
  state.pushedLive = live;
  state.lastLoadedAtUtc = new Date().toISOString();
}

function startFallbackPolling(): void {
  if (fallbackTimer !== undefined) return;
  fallbackTimer = window.setInterval(() => void refreshAll(), fallbackPollMilliseconds);
}

function stopFallbackPolling(): void {
  window.clearInterval(fallbackTimer);
  fallbackTimer = undefined;
}

function scheduleReconnect(): void {
  if (reconnectTimer !== undefined || subscribers === 0) return;
  reconnectTimer = window.setTimeout(() => {
    reconnectTimer = undefined;
    void connect();
  }, reconnectMilliseconds);
}

function markDisconnected(): void {
  state.connection = 'disconnected';
  startFallbackPolling();
}

async function connect(): Promise<void> {
  if (hub || subscribers === 0) return;
  state.connection = 'connecting';
  const connection = new HubConnectionBuilder()
    .withUrl('/api/hubs/dashboard', { transport: HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling })
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(LogLevel.Warning)
    .build();
  connection.on('dashboardLiveStateUpdated', applyLive);
  connection.on('dashboardDecisionUpdated', onDecisionChanged);
  connection.on('dashboardChanged', (change: { area: string }) => {
    if (change.area === 'joint') onJointChanged();
    else if (change.area === 'wallbox') onWallboxChanged();
    else if (change.area === 'ev') onEvChanged();
  });
  connection.onreconnecting(() => markDisconnected());
  connection.onreconnected(() => {
    state.connection = 'connected';
    stopFallbackPolling();
    // Changes during the gap were not sent; fetch everything once.
    void refreshAll();
  });
  connection.onclose(() => {
    if (hub !== connection) return;
    hub = null;
    markDisconnected();
    scheduleReconnect();
  });
  hub = connection;
  try {
    await connection.start();
    state.connection = 'connected';
    stopFallbackPolling();
  } catch {
    if (hub === connection) hub = null;
    markDisconnected();
    scheduleReconnect();
  }
}

async function disconnect(): Promise<void> {
  window.clearTimeout(reconnectTimer);
  reconnectTimer = undefined;
  const connection = hub;
  hub = null;
  try {
    await connection?.stop();
  } catch {
    // Leaving the page; nothing left to clean up.
  }
}

// Browsers throttle background tabs and phones drop connections while asleep; coming back must not show old values.
function onVisibilityChange(): void {
  if (document.visibilityState !== 'visible') return;
  void refreshAll();
  if (!hub) void connect();
}

function start(): void {
  void refreshAll();
  void connect();
  resyncTimer = window.setInterval(() => { if (state.connection === 'connected') void refreshAll(); }, resyncMilliseconds);
  clockTimer = window.setInterval(() => { now.value = Date.now(); }, 5_000);
  document.addEventListener('visibilitychange', onVisibilityChange);
}

function stop(): void {
  window.clearInterval(resyncTimer);
  window.clearInterval(clockTimer);
  stopFallbackPolling();
  document.removeEventListener('visibilitychange', onVisibilityChange);
  void disconnect();
}

function setting(key: string): string | null {
  return state.settings[key]?.value ?? null;
}

// The pushed sample and the summary's sample are the same kind of value; show whichever was measured last.
const live = computed(() => {
  const fromSummary = state.summary?.live ?? null;
  const pushed = state.pushedLive;
  if (!pushed) return fromSummary;
  if (!fromSummary) return pushed;
  return Date.parse(pushed.measuredAtUtc) >= Date.parse(fromSummary.measuredAtUtc) ? pushed : fromSummary;
});
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
/** Latest compressor demand if younger than ten minutes; older values say nothing about now. */
const heatPumpDemand = computed(() => {
  const demand = state.hotWater?.demand;
  return demand && now.value - Date.parse(demand.measuredAtUtc) <= 10 * 60_000 ? demand.percent : null;
});
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
  void refreshAutomations();
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
    state, now, live, isStale, batteries, jointControlActive, evAvailable, heatPumpAvailable, heatPumpDemand,
    setting, refreshAll, refreshFast, refreshSlow, refreshSettings, refreshStatus, refreshAutomations, saveSetting,
    startJointManualCharge, stopJointManualCharge, startLegacyManualCharge, stopLegacyManualCharge, evAction
  };
}

export type ZentraleData = ReturnType<typeof useZentraleData>;
