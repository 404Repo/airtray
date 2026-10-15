# AirTray v1.0.0

Erste öffentliche Windows-Version von **AirTray**, einer schlanken Akkuanzeige für AirPods im Windows-System-Tray.

## Funktionen
- Akkustand direkt im System-Tray
- Kompaktes Akku-Popup per Linksklick
- Rechtsklick-Menü mit Autostart, Bluetooth-Einstellungen, Scan und Diagnose
- BLE-Erkennung ohne überflüssige manuelle Geräteauswahl
- Separate Anzeige für links, rechts und Case, sofern verwertbare Werte empfangen werden
- Optionale, zeitlich begrenzte Bluetooth-Diagnose

## Installation
1. `AirTray-Setup-1.0.0-win-x64.exe` herunterladen und starten.
2. Installation abschließen. Die benötigte .NET 8 Desktop Runtime ist im Setup enthalten.
3. AirPods unter Windows per Bluetooth verbinden.

**Voraussetzungen:** Windows 10/11 x64 und Bluetooth-Unterstützung.

## Hinweise
- AirPods-Akkustände werden aus Bluetooth-Anzeigen näherungsweise ermittelt und können in 10-%-Schritten vorliegen.
- Nicht verfügbare Daten (z. B. Case-Akku oder Seriennummer) werden nicht erfunden.
- Diagnoseprotokolle können gerätespezifische Informationen enthalten. Vor öffentlichem Teilen prüfen.
- Dieses Projekt ist unabhängig von Apple und nicht offiziell unterstützt.
