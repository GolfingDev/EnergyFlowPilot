<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import type { ControllerSettingsResponseDto } from '../dashboard/dashboardTypes';
import type { HotWaterReportDto } from '../heating/hotWaterTypes';

const report = ref<HotWaterReportDto | null>(null);
const settings = ref<ControllerSettingsResponseDto | null>(null);
const reportError = ref<string | null>(null);
const settingsError = ref<string | null>(null);
const now = ref(Date.now());
let timer: ReturnType<typeof window.setInterval> | undefined;
let request: AbortController | undefined;
let loading = false;

const decimal = new Intl.NumberFormat('de-DE', { maximumFractionDigits: 1, minimumFractionDigits: 1 });
const priceFormat = new Intl.NumberFormat('de-DE', { maximumFractionDigits: 2 });
const dateFormat = new Intl.DateTimeFormat('de-DE', { timeZone: 'Europe/Berlin', day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });
const timeFormat = new Intl.DateTimeFormat('de-DE', { timeZone: 'Europe/Berlin', hour: '2-digit', minute: '2-digit' });
const date = (value: string | null | undefined) => value ? dateFormat.format(new Date(value)) : '–';
const temperature = (value: number | null | undefined) => value == null ? '–' : `${decimal.format(value)} °C`;
const price = (value: number | null | undefined) => value == null ? '–' : `${priceFormat.format(value * 100)} ct/kWh`;
const latest = computed(() => report.value?.samples.at(-1) ?? null);
const latestEpisode = computed(() => report.value?.episodes[0] ?? null);
const plan = computed(() => report.value?.plan ?? null);
const stale = computed(() => {
  if (!latest.value) return true;
  const age = now.value - Date.parse(latest.value.measuredAtUtc);
  return age < 0 || age > 600_000;
});

function enabled(key: string): boolean | null {
  const value = settings.value?.settings.find(s => s.key === key)?.value;
  return value === 'true' ? true : value === 'false' ? false : null;
}
const automationStatus = computed(() => {
  if (report.value?.isSuspended) return 'Automatik pausiert';
  if (report.value?.isRaised) return 'Anhebung aktiv';
  if (enabled('weishauptBiblockModbus.enabled') === false) return 'Erfassung ausgeschaltet';
  const active = enabled('weishauptHotWater.enabled');
  return active === true ? 'Aktiv · wartet' : active === false ? 'Automatik ausgeschaltet' : 'Aktivierungsstatus unbekannt';
});
const warning = computed(() => stale.value || report.value?.isSuspended || !!reportError.value || !!settingsError.value);
const operatingStatus = computed(() => {
  const code = latest.value?.operatingStatusCode;
  switch (code) {
    case 19: return 'Heizbetrieb';
    case 20: return 'Warmwasser wird geladen';
    case 21: return 'Legionellenschutz';
    case 8: return 'Abtauen';
    case undefined: return 'Noch kein Betriebsstatus';
    default: return `Reglerstatus ${code}`;
  }
});
const ownSetPoint = computed(() => {
  const episode = report.value?.episodes.find(e => e.endedAtUtc === null && e.writeConfirmed);
  if (!report.value?.isRaised) return 'Keine aktive Sollwertanhebung';
  return episode ? `${temperature(episode.originalCelsius)} → ${temperature(episode.raisedCelsius)}`
    : 'Sollwertanhebung wird geprüft';
});

// The timeline shows one temperature sensor, never a calculated state of charge of all 903 litres.
const chart = computed(() => {
  const start = now.value - 86_400_000;
  const samples = (report.value?.samples ?? []).filter(s => {
    const at = Date.parse(s.measuredAtUtc);
    return at >= start && at <= now.value && Number.isFinite(s.temperatureCelsius) && s.temperatureCelsius >= 0 && s.temperatureCelsius <= 95;
  });
  const low = samples.length ? Math.floor(Math.min(...samples.map(s => s.temperatureCelsius))) - 1 : 40;
  const high = samples.length ? Math.ceil(Math.max(...samples.map(s => s.temperatureCelsius))) + 1 : 55;
  const x = (at: number) => 52 + (at - start) / 86_400_000 * 684;
  const y = (value: number) => 20 + (high - value) / (high - low) * 112;
  const paths: string[] = [];
  let previous: number | null = null;
  for (const sample of samples) {
    const at = Date.parse(sample.measuredAtUtc);
    const point = `${x(at)},${y(sample.temperatureCelsius)}`;
    if (previous === null || at - previous > 900_000) paths.push(`M${point}`);
    else paths[paths.length - 1] += ` L${point}`;
    previous = at;
  }
  const last = samples.at(-1);
  return {
    paths,
    ticks: [low, (low + high) / 2, high].map(value => ({ value, y: y(value) })),
    times: [0, 6, 12, 18, 24].map(hour => {
      const at = start + hour * 3_600_000;
      return { x: x(at), label: timeFormat.format(new Date(at)) };
    }),
    last: last ? { x: x(Date.parse(last.measuredAtUtc)), y: y(last.temperatureCelsius) } : null
  };
});

async function get<T>(url: string, signal: AbortSignal): Promise<T> {
  const response = await fetch(url, { signal, cache: 'no-store' });
  if (!response.ok) throw new Error(`Abruf fehlgeschlagen (HTTP ${response.status}).`);
  return await response.json() as T;
}

async function load(): Promise<void> {
  if (loading) return;
  loading = true;
  now.value = Date.now();
  const controller = new AbortController();
  request = controller;
  const timeout = window.setTimeout(() => controller.abort(), 20_000);
  try {
    // Settings failure must not hide a successfully fetched temperature, or the other way round.
    const [water, configuration] = await Promise.allSettled([
      get<HotWaterReportDto>('/api/weishaupt/hot-water', controller.signal),
      get<ControllerSettingsResponseDto>('/api/settings', controller.signal)
    ]);
    if (water.status === 'fulfilled') { report.value = water.value; reportError.value = null; }
    else reportError.value = 'Warmwasserdaten nicht erreichbar. Angezeigte Werte können veraltet sein.';
    if (configuration.status === 'fulfilled') { settings.value = configuration.value; settingsError.value = null; }
    else settingsError.value = 'Aktivierungsstatus konnte nicht aktualisiert werden.';
    now.value = Date.now();
  } finally {
    window.clearTimeout(timeout);
    loading = false;
  }
}

onMounted(() => { void load(); timer = window.setInterval(() => void load(), 60_000); });
onBeforeUnmount(() => { window.clearInterval(timer); request?.abort(); });
</script>

<template>
  <section class="lhw" aria-labelledby="ls-hot-water">
    <header class="lhw-head">
      <h2 id="ls-hot-water">Warmwasser</h2>
      <span class="lhw-status" :class="{ 'lhw-status--warning': warning }">
        <i aria-hidden="true" />{{ automationStatus }}
      </span>
    </header>
    <p v-if="reportError || settingsError" class="lhw-warning" role="status">{{ reportError ?? settingsError }}</p>
    <div class="lhw-grid">
      <article class="lhw-panel">
        <span class="lhw-label">WES 910 · Isttemperatur</span>
        <strong class="lhw-temperature" :class="{ 'lhw-old': stale }">{{ temperature(latest?.temperatureCelsius) }}</strong>
        <p v-if="latest" :class="stale ? 'lhw-warning' : 'lhw-muted'">
          {{ stale ? 'Messwert veraltet' : 'Gemessen' }} · {{ date(latest.measuredAtUtc) }} Uhr
        </p>
        <p v-else class="lhw-muted">{{ report ? 'Noch keine Messwerte vorhanden.' : 'Warmwasserdaten werden geladen …' }}</p>
        <p class="lhw-operation">{{ stale && latest ? 'Zuletzt: ' : '' }}{{ operatingStatus }}</p>
        <p class="lhw-muted">{{ ownSetPoint }}</p>
      </article>
      <article class="lhw-panel">
        <span class="lhw-label">Ladeplanung · Preis oder PV-Überschuss</span>
        <template v-if="plan">
          <dl class="lhw-facts">
            <div><dt>Preisfenster</dt><dd>{{ date(plan.startsAtUtc) }} – {{ date(plan.endsAtUtc) }}</dd></div>
            <div><dt>Warmwasserbedarf</dt><dd>{{ date(plan.needByUtc) }} Uhr</dd></div>
            <div><dt>Fenster / später</dt><dd>{{ price(plan.windowPricePerKwh) }} / {{ price(plan.referencePricePerKwh) }}</dd></div>
            <div><dt>Modellierter Vorteil</dt><dd>{{ price(plan.estimatedAdvantagePerKwh) }}</dd></div>
          </dl>
          <p class="lhw-muted lhw-note">{{ plan.reason }}</p>
        </template>
        <p v-else class="lhw-muted lhw-note">Noch keine Planung vorhanden.</p>
        <p class="lhw-muted lhw-note">Ohne separaten Stromzähler: keine gemessene Euro-Ersparnis.</p>
      </article>
    </div>
    <details class="lhw-panel lhw-details">
      <summary>Temperaturverlauf und letzte Vorladung</summary>
      <div v-if="chart.last" class="lhw-chart">
        <svg viewBox="0 0 760 166" role="img" aria-label="Warmwasser-Isttemperatur der letzten 24 Stunden; Unterbrechungen zeigen Messlücken">
          <g v-for="tick in chart.ticks" :key="tick.value">
            <line x1="52" x2="736" :y1="tick.y" :y2="tick.y" />
            <text x="44" :y="tick.y + 4" text-anchor="end">{{ priceFormat.format(tick.value) }} °C</text>
          </g>
          <path v-for="(path, index) in chart.paths" :key="index" :d="path" class="lhw-curve" />
          <circle :cx="chart.last.x" :cy="chart.last.y" r="3" class="lhw-point" />
          <text v-for="tick in chart.times" :key="tick.x" :x="tick.x" y="157" text-anchor="middle">{{ tick.label }}</text>
        </svg>
      </div>
      <p v-else class="lhw-muted lhw-note">Noch kein Temperaturverlauf vorhanden.</p>
      <template v-if="latestEpisode">
        <p class="lhw-note"><strong>Letzte Vorladung:</strong> {{ date(latestEpisode.startedAtUtc) }} · {{ latestEpisode.trigger }} · {{ temperature(latestEpisode.originalCelsius) }} → {{ temperature(latestEpisode.raisedCelsius) }}</p>
        <dl class="lhw-facts">
          <div><dt>Ziel erreicht</dt><dd>{{ date(latestEpisode.targetReachedAtUtc) }}</dd></div>
          <div><dt>Erneute Ladung erkannt</dt><dd>{{ date(latestEpisode.nextHeatingAtUtc) }}</dd></div>
          <div><dt>Temperatur zum Bedarf</dt><dd>{{ temperature(latestEpisode.temperatureAtNeedCelsius) }}</dd></div>
        </dl>
        <p class="lhw-note">{{ latestEpisode.assessment }}</p>
      </template>
      <p v-else class="lhw-muted lhw-note">Noch keine Vorladung aufgezeichnet. Ob die Wärme bis zum Abend reicht, wird anhand künftiger Verläufe ausgewertet.</p>
    </details>
  </section>
</template>

<style scoped>
.lhw { display: grid; gap: 12px; min-width: 0; color: var(--ls-fg); }
.lhw h2 { font-family: var(--ls-font-display); font-size: 1.35rem; font-weight: 600; margin: 0; }
.lhw p { margin: 0; line-height: 1.45; }
.lhw-head { display: flex; flex-wrap: wrap; justify-content: space-between; align-items: center; gap: 8px; }
.lhw-status { display: inline-flex; align-items: center; gap: 7px; font-size: 0.85rem; color: var(--ls-muted); }
.lhw-status i { width: 8px; height: 8px; border-radius: 50%; background: var(--ls-info); }
.lhw-status--warning i { background: var(--ls-warning); }
.lhw-grid { display: grid; grid-template-columns: minmax(220px, 0.7fr) minmax(0, 1.3fr); gap: 12px; }
.lhw-panel { padding: 16px; border: 1px solid var(--ls-line); border-radius: 10px; background: var(--ls-surface); min-width: 0; }
.lhw-label { color: var(--ls-muted); font-size: 0.85rem; }
.lhw-temperature { display: block; font-family: var(--ls-font-display); font-size: 2.4rem; font-weight: 600; line-height: 1.25; margin-block: 6px; font-variant-numeric: tabular-nums; }
.lhw-old, .lhw-warning { color: var(--ls-warning); }
.lhw-operation { margin-top: 14px !important; font-weight: 600; }
.lhw-muted { color: var(--ls-muted); font-size: 0.86rem; }
.lhw-note { margin-top: 12px !important; font-size: 0.86rem; }
.lhw-facts { margin: 12px 0 0; display: grid; gap: 8px; }
.lhw-facts div { display: flex; flex-wrap: wrap; justify-content: space-between; gap: 4px 16px; border-bottom: 1px solid var(--ls-line); padding-bottom: 6px; }
.lhw-facts dt { color: var(--ls-muted); font-size: 0.86rem; }
.lhw-facts dd { margin: 0; font-size: 0.86rem; font-family: var(--ls-font-data); overflow-wrap: anywhere; }
.lhw-details summary { cursor: pointer; font-family: var(--ls-font-display); font-weight: 600; }
.lhw-details summary:focus-visible { outline: 2px solid var(--ls-accent); outline-offset: 4px; }
.lhw-chart { margin-top: 16px; overflow-x: auto; }
.lhw-chart svg { display: block; width: 100%; min-width: 430px; }
.lhw-chart text { fill: var(--ls-muted); font-family: var(--ls-font-data); font-size: 11px; }
.lhw-chart line { stroke: var(--ls-line); }
.lhw-chart .lhw-curve { fill: none; stroke: var(--ls-accent); stroke-width: 2.5; }
.lhw-chart .lhw-point { fill: var(--ls-accent); }
@media (max-width: 700px) { .lhw-grid { grid-template-columns: minmax(0, 1fr); } }
</style>
