<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import { useRouter } from 'vue-router';
import { isAdmin } from '../../auth';
import { errorText, getJson, sendJson } from '../zentrale/zentraleApi';

/**
 * Enter missing settings right where the system reports them (operator 2026-11-08). Only users allowed to change
 * settings (admins; the server enforces the same) get the fields; everyone else sees what is missing and who can
 * enter it. Sensitive values are never shown, only overwritten.
 */
const props = defineProps<{ keys: string[] }>();
const emit = defineEmits<{ saved: [] }>();
const router = useRouter();

interface Metadata { key: string; displayName: string; description: string; inputKind: string; unit: string | null; isSensitive: boolean }
interface Setting { key: string; value: string | null; isSensitive: boolean; isConfigured: boolean }

const metadata = ref<Metadata[]>([]);
const settings = ref<Setting[]>([]);
const drafts = ref<Record<string, string>>({});
const saving = ref(false);
const message = ref<{ type: 'error' | 'ok'; text: string } | null>(null);

const uniqueKeys = computed(() => [...new Set(props.keys)]);
const fields = computed(() => uniqueKeys.value.map((key) => {
  const meta = metadata.value.find((entry) => entry.key === key);
  const setting = settings.value.find((entry) => entry.key === key);
  return { key, label: meta?.displayName ?? key, description: meta?.description ?? '', kind: meta?.inputKind ?? 'text', unit: meta?.unit ?? null,
    sensitive: meta?.isSensitive ?? setting?.isSensitive ?? false, configured: setting?.isConfigured ?? false };
}));

async function load(): Promise<void> {
  if (uniqueKeys.value.length === 0) return;
  try {
    const [meta, current] = await Promise.all([
      getJson<{ settings: Metadata[] }>('/api/gui/metadata', 'Einstellungen konnten nicht geladen werden'),
      isAdmin.value ? getJson<{ settings: Setting[] }>('/api/settings', 'Einstellungen konnten nicht geladen werden') : Promise.resolve({ settings: [] })
    ]);
    metadata.value = meta.settings;
    settings.value = current.settings;
    drafts.value = Object.fromEntries(uniqueKeys.value.map((key) => {
      const setting = current.settings.find((entry) => entry.key === key);
      // Placeholders such as "not-configured" are no value worth editing.
      const value = setting && !setting.isSensitive && setting.value && setting.value !== 'not-configured' ? setting.value : '';
      return [key, value];
    }));
  } catch (reason) {
    message.value = { type: 'error', text: errorText(reason, 'Einstellungen konnten nicht geladen werden.') };
  }
}
onMounted(() => void load());
watch(uniqueKeys, () => void load());

async function save(): Promise<void> {
  saving.value = true;
  message.value = null;
  try {
    for (const field of fields.value) {
      const value = (drafts.value[field.key] ?? '').trim();
      // An empty sensitive field keeps its stored secret; other empty fields are not saved either.
      if (value === '') continue;
      await sendJson('PUT', `/api/settings/${encodeURIComponent(field.key)}`, { value: field.kind === 'number' ? value.replace(',', '.') : value },
        `${field.label} konnte nicht gespeichert werden`);
    }
    message.value = { type: 'ok', text: 'Gespeichert.' };
    emit('saved');
  } catch (reason) {
    message.value = { type: 'error', text: errorText(reason, 'Speichern fehlgeschlagen.') };
  } finally {
    saving.value = false;
  }
}

const canSave = computed(() => fields.value.some((field) => (drafts.value[field.key] ?? '').trim() !== ''));
function openSettings(): void {
  void router.push({ name: 'settings' });
}
</script>

<template>
  <div v-if="uniqueKeys.length" class="quickfix" role="group" aria-label="Fehlende Einstellungen">
    <template v-if="isAdmin">
      <div v-for="field in fields" :key="field.key" class="quickfix__field">
        <v-switch v-if="field.kind === 'boolean'" v-model="drafts[field.key]" :label="field.label" true-value="true" false-value="false"
          density="compact" hide-details color="primary" />
        <v-text-field v-else v-model="drafts[field.key]" :label="field.label" :suffix="field.unit ?? undefined"
          :type="field.kind === 'password' || field.sensitive ? 'password' : 'text'" :inputmode="field.kind === 'number' ? 'decimal' : undefined"
          :placeholder="field.sensitive && field.configured ? 'gespeichert – neu eintragen zum Ersetzen' : undefined"
          density="compact" hide-details autocomplete="off" />
        <small v-if="field.description" class="quickfix__hint">{{ field.description }}</small>
      </div>
      <div class="quickfix__actions">
        <v-btn size="small" color="primary" variant="tonal" :loading="saving" :disabled="!canSave" @click="save">Speichern</v-btn>
        <v-btn size="small" variant="text" @click="openSettings">Alle Einstellungen</v-btn>
      </div>
    </template>
    <p v-else class="z-note">Fehlt: {{ fields.map((field) => field.label).join(', ') }}. Eintragen kann ein Admin in den Einstellungen.</p>
    <p v-if="message" class="z-note" :class="message.type === 'error' ? 'z-note--error' : 'z-note--ok'" role="status">{{ message.text }}</p>
  </div>
</template>

<style scoped>
.quickfix { display: grid; gap: 8px; margin-top: 8px; padding: 10px 12px; border: 1px dashed var(--efp-border); border-radius: 10px; }
.quickfix__field { display: grid; gap: 2px; }
.quickfix__hint { font-size: .74rem; color: var(--efp-muted); }
.quickfix__actions { display: flex; flex-wrap: wrap; gap: 8px; }
</style>
