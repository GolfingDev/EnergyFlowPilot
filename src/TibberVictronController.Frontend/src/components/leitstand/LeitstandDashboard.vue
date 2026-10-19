<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import type { BatterySavingsResponseDto, ControllerStatusResponseDto, DecisionLogEntryResponseDto, ManualChargeStatusResponseDto } from '../dashboard/dashboardTypes';
import { getDecisionLabel } from '../dashboard/dashboardFormatters';
import LeitstandHotWaterPanel from './LeitstandHotWaterPanel.vue';
import {
  leitstandCategoryLabels,
  type DashboardPlanSlotDto,
  type DashboardReviewHourDto,
  type DashboardPlanSegmentDto,
  type DashboardReviewDto,
  type DashboardSummaryDto,
  type LeitstandCategory,
  type LeitstandSeverity
} from './leitstandTypes';

const props = defineProps<{
  dark: boolean;
  next?: boolean;
  status: ControllerStatusResponseDto | null;
  decisionLogEntries: DecisionLogEntryResponseDto[];
  manualCharge: ManualChargeStatusResponseDto | null;
  isManualChargeBusy: boolean;
}>();

const emit = defineEmits<{
  (event: 'start-manual-charge', durationMinutes: number, powerKw: number): void;
  (event: 'stop-manual-charge'): void;
}>();

const summaryRefreshMilliseconds = 15_000;
const reviewRefreshMilliseconds = 300_000;

const summary = ref<DashboardSummaryDto | null>(null);
const review = ref<DashboardReviewDto | null>(null);
const summaryError = ref<string | null>(null);
const reviewError = ref<string | null>(null);
const reviewHours = ref(24);
type SavingsPeriod = 'day' | 'yesterday' | 'week' | 'month' | 'year';
const savingsPeriods: ReadonlyArray<{ value: SavingsPeriod; label: string }> = [
  { value: 'day', label: 'Heute' },
  { value: 'yesterday', label: 'Gestern' },
  { value: 'week', label: 'Woche' },
  { value: 'month', label: 'Monat' },
  { value: 'year', label: 'Jahr' }
];
const savingsPeriod = ref<SavingsPeriod>('day');
const savings = ref<BatterySavingsResponseDto | null>(null);
const savingsError = ref<string | null>(null);
const loadedLogEntries = ref<DecisionLogEntryResponseDto[] | null>(null);
const logEntries = computed({
  get: () => loadedLogEntries.value ?? props.decisionLogEntries,
  set: (value: DecisionLogEntryResponseDto[]) => { loadedLogEntries.value = value; }
});
const manualDurationMinutes = ref(30);
const manualPowerKw = ref(2.5);
let summaryTimer: ReturnType<typeof window.setInterval> | null = null;
let reviewTimer: ReturnType<typeof window.setInterval> | null = null;

const numberFormat = (digits: number) => new Intl.NumberFormat('de-DE', { minimumFractionDigits: digits, maximumFractionDigits: digits });
const timeFormat = new Intl.DateTimeFormat('de-DE', { hour: '2-digit', minute: '2-digit' });
const dayTimeFormat = new Intl.DateTimeFormat('de-DE', { weekday: 'short', hour: '2-digit', minute: '2-digit' });

function kw(watts: number | null | undefined): string {
  if (typeof watts !== 'number') {
    return '–';
  }

  return Math.abs(watts) < 1000 ? `${numberFormat(0).format(watts)} W` : `${numberFormat(2).format(watts / 1000)} kW`;
}

function kwh(value: number): string {
  return `${numberFormat(value < 10 ? 2 : 1).format(value)} kWh`;
}

function percent(value: number | null | undefined): string {
  return typeof value === 'number' ? `${numberFormat(0).format(value)} %` : '–';
}

function price(value: number): string {
  return numberFormat(3).format(value);
}

function time(iso: string): string {
  return timeFormat.format(new Date(iso));
}

function dayTime(iso: string): string {
  const date = new Date(iso);
  return date.toDateString() === new Date().toDateString() ? timeFormat.format(date) : dayTimeFormat.format(date);
}

const live = computed(() => summary.value?.live ?? null);
const current = computed(() => summary.value?.current ?? null);
const supplementaryChargeFromSiteWatts = computed(() => {
  const battery = live.value?.supplementaryBatteryPowerWatts;
  return typeof battery === 'number' ? Math.max(0, battery - Math.max(0, live.value?.supplementaryPvProductionWatts ?? 0)) : 0;
});

const nextSegment = computed<DashboardPlanSegmentDto | null>(() => {
  const segments = summary.value?.planSegments ?? [];
  const now = Date.now();
  return segments.find((segment) => new Date(segment.startsAtUtc).getTime() > now && segment.category !== current.value?.category) ?? null;
});

/** One plain sentence: what the Victron battery does, what the E3/DC does and what comes next. */
const situationSentence = computed(() => {
  if (!current.value) {
    return 'Noch keine Daten.';
  }

  const battery = live.value?.batteryPowerWatts ?? 0;
  const parts: string[] = [];

  switch (current.value.category) {
    case 'discharge':
      parts.push(`Der Akku deckt ${kw(Math.abs(battery))} Verbrauch.`);
      break;
    case 'pvCharge':
      parts.push(`Der Akku nimmt ${kw(Math.max(0, battery))} PV-Überschuss auf.`);
      break;
    case 'gridCharge':
      parts.push(`Der Akku lädt mit ${kw(current.value.targetPowerWatts)} aus dem Netz.`);
      break;
    case 'hold':
      parts.push('Der Akku hält seine Energie für teurere Stunden zurück.');
      break;
    case 'full':
      parts.push('Der Akku ist voll.');
      break;
    default:
      parts.push(current.value.ruleId === 'MIN_SOC_RESERVE' ? 'Der Akku steht auf der Reserve, der Verbrauch kommt aus dem Netz.' : 'Der Akku ist in Ruhe.');
  }

  const supplementaryBattery = live.value?.supplementaryBatteryPowerWatts;
  if (typeof supplementaryBattery === 'number' && Math.abs(supplementaryBattery) > 50) {
    const source = supplementaryChargeFromSiteWatts.value > 100 ? 'aus dem Hausnetz' : 'aus eigener PV';
    parts.push(supplementaryBattery > 0
      ? `Das E3/DC lädt mit ${kw(supplementaryBattery)} ${source}.`
      : `Das E3/DC entlädt mit ${kw(Math.abs(supplementaryBattery))}.`);
  }

  if (nextSegment.value) {
    parts.push(`Ab ${dayTime(nextSegment.value.startsAtUtc)}: ${leitstandCategoryLabels[nextSegment.value.category]}.`);
  }

  return parts.join(' ');
});

const severityRank: Record<LeitstandSeverity, number> = { critical: 0, warning: 1, info: 2, ok: 3 };
const sortedChecks = computed(() => [...(summary.value?.checks ?? [])].sort((a, b) => severityRank[a.severity] - severityRank[b.severity]));
const worstSeverity = computed<LeitstandSeverity>(() => sortedChecks.value[0]?.severity ?? 'ok');

function segmentWhy(segment: DashboardPlanSegmentDto): string {
  const priceRange = segment.minimumPricePerKwh === segment.maximumPricePerKwh
    ? `${price(segment.minimumPricePerKwh)} €/kWh`
    : `${price(segment.minimumPricePerKwh)}–${price(segment.maximumPricePerKwh)} €/kWh`;

  switch (segment.category) {
    case 'discharge':
      return `Strompreis ${priceRange}. Der Akku spart den Netzbezug.`;
    case 'pvCharge':
      return `Erwartet ${kwh(segment.expectedPvYieldKwh)} PV.`;
    case 'gridCharge':
      return `${kwh(segment.gridEnergyKwh)} aus dem Netz zu ${priceRange}. ${segment.reason ?? ''}`.trim();
    case 'hold':
      return segment.reason ?? `Strompreis ${priceRange}.`;
    case 'full':
      return 'Planungs-Maximum erreicht, weiterer Überschuss geht an das E3/DC oder ins Netz.';
    default:
      return segment.ruleId === 'MIN_SOC_RESERVE' ? `Akku auf Reserve, Netzbezug zu ${priceRange}.` : 'Kein Verbrauch aus dem Akku erwartet.';
  }
}

const visibleSegments = computed(() => (summary.value?.planSegments ?? [])
  .filter((segment) => new Date(segment.endsAtUtc).getTime() > Date.now())
  .slice(0, 7));

/* ---------- Plan chart geometry (one scale per band, labels only at values the chart reaches) ---------- */
const plan = computed(() => {
  const slots = summary.value?.planSlots ?? [];
  const width = 760;
  const left = 44;
  const right = 56;
  const socTop = 22;
  const socHeight = 118;
  const priceTop = 172;
  const priceHeight = 96;
  const step = slots.length > 0 ? (width - left - right) / slots.length : 0;
  const maxPvKw = Math.max(4, ...slots.map((slot) => slot.expectedPvYieldKwh * 4));
  const maxPrice = Math.max(0.5, ...slots.map((slot) => slot.pricePerKwh));
  const priceTicks = [0, maxPrice / 2, maxPrice];
  const socY = (value: number) => socTop + socHeight - value / 100 * socHeight;
  const pvY = (kwValue: number) => socTop + socHeight - kwValue / maxPvKw * socHeight;
  const center = (index: number) => left + step * index + step / 2;

  const pvLine = slots.map((slot, index) => `${center(index)},${pvY(slot.expectedPvYieldKwh * 4)}`).join(' ');
  const pvArea = slots.length > 0 ? `${center(0)},${socTop + socHeight} ${pvLine} ${center(slots.length - 1)},${socTop + socHeight}` : '';
  const socLine = slots.map((slot, index) => `${center(index)},${socY(slot.stateOfChargeAfterPercent)}`).join(' ');
  const bars = slots.map((slot, index) => {
    const height = slot.pricePerKwh <= 0 ? 2 : slot.pricePerKwh / maxPrice * priceHeight;
    return { x: left + step * index + 1, y: priceTop + priceHeight - height, width: Math.max(1, step - 2), height, category: slot.category };
  });
  const labels = slots
    .map((slot, index) => ({ date: new Date(slot.startsAtUtc), index }))
    .filter(({ date }) => date.getMinutes() === 0 && date.getHours() % 3 === 0)
    .map(({ date, index }) => ({ x: left + step * index, text: `${String(date.getHours()).padStart(2, '0')}:00` }));
  const firstStart = slots.length > 0 ? new Date(slots[0].startsAtUtc).getTime() : 0;
  const slotMs = slots.length > 0 ? new Date(slots[0].endsAtUtc).getTime() - firstStart : 1;
  const nowX = left + Math.min(1, Math.max(0, (Date.now() - firstStart) / slotMs)) * step;
  const lastSoc = slots.length > 0 ? { x: center(slots.length - 1), y: socY(slots[slots.length - 1].stateOfChargeAfterPercent) } : null;

  return {
    width, left, right, socTop, socHeight, priceTop, priceHeight, maxPvKw, priceTicks, socY, pvY,
    pvLine, pvArea, socLine, bars, labels, nowX, lastSoc, hasData: slots.length > 1
  };
});

/* ---------- Review chart geometry ---------- */
const reviewChart = computed(() => {
  const hours = review.value?.hours ?? [];
  const width = 760;
  const left = 44;
  const right = 20;
  const socTop = 22;
  const socHeight = 86;
  const zero = 176;
  const upHeight = 40;
  const downHeight = 56;
  const step = hours.length > 0 ? (width - left - right) / hours.length : 0;
  const maxUp = Math.max(1, ...hours.map((hour) => hour.gridImportKwh));
  const maxDown = Math.max(1, ...hours.map((hour) => hour.batteryDischargeKwh));
  const socY = (value: number) => socTop + socHeight - value / 100 * socHeight;
  const socPoints = hours
    .map((hour, index) => (hour.batterySocEndPercent === null ? null : `${left + step * (index + 1)},${socY(hour.batterySocEndPercent)}`))
    .filter((point): point is string => point !== null)
    .join(' ');
  const quarter = step / 4;
  const bars = hours.flatMap((hour, index) => {
    const x = left + step * index;
    return [
      { key: `i${index}`, index, x: x + 1, y: zero - hour.gridImportKwh / maxUp * upHeight, width: Math.max(1, quarter - 1), height: hour.gridImportKwh / maxUp * upHeight, kind: 'import' },
      { key: `d${index}`, index, x: x + quarter, y: zero, width: Math.max(1, quarter - 1), height: hour.batteryDischargeKwh / maxDown * downHeight, kind: 'discharge' },
      { key: `f${index}`, index, x: x + quarter * 2, y: zero, width: Math.max(1, quarter - 1), height: hour.batteryFeedInKwh / maxDown * downHeight, kind: 'feedin' },
      { key: `s${index}`, index, x: x + quarter * 3, y: zero, width: Math.max(1, quarter - 1), height: hour.supplementaryChargedFromBatteryKwh / maxDown * downHeight, kind: 'transfer' }
    ].filter((bar) => bar.height > 0.3);
  });
  const labels = hours
    .map((hour, index) => ({ date: new Date(hour.startsAtUtc), index }))
    .filter(({ date }, position) => hours.length <= 12 || date.getHours() % 3 === 0 || position === 0)
    .map(({ date, index }) => ({ x: left + step * index + step / 2, text: `${String(date.getHours()).padStart(2, '0')}` }));

  return { width, left, right, socTop, socHeight, zero, upHeight, downHeight, maxUp, maxDown, socY, socPoints, bars, labels, hasData: hours.length > 0 };
});

const reviewKpis = computed(() => {
  if (!review.value) {
    return [];
  }

  const value = review.value;
  const coverage = value.batteryDischargeKwh + value.gridImportWhileBatteryAvailableKwh > 0
    ? value.batteryDischargeKwh / (value.batteryDischargeKwh + value.gridImportWhileBatteryAvailableKwh) * 100
    : null;
  const supplementarySoc = value.supplementaryStateOfChargeStartPercent !== null && value.supplementaryStateOfChargeEndPercent !== null
    ? `E3/DC ${percent(value.supplementaryStateOfChargeStartPercent)} → ${percent(value.supplementaryStateOfChargeEndPercent)}`
    : 'E3/DC-Verlauf noch nicht gespeichert';

  return [
    {
      label: 'Aus dem Akku gedeckt',
      value: kwh(value.batteryDischargeKwh),
      hint: coverage === null ? 'Kein Verbrauch, solange Energie im Akku war' : `${numberFormat(0).format(coverage)} % des Bedarfs, solange Energie im Akku war`,
      severity: (coverage !== null && coverage < 90 ? 'warning' : 'ok') as LeitstandSeverity
    },
    {
      label: 'Netzladung',
      value: kwh(value.gridChargeKwh),
      hint: value.gridChargeKwh > 0.05 ? 'Nur bei Bedarf, siehe Entscheidungen' : 'Kein Strom aus dem Netz geladen',
      severity: (value.gridChargeKwh > 0.05 ? 'info' : 'ok') as LeitstandSeverity
    },
    {
      label: 'Einspeisung aus dem Akku',
      value: kwh(value.batteryFeedInKwh),
      hint: value.batteryFeedInKwh > 0.05 ? 'Der Akku hat ins Netz gespeist' : 'Keine Einspeisung',
      severity: (value.batteryFeedInKwh > 0.05 ? 'critical' : 'ok') as LeitstandSeverity
    },
    {
      label: 'E3/DC aus dem Victron-Akku geladen',
      value: kwh(value.supplementaryChargedFromBatteryKwh),
      hint: supplementarySoc,
      severity: (value.supplementaryChargedFromBatteryKwh > 0.1 ? 'critical' : 'ok') as LeitstandSeverity
    }
  ];
});

const statusRows = computed(() => [
  { label: 'Controller', value: props.status?.status ?? '–', ok: props.status?.status === 'Healthy' },
  { label: 'Victron MQTT', value: props.status?.victronMqttStatus ?? '–', ok: props.status?.victronMqttStatus === 'Connected' },
  {
    label: 'Letzte Messung',
    value: live.value ? `${time(live.value.measuredAtUtc)} Uhr` : '–',
    ok: live.value !== null && Date.now() - new Date(live.value.measuredAtUtc).getTime() < 120_000
  }
]);

const manualChargeLabel = computed(() => {
  if (!props.manualCharge?.isActive) {
    return 'Nicht aktiv';
  }

  return `${numberFormat(1).format(props.manualCharge.powerKw)} kW für noch ${Math.ceil(props.manualCharge.remainingSeconds / 60)} min`;
});

function categoryVar(category: LeitstandCategory): string {
  return `var(--ls-${category})`;
}

function planSlotTooltip(slot: DashboardPlanSlotDto | undefined): string {
  if (!slot) {
    return '';
  }

  return [
    `${dayTime(slot.startsAtUtc)}–${time(slot.endsAtUtc)}`,
    leitstandCategoryLabels[slot.category],
    `Preis: ${price(slot.pricePerKwh)} €/kWh`,
    `SoC danach: ${percent(slot.stateOfChargeAfterPercent)}`,
    `PV: ${kwh(slot.expectedPvYieldKwh)} · Verbrauch: ${kwh(slot.expectedConsumptionKwh)}`
  ].join('\n');
}

function reviewHourTooltip(hour: DashboardReviewHourDto | undefined): string {
  if (!hour) {
    return '';
  }

  return [
    `${dayTime(hour.startsAtUtc)}–${time(new Date(new Date(hour.startsAtUtc).getTime() + 3_600_000).toISOString())}`,
    `SoC: ${percent(hour.batterySocEndPercent)}`,
    `Netzbezug: ${kwh(hour.gridImportKwh)} · Akku: ${kwh(hour.batteryDischargeKwh)}`,
    `Akku-Einspeisung: ${kwh(hour.batteryFeedInKwh)} · E3/DC-Transfer: ${kwh(hour.supplementaryChargedFromBatteryKwh)}`,
    ...(hour.actualCost !== null && hour.baselineCost !== null
      ? [`Kosten: ${money(hour.actualCost)} · „Akku zuerst“: ${money(hour.baselineCost)}`]
      : [])
  ].join('\n');
}

async function fetchJson<T>(url: string): Promise<T> {
  const response = await fetch(url);
  if (!response.ok) {
    const body = await response.json().catch(() => null) as { message?: string } | null;
    throw new Error(body?.message ?? `Anfrage an ${url} mit HTTP ${response.status} fehlgeschlagen.`);
  }

  return await response.json() as T;
}

async function loadSummary(): Promise<void> {
  try {
    const [loadedSummary, loadedLogEntries] = await Promise.all([
      fetchJson<DashboardSummaryDto>('/api/dashboard/summary'),
      fetchJson<DecisionLogEntryResponseDto[]>('/api/decision/logs?maxCount=8')
    ]);
    summary.value = loadedSummary;
    logEntries.value = loadedLogEntries;
    summaryError.value = null;
  } catch (error) {
    summaryError.value = error instanceof Error ? error.message : 'Die Lage konnte nicht geladen werden.';
  }
}

async function loadReview(): Promise<void> {
  try {
    review.value = await fetchJson<DashboardReviewDto>(`/api/dashboard/review?hours=${reviewHours.value}`);
    reviewError.value = null;
  } catch (error) {
    reviewError.value = error instanceof Error ? error.message : 'Der Rückblick konnte nicht geladen werden.';
  }
}

function localIsoDate(date: Date): string {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
}

async function loadSavings(): Promise<void> {
  const today = new Date();
  const referenceDate = savingsPeriod.value === 'yesterday'
    ? new Date(today.getFullYear(), today.getMonth(), today.getDate() - 1)
    : today;
  const parameters = new URLSearchParams({
    period: savingsPeriod.value === 'yesterday' ? 'day' : savingsPeriod.value,
    referenceDate: localIsoDate(referenceDate),
    currency: 'EUR'
  });

  try {
    savings.value = await fetchJson<BatterySavingsResponseDto>(`/api/savings?${parameters.toString()}`);
    savingsError.value = null;
  } catch (error) {
    savingsError.value = error instanceof Error ? error.message : 'Die Ersparnis konnte nicht geladen werden.';
  }
}

async function changeSavingsPeriod(period: SavingsPeriod): Promise<void> {
  savingsPeriod.value = period;
  await loadSavings();
}

const currencyFormat = new Intl.NumberFormat('de-DE', { style: 'currency', currency: 'EUR' });

function money(value: number): string {
  return currencyFormat.format(value);
}

async function changeReviewHours(hours: number): Promise<void> {
  reviewHours.value = hours;
  await loadReview();
}

onMounted(() => {
  void loadSummary();
  void loadReview();
  void loadSavings();
  summaryTimer = window.setInterval(() => void loadSummary(), summaryRefreshMilliseconds);
  reviewTimer = window.setInterval(() => {
    void loadReview();
    void loadSavings();
  }, reviewRefreshMilliseconds);
});

onBeforeUnmount(() => {
  if (summaryTimer !== null) window.clearInterval(summaryTimer);
  if (reviewTimer !== null) window.clearInterval(reviewTimer);
});
</script>

<template>
  <div class="leitstand" :class="{ 'leitstand--dark': dark, 'leitstand--next': next }">
    <!-- Lage -->
    <section class="ls-zone" aria-labelledby="ls-situation">
      <h2 id="ls-situation" class="ls-eyebrow">Lage</h2>
      <div class="ls-panel ls-situation">
        <span class="ls-pill" :style="{ background: categoryVar(current?.category ?? 'idle') }">
          {{ leitstandCategoryLabels[current?.category ?? 'idle'] }}
        </span>
        <p class="ls-situation__text">{{ situationSentence }}</p>
        <div class="ls-situation__meta">
          <span><span class="ls-dot" :style="{ background: `var(--ls-${worstSeverity})` }" />
            {{ worstSeverity === 'ok' ? 'Alles in Ordnung' : worstSeverity === 'critical' ? 'Achtung, siehe Prüfung' : 'Hinweise vorhanden' }}</span>
          <span v-if="current?.pricePerKwh != null" class="ls-num">{{ price(current.pricePerKwh) }} €/kWh</span>
        </div>
      </div>
      <p v-if="summaryError" class="ls-error">{{ summaryError }}</p>
    </section>

    <!-- Jetzt -->
    <section class="ls-zone" aria-labelledby="ls-now">
      <div class="ls-zone__head">
        <h2 id="ls-now">Jetzt</h2>
        <span v-if="live" class="ls-muted ls-num">gemessen {{ time(live.measuredAtUtc) }} Uhr</span>
      </div>
      <div class="ls-split">
        <div class="ls-panel ls-flow">
          <svg viewBox="0 0 720 300" role="img" aria-label="Energiefluss zwischen Netz, Haus, Victron-Akku und E3/DC">
            <rect class="ls-boundary" x="430" y="14" width="276" height="272" rx="12" />
            <text class="ls-lbl" x="444" y="34">E3/DC · EIGENSTÄNDIG</text>

            <path class="ls-wire" :class="{ 'ls-wire--live': Math.abs(live?.gridPowerWatts ?? 0) > 30, 'ls-wire--crit': (live?.gridPowerWatts ?? 0) < -50 && (live?.batteryPowerWatts ?? 0) < -100 }" d="M170 150 L240 150" />
            <path class="ls-wire" :class="{ 'ls-wire--live': Math.abs(live?.batteryPowerWatts ?? 0) > 50, 'ls-wire--discharge': (live?.batteryPowerWatts ?? 0) < -50, 'ls-wire--pv': (live?.batteryPowerWatts ?? 0) > 50 }" d="M315 214 L315 190" />
            <path class="ls-wire" :class="{ 'ls-wire--live ls-wire--pv': (live?.supplementaryPvProductionWatts ?? 0) > 50 }" d="M450 102 C 410 102 410 150 390 150" />
            <path class="ls-wire" :class="{ 'ls-wire--live': Math.abs(live?.supplementaryBatteryPowerWatts ?? 0) > 50, 'ls-wire--crit': supplementaryChargeFromSiteWatts > 100 && (live?.batteryPowerWatts ?? 0) < -100 }" d="M450 226 C 410 226 410 160 390 160" />

            <rect class="ls-node" x="20" y="110" width="150" height="80" rx="10" />
            <text class="ls-lbl" x="36" y="134">NETZ</text>
            <text class="ls-val" x="36" y="162">{{ kw(Math.abs(live?.gridPowerWatts ?? 0)) }}</text>
            <text class="ls-sub" x="36" y="180">{{ (live?.gridPowerWatts ?? 0) < -30 ? 'Einspeisung' : (live?.gridPowerWatts ?? 0) > 30 ? 'Bezug' : 'ausgeglichen' }}</text>

            <rect class="ls-node" x="240" y="110" width="150" height="80" rx="10" />
            <text class="ls-lbl" x="256" y="134">HAUS</text>
            <text class="ls-val" x="256" y="162">{{ kw(live?.houseConsumptionWatts) }}</text>
            <text class="ls-sub" x="256" y="180">alle Phasen</text>

            <rect class="ls-node" x="240" y="214" width="150" height="72" rx="10" />
            <text class="ls-lbl" x="256" y="236">VICTRON-AKKU</text>
            <text class="ls-val" x="256" y="264">{{ percent(live?.batterySocPercent) }}</text>
            <text class="ls-sub" x="316" y="264">{{ (live?.batteryPowerWatts ?? 0) > 50 ? `lädt ${kw(live?.batteryPowerWatts)}` : (live?.batteryPowerWatts ?? 0) < -50 ? `entlädt ${kw(Math.abs(live?.batteryPowerWatts ?? 0))}` : 'Ruhe' }}</text>

            <rect class="ls-node" x="450" y="54" width="240" height="96" rx="10" />
            <text class="ls-lbl" x="466" y="78">PV (OST + WEST)</text>
            <text class="ls-val" x="466" y="110">{{ kw(live?.supplementaryPvProductionWatts) }}</text>

            <rect class="ls-node" x="450" y="178" width="240" height="96" rx="10" />
            <text class="ls-lbl" x="466" y="202">E3/DC-AKKU</text>
            <text class="ls-val" x="466" y="234">{{ percent(live?.supplementaryBatterySocPercent) }}</text>
            <text class="ls-sub" x="466" y="258">{{ (live?.supplementaryBatteryPowerWatts ?? 0) > 50
              ? `lädt ${kw(live?.supplementaryBatteryPowerWatts)} ${supplementaryChargeFromSiteWatts > 100 ? 'aus dem Hausnetz' : 'aus eigener PV'}`
              : (live?.supplementaryBatteryPowerWatts ?? 0) < -50 ? `entlädt ${kw(Math.abs(live?.supplementaryBatteryPowerWatts ?? 0))}` : 'Ruhe' }}</text>
          </svg>
        </div>
        <div class="ls-panel ls-decision-panel">
          <h3>{{ next ? 'Warum jetzt?' : 'Prüfung jetzt' }}</h3>
          <p v-if="next && current?.reason" class="ls-decision-reason">{{ current.reason }}</p>
          <ul class="ls-checks">
            <li v-for="check in sortedChecks" :key="check.checkId" class="ls-check" :class="`ls-check--${check.severity}`">
              <span class="ls-check__mark">{{ check.severity === 'ok' ? '✓' : check.severity === 'info' ? 'i' : '!' }}</span>
              <span>{{ check.message }}</span>
            </li>
          </ul>
        </div>
      </div>
    </section>

    <LeitstandHotWaterPanel />

    <!-- Plan -->
    <section class="ls-zone" aria-labelledby="ls-plan">
      <div class="ls-zone__head">
        <h2 id="ls-plan">Plan nächste 24 Stunden</h2>
        <span v-if="summary" class="ls-muted">Stand {{ time(summary.createdAtUtc) }} Uhr</span>
      </div>
      <div class="ls-split">
        <div class="ls-panel ls-chart-panel">
          <div class="ls-chart">
            <svg v-if="plan.hasData" :viewBox="`0 0 ${plan.width} 300`" role="img" aria-label="Plan: Ladezustand, PV und Strompreis je Viertelstunde">
              <text class="ls-band" :x="plan.left" :y="plan.socTop - 8">Ladezustand und PV</text>
              <g v-for="value in [0, 50, 100]" :key="`s${value}`">
                <line class="ls-grid" :x1="plan.left" :x2="plan.width - plan.right" :y1="plan.socY(value)" :y2="plan.socY(value)" />
                <text class="ls-axis" :x="plan.left - 6" :y="plan.socY(value) + 3" text-anchor="end">{{ value }}%</text>
              </g>
              <text v-for="value in [0, plan.maxPvKw / 2, plan.maxPvKw]" :key="`p${value}`" class="ls-axis"
                :x="plan.width - plan.right + 6" :y="plan.pvY(value) + 3">{{ numberFormat(1).format(value) }} kW</text>
              <polygon class="ls-pv-area" :points="plan.pvArea" />
              <polyline class="ls-pv-line" :points="plan.pvLine" />
              <polyline class="ls-soc" :points="plan.socLine" />
              <circle v-if="plan.lastSoc" class="ls-soc-dot" :cx="plan.lastSoc.x" :cy="plan.lastSoc.y" r="4" />

              <text class="ls-band" :x="plan.left" :y="plan.priceTop - 8">Strompreis und geplante Aktion (€/kWh)</text>
              <g v-for="value in plan.priceTicks" :key="`c${value}`">
                <line class="ls-grid" :x1="plan.left" :x2="plan.width - plan.right" :y1="plan.priceTop + plan.priceHeight - value / plan.priceTicks[2] * plan.priceHeight" :y2="plan.priceTop + plan.priceHeight - value / plan.priceTicks[2] * plan.priceHeight" />
                <text class="ls-axis" :x="plan.left - 6" :y="plan.priceTop + plan.priceHeight - value / plan.priceTicks[2] * plan.priceHeight + 3" text-anchor="end">{{ numberFormat(2).format(value) }}</text>
              </g>
              <rect v-for="(bar, index) in plan.bars" :key="`b${index}`" :class="{ 'ls-chart-hit': next }" :x="bar.x" :y="bar.y" :width="bar.width" :height="bar.height" rx="1.5" :style="{ fill: categoryVar(bar.category) }" :tabindex="next ? 0 : undefined">
                <title v-if="next">{{ planSlotTooltip(summary?.planSlots[index]) }}</title>
              </rect>
              <text v-for="label in plan.labels" :key="label.text + label.x" class="ls-axis" :x="label.x" y="292" text-anchor="middle">{{ label.text }}</text>
              <line class="ls-now" :x1="plan.nowX" :x2="plan.nowX" :y1="plan.socTop - 2" :y2="plan.priceTop + plan.priceHeight" />
              <text class="ls-now-label" :x="plan.nowX + 4" :y="plan.socTop + 10">jetzt</text>
            </svg>
            <p v-else class="ls-muted">Kein Plan verfügbar.</p>
          </div>
          <div class="ls-legend">
            <span v-for="category in (['discharge', 'pvCharge', 'full', 'hold', 'gridCharge', 'idle'] as LeitstandCategory[])" :key="category">
              <i :style="{ background: categoryVar(category) }" />{{ leitstandCategoryLabels[category] }}
            </span>
            <span><i class="ls-legend__line" />Ladezustand Victron</span>
          </div>
        </div>
        <div class="ls-panel">
          <h3>Nächste Schritte</h3>
          <ol class="ls-steps">
            <li v-for="segment in visibleSegments" :key="segment.startsAtUtc" class="ls-step">
              <span class="ls-step__time">{{ dayTime(segment.startsAtUtc) }}–{{ time(segment.endsAtUtc) }}</span>
              <span class="ls-step__bar" :style="{ background: categoryVar(segment.category) }" />
              <div>
                <div class="ls-step__title">{{ leitstandCategoryLabels[segment.category] }} · {{ percent(segment.stateOfChargeStartPercent) }} → {{ percent(segment.stateOfChargeEndPercent) }}</div>
                <p class="ls-step__why">{{ segmentWhy(segment) }}</p>
              </div>
            </li>
          </ol>
        </div>
      </div>
    </section>

    <!-- Rückblick -->
    <section class="ls-zone" aria-labelledby="ls-review">
      <div class="ls-zone__head">
        <h2 id="ls-review">Rückblick</h2>
        <div class="ls-tabs" role="group" aria-label="Zeitraum">
          <button v-for="hours in [12, 24, 48]" :key="hours" type="button" class="ls-tab" :class="{ 'ls-tab--active': reviewHours === hours }" @click="changeReviewHours(hours)">{{ hours }} h</button>
        </div>
      </div>
      <p v-if="reviewError" class="ls-error">{{ reviewError }}</p>
      <div class="ls-kpis">
        <div v-for="kpi in reviewKpis" :key="kpi.label" class="ls-kpi" :style="{ borderTopColor: `var(--ls-${kpi.severity})` }">
          <span class="ls-kpi__label">{{ kpi.label }}</span>
          <span class="ls-kpi__value">{{ kpi.value }}</span>
          <span class="ls-kpi__hint">{{ kpi.hint }}</span>
        </div>
      </div>
      <div class="ls-panel ls-savings">
        <div class="ls-zone__head">
          <h3>Ersparnis</h3>
          <div class="ls-tabs" role="group" aria-label="Zeitraum der Ersparnis">
            <button v-for="option in savingsPeriods" :key="option.value" type="button" class="ls-tab"
              :class="{ 'ls-tab--active': savingsPeriod === option.value }" @click="changeSavingsPeriod(option.value)">{{ option.label }}</button>
          </div>
        </div>
        <p v-if="savingsError" class="ls-error">{{ savingsError }}</p>
        <div v-else-if="savings" class="ls-savings__body">
          <div class="ls-savings__total">
            <span class="ls-kpi__label">Netto gespart</span>
            <span class="ls-savings__value" :class="{ 'ls-savings__value--negative': savings.aggregate.netSavings < 0 }">{{ money(savings.aggregate.netSavings) }}</span>
          </div>
          <dl class="ls-savings__rows">
            <div>
              <dt>Vermiedener Netzbezug</dt>
              <dd><span class="ls-num">+{{ money(savings.aggregate.dischargeAvoidedCost) }}</span>
                <small>{{ kwh(savings.aggregate.dischargedEnergyKwh) }}<template v-if="savings.aggregate.averageDischargePricePerKwh != null"> zu Ø {{ price(savings.aggregate.averageDischargePricePerKwh) }} €/kWh</template></small></dd>
            </div>
            <div>
              <dt>Kosten Netzladung</dt>
              <dd><span class="ls-num">−{{ money(savings.aggregate.gridChargeCost) }}</span>
                <small>{{ kwh(savings.aggregate.gridChargedEnergyKwh) }}<template v-if="savings.aggregate.averageGridChargePricePerKwh != null"> zu Ø {{ price(savings.aggregate.averageGridChargePricePerKwh) }} €/kWh</template></small></dd>
            </div>
            <div>
              <dt>Entgangene Einspeisevergütung</dt>
              <dd><span class="ls-num">−{{ money(savings.aggregate.pvOpportunityCost) }}</span>
                <small>{{ kwh(savings.aggregate.pvChargedEnergyKwh) }} PV statt Einspeisung</small></dd>
            </div>
          </dl>
          <p class="ls-muted ls-savings__note">Energie, die aus dem Akku ins Netz oder in das E3/DC geflossen ist, zählt nicht als Ersparnis.</p>
        </div>
      </div>
      <div class="ls-panel ls-savings">
        <div class="ls-zone__head">
          <h3>Strategievergleich ({{ reviewHours }} h)</h3>
        </div>
        <div v-if="review?.costs" class="ls-savings__body">
          <div class="ls-savings__total">
            <span class="ls-kpi__label">Vorteil gegenüber „Akku zuerst“</span>
            <span class="ls-savings__value" :class="{ 'ls-savings__value--negative': review.costs.advantageOverBaseline < 0 }">{{ money(review.costs.advantageOverBaseline) }}</span>
          </div>
          <dl class="ls-savings__rows">
            <div>
              <dt>Tatsächliche Stromkosten</dt>
              <dd><span class="ls-num">{{ money(review.costs.actualCost) }}</span>
                <small>Ø Preis {{ price(review.costs.averagePricePerKwh) }} €/kWh</small></dd>
            </div>
            <div>
              <dt>„Akku zuerst“ ohne Netzladung</dt>
              <dd><span class="ls-num">{{ money(review.costs.baselineCost) }}</span>
                <small>Akku deckt jeden Bezug, lädt nur aus Überschuss</small></dd>
            </div>
            <div>
              <dt>Ohne Victron-Akku</dt>
              <dd><span class="ls-num">{{ money(review.costs.withoutBatteryCost) }}</span>
                <small>Akku spart insgesamt {{ money(review.costs.advantageOverNoBattery) }}</small></dd>
            </div>
            <div>
              <dt>Mehr Energie im Akku am Ende</dt>
              <dd><span class="ls-num">{{ money(review.costs.storedEnergyDifferenceValue) }}</span>
                <small>{{ kwh(review.costs.storedEnergyDifferenceKwh) }} gegenüber „Akku zuerst“</small></dd>
            </div>
          </dl>
          <p class="ls-muted ls-savings__note">
            Nachgerechnet aus den Messwerten; die Reaktion des E3/DC wird als unverändert angenommen.
            <template v-if="review.costs.pricedCoveragePercent < 95"> Preise liegen nur für {{ numberFormat(0).format(review.costs.pricedCoveragePercent) }} % des Zeitraums vor.</template>
          </p>
        </div>
        <p v-else class="ls-muted">Für den Zeitraum liegen noch keine Preise oder Messwerte vor.</p>
      </div>
      <div class="ls-panel ls-chart-panel">
        <div class="ls-chart">
          <svg v-if="reviewChart.hasData" :viewBox="`0 0 ${reviewChart.width} 260`" role="img" aria-label="Rückblick je Stunde: Ladezustand, Netzbezug, Entladung, Einspeisung und Laden des E3/DC">
            <text class="ls-band" :x="reviewChart.left" :y="reviewChart.socTop - 8">Ladezustand Victron</text>
            <g v-for="value in [0, 50, 100]" :key="`r${value}`">
              <line class="ls-grid" :x1="reviewChart.left" :x2="reviewChart.width - reviewChart.right" :y1="reviewChart.socY(value)" :y2="reviewChart.socY(value)" />
              <text class="ls-axis" :x="reviewChart.left - 6" :y="reviewChart.socY(value) + 3" text-anchor="end">{{ value }}%</text>
            </g>
            <polyline class="ls-soc" :points="reviewChart.socPoints" />
            <text class="ls-band" :x="reviewChart.left" :y="reviewChart.zero - reviewChart.upHeight - 10">Energie je Stunde (kWh)</text>
            <line class="ls-grid" :x1="reviewChart.left" :x2="reviewChart.width - reviewChart.right" :y1="reviewChart.zero" :y2="reviewChart.zero" />
            <text class="ls-axis" :x="reviewChart.left - 6" :y="reviewChart.zero - reviewChart.upHeight + 3" text-anchor="end">+{{ numberFormat(1).format(reviewChart.maxUp) }}</text>
            <text class="ls-axis" :x="reviewChart.left - 6" :y="reviewChart.zero + 3" text-anchor="end">0</text>
            <text class="ls-axis" :x="reviewChart.left - 6" :y="reviewChart.zero + reviewChart.downHeight + 3" text-anchor="end">−{{ numberFormat(1).format(reviewChart.maxDown) }}</text>
            <rect v-for="bar in reviewChart.bars" :key="bar.key" :class="[`ls-bar--${bar.kind}`, { 'ls-chart-hit': next }]" :x="bar.x" :y="bar.y" :width="bar.width" :height="bar.height" rx="1" :tabindex="next ? 0 : undefined">
              <title v-if="next">{{ reviewHourTooltip(review?.hours[bar.index]) }}</title>
            </rect>
            <text v-for="label in reviewChart.labels" :key="label.x" class="ls-axis" :x="label.x" y="254" text-anchor="middle">{{ label.text }}</text>
          </svg>
          <p v-else class="ls-muted">Noch keine Messwerte im Zeitraum.</p>
        </div>
        <div class="ls-legend">
          <span><i class="ls-legend__line" />Ladezustand Victron</span>
          <span><i class="ls-bar--import" />Netzbezug</span>
          <span><i class="ls-bar--discharge" />Akku entlädt</span>
          <span><i class="ls-bar--feedin" />Einspeisung aus Akku</span>
          <span><i class="ls-bar--transfer" />E3/DC aus Victron geladen</span>
        </div>
      </div>
    </section>

    <!-- Details -->
    <section class="ls-zone" aria-labelledby="ls-details">
      <h2 id="ls-details">Details</h2>
      <details class="ls-panel" open>
        <summary>Entscheidungen der letzten Minuten</summary>
        <div class="ls-log-wrap">
          <table class="ls-log">
            <thead><tr><th>Zeit</th><th>Aktion</th><th>Leistung</th><th>Grund</th></tr></thead>
            <tbody>
              <tr v-for="entry in logEntries.slice(0, 8)" :key="entry.id">
                <td class="ls-num">{{ new Date(entry.decidedAtUtc).toLocaleTimeString('de-DE') }}</td>
                <td>{{ getDecisionLabel(entry.decisionState, entry.chargeSource) }}</td>
                <td class="ls-num">{{ kw(entry.targetPowerWatts) }}</td>
                <td>{{ entry.reasons[0]?.message ?? '–' }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </details>
      <details class="ls-panel">
        <summary>Systemstatus und manuelle Ladung</summary>
        <div class="ls-details-grid">
          <div class="ls-status">
            <div v-for="row in statusRows" :key="row.label" class="ls-status__row">
              <span><span class="ls-dot" :style="{ background: row.ok ? 'var(--ls-ok)' : 'var(--ls-warning)' }" />{{ row.label }}</span>
              <strong>{{ row.value }}</strong>
            </div>
          </div>
          <div class="ls-manual">
            <strong>Manuelle Ladung: {{ manualChargeLabel }}</strong>
            <div class="ls-manual__row">
              <label for="ls-manual-minutes">Minuten <input id="ls-manual-minutes" v-model.number="manualDurationMinutes" type="number" min="1" max="1440" /></label>
              <label for="ls-manual-kw">kW <input id="ls-manual-kw" v-model.number="manualPowerKw" type="number" min="0.1" max="50" step="0.1" /></label>
              <button type="button" class="ls-button" :disabled="isManualChargeBusy" @click="emit('start-manual-charge', manualDurationMinutes, manualPowerKw)">Starten</button>
              <button type="button" class="ls-button ls-button--secondary" :disabled="!manualCharge?.isActive || isManualChargeBusy" @click="emit('stop-manual-charge')">Stoppen</button>
            </div>
          </div>
        </div>
      </details>
    </section>
  </div>
</template>

<style scoped>
/* Layout: four zones from top to bottom - situation, live flow with both storages, 24 h plan, measured review. */
.leitstand {
  --ls-bg: #eef1f4;
  --ls-surface: #ffffff;
  --ls-surface-2: #f5f7f9;
  --ls-line: #d5dbe2;
  --ls-fg: #1b2530;
  --ls-muted: #5b6977;
  --ls-accent: #1f6fb2;
  --ls-discharge: #0f8a7e;
  --ls-pvCharge: #c98a00;
  --ls-gridCharge: #7b4fc9;
  --ls-hold: #7f8ea0;
  --ls-full: #b7c2cc;
  --ls-idle: #9aa6b2;
  --ls-ok: #2e8540;
  --ls-info: #1f6fb2;
  --ls-warning: #b26a00;
  --ls-critical: #c0392b;
  --ls-font-display: "Barlow Semi Condensed", "Arial Narrow", system-ui, sans-serif;
  --ls-font-data: "IBM Plex Mono", ui-monospace, "Cascadia Mono", Consolas, monospace;
  display: grid;
  gap: 22px;
  max-width: 1180px;
  margin: 0 auto;
  padding-block: 8px 40px;
  color: var(--ls-fg);
}
.leitstand--dark {
  --ls-bg: #11171d;
  --ls-surface: #18212a;
  --ls-surface-2: #1e2933;
  --ls-line: #2d3a46;
  --ls-fg: #e4eaf0;
  --ls-muted: #93a2b1;
  --ls-accent: #5aa7e6;
  --ls-discharge: #2fbfae;
  --ls-pvCharge: #f0b429;
  --ls-gridCharge: #a786ea;
  --ls-hold: #8d9bab;
  --ls-full: #3d4b58;
  --ls-idle: #56636f;
  --ls-ok: #5cc06f;
  --ls-info: #5aa7e6;
  --ls-warning: #f0a33a;
  --ls-critical: #ef6b5b;
}
h2, h3 { font-family: var(--ls-font-display); font-weight: 600; margin: 0; }
h2 { font-size: 1.35rem; }
h3 { font-size: 1.05rem; margin-bottom: 10px; }
.ls-eyebrow { font-family: var(--ls-font-data); font-size: 0.72rem; letter-spacing: 0.12em; text-transform: uppercase; color: var(--ls-muted); font-weight: 500; }
.ls-num { font-family: var(--ls-font-data); font-variant-numeric: tabular-nums; }
.ls-muted { color: var(--ls-muted); margin: 0; }
.ls-error { color: var(--ls-critical); margin: 0; font-size: 0.9rem; }
.ls-zone { display: grid; gap: 12px; }
.ls-zone__head { display: flex; flex-wrap: wrap; align-items: baseline; justify-content: space-between; gap: 8px; }
.ls-panel { background: var(--ls-surface); border: 1px solid var(--ls-line); border-radius: 10px; padding: 16px; min-width: 0; }
.ls-split { display: grid; grid-template-columns: minmax(0, 2fr) minmax(0, 1fr); gap: 16px; align-items: start; }
@media (max-width: 900px) { .ls-split { grid-template-columns: minmax(0, 1fr); } }

.ls-situation { display: grid; grid-template-columns: auto minmax(0, 1fr) auto; gap: 16px; align-items: center; }
@media (max-width: 700px) { .ls-situation { grid-template-columns: minmax(0, 1fr); } }
.ls-pill { display: inline-flex; align-items: center; gap: 8px; padding: 6px 12px; border-radius: 999px; font-family: var(--ls-font-display); font-weight: 600; font-size: 1.05rem; color: #fff; white-space: nowrap; }
.ls-situation__text { margin: 0; font-size: 1.05rem; }
.ls-situation__meta { display: flex; flex-wrap: wrap; gap: 14px; font-size: 0.85rem; color: var(--ls-muted); }
.ls-dot { display: inline-block; width: 8px; height: 8px; border-radius: 50%; margin-right: 6px; vertical-align: middle; }

.ls-flow svg { width: 100%; height: auto; display: block; }
.ls-flow text { fill: var(--ls-fg); }
.ls-lbl { font-family: var(--ls-font-data); font-size: 11px; letter-spacing: 0.1em; fill: var(--ls-muted) !important; }
.ls-val { font-family: var(--ls-font-display); font-size: 22px; font-weight: 600; }
.ls-sub { font-size: 12px; fill: var(--ls-muted) !important; }
.ls-node { fill: var(--ls-surface-2); stroke: var(--ls-line); }
.ls-boundary { fill: none; stroke: var(--ls-line); stroke-dasharray: 5 5; }
.ls-wire { fill: none; stroke: var(--ls-line); stroke-width: 3; }
.ls-wire--live { stroke: var(--ls-muted); stroke-dasharray: 8 8; animation: ls-flow 1.2s linear infinite; }
.ls-wire--pv { stroke: var(--ls-pvCharge); }
.ls-wire--discharge { stroke: var(--ls-discharge); }
.ls-wire--crit { stroke: var(--ls-critical); }
@keyframes ls-flow { to { stroke-dashoffset: -16; } }
@media (prefers-reduced-motion: reduce) { .ls-wire--live { animation: none; } }

.ls-checks { list-style: none; margin: 0; padding: 0; display: grid; gap: 8px; }
.ls-check { display: grid; grid-template-columns: 18px minmax(0, 1fr); gap: 8px; align-items: start; font-size: 0.92rem; }
.ls-check__mark { width: 18px; height: 18px; border-radius: 50%; display: grid; place-items: center; color: #fff; font-size: 11px; font-weight: 700; margin-top: 2px; }
.ls-check--ok .ls-check__mark { background: var(--ls-ok); }
.ls-check--info .ls-check__mark { background: var(--ls-info); }
.ls-check--warning .ls-check__mark { background: var(--ls-warning); }
.ls-check--critical .ls-check__mark { background: var(--ls-critical); }

.ls-chart-panel { display: grid; gap: 10px; }
.ls-chart { width: 100%; overflow-x: auto; }
.ls-chart svg { display: block; width: 100%; min-width: 640px; height: auto; }
.ls-axis { fill: var(--ls-muted); font-family: var(--ls-font-data); font-size: 10px; }
.ls-band { fill: var(--ls-fg); font-family: var(--ls-font-display); font-size: 12px; font-weight: 600; }
.ls-grid { stroke: var(--ls-line); stroke-width: 1; }
.ls-now { stroke: var(--ls-fg); stroke-width: 1.5; stroke-dasharray: 3 3; }
.ls-now-label { fill: var(--ls-fg); font-family: var(--ls-font-data); font-size: 10px; }
.ls-soc { fill: none; stroke: var(--ls-accent); stroke-width: 2.5; }
.ls-soc-dot { fill: var(--ls-accent); }
.ls-pv-area { fill: var(--ls-pvCharge); opacity: 0.22; }
.ls-pv-line { fill: none; stroke: var(--ls-pvCharge); stroke-width: 1.5; }
.ls-bar--import { fill: var(--ls-hold); background: var(--ls-hold); }
.ls-bar--discharge { fill: var(--ls-discharge); background: var(--ls-discharge); }
.ls-bar--feedin { fill: var(--ls-critical); background: var(--ls-critical); }
.ls-bar--transfer { fill: var(--ls-gridCharge); background: var(--ls-gridCharge); }
.ls-legend { display: flex; flex-wrap: wrap; gap: 6px 16px; font-size: 0.82rem; color: var(--ls-muted); }
.ls-legend span { display: inline-flex; align-items: center; gap: 6px; }
.ls-legend i { width: 12px; height: 12px; border-radius: 3px; display: inline-block; }
.ls-legend .ls-legend__line { height: 3px; background: var(--ls-accent); }

.ls-steps { list-style: none; margin: 0; padding: 0; }
.ls-step { display: grid; grid-template-columns: 112px 6px minmax(0, 1fr); gap: 12px; padding-block: 10px; border-bottom: 1px solid var(--ls-line); }
.ls-step:last-child { border-bottom: 0; }
.ls-step__time { font-family: var(--ls-font-data); font-size: 0.82rem; color: var(--ls-muted); padding-top: 2px; }
.ls-step__bar { border-radius: 3px; }
.ls-step__title { font-weight: 600; }
.ls-step__why { color: var(--ls-muted); font-size: 0.88rem; margin: 2px 0 0; }

.ls-tabs { display: inline-flex; gap: 4px; }
.ls-tab { border: 1px solid var(--ls-line); background: var(--ls-surface); color: var(--ls-fg); border-radius: 999px; padding: 3px 12px; font-size: 0.85rem; cursor: pointer; }
.ls-tab--active { background: var(--ls-accent); border-color: var(--ls-accent); color: #fff; }
.ls-tab:focus-visible, .ls-button:focus-visible, details summary:focus-visible { outline: 2px solid var(--ls-accent); outline-offset: 2px; }
.ls-kpis { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: 12px; }
.ls-kpi { background: var(--ls-surface); border: 1px solid var(--ls-line); border-top: 4px solid var(--ls-ok); border-radius: 10px; padding: 12px 14px; display: grid; gap: 2px; }
.ls-kpi__label { font-size: 0.85rem; color: var(--ls-muted); }
.ls-kpi__value { font-family: var(--ls-font-display); font-size: 1.6rem; font-weight: 600; font-variant-numeric: tabular-nums; }
.ls-kpi__hint { font-size: 0.8rem; color: var(--ls-muted); }

.ls-savings { display: grid; gap: 12px; }
.ls-savings__body { display: grid; grid-template-columns: minmax(160px, auto) minmax(0, 1fr); gap: 12px 24px; align-items: start; }
@media (max-width: 700px) { .ls-savings__body { grid-template-columns: minmax(0, 1fr); } }
.ls-savings__total { display: grid; gap: 2px; }
.ls-savings__value { font-family: var(--ls-font-display); font-size: 2.2rem; font-weight: 600; color: var(--ls-ok); font-variant-numeric: tabular-nums; }
.ls-savings__value--negative { color: var(--ls-critical); }
.ls-savings__rows { display: grid; gap: 8px; margin: 0; }
.ls-savings__rows div { display: flex; justify-content: space-between; gap: 12px; border-bottom: 1px solid var(--ls-line); padding-bottom: 6px; }
.ls-savings__rows dt { color: var(--ls-muted); font-size: 0.9rem; }
.ls-savings__rows dd { margin: 0; display: grid; justify-items: end; gap: 1px; }
.ls-savings__rows small { color: var(--ls-muted); font-size: 0.78rem; }
.ls-savings__note { grid-column: 1 / -1; font-size: 0.82rem; }

details.ls-panel summary { cursor: pointer; font-family: var(--ls-font-display); font-weight: 600; font-size: 1.05rem; }
.ls-log-wrap { overflow-x: auto; }
.ls-log { width: 100%; border-collapse: collapse; font-size: 0.88rem; margin-top: 12px; }
.ls-log th, .ls-log td { text-align: left; padding: 8px 10px; border-bottom: 1px solid var(--ls-line); vertical-align: top; }
.ls-log th { font-family: var(--ls-font-data); font-size: 0.72rem; letter-spacing: 0.08em; text-transform: uppercase; color: var(--ls-muted); font-weight: 500; }
.ls-details-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 16px; margin-top: 12px; }
.ls-status { display: grid; gap: 8px; }
.ls-status__row { display: flex; justify-content: space-between; gap: 12px; font-size: 0.92rem; }
.ls-manual { display: grid; gap: 10px; }
.ls-manual__row { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }
.ls-manual__row label { display: inline-flex; gap: 6px; align-items: center; font-size: 0.88rem; color: var(--ls-muted); }
.ls-manual__row input { width: 72px; padding: 4px 6px; border: 1px solid var(--ls-line); border-radius: 6px; background: var(--ls-surface-2); color: var(--ls-fg); }
.ls-button { border: 0; border-radius: 6px; padding: 6px 14px; background: var(--ls-accent); color: #fff; font-weight: 600; cursor: pointer; }
.ls-button--secondary { background: transparent; color: var(--ls-fg); border: 1px solid var(--ls-line); }
.ls-button:disabled { opacity: 0.5; cursor: not-allowed; }

/* Leitstand Next: stronger at-a-glance hierarchy while the original Leitstand remains unchanged. */
.leitstand--next {
  --ls-bg: #f2f6f8;
  --ls-surface: #ffffff;
  --ls-surface-2: #f6f9fb;
  --ls-line: #dbe4ea;
  --ls-fg: #152434;
  --ls-muted: #607383;
  --ls-accent: #1466a8;
  --ls-discharge: #14806b;
  --ls-pvCharge: #d39116;
  --ls-gridCharge: #7958c7;
  --ls-hold: #91a1b0;
  --ls-full: #c5d0d9;
  --ls-idle: #aab7c2;
  max-width: 1440px;
  gap: 26px;
  padding-top: 14px;
}

.leitstand--next.leitstand--dark {
  --ls-bg: #0e1720;
  --ls-surface: #16232e;
  --ls-surface-2: #1c2c38;
  --ls-line: #2b3b49;
  --ls-fg: #e9f0f5;
  --ls-muted: #9aabb9;
  --ls-accent: #69b7f3;
  --ls-discharge: #42c6aa;
  --ls-pvCharge: #f0b640;
  --ls-gridCharge: #b194f5;
  --ls-hold: #8293a2;
  --ls-full: #3c4c59;
  --ls-idle: #5e7080;
}

.leitstand--next .ls-zone { gap: 14px; }
.leitstand--next .ls-panel { border-radius: 16px; border-color: color-mix(in srgb, var(--ls-line) 88%, transparent); box-shadow: 0 12px 30px color-mix(in srgb, var(--ls-fg) 7%, transparent); }
.leitstand--next .ls-situation { grid-template-columns: minmax(230px, auto) minmax(0, 1fr) auto; min-height: 104px; padding: 18px 20px; background: linear-gradient(110deg, color-mix(in srgb, var(--ls-discharge) 12%, var(--ls-surface)), var(--ls-surface) 52%); }
.leitstand--next .ls-pill { min-height: 48px; padding: 10px 16px; font-size: 1.18rem; box-shadow: 0 8px 18px color-mix(in srgb, var(--ls-discharge) 22%, transparent); }
.leitstand--next .ls-situation__text { max-width: 720px; font-size: 1.14rem; font-weight: 600; line-height: 1.45; }
.leitstand--next .ls-situation__meta { justify-content: flex-end; font-family: var(--ls-font-data); font-size: 0.86rem; }
.leitstand--next .ls-flow { padding: 20px; min-height: 330px; }
.leitstand--next .ls-decision-panel { display: grid; align-content: start; gap: 12px; background: linear-gradient(180deg, color-mix(in srgb, var(--ls-accent) 7%, var(--ls-surface)), var(--ls-surface)); }
.ls-decision-reason { margin: -2px 0 2px; color: var(--ls-fg); font-size: 0.94rem; line-height: 1.45; font-weight: 600; }
.leitstand--next .ls-checks { gap: 11px; }
.leitstand--next .ls-check { padding-bottom: 10px; border-bottom: 1px solid color-mix(in srgb, var(--ls-line) 80%, transparent); }
.leitstand--next .ls-check:last-child { border-bottom: 0; padding-bottom: 0; }
.leitstand--next .ls-chart-panel { overflow: hidden; }
.leitstand--next .ls-chart { padding: 4px 2px; }
.leitstand--next .ls-chart-hit { cursor: help; outline: none; }
.leitstand--next .ls-chart-hit:hover, .leitstand--next .ls-chart-hit:focus { stroke-width: 2px; filter: brightness(1.08) saturate(1.25); }
.leitstand--next .ls-steps { counter-reset: steps; }
.leitstand--next .ls-step { grid-template-columns: 112px 8px minmax(0, 1fr); gap: 14px; padding: 13px 0; }
.leitstand--next .ls-step__bar { border-radius: 999px; }
.leitstand--next .ls-kpi { border-radius: 14px; padding: 16px; box-shadow: 0 8px 22px color-mix(in srgb, var(--ls-fg) 5%, transparent); }
.leitstand--next .ls-kpi__value { font-size: 1.8rem; }
.leitstand--next .ls-savings { padding: 20px; }

@media (max-width: 760px) {
  .leitstand--next { gap: 18px; padding-top: 4px; }
  .leitstand--next .ls-situation { grid-template-columns: 1fr; gap: 12px; min-height: 0; }
  .leitstand--next .ls-situation__meta { justify-content: flex-start; gap: 8px 14px; }
  .leitstand--next .ls-panel { border-radius: 13px; padding: 14px; }
  .leitstand--next .ls-flow { min-height: 0; overflow-x: auto; }
  .leitstand--next .ls-flow svg { min-width: 620px; }
  .leitstand--next .ls-chart { margin-inline: -4px; }
  .leitstand--next .ls-step { grid-template-columns: 92px 6px minmax(0, 1fr); gap: 10px; }
  .leitstand--next .ls-kpis { grid-template-columns: 1fr 1fr; gap: 9px; }
  .leitstand--next .ls-kpi { padding: 12px; }
  .leitstand--next .ls-kpi__value { font-size: 1.45rem; }
}
</style>
