export interface TrendSeries {
  label: string;
  color: string;
  points: Array<{ x: number; y: number | null }>;
  fill?: boolean;
  dashed?: boolean;
}
