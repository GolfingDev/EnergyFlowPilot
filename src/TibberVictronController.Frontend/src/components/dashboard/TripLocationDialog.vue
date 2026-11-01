<script setup lang="ts">
import { ref, watch } from 'vue';
import PlaceAutocomplete, { type PlaceSuggestion } from '../PlaceAutocomplete.vue';

const props = defineProps<{ open: boolean; title: string; hint?: string; initial?: string; busy?: boolean }>();
const emit = defineEmits<{ close: []; save: [place: { label: string; latitude: number | null; longitude: number | null }] }>();

const label = ref(props.initial ?? '');
const place = ref<PlaceSuggestion | null>(null);

watch(() => props.open, open => {
  if (open) {
    label.value = props.initial ?? '';
    place.value = null;
  }
});

function save(): void {
  // Coordinates only for a chosen suggestion that was not edited afterwards.
  const chosen = place.value && place.value.label === label.value ? place.value : null;
  emit('save', { label: label.value.trim(), latitude: chosen?.latitude ?? null, longitude: chosen?.longitude ?? null });
}
</script>

<template>
  <v-dialog :model-value="open" max-width="560" @update:model-value="value => { if (!value) emit('close'); }">
    <v-card>
      <v-card-title class="text-h6">{{ title }}</v-card-title>
      <v-card-text>
        <p v-if="hint" class="trip-location__hint mb-3">{{ hint }}</p>
        <PlaceAutocomplete v-model="label" label="Ort oder Adresse" @select="place = $event" />
      </v-card-text>
      <v-card-actions>
        <v-spacer />
        <v-btn variant="text" @click="emit('close')">Abbrechen</v-btn>
        <v-btn color="primary" :disabled="label.trim().length < 2" :loading="busy" @click="save">Übernehmen</v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>

<style scoped>
.trip-location__hint {
  font-size: 0.85rem;
  opacity: 0.75;
}
</style>
