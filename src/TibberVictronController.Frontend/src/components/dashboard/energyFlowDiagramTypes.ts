export type EnergyFlowNodeId = 'pv' | 'grid' | 'battery' | 'hub' | 'house';
export type EnergyFlowTone = 'pv' | 'grid' | 'batteryCharge' | 'batteryDischarge' | 'load';

export interface EnergyFlowNode {
  id: EnergyFlowNodeId;
  label: string;
  value: string;
  subtitle: string;
  icon: string;
  tone: EnergyFlowTone;
}

export interface EnergyFlow {
  id: string;
  from: EnergyFlowNodeId;
  to: EnergyFlowNodeId;
  label: string;
  watts: number;
  tone: EnergyFlowTone;
}
