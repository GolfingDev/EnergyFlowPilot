<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';

interface ControllerStatusResponseDto {
  status: string;
  knownSettingsCount: number;
  persistedSettingsCount: number;
  configuredSensitiveSettingsCount: number;
  generatedAtUtc: string;
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

interface SettingsErrorDto {
  message: string;
}

const status = ref<ControllerStatusResponseDto | null>(null);
const settings = ref<ControllerSettingResponseDto[]>([]);
const isLoading = ref(false);
const errorMessage = ref<string | null>(null);
const editDialogOpen = ref(false);
const settingBeingEdited = ref<ControllerSettingResponseDto | null>(null);
const editedValue = ref('');

const configuredSettingCount = computed(() => settings.value.filter((setting) => setting.isConfigured).length);

// Loads status and settings together so the dashboard always shows one consistent refresh state.
async function refreshDashboard(): Promise<void> {
  isLoading.value = true;
  errorMessage.value = null;

  try {
    const [statusResponse, settingsResponse] = await Promise.all([
      fetchJson<ControllerStatusResponseDto>('/api/status'),
      fetchJson<ControllerSettingsResponseDto>('/api/settings')
    ]);

    status.value = statusResponse;
    settings.value = settingsResponse.settings;
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : 'Die Controller-Daten konnten nicht geladen werden.';
  } finally {
    isLoading.value = false;
  }
}

// Reads one JSON response and turns API error DTOs into understandable German messages.
async function fetchJson<TResponse>(url: string, requestInit?: RequestInit): Promise<TResponse> {
  const response = await fetch(url, requestInit);

  if (!response.ok) {
    const error = (await response.json().catch(() => null)) as SettingsErrorDto | null;
    throw new Error(error?.message ?? `Der API-Request ist mit HTTP ${response.status} fehlgeschlagen.`);
  }

  return (await response.json()) as TResponse;
}

// Opens the editor with an empty value for sensitive settings so secrets are never shown again.
function openEditDialog(setting: ControllerSettingResponseDto): void {
  settingBeingEdited.value = setting;
  editedValue.value = setting.isSensitive ? '' : setting.value ?? '';
  editDialogOpen.value = true;
}

// Persists the edited setting through the DTO API and updates the local table without exposing secrets.
async function saveEditedSetting(): Promise<void> {
  if (settingBeingEdited.value === null) {
    return;
  }

  isLoading.value = true;
  errorMessage.value = null;

  try {
    const updatedSetting = await fetchJson<ControllerSettingResponseDto>(
      `/api/settings/${encodeURIComponent(settingBeingEdited.value.key)}`,
      {
        method: 'PUT',
        headers: {
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ value: editedValue.value })
      });

    settings.value = settings.value.map((setting) =>
      setting.key === updatedSetting.key ? updatedSetting : setting);
    editDialogOpen.value = false;
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : 'Die Einstellung konnte nicht gespeichert werden.';
  } finally {
    isLoading.value = false;
  }
}

function formatUtc(timestamp: string): string {
  return new Intl.DateTimeFormat('de-DE', {
    dateStyle: 'short',
    timeStyle: 'medium',
    timeZone: 'Europe/Berlin'
  }).format(new Date(timestamp));
}

function getDisplayValue(setting: ControllerSettingResponseDto): string {
  if (setting.isSensitive) {
    return setting.isConfigured ? 'Konfiguriert' : 'Nicht gesetzt';
  }

  return setting.value ?? 'Nicht gesetzt';
}

onMounted(() => {
  void refreshDashboard();
});
</script>

<template>
  <v-app>
    <v-app-bar color="surface" elevation="1">
      <v-app-bar-title>Tibber Victron Controller</v-app-bar-title>
      <v-btn
        :loading="isLoading"
        icon="mdi-refresh"
        variant="text"
        aria-label="Aktualisieren"
        @click="refreshDashboard"
      />
    </v-app-bar>

    <v-main>
      <v-container class="dashboard" fluid>
        <v-alert
          v-if="errorMessage"
          class="mb-4"
          type="error"
          variant="tonal"
        >
          {{ errorMessage }}
        </v-alert>

        <section class="status-grid">
          <v-sheet class="metric-panel" border rounded="lg">
            <span class="metric-label">Status</span>
            <strong class="metric-value">{{ status?.status ?? 'Unbekannt' }}</strong>
          </v-sheet>
          <v-sheet class="metric-panel" border rounded="lg">
            <span class="metric-label">Settings</span>
            <strong class="metric-value">{{ status?.persistedSettingsCount ?? settings.length }} / {{ status?.knownSettingsCount ?? '-' }}</strong>
          </v-sheet>
          <v-sheet class="metric-panel" border rounded="lg">
            <span class="metric-label">Konfiguriert</span>
            <strong class="metric-value">{{ configuredSettingCount }}</strong>
          </v-sheet>
          <v-sheet class="metric-panel" border rounded="lg">
            <span class="metric-label">Secrets</span>
            <strong class="metric-value">{{ status?.configuredSensitiveSettingsCount ?? 0 }}</strong>
          </v-sheet>
        </section>

        <v-sheet class="settings-panel" border rounded="lg">
          <div class="panel-header">
            <div>
              <h1>Controller-Einstellungen</h1>
              <p v-if="status">Stand {{ formatUtc(status.generatedAtUtc) }}</p>
            </div>
          </div>

          <v-table fixed-header>
            <thead>
              <tr>
                <th>Key</th>
                <th>Wert</th>
                <th>Typ</th>
                <th>Geaendert</th>
                <th class="actions-column">Aktion</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="setting in settings" :key="setting.key">
                <td class="setting-key">{{ setting.key }}</td>
                <td>{{ getDisplayValue(setting) }}</td>
                <td>
                  <v-chip
                    :color="setting.isSensitive ? 'warning' : 'secondary'"
                    size="small"
                    variant="tonal"
                  >
                    {{ setting.isSensitive ? 'Sensitiv' : 'Normal' }}
                  </v-chip>
                </td>
                <td>{{ formatUtc(setting.updatedAtUtc) }}</td>
                <td class="actions-column">
                  <v-btn
                    icon="mdi-pencil"
                    size="small"
                    variant="text"
                    aria-label="Einstellung bearbeiten"
                    @click="openEditDialog(setting)"
                  />
                </td>
              </tr>
            </tbody>
          </v-table>
        </v-sheet>
      </v-container>
    </v-main>

    <v-dialog v-model="editDialogOpen" max-width="560">
      <v-sheet class="edit-dialog" rounded="lg">
        <h2>{{ settingBeingEdited?.key }}</h2>
        <v-text-field
          v-model="editedValue"
          :label="settingBeingEdited?.isSensitive ? 'Neuen geheimen Wert setzen' : 'Wert'"
          :type="settingBeingEdited?.isSensitive ? 'password' : 'text'"
          autocomplete="off"
          hide-details="auto"
          variant="outlined"
        />
        <div class="dialog-actions">
          <v-btn variant="text" @click="editDialogOpen = false">Abbrechen</v-btn>
          <v-btn color="primary" :loading="isLoading" @click="saveEditedSetting">Speichern</v-btn>
        </div>
      </v-sheet>
    </v-dialog>
  </v-app>
</template>
