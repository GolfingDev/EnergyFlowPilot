# Forecast-Chart Änderungen

Diese Änderungen ersetzen für den Zukunfts-Chart die historischen Entscheidungen durch
eine neue Simulation mit SoC-Fortschreibung.

## Enthalten
- `Models/DomainModels.cs`
  - `TibberChartPoint` um `ForecastSocPercent` erweitert
- `Services/DecisionEngine.cs`
  - neue Methode `BuildDecisionForPointAsync(...)`
- `Services/DashboardServices.cs`
  - Zukunfts-Chart wird direkt durch die Engine berechnet
  - SoC wird je Stunde grob fortgeschrieben
- `wwwroot/index.html`
  - Preis-Chart zeigt zusätzlich eine Forecast-SoC-Linie
