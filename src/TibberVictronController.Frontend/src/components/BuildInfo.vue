<script setup lang="ts">
declare const __APP_BUILD__: {
  revision: string | null;
  committedAtUtc: string | null;
  builtAtUtc: string | null;
  dirty: boolean;
};

const build = __APP_BUILD__;
const date = (value: string) => new Date(value).toLocaleString('de-DE', {
  timeZone: 'Europe/Berlin', day: '2-digit', month: '2-digit', year: '2-digit',
  hour: '2-digit', minute: '2-digit'
});
const revision = `${build.revision?.slice(0, 7) ?? 'unbekannt'}${build.dirty ? '*' : ''}`;
const title = [
  `Frontend-Build: ${build.revision ?? 'Git-Kennung nicht verfügbar'}`,
  build.builtAtUtc ? `Erstellt: ${date(build.builtAtUtc)} (Europe/Berlin)` : 'Entwicklungsmodus',
  build.committedAtUtc ? `Code-Stand: ${date(build.committedAtUtc)} (Europe/Berlin)` : null,
  build.dirty ? '* Mit lokalen Änderungen erstellt' : null
].filter(Boolean).join('\n');
</script>

<template>
  <span class="build-info" :title="title" :aria-label="title">
    <span>Build {{ revision }}</span>
    <span>{{ build.builtAtUtc ? `· ${date(build.builtAtUtc)}` : '· Entwicklung' }}</span>
  </span>
</template>

<style scoped>
.build-info { display: flex; flex-wrap: wrap; gap: 0 4px; color: var(--efp-muted); font-size: 10px; font-weight: 400; line-height: 1.4; letter-spacing: 0; margin-top: 3px; }
.build-info > span { white-space: nowrap; }
</style>
