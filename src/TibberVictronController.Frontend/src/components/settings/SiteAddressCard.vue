<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import PlaceAutocomplete, { type PlaceSuggestion } from '../PlaceAutocomplete.vue';

interface SiteDto {
  address: string | null;
  latitude: number | null;
  longitude: number | null;
}

const site = ref<SiteDto | null>(null);
const address = ref('');
const place = ref<PlaceSuggestion | null>(null);
const saving = ref(false);
const message = ref<{ type: 'success' | 'warning'; text: string } | null>(null);
const canSave = computed(() => !!place.value && !saving.value);
const coordinates = (latitude: number | null, longitude: number | null) =>
  latitude === null || longitude === null ? '–' : `${latitude.toFixed(5)} N, ${longitude.toFixed(5)} O`;

async function load(): Promise<void> {
  const response = await fetch('/api/site');
  if (response.ok) site.value = await response.json() as SiteDto;
}

async function save(): Promise<void> {
  if (!place.value) return;
  saving.value = true;
  try {
    const response = await fetch('/api/site', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ address: place.value.label, latitude: place.value.latitude, longitude: place.value.longitude })
    });
    if (!response.ok) {
      const body = await response.json().catch(() => null) as { message?: string } | null;
      throw new Error(body?.message ?? `Speichern fehlgeschlagen (HTTP ${response.status}).`);
    }
    message.value = { type: 'success', text: 'Gespeichert. PV-Standort übernommen; die geplanten Fahrten werden im Hintergrund neu berechnet.' };
    address.value = '';
    place.value = null;
    await load();
  } catch (reason) {
    message.value = { type: 'warning', text: reason instanceof Error ? reason.message : 'Speichern fehlgeschlagen.' };
  } finally {
    saving.value = false;
  }
}

onMounted(() => void load());
</script>

<template>
  <v-card variant="outlined" class="pa-4 mb-4" aria-label="Standort der Anlage">
    <h3 class="text-subtitle-1 mb-1">Standort der Anlage</h3>
    <p class="site__meta mb-3">
      Eine feste Adresse für Akkus, PV und Wallbox: Daraus kommen die Koordinaten der PV-Prognose und der Startpunkt aller Fahrten.
    </p>
    <p v-if="site" class="mb-3">
      Aktuell: <strong>{{ site.address ?? 'keine Adresse hinterlegt' }}</strong>
      <span class="site__meta"> · {{ coordinates(site.latitude, site.longitude) }}</span>
    </p>
    <div class="site__form">
      <PlaceAutocomplete v-model="address" label="Adresse suchen (Straße Hausnummer, Ort)" @select="place = $event" />
      <v-btn color="primary" :disabled="!canSave" :loading="saving" @click="save">Übernehmen</v-btn>
    </div>
    <p class="site__meta mt-1">Bitte einen Vorschlag aus der Liste wählen, damit die Koordinaten stimmen.</p>
    <v-alert v-if="message" class="mt-3" :type="message.type" variant="tonal" density="compact">{{ message.text }}</v-alert>
  </v-card>
</template>

<style scoped>
.site__form {
  display: grid;
  grid-template-columns: 1fr auto;
  gap: 12px;
  align-items: center;
}

.site__meta {
  font-size: 0.85rem;
  opacity: 0.75;
}
</style>
