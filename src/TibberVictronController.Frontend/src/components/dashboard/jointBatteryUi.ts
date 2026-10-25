import type { ComputedRef, InjectionKey, Ref } from 'vue';
import type { JointBatteryShadowDto } from './dashboardTypes';

// Shared presentation context: the dashboard retains ownership of requests and access checks.
export interface JointBatteryUi {
  state: Ref<JointBatteryShadowDto | null>;
  recoveryBusy: Ref<boolean>;
  manualBusy: Ref<boolean>;
  canControl: ComputedRef<boolean>;
  confirmRecovery: () => Promise<void>;
  startManualCharge: (targetKwh: number, maximumPowerKw: number | null) => Promise<void>;
  stopManualCharge: () => Promise<void>;
}
export const jointBatteryUiKey: InjectionKey<JointBatteryUi> = Symbol('joint-battery-ui');
